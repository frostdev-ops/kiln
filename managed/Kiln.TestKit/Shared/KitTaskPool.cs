// SPDX-License-Identifier: MIT
// Kiln's task pool with no core: the managed host's lease table (native/core/src/tasks_host.h) over real worker threads,
// with the native rules: segments of one lease never overlap, one armed registration at a time, finish only inside a
// running segment, tick hops only in a live session of their world (the server world, and the client world for #82's
// client-world hops), after-step resumptions tied to their world's session, a queued lease cancelled without a dispatch. The harness runs it (KilnHarness.Tasks.cs: hops before the mods' Pre, the Post edge after
// their Post); Kiln's own unit tests run it under a tick thread of their own (Kiln.Api.Tests/FakeTaskBackend.cs). Misuse
// the native table answers with INTERNAL is recorded in Errors (and thrown); a dispatch that leaves its thread's scope,
// mod, context or flowed identity changed is recorded in Leaks. Both must stay empty.
namespace Kiln.TestKit;

internal class KitTaskPool : TaskBackend, IDisposable
{
    internal sealed class LeaseRecord
    {
        public ulong Id, Owner;
        public string Name = "";
        public KilnTaskOptions Options;
        public object HostState = null!;
        public KilnTaskState State = KilnTaskState.Queued;
        public bool InReady, SegmentRunning, Armed, ResumePending, AfterPending, Waiting, Finished, Cancel;
        public ulong Registration, Registrations;
        public (WorldKind World, ulong Session, ulong Edge) AfterAt, WaitAt;
        public LeaseOutcome Outcome;
        public int HopsPending;
        public bool Terminal => State is KilnTaskState.Completed or KilnTaskState.Faulted or KilnTaskState.Cancelled;
    }

    private readonly object _lock = new();
    private readonly List<Thread> _workers = [];
    private readonly LinkedList<LeaseRecord> _ready = new();
    private readonly Dictionary<ulong, LeaseRecord> _leases = new();
    private readonly HashSet<ulong> _sealed = [];
    private readonly Func<bool> _onServerTick;
    private readonly Func<bool>? _onClientThread;
    private readonly bool _notifyEnded;
    private LeaseDispatch? _dispatch;
    private ulong _nextLease = 0x1000;

    // One world's sessions, Post edges and pending hops: [0] the server world, [1] the client world.
    private sealed class WorldLine
    {
        public ulong Session;   // the live session; 0 with none
        public ulong Last;      // the newest session started (kept after its stop)
        public ulong Sessions;  // sessions started so far (the next one's number - 1)
        public ulong Edge;
        public bool Stopping;   // its STOPPING handlers run (the world still exists)
        public readonly List<LeaseRecord> Hops = [];
    }

    private readonly WorldLine[] _worlds = [new(), new()];
    private static int Line(WorldKind world) => world == WorldKind.Client ? 1 : 0;
    private int _running;
    private bool _disposed;
    [ThreadStatic] private static KitTaskPool? t_worker;

    public readonly List<string> Errors = [];
    public readonly List<string> Leaks = [];
    public readonly List<(ulong Owner, string Name, KilnTaskOptions Options)> Admitted = [];
    public int OwnerCapacity = 1024;
    public string? Unavailable;
    public int HopsQueued;
    public int Edges;

    /// <param name="workers">Pool worker threads.</param>
    /// <param name="onServerTick">True on the thread that runs the server world's tick (where hops run).</param>
    /// <param name="threadName">The workers' names, numbered.</param>
    /// <param name="notifyEnded">Tell Kiln.Api when a lease is quiescent (the table's <c>bind_ended</c>), as Kiln.Host does.</param>
    /// <param name="onClientThread">True on the game client's main thread (the client world's, where client-world hops
    /// run); null for a pool with no client (a dedicated server).</param>
    public KitTaskPool(int workers, Func<bool> onServerTick, string threadName, bool notifyEnded, Func<bool>? onClientThread = null)
    {
        _onServerTick = onServerTick;
        _onClientThread = onClientThread;
        _notifyEnded = notifyEnded;
        for (var i = 0; i < workers; i++)
        {
            var t = new Thread(WorkerLoop) { IsBackground = true, Name = threadName + i };
            _workers.Add(t);
            t.Start();
        }
    }

    public bool IsWorker(int managedThreadId) => _workers.Any(w => w.ManagedThreadId == managedThreadId);

    // ---- TaskBackend ----

    public override string? UnavailableReason => Unavailable;
    public override bool OnPoolThread => t_worker == this;
    public override bool OnServerTick => _onServerTick();

    public override ulong ServerSession
    {
        get
        {
            lock (_lock) return _worlds[0].Session;
        }
    }

    public override bool OnClientMainThread => _onClientThread?.Invoke() ?? false;

    public override bool OnWorldThread(WorldKind world) => world switch
    {
        WorldKind.Server => _onServerTick(),
        WorldKind.Client => _onClientThread?.Invoke() ?? false,
        _ => false,
    };

    public override ulong WorldSession(WorldKind world, out WorldSessionState state)
    {
        lock (_lock)
        {
            if (world is not (WorldKind.Server or WorldKind.Client))
            {
                state = WorldSessionState.None;
                return 0;
            }
            var w = _worlds[Line(world)];
            state = w.Session != 0 ? WorldSessionState.Live
                : w.Last == 0 ? WorldSessionState.None
                : w.Stopping ? WorldSessionState.Stopping
                : WorldSessionState.Ended;
            return w.Session != 0 ? w.Session : w.Last;
        }
    }

    public override void Bind(LeaseDispatch dispatch) => _dispatch = dispatch;

    public override TaskRefusal Admit(ulong owner, string name, KilnTaskOptions options, object hostState, out ulong lease)
    {
        lock (_lock)
        {
            lease = 0;
            if (_disposed) return TaskRefusal.Shutdown;
            if (_sealed.Contains(owner)) return TaskRefusal.OwnerSealed;
            if (_leases.Values.Count(l => l.Owner == owner) >= OwnerCapacity) return TaskRefusal.QueueFull;
            var l = new LeaseRecord { Id = ++_nextLease, Owner = owner, Name = name, Options = options, HostState = hostState };
            _leases[l.Id] = l;
            Admitted.Add((owner, name, options));
            Ready(l);
            lease = l.Id;
            return TaskRefusal.None;
        }
    }

    public override ulong Arm(ulong lease)
    {
        lock (_lock)
        {
            var l = Find(lease);
            if (l is null || !l.SegmentRunning || l.Armed || l.ResumePending || l.AfterPending || l.Finished) throw Misuse("arm", l);
            l.Armed = true;
            l.Registration = ++l.Registrations;
            return l.Registration;
        }
    }

    public override void Resume(ulong lease, ulong registration)
    {
        lock (_lock)
        {
            var l = Find(lease);
            if (l is null || !l.Armed || l.Registration != registration) throw Misuse("resume", l);
            l.Armed = false;
            l.Registration = 0;
            if (l.SegmentRunning) l.ResumePending = true;  // segments never overlap
            else Ready(l);
        }
    }

    public override TaskRefusal ResumeAfterStep(ulong lease, ulong registration, ulong session) =>
        ResumeAfterStep(lease, registration, WorldKind.Server, session);

    public override TaskRefusal ResumeAfterStep(ulong lease, ulong registration, WorldKind world, ulong session)
    {
        lock (_lock)
        {
            var l = Find(lease);
            if (l is null || !l.Armed || l.Registration != registration || session == 0 || world is not (WorldKind.Server or WorldKind.Client))
                throw Misuse("resume_after_step", l);
            var w = _worlds[Line(world)];
            if (w.Session == 0 || session != w.Session)
                return world == WorldKind.Server ? TaskRefusal.NoServerWorld : TaskRefusal.NoWorld;  // still armed
            l.Armed = false;
            l.Registration = 0;
            if (l.SegmentRunning)
            {
                l.AfterPending = true;
                l.AfterAt = (world, session, w.Edge);
            }
            else
            {
                PlaceAfter(l, (world, session, w.Edge));
            }
            return TaskRefusal.None;
        }
    }

    public override TaskRefusal TickHop(ulong lease)
    {
        lock (_lock) return PushHop(lease, WorldKind.Server, 0, checkSession: false);
    }

    public override TaskRefusal TickHop(ulong lease, WorldKind world, ulong session)
    {
        lock (_lock) return PushHop(lease, world, session, checkSession: true);
    }

    // Under _lock: a hop for `world` (its live session, or only `session`), never queued into another session.
    private TaskRefusal PushHop(ulong lease, WorldKind world, ulong session, bool checkSession)
    {
        var l = Find(lease);
        if (l is null || l.Terminal || world is not (WorldKind.Server or WorldKind.Client)) throw Misuse("tick_hop", l);
        var w = _worlds[Line(world)];
        if (w.Session == 0 || (checkSession && session != w.Session))
            return world == WorldKind.Server ? TaskRefusal.NoServerWorld : TaskRefusal.NoWorld;
        l.HopsPending++;
        w.Hops.Add(l);
        HopsQueued++;
        Monitor.PulseAll(_lock);
        return TaskRefusal.None;
    }

    public override void Finish(ulong lease, LeaseOutcome outcome)
    {
        lock (_lock)
        {
            var l = Find(lease);
            if (l is null || !l.SegmentRunning || l.Finished || l.Armed || l.ResumePending || l.AfterPending) throw Misuse("finish", l);
            l.Finished = true;
            l.Outcome = outcome;
        }
    }

    public override bool IsCancelled(ulong lease)
    {
        lock (_lock) return Find(lease)?.Cancel ?? false;
    }

    public override KilnTaskState Cancel(ulong owner, ulong lease)
    {
        LeaseRecord? ended = null;
        KilnTaskState state;
        lock (_lock)
        {
            var l = Find(lease);
            if (l is null || l.Owner != owner) return KilnTaskState.None;
            if (l.State == KilnTaskState.Queued)
            {
                _ready.Remove(l);
                l.InReady = false;
                l.State = KilnTaskState.Cancelled;  // never dispatched
                ended = MaybeDrop(l);
                state = KilnTaskState.Cancelled;
            }
            else
            {
                if (l.State == KilnTaskState.Running)
                {
                    l.Cancel = true;
                    ReleaseContinuation(l);
                }
                state = l.State;
            }
        }
        Ended(ended);
        return state;
    }

    public override KilnTaskState Status(ulong owner, ulong lease)
    {
        lock (_lock)
        {
            var l = Find(lease);
            return l is null || l.Owner != owner ? KilnTaskState.None : l.State;
        }
    }

    public override int SealOwner(ulong owner)
    {
        var ended = new List<LeaseRecord>();
        int left;
        lock (_lock)
        {
            _sealed.Add(owner);
            foreach (var l in _leases.Values.Where(l => l.Owner == owner).ToList())
            {
                if (l.State == KilnTaskState.Queued)
                {
                    _ready.Remove(l);
                    l.InReady = false;
                    l.State = KilnTaskState.Cancelled;
                    if (MaybeDrop(l) is { } e) ended.Add(e);
                }
                else if (l.State == KilnTaskState.Running)
                {
                    l.Cancel = true;
                    ReleaseContinuation(l);
                }
            }
            left = _leases.Values.Count(l => l.Owner == owner);
        }
        foreach (var e in ended) Ended(e);
        return left;
    }

    public override IReadOnlyList<LeaseRow> Snapshot()
    {
        lock (_lock)
            return [.. _leases.Values.Where(l => l.State == KilnTaskState.Running)
                .Select(l => new LeaseRow(l.Owner, l.Id, l.State, l.Name, TimeSpan.Zero))];
    }

    // ---- the worlds: the caller is the thread that runs that world (the server world's tick; the client's main thread) ----

    /// <summary>A new session of <paramref name="world"/> starts (its first step). A session that stopped without a stop
    /// event left hops: they are dispatched cancelled here, never carried into the new one.</summary>
    public void StartSession(WorldKind world = WorldKind.Server)
    {
        List<LeaseRecord> old;
        lock (_lock)
        {
            var w = _worlds[Line(world)];
            old = [.. w.Hops];
            w.Hops.Clear();
            w.Session = w.Last = ++w.Sessions;
            w.Edge = 0;
            w.Stopping = false;
        }
        foreach (var h in old) DispatchHop(h, cancelled: true, world);
    }

    /// <summary>The live session's STOPPING of <paramref name="world"/>, on the calling thread (the one that ends the
    /// world): every pending hop of that world is dispatched cancelled, every after-step resumption of the session is
    /// released with its cancellation set. The other world is untouched.</summary>
    public void StopSession(WorldKind world = WorldKind.Server)
    {
        List<LeaseRecord> hops;
        lock (_lock)
        {
            var w = _worlds[Line(world)];
            var ended = w.Session;
            w.Session = 0;
            hops = [.. w.Hops];
            w.Hops.Clear();
            foreach (var l in _leases.Values.Where(l => l.Waiting && l.WaitAt.World == world && l.WaitAt.Session == ended).ToList())
            {
                l.Waiting = false;
                l.Cancel = true;
                Ready(l);
            }
        }
        foreach (var h in hops) DispatchHop(h, cancelled: true, world);
    }

    /// <summary>Between a world's STOPPING and the end of its handlers: the world still exists (a task started there is
    /// bound to the ending session). The harness sets it around its stop events.</summary>
    public void SetStopping(WorldKind world, bool stopping)
    {
        lock (_lock) _worlds[Line(world)].Stopping = stopping;
    }

    /// <summary>The hops of <paramref name="world"/> queued before now, on the calling thread (that world's step, before its
    /// Pre), each with its lease's cancellation. A hop queued while these run waits for the next call.</summary>
    public void RunHops(WorldKind world = WorldKind.Server)
    {
        List<LeaseRecord> hops;
        lock (_lock)
        {
            var w = _worlds[Line(world)];
            hops = [.. w.Hops];
            w.Hops.Clear();
        }
        foreach (var h in hops)
        {
            bool cancel;
            lock (_lock) cancel = h.Cancel;
            DispatchHop(h, cancel, world);
        }
    }

    /// <summary>The live session's Post edge of <paramref name="world"/>: the after-step resumptions asked for before it
    /// become ready.</summary>
    public void PostEdge(WorldKind world = WorldKind.Server)
    {
        lock (_lock)
        {
            var w = _worlds[Line(world)];
            if (w.Session == 0) return;
            w.Edge++;
            Edges++;
            foreach (var l in _leases.Values.Where(l => l.Waiting && l.WaitAt.World == world && l.WaitAt.Session == w.Session && l.WaitAt.Edge < w.Edge).ToList())
            {
                l.Waiting = false;
                Ready(l);
            }
        }
    }

    /// <summary>The live session of <paramref name="world"/>; 0 with none.</summary>
    public ulong Session(WorldKind world)
    {
        lock (_lock) return _worlds[Line(world)].Session;
    }

    /// <summary>The server world's pending hops.</summary>
    public int PendingHops => PendingHopsOf(WorldKind.Server);

    public int PendingHopsOf(WorldKind world)
    {
        lock (_lock) return _worlds[Line(world)].Hops.Count;
    }

    /// <summary>Leases not yet dropped (alive tasks, or ended ones with a hop still to dispatch).</summary>
    public int Live
    {
        get
        {
            lock (_lock) return _leases.Count;
        }
    }

    /// <summary>Leases that wait for the next Post edge.</summary>
    public int WaitingForEdge
    {
        get
        {
            lock (_lock) return _leases.Values.Count(l => l.Waiting);
        }
    }

    /// <summary>The live leases as "owner/name (state, what it waits for)", for a test's failure message.</summary>
    public IReadOnlyList<string> Describe(Func<ulong, string> owner)
    {
        lock (_lock)
            return [.. _leases.Values.Select(l => $"{owner(l.Owner)}/{l.Name} ({l.State}, " +
                (l.SegmentRunning ? "a segment is running" : l.InReady ? "ready" : l.Waiting ? "waits for the next step" :
                 l.HopsPending > 0 ? "a tick hop is queued" : l.Armed ? "suspended: awaits something" : "ending") + ")")];
    }

    /// <summary>A copy of <see cref="Errors"/>, taken under the pool's lock.</summary>
    public string[] ErrorsNow()
    {
        lock (_lock) return [.. Errors];
    }

    public LeaseRecord? Lease(string name)
    {
        lock (_lock) return _leases.Values.LastOrDefault(l => l.Name == name);
    }

    /// <summary>Waits until no segment runs and none is ready: everything left is suspended, waiting for a hop, an edge
    /// or the test. False at the deadline.</summary>
    public bool TryWaitIdle(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_lock)
        {
            while (_ready.Count > 0 || _running > 0)
            {
                var left = deadline - Environment.TickCount64;
                if (left <= 0 || !Monitor.Wait(_lock, TimeSpan.FromMilliseconds(left))) return _ready.Count == 0 && _running == 0;
            }
            return true;
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for work to arrive (a segment ready or running, a hop queued);
    /// true when there is some.</summary>
    public bool WaitForWork(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_lock)
        {
            while (_ready.Count == 0 && _running == 0 && _worlds[0].Hops.Count == 0 && _worlds[1].Hops.Count == 0 &&
                   !_leases.Values.Any(l => l.Waiting))
            {
                var left = deadline - Environment.TickCount64;
                if (left <= 0 || !Monitor.Wait(_lock, TimeSpan.FromMilliseconds(left))) return false;
            }
            return true;
        }
    }

    public void WaitIdle(int timeoutMs = 20000)
    {
        if (!TryWaitIdle(TimeSpan.FromMilliseconds(timeoutMs)))
            lock (_lock) throw new TimeoutException($"the task pool did not go idle ({_ready.Count} ready, {_running} running)");
    }

    public virtual void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            Monitor.PulseAll(_lock);
        }
        foreach (var w in _workers) w.Join(TimeSpan.FromSeconds(5));
    }

    // ---- internals ----

    private LeaseRecord? Find(ulong lease) => _leases.GetValueOrDefault(lease);

    private InvalidOperationException Misuse(string member, LeaseRecord? l)
    {
        var what = $"{member} misused on lease {(l is null ? "?" : l.Name)} (the native table's INTERNAL)";
        Errors.Add(what);
        return new InvalidOperationException(what);
    }

    private void Ready(LeaseRecord l)
    {
        l.InReady = true;
        _ready.AddLast(l);
        Monitor.PulseAll(_lock);
    }

    private void PlaceAfter(LeaseRecord l, (WorldKind World, ulong Session, ulong Edge) at)
    {
        if (at.Session != _worlds[Line(at.World)].Session)
        {
            l.Cancel = true;  // its session is over: released cancelled, never carried into another
            Ready(l);
        }
        else if (l.Cancel)
        {
            Ready(l);
        }
        else
        {
            l.Waiting = true;
            l.WaitAt = at;
            Monitor.PulseAll(_lock);
        }
    }

    private void ReleaseContinuation(LeaseRecord l)
    {
        if (l.AfterPending)
        {
            l.AfterPending = false;
            l.ResumePending = true;
        }
        else if (l.Waiting)
        {
            l.Waiting = false;
            Ready(l);
        }
    }

    // The backend drops terminal records; with the end notification on, the dropped lease is handed back (to notify
    // outside the lock).
    private LeaseRecord? MaybeDrop(LeaseRecord l)
    {
        if (!l.Terminal || l.HopsPending != 0) return null;
        _leases.Remove(l.Id);
        Monitor.PulseAll(_lock);
        return _notifyEnded ? l : null;
    }

    private static void Ended(LeaseRecord? l)
    {
        if (l is not null) TaskRun.LeaseEnded(l.HostState, l.State);
    }

    private LeaseRecord? SegmentEnded(LeaseRecord l)
    {
        l.SegmentRunning = false;
        if (l.Finished)
        {
            l.State = l.Outcome switch
            {
                LeaseOutcome.Completed => KilnTaskState.Completed,
                LeaseOutcome.OwnTokenCancelled when l.Cancel => KilnTaskState.Cancelled,
                _ => KilnTaskState.Faulted,
            };
            return MaybeDrop(l);
        }
        if (l.ResumePending)
        {
            l.ResumePending = false;
            Ready(l);
            return null;
        }
        if (l.AfterPending)
        {
            l.AfterPending = false;
            PlaceAfter(l, l.AfterAt);
            return null;
        }
        if (l.Armed) return null;
        Errors.Add($"lease {l.Name}: a segment returned neither armed nor finished");
        l.State = KilnTaskState.Faulted;
        return MaybeDrop(l);
    }

    private static (TaskRun?, ModContext?, SynchronizationContext?, string?) Observed() =>
        (TaskExecutionScope.Current, ModContext.CurrentMod, SynchronizationContext.Current, TaskExecutionScope.FlowedIdentity);

    private void CheckLeak(string where, (TaskRun?, ModContext?, SynchronizationContext?, string?) before)
    {
        var after = Observed();
        if (after != before) lock (_lock) Leaks.Add($"{where} left {after} (was {before})");
    }

    private void DispatchHop(LeaseRecord h, bool cancelled, WorldKind world)
    {
        var before = Observed();
        try
        {
            _dispatch!(h.Id, world == WorldKind.Client ? LeaseWork.ClientHop : LeaseWork.TickHop, cancelled, h.HostState);
        }
        catch (Exception e)
        {
            lock (_lock) Errors.Add($"the dispatcher threw on a hop: {e}");
        }
        CheckLeak("a tick hop", before);
        LeaseRecord? ended;
        lock (_lock)
        {
            h.HopsPending--;
            ended = MaybeDrop(h);
        }
        Ended(ended);
    }

    private void WorkerLoop()
    {
        t_worker = this;
        while (true)
        {
            LeaseRecord l;
            bool cancel;
            lock (_lock)
            {
                while (!_disposed && _ready.Count == 0) Monitor.Wait(_lock);
                if (_disposed) return;
                l = _ready.First!.Value;
                _ready.RemoveFirst();
                l.InReady = false;
                l.SegmentRunning = true;
                l.State = KilnTaskState.Running;
                cancel = l.Cancel;
                _running++;
            }
            var before = Observed();
            try
            {
                _dispatch!(l.Id, LeaseWork.Segment, cancel, l.HostState);
            }
            catch (Exception e)
            {
                lock (_lock) Errors.Add($"the dispatcher threw on a segment: {e}");
            }
            CheckLeak("a segment", before);
            LeaseRecord? ended;
            lock (_lock)
            {
                ended = SegmentEnded(l);
                _running--;
                Monitor.PulseAll(_lock);
            }
            Ended(ended);
        }
    }
}
