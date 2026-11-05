# kiln.items: items in inventories

Give items to and take them from any inventory (players' backpacks, chests, villagers), gear with a level and rarity
included; read an entity's inventory; look items up by name. Everything goes through the game's own inventory
transaction (the code its admin "create item", loot pickup and crafting use), so stacks merge, gear gets its own item
entity, and the change is replicated and saved like a vanilla one. Evidence and layouts: `docs/re/items.md`.

| Layer | Where |
|---|---|
| Symbols | `sigdb/symbols/server.d/items.json` (features `items.transaction`, `items.program`, `items.read`, `items.catalog`, `items.scan`, `items.recipe`) |
| C table | `native/core/abi/services/kiln_items.h` (`kiln.items` v1) |
| Native | `native/services/items/` → `kiln/services/kiln-items.dll` |
| Managed | `managed/services/Kiln.Items/` → `kiln/host/Kiln.Items.dll` (namespace `Kiln`, class `Items`) |
| Test | `mods/tests/items-test/` (PASS / FAIL lines in kiln.log); `mods/tests/craftmatch-test/` (rarity, level, perks of made items) |

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

## C (native plugins and services)
```c
const KilnItemsApi* it = api->service((KilnStr){"kiln.items", 10}, 1);   /* in on_ready */
KilnItemsRequest r = { sizeof r, KILN_ITEMS_LEVEL | KILN_ITEMS_RARITY, chest, 0x43108313u, 1, 20, 3 };
uint64_t ticket;
it->give(owner, &r, on_done, user, &ticket);       /* on_done(const KilnItemsResult*, user): tick thread, Pre */
```
`give`/`take` (any thread; refused at once with `KILN_ERR_INVALID_ARGUMENT` / `KILN_ERR_UNAVAILABLE`), `cancel`,
`cancel_owner`, `read` / `find_inventories` (tick thread, Pre or Post), `item_count` / `item_at` / `item_info` /
`find_item` / `item_id_of_guid`, `status`. Details and every field: the header.

## How it runs
- A system ECS program `kiln_items` (other-write Inventory, InventorySetup, InventoryEmptyState, ItemState,
  OwnerRelationship, SlotSelection, Fuel; the item, balancing and attribute registries; the knowledge queue;
  InventoryFull and PickupEvent out) is registered through kiln.world, or by kiln.items itself when kiln.world is not
  loaded (`KILN_ITEMS_PROGRAM=auto|own|world` overrides). At its first update it checks every accessor against the
  world's component map; until then, and after any failed check or fault, give/take answer `Unavailable`.
- Requests queue (max 1024), run 16 per step inside the program, and their callbacks run in the next Pre. A request
  that has not run after 10 s comes back `Dropped`.
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

## In a game client (hosted worlds)
- The own-registration fallback (RegisterAllPrograms hook) skips worlds whose kind is not 0 (a client's client and
  prediction worlds); with kiln.world the program goes into the server world only.
- When the game closes the server world, queued requests end as DROPPED ("the server world closed"), and the next
  world's first program run verifies its accessors and item registry again (they are per world).
