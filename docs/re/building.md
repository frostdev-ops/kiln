# Building materials, placed objects and cosmetics

Client Steam build 26430625 (`enshrouded.exe` sha256 `5d9ef2f1…`), dedicated server Steam 26430253 (`f0d824be…`), both svn 1287369,
`1287369|^/game38/branches/release_1.0|2026-11-04T11:17:54Z`. RVAs are for these builds; the server's unless marked "client". Image base
`0x140000000`.

Sources: the game's reflection and every resource named below, read with `tools/kfc`; Ghidra 12.1.2 headless decompiles and capstone over both
executables; SPIR-V from `keen::GpcResource` read with `spirv-dis`; a lab server with a copied world and no player. No game files are in the repo.

Tags:
- **[refl]** the game's reflection records;
- **[data]** checked over the game's resources, with counts;
- **[dec]** decompile or disassembly;
- **[spv]** read from the game's SPIR-V;
- **[rt]** run on a lab server, log lines in the section;
- **[hyp]** not verified.

One client session checked the surface tables (1.4, **[rt client]**); everything else about pictures is **[dec]**, **[spv]** or **[hyp]**.

## Short answers
- **A building material is one byte in a cell, and every table that describes it is data.** Cell low byte 1..127 is terrain, 128..255 is building
  material `id - 128`. 116 terrain and 85 building ids are used; 11 and 43 are free. The byte is the limit: no more than 128 building materials
  without changing the cell format. **[data] [dec]**
- **The build menu is the tight limit: 64 items a pool.** A client stops on a pool past 64 items (1.2). The game's wall pool has 61, so 3 new wall
  materials fit across all mods; roofs 50, placeable terrain 8. **[dec] [data]**
- **The GPU tables already have room.** `BuildingMaterialBlendingResource` is cooked with 128 slots of 392 bytes and `GiVoxelBuildingMaterialResource` with
  128 of 180; slots 85..127 are zeros. So a new building material is an edit of existing resources; no buffer grows. **[data]**
- **Hardness is data, on both sides.** `TerraformingEfficiencyRegistryResource` holds hardness category, health points and material class per
  id. A tool holds damage per hardness category. The dig kernel's cost per material = `health / (damage[hardness] × miningDamageFactor × classMod)`; zero damage or `Unbreakable` leaves the material untouched. **[dec] [rt]**
- **Saves keep a GUID per material id; the wire doesn't.** A terrain blob in the save carries a 256-entry GUID table and remaps on load. Cells of an unknown GUID come
  back as air. Replication sends raw bytes. **[dec] [rt]**
- **A free slot draws, and an id past the count draws a fallback.** On a client, a slot filled through resource overrides alone drew like the material it was copied from; an id past `layerCount` drew a plain stone surface without edge decoration, and nothing crashed. The fill shaders compare the index with the table's `layerCount`. **[spv] [rt client]**
- **Stations are data plus one state machine.** A forge and a drying rack are the same template kinds: `Crafting` + `FactoryStation` + `Inventory` and optional parts. The
  game's "fuel" for a forge is a recipe input (charcoal). A station runs recipes of its own workshop id, and any id works on the server. **[data] [dec] [rt]**
- **Cosmetics exist in the game.** Five vanity slots, each shown instead of its armour slot; a vanity item is an armour-slot item with flag `IsVanityItem`. Dyes are a
  palette item on the item's entity. Hair and beards are items too. **[refl] [data] [dec]**

## 1. Materials: what's defined where

### 1.1 The cell, ids and GUIDs **[data] [dec]**
| What | Where | Notes |
|---|---|---|
| Cell | u16: low byte material, high byte fill (docs/re/build.md) | 0 = no material |
| Id ↔ GUID | `VoxelWorldResource.materialGuids[256]` | 5 worlds of 145 fill it, all identically: 201 entries (1..116, 128..212). The rest are zero |
| Item → id | `ItemInfo.equipment.voxelData {isBuildingVoxel, placeVoxelMaterial GUID, placeVoxelMaterialId u8}` | 135 of 135 items agree with `materialGuids[id]` |
| Id → properties | `TerraformingEfficiencyRegistryResource {terrainConfigs[116], buildingConfigs[85]}` | The id in a config is `buildingMaterial.value = id - 128` or `terrainMaterial.value = id` |
| Id → GPU data | `BuildingMaterialBlendingResource`, `GiVoxelBuildingMaterialResource`, `BuildingMaterialParametersResource` | Client only. `WorldMaterialBlending2Resource`, `SimpleWorldMaterialResource` for terrain |
| Id → particle class | `VoxelMaterialVfxDataResource.materialType[256]` | Both sides. 8 non-zero, all terrain |

`SolidVoxelMaterial` (304 B) and `PbrBuildingMaterial` (1,392 B) are editor types: they appear in reflection, and no resource of those types ships. `placeVoxelMaterial` is only a GUID to
compare with the table. **[refl] [data]**

Six **relative building slots** have no item (`materialItem` empty): 1, 3, 6, 9, 11, 28, absolute ids
129, 131, 134, 137, 139, 156. Their slots hold `_DEPRECATED_` or `OLD_` surfaces. Corrected 2026-11 (dev.28 RE,
caps VERIFY) **[data]**: these are **reuse candidates**, not unused byte values; old saves/appearance can still depend
on their GUIDs. The 43 genuinely unused ids 213..255 plus these six candidates give the often-quoted 49, not 49
never-used ids.

### 1.2 Pieces and the build menu **[data] [dec]**
A wall is two items and three resources:
- a **shape**, an item of category `Blueprints`, flag `IsBlueprint`, slot `BuildTool` (`Blueprint_Voxel_Block_Wall_Straight_4m`). 52 ship. It names its material pool
  (`voxelBlueprintMaterialPool`), a snapping config (`voxelBlueprintConfig`, one of 7 `VoxelBlueprintConfig` resources), a `voxelObject`, its cost in blocks
  (`requiredCraftingIngredients`) and its placement box;
- a **material**, a block item (`Block_T1_Stone_Rough_CRAFTING`), category `BuildTools`, slot `BlueprintMaterial_Default` or `BlueprintMaterial_Roof`;
- `VoxelBlueprintItemRegistryResource.blueprintItems` (126): `{itemId, size, data, isDataCompressed}`, per item with a shape. Compressed is one bit per cell
  (8 × 8 × 1 = 8 bytes of 0xFF), otherwise one fill byte per cell (10 terrain shapes). Block items are here too, with size 1 × 1 × 1;
- `VoxelBlueprintMaterialPoolRegistryResource.materialPools`: 4 pools, `BuildingDefaultMaterials` (61 items), `BuildingRoofMaterials` (14), `TerrainMaterials` (56), `OvergrowthMaterials` (5). A pool lists
  the items the shape cycles through. `MaterialSelection` on the player names the chosen item per pool;
- `voxelObject` → a `VoxelModelResource` `{size, data, isTerrain}` of the same GUID: 144 items name 78 resources. Both sides have them, and only the client reads them (two functions, `0x2f82d0`, `0x2fde20`).

**What a placement costs** (server 1287369, **[dec]**). `requiredCraftingIngredients` isn't it: every 4 m shape says 20 whatever its cells (the door wall's 44
too). `player_building_blueprints` (`0xc4410`) has `0x228f30` dry-run the write: `0xa3cbc0` → `0xa309f0` → `0xa38900` adds 1.0 for each blueprint cell that
lands on air or a replaceable terrain cell (a fill shape: the fill it adds / 255) and skips cells already built (material ≥ 128). `0x9333b0` rounds that count
for a building material (`(int)(count + 0.5)`), `0x22e630` lists `{material item, count}`, `0x22d480` checks and takes it, then `PlaceBlueprint`
(`0x225c10`) writes. So a placement takes one block per cell it fills: 64 for the 4 m wall in the open, 44 for its door wall. The client's number under the
cursor is the same count (with the placement's overlap: 40 for a door wall whose bottom row sat in the floor).

**The preview model's cells** (client 26430625, `0x1df330`, **[dec]**): a byte below 3 is empty; a byte b ≥ 3 is building material `b + 0x7d`, replaced by
the player's chosen material when there is one (the override `0x2fefa0` passes). Every wall-pool shape uses `0x12` (143, Rough Wood Block), every roof-pool
shape with a snapping config `0x08` (133, Tared Shingles); stairs and sloped roofs fill the space under them with `1` (not drawn). The cells the server
writes come from the blueprint entry, never from the model.

**Snapping.** Every shape's `snappingAABB` equals its `placementAABB`, and only the sloped roofs (one cell past their cells: 4.5 m for 4 m) have
`voxelSnappingConfig.snapBoxOffset` set. The snapping rules (`VoxelBlueprintConfig`) place by those boxes, so a shape whose cells don't match its boxes lands off
its cursor. `placementColliders` are boxes over the solid parts: one for a slab, three for the door wall, one per step for a slope.

**In an inventory.** The client shows an `IsBlueprint` item (flag bit 3, `ItemInfo +0x5d8`) as the Construction Hammer (`Tool_Quickbuilder_Hammer`,
`0x2950da8d`): the item view helper `0xd3e130`, the tooltip `0xb21000` (`0xb211cd`), the inventory panel `0xb1a230` (`0xb1a48b`), and two more readers of the
hammer's id at `0x152cb30` / `0x152d148` (`0x3ff670`, `0x418660`). The game's own shapes are never in an inventory: no recipe makes one.

**A pool lists 64 items at most** (client, **[dec] [data]**, and a crash on a client). The client program `player_ui` (`0x2b8430`, from the program table at
`0x1e25a90`) calls `0x3cca80` on every update of the local player. It builds a cache at `player_ui + 0x1f50`, keyed by item id, of entries of `0x610` bytes:
64 records of 24 bytes `{material item id, how many of it the inventories it gathered hold, ...}` (the count from `0xe93080`), a count byte at `+0x600` and the shape's cell count at `+0x608`
(`0xe9f9b0`, a popcount over the blueprint entry's bits). For each item (`0xe94470`) that has a blueprint entry (`0xe89bf0`), `0xe9eba0` hashes the item's
`equipment.voxelBlueprintMaterialPool` GUID (`ItemInfo +0x104 +0x3a0 = +0x4a4`) and returns that pool's `materials` (`VoxelBlueprintMaterialPool +0x8`). Each
material goes into a stack `SmallDynamicArray` of 64 (`rbp+0x310`, count in `sil`); `cmp sil, 0x40` / `je 0x3cd5f3` at `0x3ccd97` is the error `SmallDynamicArray
is out of elements 64` (`[X]` in the game log), and `0x3cd60f` then writes to address 0 on purpose. `0xe9eba0` has no other caller. So no pool may list more
than 64 items, the game's own among them. The game's pools: `BuildingDefaultMaterials` 61 (room for 3), `BuildingRoofMaterials` 14 (50), `TerrainMaterials`
56 (8), `OvergrowthMaterials` 5. Seen on a client (26430625) that served 65 and 67 items in the wall pool: three crashes out of three when entering a hosted
world, right after `Receive building zones`, the stack `0x3cd60f` ← `0x2bae75` (`player_ui`) ← `0x9af2e3` (the program runner). With 63 the same client played.

**Unlocks.** The menu lists what a player can make: recipes, as for any item (`knowledgeRequirement`). Configs carry a `knowledgeQuery` too: `0xa2fd6b` evaluates them into two
256-bit sets of known terrain and building ids (`update_terraforming_queries_*`). What reads those sets wasn't followed. **[dec]**

### 1.2.1 Namespace versus selector storage (dev.28; server 26430253 / client 26430625)

S `f0d824be…`, C `5d9ef2f1…`; dossier chapter 17.5, caps VERIFY claims 11–13 confirmed. [data] Both archives
have 116 terrain / 85 building configs and pools 61/14/56/5. **11 new terrain ids** and **eight extra placeable
terrain items in the existing pool** are independent limits; unused id room does not enlarge a selector.

[refl] VoxelBlueprintMaterialPool stride 24 B: equipmentSlot `+0`, poolId `+4`, BlobArray<ItemId> materials `+8`,
BlobString debugName `+0x10`; registry is 8 B with materialPools at `+0`. [static] C `0xe9eba0` hashes selected
ItemInfo pool reference `+0x4a4` through `0x223400` and probes a runtime map with its actual capacity, returning
row materials `+8`; no fixed four-pool restriction was found in **this lookup**. This does not establish arbitrary
appended rows were ingested/drawn. The 64-record builder/cache remains separate and must stay within its bound.

[static] Current paired inspection anchors: SaveRegion S `0x83d200` / C `0x10b89b0`, LoadStore
S `0x830e30` / C `0x10ac730`, per-peer store serializer S `0x83ba40` / C `0x10b7200`, StoreCommit
S `0x83f6e0` / C `0x10baca0`; full raw serializer ABIs are unqualified (see [build.md](build.md)).
Client PoolBuilder `0x3cca80`, compare `0x3ccd97`, fatal `0x3cd60f`, cache stride 0x610 / count `+0x600`
were independently rechecked; no callable PoolBuilder ABI or complete cache-reader audit was recovered.
The dossier contains pool-partitioning/format-expansion designs, not evidence that the byte namespace or native
selector has already been widened.

### 1.2.2 Tool-specific presentation seams (client 26430625) **[dec]**

Independently re-read from the read-only client `5d9ef2f1…` with targeted Ghidra decompilation (`-noanalysis`,
function-local disassembly) and LLVM disassembly. No client execution was performed.

- `0x40e5d0` is `void(selectorState, access, bool currentOnly)`. It gathers inventory records, then building-stock
  records. **Where Kiln hooks it (since 0.2.0-dev.31.13): inside, never at the entry** [dis]. Its prologue ends at + `0x23`
  (`lea r15,[rcx+0x638]`, 7 bytes; rcx/rdx/r8b still the caller's arguments), its one `ret` follows the epilogue that
  starts at + `0xa8a` (`mov rbx,[rsp+0x1d0]`, 8 bytes) after the finalizer's call at + `0xa76`; nothing branches into
  either site's bytes or past + `0xa8a` (the four switch tables included; the only case past the `ret` is the fatal
  default), rsi holds the groups from + `0x3b` on and rsp does not change between the two. kiln-tools
  (`tools/MaterialSelectorBody`/`Done`) and kiln-items (`build/client.MaterialRowsBody`/`Done`) put a mid hook on each and
  pair them by rsp (`native/core/abi/kiln_mid_frames.hpp`). Building Tools 0.34.0 finds the function by its first 35 bytes,
  checks + `0x194`, + `0x78f`, + `0x867` and the calls at + `0x705`, + `0x721`, + `0x876`, + `0x89d`, then redirects the
  callers and the finalizer's call at + `0xa76` for its full material list and partial area actions; until 31.13 both
  services detoured the entry and that lookup failed (XHL, 2026-11-25). `native/services/tools/tests/presentation_sigdb_test.py`
  checks all of it on the client. Its seven selector groups have stride **0x648**, not the separate player-ui pool cache's 0x610. Each group
  has data/count/capacity at +0/+8/+0x10, 24-byte rows with item +0 and aggregate stock +0x10, selected index +0x628,
  has-selection byte +0x630, and mode +0x638. Finalizer `0x40dfe0` restores a selection by item identity. Filtering a
  group must remap the selected index or clear it; retaining an index after compaction selects another material.
  Only terrain group2 is filtered. Root/brush category `Blueprints` (10), equipment `BuildTool` (3) and vanilla terrain
  pool GUID `256c43ba-dd20-4369-b3bf-f49c671a59ec` select group2 before Overgrowth flags. The runtime map is keyed by
  pool ID, not equipment slot; a second row repeating terrain items adds magic stock again even with vanilla held.
  Tool pools therefore stay policy metadata and leave the game-visible pool resource unchanged. The parent
  `0x40e2b0` reads this call's resolved held ItemInfo from `access+0x78` for category routing; its caller publishes
  that pointer alongside SlotSelection. Guarded root agreement with this per-call item prevents a prior Post sample
  from filtering a newly selected vanilla tool in the same client step.
- `0xb1a230` is an **item presentation constructor**:
  `out = constructor(out, ItemInfo*, selectorGroup*)`. The traced direct caller obtains its item from the selected
  hotbar model (`0xd43f40`), not necessarily the running action's `actionItem`. Hover provenance is not established.
  Its row view is +0x28/+0x30. A separate owned
  `SmallDynamicArray<24-byte row,64>` starts at +0x40 (data/count +0x40/+0x48, inline rows +0x68). Preview rows hold
  item/count at +0x10/+0x14. It swaps its local array into that destination with `0xd90570`, but the view may still
  name the old stack buffer. Rebind the view to the destination's owned data after the original returns, then bound
  and filter that owned data. Never cache this constructor's item as evidence of what an in-flight action holds.
- `0xd25b50` (corrected 0.2.0-dev.30.7, kiln#130) does **not** scan the runtime item registry: it takes the list
  `ItemRegistryResource.terraformingTools` (resource +0x20, BlobArray<ItemId>) through `0xe9f0f0`, whose only caller it
  is (`[registry+0x20]` is the resource, `registry` the object at `+0x4667b8` that `0xe94470` looks items up in). The game
  data lists 21 ids there: the Overgrowth and Decay tools and their improved forms, rakes, pickaxes, the gold sieve and
  the six Overgrowth/Decay shapes. Per id it looks the item up (`0xe94470`), skips it when its tag BlobArray at +0x5e0
  (24-byte rows: GUID, nominator, denominator) holds the exclude GUID, keeps it only when it holds the include GUID (a
  zero include keeps all), copies the ratio GUID's nominator/denominator, and appends a 72-byte row `{u64 0, u32 item
  +8, zeros, nominator +0x40, denominator +0x44}` to the caller's output (growing it through its callback, a fatal
  error when there is none). It ends by sorting the output ascending by nominator/denominator (`0xb0b110`). A runtime
  item (a clone in the registry map) is therefore never a row, whatever its tags. Vanilla Overgrowth shapes carry tag
  `08aea6cf…` at ratio 1, 2 and 3. The four callers: `0xc3fe18` (frame drawer: Overgrowth branch include/ratio
  `08aea6cf…` exclude `918b8e6c…`; Decay branch include/ratio `851efd4c…` exclude `ac4e9069…`), `0xd180a0` (Decay
  tags), `0xd20190` (Overgrowth tags), `0xd6aa05` (zero include, exclude `3c68fe37…` (the rakes' tag), ratio
  `db153a96…`). Only the `0xc3f210` frame drawer's call at **0xc3fe18** is adapted:
  a unique pattern qualifies the instruction and its rel32 target. A checked MID requires the selected hotbar ID
  `[RBP+0x118]` to be the held family's root or one of its brushes (root only until 0.2.0-dev.31.20: after a pick the
  slot holds the picked brush, see the pick below, kiln#153) and the resolved ItemInfo `[RBP+0xe0]` to name that same
  item. The Overgrowth branch has
  `RBP=RSP+0x100`, output `RCX=RBP+0x690` (fresh empty capacity16 array), include `R9=RSP+0x30`, ratio
  `R8=RBP+0x100`, exclude `RDX=RSP+0x40`. The include/ratio GUID **values** in these distinct caller-owned
  temporaries are replaced, and (since 0.2.0-dev.30.7) the family's rows go into the output's inline storage
  (`RBP+0x6b8`, 16 rows of 72 bytes, up to `RBP+0xb38`), its count (`RBP+0x698`) written last. After the call the
  drawer copies the output's data pointer and count into a 0x68-byte entry of its own list at `RBP+0x248` and draws
  from that view; nothing outlives the frame, so a later vanilla frame starts from a fresh empty output again. Other
  three query sites, the Decay tuple and persistent callback outputs stay untouched. Nothing reaches those three on
  this build (raw scan of the client, review of kiln#153): `0xd18070` and `0xd20160` (wrappers that pass the caller's
  output with the Decay or Overgrowth tags) and `0xd6a670` (its mode-2 branch at `0xd6aa05` only counts the non-rake
  tools, for a formatted text) have no rel32 or rel8 call or jump, no VA64 and no RVA32 but their `.pdata` rows. The
  frame drawer (called only at `0xbed3ba`) is the one list of brushes the client draws and picks from.
  There is no inherited TLS admission. `RawHook::caller()` alone is insufficient: its per-target caller record is not
  restored after a recursive predecessor. A write fault skips this one empty-output query, never a half-patched query.
- **The pick** (client 26430625, server `f0d824be…` 26430253, read-only disassembly, review of kiln#153). The UI's
  command dispatcher `0x85c7f0` posts a 24-byte UI event (at `0x85d3a6`: player +8, actionbar slot byte +0xc, item
  +0x10); the client's input handler `0x3af670` reads that stream for the local player and at `0x3b0d27` writes
  `createBuildingItemAction` (`CreateBuildingItemAction {versionData, selectedIndex, itemId}`, `ClientPlayerInputData`
  +0x1f4/+0x1f8/+0x1fc) from it. The host's `inventory_actions` (S `0x1a5af0` / C `0x4047f0`)
  takes it when `PlayerInput` +0x1fc differs from `ServerConsumedPlayerInput` +0x30: the item (+0x204) must resolve
  through the item lookup (S `0x93b5b0` / C `0xe94470`, the registry map that holds runtime items too) and its ItemInfo
  flags (+0x5d8) must hold `IsBlueprint`, `IsBuildingScrewdriverTool`, `IsTerraformer` or `IsNpc` (`ItemFlag` 3, 6, 7,
  8). CreateBuildingItem (S `0x1a0010` / C `0x3fed30`) then requires the slot below 16 and, when the slot holds an
  item, that item to carry one of those flags too; it does nothing when the slot holds the picked item already, and
  otherwise puts one of the picked item in the slot in its place. No tag, no `terraformingTools` list and no family is
  checked, so a dedicated server takes a family brush (a clone of `Building_Overgrowth_Shape_Small`:
  `IsBuildingOvergrowthTool`, `IsTerraformer`) as a host does, and from then on the slot, the frame drawer's selected
  hotbar item and the held item are that brush.
- Item tags (0.2.0-dev.30.7) are no resources: the game data cooks no `keen::ItemTag` (`{label}`), and no resource has
  a tag's GUID. `ItemRegistryResource.itemTags` (+8, BlobArray of 20-byte `ItemTagInfo {tagGuid, labelId}`) lists the
  210 tags the game knows, every tag an item carries among them (many with the empty label `0x69691905`). The readers
  seen (`0xd25b50`, the pickup notice's `0xd53370`) compare GUIDs.
- `equipment.terrainVoxelObject` (`ItemInfo+0x468`, `ObjectReference<keen::TerrainVoxelObjectDefinition>`, an empty
  editor type): the 11 game items that set it (terrain blueprints) name a `keen::VoxelModelResource` of the same GUID
  with `isTerrain` set, as `voxelObject` names one for a shape (the 144 items with a `voxelObject` name 67 models,
  every one with `isTerrain` clear; the 11 terrain models are named only by `terrainVoxelObject`). Vanilla Overgrowth
  shapes set neither; their radius is their own `FloatImpactConfig` `0x5258131e` (0.5, 1.5, 2.5).
- `0xd3e130` writes a 16-byte root identity `{itemId,nameTag,descriptionTag,present}`. It substitutes vanilla root IDs
  based on ItemInfo flags +0x5d8 (bits 3/4/5/6/8). A Kiln root can instead use its own identity and content text tags:
  the existing `kiln.item.name` / `kiln.item.description` GUID derivation, verified in the content writer. No game
  text pointer needs to escape this helper.
- `0xe9f270` is **terrain-config lookup**, not player material selection: it scans the registry's +0x38 resource,
  comparing the cell byte in **184-byte** rows. Its server equivalent is `0x9464b0`. The adjacent lookup
  (`C 0xe9f210`, `S 0x946450`) is a value/default helper over the same terrain rows, not the building lookup.
  It shares the 184-byte loop prefix; discovery must extend through the pointer/null return to distinguish them.
- Equipped-stack helper `C 0x3853c0` / `S 0x1444a0` has matching unique normalized prologues. The client decompile
  returns a 20-byte inventory binding/item/count/flags value. Its MainHand path first uses nonzero cached action item
  at SlotSelection+0xb0, otherwise selected hotbar index+0xc0 (<16), inventory entity+0xa0/+0xa4 and a 12-byte stack
  whose count+4 must be nonzero. The native client-Post producer copies complete 216-byte SlotSelection and 96-byte
  Inventory components through the shipped GetComponent ABI, resamples selection/session, then publishes only owned
  scalar values tagged by owner, actor, generation, session and step. Selector hooks accept at most one step of age;
  world events and missing or changed state clear the observation. No preview identity becomes held state.

The qualified material lookup may refuse a non-pool cell only while an Add policy is scoped around the native call.
Outside that scope it must call the vanilla continuation unchanged. The per-player producer separating backpack and
magic-chest observations remains missing: aggregate selector counts must not be represented as separate-source data.
Static qualification and portable fixtures do not establish actual menu rendering or real-game behavior.

#### HUD ammunition and spell lists on the same selector (0.2.0-dev.30.6, `kiln.ui.ammo`) **[dec]**

Static qualification on the exact client 26430625 (`5d9ef2f1…`), read-only; no client run. Served by kiln-tools as
`kiln.ui.ammo` (`native/core/abi/services/kiln_ui_ammo.h`; managed `Ui.AmmoList`).

- The existing builder `0x40e5d0` is extended (no second hook on it; its two halves are mid hooks inside it since 0.2.0-dev.31.13, 1.2.2). Mod rows enter only as owned copies of a
  published snapshot; the builder never calls managed code.
- The raw finalizer `0x40dfe0` (checked) keeps a provider row's item identity after the parent's re-finalization, so
  the selection stays on its item when rows change.
- The raw pick `0x40c150` (checked) is where the native selection wishes converge: the actions `0x40cf00` and the cycle
  `0x40c320` read the selected index at +0x628, and seven recovered direct wish sites all reach `0x40c150`. For a
  virtual row (a mod row with no backpack stack) it suppresses the `inventoryStockCycleAction` write (version +0x98,
  item +0x9c) and the local pending selector (+0x63c / +0x640), then queues a pick for the publisher. Automatic
  selection (the equipped item absent from a replaced list) also goes through it, so a pick is a selection wish, not a
  click.
- Item catalog: lookup `0xe94470` -> the unique captured rel32 map `0xe86770` (a bare generic-map prefix has 16 hits; the
  target comes from the unique `ItemLookup` signature). Only `ItemInfo` category 8 with subtype 1 (arrows) or 2 (spells)
  is admitted for the list of that kind; any other item is refused at publish.
- Backpack membership (is a row virtual?) comes from the stable client ECS snapshot: `InventorySetup` (84 bytes; linked
  entities +0, categories +64; Generic 4, Virtual 5) and `Inventory` (96 bytes; eight 12-byte stacks, id +0, count +4).
  A read failure suppresses the wish without an event. A row the game listed itself with a non-zero count is never
  virtual, in either mode; one it kept at count 0 (see the next point) is decided by the backpack (since
  0.2.0-dev.30.7).
- Rebuilding a group, vanilla zeroes the count of every row it keeps and adds stock to rows with a matching item; it does
  not remove unmatched rows, and the cycle `0x40c320` does not check a row's count. Kiln therefore keeps a group's
  binding across a session or world change until that group's next build takes the mod rows out (picks of them stay
  suppressed meanwhile, with no event).
- Rows and picks in time (0.2.0-dev.31.10, GitHub issue 145; static, same client). All seven wish sites pass the selector
  state at `+0x14fd0`, the one the parent `0x40e2b0` rebuilds in full (`currentOnly` 0) on each call from `0x2bd0bc`; the
  second state at `+0x17bd0` is built for the active group only or copied from the first. In that caller the event-loop
  wish at `0x2bce24` runs before the build at `0x2bd0bc`, so it sees the previous build's rows; the order of the input
  wish sites (`0x3a9612`, `0x3ade44`, `0x3afa75`, `0x3afb02`) against the build is not established. The builder counts
  stacks in the `Generic` (4) links `0xe882d0` collects from the actor's `InventorySetup` (recursing into `Virtual` (5)
  links), the same graph the backpack read walks. The equipped arrow or spell is a selected `Generic` backpack cell
  (the #139 notes in [items.md](items.md)), so its game row counts it and the backpack read finds it. A binding's rows can
  therefore be older than the backpack at a pick: once per set of bound rows Kiln reads the backpack (one walk) for the
  game rows with stock whose item the publication provides, and a provided row held then and absent at the pick goes to
  the mod. The answers carry over only while the rows stay the same: new rows are read again at their bind, also when a
  count only fell (an arrow shot), since the stack the read found may have left while one the reader cannot see came in
  or stayed (issue 127's case), and a held answer carried over would take that row's pick from the game. Since
  0.2.0-dev.31.12 `Ui.AmmoList` in `Append` publishes a mod's row of an item the game lists (with stock or at 0) behind the
  game's row, which `compose_ammo` keeps in its place (game rows win duplicates in `Append`, and take the room), so
  these reads and decisions cover its rows too (one walk per new set of rows, so one per arrow shot while a mod lists
  that arrow, as in `Replace` since 0.2.0-dev.31.10). When the game's rows change, Kiln binds an `Append` publication only once the provider publishes for the new serial,
  so the game's own rows show for that interval; a pick of a provided item there is decided by its row as the group
  shows it at the pick: with stock the game's, at count 0 as for a bound one. A `Replace` publication is bound over the
  newer build in the same client session, generation, player and held item until the next publication (since
  0.2.0-dev.31.21, GitHub issue 151), with the backpack read of that build's rows and Kiln's selection, at most 1 s
  from the first such build; past that the game's own rows show and are decided as above. `0x40ca70` (equip reconciliation) returns the
  pending item `+0x63c` while its version `+0x640` differs from the input's, else the equipped stack's item via
  `0x3853c0`. Which pick emptied the reporter's slot, and the server's order of the mod's swap and the game's wish, are
  runtime facts not established here.

- How the list shows again after a pick (since 0.2.0-dev.31.12, GitHub issue 140; static, same client, read with
  objdump; tests `native/services/tools/tests/ammo_sigdb_test.py` (`test_hud_reveal_chain`) and `ammo_reveal.cpp`).
  The game's pick does not show the list itself; the host's answer does:
  1. The user's wishes come from `ClientPlayerInput.clientOnlyPlayerInput.inventoryStockSelection` (+0x5a4 +2:
     `SelectionInput {isAbsolute +0x5a6, selection +0x5a7}`) in the input handler `0x3af670` (the client frame calls it
     at `0x87a94d`): an absolute selection picks that row (wish `0x3afa75`, return `0x3afa7a`), a relative one cycles by
     it (`0x40c320` at `0x3afabf`, wish `0x3afb02`, return `0x3afb07`). The pick has seven direct calls; the other five
     are the game's own: `0x2bce24` (return `0x2bce29`, player_ui's `SelectInventoryStockItemEvent`), `0x2b4846` (after
     `0x40cf00`), and `0x3afcce` in the same input handler (return `0x3afcd3`), the building tool's
     `buildingSelectMaterialFromCrosshair` (ClientOnlyPlayerInput +0x30, `ClientPlayerInput+0x5d4`), which picks the
     aimed-at block's material only when its item's category is the held tool's group; `0x3a9612` and `0x3ade44` sit in
     `0x3a95a0` / `0x3addd0`, which no code calls or jumps to and no pointer names (only `.pdata`/unwind data), so on
     this build they are not a controller path; if they ever ran, their picks would not show the list (the state before
     this fix). Which devices fill `inventoryStockSelection` was not read. The host answers every consumed action
     alike (step 3), so in the game the automatic sites show the list for the game's rows too; for mod rows Kiln shows
     it only for the user's two sites.
  2. The pick `0x40c150` writes `inventoryStockCycleAction` (`ClientPlayerInputData` +0x98 version, +0x9c item) and the
     pending selector; nothing on the HUD.
  3. On the host, `player_control_action_sequence` (`0x2dcca0`, its descriptor `0x141e1b2d0`) calls
     `0x2cb090` at `0x2ddf5f`: when `PlayerInput.fromClient`'s version (+0xa0 = 8 + 0x98) differs from
     `ServerConsumedPlayerInput.consumedInventoryStockCycleAction` (+0x3c) it equips the item and sends
     `UiInventoryStockCycleEvent` (type `0xE2267831`, 16 bytes: `playerEntityId` +8, `selectionResult` +12; `0x2cb39c`,
     the only code that sends it), then stores the version.
  4. The client's `player_ui` (`0x2b8430`, sys-write `G38UiPlayData`, which is `rsi` from `0x2b8588`) reads 14 event
     streams, the last 0x70 bytes of its 0x3c8-byte table at `rbp+0x70` in the order of its descriptor (`0x141e259a0`):
     the eighth, `UiInventoryStockCycleEvent` at `rbp+0x400` (`0x2bd0c1`), sets `G38UiPlayData+0x1a990` to
     `selectionResult` and adds one to `G38UiPlayData+0x2fe8` (`0x2bd19a`) for each event of the local player. The
     same `rsi` passes the groups at `+0x14fd0` (`0x2bce0f`), so the count is `groups - 0x14fd0 + 0x2fe8`. By displacement
     the count has one writer in `.text` (that `inc`) and one reader (step 5; the other `+0x2fe8` hits are stack slots
     or other objects); `+0x1a990` is written there and read nowhere.
  5. The HUD's watcher `0xb34ce0` (`rdi` = `G38UiPlayData`, `rbx` = the UI data `[[0x14283f4a8]+0xd0]+0x10`) keeps one
     record per watched value (time +0, a skip-zero flag +8, first +9, last value +0xc). At `0xb35d7d` it reads the
     count; when it differs from the last value it stamps `rbx+0x31b48` with the UI clock (`0xa4bd40`) and plays the
     UI sound `[[0x14283f4a8]+0xc8]+0x12d10` (`ui/UiPlaySound` `0xa4f310`).
  6. The list draw `0xbe5150` (called at `0xbed05b` with the active group, `G38UiPlayData+0x17bc8` = groups +0x2bf8,
     none at 7) fades the list from the latest of its own change time (aim states), `rbx+0x31b38` (the watcher
     record at `0xb36836`, of a byte of the player's component +0x6b34) and `rbx+0x31b48` (`0xbe603f`..`0xbe6059`), with 0.5 s and 1.5 s (`0xbe6083`, `0xbe6090`)
     through `0xb2adf0`, unless the HUD mode `+0x1db1` is 0xb. The earlier anchors `0xbe5f5a`/`0xbe5f9f` are the
     draw's own stack slot (`rbp+0x9c8` is the home slot of its second argument), not a stored timer.

  So a pick Kiln keeps from the game sends no action, the host sends no event, and the list stays hidden. For a
  synthetic pick that reaches the provider from return `0x3afb07` or `0x3afa7a` (`pick_hook.caller()`), and only when the
  play data's active group is the pick's kind, kiln-tools adds one to the count, as player_ui does for the host's event:
  the HUD's own watcher, timeout and active-list checks do the rest. It writes neither +0x1a990 nor the input or pending
  selector, and nothing for the game's automatic wishes, stale or unreadable picks, or picks the game equips (the host's
  event shows those). sigdb: `ui/AmmoCycleReturn`, `ui/AmmoSelectReturn`, `ui/AmmoEventWishReturn`, `ui/AmmoCycle`,
  `ui/AmmoCycleEventCount`, `ui/AmmoCycleWatch`, `ui/AmmoListRevealRead`; constants `ui/ammo.groups` (0x14fd0, three
  captures), `ui/ammo.cycle_count` (0x2fe8, producer and watcher), `ui/ammo.reveal_time` (0x31b48, watcher and draw);
  feature `ui.ammo.reveal`, which kiln-tools requires as a whole (the watcher's stamp and the list draw's read of it
  too, not only the names it reads), so a build where the draw no longer fades from that time never gets the count
  changed (no lone UI sound). When it does not resolve, picks work as before and `kiln.log` says once that mod rows do
  not show the list again. The write takes no access the game does not take on the same call: the pick it stands in
  for writes the same `G38UiPlayData` (the group's pending selection +0x63c/+0x640) there, and `0x40c320` before it
  the group's index. Should it still overlap player_ui's `inc`, at worst two changes merge into one reveal (the watcher
  only compares for a change; an aligned 4-byte store does not tear). Not established: whether the client and the HUD
  run on one thread in every session (the record of this build saw the client world on the main thread), and the
  event's delivery to a joined client.

- What moves the cursor between builds, and how the HUD list draws it (since 0.2.0-dev.31.23, the flicker GitHub issue
  151 still showed after 0.2.0-dev.31.21; static, same client, read with objdump; test `ammo_sigdb_test.py`
  `test_selection_sync_and_list_draw`, host tests `ammo_hook.cpp`). The report: with Grimoire's `Replace` rows the picker
  moved to the new spell, showed the previous spell for a moment and went back, a little in a hosted world (most in the
  first switches after a start) and more on a dedicated server; Grimoire returned the same rows every time and the
  server equipped each pick once.
  1. The ECS system `client_cycle_inventory_stock` (`0x2b4390`; descriptor `0x141e23120`, its name at `0x141e25c20`;
     reads CspHackInventoryStockSelection, NetworkFishing, NetworkRangedWeapon and SlotSelection, writes
     ClientCycleInventoryStock and ClientPlayerInput, with G38UiPlayData among its resources) runs for the local player
     (`0x3bb740` at `0x2b4415` compares the entity) and calls the selection action `0x40cf00` at `0x2b47e7` on the
     active group of the play data's first selector state (`[rbp-0x20] + 0x14fd0 + active * 0x648`, none at 7,
     `0x2b4793`). That is the action's only call. Only when the action returns 0 does the system pick the cursor's row
     itself (`0x2b484b`: the automatic selection of the notes above).
  2. The action `0x40cf00` asks the reconciliation `0x40ca70` (`0x40cf45`) for an item, finds its row (`0xe88660`,
     `0x40cf59`) and, when the group lists it, sets the valid flag `+0x630` and the index `+0x628` and returns 4
     (`0x40cf65`..`0x40cf8a`). This is outside any list build, so Kiln's two finalizer paths never saw it.
  3. The reconciliation returns, for arrows, spells and bait (categories 0, 1, 6), the group's pending item `+0x63c`
     while its version `+0x640` differs from the category's entry of its second argument, else the equipped stack of
     slot `0xe + category` (`0x3853c0` through `0x40c980`; `0x40cacb`..`0x40cb10`). That argument is
     `CspHackInventoryStockSelection.versions` (`StaticArray<VersionedDataVersion,7>`, 28 bytes): the server's
     `0x2cb090` stores each consumed stock-cycle action's version as `consumedInventoryStockCycleAction`
     (`0x2cb3d4`) and in `versions[category]` (`0x2cb423`), and the client gets it with the component. So the game's
     own pick (which writes the pending item and version, `0x40c190`/`0x40c19e`) keeps its cursor until the server
     consumed it: the game's client-side prediction. A pick Kiln takes for a mod row writes neither, so the action put
     the cursor back on the equipped stack (the previous spell) until the mod's swap reached the client; the next
     build's entry took that selection as its cursor (`Frame`), and once the swap landed the action named the new spell.
     The window is the pick-to-equip time: in Fuzen777's Grimoire trace 78 to 141 ms for the first five picks after a
     start (median 94) and a median of 62 ms later, plus the network round trip on a dedicated server. Why the first
     swaps of a session take longer is not established here (the mod's and the game's first-use costs are candidates).
  4. The game's list build has no other trigger to find: player_ui calls the parent `0x40e2b0` at `0x2bd0bc` on each of
     its runs, in a hosted world and on a dedicated server alike, and rebuilds the first state in full; only the rows'
     content (the list revision) changes, when the swap's inventory change reaches the client (in the same process for a
     host, a round trip later for a joined client).
  5. The HUD list draw `0xbe5150` (called at `0xbed05b` with the active group of `+0x17bc8`, none at 7) keeps a widget
     state of 0xe0 bytes (`0x1411a06e0` with the id `0x63d31e0f4d77fc72`, `0xbe5419`; `+0x10` zeroed when new,
     `0xbe5466`). It draws index = the group's `+0x628` when the valid flag `+0x630` is set, else the state's `+0x10`
     (the remembered index), and stores that index in `+0x10` (`0xbe6234`..`0xbe6269`). The highlight goes only to that
     row when the flag is set (`0xbe68e0`), the scroll target adds the rows before it only then, and the list scrolls to
     its target with exponential damping (`0xa42e80` at `0xbe69ec`, rate 20 per second at `0x141527f98`), so a cursor
     that is back for a few frames shows as a short movement to the previous row and back. Kiln's rows never draw the
     remembered index while the cursor's item is listed; one Kiln path could (a new publication without the cursor's
     item, written with no selection), and since this change it takes the game's selection instead.

  kiln-tools now holds a mod row's pick as the game's prediction holds its own: `ui/AmmoSelectAction` (a raw hook on
  `0x40cf00`) puts the pick back after the action while the action's choice is another item, and a build's entry uses it
  over what the group shows, from the pick until the action itself chooses it (result 4), a pick of that list goes to the
  game, the kind is cleared, its owner retires, the client session, world (generation) or player changes, or 1 s
  passes; only while a binding of the group exists. Another held weapon keeps it: the reconciliation's slot is
  `0xe + category` (`0x40c980`), the player's, and the game's own pending pick (in the group) holds over a weapon change
  as well. `ui/AmmoListDraw` is hooked only by the opt-in pick trace (docs/kiln/ui.md). sigdb features `ui.ammo.cursor`,
  `ui.ammo.trace`. Not established statically: the order of `client_cycle_inventory_stock`, player_ui's build, the input
  handler (`0x3af670`, called from the client frame at `0x87a94d`) and the HUD draw within one frame, and so whether
  the previous spell was drawn for every frame of the window; the trace's `sync`, `build` and `draw` lines settle it.

Real menu behaviour, the server half of a swap and controller cycling remain the owner's in-game check.

### 1.3 Hardness **[dec] [data] [rt]**
```
config   = registry lookup by id               0x9464b0 terrain, 0x933af0 building: a linear scan over the resident resource, stride 184 / 180
damage   = setup[hardness]                     soft, slightly, moderately, hard, veryHard; Unbreakable (5) gives error 6: no dig
         × miningDamageFactor                  0x93df20, server setting +0x30
         × classMod                            config.damageSusceptibility: Wood, Stone, Metal mods of DamageTerraformingRuntimeSetup; None 1.0
table[id] = config.healthPoints / damage       0x22c970, 0x229400 terrain 1..127, 0x2292f0 and 0x227bc0 building 128..255
```
- The scan reads the resource on every dig, and no table is cached across digs. A config added to the resource is found by id. **[dec]**
- One `terraforming` impact runs the terrain kernel and the building kernel over the same box (`0x2284a0`, `0x227bc0`). So a pickaxe damages both, by each one's table.
- Tools and explosives are data: `impact::DamageTerraformingSetupConfig` in `ItemInfo.impactValues` (29 configs on items) or a template's `ImpactConfiguration`:

| Source | Soft | Slightly | Moderately | Hard | Very hard |
|---|---|---|---|---|---|
| Stone pickaxe | 60 | 50 | 35 | 20 | 5 |
| Iron pickaxe | 100 | 100 | 95 | 60 | 50 |
| Celesthium pickaxe | 100 | 100 | 100 | 90 | 80 |
| Powder ball T1 | 2000 | 1500 | 1000 | 750 | 500 |

The heavy pickaxe swing is a second config at 7×.

- Building hardness and health in the data: Rough Wood `SlightlyHard` 150, Rough Stone `ModeratelyHard` 250, Granite Block `VeryHard` 1000, Iron Block `VeryHard` 2000, Gold Block `VeryHard` 5000. Terrain health is much lower: dirt 2..5, stone 5, ores 8..25. So a block is 30 to 1000 times the terrain it replaces.
- **What digging gives back** is the config's `materialItem` per `blocksPerLootItemExchangeRate` blocks, and for the game's building materials that is the main raw input of the block's recipe at the recipe's ratio: Rough Stone is made 100 from 2 stone and gives a stone per 50, Rough Wood 100 from 2 logs and a log per 50, Moonstone 100 from 10 moonstone and one per 10, Fancy Stone 100 from 5 limestone (and 5 flint) and a limestone per 20. None of the 85 configs names its own block item. **[data]** Whether a base's undo records give blocks back some other way wasn't read.
- **Bases.** The zone rule is on the player, not on the wall. A player's own impact (the node's `playerId`) needs permission bits (`0x163040`: bit 8, and bit 4 inside a zone) and, in a zone, a per-player undo record (`0x230fb0`), or it does nothing. An impact with no player skips all of that and digs anywhere. **[dec]** The game's own `Terraform_Explosion_TNT_T1`, spawned by the lab inside a base zone, removed 504 of 729 cells of Rough Wood **[rt]**. So base walls take damage from anything unowned, as far as its numbers reach.
- **No structural rules.** No support, stability or collapse type exists in reflection; a block stays where it is. Props have `AttachToSurface.detachAction` (None, Destroy, AddGravity). **[refl]**
- **Unbreakable** materials never get a table entry, and are excluded from dismantling (`0x22d640`, `hardness < 5`). **[dec]**
- **Enemies and explosions.** 36 templates have `Terraforming`. The program 14 of them share, `ea4abbf1`, passes no player to the node (`terraforming(_, ...)`). A zero damage leaves a category untouched, so the numbers decide:

| Template | Soft | Slightly | Moderately | Hard | Very hard |
|---|---|---|---|---|---|
| `Terraform_Fogger_Heavy_Strike` | 45 | 45 | 0 | 0 | 0 |
| `Terraform_Enemy_Fogger_Monstrosity` | 200 | 100 | 0 | 0 | 0 |
| `Terraform_Explosion_Meteor` | 3000 | 2500 | 2000 | 1500 | 100 |
| `Terraform_Enemy_Scavenger_ProximityMine_Explosion` | 4000 | 3500 | 3000 | 2500 | 100 |
| `Terraform_Explosion_TNT_T1` | 3000 | 2500 | 2000 | 1500 | 25 |

- Damage below a cell's removal is kept by `ServerVoxelDamageSystem`, replicated by its own message (`[S][voxel_damage]`), drawn as decals (`buildingBlockDamageDecalVfx`). **[dec]**

Other users of the registry read friction, jump costs, submerge and path node type from the same config: `actor_jump`, `locomotion_*`, `movement_collider`, `enemy01` and more (22 programs). **[data]**

### 1.4 Surfaces: textures, slots, limits **[data] [spv]**
Building materials are not terrain texture arrays or tri-planar arrays. Each surface is a slot in a structured buffer, sampled **bindless**.

`BuildingMaterialBlendingResource {layerCount 85, renderingDataBlock}`:
- `RenderDataBlock {typeSignatureHash, data, patchPoints, textures, textureFeedbacks}`; `data` = 50,176 B = 128 × `BuildingMaterialBlendingMaterialLayerShaderData` (392 B). Slots 85..127 are zero.
- A slot is 6 layers of 60 B: `baseSide`, `baseTop`, `baseBottom`, `edge`, `edgeTop`, `noise`. A layer has handles for `emissive`, `albedo`, `metallic`, `roughness`, `reflectance`, `normal`, `height`, `ao`, a feedback handle, and tiling, displacement scale and offset, emissive numbers. After the layers: height blending ranges, `edgeBlendingCurve`, `edgeOnlySide`, `noiseBlendRange`, `bevelRadius`, `isRoof`.
- 4,102 patch points write 3,596 textures and 506 feedback lists into `data`. A slot uses 48 to 54 of them. An unused map is a shared 16 × 16 constant texture.
- Formats: BC7 sRGB albedo, BC5 normal, BC4 for roughness, metallic, reflectance, height, AO, BC1 sRGB emissive. Mostly 1024 or 2048, some 4096. 7.3 GB of pixels, 1,048 distinct blobs.
- `RenderStreamedTexture`: the blob holds the whole chain, largest level first; `mipChainContentRanges` as for VFX textures (docs/re/vfx.md 2.7); `mipTailContent` a second blob of the smallest levels (1,392 B at 2048). `content.category` 1013777168, tail 1185060183, `bundleId` per material, `sortKey` per map.

`GiVoxelBuildingMaterialResource`: 128 × 180 B (`top`, `side`, `bottom` of 60 B: albedo, roughness, emissive, tiling and an albedo and an emissive map) for global illumination. `materialCount` 85.

`BuildingMaterialParametersResource`: 85 × 12 B `{mapping Default | Wood | Roof, edgeOnlySide, decoObjectRules}` and 1,989 models for edge decoration. It's an array, not a fixed block.

**The shaders use a count, not a size.** In `FillGBufferIndirectBuildingFront`:
```
index = (word >> 21) & 127
if (index < constants.buildingMaterialLayerCount)   // BrickFillGBufferConstants +4
    material = g_buildingMaterialBlendingData[index]
else
    defaults: the Phi takes constants, roughness 1
```
The same buffer (stride 392) is in 17 pipelines: the building and roof fill passes, `BrickSDF`, `BrickVoxelMaterialCopy`, `BrickMaterialCount` and the tessellated building and roof chunklet passes. Only the fill pass was read for the bound check. **[spv]**

The renderer resolves all four tables once at creation, by fixed GUIDs (client `0xf4790`). So an override must be there at boot. **[dec]**

**In a client [rt client]** (client 26430625, an offline local world). Overrides of `BuildingMaterialBlendingResource`, `GiVoxelBuildingMaterialResource`, `BuildingMaterialParametersResource` and `TerraformingEfficiencyRegistryResource`, served by kiln.assets at boot, put slot 84's maps and numbers (moonstone, id 212) into slot 85 (id 213) and raised the counts to 86. Then three 2 m cubes were written next to each other:

| Cells | Drawn |
|---|---|
| 212, the game's moonstone | Veined crystal surface, crystal edge decoration, blue veins |
| 213, the free slot | The same as 212 |
| 214, past the count | Flat grey stone brick, no edge decoration, no bevel: the fallback |

No crash or device loss, the game ran on for two minutes, and kiln.log had no error lines. The cells read back as written.

**Terrain** is tighter, by layers rather than ids: `WorldMaterialBlending2Resource` has per-id `layerStartIndexAndCount` (128 slots), 224 layer slots of which 222 are used, and 768 texture slots of which 499 are used. Terrain materials blend per biome and mode. A new terrain id that reuses other layers fits today; one with its own layers can use two, unless the buffers grow, which wasn't tested. **[data]**

### 1.5 Saves and what a removed mod leaves **[dec] [rt]**
- Save: `0x83d200` writes `materialGuids[256]` (0x1000 B) in front of every terrain blob; the table comes from the world's resource (`world+0x1bc`).
- Load: `0x830e30`, for blob versions 2..6, builds `remap[256]`. Identity for a zero GUID; otherwise the id of that GUID in the world's map (`store+0x11c0`), or **0 when the world doesn't know it**.
- So ids are not stable data: moving a material to another slot is safe as long as the GUID stays in the table. A removed material's cells read back as `material 0, fill 0`: air, walls gone. See the lab results.
- Replication (`0x83ba40`) sends the store's blocks as they are, with no table. Client and server must agree on ids.

### 1.6 Terrain materials **[refl] [data]**
**Ids.** 1..116 are configured and have a GUID in `materialGuids`; 117..127 have neither: 11 free, a room apart from the building ids. No terrain id is
deprecated: the three without an item are unbreakable world materials (5; 16 with path type `DeadlyFog`; 34 `Lava`).

**`TerrainTerraformingConfig`** (184 B): `terrainMaterial`, `terrainItem` (the loot), `hardness`, `terrainPerLootItemExchangeRate`, `healthPoints`,
`knowledgeQuery`, `canBeFlattened`, `isSlidingMaterial`, `collisionFeedbackMaterial`, `materialFeedbackId` (dig and step sounds), `damageSusceptibility`,
`buffReference`, `conditionalBuffTypes`, `isDangerous`, `frictionFactor`, jump cost items, submerge depth and percentages, `entityTag`, `pathNodeType`.
- Diggable ground has 2..25 health (dirt 2..5, stone 5, ores 8..25); mud, tar and ice more. Ores are terrain: copper ore (26) is `VeryHard`, 8, class Metal,
  and gives `Material_T2_Ore_Copper` per 8 cells. Many ids share a loot item: five rubble ids give `Build_TerrainMaterial_T1_Rubble`.
- 56 ids are placed by an item in the pool `TerrainMaterials` (slot `TerrainMaterial`, category `Materials`, `placeVoxelMaterialId` the id, `isBuildingVoxel`
  false), with the game's terrain shapes. These items have no entry in `VoxelBlueprintItemRegistryResource`; only the shapes do.

**The look, client only.** `WorldMaterialBlending2Resource` (`layerCount` 222):

| Data block | Entries | Per entry |
|---|---|---|
| `bakingVoxelMaterialDataBlock` | 128, by id | u32 `firstLayer \| layerCount << 16`. 117..127: `222 \| 0` |
| `bakingLayerDataBlock` | 224 | 12 B: flags (`Surface`, `Underworld`, `Top`, `Bottom`, `Side`), top and bottom side mapping |
| `renderingLayerDataBlock` | 224 | 40 B: flags and blend layer count, `baseLayerIndex`, height blending, detail weight and opacity, edge smoothness |
| `renderingTextureDataBlock` | 768 texture layers, 499 used | 52 B: tiling, albedo, roughness, normal, height, AO and emissive maps, displacement, a feedback list. 2,569 textures, 3,068 patch points |

A material has 1 to 6 layers: one for every face, or top, side and bottom, each once on the surface and once underground. `SimpleWorldMaterialResource`
holds the same per id in a simple form: `layerInfosDataBlock` is 256 u32, two per id (surface, underground), `first | count << 9`, into 512 layer weights
(12 B) and 512 `SimpleVoxelPbrMaterial`s (60 B: albedo, roughness, emissive, metallic, reflectance, tiling, an albedo and an emissive map), 352 used. What
reads it, far terrain or GI, wasn't followed **[hyp]**. `VoxelMaterialVfxDataResource.materialType[256]` (both sides) gives 8 terrain ids a particle class
(mycelium, fog, lava, mud, tar, snow, ice, mud).

So a new id that looks like a game terrain material is three u32 copies and no new layer. Layers of its own need entries past 222 (2 free) and texture
layers past 499 (269 free); growing the 224 wasn't tested. A dedicated server ships the registry, the particle table, the world and the biome map, not the
look tables.

**Where terrain comes from** (corrected 2026-11, dev.28 RE, server 26430253 `f0d824be…` / client 26430625
`5d9ef2f1…`). **[static] [data] [lab]** Base terrain is baked, not generated from a new-world seed.
`VoxelWorldResource` `509feadb…` part 0 (part 1 is fog) names seven levels of content tiles. Level 6 is 80 × 80 tiles
of **256 × 256 cells** (128 m), each with 32 × 32 block-columns. Tile sizes range **132,098–5,306,764 B**, median
440,227 B; the old 150..360 KB estimate was wrong. Envelope is **two dimension bytes**, then u24 offsets relative
to payload: a finest header is 3,074 B, not 3,072. Levels 0..5 are coarser; edge dimensions can differ.
`voxelHashes` is 6,553,600 B (1,024 B per finest tile), `cpuDisplacement` 98,680 B; generation of these companions
remains incomplete. Streamed baked cells are overlaid by saved edited blocks; only edits are saved. See
[build.md](build.md) §9 for decoded format, native anchors and the three-boot replacement proof.
`BiomeMapResource` `509feadb…` is 1280 × 1280 at 8 m, a `keen::Biome` (nine enum values, eight present) per pixel,
identical on both sides. Changing these bytes alone does not change voxel materials or ground shape.
Biomes are baked into the cells: each biome's dirt is an id of its own (1 grassland, 10 wetlands, 11 steppes, 12 deep forest). The editor's per-biome
material types (`BiomeVoxelMaterial`) don't ship. **[data]**

## 2. Placed objects
Placing is `equipment.placedEntity`: an item names a template, the server spawns it (docs/re/build.md 6). Props persist as entities anywhere. Template families among 1,819 placing items: `Prop` 1,118, `None` 257, `Switch` 202, `Container` 71, `Plant` 63, `Animal` 39, `Factory` 30, `Npc` 28, `Water` 7, `Sign` 3, `Altar` 1. **[data]**

### 2.1 What a station is **[data]**
`T1_Drying_Rack_Factory` and `T1_Forge_Factory`, family `Factory`:

| Component | Job | Differs |
|---|---|---|
| `Crafting {workshop GUID, workshopId}`, `CraftingInteraction`, `ClientCraftingInteraction` | which recipes; props in range | id |
| `FactoryStation`, `FactoryStationClient {soundLoopRunning}` | the state machine, running sound | sound |
| `Inventory`, `InventorySetup {genericSlotCount, availableSlotCount, flags}` | 16 slots, inputs and outputs in one | |
| `InteractionLock`, `InteractionOfferRuntime`, `ClientInteractionOffer`, `ClientInteractionLock` | the prompt: `requiredPermission`, verb and sequence | |
| `ServerToggleState`, `NetworkToggleState`, `ClientToggleState`, `Extinguish`, `IsInWaterCheck` | on while running, out in rain | |
| `RequiredCraftingProp {type, craftingPropId}` | what recipes elsewhere may require nearby | id |
| `ModelResource`, `ModelComponent`, `ModelRenderHint`, `ColliderResourceComponent`, `CollisionFeedbackMaterialComponent` | look, collision, sound of a hit | |
| `VfxComponent`, `VfxComponentResource` | fire | VFX |
| `Shelter`, `ActiveNpcTarget {npcType, offsets}`, `UiOffsets`, `Health` | roof check, a villager's work spot | Forge has `Health` |
| `AnimationGraph*`, `ToggleAnimation {animationStateOn, animationStateOff}` | moving parts | Forge only |

33 templates have `FactoryStation`. 86 have `Crafting` without it: workbenches and every crafter. That's the difference between the two: **a bench crafts now, by a player's event; a factory crafts on its own, over time, from its own inventory.**

### 2.2 The state machine **[dec] [rt]**
Program `factory_station` (`0xf5980`, every step, both exes): `FactoryStationState {NoRecipe, MissingMaterials, Paused, Stopped, Running, MissingOutputSpace}`.
- `nextRecipe` set: takes it (`0xee0a0`): state Running, `runningRecipe`, `recipeStart` = now.
- Running: `recipeStart + craftingDuration × factoryProductionSpeedFactor ≤ now` finishes it (`0xedc60`), through an inventory transaction: inputs out, outputs in. No space: `MissingOutputSpace`. Then it starts over while inputs last.
- `0xed460` re-checks inputs. `DynamicRotationalEnergyConsumer`, `DynamicIsInWaterCheck` pause it. `missingRequirements` tells the UI: Recipe, EnergySourceInRange, EnergyAmount, InputMaterials, OutputSpace, AboveWater.
- **Workshop gate.** The start goes through `CraftRecipe` `0x210f00` → `0x19a1d0` → `0x91de90`, whose first test is `recipe.workshopId == Crafting.workshopId`; a miss returns 4, and the station shows `MissingMaterials`. A recipe is found by id (`RecipeFind`). Inputs leave when a cycle starts, outputs arrive when it ends. `0x91de90` also tests `requiredHappyNpcCount`, level and comfort; `0x19a1d0` the props mask.
- `player_crafting` (`0xd2250`): `PlayerInput.playerCraftingAction`: type 0 crafts now (`CraftRecipeEvent`), 2 sets `nextRecipe` and clears `isStoppedByPlayer`, 3 sets `useAllAvailableCategoryIngredients`, 4 stops. So a client asks by recipe id.
- `factory_station_toggle` (`0xf6250`) makes the toggle follow the state; client programs `factory_station_vfx`, `factory_station_sfx` and `client_toggle_animation` read only the toggle.

### 2.3 Fuel and energy **[data] [dec]**
There are three different things:
- **Burning fuel in a forge isn't a system.** Recipes list charcoal as an input: `Material_T1_Factory_Metal_Sheets`: 2 charcoal + 2 scrap → 1 sheet, 90 s. Every smelter recipe is the same.
- `Fuel` + `FuelSetup {automaticRefill, rainRefill, maxFuelCharges}` + `NetworkFuel` is really **water**: wells, watering cans, 24 templates. `actor::TransferFuelEvent` and `RecipeInfo.waterCharges` (a recipe's water, charges a craft; `waterAmount` is 0 in every recipe and unread: docs/re/stations.md 3.6) use it. Units (charges × 64), the 16-bit limit and the fill rate: docs/re/items.md section 11. `ItemInfo.fuelSetup {burnTime, energyOutput}` is set on only 3 items (logs, hardwood, twigs).
- **Energy**: `EnergyProvider {energyPerSecond}` (fireplace, 15 templates), `RotationalEnergySource` and `RotationalEnergyConsumer {requiredEnergy, searchRadius}` (mills, 10). 68 recipes have `requiredEnergy`.

So a modder's "fuel" is a recipe input, which the data already supports. Anything else (a burn timer in a slot) is a program of ours over the same inventory.

### 2.4 Menus and limits **[data] [dec]**
- `WorkshopRegistryResource`: 51 workshops, `{isNpc, ignoreInJournalUi, workshopGuid, workshopId, name, item, upgrade…}`, and 51 crafting props. A registry lookup miss skips a recipe in the UI loop `unlocked_recipes_ui` (client `0x31b730`). The registry's map is sized from the resource at load (`0x95f5b0`).
- `FbUiBundle.menu.crafting.recipes`: `workshops` (48, BlobArrays, not a fixed array), `trees` → tabs → subcategories → recipe ids, `maxRecipeGroupCount` 15, `maxRecipeSetCount` 28. A recipe in no subcategory isn't shown (docs/re/content.md §7).
- `RecipeRegistry` workshop map: 46 workshops have recipes; capacity 0x40, 512 recipes per workshop. Reserved at load.
- `CraftingPropMask` is a u64: 64 props, 51 used.
- World budget: the cursor reports `FactoriesBudgetExhausted`; `IsFactoryEntity` items count, `ServerProgressSystem.usedFactoryBudget` holds the use. The amount wasn't read.
- `RecipeInfo`: inputs, outputs, `craftingDuration`, `requiredProps`, `comfortLevel`, `serverProgressLevel`, `requiredHappyNpcCount`, `requiredEnergy`, `waterAmount` (unused), `waterCharges` (the recipe's water), `requiresSheltered`, `isCookingRecipe`, `level`. All data, all on both sides.

### 2.5 Other placed things **[data]**
| Object | Template | What it needs |
|---|---|---|
| Chest `Prop_Decoration_T1_Storage_24` | family `Container` | `InventorySetup`, `InventorySetupOverwrite`, `UpgradeStorage`, `DefaultInventory`, `InteractionLock`, `ToggleAnimation` (lid), `SignalTransmitter` |
| Bed `Prop_Furniture_Comfort7_T5_DoubleBed` | `Prop` | `InteractionAttachment` (lie, by sub-entity), `ComfortProvider`, `Comfort`, `NoBuildZone`, `StoreAssociatedNpcIds` |
| Door `Prop_Door_Large_Wood_Crafted` | `Switch` | `Actor`, `AnimationGraph*`, `InteractionToggle`, `DirectionalInteraction`, `PathGridInteraction`, `BlockWater`, `Collider`, `LogicGraphNodeComponent` |
| Torch, lantern | `Switch`, `Prop` | `ComfortProvider`, `VfxComponent` (the light comes from its VFX), `Extinguish`, `ServerToggleState`, `Shelter`, `AudioComponent`, `AttachToSurface` |

- **Comfort:** `ComfortProvider {comfort, category, range, freezingResistance}`; 17 categories, `ComfortCategory`: Hearth, Bed, Chair, ... Item `comfortSetup` is set on only 15 items. **[refl] [data]**
- **Placement:** on the item: `placementAABBmin/max`, `snappingAABB`, `placementColliders`, `buildZoneRequired`, `checkInhibitBuild`, `allowPlacementBelowFog`, `requiresAltarSlot`, `placementConsume`, `cursorConfig`, `waterUsability`. On the template: `AttachToSurface {attachDirection, searchLength, colliderMask}`, `SlopeConfig`, `NoBuildZone`. Checked by `server_cursor_no_prediction` and `client_cursor`, which report `ecs::CursorFlag`: outside the zone, in fog, too far, in an inhibitor zone, altar or water-source limit, area occupied, enemies nearby, bad normal, `InvalidCursorVoxelMaterial`, missing materials, not unlocked, no permission, tame-animal limit, in water, save budget, `FactoriesBudgetExhausted`. **[refl]**
- **Interaction:** `InteractionOffer {defaultAction, verbLoca, reduceInteractionWeight}`, `InteractionToggle {offToOnVerb, offToOnSequence, onToOffVerb, onToOffSequence}`, `InteractionLock` with 4 lock kinds. Programs `interaction_query_new`, `interaction_attempt`, `actor_interaction`. A lock's required item: the press's lock check and `interaction_attempt`'s payment, [locks.md](locks.md) (GitHub #147). Sequences are `actor::ActorSequenceResource`, the same as items.

## 3. Cosmetics

### 3.1 Vanity slots **[refl] [data] [dec]**
- `EquipmentSlot`: `Vanity_Head`, `Vanity_UpperBody`, `Vanity_Arms`, `Vanity_LowerBody`, `Vanity_Feet` (0..4), then `Armour_*` (5..9). `SlotSelection.equipment` has both sets.
- A vanity item is an ordinary item with `equipment.slot` = `Armour_*` and flag `IsVanityItem`. `0x937910` turns its armour slot into the vanity one. 199 ship (`Vanity_*`), 126 with recipes, 146 tintable.
- Armor look and stats are already separate: `0x384420` (client `render_equipment_models_ag2`) per visual slot takes the vanity slot's item, and if there's none or it's unusable, the armour slot's. `PlayerFlags` bits 20..24 (`HideHelmet`, `HideUpperbodyArmour`, `HideLowerbodyArmour`, `HideFeetArmour`, `HideGloves`) skip the slot.
- A vanity item has no armor values: `armorSetup` isn't set. It still carries `impactEntity` and config values for wearer attributes (`IsVanityItem` items: `HasPIDE` 146).

### 3.2 How a worn model is chosen and hidden **[dec]**
`RenderEquipment` holds 24 model instances per `VisualEquippedItems`: `BodyPart_*` (0..14), `Armour_*` (15..19), `Ring0`, `Ring1`, `Shield`, `MainHand`. Per frame (client `0x389ac0`):
1. **Resolve** the 24 slots (`0x384420`). Body parts come from the character (preset, `CharacterSetup.hair`, `.beard`).
2. **Masks:** OR of every worn item's `equipment.hideMask` and `equipment.blendShapeMask`. Bit 0 of the blend mask is set for `CharacterSetup.gender == Female`. A hidden slot's model stays but isn't drawn.
3. **Model** (`0xe8fe00`): `visualModels[]` first entry with `(mask & slotMask) == entry.mask`, else `visualModel`. Slot masks: hair `Female|Hat|Hood`, beard `Female|Jaw01..16`, upper body `Female|Tight|ArmsTight01` (+ `Shield` unless it hides a shield), arms `Female` (+ `Shield` likewise), lower body `Female|NoArmor|LegsBeltTight|LegsFeetTight|LegsFeetTight01`, otherwise `Female`.
4. **Colours:** body parts from the character's colour presets. Otherwise `ColorPaletteItem.paletteItemId` on the item's entity → that palette item's `itemColorCombinationSetup`, else the item's own if `isSet`. Three colours per slot (`ItemRenderColors`) on the instance.
5. **Blend shapes** (`0x383700`): each flag maps to a fixed `BlendShapeId` GUID, `Female` → `3ec83c3b…`, weight 1, `Hat`, `Hood`, `NoArmor`, `Tight` and the rest. A model that lacks the id ignores it.

Items and models are found by GUID each frame, and a miss leaves the slot empty: `ItemRegistryFind` is tested here. **[dec]**

### 3.3 Bodies **[data]**
- One skeleton, `87b29df6…`, 102 joints, for both genders.
- Body parts are items with `BodyPart_*` slots, category `Customization`, `VC_*`, 133. Male and female are separate models: `VC_Body_Chest_01`: `visualModel` male, `visualModels[{Female}]` female. Heads per gender.
- Armor is usually one model with a `Female` morph: of 224 models with blend shapes, 159 have it. Others list per-mask models (`Vanity_TX_Hunter_Chest`: 6 entries for `Tight`, `Shield`, `Female`).
- Hair and beards are items as well: 45 and 13, listed in `CharacterCustomizationResource.hairItems`, `beardItems` in the UI bundle, with colour lists. `Hat` and `Hood` swap the hair model through `visualModels`.
- Rigid attachment exists for 8 armour items: `visualEntity` → a template with `SlotAttachment` and `VisualEquipment` (`Visuals_Ring_Of_Light`), spawned by the server's `equipment_visuals`. A rigid mesh can equally be a skinned model with all weight on one joint, which needs no server entity. The game does this itself: 19 of its vanity head models are skinned on the head joint (17) alone, and moving one's vertices to the joint's frame and attaching them again gives the same vertices within 0.3 µm. Slots of the player's skeleton, from its animation graph's `slotBoneIndexMapping`: Head 17, Chest 15, Pelvis 1, FootL 5, FootR 9, HandR 94, HandL 64, HandPalmR 74, HandPalmL 44, KneeR 7, KneeL 3, ArmL 67, Block 66. **[data]**

### 3.4 Blend shape data **[data] [spv] [dec]**
`RenderModel.blendShapeData` holds per shape and per vertex **3 × f16 position deltas**, 6 bytes, in metres:
- `mesh.blendShapeByteOffset` is the group's start. The range size is the sum of (group vertices × 6 × shapes) over the groups that have data.
- 197 of 224 models fit "all groups" or "first group" exactly; the other 27 are other subsets, each adding up. A group with no data also says offset 0.
- On `armor_n_ear_lumberer_01_chest_tint`: 2,683 vertices, 16,098 B, median delta 2.6 mm, largest 3.6 cm, 58 % non-zero.
- **Order** **[spv]**: the model vertex shader (`renderer.gpc`, `Model_*` pipelines) reads delta = `blendShapeData + blendShapeByteOffset + (shape × vertexCount + vertex) × 6`, three halves, for each
  raised bit of the instance's shape mask that the mesh's mask also has, weighted by a byte /255, and adds it to the position before skinning. So a group holds all its vertices of the first
  shape, then of the next. Normals are not changed.
- **Shapes and weights** **[dec]**: per frame `0x383700` turns the slot's mask into `{BlendShapeId, weight}` pairs, weight 1.0, through a fixed table of 26 GUIDs in `keen::BlendShapeFlags`
  order (client `0x14152a898`); `0x1161ae` matches them against the model's `blendShapes` by GUID and packs at most **4** weights into the instance (`0xf6de0` also caps the list at 4).
  The table's GUIDs: `tools/kfc/kfc/mesh.py` `BLEND_SHAPES`, checked against the executable by `game_worn_check.py table`.
- `RenderModel.blendShapeMask` has a bit per entry of `blendShapes` (1, 3, 7, 8191 in the game's data).

So a morph target in a glTF maps directly, and normals don't change. The cook writes every group, zeros where a primitive lacks a target, in the "all groups" form. **[data]**: all 224
models with blend shapes, exported to glTF by tools/kfc and cooked, give their 538 shapes back byte for byte (1,378,801 vertices).

## 4. Replication
- **Inventory and slots** replicate item ids and counts; other players' clients resolve looks locally (section 3.2). So a worn item a client doesn't have is invisible on that player, and what else that client does with it is docs/re/content.md §9.
- **Cells** replicate raw material bytes (1.5). **Factory state:** `FactoryStation` is a server component; clients see `NetworkToggleState` and `Inventory`, and the UI reads a server-built `ServerFactoryInfo` through `factory_station_info`.
- **Templates:** a client spawns what the server names. A template it lacks is a crash on a vanilla client (docs/re/network.md "Vanilla clients"). So a new placed template needs Kiln's definitions on every client.

## 5. Lab results
A dedicated server with a copied world and no player; a scratch mod and resource overrides served by kiln.assets at boot. Nothing here ran on a client.

**Hardness, by the game's own code.** Cubes of 729 cells, the game's `Terraform_Explosion_TNT_T1` spawned at the centre (radius 2.5 m, soft 3000, slightly 2500, moderately 2000, hard 1500, very hard 25):

| Material | Vanilla | Removed | With an override of the registry | Removed |
|---|---|---|---|---|
| 155 Rough Stone | `ModeratelyHard` 250 | 504 | `Unbreakable` | **0** |
| 143 Rough Wood | `SlightlyHard` 150 | 504 | `Soft` 1 | 504 |
| 135 Iron | `VeryHard` 2000 | 0 | same | 0 |
| 128 Refined Stone | `ModeratelyHard` 200 | 504 | same | 504 |
| 213, not in the game | no config | not run | config `Soft` 2 | **504** |
| 214, not in the game | | | no config | 0 |
| 143, inside a base zone, no player | `SlightlyHard` 150 | **504** | | |

**A new material id on the server.** The world resource overridden with a new GUID at `materialGuids[213]`, cells of 213 written inside a base zone:
```
write id 213:                     mat 213 fill 255 x125
restart, GUID now at slot 214:    mat 214 fill 255 x125    (the save's table remapped it)
restart, GUID in no slot:         mat 0   fill 0   x125    (the walls are gone)
```
So a removed material leaves **air**, not a neutral block, and a moved one follows its GUID. **[rt]**

**A station.** A game drying rack placed by Kiln, a new recipe at its workshop (2 twigs → 3 logs, 4 s), `FactoryStation.nextRecipe` written as `player_crafting` does, inputs given:
```
t+0.5 s  state 4 running 0x5568cc2b  toggle 1  [Twigs x4]                 inputs leave at the start
t+4.0 s  state 4 running 0x5568cc2b  toggle 1  [Twigs x2, Logs x3]        one cycle, exactly 4 s, then again
t+8.0 s  state 1 next 0x5568cc2b  missing 0x08  [Logs x9]                 MissingMaterials
```
- A clone of the forge with its own `Crafting.workshopId` 0xc0eeee01 and a recipe at the forge's workshop: `state 1 missing 0x08`, inputs present. That's the workshop gate (2.2), reported as missing materials.
- The same recipe with its `workshopId` set to 0xc0eeee01 in memory: `state 4`, a 3 s cycle, twigs to resin. So **a workshop id that nothing else knows works on the server**.
- A clone with `availableSlotCount` 6 runs with 6 slots (`generic 8/6`).
- Inside a base, with `UsedItem` set: running, saved, restarted: `state 4 running 0x5568cc2b`, still cycling, twigs 96 → 92 → 86. Without those two (on open ground, no item) a placed rack didn't come back after a restart; which of the two matters wasn't split.

Not run: a player's dig or placement, permissions, anything through `player_crafting`, fuel or energy, comfort.

## 6. Round trips **[data]**
Every resource of these types decoded and re-encoded through `tools/kfc`, byte-identical:

| Type | Resources |
|---|---|
| `TerraformingEfficiencyRegistryResource`, `BuildingMaterialBlendingResource`, `BuildingMaterialParametersResource`, `GiVoxelBuildingMaterialResource`, `WorldMaterialBlending2Resource`, `SimpleWorldMaterialResource`, `VoxelMaterialVfxDataResource`, `VoxelBlueprintItemRegistryResource`, `VoxelBlueprintMaterialPoolRegistryResource`, `WorkshopRegistryResource`, `RecipeRegistryResource`, `CharacterPresetCollection`, `MaterialFeedbackCollectionResource`, `FbUiBundle`, `Game38ClientResources` | 1 each |
| `VoxelBlueprintConfig` | 7 |
| `VoxelModelResource` | 78 |
| `WorldMaterialBlending2Resource`, `SimpleWorldMaterialResource`, `VoxelMaterialVfxDataResource`, with two new terrain ids written by kiln.worldtables | 1 each |
| `RenderMaterialResource` | 3,458 |
| `ItemInfo` | 4,268 |
| `ecs::TemplateResource`, among them `T1_Forge_Factory` (10,024 B) and `T1_Drying_Rack_Factory` (2,632 B) | 12,269 |

Written through the same code, with a new material in slot 85 copied from slot 4 (blending table, GI table, parameters, registry, pool, blueprint item, world GUID), and decoded again: `layerCount` 86, the table still 50,176 B, 48 more patch points, 42 more textures, 6 more feedback lists, and a second encode identical. The world resource differs in 16 bytes.

## 7. Open
- **Drawn by a client:** a slot with its own cooked textures, rather than copied ones; the edge decoration of a new slot; a new item in the menu; a cooked armor piece.
- Which UI reads the known-material sets (1.2).
- The factory budget's amount (2.4); the capacity of the client's per-workshop recipe list map, `G38UiPlayData` (client `0x31b730`).
- ~~Whether a factory screen is the same builder as a bench's, `CraftingMenu` `0xb80050`, with `FbUiFactory` parts.~~
  Answered statically on client 26430625 (0.2.0-dev.29.15): it is not. HUD 28 (FactoryCrafting) has no case in the HUD
  dispatcher's crafting switch; PlayStateUi draws it with the chest-style transfer screen `0xb9d0f0` (mode 2): docs/re/game-ui.md
  section 5, "Factory screens".
- Why a placed rack on open ground without `UsedItem` wasn't saved (section 5).
