// SPDX-License-Identifier: MIT
// Context.Tasks over the fake pool (FakeTaskBackend): the three Run overloads, results and faults, the final-state
// precedence, Result/GetResult/Wait that never wait where they must not, the refusals (bad arguments, a full queue, no
// pool) and the public surface.
using System.Reflection;
using Xunit;

// The task bodies stand in for mod code (their own delays, no test token), and the test thread is the mod's own thread,
// where blocking on a finished task is allowed.
#pragma warning disable xUnit1031, xUnit1051

namespace Kiln.Api.Tests;

public sealed class TasksTests : TaskTest
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(20);

    // ---- overloads and results ----

    [Fact]
    public void Run_with_an_Action_runs_on_a_worker_and_completes()
    {
        var mod = NewMod("alpha");
        var thread = 0;
        var task = mod.Tasks.Run("scan", ctx => thread = Environment.CurrentManagedThreadId);
        Ended(task);
        Assert.Equal(KilnTaskState.Completed, task.State);
        Assert.Null(task.Fault);
        Assert.Equal("scan", task.Name);
        Assert.True(Pool.IsWorker(thread));
        Assert.Equal((mod.Owner, "scan"), (Pool.Admitted.Single().Owner, Pool.Admitted.Single().Name));
    }

    [Fact]
    public void Run_with_async_work_ends_after_its_last_await_and_every_piece_runs_in_the_task()
    {
        var mod = NewMod("alpha");
        var pieces = new List<(int Thread, bool InTask)>();
        var done = false;
        var task = mod.Tasks.Run("io", async ctx =>
        {
            pieces.Add((Environment.CurrentManagedThreadId, TaskExecutionScope.Current is not null));
            await Task.Yield();
            pieces.Add((Environment.CurrentManagedThreadId, TaskExecutionScope.Current is not null));
            await Task.Delay(5);
            pieces.Add((Environment.CurrentManagedThreadId, TaskExecutionScope.Current is not null));
            done = true;
        });
        Ended(task);
        Assert.Equal(KilnTaskState.Completed, task.State);
        Assert.True(done);  // the async lambda bound to Func<KilnTaskContext, Task>, not to an async-void Action
        Assert.Equal(3, pieces.Count);
        Assert.All(pieces, p => Assert.True(Pool.IsWorker(p.Thread) && p.InTask, $"{p}"));
    }

    [Fact]
    public void Run_T_gives_its_result_to_Result_await_and_AsTask()
    {
        var mod = NewMod("alpha");
        var task = mod.Tasks.Run("path", async ctx =>
        {
            await Task.Yield();
            return 42;
        });
        Ended(task);
        Assert.Equal(42, task.Result);
        Assert.Equal(42, task.GetAwaiter().GetResult());
        Assert.Same(task.AsTask(), task.AsTask());
        Assert.Equal(42, task.AsTask().Result);

        var outer = mod.Tasks.Run("outer", async ctx => await task + 1);
        Ended(outer);
        Assert.Equal(43, outer.Result);
    }

    [Fact]
    public void A_fault_in_the_work_faults_the_task_and_is_reported_once()
    {
        var mod = NewMod("alpha");
        var boom = new InvalidOperationException("boom");
        var unobserved = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            lock (unobserved) unobserved.AddRange(e.Exception.InnerExceptions);
        };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            var sync = mod.Tasks.Run("sync", ctx => throw boom);
            var async = mod.Tasks.Run<int>("async", async ctx =>
            {
                await Task.Yield();
                throw boom;
            });
            Ended(sync);
            Ended(async);
            Assert.Equal(KilnTaskState.Faulted, sync.State);
            Assert.Same(boom, sync.Fault);
            Assert.Same(boom, Assert.Throws<InvalidOperationException>(() => sync.GetAwaiter().GetResult()));
            Assert.Same(boom, async.Fault);
            Assert.Same(boom, Assert.Throws<InvalidOperationException>(() => async.Result));
            Assert.True(async.AsTask().IsFaulted);
            // the fault is published (which ends the wait above), then logged on the worker: wait for the lines, read them
            // under the backend's lock
            Assert.True(SpinWait.SpinUntil(() => Backend.Logged("task 'sync' faulted") && Backend.Logged("task 'async' faulted"), 20000));
            lock (Backend.Lines)
            {
                Assert.Single(Backend.Lines, l => l.Contains("task 'sync' faulted", StringComparison.Ordinal));
                Assert.Single(Backend.Lines, l => l.Contains("task 'async' faulted", StringComparison.Ordinal));
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            lock (unobserved) Assert.DoesNotContain(boom, unobserved);  // Kiln observed it: reported once, in kiln.log
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    // ---- the final state ----

    [Fact]
    public void An_OperationCanceledException_of_the_tasks_own_token_after_a_cancel_is_Cancelled()
    {
        var mod = NewMod("alpha");
        using var started = new ManualResetEventSlim();
        var task = mod.Tasks.Run("own", async ctx =>
        {
            started.Set();
            await Task.Delay(Timeout.Infinite, ctx.Cancellation);
        });
        Assert.True(started.Wait(Long));
        task.Cancel();
        Ended(task);
        Assert.Equal(KilnTaskState.Cancelled, task.State);
        Assert.Null(task.Fault);
        var oce = Assert.ThrowsAny<OperationCanceledException>(() => task.GetAwaiter().GetResult());
        Assert.True(oce.CancellationToken.IsCancellationRequested);
        Assert.True(task.AsTask().IsCanceled);
    }

    [Fact]
    public void An_OperationCanceledException_of_another_token_is_Faulted()
    {
        var mod = NewMod("alpha");
        using var other = new CancellationTokenSource();
        other.Cancel();
        var task = mod.Tasks.Run("other", ctx => other.Token.ThrowIfCancellationRequested());
        Ended(task);
        Assert.Equal(KilnTaskState.Faulted, task.State);
        Assert.IsAssignableFrom<OperationCanceledException>(task.Fault);
    }

    [Fact]
    public void The_own_token_without_a_cancel_is_Faulted()
    {
        var mod = NewMod("alpha");
        var task = mod.Tasks.Run("liar", ctx => throw new OperationCanceledException(ctx.Cancellation));
        Ended(task);
        Assert.Equal(KilnTaskState.Faulted, task.State);
    }

    [Fact]
    public void A_normal_finish_after_a_late_cancel_is_Completed()
    {
        var mod = NewMod("alpha");
        using var started = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var sawCancel = false;
        var task = mod.Tasks.Run("late", ctx =>
        {
            started.Set();
            go.Wait(Long);
            sawCancel = ctx.Cancellation.IsCancellationRequested;
        });
        Assert.True(started.Wait(Long));
        task.Cancel();
        go.Set();
        Ended(task);
        Assert.True(sawCancel);
        Assert.Equal(KilnTaskState.Completed, task.State);
    }

    [Fact]
    public void A_task_cancelled_before_it_started_never_runs_and_is_Cancelled_at_once()
    {
        UsePool(1);
        var mod = NewMod("alpha");
        using var started = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var blocker = mod.Tasks.Run("blocker", ctx =>
        {
            started.Set();
            go.Wait(Long);
        });
        Assert.True(started.Wait(Long));
        var ran = false;
        var queued = mod.Tasks.Run("queued", ctx => ran = true);
        Assert.Equal(KilnTaskState.Queued, queued.State);
        queued.Cancel();
        Assert.Equal(KilnTaskState.Cancelled, queued.State);  // at once: the pool never dispatches it
        Assert.ThrowsAny<OperationCanceledException>(() => queued.GetAwaiter().GetResult());
        go.Set();
        Ended(blocker);
        Pool.WaitIdle();
        Assert.False(ran);
    }

    // ---- never a hidden wait ----

    [Fact]
    public void Result_and_GetResult_before_the_end_throw_instead_of_waiting_on_every_thread()
    {
        var mod = NewMod("alpha");
        using var go = new ManualResetEventSlim();
        var task = mod.Tasks.Run("slow", async ctx =>
        {
            await Task.Yield();
            go.Wait(Long);
            return 7;
        });
        void Refuses()
        {
            Assert.False(task.GetAwaiter().IsCompleted);
            Assert.Contains("has not ended", Assert.Throws<InvalidOperationException>(() => task.Result).Message);
            Assert.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
            Assert.Throws<InvalidOperationException>(() => ((KilnTask)task).GetAwaiter().GetResult());
        }
        Refuses();                 // the test's own thread
        Pool.OnTick(Refuses);      // the tick
        var onWorker = mod.Tasks.Run("probe", ctx => Refuses());  // a worker
        Ended(onWorker);
        Assert.Equal(KilnTaskState.Completed, onWorker.State);
        Assert.False(task.AsTask().IsCompleted);
        go.Set();
        Ended(task);
        Assert.Equal(7, task.Result);
    }

    [Fact]
    public void Wait_is_refused_on_the_tick_and_on_a_worker_and_blocks_elsewhere()
    {
        var mod = NewMod("alpha");
        var done = mod.Tasks.Run("done", ctx => { });
        Ended(done);
        Pool.OnTick(() => Assert.Contains("tick", Assert.Throws<InvalidOperationException>(done.Wait).Message));
        var inTask = mod.Tasks.Run("waiter", ctx => done.Wait());
        Ended(inTask);
        Assert.IsType<InvalidOperationException>(inTask.Fault);

        using var go = new ManualResetEventSlim();
        var slow = mod.Tasks.Run("slow", ctx => go.Wait(Long));
        var waiter = new Thread(() => slow.Wait());
        waiter.Start();
        Assert.False(waiter.Join(50));  // a thread of the mod's own blocks until the task ends
        go.Set();
        Assert.True(waiter.Join(Long));
        var faulted = mod.Tasks.Run("faulted", ctx => throw new FormatException());
        Assert.Throws<FormatException>(faulted.Wait);
    }

    // ---- refusals ----

    [Fact]
    public void Bad_arguments_throw_before_admission_and_take_no_slot()
    {
        Pool.OwnerCapacity = 1;
        var mod = NewMod("alpha");
        Action<KilnTaskContext> work = _ => { };
        Assert.Throws<ArgumentNullException>(() => mod.Tasks.Run(null!, work));
        Assert.Throws<ArgumentException>(() => mod.Tasks.Run("", work));
        Assert.Throws<ArgumentException>(() => mod.Tasks.Run("tab\there", work));
        Assert.Throws<ArgumentException>(() => mod.Tasks.Run("c1\u0085", work));
        Assert.Throws<ArgumentException>(() => mod.Tasks.Run("lone\ud800", work));
        Assert.Throws<ArgumentException>(() => mod.Tasks.Run(new string('a', 129), work));
        Assert.Throws<ArgumentException>(() => mod.Tasks.Run(new string('é', 65), work));  // 130 UTF-8 bytes
        Assert.Throws<ArgumentNullException>(() => mod.Tasks.Run("x", (Action<KilnTaskContext>)null!));
        Assert.Throws<ArgumentNullException>(() => mod.Tasks.Run("x", (Func<KilnTaskContext, Task>)null!));
        Assert.Throws<ArgumentNullException>(() => mod.Tasks.Run<int>("x", null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => mod.Tasks.Run("x", work, new KilnTaskOptions(Priority: (KilnTaskPriority)9)));
        Assert.Empty(Pool.Admitted);
        var ok = mod.Tasks.Run(new string('é', 64), work);  // 128 bytes: the limit; the one slot was never spent
        Ended(ok);
        Assert.Equal(KilnTaskState.Completed, ok.State);
    }

    [Fact]
    public void A_full_queue_throws_KilnTaskQueueFullException_and_only_for_that()
    {
        Pool.OwnerCapacity = 2;
        var mod = NewMod("alpha");
        using var go = new ManualResetEventSlim();
        var a = mod.Tasks.Run("a", ctx => go.Wait(Long));
        var b = mod.Tasks.Run("b", ctx => go.Wait(Long));
        var full = Assert.Throws<KilnTaskQueueFullException>(() => mod.Tasks.Run("c", ctx => { }));
        Assert.IsAssignableFrom<InvalidOperationException>(full);
        var other = NewMod("beta");  // another owner's room is its own
        var d = other.Tasks.Run("d", ctx => { });
        go.Set();
        Ended(a);
        Ended(b);
        Ended(d);
        Pool.WaitIdle();
        Ended(mod.Tasks.Run("e", ctx => { }));  // ended tasks give their room back

        Pool.SealOwner(mod.Owner);
        var sealedOwner = Assert.Throws<InvalidOperationException>(() => mod.Tasks.Run("f", ctx => { }));
        Assert.IsNotType<KilnTaskQueueFullException>(sealedOwner);
        Pool.Unavailable = "the pool's workers could not start";
        var off = Assert.Throws<KilnUnavailableException>(() => other.Tasks.Run("g", ctx => { }));
        Assert.Equal("tasks.pool", off.Capability);
        Assert.Equal("the pool's workers could not start", off.Reason);
    }

    [Fact]
    public void No_pool_bound_is_an_honest_capability_refusal()
    {
        var mod = NewMod("alpha");
        TaskBackend.Install(null);
        var e = Assert.Throws<KilnUnavailableException>(() => mod.Tasks.Run("x", ctx => { }));
        Assert.Equal("tasks.pool", e.Capability);
        Assert.NotNull(e.Reason);
        Assert.Throws<KilnUnavailableException>(() => mod.Tasks.Run("x", async ctx => await Task.Yield()));
        Assert.Throws<KilnUnavailableException>(() => mod.Tasks.Run("x", ctx => Task.FromResult(1)));
        Assert.Throws<ArgumentException>(() => mod.Tasks.Run("", ctx => { }));  // a bad argument is still that
    }

    [Fact]
    public void A_mod_that_is_unloading_cannot_start_tasks()
    {
        var mod = NewMod("alpha");
        mod.BeginUnload();
        Assert.Throws<InvalidOperationException>(() => mod.Tasks.Run("late", ctx => { }));
        Assert.Empty(Pool.Admitted);
    }

    [Fact]
    public void BeginUnload_seals_the_mod_signals_its_tasks_and_cancels_the_queued_one()
    {
        UsePool(1);  // one worker: the blocked task holds it, the third task stays queued
        var mod = NewMod("alpha");
        var waits = mod.Tasks.Run("waits", async ctx => await Task.Delay(Timeout.Infinite, ctx.Cancellation));
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var blocked = mod.Tasks.Run("blocked", ctx =>
        {
            started.Set();
            gate.Wait();  // ignores its token: it stops only when the test lets it
        });
        Assert.True(started.Wait(Long));
        var queuedRan = false;
        var queued = mod.Tasks.Run("queued", _ => queuedRan = true);
        Assert.Equal(KilnTaskState.Queued, queued.State);

        mod.BeginUnload();  // on no thread does this wait for the blocked task

        Ended(queued);
        Assert.Equal(KilnTaskState.Cancelled, queued.State);
        Assert.False(queuedRan);
        Assert.Equal(KilnTaskState.Running, blocked.State);  // signalled, still running: joined off the tick by the host
        Assert.Equal(KilnTaskState.Running, waits.State);  // signalled too: its resumption queues behind the blocked worker
        Assert.Throws<InvalidOperationException>(() => mod.Tasks.Run("late", ctx => { }));
        gate.Set();
        Ended(blocked);
        Assert.Equal(KilnTaskState.Completed, blocked.State);  // it returned normally after the signal
        Ended(waits);
        Assert.Equal(KilnTaskState.Cancelled, waits.State);
    }

    [Fact]
    public void Options_reach_the_pool()
    {
        var mod = NewMod("alpha");
        Ended(mod.Tasks.Run("plain", ctx => { }));
        Ended(mod.Tasks.Run("long", ctx => { }, new KilnTaskOptions(LongRunning: true, Priority: KilnTaskPriority.High)));
        Assert.Equal(default, Pool.Admitted[0].Options);
        Assert.Equal(new KilnTaskOptions(true, KilnTaskPriority.High), Pool.Admitted[1].Options);
    }

    [Fact]
    public void Each_mod_gets_its_own_Tasks()
    {
        var a = NewMod("alpha");
        var b = NewMod("beta");
        Assert.Same(a.Tasks, a.Tasks);
        Assert.NotSame(a.Tasks, b.Tasks);
        Ended(b.Tasks.Run("x", ctx => { }));
        Assert.Equal(b.Owner, Pool.Admitted.Single().Owner);
    }

    // ---- the public surface ----

    [Fact]
    public void The_public_surface_is_exactly_this()
    {
        static IEnumerable<string> Members(Type t) => t
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m is not MethodInfo { IsSpecialName: true } && m.DeclaringType == t)
            .Select(m => $"{Name(t)}.{m.Name}")
            .Distinct();
        static string Name(Type t) => t.IsNested ? Name(t.DeclaringType!) + "." + t.Name : t.Name;

        Type[] types =
        [
            typeof(KilnTasks), typeof(KilnTask), typeof(KilnTask.Awaiter), typeof(KilnTask<>), typeof(KilnTask<>.Awaiter),
            typeof(KilnTaskContext), typeof(KilnTaskContext.TickHop), typeof(KilnTaskContext.TickHop.Awaiter),
            typeof(KilnTaskContext.GateWindow), typeof(KilnTaskContext.GateWindow.Awaiter), typeof(KilnTaskQueueFullException),
        ];
        var actual = types.SelectMany(Members).Where(m => !m.EndsWith(".Equals", StringComparison.Ordinal) &&
                                                          !m.EndsWith(".GetHashCode", StringComparison.Ordinal) &&
                                                          !m.EndsWith(".ToString", StringComparison.Ordinal))
            .OrderBy(m => m, StringComparer.Ordinal).ToList();
        string[] expected =
        [
            "KilnTask.AsTask", "KilnTask.Awaiter", "KilnTask.Cancel", "KilnTask.Fault", "KilnTask.GetAwaiter", "KilnTask.Name",
            "KilnTask.State", "KilnTask.Wait",
            "KilnTask.Awaiter.GetResult", "KilnTask.Awaiter.IsCompleted", "KilnTask.Awaiter.OnCompleted", "KilnTask.Awaiter.UnsafeOnCompleted",
            "KilnTask`1.AsTask", "KilnTask`1.Awaiter", "KilnTask`1.GetAwaiter", "KilnTask`1.Result",
            "KilnTask`1.Awaiter.GetResult", "KilnTask`1.Awaiter.IsCompleted", "KilnTask`1.Awaiter.OnCompleted", "KilnTask`1.Awaiter.UnsafeOnCompleted",
            "KilnTaskContext.Cancellation", "KilnTaskContext.GateWindow", "KilnTaskContext.NextGateWindow", "KilnTaskContext.OnTick",
            "KilnTaskContext.PostToTick", "KilnTaskContext.TickHop", "KilnTaskContext.ToTick",
            "KilnTaskContext.GateWindow.Awaiter", "KilnTaskContext.GateWindow.GetAwaiter",
            "KilnTaskContext.GateWindow.Awaiter.GetResult", "KilnTaskContext.GateWindow.Awaiter.IsCompleted",
            "KilnTaskContext.GateWindow.Awaiter.OnCompleted", "KilnTaskContext.GateWindow.Awaiter.UnsafeOnCompleted",
            "KilnTaskContext.TickHop.Awaiter", "KilnTaskContext.TickHop.GetAwaiter",
            "KilnTaskContext.TickHop.Awaiter.GetResult", "KilnTaskContext.TickHop.Awaiter.IsCompleted",
            "KilnTaskContext.TickHop.Awaiter.OnCompleted", "KilnTaskContext.TickHop.Awaiter.UnsafeOnCompleted",
            "KilnTasks.Run",
        ];
        Assert.Equal(expected.OrderBy(m => m, StringComparer.Ordinal), actual);

        // a custom awaitable, never a BCL Task behind a property: blocking on it cannot hide
        Assert.False(typeof(Task).IsAssignableFrom(typeof(KilnTask)));
        foreach (var t in types)
            Assert.DoesNotContain(t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static), m => m.Name == "Completion");
        Assert.Equal(3, typeof(KilnTasks).GetMethods().Count(m => m.Name == "Run"));
        Assert.Empty(typeof(KilnTask).GetConstructors());       // only Kiln makes tasks
        Assert.Empty(typeof(KilnTaskContext).GetConstructors());
        Assert.Empty(typeof(KilnTaskQueueFullException).GetConstructors());
        Assert.Equal(typeof(KilnTasks), typeof(ModContext).GetProperty("Tasks")!.PropertyType);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, Enum.GetValues<KilnTaskState>().Select(s => (int)s));
        Assert.Equal(new[] { "Normal", "Low", "High" }, Enum.GetNames<KilnTaskPriority>());
        Assert.Equal(new[] { 0, 1, 2 }, Enum.GetValues<KilnTaskPriority>().Select(s => (int)s));
    }

    // ---- the backend's end notification (TaskBackend's real pool reports a quiescent lease) ----

    [Fact]
    public void The_end_notification_ends_a_task_whose_lease_the_pool_ended_itself()
    {
        var mod = NewMod("alpha");
        var gate = new TaskCompletionSource();
        var stuck = mod.Tasks.Run("stuck", async _ => await gate.Task);
        Pool.WaitIdle();
        Assert.Equal(KilnTaskState.Running, stuck.State);
        TaskRun.LeaseEnded(stuck.Run, KilnTaskState.Faulted);  // its worker faulted outside managed code: the core ended the lease
        Ended(stuck);
        Assert.Equal(KilnTaskState.Faulted, stuck.State);
        Assert.Contains("ended by Kiln's task pool", stuck.Fault!.Message);
        Assert.True(stuck.Run.Token.IsCancellationRequested);
        TaskRun.LeaseEnded(stuck.Run, KilnTaskState.Faulted);  // once: a second notification changes nothing
        Assert.Equal(KilnTaskState.Faulted, stuck.State);

        var parked = mod.Tasks.Run("parked", async _ => await gate.Task);
        Pool.WaitIdle();
        TaskRun.LeaseEnded(parked.Run, KilnTaskState.Cancelled);
        Ended(parked);
        Assert.Equal(KilnTaskState.Cancelled, parked.State);
    }

    [Fact]
    public void The_end_notification_of_a_task_that_ended_itself_changes_nothing()
    {
        var mod = NewMod("alpha");
        var done = mod.Tasks.Run("done", _ => { });
        var faulted = mod.Tasks.Run("faulted", _ => throw new InvalidOperationException("boom"));
        Ended(done);
        Ended(faulted);
        var fault = faulted.Fault;
        TaskRun.LeaseEnded(done.Run, KilnTaskState.Completed);
        TaskRun.LeaseEnded(faulted.Run, KilnTaskState.Faulted);
        TaskRun.LeaseEnded(new object(), KilnTaskState.Faulted);  // not a task: ignored
        Assert.Equal(KilnTaskState.Completed, done.State);
        Assert.Same(fault, faulted.Fault);
    }

    [Fact]
    public void The_hosts_cancel_asks_the_task_to_stop_like_Cancel()
    {
        var mod = NewMod("alpha");
        var task = mod.Tasks.Run("waits", async ctx => await Task.Delay(Timeout.Infinite, ctx.Cancellation));
        Pool.WaitIdle();
        TaskRun.CancelFromHost(task.Run);
        Ended(task);
        Assert.Equal(KilnTaskState.Cancelled, task.State);
        Assert.Same(typeof(TasksTests).Assembly, TaskRun.WorkAssemblyOf(task.Run));
        Assert.Null(TaskRun.WorkAssemblyOf(new object()));
    }
}
