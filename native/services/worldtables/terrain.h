// SPDX-License-Identifier: MIT
// kiln.worldtables, terrain materials: the portable part (no Windows, no game, no Kiln API; tests/ runs it on the host).
//
// A terrain material is a cell's low byte 1..127. The game uses 1..116, so 117..127 are free: 11 for every mod together, a budget of their own,
// apart from the building materials' 128..255. What one is, by table:
//  - VoxelWorldResource.materialGuids[id]: the GUID a save keeps per id (both sides);
//  - TerraformingEfficiencyRegistryResource.terrainConfigs: hardness, health, class, loot item and rate, flatten, sliding, sounds, friction (both sides);
//  - VoxelMaterialVfxDataResource.materialType[id]: the particle class (both sides);
//  - the pool "TerrainMaterials": items the game's terrain shapes place (both sides);
//  - client only, how it looks: WorldMaterialBlending2Resource's baking table, one u32 per id (first layer | layer count << 16), and
//    SimpleWorldMaterialResource's layer infos, two u32 per id (surface, underground). A new id takes a game material's entries: the same layers,
//    so it looks like that material. Layers of its own are a cooker's job (docs/kiln/services/worldtables.md, "Terrain").
// Evidence: docs/re/building.md 1.6.
#pragma once
#include <cstdint>
#include <string>
#include <vector>

#include "merge.h"

namespace kwt {

inline constexpr int kTerrainLast = 127;
// What a terrain material starts from when it doesn't say: plain stone.
inline const char* const kTerrainLike = "TerrainStone";
inline const char* const kTerrainPool = "TerrainMaterials";

// A file's "terrainMaterials" section into out->materials, each with terrain = true. Errors name the file, the block and the field.
void read_terrain_blocks(const ojson& doc, ModFile* out);

// Terrain ids with no GUID in the world's table and no config in the registry: the room for new ones.
std::vector<int> free_terrain_ids(const Bytes& world, const Layouts& L, const RegistryInfo& info);

// The terrain ids, as allocate() gives building ones: live materials first, then kept ones, each in sorted GUID order. A live one past the room is
// refused by name; kept ones hold their room and are never dropped for a live one.
Allocation allocate_terrain(std::vector<Material> live, std::vector<Material> kept, const std::vector<int>& free_ids);

// New terrain configs, after the game's: each a copy of its like_cell's config (sounds, friction, buffs and all) with its id, flags and strength. A
// strength's drops is an item GUID by now. false + *why: the registry isn't what the layout says, or a like_cell has no config.
bool merge_terrain_registry(const Bytes& reg, const Layouts& L, const std::vector<Placed>& placed, Bytes* out, std::vector<RegistryChange>* changes, std::string* why);

// Live placeable terrain materials' items, after the terrain pool's own. Past kPoolCapacity items: false + *why, never served.
bool merge_terrain_pool(const Bytes& pools, const Layouts& L, const std::vector<Placed>& placed, Bytes* out, std::string* why);

// Where the per-id tables are, from the game's reflection. A table this build doesn't have stays as it is.
struct TerrainTables {
    uint32_t vfx = 0;                // VoxelMaterialVfxDataResource.materialType: BlobArray<uint32>, one per cell id
    uint32_t blend = 0, blend_sig = 0, blend_type = 0;   // WorldMaterialBlending2Resource: bakingVoxelMaterialDataBlock.data, its typeSignatureHash, the
                                                         // element's layout hash it has to say
    uint32_t simple = 0, simple_sig = 0, simple_type = 0;  // SimpleWorldMaterialResource: layerInfosDataBlock.data, the same
    bool ok = false;
};
// Each placed terrain material takes look_cell's entries: its particle class, its baking layers, its far layers. A table with nothing to change
// comes back as it was.
bool merge_terrain_vfx(const Bytes& res, const TerrainTables& T, const std::vector<Placed>& placed, Bytes* out, std::string* why);
bool merge_terrain_blend(const Bytes& res, const TerrainTables& T, const std::vector<Placed>& placed, Bytes* out, std::string* why);
bool merge_terrain_simple(const Bytes& res, const TerrainTables& T, const std::vector<Placed>& placed, Bytes* out, std::string* why);

}  // namespace kwt
