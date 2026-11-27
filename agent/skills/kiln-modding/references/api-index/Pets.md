# Pets (Kiln.Pets)

## PetBodyLostEvent `0.2.26` — A pet's body was gone without Kiln retiring it; the pet is down (never rebuilt by itself).

## PetBodyRetiredEvent `0.2.26` — A pet's body was removed from the world (dismiss, logout, release, down).

## PetBodyViewKey `0.2.26` — A pet body as the network names it: informational (to tell whether a pet has a body, and which one), never an entity to resolve or act on from a client.
- `PetBodyViewKey(uint NetworkEntityId, ulong BodyGeneration)`
- `BodyGeneration { get; init; } -> ulong` — The body's generation.
- `NetworkEntityId { get; init; } -> uint` — The server's entity id of the body.

## PetChange `0.2.26` — One change of a pet: before and after, the request behind it and when it happened.
- `After { get; init; } -> PetSnapshot` — The pet after.
- `Before { get; init; } -> PetSnapshot` — The pet before.
- `Cause { get; init; } -> PetChangeCause` — Why the pet changed: a request, a lost body, a lethal hit, a logout, a revive channel's own course, ...
- `EventId { get; init; } -> Guid` — A GUID that is new for every change: it names this change in logs and in anything the mod keeps about it.
- `ObservedStep { get; init; } -> ulong` — The server step it was observed at (a later delivery keeps it).
- `RequestId { get; init; } -> Guid` — The id of the request behind it (`PetRequest.RequestId`); empty when no request caused it.

## PetChangeCause `0.2.26` — Why a pet changed.
enum: Request, BodyLost, NativeLethal, ProtectionLost, Logout, WorldEnd, Login, Stats, Control, Channel, Unload

## PetChangeEvent `0.2.26` — The base of every pet change event: subscribe to it for all of them, or to one kind below.
- `Change { get; } -> PetChange` — The change: before, after, the request, the step.
- `Owner { get; } -> CharacterKey` — The character that owns the pet.
- `Pet { get; } -> PetId` — The pet that changed: its id (`PetChangeEvent.Change` holds the whole before and after).

## PetCommandChangedEvent `0.2.26` — A pet's order or target changed.

## PetCommit `0.2.26` — The save that holds a pet's state: what was serialized and committed.
- `BundleHash { get; init; } -> string` — The sha256 of the committed record bundle (hex).
- `StateRevision { get; init; } -> ulong` — The pet's `PetSnapshot.StateRevision` in the committed bytes.
- `WorldEpoch { get; init; } -> ulong` — The save session it belongs to.
- `WorldRevision { get; init; } -> uint` — The world file's revision (the mod data generation).
- `WorldUnix { get; init; } -> uint` — The world file's time (Unix seconds); 0 when unknown.

## PetControlChangedEvent `0.2.26` — One of a pet's leases on its body changed (the broker's states before and after).
- `After { get; } -> ControlLeaseSnapshot` — The lease now.
- `Before { get; } -> ControlLeaseSnapshot?` — The lease before (null: first seen).
- `Owner { get; } -> CharacterKey` — Its owner.
- `Pet { get; } -> PetId` — The pet.

## PetCostDefinition `0.2.26` — A cost of so many items of one kind.
- `PetCostDefinition(ItemId Item, int Count)`
- `Count { get; init; } -> int` — 1..999.
- `Item { get; init; } -> ItemId` — A plain stack item (not gear).

## PetDefinition `0.2.26` — A kind of pet, as a mod defines it (`Pets.Define`, or a `PetBlock` through Kiln.Blocks: both make this record).
- `Creature { get; init; } -> string` — The creature the pet's body is, by its Kiln id: a game creature of the `creatures` registry...
- `Downed { get; init; } -> PetDownedPolicy` — What it becomes when it goes down.
- `Hud { get; init; } -> PetHudPolicy` — What the owner's HUD shows of it.
- `Id { get; init; } -> string` — The definition's Kiln id, `<mod id>:<name>` (lower case letters, digits, '-', '_', '.').
- `Level { get; init; } -> PetLevelPolicy` — Its level and size.
- `Limits { get; init; } -> PetLimits` — How many there may be (blocks/*.json: `limits`).
- `Movement { get; init; } -> PetMovementPolicy` — How it follows.
- `Name { get; init; } -> string?` — The name owners read (1 to 64 UTF-8 bytes), as a `PetBlock`'s `Name` is; null: the creature's own.
- `Revive { get; init; } -> PetRevivePolicy` — How it is revived.
- `Targets { get; init; } -> PetTargetPolicy` — Who it may harm and who may harm it.

## PetDefinitionChangedEvent `0.2.26` — A pet definition was registered or replaced (`Pets.Define`).
- `After { get; } -> PetDefinition` — The definition now.
- `Before { get; } -> PetDefinition?` — The definition it replaced; null for a new one.

## PetDefinitionResult `0.2.26` — The answer of `Pets.Define`.
- `PetDefinitionResult(bool Ok, string? Reason, PetDefinition? Definition)`
- `Definition { get; init; } -> PetDefinition?` — The registered definition.
- `Ok { get; init; } -> bool` — True when the definition is registered (or the same one was already).
- `Reason { get; init; } -> string?` — Why not, in words for a log: its first word is `PetDefinitionResult.Refusal` (`DefinitionInUse`: another one under...
- `Refusal { get; } -> PetRefusal` — Why not, as a value: `PetRefusal.None` when `PetDefinitionResult.Ok`, else the first word of...

## PetDismissedEvent `0.2.26` — A pet was dismissed.

## PetDownedEvent `0.2.26` — A pet went down: its combat body is gone (the event comes after that), what it became is the definition's (or `PetDowningEvent`'s) choice.

## PetDownedOutcome `0.2.26` — What a pet becomes when it goes down.
enum: RecoveryAnchor, Absent, Release

## PetDownedPolicy `0.2.26` — What a downed pet is.
- `Outcome { get; init; } -> PetDownedOutcome` — Default `PetDownedOutcome.RecoveryAnchor`.

## PetDowningEvent `0.2.26` — A pet is going down: its combat body is about to be retired.
- `Cause { get; } -> PetChangeCause` — Why it goes down (a lethal hit, a lost body, lost protection).
- `Outcome { get; set; } -> PetDownedOutcome` — What it becomes: a recovery anchor where it fell, absent, or released.
- `Pet { get; } -> PetSnapshot` — The pet before it goes down.

## PetGrantedEvent `0.2.26` — A character was given a new pet (`Pets.Grant`).

## PetHudOptions `0.2.26` — Where and how the built-in pet bar draws, on this game only (never a server state).
- `PetHudOptions(bool Enabled = true, bool RecoveryMarkers = true, double AnchorX = 0.03, double AnchorY = 0.72, double WidthPixels = 220)`
- `AnchorX { get; init; } -> double` — The bar's left edge, 0..1 of the screen width.
- `AnchorY { get; init; } -> double` — The bar's top edge, 0..1 of the screen height.
- `Enabled { get; init; } -> bool` — The bar is drawn.
- `RecoveryMarkers { get; init; } -> bool` — Recovery anchors are marked in the world.
- `WidthPixels { get; init; } -> double` — The bar's width at UI scale 1, 80..600.

## PetHudOptionsChangedEvent `0.2.26` — The built-in bar's options changed (`PetsClient.Configure`).
- `Options { get; } -> PetHudOptions` — The options now.

## PetHudPolicy `0.2.26` — What the owner's built-in HUD shows of pets of this definition (each player's own game may still hide the whole bar, `PetHudOptions`; a mod may draw its own from...
- `Bar { get; init; } -> bool` — A row in the owner's pet bar.
- `RecoveryMarker { get; init; } -> bool` — The recovery anchor's marker in the world.

## PetId `0.2.26` — A pet's id: random, kept in its owner's record for ever, never an entity id.
- `PetId()`
- `PetId(Guid Value)`
- `IsNone { get; } -> bool` — True for `PetId.None`.
- `static None { get; } -> PetId` — No pet.
- `Value { get; init; } -> Guid` — The id; `Guid.Empty` is none.

## PetLevelMode `0.2.26` — Where a pet's level comes from.
enum: Fixed, OwnerAtTame, OwnerAtSummon

## PetLevelPolicy `0.2.26` — A pet's level and size.
- `Level { get; init; } -> int` — The level for `PetLevelMode.Fixed`: 1 up to the creature's highest level (at most 255).
- `Mode { get; init; } -> PetLevelMode` — Where the level comes from.
- `Scale { get; init; } -> double` — The body's uniform scale, 0.25..4 (1 as made).

## PetLife `0.2.26` — Whether a pet is alive, down or gone.
enum: Ready, Downed, Released

## PetLimits `0.2.26` — How many pets of a definition there may be: the definition's limits.
- `LivePerCharacter { get; init; } -> int` — Live bodies of one character (of every definition) while summoning a pet of this definition, 1..16.
- `LivePerWorld { get; init; } -> int` — Live bodies of this definition in the world, 1..256.
- `RetainedPerCharacter { get; init; } -> int` — Records of this definition one character keeps (released ones not counted), 1..128.

## PetMovementMode `0.2.26` — How a pet's body moves while it follows.
enum: QueryInput, EngineGuard

## PetMovementPolicy `0.2.26` — Follow distances, catch-up and their limits: the mod's choice for its pets.
- `AllowCatchUp { get; init; } -> bool` — Whether a far or stuck pet is put next to its owner.
- `CatchUpDistance { get; init; } -> double` — Farther than this many metres (10..160) the pet is put next to its owner on safe ground, when...
- `FollowDistance { get; init; } -> double` — Metres from the owner where the pet stops, 1..10.
- `GroundSearchMeters { get; init; } -> double` — How far, in metres (0.5..8), Kiln looks up and down for ground around a catch-up spot.
- `Mode { get; init; } -> PetMovementMode` — Who walks the body.
- `RepathInterval { get; init; } -> TimeSpan` — How often a new route is asked while the owner moves, 0.1..5 s (JSON: `repathSeconds`).
- `RunDistance { get; init; } -> double` — Farther than this many metres the pet runs instead of walking: at least `PetMovementPolicy.FollowDistance`, at most 30.
- `StuckAfter { get; init; } -> TimeSpan` — How long without progress (1..30 s) before a stuck pet catches up, when `PetMovementPolicy.AllowCatchUp` (JSON...

## PetOrder `0.2.26` — What a pet's owner asked of it.
enum: Follow, Stay, Attack

## PetOwnerView `0.2.26` — An owner's whole view of their pets, as one snapshot: what `PetsClient.Snapshot` holds.
- `PetOwnerView(ulong WorldEpoch, ulong CharacterSession, ulong Sequence, IReadOnlyList<PetViewEntry> Entries)`
- `CharacterSession { get; init; } -> ulong` — The owner's join (a fresh random token each time).
- `Entries { get; init; } -> IReadOnlyList<PetViewEntry>` — The pets (at most 128).
- `Sequence { get; init; } -> ulong` — Strictly increasing within the session.
- `WorldEpoch { get; init; } -> ulong` — The server world session.

## PetOwnerViewChangedEvent `0.2.26` — This game got a new view of its owner's pets.
- `View { get; } -> PetOwnerView` — The new view.

## PetPresence `0.2.26` — Where a pet is in the world.
enum: Absent, Live, RecoveryAnchor, Retiring

## PetProtectionLostEvent `0.2.26` — Kiln could no longer protect a pet's body (a capability went away) and retired it.

## PetPvpMode `0.2.26` — Who players may fight through pets.
enum: Off, ByModRule

## PetRecordCommittedEvent `0.2.26` — A save committed a pet's state: its `PetSnapshot.DurableRevision` moved to the revision that save holds.
- `CapturedStateRevision { get; } -> ulong` — The revision the committed bytes hold.
- `Commit { get; } -> PetCommit` — The save.
- `Owner { get; } -> CharacterKey` — Its owner.
- `Pet { get; } -> PetId` — The pet.
- `PreviousDurableRevision { get; } -> ulong` — The durable revision before.

## PetRecoveryAnchorChangedEvent `0.2.26` — A downed pet's recovery anchor appeared, moved or was hidden.

## PetRefusal `0.2.26` — Why a `Pets` request (`PetResult.Refusal`) or a definition (`PetDefinitionResult.Refusal`) was refused: the first word of its `Reason`, as a value.
enum: None, Other, Busy, StaleRevision, RequestConflict, NotOwner, OwnerOffline, OwnerLeft, NoOwner, NoCharacter, NotFound, AlreadyReleased, AlreadyCompleted, AlreadyEnded, Capacity, LevelUnavailable, DefinitionUnknown, DefinitionUnloaded, DefinitionInUse, CreatureUnknown, CreatureUnsupported, CreatureAmbiguous, CreaturesUnavailable, RecordsUnavailable, RecordsUnreadable, WorldNotRunning, WorldEnded, SpawnUnavailable, SpawnRefused, NoSafeGround, BodyNotReady, BodyRefused, BodyDowned, BodyGone, ControlUnavailable, ControlRefused, ControlLost, ControlFailed, ProtectionUnavailable, ProtectionRefused, ProtectionLost, RetireFailed, NotSummoned, TargetProtected, TargetGone, NotDowned, RecoveryNotSummoned, OutOfRange, ReviveNotAllowed, RefusedByMod, CostUnavailable, CanceledNoCost, CanceledCostCommitted, ReviveFailedCostCommitted, UncertainCommit, NoChannel, Unsupported

## PetReleasedEvent `0.2.26` — A pet was released: its record is a tombstone.

## PetRequest `0.2.26` — A request that changes a pet, in the one request shape of Kiln.Pets and Kiln.Taming (`TamingRequest`): its request id makes a retry the same request (its first answer...
- `PetRequest(Guid RequestId, ulong ExpectedStateRevision = 0)`
- `ExpectedStateRevision { get; init; } -> ulong` — 0: any; otherwise the pet's `PetSnapshot.StateRevision` the request was made on (`StaleRevision` when it moved on).
- `static New(ulong expectedStateRevision = 0) -> PetRequest` — A new request with a fresh request id.
- `RequestId { get; init; } -> Guid` — Unique per request; the same id with another request is `RequestConflict`.

## PetResult `0.2.26` — The answer to a request: the outcome and the pet after it.
- `PetResult(bool Ok, string? Reason, PetSnapshot? Pet, Guid RequestId, IReadOnlyList<CostReceipt> Costs)`
- `Costs { get; init; } -> IReadOnlyList<CostReceipt>` — The receipts (Kiln.Items' `CostReceipt`) of costs that were taken or may have been (committed or uncertain), also on...
- `Ok { get; init; } -> bool` — True when the request did what it asked (or found it done: an idempotent repeat).
- `Pet { get; init; } -> PetSnapshot?` — The pet after it (also after a refusal, when Kiln knows it).
- `Reason { get; init; } -> string?` — Why not, in words for a log: its first word is `PetResult.Refusal` (`Busy`, `StaleRevision`, `RequestConflict`...
- `Refusal { get; } -> PetRefusal` — Why not, as a value: `PetRefusal.None` when `PetResult.Ok`, else the first word of `PetResult.Reason`...
- `RequestId { get; init; } -> Guid` — The request's id (`PetRequest.RequestId`).

## PetReviveCanceledEvent `0.2.26` — A revive channel was canceled (nothing was taken unless its receipt says so).

## PetRevivedEvent `0.2.26` — A pet was revived: a new protected body.

## PetRevivePolicy `0.2.26` — How a downed pet is revived: the channel, its range, the health it comes back with, its cost and what cancels it.
- `Allowed { get; init; } -> bool` — False: a downed pet of this definition cannot be revived by `Pets.Revive` (the mod releases it, or revives it its...
- `CancelOnMovement { get; init; } -> bool` — The owner moving farther than `PetRevivePolicy.MaxOwnerDisplacement` from where the channel began cancels it.
- `CancelOnObservedOwnerDamage { get; init; } -> bool` — A hit, a fall or health lost by the owner during the channel cancels it.
- `ChannelDuration { get; init; } -> TimeSpan` — How long, in simulation time, the owner channels: 0.1..60 s (JSON: `channelSeconds`).
- `Cost { get; init; } -> PetCostDefinition?` — What the revive costs, taken when the channel completes; null: free.
- `HealthFraction { get; init; } -> double` — The share of its maximum health the pet comes back with, more than 0 and at most 1.
- `MaxOwnerDisplacement { get; init; } -> double` — Metres, 0.1..2.
- `RangeMeters { get; init; } -> double` — Metres between the owner and the recovery anchor during the channel, 1..8.

## PetReviveStartedEvent `0.2.26` — A revive channel started.

## PetRevivingEvent `0.2.26` — A revive is about to start: its cost was quoted and nothing has moved.
- `Cost { get; } -> PetCostDefinition?` — The quoted cost; null: free.
- `Owner { get; } -> CharacterKey` — The owner.
- `Pet { get; } -> PetSnapshot` — The downed pet before the channel.
- `RequestId { get; } -> Guid` — The revive's request id (`Pets.CancelRevive` names it).

## Pets `0.2.26` — Pets: owned creatures that follow their owner, fight, go down and are revived.
- `static CancelRevive(PlayerRef owner, PetId pet, Guid reviveRequest, PetRequest request) -> Task<PetResult>` — Cancels the revive `reviveRequest` names (its request's id, `PetSnapshot.ReviveRequest`; never a later one).
- `static Command(PlayerRef owner, PetId pet, PetOrder order, EntityRef target, PetRequest request) -> Task<PetResult>` — Orders a live pet: follow, stay, or attack `target` (needs `pets.fight`, and the pet's target rule must allow it...
- `const ControlCapability = "pets.control" -> string` — The capability: the broker takes pets' leases (creature_control).
- `static Define(PetDefinition definition) -> PetDefinitionResult` — Registers a kind of pet as the calling mod's.
- `static Definition(string id) -> PetDefinition?` — The definition registered under `id`, or null.
- `static Definitions { get; } -> IReadOnlyList<PetDefinition>` — Every registered definition, of every mod, by id.
- `const DefinitionsCapability = "pets.definitions" -> string` — The capability: definitions register (`Pets.Define`, a `PetBlock`).
- `static Dismiss(PlayerRef owner, PetId pet, PetRequest request) -> Task<PetResult>` — Puts the pet away: a live body is retired (its health kept), a downed pet's anchor is hidden (never healed); the...
- `const DownedCapability = "pets.downed" -> string` — The capability: a pet goes down instead of dying (the native death guard and retirement), and is revived...
- `const FightCapability = "pets.fight" -> string` — The capability: pets fight (attack orders, `Pets.SetHostile`): the hostile rule and the outgoing provenance gate of...
- `const FollowCapability = "pets.follow" -> string` — The capability: pets follow and stay (walk-only routes through kiln.queries, or the game's guard).
- `static Get(PetId pet) -> PetSnapshot?` — The pet, or null (unknown here, or its owner's records are not read yet: `Pets.Of` reads them).
- `static Grant(CharacterKey owner, string definition, PetRequest request) -> Task<PetResult>` — Gives a character a new pet of `definition`: a Ready record with no body (Summon brings it out).
- `static Grant(PlayerRef owner, string definition, PetRequest request) -> Task<PetResult>` — Gives the player's character a new pet of `definition`, as `Pets.Grant` does, naming its owner as every other...
- `static HostileTo(CharacterKey character) -> IReadOnlyList<CharacterKey>` — The characters declared hostile to `character` (`Pets.SetHostile`), in no set order: a mod lists its duels (to end...
- `const HudClientCapability = "pets.hud.client" -> string` — The capability: this game shows its owner's pets (the channel and the renderer: `PetsClient`).
- `const HudServerCapability = "pets.hud.server" -> string` — The capability: the server sends owners their pets' view (channel kiln:pets).
- `static IsHostile(CharacterKey a, CharacterKey b) -> bool` — Whether the two characters were declared hostile (`Pets.SetHostile`).
- `static Of(CharacterKey owner) -> IReadOnlyList<PetSnapshot>` — The character's pets (released tombstones left out).
- `static Of(PlayerRef player) -> IReadOnlyList<PetSnapshot>` — The pets of the player's character, as `Pets.Of` and as the requests name their owner (a `PlayerRef`), for a player...
- `const RecordsCapability = "pets.records" -> string` — The capability: owned records are kept per character in the world's save, with exact save receipts (a server world...
- `static Release(PlayerRef owner, PetId pet, PetRequest request) -> Task<PetResult>` — Releases the pet for good: a body is retired first, then the record becomes a tombstone that goes once a committed...
- `static Revive(PlayerRef owner, PetId pet, PetRequest request, ItemSelection? selection = null) -> Task<PetResult>` — Revives a downed pet: the owner channels next to its recovery anchor (summon it first) for the definition's channel...
- `const ServiceCapability = "kiln.pets" -> string` — The capability: Kiln.Pets is loaded (definitions, records and the facade).
- `static SetHostile(CharacterKey a, CharacterKey b, bool hostile) -> void` — Declares (true) or ends (false) a consensual hostility between two characters: for pets whose `PetTargetPolicy.Pvp`...
- `static Summon(PlayerRef owner, PetId pet, PetRequest request) -> Task<PetResult>` — Brings the pet out: a Ready pet gets a protected body 2 to 4 m from its owner, at its kept health, following; a...
- `const SummonCapability = "pets.summon" -> string` — The capability: bodies are made and retired (`Pets.Summon`, `Pets.Dismiss`): kiln.spawn on a running server world.

## PetsClient `0.2.26` — This game's view of its owner's pets, as the server sends it, and the built-in pet bar.
- `static Configure(PetHudOptions options) -> void` — Sets where and whether the built-in bar draws (this game only; never a server state).
- `static Options { get; } -> PetHudOptions` — The built-in bar's options on this game: the defaults until `PetsClient.Configure` sets others.
- `static Snapshot { get; } -> PetOwnerView?` — The latest view, or null (none received, disconnected).
- `static State { get; } -> PetViewState` — Whether `PetsClient.Snapshot` is current, stale (no keepalive for 3 s) or there is none.

## PetSnapshot `0.2.26` — A pet as it is now: an immutable copy, never updated in place.
- `Body { get; init; } -> EntityRef` — The body while `PetSnapshot.Presence` is Live or Retiring; none otherwise.
- `BodyGeneration { get; init; } -> ulong` — The body's generation (the broker's; 0: no body).
- `Definition { get; init; } -> string` — The id of its `PetDefinition` (`my-mod:hound`).
- `DurableRevision { get; init; } -> ulong` — The highest `PetSnapshot.StateRevision` a committed save holds.
- `Health { get; init; } -> int` — Its health (0 while down; while it has no body, its kept share of `PetSnapshot.HealthMax`).
- `HealthMax { get; init; } -> int` — Its maximum health (the last body's).
- `Id { get; init; } -> PetId` — The pet's id: stable for the life of its record, whatever bodies it has.
- `Label { get; init; } -> string` — The name its owner reads.
- `LastCommit { get; init; } -> PetCommit?` — The save that holds `PetSnapshot.DurableRevision`; null before the first.
- `Level { get; init; } -> int` — Its level, 1 to 255 (from the definition's `PetLevelPolicy`).
- `Life { get; init; } -> PetLife` — Alive, down or released.
- `Owner { get; init; } -> CharacterKey` — The character that owns it (not the player's session: the owner need not be online).
- `Presence { get; init; } -> PetPresence` — Where it is: no body, a live body, a recovery anchor, or a body being removed.
- `RecoveryAnchor { get; init; } -> WorldPosition?` — The recovery anchor's spot while it is shown (and the spot kept for it while Downed and Absent).
- `RequestedOrder { get; init; } -> PetOrder` — The order it was given.
- `RequestedTarget { get; init; } -> EntityRef` — The target of an attack order; none otherwise (never saved).
- `ReviveDuration { get; init; } -> TimeSpan` — How long the channel takes.
- `ReviveElapsed { get; init; } -> TimeSpan` — How much of the channel is done (simulation time).
- `ReviveRequest { get; init; } -> Guid?` — The request id of the revive channel running (the one `Pets.CancelRevive` names); null when none.
- `StateRevision { get; init; } -> ulong` — Increases once per change of its state (not per event).
- `Transition { get; init; } -> PetTransition` — What is happening to it.
- `WantsSummoned { get; init; } -> bool` — The owner's wish that it be out: set by Summon, cleared by Dismiss, kept across logout (the next login summons it).
- `WorldEpoch { get; init; } -> ulong` — The server world session its body belongs to (0: no body).

## PetStatsChangedEvent `0.2.26` — A pet's level, health or maximum health changed.

## PetSummonedEvent `0.2.26` — A pet was summoned: a body (Ready) or its recovery anchor next to its owner (Downed).

## PetTargetPolicy `0.2.26` — Who a pet may harm and who may harm it.
- `static DefaultHostileFactions { get; } -> IReadOnlyList<Faction>` — Kiln's list of hostile factions: Fogger, Vukah, Scavenger, WildBeast, Skeleton, Cyclops, Drak, Ancients.
- `HostileFactions { get; init; } -> IReadOnlyList<Faction>?` — The factions of unowned creatures a pet may fight.
- `Pvp { get; init; } -> PetPvpMode` — Players and pets of other characters.

## PetTransition `0.2.26` — What is happening to a pet right now.
enum: None, Spawning, Downing, Dismissing, Reviving, Releasing

## PetTransitionChangedEvent `0.2.26` — A pet entered or left a transition (Spawning, Downing, Dismissing, Reviving, Releasing).

## PetViewEntry `0.2.26` — One pet in its owner's view: what a HUD needs, and nothing the owner may not see.
- `Body { get; init; } -> PetBodyViewKey?` — Its body; null while it has none.
- `Current { get; init; } -> int` — Its health now (0 while it is down).
- `Definition { get; init; } -> string` — The id of its definition (`my-mod:hound`).
- `DurableRevision { get; init; } -> ulong` — The highest revision a committed save holds (`PetSnapshot.DurableRevision`): a HUD may show a pet whose state is not...
- `Id { get; init; } -> PetId` — The pet's id (`PetSnapshot.Id`).
- `Label { get; init; } -> string` — The name its owner reads (at most 64 bytes).
- `Level { get; init; } -> int` — Its level.
- `Life { get; init; } -> PetLife` — Alive or down.
- `Maximum { get; init; } -> int` — Its maximum health.
- `Presence { get; init; } -> PetPresence` — Where it is: no body, a live body or a recovery anchor.
- `RecoveryAnchor { get; init; } -> WorldPosition?` — Its recovery anchor while one is shown.
- `RequestedOrder { get; init; } -> PetOrder` — Its order.
- `ReviveDuration { get; init; } -> TimeSpan` — How long the channel takes (whole milliseconds).
- `ReviveElapsed { get; init; } -> TimeSpan` — How much of the channel is done (whole milliseconds: the wire's).
- `ReviveRequest { get; init; } -> Guid?` — The revive channel running (its request id, for a cancel); null when none.
- `ShowBar { get; init; } -> bool` — The definition wants it in the built-in bar (`PetHudPolicy.Bar`).
- `ShowMarker { get; init; } -> bool` — The definition wants its recovery anchor marked (`PetHudPolicy.RecoveryMarker`).
- `StateRevision { get; init; } -> ulong` — The pet's `PetSnapshot.StateRevision`: goes up with every change of its state.
- `Transition { get; init; } -> PetTransition` — What is happening to it.

## PetViewState `0.2.26` — Whether this game's view of its owner's pets is current.
enum: Disconnected, Current, Stale

## PetViewStateChangedEvent `0.2.26` — This game's view went current, stale or away.
- `State { get; } -> PetViewState` — The state now.
