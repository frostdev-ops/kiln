# Combat (Kiln.Combat)

## ActionSpeed `0.2.28` — Per-player speed multipliers for melee, bow and tool actions.
- `static Available { get; } -> bool` — The sequence and graph scaling work in this game (server, host or joined client).
- `static Changed -> Action<ActionSpeedChanged>?` — An effective value changed (any side).
- `static Clear(EntityId player) -> void` — Removes all of this mod's values for one player.
- `static Clear(EntityId player, ActionKind kind) -> void` — Removes this mod's value for one player and kind (its default applies again).
- `static ClearDefault(ActionKind kind) -> void` — Removes this mod's default for `kind`.
- `static Get(EntityId player, ActionKind kind) -> double` — The effective multiplier: every mod's value multiplied, clamped to [`ActionSpeed.Min`, `ActionSpeed.Max`].
- `const Max = 10 -> double` — The largest effective multiplier.
- `const MeleeSamplingAbove = 2 -> double` — Kiln samples melee contacts between frames while a player's effective melee multiplier is above this.
- `const Min = 0.5 -> double` — The smallest effective multiplier.
- `static Set(EntityId player, ActionKind kind, double multiplier) -> void` — Sets this mod's multiplier for one player; it overrides this mod's default for that player.
- `static SetDefault(ActionKind kind, double multiplier) -> void` — Sets this mod's multiplier for `kind` for every player, now and later joins.
- `static Status { get; } -> ActionSpeedStatus` — Which parts work here, with the reason for any that don't.

## ActionSpeedChanged `0.2.28` — An effective action speed changed (`ActionSpeed.Changed`).
- `ActionSpeedChanged()`
- `ActionSpeedChanged(EntityId Player, ActionKind Kind, double Multiplier)`
- `Kind { get; init; } -> ActionKind` — The action kind.
- `Multiplier { get; init; } -> double` — The new effective multiplier (every mod's value multiplied, clamped).
- `Player { get; init; } -> EntityId` — The player whose value changed.

## ActionSpeedStatus `0.2.28` — Which parts of `ActionSpeed` work in this game.
- `ActionSpeedStatus(bool Sequence, bool Graph, bool BowTiming, bool MeleeSampling, bool Prediction, string? Reason)`
- `BowTiming { get; init; } -> bool` — Bow charge and fire durations are scaled.
- `Graph { get; init; } -> bool` — The animation graph delta is scaled (visible and collision poses).
- `MeleeSampling { get; init; } -> bool` — Melee contacts are sampled between frames above `ActionSpeed.MeleeSamplingAbove`.
- `Prediction { get; init; } -> bool` — A joined client applies the rates to its own actor's prediction and presentation.
- `Reason { get; init; } -> string?` — Why a part is off, or null.
- `Sequence { get; init; } -> bool` — The action sequence clock is scaled (hit events, combos, recovery).

## AttributeIds — The game's attribute ids of one pair, read from its attribute resource when the first world starts.
- `AttributeIds()`
- `AttributeIds(GameAttribute Kind, uint CurrentId, uint MinId, uint MaxId, uint ComponentHash, bool GameClamp)`
- `ComponentHash { get; init; } -> uint` — FNV-1a of the component's type name (keen::ecs::Stamina is 0x92410664).
- `CurrentId { get; init; } -> uint` — The id of the current value (the one `Attributes.Set` writes).
- `GameClamp { get; init; } -> bool` — The game's own formula clamps the current value to [min, max] after every change.
- `Kind { get; init; } -> GameAttribute` — The pair.
- `MaxId { get; init; } -> uint` — The id of the maximum, 0 when the pair has none.
- `MinId { get; init; } -> uint` — The id of the minimum, 0 when the pair has none.

## AttributeReading — One attribute of an entity with its pair's current value and bounds.
- `AttributeReading()`
- `AttributeReading(uint Id, GameAttribute Kind, int Value, int Current, int? Min, int? Max, bool IsCurrent, uint CurrentId, uint MaxId)`
- `Current { get; init; } -> int` — The pair's current value.
- `CurrentId { get; init; } -> uint` — The pair's current-value id.
- `Fraction { get; } -> double` — Current / maximum, 0 without a positive maximum.
- `Id { get; init; } -> uint` — The attribute id asked for.
- `IsCurrent { get; init; } -> bool` — `Id` is the pair's current value, the one that can be set and added to.
- `Kind { get; init; } -> GameAttribute` — The pair it belongs to.
- `Max { get; init; } -> int?` — The pair's maximum, null when it has none.
- `MaxId { get; init; } -> uint` — The pair's maximum id, 0 when it has none.
- `Min { get; init; } -> int?` — The pair's minimum, null when it has none.
- `Value { get; init; } -> int` — The value of `Id` (for a current value: the same as `Current`).

## Attributes — Health, stamina, mana, body heat and oxygen of any entity on the server world, by the game's own attribute ids (`Attributes.Ids`: resolved from the game's attribute...
- `static Add(EntityId entity, GameAttribute attribute, int delta) -> int` — Adds `delta` (negative to spend) to the current value, clamped like Set.
- `static Add(EntityId entity, uint attributeId, int delta) -> int` — Add by the game's attribute id of a pair's current value.
- `static Available { get; } -> bool` — True when the attribute functions work on this build and a world has started (capability `combat.attributes`).
- `static Get(EntityId entity, GameAttribute attribute) -> AttributeReading?` — The current value of a pair with its bounds, or null when the entity has no such attribute (a creature without mana...
- `static Get(EntityId entity, uint attributeId) -> AttributeReading?` — Any attribute of the five components by its id (current, minimum, maximum, recharge...), with its pair's current...
- `static Ids(GameAttribute attribute) -> AttributeIds?` — The game's ids of a pair, or null before the first server world runs or when this build has no attribute resource...
- `static Set(EntityId entity, GameAttribute attribute, int value) -> int` — Sets the current value, clamped to the pair's [min, max].
- `static Set(EntityId entity, uint attributeId, int value) -> int` — Set by the game's attribute id of a pair's current value (see `Attributes.Ids`).
- `static TryAdd(EntityId entity, GameAttribute attribute, int delta, out AttributeReading result) -> bool` — Like `Attributes.Add`, false instead of throwing when the entity lacks the attribute or attributes are unavailable.
- `static TrySet(EntityId entity, GameAttribute attribute, int value, out AttributeReading result) -> bool` — Like `Attributes.Set`, false instead of throwing when the entity lacks the attribute or attributes are unavailable.

## Combat — Health, damage, healing, kills and god mode on the server world, through the engine's own paths: health writes go through the attribute system (the game copies Health...
- `static Available { get; } -> bool` — True when health access works on this build and a server world runs.
- `static Damage(EntityId target, int amount, EntityId attacker = default(EntityId), DamageOptions? options = null) -> Task<HitResult>` — Deals `amount` damage to `target` as an engine hit from `attacker` (kill credit and XP go to the attacker when it is...
- `static GetHealth(EntityId entity) -> Health?` — The entity's health, or null when it has none (props without Health, unknown ids).
- `static Heal(EntityId entity, int amount) -> int` — Heals up to max, silently (no HitEvent).
- `static HitsAvailable { get; } -> bool` — True when Damage/Kill (the engine hit path) work.
- `static IsInvulnerable(EntityId entity) -> bool` — Kiln god mode is on for the entity (`Combat.SetInvulnerable`, any mod).
- `static Kill(EntityId target, EntityId attacker = default(EntityId), DamageOptions? options = null) -> Task<HitResult>` — Kills `target` with a hit of exactly its current health (a killing blow; kill credit for `attacker`).
- `static SetHealth(EntityId entity, int value) -> int` — Sets current health (clamped to 0..max) silently.
- `static SetInvulnerable(EntityId entity, bool on = true) -> void` — God mode for one entity (players too): damage hits on it are dropped before they apply (no reaction either), and...
- `static Status { get; } -> CombatStatus?` — kiln.combat's state and counters, or null when it is not loaded.
- `static TryGetHealth(EntityId entity, out Health health) -> bool` — The entity's health; false when it has none.

## CombatChangedEvent `0.2.2` — A mod changed an entity through Kiln.Combat without a hit or a heal: its health set (`Combat.SetHealth`), its god mode (`Combat.SetInvulnerable`) or an attribute's...
- `After { get; } -> int` — The value after.
- `Before { get; } -> int` — The value before (health points, the attribute's current value, 1/0 for god mode on/off).
- `Entity { get; } -> EntityId` — The entity.
- `Involves(EntityId entity) -> bool` — Whether the event is about `entity`.
- `Origin { get; } -> string` — The id of the mod that made the change ("" when Kiln made it for no mod).
- `What { get; } -> string` — What changed: `Health`, `Invulnerable`, or `Attribute.<name>` (`Attribute.Stamina`; `Attribute.0x...` for an id...

## CombatOrigin — Where a hit or a death came from.
enum: Game, Kiln, Other

## CombatStatus — Counters of kiln.combat (diagnostics).
- `CombatStatus(bool Health, bool Hits, bool Deaths, bool Holding, uint Interest, ulong HitsSeen, ulong HitsHeld, ulong HitsReleased, ulong HitsCanceled, ulong HitsModified, ulong KilnHits, ulong GodBlocked, ulong GodRestored, ulong DeathsSeen, ulong EventsDelivered, uint Invulnerable, ulong GameDiedEvents, ulong HitsDeferred, ulong DropsSeen, ulong DropsCanceled, ulong RepeatHits)`
- `AttributeHeals { get; init; } -> bool` — changeAttributeValue health gains reach `EntityHealEvent`.
- `AttributeHealsCorrected { get; init; } -> ulong` — Direct health gains that a handler canceled or changed, so Kiln corrected the health.
- `AttributeHealsSeen { get; init; } -> ulong` — Direct health gains (`changeAttributeValue`: potions, food, skills) kiln.combat saw.
- `Deaths { get; init; } -> bool` — Death events work on this build.
- `DeathsSeen { get; init; } -> ulong` — Deaths that kiln.combat reported.
- `DropsCanceled { get; init; } -> ulong` — Loot containers that handlers canceled (they are destroyed).
- `DropsSeen { get; init; } -> ulong` — Loot containers seen at corpses.
- `EventsDelivered { get; init; } -> ulong` — Events delivered to the listener.
- `Falls { get; init; } -> bool` — Fall damage is intercepted on this build (`EntityFallEvent`).
- `FallsCanceled { get; init; } -> ulong` — Fall damage events that handlers canceled.
- `FallsModified { get; init; } -> ulong` — Fall damage events whose damage a handler changed.
- `FallsSeen { get; init; } -> ulong` — Fall damage events kiln.combat saw (see `EntityFallEvent`).
- `GameDiedEvents { get; init; } -> ulong` — The game's own entity-died events kiln.combat read.
- `GodBlocked { get; init; } -> ulong` — Hits dropped because the target was invulnerable.
- `GodRestored { get; init; } -> ulong` — Other health losses that god mode took back.
- `Health { get; init; } -> bool` — Health reads and writes work on this build.
- `Hits { get; init; } -> bool` — Damage and kill hits and the hit hooks work on this build.
- `HitsCanceled { get; init; } -> ulong` — Held hits that handlers canceled.
- `HitsDeferred { get; init; } -> ulong` — Damage hits moved to a later step (the engine applies at most 4 impacts per target per step).
- `HitsHeld { get; init; } -> ulong` — Hits held for one step for the listener.
- `HitsModified { get; init; } -> ulong` — Held hits whose amount a handler changed.
- `HitsReleased { get; init; } -> ulong` — Held hits let go to apply.
- `HitsSeen { get; init; } -> ulong` — Hits the engine produced that kiln.combat saw.
- `Holding { get; init; } -> bool` — Hits are being held for the listener right now (see `EntityHurtEvent`).
- `Interest { get; init; } -> uint` — The mask of event kinds the listener asked for.
- `Invulnerable { get; init; } -> uint` — How many entities are in god mode now.
- `KilnHits { get; init; } -> ulong` — Hits from `Combat.Damage` and `Combat.Kill` that were applied.
- `KnockbacksModified { get; init; } -> ulong` — Push-back and hit-reaction events that a handler changed or canceled.
- `KnockbacksSeen { get; init; } -> ulong` — Push-back and hit-reaction events kiln.combat saw (see `EntityKnockbackEvent`).
- `RegenCanceled { get; init; } -> ulong` — Natural regeneration events that handlers canceled.
- `Regeneration { get; init; } -> bool` — Natural regeneration reaches `EntityHealEvent`.
- `RegenSeen { get; init; } -> ulong` — Natural regeneration events kiln.combat saw (see `EntityHealEvent`).
- `RepeatHits { get; init; } -> ulong` — Held hits not reported because they repeat an impact on a target that the engine skips.

## DamageOptions — Options for `Combat.Damage`.
- `Aggro { get; init; } -> float` — Threat for enemy AI (the game's own hits use about 1).
- `AsGameHit { get; init; } -> bool` — Treat the hit like one the engine produced: no synchronous `EntityHurtEvent`; it is held and reported in the next...
- `static readonly Default -> DamageOptions` — Options with every setting at its default.
- `ForceInterrupt { get; init; } -> bool` — The hit interrupts the target's current action.
- `NoCredit { get; init; } -> bool` — Clears the target's credit before the hit.
- `PushBack { get; init; } -> byte` — Push-back force 0..255.
- `Reaction { get; init; } -> HitReaction` — Stagger of the target (none by default).
- `Silent { get; init; } -> bool` — No HitEvent: health changes, but no reaction, no damage number for clients, no kill credit for this hit (a player...
- `Source { get; init; } -> EntityId` — The direct source entity (weapon, projectile); default: the attacker.

## EntityDamagedEvent — A hit changed an entity's health (after the fact; observe only).
- `Amount { get; } -> int` — Health lost (positive) or gained (negative).
- `Attacker { get; } -> EntityId` — The root attacker (the player for player hits, the creature for creature hits); None if unknown.
- `Flags { get; } -> HitFlags` — The engine's hit flags of the hit.
- `HealthAfter { get; } -> int` — The target's health after the hit.
- `HealthBefore { get; } -> int` — The target's health before the hit.
- `HealthMax { get; } -> int` — The target's maximum health.
- `Involves(EntityId entity) -> bool` `0.2.2` — For event filters (`EventFilter`): the target, the attacker or the source.
- `IsPlayer { get; } -> bool` — True when the target is a player.
- `KillingBlow { get; } -> bool` — This hit took the target from above 0 to 0 or below.
- `Origin { get; } -> CombatOrigin` — Where the hit came from: the engine or a `Combat` call.
- `Position { get; } -> WorldPosition` — The hit point (metres).
- `PushBack { get; } -> int` — The push-back force the hit applied with (after `EntityKnockbackEvent` handlers).
- `Reaction { get; } -> HitReaction` — The hit reaction the hit applied with (after `EntityKnockbackEvent` handlers).
- `Source { get; } -> EntityId` — The direct source: weapon, projectile, spell or the attacker itself.
- `Step { get; } -> ulong` — The sim step the hit was reported for.
- `Target { get; } -> EntityId` — The entity whose health changed (a creature, not its weakspot).

## EntityDeathEvent — An entity died (Forge: LivingDeathEvent; here observe only): the game's EntityDiedEvent, posted in the Pre after the game's death program ran.
- `Flags { get; } -> HitFlags` — Flags of the killing blow (KillingBlow is always set when there was one).
- `Health { get; } -> int` — The victim's health when the death was reported.
- `Involves(EntityId entity) -> bool` `0.2.2` — For event filters (`EventFilter`): the victim, the killer or the source.
- `IsPlayer { get; } -> bool` — True when the victim is a player.
- `Killer { get; } -> EntityId` — Root attacker of the killing blow (a player, a creature) when the death came from a hit Kiln saw; None for deaths...
- `Origin { get; } -> CombatOrigin` — Game/Kiln: the killing blow was a hit of that origin; Other: no hit.
- `Position { get; } -> WorldPosition` — Where the victim was when the death was reported (metres).
- `Source { get; } -> EntityId` — The killing blow's direct source (weapon, projectile).
- `Step { get; } -> ulong` — The sim step the death was reported for.
- `Victim { get; } -> EntityId` — The entity that died.

## EntityDropsEvent — A loot container appeared at a corpse (Forge: LivingDropsEvent, one event per container): an entity of a LootContainer_* template with the game's DestroyOnLoot tag...
- `Container { get; } -> EntityId` — The loot container entity (may equal `EntityDropsEvent.Victim` when the corpse itself became loot).
- `Involves(EntityId entity) -> bool` `0.2.2` — For event filters (`EventFilter`): the victim, the container or the killer.
- `Killer { get; } -> EntityId` — The root attacker of the killing blow when Kiln saw one; None otherwise.
- `Position { get; } -> WorldPosition` — Where the container is (metres).
- `Step { get; } -> ulong` — The sim step the container was reported for.
- `Victim { get; } -> EntityId` — The entity whose death produced the container.

## EntityDyingEvent `0.2.4` — An entity's health reached 0 or less and the game is about to let it die: a handler may `CancelableEvent.Cancel` to keep it alive with...
- `EntityDyingEvent(DecisionId id, EventEntityKey key, int health, int maximumHealth)` — Makes the event (Kiln posts it; a mod has no reason to).
- `Entity { get; } -> EntityId` — The dying entity.
- `Health { get; } -> int` — Its health, read in this Pre (0 or less).
- `Id { get; } -> DecisionId` — The decision; the same id is on its `DecisionCompletedEvent`.
- `Involves(EntityId entity) -> bool` — Whether the event is about `entity`.
- `Key { get; } -> EventEntityKey` — Which life of the entity id is dying.
- `MaximumHealth { get; } -> int` — Its maximum health, read in this Pre (0 when it could not be read).
- `SurvivalHealth { get; set; } -> int` — The health a canceled death leaves the entity with: positive, 1 by default, clamped to the entity's maximum when it...

## EntityFallEvent — An entity is about to take fall damage (Forge: LivingFallEvent).
- `Damage { get; set; } -> int` — Health points the fall takes.
- `DamageFraction { get; } -> float` — The game's damage as a fraction of max health (1 = lethal from full health).
- `Distance { get; } -> float` — Metres fallen (the game's detected fall distance).
- `Entity { get; } -> EntityId` — The entity that fell.
- `HealthBefore { get; } -> int` — The entity's health when the event was built.
- `HealthMax { get; } -> int` — The entity's maximum health when the event was built.
- `Involves(EntityId entity) -> bool` `0.2.2` — For event filters (`EventFilter`): the entity that fell.
- `IsPlayer { get; } -> bool` — True when the entity is a player.
- `Lethal { get; } -> bool` — The original damage would kill.
- `OriginalDamage { get; } -> int` — The damage the game computed, before any handler changed `EntityFallEvent.Damage`.
- `Position { get; } -> WorldPosition` — Where the entity is (metres).
- `Step { get; } -> ulong` — The sim step the fall was reported for.

## EntityHealEvent — An entity is about to be healed (Forge: LivingHealEvent): healing hits (spells), natural regeneration, effects that add health directly (potions, food), and...
- `Cause { get; } -> HealCause` — What heals the entity: a healing hit, `Combat.Heal`, natural regeneration or an effect that adds health directly.

## EntityHurtEvent — An entity is about to take damage from a hit (Forge: LivingAttackEvent + LivingHurtEvent + LivingDamageEvent in one).
- `Lethal { get; } -> bool` — The original amount would kill the target (cancel or lower it to keep the target alive).

## EntityKnockbackEvent — A hit is about to push an entity back or make it stagger (Forge: LivingKnockBackEvent).
- `Attacker { get; } -> EntityId` — The root attacker (the player for player hits, the creature for creature hits); None if unknown.
- `Direction { get; set; } -> Vector3` — The hit direction the push follows (attacker -> target).
- `Flags { get; } -> HitFlags` — The engine's hit flags of the hit.
- `ImpactId { get; } -> uint` — The engine's impact instance id (one swing or projectile).
- `Involves(EntityId entity) -> bool` `0.2.2` — For event filters (`EventFilter`): the target, the part hit, the attacker or the source.
- `IsPlayer { get; } -> bool` — True when the target is a player.
- `Origin { get; } -> CombatOrigin` — Where the hit came from: the engine or a `Combat` call.
- `OriginalDirection { get; } -> Vector3` — The hit direction the engine produced, before any handler changed `EntityKnockbackEvent.Direction`.
- `OriginalReaction { get; } -> HitReaction` — The hit reaction the engine produced, before any handler changed `EntityKnockbackEvent.Reaction`.
- `OriginalStrength { get; } -> int` — The push-back force the engine produced, before any handler changed `EntityKnockbackEvent.Strength`.
- `Part { get; } -> EntityId` — The entity the hit landed on (a weakspot or body part); equals `EntityKnockbackEvent.Target` otherwise.
- `Position { get; } -> WorldPosition` — The hit point (metres).
- `Reaction { get; set; } -> HitReaction` — How hard the target staggers.
- `Source { get; } -> EntityId` — The direct source: weapon, projectile, spell or the attacker itself.
- `Step { get; } -> ulong` — The sim step the engine produced the hit in.
- `Strength { get; set; } -> int` — Push-back force 0..255 (the engine's pushBackForce).
- `Target { get; } -> EntityId` — The entity whose health the hit changes (a creature, not its weakspot).

## GameAttribute — The attribute pairs `Attributes` handles: a current value with a minimum and a maximum that the game computes (level, gear, buffs).
enum: Health, Stamina, Mana, BodyHeat, Oxygen

## HealCause — What healed an entity (`EntityHealEvent.Cause`).
enum: Hit, Kiln, Regeneration, Attribute

## Health — An entity's health (engine health points).
- `Health()`
- `Health(int Current, int Max, bool Dead, bool HasActor, bool Invulnerable)`
- `Current { get; init; } -> int` — Health points now.
- `Dead { get; init; } -> bool` — The game's dead state (Actor state 7, read through the game's own state test) as its death check leaves it at the...
- `Fraction { get; } -> double` — Current health as a fraction of the maximum (0 when the maximum is 0 or less).
- `HasActor { get; init; } -> bool` — The entity has an Actor (creatures, players, NPCs).
- `Invulnerable { get; init; } -> bool` — Kiln god mode is on for it.
- `Max { get; init; } -> int` — The computed maximum.

## HitFlags — The engine's HitEventFlags (what the hit was).
enum: None, Blocked, Parried, BlockBroken, Crit, Visceral, Backstab, Weakspot, SkillShot, Resistance, Immune, Ineffective, KillingBlow, Healing, Terrain, Building, ForceInterrupt

## HitReaction — The engine's hit reaction (impact::HitReactionType): how hard the target staggers.
enum: None, VeryLight, Light, Medium, Heavy, Boss, LongStun, TrapStun, ThrowIntoAir

## HitResult — Outcome of one `Combat.Damage` / `Combat.Kill` hit.
- `HitResult(HitState State, EntityId Target, int Amount, int HealthBefore, int HealthAfter, bool KillingBlow, int Steps)`
- `Amount { get; init; } -> int` — The damage the hit asked for, after any `EntityHurtEvent` handler changed it.
- `Applied { get; } -> bool` — True when the engine applied the hit to the target (`HitState.Applied`).
- `HealthAfter { get; init; } -> int` — The target's health after the hit; -1 when it could not be read.
- `HealthBefore { get; init; } -> int` — The target's health before the hit; -1 when it could not be read.
- `KillingBlow { get; init; } -> bool` — The hit took the target from above 0 health to 0 or below.
- `Ok { get; } -> bool` `0.2.2` — The engine applied the hit (`HitResult.Applied`).
- `Reason { get; } -> string?` `0.2.2` — Why the hit was not applied; null when it was.
- `State { get; init; } -> HitState` — Whether the hit is still waiting, was applied or was dropped.
- `Steps { get; init; } -> int` — How many sim steps passed from the request until the hit reached its state.
- `Target { get; init; } -> EntityId` — The entity the hit was aimed at.

## HitState — Where a hit made by `Combat.Damage` or `Combat.Kill` stands.
enum: Pending, Applied, Dropped

## PendingHitEvent — Common fields of a hit that is about to apply (`EntityHurtEvent`, `EntityHealEvent`).
- `Amount { get; set; } -> int` — Health points the hit changes, as a positive number.
- `AttackCategory { get; } -> byte` — The engine's attack category of the hit, as the raw number the game uses.
- `Attacker { get; } -> EntityId` — The root attacker (the player for player hits, the creature for creature hits); None if unknown.
- `DamageTypes { get; } -> ushort` — The engine's damage type mask (Cut, Pierce, Blunt, Fire, Ice, ...; bit order of DamageSetup).
- `Flags { get; } -> HitFlags` — The engine's hit flags: what kind of hit this is (blocked, critical, backstab and so on).
- `HealthBefore { get; } -> int` — The target's health when the event was built (-1 unknown, e.g. a destructible prop without Health yet).
- `HealthMax { get; } -> int` — The target's maximum health when the event was built (-1 when unknown).
- `ImpactId { get; } -> uint` — The engine's impact instance id (one swing or projectile).
- `Involves(EntityId entity) -> bool` `0.2.2` — For event filters (`EventFilter`): the target, the part hit, the attacker or the source, the four entities...
- `IsPlayer { get; } -> bool` — True when the target is a player.
- `Origin { get; } -> CombatOrigin` — Where the hit came from: the engine or a `Combat` call.
- `OriginalAmount { get; } -> int` — The amount the engine (or the Combat call) produced, before any handler.
- `Part { get; } -> EntityId` — The entity the hit landed on (a weakspot or body part); equals `PendingHitEvent.Target` otherwise.
- `Position { get; } -> WorldPosition` — Hit point (metres).
- `Reaction { get; } -> HitReaction` — How hard the hit makes the target stagger, as the engine decided it.
- `Source { get; } -> EntityId` — The direct source: weapon, projectile, spell or the attacker itself.
- `Step { get; } -> ulong` — Sim step in which the engine produced the hit.
- `Target { get; } -> EntityId` — The entity whose health changes (a creature, not its weakspot).
- `WeaponCategory { get; } -> byte` — The engine's weapon category of the hit, as the raw number the game uses.

## PlayerRespawnEvent — A dead player is alive again (Forge: PlayerRespawnEvent): respawned at a flame altar / bed or revived by another player.
- `Health { get; } -> int` — The player's health after the respawn.
- `HealthMax { get; } -> int` — The player's maximum health.
- `Involves(EntityId entity) -> bool` `0.2.2` — For event filters (`EventFilter`): the player.
- `Player { get; } -> EntityId` — The player that is alive again.
- `Position { get; } -> WorldPosition` — Where the player is (metres).
- `Step { get; } -> ulong` — The sim step the respawn was reported for.
