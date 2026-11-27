// SPDX-License-Identifier: MIT
// Fakes for the taming engine: a world with a clock of its own, item costs whose receipts a test holds or releases, and a
// binder whose answers a test sets.
using Kiln;
using Xunit;

namespace Kiln.TamingTests;

internal sealed class FakeTamingWorld : ITamingWorld
{
    public double Now { get; set; } = 100;
    public ulong WorldEpoch { get; set; } = 1;
    public readonly Dictionary<PlayerRef, TamingOwner> Owners = new();
    public readonly Dictionary<EntityId, TamingTarget> Targets = new();

    public TamingOwner? Owner(PlayerRef player, out string? reason)
    {
        reason = Owners.ContainsKey(player) ? null : "OwnerOffline: the player is not in game";
        return Owners.TryGetValue(player, out var o) ? o : null;
    }

    public TamingTarget? Target(EntityRef target) => Targets.TryGetValue(target.Id, out var t) ? t : null;
}

internal sealed class FakeTamingCosts : ITamingCosts
{
    public readonly List<(string Kind, ItemSelection? Selection, int Amount, Guid Request)> Calls = new();
    public readonly List<(CostReceipt Paid, Guid Request)> Refunds = new();
    public TaskCompletionSource<CostReceipt>? Hold;
    public Func<ItemSelection, int, Guid, CostReceipt>? Answer;
    public List<InventorySlot> Inventory = new();

    public Exception? Throw;

    private Task<CostReceipt> Pay(string kind, ItemSelection s, int amount, Guid id)
    {
        if (Throw is { } t) throw t;
        Calls.Add((kind, s, amount, id));
        if (Hold is { } h)
        {
            Hold = null;
            return h.Task;
        }
        var r = Answer?.Invoke(s, amount, id) ?? new CostReceipt(s.ExpectedItem, amount, amount, true)
        {
            RequestId = id, Selection = s, Kind = kind == "take" ? CostKind.StackTake : CostKind.GearWear, Disposition = CostDisposition.Exact,
        };
        return Task.FromResult(r);
    }

    public Task<CostReceipt> Take(ItemSelection selection, int count, Guid requestId) => Pay("take", selection, count, requestId);
    public Task<CostReceipt> Wear(ItemSelection selection, int wear, Guid requestId, bool allowBreak) => Pay("wear", selection, wear, requestId);

    public Exception? RefundThrow;

    public Task<CostReceipt> Refund(CostReceipt paid, Guid requestId)
    {
        if (RefundThrow is { } t) throw t;
        Refunds.Add((paid, requestId));
        return Task.FromResult(new CostReceipt(paid.Item, paid.Taken, paid.Taken, true) { RequestId = requestId, RefundOf = paid.RequestId, Kind = CostKind.StackRefund });
    }

    public IReadOnlyList<InventorySlot> Slots(EntityRef owner) => Inventory;
}

internal sealed class FakeBinder : ITamingBinder
{
    public string? ReserveRefusal;
    public (string Reason, bool Transient)? PreflightRefusal;
    public TamingBindResult Result = new(true, null, null, false);
    public TaskCompletionSource<TamingBindResult>? Hold;
    public readonly List<string> Calls = new();

    public string? Reserve(TamingBindRequest request)
    {
        Calls.Add("reserve");
        return ReserveRefusal;
    }

    public (string Reason, bool Transient)? Preflight(TamingBindRequest request)
    {
        Calls.Add("preflight");
        return PreflightRefusal;
    }

    public Task<TamingBindResult> Bind(TamingBindRequest request)
    {
        Calls.Add("bind");
        if (Hold is { } h)
        {
            Hold = null;
            return h.Task;
        }
        return Task.FromResult(Result);
    }

    public void Committed(TamingBindRequest request, TamingBindResult result) => Calls.Add("committed");
    public void Release(TamingBindRequest request) => Calls.Add("release");
}

/// <summary>An engine with one player in range of one wolf, and the events it posted.</summary>
internal sealed class TamingFixture
{
    public readonly FakeTamingWorld World = new();
    public readonly FakeTamingCosts Costs = new();
    public readonly FakeBinder Binder = new();
    public readonly TamingEngine Engine;
    public readonly List<KilnEvent> Events = new();
    public Func<CancelableEvent, bool> Refuse = _ => false;

    public static readonly ItemId Bait = new(0xb59dfc2c), Whistle = new(0x1234), Other = new(0x99);
    public readonly PlayerRef A = new(76561198000000001, Guid.NewGuid(), new EntityRef(new EntityId(10), 1));
    public readonly PlayerRef B = new(76561198000000002, Guid.NewGuid(), new EntityRef(new EntityId(11), 1));
    public readonly EntityRef Wolf = new(new EntityId(500), 7);
    public readonly EntityRef Dog = new(new EntityId(501), 8);

    public TamingFixture()
    {
        Engine = new TamingEngine(World, Costs)
        {
            Binder = Binder,
            Ask = ev =>
            {
                if (Refuse(ev)) ev.Cancel();
                Events.Add(ev);
                return ev;
            },
            Tell = ev => Events.Add(ev),
        };
        World.Owners[A] = new TamingOwner(A, A.Entity, 5, new WorldPosition(0, 0, 0));
        World.Owners[B] = new TamingOwner(B, B.Entity, 6, new WorldPosition(1, 0, 0));
        World.Targets[Wolf.Id] = new TamingTarget(true, false, new WorldPosition(2, 0, 0), 1.0, "game:enemy_wildbeast_wolf", 0, false);
        World.Targets[Dog.Id] = new TamingTarget(true, false, new WorldPosition(0, 0, 2), 1.0, "game:animal_wildlife_dog_05_white_ag2", 0, true);
    }

    public static TamingDefinition Def(params TamingStep[] steps) => new()
    {
        Id = "mod-a:wolf", Steps = steps, Creatures = ["game:enemy_wildbeast_wolf", "game:animal_wildlife_dog_*"],
    };

    public TamingFixture With(TamingDefinition d)
    {
        Engine.Define(d, "mod-a");
        return this;
    }

    public ItemSelection Stack(ItemId item, int count = 5, int slot = 1, PlayerRef? owner = null) =>
        new((owner ?? A).Entity, new SlotId((ulong)slot), item, count, ItemInstanceRef.None);

    public ItemSelection Gear(ItemId item, int slot = 2, uint entity = 900) =>
        new(A.Entity, new SlotId((ulong)slot), item, 1, new ItemInstanceRef(new EntityRef(new EntityId(entity), 3), item));

    public TamingResult Begin(EntityRef? target = null, PlayerRef? who = null, Guid? id = null, string def = "mod-a:wolf") =>
        Engine.Begin(who ?? A, target ?? Wolf, def, id ?? Guid.NewGuid());

    public static TamingToken Token(TamingProgress p, Guid? id = null) => new(id ?? Guid.NewGuid(), p.StepChallenge, p.Revision, p.CompletedSteps);

    public TamingResult Advance(TamingProgress p, ItemSelection? s = null, Guid? id = null, PlayerRef? who = null) =>
        Done(Engine.Advance(who ?? A, p.Id, Token(p, id), s));

    public static TamingResult Done(Task<TamingResult> t)
    {
        Assert.True(t.IsCompleted, "the call did not complete");
        return t.Result;
    }

    public void Tick(int n = 1)
    {
        for (var i = 0; i < n; i++) Engine.Tick();
    }

    public void Pass(double seconds)
    {
        World.Now += seconds;
        Engine.Tick();
    }

    public List<string> Names() => Events.Select(e => e.GetType().Name.Replace("Taming", "").Replace("Event", "")).ToList();
}
