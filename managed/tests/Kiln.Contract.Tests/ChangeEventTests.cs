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
        // a mod built before the event existed: it writes on every event it sees
        using var sub = Events.On<KilnEvent>(e =>
        {
            if (e is Changed) calls++;
            Write(7);
        });
        Write(7);
        Assert.Equal(1, calls);              // the event, once; the handler's own write waits for the next step
        Assert.Equal(1, _notices.Waiting);
        Step(10);
        Assert.Equal(11, calls);             // one call a step, no recursion
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
        Step();
        Assert.Equal(1, seen);
    }

    private sealed class Probe : KilnEvent;

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
        var sub = Events.On<KilnEvent>(e =>
        {
            if (e is ContentChangedEvent) calls++;
            _entry.MaxStack = 5;
        });
        for (var i = 0; i < Events.MaxFaults + 1; i++) _entry.MaxStack = 5;  // before: each write faulted the handler once
        Assert.True(sub.Active);
        Assert.Equal(0, sub.Stats.Faults);
        Assert.Equal(Events.MaxFaults + 1, calls);   // one call per write; the handler's own writes wait
        Step(3);
        Assert.Equal(Events.MaxFaults + 4, calls);   // and then one a step, not 32 levels deep
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
        Swap(typeof(Creatures), "_listening", 1);  // no native listener on a fake table
        Swap(typeof(CreaturesNative), "_t", Box(t));
        _creature = Creatures.Of(new EntityId(5000));
    }

    [Fact]
    public void A_catch_all_handler_that_writes_again_keeps_working()
    {
        var calls = 0;
        var creature = _creature;
        var sub = Events.On<KilnEvent>(e =>
        {
            if (e is CreatureChangedEvent) calls++;
            creature.Level = 5;
        });
        for (var i = 0; i < Events.MaxFaults + 1; i++) creature.Level = 5;
        Assert.True(sub.Active);
        Assert.Equal(0, sub.Stats.Faults);
        Assert.Equal(Events.MaxFaults + 1, calls);
        Step(3);
        Assert.Equal(Events.MaxFaults + 4, calls);
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
