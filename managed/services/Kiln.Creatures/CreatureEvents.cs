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
