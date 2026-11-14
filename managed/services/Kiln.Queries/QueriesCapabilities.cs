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

    private const string World = "kiln.world";

    internal static readonly ServiceCapRows Rows = new(Service, Report, everySteps: 25, refreshOnlyWithService: true);

    private static void Report()
    {
        var here = QueriesNative.T != null;
        if (!here) ServiceCaps.LowerFeatures(Service, [VoxelRay]);
        const string missing = "kiln.queries is not loaded";
        // Static's `needs` only explain an off row: whether each need is on is asked here
        ServiceCaps.Static(Ground, here && Capabilities.Is(VoxelRay), CapabilityReason.ServiceMissing, missing, Service, VoxelRay);
        ServiceCaps.Static(LineOfSight, here && Capabilities.Is(VoxelRay) && Capabilities.Is(World), CapabilityReason.ServiceMissing, missing, Service, VoxelRay, World);
        ServiceCaps.Static(OverlapOrigins, here && Capabilities.Is(World), CapabilityReason.ServiceMissing, missing, Service, World);
    }

    /// <summary>Throws <see cref="KilnUnavailableException"/> naming <paramref name="capability"/> unless it is available.
    /// A row built on the ray is off the moment the ray's row is, not only at the next refresh.</summary>
    public static void Require(string capability)
    {
        Rows.Ensure();
        var on = QueriesNative.T != null && Capabilities.Is(capability) && (capability is not (Ground or LineOfSight) || Capabilities.Is(VoxelRay));
        if (on) return;
        if (Capabilities.Is(capability)) Report();   // the row lags what it is built on: say so before the exception reads it
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
