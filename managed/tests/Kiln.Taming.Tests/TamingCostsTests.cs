// SPDX-License-Identifier: MIT
// Kiln.Taming's costs in a game: GameTamingCosts over the real ItemCosts (Kiln.Items) on a made-up kiln.item_costs table,
// so what the engine gets is what ItemCosts hands out. Review of dev.32 TAME.
using Kiln;
using Xunit;

namespace Kiln.TamingTests;

public sealed unsafe class TamingCostsTests : IDisposable
{
    private sealed class Backend : IKilnBackend
    {
        public string Version => "test";
        public bool IsTickThread => true;
        public bool HasFeature(string name) => false;
        public nint Service(string name, uint minVersion) => 0;
        public void Log(ulong owner, LogLevel level, string text) { }
    }

    private sealed class Table : ItemCosts.ITable
    {
        public readonly Dictionary<ulong, ItemCostsNative.CostResult> Results = new();
        public readonly List<ItemCostsNative.SelectedCost> Submitted = new();
        public ulong Next = 100;

        public int Submit(in ItemCostsNative.SelectedCost c, out ulong ticket)
        {
            ticket = Next++;
            Submitted.Add(c);
            Results[ticket] = new ItemCostsNative.CostResult { Size = (uint)sizeof(ItemCostsNative.CostResult), State = ItemCostsNative.Queued, Ticket = ticket, Kind = c.Kind, Item = c.Item, WorldEpoch = c.WorldEpoch };
            return ItemCostsNative.Ok;
        }

        public int Peek(ulong ticket, out ItemCostsNative.CostResult r) => Results.TryGetValue(ticket, out r) ? ItemCostsNative.Ok : ItemCostsNative.NotFound;

        public int Acknowledge(ulong ticket) => Results.Remove(ticket) ? ItemCostsNative.Ok : ItemCostsNative.NotFound;

        public bool Status(out ItemCostsNative.Status s)
        {
            s = new ItemCostsNative.Status { Size = (uint)sizeof(ItemCostsNative.Status), Stacks = 1, Wear = 1, WorldEpoch = 1 };
            return true;
        }

        public void Commit(ulong ticket, uint taken)
        {
            var r = Results[ticket];
            r.State = ItemCostsNative.Committed;
            r.QuantityTaken = taken;
            Results[ticket] = r;
        }
    }

    private readonly Table _table = new();
    private readonly List<KilnEvent> _events = new();
    private readonly IKilnBackend _was = Kiln.Backend.Current;
    private readonly ModContext? _caller = HostAccess.Current;

    public TamingCostsTests()
    {
        Kiln.Backend.Current = new Backend();
        ItemCosts.Runtime.Reset();
        ItemCosts.Runtime.Table = _table;
        ItemCosts.Runtime.StacksOn = () => true;
        ItemCosts.Runtime.WearOn = () => true;
        ItemCosts.Runtime.Sink = _events.Add;
        ItemCosts.Runtime.SessionOf = _ => (true, true, 5ul);
        ItemCosts.Runtime.Alive = _ => true;
        ItemCosts.Runtime.AddTick = (_, _) => new NoTick();
    }

    private sealed class NoTick : IDisposable
    {
        public void Dispose() { }
    }

    public void Dispose()
    {
        ItemCosts.Runtime.Reset();
        HostAccess.Current = _caller;
        Kiln.Backend.Current = _was;
    }

    // ---- finding: a step's cost never settled once the mod that advanced the attempt unloaded ---------------------------
    // ItemCosts leaves a cost's task incomplete when the mod that asked unloads (its continuations must not run). The engine
    // submitted its costs as the calling mod, so a hot reload of that mod left the attempt CostPending for ever: the target
    // stayed claimed, a cancel, a failure and the timeout all waited for the receipt, and an escrow never settled.

    [Fact]
    public void An_attempt_s_cost_settles_after_the_mod_that_advanced_it_unloaded()
    {
        var f = new TamingFixture();
        var engine = new TamingEngine(f.World, new GameTamingCosts()) { Binder = f.Binder, Ask = ev => ev, Tell = _events.Add };
        engine.Define(TamingFixture.Def(new FeedStep(TamingFixture.Bait, 2), new WeakenStep(1)), "mod-a");
        var mod = HostAccess.CreateContext(new ModInfo("mod-a", "mod-a", "1", ""), 11, "");
        HostAccess.Current = mod;
        var p = engine.Begin(f.A, f.Wolf, "mod-a:wolf", Guid.NewGuid()).Progress!;
        var step = engine.Advance(f.A, p.Id, TamingFixture.Token(p), f.Stack(TamingFixture.Bait));
        HostAccess.Current = null;
        mod.Retire();                                   // a hot reload of the mod that advanced it
        Assert.Single(_table.Submitted);
        _table.Commit(100, 2);
        ItemCosts.Runtime.Pump();
        engine.Tick();
        Assert.True(step.IsCompleted, "the step never settled");
        Assert.True(step.Result.Ok, step.Result.Reason);
        Assert.Equal((TamingState.Active, 1), (engine.Inspect(p.Id)!.State, engine.Inspect(p.Id)!.CompletedSteps));
        Assert.Equal("mod-a", Assert.Single(_events.OfType<ItemCostSettledEvent>()).Origin);   // still the mod's debit
    }
}
