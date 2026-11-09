// SPDX-License-Identifier: MIT
using Xunit;
using System.Runtime.InteropServices;

namespace Kiln.Api.Tests;

/// <summary>Pacing: what mods register for the game's waits and worker caps, merged as Enshwick's loader does (load order,
/// each handler on the last one's answer, the lowest cap wins). The native half that hooks the game's waits is not part of
/// these tests; they play its calls (Attach, DispatchServerWait, DispatchLoopWait, TakeTaskWorkerCaps).</summary>
public sealed class PacingTests : KilnTest
{
    public PacingTests() => Pacing.Reset();

    public override void Dispose()
    {
        Pacing.Reset();
        ModsBackend.Current = null;
        base.Dispose();
    }

    private static long ServerWait(long wait)
    {
        Pacing.DispatchServerWait(ref wait);
        return wait;
    }

    private sealed class FakeMods(params (string Id, int Index)[] mods) : IModsBackend
    {
        public IReadOnlyList<ModEntry> All => mods.Select(m => Get(m.Id)!).ToArray();
        public ModSide Side => ModSide.Server;

        public ModEntry? Get(string id)
        {
            var m = mods.FirstOrDefault(x => x.Id == id);
            return m.Id is null ? null : new ModEntry(m.Id, m.Id, "1.0.0", "", ModSide.Server, ModState.Loaded, null, [], m.Index, null);
        }

        public ModStats Stats(string id) => default;
        public bool Send(ModContext? sender, string targetId, string method, object? payload) => false;
        public Task<ModOperationResult> Request(ModOperation op, string arg) => throw new NotSupportedException();
        public void Request(ModOperation op, string arg, Action<ModOperationResult> done, ModContext? caller) => throw new NotSupportedException();
    }

    [Fact]
    public void Nothing_runs_until_the_game_side_says_which_waits_it_hooked()
    {
        Assert.False(Pacing.Available);
        Assert.Equal(PacingSites.None, Pacing.Sites);
        var h = As(NewMod("pcnone"), () => Pacing.OnServerWait((ref long w) => w = 5));
        Assert.False(h.Found);
        Pacing.Attach(PacingSites.ServerWait | PacingSites.LoopWait);
        Assert.True(Pacing.Available);
        Assert.True(h.Found);  // live: the build has the wait
        var loop = As(NewMod("pcnone2"), () => Pacing.OnLoopWait((in LoopWait w) => { }));
        Assert.True(loop.Found);
        Pacing.Attach(PacingSites.ServerWait);
        Assert.False(loop.Found);
    }

    [Fact]
    public void Server_wait_handlers_chain_in_registration_order_and_may_change_the_wait()
    {
        var mod = NewMod("pcchain");
        As(mod, () =>
        {
            Pacing.OnServerWait((ref long w) =>
            {
                if (w == Pacing.ServerOnlineWaitNs) w *= 2;
            });
            Pacing.OnServerWait((ref long w) => w += 5);
        });
        Assert.Equal(2_000_005, ServerWait(Pacing.ServerOnlineWaitNs));
        Assert.Equal(Pacing.ServerIdleWaitNs + 5, ServerWait(Pacing.ServerIdleWaitNs));  // the first one left it alone
        Assert.Equal(2, Pacing.HandlerCount(PacingSites.ServerWait));
    }

    [Fact]
    public void Mods_run_in_load_order_not_in_the_order_they_registered()
    {
        ModsBackend.Current = new FakeMods(("pcfirst", 0), ("pcsecond", 1));
        var first = NewMod("pcfirst");
        var second = NewMod("pcsecond");
        var seen = new List<string>();
        As(second, () => Pacing.OnServerWait((ref long w) => { seen.Add("second"); w = w * 10; }));
        As(first, () => Pacing.OnServerWait((ref long w) => { seen.Add("first"); w += 1; }));
        Assert.Equal(10_000_010, ServerWait(1_000_000));  // (1 ms + 1 ns) * 10: the first-loaded mod acts first
        Assert.Equal(["first", "second"], seen);
    }

    [Fact]
    public void A_handler_runs_as_its_mod_and_dispose_removes_it()
    {
        var mod = NewMod("pcowner");
        string? ranAs = null;
        var h = As(mod, () => Pacing.OnServerWait((ref long w) => ranAs = ModContext.CurrentMod?.Info.Id));
        ServerWait(1);
        Assert.Equal("pcowner", ranAs);
        Assert.Null(ModContext.CurrentMod);  // restored for the game's thread
        ranAs = null;
        h.Dispose();
        ServerWait(1);
        Assert.Null(ranAs);
        Assert.Equal(0, Pacing.HandlerCount(PacingSites.ServerWait));
    }

    [Fact]
    public void Unloading_a_mod_removes_its_handlers_and_caps()
    {
        Pacing.Attach(PacingSites.ServerWait | PacingSites.LoopWait | PacingSites.TaskWorkers);
        var mod = NewMod("pcgone");
        var other = NewMod("pckeep");
        var calls = 0;
        As(mod, () =>
        {
            Pacing.OnServerWait((ref long w) => calls++);
            Pacing.OnLoopWait((in LoopWait w) => calls++);
            Assert.True(Pacing.CapTaskWorkers(TaskWorkerCaps.AtMost(2)));
        });
        As(other, () => Pacing.OnServerWait((ref long w) => w = 7_000_000));
        Assert.Equal(new TaskWorkerCaps(2, 2), Pacing.EffectiveTaskWorkerCaps);
        mod.Retire();
        Assert.Equal(7_000_000, ServerWait(Pacing.ServerOnlineWaitNs));
        Pacing.DispatchLoopWait(new LoopWait(0, 500_000));
        Assert.Equal(0, calls);
        Assert.Equal(1, Pacing.HandlerCount(PacingSites.ServerWait));
        Assert.Equal(0, Pacing.HandlerCount(PacingSites.LoopWait));
        Assert.True(Pacing.EffectiveTaskWorkerCaps.IsEmpty);
    }

    [Fact]
    public void A_loop_wait_handler_sees_the_wait_and_the_message_wait_too()
    {
        var seen = new List<LoopWait>();
        As(NewMod("pcloop"), () => Pacing.OnLoopWait((in LoopWait w) => seen.Add(w)));
        Pacing.DispatchLoopWait(new LoopWait(0, 400_000));
        Pacing.DispatchLoopWait(new LoopWait(3, 3_900_000, LoopWaitKind.Messages));
        Assert.Equal([new LoopWait(0, 400_000), new LoopWait(3, 3_900_000, LoopWaitKind.Messages)], seen);
        Assert.Equal(LoopWaitKind.Handles, seen[0].Kind);
    }

    [Fact]
    public void Handler_counts_include_a_loop_handler_for_both_loop_sites()
    {
        var handler = As(NewMod("pcloopcount"), () => Pacing.OnLoopWait((in LoopWait w) => { }));
        Assert.Equal(1, Pacing.HandlerCount(PacingSites.LoopWait));
        Assert.Equal(1, Pacing.HandlerCount(PacingSites.MessageWait));
        Assert.Equal(0, Pacing.HandlerCount(PacingSites.ServerWait));
        handler.Dispose();
        Assert.Equal(0, Pacing.HandlerCount(PacingSites.LoopWait | PacingSites.MessageWait));
    }

    [Fact]
    public void A_server_wait_handler_is_not_called_for_loop_waits_and_the_other_way_round()
    {
        var calls = new List<string>();
        As(NewMod("pckinds"), () =>
        {
            Pacing.OnServerWait((ref long w) => calls.Add("server"));
            Pacing.OnLoopWait((in LoopWait w) => calls.Add("loop"));
        });
        ServerWait(1);
        Pacing.DispatchLoopWait(new LoopWait(0, 1));
        Assert.Equal(["server", "loop"], calls);
    }

    [Fact]
    public void A_handler_that_throws_is_logged_and_removed_after_three_failures_in_a_row()
    {
        var mod = NewMod("pcbad");
        var n = 0;
        var h = As(mod, () => Pacing.OnServerWait((ref long w) =>
        {
            n++;
            throw new InvalidOperationException("boom");
        }));
        As(mod, () => Pacing.OnServerWait((ref long w) => w += 1));  // an honest handler of the same mod runs on
        Assert.Equal(1_000_001, ServerWait(Pacing.ServerOnlineWaitNs));
        Assert.Equal(1_000_001, ServerWait(Pacing.ServerOnlineWaitNs));
        Assert.False(h.Faulted);
        Assert.Equal(1_000_001, ServerWait(Pacing.ServerOnlineWaitNs));
        Assert.True(h.Faulted);
        Assert.Equal(1_000_001, ServerWait(Pacing.ServerOnlineWaitNs));
        Assert.Equal(3, n);  // gone
        Assert.Equal(1, Pacing.HandlerCount(PacingSites.ServerWait));
    }

    [Fact]
    public async Task Concurrent_throwing_loop_calls_do_not_lose_faults()
    {
        const int workers = 16, rounds = 64;
        var cancellation = TestContext.Current.CancellationToken;
        using var dispatch = new Barrier(workers + 1);
        using var throwing = new Barrier(workers);
        var mod = NewMod("pcparallelfaults");
        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Factory.StartNew(() =>
        {
            for (var round = 0; round < rounds; round++)
            {
                dispatch.SignalAndWait(cancellation);
                Pacing.DispatchLoopWait(new LoopWait(0, 1));
                dispatch.SignalAndWait(cancellation);
            }
        }, cancellation, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        var lostFaults = 0;
        var allRemoved = true;
        for (var round = 0; round < rounds; round++)
        {
            var handler = As(mod, () => Pacing.OnLoopWait((in LoopWait wait) =>
            {
                // Every invocation is already in the handler before any can throw and remove it.
                throwing.SignalAndWait(cancellation);
                throw new InvalidOperationException("concurrent failure");
            }));
            dispatch.SignalAndWait(cancellation);
            dispatch.SignalAndWait(cancellation);
            lostFaults += workers - handler.Faults;
            allRemoved &= handler.Faulted && Pacing.HandlerCount(PacingSites.LoopWait) == 0;
            handler.Dispose();
        }
        await Task.WhenAll(tasks);
        Assert.Equal(0, lostFaults);
        Assert.True(allRemoved);
    }

    [Fact]
    public async Task An_in_flight_success_resets_failures_that_completed_before_it()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var handler = As(NewMod("pcoverlapreset"), () => Pacing.OnLoopWait((in LoopWait wait) =>
        {
            if (wait.LeftNanoseconds == 123)
            {
                entered.Set();
                finish.Wait(cancellation);
                return;
            }
            throw new InvalidOperationException("failure before or after a successful completion");
        }));
        var success = Task.Run(() => Pacing.DispatchLoopWait(new LoopWait(0, 123)), cancellation);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), cancellation));
            Pacing.DispatchLoopWait(new LoopWait(0, 1));
            Pacing.DispatchLoopWait(new LoopWait(0, 1));
            Assert.Equal(2, handler.Faults);
            Assert.False(handler.Faulted);
        }
        finally
        {
            finish.Set();
            await success;
        }
        Assert.Equal(0, handler.Faults);
        Pacing.DispatchLoopWait(new LoopWait(0, 1));
        Pacing.DispatchLoopWait(new LoopWait(0, 1));
        Assert.Equal(2, handler.Faults);
        Assert.False(handler.Faulted);
        Pacing.DispatchLoopWait(new LoopWait(0, 1));
        Assert.True(handler.Faulted);
        Assert.Equal(0, Pacing.HandlerCount(PacingSites.LoopWait));
    }

    [Fact]
    public void A_good_call_resets_the_failure_count()
    {
        var calls = 0;
        As(NewMod("pcflaky"), () => Pacing.OnServerWait((ref long w) =>
        {
            if (++calls % 3 != 0) throw new InvalidOperationException("flaky");  // fails, fails, works, fails, fails, works ...
        }));
        for (var i = 0; i < 12; i++) ServerWait(1);
        Assert.Equal(12, calls);
        Assert.Equal(1, Pacing.HandlerCount(PacingSites.ServerWait));
    }

    [Fact]
    public void A_wait_changed_by_a_handler_that_throws_stays_as_it_was()
    {
        As(NewMod("pcpartial"), () => Pacing.OnServerWait((ref long w) =>
        {
            w = 999;
            throw new InvalidOperationException("after the write");
        }));
        Assert.Equal(10, ServerWait(10));
    }

    [Fact]
    public void Dispatch_without_handlers_changes_nothing()
    {
        Assert.Equal(42, ServerWait(42));
        Pacing.DispatchLoopWait(new LoopWait(1, 1));
    }

    [Fact]
    public void What_handlers_leave_for_the_server_loop_is_clamped_into_the_floor_to_the_maximum()
    {
        var mod = NewMod("pcclamp");
        long set = 0;
        As(mod, () => Pacing.OnServerWait((ref long w) => w = set));
        set = long.MaxValue;
        Assert.Equal(Pacing.ServerMaxWaitNs, ServerWait(Pacing.ServerIdleWaitNs));
        set = -1;
        Assert.Equal(Pacing.ServerIdleWaitNs, ServerWait(Pacing.ServerIdleWaitNs));  // garbage: the game's pick stays
        set = 0;
        Assert.Equal(Pacing.ServerMinWaitNs, ServerWait(Pacing.ServerIdleWaitNs));  // a 0 wait would be a busy spin
        set = 2_000_000;
        Assert.Equal(2_000_000, ServerWait(Pacing.ServerOnlineWaitNs));
        set = Pacing.ServerOnlineWaitNs;
        Assert.Equal(Pacing.ServerOnlineWaitNs, ServerWait(Pacing.ServerOnlineWaitNs));  // unchanged stays unchanged
    }

    [Fact]
    public void Wanted_is_the_union_of_what_handlers_are_for_and_Narrow_takes_places_away()
    {
        Assert.Equal(PacingSites.None, Pacing.Wanted);
        var mod = NewMod("pcwanted");
        var loop = As(mod, () => Pacing.OnLoopWait((in LoopWait w) => { }));
        Assert.Equal(PacingSites.LoopWait | PacingSites.MessageWait, Pacing.Wanted);
        var server = As(mod, () => Pacing.OnServerWait((ref long w) => { }));
        Assert.Equal(PacingSites.LoopWait | PacingSites.MessageWait | PacingSites.ServerWait, Pacing.Wanted);
        loop.Dispose();
        Assert.Equal(PacingSites.ServerWait, Pacing.Wanted);
        Pacing.Attach(PacingSites.ServerWait | PacingSites.TaskWorkers);
        Assert.True(server.Found);
        Pacing.Narrow(PacingSites.ServerWait);  // the game's code would not take the hook
        Assert.False(server.Found);
        Assert.Equal(PacingSites.TaskWorkers, Pacing.Sites);
        Assert.True(Pacing.Available);
    }

    [Fact]
    public void A_dispatch_with_handlers_allocates_nothing()
    {
        var count = 0;
        As(NewMod("pcalloc"), () =>
        {
            Pacing.OnServerWait((ref long w) => count++);
            Pacing.OnLoopWait((in LoopWait w) => count++);
        });
        var wait = new LoopWait(0, 400_000);
        long ns = 1_000_000;
        for (var i = 0; i < 100; i++)  // warm up: JIT, delegate caches
        {
            Pacing.DispatchServerWait(ref ns);
            Pacing.DispatchLoopWait(in wait);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            Pacing.DispatchServerWait(ref ns);
            Pacing.DispatchLoopWait(in wait);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(2200, count);
    }

    [Fact]
    public void The_native_mirrors_have_the_sizes_the_service_checks()
    {
        Assert.True(PacingNative.LayoutMatchesNative);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint NativeSitesFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetReceiverFn(nint loop, nint server, nint user);

    [Fact]
    public void A_refused_native_receiver_leaves_pacing_unavailable()
    {
        NativeSitesFn sites = () => (uint)PacingSites.ServerWait;
        SetReceiverFn refuse = (_, _, _) => 7;
        var table = Marshal.AllocHGlobal(48);
        try
        {
            Marshal.Copy(new byte[48], 0, table, 48);
            Marshal.WriteInt32(table, 0, 48);
            Marshal.WriteInt32(table, 4, 1);
            Marshal.WriteIntPtr(table, 8, Marshal.GetFunctionPointerForDelegate(sites));
            Marshal.WriteIntPtr(table, 16, Marshal.GetFunctionPointerForDelegate(refuse));
            Backend.Tables["kiln.pacing"] = table;
            PacingNative.Attach();
            Assert.False(Pacing.Available);
            Assert.Equal(PacingSites.None, Pacing.Sites);
            Assert.True(Backend.Logged("native receiver could not attach"));
        }
        finally
        {
            Marshal.FreeHGlobal(table);
            GC.KeepAlive(sites);
            GC.KeepAlive(refuse);
        }
    }

    [Fact]
    public void The_pools_having_started_is_learned_from_the_native_half_too()
    {
        Pacing.Attach(PacingSites.TaskWorkers);
        var started = false;
        Pacing.TasksStartedProbe = () => started;
        var mod = NewMod("pcprobe");
        Assert.False(Pacing.TaskWorkersStarted);
        started = true;
        Assert.True(Pacing.TaskWorkersStarted);
        Assert.False(As(mod, () => Pacing.CapTaskWorkers(TaskWorkerCaps.AtMost(2))));  // too late: nothing is recorded
        Assert.True(Pacing.EffectiveTaskWorkerCaps.IsEmpty);
        Assert.False(As(mod, () => Pacing.CapTaskWorkers(TaskWorkerCaps.AtMost(2))));  // and the hint is logged once
    }

    [Fact]
    public void Worker_caps_take_the_lowest_of_each_group_and_ignore_zero()
    {
        Pacing.Attach(PacingSites.TaskWorkers);
        var a = NewMod("pcwa");
        var b = NewMod("pcwb");
        Assert.True(As(a, () => Pacing.CapTaskWorkers(new TaskWorkerCaps(8, 0))));
        Assert.True(As(b, () => Pacing.CapTaskWorkers(new TaskWorkerCaps(4, 6))));
        Assert.Equal(new TaskWorkerCaps(4, 6), Pacing.EffectiveTaskWorkerCaps);
        Assert.True(As(a, () => Pacing.CapTaskWorkers(new TaskWorkerCaps(0, 2))));  // a mod's later call replaces its earlier one
        Assert.Equal(new TaskWorkerCaps(4, 2), Pacing.EffectiveTaskWorkerCaps);
        Assert.Equal(new TaskWorkerCaps(3, 3), TaskWorkerCaps.AtMost(3));
        Assert.False(As(a, () => Pacing.CapTaskWorkers(default)));
        Assert.True(default(TaskWorkerCaps).IsEmpty);
    }

    [Fact]
    public void Caps_are_refused_without_the_place_and_after_the_pools_started()
    {
        var mod = NewMod("pcwlate");
        Assert.False(As(mod, () => Pacing.CapTaskWorkers(TaskWorkerCaps.AtMost(2))));  // a client, or a build without the place
        Pacing.Attach(PacingSites.TaskWorkers);
        Assert.True(As(mod, () => Pacing.CapTaskWorkers(TaskWorkerCaps.AtMost(2))));
        Assert.False(Pacing.TaskWorkersStarted);
        Assert.Equal(new TaskWorkerCaps(2, 2), Pacing.TakeTaskWorkerCaps());  // what the game applies as it starts them
        Assert.True(Pacing.TaskWorkersStarted);
        Assert.False(As(NewMod("pcwlate2"), () => Pacing.CapTaskWorkers(TaskWorkerCaps.AtMost(1))));  // too late
        Assert.Equal(new TaskWorkerCaps(2, 2), Pacing.EffectiveTaskWorkerCaps);
    }

    [Fact]
    public void The_game_side_hears_when_handlers_come_and_go()
    {
        var changes = 0;
        Pacing.Reset();
        Pacing.Changed += () => changes++;
        var h = As(NewMod("pcchanged"), () => Pacing.OnServerWait((ref long w) => { }));
        Assert.Equal(1, changes);
        h.Dispose();
        Assert.Equal(2, changes);
        Pacing.Attach(PacingSites.ServerWait);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Registering_for_an_unloading_mod_is_refused_and_off_the_tick_thread_too()
    {
        var mod = NewMod("pcdead");
        mod.Retire();
        Assert.Throws<InvalidOperationException>(() => As(mod, () => Pacing.OnServerWait((ref long w) => { })));
        Backend.TickThread = false;
        Assert.Throws<InvalidOperationException>(() => Pacing.OnLoopWait((in LoopWait w) => { }));
        Assert.Throws<InvalidOperationException>(() => Pacing.CapTaskWorkers(TaskWorkerCaps.AtMost(1)));
        Backend.TickThread = true;
        Assert.Throws<ArgumentNullException>(() => Pacing.OnServerWait(null!));
    }

    [Fact]
    public void Handlers_may_run_on_several_threads_at_once()
    {
        var total = 0L;
        As(NewMod("pcthreads"), () => Pacing.OnLoopWait((in LoopWait w) => Interlocked.Add(ref total, w.Milliseconds)));
        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 1000; i++) Pacing.DispatchLoopWait(new LoopWait(1, 1_000_000));
        });
        Assert.Equal(8000, total);
    }
}
