// SPDX-License-Identifier: MIT
// Kiln.Saves in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). Reported at Kiln.Saves' start
// (its first use: the start subscribes to kiln.saves' pump, which a server whose mods never use Kiln.Saves does not
// pay for) and after a registry reset. kiln.saves has only the core's row; its sigdb features are lowered when the table
// is missing; InstanceData's rows (saves.instance_data, .carried, .sync) are reported with them.
namespace Kiln;

internal static unsafe class SavesCaps
{
    /// <summary>kiln.saves' table is here (the core's row).</summary>
    public const string Service = SavesNative.Service;

    /// <summary>The sigdb features kiln.saves serves.</summary>
    public static readonly string[] Features = ["saves.load", "saves.reasons", "saves.request", "saves.write"];

    internal static readonly ServiceCapRows Rows = new(Service, Report);

    // Kiln.Saves remembers a miss (its start, and its table): forgotten when the refresh finds the table
    static SavesCaps()
    {
        ServiceCaps.WhenFound(Service, SavesRuntime.LookAgain);
        ServiceCaps.WhenFound("kiln.spawn", SpawnLink.LookAgain);  // entity data's links to other services' tables
        ServiceCaps.WhenFound("kiln.players", PlayersLink.LookAgain);
    }

    private static void Report()
    {
        if (SavesNative.T == null) ServiceCaps.LowerFeatures(Service, Features);
        ServiceCaps.Probe("kiln.spawn");  // EntityData needs it: its row says whether it is here
        InstanceCapabilities.Refresh();
        InstanceCapabilities.ReportCarried();
    }

    /// <summary>The exception a member throws when <paramref name="capability"/> is off; the rows are reported first.
    /// A <see cref="NotSupportedException"/>, as these members threw before, with the same sentence.</summary>
    public static KilnUnavailableException Off(string capability, string detail)
    {
        Rows.Ensure();
        return ServiceCaps.Unavailable(capability, detail);
    }
}
