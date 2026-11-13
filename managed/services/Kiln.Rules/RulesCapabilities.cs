// SPDX-License-Identifier: MIT
// Kiln.Rules in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). Reported at the service's
// start (RulesStartup, the first service tick, on both sides). kiln.rules has only the core's row; its sigdb features
// are lowered when the table is missing. Nothing in Kiln.Rules throws for a missing table (a rule set is kept and
// applied where the hooks are), so it has no row of its own.
namespace Kiln;

internal static unsafe class RulesCaps
{
    /// <summary>kiln.rules' table is here (the core's row).</summary>
    public const string Service = RulesNative.Service;

    /// <summary>The sigdb features kiln.rules serves.</summary>
    public static readonly string[] Features =
    [
        "rules.consume", "rules.consume_buff", "rules.costs", "rules.craft", "rules.cursor", "rules.dig", "rules.flight",
        "rules.fly", "rules.glide", "rules.glide_steer", "rules.glide_stick", "rules.glide_view", "rules.noclip",
        "rules.shroud", "rules.storage", "rules.zone",
    ];

    internal static readonly ServiceCapRows Rows = new(Service, Report);

    private static void Report()
    {
        if (RulesNative.T == null) ServiceCaps.LowerFeatures(Service, Features);
    }
}
