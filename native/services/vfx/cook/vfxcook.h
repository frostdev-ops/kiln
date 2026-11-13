// SPDX-License-Identifier: MIT
// The VfxBlock builder: a definition (the "vfx" section of blocks/*.json, the same shape C# serialises) -> VfxResource,
// VfxMeshResource, RenderTextureResource and their blobs, using Kiln's starter modules. Nothing from the game goes into the output and
// no compiler is needed. Format: docs/kiln/services/vfx-cook.md. Recipe and evidence: docs/re/vfx.md sections 2, 3.6, 4.3, 6.
//
// Portable on purpose: no Windows, no game memory, no Kiln API. kiln-vfx.dll runs it at boot (boot.cpp) and tools/vfxcook/native builds
// the same code as kiln-vfxcook, so what the offline tests prove about the bytes holds for what the game is served.
#pragma once
#include <cstddef>
#include <cstdint>
#include <functional>
#include <map>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "contenthash.h"
#include "nlohmann/json.hpp"

namespace kvb {

using Bytes = std::vector<uint8_t>;
using ojson = nlohmann::ordered_json;

// ---- ids ---------------------------------------------------------------------------------------------------------------------
struct Guid {
    uint8_t b[16] = {};  // the game's byte order (.NET Guid.ToByteArray())
    auto operator<=>(const Guid&) const = default;
};
std::string guid_text(const Guid& g);
// The rule of Kiln.Blocks' BlockIds: UUIDv5 of "<mod>:<kind>:<name>", namespace 6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77.
Guid block_id(const std::string& mod, const std::string& kind, const std::string& name);

// keen:: type hashes (FNV-1a of the qualified name), what kiln.assets keys resources by.
constexpr uint32_t kTypeVfx = 0xe64ee036u;      // keen::VfxResource
constexpr uint32_t kTypeMesh = 0x8e2c1e03u;     // keen::VfxMeshResource
constexpr uint32_t kTypeTexture = 0x3002d39du;  // keen::RenderTextureResource
constexpr uint32_t kTypeGpc = 0x647628d6u;      // keen::GpcResource
constexpr uint32_t kTypeMaterial = 0xaee1894bu; // keen::RenderMaterialResource

// ---- limits ------------------------------------------------------------------------------------------------------------------
constexpr uint32_t kMaxEntries = 55;        // particle entries in one VFX: vanilla's largest
constexpr uint32_t kMaxResourceData = 65532;// gpuResourceData: Range16 offsets, a multiple of 4
constexpr uint32_t kMaxTextureSide = 2048;  // lint KL322; vanilla reaches 4096 but that is 22 MB of content
constexpr uint32_t kMinTextureSide = 4;     // a BC7 block
constexpr float kBurstDuration = 0.001f;    // a burst-only emitter's duration: above 0, or it waits for a stop (docs/re/vfx.md 3.6)
constexpr uint32_t kDefaultKillTime = 10;   // vanilla's value in 1,547 of 1,575

// The compact sprite module of the game's own VFX (docs/re/vfx.md 3.7). "gameSpriteModule" draws a block's particles with it, which tells a
// problem in the data path (resources, patches, meshes, textures) from a problem in Kiln's sprite module. Its blob is the game's, so a game
// update can remove it: the block is refused unless the game has it.
constexpr ContentHash kGameSpriteModule{25472, 2458470088u, 2128813632u, 3226514388u};

// ---- budget (docs/re/vfx.md 4.3; capacities are immediates in VfxSystemCreate 0x198590) ------------------------------------------
// Overflow of any of these is fatal in the game ("too many vfx resources", "too many vfx meshes", "out of vfx mesh memory", ...), so a
// mod that would overflow gets nothing registered.
struct GameCounts {
    uint32_t vfx = 1575, meshes = 638, curves = 8, sounds = 6;
    uint64_t mesh_bytes = 17567564;  // svn 1287369: the sum of every VfxMeshResource blob, all loaded at init
    uint32_t materials = 3403;            // RenderMaterialResources on the Pbr effect: the directory's count less kOtherEffectMaterials
    uint32_t streamed_textures = 13930;   // svn 1287369: distinct textures of the 8 resource types the renderer lists at boot (docs/re/vfx.md 5)
};
struct Capacity {
    uint32_t vfx = 2048, meshes = 1024, curves = 16, sounds = 16;
    uint64_t mesh_bytes = 0x1100000;
    uint64_t mesh_margin = 16384;  // kept free: the allocator's own needs were not read
    uint32_t materials = 4096;            // Pbr materials alive at once (render system create, 0x103695). Over it a model silently doesn't draw
    uint32_t streamed_textures = 16383;   // 16,384 slots less slot 0. "too many streamed textures" is fatal at boot (0xb2fb0)
};
constexpr uint32_t kOtherEffectMaterials = 55;  // svn 1287369: TerraformingCursor 48, SkyTexture 7
struct BudgetNeed {
    uint32_t vfx = 0;
    std::set<Guid> meshes;
    uint64_t mesh_bytes = 0;
    std::set<std::string> sounds;
    std::set<Guid> curves;
    uint32_t materials = 0;
    std::set<std::string> streamed_textures;  // by content hash: the game shares a texture between materials the same way
};
class Budget {
public:
    explicit Budget(const GameCounts& game = {}, const Capacity& cap = {}) : game_(game), cap_(cap) {}
    // Adds `need` if all of it fits, else adds nothing and says what is over. Meshes, sounds and curves already counted cost nothing more.
    bool try_add(const BudgetNeed& need, std::string* why);
    uint32_t room_vfx() const { return left(cap_.vfx, game_.vfx + vfx_); }
    uint32_t room_meshes() const { return left(cap_.meshes, game_.meshes + static_cast<uint32_t>(meshes_.size())); }
    uint32_t room_sounds() const { return left(cap_.sounds, game_.sounds + static_cast<uint32_t>(sounds_.size())); }
    uint32_t room_curves() const { return left(cap_.curves, game_.curves + static_cast<uint32_t>(curves_.size())); }
    uint32_t room_materials() const { return left(cap_.materials, game_.materials + materials_); }
    uint32_t room_streamed_textures() const { return left(cap_.streamed_textures, game_.streamed_textures + static_cast<uint32_t>(streamed_.size())); }
    uint64_t room_mesh_bytes() const;
    uint32_t used_vfx() const { return vfx_; }
    uint32_t used_meshes() const { return static_cast<uint32_t>(meshes_.size()); }
    uint32_t used_sounds() const { return static_cast<uint32_t>(sounds_.size()); }
    uint32_t used_curves() const { return static_cast<uint32_t>(curves_.size()); }
    uint32_t used_materials() const { return materials_; }
    uint32_t used_streamed_textures() const { return static_cast<uint32_t>(streamed_.size()); }
    uint64_t used_mesh_bytes() const { return mesh_bytes_; }
    const GameCounts& game() const { return game_; }
    const Capacity& capacity() const { return cap_; }

private:
    static uint32_t left(uint32_t cap, uint32_t used) { return cap > used ? cap - used : 0; }
    GameCounts game_;
    Capacity cap_;
    uint32_t vfx_ = 0, materials_ = 0;
    uint64_t mesh_bytes_ = 0;
    std::set<Guid> meshes_, curves_;
    std::set<std::string> sounds_, streamed_;
};

// ---- the starter modules: shaders/build/*.spv, embedded as they are ---------------------------------------------------------------
enum class Module { Emitter, Sprite, Light };
struct ModuleInfo {
    const char* name;
    const uint8_t* data;
    size_t size;
    ContentHash hash;
};
const ModuleInfo& starter_module(Module m);
// What the game's pipeline creation expects, and a bad module would be fatal there: SPIR-V <= 1.5, entry cs_main, LocalSize 32 1 1,
// capabilities the game's own modules use. "" = fine, else why not.
std::string check_module(const uint8_t* data, size_t size);

// ---- a mod's own particle hooks (ShaderBlock, level D; cook/shader.cpp, cook/spvlink.cpp, docs/kiln/modding/shaders.md) -------------
// A mod ships a *hook module*: SPIR-V of its hook functions only (kiln_hooks.hlsl + kiln_hooks_main.hlsl), never a whole particle module.
// The cook verifies it and links it into Kiln's own sprite (linkable_sprite()); what the game compiles is that linked module.
constexpr size_t kMaxModModuleBytes = 1u << 20;   // the linked module; the game's largest is 1.76 MB
constexpr uint64_t kHookBudget = 16384;           // instructions one hook call may run: its straight-line cost times the loop cap
struct HookLinkInfo {
    std::string abi;                 // the stamp's hook ABI, "" without one
    std::vector<std::string> hooks;  // "spawn", "update", "draw": those linked
    uint32_t instructions = 0;       // in the hook module's functions
    uint64_t cost = 0;               // the longest hook's straight-line instructions, callees counted at every call
    uint32_t loops = 0;              // loop headers in linked code, each capped
    uint32_t loop_cap = 0;           // loop header passes allowed per hook call (0: no loops)
    uint32_t clamped = 0;            // dynamic indices clamped
    size_t linked_size = 0;
};
using ModModuleInfo = HookLinkInfo;
// "kiln-vfx-hooks-<KILN_HOOKS_ABI>": an OpSourceExtension that kiln-vfxcook shader build writes into a hook module.
std::string hook_abi_marker();
// shaders/build/kiln_sprite_hooked.spv: Kiln's sprite with three empty, separate kiln_hook_* functions (the link points).
const ModuleInfo& linkable_sprite();
// Bounded file read before allocating the module's bytes (untrusted mod input).
bool read_mod_module(const std::string& path, Bytes* out, std::string* why);
// Verifies an untrusted hook module and links it into linkable_sprite(). "" = fine and *linked holds the module to serve; else why not.
std::string link_hook_module(const uint8_t* data, size_t size, Bytes* linked, HookLinkInfo* info);
// The same, for the cook and kiln-vfxcook shader check.
std::string check_mod_module(const uint8_t* data, size_t size, ModModuleInfo* info, Bytes* linked = nullptr);
// Writes this hook ABI's stamp into a hook module (replacing an older one).
bool stamp_module(Bytes* spv, std::string* why);

// ---- per-play parameters (KX_* in kiln_vfx_params.h): the same on every Kiln VFX ---------------------------------------------------
constexpr uint32_t kParamFloat = 0x90b5085du;  // DsTypeId VfxEffectParameterFloat
constexpr uint32_t kParamColor = 0x53039620u;  // DsTypeId VfxEffectParameterColor: RGBA8, sRGB
struct PlayParameter {
    const char* name;
    uint32_t hash;          // FNV-1a of the name, the game's rule
    uint32_t type;          // kParam*
    uint32_t offset, size;  // bytes in the 64-byte user block
};
const std::vector<PlayParameter>& play_parameters();
// defaultParameterData: white, factors 1, and the KX_MAGIC word.
Bytes play_defaults();

// ---- definition ----------------------------------------------------------------------------------------------------------------
struct Rangef {
    float min = 0, max = 0;
};
struct ParticleDef {
    uint32_t burst = 0;
    float rate = 0, duration = 0, delay = 0;
    bool has_duration = false, loop = false;
    uint32_t max_count = 0;  // 0: derived
    Rangef life{1, 1}, speed{1, 1}, spin, roll;  // spin and roll in radians
    uint32_t spread = 0;     // KS_SPREAD_*
    float cone = 0;          // radians
    float spawn_radius = 0, drag = 0;
    uint32_t shape = 0;                 // KS_SHAPE_*; "spawnRadius" is a sphere
    float shape_size[3] = {0, 0, 0};    // KS_SHAPE_A, B, C
    bool align_velocity = false;        // "align": "velocity"
    float stretch = 0;                  // metres per m/s
    float user[8] = {};                 // KS_USER: "shader".constants
    std::string shader;                 // a mod's own sprite module: a .spv in the mod folder (docs/kiln/modding/shaders.md); empty: Kiln's
    ContentHash shader_hash{};          // set by cook_mods once the module passed its checks; empty: Kiln's sprite draws it
    float accel[3] = {0, 0, 0};
    float size[8], alpha[8], color[8][3];
    std::string texture;     // relative to the mod folder; empty: white
    bool texture_linear = false;
    std::string material;    // a MaterialDef of this mod: the texture, tint and lit unless the particle says otherwise
    bool texture_set = false, color_set = false, lit_set = false;
    uint8_t address[3] = {0, 0, 0};  // keen::RenderSamplerAddressMode: Wrap
    uint32_t flip_columns = 0, flip_rows = 0;
    float flip_fps = 0;
    bool additive = false, lit = false, face_camera = true;
    std::string mesh = "quad";
    float depth_fade = 0, emissive = 0;
    ParticleDef();
};
struct LightDef {
    float lifetime = 0, fade_in = 0, fade_out = 0, radius = 6, brightness = 1, flicker = 0;
    float color[3] = {1, 1, 1};
    float offset[3] = {0, 0, 0};
};
struct VfxDef {
    std::string name;     // the block's name
    std::string id_name;  // what its id follows from: the oldest name ("was"), else the name
    std::string where;    // "blocks/x.json: vfx.name", for messages
    std::vector<ParticleDef> particles;
    std::optional<LightDef> light;
    std::string sound;    // counted in the budget; not written yet (needs kiln_sound, docs/re/vfx.md 3.6)
    float cull_min = 50, cull_max = 75;
    bool game_sprite = false;  // a diagnostic: draw with one of the game's own sprite modules (see kGameSpriteModule)
};

struct Diagnostics {
    std::vector<std::string> errors, warnings;
    std::vector<std::string> notes;  // what the author chose on purpose and should still hear about once (gameSpriteModule)
};

// A JSON document as blocks/*.json and kiln.mod.json are written: // and /* */ comments, a comma before } or ]. Discarded when invalid.
ojson parse_relaxed(const std::string& text);

// One block of the "vfx" section. false: something is wrong and d->errors says what, by block and field.
bool parse_block(const ojson& value, const std::string& name, const std::string& where, VfxDef* out, Diagnostics* d);

// ---- materials (docs/re/vfx.md 5, docs/kiln/modding/materials.md) -----------------------------------------------------------------
constexpr uint8_t kEffectPbr = 1;                // keen::RenderEffectId::Pbr
constexpr uint32_t kPbrSignature = 0x1220f31fu;  // the layout hash of keen::PbrRfxRenderMaterial
constexpr uint32_t kPbrBlockSize = 244;
// keen::RenderMaterialFlag. AlphaTested, DoubleSided, DisableShadows and HashedAlpha reach RenderMeshDrawData2.pipelineBits, which picks the pipeline.
constexpr uint8_t kMatAlphaTested = 1u << 1, kMatDoubleSided = 1u << 2, kMatDisableShadows = 1u << 3, kMatCanDissolve = 1u << 4, kMatTranslucent = 1u << 5,
                  kMatFoliage = 1u << 6, kMatHashedAlpha = 1u << 7;
// keen::PbrRfxRenderMaterialFlag
constexpr uint32_t kPbrDissolve = 1u << 0, kPbrHashedAlpha = 1u << 1, kPbrSampleAo = 1u << 3, kPbrSampleOpacity = 1u << 4, kPbrContactShadows = 1u << 5,
                   kPbrFlipBackfaceNormals = 1u << 7, kPbrDisableFrost = 1u << 9, kPbrDisableSnow = 1u << 10, kPbrDisableWetness = 1u << 11, kPbrFoliageTranslucency = 1u << 14;
// keen::PbrRfxRenderMaterial's fields
constexpr uint32_t kPbrAlbedoMap = 0x14, kPbrNormalMap = 0x1c, kPbrEmissiveMap = 0x24, kPbrParamsMap = 0x2c, kPbrTexCoordScaling = 0x3c, kPbrConstantReflectance = 0x5c,
                   kPbrLayerFeedback = 0x64, kPbrHeightBlendingRange = 0x8c, kPbrEmissiveSwizzle = 0x94, kPbrAlbedoSwizzle = 0x98, kPbrAoSwizzle = 0x9c,
                   kPbrTranslucencyStrength = 0xa0, kPbrEmissiveShaderData = 0xa8, kPbrDissolveColor = 0xd0, kPbrDissolveWidthInv = 0xdc, kPbrDissolveParam = 0xe0,
                   kPbrDissolveNoiseMap = 0xe4, kPbrDissolveNoiseTiling = 0xe8, kPbrDissolveFadeRange = 0xec, kPbrFlags = 0xf0;
constexpr uint32_t kSwizzleNone = 255;        // a vertex colour channel: none
constexpr uint32_t kDissolveNoiseMap = 2;     // the entry of g_renderNoiseMaps nearly all of the game's dissolving materials use
constexpr uint32_t kCategoryMaterialChain = 1840640251u, kCategoryMipTail = 1185060183u;  // content.category and mipTailContent.category on all 13,207 images
constexpr uint32_t kMaxFeedbackImages = 8;    // more is fatal in the loader: "SmallDynamicArray is out of elements"
constexpr uint32_t kMaxMipRanges = 16;        // more is fatal in the texture system
constexpr uint32_t kMaxMaterialTextureSide = 4096;
constexpr float kDefaultReflectance = 0.5028854608535767f;  // 3,070 of the game's 3,403

// keen::PixelFormat
constexpr uint8_t kFormatBc1Unorm = 131, kFormatBc1Srgb = 132, kFormatBc4Unorm = 139, kFormatBc5Unorm = 141, kFormatBc7Unorm = 145, kFormatBc7Srgb = 146;
// 8 bytes a 4x4 block (BC1, BC4), else 16; mips of the sRGB formats are averaged in linear light.
constexpr uint32_t format_block_bytes(uint8_t f) { return f == kFormatBc1Unorm || f == kFormatBc1Srgb || f == kFormatBc4Unorm ? 8 : 16; }
constexpr bool format_srgb(uint8_t f) { return f == kFormatBc7Srgb || f == kFormatBc1Srgb; }

// A ResourceContentReference.
struct ContentRef {
    ContentHash hash;
    uint32_t category = 0, bundle = 0;
    uint64_t sort = 0;
};
struct StreamRange {
    uint32_t start = 0, count = 0;
};
// keen::RenderStreamedTexture
struct StreamedTexture {
    uint16_t width = 0, height = 0;
    uint8_t levels = 0, format = 0;
    ContentRef content, tail;
    std::vector<StreamRange> ranges;
    std::string debug_name;
};
struct MaterialImage {
    uint32_t data_offset = 0;
    StreamedTexture texture;
};
struct MaterialFeedback {
    uint32_t data_offset = 0;
    std::vector<uint32_t> images;
};
// keen::RenderMaterialResource, any effect
struct RenderMaterial {
    uint8_t effect = 0, flags = 0;
    Bytes data;
    uint32_t signature = 0;
    std::vector<MaterialImage> images;
    std::vector<MaterialFeedback> feedbacks;
    std::string debug_name;
};
Bytes write_material(const RenderMaterial& m);
bool read_material(const uint8_t* p, size_t n, RenderMaterial* out, std::string* why);

// RGBA8, top row first
struct RawImage {
    uint32_t w = 0, h = 0;
    Bytes px;
};

// A plain host file reader by default; boot.cpp supplies one that handles wide paths.
using ReadFile = std::function<bool(const std::string& utf8_path, Bytes* out, std::string* why)>;
// A cache of encoded mip chains by key (kiln/data/vfx/cache). Either may be empty.
struct TextureCache {
    std::function<bool(const std::string& key, Bytes* out)> get;
    std::function<void(const std::string& key, const Bytes& data)> put;
};

// One block of the "materials" section. Everything a kind sets is in `kind_defaults`, so a field written in the block always wins.
struct MaterialDef {
    std::string name, id_name, where, kind = "opaque";
    std::string albedo;                     // PNG, sRGB; alpha is what alphaTest cuts by
    float tint[3] = {1, 1, 1};              // linear; multiplies albedo, or is the albedo without a map
    std::string normal;                     // PNG; R, G
    bool normal_y_down = false;
    float normal_strength = 1;
    std::string params;                     // PNG in the game's channels: R metallic, G roughness, B occlusion, A reflectance
    std::string metallic_roughness;         // PNG in glTF's channels: G roughness, B metallic
    std::string occlusion;                  // PNG; R
    bool has_roughness = false, has_metallic = false;
    float roughness = 0.7f, metallic = 0;   // the value without a map, a factor with one
    float reflectance = kDefaultReflectance;
    bool reflectance_from_params = false;   // params' alpha
    bool has_emissive = false;
    std::string emissive_map;               // PNG, sRGB
    float emissive_color[3] = {1, 1, 1};    // linear
    float emissive_strength = 1, emissive_exposure = 0.75f;
    bool alpha_test = false, hashed = false, double_sided = false, shadows = true, contact_shadows = true, frost = true, snow = true, wetness = true,
         vertex_color = false;
    float alpha_cutoff = 0.5f;
    float translucent = 0;
    bool dissolve = false;
    float dissolve_color[3] = {1, 1, 1};
    float dissolve_width = 0.05f, dissolve_tiling = 0.5f, dissolve_fade = 0.3f;
    float uv_scale[2] = {1, 1};
    std::string shader;                     // a ShaderBlock's name, for when surface shaders exist. Today: refused by name
    std::optional<Guid> replaces;           // a game material this one draws in place of: built under its GUID, served as an override
};
// "6f…-…" (.NET Guid text) -> the game's byte order. false: not a GUID.
bool parse_guid(const std::string& text, Guid* out);
// What a material block is registered under: the game material it replaces, else UUIDv5(<mod>:material:<id name>).
Guid material_guid(const MaterialDef& d, const std::string& mod);
bool parse_material(const ojson& value, const std::string& name, const std::string& where, MaterialDef* out, Diagnostics* d);
void material_flags(const MaterialDef& d, uint8_t* render, uint32_t* block);
// What the game draws a material with, by the flags: names in renderer/renderer.gpc. For reports and tests.
std::vector<std::string> material_pipelines(uint8_t render);

// A full mip chain in one block format, largest level first, with its streaming layout.
struct EncodedChain {
    uint32_t width = 0, height = 0, levels = 0;
    uint8_t format = 0;
    Bytes data;
    std::vector<StreamRange> ranges;
    size_t tail_start = 0;  // where the first level no range starts at begins
    std::string cache_key;  // the TextureCache key it is kept under (chain_cache_key); empty for a chain no cache saw
};
inline constexpr const char* kEncoderVersion = "bc7-1";  // part of every texture cache key, and of the carry-over index
// The TextureCache key of a chain: the pixels' hash, the size, the format and the encoder's version.
std::string chain_cache_key(const ContentHash& pixels, uint32_t w, uint32_t h, uint8_t format);
// The chain encode_chain gives for the pixels whose key is `key`, from the cache alone (no pixels): false when the cache has no entry of the
// chain's size under that key (then the pixels have to be encoded, as encode_chain does).
bool chain_from_cache(const std::string& key, uint32_t w, uint32_t h, uint8_t format, const TextureCache& cache, EncodedChain* out);
struct MaterialMap {
    uint32_t slot = 0;  // kPbr*Map
    const char* what = "";
    EncodedChain chain;
};
// Reads a PNG the definition names. false: *why says what is wrong with the file.
using ImageLoader = std::function<bool(const std::string& rel, RawImage* out, std::string* why)>;
// The maps of a material, built from what it names and from constants for what it doesn't: the game's shader reads albedo, normal and params unconditionally.
// false: *field is the field at fault and *why what is wrong with it.
bool material_maps(const MaterialDef& d, const ImageLoader& load, const TextureCache& cache, std::vector<MaterialMap>* out, std::string* field, std::string* why);
// What material_maps reads and makes for a definition, without reading a pixel: the files it opens in the order it opens them (each once),
// and each map's slot, name and format in its order. The carry-over index is checked against this.
struct MaterialPlan {
    std::vector<std::string> inputs;
    struct Map {
        uint32_t slot;
        const char* what;
        uint8_t format;
    };
    std::vector<Map> maps;
};
MaterialPlan material_plan(const MaterialDef& d);
// The rule every path a block names must pass before a file is opened: relative, inside the mod's folder (pack.cpp).
bool safe_relative_path(const std::string& rel);
struct BuiltMaterial {
    Guid guid;
    Bytes resource;
    uint8_t render_flags = 0;
    uint32_t block_flags = 0;
};
bool build_material(const MaterialDef& d, const std::string& mod, const std::vector<MaterialMap>& maps, BuiltMaterial* out, std::string* why);

// ---- what comes out ------------------------------------------------------------------------------------------------------------
struct ServedBlob {
    ContentHash hash;
    Bytes data;
    std::string what;
};
struct ServedResource {
    Guid guid;
    uint32_t type = 0;
    Bytes data;
    std::string what;
    bool override = false;  // replaces a game resource of this GUID (a material's "replaces"): served as an override, not a new key
    std::string mod;        // with override: the mod it belongs to. kiln.vfx serves it under an owner of that mod's, never its own
    bool standby = false;   // with override: an earlier mod in the load order replaces the same material; this one draws only if that one unloads
};

struct BuiltTexture {
    Guid guid;
    Bytes resource, blob;
    ContentHash hash;
    uint32_t width = 0, height = 0, levels = 0;
};

// PNG -> BC7 mip chain -> RenderTextureResource. Power of two, kMinTextureSide..kMaxTextureSide, or it says why not.
bool build_texture(const Bytes& png, const std::string& mod, const std::string& rel, bool linear, const TextureCache& cache, BuiltTexture* out,
                   std::string* why);

// A PNG's pixels. Power of two, kMinTextureSide..max_side, or it says why not.
bool decode_png(const Bytes& png, uint32_t max_side, RawImage* out, std::string* why);
// A resized copy, for maps of one material that differ in size. Bilinear.
RawImage resize_image(const RawImage& s, uint32_t w, uint32_t h);
constexpr uint32_t kSolidTextureSide = 16;  // the game's constant maps are this size
RawImage solid_image(uint8_t r, uint8_t g, uint8_t b, uint8_t a);
uint8_t linear_to_srgb_byte(float linear);
// Multiplies colour in linear light, as a shader multiplying a texture would; alpha stays.
void multiply_srgb(RawImage* image, const float linear[3]);
// A full chain in a format above: mips averaged (colour in linear light when srgb), encoded on every core up to 8. Cached by the pixels.
EncodedChain encode_chain(const RawImage& image, uint8_t format, const TextureCache& cache);

// The mesh every sprite uses unless it says otherwise. Built from docs/re/vfx.md 2.5's numbers. Blob sizes are multiples of 28: the game
// computes vertex offsets as allocation offset / 28, so any other size would shift every mesh loaded after it.
struct BuiltMesh {
    Guid guid;
    Bytes resource, blob;
    ContentHash hash;
    uint32_t vertices = 0, indices = 0;
};
bool builtin_mesh(const std::string& name, BuiltMesh* out);
// 28*V + 2*I padded to a multiple of 28. Exposed for lint and tests.
Bytes pad_mesh_blob(Bytes blob);

// The bytes of one VfxResource, with `mesh_of` and `tex_of` giving the GUIDs of what a particle names.
struct BuiltVfx {
    Guid guid;
    Bytes resource;
    uint32_t entries = 0, emitters = 0, kill_time = 0, max_count_total = 0;
    float stop_after = -1;   // seconds until Kiln should stop a one-shot play. < 0: it runs until stopped
    bool loops = false;
};
bool build_vfx(const VfxDef& def, const std::string& mod, const std::function<Guid(const ParticleDef&)>& mesh_of,
               const std::function<Guid(const ParticleDef&)>& tex_of, BuiltVfx* out, std::string* why);

// ---- a whole set of mods ---------------------------------------------------------------------------------------------------------
struct ModInput {
    std::string id;   // the manifest's id
    std::string dir;  // UTF-8, the mod's folder: textures are relative to it
    std::string side = "both", clients = "mod";
    bool advanced = false;  // the manifest's "advanced": a mod's own particle modules need it
    // (file name for messages, text of a blocks/*.json or a pending definition)
    std::vector<std::pair<std::string, std::string>> documents;
    // The same kind of text, read only so a particle can name a material of its mod: a definition from code comes alone, and its mod's data files are here.
    std::vector<std::string> lookup_documents;
};
struct BuiltBlock {
    std::string mod, name;
    Guid guid;
    uint32_t entries = 0, emitters = 0, kill_time = 0;
    float stop_after = -1;
    bool loops = false;
    uint64_t definition = 0;  // a hash of the block's JSON: a same definition need not be rebuilt
};
struct BuiltMaterialBlock {
    std::string mod, name;
    Guid guid;
    uint8_t render_flags = 0;
    uint32_t block_flags = 0;
    uint32_t textures = 0;
    uint64_t definition = 0;
    bool replaces = false;  // built under a game material's GUID, served as an override
};
// What one material block was built from and into (kiln-vfx's boot build records it): its definition (the hash of its block's JSON,
// as BuiltMaterialBlock.definition), every file it read with the hash of that file's bytes, and what the build gave that a report or the
// budget reads. A later cook of the same mod, block name and definition whose files still have those bytes gets exactly this from
// material_maps and build_material (they are deterministic in those inputs), so it can take it without decoding a texture.
struct MaterialRecord {
    std::string mod, name;
    uint64_t definition = 0;
    std::vector<std::pair<std::string, ContentHash>> files;  // the path the block names (relative to the mod's folder) -> its bytes' hash
    Guid guid;
    uint8_t render_flags = 0;
    uint32_t block_flags = 0;
    uint32_t textures = 0;
    std::vector<ContentHash> chains;  // each map's encoded chain: the budget counts streamed textures by it
    // For a later start (kiln-vfx's carry-over index, carry.cpp): the definition itself, and each map as material_maps made it, by the
    // key its chain has in the texture cache.
    std::string definition_text;
    struct Map {
        uint32_t slot = 0;
        std::string what;
        uint32_t width = 0, height = 0;
        uint8_t format = 0;
        std::string key;
    };
    std::vector<Map> maps;
    uint32_t age = 0;  // starts since it was last built or carried (the index drops old ones)
};

// ---- the carry-over index (kiln-vfx: <data>/vfx/cache/index.json, carry.cpp) ----------------------------------------------------
// The material records of the last starts, so that a start whose mods did not change takes each material's maps from the texture cache
// instead of decoding its textures. The header says what made them: an index of another format, another Kiln build, encoder, game build
// or side is not used at all. Each record is used only after its files hash the same (pack.cpp), and only when the cache holds its chains.
constexpr uint32_t kCarryIndexFormat = 1;
constexpr size_t kCarryIndexMaxBytes = 16u << 20, kCarryIndexMaxRecords = 4096;
constexpr uint32_t kCarryIndexMaxAge = 8;  // a record no start used in this many starts is dropped
struct CarryHeader {
    std::string kiln;  // Kiln's version and build stamp
    std::string game;  // the game exe's SHA-256
    std::string side;  // client | server
};
std::string carry_index_json(const CarryHeader& header, const std::vector<MaterialRecord>& records);
// false (and *why) for anything that is not a well-formed index of this header; then *out is empty.
bool read_carry_index(const std::string& text, const CarryHeader& header, std::vector<MaterialRecord>* out, std::string* why);
// The index to write after a start: this start's records, then the earlier index's records of blocks this start did not have, a start
// older; none older than kCarryIndexMaxAge, at most kCarryIndexMaxRecords (this start's first).
std::vector<MaterialRecord> carry_merge(const std::vector<MaterialRecord>& now, const std::vector<MaterialRecord>& before);
struct CookOptions {
    GameCounts game;
    Capacity capacity;
    ReadFile read_file;   // default: std::ifstream
    TextureCache cache;
    std::function<bool(const ContentHash&)> game_has_blob;  // empty: unknown, so what needs a game blob is refused
    std::function<bool(const Guid&)> game_has_material;  // boot: authoritative game keys, not the assets override map; empty: offline/unknown
    bool skip_vfx = false, skip_materials = false;          // what the fingerprint turned off
    bool skip_shaders = false;                              // kiln/config/vfx.json "shaders": false: mods' own modules fall back to Kiln's sprite
    std::set<std::string> skip_shader_mods;                 // "skipShaderMods": the same for these mods only
    // Called before each mod's VFX block or material is built (kiln-vfx's boot: progress.json's detail). mod: its id,
    // nth/mods: which of the mods, kind: "VFX" or "material", done/total: the blocks of that mod. Empty: nothing.
    std::function<void(const std::string& mod, size_t nth, size_t mods, const char* kind, const std::string& name, size_t done, size_t total)> progress;
    // Each material built from its textures, as it was built (the boot build keeps them). Empty: nothing.
    std::function<void(const MaterialRecord&)> record_material;
    // A record for this mod, block and definition, or null (define_json asks the boot build's). With one whose every file still has the
    // recorded bytes, the material is not built again: what it gives is taken from the record, and its textures are not served in the
    // result (CookResult.materials_reused counts it). Everything else (parsing, warnings, budget, report) runs as for a built one.
    std::function<const MaterialRecord*(const std::string& mod, const std::string& name, uint64_t definition)> known_material;
    // A record of an earlier start (kiln-vfx's carry-over index) for this mod, block and definition text, or null. With one whose every
    // file still has the recorded bytes and whose every chain the texture cache still holds, the material's maps are taken from the cache
    // without decoding or hashing a pixel, and the material is built from them as always (CookResult.materials_carried counts it). Anything
    // missing: built in full as without a record (carry_notes says why).
    std::function<const MaterialRecord*(const std::string& mod, const std::string& name, const std::string& definition)> carried_material;
};
struct CookResult {
    std::vector<ServedBlob> blobs;
    std::vector<ServedResource> resources;  // meshes and textures come before the VFX that name them
    std::vector<BuiltBlock> blocks;
    std::vector<BuiltMaterialBlock> materials;
    std::vector<std::string> errors, warnings, notes;
    std::vector<std::string> refused_mods;  // refused by the budget or a manifest rule, whole
    uint32_t mods_with_vfx = 0;
    uint32_t mod_modules = 0;                // mods' own particle modules served
    uint32_t used_vfx = 0, used_meshes = 0, used_sounds = 0, used_curves = 0, used_materials = 0, used_streamed_textures = 0;
    uint32_t materials_built = 0, materials_reused = 0;  // built from their textures; taken from a MaterialRecord
    uint32_t materials_carried = 0;                      // maps from the texture cache by a carried MaterialRecord
    std::vector<std::string> carry_notes;                // why a material with a carried record was built in full
    uint64_t used_mesh_bytes = 0;
    GameCounts game;
    Capacity capacity;
};
CookResult cook_mods(const std::vector<ModInput>& mods, const CookOptions& opt);

// The result as JSON: blocks with their GUIDs and timing, errors, warnings, notes, refusals, the budget.
ojson report_json(const CookResult& r);

// ---- fingerprint ----------------------------------------------------------------------------------------------------------------
// The modules and the builder were made against these layouts (client 26430625, svn 1287369). A difference turns mod particles off and
// says so. Game VFX and playing them from code are not affected.
struct LayoutExpect {
    const char* name;
    uint32_t type_hash;
    uint32_t layout;
};
const std::vector<LayoutExpect>& expected_layouts();
// The same for what materials are written against. A change here turns off new materials, and particles stay on.
const std::vector<LayoutExpect>& material_layouts();
constexpr uint32_t kBindingHash = 0xe69e178eu;  // FNV-1a over VfxUpdateDescriptorSetLayout's bindings: see binding_hash_of_gpc
// GUID of vfx/vfx.gpc
Guid vfx_gpc_guid();

struct LayoutFound {
    const char* name;
    uint32_t expected = 0, found = 0;  // found 0: the record was not there
};
// A view of a PE image laid out by RVA. `base`: the pointers inside it are absolute at this base (the actual base in the running process,
// the preferred one in a file loaded by load_pe_file).
struct ImageView {
    const uint8_t* image = nullptr;
    size_t size = 0;
    uint64_t base = 0;
};
// Finds the game's reflection records for expected_layouts() (the record layout is in tools/kfc/kfc/refl.py) and reads their layout hashes.
// Reads .data and .rdata only; nothing there needs the game's static init to have run.
std::vector<LayoutFound> read_layouts(const ImageView& img);
std::vector<LayoutFound> read_layouts(const ImageView& img, const std::vector<LayoutExpect>& want);
bool load_pe_file(const std::string& path, Bytes* image, uint64_t* base, std::string* why);
// "" when every layout matches, else one line naming each difference.
std::string compare_layouts(const std::vector<LayoutFound>& found);

// The binding list of VfxUpdateDescriptorSetLayout out of a GpcResource's bytes (vfx/vfx.gpc). false: not found or malformed.
bool binding_hash_of_gpc(const uint8_t* gpc, size_t len, uint32_t* out, uint32_t* count);

// Type bundles of a KFC4 directory (<game>/enshrouded.kfc): the count and layout hash per resource type, read from the tables, no
// decompression. Gives the budget the real number of game VFX, meshes and curves on whatever build this is.
struct KfcBundle {
    uint32_t layout = 0, count = 0;
};
bool read_kfc_bundles(const std::string& path, std::map<uint32_t, KfcBundle>* out, std::string* why);
// Whether the directory lists a content blob.
bool kfc_has_content(const std::string& path, const ContentHash& hash);
// GameCounts for this game: counts from the bundles, the rest from the values measured on the fingerprint's build.
GameCounts game_counts_from(const std::map<uint32_t, KfcBundle>& bundles);

}  // namespace kvb
