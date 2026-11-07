// SPDX-License-Identifier: MIT
namespace Kiln;

// The managed event fallbacks cannot implement these rules safely. CraftEvent is a request before the game's
// station/permission checks, and another handler can un-cancel it after we queue an output. PlayerConsumedEvent
// also fires on slot replacement or a failed slot read: it is not proof of consumption. Do not mint items from
// either event. These rules need authoritative, transactional native hooks before they can be enabled again.
internal static class RulesCrafting
{
    public static (ulong Crafts, ulong Kept) Counters => (0, 0);

    internal static EffectiveRules Supported(EffectiveRules rules)
    {
        if (!rules.FreeCrafting && !rules.KeepConsumables) return rules;
        var denied = rules.Denied.ToList();
        if (rules.FreeCrafting) denied.Add("freeCraftingUnavailable");
        if (rules.KeepConsumables) denied.Add("keepConsumablesUnavailable");
        return rules with { FreeCrafting = false, KeepConsumables = false, Denied = denied };
    }
}
