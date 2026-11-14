// SPDX-License-Identifier: MIT
// Items.SetToolWearPerDebit / ToolWearStatus offline, over a made-up kiln.toolwear table that keeps the owners' layers as
// the native one does (native/services/items/tests/toolwear_table_test.cpp tests that one): the one error rule, one rule
// event per change of what is in effect and none for a replay, whose rule a dispose may take, and durability events
// built from the captured item entity and stamp. What the game does is the lab test mods/tests/toolwear-test.
using Kiln;
using Xunit;

namespace Kiln.ItemsTests;

public sealed unsafe class ToolWearTests : IDisposable
{
    private const uint Pickaxe = 0x2525bb44, Torch = 0x70707070, Unknown = 0x999;

    private sealed class Backend : IKilnBackend
    {
        public bool Tick = true;
        public string Version => "test";
        public bool IsTickThread => Tick;
        public bool HasFeature(string name) => false;
        public nint Service(string name, uint minVersion) => 0;
        public void Log(ulong owner, LogLevel level, string text) { }
    }

    private sealed class NoTick : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class FakeTable : Items.IToolWearTable
    {
        public readonly List<(ulong Handle, ulong Owner, string Mod, uint Item, uint Cost)> Layers = new();
        public readonly Queue<ToolWearNative.Change> Queue = new();
        public ulong Generation, NextHandle = 1;
        public bool World = true, Catalog = true, Off;
        public int Defines, Removes;

        private (ulong Handle, ulong Owner, string Mod, uint Item, uint Cost)? Winner(uint item) =>
            Layers.FindLastIndex(l => l.Item == item) is >= 0 and var i ? Layers[i] : null;

        private void Note((ulong Handle, ulong Owner, string Mod, uint Item, uint Cost)? before, uint item)
        {
            var now = Winner(item);
            if (before?.Handle != now?.Handle || before?.Cost != now?.Cost) Generation++;
        }

        public int Define(ulong owner, string modId, uint item, uint perDebit, out ulong handle)
        {
            Defines++;
            handle = 0;
            if (Off) return ToolWearNative.Unavailable;
            if (!World) return ToolWearNative.NoAuthoritativeWorld;
            if (!Catalog) return ToolWearNative.Pending;
            if (item == Unknown) return ToolWearNative.NotFound;
            if (item == Torch) return ToolWearNative.UnsupportedTool;
            var before = Winner(item);
            var at = Layers.FindIndex(l => l.Owner == owner && l.Item == item);
            handle = at >= 0 ? Layers[at].Handle : NextHandle++;
            if (at >= 0) Layers.RemoveAt(at);
            Layers.Add((handle, owner, modId, item, perDebit));
            Note(before, item);
            return ToolWearNative.Ok;
        }

        public int Remove(ulong owner, ulong handle)
        {
            Removes++;
            var at = Layers.FindIndex(l => l.Handle == handle);
            if (at < 0) return ToolWearNative.NotFound;
            if (Layers[at].Owner != owner) return ToolWearNative.NotOwner;
            var item = Layers[at].Item;
            var before = Winner(item);
            Layers.RemoveAt(at);
            Note(before, item);
            return ToolWearNative.Ok;
        }

        public int Changes(Span<ToolWearNative.Change> into)
        {
            var n = 0;
            while (n < into.Length && Queue.Count > 0) into[n++] = Queue.Dequeue();
            return n;
        }

        public bool Status(uint item, out ToolWearNative.State state)
        {
            var s = new ToolWearNative.State { Generation = Generation, DroppedEvents = 5 };
            var reason = !Catalog ? "PendingContent" : item == Torch ? "UnsupportedTool: Torch is not a tool with an item entity" : item == Unknown ? "UnknownItem" : "";
            s.Ok = reason.Length == 0 ? 1u : 0u;
            s.Pending = Catalog ? 0u : 1u;
            Put(s.Reason, 192, reason);
            if (Winner(item) is { } w)
            {
                s.HasRule = 1;
                s.PerDebit = w.Cost;
                Put(s.OwnerModId, 128, w.Mod);
            }
            state = s;
            return true;
        }

        private static void Put(byte* to, int cap, string text)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            for (var i = 0; i < cap; i++) to[i] = i < bytes.Length && i < cap - 1 ? bytes[i] : (byte)0;
        }
    }

    private readonly FakeTable _table = new();
    private readonly List<KilnEvent> _events = new();
    private readonly Backend _backend = new();
    private readonly IKilnBackend _was = Kiln.Backend.Current;
    private readonly ModContext? _caller = HostAccess.Current;
    private readonly ModContext _a = HostAccess.CreateContext(new ModInfo("mod-a", "mod-a", "1", ""), 11, "");
    private readonly ModContext _b = HostAccess.CreateContext(new ModInfo("mod-b", "mod-b", "1", ""), 22, "");
    private int _ticks;

    public ToolWearTests()
    {
        Kiln.Backend.Current = _backend;
        Items.ToolWear.Reset();
        Items.ToolWear.Table = _table;
        Items.ToolWear.Available = () => true;
        Items.ToolWear.Sink = _events.Add;
        Items.ToolWear.AddTick = (_, _) => { _ticks++; return new NoTick(); };
    }

    public void Dispose()
    {
        Items.ToolWear.Reset();
        Items.ToolWear.Available = () => Capabilities.Is(Items.ToolWear.Capability);
        Items.ToolWear.Sink = ev => Events.Enqueue(ev);
        Items.ToolWear.AddTick = (name, step) => ServiceTickAccess.Add(name, step);
        HostAccess.Current = _caller;
        Kiln.Backend.Current = _was;
    }

    private ToolWearResult Set(ModContext mod, uint item, int cost)
    {
        HostAccess.Current = mod;
        return Items.SetToolWearPerDebit(new ItemId(item), cost);
    }

    private ToolWearRuleChangedEvent[] RuleEvents => _events.OfType<ToolWearRuleChangedEvent>().ToArray();

    [Fact]
    public void A_rule_is_set_replaced_revealed_and_taken_away_with_one_event_each()
    {
        var a = Set(_a, Pickaxe, 7);
        Assert.True(a.Ok && a.Reason == "" && a.Rule is { } && a.Rule!.Item == new ItemId(Pickaxe));
        var st = Items.ToolWearStatus(new ItemId(Pickaxe));
        Assert.Equal((true, 7, "mod-a", 1ul, 5ul, false), (st.Ok, st.PerDebit, st.OwnerModId, st.Generation, st.DroppedEvents, st.Pending));
        var e = Assert.Single(RuleEvents);
        Assert.Equal((new ItemId(Pickaxe), (int?)null, (int?)7, (string?)null, "mod-a", 1ul), (e.Item, e.Before, e.After, e.BeforeModId, e.AfterModId, e.Generation));

        // the same again: the same rule object, no event
        var again = Set(_a, Pickaxe, 7);
        Assert.Same(a.Rule, again.Rule);
        Assert.Single(RuleEvents);

        // another cost of the same mod: replaced, one event
        Assert.Same(a.Rule, Set(_a, Pickaxe, 9).Rule);
        Assert.Equal((7, 9, "mod-a", "mod-a"), (RuleEvents[1].Before, RuleEvents[1].After, RuleEvents[1].BeforeModId, RuleEvents[1].AfterModId));

        // another mod with the same cost: whose rule it is changed, so one event
        var b = Set(_b, Pickaxe, 9);
        Assert.NotSame(a.Rule, b.Rule);
        Assert.Equal((9, 9, "mod-a", "mod-b"), (RuleEvents[2].Before, RuleEvents[2].After, RuleEvents[2].BeforeModId, RuleEvents[2].AfterModId));

        // the first mod's rule is not in effect: taking it away changes nothing and says nothing
        HostAccess.Current = _a;
        a.Rule!.Dispose();
        Assert.Equal(3, RuleEvents.Length);
        a.Rule.Dispose();                                   // twice is once
        Assert.Equal(1, _table.Removes);

        // the winner goes: the game's own debit again
        HostAccess.Current = _b;
        b.Rule!.Dispose();
        Assert.Equal((9, (int?)null, "mod-b", (string?)null), (RuleEvents[3].Before, RuleEvents[3].After, RuleEvents[3].BeforeModId, RuleEvents[3].AfterModId));
        Assert.Null(Items.ToolWearStatus(new ItemId(Pickaxe)).PerDebit);
        Assert.Equal(4, RuleEvents.Length);
    }

    [Fact]
    public void Taking_the_winner_away_reveals_the_rule_before_it()
    {
        var a = Set(_a, Pickaxe, 3);
        var b = Set(_b, Pickaxe, 8);
        HostAccess.Current = _b;
        b.Rule!.Dispose();
        var e = RuleEvents[^1];
        Assert.Equal((8, 3, "mod-b", "mod-a"), (e.Before, e.After, e.BeforeModId, e.AfterModId));
        Assert.Equal(3, Items.ToolWearStatus(new ItemId(Pickaxe)).PerDebit);
        Assert.False(a.Rule!.Gone);
    }

    [Fact]
    public void Only_the_mod_that_set_a_rule_disposes_it_and_only_on_the_tick_thread()
    {
        var a = Set(_a, Pickaxe, 7);
        HostAccess.Current = _b;
        Assert.Contains("mod-a", Assert.Throws<InvalidOperationException>(() => a.Rule!.Dispose()).Message);
        HostAccess.Current = _a;
        _backend.Tick = false;
        Assert.Throws<InvalidOperationException>(() => a.Rule!.Dispose());
        Assert.Throws<InvalidOperationException>(() => Items.SetToolWearPerDebit(new ItemId(Pickaxe), 1));
        Assert.Equal(7, Items.ToolWearStatus(new ItemId(Pickaxe)).PerDebit);   // the status is any thread
        _backend.Tick = true;
        Assert.Equal(0, _table.Removes);
        Assert.Single(RuleEvents);
    }

    [Fact]
    public void A_mod_that_retires_loses_its_rule_and_the_event_says_so()
    {
        Set(_a, Pickaxe, 7);
        _a.Retire();
        Assert.Null(Items.ToolWearStatus(new ItemId(Pickaxe)).PerDebit);
        Assert.Equal((7, (int?)null), (RuleEvents[^1].Before, RuleEvents[^1].After));
        HostAccess.Current = _a;
        Assert.Throws<InvalidOperationException>(() => Items.SetToolWearPerDebit(new ItemId(Pickaxe), 1));
    }

    [Fact]
    public void The_one_error_rule()
    {
        // a bad argument throws, and nothing is asked of the table
        Assert.Throws<ArgumentOutOfRangeException>(() => Set(_a, Pickaxe, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Set(_a, Pickaxe, 32768));
        Assert.Throws<ArgumentException>(() => Set(_a, 0, 1));
        Assert.Throws<ArgumentException>(() => Items.ToolWearStatus(ItemId.None));
        Assert.Equal(0, _table.Defines);
        Assert.True(Set(_a, Pickaxe, 0).Ok && Set(_a, Pickaxe, 32767).Ok);

        // what the game cannot do answers Ok = false with the reason, and raises nothing
        var n = _events.Count;
        var torch = Set(_a, Torch, 7);
        Assert.False(torch.Ok);
        Assert.StartsWith("UnsupportedTool", torch.Reason);
        Assert.Null(torch.Rule);
        Assert.Equal((false, "UnknownItem"), (Set(_a, Unknown, 7).Ok, Set(_a, Unknown, 7).Reason));
        _table.World = false;
        Assert.Equal((false, "NoAuthoritativeWorld"), (Set(_a, Pickaxe, 7).Ok, Set(_a, Pickaxe, 7).Reason));
        _table.World = true;
        _table.Catalog = false;
        Assert.Equal("PendingContent", Set(_a, Pickaxe, 7).Reason);
        var st = Items.ToolWearStatus(new ItemId(Pickaxe));
        Assert.Equal((false, true, "PendingContent"), (st.Ok, st.Pending, st.Reason));
        _table.Catalog = true;
        Assert.Equal(n, _events.Count);
        Assert.StartsWith("UnsupportedTool", Items.ToolWearStatus(new ItemId(Torch)).Reason);

        // the capability off throws, naming it
        _table.Off = true;
        Assert.Contains("items.tool_wear_debit", Assert.Throws<KilnUnavailableException>(() => Set(_a, Pickaxe, 7)).Message);
        _table.Off = false;
        Items.ToolWear.Available = () => false;
        Assert.Contains("items.tool_wear_debit", Assert.Throws<KilnUnavailableException>(() => Set(_a, Pickaxe, 7)).Message);
        Assert.Contains("items.tool_wear_debit", Assert.Throws<KilnUnavailableException>(() => Items.ToolWearStatus(new ItemId(Pickaxe))).Message);
        Items.ToolWear.Available = () => true;
        Items.ToolWear.Table = null;
        Assert.Throws<KilnUnavailableException>(() => Set(_a, Pickaxe, 7));
    }

    [Fact]
    public void A_durability_change_becomes_one_event_with_the_captured_item_entity()
    {
        Set(_a, Pickaxe, 7);
        Assert.Equal(1, _ticks);                            // the drain was started by the first rule
        Set(_a, Pickaxe, 8);
        Assert.Equal(1, _ticks);
        _events.Clear();
        _table.Queue.Enqueue(new ToolWearNative.Change
        {
            Source = 2, Step = 90, Actor = 2022, ItemPide = 2032, ItemId = Pickaxe, InstanceStamp = 500, Before = 180, After = 172, Maximum = 180,
            OriginalDelta = -1, RequestedDelta = -8,
        });
        _table.Queue.Enqueue(new ToolWearNative.Change { Source = 1, Step = 91, Actor = 2022, ItemPide = 2032, ItemId = Pickaxe, InstanceStamp = 500, Before = 172, After = 164, Maximum = 180, OriginalDelta = -1, RequestedDelta = -8 });
        // a record that changed nothing is not an event
        _table.Queue.Enqueue(new ToolWearNative.Change { Source = 1, Step = 92, ItemPide = 2032, ItemId = Pickaxe, InstanceStamp = 500, Before = 164, After = 164 });
        // a record without a stamp names no item entity: the event says none rather than a made-up one
        _table.Queue.Enqueue(new ToolWearNative.Change { Source = 1, Step = 93, ItemPide = 2032, ItemId = Pickaxe, InstanceStamp = 0, Before = 164, After = 160 });
        Items.ToolWear.Drain(100);
        var got = _events.OfType<ToolDurabilityChangedEvent>().ToArray();
        Assert.Equal(3, got.Length);
        var e = got[0];
        Assert.Equal((ToolWearSource.ProjectileSpawn, 90ul, new EntityId(2022), 180, 172, 180, -1, -8), (e.Source, e.Step, e.Actor, e.Before, e.After, e.Maximum, e.OriginalDelta, e.RequestedDelta));
        Assert.Equal((new EntityId(2032), new ItemId(Pickaxe)), (e.Item.Entity.Id, e.Item.Item));
        Assert.Equal(got[0].Item, got[1].Item);             // the same item entity, by its stamp
        Assert.Equal(ToolWearSource.WeaponHit, got[1].Source);
        Assert.Equal(ItemInstanceRef.None, got[2].Item);
        // more than one batch drains whole
        for (var i = 0; i < 150; i++) _table.Queue.Enqueue(new ToolWearNative.Change { Source = 1, ItemPide = 2032, ItemId = Pickaxe, InstanceStamp = 500, Before = 100 - i, After = 99 - i });
        _events.Clear();
        Items.ToolWear.Drain(101);
        Assert.Equal(150, _events.Count);
    }

    [Fact]
    public void The_native_records_have_the_sizes_of_the_header()
    {
        Assert.Equal(88, sizeof(ToolWearNative.Change));
        Assert.Equal(360, sizeof(ToolWearNative.State));
        Assert.Equal(8 + 4 * 8, sizeof(ToolWearNative.Table));
    }
}
