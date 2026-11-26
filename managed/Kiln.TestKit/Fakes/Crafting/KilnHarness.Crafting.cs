// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private HarnessCrafting? _crafting;
    /// <summary>The linked crafting-source recording fake. Since 0.2.0-dev.30.6.</summary>
    public HarnessCrafting Crafting => _crafting ??= new HarnessCrafting(this);
}

/// <summary>A mod's live source registration in the kit. Since 0.2.0-dev.30.6.</summary>
/// <param name="Owner">The registering native owner.</param>
/// <param name="Player">The only player it supplies.</param>
/// <param name="Container">The storage container.</param>
/// <param name="Order">Its position relative to station outputs.</param>
/// <param name="Filter">The item-set interpretation.</param>
/// <param name="Items">A copy of the filter items.</param>
public sealed record FakeCraftSourceRegistration(ulong Owner, EntityId Player, EntityId Container, CraftSourceOrder Order,
    CraftSourceFilter Filter, IReadOnlyList<ItemId> Items)
{
    /// <summary>Registered with <see cref="CraftSourceOptions.Locks"/>: locks may count and pay from it. Since 0.2.0-dev.31.12, mod API 0.2.21.</summary>
    public bool Locks { get; init; }
    /// <summary>Registered with <see cref="CraftSourceOptions.Fills"/>: production-station fills may take from it. Since 0.2.0-dev.31.12.</summary>
    public bool Fills { get; init; }
    /// <summary>Registered with <see cref="CraftSourceOptions.Building"/>: block placements may count and pay from it. Since 0.2.0-dev.31.12.</summary>
    public bool Building { get; init; }
    /// <summary>Registered with <see cref="CraftSourceOptions.Respec"/>: the skill respec may count and pay its respec item from it. Since 0.2.0-dev.31.18.</summary>
    public bool Respec { get; init; }
}

/// <summary>Player-source registrations and their filtered counts in the kit. No base, game payment hook, station
/// outputs or client screens are simulated. Since 0.2.0-dev.30.6.</summary>
public sealed class HarnessCrafting
{
    private readonly KilnHarness _harness;
    internal HarnessCrafting(KilnHarness harness) => _harness = harness;
    /// <summary>Live registrations, in registration order. Disposing a handle or unloading its mod removes it.</summary>
    public IReadOnlyList<FakeCraftSourceRegistration> Sources => _harness.Run(() =>
    {
        var s = KitFakes.State<CraftingState>(_harness);
        lock (s.Gate) return (IReadOnlyList<FakeCraftSourceRegistration>)[..s.Sources.Select(x=>new FakeCraftSourceRegistration(x.Owner,
            new EntityId(x.Player),new EntityId(x.Container),(CraftSourceOrder)x.Order,x.Deny?CraftSourceFilter.Deny:CraftSourceFilter.Allow,
            Array.AsReadOnly(x.Items.Select(i=>new ItemId(i)).ToArray())){Locks=x.Locks,Fills=x.Fills,Building=x.Building,Respec=x.Respec})];
    });
    /// <summary>Registered source counts only (not the bag), with the same per-player allow/deny union and current
    /// inventory permission check as source_stock. No base restriction is applied.</summary>
    public IReadOnlyDictionary<ItemId, long> Stock(EntityId player) => _harness.Run(() =>
        (IReadOnlyDictionary<ItemId, long>)CraftingFake.Counts(KitFakes.State<CraftingState>(_harness), player.Value)
            .ToDictionary(x => new ItemId(x.Key), x => x.Value));

    /// <summary>The counts a lock may use: the links registered with <see cref="CraftSourceOptions.Locks"/> only, with the
    /// same filters, player-session generation and inventory permission (not the bag: the game counts it first). No lock
    /// check, interaction payment or client snapshot is simulated. Since 0.2.0-dev.31.12, mod API 0.2.21.</summary>
    public IReadOnlyDictionary<ItemId, long> LockStock(EntityId player) => _harness.Run(() =>
        (IReadOnlyDictionary<ItemId, long>)CraftingFake.Counts(KitFakes.State<CraftingState>(_harness), player.Value, locks: true)
            .ToDictionary(x => new ItemId(x.Key), x => x.Value));

    /// <summary>The counts a production-station fill may take: the links registered with <see cref="CraftSourceOptions.Fills"/>
    /// only, with the same filters, player-session generation and inventory permission (not the bag, not the base). No fill
    /// is simulated. Since 0.2.0-dev.31.12.</summary>
    public IReadOnlyDictionary<ItemId, long> FillStock(EntityId player) => _harness.Run(() =>
        (IReadOnlyDictionary<ItemId, long>)CraftingFake.Counts(KitFakes.State<CraftingState>(_harness), player.Value, fills: true)
            .ToDictionary(x => new ItemId(x.Key), x => x.Value));

    /// <summary>The counts a block placement may use: the links registered with <see cref="CraftSourceOptions.Building"/>
    /// only, likewise. No build cursor or placement payment is simulated. Since 0.2.0-dev.31.12.</summary>
    public IReadOnlyDictionary<ItemId, long> BuildStock(EntityId player) => _harness.Run(() =>
        (IReadOnlyDictionary<ItemId, long>)CraftingFake.Counts(KitFakes.State<CraftingState>(_harness), player.Value, building: true)
            .ToDictionary(x => new ItemId(x.Key), x => x.Value));

    /// <summary>The counts the skill respec (a node's unlearn, the Flame Altar's reset) may use: the links registered with
    /// <see cref="CraftSourceOptions.Respec"/> only, every item, likewise (the game pays only its respec item, the Runes).
    /// No respec payment or respec screen is simulated. Since 0.2.0-dev.31.18.</summary>
    public IReadOnlyDictionary<ItemId, long> RespecStock(EntityId player) => _harness.Run(() =>
        (IReadOnlyDictionary<ItemId, long>)CraftingFake.Counts(KitFakes.State<CraftingState>(_harness), player.Value, respec: true)
            .ToDictionary(x => new ItemId(x.Key), x => x.Value));

    /// <summary>The same registered counts restricted to ColorPalette items (dyes), with current filters, player-session
    /// generation and inventory permission. No tint screen or game transaction is simulated. Since 0.2.0-dev.30.6.</summary>
    public IReadOnlyDictionary<ItemId, long> Dyes(EntityId player) => _harness.Run(() =>
        (IReadOnlyDictionary<ItemId, long>)CraftingFake.Counts(KitFakes.State<CraftingState>(_harness), player.Value)
            .Where(x => Items.Get(new ItemId(x.Key))?.Category == ItemCategory.ColorPalette)
            .ToDictionary(x => new ItemId(x.Key), x => x.Value));
}
