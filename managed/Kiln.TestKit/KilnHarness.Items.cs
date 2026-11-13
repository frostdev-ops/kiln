// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private HarnessItems? _items;

    /// <summary>The fake kiln.items state of this harness (null before the constructor set it up).</summary>
    internal ItemsFake? ItemsFake { get; private set; }

    /// <summary>The items and inventories of this server as the kit's fake of kiln.items keeps them: the items the game
    /// has (the test adds them), the containers, and every give and take a mod asked for. A player's inventory is
    /// <see cref="FakePlayer.Inventory"/>.</summary>
    public HarnessItems Items => _items ??= new HarnessItems(this);

    private void StartItems() => ItemsFake = new ItemsFake(this);
}

/// <summary>One give or take a mod asked for (<see cref="HarnessItems.Calls"/>), as it ran.</summary>
public sealed class FakeItemsCall
{
    internal FakeItemsCall(ulong step, string? modId, bool take, EntityId entity, ItemId item, int requested, int done, ItemsStatus status, bool dryRun)
    {
        Step = step;
        ModId = modId;
        IsTake = take;
        Entity = entity;
        Item = item;
        Requested = requested;
        Done = done;
        Status = status;
        DryRun = dryRun;
    }

    /// <summary>The step whose start ran the transaction (the step after the request).</summary>
    public ulong Step { get; }

    /// <summary>The mod that asked; null for a call outside any mod.</summary>
    public string? ModId { get; }

    /// <summary>A take; false for a give.</summary>
    public bool IsTake { get; }

    /// <summary>Whose inventory.</summary>
    public EntityId Entity { get; }

    /// <summary>The item.</summary>
    public ItemId Item { get; }

    /// <summary>How many were asked for.</summary>
    public int Requested { get; }

    /// <summary>How many were given or taken (a dry run: would be).</summary>
    public int Done { get; }

    /// <summary>The result's status.</summary>
    public ItemsStatus Status { get; }

    /// <summary>A dry run: nothing changed.</summary>
    public bool DryRun { get; }

    /// <summary>The call, for a log line.</summary>
    public override string ToString() => $"step {Step} {ModId ?? "kiln"}: {(IsTake ? "take" : "give")} {Requested} x {Item} {(IsTake ? "from" : "to")} {Entity}: {Status} {Done}{(DryRun ? " (dry run)" : "")}";
}

/// <summary>One slot of a fake inventory (<see cref="FakeInventory.Slots"/>).</summary>
public sealed class FakeSlot
{
    internal FakeSlot(SlotId slot, ItemId item, int count, EntityId itemEntity, int level, ItemRarity? rarity)
    {
        Slot = slot;
        Item = item;
        Count = count;
        ItemEntity = itemEntity;
        Level = level;
        Rarity = rarity;
    }

    /// <summary>The slot (its 8-slot inventory's entity and index), as <see cref="global::Kiln.Items.Read"/> reports it.</summary>
    public SlotId Slot { get; }

    /// <summary>The item; none when the slot is empty.</summary>
    public ItemId Item { get; }

    /// <summary>How many.</summary>
    public int Count { get; }

    /// <summary>Gear: its item entity in the fake world; none for a stack.</summary>
    public EntityId ItemEntity { get; }

    /// <summary>Gear: its level; 0 for a stack.</summary>
    public int Level { get; }

    /// <summary>Gear: its rarity; null for a stack and an empty slot.</summary>
    public ItemRarity? Rarity { get; }

    /// <summary>The slot is empty.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>The slot, for a log line.</summary>
    public override string ToString() => IsEmpty ? $"{Slot}: empty" : $"{Slot}: {Count} x {Item}{(ItemEntity.IsNone ? "" : $" (gear {ItemEntity}, level {Level}, {Rarity})")}";
}

/// <summary>The inventory of a fake player (<see cref="FakePlayer.Inventory"/>) or of a container
/// (<see cref="HarnessItems.AddContainer"/>), as the fake kiln.items keeps it. The reads are the test's view; the
/// methods that change it are the test's hand (as if the game had changed it: no transaction, no event).</summary>
public sealed class FakeInventory
{
    private readonly KilnHarness _harness;
    private readonly ItemsFake.Inv _inv;

    internal FakeInventory(KilnHarness harness, ItemsFake.Inv inv)
    {
        _harness = harness;
        _inv = inv;
    }

    private ItemsFake Fake => _harness.ItemsFake ?? throw new InvalidOperationException("the harness has no items yet");

    /// <summary>The entity whose inventory it is.</summary>
    public EntityId Owner => new(_inv.Owner);

    /// <summary>Every slot, empty ones included, in the order <see cref="global::Kiln.Items.Read"/> lists them.</summary>
    public IReadOnlyList<FakeSlot> Slots => _harness.Run(() =>
    {
        lock (Fake.Lock)
            return (IReadOnlyList<FakeSlot>)[.. _inv.Slots.Select((s, i) => new FakeSlot(new SlotId(_inv.SlotId(i)), s.Count > 0 ? new ItemId(s.Item) : ItemId.None,
                s.Count, new EntityId(s.Pide), s.Level, s.Count > 0 && s.Pide != 0 ? (ItemRarity)s.Rarity : null))];
    });

    /// <summary>How many of <paramref name="item"/> the inventory holds.</summary>
    public int Count(ItemId item) => Slots.Where(s => s.Item == item).Sum(s => s.Count);

    /// <summary>How many of the item named <paramref name="name"/> (as <see cref="HarnessItems.AddItem"/> named it) it holds.</summary>
    /// <exception cref="ArgumentException">The game has no item of that name.</exception>
    public int Count(string name) => Count(_harness.Items.IdOf(name));

    /// <summary>Puts <paramref name="count"/> x <paramref name="item"/> in, as if the game had (stacks first, then empty
    /// slots; no transaction, no event). Returns how many fit.</summary>
    /// <exception cref="ArgumentException">The game has no such item, or the count is below 1.</exception>
    public int Put(ItemId item, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        return _harness.Run(() =>
        {
            var info = Fake.Item(item.Value) ?? throw new ArgumentException($"the game has no item {item} (add it with kiln.Items.AddItem)", nameof(item));
            return Fake.Put(_inv, info, count);
        });
    }

    /// <summary>Empties every slot, as if the game had (gear's item entities go too).</summary>
    public void Clear() => _harness.Run(() => Fake.Clear(_inv));

    /// <summary>The owner and what it holds, for a log line.</summary>
    public override string ToString() => $"inventory of {Owner}: {string.Join(", ", Slots.Where(s => !s.IsEmpty))}";
}

/// <summary>
/// The fake kiln.items of a harness (<see cref="KilnHarness.Items"/>): Kiln.Items' own code (<see cref="global::Kiln.Items"/>
/// Give, Take, TakeSlot, Read, FindInventories, the catalog, <see cref="ItemsGivenEvent"/> and <see cref="ItemsTakenEvent"/>)
/// runs on it.
/// </summary>
/// <remarks>
/// <para><b>Modelled.</b> The kit does not know the game's items: the test adds those its mod uses
/// (<see cref="AddItem"/>), with their stack size; an unknown item is refused as the game refuses it. A fake player has an
/// inventory of <see cref="PlayerInventories"/> generic 8-slot inventories from their first use while in game (a player
/// who leaves takes theirs along; one who comes back starts empty); a container is an entity of the fake world the test
/// gives one (<see cref="AddContainer"/>). A give or take runs as one transaction at the start of the next step and its
/// task completes then, as the game's runs in the service's program and answers in the next step: a give tops up stacks
/// first, then fills empty slots, gear one per slot with an item entity of its own (its level within the item's range,
/// its rarity); what does not fit is never created (Partial, or NoRoom with all-or-nothing); a take is all or nothing
/// and destroys gear's item entity; a dry run changes nothing.</para>
/// <para><b>Not modelled</b> (they report offline): water (<c>items.fuel</c>), moves between inventories
/// (<c>items.move</c>: the v3 table is not there), container names, transfer hooks, item instances, equip and unequip,
/// recipe results, upgrades and gem slots at creation; the game's player inventory layout (action bars, equipment, the
/// backpack's size), the "+N" popup, and saving inventories with the world.</para>
/// </remarks>
public sealed class HarnessItems
{
    private readonly KilnHarness _harness;

    internal HarnessItems(KilnHarness harness) => _harness = harness;

    private ItemsFake Fake => _harness.ItemsFake ?? throw new InvalidOperationException("the harness has no items yet");

    /// <summary>Says the game has an item, so a mod can give, take, look it up and read it. Returns its id (FNV-1a-32 of
    /// its GUID, as the game's).</summary>
    /// <param name="name">The item's debug name (<c>Material_T1_Twigs</c>).</param>
    /// <param name="maxStack">How many one slot holds (1 for gear, whatever is given).</param>
    /// <param name="category">The item's category.</param>
    /// <param name="gear">Every item is unique with an item entity of its own (weapons, armour).</param>
    /// <param name="rarity">The default rarity.</param>
    /// <param name="levelMin">Gear: the lowest level it rolls (0: none).</param>
    /// <param name="levelMax">Gear: the highest level (0: no range).</param>
    /// <param name="id">The item's GUID; empty: one made from the name (the same in every run).</param>
    /// <exception cref="ArgumentException">An empty name, a stack size below 1 or over 65535, or a name or GUID the game has already.</exception>
    public ItemId AddItem(string name, int maxStack, ItemCategory category = ItemCategory.Materials, bool gear = false,
        ItemRarity rarity = ItemRarity.Common, int levelMin = 0, int levelMax = 0, Guid id = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStack, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxStack, 65535);
        return new ItemId(_harness.Run(() => Fake.AddItem(name, maxStack, category, rarity, gear, levelMin, levelMax, id).Id));
    }

    /// <summary>The id of an item the test added.</summary>
    /// <exception cref="ArgumentException">The game has no item of that name.</exception>
    public ItemId IdOf(string name) =>
        new(_harness.Run(() => Fake.Catalog.FirstOrDefault(i => i.Name == name)?.Id) ?? throw new ArgumentException($"the game has no item named {name}", nameof(name)));

    /// <summary>Gives an entity of the fake world an inventory: a chest, a station. Returns it.</summary>
    /// <param name="entity">The entity (<see cref="FakeWorld.Create(WorldPosition)"/>).</param>
    /// <param name="inventories">How many 8-slot inventories it has (1 to 32); the first is the entity itself, each
    /// further one an entity of its own, as in the game.</param>
    /// <exception cref="ArgumentException">No such entity, or it has an inventory already.</exception>
    public FakeInventory AddContainer(EntityId entity, int inventories = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inventories, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(inventories, ItemsAbi.MaxInventories);
        return new FakeInventory(_harness, _harness.Run(() => Fake.AddContainer(entity.Value, inventories)));
    }

    /// <summary>The inventory of an entity (a container, or an in-game fake player's); null when it has none.</summary>
    public FakeInventory? Of(EntityId entity) => _harness.Run(() => Fake.Of(entity.Value)) is { } inv ? new FakeInventory(_harness, inv) : null;

    /// <summary>How many 8-slot inventories a fake player's inventory gets (default 5, 40 slots): the kit does not model
    /// the game's layout of action bars, equipment and backpack.</summary>
    public int PlayerInventories
    {
        get => Fake.PlayerInventories;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, ItemsAbi.MaxInventories);
            Fake.PlayerInventories = value;
        }
    }

    /// <summary>Every give and take a mod asked for, in the order they ran, with what became of each.</summary>
    public IReadOnlyList<FakeItemsCall> Calls => _harness.Run(() => Fake.Calls);
}
