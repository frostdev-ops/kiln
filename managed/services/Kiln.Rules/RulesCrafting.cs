// SPDX-License-Identifier: MIT
namespace Kiln;

// FreeCrafting and KeepConsumables are native hooks inside the game's own transactions (kiln.rules creative.cpp,
// docs/re/creative-mode-146.md Part 3): the recipe's payment answers "paid" for an operator with FREE_CRAFTING (the
// output still goes in through the same transaction), and the two consume removals (fired arrows and thrown items,
// staff ammunition) remove nothing for an actor with KEEP_CONSUMABLES. No managed event mints items. Where the hook is
// not installed (unknown build, an older kiln.rules) the rule stays off and is listed in Denied, as before.
internal static class RulesCrafting
{
    /// <summary>Free crafts and kept consumables so far (kiln.rules' counters; 0 without them).</summary>
    public static (ulong Crafts, ulong Kept) Counters =>
        RulesNative.CreativeStatus(out var c) ? (c.CraftsFree, c.ConsumesKept) : (0UL, 0UL);

    internal static EffectiveRules Supported(EffectiveRules rules) => Supported(rules, RulesNative.Hooks);

    internal static EffectiveRules Supported(EffectiveRules rules, uint hooks)
    {
        var noCraft = rules.FreeCrafting && (hooks & RulesNative.HookCraft) == 0;
        var noKeep = rules.KeepConsumables && (hooks & RulesNative.HookConsume) == 0;
        if (!noCraft && !noKeep) return rules;
        var denied = rules.Denied.ToList();
        if (noCraft) denied.Add("freeCraftingUnavailable");
        if (noKeep) denied.Add("keepConsumablesUnavailable");
        return rules with { FreeCrafting = rules.FreeCrafting && !noCraft, KeepConsumables = rules.KeepConsumables && !noKeep, Denied = denied };
    }
}
