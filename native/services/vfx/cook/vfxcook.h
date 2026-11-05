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
};
struct Capacity {
    uint32_t vfx = 2048, meshes = 1024, curves = 16, sounds = 16;
    uint64_t mesh_bytes = 0x1100000;
    uint64_t mesh_margin = 16384;  // kept free: the allocator's own needs were not read
};
struct BudgetNeed {
    uint32_t vfx = 0;
    std::set<Guid> meshes;
    uint64_t mesh_bytes = 0;
    std::set<std::string> sounds;
    std::set<Guid> curves;
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
    uint64_t room_mesh_bytes() const;
    uint32_t used_vfx() const { return vfx_; }
    uint32_t used_meshes() const { return static_cast<uint32_t>(meshes_.size()); }
    uint32_t used_sounds() const { return static_cast<uint32_t>(sounds_.size()); }
    uint32_t used_curves() const { return static_cast<uint32_t>(curves_.size()); }
    uint64_t used_mesh_bytes() const { return mesh_bytes_; }
    const GameCounts& game() const { return game_; }
    const Capacity& capacity() const { return cap_; }

private:
    static uint32_t left(uint32_t cap, uint32_t used) { return cap > used ? cap - used : 0; }
    GameCounts game_;
    Capacity cap_;
    uint32_t vfx_ = 0;
    uint64_t mesh_bytes_ = 0;
    std::set<Guid> meshes_, curves_;
    std::set<std::string> sounds_;
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
    float accel[3] = {0, 0, 0};
    float size[8], alpha[8], color[8][3];
    std::string texture;     // relative to the mod folder; empty: white
    bool texture_linear = false;
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
};

// A JSON document as blocks/*.json and kiln.mod.json are written: // and /* */ comments, a comma before } or ]. Discarded when invalid.
ojson parse_relaxed(const std::string& text);

// One block of the "vfx" section. false: something is wrong and d->errors says what, by block and field.
bool parse_block(const ojson& value, const std::string& name, const std::string& where, VfxDef* out, Diagnostics* d);

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
};

struct BuiltTexture {
    Guid guid;
    Bytes resource, blob;
    ContentHash hash;
    uint32_t width = 0, height = 0, levels = 0;
};

// A plain host file reader by default; boot.cpp supplies one that handles wide paths.
using ReadFile = std::function<bool(const std::string& utf8_path, Bytes* out, std::string* why)>;
// A cache of encoded mip chains by key (kiln/data/vfx/cache). Either may be empty.
struct TextureCache {
    std::function<bool(const std::string& key, Bytes* out)> get;
    std::function<void(const std::string& key, const Bytes& data)> put;
};

// PNG -> BC7 mip chain -> RenderTextureResource. Power of two, kMinTextureSide..kMaxTextureSide, or it says why not.
bool build_texture(const Bytes& png, const std::string& mod, const std::string& rel, bool linear, const TextureCache& cache, BuiltTexture* out,
                   std::string* why);

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
    // (file name for messages, text of a blocks/*.json or a pending definition)
    std::vector<std::pair<std::string, std::string>> documents;
};
struct BuiltBlock {
    std::string mod, name;
    Guid guid;
    uint32_t entries = 0, emitters = 0, kill_time = 0;
    float stop_after = -1;
    bool loops = false;
    uint64_t definition = 0;  // a hash of the block's JSON: a same definition need not be rebuilt
};
struct CookOptions {
    GameCounts game;
    Capacity capacity;
    ReadFile read_file;   // default: std::ifstream
    TextureCache cache;
    std::function<bool(const ContentHash&)> game_has_blob;  // empty: unknown, so what needs a game blob is refused
};
struct CookResult {
    std::vector<ServedBlob> blobs;
    std::vector<ServedResource> resources;  // meshes and textures come before the VFX that name them
    std::vector<BuiltBlock> blocks;
    std::vector<std::string> errors, warnings;
    std::vector<std::string> refused_mods;  // refused by the budget or a manifest rule, whole
    uint32_t mods_with_vfx = 0;
    uint32_t used_vfx = 0, used_meshes = 0, used_sounds = 0, used_curves = 0;
    uint64_t used_mesh_bytes = 0;
    GameCounts game;
    Capacity capacity;
};
CookResult cook_mods(const std::vector<ModInput>& mods, const CookOptions& opt);

// The result as JSON: blocks with their GUIDs and timing, errors, warnings, refusals, the budget.
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
