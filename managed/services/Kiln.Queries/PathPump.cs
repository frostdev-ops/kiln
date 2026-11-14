// SPDX-License-Identifier: MIT
// The managed side of kiln.queries' path queue: one lease per mod instance (its core owner), one consumer of each lease.
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
        public readonly TaskCompletionSource<QueryPathResult> Source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<ulong, Pending> ByRequest = new();
    private static readonly HashSet<ulong> Leases = new();
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);
    private static readonly ConcurrentQueue<ulong> CancelRequests = new();
    // an answer is good for the step it was asked in, of the server session and capability registry it was asked under
    private static readonly Dictionary<ReachKeyT, ((ulong Step, long Session, long Registry) At, Task<QueryReachabilityResult> Task)> Reach = new();

    private static (ulong, long, long) Now() => (ServiceTick.Step, Lifecycle.ServerSession, Capabilities.CurrentBinding.Epoch);
    private static IDisposable? _tick, _stopping;
    private static ulong _serviceOwner;

    /// <summary>The lease a call is made under: the calling mod's core owner, or one of Kiln.Queries' own outside mod code.</summary>
    internal static ulong OwnerOfCaller()
    {
        if (ModContext.CurrentMod is { } mod && mod.Owner != 0) return mod.Owner;
        lock (Gate) return _serviceOwner != 0 ? _serviceOwner : _serviceOwner = Backend.Current.OwnerCreate("queries:service");
    }

    internal static Task<QueryPathResult> Track(ulong owner, ulong request, EntityId anchor)
    {
        var mod = ModContext.CurrentMod;
        var pending = new Pending { Owner = owner, Mod = mod, Anchor = anchor };
        lock (Gate)
        {
            ByRequest[request] = pending;
            Leases.Add(owner);
            if (mod != null && Watched.Add(mod)) mod.Retiring += () => Retire(mod);
            _tick ??= ServiceTick.Add("kiln queries paths", (in TickInfo _) => Pump());
            _stopping ??= Events.On<ServerStoppingEvent>(_ => WorldClosed());
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
        pending.Registration = token.Register(() => CancelRequests.Enqueue(request));
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
        var t = QueriesNative.T;
        if (t == null) return;
        while (CancelRequests.TryDequeue(out var request))
        {
            Pending? p;
            lock (Gate) ByRequest.TryGetValue(request, out p);
            if (p != null) t->PathCancel(p.Owner, request);
        }
        ulong[] leases;
        lock (Gate) leases = [.. Leases];
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
                    lock (Gate) Leases.Remove(owner);
                    break;
                }
                if (rc != QueriesNative.Ok) break;   // NotFound: the queue is empty
                if (!Deliver(owner, in update, buffer)) return;
            }
            lock (Gate)
                if (!ByRequest.Values.Any(p => p.Owner == owner)) Leases.Remove(owner);
        }
    }

    // False: the update was not one this build understands; the path rows are lowered and the pump stops.
    private static bool Deliver(ulong owner, in QueriesNative.PathUpdate u, QueriesNative.PathPoint[]? buffer)
    {
        if (!Enum.IsDefined((QueryPathState)u.Before) || !Enum.IsDefined((QueryPathState)u.After) || !Enum.IsDefined((QueryPathStatus)u.Status) ||
            u.PointCount > QueriesNative.MaxPathPoints || (u.PointCount > 0 && buffer == null))
        {
            Lower($"kiln.queries sent an update Kiln.Queries does not know (stage {u.Before}->{u.After}, status {u.Status}, {u.PointCount} points)");
            return false;
        }
        Pending? pending;
        lock (Gate) ByRequest.TryGetValue(u.Request, out pending);
        if (pending == null || pending.Owner != owner) return true;   // ended here already (the world closed, the mod went)
        var terminal = u.Terminal != 0;
        var status = (QueryPathStatus)u.Status;
        var reason = u.Ok != 0 ? null : terminal ? ReasonOf(u.Reason, status) : null;
        if (pending.Mod is not { Alive: false })
        {
            var previous = ModContext.CurrentMod;
            ModContext.CurrentMod = pending.Mod;
            try { Events.Post(new PathQueryChangedEvent(u.Request, u.Generation, pending.Anchor, (QueryPathState)u.Before, (QueryPathState)u.After, terminal ? status : null, reason)); }
            catch (Exception e) when (e is not OutOfMemoryException) { Backend.Current.Log(0, LogLevel.Error, $"kiln.queries: a handler of PathQueryChangedEvent threw: {e.Message}"); }
            finally { ModContext.CurrentMod = previous; }
        }
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

    private static void Lower(string why)
    {
        ServiceCaps.LowerOff(QueriesCaps.Path, CapabilityReason.Faulted, why);
        ServiceCaps.LowerOff(QueriesCaps.Reachability, CapabilityReason.Faulted, why);
        Backend.Current.Log(0, LogLevel.Error, $"kiln.queries: {why}: queries.path is off");
    }

    private static void End(Func<Pending, bool> which, QueryPathStatus status, string reason)
    {
        List<(ulong Request, Pending Pending)> ended;
        lock (Gate)
        {
            ended = [.. ByRequest.Where(kv => which(kv.Value)).Select(kv => (kv.Key, kv.Value))];
            foreach (var (request, _) in ended) ByRequest.Remove(request);
        }
        foreach (var (request, pending) in ended)
        {
            pending.Registration.Dispose();
            pending.Source.TrySetResult(new QueryPathResult(false, reason, request, 0, status, null, false, [], 0, 0));
        }
    }

    /// <summary>The mod is unloading: its lease is retired with the service, and its tasks end with no event.</summary>
    private static void Retire(ModContext mod)
    {
        lock (Gate) Watched.Remove(mod);
        var t = QueriesNative.T;
        if (t != null && mod.Owner != 0) t->PathRetire(mod.Owner);
        lock (Gate) Leases.Remove(mod.Owner);
        End(p => ReferenceEquals(p.Mod, mod), QueryPathStatus.OwnerRetired, "OwnerRetired");
    }

    /// <summary>The server world is stopping: what still waits ends now, whether or not another world follows.</summary>
    private static void WorldClosed() => End(_ => true, QueryPathStatus.WorldClosed, "WorldClosed");

    /// <summary>For tests: forget every request and lease.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            ByRequest.Clear();
            Leases.Clear();
            Reach.Clear();
        }
    }
}
