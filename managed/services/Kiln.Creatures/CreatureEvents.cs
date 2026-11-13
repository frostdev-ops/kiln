// SPDX-License-Identifier: MIT
// Creature events on Kiln's event bus. Posted on the tick thread in Pre, for what happened in the previous sim step,
// and only built while someone subscribes (the native hooks record nothing otherwise).
namespace Kiln;

/// <summary>
/// The enemy AI changed state (enemy01 SetState): <see cref="From"/> to <see cref="To"/>, because of
/// <see cref="Condition"/>. Observe only. Fires on the tick thread in Pre for the previous sim step's changes, for every
/// enemy-AI creature (spawned, natural, bosses, companions). On a dedicated server creatures far from players stay in
/// Spawning, so most changes come near players.
/// </summary>
public sealed class CreatureStateEvent : KilnEvent
{
    internal CreatureStateEvent(EntityId entity, EnemyState from, EnemyState to, StateCondition condition, ulong step)
    {
        Entity = entity;
        From = from;
        To = to;
        Condition = condition;
        Step = step;
    }

    public EntityId Entity { get; }
    public EnemyState From { get; }
    public EnemyState To { get; }
    /// <summary>The StateCondition HandleCondition was given (<see cref="StateCondition.Unknown"/> if none).</summary>
    public StateCondition Condition { get; }
    /// <summary>The server-world step it happened in.</summary>
    public ulong Step { get; }

    /// <summary>Its template (null when the entity is gone already).</summary>
    public CreatureTemplate? Template => Spawn.Inspect(Entity)?.Template is { } g ? Creatures.Template(g) : null;
}

/// <summary>
/// A mod changed one creature through Kiln.Creatures: a <see cref="CreatureRef"/> value (level, faction, scale, home,
/// a resistance, loot, features), its AI switch, phase or held target, an AI override (<see cref="AiAccess.Set(string, string)"/>),
/// or a reset of them. Posted on the tick thread right after the change took. Observe only. Built only while someone
/// subscribes. The game's own changes of a creature raise nothing here: see <see cref="CreatureStateEvent"/> and
/// <see cref="CreatureTargetEvent"/> for those. Since 0.2.0-dev.27.
/// </summary>
public sealed class CreatureChangedEvent : KilnEvent
{
    internal CreatureChangedEvent(EntityId entity, string what, string origin)
    {
        Entity = entity;
        What = what;
        Origin = origin;
    }

    /// <summary>The creature.</summary>
    public EntityId Entity { get; }

    /// <summary>What was changed: the value's name as <see cref="CreatureRef"/> has it (<c>Level</c>, <c>Faction</c>,
    /// <c>Home</c>, ...), <c>AiEnabled</c>, <c>Phase</c>, <c>Target</c>, <c>Ai.&lt;Component&gt;.&lt;path&gt;</c> for an AI
    /// override, <c>Reset</c> or <c>Ai.Reset</c> when layers were dropped.</summary>
    public string What { get; }

    /// <summary>The id of the mod that made the change ("" outside mod code).</summary>
    public string Origin { get; }

    /// <summary>The creature, what changed and who did it, for a log line.</summary>
    public override string ToString() => $"creature {Entity}: {What} changed by {(Origin.Length > 0 ? Origin : "kiln")}";
}

/// <summary>
/// An active enemy AI's target changed (SelectedTargets[0]): <see cref="Target"/> is the new one (None = lost),
/// <see cref="Previous"/> the old one. Observe only. Checked every other step (about 80 ms) for creatures whose AI ran
/// in the last two minutes; fires on the tick thread in Pre.
/// </summary>
public sealed class CreatureTargetEvent : KilnEvent
{
    internal CreatureTargetEvent(EntityId entity, EntityId target, EntityId previous, EnemyState state, ulong step)
    {
        Entity = entity;
        Target = target;
        Previous = previous;
        State = state;
        Step = step;
    }

    public EntityId Entity { get; }
    public EntityId Target { get; }
    public EntityId Previous { get; }
    /// <summary>The last AI state Kiln saw for the creature.</summary>
    public EnemyState State { get; }
    public ulong Step { get; }
    public bool Acquired => !Target.IsNone && Previous.IsNone;
    public bool Lost => Target.IsNone;
}

/// <summary>A variant was applied to a creature (spawned by <see cref="Creatures.Spawn"/>, a natural spawn replaced by
/// a spawn rule, or <see cref="CreatureVariants.Apply"/>). Observe only; tick thread, right after the overrides were
/// written.</summary>
public sealed class CreatureVariantAppliedEvent : KilnEvent
{
    internal CreatureVariantAppliedEvent(EntityId entity, CreatureVariant variant, bool natural)
    {
        Entity = entity;
        Variant = variant;
        Natural = natural;
    }

    public EntityId Entity { get; }
    public CreatureVariant Variant { get; }
    /// <summary>A natural spawn (spawn rule), not a Kiln spawn.</summary>
    public bool Natural { get; }
}
