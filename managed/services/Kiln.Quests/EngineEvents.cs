// SPDX-License-Identifier: MIT
// Kiln.Quests engine on the event bus. All posted on the tick thread, built only while someone subscribes.
//   QuestStartedEvent, QuestStepEvent, QuestCompletedEvent, QuestFailedEvent: before the change; cancel to refuse it.
//   QuestStateChangedEvent: after a change is applied (not cancelable); what to mirror from.
//   QuestObjectiveProgressEvent: after a counter changed. ConversationOptionEvent: before a chosen option runs.
namespace Kiln;

/// <summary>Base of the quest events that fire before a change. Canceling refuses the change: the state stays as it
/// was and the caller gets a <see cref="QuestResult"/> with Ok = false. No field may be changed.</summary>
public abstract class QuestCancelableEvent : CancelableEvent
{
    internal QuestCancelableEvent(QuestDefinition quest, QuestHolder holder)
    {
        Quest = quest;
        Holder = holder;
    }

    public QuestDefinition Quest { get; }
    public QuestHolder Holder { get; }
    /// <summary>The player in game (null for the world, offline and synthetic holders).</summary>
    public Player? Player => Holder.Entity.IsNone || !Players.Available ? null : Players.Find(Holder.Entity);
}

/// <summary>A quest is about to start for a holder. A party quest fires one per member. Cancel: it does not start for
/// this holder.</summary>
public sealed class QuestStartedEvent : QuestCancelableEvent
{
    internal QuestStartedEvent(QuestDefinition quest, QuestHolder holder) : base(quest, holder)
    {
    }

    public override string ToString() => $"{Holder} starts {Quest.FullId}";
}

/// <summary>A step is about to finish. Cancel: the step stays active with its counters full; the engine asks again at
/// the next change of a counter or the next <see cref="Quests.Advance"/>.</summary>
public sealed class QuestStepEvent : QuestCancelableEvent
{
    internal QuestStepEvent(QuestDefinition quest, QuestHolder holder, QuestStep step, QuestStep? next) : base(quest, holder)
    {
        Step = step;
        Next = next;
    }

    public QuestStep Step { get; }
    public string StepId => Step.Id;
    /// <summary>The step that follows; null when this is the last.</summary>
    public QuestStep? Next { get; }
    public override string ToString() => $"{Holder} finishes {Quest.FullId}.{StepId}";
}

/// <summary>A quest is about to complete (its last step's objectives are done; before that step's <c>onDone</c> actions
/// and the rewards). Cancel: it stays active at its last step, and nothing of the step's end runs (since 0.2.0-dev.27:
/// before, the step's <c>onDone</c> ran first and ran again at the next completion).</summary>
public sealed class QuestCompletedEvent : QuestCancelableEvent
{
    internal QuestCompletedEvent(QuestDefinition quest, QuestHolder holder) : base(quest, holder)
    {
    }

    public override string ToString() => $"{Holder} completes {Quest.FullId}";
}

/// <summary>A quest is about to fail. Cancel: it stays active.</summary>
public sealed class QuestFailedEvent : QuestCancelableEvent
{
    internal QuestFailedEvent(QuestDefinition quest, QuestHolder holder, string reason) : base(quest, holder) => Reason = reason;

    public string Reason { get; }
    public override string ToString() => $"{Holder} fails {Quest.FullId}: {Reason}";
}

/// <summary>A holder's standing in a quest changed: status or step. Posted right after the change is applied (and
/// saved with the next world save). Not cancelable. This is the hook to mirror quest state elsewhere (Kiln.Quests/Native
/// mirrors it into the game's knowledge).</summary>
public sealed class QuestStateChangedEvent : KilnEvent
{
    internal QuestStateChangedEvent(QuestDefinition quest, QuestHolder holder, QuestStatus oldStatus, QuestStatus status, string? stepId, int stepIndex)
    {
        Quest = quest;
        Holder = holder;
        OldStatus = oldStatus;
        Status = status;
        StepId = stepId;
        StepIndex = stepIndex;
    }

    public QuestDefinition Quest { get; }
    public QuestHolder Holder { get; }
    public QuestStatus OldStatus { get; }
    public QuestStatus Status { get; }
    /// <summary>The current step; null when not at a step (done, failed, reset).</summary>
    public string? StepId { get; }
    public int StepIndex { get; }
    public override string ToString() => $"{Holder} {Quest.FullId}: {OldStatus} -> {Status}{(StepId is null ? "" : $" at {StepId}")}";
}

/// <summary>An objective's counter changed. Posted after the change. Not cancelable.</summary>
public sealed class QuestObjectiveProgressEvent : KilnEvent
{
    internal QuestObjectiveProgressEvent(QuestDefinition quest, QuestHolder holder, QuestStep step, int index, QuestObjective objective, int oldValue, int value)
    {
        Quest = quest;
        Holder = holder;
        Step = step;
        Index = index;
        Objective = objective;
        OldValue = oldValue;
        Value = value;
    }

    public QuestDefinition Quest { get; }
    public QuestHolder Holder { get; }
    public QuestStep Step { get; }
    /// <summary>The objective's place in the step.</summary>
    public int Index { get; }
    public QuestObjective Objective { get; }
    public int OldValue { get; }
    public int Value { get; }
    public int Target => Objective.Count;
    public override string ToString() => $"{Holder} {Quest.FullId}.{Step.Id}[{Index}]: {OldValue} -> {Value}/{Target}";
}

/// <summary>A player chose an option of a Kiln conversation. The server has checked the option's condition; it is about
/// to run the option's actions. Cancel: nothing runs and the node is shown again; set <see cref="RefuseMessage"/> to
/// tell the player why.</summary>
public sealed class ConversationOptionEvent : CancelableEvent
{
    internal ConversationOptionEvent(QuestHolder holder, DialogueDefinition dialogue, string node, int option, EntityId npc)
    {
        Holder = holder;
        Dialogue = dialogue;
        Node = node;
        Option = option;
        Npc = npc;
    }

    public QuestHolder Holder { get; }
    public DialogueDefinition Dialogue { get; }
    public string Node { get; }
    /// <summary>The option's place in the node's list.</summary>
    public int Option { get; }
    public EntityId Npc { get; }
    public string? RefuseMessage { get; set; }
    public override string ToString() => $"{Holder} chose {Dialogue.FullId}/{Node}[{Option}]";
}
