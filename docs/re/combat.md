# kiln.combat: engine evidence (server svn 1285601)

Build: dedicated server, Steam build 26404334, svn 1285601, exe sha256 `c5c9fe50…`, image base `0x140000000`. Addresses
are RVAs. Sources: EMF 0.3.3 (MIT) `docs/engine_parts/combat.md` (build 1269579) and `re_attributes_menu.md` Part 1
(1273043), re-found on this build with capstone (`tools/sigport/peimg.py`), the program list parsed from
`RegisterAllPrograms`, and Ghidra 12.1.2 headless decompilation (no auto-analysis) of the functions named below; then
checked live in the `combat` lab (a copied world, no player connected, test mod `mods/tests/combat-test`).

Everything below that the code depends on is captured at boot by `sigdb/symbols/server.d/combat.json` (patterns with
typed holes). The same file resolves on the old build 1273043 with that build's addresses
(AttrFind `0x986f90`, ApplyHit `0x18b8c0`, HitConclusion `0x190670`, AllocPendingHit `0x9819c0`, death `0x85bf0`) and
identical layout constants.

Tags: **static** = read in the code at the address given; **live** = seen in the running lab.

## Programs involved (registration order on this build, from `RegisterAllPrograms` `0x245960`)

| # | Program | Update | Flags | Access (abridged) | Role |
|---|---|---|---|---|---|
| 38 | `death` | `0x89660` | 0 | read Health, PlayerState?; write Actor, ActorInput; sys-read AttributeRegistry; events-out EntityDiedEvent | Health < 1 -> Actor dead state; EntityDiedEvent (see below) |
| 248 | `clear_hit_candidates` | `0x195c80` | 0x4 | sys-write impact::ImpactSystem | empties the pending list (`0x98d760`), moves the overflow pool in |
| 253 | `actor_basics` | `0x76630` | 0 | events-out EntityDiedEvent | second EntityDiedEvent source |
| 390 | `fall_damage_infliction` | `0x8a3c0` | 0 | write Health | fall damage without a hit (mid hooks, see "Fall damage") |
| 417 | `combat_experience_source` | `0xf17d0` | 0x4 | events-in EntityDiedEvent, HitEvent; other-write ExperienceSource | kill credit / XP from HitEvent.rootSourceId |
| 454 | `trigger_knowledge_on_kill` | `0x1bd4e0` | 0x4 | events-in EntityDiedEvent | the events-in queue reader Kiln copies |
| 510 | `impact_simulation` | `0x197cb0` | 0x1200 | sys-write ImpactSystem | impact graphs; the ApplyDamage native queues hits |
| 511 | `impact_hit_conclusion` | `0x195e00` | 0x4 | sys-read BasicPlayerInfos, ColliderSystem, AttributeRegistry, ImpactRegistry; sys-write TrackingSystem, WaterSimulationServer, ImpactSystem; other-write Health, Mana, DynamicOnDestroy, Stamina; events-out HitEvent, NoiseEvent | applies the pending hits |
| 524 | `network_health_attribute` | `0x8bf20` | 0 | read Health; write NetworkHealth | copies Health into the replicated NetworkHealth |
| 672 | `health_recharge` | `0x57190` | 0 | write Health | regeneration (no hit; mid hook, see "Regeneration") |

## Health attributes

| What | Value | Where | Tag |
|---|---|---|---|
| `attrFind(out, id, registry)` | `0x991820` | pattern `89 54 24 10 53 48 83 EC 20 48 8B D9 48 8D 54 24 38 49 8D 48 {68}`: `lea rcx,[r8+0x68]` = the registry's id map; result `{u8 err; u32; AttrInfo* info}`, info `+0` rootId, `+0xC` u16 slot | static |
| `attrAdd(comp, registry, delta, id)` | `0x190810` | rel32 of the call in applyHitToEntity's Health block (`44 8B 0D … 49 8B CE E8`); `slot += delta`, re-runs the root's formulas, no clamp, no markWritten | static |
| Health component | hash `0xDC101091`, 40 B: `+0` rootId, `+4` u16 storage offset, `+6` flags (bit 0 initialised), `+7` count, `+8` 8 x s32 | death program `0x89660` and applyHitToEntity use exactly this | static |
| current / max ids | `0x8EB84995` / `0xDC8358BE` | immediates in death (`mov edx,0x8eb84995`) and applyHitToEntity | static |
| registry | singleton `impact::AttributeRegistry` (`api->singleton`), the object attrFind takes | death passes its iterator slot; live: `AttributeRegistry 000000000A834BE8` | static, live |
| live | spawned `Enemy_Vukah_Melee`: `GetHealth(A) = 119/119`, `NetworkHealth(A) = 119/119 matches`; after `Damage(A, 10)`: `119 -> 109`, `NetworkHealth(A) follows: 109` two steps later; `SetHealth(A, 59)` -> `NetworkHealth(A) after SetHealth: 59` | combat-test | live |

So an attribute write through attrAdd from the tick thread (Pre or Post, no program running) reaches clients: the
game's own `network_health_attribute` copies it into `NetworkHealth` and marks that written. Health itself is
`server_only` + `do_not_save`.

## The hit pipeline

| What | Value | Where | Tag |
|---|---|---|---|
| pending list | `ImpactSystem + 0x110` {data, count `+0x118`, cap `+0x120`, grow fn `+0x128`}; overflow pool `+0x138` {data, count `+0x140`} | AllocPendingHit `add rcx,0x110` (captured `combat/hits.pool`), the clear function `mov rdx,[rcx+0x120] … mov [rcx+0x118],rdi` (captured cap/count), the conclusion's getter `0x992ed0` (`mov rax,[rdx+0x110]; mov rax,[rdx+0x118]`) | static |
| `AllocPendingHit(sys, kind)` | `0x98c040` | push onto `+0x110`, else onto `+0x138` (type byte not set there); push `0x99bab0` zeroes 0x150 bytes and sets unit scales / quaternion w at `+0x2C..+0x38` and `+0x12C..+0x138` | static |
| record | 0x150 B (3 captures: push `imul …,0x150`, clear, conclusion stride `add r12,0x150`) | first 0xF8 B = HitEvent; `+0x08` target transform, `+0x6C` hit point (float m; applyHit scales by 2^32 for terrain hits), `+0x78` normal, `+0x84` direction, `+0x90` rootSource, `+0x94` target, `+0x98` flags (2 captures), `+0xA0` rootTarget, `+0xA4` source, `+0xA8` permission source, `+0xBC` healthChange (captured), `+0xC0` staminaChange, `+0xC8` aggro, `+0xD4` impactId, `+0xE0` u16 reaction, `+0xE2` push-back, `+0xF0` target faction, `+0x108` source transform, `+0x140` mana cost, `+0x144` kind (captured; 0 entity, 1 terrain, 2 building), `+0x14C` send HitEvent, `+0x14D` ignore consecutive | static |
| `impact_hit_conclusion` update | `0x195e00` | reads the list once at its start (`0x992ed0`), checks existence (`0x6451f0`), the consecutive-hit record (`0x994b00`, with `+0x14D`), mana (`+0x140`), Destructible minimum, then dedupes per root target (`rootTarget` if set, else target) and impactId keeping the largest abs(healthChange) | static |
| per-target impact map | capacity **4** (`FUN_140194a40(map, alloc, 4, 0)` per root target; root-target map 0x100) | static; **live**: 40 hits per step on 7 targets with distinct impact ids -> 2,252 of 3,200 applied (= 7 x 4 per step + rest), every extra hit reported `Dropped`. kiln.combat now defers a target's 5th+ Kiln hit to the next step: `3200 applied` with `HitsDeferred` > 0 | static, live |
| `applyHitToEntity(bool* propagate, EntityRecord* handle, ctx, HitCandidate* copy)` | `0x190fb0` | called 3 times by the conclusion (`0x196a19`, `0x196cee`, `0x196f10`), always on a **stack copy** of the record whose `+0x94` the conclusion rewrites with each ImpactHierarchy parent (`mov [rbp+0x2d4],eax` at `0x196a6c`). Returns early unless `CombatFlags.handleHits` (+2); `*propagate = CombatFlags.propagateHits` (+1) and is initialised by the caller (`mov [rbp+0x3e8],dl` at `0x1969bc`). Health block: `attrFind(0x8EB84995)`, `attrAdd(delta)`, killing blow when `> 0 -> <= 0` (`or [rbx+0x98],0x2000` at `0x1919d5`); Destructible without Health gets one through a kind-3 add (`0xdc101091`, size 0x28) | static |
| `alreadyHit(sys, impactId, target)` | `0x994b00` (rel32 of the conclusion's call at `0x1962d9`, captured) | impact instance (`0x9932f0`) -> its hit set (`+0x40`) lookup; pure reads. Kiln calls it in Pre for held hits with `+0x14D` set and does not report the repeats the conclusion will skip (not exercised in the lab: Kiln's own hits never set `+0x14D`) | static |
| entity handle | `0x64e480(ctx, id)` = the EntityRecord* from the entity map (`storage+0x148`) | static |
| `CombatFlags.immune` | not read by applyHitToEntity (only `+1` and `+2` are) | static |

### Hook points Kiln uses
- **`impact_hit_conclusion` update (`0x195e00`), before the original.** It is a SYSTEM program that sys-writes the
  ImpactSystem, so nothing else touches the list while it runs (clear #248, prepare #507, simulation #510 and cleanup
  #514 are serialised with it). Kiln (worker thread) moves the step's entity hits (`kind 0`) out of the list when they
  must be shown to managed handlers first, appends the hits decided in the previous Pre (`AllocPendingHit` + a 0x150 B
  copy) and its own damage() hits. Pushing in Pre or Post would be wiped by `clear_hit_candidates` at the start of the
  step (EMF's finding; the order is confirmed by the program list above).
  - live: `held game hit, handler None: delta 10 (want 10) … result Applied after 1 step(s)`, `Double: delta 20`,
    `Cancel: delta 0`; a held record copied out and re-injected one step later applies exactly like the original:
    the round trip is safe for the record (plain data: the push zeroes it, the tail +0xF8..+0x150 holds only scalars).
- **`applyHitToEntity` (`0x190fb0`).** One call per entity whose health the hit changes; the target is `copy+0x94`.
  Kiln returns without calling the original for damage on invulnerable entities (no health change, no HitEvent), and
  reads Health before/after (the conclusion owns other-write Health) for the DAMAGED event, the killing blow's attacker
  and damage() results.
  - live: `Damage(A, 10, attacker B): hit 2542: 119 -> 109`; `god mode drops a hit: Dropped, health 119/119, blocked 1`;
    `EntityDamagedEvent after the hit: 2542 -10 by 2543 119->109 Kiln`.
- No interception is possible on the tick thread: hits are produced (impact_simulation) and applied (conclusion)
  inside the same sim step, on worker threads. So handlers that change or cancel game hits need the one-step hold.

### Kiln hits (damage / kill)
A Kiln hit is a HitCandidate built like the game's (unit transforms, the target's CurrentTransform at +0x08, the
attacker's at +0x108, direction attacker -> target, rootSource/permission = attacker, rootTarget = target, a unique
impact id `0xCBxxxxxx`, `+0x14C` = 1 unless silent). It is "true damage": the conclusion does not re-run armour,
susceptibility, crit or block (that happens in the ApplyDamage native before queuing). Kill = healthChange of minus the
target's health read inside the conclusion. Kill credit: `combat_experience_source` reads the HitEvent's rootSourceId
(player ids get XP; client checklist). live: `Kill(A, attacker B): hit 2542: 119 -> 0 (killing blow)`, then
`EntityDeathEvent: victim 2542, killer 2543, source 2543, origin Kiln … 45 ms after the kill`.

## Death

| What | Value | Where | Tag |
|---|---|---|---|
| Actor dead bit | `Actor + 0xBF8` bit 7 (2 captures in `death`: `or qword [rax+0xbf8],0x80` / `and …,~0x80`) | `0x8981b` | static |
| state test | `0x3b7a0(actor, n)`: bit n of `[+0xBF8]` (or, with flag `+0x1B1` bit 0, `([+0xBF8] | [+0xBD0]) & ~[+0xBD8]`) | static |
| `death` per entity | health < 1 and not in state 12: if not dead, set bit 7 and `Actor+0xEAA = 1` (event pending), skip the send. **EntityDiedEvent is sent only when the actor is in state 12, or once health >= 1 again** (then the dead bit is cleared too). Players: extra respawn bookkeeping at `+0xEA8/+0xEAB/+0xEAC` | decompiled `0x89660` | static |
| live | a Vukah killed 40 m from the base with no player near: dead bit set the next step (`A is dead: 0/119 dead`), its child entities (weakspot head, hookshot pull, VFX link) removed, **no EntityDiedEvent and no loot for 12 s**. The only game EntityDiedEvents seen came when a dead Vukah got health back (`first EntityDiedEvent read: entity 2545`, after `SetHealth(D, max)`) | combat-test | live |
| state test (svn 1287369) | `hasState(actor, bit)`: `m = 1 << bit; v = [actor+0xBF8]; if ([actor+0x1B1] & 1) v = (v \| [actor+0xBD0]) & ~[actor+0xBD8]; return (v & m) == m`. 0xBD0/0xBD8 are an override layer (forced-on / forced-off bits; written from data-driven state ops by hash at server `0x34e00`). Pattern unique on server 26430253 (`0x3b7a0`), client 26430625 (`0x21e4f0`) and both previous builds, offsets identical; `combat/ActorStateTest` (+ a rel32 witness from the death program's `mov dl,7; call`) | server `0x3b7a0`, client `0x21e4f0` | static |
| `death`, exactly | Health > 0: `and [actor+0xBF8], ~0x80` (always), then send a pending EntityDiedEvent (`+0xEAA`). Health <= 0: `hasState(12)` → skip; `hasState(7)` → skip; else `or [actor+0xBF8], 0x80`, `+0xEAA = 1`; players (an optional component with `+0x34` bit 0, or none) continue to the respawn bookkeeping and the death sequence (`+0xEAC` via ActorInput `0x369d0`) and get their EntityDiedEvent in the same step | `0x89660` | static |
| live | the dead mark lags any health write outside `death` by one step: `SetHealth(0)` → game mark False until the next step; `SetHealth(max)` / `Heal(+119)` of a dead Vukah → game mark True in that step, False one step later (`the game clears its mark 1 step(s) later`). In a client: the host player died (`first EntityDiedEvent read: entity 1`), `abt heal` in the next command answered `health 413/413 dead` from that raw mark | hosted-test, client logs | live |

State 12 is set by the actor's death sequence, which, like AI and physics, only runs for actors near a player (spawn
doc: no simulation without a player). Kiln therefore reports a death when the dead bit appears: for candidates it saw
coming (killing blows in applyHitToEntity, set_health to 0, the 16 player slots polled each Pre) in the Pre right
after the death program ran, and for anything else when the game's EntityDiedEvent arrives; each death once (the
report is forgotten once the actor is alive again).

### EntityDiedEvent queue (events-in)
Read by Kiln's own SYSTEM program `kiln_combat_deaths` (registered through kiln.world with events_in EntityDiedEvent;
the iterator slot holds the queue, as for `trigger_knowledge_on_kill`). Layout captured from `0x1bd515`: `u32 count`
at `+0x18`, block base `+0x30`, first block index (u16, 1-based) `+0x40`, blocks of `1 << 0xB` bytes, next index at
`block+2`; records packed from `block+8`, a 16-byte record never crosses the block end (`cmp ecx,0x7f8`). The sender
`0x5fcfc0(sender, hash, size, u16)` writes an 8-byte header and returns the payload; `death` writes the dead entity id
at payload `+8`, which the reader takes at `block + 8 + offset + 8`. live: the id read (`2545`) is the entity that died.

## Loot at death (what the lab could and could not show)
- The game spawns enemy loot from the death sequence (EMF re_content.md 4.1, inferred: `actor_spawn_entity` reads
  RandomLoot*; `LootContainer_Enemy_*` templates carry DestroyOnLoot, Inventory, DefaultInventory). Not seen in the lab:
  without a player the sequence never runs (`t+12s near the corpse: … +0`).
- Kiln reports drops by watching a corpse for 30 s: entities with `DestroyOnLoot` of a `LootContainer_*` template (one
  kiln.world `find` every 12 steps while a corpse is watched, template names from kiln.spawn) that appear within 4 m, or the corpse itself once it has an Inventory
  (transform_to_loot). live with a stand-in: a `LootContainer_Enemy_AncientConstruct_Critter` spawned 1 m from the
  corpse -> `EntityDropsEvent: victim 2542, container 2558 …, killer 2543`; canceled -> `container exists False`.

## Respawn
Death clears the dead bit only when Health >= 1 again; a respawn or revive restores health, so Kiln watches dead
players and reports RESPAWNED when the bit clears. live (lab switch `kiln/data/kiln.combat/watch-all`, which watches
non-players too): `revived D … respawn event 2545 health 119/119`, and the revived Vukah could be killed again
(`a revived entity can die again: killer 2544`). A real player's respawn: client checklist.

## God mode
Hits: dropped in the applyHitToEntity detour (live `blocked 1`, also for a held game hit). Other losses (fall damage
writes Health directly in #390, after `death` #38 in the same step): restored in the next Pre, before the next death
check (live: `god mode restores a non-hit health loss … restored 1`). A Kiln SetHealth below 1 on a god entity leaves
1 (live: `SetHealth(0) leaves 1, restored to 119/119 god, no death event`), because a value of 0 written in Pre would
be seen by `death` in the same step.

## Knockback (KILN_COMBAT_KNOCKBACK → EntityKnockbackEvent)
- The push is part of the hit: HitCandidate `+0xE2` u8 pushBackForce, `+0xE0` u16 hitReaction, `+0x84` float3 hit
  direction, flag `0x400000` UseHitDirectionForPushBack (EMF). The conclusion copies the record into the HitEvent;
  its readers turn it into movement (enemy01 → `ApplyPushBackEvent` → `enemy_pushback_events` #223 → `Impulse`;
  players' control programs read the reaction). So Kiln edits the held record (all hits of the group) between the hold
  and the re-injection, like the damage amount. live (events-test, AsGameHit Kiln hits on a spawned Vukah, values read
  back from the DAMAGED event built in applyHitToEntity from the record as applied):
  `knockback event of a held hit: strength 100, reaction Medium, direction <-1, 0, 0>, after its hurt event (1)`,
  `changed to strength 7, Heavy: applied with 7, Heavy`, `canceled: the damage applies (119->116) without push-back
  (0) or reaction (None)`, `Combat.Damage knockback 50 -> 42: applied 42 Light`.
- Whether the creature/player actually moves accordingly needs simulation (a player near): client checklist.

## Fall damage (KILN_COMBAT_FALL → EntityFallEvent)
| What | Value | Where | Tag |
|---|---|---|---|
| programs | `fall_damage_detection` #389 `0x89e50` (writes DynamicFallDamage from Actor/Velocity/MovementCollider), `fall_damage_calculation` #390 `0x89cb0`, `fall_damage_infliction` #391 `0x8a3c0`; all write DynamicFallDamage, so they run in this order in one step | descriptors | static |
| DynamicFallDamage | `+0` wasFalling, `+4` fallStartAltitude, `+8` detectedFallDistance, `+0xC` detectedFallDamagePercentage | EMF; calc/infliction reads | static |
| calculation | `if (FallDamage.start >= lethal) pct = 0; else if (dist >= start) pct = clamp((dist-start)/(lethal-start), 0, 1) + pct` | `0x89cb0` | static |
| infliction | per entity: `if (pct <= 0) skip` (`comiss xmm8,[rax+0xc]; jae` at `0x8a4a4`); `hp = max(0, floorf(hp - max*pct))` written straight into the Health slot (no hit, no attrAdd), `attrRecalc 0x52700`; FallDamage `+8` sequence → ActorInput (hard-landing stagger, `0x369d0`); then `pct = -pct` (`xorps xmm6, sign` + `movss [rax+0xc], xmm6` at `0x8a793`), which makes the next calculation produce 0; player deaths: TrackingSystem stat | `0x8a3c0` | static |
| hook points | mid hooks `combat/FallCheck` `0x8a4a0` (`mov rax,[rbp-0x50]`: rbp disp captured as `combat/fall.frame` 0xB0, pct offset `combat/fall.pct` 0xC) and `combat/FallStore` `0x8a793` (the flip store; `combat/fall.pct2`); entity = `CurrentEntityId(r15)`, the call at `0x8a79b` captured as `combat/FallEntityId` and compared with `world/CurrentEntityId`. 1273043: `0x86a30` / `0x86d23` | sigdb | static |

Kiln holds a fall exactly like a hit: in FallCheck a positive pct is recorded and zeroed (the entity is skipped: no
damage, no stagger, no flip); the Pre after reports it; the decision is applied when the program reaches the entity
in the next step: canceled → pct = -orig (the game's own "applied" mark), kept → pct = orig, changed → pct = (amount -
0.5) / max (so `floor(hp - max*pct) = hp - amount`), and FallStore replaces the flipped value by -orig so the next
calculation still yields 0. infliction #391 runs before `network_health_attribute` #525, so clients see a fall's
health loss one step later than vanilla (41 ms). live (pct written into a spawned Vukah's DynamicFallDamage in Pre,
detected distance 0 so the calculation leaves it; the game's own infliction applies it):
`fall event: fraction 0.25, damage 30 (expected 30), distance 0; health 119 -> 89`, `after the fall pct = -0.25`,
`canceled fall: health 119 -> 119`, `canceled fall left the applied mark (-0.5)`, `fall damage changed to 7: health
119 -> 112`, `applied once (no second infliction): 112/119`, `lethal fall: 119 -> 0` (then `0/119 dead`), `god mode:
the fall event arrives canceled, health 119 -> 119`. A real fall (detection from movement) needs a simulated
actor: client checklist.

## Regeneration and attribute heals (HEAL events with a cause)
- `health_recharge` #673 `0x57190` (read Actor, read-opt HealthRechargeMod, write Health + HealthRecharge, sys-read
  BalancingRegistry, AttributeRegistry): skips dead actors (`Actor+0xBF8 & 0x80`), reads Health current/max and four
  regen attributes (`0x95DA4F21`, `0x4C1DAA74`, `0xDEE8D7AC`, `0xA4F5866E`: delay/rate inputs), keeps HealthRecharge
  {`+0` u64 time of the last loss, `+8` float accumulator, `+0xC` last health}; when health dropped it restarts the
  delay; `0x537c0` adds rate × time to the accumulator after the delay; whole points: `acc -= floor(acc)`, then
  `add dword [slot], eax` (`0x57801`) and `attrRecalc`. The same shape (pattern `F3 0F 2C C6 4C 03 C3 41 01 04 88`)
  appears in mana_regeneration, oxygen_update, stamina_recharge, fog_resistance_update. Mid hook `combat/RegenAdd`
  (scoped to `combat/RegenProgram`, the prologue with `mov edx, 0x8EB84995`; 1273043 `0x54131`): the points (eax) are
  recorded and set to 0; Pre re-adds the decided amount with attrAdd (clamped to max). health_recharge runs after
  network_health_attribute, so the hold adds no latency for clients.
- `changeAttributeValue` impact native (`0x180ee0`, registered twice with hash `0x044e4187`; `setAttribute`
  `0x18afe0`, `applyAttributeValue` `0x17c7d0`, `revertApplyAttributeValue` `0x18a9a0`): for Health/Stamina/Mana
  (`cmp ebx, 0x8EB84995 / 0x04B6AA8B / 0x6AA2574E`) it pushes a change record `{+8 entity, +0xC attribute, +0x10
  delta}` onto `ImpactSystem+0x160` (`0x98c0d0`, its only caller) with ebx = attribute, edi = entity, r13d = old,
  r14d = new value. Mid hook `combat/AttrChangeRecord` `0x18113a` (1273043 `0x17bb7a`) records Health gains; Pre
  reports them after the fact and corrects the health if a handler changed or canceled the heal. Which game effects
  use it (potions, food, skills) and whether the record gives clients a heal number: client checklist.

## Open questions
- What exactly sets actor state 12 (death sequence end?) and how long after the dead bit it comes for a simulated
  creature (client checklist: compare Kiln's DIED step with the game's EntityDiedEvent step).
- The real loot spawn path (sequence event -> `actor_spawn_entity` -> queued create `0x5fb340`); a direct hook there would
  give the item list before the container exists.
- Which consumables heal through hits, changeAttributeValue or buffs (client checklist); the ImpactSystem `+0x160`
  attribute-change list's consumer.
- Whether `CombatFlags.immune` is honoured in `applyDamage` (impact native, before queuing): not needed by Kiln.
- Overflow pool (`+0x138`): hits pushed there when the primary list is full are only processed next step; Kiln never
  uses it.
