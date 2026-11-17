// SPDX-License-Identifier: MIT
// Faults and lifetime through the real mod manager: a managed exception in a segment or a hop faults that task only
// (no handler strike, no unobserved report, the mod and the tick keep running), a task's time on the tick is booked
// to its mod, and the mod's load context stays pinned until its tasks are quiescent and is collected afterwards.
using System.Runtime.Loader;
using Kiln.Host;
using Xunit;

#pragma warning disable xUnit1031, xUnit1051

namespace Kiln.Host.Tests;

public sealed class TaskFaultTests : HostTaskTest
{
    private ModManager LoadSampleTasks(out LoadedMod mod)
    {
        var mods = LoadSampleTasks();
        mod = mods.Find("sample-tasks")!;
        return mods;
    }

    private ModManager LoadSampleTasks()
    {
        Install("SampleTasks", "sample-tasks");
        var mods = new ModManager(Host, ModsDir, DataDir, new ModManagerOptions
        {
            KilnVersion = Host.Version, StateFile = Path.Combine(LogsDir, "mods.txt"), TestResultsDirectory = Path.Combine(LogsDir, "tests"),
        });
        ModsBackend.Current = mods;
        HopFrame = mods.TaskTick.Run;
        Native.OnTick(mods.LoadAll);
        Assert.NotNull(mods.Find("sample-tasks"));
        return mods;
    }

    private void Step(ModManager mods, ulong step) => Native.Step(() =>
    {
        mods.ProcessRequests();
        mods.Pre(Tick(step), []);
    });

    [Fact]
    public void A_task_fault_in_a_segment_is_no_handler_fault_and_the_mod_keeps_running()
    {
        using var mods = LoadSampleTasks(out var mod);
        Assert.True(Until(() => Host.Has("task 'boom' faulted")), Host.Dump());
        var ticks = 0;
        mod.ModContext.Tick.Pre += (in TickInfo _) => ticks++;
        for (ulong s = 1; s <= 5; s++) Step(mods, s);
        Assert.Equal(5, ticks);  // the tick keeps running
        Assert.Equal(0, mod.Faults);
        Assert.False(mod.Disabled);
        Assert.Equal(1, Host.Count("task 'boom' faulted"));  // logged once, with the mod's own exception
        Assert.True(Host.Has("sample-tasks: deliberate task fault"));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        for (ulong s = 6; s <= 8; s++) Step(mods, s);  // an unobserved-exception report would be attributed here
        Assert.Equal(0, mod.Faults);
        Assert.Equal(1, Host.Count("deliberate task fault"));
        ulong next = 9;
        Assert.True(Until(() =>
        {
            Step(mods, next++);  // the scan's hop runs in whichever step follows its first segment
            return Host.Has("scan applied (computed off the tick: True, applied on the tick: True");
        }), Host.Dump());
        Native.OnTick(() => Assert.True(mods.Unload("sample-tasks", out _)));
        QuiesceAfterUnload();
    }

    [Fact]
    public void A_fault_in_a_tick_hop_faults_only_its_task()
    {
        using var mods = LoadSampleTasks(out var mod);
        var context = mod.ModContext;
        var applied = 0;
        var faulty = context.Tasks.Run("bad apply", ctx => ctx.PostToTick(() => throw new InvalidOperationException("apply blew up")));
        var fine = context.Tasks.Run("good apply", ctx => ctx.PostToTick(() => applied++));
        var ticks = 0;
        context.Tick.Pre += (in TickInfo _) => ticks++;
        Assert.True(Until(() => Native.PendingHops >= 2));
        for (ulong s = 1; s <= 3; s++) Step(mods, s);
        Ended(faulty);
        Ended(fine);
        Assert.Equal(KilnTaskState.Faulted, faulty.State);
        Assert.Equal("apply blew up", faulty.Fault!.Message);
        Assert.Equal(KilnTaskState.Completed, fine.State);  // the next hop of the same mod ran
        Assert.Equal(1, applied);
        Assert.Equal(3, ticks);
        Assert.Equal(0, mod.Faults);
        Assert.False(mod.Disabled);
        Assert.Equal(1, Host.Count("task 'bad apply' faulted"));
        Native.OnTick(() => Assert.True(mods.Unload("sample-tasks", out _)));
        QuiesceAfterUnload();
    }

    [Fact]
    public void A_hops_time_is_booked_to_its_mod_and_goes_through_the_task_tick_dispatcher()
    {
        using var mods = LoadSampleTasks(out var mod);
        Assert.True(Until(() => Native.PendingHops >= 1));  // the sample's scan waits for the tick
        var before = (mod.TotalPreTicks, mods.TaskTick.Hops, mod.TotalCalls);
        var slow = mod.ModContext.Tasks.Run("slow apply", ctx => ctx.PostToTick(() => Thread.Sleep(30)));
        Assert.True(Until(() => Native.PendingHops >= 2));
        Native.Step();  // hops only: no Pre of the mod in this step
        Ended(slow);
        Assert.True(mods.TaskTick.Hops >= before.Hops + 2);
        var booked = (mod.TotalPreTicks - before.TotalPreTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Assert.True(booked >= 25, $"{booked:F1} ms booked to the mod's Pre cost");
        Assert.Equal(before.TotalCalls, mod.TotalCalls);  // a hop is not a step of the mod
        Native.OnTick(() => Assert.True(mods.Unload("sample-tasks", out _)));
        QuiesceAfterUnload();
    }

    [Fact]
    public void A_mods_load_context_is_pinned_until_its_tasks_are_quiescent_then_collected()
    {
        using var mods = LoadSampleTasks();  // no local of this frame may hold the mod: its context must be collectable
        var owner = PinnedWhileLoaded(mods);
        // the workers are held by other mods, so the unloading mod's last continuation cannot run yet
        var hold = new ManualResetEventSlim();
        var blockers = Enumerable.Range(0, 2).Select(i => NewMod("blocker" + i).Tasks.Run("blocker", _ => hold.Wait())).ToList();
        Assert.True(Until(() => blockers.All(b => b.State == KilnTaskState.Running)));
        Native.OnTick(() => Assert.True(mods.Unload("sample-tasks", out _)));
        Assert.True(Host.Has("sample-tasks: unloading"));
        Assert.True(PinnedCount(owner) > 0);  // unloaded, but its task has not unwound: still pinned
        Assert.Equal(1, mods.CollectAndCountLeaks(out var held));
        Assert.Equal(["sample-tasks"], held);  // the context is alive while a task of it can still run
        hold.Set();
        Assert.True(Until(() => Host.Has("sample-tasks: waits unwound")), Host.Dump());
        foreach (var b in blockers) Ended(b);
        Assert.True(Until(() =>
        {
            Native.Step();  // the sample's scan may still wait for the tick: its hop is dispatched (cancelled) and it ends
            return PinnedCount(owner) == 0;
        }));
        Quiesce();
        Assert.Equal(0, Tasks.LivePins);
        Assert.Equal(0, mods.CollectAndCountLeaks(out var leaked));  // nothing of the bridge holds the context any more
        Assert.Empty(leaked);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private int PinnedCount(ulong owner) => Tasks.PinnedContexts(owner).Count;

    // Their own frames, so no local or temporary of the test keeps the mod's context alive.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private ulong PinnedWhileLoaded(ModManager mods)
    {
        var mod = mods.Find("sample-tasks")!;
        Native.WaitIdle();
        var pinned = Tasks.PinnedContexts(mod.Owner);
        Assert.NotEmpty(pinned);  // "waits" at least
        Assert.All(pinned, c => Assert.Same(mod.Context, c));
        Assert.True(mod.Context.IsCollectible);
        return mod.Owner;
    }

    [Fact]
    public void Pins_return_to_the_baseline_after_cancel_revoke_fault_and_hop_races()
    {
        var a = NewMod("alpha");
        var b = NewMod("beta");
        Assert.Equal(0, Tasks.LivePins);
        var tasks = new List<KilnTask>();
        for (var i = 0; i < 40; i++)
        {
            var n = i;
            var mod = n % 2 == 0 ? a : b;
            tasks.Add(mod.Tasks.Run("mix" + n, async ctx =>
            {
                await Task.Yield();
                if (n % 5 == 0) throw new InvalidOperationException("mix fault");
                if (n % 5 == 1) ctx.PostToTick(() => { });
                if (n % 5 == 2) await ctx.ToTick();
                if (n % 5 == 3) await Task.Delay(Timeout.Infinite, ctx.Cancellation);
                if (n % 5 == 4) await ctx.NextGateWindow();
            }));
        }
        // cancels racing segments and hops, a step racing both, then one owner revoked and the world stopping
        var racer = new Thread(() =>
        {
            for (var i = 0; i < tasks.Count; i += 3) tasks[i].Cancel();
        });
        racer.Start();
        Native.Step();
        racer.Join();
        Host.OwnerRevoke(a.Owner);
        Native.Step();
        Native.StopSession();
        foreach (var t in tasks.Where(t => !t.IsTerminal)) t.Cancel();
        foreach (var t in tasks) Ended(t);
        Quiesce();
        Assert.Equal(0, Tasks.LivePins);
        Assert.Equal(0, Native.Records);
        lock (Native.Ends) Assert.Equal(40, Native.Ends.Count);  // one end per admitted lease, never two
    }
}
