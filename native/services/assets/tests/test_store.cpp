// SPDX-License-Identifier: MIT
// kiln.assets' store on the host: the real store.cpp and util.cpp, with the game faked at the calls hooks.cpp makes for them
// (game_handle, game_data, ...). Checks KILN_ASSET_PUT_PIN and the player graph's reservation (kiln_assets.h). No game, no Wine.
//   test_store            every case, in order (the store is one process-wide state: each case starts where the last one ended)
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

#include "assets.h"

// ---- the game side, faked at the narrowest seam: the calls of the game's own functions and the feature switches --------------------
namespace ka {
std::atomic<uintptr_t> g_S{0};
namespace {
struct GameRes {
    uint32_t handle;
    std::string bytes;
};
std::unordered_map<Key, GameRes, KeyHash> g_game;
bool g_fake_ui = false;
}  // namespace
bool resources_on() { return true; }
bool content_hooks_on() { return true; }
bool content_on() { return false; }
bool ui_on() { return g_fake_ui; }
uint32_t game_handle(uintptr_t S, const Key& k) {  // as hooks.cpp: nothing before the resource system is up
    if (!S) return 0;
    const auto it = g_game.find(k);
    return it == g_game.end() ? 0 : it->second.handle;
}
bool game_data(uintptr_t S, uint32_t handle, DataSpan* out) {
    if (!S) return false;
    for (const auto& [k, r] : g_game)
        if (r.handle == handle) {
            *out = {reinterpret_cast<const uint8_t*>(r.bytes.data()), r.bytes.size(), 0};
            return true;
        }
    return false;
}
bool game_type_list(uintptr_t, uint32_t, const uint8_t** keys, uint64_t* count) {
    *keys = nullptr;
    *count = 0;
    return false;
}
bool game_blob_find(const HashK&, GameBlob*) { return false; }
std::string game_container_path(uint16_t) { return {}; }
void flush_pending_textures() {}
Rgba resize(const Rgba& s, uint32_t, uint32_t) { return s; }
}  // namespace ka

namespace {
using namespace ka;

std::vector<std::string> g_log;
void KILN_CALL log_line(KilnOwner, int32_t, KilnStr s) { g_log.emplace_back(s.ptr, s.len); }

int g_pass = 0, g_fail = 0;
void check(bool ok, const std::string& what) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", what.c_str());
    ++(ok ? g_pass : g_fail);
}

Key key(const char* guid, uint32_t type, uint32_t part = 0) {
    Key k{};
    if (!parse_guid(guid, k.g)) std::abort();
    k.type = type;
    k.part = part;
    return k;
}
KilnAssetKey ak(const Key& k) {
    KilnAssetKey a{};
    std::memcpy(a.guid, k.g, 16);
    a.type = k.type;
    a.part = k.part;
    return a;
}
KilnResult put(KilnOwner o, const Key& k, const std::string& bytes, uint32_t flags) {
    const KilnAssetKey a = ak(k);
    return op_resource_put(o, &a, bytes.data(), bytes.size(), flags);
}
KilnResult remove(KilnOwner o, const Key& k) {
    const KilnAssetKey a = ak(k);
    return op_resource_remove(o, &a);
}
std::string read(const Key& k) {  // what resource_read gives: an item's bytes, else the game's own
    const KilnAssetKey a = ak(k);
    std::vector<uint8_t> b(4096);
    size_t n = 0;
    const KilnResult r = op_resource_read(&a, b.data(), b.size(), &n);
    return r == KILN_OK ? std::string(b.begin(), b.begin() + n) : "<result " + std::to_string(r) + ">";
}
std::string by_handle(uint32_t h) {  // what the game's ResourceData hook serves for its handle
    ReadGuard g;
    const ResView* v = g_res.load();
    const Served* sv = v ? v->handle(h) : nullptr;
    return sv ? std::string(reinterpret_cast<const char*>(sv->data), sv->size) : "<not served>";
}
size_t items() {  // kiln.assets' item count, as its status reports it
    const std::string s = store_status_json();
    return std::strtoul(s.c_str() + s.find("\"resources\":") + 12, nullptr, 10);
}
bool logged(size_t from, const std::string& a, const std::string& b) {
    for (size_t i = from; i < g_log.size(); ++i)
        if (g_log[i].find(a) != std::string::npos && g_log[i].find(b) != std::string::npos) return true;
    return false;
}
std::string flag_name(uint32_t f) {
    if (!f) return "no flag";
    std::string s;
    if (f & KILN_ASSET_PUT_NEW) s += "|new";
    if (f & KILN_ASSET_PUT_OVERRIDE) s += "|override";
    if (f & KILN_ASSET_PUT_PIN) s += "|pin";
    return s.substr(1);
}

const char* kGraphGuid = "21319e83-5087-4fff-8795-22997843649e";
const char* kGraphKeyText = "21319e83-5087-4fff-8795-22997843649e:keen::anim_graph::runtime_graph::AnimationGraphResource2_0:0";
constexpr uint32_t kGraphHandle = 100, kOtherGraphHandle = 101, kItemHandle = 102, kIconHandle = 103;
}  // namespace

int main() {
    KilnApi api{};
    api.log = log_line;
    g_api = &api;
    g_self = 1;
    bool ok;
    const uint32_t graph_type = type_hash("keen::anim_graph::runtime_graph::AnimationGraphResource2_0", &ok);
    check(graph_type == kPlayerGraphType && graph_type == 0xa345050eu, "the graph type hash is FNV-1a of its qualified name (0xa345050e)");
    const uint32_t item_type = type_hash("keen::ItemInfo", &ok), other_type = 0x12345678u;
    const Key graph = key(kGraphGuid, graph_type);
    const Key other_graph = key("11111111-2222-3333-4444-555555555555", graph_type);  // another graph (an NPC's, say)
    const Key graph_guid_item = key(kGraphGuid, item_type);
    const Key graph_part1 = key(kGraphGuid, graph_type, 1);
    const Key item = key("22222222-3333-4444-5555-666666666666", item_type);
    const Key icon = key("33333333-4444-5555-6666-777777777777", KILN_ASSET_TYPE_UI_TEXTURE);
    const Key fresh = key("44444444-5555-6666-7777-888888888888", other_type);
    g_game[graph] = {kGraphHandle, "GAME-GRAPH"};
    g_game[other_graph] = {kOtherGraphHandle, "GAME-OTHER-GRAPH"};
    g_game[item] = {kItemHandle, "GAME-ITEM"};
    g_game[icon] = {kIconHandle, "GAME-ICON"};
    const KilnOwner manifest = 11, playeranim = 21, mod = 31;
    const std::string merged = "MERGED-GRAPH";

    // 1. Reserved before any pin: at boot (no resource system yet, where assets.json manifests are loaded) and once it is up.
    std::puts("-- 1: the graph key without the pin flag, before any pin");
    size_t mark = g_log.size();
    for (uint32_t f : {0u, (uint32_t)KILN_ASSET_PUT_OVERRIDE, (uint32_t)KILN_ASSET_PUT_NEW})
        check(put(manifest, graph, "MOD-GRAPH", f) == KILN_ERR_UNAVAILABLE, "boot: put (" + flag_name(f) + ") refused with KILN_ERR_UNAVAILABLE");
    check(items() == 0, "boot: nothing added");
    check(logged(mark, kGraphKeyText, "reserved for kiln.playeranim"), "boot: the log line names the key");
    g_S = 1;
    on_resource_system_ready(1);
    mark = g_log.size();
    for (uint32_t f : {0u, (uint32_t)KILN_ASSET_PUT_OVERRIDE, (uint32_t)KILN_ASSET_PUT_NEW})
        check(put(mod, graph, "MOD-GRAPH", f) == KILN_ERR_UNAVAILABLE, "up: put (" + flag_name(f) + ") refused with KILN_ERR_UNAVAILABLE");
    check(items() == 0, "up: nothing added");
    check(read(graph) == "GAME-GRAPH" && by_handle(kGraphHandle) == "<not served>", "up: the game's own graph is what is read and served");
    check(logged(mark, kGraphKeyText, "reserved for kiln.playeranim"), "up: the log line names the key");

    // 2. The pin: kiln.playeranim's put.
    std::puts("-- 2: the pinning put");
    check(put(playeranim, graph, merged, KILN_ASSET_PUT_OVERRIDE | KILN_ASSET_PUT_PIN) == KILN_OK, "put (override|pin) accepted");
    check(read(graph) == merged, "resource_read gives the pinned bytes");
    check(by_handle(kGraphHandle) == merged, "served for the game's handle (an override)");
    check(items() == 1, "one item");

    // 3. Nobody puts a pinned key again, its owner included.
    std::puts("-- 3: puts after the pin");
    for (uint32_t f : {0u, (uint32_t)KILN_ASSET_PUT_OVERRIDE, (uint32_t)KILN_ASSET_PUT_NEW, (uint32_t)KILN_ASSET_PUT_PIN,
                       (uint32_t)(KILN_ASSET_PUT_OVERRIDE | KILN_ASSET_PUT_PIN)}) {
        mark = g_log.size();
        check(put(mod, graph, "MOD-GRAPH", f) == KILN_ERR_UNAVAILABLE && logged(mark, kGraphKeyText, "refused"),
              "another owner's put (" + flag_name(f) + ") refused, logged with the key");
    }
    for (uint32_t f : {(uint32_t)KILN_ASSET_PUT_OVERRIDE, (uint32_t)(KILN_ASSET_PUT_OVERRIDE | KILN_ASSET_PUT_PIN)}) {
        mark = g_log.size();
        check(put(playeranim, graph, "MERGED-AGAIN", f) == KILN_ERR_UNAVAILABLE && logged(mark, kGraphKeyText, "pinned"),
              "the pinning owner's own put (" + flag_name(f) + ") refused, logged with the key");
    }
    check(read(graph) == merged && by_handle(kGraphHandle) == merged && items() == 1, "the pinned bytes are still read and served; one item");

    // 4. No removal.
    std::puts("-- 4: removals");
    mark = g_log.size();
    check(remove(playeranim, graph) == KILN_ERR_UNAVAILABLE, "removal by the pinning owner refused");
    check(remove(mod, graph) == KILN_ERR_UNAVAILABLE, "removal by another owner refused");
    check(logged(mark, kGraphKeyText, "removal refused"), "the log line names the key");
    check(read(graph) == merged && by_handle(kGraphHandle) == merged && items() == 1, "the pinned bytes stay");

    // 5. revert_owner of the pinning owner (also what kiln.assets does when it sees the owner revoked).
    std::puts("-- 5: revert_owner of the pinning owner");
    check(put(playeranim, item, "PA-ITEM", KILN_ASSET_PUT_OVERRIDE) == KILN_OK && put(playeranim, fresh, "PA-NEW", KILN_ASSET_PUT_NEW) == KILN_OK,
          "the owner has two other resources");
    check(read(item) == "PA-ITEM" && read(fresh) == "PA-NEW" && items() == 3, "both served");
    op_revert_owner(playeranim);
    check(read(graph) == merged && by_handle(kGraphHandle) == merged, "the pinned bytes stay served");
    check(read(item) == "GAME-ITEM" && read(fresh) == "<result 2>" && by_handle(kItemHandle) == "<not served>" && items() == 1,
          "its other resources are gone");
    check(put(mod, graph, "MOD-GRAPH", KILN_ASSET_PUT_OVERRIDE) == KILN_ERR_UNAVAILABLE && read(graph) == merged, "the pin outlives the revert");

    // 6. What is not reserved: the reservation is the one key.
    std::puts("-- 6: neighbours of the key");
    check(!player_graph_key(other_graph) && !player_graph_key(graph_guid_item) && !player_graph_key(graph_part1) && player_graph_key(graph),
          "player_graph_key matches the graph key only");
    check(put(mod, other_graph, "MOD-OTHER-GRAPH", 0) == KILN_OK && read(other_graph) == "MOD-OTHER-GRAPH" &&
              by_handle(kOtherGraphHandle) == "MOD-OTHER-GRAPH",
          "another key of the graph type: put and served");
    check(put(mod, graph_guid_item, "MOD-SAME-GUID", KILN_ASSET_PUT_NEW) == KILN_OK && read(graph_guid_item) == "MOD-SAME-GUID",
          "the graph's GUID with another type: put and served");
    check(put(mod, graph_part1, "MOD-PART-1", 0) == KILN_OK && read(graph_part1) == "MOD-PART-1", "the graph's GUID and type, part 1: put and served");
    check(remove(mod, other_graph) == KILN_OK && remove(mod, graph_guid_item) == KILN_OK && remove(mod, graph_part1) == KILN_OK &&
              read(other_graph) == "GAME-OTHER-GRAPH" && read(graph_guid_item) == "<result 2>" && read(graph_part1) == "<result 2>",
          "and removed again");
    check(read(graph) == merged && items() == 1, "the pinned graph unchanged");

    // 7. The index is built again after 64 new keys (the delta): the pinned bytes come through it.
    std::puts("-- 7: more than 64 puts after the pin");
    const ResSnap* base_before;
    {
        ReadGuard g;
        base_before = g_res.load()->base;
    }
    bool all = true;
    for (int i = 0; i < 70; ++i) {
        char guid[40];
        std::snprintf(guid, sizeof guid, "55555555-0000-0000-0000-%012d", i);
        all = put(mod, key(guid, other_type), "MOD-NEW-" + std::to_string(i), KILN_ASSET_PUT_NEW) == KILN_OK && all;
    }
    bool rebuilt, in_base;
    {
        ReadGuard g;
        const ResView* v = g_res.load();
        rebuilt = v->base != base_before && v->delta->keys.size() < 64;
        in_base = v->base->keys.count(graph) == 1;
    }
    check(all && items() == 71, "70 new keys put");
    check(rebuilt, "the base was built again");
    check(in_base && read(graph) == merged && by_handle(kGraphHandle) == merged, "the pinned bytes are in it and served");
    check(put(mod, graph, "MOD-GRAPH", KILN_ASSET_PUT_OVERRIDE) == KILN_ERR_UNAVAILABLE && read(graph) == merged, "and still take no put");

    // 8. A pin of any key: refused while another owner has an item of it (the later item would be served), then holds.
    std::puts("-- 8: a pin over another owner's item");
    check(put(mod, item, "MOD-ITEM", KILN_ASSET_PUT_OVERRIDE) == KILN_OK, "another owner overrides a game key");
    mark = g_log.size();
    check(put(playeranim, item, "PA-ITEM", KILN_ASSET_PUT_OVERRIDE | KILN_ASSET_PUT_PIN) == KILN_ERR_UNAVAILABLE &&
              logged(mark, "22222222-3333-4444-5555-666666666666", "pin refused"),
          "a pin of it is refused, logged with the key");
    check(read(item) == "MOD-ITEM" && by_handle(kItemHandle) == "MOD-ITEM", "the other owner's bytes are served");
    check(remove(mod, item) == KILN_OK && put(playeranim, item, "PA-ITEM", KILN_ASSET_PUT_OVERRIDE | KILN_ASSET_PUT_PIN) == KILN_OK,
          "once it is gone, the pin is taken");
    check(put(mod, item, "MOD-ITEM", KILN_ASSET_PUT_OVERRIDE) == KILN_ERR_UNAVAILABLE && read(item) == "PA-ITEM", "and holds");

    // 9. The icon calls are puts and removals of a UI texture key too.
    std::puts("-- 9: icons on a pinned UI texture key");
    check(put(playeranim, icon, "PA-ICON", KILN_ASSET_PUT_OVERRIDE | KILN_ASSET_PUT_PIN) == KILN_OK && read(icon) == "PA-ICON", "a UI texture key pinned");
    g_client = true;
    g_fake_ui = true;
    Rgba img;
    img.w = img.h = 1;
    img.px = {1, 2, 3, 4};
    check(op_icon_register(mod, icon.g, img) == KILN_ERR_UNAVAILABLE, "icon_register of it refused");
    op_icon_remove(playeranim, icon.g);
    check(read(icon) == "PA-ICON" && by_handle(kIconHandle) == "PA-ICON", "icon_remove leaves it; the pinned bytes are served");

    // 10. What a dedicated server skips of an assets.json, by what the entry is (no mark in the file needed).
    std::puts("-- 10: the manifest entries only a game client uses");
    const uint32_t model = type_hash("keen::RenderModel", &ok), tmpl = type_hash("keen::ecs::TemplateResource", &ok), clip = type_hash("keen::JointAnimation", &ok);
    check(model == 0x177394c7u && client_only_type(model) && client_only_type(type_hash("0x177394C7", &ok)), "a RenderModel, by name and by hex");
    check(client_only_type(type_hash("keen::RenderMaterialResource", &ok)) && client_only_type(type_hash("keen::RenderTextureResource", &ok)) &&
              client_only_type(type_hash("keen::UiTextureResource", &ok)) && client_only_type(KILN_ASSET_TYPE_UI_TEXTURE),
          "materials, textures and UI textures, each by its name's hash");
    check(!client_only_type(tmpl) && !client_only_type(clip) && !client_only_type(type_hash("keen::ImpactProgram", &ok)) &&
              !client_only_type(type_hash("keen::ModelHierarchyResource", &ok)) && !client_only_type(type_hash("keen::SoundResource", &ok)) &&
              !client_only_type(kPlayerGraphType) && !client_only_type(0),
          "what a server serves is not client-only");
    const std::vector<ManifestItem> entries = {
        {"blob", "cooked/blade.blob", 0, {}},                         // 0 a model's render data
        {"resource", "cooked/blade.bin", model, {"cooked/blade.blob"}},  // 1 the model that patches it in
        {"resource", "cooked/visual.bin", tmpl, {}},                  // 2 a visual template: both sides
        {"resource", "cooked/clip.bin", clip, {}},                    // 3 a clip: both sides
        {"blob", "cooked/shared.blob", 0, {}},                        // 4 patched into a model and into a template: a server needs it
        {"resource", "cooked/m2.bin", model, {"cooked\\shared.blob"}},  // 5
        {"resource", "cooked/t2.bin", tmpl, {"cooked/shared.blob"}},  // 6
        {"blob", "cooked/alone.blob", 0, {}},                         // 7 named by no resource: loaded
        {"blob", "", 0, {}},                                          // 8 a hash-only blob
        {"icon", "icons/a.png", 0, {}},                               // 9 the loader skips an icon by its kind
        {"resource", "cooked/Blade.bin", model, {"cooked/Blade.blob"}},  // 10 another spelling is another file here
    };
    check(client_only_entries(entries) == std::vector<bool>{true, true, false, false, false, true, false, false, false, false, true},
          "a model and the blob only it patches in; a template, a clip, a shared blob and a blob of its own are loaded");
    check(client_only_entries({}).empty(), "an empty manifest");
    // a blob that overrides the game's content of a fixed hash: a server mod may read that hash, so the server keeps the override even
    // when a model patches the same file in (only "side": "client" in the manifest keeps it from a server)
    const std::vector<ManifestItem> fixed = {
        {"blob", "cooked/shared.blob", 0, {}, true},
        {"resource", "cooked/blade.bin", model, {"cooked/shared.blob"}, false},
        {"blob", "cooked/own.blob", 0, {}, false},
        {"resource", "cooked/other.bin", model, {"cooked/own.blob"}, false},
    };
    check(client_only_entries(fixed) == std::vector<bool>{false, true, true, true}, "a blob with a fixed hash is kept for the server, the model and a blob only it uses are not");

    std::printf("%d passed, %d failed\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
