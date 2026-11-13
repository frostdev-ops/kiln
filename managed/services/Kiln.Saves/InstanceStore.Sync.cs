// SPDX-License-Identifier: MIT
// What the sync of instance data (InstanceSync.cs) reads from the store: which declared keys travel, and what an item's
// values of those keys are as JSON now; and the door through which the client side's events enter the store's event queue.
// Nothing here changes a record or a place.
namespace Kiln;

internal sealed partial class InstanceStore
{
    /// <summary>One value that may travel: the item, whose key it is, and its JSON now.</summary>
    internal readonly record struct SyncedValue(LiveItem Item, InstanceKeyInfo Key, string Json);

    /// <summary>True when a loaded mod has declared a key that travels (the sync has nothing to do otherwise).</summary>
    internal bool HasSyncedKeys => _keys.Values.Any(map => map.Values.Any(k => k.Sync != InstanceSync.None && k.Mod.Alive));

    /// <summary>The declared key <paramref name="name"/> of the loaded mod <paramref name="mod"/>; null when that mod is
    /// not loaded or has not declared it (its values then travel to nobody).</summary>
    internal InstanceKeyInfo? KeyOf(string mod, string name)
    {
        foreach (var (context, map) in _keys)
            if (context.Alive && context.Info.Id == mod && map.TryGetValue(name, out var info)) return info;
        return null;
    }

    /// <summary>A copy in this game changed (the client side of the sync): its event goes out through the store's own queue,
    /// as the store's events do. While a pass or a delivery runs it waits behind the events already waiting; otherwise it
    /// is delivered now. Tick thread.</summary>
    internal void PostSynced(InstanceDataSyncedEvent ev) => Post(ev);

    /// <summary>Every value of a key that travels, on the items of this session, with its JSON as it is now: a live object
    /// is written out here, so an edit in place is seen (its last form that fit, when it grew over the cap). Tick thread.</summary>
    internal List<SyncedValue> SyncedValues()
    {
        var list = new List<SyncedValue>();
        var keys = new Dictionary<(string, string), InstanceKeyInfo?>();
        foreach (var live in _live.Values)
            foreach (var (mod, slots) in live.Values.ByMod)
                foreach (var (name, slot) in slots)
                {
                    if (!keys.TryGetValue((mod, name), out var info)) keys[(mod, name)] = info = KeyOf(mod, name);
                    if (info is null || info.Sync == InstanceSync.None) continue;
                    var json = Serialize(slot, _owner.JsonFor(info.Mod), mod, name, live.RecordId);
                    if (json is null or "null") continue;
                    list.Add(new SyncedValue(live, info, json));
                }
        return list;
    }
}
