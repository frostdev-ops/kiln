// SPDX-License-Identifier: MIT
// Queries: read-only questions about the server world (docs/kiln/services/queries.md). Wrapper for kiln.queries.
namespace Kiln;

/// <summary>
/// Questions about the server world, answered at once on the tick thread. The rays are <b>voxel</b> rays: they see terrain
/// and building voxels, not props, chests, creatures or any collider; the overlaps look at entity <b>origins</b>, one point
/// each. Every answer keeps "nothing is known" (<c>Ok = false</c> with a reason) apart from "clear".
/// All rays of all mods share one budget per server step (64 rays and 500 microseconds by default): spread work over
/// steps, and treat <c>BudgetExceeded</c> as "ask again next step".
/// </summary>
public static unsafe partial class Queries
{
    /// <summary>The longest ray, in metres.</summary>
    public const double MaxRayLength = RayRules.MaxLength;

    /// <summary>The most entities an overlap returns.</summary>
    public const int MaxEntities = 4096;

    /// <summary>The first voxel surface on the segment from <paramref name="from"/> to <paramref name="to"/>.
    /// A loaded, clear segment is <c>Ok</c> with no hit.</summary>
    /// <exception cref="ArgumentException">A coordinate is not finite, or the segment has no length or is longer than
    /// <see cref="MaxRayLength"/> (also as the floats the game gets).</exception>
    /// <exception cref="KilnUnavailableException"><c>queries.voxel_ray</c> is off.</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability("queries.voxel_ray")]
    public static VoxelRayResult RaycastVoxels(WorldPosition from, WorldPosition to, byte realm = 0) =>
        Ray(from, to, realm, QueriesCaps.VoxelRay, "Queries.RaycastVoxels");

    /// <summary>The first voxel surface below <paramref name="from"/>, at most <paramref name="maxDown"/> metres down:
    /// a roof or a floor above the terrain is that surface. Nothing within reach is <c>Ok</c> with no hit.</summary>
    /// <exception cref="ArgumentException"><paramref name="maxDown"/> is not above 0 and at most <see cref="MaxRayLength"/>,
    /// or a coordinate is not finite.</exception>
    /// <exception cref="KilnUnavailableException"><c>queries.ground</c> is off.</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability("queries.ground")]
    public static VoxelRayResult GroundBelow(WorldPosition from, double maxDown = 100, byte realm = 0)
    {
        if (!(maxDown > 0) || !(maxDown <= MaxRayLength)) throw new ArgumentException($"Queries.GroundBelow: maxDown {maxDown} is not above 0 and at most {MaxRayLength}", nameof(maxDown));
        return Ray(from, new WorldPosition(from.X, from.Y - maxDown, from.Z), realm, QueriesCaps.Ground, "Queries.GroundBelow");
    }

    /// <summary>Whether voxels stand between two entities: a voxel ray from one's position to the other's, each with
    /// its offset (<see cref="VoxelSightOptions"/>). An entity that is gone is <c>Ok = false</c>, <c>EntityGone</c>.</summary>
    /// <exception cref="ArgumentException">An offset is not finite, or the two points are the same or more than
    /// <see cref="MaxRayLength"/> apart.</exception>
    /// <exception cref="KilnUnavailableException"><c>queries.line_of_sight</c> is off.</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability("queries.line_of_sight")]
    public static VoxelSightResult VoxelLineOfSight(EntityId from, EntityId to, VoxelSightOptions? options = null)
    {
        const string api = "Queries.VoxelLineOfSight";
        Services.RequireTickThread(api);
        options ??= new VoxelSightOptions();
        if (!RayRules.Finite(options.FromOffset) || !RayRules.Finite(options.ToOffset)) throw new ArgumentException($"{api}: an offset is not a finite number", nameof(options));
        QueriesCaps.Require(QueriesCaps.LineOfSight);
        if (!World.TryGetPosition(from, out var a) || !World.TryGetPosition(to, out var b)) return new VoxelSightResult(false, "EntityGone", false, null);
        var ray = Ray(Add(a, options.FromOffset), Add(b, options.ToOffset), options.Realm, QueriesCaps.LineOfSight, api);
        return ray.Ok ? new VoxelSightResult(true, null, ray.Hit == null, ray.Hit) : new VoxelSightResult(false, ray.Reason, false, null);
    }

    /// <summary>The entities whose origin is at most <paramref name="radius"/> metres from <paramref name="center"/>,
    /// nearest first. Origins only: a large thing whose origin is outside is not found.</summary>
    /// <exception cref="ArgumentException">A number is not finite, <paramref name="radius"/> is below 0, or
    /// <paramref name="max"/> is not 1 to <see cref="MaxEntities"/>.</exception>
    /// <exception cref="KilnUnavailableException"><c>queries.overlap_origins</c> is off.</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability("queries.overlap_origins")]
    public static EntityQueryResult EntitiesInSphere(WorldPosition center, double radius, EntityId exclude = default, int max = MaxEntities)
    {
        if (!double.IsFinite(radius) || radius < 0) throw new ArgumentException($"Queries.EntitiesInSphere: radius {radius} is not a finite number of 0 or more", nameof(radius));
        return Overlap(new QueriesNative.EntityRequest { Shape = QueriesNative.Sphere, Center = new(center), Radius = radius, Exclude = exclude.Value }, center, max, "Queries.EntitiesInSphere");
    }

    /// <summary>The entities whose origin is inside the box around <paramref name="center"/> that reaches
    /// <paramref name="halfExtents"/> along each world axis, nearest to the centre first.</summary>
    /// <exception cref="ArgumentException">A number is not finite, a half extent is below 0, or <paramref name="max"/>
    /// is not 1 to <see cref="MaxEntities"/>.</exception>
    /// <exception cref="KilnUnavailableException"><c>queries.overlap_origins</c> is off.</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability("queries.overlap_origins")]
    public static EntityQueryResult EntitiesInBox(WorldPosition center, WorldPosition halfExtents, EntityId exclude = default, int max = MaxEntities)
    {
        if (!RayRules.Finite(halfExtents) || halfExtents.X < 0 || halfExtents.Y < 0 || halfExtents.Z < 0)
            throw new ArgumentException($"Queries.EntitiesInBox: half extents {halfExtents} are not finite numbers of 0 or more", nameof(halfExtents));
        return Overlap(new QueriesNative.EntityRequest { Shape = QueriesNative.Box, Center = new(center), HalfExtents = new(halfExtents), Exclude = exclude.Value }, center, max, "Queries.EntitiesInBox");
    }

    private static WorldPosition Add(WorldPosition p, WorldPosition o) => new(p.X + o.X, p.Y + o.Y, p.Z + o.Z);

    // One ray: the order is the native service's (thread, numbers, capability, the call).
    private static VoxelRayResult Ray(WorldPosition from, WorldPosition to, byte realm, string capability, string api)
    {
        Services.RequireTickThread(api);
        RayRules.Require(from, to, api);
        QueriesCaps.Require(capability);
        var request = new QueriesNative.RayRequest { Size = (uint)sizeof(QueriesNative.RayRequest), Realm = realm, From = new(from), To = new(to) };
        var result = new QueriesNative.RayResult { Size = (uint)sizeof(QueriesNative.RayResult) };
        switch (QueriesNative.T->RayVoxels(&request, &result))
        {
            case QueriesNative.Ok: break;
            case QueriesNative.InvalidArgument: throw new ArgumentException($"{api}: kiln.queries refused {from} to {to} in realm {realm} (not a ray, or no such realm)");
            case QueriesNative.WrongThread: throw new InvalidOperationException($"{api} must be called on the tick thread (use Scheduler.Post)");
            case QueriesNative.Unavailable: throw QueriesCaps.Off(capability);
            case var rc: throw new InvalidOperationException($"{api}: kiln.queries failed ({rc})");
        }
        if (result.Ok == 0) return new VoxelRayResult(false, Reason(result.Reason), null);
        return result.Hit == 0
            ? new VoxelRayResult(true, null, null)
            : new VoxelRayResult(true, null, new VoxelRayHit(result.Position.Position, result.Distance, result.Material, result.Voxel));
    }

    private static EntityQueryResult Overlap(QueriesNative.EntityRequest request, WorldPosition center, int max, string api)
    {
        Services.RequireTickThread(api);
        if (!RayRules.Finite(center)) throw new ArgumentException($"{api}: centre {center} is not finite", nameof(center));
        if (max is < 1 or > MaxEntities) throw new ArgumentException($"{api}: max {max} is not 1 to {MaxEntities}", nameof(max));
        QueriesCaps.Require(QueriesCaps.OverlapOrigins);
        request.Size = (uint)sizeof(QueriesNative.EntityRequest);
        var result = new QueriesNative.EntityResult { Size = (uint)sizeof(QueriesNative.EntityResult) };
        var ids = new uint[max];
        int rc;
        fixed (uint* p = ids) rc = QueriesNative.T->Entities(&request, p, (uint)max, &result);
        switch (rc)
        {
            case QueriesNative.Ok: break;
            case QueriesNative.InvalidArgument: throw new ArgumentException($"{api}: kiln.queries refused the shape");
            case QueriesNative.WrongThread: throw new InvalidOperationException($"{api} must be called on the tick thread (use Scheduler.Post)");
            case QueriesNative.Unavailable: throw QueriesCaps.Off(QueriesCaps.OverlapOrigins);
            default: throw new InvalidOperationException($"{api}: kiln.queries failed ({rc})");
        }
        if (result.Ok == 0) return new EntityQueryResult(false, Reason(result.Reason), [], 0, false);
        var written = (int)Math.Min(result.Written, (uint)max);
        var list = new EntityId[written];
        for (var i = 0; i < written; i++) list[i] = new EntityId(ids[i]);
        return new EntityQueryResult(true, null, list, (int)result.Total, result.Total > result.Written);
    }

    // a refusal always has a reason: the service's own word, or that it gave none
    private static string Reason(NativeStr s) => s.ToString() is { Length: > 0 } text ? text : "Refused";
}
