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
| `ItemCraftedEvent` | ItemCraftedEvent | Post | the game's craft core ran once: per unit of a request, and automated stations' (charcoal kiln, …) successful cycles | — (observe; `Succeeded`, `Result`, `Outputs`) | |
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
  `gameplay.entity_events` (the entity lifecycle events work: `Gameplay.EntityEventsAvailable`) and
  `gameplay.spawn_replace` (`Gameplay.ReplaceNaturalSpawns`), both managed and fixed at the service's first use.
- **Errors.** A capability that is off throws `KilnUnavailableException` naming it, where these members threw
  `NotSupportedException` before (it still is one): `Recipe` / `WorkshopRecipes` name `gameplay.recipes`,
  `Gameplay.Inject` names `gameplay.emit`, `ReplaceNaturalSpawns` names `gameplay.spawn_replace`, the rest
  `kiln.gameplay`.
- **Threads.** `[TickThreadOnly]` on the world calls; `Status`, `Available`, `EntityEventsAvailable`, `WatchEntities`
  and `ReplaceNaturalSpawns` are `[AnyThread]`.
- Not changed: `IsDenied` and `Offers.TryAdd` keep their `out` reasons; no result record exists in this service.
