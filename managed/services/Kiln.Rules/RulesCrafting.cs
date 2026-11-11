// SPDX-License-Identifier: MIT
namespace Kiln;

// FreeCrafting and KeepConsumables are native hooks inside the game's own transactions (kiln.rules creative.cpp,
// docs/re/creative-mode-146.md Part 3): the recipe's payment answers "paid" for an operator with FREE_CRAFTING (the
// output still goes in through the same transaction), and the consume removals (fired arrows and thrown items, staff
// ammunition; food, drinks, potions and bandages) remove nothing for an actor with KEEP_CONSUMABLES. KeepConsumables
// needs at least one of the two hooks (the status says which). No managed event mints items. Where the hook is
// not installed (unknown build, an older kiln.rules) the rule stays off and is listed in Denied, as before. The same
// holds for character flight and glider steering (their hooks, this server's build).
internal static class RulesCrafting
{
    /// <summary>Free crafts and kept consumables so far (kiln.rules' counters; 0 without them).</summary>
    public static (ulong Crafts, ulong Kept) Counters =>
        RulesNative.CreativeStatus(out var c) ? (c.CraftsFree, c.ConsumesKept) : (0UL, 0UL);

    internal static EffectiveRules Supported(EffectiveRules rules) => Supported(rules, RulesNative.Hooks);

    internal static EffectiveRules Supported(EffectiveRules rules, uint hooks)
    {
        var noCraft = rules.FreeCrafting && (hooks & RulesNative.HookCraft) == 0;
        var noKeep = rules.KeepConsumables && (hooks & (RulesNative.HookConsume | RulesNative.HookConsumeBuff)) == 0;
        var noFly = rules.CharacterFlight != CharacterFlightMode.Off && (hooks & RulesNative.HookFlight) == 0;
        var noSteer = rules.GlideSteer is not null && (hooks & RulesNative.HookGlideSteer) == 0;
        if (!noCraft && !noKeep && !noFly && !noSteer) return rules;
        var denied = rules.Denied.ToList();
        if (noCraft) denied.Add("freeCraftingUnavailable");
        if (noKeep) denied.Add("keepConsumablesUnavailable");
        if (noFly) denied.Add("characterFlightUnavailable");
        if (noSteer) denied.Add("glideSteerUnavailable");  // GlideBoost then flies as before
        return rules with
        {
            FreeCrafting = rules.FreeCrafting && !noCraft, KeepConsumables = rules.KeepConsumables && !noKeep,
            CharacterFlight = noFly ? CharacterFlightMode.Off : rules.CharacterFlight, GlideSteer = noSteer ? null : rules.GlideSteer,
            Denied = denied,
        };
    }
}
