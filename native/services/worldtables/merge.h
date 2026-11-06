// SPDX-License-Identifier: MIT
// kiln.worldtables, the portable part: what a mod's file says, the id allocator, and the writers over a resource's bytes.
// No Windows, no game, no Kiln API, so tests/ runs it on the host. The layouts come from the game's reflection (reflect.cpp).
#pragma once
#include <array>
#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <vector>

#include "nlohmann/json.hpp"

namespace kwt {

using Bytes = std::vector<uint8_t>;
using ojson = nlohmann::ordered_json;

struct Guid {
    std::array<uint8_t, 16> b{};
    bool zero() const;
    std::string text() const;  // lower-case .NET "D" form
    static bool parse(const std::string& s, Guid* out);
    auto operator<=>(const Guid&) const = default;
};

uint32_t fnv1a(const void* data, size_t n);
// Kiln.Blocks' BlockIds.Derive: UUIDv5(<mod>:<kind>:<name>) in Kiln's namespace. A C# handle and the boot merge agree on it.
Guid derive_guid(const std::string& mod, const std::string& kind, const std::string& name);
// A GUID's ItemId: FNV-1a over its bytes.
uint32_t item_id_of(const Guid& g);

// Field offsets from the game's reflection, so a game update that moves a field needs nothing from Kiln.
struct ConfigLayout {
    uint32_t stride = 0, id = 0, item = 0, hardness = 0, rate = 0, health = 0, klass = 0, conditional = 0;
    uint32_t roof = UINT32_MAX;  // buildings only
    uint32_t flatten = UINT32_MAX, sliding = UINT32_MAX;  // terrain only: canBeFlattened, isSlidingMaterial
};
struct Layouts {
    uint32_t reg_terrain = 0, reg_building = 0, reg_size = 0;
    ConfigLayout terrain, building;
    uint32_t world_guids = 0, world_size = 0;
    uint32_t pools = 0, pool_stride = 0, pool_materials = 0, pool_name = 0;
    uint32_t blueprints = 0, bp_stride = 0, bp_item = 0, bp_data = 0, bp_size = 4, bp_compressed = 24;
    std::vector<std::string> hardness_names, class_names;  // value = index
};

struct Strength {
    std::optional<int> hardness;
    std::optional<int32_t> health;
    std::optional<int> breaks;
    std::optional<uint32_t> items_per_blocks;
    std::optional<std::string> drops;  // an item: its GUID text once resolved
    bool any() const { return hardness || health || breaks || items_per_blocks || drops; }
};

struct Tweak {
    std::string mod, dir, block, material, definition;
    Strength strength;
    // after resolving
    int cell = -1;
};

struct Material {
    Guid guid;
    std::string mod, dir, block, definition = "{}";
    Strength strength;
    bool roof = false;
    bool live = true;
    bool item = false;      // has a block item: FNV of derive_guid(mod, "item", name)
    uint32_t item_id = 0;
    Guid item_guid;         // that block item
    // What digging it gives back unless its strength says: the main input of its recipe, at the recipe's ratio, as every game block does.
    std::string loot_from;  // the recipe's first input, as written
    uint32_t loot_count = 0, loot_makes = 1;
    // A terrain material (cell 1..127, terrain.h): the game material it starts from and the one whose look it takes, by name, then by cell; the
    // game's flatten and sliding flags. `item` is its placeable item in the terrain pool.
    bool terrain = false;
    std::string like, look;
    int like_cell = -1, look_cell = -1;
    std::optional<bool> flatten, sliding;
};
// strength.drops of a new material: its own block item, rather than an item by name.
inline const char* const kDropsOwnBlock = "block";
// Blocks dug per loot item for a recipe that makes `makes` blocks from `count` of its main input: the recipe's ratio, at least 1.
uint32_t loot_rate(uint32_t makes, uint32_t count);

struct Placed {
    Material m;
    int cell = 0;
    bool reused = false;
};

// Reads the sections of one file. Errors name the file, the block and the field; a wrong block is left out and the
// file's other blocks stay.
// A shape of the mod's own (BlueprintBlock): its blueprint item's entry in the shapes table, one bit per half-metre cell, and its preview model.
struct Shape {
    std::string mod, block, definition;  // definition: the normal form a define compares
    Guid item;                           // the blueprint item: UUIDv5(<mod>:item:<name or was>), as Kiln.Blocks makes it
    uint32_t item_id = 0;
    uint32_t x = 0, y = 0, z = 0;
    Bytes bits;                          // bit i = cell x + X * (y + Y * z), y up, lowest bit first
    bool roof = false;
};

struct ModFile {
    std::string mod, dir, file, clients;
    std::vector<Shape> shapes;
    std::map<std::string, ojson> strengths;  // the mod's "strengths" section: what "strength": "<name>" means
    std::map<std::string, ojson> surfaces;   // the mod's "surfaces" sections: what "surface": "<name>" means
    std::vector<Tweak> tweaks;
    std::vector<Material> materials;
    std::vector<std::string> errors;
};
ojson parse_relaxed(const std::string& text);
void read_blocks(const ojson& doc, ModFile* out);
void read_shape(const std::string& name, const ojson& j, const ModFile& mf, Shape* out, std::vector<std::string>* errors);
bool base64_decode(const std::string& text, Bytes* out);
void read_strength(const ojson& j, const std::string& where, const std::string& mod_block, Strength* s, std::vector<std::string>* errors);

// The enum values mods write; the numbers are the game's.
inline const char* const kHardnessJson[] = {"soft", "slightlyHard", "moderatelyHard", "hard", "veryHard", "unbreakable"};
inline const char* const kBreaksJson[] = {"none", "wood", "stone", "metal"};

// The ids. free_ids: never used by the game, ascending; deprecated: reusable, ascending. A live material over the room goes in
// `refused` by name; kept materials are never dropped, so what they hold is what live ones can't have.
struct Allocation {
    std::vector<Placed> placed;  // live first, then kept, each sorted by GUID text
    std::vector<std::string> refused;
    size_t room = 0;
};
Allocation allocate(std::vector<Material> live, std::vector<Material> kept, const std::vector<int>& free_ids, const std::vector<int>& deprecated);

// What client and server have to agree on: 16 hex digits over the live materials' ids and GUIDs.
std::string handshake_hash(const std::vector<Placed>& placed);
inline const char* const kEmptyHash = "0000000000000000";

// ---- reading the game's resources ------------------------------------------------------------------------------
struct ArrayView {
    const uint8_t* base = nullptr;
    uint32_t count = 0;
};
// A BlobArray at `field`: {int32 offset from the field, uint32 count}. false: it doesn't fit the resource.
bool array_at(const Bytes& res, uint32_t field, uint32_t stride, ArrayView* out);

struct RegistryInfo {
    std::map<int, uint32_t> building_at;  // cell id -> byte offset of its config
    std::map<int, uint32_t> terrain_at;
    std::vector<int> free_after_game;     // filled by the caller from the GUID table
};
bool read_registry(const Bytes& res, const Layouts& L, RegistryInfo* out, std::string* why);

// Cell ids 128..255 with no GUID in the table.
std::vector<int> free_building_ids(const Bytes& world, const Layouts& L);
// Whether this world resource has a material table at all (most have zeros).
bool world_has_table(const Bytes& world, const Layouts& L);
// Configured building ids without a loot item: the game's deprecated slots.
std::vector<int> deprecated_ids(const Bytes& reg, const RegistryInfo& info, const Layouts& L);

// ---- writing ----------------------------------------------------------------------------------------------------
// Each returns the resource's new bytes. Parts that don't apply leave the bytes as they are, so "no change" compares
// equal to the game's. false + *why: the resource isn't what the layout says.
struct RegistryChange {
    std::string text;  // for the log
};
bool merge_registry(const Bytes& game, const Layouts& L, const std::vector<Tweak>& tweaks, const std::vector<Placed>& placed, Bytes* out,
                    std::vector<RegistryChange>* changes, std::string* why);
bool merge_world(const Bytes& game, const Layouts& L, const std::vector<Placed>& placed, Bytes* out, std::string* why);
bool merge_pools(const Bytes& game, const Layouts& L, const std::vector<Placed>& placed, Bytes* out, std::string* why);
bool merge_blueprints(const Bytes& game, const Layouts& L, const std::vector<Placed>& placed, uint32_t template_item, Bytes* out, std::string* why);
// The first item of the pool a block item joins: what a new block item's blueprint entry is copied from.
uint32_t first_pool_item(const Bytes& pools, const Layouts& L);

// The shapes' entries appended to a blueprint-items table, each with its own size and cells (isDataCompressed: one bit per cell). An item id the table
// already has is refused by name in *refused; the others go in.
bool merge_shapes(const Bytes& table, const Layouts& L, const std::vector<Shape>& shapes, Bytes* out, std::vector<std::string>* refused, std::string* why);
// What the client draws under the cursor: a keen::VoxelModelResource of the shape, one byte per cell (0x12 a full cell, as the game's walls).
struct ModelLayout {
    uint32_t size = 0, size_field = 0, data = 0, terrain = 0;
};
Bytes voxel_model(const Shape& s, const ModelLayout& M);
inline constexpr uint8_t kFullCell = 0x12;
inline constexpr uint32_t kMaxShapeCells = 32;

// The stable name of a game material, the one mods write: from the debug name of the item that places it.
// "Block_T1_Wood_Rough_CRAFTING" -> "WoodRough", "Block_Roof_T1_PlantFibre" -> "RoofPlantFibre",
// "Build_TerrainMaterial_T1_Stone" -> "TerrainStone". Any id also answers to "Cell<id>".
std::string material_name(const std::string& item_debug_name);

}  // namespace kwt
