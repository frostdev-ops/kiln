// SPDX-License-Identifier: MIT
// The game's side of registry_model_test.cpp: gamedata's walker (gd::resolve, gd::node_json) over hand-made types, with
// content.h's helpers faked, in a unit of its own (content.h cannot share one with the registry's headers).
#include "../gamedata_walk.cpp"
#include "../registry/model_bytes.cpp"

#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <stdexcept>


namespace kc {
void logf(int, const char*, ...) {}
uintptr_t unreadable_test_address = 0;
bool rd(uintptr_t address, void* out, size_t n) {
    if (address == unreadable_test_address) return false;
    std::memcpy(out, reinterpret_cast<const void*>(address), n);
    return true;
}
std::string read_blob_string(uintptr_t addr) {
    int32_t rel = 0;
    uint32_t len = 0;
    std::memcpy(&rel, reinterpret_cast<const void*>(addr), 4);
    std::memcpy(&len, reinterpret_cast<const void*>(addr + 4), 4);
    if (!rel || !len) return {};
    return std::string(reinterpret_cast<const char*>(addr + static_cast<intptr_t>(rel)), len);
}
std::string lower(std::string s) {
    for (char& c : s)
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    return s;
}
std::string guid_text(const uint8_t g[16]) {
    static constexpr char hx[] = "0123456789abcdef";
    std::string s;
    for (int i = 0; i < 16; ++i) {
        if (i == 4 || i == 6 || i == 8 || i == 10) s += '-';
        s += hx[g[i] >> 4], s += hx[g[i] & 15];
    }
    return s;
}
std::string ref_text(const uint8_t g[16]) {
    for (int i = 0; i < 16; ++i)
        if (g[i]) return guid_text(g);
    return "none";
}
bool guid_parse(const std::string& s, uint8_t out[16]) {
    std::string h;
    for (char c : s)
        if (c != '-') h += c;
    if (h.size() != 32) return false;
    for (int i = 0; i < 16; ++i) out[i] = static_cast<uint8_t>(std::stoul(h.substr(static_cast<size_t>(i) * 2, 2), nullptr, 16));
    return true;
}
uint32_t fnv1a(const void* data, size_t n) {
    uint32_t h = 2166136261u;
    for (size_t i = 0; i < n; ++i) h = (h ^ static_cast<const uint8_t*>(data)[i]) * 16777619u;
    return h;
}
KilnContentValue decode(const Field& f, const uint8_t* b) {
    KilnContentValue v{};
    v.type = f.vt;
    if (f.vt == KILN_CV_GUID) std::memcpy(v.guid, b, 16);
    else if (f.vt == KILN_CV_TIME) {  // as refl.cpp: the nanoseconds in i, the seconds in f
        int64_t ns = 0;
        std::memcpy(&ns, b, 8);
        v.i = ns;
        v.f = static_cast<double>(ns) / 1e9;
    } else if (f.vt == KILN_CV_FLOAT) {
        float x;
        std::memcpy(&x, b, 4);
        v.f = x;
    } else {
        uint64_t u = 0;
        std::memcpy(&u, b, f.bytes);
        v.i = static_cast<int64_t>(u);
        v.f = f.vt == KILN_CV_TIME ? static_cast<double>(v.i) / 1e9 : static_cast<double>(u);  // keen::Time: seconds, as refl.cpp
    }
    return v;
}
const KilnApi* g_api = nullptr;
bool path_exists(const std::string&) { return false; }
std::string data_dir() { return {}; }
double as_double(const KilnContentValue& v) { return v.type == KILN_CV_INT ? static_cast<double>(v.i) : v.f; }
bool encode(const Field& f, const KilnContentValue& v, uint8_t* bytes, std::string*) {
    if (f.vt == KILN_CV_TIME) {  // as refl.cpp: a time value's nanoseconds, else seconds as given
        const int64_t ns = v.type == KILN_CV_TIME && v.f == 0 ? v.i : static_cast<int64_t>(std::llround(as_double(v) * 1e9));
        std::memcpy(bytes, &ns, 8);
        return true;
    }
    if (f.vt == KILN_CV_FLOAT) {
        const float x = static_cast<float>(v.f);
        std::memcpy(bytes, &x, 4);
    } else {
        const int64_t i = v.type == KILN_CV_FLOAT ? static_cast<int64_t>(v.f) : v.i;
        std::memcpy(bytes, &i, f.bytes);
    }
    return true;
}
const Entry* Registry::lookup(const std::string&) const { return nullptr; }
Registry& reg(uint32_t) {
    static Registry r;
    return r;
}
const Field* Schema::find(const std::string&) const { return nullptr; }
namespace gd {
const GEntry* GRegistry::lookup(const std::string&) const { return nullptr; }
const GEntry* GRegistry::get(uint32_t) const { return nullptr; }
GRegistry& greg(uint32_t) {
    static GRegistry r;
    return r;
}
bool buff_guid(uint32_t, uint8_t[16]) { return false; }
std::map<std::string, const TType*>& named() {
    static std::map<std::string, const TType*> m;
    return m;
}
const TType* TType::resolved() const { return this; }
const TType* TType::inner() const { return reinterpret_cast<const TType*>(inner_rec); }
const TType* TField::type() const { return reinterpret_cast<const TType*>(type_rec); }
const TField* TType::field(const std::string& n) const {
    for (const TField& f : fields)
        if (lower(f.name) == lower(n)) return &f;
    return nullptr;
}
std::map<uint32_t, const TType*>& hashed() {
    static std::map<uint32_t, const TType*> m;
    return m;
}
const TType* type_hash(uint32_t h) {
    const auto it = hashed().find(h);
    return it == hashed().end() ? nullptr : it->second;
}
const TType* type_named(const std::string& n) {
    const auto it = named().find(n);
    return it == named().end() ? nullptr : it->second;
}
bool res_list(uint32_t, std::vector<std::array<uint8_t, 16>>*) { return false; }
uintptr_t res_data(const uint8_t[16], uint32_t, uint64_t*) { return 0; }
}  // namespace gd
namespace rbtest {
void item_type() {
    using namespace kc::gd;
    static TType u16, u32, guid, str, item;
    auto rec = [](const TType& t) { return reinterpret_cast<uintptr_t>(&t); };
    u16.name = u16.brief = "uint16", u16.kind = K_U16, u16.size = 2;
    u32.name = u32.brief = "uint32", u32.kind = K_U32, u32.size = 4;
    guid.name = guid.brief = "keen::Guid", guid.kind = K_GUID, guid.size = 16;
    str.name = str.brief = "keen::BlobString", str.kind = K_BLOBSTRING, str.size = 8;
    item.name = "keen::ItemInfo", item.brief = "ItemInfo", item.kind = K_STRUCT, item.size = 40;
    item.fields = {{"itemId", rec(u32), 0}, {"objectId", rec(guid), 4}, {"maxStackSize", rec(u16), 0x14}, {"debugName", rec(str), 0x18}};
    named()["keen::ItemInfo"] = &item;
}
void balance_type() {
    using namespace kc::gd;
    static TType u32, bal;
    u32.name = u32.brief = "uint32", u32.kind = K_U32, u32.size = 4;
    bal.name = "keen::BalancingTable", bal.brief = "BalancingTable", bal.kind = K_STRUCT, bal.size = 8;
    bal.fields = {{"playerLevelCap", reinterpret_cast<uintptr_t>(&u32), 0}, {"skillPointsPerLevel", reinterpret_cast<uintptr_t>(&u32), 4}};
    named()["keen::BalancingTable"] = &bal;
}
// keen::ByteTable {altarsPerFlameLevel u8[21] +0, playerLevelCap u32 +24}: the real balance table's per-level counts are byte
// arrays of more than 16 elements, which the gamedata dumps show as "(21 bytes)"
void byte_blob_type(uintptr_t unreadable) {
    using namespace kc::gd;
    unreadable_test_address = unreadable;
    static TType u8, blob, tab;
    u8.kind = K_U8, u8.size = 1;
    blob.kind = K_BLOBARRAY, blob.size = 8, blob.inner_rec = reinterpret_cast<uintptr_t>(&u8);
    tab.kind = K_STRUCT, tab.size = 8, tab.name = "test::ByteBlob";
    tab.fields = {{"values", reinterpret_cast<uintptr_t>(&blob), 0}};
    named()[tab.name] = &tab;
}
// test::Wrapped {id: test::ItemId {value u32} +0, pair: test::Pair {a u32 +0, b u32 +4} +4}: the game's ids (keen::ItemId,
// keen::RecipeId) are one-scalar structs, which the walker reads as the scalar (gd::leaf_of), not as {"value": n}
void wrapped_id_type() {
    using namespace kc::gd;
    static TType u32, id, pair, w;
    auto rec = [](const TType& t) { return reinterpret_cast<uintptr_t>(&t); };
    u32.name = u32.brief = "uint32", u32.kind = K_U32, u32.size = 4;
    id.name = "test::ItemId", id.brief = "ItemId", id.kind = K_STRUCT, id.size = 4;
    id.fields = {{"value", rec(u32), 0}};
    pair.name = "test::Pair", pair.brief = "Pair", pair.kind = K_STRUCT, pair.size = 8;
    pair.fields = {{"a", rec(u32), 0}, {"b", rec(u32), 4}};
    w.name = "test::Wrapped", w.brief = "Wrapped", w.kind = K_STRUCT, w.size = 12;
    w.fields = {{"id", rec(id), 0}, {"pair", rec(pair), 4}};
    named()[w.name] = &w;
}
void byte_table_type() {
    using namespace kc::gd;
    static TType u8, u32, arr, tab;
    auto rec = [](const TType& t) { return reinterpret_cast<uintptr_t>(&t); };
    u8.name = u8.brief = "uint8", u8.kind = K_U8, u8.size = 1;
    u32.name = u32.brief = "uint32", u32.kind = K_U32, u32.size = 4;
    arr.name = arr.brief = "keen::StaticArray<uint8,21>", arr.kind = K_ARRAY, arr.size = 21, arr.count = 21, arr.inner_rec = rec(u8);
    tab.name = "keen::ByteTable", tab.brief = "ByteTable", tab.kind = K_STRUCT, tab.size = 28;
    tab.fields = {{"altarsPerFlameLevel", rec(arr), 0}, {"playerLevelCap", rec(u32), 24}};
    named()["keen::ByteTable"] = &tab;
}
// dev.32 abilities: keen::actor::ActorSequenceResource {resourceId Guid +0, subSequences BlobArray<ActorSubSequenceResource> +16}, a
// sub-sequence {events BlobArray<BlobVariant<keen::SequenceEvent>> +0, length keen::Time +8, name BlobString +20} of 32 bytes, and
// two event types (keen::actor::PayUsageCost 0x4e5c0937, keen::actor::SpawnEntityEvent 0x51e20bf3; a SpawnEntityEvent of another
// namespace with the same last name, which a qualified selector must not take): each {time keen::Time +0, duration +8}
void sequence_types() {
    using namespace kc::gd;
    static TType s64, guid, str, time, base, pay, spawn, dsspawn, var, events, sub, subs, seq;
    auto rec = [](const TType& t) { return reinterpret_cast<uintptr_t>(&t); };
    s64.name = s64.brief = "sint64", s64.kind = K_S64, s64.size = 8;
    guid.name = guid.brief = "keen::Guid", guid.kind = K_GUID, guid.size = 16;
    str.name = str.brief = "keen::BlobString", str.kind = K_BLOBSTRING, str.size = 8;
    time.name = "keen::Time", time.brief = "Time", time.kind = K_STRUCT, time.size = 8, time.fields = {{"value", rec(s64), 0}};
    base.name = "keen::SequenceEvent", base.brief = "SequenceEvent", base.kind = K_STRUCT, base.size = 16;
    base.fields = {{"time", rec(time), 0}, {"duration", rec(time), 8}};
    for (TType* t : {&pay, &spawn, &dsspawn}) t->kind = K_STRUCT, t->size = 16, t->inner_rec = rec(base), t->fields = base.fields;
    pay.name = "keen::actor::PayUsageCost", pay.brief = "PayUsageCost", pay.hash = 0x4e5c0937u;
    spawn.name = "keen::actor::SpawnEntityEvent", spawn.brief = "SpawnEntityEvent", spawn.hash = 0x51e20bf3u;
    dsspawn.name = "keen::ds::actor::SpawnEntityEvent", dsspawn.brief = "SpawnEntityEvent", dsspawn.hash = 0xc089f628u;
    var.name = "keen::BlobVariant<keen::SequenceEvent>", var.brief = "BlobVariant<keen::SequenceEvent>", var.kind = K_BLOBVAR, var.size = 12, var.inner_rec = rec(base);
    events.name = events.brief = "keen::BlobArray<keen::BlobVariant<keen::SequenceEvent>>", events.kind = K_BLOBARRAY, events.size = 8, events.inner_rec = rec(var);
    sub.name = "keen::actor::ActorSubSequenceResource", sub.brief = "ActorSubSequenceResource", sub.kind = K_STRUCT, sub.size = 32;
    sub.fields = {{"events", rec(events), 0}, {"length", rec(time), 8}, {"name", rec(str), 20}};
    subs.name = subs.brief = "keen::BlobArray<keen::actor::ActorSubSequenceResource>", subs.kind = K_BLOBARRAY, subs.size = 8, subs.inner_rec = rec(sub);
    seq.name = "keen::actor::ActorSequenceResource", seq.brief = "ActorSequenceResource", seq.kind = K_STRUCT, seq.size = 24;
    seq.fields = {{"resourceId", rec(guid), 0}, {"subSequences", rec(subs), 16}};
    named()[seq.name] = &seq;
    for (TType* t : {&pay, &spawn, &dsspawn}) hashed()[t->hash] = t;
}
// keen::SceneResource as R3b's tests need it: {flag u32 +0, noBuildZones BlobArray<Zone> +8, tail u32 +16}, 24 bytes;
// Zone {worldBoxMin WorldPosition +0, worldBoxMax WorldPosition +0x18, marker u8 +0x30 (no Kiln field)}, 0x38 bytes;
// WorldPosition {x, y, z sint64}, 32.32 fixed-point metres. test::Nested holds a list whose elements hold a list (not movable).
void scene_type() {
    using namespace kc::gd;
    static TType u8, u32, s64, pos, zone, zones, scene, inner, outer, outers, nested;
    auto rec = [](const TType& t) { return reinterpret_cast<uintptr_t>(&t); };
    u8.name = u8.brief = "uint8", u8.kind = K_U8, u8.size = 1;
    u32.name = u32.brief = "uint32", u32.kind = K_U32, u32.size = 4;
    s64.name = s64.brief = "sint64", s64.kind = K_S64, s64.size = 8;
    pos.name = "keen::WorldPosition", pos.brief = "WorldPosition", pos.kind = K_STRUCT, pos.size = 24;
    pos.fields = {{"x", rec(s64), 0}, {"y", rec(s64), 8}, {"z", rec(s64), 16}};
    zone.name = "keen::SceneNoBuildZone", zone.brief = "SceneNoBuildZone", zone.kind = K_STRUCT, zone.size = 0x38;
    zone.fields = {{"worldBoxMin", rec(pos), 0}, {"worldBoxMax", rec(pos), 0x18}, {"marker", rec(u8), 0x30}};
    zones.name = zones.brief = "keen::BlobArray<keen::SceneNoBuildZone>", zones.kind = K_BLOBARRAY, zones.size = 8, zones.inner_rec = rec(zone);
    scene.name = "keen::SceneResource", scene.brief = "SceneResource", scene.kind = K_STRUCT, scene.size = 24;
    scene.fields = {{"flag", rec(u32), 0}, {"noBuildZones", rec(zones), 8}, {"tail", rec(u32), 16}};
    named()[scene.name] = &scene;
    inner.name = inner.brief = "keen::BlobArray<uint32>", inner.kind = K_BLOBARRAY, inner.size = 8, inner.inner_rec = rec(u32);
    outer.name = "test::Outer", outer.brief = "Outer", outer.kind = K_STRUCT, outer.size = 8;
    outer.fields = {{"values", rec(inner), 0}};
    outers.name = outers.brief = "keen::BlobArray<test::Outer>", outers.kind = K_BLOBARRAY, outers.size = 8, outers.inner_rec = rec(outer);
    nested.name = "test::Nested", nested.brief = "Nested", nested.kind = K_STRUCT, nested.size = 8;
    nested.fields = {{"lists", rec(outers), 0}};
    named()[nested.name] = &nested;
}
// test::Timed {t keen::Time +0}, keen::Time {value sint64 nanoseconds}: a boot-routed time field (difficulty's durations)
void timed_type() {
    using namespace kc::gd;
    static TType s64, time, timed;
    auto rec = [](const TType& t) { return reinterpret_cast<uintptr_t>(&t); };
    s64.name = s64.brief = "sint64", s64.kind = K_S64, s64.size = 8;
    time.name = "keen::Time", time.brief = "Time", time.kind = K_STRUCT, time.size = 8;
    time.fields = {{"value", rec(s64), 0}};
    timed.name = "test::Timed", timed.brief = "Timed", timed.kind = K_STRUCT, timed.size = 8;
    timed.fields = {{"t", rec(time), 0}};
    named()[timed.name] = &timed;
}
// keen::BalancingTable as the gear rules see it (gear.h lower_rules; regmap/common/balance.json's rows): the five-rarity
// factors and chances as structs of floats, the durability curve and the enhancement unlock cost (two whole numbers). The
// front-end suite (gear_frontend_test) writes it both ways, as a resource patch and through the registry's boot back-end.
size_t gear_balance_type() {
    using namespace kc::gd;
    static TType u32, f32, rarities, bal;
    auto rec = [](const TType& t) { return reinterpret_cast<uintptr_t>(&t); };
    u32.name = u32.brief = "uint32", u32.kind = K_U32, u32.size = 4;
    f32.name = f32.brief = "float32", f32.kind = K_F32, f32.size = 4;
    rarities.name = "keen::ItemRarityFloats", rarities.brief = "ItemRarityFloats", rarities.kind = K_STRUCT, rarities.size = 20;
    rarities.fields = {{"common", rec(f32), 0}, {"uncommon", rec(f32), 4}, {"rare", rec(f32), 8}, {"epic", rec(f32), 12}, {"legendary", rec(f32), 16}};
    bal.name = "keen::BalancingTable", bal.brief = "BalancingTable", bal.kind = K_STRUCT, bal.size = 80;
    bal.fields = {{"rarityDurabilityMod", rec(rarities), 0},
                  {"gemSlotProbability", rec(rarities), 20},
                  {"durabilityStart", rec(f32), 40},
                  {"durabilityGrowth", rec(f32), 44},
                  {"perkUpgradeCostSeed", rec(u32), 48},
                  {"perkCostCreep", rec(f32), 52},
                  {"perkCostFactorWeaponsShield", rec(f32), 56},
                  {"perkCostFactorArmor", rec(f32), 60},
                  {"perkCostFactorRings", rec(f32), 64},
                  {"advancedPerkCostNominator", rec(f32), 68},
                  {"advancedPerkCostStartLevel", rec(u32), 72},
                  {"playerLevelCap", rec(u32), 76}};
    named()["keen::BalancingTable"] = &bal;
    return bal.size;
}
}  // namespace rbtest
}  // namespace kc

