// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;

namespace Kiln;

/// <summary>
/// What a task's work gets from Kiln: its cancellation, and the ways back to the server tick. It belongs to one task and
/// works only while that task's work runs: once the work has returned its members throw
/// <see cref="InvalidOperationException"/>. While the mod unloads, the task is cancelled: a way to the tick then ends it
/// <see cref="KilnTaskState.Cancelled"/> through its own token, as after <see cref="KilnTask.Cancel"/>, with nothing in
/// <c>kiln.log</c>; used then from code outside the task's work (a raw thread, <c>Task.Run</c>, code after
/// <c>ConfigureAwait(false)</c>) its members throw <see cref="InvalidOperationException"/>.
/// </summary>
/// <remarks>
/// <para>Inside a task, <c>await</c> resumes in the task, on a Kiln worker, like the rest of its work. Work that leaves
/// the task (<c>ConfigureAwait(false)</c>, <c>Task.Run</c>, <c>new Thread</c>) is not part of it any more: Kiln does not
/// track it, and it is not on the tick.</para>
/// <para>Tick work lands on the server world's tick, before the mods' Pre handlers, a bounded amount per mod and step;
/// the rest waits for the next step and is never dropped. With no live server world here (the main menu, or a client
/// joined to someone else's server) <see cref="ToTick"/>, <see cref="OnTick{T}"/>, <see cref="PostToTick"/> and
/// <see cref="NextGateWindow"/> throw <see cref="KilnUnavailableException"/> naming <c>tasks.pool</c> at once (a
/// cancelled task's own <see cref="OperationCanceledException"/> comes first). Tick work that was accepted but can no
/// longer run (the task was cancelled, the mod unloads, the server world stops) never runs and cancels the task.</para>
/// </remarks>
public sealed class KilnTaskContext
{
    private readonly TaskRun _run;

    internal KilnTaskContext(TaskRun run) => _run = run;

    /// <summary>Signalled when the task is cancelled: <see cref="KilnTask.Cancel"/>, or Kiln stopping it (its mod unloads,
    /// the server world it posts to stops). An <see cref="OperationCanceledException"/> of this token that ends the work
    /// ends the task <see cref="KilnTaskState.Cancelled"/>. Any thread.</summary>
    [AnyThread]
    public CancellationToken Cancellation => _run.Token;

    /// <summary><c>await ctx.ToTick()</c> continues the work on the server tick, before the next step's Pre handlers,
    /// where tick-only APIs work. The method stays on the tick from there: its later awaits come back to the tick too,
    /// until <see cref="NextGateWindow"/> takes it back to a worker. Already on this task's tick it continues at once. If
    /// the task is cancelled, or the mod unloads or the world stops before the hop runs, nothing runs on the tick: the
    /// await throws the task's <see cref="OperationCanceledException"/> on a worker, so <c>finally</c> blocks still
    /// run.</summary>
    /// <exception cref="KilnUnavailableException">No live server world here (tasks.pool), and the task is not
    /// cancelled.</exception>
    /// <exception cref="InvalidOperationException">The task's work has ended; or the mod is unloading and this is not the
    /// task's own work.</exception>
    [RequiresCapability(TaskRun.Capability)]
    public TickHop ToTick()
    {
        _run.CheckUsable();
        // a cancelled task's hop never runs: its await throws the task's own OperationCanceledException, world or not
        if (!_run.OnOwnTick && !_run.Token.IsCancellationRequested) _run.RequireServerWorld();
        return new TickHop(new TaskRun.HopState(_run));
    }

    /// <summary>Runs <paramref name="apply"/> once on the server tick and gives its result back: <c>var n = await
    /// ctx.OnTick(() =&gt; Inventory.Count(player));</c>. The code after the await continues where it was (on a worker
    /// inside the task). Called on this task's tick, <paramref name="apply"/> runs at once. If the task is cancelled (also by
    /// its mod's unload), or the world stops, before it ran, it never runs and the Task is cancelled with the task's own
    /// token; an exception from <paramref name="apply"/> comes back through the Task.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="apply"/> is null.</exception>
    /// <exception cref="KilnUnavailableException">No live server world here (tasks.pool), and the task is not
    /// cancelled.</exception>
    /// <exception cref="InvalidOperationException">The task's work has ended; or the mod is unloading and this is not the
    /// task's own work.</exception>
    [RequiresCapability(TaskRun.Capability)]
    public Task<T> OnTick<T>(Func<T> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        return _run.OnTick(apply);
    }

    /// <summary>Hands <paramref name="apply"/> to the server tick without waiting for it: it runs once, before a coming
    /// step's Pre handlers. The task tracks it and does not end until it was applied, so awaiting the task also waits for
    /// its posts. An exception from <paramref name="apply"/> faults the task. A post that cannot be applied any more (the
    /// task was cancelled, the mod unloads, the world stops) is dropped, and the task then ends
    /// <see cref="KilnTaskState.Cancelled"/>, never <see cref="KilnTaskState.Completed"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="apply"/> is null.</exception>
    /// <exception cref="KilnUnavailableException">No live server world here (tasks.pool), and the task is not cancelled:
    /// nothing was queued.</exception>
    /// <exception cref="OperationCanceledException">The task was cancelled (also by its mod's unload): nothing was queued.
    /// It is the task's own, so letting it end the work ends the task <see cref="KilnTaskState.Cancelled"/>.</exception>
    /// <exception cref="InvalidOperationException">The task's work has ended; or the mod is unloading and this is not the
    /// task's own work.</exception>
    [RequiresCapability(TaskRun.Capability)]
    public void PostToTick(Action apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        _run.PostToTick(apply);
    }

    /// <summary><c>await ctx.NextGateWindow()</c> continues the work on a worker after a later Post edge of the server
    /// session that is live now, without holding a worker while it waits. It is a scheduling hint for retrying between
    /// steps, not a promise that the game's state is free. From the tick it takes the work back to a worker. When the
    /// task is cancelled (also by its mod's unload) or that session ends first, the await throws the task's
    /// <see cref="OperationCanceledException"/>; it never resumes in another world. Never continues inline on a game
    /// thread.</summary>
    /// <exception cref="KilnUnavailableException">No live server world here (tasks.pool), and the task is not
    /// cancelled.</exception>
    /// <exception cref="InvalidOperationException">The task's work has ended; or the mod is unloading and this is not the
    /// task's own work.</exception>
    [RequiresCapability(TaskRun.Capability)]
    public GateWindow NextGateWindow()
    {
        _run.CheckUsable();
        // a cancelled task waits for no window: its await throws the task's own OperationCanceledException, world or not
        var session = _run.Token.IsCancellationRequested ? 0 : _run.RequireServerWorld();
        return new GateWindow(new TaskRun.GateState(_run, session));
    }

    /// <summary>The awaitable of <see cref="ToTick"/>.</summary>
    public readonly struct TickHop
    {
        private readonly TaskRun.HopState? _hop;

        internal TickHop(TaskRun.HopState hop) => _hop = hop;

        /// <summary>The awaiter <c>await</c> uses.</summary>
        public Awaiter GetAwaiter() => new(_hop ?? throw new InvalidOperationException("a default TickHop: use KilnTaskContext.ToTick()"));

        /// <summary>Awaits a hop to the tick.</summary>
        public readonly struct Awaiter : ICriticalNotifyCompletion
        {
            private readonly TaskRun.HopState? _hop;

            internal Awaiter(TaskRun.HopState hop) => _hop = hop;

            /// <summary>True only on this task's tick (no hop needed), while the task is not cancelled.</summary>
            public bool IsCompleted => Hop.CheckFastPath();

            /// <summary>Throws the task's <see cref="OperationCanceledException"/> when the hop could not run. Called by hand
            /// before the hop ran it throws <see cref="InvalidOperationException"/>.</summary>
            public void GetResult() => Hop.GetResult();

            /// <summary>Queues <paramref name="continuation"/> for the tick, whatever context is current.</summary>
            public void OnCompleted(Action continuation) => Hop.Queue(continuation);

            /// <summary>As <see cref="OnCompleted"/>.</summary>
            public void UnsafeOnCompleted(Action continuation) => Hop.Queue(continuation);

            private TaskRun.HopState Hop => _hop ?? throw new InvalidOperationException("a default TickHop.Awaiter: use KilnTaskContext.ToTick()");
        }
    }

    /// <summary>The awaitable of <see cref="NextGateWindow"/>.</summary>
    public readonly struct GateWindow
    {
        private readonly TaskRun.GateState? _gate;

        internal GateWindow(TaskRun.GateState gate) => _gate = gate;

        /// <summary>The awaiter <c>await</c> uses.</summary>
        public Awaiter GetAwaiter() => new(_gate ?? throw new InvalidOperationException("a default GateWindow: use KilnTaskContext.NextGateWindow()"));

        /// <summary>Awaits the next gate window.</summary>
        public readonly struct Awaiter : ICriticalNotifyCompletion
        {
            private readonly TaskRun.GateState? _gate;

            internal Awaiter(TaskRun.GateState gate) => _gate = gate;

            /// <summary>Always false: the work always waits for a later Post edge.</summary>
            public bool IsCompleted => false;

            /// <summary>Throws the task's <see cref="OperationCanceledException"/> when the task was cancelled or the session
            /// ended first. Called by hand before the window came it throws <see cref="InvalidOperationException"/>.</summary>
            public void GetResult() => Gate.GetResult();

            /// <summary>Queues <paramref name="continuation"/> for a worker after the next Post edge.</summary>
            public void OnCompleted(Action continuation) => Gate.Queue(continuation);

            /// <summary>As <see cref="OnCompleted"/>.</summary>
            public void UnsafeOnCompleted(Action continuation) => Gate.Queue(continuation);

            private TaskRun.GateState Gate => _gate ?? throw new InvalidOperationException("a default GateWindow.Awaiter: use KilnTaskContext.NextGateWindow()");
        }
    }
}
