// SPDX-License-Identifier: MIT
// More conditions and actions for creature rules (RuleBlock.When / Do, ArsenalRule.If / Do). Three kinds, cheapest first:
//   - the game's own constraint and action kinds, written into the arsenal as data: no hook, the game's rule walker runs them;
//   - Kiln's conditions that read the world (who is near, how many summons live): code on the tick thread every few tenths of a second, published
//     to the table the game's job threads read (Is.Custom underneath);
//   - Kiln's actions that change the world (cast, summon, heal): queued from the game's job thread, run on the tick thread in the next step.
// docs/kiln/modding/npc-ai.md has the list with JSON forms.
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>Who a summon fights for.</summary>
public enum SummonSide
{
    /// <summary>The summoner's faction, and the summoner's target as its first.</summary>
    Summoner,
    /// <summary>Its own template's faction.</summary>
    Own,
}

/// <summary>What <see cref="AiRulesApi"/> adds to <see cref="Is"/> and <see cref="Act"/>.</summary>
public static class AiRulesApi
{
    private static RuleCondition StateIs(params EnemyState[] states) => Is.Game("StateConstrain", ("states", states.Select(s => s.ToString()).ToArray()));

    extension(Is)
    {
        // ---- the game's kinds: data, no hook ----

        /// <summary>It is fighting (the game's Combat state).</summary>
        public static RuleCondition InCombat => StateIs(EnemyState.Combat);

        /// <summary>Its AI is in one of these states (<c>StateConstrain</c>).</summary>
        public static RuleCondition InState(params EnemyState[] states)
        {
            if (states.Length == 0) throw new ArgumentException("name at least one state");
            return StateIs(states);
        }

        /// <summary>It has a combat target (<c>HasCombatTargetConstrain</c>).</summary>
        public static RuleCondition HasTarget => Is.Game("HasCombatTargetConstrain");

        /// <summary>Its target is a player, or with false is not (<c>IsPlayerTargetConstrain</c>).</summary>
        public static RuleCondition TargetIsPlayer(bool player = true) => Is.Game("IsPlayerTargetConstrain", ("isPlayer", player));

        /// <summary>Its target's health is at or below this share, 0..1 (<c>CurrentTargetHealthPercentageConstrain</c>).</summary>
        public static RuleCondition TargetHealthBelow(double fraction) =>
            Is.Game("CurrentTargetHealthPercentageConstrain", ("minPercentage", 0.0), ("maxPercentage", Fraction(fraction, "TargetHealthBelow")));

        /// <summary>It was hit within the last <paramref name="seconds"/> (<c>LastHitReceivedConstrain</c>).</summary>
        public static RuleCondition HitWithin(double seconds) => Is.Game("LastHitReceivedConstrain", ("minDuration", 0.0), ("maxDuration", Positive(seconds, "HitWithin")));

        /// <summary>A roll that comes up this share of the times, 0..1 (<c>RandomChanceConstrain</c>). A rule's <c>Chance</c> is the same roll.</summary>
        public static RuleCondition Chance(double chance) => Is.Game("RandomChanceConstrain", ("chance", Fraction(chance, "Chance")));

        /// <summary>Its target stands inside its home zone (<c>IsTargetInHomeZoneConstraint</c>).</summary>
        public static RuleCondition TargetAtHome => Is.Game("IsTargetInHomeZoneConstraint");

        // ---- Kiln's: the tick looks, the rule reads ----

        /// <summary>At least <paramref name="count"/> creatures or players it would fight are within <paramref name="metres"/>, by the game's own check
        /// (with Kiln's relations in it). Looked at every half second.</summary>
        public static RuleCondition EnemiesNear(int count, double metres) =>
            Is.Custom(npc => AiRuntime.CountNear(npc.Entity, Positive(metres, "EnemiesNear"), enemies: true) >= count, 0.5);

        /// <summary>At least <paramref name="count"/> creatures on its side are within <paramref name="metres"/> (itself not counted).</summary>
        public static RuleCondition AlliesNear(int count, double metres) =>
            Is.Custom(npc => AiRuntime.CountNear(npc.Entity, Positive(metres, "AlliesNear"), enemies: false) >= count, 0.5);

        /// <summary>Fewer than <paramref name="count"/> of the creatures it summoned (<see cref="Act.Summon(string, int, double, double, SummonSide)"/>) are alive.</summary>
        public static RuleCondition SummonsBelow(int count) => Is.Custom(npc => AiRuntime.SummonsOf(npc.Entity) < count, 0.5);
    }

    extension(Act)
    {
        // ---- the game's kinds ----

        /// <summary>It runs from the fight, as the game's creatures flee (the state condition WantsToFlee; how far: <see cref="AIBlock.Flee"/>).</summary>
        public static RuleAction Flee() => Act.Game("TriggerStateConditionAction", ("condition", "WantsToFlee"));

        /// <summary>It backs away from its target, between these metres, and fights on (<c>RunAwayAction</c>).</summary>
        public static RuleAction BackOff(double min, double max) => Act.Game("RunAwayAction", ("minDistance", Positive(min, "BackOff")), ("maxDistance", Positive(max, "BackOff")));

        /// <summary>Plays one of the game's sequences (an attack, a shout) by its GUID, without choosing an attack (<c>PlayActionSequenceAction</c>).</summary>
        public static RuleAction Play(Guid sequence, bool wait = false) => Act.Game("PlayActionSequenceAction", ("sequence", sequence), ("waitUntilFinished", wait));

        /// <summary>Takes no damage, or with false takes it again (<c>SetImmuneAction</c>).</summary>
        public static RuleAction Immune(bool immune = true) => Act.Game("SetImmuneAction", ("immune", immune));

        /// <summary>Stands still for a while, facing its target (<c>PauseAction</c>).</summary>
        public static RuleAction Pause(double minSeconds, double maxSeconds) =>
            Act.Game("PauseAction", ("minDuration", Positive(minSeconds, "Pause")), ("maxDuration", Positive(maxSeconds, "Pause")), ("lookAtCombatTarget", true));

        /// <summary>Turns one of the game's AI features on (<c>EnableFeatureAction</c>).</summary>
        public static RuleAction EnableFeature(EnemyFeatures feature) => Act.Game("EnableFeatureAction", ("feature", Single(feature)));

        /// <summary>Turns one of the game's AI features off (<c>DisableFeatureAction</c>).</summary>
        public static RuleAction DisableFeature(EnemyFeatures feature) => Act.Game("DisableFeatureAction", ("feature", Single(feature)));

        // ---- Kiln's: queued, run on the tick ----

        /// <summary>Heals it by a share of its most health, 0..1.</summary>
        public static RuleAction Heal(double fraction)
        {
            Fraction(fraction, "Heal");
            return Act.Custom(npc => AiRuntime.Heal(npc, fraction));
        }

        /// <summary>
        /// Calls <paramref name="count"/> creatures around it: a creature block of this mod (<c>"imp"</c>), of another (<c>"mod:imp"</c>), a kiln.creatures
        /// variant or a game creature (<c>GameTemplates</c>), within <paramref name="radius"/> metres. They go after its target, live
        /// <paramref name="seconds"/> (0: until killed) and count for <see cref="Is.SummonsBelow"/>.
        /// </summary>
        public static RuleAction Summon(string creature, int count = 1, double radius = 3, double seconds = 0, SummonSide side = SummonSide.Summoner)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(creature);
            if (count is < 1 or > 16) throw new ArgumentException($"count {count} is outside 1..16");
            var mod = BlocksBridge.CallerModId ?? "kiln";
            return AiRuntime.SummonAction(new SummonSpec(creature, mod, count, Positive(radius, "Summon"), Math.Max(0, seconds), side));
        }

        /// <summary>
        /// Casts a delivery at its target from where it stands, without an animation: a projectile flies at it. The delivery is compiled with the
        /// creature (a rule of a creature block). For a cast with an animation, give the behaviour an attack with that <c>Delivery</c> and use
        /// <c>Act.Attack</c>.
        /// </summary>
        public static RuleAction Cast(IDeliveryBlock delivery)
        {
            ArgumentNullException.ThrowIfNull(delivery);
            return AiLowering.CastAction(delivery);
        }

        /// <summary>Puts a status on its target, or on itself. Queued like <see cref="NpcBlocksApi"/>'s <c>Act.Effect</c>.</summary>
        public static RuleAction Apply(StatusApply status, NpcEffectOn on = NpcEffectOn.Target) => Act.Effect(new EffectBlock { Applies = status }, on);
    }

    extension(Blocks)
    {
        /// <summary>A sense filter for data files: <c>"senseFilter": "name"</c>. Runs on the tick thread, as <see cref="AIBlock.SenseFilter"/>.</summary>
        public static void SenseFilter(string name, Func<NpcView, EntityId, bool> filter) => NpcRuntime.Named(AiRuntime.Filters, name, filter);
    }

    internal static double Fraction(double v, string what) =>
        v is >= 0 and <= 1 ? v : throw new ArgumentOutOfRangeException(what, v, $"{what}: {v} is outside 0..1");

    internal static double Positive(double v, string what) =>
        v >= 0 && double.IsFinite(v) ? v : throw new ArgumentOutOfRangeException(what, v, $"{what}: {v} must be 0 or more");

    private static string Single(EnemyFeatures f)
    {
        if (f == EnemyFeatures.None || ((uint)f & ((uint)f - 1)) != 0) throw new ArgumentException($"{f}: one feature at a time");
        return f.ToString();
    }
}

/// <summary>What a summon action calls.</summary>
internal sealed record SummonSpec(string Creature, string Mod, int Count, double Radius, double Seconds, SummonSide Side);
