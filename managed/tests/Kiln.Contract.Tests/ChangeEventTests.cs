// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>A service's change events (the contract's "an event for every write"): a handler that writes again must not
/// loop, a write that changed nothing posts nothing (docs/kiln/modding/contract.md, "Change events";
/// managed/Kiln.Api/Services/ChangeNotices.cs).</summary>
public sealed class ChangeNoticesTests : ContractTest
{
    private sealed class Changed(int key) : KilnEvent
    {
        public int Key { get; } = key;
    }

    private readonly ChangeNotices<Changed, int> _notices = new("test");

    // a write of the service under test: the write "took", its event goes through the notices
    private void Write(int key) => _notices.Post(key, new Changed(key));

    [Fact]
    public void A_write_posts_its_event_at_once_on_the_tick_thread()
    {
        var seen = new List<int>();
        using var sub = Events.On<Changed>(e => seen.Add(e.Key));
        Write(1);
        Write(2);
        Assert.Equal([1, 2], seen);
        Assert.Equal(0, _notices.Waiting);
    }

    [Fact]
    public void A_catch_all_handler_that_writes_again_does_not_loop_and_is_never_faulted()
    {
        var calls = 0;
        var others = 0;
        // a mod built before the event existed: it writes on every event it sees
        using var sub = Events.On<KilnEvent>(e =>
        {
            if (e is Changed) calls++;
            else others++;  // another event (a row that changed): the handler writes on it too
            Write(7);
        });
        Write(7);
        Assert.Equal(1, calls);              // the event, once; the handler's own write waits for the next step
        Assert.Equal(1, _notices.Waiting);
        Step(10);
        Assert.Equal(11 + others, calls);    // one call a step (and one per other event), no recursion
        Assert.True(sub.Active);
        Assert.Equal(0, sub.Stats.Faults);
        Assert.DoesNotContain(Backend.Lines, l => l.Contains("threw", StringComparison.Ordinal));
    }

    [Fact]
    public void Writes_of_one_key_made_during_a_dispatch_are_one_event_at_the_next_step()
    {
        var seen = new List<int>();
        var first = true;
        using var sub = Events.On<Changed>(e =>
        {
            seen.Add(e.Key);
            if (!first) return;
            first = false;
            Write(5);
            Write(5);
            Write(6);
        });
        Write(1);
        Assert.Equal([1], seen);
        Assert.Equal(2, _notices.Waiting);
        Step();
        Assert.Equal([1, 5, 6], seen);
        Assert.Equal(0, _notices.Waiting);
    }

    [Fact]
    public void A_handler_that_throws_leaves_no_guard_behind()
    {
        var seen = 0;
        using var bad = Events.On<Changed>(_ => throw new InvalidOperationException("boom"));
        using var good = Events.On<Changed>(_ => seen++);
        Write(1);
        Write(2);
        Assert.Equal(2, seen);               // both inline: the throw did not leave the notices "dispatching"
        Assert.Equal(0, _notices.Waiting);
    }

    [Fact]
    public void Deep_inside_other_handlers_the_event_waits_instead_of_failing_the_write()
    {
        var seen = 0;
        using var count = Events.On<Changed>(_ => seen++);
        // a chain of other events as deep as the bus allows; the write happens at the bottom of it
        var depth = 0;
        using var chain = Events.On<Probe>(_ =>
        {
            if (++depth < Events.MaxDepth) Events.Post(new Probe());
            else Write(9);
        });
        Events.Post(new Probe());
        Assert.Equal(0, seen);
        Assert.Equal(1, _notices.Waiting);
        // the refused post released the guard: an ordinary write goes out at once, nothing more waits
        Write(10);
        Assert.Equal(1, seen);
        Assert.Equal(1, _notices.Waiting);
        Step();
        Assert.Equal(2, seen);
        Assert.Equal(0, _notices.Waiting);
    }

    private sealed class Probe : KilnEvent;

    [Fact]
    public void A_write_during_a_dispatch_waits_for_the_next_step_whatever_order_the_service_handlers_have()
    {
        var seen = new List<(int Key, ulong Step)>();
        // a service handler added before the notices' first wait (a pump): it posts an event whose handler writes
        using var pump = ServiceTick.Add("pump", (in TickInfo t) =>
        {
            if (t.Step == 2) Events.Post(new Probe());
        });
        using var probe = Events.On<Probe>(_ => Write(1));
        using var sub = Events.On<Changed>(e =>
        {
            seen.Add((e.Key, ServiceTick.Step));
            if (e.Key is 1 or 5) Write(e.Key + 1);  // during the dispatch of 1 (or 5): waits
        });
        Write(5);   // before the first step: 6 waits, so the notices are flushed on the service tick from now on
        Step();     // step 1: 6 goes out
        Step();     // step 2: the pump's event writes 1; its handler writes 2, which waits
        Assert.Equal([(5, 0UL), (6, 1UL), (1, 2UL)], seen);
        Assert.Equal(1, _notices.Waiting);
        Step();
        Assert.Equal([(5, 0UL), (6, 1UL), (1, 2UL), (2, 3UL)], seen);
    }

    [Fact]
    public void No_waiting_event_is_dropped_for_want_of_room()
    {
        var seen = new HashSet<int>();
        var first = true;
        using var sub = Events.On<Changed>(e =>
        {
            seen.Add(e.Key);
            if (!first) return;
            first = false;
            for (var k = 1; k <= 5000; k++) Write(k);  // 5000 values changed during one dispatch: every write took
        });
        Write(0);
        Assert.Equal(5000, _notices.Waiting);
        Step();
        Assert.Equal(5001, seen.Count);
        Assert.Equal(0, _notices.Waiting);
        Assert.DoesNotContain(Backend.Lines, l => l.Contains("dropped", StringComparison.Ordinal));
    }

    [Fact]
    public void The_last_round_before_mods_unload_sends_what_waits_and_announces_no_further_write()
    {
        var seen = new List<int>();
        using var sub = Events.On<Changed>(e =>
        {
            seen.Add(e.Key);
            if (e.Key < 3) Write(e.Key + 1);  // each event's handler writes again
        });
        Write(1);
        Assert.Equal([1], seen);
        Assert.Equal(1, ChangeNoticeQueue.FlushFinal("test"));  // 2 goes out; 3, written by its handler, is not posted
        Assert.Equal([1, 2], seen);
        Assert.Equal(0, _notices.Waiting);
        Assert.Contains(Backend.Lines, l => l.Contains("1 change event(s)", StringComparison.Ordinal));
        Step();
        Assert.Equal([1, 2], seen);
    }

    private sealed class Other(int key) : KilnEvent
    {
        public int Key { get; } = key;
    }

    private sealed class Valued(int key, int value) : KilnEvent
    {
        public int Key { get; } = key;
        public int Value { get; } = value;
    }

    [Fact]
    public void What_waited_goes_out_in_one_step_and_what_its_handlers_write_waits_whichever_queue_comes_first()
    {
        var others = new ChangeNotices<Other, int>("test-other");
        var seen = new List<(string, int, ulong)>();
        using var a = Events.On<Changed>(e =>
        {
            seen.Add(("a", e.Key, ServiceTick.Step));
            if (e.Key == 1) Write(2);                          // waits in the first queue
            if (e.Key == 2) others.Post(10, new Other(10));    // the other queue, not dispatching: inline
        });
        using var b = Events.On<Other>(e =>
        {
            seen.Add(("b", e.Key, ServiceTick.Step));
            if (e.Key is 10 or 20) others.Post(e.Key + 1, new Other(e.Key + 1));  // waits in the second queue
        });
        Write(1);                        // the first queue waits first: it is first on the list
        others.Post(20, new Other(20));  // the second queue waits too, after it
        Step();  // step 1: 21 and 2 go out; 2's handler posts 10 inline, whose handler writes 11: 11 waits for step 2
        Assert.Contains(("b", 21, 1UL), seen);
        Assert.Contains(("a", 2, 1UL), seen);
        Assert.Contains(("b", 10, 1UL), seen);
        Assert.DoesNotContain(seen, x => x.Item2 == 11 && x.Item3 == 1);
        Step();
        Assert.Contains(("b", 11, 2UL), seen);
    }

    [Fact]
    public void A_newer_write_of_a_key_replaces_its_older_waiting_event()
    {
        var notices = new ChangeNotices<Valued, int>("test-valued");
        var seen = new List<int>();
        using var sub = Events.On<Valued>(e =>
        {
            seen.Add(e.Value);
            if (e.Value == 0) notices.Post(5, new Valued(5, 1));  // during the dispatch: waits
        });
        notices.Post(5, new Valued(5, 0));
        notices.Post(5, new Valued(5, 2));  // after it, inline: the value now is 2
        Step();
        Assert.Equal([0, 2], seen);         // 1, older than what was announced last, never comes after it
        Assert.Equal(0, notices.Waiting);
    }

    private sealed class Serials : IEntitySerialSource
    {
        public readonly Dictionary<EntityId, ulong> Alive = new();
        public bool TryGetSerial(EntityId entity, out ulong serial) => Alive.TryGetValue(entity, out serial);
    }

    [Fact]
    public void A_waiting_event_about_an_entity_goes_out_only_while_that_entity_is_there()
    {
        var serials = new Serials();
        var was = IdentitySources.Entities;
        IdentitySources.Entities = serials;
        try
        {
            serials.Alive[new EntityId(10)] = 7;   // will be destroyed, its id given to another entity
            serials.Alive[new EntityId(11)] = 3;   // stays
            serials.Alive[new EntityId(12)] = 0;   // stays, has no stamp
            serials.Alive[new EntityId(13)] = 4;   // will be destroyed
            var seen = new List<int>();
            var first = true;
            using var sub = Events.On<Changed>(e =>
            {
                seen.Add(e.Key);
                if (!first) return;
                first = false;
                for (var id = 10; id <= 13; id++) _notices.Post(id, new Changed(id), new EntityId((uint)id));
            });
            Write(0);
            Assert.Equal(4, _notices.Waiting);
            serials.Alive[new EntityId(10)] = 8;
            serials.Alive.Remove(new EntityId(13));
            Step();
            Assert.Equal([0, 11, 12], seen);
            Assert.Equal(0, _notices.Waiting);
        }
        finally
        {
            IdentitySources.Entities = was;
        }
    }

    [Fact]
    public void A_write_off_the_tick_thread_is_posted_at_the_next_step()
    {
        var seen = 0;
        using var sub = Events.On<Changed>(_ => seen++);
        Backend.TickThread = false;
        Write(3);
        Backend.TickThread = true;
        Assert.Equal(0, seen);
        Step();
        Assert.Equal(1, seen);
    }
}

/// <summary>A mod's context for writes made "from mod code", and the fake tables' plumbing.</summary>
public abstract unsafe class FakeTableTest : ContractTest
{
    protected readonly ModContext Mod;
    private readonly List<(FieldInfo Field, object? Was)> _restore = [];

    protected FakeTableTest()
    {
        Mod = HostAccess.CreateContext(new ModInfo("writer", "Writer", "1.0.0", "."), 77, ".");
        HostAccess.Current = Mod;
    }

    public override void Dispose()
    {
        HostAccess.Current = null;
        foreach (var (f, was) in Enumerable.Reverse(_restore)) f.SetValue(null, was);
        base.Dispose();
    }

    /// <summary>Points a wrapper's cached static at <paramref name="value"/> for this test.</summary>
    protected void Swap(Type owner, string field, object? value)
    {
        var f = owner.GetField(field, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                ?? throw new InvalidOperationException($"{owner.Name}.{field} not found");
        _restore.Add((f, f.GetValue(null)));
        f.SetValue(null, value);
    }

    protected static object Box<T>(T* p) where T : unmanaged => Pointer.Box(p, typeof(T*));

    /// <summary>Serves <paramref name="table"/> as the native service <paramref name="name"/> (Services.Get finds it, so
    /// its row is the core's "found"). <paramref name="wrapper"/>.<paramref name="field"/>: the wrapper's cached
    /// pointer, cleared so the wrapper looks the table up, and cleared again after the test.</summary>
    protected void Serve<T>(string name, T* table, Type wrapper, string field) where T : unmanaged
    {
        // the field first: reading it runs the wrapper's static constructor, which may look the table up (it must find
        // none yet, or the pointer it caches would be "restored" after the test)
        Swap(wrapper, field, Box<T>(null));
        Backend.Tables[name] = (nint)table;
    }

    protected static T* Alloc<T>() where T : unmanaged
    {
        var p = (T*)NativeMemory.AllocZeroed((nuint)sizeof(T));
        return p;  // left allocated: a wrapper may keep the pointer past the test (one small block per test)
    }

}

/// <summary>Kiln.Content's writes with subscribers attached, on a fake kiln.content table.</summary>
public sealed unsafe class ContentChangeTests : FakeTableTest
{
    private static int _setRc, _resetRc, _applied, _sets;
    private static uint _reverted;
    private static bool _longReport;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeLabel(ulong owner, NativeStr label) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeSet(ulong owner, uint kind, uint id, NativeStr path, ContentNative.Value* v)
    {
        _sets++;
        return _setRc;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeReset(ulong owner, uint kind, uint id, NativeStr path) => _resetRc;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeApply(ulong owner, NativeStr text, NativeStr source, byte* report, nuint cap, nuint* len)
    {
        // what native says for a document: Ok also when every section was refused
        var json = _applied > 0 ? $"{{\"applied\":{_applied},\"errors\":[]}}" : "{\"applied\":0,\"errors\":[\"items: an array, not an object\"]}";
        if (_longReport)
        {
            // native's report lists every conflict of the store (its keys sorted: applied, conflicts, errors, warnings)
            var c = new StringBuilder($"{{\"applied\":{_applied},\"conflicts\":[");
            for (var i = 0; c.Length <= (int)cap; i++)
                c.Append(i == 0 ? "" : ",").Append($"{{\"field\":\"items.{i}.maxStackSize\",\"winner\":\"a\",\"winnerValue\":\"1\",\"loser\":\"b\",\"loserValue\":\"2\"}}");
            json = c.Append("],\"errors\":[],\"warnings\":[]}").ToString();
        }
        var bytes = Encoding.UTF8.GetBytes(json);
        for (var i = 0; i < bytes.Length && i < (int)cap - 1; i++) report[i] = bytes[i];
        *len = (nuint)bytes.Length;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint FakeRevert(ulong owner) => _reverted;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint FakeFieldCount(uint kind) => 0;

    private readonly ContentEntry _entry;

    public ContentChangeTests()
    {
        _setRc = _resetRc = _applied = _sets = 0;
        _reverted = 0;
        _longReport = false;
        var t = Alloc<ContentNative.Table>();
        t->Header = new ServiceHeader { Size = (uint)sizeof(ContentNative.Table), Version = 4 };
        t->LabelOwner = &FakeLabel;
        t->Set = &FakeSet;
        t->Reset = &FakeReset;
        t->ApplyJson = &FakeApply;
        t->RevertOwner = &FakeRevert;
        t->FieldCount = &FakeFieldCount;  // the hint of a refused write lists the registry's fields
        Swap(typeof(Content), "_table", Box(t));
        var e = new ContentNative.Entry { Size = (uint)sizeof(ContentNative.Entry), Kind = (uint)ContentKind.Items, Id = 0x1234 };
        _entry = new ContentEntry(Content.Items, in e);
    }

    [Fact]
    public void A_catch_all_handler_that_writes_again_keeps_working()
    {
        var calls = 0;
        var others = 0;
        var sub = Events.On<KilnEvent>(e =>
        {
            if (e is ContentChangedEvent) calls++;
            else others++;
            _entry.MaxStack = 5;
        });
        for (var i = 0; i < Events.MaxFaults + 1; i++) _entry.MaxStack = 5;  // before: each write faulted the handler once
        Assert.True(sub.Active);
        Assert.Equal(0, sub.Stats.Faults);
        Assert.Equal(Events.MaxFaults + 1 + others, calls);   // one call per write; the handler's own writes wait
        Step(3);
        Assert.Equal(Events.MaxFaults + 4 + others, calls);   // and then one a step, not 32 levels deep
        Assert.True(sub.Active);
        sub.Dispose();
        Step();                                      // the last write's event goes out to nobody
        Assert.Equal(0, Content.Notices.Waiting);
    }

    [Fact]
    public void Each_write_that_took_posts_one_event_on_the_tick_thread()
    {
        var seen = new List<ContentChangedEvent>();
        var onTick = true;
        using var sub = Events.On<ContentChangedEvent>(e =>
        {
            seen.Add(e);
            onTick &= Backend.IsTickThread;
        });
        _entry.Set("maxStackSize", 20);
        _resetRc = 0;
        Assert.True(_entry.Reset("maxStackSize"));
        var e = Assert.Single(seen.Where(x => x.Change == ContentChange.Set));
        Assert.Equal((ContentKind?)ContentKind.Items, e.Kind);
        Assert.Equal(0x1234u, e.Id);
        Assert.Equal("maxStackSize", e.Path);
        Assert.Equal("writer", e.Origin);
        Assert.Single(seen.Where(x => x.Change == ContentChange.Reset));
        Assert.True(onTick);
    }

    [Fact]
    public void A_refused_write_and_a_reset_with_no_layer_post_nothing()
    {
        var seen = 0;
        using var sub = Events.On<ContentChangedEvent>(_ => seen++);
        _setRc = ContentNative.InvalidArgument;
        Assert.Throws<ArgumentException>(() => _entry.Set("maxStackSize", 20));
        _resetRc = ContentNative.NotFound;
        Assert.False(_entry.Reset("maxStackSize"));
        _reverted = 0;
        Assert.Equal(0, Content.Revert());
        Assert.Equal(0, seen);
        _reverted = 3;
        Assert.Equal(3, Content.Revert());
        Assert.Equal(1, seen);
    }

    [Fact]
    public void A_document_that_changed_nothing_posts_nothing()
    {
        var seen = new List<ContentChangedEvent>();
        using var sub = Events.On<ContentChangedEvent>(seen.Add);
        _applied = 0;
        var refused = Content.Apply("""{"items":[]}""", "test: refused.json");
        Assert.False(refused.Ok);
        Assert.Equal(0, refused.Applied);
        Assert.Empty(seen);
        _applied = 2;
        var took = Content.Apply("""{"items":{"Material_T1_Wood_Logs":{"maxStackSize":200}}}""", "test: stacks.json");
        Assert.Equal(2, took.Applied);
        var e = Assert.Single(seen);
        Assert.Equal(ContentChange.Applied, e.Change);
        Assert.Equal("test: stacks.json", e.Path);
    }

    [Fact]
    public void A_report_cut_at_its_buffer_still_counts_what_was_applied_and_posts_Applied()
    {
        var seen = new List<ContentChangedEvent>();
        using var sub = Events.On<ContentChangedEvent>(seen.Add);
        _applied = 3;
        _longReport = true;  // longer than the 2 MiB native may write: the text is cut, the document was applied
        var r = Content.Apply("""{"items":{"Material_T1_Wood_Logs":{"maxStackSize":200}}}""", "test: big.json");
        Assert.Equal(3, r.Applied);
        Assert.False(r.Ok);  // as before: the report itself could not be read whole
        Assert.Contains("could not be read", Assert.Single(r.Errors), StringComparison.Ordinal);
        Assert.Equal(ContentChange.Applied, Assert.Single(seen).Change);
    }
}

/// <summary>Kiln.Creatures' writes with subscribers attached, on a fake kiln.creatures table.</summary>
public sealed unsafe class CreatureChangeTests : FakeTableTest
{
    private static int _setRc, _resetRc, _switchGetRc;
    private static uint _layers, _aiLayers, _aiOn;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeSet(ulong owner, uint e, uint field, uint index, CreaturesNative.Value* v) => _setRc;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeReset(ulong owner, uint e, uint field, uint index, uint* n)
    {
        *n = _layers;
        return _resetRc;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeAiReset(ulong owner, uint e, NativeStr comp, NativeStr path, uint* n)
    {
        *n = _aiLayers;
        return _resetRc;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeSetReady(ulong owner, uint e, uint on) => _resetRc;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeAttackOverride(ulong owner, uint e, CreaturesNative.AttackOverride* o) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeAiSwitch(ulong owner, uint e, uint enabled)
    {
        if (_switchGetRc != 0) return _switchGetRc;
        if (enabled != 0) _aiOn = 1;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeAiSwitchGet(uint e, uint* on)
    {
        *on = _aiOn;
        return _switchGetRc;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakePrefer(ulong owner, uint e, uint target, float seconds) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeCommand(CreaturesNative.Command* c) => 0;

    private readonly CreatureRef _creature;

    public CreatureChangeTests()
    {
        _setRc = _resetRc = _switchGetRc = 0;
        _layers = _aiLayers = 0;
        _aiOn = 1;
        var t = Alloc<CreaturesNative.Table>();
        t->Header = new ServiceHeader { Size = (uint)sizeof(CreaturesNative.Table), Version = 1 };
        t->Set = &FakeSet;
        t->Reset = &FakeReset;
        t->AiReset = &FakeAiReset;
        t->SetReady = &FakeSetReady;
        t->SetAttackOverride = &FakeAttackOverride;
        t->AiSwitch = &FakeAiSwitch;
        t->AiSwitchGet = &FakeAiSwitchGet;
        t->TargetPrefer = &FakePrefer;
        t->SendCommand = &FakeCommand;
        Swap(typeof(Creatures), "_listening", 1);  // no native listener on a fake table
        Swap(typeof(CreaturesNative), "_t", Box(t));
        _creature = Creatures.Of(new EntityId(5000));
    }

    [Fact]
    public void A_catch_all_handler_that_writes_again_keeps_working()
    {
        var calls = 0;
        var others = 0;
        var creature = _creature;
        var sub = Events.On<KilnEvent>(e =>
        {
            if (e is CreatureChangedEvent) calls++;
            else others++;
            creature.Level = 5;
        });
        for (var i = 0; i < Events.MaxFaults + 1; i++) creature.Level = 5;
        Assert.True(sub.Active);
        Assert.Equal(0, sub.Stats.Faults);
        Assert.Equal(Events.MaxFaults + 1 + others, calls);
        Step(3);
        Assert.Equal(Events.MaxFaults + 4 + others, calls);
        Assert.True(sub.Active);
        sub.Dispose();
        Step();
        Assert.Equal(0, Creatures.Notices.Waiting);
    }

    [Fact]
    public void A_write_posts_one_event_and_a_refused_one_none()
    {
        var seen = new List<CreatureChangedEvent>();
        using var sub = Events.On<CreatureChangedEvent>(seen.Add);
        _creature.Level = 12;
        var e = Assert.Single(seen);
        Assert.Equal(new EntityId(5000), e.Entity);
        Assert.Equal("Level", e.What);
        Assert.Equal("writer", e.Origin);
        _setRc = CreaturesNative.InvalidArgument;
        Assert.ThrowsAny<ArgumentException>(() => _creature.Level = 9999);
        Assert.Single(seen);
    }

    [Fact]
    public void Resetting_a_creature_that_is_gone_or_unchanged_posts_nothing()
    {
        var seen = new List<CreatureChangedEvent>();
        using var sub = Events.On<CreatureChangedEvent>(seen.Add);
        // gone: every call answers "not found", nothing was dropped
        _resetRc = _switchGetRc = CreaturesNative.NotFound;
        Assert.Equal(0, _creature.Reset());
        // here, but this mod had changed nothing
        _resetRc = _switchGetRc = 0;
        _aiOn = 1;
        Assert.Equal(0, _creature.Reset());
        Assert.Empty(seen);
    }

    [Fact]
    public void Resetting_a_held_target_or_an_attack_override_alone_posts_Reset()
    {
        var seen = new List<string>();
        using var sub = Events.On<CreatureChangedEvent>(e => seen.Add(e.What));
        // no value or AI layer, the AI stays on: what the mod held is the only thing Reset drops
        _creature.PreferTarget(new EntityId(6000), seconds: 60);
        Assert.Equal(0, _creature.Reset());
        Assert.Equal(["Target", "Reset"], seen);
        seen.Clear();
        _creature.Attacks.Natural();
        Assert.Equal(0, _creature.Reset());
        Assert.Equal(["Reset"], seen);
        // dropped once: a second Reset has nothing left
        seen.Clear();
        Assert.Equal(0, _creature.Reset());
        Assert.Empty(seen);
        // let go of before the Reset: nothing to drop
        _creature.PreferTarget(new EntityId(6000), seconds: 60);
        _creature.ReleaseTarget();
        seen.Clear();
        Assert.Equal(0, _creature.Reset());
        Assert.Empty(seen);
    }

    [Fact]
    public void A_target_another_mod_took_over_is_not_the_first_mod_s_to_reset()
    {
        var seen = new List<string>();
        using var sub = Events.On<CreatureChangedEvent>(e => seen.Add(e.What));
        _creature.PreferTarget(new EntityId(6000), seconds: 60);  // the writer mod holds the target
        var other = HostAccess.CreateContext(new ModInfo("other", "Other", "1.0.0", "."), 78, ".");
        HostAccess.Current = other;
        Creatures.Of(new EntityId(5000)).PreferTarget(new EntityId(6001), seconds: 60);  // kiln.creatures keeps this one only
        HostAccess.Current = Mod;
        seen.Clear();
        Assert.Equal(0, _creature.Reset());
        Assert.Empty(seen);  // the writer held nothing any more: its Reset changed nothing
    }

    private sealed class Serials : IEntitySerialSource
    {
        public readonly Dictionary<EntityId, ulong> Alive = new();
        public bool TryGetSerial(EntityId entity, out ulong serial) => Alive.TryGetValue(entity, out serial);
    }

    [Fact]
    public void A_change_that_waited_names_no_creature_that_took_the_id_since()
    {
        var serials = new Serials();
        serials.Alive[new EntityId(5000)] = 7;
        serials.Alive[new EntityId(5001)] = 3;
        var was = IdentitySources.Entities;
        IdentitySources.Entities = serials;
        try
        {
            var seen = new List<EntityId>();
            var creature = _creature;
            var other = Creatures.Of(new EntityId(5001));
            var first = true;
            using var sub = Events.On<CreatureChangedEvent>(e =>
            {
                seen.Add(e.Entity);
                if (!first) return;
                first = false;
                creature.Level = 6;  // both wait for the next step
                other.Level = 6;
            });
            _creature.Level = 5;
            Assert.Equal(2, Creatures.Notices.Waiting);
            serials.Alive[new EntityId(5000)] = 8;  // 5000 is destroyed and its id given to a new entity
            Step();
            Assert.Equal([new EntityId(5000), new EntityId(5001)], seen);  // nothing about the newcomer
        }
        finally
        {
            IdentitySources.Entities = was;
        }
    }

    [Fact]
    public void Resetting_drops_layers_or_turns_the_AI_back_on_and_posts_Reset()
    {
        var seen = new List<CreatureChangedEvent>();
        using var sub = Events.On<CreatureChangedEvent>(seen.Add);
        _layers = 2;
        _aiLayers = 1;
        Assert.Equal(3, _creature.Reset());
        Assert.Equal("Reset", Assert.Single(seen).What);
        // only the AI switch was this mod's: no layer dropped, the AI comes back on
        seen.Clear();
        _layers = _aiLayers = 0;
        _aiOn = 0;
        Assert.Equal(0, _creature.Reset());
        Assert.Equal("Reset", Assert.Single(seen).What);
    }
}
