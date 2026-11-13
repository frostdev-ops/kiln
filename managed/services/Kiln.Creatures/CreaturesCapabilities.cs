// SPDX-License-Identifier: MIT
// Kiln.Creatures in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). Reported at the service's
// first use; it has no row of its own (it lowers its sigdb features when kiln.creatures is missing, which the core's row
// says from the start), so it does not start at the first service tick.
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
