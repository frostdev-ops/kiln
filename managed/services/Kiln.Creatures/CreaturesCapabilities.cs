// SPDX-License-Identifier: MIT
// Kiln.Creatures in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). Reported at the service's
// first use; kiln.creatures decides what works when it starts, so the rows are fixed from then on.
namespace Kiln;

internal static unsafe class CreaturesCaps
{
    /// <summary>The native table is here (the core's row).</summary>
    public const string Service = CreaturesNative.Name;

    /// <summary>sigdb: arsenals (<see cref="Arsenals"/>).</summary>
    public const string Arsenals = "creatures.arsenals";

    /// <summary>sigdb: attack overrides (<see cref="AttackControl"/>, an arsenal's <c>PickAttack</c>).</summary>
    public const string Attacks = "creatures.attacks";

    /// <summary>The sigdb features kiln.creatures serves.</summary>
    public static readonly string[] Features =
    [
        "creatures.ai", "creatures.arsenal_writer", Arsenals, Attacks, "creatures.commands", "creatures.ready",
        "creatures.reflection", "creatures.rules", "creatures.slots", "creatures.table",
    ];

    internal static readonly ServiceCapRows Rows = new(Service, Report);

    private static void Report()
    {
        if (CreaturesNative.T == null) ServiceCaps.LowerFeatures(Service, Features);
    }

    /// <summary>The service is here but older than the call needs: names kiln.creatures, with what is missing.</summary>
    public static KilnUnavailableException Older(string detail)
    {
        Rows.Ensure();
        return new KilnUnavailableException(Service, detail);
    }

    /// <summary>A feature of kiln.creatures is off on this build.</summary>
    public static KilnUnavailableException Off(string capability, string detail)
    {
        Rows.Ensure();
        return ServiceCaps.Unavailable(capability, detail);
    }
}
