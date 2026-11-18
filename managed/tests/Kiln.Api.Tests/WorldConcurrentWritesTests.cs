// SPDX-License-Identifier: MIT
// WorldConcurrent's compare-exchange writes (Kiln.World) over the fake kiln.world table (FakeEcsReads, the shared write rules
// of Kiln.TestKit/Shared/EcsWritesLogic.cs) and the task API's fake pool: every outcome comes through as itself with its
// reason, the value the write saw comes back, the byte form checks its buffers, a call on the server tick is busy without
// reaching kiln.world, the calls are refused outside a task or the tick, the writes are unavailable without their members or
// capability, and committed writes come back as ComponentExchangedEvent on the tick, naming the mod.
using Xunit;

#pragma warning disable xUnit1031, xUnit1051  // the test thread is the mod's own thread; task bodies stand in for mod code
#pragma warning disable KILNX001

namespace Kiln.Api.Tests;

public sealed class WorldConcurrentWritesTests : TaskTest
{
    private static byte[] Le(long v) => BitConverter.GetBytes(v);

    public WorldConcurrentWritesTests()
    {
        FakeEcsReads.Reset();
        Backend.Tables["kiln.world"] = FakeEcsReads.Table;
        WorldNative.Reset();
        WorldConcurrent.Reset();
    }

    public override void Dispose()
    {
        try
        {
            Backend.Tables.Remove("kiln.world");
            WorldNative.Reset();
            WorldConcurrent.Reset();
            Capabilities.Reset();
        }
        finally
        {
            base.Dispose();
        }
    }

    private static T InTask<T>(ModContext mod, Func<T> body)
    {
        var result = default(T);
        var task = mod.Tasks.Run("writes", ctx => { result = body(); });
        Ended(task);
        if (task.Fault is { } fault) throw new Xunit.Sdk.XunitException($"the task faulted: {fault}");
        return result!;
    }

    // what the work of a task of the mod threw
    private static Exception? Thrown(ModContext mod, Action body)
    {
        var task = mod.Tasks.Run("throws", ctx => body());
        Ended(task);
        return task.Fault;
    }

    private static ConcurrentEntity Opened(ModContext mod, uint id)
    {
        var open = InTask(mod, () => WorldConcurrent.Open(new EntityId(id)));
        Assert.Equal(ReadStatus.Ok, open.Status);
        return open.Value;
    }

    private static long Counter(uint id) => BitConverter.ToInt64(FakeEcsReads.Entities[(0, id)].Components[World.ComponentHash("Counter")]);

    [Fact]
    public void A_task_compares_and_exchanges_a_component()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Counter", Le(5)));
        var r = Opened(mod, 12);
        var ok = InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Counter", 5L, 6L));
        Assert.Equal((ExchangeStatus.Ok, WriteReason.None, 6L), (ok.Status, ok.Reason, ok.Current));
        Assert.Equal(ExchangeFlags.Written | ExchangeFlags.Confirmed | ExchangeFlags.EventQueued, ok.Flags);
        Assert.True(ok.IsOk && ok.Written && ok.HoldTime > TimeSpan.Zero);
        Assert.Equal(6L, Counter(12));
        var noop = InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Counter", 6L, 6L));
        Assert.Equal((ExchangeStatus.Ok, ExchangeFlags.NoOp | ExchangeFlags.Confirmed, 6L), (noop.Status, noop.Flags, noop.Current));
        Assert.False(noop.Written);
        var conflict = InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Counter", 5L, 9L));
        Assert.Equal((ExchangeStatus.Conflict, WriteReason.Compare, 6L), (conflict.Status, conflict.Reason, conflict.Current));
        Assert.Equal(6L, Counter(12));
        Assert.Equal(1, FakeEcsReads.Writes);
        Assert.All(FakeEcsReads.Owners, o => Assert.Equal(mod.Owner, o));  // the task's mod is the owner of every call
    }

    [Fact]
    public void The_byte_form_merges_under_a_mask_and_checks_its_buffers()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Counter", Le(0x1111)));
        var r = Opened(mod, 12);
        var current = new byte[8];
        var mask = new byte[8];
        mask[0] = 1;
        var res = InTask(mod, () => WorldConcurrent.TryCompareExchangeBytes(r, "Counter", Le(0x1111), Le(0x2222), mask, current));
        Assert.Equal((ExchangeStatus.Ok, true), (res.Status, res.Written));
        Assert.Equal(0x1122L, Counter(12));  // the low byte from desired, the rest kept
        Assert.Equal(Le(0x1122), current);
        Assert.IsType<ArgumentException>(Thrown(mod, () => WorldConcurrent.TryCompareExchangeBytes(r, "Counter", Le(1), new byte[4])));
        Assert.IsType<ArgumentException>(Thrown(mod, () => WorldConcurrent.TryCompareExchangeBytes(r, "Counter", Le(1), Le(2), new byte[3])));
        Assert.IsType<ArgumentException>(Thrown(mod, () => WorldConcurrent.TryCompareExchangeBytes(r, "Counter", Le(1), Le(2), default, new byte[2])));
        Assert.IsType<ArgumentException>(Thrown(mod, () => WorldConcurrent.TryCompareExchangeBytes(r, "Counter", [], [])));
        Assert.IsType<ArgumentException>(Thrown(mod, () => WorldConcurrent.TryCompareExchange(default, "Counter", 1L, 2L)));
        Assert.Equal(1, FakeEcsReads.Writes);  // none of them reached the write
    }

    [Fact]
    public void A_value_of_another_size_is_not_writable()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Counter", Le(5)));
        var r = Opened(mod, 12);
        var res = InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Counter", 5, 6));  // an int for an 8-byte component
        Assert.Equal((ExchangeStatus.NotWritable, WriteReason.Size), (res.Status, res.Reason));
        Assert.Equal(5L, Counter(12));
    }

    [Fact]
    public void The_policy_says_what_is_writable_and_why_not()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Counter", Le(5)), ("Health", new byte[8]), ("Names", new byte[16]));
        FakeEcsReads.Policies[World.ComponentHash("Health")] = new(true, 8, true, true, false, true, 8, null);
        FakeEcsReads.Policies[World.ComponentHash("Names")] = new(true, 16, true, false, false, false, 16, "the type holds a dynamic array");
        var counter = WorldConcurrent.WritePolicy("Counter");  // any thread
        Assert.True(counter.IsWritable);
        Assert.Equal((8, 65536, true, false, true), (counter.Size, counter.Budget, counter.TrackingKnown, counter.IsTracked, counter.IsPlain));
        var health = WorldConcurrent.WritePolicy("Health");
        Assert.Equal((ExchangeStatus.NotWritable, WriteReason.Tracked, true), (health.Status, health.Reason, health.IsTracked));
        Assert.Contains("tracks changes", health.Text);
        var names = WorldConcurrent.WritePolicy("Names");
        Assert.Equal((ExchangeStatus.NotWritable, WriteReason.NotPlain, "the type holds a dynamic array"), (names.Status, names.Reason, names.Text));
        Assert.Equal((ExchangeStatus.NotWritable, WriteReason.Unregistered), (WorldConcurrent.WritePolicy("Nothing").Status, WorldConcurrent.WritePolicy("Nothing").Reason));
        var client = WorldConcurrent.WritePolicy("Counter", WorldKind.Client);
        Assert.Equal((ExchangeStatus.Unavailable, WriteReason.NotServerWorld), (client.Status, client.Reason));
        var r = Opened(mod, 12);
        var tracked = InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Health", 0L, 1L));
        Assert.Equal((ExchangeStatus.NotWritable, WriteReason.Tracked), (tracked.Status, tracked.Reason));
    }

    [Fact]
    public void A_busy_gate_latches_the_mod_for_the_step_only()
    {
        var mod = NewMod("alpha");
        var other = NewMod("beta");
        FakeEcsReads.Add(12, WorldKind.Server, ("Counter", Le(5)));
        var r = Opened(mod, 12);
        var theirs = Opened(other, 12);
        FakeEcsReads.GateBusyNext = 1;
        var busy = InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Counter", 5L, 6L));
        var latched = InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Counter", 5L, 6L));
        var notLatched = InTask(other, () => WorldConcurrent.TryCompareExchange(theirs, "Counter", 5L, 6L));
        Assert.Equal((ExchangeStatus.Busy, WriteReason.Gate), (busy.Status, busy.Reason));
        Assert.Equal(ExchangeStatus.Busy, latched.Status);
        Assert.Equal(ExchangeStatus.Ok, notLatched.Status);
        FakeEcsReads.Step++;
        Assert.Equal(ExchangeStatus.Ok, InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Counter", 6L, 7L)).Status);
        FakeEcsReads.EventCapacity = 0;
        var full = InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Counter", 7L, 8L));
        Assert.Equal((ExchangeStatus.Busy, WriteReason.EventsFull), (full.Status, full.Reason));
    }

    [Fact]
    public void Uncertain_latches_the_target_and_a_rollback_is_unreadable()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Counter", Le(5)));
        FakeEcsReads.Add(13, WorldKind.Server, ("Counter", Le(5)));
        var r = Opened(mod, 12);
        var other = Opened(mod, 13);
        FakeEcsReads.RolledBackNext = 1;
        var rolled = InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Counter", 5L, 6L));
        Assert.Equal((ExchangeStatus.Unreadable, ExchangeFlags.RolledBack, 5L), (rolled.Status, rolled.Flags, Counter(12)));
        FakeEcsReads.UncertainNext = 1;
        var uncertain = InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Counter", 5L, 6L));
        Assert.Equal((ExchangeStatus.Uncertain, true), (uncertain.Status, uncertain.Flags.HasFlag(ExchangeFlags.TargetLatched)));
        var again = InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Counter", 6L, 7L));
        Assert.Equal((ExchangeStatus.Uncertain, WriteReason.TargetLatched, TimeSpan.Zero), (again.Status, again.Reason, again.HoldTime));
        Assert.Equal(ExchangeStatus.Ok, InTask(mod, () => WorldConcurrent.TryCompareExchange(other, "Counter", 5L, 6L)).Status);
    }

    [Fact]
    public void Stale_refs_another_mods_ref_and_a_retired_mod_never_write()
    {
        var mod = NewMod("alpha");
        var other = NewMod("beta");
        var e = FakeEcsReads.Add(12, WorldKind.Server, ("Counter", Le(5)));
        var r = Opened(mod, 12);
        var theirs = Opened(other, 12);
        var foreign = InTask(mod, () => WorldConcurrent.TryCompareExchange(theirs, "Counter", 5L, 6L));
        Assert.Equal((ExchangeStatus.Stale, WriteReason.NotOwnersRef), (foreign.Status, foreign.Reason));
        e.Generation = 2;
        var stale = InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Counter", 5L, 6L));
        Assert.Equal((ExchangeStatus.Stale, WriteReason.Ref), (stale.Status, stale.Reason));
        e.Generation = 1;
        FakeEcsReads.Retire(mod.Owner);
        var retired = InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Counter", 5L, 6L));
        Assert.Equal((ExchangeStatus.Stale, WriteReason.OwnerRetired), (retired.Status, retired.Reason));
        Assert.Equal(5L, Counter(12));
        Assert.Equal(0, FakeEcsReads.Writes);
    }

    [Fact]
    public void On_the_server_tick_it_is_busy_without_reaching_kiln_world()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Counter", Le(5)));
        var r = Opened(mod, 12);
        var calls = FakeEcsReads.CallsOf("CompareExchange");
        ExchangeResult<long> onTick = default, inHop = default;
        TickAs(mod, () => onTick = WorldConcurrent.TryCompareExchange(r, "Counter", 5L, 6L));
        var task = mod.Tasks.Run("hop", async ctx =>
        {
            await ctx.ToTick();
            inHop = WorldConcurrent.TryCompareExchange(r, "Counter", 5L, 6L);
        });
        Pool.WaitIdle();
        Pool.Step();
        Ended(task);
        Assert.Equal((ExchangeStatus.Busy, WriteReason.InFrame), (onTick.Status, onTick.Reason));
        Assert.Equal((ExchangeStatus.Busy, WriteReason.InFrame), (inHop.Status, inHop.Reason));
        Assert.Equal(calls, FakeEcsReads.CallsOf("CompareExchange"));
        Assert.Equal(5L, Counter(12));
    }

    [Fact]
    public void A_raw_thread_is_refused()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Counter", Le(5)));
        var r = Opened(mod, 12);
        Exception? caught = null;
        var raw = new Thread(() =>
        {
            try { WorldConcurrent.TryCompareExchange(r, "Counter", 5L, 6L); }
            catch (Exception e) { caught = e; }
        });
        raw.Start();
        raw.Join();
        Assert.Contains("inside a Kiln task or on the tick", Assert.IsType<InvalidOperationException>(caught).Message);
        Assert.Equal(0, FakeEcsReads.CallsOf("CompareExchange"));
    }

    [Fact]
    public void Without_the_members_or_their_capability_the_writes_are_unavailable()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Counter", Le(5)));
        var r = Opened(mod, 12);
        FakeEcsReads.WritesCapable = false;
        WorldConcurrent.Reset();
        Assert.False(WorldConcurrent.CanWrite);
        var off = InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Counter", 5L, 6L));
        Assert.Equal((ExchangeStatus.Unavailable, WriteReason.NoProfile), (off.Status, off.Reason));
        Assert.Equal(ExchangeStatus.Unavailable, WorldConcurrent.WritePolicy("Counter").Status);
        FakeEcsReads.Reset(584);  // a kiln.world with the reads only
        FakeEcsReads.Add(12, WorldKind.Server, ("Counter", Le(5)));
        WorldNative.Reset();
        WorldConcurrent.Reset();
        Assert.False(WorldConcurrent.CanWrite);
        Assert.True(WorldConcurrent.IsAvailable);
        var r2 = Opened(mod, 12);
        Assert.Equal(ExchangeStatus.Unavailable, InTask(mod, () => WorldConcurrent.TryCompareExchange(r2, "Counter", 5L, 6L)).Status);
        Assert.Equal((ExchangeStatus.Unavailable, WriteReason.NoProfile), (WorldConcurrent.WritePolicy("Counter").Status, WorldConcurrent.WritePolicy("Counter").Reason));
        Assert.Equal(0, FakeEcsReads.CallsOf("CompareExchange"));
    }

    [Fact]
    public void A_committed_write_comes_back_as_an_event_on_the_tick_naming_the_mod()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Counter", Le(5)));
        var r = Opened(mod, 12);
        var seen = new List<ComponentExchangedEvent>();
        Capabilities.Report(WorldConcurrent.WriteCapability, CapabilityState.Available);  // kiln.world reports it at boot
        using var sub = Events.On<ComponentExchangedEvent>(seen.Add);
        InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Counter", 5L, 6L));
        Assert.Equal(1, FakeEcsReads.Subscriptions);  // one native subscription for the backend
        InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Counter", 5L, 7L));  // a conflict: no event
        InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Counter", 6L, 6L));  // a no-op: no event
        Assert.Empty(seen);
        Pool.Step(() =>
        {
            FakeEcsReads.Drain();     // kiln.world's Pre
            WorldExchanges.Pump();    // the service tick
        });
        var e = Assert.Single(seen);
        Assert.Equal((12u, 1u, World.ComponentHash("Counter"), 1ul, "alpha"), (e.Entity.Value, e.Generation, e.ComponentHash, e.Session, e.Mod));
        Assert.True(e.Is("Counter") && e.Involves(new EntityId(12)) && !e.Involves(new EntityId(13)));
        Assert.Equal(WorldKind.Server, e.World);
    }

    // A new backend (a test kit's next harness) gets a subscription of its own at its first write; that write's event still
    // names the mod (the binding forgets the last backend's writers before it records this one).
    [Fact]
    public void After_a_new_backend_the_first_write_s_event_names_the_mod()
    {
        var seen = new List<ComponentExchangedEvent>();
        Capabilities.Report(WorldConcurrent.WriteCapability, CapabilityState.Available);
        using var sub = Events.On<ComponentExchangedEvent>(seen.Add);
        for (var round = 0; round < 2; round++)
        {
            if (round > 0) UsePool(2);
            var mod = NewMod("alpha" + round);
            FakeEcsReads.Add((uint)(20 + round), WorldKind.Server, ("Counter", Le(5)));
            var r = Opened(mod, (uint)(20 + round));
            Assert.True(InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Counter", 5L, 6L)).Written);
            Pool.Step(() =>
            {
                FakeEcsReads.Drain();
                WorldExchanges.Pump();
            });
            var e = Assert.Single(seen);
            Assert.Equal(("alpha" + round, (uint)(20 + round)), (e.Mod, e.Entity.Value));
            seen.Clear();
        }
        Assert.Equal(1, FakeEcsReads.Subscriptions);  // the last backend's subscription ended
    }

    // The callback queues for the service tick; a service tick that stopped can't make that queue grow without end.
    [Fact]
    public void The_events_waiting_for_the_service_tick_are_bounded_and_the_drops_logged()
    {
        var seen = 0;
        Capabilities.Report(WorldConcurrent.WriteCapability, CapabilityState.Available);
        using var sub = Events.On<ComponentExchangedEvent>(_ => seen++);
        WorldExchanges.Bind();
        for (var i = 0; i < WorldExchanges.MaxQueued + 3; i++)
            WorldExchanges.Deliver(new EcsWriteEventNative { Size = 48, WorldKind = 0, Entity = 12, Generation = 1, Component = 1, Session = 1 });
        Assert.Equal(WorldExchanges.MaxQueued, WorldExchanges.Queued);
        Pool.Step(WorldExchanges.Pump);
        Assert.Equal((WorldExchanges.MaxQueued, 0), (seen, WorldExchanges.Queued));
        Assert.True(Backend.Logged($"3 ComponentExchangedEvent(s) were dropped: more than {WorldExchanges.MaxQueued} waited"));
    }

    [Fact]
    public void Without_kiln_types_the_writes_are_unavailable()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Counter", Le(5)));
        var r = Opened(mod, 12);
        FakeEcsReads.TypesAvailable = false;
        WorldConcurrent.Reset();
        Assert.False(WorldConcurrent.CanWrite);
        var policy = WorldConcurrent.WritePolicy("Counter");
        Assert.Equal((ExchangeStatus.Unavailable, WriteReason.NoProfile), (policy.Status, policy.Reason));
        Assert.Contains("kiln.types", policy.Text);
        Assert.Equal(ExchangeStatus.Unavailable, InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Counter", 5L, 6L)).Status);
        Assert.Equal(5L, Counter(12));
    }

    // ---- the shared rules' unsubscribe: from another thread it waits for a running callback ----

    private static readonly ManualResetEventSlim CallbackEntered = new(), CallbackRelease = new();

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void Blocking(EcsWriteEventNative* e, void* user)
    {
        CallbackEntered.Set();
        CallbackRelease.Wait(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public unsafe void Unsubscribe_from_another_thread_waits_for_a_running_callback()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Counter", Le(5)));
        var r = Opened(mod, 12);
        CallbackEntered.Reset();
        CallbackRelease.Reset();
        delegate* unmanaged[Cdecl]<EcsWriteEventNative*, void*, void> fn = &Blocking;
        Assert.Equal(0, FakeEcsReads.Subscribe(99, (nint)fn, 0, out var handle));
        var written = InTask(mod, () => WorldConcurrent.TryCompareExchange(r, "Counter", 5L, 6L));
        Assert.True(written.Written, written.ToString());
        var drain = new Thread(() => FakeEcsReads.Drain());
        drain.Start();
        Assert.True(CallbackEntered.Wait(TimeSpan.FromSeconds(10)));
        var unsubscribed = new ManualResetEventSlim();
        var other = new Thread(() =>
        {
            FakeEcsReads.Unsubscribe(handle);
            unsubscribed.Set();
        });
        other.Start();
        Assert.False(unsubscribed.Wait(TimeSpan.FromMilliseconds(200)));  // the callback still runs: it waits
        CallbackRelease.Set();
        Assert.True(unsubscribed.Wait(TimeSpan.FromSeconds(10)));
        drain.Join();
        other.Join();
    }
}
