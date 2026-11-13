# kiln.items: items in inventories

Give items to and take them from any inventory (players' backpacks, chests, villagers), gear with a level and rarity
included; read an entity's inventory; look items up by name. Everything goes through the game's own inventory
transaction (the code its admin "create item", loot pickup and crafting use), so stacks merge, gear gets its own item
entity, and the change is replicated and saved like a vanilla one. Evidence and layouts: `docs/re/items.md`.

| Layer | Where |
|---|---|
| Symbols | `sigdb/symbols/server.d/items.json` (features `items.transaction`, `items.program`, `items.read`, `items.catalog`, `items.scan`, `items.recipe`, `items.fuel`, `items.pide_options`, `items.move`, `items.transfer`, `items.names`, `items.instances`, `items.equip`) |
| C table | `native/core/abi/services/kiln_items.h` (`kiln.items` v5: v1 + water + moves, container names, transfer rules and events + item instances + equip and unequip) |
| Native | `native/services/items/` → `kiln/services/kiln-items.dll` |
| Managed | `managed/services/Kiln.Items/` → `kiln/host/Kiln.Items.dll` (namespace `Kiln`, class `Items`) |
| Test | `mods/tests/items-test/` (PASS / FAIL lines in kiln.log; v3: `MovesTest.cs`; v5: `EquipTest.cs`); `managed/tests/Kiln.Items.Tests` (offline); `mods/tests/craftmatch-test/` (rarity, level, perks of made items); `mods/tests/fuel-test/` (water, three boots); `native/services/items/tests/run.sh` (host tests under Wine); `mods/tests/instances-test/` (entity refs, item instances, following items) |

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

## Item instances (v4): one particular item
Capability `items.instances` (sigdb feature of the same name: kiln.items v4 is there and its reads resolved). When it is
off (an older Kiln, an unknown game build: no offset is guessed), every member below throws
`KilnUnavailableException("items.instances")`, and the `Ref` fields of results and reads are `ItemInstanceRef.None`.
`Items.Held`, `Items.Equipped` and `Location` carry `[RequiresCapability("items.instances")]`.

The row comes from sigdb; Kiln.Items only lowers it, at its start (kiln.items decides the feature once, when it resolves):

| kiln.items has | The row | `Capabilities.Why("items.instances")` says |
|---|---|---|
| no table | unavailable, the reason of `kiln.items` (usually `service-missing`) | `items.instances is unavailable: kiln.items is unavailable: <why kiln.items is not there> (<reason>, from capabilities).` |
| a table without v4 | unavailable, `service-older` | `items.instances is unavailable: kiln.items v<n> has no item instances (update Kiln) (service-older, from capabilities).` |
| v4 with the feature turned off | unavailable: the reason of `items.read` with `needs items.read: <why>` when the inventory reads are what is off, else `faulted` with kiln.items' status detail (unless sigdb said why already) | `items.instances is unavailable: <that text> (<reason>, from capabilities).` |
| v4, on | sigdb's row stands | `items.instances is available.` |

The feature also goes off if kiln.items ever ran out of stamps (2^32 - 1 item entities in one process); the row does not
follow that, the members throw.
Reads only: nothing here writes a game component, so a client without Kiln sees nothing new.

| Member | Side, thread | What |
|---|---|---|
| `InventorySlot.Ref` | any (a value) | the item in the slot with the stamp of this read; `None` for an empty slot, a plain stack (no item entity), or the capability off |
| `ItemChange.Ref` | any (a value) | the item a give created or a take destroyed, with the stamp of its commit (a take's ref is not alive afterwards); `None` for a plain stack or a dry-run give |
| `MoveChange.Ref` | any (a value) | the item entity that moved; equal to its `Ref` in a read before and after |
| `Items.Held(player)` | `[ServerOnly][TickThreadOnly]` | the item in hand: what the running action uses, else the selected action-bar slot; `None` when empty, a plain stack, or not a player |
| `Items.Equipped(player, EquipmentSlot)` | `[ServerOnly][TickThreadOnly]` | the item in one of the 20 equipment slots of the game's `SlotSelection` (`EquipmentSlot` has the values of Kiln.Players' `PlayerEquipSlot`) |
| `item.Location()` (`ItemInstanceExtensions`) | `[ServerOnly][TickThreadOnly]` | `ItemLocation?`: where the item is now; null when it is gone or the ref is stale |
| `ItemLocation(Holder, Slot, Link, FlatSlot)` | value | `IsPlaced`, `IsSavedSlot`; see "Flat slots" |
| `ItemMovedEvent` (`Item`, `From`, `To`, `Cause`, `Step`) | `[ServerOnly]`, tick thread | a followed item moved; posted only while it has subscribers |
| `ItemMoveCause` | value | `Transaction` (Kiln's own stack move), `Transfer` (a quick stack or move all), `Observed` (the per-step read) |

`Held` and `Equipped` read `SlotSelection` by the game's own field names (`kiln.types`), as kiln.players does; without the
reflection they throw `items.instances` too. The data calls on an item (`Get`, `Set`, `Has`, ...) are in Kiln.Saves; Kiln.Saves
also decides which items are followed. Lab-only members (hidden, `[Experimental("KILNX001")]`): `Items.LabFollow(item, gone)`,
`Items.LabMoveUnseen(from, slot, to)` (a move kept from the tracker), `Items.LabInstancesStatus`.

```csharp
InventorySlot s = Items.Read(chest)!.Slots.First(x => !x.Ref.IsNone);
ItemInstanceRef sword = s.Ref;                          // this sword, not "a sword"
ItemLocation? where = sword.Location();                 // holder, slot, link, flat slot; null once it is gone
MoveResult m = await Items.Move(chest, s.Slot, other);
bool same = m.Changes[0].Ref == sword;                  // true: the item entity moved whole, stamp and all
ItemInstanceRef inHand = Items.Held(player);
```

### The stamp, and why it is enough
Every item entity (the "pide" of a stack of gear, a tool, a can) gets a 32-bit sequence number, never 0, new for every
item entity the process sees, delivered where the item entity is delivered: `KilnItemsSlot.reserved` (captured at the
read), `KilnItemsChange.reserved` and `KilnItemsMoveChange.reserved` (captured at the commit). An `ItemInstanceRef`'s
serial is that number in the Items place of `EntityRef` (`IdentitySources.Stamp(Items, n)`). Instance data is keyed by
(entity id, stamp), and every access compares both.

The requirement: when an item entity is destroyed and its id is given to another item entity, no caller may ever see
the old stamp for the new one. Evidence (server 26430253, exe `f0d824be…`; the same on 26404334), static:
- **One counter hands out every non-player id.** `createEntity` (`0x623ce0`) called with id 0 takes the next id with
  `lock xadd dword [storage + 0x498], 1` (`0x6247c3`). The job command queues' queued create (`0x5fb340` → `0x5fb3d0`),
  which createPide (`0x1b1920`, `call` at `0x1b1e9d`) uses for every new item entity, takes its id at queue time with
  `lock xadd` through the queue's pointer `+0x38` (`0x5fb49e`); the storage's setup stores `&storage[0x498]` into every
  queue's `+0x38` (`0x62d556`, `0x62d5a2`). The record loader (`0x7abd60`) creates item entities through `0x620ce0`,
  createEntity with the read-only id `0` at `0x1301130`. sigdb captures `0x498` from both places and refuses to start the
  feature unless they agree (`items/storage.next_id`, 2 votes; `items/queue.next_id`, 2 votes).
- **The counter only grows in a running world.** Its only writers in the ECS code are the storage constructors
  (`0x5f6ce8`: `:= 1023`) and `deserializeEntityGrid` (`0x613f8c`: `:= 1023`, or the value of the snapshot it loads at a
  world's set-up). Nothing that destroys an entity touches it (`destroyEntity` `0x63e8a0` removes the record only).
- **Explicit ids** come only from player slots (`0x628e20`: id = slot + 1, players 1..1023), the network's create message
  on a client (`0x677ba8`), the queued creates' own pre-taken ids (`0x6bf473`), and an engine sub-range with bit 31 set
  (`0x66e94e`). kiln.items never stamps an id below 1024 or with bit 31 set.

So within one world an id never names a second entity. The stamp table is keyed by id, forgotten when a server world
closes or starts, and also forgotten if the counter (read every Pre) is ever seen to go down. A take's commit leaves a
tombstone: the id reads as gone from then on, in the same step and the next. An id whose `ItemState` names another item
than its entry is treated as another item entity (a new stamp). The case nothing in kiln.items could see by itself (the
game frees an id and gives it to another item entity of the same item in the same world, with no signal) is the one the
counter rules out; the host test shows what the table would do then. Lab evidence: "Lab results: item instances" below.

### Reads
`read_instances` reads N item entities in one call (tick thread, Pre or Post): found or not, stamp, item id, holder
(the entity whose `InventorySetup` links the inventory), slot, link, flat slot, count, and the fields the game's record
builder (`0x72a9b0`) writes into the item's save record, read the same way: level (the `Level` attribute; the record keeps
its low byte), durability current and max, fuel current and max (through the attribute registry, the block must have
values and the attribute's group), dye (`ColorPaletteItem`), perks available and unlocked (`PerkContainerNew`, else the
older `PerkContainer`, counted up to its first empty perk), gem item and level (`WeaponGemSlot`; the level is the gem's
own item entity's `Level`). `KILN_ITEMS_READ_LOCATION` skips the record fields. `read_holder` reads every item entity of
one holder in flat-slot order. `instances_status` counts stamps, resets and mismatches and shows the id counter.

### Flat slots
The world and character savers (`0x72a520`, `0x72c6eb`) walk the 16 links of a holder's `InventorySetup`, save only
**Generic** (4) links, and number their slots `(Generic links before this one) x 8 + index`: a chest of three Generic
links has slots 0-23, link 1 index 5 is slot 13. That is `ItemLocation.FlatSlot` 0-255 (`IsSavedSlot`). A slot in a link
the savers do not number this way (equipment, currency, customization, a Virtual link's nested inventories) gets
`256 + (its inventory's position in the link tree) x 8 + index`: unique in the holder, stable while the links are, but not
a number the game saves.

### Following items: what the hooks see, and what only the per-step read sees
Kiln follows the items Kiln.Saves gives data (and `Items.LabFollow` in labs); nothing is read per step while nothing is
followed. In order:
1. Kiln's own `Items.Move` (a stack) names the item entity in its result: cause `Transaction`. A take that destroys a
   followed item reports it gone at once.
2. A transfer report (the game's quick stack or move all, also `Items.QuickStack` / `Items.MoveAll`) makes the tracker
   read again at once the followed items whose holder is the report's from or to: cause `Transfer`. A followed item makes
   the tracker register one report listener, which installs kiln.items' two transfer hooks (never removed; chained).
3. The per-step read catches the rest, within a budget: cause `Observed`, reported when two reads in a row agree, so a
   hooked report of the same move wins. 1 and 2 are not limited by the budget.
4. A fresh read through Kiln.Items' internal face (Kiln.Saves reads every item with data at each save) brings a followed
   item's location up to date (as an `Observed` move when it changed): the tracker is never older than the last such
   read. Kiln.Saves learns of such a move inside the read; mods get its `ItemMovedEvent` at the start of the next
   step, so no mod handler runs in the middle of a save's staging.

A followed item that is gone (not found, or the id holds another item entity) is reported once with its last placed
location and is no longer followed. A read that finds an item entity that exists but sits in no holder's slot changes
nothing: no move, no candidate, the last placed location stays. That is what a holder's teardown looks like: the holder
(a player leaving, a chest destroyed) goes in one call and its item entities one step later (the leave-order
investigation, `out/instances/t3/leave-order.md`), so the gone that follows carries the location in that holder.

#### The budget
Each step the per-step read reads, in one native call, the next **64** followed items of a rotation (in the order they
were followed) and, outside the rotation, up to **64** items a read found somewhere new one step earlier, until each is
reported or back where it was. Only a read that finds an item somewhere new adds such an item, so the rotation adds at
most 64 a step, and a step reads at most 128 items, whatever the number followed (lab cost below). With N items
followed:
- every item is read at least once every ceil(N / 64) steps;
- a move no hook names is reported at most **ceil(N / 64) + 1** steps after the step it happened in (the rotation's read
  that sees it, then the confirming read; it waits a step longer only when more than 64 items keep moving every step);
- a gone item is reported at most **ceil(N / 64)** steps after it went (no confirmation is needed).

| Followed | Read every | A move reported within | A gone item within | the move, at 20.9 steps/s (lab) | the move, at 60 steps/s (a hosted world) |
|---|---|---|---|---|---|
| 64 or fewer | 1 step | 2 steps | 1 step | 0.1 s | 0.03 s |
| 1,000 | 16 steps | 17 steps | 16 steps | 0.8 s | 0.3 s |
| 10,000 | 157 steps | 158 steps | 157 steps | 7.6 s | 2.6 s |

A dedicated server without players steps at about 21 to 25 steps a second. Nothing a save writes depends on this lag:
the store reads every item with data and every holder's item list fresh at each save (below).

Moves only the per-step read sees (they reach the inventories through the game's slot transfer `0x1b4550`, take/put
`0x1b9620` / `0x1b7100`, or its equip path, none of which kiln.items hooks; docs/re/items.md section 13 and the item
lifecycle notes):
- a player's single drag and drop, swap, split (half, one, a custom amount) between any two slots: the client's
  `inventoryTransferAction` without the RefillStacks / MoveAllStacks flags (`0x19f077`);
- a move between backpack and action bar (the ToActionbar / ToBackpack flags, `0x19f15c`);
- a player's own equip and unequip, and swapping the equipment set (`equipment_actions` `0x1472c0` → equipItem `0x1b4e70`
  → moveStack; `swap_equipment_set_actions` `0x147ee0`), except the move all equipItem runs itself (`0x1b5457`,
  `0x1b548a`), which the hook reports. Kiln's own `Items.Equip` / `Items.Unequip` name the item entities in their result
  (cause `Transaction`);
- a gift to another player (the Gifting flag; the same slot transfer);
- death: the items that go to the tombstone (`player_items_loot` `0x1aa7a0`, take and put per slot);
- the slot transfer the actor interaction code calls (`0x7ca86`);
- another loader's or plugin's own transaction (EMF's moveStack, shroudlink's `move_stack` outside quick stack).
Not moves: trash, destroy, crafting use, a player leaving (the item entity is destroyed: gone), a pickup, a join and a
world load (new item entities).

### Lab test
`mods/tests/instances-test/` (PASS / FAIL lines, "instances-test done: N PASS, M FAIL"): deploy it into a lab with a
copied world and no player, start, read kiln.log. Check 5 needs a restart: the first boot leaves
`kiln/data/instances-test/boot1.done` and waits for an autosave; stop and start the lab; the second boot compares.
`KILN_LAB_NO_ENTITY_SOURCES=1` starts the server without the entity sources (to see the identity checks fail).

## Equip and unequip (v5, since 0.2.0-dev.27)
Put an item a player has on, or take one off, through the game's own equip and unequip: the two functions its
`equipment_actions` program runs for a player's equip action, with all their checks. Capability `items.equip` (sigdb
feature of the same name; Kiln.Items lowers it when the table is older than v5, kiln.world is not loaded, or kiln.items
switched equip off). Evidence: docs/re/items.md section 16.

```csharp
InventorySlot ring = Items.Read(player)!.Slots.First(s => s.Item == ringId && !s.IsEquipped);
EquipResult on = await Items.Equip(player, ring.Slot, EquipmentSlot.Ring1);   // a named slot
EquipResult own = await Items.Equip(player, ring.Slot);                       // the slot the item names (null)
EquipResult off = await Items.Unequip(player, EquipmentSlot.Glider);         // into a free backpack slot
if (!on.Ok) Log.Info($"not equipped: {on.Reason}");                          // the one error rule: IKilnResult
```

| Member | Side, thread | What |
|---|---|---|
| `Items.Equip(player, from, slot = null, options = None, itemEntity = default)` → `Task<EquipResult>` | `[ServerOnly][TickThreadOnly]` | the stack in `from` (a slot of the player's own inventories) goes into `slot`, or into the item's own slot; what the slot held comes off as the game swaps it. `itemEntity`: the item entity the slot must still hold |
| `Items.Unequip(player, slot, options = None)` → `Task<EquipResult>` | `[ServerOnly][TickThreadOnly]` | what the slot holds goes into a free Generic (backpack) slot |
| `EquipOptions.DryRun` | value | run the game's call and roll it back: the result says what would happen |
| `EquipResult` (`IKilnResult`) | value | `Ok`, `Reason`, `Status`, `Player`, `Slot` (for the item's own slot: where it went), `Unequipped`, `Item`, `ItemEntity`, `Ref`, `From`, `To`, `Replaced`, `ReplacedEntity`, `ReplacedRef`, `ReplacedTo`, `Message`, `GameError`, `Steps`, `DryRun` |

The one error rule:
- **refused** (a result, `Ok == false`, nothing changed): `SlotChanged` (the slot is not one of the player's, is empty, or
  holds another item entity; an unequip of an empty slot), `NoInventory` (no player character with that id in the
  world), `NoRoom` (an unequip with no free backpack slot: the game also shows the player its "inventory full" notice, as
  for their own unequip), `Refused` (the game refused: the item's EquipmentSlotType is not the slot's, `GameError` 0x11;
  or Kiln refused: the player is dead), `Unavailable` / `Dropped` (equip went off, or the server world closed, after the
  call);
- **bad argument** (`ArgumentException`): player or slot none, a value outside `EquipmentSlot`;
- **capability off** (`KilnUnavailableException("items.equip")`): an older Kiln, a game build without the two functions,
  no kiln.world, equip switched off after a fault.

How it runs:
- The game's equip takes the player from the running program's **current entity**, not from an argument, so it can only
  run in a program that iterates the player. kiln.items registers a second program through kiln.world,
  `kiln_items_equip` (an entity program over every entity with `PlayerInput`, with kiln_items' accessors), and builds
  the transaction there as `equipment_actions` does. Without kiln.world there is no equip.
- A request runs in the next sim step; its task completes on the tick thread one step later. At most 4 requests of one
  player run per step, 1024 wait at most; a request that has not run after 10 s comes back `Dropped`.
- **With the player's own input**: the game's `equipment_actions` runs first in a step, `kiln_items_equip` later. Both
  happen, the player's first; a request whose slot the player just emptied comes back `SlotChanged`. Kiln never writes
  the player's input (`PlayerInput`, `ServerConsumedPlayerInput`), so no action of the player is held, skipped or run
  twice.
- A refusal rolls the whole transaction back: the inventory is as it was.
- The change reaches players through the game's own replication; equipment is not predicted by a client
  (`equipment_actions` is host only), so a player's game shows the server's equipment. Kiln.Players'
  `PlayerEquipmentChangedEvent` follows within four steps, as for the player's own equip; Kiln.Saves' followed items
  see the move at once (cause `Transaction`).
- A dead player: Kiln refuses when the call is made (`Actor.currentState` has the Dead state; the game's own equip has
  no such check). The C table does not check it.
- The vanity slots take armour of the same body part (the game's table); the four building-material values of the game's
  `EquipmentSlot` are not equipment slots (`SlotSelection.equipment` has 20 entries) and are not offered.

C (v5, header size >= `sizeof(KilnItemsApi)` with `equip`): `equip(owner, KilnItemsEquipRequest*, done, user, &ticket)`
(`equipment_slot` is the game's `keen::EquipmentSlot`: 0-16, 21 HookShot, 22 Glider, 23 Backpack, or
`KILN_ITEMS_EQUIP_ANY`; flags `KILN_ITEMS_EQUIP_UNEQUIP`, `KILN_ITEMS_EQUIP_DRY_RUN`), the callback gets a
`KilnItemsEquipResult` on the tick thread in Pre; `equip_status` says whether it runs and why not. `cancel(ticket)` and
`cancel_owner` drop the callback.

Lab: `mods/tests/items-test` (`EquipTest.cs`, on a spawned `1_Player` character; `kiln/data/items-test/equip-only` runs it
alone): unequip, equip into a named slot, a swap, the item's own slot, a dry run, `PlayerEquipmentChangedEvent` after each,
and every refusal above leaving the inventory as it was: 30 PASS on server 26430253 (lines in docs/re/items.md section 16).
A dry run into the item's own slot cannot name the slot (`Slot` null): the game writes the selection only at its commit.
What a player sees is the guided checks `items.equip` and `items.unequip` (mods/tests/client-pass, session 1).

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
per Pre) / `fuel_keep_end`. v4 (header size >= `sizeof(KilnItemsApi)` of v4; feature `items.instances`):
`read_instances(items, count, item_size, flags)` and `read_holder(holder, out, capacity, item_size, &count, &initialized)`
fill `KilnItemsInstance` records (stamp, item, holder, slot, link, flat slot, count, the save record's fields; tick thread),
`instances_status` (any thread); the stamps are in the `reserved` fields of `KilnItemsSlot`, `KilnItemsChange` and
`KilnItemsMoveChange` (0 from an older service). Details and every field: the header.

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

## Lab results: item instances (lab `dev25-instances`, a copy of a real world, no player; server 26430253)
`mods/tests/instances-test`, two full runs (94 PASS, 0 FAIL in the second), check 5 across a restart (10 PASS, 0 FAIL).
- **Ids are never handed out twice.** 43 new entities in one run (30 creatures destroyed and spawned again in the same
  tick callback, tracked and untracked, plus item entities given, taken, and a take and a give in one step):
  "entity ids handed out again: 0 of 43 new entities". Every old ref was not alive at once and `EntityRef.Of(old id)` was
  none; the stamps of the new ones differed. The SpawnTime of a respawn in the same callback was never the old one's
  (the old one was spawned a step earlier).
- **A tracked spawn with a SpawnTime** reports the SpawnTime as its stamp, not its tracker serial: "tracked True,
  record serial 1, SpawnTime 24383333823", stamp unchanged over 30 steps.
- **One item, one ref:** the read's `Ref`, the give's and the move's are equal; `Location()` follows the move; a plain
  stack has none; after the take the ref is not alive and has no location.
- **Following:** `Items.Move` → `Transaction` (step 650), `Items.MoveAll` → `Transfer` (the next step), a move kept
  from the tracker → `Observed` (step 661, after the per-step read had seen it twice; the steps since 651 include the
  moves that put the move all back), the take → gone once with its last location; 3 events for 3 moves.
- **Flat slots across a restart:** three swords given into link 1 of three stations (flat slots 8, 8, 9); after a stop
  and a start the holders came back as other entities (1153 → 1156, 1150 → 1152), found by template and position, and
  each sword was at the same flat slot, as a new item entity in a new inventory entity. The world save written before the
  restart holds the swords' records with slot bytes 8, 8 and 9 (`entity_chunk00`), the numbers kiln.items reported.
- **What the per-step read costs**, with the budget (64 a step) and without (every followed item every step, the lab
  switch `Items.LabTrackerReadsPerStep = 0`): one native location read per step, µs, average of 64 steps and the worst
  step, two lab runs of two passes each (the lab at nice 19; the PC was busy: load average 20 to 31, other packages
  building). 10,000 item entities: 420 spawned 24-slot containers (`Prop_Decoration_T1_Storage_24`).

  | Followed | budget, average | budget, worst | without, average | without, worst |
  |---|---|---|---|---|
  | 10 | 6.1 to 22.6 | 9.1 to 706 | 6.8 to 36.9 | 10.3 to 1,022 |
  | 100 | 28.8 to 42.3 | 82 to 809 | 33.0 to 161.7 | 47 to 5,371 |
  | 1,000 | 25.2 to 72.0 | 36.5 to 2,535 | 291 to 380 | 1,266 to 2,381 |
  | 10,000 | 21.3 to 24.5 | 34.6 to 37.8 | 2,328 to 2,372 | 6,622 to 8,637 |

  With the budget a step reads 64 items (10 when fewer are followed) whatever the number followed; the worst steps above
  a few dozen µs are the scheduler (they appear without the budget at 10 items as well). Without it the cost grows with
  the number followed: 2.3 ms a step at 10,000.
- **The bound holds:** 1,000 followed, the last item of the rotation moved by a move the hooks do not see right after the
  rotation had read it: reported 18 steps after the step the move was asked in (it ran in the next one: 17 = ceil(1,000 /
  64) + 1).
- **A holder's teardown is not a move:** a container with 5 followed items destroyed (`Spawn.Destroy`): the next read
  found them placed nowhere and reported nothing; the read after that reported each gone once, with its last placed
  location in the container (destroyed at step 1634, gone at 1636; budget 0, so the one-step gap was read for sure).
- **Census** (ids 1 to 2681 after a start): 67 item stamps, 1,219 SpawnTime serials, 232 entities without a serial (save
  props and scatter, the linked inventory entities, stations, NPCs), 1,163 ids absent.
- With `KILN_LAB_NO_ENTITY_SOURCES=1` check 1 and check 2 fail ("no service supplies entity serials in this process").

## In a game client (hosted worlds)
- The own-registration fallback (RegisterAllPrograms hook) skips worlds whose kind is not 0 (a client's client and
  prediction worlds); with kiln.world the program goes into the server world only.
- When the game closes the server world, queued requests end as DROPPED ("the server world closed"), and the next
  world's first program run verifies its accessors and item registry again (they are per world).
