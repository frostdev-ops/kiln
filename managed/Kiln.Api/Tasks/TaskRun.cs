// SPDX-License-Identifier: MIT
using System.Buffers;
using System.Text;

namespace Kiln;

/// <summary>
/// One C# task on the pool: one lease of <see cref="TaskBackend"/>, the host state its dispatches carry. It runs the
/// work's pieces (segments on workers, hops on the tick) inside <see cref="TaskExecutionScope"/>, keeps the work's tick
/// items in order, and ends the lease exactly once, when the work and everything it handed to the tick have resolved.
/// </summary>
/// <remarks>
/// <para>Suspension: every segment that does not end the task arms one registration before it returns and stores it
/// here. Whatever needs the task again (a continuation posted to its pool context, the work's end, the last tick item
/// resolving) takes that registration, once, and resumes it. A stored registration means the task holds no worker.</para>
/// <para>Locks: <c>_gate</c> guards this object's state. No user code runs under it; the backend calls made under it
/// (<see cref="TaskBackend.TickHop"/>) never call back into the host.</para>
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
    private readonly TickContext _tickContext;
    private long _lease;

    // under _gate
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _pool = new();
    private readonly LinkedList<TickItem> _tick = new();
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

    private TaskRun(TaskBackend backend, Owner owner, string name, Delegate work, KilnTask task)
    {
        _backend = backend;
        _work = work;
        _flow = ExecutionContext.Capture();
        _poolContext = new PoolContext(this);
        _tickContext = new TickContext(this);
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
        var run = new TaskRun(backend, owner, name, work, task);
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
            else if (what == LeaseWork.TickHop) run.RunTickHop(cancelled);
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
        _bodyEnded && _pool.Count == 0 && _tick.Count == 0 && _tickRunning == 0 && _gateRequested is null && _gateArmed is null;

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

    internal void CheckUsable()
    {
        if (Volatile.Read(ref _bodyEnded)) throw new InvalidOperationException($"the task '{Name}' has ended: its KilnTaskContext no longer works");
        if (!OwnerAlive) throw new InvalidOperationException("the mod is unloading");
    }

    /// <summary>The live server session, or <see cref="KilnUnavailableException"/> naming tasks.pool.</summary>
    internal ulong RequireServerWorld()
    {
        var session = _backend.ServerSession;
        return session != 0 ? session : throw NoServerWorld();
    }

    private static KilnUnavailableException NoServerWorld() => new(Capability,
        "no live server world here (the main menu, or a client joined to someone else's server): tick work has nowhere to run");

    /// <summary>This thread is running this task's tick hop, on the server tick, and nothing stops it there.</summary>
    internal bool OnOwnTick =>
        TaskExecutionScope.Current == this && TaskExecutionScope.OnTick && _backend.OnServerTick && !Token.IsCancellationRequested && OwnerAlive;

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

    /// <summary>Queues <paramref name="item"/> and its hop, under the lock so the hop's dispatch finds it.
    /// <see cref="TaskRefusal.Argument"/> stands for "the task has ended".</summary>
    private TaskRefusal EnqueueTick(TickItem item)
    {
        lock (_gate)
        {
            if (_finished) return TaskRefusal.Argument;
            var node = _tick.AddLast(item);
            var refusal = _backend.TickHop(Lease);
            if (refusal != TaskRefusal.None) _tick.Remove(node);
            return refusal;
        }
    }

    private void RunTickHop(bool cancelled)
    {
        TickItem item;
        lock (_gate)
        {
            if (_tick.First is not { } first)
            {
                KilnBug("a tick hop came with nothing queued for it");
                return;
            }
            _tick.RemoveFirst();
            item = first.Value;
            _tickRunning++;
        }
        try
        {
            var live = _backend.OnServerTick && _backend.ServerSession != 0 && OwnerAlive;
            if (live && !cancelled && !Token.IsCancellationRequested)
            {
                using (TaskExecutionScope.Enter(this, onTick: true, _tickContext)) RunTickItem(item);
            }
            else if (live && item.Kind == TickKind.Continuation)
            {
                // a continuation of code already on the tick: cancellation is cooperative, it still runs there
                using (TaskExecutionScope.Enter(this, onTick: true, _tickContext)) RunTickItem(item);
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

    /// <summary>The task's tick context: <c>await</c> in a tick hop comes back to the tick, through the task's hops.</summary>
    private void PostToTickContext(SendOrPostCallback callback, object? state)
    {
        var refusal = EnqueueTick(new TickItem(TickKind.Continuation) { Callback = callback, State = state });
        if (refusal == TaskRefusal.None) return;
        if (refusal == TaskRefusal.Argument)
        {
            // the task has ended: tick code that outlived it continues as the mod's ordinary tick work
            if (Mod is { } mod) mod.Scheduler.Post(() => callback(state));
            else ThreadPool.UnsafeQueueUserWorkItem(static s => s.Callback(s.State), (Callback: callback, State: state), preferLocal: false);
            return;
        }
        // no server world to come back to: resume on a worker with the task cancelled
        RequestCancel();
        PostToPool(callback, state);
    }

    internal Task<T> OnTick<T>(Func<T> apply)
    {
        CheckUsable();
        if (Token.IsCancellationRequested) return System.Threading.Tasks.Task.FromCanceled<T>(Token);
        if (OnOwnTick)
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
        RequireServerWorld();
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
        ThrowIfRefused(EnqueueTick(item));
        return source.Task;
    }

    internal void PostToTick(Action apply)
    {
        CheckUsable();
        Token.ThrowIfCancellationRequested();
        RequireServerWorld();
        ThrowIfRefused(EnqueueTick(new TickItem(TickKind.Post) { Run = apply }));
    }

    private void ThrowIfRefused(TaskRefusal refusal)
    {
        if (refusal == TaskRefusal.None) return;
        if (refusal == TaskRefusal.Argument) throw new InvalidOperationException($"the task '{Name}' has ended: its KilnTaskContext no longer works");
        throw NoServerWorld();
    }

    /// <summary>One <c>await ctx.ToTick()</c>.</summary>
    internal sealed class HopState(TaskRun run)
    {
        private const int Waiting = 0, FastPath = 1, Done = 2, Cancelled = 3;
        private int _state;

        internal bool CheckFastPath()
        {
            if (Volatile.Read(ref _state) is FastPath or Done) return true;
            if (!run.OnOwnTick) return false;
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
            if (run.EnqueueTick(item) == TaskRefusal.None) return;
            // the world went away (or the task ended under a stray await): resume on a worker, cancelled
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

    /// <summary>One <c>await ctx.NextGateWindow()</c>, for the session that was live when it was asked for.</summary>
    internal sealed class GateState(TaskRun run, ulong session)
    {
        private const int Waiting = 0, Done = 1, Cancelled = 2, Second = 3;
        private int _state;

        internal ulong Session => session;
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
        var refusal = gate.Session == _backend.ServerSession
            ? _backend.ResumeAfterStep(Lease, registration, gate.Session)
            : TaskRefusal.NoServerWorld;
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

    private sealed class TickContext(TaskRun run) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            ArgumentNullException.ThrowIfNull(d);
            run.PostToTickContext(d, state);
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            ArgumentNullException.ThrowIfNull(d);
            if (TaskExecutionScope.Current != run || !TaskExecutionScope.OnTick)
                throw new InvalidOperationException("Send works only inside this task's tick work");
            d(state);
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
