# kiln.combat

Health, damage, healing, kills and god mode on the server world, and Forge-style combat events (hurt, heal, damaged,
death, drops, respawn). Everything goes through the engine's own paths: health writes through the attribute system
(the game copies Health into the replicated `NetworkHealth` every step), damage as a real engine hit (health change,
killing blow, hit reaction, damage number on clients, kill credit / XP for the attacker).

| Layer | File |
|---|---|
| C table | `native/core/abi/services/kiln_combat.h` (`kiln.combat` v1) |
| Native | `native/services/combat/` → `kiln/services/kiln-combat.dll` |
| Symbols | `sigdb/symbols/server.d/combat.json` (features `combat.health`, `combat.hits`, `combat.deaths`, `combat.actor_states`) |
| Managed | `managed/services/Kiln.Combat/` → `kiln/host/Kiln.Combat.dll` (`Kiln.Combat` + the event classes) |
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
  (no HitEvent: no reaction, number or credit), `Source`, `AsGameHit` (see below).
- The engine applies at most 4 different impacts to one target per step; Kiln keeps further Kiln hits on a target for
  the following steps (`CombatStatus.HitsDeferred`), so many hits in one step all apply, a few steps later.
- Dropped: canceled by a handler, the target is invulnerable, gone, or has no `CombatFlags.handleHits` (props).
- God mode: damage hits are dropped before they apply (no reaction either); health lost any other way (fall damage,
  drowning) is restored at the next Pre, before the game's death check; `SetHealth` cannot take a god entity below 1.
  Healing still works. Several mods may hold it; it ends when the last one lets go or unloads.

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
listen server). Without subscribers hits are untouched. `AsGameHit` sends a Kiln hit through the same hold (it is
reported like a game hit, not inside the call). Hits on one swing/projectile that the engine would merge (same
target and impact) are reported once; the decision applies to all of them. Repeat hits of an impact that already hit
the target (which the engine skips) are not reported (`CombatStatus.RepeatHits`).

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
`summary: N PASS, 0 FAIL`. It spawns transient Vukahs 40 m from the flame altar and removes them at the end.

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
