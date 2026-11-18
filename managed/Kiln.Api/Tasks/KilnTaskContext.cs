// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;

namespace Kiln;

/// <summary>
/// What a task's work gets from Kiln: its cancellation, and the ways back to a world's tick. It belongs to one task and
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
/// <para><b>Which world.</b> A task belongs to the world it was started in (<see cref="TickWorld"/>), and
/// <see cref="ToTick()"/>, <see cref="OnTick{T}(Func{T})"/>, <see cref="PostToTick(Action)"/> and
/// <see cref="NextGateWindow()"/> go back there: the server world when it was started on the server world's thread (a
/// dedicated server's tick; a hosting client's <c>Context.Tick</c>, events and commands), the client world when it was
/// started on the game's main thread (<c>Client.Tick</c>, <c>Client.Post</c>, Kiln's UI callbacks, client-side network
/// handlers, <c>Context.Tick</c> on a joined client, <c>OnLoad</c> in a client's main menu), the server world anywhere
/// else. The overloads with a <see cref="WorldKind"/> name the world. A task started inside a task belongs to its
/// parent's world (in a hop, to that hop's world).</para>
/// <para><b>Sessions.</b> A task is bound to one session per world kind: the one it was started in, else the first one
/// a hop, post or window of it reaches. It never comes back into a later world of that kind. When that world stops, the
/// task's pending hops, posts and windows there, and every one it asks for there later, end it
/// <see cref="KilnTaskState.Cancelled"/> through its own token, with nothing logged; a task that is only computing is
/// left alone until then. With no world of a kind it is not bound to (the main menu; a client joined to someone else's
/// server, for the server world; a dedicated server, for the client world) a call there throws
/// <see cref="KilnUnavailableException"/> naming <c>tasks.pool</c> at once, and the task stays usable. A cancelled task's
/// own <see cref="OperationCanceledException"/> comes first.</para>
/// <para><b>Where hops land.</b> Server-world hops run on the server world's tick before the mods' Pre handlers; client-world
/// hops on the game's main thread at the client world's step, before its Pre subscribers. Each world has its own budget
/// per mod and step (server: 64 hops and 2 ms per mod, 4 ms in all; client: 64 hops and 1 ms per mod, 2 ms in all); the
/// rest waits for the next step and is never dropped. On a joined client a client-world hop is the mods' tick there
/// (<c>Game.IsTickThread</c> is true, as in its <c>Context.Tick</c>); on a hosting client it is like a <c>Client.Tick</c>
/// handler, and server-world tick-only APIs belong to a server-world hop.</para>
/// </remarks>
public sealed class KilnTaskContext
{
    private readonly TaskRun _run;

    internal KilnTaskContext(TaskRun run) => _run = run;

    /// <summary>Signalled when the task is cancelled: <see cref="KilnTask.Cancel"/>, or Kiln stopping it (its mod unloads,
    /// a world it hops to stops). An <see cref="OperationCanceledException"/> of this token that ends the work ends the
    /// task <see cref="KilnTaskState.Cancelled"/>. Any thread.</summary>
    [AnyThread]
    public CancellationToken Cancellation => _run.Token;

    /// <summary>The world the parameterless <see cref="ToTick()"/>, <see cref="OnTick{T}(Func{T})"/>,
    /// <see cref="PostToTick(Action)"/> and <see cref="NextGateWindow()"/> go to: the one the task was started in
    /// (<see cref="WorldKind.Server"/> or <see cref="WorldKind.Client"/>). Any thread.</summary>
    [AnyThread]
    public WorldKind TickWorld => _run.Home;

    /// <summary><c>await ctx.ToTick()</c> continues the work on the tick of the task's world (<see cref="TickWorld"/>),
    /// before that step's Pre handlers, where tick-only APIs work. The method stays there: its later awaits come back to the
    /// same world's tick too, until <see cref="NextGateWindow()"/> takes it back to a worker. Already on this task's tick of
    /// that world it continues at once. If the task is cancelled, the mod unloads or the world stops before the hop runs,
    /// nothing runs on the tick: the await throws the task's <see cref="OperationCanceledException"/> on a worker, so
    /// <c>finally</c> blocks still run.</summary>
    /// <exception cref="KilnUnavailableException">No world of that kind here and the task is not bound to one
    /// (tasks.pool).</exception>
    /// <exception cref="InvalidOperationException">The task's work has ended; or the mod is unloading and this is not the
    /// task's own work.</exception>
    [RequiresCapability(TaskRun.Capability)]
    public TickHop ToTick() => Hop(_run.Home);

    /// <summary><c>await ctx.ToTick(WorldKind.Client)</c>: <see cref="ToTick()"/> to the named world (the server world's tick,
    /// or the client world's step on the game's main thread), whatever the task's own world is.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="world"/> is not Server or Client.</exception>
    /// <exception cref="KilnUnavailableException">No world of that kind here and the task is not bound to one
    /// (tasks.pool).</exception>
    /// <exception cref="InvalidOperationException">As <see cref="ToTick()"/>.</exception>
    [RequiresCapability(TaskRun.Capability)]
    public TickHop ToTick(WorldKind world) => Hop(TaskRun.HopWorld(world, nameof(world)));

    private TickHop Hop(WorldKind world)
    {
        _run.CheckUsable();
        // a cancelled task's hop never runs: its await throws the task's own OperationCanceledException, world or not;
        // a task whose world of that kind has ended is cancelled here, and its await throws the same
        if (!_run.OnOwnTick(world) && !_run.Token.IsCancellationRequested) _run.TargetOrCancel(world);
        return new TickHop(new TaskRun.HopState(_run, world));
    }

    /// <summary>Runs <paramref name="apply"/> once on the tick of the task's world and gives its result back: <c>var near =
    /// await ctx.OnTick(() =&gt; NearOnTick(at));</c>, where <c>NearOnTick</c> is the mod's own tick-only code (as in the
    /// FetchNearby sample). The code after the await continues where it was (on a worker inside the task). Called on this
    /// task's tick of that world, <paramref name="apply"/> runs at once. If the task is cancelled (also by its mod's
    /// unload), or the world stops, before it ran, it never runs and the Task is cancelled with the task's own token; an
    /// exception from <paramref name="apply"/> comes back through the Task.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="apply"/> is null.</exception>
    /// <exception cref="KilnUnavailableException">No world of that kind here and the task is not bound to one
    /// (tasks.pool).</exception>
    /// <exception cref="InvalidOperationException">The task's work has ended; or the mod is unloading and this is not the
    /// task's own work.</exception>
    [RequiresCapability(TaskRun.Capability)]
    public Task<T> OnTick<T>(Func<T> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        return _run.OnTick(_run.Home, apply);
    }

    /// <summary><see cref="OnTick{T}(Func{T})"/> on the named world's tick: <c>await ctx.OnTick(WorldKind.Client, () =&gt;
    /// _view.Selected)</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="world"/> is not Server or Client.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="apply"/> is null.</exception>
    /// <exception cref="KilnUnavailableException">As <see cref="OnTick{T}(Func{T})"/>.</exception>
    /// <exception cref="InvalidOperationException">As <see cref="OnTick{T}(Func{T})"/>.</exception>
    [RequiresCapability(TaskRun.Capability)]
    public Task<T> OnTick<T>(WorldKind world, Func<T> apply)
    {
        world = TaskRun.HopWorld(world, nameof(world));
        ArgumentNullException.ThrowIfNull(apply);
        return _run.OnTick(world, apply);
    }

    /// <summary>Hands <paramref name="apply"/> to the tick of the task's world without waiting for it: it runs once, before
    /// a coming step's Pre handlers. The task tracks it and does not end until it was applied, so awaiting the task also
    /// waits for its posts. An exception from <paramref name="apply"/> faults the task. A post that cannot be applied any
    /// more (the task was cancelled, the mod unloads, the world stops) is dropped, and the task then ends
    /// <see cref="KilnTaskState.Cancelled"/>, never <see cref="KilnTaskState.Completed"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="apply"/> is null.</exception>
    /// <exception cref="KilnUnavailableException">No world of that kind here and the task is not bound to one
    /// (tasks.pool): nothing was queued.</exception>
    /// <exception cref="OperationCanceledException">The task was cancelled (also by its mod's unload, or because its world
    /// of that kind has ended): nothing was queued. It is the task's own, so letting it end the work ends the task
    /// <see cref="KilnTaskState.Cancelled"/>.</exception>
    /// <exception cref="InvalidOperationException">The task's work has ended; or the mod is unloading and this is not the
    /// task's own work.</exception>
    [RequiresCapability(TaskRun.Capability)]
    public void PostToTick(Action apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        _run.PostToTick(_run.Home, apply);
    }

    /// <summary><see cref="PostToTick(Action)"/> to the named world's tick: <c>ctx.PostToTick(WorldKind.Client, () =&gt;
    /// _view.Show(marks))</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="world"/> is not Server or Client.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="apply"/> is null.</exception>
    /// <exception cref="KilnUnavailableException">As <see cref="PostToTick(Action)"/>.</exception>
    /// <exception cref="OperationCanceledException">As <see cref="PostToTick(Action)"/>.</exception>
    /// <exception cref="InvalidOperationException">As <see cref="PostToTick(Action)"/>.</exception>
    [RequiresCapability(TaskRun.Capability)]
    public void PostToTick(WorldKind world, Action apply)
    {
        world = TaskRun.HopWorld(world, nameof(world));
        ArgumentNullException.ThrowIfNull(apply);
        _run.PostToTick(world, apply);
    }

    /// <summary><c>await ctx.NextGateWindow()</c> continues the work on a worker after a later Post edge of the task's
    /// session of its world, without holding a worker while it waits. It is a scheduling hint for retrying between steps,
    /// not a promise that the game's state is free. From the tick it takes the work back to a worker. When the task is
    /// cancelled (also by its mod's unload) or that session ends first, the await throws the task's
    /// <see cref="OperationCanceledException"/>; it never resumes in another world. Never continues inline on a game
    /// thread.</summary>
    /// <exception cref="KilnUnavailableException">No world of that kind here and the task is not bound to one
    /// (tasks.pool).</exception>
    /// <exception cref="InvalidOperationException">The task's work has ended; or the mod is unloading and this is not the
    /// task's own work.</exception>
    [RequiresCapability(TaskRun.Capability)]
    public GateWindow NextGateWindow() => Window(_run.Home);

    /// <summary><see cref="NextGateWindow()"/> after a later step of the named world: <c>NextGateWindow(WorldKind.Client)</c>
    /// resumes on a worker after the client world's next step (about 60 a second while in game).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="world"/> is not Server or Client.</exception>
    /// <exception cref="KilnUnavailableException">As <see cref="NextGateWindow()"/>.</exception>
    /// <exception cref="InvalidOperationException">As <see cref="NextGateWindow()"/>.</exception>
    [RequiresCapability(TaskRun.Capability)]
    public GateWindow NextGateWindow(WorldKind world) => Window(TaskRun.HopWorld(world, nameof(world)));

    private GateWindow Window(WorldKind world)
    {
        _run.CheckUsable();
        // a cancelled task waits for no window: its await throws the task's own OperationCanceledException, world or not
        return new GateWindow(new TaskRun.GateState(_run, world, _run.WindowSession(world)));
    }

    /// <summary>The awaitable of <see cref="ToTick()"/>.</summary>
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

    /// <summary>The awaitable of <see cref="NextGateWindow()"/>.</summary>
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
