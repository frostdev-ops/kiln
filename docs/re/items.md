# Items and inventories on the dedicated server (svn 1285601): evidence for kiln.items

Build: dedicated server Steam 26404334 / svn 1285601, exe sha256 `c5c9fe50…`, image base
`0x140000000`; RVAs below. Cross-checked against build 1273043 and EMF 0.3.3's notes
(MIT: `docs/engine_parts/re_inventory_api.md`, `re_take_items.md`, `inventory_crafting.md`, `re_custom_programs.md`;
client addresses there are 1269579 / 1273043). Method: capstone disassembly and Ghidra 12.1.2 headless decompiles of
the functions below, the sigdb resolver (typed holes) on both builds, and runtime checks in lab `items` (a copy of the
user's world, no player connected). Evidence tags: **[dis]** disassembly, **[dec]** decompile, **[cap]** typed-hole
capture in `sigdb/symbols/server.d/items.json`, **[rt]** runtime in the lab (kiln.log lines quoted).

## Short answers
- EMF signatures 26-33 (InventoryTxCtor/Init/AddItems/Commit/Rollback/FreeArrays, ReadOther, RemoveItems) resolve
  uniquely on 1285601 with EMF's own patterns; RVAs in the table. **[cap]**
- **The transaction did not change in this build.** InventoryTx is still `0x71B0` bytes (result flags at `+0x71A8`,
  captured 3 times), the setup block is still `0xF0` bytes with the same 30 fields, op records are still `0x34` bytes.
  The only shifted engine layout the service touches is the world's program table (count `+0xE15350 → +0xE16F50`,
  stride `0x1F20 → 0x1F28`), which the service captures. **[dis] [cap]**
- The transaction must run inside an ECS program: the tx holds the ProgramContext (`tx[0]`), reads other entities
  through program accessors (`readOther(storage = [*ctx + 0x30], accessor, entity)`), spawns gear item entities and
  destroys them through the job's command queue, and sends events through the program's events-out slots. So
  kiln.items registers a system program `kiln_items` (through kiln.world, or itself when kiln.world is absent) and runs
  every give/take in its update. Reads use `GetComponent` on the tick thread. **[dec] [rt]**
- Item id = FNV-1a-32 over the 16 GUID bytes (`GuidToItemId` `0x40720`): true for 4213 / 4213 items of the server
  registry. **[dec] [rt]**
- Gear: `addItems(…, OptU32* level, OptU8* rarity, OptF32* fuel)` → at commit `createPide` spawns the item's pide
  template with `Level` (requested level clamped to the item's `ItemInfo+0x674/+0x678` range), `PerkContainerNew`
  (`availablePerks = rarity + 1`), `ItemState`, `OwnerRelationship`, `UsedItem` and, for weapons, a gem slot by
  `BalancingRegistry` chance. What happens without a requested level or rarity (crafting): section 10. A level-2 Rare and a level-3 Epic Bronze Sword were created, read back and removed; the
  level-3 Epic survived a save + restart. **[dec] [rt]**

## 1. Functions (MS x64)

| name (sigdb) | 1285601 | 1273043 | signature / notes |
|---|---|---|---|
| `items/InventoryTxCtor` | `0x3ed10` | `0x3ba50` | `InventoryTx* (tx)`: zeroes, sets the three inline arrays (EMF pattern 26) |
| `items/InventoryTxInit` | `0x1b9300` | `0x1b3aa0` | `void (tx, const Setup*)`: copies the setup block (section 3) |
| `items/InventoryAddItems` | `0x1b1f60` | `0x1ac790` | `Result12* (out, tx, u32 owner, u64 slot, u32 item, u32 count, OptU32* level, OptU8* rarity, OptF32* fuel)` |
| `items/InventoryRemoveItems` | `0x1b0d90` | `0x1ab5d0` | `Result12* (out, tx, u32 owner, u32 item, u64 preferredSlot, u32 count)` |
| `items/InventoryCommit` | `0x1af860` | `0x1aa0a0` | `void (tx)`: rolls back if flags & 0x40, or if `tx[0x14]` (InventoryEmptyState accessor) is NULL and inventories were touched |
| `items/InventoryRollback` | `0x1b8310` | `0x1b2b10` | `void (tx)` |
| `items/InventoryFreeArrays` | `0x3ee60` | `0x3bba0` | `void (tx)`: frees arrays that grew past inline storage |
| `items/ReadOther` | `0x64e9e0` | `0x647a80` | `PtrSize* (out, storage, u16* accessor, u32 entity)` → `{ptr, size}`; wraps `items/ReadOtherImpl` `0x64ea80` |
| `items/JobStorage` | `0x65ba10` | | `mov rax,[rcx+0x30]`: job → entity storage (captured from addItems and removeItems) |
| `items/ItemRegistryFind` | `0x93b5c0` | `0x9315d0` | `ItemInfo* (u32 id, ItemRegistry*)`, map at `+0x38` (captured) |
| `items/GuidToItemId` | `0x40720` | `0x3d440` | `u32 (const u8 guid[16])`: 0 for a zero GUID, else FNV-1a-32 (`0x811c9dc5`, `*0x01000193`) in memory order |
| `items/ComponentMapFind` | `0x64dc40` | | `u16* (map, const u32* qualifiedHash)`: component type index (captured inside `GetComponent`) |
| `items/RegisterProgram` | `0x67ede0` | `0x6778e0` | `u8 (world, ProgramDesc*, u8 disabled)`; count u16 at `world+0xE16F50` (1273043: `+0xE15350`), max `0x380`, stride `0x1F28` (`0x1F20`) **[cap]** |
| `items/RegisterAllPrograms` | `0x245960` | `0x23ffc0` | `void (world)`; one caller (`0x2381f6`, inside ServerWorldSetup) |
| `items/IterBegin` | `0x663b30` | `0x65cb40` | `void (ctx, u64* iter, u32 size)`; size ignored |
| `GetComponent` (core) | `0x64ed50` | | `PtrSize* (out, world, u32 qualifiedHash, u32 entity)`: `storage = world+0xE179C0`, `registry = *storage`, map `registry+0x88` |

Not used by the service but located (unique, EMF's 1273043 patterns from `re_take_items.md`): takeFromSlotById
`0x1b1070`, getInventoryRef `0x1b5fa0`, takeItems `0x1b9460`, takeFromSlot `0x1b9620`, putItems `0x1b7100`, moveStack
`0x1b4550`, destroyItemStack `0x1b27a0`, getItemStack `0x1b61c0`, equipItem `0x1b4e70`, touchInventory `0x1b6b80`,
putStack `0x1b2540`, createPide `0x1b1920`, postCommitEvents `0x1b8820`, collectTargetInventories `0x1b5710`, admin
create handler `0x19df10`, itemLevelOf `0x1bb190`, itemRarityOf `0x1bb220`.

`Result12 = {u8 error; u8 pad[3]; u32 zero; u32 value}`; `OptU32 = {u32 value; u8 has; pad}`, `OptU8 = {u8 value; u8
has}`, `OptF32 = {f32 value; u8 has; pad}` **[dec]** (addItems copies `*level` (8 B), `*rarity` (2 B), `*fuel` (8 B)
into each op record).

## 2. InventoryTx (0x71B0 bytes) **[dis 0x3ed10, 0x1b9300] [cap]**
- `[0x00..0x1D]` copied setup (section 3). `+0xB0` flags, `+0xB1` stock mode, `+0xE0` operator, `+0xE4` source.
- `+0xF0` op array pointer, `+0xF8` count, inline storage `+0x118`, capacity `0x200` (`mov qword [rcx+0x100],0x200`),
  grow fn at `+0x108`.
- `+0x6918` remap array (inline `+0x6940`, capacity `0x80`); `+0x7140` touched inventories (inline `+0x7168`,
  capacity `0x10`); `+0x71A8` u8 result flags (`0x10` = did not fit: `or byte [r13+0x71a8],0x10` in addItems).
- Captured: `items/tx.result` 0x71A8 (ctor, init, addItems agree), `tx.ops` 0xF0, `tx.remap(_inline)`
  0x6918/0x6940, `tx.touched(_inline)` 0x7140/0x7168, `tx.touched_capacity` 0x10, `tx.operator` 0xE0. Same values on
  1273043. The service allocates `(0x71A8 + 8 + 15) & ~15` = 29104 bytes once (VirtualAlloc); the program is serial, so
  one buffer serves every transaction.

Op record (`0x34` B) **[dec putStack 0x1b2540, takeFromSlotById 0x1b1070, commit 0x1af860]**: `+0x00 u64 slot`,
`+0x08 item`, `+0x0C OptU32 level`, `+0x14 OptU8 rarity`, `+0x18 OptF32 fuel`, `+0x20 u32 pide`, `+0x24 u16 amount`,
`+0x26 u8 type` (0 put by addItems, 1 take by removeItems), `+0x30 u8 destroy` (take emptied the stack: the pide is
destroyed at commit). kiln.items reads the queued ops before committing to report which slots changed.

## 3. Setup block (0xF0 bytes) → tx, as InventoryTx_init copies it on 1285601 **[dis 0x1b9300]**

| setup | tx | kiln.items fills |
|---|---|---|
| `+0x00` u8 flags / `+0x01` stock mode | `+0xB0` / `+0xB1` | `0x01` with KILN_ITEMS_NOTIFY for a player, `0x80` with NO_MERGE; stock mode 0 |
| `+0x08` ProgramContext* | `[0]` | the update's ctx |
| `+0x10` ItemRegistry / `+0x18` BalancingRegistry / `+0x20` AttributeRegistry | `[1]` / `[2]` / `[0xF]` | singleton slots |
| `+0x28` InventorySetup (read) | `[3]` | accessor |
| `+0x30` Inventory | `[4]` | accessor |
| `+0x38` SlotSelection | `[0x1D]` | accessor |
| `+0x40` ItemState | `[5]` | accessor |
| `+0x48` OwnerRelationship | `[0x12]` | accessor |
| `+0x50` InventorySetup (write) | `[0x13]` | accessor |
| `+0x58` InventoryEmptyState (required) | `[0x14]` | accessor |
| `+0x60` WeaponGemSlot / `+0x68` NetworkFuel | `[0x15]` / `[6]` | 0 |
| `+0x70` Fuel | `[7]` | accessor |
| `+0x78..+0x98` | `[8..0xC]` | 0 (unused by add/remove) |
| `+0xA0` PickupEvent / `+0xA8` InventoryFull / `+0xB0` equip-event / `+0xB8` ? | `[0x17..0x1A]` | the two events-out slots (= the job) / 0 / 0 |
| `+0xC0` KnowledgeTriggerQueue | `[0x1B]` | players only (blueprint / "obtained" knowledge); NULL for containers |
| `+0xC8` operator / `+0xCC` source | `+0xE0` / `+0xE4` | the player (PickupEvent receiver), 0 for containers / 0 |
| `+0xD0` / `+0xD8` PlayerBases / `+0xE0` float3 | `[0xD]` / `[0xE]` / `[0x10..0x11]` | 0 (stock mode only) |

Identical to EMF's table for 1269579. The current `inventory_actions` descriptor (`0x1361390`) additionally sends
`ItemPerkChanged` (new event); add/remove do not use it.

## 4. addItems / removeItems on 1285601 **[dec]**
- addItems (`0x1b1f60`): `ItemRegistryFind` → 0x15; owner's InventorySetup via `tx[3]` must exist with
  `isInitialized` (+0x53) → 0x15; `collectTargetInventories` (empty → 0x1F); preferred slot if `slot == 0`; tops up
  stacks of the same id below `ItemInfo+0x14` unless HasPIDE (`ItemInfo+0x5D8 & 2`) or flag 0x80; then the default
  slot finder (skipped with 0x20) and every empty slot; leftover → `+0x71A8 |= 0x10`, `out.value = remaining`.
  putStack writes `{id, count}` into the Inventory component at once (write-tracked) and queues op 0.
- removeItems (`0x1b0d90`): owner's InventorySetup → 0x16 if missing; with `preferredSlot == 0` and a SlotSelection
  (players) the held action-bar slot first; then every link (Virtual links expanded) slot by slot; op 1 per slot,
  `out.value` = still missing. Not all-or-nothing by itself: kiln.items commits only when `value == 0`.
- createPide (`0x1b1920`, commit of op 0 for HasPIDE): level and rarity as in section 10; WeaponGemSlot for category 2 (Weapons) by
  BalancingRegistry chance; spawn through the job's queue (`0x5fb340`) with spawner = current entity / tx source /
  operator. The new id is written to `stack.pide` at commit.

## 5. The program `kiln_items`
Descriptor (engine ProgramDesc, `0x188` bytes, layout unchanged on 1285601: `+0x98` sys-read, `+0xB0` sys-write,
`+0xC0` sys-write optional mask, `+0xF0` other-write, `+0x170` events-out, `+0x180` flags, `+0x184` interval;
`RegisterProgram` copies `+0x180`/`+0x184`) **[dec 0x67ede0]**:
- flags `0x14` (system: no entity query; not in prediction) as EMF uses for its server programs (`emf_core.dll`
  `0x45895`: `world_kind == client ? 5 : 0x14`), interval 1;
- sys-read `BalancingRegistry, g38_items::ItemRegistry, impact::AttributeRegistry`; sys-write
  `KnowledgeTriggerQueue` (optional, as in `inventory_actions`);
- other-write `Fuel, Inventory, InventoryEmptyState, InventorySetup, ItemState, OwnerRelationship, SlotSelection`;
- events-out `InventoryFull, PickupEvent`. (The same access EMF's `emf_inventory` declares.)

Iterator (IterBegin `0x663b30` **[dec]**: `[ctx][T][R][6W][S][OT][O][EI][EO = job][add/remove u16]`): for this
program `[ctx][4 singletons][7 accessors][2 × job]` = 112 bytes; kiln.world reported "S4 OT0 O7 EI0 EO2, iterator 112
bytes" **[rt]**. Each accessor is a pointer to the component's u16 type index (ReadOtherImpl reads `*(u16*)acc`), so the
service checks every accessor against `ComponentMapFind(registry+0x88, FNV-1a("keen::ecs::"+name))` and maps them by
type index (iterator order does not matter), checks the item-registry slot holds a sane HashMap, and that both
events-out slots equal the job. Any mismatch switches give/take off. Observed type indices **[rt]**:
"accessors verified against the world's component map (Fuel 232, Inventory 283, InventoryEmptyState 285, InventorySetup
286, ItemState 296, OwnerRelationship 416, SlotSelection 530)".

Registration: through kiln.world (`register_program`, raw update) when that service is loaded; otherwise a hook on
RegisterAllPrograms registers the descriptor right after the game's 757 programs (checks: world not finalized
(`world[0] == 0`), server storage, table not full, every component name in the world's map). Both paths ran the full
test in the lab: "program kiln_items registered as #819 (T0 R0 W0 S4 OT0 O7 EI0 EO2, iterator 112 bytes, flags 0x14)"
(kiln.world) and "program kiln_items registered into the server world (own registration)".

## 6. Item registry and ItemInfo **[rt]**
`g38_items::ItemRegistry` (the program's sys-read slot): HashMap at `+0x38` (bits `+0x08`, capacity `+0x10`, keys
`+0x28`, values `+0x40`, count `+0x54`, relative to the map). ItemInfo: `+0` id, `+4` GUID, `+0x14` u16 maxStack,
`+0x17` rarity, `+0xCC` u8 ItemCategory, `+0x104` EquipmentSlotType, `+0x5D8` ItemFlag (bit 1 HasPIDE),
`+0x674/+0x678` level range, `+0x820` debugName BlobString `{u32 offset from the field, u32 length}`.
- "catalog: 4213 items from the item registry (4213 with a debug name; GUID hash = item id for 4213)".
- Against the Save Editor's item list (4262 entries): "4206 known (56 not in this build), debug
  name equal 4206, max stack equal 4206, category name equal 4206".
- ItemCategory values (list's name → game value): Customization 0, Equipment 1, Weapons 2, Tools 3, Instrument 4,
  FishingRod 5, BuildTools 6, Consumables 7, Ammunition 8, Materials 9, Blueprints 10, Currency 11, Collectibles 12,
  AnimalFood 13, WeaponGem 15, ColorPalette 16 (14 not seen).

## 7. Inventories **[rt]**
- An inventory owner has `InventorySetup` (84 B: `+0x00 linksEntities[16]`, `+0x40 linksCategories[16]`, `+0x50`
  flags, `+0x51` genericSlotCount, `+0x52` availableSlotCount, `+0x53` isInitialized); each link is an entity with an
  8-slot `Inventory` (96 B of `{u32 id; u32 count; u32 pide}`). A chest's first link is the chest itself: container
  1127 = "3 inventories, 24 slots … flags 0xc" with slots `1127:0..7` and two more inventory entities.
- The copied world has 26 entities with an initialized InventorySetup (21 non-empty containers; none with the
  IsPlayer or IsNpc flag, no player connected). kiln.world `find` and the service's own entity-map scan (`storage+0x148`) agree.
- Gear stack: `count 1, pide` → ItemState (20 B: `+0x00 containedInSlotId`, `+0x0E` state bits, `+0x10` rarity, `+0x11`
  level), Level (16 B, value at `+storageOffset`), PerkContainerNew (2 B), Durability (16 B), OwnerRelationship (the
  inventory entity). Read back through kiln.world: "gear item entity 2542 (kiln.world): Level 2,
  PerkContainerNew.availablePerks-1 = 2, OwnerRelationship 1127, ItemState.containedInSlotId 1127:7".
- **Entity ids of run-time entities change across a save + restart:** a sword given as entity 2543 in slot
  `<inv>:0` was entity 1128 in slot `1129:0` after the restart (level 3 Epic kept). The chest (1127) kept its id. Keep
  item ids and slot positions, not pide ids, across restarts.

## 8. Lab results (lab `items`, a copied world) **[rt]**
```
PASS give 7 x Wood Logs: Done 7/7 x 0x2b0cf1d5 @ 1127 slots [1127:7+7] after 0 step(s)
PASS read back: wood logs 0 -> 7
PASS take 7 back from 1127:7: Done 7/7 x 0x2b0cf1d5 @ 1127
PASS net zero after the stack round trip (wood logs 0)
PASS burst of 20 gives: 20 done, steps 0,1
PASS give gear Bronze Sword level 2 Rare: Done 1/1 x 0x43108313 @ 1127 -> 1127:7 item entity 2542
PASS gear level / rarity: level 2 (asked 2), rarity Rare (asked Rare)
PASS take the gear out of 1127:7: Done 1/1 x 0x43108313 @ 1127 (item entity 2542 destroyed)
PASS the taken gear's item entity 2542 is gone (no ItemState / OwnerRelationship)
PASS all-or-nothing give that cannot fit: NoRoom 4250/100000 x 0x2b0cf1d5 @ 1127 (dry run): only 4250 of 100000 fit
PASS container 1127 ends as it started: 1127:0=Midnight Lily x243, 1127:1=Raw Game x4, … 1127:6=Penny Bun x1
items-test done: 32 PASS, 0 FAIL
PASS persistence: the 3 wood logs survived the save (1127:7 x3)
PASS persistence: the gear survived: 1129:0 item 0x43108313 entity 1128 (was 2543 before the restart) level 3 rarity Epic
reload-test items-test 10: "10 reloads, 0 failures, avg 35.08 ms, max 39.91 ms, 0 leaked context(s)"
```
Latency: a request made in Pre runs in the same step's program update and its callback comes in the next Pre (one
step, ~41 ms). Step time p50 0.93 ms with the program registered (idle updates take one lock).

## 10. Rarity, level and perks of a created item **[dec] [rt]**
Why a crafted item can differ from its recipe tooltip. Server 26430253 / client 26430625 (svn 1287369); RVAs server
`0x…`, client in brackets. Fields from the game's reflection of `keen::ItemInfo`: `+0x16 generateRarity` (bool), `+0x17
rarity`, `+0x18 disableRarityGeneration` (Bitmask8), `+0x608 perkReferences[5]`, `+0x658 perkIds[5]`, `+0x674
itemLevelRange{minLevel, maxLevel}`, `+0x67c damageSetup`. On the instance: `Level` (`dataStorage`: current, max),
`PerkContainerNew{availablePerks, unlockedPerks}`, `ItemState{+0x0c baseDamageUi, +0x10 itemRarityUi, +0x11 itemLevelUi}`.

**What the item entity gets**, createPide `0x1b1920` [`0x411e20`], from the op's `OptU32 level` and `OptU8 rarity`:
| | requested | not requested |
|---|---|---|
| level | clamped to `[min, max]` | `min == 0` or `max < min`: no `Level` component; else a **uniform random roll in `[min, max]`** (the transaction's MWC generator, no player, world or area input) |
| rarity | as given (not clamped by anything) | `generateRarity` set: **the lowest rarity `disableRarityGeneration` allows, not a roll** (Common with an empty mask, value 8 when all are disabled); else `rarity` |
| perks | `PerkContainerNew{available = min(rarity + 1, 5), unlocked = 0}` only when `perkReferences[rarity]` is set (index = rarity, the earlier note `rarity-1` in docs/re/content.md was wrong) | same |

An instance's rarity **is** its perk count: itemRarityOf `0x1bb220` returns `availablePerks - 1`, and `ItemInfo.rarity` only without a
`PerkContainerNew`. With slot [rarity] empty, createPide gives no container and `rebalance_perks_and_level` adds one for the
slots up to the last filled `perkIds`: lab, Epic with slot 3 cleared: "rarity Rare, level 1, perks 3 available".

Only createPide and `rebalance_perks_and_level` read `generateRarity` (byte scan of both exes for a `+0x16` test next to
`+0x17`/`+0x18`), so changing it has no other effect.

**Who requests what.** All 26 callers of addItems pass the three optionals by pointer:
- Crafting: `handle_crafting_events` `0x8ad90` [`0x2790a0`] → applyRecipe `0x210f00` → the recipe function `0x19a1d0`
  [`0x3f8f30`, sigdb `items/RecipeApply`], whose output loop clears all three `has` flags (`uStack_4c = 0; uStack_57 = 0;
  uStack_3c = 0` right before the call at `0x19a3f3`). **A crafted item never has a requested level or rarity.**
- Random loot (`0x16ae30`, two sites) and random drop-table stacks (`0x1a1d70`): `has` = 1 for both, values from the
  roll (`0x16b5f0`). That is how a loot weapon with `generateRarity` and levels 1..55 gets a real rarity and level.
- kiln.items `Give`: as `Gear` says. Without `Gear` it is the same call as crafting.

**What the tooltips show.** The recipe tooltip builds its view in `0xb1fa70` (client) from `ItemInfo` of the recipe's first
output and never sees `generateRarity`: `view+0x2c` rarity = `rarity`; `view+0x24` level = `minLevel` (getter `0xe8f480`);
`view+0x28` damage = a function of `dmgMod`, `speedMod`, `minLevel` (`0xe779a0`); `view+0x2d` perks = `min(rarity + 1, 5)`
when the item has perks (`0xd64300`). `0xb20100` then prints `level` and `max = level + available - unlocked`:
`LEVEL 1/5` is level 1, 4 perks, none unlocked. An enhancement sits at `level - unlocked + i + 1`.
An unlocked perk raises the level. Two versioned player actions of `inventory_actions` (`0x1a5af0`, from PlayerInput) do it: ItemAction
type 3 UnlockPerk (`0x1a0b80`) pays a cost through a transaction (a balancing item, scaled by level, `0x93fb40`), then `unlockedPerks + 1`,
`Level + 1`; a second action (version at PlayerInput `+0x4b0`, handler `0x19fe00`) moves `unlockedPerks` and `Level` by +1 or -1 with no cost.
What in the client's UI sends the second one was not traced. No program does either without a player: lab, an Epic left in a chest for 30 s
stayed at perks `0400`, level 1. So an instance's `LEVEL 3/3` with one enhancement at level 3 is base level 2, the perk unlocked, and
`LEVEL 54/54` on an instance given at level 50 with 4 perks is four unlocks (the `inventory` op read 50, then 54).

**So a recipe's promise holds exactly when `generateRarity` is off and `minLevel == maxLevel`.** The game data:
| vanilla items | count | generateRarity | level range |
|---|---|---|---|
| crafted gear with perks | 515 | off | one level, every one (rarity: Rare 431, Legendary 31, Epic 24, Uncommon 19, Common 10) |
| exceptions: `Craft_Weapon_T3_2H_StaffEpic_chineseNewYear_01`, `Craft_Weapon_T4_1H_Lore_AxeEpic_Ministory_02`, `Craft_Weapon_T4_1H_Lore_Axe_Ministory_10` | 3 | on | one level; shown Rare, made Common: the game's own data has the same bug |
| loot weapons (`Weapon_T1_1H_SwordEpic_01`, ...), no recipe | 321 | on | 1..55 |
| **Kiln's clones of those, with a recipe**, before the fix | 3 in the old dump | on (inherited) | 1..55 (inherited): the bug |

kiln.content now gives a new item the crafted form unless its definition opts in (`settle_gear`, newcontent.cpp;
docs/kiln/services/content.md "Rarity, level and perks of new gear").

`rebalance_perks_and_level` `0x1acba0` [`0x40b8a0`] runs once per item entity without `PideRebalanced`: it builds
`PerkContainerNew` from an old `PerkContainer` (or `rarity + 1`), re-clamps `Level - unlocked` into the range, adds
`PideRebalanced`. It also treats a `generateRarity` item with no perk data as Common.

**A save load re-clamps the level [rt]:** instances saved at levels 49 and 3 (range 1..55) read level 1, rarity and
`PerkContainerNew` unchanged, after a restart under a one-level definition. The clamp above fits, but its other write (attribute
`0x638a979a`) did not show in `Level`'s second slot (still 55), so which function did it was not confirmed.

### Lab results (lab `craft-match`, `mods/tests/craftmatch-test`, server 26430253, no player) **[rt]**
`MADE` = an item entity read back after `Items.GiveRecipeResult`, which runs the game's recipe function `0x19a1d0`; `TOOLTIP` = computed as `0xb1fa70` does.
```
(vanilla, control)
TOOLTIP Weapon_T1_1H_Sword_Craft: Common, level 5, 1 perk(s) (rarity Common, generateRarity False, levels 5..5)
MADE Weapon_T1_1H_Sword_Craft via recipe: rarity Common, level 5, perks 1 available, 0 unlocked, baseDamageUi 17, perk list [Perk_Damage_Cut]
PASS control: without a level the game rolls one in the range: Bronze Sword levels 7,1,32,36,5,36,18,50,12,51,20,6
NOTE vanilla oddity Craft_Weapon_T3_2H_StaffEpic_chineseNewYear_01: tooltip Rare, level 16, 3 perk(s) (generateRarity True, levels 16..16), made: rarity Common, level 16, perks 1 available
(before the fix)
TOOLTIP Kiln_Sample_Glacier: Epic, level 1, 4 perk(s) (rarity Epic, generateRarity True, levels 1..55)
MADE Kiln_Sample_Glacier via recipe: rarity Common, level 18, perks 1 available, 0 unlocked, baseDamageUi 59, perk list [Perk_Damage_Lightning]
MADE Kiln_Sample_Glacier via recipe: rarity Common, level 13, perks 1 available, 0 unlocked, baseDamageUi 46, perk list [Perk_Damage_Lightning]
MADE Kiln_Sample_TowerSword via recipe: rarity Common, level 51, perks 1 available, 0 unlocked, baseDamageUi 86
(after the fix)
new content: item Kiln_Sample_Glacier (Epic, level 1): generateRarity off, levels 1..55 -> 1, so it is made as its tooltip shows (...)
TOOLTIP Kiln_Sample_Glacier: Epic, level 1, 4 perk(s) (rarity Epic, generateRarity False, levels 1..1)
MADE Kiln_Sample_Glacier via recipe: rarity Epic, level 1, perks 4 available, 0 unlocked, baseDamageUi 17, perk list [Perk_Damage_Lightning, Perk_Leech_OnDamage_Dealt_Percentage_Health_Wpn_3, Kiln_Sample_FrostbiteEdge, Perk_Damage_Cut]
```
Damage does not depend on rarity (dmgMod 1.6, as Glacier): level 1 Epic 17, level 1 Common 17, level 2 20, level 3 22, level 49 132, level 50 136. The client's menu said
Damage 17 at level 1 and an instance's tooltip Cutting 22 at level 3, so server and client data agree on the damage curve.

## 11. Water containers: capacity, units, fill and pour (server 26430253, exe `f0d824be…`) **[data] [dis] [rt]**
Where the water is, what the numbers mean, and what limits a bigger can. Data decoded with `tools/kfc/kfc.py` from a lab's
game files; disassembly of the server exe (VAs, image base `0x140000000`); runtime in labs `wcan-a`..`wcan-d` (2026-11-06,
no player). Used by the item blocks' `Holds` and `Flow` (docs/kiln/modding/item-blocks.md, "Water containers").

**Where the capacity is.** Not on the item. A water container's ItemInfo has `pidEntity` (flags `HasPIDE`, `UsesFuel`), and
the item's own entity is made from that template: `Fuel` (attribute), `FuelSetup`, `NetworkFuel`. **[data]**

| Item | `pidEntity` template | `FuelSetup.maxFuelCharges` | `Volume` config | Server `Fuel` max |
|---|---|---|---|---|
| `Tool_T1_WateringCan` (Watering Can) | `Pide_Fuel_Watering_Can` | 100 | 32 | 6,400 |
| `Tool_Water_Pouch_Refillable` (Water Pouch) | `Pide_Fuel_Water_Pouch` | 20 | 16 | 1,280 |
| `Tool_T3_Channelled_Water_Hose_Tool` (Portable Water Pump) | `Pide_Fuel_Water_Channeling_Tool` | 500 | 128 | 32,000 |
| (template only) | `Base_Pide_Fuel` | 50 | | |

Placed ones carry the same three components: the Water Barrel (`Prop_Destructible_Furniture_Barrel02_A_Wood_Water`) 150 with
rain refill, wells 75 to 500 (`Prop_NPC_Water_Well_a` 250, starts half full), factories (oven, seedling factory) 50 to 150.
Item rarity does not change the capacity (Common and Legendary cans: 6,400). No vanilla container holds 80. **[data] [rt]**

**Units.** `FuelSetup.maxFuelCharges` (sint32) is in charges. At spawn the game sets the `Fuel` attribute (Sint32 attribute,
hash `0x05b0bf75`; its max is the attribute `Fuel_Max` `0xa00e2601`; `dataStorage[0]` current, `[1]` max) to charges × 64.
Lab, items given into a chest and read with `World.ReadBytes`: can 100 → 6,400, pouch 20 → 1,280, pump 500 → 32,000, well 250
→ 16,000 (8,000 at its 50 % start), barrel 150 → 9,600. The attribute formula clamps `Fuel` to 0..`Fuel_Max` after every
change (`[0x20000, 0x1ffff, 0x20001, 0xcffff]`, interpreter `0x14003d730`). **[rt] [dis]**

**What players are sent: 16 bits.** `NetworkFuel {u16 fuel, u16 fuelMax}`, written by `fuel_network` (`0x140056dc0`) as
min(value, 0xFFFF) (`cmovg` at `0x140056ed9`, `0x140056f28`): it saturates, it does not wrap. Lab, the can's template set to:

| `maxFuelCharges` | server `Fuel` max | `NetworkFuel.fuelMax` | full can: `Fuel` / `NetworkFuel.fuel` |
|---|---|---|---|
| 100 | 6,400 | 6,400 | 6,400 / 6,400 |
| 1,023 | 65,472 | 65,472 | 65,472 / 65,472 |
| 1,024 | 65,536 | 65,535 | 65,536 / 65,535 |
| 1,025 | 65,600 | 65,535 | 65,600 / 65,535 |
| 1,000,000 | 64,000,000 | 65,535 | 64,000,000 / 65,535 |

So 1,023 charges is the largest capacity players see exactly; above it a container shows full until it is down to 65,535
units. The server's own math is i32 and stays right (`Fuel` holds 64,000,000). `ClientFuelChangeFx.previousFuel` is u16 too. **[rt] [dis]**

**Filling and pouring: one rate, the item's `Volume`.** The can's sequences (`Tool_RemoveWater` 81314069-…, fill, Secondary;
`Tool_AddWater_WateringCan` afc98d1b-…, pour, Primary; the pouch and the pump fill with `Tool_RemoveWater` too) are 0.5 s with a
`LoopEvent` at 0.2 s of 0.05 s, held while the button is (the pour also needs `CanPayUsageCosts`). At 0.2 s each pass:
- at open water, a `SpawnImpact`: fill program `ac68f1d4` does `addWater(cursor, -Volume, Range)` and adds what it took to the
  pide's `Fuel`; pour program `408ae9a0` clamps `Volume` to the `Fuel` left, `addWater(+)`, and takes off what it placed;
- at a prop (a well, a barrel, a crop, a seedling factory), a `TransferFuelEvent` (type hash `0x54bf4b90`), handled in program
  `actor_interaction` (update `0x14007bd70`, branch `0x14007cb92`): the used item's `Volume` (`0x1bb380` reads ItemInfo
  `+0x5F8` impactValues, config `0xd8f493f3`, as a raw i32) moves between the pide and `triggerContext.interactionHostId`:
  `PideToProp` (0) `n = min(prop room, pide water, Volume)`, `PropToPide` (1) `n = min(pide room, prop water, Volume)`
  (`0x14007cfa2`, `0x14007cda2`). It never reads `FuelAmount` or `FuelCharges`. **[data] [dis]**

Which of the two runs follows the target, by entity tag (the interaction target or the cursor). Both use `Volume`, so the
game has no fill-only rate: a bigger `Volume` fills faster everywhere and pours faster everywhere. A fill-only change would
need extra `TransferFuelEvent`s in a copy of `Tool_RemoveWater` (props) and a copy of program `ac68f1d4` (open water), or a
hook at `0x14007cd8c`; not built. A step can't overfill (the min above, then the clamp). `Volume` must stay a Sint32: a float
config would be read as its bits. **[dis] [inf]**

**Rate.** The loop wraps once per 0.05 s of sequence time, and the step adds the world's dt (`0x1400377a0`; at most one pass a
step): about 20 passes a second on a dedicated server (24.5 steps a second, ~0.82 a step). The can moves 32 a pass: 640 units,
10 charges a second, so empty to full in about 10 s at a lake or a well that has enough. Derived from the code and the
sequence data; not timed with a player (a lab has no player to hold the button, and `Abilities.PlaySequence` has no used item). **[dis] [inf]**

**Save.** A can of 1,000 charges (64,000 units, a template clone) left full in a chest was full after a save and a restart:
64,000 / 64,000 on the server and in `NetworkFuel` (`mods/tests/blocks-water-test`, restart phase). **[rt]**

## 12. Tracked writes to another entity's component (server 26430253, exe `f0d824be…`; client 26430625) **[dis] [dec] [rt]**
How a program changes another entity's component so that players and the save get it, as `actor_interaction` does when
it moves water between a can and a well. Used by kiln.items' water functions (docs/kiln/services/items.md, "Water").

| What | Server | Client | Notes |
|---|---|---|---|
| `WriteOther` | `0x64e790` (wrapper of `0x64e7c0`) | `0x9b2900` | `PtrSize* (out, storage, tracker, const u16* accessor, u32 entity, u8 flag)`: the component like `ReadOther`, and when `storage+8 == 0` (the server world) `0x66f170(storage, tracker, record, type, flag)` marks it |
| job's tracker | `0x65b6b0` = `mov rax,[rcx+0x1c0]; ret` | `0x9bf820` | takes the job (`*ctx`) |
| job's write flag | `0x661300` = `mov rax,[rcx]; movzx eax, byte [rax+0xbb]; ret` | `0x9c5350` | takes the program context |
| `JobStorage` | `0x65ba10` (`mov rax,[rcx+0x30]`) | `0x9bfb80` | already `items/JobStorage` |
| `AttrFind` | `0x991810` | `0x123a930` | `{u8 error; u32 0; desc*} (out, attribute id, AttributeRegistry*)`; desc `+0` the component's attribute group, `+0xC` u16 index into its values |
| `AttrChanged` | `0x52700` | `0x2358c0` | `(component, AttributeRegistry*)`: re-runs the attribute's formulas (Fuel: the 0..max clamp) |
| tick-thread variant | `0x64edb0` | | `(out, world, hash, entity)`: `WriteOther` with the world's own tracker at `world+0x15d39f0`, flag 0 (not used by Kiln) |

- **The marking** (`0x66f170`): replication (`0x66f2c0`, gated on a per-component bit at `registry+0x190` and a peer
  count at `storage+0x7bc2d0`) and the save (`0x66f360`, gated on a per-component bit at `registry+0x230`; for entity ids
  1..1023, players, only with the entity flag `8`). **[dis]**
- **The call site** sigdb captures all four from: `actor_interaction`'s `TransferFuelEvent` handler (`0x7cb28`): write
  flag, tracker, storage, then `WriteOther` of Fuel; `AttrFind` from its `Fuel_Max` lookup (`0x7ce22`), `AttrChanged`
  from the line after it takes the water off the source (`0x7cf34`). Unique on both builds' sides
  (`sigdb/symbols/server.d/items.json`, feature `items.fuel`). **[dis]**
- **Fuel's block** `{u32 group 0x05b0bf75; u16 values offset; u8 flags (bit 0: has values); u8 count; s32 values[]}`;
  `actor_interaction` checks `flags & 1` and that the desc's group equals the block's first u32 before it reads a value.
  **[dis]**
- **Lab** (`mods/tests/fuel-test`, three boots): a write through `WriteOther` + `AttrChanged` inside kiln.items' program
  reached `NetworkFuel` within 3 steps and was kept by a save and a restart (`4321/6400`, `777/1280`); a raw write on a
  container a save had already written was not (`1000/6400` back, raw `3000`). A container created in the same session
  is saved whole at its first save, raw writes included. **[rt]**

## 13. Moving items: the slot transfer, quick stack and move all (server 26430253, exe `f0d824be…`; also 26404334) **[dis] [rt]**
How a stack moves between inventories with its item entity, and where the game does quick stack and move all. Starting
point: shroudlink's hook table (MIT, `native/plugins/shroudlink/upstream/server/src/hooks.rs`, game_api.rs, events.rs) and EMF's
`moveStack` (section 1). Sigdb: `items/SlotTransfer`, `items/QuickStack`, `items/AreaDeposit`, `items/MoveAll` (features
`items.move`, `items.transfer`). All four resolve uniquely on both builds, with the same RVAs on both.

| What | RVA | Signature (MS x64) |
|---|---|---|
| `items/SlotTransfer` (EMF moveStack, shroudlink `transfer`) | `0x1b4550` | `Result12* (out, tx, u64 dst, u64 src, i32 mode)`: slot ids are `entity | index << 32`; mode 0 whole stack, 1 half, 2 one item; `out.value` = items moved. Same slot: ok, 0. Calls takeFromSlot `0x1b9620` / takeItems `0x1b9460` / putItems `0x1b7100`, queues ops in the tx's remap array (`tx+0x6918`) |
| `items/QuickStack` (shroudlink `quick_stack`) | `0x1b3830` | `Result12* (out, tx, u32 to, u32 from, feedback*)`: calls the slot transfer once per stack. `feedback` is optional: when not null, `0x1b7d80(feedback, ItemInfo)` records each item (the "stored" list); kiln.items passes null |
| `items/AreaDeposit` (shroudlink `area_deposit`) | `0x1b2a70` | the Quick Stack Station's deposit; calls quick stack from `0x1b305f`, once per chest. Called from `0x88ea1`. Checked, not hooked |
| `items/MoveAll` (shroudlink `move_all`) | `0x1b3fb0` | `Result12* (out, tx, u32 from, u32 to, u32 mode, u8 flag)`. `flag` 0 leaves the source player's two action-bar pages (`SlotSelection+0xa0/+0xa4`) alone, 1 moves them too. Its `out.value` is **not** an item count (a move of 275 items returned 0), so kiln.items counts from the inventories |

Callers. A client's `inventoryTransferAction` (32 bytes: version, source and target entity, source and target slot, `type`
u8 `InventoryActionType` One/Half/All/CustomAmount/Destroy, `flags` u8 `TransferItemToInventoryFlag`, `amount` u16) is handled in
the chunk `0x19ea0f..0x19f35e` of `inventory_actions`. The handler builds a fresh tx itself (`0x3ed10` ctor, `0x1b9300` init
with a 0xF0 setup block, as section 3) and branches on the flags byte: **bit 1 (`RefillStacks`, 0x02) = quick stack** (call at
`0x19ec04`, `to` = the action's target entity, `from` = its source), **bit 2 (`MoveAllStacks`, 0x04) = move all** (call at
`0x19ef76` with mode 1 and flag `!(flags >> 2 & 1)`), bits 3-4 (`ToActionbar`, `ToBackpack`) another branch (`0x19f15c`), none =
one stack by the slot transfer (`0x19f077`). So both whole-inventory transfers are the same client action. `MoveAll` has more
callers: `0x1b5457`, `0x1b548a` (in `0x1b4e70`, EMF's equipItem) and `0x1b9e21`; the slot transfer is also called from `0x7ca86`.

**The transaction words** the hooks read (the game's own tx, as section 3 lists): `tx[0]` ProgramContext (`*tx[0]` the job,
`JobStorage(job)` the entity storage), `tx[1]` item registry, `tx[3]` InventorySetup accessor, `tx[4]` Inventory, `tx[5]`
ItemState, `tx[7]` Fuel, `tx[0x12]` OwnerRelationship, `tx[0x14]` InventoryEmptyState, `tx[0x1D]` SlotSelection. kiln.items checks every accessor's type
index against the world's component map before it trusts them (once per registry), and passes straight through if one differs.

**A piece of gear moves with its item entity.** The slot transfer moves the stack record `{item, count, pide}` to the other
slot and the commit rewrites the pide's `ItemState.containedInSlotId` and `OwnerRelationship`; the pide is not destroyed.
Lab: a level 4 Epic Bronze Sword with an unlocked upgrade and a gem slot, moved between two chests and back: "item entity 2543 ->
2543", `Level`, `PerkContainerNew`, `Durability` and `WeaponGemSlot` byte for byte the same, `ItemState.containedInSlotId`
1142:0 and `OwnerRelationship` 1142 after the move; after a save and a restart the sword is in the second chest at level 3
Epic (the item entity is renumbered, as section 7 says). A move inside kiln.items' program runs the slot transfer on a tx made
as `run_give` makes it, then commits.

**Hooking.** safetyhook inline hooks on `0x1b3830` and `0x1b3fb0` (first bytes `48 89 5C 24 18 ...` and `48 89 4C 24 08 ...`, both 5-byte
relocatable). shroudlink's retour hooks the same two. Both orders ran in a lab with a shroudlink mod from the vendored SDK
keeping wood out of every quick stack and move all: shroudlink installed first (Kiln's hook on top, installed at the first
registration), and Kiln first (`KILN_ITEMS_HOOKS=early`; shroudlink then logs `kiln: found in the exe file (already hooked in
memory): +0x1b3830`): in both, shroudlink's keep ran once per call and Kiln's report arrived once.

Lab (`items-test`, `dev20-wp13`):
```
PASS move_stack: 9 wood logs moved, 1127 9 -> 0, 1142 0 -> 9 (Stack Done 9 item(s) in 1 stack(s) 1127 -> 1142; ledger 1127:7->1142:0 x9)
PASS move_items: 3 of 9 wood logs moved back, 1142 9 -> 6, 1127 -> 3
PASS move gear: Stack Done 1 item(s) in 1 stack(s) 1127 -> 1142; item entity 2543 -> 2543 in 1142:0 (a take and a give would have made a new entity)
PASS quick stack by the game's function: QuickStack Done 5 item(s) in 1 stack(s) 1127 -> 1142; A lost 5, B gained 5, ledger 2b0cf1d5 1127:7->1142:0 x5
PASS move all by the game's function: All Done 280 item(s) in 8 stack(s) 1127 -> 1142; A now holds 0 stack(s), B gained 280
PASS native handler: it moved a stack itself first (4 of 0x95c119cb), the game moved the rest: 32 items in all, B 60 -> 92
PASS native handler: cancel: the game's move all did not run; only the handler's own move happened (4 items, canceled 1, A 275 -> 271)
```
Not known: what the client shows after a server-side move while the player's window is open (the game's own drag is predicted by the
client, a server move is not); the meaning of move all's `mode` (kiln.items passes 1 as the game's own caller does for a client
action); how quick stack's `feedback` reaches the client; whether anything in a real player's tx (operator, events) changes
what these functions do. Client checklist: docs/kiln/modding/inventory.md.

## 14. Container names: the string table (server 26430253) **[dis] [rt]**
A renamed chest carries `keen::ecs::CustomPlayerString` (hash `0x0e699564`, 8 bytes: the id), which only a renamed object has: in
the lab world none of the 26 inventories has it, and the one entity that does (entity 1025, not an inventory) holds 0. The id is
the FNV-1a 64 of the UTF-8 text. The text is in the world's `CustomPlayerStringsSystem` singleton (a sys-write of the program
`update_logic_graph_system`, which calls `set_string`; `api->singleton(world, "CustomPlayerStringsSystem")` finds it).

| What | RVA | Notes |
|---|---|---|
| `items/SetString` (shroudlink `set_string`) | `0x915b80` (26404334: `0x915b90`) | `(out, strings, u64 oldId, {char*, u64 len}* text)`: hashes the text, looks the id up, inserts the record when new; the id at `out+8`. Callers: `0x1e97fc` (the logic-graph program) and `0x7bde90` (the message dispatcher's rename handler) |
| rename handler | `0x7bdb20` (dispatcher), call at `0x7bde90` | after `set_string`: if the entity has the component (`0x64edb0`, the tick-thread WriteOther of section 12), writes the id; else `0x5fc570` AddComponent `(world, entity, hash, {ptr, 8}*)` adds it |
| `items/AddComponent` | `0x5fc570` | wrapper of `0x5fc340` on `world+0xE179C0`; lab use only |

The table is a `HashMap<u64, record*>` of the same type as the item registry's (section 6): bitmap pointer at `strings+0x198`,
capacity `+0x1a0`, keys `+0x1b8`, values `+0x1d0`, count `+0x1e4` (sigdb captures all five from `set_string` and kiln.items checks them
against the registry map's offsets). Open addressing, linear probing from the low bits of the id. The record is the layout shroudlink's heap scan
recognises, found at `+0x10` of the pointer the values array holds: `{u64 id; char* text = &record + 0x28; u32 len; u32 pad; u64
capacity = 0x100; ...; char text[256]}`. kiln.items checks pointer, capacity, length, the NUL and the text's hash.

Lab: the self-test (`kiln/data/kiln.items/selftest`) names the lowest-id non-empty container through `set_string` and `AddComponent`, as the
handler would for a client:
```
items selftest: named entity 1026 "Kiln Lab Ores" (id 6b14f1f1dc987e89)
container names: string table at 00000000BBA03898, 1 of 2048 slots used; the record for id 6b14f1f1dc987e89 is at +0x10 of what the table points at (text 13 bytes)
PASS chest_name: the placed chest 1026 is "Kiln Lab Ores" (a name the game's rename path stored; shroudlink's "world chest_name")
PASS chest_name: the component holds 6b14f1f1dc987e89, the FNV-1a 64 of the text (6b14f1f1dc987e89)
PASS persistence: the chest named "Kiln Lab Ores" before the restart is read back from the save: entity 1026 is "Kiln Lab Ores"
items selftest: PASS a container from the save is already named: entity 1026 is "Kiln Lab Ores"
```
The save (`world0.ksd`) carries both the component and the table: a restarted lab reads the name without anything naming it again.
The user's world copy in `~/Work/ew-lab` has no named chest, which is why the lab names one. Not known: how clients get the text (the
`CustomPlayerStringServerMessage` path: `next_pending` `0x92fad0`, `mark_sent` `0x94ec70`), and what the string table's limit of 256 bytes does
to a longer name (the game's own text field refuses it).

## 15. Per-item attachment identity (stretch finding)

Gear's item entity is stable during a session, including `Items.Move`, but is renumbered on load. It has no
`CurrentTransform`; its default position is not an identity. The first restart probe attached `level3` and `level5`
to two swords of the same template. Kiln.Saves merged their template/position records on load and assigned `level5`
to the level-3 sword. That exposed a real bug in `EntityTable.Absorb`'s positional identity assumptions.

Kiln.Saves now permits positional persistence only when kiln.spawn reports a known template with `CurrentTransform`
and without `DoNotSave` (template flag bits 0 and 3 set, bit 1 clear). Item attachments remain session-only;
`EntityData.IsPersistent` returns false, staging omits them, and both direct and periodic matching refuse legacy
positional item records. Known saved world props, creatures and NPCs retain their existing matching. Unknown
template metadata is retried rather than cached permanently. Regression: two same-template swords after a save and
restart read neither tag; a placed chest restores `chest-persistence-control`. Lab: `items-test` 95 PASS before the
save and 15 PASS after the restart, no failures.

The MIT Enshwick source at alpha.13 (`945a1b8f0c9d1971679d9f3bc3dcecc127a5bcad`),
`crates/internal/loader/src/item_data.rs` and `item_data/{attach,world_file,carried}.rs`, uses per-session item entity ids
and saved inventory/character anchors with item checks. Ambiguous or missing anchors are set aside and cannot later
attach to another item. `stats/affixes.rs` serializes rolled `ItemStats` into that store and its own stat engine
applies the modifier bundles. Ordinary `EntityData` is not a substitute for the inventory anchors.

**dev.25: per-item data exists.** `InstanceData` (Kiln.Saves, `managed/services/Kiln.Saves/InstanceData.cs`) keeps a
typed value on one item with an item entity of its own (gear, tools, cans): in a session the item is its entity plus
the stamp kiln.items v4 gives it (`ItemInstanceRef`, capability `items.instances`); at each save Kiln writes the
item's holder, flat slot, item id, the fingerprint of its save record (level, perks, gem, dye; durability and fuel as
ratios) and the holder's whole item list, and after a load or a join it gives a record back only to the one item that
certainly is it. When that is not certain the record is set aside and reported, never attached by itself. `EntityData`
on an item entity stays session-only. Limits:
- `Persist.Carried` (data that travels with a character between worlds) is not available: declaring such a key throws
  `KilnUnavailableException("saves.instance_data.carried")`.
- Two identical items (equal item id, level, perks, gem, dye, durability and fuel) whose slots were swapped where Kiln
  did not see (in another world, edited offline) come back by slot on the newest exact load and trade their data;
  nothing reports it.
- World holders (chests, stations, tombstones, NPCs) work on a test server (`mods/tests/instances-test`, lab copy of a
  real world: stop, `kill -9`, a restored backup, 1,000 tagged identical swords). Nothing about items in a player's
  inventory has run with a real player; that half waits for the guided client pass.
- Affixes (rolled modifiers and an engine that applies them) are still not built.
Guide: `docs/kiln/modding/inventory.md`, "Data on one item"; service: `docs/kiln/services/saves.md`.

## 9. Open questions
- Players (no client in the lab): popup and PickupEvent with `0x01`, knowledge unlocks through the queue, the
  backpack-priority setting (the game's own handlers add `0x20` when `PlayerInput+0x5b0` bit 11 is set; kiln.items does
  not), which slot new weapons land in, and take from a player's held / equipped slots (removeItems unequips and
  destroys equipped gear). See the client checklist in docs/kiln/services/items.md.
- Fuel at creation (`OptF32`, FuelPercentageInit): not exposed; its unit (0..1 or 0..100) was not checked. What fuel is:
  section 11; writing it afterwards: section 12.
- Water containers: the fill and pour rate with a player (section 11 derives it), and whether a can made before a capacity
  change takes the new capacity when the world next loads.
- Moving gear without destroying it: done in section 13 (`Items.Move`, kiln.items v3).
- No inventory in this world carries the IsNpc flag; whether villagers / companions have one was not checked.
- `tools/sigport/sigdb_check.py` counts non-overlapping matches (Python `finditer`), the core counts overlapping ones:
  a pattern can pass offline and fail at boot (it happened with the first InventoryTxCtor probe).
