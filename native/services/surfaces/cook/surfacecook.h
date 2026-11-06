// SPDX-License-Identifier: MIT
// surfacecook: building-material surfaces from PNGs. A surface is a slot in three of the client's tables: BuildingMaterialBlendingResource (six
// layers of maps and numbers per slot, what the wall shaders read), GiVoxelBuildingMaterialResource (averages and two maps per face, for global
// illumination) and BuildingMaterialParametersResource (mapping and edge decoration). This reads the game's tables, writes slots into them and
// writes them back, and cooks each map to the format the game's own building surfaces use. Evidence: docs/re/building.md 1.4.
//
// Portable: no Windows, no game memory, no Kiln API. kiln-surfaces.dll runs it at boot and tools/surfacecook builds the same code as
// kiln-surfacecook, so what the offline tests prove about the bytes holds for what the game is served.
#pragma once
#include <array>
#include <cstdint>
#include <functional>
#include <map>
#include <optional>
#include <string>
#include <vector>

#include "surfacecheck.h"
#include "vfxcook.h"

namespace ksf {

using Bytes = kvb::Bytes;
using kvb::ContentHash;
using kvb::Guid;

// keen:: type hashes, what kiln.assets keys resources by
constexpr uint32_t kTypeBlending = 0xbea8708au;  // keen::BuildingMaterialBlendingResource
constexpr uint32_t kTypeGi = 0x33ce35abu;        // keen::GiVoxelBuildingMaterialResource
constexpr uint32_t kTypeParams = 0xf7452b6bu;    // keen::BuildingMaterialParametersResource

constexpr uint32_t kSlotBytes = 392;   // keen::BuildingMaterialBlendingMaterialLayerShaderData
constexpr uint32_t kLayerBytes = 60;   // keen::BuildingMaterialBlendingMaterialLayerTextureShaderData
constexpr uint32_t kGiSlotBytes = 180; // three faces of 60 B: top, side, bottom
constexpr uint32_t kGiFaceBytes = 60;
constexpr uint32_t kMaxSlots = 128;    // the cell's building id is a byte: 128..255
constexpr uint32_t kBlendSignature = 0xf0627487u;  // RenderDataBlock.typeSignatureHash: the layout of the slot's shader data
constexpr uint32_t kGiSignature = 0xd1bec908u;     // the same for the GI table's shader data (not in the reflection)

// Map handles in a layer: where the loader writes each map's bindless index, and the feedback list's.
constexpr uint32_t kMapOffset[kMapCount] = {0x00, 0x0c, 0x10, 0x14, 0x18, 0x1c, 0x20, 0x24};
constexpr uint32_t kFeedbackOffset = 0x28;
// keen::PixelFormat of each map, as the game's 3,596 building textures have them.
constexpr uint8_t kMapFormat[kMapCount] = {kvb::kFormatBc1Srgb, kvb::kFormatBc7Srgb, kvb::kFormatBc4Unorm, kvb::kFormatBc4Unorm,
                                           kvb::kFormatBc4Unorm, kvb::kFormatBc5Unorm, kvb::kFormatBc4Unorm, kvb::kFormatBc4Unorm};
// GI faces, in the table's order, and the blending layer each one averages.
constexpr int kGiFacePart[3] = {kTop, kSide, kBottom};
constexpr uint32_t kGiEmissiveOffset = 0x34, kGiAlbedoOffset = 0x38;

constexpr uint16_t kStreamBuilding = 2, kStreamGi = 8;  // keen::TextureStreamingCategory
constexpr uint32_t kCategorySurfaceChain = 1013777168u;  // content.category of every building-surface texture
constexpr uint8_t kPatchTexture = 0, kPatchFeedback = 1;  // keen::RenderDataBlockPatchPointType

// ---- the tables, as data ---------------------------------------------------------------------------------------------------------------
// keen::RenderDataBlockTextureParameter
struct TextureParam {
    kvb::StreamedTexture tex;
    uint16_t category = 0;
};
// A patch point, with what it points at. A feedback list names textures of the same slot by their order among the slot's texture patches.
struct Patch {
    uint8_t type = kPatchTexture;
    uint32_t offset = 0;  // within the slot
    uint32_t size = 4;
    TextureParam texture;          // kPatchTexture
    std::vector<uint32_t> list;    // kPatchFeedback
};
// keen::RenderDataBlock, split into slots of `stride` bytes.
struct DataBlock {
    std::string debug_name;
    uint32_t signature = 0;
    uint32_t stride = 0;
    Bytes data;
    std::vector<std::vector<Patch>> slots;  // data.size() / stride entries, each in offset order
};
struct BlendingTable {
    uint32_t layer_count = 0;
    DataBlock block;
};
struct GiTable {
    DataBlock block;
    uint32_t material_count = 0;
};
// keen::DecoObjectRuleSet: 80 B, its three model lists by index into the resource's decoObjectModels
struct DecoRule {
    std::array<uint8_t, 80> raw{};  // the lists' fields zero
    std::vector<uint32_t> models[3];
};
// keen::BuildingMaterialParameters
struct MaterialParams {
    uint8_t mapping = 0;  // keen::BuildingMappingType: Default, Wood, Roof
    uint8_t edge_only_side = 0;
    std::vector<DecoRule> rules;
};
struct ParamsTable {
    std::vector<MaterialParams> materials;
    std::vector<std::array<uint8_t, 16>> models;  // ObjectReference<Model>
};

bool read_blending(const uint8_t* p, size_t n, BlendingTable* out, std::string* why);
bool read_gi(const uint8_t* p, size_t n, GiTable* out, std::string* why);
bool read_params(const uint8_t* p, size_t n, ParamsTable* out, std::string* why);
Bytes write_blending(const BlendingTable& t);
Bytes write_gi(const GiTable& t);
Bytes write_params(const ParamsTable& t);

// ---- one slot, as parts --------------------------------------------------------------------------------------------------------------
struct SlotLayer {
    std::optional<TextureParam> map[kMapCount];  // emissive may be absent; the others are there in every game slot that has the layer
    bool feedback = true;                         // the layer's feedback list: every layer that has maps
    float emissive_intensity = 0, emissive_exposure = 0, tiling = 0.25f, displacement_scale = 0, displacement_offset = 0, emissive_gi = 0;
};
struct GiFace {
    std::array<float, 13> values{};               // albedo rgb, roughness, emissive rgb, emissive gi, exposure, intensity, metallic, reflectance, tiling
    std::optional<TextureParam> emissive, albedo;
};
struct Slot {
    SlotLayer layer[kPartCount];
    float height_blend = 1, height_blend_noise = 1, edge_curve = 1, noise_scale = 2.4f, noise_bias = -0.8f, bevel = 0.01f;
    uint32_t edge_only_side = 1, is_roof = 0;
    GiFace gi[3];
    MaterialParams params;
};
// One slot of the game's tables as parts. false: it holds something the parts can't say (a patch elsewhere, a non-zero handle), and *why says what.
bool slot_of(const BlendingTable& b, const GiTable& g, const ParamsTable& p, uint32_t index, Slot* out, std::string* why);
// Writes a slot into the tables, replacing what was there, growing counts and the parameter list to cover it.
void put_slot(BlendingTable* b, GiTable* g, ParamsTable* p, uint32_t index, const Slot& s);

// ---- streaming layout ------------------------------------------------------------------------------------------------------------
// The mip ranges and the tail's start the game uses for a chain of this size and format (vfx-cook's rule, which all 3,878 building textures follow).
struct ChainLayout {
    std::vector<kvb::StreamRange> ranges;
    uint64_t total = 0, tail_start = 0;
    uint32_t levels = 0;
};
ChainLayout chain_layout(uint32_t w, uint32_t h, uint8_t format);

// ---- building surfaces ----------------------------------------------------------------------------------------------------------
struct SurfaceJob {
    std::string mod, block;   // for messages and names
    std::string dir;          // the mod's folder: the surface's PNGs are relative to it
    std::string label;        // "block buildingMaterial moss-brick (mod x)": what messages start with
    Guid guid;                // the material's or the tweak's: its first field is the textures' bundleId
    uint32_t slot = 0;        // cell id - 128
    bool tweak = false;       // a game material retextured: what isn't written stays the game's
    bool roof = false;        // a new material's roof flag
    bool icon = false;        // draw its block item's icon from the surface (a new material whose block doesn't name one)
    Guid item;                // that block item
    std::optional<ojson> surface;  // the block's "surface"; none: a new material looks like kDefaultSurface
};
constexpr const char* kDefaultSurface = "StoneRough";  // what a new material without a surface draws like: its whole slot

struct ServedBlob {
    ContentHash hash;
    Bytes data;
    std::string what;
};
struct JobReport {
    std::string mod, block;
    uint32_t slot = 0;
    std::string status;            // "written", "default" (no surface, or it failed: errors say why), "skipped"
    uint32_t textures = 0;         // texture parameters in the slot
    uint32_t new_textures = 0;     // distinct ones that aren't the game's
    bool icon = false;             // its block item's icon was drawn
};
struct BuiltIcon {
    std::string mod, block;
    Guid item;
    Bytes png;  // kIconSide square, RGBA
};
constexpr uint32_t kIconSide = 256;  // the game's item icons
struct WriteOptions {
    // A PNG of a mod, power of two, 4..4096 on a side. false: *why says what is wrong with it.
    std::function<bool(const std::string& dir, const std::string& rel, kvb::RawImage* out, std::string* why)> load;
    kvb::TextureCache cache;
    std::function<int(const std::string&)> game_material;          // a GameMaterials name, Cell<id> or GUID -> cell id, -1 unknown
    std::function<bool(const kvb::ContentRef&, Bytes*)> read_blob;  // a game blob, for the averages of a game map; may be empty
    uint32_t streamed_room = UINT32_MAX;                            // new streamed textures that still fit (the game's limit, less what others use)
};
struct WriteResult {
    BlendingTable blend;
    GiTable gi;
    ParamsTable params;
    std::vector<ServedBlob> blobs;  // every chain and mip tail the new slots name, once each
    std::vector<JobReport> jobs;
    std::vector<BuiltIcon> icons;
    std::vector<std::string> errors, warnings;
    uint32_t new_streamed = 0;
};
// Writes every job into copies of the game's tables. A job that fails says why by its label and falls back: a new material to kDefaultSurface,
// a tweak to the game's look. false only when the game's tables don't read.
bool write_surfaces(const BlendingTable& b, const GiTable& g, const ParamsTable& p, const std::vector<SurfaceJob>& jobs, const WriteOptions& opt,
                    WriteResult* out);

// An RGBA PNG, uncompressed (stored deflate blocks).
Bytes png_rgba(uint32_t w, uint32_t h, const Bytes& px);
// The first level of a BC7 chain no wider or taller than max_side, decoded. false: not BC7, or the chain is short.
bool decode_level(const Bytes& chain, uint32_t width, uint32_t height, uint8_t format, uint32_t max_side, kvb::RawImage* out);
// A block of a surface seen from above at an angle: the top face from `top`, the two sides from `side`, shaded; transparent around it.
kvb::RawImage block_icon(const kvb::RawImage& side, const kvb::RawImage& top, uint32_t size = kIconSide);

// The game's layouts these are written against (client 26430625, svn 1287369). A difference turns surfaces off.
const std::vector<kvb::LayoutExpect>& surface_layouts();

// The report of a write as JSON.
ojson report_json(const WriteResult& r);

}  // namespace ksf
