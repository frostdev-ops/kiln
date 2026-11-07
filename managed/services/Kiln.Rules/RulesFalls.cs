// SPDX-License-Identifier: MIT
// NoFallDamage: the server cancels Kiln.Combat's fall event for players whose rules say so. Kiln.Combat holds every
// fall for one step while anything listens, so Kiln listens only once some player has had the rule.
namespace Kiln;

internal static class RulesFalls
{
    private static EventSubscription? _sub;
    private static ulong _canceled;

    /// <summary>Falls canceled by the rule this session.</summary>
    public static ulong Canceled => _canceled;

    /// <summary>Called when a player's rules change (tick thread).</summary>
    public static void Changed(EffectiveRules rules)
    {
        RulesCrafting.Changed(rules);
        if (!rules.NoFallDamage || _sub is not null) return;
        try
        {
            if (!Combat.Available || Combat.Status is not { Falls: true })
            {
                RulesRuntime.Log(LogLevel.Warn, "NoFallDamage: Kiln.Combat does not see falls on this build; the rule does nothing");
                return;
            }
            var outer = ModContext.CurrentMod;
            ModContext.CurrentMod = null;  // Kiln's subscription, whichever mod's rule set led here
            try
            {
                _sub = Events.On<EntityFallEvent>(OnFall, EventPriority.High);
            }
            finally
            {
                ModContext.CurrentMod = outer;
            }
            RulesRuntime.Log(LogLevel.Info, "NoFallDamage: listening to falls");
        }
        catch (Exception e) when (e is FileNotFoundException or TypeLoadException or InvalidOperationException)
        {
            RulesRuntime.Log(LogLevel.Warn, $"NoFallDamage unavailable: {e.Message}");
        }
    }

    private static void OnFall(EntityFallEvent e)
    {
        if (!RulesRuntime.Effective(e.Entity).NoFallDamage) return;
        e.Canceled = true;
        _canceled++;
        RulesRuntime.Log(LogLevel.Info, $"player {e.Entity}: a {e.Distance:0.#} m fall canceled (would have taken {e.OriginalDamage} health)");
    }
}
