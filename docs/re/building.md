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

Nothing here has been drawn by a client: everything about pictures is **[dec]**, **[spv]** or **[hyp]**.

## Short answers
- **A building material is one byte in a cell, and every table that describes it is data.** Cell low byte 1..127 is terrain, 128..255 is building
  material `id - 128`. 116 terrain and 85 building ids are used; 11 and 43 are free. The byte is the limit: no more than 128 building materials
  without changing the cell format. **[data] [dec]**
- **The GPU tables already have room.** `BuildingMaterialBlendingResource` is cooked with 128 slots of 392 bytes and `GiVoxelBuildingMaterialResource` with
  128 of 180; slots 85..127 are zeros. So a new building material is an edit of existing resources; no buffer grows. **[data]**
- **Hardness is data, on both sides.** `TerraformingEfficiencyRegistryResource` holds hardness category, health points and material class per
  id. A tool holds damage per hardness category. The dig kernel's cost per material = `health / (damage[hardness] × miningDamageFactor × classMod)`; zero damage or `Unbreakable` leaves the material untouched. **[dec] [rt]**
- **Saves keep a GUID per material id; the wire doesn't.** A terrain blob in the save carries a 256-entry GUID table and remaps on load. Cells of an unknown GUID come
  back as air. Replication sends raw bytes. **[dec] [rt]**
- **A client without a material draws a fallback, by a bound check in the shader.** The fill shaders compare the index with a uniform, the table's `layerCount`.
  **[spv]**
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

Six building ids have no item (`materialItem` empty): 1, 3, 6, 9, 11, 28. Their slots hold `_DEPRECATED_` or `OLD_` surfaces. They are free for reuse. **[data]**

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

**Unlocks.** The menu lists what a player can make: recipes, as for any item (`knowledgeRequirement`). Configs carry a `knowledgeQuery` too: `0xa2fd6b` evaluates them into two
256-bit sets of known terrain and building ids (`update_terraforming_queries_*`). What reads those sets wasn't followed. **[dec]**

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

**Terrain** is tighter, by layers rather than ids: `WorldMaterialBlending2Resource` has per-id `layerStartIndexAndCount` (128 slots), 224 layer slots of which 222 are used, and 768 texture slots of which 499 are used. Terrain materials blend per biome and mode. A new terrain id that reuses other layers fits today; one with its own layers can use two, unless the buffers grow, which wasn't tested. **[data]**

### 1.5 Saves and what a removed mod leaves **[dec] [rt]**
- Save: `0x83d200` writes `materialGuids[256]` (0x1000 B) in front of every terrain blob; the table comes from the world's resource (`world+0x1bc`).
- Load: `0x830e30`, for blob versions 2..6, builds `remap[256]`. Identity for a zero GUID; otherwise the id of that GUID in the world's map (`store+0x11c0`), or **0 when the world doesn't know it**.
- So ids are not stable data: moving a material to another slot is safe as long as the GUID stays in the table. A removed material's cells read back as `material 0, fill 0`: air, walls gone. See the lab results.
- Replication (`0x83ba40`) sends the store's blocks as they are, with no table. Client and server must agree on ids.

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
- `Fuel` + `FuelSetup {automaticRefill, rainRefill, maxFuelCharges}` + `NetworkFuel` is really **water**: wells, watering cans, 24 templates. `actor::TransferFuelEvent`, `RecipeInfo.waterAmount`, `waterCharges` use it. `ItemInfo.fuelSetup {burnTime, energyOutput}` is set on only 3 items (logs, hardwood, twigs).
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
- Rigid attachment exists for 8 armour items: `visualEntity` → a template with `SlotAttachment` and `VisualEquipment` (`Visuals_Ring_Of_Light`), spawned by the server's `equipment_visuals`. A rigid mesh can equally be a skinned model with all weight on one joint, which needs no server entity.

### 3.4 Blend shape data **[data]**
`RenderModel.blendShapeData` holds per shape and per vertex **3 × f16 position deltas**, 6 bytes, in metres:
- `mesh.blendShapeByteOffset` is the group's start. The range size is the sum of (group vertices × 6 × shapes) over the groups that have data.
- 197 of 224 models fit "all groups" or "first group" exactly; the other 27 are other subsets, each adding up.
- On `armor_n_ear_lumberer_01_chest_tint`: 2,683 vertices, 16,098 B, median delta 2.6 mm, largest 3.6 cm, 58 % non-zero.

So a morph target in a glTF maps directly, and normals don't change.

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
| `RenderMaterialResource` | 3,458 |
| `ItemInfo` | 4,268 |
| `ecs::TemplateResource`, among them `T1_Forge_Factory` (10,024 B) and `T1_Drying_Rack_Factory` (2,632 B) | 12,269 |

Written through the same code, with a new material in slot 85 copied from slot 4 (blending table, GI table, parameters, registry, pool, blueprint item, world GUID), and decoded again: `layerCount` 86, the table still 50,176 B, 48 more patch points, 42 more textures, 6 more feedback lists, and a second encode identical. The world resource differs in 16 bytes.

## 7. Open
- **Drawn by a client: everything.** A new slot's picture, the fallback, a new item in the menu, a cooked armor piece.
- Which UI reads the known-material sets (1.2).
- The factory budget's amount (2.4); the capacity of the client's per-workshop recipe list map, `G38UiPlayData` (client `0x31b730`).
- Whether a factory screen is the same builder as a bench's, `CraftingMenu` `0xb80050`, with `FbUiFactory` parts.
- Other shaders' bound checks on the material index (1.4).
- Why a placed rack on open ground without `UsedItem` wasn't saved (section 5).
