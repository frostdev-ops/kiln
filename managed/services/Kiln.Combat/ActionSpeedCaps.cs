// SPDX-License-Identifier: MIT
// Kiln.ActionSpeed in the capability registry (docs/kiln/services/caps.md). combat.action_speed and combat.bow_timing are
// sigdb features (the core's rows), lowered here when kiln.combat.speed cannot serve them; combat.melee_sampling is a
// sigdb feature too (its sites resolve) that this release does not serve; combat.action_speed.prediction is this
// service's own row.
namespace Kiln;

internal static unsafe class SpeedCaps
{
    public const string Service = SpeedNative.Name;
    public const string ActionSpeed = "combat.action_speed";
    public const string BowTiming = "combat.bow_timing";
    public const string MeleeSampling = "combat.melee_sampling";
    public const string Prediction = "combat.action_speed.prediction";

    internal static readonly ServiceCapRows Rows = new(Service, Report);

    private static void Report()
    {
        var s = SpeedNative.Read();
        ServiceCaps.LowerOff(MeleeSampling, CapabilityReason.ServiceOlder,
            "planned: Kiln's melee contact sampler needs an in-game check first; nothing is sampled between frames in this release (a mod may keep its own sampling and read the rate with ActionSpeed.Get)");
        if (s is not { } v)
        {
            var (reason, text) = ServiceCaps.Cause(CapabilityReason.ServiceMissing,
                "kiln.combat.speed is not loaded (an older kiln-combat.dll, or none)", Service);
            ServiceCaps.Lower(ActionSpeed, reason, text);
            ServiceCaps.Lower(BowTiming, reason, text);
            ServiceCaps.Static(Prediction, false, reason, text, Service);
            return;
        }
        var detail = SpeedNative.Detail(v);
        var why = detail.Length > 0 ? detail : "see kiln.log (speed:)";
        var wraps = (v.Parts & SpeedNative.Wraps) != 0;
        var client = Mods.Side == ModSide.Client;
        if (!wraps || (v.Parts & (SpeedNative.Sequence | SpeedNative.Graph)) != (SpeedNative.Sequence | SpeedNative.Graph))
            ServiceCaps.LowerOff(ActionSpeed, CapabilityReason.Faulted, $"kiln.combat.speed cannot scale here: {why}");
        else if (client && (v.Parts & SpeedNative.Presentation) == 0)
            ServiceCaps.LowerOff(ActionSpeed, CapabilityReason.SymbolMissing, $"the presentation sites did not resolve on this game client: {why}");
        if (!wraps || (v.Parts & SpeedNative.Bow) == 0)
            ServiceCaps.LowerOff(BowTiming, CapabilityReason.Faulted, $"kiln.combat.speed cannot divide bow timings here: {why}");
        if (!client)
            ServiceCaps.Report(Prediction, CapabilityState.Unavailable, CapabilityReason.WrongSide,
                "prediction is a game client's: a dedicated server sends its rates to joined clients, which apply them there");
        else
            ServiceCaps.Static(Prediction,
                wraps && (v.Parts & (SpeedNative.Prediction | SpeedNative.Sequence | SpeedNative.Presentation)) ==
                (SpeedNative.Prediction | SpeedNative.Sequence | SpeedNative.Presentation),
                CapabilityReason.Faulted, $"the local player's prediction cannot be scaled here: {why}", Service);
    }
}
