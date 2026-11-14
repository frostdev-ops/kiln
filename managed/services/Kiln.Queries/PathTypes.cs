// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>How a path request ended (or <see cref="Pending"/>). The numbers are kiln.queries' own.</summary>
public enum QueryPathStatus
{
    Pending = 0,
    /// <summary>A walking route whose last point is at the target (within 1.5 m across and 3 m up or down).</summary>
    Found = 1,
    /// <summary>A walking route that ends somewhere else (the closest the search came, with or without <see cref="QueryPathRequest.ClosestFallback"/>).</summary>
    Partial = 2,
    NoPath = 3,
    NoStartNode = 4,
    NoTargetNode = 5,
    IterationLimit = 6,
    OutOfMemory = 7,
    /// <summary>The game gave no answer it stands behind: the area is not loaded, or a result code Kiln does not know.</summary>
    NotLoadedOrUnknown = 8,
    Cancelled = 9,
    /// <summary>The anchor went, or its id came to name another entity.</summary>
    OwnerGone = 10,
    TimedOut = 11,
    /// <summary>More than 4096 points: none are given.</summary>
    Truncated = 12,
    Refused = 13,
    /// <summary>The route needs a move that is not walking (a jump, a door, swimming): its points are given for diagnosis only.</summary>
    UnsupportedTraversal = 14,
    MalformedResult = 15,
    /// <summary>32 requests are open in all, or 8 of this mod's, counting results not yet delivered.</summary>
    QueueFull = 16,
    /// <summary>The anchor has no SpawnTime marker: Kiln could not tell it from a later entity with the same id.</summary>
    OwnerIdentityUnavailable = 17,
    OutOfGrid = 18,
    WorldClosed = 19,
    OwnerRetired = 20,
}

/// <summary>Where a path request is.</summary>
public enum QueryPathState { Created = 0, Queued = 1, Searching = 2, Completed = 3, Cancelled = 4, TimedOut = 5 }

/// <summary>How a point of a route is reached from the one before it (the game's PathMoveType).</summary>
public enum QueryPathMove : byte { Walk = 0, Fly = 1, JumpUp = 2, JumpDown = 3, JumpAcross = 4, OpenDoor = 5, Swim = 6, Dive = 7, WalkUnderWater = 8, FlyThroughWater = 9 }

/// <summary>One point of a route.</summary>
/// <param name="RawMove">The game's move byte as it came; <see cref="Move"/> when Kiln knows it.</param>
public sealed record QueryPathPoint(WorldPosition Position, byte RawMove)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public QueryPathMove? Move => RawMove <= 9 ? (QueryPathMove)RawMove : null;
}

/// <summary>A question to the game's path search: a walking route from <paramref name="From"/> to <paramref name="To"/>,
/// at most 200 m apart.</summary>
/// <param name="Anchor">An entity the game's query is made for. It is <b>not moved</b> and need not be what will walk the
/// route. It must have a SpawnTime marker, which entities spawned into the world by the game or by <c>Spawn</c> have;
/// saved entities, items and players may not. For those, spawn a helper (<c>Base_LootPickup_Generic_Gravity</c>) at
/// <paramref name="From"/>, use it as the anchor and destroy it afterwards.</param>
public sealed record QueryPathRequest(EntityId Anchor, WorldPosition From, WorldPosition To)
{
    /// <summary>Free space the route needs sideways, in metres, as the game's query takes it (0 to 16; rounded up to half metres).</summary>
    public double HorizontalSpace { get; init; } = 0.5;
    /// <summary>Free space the route needs upwards, in metres.</summary>
    public double VerticalSpace { get; init; } = 2;
    /// <summary>The search's iteration limit, 1 to 65535.</summary>
    public uint MaxIterations { get; init; } = 2000;
    /// <summary>When the target has no node, end at the closest one: the result is then <see cref="QueryPathStatus.Partial"/>.</summary>
    public bool ClosestFallback { get; init; }
}

/// <summary>A route as it was at the step the game answered: a copy, nothing live. <see cref="Ok"/> for
/// <see cref="QueryPathStatus.Found"/> and <see cref="QueryPathStatus.Partial"/>; read <see cref="ReachesTarget"/> to tell them apart.</summary>
/// <param name="GameResult">The game's own result code, when it gave one.</param>
/// <param name="Steps">Server steps the request was worked on.</param>
public sealed record QueryPathResult(bool Ok, string? Reason, ulong Request, uint Generation, QueryPathStatus Status, byte? GameResult, bool ReachesTarget,
    IReadOnlyList<QueryPathPoint> Points, double Length, uint Steps) : IKilnResult;

/// <summary>An admitted request (<see cref="Ok"/>: <see cref="Completion"/> ends some steps later) or a refusal
/// (<see cref="Completion"/> is complete already, with the same reason).</summary>
public sealed record QueryPathTicket(bool Ok, string? Reason, ulong Request, Task<QueryPathResult> Completion) : IKilnResult;

/// <summary>Whether the target can be walked to. <see cref="Ok"/>: the search gave an answer, and <see cref="Reachable"/> is it.
/// Not <see cref="Ok"/>: nothing is known (<see cref="Reason"/>); <see cref="Reachable"/> is false then, and that does not mean "no".</summary>
public sealed record QueryReachabilityResult(bool Ok, string? Reason, bool Reachable, QueryPathStatus Status, byte? GameResult) : IKilnResult;

public sealed record QueryCancelResult(bool Ok, string? Reason, ulong Request) : IKilnResult;

/// <summary>A path request of this mod moved on: queued, searching, or ended (<see cref="TerminalStatus"/>). Posted on the
/// tick thread; for an end, before the request's task completes.</summary>
public sealed class PathQueryChangedEvent(ulong request, uint generation, EntityId anchor, QueryPathState before, QueryPathState after, QueryPathStatus? terminalStatus, string? reason) : KilnEvent
{
    public ulong Request { get; } = request;
    /// <summary>The server world the request belongs to (worlds since the process started).</summary>
    public uint Generation { get; } = generation;
    public EntityId Anchor { get; } = anchor;
    public QueryPathState Before { get; } = before;
    public QueryPathState After { get; } = after;
    public QueryPathStatus? TerminalStatus { get; } = terminalStatus;
    public string? Reason { get; } = reason;
}

internal static class PathMaps
{
    internal static async Task<QueryReachabilityResult> Reachability(Task<QueryPathResult> path) => Queries.Reachability(await path.ConfigureAwait(false));
}
