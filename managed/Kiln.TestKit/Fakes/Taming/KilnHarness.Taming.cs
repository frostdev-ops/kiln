// SPDX-License-Identifier: MIT
// The test kit's Kiln.Taming (API-SHAPE-2 C11): the real attempt engine (selections, tokens, the replay cache, exact-cost
// receipts, the refund policies, timeouts, events) on a world and item costs of the kit's: owners the test joins,
// creatures it places, and inventories it fills. A mod's Taming.Define, Begin, Advance, Retry, Cancel, Choices and Inspect
// run in kiln.Run (or its handlers) as on a server; each step runs the engine's own tick. A tame of a definition that names
// a pet makes a Kiln.Pets record when kiln.Pets is in use too (the same character, joined to both).
namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private HarnessTaming? _taming;

    /// <summary>The fake of Kiln.Taming's world and item costs (created and installed on first use, gone with the harness).</summary>
    public HarnessTaming Taming => _taming ??= new HarnessTaming(this);

    internal HarnessTaming? TamingIfUsed => _taming;
}

/// <summary>Kiln.Taming offline: the players in game, the creatures they tame and what their inventories hold.</summary>
public sealed class HarnessTaming
{
    private readonly KilnHarness _kiln;
    private readonly KitTamingWorld _world = new();
    private readonly KitTamingCosts _costs = new();
    private readonly Func<string, bool> _caps;

    internal HarnessTaming(KilnHarness kiln)
    {
        _kiln = kiln;
        _caps = TamingCaps.Is;
        TamingCaps.Is = _ => true;   // taming.steps, taming.native and the exact costs: the kit plays them
        var engine = new TamingEngine(_world, _costs)
        {
            // the binder makes the pet record through Kiln.Pets as a server does; the kit's players are its characters
            Binder = new PetsTamingBinder { CharacterOf = p => p.CharacterId == Guid.Empty ? null : new CharacterKey(p.SteamId, p.CharacterId) },
            ResolveCreature = id => PetCreatures.Resolve(id, out _)?.Template,
        };
        kiln.Run(() =>
        {
            // a mod's tamings defined before its test first used kiln.Taming (its OnLoad: a definition needs no world) stay
            // its own on the kit's engine: they begin here, and they go when the mod unloads
            if (TamingRuntime.Current is { } before)
                foreach (var (d, mod) in before.Registered) engine.Define(d, mod);
            TamingRuntime.Engine = engine;
        });
    }

    /// <summary>The player is in game at <paramref name="at"/> (a character joined to <see cref="HarnessPets"/> too is the same
    /// player there). Returns the player its requests name: the same whichever fake joined the character first, and the same
    /// again after a <see cref="Leave"/>.</summary>
    public PlayerRef Join(CharacterKey character, WorldPosition at = default) => _world.Join(_kiln.KitPlayer(character), at);

    /// <summary>The player left: their attempts fail OwnerLeft at the next step.</summary>
    public void Leave(PlayerRef player) => _world.Owners.Remove(player);

    /// <summary>A creature of <paramref name="creature"/> (a creatures registry id or a creature block's) at
    /// <paramref name="at"/>; <paramref name="nativeTameable"/>: a tameable wild animal the game's own steps count on.</summary>
    public EntityRef Place(string creature, WorldPosition at, double healthFraction = 1, bool nativeTameable = false) =>
        _world.Place(creature, at, healthFraction, nativeTameable);

    /// <summary>The creature's health is now <paramref name="healthFraction"/> of its maximum (a weaken step reads it).</summary>
    public void Hurt(EntityRef creature, double healthFraction) => _world.Set(creature, t => t with { HealthFraction = healthFraction });

    /// <summary>The game counted one native step on the creature (a petting while it ate its bait).</summary>
    public void NativeStep(EntityRef creature) => _world.Set(creature, t => t with { NativeProgress = t.NativeProgress + 1 });

    /// <summary>The creature is gone (its attempt fails BodyGone).</summary>
    public void Remove(EntityRef creature) => _world.Targets.Remove(creature.Id);

    /// <summary>Puts <paramref name="count"/> of <paramref name="item"/> (a plain stack) in a slot of the player's own and
    /// returns its selection, as <c>Taming.Choices</c> would offer it.</summary>
    public ItemSelection Give(PlayerRef player, ItemId item, int count) => _costs.Give(_world.EntityOf(player), item, count);

    /// <summary>How many of <paramref name="item"/> the player holds now.</summary>
    public int Count(PlayerRef player, ItemId item) => _costs.Count(_world.EntityOf(player), item);

    internal void End()
    {
        TamingRuntime.Reset();
        TamingCaps.Is = _caps;
    }
}

internal sealed class KitTamingWorld : ITamingWorld
{
    private static readonly GameTamingWorld Clock = new();
    internal readonly Dictionary<PlayerRef, TamingOwner> Owners = new();
    internal readonly Dictionary<EntityId, TamingTarget> Targets = new();
    private ulong _sessions;
    private uint _next = 0x7800_0000;

    public double Now => Clock.Now;
    public ulong WorldEpoch => 1;

    public PlayerRef Join(PlayerRef who, WorldPosition at)
    {
        Owners[who] = new TamingOwner(who, who.Entity, ++_sessions, at);
        return who;
    }

    public EntityRef EntityOf(PlayerRef player) =>
        Owners.TryGetValue(player, out var o) ? o.Entity : throw new ArgumentException("the player has not joined (HarnessTaming.Join)", nameof(player));

    public EntityRef Place(string creature, WorldPosition at, double health, bool native)
    {
        var e = new EntityRef(new EntityId(_next++), 1);
        Targets[e.Id] = new TamingTarget(true, false, at, health, creature, 0, native) { Template = PetCreatures.Resolve(creature, out _)?.Template ?? Guid.Empty };
        return e;
    }

    public void Set(EntityRef e, Func<TamingTarget, TamingTarget> change)
    {
        if (!Targets.TryGetValue(e.Id, out var t)) throw new ArgumentException($"{e} is not a creature the kit placed", nameof(e));
        Targets[e.Id] = change(t);
    }

    public TamingOwner? Owner(PlayerRef player, out string? reason)
    {
        reason = Owners.ContainsKey(player) ? null : "OwnerOffline: the player is not in game";
        return Owners.TryGetValue(player, out var o) ? o : null;
    }

    public TamingTarget? Target(EntityRef target) => Targets.TryGetValue(target.Id, out var t) ? t : null;
}

/// <summary>Exact costs on the kit's inventories: a take takes from the selected slot only, a refund gives back to it.</summary>
internal sealed class KitTamingCosts : ITamingCosts
{
    private readonly Dictionary<EntityId, List<InventorySlot>> _slots = new();
    private ulong _slot = 1;

    public ItemSelection Give(EntityRef owner, ItemId item, int count)
    {
        if (!_slots.TryGetValue(owner.Id, out var list)) _slots[owner.Id] = list = [];
        var s = new InventorySlot(new SlotId(_slot++), item, count, EntityId.None, 0, null, default, 0, false, false, false, 0);
        list.Add(s);
        return ItemSelection.Of(owner, s);
    }

    public int Count(EntityRef owner, ItemId item) => _slots.TryGetValue(owner.Id, out var l) ? l.Where(s => s.Item == item).Sum(s => s.Count) : 0;

    public Task<CostReceipt> Take(ItemSelection selection, int count, Guid requestId)
    {
        var list = _slots.GetValueOrDefault(selection.InventoryOwner.Id) ?? [];
        var i = list.FindIndex(s => s.Slot == selection.Slot && s.Item == selection.ExpectedItem && s.Count >= count);
        if (i < 0)
            return Task.FromResult(new CostReceipt(selection.ExpectedItem, count, 0, false)
            {
                RequestId = requestId, Selection = selection, Kind = CostKind.StackTake, Refusal = CostRefusal.StaleSelection, Reason = "the slot does not hold it now",
            });
        list[i] = list[i] with { Count = list[i].Count - count };
        return Task.FromResult(new CostReceipt(selection.ExpectedItem, count, count, true)
        {
            RequestId = requestId, Selection = selection, Kind = CostKind.StackTake, Disposition = CostDisposition.Exact,
        });
    }

    public Task<CostReceipt> Wear(ItemSelection selection, int wear, Guid requestId, bool allowBreak) =>
        Task.FromResult(new CostReceipt(selection.ExpectedItem, wear, 0, false)
        {
            RequestId = requestId, Selection = selection, Kind = CostKind.GearWear, Refusal = CostRefusal.NotRun, Reason = "the kit has no gear durability (a tool step is not faked)",
        });

    public Task<CostReceipt> Refund(CostReceipt paid, Guid requestId)
    {
        if (paid.Selection is { } s && _slots.TryGetValue(s.InventoryOwner.Id, out var list) && list.FindIndex(x => x.Slot == s.Slot) is var i and >= 0)
            list[i] = list[i] with { Count = list[i].Count + paid.Taken };
        return Task.FromResult(new CostReceipt(paid.Item, paid.Taken, paid.Taken, true) { RequestId = requestId, RefundOf = paid.RequestId, Kind = CostKind.StackRefund });
    }

    public IReadOnlyList<InventorySlot> Slots(EntityRef owner) => _slots.GetValueOrDefault(owner.Id) ?? [];
}
