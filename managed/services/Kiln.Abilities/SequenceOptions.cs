// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>How <see cref="Abilities.PlaySequence(EntityId, Guid, SequenceOptions)"/> starts a sequence.</summary>
public sealed record SequenceOptions
{
    /// <summary>Abort what the actor is doing (ExternalAbortAndOverride), hold the keep-alive states.</summary>
    public static readonly SequenceOptions Default = new();

    /// <summary>ExternalTrigger (the trigger the game's emotes use), hold the keep-alive states.</summary>
    public static readonly SequenceOptions Queue = new() { AbortCurrent = false };

    /// <summary>Abort the actor's current sequence (trigger ExternalAbortAndOverride, as fall damage does); false =
    /// ExternalTrigger (as emotes do). Default true.</summary>
    public bool AbortCurrent { get; init; } = true;

    /// <summary>Hold the states the sequence's KeepAlive and Loop events wait for (InRangeCombat, InRangeCombatShooting,
    /// ...: what a player's held input sets), so input-gated sequences such as staff casts and bow shots play to the
    /// end instead of stopping at their first keep-alive check; each loop body plays once. The states go into the
    /// running sequence's own state mask (replicated) and are taken back as soon as the last check has passed. No
    /// effect on sequences without keep-alive events (melee attacks, emotes). Default true.</summary>
    public bool HoldInputs { get; init; } = true;
}
