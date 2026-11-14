// SPDX-License-Identifier: MIT
// The managed side of kiln.queries' path queue: one private lease per mod instance and server session, one consumer.
// On the service tick it takes the updates the service has, in order, posts PathQueryChangedEvent as the mod that asked,
// and completes the request's task after the event of its end. It runs on the service tick, never inside RequestPath:
// nothing is delivered from within the call that asked. A mod that unloads has its lease retired: its tasks end
// OwnerRetired with no event. When the server world stops, tasks that still wait end WorldClosed at once.
using System.Collections.Concurrent;

namespace Kiln;

internal static unsafe class PathPump
{
    private sealed class Pending
    {
        public required ulong Owner;
        public required ModContext? Mod;
        public required EntityId Anchor;
        public ulong AdmittedStep;
        public uint Generation;
        public QueryPathState State = QueryPathState.Queued;
        public readonly TaskCompletionSource<QueryPathResult> Source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<ulong, Pending> ByRequest = new();
    private static readonly HashSet<ulong> Leases = new();
    private static readonly Dictionary<ulong, uint> Generations = new();
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<ModContext, (ulong Owner, long Session, long Registry)> ModLeases = new(ReferenceEqualityComparer.Instance);
    private static readonly ConcurrentQueue<(IKilnBackend Backend, ulong Owner)> Revokes = new();
    private static readonly ConcurrentQueue<ulong> CancelRequests = new();
    private static readonly List<(ulong Owner, QueriesNative.PathUpdate Update, QueriesNative.PathPoint[] Points)> Deferred = [];
    private static bool _pumping;
    private static readonly List<(ModContext? Mod, PathQueryChangedEvent Event)> ClosedEvents = [];
    // an answer is good for the step it was asked in, of the server session and capability registry it was asked under
    private static readonly Dictionary<ReachKeyT, ((ulong Step, long Session, long Registry) At, Task<QueryReachabilityResult> Task)> Reach = new();

    private static (ulong, long, long) Now() => (ServiceTick.Step, Lifecycle.ServerSession, Capabilities.CurrentBinding.Epoch);
    private static IDisposable? _tick, _stopping;
    private static ulong _serviceOwner;
    private static (long Session, long Registry) _serviceAt;

    /// <summary>A private lease for the calling mod instance and server session; ambient service calls have their own.</summary>
    internal static ulong OwnerOfCaller()
    {
        lock (Gate)
        {
            var (_, session, registry) = Now();
            var mod = ModContext.CurrentMod;
            if (mod != null)
            {
                if (ModLeases.TryGetValue(mod, out var lease))
                {
                    if (lease.Session == session && lease.Registry == registry) return lease.Owner;
                    RetireOwner(lease.Owner);
                    End(p => p.Owner == lease.Owner, QueryPathStatus.WorldClosed, "WorldClosed", notify: true);
                }
                var owner = Backend.Current.OwnerCreate("queries:" + mod.Info.Id);
                ModLeases[mod] = (owner, session, registry);
                if (Watched.Add(mod)) mod.Retiring += () => Retire(mod);
                return owner;
            }
            if (_serviceOwner != 0 && _serviceAt != (session, registry)) RetireOwner(_serviceOwner);
            if (_serviceOwner == 0 || _serviceAt != (session, registry))
            {
                _serviceOwner = Backend.Current.OwnerCreate("queries:service");
                _serviceAt = (session, registry);
            }
            return _serviceOwner;
        }
    }

    internal static Task<QueryPathResult> Track(ulong owner, ulong request, EntityId anchor)
    {
        var mod = ModContext.CurrentMod;
        var pending = new Pending { Owner = owner, Mod = mod, Anchor = anchor, AdmittedStep = ServiceTick.Step };
        lock (Gate)
        {
            ByRequest[request] = pending;
            Leases.Add(owner);
            _tick ??= ServiceTick.Add("kiln queries paths", (in TickInfo _) => Pump());
            // This listener outlives whichever mod first used paths.
            var previous = ModContext.CurrentMod;
            ModContext.CurrentMod = null;
            try { _stopping ??= Events.On<ServerStoppingEvent>(_ => WorldClosed()); }
            finally { ModContext.CurrentMod = previous; }
            if (!Generations.TryGetValue(owner, out pending.Generation))
            {
                // A fresh private lease's first FIFO record is its admission (no points). Copy it now to retain the
                // native server session even if the world closes before the first pump; dispatch still waits a step.
                var update = new QueriesNative.PathUpdate { Size = (uint)sizeof(QueriesNative.PathUpdate) };
                uint needed = 0;
                var t = QueriesNative.T;
                var rc = t == null ? QueriesNative.Unavailable : t->PathPoll(owner, &update, null, 0, &needed);
                if (rc == QueriesNative.Ok && update.PointCount == 0)
                {
                    Generations[owner] = pending.Generation = update.Generation;
                    Deferred.Add((owner, update, []));
                }
                else if (rc == QueriesNative.Revoked) End(p => p.Owner == owner, QueryPathStatus.OwnerRetired, "OwnerRetired");
                else Lower($"kiln.queries did not supply the lease's admission record ({rc}, {needed} points)");
            }
        }
        if (mod is { Alive: false }) Retire(mod);   // it retired between the call and the subscription
        return pending.Source.Task;
    }

    /// <summary>A token that cancels the request: the cancel is made on the tick thread, whichever thread the token fires on.</summary>
    internal static void CancelWith(ulong request, CancellationToken token)
    {
        if (!token.CanBeCanceled) return;
        Pending? pending;
        lock (Gate) ByRequest.TryGetValue(request, out pending);
        if (pending == null) return;
        if (token.IsCancellationRequested)
        {
            // Registration happens in the submitting tick-thread call. A token already cancelled must win before
            // the engine gets its next step, rather than waiting behind a completed result at the next pump.
            var t = QueriesNative.T;
            if (t != null) t->PathCancel(pending.Owner, request);
            return;
        }
        var registration = token.Register(() => CancelRequests.Enqueue(request));
        lock (Gate)
        {
            if (ByRequest.TryGetValue(request, out var current) && ReferenceEquals(current, pending)) pending.Registration = registration;
            else registration.Dispose();  // retirement may have ended it while Register ran
        }
    }

    internal static QueryCancelResult Cancel(QueriesNative.Table* t, ulong owner, ulong request) => t->PathCancel(owner, request) switch
    {
        QueriesNative.Ok => new QueryCancelResult(true, null, request),
        QueriesNative.Revoked => new QueryCancelResult(false, "OwnerRetired", request),
        QueriesNative.WrongThread => throw new InvalidOperationException("Queries.CancelPath must be called on the tick thread (use Scheduler.Post)"),
        _ => new QueryCancelResult(false, "UnknownRequest", request),
    };

    // ---- reachability's one-step cache ----
    internal readonly record struct ReachKeyT(ulong Owner, uint Anchor, long Fx, long Fy, long Fz, long Tx, long Ty, long Tz, double H, double V, uint Iterations, bool Fallback);

    internal static ReachKeyT ReachKey(QueryPathRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        static long Q(double v) => double.IsFinite(v) ? (long)Math.Round(Math.Clamp(v, -1e9, 1e9) * 2) : long.MinValue;   // half metres
        return new ReachKeyT(OwnerOfCaller(), r.Anchor.Value, Q(r.From.X), Q(r.From.Y), Q(r.From.Z), Q(r.To.X), Q(r.To.Y), Q(r.To.Z), r.HorizontalSpace, r.VerticalSpace, r.MaxIterations, r.ClosestFallback);
    }

    internal static Task<QueryReachabilityResult>? Cached(ReachKeyT key)
    {
        var now = Now();
        lock (Gate) return Reach.TryGetValue(key, out var have) && have.At == now ? have.Task : null;
    }

    internal static void Cache(ReachKeyT key, Task<QueryReachabilityResult> task)
    {
        lock (Gate)
        {
            var now = Now();
            foreach (var old in Reach.Where(kv => kv.Value.At != now).Select(kv => kv.Key).ToList()) Reach.Remove(old);
            Reach[key] = (now, task);
        }
    }

    // ---- the pump (tick thread) ----
    internal static void Pump()
    {
        if (_pumping) return;
        _pumping = true;
        try { PumpBatch(); }
        finally { _pumping = false; }
    }

    private static void PumpBatch()
    {
        while (Revokes.TryDequeue(out var revoke)) revoke.Backend.OwnerRevoke(revoke.Owner);
        (ModContext? Mod, PathQueryChangedEvent Event)[] closed;
        lock (Gate) { closed = [.. ClosedEvents]; ClosedEvents.Clear(); }
        foreach (var notice in closed) Post(notice.Mod, notice.Event);
        var t = QueriesNative.T;
        if (t == null) { End(_ => true, QueryPathStatus.Refused, "ServiceUnavailable"); return; }
        while (CancelRequests.TryDequeue(out var request))
        {
            Pending? p;
            lock (Gate) ByRequest.TryGetValue(request, out p);
            if (p != null) t->PathCancel(p.Owner, request);
        }
        ulong[] leases;
        List<(ulong Owner, QueriesNative.PathUpdate Update, QueriesNative.PathPoint[] Points)> batch;
        lock (Gate)
        {
            leases = [.. Leases];
            batch = [.. Deferred];
            Deferred.Clear();
        }
        var points = stackalloc QueriesNative.PathPoint[0];
        QueriesNative.PathPoint[]? buffer = null;
        foreach (var owner in leases)
        {
            for (var guard = 0; guard < 256; guard++)
            {
                var update = new QueriesNative.PathUpdate { Size = (uint)sizeof(QueriesNative.PathUpdate) };
                uint needed = 0;
                var rc = t->PathPoll(owner, &update, points, 0, &needed);
                if (rc == QueriesNative.BufferTooSmall)
                {
                    if (needed > QueriesNative.MaxPathPoints) { Lower($"kiln.queries offered {needed} points for one update"); return; }
                    buffer ??= new QueriesNative.PathPoint[QueriesNative.MaxPathPoints];
                    fixed (QueriesNative.PathPoint* p = buffer) rc = t->PathPoll(owner, &update, p, (uint)buffer.Length, &needed);
                }
                if (rc == QueriesNative.Revoked)
                {
                    RetireOwner(owner);
                    End(p => p.Owner == owner, QueryPathStatus.OwnerRetired, "OwnerRetired");
                    break;
                }
                if (rc == QueriesNative.NotFound) break;
                if (rc != QueriesNative.Ok) { Lower($"kiln.queries path_poll failed ({rc})"); return; }
                if (update.PointCount > QueriesNative.MaxPathPoints || update.PointCount > (buffer?.Length ?? 0))
                {
                    Lower($"kiln.queries sent {update.PointCount} points without a complete point buffer");
                    return;
                }
                batch.Add((owner, update, update.PointCount == 0 ? [] : buffer!.AsSpan(0, (int)update.PointCount).ToArray()));
            }
        }
        // Finish copying the whole batch before calling mod code. Reentrant submissions/cancellations wait for the
        // next pump, and a pump called in the admission step retains those copies until a later published step.
        foreach (var item in batch)
        {
            lock (Gate)
            {
                if (ByRequest.TryGetValue(item.Update.Request, out var pending) && pending.AdmittedStep == ServiceTick.Step)
                {
                    Deferred.Add(item);
                    continue;
                }
            }
            if (!Deliver(item.Owner, item.Update, item.Points)) return;
        }
        foreach (var owner in leases)
        {
            lock (Gate)
                if (!ByRequest.Values.Any(p => p.Owner == owner)) Leases.Remove(owner);
        }
    }

    // False: the update was not one this build understands; the path rows are lowered and the pump stops.
    private static bool Deliver(ulong owner, in QueriesNative.PathUpdate u, QueriesNative.PathPoint[]? buffer)
    {
        if (!Enum.IsDefined((QueryPathState)u.Before) || !Enum.IsDefined((QueryPathState)u.After) || !Enum.IsDefined((QueryPathStatus)u.Status) ||
            u.PointCount > QueriesNative.MaxPathPoints || u.PointCount > (buffer?.Length ?? 0) ||
            (u.GameResult != QueriesNative.NoGameResult && u.GameResult > byte.MaxValue))
        {
            Lower($"kiln.queries sent an update Kiln.Queries does not know (stage {u.Before}->{u.After}, status {u.Status}, {u.PointCount} points)");
            return false;
        }
        Pending? pending;
        lock (Gate) ByRequest.TryGetValue(u.Request, out pending);
        if (pending == null || pending.Owner != owner) return true;   // ended here already (the world closed, the mod went)
        pending.Generation = u.Generation;
        pending.State = (QueryPathState)u.After;
        var terminal = u.Terminal != 0;
        var status = (QueryPathStatus)u.Status;
        var reason = u.Ok != 0 ? null : terminal ? ReasonOf(u.Reason, status) : null;
        Post(pending.Mod, new PathQueryChangedEvent(u.Request, u.Generation, pending.Anchor, (QueryPathState)u.Before, (QueryPathState)u.After, terminal ? status : null, reason));
        if (!terminal) return true;
        // a handler may have unloaded the mod or ended the request: only what still waits is completed
        lock (Gate)
            if (!ByRequest.Remove(u.Request)) return true;
        pending.Registration.Dispose();
        var list = new QueryPathPoint[u.PointCount];
        for (var i = 0; i < list.Length; i++) list[i] = new QueryPathPoint(buffer![i].Position.Position, (byte)Math.Min(buffer[i].RawMove, 255));
        pending.Source.TrySetResult(new QueryPathResult(u.Ok != 0, reason, u.Request, u.Generation, status, u.GameResult == QueriesNative.NoGameResult ? null : (byte)u.GameResult,
            u.ReachesTarget != 0, list, u.Length, u.Steps));
        return true;
    }

    private static string ReasonOf(NativeStr s, QueryPathStatus status) => s.ToString() is { Length: > 0 } text ? text : status.ToString();

    private static void Post(ModContext? mod, PathQueryChangedEvent notice)
    {
        if (mod is { Alive: false }) return;
        var previous = ModContext.CurrentMod;
        ModContext.CurrentMod = mod;
        try { Events.Post(notice); }
        catch (Exception e) when (e is not OutOfMemoryException) { Backend.Current.Log(0, LogLevel.Error, $"kiln.queries: a handler of PathQueryChangedEvent threw: {e.Message}"); }
        finally { ModContext.CurrentMod = previous; }
    }

    private static void Lower(string why)
    {
        ServiceCaps.LowerOff(QueriesCaps.Path, CapabilityReason.Faulted, why);
        ServiceCaps.LowerOff(QueriesCaps.Reachability, CapabilityReason.Faulted, why);
        Backend.Current.Log(0, LogLevel.Error, $"kiln.queries: {why}: queries.path is off");
        RetireLeases();
        End(_ => true, QueryPathStatus.Refused, "MalformedNativeUpdate");
    }

    private static void End(Func<Pending, bool> which, QueryPathStatus status, string reason, bool notify = false)
    {
        List<(ulong Request, Pending Pending)> ended;
        lock (Gate)
        {
            ended = [.. ByRequest.Where(kv => which(kv.Value)).Select(kv => (kv.Key, kv.Value))];
            foreach (var (request, _) in ended) ByRequest.Remove(request);
            if (notify)
                foreach (var (request, pending) in ended)
                    ClosedEvents.Add((pending.Mod, new PathQueryChangedEvent(request, pending.Generation, pending.Anchor, pending.State, QueryPathState.Cancelled, status, reason)));
        }
        foreach (var (request, pending) in ended)
        {
            pending.Registration.Dispose();
            pending.Source.TrySetResult(new QueryPathResult(false, reason, request, pending.Generation, status, null, false, [], 0, 0));
        }
    }

    /// <summary>The mod is unloading: its lease is retired with the service, and its tasks end with no event.</summary>
    private static void Retire(ModContext mod)
    {
        lock (Gate)
        {
            Watched.Remove(mod);
            ClosedEvents.RemoveAll(n => ReferenceEquals(n.Mod, mod));
            if (ModLeases.Remove(mod, out var lease)) RetireOwner(lease.Owner);
        }
        End(p => ReferenceEquals(p.Mod, mod), QueryPathStatus.OwnerRetired, "OwnerRetired");
    }

    private static void RetireOwner(ulong owner)
    {
        if (owner == 0) return;
        var t = QueriesNative.T;
        if (t != null) t->PathRetire(owner);
        lock (Gate)
        {
            Leases.Remove(owner);
            Generations.Remove(owner);
            foreach (var key in Reach.Keys.Where(k => k.Owner == owner).ToArray()) Reach.Remove(key);
        }
        if (Backend.Current.IsTickThread) Backend.Current.OwnerRevoke(owner);
        else Revokes.Enqueue((Backend.Current, owner));
    }

    private static void RetireLeases()
    {
        lock (Gate)
        {
            foreach (var owner in ModLeases.Values.Select(l => l.Owner).Append(_serviceOwner).Concat(Leases).Distinct().ToArray()) RetireOwner(owner);
            ModLeases.Clear();
            _serviceOwner = 0;
            Reach.Clear();
            Deferred.Clear();
        }
    }

    /// <summary>The server world is stopping: what still waits ends now, whether or not another world follows.</summary>
    private static void WorldClosed()
    {
        RetireLeases();
        End(_ => true, QueryPathStatus.WorldClosed, "WorldClosed", notify: true);
    }

    /// <summary>For tests: forget every request and lease.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            ByRequest.Clear();
            Leases.Clear();
            Generations.Clear();
            Reach.Clear();
            Deferred.Clear();
            ClosedEvents.Clear();
        }
    }
}
