# kiln.combat

Health, damage, healing, kills and god mode on the server world, and Forge-style combat events (hurt, heal, damaged,
death, drops, respawn). Everything goes through the engine's own paths: health writes through the attribute system
(the game copies Health into the replicated `NetworkHealth` every step), damage as a real engine hit (health change,
killing blow, hit reaction, damage number on clients, kill credit / XP for the attacker).

| Layer | File |
|---|---|
| C table | `native/core/abi/services/kiln_combat.h` (`kiln.combat` v2: v1 plus the attribute functions) |
| Native | `native/services/combat/` → `kiln/services/kiln-combat.dll` |
| Symbols | `sigdb/symbols/server.d/combat.json` (features `combat.health`, `combat.hits`, `combat.deaths`, `combat.actor_states`) |
| Managed | `managed/services/Kiln.Combat/` → `kiln/host/Kiln.Combat.dll` (`Kiln.Combat`, `Kiln.Attributes`, the event classes) |
| Test mod | `mods/tests/combat-test/` (player-free) |
| Evidence | `docs/re/combat.md` |

Fails soft: the service is always published; a part that does not resolve on a build makes its functions return
`KILN_ERR_UNAVAILABLE` (logged at boot: `resolved: health on, hits on (record 0x150), deaths on`). Death events need
kiln.world (its program registration); drop cancel needs kiln.spawn.

## Managed (mods)

Reference `managed/services/Kiln.Combat/Kiln.Combat.csproj` with `Private="false" ExcludeAssets="runtime"`. All calls
are tick-thread only (mod callbacks, after `await NextTick()`); other threads get `InvalidOperationException`.

```csharp
using Kiln;

Health? h = Combat.GetHealth(entity);            // Current, Max, Dead, HasActor, Invulnerable; null = no Health
int now = Combat.SetHealth(entity, 50);          // clamped 0..max, silent; 0 kills next step (no killer)
int added = Combat.Heal(entity, 25);             // silent, up to max; fires EntityHealEvent first
HitResult r = await Combat.Damage(target, 30, attacker, new DamageOptions { Reaction = HitReaction.Medium });
HitResult k = await Combat.Kill(target, attacker);   // a killing blow of exactly its health; credit to attacker
Combat.SetInvulnerable(player);                  // god mode (per mod; ends when the mod unloads)
Combat.SetInvulnerable(player, false);
```

- `Damage`/`Kill` return `Task<HitResult>` (`State` Applied/Dropped, `HealthBefore`, `HealthAfter`, `KillingBlow`,
  `Steps`). The hit applies in the engine's next hit conclusion (the same step when called in Pre); the task completes
  in the next Pre. Damage is "true damage" (armour and resistances are applied by the engine before hits are queued,
  so they do not apply to Kiln hits). `DamageOptions`: `Reaction`, `PushBack`, `Aggro`, `ForceInterrupt`, `Silent`
  (no HitEvent: no reaction, number or credit for this hit), `NoCredit`, `Source`, `AsGameHit` (see below).
- **Kill credit.** The game gives a death's XP to the target's `ExperienceSource.lastHitPlayerId`: the last player whose
  hit landed (or whose summon or guard hit it), not whoever lands the killing blow. A hit with no attacker leaves it as
  it is, so `Kill(target)` alone still credits a player who hit the target earlier. `NoCredit` clears it first: with no
  attacker, the kill credits nobody (`Kill(target, EntityId.None, new DamageOptions { NoCredit = true })`).
- The engine applies at most 4 different impacts to one target per step; Kiln keeps further Kiln hits on a target for
  the following steps (`CombatStatus.HitsDeferred`), so many hits in one step all apply, a few steps later.
- Dropped: canceled by a handler, the target is invulnerable, gone, or has no `CombatFlags.handleHits` (props).
- God mode: damage hits are dropped before they apply (no reaction either); health lost any other way (fall damage,
  drowning) is restored at the next Pre, before the game's death check; `SetHealth` cannot take a god entity below 1.
  Healing still works. Several mods may hold it; it ends when the last one lets go or unloads.

### Attributes: stamina, mana, body heat, oxygen (and health)

`Attributes` reads and writes the five attribute pairs of any entity that carries them: creatures and players alike.
The game's attribute ids are not built in: Kiln reads them from the game's attribute resource when the first server
world starts (`Attributes.Ids(...)`).

```csharp
using Kiln;

AttributeReading? s = Attributes.Get(entity, GameAttribute.Stamina);   // Current, Min, Max; null = the entity has none
int now = Attributes.Set(entity, GameAttribute.Mana, 40);              // clamped to [min, max]; returns the new value
int after = Attributes.Add(player, GameAttribute.Oxygen, -10);         // negative spends; throws if the entity has none
if (Attributes.TryAdd(entity, GameAttribute.BodyHeat, 25, out var r)) Log.Info($"{r}");   // no throw
AttributeIds? ids = Attributes.Ids(GameAttribute.Stamina);              // the game's ids: CurrentId, MinId, MaxId
```

- `GameAttribute`: `Health`, `Stamina`, `Mana`, `BodyHeat`, `Oxygen`. A creature has the ones its template carries: a
  Vukah has health, stamina and oxygen; mana and body heat belong to players (`Player_Vukah_Melee` has mana too). `Get`
  returns null and `Set`/`Add` throw (`TrySet`/`TryAdd` return false) for an attribute the entity lacks.
- `Set` and `Add` clamp to the pair's minimum and maximum. The game computes the maximum from level, gear and buffs, so
  it is not writable. A write is silent: no hit, no event, no heal number. Health goes through `Combat.SetHealth`
  (god mode keeps 1); use `Combat.Heal`/`Damage` when you want events.
- By id: `Attributes.Get(entity, 0x04B6AA8B)` reads any attribute of the five components (current, minimum, maximum,
  recharge, base...) together with its pair; `Set`/`Add` by id accept only a pair's current-value id. An id outside the
  five components throws. These are the ids EMF's `attribute_get` and `attribute_add` use.
- Tick thread only (except `Ids`). The game copies a creature's stamina into `NetworkStamina` and a player's values into
  `NetworkPlayerAttributes` itself; its own regeneration keeps running, so a value you set can move on the next step.
- `Kiln.Abilities.Attributes` (health, mana, stamina, special ability by built-in ids, `Add` without a clamp of its own)
  still works; use `Kiln.Attributes` for the clamp, body heat and oxygen, and for non-player reads.

### Events (on Kiln's bus, `Events.On<T>` / `[Subscribe]`)

All are posted on the tick thread in **Pre** (before the sim step) and only built while someone subscribes.

| Event | Forge | When | Writable / cancel |
|---|---|---|---|
| `EntityHurtEvent` | LivingAttackEvent + LivingHurtEvent + LivingDamageEvent | before a damage hit applies. Game hits: the Pre after the engine produced them (they are held one step); `Combat.Damage/Kill`: inside the call | `Amount` (>= 0), `Cancel()` = the hit never happens. `Lethal` = the original amount would kill |
| `EntityHealEvent` | LivingHealEvent | before a heal applies; `Cause`: `Hit` (healing hits, held one step), `Regeneration` (the game's health_recharge, held one step), `Attribute` (changeAttributeValue: potions/food/skills adding Health directly; reported the Pre after it applied), `Kiln` (inside `Combat.Heal`) | `Amount`, `Cancel()` (Attribute: Kiln takes the heal back / corrects it, never below 1 for a living entity) |
| `EntityKnockbackEvent` | LivingKnockBackEvent | a held hit with a push-back or hit reaction is about to apply (after its `EntityHurtEvent`; none when that was canceled), and inside `Combat.Damage` for Kiln hits with `PushBack`/`Reaction` | `Strength` (0..255), `Direction`, `Reaction`; `Cancel()` = no push-back, no stagger (the damage still applies) |
| `EntityFallEvent` | LivingFallEvent | fall damage is about to apply (the game's fall_damage_infliction; held one step) | `Damage` (0 cancels), `Cancel()` = no damage, no hard-landing stagger; `Distance`, `DamageFraction`, `Lethal`. God-mode entities arrive canceled |
| `EntityDamagedEvent` | (after LivingDamageEvent) | the Pre after a hit changed an entity's health | observe only: before/after/max, `KillingBlow` |
| `EntityDeathEvent` | LivingDeathEvent | the Pre after the game's death program marked the actor dead | observe only (see below). `Killer`/`Source` = the killing blow's, `Origin` Game/Kiln/Other |
| `EntityDropsEvent` | LivingDropsEvent | a loot container appeared within 4 m of a corpse within 30 s of the death (about 1 s after it appeared, filled) | `Cancel()` = the container is destroyed; change items with Kiln.Items on `Container` |
| `PlayerRespawnEvent` | PlayerRespawnEvent | a dead player is alive again (respawn or revive) | observe only |

Fields of the hit events: `Target` (the creature, not its weakspot), `Part` (what the hit landed on), `Attacker` (root
source: the player for player hits), `Source` (weapon/projectile), `Flags` (`HitFlags`: Blocked, Crit, Weakspot,
Backstab, KillingBlow, Healing, ...), `DamageTypes`, `Reaction`, `Origin`, `IsPlayer`, `ImpactId`, `Position`, `Step`,
`HealthBefore`, `HealthMax`.

**Timing cost of hurt/heal handlers.** Game hits are produced and applied inside the same sim step on worker threads,
where mod code cannot run. While anything subscribes to `EntityHurtEvent` or `EntityHealEvent`, kiln.combat therefore
holds every entity hit for one step: it applies about 41 ms later than vanilla on a dedicated server (one step on a
listen server). Without subscribers hits are untouched. **With entity filters (dev.27)** only the hits that name a
watched entity wait: when every hurt, heal and knockback handler subscribed with `Events.On<T>(handler,
EventFilter.ForEntity(...))`, kiln.combat holds a hit only if its target, root target, source or root source is one of
the watched entities, and every other hit applies at once, untouched (`hold_filter`, appended to the table; capability
`combat.hold_filter`; one handler without a filter holds every hit again). A hit is about an entity when it is the hit's
target, the part hit, its attacker or its source: the hold and the events take these four from one place
(`hold_filter.h` `hit_entities`), so a held hit is always one a filtered handler is given. A watched creature's own
attacks are about it too: they come with the player it hit as `Target` and the creature as `Attacker` (the first
guided check in a real game, 13 Nov 2026, got three of those before the player had swung). A handler that wants only
the hits the creature takes tests `e.Target == creature` (or `e.Part` for a weak spot). Kiln's own hits name the same
four: `DamageOptions.Source` is the `Source` of the hurt and knockback events `Combat.Damage` posts in the call, as it is
of the held record (until dev.27 the events in the call named the attacker there). Lab (`mods/tests/event-filter-test`, one
watched doll): `a hit on another creature applies ... and is not held: hits held 0 -> 0`, `a hit on the doll ... is held
for the watcher: hits held 0 -> 1`, `control: with a handler without a filter the other creature's hit is held too`,
`a hit the doll deals is held and reported to the doll's handler, Target the other creature`, `a Kiln hit with a source
reaches a handler filtered on the source, in the call and held alike`; and the game's own hits, a spawned player
character's sword swing on eight watched Vukahs round it and their attacks on it (lab 2026-11-13, server 26430253):
`the game's melee hit names the Vukah it landed on as Target (and the character as Attacker): target 2568 part 2568
source 2550`, `a watched Vukah's own attack names the character as Target and the Vukah as Attacker: target 2550 part
2550 attacker 2564 source 2564 ... origin Game` (25 PASS). `AsGameHit` sends a Kiln hit through the same hold (it is
reported like a game hit, not inside the call). Hits on one swing/projectile that the engine would merge (same
target and impact) are reported once; the decision applies to all of them. Repeat hits of an impact that already hit
the target (which the engine skips) are not reported (`CombatStatus.RepeatHits`). A target in god mode
(`SetInvulnerable`) is the exception: kiln.combat drops the hit before the game records that the impact touched it, so a
swing that stays on it is reported again in each step (lab: one sword swing, impact `0x01000002`, four hurt events in
steps 462 to 465 on an invulnerable Vukah, one event on the same Vukah out of god mode); count by `ImpactId` there.

**Knockback, falls and regeneration.** A knockback event comes from the same one-step hold as the hurt
event (subscribing to `EntityKnockbackEvent` alone turns the hold on). Fall damage never was a hit: the game's
`fall_damage_infliction` writes Health directly, so kiln.combat holds the fall itself (its damage fraction is zeroed
when the program reaches the entity, reported in the next Pre, then applied by the game's own code in the step after
with the decided amount); clients see a fall's damage one step later. Regeneration (`health_recharge`, only players
have its `HealthRecharge` component on this build) is held the same way: the whole points the game would add are
withheld, reported, and added in Pre (no later than vanilla for clients: health_recharge runs after the Health
replication). Attribute heals (`changeAttributeValue`) are reported after the fact. Without subscribers none of this
runs (the mid hooks test one flag).

**Death is observe-only.** By the time the game marks an actor dead, its own consumers (XP, quests, spawners, loot)
react to it; there is no safe engine point to undo that. To keep something alive cancel or lower the lethal
`EntityHurtEvent` (`e.Lethal`) or use god mode. Deaths without a hit (fall damage, `SetHealth(0)`, drowning) have
`Killer` None and `Origin` Other. Note: the game's own EntityDiedEvent is sent only once the actor's death sequence
ran, which needs a player near; Kiln reports the dead mark itself, so its event also comes for creatures killed far
from players (and only once per death).

**Drops** are found by watching the corpse (entities of a `LootContainer_*` template with the game's `DestroyOnLoot`
tag near it, or the corpse itself once it turned into loot; anything else nearby, such as a player's grave, is never
reported or touched). The game's loot spawn itself needs the death sequence (a player near); verified in the lab
with a stand-in container.

```csharp
[Subscribe]
private void OnHurt(EntityHurtEvent e)
{
    if (e.IsPlayer && e.Lethal) e.Amount = Math.Max(0, e.HealthBefore - 1);   // players survive with 1 hp
    if (e.Attacker == boss) e.Amount *= 2;
}

[Subscribe]
private void OnDeath(EntityDeathEvent e) => Log.Info($"{e.Victim} killed by {e.Killer} ({e.Origin})");

[Subscribe]
private void OnDrops(EntityDropsEvent e) { if (noLootHere) e.Cancel(); }
```

```csharp
[Subscribe] private void OnFall(EntityFallEvent e) { if (e.IsPlayer && InSafeZone(e.Position)) e.Cancel(); else e.Damage /= 2; }
[Subscribe] private void OnKnockback(EntityKnockbackEvent e) { if (e.Attacker == boss) e.Strength = 255; }
[Subscribe] private void OnHeal(EntityHealEvent e) { if (e.Cause == HealCause.Regeneration && InCombat(e.Target)) e.Cancel(); }
```

**`Health.Dead`** is the game's dead state (Actor state 7 through the game's own state test, `combat/ActorStateTest`,
which honours the actor's override layer) as the game's `death` program will leave it at its next run: that program
marks an actor dead while Health < 1 and alive again once Health >= 1, once per step. So `SetHealth(e, max)` or `Heal`
of a dead entity reads back alive at once, `SetHealth(e, 0)` reads back dead (dying), and the game flips its own mark
one step later. `Dead` is not the raw mark, which lags any health change made outside that program by a
step (`abt heal` on a host player that had just died would answer `413/413 dead`). Lab evidence:
`mods/tests/hosted-test` ("dead-state check": `Heal of a dead entity: +119, reads 119/119 (game mark True)`).

## In a game client
- kiln.combat works on the hosted server world (`api->server_world()`); impact programs and Health are server-only, so
  its hooks only see the server world's hits.
- Per hosted world: at `KILN_WORLD_STOPPING` of the server world (and, without world events, when a new server world
  starts) held hits and damage requests are dropped (status Dropped), deaths, kills, corpses, falls, regeneration and
  attribute-heal records and god-mode holds are forgotten (entity ids repeat across worlds), and the `ImpactSystem` /
  `AttributeRegistry` are released; the next world binds its own at its first Pre. kiln.log: `server world closed (the
  game is closing it): N held hit(s) and N damage request(s) dropped, N god-mode hold(s) and N tracked entit(ies)
  forgotten; waiting for the next server world`. A mod that wants god mode in the next hosted world sets it again
  (server mods are loaded anew per hosted world anyway: docs/kiln/client.md, "Mods and sides").
- Boot log: `actor state: +0xbf8 through the game's state test (override flag, on/off layers resolved)`; without
  `combat.actor_states` the raw bits are read (`raw bits (state test unresolved)`).

## Native (other services)

`api->service("kiln.combat", 1)` in `on_ready`. Table: `health`, `set_health`, `heal`, `damage` (a `KilnDamageRequest`;
flags `KILN_DAMAGE_FORCE_INTERRUPT`, `_SILENT`, `_AS_GAME_HIT`, `_KILL`), `hit_status`, `set_invulnerable`,
`invulnerable`, `release_owner`, `set_listener`, `status`. One listener (the managed Kiln.Combat) gets every event
in Pre as an array of `KilnCombatEvent` it may modify (HURT/HEAL amount + canceled, DROPS canceled) and returns the
kinds it wants next (`KILN_COMBAT_MASK`); asking for HURT, HEAL or KNOCKBACK turns the one-step hold on. Later in v1:
`KILN_COMBAT_KNOCKBACK` (7, writable `push_back`/`direction`/`reaction`), `KILN_COMBAT_FALL` (8, writable `amount`,
`fall_distance`, `fall_fraction`), HEAL `cause` (`KILN_HEAL_*`); `KilnCombatEvent` grew to 144 bytes (fields after
`step`; the managed listener checks `size`), `KilnCombatStatus` grew (falls/regen/attribute flags and counters; the
service accepts the older size). Sigdb features `combat.falls`, `combat.regen`, `combat.attr_heals` (mid hooks; boot
log `mid hooks: fall_damage_infliction on, health_recharge on, changeAttributeValue on`).

**Attributes (v2, appended after `status`; check `header.size >= offsetof(KilnCombatApi, attribute_add) + sizeof(void*)`):**
`attribute_ids(kind, KilnAttributeIds*)` (any thread; `KILN_ATTR_HEALTH/STAMINA/MANA/BODY_HEAT/OXYGEN` to the game's
current, minimum and maximum ids, the component hash and whether the game's own formula clamps),
`attribute_get(entity, id, KilnAttributeValue*)`, `attribute_set(entity, id, value, out)` and
`attribute_add(entity, id, delta, out)` (tick thread). `id` is the game's attribute id (EMF's `EMF_ATTR_*` numbers are
the same ids), any id of the five components for `get`, a pair's current id for `set`/`add`. `out` (optional on
set/add) = the pair after the write: `value`, `current`, `min`/`max` with `has_min`/`has_max`, `current_id`, `max_id`.
Results: `INVALID_ARGUMENT` (an id Kiln does not know, or set/add on a minimum, maximum or computed id), `NOT_FOUND`
(no such entity, or no such component or not set up), `UNAVAILABLE` (the resource, kiln.types or kiln.assets was not
found on this build, or no world), `WRONG_THREAD`. Writes clamp to `[min, max]` (min: the pair's minimum attribute, else
0) and go through the game's attrAdd, whose formulas re-run. Health delegates to `set_health`. Boot log:
`attributes: Stamina: component keen::ecs::Stamina (0x92410664), current 0x04B6AA8B, min 0x519C89A1, max 0xF443C410,
8 slots, the game's formula clamps current to [min, max]` for each pair, then `attributes: on (5 pairs ...)`. Evidence and
how the ids are found: `docs/re/combat.md` "Attributes". Host test: `native/services/combat/tests/run.sh`.

## Diagnostics
- `Combat.Status`: hits seen/held/released/canceled/modified, Kiln hits, deferred, god blocked/restored, deaths,
  game EntityDiedEvents, drops, repeat hits not reported, events delivered.
- kiln.log: `holding entity hits for the listener (interest 0x..)` / `no longer holding ...` when the hold switches,
  `first damage() request ...`, `first EntityDiedEvent read ...`.
- Lab switch: an empty file `kiln/data/kiln.combat/watch-all` makes RESPAWNED cover every revived victim, not only
  players (used by combat-test; do not ship it).

## Lab test
`dotnet build mods/tests/combat-test -c Release -o $KILN_OUT/mods/combat-test --artifacts-path $KILN_OUT/dotnet`,
deploy, `touch <lab>/kiln/data/kiln.combat/watch-all`, start the lab; `grep combat-test kiln/logs/kiln.log` ends with
`summary: N PASS, 0 FAIL`. It spawns transient Vukahs 40 m from the flame altar and removes them at the end. The
attribute part spawns `Enemy_Vukah_Melee` (health, stamina, oxygen), `Player_Vukah_Melee` (+ mana) and `AutomatedPlayer` (all
five, body heat included) 14 m or more away, and for each attribute sets, adds, clamps and reads it back, checks the replicated
copy (`NetworkStamina`, `NetworkPlayerAttributes`) and the refusals (an unknown id, a maximum or minimum id, an entity
without the attribute, the wrong thread). Host test of the ABI layout and the attribute-resource parser (no game needed;
with the game's resource when `KILN_GAME_DIR` and `KILN_SERVER_EXE` are set): `native/services/combat/tests/run.sh`.

## Lab results (mods/tests/events-test, player-free)
- Knockback: `knockback event of a held hit: strength 100, reaction Medium, direction <-1, 0, 0>, after its hurt
  event (1)`; changed `strength 7, Heavy: applied with 7, Heavy` (read back from the DAMAGED event, i.e. the record
  the engine applied); canceled `the damage applies (119->116) without push-back (0) or reaction (None)`;
  `Combat.Damage knockback 50 -> 42: applied 42 Light`; no event without push-back/reaction; none after a canceled hurt.
- Falls (DynamicFallDamage fraction written on a spawned Vukah; the game's infliction applies it): `fall event:
  fraction 0.25, damage 30 (expected 30) ... health 119 -> 89`, `canceled fall: health 119 -> 119` (and the game's
  applied mark -0.5 left), `changed to 7: health 119 -> 112`, `applied once`, `lethal fall: 119 -> 0`, god mode
  arrives canceled.
- Regeneration: hook installed; no entity in the copied world (130 with Health, 20 spawned creature/NPC/animal
  templates) has `HealthRecharge`: players only, client checklist. Attribute heals: hook installed, no trigger without
  a player.
- Summary `events-test: INFO summary: 45 PASS, 0 FAIL`; `reload-test events-test 5`: `5 reloads, 0 failures, avg 12.53
  ms, max 21.36 ms, 0 leaked context(s)`. Regression on the same build: combat-test 48 PASS / 0 FAIL (watch-all),
  gameplay-test 49 / 0 (with the Claims sample's lab claim), api-test 74 / 0, chat-test 50 / 0.

## Manual test
1. Fall from a height with a handler that halves `EntityFallEvent.Damage`: half the damage, the health bar drops once
   (one step late), the hard-landing stagger plays; canceled: no damage and no stagger, nothing odd on the client.
   `Distance` matches the height.
2. Get hit by an enemy with a handler that sets `EntityKnockbackEvent.Strength = 0` / `Reaction = None`: no push, no
   stagger; `Strength = 255`: a stronger push (does the client show it, or correct it after the fact?).
3. Lose health and wait: `EntityHealEvent` `Cause Regeneration` with small amounts; canceled: no regeneration on the
   HUD. Drink a healing potion / eat: which `Cause` arrives (Hit, Attribute, Regeneration through a buff)? Canceled
   Attribute heal: the health goes back down a step later (visible flicker).

## The contract (dev.27)

`Combat` and `Attributes` follow [the API contract](../modding/contract.md). Server side only (`[ServerOnly]`).

- **Capabilities.** `kiln.combat` and its sigdb features (`combat.actor_states`, `attr_heals`, `deaths`, `falls`,
  `health`, `hits`, `regen`), lowered by Kiln.Combat at its start when the service is not loaded; `combat.hold_filter`
  (static) and `combat.attributes` (session: the attribute functions and the game's attribute resource), both reported
  at Kiln.Combat's start, the first service tick, before a mod calls anything.
- **Errors.** Without the service every call throws `KilnUnavailableException` naming `kiln.combat` (a
  `NotSupportedException`, as before); `Attributes` by attribute id on an older kiln.combat names `combat.attributes`.
  **Not changed:** a refusal of the native call (`SetHealth`, `Heal`, `Damage` on an entity without health, no world)
  keeps its `InvalidOperationException`, and `Attributes.Set`/`Add` by `GameAttribute` before the attribute resource
  is read keep theirs: a `catch (InvalidOperationException)` written before would not catch the new type.
- **Results.** `HitResult` implements `IKilnResult`: `Ok` is `Applied`; `Reason` says why a hit was dropped or is
  still pending.
- **Events.** `CombatChangedEvent` (new) for the writes that had none: `SetHealth` (`Health`), `SetInvulnerable`
  (`Invulnerable`, only when the entity's god mode changed, not while another mod still holds it) and the attribute
  writes (`Attribute.Stamina`, ...), with the value before and after; only when the value changed, and a write made
  by a handler while the event is dispatched is posted at the next step ([contract](../modding/contract.md#3-change-events)).
  A mod's unload that ends god mode it held posts `Invulnerable` 1 -> 0 with that mod as `Origin`.
  `Abilities.Attributes.Add` posts the same event. Hits and heals keep `EntityHurtEvent` and `EntityHealEvent`.
- **Listener.** Kiln.Combat starts at the first service tick for its rows; its hit listener (with which kiln.combat
  holds hits a step for handlers) starts at the first use, as before: a call into `Combat`, or a subscription to one of
  its events. A handler of every event (`KilnEvent`) is not a use.
- **Threads.** `[TickThreadOnly]` on every call that touches an entity; `Available`, `HitsAvailable`, `Status`,
  `Attributes.Available` and `Attributes.Ids` are `[AnyThread]`.
- Tested offline (`managed/tests/Kiln.Contract.Tests/Group7ContractTests.cs`): rows, exceptions, attributes,
  `HitResult` as a kiln result, and the writes with subscribers on a fake kiln.combat table. No lab run in dev.27 has
  watched `CombatChangedEvent` against a real kiln.combat.
