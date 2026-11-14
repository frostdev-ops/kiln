// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private HarnessQueries? _queries;

    /// <summary>The fake kiln.queries of this harness: what a voxel ray answers, and the rays mods asked for.</summary>
    public HarnessQueries Queries => _queries ??= new HarnessQueries(this);
}

/// <summary>A voxel ray a mod asked for (<c>Queries.RaycastVoxels</c>, <c>GroundBelow</c>, <c>VoxelLineOfSight</c>, a brain's sight).</summary>
public readonly record struct FakeVoxelRay(WorldPosition From, WorldPosition To, byte Realm);

/// <summary>What the fake answers a voxel ray: <see cref="Clear"/>, <see cref="Hit"/>, or <see cref="Refused"/> with one of
/// the service's reasons (<c>NotLoaded</c>, <c>OutOfGrid</c>).</summary>
public readonly record struct FakeVoxelAnswer
{
    internal WorldPosition? HitAt { get; private init; }
    internal uint Material { get; private init; }
    internal ushort Voxel { get; private init; }
    internal string? Refusal { get; private init; }

    /// <summary>Loaded, and nothing on the segment.</summary>
    public static FakeVoxelAnswer Clear => default;

    /// <summary>The first voxel surface is at <paramref name="at"/>.</summary>
    public static FakeVoxelAnswer Hit(WorldPosition at, uint material = 1, ushort voxel = 0xff01) => new() { HitAt = at, Material = material, Voxel = voxel };

    /// <summary>Nothing is known, for <paramref name="reason"/>: <c>NotLoaded</c>, <c>OutOfGrid</c>.</summary>
    /// <exception cref="ArgumentException">An empty reason.</exception>
    public static FakeVoxelAnswer Refused(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return new() { Refusal = reason };
    }
}

/// <summary>A path request a mod made (<c>Queries.RequestPath</c>, <c>FindPath</c>, <c>CanReach</c>).</summary>
public sealed record FakePathRequest(EntityId Anchor, WorldPosition From, WorldPosition To, double HorizontalSpace, double VerticalSpace, uint MaxIterations, bool ClosestFallback);

/// <summary>What the fake's path search answers: <see cref="Route(WorldPosition[])"/> (found when its last point is at the
/// request's target, partial otherwise), or one of the game's results that are no route.</summary>
public sealed class FakePathAnswer
{
    internal QueryPathStatusFake Status { get; private init; }
    internal bool Ok { get; private init; }
    internal bool Reaches { get; private init; }
    internal uint? GameResult { get; private init; }
    internal double Length { get; private init; }
    internal string Reason { get; private init; } = "";
    internal IReadOnlyList<(WorldPosition Position, byte Move)> Points { get; private init; } = [];
    private bool _route;

    /// <summary>A walking route through these points.</summary>
    /// <exception cref="ArgumentException">No point.</exception>
    public static FakePathAnswer Route(params WorldPosition[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Length == 0) throw new ArgumentException("a route has at least one point", nameof(points));
        var length = 0.0;
        for (var i = 1; i < points.Length; i++) length += points[i - 1].DistanceTo(points[i]);
        return new FakePathAnswer { _route = true, Ok = true, GameResult = 1, Length = length, Points = [.. points.Select(p => (p, (byte)0))] };
    }

    /// <summary>A route with a step that is not walking (<paramref name="move"/>: the game's move kind, 2 = jump up): the
    /// service reports it as unsupported and keeps the points for diagnosis.</summary>
    public static FakePathAnswer RouteWithMove(byte move, params WorldPosition[] points)
    {
        var walk = Route(points);
        return new FakePathAnswer { Status = QueryPathStatusFake.UnsupportedTraversal, GameResult = 1, Reason = "UnsupportedTraversal", Points = [.. walk.Points.Select((p, i) => (p.Position, i == 0 ? (byte)0 : move))] };
    }

    /// <summary>The game found no path.</summary>
    public static FakePathAnswer NoPath => new() { Status = QueryPathStatusFake.NoPath, GameResult = 2, Reason = "NoPathFound" };
    /// <summary>The start has no node.</summary>
    public static FakePathAnswer NoStartNode => new() { Status = QueryPathStatusFake.NoStartNode, GameResult = 5, Reason = "NoStartNodeFound" };
    /// <summary>The target has no node.</summary>
    public static FakePathAnswer NoTargetNode => new() { Status = QueryPathStatusFake.NoTargetNode, GameResult = 6, Reason = "NoTargetNodeFound" };
    /// <summary>The search hit its iteration limit.</summary>
    public static FakePathAnswer IterationLimit => new() { Status = QueryPathStatusFake.IterationLimit, GameResult = 7, Reason = "MaxIterationsReached" };
    /// <summary>The game gave no answer (the area is not loaded).</summary>
    public static FakePathAnswer NotLoaded => new() { Status = QueryPathStatusFake.NotLoadedOrUnknown, GameResult = 0, Reason = "NoEngineAnswer" };
    /// <summary>The request ran out of time.</summary>
    public static FakePathAnswer TimedOut => new() { Status = QueryPathStatusFake.TimedOut, Reason = "TimedOut" };

    internal static FakePathAnswer Ended(QueryPathStatusFake status, string reason) => new() { Status = status, Reason = reason };

    // a route is found when it ends at the target (1.5 m across, 3 m up or down), partial otherwise
    internal FakePathAnswer For(FakePathRequest request)
    {
        if (!_route) return this;
        var last = Points[^1].Position;
        var reaches = Math.Sqrt((last.X - request.To.X) * (last.X - request.To.X) + (last.Z - request.To.Z) * (last.Z - request.To.Z)) <= 1.5 && Math.Abs(last.Y - request.To.Y) <= 3;
        return new FakePathAnswer { _route = true, Ok = true, Reaches = reaches, Status = reaches ? QueryPathStatusFake.Found : QueryPathStatusFake.Partial, GameResult = 1, Length = Length, Points = Points };
    }
}

/// <summary>
/// The fake kiln.queries of a harness (<see cref="KilnHarness.Queries"/>): Kiln.Queries' own code runs on it.
/// </summary>
/// <remarks>
/// <para><b>Modelled.</b> The rules around a ray: its numbers, the tick thread, the capability rows, the shared budget
/// per step (<see cref="MaxRaysPerStep"/>), refusals with their reasons. Entity origins come from the harness's fake world.</para>
/// <para><b>Not modelled.</b> Voxels: a ray answers what <see cref="Voxels"/> says, and is unavailable without a
/// script. The time part of the budget and realm grids. A path request answers what <see cref="Paths"/>
/// says, after <see cref="PathSteps"/> steps: there is no path grid offline.</para>
/// </remarks>
public sealed class HarnessQueries
{
    private readonly KilnHarness _harness;

    internal HarnessQueries(KilnHarness harness) => _harness = harness;

    private QueriesFakeState State => KitFakes.State<QueriesFakeState>(_harness);

    /// <summary>What a voxel ray answers. Null: voxel rays are unavailable. Runs on the tick thread, inside the mod's call.</summary>
    public Func<FakeVoxelRay, FakeVoxelAnswer>? Voxels
    {
        get { lock (State.Lock) return State.Voxels; }
        set
        {
            lock (State.Lock)
            {
                State.Voxels = value;
                if (ReferenceEquals(KitBackend.Instance.Harness, _harness)) QueriesFake.ReportRay(value != null);
            }
        }
    }

    /// <summary>How many rays the fake admits per step before it refuses with <c>BudgetExceeded</c> (the service's default: 64).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not 0 to 4096.</exception>
    public int MaxRaysPerStep
    {
        get { lock (State.Lock) return State.MaxRaysPerStep; }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 4096);
            lock (State.Lock) State.MaxRaysPerStep = value;
        }
    }

    /// <summary>What the path search answers a request. Null: a straight walking route from its start to its target.
    /// Runs on the tick thread at the step the answer is due.</summary>
    public Func<FakePathRequest, FakePathAnswer>? Paths
    {
        get { lock (State.Lock) return State.Paths; }
        set { lock (State.Lock) State.Paths = value; }
    }

    /// <summary>How many steps after its admission a path request answers (default 3; the game takes a few).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Less than 1.</exception>
    public int PathSteps
    {
        get { lock (State.Lock) return State.PathSteps; }
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            lock (State.Lock) State.PathSteps = value;
        }
    }

    /// <summary>Every path request that was admitted, in order.</summary>
    public IReadOnlyList<FakePathRequest> PathRequests { get { lock (State.Lock) return [.. State.PathRequests]; } }

    /// <summary>How many path requests hold a slot now (not ended, or ended and not yet delivered).</summary>
    public int OpenPaths { get { lock (State.Lock) return State.Open.Count; } }

    /// <summary>Makes <paramref name="entity"/> an anchor without a SpawnTime marker (as a saved entity or an item is in
    /// the game): a path request with it is refused, OwnerIdentityUnavailable.</summary>
    public void WithoutSpawnTime(EntityId entity) { lock (State.Lock) State.Unstamped.Add(entity.Value); }

    /// <summary>Every ray that was admitted, in order.</summary>
    public IReadOnlyList<FakeVoxelRay> Rays { get { lock (State.Lock) return [.. State.Rays]; } }
}
