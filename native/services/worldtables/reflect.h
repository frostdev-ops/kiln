// SPDX-License-Identifier: MIT
// kiln.worldtables: the game's reflection records, read from the running exe. They are static data, so this needs no signature and no
// game code (record layout: tools/kfc/kfc/refl.py). A field or type a game update removed turns the service off with a plain line.
#pragma once
#include <string>
#include <vector>

#include "merge.h"
#include "terrain.h"

namespace kwt {

// Where the fields of an ItemInfo are: what names a game material and an item.
struct ItemLayout {
    uint32_t object_id = 0, debug_name = 0, voxel_guid = 0, voxel_id = 0, is_building = 0, stride = 0;
};

// Fills the layouts from the image at `image_base`. test_missing: a field to pretend is gone, for the fail-safe test.
bool read_layouts(const uint8_t* image_base, size_t image_size, const std::string& test_missing, Layouts* L, ItemLayout* I, std::string* why);
// The preview model's layout (keen::VoxelModelResource). false: shapes get no preview on this build; the rest is unaffected.
bool read_model_layout(const uint8_t* image_base, size_t image_size, ModelLayout* M, std::string* why);
// Where the game's table of crafting props is (merge.h CraftingPropLayout). false: crafting props are left out on this build; the rest is unaffected.
bool read_crafting_prop_layout(const uint8_t* image_base, size_t image_size, CraftingPropLayout* C, std::string* why);
// Where the per-id terrain tables are (terrain.h). false: terrain materials get no particle class or look on this build; the rest is unaffected.
bool read_terrain_tables(const uint8_t* image_base, size_t image_size, TerrainTables* T, std::string* why);
// "keen::X" -> its layout hash, for the status report. 0 when not found.
uint32_t layout_hash_of(const uint8_t* image_base, size_t image_size, const std::string& qname);

}  // namespace kwt
