# Production stations and bases (kiln.stations)

What Kiln knows about the game's automated production stations (kilns, smelters, looms, seed beds: `FactoryStation`), the
bases the game's crafting code sees (`PlayerBasesSystemServer`), the game's own "fill from chests", stop and select, and
the station events. Read 2026-11-16 on game client Steam 26430625 and dedicated server Steam 26430253 (svn 1287369);
image base `0x140000000`, addresses are RVAs, "C" = client, "S" = server. The API built on it: kiln.stations
(`native/core/abi/services/kiln_stations.h`, `native/services/items/stations.cpp`; C# `Stations` and `Bases` in
Kiln.Blocks), guide [docs/kiln/services/stations.md](../kiln/services/stations.md); symbols `sigdb/symbols/server.d/stations.json`
(shared with the client) and `@gameplay/CraftRecipe.factory_phase` in `server.d/gameplay.json`.

Evidence tags: **[refl]** the game's reflection records (read offline with `tools/kfc`), **[data]** game resources decoded
with `tools/kfc`, **[dis]** disassembly, **[sig]** offline sigdb resolve on both exes, **[cmp]** the client and server
functions compared instruction by instruction (same code, only addresses differ), **[kiln]** already in Kiln's docs/re,
**[inf]** inferred from code, not run, **[hyp]** a guess that needs a lab or client check, **[lab]** seen on a lab
dedicated server (section 7).

## 1. Stations and bases (task item 1)

### 1.1 What a production station is [data][refl]

`kfc find-templates --components` over all 12,269 templates: **31 templates carry `FactoryStation`** (family `Factory`):
`T1_Charcoal_Kiln_Factory`, `T1_Drying_Rack_Factory`, `T1_Farmbed_Factory`, `T1_Forge_Factory`, `T1_HandGrinder_Factory`,
`T1_Seedling_Factory`, `T2_ColoringStation_Factory`, `T2_EctoplasmPress_Factory`, `T2_Fireplace_Factory`,
`T2_HandSpindle_Factory`, `T2_Kiln_Factory`, `T2_Smelter_Factory`, `T2_Table_Saw_Factory`, `T3_Alchemy_Station_Factory`,
`T3_Oven_Factory`, `T3_Tanning_Station_Factory`, `T3_butter_churn_factory`, `T4_Grindstones_Factory`, `T4_Honey_Factory`,
`T4_Laboratory_Factory`, `T4_Loom_Factory`, `T4_SpinningWheel_Factory`, `T5_BlastFurnace_Factory`,
`T5_cardingMachine_Factory`, `T6_Improved_Grinder_Factory` (the one with a rotational-energy consumer),
`T6_Medieval_Grill_Factory`, `T6_coffeemaker_brewer_Factory`, `T6_coffemaker_roaster_Factory`,
`T7_Force_Generator_Factory`, `T7_Infusion_Station_Factory`, and `Base_Craftingstation_Factory`. Six have `Fuel`
(water): oven, honey, laboratory, fireplace, seedling bed, coffee brewer. All have `CheckPlayerInRange`, which
`factory_station` does **not** require (its descriptor has no `with` list).

Kiln.Blocks' `StationKind` table (`managed/services/Kiln.Blocks/World/PropModel.cs`) lists 12 timed kinds; the API must
not use that table to recognise stations: it reads `FactoryStation` and `Crafting.workshopId` from the entity.

Components a station uses (sizes from reflection):

| Component | Size | Fields used |
|---|---|---|
| `FactoryStation` (`0x05599679`) | 104 | `+0x00` u8 `state` (`FactoryStationState`), `+0x04` `runningRecipe`, `+0x08` `runningRecipeInputCategorySelections` (32 B), `+0x28` `nextRecipe` (= the **selected** recipe; it stays set and repeats), `+0x2C` `nextRecipeInputCategorySelections` (32 B), `+0x50` `recipeStart` (Time, ns), `+0x58` `recipePauseDuration`, `+0x60` bool `useAllAvailableCategoryIngredients`, `+0x61` bool `isStoppedByPlayer`, `+0x62` u8 `missingRequirements` (Bitmask8) |
| `Crafting` (`0xad776575`) | 20 | `+0x00` workshop GUID, `+0x10` `workshopId` (which recipes run here) |
| `CraftingInteraction` | 32 | `+0x1C` `workshopId` (which menu opens) |
| `InventorySetup` | 84 | template: 16 generic slots, 16 available, flag `IndestructibleWhileNotEmpty`; inputs and outputs share the slots |
| `CurrentTransform` | 0x38 | position, fixed 32.32 (the game converts with `× 2^-32`, constant C `0x13d265c`) |
| `ServerToggleState` / `NetworkToggleState` | 24 / 16 | the "fire on" mirror, copied from the state by `factory_station_toggle`; what other players see |

`FactoryStationState`: 0 NoRecipe, 1 MissingMaterials, 2 Paused, 3 Stopped, 4 Running, 5 MissingOutputSpace.
`FactoryStationRequirement` (bit index in `missingRequirements`): 0 Recipe, 1 EnergySourceInRange, 2 EnergyAmount,
3 InputMaterials, 4 OutputSpace, 5 AboveWater. `RecipeInputCategorySelections` = 4 × `{u32 categoryId; u8 selectedIndex}`
(8 B each). [refl]

### 1.2 Bases: `PlayerBasesSystemServer` [dis][sig][cmp]

The singleton `PlayerBasesSystemServer` (a sys-read of `player_crafting`, `inventory_actions`, `factory_station_info`,
`handle_crafting_events`, …; its iterator slot in `player_crafting` is `+0x48`) holds the bases the crafting code uses.
Offsets are from the singleton; `i` = base index (u8, 0..15):

| Offset | What | Evidence |
|---|---|---|
| `+0x20` | u32 zone-set version; a change rebuilds the bases | update C `0x473470` / S `0x20be40` |
| `+0x24` | u32 base count (≤ 16) | `BaseAtPosition`, captured `stations/bases.count` |
| `+0x30 + i·0x130` | DynamicArray<u32 entity> **stock containers** `{ptr, count +8, capacity +0x10, grow fn +0x18}` | `BaseStockForPlayer`, `AddToBaseStock`, captured `stock.items` 0x30 / `stock.items_count` 0x38, stride 0x130 (2 captures) |
| `+0x58 + i·0x130` | DynamicArray<u32 entity> **water sources** | captured `stock.water` 0x58 / `water_count` 0x60 |
| `+0x108 + i·0x130` | u64 `CraftingPropMask` of the base (`lock or` by `base_crafting_prop_register`) | C `0x470d30`, `0x471a80` |
| `+0x120 + i·0x130` | float3 AABB min (+pad), `+0x130` max | `BaseAtPosition` (`bases.aabb_y` 0x124 = min.y) |
| `+0x140 + i·0x130` | u32 mask of the zone boxes that make the base | `bases.zone_mask_rel` 0x1C from `+0x124` |
| `+0x144.. / +0x150..` | per-base counters (animals, beds, NPCs) for `ClientBaseIdComponent` | C `0x470700`, `0x470dc0` |
| `+0x1330` / `+0x1338` | pointer / count of the zone boxes, 0x20 B each `{float3 min, pad, float3 max, pad}`, at most 16 | `bases.zones` 0x1330 |

- **`BaseAtPosition(Result* out, PlayerBasesSystem*, const float3* pos)`** C `0x470410`, S `0x208c60`: first base whose
  AABB holds the point (min inclusive, max exclusive) and, when it has more than one zone, one of whose zone boxes holds
  it. `out+0` u8 error (0 found, `0x1F` in no base), `out+8` u8 index. 25 callers on the client. [dis][sig][cmp]
- **`RebuildBases(sys, world)`** C `0x46dda0`, S `0x206510`: clears every base, copies the zone list (`0x9bbfa0`) and
  merges overlapping zone boxes into bases (`AddBase` C `0x46eb30`). So **one record can hold several altars** when
  their zones overlap. [dis]
- **`RebuildBaseStock(sys, world)`** C `0x46fde0`, S `0x208590`: zeroes both lists of every base, enumerates every entity
  with **`InventoryCraftingStock`** (hash read from `.data` C `0x152fc20` = `0xe41a0d1f`) and adds each to list A of the
  base that holds its position (`AddToBaseStock` C `0x471220`, S `0x209b20`; error `0x1F` outside every base, `0x27` list
  full), then every entity with **`FuelCraftingStock`** (`0x560461c1`) whose `NetworkFuel` (`0x15557d63`) is not zero to
  list B. [dis][data]
- **When**: the server's system update `0x23b000` (called from the server frame `0x7bbffc` at `0x7bc585`) calls
  `PlayerBasesSystemServer::update` S `0x20be40` with no player test: zones changed → `RebuildBases`; zones present →
  `RebuildBaseStock`, **every frame**. The same frame function reaches the world step (`SimStep` wrapper `0x6c8e40` from
  `0x7bcb14`) later, on the same thread. So the lists are current and stable during the step and in Kiln's Pre/Post.
  On a hosting client the hosted server's update is C `0x473540` (from `0x4a28bd`); the client's own
  `PlayerBasesSystemClient` is C `0x473470` (from `0x3af099`). [dis] Order inside the frame: **[inf]**, lab check L1.

### 1.3 Which containers are "the base's storage" [data][dis]

Templates with `InventoryCraftingStock` (10): `Prop_Decoration_T1_Storage_24_Magic`, `T2_Storage_32_Magic`,
`T3_Storage_40_Magic`, `T4_Storage_56_Magic`, `T6_Storage_72_Magic`, and five player-planted refilling harvest plants
(`Scatter_Loot_Wetland_Above_Bananaplant_01_e_fruits_harvest_PlayerPlanted_Refill`, coffee tree, hazelnut, coconut
palm, date palm). `InventoryCraftingStock {bool canReceiveAreaDeposit}`, overridable per entity by
`DynamicInventoryCraftingStock` (2 bytes, reflection: `+0 canReceiveAreaDepositOverrideIsSet`, `+1
canReceiveAreaDepositOverride`). By the programs' access lists, `area_deposit_trigger` (S update `0x88a50`) reads the
template's `InventoryCraftingStock` and the entity's `DynamicInventoryCraftingStock`, and `inventory_actions` (S
`0x1a5af0`, a player's inventory input) writes the latter: the per-chest deposit switch is taken to be that write [refl],
not seen in a game. Templates with `FuelCraftingStock` (13):
wells (`Prop_NPC_Water_Well_a/b/c`, desert/wetland wells) and the two water barrels.

Ordinary chests (`Prop_Decoration_T1_Storage_24`, …) have no `InventoryCraftingStock`: **the game's fill never uses them**,
and neither does crafting from base stock (`rules/InventoryPay` S `0x199c80` calls `BaseStockForPlayer` too). This is the
game's own rule ("magic chests").

### 1.4 Base identity

- The flame altar is the one template with `BaseIdComponent` (`Home_Base_FlameAltar`, family `Altar`, also
  `PlayerSpawnPoint`, `TriggerShape`). `BaseIdComponent` (48 B) `+0x00 baseId` (a GUID; made once by the INIT program
  `base_id_generator` from `GuidGenerator`), `+0x10` `buildZoneSize` (not authoritative), `+0x20 endOfTimeout`,
  `+0x28 wasAbandoned`. [refl][kiln build.md §11]
- The game itself maps altar → record by position: `home_base` (C `0x339040`) reads the altar's `BaseIdComponent` and
  `CurrentTransform` and calls `BaseAtPosition` (C `0x46fdb0`) for the record's counters. Kiln does the same.
- **Proposed stable base id** = the `baseId` GUID of the record's altar; when a record holds several altars, the
  numerically smallest GUID, with every altar listed (`Base.Altars`). Survives restarts (the save keeps
  `BaseIdComponent`; save regions and characters' respawn bases are keyed by it, docs/re/build.md §5,
  docs/re/characters.md). The record index is per session and can change when zones change. Name: the altar's
  `CustomPlayerString` (`Items.ContainerName` already reads any entity's).
- Kiln.Build's `Build.Bases` (BuildZoneSystem list 0: altar, centre, half size) stays the per-altar view.

### 1.5 Enumerating stations of a base

Stations are not in any base list. A station belongs to the record that `BaseAtPosition(station position)` returns;
that is the same test the game applies to the stock containers. Kiln finds stations by component (`World.Find` /
kiln.world `find` on `FactoryStation`, or the service's own scan of the entity map) and groups them per record.

---------------------------------------------------------------------------------------------------------------------

## 2. A station's state (task item 2)

### 2.1 The state machine, `factory_station` (C update `0x335e80`, S `0xf5980`, flags `0x200`, every step) [dis][cmp]

Access: read `Crafting`, `CurrentTransform`; read-opt `DynamicIsInWaterCheck`, `DynamicRotationalEnergyConsumer`; write
`FactoryStation`; sys-read `BalancingRegistry`, `ServerPlayers`, `ServerProgressSystem`, `ItemRegistry`, `RecipeRegistry`,
`AttributeRegistry`; sys-write `KnowledgeTriggerQueue`, `ServerFactoryInfo`; other-read `InventorySetup`; other-write
`Fuel, Inventory, InventoryEmptyState, ItemState, NetworkFuel, OwnerRelationship, SlotSelection`; events-out
`CraftRecipeSuccessEvent`. Per station, by state (jump table S `0xf5cf0`):

| State | What the program does |
|---|---|
| NoRecipe | `nextRecipe` set → **StartNext** (C `0x32def0`, S `0xee0a0`) |
| MissingMaterials | `nextRecipe` set → StartNext; else `CancelRunning` |
| Paused | resume check (S `0x110e20`) |
| Stopped | `runningRecipe` set → **CancelRunning** (C `0x32d2b0`, S `0xed460`: refunds the running recipe's inputs into the station through an inventory transaction (`0x19ac50` → `InventoryAddItems`), clears `runningRecipe`/`recipeStart`). Then water/energy → pause; `isStoppedByPlayer == 0` → StartNext; else stays stopped |
| Running | `nextRecipe == 0` → CancelRunning. `isStoppedByPlayer` → state := Stopped. Water/energy → pause. Then `end = recipeStart + craftingDuration × f(factoryProductionSpeedFactor)` (`FactorySpeed` C `0xe903d0` / S `0x937510` reads `BalancingRegistry+0x90` → settings `+0x3C`); `now ≥ end` → **Complete** (C `0x32dab0`, S `0xedc60`) |
| MissingOutputSpace | like Running's completion path, retried |

- **StartNext** checks pause conditions, calls `TryStart` (S `0xe4330`): `CraftRecipe` with context flag **`0x08`**
  (take the inputs only; `| 0x40` when "use all ingredient options" is on: `RecipePayAlt`), failure → state
  MissingMaterials; success → state Running, `runningRecipe = nextRecipe`, running selections := next selections,
  `recipeStart = now`. **Inputs leave at the start.**
- **Complete**: `CraftRecipe` with flag **`0x10`** (outputs only: the recipe function skips payment when `ctx+0x10 & 0x10`,
  S `0x19a25a`); failure → state MissingOutputSpace; success → knowledge push, state NoRecipe, so the next step starts
  the next cycle. **Outputs arrive at the end.** `CraftRecipe` sends `CraftRecipeSuccessEvent {workshopId, recipeId}`
  (16 B, no station id). [dis]
- When the state byte changes, the program calls **`FactoryInfoInvalidate(ServerFactoryInfo*, station)`**
  (C `0x4a6350`, S `0x23ec60`): it clears the station's "sent" bits so players with the station's screen open get it
  again. `player_crafting` calls the same before any station action. [dis]

### 2.2 What the API reports, and how each value is computed

| Field | Source |
|---|---|
| running / stopped (the player's stop button) | `state`, `isStoppedByPlayer` |
| selected recipe(s) and category choices | `nextRecipe`, `nextRecipeInputCategorySelections`, `useAllAvailableCategoryIngredients` |
| the craft in progress | `runningRecipe`, its selections, `recipeStart` |
| progress | `(now − recipeStart) / (craftingDuration × f(speed))`, clamped 0..1, while Running; `now` = the world's program time (`gameplay/ProgramTime`); the speed function read from `FactorySpeed` or kiln.settings (`factoryProductionSpeedFactor`, live) |
| powered / water | `missingRequirements` bits EnergySourceInRange, EnergyAmount, AboveWater; state Paused |
| inputs / fuel / outputs | the station's `Inventory` (Items.Read): **no slot is typed**; Kiln classifies a stack as output when its item is an output and not an input of the running or selected recipe; "loaded crafts" = `min over inputs (have_i / count_i)`, exactly as the fill computes it (§3.2). Water: `Fuel` of the station (the 6 templates with water, §3.6) |
| loaded? | on a dedicated server every entity simulates; `factory_station` has no player-in-range filter, and the lab already saw a rack cycling with no player (docs/re/building.md §5). Not a v1 field; lab check L6 |

### 2.3 `PlayerCraftingAction` and how the server handles it [refl][dis][cmp][sig]

`PlayerInput.fromClient.playerCraftingAction` at `PlayerInput+0xBC` (68 B): `+0x00 craftingStationId`, `+0x04 recipeId`,
`+0x08 itemId`, `+0x0C u16 amount`, `+0x10 waterAmount`, `+0x14 waterSourceId`, `+0x18` selections (32 B), `+0x38 bool
useAllAvailableIngredientOptions`, `+0x3C` version, `+0x40 u8 type`. Consumed version at
`ServerConsumedPlayerInput+0x44`. Program `player_crafting` (C `0x2e62b0`, S `0xd2250`, flags `0x210` = host only,
chunked) runs each new version; dispatch (jump table C `0x2e6520`):

| Type | Name | Server action | Checks |
|---|---|---|---|
| 0 | CraftRecipe | sends `CraftRecipeEvent` (handled by `handle_crafting_events`) | there |
| 1 | **AutomaticTransfer** | `AutoTransferAction(action, iterator)` C `0x2c6b30` / S `0xb2080` | §3.2 |
| 2 | **SelectCraftingStationRecipe** | `StationWrite` (C `0x2c0c60`, S `0xabf70` = `WriteOther` on the program's `FactoryStation` accessor) → `nextRecipe = recipeId`, next selections = action's, `isStoppedByPlayer = 0` | **none** (no distance, no permission, no workshop or knowledge test; the workshop gate only comes at the start) |
| 3 | SetCraftingStationUseAllIngredientOptions | `useAllAvailableCategoryIngredients = action's` | none |
| 4 | **CraftingStationStopProduction** | `isStoppedByPlayer = 1` | none |
| 5 | TransferWater | C `0x2cbfb0` | not traced |

Before the dispatch it calls `FactoryInfoInvalidate(ServerFactoryInfo, action.craftingStationId)`; after it, the
consumed version is stored. **Resume = type 2 with the station's own selected recipe** (it clears the flag).

---------------------------------------------------------------------------------------------------------------------

## 3. Actions (task item 3)

### 3.1 Fill from chests: the game's path [dis][cmp][sig]

`AutoTransferAction` (C `0x2c6b30`, S `0xb2080`), for the player = the program's current entity:

1. Builds an inventory-transaction setup block (docs/re/items.md §3) from `player_crafting`'s iterator: flags 0, stock mode
   0, the context, ItemRegistry, BalancingRegistry, AttributeRegistry, accessors InventorySetup (read), Inventory,
   SlotSelection, ItemState, OwnerRelationship, InventoryEmptyState, **NetworkFuel**, **Fuel**, the PickupEvent and
   InventoryFull senders, the knowledge queue, **operator = the player**, **`+0xD8` = PlayerBasesSystemServer**,
   **`+0xE0` = the player's position** (float3). `InventoryTxCtor` + `InventoryTxInit`.
2. **`BaseStockForPlayer(out, &stock, 0, PlayerBasesSystemServer*, &position, player)`** (C `0x46fa10`, S `0x2081c0`):
   `PlayerPermissions(player)` (C `0x3b8410`, S `0x163040`: `BasicPlayerInfos[player−1]+0x9D`) must have bit **0x02 =
   `CanAccessInventories`** (else `0x16`); `BaseAtPosition(player position)` (else `0x1F`); `stock` = the record's two
   lists `{A.ptr, A.count, B.ptr, B.count}`. **The player has to stand in the base**.
3. `recipeId != 0`: `RecipeFind`; **`ResolveRecipeInputs(recipe, &out, &selections, RecipeRegistry)`** (C `0xe86340`,
   S `0x92d4c0`) turns every input into a concrete 24-byte item stack (`ResolveInput` C `0xe99df0` / S `0x941030`: a
   category input takes the selected option of `FindInputCategory` (C `0xe99890`, S `0x940ad0`) and its count is
   `ceil(count × option.scaling)`); then **`AutoTransferRecipe(tx, &stock, &inputs, station, u16 amount,
   recipe.waterCharges, player)`**. `itemId != 0`: `AutoTransferItem` (C `0x3f7950`, S `0x198bf0`).
   `waterAmount != 0`: `AutoTransferWater` (C `0x3f8200`, S `0x1994a0`).
4. Result 0 → `InventoryCommit`, else `InventoryRollback`.

**`AutoTransferRecipe`** (C `0x3f7ab0`, S `0x198d50`), `int (InventoryTx*, const BaseStock*, const Slice<ItemStack24>*
inputs, u32 station, u16 crafts, u32 waterCharges, u32 player)`:

```
no inputs                                  -> return 1
station InventorySetup missing (tx[3])     -> return 5
have[i]  = sum of input i over every slot of every link of the station
loaded   = min over inputs of have[i] / count[i]                (0 if any count is 0)
           (with water: also min with station water / (charge units * waterCharges))
for each input i:
    need = clamp((crafts + loaded) * count[i] - have[i], 0, 0xFFFF)
    if need == 0: continue
    if player: take up to need from the player's inventories          (removeItems, preferred slot "none")
    for each container c in base stock list A, in list order, while something is missing:
        take the rest from c
    still missing                          -> return 4  (missing materials)
    addItems(station, item, need)          -> on error or leftover: return 2 (no room in the station)
water: need = charges*(crafts+loaded) - station water; AutoTransferWater from list B -> its code
return 0
```

So the game's `amount` means **"this many more complete crafts than the station holds now"**, every input topped up
to the same level; partial stacks count. **All or nothing**: one transaction, rolled back by the caller on any code.
The client uses the same function as a **dry run** to enable the button: player_ui (C `0x2be77f`) calls it on a scratch
transaction and rolls back (`0x2be78d`), with an amount from the station's `ClientFactoryInfo` (how that amount is
chosen was not traced).

### 3.2 Fill from chests: how Kiln calls it (no player needed)

Kiln does steps 1, 3, 4 itself, **inside its own program**, and replaces step 2:
- the stock = the 4 qwords of the station's own record (`BaseAtPosition(station position)`), read from the singleton; no
  permission test (no player); `player = 0` unless the caller asks to also take from a named player's inventories (the
  button's behaviour, opt-in);
- setup as the game builds it, with operator 0 (a container, as kiln.items gives to chests), no knowledge queue,
  `+0xD8` the singleton and `+0xE0` the station's position;
- **crafts chosen by Kiln**, so that the all-or-nothing call does not fail for a missing stack: Kiln counts the stock
  (list A, per item, with optional reserves the caller keeps back), computes the largest `n ≤ requested` the stock
  covers for every resolved input, calls with `n`, and on code 2 (no room) or a race tries `n/2`, … (each attempt rolled
  back) within the same update, at most 6 attempts (kMaxTries);
- a dry run = the same call, then rollback (exactly what the client's button does);
- after a commit: `FactoryInfoInvalidate(station)`.

`AutoTransferRecipe` never reads the program's current entity (it reads through the transaction's accessors and job
storage only, [dis]), so it is safe to call from a SYSTEM program. The player-taking path, the water path and the
transaction internals are the game's.

Known limits (from the code): the station's **selected** category options are used (Kiln passes the station's
`nextRecipeInputCategorySelections`); with "use all ingredient options" on, the fill still loads only the selected
option ([inf], lab L7). Gear inputs (`HasPIDE`) go through the same `removeItems`/`addItems`.

### 3.3 Stop, resume, select

Kiln writes, inside its program, through `WriteOther` on its own `FactoryStation` other-write accessor (the path
kiln.world's tracked writes and kiln.items' water already use, docs/re/world.md "Tracked writes"), exactly the bytes
the game's type 2 and 4 write, after `FactoryInfoInvalidate(station)`:
- **Stop**: `isStoppedByPlayer = 1`. The running cycle is aborted and refunded next step (§2.1); the station shows
  Stopped to everyone. Option **after the current craft**: Kiln waits for the station's completion (§4) and sets the
  flag then; the cycle that `StartNext` begins in the same instant is aborted and refunded at once, so nothing is lost
  ([inf], lab L4).
- **Resume**: `isStoppedByPlayer = 0` (`nextRecipe` unchanged); the station starts a new cycle next step.
- **Select**: `nextRecipe`, selections, `isStoppedByPlayer = 0` (or keep it stopped on request); `nextRecipe = 0` clears
  the selection (the game then cancels the running cycle).
The server checks nothing for these (§2.3); Kiln checks the entity has `FactoryStation`, and for Select that the
recipe's `workshopId` equals the station's `Crafting.workshopId` (else the game would sit in MissingMaterials). The
save keeps `FactoryStation` (a running rack came back running after a restart, docs/re/building.md §5); that our
written flag survives a restart is lab check L4.

### 3.4 Collect finished items

Already in Kiln: `Items.Move(station, slot, chest)` (the game's `SlotTransfer` S/C `0x1b4550`/`0x4149b0`, keeps item
entities, docs/re/items.md §13). The station's output stacks are classified as in §2.2. Choosing a chest is the mod's
policy; Kiln offers a helper that mirrors quick stack: base stock containers already holding the item, then any with a
free slot. Moving into an ordinary chest is allowed (it is just a move).

### 3.5 Recipes, ingredient categories, stack sizes

| Data | Where | In Kiln today |
|---|---|---|
| recipes (2,542 in the game data) | `RecipeRegistry` map `+0x48` | Kiln.Content `Recipes` (inputs, outputs, duration, `workshopId` field) |
| a workshop's recipes | RecipeRegistry `+0xF8` (workshop → `SmallDynamicArray<RecipeInfo*,512>`) | captured by kiln.content (`content/recipes.workshops`); no public "recipes of workshop" call |
| **input categories (22)** | RecipeRegistry `+0xA0` HashMap: bits `+0xA8`, capacity `+0xB0`, keys `+0xC8`, values `+0xE0` (`RecipeInputCategory*` = `{u32 id; BlobArray<{ItemId, float scaling}>}`), count `+0xF4` | **new** (Kiln.Content knows a category's id and count on a recipe input, not its options) |
| options with scaling ≠ 1 | 4 of the 22 categories | new |
| `RecipeInfo.waterCharges` `+0x98`, `craftingDuration` `+0x38` | reflection | Content fields |
| max stack | `ItemInfo+0x14` | `Items.Get(id).MaxStack` |

The category map offsets are captured from `FindInputCategory` (`@stations/FindInputCategory.fields`); 22 categories and
2,542 recipes counted in `RecipeRegistryResource` (`5e57de0f-…`) with `tools/kfc decode`. [data][sig]

---------------------------------------------------------------------------------------------------------------------

### 3.6 A recipe's water at craft time (server 26430253, exe `f0d824be…`) [dis][data]

Why a pack's "station tank water" input did nothing of the kind (netjunk, 0.2.0-dev.30.2), and what a recipe's water is.

- **The field.** `RecipeInfo` has two u32s: `waterAmount` `+0x94` and `waterCharges` `+0x98` (reflection, 168-byte
  struct). In the game's data (`RecipeRegistryResource` `5e57de0f-…`, decoded with `tools/kfc`) **all 2,542 recipes have
  `waterAmount` 0**; 107 have `waterCharges` 1, 2, 5 or 10. Seedbed 62 (1 or 2), Alchemist 16 (1, 2, 10), Kitchen 16 (5),
  Coffee Brewer 3, Cooking Station 2, Beehive 2, hand 3, Oven 1, Laboratory 1, Collector 1. No recipe names an item for
  water: `Food_T1_raw_hydration_Water_Pouch_Filled_DEPRECATED` ("Water", a canteen) is in no recipe's inputs. [data]
- **The payment.** `rules/RecipeApply` (S `0x19a1d0`) pays the inputs (`InventoryPay` S `0x199c80`, or `RecipePayAlt`
  with flag `0x40`), then `if (recipe+0x98 > 0)`: units = `0x946c80(balancing)` (`settings+0xFC`, 1 without settings;
  64 measured: §7 L3) × `waterCharges` × crafts (S `0x19a2b3`, `0x19a2bb`), and takes them through `0x199f10` (or the
  operator alone in the `0x40` path, `0x1b09e0`). `0x199f10` by the transaction's stock mode (`tx+0xb1`): mode 0 takes from
  the operator only (`0x1b09e0`); mode 2 (a player's craft, §10.1) the operator first, then `BaseStockForPlayer`'s lists
  (`0x1b0bf0` over one list, `0x1b09e0` over the other); mode 1 the operator last. Not enough → 4 (missing materials).
  Nothing in `RecipeApply` reads `+0x94`. [dis]
- **So at a factory station** (`TryStart`, flag `0x08`, stock mode 0) the water leaves **the station's own tank** (its
  `Fuel`), with the inputs, at the start of the craft; without enough the station stays in MissingMaterials. **At a
  crafting menu** (a player's craft) it comes from the crafter, then the base's water sources (list B: wells, barrels,
  §1.2) — how the game's Kitchen soups and Alchemist potions get theirs. The fill (§3.1) pours `waterCharges` per craft into
  the station from list B. The recipe screen shows the charges as an ingredient row named and drawn after the "Water" item
  ("Water 92 / 2", owner pass 2026-11-20): that row is `waterCharges`, not the item. [dis][rt]
- **Tanks.** 33 templates carry `FactoryStation`; **six** have `Fuel` + `FuelSetup` (§1.1; the "7" of §2.2 is
  corrected), by `Crafting.workshopId` and `maxFuelCharges`: T1_Seedling_Factory Seedbed `0xba67134f` 150,
  T3_Oven_Factory Oven `0x1ee9f0bf` 100, T2_Fireplace_Factory Cooking Station `0x39fbd98f` 100, T4_Laboratory_Factory
  Laboratory `0x20b86c20` 100, T6_coffeemaker_brewer_Factory Coffee Brewer `0x6b8247d6` 100, T4_Honey_Factory Beehive
  `0x269a27a7` 50. The other factory stations have no tank, so a recipe with water there never starts.
  `kiln_manage::catalog::TANK_STATIONS` / `DRY_STATIONS` hold both lists (kiln lint KL408, Pack Studio). [data]
- **Kiln.** A new recipe's `"water"` writes `waterCharges` (native/services/content/recipe_water.h), after a station-only
  recipe's requirements are cleared; `waterAmount` is left as copied or cleared. A rule changes a game recipe's
  `waterCharges` like any field. Not checked in a real game yet (0.2.0-dev.30.3): the craft-time take at a tank station
  and at a crafting menu, and the screen's Water row for a new recipe.

## 4. Events (task item 4)

| Event | Game seam | Proposed source | Confidence |
|---|---|---|---|
| a cycle finished, outputs in the station | `Complete` → `CraftRecipe(ctx, recipe)` with `ctx+0x10 & 0x10` (outputs phase); `ctx+0` operator = `ctx+4` station = the station | kiln.gameplay's existing CraftRecipe hook (`gameplay/CraftRecipe` S `0x210f00` / C `0x478680`), **extended to read the flag byte** and report the phase; the managed side posts `StationCraftedEvent` for phase "outputs" of a FactoryStation entity, next Pre, with the recipe's outputs | [dis] for the flags; double report today [inf from code: the hook keeps successes where operator == station], lab L5 |
| a cycle started (inputs taken) | the same hook, flag `0x08` | `StationStartedEvent` (optional) | same |
| state changed (started, stopped, paused, missing materials, output full) | none: `FactoryStation` written by `factory_station` and `player_crafting` | per-step diff of every known station's `state`, `isStoppedByPlayer`, `nextRecipe` in Pre (`GetComponent`, tens of stations) | [inf]; cost lab |
| who stopped/selected/filled: a player | `PlayerCraftingAction` types 1..4 in `PlayerInput` | the existing `PlayerClientActionEvent` (`playerCraftingAction`, held one step, cancelable, docs/kiln/services/player-state.md) decoded into a typed **`StationPlayerActionEvent`** (station, type, recipe, amount, selections) | [kiln] for the hold; decoding [refl] |
| …: Kiln | Kiln's own writes | the state event names the mod | n/a |
| placed / removed | entity created / destroyed | kiln.gameplay `ENTITY_JOINED`/`ENTITY_LEFT` filtered to `FactoryStation` (template family Factory via kiln.spawn) | [kiln] |
| base list changed | `+0x20` version, `+0x24` count | Pre poll of the version: `BasesChangedEvent` | [dis] |

`CraftRecipeSuccessEvent` (the game's own, `0x7fe02e20`, sent by `CraftRecipe` S `0x211458`) has no station id, so it is
not usable for per-station events. Hooking `Complete` directly (S `0xedc60`) is the fallback if the gameplay change is
refused; the kiln.gameplay route (an existing hook) is preferred, and it is what Kiln does since 0.2.0-dev.29: the
craft core's context has a flags byte (`ctx+0xA0-0x90` from the hook's `ctx`) whose bit `0x10` is set for the completion
call of a factory cycle and clear for its start (`@gameplay/CraftRecipe.factory_phase`, feature `gameplay.craft_phase`).

---------------------------------------------------------------------------------------------------------------------

## 5. Threads and sides (task item 5)

- **Actions** (fill, stop, resume, select): inside a Kiln ECS program of the **server world**, as kiln.items does
  (docs/re/items.md §1: the transaction holds the `ProgramContext`, reads through program accessors, sends events through
  the program's event slots). Proposed program `kiln_items_stations`, flags SYSTEM | HOST_ONLY, registered through
  kiln.world after the game's programs, with exactly the access `player_crafting` declares minus `PlayerInput` /
  `ServerConsumedPlayerInput`: sys-read `BalancingRegistry`, `PlayerBasesSystemServer`, `g38_items::ItemRegistry`,
  `g38_items::RecipeRegistry`, `impact::AttributeRegistry`; sys-write `KnowledgeTriggerQueue` (optional),
  `ServerFactoryInfo`; other-read `CurrentTransform`, `Crafting`; other-write `FactoryStation, Fuel, Inventory,
  InventoryEmptyState, InventorySetup, ItemState, NetworkFuel, OwnerRelationship, SlotSelection`; events-out
  `InventoryFull`, `PickupEvent`. Requests are queued from any thread, run in the next update (≤ 16 per step), and their
  callbacks come in the next Pre on the tick thread (one step, ~41 ms on a dedicated server). Because it registers after
  the game's programs and declares `FactoryStation`/`Inventory` writes, it runs after `player_crafting` and
  `factory_station` in a step ([kiln] world.md "Where the write sits"): a player's own action in the same step runs first.
- **Reads** (bases, stock, stations, categories): the tick thread, Pre or Post, when no job runs. `GetComponent` for
  components; the `PlayerBasesSystemServer` and `RecipeRegistry` singletons read directly. The stock lists are rebuilt in
  the server frame before the step ([dis], order [inf], lab L1); Kiln copies what it hands out.
- **Events**: posted in Pre (next step) on the tick thread, like every Kiln gameplay event.

| | dedicated server | hosting client | joined client |
|---|---|---|---|
| reads | yes | yes (the hosted server world; same functions, [cmp]) | **no** for kiln.stations: `FactoryStation` is server-only. Since 0.2.0-dev.31.13 a read-only view of the client's own station cache (`ClientFactoryInfo`): §14 |
| fill / stop / resume / select / collect | yes | yes | no: the mod sends a request to its own server side (Kiln.Network); the only vanilla route would be writing the local player's `ClientPlayerInput.playerCraftingAction` (acts as the player, needs the player in the base): not proposed |
| events | yes | yes | no (`StationPlayerActionEvent` only where `PlayerClientActionEvent` can hold) |

Other loaders: Kiln adds **no hook** on `player_crafting`, `factory_station` or the fill functions (another loader may
detour `player_crafting`'s update; Kiln stays off it). The only hook involved is kiln.gameplay's existing `CraftRecipe`
hook (already chain-safe).

---------------------------------------------------------------------------------------------------------------------

## 6. Signatures (`sigdb/symbols/server.d/stations.json`) [sig][cmp]

All patterns were checked with `tools/sigport/sigdb_check.py` composed into a copy of the full sigdb tree
(`server.d/stations.json`, shared with the client like every server.d file): **each resolves exactly once on client
26430625 and on server 26430253, every constant captured with the same value on both, all four features on, no name
collides.** Client and server bodies of the main functions compare instruction-for-instruction identical
(only jump-table addresses differ).

| Name | C 26430625 | S 26430253 | How it is found |
|---|---|---|---|
| `stations/PlayerCraftingUpdate` | `0x2e62b0` | `0xd2250` | prologue `40 55 41 57 48 8D 6C 24 D8 48 81 EC 28 01 00 00 41 B8 E0 00 00 00` (22 B; also the descriptor `player_crafting`) |
| `@stations/PlayerCrafting.dispatch` | in update | in update | 3 scoped patterns: captures `input.crafting_action` 0xBC, `consumed.crafting_action` 0x44, `action.version` 0x3C, `action.type` 0x40, `factory.next_recipe` 0x28, `next_selections` 0x2C, `stopped_by_player` 0x61, `use_all` 0x60, `FactoryInfoInvalidate`, `AutoTransferAction`, `StationWrite` (rel32) |
| `stations/AutoTransferAction` | `0x2c6b30` | `0xb2080` | prologue (26 B) + captured from the dispatch |
| `@stations/AutoTransferAction.calls` | | | captures `BaseStockForPlayer`, `ResolveRecipeInputs`, `AutoTransferRecipe`, `recipe.water_charges` 0x98, `action.amount` 0x0C |
| `stations/AutoTransferRecipe` | `0x3f7ab0` | `0x198d50` | prologue `44 89 4C 24 20 4C 89 44 24 18 48 89 54 24 10 48 89 4C 24 08 55 56 57 41 56 41 57 48 8D AC 24 60 FF FF FF` (35 B) and the capture, which must agree |
| `stations/ResolveRecipeInputs` | `0xe86340` | `0x92d4c0` | prologue (35 B, through `cmp [rcx+0x2C], esi`: the input count) + capture |
| `@stations/ResolveInput.calls` → `stations/FindInputCategory` | `0xe99890` | `0x940ad0` | the call in `ResolveInput` (C `0xe99df0`, S `0x941030`; its prologue has a twin, so it is found by the call site) |
| `@stations/FindInputCategory.fields` | | | `categories.count` 0xF4, `cap` 0xB0, `bits` 0xA8, `keys` 0xC8, `values` 0xE0 |
| `stations/BaseAtPosition` | `0x470410` | `0x208c60` | prologue with `bases.count` 0x24 |
| `@stations/BaseAtPosition.fields` | | | `bases.aabb_y` 0x124, `zone_mask_rel` 0x1C, `zones` 0x1330, `stride` 0x130 |
| `stations/BaseStockForPlayer` | `0x46fa10` | `0x2081c0` | prologue + capture; fields `stock.items` 0x30, `items_count` 0x38, `water` 0x58, `water_count` 0x60, stride (2nd vote), `PlayerPermissions` (C `0x3b8410`, S `0x163040`), `permission.stock` 0x02 |
| `stations/RebuildBaseStock` | `0x46fde0` | `0x208590` | prologue (evidence / optional) |
| `stations/FactoryStationUpdate` | `0x335e80` | `0xf5980` | prologue; `@…timing` captures `FactorySpeed` (C `0xe903d0`, S `0x937510`), `recipe.duration` 0x38, `factory.recipe_start` 0x50 (also fixed) |
| `@stations/FactorySpeed.fields` | | | `balancing.settings` 0x90, `settings.factory_speed` 0x3C |
| `stations/FactoryStart` | `0x32def0` | `0xee0a0` | prologue (events, optional) |
| `stations/FactoryComplete` | `0x32dab0` | `0xedc60` | prologue; `@…craft` captures `craft.outputs_only` 0x10 |
| `stations/FactoryInfoInvalidate` | `0x4a6350` | `0x23ec60` | captured from the dispatch |
| `stations/StationWrite` | `0x2c0c60` | `0xabf70` | captured (evidence; Kiln uses its own `WriteOther`) |

Fixed constants (reflection, not code): `factory.size` 0x68, `state` 0, `running_recipe` 4, `running_selections` 8,
`pause_duration` 0x58, `missing` 0x62, `basesys.record0` 0x30. Features: `stations.read`, `stations.fill`,
`stations.control`, `stations.events`. Existing names reused, not redefined: `items/*` transaction, `world/*`,
`gameplay/CraftRecipe`, `gameplay/ProgramTime`, `content/RecipeFind`.

The build records (`sigdb/builds/{client,server}/<sha>.json`) get the new service at level `static` when the file lands;
`lab` after the lab tests (section 11).

---------------------------------------------------------------------------------------------------------------------


---------------------------------------------------------------------------------------------------------------------

## 7. Lab results [lab]

Lab dedicated server 26430253 (a copy of the standard lab save: one base, altar 1025, no player), 2026-11-16, test mod
`mods/tests/station-actions-test` (kiln.log lines `station-actions-test: PASS|FAIL`). Run 3: 62 PASS, 1 FAIL (the altar's
`EntityData.IsPersistent`, below, since an INFO line); run 4 after a SIGINT stop and a start: 4 PASS, 0 FAIL.

- **Bases** (L1): the running core resolves `stations.json` like the offline check ("stations: reads on, fill on, control
  on, events on"). `PlayerBasesSystemServer`'s list A holds a placed Magic chest and not a plain chest, a few steps after
  the placement, and still after the whole run (some thousand steps, no player). The base's id is its altar's own
  `BaseIdComponent` id. The lab save's base has Magic chests of its own (entity 1063 among them): the game's fill takes
  from them in list order, so counts are the base's, not one chest's. `EntityData.IsPersistent` is **false for the flame
  altar** and true for a placed kiln and Magic chest: per-base data is kept by the altars' ids.
- **Fill** (L2): up to 5 from 0 moves 5 crafts' logs (12 a craft) and dirt (3) from the Magic chests in one
  transaction; the ledger names only stock containers (a chest's take comes from its inventory entity: the chest 2542 took
  from 2543); add 2 makes 7; up to 7 is unchanged; an exact fill beyond the stock moves nothing (the game's code 4); a
  reserve leaves exactly the reserve in the base; a dry run moves nothing; a plain chest's logs are untouched.
- **A stopped station that receives inputs** (a fill on a stopped kiln, MissingMaterials with the stop flag set) starts a
  cycle in its next update (the inputs leave) and the stop aborts it in the update after (the inputs come back): for one
  step the station reads Running, one craft less loaded. The game's own fill button leads to the same.
- **Water** (L3): a well (`Prop_NPC_Water_Well_a`) filled with water is in list B; a seedling bed's water recipe
  (1 charge) filled for 2 crafts pours 128 units (64 a charge). With the items for 2 crafts in and the water taken out,
  the station holds 0 complete crafts (water counts) and a fill up to 2 pours the water.
- **Stop and resume** (L4): stop now mid-cycle aborts it and the inputs are back (11 loaded while running, 12 after);
  stop after the current craft waits through the cycle (120 s here), writes the stop in the step between two cycles
  (state NoRecipe), the next cycle the game starts in that step is aborted at once, and nothing is lost (10 charcoal, one
  craft fewer loaded). A waiting stop is withdrawn by a stop now (`Canceled`, nothing written); a second one is
  `Unchanged`. The stop flag, the selection and 3 crafts' inputs came back after a SIGINT stop and a start.
- **Pause** (from the code, not run): `factory_station`'s resume from Paused sets `recipeStart := now -
  recipePauseDuration` (S `0x110f55`..`0x110fd8`): `recipePauseDuration` holds how long the cycle had run when it paused.
  So a paused cycle's progress is `pause / duration`, and the start moves at a resume (a stop waiting for the cycle's end
  must not take that for a new cycle).
- **Events** (L5): one `StationCraftedEvent` and one successful `ItemCraftedEvent` per cycle (the craft phase flag);
  the state events of Kiln's stop and resume name the mod; `StationPlacedEvent` for a placed kiln; a subscription made in
  `OnLoad` is accepted.
- **Select**: the charcoal recipe was selectable (the world has it); a blast furnace's recipe (0xf362723f) was refused
  with "no one on this server has unlocked" (the world and 0 players checked) and selected with `allowLocked`.
- **No player** (L6): all of the above ran with no player connected. **Use all options** (L7) was not run. **Cost** (L8):
  the managed reads of 14 stations took 0.6 ms (first call, JIT included); the native per-step diff was not timed apart.

---------------------------------------------------------------------------------------------------------------------

## 8. Station outputs as crafting stock (GitHub #68) [dis][cmp][sig]

Read 2026-11-17 on the same builds (server 26430253 = sigdb f0d824be, client 26430625 = sigdb 5d9ef2f1), static.

**How the fill takes (§3.1, in detail).** `AutoTransferRecipe` (S `0x198d50`, C `0x3f7ab0`) walks the resolved input
stacks (24 B each: `+0x10` item, `+0x14` count, read together as one qword at S `0x199240`; stride `0x18` at
S `0x1993ce`). Per input, after `need`: the player first, then every u32 entity of list A in order (loop S
`0x1992f4`..`0x199331`, C `0x3f8054`..), each through **`PayMaterial(out, tx, entity, item, missing)`** (S `0x1b0d60`,
C `0x411330`; the game's `removeItems` with no preferred slot, already `gems/PayMaterial`): it takes only stacks of
the asked item, from every slot of every link of that entity. Still missing: code 4 (a build constant check, S
`0x95b720`, returns false). Then `InventoryAddItems` (S `0x1b1f60`) into the station; water last through
`AutoTransferWater(tx, stock, station, units, player)` (S `0x1994a0`, C `0x3f8200`) from list B. `have[i]` reads the
station's inventory live, so writes earlier in the same transaction count. The caller commits on 0 and rolls the whole
transaction back on any code.

**So a source is per item.** A list-A entity gives only stacks of the item asked. A production station in the list is
safe **for item X** exactly when every stack of X in it is an Output: X an output of its selected or running recipe and
no input of either (all of a category input's options count). Its queued inputs (input items), its leftovers (neither)
and an output that is also an input (an input item) are then out of reach by construction. No hook is needed: Kiln
already passes its own `stock` to the function for `Stations.Fill` (§3.2).

**What Kiln does (since 0.2.0-dev.29.17, feature `stations.output_sources`).** Stations are opted in one by one
(`set_output_source`; managed: `Stations.SetOutputSource`, kept beside the save). In `kiln_items_stations`, at the
transaction, for a fill of n crafts on top of `loaded`:
1. the opted-in stations of the fill's base (the game's own `BaseAtPosition`), the target excluded, are read through the
   program's accessors: their recipes now, their inventories now; for each resolved input item, the stations that lend
   it and how much;
2. `short_i = need_i - (player's bag + chests after the reserves)` where positive (the player and the Magic chests pay
   first);
3. per input with a shortfall: `AutoTransferRecipe(tx, {lending stations of item i, in entity order}, [stack_i with count
   1], target, crafts = short_i, water 0, player 0)`: with count 1 the game's `loaded` is what the target holds of the
   item, so its `need` is exactly `short_i`;
4. the game's own call for the rest: the base's stock, the real inputs, `crafts = n + loaded - loaded'`, the water and the
   player: every input ends where a plain fill of n would put it, the player and the chests paying what they would have;
5. any code: rollback, nothing moved.
A fill whose resolved inputs repeat an item, a fill with `KILN_STATION_FILL_NO_OUTPUTS`, and every fill while nothing is
opted in run exactly as before (one plain call).

**Not chosen.** (a) A hook on `PayMaterial` or the list loop with a per-source filter: possible, but it is a shared
payment function (gem payment and others), and Kiln adds no hook on the fill path (§5). Collect-on-demand (move the
needed outputs into a Magic chest first, then fill): two commits, not all or nothing, and it needs a chest with room.

**The station screen's button.** The server's `player_crafting` handles it with the **player's** base (§3.1). Kiln does
not hook it; Kiln.Blocks follows the held client action (`PlayerClientActionEvent`, docs/kiln/services/player-state.md)
while a station is opted in, and runs a fill the player and the chests cannot cover but the outputs can as its own fill
(same recipe, choices and amount, the player's bag first, all or nothing), canceling the game's.
**Client [hyp]:** the client runs `AutoTransferRecipe` as a dry run on its own view of the base (C `0x2be77f`, rolled
back at C `0x2be78d`) and keeps a status per entry (C `0x2be816`..`0x2bea65`: 1 ok, 2..5 the game's codes). Whether a
"missing materials" status greys the button or stops the client from sending the action was not traced: a real-client
check. If it does, the button reaches the server only when the chests alone cover the fill, and station outputs at the
button need a client half (phase 2).

Symbols: `@stations/AutoTransferRecipe.take` (scoped to `stations/AutoTransferRecipe`, three patterns) captures
`stations/stack.item` = 0x10, `stations/stack.size` = 0x18 and `gems/PayMaterial` from the list loop, on both exes;
feature `stations.output_sources` (`server.d/stations.json`).

**Lab (2026-11-17, server 26430253, `mods/tests/station-outputs-test`, standard lab save, no player): 32 PASS, 0 FAIL,
and 3 PASS on the run after a SIGINT stop and a start.** A T1 forge's recipe (2 charcoal + 2 metal scraps) filled for 2
crafts took its 4 charcoal from an opted-in T1 charcoal kiln's outputs, the kiln's 36 queued logs stayed, the Magic chests'
charcoal (all reserved) was untouched, the ledger names the kiln's slot; with one craft's charcoal let through by the
reserve the chests gave 2 and the kiln 2; with the chests free the kiln gave nothing; an exact fill beyond both moved
nothing; the kiln's leftover (metal scraps) and a kiln with no recipe lent nothing; a kiln in a second base (an altar
placed 565 m away made the game's second base) was never used; a dry run and `UseStationOutputs = false` behaved as
documented; the opt-in came back after the restart. Scanning all 31 production station templates: **no recipe has an
item on both sides** (inputs and category options against outputs), so "an output that is also an input" is a guard
the game's data does not exercise today (host test only). `station-actions-test` on the same build: 64 PASS, 0 FAIL,
then 4 PASS, 0 FAIL after the restart.

## 9. Ordinary chests as a named fill source

Same call as §8, with one more source list. An ordinary chest is a placed storage container in the station's base that
the game's list A does not contain (no `InventoryCraftingStock`: not a Magic chest and not a refilling plant) and that
is not a production station. `PayMaterial` takes stacks of the asked item from whatever entity it is given, so Kiln
passes only those chests, and only for the amount the player and the Magic chests do not cover.

For a fill of n crafts the shares are, per input: the player's bag and the Magic chests (after reserves) first, then
the named chests, then opted-in station outputs. Inside the one transaction Kiln asks `AutoTransferRecipe` for the
chest share (source list = those chests, one stack of count 1, crafts = the share, player 0, water 0), then for the
output share as in §8, then for the remainder with the base's own stock, the real inputs, the water and the player.
Any code rolls the transaction back. A request that names no chests is the §8 fill. The station screen's button does
not name chests, so it is unchanged.

## 10. Station outputs in a craft

GitHub #68 step 2. Same two builds as §8: server **26430253** (`f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2`,
svn 1287369) and client **26430625** (`5d9ef2f140b8ed49e30dbee655841b89d08baec20f799a1817e171a18a850c77`). Image base
`0x140000000`; the numbers below are RVAs. Static only. `rules/InventoryPay` was also decompiled with Ghidra 12.1.2
through `rea` 6.7.0 (evidence `ev_750028c865fbccdf76fe105c80640bbd9eb1426d6efffb10c89024d4351839f8`, subject the server
sha256 above). The pseudocode is the decompiler's, not the game's source. The other sites are disassembly.

### 10.1 How InventoryPay builds its sources

A craft that is only short of materials does enter `InventoryPay`. `RecipeApply` returns 4 at S `0x19a208`, before any payment, when `0x91de90` returns 0. That function compares the recipe's workshop (`+0x24`) with the station's, then `requiresSheltered` (recipe `+0xa1` on this build: skipped when that byte is 0, otherwise the context byte `+0x1d` must be set and `+0x1c` must not be 0), then a level (recipe `+0x8c`), then three requirement words (recipe `+0x9c`, `+0x9d`, `+0x9e`: level, comfort, server progress). It does not read an inventory. A props mismatch returns 5 at S `0x19a228`, still before payment. Being short of an item is `InventoryPay`'s own 4, tested at S `0x19a293` and taken to the failure path at S `0x19a41a`. A player's or a request's craft (`handle_crafting_events`) sets the transaction's stock mode to 2 at S `0x8b226`: the owner, then the base's chests. `BaseStockForPlayer` then requires permission bit 0x02 (`CanAccessInventories`) at S `0x2081fb`; without it, it writes error 0x16 at S `0x208209` and Kiln leaves the payment to the game. A Magic chest is not a player, so a dedicated server with nobody joined cannot take from a station this way. A factory's start (`TryStart` S `0xe4330`) sets the inputs-only flag `0x08` at S `0xe43a7` and does not set that mode.

`rules/InventoryPay` is S `0x199c80` (returns at `0x199f05`). `rules/RecipeApply` (S `0x19a1d0`) calls it at S `0x19a287`;
the return address is `rules/RecipePaid` S `0x19a28c` (the sigdb pattern already on both builds; client `0x3f8fec`).
The fifth argument, when non-null, replaces the cost line's item. Each cost line is 0x18 bytes, item at +0x10, count
at +0x14. `tx+0xb1` is the stock mode.

Mode 0 pays the owner only, one `gems/PayMaterial` (S `0x1b0d60`) per line, and returns 4 when a line is short.
Any other mode calls `stations/BaseStockForPlayer` (S `0x2081c0`) **once**, then walks the lines. Mode 2 pays the
owner before the list; mode 1 pays the owner after it. The list is one list for every line. `PayMaterial` takes only
stacks of the asked item, but a station put on that shared list is asked for every line, so a line whose item is also
queued in the station would take those inputs. Fill's option (b) (a different entity list per item, passed into
`AutoTransferRecipe`) is not available here: `InventoryPay` builds the list itself and does not take one.

A take before the original call does not work either. The original still tries to take the full cost from the owner
and the chests. When they can pay, both takes would run. When they cannot, the original returns 4 and `CraftRecipe`
(S `0x210f00`) rolls the transaction back at S `0x2112d4` (`rules/InventoryRollback` S `0x1b8310`), which undoes the
earlier take, so the outputs would never actually pay.

Two more facts of the decompile. `InventoryPay` does not test `BaseStockForPlayer`'s error byte: the stock list is
zeroed before the call, so on 0x16 or 0x1f mode 2 pays from the owner alone. And the test it makes before returning 4
for a short line, `0x95b720`, is `xor al,al; ret` on this build (a compiled-out cheat): a shortfall is always 4.

The permission test inside `BaseStockForPlayer` is `0x163040(out, player, [bases+0x10])` (S `0x2081f6`): it answers 0
for any id outside 1..1023 (`cmp eax,0x3fe; ja`), otherwise the byte at `+0x9d` of the 0xa0-byte record `player-1` of
the table at `[bases+0x10]` (BasicPlayerInfos, `docs/re/gameplay.md`), whose first dword must be the id. So only a
logged-in player can pass it: a character spawned by a lab (an id from 1024 up) and a Magic chest never do.

**What Kiln does.** Only when the return address is `RecipePaid` (not `RecipePayAlt` S `0x19a9b0`, which `RecipeApply`
calls instead when `tx+0xb0` has bit 0x40, and not any other caller), on the server world's own transaction (§10.4), when
the mode is not 0, and `BaseStockForPlayer` succeeds (its error byte stays 0; 0x16 and 0x1f are the permission and the
"no base" the game already returns), and the opted-in outputs cover every line's shortfall after the owner and the
chests are counted in the game's order: Kiln pays the whole recipe with `PayMaterial` inside the same transaction (owner
and chests first, then the lending stations for that line's remainder) and returns 0 or 4 without calling the original.
A fault or a remainder returns 4 so `CraftRecipe` rolls back. When the outputs cannot cover, or a line's item repeats,
or the mode is 0, or nothing is opted in, or the stock list is longer than 4096 entries, the original runs unchanged.
The decision and the loop are `stations_logic.h` (`craft_order`, `craft_lenders`, `craft_pay`) and are host-tested.

Only players' crafts reach this path. A production station's own cycle (`factory_station` -> `TryStart` S `0xe4330`)
pays with stock mode 0, and a request whose operator is not a player fails the permission test above, so neither ever
takes from another station.

The station being crafted at is `CraftContext+4`, noted by kiln.gameplay's `CraftRecipe` hook for the duration of the
call (`native/services/gameplay/craft_phase.h`: a factory crafting into itself has operator == station, and player
entities are 1..1023). That hook group is installed on demand; kiln.stations asks for it (`request_hooks`,
`KILN_GAMEPLAY_HOOK_CRAFT`) once a station is opted in. Until it is in, only a non-player owner is left out. A player
crafts at a crafting station (a Crafting component), and only a station with a FactoryStation lends, so this matters
only for an entity that has both.

### 10.2 The client's craft status (GitHub #132 correction)

Static disassembly on client 26430625 (`5d9ef2f1`), compared with dedicated `f0d824be`; no game was run for this correction.
`rules/client.CraftableAmount` (C `0xb2ddd0`) is misnamed: it returns the **status** of a type-1 action query, not a
number of craftable items. The key includes the recipe, requested amount (1 or 10), and selections. Pending is 0;
ready is 1; refusals are 2/3/4/5. `player_ui` calls `RecipeApply` at C `0x2be03a`, preserves its result, and always
rolls the transaction back at C `0x2be048`. It caches a material refusal as 4 at C `0x2be854` and success as 1 at
C `0x2bea65`. The screen stores that status at entry `+8` (C `0xc79668`). Both Craft handlers require **exactly 1**
(C `0xd13192`, `0xd1327f`) before calling `UiQueueCraftRecipe` (C `0xd6e460`).

The previous Kiln bridge incorrectly returned `max(status, snapshotCrafts)`: a native material refusal 4 was not
changed for amount 1 (`4 < 1` is false), while a pending 0 or refusal 4 could become the invalid status 10 for amount
10. The old “count raised 0 -> 10” log therefore did not establish that Craft could send a request. A request blocked
here never reaches the hosted server's `CraftRecipe` or its `stations: player ...` line. This is a verified static
explanation of that symptom; the exact branch taken in the player's process was not observed.

The first material-only refusal is inside `InventoryPay`: C `0x3f8c42` / S `0x199ee2` return 4 when the line is still
short after the normal sources. This is separate from `RecipeApply`'s **pre-payment** 4 (C `0x3f8f68` / S `0x19a208`):
workshop, shelter, level, comfort and progress. Props refuse as 5. Neither eligibility branch is changed.

Kiln now scopes a thread-local preview context to the exact client call returning to C `0x2be03f`, pinned together
with its unconditional rollback. It verifies the recipe against the client registry, the client-storage kind 2,
the qualified transaction layout, read-only inventory accessor types and initialized inventory pages, item identities, and the actual resolved cost
lines. **Review correction:** the outer `PayMaterial` result alone is not enough. Its implementation
`InventoryRemoveItems` (C `0x411360`, S `0x1b0d90`) calls `InventoryPageFetch` (C `0x416400`, S `0x1b5fa0`).
The page loop silently skips a failed fetch, including missing-view error `0x1f`, then can return **error 0 with an
unpaid remainder**. Checking only the owner's inventory pages misses that failure on a native base-stock payer.

A passive fetch observer now runs only inside each actual `PayMaterial` call of the exact preview transaction. It
accepts the loop fetch return (C `0x41154b`, S `0x1b0f7b`) and preferred-page fetch return (C `0x411474`, S `0x1b0ea4`).
Each take must have at least one observed native fetch, and every participating fetch must return its out pointer,
error 0 and a nonnull inventory view. Missing observations, unknown call sites, error bytes or unreadable results mark
the take incomplete even when the outer take says error 0. Other transactions and fetches outside that take's scope
are passed through without affecting the preview. The observer never changes the result or repeats a take.

After the original `InventoryPay` returns 4, **observed, complete, error-free native takes** are required before its
existing kiln.rules hook may complement **only that preview's payment result**. The server-stock snapshot must also
cover every ingredient times the requested amount. Unknown/zero/duplicate costs, unknown modes/flags, absent stock
and failed reads stay vanilla. This observes the work the native payer actually performed; it does not claim a full
simulation of all native inventories or run an additional mutating payment.
`RecipeApply` still executes its normal water and output-space checks and computes the cached status itself.
The obsolete private count-adjust bridge is inert; ingredient display counts never authorize real payment.

This commits nothing: all native preview takes, including partial ones, are still rolled back. The snapshot comes from
`CraftStock.Build`: logged-in player, native permission/session-filtered `ExtraStock`, bag and authorized base stock.
The server recomputes it every 30 steps (about 1.2 s), sends changed totals, and clears it on menu/world lifecycle changes.
There is **no per-attempt freshness or reservation guarantee**; unchanged totals are not a new timestamped grant. A stale
snapshot can offer a request which the server then refuses. All actual authorization, live counts, bag-first payment, linked-source filtering, exact
shortfall limits and rollback remain in the unchanged server adapter (§10.1, §10.7). Hosted and dedicated authoritative
transactions cannot enter the preview context; the dedicated executable has no such call site.

The preview is not an exact simulated withdrawal from linked inventories. Native InventoryPay stops at its first short
line; subsequent bag deductions are therefore absent when RecipeApply continues into its water/output checks. Those
checks are **not bypassed**. Output-capacity evaluation can be conservatively false-negative when a later ingredient's
removal would free a bag slot in the real payment; only the real server transaction constructs the final paid inventory.
This limit remains, and a successful preview is never treated as authority to commit or dispense outputs.

Before installing the RecipeApply and passive observers, at preview entry, and immediately before complementing the
payment result, Kiln checks the full relevant code ranges with kiln.hooks' code-state API:

| Dependency | Client RVA | Checked bytes |
|---|---:|---:|
| RecipeApply | `0x3f8f30` | `0x275` |
| InventoryPay | `0x3f89e0` | `0x286` |
| PayMaterial wrapper | `0x411330` | `0x2b` |
| InventoryRemoveItems implementation | `0x411360` | `0x2dd` |
| InventoryPageFetch | `0x416400` | `0x10c` |
| BaseStockForPlayer | `0x46fa10` | `0x174` |
| InventoryRollback, including split tails and dispatch table | `0x418150` | `0x508` |
| Exact RecipeApply-to-rollback caller | `0x2be03a` | 19 |

The material implementation, fetch, base-stock and rollback extents are verified against the complete contiguous
`.pdata` fragments on both pinned releases, not just each function's first unwind fragment. Foreign/occupied or
unknown code in those ranges causes stand-down (logged at installation), never a patch over CBS. This includes
CBS changing BaseStockForPlayer even though Kiln does not hook that dependency. Original or Kiln-managed code can use
the shared detour. Native successes are always preserved; stand-down does not replace or suppress CBS's own native
payment. CBS itself was not downloaded or executed for this work. Address-specific tests cover every qualified range
at installation/use, unknown and foreign states, native success, and a dependency changed during the original call.

Regression evidence: `native/services/items/tests/craft_preview_test.cpp` (actual guarded bridge with fake native
functions) and `craft_preview_sigdb_test.py` (the two static executable copies, exact branches and changed-call
negative cases). The no-CBS Windows player's real game after this fix, and actual CBS 1.1.0 coexistence, remain unverified.

**Correction (GitHub #132, 0.2.0-dev.31.12; static, client 26430625 `5d9ef2f1`, objdump).** The bridge above never ran in a
real game: the crafting menu's type-1 query does not pay through `InventoryPay`. `player_ui`'s common setup starts the
descriptor word at `[rbp+0x780] = 0x0040` (C `0x2bd88b`); the recipe query sets its second byte, the stock mode, to 0 or 2
(C `0x2bdcec` / `0x2bdd01`) and one workshop (`0xfd28ca06`) ORs `0x08` into the first (C `0x2bdda4`); `InventoryInit`
copies both bytes to `tx+0xb0` / `tx+0xb1`. `RecipeApply` (C `0x3f8f30`) tests `tx+0xb0 & 0x40` at C `0x3f8fe3` and calls
**`RecipePayAlt`** (C `0x3f9700`, S `0x19a9b0`; .pdata `0x3f9700`..`0x3f98f4`, one fragment), returning to C `0x3f8ff3`
(`rules/RecipePaid` + 7); only the other branch calls `InventoryPay`. So every preview of every crafting menu was left
native at Kiln's descriptor check (`transaction-mode-or-flags`, recipe 0: the check runs before the recipe is read), which
is Fuzen777's clean 31.2 trace exactly. `RecipePayAlt(tx, costs, owner, multiplier, extra)`: mode 0 asks
`PayMaterial(out, tx, owner, item, multiplier x count)` per line (return C `0x3f9777`); any other mode first reads the
client's published stock map (`0x4709a0`: `lea rax,[rcx+0x1610]` on `tx+0x68`, the same map the ingredient rows count, `u16`
per item), skips a line it covers with no take, and asks `PayMaterial` for the rest (return C `0x3f98a3`). A remainder then
answers 4 (`0xeb3ab0` is `xor al,al; ret`, so a short line always refuses); it never calls `BaseStockForPlayer` or
`InventoryPay`. After it, `RecipeApply`'s water (the client's water map `0x4709b0`, or the owner) and output checks run as
before. Cross-Base Storage worked because it rewrites the published stock map with every base's Magic chests, so the
alternate payer's table covered the line natively.

Kiln (0.2.0-dev.31.12, building on work-23's unfinished start): the exact preview reads the descriptor first; `0x40` (with
or without `0x08`) names the RecipePayAlt route, `0` the InventoryPay route, anything else (0x10 skips payment) stays native.
kiln-items joins `RecipePayAlt` with a RawHook (kiln.rules' free crafting may already have a link there) and, after the
original returns 4 for that exact transaction, complements it as above, requiring the payer's return address
(`0x3f8ff3`) and every take to return to the one site its mode uses (`stations/client.CraftAltTakeRet` /
`CraftAltStockTakeRet`; `unrecognized-take-caller` otherwise). **The code-state check is per route:** RecipeApply,
PayMaterial, InventoryRemoveItems, InventoryPageFetch, InventoryRollback and the menu's call for every preview;
`RecipePayAlt` (0x1f4 bytes) and the stock-table getter (8 bytes) for the RecipePayAlt route; `InventoryPay` and
`BaseStockForPlayer` only for the InventoryPay route, which the pinned client never takes. So Building Tools 0.34.0's
redirected call inside InventoryPay (`0x3f8aee`) and Unified Tweaks Live's hook inside BaseStockForPlayer (`0x46fb65`),
which stood the whole preview down in 31.1 and 31.2, are off the route. A stand-down is checked again in the next client
world. The refusal line now names the payer, the descriptor byte and the mode. sigdb: `client.d/stations-craft-ui.json`;
`craft_preview_sigdb_test.py` checks the descriptor writes, the branch, the payer's two take sites, the table getter, the
.pdata extent and that a changed call disables the route; `craft_preview_test.cpp` runs the bridge on a fake RecipePayAlt
with the real control flow (both modes, a table that covers a line, wrong caller/take site, route mismatch, foreign code
on each route).

### 10.3 C7, the station screen's fill button

The dry run is inside client `FUN_1402b8430` (Ghidra 12.1.2, `rea` 6.7.0, evidence
`ev_44de36cc918e87a4a86e5e7d08c29ddc33e2debbd5cc1b00b49705dc3857b8b2`). It calls `AutoTransferRecipe`
(`FUN_1403f7ab0`) and then `FUN_140418150` before it breaks out of the case: the disassembly of that site is the call
at C `0x2be77f` and the rollback at C `0x2be78d`. The decompiler does not keep the result through the rest of the
function; the status stores below are from the disassembly. The call at C `0x2be77f` keeps the code in `edi` and always
rolls back at C `0x2be78d`. Status 0 is stored as 1 (ok) at C `0x2bea65`. Codes 2, 3, 4 and 5 are stored as themselves
(C `0x2be836`..`0x2be87e`); 4 is missing materials. The station screen reads that dword at C `0xbda1fd`. At
C `0xbda219` it compares with 1: the byte it then stores at `[rbp+0x520]` (C `0xbda22d`) is 1 only for status 1 (and
two other conditions, `r12b` and `[r13+0x218] == 0`), else 0. That byte is the enabled flag the widget calls get
(C `0xbdaa90`, `0xbdaac8`). At C `0xbdab1c` a status other than 1 selects the missing-materials style (`rbx+0x4b90`).

So the button is **disabled**, not only restyled, when the bag and the chests alone cannot cover the fill: a fill only
station outputs can complete cannot be pressed there, and the server's handling of that press (step 1) is reached only
if something else sends it. The hotkey poll C `0xd68510` does not receive the status; whether the fill hotkey still
sends the press was not shown by the static trace (C7 checks it).

**The query behind the buttons** (read 2026-11-19, same client build, disassembly only; `objdump`, no decompiler).
The status the screen reads is a cache of UI action queries at gameUi `+0x14f70` (the same cache
`rules/client.CraftableAmount` reads, §10.2). C `0xb2dbe0` is its generic query, `u32 UiActionStatus(gameUi*, u32 a,
u32 recipe, u32 item, u16 amount, u32 type, const RecipeInputCategorySelections*)` (`stations/client.UiActionStatus`):
it builds a 0x4c-byte key (`+0` type, `+4` `a` = the station, `+0x1c` recipe, `+0x20` item, `+0x24` amount, `+0x2c` the
32 bytes of selections), inserts it into the cache when it is new (value 0, "not run yet") and returns the value's
first dword. Thin wrappers exist for type 4 (C `0xb2dd20`) and type 3 (C `0xb2dd70`); the station screen calls the
generic function directly. The client program behind the dry runs is `FUN_1402b8430`: per cache entry it switches on
the type (jump table at C `0x2bee34`): 0 an inventory transfer (C `0x4150c0` / `0x4149b0`), 1 a craft
(`RecipeApply`, C `0x2be03a`, §10.4), 2 C `0x4117d0`, **3 a recipe fill** (`AutoTransferRecipe` at C `0x2be77f` with
`station = key+4`, `crafts = key+0x24`, the selections `key+0x2c`, the player from `0x9bf9e0`, the stock from the
client's `BaseStockForPlayer` at C `0x2bda75`), 4 an item fill (`AutoTransferItem`, C `0x2be462`), 5 a count
(`0x4709b0`, stored at value `+4`). Every dry run is rolled back; the status is then written as described above.

The production station's screen (the function at C `0xc743e0`) asks type 3 twice for the selected recipe: amount 1 at C
`0xc7451a` and amount 10 at C `0xc746f3`, with the station (`[rbx]`), the recipe (`[rsi+0x10]`) and the selections
copied from `[r14+8]`. Right after each call `cmp eax,1; sete` stores the enabled byte of the screen's action (ids 5 and
6). (The read at C `0xbda1fd` described above builds its key inline with type 0, an inventory transfer for the station
with `key+0x14` from its widget: it is not the fill's query.) So "+1" and "+10" are enabled exactly when their query
answers 1, and the query's caller cannot tell a Kiln answer
from the game's.

**What Kiln does (since 0.2.0-dev.29.19, feature `stations.fill_ui`, Kiln clients).** kiln-items hooks
`stations/client.UiActionStatus` (pattern plus `@stations/client.UiActionStatus.fill`, the "+1" call site, which pins the
argument order and the type value). The hook goes in at the first client-world Pre with the server's craft-stock
snapshot on (§10.5), never before. It calls the game's function first. Only a type-3 answer of 4 (missing materials)
while the snapshot is on is looked at: when the server's answer for the same station, recipe and selections (category
and option index of each, the padding bytes left out) covers at least `amount` more crafts, the hook returns 1;
otherwise it returns the game's value and notes the key so the client asks the server. The server's answer is
`Stations.Plan` for the press's own options (n more crafts, as many as the stock covers, up to 1000, the player's bag,
the chests, then the opted-in outputs; the station's room and the recipe's water are in the plan), behind the same
checks as the press (`StationsCore.SourceFillAllowed`). The logic is `stations_logic.h` (`fill_ui_status`,
`note_fill_ask`, `drop_answered`), host-tested.

**The input rows' "available" counts are not this query** (read 2026-11-19, the same build, disassembly only). They
are built on the client, from a cache of the base's Magic chests:
- `factory_station_info` (update C `0x336210`) is not the source: it fills a per-station record (state, recipe,
  selections, position, the crafts its inventory holds at `+0xac`) and reads no base stock.
- `cache_crafting_stock` (a client program, flags 5; update C `0x2b4150`; sys-read `LocalPlayerData`, sys-write
  `PlayerBasesSystemClient`) finds the local player's base (C `0x46f460`) and walks its crafting-stock containers 40 a
  frame, adding each container's stacks into a working hash map at `PlayerBasesSystemClient+0x1668` (item -> 8-byte value,
  `u16` count first; C `0x40c6d0`, find C `0x2b1bf0`, add C `0x46d7e0`). When a pass ends C `0x46f460` copies it to the
  published map at `+0x1610`.
- `player_ui` (C `0x2b8430`, sys-write `G38UiPlayData`) copies that published map (getter C `0x4709a0`) into
  `G38UiPlayData+0x2000` (C `0x2bd1ca`..`0x2bd2e9`) and sets `G38UiPlayData+0x828` when the player stands in a base
  (C `0x2bd366`).
- `UiAvailableCount` (C `0xd397d0`, `u32 (u32 item, inventory view*, stock map*)`) is the UI's count: the item's stacks
  in the player's inventory view (72-byte entries at `+0x2d8`, item `+8`, count `+0x10`) plus, when `+0x828` is set, the
  map's `u16`. The ingredient row widget C `0xbb1850` calls it twice: C `0xbb19b3` (return `0xbb19b8`) and C `0xbb2805`
  (return `0xbb280a`), with `G38UiPlayData+0x2000` as the map. It has 9 other callers (other screens).
- **Correction (GitHub #132, read 2026-11-23, same client build; Ghidra 12.1.2 headless decompile without auto-analysis,
  plus disassembly).** `0xbb1850` is not the crafting menu's row. The ingredient list C `0xbf4a90` (called by the crafting
  menu C `0xc771d0` at C `0xc7883a`, by the production station's screen C `0xbb5b20` at C `0xbb6562`, and by C `0xbb2b10`,
  `0xbf56c0`, `0xcb00c0`) switches on a style byte: style 4 (the station screen) draws each row with `0xbb1850`, whose
  Available count is shown ("9999+" from 10000) and also passed to the row's transfer control (C `0xccbc10`; a press queues
  UI command `0x39` {the screen's entity, item, u16 amount}: an item transfer into the station [inf], and the control's
  "+N" follows that count [inf]: a real-game report saw "+1" / "+10 000" with linked stock counted); every other style (the crafting menus) counts each
  row itself and draws it with C `0xbf1ea0`. The list's count: when its parameter's byte `+0x25` is set,
  `UiAvailableCount(item, the bag's view, G38UiPlayData+0x2000)` at C `0xbf5090` (return `0xbf5095`), else the bag's
  stacks inline; then **plus** `UiFixedSlotsCount(item, view)` (C `0xd39430`, call C `0xbf50e1`, `add edi,eax` at
  C `0xbf50ee`): 0 unless the view's `+0x290` is set, else the item's stacks over the view's fixed slot table (24 view
  indices at `+0x230`; positions 12, 14, 15, 17, 18, 19, 20 are left out). The row (C `0xbf1ea0`) shows that sum and, for a
  category ingredient, each option's count, again `UiAvailableCount` behind the same `+0x25` byte (C `0xbf3527`, return
  `0xbf352c`). The category option picker C `0xbf39b0` counts each option inline (the bag, the map's `u16` through
  C `0xd28e70` while `+0x828` is set, plus `UiFixedSlotsCount` at C `0xbf3f93`); its extra count text calls
  `UiAvailableCount` at C `0xbf423d` (return `0xbf4242`). So through 0.2.0-dev.30.7 the hook below raised only the station
  screen's rows: no crafting menu's ingredient rows (inventory, NPC, a crafting station's) showed the snapshot; only their
  craft count (`CraftableAmount`, §10.2) did. The flag `+0x25` comes from the menu's caller (`[param_1+8]+0x25` in
  C `0xc771d0`); a report from a real game (a crafting station's menu showing "the bag and the base") puts it on the
  `UiAvailableCount` side there. Static only.

**What Kiln does (feature `stations.stock_ui`, Kiln clients).** kiln-items hooks `stations/client.UiAvailableCount` on
demand (the same trigger as the fill query). Only for a call from the station screen's row (`stations/client.AvailableRowRet1`
/ `Ret2`, the game's return address as kiln.hooks reports it) or, since 0.2.0-dev.30.8, from the crafting menu's list
(`stations/client.AvailableListRet`) and its row's options (`stations/client.AvailableOptionRet`), and only for an item the
snapshot kiln-items holds names, it returns the greater of the game's count and the snapshot's (§10.5). Since
0.2.0-dev.30.8 Kiln.Blocks hands kiln-items, while a production station's screen is open (HUD FactoryCrafting), every
item of the server's snapshot with count 0 (`set_craft_stock` keeps such a row: the snapshot stays on for the fill query
above, and no count is raised): that screen's rows, the list's style-4 rows included, keep the game's counts, since their
transfer control moves only what the bag and list A hold (§10.7); since 0.2.0-dev.31.16 those rows count the server's
station rows instead (`kiln:craftstock` message 10: the bag, the Magic chests and the player's fill-opted links, per item the
links hold), because the transfer then takes from those links too (§10.9). The list adds the fixed
slots after the call, and the snapshot already holds those stacks (the server reads every link of the player, equipment
included), so at the list Kiln first calls `stations/client.UiFixedSlotsCount` with the same item and view (only when the
snapshot is above the game's count; a fault there leaves the game's count) and answers `max(game, snapshot - fixed)`: the
row shows `max(game + fixed, snapshot)`. The option picker's inline counts and its `0xbf423d` text stay the game's. Every
other call and item is the game's. The cache maps are never written. `stations_logic.h` `stock_ui_site`,
`stock_ui_site_count`, host-tested; `tests/stock_ui_native_test.cpp` runs the hook on fake functions; the sigdb rows are
checked on both exes by `tools/sigport/tests/test_stations_stock_ui.py` (client: each once, at these RVAs; server: none,
it has no game UI).

### 10.4 Which transaction, which thread

`InventoryPay` passes two words of the transaction to `BaseStockForPlayer`: `tx+0x68` (r8) and `tx+0x70` (r9). The
function uses r8 when it is set (kept in `rbp` at S `0x2081d7`) and r9 otherwise (`rdi`, S `0x208281`); with both null
it answers 0x12. Either way the word is a bases system: its `+0x10` is the permission table (S `0x2081e9` / `0x208291`), it
is `BaseAtPosition`'s system (S `0x20821b` / `0x2082b8`), and the base records it indexes (`+ index*0x130 + 0x30`,
S `0x208256` / `0x2082ef`) are `PlayerBasesSystemServer`'s (stride and offsets as `stations/bases.*`). The server's craft
transaction (`handle_crafting_events`) leaves `+0x68` zero and holds `PlayerBasesSystemServer` at `+0x70` **[lab]**
(dev29-sc2, kiln.log: "bases words 0 / 0x1246a0b20, the server's 0x1246a0b20" on a spawned character's craft; the first
build of this check read only `+0x68` and so left every player's craft to the game, §10.6). Kiln acts only when the word
`BaseStockForPlayer` reads is the server world's `PlayerBasesSystemServer` (read by the tick thread every Pre) and
kiln.items' `access_from_tx` accepts the transaction (its job's storage is the server world's: storage kind 0). A
factory's own craft (stock mode 0) never reaches this test.

The client's own `RecipeApply` dry run (C `0x2be03a`, inside the station screen's status function `FUN_1402b8430`, stock
mode 2 set at C `0x2bdd01`, rolled back at C `0x2be048`) also returns to `RecipePaid` (C `0x3f8fec`). On a hosting Kiln
client with a station opted in, the payment hook sees it. Its transaction is the client world's: its storage kind is 2
(and its bases words are not the server's), so the real payment adapter leaves it to the game (counter `other_world`). The client-only material-preview bridge
in §10.2 can complement that dry run after the native payment; it cannot use the server adapter to take items.

Thread. On a dedicated server the game's programs, `handle_crafting_events` among them, run on the tick thread
(docs/kiln/services/world.md). In a hosted game the server world's programs may run on a game worker thread. Kiln does
not rely on either: inside the payment it calls nothing of kiln.world (tick thread only) and not the core's singleton
lookup. It reads inventories through the transaction's own accessors (`ReadOther`, the view `PayMaterial` takes from),
a lender's `FactoryStation` and `CurrentTransform` through the game's `GetComponent` under an exception guard (as
`read_inventory` does), the base through `BaseAtPosition` on the transaction's bases system (what `BaseStockForPlayer`
itself calls), and the recipe registry and the server's bases system from what the tick thread cached. Writes from
other programs at the same time: the craft program holds write access to `Inventory` (its transaction's accessors);
`factory_station` and `player_crafting`, which write `FactoryStation`, also write inventories (a cycle's inputs and
outputs, a fill), and so does Kiln's `kiln_items_stations`, so the scheduler does not run them beside it; a station's
`CurrentTransform` does not change. That last part is reasoning from what those programs do, not a traced access list.

### 10.5 What a Kiln client is sent

While a Kiln client's crafting menu is open (any `ScreenKind.Crafting`: the player's own, a crafting station's, an NPC's,
a production station's screen), it says so on `kiln:craftstock` (`required: false`) every 5 s and when the menu closes.
For such a player, standing in a base where they have `CanAccessInventories` (the test the payment relies on), the
server sends per item the bag, the base's Magic chests and the opted-in stations' Outputs, only for the items of the
recipes that take an item those stations lend (native `craft_items`, an index of every recipe's inputs and category
options from the `RecipeRegistry` map, `content/recipes.*`), at most 1024 rows, again when a count changes (every 30
steps it looks). A vanilla client never says its menu opened and is sent nothing.

### 10.6 Lab (dev29-sc2)

**Lab (2026-11-17, server 26430253, `mods/tests/station-craft-test`, standard lab save, kiln.log build
`0.2.0-dev+20261118.0248a86`): 33 PASS, 0 FAIL [lab].** A lab has no logged-in player, so the server ran with
`KILN_STATIONS_CRAFT_LAB=1` and the payer was a spawned player character (`1_Player`, entity 2618: a bag and a place in the
base); the switch gives only such a character the base's stock list (§10.1: the game's permission test never answers
for it). Each case is read against Kiln's counters (`kiln_stations_craft_stats`), not only the inventories:
- opted out: `idle` for every payment, the craft refused, nothing moved;
- opted in, one craft of 200 charcoal: the bag's 20 and the base's chests' 57 first, then 123 from an opted-in T1
  charcoal kiln (`paid +1, from_outputs +123`; kiln.log `stations: a craft paid 123 from opted-in station outputs`); the
  kiln kept its 36 queued logs; the craft completed;
- not enough (the kiln's 30 left): `considered +1, short +1`, nothing moved (kiln.log `...cannot cover it (short by 170)`);
- the bag covers it: `covered +1`, the game's own payment took the bag's charcoal, the kiln was untouched;
- craft x3: `covered +2, paid +1, from_outputs +150` (each craft its own payment), three crafts made;
- a kiln opted in in a second base (an altar 565 m away): `considered +1, short +1`, that kiln untouched;
- a Magic chest as the payer: `no_access +1` (stock error 0x16), the switch does not apply to it, nothing moved.
The same run without the switch (control): every craft of the character `no_access`, `paid 0`, `lab 0`, the kiln
untouched. The first run of this branch had every player craft `other_world`: it read only `tx+0x68` (§10.4).
Factories' own cycles show as `mode0` (about 11 a step in that save). Not run in a lab: anything on a client (C7 to C15).


### 10.7 Player-registered sources (#125, since 0.2.0-dev.30.6)

The existing `RecipePaid` adapter also accepts player-bound container registrations. They deliberately skip §8's
source-base membership rule; they do not skip the game's per-player inventory permission. `source_access` rereads
`[serverBases+0x10] + (player-1)*0xa0`, requires the id at +0 to match and bit2 of +0x9d (`CanAccessInventories`) to be
set, then uses `inventory_matches(FIND_CONTAINERS)` (initialized InventorySetup, neither player nor NPC flags) and
rejects any `FactoryStation` component through the game's guarded `GetComponent`, never kiln.world in the payment hook.
Registration, counts and payment all enforce this: production stations lend only through #68's output-safe path,
since `PayMaterial` would otherwise consume queued inputs too. A fault determining the container type fails closed. At
registration it also captures the existing `players/InfoHandle` result (+4), and every snapshot rejects registrations
whose login handle changed. A reused player entity slot cannot inherit the previous login's links. The mod chooses
which containers it authorizes; this right is server-wide, not ownership of a source base.

Mode0 is eligible only for a real registered player, with the bag as its normal order. Its bases words may both be null
if `access_from_tx` still proves the server inventory storage; the cached server bases supplies only the player
permission check. In other modes a refused BaseStockForPlayer leaves the normal list empty, as the game does, and
station outputs are not added. Independently authorized registered sources can still cover the bag's shortfall.
Per-item registered sources are merged before or after the existing output lenders, deduplicated against the normal
order and one another, then passed through the same preflight and same-transaction `PayMaterial` loop. The only
intercepted return remains `RecipePaid`; a fault/remainder returns4 for CraftRecipe's rollback.

**Station crafts (GitHub #132, read 2026-11-23, server build, Ghidra decompile without auto-analysis).** A crafting
station's menu (the decoration table's: HUD StationBasedCrafting) crafts through the same `CraftRecipeEvent` as the
inventory: `handle_crafting_events` (S `0x8ad90`) builds the transaction with flags `0x04` (`| 0x20` when C `0x1b6e30`
says so) and stock mode 2, the station at `CraftContext+4`, then `CraftRecipe` (S `0x210f00`). `RecipeApply` (S
`0x19a1d0`) calls `RecipePayAlt` (S `0x19a9b0`, which subtracts a cached stock map from `tx+0x68` and pays the rest from
the owner) only for transaction flag `0x40`, which this handler never sets; so a station craft reaches `InventoryPay` and
returns to `RecipePaid` like any other, and the adapter above pays it from registered sources, the station itself left
out (`exclude`). `craft_payment_test.cpp` covers a station's craft context. What a station's menu lacked was the client's
ingredient counts (§10.3).

**A production station's screen** counts in its rows only what the row transfer moves (0.2.0-dev.30.8; since
0.2.0-dev.31.16 that includes the player's fill-opted links, §10.9): the row
transfer (UI command `0x39`; on the server most likely `AutoTransferAction`'s item branch, `AutoTransferItem` from the
player and base list A [inf]) is an item fill, which Kiln.Blocks leaves to the game (`SourceFill` passes `item != 0`), so
it takes neither registered sources nor opted-in outputs. A Kiln client names every item with none there (§10.3) and the
rows keep the game's counts, with a server of any version; the fill (§3.1, §8, §9) and its "+1" / "+10" still take
opted-in outputs through the fill query's answers, which do not read the rows. From 0.2.0-dev.29.19 through
0.2.0-dev.30.7 those rows counted opted-in outputs, and from 0.2.0-dev.30.6 registered sources too: the transfer control
then offered amounts whose press moved nothing of them (GitHub #132).

`source_stock(player, flags, ...)` runs on the server tick thread: flags0 returns filtered extra counts excluding
current base stock and opted-in outputs already counted by `kiln:craftstock`; `KILN_CRAFT_SOURCE_STOCK_ALL` returns
all filtered registered counts for consumers whose normal inventory view does not include that stock. Unknown flags
refuse. Counts recheck the live player login, permission and container setup; they never authorize a payment.

**Bag first (GitHub #132, dev.31 beta 1).** A hosting player reported both crafts paid in full from linked sources
("a craft paid 5 from additional crafting sources") while the bag held all five. The adapter asks `PayMaterial` for the
payer and every Magic chest before any lender, and `from` counts only lender takes, so a full-cost `from` means the
game's own `PayMaterial` took nothing from the bag in that transaction; and with the bag counted at the full cost the
craft is `covered` and never paid by Kiln at all (craft_payment_test, both bases words). What the transaction's view of
the bag held, and whether `PayMaterial` answered the bag with an error byte (treated, as `InventoryPay` does, as "next
source"), was not logged; the lines now say both. Lenders now pay at most the counted shortfall: a bag or chest that
pays less than counted rolls the craft back instead of the lenders paying the difference.

**Earlier diagnostic hypothesis (GitHub #132 follow-up, 0.2.0-dev.31; corrected by §10.2).** A 30.9 report from a hosting client: a
decoration table's menu counted linked stock (an ingredient 0 -> 770, the recipe 0 -> 10), but Craft made nothing and kiln.log
had no payment line. The earlier analysis assumed the returned value was a count. The corrected status trace is in §10.2. The craft action handler queues the craft only for an enabled action
(§10.2) and the UI dispatcher turns it into `UiCraftRecipeEvent` without checks (docs/re/creative-mode-146.md §3), so a
press is sent only when the entry holds status 1 (the old count raise did not ensure that); `RecipeApply` returns 4 *before* `InventoryPay` when `0x91de90` (workshop,
shelter, level, comfort, progress) refuses, and 5 for props (§10.1): such a refusal never reaches the adapter and looked
exactly like a payment refusal in the log. On a hosting client the client world's own `RecipeApply` dry runs reach the
adapter as well (their bases word is the client world's): they used up the six "left to the game" lines before the
server's craft came. Which branch Fuzen777's craft took is not known from the report. Kiln now (a) keeps those dry runs on
their own lines, (b) logs a server-world transaction whose accessors fail apart from them, and (c) brackets every player
craft through kiln.gameplay's `CraftRecipe` hook (`kiln_stations_set_craft_context` at the start, the new
`kiln_stations_craft_done(operator, station, recipe, code)` at the end, both not ABI) and writes one line saying whether
the payment was never asked, was left to the game and why, was short and by how much, or was paid. The count line now says
"by the server's crafting stock", since the snapshot does not record which source holds an item.

Verification here is offline: host registry/payment arithmetic, Windows tests of the actual guarded adapter with
fake transactions, managed snapshot/lifetime tests and symbol resolution on the two pinned binaries. No new game
client, server or lab was run for this change; the guided checks are in the public station service docs.

### 10.8 Fills from registered sources (built in 0.2.0-dev.31.12)

Built as sketched: a FILL with `KILN_STATION_FILL_LINKED` and a player takes, per input with a shortfall after the bag, the
Magic chests and named chests, from that player's links registered with `KILN_CRAFT_SOURCE_FILLS`, through
`AutoTransferRecipe(tx, {that item's links, in order}, [stack_i with count 1], station, crafts = share_i, water 0,
player 0)` inside the fill's one transaction. Layers per input (`stations_logic.h` `pay_shares`): named chests, links
ordered `BeforeStationOutputs`, opted-in outputs, links ordered `AfterStationOutputs`; the rest is the game's own call. The
links are revalidated in the program through the transaction's accessors (`source_access`: the player's id, login and
`CanAccessInventories`; an initialized inventory that is no person's), every container once per item, never the station,
the base's Magic chests, a named chest or a lending station. The press: Kiln.Blocks follows the held
`PlayerCraftingAction` fill when the pressing player has a fill-opted link and runs it as Kiln's fill when the plain fill
cannot and the linked one can; the fill query's answers (§10.3) use the same plan. The row transfer (UI command `0x39`,
`AutoTransferItem`) stays the game's. CharosDX's Cross-Base Storage takes the other road (it rewrites the stock list the
game's own queue fill reads, `player_crafting`'s AutomaticTransfer); Kiln keeps the game's list and adds a source list of
its own, so a link's filter and opt-in hold. Not run in a lab or a game.

### 10.9 The per-ingredient "+1" / "+n" (GitHub #132, 0.2.0-dev.31.16)

Static disassembly (objdump) of client 26430625 (`5d9ef2f1`) and dedicated 26430253 (`f0d824be`); no game was run. Report:
Fuzen777, 31.13, hosted, Streamlined Logistics linking 48 chests with `fills True`: the recipe's "+1" / "+10" filled from linked
chests ("20 from linked sources"), the rows' own "+1" / "+n" on the left stayed grey with "Available: 0".

**A separate path, not the recipe fill's.** The recipe's buttons are type-3 `UiActionStatus` queries (§10.3), a dry run of
`AutoTransferRecipe`, which Kiln answers from the server's `FillCrafts` (the fill it supplements with links). The rows are
not: the ingredient list's style 4 draws each row with C `0xbb1850`, whose count is `UiAvailableCount` at C `0xbb19b3` (the
bag's view plus, in a base, the published Magic-chest map, §10.3; `[rbp-0x38]`, "9999+" above `0x270f` at C `0xbb2086`). The
row hands that count to its transfer control C `0xccbc10` in a 0x48-byte block copied from `[rbp+0x0]` (item `+8`, count
`+0x10`). The control reads the item's max stack (`ItemInfo+0x14` through C `0xe94470`), sets its amount to
`min(count, max stack)` (with a slot already holding the item, `min(count, max stack - [rsi+0x14])`, C `0xccbcf2`..), and
its two buttons' enabled bytes (`[rbp-0x7f]`, `[rbp-0x7]`, C `0xccbd70`..`0xccbe17`) are set only when the count is not 0,
the slot test passes and the room is not 0. It returns the amount on a press (C `0xccc0e1`), 0 otherwise. **So the grey
buttons and "Available: 0" are one client-only number:** no query, no dry run, nothing asked of the server before the press.

**The press.** C `0xbb2340` -> on a non-zero amount the row queues UI command `0x39` {`+4` the screen's station, `+8` the
item, `+0xc` u16 amount} (C `0xbb241f`..`0xbb2472`). The UI dispatcher (C `0x85c7f0`, jump table `0x85e6e0` / `0x85e578`:
case `0x39` at C `0x85cdee`) turns it into the 0x40-byte transfer event (allocator C `0x82f840`, the same event as the
recipe fill's command `0x38` at C `0x85cd96` and the water fill's at C `0x85ce3d`): player `+8`, station `+0xc`, item `+0x14`,
amount `+0x18`. C `0x3b05fe`..`0x3b0773` copies it into `ClientPlayerInput+0xb4` as a `PlayerCraftingAction` with type 1
(AutomaticTransfer, `+0xf4 = 1`): recipe 0, the item, the amount. On the server `AutoTransferAction` (S `0xb2080`, C
`0x2c6b30`) takes the item branch when `[rsi+8]` is set (S `0xb23d2`: `cmp [rsi+8],r12d; je water`) and calls
**`AutoTransferItem(tx = rbp+0x3b0, &{list A, count}, item, station, u16 amount, player)`** (S `0xb2408` -> S `0x198bf0`; C
`0x2c6eb8` -> C `0x3f7950`); any code but 0 rolls the transaction back (S `0xb2438`..`0xb2443`), 0 commits. List A is the
player's base, from `BaseStockForPlayer`, behind `CanAccessInventories` (§3.1).

`AutoTransferItem` (S `0x198bf0`): with a player, `PayMaterial(out, tx, player, item, amount)` (S `0x198c44`) and the rest is
what it did not pay; then each u32 entity of the list in order (`mov r15,[r14]`; `cmp rbp,[r14+8]`; `add r15,4`), each
`PayMaterial` for the rest (S `0x198c82`); still missing: the compiled-out cheat test (S `0x95b720`) and **4**; then
`InventoryAddItems` into the station for the whole amount (S `0x1b1f60`, C `0x412460`): an error or a leftover is **2**.
So the move is exact and all or nothing, and the server decides it: a press the client counted wrong moves nothing.

**What Kiln does (0.2.0-dev.31.16, feature `stations.fill_items`).** sigdb `stations/AutoTransferItem` (its prologue) and
`@stations/AutoTransferAction.item` (the item branch's call, which pins the argument order; `item_fill_sigdb_test.py`).
- Server, the press: Kiln.Blocks follows the held `PlayerCraftingAction` as it follows the recipe fill (§10.8). For an item
  press of a player with a fill-opted link, after the recipe fill's own checks (`SourceFillAllowed`: CanAccessInventories,
  the player in the station's base), it plans the item fill without links; when that covers it, the game's press runs. When
  it does not and the plan with links does, it submits Kiln's item fill and then cancels the game's press.
- Native, the item fill (`KILN_STATION_FILL_ITEM`, `run_item_fill`): inside `kiln_items_stations`' transaction, the station's
  base list A, the bag and the links are counted through the transaction's accessors; the links are the player's
  `Fills` links, revalidated as the recipe fill does (`source_access`: id, login, CanAccessInventories; an initialized
  inventory that is no person's), never the station or a Magic chest of its base. The split is the recipe fill's
  arithmetic on a one-input fill of count 1 (`item_fill_plan` over `pay_shares`): the bag and the chests' share is the
  game's own call, the Before and then After links pay the rest. Then the game's own `AutoTransferItem` once per layer
  (each link group's share from that group's containers with player 0, then the rest from list A with the player), and
  any code rolls the whole transaction back. One rate-limited `stations:` line per outcome.
- Client, the count: the server sends `kiln:craftstock` message 10 per item the player's fill-opted links hold: bag +
  Magic chests of the player's base + those links (`source_stock(STOCK_FILLS)` leaves the base's Magic chests out since
  31.16, so a linked Magic chest is not counted twice). While a production station's screen is open the Kiln client hands
  those counts to the `stations.stock_ui` hook (`UiAvailableCount` at `AvailableRowRet1/2` answers the greater of the game's
  count and this); every other row keeps the game's count. A vanilla client never says hello and keeps the game's count; its
  press of an amount the bag and chests cannot cover never comes (the buttons stay grey), and if one does, the server's rule
  above applies.

Not verified without a game: that the station screen's rows are the style-4 list in every station (they are for the
screens read); that a raised count enables both buttons and that "+n" then offers `min(count, max stack)` as read; the
server taking the press from a joined Kiln client and a hosted one alike.

## 12. Registered sources for dyes (GitHub #125)

Static qualification on the same exact builds: server **26430253** (`f0d824be…`) and client **26430625** (`5d9ef2f1…`).
Owned headless Ghidra projects (no automatic analysis), instruction reads, exact-build uniqueness/call-edge/schema tests
and generator-refreshed sigdb records; no game or lab execution. Definitions: `server.d/dye-sources.json` and its client
extension. Since 0.2.0-dev.30.6; request by Fuzen777. RVAs below.

| Seam | Server | Client (hosted server code) |
| --- | --- | --- |
| `dyes/TintAction(action*, inventory_actions iterator*)` | `0x1a1230` | `0x3fff50` |
| return from `InventoryRemoveItems(out, tx, player, dye, preferredSlot, 1)` | `0x1a13e8` | `0x400108` |
| return from Tint's rollback | `0x1a1455` | `0x400175` |
| return from Tint's commit | `0x1a1490` | `0x4001b0` |
| slot-info resolver | `0x1445d0` | `0x3854f0` |

Tint first resolves both requested slots, looks up both items, rejects a non-tintable target (flag `0x10000`) or a dye
without `itemColorCombinationSetup.isSet` (`ItemInfo+0x748`), then opens its own inventory transaction. It does **not**
call InventoryPay/RecipePaid. Its removeItems result's error and remainder are tested before a palette write; either
failure branches to rollback. A successful payment writes/adds `ColorPaletteItem` (the existing component's store is
S `0x1a1482` / C `0x4001a2`) and commits. The commit's deliberate rollback guards are the existing `@items/CommitGuards`:
flags `tx+0xb0 & 0x40`, or no InventoryEmptyState accessor at `tx+0xa0` with touched inventories.

The sparse setup is captured on both builds. Iterator `+0x160` is InventorySetup, `+0x148` Inventory, `+0x190`
SlotSelection, `+0x168` ItemState, `+0x178` OwnerRelationship and `+0x158` InventoryEmptyState. Iterator `+0xc8` is copied
to setup `+0xd8`, hence **tx `+0x70` has the server bases**; Fuel is absent. Kiln validates the used accessor type indices
against this job storage's component map on every request, requires server storage kind0 **and the active server
world's exact storage identity** cached at Pre, and never relies on the full-accessor cache. removeItems reads the named holder's setup through tx[3], expands its links, writes each page
through tx[4] in the same job, and queues type1 operations keyed by page/slot in tx+0xf0. Thus a registered chest is
reachable through this transaction's own accessors, and the game's rollback restores its debits.

Client UI: screen `0xc0ace0` calls submenu `0xc09740`; palette builder `0xd14ce0` fills the 72-byte views at tint state
`+0x90b80` (count `+0x90b88`) from the player/station models plus unlocked palettes. **Order (GitHub #132, decompiled
2026-11-23):** the builder clears the list, adds the two models' stacks (C `0xd24a50` twice), then each unlocked palette it
does not hold yet with count 0 (setting byte `+0x3c` from C `0xd64250`, also on a held one), sorts the whole list by the
count at `+0x10`, most first (C `0xb0aed0`), sorts the part after the held stacks by byte `+0x3c`, highest first
(C `0xb0b620`), and inserts the empty "no dye" row (item 0) at the front. Kiln's grid hook merges the linked dye counts
after that build (a held row's count grows, a missing dye is appended), so since 0.2.0-dev.30.8 it sorts the rows after the
leading item-0 row again by count, most first, stable: a linked dye joins the held ones by its count, equal counts and the
count-0 rows keep the game's order (`dye_sources_logic.h` `order_with_sources`). Palette screen `0xc0b930` overwrites
row counts from the two models' Generic-index views at `0xc0bf14` (eax count, rbx row, r15d item). Its selected palette's
detail has a separate sum at `0xc0c302` (eax, r15d). Confirm `0xb2ceb0` emits command `0x6e`, copying the target key at
command+4 and the selected palette key at command+0xc. The input writer preserves both keys unchanged.

**No chest key is sent.** A server-announced source palette uses `{entityId = dye ItemId, slotIndex = 0xff}`. Both
actual input wire schemas preserve that index as a full u8: C root `0x1fe0cf0` → ItemAction `0x1fdfdd0` → InventorySlotId
`0x1fde3f8`; S root `0x14b0bb0` → ItemAction `0x14afc90` → InventorySlotId `0x14ae2b8`. The slotIndex field is kind4,
fixed1, offset4, not a bitfield; client serializer `0x11f6420` writes eight bits, server decoder `0x9a77e0` reads eight
without clamping. Kiln also validates the live schema path before enabling the hooks. Without the entry hook, the
normal slot-info resolver refuses index255 (it requires <8), not a real source slot.

The entry re-resolves that requested dye from the bag first, then this player's generation-bound, filtered,
access-checked sources, using the iterator's own accessors, and calls the original on a shadow action. Only that call's
scoped removeItems hook may borrow: it rechecks the live source registry, permission, sparse transaction identity and
commit guards, pays the bag first, and verifies a source take **before returning success to Tint**. Every hook-owned
fault, error or remainder returns a payment failure, so Tint rolls back before the palette is written. The ordinary
unmarked action is unchanged and clears any outer Tint's thread-local payment route for the duration of a nested call.
Vanilla grid callbacks likewise clear any outer palette's source-count scope, restoring it when the nested call returns.
Before installing server or client hooks, Kiln compares qualified prologues/count instructions through `read_original`
and refuses foreign or unreadable live code through `code_state`; registry-owned detours remain valid. The entry is
installed only after the payment link, and an entry installation failure revokes that otherwise unused link.
Windows fake-transaction tests cover entry/debit revocation, player generations, wrong storage/missing accessors,
commit guards, nested routing, code qualification, partial installation and an injected SEH fault after a source debit;
native palette tests cover the raw grid callback and both mids. These are not real-client execution evidence.

## 13. Block placements from registered sources (GitHub #132, since 0.2.0-dev.31.12)

Static, server 26430253 (`f0d824be…`) and client 26430625 (`5d9ef2f1…`), objdump; sigdb `server.d/build-sources.json`,
checked by `native/services/items/tests/build_sources_sigdb_test.py` on both copies. RVAs: server / client.

| Seam | Server | Client |
| --- | --- | --- |
| the cursor's material check (under `rules/CursorFlags`' bit 12) | `0x99750` | `0x28ab60` |
| its blueprint branch's stock-count call, return (`build/CursorStockRet`) | `0x99a50` | `0x28ae3c` |
| the stock count (`build/StockCount`) | `0x209210` | `0x4709c0` |
| `rules/ApplyMaterials` | `0x22d480` | `0x495f20` |
| its take's `InventoryPay` call, return (`build/ApplyMaterialsPaid`) | `0x22d562` | `0x496002` |
| `player_building_blueprints`' `ApplyMaterials` call, return (`build/BlueprintApplyRet`) | `0xc4b50` | `0x2d8bb0` |

**The check.** For an item with a voxel object (a blueprint shape) the material check counts the query's material
(`[[rsi+0x68]]`) in the player's inventories (`InventoryItemCount`, the count at `[rsp+0x58]` -> `ebx`), then calls the
stock count with the cursor's position -> `eax`, and answers "missing" when `eax + ebx < ebp` (`ebp`: the blocks the
placement fills, from `0x9333b0`). The stock count reads a client world's published stock map (`bases + 0x1610`, the
crafting menus' map, docs 10.3) or, on a server world, the base's stock list at the position (`0x208c60`). The function's
register use differs between the executables (the server keeps the program context in r14, the client in rbx, which the
bag count then overwrites), but in both `rdi` is the cursor context, whose first qword is the program context (its job's
storage kind tells the world). A terraformer's material (the other branch, `add eax,ebx; je`) is not hooked.

**The payment.** `player_building_blueprints` (S `0xc4410`) opens the placement's transaction from a setup whose stock-mode
byte (`[rbp+0x291]`) is `sete`: 1 (the base's stock list first, then the owner) or 0 (the owner alone); `InventoryInit`
copies it to `tx+0xb1`. It calls `ApplyMaterials({mode 0, tx}, list, take = r12b)`, which in mode 0 calls
`InventoryPay(tx, list, [tx+0xe0] the operator, 1, 0)`. kiln.rules' InventoryPay hook (free crafting, the storage upgrade,
kiln.stations' adapter) sees it with the return address `build/ApplyMaterialsPaid`.

**What Kiln does.** A mid hook at `build/CursorStockRet` (on demand: a server once a link has `KILN_CRAFT_SOURCE_BUILDING`, a
client once its server's building snapshot is on) adds the player's building links to `eax` when `eax + ebx < ebp`: a server
world from the server's count of those links (cached on the tick thread every 30 steps and on a registration change, so the
check never calls kiln.world), a client world from the snapshot (`set_build_stock`). Both leave out the links to the
Magic chests of the base the player stands in (`player_base_chests`): `eax` holds those already, and the payment's base
list pays them and keeps them out of the links. A RawHook on `ApplyMaterials` sets a
thread-local scope only for a take whose caller is `build/BlueprintApplyRet`; inside it, the craft adapter (§10.1, §10.7)
accepts `build/ApplyMaterialsPaid` and pays with the building links only (no station outputs): the owner and the base list
in the game's order first, then the links, all or nothing; any fault or remainder returns 4, `ApplyMaterials` answers false
and the placement is not made.

**The blueprint transaction's accessors (GitHub #148).** `player_building_blueprints` builds its setup at `rbp+0x290`
(S `0xc49d2`..`0xc4aef`, C `0x2d8a32`..`0x2d8b4f`) and stores `+0x60`..`+0xbf` from zeroed `xmm0`/`xmm1` (the six
`movdqa` at S `0xc4a57` / C `0x2d8ab7`): `+0x70`, the Fuel accessor, stays 0, and `InventoryTxInit` (S `0x1b9300`, C
`0x419140`; `mov rax,[rdx+0x70]; mov [rcx+0x38],rax` at + `0x6f`) copies it to `tx+0x38`, the word kiln.items'
`access_from_tx` reads as Fuel (`w[7]`). That check needs every accessor, so it refused every placement (`the server
world's craft transaction did not check out (its accessors) ... (mode 1, payer 1)`, five times in Fuzen777's 31.13 log,
once per try) and the game paid from the bag and the base alone. A placement's payment reads only InventorySetup and
Inventory (the link counts, the setup flags) and takes through `PayMaterial` on the transaction itself: since #148 it is
checked by those two (`access_from_tx_reads`, as a lock's payment), crafts keep the full check.
`build_sources_sigdb_test.py` checks the zeroed slot, the copy and the word on both executables.

**Building Tools 0.34.0 (#148).** Its `pay_hook` (the blueprint call's redirect) calls `ApplyMaterials` with the program's
own cost context. kiln_call_site.hpp's walk of the stack to the game's call did not reach it in Fuzen777's hosted game
(`a materials take from BuildingTools.dll could not be traced`; why is not known from the static copies: BuildingTools'
own unwind data was not read). The fallback: the program's context is `rsp+0x50` at its call (`lea rcx,[rsp+0x50]`,
pinned by `build/BlueprintApplyRet`), so that call's return slot is `ctx-0x58`. When that slot, on the thread's stack
above the hook, still holds `build/BlueprintApplyRet`, the context is mode 0 and its transaction lies in the same frame
(`ctx+0x5d0` in the game: `rbp+0x520`, `rbp = rsp+0x100`), the take is that call's, whoever made it. Foreign code on the cursor's call (98 bytes before and 6 after the return), the stock
count's entry, ApplyMaterials' entry, its take's call or the blueprint program's call stands the building half down with
one line. A fault inside the cursor hook does the same.

Cross-Base Storage (CharosDX, MIT; read as source, commit `2bb3978`, written against build 1273043) covers building by
redirecting the stock-list calls of the building tool and the material consumption, the build menu's count rebuild and
the two base-count calls of the build check (the same `F3 0F 11 44 24 44 E8 .. 03 C3` sites as `build/CursorStockRet`), and
optionally takes from the inventory first by removing `InventoryPay`'s mode-2 test. Kiln changes none of the game's lists:
it counts and pays links on top. Its own client prediction of the base counts is therefore not needed.

**The build menu's material counts** (client only, `client.d/build-ui.json`, feature `crafting.build_ui`). The material
selector's gatherer `0x40e5d0` (`void(selectorState, access, bool currentOnly)`, docs/re/building.md 1.2.2) walks seven
groups of `0x648` bytes (`imul rcx,rax,0x648` at `0x40ee37`; the current-group byte after them at `+0x2bf8`), one per
material pool kind (`0xe`..`0x14` -> groups 0, 1, 6, 2, 3, 4, 5; 7 for none). Each group's byte `+0x638` is its sort mode,
the material catalogue's tab (Building Tools 0.34.0's source names mode 0 "backpack and magic chests"; the code shows the
rest): **0** Backpack & Magic Chests, **1** Last Used, **2** by amount (sorted by `0x40be40` after the counts), **3** All
Materials / item sets, **4** Favorites. Per rebuilt group: modes 1, 2 and 4 drop their rows (mode 0 when it is not the
current group, groups 0 and 1 by two access flags; mode 3 never), every row's stock is zeroed (`0x40e7bd`..`0x40e98d`), and **only modes 1 and 4 get a zero row
per listed material** (`0x40e6ab`: `[access+0x58]` lists, `0x40e750` loop, `0x40c010` with the no-container key at
`0x152d040` and pool-row 1). Then the bag's stacks and, per pool material, the client's published base stock map's `u16`
(`0x40ee46`, return `0x40ee4b`). `0x40c010` finds a row by item and adds the count at `+0x10` of a 24-byte row
(`0x40c0f4`), else appends one, except in modes 1 and 4 for anything but a zero row (`0x40c04b`); a full group grows by its
callback at `+0x18`, and with none the game stops (`0x40c11e`).

So the doc's earlier claim ("a material only the links hold still has its row") was wrong: such a material has a row only
in Last Used and Favorites. 31.12 to 31.14 raised the rows after the gatherer, and Fuzen777 (#148, 31.13) saw exactly that:
the hammer's list empty, `0 / needed` under the cursor, 0 in Backpack & Magic Chests, All Materials and the amount sort,
the right counts in Last Used and Favorites, and after a placement try that one material listed (inferred, not traced to the end: player_ui's
selection add `0x40c150`, C `0x2bce24`, gives the used material a row in the current group, and a mode 0 current group
keeps its rows).

Kiln since #148 adds the links where the game adds the base's stock: a mid hook at `0x40ee4b` (`build/client.StockRowsRet`,
7 bytes: `mov eax,[r15+0xc]; inc r14`), the stock loop's step to the next material, reached on every path of the loop (the
five not-found jumps and the add) and only for a group the call rebuilds; `rsi` the state, `r15` the pool (materials: i32
offset `+8` from `+8`, count `+0xc`), `r14` the index, `r12b` the group. It calls `0x40c010` itself with `{item, linked
count, 0}`, the no-container key and pool-row 0, once per group and call: so the row is appended where the base's stock
would append it (never in Last Used and Favorites), the amount sort sees the sum, and the next rebuild zeroes it again
(nothing to take back). A group without room and without a grow callback is skipped. The hook's call is opened and closed
by the two mid hooks after the prologue and at the epilogue (kiln-tools' picker uses the same two sites; an entry hook until
0.2.0-dev.31.13), paired by rsp, which is the same at all three. A row or pool that cannot be read stands the menu's half
down (a fault guard). The step is off Building Tools' checked bytes (its call at + `0x876` ends at + `0x87b`).

Not done: props (`place_prop`'s two `ApplyMaterials` calls), undo, dismantle and terraforming. Not run in a lab or a game.
---------------------------------------------------------------------------------------------------------------------

## 14. A game client's own station cache (ClientFactoryInfo, since 0.2.0-dev.31.13)

What `ClientStations` / `kiln.stations.client` read on a game client, joined or hosting: the client world's
`ClientFactoryInfo`, the station cache the game's own station HUD and production-station screen read. Read on client
Steam 26430625 (exe `5d9ef2f1…`) and dedicated server Steam 26430253 (`f0d824be…`), static copies, never run; started
from XHL's reference ("Read the client workshop cache, recipe and production clock", Easy HUD), every claim checked
again here. Symbols: `sigdb/symbols/client.d/stations-client-view.json` (feature `stations.client_view`); test
`native/services/items/tests/client_stations_sigdb_test.py` (72 checks); logic `client_stations_logic.h` (host test
`client_stations_test.cpp`).

### 14.1 The resource [dis][sig]
- `player_ui` (C `0x2b8430`) lists `ClientFactoryInfo` in its sys-reads (program catalog), so it is a registered system
  resource of the client world: Kiln gets it with the core's `singleton(world, "ClientFactoryInfo")`. Nothing in the
  catalog writes it: the network decoder below does.
- Constructor `stations/client.FactoryInfoInit` (C `0x3bf5d0`): allocates `0x30f00` bytes; rows pointer `+0x00` = the
  inline rows at `+0x28`, row count `+0x08` (u64, 0), capacity `+0x10` = 1000; the base groups' vector pointer
  `+0x30d68` = inline groups at `+0x30d90`, group count `+0x30d70`, capacity `+0x30d78` = 16; a byte at `+0x30ef0`.
  1000 rows x `0xc8` end exactly at `+0x30d68`.
- A group is 20 bytes: the base's 16-byte GUID, u16 first row, u16 row count (`stations/client.FactoryInfoGroups`,
  C `0xe869b0`, the stock getter XHL names; it returns the base's row span). Rows are contiguous per group and kept
  sorted by entity: `stations/client.FactoryInfoFind` (C `0x3bf700`) is `Row* (info, const Guid* base, u32 entity)`, a
  binary search on the u32 at row `+0`. Kiln walks rows `[0, count)` directly (every row of every group) and reads
  the base GUID each row carries (`+0xb0`).
- Its one caller, `player_ui` at C `0x2bd498`, looks up the base GUID at `G38UiPlayData+0x82c` (valid flag `+0x83c`,
  copied from the client's base records when the player stands in a base) with the entity at `+0x2c50`
  (`ui/play.target`, the screen's entity) and stores the row pointer at `G38UiPlayData+0x2cc0` (what the station
  screen and its recipe picker read: content.md "a station's screens").

### 14.2 How rows arrive [dis][cmp]
- Network handler C `0x8760a3` calls the decoder C `0x3bf7f0`: a count byte, a u16 (stored at row `+0xc0`), then per
  entry the u32 entity, a u8 operation (1 removes the row), three u16 positions (x 0.25: constant C `0x13d290c`), a
  lookup of the base at that position in the client's own base list (C `0x46fd00`; **no base found: the entry is
  skipped**, C `0x3bfbd4`), the group insert/update/remove, and the row's fields (`stations/client.FactoryInfoRead`,
  C `0x3c0920`).
- The server half: `factory_station_info` (C `0x336210`, flags 4; in the dedicated server's catalog too) fills `ServerFactoryInfo` rows from
  `Crafting`, `FactoryStation`, `CurrentTransform`, `UiOffsets`, `Fuel` (attributes `0x05b0bf75` Fuel, `0xa00e2601`
  Fuel_Max) and `RotationalEnergyConsumer`; its encoder (C `0x4a468c`, S `0x23d1e3`..`0x23dccf`) writes the fields in the
  order the decoder reads them. `FactoryInfoInvalidate` (§2.1) re-marks a station for sending when its state byte changes
  and before any player station action. **Which stations go to which player (a radius, the player's base) was not
  traced**, nor whether a change of only the loaded crafts or the water re-sends a row at once.

### 14.3 A row (0xc8 bytes) [dis][sig][cmp]

| Row | Width | What | Server row (encoder) |
|---|---|---|---|
| `+0x00` | u32 | the station entity (the server's id) | `+0x00` |
| `+0x04` | u32 | `Crafting.workshopId` | `+0x08` |
| `+0x08` | u32 | the selected recipe (`FactoryStation.nextRecipe`) | `+0x0c` |
| `+0x0c` | 3 x f32 | position, 0.25 m steps | `+0x98` (from `CurrentTransform`) |
| `+0x18` | 4 x f32 | rotation | `+0x88` |
| `+0x28` | i64 | `recipeStart` (ns, the world clock) | `+0x30` |
| `+0x30` / `+0x34` | 4 x {u32, u8} (stride 8) | the selected recipe's category choices | `+0x10`.. |
| `+0x78` | 10 x f32 | `UiOffsets.interactionPromptOffset` | `+0x60` |
| `+0xa0` | u32 | complete crafts still loaded after the cycle in progress (the server's count, capped by water charges) | `+0xac` |
| `+0xa4` | u16 | water capacity (Fuel_Max) | `+0x06` |
| `+0xa6` | u8 | `FactoryStation.state` (0..5) | `+0x04` |
| `+0xa7` | u8 | `missingRequirements` | `+0x05` |
| `+0xa8` | u16 | water (Fuel) | `+0xa4` |
| `+0xaa` | u8 | use all ingredient options | `+0xa6` |
| `+0xab` | u8 | has a `RotationalEnergyConsumer` | `+0xa7` |
| `+0xac` | f32 | its search radius | `+0xa8` |
| `+0xb0` | 16 | the base GUID (the group key) | (the decoder's base lookup) |
| `+0xc0` | u16 | the message's u16 | |

Not in a row: `runningRecipe`, `recipePauseDuration`, `isStoppedByPlayer` (only the state), Kiln's own "stop after the
current craft". XHL's offsets (+0 entity, +8 recipe, +160 crafts, +166 state 0..5) are confirmed; their "batch count" is
the loaded crafts after the cycle in progress.

### 14.4 The time [dis][sig]
- `stations/client.FactoryRemaining` (C `0xb2d6e0`) is `i64 (const RecipeInfo*, const Row*, i64 now, float speed)`:
  no recipe answers 0; one craft = `(i64)((double)(float(double(recipe+0x38) * 1e-9) * speed) * 1e9)` (constants C
  `0x13d2670`, `0x13d6b90`); total = craft x row `+0xa0` (kept at an extreme when craft is `INT64_MAX`/`MIN`), + craft;
  elapsed = now - row `+0x28` with the same saturation; result = total - elapsed. So it is the time until every loaded
  craft is done; with 0 crafts loaded, the cycle in progress. `client_stations_logic.h` `game_remaining` follows every
  branch (host test).
- Its two callers, the HUD (C `0xd9db4e`, `@stations/client.HudRemaining`) and the station screen (C `0xbb8595`,
  `@stations/client.ScreenRemaining`), call it only when the row's state is 4, Running (`C 0xd9dac4`, `C 0xbb8511`), with
  `now = [G38UiPlayData + 0]` and `speed = FactorySpeed(G38UiPlayData+0x4667d8)` (the BalancingRegistry), 1.0 when that
  pointer is null; the recipe from `content/RecipeFind` on `G38UiPlayData+0x4667c0`. Kiln does the same with the client
  world's `BalancingRegistry` and `g38_items::RecipeRegistry` singletons and the game's `FactorySpeed` (read-only), on the
  client world's thread after its step.
- The client's speed factor is its own copy of the setting; that a server's live change of `factoryProductionSpeedFactor`
  reaches it was not checked.

### 14.5 Outputs
The finished stacks: Kiln reads the station's inventory in the client world with kiln.items' reader (the same code as on
the server, `read_inventory` on the client world) every 15th copy, and keeps the stacks of an item that is an output of
the selected recipe and no input of it (a category input's options count as inputs), as XHL's reference sums
`ItemStack` counts of the product. That the client world holds a station's inventory outside its open screen was not seen
in a running game: without one, `Outputs` is null.

### 14.6 Sides and threads
The decoder runs on the client; a hosting client has the cache too (it is a client of its own server). Kiln copies the
rows after the client world's step (Post, no job running), at most 1000 rows, only while some caller read in the last 5 s;
readers on any thread get that copy. No hook, no write, no message.

### 14.7 Not verified without a running game
- that the cache holds the stations a joined player sees (and how far around them), and how soon a change arrives;
- that `ClientFactoryInfo` is the singleton's registered name in a live client world (the catalog's name; the core's
  lookup is by that name);
- the remaining time against the game's own HUD (the arithmetic is the game's, statically);
- the station inventories in the client world (outputs), and the server's speed setting reaching the client;
- the copy's cost with many stations (at most 1000 rows of 200 bytes and 1 recipe lookup per recipe per copy).

## 15. The stations registry (dev.32) [data][refl][kiln]

What the `stations` registry and `recipes.station` rest on (server 26430253 `f0d824be…`, client 26430625 `5d9ef2f1…`;
reflection of both exes, data decoded with `tools/kfc` from the server's archive; schema/registry/stations.json,
regmap/common/stations.json).

- **Entries.** `keen::WorkshopRegistryResource` (one resource, `3baba1c2-…`, 5,328 bytes) holds three arrays: `npcs` (0
  elements on this build), `workshops` (51) and `craftingProps` (51). A Kiln station is one element of `workshops`
  (`keen::WorkshopResource`, 56 bytes: `isNpc` `+0x00`, `ignoreInJournalUi` `+0x01`, `workshopGuid` `+0x04`, `workshopId`
  `+0x14` (a `HashKey32`), `name` `+0x18` (a loca tag), `item` `+0x1c`, `itemRef` `+0x20`, the upgrade masks `+0x30..`).
  The layout is the same on both exes (`regmap_check.py --exe`: every row resolves on both). 11 of the 51 are villagers'
  (`isNpc`); 7 have no item (the flame altar's two, the farm bed's and four others).
- **Ids.** A workshop has no debug name. Its `item` names the item that builds it (a villager's: the villager's item), so a
  station takes that item's Kiln id (regmap entries `"key": "items", "keyMember": "item"`): `game:craftingstation_workbench`
  (`0x4da88f73`), `game:prop_production_t1_forge` (`0x8dca2c49`), `game:npc_crafter_blacksmith01` (`0x41bf48ce`); one
  without an item is `game:station_<workshopId>` (`game:station_b326e042`, the altar's). No two stations share an item on
  this build. Its 32-bit id stays `workshopId`: recipes name a station by it.
- **When the game reads it.** The workshop registry (`workshop::WorkshopRegistry`) copies the records into its own hash
  map (inline 0x38-byte records, docs/re/content.md section 7; its map is sized from the resource at load,
  docs/re/building.md 2.4): read once at start. Both fields are read-only and `restart`.
- **`recipes.station`.** `keen::RecipeInfo.workshopId` (`+0x24`) and `workshopGuid` (`+0x14`). Of the server's 2,542
  recipes, 2,485 name one of 45 workshops by `workshopId`; every recipe's `workshopGuid` is all zeros on this build, so
  the reference resolves by the 32-bit id; 57 recipes name none (the player's own crafting): `null`. RecipeFinishLoading
  sorts recipes into per-workshop lists once (docs/re/content.md section 2): read once, read-only, `restart`.
- **Templates and stations.** 117 templates carry `CraftingInteraction` and 119 `Crafting`; they name 46 workshops
  (`workshopId 0` on 28 of them: the player's own menu). A workshop may have many templates (the workbench 17, a
  villager's station up to 9 NPC templates), so a station is not a template, and the crafting stock (the
  `InventoryCraftingStock` component of 10 templates: the five Magic chests and five refilling plants) and the area
  deposit switch (its `canReceiveAreaDeposit`) are templates' data, not a station's: no station field maps them (see
  docs/kiln/modding/registries/README.md "Stations, recipes and the base's storage").
