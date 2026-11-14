// SPDX-License-Identifier: MIT
// Kiln.Queries in the capability registry (docs/kiln/services/caps.md). kiln.queries is the core's row for the table and
// queries.voxel_ray the sigdb feature of the game's ray; Kiln.Queries adds a row for each thing it composes from them.
// Reported at the service's start (QueriesStartup, the first service tick) and again every 25 steps: kiln.queries turns
// the ray off for the process when the game's ray faults, and the rows built on it follow.
namespace Kiln;

internal static unsafe class QueriesCaps
{
    /// <summary>kiln.queries' table is here (the core's row).</summary>
    public const string Service = "kiln.queries";
    /// <summary>sigdb: the game's voxel ray (<see cref="Queries.RaycastVoxels"/>).</summary>
    public const string VoxelRay = "queries.voxel_ray";
    /// <summary><see cref="Queries.GroundBelow"/>: a downward voxel ray.</summary>
    public const string Ground = "queries.ground";
    /// <summary><see cref="Queries.VoxelLineOfSight"/>: a voxel ray between two entities' positions.</summary>
    public const string LineOfSight = "queries.line_of_sight";
    /// <summary><see cref="Queries.EntitiesInSphere"/> and <see cref="Queries.EntitiesInBox"/>: entity origins from kiln.world.</summary>
    public const string OverlapOrigins = "queries.overlap_origins";

    /// <summary>sigdb: the game's path queries (<see cref="Queries.RequestPath"/>, <see cref="Queries.FindPath"/>).</summary>
    public const string Path = "queries.path";
    /// <summary><see cref="Queries.CanReach"/>: the same path search, asked only whether it arrives.</summary>
    public const string Reachability = "queries.reachability";

    private const string World = "kiln.world";
    private const string WorldEntities = "world.entities", WorldFind = "world.find";

    internal static readonly ServiceCapRows Rows = new(Service, Report, everySteps: 25, refreshOnlyWithService: true);

    private static void Report()
    {
        var here = QueriesNative.T != null && Capabilities.Is(Service);
        if (!here) ServiceCaps.LowerFeatures(Service, [VoxelRay, Path]);
        const string missing = "a required query dependency is unavailable";
        // Static's `needs` only explain an off row: whether each need is on is asked here
        ServiceCaps.Static(Ground, here && Capabilities.Is(VoxelRay), CapabilityReason.ServiceMissing, missing, Service, VoxelRay);
        ServiceCaps.Static(LineOfSight, here && Capabilities.Is(VoxelRay) && Capabilities.Is(World) && Capabilities.Is(WorldEntities), CapabilityReason.ServiceMissing, missing, Service, VoxelRay, World, WorldEntities);
        ServiceCaps.Static(Reachability, here && Capabilities.Is(Path), CapabilityReason.ServiceMissing, missing, Service, Path);
        ServiceCaps.Static(OverlapOrigins, here && Capabilities.Is(World) && Capabilities.Is(WorldEntities) && Capabilities.Is(WorldFind), CapabilityReason.ServiceMissing, missing, Service, World, WorldEntities, WorldFind);
    }

    // A composed row may still read on after one of its dependencies went off: check every current input too.
    private static bool OnNow(string capability) =>
        QueriesNative.T != null && Capabilities.Is(Service) && Capabilities.Is(capability) &&
        (capability is not (Ground or LineOfSight) || Capabilities.Is(VoxelRay)) &&
        (capability is not (LineOfSight or OverlapOrigins) || (Capabilities.Is(World) && Capabilities.Is(WorldEntities))) &&
        (capability != OverlapOrigins || Capabilities.Is(WorldFind)) &&
        (capability != Reachability || Capabilities.Is(Path));

    /// <summary>Throws <see cref="KilnUnavailableException"/> naming <paramref name="capability"/> unless it is available
    /// now. The composed rows are decided when they are reported, from the table and dependencies as they were then; a
    /// row that reads off is decided again here before it refuses, so a report made while kiln.queries' own rows were
    /// not there yet (a new capability registry, as each test-kit harness has; a service found late) never outlives them.
    /// A row built on the ray is also off the moment the ray's row is, not only at the next refresh.</summary>
    public static void Require(string capability)
    {
        Rows.Ensure();
        if (OnNow(capability)) return;
        Report();
        if (OnNow(capability)) return;
        throw ServiceCaps.Unavailable(capability);
    }

    /// <summary>The exception for a native "unavailable" answer under a row that still read available: the rows are
    /// reported again first, so the reason is the one of now.</summary>
    public static KilnUnavailableException Off(string capability)
    {
        Rows.Ensure();
        Report();
        return ServiceCaps.Unavailable(capability, "kiln.queries answered that it is unavailable");
    }
}

/// <summary>Started by Kiln (ServiceTick.AutoStart) at its first service tick: the rows are there before a mod asks.</summary>
internal static class QueriesStartup
{
    static QueriesStartup() => QueriesCaps.Rows.Ensure();
}
