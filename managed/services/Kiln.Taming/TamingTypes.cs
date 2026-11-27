// SPDX-License-Identifier: MIT
// Kiln's own taming steps (dossier 19e sections 4-5): what a mod declares (TamingDefinition and its steps, the refund
// policy, the timeouts, which creatures), the tokens a client echoes, and the results, receipts and events. Kiln ships the
// mechanism; every choice is the definition's.
namespace Kiln;

/// <summary>
/// One step of a taming: the base of <see cref="FeedStep"/>, <see cref="WeakenStep"/>, <see cref="UseToolStep"/> and
/// <see cref="NativeStep"/> (no other step kind can be made). A <see cref="TamingDefinition"/> lists its steps in order;
/// a player takes them one at a time with <see cref="Taming.Advance"/>, and the last one tames. A step is immutable data
/// and holds no state of an attempt. Since 0.2.0-dev.32.
/// </summary>
/// <seealso cref="TamingDefinition.Steps"/>
/// <seealso cref="Taming.Advance"/>
public abstract record TamingStep
{
    private protected TamingStep() { }

    /// <summary>The step's kind in words: <c>"feed"</c>, <c>"weaken"</c>, <c>"tool"</c> or <c>"native"</c>. It is the
    /// <c>kind</c> of a step in a taming's JSON (<see cref="Taming.DefineJson"/>) and the <see cref="TamingAdvanceContext.StepKind"/>
    /// of the before-event.</summary>
    public abstract string Kind { get; }
}

/// <summary>Feed: <paramref name="Count"/> of a plain-stack item from the slot the player selected (exact, no search). The
/// cost is taken with <see cref="ItemCosts.Take"/> from the <see cref="ItemSelection"/> given to <see cref="Taming.Advance"/>:
/// that slot must hold a plain stack of <paramref name="Item"/> with at least <paramref name="Count"/>; nothing else is searched.
/// In Kiln.Blocks a <c>TamingStepBlock.Feed</c> makes one.</summary>
/// <param name="Item">The item, as a resolved <see cref="ItemId"/> (a game item of the items registry, or a mod's own item).</param>
/// <param name="Count">How many to take: 1 to 999.</param>
/// <seealso cref="Taming.Choices"/>
public sealed record FeedStep(ItemId Item, int Count = 1) : TamingStep
{
    /// <inheritdoc/>
    public override string Kind => "feed";
}

/// <summary>Weaken: the target's health must be at most <paramref name="MaxHealthFraction"/> of its maximum (no cost, no
/// hidden damage). With <see cref="TamingDefinition.RequireThresholdUntilCommit"/> the threshold holds for every later step
/// and the final commit. A weaken step takes no <see cref="ItemSelection"/>: pass null to <see cref="Taming.Advance"/>.</summary>
/// <param name="MaxHealthFraction">The most health the target may have, as a share of its maximum: above 0, at most 1
/// (0.4 = at 40 % health or less).</param>
public sealed record WeakenStep(double MaxHealthFraction) : TamingStep
{
    /// <inheritdoc/>
    public override string Kind => "weaken";
}

/// <summary>Use a tool: <paramref name="Wear"/> durability off the exact gear item the player selected, through the
/// game's own durability debit (<see cref="ItemCosts.Wear"/>).</summary>
/// <param name="Item">The tool's item (a gear item: the selection must hold one).</param>
/// <param name="Wear">Durability to take, 0 to 32767 (0: a use that costs nothing).</param>
/// <param name="AllowBreak">True: the wear may break the tool. False (the default): a wear that would leave the tool
/// below 1 durability is refused (<see cref="CostRefusal.WouldBreak"/>).</param>
/// <seealso cref="ItemCosts.Wear"/>
public sealed record UseToolStep(ItemId Item, int Wear = 1, bool AllowBreak = false) : TamingStep
{
    /// <inheritdoc/>
    public override string Kind => "tool";
}

/// <summary>The game's own step(s), counted by the game (a player petting the target while it eats its favourite bait;
/// docs/re/taming.md): the step applies once the target's native progress has risen by <paramref name="Steps"/> since the
/// previous step was applied. No cost of Kiln's (the bait is the game's). Only for a tameable wild target. Pass null to
/// <see cref="Taming.Advance"/>; the answer is <c>NativePending</c> until the game's own count has risen far enough.</summary>
/// <param name="Steps">How many of the game's steps this step waits for: 1 to 255.</param>
/// <seealso cref="NativeTaming"/>
public sealed record NativeStep(int Steps = 1) : TamingStep
{
    /// <inheritdoc/>
    public override string Kind => "native";
}

/// <summary>What happens to the costs of an attempt that does not end in a tame: the mod's choice, set per definition
/// (<see cref="TamingDefinition.Refund"/>). Kiln gives refunds back through <see cref="ItemCosts.Refund"/>; the receipts are
/// in <see cref="TamingProgress.Refunds"/>. Since 0.2.0-dev.32.</summary>
public enum TamingRefundPolicy
{
    /// <summary>The default: a paid step stays paid whatever happens later (cancel, timeout, failure, logout, crash).</summary>
    NonRefundable = 0,
    /// <summary>Paid steps are given back when the attempt ends as <see cref="TamingState.Failed"/> (the target died or went
    /// away, the owner left, the world ended, the mod's definition went, or the final tame was refused for good); a cancel
    /// or a timeout keeps them spent. A cost whose receipt is <see cref="CostReceipt.Uncertain"/> is never given back.</summary>
    RefundOnFail = 1,
    /// <summary>Paid steps are held until the tame: any other end (cancel, timeout, failure) gives every one back (a cost whose
    /// receipt is <see cref="CostReceipt.Uncertain"/> excepted).</summary>
    Escrow = 2,
}

/// <summary>
/// A taming, as a mod declares it: which creatures it tames, the steps a player takes in order (the last one tames), the
/// timeouts, what happens to paid steps when the attempt fails, and the <c>Kiln.Pets</c> definition a tame makes. Register
/// it with <see cref="Taming.Define"/>, or write it as JSON for <see cref="Taming.DefineJson"/>, or as a
/// <c>TamingBlock</c> in Kiln.Blocks: all three make this record. Kiln ships the mechanism; every choice here is the
/// mod's. Since 0.2.0-dev.32.
/// </summary>
/// <remarks>
/// <para>A definition is data: declare it when the mod loads. It needs no world and holds for every world of the process until
/// the mod unloads. Only the attempts (<see cref="Taming.Begin"/> and the calls after it) run on a server's tick thread.</para>
/// <para>Time is simulation time: <see cref="InactivityTimeout"/> and <see cref="MaxAttemptDuration"/> stand still while the
/// world is paused or stopped.</para>
/// </remarks>
/// <example>
/// A wolf that must be weakened to 40 % health and then fed two lean meat from a slot the player picks. A failure that is
/// not the player's gives the meat back, and a tame makes the pet <c>my-mod:tame-wolf</c> (defined with <see cref="Pets.Define"/>):
/// <code>
/// TamingDefinitionResult defined = Taming.Define(new TamingDefinition
/// {
///     Id = "my-mod:wolf",
///     Creatures = ["game:enemy_wildbeast_wolf_earlygame"],
///     Steps = [new WeakenStep(0.4), new FeedStep(leanMeat, 2)],   // leanMeat: an ItemId read from the items registry
///     Refund = TamingRefundPolicy.RefundOnFail,
///     Pet = "my-mod:tame-wolf",
/// });
/// if (!defined.Ok) Log.Warn($"taming not defined: {defined.Reason}");
/// </code>
/// The same taming as JSON (<see cref="Taming.DefineJson"/>):
/// <code>
/// { "id": "my-mod:wolf", "creatures": ["game:enemy_wildbeast_wolf_earlygame"], "pet": "my-mod:tame-wolf",
///   "steps": [ { "kind": "weaken", "maxHealthFraction": 0.4 },
///              { "kind": "feed", "item": "game:food_t1_raw_bad_meat_leanmeat", "count": 2 } ],
///   "refund": "refundOnFail" }
/// </code>
/// </example>
/// <seealso cref="Taming.Define"/>
/// <seealso cref="PetDefinition"/>
/// <seealso href="https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/taming.md">Taming: the modder's guide</seealso>
public sealed record TamingDefinition
{
    /// <summary>The definition's id: <c>&lt;mod id&gt;:&lt;name&gt;</c> (<c>my-mod:wolf</c>), lower case, at most 128 characters,
    /// with the mod's own id as the prefix. <see cref="Taming.Define"/> refuses another mod's prefix.</summary>
    public required string Id { get; init; }
    /// <summary>The steps, in the order a player takes them: 1 to 64. The last step tames.</summary>
    public required IReadOnlyList<TamingStep> Steps { get; init; }
    /// <summary>Which creatures it tames: creatures registry ids (<c>game:enemy_wildbeast_wolf_earlygame</c>, a mod's
    /// <c>my-mod:wolf</c>); a trailing <c>*</c> matches a prefix. Empty: none (a definition always names its targets).</summary>
    public IReadOnlyList<string> Creatures { get; init; } = [];
    /// <summary>How close the player must be to the target for every step, in metres: 1 to 8. Default 3.</summary>
    public double RangeMeters { get; init; } = 3;
    /// <summary>True (the default): the latest <see cref="WeakenStep"/> threshold holds for every later step and for the final
    /// tame, so a target that heals above it is refused (<c>Threshold</c>). False: it counts only when its own step is taken.</summary>
    public bool RequireThresholdUntilCommit { get; init; } = true;
    /// <summary>How long in simulation time without an applied step before the attempt expires: 1 to 300 s. Default 30 s
    /// (JSON: <c>inactivitySeconds</c>, a number of seconds). The attempt's own deadline is
    /// <see cref="TamingProgress.ExpiresAt"/>.</summary>
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>The longest an attempt lasts in simulation time: at least <see cref="InactivityTimeout"/>, at most 3600 s.
    /// Default 300 s (JSON: <c>maxAttemptSeconds</c>).</summary>
    public TimeSpan MaxAttemptDuration { get; init; } = TimeSpan.FromSeconds(300);
    // the engine's and the JSON reader's names, in seconds (internal)
    internal double InactivitySeconds { get => InactivityTimeout.TotalSeconds; init => InactivityTimeout = Seconds.Of(value); }
    internal double MaxAttemptSeconds { get => MaxAttemptDuration.TotalSeconds; init => MaxAttemptDuration = Seconds.Of(value); }
    /// <summary>What happens to paid steps when the attempt ends without a tame. Default
    /// <see cref="TamingRefundPolicy.NonRefundable"/>.</summary>
    public TamingRefundPolicy Refund { get; init; } = TamingRefundPolicy.NonRefundable;
    /// <summary>The Kiln.Pets definition (<see cref="PetDefinition.Id"/>) a tame binds: when the tame commits, the owner's
    /// character gets a new pet record of it (<see cref="Pets.Grant(CharacterKey, string, PetRequest)"/>), and <see cref="TamingResult.Pet"/> is that pet. A
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

/// <summary>An attempt's id: random, never reused. It names the attempt in every call after <see cref="Taming.Begin"/> and in
/// <see cref="TamingProgress.Id"/>. Since 0.2.0-dev.32.</summary>
/// <param name="Value">The id's GUID.</param>
public readonly record struct TameAttemptId(Guid Value)
{
    /// <summary>The first 12 hex digits of the id, for logs (not unique enough to look an attempt up by).</summary>
    public override string ToString() => Value.ToString("N")[..12];
}

/// <summary>
/// The view of an attempt a request was made on (19e 4.2): the server's challenge (random, rotated by every applied step),
/// the revision and the step index the server last gave. The last answer gives it (<see cref="TamingProgress.Token"/>,
/// <see cref="TamingChoicesResult.Token"/>); a request echoes it (<see cref="TamingRequest.Token"/>). A token that no longer
/// matches the attempt is <c>StaleStep</c> and changes nothing. Since 0.2.0-dev.32.
/// </summary>
/// <remarks>
/// Make the next request on the last answer's token: <c>TamingRequest.New(progress.Token)</c>. <see cref="Taming.Advance"/>
/// and <see cref="Taming.Retry"/> check all three values; <see cref="Taming.Cancel"/> with a token cancels only while it is
/// current (without one it cancels from a stale view). The request id is the request's (<see cref="TamingRequest.RequestId"/>):
/// repeat the same request for a network retry of one action (the first answer is given again and nothing is taken twice);
/// never reuse its id for another action (<c>RequestConflict</c>).
/// </remarks>
/// <param name="StepChallenge">The server's random challenge for the current step (<see cref="TamingProgress.StepChallenge"/>);
/// it changes every time a step applies.</param>
/// <param name="ExpectedRevision">The attempt's revision the caller saw (<see cref="TamingProgress.Revision"/>).</param>
/// <param name="StepIndex">The number of steps the caller saw done (<see cref="TamingProgress.CompletedSteps"/>): the index of
/// the step it asks to take.</param>
public sealed record TamingToken(Guid StepChallenge, ulong ExpectedRevision, int StepIndex);

/// <summary>A taming request, in the one request shape of Kiln.Pets and Kiln.Taming (as <see cref="PetRequest"/>): its id
/// and what it expects of the view it was made on. The same request again is the same answer and never a second cost;
/// another action under a used id is <c>RequestConflict</c>; a token the attempt moved on from is <c>StaleStep</c>. Since
/// 0.2.0-dev.32.</summary>
/// <param name="RequestId">New for every intended action, the same for a network retry of it; not empty. The answer
/// (<see cref="TamingResult.RequestId"/>), the events and the cost receipts carry it.</param>
/// <param name="Token">The attempt's token from the last answer: <see cref="Taming.Advance"/> and <see cref="Taming.Retry"/>
/// need it; <see cref="Taming.Cancel"/> takes it when it should cancel only an attempt that has not moved on (without one it
/// cancels from a stale view); <see cref="Taming.Begin"/> takes none.</param>
public sealed record TamingRequest(Guid RequestId, TamingToken? Token = null)
{
    /// <summary>A new request with a fresh request id, made on <paramref name="token"/>'s view.</summary>
    /// <param name="token">The last answer's token (<see cref="TamingProgress.Token"/>); null for a begin, or a cancel from a
    /// stale view.</param>
    /// <returns>A request with a new request id.</returns>
    public static TamingRequest New(TamingToken? token = null) => new(Guid.NewGuid(), token);
}

/// <summary>Where an attempt is (<see cref="TamingProgress.State"/>). <see cref="Committed"/>, <see cref="Canceled"/>,
/// <see cref="Failed"/> and <see cref="Expired"/> are final: the attempt takes no more calls. Since 0.2.0-dev.32.</summary>
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

/// <summary>A refund of a paid step (the definition's <see cref="TamingRefundPolicy"/>), pairing the step's receipt with the
/// receipt of what was given back. Since 0.2.0-dev.32.</summary>
/// <param name="Paid">The receipt of the step's cost, as it was taken.</param>
/// <param name="Refund">The refund's receipt (<see cref="ItemCosts.Refund"/>); null when nothing was given back: the step was
/// not refundable, or nothing had been taken.</param>
public sealed record TamingRefund(CostReceipt Paid, CostReceipt? Refund);

/// <summary>
/// An attempt as it is now: an immutable copy, never updated. Make the next request on its <see cref="Token"/>, or ask again
/// with <see cref="Taming.Inspect"/>. Since 0.2.0-dev.32.
/// </summary>
/// <remarks>Its times are simulation time (the game's own world clock, from its zero: the same scale as
/// <see cref="ExpiresAt"/>); they stand still while the world does not run. An attempt stays readable for 10 minutes of
/// simulation after it closed.</remarks>
/// <param name="Id">The attempt's id.</param>
/// <param name="Owner">The player who began it. Only they may advance, retry or cancel it.</param>
/// <param name="WorldEpoch">The server world session the attempt belongs to. Another world session ends it (<c>StaleSession</c>).</param>
/// <param name="CharacterSession">The owner's join the attempt belongs to. Leaving and joining again is another session, and
/// ends the attempt (<c>OwnerLeft</c>).</param>
/// <param name="Target">The creature being tamed.</param>
/// <param name="Definition">The taming definition's id (<see cref="TamingDefinition.Id"/>).</param>
/// <param name="State">Where the attempt is.</param>
/// <param name="CompletedSteps">Steps applied so far; also the index of the next step.</param>
/// <param name="TotalSteps">The definition's number of steps.</param>
/// <param name="Revision">Starts at 1 and goes up by one each time a step applies; part of the next <see cref="Token"/>.</param>
/// <param name="StepChallenge">The server's challenge for the current step; part of the next <see cref="Token"/>.</param>
/// <param name="StartedAt">When the attempt began.</param>
/// <param name="LastProgressAt">When a step last applied (the start, before the first one).</param>
/// <param name="ExpiresAt">When the attempt expires if nothing changes: the earlier of the last progress plus
/// <see cref="TamingDefinition.InactivityTimeout"/> and the start plus <see cref="TamingDefinition.MaxAttemptDuration"/>.</param>
/// <param name="Spent">The receipts of the steps paid so far.</param>
public sealed record TamingProgress(TameAttemptId Id, PlayerRef Owner, ulong WorldEpoch, ulong CharacterSession, EntityRef Target, string Definition,
    TamingState State, int CompletedSteps, int TotalSteps, ulong Revision, Guid StepChallenge, TimeSpan StartedAt,
    TimeSpan LastProgressAt, TimeSpan ExpiresAt, IReadOnlyList<CostReceipt> Spent)
{
    // in seconds (internal)
    internal double StartedAtSimSeconds => StartedAt.TotalSeconds;
    internal double LastProgressAtSimSeconds => LastProgressAt.TotalSeconds;
    internal double ExpiresAtSimSeconds => ExpiresAt.TotalSeconds;

    /// <summary>The refunds made at the end (empty while it runs, and for a non-refundable definition).</summary>
    public IReadOnlyList<TamingRefund> Refunds { get; init; } = [];
    /// <summary>A cancel was asked while a cost was being paid: it ends when the cost settled.</summary>
    public bool CancelRequested { get; init; }
    /// <summary>The token the next request echoes (<see cref="TamingRequest.New"/>): the challenge, the revision and the
    /// steps completed.</summary>
    public TamingToken Token => new(StepChallenge, Revision, CompletedSteps);
}

/// <summary>A slot the server offers to pay a step: an inventory slot it read, as an <see cref="ItemSelection"/>, with a
/// label for a menu. Pass <see cref="Selection"/> to <see cref="Taming.Advance"/> to pay the step from that slot; the server
/// checks it again when the cost runs (<c>StaleSelection</c> if the slot changed). Since 0.2.0-dev.32.</summary>
/// <param name="Selection">The slot, as read: its inventory owner, slot, item and count.</param>
/// <param name="Label">A short text for a menu: the slot's name and count (at most 64 characters). It is not localized.</param>
public sealed record TamingItemChoice(ItemSelection Selection, string Label);

/// <summary>The answer of <see cref="Taming.Choices"/>: the slots that can pay the attempt's next step, with the token of the
/// view they were read on. Since 0.2.0-dev.32.</summary>
/// <param name="Ok">True when the list is the real one (it may be empty: weaken and native steps take no slot, and a feed
/// or tool step may have no matching slot). False: <paramref name="Reason"/> says why.</param>
/// <param name="Reason">Why not, in words for a log: <c>AttemptClosed</c> (no such attempt of this player) or
/// <c>WrongState</c> (the attempt is not waiting for a step); its first word is <see cref="Refusal"/>. Null when
/// <paramref name="Ok"/>.</param>
/// <param name="Token">The view the choices were read on: a request for one of them echoes it
/// (<c>TamingRequest.New(choices.Token)</c>); null when there is no such attempt of the player's.</param>
/// <param name="Choices">The slots that can pay the step, in the order the owner's inventories list them: the first 256
/// (<see cref="Truncated"/> when more could pay).</param>
public sealed record TamingChoicesResult(bool Ok, string? Reason, TamingToken? Token, IReadOnlyList<TamingItemChoice> Choices) : IKilnResult
{
    /// <summary>More slots could pay the step than the 256 <see cref="Choices"/> lists: these are the first 256 in the
    /// owner's inventories' order (a UI says that there are more).</summary>
    public bool Truncated { get; init; }

    /// <summary>Why not, as a value: <see cref="TamingRefusal.None"/> when <see cref="Ok"/>, else the first word of
    /// <see cref="Reason"/>.</summary>
    public TamingRefusal Refusal => RefusalWords.Of(Ok, Reason, TamingRefusal.None, TamingRefusal.Other);
}

/// <summary>
/// The answer of <see cref="Taming.Begin"/>, <see cref="Taming.Advance"/>, <see cref="Taming.Retry"/> and
/// <see cref="Taming.Cancel"/>. <see cref="Ok"/> means a step applied, the tame committed or the cancel is done. A deferred
/// tame is <c>Ok</c> false with <see cref="StepApplied"/> true and the reason <c>CommitDeferred</c>: every step is paid and
/// the tame waits for <see cref="Taming.Retry"/>. Since 0.2.0-dev.32.
/// </summary>
/// <remarks>A refusal is a result, never an exception: check <see cref="Ok"/> first, then decide by
/// <see cref="Refusal"/> (<c>r.Refusal == TamingRefusal.Busy</c>), never by the text: <see cref="Reason"/> starts with that
/// word and may go on with a colon and details for a log. Always make the next request on <see cref="Progress"/>'s token,
/// also after a refusal: a refusal leaves the attempt as it was.</remarks>
/// <param name="Ok">True: the action did what it asked.</param>
/// <param name="Reason">Why not, in words for a log, its first word <see cref="Refusal"/>: <c>NotOwner</c>,
/// <c>OwnerOffline</c>, <c>StaleSession</c>, <c>Busy</c> (a step's cost is being paid, or another attempt claims the target),
/// <c>StaleStep</c> (the token does not match: read the attempt again), <c>RequestConflict</c> (a request id reused for
/// another action), <c>StaleSelection</c>, <c>OutOfRange</c>, <c>Threshold</c> (the target is above the weaken threshold),
/// <c>BodyGone</c>, <c>TemplateMismatch</c>, <c>DefinitionUnknown</c>, <c>Capacity</c>, <c>DefinitionInUse</c>,
/// <c>CommitDeferred</c>, <c>AttemptClosed</c>, <c>Expired</c>, <c>UncertainCommit</c>, <c>PartialCostCommitted</c>,
/// <c>Canceled</c> (the attempt was canceled meanwhile), <c>RefusedByMod</c> (a mod's handler refused), <c>NativePending</c>
/// (the game has not counted the native steps yet), <c>TooManyRequests</c>, <c>Refused</c> (the cost or the bind was refused;
/// its reason follows). Null when <paramref name="Ok"/>.</param>
/// <param name="Progress">The attempt after the call; null only when no such attempt exists.</param>
/// <param name="Pet">The pet the tame made (a definition with <see cref="TamingDefinition.Pet"/>), as it was made; null
/// otherwise.</param>
/// <param name="RequestId">The request id the call ran under (the request's).</param>
/// <param name="StepApplied">True when a step applied, so its cost, if any, is spent.</param>
/// <param name="Receipts">The receipts of this call's cost (and of every spent step at a final result). Check
/// <see cref="CostReceipt.Committed"/> and <see cref="CostReceipt.Uncertain"/> before trusting a count.</param>
public sealed record TamingResult(bool Ok, string? Reason, TamingProgress? Progress, PetSnapshot? Pet, Guid RequestId, bool StepApplied,
    IReadOnlyList<CostReceipt> Receipts) : IKilnResult
{
    /// <summary>Why not, as a value: <see cref="TamingRefusal.None"/> when <see cref="Ok"/>, else the first word of
    /// <see cref="Reason"/> (<see cref="TamingRefusal.Other"/> for a word this Kiln has no member for). A deferred tame is
    /// <see cref="TamingRefusal.CommitDeferred"/>.</summary>
    public TamingRefusal Refusal => RefusalWords.Of(Ok, Reason, TamingRefusal.None, TamingRefusal.Other);
}

// ---- before-events (cancelable; their context is a copy nobody can change) ----

/// <summary>What <see cref="Taming.Begin"/> asks, as <see cref="TamingBeginningEvent"/> shows it to handlers. A copy: changing
/// nothing here changes the attempt.</summary>
/// <param name="RequestId">The caller's request id.</param>
/// <param name="Player">The player who asks.</param>
/// <param name="Target">The creature to tame.</param>
/// <param name="Definition">The definition's id.</param>
/// <param name="TotalSteps">How many steps the definition has.</param>
public sealed record TamingBeginContext(Guid RequestId, PlayerRef Player, EntityRef Target, string Definition, int TotalSteps);

/// <summary>What <see cref="Taming.Advance"/> asks, as <see cref="TamingAdvancingEvent"/> shows it to handlers: the step, what
/// it would cost and whether it would try the final tame. A copy: changing nothing here changes the attempt.</summary>
/// <param name="Before">The attempt before the step.</param>
/// <param name="RequestId">The caller's request id.</param>
/// <param name="Token">The caller's token.</param>
/// <param name="StepIndex">The index of the step about to be taken.</param>
/// <param name="StepKind">The step's <see cref="TamingStep.Kind"/>.</param>
/// <param name="Selection">The slot the player selected; null for a weaken or native step.</param>
/// <param name="QuotedQuantity">The items the step would take (a feed step); 0 otherwise.</param>
/// <param name="QuotedWear">The durability the step would take (a tool step); 0 otherwise.</param>
/// <param name="WillTryFinalCommit">True when this is the last step: after its cost the tame is tried, and
/// <see cref="TamingCommittingEvent"/> is asked first.</param>
public sealed record TamingAdvanceContext(TamingProgress Before, Guid RequestId, TamingToken Token, int StepIndex, string StepKind, ItemSelection? Selection,
    int QuotedQuantity, int QuotedWear, bool WillTryFinalCommit);

/// <summary>What the final tame asks, as <see cref="TamingCommittingEvent"/> shows it to handlers: before the last step's cost,
/// or on a cost-free retry. A copy: changing nothing here changes the attempt.</summary>
/// <param name="Attempt">The attempt.</param>
/// <param name="RequestId">The caller's request id.</param>
/// <param name="Owner">The player taming.</param>
/// <param name="Target">The creature about to be tamed.</param>
/// <param name="Definition">The definition's id.</param>
/// <param name="EarlierCosts">The receipts of the steps already paid.</param>
/// <param name="PendingSelection">The slot the last step would be paid from; null on a retry or when it takes no slot.</param>
/// <param name="PendingQuantity">The items the last step would take; 0 when none.</param>
/// <param name="PendingWear">The durability the last step would take; 0 when none.</param>
/// <param name="CostFreeRetry">True when this is <see cref="Taming.Retry"/> of a deferred tame: nothing is taken.</param>
public sealed record TamingCommitContext(TameAttemptId Attempt, Guid RequestId, PlayerRef Owner, EntityRef Target, string Definition,
    IReadOnlyList<CostReceipt> EarlierCosts, ItemSelection? PendingSelection, int PendingQuantity, int PendingWear, bool CostFreeRetry);

/// <summary>A taming is about to begin: cancel it to refuse (nothing is reserved yet). Posted on the tick thread before
/// anything is reserved, during <see cref="Taming.Begin"/>. A canceled begin answers <c>Canceled</c> and is remembered under
/// its request id. Since 0.2.0-dev.32.</summary>
/// <remarks>Server only, tick thread. The mod's own rules go here (a level, a zone, a cooldown, a permission). Do not call
/// <see cref="Taming.Begin"/> on the same target from the handler: the first attempt to claim the target wins.</remarks>
/// <example><code>
/// Events.On&lt;TamingBeginningEvent&gt;(e =&gt;
/// {
///     if (e.Context.Definition == "my-mod:wolf" &amp;&amp; !IsAllowed(e.Context.Player)) e.Cancel();   // IsAllowed: the mod's own rule
/// });
/// </code></example>
/// <seealso cref="Taming.Begin"/>
[ServerOnly]
public sealed class TamingBeginningEvent : CancelableEvent
{
    internal TamingBeginningEvent(TamingBeginContext context) => Context = context;
    /// <summary>What is asked.</summary>
    public TamingBeginContext Context { get; }
}

/// <summary>A step is about to be paid and applied: cancel it to refuse (nothing is spent, the token stays valid). Posted on
/// the tick thread during <see cref="Taming.Advance"/>, after the token, range, target and threshold checks. The answer to the
/// caller is <c>Canceled</c>. Since 0.2.0-dev.32.</summary>
/// <seealso cref="Taming.Advance"/>
/// <seealso cref="TamingCommittingEvent"/>
[ServerOnly]
public sealed class TamingAdvancingEvent : CancelableEvent
{
    internal TamingAdvancingEvent(TamingAdvanceContext context) => Context = context;
    /// <summary>What is asked.</summary>
    public TamingAdvanceContext Context { get; }
}

/// <summary>The tame is about to be made (before the last step's cost, or a cost-free retry): cancel it to refuse; earlier
/// paid steps stay paid by the definition's refund policy. Nothing more is spent by the last step. Posted on the tick thread
/// after <see cref="TamingAdvancingEvent"/> for the last step, and again on every <see cref="Taming.Retry"/>. Since
/// 0.2.0-dev.32.</summary>
/// <seealso cref="Taming.Retry"/>
[ServerOnly]
public sealed class TamingCommittingEvent : CancelableEvent
{
    internal TamingCommittingEvent(TamingCommitContext context) => Context = context;
    /// <summary>What is asked.</summary>
    public TamingCommitContext Context { get; }
}

// ---- after-events (19e 5.3) ----

/// <summary>Base of the taming after-events: an attempt changed. Subscribe to it for all of them, or to one kind below. Posted
/// on the tick thread after the change settled; not cancelable. A handler may call <see cref="Taming"/> again. Since
/// 0.2.0-dev.32.</summary>
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

    /// <summary>The attempt before the change (null for a start).</summary>
    public TamingProgress? Before { get; }
    /// <summary>The attempt now: after the change, with its state, steps, receipts and refunds.</summary>
    public TamingProgress After { get; }
    /// <summary>The request id that changed it (empty for a timeout, or a failure Kiln saw by itself).</summary>
    public Guid RequestId { get; }
    /// <summary>The receipts of the cost of this change (at a final event, of every spent step).</summary>
    public IReadOnlyList<CostReceipt> Receipts { get; }
    /// <summary>Why, in a word (a request, Expired, BodyGone, OwnerLeft, WorldEnded, DefinitionGone, UncertainCommit...).</summary>
    public string Cause { get; }

    /// <summary>A line for logs: the event's name, the attempt, its state and step count, and the cause.</summary>
    public override string ToString() => $"{GetType().Name} {After.Id} {After.State} {After.CompletedSteps}/{After.TotalSteps} ({Cause})";
}

/// <summary>An attempt started (<see cref="Taming.Begin"/> answered Ok). Since 0.2.0-dev.32.</summary>
public sealed class TamingStartedEvent : TamingChangedEvent
{
    internal TamingStartedEvent(TamingProgress after, Guid requestId) : base(null, after, requestId, [], "Begin") { }
}

/// <summary>A step applied and its cost is spent. Since 0.2.0-dev.32.</summary>
public sealed class TamingStepChangedEvent : TamingChangedEvent
{
    internal TamingStepChangedEvent(TamingProgress before, TamingProgress after, Guid requestId, IReadOnlyList<CostReceipt> receipts)
        : base(before, after, requestId, receipts, "Step") { }
}

/// <summary>Every step is paid, but the tame was refused for now (a bind that may change, such as a full roster): the attempt
/// is <see cref="TamingState.AwaitingCommit"/> and waits for a cost-free <see cref="Taming.Retry"/>. Since 0.2.0-dev.32.</summary>
public sealed class TamingCommitDeferredEvent : TamingChangedEvent
{
    internal TamingCommitDeferredEvent(TamingProgress before, TamingProgress after, Guid requestId, string cause)
        : base(before, after, requestId, [], cause) { }
}

/// <summary>The target was tamed: the one ownership event of a Kiln taming (posted after Kiln.Pets' own
/// <see cref="PetGrantedEvent"/> when the definition binds a pet). Subscribe here to reward or announce a tame. Since
/// 0.2.0-dev.32.</summary>
public sealed class TamedEvent : TamingChangedEvent
{
    internal TamedEvent(TamingProgress before, TamingProgress after, Guid requestId, PetSnapshot? pet) : base(before, after, requestId, after.Spent, "Tamed") =>
        Pet = pet;
    /// <summary>The pet the tame made (<see cref="TamingDefinition.Pet"/>), or null.</summary>
    public PetSnapshot? Pet { get; }
}

/// <summary>The attempt is over with a tame (after <see cref="TamedEvent"/>). The state is <see cref="TamingState.Committed"/>.
/// Since 0.2.0-dev.32.</summary>
public sealed class TamingCompletedEvent : TamingChangedEvent
{
    internal TamingCompletedEvent(TamingProgress before, TamingProgress after, Guid requestId) : base(before, after, requestId, after.Spent, "Completed") { }
}

/// <summary>The attempt was canceled by the player or a mod; its paid steps are as the refund policy says
/// (<see cref="TamingProgress.Refunds"/>). Since 0.2.0-dev.32.</summary>
public sealed class TamingCanceledEvent : TamingChangedEvent
{
    internal TamingCanceledEvent(TamingProgress before, TamingProgress after, Guid requestId, string cause) : base(before, after, requestId, after.Spent, cause) { }
}

/// <summary>The attempt ran out of time: <see cref="TamingDefinition.InactivityTimeout"/> without a step, or
/// <see cref="TamingDefinition.MaxAttemptDuration"/> in all (the cause says which: <c>InactivitySeconds</c> or
/// <c>MaxAttemptSeconds</c>). Since 0.2.0-dev.32.</summary>
public sealed class TamingExpiredEvent : TamingChangedEvent
{
    internal TamingExpiredEvent(TamingProgress before, TamingProgress after, string cause) : base(before, after, Guid.Empty, after.Spent, cause) { }
}

/// <summary>The attempt failed for a reason that is not a request: the target is gone, the owner left, the world ended, the
/// definition went, the final tame was refused for good, or a cost is uncertain (see <see cref="TamingChangedEvent.Cause"/>).
/// Since 0.2.0-dev.32.</summary>
public sealed class TamingFailedEvent : TamingChangedEvent
{
    internal TamingFailedEvent(TamingProgress before, TamingProgress after, Guid requestId, string cause) : base(before, after, requestId, after.Spent, cause) { }
}
