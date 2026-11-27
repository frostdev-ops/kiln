# Kiln.Pets: the pet framework

Since 0.2.0-dev.32. Managed service `Kiln.Pets`; native safety table `kiln.pets` v1 in `kiln-creature-control.dll`
(`native/core/abi/services/kiln_pets.h`), beside the creature control broker it uses. Design: dossier chapter 19d.

Kiln makes pets possible; it does not decide what a pet is. Kiln ships the mechanisms: durable owned records, the
broker's leases on a pet's body, the lifecycle and its events, the revive channel and its cost receipts, the owner's
view data and the built-in bar, and typed definitions. **Every behaviour choice is the mod's**, declared in its
`PetDefinition` (or `PetBlock`) or decided through the API and events. Where a mod says nothing, Kiln's default is the
safe one: every player protected, a downed pet a bodyless recovery anchor.

Author guide: [modding/pets.md](../modding/pets.md). Sample: `mods/samples/PetsSample`.

## The choices, and where a mod makes them

| Choice | Where | Kiln's default |
|---|---|---|
| Which creature the body is | `PetDefinition.Creature`: a `creatures` registry id (`game:enemy_wildbeast_wolf_earlygame`) or a creature block (`<mod>:<name>`) | none (required) |
| Level and size | `PetLevelPolicy`: fixed, the owner's at grant/tame, the owner's at each summon; scale 0.25..4 | fixed 1, scale 1 |
| How it follows | `PetMovementPolicy`: Kiln's walk-only routes or the game's guard; follow, run and catch-up distances, stuck time, catch-up on/off | routes, 3 m, run past 10 m, catch up past 60 m or 5 s stuck |
| Who it may harm, who may harm it | `PetTargetPolicy`: the hostile creature factions (never Player, AutomatedPlayer, PlayerAlly or FarmAnimal: `Define` refuses them); PvP off, or by the mod's rule (`Pets.SetHostile` pairs) | PvP off; Fogger, Vukah, Scavenger, WildBeast, Skeleton, Cyclops, Drak, Ancients |
| What a downed pet is | `PetDownedPolicy.Outcome` (recovery anchor, absent, released); per pet in `PetDowningEvent` | a recovery anchor where it fell |
| Revive: allowed, channel, range, health, cost, what cancels | `PetRevivePolicy`; a refusal per attempt in `PetRevivingEvent` | allowed, 3 s, 3 m, half health, free, movement and damage cancel |
| How many | `PetLimits` (`Limits`; JSON `limits`): records per character, live per character, live per world | 32, 1, 16 |
| What the owner's HUD shows | `PetHudPolicy` (bar row, anchor marker) per definition; `PetsClient.Configure` per player; or a HUD of the mod's own from `PetsClient.Snapshot` | bar and marker on |
| Who gets a pet | `Pets.Grant` (a reward, a quest, a command); taming (19e) when it lands | none |
| Attack, assist, defend | `Pets.Command(..., PetOrder.Attack, target, ...)`: the mod decides when; Kiln checks the target rule | Kiln never attacks by itself |

Every time on this page and in a definition (the revive channel, stuck and repath times, a new body's 5 s to get ready,
the owner's view's rates) is the game's own world clock. In C# a duration is a `TimeSpan` (`ChannelDuration`,
`RepathInterval`, `StuckAfter`, `PetSnapshot.ReviveElapsed`), as in Npcs and the registries; blocks/*.json keeps numbers of
seconds (`channelSeconds`, `repathSeconds`, `stuckSeconds`). It runs on that clock whatever the server's step rate (a dedicated server
steps 15 to 25 times a second). A paused world's clock stands still. The stuck and no-route times count each step at most
0.25 s: a server stall (a long save, a loaded host) moves nothing, so it is no stuck time and teleports no pet.

Not possible yet, with what it would take:

- **A body left lying when a pet goes down** (a decorative corpse): needs a client-side decorative body that can neither
  act nor be hit, drawn over the recovery anchor, and its appearance checked in a real game: 2 to 4 days. Today a downed
  pet is a bodyless anchor, absent, or released.
- **Flying pets**: wait for riding's flight (walk-only routes now; fly, jump, swim and door segments are refused).
- **Wildlife and farm-animal bodies**: only enemy creatures are admitted (their AI is the one the broker and the guard
  know); a creature of another kind is refused `CreatureUnsupported`.
- **Pets that cross worlds** (the portable `Carried` scope): records are world-local.

## Records

One record per pet, kept per character in the world's save (scope `service/kiln.pets/character/<character>`, staged
and committed with the world's other mod data). A random `PetId`, the definition id, two axes and the owner's wish:

- `PetLife`: Ready, Downed, Released (a tombstone until a committed save holds it, then it goes).
- `PetPresence`: Absent, Live (a body), RecoveryAnchor (a downed pet's spot: no entity, no collision, never a target),
  Retiring.
- `PetTransition`: what is happening (Spawning, Downing, Dismissing, Reviving, Releasing); requests meanwhile are `Busy`.
- `WantsSummoned`: set by Summon, cleared by Dismiss, kept through logout (the next login brings the pet back; a downed
  one comes back as its anchor, never its health).

Saved: id, definition, life, wish, order (an attack order is saved as follow), level, health share, last maximum,
anchor, state revision. Never saved: bodies, leases, targets, transitions, the HUD. A record whose definition is not
defined in this session stays as it is (dormant), never erased. A document of a newer Kiln is kept and never
overwritten (`RecordsUnreadable`); a single record that does not read (or names a pet another character holds) is
written back as it was.

**Revisions.** `StateRevision` rises once per change (not per event); every event of one change carries it.
`DurableRevision` is the revision the last committed save's bytes hold: Kiln reads it out of exactly what each staging
serialized, and the final snapshot staging re-reads every body first, so a pet that took a lethal hit in the step
of the save, or whose down is still retiring its body, is written Downed (and settles with that same revision), never
Ready. A failed save acknowledges nothing (`PetRecordCommittedEvent` says when it moves).

## The life of a pet

| Request on | Outcome | Events |
|---|---|---|
| Ready, absent: Summon | a protected body 2-4 m from the owner at its kept health, following | TransitionChanged, (CommandChanged), Summoned |
| Ready, live: Summon | nothing (idempotent) | none |
| Ready, live: Dismiss | the body retired, its health kept | TransitionChanged, BodyRetired, Dismissed |
| Downed, absent: Summon | its anchor next to the owner: no body, no heal | RecoveryAnchorChanged, Summoned |
| Downed, anchor: Summon | re-anchored only past 3 m | RecoveryAnchorChanged |
| Downed: Dismiss | the anchor hidden, never healed | RecoveryAnchorChanged, Dismissed |
| Downed, anchor: Revive | the channel, then a new protected body | ReviveStarted, Revived (or ReviveCanceled) |
| any: Release | a body retired first; then a tombstone | (TransitionChanged, BodyRetired,) Released |
| a lethal hit, fall or health loss | the guard latches, the body is retired, then the definition's outcome | TransitionChanged, BodyRetired, Downed (, Released) |
| the body gone by itself | down, never rebuilt by itself | BodyLost, Downed |
| protection lost | the body retired | TransitionChanged, ProtectionLost, BodyRetired, Downed |
| logout, world end | requests and channels end; bodies retired; the wish kept | BodyRetired / RecoveryAnchorChanged |

`PetDowningEvent` comes before a down settles: its `Outcome` starts as the definition's and a handler may choose another
for this pet; the down itself cannot be refused. `PetDownedEvent` comes only after the combat body is gone.

## The API

```csharp
[ServerOnly, TickThreadOnly] public static class Pets {
    PetDefinitionResult Define(PetDefinition definition);                     // pets.definitions (any thread)
    PetDefinition? Definition(string id); IReadOnlyList<PetDefinition> Definitions;
    IReadOnlyList<PetSnapshot> Of(CharacterKey owner); IReadOnlyList<PetSnapshot> Of(PlayerRef player);   // pets.records (a player: in game)
    PetSnapshot? Get(PetId pet);
    Task<PetResult> Grant(PlayerRef owner, string definition, PetRequest request);   // in game, as every request
    Task<PetResult> Grant(CharacterKey owner, string definition, PetRequest request); // a character, also offline
    Task<PetResult> Summon(PlayerRef owner, PetId pet, PetRequest request);    // pets.summon
    Task<PetResult> Dismiss(PlayerRef owner, PetId pet, PetRequest request);
    Task<PetResult> Command(PlayerRef owner, PetId pet, PetOrder order, EntityRef target, PetRequest request); // pets.follow (+ pets.fight for Attack)
    Task<PetResult> Revive(PlayerRef owner, PetId pet, PetRequest request, ItemSelection? cost = null);       // pets.downed
    Task<PetResult> CancelRevive(PlayerRef owner, PetId pet, Guid reviveRequest, PetRequest request);
    Task<PetResult> Release(PlayerRef owner, PetId pet, PetRequest request);   // pets.records
    void SetHostile(CharacterKey a, CharacterKey b, bool hostile); bool IsHostile(CharacterKey a, CharacterKey b); // pets.fight
}
```

- **Requests** carry a request id (`PetRequest.New()`, `PetRequest(RequestId, ExpectedStateRevision)`: the one request
  shape of Kiln.Pets and Kiln.Taming, whose `TamingRequest(RequestId, Token)` is the same idea; Kiln.Items' `ItemCosts`
  takes the request id beside the `ItemSelection` it expects); `PetResult.RequestId`, `PetChange.RequestId` and
  `PetSnapshot.ReviveRequest` carry it. A retry with the same id gives the first answer for 10 minutes; the same id for another request is `RequestConflict`; a nonzero `ExpectedStateRevision` that moved on is
  `StaleRevision` (nothing changes). Requests start at the next step and complete when they settled.
- **The error rule:** a bad argument throws `ArgumentException`; an off row throws `KilnUnavailableException` naming it;
  a refusal is `Ok = false` with a reason, and the reason's first word as a value, `PetResult.Refusal` (`PetRefusal`:
  `Busy`, `NotOwner`, `OwnerOffline`, `AlreadyReleased`, `Capacity`, `NoSafeGround`, `BodyNotReady`, `ControlRefused`,
  `ProtectionUnavailable`, `TargetProtected`, `RecoveryNotSummoned`, `OutOfRange`, `CostUnavailable`, `RefusedByMod`,
  `CanceledNoCost`, `CanceledCostCommitted`, `ReviveFailedCostCommitted`, `UncertainCommit`, ...; `Other` for a word a
  later Kiln adds). Decide by `Refusal`, never by the text: `if (r.Refusal == PetRefusal.Capacity) ...`. Kiln.Taming's
  `TamingRefusal` uses the same word for the same meaning. A refusal after a cost was taken carries its receipts
  (`PetResult.Costs`, Kiln.Items' `CostReceipt`): no refund is invented.
- **Revive:** quoted before anything starts (`ItemCosts.Check`, a read: nothing moves); a mod may refuse (`PetRevivingEvent`); the
  channel runs in simulation time (leaving range, the owner leaving or going down always cancel; moving past
  `MaxOwnerDisplacement` and being hurt when the definition says so); at completion everything is checked again, an inert
  preview body is prepared (invulnerable, harmless), the cost is taken exactly once, and only then is the pet Ready and
  Live. A channel cut by a logout or an unload while its debit settles waits for the exact receipt and answers
  `CanceledCostCommitted` with it. The cost goes through the exact-cost adapter (`kiln.item_costs`, Kiln.Items'
  `ItemCosts`, [item-costs.md](item-costs.md)): `ItemCosts.Take` from one slot, all or nothing, under one request id per
  revive operation and world. The `cost` selection (Kiln.Items' `ItemSelection`) must be the owner's plain stack of the
  cost's item holding at least the cost, and only that slot pays; with none, the first slot holding a plain stack of the
  whole cost pays (`ItemCosts.Choose`: a cost split over two stacks is not taken). An uncertain receipt (a fault inside the
  game's change) answers `UncertainCommit` with that receipt and the pet stays Downed.
- **Events:** every change is a `PetChangeEvent` (subscribe to the base for all): Granted, TransitionChanged, Summoned,
  Dismissed, CommandChanged, RecoveryAnchorChanged, BodyRetired, BodyLost, Downed, ReviveStarted, ReviveCanceled,
  Revived, Released, StatsChanged, ProtectionLost. Also `PetDowningEvent` (the outcome), `PetRevivingEvent` (cancelable),
  `PetRecordCommittedEvent`, `PetControlChangedEvent` (the broker's lease states of a pet: a body's final base lease, once
  it is the pet's, comes first with `Before` null), `PetDefinitionChangedEvent`.
  Server tick thread, after the change settled; a handler's own request runs at the next step.

## Bodies, the broker and following

A body is spawned transient (never saved), on the players' side, at its level and scale, and bound in the same tick to
both the broker (a **PetStay preparation lease**) and the native guard (a **Staging** row: invulnerable, harmless, never
a semantic down). It is the pet's only once the broker's same-owner replacement to the final base role is Active
(`PetFollow`, or `EngineGuard` for that movement mode) and its health is initialized (5 s, else `BodyNotReady` and the
body goes). Orders move the base lease (Follow, Stay) or add a `PetCombat` lease above it. A rider's `Ridden` lease (a
mount service, another owner) preempts the pet's leases without a loss; the broker resumes the base lease after the ride
with a new transition sequence; an order during the ride commits with its lease waiting. A lease of the pet's that ends
without its asking is lost control: the body is retired and the pet goes down (never left as a wild creature).
What a body's setup and orders change in kiln.creatures (its faction, level and scale, an attack order's held target)
is the defining mod's change, as every creature change is a mod's: Kiln.Pets makes it as that mod, and a pet no loaded
mod defines gets no body (`BodyRefused`).

Retiring a body quarantines it first (every lease, a rider's too, loses authority at once, its root is denied), gives a
rider two steps to get off, then destroys it; the broker retires its rows when it sees the body gone. Every try of a
dismiss, release or logout retirement reads the wounds again: a down latched in between is a down.

Following (QueryInput): walk-only routes from kiln.queries (Found, reaching the target, every point a walk; never code 0
with no points, never partial), at most 4 requests in flight, a new route past 2 m of owner movement or each
`RepathInterval` (JSON `repathSeconds`); input written only while the broker's `can_drive` says so. **Path clearance is measured, not guessed**
(19d §4.3a, gate C6): the HorizontalSpace/VerticalSpace a route asks for come from an accepted profile per creature,
scale and game build ([below](#measured-clearance-gate-c6)); without one the pet does not walk routes and only catches up
(`ClearanceMappingUnverified`). Catch-up puts the body on validated ground next to the owner (ground under its centre and
4 footprint points, a floor spread of at most 0.5 m, a clear column, no entity origin within 2.5 m but the owner's and
the pet's own; the same check places a summoned body, an anchor and a revived body), and only with the broker's
authority, then checks that it settled (within 1 m after 0.5 s); one that does not settle stops and waits 10 s before
another checked try. Stay holds
with zero input. EngineGuard sends the game's own guard event (Kiln.Creatures' keeper is not run beside it). Attack hands
the body to the game's AI under PetCombat with the target preferred; a target that is gone or dead puts the pet back on
follow.

## Measured clearance (gate C6)

The space a pet's route asks kiln.queries for (`HorizontalSpace`, `VerticalSpace`) is never derived from the body's radius
or height: nothing proves what the game's two numbers mean physically. It is a **profile**, measured on a dedicated lab
server by `tools/pets-clearance` (no game client) and accepted only when the gate passes.

**The gate** (per creature, scale and game build): the lab builds fixtures of building blocks, sealed lanes whose only
walk from the start to the exit room is one measured opening: corridors 0.5, 1 and 2 m wide with 1, 2 and 3 m of
headroom, plus a 1.5 m gap under 3 m and 1.5 m of headroom over 2 m, the game's door-wall doorway (2 x 2.5 m), two
S-bends (1 and 2 m) and a wide open control. The creature is
spawned without an owner and held by Kiln.Pets' own broker leases; for every pair of 0.5/1/2 m and 1/2/3 m the lab asks
for a route through each fixture, and **Kiln.Pets' real follower and mover** walk every accepted route (the body's
`ActorInput` under the broker's `can_drive`), catch-up and teleport off, logging the body's position every step. All
times (traces, waits, the follower's repath and stuck timers) are game seconds from the game's own clock, never a count
of steps: a dedicated server steps its world 15 to 25 times a second (the lab's, about 22). The body's own walker (the
game's guard walk) walks each fixture too, as the comparison. `tools/pets-clearance/profiles.py` judges each walk: a
pass crosses the exit by the body's length without its movement collider meeting a wall or a ceiling closer than its
size allows, without a stall and without a teleport. A pair passes when no accepted route failed or stayed unknown and
the control passed. A passing pair vouches for the body only where the fixtures measured it: a passage in the world is
a whole number of 0.5 m cells, so a gap or headroom one cell narrower or lower than the body that lies between the
widest/highest the pair refused and the narrowest/lowest it was walked through, unmeasured, is one the pair might take
and the body cannot walk. The profile is the passing, vouching pair that walks the most fixtures; on a tie, the larger
vertical and then the larger horizontal requirement, which refuses more of what the fixtures did not measure. A body
wider than the widest measured gap (2 m) or taller than the highest measured headroom (3 m) gets no profile: every pair
it passes walks only the control, which says nothing about the gaps in between. Nor does a body whose walker and
collider Kiln.Pets could not read in the lab.

**Body profiles.** The routes kiln.queries finds do not depend on the creature (the lab's route sweeps were identical
for every creature it calibrated), and the walk is the movement collider's: every body stalled exactly its collider's
radius before an opening it could not pass, and Spider Mama's 2 m collider kept exactly 1 m from both walls of a 2 m
corridor. So creatures with the same body (path walker radius and height, movement collider width and height, the
template's own scale) walk the same. When three or more walking creatures with one body were calibrated at a scale on a
build and every one chose the same pair, `profiles.py` also writes a **body profile** (`"match": "body"`), and Kiln.Pets
applies it to every walking creature with that body (its template finds paths and has no flying component). One
creature of the body without a profile, or with another pair, and the body gets none. A creature profile always wins
over a body profile.

**What the lab measured** (game build 1287369; five sessions, 25 creature-and-scale runs):
- `VerticalSpace` acts as the headroom a route needs. A route is found only where the ceiling is at least that many
  metres up: under 1 m only 1; under 1.5 m only 1; under 2 m and the 2.5 m door, 1 and 2; under 3 m, all three.
- `HorizontalSpace` 0.5 finds routes through 1.5 m and 2 m gaps (the corridors, the door, the 2 m S-bend) and never
  through 1 m or 0.5 m ones. 1 and 2 find only the 5 m control.
- The routes are the same whatever the creature: every creature's sweep gave the same answer for every fixture and pair.
- The walk is the movement collider's. A body that cannot pass stalls exactly its collider's radius short: 0.5 m for a
  1 m collider, 0.375 m for the rat's 0.75 m, 1 m for a 2 m one under a 1 m ceiling, and 0.66 m before a 1.5 m gap for
  a 2 m one (where a 1 m radius meets the opening's edges). The model does not count: the Shroud Stalker's 4 m model on
  a 1 x 2 collider walks exactly as the Vukah. Nor does the pet's scale: at scale 1.5 the collider stays as it was.
- No calibrated body passes under a 1 m ceiling, whatever its collider's height (the rat's is 0.75 m), though
  `VerticalSpace` 1 accepts that route. So 0.5/1 fails the gate for all of them, and 0.5/2 is the passing pair that walks
  the most fixtures. The 2.5 m bodies also stall under 2 m, so their pair is 0.5/3. The 2 m wide bodies stall in the
  1.5 m gap that 0.5 takes, so they walk open ground only (2/3).
- The body's own walker (path walker radius 0, height 2) also gets through some 1 m gaps that 0.5 refuses. There a pet
  catches up instead of walking.
- At scale 1.5 the gate judges the body as a player sees it (a 0.75 m radius), and the bodies keep 0.63 to 0.64 m from
  the 2 m S-bend's corner, at the edge of the 0.65 m the check allows: the hound, the boar and the wolf came out at three
  different pairs, so that body gets no body profile at 1.5.

**Kiln's shipped profiles** (game build 1287369, scale 1). Body profiles, for every walking creature with the body:

| Movement collider (width x height, m; walker 0/2, template scale 1) | Creatures calibrated | Walking templates with the body | HorizontalSpace / VerticalSpace |
|---|---|---|---|
| 1 x 2 (the most common: humanoids, constructs, the Fell and Scavenger enemies) | Vukah, Drak'Dal Chieftain, Fell Scout, Shroud Stalker | 300 | 0.5 / 2 |
| 1 x 1 (the beasts) | early-game wolf, Wolf, Boar, Sabertooth, Scavenger Hound, Hollow Hound, Spider | 31 | 0.5 / 2 |
| 1 x 2.5 | Vukah Bonebreaker and the two other Bonebreaker templates | 3 | 0.5 / 3 |
| 2 x 2 | Spider Mama, Vukah Bonehunter, Drak'Dal Hunter | 8 | 2 / 3 (open ground) |

Creature profiles (each calibrated on its own; a creature profile wins over a body profile):

| Creature | Movement collider (m) | HorizontalSpace / VerticalSpace |
|---|---|---|
| `game:enemy_vukah_melee` (Vukah), `game:enemy_lizardpeopledrak03_elite_melee` (Drak'Dal Chieftain), `game:enemy_fogger_soldier01` (Fell Scout), `game:enemy_fog_predator` (Shroud Stalker) | 1 x 2 | 0.5 / 2 |
| `game:enemy_vukah_melee_elite_armored` (Vukah Bonebreaker), `game:enemy_vukah_melee_elite_unarmored_wip`, `game:base_enemy_vukah_melee_elite_hookpte` | 1 x 2.5 | 0.5 / 3 |
| `game:enemy_wildbeast_wolf_earlygame`, `game:enemy_wildbeast_wolf_hook_ag2` (Wolf), `game:enemy_wildbeast_boar` (Boar), `game:enemy_wildbeast_sabertooth` (Sabertooth), `game:enemy_scavenger_hound` (Scavenger Hound), `game:enemy_skeleton_hound` (Hollow Hound), `game:enemy_wildbeast_spiderant_melee` (Spider) | 1 x 1 | 0.5 / 2 |
| `game:enemy_fogger_wildbeast_infestedboar` (Fell Boar) | 1 x 1.5 | 0.5 / 2 |
| `game:enemy_wildbeast_rat_hook` (Rat), `game:enemy_wildbeast_rat_coldheights_variation_hook` | 0.75 x 0.75 | 0.5 / 2 |
| `game:enemy_wildbeast_spiderant_huge_hookpte` (Spider Mama), `game:enemy_vukah_ranger_elite_hive_tyrant` (Vukah Bonehunter), `game:enemy_lizardpeopledrak02_ranger` (Drak'Dal Hunter) | 2 x 2 | 2 / 3 (open ground) |
| `game:enemy_scavenger_hound`, scale 1.5 | 1 x 1 | 0.5 / 2 |
| `game:enemy_wildbeast_boar`, scale 1.5 | 1 x 1 | 0.5 / 3 |
| `game:enemy_wildbeast_wolf_earlygame`, scale 1.5 | 1 x 1 | 2 / 3 (open ground) |

Of the game's 389 walking enemy templates (no bosses), 345 have a profile at scale 1: 342 through a body profile, plus
the two rats and the Fell Boar (bodies with too few creatures for a body profile). The other 44 catch up: 11 bodies wider
than 2 m (the Fell Critter Queens' 4 x 3, four 3.5 m wide ones), 21 tentacles, wisps and Shroud Flayers with 0.1 m
colliders, 10 templates with a scale of their own (1.3, 0.8, 1.2, 0.4, 1.4, 3) and two unfinished yak templates (walker
radius 1). At any other pet scale only the three scale 1.5 creature profiles above apply.

**Where profiles come from**: a creature profile from any source before a body profile; within each kind, first match
wins:

| Source | Applies to |
|---|---|
| `<mod folder>/pets/clearance.json` | the pets that mod defines (its own creature block, or a game creature at its scale) |
| `kiln/data/kiln.pets/clearance.json` | every pet on this server (an operator's file, a calibration run's output) |
| Kiln's shipped profiles (`clearance.json` inside `Kiln.Pets.dll`) | every pet |

**Format 1:**
```json
{ "format": 1, "profiles": [ {
  "creature": "game:enemy_vukah_melee", "scale": 1, "game": 1287369,
  "horizontalSpace": 0.5, "verticalSpace": 2,
  "body": { "walkerRadius": 0, "walkerHeight": 2, "colliderWidth": 1, "colliderHeight": 2, "templateScale": 1 },
  "calibration": { "lab": "dev32-pets-clearance", "date": "2026-11-27", "gate": "19d C6",
                   "passed": ["corner-w2", "..."], "refused": ["corridor-w1-h3", "..."],
                   "pairs": { "0.5/1": "accepted a route the body cannot walk: ...", "0.5/2": "pass" } } },
  { "match": "body", "scale": 1, "game": 1287369, "horizontalSpace": 0.5, "verticalSpace": 2,
    "body": { "walkerRadius": 0, "walkerHeight": 2, "colliderWidth": 1, "colliderHeight": 1, "templateScale": 1 },
    "calibration": { "gate": "19d C6, by body", "creatures": ["game:enemy_wildbeast_boar", "..."] } } ] }
```
- `creature` is the Kiln id, `scale` the definition's `Level.Scale` (0.25..4). A game creature's id matches whatever its
  case, as the creatures registry resolves it; a creature block's id matches exactly. A body profile has
  `"match": "body"` and no `creature`.
- `game` is the game's own build number (svn), as Kiln's build record of the running exe names it
  (`kiln/sigdb/builds/<side>/<sha256>.json`); the dedicated server and a client that hosts share it. Kiln hashes the
  running exe once, off the tick thread.
- `body` is the creature's template as it was measured: the path walker's `horizontalRadius`/`height`, the movement
  collider's `width`/`height` (unscaled) and `templateScale`, the template's own `ScaleComponent.uniformScale` (a
  creature block's `Body.Scale`; default 1, a body profile states it). A body that differs now (a game update, a creature
  block that changed its `Body.Size` or `Body.Scale`, a data pack) invalidates the profile; Kiln reads a template's body
  again every 10 s.
- `calibration` is the evidence summary; Kiln does not read it.
- A profile without `game` or `body`, or out of range, is left out with a warning naming it; a file of another format, or
  the pre-dev.32 bare list, gives none.

**Refusals** (the follower's `Limited`, always `ClearanceMappingUnverified: ...`): no measured path clearance for the
creature at the scale; no profile names it and no body profile of this build is its body (or its template does not
walk); it was measured on another game build (a game update needs a new calibration); the body changed since it was
measured; the body cannot be read here; Kiln does not know this game build; the build is still being identified (the
first moment after start).

## The native safety module (kiln.pets v1)

Native, in the pinned broker DLL, POD records, no managed code on a worker:

- **Death policy** (kiln.decisions' native death guard, installed with the first row): a guarded body is never marked
  dead; its health reaching 0 latches DownPending (after the broker's quarantine) and Kiln.Pets retires it at the next
  Pre; a staging body is skipped without a down.
- **Hit policy** (asked by kiln.combat before ApplyHit, before every other policy, for every hit a pet row is part of):
  one rule for damage, reaction, knockback and aggro, the pet's declared rule. Pet to player, player to pet, pet to
  another owner's pet: denied unless both sides are by-mod-rule and the owners a declared pair. Pet to a creature: harm
  only to an unowned enemy-AI creature of a declared hostile faction (a player-like component always protects). Creature
  to pet: harm only from such a creature. Unknown source, terrain, buildings, anything kiln.combat could not read:
  denied. Heals: of the owner and the owner's pets only. An allowed lethal hit latches and is denied before the original
  (no death, reaction or aggro). Staging, down-pending and protection-lost rows: every hit denied.
- **Two-phase protection loss:** when decisions.native_guards goes off (or `withdraw`), every row is marked
  ProtectionLost at once and stays guarded; Kiln.Pets retires each body at the next Pre; rows go only with their bodies.
  No new body is admitted after that, also when none was bound at the moment, nor in a later world of the process.
- Ceilings: 256 rows, 256 hostile pairs; a full table refuses before anything is made.

Table: `bind`, `activate`, `inspect_body`, `collect_dirty`, `retire_binding`, `death_policy`, `hit_policy`, `involved`,
`set_owner_entity`, `set_hostile`, `withdraw` (records 96/80/56 bytes, pack 8; the header documents every field).

## The owner's view and the bar

The server sends each owner in game their own pets' full snapshot on the channel `kiln:pets` v1 (never anyone else's,
never to all): at most 5 per second while something changed, one a second as a keepalive; at most 64 KiB, 128 pets,
384 bytes an entry, names 64 bytes, definition ids 128 bytes; a fresh random session token per join and a strictly
increasing sequence. The client takes only the current session's increasing sequences (a delayed packet of an old
connection never wins), goes Stale after 3 s without one (the bar hides), and clears on disconnect.

```csharp
[ClientOnly] public static class PetsClient {
    PetOwnerView? Snapshot { get; }   // pets.hud.client; one immutable snapshot
    PetViewState State { get; }       // Disconnected, Current, Stale
    PetHudOptions Options { get; }
    void Configure(PetHudOptions options);   // pets.hud.client; this game only
}
```

`Snapshot` and `Configure` check the row they declare: while `pets.hud.client` (`Pets.HudClientCapability`) is off (a
dedicated server, a game without Kiln.Network or the renderer) they throw `KilnUnavailableException` naming it. Guard
them with `Capabilities.Is(Pets.HudClientCapability)`. `State` and `Options` read on every side.

The built-in bar: at (0.03, 0.72) of the screen, 220 x UI scale wide, rows 24 x scale, at most 6 rows (live pets first,
then anchors), name, health, Ready/Downed/Recovering and the channel's progress; recovery anchors as cyan world circles
(UI markers only). Events: `PetOwnerViewChangedEvent`, `PetViewStateChangedEvent`, `PetHudOptionsChangedEvent` (client
main thread). Disabling the bar changes nothing in the game. A vanilla client receives nothing (none is built for a game
without Kiln or without `kiln:pets`) and sees the bodies as the game's creatures; a mod commands pets from its own UI
through its own channel (Kiln has no client command path).

## Capabilities

Each row's name is a public constant of `Pets` (`Capabilities.Is(Pets.SummonCapability)`; a `KilnUnavailableException`'s
`Capability` is one of them). A definition's own limits are `PetLimits`, not capabilities.

| Row | Constant | Says |
|---|---|---|
| `kiln.pets` | `Pets.ServiceCapability` | Kiln.Pets is loaded |
| `pets.definitions` | `Pets.DefinitionsCapability` | definitions register |
| `pets.records` | `Pets.RecordsCapability` | records are kept (a server world runs) |
| `pets.summon` | `Pets.SummonCapability` | bodies are made and retired (kiln.spawn, a running world, creature_control) |
| `pets.control` | `Pets.ControlCapability` | the broker takes pets' leases (creature_control) |
| `pets.follow` | `Pets.FollowCapability` | pets follow and stay |
| `pets.fight` | `Pets.FightCapability` | the native target rule and kiln.combat's seam |
| `pets.downed` | `Pets.DownedCapability` | the native death guard (decisions.native_guards) and retirement |
| `pets.hud.server` | `Pets.HudServerCapability` | owners' views are sent (Kiln.Network) |
| `pets.hud.client` | `Pets.HudClientCapability` | this game shows its owner's pets (the channel and the renderer) |

## Tests

- Managed, offline: `managed/tests/Kiln.Pets.Tests` (definitions, the life table, receipts, the broker's leases on the
  kit's broker, following, the target rule, revive, the view and the bar); `Kiln.Blocks.Tests/PetBlockTests.cs`;
  `Kiln.Saves.Tests/ServiceCharactersTests.cs`.
- Native, host: `native/services/creature_control/tests/run.sh` (`pets_test.cpp`: rows, death and hit policies, pairs,
  withdrawal, worker reads under ASan and UBSan).
- Clearance: `ClearanceTests.cs` (format 1, the sources and their order, build, scale and body checks, the template
  scale, a body read again, game ids in any case, body profiles, the follower's pair, every shipped profile);
  `python3 -I tools/pets-clearance/test_profiles.py` (the C6 judgement of walks, the pair chosen and whether it vouches
  for the body, the bodies the fixtures cannot judge, body profiles, the profile file);
  `python3 -I tools/pets-clearance/test_calibrate.py` (`calibrate.sh run` against a lab that is not running).
- Lab (dedicated server, no client): `mods/tests/pets-clearance-lab` through `tools/pets-clearance/calibrate.sh`
  (fixtures, the route sweep, the real follower's walks, the native walker; evidence in `runs.jsonl`).
