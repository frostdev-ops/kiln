// SPDX-License-Identifier: MIT
// The ways back to the tick over the fake pool: ToTick (a real hop to the tick thread, the fast path, a hop that loses to
// a cancel, an unload or the world's end), OnTick, PostToTick (accounting, faults, cancellation) and NextGateWindow
// (Post edges, STOPPING, a new session, never inline on a game thread), the refusals without a live server world, and
// each way to the tick while the mod unloads (the task's own work ends Cancelled, with nothing logged; code outside the
// task is refused loudly).
using Xunit;

// The task bodies stand in for mod code (their own delays, no test token), and the test thread is the mod's own thread,
// where blocking on a finished task is allowed.
#pragma warning disable xUnit1031, xUnit1051

namespace Kiln.Api.Tests;

public sealed class TaskTickTests : TaskTest
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(20);

    // ---- ToTick ----

    [Fact]
    public void ToTick_from_a_worker_really_continues_on_the_tick()
    {
        var mod = NewMod("alpha");
        int before = 0, after = 0;
        ModContext? modOnTick = null;
        var onTickScope = false;
        var task = mod.Tasks.Run("hop", async ctx =>
        {
            before = Environment.CurrentManagedThreadId;
            await ctx.ToTick();
            after = Environment.CurrentManagedThreadId;
            modOnTick = ModContext.CurrentMod;
            onTickScope = TaskExecutionScope.OnTick;
        });
        Pool.WaitIdle();
        Assert.Equal(1, Pool.PendingHops);
        Assert.Equal(KilnTaskState.Running, task.State);  // suspended, holding no worker
        Pool.Step();
        Ended(task);
        Assert.Equal(KilnTaskState.Completed, task.State);
        Assert.True(Pool.IsWorker(before));
        Assert.Equal(Pool.TickThreadId, after);
        Assert.Same(mod, modOnTick);
        Assert.True(onTickScope);
    }

    [Fact]
    public void ToTick_on_the_tasks_own_tick_is_the_fast_path()
    {
        var mod = NewMod("alpha");
        var fast = false;
        var threads = new List<int>();
        var task = mod.Tasks.Run("twice", async ctx =>
        {
            await ctx.ToTick();
            threads.Add(Environment.CurrentManagedThreadId);
            fast = ctx.ToTick().GetAwaiter().IsCompleted;
            await ctx.ToTick();
            threads.Add(Environment.CurrentManagedThreadId);
            Assert.Equal(7, await ctx.OnTick(() => 7));  // on its own tick OnTick runs at once too
            threads.Add(Environment.CurrentManagedThreadId);
        });
        Pool.WaitIdle();
        Pool.Step();
        Ended(task);
        Assert.Equal(KilnTaskState.Completed, task.State);
        Assert.True(fast);
        Assert.Equal(1, Pool.HopsQueued);  // one hop for three awaits
        Assert.All(threads, t => Assert.Equal(Pool.TickThreadId, t));
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        public int Posts;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref Posts);
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }
    }

    [Fact]
    public void ToTick_goes_to_the_tick_whatever_context_was_captured()
    {
        var mod = NewMod("alpha");
        var custom = new RecordingContext();
        var after = 0;
        var task = mod.Tasks.Run("custom", async ctx =>
        {
            SynchronizationContext.SetSynchronizationContext(custom);
            await ctx.ToTick();
            after = Environment.CurrentManagedThreadId;
        });
        Pool.WaitIdle();
        Pool.Step();
        Ended(task);
        Assert.Equal(Pool.TickThreadId, after);
        Assert.Equal(0, custom.Posts);
    }

    [Fact]
    public void Awaits_after_ToTick_come_back_to_the_tick()
    {
        var mod = NewMod("alpha");
        var threads = new List<int>();
        var task = mod.Tasks.Run("stays", async ctx =>
        {
            await ctx.ToTick();
            threads.Add(Environment.CurrentManagedThreadId);
            await Task.Yield();
            threads.Add(Environment.CurrentManagedThreadId);
            await Task.Delay(5);
            threads.Add(Environment.CurrentManagedThreadId);
        });
        for (var i = 0; i < 50 && !task.WaitEnded(TimeSpan.FromMilliseconds(20)); i++)
        {
            Pool.WaitIdle();
            Pool.Step();
        }
        Ended(task);
        Assert.Equal(3, threads.Count);
        Assert.All(threads, t => Assert.Equal(Pool.TickThreadId, t));
    }

    public enum Loser { Cancel, Unload, WorldStops }

    [Theory]
    [InlineData(Loser.Cancel)]
    [InlineData(Loser.Unload)]
    [InlineData(Loser.WorldStops)]
    public void A_queued_hop_that_loses_runs_nothing_on_the_tick_and_unwinds_on_a_worker(Loser loser)
    {
        var mod = NewMod("alpha");
        var ranOnTick = false;
        var finallyThread = 0;
        Exception? thrown = null;
        var task = mod.Tasks.Run("loses", async ctx =>
        {
            try
            {
                await ctx.ToTick();
                ranOnTick = true;
            }
            catch (Exception e)
            {
                thrown = e;
                throw;
            }
            finally
            {
                finallyThread = Environment.CurrentManagedThreadId;
            }
        });
        Pool.WaitIdle();
        Assert.Equal(1, Pool.PendingHops);
        switch (loser)
        {
            case Loser.Cancel:
                task.Cancel();
                Pool.Step();
                break;
            case Loser.Unload:
                mod.BeginUnload();
                Pool.Step();
                break;
            case Loser.WorldStops:
                Pool.StopSession();  // its cancelled hops run on this thread, the one that ends the world
                break;
        }
        Ended(task);
        Assert.False(ranOnTick);
        Assert.True(Pool.IsWorker(finallyThread));
        var oce = Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.True(oce.CancellationToken.IsCancellationRequested);
        Assert.Equal(KilnTaskState.Cancelled, task.State);
    }

    [Fact]
    public void A_cancelled_task_never_hops()
    {
        var mod = NewMod("alpha");
        using var started = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var task = mod.Tasks.Run("cancelled-first", async ctx =>
        {
            started.Set();
            go.Wait(Long);
            Assert.False(ctx.ToTick().GetAwaiter().IsCompleted);
            await ctx.ToTick();
        });
        Assert.True(started.Wait(Long));
        task.Cancel();
        go.Set();
        Ended(task);
        Assert.Equal(KilnTaskState.Cancelled, task.State);
        Assert.Equal(0, Pool.HopsQueued);
    }

    // ---- OnTick ----

    [Fact]
    public void OnTick_runs_once_on_the_tick_and_resumes_in_the_task()
    {
        var mod = NewMod("alpha");
        var applies = 0;
        var applyThread = 0;
        TaskRun? before = null, after = null;
        var afterThread = 0;
        var task = mod.Tasks.Run<int>("ask", async ctx =>
        {
            before = TaskExecutionScope.Current;
            var n = await ctx.OnTick(() =>
            {
                Interlocked.Increment(ref applies);
                applyThread = Environment.CurrentManagedThreadId;
                return 41;
            });
            after = TaskExecutionScope.Current;
            afterThread = Environment.CurrentManagedThreadId;
            return n + 1;
        });
        Pool.WaitIdle();
        Assert.Equal(0, applies);
        Pool.Step();
        Ended(task);
        Assert.Equal(42, task.Result);
        Assert.Equal(1, applies);
        Assert.Equal(Pool.TickThreadId, applyThread);
        Assert.True(Pool.IsWorker(afterThread));
        Assert.Same(before, after);
        Assert.False(TaskExecutionScope.OnTick);
    }

    [Fact]
    public void An_OnTick_fault_comes_back_through_its_Task()
    {
        var mod = NewMod("alpha");
        Exception? seen = null;
        var task = mod.Tasks.Run("ask", async ctx =>
        {
            try
            {
                await ctx.OnTick<int>(() => throw new FormatException("tick"));
            }
            catch (FormatException e)
            {
                seen = e;
            }
        });
        Pool.WaitIdle();
        Pool.Step();
        Ended(task);
        Assert.Equal("tick", seen?.Message);
        Assert.Equal(KilnTaskState.Completed, task.State);  // the work handled it
    }

    [Fact]
    public void An_OnTick_cancelled_before_it_ran_never_runs()
    {
        var mod = NewMod("alpha");
        var applied = false;
        var task = mod.Tasks.Run("ask", async ctx => await ctx.OnTick(() => applied = true));
        Pool.WaitIdle();
        task.Cancel();
        Pool.Step();
        Ended(task);
        Assert.False(applied);
        Assert.Equal(KilnTaskState.Cancelled, task.State);
    }

    // ---- PostToTick ----

    [Fact]
    public void PostToTick_keeps_the_task_alive_until_its_posts_are_applied()
    {
        var mod = NewMod("alpha");
        var applied = new List<(int Value, int Thread, ModContext? Mod)>();
        var task = mod.Tasks.Run("posts", ctx =>
        {
            for (var i = 0; i < 3; i++)
            {
                var v = i;
                ctx.PostToTick(() => applied.Add((v, Environment.CurrentManagedThreadId, ModContext.CurrentMod)));
            }
        });
        Pool.WaitIdle();
        Assert.Equal(3, Pool.PendingHops);
        Assert.Equal(KilnTaskState.Running, task.State);  // the work returned; its posts are pending
        Assert.False(task.WaitEnded(TimeSpan.FromMilliseconds(20)));
        Pool.Step();
        Ended(task);
        Assert.Equal(KilnTaskState.Completed, task.State);
        Assert.Equal(new[] { 0, 1, 2 }, applied.Select(a => a.Value));
        Assert.All(applied, a => Assert.Equal(Pool.TickThreadId, a.Thread));
        Assert.All(applied, a => Assert.Same(mod, a.Mod));
    }

    [Fact]
    public void A_post_that_throws_faults_the_task()
    {
        var mod = NewMod("alpha");
        var boom = new FormatException("apply");
        var task = mod.Tasks.Run("posts", ctx => ctx.PostToTick(() => throw boom));
        Pool.WaitIdle();
        Pool.Step();
        Ended(task);
        Assert.Equal(KilnTaskState.Faulted, task.State);
        Assert.Same(boom, task.Fault);
    }

    [Fact]
    public void Cancelling_drops_unapplied_posts_and_the_task_ends_Cancelled_even_after_a_normal_return()
    {
        var mod = NewMod("alpha");
        var applied = 0;
        var task = mod.Tasks.Run("posts", ctx =>
        {
            ctx.PostToTick(() => applied++);
            ctx.PostToTick(() => applied++);
        });
        Pool.WaitIdle();
        task.Cancel();
        Pool.Step();
        Ended(task);
        Assert.Equal(0, applied);
        Assert.Equal(KilnTaskState.Cancelled, task.State);  // never Completed for work that did not happen
        Assert.ThrowsAny<OperationCanceledException>(() => task.GetAwaiter().GetResult());
    }

    [Fact]
    public void The_world_stopping_drops_accepted_posts_and_cancels_the_task()
    {
        var mod = NewMod("alpha");
        var applied = false;
        var task = mod.Tasks.Run("posts", ctx => ctx.PostToTick(() => applied = true));
        Pool.WaitIdle();
        Pool.StopSession();
        Ended(task);
        Assert.False(applied);
        Assert.Equal(KilnTaskState.Cancelled, task.State);
    }

    [Fact]
    public void PostToTick_after_a_cancel_throws_and_queues_nothing()
    {
        var mod = NewMod("alpha");
        using var started = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        Exception? thrown = null;
        var task = mod.Tasks.Run("late-post", ctx =>
        {
            started.Set();
            go.Wait(Long);
            try
            {
                ctx.PostToTick(() => { });
            }
            catch (Exception e)
            {
                thrown = e;
                throw;
            }
        });
        Assert.True(started.Wait(Long));
        task.Cancel();
        go.Set();
        Ended(task);
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.Equal(0, Pool.HopsQueued);
        Assert.Equal(KilnTaskState.Cancelled, task.State);
    }

    // ---- no live server world ----

    [Fact]
    public void Without_a_live_server_world_every_way_to_the_tick_refuses_at_once_naming_tasks_pool()
    {
        var mod = NewMod("alpha");
        Pool.StopSession();  // the main menu, or a client joined to someone else's server
        var refusals = new List<Exception>();
        var task = mod.Tasks.Run("menu", ctx =>
        {
            foreach (var call in new Action[]
                     {
                         () => ctx.ToTick(),
                         () => ctx.OnTick(() => 1),
                         () => ctx.PostToTick(() => { }),
                         () => ctx.NextGateWindow(),
                     })
            {
                try
                {
                    call();
                }
                catch (Exception e)
                {
                    refusals.Add(e);
                }
            }
        });
        Ended(task);
        Assert.Equal(KilnTaskState.Completed, task.State);
        Assert.Equal(4, refusals.Count);
        Assert.All(refusals, e => Assert.Equal("tasks.pool", Assert.IsType<KilnUnavailableException>(e).Capability));
        Assert.Equal(0, Pool.HopsQueued);  // nothing was queued, so nothing was dropped
    }

    // ---- NextGateWindow ----

    [Fact]
    public void NextGateWindow_resumes_on_a_worker_after_the_next_Post_edge()
    {
        var mod = NewMod("alpha");
        var edgeAtResume = -1;
        var resumeThread = 0;
        var task = mod.Tasks.Run("retry", async ctx =>
        {
            await ctx.NextGateWindow();
            edgeAtResume = Pool.Edges;
            resumeThread = Environment.CurrentManagedThreadId;
        });
        Pool.WaitIdle();
        Assert.Equal(-1, edgeAtResume);  // waits for an edge, holding no worker
        Assert.Equal(KilnTaskState.Running, task.State);
        Pool.Step();
        Ended(task);
        Assert.Equal(1, edgeAtResume);
        Assert.True(Pool.IsWorker(resumeThread));  // never inline on the tick's Post
        Assert.Equal(KilnTaskState.Completed, task.State);
    }

    [Fact]
    public void NextGateWindow_from_the_tick_takes_the_work_back_to_a_worker()
    {
        var mod = NewMod("alpha");
        var threads = new List<int>();
        var task = mod.Tasks.Run("back", async ctx =>
        {
            await ctx.ToTick();
            threads.Add(Environment.CurrentManagedThreadId);
            await ctx.NextGateWindow();
            threads.Add(Environment.CurrentManagedThreadId);
        });
        Pool.WaitIdle();
        Pool.Step();  // the hop runs before Pre; the window it asks for opens at this step's Post edge
        Ended(task);
        Assert.Equal(2, threads.Count);
        Assert.Equal(Pool.TickThreadId, threads[0]);
        Assert.True(Pool.IsWorker(threads[1]));
    }

    [Fact]
    public void NextGateWindow_and_STOPPING_completes_cancelled_and_never_wakes_in_a_new_session()
    {
        var mod = NewMod("alpha");
        var resumedInWindow = false;
        Exception? thrown = null;
        var task = mod.Tasks.Run("stopping", async ctx =>
        {
            try
            {
                await ctx.NextGateWindow();
                resumedInWindow = true;
            }
            catch (Exception e)
            {
                thrown = e;
                throw;
            }
        });
        Pool.WaitIdle();
        Pool.StopSession();
        Pool.StartSession();
        Ended(task);
        Pool.Step();
        Assert.False(resumedInWindow);
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.Equal(KilnTaskState.Cancelled, task.State);
    }

    [Fact]
    public void A_window_asked_for_in_one_session_is_never_moved_into_the_next()
    {
        var mod = NewMod("alpha");
        using var asked = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var resumedInWindow = false;
        var task = mod.Tasks.Run("turnover", async ctx =>
        {
            var window = ctx.NextGateWindow();  // session 1
            asked.Set();
            go.Wait(Long);                      // the world restarts before the await arms it
            await window;
            resumedInWindow = true;
        });
        Assert.True(asked.Wait(Long));
        Pool.StopSession();
        Pool.StartSession();
        go.Set();
        Ended(task);
        Pool.Step();
        Assert.False(resumedInWindow);
        Assert.Equal(KilnTaskState.Cancelled, task.State);
    }

    [Fact]
    public void Cancelling_a_task_that_waits_for_a_window_resumes_it_cancelled_at_once()
    {
        var mod = NewMod("alpha");
        var thread = 0;
        var task = mod.Tasks.Run("cancel-window", async ctx =>
        {
            try
            {
                await ctx.NextGateWindow();
            }
            finally
            {
                thread = Environment.CurrentManagedThreadId;
            }
        });
        Pool.WaitIdle();
        task.Cancel();  // no edge needed
        Ended(task);
        Assert.Equal(KilnTaskState.Cancelled, task.State);
        Assert.True(Pool.IsWorker(thread));
        Assert.Equal(0, Pool.Edges);
    }

    [Fact]
    public void A_task_looping_on_windows_resumes_once_per_edge()
    {
        var mod = NewMod("alpha");
        var rounds = 0;
        var task = mod.Tasks.Run<int>("loop", async ctx =>
        {
            while (rounds < 3)
            {
                await ctx.NextGateWindow();
                rounds++;
            }
            return rounds;
        });
        for (var i = 0; i < 3; i++)
        {
            Pool.WaitIdle();
            Assert.Equal(i, rounds);
            Pool.Step();
        }
        Ended(task);
        Assert.Equal(3, task.Result);
    }

    // ---- the context is the task's own ----

    private static void Called(Task _) { }  // the call itself throws: no Task comes back

    [Fact]
    public void A_KilnTaskContext_kept_after_the_work_ended_refuses()
    {
        var mod = NewMod("alpha");
        KilnTaskContext? kept = null;
        var task = mod.Tasks.Run("keeps", ctx => kept = ctx);
        Ended(task);
        Assert.Throws<InvalidOperationException>(() => kept!.PostToTick(() => { }));
        Assert.Throws<InvalidOperationException>(() => kept!.ToTick());
        Assert.Throws<InvalidOperationException>(() => Called(kept!.OnTick(() => 1)));
        Assert.Throws<InvalidOperationException>(() => kept!.NextGateWindow());
        Assert.Equal(0, Pool.HopsQueued);
    }

    // ---- a way to the tick while the mod unloads ----

    public enum Hop { ToTick, OnTick, PostToTick, NextGateWindow }

    // The next round of a loop: one call of one way to the tick.
    private static async Task NextRound(KilnTaskContext ctx, Hop hop)
    {
        switch (hop)
        {
            case Hop.ToTick:
                await ctx.ToTick();
                break;
            case Hop.OnTick:
                await ctx.OnTick(() => 1);
                break;
            case Hop.PostToTick:
                ctx.PostToTick(() => { });
                break;
            case Hop.NextGateWindow:
                await ctx.NextGateWindow();
                break;
        }
    }

    private void NoFaultLogged()
    {
        Pool.WaitIdle();  // the task's last segment, which would log a fault, has returned
        lock (Backend.Lines) Assert.DoesNotContain(Backend.Lines, l => l.Contains("faulted", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Hop.ToTick, false)]
    [InlineData(Hop.OnTick, false)]
    [InlineData(Hop.PostToTick, false)]
    [InlineData(Hop.NextGateWindow, false)]
    [InlineData(Hop.ToTick, true)]
    [InlineData(Hop.OnTick, true)]
    [InlineData(Hop.PostToTick, true)]
    [InlineData(Hop.NextGateWindow, true)]
    public void A_loop_that_hops_while_its_mod_unloads_ends_Cancelled_by_its_own_token_and_logs_nothing(Hop hop, bool worldGone)
    {
        var mod = NewMod("alpha");
        using var inRound = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        Exception? thrown = null;
        var token = CancellationToken.None;
        var finallyThread = 0;
        var task = mod.Tasks.Run("loop", async ctx =>
        {
            token = ctx.Cancellation;
            try
            {
                while (!ctx.Cancellation.IsCancellationRequested)
                {
                    inRound.Set();
                    go.Wait(Long);  // the unload lands between the loop's check and its next hop
                    await NextRound(ctx, hop);
                }
            }
            catch (Exception e)
            {
                thrown = e;
                throw;
            }
            finally
            {
                finallyThread = Environment.CurrentManagedThreadId;
            }
        });
        Assert.True(inRound.Wait(Long));
        if (worldGone) Pool.StopSession();  // a hosting client leaving its world: the world stops, then its server-side mods unload
        mod.BeginUnload();
        go.Set();
        Ended(task);
        Assert.Equal(KilnTaskState.Cancelled, task.State);
        var oce = Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.Equal(token, oce.CancellationToken);
        Assert.True(Pool.IsWorker(finallyThread));
        Assert.Equal(0, Pool.HopsQueued);  // nothing was handed to the tick
        NoFaultLogged();
    }

    [Theory]
    [InlineData(Hop.ToTick)]
    [InlineData(Hop.OnTick)]
    [InlineData(Hop.PostToTick)]
    [InlineData(Hop.NextGateWindow)]
    public void A_hop_after_the_unload_began_but_before_it_signalled_the_task_still_ends_it_Cancelled(Hop hop)
    {
        var mod = NewMod("alpha");
        using var inRound = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var token = CancellationToken.None;
        var task = mod.Tasks.Run("loop", async ctx =>
        {
            token = ctx.Cancellation;
            inRound.Set();
            go.Wait(Long);
            await NextRound(ctx, hop);
        });
        Assert.True(inRound.Wait(Long));
        mod.UnloadTaskSeal = null;  // the moment between the unload's start and its signal to the mod's tasks
        mod.BeginUnload();
        Assert.False(token.IsCancellationRequested);
        go.Set();
        Ended(task);
        Assert.Equal(KilnTaskState.Cancelled, task.State);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(0, Pool.HopsQueued);
        NoFaultLogged();
    }

    [Fact]
    public void A_KilnTaskContext_used_outside_its_task_while_the_mod_unloads_throws_InvalidOperationException()
    {
        var mod = NewMod("alpha");
        KilnTaskContext? kept = null;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = mod.Tasks.Run("holds", async ctx =>
        {
            kept = ctx;
            await hold.Task;  // suspended: its work has not ended
        });
        Pool.WaitIdle();
        mod.BeginUnload();
        // this thread is not the task's work: the mod's own mistake stays loud
        var refused = new List<InvalidOperationException>
        {
            Assert.Throws<InvalidOperationException>(() => kept!.ToTick()),
            Assert.Throws<InvalidOperationException>(() => Called(kept!.OnTick(() => 1))),
            Assert.Throws<InvalidOperationException>(() => kept!.PostToTick(() => { })),
            Assert.Throws<InvalidOperationException>(() => kept!.NextGateWindow()),
        };
        Assert.All(refused, e => Assert.Contains("the mod is unloading", e.Message));
        Assert.Equal(0, Pool.HopsQueued);
        hold.SetResult();
        Ended(task);
        Assert.Equal(KilnTaskState.Completed, task.State);  // it returned normally after the signal
    }

    [Fact]
    public void Work_that_left_the_task_and_hops_while_the_mod_unloads_faults_the_task_loudly()
    {
        var mod = NewMod("alpha");
        using var left = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var task = mod.Tasks.Run("leaves", async ctx =>
        {
            await Task.Delay(1).ConfigureAwait(false);  // the rest runs on a .NET thread-pool thread, outside the task
            left.Set();
            go.Wait(Long);
            await ctx.NextGateWindow();
        });
        Assert.True(left.Wait(Long));
        mod.BeginUnload();
        go.Set();
        Ended(task);
        Assert.Equal(KilnTaskState.Faulted, task.State);
        Assert.Contains("the mod is unloading", Assert.IsType<InvalidOperationException>(task.Fault).Message);
        Assert.True(SpinWait.SpinUntil(() => Backend.Logged("task 'leaves' faulted"), 20000));
    }

    [Theory]
    [InlineData(Hop.ToTick)]
    [InlineData(Hop.OnTick)]
    [InlineData(Hop.PostToTick)]
    [InlineData(Hop.NextGateWindow)]
    public void A_child_task_that_hops_with_its_parents_KilnTaskContext_while_the_mod_unloads_faults_loudly(Hop hop)
    {
        var mod = NewMod("alpha");
        KilnTaskContext? parentContext = null;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parent = mod.Tasks.Run("parent", async ctx =>
        {
            parentContext = ctx;
            await hold.Task;  // suspended: its work has not ended
        });
        Pool.WaitIdle();
        using var inChild = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var child = mod.Tasks.Run("child", async ctx =>
        {
            inChild.Set();
            go.Wait(Long);
            await NextRound(parentContext!, hop);  // the parent's context, inside another task's work: the mod's mistake
        });
        Assert.True(inChild.Wait(Long));
        mod.BeginUnload();
        go.Set();
        Ended(child);
        Assert.Equal(KilnTaskState.Faulted, child.State);
        var refused = Assert.IsType<InvalidOperationException>(child.Fault);
        Assert.Contains("this is the task 'child', not 'parent'", refused.Message);
        Assert.Equal(0, Pool.HopsQueued);
        Assert.True(SpinWait.SpinUntil(() => Backend.Logged("task 'child' faulted"), 20000));
        hold.SetResult();
        Ended(parent);
        Assert.Equal(KilnTaskState.Completed, parent.State);  // the child's misuse ended nothing of the parent
    }

    [Theory]
    [InlineData(Hop.ToTick, false)]
    [InlineData(Hop.OnTick, false)]
    [InlineData(Hop.PostToTick, false)]
    [InlineData(Hop.NextGateWindow, false)]
    [InlineData(Hop.ToTick, true)]
    [InlineData(Hop.OnTick, true)]
    [InlineData(Hop.PostToTick, true)]
    [InlineData(Hop.NextGateWindow, true)]
    public void A_hop_after_a_continuation_that_resumed_while_the_mod_unloads_ends_the_task_Cancelled(Hop hop, bool fromTick)
    {
        var mod = NewMod("alpha");
        using var waiting = new ManualResetEventSlim();
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? thrown = null;
        var token = CancellationToken.None;
        var resumedInTask = false;
        var task = mod.Tasks.Run("resumes", async ctx =>
        {
            token = ctx.Cancellation;
            try
            {
                if (fromTick) await ctx.ToTick();  // the await below then captures the task's tick context
                waiting.Set();
                await resume.Task.ConfigureAwait(true);  // resumes during the unload, in the task (a worker)
                resumedInTask = TaskExecutionScope.Current is not null && !TaskExecutionScope.OnTick;
                await NextRound(ctx, hop);
            }
            catch (Exception e)
            {
                thrown = e;
                throw;
            }
        });
        Assert.True(SpinWait.SpinUntil(() =>
        {
            if (fromTick) Pool.Step();  // the hop to the tick, once the worker has queued it
            return waiting.IsSet;
        }, 20000));
        Pool.WaitIdle();
        mod.BeginUnload();
        resume.SetResult();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            if (!task.IsTerminal) Pool.Step();  // a continuation queued for the tick is dispatched cancelled there
            return task.IsTerminal;
        }, 20000), $"the task did not end ({task.State})");
        Assert.Equal(KilnTaskState.Cancelled, task.State);
        Assert.True(resumedInTask);
        Assert.Equal(token, Assert.IsAssignableFrom<OperationCanceledException>(thrown).CancellationToken);
        NoFaultLogged();
    }
}
