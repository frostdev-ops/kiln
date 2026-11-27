# kiln.items: items in inventories

Give items to and take them from any inventory (players' backpacks, chests, villagers), gear with a level and rarity
included; read an entity's inventory; look items up by name. Everything goes through the game's own inventory
transaction (the code its admin "create item", loot pickup and crafting use), so stacks merge, gear gets its own item
entity, and the change is replicated and saved like a vanilla one. Evidence and layouts: `docs/re/items.md`.

| Layer | Where |
|---|---|
| Symbols | `sigdb/symbols/server.d/items.json` (features `items.transaction`, `items.program`, `items.read`, `items.catalog`, `items.scan`, `items.recipe`, `items.fuel`, `items.pide_options`, `items.move`, `items.transfer`, `items.names`, `items.rename`, `items.instances`, `items.equip`, `items.gear`, `items.gear.rolls`); `sigdb/symbols/server.d/dev29-10-equipment-sets.json` (`items.equip_sets`, used by the client unchanged) |
| C table | `native/core/abi/services/kiln_items.h` (`kiln.items` v6: v1 + water + moves, container names, transfer rules and events + item instances + equip and unequip + equipment sets) |
| Native | `native/services/items/` → `kiln/services/kiln-items.dll` |
| Managed | `managed/services/Kiln.Items/` → `kiln/host/Kiln.Items.dll` (namespace `Kiln`, class `Items`) |
| Test | `mods/tests/items-test/` (PASS / FAIL lines in kiln.log; v3: `MovesTest.cs`; v5: `EquipTest.cs`); `mods/tests/equipment-sets-test/` (v6); `managed/tests/Kiln.Items.Tests` (offline); `mods/tests/craftmatch-test/` (rarity, level, perks of made items); `mods/tests/fuel-test/` (water, three boots); `native/services/items/tests/run.sh` (host tests under Wine); `mods/tests/instances-test/` (entity refs, item instances, following items) |

## Persistent player storage (since 0.2.0-dev.30.7)

`Items.RegisterPlayerStorage` registers an independent inventory for each player, persisted by Kiln.Saves and paired
with the character save. Use its holder with the inventory operations on this page. It is not part of the backpack,
does not pay crafting costs, and is excluded from default inventory scans. Read the
[player storage contract](player-storage.md) and [modding guide](../modding/player-storage.md) for filters, death rules,
parking, travel and trust limits.

## Give straight into equipment (since 0.2.0-dev.30.6)

`Items.GiveEquip(player, item, count, EquipmentSlot slot, GiveEquipOptions? options = null)` returns
`Task<EquipResult>`. It creates **one stack** directly in equipment and equips it in **one game inventory
transaction**. An empty gear/vanity target needs no free backpack slot. Gear uses the game's default level and rarity and is
created with its item entity and normal instance data in its final equipped cell, never in an intermediate backpack
slot. The item's `EquipmentSlotType` must fit, just as for `Items.Equip`.

```csharp
var result = await Items.GiveEquip(player, ringId, 1, EquipmentSlot.Ring1,
    new GiveEquipOptions { Replaced = Replaced.Take });
if (!result.Ok) Log.Info(result.Reason);
else Log.Info($"replaced {result.ReplacedCount} x {result.ReplacedItem}");
```

- **Arrow, Spell and FishingBait** are selected in place on a verified Generic backpack page, never in a spare
  Equipment page cell. The item must have the matching physical slot type and the Ammunition category, and must not
  carry its own inventory.
  With a zero selection, a real empty Generic cell not on the action bars or reserved by another equipment selection
  is required, even for `Replaced.Take`. A valid already-selected Generic cell is reused, empty or occupied.
  Occupied contents must still resolve to Ammunition of the selected logical slot's type; stale, unknown or
  non-ammo contents return `SlotChanged` before mutation, including Take and DryRun. A different valid spell,
  arrow or bait of the matching type can still replace the old one; item identity need not match.
  Missing/short Inventory components and unverified Virtual backpack setups are not free space. A selected non-Generic,
  foreign, action-bar or shared cell returns `SlotChanged` before mutation; this call does not repair or migrate it.
- **Instance-backed ammo (eternal spells and arrows), since 0.2.0-dev.31.12.** An eternal spell is an item entity, not
  a plain stack. `GiveEquip` gives one (count 1) the way the game equips one from the backpack: it is created once,
  with its own item entity, directly in the Generic cell above, and selected there with the entity marked equipped.
  Its identity is that entity: the result's `ItemEntity` and `Ref` name it, and nothing moves it afterwards. A
  replaced eternal keeps its entity: `Replaced.Backpack` unequips it and moves it to a free Generic cell
  (`ReplacedEntity` names the same entity), `Replaced.Take` destroys it. Plain and eternal ammo can replace each other.
  A selected eternal whose item entity is missing, or whose entity's own state no longer says it lies in that cell
  as that item, is stale and returns `SlotChanged` before mutation. This needs the
  sigdb feature `items.give_equip.instance_ammo` (both approved builds have it); without it, or if the game's own
  equip decision does not place this item in the backpack, an eternal is refused before any change with a reason
  naming it. Plain ammo is unchanged and never needs that feature.
- `Replaced.Backpack` (default) preserves the old stack and its instance in an empty Generic backpack slot.
  An occupied target with a full backpack returns `NoRoom` **before mutation**. An empty gear/vanity target still works;
  selected empty ammo reuses its verified Generic cell without needing a second cell.
- `Replaced.Take` **destroys** the old stack: it and its item entity are removed from the game, not moved or
  dropped anywhere. The result reports `ReplacedItem` (an alias of `Replaced`), `ReplacedCount`, and the old instance
  identity. It works with a full backpack when the final cell is valid (ammo still requires its selected Generic
  cell, or a free Generic cell for a zero selection). `ReplacedTo` is None for a taken stack.
- `DryRun = true` runs and rolls back the whole transaction. No inventory/selection changes, new item entity or
  `EquipChangedEvent` survives. The result reports the old stack and the predicted destination, but no new instance.
- Always all-or-nothing: no partial give, no duplicated or missing item on an ordinary refusal. Count is 1..65535,
  must fit one item stack, and must be **1 for gear and eternal ammo**. `From` is None. Ordinary `Equip`/`Unequip` keep
  `ReplacedCount = 0`. A successful committed GiveEquip posts one `EquipChangedEvent` on the tick thread.
  A fault inside the game's commit is different from an ordinary refusal: the result is `Unavailable`, equipment
  calls are disabled until restart, and the reason warns that items may have changed. Kiln never attempts an unsafe
  second rollback after item-instance creation has begun.
- **Limitation:** giving a backpack item into `EquipmentSlot.Backpack` is refused with
  "backpack items can't be given straight into the bag slot yet". Its nested inventory is only created at commit;
  this operation does not guess how to transfer backpack contents. Other equipment slots, including vanity, are supported.
- `items.give_equip` / `Items.GiveEquipAvailable`: server-authoritative only (dedicated server or the hosting
  player's server world). Unavailable on a joined remote client, without a live server session, on an older runtime,
  or without the resolved selection helper, transaction guards, move/equip functions and scoped creation hook.
  `Capabilities.Why("items.give_equip")` explains the refusal. Player-session, death, FIFO, timeout and cancellation
  checks are shared with `Equip`; the shared queue holds at most 1024 requests.
- TestKit models this operation's logical equipment, replacement, dry-run and event behaviour. Set an item's type
  with `kiln.Items.SetEquipmentSlotType(item, EquipmentSlot.Ring0)` (both ring slots fit the same type). It does not
  reproduce the game's physical equipment-page layout, derived stats, visual updates or persistence. Arrows, spells and
  bait (eternal ones included) follow the backpack-cell rules above in the kit's fake backpack.

Changelog: added atomic give-and-equip into empty or occupied equipment slots, with replacement policies and dry-run;
thanks to **Fuzen777** for [#126](https://github.com/frostdev-ops/kiln/issues/126).

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
if (!r.Ok) Log.Info(r.Reason);                         // Ok: all of it (Done); "Partial: ..." when only r.Done fit (r.Succeeded)

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

// ... with what each one is, as far as Kiln can prove it (since 0.2.0-dev.31)
foreach (var (entity, kind) in Items.FindInventoryOwners(InventoryKinds.Containers))
    if (kind == InventoryOwnerKind.ProductionStation) Log.Info($"station {entity}");
```

### Inventory owners and their kinds (since 0.2.0-dev.31)
`Items.FindInventoryOwners(InventoryKinds kinds = InventoryKinds.Containers)` → `IReadOnlyList<InventoryOwnerInfo>`: the
entities `FindInventories(kinds)` finds (the same flags and order; player storage holders only with
`InventoryKinds.PlayerStorage`), each as `InventoryOwnerInfo(EntityId Entity, InventoryOwnerKind Kind)`. Server world, tick
thread; capability `items.inventory_kinds` (`Items.InventoryKindsCapability`). `FindInventories` and `Bases.Storage` are
unchanged. Thanks to Fuzen777 for the request (#138).

`InventoryOwnerKind` is only what a component of the game or Kiln's own registry proves; Kiln never guesses from a
template's name. The codes are stable and only ever added to (an unknown code reads as `Unknown`):

| Code | Kind | Proven by |
|---|---|---|
| 0 | `Unknown` | nothing: ordinary storage chests are here (no component tells a chest from another plain container), and a base's storage while the game's bases cannot be read (`stations.read` off) |
| 1 | `Player` | the game's `InventorySetup.inventoryFlags` IsPlayer |
| 2 | `Npc` | `InventorySetup.inventoryFlags` IsNpc (a villager) |
| 3 | `PlayerStorage` | a holder of `Items.RegisterPlayerStorage` (kiln.items.storage's registry) |
| 4 | `ProductionStation` | the entity carries the game's `FactoryStation` (what `Stations` reads) |
| 5 | `BaseStorage` | in a base's crafting stock as the game's `PlayerBasesSystemServer` lists it this frame (`Bases.Storage`): a Magic storage chest, or a player-planted refilling fruit tree or palm (the game marks both alike) |

The first that holds, in this order. The base's chests, ordinary and Magic: `Bases.Chests(BaseId)`
([stations.md](stations.md)). Native: `kiln.items.inventory_kinds` v1 (`kiln_items_inventory_kinds.h`): `find(flags, rows,
capacity, &count)` with rows of 8 bytes `{u32 entity, u32 kind}`, `find_inventories`' flags and guards, `*count` = all that
match, `KILN_ERR_BUFFER_TOO_SMALL` past `capacity` (the first `capacity` rows written), capacity 0 asks for the count.

- `Give(target, ItemId|string, count = 1, Gear? = null, GiveOptions = None, SlotId? slot = null)` →
  `Task<ItemsResult>`. Options: `Notify` (player popup + PickupEvent), `AllOrNothing`, `NoMerge`, `DryRun` (run and
  roll back: says what would happen). `slot` = the slot to fill first.
  Unset `Gear` values are the game's defaults, the same as crafting: a level rolled in the item's range, `rarity` or, with
  `generateRarity` set (loot weapons), the lowest allowed rarity (Common), never a rolled one: docs/re/items.md section 10.
- `Gear.Unlocked` (1-4, 5 or 255 = all the item has; null or 0 = none) and `Gear.GemSlot` (weapons: an empty gem slot when the game's roll
  gave none; other gear only when a mod allowed its kind with `Gems.AllowSockets`: [gems-sockets.md](gems-sockets.md)) are applied as the item entity is created: a hook in the game's createPide changes the component list it
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
  `Message`, `GameError` (the game's code), `Steps`, `DryRun`. Game-side outcomes never throw. Since 0.2.0-dev.32 it is an
  `IKilnResult` like the others: `Ok` is `Status == Done` (all of it; there is no `ItemsStatus.Ok`), `Reason` the status and
  message when not, and `Succeeded` also counts a `Partial` give.
- `Read(entity)` → `InventoryContents` (`Inventories` with their `InventoryCategory`, `Slots` with `Item`, `Count`,
  `ItemEntity`, `Level`, `Rarity`, `Category`, `Link`, `IsGear`, `IsEquipped`, `IsBroken`), `GenericSlots`,
  `AvailableSlots`, `IsPlayer`, `Count(item)`, `FreeSlots()`.
- Catalog: `All`, `Get(id)`, `Lookup(nameOrId)`, `Search(text)`; `ItemInfo` = id, debug `Name`, `DisplayName`, `Label`,
  `MaxStack`, `Category`, `Rarity`, `IsGear`, `LevelMin/Max`, `EquipSlot`, `Flags`, `IsBlueprint`, `Guid`.
  `ItemId.FromGuidBytes(bytes)` computes an id like the game. The catalog is the server world's registry: on a client that
  joined another's world (and in the main menu) it is empty.
- `Items.DisplayName(item)` (since 0.2.0-dev.29.15, any side, any thread, never waits): the item's name for players. On a game
  client in the game's text language, read from the game's own texts (its loca table of that language), also on a joined
  client and in the main menu; a new item of kiln.content: its definition's text for that language (`Texts`, then
  `DisplayName`, else its base item's name). On a dedicated server (no texts in its data), and until the game's texts are read
  (the first call starts that in the background), the English name Kiln ships (`ItemInfo.DisplayName`). Null for an id Kiln
  knows no name of. Icons: `Ui.ItemImage` ([ui.md](../ui.md#item-icons-and-names)).
- Descriptions and names in a chosen language (since 0.2.0-dev.31.13, mod API 0.2.22; any side, any thread, never waits):
  `Items.Description(item)` / `Items.DescriptionIn(item, language)` (the description without markup),
  `Items.DisplayNameIn(item, language)` and `Items.TextTags(item)` (the loca tag ids of the item type's name, caption,
  description and lore, from its `ItemInfo`; works on a server too). `language` is a key the game ships (`"de-de"`; `"de"`,
  `"De_De"` work too; null: the player's). A language's table is read in the background at the first question: null until
  then (`await Content.LoadTextsAsync(language)` first to have it at once). Unlike `DisplayName(item)`, `DisplayNameIn`
  does not answer English for another language; English (`"en-us"`) falls back to the name Kiln ships. On a
  dedicated server descriptions are null (no texts in its data). A new item: its definition's texts. Guide:
  [content.md](content.md#game-texts-in-any-language-since-020-dev3113).
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
  rule does that: docs/kiln/services/rules.md). At most 256 at once. While a refill covers a container it wins over
  every other change of its water, a mod's included: `Fuel.Set` is undone in the same step (the program runs the
  refills right after the requests), and a tracked `World.Update<Ecs.Fuel>` lasts one step (its answer names the
  refill: `WorldWriteResult.WrittenOverBy`, docs/kiln/services/world.md). End or narrow the refill before such a
  write.
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
bool renamed = await Items.SetContainerName(chest, "Ores");     // v9 (0.2.0-dev.30.3): the game's own rename
using var lockSlot = Items.Transfers.Keep(slot);                // R leaves this stack alone
```
Where a stack move lands in a player's or an NPC's inventory (issue #109): only the Generic pages (the backpack, the worn
backpack's pages included), never equipment, currency or customization pages, nor the action bar without `ActionBar`; a
named `toSlot` must be one the item may enter (an equipment slot only to top up the worn stack of the same type, never an
empty one: `Items.Equip`), else `Refused`; a move that put
anything elsewhere, or lost items, is rolled back and `Refused`. The rules: `native/services/items/moves.h`
(`choose_destination`, `may_land`); the tests: `native/services/items/tests/move_test.cpp` (`issue109_logic`,
`issue109_engine`); the guide: [modding/inventory.md](../modding/inventory.md#moving-items).

**Moves inside one owner** (`from == to`: an equipment slot into the same player's backpack, one chest slot to another)
are refused, `Invalid` ("from and to are the same entity") before anything runs, and kiln.items refuses them too
(`KILN_ERR_INVALID_ARGUMENT`). Looked at again for GitHub issue #138 (0.2.0-dev.31) and kept, because nothing proves such a
move safe: the move's ledger and its issue #109 guard (`build_ledger`, `may_land`, "rolled back: ... left the slot without
arriving") compare the source owner's pages before and after with the target owner's, and with one owner both pairs are the
same pages, so a stack leaving one slot and arriving in another reads as both, and "lost" or "astray" cannot be told from a
good move; a worn item moved by the game's slot transfer is not the game's own unequip (`Items.Unequip`, which the game
checks and replicates as the player's own); and no test or lab covers the game's transfer within one owner. To take off what
a player wears: for example, `Items.Unequip(player, EquipmentSlot.ArmourHead)` (into a free backpack slot); to put it on:
`Items.Equip`. Moving
between two slots of one chest is not offered.

**Checked moves** (since 0.2.0-dev.32, asked for by Flamebound Ledger). `Items.Move` names its source by slot number, so a
request made on an old view (a drag and drop from a terminal's list, a request sent twice) moves whatever is in that slot
when it runs. `Items.MoveChecked(from, expected, to, count, destination)` moves `count` items of `expected` (an
`InventorySlot` from `Items.Read`) only while that slot still holds what the read saw: the item and the count, and for gear
the same item entity with the same stamp (`expected.Ref`). With `destination` (a slot of `to` from a read), that slot must
still hold what it held (empty: still empty), and the whole count lands there or nothing moves; without one, Kiln chooses
as for `Items.Move` and a part may move (`Partial`). kiln.items checks both slots inside the move's own inventory
transaction, in the step it runs, so nothing changes between the check and the move.
- `SlotChanged`: a slot no longer holds what was read. `NoRoom`: the destination cannot take the whole count (full,
  another item, or room for part). `Refused`: as for `Items.Move`, and anything that would land elsewhere. In each case the
  move is rolled back and both inventories are as before.
- A request that already ran finds its source changed and is `SlotChanged`: it commits once. A plain stack has no
  identity: a stack of the same item and count put in its place passes.
- No `MoveOptions`: a player's equipped or held items and the action bar are refused, as by `Items.Move` without options.
- `Items.CheckedMovesAvailable`: capability `items.move`, the kiln_items program running, and a kiln.items that has checked
  moves (an older runtime: false, and `MoveChecked` is `Unavailable` saying to update Kiln). Gear also needs
  `items.instances` (without it no read carries a stamp: `Unavailable`).
- C: set `KILN_ITEMS_MOVE_CHECKED` on a STACK move and pass a `KilnItemsCheckedMoveRequest` (`size` at least its sizeof)
  with the two `KilnItemsSlotWitness`; an older kiln.items refuses the flag. `status2` reports `KILN_ITEMS_HAS_CHECKED_MOVE`
  in `reserved`.
- Tests: `native/services/items/tests/move_test.cpp` (`checked_move_test`), `managed/tests/Kiln.Items.Tests/CheckedMoveTests.cs`;
  the game's side: Flamebound Ledger's lab fixture (a deposit into a magic chest, a stale drag refused, a withdraw into an
  exact bag slot, a request sent twice moving once). The guide: [modding/inventory.md](../modding/inventory.md#checked-moves).

C (header size >= `sizeof(KilnItemsApi)` of v3): `move` (a `KilnItemsMoveRequest`: STACK, QUICK_STACK or MOVE_ALL; callback with a
`KilnItemsMoveResult` and its ledger, tick thread Pre), `container_name` (tick thread), `transfer_rule` / `transfer_listen` /
`transfer_report` / `transfer_end` (the first registration installs the hooks), `status2`. A transfer handler runs inside the game's
call on its worker thread with keep, cancel, move, read and name (`KilnItemsTransferHost`); `cancel_owner` ends an owner's rules,
handlers and reports too.

Lab switches: `kiln/data/kiln.items/selftest` (a marker file) makes the service name a container through the game's own rename path and
run a native handler scenario when the test mod asks; `KILN_ITEMS_HOOKS=early` installs the transfer hooks at load.

## Renaming a container (v9, since 0.2.0-dev.30.3)
`Items.SetContainerName(entity, name)` renames a chest or other container the way a player's Rename button does: on the
server, through the game's own rename, so the name replicates to every player, is in the next save, and shows in the
game's Rename screen and in `Items.ContainerName` like one a player typed (GitHub issue #112, thanks Fuzen777). Capability
`items.rename` (sigdb feature of the same name); `Items.ContainerRenameAvailable` adds the session: true only where the
server world runs. Since mod API 0.2.13. The guide: [modding/inventory.md](../modding/inventory.md#container-names).

```csharp
if (Items.ContainerRenameAvailable && await Items.SetContainerName(chest, "Ores")) Log.Info("renamed");
await Items.SetContainerName(chest, "");                        // clears the name
```

**Name rules**: the game's Rename field's (client 26430625, docs/re/items.md section 14): at most 48 characters (Unicode
code points: `Items.ContainerNameMaxChars`, the game data's `FbUiBundle.maxInputLengthForLabelledEntities`) and 64 bytes
of UTF-8 (`Items.ContainerNameMaxBytes`, the field's buffer); no control character but tab (U+0000-U+001F other than
U+0009, and U+007F: the field drops them); any script and emoji; kept as given, no trimming, whitespace-only allowed; no
profanity filter (the game has none on names; a player's client may filter what it shows). Kiln refuses where the field
would cut or drop instead of storing something a player could not have typed, and refuses text that is not well-formed
(a lone UTF-16 surrogate). "" clears the name: the server's rename does that, though the Rename field never sends an
empty name.

**What it does**, the steps of the game's rename handler (the message dispatcher's case for
`CustomPlayerStringClientMessage`), in the next Pre on the tick thread: the entity is checked again (it exists, it has an
initialized inventory that is neither a player's nor an NPC's); the world's tracked write of the object's
`CustomPlayerString` (replication, the save) marks it written and gives its old id, as the handler's first call does;
`set_string(old id, text)` stores the text in the world's string table, which releases the old name's record and queues
the new one for every client; the id (FNV-1a 64 of the text) then goes into the component or, for an object never named
before, a new component (`AddComponent`). Nothing that can fail runs between `set_string` and storing the id. A name the
object already has changes nothing. The string table holds at most 1024 different names (its record pool, and its map
when that is smaller); a rename that would need a new record past that is refused before `set_string`, which would
otherwise release the old name first.

**Outcomes**: refusals at the call throw: `ArgumentNullException` (null), `ArgumentException` (no entity, an entity that
does not exist or is not a container, a name too long, not valid text or with a control character; the message names the
rule), `InvalidOperationException` (a game client joined to another machine's world, or in its menu: no server world in
this process), `KilnUnavailableException("items.rename")` (an older Kiln, a game build whose rename is not known). The task
completes on the tick thread in the next step: `true` when the game took the name (also when the container already had
it), `false` when it could not run there (the entity went away or stopped being a container before the step, the string
table was full, the server world closed or did not step for 10 s); the reason is in kiln.log (`rename ticket ...`).

C (header size >= `sizeof(KilnItemsApi)` of v9, 384; feature `items.rename`): `container_rename(owner,
KilnItemsRenameRequest*, done, user, &ticket, &refusal)` checks the request (and on the tick thread the entity), fills
`refusal` (`KILN_ITEMS_RENAME_*` and a message) when it refuses, and queues the rename; `done` gets a
`KilnItemsRenameResult` in the next Pre (status, `changed`, `cleared`, `game_error`, steps, message). `rename_status`
(any thread): available, the feature, a server world here, counters. Renames run up to 64 per step, in order;
`cancel` / `cancel_owner` drop the callback, not the rename. The rules without game memory:
`native/services/items/rename_logic.h` (host test `tests/rename_test.cpp`: the text check and the steps against a fake
string table); the queue: `rename.cpp`; the game calls: `names.cpp`.

Symbols: `items/WriteComponentTick` (captured at the rename handler's own call, `@items/RenameHandler.site`),
`items/StringsInsert` and its two limits (`@items/StringsInsert.site`, `.fields`), with items.names' `items/SetString`
and `items/AddComponent` (`sigdb/symbols/server.d/items.json`; both exact builds, client and server).

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
| `EquipChangedEvent` (`Result`, `Player`, `Slot`, `Before`, `After`, `BeforeItem`, `AfterItem`) | `[ServerOnly]`, tick thread | once per committed change of `Equip` / `Unequip`, when its result arrives; also for two instances of one item type, and when the mod that asked unloaded before the change completed; none for a refusal or a dry run |
| `EquipResult` (`IKilnResult`) | value | `Ok`, `Reason`, `Status`, `Player`, `Slot` (for the item's own slot: where it went), `Unequipped`, `Item`, `ItemEntity`, `Ref`, `From`, `To`, `Replaced`, `ReplacedEntity`, `ReplacedRef`, `ReplacedTo`, `Message`, `GameError`, `Steps`, `DryRun` |

The one error rule:
- **refused** (a result, `Ok == false`, nothing changed): `SlotChanged` (the slot is not one of the player's, is empty, or
  holds another item entity; an unequip of an empty slot), `NoInventory` (no player character with that id in the
  world), `NoRoom` (an unequip with no free backpack slot: the game also shows the player its "inventory full" notice, as
  for their own unequip), `Refused` (the game refused: the item's EquipmentSlotType is not the slot's, `GameError` 0x11;
  or Kiln refused: the player is dead right before the transaction, or 1024 requests are waiting), `Unavailable` /
  `Dropped` (equip went off, or the server world closed, after the call); a request whose player left before it ran comes
  back `NoInventory` (the next player in the slot gets the same entity id: a request runs only for the session it was
  made for);
- **bad argument** (`ArgumentException`): player or slot none, a value outside `EquipmentSlot`;
- **bad argument** also: option bits other than `DryRun`;
- **capability off** (`KilnUnavailableException("items.equip")`): an older Kiln, a game build without the two functions,
  no kiln.world or kiln.players, no death check on this build (`Actor.currentState` and `StateFlag.Dead` by the game's
  reflection), equip switched off after a fault, or a game client with no server world (its menu, or joined to another
  machine's world, or a world that stopped: the row is then unavailable for the session, reason `session`, and comes back
  when a server world runs here again), or the core's session query that does not answer (its tick delivery faulted:
  nothing would complete a request, so the row goes off).

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
  (`equipment_actions` is host only), so a player's game shows the server's equipment. `EquipChangedEvent` reports each
  committed change of Kiln's exactly; Kiln.Players' `PlayerEquipmentChangedEvent` is the poll that also sees the
  player's own changes (every four steps, by item type: it misses one ring replacing another of the same type, and an
  equip and unequip between two polls). Kiln.Saves' followed items see the move at once (cause `Transaction`).
- A dead player: refused right before the transaction, from the iterated player's `Actor` (a declared read of
  `kiln_items_equip`; the game's own equip has no such check). The C table does the same.
- The session: a request for a player entity (1..1023) captures the player's session at the call (kiln.players: the
  connection handle with the slot's generation) and is checked in the Pre of the step it runs in against the game's own
  player record of that slot, read then (kiln.players' scan of that step may come later): the same handle, in game, the
  same entity. A request runs only in a step whose Pre checked it. A build where the records cannot be read refuses
  such requests (`Refused`); a player who left comes back `NoInventory`.
- A mod that unloads while its request waits: the request still runs, and its change is reported (`EquipChangedEvent`,
  Kiln.Saves' followed items); only the mod's task is never completed. kiln.items holds the request under Kiln's own
  owner, so releasing the mod's owner does not drop the report.
- The vanity slots take armour of the same body part (the game's table); the four building-material values of the game's
  `EquipmentSlot` are not equipment slots (`SlotSelection.equipment` has 20 entries) and are not offered.

C (v5, header size >= `sizeof(KilnItemsApi)` with `equip`): `equip(owner, KilnItemsEquipRequest*, done, user, &ticket)`
(`equipment_slot` is the game's `keen::EquipmentSlot`: 0-16, 21 HookShot, 22 Glider, 23 Backpack, or
`KILN_ITEMS_EQUIP_ANY`; flags `KILN_ITEMS_EQUIP_UNEQUIP`, `KILN_ITEMS_EQUIP_DRY_RUN`), the callback gets a
`KilnItemsEquipResult` on the tick thread in Pre; `equip_status` says whether it runs and why not. `cancel(ticket)` and
`cancel_owner` drop the callback.

Lab: `mods/tests/items-test` (`EquipTest.cs`, on a spawned `1_Player` character; `kiln/data/items-test/equip-only` runs it
alone): unequip, equip into a named slot, a swap, the item's own slot, a dry run, `PlayerEquipmentChangedEvent` after each,
and every refusal above leaving the inventory as it was, counts and item entities included; `EquipChangedEvent` once per
change (two rings of one type, an equip and an unequip of one slot in one step, both run in order) and never for a
refusal or a dry run; a full queue; a player killed after the call and before the transaction, refused: 41 PASS on server
26430253 (lines in docs/re/items.md section 16). Not checked in a lab: a slot reused by a new connection (no second client
there; the host test checks the rule), a remote client, a request queued when the core's tick delivery faults.
A dry run into the item's own slot cannot name the slot (`Slot` null): the game writes the selection only at its commit.
What a player sees is the guided checks `items.equip` and `items.unequip` (mods/tests/client-pass, session 1).

## Equipment sets (v6, since 0.2.0-dev.29)
A player's 20 equipment slots changed in **one inventory transaction**: every slot a set names changes, or none does,
with one exception, `InvariantFailure`: the game's transaction left the inventory neither as asked nor as before, or the
game faulted inside it (then the inventory may be in an unknown state, and sets and single equips go off until the server,
or the hosting player's game, restarts; "The transaction" below).
`Items.CaptureSet` reads what a player wears as an `EquipmentSet`, a mod builds a subset (or keeps the capture),
`Items.PlanSet` checks it against a snapshot taken now, and `Items.EquipSet` applies it: one request, one
`PlayerEquippingEvent` for the whole set, one transaction inside `kiln_items_equip`, one result. Capability
`items.equip_sets` (sigdb feature of the same name over the game's equip functions and the commit's own rollback
guards; Kiln.Items lowers it when the table is older than v6, `items.equip` or `items.instances` is off, kiln.decisions or
kiln.window_clock is not here, this build's component layouts are not the ones the snapshot reads, or kiln.items switched
sets off after an invariant failure, until the server, or the hosting player's game, restarts). Chapter 10 of the dev.28 dossier is the contract.

```csharp
EquipmentSetCaptureResult day = Items.CaptureSet(player);         // all 20 slots, empty ones as EmptyEquipment
var rings = day.Set! with { Entries = day.Set!.Entries.Where(e => e.Slot is EquipmentSlot.Ring0 or EquipmentSlot.Ring1).ToArray() };
EquipmentSetResult r = await Items.EquipSet(player, rings);       // only the two ring slots; the rest stays as it is
if (!r.Ok) Log.Info($"{r.Status}: {r.Reason}");                   // a refusal is a result: nothing changed
using var plan = Items.PlanSet(player, rings).Plan;              // the steps, without changing anything
```

| Member | Side, thread | What |
|---|---|---|
| `Items.CaptureSet(player)` → `EquipmentSetCaptureResult` | `[ServerOnly][TickThreadOnly]` | the 20 slots as a set: `InstanceEquipment` (an item with its own entity), `StackEquipment` (a plain stack: arrows, bait), `EmptyEquipment`; scoped to this process, server world and session of the player. Reads only, holds no snapshot |
| `Items.PlanSet(player, set)` → `EquipmentSetPlanResult` | `[ServerOnly][TickThreadOnly]` | a snapshot taken now and the plan: the game's equip and unequip calls in order (`Steps`), the free backpack slots they need (`RequiredFreeSlots`), `ValidThroughStep`. The plan holds the snapshot until it is applied, disposed, 250 steps pass, the mod unloads or the world stops |
| `Items.EquipSet(player, set, options)` → `Task<EquipmentSetResult>` | `[ServerOnly][TickThreadOnly]` | a fresh snapshot and plan, then the set in one transaction |
| `Items.EquipSet(plan, options)` → `Task<EquipmentSetResult>` | `[ServerOnly][TickThreadOnly]` | the plan's set against the plan's snapshot (used up: a plan applies once) |
| `EquipmentSetResult` (`IKilnResult`) | value | `Status`, `Reason` (it starts with its code: `NoRoom: ...`), `Player`, `Ticket`, `Decision`, `DryRun`, `Committed`, `MayHaveMutated`, `FailedOperation`, `GameError`, `Changes` (one `EquipmentSlotChange` per slot a commit changed) |
| `EquipmentSetChangedEvent` (`Player`, `Ticket`, `Outcome`, `Changes`) | `[ServerOnly][TickThreadOnly]` | once per committed set, when its result arrives, also when the asking mod unloaded first; not for a dry run, a refusal or a cancel |
| `EquipmentSetFaultedEvent` (`Result`) | `[ServerOnly][TickThreadOnly]` | an invariant failure: the inventory is neither as asked nor as before, or the game faulted (`MayHaveMutated`: it may be in an unknown state; without it, a fault in a read before the transaction, nothing was changed); sets and single equips are off until the server, or the hosting player's game, restarts, other item calls still run. Posted after an `EquipmentSetChangedEvent` of the same ticket when the changes could still be read: the two are not exclusive (chapter 10 §4.4 asks for both) |
| `EquipmentSetJson.Serialize(set)` / `Deserialize(json)` | any | the session JSON (schema 1): a round trip for this session and for logs, never a saved loadout |

| `EquipmentSetStatus` | `Ok` | Means |
|---|---|---|
| `Done` | yes | every named slot changed, committed in one transaction |
| `NoChange` | yes | every named slot held what the set asks already (or no entries): no decision, no transaction, no event |
| `Simulated` | yes | a dry run: the transaction ran, was rolled back and the snapshot compared equal; `Changes` is empty (the plan's steps say what it would do) |
| `Canceled` | no | a `PlayerEquippingEvent` handler canceled the set: nothing ran |
| `StaleSnapshot` | no | another process, server world or session of the player, or the inventory changed since the snapshot (also by a decision's handler): nothing ran |
| `Refused` | no | refused before or during the transaction (`NoRoom`, `UnsupportedCycle`, `UnsupportedBackpack`, `OmittedSlotWouldChange`, `StackMerge`, `SlotType`, `NotHere`, `SnapshotBudgetExceeded`, the game refused a step: `FailedOperation`, `GameError`); after a transaction it was rolled back and compared equal |
| `CommitRolledBack` | no | the game's commit rolled the transaction back by itself; compared equal to the snapshot |
| `Dropped` | no | never ran: the world stopped, the asking mod unloaded before it ran, it waited 250 steps |
| `InvariantFailure` | no | neither as asked nor as before, or the game faulted inside it (`MayHaveMutated`) or in a read before it (nothing changed); sets and equips go off until the server, or the hosting player's game, restarts |
| `Unavailable` | no | sets went off after the call was accepted |

The one error rule: a bad argument (`ArgumentException`): a null or zero scope, a null entry or selection, a slot named
twice, a value outside `EquipmentSlot` (the building materials and the main hand are not equipment slots), one item or
one stack selected twice, an item ref without a kiln.items stamp, a stack without its item, slot or a positive count, a
player that is not the set's, unknown options. Capability off: `KilnUnavailableException("items.equip_sets")`. Everything
else is a result.

The rules (chapter 10 §4.2-4.5):
- **Omitted means unchanged.** A set names the slots it changes; `EmptyEquipment` takes off what a slot holds (into the
  first free Generic backpack slot); an empty slot asked empty is no change. `StackEquipment.ExpectedCount` is a guard
  (the stack must still hold that many), not a split: the whole stack moves.
- **Session only.** A set's scope is the process nonce (kiln.window_clock), the server world's epoch and the player's
  identity (kiln.decisions' `EventEntityKey`: the connection session of a player slot, the spawn time of anything else).
  A set from another process, world or session comes back `StaleSnapshot`; nothing in it is re-resolved. Keep sets in
  memory for the current session and forget a player's when they leave; nothing is saved (the session JSON is for logs and
  this session).
- **The snapshot.** Canonical copied bytes, compared byte for byte, never a revision counter or a hash: the player's
  InventorySetup tree (the root and every Virtual node it reaches: their ordered links and counts), every leaf inventory
  (eight `{item, count, item entity}`, empty stacks all zero), `SlotSelection`'s 20 equipment slots and two action bars,
  every item entity's kiln.items stamp, `ItemState` slot and item and `OwnerRelationship` owner. Left out: the selected
  hotbar slot, the item in hand, the materials, durability, fuel, perks and level. A tree beyond 64 setups, Virtual links
  below depth 3 or 32 inventories, a cycle or a link that cannot be read is a refusal (the inventory reads elsewhere stop
  there silently; a set never runs on a cut tree). 256 snapshots live at most, 16 per mod; a full budget refuses
  (`SnapshotBudgetExceeded`), it never evicts another mod's plan.
- **The plan.** Items into equipment slots that are empty first, in numeric slot order; then the slots to be emptied;
  then items into slots that hold another item (the game's equip swaps: what the slot held goes to the source slot);
  plain stacks (arrows, spells, bait, which the game equips where they lie) last. Each source is found again by its item
  entity after the steps before it. At most 128 steps. Refused before anything is touched: no free backpack slot for an
  unequip (`NoRoom`), **more than one slot that holds an item getting another one, or a slot emptied after such a swap**
  (`UnsupportedSwaps`: on this game build the commit then points the swapped slot at the item that came off; the
  rollback matrix below; apply it as two sets), a backpack change (`UnsupportedBackpack`: its contents move with it), an
  item worn in a slot the set leaves out, a stack of the item type its slot already holds (the game could merge them
  instead of swapping), an item that does not fit its slot. Two worn items exchanged between their slots (a cycle) go
  through a free backpack slot (`UnsupportedCycle` without one). A backpack change stays refused until its rollback matrix
  has run.
- **The decision.** In the Pre after the call kiln.items files one kiln.decisions request of kind 18 for the set; Kiln's
  dispatcher posts one `PlayerEquippingEvent` with a `SetEquipmentChange` (the ticket, the snapshot, the set, `DryRun`).
  Cancel: `Canceled`, nothing runs. A handler that changes the inventory makes the set `StaleSnapshot` at its check.
  Without a decision consumer, with a full queue, a timeout or a released capability the set goes on (kiln.decisions
  fails open). Subscribing to `PlayerEquippingEvent` needs `events.equip`.
- **The transaction.** In the Pre of the step it runs in the player's session and identity are checked again; inside
  `kiln_items_equip` the snapshot is read again through the program's own accessors and compared before the
  transaction, the plan is made again from it (a caller's steps are never replayed), every step is staged on one
  transaction set up as the game's `equipment_actions` sets up its own, and the terminal state is checked against the
  request and the snapshot: a refusal rolls back once and compares, a dry run rolls back once and compares (never
  commits), the commit's own rollback conditions (flags bit 0x40, no InventoryEmptyState accessor with touched
  inventories) refuse before the commit, the commit is called once and never followed by a rollback, the arrays are freed
  once. "As it was" after a refusal, a dry run or a commit that rolled back by itself is two comparisons in the same
  program update: the canonical snapshot, and every byte of the seven components the program declares (Fuel,
  Inventory, InventoryEmptyState, InventorySetup, ItemState, OwnerRelationship, SlotSelection) of the player, every
  setup node, every inventory and every item entity, read before the transaction opened; a difference in either (an
  item's fuel, its level or rarity byte, an empty-state flag) is `InvariantFailure`. A part this build's program does not
  declare is not compared (it cannot write it either): kiln.log says which once, at the first set, and the capability
  row's text carries it; a component that cannot be read whole before the transaction refuses the set before anything
  changes, and one readable before and not after the rollback is `InvariantFailure`. Durability, level and perks are
  other components (Durability, Level, PerkContainer), which the program cannot write; Kiln does not compare them (the
  lab's rollback series did). After a set that succeeded: every named slot as asked, every slot left out as it was, and
  every stack, item entity, stamp and owner, the tree and the action bars conserved (an item may move); nothing else is
  compared after a commit. A mismatch or a fault: `InvariantFailure`, sets (and equip) go off; nothing is repaired.
- **A fault inside the transaction is not rolled back.** The game's rollback replays the operations its transaction
  recorded; a structured exception inside its equip, unequip or commit may have left that record half written, so asking
  for a rollback could undo a write that never happened or miss one that did. No lab probe has shown the rollback safe
  after a fault (the dev.28 dossier proved commit and rollback for staged equips only), so kiln.items stops: no further
  engine call, no read, no free. The result is `InvariantFailure` with `MayHaveMutated` and a reason that says the
  inventory may be in an unknown state; `EquipmentSetFaultedEvent` carries it; kiln.log has an error line with the
  player, the server step and the set's step; the capability row `items.equip_sets` keeps that reason until the server,
  or the hosting player's game, restarts (on a hosted world the process is the game), and single equips are off too. A
  fault in one of the set's reads before the transaction opened is reported the same way (the event, the log line, sets
  and equips off), without `MayHaveMutated`: nothing was changed.
- **What stays on after a fault.** Moves, gives, takes and fuel writes (`Items.Move`, `Give`, `Take`, `Items.Fuel`) keep
  running after an "unknown state" fault: the player can still move their own items by hand, and Kiln's own jobs are no
  riskier than that (owner's decision, 2026-11-15). The reason text says so ("sets and equips are off ...; other item
  calls still run").
- **Order.** FIFO by request with the player's single `Equip`/`Unequip` requests (setorder.h, host test
  `setorder_test.cpp`): at most one set per player and step, and only the player's oldest set (a later set that is ready
  waits for an earlier one still being decided); a single request waits for every set of the player asked before it; a
  set waits for every single request asked before it. 256 sets queued at most (`Refused`, try again).
- **Ownership.** Kiln.Items submits under Kiln's own owner and names the asking mod as the snapshot's owner, so a
  committed set is reported (`EquipmentSetChangedEvent`) even when the mod unloads first; a mod's sets that did not start
  end `Dropped` at its unload; its tasks are left alone. A result kiln.items never sends within 400 steps ends the task
  `Dropped` and switches sets off (a lost result must not hang a mod).
- **What runs for a server whose mods use no set:** `kiln_items_equip` is equip's program (registered when equip is on);
  sets add an atomic check to it and to equip's Pre, and nothing else: no hook, no snapshot, no decision.
  kiln.window_clock, kiln.decisions and the component layouts (kiln.types) are looked up once, at the first status
  query of `items.equip_sets` (Kiln.Items' capability refresh asks at boot and on its heartbeat) or the first set call,
  whichever comes first; this happens on every server, also when no mod uses a set.

C (v6, header size >= the member's end): `set_capture(owner, player, KilnSetSnapshotV1*)` (world thread, Pre or Post),
`set_release_snapshot(owner, token)`, `set_status(KilnSetStatusV1*)`, `set_plan(owner, token, entries, count,
KilnSetPlanV1*)`, `equip_set(owner, KilnSetRequestV1*, KilnSetDoneV1, user, &ticket)` and `set_cancel(ticket)`; the
entries are kiln.decisions' `KilnSetOpV1`. `done` runs once, on the tick thread in Pre (or on the thread that stops the
world), with a `KilnSetResultV1` valid during the call. A native caller other than Kiln names itself (or 0) as the
snapshot's owner; `cancel_owner` drops its queued sets.

Lab: see "Lab results: equipment sets" below. What a player sees (one decision, both rings and the helmet at once, no
in-between state on the player's own screen) is the guided check `10-items.equip-set`, and what a second player sees
`10-items.equip-set.observer` (mods/tests/client-pass; it needs a second player in a hosted world). Both prepare a
supported set (the three target slots emptied first) and put the player's own gear back with fresh plans, asserted.

### Lab results: equipment sets (lab `dev29-10`, server 26430253 `f0d824be…`, the standard lab save, no player)
`mods/tests/equipment-sets-test` on a spawned `1_Player` character: a stand-in for a player, so these rows show Kiln's
machinery on the game's own transaction, not what a player's client shows. With `kiln/control/systems.parallel` (the
game's programs forced onto its workers, players mask 0x8000) 12 of the 16 sets of the full run ran on a worker thread
and 4 on the world thread (each result's log line says which). `kiln/data/kiln.items/sets-lab` injects faults into
real runs.

- Full run: 28 PASS, 0 FAIL: SET-CAPTURE (20 unique slots, the scope), SET-EMPTY / NOCHANGE (no decision, no
  transaction), SET-OMITTED (one Ring0 entry, the other 19 slots and the hand unchanged), SET-EMPTY (an explicit empty),
  SET-SINGLE-DECISION (one `SetEquipmentChange` for three slots; a cancel leaves everything), SET-DRY, SET-ROLLBACK (the
  game's equip refused at the first, a middle and the last step: Refused, kiln.items' snapshot compared equal after the
  rollback), SET-COMMIT-ROLLBACK (the commit's rollback bit: refused before the commit; committed anyway: the game's
  commit rolled back by itself, `CommitRolledBack`, no second rollback), three slots in one transaction, a single swap,
  SET-SWAPS (three occupied slots refused before mutation), SET-SNAPSHOT (a give after the plan: StaleSnapshot before the
  transaction; a plan applies once; a disposed plan is stale), SET-CALLBACK (a cancel before it ran: Dropped once; the
  copied changes still right three steps later), SET-SESSION-ONLY (a JSON round trip applies; another process, world or
  life: StaleSnapshot), SET-CYCLE (two worn rings exchanged through a free slot, and the same refused at its last step),
  SET-BACKPACK (refused), SET-INVARIANT last (a faked mismatch after a real commit: `InvariantFailure`, the real change
  reported, one `EquipmentSetFaultedEvent`, sets off and `CaptureSet` throwing afterwards).
- The rollback matrix (`kiln/data/equipment-sets-test/matrix` with `kiln/data/kiln.items/sets-matrix`, one fresh character
  per form). First run, the order of 0.2.0-dev.29's first build (swaps in numeric order): 11 of 14 forms committed exactly;
  **a swap followed by another equip, by a second swap or by a removal left the swapped slot's selection pointing at the
  backpack slot the old item went to** (`InvariantFailure`; a swap alone, an equip into an empty slot followed by a swap,
  arrows after a swap and the cycle were exact; arrows are equipped where they lie). Second run, with the order above
  and `UnsupportedSwaps`: 15 of 15 forms as expected (the three forms that failed are reordered and exact, two swaps are
  refused before mutation; a removal then a swap, two equips into empty slots then a swap then arrows, the cycle: exact).
- After the review (merged tree, 2026-11-15; logs `out/10/lab-r3-series-kiln.log` .. `lab-r6-matrix-kiln.log` of the dev.29
  wave): full run 29 PASS, 0 FAIL (the rows above and SET-FIFO: a set's dry run, the same set and a single equip asked in
  one step: Simulated, Done, Done); the matrix again 15 of 15 forms as expected, now with the commit's conservation check.
- **The rollback series** (`kiln/data/equipment-sets-test/series`, `mods/tests/equipment-sets-test/Series.cs`): one
  stand-in with items that carry their own data (two shields with a level, a rarity and unlocked perks, a sword with a
  level and a perk, a grappling hook and a pickaxe with lowered durability, a watering can with its fuel set). For each
  form a plan can take on this build (empty targets: a shield, a hook, a ring and a helmet; removals; the one swap; plain
  stacks; arrows in place; the cycle through a free slot; a form mixing a removal, an empty target, the swap and arrows) a
  failure at every operation (the game's equip refused at step k), after the last one (the commit's rollback bit), at the
  commit (the game's commit rolling back by itself) and a dry run: 38 rollbacks, each Refused, CommitRolledBack or
  Simulated with kiln.items' full comparison equal (every byte of the program's seven components of every entity of the
  tree, in the same program update) and the test's own read of every byte of those components and of Durability, Level,
  PerkContainer, PerkContainerNew, NetworkDurability, NetworkFuel, NetworkLevel and ItemEquippedLifetime equal. Each form
  then committed: Done, as asked, and every item's own bytes as before. Equipping changes two fields of an item on
  purpose: ItemState's equipped bit (0x08 of `itemState`) and OwnerRelationship, which names **the inventory that holds
  the item** (the backpack's or an equipment page's), not the player. The first version of the commit's conservation check
  (the review's) read the owner as identity and failed every set that moved an item between the backpack and an
  equipment page; it now requires the owner to follow the item's inventory (or stay what it was when it named anything
  else). 48 PASS, 0 FAIL. Every form a plan can take stays on.
- **A save and a restart cannot be shown in a lab:** the game does not load a spawned `1_Player` from a dedicated server's
  world save (a character is its client's own), so SERIES-RESTART found no character. After a rollback the program's
  components are byte for byte what they were, so a later save writes what it would have written without the set; what a
  committed set leaves after a save and a reload of a real character is the guided checks `10-items.equip-set.persist` and
  `10-items.equip-set.persist.after`.
- Not run: a backpack change (refused), a set while a player is in the world (the lab has no client), a set whose server
  world stops while it waits, the 250-step timeout, a set from a native caller, derived stats and set bonuses read per
  program boundary (chapter 10 §8: no attribute snapshot was taken), a fault of the game inside the transaction (only the
  host test injects one).

## Gear: rarity, enhancements, level (v8, since 0.2.0-dev.30)

The game keeps an item's rarity as its number of **enhancement slots** (its `PerkContainerNew` component: Common one ..
Legendary five), and every unlocked slot is one level (docs/re/items.md section 19). Kiln.Items reads and writes these in
place: the same item entity, so its item data (`InstanceData`), affixes and gem stay on it. The writes run in the next
sim step inside kiln.items' serial program `kiln_items_gear` through the game's tracked writes, so players and the save
get them like the game's own; the game derives the shown rarity (colour), the "LEVEL x/y" line and the damage itself in
its next step.

```csharp
var item = Items.Held(player);                         // or a slot's Ref, a give result's Changes[i].Ref
GearState? g = Items.GearOf(item);                     // null: gone, or the id names another item now
// g.Rarity, g.Enhancements (slots), g.Unlocked, g.Level, g.BaseLevel, g.MaxLevel ("LEVEL x/max"), g.MaxRarity (the
// type's last perk slot), g.Durability / DurabilityMax, g.HasSocket, g.Perks[0..4] (Available, Unlocked, TypeHasSlot),
// g.ShownRarity / ShownLevel (what the game derived and sends: a step behind a change)
ItemRarity? r = Items.RarityOf(item);

GearResult up = await Items.SetRarity(item, ItemRarity.Epic);          // one more slot, locked: unlocked at the workbench
if (!up.Ok) Log.Info($"not changed: {up.Reason}");                     // NoSlot, Stale, OutOfRange, ...
await Items.SetEnhancements(item, 2);                                   // unlock two; the level moves with them
await Items.SetLevel(item, 12, new GearWriteOptions { Durability = DurabilityRule.Repair });

Events.On<GearRarityChangedEvent>(e => Log.Info($"{e.Item}: {e.From} -> {e.To} ({e.Source} by {e.Origin})"));
Events.On<GearEnhancedEvent>(e => Log.Info($"{e.Item}: unlocked {e.From} -> {e.To}"));   // also a player's workbench unlock
```

- `GearOf(item)` / `RarityOf(item)` (server, tick thread; capability `items.gear.read`). The ref must carry kiln.items'
  stamp (`Items.Held`, `Items.Equipped`, `InventorySlot.Ref`, `ItemChange.Ref`): another ref is an `ArgumentException`.
  `MaxRarity` is the highest rarity the type has a perk slot for: 487 of 497 weapons and 653 of 863 equipment items with an
  item entity fill all five (the equipment dossier); tools stop at Rare or Epic.
- `SetRarity(item, rarity, options)`, `SetEnhancements(item, unlocked 0..5, options)`, `SetLevel(item, level 1..255,
  options)` → `Task<GearResult>` (server, tick thread; capability `items.gear.write`). A higher rarity adds slots,
  locked; a lower one takes slots away, unlocked ones among them, and the level comes down by as many (as the game's own
  -1 does). `SetLevel` sets the level the game shows, unlocked slots included; the base (level - unlocked) must be in the
  type's level range, since the game sets anything else back when the world loads (`OutOfRange`), unless
  `GearWriteOptions.AnyLevel`: Kiln then keeps the level and writes it again after each load (see "Per-instance values"). The task completes on the tick thread one step after the write.
- `GearWriteOptions`: `Durability` (`KeepRatio`, the default: the game computes max durability again at the new rarity
  and level in its next step and the item keeps its share; `Keep`: max stays; `Repair`: as `KeepRatio`, and full; both
  need `items.gear.durability`), `AllowEmptySlot` (default false: a rarity whose slot the type has no perk for is refused
  `NoSlot`; with it the slot exists and unlocks a level, but no perk acts), `RefuseEquipped` (default false: an equipped
  item changes in place too), `DryRun`.
- `GearResult` (`IKilnResult`): `Status` (`Done`, `Stale`, `NotGear`, `NoSlot`, `OutOfRange`, `Equipped`, `Unavailable`,
  `Dropped`, `Refused`: kiln.items' own values), `Before` / `After` (`GearState`; null when it never ran), `Changed`
  (`Enhancements`, `Level`, `Durability`, `DurabilityRefresh`), `Message`, `Steps`, `DryRun`. `Done` also when nothing
  needed a change. A refusal changes nothing; bad arguments throw; an off capability throws `KilnUnavailableException`.
  1024 writes waiting is a `Refused` result.
- Events (server, tick thread; through the change-notice queue of docs/kiln/modding/contract.md "Change events", so a
  handler that writes again gets its event in a later step, never inside the dispatch): `GearChangedEvent` (every change of
  slots, unlocked, level or, a repair, durability; `Changed`), then `GearRarityChangedEvent` (`From`, `To`) and
  `GearEnhancedEvent` (`From`, `To` unlocked) where they apply. All carry `Before`, `After`, `Source` (`Code`,
  `Upgrade`, `Game`) and `Origin` (the mod's id, "kiln", or "game"). Kiln's own writes post when their result arrives,
  only when something changed (not a dry run, not a refusal). **The game's own changes** (a player's unlock at the workbench,
  the game's free +1 / -1) are found by Kiln.Items' gear watch: while one of the three events has subscribers, it reads one
  player's items per step (kiln.items' inventory read, then its gear read of each item entity) and compares them with the
  last read, so a change shows within as many steps as players are in game. The first read of an item is its baseline (no
  event); items outside players' inventories (chests) are not read; wear is not a gear change; Kiln's own writes are never
  reported again as the game's. Gear writes are not reported to kiln.component_changes, which is why this is a poll.
- Capabilities: `items.gear` (sigdb, the core; kiln-items.dll lowers it, see [caps.md](caps.md)), and Kiln.Items' own rows
  `items.gear.read` (kiln.items v8 and `items.gear`), `items.gear.write` (the read row and kiln.items says
  `kiln_items_gear` can run: `faulted` with its detail when it was refused or switched off), `items.gear.durability` (with
  the write row). Followed every 25 steps; a call checks `items.gear` live. `Items.GearReadAvailable`,
  `Items.GearWriteAvailable`.
- Not confirmed in a real game yet: that a client shows the new rarity, enhancement list and level without re-joining (vanilla
  and Kiln clients), that the workbench then offers the new unlock at its cost, an equipped item's perks after a change, and
  the durability bar after a refresh. Lab (package P1's self-test, server 26430253, no player): the writes, the derived
  rarity within 10 steps, the refreshed max durability with its ratio, and a save and restart keeping them.
- Test kit: every piece of gear the fake kiln.items makes has enhancement slots, a level, a durability of 100 and a stamp;
  `HarnessItems.SetPerkSlots(item, n)` sets a type's filled perk slots, `HarnessItems.SetGear(ref, ...)` plays the game's
  own change ([unit-tests.md](../modding/unit-tests.md)).

## Upgrading rarity (v8, since 0.2.0-dev.30)

`Items.UpgradeRarity` raises an item's rarity by one step of a rule, paid for: the item gets its next enhancement slot in
place (locked: the player unlocks it at the workbench as usual, for its cost, and gets a level and the perk), and the
step's cost comes out of the player's inventories (or a chest) in the same server step as the change, all or nothing. The
rule is a C# record or a block (`RarityUpgradeBlock`, `rarityUpgrades` in `blocks/*.json`:
[item-blocks.md](../modding/item-blocks.md#rarity-upgrades-rarityupgradeblock)). A station that runs it when a player uses it is
`InteractBlock.Upgrade`, and from code `UpgradeStation.Use` / `UpgradeStation.Offer`
([../modding/world-blocks.md](../modding/world-blocks.md#upgrade-stations)).

```csharp
var rule = new RarityUpgradeRule
{
    Items = ["Weapon_*", "Armor_*"],                    // none: every item with enhancement slots
    Steps =                                             // by the rarity each step reaches
    {
        [ItemRarity.Uncommon]  = new UpgradeStep { Cost = { ["Material_T1_Metal_Scraps"] = 10 } },
        [ItemRarity.Rare]      = new UpgradeStep { Cost = { ["Material_T1_Metal_Sheets"] = 5 }, Chance = 0.9 },
        [ItemRarity.Epic]      = new UpgradeStep { Cost = { ["my-mod:ember-shard"] = 3 }, Chance = 0.75, OnFail = UpgradeFail.KeepItemLoseCost },
        [ItemRarity.Legendary] = new UpgradeStep { Cost = { ["my-mod:ember-shard"] = 8 }, Chance = 0.5, LevelScale = 0.1 },
    },
};
UpgradeResult r = await Items.UpgradeRarity(player, Items.Held(player), rule);
if (!r.Ok) Chat.Tell(player, $"Not upgraded: {r.Reason}");               // Failed, CantPay, NoSlot, AtMax, ...
await Items.UpgradeRarity(player, item, "standard-upgrades");            // a block of this mod, or "other-mod:name"
await Items.UpgradeRarity(player, item, rule, new UpgradeOptions { PayFrom = chest });  // a chest pays
```

- **What it checks, in order** (each a result, nothing paid): the item is gone (`Stale`), has no enhancement slots
  (`NotGear`), the rule does not take it or has no step to its next rarity (`NotAllowed`), it is at the rule's `Max` or
  Legendary (`AtMax`), its type has no enhancement slot for the next rarity (`NoSlot`, unless `AllowEmptySlot`), a cost
  item the game does not have or that is no plain item (`Refused`), a `GearUpgradingEvent` handler canceled it
  (`Canceled`), the payers hold too little (`CantPay`), a payer that is another player or an NPC (`Refused`). A rule that
  is malformed (a chance outside 0..1, a count outside 1..100000, more than 8 cost items, a step below Uncommon) throws.
- **The cost.** Item names as everywhere in blocks: game names, `"other-mod:name"`, or the rule's own mod's item by block
  name. Plain items only (materials, runes): an item with an entity of its own (gear, a gem) cannot be a cost, so the item
  being upgraded is never spent. `LevelScale` grows the cost with the item's level: `count × (1 + LevelScale × (level - 1))`,
  rounded up. `PayFrom`: a container your mod vouches for pays instead of the player (each payer pays what it has, in
  order; kiln.items takes up to 8). `Free`: nothing is paid.
- **The roll** is the server's (a cryptographic random number; a player's game has no part in it). `Chance` 1 (the
  default) rolls nothing; `UpgradeOptions.Guaranteed` always succeeds. A failed roll does what the step's `OnFail` says:
  `KeepItemLoseCost` (the default: the item stays, the cost is spent), `KeepItemKeepCost` (nothing paid), `Downgrade` (the
  cost is spent and the item drops a rarity; a Common item stays). An upgrade never destroys an item. The result says
  `Failed`, with `Rolled` and `Chance`.
- **One step, all or nothing.** kiln.items' program `kiln_items_gear` checks the item's stamp, plans the write, counts the
  costs in the payers, takes them on one inventory transaction of its own, counts again after the commit (a commit that
  rolled back by itself ends `Refused`, nothing written), and only then writes the enhancement slots. Two upgrades of one
  item in the same step: the second finds the slots it was priced for changed and is `Refused`, paying nothing.
- **Options of the rule:** `Max` (default Legendary), `Durability` (default `KeepRatio`: max durability computed again at
  the new rarity, the share kept; it needs `items.gear.durability`), `Socket` (default `Keep`; `GameChance`: a weapon
  without a socket rolls one with the unchanged game's chances for new weapons, 0 / 5 / 25 / 50 / 100 % from Common to
  Legendary, and a won socket re-creates the weapon in its slot as `Gems.AddSocket` does, a new item entity that
  `UpgradeResult.After` names; not for an equipped weapon, and a pack's changed chances are not read), `AllowEmptySlot`.
  `UpgradeStep.Unlock` also unlocks one more slot at once (one level, one more perk acting) instead of waiting for the
  workbench.
- `UpgradeResult` (`IKilnResult`; `Ok` only for `Done`): `Status`, `Player`, `Item`, `From`, `To`, `Before`, `After`
  (`GearState`), `Paid` (by item), `Chance`, `Rolled`, `Message`, `Rule` (a block's id), `DryRun`, `SocketAdded`.
  `UpgradeOptions.DryRun` checks, counts and plans, pays and changes nothing, rolls nothing: `Paid` and `After` say what a
  success would cost and make.
- **Events** (server, tick thread): `GearUpgradingEvent` (cancelable) inside the call, after the checks and before
  anything is paid or rolled, for every upgrade (code or a station's interaction; not a dry run): `Player`, `Gear`, `From`,
  `To`, settable `Cost` (by item id, already level-scaled) and `Chance`, `Rule`, `Origin`. `GearUpgradedEvent` when the
  result arrives, for a success or a failed roll (`Succeeded`, `Result`). The rarity change itself is also a
  `GearRarityChangedEvent` (and `GearChangedEvent`) with `Source` `Upgrade`.
- Capability `items.gear.upgrade` (Kiln.Items' row: the write row, kiln.items' `gear_upgrade` and the inventory
  transaction); `Items.UpgradeAvailable`.
- **Players without Kiln:** the write is the game's own state on the server (enhancement slots), so a vanilla client should
  show it like any server change; not seen in a real game yet (the equipment wave's client checks).
- Test kit: the fake kiln.items runs upgrades with kiln.items' rules (pay and write together, payers, refusals);
  `HarnessItems.SetUpgradeRolls(0.9, 0.1)` names the rolls of the upgrades that follow.

## Per-instance values: power, armour, max durability, levels outside the range (v8, since 0.2.0-dev.30)

One item can be stronger or weaker than others of its type, and keep it wherever it goes: its power (the damage it deals),
its armour, its max durability and a level outside its type's range. The game stores none of these per item, so Kiln does
it: multipliers in kiln.items' table, applied where the game computes the item's power and armour on the server, and the
values the game sets back at a load (the level, max durability) kept with the item as Kiln's own item data and written
again after each load (docs/re/items.md section 20).

```csharp
var sword = Items.Held(player);
GearValuesResult p = Items.SetPower(sword, 1.25);             // this sword deals a quarter more, wherever it goes
Items.SetArmor(Items.Equipped(player, EquipmentSlot.ArmourUpperBody), 1.5);
await Items.SetDurabilityMax(sword, 900);                     // lasts longer; a half-worn sword stays half worn
await Items.SetLevel(sword, 60, new GearWriteOptions { AnyLevel = true });   // past its type's levels
GearValues? v = Items.ValuesOf(sword);                        // Power, Armor, KeptBaseLevel, KeptDurabilityMax, Kept
Items.SetPower(sword, 1);                                     // its own power again
await Items.SetDurabilityMax(sword, null);                    // the game's own max again
```

- `SetPower(item, scale)` / `SetArmor(item, scale)` → `GearValuesResult` at once (server, tick thread; capabilities
  `items.gear.power`, `items.gear.armor`). Scale 0.01 to 100; 1 removes it. **Power:** the game computes a used item's power
  when an impact of it starts (a swing, a shot, a cast; `items/ItemPower`) and multiplies the impact's damage distribution by
  the weapon's power plus its ammunition's; Kiln multiplies that one item's power, so armour, block, crit and the attacker's
  attributes still act after, as for the game's numbers. An arrow's or a spell's own power is that item's. **Armour:** the
  game sums each worn piece's armour (its type's armour at its level plus each unlocked enhancement's) into the wearer's
  armour values; Kiln multiplies what that one piece adds. Both act on the server world only; the first scale of a kind
  installs its hooks (a server that never uses them hooks nothing). `Status` `Done`, `Stale`, `Full` (8,192 items with a
  multiplier in this world).
- `SetDurabilityMax(item, max, options)` → `Task<GearResult>` (capability `items.gear.write`): the max in place, current
  at the same share (`DurabilityRule.Repair`: full). `null` hands the item back to the game's own max (computed again in its
  next step, the share kept). `Refused` while the game has not computed the item's durability yet (its first step);
  `NotGear` without durability.
- `SetLevel(item, level, new GearWriteOptions { AnyLevel = true })`: a level whose base (the level without the unlocked
  enhancements) lies outside the type's range; the base must still be 1 or more. Without `AnyLevel` such a level is
  `OutOfRange`, as before. A level inside the range ends a kept one.
- **Kept through saves and restarts.** `GearValuesResult.Kept` / `GearResult.Kept` say Kiln keeps the value: item data of
  Kiln's own (owner `kiln-gear`, key `values`), so it follows the item through moves, saves, restarts and a restored backup
  by item data's rules ([../modding/inventory.md](../modding/inventory.md)). After a load the game sets the level back into the
  range and computes max durability again, and kiln.items' multipliers start empty; a pass of Kiln.Blocks about once a second
  writes every kept value again wherever the item differs (also after a rarity change that made the game compute max
  durability again). Item data recognises the item by the level as a load gives it back, so the record still finds it. The
  pass starts only once a value was kept on this install (`<kiln>/data/kiln-gear/used`), so a server that never uses this
  does not start item data for it. `Kept` false: item data is off, and the value lasts until the server world closes.
- **What players see.** Every value acts on the server for everyone. The game's tooltip computes damage and armour from the
  item type on each player's own game, so a vanilla client shows the type's numbers; on Kiln clients with the tooltip values
  on (`ui.tooltip.values`, [ui.md](ui.md)) Kiln shows the item's main number times its multiplier (the value synced to
  players near the item). The level and the durability bar come from the game's own replicated components: every client
  should show them; not yet seen in a real game.
- Test kit: the fake kiln.items keeps the multipliers (no damage or armour is computed) and writes levels and max
  durability with kiln.items' rules; the keeping is Kiln.Blocks' (a mod's test registers no keeper unless Kiln.Blocks
  started).
- Lab (server 26430253, a copy of a real world, no player; docs/re/items.md section 20): the writes, the hooks installing at
  the first scale with the server running on, and after a save and restart every value back within about three seconds;
  with the keeper's pass off the same load showed the game's level clamp and its own max durability.
- Not confirmed in a real game (guided checks `equip-p7.power`, `equip-p7.armor`, `equip-p7.tooltip`, `equip-p7.reload`): the damage a player deals with a multiplied
  weapon, the damage a player takes in multiplied armour, a Kiln client's scaled tooltip number and a vanilla client's
  unchanged one, the level and durability bar after a load on both.

## Tool wear per debit (since 0.2.0-dev.28)
`Items.SetToolWearPerDebit(tool, n)` (or `ToolBlock.WearPerDebit` in a block) sets what one of the game's wear debits
costs a tool: `n` instead of the game's 1, 0 to 32767. Native table `kiln.toolwear` (`kiln_toolwear.h`), published by
kiln-items.dll; capability `items.tool_wear_debit`.

- **Per debit of the game, not per swing.** The game debits a tool in two places a rule reaches: when a hit debits the
  attacker's weapon (the game's own hit-counter cap still decides whether it does), and after a projectile was spawned
  with the tool as the used item. Two debits in one swing spend twice the cost; a swing with no debit spends nothing.
- **Which tools take a rule:** a tool that is gear with an item of its own (an item entity): the game's axes (category
  Tools) and pickaxes (category BuildTools). A torch (its durability burns down by another path), a rake and the
  other tools without an item entity answer `Ok = false`, `Reason` `UnsupportedTool: …`.
- **What stays the game's:** the skill roll that can save a debit, the clamp at 0 (a cost larger than what is left ends
  at 0), the break notice, save, reload and repair. The armor a hit debits and a torch's burn-down are never touched.
  Only the authoritative server world of the process is acted on (a dedicated server, or the host's own game).
- **Hooks:** installed when a mod first sets a valid tool-wear rule, before the rule is published, rather than at startup; they remain installed until process exit even after all rules are removed. The host’s prediction and joined players still use the game’s own wear rate locally: this service does not reconcile their bars with the server, so early breaks, correction timing and repair display remain unconfirmed. Check `16b-g` currently covers the host only and does not require wearing the tool to zero.
- **Rules:** the calling mod's. Setting it again replaces it (same `ToolWearRule`); across mods the latest set is in
  effect, and disposing it reveals the one before. A rule goes when its mod unloads. Rules last for the process: they
  are not saved (durability itself is the game's and is saved as usual). 256 tools can have had a rule in a process.
- **Results** by Kiln's error rule: a refusal is `Ok = false` with `Reason` `UnsupportedTool`, `NoAuthoritativeWorld`,
  `UnknownItem`, `PendingContent` or `TooManyRules`; `Reason` is `""` (not null) when `Ok`. A cost outside 0..32767 or
  no item throws `ArgumentException`; the capability off throws `KilnUnavailableException` naming
  `items.tool_wear_debit`. `Items.ToolWearStatus(tool)` says the cost in effect, whose rule it is, and why not.
- **Events** on the server tick: `ToolWearRuleChangedEvent` once per change of the cost in effect or of whose rule it
  is (an identical replay raises none), in the order the changes were made, whoever made them: a mod (raised before its
  call returns), a native plugin through `kiln.toolwear` or a plugin's owner going away (raised on the next tick). Its
  `Generation` grows by one per change, so five changes within one tick are five events even when the last state equals
  the first. `ToolDurabilityChangedEvent` once per debit that changed the value (a cost of 0 or a debit the skill saved
  raises none), with the item instance as it was at the debit, also under a native plugin's rule.
- **Native plugins:** `define`, `remove`, `changes` and `status` as `kiln_toolwear.h` says; `rule_changes` (appended in
  0.2.0-dev.28: check `header.size`) reads the rule changes after a generation, oldest first, as
  `KilnToolWearRuleChange` records (item, cause `DEFINED`, `REMOVED` or `RETIRED`, cost, owner, rule handle and mod id
  before and after, generation). Reading takes nothing away: each reader keeps its own cursor, so a plugin's reads do
  not cost Kiln's managed events. The service holds the last 1024 changes and says how many a reader that fell behind
  missed (`*lost`); Kiln.Items logs such a gap as a warning. `changes` (durability) is different: it takes the records
  away, and Kiln.Items drains it every tick from the first one, so a plugin that drains it too takes records from the
  managed `ToolDurabilityChangedEvent`s.

### Lab test
`mods/tests/toolwear-test` (with its second rule owner `toolwear-test-b`; how to run it is in its project file) drives
the game's own debits of a real pickaxe in a lab: HitEvents through a declared program, and a projectile spawn. On server
26430253 (lab `dev28-16b-tool-durability`, 2026-11-14), 11 of 11 PASS: a torch refused; one hit at cost 7 spends 7, two
hits 14, a hit over the game's cap 0; the armor a hit debits loses the game's 1 while the weapon loses 7; a projectile
spawn spends 7 at cost 7 and 0 at cost 0; another mod's rule on top (3) and its removal (back to 7, no event for an
identical replay); forced parallel mode (the lab switch) with exact debits; the clamp at 0. Since the fix round
(lab `dev28-16b-tool-durability-events`, 2026-11-14, 12 of 12 PASS) a native rule owner as well (`native/`,
kiln-toolwear-test.dll in `kiln/plugins`): its define, its replacement, a second native owner's define and remove, all
in one tick, then its retirement: five `ToolWearRuleChangedEvent`s in order with consecutive generations, a hit under
its rule spends 6 with its durability event, and the mod's rule is back after the retirement.

**Limits (not run):** worker threads: the two programs whose debits a rule changes (`durability_loss`,
`actor_spawn_entity`) run on the world thread also in parallel mode, by their flags, so the hooks ran on one thread;
the torch's burn-down (the only debit on worker threads) was not driven. The game's own armor: the armor items tried
have no durability of their own, so an axe with a rule stood in as the armor a hit debits. Not run: save and reload,
repair, a world change dropping queued changes (a host test proves it), a debit the skill saved, the world's durability
setting off, the game client (on since 0.2.0-dev.29; check `16b-g` still needs to confirm the predicted bar in a hosted and a joined game).

## C (native plugins and services)
```c
const KilnItemsApi* it = api->service((KilnStr){"kiln.items", 10}, 1);   /* in on_ready */
KilnItemsRequest r = { sizeof r, KILN_ITEMS_LEVEL | KILN_ITEMS_RARITY, chest, 0x43108313u, 1, 20, 3 };
uint64_t ticket;
it->give(owner, &r, on_done, user, &ticket);       /* on_done(const KilnItemsResult*, user): tick thread, Pre */
```
There is no native `DisplayName`. `Items.DisplayName` on a dedicated server returns the English string from the managed
assembly's embedded item catalog (`items.tsv`), because that server's game data has no loca texts. On a client it is
the game's loca text for the current language, read in the background, with a kiln.content text for a mod item. A
native reader of the game's loca table would return null on a dedicated server where `Items.DisplayName` returns the
shipped English name, and a second copy of that catalog inside kiln-items would drift from the managed one.

`give`/`take` (any thread; refused at once with `KILN_ERR_INVALID_ARGUMENT` / `KILN_ERR_UNAVAILABLE`), `cancel`,
`cancel_owner`, `read` / `find_inventories` (tick thread, Pre or Post), `item_count` / `item_at` / `item_info` /
`find_item` / `item_id_of_guid`, `status`. v2 (check `header.size`): `fuel` (a `KilnItemsFuelRequest`: `SET`, `FILL` or
`ADD`, `INVENTORIES`, `DRY_RUN`, `limit`; the callback gets a `KilnItemsFuelResult`), `fuel_read` (tick thread),
`fuel_keep` (a refill policy: `FILL`, or `ADD` with `units` per second; an optional callback with what it refilled, once
per Pre) / `fuel_keep_end`. v4 (header size >= `sizeof(KilnItemsApi)` of v4; feature `items.instances`):
`read_instances(items, count, item_size, flags)` and `read_holder(holder, out, capacity, item_size, &count, &initialized)`
fill `KilnItemsInstance` records (stamp, item, holder, slot, link, flat slot, count, the save record's fields; tick thread),
`instances_status` (any thread); the stamps are in the `reserved` fields of `KilnItemsSlot`, `KilnItemsChange` and
`KilnItemsMoveChange` (0 from an older service). v8 (since 0.2.0-dev.30; feature `items.gear`): `gear_write` queues a
`KilnItemsGearRequest` for one item entity named with its stamp (`AVAILABLE` sets the enhancement slots, so the rarity;
`UNLOCKED` the unlocked enhancements, Level moving with them; `LEVEL`; `DURABILITY_REFRESH` / `DURABILITY_REPAIR`;
`DRY_RUN`; `FORCE_SLOT`; `NOT_EQUIPPED`), run in the next step inside the serial program `kiln_items_gear` through the
game's tracked writes; the callback gets a `KilnItemsGearResult` (status, what changed, before and after). `gear_read`
(tick thread) and `gear_status` (any thread). The game derives the shown rarity and level in the next step; evidence in
docs/re/items.md section 19. v8, package P5 (feature `items.gear.rolls`): `rarity_rolls(owner, rolls, count, size)` sets an
owner's `KilnItemsRarityRoll` rules (item type, `KILN_ITEMS_ROLL_CRAFT` / `LOOT` / `GIVE`, five weights) and
`roll_status` counts what they did; a mid hook where createPide loads the rarity its caller asked for replaces it with a
weighted roll among the rarities the type has a perk slot for, so the game makes the item at that rarity (docs/re/items.md
section 10, "Rarity rolls at creation"). Managed: `RarityRollBlock` ([item-blocks.md](../modding/item-blocks.md#rarity-at-creation-rarityrollblock)).
v8, package P3 (feature `items.gear` and the inventory transaction): `gear_upgrade` queues a `KilnItemsUpgradeRequest`: the
item (a gear request, with `KILN_ITEMS_UPGRADE_WRITE` the write to make), up to 8 costs (plain items), up to 8 payers in
order (the player, or containers) and `expect_available` (the enhancement slots it was priced for); `kiln_items_gear`
counts the costs, takes them on one inventory transaction of its own, counts again after the commit, and only then makes
the write, all or nothing. `DRY_RUN` plans without paying. The callback gets a `KilnItemsUpgradeResult` (status, what
each cost took, what the payers held, the gear result). kiln.items never rolls: a failed roll is the caller's request
without `WRITE`. Managed: `Items.UpgradeRarity` ("Upgrading rarity" above). v9 (since 0.2.0-dev.30.3; feature
`items.rename`): `container_rename` and `rename_status` ("Renaming a container" above).
Details and every field: the header.

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
| `inventory {entity}` → `{inventories:[{link, entity, category, slots:[{item, count, pide}]}], generic, available}` | `Items.Read(entity)`: group `Slots` by `InventorySlot.Link` (an index into `Inventories`, whose entries have an `Inventory` and a `Category`), `GenericSlots`, `AvailableSlots`. Names: `Items.Get(slot.Item)?.Label` |
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
9. Container renames (v9): the list in [modding/inventory.md](../modding/inventory.md#client-checklist-needs-a-player) (items 10 to 15).

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

## The contract in dev.27

Kiln.Items followed [the API contract](../modding/contract.md) since dev.26; dev.27 adds:

- the rows run on a `ServiceCapRows`: reported again under a new registry binding (the test kit's), a failure logged
  once; `items.pide_options` is lowered with the other rows when kiln.items is not loaded;
- `KilnUnavailableException` (a `NotSupportedException`, as before) naming `kiln.items`, `items.fuel` (an older
  kiln.items without water), `items.move` (without moves), `items.names` (`ContainerName`) or `items.transfer`
  (`Transfers`);
- `FuelResult` implements `IKilnResult` (`Reason` is its `Message` when not `Ok`);
- `[TickThreadOnly]` on every member that reaches a tick-thread check (`Read`, `Give`, `Take`, `Move`, the `Fuel`
  calls, ...).
- Tested offline: `managed/tests/Kiln.Contract.Tests/Group10ContractTests.cs` and the conformance rules.
- Since 0.2.0-dev.30.3: `items.rename` (`SetContainerName`, an older kiln.items without v9).

## Typed equipment decisions (since 0.2.0-dev.29)
`PlayerEquippingEvent`: a player's equipment input, one slot (`SingleEquipmentChange` with `InstanceEquipment`,
`StackEquipment` for a plain stack such as arrows, or `EmptyEquipment`) or the game's collection swap
(`CollectionEquipmentChange`, its contents as read in Pre), shares one verdict with the same input's
`PlayerClientActionEvent`: refused when either canceled. Kiln set requests (`SetEquipmentChange`) come with
10-equipment-sets. The set model (`EquipmentSet`, `EquipmentSetEntry`, `EquipmentSetSelection`, `InventorySnapshotToken`)
is declared here as chapter 10 has it. Capability `events.equip`. [decisions.md](decisions.md#equipment).
