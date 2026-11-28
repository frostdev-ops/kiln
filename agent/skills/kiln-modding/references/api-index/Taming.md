# Taming (Kiln.Taming)

## FeedStep `0.2.27` — Feed: `Count` of a plain-stack item from the slot the player selected (exact, no search).
- `FeedStep(ItemId Item, int Count = 1)`
- `Count { get; init; } -> int` — How many to take: 1 to 999.
- `Item { get; init; } -> ItemId` — The item, as a resolved `ItemId` (a game item of the items registry, or a mod's own item).
- `override Kind { get; } -> string`

## NativeBaitEatenEvent `0.2.27` — An animal finished a bait.
- `Bait { get; } -> EntityId` — The bait it ate (gone now).
- `Counted { get; } -> bool` — True when the bait went with a step (a petting); false when it was eaten without one.
- `Creature { get; } -> EntityId` — The animal.

## NativeStep `0.2.27` — The game's own step(s), counted by the game (a player petting the target while it eats its favourite bait; docs/re/taming.md): the step applies once the target's...
- `NativeStep(int Steps = 1)`
- `override Kind { get; } -> string`
- `Steps { get; init; } -> int` — How many of the game's steps this step waits for: 1 to 255.

## NativeStepResult `0.2.27` — The result of `NativeTaming.PetStep`.
- `NativeStepResult(bool Ok, string? Reason)`
- `Ok { get; init; } -> bool` — The animal's pet sequence started while it eats its bait.
- `Reason { get; init; } -> string?` — Why not, in words for a log: its first word is `NativeStepResult.Refusal`.
- `Refusal { get; } -> TamingRefusal` — Why not, as a value (`TamingRefusal.NotWild`, `TamingRefusal.NotEating`, ...): `TamingRefusal.None` when...

## NativeTamedEvent `0.2.27` — The game tamed an animal (it became a farm animal with IsTame).
- `Creature { get; } -> EntityId` — The animal (the same entity, now a farm animal).
- `Steps { get; } -> int` — The steps it took.

## NativeTaming `0.2.27` — The game's own taming as mechanism: a wild animal eats a bait of its favourite food and is petted while it eats; each petting is one step, and at its required steps...
- `static Available { get; } -> bool` — True when the game's taming can be read and driven here (the capability `NativeTaming.Capability` is on).
- `const BaitCapability = "taming.native_bait" -> string` — The capability of `NativeTaming.PlaceBait`: a bait Kiln places that the game senses.
- `const Capability = "taming.native" -> string` — The capability of reading and driving the game's taming: `"taming.native"`.
- `const ObserveCapability = "taming.native_observe" -> string` — The capability of who tamed an animal (`NativeTamingState.TamedBy`): off until a lab with a player proves where the...
- `static Read(EntityId creature) -> NativeTamingState?` — The animal's taming now; null when it is no wild animal (no DynamicWildlife: a farm animal that never was wild, any...

## NativeTamingCause `0.2.27` — Why the game's taming progress of an animal changed.
enum: Step, Startle, Other

## NativeTamingPhase `0.2.27` — Where a wild animal is in the game's taming.
enum: Wild, PursuingBait, EatingBait, Tamed, Dead

## NativeTamingProgressEvent `0.2.27` — An animal's taming progress changed in the game.
- `After { get; } -> int` — Its progress now, in the game's steps (`NativeTamingProgressEvent.Required` is the number that tames it).
- `Before { get; } -> int` — Its progress before the change, in the game's steps.
- `Cause { get; } -> NativeTamingCause` — Why the progress changed: a step, a startle, or something else.
- `Creature { get; } -> EntityId` — The wild animal whose taming progress changed.
- `Required { get; } -> int` — Steps to the farm transformation.

## NativeTamingState `0.2.27` — One animal's taming as the game has it now.
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

## TameAttemptId `0.2.27` — An attempt's id: random, never reused.
- `TameAttemptId()`
- `TameAttemptId(Guid Value)`
- `Value { get; init; } -> Guid` — The id's GUID.

## TamedEvent `0.2.27` — The target was tamed: the one ownership event of a Kiln taming (posted after Kiln.Pets' own `PetGrantedEvent` when the definition binds a pet).
- `Pet { get; } -> PetSnapshot?` — The pet the tame made (`TamingDefinition.Pet`), or null.

## Taming `0.2.27` — Kiln's taming steps: a server-authoritative attempt per target, its steps (feed, weaken, use a tool, the game's own native step) taken in order with exact costs and...
- `static Advance(PlayerRef player, TameAttemptId attempt, TamingRequest request, ItemSelection? selection = null) -> Task<TamingResult>` — Takes the attempt's next step: checks the request's token, the range, the target and the weaken threshold, asks the...
- `static Available { get; } -> bool` — True when attempts can run here: the capability `Taming.Capability` is on, which is while a server world runs.
- `static Begin(PlayerRef player, EntityRef target, string definition, TamingRequest request) -> Task<TamingResult>` — Starts an attempt of `definition` by `player` on `target`: the target is claimed (another attempt on it is `Busy`)...
- `static Cancel(PlayerRef player, TameAttemptId attempt, TamingRequest request) -> Task<TamingResult>` — Ends the attempt, named by its id: a request without a token (`TamingRequest.New()`) cancels from a stale view (a UI...
- `const Capability = "taming.steps" -> string` — The capability of Kiln's taming steps: `"taming.steps"`.
- `static Choices(PlayerRef player, TameAttemptId attempt) -> TamingChoicesResult` — The slots of the owner's inventories that can pay the next step, as selections the server read, with the token of...
- `static Define(TamingDefinition definition) -> TamingDefinitionResult` — Registers a taming as the calling mod's, and answers as `Pets.Define` does.
- `static DefineJson(string json) -> IReadOnlyList<TamingDefinitionResult>` — Reads tamings from JSON (one definition, or an array of them; docs/kiln/modding/taming.md) and registers each as...
- `static Definition(string id) -> TamingDefinition?` — The definition registered under `id`, or null.
- `static Definitions { get; } -> IReadOnlyList<TamingDefinition>` — Every registered definition, of every mod, in no particular order.
- `static Inspect(TameAttemptId attempt) -> TamingProgress?` — The attempt as it is now; null once it is gone (10 minutes of simulation after it closed).
- `static Retry(PlayerRef player, TameAttemptId attempt, TamingRequest request) -> Task<TamingResult>` — The cost-free retry of a deferred tame (`TamingState.AwaitingCommit`): checks again and binds, taking nothing.

## TamingAdvanceContext `0.2.27` — What `Taming.Advance` asks, as `TamingAdvancingEvent` shows it to handlers: the step, what it would cost and whether it would try the final tame.
- `TamingAdvanceContext(TamingProgress Before, Guid RequestId, TamingToken Token, int StepIndex, string StepKind, ItemSelection? Selection, int QuotedQuantity, int QuotedWear, bool WillTryFinalCommit)`
- `Before { get; init; } -> TamingProgress` — The attempt before the step.
- `QuotedQuantity { get; init; } -> int` — The items the step would take (a feed step); 0 otherwise.
- `QuotedWear { get; init; } -> int` — The durability the step would take (a tool step); 0 otherwise.
- `RequestId { get; init; } -> Guid` — The caller's request id.
- `Selection { get; init; } -> ItemSelection?` — The slot the player selected; null for a weaken or native step.
- `StepIndex { get; init; } -> int` — The index of the step about to be taken.
- `StepKind { get; init; } -> string` — The step's `TamingStep.Kind`.
- `Token { get; init; } -> TamingToken` — The caller's token.
- `WillTryFinalCommit { get; init; } -> bool` — True when this is the last step: after its cost the tame is tried, and `TamingCommittingEvent` is asked first.

## TamingAdvancingEvent `0.2.27` — A step is about to be paid and applied: cancel it to refuse (nothing is spent, the token stays valid).
- `Context { get; } -> TamingAdvanceContext` — What is asked.

## TamingBeginContext `0.2.27` — What `Taming.Begin` asks, as `TamingBeginningEvent` shows it to handlers.
- `TamingBeginContext(Guid RequestId, PlayerRef Player, EntityRef Target, string Definition, int TotalSteps)`
- `Definition { get; init; } -> string` — The definition's id.
- `Player { get; init; } -> PlayerRef` — The player who asks.
- `RequestId { get; init; } -> Guid` — The caller's request id.
- `Target { get; init; } -> EntityRef` — The creature to tame.
- `TotalSteps { get; init; } -> int` — How many steps the definition has.

## TamingBeginningEvent `0.2.27` — A taming is about to begin: cancel it to refuse (nothing is reserved yet).
- `Context { get; } -> TamingBeginContext` — What is asked.

## TamingCanceledEvent `0.2.27` — The attempt was canceled by the player or a mod; its paid steps are as the refund policy says (`TamingProgress.Refunds`).

## TamingChangedEvent `0.2.27` — Base of the taming after-events: an attempt changed.
- `After { get; } -> TamingProgress` — The attempt now: after the change, with its state, steps, receipts and refunds.
- `Before { get; } -> TamingProgress?` — The attempt before the change (null for a start).
- `Cause { get; } -> string` — Why, in a word (a request, Expired, BodyGone, OwnerLeft, WorldEnded, DefinitionGone, UncertainCommit...).
- `Receipts { get; } -> IReadOnlyList<CostReceipt>` — The receipts of the cost of this change (at a final event, of every spent step).
- `RequestId { get; } -> Guid` — The request id that changed it (empty for a timeout, or a failure Kiln saw by itself).

## TamingChoicesResult `0.2.27` — The answer of `Taming.Choices`: the slots that can pay the attempt's next step, with the token of the view they were read on.
- `TamingChoicesResult(bool Ok, string? Reason, TamingToken? Token, IReadOnlyList<TamingItemChoice> Choices)`
- `Choices { get; init; } -> IReadOnlyList<TamingItemChoice>` — The slots that can pay the step, in the order the owner's inventories list them: the first 256...
- `Ok { get; init; } -> bool` — True when the list is the real one (it may be empty: weaken and native steps take no slot, and a feed or tool step...
- `Reason { get; init; } -> string?` — Why not, in words for a log: `AttemptClosed` (no such attempt of this player) or `WrongState` (the attempt is not...
- `Refusal { get; } -> TamingRefusal` — Why not, as a value: `TamingRefusal.None` when `TamingChoicesResult.Ok`, else the first word of...
- `Token { get; init; } -> TamingToken?` — The view the choices were read on: a request for one of them echoes it (`TamingRequest.New(choices.Token)`); null...
- `Truncated { get; init; } -> bool` — More slots could pay the step than the 256 `TamingChoicesResult.Choices` lists: these are the first 256 in the...

## TamingCommitContext `0.2.27` — What the final tame asks, as `TamingCommittingEvent` shows it to handlers: before the last step's cost, or on a cost-free retry.
- `TamingCommitContext(TameAttemptId Attempt, Guid RequestId, PlayerRef Owner, EntityRef Target, string Definition, IReadOnlyList<CostReceipt> EarlierCosts, ItemSelection? PendingSelection, int PendingQuantity, int PendingWear, bool CostFreeRetry)`
- `Attempt { get; init; } -> TameAttemptId` — The attempt.
- `CostFreeRetry { get; init; } -> bool` — True when this is `Taming.Retry` of a deferred tame: nothing is taken.
- `Definition { get; init; } -> string` — The definition's id.
- `EarlierCosts { get; init; } -> IReadOnlyList<CostReceipt>` — The receipts of the steps already paid.
- `Owner { get; init; } -> PlayerRef` — The player taming.
- `PendingQuantity { get; init; } -> int` — The items the last step would take; 0 when none.
- `PendingSelection { get; init; } -> ItemSelection?` — The slot the last step would be paid from; null on a retry or when it takes no slot.
- `PendingWear { get; init; } -> int` — The durability the last step would take; 0 when none.
- `RequestId { get; init; } -> Guid` — The caller's request id.
- `Target { get; init; } -> EntityRef` — The creature about to be tamed.

## TamingCommitDeferredEvent `0.2.27` — Every step is paid, but the tame was refused for now (a bind that may change, such as a full roster): the attempt is `TamingState.AwaitingCommit` and waits for a...

## TamingCommittingEvent `0.2.27` — The tame is about to be made (before the last step's cost, or a cost-free retry): cancel it to refuse; earlier paid steps stay paid by the definition's refund policy.
- `Context { get; } -> TamingCommitContext` — What is asked.

## TamingCompletedEvent `0.2.27` — The attempt is over with a tame (after `TamedEvent`).

## TamingDefinition `0.2.27` — A taming, as a mod declares it: which creatures it tames, the steps a player takes in order (the last one tames), the timeouts, what happens to paid steps when the...
- `Creatures { get; init; } -> IReadOnlyList<string>` — Which creatures it tames: creatures registry ids (`game:enemy_wildbeast_wolf_earlygame`, a mod's `my-mod:wolf`); a...
- `Id { get; init; } -> string` — The definition's id: `<mod id>:<name>` (`my-mod:wolf`), lower case, at most 128 characters, with the mod's own id as...
- `InactivityTimeout { get; init; } -> TimeSpan` — How long in simulation time without an applied step before the attempt expires: 1 to 300 s.
- `MaxAttemptDuration { get; init; } -> TimeSpan` — The longest an attempt lasts in simulation time: at least `TamingDefinition.InactivityTimeout`, at most 3600 s.
- `Pet { get; init; } -> string?` — The Kiln.Pets definition (`PetDefinition.Id`) a tame binds: when the tame commits, the owner's character gets a new...
- `RangeMeters { get; init; } -> double` — How close the player must be to the target for every step, in metres: 1 to 8.
- `Refund { get; init; } -> TamingRefundPolicy` — What happens to paid steps when the attempt ends without a tame.
- `RemoveTarget { get; init; } -> bool` — With `TamingDefinition.Pet`: the tamed wild body is removed once the pet record is made (the pet's own body comes...
- `RequireThresholdUntilCommit { get; init; } -> bool` — True (the default): the latest `WeakenStep` threshold holds for every later step and for the final tame, so a target...
- `Steps { get; init; } -> IReadOnlyList<TamingStep>` — The steps, in the order a player takes them: 1 to 64.
- `SummonPet { get; init; } -> bool` — With `TamingDefinition.Pet`: the new pet is summoned at once (a body next to its owner, as `Pets.Summon`); a summon...

## TamingDefinitionResult `0.2.27` — The answer of `Taming.Define`, as `PetDefinitionResult` is of `Pets.Define`.
- `TamingDefinitionResult(bool Ok, string? Reason, TamingDefinition? Definition)`
- `Definition { get; init; } -> TamingDefinition?` — The registered definition (the one that stays, when refused).
- `Ok { get; init; } -> bool` — True when the definition is registered (or the same one was already).
- `Reason { get; init; } -> string?` — Why not, in words for a log: its first word is `TamingDefinitionResult.Refusal` (`DefinitionInUse`).
- `Refusal { get; } -> TamingRefusal` — Why not, as a value: `TamingRefusal.None` when `TamingDefinitionResult.Ok`.

## TamingExpiredEvent `0.2.27` — The attempt ran out of time: `TamingDefinition.InactivityTimeout` without a step, or `TamingDefinition.MaxAttemptDuration` in all (the cause says which...

## TamingFailedEvent `0.2.27` — The attempt failed for a reason that is not a request: the target is gone, the owner left, the world ended, the definition went, the final tame was refused for good...

## TamingItemChoice `0.2.27` — A slot the server offers to pay a step: an inventory slot it read, as an `ItemSelection`, with a label for a menu.
- `TamingItemChoice(ItemSelection Selection, string Label)`
- `Label { get; init; } -> string` — A short text for a menu: the slot's name and count (at most 64 characters).
- `Selection { get; init; } -> ItemSelection` — The slot, as read: its inventory owner, slot, item and count.

## TamingProgress `0.2.27` — An attempt as it is now: an immutable copy, never updated.
- `TamingProgress(TameAttemptId Id, PlayerRef Owner, ulong WorldEpoch, ulong CharacterSession, EntityRef Target, string Definition, TamingState State, int CompletedSteps, int TotalSteps, ulong Revision, Guid StepChallenge, TimeSpan StartedAt, TimeSpan LastProgressAt, TimeSpan ExpiresAt, IReadOnlyList<CostReceipt> Spent)`
- `CancelRequested { get; init; } -> bool` — A cancel was asked while a cost was being paid: it ends when the cost settled.
- `CharacterSession { get; init; } -> ulong` — The owner's join the attempt belongs to.
- `CompletedSteps { get; init; } -> int` — Steps applied so far; also the index of the next step.
- `Definition { get; init; } -> string` — The taming definition's id (`TamingDefinition.Id`).
- `ExpiresAt { get; init; } -> TimeSpan` — When the attempt expires if nothing changes: the earlier of the last progress plus...
- `Id { get; init; } -> TameAttemptId` — The attempt's id.
- `LastProgressAt { get; init; } -> TimeSpan` — When a step last applied (the start, before the first one).
- `Owner { get; init; } -> PlayerRef` — The player who began it.
- `Refunds { get; init; } -> IReadOnlyList<TamingRefund>` — The refunds made at the end (empty while it runs, and for a non-refundable definition).
- `Revision { get; init; } -> ulong` — Starts at 1 and goes up by one each time a step applies; part of the next `TamingProgress.Token`.
- `Spent { get; init; } -> IReadOnlyList<CostReceipt>` — The receipts of the steps paid so far.
- `StartedAt { get; init; } -> TimeSpan` — When the attempt began.
- `State { get; init; } -> TamingState` — Where the attempt is.
- `StepChallenge { get; init; } -> Guid` — The server's challenge for the current step; part of the next `TamingProgress.Token`.
- `Target { get; init; } -> EntityRef` — The creature being tamed.
- `Token { get; } -> TamingToken` — The token the next request echoes (`TamingRequest.New`): the challenge, the revision and the steps completed.
- `TotalSteps { get; init; } -> int` — The definition's number of steps.
- `WorldEpoch { get; init; } -> ulong` — The server world session the attempt belongs to.

## TamingRefund `0.2.27` — A refund of a paid step (the definition's `TamingRefundPolicy`), pairing the step's receipt with the receipt of what was given back.
- `TamingRefund(CostReceipt Paid, CostReceipt? Refund)`
- `Paid { get; init; } -> CostReceipt` — The receipt of the step's cost, as it was taken.
- `Refund { get; init; } -> CostReceipt?` — The refund's receipt (`ItemCosts.Refund`); null when nothing was given back: the step was not refundable, or nothing...

## TamingRefundPolicy `0.2.27` — What happens to the costs of an attempt that does not end in a tame: the mod's choice, set per definition (`TamingDefinition.Refund`).
enum: NonRefundable, RefundOnFail, Escrow

## TamingRefusal `0.2.27` — Why a `Taming` call (`TamingResult.Refusal`, `TamingChoicesResult.Refusal`) or a `NativeTaming.PetStep` (`NativeStepResult.Refusal`) was refused: the first word of...
enum: None, Other, NotOwner, OwnerOffline, OwnerLeft, NoCharacter, StaleSession, Busy, StaleStep, RequestConflict, TooManyRequests, StaleSelection, OutOfRange, Threshold, BodyGone, TemplateMismatch, Capacity, DefinitionUnknown, DefinitionInUse, DefinitionGone, WrongState, NativePending, CommitDeferred, AttemptClosed, Expired, Canceled, RefusedByMod, Refused, UncertainCommit, PartialCostCommitted, WorldEnded, NotWild, NotTameable, Tamed, Complete, NotEating, OtherPetSequence, SequenceRefused

## TamingRequest `0.2.27` — A taming request, in the one request shape of Kiln.Pets and Kiln.Taming (as `PetRequest`): its id and what it expects of the view it was made on.
- `TamingRequest(Guid RequestId, TamingToken? Token = null)`
- `static New(TamingToken? token = null) -> TamingRequest` — A new request with a fresh request id, made on `token`'s view.
- `RequestId { get; init; } -> Guid` — New for every intended action, the same for a network retry of it; not empty.
- `Token { get; init; } -> TamingToken?` — The attempt's token from the last answer: `Taming.Advance` and `Taming.Retry` need it; `Taming.Cancel` takes it when...

## TamingResult `0.2.27` — The answer of `Taming.Begin`, `Taming.Advance`, `Taming.Retry` and `Taming.Cancel`.
- `TamingResult(bool Ok, string? Reason, TamingProgress? Progress, PetSnapshot? Pet, Guid RequestId, bool StepApplied, IReadOnlyList<CostReceipt> Receipts)`
- `Ok { get; init; } -> bool` — True: the action did what it asked.
- `Pet { get; init; } -> PetSnapshot?` — The pet the tame made (a definition with `TamingDefinition.Pet`), as it was made; null otherwise.
- `Progress { get; init; } -> TamingProgress?` — The attempt after the call; null only when no such attempt exists.
- `Reason { get; init; } -> string?` — Why not, in words for a log, its first word `TamingResult.Refusal`: `NotOwner`, `OwnerOffline`, `StaleSession`...
- `Receipts { get; init; } -> IReadOnlyList<CostReceipt>` — The receipts of this call's cost (and of every spent step at a final result).
- `Refusal { get; } -> TamingRefusal` — Why not, as a value: `TamingRefusal.None` when `TamingResult.Ok`, else the first word of `TamingResult.Reason`...
- `RequestId { get; init; } -> Guid` — The request id the call ran under (the request's).
- `StepApplied { get; init; } -> bool` — True when a step applied, so its cost, if any, is spent.

## TamingStartedEvent `0.2.27` — An attempt started (`Taming.Begin` answered Ok).

## TamingState `0.2.27` — Where an attempt is (`TamingProgress.State`).
enum: Active, CostPending, AwaitingCommit, Committed, Canceled, Failed, Expired

## TamingStep `0.2.27` — One step of a taming: the base of `FeedStep`, `WeakenStep`, `UseToolStep` and `NativeStep` (no other step kind can be made).
- `TamingStep(TamingStep original)`
- `abstract Kind { get; } -> string` — The step's kind in words: `"feed"`, `"weaken"`, `"tool"` or `"native"`.

## TamingStepChangedEvent `0.2.27` — A step applied and its cost is spent.

## TamingToken `0.2.27` — The view of an attempt a request was made on (19e 4.2): the server's challenge (random, rotated by every applied step), the revision and the step index the server...
- `TamingToken(Guid StepChallenge, ulong ExpectedRevision, int StepIndex)`
- `ExpectedRevision { get; init; } -> ulong` — The attempt's revision the caller saw (`TamingProgress.Revision`).
- `StepChallenge { get; init; } -> Guid` — The server's random challenge for the current step (`TamingProgress.StepChallenge`); it changes every time a step...
- `StepIndex { get; init; } -> int` — The number of steps the caller saw done (`TamingProgress.CompletedSteps`): the index of the step it asks to take.

## UseToolStep `0.2.27` — Use a tool: `Wear` durability off the exact gear item the player selected, through the game's own durability debit (`ItemCosts.Wear`).
- `UseToolStep(ItemId Item, int Wear = 1, bool AllowBreak = false)`
- `AllowBreak { get; init; } -> bool` — True: the wear may break the tool.
- `Item { get; init; } -> ItemId` — The tool's item (a gear item: the selection must hold one).
- `override Kind { get; } -> string`
- `Wear { get; init; } -> int` — Durability to take, 0 to 32767 (0: a use that costs nothing).

## WeakenStep `0.2.27` — Weaken: the target's health must be at most `MaxHealthFraction` of its maximum (no cost, no hidden damage).
- `WeakenStep(double MaxHealthFraction)`
- `override Kind { get; } -> string`
- `MaxHealthFraction { get; init; } -> double` — The most health the target may have, as a share of its maximum: above 0, at most 1 (0.4 = at 40 % health or less).
