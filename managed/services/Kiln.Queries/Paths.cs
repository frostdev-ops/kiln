// SPDX-License-Identifier: MIT
// Queries, paths: the game's own path search (docs/kiln/services/queries.md, "Paths"). A request is queued with
// kiln.queries under a lease of the calling mod; the pump (PathPump.cs) takes what the service has for the lease on the
// service tick, posts PathQueryChangedEvent and completes the request's task.
namespace Kiln;

public static unsafe partial class Queries
{
    /// <summary>The farthest apart <c>From</c> and <c>To</c> of a path request may be, in metres.</summary>
    public const double MaxPathDistance = 200;

    /// <summary>Asks for a walking route. The ticket's <see cref="QueryPathTicket.Completion"/> ends some server steps
    /// later (at most 10 s) with a copy of the route as it was then. Nothing is moved. A refusal (the queue is full, the
    /// anchor has no SpawnTime marker, an endpoint is outside the world's grid) is a ticket with <c>Ok = false</c>.</summary>
    /// <exception cref="ArgumentException">Numbers that are no request: not finite, more than <see cref="MaxPathDistance"/>
    /// apart, a space outside 0..16 m, iterations outside 1..65535, no anchor.</exception>
    /// <exception cref="KilnUnavailableException"><c>queries.path</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or inside a world program's update, a wrap
    /// stage or a game system hook (the world is mid-step there).</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability("queries.path")]
    public static QueryPathTicket RequestPath(QueryPathRequest request) => Submit(request, QueriesCaps.Path, "Queries.RequestPath");

    /// <summary><see cref="RequestPath"/> as a task. Cancelling <paramref name="cancellationToken"/> cancels the request:
    /// the task still completes normally, with <see cref="QueryPathStatus.Cancelled"/>.</summary>
    [ServerOnly, TickThreadOnly, RequiresCapability("queries.path")]
    public static Task<QueryPathResult> FindPath(QueryPathRequest request, CancellationToken cancellationToken = default)
    {
        var ticket = Submit(request, QueriesCaps.Path, "Queries.FindPath");
        if (ticket.Ok) PathPump.CancelWith(ticket.Request, cancellationToken);
        return ticket.Completion;
    }

    /// <summary>Cancels a request of the calling mod that has not ended. One that is unknown, another mod's or already
    /// ended answers <c>Ok = false</c>, <c>UnknownRequest</c>.</summary>
    [ServerOnly, TickThreadOnly, RequiresCapability("queries.path")]
    public static QueryCancelResult CancelPath(ulong request)
    {
        Services.RequireTickThread("Queries.CancelPath");
        if (request == 0) throw new ArgumentException("Queries.CancelPath: request 0", nameof(request));
        QueriesCaps.Rows.Ensure();
        var t = QueriesNative.T;
        if (t == null) throw ServiceCaps.Unavailable(QueriesCaps.Path);
        return PathPump.Cancel(t, PathPump.OwnerOfCaller(), request);
    }

    /// <summary>Whether <c>To</c> can be walked to from <c>From</c>: the same search as <see cref="FindPath"/>, its route
    /// thrown away. Asking the same thing again in the same step gives the same answer without another search.</summary>
    [ServerOnly, TickThreadOnly, RequiresCapability("queries.reachability")]
    public static Task<QueryReachabilityResult> CanReach(QueryPathRequest request, CancellationToken cancellationToken = default)
    {
        var key = PathPump.ReachKey(request);
        if (PathPump.Cached(key) is { } known) return known;
        var ticket = Submit(request, QueriesCaps.Reachability, "Queries.CanReach");
        if (ticket.Ok) PathPump.CancelWith(ticket.Request, cancellationToken);
        var task = PathMaps.Reachability(ticket.Completion);
        if (ticket.Ok) PathPump.Cache(key, task);
        return task;
    }

    /// <summary>What a path result says about reaching the target. A route that arrives: yes. One that ends elsewhere, or no
    /// route: no. Anything else: not known.</summary>
    internal static QueryReachabilityResult Reachability(QueryPathResult r) => r.Status switch
    {
        QueryPathStatus.Found => new QueryReachabilityResult(true, null, true, r.Status, r.GameResult),
        QueryPathStatus.Partial or QueryPathStatus.NoPath => new QueryReachabilityResult(true, null, false, r.Status, r.GameResult),
        _ => new QueryReachabilityResult(false, r.Reason ?? r.Status.ToString(), false, r.Status, r.GameResult),
    };

    internal static void RequirePath(QueryPathRequest request, string api)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Anchor.IsNone) throw new ArgumentException($"{api}: no anchor entity", nameof(request));
        if (!RayRules.Finite(request.From) || !RayRules.Finite(request.To)) throw new ArgumentException($"{api}: {request.From} to {request.To} has a coordinate that is not a finite number", nameof(request));
        if (!(request.From.DistanceTo(request.To) <= MaxPathDistance)) throw new ArgumentException($"{api}: {request.From} to {request.To} is more than {MaxPathDistance} m apart", nameof(request));
        foreach (var (name, v) in new[] { (nameof(request.HorizontalSpace), request.HorizontalSpace), (nameof(request.VerticalSpace), request.VerticalSpace) })
            if (!double.IsFinite(v) || v < 0 || v > 16) throw new ArgumentException($"{api}: {name} {v} is not 0 to 16 m", nameof(request));
        if (request.MaxIterations is < 1 or > 65535) throw new ArgumentException($"{api}: MaxIterations {request.MaxIterations} is not 1 to 65535", nameof(request));
    }

    private static QueryPathTicket Submit(QueryPathRequest request, string capability, string api)
    {
        Services.RequireTickThread(api);
        RequirePath(request, api);
        QueriesCaps.Require(capability);
        if (!Capabilities.Is(QueriesCaps.Path)) throw ServiceCaps.Unavailable(capability);
        var t = QueriesNative.T;
        var owner = PathPump.OwnerOfCaller();
        var native = new QueriesNative.PathRequest
        {
            Size = (uint)sizeof(QueriesNative.PathRequest), Anchor = request.Anchor.Value, From = new(request.From), To = new(request.To),
            HorizontalSpace = request.HorizontalSpace, VerticalSpace = request.VerticalSpace, MaxIterations = request.MaxIterations, ClosestFallback = request.ClosestFallback ? 1u : 0,
        };
        var admission = new QueriesNative.PathAdmission { Size = (uint)sizeof(QueriesNative.PathAdmission) };
        switch (t->PathSubmit(owner, &native, &admission))
        {
            case QueriesNative.Ok: break;
            case QueriesNative.InvalidArgument: throw new ArgumentException($"{api}: kiln.queries refused the request's numbers", nameof(request));
            case QueriesNative.WrongThread: throw WrongThread(api, admission.Reason);
            case QueriesNative.Revoked: return Refused(QueryPathStatus.OwnerRetired, "OwnerRetired");
            case QueriesNative.Unavailable:
                // the row reads on and the service says no: no server world runs here right now, or the path half just went off
                if (!Capabilities.Is(QueriesCaps.Path)) throw QueriesCaps.Off(capability);
                return Refused(QueryPathStatus.WorldClosed, "WorldNotReady");
            case var rc: throw new InvalidOperationException($"{api}: kiln.queries failed ({rc})");
        }
        if (admission.Ok == 0)
        {
            var status = Enum.IsDefined((QueryPathStatus)admission.Status) && admission.Status != 0 ? (QueryPathStatus)admission.Status : QueryPathStatus.Refused;
            return Refused(status, Reason(admission.Reason));
        }
        var completion = PathPump.Track(owner, admission.Request, request.Anchor);
        return new QueryPathTicket(true, null, admission.Request, completion);

        static QueryPathTicket Refused(QueryPathStatus status, string reason) =>
            new(false, reason, 0, Task.FromResult(new QueryPathResult(false, reason, 0, 0, status, null, false, [], 0, 0)));
    }
}
