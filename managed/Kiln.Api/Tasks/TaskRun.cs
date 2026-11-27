// SPDX-License-Identifier: MIT
using System.Buffers;
using System.Text;

namespace Kiln;

/// <summary>
/// One C# task on the pool: one lease of <see cref="TaskBackend"/>, the host state its dispatches carry. It runs the
/// work's pieces (segments on workers, hops on a world's step) inside <see cref="TaskExecutionScope"/>, keeps the work's
/// tick items in order per world, and ends the lease exactly once, when the work and everything it handed to a world
/// have resolved.
/// </summary>
/// <remarks>
/// <para>Worlds (#82). A task has a home world, where its parameterless hops go: the one it was started in (see
/// <see cref="Capture"/>). It is bound to at most one session per world kind: the one it was started in, else the first
/// one a hop, post or window of it reaches. A call to a world kind whose bound session has ended cancels the task
/// through its own token (it has no world of that kind left to go to); a call to a kind it is not bound to with no live
/// world throws <see cref="KilnUnavailableException"/> and leaves the task alone.</para>
/// </remarks>
/// <remarks>
/// <para>Suspension: every segment that does not end the task arms one registration before it returns and stores it
/// here. Whatever needs the task again (a continuation posted to its pool context, the work's end, the last tick item
/// resolving) takes that registration, once, and resumes it. A stored registration means the task holds no worker.</para>
/// <para>Locks: <c>_gate</c> guards this object's state. No user code runs under it; the backend calls made under it
/// (<see cref="TaskBackend.TickHop(ulong)"/>) never call back into the host.</para>
/// </remarks>
internal sealed class TaskRun
{
    internal const string Capability = "tasks.pool";
    internal const int NameMaxBytes = 128;
    private const int DrainPerSegment = 64;

    /// <summary>Test seam: called at these points (outside every lock); null in production.</summary>
    internal enum Point
    {
        /// <summary>A segment decided to end the task, before <see cref="TaskBackend.Finish"/>.</summary>
        BeforeFinish,

        /// <summary>The lease is finished; the public task is about to end.</summary>
        BeforePublish,

        /// <summary>The public task has ended.</summary>
        AfterPublish,
    }

    internal static Action<Point, TaskRun>? TestHook;

    /// <summary>Who a task belongs to: a mod, or a Kiln component (no mod) under its own owner.</summary>
    internal readonly record struct Owner(ModContext? Mod, ulong Id, string Name);

    internal static Owner ForMod(ModContext mod) => new(mod, mod.Owner, mod.Info.Id);

    private readonly object _gate = new();
    private readonly TaskBackend _backend;
    private readonly Delegate _work;
    private readonly ExecutionContext? _flow;
    private readonly CancellationTokenSource _cts = new();
    private readonly PoolContext _poolContext;
    private readonly TickContext[] _tickContexts;  // per world line: [0] the server world, [1] the client world, [2] the menu
    private readonly WorldKind _home;
    private long _lease;

    // under _gate
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _pool = new();
    private readonly LinkedList<TickItem>[] _tick = [new(), new(), new()];  // per world line, FIFO each
    private readonly ulong[] _bound = new ulong[3];  // the session the task is bound to, per world line; 0 = not yet
    private int _tickRunning;
    private ulong _registration;  // the armed registration, stored while the task is suspended; 0 otherwise
    private bool _started, _bodyEnded, _finished;
    private Exception? _bodyFault, _lateFault;
    private OperationCanceledException? _bodyCancel;
    private bool _tickWorkCancelled;  // a post (or other tick work) the task handed over was never applied
    private GateState? _gateRequested, _gateArmed;

    internal readonly ModContext? Mod;
    internal readonly ulong OwnerId;
    internal readonly string OwnerName;
    internal readonly string Name;
    internal readonly KilnTask Task;
    internal readonly KilnTaskContext Context;

    private TaskRun(TaskBackend backend, Owner owner, string name, Delegate work, KilnTask task, Captured at)
    {
        _backend = backend;
        _work = work;
        _flow = ExecutionContext.Capture();
        _poolContext = new PoolContext(this);
        _tickContexts = [new TickContext(this, WorldKind.Server), new TickContext(this, WorldKind.Client), new TickContext(this, WorldKind.Menu)];
        _home = at.Home;
        _bound[0] = at.Server;
        _bound[1] = at.Client;
        _bound[2] = at.Menu;
        Mod = owner.Mod;
        OwnerId = owner.Id;
        OwnerName = owner.Name;
        Name = name;
        Task = task;
        Context = new KilnTaskContext(this);
        task.Attach(this);
    }

    // ---- the live tasks of each mod, for its unload (ModContext.UnloadTaskSeal / UnloadTaskBlocker) ----

    private static readonly object LiveGate = new();
    private static readonly Dictionary<ModContext, List<TaskRun>> LiveByMod = new();

    private void RegisterLive()
    {
        if (Mod is not { } mod) return;
        lock (LiveGate)
        {
            if (!LiveByMod.TryGetValue(mod, out var list)) LiveByMod[mod] = list = [];
            list.Add(this);
        }
    }

    private void UnregisterLive()
    {
        if (Mod is not { } mod) return;
        lock (LiveGate)
        {
            if (!LiveByMod.TryGetValue(mod, out var list)) return;
            list.Remove(this);
            if (list.Count == 0) LiveByMod.Remove(mod);
        }
    }

    /// <summary>The unload of <paramref name="mod"/> begins: every live task of it is asked to stop (a queued one
    /// ends cancelled here, a running or suspended one through its token), then the pool seals the owner so no new
    /// task of it is admitted. Continuations of the cancelled tasks still run. Never waits for any task.</summary>
    internal static void SealTasks(ModContext mod)
    {
        TaskRun[] live;
        lock (LiveGate) live = LiveByMod.TryGetValue(mod, out var list) ? [.. list] : [];
        foreach (var run in live)
            if (!run.Task.IsTerminal) run.RequestCancel();
        TaskBackend.Current?.SealOwner(mod.Owner);
    }

    /// <summary>The live tasks of <paramref name="mod"/> as <c>'name' (State)</c> joined by ", ", or null when it
    /// has none: what an unload still joins off the tick. Ended tasks are pruned as they are seen.</summary>
    internal static string? LiveTasks(ModContext mod)
    {
        lock (LiveGate)
        {
            if (!LiveByMod.TryGetValue(mod, out var list)) return null;
            list.RemoveAll(r => r.Task.IsTerminal);
            if (list.Count == 0)
            {
                LiveByMod.Remove(mod);
                return null;
            }
            return string.Join(", ", list.Select(r => $"'{r.Name}' ({r.Task.State})"));
        }
    }

    internal CancellationToken Token => _cts.Token;

    internal ulong Lease => (ulong)Interlocked.Read(ref _lease);

    /// <summary>"owner/task", for logs and the flowed diagnostics identity.</summary>
    internal string Identity => OwnerName + "/" + Name;

    private bool OwnerAlive => Mod is null || (Mod.Alive && !Mod.Unloading);

    private void SetLease(ulong lease) => Interlocked.CompareExchange(ref _lease, (long)lease, 0);

    // ---- admission ----

    internal static KilnTask Start(Owner owner, string name, Delegate work, KilnTaskOptions? options, KilnTask task)
    {
        ValidateName(name);
        var o = options ?? default;
        if (!Enum.IsDefined(o.Priority))
            throw new ArgumentOutOfRangeException(nameof(options), o.Priority, "not a KilnTaskPriority");
        var backend = TaskBackend.Current ?? throw new KilnUnavailableException(Capability, "Kiln's task pool is not running in this process");
        if (backend.UnavailableReason is { } why) throw new KilnUnavailableException(Capability, why);
        if (owner.Mod is { } mod && (mod.Unloading || !mod.Alive))
            throw new InvalidOperationException("the mod is unloading: it cannot start tasks");
        var run = new TaskRun(backend, owner, name, work, task, Capture(backend));
        run.RegisterLive();  // before the admit: its end, which unregisters it, can come before the admit returns
        var refusal = backend.Admit(owner.Id, name, o, run, out var lease);
        if (refusal != TaskRefusal.None)
        {
            run.UnregisterLive();
            throw Refused(refusal, backend);
        }
        run.SetLease(lease);
        return task;
    }

    // ---- worlds (#82) ----

    /// <summary>What a new task takes from where it is started: its home world, and the sessions it is bound to.</summary>
    internal readonly record struct Captured(WorldKind Home, ulong Server, ulong Client, ulong Menu = 0);

    /// <summary>The world a task belongs to, from the thread that starts it (never from SynchronizationContext.Current:
    /// several Kiln callbacks on client threads install the mod's server scheduler context):
    /// <list type="number">
    /// <item>inside a task: its parent's bindings, and as home the world of the parent's tick hop (in a hop) or the
    /// parent's home (in a segment);</item>
    /// <item>on the server world's thread, while a server world exists (live, or in its STOPPING): the server world,
    /// bound to that session;</item>
    /// <item>on the game client's main thread: the client world, bound to its session when one exists, else
    /// unbound (the main menu, a loading screen); bound to the live menu session too, when there is one (#83);</item>
    /// <item>anywhere else (the render thread, the thread pool, raw threads): the server world, unbound, as before.</item>
    /// </list></summary>
    internal static Captured Capture(TaskBackend backend)
    {
        if (TaskExecutionScope.Current is { } parent) return parent.CaptureForChild();
        if (backend.OnWorldThread(WorldKind.Server))
        {
            var session = backend.WorldSession(WorldKind.Server, out var state);
            return new Captured(WorldKind.Server, Exists(state) ? session : 0, 0);
        }
        if (backend.OnClientMainThread)
        {
            var session = backend.WorldSession(WorldKind.Client, out var state);
            var menu = backend.WorldSession(WorldKind.Menu, out var menuState);
            return new Captured(WorldKind.Client, 0, Exists(state) ? session : 0, menuState == WorldSessionState.Live ? menu : 0);
        }
        return new Captured(WorldKind.Server, 0, 0);
    }

    private static bool Exists(WorldSessionState state) => state is WorldSessionState.Live or WorldSessionState.Stopping;

    private Captured CaptureForChild()
    {
        var home = TaskExecutionScope.OnTick ? TaskExecutionScope.TickWorld : _home;
        lock (_gate) return new Captured(home, _bound[0], _bound[1], _bound[2]);
    }

    private static int Line(WorldKind world) => world switch { WorldKind.Client => 1, WorldKind.Menu => 2, _ => 0 };

    /// <summary>Where the parameterless hops go.</summary>
    internal WorldKind Home => _home;

    /// <summary>A world argument of a hop (ToTick, OnTick, PostToTick): Server, Client or Menu, else
    /// ArgumentOutOfRangeException (before anything else happens).</summary>
    internal static WorldKind HopWorld(WorldKind world, string parameter) => world is WorldKind.Server or WorldKind.Client or WorldKind.Menu
        ? world
        : throw new ArgumentOutOfRangeException(parameter, world, "a task hops to the server world, the client world or the main menu; the prediction world is no hop target");

    /// <summary>A world argument of NextGateWindow: Server or Client (the menu has no steps to wait for), else
    /// ArgumentOutOfRangeException.</summary>
    internal static WorldKind WindowWorld(WorldKind world, string parameter) => world is WorldKind.Server or WorldKind.Client
        ? world
        : throw new ArgumentOutOfRangeException(parameter, world, world == WorldKind.Menu
            ? "the main menu has no world steps to wait for: NextGateWindow waits for the server or the client world (in the menu, await ToTick(WorldKind.Menu) or Task.Delay)"
            : "a gate window comes after a step of the server world or the client world; the prediction world has none");

    /// <summary>The session a call to <paramref name="world"/> goes to: the task's binding, else the live session (the task is
    /// bound to it from now on). Null when the bound session has ended: the task has no world of that kind left, and the
    /// caller cancels it. Throws <see cref="KilnUnavailableException"/> when the task is not bound to that kind and no
    /// world of it runs.</summary>
    private ulong? Target(WorldKind world)
    {
        var line = Line(world);
        var live = _backend.WorldSession(world, out var state);
        lock (_gate)
        {
            if (_bound[line] != 0) return state == WorldSessionState.Live && live == _bound[line] ? _bound[line] : null;
            if (state != WorldSessionState.Live || live == 0) throw NoWorld(world);
            _bound[line] = live;
            return live;
        }
    }

    /// <summary><see cref="Target"/> for a hop: false when the task's world of that kind has ended, after cancelling the
    /// task (so the hop takes the cancelled path, quietly).</summary>
    internal bool TargetOrCancel(WorldKind world)
    {
        if (Target(world) is not null) return true;
        RequestCancel();
        return false;
    }

    /// <summary>The session a window of <paramref name="world"/> waits in; 0 for a cancelled task (also one whose world of
    /// that kind has ended, which this cancels): its await then throws the task's own OperationCanceledException.</summary>
    internal ulong WindowSession(WorldKind world)
    {
        if (Token.IsCancellationRequested) return 0;
        if (Target(world) is { } session) return session;
        RequestCancel();
        return 0;
    }

    private static KilnUnavailableException NoWorld(WorldKind world) => world switch
    {
        WorldKind.Client => new(Capability, "no live client world here (a dedicated server, the main menu or a loading screen): client-world work has nowhere to run"),
        WorldKind.Menu => new(Capability, "no main menu here (a world runs, or this is a dedicated server, or the game's UI frame has not run since the last world): menu work has nowhere to run"),
        _ => NoServerWorld(),
    };

    private static Exception Refused(TaskRefusal refusal, TaskBackend backend) => refusal switch
    {
        TaskRefusal.QueueFull => new KilnTaskQueueFullException(
            "this owner already holds as many tasks as Kiln allows (1024 that have not ended); nothing was started"),
        TaskRefusal.OwnerSealed => new InvalidOperationException("the mod is unloading: it cannot start tasks"),
        TaskRefusal.OwnerQuarantined => new KilnUnavailableException(Capability, "this owner's tasks were stopped after a native fault in one of them"),
        TaskRefusal.Shutdown => new KilnUnavailableException(Capability, "the game is exiting"),
        TaskRefusal.Argument => new ArgumentException("Kiln's task pool refused the task's arguments"),
        _ => new KilnUnavailableException(Capability, backend.UnavailableReason ?? $"Kiln's task pool refused the task ({refusal})"),
    };

    /// <summary>The native name rule: 1..128 UTF-8 bytes of valid text without control characters.</summary>
    internal static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0) throw new ArgumentException("a task needs a name", nameof(name));
        for (var i = 0; i < name.Length;)
        {
            if (Rune.DecodeFromUtf16(name.AsSpan(i), out var rune, out var used) != OperationStatus.Done)
                throw new ArgumentException("a task name must be valid text (it has a lone surrogate)", nameof(name));
            var cp = rune.Value;
            if (cp < 0x20 || cp == 0x7F || cp is >= 0x80 and <= 0x9F)
                throw new ArgumentException("a task name must not contain control characters", nameof(name));
            i += used;
        }
        if (Encoding.UTF8.GetByteCount(name) > NameMaxBytes)
            throw new ArgumentException($"a task name is at most {NameMaxBytes} UTF-8 bytes", nameof(name));
    }

    /// <summary>Why a blocking wait is refused on this thread; null where it is allowed (a thread of the mod's own).</summary>
    internal static string? BlockingRefused()
    {
        if (TaskExecutionScope.Current is not null) return "a task cannot block on a task: await it";
        var backend = TaskBackend.Current;
        if (backend is { OnPoolThread: true }) return "Kiln's task workers never block on a task: await it";
        if (Game.IsTickThread || backend is { OnServerTick: true }) return "the tick never waits for a task: await it";
        // a task may need a client-world hop, which only this thread runs: waiting here could never end
        if (backend is { OnClientMainThread: true }) return "the game's main thread never waits for a task: await it";
        return null;
    }

    // ---- the dispatcher ----

    /// <summary>The one dispatcher <see cref="TaskBackend.Install"/> binds. Never throws back into the backend.</summary>
    internal static void Dispatch(ulong lease, LeaseWork what, bool cancelled, object hostState)
    {
        if (hostState is not TaskRun run) return;
        try
        {
            run.SetLease(lease);
            if (what == LeaseWork.Segment) run.RunSegment(cancelled);
            else if (what == LeaseWork.TickHop) run.RunTickHop(WorldKind.Server, cancelled);
            else if (what == LeaseWork.ClientHop) run.RunTickHop(WorldKind.Client, cancelled);
            else if (what == LeaseWork.MenuHop) run.RunTickHop(WorldKind.Menu, cancelled);
            else run.KilnBug($"unknown dispatch {what}");
        }
        catch (Exception e)
        {
            run.KilnBug($"the task dispatcher threw: {e}");
        }
    }

    private void RunSegment(bool cancelled)
    {
        if (cancelled || _backend.IsCancelled(Lease)) CancelToken();
        bool first;
        GateState? gate;
        lock (_gate)
        {
            first = !_started;
            _started = true;
            gate = _gateArmed;
            _gateArmed = null;
        }
        if (first) Task.MarkRunning();
        using (TaskExecutionScope.Enter(this, onTick: false, _poolContext))
        {
            if (first) StartWork();
            if (gate is not null)
            {
                gate.Resumed(cancelled || Token.IsCancellationRequested);
                RunCallback(static s => ((Action)s!)(), gate.Continuation);
            }
            DrainPool();
        }
        EndSegment();
    }

    private void StartWork()
    {
        if (_flow is null)
        {
            var previous = TaskExecutionScope.FlowedIdentity;
            try
            {
                InvokeWork();
            }
            finally
            {
                TaskExecutionScope.SetFlowedIdentity(previous);
            }
        }
        else
        {
            ExecutionContext.Run(_flow, static s => ((TaskRun)s!).InvokeWork(), this);
        }
    }

    private void InvokeWork()
    {
        TaskExecutionScope.SetFlowedIdentity(Identity);
        try
        {
            switch (_work)
            {
                case Action<KilnTaskContext> action:
                    action(Context);
                    WorkEnded(null);
                    break;
                case Func<KilnTaskContext, Task> func:  // Task<T> work too (delegate variance)
                    var body = func(Context) ?? throw new InvalidOperationException("the task's work returned null instead of a Task");
                    if (body.IsCompleted) BodyTaskEnded(body);
                    else body.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() => BodyTaskEnded(body));
                    break;
                default:
                    throw new InvalidOperationException("unknown task work");
            }
        }
        catch (Exception e)
        {
            WorkEnded(e);
        }
    }

    private void BodyTaskEnded(Task body)
    {
        Exception? fault = null;
        try
        {
            body.GetAwaiter().GetResult();  // observes the fault: it is reported once, by Kiln
            Task.TakeResult(body);
        }
        catch (Exception e)
        {
            fault = e;
        }
        WorkEnded(fault);
    }

    /// <summary>The work returned (null) or threw: classified now, the task ends when its tick work has resolved too.</summary>
    private void WorkEnded(Exception? fault)
    {
        lock (_gate)
        {
            if (_bodyEnded) return;
            _bodyEnded = true;
            // only this task's own token, and only after a cancel was asked, ends it Cancelled
            if (fault is OperationCanceledException oce && oce.CancellationToken == Token && Token.IsCancellationRequested)
                _bodyCancel = oce;
            else
                _bodyFault = fault;
        }
        Wake();
    }

    private void DrainPool()
    {
        for (var n = 0; n < DrainPerSegment; n++)
        {
            (SendOrPostCallback Callback, object? State) next;
            lock (_gate)
                if (!_pool.TryDequeue(out next)) return;
            RunCallback(next.Callback, next.State);
        }
    }

    private void RunCallback(SendOrPostCallback callback, object? state)
    {
        try
        {
            callback(state);
        }
        catch (Exception e)
        {
            LateFault(e);  // work posted to the task's context threw (an await continuation never does)
        }
    }

    private void LateFault(Exception e)
    {
        lock (_gate) _lateFault ??= e;
    }

    // Under _gate: nothing of the task is left to run.
    private bool CanFinishLocked() =>
        _bodyEnded && _pool.Count == 0 && _tick[0].Count == 0 && _tick[1].Count == 0 && _tick[2].Count == 0 && _tickRunning == 0 &&
        _gateRequested is null && _gateArmed is null;

    /// <summary>The end of a segment, inside it: finish the lease, or arm one resumption and store it (or spend it at once
    /// when something is already waiting).</summary>
    private void EndSegment()
    {
        bool finish;
        lock (_gate)
        {
            finish = CanFinishLocked();
            if (finish) _finished = true;
        }
        if (finish)
        {
            TestHook?.Invoke(Point.BeforeFinish, this);
            var (state, fault, cancel) = Outcome();
            _backend.Finish(Lease, state switch
            {
                KilnTaskState.Completed => LeaseOutcome.Completed,
                KilnTaskState.Cancelled => LeaseOutcome.OwnTokenCancelled,
                _ => LeaseOutcome.Faulted,
            });
            TestHook?.Invoke(Point.BeforePublish, this);
            UnregisterLive();
            if (Task.Publish(state, fault, cancel) && fault is not null) LogFault(fault);
            TestHook?.Invoke(Point.AfterPublish, this);
            return;
        }
        var registration = _backend.Arm(Lease);
        GateState? gate = null;
        var resume = false;
        lock (_gate)
        {
            if (_gateRequested is { } g)
            {
                _gateRequested = null;
                gate = g;
            }
            else if (_pool.Count > 0 || CanFinishLocked())
            {
                resume = true;
            }
            else
            {
                _registration = registration;
            }
        }
        if (gate is not null) ArmGate(gate, registration);
        else if (resume) _backend.Resume(Lease, registration);
    }

    private (KilnTaskState, Exception?, OperationCanceledException?) Outcome()
    {
        lock (_gate)
        {
            if (_bodyFault is not null) return (KilnTaskState.Faulted, _bodyFault, null);
            if (_lateFault is not null) return (KilnTaskState.Faulted, _lateFault, null);
            if (_bodyCancel is not null) return (KilnTaskState.Cancelled, null, _bodyCancel);
            if (_tickWorkCancelled)
                return (KilnTaskState.Cancelled, null, new OperationCanceledException($"the task '{Name}' was cancelled before its tick work was applied", Token));
            return (KilnTaskState.Completed, null, null);
        }
    }

    /// <summary>Something changed that may need the task again: resume its stored registration, once, if there is work for
    /// a segment. While a segment runs (no stored registration) its end sees the change.</summary>
    private void Wake()
    {
        ulong registration;
        lock (_gate)
        {
            if (_registration == 0 || (_pool.Count == 0 && !CanFinishLocked())) return;
            registration = _registration;
            _registration = 0;
        }
        _backend.Resume(Lease, registration);
    }

    // ---- cancellation ----

    /// <summary><see cref="KilnTask.Cancel"/>, and Kiln's own cancels: the token (callbacks on the thread pool, never
    /// here) and the lease. A lease that never started ends Cancelled here.</summary>
    internal void RequestCancel()
    {
        CancelToken();
        var lease = Lease;
        if (lease == 0) return;
        if (_backend.Cancel(OwnerId, lease) != KilnTaskState.Cancelled) return;
        lock (_gate)
        {
            if (_started || _finished) return;
            _finished = true;
        }
        UnregisterLive();
        Task.Publish(KilnTaskState.Cancelled, null, new OperationCanceledException($"the task '{Name}' was cancelled before it started", Token));
    }

    /// <summary>The host retires the task's owner (unload, disable): the task is asked to stop, like a
    /// <see cref="KilnTask.Cancel"/>. <paramref name="hostState"/> is what <see cref="TaskBackend.Admit"/> got.</summary>
    internal static void CancelFromHost(object hostState)
    {
        if (hostState is TaskRun run) run.RequestCancel();
    }

    /// <summary>The assembly the task's work lives in (the host pins its load context while the task can run); null when
    /// <paramref name="hostState"/> is no task.</summary>
    internal static System.Reflection.Assembly? WorkAssemblyOf(object hostState) =>
        hostState is TaskRun run ? run._work.Method.Module.Assembly : null;

    /// <summary>The backend's end notification: the lease is quiescent, nothing of it will be dispatched again. A task
    /// that never got a segment (cancelled while queued by a seal, a revoke or its owner's retirement, which only the
    /// backend sees) ends Cancelled here. A task that started but never ended its lease itself (its worker faulted
    /// natively, so the core ended the lease) ends Faulted here, so nothing waits on it for ever.</summary>
    internal static void LeaseEnded(object hostState, KilnTaskState state)
    {
        if (hostState is TaskRun run) run.EndedByBackend(state);
    }

    private void EndedByBackend(KilnTaskState state)
    {
        bool started;
        lock (_gate)
        {
            if (_finished) return;  // the usual case: the task ended its lease itself, in its last segment
            _finished = true;
            started = _started;
        }
        CancelToken();
        UnregisterLive();
        if (!started || state == KilnTaskState.Cancelled)
        {
            Task.Publish(KilnTaskState.Cancelled, null,
                new OperationCanceledException($"the task '{Name}' was cancelled before it {(started ? "ended" : "started")}", Token));
            return;
        }
        var fault = new InvalidOperationException(
            $"the task '{Name}' was ended by Kiln's task pool ({state}) before its work returned: its worker faulted outside managed code");
        if (Task.Publish(KilnTaskState.Faulted, fault, null)) LogFault(fault);
    }

    /// <summary>The token, after asking the backend whether the lease was cancelled on the pool's side alone (a seal at the
    /// process exit, a revoke) while a segment runs: then the token is signalled here.</summary>
    internal bool PollCancelled()
    {
        if (!Token.IsCancellationRequested && Lease != 0 && _backend.IsCancelled(Lease)) CancelToken();
        return Token.IsCancellationRequested;
    }

    private void CancelToken()
    {
        if (_cts.IsCancellationRequested) return;
        var cancelling = _cts.CancelAsync();  // registered callbacks run on the thread pool
        if (!cancelling.IsCompleted)
            cancelling.ContinueWith(static (t, s) => ((TaskRun)s!).CallbackFault(t.Exception!), this,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        else if (cancelling.IsFaulted) CallbackFault(cancelling.Exception!);
    }

    private void CallbackFault(AggregateException e) =>
        Log(LogLevel.Warn, $"a callback registered on task '{Name}''s cancellation threw: {e.InnerException ?? e}");

    // ---- usable context ----

    /// <summary>The check every way to the tick starts with. A context kept after its work returned throws. While the
    /// mod unloads, the task's own work finds its task cancelled (the unload signals the token; this makes sure it is
    /// signalled by now), so the way to the tick ends the task Cancelled through its own token, as after a cancel, and
    /// nothing is logged. Code outside the task's work (a raw thread, Task.Run, code after ConfigureAwait(false)) that
    /// uses the context then throws instead: that is the mod's mistake, and it stays loud.</summary>
    internal void CheckUsable()
    {
        if (Volatile.Read(ref _bodyEnded)) throw new InvalidOperationException($"the task '{Name}' has ended: its KilnTaskContext no longer works");
        if (OwnerAlive) return;
        if (TaskExecutionScope.Current != this) throw new InvalidOperationException(UsedOutsideDuringUnload());
        RequestCancel();
    }

    private string UsedOutsideDuringUnload() => TaskExecutionScope.Current is { } other
        ? $"the mod is unloading, and this is the task '{other.Name}', not '{Name}': a KilnTaskContext works only inside its own task's work"
        : TaskExecutionScope.FlowedIdentity is { } from
            ? $"the mod is unloading, and this code left the task {from} (ConfigureAwait(false), Task.Run or a thread it started): only the task's own work ends cancelled here. Keep the awaits in the task: drop ConfigureAwait(false)."
            : $"the mod is unloading, and this thread is not running the task '{Name}': its KilnTaskContext works only inside the task's own work";

    private static KilnUnavailableException NoServerWorld() => new(Capability,
        "no live server world here (the main menu, or a client joined to someone else's server): tick work has nowhere to run");

    /// <summary>This thread is running this task's tick hop of <paramref name="world"/>, on that world's step, and nothing
    /// stops it there.</summary>
    internal bool OnOwnTick(WorldKind world) =>
        TaskExecutionScope.Current == this && TaskExecutionScope.OnTick && TaskExecutionScope.TickWorld == world &&
        _backend.OnWorldThread(world) && !Token.IsCancellationRequested && OwnerAlive;

    // ---- the tick ----

    private enum TickKind { Hop, Call, Post, Continuation }

    private sealed class TickItem(TickKind kind)
    {
        public readonly TickKind Kind = kind;
        public HopState? Hop;
        public Action? Continuation;   // Hop
        public Action? Run;            // Call (runs and completes its Task), Post (the apply)
        public Action? Cancel;         // Call
        public SendOrPostCallback? Callback;  // Continuation
        public object? State;
    }

    /// <summary>Queues <paramref name="item"/> and its hop for the task's session of <paramref name="world"/>, under the lock so
    /// the hop's dispatch finds it. <see cref="TaskRefusal.Argument"/> stands for "the task has ended"; NoServerWorld /
    /// NoWorld for a session that is not (any more) that world's live one, or none bound.</summary>
    private TaskRefusal EnqueueTick(TickItem item, WorldKind world)
    {
        var line = Line(world);
        lock (_gate)
        {
            if (_finished) return TaskRefusal.Argument;
            var session = _bound[line];
            if (session == 0) return world == WorldKind.Server ? TaskRefusal.NoServerWorld : TaskRefusal.NoWorld;  // the client world, the menu
            var node = _tick[line].AddLast(item);
            var refusal = _backend.TickHop(Lease, world, session);
            if (refusal != TaskRefusal.None) _tick[line].Remove(node);
            return refusal;
        }
    }

    private void RunTickHop(WorldKind world, bool cancelled)
    {
        var line = Line(world);
        TickItem item;
        lock (_gate)
        {
            if (_tick[line].First is not { } first)
            {
                KilnBug($"a {(world switch { WorldKind.Client => "client-world", WorldKind.Menu => "menu", _ => "tick" })} hop came with nothing queued for it");
                return;
            }
            _tick[line].RemoveFirst();
            item = first.Value;
            _tickRunning++;
        }
        try
        {
            var session = _backend.WorldSession(world, out var state);
            bool sameSession;
            lock (_gate) sameSession = state == WorldSessionState.Live && session != 0 && session == _bound[line];
            var live = sameSession && _backend.OnWorldThread(world) && OwnerAlive;
            var context = _tickContexts[line];
            if (live && !cancelled && !Token.IsCancellationRequested)
            {
                using (TaskExecutionScope.Enter(this, onTick: true, context, world)) RunTickItem(item);
            }
            else if (live && item.Kind == TickKind.Continuation)
            {
                // a continuation of code already on that world's step: cancellation is cooperative, it still runs there
                using (TaskExecutionScope.Enter(this, onTick: true, context, world)) RunTickItem(item);
            }
            else
            {
                CancelTickItem(item);
            }
        }
        finally
        {
            lock (_gate) _tickRunning--;
            Wake();
        }
    }

    private void RunTickItem(TickItem item)
    {
        switch (item.Kind)
        {
            case TickKind.Hop:
                item.Hop!.Ran();
                RunCallback(static s => ((Action)s!)(), item.Continuation);
                break;
            case TickKind.Call:
                item.Run!();  // completes its own Task, never throws
                break;
            case TickKind.Post:
                try
                {
                    item.Run!();
                }
                catch (Exception e)
                {
                    LateFault(e);
                }
                break;
            case TickKind.Continuation:
                RunCallback(item.Callback!, item.State);
                break;
        }
    }

    /// <summary>Tick work that cannot run on the tick: never runs there, and cancels the task. A hop and a continuation
    /// resume the method on a worker instead (the hop's await throws there), so it is never stranded.</summary>
    private void CancelTickItem(TickItem item)
    {
        RequestCancel();
        switch (item.Kind)
        {
            case TickKind.Hop:
                item.Hop!.Refused();
                PostToPool(static s => ((Action)s!)(), item.Continuation);
                break;
            case TickKind.Call:
                item.Cancel!();
                break;
            case TickKind.Post:
                lock (_gate) _tickWorkCancelled = true;
                break;
            case TickKind.Continuation:
                PostToPool(item.Callback!, item.State);
                break;
        }
    }

    /// <summary>The task's pool context: <c>await</c> in a segment comes back here, to a new segment of this task.</summary>
    private void PostToPool(SendOrPostCallback callback, object? state)
    {
        bool queued;
        lock (_gate)
        {
            queued = !_finished;
            if (queued) _pool.Enqueue((callback, state));
        }
        if (queued) Wake();
        // the task has ended: work that outlived it runs on the thread pool, outside any task
        else ThreadPool.UnsafeQueueUserWorkItem(static s => s.Callback(s.State), (Callback: callback, State: state), preferLocal: false);
    }

    /// <summary>The task's tick context of <paramref name="world"/>: <c>await</c> in a hop comes back to that world's step,
    /// through the task's hops.</summary>
    private void PostToTickContext(WorldKind world, SendOrPostCallback callback, object? state)
    {
        var refusal = EnqueueTick(new TickItem(TickKind.Continuation) { Callback = callback, State = state }, world);
        if (refusal == TaskRefusal.None) return;
        if (refusal == TaskRefusal.Argument)
        {
            // the task has ended: server tick code that outlived it continues as the mod's ordinary tick work; client
            // code that outlived it runs on the thread pool, outside any task (Kiln.Api has no way onto the main thread)
            if (world == WorldKind.Server && Mod is { } mod) mod.Scheduler.Post(() => callback(state));
            else ThreadPool.UnsafeQueueUserWorkItem(static s => s.Callback(s.State), (Callback: callback, State: state), preferLocal: false);
            return;
        }
        // its world's session is over: resume on a worker with the task cancelled
        RequestCancel();
        PostToPool(callback, state);
    }

    internal Task<T> OnTick<T>(WorldKind world, Func<T> apply)
    {
        CheckUsable();
        if (Token.IsCancellationRequested) return System.Threading.Tasks.Task.FromCanceled<T>(Token);
        if (OnOwnTick(world))
        {
            try
            {
                return System.Threading.Tasks.Task.FromResult(apply());
            }
            catch (Exception e)
            {
                return System.Threading.Tasks.Task.FromException<T>(e);
            }
        }
        if (!TargetOrCancel(world)) return System.Threading.Tasks.Task.FromCanceled<T>(Token);
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = Token;
        var item = new TickItem(TickKind.Call)
        {
            Run = () =>
            {
                try
                {
                    source.TrySetResult(apply());
                }
                catch (Exception e)
                {
                    source.TrySetException(e);
                }
            },
            Cancel = () => source.TrySetCanceled(token),
        };
        var refusal = EnqueueTick(item, world);
        if (refusal == TaskRefusal.None) return source.Task;
        ThrowIfEnded(refusal);
        RequestCancel();  // its session ended between the check and the queueing: the task has no such world left
        return System.Threading.Tasks.Task.FromCanceled<T>(Token);
    }

    internal void PostToTick(WorldKind world, Action apply)
    {
        CheckUsable();
        Token.ThrowIfCancellationRequested();
        if (!TargetOrCancel(world)) Token.ThrowIfCancellationRequested();
        var refusal = EnqueueTick(new TickItem(TickKind.Post) { Run = apply }, world);
        if (refusal == TaskRefusal.None) return;
        ThrowIfEnded(refusal);
        RequestCancel();
        Token.ThrowIfCancellationRequested();
    }

    private void ThrowIfEnded(TaskRefusal refusal)
    {
        if (refusal == TaskRefusal.Argument) throw new InvalidOperationException($"the task '{Name}' has ended: its KilnTaskContext no longer works");
    }

    /// <summary>One <c>await ctx.ToTick()</c>, to <paramref name="world"/>.</summary>
    internal sealed class HopState(TaskRun run, WorldKind world)
    {
        private const int Waiting = 0, FastPath = 1, Done = 2, Cancelled = 3;
        private int _state;

        internal bool CheckFastPath()
        {
            if (Volatile.Read(ref _state) is FastPath or Done) return true;
            if (!run.OnOwnTick(world)) return false;
            Interlocked.CompareExchange(ref _state, FastPath, Waiting);
            return true;
        }

        internal void Queue(Action continuation)
        {
            ArgumentNullException.ThrowIfNull(continuation);
            if (run.Token.IsCancellationRequested || !run.OwnerAlive)
            {
                run.RequestCancel();
                Refused();
                run.PostToPool(static s => ((Action)s!)(), continuation);
                return;
            }
            var item = new TickItem(TickKind.Hop) { Hop = this, Continuation = continuation };
            if (run.EnqueueTick(item, world) == TaskRefusal.None) return;
            // its world's session ended (or the task ended under a stray await): resume on a worker, cancelled
            run.RequestCancel();
            Refused();
            run.PostToPool(static s => ((Action)s!)(), continuation);
        }

        internal void Ran() => Volatile.Write(ref _state, Done);

        internal void Refused() => Volatile.Write(ref _state, Cancelled);

        internal void GetResult()
        {
            switch (Volatile.Read(ref _state))
            {
                case FastPath or Done:
                    return;
                case Cancelled:
                    throw new OperationCanceledException($"the task '{run.Name}' could not go to the tick: it was cancelled", run.Token);
                default:
                    throw new InvalidOperationException("await ctx.ToTick(): the hop has not run yet");
            }
        }
    }

    // ---- gate windows ----

    /// <summary>One <c>await ctx.NextGateWindow()</c>, for the task's session of <paramref name="world"/> (0: none, the task
    /// is cancelled).</summary>
    internal sealed class GateState(TaskRun run, WorldKind world, ulong session)
    {
        private const int Waiting = 0, Done = 1, Cancelled = 2, Second = 3;
        private int _state;

        internal ulong Session => session;
        internal WorldKind World => world;
        internal Action? Continuation { get; private set; }

        internal void Queue(Action continuation)
        {
            ArgumentNullException.ThrowIfNull(continuation);
            Continuation = continuation;
            run.QueueGate(this);
        }

        internal void Resumed(bool cancelled) => Volatile.Write(ref _state, cancelled ? Cancelled : Done);

        internal void Rejected() => Volatile.Write(ref _state, Second);

        internal void GetResult()
        {
            switch (Volatile.Read(ref _state))
            {
                case Done:
                    return;
                case Cancelled:
                    throw new OperationCanceledException($"the task '{run.Name}' was cancelled, or its server session ended, before the next gate window", run.Token);
                case Second:
                    throw new InvalidOperationException($"the task '{run.Name}' already waits for a gate window: one at a time");
                default:
                    throw new InvalidOperationException("await ctx.NextGateWindow(): the window has not come yet");
            }
        }
    }

    private void QueueGate(GateState gate)
    {
        if (Token.IsCancellationRequested)
        {
            gate.Resumed(cancelled: true);
            PostToPool(static s => ((Action)s!)(), gate.Continuation);
            return;
        }
        ulong registration = 0;
        var second = false;
        lock (_gate)
        {
            if (_gateRequested is not null || _gateArmed is not null)
            {
                second = true;  // another flow of this task already waits for a window: one at a time
            }
            else if (_registration == 0)
            {
                _gateRequested = gate;  // inside a segment (or one is on its way): its end arms the window
            }
            else
            {
                registration = _registration;  // suspended (e.g. on the tick): the stored registration waits for it
                _registration = 0;
            }
        }
        if (second)
        {
            gate.Rejected();
            PostToPool(static s => ((Action)s!)(), gate.Continuation);
        }
        else if (registration != 0)
        {
            ArmGate(gate, registration);
        }
    }

    private void ArmGate(GateState gate, ulong registration)
    {
        lock (_gate) _gateArmed = gate;
        var refusal = gate.Session != 0
            ? _backend.ResumeAfterStep(Lease, registration, gate.World, gate.Session)
            : TaskRefusal.NoWorld;
        if (refusal == TaskRefusal.None) return;
        // the session it was asked for is over: never moved into another world; the registration is still armed
        lock (_gate)
        {
            _gateArmed = null;
            _pool.Enqueue((static s => ((Action)s!)(), gate.Continuation));
        }
        RequestCancel();
        gate.Resumed(cancelled: true);
        _backend.Resume(Lease, registration);
    }

    // ---- logging ----

    private void LogFault(Exception fault) => Log(LogLevel.Error, $"task '{Name}' faulted: {fault}");

    private void KilnBug(string what) => Log(LogLevel.Error, $"Kiln task '{Name}': {what} (a bug in Kiln)");

    private void Log(LogLevel level, string text)
    {
        try
        {
            Kiln.Backend.Current.Log(OwnerId, level, text);
        }
        catch
        {
            // logging never takes a task down
        }
    }

    // ---- synchronization contexts ----

    private sealed class PoolContext(TaskRun run) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            ArgumentNullException.ThrowIfNull(d);
            run.PostToPool(d, state);
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            ArgumentNullException.ThrowIfNull(d);
            if (TaskExecutionScope.Current != run || TaskExecutionScope.OnTick)
                throw new InvalidOperationException("Send works only inside this task's own work");
            d(state);
        }

        public override SynchronizationContext CreateCopy() => this;
    }

    private sealed class TickContext(TaskRun run, WorldKind world) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            ArgumentNullException.ThrowIfNull(d);
            run.PostToTickContext(world, d, state);
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            ArgumentNullException.ThrowIfNull(d);
            if (TaskExecutionScope.Current != run || !TaskExecutionScope.OnTick || TaskExecutionScope.TickWorld != world)
                throw new InvalidOperationException("Send works only inside this task's tick work");
            d(state);
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
