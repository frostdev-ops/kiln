// SPDX-License-Identifier: MIT
// Client-world hops (#82): a task belongs to the world it was started in, and ToTick / OnTick / PostToTick /
// NextGateWindow go back there; the overloads with a WorldKind name the other world. A task is bound to one session per
// world kind: when that world stops, its pending hops there and every later one end it Cancelled through its own token,
// quietly; a task that is only computing is left alone until then. With no world of a kind it is not bound to, a hop
// there throws KilnUnavailableException and the task stays usable. On the fake pool: the server world on the fake tick
// thread, the client world on the fake main thread.
using Xunit;

// The task bodies stand in for mod code (their own waits, no test token).
#pragma warning disable xUnit1031, xUnit1051

namespace Kiln.Api.Tests;

public sealed class TaskWorldTests : TaskTest
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(20);

    // A hosting client: the server world (session 1, from TaskTest) and a client world.
    private void Hosting() => Pool.StartSession(WorldKind.Client);

    // A client joined to someone else's server: a client world, no server world.
    private void Joined()
    {
        Pool.StopSession();
        Pool.StartSession(WorldKind.Client);
    }

    private KilnTask StartOnTick(ModContext mod, string name, Func<KilnTaskContext, Task> work)
    {
        KilnTask? task = null;
        Pool.OnTick(() => task = mod.Tasks.Run(name, work));
        return task!;
    }

    private KilnTask StartOnMain(ModContext mod, string name, Func<KilnTaskContext, Task> work)
    {
        KilnTask? task = null;
        Pool.OnMain(() => task = mod.Tasks.Run(name, work));
        return task!;
    }

    // Steps both worlds until the task ended (the client world first, as the game's main thread runs beside the server).
    private void StepUntilEnded(KilnTask task, int steps = 50)
    {
        for (var i = 0; i < steps && !task.IsTerminalForTests(); i++)
        {
            Pool.WaitIdle();
            if (Pool.Session(WorldKind.Client) != 0) Pool.ClientStep();
            if (Pool.Session(WorldKind.Server) != 0) Pool.Step();
        }
        Ended(task);
    }

    private List<string> Errors()
    {
        lock (Backend.Lines)
            return [.. Backend.Lines.Where(l => l.StartsWith("Error:", StringComparison.Ordinal) || l.StartsWith("Warn:", StringComparison.Ordinal))];
    }

    [Fact]
    public void A_task_started_on_the_server_tick_goes_back_to_the_server_world()
    {
        Hosting();
        var mod = NewMod("alpha");
        WorldKind home = default;
        var landed = 0;
        var task = StartOnTick(mod, "server-side", async ctx =>
        {
            home = ctx.TickWorld;
            await ctx.ToTick();
            landed = Environment.CurrentManagedThreadId;
        });
        Pool.WaitIdle();
        Pool.ClientStep();  // the client world's step runs none of it
        Assert.Equal(0, landed);
        StepUntilEnded(task);
        Assert.Equal(KilnTaskState.Completed, task.State);
        Assert.Equal(WorldKind.Server, home);
        Assert.Equal(Pool.TickThreadId, landed);
    }

    [Fact]
    public void A_task_started_on_the_main_thread_goes_back_to_the_client_world()
    {
        Hosting();
        var mod = NewMod("alpha");
        WorldKind home = default;
        var landed = new List<int>();
        var task = StartOnMain(mod, "client-side", async ctx =>
        {
            home = ctx.TickWorld;
            await ctx.ToTick();
            landed.Add(Environment.CurrentManagedThreadId);
            await Task.Yield();  // a later await on the client world's step comes back there too
            landed.Add(Environment.CurrentManagedThreadId);
            var n = await ctx.OnTick(() => Environment.CurrentManagedThreadId);  // on its own tick: at once
            landed.Add(n);
        });
        Pool.WaitIdle();
        Pool.Step();  // the server world's step runs none of it
        Assert.Empty(landed);
        StepUntilEnded(task);
        Assert.Equal(KilnTaskState.Completed, task.State);
        Assert.Equal(WorldKind.Client, home);
        Assert.Equal([Pool.MainThreadId, Pool.MainThreadId, Pool.MainThreadId], landed);
    }

    [Fact]
    public void The_overloads_name_the_other_world()
    {
        Hosting();
        var mod = NewMod("alpha");
        var seen = new List<string>();
        var task = StartOnTick(mod, "cross", async ctx =>
        {
            var main = await ctx.OnTick(WorldKind.Client, () => Pool.IsMainThread);
            seen.Add($"OnTick(Client) on main: {main}, back on a worker: {Pool.IsWorker(Environment.CurrentManagedThreadId)}");
            ctx.PostToTick(WorldKind.Client, () => seen.Add($"PostToTick(Client) on main: {Pool.IsMainThread}"));
            await ctx.ToTick(WorldKind.Client);
            seen.Add($"ToTick(Client) on main: {Pool.IsMainThread}");
            await ctx.ToTick();  // its own world: from the client world's step to the server world's
            seen.Add($"ToTick() on the tick: {Pool.IsTickThread}");
        });
        StepUntilEnded(task);
        Assert.Equal(KilnTaskState.Completed, task.State);
        Assert.Equal(
            ["OnTick(Client) on main: True, back on a worker: True", "PostToTick(Client) on main: True",
             "ToTick(Client) on main: True", "ToTick() on the tick: True"], seen);
    }

    [Fact]
    public void A_post_to_the_client_world_keeps_the_task_alive_until_it_was_applied()
    {
        Hosting();
        var mod = NewMod("alpha");
        var applied = false;
        var task = StartOnTick(mod, "post", ctx =>
        {
            ctx.PostToTick(WorldKind.Client, () => applied = true);
            return Task.CompletedTask;
        });
        Pool.WaitIdle();
        Pool.Step();
        Assert.False(task.WaitEnded(TimeSpan.FromMilliseconds(50)));  // its post waits for the client world's step
        Pool.ClientStep();
        Ended(task);
        Assert.True(applied);
        Assert.Equal(KilnTaskState.Completed, task.State);
    }

    [Fact]
    public void A_client_window_wakes_only_after_a_client_step()
    {
        Hosting();
        var mod = NewMod("alpha");
        using var asked = new ManualResetEventSlim();
        var task = StartOnMain(mod, "window", async ctx =>
        {
            var window = ctx.NextGateWindow();  // its world: the client world
            asked.Set();
            await window;
        });
        Assert.True(asked.Wait(Long));
        Pool.WaitIdle();
        Pool.Step();
        Pool.Step();
        Pool.WaitIdle();
        Assert.False(task.WaitEnded(TimeSpan.FromMilliseconds(50)));  // server steps do not end a client window
        Pool.ClientStep();
        Ended(task);
        Assert.Equal(KilnTaskState.Completed, task.State);
    }

    [Fact]
    public void The_prediction_world_is_no_hop_target_and_nothing_is_queued()
    {
        Hosting();
        var mod = NewMod("alpha");
        var thrown = new List<string>();
        var task = StartOnTick(mod, "prediction", ctx =>
        {
            foreach (var call in new Action[]
                     {
                         () => ctx.ToTick(WorldKind.Prediction),
                         () => ctx.OnTick(WorldKind.Prediction, () => 1),
                         () => ctx.PostToTick(WorldKind.Prediction, () => { }),
                         () => ctx.NextGateWindow((WorldKind)7),
                     })
            {
                var e = Record.Exception(call);
                thrown.Add(e?.GetType().Name ?? "none");
            }
            return Task.CompletedTask;
        });
        Ended(task);
        Assert.All(thrown, t => Assert.Equal(nameof(ArgumentOutOfRangeException), t));
        Assert.Equal(KilnTaskState.Completed, task.State);
        Assert.Equal(0, Pool.HopsQueued);
    }

    [Fact]
    public void A_world_that_is_not_there_refuses_at_once_and_leaves_the_task_usable()
    {
        // a dedicated server: no client world
        var mod = NewMod("alpha");
        KilnUnavailableException? client = null;
        var landed = false;
        var task = StartOnTick(mod, "no-client", async ctx =>
        {
            client = Assert.Throws<KilnUnavailableException>(() => ctx.ToTick(WorldKind.Client));
            Assert.False(ctx.Cancellation.IsCancellationRequested);
            await ctx.ToTick();  // its own world still works
            landed = true;
        });
        StepUntilEnded(task);
        Assert.Equal(KilnTaskState.Completed, task.State);
        Assert.True(landed);
        Assert.Equal("tasks.pool", client?.Capability);
        Assert.Contains("client world", client!.Message);

        // a joined client: no server world
        Joined();
        KilnUnavailableException? server = null;
        var main = false;
        var joined = StartOnMain(mod, "no-server", async ctx =>
        {
            server = Assert.Throws<KilnUnavailableException>(() => ctx.ToTick(WorldKind.Server));
            await ctx.ToTick();
            main = Pool.IsMainThread;
        });
        StepUntilEnded(joined);
        Assert.Equal(KilnTaskState.Completed, joined.State);
        Assert.True(main);
        Assert.Contains("server world", server!.Message);
        Assert.Empty(Errors());
    }

    [Fact]
    public void A_client_worlds_stop_ends_its_pending_hops_Cancelled_quietly()
    {
        Hosting();
        var mod = NewMod("alpha");
        var ran = false;
        var finallyOnWorker = false;
        var task = StartOnMain(mod, "pending", async ctx =>
        {
            try
            {
                await ctx.ToTick();
                ran = true;
            }
            finally
            {
                finallyOnWorker = Pool.IsWorker(Environment.CurrentManagedThreadId);
            }
        });
        Pool.WaitIdle();
        Assert.Equal(1, Pool.PendingHopsOf(WorldKind.Client));
        Pool.OnMain(() => Pool.StopSession(WorldKind.Client));  // the client world ends on the main thread
        Ended(task);
        Assert.Equal(KilnTaskState.Cancelled, task.State);
        Assert.False(ran);
        Assert.True(finallyOnWorker);
        Assert.Empty(Errors());
    }

    [Fact]
    public void A_computing_task_is_left_alone_at_its_worlds_stop_and_ends_Cancelled_at_its_next_hop()
    {
        Hosting();
        var mod = NewMod("alpha");
        using var computing = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var cancelledAtStop = false;
        var ran = false;
        var task = StartOnMain(mod, "computing", async ctx =>
        {
            computing.Set();
            go.Wait(Long);
            cancelledAtStop = ctx.Cancellation.IsCancellationRequested;
            await ctx.ToTick();
            ran = true;
        });
        Assert.True(computing.Wait(Long));
        Pool.OnMain(() => Pool.StopSession(WorldKind.Client));
        Pool.OnMain(() => Pool.StartSession(WorldKind.Client));  // the player joins another world meanwhile
        go.Set();
        Ended(task);
        Pool.ClientStep();
        Assert.False(cancelledAtStop);  // lazy: the stop did not cancel a task that had nothing there
        Assert.False(ran);  // never in the next client world
        Assert.Equal(KilnTaskState.Cancelled, task.State);
        Assert.Equal(0, Pool.PendingHopsOf(WorldKind.Client));
        Assert.Empty(Errors());
    }

    // The re-host bug (red before #82): a task that computes across a hosted world's stop and a new hosted world ran its
    // next hop in the new world. Only the API that existed before is used here.
    [Fact]
    public void A_task_computing_across_a_rehost_never_hops_into_the_new_world()
    {
        var mod = NewMod("alpha");
        using var computing = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var ranInNewWorld = false;
        KilnTask? task = null;
        Pool.OnTick(() => task = mod.Tasks.Run("rehost", async ctx =>
        {
            computing.Set();
            go.Wait(Long);
            await ctx.ToTick();
            ranInNewWorld = true;
        }));
        Assert.True(computing.Wait(Long));
        Pool.StopSession();   // the hosted world ends; the mod (a "both" mod) stays loaded
        Pool.StartSession();  // the player hosts again
        go.Set();
        for (var i = 0; i < 5 && !task!.WaitEnded(TimeSpan.FromMilliseconds(20)); i++)
        {
            Pool.WaitIdle();
            Pool.Step();
        }
        Ended(task!);
        Assert.False(ranInNewWorld);
        Assert.Equal(KilnTaskState.Cancelled, task!.State);
        Assert.Empty(Errors());
    }

    [Fact]
    public void A_task_started_off_every_world_thread_belongs_to_the_first_server_world_it_reaches()
    {
        var mod = NewMod("alpha");
        using var go = new ManualResetEventSlim();
        var hops = 0;
        WorldKind home = default;
        var task = mod.Tasks.Run("off-thread", async ctx =>  // the test's own thread: no world's thread
        {
            home = ctx.TickWorld;
            await ctx.ToTick();
            hops++;
            await ctx.NextGateWindow();
            go.Wait(Long);
            await ctx.ToTick();
            hops++;
        });
        Pool.WaitIdle();
        Pool.Step();  // the first hop binds it to server session 1; the window it asks there opens at this step's edge
        Assert.True(SpinWait.SpinUntil(() => hops == 1, Long));
        Pool.StopSession();
        Pool.StartSession();
        go.Set();
        for (var i = 0; i < 5 && !task.WaitEnded(TimeSpan.FromMilliseconds(20)); i++)
        {
            Pool.WaitIdle();
            Pool.Step();
        }
        Ended(task);
        Assert.Equal(WorldKind.Server, home);
        Assert.Equal(1, hops);
        Assert.Equal(KilnTaskState.Cancelled, task.State);
    }

    [Fact]
    public void A_task_started_in_a_task_takes_its_parents_world()
    {
        Hosting();
        var mod = NewMod("alpha");
        WorldKind inSegment = default, inServerHop = default;
        var task = StartOnMain(mod, "parent", async ctx =>
        {
            var a = mod.Tasks.Run("child-a", c =>
            {
                inSegment = c.TickWorld;
                return Task.CompletedTask;
            });
            await a;
            await ctx.ToTick(WorldKind.Server);
            var b = mod.Tasks.Run("child-b", c =>
            {
                inServerHop = c.TickWorld;
                return Task.CompletedTask;
            });
            await ctx.NextGateWindow();
            await b;
        });
        StepUntilEnded(task);
        Assert.Equal(KilnTaskState.Completed, task.State);
        Assert.Equal(WorldKind.Client, inSegment);
        Assert.Equal(WorldKind.Server, inServerHop);
    }

    [Fact]
    public void Wait_is_refused_on_the_games_main_thread()
    {
        Hosting();
        var mod = NewMod("alpha");
        using var go = new ManualResetEventSlim();
        var task = mod.Tasks.Run("waited", _ => go.Wait(Long));
        InvalidOperationException? refused = null;
        Pool.OnMain(() => refused = Assert.Throws<InvalidOperationException>(() => task.Wait()));
        go.Set();
        Ended(task);
        Assert.Contains("main thread", refused!.Message);
    }

    [Fact]
    public void An_unload_with_client_hops_pending_ends_the_tasks_Cancelled_quietly()
    {
        Hosting();
        var mod = NewMod("alpha");
        var ran = false;
        var task = StartOnMain(mod, "unloaded", async ctx =>
        {
            await ctx.ToTick();
            ran = true;
        });
        Pool.WaitIdle();
        Assert.Equal(1, Pool.PendingHopsOf(WorldKind.Client));
        mod.BeginUnload();
        Pool.ClientStep();
        Ended(task);
        Assert.False(ran);
        Assert.Equal(KilnTaskState.Cancelled, task.State);
        Assert.Empty(Errors());
    }

    [Fact]
    public void A_task_started_in_a_worlds_stopping_handlers_never_reaches_the_next_world()
    {
        var mod = NewMod("alpha");
        KilnTask? task = null;
        Pool.OnTick(() =>
        {
            Pool.StopSession();
            Pool.SetStopping(WorldKind.Server, true);  // ServerStopping handlers run: the world still exists
            task = mod.Tasks.Run("at-stop", async ctx => await ctx.ToTick());
            Pool.SetStopping(WorldKind.Server, false);
        });
        Pool.WaitIdle();
        Pool.StartSession();
        Pool.Step();
        Ended(task!);
        Assert.Equal(KilnTaskState.Cancelled, task!.State);
        Assert.Equal(0, Pool.PendingHops);
    }
}

internal static class KilnTaskTestExtensions
{
    public static bool IsTerminalForTests(this KilnTask task) =>
        task.State is KilnTaskState.Completed or KilnTaskState.Faulted or KilnTaskState.Cancelled;
}
