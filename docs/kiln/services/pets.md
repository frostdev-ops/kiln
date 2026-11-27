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
| Which creature the body is | `PetDefinition.Creature`: a `creatures` registry id (`game:enemy_wildbeast_wolf`) or a creature block (`<mod>:<name>`) | none (required) |
| Level and size | `PetLevelPolicy`: fixed, the owner's at grant/tame, the owner's at each summon; scale 0.25..4 | fixed 1, scale 1 |
| How it follows | `PetMovementPolicy`: Kiln's walk-only routes or the game's guard; follow, run and catch-up distances, stuck time, catch-up on/off | routes, 3 m, run past 10 m, catch up past 60 m or 5 s stuck |
| Who it may harm, who may harm it | `PetTargetPolicy`: the hostile creature factions; PvP off, or by the mod's rule (`Pets.SetHostile` pairs) | PvP off; Fogger, Vukah, Scavenger, WildBeast, Skeleton, Cyclops, Drak, Ancients |
| What a downed pet is | `PetDownedPolicy.Outcome` (recovery anchor, absent, released); per pet in `PetDowningEvent` | a recovery anchor where it fell |
| Revive: allowed, channel, range, health, cost, what cancels | `PetRevivePolicy`; a refusal per attempt in `PetRevivingEvent` | allowed, 3 s, 3 m, half health, free, movement and damage cancel |
| How many | `PetCaps`: records per character, live per character, live per world | 32, 1, 16 |
| What the owner's HUD shows | `PetHudPolicy` (bar row, anchor marker) per definition; `PetsClient.Configure` per player; or a HUD of the mod's own from `PetsClient.Snapshot` | bar and marker on |
| Who gets a pet | `Pets.Grant` (a reward, a quest, a command); taming (19e) when it lands | none |
| Attack, assist, defend | `Pets.Command(..., PetOrder.Attack, target, ...)`: the mod decides when; Kiln checks the target rule | Kiln never attacks by itself |

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
overwritten (`RecordsUnreadable`).

**Revisions.** `StateRevision` rises once per change (not per event); every event of one change carries it.
`DurableRevision` is the revision the last committed save's bytes hold: Kiln reads it out of exactly what each staging
serialized, and the final snapshot staging re-reads every live body first, so a pet that took a lethal hit in the step
of the save is written Downed (and retired at the next step with that same revision), never Ready. A failed save
acknowledges nothing (`PetRecordCommittedEvent` says when it moves).

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
    IReadOnlyList<PetSnapshot> Of(CharacterKey owner);                         // pets.records
    PetSnapshot? Get(PetId pet);
    Task<PetResult> Grant(CharacterKey owner, string definition, PetRequest request);
    Task<PetResult> Summon(PlayerRef owner, PetId pet, PetRequest request);    // pets.summon
    Task<PetResult> Dismiss(PlayerRef owner, PetId pet, PetRequest request);
    Task<PetResult> Command(PlayerRef owner, PetId pet, PetOrder order, EntityRef target, PetRequest request); // pets.follow (+ pets.fight for Attack)
    Task<PetResult> Revive(PlayerRef owner, PetId pet, PetRequest request, ItemSelection? cost = null);       // pets.downed
    Task<PetResult> CancelRevive(PlayerRef owner, PetId pet, Guid reviveOperation, PetRequest request);
    Task<PetResult> Release(PlayerRef owner, PetId pet, PetRequest request);   // pets.records
    void SetHostile(CharacterKey a, CharacterKey b, bool hostile); bool IsHostile(CharacterKey a, CharacterKey b); // pets.fight
}
```

- **Requests** carry an operation id (`PetRequest.New()`): a retry with the same id gives the first answer for 10
  minutes; the same id for another request is `RequestConflict`; a nonzero `ExpectedStateRevision` that moved on is
  `StaleRevision` (nothing changes). Requests start at the next step and complete when they settled.
- **The error rule:** a bad argument throws `ArgumentException`; an off row throws `KilnUnavailableException` naming it;
  a refusal is `Ok = false` with a reason (`Busy`, `NotOwner`, `OwnerOffline`, `AlreadyReleased`, `Capacity`,
  `NoSafeGround`, `BodyNotReady`, `ControlRefused`, `ProtectionUnavailable`, `TargetProtected`, `RecoveryNotSummoned`,
  `OutOfRange`, `CostUnavailable`, `ReviveRefused`, `CanceledNoCost`, `CanceledCostCommitted`,
  `ReviveFailedCostCommitted`, ...). A refusal after a cost was taken carries its receipts (`PetResult.Costs`): no refund
  is invented.
- **Revive:** quoted before anything starts (a dry run of the exact take); a mod may refuse (`PetRevivingEvent`); the
  channel runs in simulation time (leaving range, the owner leaving or going down always cancel; moving past
  `MaxOwnerDisplacement` and being hurt when the definition says so); at completion everything is checked again, an inert
  preview body is prepared (invulnerable, harmless), the cost is taken exactly once, and only then is the pet Ready and
  Live. The cost adapter is the minimal form of 19e's exact-cost adapter (the taming lane widens it).
- **Events:** every change is a `PetChangeEvent` (subscribe to the base for all): Granted, TransitionChanged, Summoned,
  Dismissed, CommandChanged, RecoveryAnchorChanged, BodyRetired, BodyLost, Downed, ReviveStarted, ReviveCanceled,
  Revived, Released, StatsChanged, ProtectionLost. Also `PetDowningEvent` (the outcome), `PetRevivingEvent` (cancelable),
  `PetRecordCommittedEvent`, `PetControlChangedEvent` (the broker's lease states of a pet), `PetDefinitionChangedEvent`.
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

Retiring a body quarantines it first (every lease, a rider's too, loses authority at once, its root is denied), gives a
rider two steps to get off, then destroys it; the broker retires its rows when it sees the body gone.

Following (QueryInput): walk-only routes from kiln.queries (Found, reaching the target, every point a walk; never code 0
with no points, never partial), at most 4 requests in flight, a new route past 2 m of owner movement or each
`RepathSeconds`; input written only while the broker's `can_drive` says so. **Path clearance is measured, not guessed**
(19d §4.3a, gate C6): the HorizontalSpace/VerticalSpace a route asks for come from an accepted profile per creature and
scale (`kiln/data/kiln.pets/clearance.json`); without one the pet does not walk routes and only catches up
(`ClearanceMappingUnverified`). Catch-up puts the body on validated ground next to the owner (ground under its centre and
4 footprint points, a floor spread of at most 0.5 m, a clear column, no entity origin within 2.5 m), then checks that it
settled (within 1 m after 0.5 s); one that does not settle stops and waits 10 s before another checked try. Stay holds
with zero input. EngineGuard sends the game's own guard event (Kiln.Creatures' keeper is not run beside it). Attack hands
the body to the game's AI under PetCombat with the target preferred; a target that is gone or dead puts the pet back on
follow.

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
    void Configure(PetHudOptions options);   // this game only
}
```

The built-in bar: at (0.03, 0.72) of the screen, 220 x UI scale wide, rows 24 x scale, at most 6 rows (live pets first,
then anchors), name, health, Ready/Downed/Recovering and the channel's progress; recovery anchors as cyan world circles
(UI markers only). Events: `PetOwnerViewChangedEvent`, `PetViewStateChangedEvent`, `PetHudOptionsChangedEvent` (client
main thread). Disabling the bar changes nothing in the game. A vanilla client receives nothing and sees the bodies as the
game's creatures; a mod commands pets from its own UI through its own channel (Kiln has no client command path).

## Capabilities

| Row | Says |
|---|---|
| `kiln.pets` | Kiln.Pets is loaded |
| `pets.definitions` | definitions register |
| `pets.records` | records are kept (a server world runs) |
| `pets.summon` | bodies are made and retired (kiln.spawn, a running world, creature_control) |
| `pets.control` | the broker takes pets' leases (creature_control) |
| `pets.follow` | pets follow and stay |
| `pets.fight` | the native target rule and kiln.combat's seam |
| `pets.downed` | the native death guard (decisions.native_guards) and retirement |
| `pets.hud.server` | owners' views are sent (Kiln.Network) |
| `pets.hud.client` | this game shows its owner's pets (the channel and the renderer) |

## Tests

- Managed, offline: `managed/tests/Kiln.Pets.Tests` (definitions, the life table, receipts, the broker's leases on the
  kit's broker, following, the target rule, revive, the view and the bar); `Kiln.Blocks.Tests/PetBlockTests.cs`;
  `Kiln.Saves.Tests/ServiceCharactersTests.cs`.
- Native, host: `native/services/creature_control/tests/run.sh` (`pets_test.cpp`: rows, death and hit policies, pairs,
  withdrawal, worker reads under ASan and UBSan).
