// SPDX-License-Identifier: MIT
// kiln.worldtables: the plugin. merge.cpp does the work over bytes; this file finds the mods, picks the moment, serves the results and keeps
// the per-world record of absent mods' materials. native/core/abi/services/kiln_worldtables.h, docs/kiln/services/worldtables.md.
//
// The moment: the game reads these tables once its resource service is up (the renderer resolves its tables at creation, a world takes its
// GUID table when it's made). kiln.assets calls back right after the service comes up, on the game's thread and before any of those reads.
// Everything here is either complete and served, or nothing is: a problem leaves the game's tables untouched, with a line in kiln.log.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <mutex>
#include <set>
#include <string>
#include <vector>

#include "kiln_api.h"
#include "kiln_boot_collector.hpp"
#include "merge.h"
#include "reflect.h"
#include "surfacecheck.h"
#include "terrain.h"
#include "services/kiln_assets.h"
#include "services/kiln_profile.h"
#include "services/kiln_saves.h"
#include "services/kiln_worldtables.h"

namespace kwt {
namespace {

namespace fs = std::filesystem;

constexpr uint32_t kTypeRegistry = 0xc9ce5b84u;   // keen::TerraformingEfficiencyRegistryResource
constexpr uint32_t kTypeWorld = 0x75f48072u;      // keen::VoxelWorldResource
constexpr uint32_t kTypePools = 0x9a2e4101u;      // keen::VoxelBlueprintMaterialPoolRegistryResource
constexpr uint32_t kTypeBlueprints = 0x0dfcca99u; // keen::VoxelBlueprintItemRegistryResource
constexpr uint32_t kTypeItem = 0xb5ce8765u;       // keen::ItemInfo
constexpr uint32_t kTypeVoxelModel = 0xe53afbafu; // keen::VoxelModelResource
constexpr uint32_t kTypeVfx = 0x293464a1u;       // keen::VoxelMaterialVfxDataResource
constexpr uint32_t kTypeBlend2 = 0xdbfc162cu;    // keen::WorldMaterialBlending2Resource (client)
constexpr uint32_t kTypeSimple = 0xebdb76f1u;    // keen::SimpleWorldMaterialResource (client)
constexpr uint32_t kTypeWorkshops = 0xa8ea5924u; // keen::WorkshopRegistryResource: workshops, NPCs and the crafting props recipes can ask for

const KilnApi* g_api = nullptr;
KilnOwner g_self = 0;
const KilnAssetsApi* g_assets = nullptr;
bool g_client = false;
std::string g_test_missing_field;

std::atomic<int32_t> g_state{KILN_WT_OFF};
std::mutex g_mu;  // everything below, once the merge has run it only changes through the world record and define

struct Writer {
    KilnOwner owner;
    KilnWtWriterFn fn;
    void* user;
};
std::vector<Writer> g_writers;
bool g_merge_started = false;

// What the mods say.
std::vector<Tweak> g_tweaks;
std::vector<Material> g_live;
std::vector<Material> g_kept_read;
std::vector<std::string> g_errors;
std::map<std::string, std::string> g_boot_definitions;  // "<mod>/<kind>/<block>" -> the normal form the merge read
std::set<std::string> g_from_code;                       // "<mod>/<block>": read from what code kept in an earlier run
std::map<std::string, fs::path> g_pending_dirs;          // mod id -> its folder of blocks kept from code
std::set<std::string> g_defined;                         // "<mod>/<block>": defined by code this session

// What came of it.
std::vector<Placed> g_placed;             // building materials, then terrain ones (cell < 128): live first in each
size_t g_terrain_room = 0;                // ids for new terrain materials
size_t g_pending_terrain = 0;             // new terrain materials code defined this session
TerrainTables g_terrain_tables;
std::vector<std::string> g_refused;
std::vector<Shape> g_shapes;              // every enabled mod's shapes, then those the shapes table took
std::vector<Shape> g_shapes_in;
std::vector<CraftingProp> g_craft_read;   // every enabled mod's crafting props, as read
std::vector<CraftingProp> g_craft;        // those the game's table took, each with its place
CraftingPropLayout g_craft_layout;
ModelLayout g_model;
bool g_model_ok = false;
std::vector<RegistryChange> g_changes;
std::string g_hash = kEmptyHash;
std::string g_off_reason;
int g_first_new_id = -1;
size_t g_room = 0;
ojson g_tables = ojson::array();
Layouts g_layouts;
ItemLayout g_item_layout;
bool g_layouts_ok = false;
Bytes g_world_game;       // a world resource as the game has it: what a name or GUID resolves against later
size_t g_pending_new = 0; // new materials code defined this session, kept for the next start
std::map<std::string, uint32_t> g_pool_game, g_pool_served;  // the build menu's pools: items the game lists, and what's served
std::map<std::string, size_t> g_pending_pool;                 // pool -> new items code defined this session

// Strings a caller keeps: never freed.
const char* keep(const std::string& s) {
    auto* p = new char[s.size() + 1];
    std::memcpy(p, s.c_str(), s.size() + 1);
    return p;
}
KilnStr keep_str(const std::string& s) { return KilnStr{keep(s), s.size()}; }

void logf(int level, const char* fmt, ...) {
    char buf[2048];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    if (n <= 0 || !g_api) return;
    g_api->log(g_self, level, KilnStr{buf, static_cast<size_t>(n < static_cast<int>(sizeof(buf)) ? n : sizeof(buf) - 1)});
}
#define WT_I(...) logf(KILN_LOG_INFO, __VA_ARGS__)
#define WT_W(...) logf(KILN_LOG_WARN, __VA_ARGS__)
#define WT_E(...) logf(KILN_LOG_ERROR, __VA_ARGS__)

fs::path to_path(const std::string& utf8) { return fs::path(std::u8string(reinterpret_cast<const char8_t*>(utf8.data()), utf8.size())); }
std::string to_utf8(const fs::path& p) {
    const std::u8string s = p.u8string();
    return std::string(reinterpret_cast<const char*>(s.data()), s.size());
}
std::string api_path(int32_t kind) {
    char buf[1024];
    size_t n = 0;
    if (g_api->path(kind, buf, sizeof buf, &n) != KILN_OK || n >= sizeof buf) return "";
    return std::string(buf, n);
}
std::string read_text(const fs::path& p) {
    std::ifstream f(p, std::ios::binary);
    return std::string(std::istreambuf_iterator<char>(f), {});
}
fs::path data_dir() { return to_path(api_path(KILN_PATH_DATA)) / L"worldtables"; }

bool write_atomic(const fs::path& path, const std::string& text) {
    std::error_code ec;
    fs::create_directories(path.parent_path(), ec);
    const fs::path tmp = fs::path(path).concat(L".tmp");
    {
        std::ofstream f(tmp, std::ios::binary);
        if (!f) return false;
        f.write(text.data(), static_cast<std::streamsize>(text.size()));
        if (!f) return false;
    }
    if (!MoveFileExW(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING)) {
        fs::remove(tmp, ec);
        return false;
    }
    return true;
}

// Every mod folder in the load order: the profile's, and kiln/mods when no profile says otherwise. The rule kiln.assets uses for manifests.
std::vector<std::string> mod_folders() {
    std::vector<std::string> out;
    const auto* prof = static_cast<const KilnProfileApi*>(g_api->service(KilnStr{"kiln.profile", 12}, 1));
    bool scan_dropins = true;
    if (prof && prof->header.size >= sizeof(KilnProfileApi)) {
        const uint32_t n = prof->count(KILN_PROFILE_KIND_KILN);
        for (uint32_t i = 0; i < n; ++i) {
            KilnProfileMod m{};
            m.size = sizeof m;
            if (prof->get(KILN_PROFILE_KIND_KILN, i, &m) == KILN_OK && m.enabled) out.emplace_back(m.path.ptr, m.path.len);
        }
        if (prof->mode() == KILN_PROFILE_ACTIVE || prof->mode() == KILN_PROFILE_ONCE) scan_dropins = (prof->dropins() & (1u << KILN_PROFILE_KIND_KILN)) != 0;
    }
    if (scan_dropins) {
        const std::string mods = api_path(KILN_PATH_MODS);
        std::error_code ec;
        std::vector<std::string> found;
        for (auto it = fs::directory_iterator(to_path(mods), ec); !ec && it != fs::directory_iterator(); it.increment(ec)) {
            std::error_code e2;
            if (it->is_directory(e2) && !fs::exists(it->path() / L".disabled", e2)) found.push_back(to_utf8(it->path()));
        }
        std::sort(found.begin(), found.end());
        for (std::string& f : found)
            if (std::find(out.begin(), out.end(), f) == out.end()) out.push_back(std::move(f));
    }
    return out;
}

// ---- what makes a block "the same" for the boot merge: what its tables depend on, not its name or recipe -------------
ojson strength_json(const Strength& s) {
    ojson o = ojson::object();
    if (s.hardness) o["hardness"] = kHardnessJson[*s.hardness];
    if (s.health) o["health"] = *s.health;
    if (s.breaks) o["breaks"] = kBreaksJson[*s.breaks];
    if (s.items_per_blocks) o["itemsPerBlocks"] = *s.items_per_blocks;
    if (s.drops) o["drops"] = *s.drops;
    return o;
}
// A surface in one form whatever the words it was written in (a block from code and its data file say it differently), by kiln.surfaces' reader.
nlohmann::json normal_surface(const nlohmann::json& j, bool tweak) {
    return nlohmann::json::parse(ksf::normal_surface(ksf::ojson::parse(j.dump()), tweak).dump());
}
std::string normal_tweak(const Tweak& t) {
    nlohmann::json j = nlohmann::json::parse(t.definition, nullptr, false);
    nlohmann::json o = nlohmann::json::object();
    o["material"] = t.material;
    o["strength"] = nlohmann::json::parse(strength_json(t.strength).dump());
    if (j.is_object() && j.contains("surface") && !j["surface"].is_null()) o["surface"] = normal_surface(j["surface"], true);
    return o.dump();
}
std::string normal_material(const Material& m) {
    nlohmann::json j = nlohmann::json::parse(m.definition, nullptr, false);
    nlohmann::json o = nlohmann::json::object();
    o["guid"] = m.guid.text();
    o["roof"] = m.roof;
    o["strength"] = nlohmann::json::parse(strength_json(m.strength).dump());
    if (!m.loot_from.empty()) o["loot"] = {{"from", m.loot_from}, {"count", m.loot_count}, {"makes", m.loot_makes}};
    if (j.is_object() && j.contains("surface") && !j["surface"].is_null()) o["surface"] = normal_surface(j["surface"], false);
    if (m.terrain) {
        o["terrain"] = {{"like", m.like}, {"look", m.look}, {"placeable", m.item}};
        if (m.flatten) o["terrain"]["flatten"] = *m.flatten;
        if (m.sliding) o["terrain"]["sliding"] = *m.sliding;
    }
    return o.dump();
}

// ---- the mods' files ---------------------------------------------------------------------------------------------
struct Manifest {
    std::string id, side, clients;
};
bool read_manifest(const fs::path& dir, Manifest* out) {
    std::error_code ec;
    const fs::path f = dir / L"kiln.mod.json";
    if (!fs::exists(f, ec)) return false;
    const ojson j = parse_relaxed(read_text(f));
    if (j.is_discarded() || !j.is_object() || !j.contains("id") || !j["id"].is_string()) {
        WT_W("worldtables: %s: kiln.mod.json has no id: its blocks are skipped", to_utf8(dir).c_str());
        return false;
    }
    out->id = j["id"].get<std::string>();
    out->side = j.value("side", "server");
    out->clients = j.value("clients", out->side == "server" ? "none" : "mod");
    return true;
}

// One mod: its data files, then what its build wrote, then (a boot generation, chapter 08) what its OnBoot staged, then what its code defined
// in an earlier run. A block a later source repeats stays the first's: the data file or the fresh build is newer than a block kept from code.
// `boot`: the generation's prepare input; a mod it excludes is left out whole.
void read_mod(const std::string& dir, const fs::path& pending_root, const kiln::boot::Input* boot = nullptr) {
    const fs::path d = to_path(dir);
    Manifest man;
    if (!read_manifest(d, &man)) return;
    if (!g_client && man.side == "client") return;  // a dedicated server doesn't load it
    if (boot && boot->out(man.id)) {
        WT_I("worldtables: %s is out of the boot generation: its blocks are not read", man.id.c_str());
        return;
    }
    std::vector<std::pair<std::string, fs::path>> files;
    const auto folder = [&](const fs::path& p, const std::string& prefix) {
        std::error_code ec;
        std::vector<fs::path> found;
        for (auto it = fs::directory_iterator(p, ec); !ec && it != fs::directory_iterator(); it.increment(ec))
            if (it->path().extension() == L".json") found.push_back(it->path());
        std::sort(found.begin(), found.end());
        for (const fs::path& f : found) files.emplace_back(prefix + to_utf8(f.filename()), f);
    };
    folder(d / L"blocks", "blocks/");
    std::error_code ec;
    if (fs::exists(d / L"blocks.tables.json", ec)) files.emplace_back("blocks.tables.json", d / L"blocks.tables.json");
    std::vector<std::pair<std::string, ojson>> docs;
    std::map<std::string, ojson> strengths, surfaces;
    const auto add = [&](const std::string& label, ojson doc) {
        docs.emplace_back(label, std::move(doc));
        const ojson& d = docs.back().second;
        if (d.is_discarded() || !d.is_object()) return;
        if (d.contains("strengths") && d["strengths"].is_object())
            for (auto it = d["strengths"].begin(); it != d["strengths"].end(); ++it) strengths.emplace(it.key(), it.value());
        if (d.contains("surfaces") && d["surfaces"].is_object())
            for (auto it = d["surfaces"].begin(); it != d["surfaces"].end(); ++it) surfaces.emplace(it.key(), it.value());
    };
    for (const auto& [label, path] : files) add(label, parse_relaxed(read_text(path)));
    if (boot)
        for (const auto& b : boot->docs("worldtables", man.id)) {
            if (!b.document.is_object())
                throw kiln::boot::Refusal(man.id, "worldtables", b.key, "kiln.worldtables: a boot document is not a blocks file (a JSON object)");
            add("boot code: " + b.key, ojson::parse(b.document.dump(), nullptr, false));
        }
    {
        std::error_code e2;
        std::vector<fs::path> found;
        for (auto it = fs::directory_iterator(pending_root / to_path(man.id), e2); !e2 && it != fs::directory_iterator(); it.increment(e2))
            if (it->path().extension() == L".json") found.push_back(it->path());
        std::sort(found.begin(), found.end());
        for (const fs::path& f : found) add("kept from code: " + to_utf8(f.filename()), parse_relaxed(read_text(f)));
        if (!found.empty()) g_pending_dirs[man.id] = pending_root / to_path(man.id);
    }
    std::set<std::string> seen;
    for (const auto& [label, doc] : docs) {
        ModFile mf;
        mf.mod = man.id;
        mf.dir = dir;
        mf.file = man.id + ": " + label;
        mf.clients = man.clients;
        mf.strengths = strengths;
        mf.surfaces = surfaces;
        if (doc.is_discarded()) {
            g_errors.push_back(mf.file + ": not valid JSON");
            continue;
        }
        if (!doc.is_object() ||
            (!doc.contains("materialTweaks") && !doc.contains("buildingMaterials") && !doc.contains("blueprints") && !doc.contains("terrainMaterials") && !doc.contains("craftingProps") &&
             !doc.contains("props") && !doc.contains("stations") && !doc.contains("containers") && !doc.contains("doors") && !doc.contains("beds") && !doc.contains("seats")))
            continue;
        read_blocks(doc, &mf);
        g_errors.insert(g_errors.end(), mf.errors.begin(), mf.errors.end());
        for (Tweak& t : mf.tweaks) {
            if (!seen.insert("t/" + t.block).second) continue;
            g_boot_definitions[man.id + "/t/" + t.block] = normal_tweak(t);
            if (label.rfind("kept from code: ", 0) == 0) g_from_code.insert(man.id + "/" + t.block);
            g_tweaks.push_back(std::move(t));
        }
        for (Material& m : mf.materials) {
            if (!seen.insert("m/" + m.block).second) continue;
            g_boot_definitions[man.id + "/m/" + m.block] = normal_material(m);
            if (label.rfind("kept from code: ", 0) == 0) g_from_code.insert(man.id + "/" + m.block);
            g_live.push_back(std::move(m));
        }
        for (Shape& sh : mf.shapes) {
            if (!seen.insert("b/" + sh.block).second) continue;
            g_boot_definitions[man.id + "/b/" + sh.block] = sh.definition;
            if (label.rfind("kept from code: ", 0) == 0) g_from_code.insert(man.id + "/" + sh.block);
            g_shapes.push_back(std::move(sh));
        }
        for (CraftingProp& c : mf.craft_props) {
            if (!seen.insert("c/" + c.block).second) continue;
            g_boot_definitions[man.id + "/c/" + c.block] = c.definition;
            if (label.rfind("kept from code: ", 0) == 0) g_from_code.insert(man.id + "/" + c.block);
            g_craft_read.push_back(std::move(c));
        }
    }
}

// The per-world record: every material a world ran with, under kiln/data/worldtables/worlds/<world GUID>.json. At boot it isn't known yet which
// world this session opens, so a material any world remembers stays in the table.
void read_kept() {
    std::error_code ec;
    std::map<std::string, Material> found;
    for (auto it = fs::directory_iterator(data_dir() / L"worlds", ec); !ec && it != fs::directory_iterator(); it.increment(ec)) {
        if (it->path().extension() != L".json") continue;
        const ojson doc = parse_relaxed(read_text(it->path()));
        if (doc.is_discarded() || !doc.is_object() || !doc.contains("materials") || !doc["materials"].is_object()) {
            WT_W("worldtables: %s isn't readable: the materials it remembers aren't kept this session", to_utf8(it->path()).c_str());
            continue;
        }
        for (auto m = doc["materials"].begin(); m != doc["materials"].end(); ++m) {
            Guid g;
            if (!Guid::parse(m.key(), &g) || !m->is_object()) continue;
            Material k;
            k.guid = g;
            k.live = false;
            k.mod = m->value("mod", "?");
            k.block = m->value("block", "?");
            k.roof = m->value("roof", false);
            // a terrain material: what it started from and looked like, by name, so its config and look come back the same
            k.terrain = m->value("kind", "") == "terrain";
            if (k.terrain) {
                k.like = m->value("like", kTerrainLike);
                k.look = m->value("look", "");
                if (m->contains("flatten") && (*m)["flatten"].is_boolean()) k.flatten = (*m)["flatten"].get<bool>();
                if (m->contains("sliding") && (*m)["sliding"].is_boolean()) k.sliding = (*m)["sliding"].get<bool>();
            }
            std::vector<std::string> errors;
            if (m->contains("strength")) read_strength((*m)["strength"], "strength", k.block, &k.strength, &errors);
            found.emplace(m.key(), std::move(k));
        }
    }
    for (auto& [text, k] : found) g_kept_read.push_back(std::move(k));
}

// ---- reading resources through kiln.assets -----------------------------------------------------------------------
std::vector<KilnAssetKey> keys_of(uint32_t type) {
    size_t n = 0;
    g_assets->resource_list(type, nullptr, 0, &n);
    std::vector<KilnAssetKey> keys(n);
    if (n && g_assets->resource_list(type, keys.data(), keys.size(), &n) != KILN_OK) keys.clear();
    return keys;
}
bool bytes_of(const KilnAssetKey& k, Bytes* out) {
    const void* p = nullptr;
    size_t n = 0;
    if (g_assets->resource_data(&k, &p, &n) != KILN_OK || !p) return false;
    out->assign(static_cast<const uint8_t*>(p), static_cast<const uint8_t*>(p) + n);
    return true;
}
uint64_t fnv64(const Bytes& b) {
    uint64_t h = 0xcbf29ce484222325ull;
    for (uint8_t x : b) h = (h ^ x) * 0x100000001b3ull;
    return h;
}
std::string hex64(uint64_t v) {
    char s[17];
    std::snprintf(s, sizeof s, "%016llx", static_cast<unsigned long long>(v));
    return s;
}

struct Output {
    KilnAssetKey key;
    Bytes game, merged;
    std::string name;
};

// ---- names: the items that place each material ---------------------------------------------------------------------
struct GameNames {
    std::map<std::string, int> material;    // material name -> cell id
    std::map<std::string, Guid> item;       // item debug name -> GUID
    bool read = false;
};
GameNames g_names;
const uint8_t* own_base() { return reinterpret_cast<const uint8_t*>(GetModuleHandleW(nullptr)); }
size_t own_size() {
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(own_base());
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(own_base() + dos->e_lfanew);
    return nt->OptionalHeader.SizeOfImage;
}

void read_names() {
    if (g_names.read) return;
    g_names.read = true;
    const ItemLayout& I = g_item_layout;
    for (const KilnAssetKey& k : keys_of(kTypeItem)) {
        const void* p = nullptr;
        size_t n = 0;
        if (g_assets->resource_data(&k, &p, &n) != KILN_OK || !p || n < I.stride) continue;
        const auto* b = static_cast<const uint8_t*>(p);
        int32_t off = 0;
        uint32_t len = 0;
        std::memcpy(&off, b + I.debug_name, 4);
        std::memcpy(&len, b + I.debug_name + 4, 4);
        const int64_t at = int64_t{I.debug_name} + off;
        if (!off || !len || at < 0 || static_cast<uint64_t>(at) + len > n) continue;
        const std::string name(reinterpret_cast<const char*>(b + at), len);
        Guid g;
        std::memcpy(g.b.data(), b + I.object_id, 16);
        g_names.item.emplace(name, g);
        Guid vox;
        std::memcpy(vox.b.data(), b + I.voxel_guid, 16);
        if (vox.zero()) continue;
        const std::string material = material_name(name);
        if (!material.empty()) g_names.material.emplace(material, b[I.voxel_id]);
    }
}

// "Cell143", a material's GUID, or a name: the cell id, or -1.
int resolve_material(const std::string& text, const Bytes& world) {
    if (text.rfind("Cell", 0) == 0 && text.size() > 4) {
        char* end = nullptr;
        const long v = std::strtol(text.c_str() + 4, &end, 10);
        return *end == 0 && v > 0 && v < 256 ? static_cast<int>(v) : -1;
    }
    Guid g;
    if (Guid::parse(text, &g)) {
        for (int id = 1; id < 256; ++id)
            if (std::memcmp(world.data() + g_layouts.world_guids + size_t(id) * 16, g.b.data(), 16) == 0) return id;
        return -1;
    }
    read_names();
    const auto it = g_names.material.find(text);
    return it == g_names.material.end() ? -1 : it->second;
}

bool resolve_drops(Strength* s, const std::string& label) {
    if (!s->drops) return true;
    Guid g;
    if (Guid::parse(*s->drops, &g)) return true;
    read_names();
    const auto it = g_names.item.find(*s->drops);
    if (it == g_names.item.end()) {
        g_errors.push_back(label + ", strength.drops: the game has no item \"" + *s->drops + "\"");
        return false;
    }
    s->drops = it->second.text();
    return true;
}

// What digging a new material gives back. Its strength's drops win; "block" is its own block item, one per block unless it says. Otherwise the game's rule:
// the main input of its recipe at the recipe's ratio (Rough Stone: 2 stone make 100 blocks, and 50 blocks dug give a stone back). With no recipe, or a
// first input that isn't a game item, it's what the material it starts from gives.
bool default_loot(Material* m, const std::string& label) {
    Strength& s = m->strength;
    if (s.drops && *s.drops == kDropsOwnBlock) {
        s.drops = m->item_guid.text();
        if (!s.items_per_blocks) s.items_per_blocks = 1;
        return true;
    }
    if (s.drops || m->loot_from.empty()) return true;
    Guid g;
    bool found = Guid::parse(m->loot_from, &g);
    if (!found) {
        read_names();
        const auto it = g_names.item.find(m->loot_from);
        if (it != g_names.item.end()) {
            g = it->second;
            found = true;
        }
    }
    if (!found) {
        WT_W("worldtables: %s: the first input of its recipe, \"%s\", isn't a game item: digging it gives what the game material it starts from gives. "
             "Say what it gives with strength.drops", label.c_str(), m->loot_from.c_str());
        return true;
    }
    s.drops = g.text();
    if (!s.items_per_blocks) s.items_per_blocks = loot_rate(m->loot_makes, m->loot_count);
    return true;
}

// A terrain material's "like" and "look" as cell ids, and what digging it gives back: its own item when it's placeable and says nothing else, otherwise
// what it starts from gives, unless its strength says. Both have to be game terrain materials with a config.
bool resolve_terrain(Material* m, const std::string& label, const RegistryInfo& info, const Bytes& world, std::vector<std::string>* errors) {
    const auto cell_of = [&](const std::string& name, const char* field, int* out) {
        const int c = resolve_material(name, world);
        if (c >= 1 && c <= kTerrainLast && info.terrain_at.count(c)) {
            *out = c;
            return true;
        }
        errors->push_back(label + ", " + field + ": " + (c < 0 ? "the game has no material \"" + name + "\"" : "\"" + name + "\" is a building material: it takes a terrain material, a GameMaterials Terrain name or Cell<1..127>"));
        return false;
    };
    if (!cell_of(m->like, "like", &m->like_cell)) return false;
    if (m->look.empty()) m->look_cell = m->like_cell;
    else if (!cell_of(m->look, "look", &m->look_cell)) return false;
    Strength& s = m->strength;
    if (m->item && (!s.drops || *s.drops == kDropsOwnBlock)) s.drops = m->item_guid.text();
    if (!s.drops) return true;
    Guid g;
    if (Guid::parse(*s.drops, &g)) return true;
    read_names();
    const auto it = g_names.item.find(*s.drops);
    if (it == g_names.item.end()) {
        errors->push_back(label + ", strength.drops: the game has no item \"" + *s.drops + "\"");
        return false;
    }
    s.drops = it->second.text();
    return true;
}

// ---- the status file ------------------------------------------------------------------------------------------------
const char* state_name(int32_t s) {
    switch (s) {
        case KILN_WT_WAITING: return "waiting";
        case KILN_WT_UNTOUCHED: return "untouched";
        case KILN_WT_MERGED: return "merged";
        default: return "off";
    }
}
std::string status_text() {
    ojson o = ojson::object();
    o["state"] = state_name(g_state.load());
    if (!g_off_reason.empty()) o["why"] = g_off_reason;
    o["hash"] = g_hash;
    o["firstNewId"] = g_first_new_id;
    o["room"] = g_room;
    o["terrainRoom"] = g_terrain_room;
    ojson pools = ojson::array();
    for (const auto& [name, game] : g_pool_game) {
        const auto it = g_pool_served.find(name);
        pools.push_back({{"name", name}, {"game", game}, {"served", it == g_pool_served.end() ? game : it->second}, {"capacity", kPoolCapacity}});
    }
    o["pools"] = std::move(pools);
    ojson mats = ojson::array();
    for (const Placed& p : g_placed) {
        ojson m = ojson::object();
        m["id"] = p.cell;
        m["guid"] = p.m.guid.text();
        m["state"] = p.m.live ? "live" : "kept";
        m["mod"] = p.m.mod;
        m["block"] = p.m.block;
        if (p.reused) m["reused"] = true;
        if (p.m.terrain) m["kind"] = "terrain", m["like"] = p.m.like_cell, m["look"] = p.m.look_cell;
        mats.push_back(std::move(m));
    }
    o["materials"] = std::move(mats);
    ojson tweaks = ojson::array();
    for (const Tweak& t : g_tweaks) {
        ojson m = ojson::object();
        m["mod"] = t.mod;
        m["block"] = t.block;
        m["material"] = t.material;
        m["id"] = t.cell;
        tweaks.push_back(std::move(m));
    }
    o["tweaks"] = std::move(tweaks);
    o["errors"] = g_errors;
    o["refused"] = g_refused;
    ojson shapes = ojson::array();
    for (const Shape& sh : g_shapes_in) {
        ojson m = ojson::object();
        m["mod"] = sh.mod;
        m["block"] = sh.block;
        m["item"] = sh.item.text();
        m["itemId"] = sh.item_id;
        m["size"] = ojson::array({sh.x, sh.y, sh.z});
        m["roof"] = sh.roof;
        shapes.push_back(std::move(m));
    }
    o["shapes"] = std::move(shapes);
    ojson props = ojson::array();
    for (const CraftingProp& c : g_craft) {
        ojson m = ojson::object();
        m["mod"] = c.mod;
        m["block"] = c.block;
        m["guid"] = c.guid.text();
        m["id"] = c.prop_id;
        m["index"] = c.index;
        m["item"] = c.item.text();
        props.push_back(std::move(m));
    }
    o["craftingProps"] = std::move(props);
    o["tables"] = g_tables;
    return o.dump(1);
}
void write_status() {
    if (!write_atomic(data_dir() / L"status.json", status_text())) WT_W("worldtables: can't write status.json");
}

// ---- the merge -------------------------------------------------------------------------------------------------------
void go_off(const std::string& why) {
    g_state.store(KILN_WT_OFF);
    g_off_reason = why;
    g_placed.clear();
    g_pool_served.clear();
    g_hash = kEmptyHash;
    WT_E("worldtables: %s. The game's tables are untouched: material blocks are off this session", why.c_str());
    write_status();
}

void merge_locked() {
    if (!g_layouts_ok) {
        std::string why;
        if (!read_layouts(own_base(), own_size(), g_test_missing_field, &g_layouts, &g_item_layout, &why)) {
            go_off("this game build doesn't resolve: " + why);
            return;
        }
        g_layouts_ok = true;
    }
    const Layouts& L = g_layouts;

    // The game's bytes.
    std::vector<Output> outputs;
    const auto one = [&](uint32_t type, const char* name, Output* o) {
        const std::vector<KilnAssetKey> keys = keys_of(type);
        if (keys.size() != 1) return false;
        o->key = keys[0];
        o->name = name;
        return bytes_of(o->key, &o->game);
    };
    Output reg, pools, blueprints;
    if (!one(kTypeRegistry, "registry", &reg)) {
        go_off("the terraforming registry isn't there");
        return;
    }
    if (!one(kTypePools, "pools", &pools) || !one(kTypeBlueprints, "blueprints", &blueprints)) {
        go_off("the material pools or the blueprint items aren't there");
        return;
    }
    std::vector<Output> worlds;
    for (const KilnAssetKey& k : keys_of(kTypeWorld)) {
        Output o;
        o.key = k;
        o.name = "world";
        if (bytes_of(k, &o.game) && world_has_table(o.game, L)) worlds.push_back(std::move(o));
    }
    if (worlds.empty()) {
        go_off("no world resource has a material table");
        return;
    }
    for (const Output& w : worlds)
        if (std::memcmp(w.game.data() + L.world_guids, worlds[0].game.data() + L.world_guids, 256 * 16) != 0) {
            go_off("the game's world resources disagree on their material tables");
            return;
        }

    g_world_game = worlds[0].game;
    RegistryInfo info;
    std::string why;
    if (!read_registry(reg.game, L, &info, &why)) {
        go_off(why);
        return;
    }
    std::vector<int> free_ids;
    for (int id : free_building_ids(worlds[0].game, L))
        if (!info.building_at.count(id)) free_ids.push_back(id);
    const std::vector<int> deprecated = deprecated_ids(reg.game, info, L);
    g_first_new_id = free_ids.empty() ? -1 : free_ids[0];

    // Names to ids. What doesn't resolve is that block's problem; the others go on.
    {
        std::vector<Tweak> ok;
        for (Tweak& t : g_tweaks) {
            const std::string label = "block materialTweak " + t.block + " (mod " + t.mod + ")";
            t.cell = resolve_material(t.material, worlds[0].game);
            if (t.cell < 0) {
                g_errors.push_back(label + ", material: the game has no material \"" + t.material + "\"");
                continue;
            }
            if (t.strength.drops && *t.strength.drops == kDropsOwnBlock) {
                g_errors.push_back(label + ", strength.drops: \"block\" is a new material's own block item; a game material's takes an item by name");
                continue;
            }
            if (!resolve_drops(&t.strength, label)) continue;
            ok.push_back(std::move(t));
        }
        g_tweaks = std::move(ok);
        std::vector<Material> live;
        std::set<Guid> seen;
        for (Material& m : g_live) {
            const std::string label = std::string(m.terrain ? "block terrainMaterial " : "block buildingMaterial ") + m.block + " (mod " + m.mod + ")";
            if (!seen.insert(m.guid).second) {
                g_errors.push_back(label + ": another block has its GUID: a \"was\" that names another block?");
                continue;
            }
            if (m.terrain) {
                if (resolve_terrain(&m, label, info, worlds[0].game, &g_errors)) live.push_back(std::move(m));
                continue;
            }
            if (!default_loot(&m, label) || !resolve_drops(&m.strength, label)) continue;
            live.push_back(std::move(m));
        }
        g_live = std::move(live);
    }
    // Two mods setting one field of one material: load order decides, and it's worth a line.
    {
        std::map<std::pair<int, std::string>, std::string> who;
        for (const Tweak& t : g_tweaks)
            for (auto [field, set] : std::initializer_list<std::pair<const char*, bool>>{{"hardness", t.strength.hardness.has_value()},
                                                                                         {"health", t.strength.health.has_value()},
                                                                                         {"breaks", t.strength.breaks.has_value()},
                                                                                         {"itemsPerBlocks", t.strength.items_per_blocks.has_value()},
                                                                                         {"drops", t.strength.drops.has_value()}}) {
                if (!set) continue;
                auto& prev = who[{t.cell, field}];
                if (!prev.empty()) WT_W("worldtables: %s and %s both set %s of %s: the later in the load order wins", prev.c_str(), (t.mod + ":" + t.block).c_str(), field, t.material.c_str());
                prev = t.mod + ":" + t.block;
            }
    }

    // The build menu's pools: 64 items each in a player's game, the game's own among them. A material whose item has no room left there is refused
    // by name before it takes an id; one a world has cells of is kept as an absent mod's is.
    if (!pool_counts(pools.game, L, &g_pool_game, &why)) {
        go_off(why);
        return;
    }
    std::vector<Material> held;
    std::vector<std::string> pool_refused;
    {
        std::set<Guid> remembered;
        for (const Material& k : g_kept_read) remembered.insert(k.guid);
        fit_pools(&g_live, g_pool_game, remembered, &held, &pool_refused);
    }
    std::vector<Material> kept;
    {
        std::set<Guid> live;
        for (const Material& m : g_live) live.insert(m.guid);
        for (const Material& h : held) live.insert(h.guid);
        for (const Material& k : g_kept_read)
            if (!live.count(k.guid)) kept.push_back(k);
        for (Material& h : held) kept.push_back(std::move(h));
    }
    // Terrain ids are a room of their own: a cell's low byte 1..127 against 128..255.
    std::vector<Material> tlive, tkept;
    {
        const auto split = [](std::vector<Material>* all, std::vector<Material>* terrain) {
            std::vector<Material> rest;
            for (Material& m : *all) (m.terrain ? *terrain : rest).push_back(std::move(m));
            *all = std::move(rest);
        };
        split(&g_live, &tlive);
        split(&kept, &tkept);
        // a kept one keeps its config and look by name; a name this build lacks falls back to plain stone rather than losing the cells
        std::vector<std::string> quiet;
        for (Material& k : tkept)
            if (!resolve_terrain(&k, "kept terrain material " + k.block, info, worlds[0].game, &quiet)) {
                k.like = kTerrainLike, k.look.clear(), k.strength.drops.reset();
                resolve_terrain(&k, k.block, info, worlds[0].game, &quiet);
            }
    }
    Allocation a = allocate(g_live, kept, free_ids, deprecated);
    g_room = a.room;
    g_refused = std::move(pool_refused);
    g_refused.insert(g_refused.end(), a.refused.begin(), a.refused.end());
    g_placed = std::move(a.placed);
    Allocation ta = allocate_terrain(tlive, tkept, free_terrain_ids(worlds[0].game, L, info));
    g_terrain_room = ta.room;
    g_refused.insert(g_refused.end(), ta.refused.begin(), ta.refused.end());
    for (const std::string& r : g_refused) WT_E("worldtables: %s", r.c_str());

    // The new bytes. Anything that doesn't read leaves everything as the game has it.
    std::vector<RegistryChange> changes;
    if (!merge_registry(reg.game, L, g_tweaks, g_placed, &reg.merged, &changes, &why)) {
        go_off(why);
        return;
    }
    if (!merge_pools(pools.game, L, g_placed, &pools.merged, &why)) {
        go_off(why);
        return;
    }
    if (!ta.placed.empty()) {
        Bytes next;
        if (!merge_terrain_registry(reg.merged, L, ta.placed, &next, &changes, &why)) {
            go_off(why);
            return;
        }
        reg.merged = std::move(next);
        if (!merge_terrain_pool(pools.merged, L, ta.placed, &next, &why)) {
            go_off(why);
            return;
        }
        pools.merged = std::move(next);
    }
    if (!pool_counts(pools.merged, L, &g_pool_served, &why)) {
        go_off(why);
        return;
    }
    if (!merge_blueprints(blueprints.game, L, g_placed, first_pool_item(pools.game, L), &blueprints.merged, &why)) {
        go_off(why);
        return;
    }
    // shapes of the mods' own: their entries after the block items', each with its own cells
    if (!g_shapes.empty()) {
        std::vector<std::string> refused;
        Bytes with_shapes;
        if (!merge_shapes(blueprints.merged, L, g_shapes, &with_shapes, &refused, &why)) {
            go_off(why);
            return;
        }
        blueprints.merged = std::move(with_shapes);
        for (const std::string& r : refused) g_refused.push_back(r);
        for (const Shape& sh : g_shapes)
            if (std::none_of(refused.begin(), refused.end(), [&](const std::string& r) { return r.rfind("block blueprint " + sh.block + " (mod " + sh.mod + ")", 0) == 0; }))
                g_shapes_in.push_back(sh);
    }
    // The crafting props recipes can ask to stand near: the next free places of the game's table of them. A build that doesn't resolve it leaves them out and keeps the rest.
    Output workshops;
    bool craft_merged = false;
    {
        std::string cwhy;
        g_craft.clear();
        if (!g_craft_read.empty()) {
            uint64_t used = 0;
            std::set<uint32_t> ids;
            if (!g_craft_layout.ok && !read_crafting_prop_layout(own_base(), own_size(), &g_craft_layout, &cwhy)) {
                WT_W("worldtables: this build's crafting prop table doesn't resolve (%s): crafting props are left out this session", cwhy.c_str());
            } else if (!one(kTypeWorkshops, "crafting props", &workshops)) {
                WT_W("worldtables: the game's workshop registry isn't there: crafting props are left out this session");
            } else if (!read_crafting_prop_table(workshops.game, g_craft_layout, &used, &ids, &cwhy)) {
                WT_W("worldtables: %s: crafting props are left out this session", cwhy.c_str());
            } else {
                std::vector<CraftingProp> mine;
                for (CraftingProp& c : g_craft_read) {
                    if (ids.count(c.prop_id)) {
                        g_errors.push_back("block craftingProp " + c.block + " (mod " + c.mod + "): its id is one of the game's crafting props");
                        continue;
                    }
                    mine.push_back(c);
                }
                std::vector<std::string> over;
                allocate_crafting_props(&mine, used, &over);
                for (const std::string& r : over) {
                    g_refused.push_back(r);
                    WT_E("worldtables: %s", r.c_str());
                }
                if (merge_crafting_props(workshops.game, g_craft_layout, mine, &workshops.merged, &cwhy)) {
                    g_craft = std::move(mine);
                    craft_merged = true;
                } else {
                    WT_W("worldtables: %s: crafting props are left out this session", cwhy.c_str());
                }
            }
        }
    }
    const size_t building_n = g_placed.size();
    g_placed.insert(g_placed.end(), ta.placed.begin(), ta.placed.end());
    for (Output& w : worlds)
        if (!merge_world(w.game, L, g_placed, &w.merged, &why)) {
            go_off(why);
            return;
        }
    outputs.push_back(std::move(reg));
    outputs.push_back(std::move(pools));
    outputs.push_back(std::move(blueprints));
    for (Output& w : worlds) outputs.push_back(std::move(w));
    if (craft_merged) outputs.push_back(std::move(workshops));
    // A terrain material's particle class (both sides) and look (a client's tables): the entries of the game material it looks like. A table that
    // doesn't read costs that much: the material still digs and saves.
    if (!ta.placed.empty()) {
        std::string twhy;
        if (!g_terrain_tables.ok && !read_terrain_tables(own_base(), own_size(), &g_terrain_tables, &twhy)) {
            WT_W("worldtables: this build's terrain look tables don't resolve (%s): new terrain materials dig and save, and get no look or particles", twhy.c_str());
        } else {
            using Fn = bool (*)(const Bytes&, const TerrainTables&, const std::vector<Placed>&, Bytes*, std::string*);
            for (const auto& [type, name, fn] : {std::tuple<uint32_t, const char*, Fn>{kTypeVfx, "particle", &merge_terrain_vfx},
                                                 {kTypeBlend2, "terrain blending", &merge_terrain_blend},
                                                 {kTypeSimple, "far terrain", &merge_terrain_simple}}) {
                Output o;
                if (!one(type, name, &o)) continue;  // a dedicated server has no look tables
                if (!fn(o.game, g_terrain_tables, ta.placed, &o.merged, &twhy)) {
                    WT_W("worldtables: %s; new terrain materials get no entry there", twhy.c_str());
                    continue;
                }
                outputs.push_back(std::move(o));
            }
        }
    }

    // Served as a whole or not at all.
    bool any = false;
    std::vector<const Output*> put;
    for (const Output& o : outputs) {
        if (o.merged == o.game) continue;
        if (g_assets->resource_put(g_self, &o.key, o.merged.data(), o.merged.size(), KILN_ASSET_PUT_OVERRIDE) != KILN_OK) {
            for (const Output* p : put) g_assets->resource_remove(g_self, &p->key);
            go_off("kiln.assets refused the " + o.name + " table");
            return;
        }
        put.push_back(&o);
        any = true;
    }
    g_tables = ojson::array();
    for (const Output& o : outputs) {
        ojson t = ojson::object();
        t["name"] = o.name;
        t["gameBytes"] = o.game.size();
        t["gameFnv"] = hex64(fnv64(o.game));
        t["servedBytes"] = o.merged.size();
        t["servedFnv"] = hex64(fnv64(o.merged));
        t["identical"] = o.merged == o.game;
        g_tables.push_back(std::move(t));
    }
    // the client draws a shape's preview from a model of the item's own GUID; a dedicated server has it too, for the item's reference
    size_t previews = 0;
    if (!g_shapes_in.empty()) {
        std::string mwhy;
        g_model_ok = g_model_ok || read_model_layout(own_base(), own_size(), &g_model, &mwhy);
        for (const Shape& sh : g_shapes_in) {
            if (!g_model_ok) break;
            const Bytes model = voxel_model(sh, g_model);
            KilnAssetKey k{};
            std::memcpy(k.guid, sh.item.b.data(), 16);
            k.type = kTypeVoxelModel;
            if (g_assets->resource_put(g_self, &k, model.data(), model.size(), KILN_ASSET_PUT_NEW) == KILN_OK) ++previews;
            else WT_W("worldtables: shape %s (mod %s): kiln.assets refused its preview model: the client shows the shape it starts from under the cursor", sh.block.c_str(), sh.mod.c_str());
        }
        if (!g_model_ok) WT_W("worldtables: this build's preview models don't read (%s): shapes place, and the client shows the shape they start from under the cursor", mwhy.c_str());
        for (const Shape& sh : g_shapes_in)
            WT_I("worldtables: shape %s (mod %s): item %s (0x%08x), %ux%ux%u cells%s", sh.block.c_str(), sh.mod.c_str(), sh.item.text().c_str(), sh.item_id, sh.x, sh.y, sh.z, sh.roof ? ", roof" : "");
    }
    g_changes = std::move(changes);
    g_hash = handshake_hash(g_placed, g_craft);
    g_state.store(any ? KILN_WT_MERGED : KILN_WT_UNTOUCHED);

    for (const RegistryChange& c : g_changes) WT_I("worldtables: %s", c.text.c_str());
    for (const Placed& p : g_placed)
        if (p.reused) WT_W("worldtables: material %s took the game's deprecated slot %d: a world with cells of the deprecated material now shows this one", p.m.block.c_str(), p.cell);
    size_t live_n = 0, tlive_n = 0;
    for (size_t i = 0; i < g_placed.size(); ++i) (i < building_n ? live_n : tlive_n) += g_placed[i].m.live ? 1 : 0;
    if (any) {
        WT_I("worldtables: merged: %zu tweak(s), %zu live and %zu kept material(s) of room for %zu, first new id %d, %zu table(s) served; hash %s", g_tweaks.size(), live_n, building_n - live_n, g_room, g_first_new_id,
             put.size(), g_hash.c_str());
        std::string menu;
        for (const char* p : {kWallPool, kRoofPool, kTerrainPool}) {
            const uint32_t game = g_pool_game.count(p) ? g_pool_game.at(p) : 0, served = g_pool_served.count(p) ? g_pool_served.at(p) : 0;
            menu += (menu.empty() ? "" : ", ") + std::string(p) + " " + std::to_string(game) + " + " + std::to_string(served > game ? served - game : 0) + " of " + std::to_string(kPoolCapacity);
        }
        WT_I("worldtables: build menu pools: %s", menu.c_str());
    }
    if (g_placed.size() > building_n)
        WT_I("worldtables: terrain: %zu live and %zu kept material(s) of room for %zu", tlive_n, g_placed.size() - building_n - tlive_n, g_terrain_room);
    else
        WT_I("worldtables: nothing to change: the game's %zu tables are served as they are (registry fnv %s); hash %s", outputs.size(), hex64(fnv64(outputs[0].game)).c_str(), g_hash.c_str());
}

const KilnWorldTablesApi* table();

void KILN_CALL on_resources_ready(void*) {
    std::vector<Writer> writers;
    {
        std::lock_guard lock(g_mu);
        if (g_merge_started) return;
        g_merge_started = true;
        try {
            merge_locked();
        } catch (const std::exception& e) {
            go_off(std::string("the merge failed: ") + e.what());
        }
        for (const std::string& e : g_errors) WT_E("worldtables: %s", e.c_str());
        write_status();
        if (g_state.load() != KILN_WT_OFF) writers = g_writers;
    }
    for (const Writer& w : writers) w.fn(table(), w.user);
}

// ---- the per-world record --------------------------------------------------------------------------------------------
const KilnSavesApi* g_saves = nullptr;
std::atomic<bool> g_world_recorded{false};

std::string guid_or_default(const KilnSavesStatus& s) {
    if (!s.has_guid) return "default";
    Guid g;
    std::memcpy(g.b.data(), s.world_guid, 16);
    return g.zero() ? "default" : g.text();
}

void record_world(const std::string& world) {
    std::lock_guard lock(g_mu);
    bool any = false;
    for (const Placed& p : g_placed) any = any || p.m.live;
    if (!any) return;
    const fs::path file = data_dir() / L"worlds" / to_path(world + ".json");
    std::error_code ec;
    ojson doc = fs::exists(file, ec) ? parse_relaxed(read_text(file)) : ojson::object();
    if (doc.is_discarded() || !doc.is_object()) doc = ojson::object();
    doc["format"] = 1;
    doc["world"] = world;
    if (!doc.contains("materials") || !doc["materials"].is_object()) doc["materials"] = ojson::object();
    for (const Placed& p : g_placed) {
        if (!p.m.live) continue;
        ojson m = ojson::object();
        m["mod"] = p.m.mod;
        m["block"] = p.m.block;
        m["id"] = p.cell;
        m["roof"] = p.m.roof;
        if (p.m.terrain) {
            m["kind"] = "terrain";
            m["like"] = p.m.like;
            if (!p.m.look.empty()) m["look"] = p.m.look;
            if (p.m.flatten) m["flatten"] = *p.m.flatten;
            if (p.m.sliding) m["sliding"] = *p.m.sliding;
        }
        Strength kept = p.m.strength;
        if (kept.drops && *kept.drops == p.m.item_guid.text()) kept.drops.reset(), kept.items_per_blocks.reset();  // a kept material has no block item
        m["strength"] = strength_json(kept);
        doc["materials"][p.m.guid.text()] = std::move(m);
    }
    if (write_atomic(file, doc.dump(1))) WT_I("worldtables: world %s remembers %zu material(s)", world.c_str(), doc["materials"].size());
    else WT_W("worldtables: can't write the record of world %s: its materials won't be kept if their mods go", world.c_str());
}

// Blocks kept from code that the code didn't define this session go from the next start. By the time the world runs every mod's OnLoad has run; a
// block a mod defines later is kept again when it's defined (define_json), so it never drops out for good.
void prune_pending() {
    std::lock_guard lock(g_mu);
    for (const auto& [mod, dir] : g_pending_dirs) {
        std::error_code ec;
        std::vector<fs::path> gone;
        for (auto it = fs::directory_iterator(dir, ec); !ec && it != fs::directory_iterator(); it.increment(ec))
            if (it->path().extension() == L".json" && !g_defined.count(mod + "/" + to_utf8(it->path().stem()))) gone.push_back(it->path());
        for (const fs::path& p : gone) {
            std::error_code e2;
            fs::remove(p, e2);
            WT_I("worldtables: %s: the code no longer defines %s: it's gone from the next start", mod.c_str(), to_utf8(p.stem()).c_str());
        }
    }
}

void KILN_CALL on_tick(const KilnTickInfo* tick, void*) {
    if (g_world_recorded.load(std::memory_order_relaxed) || (tick->step & 63) != 7) return;
    if (g_saves) {
        KilnSavesStatus s{};
        s.size = sizeof s;
        if (g_saves->status(&s) != KILN_OK || s.state < KILN_SAVES_STATE_RUNNING) return;
        g_world_recorded.store(true);
        record_world(guid_or_default(s));
    } else {
        g_world_recorded.store(true);
        record_world("default");
    }
    static bool pruned = false;
    if (!pruned) {
        pruned = true;
        prune_pending();
    }
}

void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    if (ev && ev->event == KILN_WORLD_STOPPING && ev->kind == KILN_WORLD_SERVER) g_world_recorded.store(false);
}

// ---- the table -------------------------------------------------------------------------------------------------------
int32_t KILN_CALL t_state() { return g_state.load(); }

// Set once by the merge; a caller keeps the pointer for the life of the process.
KilnStr KILN_CALL t_hash() {
    static const char* kept = nullptr;
    static size_t kept_for = SIZE_MAX;
    std::lock_guard lock(g_mu);
    if (kept_for != std::hash<std::string>{}(g_hash)) {
        kept = keep(g_hash);
        kept_for = std::hash<std::string>{}(g_hash);
    }
    return KilnStr{kept, g_hash.size()};
}

uint32_t KILN_CALL t_count() {
    std::lock_guard lock(g_mu);
    return static_cast<uint32_t>(g_placed.size());
}

KilnResult KILN_CALL t_material_at(uint32_t i, KilnWtMaterial* out) {
    if (!out || out->size < sizeof(KilnWtMaterial)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    if (i >= g_placed.size()) return KILN_ERR_NOT_FOUND;
    const Placed& p = g_placed[i];
    out->state = p.m.live ? KILN_WT_LIVE : KILN_WT_KEPT;
    std::memcpy(out->guid, p.m.guid.b.data(), 16);
    out->cell_id = static_cast<uint32_t>(p.cell);
    out->reused = p.reused ? 1 : 0;
    out->item_id = p.m.live && p.m.item ? p.m.item_id : 0;
    static std::vector<std::array<KilnStr, 4>> strings;
    if (strings.size() != g_placed.size()) {
        strings.clear();
        for (const Placed& q : g_placed) strings.push_back({keep_str(q.m.mod), keep_str(q.m.block), keep_str(q.m.dir), keep_str(q.m.definition)});
    }
    out->mod = strings[i][0];
    out->block = strings[i][1];
    out->mod_dir = strings[i][2];
    out->definition = strings[i][3];
    return KILN_OK;
}

int32_t KILN_CALL t_material_id(const uint8_t guid[16]) {
    if (!guid) return -1;
    std::lock_guard lock(g_mu);
    for (const Placed& p : g_placed)
        if (std::memcmp(p.m.guid.b.data(), guid, 16) == 0) return p.cell;
    return -1;
}

int32_t KILN_CALL t_first_new_id() {
    std::lock_guard lock(g_mu);
    return g_first_new_id;
}

KilnResult KILN_CALL t_add_writer(KilnOwner owner, KilnWtWriterFn fn, void* user) {
    if (!fn) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    if (g_merge_started) return KILN_ERR_UNAVAILABLE;
    g_writers.push_back({owner, fn, user});
    return KILN_OK;
}

KilnResult give_text(const std::string& text, char* buf, size_t cap, size_t* out_len, KilnResult rc = KILN_OK) {
    if (out_len) *out_len = text.size();
    if (!buf || cap <= text.size()) return cap && buf ? KILN_ERR_BUFFER_TOO_SMALL : (rc == KILN_OK ? KILN_ERR_BUFFER_TOO_SMALL : rc);
    std::memcpy(buf, text.data(), text.size());
    buf[text.size()] = 0;
    return rc;
}

KilnResult KILN_CALL t_status(char* buf, size_t cap, size_t* out_len) {
    std::lock_guard lock(g_mu);
    return give_text(status_text(), buf, cap, out_len);
}

KilnResult KILN_CALL t_define(KilnStr json, char* report, size_t cap, size_t* out_len) {
    KilnResult rc = KILN_OK;
    ojson result = ojson::object();
    ojson errors = ojson::array();
    ojson blocks = ojson::array();
    try {
        const ojson doc = parse_relaxed(std::string(json.ptr, json.len));
        if (doc.is_discarded() || !doc.is_object() || !doc.contains("mod") || !doc["mod"].is_string()) {
            errors.push_back("expected {\"format\": 1, \"mod\": id, \"dir\": folder, \"materialTweaks\": {...}, \"buildingMaterials\": {...}, \"blueprints\": {...}}");
        } else {
            ModFile mf;
            mf.mod = doc["mod"].get<std::string>();
            mf.dir = doc.value("dir", "");
            mf.file = "code";
            mf.clients = doc.value("clients", "mod");
            read_blocks(doc, &mf);
            for (const std::string& e : mf.errors) errors.push_back(e);

            std::lock_guard lock(g_mu);
            const fs::path pending = data_dir() / L"pending" / to_path(mf.mod);
            const auto keep_block = [&](const char* section, const std::string& name) {
                ojson one = ojson::object();
                one["format"] = 1;
                one[section] = ojson::object();
                one[section][name] = doc[section][name];
                if (!write_atomic(pending / to_path(name + ".json"), one.dump(1))) WT_W("worldtables: %s:%s: can't keep it for the next start", mf.mod.c_str(), name.c_str());
            };
            for (const Tweak& t : mf.tweaks) g_defined.insert(mf.mod + "/" + t.block);
            for (const Material& m : mf.materials) g_defined.insert(mf.mod + "/" + m.block);
            for (const Shape& sh : mf.shapes) g_defined.insert(mf.mod + "/" + sh.block);
            for (const CraftingProp& c : mf.craft_props) g_defined.insert(mf.mod + "/" + c.block);
            const std::string off = state_name(g_state.load());
            const bool usable = g_state.load() != KILN_WT_OFF;
            for (const Tweak& t : mf.tweaks) {
                ojson b = ojson::object();
                b["name"] = t.block;
                b["kind"] = "materialTweak";
                const auto it = g_boot_definitions.find(mf.mod + "/t/" + t.block);
                if (!usable) b["status"] = off;
                else if (it != g_boot_definitions.end() && it->second == normal_tweak(t)) {
                    b["status"] = "registered";
                    if (g_from_code.count(mf.mod + "/" + t.block)) keep_block("materialTweaks", t.block);
                } else {
                    b["status"] = "pending";
                    if (g_layouts_ok && !g_world_game.empty() && resolve_material(t.material, g_world_game) < 0) {
                        errors.push_back("code: block materialTweak " + t.block + ", material: the game has no material \"" + t.material + "\"");
                        continue;
                    }
                    keep_block("materialTweaks", t.block);
                }
                for (const Tweak& have : g_tweaks)
                    if (have.mod == mf.mod && have.block == t.block) b["id"] = have.cell;
                blocks.push_back(std::move(b));
            }
            for (const Material& m : mf.materials) {
                // a building or a terrain material: the same path, each with its own section and room
                const char* kind = m.terrain ? "terrainMaterial" : "buildingMaterial";
                const char* section = m.terrain ? "terrainMaterials" : "buildingMaterials";
                const size_t room = m.terrain ? g_terrain_room : g_room;
                size_t& pending_n = m.terrain ? g_pending_terrain : g_pending_new;
                const size_t taken = pending_n + static_cast<size_t>(std::count_if(g_placed.begin(), g_placed.end(), [&](const Placed& p) { return p.m.terrain == m.terrain; }));
                ojson b = ojson::object();
                b["name"] = m.block;
                b["kind"] = kind;
                b["guid"] = m.guid.text();
                const auto it = g_boot_definitions.find(mf.mod + "/m/" + m.block);
                if (!usable) b["status"] = off;
                else if (it != g_boot_definitions.end() && it->second == normal_material(m)) {
                    b["status"] = "registered";
                    if (g_from_code.count(mf.mod + "/" + m.block)) keep_block(section, m.block);
                } else {
                    b["status"] = "pending";
                    // a terrain material's like and look are checked now, as a tweak's material is, rather than at the next start
                    if (m.terrain && g_layouts_ok && !g_world_game.empty()) {
                        bool bad = false;
                        for (const auto& [field, name] : {std::pair<const char*, std::string>{"like", m.like}, {"look", m.look}}) {
                            if (name.empty()) continue;
                            const int c = resolve_material(name, g_world_game);
                            if (c >= 1 && c <= kTerrainLast) continue;
                            errors.push_back("code: block terrainMaterial " + m.block + ", " + field + ": " +
                                             (c < 0 ? "the game has no material \"" + name + "\"" : "\"" + name + "\" is a building material: it takes a terrain material, a GameMaterials Terrain name or Cell<1..127>"));
                            bad = true;
                        }
                        if (bad) continue;
                    }
                    if (room && taken >= room) {
                        char text[160];
                        std::snprintf(text, sizeof text, "the game has room for %zu new %s materials across all mods, and %zu are taken", room, m.terrain ? "terrain" : "building", taken);
                        errors.push_back(std::string("code: block ") + kind + " " + m.block + ": refused: " + text);
                        continue;
                    }
                    // its item's room in the build menu's pool, as at the next start
                    const std::string pool = pool_of(m);
                    if (!pool.empty() && !g_pool_game.empty()) {
                        const uint32_t have = g_pool_game.count(pool) ? g_pool_game.at(pool) : 0;
                        const size_t pool_room = have < kPoolCapacity ? kPoolCapacity - have : 0;
                        const size_t pool_taken = g_pending_pool[pool] + static_cast<size_t>(std::count_if(g_placed.begin(), g_placed.end(), [&](const Placed& p) {
                                                      return p.m.live && pool_of(p.m) == pool;
                                                  }));
                        if (pool_taken >= pool_room) {
                            errors.push_back(std::string("code: block ") + kind + " " + m.block + ": refused: " + pool_room_text(pool, have) + ", and " + std::to_string(pool_taken) +
                                             " are taken");
                            continue;
                        }
                        ++g_pending_pool[pool];
                    }
                    ++pending_n;
                    keep_block(section, m.block);
                }
                for (const Placed& p : g_placed)
                    if (p.m.live && p.m.guid == m.guid) b["id"] = p.cell;
                for (const std::string& r : g_refused)
                    if (r.find(std::string("block ") + kind + " " + m.block + " (mod " + mf.mod + ")") == 0) errors.push_back(r);
                blocks.push_back(std::move(b));
            }
            for (const Shape& sh : mf.shapes) {
                ojson b = ojson::object();
                b["name"] = sh.block;
                b["kind"] = "blueprint";
                b["item"] = sh.item.text();
                const auto it = g_boot_definitions.find(mf.mod + "/b/" + sh.block);
                const bool in = std::any_of(g_shapes_in.begin(), g_shapes_in.end(), [&](const Shape& x) { return x.mod == mf.mod && x.block == sh.block; });
                if (!usable) b["status"] = off;
                else if (it != g_boot_definitions.end() && it->second == sh.definition && in) {
                    b["status"] = "registered";
                    if (g_from_code.count(mf.mod + "/" + sh.block)) keep_block("blueprints", sh.block);
                } else {
                    b["status"] = "pending";
                    keep_block("blueprints", sh.block);
                }
                for (const std::string& r : g_refused)
                    if (r.find("block blueprint " + sh.block + " (mod " + mf.mod + ")") == 0) errors.push_back(r);
                blocks.push_back(std::move(b));
            }
            for (const CraftingProp& c : mf.craft_props) {
                ojson b = ojson::object();
                b["name"] = c.block;
                b["kind"] = "craftingProp";
                b["guid"] = c.guid.text();
                b["id"] = c.prop_id;
                const auto it = g_boot_definitions.find(mf.mod + "/c/" + c.block);
                const auto in = std::find_if(g_craft.begin(), g_craft.end(), [&](const CraftingProp& x) { return x.guid == c.guid; });
                if (!usable) b["status"] = off;
                else if (it != g_boot_definitions.end() && it->second == c.definition && in != g_craft.end()) {
                    b["status"] = "registered";
                    b["index"] = in->index;
                    if (g_from_code.count(mf.mod + "/" + c.block)) keep_block("craftingProps", c.block);
                } else {
                    b["status"] = "pending";
                    keep_block("craftingProps", c.block);
                }
                for (const std::string& r : g_refused)
                    if (r.find("block craftingProp " + c.block + " (mod " + mf.mod + ")") == 0) errors.push_back(r);
                blocks.push_back(std::move(b));
            }
        }
    } catch (const std::exception& e) {
        errors.push_back(std::string("internal: ") + e.what());
        rc = KILN_ERR_INTERNAL;
    }
    if (!errors.empty() && rc == KILN_OK) rc = KILN_ERR_INVALID_ARGUMENT;
    result["ok"] = errors.empty();
    result["errors"] = std::move(errors);
    result["blocks"] = std::move(blocks);
    return give_text(result.dump(), report, cap, out_len, rc);
}

KilnResult KILN_CALL t_prune(KilnStr mod, KilnStr names) {
    const ojson list = parse_relaxed(std::string(names.ptr, names.len));
    if (list.is_discarded() || !list.is_array()) return KILN_ERR_INVALID_ARGUMENT;
    std::set<std::string> keep_names;
    for (const ojson& n : list)
        if (n.is_string()) keep_names.insert(n.get<std::string>());
    std::lock_guard lock(g_mu);
    const fs::path dir = data_dir() / L"pending" / to_path(std::string(mod.ptr, mod.len));
    std::error_code ec;
    std::vector<fs::path> gone;
    for (auto it = fs::directory_iterator(dir, ec); !ec && it != fs::directory_iterator(); it.increment(ec))
        if (it->path().extension() == L".json" && !keep_names.count(to_utf8(it->path().stem()))) gone.push_back(it->path());
    for (const fs::path& p : gone) {
        std::error_code e2;
        fs::remove(p, e2);
        WT_I("worldtables: %.*s: the code no longer defines %s: it's gone from the next start", static_cast<int>(mod.len), mod.ptr, to_utf8(p.stem()).c_str());
    }
    return KILN_OK;
}

KilnResult KILN_CALL t_forget(const uint8_t guid[16]) {
    if (!guid) return KILN_ERR_INVALID_ARGUMENT;
    Guid g;
    std::memcpy(g.b.data(), guid, 16);
    const std::string text = g.text();
    std::lock_guard lock(g_mu);
    bool found = false;
    std::error_code ec;
    for (auto it = fs::directory_iterator(data_dir() / L"worlds", ec); !ec && it != fs::directory_iterator(); it.increment(ec)) {
        if (it->path().extension() != L".json") continue;
        ojson doc = parse_relaxed(read_text(it->path()));
        if (doc.is_discarded() || !doc.is_object() || !doc.contains("materials") || !doc["materials"].contains(text)) continue;
        doc["materials"].erase(text);
        write_atomic(it->path(), doc.dump(1));
        found = true;
    }
    return found ? KILN_OK : KILN_ERR_NOT_FOUND;
}

bool merged_locked() { return g_merge_started && g_state.load() != KILN_WT_OFF && g_state.load() != KILN_WT_WAITING; }

uint32_t KILN_CALL t_tweak_count() {
    std::lock_guard lock(g_mu);
    return merged_locked() ? static_cast<uint32_t>(g_tweaks.size()) : 0;
}

KilnResult KILN_CALL t_tweak_at(uint32_t i, KilnWtTweak* out) {
    if (!out || out->size < sizeof(KilnWtTweak)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    if (!merged_locked() || i >= g_tweaks.size()) return KILN_ERR_NOT_FOUND;
    // the tweaks don't change after the merge: their strings are made once
    static std::vector<std::array<KilnStr, 4>> strings;
    if (strings.size() != g_tweaks.size()) {
        strings.clear();
        for (const Tweak& t : g_tweaks) strings.push_back({keep_str(t.mod), keep_str(t.block), keep_str(t.dir), keep_str(t.definition)});
    }
    out->cell_id = static_cast<uint32_t>(g_tweaks[i].cell);
    out->mod = strings[i][0];
    out->block = strings[i][1];
    out->mod_dir = strings[i][2];
    out->definition = strings[i][3];
    return KILN_OK;
}

int32_t KILN_CALL t_game_material_id(KilnStr text) {
    if (!text.ptr || !text.len) return -1;
    std::lock_guard lock(g_mu);
    if (!g_layouts_ok || g_world_game.empty()) return -1;
    return resolve_material(std::string(text.ptr, text.len), g_world_game);
}

uint32_t KILN_CALL t_craft_count() {
    std::lock_guard lock(g_mu);
    return merged_locked() ? static_cast<uint32_t>(g_craft.size()) : 0;
}

KilnResult KILN_CALL t_craft_at(uint32_t i, KilnWtCraftingProp* out) {
    if (!out || out->size < sizeof(KilnWtCraftingProp)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    if (!merged_locked() || i >= g_craft.size()) return KILN_ERR_NOT_FOUND;
    // the props don't change after the merge: their strings are made once
    static std::vector<std::array<KilnStr, 2>> strings;
    if (strings.size() != g_craft.size()) {
        strings.clear();
        for (const CraftingProp& c : g_craft) strings.push_back({keep_str(c.mod), keep_str(c.block)});
    }
    const CraftingProp& c = g_craft[i];
    out->state = KILN_WT_LIVE;
    std::memcpy(out->guid, c.guid.b.data(), 16);
    out->prop_id = c.prop_id;
    out->index = static_cast<uint32_t>(c.index);
    out->item_id = c.item_id;
    out->name_tag = c.name_tag;
    out->mod = strings[i][0];
    out->block = strings[i][1];
    return KILN_OK;
}

int32_t KILN_CALL t_craft_index(const uint8_t guid[16]) {
    if (!guid) return -1;
    std::lock_guard lock(g_mu);
    if (!merged_locked()) return -1;
    for (const CraftingProp& c : g_craft)
        if (std::memcmp(c.guid.b.data(), guid, 16) == 0) return c.index;
    return -1;
}

const KilnWorldTablesApi kTable = {
    {sizeof(KilnWorldTablesApi), KILN_WORLDTABLES_VERSION},
    &t_state,
    &t_hash,
    &t_count,
    &t_material_at,
    &t_material_id,
    &t_first_new_id,
    &t_add_writer,
    &t_define,
    &t_prune,
    &t_status,
    &t_forget,
    &t_tweak_count,
    &t_tweak_at,
    &t_game_material_id,
    &t_craft_count,
    &t_craft_at,
    &t_craft_index,
};
const KilnWorldTablesApi* table() { return &kTable; }

// kiln/config/worldtables.json. Missing or broken: the defaults.
struct Config {
    bool enabled = true;
    std::string test_missing_field;  // lab only: read the layouts as if the game had no such field
};
Config read_config() {
    Config c;
    const std::string dir = api_path(KILN_PATH_CONFIG);
    if (dir.empty()) return c;
    std::error_code ec;
    const fs::path p = to_path(dir) / L"worldtables.json";
    if (!fs::exists(p, ec)) return c;
    const ojson j = parse_relaxed(read_text(p));
    if (j.is_discarded() || !j.is_object()) {
        WT_W("worldtables: %s isn't valid JSON: using the defaults", to_utf8(p).c_str());
        return c;
    }
    c.enabled = j.value("enabled", true);
    c.test_missing_field = j.value("testMissingField", "");
    return c;
}

// The mods' blocks and what worlds remember (g_mu held): read in on_ready, or with a boot generation in its prepare.
void read_all(const kiln::boot::Input* boot) {
    try {
        const fs::path pending = data_dir() / L"pending";
        for (const std::string& dir : mod_folders()) read_mod(dir, pending, boot);
        read_kept();
    } catch (const kiln::boot::Refusal&) {
        throw;  // a boot document: its prepare refuses the mod
    } catch (const std::exception& e) {
        g_errors.push_back(std::string("reading the mods' blocks failed: ") + e.what());
    }
    g_state.store(KILN_WT_WAITING);
    if (!g_tweaks.empty() || !g_live.empty() || !g_kept_read.empty())
        WT_I("worldtables: read %zu tweak(s), %zu material(s) from mods, %zu remembered by worlds", g_tweaks.size(), g_live.size(), g_kept_read.size());
}

// A boot generation (chapter 08): the snapshot on_ready reads without a boot profile is read here, after OnBoot, before the game's entry
// point; nothing merges it before the resource service is up (on_resources_ready), so destroy only forgets it.
struct BootPlan {
    size_t tweaks = 0, materials = 0, shapes = 0, props = 0;
};
void forget_locked() {
    g_tweaks.clear();
    g_live.clear();
    g_kept_read.clear();
    g_shapes.clear();
    g_craft_read.clear();
    g_boot_definitions.clear();
    g_from_code.clear();
    g_pending_dirs.clear();
    g_errors.clear();
}
void KILN_CALL destroy_boot(const void* p) {
    {
        std::lock_guard lock(g_mu);
        forget_locked();
    }
    WT_I("worldtables: the boot generation's prepared snapshot is dropped");
    delete static_cast<const BootPlan*>(p);
}
KilnResult KILN_CALL prepare_boot(void*, const KilnBootPrepareInputV1* in, KilnBootPreparedV1* out, KilnBootIssueV1* issue) {
    const kiln::boot::Input input = kiln::boot::parse(in);
    if (!input.ok) return kiln::boot::refuse(out, issue, "", "", "", "kiln.worldtables: the boot envelope is not JSON");
    auto* plan = new BootPlan;
    {
        std::lock_guard lock(g_mu);
        try {
            read_all(&input);
        } catch (const kiln::boot::Refusal& r) {
            forget_locked();
            delete plan;
            return kiln::boot::refuse(out, issue, r.mod, r.kind, r.key, r.what());
        }
        *plan = BootPlan{g_tweaks.size(), g_live.size(), g_shapes.size(), g_craft_read.size()};
    }
    WT_I("worldtables: boot generation %llu prepared: %zu tweak(s), %zu material(s), %zu shape(s), %zu crafting prop(s)",
         static_cast<unsigned long long>(input.generation), plan->tweaks, plan->materials, plan->shapes, plan->props);
    return kiln::boot::prepared(out, plan, &destroy_boot);
}

// After every service's on_ready order is settled: kiln.assets is registered, S isn't up yet.
void KILN_CALL on_ready(void*) {
    g_assets = static_cast<const KilnAssetsApi*>(g_api->service(KilnStr{KILN_ASSETS_SERVICE, sizeof(KILN_ASSETS_SERVICE) - 1}, KILN_ASSETS_VERSION));
    if (!g_assets || g_assets->header.size < sizeof(KilnAssetsApi)) {
        std::lock_guard lock(g_mu);
        go_off("kiln.assets isn't there");
        return;
    }
    if (kiln::boot::collect(g_api, g_self, "worldtables", &prepare_boot, nullptr)) {
        // a boot profile: the snapshot is read in the boot generation's prepare; the merge below stays where it is
        WT_I("worldtables: boot profile: the mods' blocks are read with the boot generation");
    } else {
        std::lock_guard lock(g_mu);
        read_all(nullptr);
    }
    g_saves = static_cast<const KilnSavesApi*>(g_api->service(KilnStr{KILN_SAVES_SERVICE, sizeof(KILN_SAVES_SERVICE) - 1}, KILN_SAVES_VERSION));
    if (g_assets->when_resources_ready(&on_resources_ready, nullptr) != KILN_OK) {
        std::lock_guard lock(g_mu);
        go_off("kiln.assets has no resource service on this build");
    }
}

}  // namespace
}  // namespace kwt

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    using namespace kwt;
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_self = owner;
    info->id = KilnStr{KILN_WORLDTABLES_SERVICE, sizeof(KILN_WORLDTABLES_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    g_client = api->side() == KILN_SIDE_CLIENT;
    const Config cfg = read_config();
    g_test_missing_field = cfg.test_missing_field;
    const KilnResult rc = api->register_service(owner, KilnStr{KILN_WORLDTABLES_SERVICE, sizeof(KILN_WORLDTABLES_SERVICE) - 1}, &kTable);
    if (rc != KILN_OK) return rc;
    KilnBuildInfo bi{sizeof bi};
    api->build_info(&bi);
    if (!cfg.enabled) {
        g_off_reason = "off by kiln/config/worldtables.json";
        WT_I("worldtables: off by kiln/config/worldtables.json: the game's tables are untouched");
        return KILN_OK;
    }
    if (!bi.hooks_enabled) {
        g_off_reason = "hooks disabled (safe idle)";
        WT_I("worldtables: hooks disabled (safe idle): the game's tables are untouched");
        return KILN_OK;
    }
    info->on_ready = &on_ready;
    KilnHandle h = 0;
    api->subscribe_tick(owner, KILN_TICK_PRE, &on_tick, nullptr, &h);
    api->subscribe_world(owner, &on_world, nullptr, &h);
    return KILN_OK;
}
