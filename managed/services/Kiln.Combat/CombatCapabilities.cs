// SPDX-License-Identifier: MIT
// Kiln.Combat in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). Reported at the service's
// start (CombatStartup, the first service tick); combat.attributes follows the session (the game's attribute resource is
// read once a server world runs) and is refreshed on the service tick, never in a getter.
namespace Kiln;

internal static unsafe class CombatCaps
{
    /// <summary>kiln.combat's table is here (the core's row).</summary>
    public const string Service = CombatNative.Name;

    /// <summary>Kiln.Combat's own row: kiln.combat holds only the hits a handler's entity filter lets through (table v3).</summary>
    public const string HoldFilter = "combat.hold_filter";

    /// <summary>Kiln.Combat's session row: <see cref="Attributes"/> reads and writes the game's attributes (kiln.combat's
    /// attribute functions, and the game's attribute resource read once a server world runs).</summary>
    public const string Attributes = "combat.attributes";

    /// <summary>The sigdb features kiln.combat serves.</summary>
    public static readonly string[] Features =
        ["combat.actor_states", "combat.attr_heals", "combat.deaths", "combat.falls", "combat.health", "combat.hits", "combat.regen"];

    internal static readonly ServiceCapRows Rows = new(Service, Report, everySteps: 25, refreshOnlyWithService: true);

    private static void Report()
    {
        var t = CombatNative.T;
        if (t == null) ServiceCaps.LowerFeatures(Service, Features);
        ServiceCaps.Static(HoldFilter, CombatNative.V3 != null, CapabilityReason.ServiceOlder,
            "this kiln-combat.dll has no hold filter (update Kiln): filtered handlers still get only their entities' hits, but every hit waits a step",
            Service);
        if (t != null && CombatNative.V2 == null)
            ServiceCaps.Report(Attributes, CapabilityState.Unavailable, CapabilityReason.ServiceOlder,
                "this Kiln runtime's kiln.combat has no attribute functions (update Kiln)");
        else
            ServiceCaps.Session(Attributes, global::Kiln.Attributes.Available,
                "the game's attribute resource is read once a server world runs", Service);
    }

    /// <summary>The exception a member throws when <paramref name="capability"/> is off; the rows are reported first.</summary>
    public static KilnUnavailableException Off(string capability, string detail)
    {
        Rows.Ensure();
        return ServiceCaps.Unavailable(capability, detail);
    }
}

/// <summary>Started by Kiln (ServiceTick.AutoStart) at its first service tick: the capability rows are there before a
/// mod asks for them, not from Kiln.Combat's first use.</summary>
internal static class CombatStartup
{
    static CombatStartup() => CombatCaps.Rows.Ensure();
}
