// SPDX-License-Identifier: MIT
// Kiln's own taming steps (dossier 19e sections 4-5): what a mod declares (TamingDefinition and its steps, the refund
// policy, the timeouts, which creatures), the tokens a client echoes, and the results, receipts and events. Kiln ships the
// mechanism; every choice is the definition's.
namespace Kiln;

/// <summary>One step of a taming (19e 5.1). Since 0.2.0-dev.32.</summary>
public abstract record TamingStep
{
    private protected TamingStep() { }

    /// <summary>The step's kind in words ("feed", "weaken", "tool", "native").</summary>
    public abstract string Kind { get; }
}

/// <summary>Feed: <paramref name="Count"/> of a plain-stack item from the slot the player selected (exact, no search).</summary>
/// <param name="Item">The item (resolved: the items registry or the game's catalog).</param>
/// <param name="Count">1 to 999.</param>
public sealed record FeedStep(ItemId Item, int Count = 1) : TamingStep
{
    /// <inheritdoc/>
    public override string Kind => "feed";
}

/// <summary>Weaken: the target's health must be at most <paramref name="MaxHealthFraction"/> of its maximum (no cost, no
/// hidden damage). With <see cref="TamingDefinition.RequireThresholdUntilCommit"/> the threshold holds for every later step
/// and the final commit.</summary>
/// <param name="MaxHealthFraction">In (0, 1].</param>
public sealed record WeakenStep(double MaxHealthFraction) : TamingStep
{
    /// <inheritdoc/>
    public override string Kind => "weaken";
}

/// <summary>Use a tool: <paramref name="Wear"/> durability off the exact gear item the player selected, through the
/// game's own durability debit (<see cref="ItemCosts.Wear"/>).</summary>
/// <param name="Item">The tool's item.</param>
/// <param name="Wear">0 to 32767 (0: a use that costs nothing).</param>
/// <param name="AllowBreak">The wear may break the tool.</param>
public sealed record UseToolStep(ItemId Item, int Wear = 1, bool AllowBreak = false) : TamingStep
{
    /// <inheritdoc/>
    public override string Kind => "tool";
}

/// <summary>The game's own step(s), counted by the game (a player petting the target while it eats its favourite bait;
/// docs/re/taming.md): the step applies once the target's native progress has risen by <paramref name="Steps"/> since the
/// previous step was applied. No cost of Kiln's (the bait is the game's). Only for a tameable wild target.</summary>
/// <param name="Steps">1 to 255.</param>
public sealed record NativeStep(int Steps = 1) : TamingStep
{
    /// <inheritdoc/>
    public override string Kind => "native";
}

/// <summary>What happens to the costs of an attempt that does not end in a tame: the mod's choice (19e O3). Since
/// 0.2.0-dev.32.</summary>
public enum TamingRefundPolicy
{
    /// <summary>The default: a paid step stays paid whatever happens later (cancel, timeout, failure, logout, crash).</summary>
    NonRefundable = 0,
    /// <summary>Paid steps are given back when the attempt fails for a reason that is not the player's (the target died or
    /// went away, the world ended, the final tame was refused for good); a cancel or a timeout keeps them spent.</summary>
    RefundOnFail = 1,
    /// <summary>Paid steps are held until the tame: any other end (cancel, timeout, failure) gives every one back.</summary>
    Escrow = 2,
}

/// <summary>A taming, as a mod declares it (19e 5.1). Steps run in order; the last one tames. Since 0.2.0-dev.32.</summary>
public sealed record TamingDefinition
{
    /// <summary>The definition's id, unique per mod ("<c>my-mod:wolf</c>"; lower case, a mod prefix).</summary>
    public required string Id { get; init; }
    /// <summary>The steps (1 to 64).</summary>
    public required IReadOnlyList<TamingStep> Steps { get; init; }
    /// <summary>Which creatures it tames: creatures registry ids (<c>game:enemy_wildbeast_wolf_earlygame</c>, a mod's
    /// <c>my-mod:wolf</c>); a trailing <c>*</c> matches a prefix. Empty: none (a definition always names its targets).</summary>
    public IReadOnlyList<string> Creatures { get; init; } = [];
    /// <summary>How close the player must be, 1 to 8 m.</summary>
    public double RangeMeters { get; init; } = 3;
    /// <summary>The latest weaken threshold must hold until the tame.</summary>
    public bool RequireThresholdUntilCommit { get; init; } = true;
    /// <summary>Seconds of simulation without an applied step before the attempt expires (1 to 300).</summary>
    public double InactivitySeconds { get; init; } = 30;
    /// <summary>The longest an attempt lasts (at least the inactivity, at most 3600 s).</summary>
    public double MaxAttemptSeconds { get; init; } = 300;
    /// <summary>What happens to paid steps when the attempt ends without a tame.</summary>
    public TamingRefundPolicy Refund { get; init; } = TamingRefundPolicy.NonRefundable;
    /// <summary>The Kiln.Pets definition (<see cref="PetDefinition.Id"/>) a tame binds: when the tame commits, the owner's
    /// character gets a new pet record of it (<see cref="Pets.Grant"/>), and <see cref="TamingResult.Pet"/> is that pet. A
    /// roster slot of it is held from Begin to the end of the attempt, and checked again before the last step's cost
    /// (no cost is taken for a tame the roster cannot hold). Null: no pet; the tame is the attempt's own result and the
    /// mod's handlers decide what it means.</summary>
    public string? Pet { get; init; }
    /// <summary>With <see cref="Pet"/>: the tamed wild body is removed once the pet record is made (the pet's own body
    /// comes from <see cref="Pets.Summon"/>), so the world never has both. False: it stays as it is. Default true.</summary>
    public bool RemoveTarget { get; init; } = true;
    /// <summary>With <see cref="Pet"/>: the new pet is summoned at once (a body next to its owner, as
    /// <see cref="Pets.Summon"/>); a summon that is refused leaves the pet Ready (the tame stands). Default true.</summary>
    public bool SummonPet { get; init; } = true;
}

/// <summary>An attempt's id: random, never reused. Since 0.2.0-dev.32.</summary>
public readonly record struct TameAttemptId(Guid Value)
{
    /// <summary>For logs.</summary>
    public override string ToString() => Value.ToString("N")[..12];
}

/// <summary>The view of an attempt a request was made on (19e 4.2): the server's challenge (random, rotated by every applied
/// step), the revision and the step. The last answer gives it (<see cref="TamingProgress.Token"/>,
/// <see cref="TamingChoicesResult.Token"/>); a request echoes it (<see cref="TamingRequest.Token"/>). Since
/// 0.2.0-dev.32.</summary>
/// <param name="StepChallenge">The attempt's challenge.</param>
/// <param name="ExpectedRevision">The attempt's revision.</param>
/// <param name="StepIndex">The step it expects next (the steps completed).</param>
public sealed record TamingToken(Guid StepChallenge, ulong ExpectedRevision, int StepIndex);

/// <summary>A taming request, in the one request shape of Kiln.Pets and Kiln.Taming (as <see cref="PetRequest"/>): its id
/// and what it expects of the view it was made on. The same request again is the same answer and never a second cost;
/// another action under a used id is <c>RequestConflict</c>; a token the attempt moved on from is <c>StaleStep</c>. Since
/// 0.2.0-dev.32.</summary>
/// <param name="RequestId">New for every intended action, the same for a network retry of it. The answer
/// (<see cref="TamingResult.RequestId"/>), the events and the cost receipts carry it.</param>
/// <param name="Token">The attempt's token from the last answer: <see cref="Taming.Advance"/> and <see cref="Taming.Retry"/>
/// need it; <see cref="Taming.Begin"/> takes none.</param>
public sealed record TamingRequest(Guid RequestId, TamingToken? Token = null)
{
    /// <summary>A new request with a fresh request id, made on <paramref name="token"/>'s view.</summary>
    public static TamingRequest New(TamingToken? token = null) => new(Guid.NewGuid(), token);
}

/// <summary>Where an attempt is (19e 4.3). Since 0.2.0-dev.32.</summary>
public enum TamingState
{
    /// <summary>Waiting for the next step.</summary>
    Active,
    /// <summary>A step's cost is being paid (another request for it is Busy).</summary>
    CostPending,
    /// <summary>Every step is paid; the tame waits for a cost-free retry (the final condition was not met at the time).</summary>
    AwaitingCommit,
    /// <summary>Tamed.</summary>
    Committed,
    /// <summary>Canceled by the player or a mod.</summary>
    Canceled,
    /// <summary>Ended by a failure (the target, the world, the owner, a refusal for good, an uncertain cost).</summary>
    Failed,
    /// <summary>Ran out of time.</summary>
    Expired,
}

/// <summary>A refund of a paid step (the definition's <see cref="TamingRefundPolicy"/>). Since 0.2.0-dev.32.</summary>
/// <param name="Paid">The step's receipt.</param>
/// <param name="Refund">The refund's receipt (null: not given back: the step was not refundable, or nothing was taken).</param>
public sealed record TamingRefund(CostReceipt Paid, CostReceipt? Refund);

/// <summary>An attempt as it is now (immutable copy). Since 0.2.0-dev.32.</summary>
public sealed record TamingProgress(TameAttemptId Id, PlayerRef Owner, ulong WorldEpoch, ulong CharacterSession, EntityRef Target, string Definition,
    TamingState State, int CompletedSteps, int TotalSteps, ulong Revision, Guid StepChallenge, double StartedAtSimSeconds,
    double LastProgressAtSimSeconds, double ExpiresAtSimSeconds, IReadOnlyList<CostReceipt> Spent)
{
    /// <summary>The refunds made at the end (empty while it runs, and for a non-refundable definition).</summary>
    public IReadOnlyList<TamingRefund> Refunds { get; init; } = [];
    /// <summary>A cancel was asked while a cost was being paid: it ends when the cost settled.</summary>
    public bool CancelRequested { get; init; }
    /// <summary>The token the next request echoes (<see cref="TamingRequest.New"/>): the challenge, the revision and the
    /// steps completed.</summary>
    public TamingToken Token => new(StepChallenge, Revision, CompletedSteps);
}

/// <summary>A choice the server offers for a step (19e 5.1): a slot it read, as a selection, with a label.</summary>
public sealed record TamingItemChoice(ItemSelection Selection, string Label);

/// <summary>The choices for the attempt's next step. Since 0.2.0-dev.32.</summary>
/// <param name="Ok">True when the attempt waits for a step (its choices may be none: a weaken or native step).</param>
/// <param name="Reason">Why not, in words for a log.</param>
/// <param name="Token">The view the choices were read on: a request for one of them echoes it
/// (<c>TamingRequest.New(choices.Token)</c>); null when there is no such attempt of the player's.</param>
/// <param name="Choices">The slots that can pay the step.</param>
public sealed record TamingChoicesResult(bool Ok, string? Reason, TamingToken? Token, IReadOnlyList<TamingItemChoice> Choices) : IKilnResult
{
    /// <summary>More slots could pay the step than the 256 <see cref="Choices"/> lists: these are the first 256 in the
    /// owner's inventories' order (a UI says that there are more).</summary>
    public bool Truncated { get; init; }

    /// <summary>Why not, as a value: <see cref="TamingRefusal.None"/> when <see cref="Ok"/>, else the first word of
    /// <see cref="Reason"/>.</summary>
    public TamingRefusal Refusal => RefusalWords.Of(Ok, Reason, TamingRefusal.None, TamingRefusal.Other);
}

/// <summary>The result of a taming call (19e 5.1, 5.4). <see cref="Ok"/>: a step applied, the tame committed or the cancel
/// done. A deferred tame is Ok false with <see cref="StepApplied"/> true and the reason <c>CommitDeferred</c>. Since
/// 0.2.0-dev.32.</summary>
/// <param name="Ok">See above.</param>
/// <param name="Reason">Why not, in words for a log: its first word is <see cref="Refusal"/> (NotOwner, OwnerOffline,
/// StaleSession, Busy, StaleStep, RequestConflict, StaleSelection, OutOfRange, Threshold, BodyGone, TemplateMismatch,
/// Capacity, DefinitionInUse, CommitDeferred, AttemptClosed, Expired, UncertainCommit, PartialCostCommitted, Canceled,
/// RefusedByMod, NativePending, TooManyRequests, Refused). Decide by <see cref="Refusal"/>, never by the text.</param>
/// <param name="Progress">The attempt after the call.</param>
/// <param name="Pet">The pet the tame made (a definition with <see cref="TamingDefinition.Pet"/>), as it was made; null
/// otherwise.</param>
/// <param name="RequestId">The request id the call ran under.</param>
/// <param name="StepApplied">A step applied (its cost, if any, is spent).</param>
/// <param name="Receipts">The receipts of this call's cost (and of every spent step at a terminal result).</param>
public sealed record TamingResult(bool Ok, string? Reason, TamingProgress? Progress, PetSnapshot? Pet, Guid RequestId, bool StepApplied,
    IReadOnlyList<CostReceipt> Receipts) : IKilnResult
{
    /// <summary>Why not, as a value: <see cref="TamingRefusal.None"/> when <see cref="Ok"/>, else the first word of
    /// <see cref="Reason"/> (<see cref="TamingRefusal.Other"/> for a word this Kiln has no member for). A deferred tame is
    /// <see cref="TamingRefusal.CommitDeferred"/>.</summary>
    public TamingRefusal Refusal => RefusalWords.Of(Ok, Reason, TamingRefusal.None, TamingRefusal.Other);
}

// ---- before-events (cancelable; their context is a copy nobody can change) ----

/// <summary>The context of a Begin (19e 5.3).</summary>
public sealed record TamingBeginContext(Guid RequestId, PlayerRef Player, EntityRef Target, string Definition, int TotalSteps);

/// <summary>The context of an Advance (19e 5.3).</summary>
public sealed record TamingAdvanceContext(TamingProgress Before, Guid RequestId, TamingToken Token, int StepIndex, string StepKind, ItemSelection? Selection,
    int QuotedQuantity, int QuotedWear, bool WillTryFinalCommit);

/// <summary>The context of the final tame (19e 5.3): before the last step's cost, or a cost-free retry.</summary>
public sealed record TamingCommitContext(TameAttemptId Attempt, Guid RequestId, PlayerRef Owner, EntityRef Target, string Definition,
    IReadOnlyList<CostReceipt> EarlierCosts, ItemSelection? PendingSelection, int PendingQuantity, int PendingWear, bool CostFreeRetry);

/// <summary>A taming is about to begin: cancel it to refuse (nothing is reserved yet). Posted on the tick thread before
/// anything is reserved. Since 0.2.0-dev.32.</summary>
[ServerOnly]
public sealed class TamingBeginningEvent : CancelableEvent
{
    internal TamingBeginningEvent(TamingBeginContext context) => Context = context;
    /// <summary>What is asked.</summary>
    public TamingBeginContext Context { get; }
}

/// <summary>A step is about to be paid and applied: cancel it to refuse (nothing is spent, the token stays valid).</summary>
[ServerOnly]
public sealed class TamingAdvancingEvent : CancelableEvent
{
    internal TamingAdvancingEvent(TamingAdvanceContext context) => Context = context;
    /// <summary>What is asked.</summary>
    public TamingAdvanceContext Context { get; }
}

/// <summary>The tame is about to be made (before the last step's cost, or a cost-free retry): cancel it to refuse; earlier
/// paid steps stay paid by the definition's refund policy.</summary>
[ServerOnly]
public sealed class TamingCommittingEvent : CancelableEvent
{
    internal TamingCommittingEvent(TamingCommitContext context) => Context = context;
    /// <summary>What is asked.</summary>
    public TamingCommitContext Context { get; }
}

// ---- after-events (19e 5.3) ----

/// <summary>Base of the taming after-events: an attempt changed. Posted on the tick thread; not cancelable.</summary>
[ServerOnly]
public abstract class TamingChangedEvent : KilnEvent
{
    private protected TamingChangedEvent(TamingProgress? before, TamingProgress after, Guid requestId, IReadOnlyList<CostReceipt> receipts, string cause)
    {
        Before = before;
        After = after;
        RequestId = requestId;
        Receipts = receipts;
        Cause = cause;
    }

    /// <summary>The attempt before (null for a start).</summary>
    public TamingProgress? Before { get; }
    /// <summary>The attempt now.</summary>
    public TamingProgress After { get; }
    /// <summary>The request that changed it (empty for a timeout or a failure Kiln saw).</summary>
    public Guid RequestId { get; }
    /// <summary>The receipts of the change.</summary>
    public IReadOnlyList<CostReceipt> Receipts { get; }
    /// <summary>Why, in a word (a request, Expired, BodyGone, OwnerLeft, WorldEnded, DefinitionGone, UncertainCommit...).</summary>
    public string Cause { get; }

    /// <summary>For logs.</summary>
    public override string ToString() => $"{GetType().Name} {After.Id} {After.State} {After.CompletedSteps}/{After.TotalSteps} ({Cause})";
}

/// <summary>An attempt started.</summary>
public sealed class TamingStartedEvent : TamingChangedEvent
{
    internal TamingStartedEvent(TamingProgress after, Guid requestId) : base(null, after, requestId, [], "Begin") { }
}

/// <summary>A step applied (its cost spent).</summary>
public sealed class TamingStepChangedEvent : TamingChangedEvent
{
    internal TamingStepChangedEvent(TamingProgress before, TamingProgress after, Guid requestId, IReadOnlyList<CostReceipt> receipts)
        : base(before, after, requestId, receipts, "Step") { }
}

/// <summary>Every step is paid; the tame waits for a cost-free retry.</summary>
public sealed class TamingCommitDeferredEvent : TamingChangedEvent
{
    internal TamingCommitDeferredEvent(TamingProgress before, TamingProgress after, Guid requestId, string cause)
        : base(before, after, requestId, [], cause) { }
}

/// <summary>The target was tamed: the one ownership event of a Kiln taming (posted after Kiln.Pets' own
/// <see cref="PetGrantedEvent"/> when the definition binds a pet).</summary>
public sealed class TamedEvent : TamingChangedEvent
{
    internal TamedEvent(TamingProgress before, TamingProgress after, Guid requestId, PetSnapshot? pet) : base(before, after, requestId, after.Spent, "Tamed") =>
        Pet = pet;
    /// <summary>The pet the tame made (<see cref="TamingDefinition.Pet"/>), or null.</summary>
    public PetSnapshot? Pet { get; }
}

/// <summary>The attempt is over with a tame (after <see cref="TamedEvent"/>).</summary>
public sealed class TamingCompletedEvent : TamingChangedEvent
{
    internal TamingCompletedEvent(TamingProgress before, TamingProgress after, Guid requestId) : base(before, after, requestId, after.Spent, "Completed") { }
}

/// <summary>The attempt was canceled; its paid steps are as the refund policy says (<see cref="TamingProgress.Refunds"/>).</summary>
public sealed class TamingCanceledEvent : TamingChangedEvent
{
    internal TamingCanceledEvent(TamingProgress before, TamingProgress after, Guid requestId, string cause) : base(before, after, requestId, after.Spent, cause) { }
}

/// <summary>The attempt ran out of time.</summary>
public sealed class TamingExpiredEvent : TamingChangedEvent
{
    internal TamingExpiredEvent(TamingProgress before, TamingProgress after, string cause) : base(before, after, Guid.Empty, after.Spent, cause) { }
}

/// <summary>The attempt failed.</summary>
public sealed class TamingFailedEvent : TamingChangedEvent
{
    internal TamingFailedEvent(TamingProgress before, TamingProgress after, Guid requestId, string cause) : base(before, after, requestId, after.Spent, cause) { }
}
