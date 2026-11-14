// SPDX-License-Identifier: MIT
// Kiln.Terrain in the capability registry (docs/kiln/services/caps.md). The library has no native table: its rows say
// whether what it is built on is there. terrain.overlays is in the client gate's list for the client side
// (native/core/gates/dev28-19f-terrain.json): on a client that hosts a world it is held until its game test.
namespace Kiln;

internal static class TerrainCaps
{
    /// <summary>The library is loaded.</summary>
    public const string Library = "kiln.terrain";
    /// <summary><see cref="Terrain.Apply"/>: kiln.build reads, writes and keeps, and kiln.saves holds the ledger.</summary>
    public const string Overlays = "terrain.overlays";
    /// <summary>Baked terrain regions: not built.</summary>
    public const string Baked = "terrain.baked";
    /// <summary>Biome changes: not built.</summary>
    public const string Biomes = "terrain.biomes";

    private static readonly string[] Needs = ["kiln.build", "build.read", "build.write", "build.persist", "kiln.saves"];

    internal static readonly ServiceCapRows Rows = new(Library, Report, everySteps: 25);

    private static void Report()
    {
        ServiceCaps.Report(Library, CapabilityState.Available);
        // a world put behind the library (the test kit's fake) stands for kiln.build and kiln.saves
        var on = Terrain.BackendFactory is not null;
        if (!on)
        {
            on = true;
            foreach (var n in Needs) on &= Capabilities.Is(n);
        }
        ServiceCaps.Static(Overlays, on, CapabilityReason.ServiceMissing, "kiln.build or kiln.saves is not loaded", Needs);
        ServiceCaps.Report(Baked, CapabilityState.Unavailable, CapabilityReason.Policy, "baked terrain regions are not built in this version");
        ServiceCaps.Report(Biomes, CapabilityState.Unavailable, CapabilityReason.Policy, "biome changes are not built in this version");
    }

    /// <summary>Throws <see cref="KilnUnavailableException"/> naming <paramref name="capability"/> unless it is available.</summary>
    public static void Require(string capability)
    {
        Rows.Ensure();
        if (Capabilities.Is(capability)) return;
        Report();
        if (Capabilities.Is(capability)) return;
        throw ServiceCaps.Unavailable(capability);
    }
}

/// <summary>Started by Kiln (ServiceTick.AutoStart) at its first service tick: the rows are there before a mod asks.</summary>
internal static class TerrainStartup
{
    static TerrainStartup() => TerrainCaps.Rows.Ensure();
}
