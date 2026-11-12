// SPDX-License-Identifier: MIT
// What the instance-data store needs from the game, behind one internal interface: the items of the world (kiln.items:
// stamps, locations, save-record fields, following), the holders (kiln.spawn's template and position, kiln.players'
// characters) and the step counter. The store and the matcher never read the game themselves, so they are tested against a
// fake (managed/tests/Kiln.Saves.Tests/FakeInstanceWorld.cs). The game's adapter maps these calls onto Kiln.Items'
// ItemInstances, SpawnLink and Players; until it is there, InstanceStore.World is null and InstanceData is unavailable.
namespace Kiln;

/// <summary>The fields the game writes into an item's save record (design §1.3), read from the item entity now. The
/// same twelve values as Kiln.Items' internal <c>ItemRecord</c>.</summary>
internal readonly record struct InstanceFields(ItemId Item, int Count, int Level, int Durability, int DurabilityMax,
    int Fuel, int FuelMax, ItemId Dye, int PerksAvailable, int PerksUnlocked, ItemId Gem, int GemLevel)
{
    /// <summary>The part of the fingerprint that a load gives back exactly (item id, level, perks, gem, dye).</summary>
    public StableFingerprint Stable => new(Item, Level, Dye, PerksAvailable, PerksUnlocked, Gem, GemLevel);

    public override string ToString() =>
        $"{Item} level {Level}{(Dye.IsNone ? "" : $" dye {Dye}")}{(PerksAvailable + PerksUnlocked > 0 ? $" perks {PerksUnlocked}/{PerksAvailable}" : "")}" +
        $"{(Gem.IsNone ? "" : $" gem {Gem} {GemLevel}")}{(DurabilityMax > 0 ? $" durability {Durability}/{DurabilityMax}" : "")}" +
        $"{(FuelMax > 0 ? $" fuel {Fuel}/{FuelMax}" : "")}";
}

/// <summary>Item id, level, perks available and unlocked, gem item and level, dye: what a record and an item must share
/// to be the same item (PLAN D6). Durability and fuel come back from a load as ratios, so they are not in it.</summary>
internal readonly record struct StableFingerprint(ItemId Item, int Level, ItemId Dye, int PerksAvailable, int PerksUnlocked, ItemId Gem, int GemLevel)
{
    public override string ToString() =>
        $"{Item} level {Level}{(Dye.IsNone ? "" : $" dye {Dye}")}{(PerksAvailable + PerksUnlocked > 0 ? $" perks {PerksUnlocked}/{PerksAvailable}" : "")}" +
        $"{(Gem.IsNone ? "" : $" gem {Gem} {GemLevel}")}";
}

/// <summary>Where an item is: its holder (the chest, the player, the NPC: the entity whose inventory setup links the
/// inventory) and the flat slot (link x 8 + index, the number the game's save record uses).</summary>
internal readonly record struct InstancePlace(EntityId Holder, int FlatSlot)
{
    public override string ToString() => $"{Holder}:{FlatSlot}";
}

/// <summary>One item entity as it is now.</summary>
internal readonly record struct InstanceItem(ItemInstanceRef Ref, InstancePlace Place, InstanceFields Fields);

internal enum HolderKind
{
    /// <summary>No persistent identity: not an entity the game saves with a position, or not a player with a character.
    /// Data on items there lives for the session only.</summary>
    None = 0,
    /// <summary>A chest, tombstone, station, NPC or animal: found again by template and position.</summary>
    World = 1,
    /// <summary>A player: found again by <see cref="CharacterKey"/>.</summary>
    Player = 2,
}

/// <summary>Who a holder is, in the terms a later session can find it by (PLAN D6 rule 5).</summary>
/// <param name="Kind">World, player, or none.</param>
/// <param name="Template">A world holder's template.</param>
/// <param name="Position">A world holder's position now.</param>
/// <param name="Mobile">A creature or NPC (found within 16 m), else a prop (0.5 m).</param>
/// <param name="Character">A player's character.</param>
/// <param name="Name">For the log and the admin command.</param>
internal readonly record struct HolderIdentity(HolderKind Kind, Guid Template, WorldPosition Position, bool Mobile, CharacterKey Character, string? Name = null)
{
    /// <summary>A player's session stamp, independent of the reusable entity slot; 0 when not known.</summary>
    public ulong Visit { get; init; }

    public static HolderIdentity None => default;

    public static HolderIdentity OfWorld(Guid template, WorldPosition position, bool mobile, string? name = null) =>
        new(HolderKind.World, template, position, mobile, default, name);

    public static HolderIdentity OfPlayer(CharacterKey character, string? name = null) =>
        new(HolderKind.Player, Guid.Empty, default, false, character, name);
}

/// <summary>Everything the instance-data store reads from the game. Tick thread, every member.</summary>
internal interface IInstanceWorld
{
    /// <summary>Throws <see cref="KilnUnavailableException"/> (<c>items.instances</c>) when the item reads are off.</summary>
    void Require();

    /// <summary>The server-world step now.</summary>
    ulong Step { get; }

    /// <summary>The item now; null when it is gone or the ref's stamp is no longer the id's stamp.</summary>
    /// <exception cref="KilnUnavailableException">The reads cannot answer right now (never "gone" for that).</exception>
    InstanceItem? Read(ItemInstanceRef item);

    /// <summary>One batched read of all refs, with results in input order (null for a gone or stale ref).</summary>
    IReadOnlyList<InstanceItem?> Read(IReadOnlyList<ItemInstanceRef> items);

    /// <summary>Every item entity in the holder's inventories, in flat-slot order; empty when it has no inventory or the
    /// inventory is not initialized yet (<paramref name="initialized"/> false).</summary>
    IReadOnlyList<InstanceItem> ReadHolder(EntityId holder, out bool initialized);

    /// <summary>Counted: the item is followed until every handle is disposed or it is gone.</summary>
    IDisposable Follow(ItemInstanceRef item);

    /// <summary>A followed item changed its holder or slot (from, to).</summary>
    event Action<ItemInstanceRef, InstancePlace, InstancePlace>? Moved;

    /// <summary>A followed item no longer exists: its last known place and the step it was noticed in.</summary>
    event Action<ItemInstanceRef, InstancePlace, ulong>? Gone;

    /// <summary>Who the holder entity is (<see cref="HolderIdentity.None"/> when it has no persistent identity).</summary>
    HolderIdentity Holder(EntityId holder);

    /// <summary>Entities of the template within <paramref name="radius"/> of the point, nearest first.</summary>
    IReadOnlyList<EntityId> FindHolders(Guid template, WorldPosition near, double radius);

    /// <summary>The players in game now, with their characters (account-only keys included; the store refuses those).</summary>
    IReadOnlyList<(EntityId Entity, CharacterKey Character, ulong Visit)> PlayersInGame();
}
