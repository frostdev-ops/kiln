// SPDX-License-Identifier: MIT
// ItemId lives in Kiln.Api since dev.26; mods built against the old Kiln.Items still find it through this forward.
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(Kiln.ItemId))]

namespace Kiln;

/// <summary>One slot of an 8-slot inventory: the inventory entity and the slot index 0-7 (the game's
/// InventorySlotId).</summary>
/// <param name="Value">The packed id: the inventory entity's id in the low 32 bits and the slot index (0-7) from bit
/// 32. 0 is no slot.</param>
public readonly record struct SlotId(ulong Value)
{
    /// <summary>The slot <paramref name="index"/> (0-7) of the inventory entity <paramref name="inventory"/>. Throws
    /// <see cref="ArgumentOutOfRangeException"/> for an index outside 0-7.</summary>
    public static SlotId Of(EntityId inventory, int index) =>
        index is < 0 or > 7 ? throw new ArgumentOutOfRangeException(nameof(index)) : new(inventory.Value | ((ulong)index << 32));
    /// <summary>The inventory entity this slot belongs to.</summary>
    public EntityId Inventory => new((uint)(Value & 0xFFFFFFFF));
    /// <summary>The slot's index in its inventory, 0-7.</summary>
    public int Index => (int)((Value >> 32) & 0xFF);
    /// <summary>True for the default value, which names no slot.</summary>
    public bool IsNone => Value == 0;
    /// <summary>The inventory entity and the index, <c>"1043:5"</c>.</summary>
    public override string ToString() => $"{Inventory}:{Index}";
}

/// <summary>The rarity of an item, from Common (the lowest) to Legendary (the highest).</summary>
public enum ItemRarity : byte
{
    /// <summary>The lowest rarity.</summary>
    Common = 0,
    /// <summary>The second rarity.</summary>
    Uncommon = 1,
    /// <summary>The third rarity.</summary>
    Rare = 2,
    /// <summary>The fourth rarity.</summary>
    Epic = 3,
    /// <summary>The highest rarity.</summary>
    Legendary = 4,
}

/// <summary>The game's ItemCategory (ItemInfo +0xCC). Values measured against the dashboard catalog on svn 1285601
/// (docs/re/items.md); 14 was not seen.</summary>
public enum ItemCategory : byte
{
    /// <summary>Customization items (a character's looks).</summary>
    Customization = 0,
    /// <summary>Armor and other worn equipment.</summary>
    Equipment = 1,
    /// <summary>Weapons.</summary>
    Weapons = 2,
    /// <summary>Tools.</summary>
    Tools = 3,
    /// <summary>Musical instruments.</summary>
    Instrument = 4,
    /// <summary>Fishing rods.</summary>
    FishingRod = 5,
    /// <summary>Building tools.</summary>
    BuildTools = 6,
    /// <summary>Consumables (food, potions and the like).</summary>
    Consumables = 7,
    /// <summary>Ammunition.</summary>
    Ammunition = 8,
    /// <summary>Crafting and building materials.</summary>
    Materials = 9,
    /// <summary>Blueprints and other unlock items.</summary>
    Blueprints = 10,
    /// <summary>Currency.</summary>
    Currency = 11,
    /// <summary>Collectibles.</summary>
    Collectibles = 12,
    /// <summary>Animal food.</summary>
    AnimalFood = 13,
    /// <summary>Weapon gems.</summary>
    WeaponGem = 15,
    /// <summary>Color palettes (dyes).</summary>
    ColorPalette = 16,
}

/// <summary>What a linked inventory is for (InventorySetup link category).</summary>
public enum InventoryCategory : byte
{
    /// <summary>No category.</summary>
    None = 0,
    /// <summary>A link that holds customization items.</summary>
    Customization = 1,
    /// <summary>A link that holds worn equipment.</summary>
    Equipment = 2,
    /// <summary>A link that holds currency.</summary>
    Currency = 3,
    /// <summary>The ordinary slots that take any item: a backpack's or a chest's.
    /// <see cref="InventoryContents.FreeSlots"/> counts these by default.</summary>
    Generic = 4,
    /// <summary>A link that holds no items itself: its setup links further inventories.</summary>
    Virtual = 5,
}

/// <summary>An item definition from the server world's item registry.</summary>
/// <param name="Id">The item's id, the hash of its <paramref name="Guid"/> (see
/// <see cref="ItemId.FromGuidBytes"/>).</param>
/// <param name="Name">The game's debug name, e.g. <c>Material_T1_Wood_Logs</c>.</param>
/// <param name="DisplayName">The name players see, from the catalog Kiln ships; null when that catalog does not know
/// the item.</param>
/// <param name="MaxStack">How many fit in one stack.</param>
/// <param name="Category">The game's category of the item.</param>
/// <param name="Rarity">The item definition's rarity.</param>
/// <param name="IsGear">True for gear: an item with a level, a rarity and perks.</param>
/// <param name="LevelMin">The lowest level the item rolls at.</param>
/// <param name="LevelMax">The highest level the item rolls at.</param>
/// <param name="EquipSlot">The game's equipment slot type of the item.</param>
/// <param name="Flags">The game's raw ItemFlag bits. <see cref="IsBlueprint"/> reads one of them.</param>
/// <param name="Guid">The item's GUID in the game's registry.</param>
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
    /// <summary>The label, the debug name and the id, for logs.</summary>
    public override string ToString() => $"{Label} ({Name}, {Id})";
}

/// <summary>Gear parameters for <see cref="Items.Give(EntityId, ItemId, int, Gear?, GiveOptions, SlotId?)"/>. Unset
/// values are the game's defaults, the same as crafting: the level is a random roll in the item's level range, the rarity
/// is <c>rarity</c>, or the lowest allowed one (Common) for an item with <c>generateRarity</c> set (loot weapons). The
/// game clamps a given level to the item's level range; a crafted item, and a new item unless it opts in to rolled loot, has
/// one level (docs/kiln/services/content.md "Rarity, level and perks of new gear").</summary>
/// <param name="Level">The item's level; null for the game's default (a random roll in the item's level range). The
/// game clamps it to that range.</param>
/// <param name="Rarity">The item's rarity; null for the game's default (the item's own rarity, or Common for an item
/// with <c>generateRarity</c> set).</param>
public readonly record struct Gear(int? Level = null, ItemRarity? Rarity = null)
{
    /// <summary>Upgrades (perks) unlocked at creation, as the workbench's upgrade does it: each raises the level by 1.
    /// 1-4, or 5 or 255 for all the item has (the rarity decides how many: Common 1 .. Legendary 5). Null or 0: none, the
    /// game's default. Needs <see cref="Items.GearOptionsAvailable"/> (else the give is Unavailable).</summary>
    [RequiresCapability(Items.Caps.GearOptions)]
    public int? Unlocked { get; init; }

    /// <summary>A weapon gets an empty gem slot when the game's own roll at creation gave none. Other gear (rings,
    /// armour, shields, tools...) gets one only when a mod allowed its kind with Kiln.Abilities' <c>Gems.AllowSockets</c>
    /// (capability <c>gems.sockets</c>); otherwise it is unchanged. Needs <see cref="Items.GearOptionsAvailable"/>.</summary>
    [RequiresCapability(Items.Caps.GearOptions)]
    public bool GemSlot { get; init; }
}

/// <summary>Options of <see cref="Items.Give(EntityId, ItemId, int, Gear?, GiveOptions, SlotId?)"/> and the calls
/// like it. Combine them with <c>|</c>.</summary>
[Flags]
public enum GiveOptions
{
    /// <summary>No options: no popup, new items top up existing stacks first, and a give that only partly fits is
    /// <see cref="ItemsStatus.Partial"/>.</summary>
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

/// <summary>How a give, take, move or water write ended. Game-side outcomes are a status, never an
/// exception.</summary>
public enum ItemsStatus
{
    /// <summary>Everything requested was given / taken (dry run: would be).</summary>
    Done = 0,
    /// <summary>Give: only <see cref="ItemsResult.Done"/> of the requested fit; the rest was never created.</summary>
    Partial = 1,
    /// <summary>Give: nothing fits (or not all of it, with <see cref="GiveOptions.AllOrNothing"/>); nothing
    /// changed.</summary>
    NoRoom = 2,
    /// <summary>Take: fewer are there than requested; nothing was taken.</summary>
    NotEnough = 3,
    /// <summary>No such entity, or it has no (initialized) inventory.</summary>
    NoInventory = 4,
    /// <summary>The item id is not in the game's item registry.</summary>
    UnknownItem = 5,
    /// <summary>The slot is not the entity's, or no longer holds the item.</summary>
    SlotChanged = 6,
    /// <summary>The game's transaction returned an error (<see cref="ItemsResult.GameError"/>).</summary>
    Refused = 7,
    /// <summary>The service cannot transact on this build / right now; nothing happened.</summary>
    Unavailable = 8,
    /// <summary>Not run: the server world did not step.</summary>
    Dropped = 9,
    /// <summary>An add into a player's storage that the storage's filter does not take (<c>StorageFilter</c>);
    /// nothing changed. Since 0.2.0-dev.30.7 (mod API 0.2.16).</summary>
    Filtered = 10,
    /// <summary>Refused before it was queued (bad arguments); nothing happened.</summary>
    Invalid = 100,
}

/// <summary>One slot a transaction changed.</summary>
/// <param name="Slot">The slot that changed.</param>
/// <param name="Item">The item put into or taken out of it.</param>
/// <param name="Amount">How many were put in (a give) or taken out (a take).</param>
/// <param name="ItemEntity">The item entity of a piece of gear: a give, the one the game created (none in a dry run);
/// a take, the one it destroyed. None for a plain stack.</param>
public readonly record struct ItemChange(SlotId Slot, ItemId Item, int Amount, EntityId ItemEntity)
{
    /// <summary>The item entity as one particular item, with the stamp kiln.items gave it at the commit (a give: the item
    /// the game created; a take: the item it destroyed, no longer alive). <see cref="ItemInstanceRef.None"/> for a plain
    /// stack, which has no item entity, for a dry-run give, and when the capability <c>items.instances</c> is off (an
    /// older Kiln, an unknown game build). Filled by Kiln when it builds the value.</summary>
    [AnyThread]
    public ItemInstanceRef Ref { get; init; }
}

/// <summary>What a give or take did. <see cref="ItemsResult.Status"/> says how it ended; game-side outcomes never
/// throw.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Entity">The inventory owner (player, chest, NPC) it ran on.</param>
/// <param name="Item">The item asked for.</param>
/// <param name="Requested">How many were asked for.</param>
/// <param name="Done">How many were given or taken (a dry run: how many would be).</param>
/// <param name="Changes">The slots it changed, with the amount in each.</param>
/// <param name="Message">Why it failed or fell short, in words; empty when there is nothing to add.</param>
/// <param name="GameError">The game's own error code when its transaction returned one
/// (<see cref="ItemsStatus.Refused"/>); 0 otherwise.</param>
/// <param name="Steps">How many sim steps passed between the request and the transaction.</param>
/// <param name="DryRun">True when it was a dry run: nothing changed, the result says what would have.</param>
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
    bool DryRun) : IKilnResult
{
    /// <summary>Something was given / taken (or would be, in a dry run).</summary>
    public bool Succeeded => Status is ItemsStatus.Done or ItemsStatus.Partial;
    /// <summary>Everything requested was given / taken (<see cref="ItemsStatus.Done"/>), as every Kiln result's Ok says
    /// (<see cref="IKilnResult"/>); a <see cref="ItemsStatus.Partial"/> give is not Ok (<see cref="Succeeded"/> counts it).
    /// Since 0.2.0-dev.32.</summary>
    public bool Ok => Status == ItemsStatus.Done;
    /// <summary>Why it was not <see cref="Ok"/>: the status and the message; null when Ok. Since 0.2.0-dev.32.</summary>
    public string? Reason => Ok ? null : Message.Length > 0 ? $"{Status}: {Message}" : Status.ToString();
    /// <summary>The status, the counts, the item and the entity, for logs.</summary>
    public override string ToString() =>
        $"{Status} {Done}/{Requested} x {Item} @ {Entity}{(DryRun ? " (dry run)" : "")}" + (Message.Length > 0 ? $": {Message}" : "");

    internal static ItemsResult Fail(ItemsStatus status, EntityId e, ItemId item, int count, string message) =>
        new(status, e, item, count, 0, Array.Empty<ItemChange>(), message, 0, 0, false);
}

/// <summary>One slot of an inventory.</summary>
/// <param name="Slot">The slot (inventory entity and index).</param>
/// <param name="Item">The item in the slot; <see cref="ItemId.None"/> when it is empty.</param>
/// <param name="Count">How many of it are in the slot.</param>
/// <param name="ItemEntity">The item entity of a piece of gear, tool or can; none for a plain stack.</param>
/// <param name="Level">The item's level; meaningful for gear.</param>
/// <param name="Rarity">The item's rarity; null when the game reports none for the slot.</param>
/// <param name="Category">The category of the inventory link the slot sits in.</param>
/// <param name="Link">The index of that link in the owner's inventory setup.</param>
/// <param name="IsGear">True for gear: the slot holds an item entity with a level and a rarity.</param>
/// <param name="IsEquipped">True when the item is equipped.</param>
/// <param name="IsBroken">True when the item is broken (no durability left).</param>
/// <param name="ItemState">The game's raw ItemState bits of the item (<c>itemState</c>; for example 0x100 is
/// HasWeaponGemSlot).</param>
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
    /// <summary>True when the slot holds no item or a count of 0.</summary>
    public bool IsEmpty => Item.IsNone || Count == 0;

    /// <summary>The item in this slot as one particular item, with the stamp kiln.items gave it at this read: equal to
    /// the <c>Ref</c> of the same item in a give, take or move result and in any later read while it lives.
    /// <see cref="ItemInstanceRef.None"/> for an empty slot, for a plain stack (no item entity), and when the capability
    /// <c>items.instances</c> is off (an older Kiln, an unknown game build). Filled by Kiln when it builds the
    /// value.</summary>
    [AnyThread]
    public ItemInstanceRef Ref { get; init; }
}

/// <summary>An entity's inventory: every slot of every linked 8-slot inventory, in link order.</summary>
/// <param name="Entity">The inventory owner that was read.</param>
/// <param name="Initialized">True when the game has set up the owner's inventories; false means the other values are
/// not meaningful yet.</param>
/// <param name="SetupFlags">The game's InventorySetup flag byte. Bit 1 marks a player and bit 2 an NPC
/// (<see cref="IsPlayer"/>, <see cref="IsNpc"/>).</param>
/// <param name="GenericSlots">The setup's <c>genericSlotCount</c>.</param>
/// <param name="AvailableSlots">The setup's <c>availableSlotCount</c>.</param>
/// <param name="Inventories">The inventory entities of the setup, in link order, each with its category.</param>
/// <param name="Slots">Every slot of every linked inventory, in link order (empty ones included).</param>
public sealed record InventoryContents(
    EntityId Entity,
    bool Initialized,
    byte SetupFlags,
    int GenericSlots,
    int AvailableSlots,
    IReadOnlyList<(EntityId Inventory, InventoryCategory Category)> Inventories,
    IReadOnlyList<InventorySlot> Slots)
{
    /// <summary>True when the setup is flagged as a player's.</summary>
    public bool IsPlayer => (SetupFlags & 1) != 0;
    /// <summary>True when the setup is flagged as an NPC's.</summary>
    public bool IsNpc => (SetupFlags & 2) != 0;
    /// <summary>How many slots hold an item.</summary>
    public int UsedSlots => Slots.Count(s => !s.IsEmpty);
    /// <summary>How many empty slots there are in inventories of <paramref name="category"/>.</summary>
    public int FreeSlots(InventoryCategory category = InventoryCategory.Generic) =>
        Slots.Count(s => s.IsEmpty && s.Category == category);
    /// <summary>How many of <paramref name="item"/> the inventory holds.</summary>
    public int Count(ItemId item) => Slots.Where(s => s.Item == item).Sum(s => s.Count);
}

/// <summary>Which kinds of inventory owner <see cref="Items.FindInventories"/> looks for. Combine them with
/// <c>|</c>.</summary>
[Flags]
public enum InventoryKinds
{
    /// <summary>Chests, stations and other non-actors.</summary>
    Containers = 1,
    /// <summary>Players' inventories.</summary>
    Players = 2,
    /// <summary>NPCs' inventories.</summary>
    Npcs = 4,
    /// <summary>Only inventories that hold at least one item.</summary>
    NonEmpty = 8,
    /// <summary>Containers, players and NPCs.</summary>
    All = Containers | Players | Npcs,
    /// <summary>The holders of players' storages (<c>Items.RegisterPlayerStorage</c>): only when asked for by this
    /// flag, never as part of <see cref="All"/> or <see cref="Containers"/>, so code that sums every chest does not count a
    /// player's quiver as well. Since 0.2.0-dev.30.7 (mod API 0.2.16).</summary>
    PlayerStorage = 16,
}

/// <summary>State of the kiln.items service.</summary>
/// <param name="Transactions">True when give and take can run.</param>
/// <param name="Read">True when <see cref="Kiln.Items.Read"/> and <see cref="Kiln.Items.FindInventories"/>
/// work.</param>
/// <param name="Catalog">True when the item catalog is loaded.</param>
/// <param name="Program">The state of the kiln.items program in the server world: 0 not registered, 1 waiting for the
/// world, 2 registered but not run yet, 3 running, 4 broken (give and take are off).</param>
/// <param name="Pending">Requests waiting for the program.</param>
/// <param name="Items">How many items the catalog holds.</param>
/// <param name="Committed">Transactions committed so far.</param>
/// <param name="RolledBack">Transactions rolled back so far.</param>
/// <param name="ProgramUpdates">How many times the program has run.</param>
/// <param name="Detail">The reason when <see cref="Transactions"/> is false; empty otherwise.</param>
public sealed record ItemsServiceStatus(bool Transactions, bool Read, bool Catalog, int Program, int Pending, int Items,
    ulong Committed, ulong RolledBack, ulong ProgramUpdates, string Detail);
