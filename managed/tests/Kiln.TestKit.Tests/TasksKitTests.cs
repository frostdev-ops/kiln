// SPDX-License-Identifier: MIT
// Kiln tasks under the harness (KilnHarness.Tasks, Shared/KitTaskPool.cs): a mod's work runs on the kit's workers, its
// hops run in the harness's steps before the mods' Pre and NextGateWindow resumes after one, Settle runs every task to
// its end with no sleeping in the test and fails clearly when one never ends, an unload cancels a mod's tasks, and a stop
// ends the hops still queued.
using TkEvents;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class TasksKitTests
{
    private static (KilnHarness Kiln, ModHandle Mod) Start()
    {
        var kiln = new KilnHarness();
        var mod = kiln.Load<TkEventsMod>(new ModManifest("tk-events"));
        kiln.Step();
        return (kiln, mod);
    }

    [Fact]
    public void A_task_works_on_a_worker_and_hops_to_the_tick_before_Pre()
    {
        var (kiln, mod) = Start();
        using var _ = kiln;
        var seen = new List<string>();
        var task = kiln.As(mod, () => mod.Context.Tasks.Run("hop", async ctx =>
        {
            lock (seen) seen.Add($"work tick={Game.IsTickThread}");
            await ctx.ToTick();
            lock (seen) seen.Add($"hop tick={Game.IsTickThread} step={Lifecycle.StartedAtStep > 0}");
            ctx.PostToTick(() => { lock (seen) seen.Add($"post tick={Game.IsTickThread}"); });
            await ctx.NextGateWindow();
            lock (seen) seen.Add($"window tick={Game.IsTickThread}");
        }));
        var steps = kiln.Tasks.Settle();
        Assert.Equal(KilnTaskState.Completed, task.State);
        // a post made on the tick lands at a later step's hops; the window resumes at this step's Post edge
        Assert.Equal(["work tick=False", "hop tick=True step=True"], seen.Take(2));
        Assert.Equal(["post tick=True", "window tick=False"], seen.Skip(2).Order());
        Assert.InRange(steps, 1, 5);
        Assert.Equal(0, kiln.Tasks.Live);
        Assert.True(kiln.Run(() => Capabilities.Is("tasks.pool")));
    }

    [Fact]
    public void A_task_with_a_result_and_a_fault_end_as_in_the_game()
    {
        var (kiln, mod) = Start();
        using var _ = kiln;
        var sum = kiln.As(mod, () => mod.Context.Tasks.Run<int>("sum", async ctx => await ctx.OnTick(() => 2) + 3));
        var bad = kiln.As(mod, () => mod.Context.Tasks.Run("bad", (Action<KilnTaskContext>)(ctx => throw new FormatException("bad input"))));
        kiln.Tasks.Settle();
        Assert.Equal((KilnTaskState.Completed, 5), (sum.State, sum.Result));
        Assert.Equal(KilnTaskState.Faulted, bad.State);
        Assert.IsType<FormatException>(bad.Fault);
    }

    [Fact]
    public void Settle_names_a_task_that_never_ends_and_lets_go_once_it_does()
    {
        var (kiln, mod) = Start();
        using var _ = kiln;
        var gate = new ManualResetEventSlim();
        kiln.As(mod, () => mod.Context.Tasks.Run("stuck", ctx => gate.Wait()));
        var e = Assert.Throws<TimeoutException>(() => kiln.Tasks.Settle(timeout: TimeSpan.FromMilliseconds(300)));
        Assert.Contains("tk-events/stuck", e.Message);
        Assert.Contains("a segment is running", e.Message);
        gate.Set();
        Assert.Equal(0, kiln.Tasks.Settle());
    }

    [Fact]
    public void Settle_names_a_task_that_waits_for_ever_without_hanging()
    {
        var (kiln, mod) = Start();
        using var _ = kiln;
        var never = new TaskCompletionSource();
        kiln.As(mod, () => mod.Context.Tasks.Run("waits", async ctx => await never.Task));
        var e = Assert.Throws<TimeoutException>(() => kiln.Tasks.Settle(maxSteps: 20));
        Assert.Contains("after 20 steps", e.Message);
        Assert.Contains("tk-events/waits", e.Message);
        never.SetResult();
        kiln.Tasks.Settle();
        Assert.Equal(0, kiln.Tasks.Live);
    }

    [Fact]
    public void Settle_refuses_the_tick_thread()
    {
        using var kiln = new KilnHarness();
        var e = Assert.Throws<InvalidOperationException>(() => kiln.Run(() => kiln.Tasks.Settle()));
        Assert.Contains("never on the harness's tick thread", e.Message);
        Assert.Equal(0, kiln.Tasks.Settle());
    }

    [Fact]
    public void An_unload_cancels_the_mods_tasks()
    {
        var (kiln, mod) = Start();
        using var _ = kiln;
        var rounds = 0;
        var task = kiln.As(mod, () => mod.Context.Tasks.Run("forever", async ctx =>
        {
            while (true)
            {
                Interlocked.Increment(ref rounds);
                await ctx.NextGateWindow();
            }
        }));
        kiln.Step(3);
        // the round the last step's Post edge released is over, the task waits for the next edge: the unload cancels it
        // there (an unload that meets the round itself makes its NextGateWindow throw "the mod is unloading")
        Assert.True(kiln.TaskPool.TryWaitIdle(TimeSpan.FromSeconds(10)));
        kiln.Unload(mod, checkCollected: false);  // the cancelled task may still be finishing on a worker
        kiln.Tasks.Settle();
        Assert.Equal(KilnTaskState.Cancelled, task.State);
        Assert.True(Volatile.Read(ref rounds) >= 1);
    }

    [Fact]
    public void A_stop_ends_the_hops_still_queued()
    {
        var (kiln, mod) = Start();
        using var _ = kiln;
        var landed = false;
        var task = kiln.As(mod, () => mod.Context.Tasks.Run("late", async ctx =>
        {
            await ctx.ToTick();
            landed = true;
        }));
        kiln.TaskPool.TryWaitIdle(TimeSpan.FromSeconds(10));  // the hop is queued, no step has run it
        kiln.Stop("test");
        kiln.Tasks.Settle();
        Assert.Equal(KilnTaskState.Cancelled, task.State);
        Assert.False(landed);
    }
}
