// SPDX-License-Identifier: MIT
// The client's face of instance data (dev.27): the server's copies of the values whose key says who may see them
// (InstanceKeyOptions.Sync), as this game received them over the channel "kiln:instance" (InstanceSync.cs).
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Kiln;

/// <summary>What happened to a value this game holds a copy of (<see cref="InstanceDataSyncedEvent"/>).</summary>
public enum InstanceSyncChange
{
    /// <summary>The server sent the value: new on this item, or changed.</summary>
    Set = 0,

    /// <summary>The value was removed from the item on the server, and the item still has another value this game holds
    /// (the last one's removal arrives as <see cref="Gone"/>).</summary>
    Removed = 1,

    /// <summary>The item is in another holder or slot now; the value is the same.</summary>
    Moved = 2,

    /// <summary>The item left this game's view: destroyed, moved to a holder this player may not see, out of range, its
    /// last value removed, its mod unloaded on the server, or the connection ended. Its copies are gone from
    /// <see cref="InstanceData.Synced()"/>.</summary>
    Gone = 3,
}

/// <summary>One item as this game knows it from the server, with the values the server sent for it: the copies of what
/// the server keeps with <see cref="InstanceData"/>. Never changes: when the server sends news, a new one replaces it
/// (<see cref="InstanceData.Synced(EntityId)"/>). Since 0.2.0-dev.27.</summary>
[ClientOnly]
public sealed class SyncedItem
{
    private readonly IReadOnlyDictionary<(string Mod, string Key), string> _values;

    internal SyncedItem(EntityId entity, ItemId item, EntityId holder, int flatSlot, IReadOnlyDictionary<(string Mod, string Key), string> values)
    {
        Entity = entity;
        Item = item;
        Holder = holder;
        FlatSlot = flatSlot;
        _values = values;
    }

    /// <summary>The item entity's id on the server.</summary>
    public EntityId Entity { get; }

    /// <summary>The item type.</summary>
    public ItemId Item { get; }

    /// <summary>The server's id of the entity whose inventory holds the item: a player, a chest, a station, an NPC.</summary>
    public EntityId Holder { get; }

    /// <summary>The flat slot in the holder, as Kiln.Items numbers it on the server: 0 to 255 are the backpack and action bar
    /// slots the game's save numbers (8 per page), 256 and up the other slots (worn gear, currency, nested).</summary>
    public int FlatSlot { get; }

    /// <summary>True when this game's own player holds the item.</summary>
    public bool Own => Client.Available && Client.HasLocalPlayer && Client.LocalPlayer == Holder;

    /// <summary>The values this game has for the item, as (mod id, key name).</summary>
    public IReadOnlyList<(string Mod, string Key)> Keys => [.. _values.Keys];

    /// <summary>The value of key <paramref name="key"/> of mod <paramref name="mod"/> as the server's JSON; null when this
    /// game has none. For a mod that shows another mod's values.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="mod"/> or <paramref name="key"/> is null.</exception>
    public string? Json(string mod, string key)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(key);
        return _values.GetValueOrDefault((mod, key));
    }

    /// <summary>Whether this game has a value of <paramref name="key"/> for the item.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public bool Has<T>(InstanceKey<T> key) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(key);
        return _values.ContainsKey((key.Mod, key.Name));
    }

    /// <summary>The value of <paramref name="key"/> for the item: a copy read from the server's JSON at each call. Changing
    /// it changes nothing.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="InvalidDataException">The server's value does not read as <typeparamref name="T"/> (another version
    /// of the mod on the server).</exception>
    public bool TryGet<T>(InstanceKey<T> key, [NotNullWhen(true)] out T? value) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(key);
        value = null;
        if (!_values.TryGetValue((key.Mod, key.Name), out var json)) return false;
        try
        {
            value = JsonSerializer.Deserialize<T>(json, key.Info.SyncJson);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"the server's value of {key} does not read as {typeof(T).Name}: {e.Message}", e);
        }
        return value is not null;
    }

    internal IReadOnlyDictionary<(string Mod, string Key), string> Values => _values;

    /// <summary>The item in one line, for a log: entity, item type, holder, slot and keys.</summary>
    public override string ToString() => $"item {Entity} ({Item}) in {Holder} slot {FlatSlot} [{string.Join(", ", _values.Keys.Select(k => $"{k.Mod}:{k.Key}"))}]";
}

/// <summary>Client: a copy this game holds of an item's value changed. The server sent a value, removed one, moved the
/// item, or the item left this game's view. One event per key. On the tick thread (a joined game: the main thread; the
/// host of a world: the server thread, a step after the message). Since 0.2.0-dev.27.</summary>
[ClientOnly]
public sealed class InstanceDataSyncedEvent : KilnEvent
{
    internal InstanceDataSyncedEvent(SyncedItem item, string mod, string key, InstanceSyncChange change)
    {
        Item = item;
        Mod = mod;
        Key = key;
        Change = change;
    }

    /// <summary>The item with its copies as they are after the change (for <see cref="InstanceSyncChange.Gone"/>: as they
    /// were last).</summary>
    public SyncedItem Item { get; }

    /// <summary>The mod id the value belongs to.</summary>
    public string Mod { get; }

    /// <summary>The key's name.</summary>
    public string Key { get; }

    /// <summary>What happened.</summary>
    public InstanceSyncChange Change { get; }

    /// <summary>The event in one line, for a log: change, mod, key and item.</summary>
    public override string ToString() => $"synced {Change} {Mod}:{Key} on {Item.Entity} ({Item.Item}) in {Item.Holder} slot {Item.FlatSlot}";
}

public static partial class InstanceData
{
    /// <summary>The capability of the sync: the channel <c>kiln:instance</c> can open in this process (kiln.net is there).</summary>
    internal const string SyncCapability = "saves.instance_data.sync";

    /// <summary>Client: every item this game has copies for, as the server sent them: the items of this player's own
    /// inventory that carry a value of a key with <see cref="InstanceSync.Holder"/> or <see cref="InstanceSync.Nearby"/>,
    /// and the items of nearby holders with a <see cref="InstanceSync.Nearby"/> value. Empty on a dedicated server, on a
    /// server without Kiln, and until the server's first message (within about a second of joining). The first call
    /// from a client mod opens the channel; a server sends only to games that have it open, so call it (or declare a
    /// synced key) when the mod loads, not when a screen first opens. Since 0.2.0-dev.27.</summary>
    /// <exception cref="KilnUnavailableException"><c>saves.instance_data.sync</c> is off: kiln.net is not available in
    /// this process.</exception>
    [ClientOnly]
    [AnyThread]
    [RequiresCapability(SyncCapability)]
    public static IReadOnlyList<SyncedItem> Synced() => InstanceSyncRuntime.RequireClient().All();

    /// <summary>Client: the item with the server's entity id <paramref name="item"/> as this game knows it; null when the
    /// server sent nothing for it. Since 0.2.0-dev.27.</summary>
    /// <exception cref="KilnUnavailableException"><c>saves.instance_data.sync</c> is off.</exception>
    [ClientOnly]
    [AnyThread]
    [RequiresCapability(SyncCapability)]
    public static SyncedItem? Synced(EntityId item) => InstanceSyncRuntime.RequireClient().Find(item);

    /// <summary>Client: the server's value of <paramref name="key"/> on the item with the server's entity id
    /// <paramref name="item"/>, as a copy; null when the server sent none. Since 0.2.0-dev.27.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="InvalidDataException">The server's value does not read as <typeparamref name="T"/>.</exception>
    /// <exception cref="KilnUnavailableException"><c>saves.instance_data.sync</c> is off.</exception>
    [ClientOnly]
    [AnyThread]
    [RequiresCapability(SyncCapability)]
    public static T? Synced<T>(EntityId item, InstanceKey<T> key) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(key);
        return InstanceSyncRuntime.RequireClient().Find(item) is { } i && i.TryGet(key, out var v) ? v : null;
    }
}
