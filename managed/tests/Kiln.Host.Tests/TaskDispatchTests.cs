// SPDX-License-Identifier: MIT
// Where a task's pieces really run: segments on pool workers, tick hops on the server tick, each inside the task's own
// scope and synchronization context, with whatever the thread had before restored afterwards.
using Kiln.Host;
using Xunit;

#pragma warning disable xUnit1031, xUnit1051

namespace Kiln.Host.Tests;

public sealed class TaskDispatchTests : HostTaskTest
{
    private sealed record Where(int Thread, bool Pool, bool Tick, ModContext? Mod, bool Scope);

    private Where Here() => new(Environment.CurrentManagedThreadId, Tasks.OnPoolThread, Tasks.OnServerTick, ModContext.CurrentMod,
        TaskExecutionScope.Current is not null);

    [Fact]
    public void Segments_run_on_pool_threads_and_hops_on_the_tick()
    {
        var mod = NewMod("alpha");
        var test = Environment.CurrentManagedThreadId;
        Where? start = null, afterAwait = null, onTick = null, inOnTick = null, afterOnTick = null, afterWindow = null;
        var task = mod.Tasks.Run("route", async ctx =>
        {
            start = Here();
            await Task.Delay(5);
            afterAwait = Here();
            await ctx.ToTick();
            onTick = Here();
            await ctx.NextGateWindow();
            afterWindow = Here();
            afterOnTick = null;
            var value = await ctx.OnTick(() =>
            {
                inOnTick = Here();
                return 5;
            });
            afterOnTick = Here();
            return value;
        });
        Assert.True(Until(() => Native.PendingHops == 1));
        Native.Step();  // the ToTick hop runs; its end asks for the next window, which this step's Post edge opens
        Assert.True(Until(() => Native.PendingHops == 1 || task.IsTerminal));
        Native.Step();  // OnTick
        Ended(task);
        Assert.Equal(KilnTaskState.Completed, task.State);
        Assert.Equal(5, task.Result);
        foreach (var w in new[] { start, afterAwait, afterWindow, afterOnTick })
        {
            Assert.NotNull(w);
            Assert.True(Native.IsWorker(w.Thread), "a segment runs on a pool worker");
            Assert.True(w.Pool && !w.Tick && w.Scope);
            Assert.Same(mod, w.Mod);
            Assert.NotEqual(test, w.Thread);
            Assert.NotEqual(Native.TickThreadId, w.Thread);
        }
        foreach (var w in new[] { onTick, inOnTick })
        {
            Assert.NotNull(w);
            Assert.Equal(Native.TickThreadId, w.Thread);
            Assert.True(w.Tick && !w.Pool && w.Scope);
            Assert.Same(mod, w.Mod);
        }
        lock (Hops) Assert.Equal([(mod.Owner, true), (mod.Owner, true)], Hops);  // both hops went through the tick dispatcher
        Quiesce();
    }

    [Fact]
    public void The_thread_facts_come_from_the_core()
    {
        Assert.False(Tasks.OnPoolThread);
        Assert.False(Tasks.OnServerTick);
        Assert.Equal((ulong)Native.Session, Tasks.ServerSession);
        var onTick = false;
        Native.OnTick(() => onTick = Tasks.OnServerTick && !Tasks.OnPoolThread);
        Assert.True(onTick);
        Native.StopSession();
        Assert.Equal(0ul, Tasks.ServerSession);
        Native.StartSession();
        Assert.Equal((ulong)Native.Session, Tasks.ServerSession);
    }

    [Fact]
    public void Two_mods_interleave_on_one_worker_without_leaking_each_others_scope()
    {
        Use(1);
        var a = NewMod("alpha");
        var b = NewMod("beta");
        var wrong = 0;
        KilnTask Start(ModContext mod, string name) => mod.Tasks.Run(name, async ctx =>
        {
            for (var i = 0; i < 20; i++)
            {
                if (ModContext.CurrentMod != mod || TaskExecutionScope.Current?.Name != name) Interlocked.Increment(ref wrong);
                await Task.Yield();
                if (ModContext.CurrentMod != mod || TaskExecutionScope.Current?.Name != name) Interlocked.Increment(ref wrong);
                ctx.PostToTick(() =>
                {
                    if (ModContext.CurrentMod != mod || !TaskExecutionScope.OnTick) Interlocked.Increment(ref wrong);
                });
            }
        });
        var ta = Start(a, "a-work");
        var tb = Start(b, "b-work");
        Assert.True(Until(() =>
        {
            Native.Step(() =>
            {
                if (ModContext.CurrentMod is not null || TaskExecutionScope.Current is not null) Interlocked.Increment(ref wrong);
            });
            return ta.IsTerminal && tb.IsTerminal;
        }));
        Assert.Equal(KilnTaskState.Completed, ta.State);
        Assert.Equal(KilnTaskState.Completed, tb.State);
        Assert.Equal(0, wrong);
        Quiesce();
    }

    [Fact]
    public void Work_that_left_the_task_has_no_scope_and_Send_never_blocks_across_threads()
    {
        var mod = NewMod("alpha");
        SynchronizationContext? context = null;
        bool? rawThreadScope = null, threadPoolScope = null;
        var task = mod.Tasks.Run("escapes", async _ =>
        {
            context = SynchronizationContext.Current;
            var raw = new Thread(() => rawThreadScope = TaskExecutionScope.Current is not null || ModContext.CurrentMod is not null);
            raw.Start();
            raw.Join();
            await Task.Run(() => threadPoolScope = TaskExecutionScope.Current is not null || ModContext.CurrentMod is not null);
        });
        Ended(task);
        Assert.Equal(KilnTaskState.Completed, task.State);
        Assert.False(rawThreadScope);
        Assert.False(threadPoolScope);
        Assert.NotNull(context);
        Assert.Throws<InvalidOperationException>(() => context!.Send(_ => { }, null));  // from another thread: refused, never a wait
        Quiesce();
    }

    [Fact]
    public void NextGateWindow_parks_until_a_later_edge_and_is_cancelled_when_the_world_stops()
    {
        var mod = NewMod("alpha");
        var woke = 0;
        var task = mod.Tasks.Run("window", async ctx =>
        {
            await ctx.NextGateWindow();
            Interlocked.Increment(ref woke);
            await ctx.NextGateWindow();
            Interlocked.Increment(ref woke);
        });
        Native.WaitIdle();
        Thread.Sleep(20);
        Assert.Equal(0, woke);  // parked, no worker held, until a Post edge
        Native.Step();
        Assert.True(Until(() => Volatile.Read(ref woke) == 1));
        Native.WaitIdle();
        Native.StopSession();   // the second window's session ends: resumed cancelled, never in the next world
        Ended(task);
        Assert.Equal(KilnTaskState.Cancelled, task.State);
        Assert.Equal(1, woke);
        Native.StartSession();
        Native.Step();
        Assert.Equal(1, woke);
        // the window is armed for the session it was asked in, of its world (#82: resume_after_step_world): the core
        // compares, the host never arms "whatever is live"
        Assert.Equal((2, 0, 0), (Native.AfterStepWorldCalls, Native.AfterStepInCalls, Native.AfterStepCalls));
        Quiesce();
    }

    [Fact]
    public void Without_a_server_world_tick_work_is_refused_at_once()
    {
        Use(2, session: false);
        var mod = NewMod("alpha");
        Exception? hop = null, post = null, window = null;
        var task = mod.Tasks.Run("menu", async ctx =>
        {
            try { await ctx.ToTick(); } catch (Exception e) { hop = e; }
            try { ctx.PostToTick(() => { }); } catch (Exception e) { post = e; }
            try { await ctx.NextGateWindow(); } catch (Exception e) { window = e; }
        });
        Ended(task);
        Assert.IsType<KilnUnavailableException>(hop);
        Assert.IsType<KilnUnavailableException>(post);
        Assert.IsType<KilnUnavailableException>(window);
        Assert.Equal(0, Native.PendingHops);
        Quiesce();
    }
}
