# Taming (Kiln.Taming)

## FeedStep `0.2.25` — Feed: `Count` of a plain-stack item from the slot the player selected (exact, no search).
- `FeedStep(ItemId Item, int Count = 1)`
- `Count { get; init; } -> int` — 1 to 999.
- `Item { get; init; } -> ItemId` — The item (resolved: the items registry or the game's catalog).
- `override Kind { get; } -> string`

## NativeBaitEatenEvent `0.2.25` — An animal finished a bait.
- `Bait { get; } -> EntityId` — The bait it ate (gone now).
- `Counted { get; } -> bool` — True when the bait went with a step (a petting); false when it was eaten without one.
- `Creature { get; } -> EntityId` — The animal.

## NativeStep `0.2.25` — The game's own step(s), counted by the game (a player petting the target while it eats its favourite bait; docs/re/taming.md): the step applies once the target's...
- `NativeStep(int Steps = 1)`
- `override Kind { get; } -> string`
- `Steps { get; init; } -> int` — 1 to 255.

## NativeStepResult `0.2.25` — The result of `NativeTaming.PetStep`.
- `NativeStepResult(bool Ok, string? Reason)`
- `Ok { get; init; } -> bool` — The animal's pet sequence started while it eats its bait.
- `Reason { get; init; } -> string?` — Why not, in words for a log: its first word is `NativeStepResult.Refusal`.
- `Refusal { get; } -> TamingRefusal` — Why not, as a value (`TamingRefusal.NotWild`, `TamingRefusal.NotEating`, ...): `TamingRefusal.None` when...

## NativeTamedEvent `0.2.25` — The game tamed an animal (it became a farm animal with IsTame).
- `Creature { get; } -> EntityId` — The animal (the same entity, now a farm animal).
- `Steps { get; } -> int` — The steps it took.

## NativeTaming `0.2.25` — The game's own taming as mechanism (docs/re/taming.md).
- `static Available { get; } -> bool` — True when the game's taming can be read and driven here.
- `const BaitCapability = "taming.native_bait" -> string` — The capability of `NativeTaming.PlaceBait`: a bait Kiln places that the game senses.
- `const Capability = "taming.native" -> string` — The capability.
- `const ObserveCapability = "taming.native_observe" -> string` — The capability of who tamed an animal (`NativeTamingState.TamedBy`): off until a lab with a player proves where the...
- `static Read(EntityId creature) -> NativeTamingState?` — The animal's taming now; null when it is no wild animal (no DynamicWildlife: a farm animal that never was wild, any...

## NativeTamingCause `0.2.25` — Why the game's taming progress of an animal changed.
enum: Step, Startle, Other

## NativeTamingPhase `0.2.25` — Where a wild animal is in the game's taming.
enum: Wild, PursuingBait, EatingBait, Tamed, Dead

## NativeTamingProgressEvent `0.2.25` — An animal's taming progress changed in the game.
- `After { get; } -> int` — Progress now.
- `Before { get; } -> int` — Progress before.
- `Cause { get; } -> NativeTamingCause` — Why.
- `Creature { get; } -> EntityId` — The animal.
- `Required { get; } -> int` — Steps to the farm transformation.

## NativeTamingState `0.2.25` — One animal's taming as the game has it now.
- `NativeTamingState(EntityId Creature, bool Tameable, int Progress, int Required, NativeTamingPhase Phase, EntityId Bait, Guid BaitTemplate, double SensorRangeMeters, TimeSpan EatDuration)`
- `Bait { get; init; } -> EntityId` — The bait it is after or eating; none otherwise.
- `BaitTemplate { get; init; } -> Guid` — Its favourite bait's template (what `NativeTaming.PlaceBait` places); empty when it has none.
- `Creature { get; init; } -> EntityId` — The animal.
- `EatDuration { get; init; } -> TimeSpan` — How long it eats one bait (a petting must start before it is done).
- `Phase { get; init; } -> NativeTamingPhase` — Where it is.
- `Progress { get; init; } -> int` — Steps so far (the game's byte; it never goes above `Required`).
- `RawState { get; init; } -> int` — The game's raw state byte (wildlife::State), for logs.
- `Required { get; init; } -> int` — Steps to the farm transformation (its template's).
- `SensorRangeMeters { get; init; } -> double` — How far it senses a bait.
- `Tameable { get; init; } -> bool` — Its template allows taming (the Wildlife template's Tameable feature).
- `TamedBy { get; init; } -> PlayerRef?` — Who tamed it.

## TameAttemptId `0.2.25` — An attempt's id: random, never reused.
- `TameAttemptId()`
- `TameAttemptId(Guid Value)`
- `Value { get; init; } -> Guid`

## TamedEvent `0.2.25` — The target was tamed: the one ownership event of a Kiln taming (posted after Kiln.Pets' own `PetGrantedEvent` when the definition binds a pet).
- `Pet { get; } -> PetSnapshot?` — The pet the tame made (`TamingDefinition.Pet`), or null.

## Taming `0.2.25` — Kiln's taming steps: a server-authoritative attempt per target, its steps (feed, weaken, use a tool, the game's own native step) taken in order with exact costs and...
- `static Advance(PlayerRef player, TameAttemptId attempt, TamingRequest request, ItemSelection? selection = null) -> Task<TamingResult>` — Takes the attempt's next step: checks the request's token, the range, the target and the weaken threshold, asks the...
- `static Available { get; } -> bool` — True when attempts can run here (a server).
- `static Begin(PlayerRef player, EntityRef target, string definition, TamingRequest request) -> Task<TamingResult>` — Starts an attempt of `definition` by `player` on `target`: the target is claimed (another attempt on it is `Busy`)...
- `static Cancel(PlayerRef player, TameAttemptId attempt, TamingRequest request) -> Task<TamingResult>` — Ends the attempt, named by its id: a request without a token (`TamingRequest.New()`) cancels from a stale view (a UI...
- `const Capability = "taming.steps" -> string` — The capability of Kiln's taming steps.
- `static Choices(PlayerRef player, TameAttemptId attempt) -> TamingChoicesResult` — The slots of the owner's inventories that can pay the next step, as selections the server read, with the token of...
- `static Define(TamingDefinition definition) -> TamingDefinitionResult` — Registers a taming as the calling mod's, and answers as `Pets.Define` does.
- `static DefineJson(string json) -> IReadOnlyList<TamingDefinitionResult>` — Reads tamings from JSON (one definition, or an array of them; docs/kiln/modding/taming.md) and registers each as...
- `static Definition(string id) -> TamingDefinition?` — The definition registered under `id`, or null.
- `static Definitions { get; } -> IReadOnlyList<TamingDefinition>` — Every registered definition.
- `static Inspect(TameAttemptId attempt) -> TamingProgress?` — The attempt as it is now; null once it is gone (10 minutes of simulation after it closed).
- `static Retry(PlayerRef player, TameAttemptId attempt, TamingRequest request) -> Task<TamingResult>` — The cost-free retry of a deferred tame (`TamingState.AwaitingCommit`): checks again and binds, taking nothing.

## TamingAdvanceContext `0.2.25` — The context of an Advance (19e 5.3).
- `TamingAdvanceContext(TamingProgress Before, Guid RequestId, TamingToken Token, int StepIndex, string StepKind, ItemSelection? Selection, int QuotedQuantity, int QuotedWear, bool WillTryFinalCommit)`
- `Before { get; init; } -> TamingProgress`
- `QuotedQuantity { get; init; } -> int`
- `QuotedWear { get; init; } -> int`
- `RequestId { get; init; } -> Guid`
- `Selection { get; init; } -> ItemSelection?`
- `StepIndex { get; init; } -> int`
- `StepKind { get; init; } -> string`
- `Token { get; init; } -> TamingToken`
- `WillTryFinalCommit { get; init; } -> bool`

## TamingAdvancingEvent `0.2.25` — A step is about to be paid and applied: cancel it to refuse (nothing is spent, the token stays valid).
- `Context { get; } -> TamingAdvanceContext` — What is asked.

## TamingBeginContext `0.2.25` — The context of a Begin (19e 5.3).
- `TamingBeginContext(Guid RequestId, PlayerRef Player, EntityRef Target, string Definition, int TotalSteps)`
- `Definition { get; init; } -> string`
- `Player { get; init; } -> PlayerRef`
- `RequestId { get; init; } -> Guid`
- `Target { get; init; } -> EntityRef`
- `TotalSteps { get; init; } -> int`

## TamingBeginningEvent `0.2.25` — A taming is about to begin: cancel it to refuse (nothing is reserved yet).
- `Context { get; } -> TamingBeginContext` — What is asked.

## TamingCanceledEvent `0.2.25` — The attempt was canceled; its paid steps are as the refund policy says (`TamingProgress.Refunds`).

## TamingChangedEvent `0.2.25` — Base of the taming after-events: an attempt changed.
- `After { get; } -> TamingProgress` — The attempt now.
- `Before { get; } -> TamingProgress?` — The attempt before (null for a start).
- `Cause { get; } -> string` — Why, in a word (a request, Expired, BodyGone, OwnerLeft, WorldEnded, DefinitionGone, UncertainCommit...).
- `Receipts { get; } -> IReadOnlyList<CostReceipt>` — The receipts of the change.
- `RequestId { get; } -> Guid` — The request that changed it (empty for a timeout or a failure Kiln saw).

## TamingChoicesResult `0.2.25` — The choices for the attempt's next step.
- `TamingChoicesResult(bool Ok, string? Reason, TamingToken? Token, IReadOnlyList<TamingItemChoice> Choices)`
- `Choices { get; init; } -> IReadOnlyList<TamingItemChoice>` — The slots that can pay the step.
- `Ok { get; init; } -> bool` — True when the attempt waits for a step (its choices may be none: a weaken or native step).
- `Reason { get; init; } -> string?` — Why not, in words for a log.
- `Refusal { get; } -> TamingRefusal` — Why not, as a value: `TamingRefusal.None` when `TamingChoicesResult.Ok`, else the first word of...
- `Token { get; init; } -> TamingToken?` — The view the choices were read on: a request for one of them echoes it (`TamingRequest.New(choices.Token)`); null...
- `Truncated { get; init; } -> bool` — More slots could pay the step than the 256 `TamingChoicesResult.Choices` lists: these are the first 256 in the...

## TamingCommitContext `0.2.25` — The context of the final tame (19e 5.3): before the last step's cost, or a cost-free retry.
- `TamingCommitContext(TameAttemptId Attempt, Guid RequestId, PlayerRef Owner, EntityRef Target, string Definition, IReadOnlyList<CostReceipt> EarlierCosts, ItemSelection? PendingSelection, int PendingQuantity, int PendingWear, bool CostFreeRetry)`
- `Attempt { get; init; } -> TameAttemptId`
- `CostFreeRetry { get; init; } -> bool`
- `Definition { get; init; } -> string`
- `EarlierCosts { get; init; } -> IReadOnlyList<CostReceipt>`
- `Owner { get; init; } -> PlayerRef`
- `PendingQuantity { get; init; } -> int`
- `PendingSelection { get; init; } -> ItemSelection?`
- `PendingWear { get; init; } -> int`
- `RequestId { get; init; } -> Guid`
- `Target { get; init; } -> EntityRef`

## TamingCommitDeferredEvent `0.2.25` — Every step is paid; the tame waits for a cost-free retry.

## TamingCommittingEvent `0.2.25` — The tame is about to be made (before the last step's cost, or a cost-free retry): cancel it to refuse; earlier paid steps stay paid by the definition's refund policy.
- `Context { get; } -> TamingCommitContext` — What is asked.

## TamingCompletedEvent `0.2.25` — The attempt is over with a tame (after `TamedEvent`).

## TamingDefinition `0.2.25` — A taming, as a mod declares it (19e 5.1).
- `Creatures { get; init; } -> IReadOnlyList<string>` — Which creatures it tames: creatures registry ids (`game:enemy_wildbeast_wolf_earlygame`, a mod's `my-mod:wolf`); a...
- `Id { get; init; } -> string` — The definition's id, unique per mod ("`my-mod:wolf`"; lower case, a mod prefix).
- `InactivityTimeout { get; init; } -> TimeSpan` — How long in simulation without an applied step before the attempt expires (1 to 300 s; JSON: `inactivitySeconds`).
- `MaxAttemptDuration { get; init; } -> TimeSpan` — The longest an attempt lasts (at least the inactivity, at most 3600 s; JSON: `maxAttemptSeconds`).
- `Pet { get; init; } -> string?` — The Kiln.Pets definition (`PetDefinition.Id`) a tame binds: when the tame commits, the owner's character gets a new...
- `RangeMeters { get; init; } -> double` — How close the player must be, 1 to 8 m.
- `Refund { get; init; } -> TamingRefundPolicy` — What happens to paid steps when the attempt ends without a tame.
- `RemoveTarget { get; init; } -> bool` — With `TamingDefinition.Pet`: the tamed wild body is removed once the pet record is made (the pet's own body comes...
- `RequireThresholdUntilCommit { get; init; } -> bool` — The latest weaken threshold must hold until the tame.
- `Steps { get; init; } -> IReadOnlyList<TamingStep>` — The steps (1 to 64).
- `SummonPet { get; init; } -> bool` — With `TamingDefinition.Pet`: the new pet is summoned at once (a body next to its owner, as `Pets.Summon`); a summon...

## TamingDefinitionResult `0.2.25` — The answer of `Taming.Define`, as `PetDefinitionResult` is of `Pets.Define`.
- `TamingDefinitionResult(bool Ok, string? Reason, TamingDefinition? Definition)`
- `Definition { get; init; } -> TamingDefinition?` — The registered definition (the one that stays, when refused).
- `Ok { get; init; } -> bool` — True when the definition is registered (or the same one was already).
- `Reason { get; init; } -> string?` — Why not, in words for a log: its first word is `TamingDefinitionResult.Refusal` (`DefinitionInUse`).
- `Refusal { get; } -> TamingRefusal` — Why not, as a value: `TamingRefusal.None` when `TamingDefinitionResult.Ok`.

## TamingExpiredEvent `0.2.25` — The attempt ran out of time.

## TamingFailedEvent `0.2.25` — The attempt failed.

## TamingItemChoice `0.2.25` — A choice the server offers for a step (19e 5.1): a slot it read, as a selection, with a label.
- `TamingItemChoice(ItemSelection Selection, string Label)`
- `Label { get; init; } -> string`
- `Selection { get; init; } -> ItemSelection`

## TamingProgress `0.2.25` — An attempt as it is now (immutable copy).
- `TamingProgress(TameAttemptId Id, PlayerRef Owner, ulong WorldEpoch, ulong CharacterSession, EntityRef Target, string Definition, TamingState State, int CompletedSteps, int TotalSteps, ulong Revision, Guid StepChallenge, TimeSpan StartedAt, TimeSpan LastProgressAt, TimeSpan ExpiresAt, IReadOnlyList<CostReceipt> Spent)`
- `CancelRequested { get; init; } -> bool` — A cancel was asked while a cost was being paid: it ends when the cost settled.
- `CharacterSession { get; init; } -> ulong`
- `CompletedSteps { get; init; } -> int`
- `Definition { get; init; } -> string`
- `ExpiresAt { get; init; } -> TimeSpan`
- `Id { get; init; } -> TameAttemptId`
- `LastProgressAt { get; init; } -> TimeSpan`
- `Owner { get; init; } -> PlayerRef`
- `Refunds { get; init; } -> IReadOnlyList<TamingRefund>` — The refunds made at the end (empty while it runs, and for a non-refundable definition).
- `Revision { get; init; } -> ulong`
- `Spent { get; init; } -> IReadOnlyList<CostReceipt>`
- `StartedAt { get; init; } -> TimeSpan`
- `State { get; init; } -> TamingState`
- `StepChallenge { get; init; } -> Guid`
- `Target { get; init; } -> EntityRef`
- `Token { get; } -> TamingToken` — The token the next request echoes (`TamingRequest.New`): the challenge, the revision and the steps completed.
- `TotalSteps { get; init; } -> int`
- `WorldEpoch { get; init; } -> ulong`

## TamingRefund `0.2.25` — A refund of a paid step (the definition's `TamingRefundPolicy`).
- `TamingRefund(CostReceipt Paid, CostReceipt? Refund)`
- `Paid { get; init; } -> CostReceipt` — The step's receipt.
- `Refund { get; init; } -> CostReceipt?` — The refund's receipt (null: not given back: the step was not refundable, or nothing was taken).

## TamingRefundPolicy `0.2.25` — What happens to the costs of an attempt that does not end in a tame: the mod's choice (19e O3).
enum: NonRefundable, RefundOnFail, Escrow

## TamingRefusal `0.2.25` — Why a `Taming` call (`TamingResult.Refusal`, `TamingChoicesResult.Refusal`) or a `NativeTaming.PetStep` (`NativeStepResult.Refusal`) was refused: the first word of...
enum: None, Other, NotOwner, OwnerOffline, OwnerLeft, NoCharacter, StaleSession, Busy, StaleStep, RequestConflict, TooManyRequests, StaleSelection, OutOfRange, Threshold, BodyGone, TemplateMismatch, Capacity, DefinitionUnknown, DefinitionInUse, DefinitionGone, WrongState, NativePending, CommitDeferred, AttemptClosed, Expired, Canceled, RefusedByMod, Refused, UncertainCommit, PartialCostCommitted, WorldEnded, NotWild, NotTameable, Tamed, Complete, NotEating, OtherPetSequence, SequenceRefused

## TamingRequest `0.2.25` — A taming request, in the one request shape of Kiln.Pets and Kiln.Taming (as `PetRequest`): its id and what it expects of the view it was made on.
- `TamingRequest(Guid RequestId, TamingToken? Token = null)`
- `static New(TamingToken? token = null) -> TamingRequest` — A new request with a fresh request id, made on `token`'s view.
- `RequestId { get; init; } -> Guid` — New for every intended action, the same for a network retry of it.
- `Token { get; init; } -> TamingToken?` — The attempt's token from the last answer: `Taming.Advance` and `Taming.Retry` need it; `Taming.Cancel` takes it when...

## TamingResult `0.2.25` — The result of a taming call (19e 5.1, 5.4).
- `TamingResult(bool Ok, string? Reason, TamingProgress? Progress, PetSnapshot? Pet, Guid RequestId, bool StepApplied, IReadOnlyList<CostReceipt> Receipts)`
- `Ok { get; init; } -> bool` — See above.
- `Pet { get; init; } -> PetSnapshot?` — The pet the tame made (a definition with `TamingDefinition.Pet`), as it was made; null otherwise.
- `Progress { get; init; } -> TamingProgress?` — The attempt after the call.
- `Reason { get; init; } -> string?` — Why not, in words for a log: its first word is `TamingResult.Refusal` (NotOwner, OwnerOffline, StaleSession, Busy...
- `Receipts { get; init; } -> IReadOnlyList<CostReceipt>` — The receipts of this call's cost (and of every spent step at a terminal result).
- `Refusal { get; } -> TamingRefusal` — Why not, as a value: `TamingRefusal.None` when `TamingResult.Ok`, else the first word of `TamingResult.Reason`...
- `RequestId { get; init; } -> Guid` — The request id the call ran under.
- `StepApplied { get; init; } -> bool` — A step applied (its cost, if any, is spent).

## TamingStartedEvent `0.2.25` — An attempt started.

## TamingState `0.2.25` — Where an attempt is (19e 4.3).
enum: Active, CostPending, AwaitingCommit, Committed, Canceled, Failed, Expired

## TamingStep `0.2.25` — One step of a taming (19e 5.1).
- `TamingStep(TamingStep original)`
- `abstract Kind { get; } -> string` — The step's kind in words ("feed", "weaken", "tool", "native").

## TamingStepChangedEvent `0.2.25` — A step applied (its cost spent).

## TamingToken `0.2.25` — The view of an attempt a request was made on (19e 4.2): the server's challenge (random, rotated by every applied step), the revision and the step.
- `TamingToken(Guid StepChallenge, ulong ExpectedRevision, int StepIndex)`
- `ExpectedRevision { get; init; } -> ulong` — The attempt's revision.
- `StepChallenge { get; init; } -> Guid` — The attempt's challenge.
- `StepIndex { get; init; } -> int` — The step it expects next (the steps completed).

## UseToolStep `0.2.25` — Use a tool: `Wear` durability off the exact gear item the player selected, through the game's own durability debit (`ItemCosts.Wear`).
- `UseToolStep(ItemId Item, int Wear = 1, bool AllowBreak = false)`
- `AllowBreak { get; init; } -> bool` — The wear may break the tool.
- `Item { get; init; } -> ItemId` — The tool's item.
- `override Kind { get; } -> string`
- `Wear { get; init; } -> int` — 0 to 32767 (0: a use that costs nothing).

## WeakenStep `0.2.25` — Weaken: the target's health must be at most `MaxHealthFraction` of its maximum (no cost, no hidden damage).
- `WeakenStep(double MaxHealthFraction)`
- `override Kind { get; } -> string`
- `MaxHealthFraction { get; init; } -> double` — In (0, 1].
