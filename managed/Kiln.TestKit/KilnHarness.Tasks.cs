// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private KitTaskPool? _taskPool;
    private HarnessTasks? _tasks;

    /// <summary>Kiln tasks (<see cref="ModContext.Tasks"/>) of the mods under this harness: they run on the kit's task pool,
    /// whose workers are real threads, and their hops back to the tick run in <see cref="Step"/>.</summary>
    public HarnessTasks Tasks => _tasks ??= new HarnessTasks(this);

    internal KitTaskPool TaskPool => _taskPool ?? throw new InvalidOperationException("the harness has no task pool");

    // The pool lives as long as the harness: two workers, the harness's tick thread as the server world's tick.
    private void StartTasks()
    {
        _taskPool = new KitTaskPool(2, () => _tick?.IsCurrent == true, $"kiln-testkit task {_serial}.", notifyEnded: true);
        TaskBackend.Install(_taskPool);
        KitBackend.Instance.Capabilities.Report(HarnessCapabilities.HarnessOwner, TaskRun.Capability, CapState.Available, CapReason.None,
            "the test kit's task pool: two worker threads, tick hops in the harness's steps", KitBackend.CapabilityOwner);
    }

    private void EndTasks()
    {
        if (_taskPool is not { } pool) return;
        if (ReferenceEquals(TaskBackend.Current, pool)) TaskBackend.Install(null);
        KitBackend.Instance.Capabilities.Withdraw(HarnessCapabilities.HarnessOwner, TaskRun.Capability);
        pool.Dispose();
    }

    // The server world's session for tasks: from the first step (the world exists while the mods load) to the stop.
    private void TasksSessionStarted() => _taskPool?.StartSession();

    private void TasksSessionStopping() => _taskPool?.StopSession();

    // In the step, as the core does: the hops before the mods' Pre, the Post edge after their Post.
    private void TasksBeforePre() => _taskPool?.RunHops();

    private void TasksAfterPost() => _taskPool?.PostEdge();
}

/// <summary>
/// The Kiln tasks of a harness's mods (<see cref="KilnHarness.Tasks"/>). A task's work runs on one of the kit's two
/// worker threads, as on Kiln's pool; its hops back to the tick (<c>ToTick</c>, <c>OnTick</c>, <c>PostToTick</c>) run in the
/// next <see cref="KilnHarness.Step"/>, before the mods' <c>Tick.Pre</c>, and <c>NextGateWindow</c> resumes after a step.
/// </summary>
/// <remarks>The workers run in real time, beside the steps: <see cref="Settle"/> is how a test waits for the mods' tasks
/// to end, stepping as their hops need, with no sleeping in the test. The pool's limits and budgets are not modelled (no
/// 64-hop or 2 ms budget per step, no running cap per mod).</remarks>
public sealed class HarnessTasks
{
    private readonly KilnHarness _harness;

    internal HarnessTasks(KilnHarness harness) => _harness = harness;

    /// <summary>The tasks that have not ended yet (and ended ones with a hop still to run).</summary>
    public int Live => _harness.TaskPool.Live;

    /// <summary>
    /// Runs the mods' tasks to their end: waits while their work runs on the workers, and steps the server whenever all of
    /// them wait for the tick (a hop, <c>NextGateWindow</c>, a service task that completes in a step). Returns the steps it
    /// took (0 when no task was alive).
    /// </summary>
    /// <param name="maxSteps">Then it throws <see cref="TimeoutException"/>, naming the tasks still alive.</param>
    /// <param name="timeout">Real time for the whole call (default 10 s); then it throws <see cref="TimeoutException"/>
    /// naming the tasks still alive and what each one waits for (a task stuck in its work, a deadlock).</param>
    /// <exception cref="InvalidOperationException">Called on the harness's tick thread or inside a task, where waiting for
    /// tasks would deadlock; or the pool reported a bug in Kiln's task bridge (the message says what).</exception>
    public int Settle(int maxSteps = 1000, TimeSpan? timeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxSteps);
        var pool = _harness.TaskPool;
        if (_harness.IsTickThread || pool.OnPoolThread || TaskExecutionScope.Current is not null)
            throw new InvalidOperationException("Settle waits for tasks: call it from the test, never on the harness's tick thread or inside a task");
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        var deadline = Environment.TickCount64 + (long)limit.TotalMilliseconds;
        TimeSpan Left() => TimeSpan.FromMilliseconds(Math.Max(0, deadline - Environment.TickCount64));
        for (var steps = 0; ; steps++)
        {
            if (!pool.TryWaitIdle(Left()))
                throw new TimeoutException($"the mods' tasks were still running after {limit.TotalSeconds:0.#} s: {Alive(pool)}");
            Check(pool);
            if (pool.Live == 0) return steps;
            if (steps >= maxSteps) throw new TimeoutException($"the mods' tasks had not ended after {maxSteps} steps: {Alive(pool)}");
            if (Left() == TimeSpan.Zero) throw new TimeoutException($"the mods' tasks had not ended after {limit.TotalSeconds:0.#} s: {Alive(pool)}");
            var hopsOrEdges = pool.PendingHops > 0 || pool.WaitingForEdge > 0;
            _harness.Step();
            // a task that awaits something of its own (a delay, a file, a thread) moves in real time: give it a moment
            if (!hopsOrEdges) pool.WaitForWork(TimeSpan.FromMilliseconds(Math.Min(10, Left().TotalMilliseconds)));
        }
    }

    private static void Check(KitTaskPool pool)
    {
        var errors = pool.ErrorsNow();
        if (errors.Length > 0) throw new InvalidOperationException("Kiln's task bridge misused the pool (a bug in Kiln, not in the mod): " + string.Join("; ", errors));
    }

    private string Alive(KitTaskPool pool)
    {
        var mods = _harness.Run(() => _harness.Manager.Mods.ToDictionary(m => m.Owner, m => m.Id));
        return string.Join(", ", pool.Describe(o => mods.GetValueOrDefault(o) ?? $"owner {o}"));
    }
}
