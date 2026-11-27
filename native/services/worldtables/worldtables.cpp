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
#include "pool_store.h"
#include "reflect.h"
#include "surfacecheck.h"
#include "terrain.h"
#include "services/kiln_assets.h"
#include "services/kiln_caps.h"
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
bool pool_identifier(const std::string& text);

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
std::set<std::string> g_kept_noted;  // stored files of mods that are not loaded, noted once a process
std::vector<std::string> g_kept;      // those notes, for status.json ("kept")
std::set<std::string> g_logged_errors;  // of g_errors, what was logged when it happened (the pending store's refusals)
std::map<std::string, std::string> g_boot_definitions;  // "<mod>/<kind>/<block>" -> the normal form the merge read
std::set<std::string> g_from_code;                       // "<mod>/<block>": read from what code kept in an earlier run
std::map<std::string, fs::path> g_pending_dirs;          // mod id -> its folder of blocks kept from code
std::set<std::string> g_defined;                         // "<mod>/<block>" or "<mod>/@pool/<block>": defined by code this session
// A boot generation's required participants that have world-table blocks (prepare): what the merge cannot apply of theirs is an
// activation fault, which stops the start (kiln.boot activation_fault)
std::set<std::string> g_boot_required;

// What came of it.
std::vector<Placed> g_placed;             // building materials, then terrain ones (cell < 128): live first in each
size_t g_terrain_room = 0;                // ids for new terrain materials
size_t g_pending_terrain = 0;             // new terrain materials code defined this session
TerrainTables g_terrain_tables;
std::vector<std::string> g_refused;
std::vector<PoolDefinition> g_pool_defs;
std::vector<MaterialPool> g_custom_pools;
std::set<Guid> g_grouped;
std::map<std::string,std::set<std::string>> g_dependencies;
std::map<std::string,PoolGameMaterial> g_game_pool_materials;
bool g_game_pool_materials_read=false;
bool g_has_pool_shapes=false;
std::map<std::string,uint32_t> g_pool_original_ids;
PoolBootStore g_pending_boot;
std::vector<PoolDefinition> g_pool_replayed;
bool g_pool_layout_ok=false;
std::string g_pool_reason;
uint32_t g_item_pool=UINT32_MAX;
PoolRegistrations g_pool_registrations;
// Stable nodes: revoked callbacks cannot reference freed user data. No engine/world addresses.
std::map<KilnOwner,PoolOwnerLease> g_pool_owner_leases;
std::map<Guid,KilnOwner> g_pool_owners;
std::map<Guid,std::string> g_pool_pruned;  // pool envelopes this start's prune removed (definition -> mod)
KilnHandle g_pool_owner_post=0;

bool g_budget_cap=false,g_pool_cap=false,g_budgets_ready=false;
uint32_t g_game_material_used[2]{};
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
void report_pool_caps(uint32_t reason,const std::string& text) {
    const auto* caps=static_cast<const KilnCapsApi*>(g_api->service(KilnStr{KILN_CAPS_SERVICE,sizeof(KILN_CAPS_SERVICE)-1},1));
    if(!caps || caps->header.size<sizeof(KilnCapsApi))return;
    for(const auto& [name,on]:{std::pair<const char*,bool>{"worldtables.budgets",g_budget_cap},{"worldtables.material_pools",g_pool_cap}}) {
        const std::string why=on?"boot table snapshots":text;
        caps->report(g_self,KilnStr{name,std::strlen(name)},on?KILN_CAP_AVAILABLE:KILN_CAP_UNAVAILABLE,on?KILN_CAP_REASON_NONE:reason,KilnStr{why.data(),why.size()});
    }
}


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
    return kiln::capacity::write_atomic(path,text);
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

kiln::capacity::pending_io::TrustedBase pending_base() {
    static const bool hooked=[] {
        kiln::capacity::pending_io::diagnostic=[](const auto& path,const auto& why) {
            const auto message=stored_refusal_text(to_utf8(path),why);
            // said now (the store is read before the merge, and also after it); the merge's error list does not repeat it
            WT_W("worldtables: %s",message.c_str());g_errors.push_back(message);g_logged_errors.insert(message);
        };return true;
    }();
    (void)hooked;return {data_dir()};
}
fs::path pool_pending_dir(const std::string& mod) {return data_dir()/L"pending"/to_path(mod);}
bool persist_pool(const PoolReceipt& receipt) {
    const fs::path path=pool_pending_dir(receipt.definition.mod)/to_path("@pool-"+receipt.definition.guid.text()+".json");
    std::string reason;
    const bool ok=kiln::capacity::pending_store_write(pending_base(),data_dir()/L"pending",path,receipt.envelope(),&reason);
    if(!ok)kiln::capacity::pending_io::refused(path,"unsafe: "+reason);
    return ok;
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
    std::set<std::string> dependencies;
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
    if(out->id.empty() || out->id=="." || out->id==".." || out->id.find_first_not_of("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-")!=std::string::npos)return false;
    if(auto deps=j.find("dependencies");deps!=j.end()) {
        if(deps->is_object()) for(auto it=deps->begin();it!=deps->end();++it) out->dependencies.insert(it.key());
        else if(deps->is_array()) for(const auto& d:*deps) if(d.is_object() && d.contains("id") && d["id"].is_string()) out->dependencies.insert(d["id"].get<std::string>());
    }
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
    g_dependencies[man.id]=man.dependencies;
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
    // a required participant prepares strictly (kiln_boot_collector.hpp): a file of its own that cannot be read or is not JSON, or a
    // block with an error, refuses it; another mod's are logged and skipped as they always were. What Kiln kept from code at an
    // earlier start is Kiln's own data, not the mod's boot content: read as before.
    const auto strict = [&](const std::string& key, const std::string& why) {
        kiln::boot::entry_error(boot, man.id, "worldtables", key, "kiln.worldtables: " + why);
    };
    for (const auto& [label, path] : files) {
        std::ifstream probe(path, std::ios::binary);
        if (!probe) {
            g_errors.push_back(man.id + ": " + label + ": cannot be read");
            strict(label, label + ": cannot be read");
            continue;
        }
        add(label, parse_relaxed(read_text(path)));
    }
    if (boot)
        for (const auto& b : boot->docs("worldtables", man.id)) {
            if (!b.document.is_object())
                throw kiln::boot::Refusal(man.id, "worldtables", b.key, "kiln.worldtables: a boot document is not a blocks file (a JSON object)");
            add("boot code: " + b.key, ojson::parse(b.document.dump(), nullptr, false));
        }
    for(auto definition:g_pool_replayed)if(definition.mod==man.id) {
        definition.dependencies=man.dependencies;
        docs.emplace_back("kept pool receipt: "+definition.guid.text(),pool_replay_document(definition));
        g_pending_dirs[man.id]=pending_root/to_path(man.id);
    }
    for(const auto& frozen:g_pending_boot.files) {
        const auto& pending=frozen.file;
        if(pending.mod!=man.id || pending.path.extension()!=L".json" || pending.name.rfind("@pool-",0)==0)continue;
        std::string bytes;
        if(g_pending_boot.read(pending_base(),frozen,&bytes))add("kept from code: "+pending.name,parse_relaxed(bytes));
        g_pending_dirs[man.id]=pending_root/to_path(man.id);
    }
    std::set<std::string> seen;
    for (const auto& [label, doc] : docs) {
        ModFile mf;
        mf.mod = man.id;
        mf.dir = dir;
        mf.file = man.id + ": " + label;
        mf.clients = man.clients;
        mf.dependencies = man.dependencies;
        mf.strengths = strengths;
        mf.surfaces = surfaces;
        const bool own = label.rfind("kept from code: ", 0) != 0 && label.rfind("kept pool receipt: ",0)!=0;
        if (doc.is_discarded()) {
            g_errors.push_back(mf.file + ": not valid JSON");
            if (own) strict(label, label + ": not valid JSON");
            continue;
        }
        // every object document goes to read_blocks, which reads its own sections and leaves the rest alone: a list of section
        // names here would have to follow every reader there (one that did not skipped tool-only documents, GitHub #129)
        if (!doc.is_object()) continue;
        read_blocks(doc, &mf);
        g_errors.insert(g_errors.end(), mf.errors.begin(), mf.errors.end());
        if (own && !mf.errors.empty())
            strict(label, mf.errors.front() + (mf.errors.size() > 1 ? " (and " + std::to_string(mf.errors.size() - 1) + " more, kiln.log)" : ""));
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
        for (auto& p:mf.pools) {
            if(!seen.insert("p/"+p.block).second) continue;
            g_boot_definitions[man.id+"/p/"+p.block]=p.definition;
            g_pool_defs.push_back(std::move(p));
        }
        for (Shape& sh : mf.shapes) {
            if (!seen.insert("b/" + sh.block).second) continue;
            g_boot_definitions[man.id + "/b/" + sh.block] = sh.definition;
            if (label.rfind("kept from code: ", 0) == 0) g_from_code.insert(man.id + "/" + sh.block);
            g_has_pool_shapes=g_has_pool_shapes || !sh.material_pool.empty();
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
    std::vector<GameBlockItem> blocks;      // the items that place a material: what a pool's gameMaterial names
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
        g_names.blocks.push_back({name, item_id_of(g), b[I.voxel_id]});
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
    o["customPools"]=ojson::array();
    for(const auto& p:g_custom_pools)o["customPools"].push_back({{"guid",p.guid.text()},{"name",p.name},{"poolId",item_id_of(p.guid)},{"roof",p.roof},{"items",p.items}});
    o["shapeBindings"]=ojson::array();
    for(const auto& shape:g_shapes_in)if(!shape.pool.zero())o["shapeBindings"].push_back({{"item",shape.item.text()},{"pool",shape.pool.text()},{"mod",shape.mod},{"block",shape.block}});
    o["poolRegistrations"]=ojson::array();
    for(const auto& [id,r]:g_pool_registrations.receipts)o["poolRegistrations"].push_back({{"registration",r.key()},{"definition",r.definition.guid.text()},{"mod",r.definition.mod},{"owner",g_pool_owners.count(r.definition.guid)?g_pool_owners.at(r.definition.guid):KilnOwner(0)},{"state",r.reply.disposition},{"code",r.reply.reason_code},{"slots",r.reply.slots_consumed},{"revision",r.reply.snapshot_revision},{"reason",r.reason.empty()?ojson(nullptr):ojson(r.reason)}});
    o["revision"]=g_pool_registrations.revision;
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
    o["kept"] = g_kept;
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
    g_budgets_ready=false;g_budget_cap=false;g_pool_cap=false;
    report_pool_caps(KILN_CAP_REASON_FAULTED,why);
    g_off_reason = why;
    g_placed.clear();
    g_pool_served.clear();
    g_hash = kEmptyHash;
    WT_E("worldtables: %s. The game's tables are untouched: material blocks are off this session", why.c_str());
    write_status();
}

// Only a custom pool needs this catalog. A first late definition builds it lazily.
void read_pool_catalog(const Bytes& registry,const RegistryInfo& info,const Bytes& pools) {
    read_names();
    std::set<uint32_t> vanilla;std::string why;
    terrain_pool_items(pools,g_layouts,&vanilla,&why); // A missing/changed terrain pool marks no item eligible for tool policies.
    g_game_pool_materials=pool_game_materials(g_names.blocks,registry,info,g_layouts,true,vanilla);
    g_game_pool_materials_read=true;
}
bool ensure_pool_catalog() {
    const auto one=[](uint32_t type,const char* name,Output* out) {
        const auto keys=keys_of(type);if(keys.size()!=1)return false;
        out->key=keys[0];out->name=name;return bytes_of(out->key,&out->game);
    };
    if(g_pool_original_ids.empty()) {
        Output pools;ArrayView rows;
        if(!g_pool_layout_ok || !one(kTypePools,"pools",&pools) || !array_at(pools.game,g_layouts.pools,g_layouts.pool_stride,&rows))return false;
        for(uint32_t i=0;i<rows.count;++i) {
            uint32_t id;std::memcpy(&id,rows.base+size_t(i)*g_layouts.pool_stride+g_layouts.pool_id,4);
            // Ignore this service's own appended rows: they are identified separately by GUID/name.
            if(std::any_of(g_custom_pools.begin(),g_custom_pools.end(),[&](const auto& p){return item_id_of(p.guid)==id;}))continue;
            g_pool_original_ids[std::to_string(i)]=id;
        }
    }
    if(g_game_pool_materials_read)return true;
    Output registry,pools;RegistryInfo info;std::string why;
    if(!one(kTypeRegistry,"registry",&registry) || !one(kTypePools,"pools",&pools) || !read_registry(registry.game,g_layouts,&info,&why))return false;
    read_pool_catalog(registry.game,info,pools.game);return true;
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
    if(g_pool_layout_ok && !g_pool_defs.empty()) {
        ArrayView rows;
        if(array_at(pools.game,L.pools,L.pool_stride,&rows)) for(uint32_t i=0;i<rows.count;++i) {
            const auto* rec=rows.base+size_t(i)*L.pool_stride;uint32_t id;std::memcpy(&id,rec+L.pool_id,4);
            g_pool_original_ids[std::to_string(i)]=id;
        }
    }
    // Only accepted custom groups affect selector fitting. Allocation may subsequently refuse a member, so repeat
    // fitting from the original inputs after removing such a pool (the accepted set only shrinks).
    PoolResolution resolution;
    if(!g_pool_defs.empty()) {
        read_pool_catalog(reg.game,info,pools.game);
        resolution=resolve_pools(g_pool_defs,g_live,g_game_pool_materials);
        if(g_pool_layout_ok)admit_custom_pools(pools.game,L,&resolution);
        else refuse_custom_pools(&resolution,g_pool_reason);
    }
    const auto all_live=g_live;
    std::vector<Material> tlive,tkept;
    Allocation a,ta;
    for(;;) {
        g_live=all_live;tlive.clear();tkept.clear();
        g_grouped=resolution.grouped;
        std::vector<Material> held;
        std::vector<std::string> pool_refused;
        {
            std::set<Guid> remembered;
            for (const Material& k : g_kept_read) remembered.insert(k.guid);
            if(g_grouped.empty()) fit_pools(&g_live,g_pool_game,remembered,&held,&pool_refused);
            else fit_pools(&g_live,g_pool_game,remembered,&held,&pool_refused,g_grouped);
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
        a = allocate(g_live, kept, free_ids, deprecated,g_kept_read);
        g_room = a.room;
        g_refused = std::move(pool_refused);
        g_refused.insert(g_refused.end(), a.refused.begin(), a.refused.end());
        g_placed = std::move(a.placed);
        ta = allocate_terrain(tlive, tkept, free_terrain_ids(worlds[0].game, L, info));
        g_terrain_room = ta.room;
        g_refused.insert(g_refused.end(), ta.refused.begin(), ta.refused.end());
        if(!retain_allocated_pools(&resolution,all_live,g_placed,g_game_pool_materials))break;
    }
    g_custom_pools=std::move(resolution.pools);
    g_errors.insert(g_errors.end(),resolution.errors.begin(),resolution.errors.end());
    for (const std::string& r : g_refused) WT_E("worldtables: %s", r.c_str());

    // The new bytes. Anything that doesn't read leaves everything as the game has it.
    std::vector<RegistryChange> changes;
    if (!merge_registry(reg.game, L, g_tweaks, g_placed, &reg.merged, &changes, &why)) {
        go_off(why);
        return;
    }
    if(!g_pool_defs.empty() || g_has_pool_shapes)bind_shape_pools(&g_shapes,g_pool_defs,g_custom_pools,&g_refused);
    const bool pool_ok=(g_pool_defs.empty() && g_grouped.empty())
        ? merge_pools(pools.game,L,g_placed,&pools.merged,&why)
        : merge_pools(pools.game,L,g_placed,g_custom_pools,&pools.merged,&why,g_grouped);
    if (!pool_ok) {
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
    g_hash = g_custom_pools.empty()?handshake_hash(g_placed,g_craft):handshake_hash(g_placed,g_craft,g_custom_pools,g_shapes_in);
    g_state.store(any ? KILN_WT_MERGED : KILN_WT_UNTOUCHED);
    g_game_material_used[0]=uint32_t(128-free_ids.size()-deprecated.size());
    g_game_material_used[1]=uint32_t(127-g_terrain_room);
    g_budgets_ready=true;
    ++g_pool_registrations.revision;
    for(const auto& def:g_pool_defs) {
        const bool served=std::any_of(g_custom_pools.begin(),g_custom_pools.end(),[&](const auto& p){return p.guid==def.guid;});
        auto receipt=g_pool_registrations.prepare(def,KILN_DEFINE_REQUIRE_REGISTERED,served,served?KILN_DEFINE_NONE:KILN_DEFINE_UNKNOWN_BASE,served?"":"pool refused during boot admission");
        g_pool_registrations.commit(std::move(receipt),[](const PoolReceipt&){return false;});
    }

    for(const auto& def:g_pool_replayed) {
        const auto boot=g_boot_definitions.find(def.mod+"/p/"+def.block);
        const bool same=boot!=g_boot_definitions.end() && boot->second==def.definition;
        const bool served=same && std::any_of(g_custom_pools.begin(),g_custom_pools.end(),[&](const auto& p){return p.guid==def.guid;});
        auto receipt=g_pool_registrations.prepare(def,KILN_DEFINE_REQUIRE_REGISTERED,served,served?KILN_DEFINE_NONE:(same?KILN_DEFINE_UNKNOWN_BASE:KILN_DEFINE_SUPERSEDED),served?"":(same?"pool refused during boot admission":"a newer authored definition superseded the pending pool"));
        g_pool_registrations.commit(std::move(receipt),[](const PoolReceipt&){return false;});
    }
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
    else if (!any)
        WT_I("worldtables: nothing to change: the game's %zu tables are served as they are (registry fnv %s); hash %s", outputs.size(), hex64(fnv64(outputs[0].game)).c_str(), g_hash.c_str());
}

const KilnWorldTablesApi* table();

// The mod a merge error or refusal names: "... (mod <id>)..." or "... (<block>, mod <id>): ...".
std::string mod_named(const std::string& text) {
    for (const char* tag : {"(mod ", ", mod "}) {
        const size_t at = text.find(tag);
        if (at == std::string::npos) continue;
        const size_t from = at + std::strlen(tag), end = text.find_first_of("):", from);
        return text.substr(from, end == std::string::npos ? std::string::npos : end - from);
    }
    return {};
}

void KILN_CALL on_resources_ready(void*) {
    std::vector<Writer> writers;
    std::vector<std::pair<std::string, std::string>> faults;  // a required boot mod, what the merge could not apply of it
    {
        std::lock_guard lock(g_mu);
        if (g_merge_started) return;
        g_merge_started = true;
        WT_I("worldtables: the game's resource service is up: merging from the bytes kiln.assets serves now");
        try {
            merge_locked();
        } catch (const std::exception& e) {
            go_off(std::string("the merge failed: ") + e.what());
        }
        for (const std::string& e : merge_error_lines(g_errors, g_logged_errors)) WT_E("worldtables: %s", e.c_str());
        write_status();
        if (g_state.load() != KILN_WT_OFF) writers = g_writers;
        std::set<std::string> said;
        if (g_state.load() == KILN_WT_OFF) {
            for (const std::string& m : g_boot_required)
                if (said.insert(m).second) faults.emplace_back(m, "kiln.worldtables is off this session (" + g_off_reason + "): its world-table blocks are not applied");
        } else {
            for (const auto* list : {&g_errors, &g_refused})
                for (const std::string& e : *list) {
                    if (e.rfind("kept ", 0) == 0) continue;  // what a world remembers, not the mod's blocks of this start
                    const std::string m = mod_named(e);
                    if (g_boot_required.count(m) && said.insert(m).second) faults.emplace_back(m, e);
                }
        }
    }
    // a required boot mod's blocks that could not be applied: kiln.boot stops the start (the call does not return then)
    for (const auto& [mod, why] : faults) kiln::boot::activation_fault(g_api, g_self, mod, "worldtables", "", "kiln.worldtables: " + why);
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

// Blocks kept from code that the code didn't define this session go from the next start, pool envelopes included: a
// stored pool is its mod's standing request only as long as the mod still asks for it. By the time the world runs every
// mod's OnLoad has run; a block or pool a mod defines later is kept again when it's defined (define_json, define_pool),
// so it never drops out for good. Only mods read at this start (enabled in the profile) are pruned; a mod that is not
// loaded keeps every file: the profile's business. `remove_pools`: an explicit removal of these pool keys only.
bool prune_mod_pending(const std::string& mod,const std::set<std::string>& keep_names,const std::set<std::string>* remove_pools=nullptr,size_t* removed=nullptr) {
    kiln::capacity::PendingScan scan;
    const auto files=kiln::capacity::pending_files_bounded(pending_base(),data_dir()/L"pending",&scan);
    if(!scan.complete)return false;
    for(const auto& f:files) {
        if(f.mod!=mod || f.path.extension()!=L".json")continue;
        auto key=to_utf8(f.path.stem());Guid guid;
        const bool pool=key.rfind("@pool-",0)==0;
        if(remove_pools && !pool)continue;
        if(pool) {
            PoolDefinition def;std::string why,bytes;
            if(!kiln::capacity::pending_io::read(pending_base(),f.path,&bytes,65536) || !pool_envelope(parse_relaxed(bytes),mod,&def,&why))continue;
            if(f.name!="@pool-"+def.guid.text()+".json")continue;
            key=def.block;guid=def.guid;
        }
        if(remove_pools ? !remove_pools->count(key) : !stored_file_goes(to_utf8(f.path.stem()),key,true,keep_names))continue;
        if(!kiln::capacity::pending_io::remove(pending_base(),f.path))return false;
        if(removed)++*removed;
        if(!guid.zero()){g_pool_registrations.remove_pending(guid);g_pool_pruned[guid]=mod;}
        if(remove_pools)WT_I("worldtables: %s: stored pool %s removed: it's gone from the next start",mod.c_str(),key.c_str());
        else WT_I("worldtables: %s: the code no longer defines %s: it's gone from the next start",mod.c_str(),key.c_str());
    }
    return true;
}
void prune_pending() {
    std::lock_guard lock(g_mu);
    for(const auto& [mod,dir]:g_pending_dirs) {
        std::set<std::string> keep;
        for(const auto& name:g_defined)if(name.rfind(mod+"/",0)==0)keep.insert(name.substr(mod.size()+1));
        if(!prune_mod_pending(mod,keep))WT_W("worldtables: %s: failed to prune pending definitions",mod.c_str());
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

void KILN_CALL pool_owner_pre(const KilnTickInfo* tick,void* user) {
    if(!tick || !user)return;
    std::lock_guard lock(g_mu);auto& lease=*static_cast<PoolOwnerLease*>(user);
    lease.step=tick->step;lease.seen=true;lease.misses=0;
}
void KILN_CALL pool_owner_probe(void*) {}
void KILN_CALL pool_owners_post(const KilnTickInfo* tick,void*) {
    if(!tick)return;
    std::lock_guard lock(g_mu);
    for(auto it=g_pool_owner_leases.begin();it!=g_pool_owner_leases.end();) {
        const KilnOwner owner=it->first;
        const auto alive=[&]{return g_api->post_world(owner,g_client?KILN_WORLD_CLIENT:KILN_WORLD_SERVER,&pool_owner_probe,nullptr);};
        if(!pool_lease_gone(it->second,tick->step,alive)){++it;continue;}
        for(const auto& [definition,current]:g_pool_owners)if(current==owner)g_pool_registrations.owner_inactive(definition);
        it=g_pool_owner_leases.erase(it);  // a revoked owner never comes back: no more probes
    }
}
bool track_pool_owner(const Guid& definition,KilnOwner owner) {
    g_pool_owners[definition]=owner;
    if(!g_pool_owner_post) {
        if(g_api->subscribe_world_tick(g_self,g_client?KILN_WORLD_CLIENT:KILN_WORLD_SERVER,KILN_TICK_POST,&pool_owners_post,nullptr,&g_pool_owner_post)!=KILN_OK) {
            g_pool_cap=false;report_pool_caps(KILN_CAP_REASON_SYMBOL_MISSING,"pool owner lifetime requires world tick subscriptions");return false;
        }
    }
    if(g_pool_owner_leases.count(owner))return true;
    auto& lease=g_pool_owner_leases[owner];lease.owner=owner;KilnHandle handle=0;
    if(g_api->subscribe_world_tick(owner,g_client?KILN_WORLD_CLIENT:KILN_WORLD_SERVER,KILN_TICK_PRE,&pool_owner_pre,&lease,&handle)!=KILN_OK) {
        g_pool_registrations.owner_inactive(definition);return false;
    }
    return true;
}
struct PoolAdmission {uint32_t code=KILN_DEFINE_NONE;bool bad_argument=false;std::string reason;};
PoolAdmission pool_admission(const PoolDefinition& definition) {
    if(!g_budgets_ready)return {KILN_DEFINE_NOT_READY,false,"the boot tables are not ready"};
    if(!ensure_pool_catalog())return {KILN_DEFINE_NOT_READY,false,"the building material catalog could not be read"};
    std::vector<Material> materials;for(const auto& p:g_placed)if(p.m.live)materials.push_back(p.m);
    auto resolved=resolve_pools({definition},materials,g_game_pool_materials);
    if(resolved.arguments_invalid)return {KILN_DEFINE_UNKNOWN_BASE,true,resolved.argument_reason};
    if(!resolved.errors.empty())return {KILN_DEFINE_UNKNOWN_BASE,false,resolved.errors.front()};
    for(const auto& [name,id]:g_pool_original_ids)if(id==item_id_of(definition.guid))return {KILN_DEFINE_ID_COLLISION,false,"pool id belongs to an existing registry row"};
    for(const auto& p:g_custom_pools) {
        if(p.guid!=definition.guid && item_id_of(p.guid)==item_id_of(definition.guid))return {KILN_DEFINE_ID_COLLISION,false,"pool id collision"};
        if(p.guid!=definition.guid && p.name==definition.mod+":"+definition.block)return {KILN_DEFINE_NAME_TAKEN,false,"pool name is taken"};
    }
    return {};
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
            if(!mf.pools.empty() || std::any_of(mf.shapes.begin(),mf.shapes.end(),[](const auto& sh){return !sh.material_pool.empty();})) {
                auto deps=g_dependencies.find(mf.mod);
                if(deps!=g_dependencies.end()) {
                    for(auto& p:mf.pools)p.dependencies=deps->second;
                    for(auto& sh:mf.shapes)sh.dependencies=deps->second;
                }
            }
            const fs::path pending = data_dir() / L"pending" / to_path(mf.mod);
            const auto keep_block = [&](const char* section, const std::string& name) {
                ojson one = ojson::object();
                one["format"] = 1;
                one[section] = ojson::object();
                one[section][name] = doc[section][name];
                std::string pending_reason;
                if (!kiln::capacity::pending_store_write(pending_base(),data_dir()/L"pending",pending / to_path(name + ".json"), one.dump(1), &pending_reason))
                    kiln::capacity::pending_io::refused(pending / to_path(name + ".json"),"unsafe: "+pending_reason);
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
                    const std::string pool = pool_of(m,g_grouped);
                    if (!pool.empty() && !g_pool_game.empty()) {
                        const uint32_t have = g_pool_game.count(pool) ? g_pool_game.at(pool) : 0;
                        const size_t pool_room = have < kPoolCapacity ? kPoolCapacity - have : 0;
                        const size_t pool_taken = g_pending_pool[pool] + static_cast<size_t>(std::count_if(g_placed.begin(), g_placed.end(), [&](const Placed& p) {
                                                      return p.m.live && pool_of(p.m,g_grouped) == pool;
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
            for (const auto& p:mf.pools) {
                const std::string kind=p.tool?"block toolMaterialPool ":"block buildingMaterialPool ";
                const auto registered=std::find_if(g_custom_pools.begin(),g_custom_pools.end(),[&](const auto& g){return g.guid==p.guid;});
                const auto boot=g_boot_definitions.find(mf.mod+"/p/"+p.block);
                if(!g_pool_layout_ok) { errors.push_back(kind+p.block+": "+g_pool_reason); continue; }
                const auto admission=pool_admission(p);
                if(admission.code){errors.push_back(kind+p.block+" (mod "+p.mod+"): refused: "+admission.reason);continue;}
                const bool identical=registered!=g_custom_pools.end() && boot!=g_boot_definitions.end() && boot->second==p.definition;
                auto receipt=g_pool_registrations.commit(g_pool_registrations.prepare(p,KILN_DEFINE_ALLOW_NEXT_START,identical),persist_pool);
                if(!receipt.reply.ok){errors.push_back(kind+p.block+": "+receipt.reason);continue;}
                if(!restore_pruned_pool(g_pool_pruned,receipt,persist_pool)) {
                    errors.push_back(kind+p.block+": could not restore its stored definition for the next start");
                    rc=KILN_ERR_INTERNAL;continue;
                }
                g_defined.insert(mf.mod+"/@pool/"+p.block);
                blocks.push_back({{"name",p.block},{"kind",p.tool?"toolMaterialPool":"buildingMaterialPool"},{"guid",p.guid.text()},{"status",identical?"registered":"pending"}});
            }
            for (const Shape& sh : mf.shapes) {
                ojson b = ojson::object();
                b["name"] = sh.block;
                b["kind"] = sh.terrain_brush ? "terrainBrush" : "blueprint";
                b["item"] = sh.item.text();
                const auto it = g_boot_definitions.find(mf.mod + "/b/" + sh.block);
                const bool in = std::any_of(g_shapes_in.begin(), g_shapes_in.end(), [&](const Shape& x) { return x.mod == mf.mod && x.block == sh.block; });
                if (!usable) b["status"] = off;
                else if (it != g_boot_definitions.end() && it->second == sh.definition && in) {
                    b["status"] = "registered";
                    if (g_from_code.count(mf.mod + "/" + sh.block)) keep_block(sh.terrain_brush ? "toolBrushShapes" : "blueprints", sh.block);
                } else {
                    b["status"] = "pending";
                    keep_block(sh.terrain_brush ? "toolBrushShapes" : "blueprints", sh.block);
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

KilnResult KILN_CALL t_prune(KilnStr mod,KilnStr names) {
    if(!mod.ptr || !names.ptr)return KILN_ERR_INVALID_ARGUMENT;
    const std::string owner(mod.ptr,mod.len);
    if(!pool_identifier(owner) || owner.find(':')!=std::string::npos)return KILN_ERR_INVALID_ARGUMENT;
    const auto list=parse_relaxed(std::string(names.ptr,names.len));
    if(list.is_object()) {
        // {"removePools": ["key", ...]}: remove exactly these stored pool definitions of the mod (served rows stay this session)
        const auto it=list.find("removePools");
        if(list.size()!=1 || it==list.end() || !it->is_array())return KILN_ERR_INVALID_ARGUMENT;
        std::set<std::string> pools;
        for(const auto& name:*it){if(!name.is_string() || !pool_identifier(name.get<std::string>()) || name.get<std::string>().find(':')!=std::string::npos)return KILN_ERR_INVALID_ARGUMENT;pools.insert(name.get<std::string>());}
        std::lock_guard lock(g_mu);size_t removed=0;
        if(!prune_mod_pending(owner,{},&pools,&removed))return KILN_ERR_INTERNAL;
        return removed?KILN_OK:KILN_ERR_NOT_FOUND;
    }
    if(!list.is_array())return KILN_ERR_INVALID_ARGUMENT;
    std::set<std::string> keep;for(const auto& name:list) {
        if(!name.is_string())return KILN_ERR_INVALID_ARGUMENT;
        keep.insert(name.get<std::string>());
        keep.insert("@pool/"+name.get<std::string>()); // the explicit legacy list names blocks of any kind
    }
    std::lock_guard lock(g_mu);
    return prune_mod_pending(owner,keep)?KILN_OK:KILN_ERR_INTERNAL;
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

bool pool_identifier(const std::string& text) {
    if(text.empty())return false;
    const auto at=text.find(':');
    const auto part=[](const std::string& s){return !s.empty() && s.find_first_not_of("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-")==std::string::npos;};
    return at==std::string::npos?part(text):part(text.substr(0,at)) && part(text.substr(at+1));
}
KilnResult KILN_CALL t_material_budget(uint32_t category,KilnCapacityInfo* out) {
    if(category>1 || !kiln::capacity::valid_out(out))return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    if(!g_budget_cap)return KILN_ERR_UNAVAILABLE;
    const auto scope=kiln::capacity::Scope::process();
    if(!g_budgets_ready){kiln::capacity::not_ready(out,KILN_CAPACITY_NOT_READY,scope,g_pool_registrations.revision);return KILN_OK;}
    uint64_t live=0,retained=0;
    for(const auto& p:g_placed)if(p.m.terrain==(category==1))(p.m.live?live:retained)++;
    // The game's own use is what its bytes held before Kiln's merge (read at boot), not a constant of one build.
    const auto b=material_budget_counts(category,g_game_material_used[category],live,retained);
    if(!b.ready){kiln::capacity::not_ready(out,KILN_CAPACITY_ACCOUNTING_MISMATCH,scope,g_pool_registrations.revision);return KILN_OK;}
    kiln::capacity::publish(out,b.capacity,b.game,b.kiln,b.reserved,scope,g_pool_registrations.revision,false);
    return KILN_OK;
}
KilnResult KILN_CALL t_pool_budget(KilnStr name,KilnCapacityInfo* out) {
    if(!name.ptr || !kiln::capacity::valid_out(out) || name.len>(16u<<20))return KILN_ERR_INVALID_ARGUMENT;
    const std::string pool(name.ptr,name.len);if(!pool_identifier(pool))return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);if(!g_budget_cap)return KILN_ERR_UNAVAILABLE;
    const auto scope=kiln::capacity::Scope::process();const auto revision=g_pool_registrations.revision;
    if(!g_budgets_ready){kiln::capacity::not_ready(out,KILN_CAPACITY_NOT_READY,scope,revision);return KILN_OK;}
    const auto custom=std::find_if(g_custom_pools.begin(),g_custom_pools.end(),[&](const auto& p){return p.name==pool;});
    if(custom!=g_custom_pools.end()) {
        const bool retained=std::any_of(g_pool_registrations.receipts.begin(),g_pool_registrations.receipts.end(),[&](const auto& r){return r.second.definition.guid==custom->guid && r.second.reply.disposition==KILN_DEFINITION_RETAINED;});
        kiln::capacity::publish(out,64,0,retained?0:custom->items.size(),retained?custom->items.size():0,scope,revision,false);return KILN_OK;
    }
    const auto original=g_pool_game.find(pool),served=g_pool_served.find(pool);
    if(original==g_pool_game.end() || served==g_pool_served.end()){kiln::capacity::not_ready(out,KILN_CAPACITY_NOT_FOUND,scope,revision);return KILN_OK;}
    // The game's own entries are what the pool listed before Kiln's merge (its bytes at boot), not a constant of one build.
    const auto b=pool_budget_counts(pool,original->second,served->second);
    if(!b.ready){kiln::capacity::not_ready(out,KILN_CAPACITY_ACCOUNTING_MISMATCH,scope,revision);return KILN_OK;}
    kiln::capacity::publish(out,b.capacity,b.game,b.kiln,b.reserved,scope,revision,false);return KILN_OK;
}
KilnResult KILN_CALL t_define_pool(KilnOwner owner,KilnStr name,KilnStr json,uint32_t timing,KilnDefinitionReply* out,char* report,size_t cap,size_t* out_len) {
    if(!owner || !out || out->size<sizeof *out || !name.ptr || !json.ptr || !out_len || timing>1 || name.len>(16u<<20) || json.len>(16u<<20))return KILN_ERR_INVALID_ARGUMENT;
    const std::string key(name.ptr,name.len);if(!pool_identifier(key) || key.find(':')!=std::string::npos)return KILN_ERR_INVALID_ARGUMENT;
    try {
        auto doc=parse_relaxed(std::string(json.ptr,json.len));
        if(!doc.is_object() || !doc.contains("mod") || !doc["mod"].is_string() || !doc.contains("buildingMaterialPools") || !doc["buildingMaterialPools"].is_object() || doc["buildingMaterialPools"].size()!=1 || !doc["buildingMaterialPools"].contains(key))return KILN_ERR_INVALID_ARGUMENT;
        ModFile mf;mf.mod=doc["mod"].get<std::string>();mf.clients="mod";
        if(!pool_identifier(mf.mod) || mf.mod.find(':')!=std::string::npos)return KILN_ERR_INVALID_ARGUMENT;
        read_blocks(doc,&mf);
        if(!mf.errors.empty() || mf.pools.size()!=1)return KILN_ERR_INVALID_ARGUMENT;
        std::lock_guard lock(g_mu);if(!g_pool_cap)return KILN_ERR_UNAVAILABLE;
        auto definition=mf.pools[0];const auto deps=g_dependencies.find(mf.mod);
        if(deps!=g_dependencies.end())definition.dependencies=deps->second;
        uint32_t refusal=KILN_DEFINE_NONE;std::string reason;
        if(deps==g_dependencies.end()){refusal=KILN_DEFINE_OWNER_INACTIVE;reason="owning mod is not enabled";}
        if(!refusal) {
            const auto admission=pool_admission(definition);
            // the author's own mistake: a bad argument, its reason in the report (mutation-free, so a probe may grow)
            if(admission.bad_argument)return give_text(admission.reason,report,cap,out_len,KILN_ERR_INVALID_ARGUMENT);
            refusal=admission.code;reason=admission.reason;
        }
        const auto boot=g_boot_definitions.find(definition.mod+"/p/"+definition.block);
        const bool served=boot!=g_boot_definitions.end() && boot->second==definition.definition && std::any_of(g_custom_pools.begin(),g_custom_pools.end(),[&](const auto& p){return p.guid==definition.guid;});
        auto preview=g_pool_registrations.prepare(definition,timing,served,refusal,reason);
        // Size for both success and a possible durable-write failure before performing any mutation.
        const size_t required=pool_define_report_size(preview);
        *out_len=required;
        if(!report || cap<=required)return KILN_ERR_BUFFER_TOO_SMALL;
        // Core revocation is checked only after the mutation-free probe; this posts no game work.
        const auto owner_check=g_api->post_world(owner,g_client?KILN_WORLD_CLIENT:KILN_WORLD_SERVER,&pool_owner_probe,nullptr);
        if(owner_check==KILN_ERR_REVOKED)preview=g_pool_registrations.prepare(definition,timing,served,KILN_DEFINE_OWNER_INACTIVE,"owner is no longer active");
        else if(owner_check!=KILN_OK){g_pool_cap=false;report_pool_caps(KILN_CAP_REASON_SYMBOL_MISSING,"owner liveness requires the core world tick");return KILN_ERR_UNAVAILABLE;}
        if(preview.reply.disposition==KILN_DEFINITION_REGISTERED && !track_pool_owner(definition.guid,owner))return KILN_ERR_UNAVAILABLE;
        auto committed=g_pool_registrations.commit(std::move(preview),persist_pool);
        // its stored envelope went at this start's prune: the mod asks for it again, so the next start has it again
        if(!restore_pruned_pool(g_pool_pruned,committed,persist_pool)) {
            WT_E("worldtables: %s: %s: could not restore its stored definition for the next start",definition.mod.c_str(),definition.block.c_str());
            return KILN_ERR_INTERNAL;
        }
        if(committed.reply.ok)g_defined.insert(definition.mod+"/@pool/"+definition.block);
        const uint32_t size=out->size;*out=committed.reply;out->size=size;
        return give_text(committed.report(),report,cap,out_len);
    } catch(const nlohmann::json::exception&){return KILN_ERR_INVALID_ARGUMENT;}
      catch(const std::exception& e){WT_E("worldtables: define_pool_result: %s",e.what());return KILN_ERR_INTERNAL;}
}
KilnResult KILN_CALL t_pool_registration(KilnOwner owner,const uint8_t id[16],KilnDefinitionReply* out,char* report,size_t cap,size_t* out_len) {
    if(!id || !out || out->size<sizeof *out || !out_len)return KILN_ERR_INVALID_ARGUMENT;
    std::array<uint8_t,16> key{};std::memcpy(key.data(),id,16);
    if(std::all_of(key.begin(),key.end(),[](uint8_t b){return b==0;}))return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);if(!g_pool_cap)return KILN_ERR_UNAVAILABLE;
    const auto receipt=g_pool_registrations.query(key);const auto text=receipt.report();
    *out_len=text.size();if(!report || cap<=text.size())return KILN_ERR_BUFFER_TOO_SMALL;
    // Boot receipts have no runtime owner yet. The managed observer labels them with the exact loaded
    // context, without redefining their immutable row. Never hand a retired real owner's row to a reload.
    if(owner && receipt.reply.disposition==KILN_DEFINITION_REGISTERED && !g_pool_owners.count(receipt.definition.guid)) {
        const auto check=g_api->post_world(owner,g_client?KILN_WORLD_CLIENT:KILN_WORLD_SERVER,&pool_owner_probe,nullptr);
        if(check!=KILN_OK)return check;
        if(!track_pool_owner(receipt.definition.guid,owner))return KILN_ERR_UNAVAILABLE;
    }
    const uint32_t size=out->size;*out=receipt.reply;out->size=size;
    return give_text(text,report,cap,out_len);
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
    &t_material_budget,
    &t_pool_budget,
    &t_define_pool,
    &t_pool_registration,
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
        const auto folders=mod_folders();
        std::set<std::string> enabled;
        for(const auto& dir:folders) {
            Manifest man;if(!read_manifest(to_path(dir),&man) || (!g_client && man.side=="client") || (boot && boot->out(man.id)))continue;
            enabled.insert(man.id);
        }
        g_pool_replayed=g_pending_boot.replay(pending_base(),enabled);
        // a mod that is not loaded keeps its stored definitions: noted once per start, not refused (nothing is unsafe)
        for(const auto& f:g_pending_boot.files)if(!enabled.count(f.file.mod) && g_kept_noted.insert(to_utf8(f.file.path)).second) {
            const auto note=kept_file_text(to_utf8(f.file.path));
            WT_I("worldtables: %s",note.c_str());g_kept.push_back(note);
        }
        for (const std::string& dir : folders) {
            try {
                read_mod(dir, pending, boot);
            } catch (const kiln::boot::Refusal&) {
                throw;
            } catch (const std::exception& e) {  // a required participant's read that failed refuses it; anything else ends the read as before
                const std::string id = boot ? kiln::boot::mod_id_of(to_path(dir)) : std::string();
                if (boot && boot->strict(id))
                    throw kiln::boot::Refusal(id, "worldtables", "blocks", std::string("kiln.worldtables: reading its blocks failed: ") + e.what());
                throw;
            }
        }
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
    g_pool_defs.clear(); g_custom_pools.clear(); g_grouped.clear(); g_dependencies.clear(); g_game_pool_materials.clear();g_game_pool_materials_read=false;g_has_pool_shapes=false; g_pool_original_ids.clear(); g_pool_replayed.clear();
    g_pool_owners.clear();g_pool_registrations.receipts.clear();++g_pool_registrations.revision;g_budgets_ready=false;
    g_craft_read.clear();
    g_boot_definitions.clear();
    g_from_code.clear();
    g_pending_dirs.clear();
    g_errors.clear();g_logged_errors.clear();
    g_boot_required.clear();
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
        g_boot_required.clear();
        for (const auto& [key, def] : g_boot_definitions) {
            const std::string mod = key.substr(0, key.find('/'));
            if (input.strict(mod)) g_boot_required.insert(mod);
        }
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
    g_pending_boot.capture(pending_base()); // before managed OnBoot/OnLoad; never recaptured
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
        report_pool_caps(KILN_CAP_REASON_POLICY,g_off_reason);
        WT_I("worldtables: off by kiln/config/worldtables.json: the game's tables are untouched");
        return KILN_OK;
    }
    if (!bi.hooks_enabled) {
        g_off_reason = "hooks disabled (safe idle)";
        report_pool_caps(KILN_CAP_REASON_POLICY,g_off_reason);
        WT_I("worldtables: hooks disabled (safe idle): the game's tables are untouched");
        return KILN_OK;
    }
    std::string layout_why;
    g_layouts_ok=read_layouts(own_base(),own_size(),g_test_missing_field,&g_layouts,&g_item_layout,&layout_why);
    g_pool_layout_ok=g_layouts_ok && g_layouts.pool_id!=UINT32_MAX && g_layouts.item_pool_ref!=UINT32_MAX;
    g_item_pool=g_layouts.item_pool_ref;g_pool_reason=g_layouts_ok?g_layouts.pool_reason:layout_why;
    g_budget_cap=g_layouts_ok;g_pool_cap=g_pool_layout_ok;
    report_pool_caps(KILN_CAP_REASON_SYMBOL_MISSING,g_pool_reason);
    if(!g_pool_layout_ok)WT_W("worldtables.material_pools unavailable on this executable: %s",g_pool_reason.c_str());
    info->on_ready = &on_ready;
    KilnHandle h = 0;
    api->subscribe_tick(owner, KILN_TICK_PRE, &on_tick, nullptr, &h);
    api->subscribe_world(owner, &on_world, nullptr, &h);
    return KILN_OK;
}
