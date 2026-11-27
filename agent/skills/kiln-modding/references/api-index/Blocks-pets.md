# Blocks (Kiln.Blocks): pets

## PetBlock `0.2.26` — A pet: a creature an owner keeps, summons, orders and revives.
- `Creature { get; init; } -> PartRef<CreatureBlock>` — The creature its body is: a `CreatureBlock` (inline, a handle, or a block's name) or a game creature by its...
- `Downed { get; init; } -> PetDownedPolicy` — What it becomes when it goes down.
- `Hud { get; init; } -> PetHudPolicy` — What the owner's HUD shows of it.
- `Level { get; init; } -> PetLevelPolicy` — Its level and size.
- `Limits { get; init; } -> PetLimits` — How many there may be (in JSON: `limits`).
- `Movement { get; init; } -> PetMovementPolicy` — How it follows: distances, catch-up.
- `Name { get; init; } -> string?` — The name owners read; default: the creature's own (`PetDefinition.Name`).
- `Revive { get; init; } -> ReviveBlock` — How it is revived.
- `Targets { get; init; } -> PetTargetPolicy` — Who it may harm and who may harm it.
- `Was { get; init; } -> string?` — The name before a rename: the id comes from the oldest name, so records keep their pets.

## ReviveBlock `0.2.26` — How a downed pet is revived: the owner channels next to its recovery anchor.
- `Allowed { get; init; } -> bool` — False: Kiln's revive is off for this pet (the mod releases it, or revives it its own way).
- `CancelOnMovement { get; init; } -> bool` — The owner moving more than `ReviveBlock.MaxOwnerDisplacement` cancels it.
- `CancelOnObservedOwnerDamage { get; init; } -> bool` — The owner being hurt during the channel cancels it.
- `ChannelDuration { get; init; } -> TimeSpan` — How long the owner channels, 0.1..60 s (JSON: `channelSeconds`, a number of seconds).
- `Cost { get; init; } -> ReviveCostBlock?` — What it costs, taken when the channel completes; null: free.
- `HealthFraction { get; init; } -> double` — The share of its maximum health the pet comes back with, more than 0 and at most 1.
- `MaxOwnerDisplacement { get; init; } -> double` — Metres, 0.1..2.
- `RangeMeters { get; init; } -> double` — Metres between owner and anchor while channelling, 1..8.

## ReviveCostBlock `0.2.26` — What a revive costs: so many of a plain stack item.
- `ReviveCostBlock(ItemRef Item, int Count = 1)`
- `Count { get; init; } -> int` — 1..999.
- `Item { get; init; } -> ItemRef` — A plain stack item of the game or of a mod (not gear).

## TamingBlock `0.2.26` — A taming: which creatures it tames, its steps in order (the last one tames), the timeouts, the refund policy, and the pet a tame makes.
- `Creatures { get; init; } -> IReadOnlyList<PartRef<CreatureBlock>>` — The creatures it tames, each named as a `PetBlock.Creature` is (one reference type): a game creature by its...
- `InactivityTimeout { get; init; } -> TimeSpan` — How long in simulation without an applied step before the attempt expires, 1..300 s (default 30; JSON...
- `MaxAttemptDuration { get; init; } -> TimeSpan` — The longest an attempt lasts, from `TamingBlock.InactivityTimeout` to 3600 s (default 300; JSON: `maxAttemptSeconds`).
- `Pet { get; init; } -> PartRef<PetBlock>?` — The pet a tame makes: a `PetBlock` (inline, a handle, its name, or `other-mod:name`, or the id of a pet a mod...
- `RangeMeters { get; init; } -> double` — How close the player must be, 1..8 m (default 3).
- `Refund { get; init; } -> TamingRefundPolicy` — What happens to paid steps when the attempt ends without a tame (default non-refundable).
- `RemoveTarget { get; init; } -> bool` — With a pet: the tamed wild body is removed once the pet record is made (default true).
- `RequireThresholdUntilCommit { get; init; } -> bool` — The latest weaken threshold holds until the tame (default true).
- `Steps { get; init; } -> IReadOnlyList<TamingStepBlock>` — The steps, 1..64, in order.
- `SummonPet { get; init; } -> bool` — With a pet: the new pet is summoned at once (default true).
- `Was { get; init; } -> string?` — The name before a rename: the id comes from the oldest name, so a running server keeps naming it the same.

## TamingStepBlock `0.2.26` — One step of a `TamingBlock`: `feed` (`TamingStepBlock.Item`, `TamingStepBlock.Count`), `weaken` (`TamingStepBlock.MaxHealthFraction`), `tool` (`TamingStepBlock.Item`...
- `AllowBreak { get; init; } -> bool?` — tool: the use may break the tool (default false).
- `Count { get; init; } -> int?` — feed: how many, 1..999 (default 1).
- `static Feed(ItemRef item, int count = 1) -> TamingStepBlock` — A feed step.
- `Item { get; init; } -> ItemRef?` — feed: a plain stack item; tool: a gear item.
- `Kind { get; init; } -> string` — `feed`, `weaken`, `tool` or `native`.
- `MaxHealthFraction { get; init; } -> double?` — weaken: the target's health must be at most this share of its maximum, in (0, 1].
- `static Native(int steps = 1) -> TamingStepBlock` — A native step (the game's own count).
- `Steps { get; init; } -> int?` — native: how many of the game's own steps (a petting while it eats its bait), 1..255 (default 1).
- `static Tool(ItemRef item, int wear = 1, bool allowBreak = false) -> TamingStepBlock` — A tool step.
- `static Weaken(double maxHealthFraction) -> TamingStepBlock` — A weaken step.
- `Wear { get; init; } -> int?` — tool: the durability one use takes, 0..32767 (default 1).
