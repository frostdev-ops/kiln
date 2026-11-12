// SPDX-License-Identifier: MIT
// The mod's face of instance data (PLAN D4, Kiln.Saves part). The store is InstanceStore.cs, the matching InstanceMatcher.cs.
using System.Diagnostics.CodeAnalysis;

namespace Kiln;

/// <summary>Where the values of an <see cref="InstanceKey{T}"/> are kept.</summary>
public enum Persist
{
    /// <summary>With this world: an item in a chest, a tombstone, a station or an NPC in the world's save generation; an
    /// item in a player's inventory in this world's record of the character.</summary>
    World = 0,

    /// <summary>With the character, to other worlds (<see cref="CharacterData.Carried"/>). Not available in this release:
    /// declaring such a key throws <see cref="KilnUnavailableException"/> (<c>saves.instance_data.carried</c>). Use
    /// <see cref="World"/>: it keeps the value on this world's save, the item in a player's inventory included; data that
    /// travels with a character between worlds is not available yet.</summary>
    Carried = 1,

    /// <summary>For this server session only: never written.</summary>
    Session = 2,
}

/// <summary>Options of an <see cref="InstanceKey{T}"/>.</summary>
public sealed record InstanceKeyOptions
{
    /// <summary>Where the values are kept (default <see cref="Kiln.Persist.World"/>).</summary>
    public Persist Persist { get; init; } = Persist.World;

    /// <summary>The item types that may carry this key; null: any item with an item entity (gear, tools, cans). An item
    /// of another type throws <see cref="ArgumentException"/>.</summary>
    public IReadOnlyList<ItemId>? Items { get; init; }
}

/// <summary>A typed key of instance data: one kind of value a mod keeps on single items (this sword's kill count, this
/// ring's rolled bonus). Declared once with <see cref="InstanceData.Key{T}"/>; it belongs to the mod that declared it.</summary>
/// <typeparam name="T">The value: a class with a parameterless constructor, stored as JSON (System.Text.Json, public
/// properties and fields) like <see cref="WorldData"/>'s.</typeparam>
public sealed class InstanceKey<T> where T : class, new()
{
    internal InstanceKey(InstanceKeyInfo info) => Info = info;

    internal readonly InstanceKeyInfo Info;

    /// <summary>The key's name, unique within its mod.</summary>
    public string Name => Info.Name;

    /// <summary>The id of the mod that declared it (its <c>kiln.mod.json</c> id).</summary>
    public string Mod => Info.ModId;

    /// <summary>Where its values are kept.</summary>
    public Persist Persist => Info.Persist;

    /// <summary><c>"mod:name"</c>.</summary>
    public override string ToString() => $"{Mod}:{Name}";
}

/// <summary>
/// Values that stay with one item: <c>sword.Get(Kills).Count++</c>, and the count follows that sword through moves
/// between chests and players, saves, restarts, <c>kill -9</c> and a restored backup. Declare a key once, then use the
/// calls on <see cref="ItemInstanceRef"/> (<see cref="ItemInstanceDataExtensions"/>).
/// </summary>
/// <remarks>
/// <para>How an item is found again. In a session an item is its item entity plus the stamp kiln.items gives it (a
/// <see cref="ItemInstanceRef"/> from an inventory read, a give, take or move result, <c>Items.Held</c> or
/// <c>Items.Equipped</c>). The game keeps no id of its own for an item, so at a save Kiln writes each item's holder, flat
/// slot, item id and the fingerprint of its save record (level, perks, gem, dye; durability and fuel as ratios), with the
/// holder's whole list of items, and after a load it gives a record back only to the one item that certainly is it. When
/// that is not certain (two identical swords in a holder that changed while Kiln was not watching) the record is set
/// aside and reported (<see cref="InstanceDataLostEvent"/>, <see cref="InstanceLossReason.Ambiguous"/>, <see cref="Parked{T}"/>):
/// an item never gets another item's data, except in the one case nobody can detect: two identical items (equal item id,
/// level, perks, gem and dye, and the same durability and fuel) whose slots were swapped in another world, or anywhere Kiln
/// did not see, come back by slot on the newest exact load and trade their data. Nothing reports it.</para>
/// <para>What is proven where. Items in chests and other world holders (tombstones, stations, NPCs) are matched by the
/// world's own generation and are proven on a test server (the lab results are in docs/kiln/services/saves.md). Items in
/// a player's inventory go with this world's record of the character and are matched when the player joins; that half
/// waits for the guided client pass, because only a real client brings a character's inventory back.</para>
/// <para>Tick thread, server side, from mod code (handlers, <c>OnLoad</c>, tasks). Keys belong to the mod that declared
/// them. A mod reload keeps the data: live objects are written out as JSON when the mod unloads and read into the new
/// instance's types. Values of a mod that is not loaded are kept unread and move with their item.</para>
/// <para>Capability <c>saves.instance_data</c>: kiln.saves, kiln.items' item instances (<c>items.instances</c>) and the
/// kiln.spawn's holder inspection/search/metadata, and the world's data open. When it is off the calls throw <see cref="KilnUnavailableException"/>; declaring a key needs
/// nothing.</para>
/// </remarks>
public static class InstanceData
{
    internal const string Capability = InstanceStore.Capability;
    internal const string CarriedCapability = "saves.instance_data.carried";

    internal const string CarriedReason = "Persist.World keeps the value on this world's save, the item in a player's inventory included; " +
                                          "data that travels with a character between worlds is not available yet";

    /// <summary>Declares a key of the calling mod. Call it once, from a static field of your mod's class or from
    /// <c>OnLoad</c>; the same name again returns the same key.</summary>
    /// <param name="name">Unique within the mod, at most 200 characters.</param>
    /// <param name="options">Where values are kept, and which item types may carry them.</param>
    /// <exception cref="ArgumentException">An empty or too long name, an unknown <see cref="Kiln.Persist"/>, an empty
    /// <see cref="InstanceKeyOptions.Items"/>, or a name the mod declared before with another type or other options.</exception>
    /// <exception cref="KilnUnavailableException"><see cref="Kiln.Persist.Carried"/>: not available in this release
    /// (<c>saves.instance_data.carried</c>).</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or not from a mod's code.</exception>
    [ServerOnly]
    [TickThreadOnly]
    public static InstanceKey<T> Key<T>(string name, InstanceKeyOptions? options = null) where T : class, new()
    {
        Services.RequireTickThread("InstanceData.Key");
        WorldStore.CheckKey(name);
        options ??= new InstanceKeyOptions();
        if (!Enum.IsDefined(options.Persist)) throw new ArgumentException($"unknown Persist {(int)options.Persist}", nameof(options));
        if (options.Items is { Count: 0 }) throw new ArgumentException("Items lists no item type; give at least one, or null for any item", nameof(options));
        if (options.Persist == Persist.Carried)
            throw new KilnUnavailableException(CarriedCapability, CarriedReason);
        var mod = ModContext.CurrentMod ?? throw new InvalidOperationException(
            "InstanceData.Key must be called from a mod's code (OnLoad, its handlers, or a static field of the mod's class read there)");
        var key = SavesRuntime.Store.Instances.Declare<T>(mod, name, options);
        InstanceCommand.Ensure();
        return key;
    }

    /// <summary>Every item in this session that carries a value of <paramref name="key"/>, with the value (the live object).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="InvalidDataException">A stored value does not read as <typeparamref name="T"/> (it is kept).</exception>
    /// <exception cref="KilnUnavailableException"><c>saves.instance_data</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the key's mod has unloaded.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(InstanceStore.Capability)]
    public static IReadOnlyList<(ItemInstanceRef Item, T Value)> All<T>(InstanceKey<T> key) where T : class, new()
    {
        var store = Use("InstanceData.All", key);
        return store.AllOf(key.Info).Select(x => (x.Item.Ref, store.Materialize<T>(x.Slot, key.Info, x.Item.RecordId, create: true))).ToList();
    }

    /// <summary>The saved values of <paramref name="key"/> that are not on an item now: records waiting for their holder or
    /// their item (<see cref="ParkedState.Waiting"/>), and records set aside because it was not certain which item they
    /// belong to (<see cref="ParkedState.SetAside"/>), each with the reason. The values are copies: changing them changes
    /// nothing.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="KilnUnavailableException"><c>saves.instance_data</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the key's mod has unloaded.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(InstanceStore.Capability)]
    public static IReadOnlyList<ParkedInstance<T>> Parked<T>(InstanceKey<T> key) where T : class, new()
    {
        var store = Use("InstanceData.Parked", key);
        var list = new List<ParkedInstance<T>>();
        foreach (var (h, r, pending, slot) in store.ParkedOf(key.Info))
        {
            var value = store.Copy<T>(slot, key.Info);
            if (h is not null && r is not null)
                list.Add(new ParkedInstance<T>(h.Describe, h.Key.Kind == HolderKind.Player ? h.Key.Character : default, r.Slot, r.Fields.Item,
                    r.Aside ? ParkedState.SetAside : ParkedState.Waiting, r.Reason.Length > 0 ? r.Reason : "not matched yet",
                    Math.Max(r.Missed, h.Missed), value));
            else if (pending is not null)
                list.Add(new ParkedInstance<T>($"character {pending.Holder.Character}", pending.Holder.Character, pending.Place.FlatSlot, pending.Fields.Item,
                    ParkedState.Waiting, "gone from the player's inventory: destroyed, or the player is leaving (decided within a few seconds)", 0, value));
        }
        return list;
    }

    internal static InstanceStore Use<T>(string api, InstanceKey<T> key) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(key);
        Services.RequireTickThread(api);
        if (!SavesRuntime.Ensure()) throw new KilnUnavailableException(Capability, "kiln.saves is not loaded");
        var store = SavesRuntime.Store.Instances;
        store.RequireWorld();
        if (!SavesRuntime.WorldDataReady)
            throw new KilnUnavailableException(Capability, InstanceCapabilities.WaitingText);
        if (!store.IsDeclared(key.Info))
            throw new InvalidOperationException($"instance key {key} belongs to a mod instance that has unloaded; declare it again");
        SavesRuntime.Store.EnsureOpen();
        return store;
    }
}

/// <summary>Whether a <see cref="ParkedInstance{T}"/> still waits for its item or is set aside.</summary>
public enum ParkedState
{
    /// <summary>Its holder was not found yet, or no item there fits it (it may come back); kept for 3 sessions of that.</summary>
    Waiting = 0,

    /// <summary>It was not certain which item it belongs to: it is never attached automatically, also not later.</summary>
    SetAside = 1,
}

/// <summary>A saved value that is not on an item now (<see cref="InstanceData.Parked{T}"/>).</summary>
/// <typeparam name="T">The key's type.</typeparam>
public sealed class ParkedInstance<T> where T : class
{
    internal ParkedInstance(string holder, CharacterKey character, int flatSlot, ItemId item, ParkedState state, string reason, int sessionsMissed, T? value)
    {
        Holder = holder;
        Character = character;
        FlatSlot = flatSlot;
        Item = item;
        State = state;
        Reason = reason;
        SessionsMissed = sessionsMissed;
        Value = value;
    }

    /// <summary>The holder it was saved in, in words (a template and position, or a character).</summary>
    public string Holder { get; }

    /// <summary>The character, for a record of a player's inventory; <see cref="CharacterKey.None"/> otherwise.</summary>
    public CharacterKey Character { get; }

    /// <summary>The flat slot it was saved at (link x 8 + index).</summary>
    public int FlatSlot { get; }

    /// <summary>The item type it was saved on.</summary>
    public ItemId Item { get; }

    /// <summary>Whether it waits for its item or was set aside (<see cref="ParkedState"/>).</summary>
    public ParkedState State { get; }

    /// <summary>Why it is not on an item, in words.</summary>
    public string Reason { get; }

    /// <summary>Sessions in which its holder or its item was not found.</summary>
    public int SessionsMissed { get; }

    /// <summary>A copy of the value; null when it does not read as <typeparamref name="T"/>.</summary>
    public T? Value { get; }

    /// <summary>The record in one line, for a log: state, item type, holder, slot and reason.</summary>
    public override string ToString() => $"{State} {Item} in {Holder} slot {FlatSlot}: {Reason}";
}

/// <summary>The calls on one item (<see cref="InstanceData"/>). Tick thread, server side.</summary>
/// <remarks>Errors follow <c>docs/kiln/modding/contract.md</c>: a none ref, a ref without a stamp, a stale ref (the item is
/// gone, or its id belongs to another item now), an item type the key does not allow and a value over 64 KB as JSON throw
/// <see cref="ArgumentException"/>, except that <see cref="TryGet{T}"/>, <see cref="Has{T}"/> and <see cref="Remove{T}"/>
/// answer false for a stale ref. Capability <c>saves.instance_data</c> off: <see cref="KilnUnavailableException"/>.</remarks>
public static class ItemInstanceDataExtensions
{
    /// <summary>The item's value of <paramref name="key"/>, created with <c>new T()</c> if it has none: a live object,
    /// change it in place and Kiln saves it with the world.</summary>
    /// <exception cref="ArgumentException">A none, stamp-less or stale ref, or an item type the key does not allow.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="InvalidDataException">The stored value does not read as <typeparamref name="T"/> (it is kept).</exception>
    /// <exception cref="KilnUnavailableException"><c>saves.instance_data</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the key's mod has unloaded.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(InstanceStore.Capability)]
    public static T Get<T>(this ItemInstanceRef item, InstanceKey<T> key) where T : class, new()
    {
        var store = InstanceData.Use("ItemInstanceRef.Get", key);
        var live = store.Lookup(item, key.Info, create: true) ?? throw Stale(item);
        var slot = InstanceStore.SlotOf(live, key.Info, create: true)!;
        return store.Materialize<T>(slot, key.Info, live.RecordId, create: true);
    }

    /// <summary>The item's value of <paramref name="key"/> if it has one. False for a stale ref.</summary>
    /// <exception cref="ArgumentException">A none or stamp-less ref, or an item type the key does not allow.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="InvalidDataException">The stored value does not read as <typeparamref name="T"/> (it is kept).</exception>
    /// <exception cref="KilnUnavailableException"><c>saves.instance_data</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the key's mod has unloaded.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(InstanceStore.Capability)]
    public static bool TryGet<T>(this ItemInstanceRef item, InstanceKey<T> key, [NotNullWhen(true)] out T? value) where T : class, new()
    {
        value = null;
        var store = InstanceData.Use("ItemInstanceRef.TryGet", key);
        if (store.Lookup(item, key.Info, create: false) is not { } live || InstanceStore.SlotOf(live, key.Info, create: false) is not { } slot) return false;
        if (slot.Value is null && slot.Json is null or "null") return false;
        value = store.Materialize<T>(slot, key.Info, live.RecordId, create: false);
        return true;
    }

    /// <summary>Whether the item has a value of <paramref name="key"/>. False for a stale ref.</summary>
    /// <exception cref="ArgumentException">A none or stamp-less ref, or an item type the key does not allow.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="KilnUnavailableException"><c>saves.instance_data</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the key's mod has unloaded.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(InstanceStore.Capability)]
    public static bool Has<T>(this ItemInstanceRef item, InstanceKey<T> key) where T : class, new()
    {
        var store = InstanceData.Use("ItemInstanceRef.Has", key);
        return store.Lookup(item, key.Info, create: false) is { } live && InstanceStore.SlotOf(live, key.Info, create: false) is not null;
    }

    /// <summary>Gives the item <paramref name="value"/> for <paramref name="key"/> (replacing what it had).</summary>
    /// <exception cref="ArgumentException">A none, stamp-less or stale ref, an item type the key does not allow, or a value
    /// over 64 KB as JSON.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="value"/> is null.</exception>
    /// <exception cref="KilnUnavailableException"><c>saves.instance_data</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the key's mod has unloaded.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(InstanceStore.Capability)]
    public static void Set<T>(this ItemInstanceRef item, InstanceKey<T> key, T value) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(value);
        var store = InstanceData.Use("ItemInstanceRef.Set", key);
        var json = store.JsonForSet(key.Info, value);
        var live = store.Lookup(item, key.Info, create: true) ?? throw Stale(item);
        var slot = InstanceStore.SlotOf(live, key.Info, create: true)!;
        slot.Value = value;
        slot.Type = typeof(T);
        slot.Json = json;
        slot.TooBig = 0;
        if (Events.HasSubscribers<InstanceDataChangedEvent>())
            Events.Post(new InstanceDataChangedEvent(live.Ref, key.Mod, key.Name, removed: false));
    }

    /// <summary>Removes the item's value of <paramref name="key"/> (from the next save on). False when it had none, or for a
    /// stale ref.</summary>
    /// <exception cref="ArgumentException">A none or stamp-less ref, or an item type the key does not allow.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="KilnUnavailableException"><c>saves.instance_data</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the key's mod has unloaded.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(InstanceStore.Capability)]
    public static bool Remove<T>(this ItemInstanceRef item, InstanceKey<T> key) where T : class, new()
    {
        var store = InstanceData.Use("ItemInstanceRef.Remove", key);
        if (store.Lookup(item, key.Info, create: false) is not { } live || !store.Remove(live, key.Info)) return false;
        if (Events.HasSubscribers<InstanceDataChangedEvent>())
            Events.Post(new InstanceDataChangedEvent(live.Ref, key.Mod, key.Name, removed: true));
        return true;
    }

    private static ArgumentException Stale(ItemInstanceRef item) =>
        new($"{item} is stale: the item is gone, or its id belongs to another item now", nameof(item));
}

/// <summary>A value was set or removed through the instance-data API. Tick thread, one event per successful call.
/// In-place edits to the live object returned by Get are not intercepted; call Set to announce an edit.</summary>
[ServerOnly]
public sealed class InstanceDataChangedEvent : KilnEvent
{
    internal InstanceDataChangedEvent(ItemInstanceRef item, string mod, string key, bool removed)
    {
        Item = item;
        Mod = mod;
        Key = key;
        Removed = removed;
    }

    /// <summary>The item whose value changed.</summary>
    public ItemInstanceRef Item { get; }

    /// <summary>The mod id the value belongs to.</summary>
    public string Mod { get; }

    /// <summary>The key's name.</summary>
    public string Key { get; }

    /// <summary>True for Remove; false for Set.</summary>
    public bool Removed { get; }
}

/// <summary>How a saved record found its item again.</summary>
public enum InstanceMatch
{
    /// <summary>In its own slot.</summary>
    Exact = 0,

    /// <summary>In another slot of the same holder: the one item that fits.</summary>
    Moved = 1,
}

/// <summary>Why a record waits.</summary>
public enum InstanceParkReason
{
    /// <summary>Its holder (a chest, an NPC) was not found in this session; it is looked for again in the next ones.</summary>
    HolderAway = 0,

    /// <summary>The player left with the item: the record waits with the character, at the item's last slot.</summary>
    PlayerLeft = 1,

    /// <summary>The holder was found, but no item there fits the record (it may come back with its character).</summary>
    NoCandidate = 2,
}

/// <summary>Why a record no longer goes with any item.</summary>
public enum InstanceLossReason
{
    /// <summary>The item was destroyed (trash, crafting, taken away): its data is dropped.</summary>
    Destroyed = 0,

    /// <summary>Its holder or its item was not found in 3 sessions: dropped.</summary>
    Unmatched = 1,

    /// <summary>It was not certain which item the record belongs to: set aside, kept, never attached automatically
    /// (<see cref="InstanceData.Parked{T}"/>).</summary>
    Ambiguous = 2,

    /// <summary>The holder had more than 256 records of the mod: the oldest waiting or set-aside ones were dropped.</summary>
    Capped = 3,
}

/// <summary>A saved record found its item again after a load or a join: the value is on the item now. One event per key.
/// Tick thread.</summary>
[ServerOnly]
public sealed class InstanceDataRestoredEvent : KilnEvent
{
    internal InstanceDataRestoredEvent(ItemInstanceRef item, string mod, string key, InstanceMatch match, string detail)
    {
        Item = item;
        Mod = mod;
        Key = key;
        Match = match;
        Detail = detail;
    }

    /// <summary>The item the value is on now.</summary>
    public ItemInstanceRef Item { get; }

    /// <summary>The mod id the value belongs to.</summary>
    public string Mod { get; }

    /// <summary>The key's name.</summary>
    public string Key { get; }

    /// <summary>How the record was matched to this item.</summary>
    public InstanceMatch Match { get; }

    /// <summary>Why this item, in words.</summary>
    public string Detail { get; }

    /// <summary>The event in one line, for a log: mod, key, item and match.</summary>
    public override string ToString() => $"restored {Mod}:{Key} on {Item} ({Match})";
}

/// <summary>A record waits for its item: its holder was not found, the player left with it, or no item of its holder fits
/// it. One event per mod. Tick thread.</summary>
[ServerOnly]
public sealed class InstanceDataParkedEvent : KilnEvent
{
    internal InstanceDataParkedEvent(string holder, CharacterKey character, int flatSlot, ItemId item, string mod, IReadOnlyList<string> keys,
        InstanceParkReason reason, string detail)
    {
        Holder = holder;
        Character = character;
        FlatSlot = flatSlot;
        Item = item;
        Mod = mod;
        Keys = keys;
        Reason = reason;
        Detail = detail;
    }

    /// <summary>The holder, in words (a template and position, or a character).</summary>
    public string Holder { get; }

    /// <summary>The character for a player's record; <see cref="CharacterKey.None"/> otherwise.</summary>
    public CharacterKey Character { get; }

    /// <summary>The flat slot it was saved at (link x 8 + index).</summary>
    public int FlatSlot { get; }

    /// <summary>The item type.</summary>
    public ItemId Item { get; }

    /// <summary>The mod id the values belong to.</summary>
    public string Mod { get; }

    /// <summary>The names of the mod's keys that wait.</summary>
    public IReadOnlyList<string> Keys { get; }

    /// <summary>Why they wait.</summary>
    public InstanceParkReason Reason { get; }

    /// <summary>Why, in words.</summary>
    public string Detail { get; }

    /// <summary>The event in one line, for a log: mod, keys, item type, holder, slot and reason.</summary>
    public override string ToString() => $"parked {Mod} [{string.Join(", ", Keys)}] of {Item} in {Holder} slot {FlatSlot} ({Reason})";
}

/// <summary>A record no longer goes with an item: the item was destroyed, it was not found in 3 sessions, the holder was
/// over the cap, or it was set aside because it was not certain which item it belongs to (<see cref="InstanceLossReason.Ambiguous"/>:
/// kept and listed, never attached). One event per mod. Tick thread.</summary>
[ServerOnly]
public sealed class InstanceDataLostEvent : KilnEvent
{
    internal InstanceDataLostEvent(ItemId item, ItemInstanceRef lastRef, string mod, IReadOnlyList<string> keys, InstanceLossReason reason, string detail)
    {
        Item = item;
        LastRef = lastRef;
        Mod = mod;
        Keys = keys;
        Reason = reason;
        Detail = detail;
    }

    /// <summary>The item type.</summary>
    public ItemId Item { get; }

    /// <summary>The item as it was in this session (a destroyed item), <see cref="ItemInstanceRef.None"/> for a saved record.</summary>
    public ItemInstanceRef LastRef { get; }

    /// <summary>The mod id the values belonged to.</summary>
    public string Mod { get; }

    /// <summary>The names of the mod's keys that were lost or set aside.</summary>
    public IReadOnlyList<string> Keys { get; }

    /// <summary>Why they no longer go with an item.</summary>
    public InstanceLossReason Reason { get; }

    /// <summary>What happened, in words.</summary>
    public string Detail { get; }

    /// <summary>The event in one line, for a log: mod, keys, item type, reason and detail.</summary>
    public override string ToString() => $"lost {Mod} [{string.Join(", ", Keys)}] of {Item} ({Reason}): {Detail}";
}
