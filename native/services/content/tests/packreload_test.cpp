// SPDX-License-Identifier: MIT
// kiln.content, host test: a mod's data packs follow the mod's load, not only the set of loaded mod ids (live server,
// Kiln 0.2.0-dev.31.10: a data-only mod loaded with `load`, its kiln.mod.json changed, then `reload <id>`: no line, the
// old layer stayed). content.cpp's Pre, pack_mods and pack_mod_reloaded over the real packs.cpp (discovery in a
// temporary kiln/mods, the apply), store.cpp (layers over the game's original bytes) and refl.cpp, with an item
// registry of two items in a made-up memory:
//  - world load applies the pack; a reload of the same id (pack_mods unchanged) re-applies the current files, with one
//    line naming the mod and the reason; the old layer comes off (a field the new version no longer sets goes back)
//  - the generation grows (the content sync rebuilds the clients' state from it); reload_pending says it is queued
//  - a reload whose new files cannot all be read keeps the mod's previous packs whole (old layer, never half)
//  - unload reverts, an unload and a load before the next Pre apply the current files, as before
//  - a reload of a mod without data packs, or before the world is ready, applies nothing
//  - kiln.mod.json changing without a reload (a hot edit) is a pack change too: the scan re-applies
//  - a pack that throws while it applies: its mod's layers of that application come off, its packs from before apply
//    again whole (also when they had ordinary field errors), with their registry sections, else none of its packs apply
//  - a pack that throws leaves nothing of its mod behind: what its "add" lists defined or changed comes
//    off at once, in the mod's place in the load order, so a later mod applies over the state without it (new content is
//    a model of newcontent.cpp's bookkeeping and pack journal here; packundo_test.cpp runs the real one)
//  - built with kiln.gamedata's hooks (packreload_test.sh's second build): its template clones, config adds and other
//    services' sections come off the same way (a model again; gdundo_test.cpp runs the real ones)
//  - a pack that throws after an earlier pack of its mod switched the stack guard off: the guard is as before the reload
//  - 300 reloads (good, broken, throwing): no new owners, nothing left queued
// packreload_test.sh builds and runs it.
#include <windows.h>

#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
#include <unistd.h>

// What content.cpp takes from <windows.h> beyond host/windows.h; libstdc++ already defines __try as `try`.
static unsigned long long g_now_ms = 1000;
inline unsigned long long GetTickCount64() { return g_now_ms; }
inline unsigned long GetExceptionCode() { return 0; }
#define EXCEPTION_EXECUTE_HANDLER 1
#define __except(...) catch (...)
#define __declspec(x)
struct MEMORY_BASIC_INFORMATION {
    void *BaseAddress, *AllocationBase;
    unsigned long Protect, Type;
    size_t RegionSize;
};
#define MEM_PRIVATE 0x20000
#define MEM_IMAGE 0x1000000
inline size_t VirtualQuery(const void*, MEMORY_BASIC_INFORMATION*, size_t) { return 0; }

#include "services/kiln_gamedata.h"
#include "../content.cpp"

namespace fs = std::filesystem;

// ============================================================================================= the fake game =====
namespace fake {
std::map<uintptr_t, uint8_t> mem;  // the game's memory, byte by byte
std::vector<std::string> lines;    // kiln.log
std::string mods;                  // <temp>/kiln/mods
KilnOwner next_owner = 100;

void KILN_CALL log(KilnOwner, int32_t, KilnStr text) { lines.emplace_back(text.ptr, text.len); }
bool KILN_CALL on_world_thread(uint32_t) { return true; }
const void* KILN_CALL service(KilnStr, uint32_t) { return nullptr; }  // no kiln.profile: kiln/mods in folder order
KilnOwner KILN_CALL owner_create(KilnStr) { return next_owner++; }
KilnResult KILN_CALL post_world(KilnOwner, uint32_t, KilnTaskFn, void*) { return KILN_OK; }
KilnResult KILN_CALL path(int32_t kind, char* buf, size_t cap, size_t* len) {
    const std::string p = kind == KILN_PATH_MODS ? mods : fs::path(mods).parent_path().string() + "/data";
    if (p.size() >= cap) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buf, p.data(), p.size());
    *len = p.size();
    return KILN_OK;
}
KilnApi api = [] {
    KilnApi a{};
    a.size = sizeof a;
    a.log = &log;
    a.on_world_thread = &on_world_thread;
    a.service = &service;
    a.owner_create = &owner_create;
    a.post_world = &post_world;
    a.path = &path;
    return a;
}();

uint32_t read32(uintptr_t a) {
    uint32_t v = 0;
    for (int i = 0; i < 4; ++i) v |= uint32_t(mem[a + i]) << (8 * i);
    return v;
}
void write32(uintptr_t a, uint32_t v) {
    for (int i = 0; i < 4; ++i) mem[a + i] = uint8_t(v >> (8 * i));
}
}  // namespace fake

namespace kc {
// game.cpp's memory access, over fake::mem
bool rd(uintptr_t address, void* out, size_t n) {
    auto* o = static_cast<uint8_t*>(out);
    for (size_t i = 0; i < n; ++i) {
        auto it = fake::mem.find(address + i);
        if (it == fake::mem.end()) return false;
        o[i] = it->second;
    }
    return true;
}
size_t write_batch(std::vector<Write>& writes, uintptr_t*) {
    for (const Write& w : writes)
        for (uint8_t i = 0; i < w.n; ++i) fake::mem[w.addr + i] = w.bytes[i];
    return writes.size();
}
WriteStats write_stats() { return {}; }
// the stack clamp's `jbe` (0x76; the guard makes it a `jmp`, 0xEB): only reached once a case sets E.guard
uint8_t g_clamp_byte = 0x76;
bool patch_code_byte(uintptr_t, uint8_t expect, uint8_t to) {
    if (g_clamp_byte != expect) return false;
    g_clamp_byte = to;
    return true;
}
void* near_alloc(uintptr_t, size_t) { return nullptr; }
uint32_t fnv1a(const void* p, size_t n) {
    uint32_t h = 2166136261u;
    for (size_t i = 0; i < n; i++) h = (h ^ static_cast<const uint8_t*>(p)[i]) * 16777619u;
    return h;
}
std::string lower(std::string s) {
    for (char& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}
std::string guid_text(const uint8_t*) { return "00000000-0000-0000-0000-000000000000"; }

// registries.cpp: one item registry (items Wood and Stone; field maxStackSize, u32 at +0)
Registry g_regs[5];
Registry& reg(uint32_t kind) { return g_regs[valid_kind(kind) ? kind : 0]; }
bool valid_kind(uint32_t k) { return k >= KILN_CONTENT_ITEMS && k <= KILN_CONTENT_DROPS; }
void Registry::add(Entry e) {
    const uint32_t i = static_cast<uint32_t>(entries.size());
    by_id[e.id] = i, by_name[e.name] = i, by_lower[lower(e.name)] = i, by_addr[e.addr] = i;
    entries.push_back(std::move(e));
}
const Entry* Registry::get(uint32_t id) const {
    const auto it = by_id.find(id);
    return it == by_id.end() ? nullptr : &entries[it->second];
}
const Entry* Registry::lookup(const std::string& key) const {
    if (auto it = by_name.find(key); it != by_name.end()) return &entries[it->second];
    if (auto it = by_lower.find(lower(key)); it != by_lower.end()) return &entries[it->second];
    return nullptr;
}
bool read_recipe_stacks(const Entry&, uint32_t, std::vector<KilnContentStack>*) { return false; }
bool walk_drop_table(uintptr_t, std::vector<DropStackRef>*) { return false; }
uint32_t map_count(uintptr_t) { return 0; }
void read_drop_stack(uintptr_t, uint32_t, KilnContentStack*) {}
bool build_registries(uintptr_t, std::string* why) {
    *why = "not in this test";
    return false;
}
Engine E;
RecipeLayout RL;
DropLayout DL;
std::unordered_map<uint32_t, std::vector<uint8_t>> g_category_guid;

// What the packs reach in the service's other files: no kitbash, no registry stage in this test. New content is a model of
// newcontent.cpp's bookkeeping (definitions by name: owner, seen in this application, orphaned) and of its pack journal
// (nc_pack_mark / rollback / unmark, content.h), so the cases see what packs.cpp asks of it and when.
namespace model {
struct Def {
    KilnOwner owner = 0;
    std::string v;  // the definition's "v": which version of the pack defined it
    bool seen = true, orphan = false;
};
std::map<std::string, Def> defs;
KilnOwner journal_owner = 0;
std::vector<std::pair<std::string, std::optional<Def>>> journal;
std::vector<std::string> events;  // "define <name>", "nc rollback <owner>", ... in order
bool active(const std::string& name) {
    const auto it = defs.find(name);
    return it != defs.end() && !it->second.orphan;
}
std::string v_of(const std::string& name) { return defs.count(name) ? defs[name].v : std::string(); }
}  // namespace model
void nc_packs_begin() {
    for (auto& [n, d] : model::defs) d.seen = false;
}
void nc_packs_end() {
    for (auto& [n, d] : model::defs)
        if (!d.seen) d.orphan = true;
}
void nc_pack_mark(KilnOwner owner) {
    model::journal_owner = owner;
    model::journal.clear();
}
uint32_t nc_pack_rollback(KilnOwner owner) {
    model::events.push_back("nc rollback " + std::to_string(owner));
    if (owner != model::journal_owner) return 0;
    const uint32_t n = static_cast<uint32_t>(model::journal.size());
    for (auto it = model::journal.rbegin(); it != model::journal.rend(); ++it) {
        if (it->second) model::defs[it->first] = *it->second;
        else model::defs[it->first].orphan = true, model::defs[it->first].seen = false;
    }
    model::journal.clear();
    return n;
}
void nc_pack_unmark() { model::journal_owner = 0; }
// An "add" list defines {"name", "v"} entries; an entry {"throw": "<what>"} throws while its pack applies (a section's code
// meeting a value type it did not expect): what the list defined before it, and the rules before it, have applied already.
void nc_pack_add(KilnOwner owner, uint32_t, const std::string& list, const std::string&, ApplyResult* out) {
    const nlohmann::ordered_json l = nlohmann::ordered_json::parse(list);
    for (const nlohmann::ordered_json& e : l) {
        if (e.contains("throw")) throw std::runtime_error("[json.exception.type_error.302] type must be string, but is number");
        const std::string name = e.value("name", "?");
        if (owner == model::journal_owner &&
            std::none_of(model::journal.begin(), model::journal.end(), [&](const auto& j) { return j.first == name; }))
            model::journal.emplace_back(name, model::defs.count(name) ? std::optional<model::Def>(model::defs[name]) : std::nullopt);
        model::defs[name] = model::Def{owner, e.value("v", ""), true, false};
        model::events.push_back("define " + name);
        ++out->applied;
    }
}
#if KC_GAMEDATA
using gdjson = nlohmann::ordered_json;
// packreload_test.sh's second build (-DKILN_CONTENT_GAMEDATA): kiln.gamedata as a model of what a pack leaves outside the
// store's layers, so the cases see what packs.cpp asks of it and when: template clones by name ("templates": {"add":
// [{"name", "v"}]}), config adds (an item rule's "impactValues.<x>" path: gd_item_path), and other services' sections
// ("creatures": {...}: the service's state per owner; {"throw": 1} in one throws while it applies), with the pack journal
// of gamedata.h (gd_pack_mark / rollback / unmark).
namespace gdmodel {
struct Clone {
    KilnOwner owner = 0;
    std::string v;
    bool seen = true, orphan = false;
};
std::map<std::string, Clone> clones;
std::map<std::pair<KilnOwner, std::string>, gdjson> added;   // config adds: (owner, "<item>.<path>") -> value
std::map<std::pair<std::string, KilnOwner>, std::string> service, cur, prev;  // (section, owner) -> the json it applied
KilnOwner journal_owner = 0;
std::vector<std::pair<std::string, std::optional<Clone>>> journal;
}  // namespace gdmodel
void gd_packs_begin() {
    for (auto& [n, c] : gdmodel::clones) c.seen = false;
    gdmodel::added.clear();  // every pack owner's layers were dropped
    gdmodel::cur.clear();
}
void gd_packs_end() {
    for (auto& [n, c] : gdmodel::clones)
        if (!c.seen) c.orphan = true;
    for (const auto& [k, v] : gdmodel::prev)
        if (!gdmodel::cur.count(k)) gdmodel::service.erase(k);
    gdmodel::prev = gdmodel::cur;
}
void gd_pack_mark(KilnOwner owner) {
    gdmodel::journal_owner = owner;
    gdmodel::journal.clear();
}
uint32_t gd_pack_rollback(KilnOwner owner) {
    model::events.push_back("gd rollback " + std::to_string(owner));
    if (owner != gdmodel::journal_owner) return 0;
    uint32_t n = 0;
    for (auto it = gdmodel::journal.rbegin(); it != gdmodel::journal.rend(); ++it, ++n) {
        if (it->second) gdmodel::clones[it->first] = *it->second;
        else gdmodel::clones[it->first].orphan = true, gdmodel::clones[it->first].seen = false;
    }
    gdmodel::journal.clear();
    std::erase_if(gdmodel::added, [&](const auto& kv) { return kv.first.first == owner; });
    std::erase_if(gdmodel::cur, [&](const auto& kv) { return kv.first.second == owner; });
    std::erase_if(gdmodel::service, [&](const auto& kv) { return kv.first.second == owner; });
    return n;
}
void gd_pack_unmark() { gdmodel::journal_owner = 0; }
void gd_pack_adds(KilnOwner owner, uint64_t, const gdjson& doc, const std::string&, ApplyResult* out) {
    if (!doc.contains("templates") || !doc["templates"].contains("add")) return;
    for (const gdjson& e : doc["templates"]["add"]) {
        const std::string name = e.value("name", "?");
        if (owner == gdmodel::journal_owner &&
            std::none_of(gdmodel::journal.begin(), gdmodel::journal.end(), [&](const auto& j) { return j.first == name; }))
            gdmodel::journal.emplace_back(name, gdmodel::clones.count(name) ? std::optional<gdmodel::Clone>(gdmodel::clones[name]) : std::nullopt);
        gdmodel::clones[name] = gdmodel::Clone{owner, e.value("v", ""), true, false};
        model::events.push_back("clone " + name);
        ++out->applied;
    }
}
int gd_item_path(KilnOwner owner, uint64_t, const Entry& e, const std::string& path, const gdjson& val, const std::string&, std::string*) {
    if (path.rfind("impactValues.", 0) != 0) return 0;
    gdmodel::added[{owner, e.name + "." + path}] = val;  // a config the item does not have yet: added to its array
    return 1;
}
void gd_pack_sections(KilnOwner owner, uint64_t, const gdjson& doc, const std::string&, ApplyResult* out) {
    for (const char* k : {"abilities", "creatures"}) {  // in document order is all the model needs: these two, in this order
        if (!doc.contains(k)) continue;
        if (doc[k].contains("throw")) throw std::runtime_error(std::string("[json.exception.type_error.302] ") + k);
        gdmodel::service[{k, owner}] = doc[k].dump();
        gdmodel::cur[{k, owner}] = doc[k].dump();
        model::events.push_back(std::string("section ") + k);
        ++out->applied;
    }
}
bool gd_is_section(const std::string& k) { return k == "templates" || k == "abilities" || k == "creatures"; }
uint32_t gd_export(gdjson&, uint32_t) { return 0; }
void gd_boot() {}
void gd_boot_patches_bind() {}
void gd_world_ready(uintptr_t, uint32_t) {}
void gd_world_reset() {}
void gd_tick(uint64_t) {}
void gd_owner_gone(KilnOwner) {}
void gd_owner_revoked(KilnOwner) {}
std::string gd_slot_what(const Slot&, std::string* what) {
    *what = "?";
    return "gamedata";
}
std::string gd_layer_value(const Slot&, const uint8_t*) { return "?"; }
namespace gd {
void after_changes() {}
void pending_mods_changed(const std::vector<std::string>&) {}
void pending_owner_loaded(KilnOwner, const std::string&) {}
KilnResult clones_define(KilnOwner, uint32_t, const std::string&, bool, const std::string&, std::string*) { return KILN_ERR_UNAVAILABLE; }
}  // namespace gd
#endif
bool nc_is_new(uint32_t, uint32_t) { return false; }
void nc_drops_table(const Entry&) {}
void nc_world_ready(uintptr_t, uint32_t) {}
void nc_world_stopping(uintptr_t) {}
KilnResult nc_define(KilnOwner, uint32_t, const std::string&, bool, const std::string&, std::string*) { return KILN_ERR_UNAVAILABLE; }
std::string nc_definitions_json() { return "[]"; }
bool nc_icon_alias(const uint8_t[16], const uint8_t[16]) { return false; }
std::string nc_info_json(const std::string&) { return "{}"; }
uint32_t nc_undefine_owner(KilnOwner) { return 0; }
uint32_t nc_forget_unbound(KilnOwner) { return 0; }
void nc_boot_failed(std::vector<BootFailed>*) {}
bool nc_lab_refuse(const std::string&, const std::string&) { return false; }
void nc_boot() {}
void nc_text_boot() {}
bool nc_menu_render() { return false; }
KilnResult KILN_CALL nc_text_api(uint32_t, char*, size_t, size_t*) { return KILN_ERR_UNAVAILABLE; }
KilnResult KILN_CALL nc_text_info_api(KilnContentTextInfo*) { return KILN_ERR_UNAVAILABLE; }
bool is_kitbash_key(const Schema&, const std::string&) { return false; }
void kitbash(const Schema&, const std::string&, const nlohmann::ordered_json&, const ItemFind&, const std::vector<uintptr_t>&, KitbashOut*, uintptr_t) {}
std::string ref_text(const uint8_t*) { return "none"; }
// The registry stage: a schema (so the packs' registry sections are lowered and handed over), and what it was handed.
std::shared_ptr<const registry::Schema> registry_live_schema() {
    static const auto schema = std::make_shared<const registry::Schema>();
    return schema;
}
std::vector<RegistrySection> g_staged;  // the sections of the last application
void registry_live_apply(const std::vector<RegistrySection>& sections, const registry::Schema*) { g_staged = sections; }
void registry_live_publish() {}
void registry_live_world_begin(uint32_t) {}
void registry_live_world_stop() {}
void drops_table_loaded(const Entry&) {}
void refs_reset() {}
std::string refs_info_json(const Schema&, const std::vector<uintptr_t>&) { return {}; }
const registry::Schema& registry_schema();
namespace registry {
bool lower_section(const json&, const std::string&, const std::string&, const std::string&, const Schema&, std::vector<Batch>*,
                   std::vector<Refusal>*, std::string* err) {
    *err = "no registry stage in this test";
    return false;
}
Schema generated_schema() { return {}; }
void link_elements(Schema*) {}
namespace stage {
void hooks_tick() {}  // content.cpp pre: other mods' hooks next to a limits value (dev.32); no limits hooks in this test
}  // namespace stage
}  // namespace registry
namespace ml {
void probe_owners() {}
}  // namespace ml
}  // namespace kc

// ============================================================================================= the cases =========
namespace {

int failures = 0;
void check(bool ok, const std::string& what) {
    std::printf("  %s %s\n", ok ? "ok  " : "FAIL", what.c_str());
    if (!ok) ++failures;
}
bool logged(const std::string& part) {
    for (const auto& l : fake::lines)
        if (l.find(part) != std::string::npos) return true;
    return false;
}
size_t count_logged(const std::string& part) {
    size_t n = 0;
    for (const auto& l : fake::lines) n += l.find(part) != std::string::npos;
    return n;
}

constexpr uintptr_t kWood = 0x1000, kStone = 0x2000;
fs::path g_root;

void write(const fs::path& p, const std::string& text) {
    fs::create_directories(p.parent_path());
    std::ofstream(p, std::ios::binary | std::ios::trunc) << text;
}
fs::path mod_dir(const std::string& folder) { return g_root / "kiln" / "mods" / folder; }
void manifest(const std::string& folder, const std::string& id, const std::string& version) {
    write(mod_dir(folder) / "kiln.mod.json", "{\"id\": \"" + id + "\", \"name\": \"" + id + "\", \"version\": \"" + version + "\"}");
}
void pack(const std::string& folder, const std::string& file, const std::string& text) {
    write(mod_dir(folder) / "content" / file, text);
}
// A file rewritten with the same size and modification time as before: the 2-s scan cannot see it (a deploy that
// keeps times, or two writes in one timestamp tick). Only the mod's load can say the files are new.
void same_stamp(const std::string& folder, const std::string& file, const std::string& text) {
    const fs::path p = mod_dir(folder) / "content" / file;
    const auto size = fs::file_size(p);
    const auto t = fs::last_write_time(p);
    std::string padded = text;
    if (padded.size() > size) {
        std::printf("  (test bug: %s longer than the file it replaces)\n", file.c_str());
        std::abort();
    }
    padded.append(size - padded.size(), ' ');
    write(p, padded);
    fs::last_write_time(p, t);
}
void pack_mods(const std::string& json) { t_pack_mods(KilnStr{json.data(), json.size()}); }
KilnResult reloaded(const std::string& mod, const std::string& reason) {
    return t_pack_mod_reloaded(KilnStr{mod.data(), mod.size()}, KilnStr{reason.data(), reason.size()});
}

uint64_t g_steps = 1;
void step() {
    ++g_steps;
    if (g_steps % 25 == 0 || g_steps % 250 == 0) ++g_steps;  // no owner probe or timing lines in these steps
    KilnTickInfo t{};
    t.size = sizeof t;
    t.step = g_steps;
    pre(&t, KILN_WORLD_SERVER, 0x5000);
}
uint64_t generation() { return g_generation.load(); }
bool pending() {
    KilnContentStatus s{};
    s.size = sizeof s;
    t_status(&s);
    return s.reload_pending != 0;
}

// The world's registries loaded: what try_ready does once build_registries succeeded, then apply_packs(false).
void world_load() {
    Registry& items = reg(KILN_CONTENT_ITEMS);
    items.kind = KILN_CONTENT_ITEMS;
    items.label = "items";
    items.available = true;
    Field f;
    f.path = "maxStackSize";
    f.type_name = "uint32";
    f.offset = 0;
    f.bytes = 4;
    f.vt = KILN_CV_INT;
    f.is_signed = false;
    items.schema.type = "keen::ItemInfo";
    items.schema.size = 16;
    items.schema.fields.push_back(f);
    items.schema.by_path["maxStackSize"] = 0;
    items.schema.by_lower["maxstacksize"] = 0;
    for (const auto& [name, addr] : {std::pair<const char*, uintptr_t>{"Wood", kWood}, {"Stone", kStone}}) {
        Entry e;
        e.name = name;
        e.id = fnv1a(e.name);
        e.addr = addr;
        items.add(e);
    }
    fake::write32(kWood, 10);
    fake::write32(kStone, 20);
    g_kind.store(KILN_WORLD_SERVER);
    g_world.store(0x5000);
    apply_packs(false);
    g_ready.store(true);
}

void setup() {
    g_root = fs::temp_directory_path() / ("kiln-packreload-" + std::to_string(::getpid()));
    fs::remove_all(g_root);
    fs::create_directories(g_root / "kiln" / "mods");
    fake::mods = (g_root / "kiln" / "mods").string();
    kc::g_api = &fake::api;
}

void run() {
    setup();
    // a data-only mod, hot-loaded with `load` (so it is the last loaded mod): content/skills.json
    manifest("skilltest", "skilltest", "1.0.0");
    pack("skilltest", "skills.json", R"({"format": 1, "items": {"Wood": {"maxStackSize": 100}}})");
    manifest("other", "other", "1.0.0");
    pack("other", "other.json", R"({"format": 1, "items": {"Stone": {"maxStackSize": 25}}})");
    pack_mods(R"(["other","skilltest"])");  // Kiln.Content's list, before the world
    world_load();
    check(fake::read32(kWood) == 100 && fake::read32(kStone) == 25, "world load: both mods' packs apply (Wood 100, Stone 25)");
    const uint64_t g0 = generation();
    step();
    check(generation() == g0, "nothing changed: no re-application");

    // ---- a reload: same id, same place in the load order (pack_mods gets the same list), new kiln.mod.json and files
    manifest("skilltest", "skilltest", "1.0.1");
    same_stamp("skilltest", "skills.json", R"({"format": 1, "items": {"Stone": {"maxStackSize": 50}}})");
    pack_mods(R"(["other","skilltest"])");
    check(reloaded("skilltest", "was reloaded (1.0.0 -> 1.0.1)") == KILN_OK, "pack_mod_reloaded takes the mod and the reason");
    check(pending(), "status: reload_pending says a re-application is queued");
    fake::lines.clear();
    step();
    check(logged("mod skilltest was reloaded (1.0.0 -> 1.0.1): re-applying data packs"),
          "one line names the mod and the reason");
    check(count_logged("re-applying data packs") == 1, "exactly one such line");
    check(!pending(), "nothing queued after the Pre");
    check(generation() == g0 + 1, "the generation grew by one (the content sync rebuilds the clients' state from it)");
    check(fake::read32(kWood) == 10, "the old layer came off: Wood, which the new version no longer sets, is back to the game's 10");
    check(fake::read32(kStone) == 50, "the new version's value applies over the earlier mod's (Stone 50, load order kept)");

    // ---- a reload whose new files cannot all be read: the mod's previous packs stay, whole
    pack("skilltest", "more.json", R"({"format": 1, "items": {"Wood": {"maxStackSize": 70}}})");
    reloaded("skilltest", "was reloaded");
    step();
    check(fake::read32(kWood) == 70 && fake::read32(kStone) == 50, "a second file of the mod applies with the first (Wood 70, Stone 50)");
    const uint64_t g1 = generation();
    same_stamp("skilltest", "skills.json", R"({"format": 1, "items": {"Stone": {"maxStackSize": 60}}})");
    pack("skilltest", "more.json", R"({"format": 1, "items": {"Wood": {"maxStackSize": 7)");  // cut off: the deploy was still writing
    reloaded("skilltest", "was reloaded (1.0.1 -> 1.0.2)");
    fake::lines.clear();
    step();
    check(generation() == g1 + 1, "the reload re-applied");
    check(fake::read32(kWood) == 70 && fake::read32(kStone) == 50,
          "one file of the new version is broken: the mod keeps its previous packs whole (Wood 70, Stone 50; not Stone 60)");
    check(logged("data packs of skilltest: ") && logged("the packs it had before stay applied, whole"),
          "a line says the mod's previous packs stay, and why");
    pack("skilltest", "more.json", R"({"format": 1, "items": {"Wood": {"maxStackSize": 80}}})");
    reloaded("skilltest", "was reloaded");
    step();
    check(fake::read32(kWood) == 80 && fake::read32(kStone) == 60, "once every file reads, the new version applies whole (Wood 80, Stone 60)");

    // ---- unload reverts; an unload and a load before the next Pre apply the current files
    pack_mods(R"(["other"])");
    step();
    check(fake::read32(kWood) == 10 && fake::read32(kStone) == 25, "unload: the mod's layer comes off (Wood 10, Stone back to other's 25)");
    std::filesystem::remove(mod_dir("skilltest") / "content" / "more.json");
    pack("skilltest", "skills.json", R"({"format": 1, "items": {"Wood": {"maxStackSize": 90}}})");
    reloaded("skilltest", "was reloaded");  // not in the loaded set: nothing to do for it
    const uint64_t g2 = generation();
    step();
    check(generation() == g2, "a reload line for a mod that is not loaded re-applies nothing");
    pack_mods(R"([])");
    pack_mods(R"(["other","skilltest"])");
    step();
    check(fake::read32(kWood) == 90 && fake::read32(kStone) == 25, "an unload and a load between two Pres apply the current files (Wood 90)");

    // ---- a mod without data packs, and before the world is ready
    const uint64_t g3 = generation();
    reloaded("codeonly", "was reloaded");
    step();
    check(generation() == g3, "a reload of a mod that has no data packs re-applies nothing");
    check(reloaded("", "was reloaded") == KILN_ERR_INVALID_ARGUMENT, "an empty mod id is refused");

    // ---- kiln.mod.json changing without a reload (a hot edit): the scan re-applies
    step();  // the first scan (records the signature)
    const uint64_t g4 = generation();
    manifest("skilltest", "skilltest", "1.0.3");
    g_now_ms += 2500;
    fake::lines.clear();
    step();
    check(generation() == g4 + 1 && logged("data pack files changed: re-applying"),
          "a kiln.mod.json edit (version) is a pack change: the 2-s scan re-applies");

    // ---- a pack that throws while it applies (nobody had tested this branch): none of that application's layers of the
    // mod stay; its packs from before apply again, whole, with their registry sections
    {
        // the version before: a field error (an ordinary problem, reported, not a throw) and a registry section
        pack("skilltest", "skills.json",
             R"({"format": 1, "items": {"Wood": {"maxStackSize": 90, "noSuchField": 1}}, "registry": {"v": "old"}})");
        reloaded("skilltest", "was reloaded");
        step();
        check(fake::read32(kWood) == 90 && fake::read32(kStone) == 25, "throw: the version before applies (Wood 90, Stone 25)");
        auto staged_of = [](const std::string& mod) {
            std::vector<uint64_t> h;
            for (const RegistrySection& s : g_staged)
                if (s.mod == mod) h.push_back(s.hash);
            return h;
        };
        const std::vector<uint64_t> old_sections = staged_of("skilltest");
        check(old_sections.size() == 1, "throw: the registry stage got the version's one registry section");
        // the new version: Stone 33 applies, then its "add" list throws
        pack("skilltest", "skills.json",
             R"({"format": 1, "items": {"Stone": {"maxStackSize": 33}, "add": [{"throw": 1}]}, "registry": {"v": "new"}})");
        reloaded("skilltest", "was reloaded (1.0.3 -> 1.0.4)");
        fake::lines.clear();
        step();
        check(fake::read32(kStone) == 25, "throw: the layer the throwing pack wrote before it threw comes off (Stone back to other's 25)");
        check(fake::read32(kWood) == 90, "throw: the packs from before apply again, whole, though they had an ordinary field error (Wood 90)");
        check(logged("a pack threw while it applied: what it did came off") && logged("; the packs it had before apply again, whole"),
              "throw: a line says the packs from before apply again");
        check(staged_of("skilltest") == old_sections,
              "throw: the registry stage gets the section of the packs that apply (the one from before), not the throwing pack's");
        check(g_report.find("applying it failed") != std::string::npos && g_report.find("type_error") != std::string::npos,
              "throw: the report names the file and the throw");
        // registry-fixes item 3: the report lists the mod set apart, the pack that threw and whether its packs from before
        // apply again (the reload reply of Kiln.Host reads it): {"setApart": [{"mod", "file", "reason", "previous"}]}
        auto set_apart = [] {
            const nlohmann::json j = nlohmann::json::parse(g_report, nullptr, false);
            return j.is_object() && j.contains("setApart") && j["setApart"].is_array() ? j["setApart"] : nlohmann::json(nullptr);
        };
        {
            const nlohmann::json rows = set_apart();
            check(rows.is_array() && rows.size() == 1 && rows[0].value("mod", "") == "skilltest" &&
                      rows[0].value("file", "") == "skilltest/content/skills.json" &&
                      rows[0].value("reason", "").find("type_error") != std::string::npos && rows[0].value("previous", false) == true,
                  "throw: the report's setApart row names the mod, the file, the throw, and that its packs from before apply again: " + rows.dump());
        }
        // and once more: the bookkeeping still holds the version before (not the one that threw)
        reloaded("skilltest", "was reloaded");
        step();
        check(fake::read32(kWood) == 90 && fake::read32(kStone) == 25, "throw, again: the version before still applies");
        // no version before (the mod's packs came off with an unload): the throwing pack's mod applies nothing
        pack_mods(R"(["other"])");
        step();
        pack_mods(R"(["other","skilltest"])");
        fake::lines.clear();
        step();
        check(fake::read32(kWood) == 10 && fake::read32(kStone) == 25, "throw without a version before: none of the mod's packs apply (Wood 10, Stone 25)");
        check(logged("a pack threw while it applied: what it did came off") && logged("; none of its packs apply"),
              "throw without a version before: a line says none apply");
        check(staged_of("skilltest").empty(), "throw without a version before: no registry section of the mod reaches the stage");
        {
            const nlohmann::json rows = set_apart();
            check(rows.is_array() && rows.size() == 1 && rows[0].value("mod", "") == "skilltest" && rows[0].contains("previous") &&
                      rows[0]["previous"] == false,
                  "throw without a version before: the setApart row says none of its packs apply (previous false): " + rows.dump());
        }
        // fixed: the new version applies
        pack("skilltest", "skills.json", R"({"format": 1, "items": {"Wood": {"maxStackSize": 90}}})");
        reloaded("skilltest", "was reloaded");
        step();
        check(fake::read32(kWood) == 90 && fake::read32(kStone) == 25, "throw fixed: the new version applies (Wood 90)");
        check(set_apart().is_array() && set_apart().empty(), "throw fixed: no mod is set apart in this application: " + set_apart().dump());
    }

    // ---- a pack that throws leaves nothing of its mod behind: what its "add" lists defined or changed,
    // its template clones, config adds and other services' sections come off with its layers, at once, in its place in the
    // load order: a later mod applies over the state without it
    {
        manifest("late", "late", "1.0.0");
        pack("late", "late.json", R"({"format": 1, "items": {"add": [{"name": "Late", "v": "1"}]}})");
        pack("skilltest", "skills.json",
             R"({"format": 1, "items": {"Wood": {"maxStackSize": 90}, "add": [{"name": "Axe", "v": "old"}, {"name": "Sword", "v": "old"}]},
                 "templates": {"add": [{"name": "TplA", "v": "old"}]}, "abilities": {"v": "old"}})");
        pack_mods(R"(["other","skilltest","late"])");
        reloaded("skilltest", "was reloaded");
        step();
        check(model::active("Axe") && model::v_of("Sword") == "old" && model::active("Late"), "undo: the version before defines Axe and Sword, late defines Late");
#if KC_GAMEDATA
        check(gdmodel::clones.count("TplA") && gdmodel::clones["TplA"].v == "old" && !gdmodel::clones["TplA"].orphan, "undo: the version before clones TplA");
        const auto service_of = [](const std::string& section) {
            for (const auto& [k, v] : gdmodel::service)
                if (k.first == section) return v;
            return std::string();
        };
        check(service_of("abilities") == R"({"v":"old"})", "undo: the version before applies its abilities section");
        // the new version: clones TplA again and TplB, adds a config to Wood, redefines Sword, adds Pick, applies its
        // abilities section, then its creatures section throws
        pack("skilltest", "skills.json",
             R"({"format": 1, "templates": {"add": [{"name": "TplA", "v": "new"}, {"name": "TplB", "v": "new"}]},
                 "items": {"Wood": {"impactValues.Foo": 5}, "add": [{"name": "Sword", "v": "new"}, {"name": "Pick", "v": "new"}]},
                 "abilities": {"v": "new"}, "creatures": {"throw": 1}})");
#else
        // the new version: redefines Sword, adds Pick, then its "add" list throws
        pack("skilltest", "skills.json",
             R"({"format": 1, "items": {"add": [{"name": "Sword", "v": "new"}, {"name": "Pick", "v": "new"}, {"throw": 1}]}})");
#endif
        reloaded("skilltest", "was reloaded");
        model::events.clear();
        fake::lines.clear();
        step();
        check(!model::active("Pick"), "undo: an item the throwing pack added comes off (orphaned, as a removed pack's)");
        check(model::active("Sword") && model::v_of("Sword") == "old", "undo: an item the throwing pack redefined is the version before's again");
        check(model::active("Axe") && fake::read32(kWood) == 90, "undo: the packs from before apply again, whole (Axe, Wood 90)");
        check(model::active("Late"), "undo: a later mod's definition is untouched");
        const auto at = [](const std::string& e) {
            const auto it = std::find_if(model::events.begin(), model::events.end(), [&](const std::string& x) { return x.rfind(e, 0) == 0; });
            return it == model::events.end() ? model::events.size() + 1 : static_cast<size_t>(it - model::events.begin());
        };
        check(at("nc rollback") < at("define Late"), "undo: the throwing mod is rolled back in its place, before the next mod applies");
        check(logged("a pack threw while it applied: what it did came off"), "undo: a line says what came off");
#if KC_GAMEDATA
        check(gdmodel::clones["TplB"].orphan, "undo: a template clone the throwing pack added comes off");
        check(!gdmodel::clones["TplA"].orphan && gdmodel::clones["TplA"].v == "old", "undo: a clone the throwing pack redefined is the version before's again");
        check(std::none_of(gdmodel::added.begin(), gdmodel::added.end(), [](const auto& kv) { return kv.first.second == "Wood.impactValues.Foo"; }),
              "undo: a config the throwing pack added to an item is gone");
        check(service_of("abilities") == R"({"v":"old"})", "undo: another service's section the throwing pack applied is the version before's again");
        check(service_of("creatures").empty(), "undo: nothing of the section that threw stays");
        check(at("gd rollback") < at("define Late"), "undo: gamedata is rolled back in its place too");
#endif
        // no version before (the mod's packs came off with an unload): nothing of the throwing pack's mod stays at all
        pack_mods(R"(["other","late"])");
        step();
        pack_mods(R"(["other","skilltest","late"])");
        step();
        check(!model::active("Pick") && !model::active("Sword") && !model::active("Axe") && model::active("Late"),
              "undo without a version before: none of the mod's definitions stay, the later mod's do");
#if KC_GAMEDATA
        check(gdmodel::clones["TplA"].orphan && gdmodel::clones["TplB"].orphan && service_of("abilities").empty() && gdmodel::added.empty(),
              "undo without a version before: none of its clones, config adds or sections stay");
#endif
        pack("skilltest", "skills.json", R"({"format": 1, "items": {"Wood": {"maxStackSize": 90}}})");
        fs::remove_all(mod_dir("late"));
        pack_mods(R"(["other","skilltest"])");
        step();
    }

    // ---- the stack guard is the whole game's: a pack of the mod that throws took it off ("options": {"stackGuard": false})
    // before a later pack of the mod threw. It is as before the reload again (on: the game keeps stacks above an item's max),
    // not off until the next application, with saved stacks cut down meanwhile.
    {
        E.guard = true, E.stack_clamp = 0x9000;
        pack("skilltest", "skills.json", R"({"format": 1, "items": {"Wood": {"maxStackSize": 5}}})");  // lowers: no reason for the guard
        reloaded("skilltest", "was reloaded");
        step();
        std::string why;
        check(guard_set(true, &why) && guard_on(), "guard: on before the reload (another mod raised a max stack)");
        pack("skilltest", "skills.json", R"({"format": 1, "options": {"stackGuard": false}, "items": {"Wood": {"maxStackSize": 5}}})");
        pack("skilltest", "throws.json", R"({"format": 1, "items": {"add": [{"throw": 1}]}})");
        reloaded("skilltest", "was reloaded");
        fake::lines.clear();
        step();
        check(logged("a pack threw while it applied: what it did came off"), "guard: the mod's later pack threw");
        check(fake::read32(kWood) == 5, "guard: the packs from before apply again (Wood 5)");
        check(guard_on(), "guard: the stack guard the throwing mod's earlier pack switched off is on again, as before the reload");
        fs::remove(mod_dir("skilltest") / "content" / "throws.json");
        pack("skilltest", "skills.json", R"({"format": 1, "items": {"Wood": {"maxStackSize": 90}}})");
        reloaded("skilltest", "was reloaded");
        step();
        guard_set(false, &why);
        E.guard = false, E.stack_clamp = 0;
    }

    // ---- many reloads (good, broken, throwing, in turn): no new owners, nothing left queued, the last good version applies
    {
        const KilnOwner owners = fake::next_owner;
        const uint64_t g = generation();
        for (int i = 0; i < 300; ++i) {
            const int k = i % 3;
            if (k == 0) pack("skilltest", "more.json", R"({"format": 1, "items": {"Stone": {"maxStackSize": 4)");  // cut off
            else if (k == 1) pack("skilltest", "more.json", R"({"format": 1, "items": {"Stone": {"maxStackSize": 5}, "add": [{"throw": 1}]}})");
            else pack("skilltest", "more.json", R"({"format": 1, "items": {"Stone": {"maxStackSize": )" + std::to_string(100 + i) + "}}}");
            reloaded("skilltest", "was reloaded");
            reloaded("skilltest", "was reloaded");  // twice before one Pre: one application
            step();
        }
        check(generation() == g + 300, "300 reloads: one application each (a mod named twice before a Pre applies once)");
        check(fake::next_owner == owners, "300 reloads: no owner created after the first load (one pack owner per mod id)");
        check(!pending() && fake::read32(kWood) == 90 && fake::read32(kStone) == 399, "300 reloads: nothing queued; the last version applies (Wood 90, Stone 399)");
        fs::remove(mod_dir("skilltest") / "content" / "more.json");
        reloaded("skilltest", "was reloaded");
        step();
    }

    // ---- the world goes away: nothing stays queued for the next one
    reloaded("skilltest", "was reloaded");
    world_reset(true, "is stopping");
    check(!pending() && fake::read32(kWood) == 10 && fake::read32(kStone) == 20, "a world reset puts every original back and drops what was queued");
    check(reloaded("skilltest", "was reloaded") == KILN_OK && !pending(), "before the world is ready a reload queues nothing (the world load applies the files)");

    fs::remove_all(g_root);
}

// review2 S5 remainder: Content.Entry.Modified (KilnContentEntry.flags) follows registry layers. A registry write goes
// through the shared store, never through Content's setters, so only the store's generation tells fill_entry's cache
// (refresh_modified) that it changed: a registry layer makes the entry modified, its drop makes it unmodified again,
// with no Content call in between.
bool modified(const char* name) {
    KilnContentEntry e{};
    e.size = sizeof e;
    const KilnResult rc = t_find(KILN_CONTENT_ITEMS, KilnStr{name, std::strlen(name)}, &e);
    return rc == KILN_OK && (e.flags & KILN_CONTENT_MODIFIED) != 0;
}
void modified_follows_registry_layers() {
    setup();
    world_load();  // no mod has packs: nothing modified
    check(!modified("Wood") && !modified("Stone"), "Modified: nothing differs from the game's original yet");
    constexpr KilnOwner kRegistry = 0x7e9;  // the registry's owner (registry_live.cpp registry_owner)
    const Entry* wood = reg(KILN_CONTENT_ITEMS).get(fnv1a("Wood", 4));
    const uint32_t v = 40;
    uint8_t b[4];
    std::memcpy(b, &v, 4);
    std::string err;
    {
        std::lock_guard lock(store_mutex());
        check(wood && store_put(kRegistry, registry_seq(1), KILN_CONTENT_ITEMS, wood->id, 0, -1, kWood, 4, b, "registry"),
              "a registry layer on Wood's maxStackSize");
        store_flush(&err);
    }
    check(fake::read32(kWood) == 40, "the registry layer is written (40)");
    check(modified("Wood") && !modified("Stone"), "Modified: Wood is modified by the registry layer alone (the store's generation, no Content setter)");
    {
        std::lock_guard lock(store_mutex());
        store_drop_owner(kRegistry);
        store_flush(&err);
    }
    check(fake::read32(kWood) == 10 && !modified("Wood"), "Modified: the registry layer's drop puts Wood back, unmodified");
    {
        std::lock_guard lock(store_mutex());
        const uint32_t original = 10;
        std::memcpy(b, &original, 4);
        store_put(kRegistry, registry_seq(2), KILN_CONTENT_ITEMS, wood->id, 0, -1, kWood, 4, b, "registry");  // the original value
        store_flush(&err);
    }
    check(!modified("Wood"), "Modified: a registry layer that writes the original value leaves the entry unmodified");
    {
        std::lock_guard lock(store_mutex());
        store_drop_owner(kRegistry);
        store_flush(&err);
    }
    world_reset(true, "is stopping");
    reg(KILN_CONTENT_ITEMS) = Registry{};  // run() loads its own world
    fs::remove_all(g_root);
}

}  // namespace

int main() {
    modified_follows_registry_layers();
    run();
    if (failures) std::printf("packreload-test: %d check(s) FAILED\n", failures);
    else std::printf("packreload-test: all checks passed\n");
    return failures ? 1 : 0;
}
