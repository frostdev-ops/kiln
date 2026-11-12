// SPDX-License-Identifier: MIT
// ItemId lives in Kiln.Api since dev.25; mods built against the old Kiln.Items still find it through this forward.
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(Kiln.ItemId))]

namespace Kiln;

/// <summary>One slot of an 8-slot inventory: the inventory entity and the slot index 0-7 (the game's
/// InventorySlotId).</summary>
public readonly record struct SlotId(ulong Value)
{
    public static SlotId Of(EntityId inventory, int index) =>
        index is < 0 or > 7 ? throw new ArgumentOutOfRangeException(nameof(index)) : new(inventory.Value | ((ulong)index << 32));
    public EntityId Inventory => new((uint)(Value & 0xFFFFFFFF));
    public int Index => (int)((Value >> 32) & 0xFF);
    public bool IsNone => Value == 0;
    public override string ToString() => $"{Inventory}:{Index}";
}

public enum ItemRarity : byte
{
    Common = 0,
    Uncommon = 1,
    Rare = 2,
    Epic = 3,
    Legendary = 4,
}

/// <summary>The game's ItemCategory (ItemInfo +0xCC). Values measured against the dashboard catalog on svn 1285601
/// (docs/re/items.md); 14 was not seen.</summary>
public enum ItemCategory : byte
{
    Customization = 0,
    Equipment = 1,
    Weapons = 2,
    Tools = 3,
    Instrument = 4,
    FishingRod = 5,
    BuildTools = 6,
    Consumables = 7,
    Ammunition = 8,
    Materials = 9,
    Blueprints = 10,
    Currency = 11,
    Collectibles = 12,
    AnimalFood = 13,
    WeaponGem = 15,
    ColorPalette = 16,
}

/// <summary>What a linked inventory is for (InventorySetup link category).</summary>
public enum InventoryCategory : byte
{
    None = 0,
    Customization = 1,
    Equipment = 2,
    Currency = 3,
    Generic = 4,
    Virtual = 5,
}

/// <summary>An item definition from the server world's item registry.</summary>
public sealed record ItemInfo(
    ItemId Id,
    string Name,
    string? DisplayName,
    int MaxStack,
    ItemCategory Category,
    ItemRarity Rarity,
    bool IsGear,
    int LevelMin,
    int LevelMax,
    byte EquipSlot,
    uint Flags,
    Guid Guid)
{
    /// <summary>Blueprints and other unlock items (ItemFlag IsBlueprint).</summary>
    public bool IsBlueprint => (Flags & 0x8) != 0;
    /// <summary>The display name when the shipped catalog knows it, else the game's debug name.</summary>
    public string Label => DisplayName ?? Name;
    public override string ToString() => $"{Label} ({Name}, {Id})";
}

/// <summary>Gear parameters for <see cref="Items.Give(EntityId, ItemId, int, Gear?, GiveOptions, SlotId?)"/>. Unset
/// values are the game's defaults, the same as crafting: the level is a random roll in the item's level range, the rarity
/// is <c>rarity</c>, or the lowest allowed one (Common) for an item with <c>generateRarity</c> set (loot weapons). The
/// game clamps a given level to the item's level range; a crafted item, and a new item unless it opts in to rolled loot, has
/// one level (docs/kiln/services/content.md "Rarity, level and perks of new gear").</summary>
public readonly record struct Gear(int? Level = null, ItemRarity? Rarity = null)
{
    /// <summary>Upgrades (perks) unlocked at creation, as the workbench's upgrade does it: each raises the level by 1.
    /// 1-4, or 5 or 255 for all the item has (the rarity decides how many: Common 1 .. Legendary 5). Null or 0: none, the
    /// game's default. Needs <see cref="Items.GearOptionsAvailable"/> (else the give is Unavailable).</summary>
    public int? Unlocked { get; init; }

    /// <summary>A weapon gets an empty gem slot when the game's own roll at creation gave none (other items are
    /// unchanged). Needs <see cref="Items.GearOptionsAvailable"/>.</summary>
    public bool GemSlot { get; init; }
}

[Flags]
public enum GiveOptions
{
    None = 0,
    /// <summary>A player receiver gets the game's "+N item" popup (and PickupEvent).</summary>
    Notify = 1,
    /// <summary>Commit only when every item fits.</summary>
    AllOrNothing = 2,
    /// <summary>Do not top up existing stacks; use empty slots only.</summary>
    NoMerge = 4,
    /// <summary>Run the transaction and roll it back: the result says what would happen.</summary>
    DryRun = 8,
}

public enum ItemsStatus
{
    /// <summary>Everything requested was given / taken (dry run: would be).</summary>
    Done = 0,
    /// <summary>Give: only <see cref="ItemsResult.Done"/> of the requested fit; the rest was never created.</summary>
    Partial = 1,
    NoRoom = 2,
    /// <summary>Take: fewer are there than requested; nothing was taken.</summary>
    NotEnough = 3,
    /// <summary>No such entity, or it has no (initialized) inventory.</summary>
    NoInventory = 4,
    UnknownItem = 5,
    /// <summary>The slot is not the entity's, or no longer holds the item.</summary>
    SlotChanged = 6,
    /// <summary>The game's transaction returned an error (<see cref="ItemsResult.GameError"/>).</summary>
    Refused = 7,
    /// <summary>The service cannot transact on this build / right now; nothing happened.</summary>
    Unavailable = 8,
    /// <summary>Not run: the server world did not step.</summary>
    Dropped = 9,
    /// <summary>Refused before it was queued (bad arguments); nothing happened.</summary>
    Invalid = 100,
}

/// <summary>One slot a transaction changed.</summary>
public readonly record struct ItemChange(SlotId Slot, ItemId Item, int Amount, EntityId ItemEntity);

public sealed record ItemsResult(
    ItemsStatus Status,
    EntityId Entity,
    ItemId Item,
    int Requested,
    int Done,
    IReadOnlyList<ItemChange> Changes,
    string Message,
    int GameError,
    int Steps,
    bool DryRun)
{
    /// <summary>Something was given / taken (or would be, in a dry run).</summary>
    public bool Succeeded => Status is ItemsStatus.Done or ItemsStatus.Partial;
    public override string ToString() =>
        $"{Status} {Done}/{Requested} x {Item} @ {Entity}{(DryRun ? " (dry run)" : "")}" + (Message.Length > 0 ? $": {Message}" : "");

    internal static ItemsResult Fail(ItemsStatus status, EntityId e, ItemId item, int count, string message) =>
        new(status, e, item, count, 0, Array.Empty<ItemChange>(), message, 0, 0, false);
}

/// <summary>One slot of an inventory.</summary>
public sealed record InventorySlot(
    SlotId Slot,
    ItemId Item,
    int Count,
    EntityId ItemEntity,
    int Level,
    ItemRarity? Rarity,
    InventoryCategory Category,
    int Link,
    bool IsGear,
    bool IsEquipped,
    bool IsBroken,
    ushort ItemState)
{
    public bool IsEmpty => Item.IsNone || Count == 0;
}

/// <summary>An entity's inventory: every slot of every linked 8-slot inventory, in link order.</summary>
public sealed record InventoryContents(
    EntityId Entity,
    bool Initialized,
    byte SetupFlags,
    int GenericSlots,
    int AvailableSlots,
    IReadOnlyList<(EntityId Inventory, InventoryCategory Category)> Inventories,
    IReadOnlyList<InventorySlot> Slots)
{
    public bool IsPlayer => (SetupFlags & 1) != 0;
    public bool IsNpc => (SetupFlags & 2) != 0;
    public int UsedSlots => Slots.Count(s => !s.IsEmpty);
    public int FreeSlots(InventoryCategory category = InventoryCategory.Generic) =>
        Slots.Count(s => s.IsEmpty && s.Category == category);
    /// <summary>How many of <paramref name="item"/> the inventory holds.</summary>
    public int Count(ItemId item) => Slots.Where(s => s.Item == item).Sum(s => s.Count);
}

[Flags]
public enum InventoryKinds
{
    /// <summary>Chests, stations and other non-actors.</summary>
    Containers = 1,
    Players = 2,
    Npcs = 4,
    /// <summary>Only inventories that hold at least one item.</summary>
    NonEmpty = 8,
    All = Containers | Players | Npcs,
}

/// <summary>State of the kiln.items service.</summary>
public sealed record ItemsServiceStatus(bool Transactions, bool Read, bool Catalog, int Program, int Pending, int Items,
    ulong Committed, ulong RolledBack, ulong ProgramUpdates, string Detail);
