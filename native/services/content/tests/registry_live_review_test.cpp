// SPDX-License-Identifier: MIT
// kiln.content, host test: the registry line review's findings on the live back-end and the store (out/registry-review,
// L1-L3, moved here), over the real store. Harness as registry_live_test.cpp. registry_live_review_test.sh builds and runs it.
#include <windows.h>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <set>
#include <stdexcept>
#include <string>
#include <thread>
#include <sys/stat.h>
#include "../content.h"
#include "../refs.h"
#include "../registry/formulas.h"
#include "../registry/schema_ir.h"
#include "../registry_live.h"
#include "../store.cpp"
#include "../registry_live.cpp"
#include "../registry/pristine.h"

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
std::filesystem::path g_pack_root;
}  // namespace
uintptr_t A(size_t off) { return reinterpret_cast<uintptr_t>(g_arena) + off; }
void logf(int, const char* fmt, ...) {
    va_list a;
    va_start(a, fmt);
    std::vfprintf(stderr, fmt, a);
    va_end(a);
    std::fputc('\n', stderr);
}
bool rd(uintptr_t address, void* out, size_t n) {
    if (address < A(0) || address + n > A(sizeof g_arena)) return false;
    std::memcpy(out, reinterpret_cast<const void*>(address), n);
    return true;
}
size_t write_batch(std::vector<Write>& writes, uintptr_t*) {
    for (const Write& w : writes) std::memcpy(reinterpret_cast<void*>(w.addr), w.bytes, w.n);
    return writes.size();
}
void* near_alloc(uintptr_t, size_t n) {
    void* p = g_arena + g_near;
    g_near += (n + 15) & ~size_t(15);
    return p;
}
Registry& reg(uint32_t kind) { return g_regs[kind]; }
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
std::vector<size_t> g_stack_at = {0x31000, 0x31040};  // where the table's stacks are (newcontent may move them)
bool walk_drop_table(uintptr_t resource, std::vector<DropStackRef>* out) {
    out->clear();
    if (resource != A(0x30000)) return false;
    for (size_t s : g_stack_at) out->push_back({A(s), 0});
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
// kiln.gamedata's item configs: the sword has a ManaCost config (float at +0x40) with a tooltip row (+0x44, Normal format)
bool gd_item_config_slot(const Entry& e, const std::string& path, GdValueSlot* out, std::string* why) {
    if (e.id != 0x222 || path != "impactValues.0xaf236ece") return *why = "the item has no such configuration (Kiln's live back-end does not add one)", false;
    out->addr = e.addr + 0x40, out->kind = 1000, out->id = e.id, out->field = 7;
    out->f.path = "impactValues.0xaf236ece.value", out->f.offset = 0, out->f.bytes = 4, out->f.vt = KILN_CV_FLOAT, out->f.writable = true;
    out->tip = e.addr + 0x44, out->tip_format = 0;
    return true;
}
bool gd_ui_scale(uint8_t, double v, int64_t* out) { return *out = std::llround(v), true; }
// dev.32's gamedata-section registries are not part of this review's cases (registry_gd_live_test.cpp has them)
bool gd_section_entry(uint32_t, const std::string&, Entry*) { return false; }
bool gd_value_slot(uint32_t, const Entry&, const std::string&, GdValueSlot*, std::string* why) { return *why = "none here", false; }
bool gd_shown_config(uint32_t, const Entry&, const std::string&, std::string*, std::string* why) { return *why = "none here", false; }
bool gd_list_size(uint32_t, const Entry&, const std::string&, uint32_t*, std::string* why) { return *why = "none here", false; }
bool read_recipe_stacks(const Entry&, uint32_t, std::vector<KilnContentStack>*) { return false; }
bool nc_is_new(uint32_t, uint32_t) { return false; }
void nc_pack_add(KilnOwner, uint32_t, const std::string&, const std::string&, ApplyResult*) {}
// packs.cpp's per-mod rollback of a pack that throws: no new content in this test
void nc_pack_mark(KilnOwner) {}
uint32_t nc_pack_rollback(KilnOwner) { return 0; }
void nc_pack_unmark() {}
bool is_kitbash_key(const Schema&, const std::string&) { return false; }
void kitbash(const Schema&, const std::string&, const nlohmann::ordered_json&, const ItemFind&,
             const std::vector<uintptr_t>&, KitbashOut*, uintptr_t) {}
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
bool g_report_application = false;

void world() {
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
    Registry& L = g_regs[KILN_CONTENT_LOOT];
    L.kind = KILN_CONTENT_LOOT, L.label = "loot", L.available = true;
    add_field(L.schema, fld("stackSizeMin", 0x00, 4, KILN_CV_INT));
    add_field(L.schema, fld("stackSizeMax", 0x04, 4, KILN_CV_INT));
    add_entry(L, WOOD, "Material_T1_Wood_Logs", 0x40000, G1);
    mem<uint32_t>(0x40000, 1), mem<uint32_t>(0x40004, 5);
    mem<uint32_t>(STONE_AT, 50);
    mem<float>(SWORD_AT + 0x08, 1.0f), mem<float>(SWORD_AT + 0x0C, 1.0f), mem<uint32_t>(SWORD_AT + 0x10, 10), mem<uint32_t>(SWORD_AT + 0x14, 15);
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
    w.fields["rarity"] = registry::Value::text("common");
    w.fields["perks"] = registry::Value::of_list({registry::Value::text("game:perk_a")});
    items.push_back(w);
    registry::BaseModel::Entry s;
    s.id = "game:sword_t2";
    double power = 0;
    registry::formulas::read("gear.power", {1.0, 1.0, 10}, registry::formulas::constants_of({}), &power);
    s.fields["power"] = registry::Value::integer(std::llround(power));
    s.fields["maxStack"] = registry::Value::integer(1);
    s.fields["manaCost"] = registry::Value::number(10);
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
    b.registries["loot"].push_back(lt);
    return b;
}

std::vector<PackFile> files_of(const std::vector<std::pair<std::string, std::string>>& mods_files) {
    std::vector<PackFile> out;
    for (auto& [mod, rel] : mods_files) {
        PackFile f;
        f.mod = mod, f.rel = rel, f.version = "1.0.0";
        f.path = (g_pack_root / rel).string();
        out.push_back(f);
    }
    return out;
}

void write_pack(const std::string& rel, const std::string& text) {
    const auto path = g_pack_root / rel;
    std::filesystem::create_directories(path.parent_path());
    std::ofstream file(path, std::ios::binary | std::ios::trunc);
    file << text;
    file.close();
    if (!file) throw std::runtime_error("cannot write pack fixture " + path.string());
}

// What S11 does at resources-ready: lower the sections, compose, install.
void stage(const std::vector<PackFile>& files) {
    for (const auto& [rel, text] : g_files) write_pack(rel, text);
    const registry::Schema& sc = registry_schema();
    std::vector<registry::ModInput> mods;
    for (const RegistrySection& s : registry_sections(files, sc)) {
        auto it = std::find_if(mods.begin(), mods.end(), [&](const registry::ModInput& m) { return m.id == s.mod; });
        if (it == mods.end()) mods.push_back({}), it = mods.end() - 1, it->id = s.mod;
        for (auto& b : s.batches) it->batches.push_back(b);
        for (auto& r : s.refused) it->refused.push_back(r);
    }
    RegistryLive in;
    in.schema = std::make_shared<const registry::Schema>(sc);
    in.regmap = g_regmap;
    in.base = base();
    in.mods = mods;
    in.result = registry::compose(sc, in.base, {}, mods);
    in.ids = {{"items|game:material_t1_wood_logs", guid_text(G1)}, {"items|game:sword_t2", guid_text(G2)}, {"items|game:material_stone", guid_text(G4)},
              {"recipes|game:plank_recipe", guid_text(G3)}, {"drops|game:dt_wolf", guid_text(G3)},
              {"loot|game:material_t1_wood_logs", guid_text(G1)}, {"perks|game:perk_a", guid_text(P1)}, {"perks|game:perk_b", guid_text(P2)}};
    in.nums = {{"items|game:material_t1_wood_logs", WOOD}, {"items|game:sword_t2", SWORD}, {"items|game:material_stone", STONE},
               {"recipes|game:plank_recipe", PLANK}, {"drops|game:dt_wolf", 0x444},
               {"loot|game:material_t1_wood_logs", WOOD}, {"perks|game:perk_a", 0x901}, {"perks|game:perk_b", 0x902}};
    in.decoded = {"items.perks", "items.manaCost", "items.staminaCost", "recipes.unlock", "loot.stack.max", "items.maxStack", "items.rarity", "items.power", "recipes.duration", "recipes.inputs", "recipes.outputs", "drops.entries"};
    in.report = [](const registry::Result& r, bool application) {
        g_last_report = r;
        g_report_application = application;
        ++g_reports;
    };
    registry_live_install(std::move(in));
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

}  // namespace

// When new content adds to a selector group, it copies the group's stacks to near memory, repoints them and applies the rules again.
namespace kc {
bool g_repoint = false;
void nc_drops_table(const Entry& table) {
    if (!g_repoint) return;
    g_repoint = false;
    std::vector<size_t> moved;
    for (size_t s : g_stack_at) {
        std::memcpy(g_arena + s + 0x1000, g_arena + s, 0x40);
        store_originals(A(s), 0x40, g_arena + s + 0x1000);  // as newcontent copies a group: the game's values, not Kiln's layers
        moved.push_back(s + 0x1000);
    }
    g_stack_at = moved;
    drops_apply_rules(table);
}
// content.cpp note_table: the acquire path
void note_table_as_content_cpp(const Entry& t) {
    drops_table_loaded(t);
    std::string err;
    store_flush(&err);
}
}  // namespace kc

namespace {
// L1: a registry drop-table edit whose table the world loads after the world-load application never gets its layers when
// no old data pack has drop rules: registry_live_table is reached only through drops_apply_rules, which content.cpp's
// acquire hook calls only when drops_have_rules().
void drop_table_without_old_rules() {
    world();
    g_files = {{"m/content/a.json", R"({"registry": {"alter": [{"registry": "drops", "select": {"id": "game:dt_wolf"}, "field": "entries[*].count.max", "op": {"mul": 2}}]}})"}};
    const auto files = files_of({{"m", "m/content/a.json"}});
    stage(files);
    apply_registry_files(files);
    check(g_report_application, "the world load publishes a new application");
    check(at<uint32_t>(0x31018) == 3, "no table yet");
    add_drop_table();
    const auto& table = g_regs[KILN_CONTENT_DROPS].entries.back();
    const std::string source = "olddrops: content/a.json";
    const uint32_t counts[] = {4, 7};
    set_owner_label(PACK, "pack olddrops");
    for (int i = 0; i < 2; ++i)
        store_put(PACK, kPackSeq, KILN_CONTENT_DROPS, table.id, 1, i, A(0x31018 + 0x40 * i), 4,
                  reinterpret_cast<const uint8_t*>(&counts[i]), source);
    note_table_as_content_cpp(table);
    check(at<uint32_t>(0x31018) == 8 && at<uint32_t>(0x31058) == 14, "count.max x2 over the old layers once acquired");
    const int reports = g_reports;
    registry_live_publish();
    check(g_reports == reports + 1, "the acquired table refreshes the published report");
    check(!g_report_application, "the drop-table refresh keeps the world-load application");
    const registry::FieldRef ref{"drops", "game:dt_wolf", "entries"};
    const auto& outcome = g_last_report.fields.at(ref);
    check(outcome.result.list[0].leaf("count.max")->as_double() == 8 && outcome.result.list[1].leaf("count.max")->as_double() == 14,
          "the live drop result contains both updated stacks");
    check(outcome.chain.size() == 2 && outcome.chain[0].kind == registry::WriterKind::OldPack && outcome.chain[0].who == source &&
              outcome.chain[0].after.list[0].leaf("count.max")->as_double() == 4 && outcome.chain[0].after.list[1].leaf("count.max")->as_double() == 7,
          "one old writer row captures both stack writes");
    check(std::any_of(g_last_report.lines.begin(), g_last_report.lines.end(), [&](const registry::Line& line) {
        return line.field == ref && line.kind == registry::LineKind::ComposedOn && line.loser == source && line.winner == "m";
    }), "the drop-list composedOn line names the old pack");
    registry_live_publish();
    check(g_reports == reports + 1, "the next publish has no duplicate report");
}

// L1b: newcontent repoints the table's stacks when the table loads: the registry's layers follow them to the new stacks.
void drop_table_repointed_by_newcontent() {
    world();
    g_stack_at = {0x31000, 0x31040};
    g_files = {{"m/content/a.json", R"({"registry": {"alter": [{"registry": "drops", "select": {"id": "game:dt_wolf"}, "field": "entries[*].count.max", "op": {"mul": 2}}]}})"}};
    const auto files = files_of({{"m", "m/content/a.json"}});
    stage(files);
    apply_registry_files(files);
    add_drop_table();
    note_table_as_content_cpp(g_regs[KILN_CONTENT_DROPS].entries.back());
    check(at<uint32_t>(0x31018) == 6, "bound when the table loads");
    // definitions change later (newcontent drops_sync): the group's stacks are copied and repointed, the rules applied again
    g_repoint = true;
    nc_drops_table(g_regs[KILN_CONTENT_DROPS].entries.back());
    std::string ferr;
    store_flush(&ferr);
    check(at<uint32_t>(0x32018) == 6 && at<uint32_t>(0x32058) == 6, "the stacks the game reads now have count.max x2 (now " + std::to_string(at<uint32_t>(0x32018)) + ")");
    check(at<uint32_t>(0x31018) == 3, "the stacks it no longer reads keep no registry layer (now " + std::to_string(at<uint32_t>(0x31018)) + ")");
    g_stack_at = {0x31000, 0x31040};
}

// L2: a hot reload that recomposes (registry_live.cpp recompose) replaces the handed-over result with a fresh compose():
// the refusals the stage made before compose (unmapped/unproven fields, unusable sections, faulted callbacks, boot writes
// that failed: stage.cpp run() 590-597 and finish() 654-658) leave registry.json.
void reload_loses_stage_refusals() {
    world();
    g_files = {{"a/content/a.json", R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": {"mul": 4}}]}})"},
               {"b/content/b.json", R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": {"mul": 2}}]}})"}};
    const auto files = files_of({{"a", "a/content/a.json"}, {"b", "b/content/b.json"}});
    stage(files);
    {
        // what stage.cpp run() hands over: pre-compose refusals at the front of result.refused
        std::lock_guard lock(store_mutex());
        registry::Refusal x;
        x.mod = "a", x.source = "content/a.json", x.stage = registry::Stage::Alter, x.n = 1;
        x.reason = "items game:material_t1_wood_logs: block: Unproven on this build; this edit was not applied";
        g.in->result.refused.insert(g.in->result.refused.begin(), x);
        g.in->pre_refused.push_back(x);  // the stage's handoff (stage_service: LiveHandoff.pre_refused)
        // Installation freezes the stage baseline for later entry points; hand over the complete stage result.
        registry_live_install(*g.in);
    }
    apply_registry_files(files);
    check(reported("Unproven"), "the world-load report has the stage's refusal");
    g_files["b/content/b.json"] = R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": {"mul": 3}}]}})";
    write_pack(files.back().rel, g_files[files.back().rel]);
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT) == 120, "recomposed: x4 then x3");
    check(reported("Unproven"), "after b's section is reloaded registry.json still names a's refused edit");
}
// L2b: a reload keeps the restart fields' outcomes as the boot back-end wrote them, and runs the stage's mapping check on the
// reloaded sections.
void reload_keeps_boot_outcomes_and_checks_sections() {
    world();
    g_files = {{"a/content/a.json", R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": {"mul": 4}}]}})"}};
    const auto files = files_of({{"a", "a/content/a.json"}});
    stage(files);
    const registry::FieldRef cap{"balance", "game:balance", "levelCap"};
    {
        std::lock_guard lock(store_mutex());
        registry::FieldOutcome fo;
        fo.base = registry::Value::integer(50), fo.result = registry::Value::integer(60), fo.winner = "a";
        g.in->boot_fields[cap] = fo;
        g.in->result.fields[cap] = fo;
        g.in->check = [](registry::Batch* b, std::vector<registry::Refusal>* r) {
            std::erase_if(b->edits, [&](const registry::Edit& e) {
                if (e.field != "rarity") return false;
                r->push_back({b->mod, b->source, b->stage, e.n, "Unproven: items.rarity is not proven for this game build; this edit was not applied", ""});
                return true;
            });
        };
        registry_live_install(*g.in); // boot outcomes are part of the immutable installed handoff too
    }
    apply_registry_files(files);
    g_files["a/content/a.json"] = R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": {"mul": 3}},
        {"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "rarity", "op": "epic"}]}})";
    write_pack(files.front().rel, g_files[files.front().rel]);
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT) == 30, "recomposed: x3");
    check(g_last_report.fields.count(cap) && g_last_report.fields.at(cap).result == registry::Value::integer(60), "the boot back-end's levelCap 60 stays in registry.json");
    check(reported("Unproven: items.rarity"), "the reloaded section's unproven edit is refused by the stage's check");
    check(at<uint32_t>(WOOD_AT + 4) == 0, "and not written");
}

// L4 (review finding 10): registry.json is written with no lock of kiln.content's held.
void report_written_outside_the_store_lock() {
    world();
    g_files = {{"a/content/a.json", R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": {"mul": 4}}]}})"}};
    const auto files = files_of({{"a", "a/content/a.json"}});
    stage(files);
    bool free = false, called = false;
    {
        std::lock_guard lock(store_mutex());
        g.in->report = [&](const registry::Result&, bool) {
            called = true;
            std::thread t([&] {
                free = store_mutex().try_lock();
                if (free) store_mutex().unlock();
            });
            t.join();
        };
    }
    const auto schema = registry_live_schema();
    const auto sections = registry_sections(files, *schema);
    {
        std::lock_guard lock(store_mutex());  // apply_all_packs's hold
        registry_live_apply(sections, schema.get());
        check(!called, "not published under the store's lock");
    }
    registry_live_publish();
    check(called && free, "published with the store's lock free (another thread took it)");
}

void pack_read_outside_the_store_lock() {
    world();
    const std::string rel = "lock-probe/content/pack.json";
    std::filesystem::remove(g_pack_root / rel);  // discard a FIFO left by an interrupted run
    g_files = {{rel, R"({"items":{"Material_T1_Wood_Logs":{"maxStackSize":20}},"registry":{"alter":[{"registry":"items","select":{"id":"game:material_t1_wood_logs"},"field":"maxStack","op":{"mul":2}}]}})"}};
    const auto files = files_of({{"lock-probe", rel}});
    stage(files);
    KilnApi api{};
    api.owner_create = [](KilnStr) -> KilnOwner { return 31; };
    g_api = &api;
    apply_all_packs(files);
    g_api = nullptr;
    check(at<uint32_t>(WOOD_AT) == 40, "world load applies the registry over the legacy pack layer");

    const auto& path = files.front().path;
    const auto replacement = path + ".next";
    write_pack(rel + ".next", "{}");
    std::filesystem::remove(path);
    check(mkfifo(path.c_str(), 0600) == 0, "create the pack read probe");
    bool free = false;
    bool written = false;
    // The writer can only open the FIFO once the real pack reader reaches it. Probe the recursive mutex from this thread
    // before delivering bytes. Replace the path before EOF so any second open would see a different registry section.
    std::jthread writer([&] {
        std::ofstream pipe(path, std::ios::binary);
        free = store_mutex().try_lock();
        if (free) store_mutex().unlock();
        pipe << R"({"items":{"Material_T1_Wood_Logs":{"maxStackSize":30}},"registry":{"alter":[{"registry":"items","select":{"id":"game:material_t1_wood_logs"},"field":"maxStack","op":{"mul":3}}]}})";
        std::error_code err;
        std::filesystem::rename(replacement, path, err);
        pipe.close();
        written = pipe && !err;
    });
    const auto loaded = apply_all_packs(files);
    writer.join();
    check(written && loaded.files.size() == 1 && loaded.files.front().second.errors.empty(), "read the pack fixture");
    check(free, "another thread can take store_mutex during the pack file read");
    check(at<uint32_t>(WOOD_AT) == 90, "reload composes legacy and registry layers from the same file contents");
    std::filesystem::remove(path);
    apply_all_packs({});
    check(at<uint32_t>(WOOD_AT) == 10, "removing the pack drops both of its layers");
}

// L3: store.cpp store_flush: when compare-before-write fires on a slot in the same flush as an old pack's layer change
// (every pack application re-puts the pack layers: apply_all_packs drops and puts them, old_changed = true), the
// "continue" also drops the pack's write and clears old_changed, so the old pack's new value is never written that time.
// Ruling (store, 5:22 PM) and lane D's report: old packs keep writing such a slot as they do today.
void pack_write_lost_on_detection() {
    world();
    g_files = {{"bigstacks/content/a.json",
                R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": {"mul": 4}}]}})"}};
    const auto files = files_of({{"bigstacks", "bigstacks/content/a.json"}});
    stage(files);
    put_pack(WOOD_AT, 30);
    apply_registry_files(files);
    check(at<uint32_t>(WOOD_AT) == 120, "x4 over the pack's 30");
    mem<uint32_t>(WOOD_AT, 777);  // another writer (a ShroudForge run-time write in kiln mode)
    // a pack reload as apply_all_packs does it: the pack's layer dropped and put again with its new value, then the registry
    store_drop_owner(PACK);
    put_pack(WOOD_AT, 50);
    apply_registry_files(files);
    check(reported("another writer"), "the registry's edit is skipped and reported");
    check(at<uint32_t>(WOOD_AT) == 50, "the old pack's new value is written as today (memory " + std::to_string(at<uint32_t>(WOOD_AT)) + ")");
}
}  // namespace

// review4 NS1: a lazy base decode reads the game's resident resource bytes; Kiln's own store writes in them (packs, Content
// code, registry layers) are taken back out, so the base, VIEW_BASE and the cache digest are the game's.
struct ResidentView final : kiln::boot_view::BaseView {
    uintptr_t at = 0; size_t len = 0; bool released = false;
    const char* source() const override { return "live"; }
    bool ready(std::string*) override { return true; }
    std::string digest() override { return "inner"; }
    KilnResult tree(std::string_view, size_t, size_t, uint64_t, std::string*, size_t*, size_t*, std::string* why) override {
        return *why = "bytes only", KILN_ERR_UNAVAILABLE;
    }
    KilnResult bytes(const KilnAssetKey&, size_t, std::string* out, std::string*) override {
        out->assign(reinterpret_cast<const char*>(at), len);  // resource_read: a copy of the resident bytes
        return KILN_OK;
    }
    void release() override { released = true; }
};
void lazy_decode_reads_pristine_bytes() {
    world();
    ResidentView inner; inner.at = A(0x10000); inner.len = 0x40;
    auto resident = [&](const KilnAssetKey&, uintptr_t* at, size_t* len) { *at = inner.at, *len = inner.len; return true; };
    auto restore = [](uintptr_t at, size_t len, uint8_t* bytes) { store_pristine(at, len, bytes); };
    const KilnAssetKey key{};
    std::string before, why;
    registry::PristineView fresh(inner, resident, restore);
    check(fresh.bytes(key, 1 << 20, &before, &why) == KILN_OK && before.size() == 0x40, "the fresh view reads the resource");
    const std::string pristine_digest = fresh.digest();
    const uint32_t v = 777;
    store_put(11, (1ull << 44) | 1, KILN_CONTENT_ITEMS, 0x111, 0, -1, A(0x10000), 4, reinterpret_cast<const uint8_t*>(&v), "pack");
    store_put(11, (1ull << 44) | 1, KILN_CONTENT_ITEMS, 0x111, -1, -1, A(0x10010), 4, reinterpret_cast<const uint8_t*>(&v), "pack");
    std::string error; store_flush(&error);
    check(std::memcmp(g_arena + 0x10000, &v, 4) == 0, "Kiln's pack write is in the resident bytes");
    registry::PristineView later(inner, resident, restore);
    std::string after;
    check(later.bytes(key, 1 << 20, &after, &why) == KILN_OK && after == before,
          "the lazy view takes Kiln's own writes back out of the resident bytes");
    check(later.digest() == pristine_digest, "so its digest matches the base decoded before the writes (the cache stays valid)");
    std::string raw; inner.bytes(key, 0, &raw, &why);
    check(raw != before, "the inner view alone would have served Kiln's bytes");
    store_reset(true, &error);
    check(std::memcmp(g_arena + 0x10000, before.data(), 4) == 0, "the store's reset restores memory");
    std::string again; registry::PristineView reset(inner, resident, restore);
    check(reset.bytes(key, 1 << 20, &again, &why) == KILN_OK && again == before, "no stale original outlives the store's slots");
}

int main() {
    g_registry_stack_guard = [](std::string*) { return true; };  // the host has no game code to patch: the guard as on a build that has it
    g_pack_root = std::filesystem::path(std::getenv("KILN_OUT")) / "content-tests/registry-live-review-packs";
    std::vector<std::string> errs;
    g_regmap = std::make_shared<registry::map::RegMap>();
    if (!registry::map::load_dir(KILN_REGMAP_DIR, "server", "f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2", g_regmap.get(), &errs) || !g_regmap->has_build) {
        std::printf("FAIL cannot load the regmap at %s\n", KILN_REGMAP_DIR);
        return 2;
    }
    int failed = 0;
    const std::pair<const char*, void (*)()> groups[] = {{"L1 drop_table_without_old_rules", &drop_table_without_old_rules},
                                                         {"L1b drop_table_repointed_by_newcontent", &drop_table_repointed_by_newcontent},
                                                         {"L2 reload_loses_stage_refusals", &reload_loses_stage_refusals},
                                                         {"L2b reload_keeps_boot_outcomes_and_checks_sections", &reload_keeps_boot_outcomes_and_checks_sections},
                                                         {"L3 pack_write_lost_on_detection", &pack_write_lost_on_detection},
                                                         {"L4 report_written_outside_the_store_lock", &report_written_outside_the_store_lock},
                                                         {"L4 pack_read_outside_the_store_lock", &pack_read_outside_the_store_lock},
                                                         {"NS1 lazy_decode_reads_pristine_bytes", &lazy_decode_reads_pristine_bytes}};
    for (const auto& [name, fn] : groups) {
        try {
            fn();
            std::printf("registry-live-review: PASS %s\n", name);
        } catch (const std::exception& e) {
            ++failed;
            std::printf("registry-live-review: FAIL %s: %s\n", name, e.what());
        }
    }
    std::printf("registry-live-review: %d check(s), %d failed group(s)\n", checks, failed);
    return failed ? 1 : 0;
}
