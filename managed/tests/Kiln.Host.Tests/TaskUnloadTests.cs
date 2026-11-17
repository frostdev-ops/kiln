// SPDX-License-Identifier: MIT
// T3 (issue #48): unloading or reloading a mod with tasks in flight never stalls the tick, never waits on the
// tick, and never tears a load context down while its tasks can still run. The join runs off the tick on the
// pool's drain_owner with a bounded budget; a task that will not stop leaves its context pinned (logged once)
// until it finishes. Mod objects (LoadedMod, ModContext) are only touched inside NoInlining helpers that have
// returned: a test frame that held one would root the very context it asks the GC about.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Kiln.Host;
using Xunit;

#pragma warning disable xUnit1031, xUnit1051

namespace Kiln.Host.Tests;

public sealed class TaskUnloadTests : HostTaskTest
{
    private ModManager NewMods(ModManagerOptions? options = null)
    {
        var mods = new ModManager(Host, ModsDir, DataDir, options ?? new ModManagerOptions
        {
            KilnVersion = Host.Version, StateFile = Path.Combine(LogsDir, "mods.txt"),
            TestResultsDirectory = Path.Combine(LogsDir, "tests"),
        });
        ModsBackend.Current = mods;
        HopFrame = mods.TaskTick.Run;
        return mods;
    }

    private ModManager LoadSampleTasks()
    {
        Install("SampleTasks", "sample-tasks");
        var mods = NewMods();
        Native.OnTick(mods.LoadAll);
        Assert.True(IsLoaded(mods, "sample-tasks"));
        return mods;
    }

    private void Step(ModManager mods, ulong step) => Native.Step(() =>
    {
        mods.ProcessRequests();
        mods.Pre(Tick(step), []);
    });

    private int LineIndex(string fragment)
    {
        lock (Host.Lines) return Host.Lines.FindIndex(l => l.Contains(fragment, StringComparison.Ordinal));
    }

    /// <summary>Asks how many unloaded contexts are still alive, from its own frame.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Collect(ModManager mods) => mods.CollectAndCountLeaks(out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsLoaded(ModManager mods, string id) => mods.Find(id) is not null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private KilnTask StartOn(ModManager mods, string id, string name, Action<KilnTaskContext> work) =>
        mods.Find(id)!.ModContext.Tasks.Run(name, work);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private KilnTask StartOn(ModManager mods, string id, string name, Func<KilnTaskContext, Task> work) =>
        mods.Find(id)!.ModContext.Tasks.Run(name, work);

    /// <summary>Counts the calls of the mod's load context's Unloading event (raised by AssemblyLoadContext.Unload).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static StrongBox<int> WatchUnloading(ModManager mods, string id)
    {
        var box = new StrongBox<int>();
        mods.Find(id)!.Context.Unloading += _ => Interlocked.Increment(ref box.Value);
        return box;
    }

    /// <summary>Steps the tick until every lease's pin is gone (a cancelled hop is flushed by a step).</summary>
    private void StepUntilNoPins(ModManager mods) =>
        Assert.True(Until(() =>
        {
            Step(mods, 0);
            return Tasks.LivePins == 0;
        }), Host.Dump());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void UnloadOnTick(ModManager mods, string id) =>
        Native.OnTick(() => Assert.True(mods.Unload(id, out _)));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CountHelloTicks(ModManager mods, Action onTick) =>
        mods.Find("hello")!.ModContext.Tick.Pre += (in TickInfo _) => onTick();

    [Fact]
    public void Unload_with_blocked_queued_and_tick_suspended_tasks_never_waits_and_pins_the_blocked_context()
    {
        Use(1);  // one worker: the order below is forced, not hoped for
        Install("Hello", "hello");
        using var mods = LoadSampleTasks();
        var helloTicks = 0;
        CountHelloTicks(mods, () => Interlocked.Increment(ref helloTicks));

        var applied = false;
        var suspended = StartOn(mods, "sample-tasks", "suspended-hop", async ctx =>
        {
            await ctx.ToTick();
            applied = true;
        });
        Native.WaitIdle();
        Assert.True(Until(() => Native.PendingHops >= 1));  // suspended in ToTick: it holds no worker

        using var gate = new ManualResetEventSlim();
        var blockedStarted = new ManualResetEventSlim();
        var blocked = StartOn(mods, "sample-tasks", "blocked", ctx =>
        {
            blockedStarted.Set();
            gate.Wait();  // ignores its cancellation token: it will not stop
        });
        Assert.True(blockedStarted.Wait(Long));
        var queuedRan = false;
        var queued = StartOn(mods, "sample-tasks", "queued", _ => queuedRan = true);
        Assert.Equal(KilnTaskState.Queued, queued.State);  // the one worker is the blocked task's
        var unloading = WatchUnloading(mods, "sample-tasks");

        UnloadOnTick(mods, "sample-tasks");  // returns with the gate still closed: the tick never waited
        Assert.True(Host.Has("sample-tasks: unloading"));  // OnUnload ran on the tick, not deferred by the tasks
        Assert.Equal(KilnTaskState.Cancelled, queued.State);
        Assert.False(queuedRan);
        Assert.Equal(KilnTaskState.Running, blocked.State);

        // Other mods keep ticking while this one is blocked, and the tick-suspended task's hop is flushed
        // cancelled by the steps, never applied.
        for (ulong s = 1; s <= 3; s++) Step(mods, s);
        Assert.True(helloTicks >= 3, $"hello ticked {helloTicks} time(s)");

        // The blocked task outlives the join budget: Kiln says so once, naming the mod, the task and its state,
        // and its context stays pinned instead of being torn down.
        Assert.True(Until(() => Host.Has("stays pinned")), Host.Dump());
        Assert.True(Host.Has("'blocked' (Running)"), Host.Dump());
        Assert.Equal(1, Host.Count("stays pinned"));
        Assert.Equal(1, Collect(mods));
        Assert.Equal(0, Volatile.Read(ref unloading.Value));  // pinned: AssemblyLoadContext.Unload was not called

        gate.Set();
        Ended(blocked);
        Ended(suspended);
        Assert.Equal(KilnTaskState.Cancelled, suspended.State);
        Assert.False(applied);
        Assert.True(Until(() =>
        {
            Native.Step();
            return Host.Has("load context unloaded") && Tasks.LivePins == 0;
        }), Host.Dump());
        Assert.Equal(1, Volatile.Read(ref unloading.Value));
        Assert.True(Until(() => Collect(mods) == 0), Host.Dump());
        Quiesce();
    }

    [Fact]
    public void Reload_while_a_task_is_suspended_in_ToTick_never_runs_the_old_instance_on_the_new_one()
    {
        using var mods = LoadSampleTasks();
        var applied = 0;
        var suspended = StartOn(mods, "sample-tasks", "old-apply", async ctx =>
        {
            await ctx.ToTick();
            Interlocked.Increment(ref applied);
        });
        Native.WaitIdle();
        Assert.True(Until(() => Native.PendingHops >= 1));

        ReloadSampleTasks(mods);
        for (ulong s = 1; s <= 4; s++) Step(mods, s);
        Ended(suspended);
        Assert.Equal(KilnTaskState.Cancelled, suspended.State);
        Assert.Equal(0, Volatile.Read(ref applied));  // the old hop never applied on the new instance
        UnloadOnTick(mods, "sample-tasks");
        StepUntilNoPins(mods);  // the new instance's own suspended scan is flushed cancelled by a step
        Quiesce();
    }

    /// <summary>Reloads sample-tasks and checks, inside this frame, that the new instance and its context are
    /// fresh ones.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReloadSampleTasks(ModManager mods)
    {
        var old = mods.Find("sample-tasks")!;
        ModOperationResult? result = null;
        Native.OnTick(() => result = mods.ReloadCore("sample-tasks"));
        Assert.True(result!.Ok, result.Message);
        var fresh = mods.Find("sample-tasks")!;
        Assert.NotSame(old, fresh);
        Assert.NotSame(old.Context, fresh.Context);
    }

    [Fact]
    public void A_dependents_blocked_task_blocks_its_dependencys_context_teardown_in_order()
    {
        InstallLibSet();
        using var mods = NewMods();
        Native.OnTick(mods.LoadAll);
        Assert.True(IsLoaded(mods, "sample-consumer") && IsLoaded(mods, "sample-lib"));

        using var gate = new ManualResetEventSlim();
        var started = new ManualResetEventSlim();
        var blocked = StartOn(mods, "sample-consumer", "consumer-blocked", ctx =>
        {
            started.Set();
            gate.Wait();
        });
        Assert.True(started.Wait(Long));

        UnloadOnTick(mods, "sample-lib");  // cascades: the consumer first
        Assert.True(Host.Has("sample-consumer unloading"));
        Assert.True(Host.Has("sample-lib unloading"));
        Assert.Equal(KilnTaskState.Running, blocked.State);

        Assert.True(Until(() => Host.Has("stays pinned") && Host.Has("'consumer-blocked'")), Host.Dump());
        Assert.Equal(2, Collect(mods));  // the dependent's context and, behind it, its dependency's

        gate.Set();
        Ended(blocked);
        Assert.True(Until(() =>
            LineIndex("mod sample-consumer's load context unloaded") >= 0 &&
            LineIndex("mod sample-lib's load context unloaded") >= 0 &&
            Collect(mods) == 0), Host.Dump());
        var consumerGone = LineIndex("mod sample-consumer's load context unloaded");
        var libGone = LineIndex("mod sample-lib's load context unloaded");
        Assert.True(libGone > consumerGone,
            $"consumer context unloaded at line {consumerGone}, lib at {libGone}:\n{Host.Dump()}");
        Quiesce();
    }

    [Fact]
    public void Hosted_stop_and_rehost_never_applies_old_session_work_in_the_new_session()
    {
        Install("SampleTasks", "sample-tasks");
        using var mods = NewMods(new ModManagerOptions
        {
            Side = ManifestSide.Client, HostedServerMods = true, KilnVersion = Host.Version,
            StateFile = Path.Combine(LogsDir, "mods.txt"), TestResultsDirectory = Path.Combine(LogsDir, "tests"),
        });
        Native.OnTick(mods.LoadAll);
        Assert.False(IsLoaded(mods, "sample-tasks"));  // a server mod: skipped until this client hosts

        Assert.Equal(["sample-tasks"], StartHosting(mods));
        var applied = 0;
        var scan = StartOn(mods, "sample-tasks", "hosted-scan", async ctx =>
        {
            await ctx.ToTick();
            Interlocked.Increment(ref applied);
        });
        Native.WaitIdle();
        Assert.True(Until(() => Native.PendingHops >= 1));  // a pending tick apply of the old session

        Native.StopSession();  // the hosted world ends: its hops are flushed cancelled by the core
        StopHosting(mods);
        Ended(scan);
        Assert.Equal(KilnTaskState.Cancelled, scan.State);
        Assert.Equal(0, Volatile.Read(ref applied));

        Native.StartSession();
        Assert.Equal(["sample-tasks"], StartHosting(mods));
        Assert.True(IsLoaded(mods, "sample-tasks"));
        for (ulong s = 1; s <= 4; s++) Step(mods, s);
        Assert.Equal(0, Volatile.Read(ref applied));  // nothing of the old session applied in the new one
        Native.StopSession();
        StopHosting(mods);
        Quiesce();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<string> StartHosting(ModManager mods)
    {
        var loaded = new List<string>();
        Native.OnTick(() => loaded = mods.StartHosting());
        return loaded;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StopHosting(ModManager mods) => Native.OnTick(() => mods.StopHosting("test"));

    [Fact]
    public void UnloadAll_does_not_wait_for_a_blocked_task()
    {
        using var mods = LoadSampleTasks();
        using var gate = new ManualResetEventSlim();
        var started = new ManualResetEventSlim();
        var blocked = StartOn(mods, "sample-tasks", "exit-blocked", ctx =>
        {
            started.Set();
            gate.Wait();
        });
        Assert.True(started.Wait(Long));

        var sw = Stopwatch.StartNew();
        Native.OnTick(() => mods.UnloadAll("server stopping"));
        sw.Stop();
        Assert.True(Host.Has("sample-tasks: unloading"));
        Assert.Equal(KilnTaskState.Running, blocked.State);  // still blocked: shutdown did not join it
        Assert.True(sw.Elapsed < ModManager.ShutdownWait, $"UnloadAll took {sw.Elapsed}");
        gate.Set();
        Ended(blocked);
        Native.StopSession();  // the world is gone: the sample's pending hop is flushed cancelled, as at a real stop
        Assert.True(Until(() => Host.Has("load context unloaded") && Tasks.LivePins == 0), Host.Dump());
        Quiesce();
    }
    [Fact]
    public void A_dependency_with_its_own_blocked_task_is_reported_too_while_its_dependent_still_holds_it()
    {
        InstallLibSet();
        using var mods = NewMods();
        Native.OnTick(mods.LoadAll);
        using var gate = new ManualResetEventSlim();
        var started = new CountdownEvent(2);
        var consumer = StartOn(mods, "sample-consumer", "consumer-blocked", _ =>
        {
            started.Signal();
            gate.Wait();
        });
        var lib = StartOn(mods, "sample-lib", "lib-blocked", _ =>
        {
            started.Signal();
            gate.Wait();
        });
        Assert.True(started.Wait(Long));

        UnloadOnTick(mods, "sample-lib");
        // each mod's blocked task is named once, the dependency's too, though its join waits behind its dependent's
        Assert.True(Until(() => Host.Has("mod sample-consumer: task 'consumer-blocked' (Running)") &&
                                Host.Has("mod sample-lib: task 'lib-blocked' (Running)")), Host.Dump());
        Assert.Equal(1, Host.Count("mod sample-lib: task"));
        Assert.Equal(1, Host.Count("mod sample-consumer: task"));

        gate.Set();
        Ended(consumer);
        Ended(lib);
        Assert.True(Until(() => Host.Has("mod sample-lib's load context unloaded") && Collect(mods) == 0), Host.Dump());
        StepUntilNoPins(mods);
        Quiesce();
    }

    [Fact]
    public void Disposing_the_mod_manager_stops_its_task_join()
    {
        using var gate = new ManualResetEventSlim();
        var started = new ManualResetEventSlim();
        KilnTask blocked;
        using (var mods = LoadSampleTasks())
        {
            blocked = StartOn(mods, "sample-tasks", "blocked", _ =>
            {
                started.Set();
                gate.Wait();
            });
            Assert.True(started.Wait(Long));
            UnloadOnTick(mods, "sample-tasks");
            Assert.True(Until(() => Host.Has("stays pinned")), Host.Dump());  // its join keeps visiting the pinned context
        }
        // the manager is gone (a test's end): its join no longer calls into the core, which may be another one by now
        var drains = Native.Drains;
        Thread.Sleep(300);
        Assert.Equal(drains, Native.Drains);
        gate.Set();
        Ended(blocked);
        Native.StopSession();  // flushes the sample's own pending hop
        Assert.True(Until(() => Tasks.LivePins == 0), Host.Dump());
        Quiesce();
    }
}
