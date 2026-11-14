// SPDX-License-Identifier: MIT
// Kiln.World's sigdb features in the capability registry (docs/kiln/services/caps.md): lowered when kiln.world is not
// loaded, so Capabilities.Why names the missing service and not a feature that resolved for nobody. Reported at
// Kiln.World's first use (World's static constructor), as world.tracked_writes is (WorldCapabilities).
namespace Kiln;

internal static unsafe class WorldRows
{
    /// <summary>kiln.world's table is here (the core's row).</summary>
    public const string Service = WorldNative.Name;

    /// <summary>The sigdb features kiln.world serves.</summary>
    public static readonly string[] Features =
    [
        "world.component_bits", "world.dead_check", "world.entities", "world.find", "world.names", "world.names.events",
        "world.names.singletons", "world.programs", "world.queue", "world.read_other", "world.teleport", "world.write",
    ];

    internal static readonly ServiceCapRows Rows = new(Service, Report);

    // kiln.world's wrappers remember a miss (the entity table, the tracked writes'): forgotten when the refresh finds it
    static WorldRows()
    {
        ServiceCaps.WhenFound(Service, WorldNative.LookAgain);
        ServiceCaps.WhenFound(Service, WorldWrites.LookAgain);
    }

    private static void Report()
    {
        if (WorldNative.T == null) ServiceCaps.LowerFeatures(Service, Features);
        // world.tracked_writes: WorldCapabilities reports it once and follows the session; under a new registry binding
        // (a test kit's next harness) it reports again from the table that is there now
        WorldCapabilities.Reset();
        WorldCapabilities.Is(World.TrackedWritesCapability);
    }
}

/// <summary>Started by Kiln (ServiceTick.AutoStart) at its first service tick (0.2.0-dev.28): <c>world.tracked_writes</c>,
/// <c>world.transform_scale</c> and kiln.world's sigdb features have their rows before a mod first uses Kiln.World, so
/// <c>Capabilities.Is</c> answers from the start (as World's static constructor does at the first use otherwise).</summary>
internal static class WorldStartup
{
    static WorldStartup()
    {
        TransformScale.Start();
        WorldRows.Rows.Ensure();
    }
}
