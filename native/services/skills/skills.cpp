// SPDX-License-Identifier: MIT
// kiln.skills: mod nodes in the game's own skill tree, and a guard for saved unlocks of nodes that are gone
// (kiln_skills.h; evidence docs/re/abilities.md section 4; symbols sigdb/symbols/server.d/skills.json, shared by the
// client).
//
//  - Definitions: every mod folder's skills/*.json ("gameTree": {"nodes": [...]}) at boot, the cache of code
//    definitions (<kiln>/data/skills/defined/<mod>.json) for mods in the load order, and define().
//  - TreeLoad detour: when a world's g38_skilltree::SkillTree is about to read its resource (data pointer 0), the
//    expanded SkillTreeResource is built from the game's bytes and the definitions and served through kiln.assets as an
//    override; the original loader then builds every map from it. A build is reused while a world holds a tree (all
//    worlds of the process must agree on the index order); changes wait for the next world.
//  - LoginCheck detour: before the game checks a joining player's UnlockedSkillNodes (initialized == 0), ids the tree
//    does not know are removed (the game would reset the whole tree). Records go to a ring; Pre resolves the entity.
//  - Every game call and memory read is SEH-guarded; a fault turns that part off (logged).
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <memory>
#include <mutex>
#include <set>
#include <string>
#include <vector>

#include "kiln_api.h"
#include "kiln_boot_collector.hpp"
#include "kiln_skills.h"
#include "kiln_hook.hpp"
#include "services/kiln_assets.h"
#include "services/kiln_profile.h"
#include "services/kiln_world.h"
#include "shadow.h"
#include "tree.h"

namespace fs = std::filesystem;
using namespace kiln::skills;

namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
const KilnAssetsApi* A = nullptr;
const KilnWorldApi* W = nullptr;

void logf(int level, const char* fmt, ...) {
    if (!g_api) return;
    char buf[1400];
    va_list ap;
    va_start(ap, fmt);
    int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (n > static_cast<int>(sizeof buf) - 1) n = sizeof buf - 1;
    g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(n)});
}
#define SI(...) logf(KILN_LOG_INFO, "skills: " __VA_ARGS__)
#define SW(...) logf(KILN_LOG_WARN, "skills: " __VA_ARGS__)

KilnStr kstr(const char* s) { return KilnStr{s, std::strlen(s)}; }
KilnStr kstr(const std::string& s) { return KilnStr{s.data(), s.size()}; }

std::wstring widen(const std::string& s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}
std::string narrow(const std::wstring& w) {
    if (w.empty()) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), nullptr, 0, nullptr, nullptr);
    std::string s(static_cast<size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), s.data(), n, nullptr, nullptr);
    return s;
}
std::string api_path(int kind) {
    char b[1024];
    size_t n = 0;
    if (g_api->path(kind, b, sizeof b, &n) != KILN_OK) return {};
    return std::string(b, n);
}

// ------------------------------------------------------------------------------------------------ the game ------
using TreeLoadFn = void (*)(void* tree);
using LoginCheckFn = void* (*)(void* out, uint8_t* iter);
using NodeByIdFn = uint8_t* (*)(uint32_t id, void* tree);
using NodeSetFn = bool (*)(uint32_t id, void* unlocked, bool unlock, void* tree);
using ReachableFn = bool (*)(uint32_t id, const void* ui, void* tree);
using UiRebuildFn = bool (*)(void* ui, const void* unlocked, void* tree);
using ConnectedFn = bool (*)(const void* ui, void* tree);
using LevelsValidFn = bool (*)(const void* unlocked, const void* ui, void* tree);
using PointsSpentFn = int32_t (*)(const void* masks, const void* levels, void* tree);
using WriteAccessFn = void* (*)(void* accessor);

struct Fns {
    NodeByIdFn node_by_id = nullptr;
    NodeSetFn node_set = nullptr;
    ReachableFn reachable = nullptr;
    UiRebuildFn ui_rebuild = nullptr;
    ConnectedFn connected = nullptr;
    LevelsValidFn levels_valid = nullptr;
    PointsSpentFn points_spent = nullptr;
    WriteAccessFn write_access = nullptr;
} G;

struct Layout {
    int64_t data = 0x20, guid = 0x38, node_map = 0x48, link_map = 0xa0, index_map = 0xf8, ids = 0x150, roots = 0x178;
    int64_t login_tree = 0xd0, login_unlocked = 0x80, login_ui = 0x20;
} L;

// HashMap (the engine's): bits +0x08, capacity +0x10, keys +0x28, values +0x40, count +0x54 from the map.
constexpr int64_t kMapCap = 0x10, kMapCount = 0x54;
// UnlockedSkillNodes (2316 B): initialized +0, knownSkillNodes[256] +4, activeSkillImpacts[256] +0x404,
// unlockLevel[256] +0x804, version +0x904, changeCount +0x908. PlayerSkillsUi (136 B): masks[4] +0, levels[100] +0x20.
constexpr size_t kUnlocked = 2316, kUi = 136, kU_Ids = 4, kU_Levels = 0x804, kU_Change = 0x908, kUi_Levels = 0x20;

std::atomic<bool> g_load_on{false}, g_guard_on{false}, g_fns_on{false};
std::string g_reason;
std::mutex g_reason_mu;
void set_reason(const char* fmt, ...) {
    char b[160];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    std::lock_guard lock(g_reason_mu);
    if (g_reason.empty()) g_reason = b;
}

// ---- SEH-guarded helpers (no C++ objects with destructors in these) ----
bool g_read(const void* src, void* dst, size_t n) {
    if (!src) return false;
    __try {
        std::memcpy(dst, src, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
template <class T>
bool rd(uintptr_t a, T* out) {
    return g_read(reinterpret_cast<const void*>(a), out, sizeof(T));
}
// node pointer or null; *fault on an exception
uint8_t* g_node(uint32_t id, void* tree, bool* fault) {
    __try {
        return G.node_by_id(id, tree);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *fault = true;
        return nullptr;
    }
}
int g_call_bool3(void* fn, int kind, uint32_t id, void* a, void* b, void* tree) {
    __try {
        switch (kind) {
        case 0: return reinterpret_cast<NodeSetFn>(fn)(id, a, b != nullptr, tree) ? 1 : 0;
        case 1: return reinterpret_cast<ReachableFn>(fn)(id, a, tree) ? 1 : 0;
        case 2: return reinterpret_cast<UiRebuildFn>(fn)(a, b, tree) ? 1 : 0;
        case 3: return reinterpret_cast<ConnectedFn>(fn)(a, tree) ? 1 : 0;
        case 4: return reinterpret_cast<LevelsValidFn>(fn)(a, b, tree) ? 1 : 0;
        default: return -1;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}
int64_t g_spent(const void* masks, const void* levels, void* tree) {
    __try {
        return G.points_spent(masks, levels, tree);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}

// ------------------------------------------------------------------------------------------------ texts ---------
using SetTextsFn = void (*)(uint32_t tag, const char* json, size_t len);
SetTextsFn g_set_texts = nullptr;

// ------------------------------------------------------------------------------------------------ definitions ---
std::mutex g_mu;  // definitions, the build, the live trees
std::vector<std::unique_ptr<Def>> g_defs;
std::vector<uint8_t> g_vanilla;     // the game's resource as first read (before Kiln's override)
std::vector<uint8_t> g_built;       // what Kiln serves now (empty: nothing served)
Built g_last;                       // the last build's numbers
uint8_t g_key_guid[16] = {};
bool g_have_key = false, g_served = false, g_dirty = true;
uint64_t g_builds = 0, g_worlds = 0;
struct LiveTree {
    uintptr_t tree;
    uint32_t kind;  // KILN_WORLD_*, 99 = not known
};
std::vector<LiveTree> g_live;
std::map<std::string, KilnOwner> g_mod_owner;  // boot owners by mod dir / cache id

const char* state_name(int s) {
    switch (s) {
    case kInTree: return "in-tree";
    case kError: return "error";
    case kOrphaned: return "orphaned";
    case kPending: return "pending";
    default: return "defined";
    }
}

std::string manifest_id(const fs::path& dir) {
    std::ifstream f(dir / L"kiln.mod.json", std::ios::binary);
    if (!f) return {};
    std::string s((std::istreambuf_iterator<char>(f)), {});
    json j = json::parse(s, nullptr, false, true);
    return j.is_object() && j.contains("id") && j["id"].is_string() ? j["id"].get<std::string>() : std::string();
}

// Registers an icon PNG (client) under the node's icon GUID; true when kiln.assets took it.
bool icon_from_file(KilnOwner owner, NodeDef& d, const fs::path& base) {
    if (d.icon_file.empty() || !A) return false;
    fs::path p = widen(d.icon_file);
    if (p.is_relative()) p = base / p;
    uint8_t g[16];
    icon_guid(d.guid, g);
    const std::string ps = narrow(p.wstring());
    const KilnResult r = A->icon_register_file(owner, g, kstr(ps));
    if (r != KILN_OK) return false;
    std::memcpy(d.icon, g, 16);
    d.has_icon = true;
    return true;
}

// Adds (or replaces) one definition; caller holds g_mu. Returns the state text for the report.
std::string add_def(std::unique_ptr<Def> nd, std::string* error) {
    for (auto& d : g_defs) {
        if (std::memcmp(d->node.guid, nd->node.guid, 16) != 0) continue;
        if (d->owner == nd->owner || d->state == kOrphaned || d->source.rfind("cache:", 0) == 0 ||
            (!nd->owner_id.empty() && d->owner_id == nd->owner_id)) {
            d = std::move(nd);
            g_dirty = true;
            return "redefined";
        }
        *error = "GUID " + guid_str(nd->node.guid) + " is defined by " + (d->owner_id.empty() ? d->source : d->owner_id);
        return "error";
    }
    g_defs.push_back(std::move(nd));
    g_dirty = true;
    return "defined";
}

// raw JSON of code definitions per mod (for the boot cache)
std::map<std::string, std::map<std::string, json>> g_code_json;  // mod -> guid -> node JSON

void save_cache(const std::string& mod) {
    if (mod.empty()) return;
    const std::string dir = api_path(KILN_PATH_DATA) + "\\skills\\defined";
    std::error_code ec;
    fs::create_directories(widen(dir), ec);
    json arr = json::array();
    for (auto& [g, j] : g_code_json[mod]) arr.push_back(j);
    const fs::path f = fs::path(widen(dir)) / widen(mod + ".json");
    const fs::path tmp = f.wstring() + L".tmp";
    {
        std::ofstream o(tmp, std::ios::binary | std::ios::trunc);
        if (!o) return;
        const std::string s = json{{"format", 1}, {"mod", mod}, {"nodes", arr}}.dump(1);
        o.write(s.data(), static_cast<std::streamsize>(s.size()));
    }
    fs::rename(tmp, f, ec);
}

int load_nodes(KilnOwner owner, const std::string& owner_id, const std::string& source, const json& nodes,
               const fs::path& base, json* report) {
    int n = 0;
    for (const json& e : nodes) {
        auto d = std::make_unique<Def>();
        std::string why;
        json r = json::object();
        if (!parse_node(e, &d->node, &why)) {
            r["state"] = "error";
            r["error"] = why;
            if (e.is_object() && e.contains("guid")) r["guid"] = e["guid"];
            SW("%s: a node definition is refused: %s", source.c_str(), why.c_str());
        } else {
            d->owner = owner;
            d->owner_id = owner_id;
            d->source = source;
            icon_from_file(owner, d->node, base);
            r["guid"] = guid_str(d->node.guid);
            r["id"] = hex32(d->node.id);
            r["name"] = d->node.name;
            std::string err;
            const std::string st = add_def(std::move(d), &err);
            r["state"] = st;
            if (!err.empty()) r["error"] = err;
            if (st != "error") ++n;
        }
        if (report) report->push_back(r);
    }
    return n;
}

void load_file(KilnOwner owner, const std::string& owner_id, const fs::path& file, const fs::path& base) {
    std::ifstream f(file, std::ios::binary);
    if (!f) return;
    std::string s((std::istreambuf_iterator<char>(f)), {});
    json j = json::parse(s, nullptr, false, true);
    const std::string src = narrow(file.wstring());
    if (j.is_discarded() || !j.is_object()) {
        SW("%s is not a JSON object (skipped)", src.c_str());
        return;
    }
    if (!j.contains("gameTree")) return;  // Kiln trees only (Kiln.Skills reads those)
    const json& gt = j["gameTree"];
    const json nodes = gt.is_object() && gt.contains("nodes") ? gt["nodes"] : gt.is_array() ? gt : json::array();
    if (!nodes.is_array()) {
        SW("%s: \"gameTree\" must be {\"nodes\": [...]}", src.c_str());
        return;
    }
    std::lock_guard lock(g_mu);
    const int n = load_nodes(owner, owner_id, src, nodes, base, nullptr);
    SI("%s: %d game-tree node(s) defined (mod %s)", src.c_str(), n, owner_id.c_str());
}

// The game-tree definitions of the load order: the mods' skills/*.json, then (a boot generation, chapter 08) what their OnBoot staged,
// then the code definitions cached by earlier runs; a node an earlier source defined stays the earlier source's. `boot`: the generation's
// prepare input (a mod it excludes is left out whole); `made`: every owner created here.
void load_boot_definitions(const kiln::boot::Input* boot = nullptr, std::vector<KilnOwner>* made = nullptr) {
    std::set<std::string> mod_ids;
    auto one = [&](const std::string& dir) {
        const fs::path d = widen(dir);
        const std::string id = manifest_id(d);
        if (boot && !id.empty() && boot->out(id)) {
            SI("mod %s is out of the boot generation: its skills are not read", id.c_str());
            return;
        }
        if (!id.empty()) mod_ids.insert(id);
        std::error_code ec;
        if (!fs::is_directory(d / L"skills", ec)) return;
        std::vector<fs::path> files;
        for (auto it = fs::directory_iterator(d / L"skills", ec); !ec && it != fs::directory_iterator(); it.increment(ec))
            if (it->is_regular_file(ec) && it->path().extension() == L".json") files.push_back(it->path());
        std::sort(files.begin(), files.end());
        if (files.empty()) return;
        KilnOwner o = g_api->owner_create(kstr("skills:" + (id.empty() ? dir : id)));
        if (made) made->push_back(o);
        g_mod_owner[id.empty() ? dir : id] = o;
        for (auto& f : files) load_file(o, id, f, d);
    };
    const KilnProfileApi* prof = static_cast<const KilnProfileApi*>(g_api->service(KilnStr{"kiln.profile", 12}, 1));
    bool scan_dropins = true;
    if (prof && prof->header.size >= sizeof(KilnProfileApi)) {
        const uint32_t n = prof->count(KILN_PROFILE_KIND_KILN);
        for (uint32_t i = 0; i < n; ++i) {
            KilnProfileMod m{sizeof m};
            if (prof->get(KILN_PROFILE_KIND_KILN, i, &m) == KILN_OK && m.enabled) one(std::string(m.path.ptr, m.path.len));
        }
        const bool used = prof->mode() == KILN_PROFILE_ACTIVE || prof->mode() == KILN_PROFILE_ONCE;
        if (used) scan_dropins = (prof->dropins() & (1u << KILN_PROFILE_KIND_KILN)) != 0;
    }
    if (scan_dropins) {
        const std::string mods = api_path(KILN_PATH_MODS);
        std::error_code ec;
        std::vector<fs::path> dirs;
        for (auto it = fs::directory_iterator(widen(mods), ec); !ec && it != fs::directory_iterator(); it.increment(ec))
            if (it->is_directory(ec) && !fs::exists(it->path() / L".disabled", ec)) dirs.push_back(it->path());
        std::sort(dirs.begin(), dirs.end());
        for (auto& d : dirs) one(narrow(d.wstring()));
    }
    // a boot generation's documents ({"nodes": [...]}, the cache's form), each mod's after its files
    if (boot)
        for (const std::string& mod : mod_ids) {
            for (const auto& b : boot->docs("skills", mod)) {
                if (!b.document.is_object() || !b.document.contains("nodes") || !b.document["nodes"].is_array()) {
                    SW("mod %s: boot document %s is not {\"nodes\": [...]} (skipped)", mod.c_str(), b.key.c_str());
                    continue;
                }
                std::lock_guard lock(g_mu);
                KilnOwner o = g_mod_owner.count(mod) ? g_mod_owner[mod] : g_api->owner_create(kstr("skills:" + mod));
                if (!g_mod_owner.count(mod) && made) made->push_back(o);
                g_mod_owner[mod] = o;
                int n = 0;
                for (const json& e : b.document["nodes"]) {
                    uint8_t g[16];
                    if (!e.is_object() || !e.contains("guid") || !e["guid"].is_string() || !guid_parse(e["guid"].get<std::string>(), g))
                        continue;
                    bool have = false;
                    for (auto& d : g_defs) have |= std::memcmp(d->node.guid, g, 16) == 0;
                    if (have) continue;  // its data file defines it: the file wins
                    n += load_nodes(o, mod, "boot:" + mod + "/" + b.key, json::array({e}), fs::path(widen(api_path(KILN_PATH_KILN))), nullptr);
                }
                if (n) SI("%d game-tree node(s) of mod %s from its boot definitions", n, mod.c_str());
            }
        }
    // code definitions cached by earlier runs, for mods that are in the load order now
    const fs::path cache = fs::path(widen(api_path(KILN_PATH_DATA))) / L"skills" / L"defined";
    std::error_code ec;
    for (auto it = fs::directory_iterator(cache, ec); !ec && it != fs::directory_iterator(); it.increment(ec)) {
        if (!it->is_regular_file(ec) || it->path().extension() != L".json") continue;
        const std::string mod = narrow(it->path().stem().wstring());
        if (!mod_ids.count(mod)) {
            SI("cached code definitions of mod %s are not used: the mod is not in the load order", mod.c_str());
            continue;
        }
        std::ifstream f(it->path(), std::ios::binary);
        std::string s((std::istreambuf_iterator<char>(f)), {});
        json j = json::parse(s, nullptr, false, true);
        if (!j.is_object() || !j.contains("nodes") || !j["nodes"].is_array()) continue;
        std::lock_guard lock(g_mu);
        KilnOwner o = g_mod_owner.count(mod) ? g_mod_owner[mod] : g_api->owner_create(kstr("skills:" + mod));
        if (!g_mod_owner.count(mod) && made) made->push_back(o);
        g_mod_owner[mod] = o;
        int n = 0;
        for (const json& e : j["nodes"]) {
            uint8_t g[16];
            if (!e.is_object() || !e.contains("guid") || !e["guid"].is_string() || !guid_parse(e["guid"].get<std::string>(), g))
                continue;
            bool have = false;
            for (auto& d : g_defs) have |= std::memcmp(d->node.guid, g, 16) == 0;
            if (have) continue;
            g_code_json[mod][guid_str(g)] = e;
            n += load_nodes(o, mod, "cache:" + mod, json::array({e}), fs::path(widen(api_path(KILN_PATH_KILN))), nullptr);
        }
        if (n) SI("%d game-tree node(s) of mod %s from its cached code definitions", n, mod.c_str());
    }
}

// ------------------------------------------------------------------------------------------------ building ------
void register_texts() {
    if (!g_set_texts) return;
    for (auto& d : g_defs) {
        if (d->state != kInTree || !d->node.texts.is_object()) continue;
        static const char* const kSlots[3] = {"name", "description", "perLevel"};
        for (int s = 0; s < 3; ++s) {
            json per = json::object();
            for (auto& [lang, t] : d->node.texts.items())
                if (t.is_object() && t.contains(kSlots[s]) && t[kSlots[s]].is_string()) per[lang] = t[kSlots[s]];
            if (per.empty()) continue;
            const std::string js = per.dump();
            g_set_texts(text_tag(d->node.guid, s), js.data(), js.size());
        }
    }
}

// Icons the UI cache does not serve (yet) would stop the game's UI ("corrupted game ui texture"): such a node draws
// its like node's icon instead.
void guard_icons() {
    for (auto& d : g_defs) {
        if (!d->node.has_icon || !A) continue;
        uint8_t g[16];
        icon_guid(d->node.guid, g);
        if (std::memcmp(g, d->node.icon, 16) != 0) continue;  // a GUID given directly: the mod's own concern
        if (A->icon_state(g) != KILN_ICON_SERVED) d->node.has_icon = false;
    }
}

// Builds and serves (caller holds g_mu). Only while no world holds a tree, or for the first build.
void rebuild_locked(const char* why) {
    std::vector<Def*> ds;
    for (auto& d : g_defs) ds.push_back(d.get());
    guard_icons();
    const uint32_t need = g_api->side() == KILN_SIDE_CLIENT ? 2u : 1u;  // a client also needs the screen part
    Built b = build(g_vanilla.data(), g_vanilla.size(), ds, false, g_set_texts != nullptr, shadow::level() >= need);
    ++g_builds;
    g_dirty = false;
    for (const auto& p : b.problems) SW("%s", p.c_str());
    for (auto& d : g_defs)
        if (d->state == kError) SW("node %s (%s, %s) is not in the tree: %s", d->node.name.c_str(), guid_str(d->node.guid).c_str(),
                                   d->source.c_str(), d->error.c_str());
    KilnAssetKey key{};
    std::memcpy(key.guid, g_key_guid, 16);
    key.type = 0xe4b6994fu;  // keen::SkillTreeResource
    if (b.added_nodes == 0) {
        if (g_served && A) A->resource_remove(g_owner, &key);
        g_served = false;
        g_built.clear();
    } else {
        const KilnResult r = A ? A->resource_put(g_owner, &key, b.bytes.data(), b.bytes.size(), KILN_ASSET_PUT_OVERRIDE)
                               : KILN_ERR_UNAVAILABLE;
        if (r != KILN_OK) {
            SW("kiln.assets refused the expanded skill tree (%d): the game's tree stays as it is", r);
            for (auto& d : g_defs)
                if (d->state == kInTree) d->state = kError, d->error = "kiln.assets refused the expanded resource";
            g_served = false;
            g_built.clear();
        } else {
            g_served = true;
            g_built = b.bytes;
            register_texts();
        }
    }
    shadow::set_tree(g_served ? b.shadow : std::vector<ShadowNode>{});
    SI("%s: skill tree built: %u nodes (%u game + %u Kiln, %u of them shadow nodes past the game's caps), %u links "
       "(%u + %u), %u of 256 native node slots and %u of 100 level slots free, %zu bytes%s",
       why, b.nodes, b.vanilla_nodes, b.added_nodes, b.shadow_nodes, b.links, b.vanilla_links, b.added_links,
       256 - std::min(256u, b.nodes - b.shadow_nodes), b.free_levels, b.bytes.size(),
       g_served ? " (served as the game's resource)" : " (the game's own)");
    g_last = std::move(b);
    g_last.bytes.clear();
}

uint32_t kind_of_tree(uintptr_t tree) {
    for (uint32_t k : {KILN_WORLD_SERVER, KILN_WORLD_CLIENT}) {
        const uintptr_t w = g_api->world(k);
        if (w && g_api->singleton(w, kstr("g38_skilltree::SkillTree")) == tree) return k;
    }
    // before a world's first step the core does not know it yet: a dedicated server has only its server world
    return g_api->side() == KILN_SIDE_SERVER ? KILN_WORLD_SERVER : 99;
}

kiln::RawHook g_load_hook, g_login_hook;

// Runs before the game's loader when the tree's data is 0. Never throws.
void before_load(void* T) {
    try {
        uint8_t guid[16];
        if (!g_read(static_cast<uint8_t*>(T) + L.guid, guid, 16)) return;
        std::lock_guard lock(g_mu);
        if (!g_have_key) {
            std::memcpy(g_key_guid, guid, 16);
            g_have_key = true;
        } else if (std::memcmp(g_key_guid, guid, 16) != 0) {
            SW("a skill tree reads resource %s, not %s: left alone", guid_str(guid).c_str(), guid_str(g_key_guid).c_str());
            return;
        }
        if (g_vanilla.empty()) {
            KilnAssetKey key{};
            std::memcpy(key.guid, guid, 16);
            key.type = 0xe4b6994fu;
            size_t n = 0;
            KilnResult r = A ? A->resource_read(&key, nullptr, 0, &n) : KILN_ERR_UNAVAILABLE;
            if ((r == KILN_OK || r == KILN_ERR_BUFFER_TOO_SMALL) && n > 16 && n < (64u << 20)) {
                g_vanilla.resize(n);
                r = A->resource_read(&key, g_vanilla.data(), n, &n);
            }
            if (r != KILN_OK) {
                g_vanilla.clear();
                SW("the game's SkillTreeResource %s did not read (%d): the tree stays the game's", guid_str(guid).c_str(), r);
                return;
            }
            const json s = summary(g_vanilla.data(), g_vanilla.size());
            SI("the game's skill tree %s: %s nodes, %s links, levelIndex up to %s (%zu bytes)", guid_str(guid).c_str(),
               s.value("nodes", json(0)).dump().c_str(), s.value("links", json(0)).dump().c_str(),
               s.value("levelMax", json(0)).dump().c_str(), g_vanilla.size());
        }
        // the live trees of worlds that are gone (a missed stop) do not hold the build back
        g_live.erase(std::remove_if(g_live.begin(), g_live.end(), [](const LiveTree& t) {
                         return t.kind != 99 && g_api->world(t.kind) == 0;
                     }), g_live.end());
        if (g_builds == 0 || (g_dirty && g_live.empty())) {
            rebuild_locked(g_builds == 0 ? "first world" : "next world");
        } else if (g_dirty) {
            for (auto& d : g_defs)
                if (d->state == kDefined) d->state = kPending;
            SI("a world loads its skill tree while another holds the current one: it gets the same tree (%u nodes); "
               "changed definitions wait for the next world",
               g_last.nodes);
        }
        g_live.push_back({reinterpret_cast<uintptr_t>(T), kind_of_tree(reinterpret_cast<uintptr_t>(T))});
        ++g_worlds;
    } catch (...) {
        SW("building the skill tree failed (exception): the tree stays the game's");
    }
}

void after_load(void* T) {
    const uintptr_t t = reinterpret_cast<uintptr_t>(T);
    uint64_t data = 0, ids = 0;
    uint32_t nmap = 0, lmap = 0, imap = 0;
    uint64_t ncap = 0, lcap = 0, icap = 0, idcap = 0;
    if (!rd(t + L.data, &data) || !data) return;  // not loaded yet (the resource system answers later)
    rd(t + L.ids + 8, &ids);
    rd(t + L.ids + 16, &idcap);
    rd(t + L.node_map + kMapCount, &nmap);
    rd(t + L.link_map + kMapCount, &lmap);
    rd(t + L.index_map + kMapCount, &imap);
    rd(t + L.node_map + kMapCap, &ncap);
    rd(t + L.link_map + kMapCap, &lcap);
    rd(t + L.index_map + kMapCap, &icap);
    uint32_t want_n, want_l;
    bool served;
    {
        std::lock_guard lock(g_mu);
        want_n = g_last.nodes;
        want_l = g_last.links;
        served = g_served;
    }
    const uint32_t k = kind_of_tree(t);
    const bool ok = ids == want_n && nmap == want_n && imap == want_n && lmap == want_l;
    logf(ok ? KILN_LOG_INFO : KILN_LOG_WARN,
         "skills: the %s world's skill tree loaded %llu nodes and %u links (expected %u / %u%s): node map %u of %llu, "
         "link map %u of %llu, index map %u of %llu, id list %llu of %llu%s",
         k == KILN_WORLD_SERVER ? "server" : k == KILN_WORLD_CLIENT ? "client" : "new", static_cast<unsigned long long>(ids),
         lmap, want_n, want_l, served ? ", Kiln's resource" : ", the game's resource", nmap,
         static_cast<unsigned long long>(ncap), lmap, static_cast<unsigned long long>(lcap), imap,
         static_cast<unsigned long long>(icap), static_cast<unsigned long long>(ids), static_cast<unsigned long long>(idcap),
         ok ? "" : " - MISMATCH");
}

void hk_tree_load(void* T) {
    uint64_t data = 1;
    if (T && g_load_on.load(std::memory_order_relaxed) && rd(reinterpret_cast<uintptr_t>(T) + L.data, &data) && data == 0) {
        before_load(T);
        g_load_hook.call<void>(T);
        after_load(T);
        return;
    }
    g_load_hook.call<void>(T);
}

// ---- login guard ----
std::mutex g_strip_mu;
std::vector<KilnSkillsStrip> g_strips;  // ring of 32
struct StripPending {
    uint64_t seq;
    uintptr_t comp;
};
std::vector<StripPending> g_strip_pending;
uint64_t g_strip_seq = 0;
std::atomic<uint64_t> g_stripped{0};
std::atomic<uint64_t> g_step{0};

// SEH part: finds ids the tree does not know; strips them (after asking the game for write access). Returns count.
// Shadow nodes found in the list (they never belong there) are moved out too: *n_shadow of them, in sids / slevels.
int strip_raw(uint8_t* iter, uint32_t* ids, uint8_t* levels, uintptr_t* comp, uint32_t* sids, uint8_t* slevels,
              int* n_shadow) {
    __try {
        uint8_t* u = *reinterpret_cast<uint8_t**>(iter + L.login_unlocked);
        void* tree = *reinterpret_cast<void**>(iter + L.login_tree);
        if (!u || !tree || u[0] != 0) return 0;
        int slots[256];
        int n = 0, ns = 0;
        for (int i = 0; i < 256; ++i) {
            uint32_t id;
            std::memcpy(&id, u + kU_Ids + i * 4, 4);
            if (!id) continue;
            if (shadow::is_shadow(id)) {
                if (ns < 256) sids[ns] = id, slevels[ns] = u[kU_Levels + i], ++ns;
                slots[n++] = i;
            } else if (!G.node_by_id(id, tree)) {
                slots[n++] = i;
            }
        }
        *n_shadow = ns;
        if (!n) return 0;
        uint8_t* w = static_cast<uint8_t*>(G.write_access(iter + L.login_unlocked));
        if (w != u) return -1;
        for (int k = 0; k < n; ++k) {
            const int i = slots[k];
            if (k < 16) {
                std::memcpy(&ids[k], u + kU_Ids + i * 4, 4);
                levels[k] = u[kU_Levels + i];
            }
            std::memset(u + kU_Ids + i * 4, 0, 4);
            u[kU_Levels + i] = 0;
        }
        *comp = reinterpret_cast<uintptr_t>(u);
        return n;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -2;
    }
}

// The check said no (out[0] 0 = decided, out[8] 0 = invalid), so the game resets the tree right after this returns: says
// why, with the game's own functions. Before this the only trace was the reset itself.
void log_failed_check(const uint8_t* out, uint8_t* iter) {
    uint8_t* u = nullptr;
    void* tree = nullptr;
    uint8_t* ui = nullptr;
    uint32_t count = 0;
    __try {
        if (out[0] != 0 || out[8] != 0) return;
        u = *reinterpret_cast<uint8_t**>(iter + L.login_unlocked);
        tree = *reinterpret_cast<void**>(iter + L.login_tree);
        ui = *reinterpret_cast<uint8_t**>(iter + L.login_ui);
        if (!u || !tree || !ui) return;
        for (int i = 0; i < 256; ++i) {
            uint32_t id;
            std::memcpy(&id, u + kU_Ids + i * 4, 4);
            if (id) ++count;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return;
    }
    if (!g_fns_on) {
        SW("the game's join check rejected a character's tree (%u nodes): it resets it", count);
        return;
    }
    const bool rebuilt = g_call_bool3(reinterpret_cast<void*>(G.ui_rebuild), 2, 0, ui, u, tree) == 1;
    const bool connected = rebuilt && g_call_bool3(reinterpret_cast<void*>(G.connected), 3, 0, ui, nullptr, tree) == 1;
    const bool levels = rebuilt && g_call_bool3(reinterpret_cast<void*>(G.levels_valid), 4, 0, u, ui, tree) == 1;
    SW("the game's join check rejected a character's tree (%u nodes): %s. The game resets the whole tree, free; shadow "
       "nodes go with it", count,
       !rebuilt ? "a node id the tree does not know" : !connected ? "some nodes are not linked to a root (an admin force unlock?)"
       : !levels ? "a level above a node's maxLevel" : "it costs more than the character's skill points");
}

void* hk_login(void* out, uint8_t* iter) {
    if (g_guard_on.load(std::memory_order_relaxed) && iter) {
        uint32_t ids[16] = {};
        uint8_t levels[16] = {};
        uint32_t sids[256];
        uint8_t slevels[256];
        int n_shadow = 0;
        uintptr_t comp = 0;
        const int n_all = strip_raw(iter, ids, levels, &comp, sids, slevels, &n_shadow);
        for (int k = 0; k < n_shadow && n_all > 0; ++k)
            if (!shadow::migrate(reinterpret_cast<void*>(comp), sids[k], slevels[k]))
                SW("a character's save holds 0x%08x (level %u), a shadow node of this tree, but its player is not known yet: it could not be "
                   "moved into the shadow table, and its points are free again", sids[k], static_cast<unsigned>(slevels[k] >> 5));
        const int n = n_all > 0 ? n_all - n_shadow : n_all;
        if (n < 0) {
            g_guard_on = false;
            set_reason("the login guard faulted (%d): off", n);
        } else if (n > 0) {
            g_stripped.fetch_add(static_cast<uint64_t>(n));
            std::lock_guard lock(g_strip_mu);
            KilnSkillsStrip s{};
            s.seq = ++g_strip_seq;
            s.step = g_step.load();
            s.count = static_cast<uint32_t>(n);
            std::memcpy(s.ids, ids, sizeof ids);
            std::memcpy(s.levels, levels, sizeof levels);
            if (g_strips.size() < 32) g_strips.push_back(s);
            else g_strips[s.seq % 32] = s;
            g_strip_pending.push_back({s.seq, comp});
        }
    }
    // The game judges the native tree only: shadow costs are Kiln's to check, so they can't reset a valid native tree.
    shadow::native_costs_only(true);
    void* r = g_login_hook.call<void*>(out, iter);
    shadow::native_costs_only(false);
    log_failed_check(static_cast<const uint8_t*>(out), iter);
    return r;
}

// ------------------------------------------------------------------------------------------------ the table -----
void fill(char* buf, size_t cap, size_t* out_len, const std::string& s) {
    if (out_len) *out_len = s.size();
    if (buf && cap) {
        const size_t n = std::min(cap - 1, s.size());
        std::memcpy(buf, s.data(), n);
        buf[n] = 0;
    }
}
KilnResult fill_r(char* buf, size_t cap, size_t* out_len, const std::string& s) {
    fill(buf, cap, out_len, s);
    return buf && cap > s.size() ? KILN_OK : (buf ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK);
}

KilnResult KILN_CALL t_status(KilnSkillsStatus* o) {
    if (!o || o->size < sizeof(KilnSkillsStatus)) return KILN_ERR_INVALID_ARGUMENT;
    std::memset(reinterpret_cast<uint8_t*>(o) + 4, 0, sizeof *o - 4);
    o->tree_load = g_load_on;
    o->login_guard = g_guard_on;
    o->functions = g_fns_on;
    o->texts = g_set_texts != nullptr;
    {
        std::lock_guard lock(g_mu);
        o->vanilla_nodes = g_last.vanilla_nodes;
        o->vanilla_links = g_last.vanilla_links;
        o->vanilla_level_max = g_last.level_max;
        o->nodes = g_last.nodes;
        o->links = g_last.links;
        o->added_nodes = g_last.added_nodes;
        o->added_links = g_last.added_links;
        o->free_nodes = g_last.nodes ? 256 - std::min(256u, g_last.nodes - g_last.shadow_nodes) : 0;
        o->free_levels = g_last.free_levels;
        for (auto& d : g_defs) {
            if (d->state == kOrphaned) continue;
            ++o->defined;
            o->refused += d->state == kError;
            o->pending += d->state == kPending || (d->state == kDefined && g_builds);
        }
        o->worlds = static_cast<uint32_t>(g_worlds);
        o->builds = static_cast<uint32_t>(g_builds);
    }
    o->stripped = g_stripped.load();
    o->shadow = static_cast<uint8_t>(shadow::level());
    {
        uint32_t sn = 0;
        shadow::t_nodes(nullptr, 0, &sn);
        o->shadow_nodes = sn;
    }
    {
        std::lock_guard lock(g_strip_mu);
        o->strip_seq = g_strip_seq;
    }
    std::lock_guard lock(g_reason_mu);
    std::snprintf(o->reason, sizeof o->reason, "%s", g_reason.empty() ? shadow::why_off().c_str() : g_reason.c_str());
    return KILN_OK;
}

KilnResult KILN_CALL t_define(KilnOwner owner, KilnStr text, char* report, size_t cap, size_t* out_len) {
    json rep = json::object();
    json nodes_rep = json::array();
    json j = json::parse(std::string(text.ptr ? text.ptr : "", text.len), nullptr, false, true);
    std::string mod;
    json nodes;
    if (j.is_array()) nodes = j;
    else if (j.is_object() && j.contains("nodes")) {
        nodes = j["nodes"];
        if (j.contains("mod") && j["mod"].is_string()) mod = j["mod"].get<std::string>();
    } else if (j.is_object()) nodes = json::array({j});
    if (!nodes.is_array()) {
        rep["ok"] = false;
        rep["error"] = "expected a node object, an array of them, or {\"nodes\": [...]}";
        fill(report, cap, out_len, rep.dump());
        return KILN_ERR_INVALID_ARGUMENT;
    }
    int n = 0;
    bool live = false;
    {
        std::lock_guard lock(g_mu);
        n = load_nodes(owner, mod, "code", nodes, fs::path(widen(api_path(KILN_PATH_KILN))), &nodes_rep);
        live = !g_live.empty() || g_builds;
        if (live)
            for (auto& d : g_defs)
                if (d->owner == owner && d->state == kDefined) d->state = kPending;
        if (!mod.empty()) {
            for (const json& e : nodes) {
                uint8_t g[16];
                if (e.is_object() && e.contains("guid") && e["guid"].is_string() && guid_parse(e["guid"].get<std::string>(), g))
                    g_code_json[mod][guid_str(g)] = e;
            }
            save_cache(mod);
        }
        for (json& r : nodes_rep)
            if (live && r.value("state", "") != "error") r["state"] = "pending";
    }
    if (live && n)
        SI("%d game-tree node(s) defined after a world read its tree: in the tree at the next world load%s", n,
           mod.empty() ? "" : " (cached for the next start)");
    rep["ok"] = n > 0;
    rep["nodes"] = nodes_rep;
    fill(report, cap, out_len, rep.dump());
    return n > 0 ? KILN_OK : KILN_ERR_INVALID_ARGUMENT;
}

uint32_t KILN_CALL t_undefine_owner(KilnOwner owner) {
    std::lock_guard lock(g_mu);
    uint32_t n = 0;
    for (auto& d : g_defs)
        if (d->owner == owner && d->state != kOrphaned) d->state = kOrphaned, ++n, g_dirty = true;
    return n;
}

KilnResult KILN_CALL t_definitions(char* buf, size_t cap, size_t* out_len) {
    json a = json::array();
    {
        std::lock_guard lock(g_mu);
        for (auto& d : g_defs) {
            json e = {{"guid", guid_str(d->node.guid)}, {"id", hex32(d->node.id)}, {"name", d->node.name},
                      {"owner", d->owner_id}, {"source", d->source}, {"state", state_name(d->state)}};
            if (d->index >= 0) e["index"] = d->index;
            if (d->level_index >= 0) e["levelIndex"] = d->level_index;
            if (d->shadow) e["shadow"] = true;
            if (!d->error.empty()) e["error"] = d->error;
            if (d->tag[0]) e["nameTag"] = hex32(d->tag[0]);
            // the links as defined (a game node's "0x<id>", a node GUID or a Kiln node's name): which node hangs on which
            if (!d->node.from.empty()) e["from"] = d->node.from;
            if (!d->node.to.empty()) e["to"] = d->node.to;
            a.push_back(e);
        }
    }
    return fill_r(buf, cap, out_len, a.dump());
}

uint32_t rd32le(const uint8_t* p) {
    uint32_t v;
    std::memcpy(&v, p, 4);
    return v;
}

void* server_tree() {
    const uintptr_t w = g_api->server_world();
    return w ? reinterpret_cast<void*>(g_api->singleton(w, kstr("g38_skilltree::SkillTree"))) : nullptr;
}

KilnResult KILN_CALL t_tree(uint32_t flags, char* buf, size_t cap, size_t* out_len) {
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!g_fns_on) return KILN_ERR_UNAVAILABLE;
    void* T = server_tree();
    if (!T) return KILN_ERR_NOT_FOUND;
    const uintptr_t t = reinterpret_cast<uintptr_t>(T);
    uint64_t idp = 0, cnt = 0, data = 0;
    if (!rd(t + L.ids, &idp) || !rd(t + L.ids + 8, &cnt) || !rd(t + L.data, &data) || !data || cnt > 4096)
        return KILN_ERR_NOT_FOUND;
    std::set<uint32_t> kiln_ids;
    {
        std::lock_guard lock(g_mu);
        for (auto& d : g_defs)
            if (d->state == kInTree || d->state == kPending) kiln_ids.insert(d->node.id);
    }
    json nodes = json::array();
    bool fault = false;
    uint32_t kiln_n = 0;
    for (uint64_t i = 0; i < cnt && !fault; ++i) {
        uint32_t id = 0;
        if (!rd(idp + i * 4, &id)) break;
        const bool mine = kiln_ids.count(id) != 0;
        kiln_n += mine;
        if ((flags & 1) && !mine) continue;
        const uint8_t* n = g_node(id, T, &fault);
        json e = {{"index", i}, {"id", hex32(id)}, {"kiln", mine}, {"shadow", shadow::is_shadow(id)}};
        uint8_t b[kNode];
        if (n && g_read(n, b, kNode)) {
            float x, y;
            uint16_t c;
            uint32_t in, out;
            std::memcpy(&x, b + F::pos, 4);
            std::memcpy(&y, b + F::pos + 4, 4);
            std::memcpy(&c, b + F::cost, 2);
            std::memcpy(&in, b + F::in + 4, 4);
            std::memcpy(&out, b + F::out + 4, 4);
            e["type"] = b[F::type];
            e["class"] = b[F::cls];
            e["cost"] = c;
            e["maxLevel"] = b[F::max_level];
            e["levelIndex"] = b[F::level_index];
            e["x"] = x;
            e["y"] = y;
            e["in"] = in;
            e["out"] = out;
            uint8_t zero[16] = {};
            e["program"] = std::memcmp(b + F::impact, zero, 16) ? guid_str(b + F::impact) : "";
            e["name"] = hex32(rd32le(b + F::name));
        } else {
            e["missing"] = true;  // the game's NodeById did not find it
        }
        nodes.push_back(e);
    }
    uint32_t nm = 0, lm = 0, im = 0;
    uint64_t nc = 0, lc = 0, ic = 0;
    rd(t + L.node_map + kMapCount, &nm);
    rd(t + L.link_map + kMapCount, &lm);
    rd(t + L.index_map + kMapCount, &im);
    rd(t + L.node_map + kMapCap, &nc);
    rd(t + L.link_map + kMapCap, &lc);
    rd(t + L.index_map + kMapCap, &ic);
    json j = {{"world", "server"},
              {"count", cnt},
              {"kiln", kiln_n},
              {"nodes", nodes},
              {"maps", {{"nodes", nm}, {"nodesCap", nc}, {"links", lm}, {"linksCap", lc}, {"index", im}, {"indexCap", ic}}},
              {"data", hex32(static_cast<uint32_t>(data))}};
    return fill_r(buf, cap, out_len, j.dump());
}

int index_in(void* T, uint32_t id) {
    uint64_t idp = 0, cnt = 0;
    const uintptr_t t = reinterpret_cast<uintptr_t>(T);
    if (!rd(t + L.ids, &idp) || !rd(t + L.ids + 8, &cnt) || cnt > 4096) return -1;
    for (uint64_t i = 0; i < cnt; ++i) {
        uint32_t v = 0;
        if (!rd(idp + i * 4, &v)) return -1;
        if (v == id) return static_cast<int>(i);
    }
    return -1;
}

KilnResult KILN_CALL t_simulate(const KilnSkillsSimStep* steps, uint32_t count, KilnSkillsSimResult* out) {
    if (!steps || !out || count > 256) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!g_fns_on) return KILN_ERR_UNAVAILABLE;
    void* T = server_tree();
    if (!T) return KILN_ERR_NOT_FOUND;
    alignas(8) static uint8_t u[kUnlocked];
    alignas(8) static uint8_t ui[kUi];
    std::memset(u, 0, sizeof u);
    std::memset(ui, 0, sizeof ui);
    u[0] = 1;
    for (uint32_t i = 0; i < count; ++i) {
        KilnSkillsSimResult& r = out[i];
        std::memset(&r, 0, sizeof r);
        r.op = steps[i].op;
        r.node = steps[i].node;
        bool fault = false;
        r.known = g_node(r.node, T, &fault) != nullptr;
        r.index = index_in(T, r.node);
        r.reachable = g_call_bool3(reinterpret_cast<void*>(G.reachable), 1, r.node, ui, nullptr, T) == 1;
        switch (r.op) {
        case KILN_SKILLS_SIM_UNLOCK:
            r.ok = r.reachable && g_call_bool3(reinterpret_cast<void*>(G.node_set), 0, r.node, u, u, T) == 1;
            break;
        case KILN_SKILLS_SIM_UNLOCK_FORCE:
            r.ok = g_call_bool3(reinterpret_cast<void*>(G.node_set), 0, r.node, u, u, T) == 1;
            break;
        case KILN_SKILLS_SIM_LOCK:
            r.ok = g_call_bool3(reinterpret_cast<void*>(G.node_set), 0, r.node, u, nullptr, T) == 1;
            break;
        case KILN_SKILLS_SIM_ADD_RAW:
            for (int s = 0; s < 256; ++s) {
                uint32_t id;
                std::memcpy(&id, u + kU_Ids + s * 4, 4);
                if (id) continue;
                std::memcpy(u + kU_Ids + s * 4, &r.node, 4);
                u[kU_Levels + s] = 0x20;
                r.ok = 1;
                break;
            }
            break;
        case KILN_SKILLS_SIM_STRIP:
            for (int s = 0; s < 256; ++s) {
                uint32_t id;
                std::memcpy(&id, u + kU_Ids + s * 4, 4);
                if (!id || g_node(id, T, &fault)) continue;
                std::memset(u + kU_Ids + s * 4, 0, 4);
                u[kU_Levels + s] = 0;
                ++r.stripped;
            }
            r.ok = !fault;
            break;
        default:
            r.ok = 1;
        }
        ++u[kU_Change];
        std::memset(ui, 0, sizeof ui);
        r.rebuild = g_call_bool3(reinterpret_cast<void*>(G.ui_rebuild), 2, 0, ui, u, T) == 1;
        r.connected = r.rebuild && g_call_bool3(reinterpret_cast<void*>(G.connected), 3, 0, ui, nullptr, T) == 1;
        r.levels = g_call_bool3(reinterpret_cast<void*>(G.levels_valid), 4, 0, u, ui, T) == 1;
        r.spent = r.rebuild ? static_cast<int32_t>(g_spent(ui, ui + kUi_Levels, T)) : -1;
        for (int k = 0; k < 4; ++k) {
            uint64_t m;
            std::memcpy(&m, ui + k * 8, 8);
            r.nodes += static_cast<uint32_t>(__builtin_popcountll(m));
        }
        for (int s = 0; s < 256; ++s) {
            uint32_t id;
            std::memcpy(&id, u + kU_Ids + s * 4, 4);
            if (id == r.node) r.level = static_cast<uint8_t>(u[kU_Levels + s] >> 5);
        }
        if (fault) return KILN_ERR_INTERNAL;
    }
    return KILN_OK;
}

KilnResult KILN_CALL t_strips(uint64_t after, KilnSkillsStrip* out, uint32_t capacity, uint32_t* out_count) {
    std::lock_guard lock(g_strip_mu);
    std::vector<KilnSkillsStrip> v;
    for (auto& s : g_strips)
        if (s.seq > after) v.push_back(s);
    std::sort(v.begin(), v.end(), [](auto& a, auto& b) { return a.seq < b.seq; });
    if (out_count) *out_count = static_cast<uint32_t>(v.size());
    for (uint32_t i = 0; i < v.size() && i < capacity && out; ++i) out[i] = v[i];
    return v.size() > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult KILN_CALL t_resource(uint32_t which, void* buffer, size_t cap, size_t* out_len, char* report, size_t rcap) {
    std::vector<uint8_t> bytes;
    json rep;
    {
        std::lock_guard lock(g_mu);
        if (g_vanilla.empty()) return KILN_ERR_NOT_FOUND;
        if (which == 1) {
            bytes = g_vanilla;
        } else if (which == 0 && g_served) {
            bytes = g_built;  // exactly what the game reads
        } else {
            // 0: what would be served now (a scratch build of copies of the definitions); 2: the copy path with no
            // additions (every node and link array rebuilt, nothing added)
            std::vector<Def> copy;
            if (which == 0)
                for (auto& d : g_defs) copy.push_back(*d);
            std::vector<Def*> ds;
            for (auto& d : copy) ds.push_back(&d);
            const uint32_t need = g_api->side() == KILN_SIDE_CLIENT ? 2u : 1u;
            Built b = build(g_vanilla.data(), g_vanilla.size(), ds, which == 2, g_set_texts != nullptr, shadow::level() >= need);
            bytes = std::move(b.bytes);
        }
        if (report) {
            rep = verify(g_vanilla.data(), g_vanilla.size(), bytes.data(), bytes.size());
            rep["vanillaIdentical"] = bytes == g_vanilla;
            rep["served"] = g_served && bytes == g_built;
        }
    }
    if (report) fill(report, rcap, nullptr, rep.dump());
    if (out_len) *out_len = bytes.size();
    if (!buffer) return KILN_OK;
    if (cap < bytes.size()) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buffer, bytes.data(), bytes.size());
    return KILN_OK;
}

uint32_t KILN_CALL t_shadow_on() { return shadow::level(); }
KilnResult KILN_CALL t_shadow_nodes(uint32_t* o, uint32_t c, uint32_t* n) { return shadow::t_nodes(o, c, n); }
KilnResult KILN_CALL t_shadow_get(uint32_t w, KilnEntity e, KilnSkillsShadowEntry* o, uint32_t c, uint32_t* n) {
    return shadow::t_get(w, e, o, c, n);
}
KilnResult KILN_CALL t_shadow_set(uint32_t w, KilnEntity e, const KilnSkillsShadowEntry* in, uint32_t n) {
    return shadow::t_set(w, e, in, n);
}
KilnResult KILN_CALL t_shadow_changes(uint64_t a, KilnSkillsShadowChange* o, uint32_t c, uint32_t* n) {
    return shadow::t_changes(a, o, c, n);
}
KilnResult KILN_CALL t_shadow_scope(uint32_t w, KilnEntity e) { return shadow::t_scope(w, e); }
KilnResult KILN_CALL t_shadow_level(uint32_t w, KilnEntity e, uint32_t node, uint32_t* l, uint32_t* m) {
    return shadow::t_level(w, e, node, l, m);
}
KilnResult KILN_CALL t_shadow_reset(uint32_t w, KilnEntity e, uint32_t* l) { return shadow::t_reset(w, e, l); }
KilnResult KILN_CALL t_shadow_linked(uint32_t w, KilnEntity e, const void* ui, void* tree, uint32_t without, int32_t* ok) {
    return shadow::t_linked(w, e, ui, tree, without, ok);
}
KilnResult KILN_CALL t_spent(KilnEntity e, int32_t* o) { return shadow::t_spent(e, o); }
KilnResult KILN_CALL t_is_unlocked(KilnEntity e, uint32_t node, int32_t* u, int32_t* r) {
    return shadow::t_is_unlocked(e, node, u, r);
}

// PlayerInput (1472 B) +0x08 fromClient (ClientPlayerInputData) +0x178 unlockSkillAction {skillId +0, unlearnSkill +4,
// resetAllSkills +5, versionData +8} (reflection, svn 1287369; sigdb skills/input.unlock).
int64_t g_input_unlock = 0x180;
bool write_input(uint8_t* in, uint32_t op, uint32_t node) {
    __try {
        std::memcpy(in + g_input_unlock, &node, 4);
        in[g_input_unlock + 4] = op == 1;
        in[g_input_unlock + 5] = op == 2;
        uint32_t ver;
        std::memcpy(&ver, in + g_input_unlock + 8, 4);
        ++ver;
        std::memcpy(in + g_input_unlock + 8, &ver, 4);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool set_initialized(uint8_t* u) {
    __try {
        u[0] = 1;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool put_in_list(uint8_t* u, uint32_t node, uint32_t level) {
    __try {
        for (int i = 0; i < 256; ++i) {
            uint32_t id;
            std::memcpy(&id, u + kU_Ids + i * 4, 4);
            if (id != 0 && id != node) continue;
            std::memcpy(u + kU_Ids + i * 4, &node, 4);
            u[kU_Levels + i] = static_cast<uint8_t>(level << 5);
            return true;
        }
        return false;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
int shadow_ids_in_list(uint8_t* u, int* slots, uint32_t* ids, uint8_t* levels) {
    __try {
        int n = 0;
        for (int i = 0; i < 256; ++i) {
            uint32_t id;
            std::memcpy(&id, u + kU_Ids + i * 4, 4);
            if (!id || !shadow::is_shadow(id)) continue;
            slots[n] = i, ids[n] = id, levels[n] = u[kU_Levels + i], ++n;
        }
        return n;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}
bool clear_list_slots(uint8_t* u, const int* slots, int n) {
    __try {
        for (int k = 0; k < n; ++k) {
            std::memset(u + kU_Ids + slots[k] * 4, 0, 4);
            u[kU_Levels + slots[k]] = 0;
        }
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool login_guard_lab(uint8_t* u) {
    int slots[256];
    uint32_t ids[256];
    uint8_t levels[256];
    const int n = shadow_ids_in_list(u, slots, ids, levels);
    if (n < 0 || !clear_list_slots(u, slots, n)) return false;
    for (int k = 0; k < n; ++k) shadow::migrate(u, ids[k], levels[k]);
    return true;
}
bool poke_byte(uint8_t* at, uint8_t value) {
    __try {
        *at = value;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
// Requests 3 and up change a player or item entity's components directly, which only a test has any use for. They run where <kiln>/data/skills/lab-ops exists,
// which a test server's deployment or its test mod creates; everywhere else they are refused. Read at each call, so a test can turn it off.
bool lab_ops_on() {
    if (GetFileAttributesW(widen(api_path(KILN_PATH_DATA) + "\\skills\\lab-ops").c_str()) != INVALID_FILE_ATTRIBUTES) return true;
    static std::atomic<bool> told{false};
    if (!told.exchange(true)) SW("a test-only request was refused: <kiln>/data/skills/lab-ops does not exist");
    return false;
}

// Which inventory slot a player entity's equipment slot is (keen::ecs::EquipmentSlotData: 20 InventorySlotId { entity, slot } at the start of SlotSelection).
// A player-template entity spawned for a test has none set, a join sets them. So it only runs on a player entity whose slots this has written or are all
// empty: never on a joined player.
std::set<KilnEntity> g_lab_equipped;
KilnResult lab_equip(KilnEntity e, uint32_t slot, uint32_t node) {
    if (slot >= 20) return KILN_ERR_INVALID_ARGUMENT;
    uint32_t sz = 0;
    if (!W->component(e, W->component_hash(kstr("PlayerState")), &sz) || sz != 96) return KILN_ERR_NOT_FOUND;
    auto* c = const_cast<uint8_t*>(static_cast<const uint8_t*>(W->component(e, W->component_hash(kstr("SlotSelection")), &sz)));
    if (!c || sz != 216) return KILN_ERR_NOT_FOUND;
    if (!g_lab_equipped.count(e))
        for (uint32_t i = 0; i < 160; ++i)
            if (c[i]) return KILN_ERR_INVALID_ARGUMENT;
    uint8_t* at = c + slot * 8;
    const uint32_t entity = node & 0xffffff;
    bool ok = true;
    for (int i = 0; i < 4; ++i) ok = ok && poke_byte(at + i, static_cast<uint8_t>(entity >> (i * 8)));
    ok = ok && poke_byte(at + 4, static_cast<uint8_t>(node >> 24));
    if (ok) g_lab_equipped.insert(e);
    return ok ? KILN_OK : KILN_ERR_INTERNAL;
}
KilnResult KILN_CALL t_game_request(KilnEntity e, uint32_t op_and_level, uint32_t node) {
    const uint32_t op = op_and_level & 0xff, level = op_and_level >> 8;
    if (op > 8) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!W) return KILN_ERR_UNAVAILABLE;
    if (op >= 3 && !lab_ops_on()) return KILN_ERR_UNAVAILABLE;
    if (op == 8) return lab_equip(e, level, node);
    uint32_t sz = 0;
    if (op == 6) {
        // lab: a node in the entity's list as a character's save holds it (a tree from a run with another mod list)
        auto* u = const_cast<uint8_t*>(static_cast<const uint8_t*>(W->component(e, W->component_hash(kstr("UnlockedSkillNodes")), &sz)));
        if (!u || sz != kUnlocked) return KILN_ERR_NOT_FOUND;
        return put_in_list(u, node, level) ? KILN_OK : KILN_ERR_INTERNAL;
    }
    if (op == 7) {
        // lab: what the login guard does with a shadow id it finds in a character's save, at the moment the game's check runs
        auto* u = const_cast<uint8_t*>(static_cast<const uint8_t*>(W->component(e, W->component_hash(kstr("UnlockedSkillNodes")), &sz)));
        if (!u || sz != kUnlocked) return KILN_ERR_NOT_FOUND;
        return login_guard_lab(u) ? KILN_OK : KILN_ERR_INTERNAL;
    }
    if (op == 4 || op == 5) {
        // lab: an item's unlocked enhancements (the game's upgrade pays for them), a player entity's selected hotbar slot
        const char* comp = op == 4 ? "PerkContainerNew" : "SlotSelection";
        const size_t at = op == 4 ? 1 : 0xc0, need = op == 4 ? 2 : 216;
        auto* c = const_cast<uint8_t*>(static_cast<const uint8_t*>(W->component(e, W->component_hash(kstr(comp)), &sz)));
        if (!c || sz != need) return KILN_ERR_NOT_FOUND;
        return poke_byte(c + at, static_cast<uint8_t>(node)) ? KILN_OK : KILN_ERR_INTERNAL;
    }
    if (op == 3) {
        // the join check waits for the player's skill-point knowledge, which a player-template entity never gets
        auto* u = const_cast<uint8_t*>(static_cast<const uint8_t*>(W->component(e, W->component_hash(kstr("UnlockedSkillNodes")), &sz)));
        if (!u || sz != kUnlocked) return KILN_ERR_NOT_FOUND;
        return set_initialized(u) ? KILN_OK : KILN_ERR_INTERNAL;
    }
    auto* in = const_cast<uint8_t*>(static_cast<const uint8_t*>(W->component(e, W->component_hash(kstr("PlayerInput")), &sz)));
    if (!in || sz != 1472) return KILN_ERR_NOT_FOUND;
    return write_input(in, op, node) ? KILN_OK : KILN_ERR_INTERNAL;
}

const KilnSkillsApi kTable = {
    {sizeof(KilnSkillsApi), KILN_SKILLS_VERSION}, &t_status, &t_define, &t_undefine_owner, &t_definitions, &t_tree,
    &t_simulate, &t_strips, &t_resource, &t_shadow_on, &t_shadow_nodes, &t_shadow_get, &t_shadow_set, &t_shadow_changes,
    &t_spent, &t_is_unlocked, &t_game_request, &t_shadow_scope, &t_shadow_level, &t_shadow_reset, &t_shadow_linked,
};

// ------------------------------------------------------------------------------------------------ ticks ---------
uint32_t H_UNLOCKED = 0, H_PLAYERSTATE = 0;

void KILN_CALL on_pre(const KilnTickInfo* tick, void*) {
    g_step.store(tick->step);
    shadow::pre_server();
    std::vector<StripPending> todo;
    {
        std::lock_guard lock(g_strip_mu);
        todo.swap(g_strip_pending);
    }
    if (todo.empty() || !W) return;
    KilnEntity players[64];
    uint32_t n = 0;
    if (W->find(H_PLAYERSTATE, players, 64, &n) != KILN_OK) n = std::min<uint32_t>(n, 64);
    for (auto& p : todo) {
        KilnEntity who = 0;
        for (uint32_t i = 0; i < n && i < 64; ++i) {
            uint32_t sz = 0;
            if (reinterpret_cast<uintptr_t>(W->component(players[i], H_UNLOCKED, &sz)) == p.comp) who = players[i];
        }
        KilnSkillsStrip s{};
        {
            std::lock_guard lock(g_strip_mu);
            for (auto& r : g_strips)
                if (r.seq == p.seq) r.entity = who, s = r;
        }
        std::string ids;
        for (uint32_t k = 0; k < s.count && k < 16; ++k) ids += (k ? ", " : "") + hex32(s.ids[k]) + " L" + std::to_string(s.levels[k] >> 5);
        SI("player entity %u joined with %u skill node(s) the tree does not know (a mod that is gone?): %s; removed "
           "before the game's check (their points are free again; the rest of the tree stays)",
           who, s.count, ids.c_str());
    }
}

void KILN_CALL on_client_pre(const KilnTickInfo*, void*) { shadow::pre_client(); }

void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    if (ev->event != KILN_WORLD_STOPPING) return;
    const uintptr_t T = g_api->singleton(ev->world, kstr("g38_skilltree::SkillTree"));
    std::lock_guard lock(g_mu);
    const size_t before = g_live.size();
    g_live.erase(std::remove_if(g_live.begin(), g_live.end(),
                                [&](const LiveTree& t) { return (T && t.tree == T) || (!T && t.kind == ev->kind); }),
                 g_live.end());
    if (g_live.size() != before && g_dirty)
        SI("a world with a skill tree stopped; changed definitions are built for the next world");
}

bool sym(const char* n, void* out) {
    uintptr_t a = 0;
    if (g_api->symbol(kstr(n), &a) != KILN_OK || !a) return false;
    std::memcpy(out, &a, sizeof a);
    return true;
}
bool cst(const char* n, int64_t* out) { return g_api->constant(kstr(n), out) == KILN_OK; }

// A boot generation (chapter 08): the definitions on_ready reads without a boot profile are read here, after OnBoot, before the game's
// entry point; the tree is built from them when the game loads it (hk_tree_load). destroy takes them out again.
struct BootPlan {
    std::vector<KilnOwner> owners;
};
void KILN_CALL destroy_boot(const void* p) {
    const auto* plan = static_cast<const BootPlan*>(p);
    {
        std::lock_guard lock(g_mu);
        const std::set<KilnOwner> mine(plan->owners.begin(), plan->owners.end());
        std::erase_if(g_defs, [&](const std::unique_ptr<Def>& d) { return mine.count(d->owner) != 0; });
        std::erase_if(g_mod_owner, [&](const auto& kv) { return mine.count(kv.second) != 0; });
        g_code_json.clear();
        g_dirty = true;
    }
    for (const KilnOwner o : plan->owners)
        if (A) A->revert_owner(o);  // the nodes' icons
    SI("the boot generation's prepared game-tree definitions are dropped (%zu owner(s))", plan->owners.size());
    delete plan;
}
KilnResult KILN_CALL prepare_boot(void*, const KilnBootPrepareInputV1* in, KilnBootPreparedV1* out, KilnBootIssueV1* issue) {
    const kiln::boot::Input input = kiln::boot::parse(in);
    if (!input.ok) return kiln::boot::refuse(out, issue, "", "", "", "kiln.skills: the boot envelope is not JSON");
    auto* plan = new BootPlan;
    load_boot_definitions(&input, &plan->owners);
    size_t n;
    {
        std::lock_guard lock(g_mu);
        n = g_defs.size();
    }
    SI("boot generation %llu prepared: %zu game-tree definition(s)", static_cast<unsigned long long>(input.generation), n);
    return kiln::boot::prepared(out, plan, &destroy_boot);
}

void KILN_CALL on_ready(void*) {
    A = static_cast<const KilnAssetsApi*>(g_api->service(kstr(KILN_ASSETS_SERVICE), 1));
    W = static_cast<const KilnWorldApi*>(g_api->service(kstr(KILN_WORLD_SERVICE), 1));
    if (W) {
        H_UNLOCKED = W->component_hash(kstr("UnlockedSkillNodes"));
        H_PLAYERSTATE = W->component_hash(kstr("PlayerState"));
    }
    if (HMODULE m = GetModuleHandleW(L"kiln-content.dll"))
        g_set_texts = reinterpret_cast<SetTextsFn>(GetProcAddress(m, "kc_nc_set_texts"));
    if (!g_set_texts)
        set_reason("node texts: kiln-content.dll does not export kc_nc_set_texts (new nodes show their like node's texts)");
    if (!A || A->header.size < sizeof(KilnAssetsApi)) {
        g_load_on = false;
        set_reason("kiln.assets is unavailable: the game's tree cannot be expanded");
    }
    shadow::ready();
    if (g_api->side() == KILN_SIDE_CLIENT && shadow::level()) {
        KilnHandle h = 0;
        g_api->subscribe_world_tick(g_owner, KILN_WORLD_CLIENT, KILN_TICK_PRE, &on_client_pre, nullptr, &h);
    }
    if (kiln::boot::collect(g_api, g_owner, "skills", &prepare_boot, nullptr)) {
        SI("boot profile: the game-tree definitions are read with the boot generation");
    } else {
        load_boot_definitions();
    }
    size_t n;
    {
        std::lock_guard lock(g_mu);
        n = g_defs.size();
    }
    SI("ready: tree expansion %s, login guard %s, game functions %s, node texts %s, shadow table %s; %zu game-tree "
       "definition(s) at boot",
       g_load_on ? "on" : "OFF", g_guard_on ? "on" : "OFF", g_fns_on ? "on" : "OFF", g_set_texts ? "on" : "off (like-node texts)",
       shadow::level() == 2 ? "on (+ screen)" : shadow::level() == 1 ? "on" : ("OFF: " + shadow::why_off()).c_str(), n);
}

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = kstr(KILN_SKILLS_SERVICE);
    info->version = KilnStr{"1", 1};
    info->on_ready = &on_ready;
    int64_t v = 0;
    if (cst("skills/tree.data", &v)) L.data = v;
    if (cst("skills/tree.guid", &v)) L.guid = v;
    if (cst("skills/tree.node_map", &v)) L.node_map = v;
    if (cst("skills/tree.link_map", &v)) L.link_map = v;
    if (cst("skills/tree.index_map", &v)) L.index_map = v;
    if (cst("skills/tree.ids", &v)) L.ids = v;
    if (cst("skills/tree.roots", &v)) L.roots = v;
    if (cst("skills/login.tree", &v)) L.login_tree = v;
    if (cst("skills/login.unlocked", &v)) L.login_unlocked = v;
    if (cst("skills/login.ui", &v)) L.login_ui = v;
    if (cst("skills/input.unlock", &v)) g_input_unlock = v;
    const bool fns = api->feature(kstr("skills.functions")) && sym("skills/NodeById", &G.node_by_id) &&
                     sym("skills/NodeSet", &G.node_set) && sym("skills/Reachable", &G.reachable) &&
                     sym("skills/UiRebuild", &G.ui_rebuild) && sym("skills/TreeConnected", &G.connected) &&
                     sym("skills/LevelsValid", &G.levels_valid) && sym("skills/PointsSpent", &G.points_spent);
    g_fns_on = fns;
    if (!fns) set_reason("feature skills.functions is off on this build (diagnostics off)");
    void* load = nullptr;
    if (api->feature(kstr("skills.tree_load")) && sym("skills/TreeLoad", &load)) {
        g_load_hook.install(api, owner, reinterpret_cast<uintptr_t>(load), reinterpret_cast<void*>(&hk_tree_load), "skills/TreeLoad");
        g_load_on = static_cast<bool>(g_load_hook);
        if (!g_load_on) set_reason("the skill-tree loader hook did not install");
    } else {
        set_reason("feature skills.tree_load is off on this build: the game's tree cannot be expanded");
    }
    void* login = nullptr;
    if (api->feature(kstr("skills.login_guard")) && sym("skills/LoginCheck", &login) && sym("skills/NodeById", &G.node_by_id) &&
        sym("skills/WriteAccess", &G.write_access)) {
        g_login_hook.install(api, owner, reinterpret_cast<uintptr_t>(login), reinterpret_cast<void*>(&hk_login), "skills/LoginCheck");
        g_guard_on = static_cast<bool>(g_login_hook);
        if (!g_guard_on) set_reason("the login-check hook did not install");
    } else {
        set_reason("feature skills.login_guard is off on this build: unknown saved nodes reset the tree (the game's way)");
    }
    shadow::init(api, owner);
    KilnHandle h = 0;
    api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h);
    api->subscribe_world(owner, &on_world, nullptr, &h);
    return api->register_service(owner, kstr(KILN_SKILLS_SERVICE), &kTable);
}
