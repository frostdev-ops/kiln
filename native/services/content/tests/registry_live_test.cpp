// SPDX-License-Identifier: MIT
// kiln.content, host test: the registry's live back-end (registry_live.cpp, registry P1 S9) over the real store (store.cpp),
// the real IR / composition (registry/ir.cpp, compose.cpp), the real formulas and the committed regmap (regmap/common + the
// server record), on a made-up game memory: the fields of a composed stage written as evaluated layers above packs and below
// code, relative edits re-evaluated over a pack's new value, hot reload (a changed section recomposed, a removed one leaving
// nothing behind, a new field refused "RestartRequired: ..."), what a reload leaves for the next start (restart fields,
// new fields, new sections: RestartRequired rows and counts.restartRequired), loud refusals, compare-before-write in the report.
// registry_live_test.sh builds and runs it.
#include <windows.h>
#include <algorithm>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <map>
#include <set>
#include <stdexcept>
#include <string>
#include "../content.h"
#include "../registry/formulas.h"
#include "../registry/report.h"
#include "../registry/schema_ir.h"
#include "../registry_live.h"
#include "../registry/live_table.h"
#include "../store.cpp"
#include "../registry_live.cpp"

namespace kc {
const KilnApi* g_api = nullptr;
Engine E;
RecipeLayout RL;
DropLayout DL;
std::unordered_map<uint32_t, std::vector<uint8_t>> g_category_guid;
namespace {
alignas(16) uint8_t g_arena[1 << 20];  // the made-up game memory (real addresses, so near memory is within reach)
size_t g_near = 1 << 19;
Registry g_regs[5];
std::map<std::string, std::string> g_files;  // pack file rel -> its text
std::vector<size_t> g_write_limits; // test-only: successful writes allowed in each next batch (empty: no injected failure)
std::function<void()> g_before_write; // test-only: one callback just before a real batch writes
std::function<void()> g_after_each_write; // test-only: after every single write lands (what a game thread could read between two)
std::set<uintptr_t> g_fail_addrs; // test-only: writes to these addresses fail; the batch goes on with the others (as write_batch does)
std::set<std::string> g_integer_impacts; // made-up per-entry Sint32 vs Float cost storage
}  // namespace
uintptr_t A(size_t off) { return reinterpret_cast<uintptr_t>(g_arena) + off; }
std::vector<std::string> g_log_lines;  // test-only: every kiln.log line, "<level> <text>" (KILN_LOG_DEBUG lines too)
void logf(int level, const char* fmt, ...) {
    char b[4096];
    va_list a;
    va_start(a, fmt);
    std::vsnprintf(b, sizeof b, fmt, a);
    va_end(a);
    std::fprintf(stderr, "%s\n", b);
    if (g_log_lines.size() < 100000) g_log_lines.push_back(std::to_string(level) + " " + b);
}
size_t g_rd_calls = 0;  // test-only: reads of game memory (what one tick of the later-writes look reads)
bool rd(uintptr_t address, void* out, size_t n) {
    ++g_rd_calls;
    if (address < A(0) || address + n > A(sizeof g_arena)) return false;
    std::memcpy(out, reinterpret_cast<const void*>(address), n);
    return true;
}
size_t write_batch(std::vector<Write>& writes, uintptr_t* failed) {
    if (g_before_write) { auto callback = std::move(g_before_write); g_before_write = {}; callback(); }
    size_t limit = writes.size();
    if (!g_write_limits.empty()) { limit = g_write_limits.front(); g_write_limits.erase(g_write_limits.begin()); }
    for (size_t i = 0; i < writes.size(); ++i) {
        const Write& w = writes[i];
        if (i >= limit) { if (failed) *failed = w.addr; return i; }
        if (g_fail_addrs.count(w.addr)) { if (failed && !*failed) *failed = w.addr; continue; }
        std::memcpy(reinterpret_cast<void*>(w.addr), w.bytes, w.n);
        if (g_after_each_write) g_after_each_write();
    }
    size_t done = writes.size();
    for (const Write& w : writes) done -= g_fail_addrs.count(w.addr);
    return done;
}
void* near_alloc(uintptr_t, size_t n) {
    void* p = g_arena + g_near;
    g_near += (n + 15) & ~size_t(15);
    return p;
}
// a gamedata slot's kind (GD_KIND_BASE + section, registry_gd_live_test) is no kiln.content registry: kiln.gamedata names it in
// the real build (KC_GAMEDATA); here an empty registry
Registry& reg(uint32_t kind) {
    static Registry none;
    return kind < 5 ? g_regs[kind] : (none = Registry{});
}
const Entry* Registry::get(uint32_t id) const {
    auto it = by_id.find(id);
    return it == by_id.end() ? nullptr : &entries[it->second];
}
const Entry* Registry::lookup(const std::string& key) const {
    for (const Entry& e : entries)
        if (lower(guid_text(e.guid)) == lower(key) || e.name == key) return &e;
    return nullptr;
}
std::string lower(std::string s) {
    for (char& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}
std::string guid_text(const uint8_t g[16]) {
    char b[40];
    std::snprintf(b, sizeof b, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x", g[0], g[1], g[2], g[3], g[4], g[5],
                  g[6], g[7], g[8], g[9], g[10], g[11], g[12], g[13], g[14], g[15]);
    return b;
}
bool walk_drop_table(uintptr_t resource, std::vector<DropStackRef>* out) {
    out->clear();
    if (resource != A(0x30000)) return false;
    out->push_back({A(0x31000), 0});
    out->push_back({A(0x31040), 0});
    return true;
}
bool patch_code_byte(uintptr_t, uint8_t, uint8_t) { return false; }
uintptr_t type_by_hash(uint32_t) { return 0; }
std::string read_text(uintptr_t, uint64_t) { return {}; }
bool guid_parse(const std::string& s, uint8_t out[16]) {
    std::string h;
    for (char c : s)
        if (c != '-') h += c;
    if (h.size() != 32) return false;
    for (int i = 0; i < 16; ++i) out[i] = static_cast<uint8_t>(std::stoul(h.substr(2 * i, 2), nullptr, 16));
    return true;
}
uint32_t fnv1a(const void* data, size_t n) {
    uint32_t h = 2166136261u;
    for (size_t i = 0; i < n; ++i) h = (h ^ static_cast<const uint8_t*>(data)[i]) * 16777619u;
    return h;
}
// The made-up sword has mana/stamina configs and tooltip rows; each can use actual integer or float storage.
bool gd_item_config_slot(const Entry& e, const std::string& path, GdValueSlot* out, std::string* why) {
    const bool mana = path == "impactValues.0xaf236ece", stamina = path == "impactValues.0x727d7f78";
    if (e.id != 0x222 || (!mana && !stamina)) return *why = "the item has no such configuration (Kiln's live back-end does not add one)", false;
    const size_t offset = mana ? 0x40 : 0x48;
    const bool integer = g_integer_impacts.count(mana ? "manaCost" : "staminaCost") != 0;
    out->addr = e.addr + offset, out->kind = 1000, out->id = e.id, out->field = mana ? 7 : 8;
    out->f.path = path + ".value", out->f.offset = 0, out->f.bytes = 4;
    out->f.vt = integer ? KILN_CV_INT : KILN_CV_FLOAT, out->f.is_signed = integer, out->f.writable = true;
    out->tip = e.addr + offset + 4, out->tip_format = 0;
    return true;
}
bool gd_ui_scale(uint8_t, double v, int64_t* out) { return *out = std::llround(v), true; }
// dev.32: a made-up kiln.gamedata world for the creatures, arsenals and perks registries (registry_gd_live_test.cpp fills it):
// entries by GUID, value slots by (entry id, path), the paths an entry lacks (a component it has none of), a perk's one value.
struct GdSectionFake {
    std::map<std::string, Entry> entries;                         // lower-case GUID -> the entry
    std::map<std::pair<uint32_t, std::string>, GdValueSlot> slots; // (entry id, path) -> its slot
    std::set<std::pair<uint32_t, std::string>> absent;             // (entry id, path): the entry has none of it
    std::map<uint32_t, std::string> shown;                         // entry id -> its one value's path, or "none" / "several"
    std::map<std::pair<uint32_t, std::string>, uint32_t> lists;    // (entry id, list path) -> its length
};
std::map<uint32_t, GdSectionFake> g_gd_sections;  // by section
bool gd_section_entry(uint32_t section, const std::string& guid, uint32_t num, Entry* out) {
    const auto s = g_gd_sections.find(section);
    if (s == g_gd_sections.end()) return false;
    for (const auto& [g, e] : s->second.entries)
        if (guid.empty() ? (num && e.id == num) : g == guid) return *out = e, true;
    return false;
}
bool gd_value_slot(uint32_t section, const Entry& e, const std::string& path, GdValueSlot* out, std::string* why) {
    GdSectionFake& f = g_gd_sections[section];
    if (f.absent.count({e.id, path})) return out->absent = true, *why = "no component " + path, false;
    const auto it = f.slots.find({e.id, path});
    if (it == f.slots.end()) return *why = "the template has no member " + path, false;
    return *out = it->second, true;
}
bool gd_shown_config(uint32_t section, const Entry& e, const std::string& configs, std::string* path, std::string* why) {
    const auto it = g_gd_sections[section].shown.find(e.id);
    if (it == g_gd_sections[section].shown.end()) return *why = "the perk has no " + configs, false;
    if (it->second == "none" || it->second == "several") return *why = it->second, false;
    return *path = it->second, true;
}
bool gd_list_size(uint32_t section, const Entry& e, const std::string& path, uint32_t* n, std::string* why) {
    const auto it = g_gd_sections[section].lists.find({e.id, path});
    if (it == g_gd_sections[section].lists.end()) return *why = "the arsenal has no member " + path, false;
    return *n = it->second, true;
}
const registry::Schema& registry_schema() {
    static const registry::Schema s = registry::generated_schema();
    return s;
}
std::vector<RegistrySection> registry_sections(const std::vector<PackFile>& files, const registry::Schema& schema) {
    std::vector<RegistrySection> out;
    for (const PackFile& f : files) {
        const registry::json doc = registry::json::parse(g_files[f.rel], nullptr, false, true);
        if (doc.is_discarded() || !doc.contains("registry")) continue;
        RegistrySection s;
        s.mod = f.mod, s.version = f.version, s.rel = f.rel, s.source = f.rel.substr(f.rel.find("/content/") + 1);
        s.hash = std::hash<std::string>{}(doc["registry"].dump());
        registry::lower_section(doc["registry"], f.mod, f.version, s.source, schema, &s.batches, &s.refused, &s.error);
        out.push_back(std::move(s));
    }
    return out;
}

void apply_registry_files(const std::vector<PackFile>& files) {
    const auto schema = registry_live_schema();
    const auto sections = registry_sections(files, *schema);
    {
        std::lock_guard lock(store_mutex());
        registry_live_apply(sections, schema.get());
    }
    registry_live_publish();
}
}  // namespace kc

namespace {
using namespace kc;
int checks = 0;
void check(bool yes, const std::string& name) {
    if (!yes) throw std::runtime_error(name);
    ++checks;
}

constexpr KilnOwner PACK = 11, CODE = 44;
constexpr uint64_t kPackSeq = (1ull << 44) | (1ull << 32) | 1;
const uint8_t G1[16] = {1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1}, G2[16] = {2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2},
              G3[16] = {3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3}, G4[16] = {4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4};
const uint8_t P1[16] = {9, 1, 9, 1, 9, 1, 9, 1, 9, 1, 9, 1, 9, 1, 9, 1}, P2[16] = {9, 2, 9, 2, 9, 2, 9, 2, 9, 2, 9, 2, 9, 2, 9, 2};
constexpr uint32_t WOOD = 0x111, SWORD = 0x222, PLANK = 0x333, STONE = 0x555;
constexpr size_t WOOD_AT = 0x10000, SWORD_AT = 0x10100, STONE_AT = 0x10200, PLANK_AT = 0x20000;

template <class T>
T at(size_t off) {
    T v{};
    std::memcpy(&v, g_arena + off, sizeof v);
    return v;
}
template <class T>
void mem(size_t off, T v) {
    std::memcpy(g_arena + off, &v, sizeof v);
}

Field fld(const char* path, uint32_t off, uint8_t bytes, uint8_t vt, bool f64 = false) {
    Field f;
    f.path = path, f.offset = off, f.bytes = bytes, f.vt = vt, f.f64 = f64, f.writable = true;
    return f;
}
void add_field(Schema& s, Field f) {
    s.by_path[f.path] = static_cast<uint32_t>(s.fields.size());
    s.by_lower[lower(f.path)] = static_cast<uint32_t>(s.fields.size());
    s.fields.push_back(std::move(f));
}
void add_entry(Registry& r, uint32_t id, const char* name, size_t at_off, const uint8_t g[16]) {
    Entry e;
    e.id = id, e.name = name, e.addr = A(at_off);
    std::memcpy(e.guid, g, 16);
    r.by_id[id] = static_cast<uint32_t>(r.entries.size());
    r.entries.push_back(e);
}

std::shared_ptr<registry::map::RegMap> g_regmap;
registry::Result g_last_report;
int g_reports = 0;
std::function<std::string(KilnOwner)> g_owner_name;  // stage(): RegistryLive::owner_name (the core's id of an owner)
std::function<void(RegistryLive&)> g_stage_extra;    // stage(): more base entries, ids and numbers (composed again after it)

void world() {
    g_owner_name = {};
    g_stage_extra = {};
    g_write_limits.clear(); g_before_write = {}; g_after_each_write = {}; g_fail_addrs.clear(); g_integer_impacts.clear();
    g_registry_type_size = [](const char* q) -> uint32_t { return std::string(q) == "keen::KnowledgeQueryData" ? 12u : 0u; };
    std::string e;
    store_reset(false, &e);
    std::memset(g_arena, 0, sizeof g_arena);
    g_near = 1 << 19;
    for (Registry& r : g_regs) r = Registry{};
    Registry& I = g_regs[KILN_CONTENT_ITEMS];
    I.kind = KILN_CONTENT_ITEMS, I.label = "items", I.available = true;
    add_field(I.schema, fld("maxStackSize", 0x00, 4, KILN_CV_INT));
    Field rar = fld("rarity", 0x04, 4, KILN_CV_INT);
    rar.enum_kind = KILN_CONTENT_ENUM;
    rar.en = std::make_shared<EnumInfo>();
    rar.en->values = {{"Common", 0}, {"Uncommon", 1}, {"Rare", 2}, {"Epic", 3}, {"Legendary", 4}};
    add_field(I.schema, rar);
    add_field(I.schema, fld("damageSetup.dmgMod", 0x08, 4, KILN_CV_FLOAT));
    add_field(I.schema, fld("damageSetup.speedMod", 0x0C, 4, KILN_CV_FLOAT));
    add_field(I.schema, fld("armorSetup.distribution.physical", 0x90, 4, KILN_CV_FLOAT));
    Field socket = fld("gemSlotOverride", 0x98, 1, KILN_CV_BOOL); socket.type_name = "keen::GemSlotOverride";
    add_field(I.schema, socket); // refl.cpp single_scalar flattens {isSet} to the parent path
    add_field(I.schema, fld("itemLevelRange.minLevel", 0x10, 4, KILN_CV_INT));
    add_field(I.schema, fld("itemLevelRange.maxLevel", 0x14, 4, KILN_CV_INT));
    for (int i = 0; i < 5; ++i) {
        Field pr = fld(("perkReferences[" + std::to_string(i) + "]").c_str(), 0x30 + 16 * i, 16, KILN_CV_GUID);
        pr.ref = true;
        add_field(I.schema, pr);
        add_field(I.schema, fld(("perkIds[" + std::to_string(i) + "]").c_str(), 0x80 + 4 * i, 4, KILN_CV_INT));
    }
    add_entry(I, WOOD, "Material_T1_Wood_Logs", WOOD_AT, G1);
    add_entry(I, SWORD, "Sword_T2", SWORD_AT, G2);
    add_entry(I, STONE, "Material_Stone", STONE_AT, G4);
    mem<uint32_t>(WOOD_AT, 10);
    std::memcpy(g_arena + WOOD_AT + 0x30, P1, 16), mem<uint32_t>(WOOD_AT + 0x80, 0x901);  // perk slot 0: perk_a
    mem<float>(SWORD_AT + 0x40, 10.0f), mem<int32_t>(SWORD_AT + 0x44, 10);            // ManaCost 10, its tooltip 10
    mem<float>(SWORD_AT + 0x48, 10.0f), mem<int32_t>(SWORD_AT + 0x4C, 10);            // StaminaCost 10, its tooltip 10
    Registry& L = g_regs[KILN_CONTENT_LOOT];
    L.kind = KILN_CONTENT_LOOT, L.label = "loot", L.available = true;
    add_field(L.schema, fld("stackSizeMin", 0x00, 4, KILN_CV_INT));
    add_field(L.schema, fld("stackSizeMax", 0x04, 4, KILN_CV_INT));
    // the game's LootableItemInfo: a Bitmask8<ItemRarity>, the fixed rarity's switch and the fixed rarity (an enum)
    Field mask = fld("rarities", 0x08, 1, KILN_CV_INT);
    mask.enum_kind = KILN_CONTENT_FLAGS;
    mask.en = rar.en;
    add_field(L.schema, mask);
    add_field(L.schema, fld("useUiRarity", 0x09, 1, KILN_CV_BOOL));
    Field fixed = fld("uiRarity", 0x0A, 1, KILN_CV_INT);
    fixed.enum_kind = KILN_CONTENT_ENUM;
    fixed.en = rar.en;
    add_field(L.schema, fixed);
    add_entry(L, WOOD, "Material_T1_Wood_Logs", 0x40000, G1);
    mem<uint32_t>(0x40000, 1), mem<uint32_t>(0x40004, 5);
    mem<uint8_t>(0x40008, 0x07), mem<uint8_t>(0x40009, 0), mem<uint8_t>(0x4000A, 2);  // common, uncommon, rare; no fixed rarity (rare below)
    mem<uint32_t>(STONE_AT, 50);
    mem<float>(SWORD_AT + 0x08, 1.0f), mem<float>(SWORD_AT + 0x0C, 1.0f), mem<uint32_t>(SWORD_AT + 0x10, 10), mem<uint32_t>(SWORD_AT + 0x14, 15);
    mem<float>(SWORD_AT + 0x90, 1.0f);
    Registry& R = g_regs[KILN_CONTENT_RECIPES];
    R.kind = KILN_CONTENT_RECIPES, R.label = "recipes", R.available = true;
    add_field(R.schema, fld("craftingDuration", 0x00, 8, KILN_CV_TIME));
    R.schema.special["knowledgeRequirement"] = 0x50;
    add_entry(R, PLANK, "Plank_Recipe", PLANK_AT, G3);
    mem<uint32_t>(PLANK_AT + 0x50, 0x12345678), mem<uint32_t>(PLANK_AT + 0x58, 7);  // a knowledge requirement
    RL.input = 0x40, RL.output = 0x48;
    mem<int64_t>(PLANK_AT, 3'000'000'000ll);  // 3 s
    // inputs: 2 wood (one stack of 48 bytes at +0x100), outputs: 1 sword
    const size_t in_list = PLANK_AT + 0x100, out_list = PLANK_AT + 0x200;
    mem<int32_t>(PLANK_AT + 0x40, static_cast<int32_t>(in_list - (PLANK_AT + 0x40))), mem<uint32_t>(PLANK_AT + 0x44, 1);
    std::memcpy(g_arena + in_list + RL.in_item_ref, G1, 16), mem<uint32_t>(in_list + RL.in_item, WOOD), mem<uint32_t>(in_list + RL.in_count, 2);
    mem<int32_t>(PLANK_AT + 0x48, static_cast<int32_t>(out_list - (PLANK_AT + 0x48))), mem<uint32_t>(PLANK_AT + 0x4C, 1);
    std::memcpy(g_arena + out_list + RL.out_item_ref, G2, 16), mem<uint32_t>(out_list + RL.out_item, SWORD), mem<uint32_t>(out_list + RL.out_count, 1);
    Registry& D = g_regs[KILN_CONTENT_DROPS];
    D.kind = KILN_CONTENT_DROPS, D.label = "drops", D.available = true;
    add_field(D.schema, fld("countMin", 0x14, 4, KILN_CV_INT));
    add_field(D.schema, fld("countMax", 0x18, 4, KILN_CV_INT));
    add_field(D.schema, fld("spawnProbability", 0x1C, 4, KILN_CV_FLOAT));
    add_field(D.schema, fld("weight", 0x20, 4, KILN_CV_FLOAT));
    for (size_t s : {size_t(0x31000), size_t(0x31040)}) {
        mem<uint32_t>(s + 0x10, WOOD), mem<uint32_t>(s + 0x14, 1), mem<uint32_t>(s + 0x18, 3), mem<float>(s + 0x1C, 0.5f), mem<float>(s + 0x20, 1.0f);
    }
}
void add_drop_table() { add_entry(g_regs[KILN_CONTENT_DROPS], 0x444, "DT_Wolf", 0x30000, G3); }

registry::BaseModel base() {
    registry::BaseModel b;
    auto& items = b.registries["items"];
    registry::BaseModel::Entry w;
    w.id = "game:material_t1_wood_logs";
    w.fields["maxStack"] = registry::Value::integer(10);
    w.fields["socket"] = registry::Value::boolean(false);
    w.fields["rarity"] = registry::Value::text("common");
    w.fields["perks"] = registry::Value::of_list({registry::Value::text("game:perk_a")});
    items.push_back(w);
    registry::BaseModel::Entry s;
    s.id = "game:sword_t2";
    double power = 0;
    registry::formulas::read("gear.power", {1.0, 1.0, 10}, registry::formulas::constants_of({}), &power);
    s.fields["power"] = registry::Value::integer(std::llround(power));
    s.fields["armor.physical"] = registry::Value::integer(
        registry::formulas::armor_read(1, 10, registry::formulas::constants_of({})));
    s.fields["maxStack"] = registry::Value::integer(1);
    s.fields["manaCost"] = registry::Value::number(10);
    s.fields["staminaCost"] = registry::Value::number(10);
    items.push_back(s);
    registry::BaseModel::Entry st;
    st.id = "game:material_stone";
    st.fields["maxStack"] = registry::Value::integer(50);
    items.push_back(st);
    registry::BaseModel::Entry rc;
    rc.id = "game:plank_recipe";
    rc.fields["duration"] = registry::Value::number(3);
    rc.fields["unlock"] = registry::Value::text("game");
    auto stru = [](std::vector<std::pair<std::string, registry::Value>> kv) {
        registry::Value x;
        x.kind = registry::Value::Struct;
        std::sort(kv.begin(), kv.end(), [](auto& a, auto& b) { return a.first < b.first; });
        for (auto& [k, v] : kv) x.keys.push_back(k), x.list.push_back(v);
        return x;
    };
    rc.fields["inputs"] = registry::Value::of_list({stru({{"count", registry::Value::integer(2)}, {"item", registry::Value::text("game:material_t1_wood_logs")}})});
    rc.fields["outputs"] = registry::Value::of_list({stru({{"count", registry::Value::integer(1)}, {"item", registry::Value::text("game:sword_t2")}})});
    b.registries["recipes"].push_back(rc);
    registry::BaseModel::Entry dt;
    dt.id = "game:dt_wolf";
    std::vector<registry::Value> st2;
    for (int i = 0; i < 2; ++i)
        st2.push_back(stru({{"chance", registry::Value::number(0.5)}, {"count.max", registry::Value::integer(3)}, {"count.min", registry::Value::integer(1)},
                            {"item", registry::Value::text("game:material_t1_wood_logs")}, {"weight", registry::Value::number(1)}}));
    dt.fields["entries"] = registry::Value::of_list(st2);
    b.registries["drops"].push_back(dt);
    registry::BaseModel::Entry lt;
    lt.id = "game:material_t1_wood_logs";
    lt.fields["stack.min"] = registry::Value::integer(1);
    lt.fields["stack.max"] = registry::Value::integer(5);
    lt.fields["rarities"] = registry::Value::of_list({registry::Value::text("common"), registry::Value::text("uncommon"), registry::Value::text("rare")});
    lt.fields["fixedRarity"] = registry::Value::text("none");
    b.registries["loot"].push_back(lt);
    registry::BaseModel::Entry bal;  // a restart field's entry (the boot back-end's): a reload changes it at the next start
    bal.id = "game:balance";
    bal.fields["levelCap"] = registry::Value::integer(25);
    b.registries["balance"].push_back(bal);
    return b;
}

std::vector<PackFile> files_of(const std::vector<std::pair<std::string, std::string>>& mods_files) {
    std::vector<PackFile> out;
    for (auto& [mod, rel] : mods_files) {
        PackFile f;
        f.mod = mod, f.rel = rel, f.version = "1.0.0";
        out.push_back(f);
    }
    return out;
}

// What S11 does at resources-ready: lower the sections, compose, install.
void stage(const std::vector<PackFile>& files, const std::vector<registry::Batch>& code = {}, const std::string& rollback = "",
           const std::vector<std::string>& left_out = {}, const std::vector<registry::OldWrite>& old = {}) {
    const registry::Schema& sc = registry_schema();
    std::vector<registry::ModInput> mods;
    for (const RegistrySection& s : registry_sections(files, sc)) {
        auto it = std::find_if(mods.begin(), mods.end(), [&](const registry::ModInput& m) { return m.id == s.mod; });
        if (it == mods.end()) mods.push_back({}), it = mods.end() - 1, it->id = s.mod;
        for (auto& b : s.batches) it->batches.push_back(b);
        for (auto& r : s.refused) it->refused.push_back(r);
    }
    // Runner hands over data first, queued code next, and recorded callback turns last.
    for (const auto& batch : code) {
        auto it = std::find_if(mods.begin(), mods.end(), [&](const auto& m) { return m.id == batch.mod; });
        if (it == mods.end()) mods.push_back({}), it = mods.end() - 1, it->id = batch.mod;
        it->batches.push_back(batch);
    }
    RegistryLive in;
    in.schema = std::make_shared<const registry::Schema>(sc);
    in.on_world_thread = [](uint32_t) { return true; };
    in.regmap = g_regmap;
    in.base = base();
    in.mods = mods;
    in.old = old;
    in.result = registry::compose(sc, in.base, old, mods);
    in.ids = {{"items|game:material_t1_wood_logs", guid_text(G1)}, {"items|game:sword_t2", guid_text(G2)}, {"items|game:material_stone", guid_text(G4)},
              {"recipes|game:plank_recipe", guid_text(G3)}, {"drops|game:dt_wolf", guid_text(G3)},
              {"loot|game:material_t1_wood_logs", guid_text(G1)}, {"perks|game:perk_a", guid_text(P1)}, {"perks|game:perk_b", guid_text(P2)}};
    in.nums = {{"items|game:material_t1_wood_logs", WOOD}, {"items|game:sword_t2", SWORD}, {"items|game:material_stone", STONE},
               {"recipes|game:plank_recipe", PLANK}, {"drops|game:dt_wolf", 0x444},
               {"loot|game:material_t1_wood_logs", WOOD}, {"perks|game:perk_a", 0x901}, {"perks|game:perk_b", 0x902}};
    if (g_stage_extra) {
        g_stage_extra(in);
        in.result = registry::compose(sc, in.base, old, mods);
    }
    in.decoded = {"items.socket", "items.perks", "items.manaCost", "items.staminaCost", "recipes.unlock", "loot.stack.max", "loot.rarities", "loot.fixedRarity", "items.maxStack", "items.rarity", "items.power", "recipes.duration", "recipes.inputs", "recipes.outputs", "drops.entries"};
    in.report = [](const registry::Result& r, bool) {
        g_last_report = r;
        ++g_reports;
    };
    // the restart fields as the boot back-end wrote them at the start (stage.cpp hands these over): a reload changes them
    // only at the next start
    for (const auto& [ref, field] : in.result.fields) {
        const registry::RegistrySpec* rs = sc.registry(ref.registry);
        const registry::FieldSpec* sp = rs ? rs->field(ref.field) : nullptr;
        if (sp && sp->applies == registry::Applies::Restart) in.boot_fields[ref] = field;
    }
    in.rollback = rollback, in.left_out = left_out;  // a rolled-back start (stage.h LiveHandoff::rollback, left_out)
    in.owner_name = g_owner_name;
    registry_live_install(std::move(in));
}

bool reported(const std::string& needle) {
    for (const registry::Refusal& r : g_last_report.refused)
        if (r.reason.find(needle) != std::string::npos) return true;
    return false;
}

void put_pack(size_t off, uint32_t v) {
    uint8_t b[4];
    std::memcpy(b, &v, 4);
    set_owner_label(PACK, "pack oldstacks");
    store_put(PACK, kPackSeq, KILN_CONTENT_ITEMS, WOOD, 0, -1, A(off), 4, b, "oldstacks: content/a.json");
}

registry::json json_field(const registry::FieldRef& ref) {
    const auto doc = registry::report_json(registry_schema(), g_last_report, {});
    for (const auto& row : doc.at("fields"))
        if (row.at("registry") == ref.registry && row.at("id") == ref.id && row.at("field") == ref.field) return row;
    throw std::runtime_error("missing report row for " + ref.text());
}

bool conflict(const registry::FieldRef& ref, registry::LineKind kind, const std::string& pack) {
    return std::any_of(g_last_report.lines.begin(), g_last_report.lines.end(), [&](const registry::Line& line) {
        return line.field == ref && line.kind == kind && line.winner == "m" && line.loser == pack;
    });
}

void callback_order_survives_reloads() {
    for (bool queued : {false, true}) {
        world();
        g_files = {{"a/content/a.json", R"({"registry":{"alter":[{"registry":"items","select":"game:material_t1_wood_logs","field":"maxStack","op":{"mul":4}}]}})"},
                   {"b/content/b.json", R"({"registry":{"alter":[{"registry":"items","select":"game:material_stone","field":"maxStack","op":{"mul":2}}]}})"}};
        const auto files = files_of({{"a", "a/content/a.json"}, {"b", "b/content/b.json"}});
        std::vector<registry::Batch> code;
        auto record = [&](const char* op, const char* source) {
            registry::Batch batch; std::vector<registry::Refusal> refused; std::string why;
            const std::string ir = std::string(R"({"ir":1,"mod":"a","stage":"alter","edits":[{"registry":"items","select":"game:material_t1_wood_logs","field":"maxStack","op":)") + op + "}]}";
            check(registry::parse_batch(ir, registry_schema(), &batch, &refused, &why) && refused.empty(), "parse recorded code batch");
            batch.source = source; code.push_back(std::move(batch));
        };
        if (queued) record(R"({"mul":2})", "code:a");
        record(R"({"add":5})", "code");
        stage(files, code); apply_registry_files(files);
        const uint32_t factor = queued ? 2 : 1;
        check(at<uint32_t>(WOOD_AT) == 40 * factor + 5, "start applies data, queued code, then the callback");
        g_files["b/content/b.json"] = R"({"registry":{"alter":[{"registry":"items","select":"game:material_stone","field":"maxStack","op":{"mul":3}}]}})";
        apply_registry_files(files);
        check(at<uint32_t>(WOOD_AT) == 40 * factor + 5, "an unrelated section reload keeps data before recorded code");
        g_files["a/content/a.json"] = R"({"registry":{"alter":[{"registry":"items","select":"game:material_t1_wood_logs","field":"maxStack","op":{"mul":6}}]}})";
        apply_registry_files(files);
        check(at<uint32_t>(WOOD_AT) == 60 * factor + 5, "a same-mod reload also keeps the callback last, exactly once");
        g_files["a/content/a.json"] = "{}"; apply_registry_files(files);
        check(at<uint32_t>(WOOD_AT) == 10 * factor + 5, "removing data leaves queued code and the callback exactly once");
        registry_live_owner_gone(77); registry_live_publish();
        check(at<uint32_t>(WOOD_AT) == 10 * factor + 5, "owner retirement does not discard already-taken start edits");
    }
}

void live_recipe_report() {
    world();
    g_files = {{"m/content/a.json", R"({"registry": {"alter": [
        {"registry": "recipes", "select": {"id": "game:plank_recipe"}, "field": "inputs[0].count", "op": {"add": 1}},
        {"registry": "recipes", "select": {"id": "game:plank_recipe"}, "field": "outputs", "list": {"replace": [{"item": "game:sword_t2", "count": 3}]}}
    ]}})"}};
    const auto files = files_of({{"m", "m/content/a.json"}});
    stage(files);
    const Entry& recipe = *reg(KILN_CONTENT_RECIPES).get(PLANK);
    const std::string source = "oldrecipes: content/a.json";
    set_owner_label(PACK, "pack oldrecipes");
    auto pack_lists = [&](uint32_t count) {
        std::string why;
        KilnContentStack input{}, output{};
        input.item = WOOD, input.count = count;
        output.item = SWORD, output.count = 7;
        check(set_recipe_stacks(PACK, kPackSeq, recipe, KILN_CONTENT_INPUTS, {input}, source, &why) == KILN_OK, "old recipe inputs: " + why);
        check(set_recipe_stacks(PACK, kPackSeq, recipe, KILN_CONTENT_OUTPUTS, {output}, source, &why) == KILN_OK, "old recipe outputs: " + why);
    };
    const registry::FieldRef inputs{"recipes", "game:plank_recipe", "inputs"}, outputs{"recipes", "game:plank_recipe", "outputs"};
    pack_lists(5);
    apply_registry_files(files);
    auto row = json_field(inputs);
    check(row["result"].at(0)["count"] == 6, "recipe input report uses the old pack's live count");
    check(row["writers"].size() == 2 && row["writers"][0]["kind"] == "oldPack" &&
              row["writers"][0]["who"] == source && row["writers"][0]["list"]["replace"][0]["count"] == 5 &&
              row["writers"][1]["value"][0]["count"] == 6, "recipe input writers use the live list");
    check(conflict(inputs, registry::LineKind::ComposedOn, source), "recipe input composedOn names the old pack");
    row = json_field(outputs);
    check(row["result"][0]["count"] == 3 && row["writers"][0]["value"][0]["count"] == 7, "recipe output replacement shows both lists");
    check(conflict(outputs, registry::LineKind::Overridden, source), "recipe output overridden names the old pack");
    store_drop_owner(PACK);
    pack_lists(9);
    apply_registry_files(files);
    check(json_field(inputs)["result"][0]["count"] == 10, "recipe input report follows an old pack reload");
    KilnContentStack code{};
    code.item = WOOD, code.count = 12;
    std::string why;
    check(set_recipe_stacks(CODE, next_code_seq(), recipe, KILN_CONTENT_INPUTS, {code}, "code recipe", &why) == KILN_OK,
          "code recipe input writer: " + why);
    apply_registry_files(files);
    row = json_field(inputs);
    check(row["result"][0]["count"] == 12 && row["winner"] == "code recipe" && row["writers"].back()["kind"] == "code" &&
              row["writers"][1]["value"][0]["count"] == 10, "code above the registry wins and both live list values are shown");
    check(!row.contains("resultEntriesLeftOut"), "a short result list has no omission count");
}

void live_formula_report() {
    world();
    g_files = {{"m/content/a.json", R"({"registry": {"alter": [
        {"registry": "items", "select": {"id": "game:sword_t2"}, "field": "power", "op": {"mul": 2}},
        {"registry": "items", "select": {"id": "game:sword_t2"}, "field": "armor.physical", "op": {"add": 4}}
    ]}})"}};
    const auto files = files_of({{"m", "m/content/a.json"}});
    stage(files);
    const std::string source = "oldgear: content/a.json";
    set_owner_label(PACK, "pack oldgear");
    const uint32_t level = 20;
    store_put(PACK, kPackSeq, KILN_CONTENT_ITEMS, SWORD, -1, -1, A(SWORD_AT + 0x10), 4,
              reinterpret_cast<const uint8_t*>(&level), source);
    apply_registry_files(files);
    const auto c = registry::formulas::constants_of({});
    for (const auto& field : {"power", "armor.physical"}) {
        const bool power = std::string(field) == "power";
        const registry::FieldRef ref{"items", "game:sword_t2", field};
        const auto row = json_field(ref);
        const int below = power ? registry::formulas::power_read(1, 1, level, c) : registry::formulas::armor_read(1, level, c);
        const int expected = power ? below * 2 : below + 4;
        check(row["result"] == expected, std::string(field) + " report uses the live formula result");
        const registry::json inputs = power ? registry::json::array({at<float>(SWORD_AT + 8), 1, level})
                                            : registry::json::array({at<float>(SWORD_AT + 0x90), level});
        check(row["formulaInputs"] == inputs, std::string(field) + " report includes the effective inputs");
        check(row["writers"].size() == 2 && row["writers"][0]["who"] == source && row["writers"][0]["value"] == below &&
                  row["writers"][1]["value"] == expected, std::string(field) + " writers use live formula values");
        check(conflict(ref, registry::LineKind::ComposedOn, source), std::string(field) + " composedOn names the old pack");
    }
}

void report_caps_lists() {
    registry::Result result;
    auto& field = result.fields[{"drops", "game:dt_wolf", "entries"}];
    std::vector<registry::Value> entries;
    for (int i = 0; i < 70; ++i) entries.push_back(registry::Value::integer(i));
    field.base = field.result = registry::Value::of_list(entries);
    auto& writer = field.chain.emplace_back();
    writer.after = field.result;
    writer.list = registry::ListOp::Replace;
    writer.list_values = entries;
    auto& set_writer = field.chain.emplace_back();
    set_writer.after = field.result;
    set_writer.op.set = true;
    set_writer.op.value = field.result;
    const auto doc = registry::report_json(registry_schema(), result, {});
    const auto& row = doc["fields"][0];
    check(row["result"].size() == 64 && row["result"][63] == 63 && row["resultEntriesLeftOut"] == 6,
          "long result list keeps its first 64 entries and counts the rest");
    check(row["base"]["value"].size() == 64 && row["base"]["entriesLeftOut"] == 6, "long base list is capped");
    check(row["writers"][0]["value"].size() == 64 && row["writers"][0]["valueEntriesLeftOut"] == 6, "long writer value is capped");
    check(row["writers"][0]["list"]["replace"].size() == 64 && row["writers"][0]["listEntriesLeftOut"] == 6, "long list operand is capped");
    check(row["writers"][1]["op"].size() == 64 && row["writers"][1]["opEntriesLeftOut"] == 6, "long list passed to op is capped");
}

// a relative registry edit over the game's value, over a pack's value, re-evaluated when the pack is applied again
void relative_over_packs_and_reload() {
    world();
    g_files = {{"bigstacks/content/a.json",
                R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": {"mul": 4}}]}})"}};
    const auto files = files_of({{"bigstacks", "bigstacks/content/a.json"}});
    stage(files);
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT) == 40, "x4 over the game's 10: " + std::to_string(at<uint32_t>(WOOD_AT)));
    // world load with an old pack (apply_all_packs: packs first, then the registry)
    put_pack(WOOD_AT, 30);
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT) == 120, "x4 over the pack's 30");
    {
        const registry::FieldOutcome& fo = g_last_report.fields.at({"items", "game:material_t1_wood_logs", "maxStack"});
        check(fo.result == registry::Value::integer(120), "registry.json at world load: the live result 120 (" + fo.result.text_of() + ")");
        check(fo.chain.size() == 2 && fo.chain[0].kind == registry::WriterKind::OldPack && fo.chain[0].who == "oldstacks: content/a.json" &&
                  fo.chain[0].after == registry::Value::integer(30),
              "registry.json at world load: the old pack is a writer below the registry");
        bool line = false;
        for (const registry::Line& l : g_last_report.lines)
            line = line || (l.kind == registry::LineKind::ComposedOn && l.winner == "bigstacks" && l.loser == "oldstacks: content/a.json");
        check(line, "registry.json at world load: composedOn names the old pack");
    }
    store_drop_owner(PACK);  // the pack edited: a reload
    put_pack(WOOD_AT, 50);
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT) == 200, "x4 over the reloaded pack's 50");
    check(g_reports == 3 && g_last_report.refused.empty(), "registry.json written at every application, nothing refused");
    uint8_t b[4] = {7, 0, 0, 0};
    store_put(CODE, next_code_seq(), KILN_CONTENT_ITEMS, WOOD, 0, -1, A(WOOD_AT), 4, b, "code");
    store_flush(nullptr);
    check(at<uint32_t>(WOOD_AT) == 7, "code wins over the registry");
    const auto cs = store_conflicts();
    bool named = false;
    for (const Conflict& c : cs) named = named || c.loser.find("bigstacks: content/a.json (registry items[game:material_t1_wood_logs].maxStack)") == 0;
    check(named, "conflicts name the registry layer's mod, file and field");
}

// set values of every live kind: enum (Kiln name -> the game's), time, formula (power), recipe lists, drop-entry leaves
void every_live_kind() {
    world();
    g_files = {{"m/content/a.json", R"({"registry": {"alter": [
        {"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "rarity", "op": "epic"},
        {"registry": "items", "select": {"id": "game:sword_t2"}, "field": "power", "op": {"mul": 2}},
        {"registry": "recipes", "select": {"id": "game:plank_recipe"}, "field": "duration", "op": 5},
        {"registry": "recipes", "select": {"id": "game:plank_recipe"}, "field": "inputs", "list": {"append": [{"item": "game:material_stone", "count": 3}]}},
        {"registry": "recipes", "select": {"id": "game:plank_recipe"}, "field": "outputs[0].count", "op": {"add": 1}},
        {"registry": "drops", "select": {"id": "game:dt_wolf"}, "field": "entries[*].count.max", "op": {"mul": 2}}
    ]}})"}};
    const auto files = files_of({{"m", "m/content/a.json"}});
    stage(files);
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT + 4) == 3, "rarity epic -> the game's Epic (3)");
    check(at<int64_t>(PLANK_AT) == 5'000'000'000ll, "duration 5 s");
    const gear::Constants c = registry::formulas::constants_of({});
    double p0 = 0, p1 = 0;
    registry::formulas::read("gear.power", {1.0, 1.0, 10}, c, &p0);
    registry::formulas::read("gear.power", {at<float>(SWORD_AT + 8), at<float>(SWORD_AT + 0x0C), 10}, c, &p1);
    check(std::llround(p1) == 2 * std::llround(p0), "power x2 through the formula: " + std::to_string(p0) + " -> " + std::to_string(p1));
    // inputs: wood x2, stone x3
    const int32_t off = at<int32_t>(PLANK_AT + 0x40);
    const uint32_t n = at<uint32_t>(PLANK_AT + 0x44);
    check(n == 2, "two inputs now");
    const size_t list = PLANK_AT + 0x40 + off;
    check(at<uint32_t>(list + RL.in_item) == WOOD && at<uint32_t>(list + RL.in_count) == 2, "first input unchanged");
    check(at<uint32_t>(list + RL.in_stride + RL.in_item) == STONE && at<uint32_t>(list + RL.in_stride + RL.in_count) == 3, "stone x3 appended");
    check(std::memcmp(g_arena + list + RL.in_stride + RL.in_item_ref, G4, 16) == 0, "with the stone's GUID");
    const size_t olist = PLANK_AT + 0x48 + at<int32_t>(PLANK_AT + 0x48);
    check(at<uint32_t>(olist + RL.out_count) == 2, "outputs[0].count + 1");
    // the drop table is not loaded yet: its layers wait for it
    check(at<uint32_t>(0x31018) == 3, "no table yet");
    add_drop_table();
    registry_live_table(g_regs[KILN_CONTENT_DROPS].entries[0]);
    store_flush(nullptr);
    check(at<uint32_t>(0x31018) == 6 && at<uint32_t>(0x31058) == 6, "every stack's count.max x2 once the table appears");
    check(at<uint32_t>(0x31014) == 1, "count.min unchanged");
    check(g_last_report.refused.empty(), "nothing refused");
}

// perks (refList: references and ids in step), manaCost (impactConfig, its tooltip follows), unlock ("always" clears the
// knowledge requirement, "game" keeps it), loot stack sizes
void perks_costs_unlock_loot() {
    world();
    g_files = {{"m/content/a.json", R"({"registry": {"alter": [
        {"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "perks", "list": {"append": ["game:perk_b"]}},
        {"registry": "items", "select": {"id": "game:sword_t2"}, "field": "manaCost", "op": {"mul": 1.5}},
        {"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "staminaCost", "op": 3},
        {"registry": "recipes", "select": {"id": "game:plank_recipe"}, "field": "unlock", "op": "always"},
        {"registry": "loot", "select": {"id": "game:material_t1_wood_logs"}, "field": "stack.max", "op": {"mul": 2}}
    ]}})"}};
    const auto files = files_of({{"m", "m/content/a.json"}});
    stage(files);
    const std::string source = "oldperks: content/a.json";
    set_owner_label(PACK, "pack oldperks");
    const uint8_t empty[16] = {};
    store_put(PACK, kPackSeq, KILN_CONTENT_ITEMS, WOOD, -1, -1, A(WOOD_AT + 0x30), 16, empty, source);
    store_put(PACK, kPackSeq, KILN_CONTENT_ITEMS, WOOD, -1, -1, A(WOOD_AT + 0x40), 16, P1, source);
    apply_registry_files(files);
    for (const auto& r : g_last_report.refused) std::fprintf(stderr, "  refused: %s\n", r.reason.c_str());
    check(std::memcmp(g_arena + WOOD_AT + 0x30, P1, 16) == 0 && at<uint32_t>(WOOD_AT + 0x80) == 0x901, "perk slot 0 kept");
    check(std::memcmp(g_arena + WOOD_AT + 0x40, P2, 16) == 0 && at<uint32_t>(WOOD_AT + 0x84) == 0x902, "perk_b appended into slot 1, its id with it");
    check(at<float>(SWORD_AT + 0x40) == 15.0f, "manaCost x1.5: 15");
    check(at<int32_t>(SWORD_AT + 0x44) == 15, "its tooltip row follows: 15");
    check(reported("items[game:material_t1_wood_logs].staminaCost: the item has no such configuration"), "staminaCost on an item without the config: refused, loud");
    check(at<uint32_t>(PLANK_AT + 0x50) == 0 && at<uint32_t>(PLANK_AT + 0x58) == 0, "unlock always: the knowledge requirement cleared");
    check(at<uint32_t>(0x40004) == 10, "loot stack.max x2: 10");
    const registry::FieldRef perks{"items", "game:material_t1_wood_logs", "perks"};
    const auto row = json_field(perks);
    check(row["result"] == registry::json::array({"game:perk_a", "game:perk_b"}) &&
              row["writers"].size() == 2 && row["writers"][0]["who"] == source &&
              row["writers"][0]["value"] == registry::json::array({"game:perk_a"}), "perk reference slots report one writer and one list");
    store_drop_owner(PACK);
    // back to the game's: the section removed on a reload
    g_files["m/content/a.json"] = R"({"registry": {"alter": [{"registry": "recipes", "select": {"id": "game:plank_recipe"}, "field": "unlock", "op": "game"}]}})";
    apply_registry_files(files);
    check(at<uint32_t>(PLANK_AT + 0x50) == 0x12345678 && at<uint32_t>(PLANK_AT + 0x58) == 7, "unlock game: the game's requirement again");
    check(std::memcmp(g_arena + WOOD_AT + 0x40, "\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0", 16) == 0 && at<uint32_t>(WOOD_AT + 0x84) == 0, "perk slot 1 back to none");
    check(at<float>(SWORD_AT + 0x40) == 10.0f && at<int32_t>(SWORD_AT + 0x44) == 10, "manaCost and its tooltip back");
    check(at<uint32_t>(0x40004) == 5, "loot back");
}

// fields the live back-end does not take are refused loudly; restart fields are not touched and not refused
void refusals_and_restart_fields() {
    world();
    g_files = {{"m/content/a.json", R"({"registry": {"alter": [
        {"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": 99999},
        {"registry": "balance", "select": {"id": "*"}, "field": "levelCap", "op": 30}
    ]}})"}};
    const auto files = files_of({{"m", "m/content/a.json"}});
    stage(files);
    apply_registry_files(files);
    for (const auto& r : g_last_report.refused) std::fprintf(stderr, "  refused: %s\n", r.reason.c_str());
    check(!reported("levelCap"), "a restart field is the boot back-end's: not refused here");
    check(at<uint32_t>(WOOD_AT) == 10, "an out-of-range value is not written");
}

// a changed section is composed again; a removed pack's registry layer leaves nothing behind; a new field needs a restart
void hot_reload() {
    world();
    g_files = {{"a/content/a.json", R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": {"mul": 4}}]}})"},
               {"b/content/b.json", R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": {"mul": 2}}]}})"}};
    auto files = files_of({{"a", "a/content/a.json"}, {"b", "b/content/b.json"}});
    stage(files);
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT) == 80, "x4 then x2 stack: 80");
    // b's section edited: x2 -> x3
    g_files["b/content/b.json"] = R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": {"mul": 3}}]}})";
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT) == 120, "recomposed: x4 then x3 = 120");
    const auto r = registry_live_result();
    check(r && r->fields.count({"items", "game:material_t1_wood_logs", "maxStack"}) &&
              r->fields.at({"items", "game:material_t1_wood_logs", "maxStack"}).result == registry::Value::integer(120),
          "registry.json has the new result");
    // a's pack removed
    files = files_of({{"b", "b/content/b.json"}});
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT) == 30, "a removed pack's x4 leaves nothing behind: x3 over 10");
    // b removed too: back to the game's value, no registry layer left
    files.clear();
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT) == 10, "no residue");
    check(!owner_has_layers(registry_owner()), "no registry layer left");
    // a new field the start never decoded needs a restart
    files = files_of({{"b", "b/content/b.json"}});
    g_files["b/content/b.json"] = R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "levels.min", "op": 5}]}})";
    apply_registry_files(files);
    check(reported("RestartRequired: items.levels.min: the base was read at start"), "a field the start never decoded: refused, needs a restart (the stable token first)");
}

// A reload whose registry edits need a restart says so where the admin looks: every such row of
// registry.json starts with the stable token "RestartRequired: " and names the mod and the place, and counts has their
// number. Before, only kiln.log said a restart field changes at the next start, and a new field's row had no token.
std::vector<registry::Refusal> restart_rows() {
    std::vector<registry::Refusal> out;
    for (const registry::Refusal& r : g_last_report.refused)
        if (r.reason.rfind("RestartRequired: ", 0) == 0) out.push_back(r);
    return out;
}
const registry::Refusal* restart_row(const std::string& mod, const std::string& field) {
    static registry::Refusal found;
    for (const registry::Refusal& r : restart_rows())
        if (r.mod == mod && r.field == field) return &(found = r);
    return nullptr;
}
void restart_required_on_reload() {
    world();
    const std::string start = R"({"registry": {"alter": [
        {"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": 50},
        {"registry": "balance", "select": {"id": "*"}, "field": "levelCap", "op": 30}]}})";
    g_files = {{"m/content/a.json", start}};
    const auto only_m = files_of({{"m", "m/content/a.json"}});
    stage(only_m);
    apply_registry_files(only_m);
    check(restart_rows().empty(), "restart: at the start nothing waits for a restart");
    // the reload: m's restart field changes, m names a field the start never decoded, and n brings a registry section the
    // start did not have
    g_files["m/content/a.json"] = R"({"registry": {"alter": [
        {"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": 50},
        {"registry": "balance", "select": {"id": "*"}, "field": "levelCap", "op": 40},
        {"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "levels.min", "op": 5}]}})";
    g_files["n/content/n.json"] = R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": {"mul": 2}}]}})";
    const auto both = files_of({{"m", "m/content/a.json"}, {"n", "n/content/n.json"}});
    apply_registry_files(both);
    for (const auto& r : g_last_report.refused) std::fprintf(stderr, "  refused: %s: %s\n", r.mod.c_str(), r.reason.c_str());
    check(at<uint32_t>(WOOD_AT) == 50, "restart: the live field still applies (maxStack 50)");
    const registry::Refusal* cap = restart_row("m", "levelCap");
    check(cap && cap->registry == "balance" && cap->reason.find("next start") != std::string::npos,
          "restart: a changed restart field is a RestartRequired row naming m, balance and levelCap, applying at the next start");
    const registry::Refusal* min = restart_row("m", "levels.min");
    check(min && min->registry == "items" && min->id == "game:material_t1_wood_logs",
          "restart: a field the start never decoded is a RestartRequired row naming m, items, the entry and levels.min");
    check(std::any_of(restart_rows().begin(), restart_rows().end(), [](const registry::Refusal& r) { return r.mod == "n"; }),
          "restart: a section added after the start is a RestartRequired row naming n");
    const registry::json doc = registry::report_json(registry_schema(), g_last_report, {});
    check(doc["counts"].value("restartRequired", 0) == 3, "restart: registry.json counts the three (counts.restartRequired)");
    // back to the start's edits: nothing waits any more
    g_files["m/content/a.json"] = start;
    apply_registry_files(only_m);
    check(restart_rows().empty(), "restart: the start's edits again, nothing waits for a restart");
    check(registry::report_json(registry_schema(), g_last_report, {})["counts"].value("restartRequired", -1) == 0,
          "restart: counts.restartRequired is 0");
}

// compare-before-write in the report: another writer's value stays
void another_writer_in_the_report() {
    world();
    g_files = {{"bigstacks/content/a.json",
                R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": {"mul": 4}}]}})"}};
    const auto files = files_of({{"bigstacks", "bigstacks/content/a.json"}});
    stage(files);
    apply_registry_files(files);
    mem<uint32_t>(WOOD_AT, 777);  // a ShroudForge run-time write in kiln mode
    apply_registry_files(files);   // a pack reload
    check(at<uint32_t>(WOOD_AT) == 777, "the other writer's value stays");
    check(reported("was written by another writer after Kiln's layer: Kiln's edit not applied"), "and registry.json says so");
    check(json_field({"items", "game:material_t1_wood_logs", "maxStack"})["result"] == 777, "the report shows the other writer's effective value");
    // P32: the row names the Kiln place, never the game's debug or member names
    check(reported("items[game:material_t1_wood_logs].maxStack was written by another writer after Kiln's layer"), "the skipped row names the Kiln field");
    check(!reported("Material_T1_Wood_Logs") && !reported("maxStackSize"), "no game debug or member name in the row");
    // the drift probe: the slot another writer changed after Kiln's layer gets a best-effort writer row, last
    const auto row = json_field({"items", "game:material_t1_wood_logs", "maxStack"});
    check(!row["writers"].empty(), "the field row has writers: " + row.dump());
    const auto& last = row["writers"].back();
    check(last["kind"] == "drift" && last["certainty"] == "bestEffort" && last["value"] == 777 && !last.contains("mod") &&
              last["who"].get<std::string>().rfind("outside Kiln (best effort", 0) == 0,
          "the foreign write is a best-effort drift row: " + row["writers"].dump());
}
// P32: a skipped revert (the pack removed after another writer took the slot) still names the Kiln place it had
void skipped_revert_names_the_kiln_place() {
    world();
    g_files = {{"bigstacks/content/a.json",
                R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": {"mul": 4}}]}})"}};
    stage(files_of({{"bigstacks", "bigstacks/content/a.json"}}));
    apply_registry_files(files_of({{"bigstacks", "bigstacks/content/a.json"}}));
    mem<uint32_t>(WOOD_AT, 777);
    apply_registry_files({});  // the pack removed: Kiln's revert is skipped
    check(at<uint32_t>(WOOD_AT) == 777, "the other writer's value stays");
    check(reported("items[game:material_t1_wood_logs].maxStack was written by another writer after Kiln's layer: Kiln's revert not applied"),
          "the skipped revert names the Kiln field");
    check(!reported("Material_T1_Wood_Logs") && !reported("maxStackSize"), "no game debug or member name in the row");
}

// The drift probe (commitment 7): a value that changed outside Kiln between the stage's read (10) and Kiln's first write at
// world load (12) is a best-effort writer row with a line, never a refusal; Kiln's relative edit composes on what the game
// holds. Without drift no such row; an entry the stage read the same as the game has none.
void drift_probe() {
    world();
    g_files = {{"bigstacks/content/a.json",
                R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": {"mul": 4}},
                                           {"registry": "items", "select": {"id": "game:material_stone"}, "field": "maxStack", "op": {"mul": 2}}]}})"}};
    const auto files = files_of({{"bigstacks", "bigstacks/content/a.json"}});
    stage(files);
    mem<uint32_t>(WOOD_AT, 12);  // another DLL engine wrote the logs' stack size after resources-ready
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT) == 48, "Kiln's x4 composes on the value the game holds");
    check(g_last_report.refused.empty(), "drift is never a refusal");
    const auto row = json_field({"items", "game:material_t1_wood_logs", "maxStack"});
    check(row["writers"].size() == 2 && row["writers"][0]["kind"] == "drift" && row["writers"][0]["certainty"] == "bestEffort" &&
              row["writers"][0]["value"] == 12 && row["writers"][1]["kind"] == "registry" && row["result"] == 48,
          "a drift row below the registry edit: " + row.dump());
    check(row["base"]["value"] == 10, "the base stays what the stage read");
    const registry::Line* line = nullptr;
    for (const registry::Line& l : g_last_report.lines)
        if (l.field.id == "game:material_t1_wood_logs") line = &l;
    check(line && line->certainty == registry::Certainty::BestEffort && line->kind == registry::LineKind::ComposedOn && line->winner == "bigstacks" &&
              line->text.find("changed outside Kiln") != std::string::npos && line->text.find("best effort") != std::string::npos,
          "one best-effort line: " + (line ? line->text : std::string("none")));
    const auto stone = json_field({"items", "game:material_stone", "maxStack"});
    check(stone["writers"].size() == 1 && stone["writers"][0]["kind"] == "registry", "no drift where the game holds what the stage read");
    // a reload checks again: the drift row stays (the slot's original is the value before Kiln's first write)
    apply_registry_files(files);
    check(json_field({"items", "game:material_t1_wood_logs", "maxStack"})["writers"][0]["kind"] == "drift", "a reload reports it again");
}
// registry-followups 1 (the compat rehearsal: EMF's big_stacks raised the logs to 1200 after the world load's report, and
// registry.json said 300 until the next pack reload). A write made after the report reaches registry.json without a reload:
// another loader's code change through kiln.content (as EMF's set_item_max_stack makes one) once the store's generation moved
// and a short wait passed, and a write outside Kiln's store (a DLL writing game memory) at the next modest timer. Looks in
// between do nothing (never per tick), a look that finds nothing changed rebuilds nothing, and a drift row is never a refusal.
uint64_t g_now = 1000;
void later_writes_reach_the_report() {
    world();
    g_registry_now_ms = [] { return g_now; };
    g_files = {{"compat/content/a.json",
                R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": 30}]}})"}};
    const auto files = files_of({{"compat", "compat/content/a.json"}});
    stage(files);
    registry_live_world_begin();
    apply_registry_files(files);
    const registry::FieldRef logs{"items", "game:material_t1_wood_logs", "maxStack"};
    check(at<uint32_t>(WOOD_AT) == 30 && json_field(logs)["result"] == 30, "the world load's report: the registry's 30");
    const int reports = g_reports;
    const size_t refused = g_last_report.refused.size();
    // EMF's big_stacks after the report: a code layer through kiln.content's set() (its source in the game's words)
    uint8_t b[4] = {120, 0, 0, 0};
    store_put(CODE, next_code_seq(), KILN_CONTENT_ITEMS, WOOD, 0, -1, A(WOOD_AT), 4, b, "owner 44: items Material_T1_Wood_Logs.maxStackSize");
    store_flush(nullptr);
    check(at<uint32_t>(WOOD_AT) == 120, "the code change is in the game");
    registry_live_publish();
    check(g_reports == reports, "not at the next tick: the look waits");
    g_now += 500;
    registry_live_publish();
    check(g_reports == reports, "nor half a second later");
    g_now += 2000;
    registry_live_publish();
    check(g_reports == reports + 1, "the store moved and the wait passed: registry.json is rebuilt (" + std::to_string(g_reports - reports) + ")");
    auto row = json_field(logs);
    check(row["result"] == 120 && row["writers"].back()["kind"] == "code" && row["writers"].back()["certainty"] == "certain",
          "the report has the later code writer: " + row.dump());
    // nothing changed since: later looks rebuild nothing, also a mod that sets the same value again
    g_now += 20000;
    registry_live_publish();
    store_put(CODE, next_code_seq(), KILN_CONTENT_ITEMS, WOOD, 0, -1, A(WOOD_AT), 4, b, "owner 44: items Material_T1_Wood_Logs.maxStackSize");
    store_flush(nullptr);
    g_now += 3000;
    registry_live_publish();
    check(g_reports == reports + 1, "the same value set again rebuilds nothing (" + std::to_string(g_reports - reports) + ")");
    // a write outside Kiln's store: the store's generation does not move, the timer's look finds it
    mem<uint32_t>(WOOD_AT, 777);
    g_now += 1000;
    registry_live_publish();
    check(g_reports == reports + 1, "not before the timer");
    g_now += 15000;
    registry_live_publish();
    check(g_reports == reports + 2, "the timer's look rebuilds registry.json (" + std::to_string(g_reports - reports) + ")");
    row = json_field(logs);
    const auto& last = row["writers"].back();
    check(row["result"] == 777 && last["kind"] == "drift" && last["certainty"] == "bestEffort" && last["value"] == 777 &&
              last["who"].get<std::string>().rfind("outside Kiln (best effort", 0) == 0 && row["winner"] == last["who"],
          "a later write outside Kiln is a best-effort row, last: " + row.dump());
    bool line = false;
    for (const registry::Line& l : g_last_report.lines)
        line = line || (l.field == logs && l.certainty == registry::Certainty::BestEffort && l.text.find("after Kiln's last write") != std::string::npos);
    check(line, "with a best-effort line");
    check(g_last_report.refused.size() == refused, "never a refusal");
    check(at<uint32_t>(WOOD_AT) == 777, "and nothing was written over it");
    g_registry_now_ms = &steady_ms;
}
// registry-followups 1: a code writer's row names the Kiln place and the mod, never the game's debug or member names (the
// rehearsal's "owner 67: items Material_T1_Wood_Logs.maxStackSize", EMF's big_stacks). kiln.content's own calls (set, set_stacks,
// set_ref, kiln.gamedata's values) describe their layer in the game's words after the owner's label, and kiln.log's conflict
// lines keep that; registry.json names "<mod>: <Kiln place>": the mod the owner's label gives, else the core's id of the owner
// ("emf:big_stacks"), else the label. A source the mod chose itself (Content.Apply's label, a file name) stays as it is.
void code_writers_are_named_in_kiln_words() {
    world();
    g_owner_name = [](KilnOwner o) -> std::string { return o == CODE ? "emf:big_stacks" : ""; };
    g_files = {{"compat/content/a.json",
                R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": 30}]}})"}};
    const auto files = files_of({{"compat", "compat/content/a.json"}});
    stage(files);
    registry_live_world_begin();
    apply_registry_files(files);
    auto code = [](KilnOwner owner, uint32_t v, const std::string& source) {
        uint8_t b[4];
        std::memcpy(b, &v, 4);
        check(store_put(owner, next_code_seq(), KILN_CONTENT_ITEMS, WOOD, 0, -1, A(WOOD_AT), 4, b, source), "code layer " + source);
    };
    set_owner_label(45, "mod bigstacks");
    set_owner_label(46, "mod chosen");
    set_owner_label(48, "mod files");
    code(CODE, 120, "owner 44: items Material_T1_Wood_Logs.maxStackSize");      // EMF's big_stacks through kiln.content's set()
    code(45, 121, "mod bigstacks: items Material_T1_Wood_Logs.maxStackSize");   // a Kiln mod's Content set()
    code(46, 122, "chosen x2: items *");                                         // Content.Apply with the mod's own label
    code(47, 123, "owner 47: items Material_T1_Wood_Logs.maxStackSize");        // an owner the core has no id for
    code(48, 124, "mod files: content/b.json: items Material_T1_Wood_Logs");     // ApplyFile: the mod's file and its own selector
    store_flush(nullptr);
    apply_registry_files(files);  // a reload rebuilds the rows
    const auto row = json_field({"items", "game:material_t1_wood_logs", "maxStack"});
    std::vector<registry::json> codes;
    for (const auto& w : row["writers"])
        if (w["kind"] == "code") codes.push_back(w);
    check(codes.size() == 5, "five code writers: " + row["writers"].dump());
    const std::string place = "items[game:material_t1_wood_logs].maxStack";
    check(codes[0]["who"] == "emf:big_stacks: " + place && !codes[0].contains("mod"), "the core's id of an unlabelled owner: " + codes[0].dump());
    check(codes[1]["who"] == "bigstacks: " + place && codes[1]["mod"] == "bigstacks", "a labelled mod: " + codes[1].dump());
    check(codes[2]["who"] == "chosen x2: items *" && codes[2]["mod"] == "chosen", "the mod's own label stays: " + codes[2].dump());
    check(codes[3]["who"] == "owner 47: " + place, "no id for the owner: its label, and the Kiln place: " + codes[3].dump());
    check(codes[4]["who"] == "mod files: content/b.json: items Material_T1_Wood_Logs" && codes[4]["mod"] == "files",
          "a file and selector the mod wrote stay: " + codes[4].dump());
    check(row["winner"] == "mod files: content/b.json: items Material_T1_Wood_Logs" && row["result"] == 124, "the last code writer wins");
    for (size_t i : {size_t(0), size_t(1), size_t(3)}) {
        const std::string text = codes[i].dump();
        check(text.find("Material_T1_Wood_Logs") == std::string::npos && text.find("maxStackSize") == std::string::npos,
              "no game debug or member name in Kiln's own wording: " + text);
    }
    // kiln.log's conflict lines keep the store's own text (diagnostics)
    bool kept = false;
    for (const Conflict& c : store_conflicts()) kept = kept || c.loser == "owner 44: items Material_T1_Wood_Logs.maxStackSize";
    check(kept, "the store's conflicts keep their text");
}
// registry-followups 3 (the compat rehearsal's mixed set: BigStacks' Content.Apply {"mul": 4, "max": 9999, "raiseOnly": true}
// gave wood logs 1000 over the registry's 2000). Content.* changes made by code sit above registry edits (D1, services/
// registry.md), and a data-pack op made by code is computed on the game's original value (services/content.md "an op relative
// to the game's original value"; raiseOnly: "never below the game's value"), never on what the registry made of it. That is
// 31.15's behaviour and stays (a later code layer wins, at the value it computed); registry.json now says so: one certain
// "overridden" line naming the code writer, the registry edit it replaced and both values. No line when code sets the
// registry's own value.
void code_over_a_registry_value_is_reported() {
    world();
    g_files = {{"m/content/a.json",
                R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": {"mul": 200}}]}})"}};
    const auto files = files_of({{"m", "m/content/a.json"}});
    stage(files);
    registry_live_world_begin();
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT) == 2000, "the registry's x200 over the game's 10");
    // BigStacks: Content.Apply's x4 raiseOnly, computed by kiln.content on the game's 10, a code layer of 40
    set_owner_label(45, "mod bigstacks");
    uint8_t b[4] = {40, 0, 0, 0};
    store_put(45, next_code_seq(), KILN_CONTENT_ITEMS, WOOD, 0, -1, A(WOOD_AT), 4, b, "bigstacks x4 max 9999: items *");
    store_flush(nullptr);
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT) == 40, "the code layer above the registry wins, at the value it computed (31.15's behaviour, kept)");
    const registry::FieldRef logs{"items", "game:material_t1_wood_logs", "maxStack"};
    const auto row = json_field(logs);
    check(row["result"] == 40 && row["winner"] == "bigstacks x4 max 9999: items *", "the report's result and winner: " + row.dump());
    const registry::Line* line = nullptr;
    for (const registry::Line& l : g_last_report.lines)
        if (l.field == logs && l.loser == "m") line = &l;
    check(line && line->kind == registry::LineKind::Overridden && line->certainty == registry::Certainty::Certain &&
              line->winner == "bigstacks x4 max 9999: items *" && line->text.find("sets 40 over m's 2000") != std::string::npos &&
              line->text.find("game's value, 10") != std::string::npos,
          "one overridden line names the code writer and the registry edit it replaced: " + (line ? line->text : std::string("none")));
    check(g_last_report.refused.empty(), "never a refusal");
    // code that sets the registry's own value: no line
    uint8_t same[4];
    const uint32_t v = 2000;
    std::memcpy(same, &v, 4);
    store_put(45, next_code_seq(), KILN_CONTENT_ITEMS, WOOD, 0, -1, A(WOOD_AT), 4, same, "bigstacks x4 max 9999: items *");
    store_flush(nullptr);
    apply_registry_files(files);
    bool any = false;
    for (const registry::Line& l : g_last_report.lines) any = any || (l.field == logs && l.loser == "m");
    check(!any && at<uint32_t>(WOOD_AT) == 2000, "code that keeps the registry's value: no line");
    // the value code's op was computed on is the one kiln.content holds as the original (the game's before any Kiln layer),
    // not the stage's base: here another loader made it 12 before Kiln's first write (a drift row), and the line says 12
    world();
    stage(files);
    registry_live_world_begin();
    mem<uint32_t>(WOOD_AT, 12);
    apply_registry_files(files);
    uint8_t c[4] = {48, 0, 0, 0};
    store_put(45, next_code_seq(), KILN_CONTENT_ITEMS, WOOD, 0, -1, A(WOOD_AT), 4, c, "bigstacks x4 max 9999: items *");
    store_flush(nullptr);
    apply_registry_files(files);
    line = nullptr;
    for (const registry::Line& l : g_last_report.lines)
        if (l.field == logs && l.loser == "m") line = &l;
    check(line && line->text.find("sets 48 over m's 2400") != std::string::npos && line->text.find("game's value, 12,") != std::string::npos,
          "the value code computed on: " + (line ? line->text : std::string("none")));
}
// Review (registry-followups 1): the later-writes look runs in the tick's publish under the store's lock, and read every bound
// slot in one tick (a slot's bytes and its store signature): about 19 ms at 40,000 slots with four layers each (a loot or
// stacks pack over every entry binds that many), every 10 s, and every 2 s while the store moves, also holding up the game's
// job threads waiting for the lock. A look now reads at most kLateBatch slots a tick and goes on at the next ticks; a write
// it finds anywhere is in registry.json within the look's ticks, as before.
void later_writes_look_a_batch_a_tick() {
    world();
    g_registry_now_ms = [] { return g_now; };
    constexpr size_t kBulk = 2500;  // more than two batches
    constexpr size_t kBulkAt = 0x50000;
    Registry& I = g_regs[KILN_CONTENT_ITEMS];
    std::vector<std::string> names;
    for (size_t i = 0; i < kBulk; ++i) names.push_back("Bulk_" + std::to_string(i));
    for (size_t i = 0; i < kBulk; ++i) {
        uint8_t guid[16] = {0xB0, 0x1C};
        std::memcpy(guid + 4, &i, sizeof i);
        add_entry(I, static_cast<uint32_t>(0x100000 + i), names[i].c_str(), kBulkAt + i * 4, guid);
        mem<uint32_t>(kBulkAt + i * 4, 20);
    }
    g_stage_extra = [](RegistryLive& in) {
        for (size_t i = 0; i < kBulk; ++i) {
            const std::string id = "game:bulk_" + std::to_string(i);
            registry::BaseModel::Entry e;
            e.id = id;
            e.fields["maxStack"] = registry::Value::integer(20);
            in.base.registries["items"].push_back(e);
            in.ids["items|" + id] = guid_text(g_regs[KILN_CONTENT_ITEMS].get(static_cast<uint32_t>(0x100000 + i))->guid);
            in.nums["items|" + id] = static_cast<uint32_t>(0x100000 + i);
        }
    };
    g_files = {{"bulk/content/a.json",
                R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:bulk_*"}, "field": "maxStack", "op": {"mul": 2}}]}})"}};
    const auto files = files_of({{"bulk", "bulk/content/a.json"}});
    stage(files);
    registry_live_world_begin();
    apply_registry_files(files);
    check(at<uint32_t>(kBulkAt) == 40 && at<uint32_t>(kBulkAt + (kBulk - 1) * 4) == 40, "the pack's x2 on every bulk item");
    const int reports = g_reports;
    g_now += 11000;  // the timer's look is due
    g_rd_calls = 0;
    registry_live_publish();
    check(g_rd_calls <= kLateBatch, "one tick of the look reads at most a batch of slots (" + std::to_string(g_rd_calls) + " reads)");
    check(g_reports == reports, "nothing changed so far");
    // a write outside Kiln on the last bound slot: found by the same look, a batch a tick
    mem<uint32_t>(kBulkAt + (kBulk - 1) * 4, 777);
    int ticks = 1;
    for (; ticks < 16 && g_reports == reports; ++ticks) {
        g_rd_calls = 0;
        registry_live_publish();
        check(g_reports != reports || g_rd_calls <= kLateBatch, "every tick of the look reads at most a batch (" + std::to_string(g_rd_calls) + ")");
    }
    check(g_reports == reports + 1, "the write is in registry.json within the look (" + std::to_string(ticks) + " ticks)");
    const auto row = json_field({"items", "game:bulk_" + std::to_string(kBulk - 1), "maxStack"});
    check(row["result"] == 777 && row["writers"].back()["kind"] == "drift", "a best-effort row, last: " + row.dump());
    // the next look starts over from the new report: nothing changed since, nothing rebuilt
    const int after = g_reports;
    g_now += 11000;
    for (int t = 0; t < 16; ++t) registry_live_publish();
    check(g_reports == after, "a whole look over unchanged slots rebuilds nothing");
    g_registry_now_ms = &steady_ms;
}
// Review (registry-followups 1): kiln.gamedata's values (t_set, t_set_json) word their layer "<label>: items <game name>.<config
// path>" as kiln.content's do, on a slot of a gamedata kind: the config value items.manaCost and items.staminaCost write. Its
// row kept the game's item and member names ("mod spelltweaks: items Sword_T2.impactValues.0xaf236ece.value"); it is in Kiln
// words like the other code writers.
void gamedata_code_writer_in_kiln_words() {
    world();
    g_files = {{"m/content/a.json",
                R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:sword_t2"}, "field": "manaCost", "op": {"mul": 1.5}}]}})"}};
    const auto files = files_of({{"m", "m/content/a.json"}});
    stage(files);
    registry_live_world_begin();
    apply_registry_files(files);
    check(at<float>(SWORD_AT + 0x40) == 15.0f, "the registry's x1.5 on the mana cost");
    set_owner_label(45, "mod spelltweaks");
    const float v = 20.0f;
    uint8_t b[4];
    std::memcpy(b, &v, 4);
    // as gamedata_api.cpp t_set_json puts it, on the config value's slot (gd_item_config_slot: a gamedata kind, the item's id)
    check(store_put(45, next_code_seq(), 1000, SWORD, 7, -1, A(SWORD_AT + 0x40), 4, b, "mod spelltweaks: items Sword_T2.impactValues.0xaf236ece.value"),
          "kiln.gamedata's code layer");
    store_flush(nullptr);
    apply_registry_files(files);
    const auto row = json_field({"items", "game:sword_t2", "manaCost"});
    registry::json code;
    for (const auto& w : row["writers"])
        if (w["kind"] == "code") code = w;
    check(!code.is_null() && code["who"] == "spelltweaks: items[game:sword_t2].manaCost" && code["mod"] == "spelltweaks",
          "the mod and the Kiln place: " + row["writers"].dump());
    check(row.dump().find("Sword_T2") == std::string::npos && row.dump().find("impactValues") == std::string::npos,
          "no game item or member name in the row: " + row.dump());
}
// Review (registry-followups 3): the code-over-registry line said the code writer "sets" the value the game holds now. After a
// later write outside Kiln (the late look's best-effort row) that is the other writer's value, not the code's: the line named
// BigStacks as setting 777, and a code layer that kept the registry's own value got the line too. It names the value the
// code layer gives; the write outside Kiln has its own best-effort line.
void code_over_registry_line_names_the_code_value() {
    world();
    g_registry_now_ms = [] { return g_now; };
    g_files = {{"m/content/a.json",
                R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": {"mul": 200}}]}})"}};
    const auto files = files_of({{"m", "m/content/a.json"}});
    stage(files);
    registry_live_world_begin();
    apply_registry_files(files);
    const registry::FieldRef logs{"items", "game:material_t1_wood_logs", "maxStack"};
    set_owner_label(45, "mod bigstacks");
    auto code = [](uint32_t v) {
        uint8_t b[4];
        std::memcpy(b, &v, 4);
        store_put(45, next_code_seq(), KILN_CONTENT_ITEMS, WOOD, 0, -1, A(WOOD_AT), 4, b, "bigstacks x4 max 9999: items *");
        store_flush(nullptr);
    };
    auto code_line = [&]() -> const registry::Line* {
        for (const registry::Line& l : g_last_report.lines)
            if (l.field == logs && l.loser == "m" && l.certainty == registry::Certainty::Certain) return &l;
        return nullptr;
    };
    code(40);
    apply_registry_files(files);
    mem<uint32_t>(WOOD_AT, 777);  // a DLL writes the game's memory after it
    g_now += 11000;
    registry_live_publish();
    auto row = json_field(logs);
    check(row["result"] == 777 && row["writers"].back()["kind"] == "drift", "the late look's best-effort row: " + row.dump());
    const registry::Line* line = code_line();
    check(line && line->text.find("sets 40 over m's 2000") != std::string::npos && line->text.find("777") == std::string::npos,
          "the line names what the code layer sets: " + (line ? line->text : std::string("none")));
    // code that keeps the registry's value, then a write outside Kiln: the code replaced nothing, no code line
    code(2000);
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT) == 2000 && !code_line(), "code that keeps the registry's value: no line");
    mem<uint32_t>(WOOD_AT, 777);
    g_now += 11000;
    registry_live_publish();
    row = json_field(logs);
    check(row["result"] == 777, "the write outside Kiln is in the report: " + row.dump());
    check(!code_line(), "still no code line: " + (code_line() ? code_line()->text : std::string()));
    g_registry_now_ms = &steady_ms;
}
// Review (registry-writers): an entry an old boot document wrote (boot_bridge.cpp's entry mark: a path no Kiln field's row
// covers, such as a formula's input or "[*]" over a registry's elements) holds Kiln's own patch where the stage read the
// value before it: the drift probe never calls that change another loader's. An entry no document wrote is still checked.
void drift_probe_kiln_boot_patch() {
    world();
    g_files = {{"bigstacks/content/a.json",
                R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": {"mul": 4}},
                                           {"registry": "items", "select": {"id": "game:material_stone"}, "field": "maxStack", "op": {"mul": 2}}]}})"}};
    const auto files = files_of({{"bigstacks", "bigstacks/content/a.json"}});
    registry::OldWrite mark;
    mark.kind = registry::WriterKind::OldPatch, mark.mod = "oldmod", mark.who = "oldmod/stacks", mark.field = {"items", "game:material_t1_wood_logs", ""};
    stage(files, {}, "", {}, {mark});
    mem<uint32_t>(WOOD_AT, 12);   // oldmod's boot patch: the game holds 12 where the stage read 10
    mem<uint32_t>(STONE_AT, 70);  // the stone: no document wrote it, so a change there is still drift
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT) == 48, "Kiln's x4 composes on the value the game holds");
    const auto logs = json_field({"items", "game:material_t1_wood_logs", "maxStack"});
    for (const auto& w : logs["writers"]) check(w["kind"] != "drift", "no drift row on an entry Kiln's boot patch wrote: " + logs.dump());
    for (const registry::Line& l : g_last_report.lines)
        check(!(l.field.id == "game:material_t1_wood_logs" && l.certainty == registry::Certainty::BestEffort), "no best-effort line there: " + l.text);
    const auto stone = json_field({"items", "game:material_stone", "maxStack"});
    check(!stone["writers"].empty() && stone["writers"][0]["kind"] == "drift" && stone["writers"][0]["value"] == 70,
          "an entry no document wrote is still checked: " + stone.dump());
}
// P2: an explicit retained live change, reverted by handle, must compose over packs and never overtake code.
void live_transactions() {
    world();
    g_files.clear();
    stage({});
    registry_live_world_begin();
    apply_registry_files({});
    uint64_t h = 0;
    RegistryLiveReply reply;
    check(registry_live_begin(77, "change", 4, &h) == KILN_OK && h, "begin an owner-scoped live transaction");
    const std::string ir = R"({"ir":1,"mod":"change","stage":"alter","edits":[{"registry":"items","select":"game:material_t1_wood_logs","field":"maxStack","op":{"mul":3}}]})";
    check(registry_live_submit(77, h, ir, &reply, nullptr) == KILN_OK, "submit Kiln fields");
    check(registry_live_commit(77, h, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::Applied, "commit the live transaction");
    check(at<uint32_t>(WOOD_AT) == 30, "the live change writes through the real store");
    registry_live_publish();
    check(registry::report_json(registry_schema(), g_last_report, {}).dump().find("live:") != std::string::npos, "the live owner and transaction are in the report");
    check(registry_live_revert(78, h, &reply) == KILN_ERR_INVALID_ARGUMENT, "another owner cannot revert it");
    check(registry_live_revert(77, h, &reply) == KILN_OK && at<uint32_t>(WOOD_AT) == 10, "revert restores the layer below");
    check(registry_live_revert(77, h, &reply) == KILN_ERR_NOT_FOUND, "revert does not reuse stale handles");
}

// review2 S5 remainder: kiln.registry.live's C table (registry/live_table.cpp: the wrappers kiln-content.dll registers),
// bound to a fake owner table and generation: argument and owner checks, the result struct, the copies out.
std::string fake_bound_mod(KilnOwner owner, bool* revoked) {
    *revoked = owner == 99;  // revoked (or never made)
    return owner == 77 ? "change" : std::string();  // 55: an owner bound to no mod id
}
bool utf8_whole(const char* s) {
    for (size_t i = 0; s[i];) {
        const unsigned char c = static_cast<unsigned char>(s[i]);
        const size_t n = c < 0x80 ? 1 : (c & 0xe0) == 0xc0 ? 2 : (c & 0xf0) == 0xe0 ? 3 : (c & 0xf8) == 0xf0 ? 4 : 0;
        if (!n) return false;
        for (size_t k = 1; k < n; ++k)
            if ((static_cast<unsigned char>(s[i + k]) & 0xc0) != 0x80) return false;
        i += n;
    }
    return true;
}
void live_c_table() {
    world(); g_files.clear(); stage({}); registry_live_world_begin(); apply_registry_files({});
    registry::stage::LiveTableEnv env;
    env.bound_mod = &fake_bound_mod;
    env.generation = [] { return uint64_t(41); };
    const KilnRegistryLiveApi* t = registry::stage::live_table(env);
    check(t->header.size == sizeof(KilnRegistryLiveApi) && t->header.version == KILN_REGISTRY_LIVE_VERSION, "C table: header size and version");
    KilnHandle h = 0;
    check(t->begin(77, KILN_REGISTRY_LIVE_MANUAL, nullptr) == KILN_ERR_INVALID_ARGUMENT, "C begin: no out handle is refused");
    check(t->begin(0, KILN_REGISTRY_LIVE_MANUAL, &h) == KILN_ERR_INVALID_ARGUMENT && !h, "C begin: owner 0 is refused");
    check(t->begin(55, KILN_REGISTRY_LIVE_MANUAL, &h) == KILN_ERR_INVALID_ARGUMENT && !h, "C begin: an owner bound to no mod is refused");
    check(t->begin(99, KILN_REGISTRY_LIVE_MANUAL, &h) == KILN_ERR_REVOKED && !h, "C begin: a revoked owner is REVOKED");
    check(t->begin(77, KILN_REGISTRY_LIVE_MANUAL, &h) == KILN_OK && h, "C begin: the bound owner gets a handle");
    const std::string ir = R"({"ir":1,"mod":"change","stage":"alter","edits":[{"registry":"items","select":"game:material_t1_wood_logs","field":"maxStack","op":{"mul":3}}]})";
    const KilnStr irs{ir.data(), ir.size()};
    KilnRegistryLiveResult r{};
    r.size = sizeof r - 8;
    check(t->submit(77, h, irs, &r, nullptr, 0, nullptr) == KILN_ERR_INVALID_ARGUMENT, "C submit: a result struct shorter than v1 is refused");
    r.size = sizeof r;
    check(t->submit(77, h, irs, &r, nullptr, 8, nullptr) == KILN_ERR_INVALID_ARGUMENT, "C submit: a cap without a buffer is refused");
    check(t->submit(77, h, KilnStr{nullptr, 4}, &r, nullptr, 0, nullptr) == KILN_ERR_INVALID_ARGUMENT, "C submit: a null text with a length is refused");
    check(t->submit(99, h, irs, &r, nullptr, 0, nullptr) == KILN_ERR_REVOKED, "C submit: a revoked owner is REVOKED");
    size_t len = 0;
    char rep[8];
    check(t->submit(77, h, irs, &r, rep, sizeof rep, &len) == KILN_OK && r.size == sizeof r && r.accepted == 1 && r.generation == 41,
          "C submit: accepted, the result struct filled (size, accepted, the stage's generation)");
    check(len > 0 && std::strlen(rep) == std::min(len, sizeof rep - 1), "C submit: the report is cut to the buffer, 0-terminated, its whole length in out_len");
    std::memset(&r, 0x5a, sizeof r);
    r.size = sizeof r + 16;  // a newer caller's bigger struct: v1's part is written, the size says v1
    const KilnResult committed = t->commit(77, h, &r);
    const size_t used = strnlen(r.reason, sizeof r.reason);
    check(committed == KILN_OK && r.outcome == KILN_REGISTRY_LIVE_APPLIED && r.size == sizeof r && r.generation == 41 && r.reserved == 0 &&
              used < sizeof r.reason && std::all_of(r.reason + used, r.reason + sizeof r.reason, [](char c) { return c == 0; }),
          "C commit: Applied, the struct zeroed then filled (outcome " + std::to_string(r.outcome) + ", reason \"" + std::string(r.reason, used) + "\")");
    check(at<uint32_t>(WOOD_AT) == 30, "C commit: the change is written (10 x3)");
    char buf[2];
    len = 0;
    const char* reg = "items"; const char* id = "game:material_t1_wood_logs"; const char* field = "maxStack";
    const KilnStr R{reg, 5}, I{id, std::strlen(id)}, F{field, std::strlen(field)};
    check(t->read(77, R, I, F, buf, sizeof buf, &len) == KILN_ERR_BUFFER_TOO_SMALL && len == 2 && buf[0] == '3' && buf[1] == 0,
          "C read: a buffer too small says so, the cut text 0-terminated, the whole length in out_len");
    char whole[16];
    check(t->read(77, R, I, F, whole, sizeof whole, &len) == KILN_OK && std::string(whole) == "30", "C read: the value as text");
    check(t->read(77, R, I, F, nullptr, 4, &len) == KILN_ERR_INVALID_ARGUMENT, "C read: a cap without a buffer is refused");
    check(t->read(55, R, I, F, whole, sizeof whole, &len) == KILN_ERR_INVALID_ARGUMENT, "C read: an owner bound to no mod is refused");
    KilnRegistryLiveStatus st{};
    st.size = sizeof st - 4;
    check(t->status(&st) == KILN_ERR_INVALID_ARGUMENT && t->status(nullptr) == KILN_ERR_INVALID_ARGUMENT, "C status: a short or missing struct is refused");
    st.size = sizeof st;
    check(t->status(&st) == KILN_OK && st.size == sizeof st && st.generation == 41 && st.world != 0 && st.world_kind == KILN_WORLD_SERVER && st.active >= 1,
          "C status: the world, its kind, the generation and the active changes");
    check(t->revert(99, h, &r) == KILN_ERR_REVOKED && at<uint32_t>(WOOD_AT) == 30, "C revert: a revoked owner is REVOKED, nothing changes");
    r.size = 4;
    check(t->revert(77, h, &r) == KILN_ERR_INVALID_ARGUMENT && at<uint32_t>(WOOD_AT) == 30, "C revert: a short result struct is refused before anything changes");
    r.size = sizeof r;
    check(t->revert(77, h, &r) == KILN_OK && r.outcome == KILN_REGISTRY_LIVE_REVERTED && at<uint32_t>(WOOD_AT) == 10, "C revert: Reverted, the value back");
    check(t->commit(77, h, nullptr) == KILN_ERR_NOT_FOUND, "C commit: a reverted handle is gone (a null result struct is allowed)");
    // a long reason is cut on a UTF-8 boundary: an entry id of 2-byte characters (no such entry in this world, so the
    // binding refuses it and names its place) runs past the 192-byte reason; the odd prefix puts a cut inside a character
    for (const char* prefix : {"game:x", "game:xy"}) {
        KilnHandle h2 = 0;
        t->begin(77, KILN_REGISTRY_LIVE_MANUAL, &h2);
        std::string id = prefix;
        for (int i = 0; i < 150; ++i) id += "\xc3\xa9";  // é
        const std::string bad = R"({"ir":1,"mod":"change","stage":"alter","edits":[{"registry":"items","select":")" + id + R"(","field":"maxStack","op":3}]})";
        t->submit(77, h2, KilnStr{bad.data(), bad.size()}, &r, nullptr, 0, nullptr);
        t->commit(77, h2, &r);
        const size_t n = std::strlen(r.reason);
        check(n > 0 && n < sizeof r.reason && utf8_whole(r.reason) && r.outcome != KILN_REGISTRY_LIVE_APPLIED,
              "C result: a long reason is cut on a UTF-8 boundary and 0-terminated (" + std::to_string(n) + " bytes): " + std::string(r.reason, std::min<size_t>(n, 60)));
        t->revert(77, h2, &r);
    }
}

void live_transaction_foreign_revert() {
    world(); g_files.clear(); stage({}); registry_live_world_begin(); apply_registry_files({});
    uint64_t h = 0; RegistryLiveReply reply;
    registry_live_begin(77, "change", 4, &h);
    registry_live_submit(77, h, R"({"ir":1,"mod":"change","stage":"alter","edits":[{"registry":"items","select":"game:material_t1_wood_logs","field":"maxStack","op":90}]})", &reply, nullptr);
    registry_live_commit(77, h, &reply);
    mem<uint32_t>(WOOD_AT, 777);
    check(registry_live_revert(77, h, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::Skipped, "a foreign write makes the revert explicitly skipped");
    check(at<uint32_t>(WOOD_AT) == 777, "revert never overwrites the foreign writer");
}

void live_transaction_points_and_revoke() {
    world(); g_files.clear(); stage({});
    uint64_t h = 0; RegistryLiveReply reply;
    registry_live_begin(77, "change", 1, &h);
    registry_live_submit(77, h, R"({"ir":1,"mod":"change","stage":"alter","edits":[{"registry":"items","select":"game:material_t1_wood_logs","field":"maxStack","op":90}]})", &reply, nullptr);
    check(registry_live_commit(77, h, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::PendingWorld, "mod-load changes can wait for the first world");
    check(at<uint32_t>(WOOD_AT) == 10, "no premature game write before world readiness");
    registry_live_world_begin(); apply_registry_files({});
    check(at<uint32_t>(WOOD_AT) == 90, "the queued mod-load change applies at world load");
    registry_live_owner_gone(77); registry_live_publish();
    check(at<uint32_t>(WOOD_AT) == 10, "revoked live owners lose their live layers on the world thread");
    check(registry_live_begin(77, "change", 2, &h) == KILN_ERR_REVOKED, "a revoked owner cannot create more transactions");
    registry_live_begin(88, "later", 3, &h);
    registry_live_submit(88, h, R"({"ir":1,"mod":"later","stage":"alter","edits":[{"registry":"balance","select":"game:balance","field":"levelCap","op":60}]})", &reply, nullptr);
    check(registry_live_commit(88, h, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::RestartRequired, "a restart field is never silently written from hot reload");
}
}  // namespace

// registry-rollback, package K (registry/saves.h): a write that would change what existing saves hold, at the world's load
int g_guard_calls = 0;
bool g_guard_ok = true;
bool fake_guard(std::string* why) {
    ++g_guard_calls;
    if (!g_guard_ok) *why = "not on this build";
    return g_guard_ok;
}
void save_checks_at_world_load() {
    world();
    g_registry_stack_guard = &fake_guard;
    g_guard_calls = 0, g_guard_ok = true;
    // a lower max stack than the old pack's (30): the stack guard goes on, then the layer is written
    g_files = {{"m/content/a.json", R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": 20}]}})"}};
    auto files = files_of({{"m", "m/content/a.json"}});
    stage(files);
    put_pack(WOOD_AT, 30);
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT) == 20 && g_guard_calls > 0, "lowering the old pack's 30 to 20: written, the stack guard on first (" + std::to_string(at<uint32_t>(WOOD_AT)) + ")");
    check(!reported("saves:"), "and not refused");
    // the guard not available here: refused for that entry, the pack's 30 stays (no saved stack loses items)
    world();
    g_guard_ok = false;
    stage(files);
    put_pack(WOOD_AT, 30);
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT) == 30 && reported("stack guard is not available here") && reported("this edit was not applied"),
          "without the guard the lowering is refused, with why; the value under it stays (" + std::to_string(at<uint32_t>(WOOD_AT)) + ")");
    // raising needs no guard from the registry (the store's own guard follows a raise, as before)
    world();
    g_guard_calls = 0, g_guard_ok = false;
    g_files["m/content/a.json"] = R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": {"mul": 4}}]}})";
    stage(files);
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT) == 40 && g_guard_calls == 0 && !reported("saves:"), "a raise is written without asking the guard");
    // a narrower level range: refused (a load would clamp saved copies and the save keep it); a wider one is written
    world();
    g_guard_ok = true;
    g_files["m/content/a.json"] = R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:sword_t2"}, "field": "levels.max", "op": 12}]}})";
    stage(files);
    apply_registry_files(files);
    check(at<uint32_t>(SWORD_AT + 0x14) == 15 && reported("narrows the level range"), "levels.max 15 -> 12: refused, the range stays 15");
    world();
    g_files["m/content/a.json"] = R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:sword_t2"}, "field": "levels.max", "op": 20}]}})";
    stage(files);
    apply_registry_files(files);
    check(at<uint32_t>(SWORD_AT + 0x14) == 20 && !reported("saves:"), "levels.max 15 -> 20: written");
    g_registry_stack_guard = &fake_guard, g_guard_ok = true;
}

// review of registry-rollback: a start on a rolled-back plan. A pack reload's changed registry section is RestartRequired, not
// composed over the plan's edits; a mod the plan leaves out has its live changes RestartRequired too; a plan mod's are applied.
void rolled_back_start_live() {
    world();
    g_files = {{"m/content/a.json", R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": 20}]}})"}};
    auto files = files_of({{"m", "m/content/a.json"}});
    stage(files, {}, std::string(64, 'a'), {"later"});
    registry_live_world_begin();
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT) == 20, "the plan's edit is written at the world's load");
    g_files["m/content/a.json"] = R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": 40}]}})";
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT) == 20 && reported("RestartRequired: this start runs on the rolled-back registry plan aaaaaaaaaaaa"),
          "a pack reload's changed section is not composed over the plan: RestartRequired");
    uint64_t h = 0;
    RegistryLiveReply reply;
    check(registry_live_begin(88, "later", 4, &h) == KILN_OK, "begin (a mod the plan leaves out)");
    registry_live_submit(88, h, R"({"ir":1,"mod":"later","stage":"alter","edits":[{"registry":"items","select":"game:material_t1_wood_logs","field":"maxStack","op":90}]})", &reply, nullptr);
    check(registry_live_commit(88, h, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::RestartRequired && at<uint32_t>(WOOD_AT) == 20,
          "a left-out mod's live change is RestartRequired, nothing written (" + std::to_string(at<uint32_t>(WOOD_AT)) + ")");
    uint64_t h2 = 0;
    check(registry_live_begin(77, "m", 4, &h2) == KILN_OK, "begin (a mod of the plan)");
    registry_live_submit(77, h2, R"({"ir":1,"mod":"m","stage":"alter","edits":[{"registry":"items","select":"game:material_t1_wood_logs","field":"maxStack","op":30}]})", &reply, nullptr);
    check(registry_live_commit(77, h2, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::Applied && at<uint32_t>(WOOD_AT) == 30,
          "a plan mod's live change is applied over the plan (" + std::to_string(at<uint32_t>(WOOD_AT)) + ")");
}

int main() {
    std::vector<std::string> errs;
    g_registry_stack_guard = &fake_guard;  // the host has no game code to patch: the guard as on a build that has it
    g_regmap = std::make_shared<registry::map::RegMap>();
    if (!registry::map::load_dir(KILN_REGMAP_DIR, "server", "f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2", g_regmap.get(), &errs) || !g_regmap->has_build) {
        std::printf("FAIL cannot load the regmap at %s\n", KILN_REGMAP_DIR);
        return 1;
    }
    const std::pair<const char*, void (*)()> tests[] = {
        {"relative_over_packs_and_reload", relative_over_packs_and_reload},
        {"callback_order_survives_reloads", callback_order_survives_reloads},
        {"live_recipe_report", live_recipe_report},
        {"live_formula_report", live_formula_report},
        {"report_caps_lists", report_caps_lists},
        {"every_live_kind", every_live_kind},
        {"perks_costs_unlock_loot", perks_costs_unlock_loot},
        {"refusals_and_restart_fields", refusals_and_restart_fields},
        {"hot_reload", hot_reload},
        {"restart_required_on_reload", restart_required_on_reload},
        {"another_writer_in_the_report", another_writer_in_the_report},
        {"live_c_table", live_c_table},
        {"skipped_revert_names_the_kiln_place", skipped_revert_names_the_kiln_place},
        {"drift_probe", drift_probe},
        {"drift_probe_kiln_boot_patch", drift_probe_kiln_boot_patch},
        {"later_writes_reach_the_report", later_writes_reach_the_report},
        {"code_writers_are_named_in_kiln_words", code_writers_are_named_in_kiln_words},
        {"code_over_a_registry_value_is_reported", code_over_a_registry_value_is_reported},
        {"later_writes_look_a_batch_a_tick", later_writes_look_a_batch_a_tick},
        {"gamedata_code_writer_in_kiln_words", gamedata_code_writer_in_kiln_words},
        {"code_over_registry_line_names_the_code_value", code_over_registry_line_names_the_code_value},
        {"live_transactions", live_transactions},
        {"live_transaction_foreign_revert", live_transaction_foreign_revert},
        {"live_transaction_points_and_revoke", live_transaction_points_and_revoke},
        {"save_checks_at_world_load", save_checks_at_world_load},
        {"rolled_back_start_live", rolled_back_start_live},
    };
    int failed = 0;
    for (auto& [name, fn] : tests) {
        try {
            fn();
            std::printf("ok   %s\n", name);
        } catch (const std::exception& e) {
            ++failed;
            std::printf("FAIL %s: %s\n", name, e.what());
        }
    }
    std::printf("registry live: %d checks, %d failed tests\n", checks, failed);
    return failed ? 1 : 0;
}
