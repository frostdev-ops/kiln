# kiln.items: items in inventories

Give items to and take them from any inventory (players' backpacks, chests, villagers), gear with a level and rarity
included; read an entity's inventory; look items up by name. Everything goes through the game's own inventory
transaction (the code its admin "create item", loot pickup and crafting use), so stacks merge, gear gets its own item
entity, and the change is replicated and saved like a vanilla one. Evidence and layouts: `docs/re/items.md`.

| Layer | Where |
|---|---|
| Symbols | `sigdb/symbols/server.d/items.json` (features `items.transaction`, `items.program`, `items.read`, `items.catalog`, `items.scan`, `items.recipe`, `items.fuel`, `items.pide_options`, `items.move`, `items.transfer`, `items.names`) |
| C table | `native/core/abi/services/kiln_items.h` (`kiln.items` v3: v1 + water + moves, container names, transfer rules and events) |
| Native | `native/services/items/` → `kiln/services/kiln-items.dll` |
| Managed | `managed/services/Kiln.Items/` → `kiln/host/Kiln.Items.dll` (namespace `Kiln`, class `Items`) |
| Test | `mods/tests/items-test/` (PASS / FAIL lines in kiln.log; v3: `MovesTest.cs`); `mods/tests/craftmatch-test/` (rarity, level, perks of made items); `mods/tests/fuel-test/` (water, three boots); `native/services/items/tests/run.sh` (host tests under Wine) |

## C#

Reference the assembly like any Kiln service (never ship it):
```xml
<ProjectReference Include="…/managed/services/Kiln.Items/Kiln.Items.csproj" Private="false" ExcludeAssets="runtime" />
```

```csharp
using Kiln;

// Catalog (the server's item registry + display names from the dashboard catalog)
ItemInfo? wood = Items.Lookup("Wood Logs");            // also "Material_T1_Wood_Logs", "0x2b0cf1d5", "722268629"
IReadOnlyList<ItemInfo> swords = Items.Search("sword");
ItemInfo? info = Items.Get(new ItemId(0x43108313));    // Bronze Sword: IsGear, LevelMin 1, LevelMax 55

// Give: completes on the tick thread one step later
ItemsResult r = await Items.Give(player, wood!.Id, 250, options: GiveOptions.Notify);   // "+250 Wood Logs" popup
if (r.Status == ItemsStatus.Partial) Log.Info($"only {r.Done} fit");

// Gear with a level and rarity (the game clamps the level to the item's range)
var g = await Items.Give(chest, info!.Id, 1, new Gear(Level: 20, Rarity: ItemRarity.Epic));
// ... with all its upgrades unlocked (each one level higher, as at the workbench) and an empty gem slot (weapons)
var u = await Items.Give(chest, info!.Id, 1, new Gear(20, ItemRarity.Epic) { Unlocked = 5, GemSlot = true });
EntityId sword = g.Changes[0].ItemEntity;              // the new item entity (level, perks, durability live there)

// What a recipe makes, through the game's own recipe function: created exactly as crafting creates it
var made = await Items.GiveRecipeResult(chest, recipeId);   // inputs not taken; made.Done counts runs, made.Changes the stacks

// Read
InventoryContents? inv = Items.Read(chest);            // null: no inventory
int logs = inv!.Count(wood.Id);
foreach (var s in inv.Slots.Where(s => !s.IsEmpty)) Log.Info($"{s.Slot}: {s.Item} x{s.Count} lvl {s.Level}");

// Take: all or nothing; gear taken out is destroyed by the game
await Items.Take(chest, wood.Id, 7);                               // from anywhere in the inventory
await Items.Take(chest, wood.Id, 7, from: slot, exactSlot: true);  // only from that slot
await Items.TakeSlot(chest, inv.Slots[3]);                         // exactly what that slot holds (same gear entity)

// Containers in the world (one pass over the entities; tick thread)
IReadOnlyList<EntityId> chests = Items.FindInventories(InventoryKinds.Containers | InventoryKinds.NonEmpty);
```

- `Give(target, ItemId|string, count = 1, Gear? = null, GiveOptions = None, SlotId? slot = null)` →
  `Task<ItemsResult>`. Options: `Notify` (player popup + PickupEvent), `AllOrNothing`, `NoMerge`, `DryRun` (run and
  roll back: says what would happen). `slot` = the slot to fill first.
  Unset `Gear` values are the game's defaults, the same as crafting: a level rolled in the item's range, `rarity` or, with
  `generateRarity` set (loot weapons), the lowest allowed rarity (Common), never a rolled one: docs/re/items.md section 10.
- `Gear.Unlocked` (1-4, 5 or 255 = all the item has; null or 0 = none) and `Gear.GemSlot` (weapons: an empty gem slot when the game's roll
  gave none) are applied as the item entity is created: a hook in the game's createPide changes the component list it
  spawns the item with (`PerkContainerNew.unlockedPerks`, the `Level` value + the unlocked count, a `WeaponGemSlot` entry;
  docs/re/creative-mode-146.md "Part 3 research", section 2). `Items.GearOptionsAvailable` says whether this build has
  it (feature `items.pide_options`); without it a give with them is `Unavailable`. C: `KILN_ITEMS_UNLOCK` with
  `KilnItemsRequest.unlock` (1..5, 255 = all), `KILN_ITEMS_GEM_SLOT`. Lab: "all upgrades: perks 4/4, level 34 (asked 30 +
  4)", "the weapon has a gem slot". A save and a restart keep all of it (lab, three weapons in a chest, autosaved,
  restarted, read back): "level 6, perks 4/4, gem slot True", "level 4, perks 2/4", "level 2, perks 0/4".
- `GiveRecipeResult(target, recipeId, count = 1, GiveOptions)` → `Task<ItemsResult>`: the game's recipe function (feature
  `items.recipe`, C flag `KILN_ITEMS_RECIPE`). Inputs are not taken and a station's requirements count as met. For tests and
  for mods that hand out what a recipe makes. `Notify`, `NoMerge`, `DryRun`. All outputs fit or nothing changes (`NoRoom`).
- `Take(target, item, count = 1, SlotId? from = null, bool exactSlot = false, EntityId? itemEntity = null, bool dryRun
  = false)`, `TakeSlot(target, InventorySlot, dryRun)`.
- `ItemsResult`: `Status` (`Done`, `Partial`, `NoRoom`, `NotEnough`, `NoInventory`, `UnknownItem`, `SlotChanged`,
  `Refused`, `Unavailable`, `Dropped`, `Invalid`), `Done`, `Requested`, `Changes` (slot, item, amount, item entity),
  `Message`, `GameError` (the game's code), `Steps`, `DryRun`. Game-side outcomes never throw.
- `Read(entity)` → `InventoryContents` (`Inventories` with their `InventoryCategory`, `Slots` with `Item`, `Count`,
  `ItemEntity`, `Level`, `Rarity`, `Category`, `Link`, `IsGear`, `IsEquipped`, `IsBroken`), `GenericSlots`,
  `AvailableSlots`, `IsPlayer`, `Count(item)`, `FreeSlots()`.
- Catalog: `All`, `Get(id)`, `Lookup(nameOrId)`, `Search(text)`; `ItemInfo` = id, debug `Name`, `DisplayName`, `Label`,
  `MaxStack`, `Category`, `Rarity`, `IsGear`, `LevelMin/Max`, `EquipSlot`, `Flags`, `IsBlueprint`, `Guid`.
  `ItemId.FromGuidBytes(bytes)` computes an id like the game.
- `Available` (service loaded), `CanTransact`, `Status()` (program state, pending, commits, detail).

Rules: call on the tick thread (mod code always is; `Read`/`FindInventories` throw `InvalidOperationException`
elsewhere). Continuations of `await Items.Give(...)` come back on the tick thread at the next step. When a mod unloads,
its pending results are dropped (the transaction itself may still run). Item entity ids (`ItemEntity`) of gear are
reassigned when the save loads: do not keep them across restarts.

## Water in containers (`Items.Fuel`)
Watering cans, water pouches, the water pump, barrels and wells hold water in the game's `Fuel` attribute, 64 units per
charge (a can of 100 charges holds 6,400). `Items.Fuel` reads it and changes it through the game's tracked write, so
players see the new amount and the next save keeps it, like after the game's own fill or pour. A raw write
(`World.TryWrite`) reaches players too, but the save skips it on a container that was already saved
(docs/re/items.md section 12).

```csharp
FuelLevel? w = Items.Fuel.Read(can);                       // (Units, Max), Charges, IsFull; null: it holds no water
await Items.Fuel.Set(can, 3200);                           // clamped to 0..max
await Items.Fuel.Add(can, -64);                            // one charge out
await Items.Fuel.Fill(barrel);                             // a placed container: the entity itself
FuelResult r = await Items.Fuel.FillAll(player, canItem);  // every can in a player's inventories (or a chest's)
await Items.Fuel.SetAll(chest, 0);                         // every container in the chest, emptied
using FuelKeep keep = Items.Fuel.KeepFull(player, canItem); // full every step, after the game's own pours
using FuelKeep slow = Items.Fuel.Refill(barrel, FuelRefill.ChargesPerSecond(2, limitUnits: 3200), inventories: false);
slow.Refilled += changes => Log.Info($"{changes.Sum(c => c.After - c.Before)} units back");   // also WaterRefilledEvent
```
- `Set`, `Add`, `Fill` act on one container (an item entity or a placed one). `FillAll` and `SetAll` act on every
  container in a holder's inventories (Virtual links expanded), only those of `item` when given. Each takes `dryRun`.
- `FuelResult`: `Status` (`Done`, also when nothing needed a change; `SlotChanged` when the entity holds no water;
  `NoInventory` when a holder has none; `Unavailable`, `Dropped`), `Matched` (containers found), `Changed`, `Changes`
  (container, item, slot, before, after, max; the first 16), `Message`, `Steps`, `DryRun`.
- **Refill policies:** `Refill(entity, FuelRefill policy, item, inventories = true)`; `KeepFull(...)` is
  `Refill(..., FuelRefill.Full)`. Every sim step, inside kiln.items' program, the containers get water back:
  `FuelRefill.Full` keeps them full, `FuelRefill.ChargesPerSecond(n)` adds n charges per second of game time; `Limit`
  (units) is the level kept or refilled to, and a container above it is left as it is. The program runs after the game's
  programs that pour and fill (`impact_simulation`, `actor_interaction`), so a can poured in a step is full again at the
  end of that step and players are sent a full can. The returned `FuelKeep` counts `UnitsAdded` and raises `Refilled`
  (the containers it changed since the last step); `WaterRefilledEvent` is the same on the event bus. Dispose the handle
  to stop; the mod's unload and a closing server world stop it too. It does not end when the entity goes away: a
  player's entity id is reused by the next player who joins, so dispose it when the player leaves (Kiln.Rules' `Water`
  rule does that: docs/kiln/services/rules.md). At most 256 at once.
- `FillAll(holder, IEnumerable<ItemId>)`: one fill per item, the results added up.
- Units are the game's: what players see is `NetworkFuel`, a 16-bit copy (docs/re/items.md section 11).
- Errors throw like `Read`: `NotSupportedException` when the runtime's kiln.items has no water functions (an older
  Kiln), `InvalidOperationException` off the tick thread.

## Moves, container names, transfer rules (v3)
Move a stack, a quick stack or a move all between inventories keeping the item entity (gear keeps its level, upgrades, gem and
durability), read the name a player gave a chest, and keep, deny or watch the game's quick stack and move all. Ported from
shroudlink (MIT). The guide, with examples, the limits and the client checklist: [modding/inventory.md](../modding/inventory.md).
Evidence: `docs/re/items.md` sections 13 and 14.

```csharp
MoveResult r = await Items.Move(player, sword.Slot, chest);     // same item entity, ledger in r.Changes
string? name = Items.ContainerName(chest);                      // the Rename button's text, or null
using var lockSlot = Items.Transfers.Keep(slot);                // R leaves this stack alone
```
C (header size >= `sizeof(KilnItemsApi)` of v3): `move` (a `KilnItemsMoveRequest`: STACK, QUICK_STACK or MOVE_ALL; callback with a
`KilnItemsMoveResult` and its ledger, tick thread Pre), `container_name` (tick thread), `transfer_rule` / `transfer_listen` /
`transfer_report` / `transfer_end` (the first registration installs the hooks), `status2`. A transfer handler runs inside the game's
call on its worker thread with keep, cancel, move, read and name (`KilnItemsTransferHost`); `cancel_owner` ends an owner's rules,
handlers and reports too.

Lab switches: `kiln/data/kiln.items/selftest` (a marker file) makes the service name a container through the game's own rename path and
run a native handler scenario when the test mod asks; `KILN_ITEMS_HOOKS=early` installs the transfer hooks at load.

## C (native plugins and services)
```c
const KilnItemsApi* it = api->service((KilnStr){"kiln.items", 10}, 1);   /* in on_ready */
KilnItemsRequest r = { sizeof r, KILN_ITEMS_LEVEL | KILN_ITEMS_RARITY, chest, 0x43108313u, 1, 20, 3 };
uint64_t ticket;
it->give(owner, &r, on_done, user, &ticket);       /* on_done(const KilnItemsResult*, user): tick thread, Pre */
```
`give`/`take` (any thread; refused at once with `KILN_ERR_INVALID_ARGUMENT` / `KILN_ERR_UNAVAILABLE`), `cancel`,
`cancel_owner`, `read` / `find_inventories` (tick thread, Pre or Post), `item_count` / `item_at` / `item_info` /
`find_item` / `item_id_of_guid`, `status`. v2 (check `header.size`): `fuel` (a `KilnItemsFuelRequest`: `SET`, `FILL` or
`ADD`, `INVENTORIES`, `DRY_RUN`, `limit`; the callback gets a `KilnItemsFuelResult`), `fuel_read` (tick thread),
`fuel_keep` (a refill policy: `FILL`, or `ADD` with `units` per second; an optional callback with what it refilled, once
per Pre) / `fuel_keep_end`. Details and every field: the header.

## How it runs
- A system ECS program `kiln_items` (other-write Inventory, InventorySetup, InventoryEmptyState, ItemState,
  OwnerRelationship, SlotSelection, Fuel; the item, balancing and attribute registries; the knowledge queue;
  InventoryFull and PickupEvent out) is registered through kiln.world, or by kiln.items itself when kiln.world is not
  loaded (`KILN_ITEMS_PROGRAM=auto|own|world` overrides). At its first update it checks every accessor against the
  world's component map; until then, and after any failed check or fault, give/take answer `Unavailable`.
- Requests queue (max 1024), run 16 per step inside the program, and their callbacks run in the next Pre. A request
  that has not run after 10 s comes back `Dropped`. Water requests have their own queue (64 per step) and run before
  give/take in the same update; keep-full entries run every update while any exist.
- Water writes: `WriteOther` on the program's other-write `Fuel` accessor with the job's tracker, the new value, then the
  attribute's formulas (`AttrChanged`), only where the value changes.
- Give to a player sets the operator to that player (PickupEvent receiver, knowledge unlocks); containers get neither.
- Reads use `GetComponent` on the tick thread; `FindInventories` uses kiln.world's `find` (or the service's own scan
  of the entity map).

## For Kiln.Admin
| op | Kiln |
|---|---|
| `inventory {entity}` → `{inventories:[{link, entity, category, slots:[{item, count, pide}]}], generic, available}` | `Items.Read(entity)`: group `Slots` by `Slot.Inventory` (`Link`, `Category`), `GenericSlots`, `AvailableSlots`. Names: `Items.Get(slot.Item)?.Label` |
| `give_item {entity, item, count 1..100000, maxStack}` → `{given, requested, stacks}` | `await Items.Give(entity, new ItemId(item), count, gear, GiveOptions.Notify)` → `{given = r.Done, requested = r.Requested, stacks = r.Changes.Count}`. `maxStack` is no longer needed (the game knows it); gear works: pass `new Gear(level, rarity)`; `Partial` / `NoRoom` say when the backpack is full |

## Client checklist (needs a joined player; not verifiable in a player-free lab)
1. Give a stack to a player with `Notify`: the item shows in the backpack, "+N item" popup appears, PickupEvent fires.
2. Give a blueprint / first-time item to a player: the knowledge unlock (recipe) appears.
3. Give gear (`Gear(level, rarity)`) to a player: it shows the level (clamped to the item's range; a new item unless it opts in to
   rolled loot has one level) and rarity, perks match the rarity, a weapon lands
   where the game puts new weapons (hotbar or backpack); with "prioritize backpack" set in the client settings the game's
   own pickups go to the backpack, kiln.items does not apply that setting (yet).
4. Fill a player's backpack and give more: `Partial` / `NoRoom`, nothing lost, no ghost items in the client UI.
5. Take from a player: from the held action-bar slot first; taking an equipped piece of gear unequips it and the item
   disappears for the client; stats (armor, weapon) update.
6. `Items.Read(player)`: backpack, action bars, equipment links and categories look as in the client.
7. Disconnect while a request is queued: the result is `NoInventory` (or `Done` if it ran first); no crash.
8. Give to a player still loading in: `NoInventory` "not initialized yet".

## Lab results: water (a lab with a copied world, no player)
`mods/tests/fuel-test`, three boots: boot 1 30 PASS, boot 2 5 PASS, boot 3 6 PASS, 0 FAIL.
```
PASS Set(can, 1234): Done: 1 of 1 container(s) changed, now 1234/6400 (19.3/100 charges)
PASS players are sent it (NetworkFuel 1234)
PASS FillAll(chest, can) fills only cans: ...; can 6400/6400, pouch 0/1280
PASS drained 20 times, full again at the next step 20 times
PASS 10 charges a second for ~1 s: ... units; it stops at its limit (1000) and fills only cans
PASS keep full up to 3200: can 3200/6400 (capped), pouch 1280/1280
PASS after the keep is disposed a drain stays: 100/6400
PASS written on loaded containers: can 4321 (tracked), pouch 777 (tracked), control 3000 (raw)
PASS the can kept the tracked write: 4321/6400 (67.5/100 charges)
PASS the pouch kept the tracked write: 777/1280 (12.1/20 charges)
PASS the control can lost the raw write, as the game's save skips untracked writes: 1000/6400 (raw 3000, saved 1000)
```
A container created in the same session is saved whole at the next save, raw writes included; the control proves the
difference only on a container a save already wrote, so the test writes in boot 2 and reads in boot 3.

## In a game client (hosted worlds)
- The own-registration fallback (RegisterAllPrograms hook) skips worlds whose kind is not 0 (a client's client and
  prediction worlds); with kiln.world the program goes into the server world only.
- When the game closes the server world, queued requests end as DROPPED ("the server world closed"), and the next
  world's first program run verifies its accessors and item registry again (they are per world).
