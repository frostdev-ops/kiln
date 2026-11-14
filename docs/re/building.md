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
- `Fuel` + `FuelSetup {automaticRefill, rainRefill, maxFuelCharges}` + `NetworkFuel` is really **water**: wells, watering cans, 24 templates. `actor::TransferFuelEvent`, `RecipeInfo.waterAmount`, `waterCharges` use it. Units (charges × 64), the 16-bit limit and the fill rate: docs/re/items.md section 11. `ItemInfo.fuelSetup {burnTime, energyOutput}` is set on only 3 items (logs, hardwood, twigs).
- **Energy**: `EnergyProvider {energyPerSecond}` (fireplace, 15 templates), `RotationalEnergySource` and `RotationalEnergyConsumer {requiredEnergy, searchRadius}` (mills, 10). 68 recipes have `requiredEnergy`.

So a modder's "fuel" is a recipe input, which the data already supports. Anything else (a burn timer in a slot) is a program of ours over the same inventory.

### 2.4 Menus and limits **[data] [dec]**
- `WorkshopRegistryResource`: 51 workshops, `{isNpc, ignoreInJournalUi, workshopGuid, workshopId, name, item, upgrade…}`, and 51 crafting props. A registry lookup miss skips a recipe in the UI loop `unlocked_recipes_ui` (client `0x31b730`). The registry's map is sized from the resource at load (`0x95f5b0`).
- `FbUiBundle.menu.crafting.recipes`: `workshops` (48, BlobArrays, not a fixed array), `trees` → tabs → subcategories → recipe ids, `maxRecipeGroupCount` 15, `maxRecipeSetCount` 28. A recipe in no subcategory isn't shown (docs/re/content.md §7).
- `RecipeRegistry` workshop map: 46 workshops have recipes; capacity 0x40, 512 recipes per workshop. Reserved at load.
- `CraftingPropMask` is a u64: 64 props, 51 used.
- World budget: the cursor reports `FactoriesBudgetExhausted`; `IsFactoryEntity` items count, `ServerProgressSystem.usedFactoryBudget` holds the use. The amount wasn't read.
- `RecipeInfo`: inputs, outputs, `craftingDuration`, `requiredProps`, `comfortLevel`, `serverProgressLevel`, `requiredHappyNpcCount`, `requiredEnergy`, `waterAmount`, `requiresSheltered`, `isCookingRecipe`, `level`. All data, all on both sides.

### 2.5 Other placed things **[data]**
| Object | Template | What it needs |
|---|---|---|
| Chest `Prop_Decoration_T1_Storage_24` | family `Container` | `InventorySetup`, `InventorySetupOverwrite`, `UpgradeStorage`, `DefaultInventory`, `InteractionLock`, `ToggleAnimation` (lid), `SignalTransmitter` |
| Bed `Prop_Furniture_Comfort7_T5_DoubleBed` | `Prop` | `InteractionAttachment` (lie, by sub-entity), `ComfortProvider`, `Comfort`, `NoBuildZone`, `StoreAssociatedNpcIds` |
| Door `Prop_Door_Large_Wood_Crafted` | `Switch` | `Actor`, `AnimationGraph*`, `InteractionToggle`, `DirectionalInteraction`, `PathGridInteraction`, `BlockWater`, `Collider`, `LogicGraphNodeComponent` |
| Torch, lantern | `Switch`, `Prop` | `ComfortProvider`, `VfxComponent` (the light comes from its VFX), `Extinguish`, `ServerToggleState`, `Shelter`, `AudioComponent`, `AttachToSurface` |

- **Comfort:** `ComfortProvider {comfort, category, range, freezingResistance}`; 17 categories, `ComfortCategory`: Hearth, Bed, Chair, ... Item `comfortSetup` is set on only 15 items. **[refl] [data]**
- **Placement:** on the item: `placementAABBmin/max`, `snappingAABB`, `placementColliders`, `buildZoneRequired`, `checkInhibitBuild`, `allowPlacementBelowFog`, `requiresAltarSlot`, `placementConsume`, `cursorConfig`, `waterUsability`. On the template: `AttachToSurface {attachDirection, searchLength, colliderMask}`, `SlopeConfig`, `NoBuildZone`. Checked by `server_cursor_no_prediction` and `client_cursor`, which report `ecs::CursorFlag`: outside the zone, in fog, too far, in an inhibitor zone, altar or water-source limit, area occupied, enemies nearby, bad normal, `InvalidCursorVoxelMaterial`, missing materials, not unlocked, no permission, tame-animal limit, in water, save budget, `FactoriesBudgetExhausted`. **[refl]**
- **Interaction:** `InteractionOffer {defaultAction, verbLoca, reduceInteractionWeight}`, `InteractionToggle {offToOnVerb, offToOnSequence, onToOffVerb, onToOffSequence}`, `InteractionLock` with 4 lock kinds. Programs `interaction_query_new`, `interaction_attempt`, `actor_interaction`. Sequences are `actor::ActorSequenceResource`, the same as items.

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
- Whether a factory screen is the same builder as a bench's, `CraftingMenu` `0xb80050`, with `FbUiFactory` parts.
- Why a placed rack on open ground without `UsedItem` wasn't saved (section 5).
