// SPDX-License-Identifier: MIT
// kiln.assets internals (native/services/assets). Public contract: native/core/abi/services/kiln_assets.h.
#pragma once
#include <windows.h>

#include <atomic>
#include <cstdint>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "kiln_api.h"
#include "services/kiln_assets.h"

namespace ka {

extern const KilnApi* g_api;
extern KilnOwner g_self;
extern bool g_client;

void logf(int level, const char* fmt, ...);
#define LOGI(...) ::ka::logf(KILN_LOG_INFO, __VA_ARGS__)
#define LOGW(...) ::ka::logf(KILN_LOG_WARN, __VA_ARGS__)
#define LOGE(...) ::ka::logf(KILN_LOG_ERROR, __VA_ARGS__)
#define LOGD(...) ::ka::logf(KILN_LOG_DEBUG, __VA_ARGS__)

// ------------------------------------------------------------------------------------------- keys ------------
struct Key {  // the game's ResourceId without its 8 reserved bytes
    uint8_t g[16];
    uint32_t type;
    uint32_t part;
    bool operator==(const Key& o) const { return std::memcmp(this, &o, sizeof(Key)) == 0; }
};
static_assert(sizeof(Key) == 24);
struct KeyHash {
    size_t operator()(const Key& k) const {
        uint64_t a, b;
        std::memcpy(&a, k.g, 8);
        std::memcpy(&b, k.g + 8, 8);
        return static_cast<size_t>(a * 0x9E3779B97F4A7C15ull ^ b ^ (static_cast<uint64_t>(k.type) << 32 | k.part));
    }
};
struct Guid16 {
    uint8_t g[16];
    bool operator==(const Guid16& o) const { return std::memcmp(g, o.g, 16) == 0; }
};
struct GuidHash {
    size_t operator()(const Guid16& k) const {
        uint64_t a, b;
        std::memcpy(&a, k.g, 8);
        std::memcpy(&b, k.g + 8, 8);
        return static_cast<size_t>(a * 0x9E3779B97F4A7C15ull ^ b);
    }
};
struct HashK {  // keen::ContentHash
    uint32_t size, h0, h1, h2;
    bool operator==(const HashK& o) const { return std::memcmp(this, &o, sizeof(HashK)) == 0; }
};
struct HashKHash {
    size_t operator()(const HashK& k) const {
        return static_cast<size_t>((static_cast<uint64_t>(k.h0) << 32 | k.h1) * 0x9E3779B97F4A7C15ull ^ k.h2 ^ k.size);
    }
};

uint32_t fnv1a(const void* p, size_t n);
uint32_t type_hash(const std::string& name_or_hex, bool* ok);  // "keen::X" -> fnv1a, "0x..." -> value
std::string type_name(uint32_t hash);                           // known names, else "0x%08x"
bool parse_guid(const std::string& s, uint8_t out[16]);         // .NET Guid.Parse + ToByteArray order
std::string guid_str(const uint8_t g[16]);
std::string key_str(const Key& k);
std::string hash_str(const HashK& h);  // "size-h0-h1-h2" hex
bool parse_hash(const std::string& s, HashK* out);
HashK content_hash_of(const void* data, size_t len);  // SHA-256 rule (kiln_assets.h / docs)

// What of an assets.json only a game client uses, by what the entry is: a dedicated server skips it, whether or not its file is there
// (a server's cook makes no model, and the manifest is the same file on both sides). The types are the cook's list
// (tools/modelcook/cook/recipe.py CLIENT_ONLY_TYPES: what a dedicated server's files never hold).
bool client_only_type(uint32_t type);
struct ManifestItem {  // one entry, as far as the rule looks at it
    std::string kind, file;
    uint32_t type = 0;                     // of a resource
    std::vector<std::string> patch_blobs;  // the blobs a resource names in "patch"
    bool fixed_hash = false;               // a blob with "hash": it overrides the game's content of that hash, whoever reads it
};
// For each entry: a resource of a client-only type, or a blob that only such resources patch in (a model's render data).
// A blob with a fixed "hash" is never one: it replaces game content a server mod may read by that hash, whatever else
// names the file. Only "side": "client" in the manifest keeps such a blob from a server.
std::vector<bool> client_only_entries(const std::vector<ManifestItem>& items);

// ------------------------------------------------------------------------------------------- engine ----------
// Game function signatures (MS x64). Layouts: docs/re/resources.md.
struct DataSpan {  // ResourceData's 24-byte result
    const uint8_t* data;
    uint64_t size;
    uint64_t flags;  // the game returns {u32 0, u32 1}
};
struct TypeList {  // ResourcesByType's result
    uint8_t error;
    uint8_t pad[3];
    uint32_t zero;
    const uint8_t* keys;  // 32-byte ResourceIds
    uint64_t count;
};
struct KeyOfResult {  // ResourceKeyOf's result: {u8 error, u32, ResourceId (32 B)}
    uint8_t error;
    uint8_t pad[3];
    uint32_t zero;
    uint8_t key[32];
};

// The service object (S) seen by the hooks; 0 until the resource system is up.
extern std::atomic<uintptr_t> g_S;

// Calls of the game's own functions (the hook trampolines). Valid when the resources feature is on.
uint32_t game_handle(uintptr_t S, const Key& k);                     // 0 = not in the game
bool game_data(uintptr_t S, uint32_t handle, DataSpan* out);
bool game_type_list(uintptr_t S, uint32_t type, const uint8_t** keys, uint64_t* count);
bool game_key_of(uintptr_t S, uint32_t handle, Key* out);

// Hooks (hooks.cpp)
bool install_resource_hooks(std::string* why);
bool install_content_hooks(std::string* why);
bool install_ui_hooks(std::string* why);
bool verify_content_map(std::string* why);  // the game's map reads the way hooks.cpp assumes
bool resources_on();
bool content_hooks_on();  // blobs may be registered (served once the container opens)
bool content_on();        // the container is live too
bool ui_on();
std::string hooks_status_json();

// Content: the kfc's own map (for reads of game blobs) and container paths.
struct GameBlob {
    uint64_t offset;
    uint16_t container;
};
bool game_blob_find(const HashK& h, GameBlob* out);  // the game's content map (needs the resource system)
std::string game_container_path(uint16_t index);   // UTF-8 path of enshrouded_NNN.dat, "" if unknown
uint32_t game_container_count();

// ------------------------------------------------------------------------------------------- store -----------
// What the hooks serve, rebuilt under g_mu and published as immutable snapshots (lock-free reads).
struct Served {
    const uint8_t* data = nullptr;
    size_t size = 0;
    uint32_t handle = 0;  // synthetic (new) or the game's (override); 0 until known
    bool is_new = false;
};
// One level of the resource index (immutable once published).
struct ResSnap {
    std::unordered_map<Key, Served, KeyHash> keys;
    std::unordered_map<uint32_t, Served> handles;
    std::unordered_map<uint32_t, std::vector<Key>> new_by_type;
    std::unordered_map<uint32_t, Key> synth_keys;  // synthetic handle -> key
    // Every key ever served as new in this process (a delta: those added since its base). A template stays registered in its world after
    // resource_remove (docs/kiln/services/gamedata.md), so a client without Kiln can still be sent its entities.
    std::unordered_map<Key, bool, KeyHash> ever_new;
    std::unordered_map<uint32_t, size_t> ever_new_by_type;
};
// What the hooks read: a base, built again now and then, and a small delta of the resources added since, built again at each put. So a
// put costs about the same however many resources are served (mods with thousands of clones put one at a time).
struct ResView {
    uint64_t gen = 0;
    const ResSnap* base = nullptr;   // never null once published
    const ResSnap* delta = nullptr;  // never null once published
    // vanilla + new key lists per type, built on first use. The game keeps the list it was given: never freed.
    mutable std::mutex merged_mu;
    mutable std::unordered_map<uint32_t, const std::vector<uint8_t>*> merged;
    const Served* key(const Key& k) const;
    const Served* handle(uint32_t h) const;
    const Key* synth(uint32_t h) const;
    bool ever_new(const Key& k) const;
    size_t new_count(uint32_t type) const;
    void new_keys(uint32_t type, std::vector<Key>* out) const;  // base's, then delta's
    bool any_keys() const { return !base->keys.empty() || !delta->keys.empty(); }
    bool any_handles() const { return !base->handles.empty() || !delta->handles.empty(); }
    bool any_new() const { return !base->new_by_type.empty() || !delta->new_by_type.empty(); }
};

// Readers on any thread hold a ReadGuard while they use what g_res, g_content or g_icons point at. A writer frees what it replaced once
// every reader that could have seen it has left (two reader counts, by epoch). A reader must not wait for g_mu inside one.
class ReadGuard {
public:
    ReadGuard();
    ~ReadGuard();
    ReadGuard(const ReadGuard&) = delete;
    ReadGuard& operator=(const ReadGuard&) = delete;

private:
    uint64_t epoch_;
};
struct Extent {
    uint64_t offset;
    uint64_t size;
    const uint8_t* data;
};
struct ContentSnap {
    uint64_t gen = 0;
    std::unordered_map<HashK, uint64_t, HashKHash> entries;  // hash -> offset in Kiln's container
    std::vector<Extent> extents;                             // sorted by offset
};
extern std::atomic<const ResView*> g_res;
extern std::atomic<const ContentSnap*> g_content;
extern std::atomic<uint64_t> g_content_gen;
extern uint16_t g_container_index;  // Kiln's container index (the game's count), 0xFFFF until the IO queue opened it
extern std::wstring g_container_path;

constexpr uint32_t kSynthBase = 0x70000000u;

// The player's animation graph, reserved for kiln.playeranim (KILN_ASSET_PUT_PIN in kiln_assets.h): store.cpp refuses every other put.
constexpr uint32_t kPlayerGraphType = 0xa345050eu;  // keen::anim_graph::runtime_graph::AnimationGraphResource2_0
bool player_graph_key(const Key& k);

// UI icons (client)
struct IconSnap {
    uint64_t gen = 0;
    std::unordered_map<Guid16, std::shared_ptr<std::atomic<int32_t>>, GuidHash> state;
};
extern std::atomic<const IconSnap*> g_icons;
extern std::atomic<uint64_t> g_icon_want;    // generation of the newest icon key set
extern std::atomic<uint64_t> g_icon_served;  // generation the UI cache has taken in
void icons_after_populate(uint64_t gen_used);  // hooks.cpp -> assets.cpp
void on_resource_system_ready(uintptr_t S);    // hooks.cpp -> store.cpp
void flush_pending_textures();                 // store.cpp -> assets.cpp: apply boot-time textures once S is up

// Images (image.cpp)
struct Rgba {
    uint32_t w = 0, h = 0;
    std::vector<uint8_t> px;  // w*h*4
};
bool png_decode(const void* data, size_t len, Rgba* out, std::string* why);
Rgba resize(const Rgba& src, uint32_t w, uint32_t h);
// Encodes `levels` mips of `img` (already at the texture's size) to the game's PixelFormat; empty + why on failure.
std::vector<uint8_t> encode_texture(const Rgba& img, uint32_t format, uint32_t levels, std::string* why);
size_t texture_bytes(uint32_t w, uint32_t h, uint32_t format, uint32_t levels);  // 0 = unknown format
const char* pixel_format_name(uint32_t format);
bool format_is_srgb(uint32_t format);

// Memory that the game may keep pointers into: never freed.
uint8_t* keep_alloc(size_t n);
const uint8_t* keep_copy(const void* data, size_t n);

// Store operations (store.cpp).
KilnResult op_resource_put(KilnOwner owner, const KilnAssetKey* key, const void* data, size_t len, uint32_t flags);
KilnResult op_resource_remove(KilnOwner owner, const KilnAssetKey* key);
KilnResult op_resource_read(const KilnAssetKey* key, void* buffer, size_t cap, size_t* out_len);
KilnResult op_resource_data(const KilnAssetKey* key, const void** ptr, size_t* len);
KilnResult op_when_resources_ready(void(KILN_CALL* fn)(void*), void* user);
KilnResult op_resource_list(uint32_t type, KilnAssetKey* out, size_t cap, size_t* out_count);
int32_t op_resource_is_new(const KilnAssetKey* key);
size_t op_resource_new_count(uint32_t type);
KilnResult op_blob_add(KilnOwner owner, const void* data, size_t len, KilnContentHash* out);
KilnResult op_blob_override(KilnOwner owner, const KilnContentHash* hash, const void* data, size_t len);
KilnResult op_blob_remove(KilnOwner owner, const KilnContentHash* hash);
KilnResult op_blob_read(const KilnContentHash* hash, void* buffer, size_t cap, size_t* out_len);
KilnResult op_icon_register(KilnOwner owner, const uint8_t guid[16], const Rgba& img);
KilnResult op_icon_remove(KilnOwner owner, const uint8_t guid[16]);
int32_t op_icon_state(const uint8_t guid[16]);
void op_revert_owner(KilnOwner owner);
std::string store_status_json();

std::string api_path(int kind);
std::wstring widen(const std::string& s);
std::string narrow(const std::wstring& s);
bool read_file(const std::wstring& path, std::vector<uint8_t>* out, std::string* why);

}  // namespace ka
