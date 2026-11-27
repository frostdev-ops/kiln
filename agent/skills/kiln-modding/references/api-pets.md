# The mod API: pets (Kiln.Pets, dev.32+)

Part of the mod API guides ([api.md](api.md) is the index). Here: defining a pet, giving, summoning and ordering it, what
happens when it goes down, revives and their costs, who may fight whom, the owner's bar, and the rules that bite. Taming (how
a player gets a pet from a wild creature) is [api-taming.md](api-taming.md). Signatures are the common ones: grep
[api-index/Pets.md](api-index/Pets.md) (and `Blocks-pets.md` for `PetBlock`) before you write one.

**Kiln makes pets possible; your mod decides what a pet is.** Kiln keeps the records (per character, in the world's save),
the bodies, the movement leases, protection, the revive channel with its cost receipts and the owner's bar. Every behaviour
choice is yours: the creature, its level, who it may fight, what going down means, what a revive costs, when to attack. Where
you say nothing, Kiln's default is the safe one: every player protected, a downed pet is a bodyless recovery anchor.

- Assembly: `Pets` in `KilnServices` (and `Blocks` for `PetBlock`). Server side. Players need Kiln for the bar only; a pet
  on a game creature shows to vanilla players as that creature. A pet on a creature block of your own needs what that block
  needs (`clients: mod`).
- Pages: `docs/kiln/modding/pets.md` (author guide), `docs/kiln/services/pets.md` (the contract), wiki
  https://frostdev.io/kiln/wiki/pets. Samples: `PetsSample` (hound and duelist, `/pet ...`), `TamingSample`.
- Status: experimental in practice. Run on dedicated lab servers and in host tests; the bar, markers, animation, attack
  orders over 30 s, anchors at login and terrain were not seen in a real game ([versions.md](versions.md)). Say so.

## Define a pet

A pet's id is `<mod id>:<name>`, lower case letters, digits, `-`, `_`, `.`. Two ways, same record:

```csharp
public override void OnLoad()
{
    // a game creature by its creatures-registry id: asks nothing of players' games
    Blocks.Define("hound", new PetBlock { Creature = "game:enemy_wildbeast_wolf_earlygame", Name = "Hound" });   // "my-mod:hound"

    // every choice spelled out, no Kiln.Blocks
    var def = Pets.Define(new PetDefinition
    {
        Id = "my-mod:duelist",
        Creature = "game:enemy_vukah_melee",
        DisplayName = "Duelist",
        Level = new PetLevelPolicy { Mode = PetLevelMode.OwnerAtSummon },
        Movement = new PetMovementPolicy { FollowDistance = 4, RunDistance = 12 },
        Targets = new PetTargetPolicy { Pvp = PetPvpMode.ByModRule },
        Downed = new PetDownedPolicy { Outcome = PetDownedOutcome.Absent },
        Revive = new PetRevivePolicy { ChannelSeconds = 5, Cost = new PetCostDefinition(Items.Lookup("Material_T1_Twigs")!.Id, 3) },
        Caps = new PetCaps { LivePerCharacter = 2 },
        Hud = new PetHudPolicy { RecoveryMarker = false },
    });
    if (!def.Ok) Log.Warn($"pet not defined: {def.Reason}");   // DefinitionInUse, CreatureUnknown, CreatureUnsupported, ...
}
```

The same as data (`blocks/*.json`, section `pets`; names are the camelCase of the C# members; `PetsSample/blocks/pets.json`):

```json
{ "pets": { "duelist": {
    "creature": "game:enemy_vukah_melee", "name": "Duelist",
    "level": { "mode": "ownerAtSummon" },
    "movement": { "followDistance": 4, "runDistance": 12 },
    "targets": { "pvp": "byModRule", "hostileFactions": ["vukah", "fogger"] },
    "downed": { "outcome": "absent" },
    "revive": { "channelSeconds": 5, "cost": { "item": "Material_T1_Twigs", "count": 3 } },
    "caps": { "livePerCharacter": 2 }, "hud": { "recoveryMarker": false } } } }
```

| Choice | Member | Default and range |
|---|---|---|
| The body | `Creature`: a creatures-registry id (`game:enemy_...`) or a creature block of a mod (`my-mod:name`, a `CreatureBlock` handle in a `PetBlock`) | required. Never a template name or GUID. **Only enemy creatures that walk** (`CreatureUnsupported` otherwise: no wildlife, farm animals or flyers) |
| Name | `Name` on the block, `DisplayName` on `PetDefinition` | the creature's own; 1 to 64 UTF-8 bytes |
| Level and size | `PetLevelPolicy`: `Mode` (`Fixed`, `OwnerAtTame`, `OwnerAtSummon`), `Level` 1..255 (not above the creature's highest), `Scale` 0.25..4 | fixed 1, scale 1 |
| Following | `PetMovementPolicy`: `Mode` (`QueryInput` Kiln's walk-only routes, `EngineGuard` the game's guard), `FollowDistance` 1..10, `RunDistance` up to 30, `CatchUpDistance` 10..160, `StuckSeconds` 1..30, `RepathSeconds` 0.1..5, `GroundSearchMeters` 0.5..8, `AllowCatchUp` | 3 m, run past 10 m, catch up past 60 m or 5 s stuck |
| Who it fights | `PetTargetPolicy`: `HostileFactions` (never Player, AutomatedPlayer, PlayerAlly, FarmAnimal: `Define` refuses them), `Pvp` (`Off`, `ByModRule`) | Fogger, Vukah, Scavenger, WildBeast, Skeleton, Cyclops, Drak, Ancients; PvP off |
| Going down | `PetDownedPolicy.Outcome`: `RecoveryAnchor`, `Absent`, `Release`; per pet in `PetDowningEvent` | a recovery anchor where it fell |
| Revive | `PetRevivePolicy` / `ReviveBlock`: `Allowed`, `ChannelSeconds` 0.1..60, `RangeMeters` 1..8, `HealthFraction` (0, 1], `Cost` (one plain stack item, 1..999), `CancelOnMovement`, `MaxOwnerDisplacement` 0.1..2, `CancelOnObservedOwnerDamage` | allowed, 3 s, 3 m, half health, free, movement and damage cancel |
| How many | `PetCaps`: `RetainedPerCharacter` 1..128, `LivePerCharacter` 1..16, `LivePerWorld` 1..256 | 32, 1, 16 |
| The owner's HUD | `PetHudPolicy`: `Bar`, `RecoveryMarker` | both on |

A bad value throws `ArgumentException` naming the field path (in a blocks file: a `BlockError`). A revive cost's item is checked
once the game's items are readable: an item the game lacks (or gear) is your error in kiln.log, and that pet's revives are
refused `CostUnavailable` with nothing taken. `Pets.Define` of another definition under an id while one of its pets has a body
or a revive running is `DefinitionInUse`. A definition's id must start with your lower-case mod id (else `ArgumentException`).
`Was = "old-name"` on a block keeps the id through a rename: records keep their pets.

## Give, summon, order, revive, release

```csharp
var key = CharacterData.KeyOf(player);                  // the character (never account-only)
var given = await Pets.Grant(key, "my-mod:hound", PetRequest.New());       // a Ready record, no body yet
var who = PlayerRef.Of(player);
var pet = given.Pet!.Id;
await Pets.Summon(who, pet, PetRequest.New());          // a protected body 2 to 4 m away, following
await Pets.Command(who, pet, PetOrder.Stay, EntityRef.None, PetRequest.New());
await Pets.Command(who, pet, PetOrder.Attack, EntityRef.Of(enemy), PetRequest.New());   // your rule says when; Kiln checks the target
await Pets.Revive(who, pet, PetRequest.New());          // stand at its recovery anchor; summon the anchor first
await Pets.Release(who, pet, PetRequest.New());         // for good: a tombstone until a save holds it
foreach (var p in Pets.Of(key)) Log.Info($"{p.Label}: {p.Life} {p.Presence} {p.Health}/{p.HealthMax}");
```

- **Server, tick thread** (`Pets.Define` is made in `OnLoad`; `Definition` and `Definitions` read from any thread). Every
  request returns `Task<PetResult>`: `await` it from a handler or command. A handler's own request runs at the next step.
- **`PetRequest.New()`** makes a fresh operation id. The same id again gives the first answer back for 10 minutes (a retry never
  does it twice); the same id for another request is `RequestConflict`; a nonzero `ExpectedStateRevision` that moved on is
  `StaleRevision`. `PetResult` has `Ok`, `Reason`, `Pet` (after it), `Costs` (receipts).
- **Refusals are results, not exceptions:** `Busy`, `NotOwner`, `OwnerOffline`, `AlreadyReleased`, `Capacity`, `NoSafeGround`,
  `BodyNotReady`, `ControlRefused`, `ProtectionUnavailable`, `TargetProtected`, `RecoveryNotSummoned`, `OutOfRange`,
  `CostUnavailable`, `ReviveRefused`, `UncertainCommit`, ... A bad argument throws `ArgumentException`; an off row throws
  `KilnUnavailableException` naming it. A refusal after a cost was taken carries its receipts: Kiln invents no refund.
- **Rows** (ask first, [api-contract.md](api-contract.md)): `pets.definitions`, `pets.records` (Grant, Of, Release), `pets.summon`
  (Summon, Dismiss), `pets.follow` (Command), `pets.fight` (Attack, `SetHostile`), `pets.downed` (Revive), `pets.control`,
  `pets.hud.server`, `pets.hud.client`. `Capabilities.Why(row)` says why one is off (a joined client has no server rows).

## What a pet is, over time

`PetLife` (`Ready`, `Downed`, `Released`) and `PetPresence` (`Absent`, `Live`, `RecoveryAnchor`, `Retiring`) are two axes;
`PetTransition` (`Spawning`, `Downing`, `Dismissing`, `Reviving`, `Releasing`) says what is happening: requests meanwhile are
`Busy`. `WantsSummoned` is the owner's wish: set by Summon, cleared by Dismiss, kept through logout (the next login brings the
pet back; a downed one comes back as its anchor, never its health). Saved: id, definition, life, wish, order (an attack
order is saved as follow), level, health share, anchor. Never saved: bodies, leases, targets. A record whose definition is not
defined this session stays dormant, never erased.

## Decide per pet: events

All on the server tick thread, after the change settled. Subscribe to `PetChangeEvent` for every change.

```csharp
Events.On<PetDowningEvent>(e =>                         // a down is coming: choose what it becomes (it cannot be refused)
{
    if (e.Pet.Definition == "my-mod:hound" && FarFromOwner(e.Pet)) e.Outcome = PetDownedOutcome.Release;
});
Events.On<PetRevivingEvent>(e => { if (OnCooldown(e.Owner)) e.Cancel(); });    // nothing was taken yet
Events.On<PetRevivedEvent>(e => Log.Info($"{e.Change.After.Label} is back"));
```

`PetDownedEvent` comes only after the combat body is gone. `PetRevivingEvent` has the quoted `Cost` (null: free) and the
`OperationId` (`Pets.CancelRevive` names it). Also `PetGrantedEvent`, `PetSummonedEvent`, `PetDismissedEvent`,
`PetCommandChangedEvent`, `PetRecoveryAnchorChangedEvent`, `PetBodyRetiredEvent`, `PetBodyLostEvent`, `PetReviveStartedEvent`,
`PetReviveCanceledEvent`, `PetReleasedEvent`, `PetStatsChangedEvent`, `PetProtectionLostEvent`, `PetRecordCommittedEvent`
(`DurableRevision` moved: a save holds it), `PetControlChangedEvent`, `PetDefinitionChangedEvent`.

**Revive** is quoted first (a read: nothing moves), then the owner channels in simulation time next to the recovery anchor.
Leaving range, the owner leaving or going down always cancel; moving past `MaxOwnerDisplacement` and being hurt cancel when
the definition says so. At the end everything is checked again, the cost is taken exactly once from one slot of the owner's
(`ItemCosts`, [api-items.md](api-items.md#exact-costs-and-receipts-dev32)), then the pet is Ready and Live. A cost split over
two stacks is not taken. An uncertain receipt answers `UncertainCommit` and the pet stays Downed.

## PvP, defend and assist: your rule

`Pets.SetHostile(a, b, true)` declares two **characters** hostile; pets whose `Targets.Pvp` is `ByModRule` may then fight the
other character and that character's by-rule pets. Declare it when both players agreed; end it with `false`. Pairs last for the
world session. Kiln never attacks by itself: order an attack when your rule says so (the owner was hit, a key was pressed);
Kiln refuses a target the pet's rule protects (`TargetProtected`). One rule decides hits, knockback, aggro and orders.

## Walking beside the owner: measured clearance

A pet walks routes beside its owner only where Kiln has a **measured clearance profile** for its creature's body, at its
scale, on the running game build. Without one it still follows by catching up to its owner on safe ground
(`ClearanceMappingUnverified` in kiln.log). Kiln ships profiles for the creatures it calibrated and for bodies shared by three
or more calibrated creatures (most walking enemies at scale 1). A game update invalidates them until Kiln ships new ones. A
creature block of your own, or a game creature at another scale, needs a profile of your own, made in a lab with
`tools/pets-clearance/calibrate.sh` (a Kiln source checkout on Linux and a lab base): `docs/kiln/modding/pets.md`. A body
wider than 2 m or taller than 3 m gets none.

## The owner's HUD

Kiln draws a pet bar for owners whose game runs Kiln: `PetHudPolicy` per definition, `PetsClient.Configure(new
PetHudOptions(Enabled: false))` per player. To draw your own, read `PetsClient.Snapshot` (a `PetOwnerView`, at most 128
entries) **once per frame** in a `RenderFrameEvent` handler and never act on the game from the render thread; `PetsClient.State`
says current, stale (no keepalive for 3 s) or disconnected. Capability `pets.hud.client`.

## Tests

`Kiln.TestKit` has no pet fake yet (`KilnHarness.Registry` and `NpcSpeech` exist; see [testing.md](testing.md)). Test your rules (cooldowns, outcome choices) as plain functions, and check the rest on a
local server with `/pet`-style commands like `PetsSample`'s. Lab only: `LabBots` (Kiln.Players, experimental `KILNX001`) can
stand in for a player on a test server; never ship it.

## Rules that bite

[pitfalls.md](pitfalls.md#dev32-additions) has the table. The short list: the API is server-only and tick-thread; every
request needs its own `PetRequest.New()`; a game creature's id, never a GUID; only walking enemies; an id lower case with your
mod id in front; revive costs are one plain stack item; `Reason` strings are words first (`Busy: ...`), switch on the first word.
