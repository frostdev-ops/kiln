# kiln.gameplay: player/world interaction events and protection zones

What players (and villagers) do in the world, as events on Kiln's bus, with cancellation where the server can refuse
safely: crafting, interacting with chests/doors/stations, looting and picking up, building and terraforming,
teleports, natural spawns. Plus **protection zones**, the synchronous refusal that the game's own building and
terraforming code consults. Native table `native/core/abi/services/kiln_gameplay.h`, managed `Kiln.Gameplay`
(`managed/services/Kiln.Gameplay/`), evidence `docs/re/gameplay.md`, sample `mods/samples/Claims`, test
`mods/tests/gameplay-test`.

## Using it from a mod

Reference `Kiln.Gameplay` (`Private="false" ExcludeAssets="runtime"`) and subscribe:

```csharp
public sealed class NoChestsAtNight : Mod
{
    [Subscribe]
    private void OnInteract(InteractEvent e)
    {
        if (e.ByPlayer && Spawn.Inspect(e.Host)?.TemplateName?.Contains("Storage") == true && IsNight()) e.Cancel();
    }
}
```

The service activates itself when its assembly is first used: `[Subscribe]` handlers for its events are enough (Kiln
registers them before `OnLoad`, which runs the assembly's initializer: lab log `Kiln.Gameplay: listening to
kiln.gameplay` comes before the first mod's `loaded` line). `Gameplay.Enable()` does it explicitly. When the first
use is off the server-world thread (in a game hosting a world, its main thread runs the client world), the subscription
waits for the next server step instead of failing (log: `Kiln.Gameplay: first used off the server thread (…);
subscribing at the next server step. From: …`, then `listening`). Before 0.2.0-dev.15 it failed for good there
(`cannot subscribe to kiln.gameplay (kiln error 4)`) and no gameplay event reached the bus. Events are only
built while someone subscribes to them (Kiln.Gameplay tells the native service each step which kinds it wants).

## Events

All run on the tick thread. **Pre** = before the step in which the game acts on it (cancel works); **Post** = after
the step that did it.

| Event (namespace `Kiln`) | Forge | Phase | Fires when | Cancel does | Client sees |
|---|---|---|---|---|---|
| `CraftEvent` : cancelable | (none; PlayerEvent.ItemCrafted is after) | Pre | a craft request reaches the server (player at a station or by hand; a mod's `Gameplay.Inject.Craft`) | the request is dropped before `handle_crafting_events` reads it: nothing consumed, nothing made | nothing happens (materials stay); no message from the game |
| `ItemCraftedEvent` | ItemCraftedEvent | Post | the game's craft core ran once: per unit of a request, and once per finished cycle of an automated station (charcoal kiln, …), when its outputs arrive (since 0.2.0-dev.29.14: no second report at the cycle's start; feature `gameplay.craft_phase`) | — (observe; `Succeeded`, `Result`, `Outputs`) | |
| `InteractEvent` : cancelable | PlayerInteractEvent.EntityInteract / RightClickBlock | Pre | someone accepts an interaction offer: open a chest, door, use a workstation, bed, portal, talk to an NPC (villagers too) | becomes the game's own `DeclinedInteraction`: the host never accepts, the guest's interaction sequence ends | the interaction fails as when the object is busy (expected; see checklist) |
| `InteractionResultEvent` | | Pre (next step) | the game's answer: accepted / declined | — | |
| `LootEvent` : cancelable | EntityItemPickupEvent | Pre | a loot interaction: take an item lying in the world, loot-all a container, harvest, gather produce | becomes a `DeclinedInteraction`; nothing moves | nothing picked up |
| `ItemPickupEvent` | PlayerEvent.ItemPickupEvent | Pre (next step) | items arrived in an inventory (the game's "+N" popup): loot, harvest, craft output, refunds | — | |
| `BuildEvent` | BlockEvent.EntityPlaceEvent / BreakEvent | Post (same step) | place block / prop, dismantle, pick up prop, terraform (dig/add/flatten): `Kind`, `Player`, `Position`, `ItemId`, `Outcome` (`Done`, `RefusedByGame`, `DeniedByZone`) | not cancelable from the bus: it ran on a game worker inside the step. Use zones | zone refusal: the game's "could not build" result (see checklist) |
| `TeleportEvent` : cancelable | EntityTeleportEvent | Pre (step before the move) | a watched entity (all players; others via `Gameplay.WatchTeleports`) gets a teleport: fast travel, respawn, portal, admin, kiln.world | the entity stays; set `Target` to send it elsewhere | (checklist) |
| `TeleportedEvent` | | Post | a watched entity was moved (TeleportCount changed): `From`, `To` | — | |
| `NaturalSpawnEvent` : cancelable | LivingSpawnEvent.CheckSpawn | Post (same step) | a random spawner created a creature (only near players) | destroyed before the step returns; the spawner treats it as gone | never sees it |
| `EntityJoinLevelEvent` : cancelable | EntityJoinLevelEvent | Post (same step; a creation in Pre/Post: that step's or the next Post) | any entity was created: spawners, NPC logic, loot, a gear item's entity, child entities (weakspots, links), players, the save loading, Kiln.Spawn. `Entity`, `Template`/`TemplateName`/`TemplateInfo`, `Category`, `Parent`, `Position`, `ByKiln` (made outside the sim step) | (not players) the entity is destroyed again, like a canceled natural spawn | (checklist) |
| `EntityLeaveLevelEvent` | EntityLeaveLevelEvent | Post | an entity was destroyed (LifeTime, culling, despawn, loot taken, queued destroys, Kiln.Spawn); same fields, read before it went; not for the world shutdown | — | |
| `ItemTossEvent` : cancelable | ItemTossEvent | Pre | a player discards a stack (Enshrouded has no world drop: the inventory's Destroy, the item menu's Trash): `Player`, `How`, `Inventory`/`SlotIndex`, `ItemId`, `Count`, `ItemEntity` | the action is marked handled; the game never runs it, the item stays | (checklist) |

Cancelable events carry `DeniedByZone`/`ZoneName` when a zone canceled them before any handler: they arrive with
`Canceled = true`, so subscribe with `ReceiveCanceled = true` to see them (a handler may set `Canceled = false` to
override the zone for that one event). `Injected` marks requests sent by `Gameplay.Inject`.

Global natural spawn rate: kiln.settings `randomSpawnerAmount` (Few … Extreme, live), not duplicated here.

### Entity lifecycle: which entities

`EntityJoinLevelEvent`/`EntityLeaveLevelEvent` report only the categories mods ask for: `Gameplay.WatchEntities(
EntityCategories.Creature | EntityCategories.Npc)` (an `IDisposable`, per mod, released at unload; the native filter is
the union of all requests). While subscribers exist and nobody asked, `EntityCategories.Default` = Player, Creature,
Npc, Prop, Pickup; `Other` (VFX, attacks, projectiles, loot containers, links, item entities) only on request. The
category comes from Kiln.Spawn's template kind (by name; creature weakspots `Enemy_Weakspot_*` count as Creature).
Cost: with nobody subscribed the two hooks (createEntity, destroyEntity) test one atomic; with subscribers each
creation/destruction reads the entity record, its position and template kind and appends a record (filtered ones are
dropped at once). `ByKiln` = it happened outside the game's sim step (between kiln.gameplay's last Pre and its Post):
Kiln.Spawn, services, mod code in Pre/Post; an entity the game makes inside the step for a Kiln request (a gear item's
entity created by the inventory code for Kiln.Items) counts as the game's.

```csharp
[Subscribe] private void OnJoin(EntityJoinLevelEvent e) { if (e.TemplateName.StartsWith("Enemy_Fogger") && NoFog(e.Position)) e.Cancel(); }
public override void OnLoad() => Gameplay.WatchEntities(EntityCategories.Creature | EntityCategories.Pickup);
```

## Protection zones

```csharp
var zone = Gameplay.Protect(new ProtectionZone
{
    Name = "Main base", Shape = ZoneShape.Cylinder, Center = new(3800, 835, 1430), Radius = 40, MinY = 700, MaxY = 1200,
    Deny = GameplayActions.Build | GameplayActions.Break | GameplayActions.Terraform | GameplayActions.Interact |
           GameplayActions.Loot | GameplayActions.Craft | GameplayActions.Spawn,
    AllowedSteamIds = [76561198000000000], AllowAdmins = true,
});
// zone.Update(zone.Zone with { Radius = 60 }); zone.Dispose(); (also removed when the mod unloads)
```

- Players not allowed (by SteamID64 via kiln.players, or the game's admin bit with `AllowAdmins`) are refused the
  denied actions inside. Non-player actors (villagers, chests as operators) are never restricted, except `Spawn`, which
  stops natural spawns inside for everyone.
- Building, dismantling, prop pick-up and terraforming: checked synchronously inside the game's building core / the
  terraforming impact on the worker thread; reported afterwards as `BuildEvent` with `Outcome = DeniedByZone`.
- Interaction, loot, craft: checked in Pre against the host's / station's position; the event arrives canceled.
- `Gameplay.IsDenied(player, position, action, out zoneId)` asks the table directly.

## Other API

- `Gameplay.Inject.Craft(operator, station, recipe, amount)`, `.Interact(guest, host, offer)`, `.Loot(guest, host, …)`:
  Kiln's ECS program `kiln_gameplay` sends the game's own request event in the next step; the game validates it as a
  client's (materials, station workshop, offer state). Villager jobs and tests use this.
- `Gameplay.Recipe(id)` / `Gameplay.WorkshopRecipes(workshop)`: read-only recipe registry (inputs, outputs, duration,
  props). Workshop = `Crafting.workshopId` of a station (component bytes `+0x10`).
- `Gameplay.WatchTeleports(entity)`, `Gameplay.Status`.

Additions to the C table (v1, check `header.size`): kinds `KILN_GAMEPLAY_ENTITY_JOINED` (11),
`_ENTITY_LEFT` (12), `_DISCARD` (13); `entity_filter(categories)` (`KILN_GAMEPLAY_ENTITY_*`); status fields
`entity_hooks`, `entity_categories`, `entities_joined/left/filtered`, `discard`, `discards_seen/canceled` (older
callers' shorter status size is accepted). Sigdb features `gameplay.entities` (kiln.spawn's CreateEntity/DestroyEntity
and entity-map constants), `gameplay.discard` (PlayerInput/ServerConsumedPlayerInput offsets captured in
inventory_actions; same on 1273043).

C table (v1): `subscribe(owner, kinds_mask, fn, user)` (callback per event with a verdict; kind 0 = per-step interest
poll), `unsubscribe`, `zone_add/update/remove`, `zone_denies`, `inject`, `watch`, `status`, `recipe`,
`workshop_recipes`. See the header for each event's fields.

## Cost

Idle lab with the test mod subscribed to every event: Pre p50 0.029 ms for all of Kiln (15 Pre subscribers), Post p50
0.024 ms (kiln.gameplay is the only Post subscriber, so it pays WaitWorldJobs), step p50 0.84 ms (other labs 0.82-0.98).
Without players the pending buffers hold almost nothing; with players they hold one record per action. The hooks
cost a zone lookup per building/terraform operation and per random-spawner spawn.

## Lab results (lab `gameplay`, a copied world, no player)

`gameplay-test: done, 49 PASS, 0 FAIL`; `reload-test gameplay-test 5`: `5 reloads, 0 failures, avg 16.08 ms, max 22.34
ms, 0 leaked context(s)`. Covered: hooks installed on svn 1285601; zones (sphere, cylinder via Claims, box, update,
dispose, admin/steam lists); craft request seen → crafted (chest + workbench 1318) → pickup event → canceled craft
leaves the chest unchanged; interaction attempt seen and answered; canceled attempt delivered as DeclinedInteraction;
loot-all canceled / allowed (8 PickupEvents); zone denial of a player's interaction, loot and craft in Pre and a
handler overriding it; villager teleport allowed / canceled / redirected; Claims sample loaded its claim; no
building events without players.

## Lab results (mods/tests/events-test)

```
PASS join of the spawned Vukah 2542: Enemy_Vukah_Melee Creature kiln True at (3839.8, 833.0, 1433.8)
PASS its child entities joined too (3): 2544 VFX_SubEntity_Toggle_Signal_Link_Enemy_Vukah_Slinger Other kiln True,
     2545 Enemy_Weakspot_Vukah_Melee_Head Creature kiln True, 2543 Hookshot_Pull_Entity_To_Player Other kiln True
PASS leave of the destroyed Vukah: Enemy_Vukah_Melee kiln True / its children left (3/3)
PASS the gear's item entity 2546 joined from the game's queued create: Base_Pide_Durability Other kiln False parent 0
PASS and left through the game's queued destroy when taken: kiln False
PASS default categories report the creature, not its Other children (2 join(s))
PASS canceled join of 2551: exists False
PASS discard actions are read on this build (ItemTossEvent; needs a player to fire): True
```
The idle copied world (no player) creates and destroys nothing by itself (0 in 250 steps after Run).

## Client checklist (needs a joined player)

1. **Build in a zone** (not an owner): place a block and a prop inside → nothing placed, materials kept, `BuildEvent
   ... DeniedByZone`, Claims chat line. Does the client show a ghost block / prop that disappears, or stay
   desynced until relog? Same for dismantle and prop pick-up (does the block come back on the client?).
2. **Dig in a zone** with a pickaxe/shovel: no terrain change, no loot; does the client predict the hole and get it
   corrected? Any `[impact]` error lines in the game log from the 0x18 result?
3. **Owner** builds/digs inside: allowed, `BuildEvent Done` with the right item id and position (compare with the
   cursor); outside: allowed.
4. **Open a chest / door / workstation** in a zone as a stranger: the interaction is refused cleanly (no stuck
   animation, no open UI) and Claims tells the player; as the owner it opens. Then: can a stranger still move items
   out of a chest by inventory UI tricks without an accepted interaction (`inventory_actions` path)?
5. **Pick up an item** lying in a zone / harvest a plant: `LootEvent` canceled, item stays; walking over auto-pickup
   items (no interaction) is not refused (only `ItemPickupEvent`).
6. **Craft** at a workbench in a zone as a stranger: nothing crafted, materials kept; `CraftEvent` fields (operator =
   player, station = workbench); hand crafting outside zones: `ItemCraftedEvent` + `ItemPickupEvent`.
7. **Fast travel** to a flame altar with a handler that cancels `TeleportEvent`: the player stays — does the client
   hang in the fade/loading screen? With a redirect: lands at the new target? Respawn after death: `TeleportEvent` seen?
8. **Natural spawns**: walk into enemy areas: `NaturalSpawnEvent` with templates; a `Spawn` zone around a camp: no
   enemies appear there; cancel in a handler: the creature never shows up, the spawner keeps working elsewhere.
9. `InteractionResultEvent accepted` for real offers (offer ids from `InteractionOfferRuntime+0x28`).
10. **Discard** a stack (inventory destroy, item menu Trash) with a handler that logs `ItemTossEvent`: item id, count,
    slot match what was discarded; with a handler that cancels: the item stays (does the client UI show it vanish and
    come back, or never vanish?); does the event come in the Pre before the game acts (it must, for the cancel)?
11. **Entity lifecycle near a player**: walking into a camp gives `EntityJoinLevelEvent` for spawned enemies
    (`ByKiln false`), killing one gives leaves for its loot containers/corpse later; a player joining/leaving gives
    Player-category join/leave; canceling the join of a natural enemy: it never shows up (or pops in and out?).

## The contract (dev.27)

Kiln.Gameplay follows [the API contract](../modding/contract.md). Server side only (`[ServerOnly]` on `Gameplay` and `Offers`).

- **Capabilities.** `kiln.gameplay`; its sigdb features (`gameplay.build`, `craft`, `discard`, `emit`, `entities`,
  `intercept`, `queues`, `recipes`, `spawn`, `terraform`), lowered by Kiln.Gameplay when the service is not loaded;
  `gameplay.entity_events` (the lifecycle API can accept demand, including pending: `Gameplay.EntityEventsAvailable`)
  and `gameplay.spawn_replace` (`Gameplay.ReplaceNaturalSpawns`), native-owned static API aliases on current tables;
  the corresponding hook-group session rows say when delivery/enforcement is installed.
- **Errors.** A capability that is off throws `KilnUnavailableException` naming it, where these members threw
  `NotSupportedException` before (it still is one): `Recipe` / `WorkshopRecipes` name `gameplay.recipes`,
  `Gameplay.Inject` names `gameplay.emit`, `ReplaceNaturalSpawns` names `gameplay.spawn_replace`, the rest
  `kiln.gameplay`.
- **Threads.** `[TickThreadOnly]` on the world calls; `Status`, `Available`, `EntityEventsAvailable`, `WatchEntities`
  and `ReplaceNaturalSpawns` are `[AnyThread]`.
- **Start.** The rows are reported at the first service tick (`GameplayStartup`). The event listener, through which a
  handler can cancel crafting, interacting and looting, still starts at the first use only: a call into `Gameplay`,
  `Gameplay.Enable()`, or a subscription to one of Kiln.Gameplay's events. A handler of every event (`KilnEvent`,
  `CancelableEvent`) is not a use, so a server whose mods never use Kiln.Gameplay has no listener, as before dev.27.
- Not changed: `IsDenied` and `Offers.TryAdd` keep their `out` reasons; no result record exists in this service.

## Walk-over pickups (since 0.2.0-dev.29)
`ItemPickingUpEvent`: the game's walk-over pickup (an item it gives a player standing on it) is held at the entry of
its range helper and posted in a later Pre; cancel it to keep the item on the ground for this sampled exposure, and
`Decisions.ReevaluatePickup(drop, receiver)` to ask again. Interaction pickups stay `LootEvent`'s. Capability
`events.pickup`, on since 0.2.0-dev.29 and not yet confirmed in a real game. [decisions.md](decisions.md#walk-over-pickup).

Player build edits: `BuildEvent` still reports them after the fact, and `Gameplay.Protect` zones refuse them natively
(also terraforming). A managed cancelable event for player pieces (`events.build.pieces`) is not in this build, and
managed terraform cancel (`events.build.terraform`) is not part of V1.

### First use of the building hooks

`gameplay.build` means the five symbols/layouts resolved. `gameplay.build_hooks` and the native build status
fields distinguish resolved/unused, installed, waiting for checked safe Pre, and refused with a reason and mask.
BuildHooks remains true only for a complete group. PlaceBlueprint, PlaceProp, DismantleBlock, DismantleBlock2 and
PickUpProp now use the same checked typed installation as the other groups. No raw/world-stop installer remains.
A checked installation refused for threads Kiln could not check (not opened, not stopped, or inside the bytes to
replace) falls back to the unchecked install when no world runs in the process (at start, in a client's menu, after a
stop), as dev.28 installed these hooks at start: `kiln.gameplay: <group> hooks: <target> installed unchecked: no world
running (...)`; kiln.hooks' warning just above it counts the threads. Any other refusal (the code does not fit its
descriptor, the bytes changed) stays a refusal, with or without a world.

A native BUILD subscription (also tick-returned interest), managed BuildEvent interest including base-event
handlers, a Build/Break zone, or `request_build(owner)` requests the five. Known managed demand is prepared before
the world as described below. Native authors should request at kiln_plugin_main/on_ready if they will use it later.
No-world demand installs immediately. First demand during a world queues checked installation at safe Pres;
operations before completion are not observed or replayed. With only these five pending, installation needs three
Pres at the shared two-target budget. The remaining groups are ahead of build in the queue: simultaneous demand
for all ten targets completes in five successful Pres. Thread-check refusals can extend that; each target gets at
most three attempts on distinct Pres; after the third the group waits for the next no-world moment (see below) instead
of refusing. If every target needs two retries then succeeds,
all-group demand takes at most eighteen eligible Pres. Time spent with no eligible safe Pre is not bounded.

Zones with Build/Break denies refuse add/update with UNAVAILABLE while waiting or refused; the old zone stays
unchanged. Managed Protect/Update name `gameplay.build_hooks` and say "build protection is waiting for checked
installation at a safe Pre". A newer managed facade paired with an older native table still reports that table's
world-stop wait rather than promising safe-Pre installation. Bits are never silently removed. Pending subscriptions succeed with the group warning
and capability route announcing installation/refusal. Unsubscribing/removing the last zone does not unpatch.

The five typed frames are (pointer op, pointer transform/descriptor/what, u32 item) -> u8; no stack arguments or
scalar floating arguments. The observer reads positions before the continuation and returns its success byte,
or skips it on a zone deny. Partial links return CONTINUE before decoding arguments. Failure leaves the completed
prefix physically installed but inert; the existing mask names those targets, and no build events/denies run.

### Craft, terraform, natural-spawn and entity hook demand

`kiln.gameplay` installs none of its game detours merely by starting. The host's existing admitted-mod pre-world
reference pass now requests all groups for a mod referencing `Kiln.Gameplay`, including shipped helper references
and crafting-only use. References to the consuming facades also pre-arm their dependencies: `Kiln.Creatures`
requests natural-spawn hooks for variants, and `Kiln.Quests` requests craft/build/terraform for objectives.
`Kiln.Rules` requests nothing before the world: it follows building events only while an applied rule set keeps builds
outside bases, and asks then (at a safe Pre when a world runs). `Kiln.Blocks` references and admitted packs with a `blocks/` folder
pre-arm build/craft/terraform/spawn for declarative NPC variants and objectives, including data-only packs.
Admitted `quests/` folders also pre-arm craft/build/terraform (mask 7). Unreadable/malformed or over-budget metadata
conservatively requests every group; a valid native PE without managed metadata is skipped. These rules inspect
references, not arbitrary string/reflection use. ModLoadContext checks loaded helpers before their code runs;
reflection and arbitrary path-loaded helpers outside that closure raise demand on their first Gameplay call,
including through checked safe Pre during a world. Other facades do not count merely because a shared service exists.
Native plugins can request selected groups with the appended, size-checked
`request_hooks(owner, groups)` (build=1, craft=2, terraform=4, natural spawn=8, entities=16). The existing
`request_build(owner)` and its offset remain unchanged. Teleport watchers use components, not entity hooks.

| Group / session row | Demand |
| --- | --- |
| `gameplay.craft_hooks` | ItemCraftedEvent; native CRAFTED interest |
| `gameplay.terraform_hooks` | BuildEvent's Terraform operations; native BUILD interest; DENY_TERRAFORM zones |
| `gameplay.spawn_hooks` | NaturalSpawnEvent / native SPAWN interest; positive natural-spawn replacements; DENY_SPAWN zones |
| `gameplay.entities_hooks` | EntityJoinLevelEvent, EntityLeaveLevelEvent, WatchEntities, nonzero native entity_filter |

All five groups install immediately with no world, or through kiln.hooks' checked typed installer at the next
safe authoritative/joined-client Pre. They preserve the existing observer bodies, labels, priority, original-chain
continuations, calling thread and Post dispatch order. Explicit owner edges retain gameplay's old position before
SET/ShroudForge and NPC lifetime observers, after creatures, build and rules; EMF's higher priority and rules'
existing before-gameplay edge remain effective. Each Pre attempts at most two targets across the groups;
a simultaneous first request for all five needs five successful Pre passes (ten targets). An unchecked-thread refusal retries on later
safe Pre passes, at most three attempts per target. After the third the group is not refused: no unchecked write is
made while a world runs (an unchecked thread may run inside the bytes), so the group stays pending with
`waiting for the next no-world moment (the next menu|the next server start)` and the warning
`kiln.gameplay: <group> hooks wait for the next no-world moment ...`; a game client tries again at its next menu frame
(the UI tick), unchecked if it must, and logs `kiln.gameplay: <group> hooks installed with no world running, after
waiting since the refusal during play`. A dedicated server has no such moment until its next start, where the demand
comes again before the world. While it waits its zones are refused and its subscriptions accepted, as for any wait.
A signature refusal is final. Old cores lacking checked typed installation are explicitly refused.

The cost of a write during play is logged at info, one line per target:
`kiln.gameplay: <target> went in during play: the installer call took <ms> ms on the world's thread (write <k> in this Pre)`.
A lab server without players (38 to 40 of its 41 to 43 threads stopped per write, on a build machine with a load
average of 11 to 25) measured 2.0 to 9.2 ms with the other threads stopped and 3.1 to 11.6 ms for the whole call
per target; the worst write, on the busiest run, stopped them for 26.6 ms (a 37.9 ms call). Two writes in one Pre
made that step 8 to 14 ms longer (42 to 44 ms steps became 49 to 58 ms); the game's own autosave step in the same
runs took 140 to 160 ms. A target another owner already detoured (TerraformImpact with kiln.rules, DestroyEntity
with kiln.creatures) only gains a link: 0.1 to 1.1 ms. A server with players, and a hosting client with its render
threads, have more running threads to stop; that cost has not been measured.

Group rows show resolved/unused, waiting, installed or refused; appended native status fields include state,
installed-target count and reason. Existing CraftHook, TerraformHook, SpawnHook and EntityHooks booleans mean fully
installed. The existing static `gameplay.spawn_replace` and `gameplay.entity_events` capabilities stay available
while resolved, so dev.28 manifest requirements do not prevent preflight; the new session rows carry installation
progress. Permanent failures lower the legacy aliases too. Older native tables retain their old startup behavior
under the new managed facade; older managed readers ignore the appended fields.

A late event subscription is accepted while pending; the native waiting line states that earlier operations are
not replayed, and its group row and "installed on first use" line announce delivery start. Direct managed event
subscriptions use EventProducers and throw the named unavailable exception on refusal. Zones are refused while pending rather than silently failing to enforce. Positive spawn-replacement registrations
are retained while pending, with the group warning/row announcing that rules begin after installation; this lets
creature variants keep their registration instead of permanently skipping it after a transient wait. A later
permanent refusal lowers the group and static replacement alias and logs its reason; pending rules remain inert
and can be disposed/cleared normally. A registration made after permanent refusal is rejected. Removing zones/rules, unsubscribing, or disposing a watcher never removes hooks: continuations and
in-flight calls remain valid, with the existing idle/pass-through behavior, and future consumers avoid another patch.

The entity pair is one group because the category filter and lifecycle stream serve both operations. If its second
installation fails, the first stays physically installed but passes through without capturing events. A partial
group never publishes events. Clearing entity_filter or a spawn rule does not constitute new demand.

BuildEvent requests both building pieces and terraform; terraform can begin before the five-piece group completes.
CraftEvent interception and DENY_CRAFT zones do not need CraftRecipe; ItemCraftedEvent does.
Other services' own detours, such as creatures/npcai DestroyEntity observers and rules' TerraformImpact link,
remain independent; the absence of gameplay demand does not remove those services' links.

### Host compatibility and startup scan cost

The new Host announces with `request_hooks(owner, 0)` before its pre-world pass does admission or I/O. A current
Host with no consumers leaves all game entries untouched. Without the announcement, gameplay requests all groups
for old Host/native-only compatibility. Its passive subscription to the existing client UI tick runs before Host's
menu mod loader, after CLR startup, so the fallback can install immediately with no world on hosting/joining clients.
Dedicated servers (or clients without UI tick) use a one-time batch at the first authoritative/joined-client
world-start safe Pre, before normal mod OnLoad. A prediction-first order defers to that first eligible Pre.
Only that legacy batch bypasses pacing; checked refusals retain ordinary later-Pre retries and reasons. Successful
completion logs once: `no demand-aware host: all gameplay hooks installed as before`, plus each group line.
A status read before any announcement also conservatively requests all groups: this preserves old OnBoot/native
status probes that precede both those points. After announcement, status remains a read and idle groups stay idle.
Service on_ready itself is too early: native readiness precedes CLR/Host initialization. There is no public native
notification of CLR completion, so no earlier dedicated-server fallback is inferred from a timeout.

Metadata is cached across mods within one pass by absolute path, length, write time and creation time. Identity is
checked again after the read; changes/read failures are conservative demand. The cache stores AssemblyRefs, not a
mod-specific dependency closure. The pass limits reads to 4096 inputs / 256 MiB total / 64 MiB per file, and helper
enumeration to 4096 per mod. Exceeding a budget requests all groups for the affected mod. These are I/O bounds,
not a hard wall-time promise. One startup line reports `gameplay pre-world demand: <n> assemblies read in <ms> ms`.

The command-driven native fixture and exact coordinator procedures are in
[native/samples/gameplay-late](../../../native/samples/gameplay-late/README.md). It tests late subscriptions, pending
zone refusal, actual replacement activation, unsubscribe and marker-gated unchecked refusals without prearming at load.
