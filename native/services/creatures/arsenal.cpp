// SPDX-License-Identifier: MIT
// kiln.creatures: the arsenal writer. An arsenal is a self-relative blob record {guid, attacks[], behaviors[]} reached
// through the registry's GUID map, looked up again for every creature in every update (ArsenalFind, nothing keeps the
// pointer across steps). Writing one means building such a record in Kiln's memory and pointing the map at it:
//  - a GUID the game knows: a composite of the game's arsenal plus each owner's additions, appended, so the game's
//    attack and rule indexes stay what they were; the game's blob is never touched;
//  - a new GUID: a new map entry, from nothing or starting from an existing arsenal ("from").
// All map writes happen in Pre, when no job runs. Removing an owner rebuilds; the last one restores the game's pointer
// or removes the entry. Old blocks are freed some steps later.
// Types and field names come from the game's reflection on the running build, as for private AI copies.
#include <algorithm>
#include <memory>

#include "internal.h"
#include "nlohmann/json.hpp"
#include "services/kiln_creatures.h"

using json = nlohmann::ordered_json;

namespace kcr {

ArsenalLayout AL;

namespace {

// The per-creature tables are sized for this many (attackExecutionCount, nextBehaviorCheckTime in the dynamic state).
constexpr size_t kMaxAttacks = 32, kMaxRules = 32;
constexpr uint64_t kFreeDelay = 250;

struct RawVar {
    uint32_t hash = 0;
    std::vector<uint8_t> bytes;
};
using VarList = std::vector<RawVar>;
struct RawCommand {
    std::vector<uint8_t> rec;
    VarList constraints, actions;
};
struct RawAttack {
    std::vector<uint8_t> rec;  // the record, its lists zeroed
    VarList selection, execution, abort;
    std::vector<RawCommand> commands;
    bool has_data = false;
    RawVar data;
};
struct RawRule {
    std::vector<uint8_t> rec;
    VarList when, abort, actions;
};
struct RawArsenal {
    uint8_t guid[16] = {};
    std::vector<RawAttack> attacks;
    std::vector<RawRule> rules;
};

// One owner's additions, encoded when they arrive (reflection only, no world needed).
struct PatchAttack {
    std::string modify;     // GUID text of an attack to change; empty: a new attack
    int index = -1;         // or its index
    RawAttack attack;       // new: complete; modify: fields below on top of the base's record
    std::vector<std::pair<uint32_t, std::vector<uint8_t>>> fields;
    bool set_selection = false, set_execution = false, set_abort = false, set_commands = false, set_data = false;
};
struct PatchRule {
    std::string modify;
    int index = -1;
    RawRule rule;
    std::vector<std::pair<uint32_t, std::vector<uint8_t>>> fields;
    bool set_when = false, set_abort = false, set_actions = false;
};
struct Layer {
    KilnOwner owner = 0;
    std::vector<PatchAttack> attacks;
    std::vector<PatchRule> rules;
    RuleUses uses;
    std::string problem;
};
struct Composite {
    uint8_t guid[16] = {};
    uint8_t from[16] = {};
    bool has_from = false;
    std::vector<Layer> layers;
    bool installed = false, had_game_entry = false;
    uintptr_t game_ptr = 0;  // the map's value before Kiln (the game's record)
    std::unique_ptr<uint8_t[]> block;
    size_t block_size = 0;
    size_t attack_count = 0, rule_count = 0;
    bool dirty = true;
    std::string problem;
};

std::vector<std::unique_ptr<Composite>> g_all;
struct Retired {
    std::unique_ptr<uint8_t[]> p;
    uint64_t at;
};
std::vector<Retired> g_retired;
RuleUses g_uses;
bool g_layout_tried = false, g_layout_ok = false;
uintptr_t g_reg = 0;
int64_t g_m_count = -1, g_m_cap = -1, g_m_bits = -1, g_m_keys = -1, g_m_values = -1;
bool g_map_ok = false;
std::string g_layout_error;

// ---- the registry's map ----

bool seh_u32(uintptr_t a, uint32_t* v) { return seh_read(reinterpret_cast<const void*>(a), v, 4); }
bool seh_u64(uintptr_t a, uint64_t* v) { return seh_read(reinterpret_cast<const void*>(a), v, 8); }

struct MapView {
    uint64_t cap = 0;
    uint32_t count = 0;
    uint64_t* bits = nullptr;
    uint8_t* keys = nullptr;
    uint64_t* values = nullptr;
};
bool map_view(MapView* m) {
    if (!g_reg || !g_map_ok) return false;
    uint64_t b = 0, k = 0, v = 0;
    if (!seh_u64(g_reg + g_m_cap, &m->cap) || !seh_u32(g_reg + g_m_count, &m->count) || !seh_u64(g_reg + g_m_bits, &b) ||
        !seh_u64(g_reg + g_m_keys, &k) || !seh_u64(g_reg + g_m_values, &v))
        return false;
    m->bits = reinterpret_cast<uint64_t*>(b);
    m->keys = reinterpret_cast<uint8_t*>(k);
    m->values = reinterpret_cast<uint64_t*>(v);
    return m->cap && (m->cap & (m->cap - 1)) == 0 && m->cap <= (1ull << 20) && m->count <= m->cap && b && k && v;
}
inline bool bit_of(const MapView& m, uint64_t i) { return (m.bits[i >> 6] >> (i & 63)) & 1; }
inline uint32_t key_hash(const uint8_t* g) {
    uint32_t h = 0;
    std::memcpy(&h, g, 4);
    return h;
}
int64_t map_find(const MapView& m, const uint8_t guid[16]) {
    uint64_t i = key_hash(guid) & (m.cap - 1);
    for (uint64_t n = 0; n < m.cap; ++n) {
        if (!bit_of(m, i)) return -1;
        if (std::memcmp(m.keys + i * 16, guid, 16) == 0) return static_cast<int64_t>(i);
        i = i + 1 < m.cap ? i + 1 : 0;
    }
    return -1;
}

// The map's arrays are the game's allocation: when an insert would fill more than half of it, Kiln moves the map to its
// own arrays, twice the size. Only in Pre.
bool map_grow(MapView* m) {
    const uint64_t cap = m->cap * 2;
    auto* bits = static_cast<uint64_t*>(std::calloc((cap + 63) / 64, 8));
    auto* keys = static_cast<uint8_t*>(std::calloc(cap, 16));
    auto* values = static_cast<uint64_t*>(std::calloc(cap, 8));
    if (!bits || !keys || !values) return false;
    for (uint64_t i = 0; i < m->cap; ++i) {
        if (!bit_of(*m, i)) continue;
        uint64_t j = key_hash(m->keys + i * 16) & (cap - 1);
        while ((bits[j >> 6] >> (j & 63)) & 1) j = j + 1 < cap ? j + 1 : 0;
        bits[j >> 6] |= 1ull << (j & 63);
        std::memcpy(keys + j * 16, m->keys + i * 16, 16);
        values[j] = m->values[i];
    }
    auto w = [&](int64_t off, uint64_t v) { return seh_write(reinterpret_cast<void*>(g_reg + off), &v, 8); };
    if (!w(g_m_bits, reinterpret_cast<uint64_t>(bits)) || !w(g_m_keys, reinterpret_cast<uint64_t>(keys)) ||
        !w(g_m_values, reinterpret_cast<uint64_t>(values)) || !w(g_m_cap, cap))
        return false;
    m->cap = cap;
    m->bits = bits;
    m->keys = keys;
    m->values = values;
    KC_I("arsenals: the registry's map moved to Kiln's arrays, capacity %llu", static_cast<unsigned long long>(cap));
    return true;
}

bool map_put(const uint8_t guid[16], uintptr_t value) {
    MapView m;
    if (!map_view(&m)) return false;
    int64_t at = map_find(m, guid);
    if (at >= 0) return seh_write(&m.values[at], &value, 8);
    if (static_cast<uint64_t>(m.count) + 1 > m.cap / 2 && !map_grow(&m)) return false;
    uint64_t i = key_hash(guid) & (m.cap - 1);
    while (bit_of(m, i)) i = i + 1 < m.cap ? i + 1 : 0;
    if (!seh_write(&m.values[i], &value, 8) || !seh_write(m.keys + i * 16, guid, 16)) return false;
    uint64_t word = m.bits[i >> 6] | (1ull << (i & 63));
    if (!seh_write(&m.bits[i >> 6], &word, 8)) return false;
    const uint32_t count = m.count + 1;
    return seh_write(reinterpret_cast<void*>(g_reg + g_m_count), &count, 4);
}

// Linear probing: a slot can only go empty if the entries after it that would no longer be found move up.
bool map_erase(const uint8_t guid[16]) {
    MapView m;
    if (!map_view(&m)) return false;
    int64_t at = map_find(m, guid);
    if (at < 0) return true;
    uint64_t i = static_cast<uint64_t>(at), j = i;
    const uint64_t mask = m.cap - 1;
    for (;;) {
        j = (j + 1) & mask;
        if (!bit_of(m, j)) break;
        const uint64_t home = key_hash(m.keys + j * 16) & mask;
        if (((j - home) & mask) >= ((j - i) & mask)) {
            std::memcpy(m.keys + i * 16, m.keys + j * 16, 16);
            m.values[i] = m.values[j];
            i = j;
        }
    }
    m.bits[i >> 6] &= ~(1ull << (i & 63));
    std::memset(m.keys + i * 16, 0, 16);
    m.values[i] = 0;
    const uint32_t count = m.count - 1;
    return seh_write(reinterpret_cast<void*>(g_reg + g_m_count), &count, 4);
}

uintptr_t map_get(const uint8_t guid[16]) {
    MapView m;
    if (!map_view(&m)) return 0;
    const int64_t at = map_find(m, guid);
    return at < 0 ? 0 : m.values[at];
}

// ---- reading a record ----

bool read_list(const uint8_t* field, VarList* out) {
    int32_t rel = 0;
    uint32_t n = 0;
    if (!seh_read(field, &rel, 4) || !seh_read(field + 4, &n, 4)) return false;
    if (!n) return true;
    if (!rel || n > 4096) return false;
    const uint8_t* el = field + rel;
    out->resize(n);
    for (uint32_t i = 0; i < n; ++i) {
        const uint8_t* v = el + static_cast<size_t>(i) * 12;
        int32_t vr = 0;
        uint32_t size = 0;
        if (!seh_read(v, &(*out)[i].hash, 4) || !seh_read(v + 4, &vr, 4) || !seh_read(v + 8, &size, 4)) return false;
        if (!vr || size > 4096) return false;
        (*out)[i].bytes.resize(size);
        if (size && !seh_read(v + 4 + vr, (*out)[i].bytes.data(), size)) return false;
    }
    return true;
}

bool read_attack(const uint8_t* rec, RawAttack* a) {
    a->rec.resize(AL.attack_size);
    if (!seh_read(rec, a->rec.data(), AL.attack_size)) return false;
    if (!read_list(rec + AL.attack_selection, &a->selection) || !read_list(rec + AL.attack_execution, &a->execution) ||
        !read_list(rec + AL.attack_abort, &a->abort))
        return false;
    int32_t rel = 0;
    uint32_t n = 0;
    if (!seh_read(rec + AL.attack_commands, &rel, 4) || !seh_read(rec + AL.attack_commands + 4, &n, 4)) return false;
    if (n) {
        if (!rel || n > 256) return false;
        const uint8_t* el = rec + AL.attack_commands + rel;
        a->commands.resize(n);
        for (uint32_t i = 0; i < n; ++i) {
            const uint8_t* c = el + static_cast<size_t>(i) * AL.command_size;
            a->commands[i].rec.resize(AL.command_size);
            if (!seh_read(c, a->commands[i].rec.data(), AL.command_size) ||
                !read_list(c + AL.command_constraints, &a->commands[i].constraints) ||
                !read_list(c + AL.command_actions, &a->commands[i].actions))
                return false;
        }
    }
    int32_t dr = 0;
    uint32_t dh = 0, ds = 0;
    const uint8_t* d = rec + AL.attack_data;
    if (!seh_read(d, &dh, 4) || !seh_read(d + 4, &dr, 4) || !seh_read(d + 8, &ds, 4)) return false;
    if (dh && dr && ds <= 4096) {
        a->has_data = true;
        a->data.hash = dh;
        a->data.bytes.resize(ds);
        if (ds && !seh_read(d + 4 + dr, a->data.bytes.data(), ds)) return false;
    }
    return true;
}

bool read_rule(const uint8_t* rec, RawRule* r) {
    r->rec.resize(AL.rule_size);
    return seh_read(rec, r->rec.data(), AL.rule_size) && read_list(rec + AL.rule_when, &r->when) &&
           read_list(rec + AL.rule_abort, &r->abort) && read_list(rec + AL.rule_actions, &r->actions);
}

bool read_arsenal(const uint8_t* rec, RawArsenal* a) {
    if (!seh_read(rec, a->guid, 16)) return false;
    int32_t rel = 0;
    uint32_t n = 0;
    if (!seh_read(rec + AL.attacks, &rel, 4) || !seh_read(rec + AL.attacks + 4, &n, 4) || n > 256) return false;
    a->attacks.resize(n);
    for (uint32_t i = 0; i < n; ++i)
        if (!read_attack(rec + AL.attacks + rel + static_cast<size_t>(i) * AL.attack_size, &a->attacks[i])) return false;
    if (!seh_read(rec + AL.rules, &rel, 4) || !seh_read(rec + AL.rules + 4, &n, 4) || n > 256) return false;
    a->rules.resize(n);
    for (uint32_t i = 0; i < n; ++i)
        if (!read_rule(rec + AL.rules + rel + static_cast<size_t>(i) * AL.rule_size, &a->rules[i])) return false;
    return true;
}

// ---- writing a record ----

struct Builder {
    std::vector<uint8_t> b;
    size_t take(size_t n, size_t align = 4) {
        size_t at = (b.size() + align - 1) & ~(align - 1);
        b.resize(at + n);
        return at;
    }
    void rel(size_t field, size_t target) {
        const int32_t r = static_cast<int32_t>(static_cast<int64_t>(target) - static_cast<int64_t>(field));
        std::memcpy(&b[field], &r, 4);
    }
    void u32(size_t at, uint32_t v) { std::memcpy(&b[at], &v, 4); }
};

void put_list(Builder& w, size_t field, const VarList& l) {
    w.rel(field, field);  // placeholder until the array exists
    std::memset(&w.b[field], 0, 8);
    if (l.empty()) return;
    const size_t arr = w.take(l.size() * 12);
    w.rel(field, arr);
    w.u32(field + 4, static_cast<uint32_t>(l.size()));
    for (size_t i = 0; i < l.size(); ++i) {
        const size_t el = arr + i * 12;
        w.u32(el, l[i].hash);
        w.u32(el + 8, static_cast<uint32_t>(l[i].bytes.size()));
        const size_t p = w.take(std::max<size_t>(l[i].bytes.size(), 1));
        if (!l[i].bytes.empty()) std::memcpy(&w.b[p], l[i].bytes.data(), l[i].bytes.size());
        w.rel(el + 4, p);
    }
}

void put_attack(Builder& w, size_t at, const RawAttack& a) {
    std::memcpy(&w.b[at], a.rec.data(), AL.attack_size);
    put_list(w, at + AL.attack_selection, a.selection);
    put_list(w, at + AL.attack_execution, a.execution);
    put_list(w, at + AL.attack_abort, a.abort);
    std::memset(&w.b[at + AL.attack_commands], 0, 8);
    if (!a.commands.empty()) {
        const size_t arr = w.take(a.commands.size() * AL.command_size);
        w.rel(at + AL.attack_commands, arr);
        w.u32(at + AL.attack_commands + 4, static_cast<uint32_t>(a.commands.size()));
        for (size_t i = 0; i < a.commands.size(); ++i) {
            const size_t c = arr + i * AL.command_size;
            std::memcpy(&w.b[c], a.commands[i].rec.data(), AL.command_size);
            put_list(w, c + AL.command_constraints, a.commands[i].constraints);
            put_list(w, c + AL.command_actions, a.commands[i].actions);
        }
    }
    std::memset(&w.b[at + AL.attack_data], 0, 12);
    if (a.has_data) {
        w.u32(at + AL.attack_data, a.data.hash);
        w.u32(at + AL.attack_data + 8, static_cast<uint32_t>(a.data.bytes.size()));
        const size_t p = w.take(std::max<size_t>(a.data.bytes.size(), 1));
        if (!a.data.bytes.empty()) std::memcpy(&w.b[p], a.data.bytes.data(), a.data.bytes.size());
        w.rel(at + AL.attack_data + 4, p);
    }
}

void put_rule(Builder& w, size_t at, const RawRule& r) {
    std::memcpy(&w.b[at], r.rec.data(), AL.rule_size);
    put_list(w, at + AL.rule_when, r.when);
    put_list(w, at + AL.rule_abort, r.abort);
    put_list(w, at + AL.rule_actions, r.actions);
}

std::vector<uint8_t> write_arsenal(const RawArsenal& a) {
    Builder w;
    const size_t rec = w.take(AL.record_size, 16);
    std::memcpy(&w.b[rec], a.guid, 16);
    std::memset(&w.b[rec + AL.attacks], 0, 8);
    std::memset(&w.b[rec + AL.rules], 0, 8);
    size_t attacks = 0, rules = 0;
    if (!a.attacks.empty()) {
        attacks = w.take(a.attacks.size() * AL.attack_size);
        w.rel(rec + AL.attacks, attacks);
        w.u32(rec + AL.attacks + 4, static_cast<uint32_t>(a.attacks.size()));
    }
    if (!a.rules.empty()) {
        rules = w.take(a.rules.size() * AL.rule_size);
        w.rel(rec + AL.rules, rules);
        w.u32(rec + AL.rules + 4, static_cast<uint32_t>(a.rules.size()));
    }
    for (size_t i = 0; i < a.attacks.size(); ++i) put_attack(w, attacks + i * AL.attack_size, a.attacks[i]);
    for (size_t i = 0; i < a.rules.size(); ++i) put_rule(w, rules + i * AL.rule_size, a.rules[i]);
    return std::move(w.b);
}

bool same_list(const VarList& x, const VarList& y) {
    if (x.size() != y.size()) return false;
    for (size_t i = 0; i < x.size(); ++i)
        if (x[i].hash != y[i].hash || x[i].bytes != y[i].bytes) return false;
    return true;
}

// Compares parsed forms; a record's own list fields hold offsets, which legitimately differ after a move.
bool same_record(const std::vector<uint8_t>& x, const std::vector<uint8_t>& y, std::initializer_list<std::pair<uint32_t, uint32_t>> skip) {
    if (x.size() != y.size()) return false;
    for (size_t i = 0; i < x.size(); ++i) {
        bool skipped = false;
        for (const auto& [at, n] : skip) skipped |= i >= at && i < at + n;
        if (!skipped && x[i] != y[i]) return false;
    }
    return true;
}

bool same_arsenal(const RawArsenal& x, const RawArsenal& y) {
    if (x.attacks.size() != y.attacks.size() || x.rules.size() != y.rules.size()) return false;
    for (size_t i = 0; i < x.attacks.size(); ++i) {
        const RawAttack& a = x.attacks[i];
        const RawAttack& b = y.attacks[i];
        if (!same_record(a.rec, b.rec, {{AL.attack_selection, 8}, {AL.attack_execution, 8}, {AL.attack_abort, 8}, {AL.attack_commands, 8}, {AL.attack_data + 4, 4}}))
            return false;
        if (!same_list(a.selection, b.selection) || !same_list(a.execution, b.execution) || !same_list(a.abort, b.abort)) return false;
        if (a.has_data != b.has_data || (a.has_data && (a.data.hash != b.data.hash || a.data.bytes != b.data.bytes))) return false;
        if (a.commands.size() != b.commands.size()) return false;
        for (size_t k = 0; k < a.commands.size(); ++k) {
            if (!same_record(a.commands[k].rec, b.commands[k].rec, {{AL.command_constraints, 8}, {AL.command_actions, 8}})) return false;
            if (!same_list(a.commands[k].constraints, b.commands[k].constraints) || !same_list(a.commands[k].actions, b.commands[k].actions)) return false;
        }
    }
    for (size_t i = 0; i < x.rules.size(); ++i) {
        const RawRule& a = x.rules[i];
        const RawRule& b = y.rules[i];
        if (!same_record(a.rec, b.rec, {{AL.rule_when, 8}, {AL.rule_abort, 8}, {AL.rule_actions, 8}})) return false;
        if (!same_list(a.when, b.when) || !same_list(a.abort, b.abort) || !same_list(a.actions, b.actions)) return false;
    }
    return true;
}

// ---- JSON to bytes ----

std::string leaf_text(const json& v) {
    if (v.is_string()) return v.get<std::string>();
    if (v.is_boolean()) return v.get<bool>() ? "true" : "false";
    if (v.is_number()) return v.dump();
    if (v.is_array()) {
        std::string o;
        for (const auto& x : v) {
            if (!o.empty()) o += "|";
            o += leaf_text(x);
        }
        return o.empty() ? "0" : o;
    }
    return {};
}

bool time_object(const json& v, std::string* text) {
    if (!v.is_object() || !v.contains("value") || !v["value"].is_number() || v.size() != 1) return false;
    char b[64];
    std::snprintf(b, sizeof b, "%.9f", v["value"].get<double>() / 1e9);
    *text = b;
    return true;
}

// Sets `value` at `path` in the bytes of `t`: a scalar, or an object of fields inside a struct.
bool set_value(const TypeInfo& t, const std::string& path, const json& value, std::vector<uint8_t>* bytes,
               std::vector<std::pair<uint32_t, std::vector<uint8_t>>>* record, std::string* err) {
    FieldRef f;
    std::string why;
    if (resolve_path(t, path, &f, &why)) {
        std::string text;
        if (!time_object(value, &text)) {
            if (value.is_object()) return *err = path + ": expected a value", false;
            text = leaf_text(value);
        }
        if (f.offset + f.bytes > bytes->size()) return *err = path + ": outside the record", false;
        if (!parse_value(f, text, bytes->data() + f.offset, &why)) return *err = path + ": " + why, false;
        if (record) record->emplace_back(f.offset, std::vector<uint8_t>(bytes->begin() + f.offset, bytes->begin() + f.offset + f.bytes));
        return true;
    }
    if (value.is_object()) {
        for (const auto& [k, x] : value.items())
            if (!set_value(t, path + "." + k, x, bytes, record, err)) return false;
        return true;
    }
    return *err = why, false;
}

std::string game_type_name(std::string_view name) {
    return name.find("::") != std::string_view::npos ? std::string(name) : "keen::enemy01::" + std::string(name);
}

bool guid_of(const json& v, uint8_t out[16]) {
    return v.is_string() && parse_guid(v.get<std::string>(), out);
}

bool encode_constraint(const json& j, RawVar* out, RuleUses* uses, std::string* err);
bool encode_action(const json& j, RawVar* out, RuleUses* uses, std::string* err);

bool encode_game_variant(const json& j, RawVar* out, std::string* err) {
    std::string type;
    if (j.contains("$type") && j["$type"].is_string()) type = j["$type"].get<std::string>();
    else if (j.contains("type") && j["type"].is_string()) type = j["type"].get<std::string>();
    else return *err = "an entry needs \"type\" or \"kiln\": " + j.dump(), false;
    const std::string q = game_type_name(type);
    TypeInfo t;
    if (!type_info(q, &t, err)) return false;
    out->hash = fnv1a(q);
    out->bytes.assign(t.size, 0);
    const json* fields = j.contains("value") && j["value"].is_object() ? &j["value"] : &j;
    for (const auto& [k, v] : fields->items()) {
        if (k == "$type" || k == "type") continue;
        std::string e;
        if (!set_value(t, k, v, &out->bytes, nullptr, &e)) return *err = q + ": " + e, false;
    }
    return true;
}

bool phase_id(const json& j, uint32_t* out, std::string* err) {
    if (j.contains("id") && j["id"].is_number_unsigned()) return *out = j["id"].get<uint32_t>(), true;
    uint8_t g[16];
    if (j.contains("phase") && guid_of(j["phase"], g)) return *out = fnv1a(std::string_view(reinterpret_cast<const char*>(g), 16)), true;
    return *err = "a phase entry needs \"phase\" (a GUID) or \"id\": " + j.dump(), false;
}

bool encode_constraint(const json& j, RawVar* out, RuleUses* uses, std::string* err) {
    if (!j.is_object()) return *err = "a constraint must be an object: " + j.dump(), false;
    if (!j.contains("kiln")) return encode_game_variant(j, out, err);
    RuleCond c{};
    c.negated = j.value("negated", false) ? 1 : 0;
    const std::string kind = j["kiln"].is_string() ? j["kiln"].get<std::string>() : "";
    uses->any_kiln = true;
    if (kind == "condition") {
        const int slot = j.value("slot", -1);
        if (slot < 0 || slot > 255) return *err = "a condition needs \"slot\" 0..255: " + j.dump(), false;
        c.kind = KILN_RC_CONDITION;
        c.slot = static_cast<uint16_t>(slot);
    } else if (kind == "buff") {
        c.kind = KILN_RC_BUFF;
        uses->buffs = true;
        uint8_t g[16];
        if (j.contains("id") && j["id"].is_number_unsigned()) c.id = j["id"].get<uint32_t>();
        else if (j.contains("buff") && guid_of(j["buff"], g)) c.id = fnv1a(std::string_view(reinterpret_cast<const char*>(g), 16));
        else return *err = "a buff entry needs \"buff\" (a GUID) or \"id\": " + j.dump(), false;
    } else if (kind == "time") {
        c.kind = KILN_RC_TIME;
        uses->time = true;
        if (!j.contains("from") || !j.contains("to") || !j["from"].is_number() || !j["to"].is_number())
            return *err = "a time entry needs \"from\" and \"to\" in hours: " + j.dump(), false;
        c.a = j["from"].get<float>();
        c.b = j["to"].get<float>();
    } else if (kind == "phase") {
        c.kind = KILN_RC_PHASE;
        uses->phase = true;
        if (!phase_id(j, &c.id, err)) return false;
    } else if (kind == "job") {
        c.kind = KILN_RC_JOB;
        const int id = j.value("id", -1);
        if (id < 1 || id > 63) return *err = "a job entry needs \"id\" 1..63: " + j.dump(), false;
        c.id = static_cast<uint32_t>(id);
        c.slot = static_cast<uint16_t>(j.value("arg", 0));
    } else {
        return *err = "unknown constraint kind '" + kind + "'", false;
    }
    out->hash = kRuleConstraintHash;
    out->bytes.resize(sizeof c);
    std::memcpy(out->bytes.data(), &c, sizeof c);
    return true;
}

bool encode_action(const json& j, RawVar* out, RuleUses* uses, std::string* err) {
    if (!j.is_object()) return *err = "an action must be an object: " + j.dump(), false;
    if (!j.contains("kiln")) return encode_game_variant(j, out, err);
    RuleAct a{};
    const std::string kind = j["kiln"].is_string() ? j["kiln"].get<std::string>() : "";
    uses->any_kiln = true;
    if (kind == "queue") {
        a.kind = KILN_RA_QUEUE;
        if (!j.contains("id") || !j["id"].is_number_unsigned()) return *err = "a queue entry needs \"id\": " + j.dump(), false;
        a.id = j["id"].get<uint32_t>();
    } else if (kind == "phase") {
        a.kind = KILN_RA_PHASE;
        uses->phase = true;
        if (!phase_id(j, &a.id, err)) return false;
    } else if (kind == "attack") {
        a.kind = KILN_RA_ATTACK;
        uses->attack_action = true;
        if (!j.contains("attack") || !guid_of(j["attack"], a.guid)) return *err = "an attack entry needs \"attack\" (a GUID): " + j.dump(), false;
    } else if (kind == "job") {
        a.kind = KILN_RA_JOB;
        const int id = j.value("id", -1);
        if (id < 1 || id > 63) return *err = "a job entry needs \"id\" 1..63: " + j.dump(), false;
        a.id = static_cast<uint32_t>(id);
        a.slot = static_cast<uint16_t>(j.value("arg", 0));
    } else {
        return *err = "unknown action kind '" + kind + "'", false;
    }
    out->hash = kRuleActionHash;
    out->bytes.resize(sizeof a);
    std::memcpy(out->bytes.data(), &a, sizeof a);
    return true;
}

bool encode_constraints(const json& j, VarList* out, RuleUses* uses, std::string* err) {
    if (!j.is_array()) return *err = "expected a list of constraints: " + j.dump(), false;
    out->resize(j.size());
    for (size_t i = 0; i < j.size(); ++i)
        if (!encode_constraint(j[i], &(*out)[i], uses, err)) return false;
    return true;
}

bool encode_actions(const json& j, VarList* out, RuleUses* uses, std::string* err) {
    if (!j.is_array()) return *err = "expected a list of actions: " + j.dump(), false;
    out->resize(j.size());
    for (size_t i = 0; i < j.size(); ++i)
        if (!encode_action(j[i], &(*out)[i], uses, err)) return false;
    return true;
}

bool encode_attack(const json& j, PatchAttack* p, RuleUses* uses, std::string* err) {
    TypeInfo t;
    if (!type_info("keen::enemy01::AttackDesscriptionResource", &t, err)) return false;
    const bool modifying = j.contains("modify") || j.contains("index");
    if (j.contains("modify")) {
        if (!j["modify"].is_string()) return *err = "\"modify\" must be an attack's GUID", false;
        p->modify = j["modify"].get<std::string>();
    } else if (j.contains("index")) {
        if (!j["index"].is_number_integer()) return *err = "\"index\" must be an attack's index", false;
        p->index = j["index"].get<int>();
    }
    RawAttack& a = p->attack;
    a.rec.assign(AL.attack_size, 0);
    auto* rec = modifying ? &p->fields : nullptr;
    auto field = [&](const std::string& path, const json& v) { return set_value(t, path, v, &a.rec, rec, err); };
    if (!modifying) {
        // what a plain attack needs to be chosen at all
        if (!field("description.attackStyle", json::array({"Normal"})) || !field("description.selectionChance", 1.0)) return false;
    }
    for (const auto& [k, v] : j.items()) {
        if (k == "modify" || k == "index" || k == "name") continue;
        if (k == "guid") {
            if (!field("guid", v)) return false;
        } else if (k == "sequence") {
            if (!field("description.actionSequence", v)) return false;
        } else if (k == "description") {
            if (!field("description", v)) return false;
        } else if (k == "selection" || k == "selectionConstrain") {
            if (!encode_constraints(v, &a.selection, uses, err)) return false;
            p->set_selection = true;
        } else if (k == "execution" || k == "executionConstrain") {
            if (!encode_constraints(v, &a.execution, uses, err)) return false;
            p->set_execution = true;
        } else if (k == "abort" || k == "abortConstraints") {
            if (!encode_constraints(v, &a.abort, uses, err)) return false;
            p->set_abort = true;
        } else if (k == "commands") {
            if (!v.is_array()) return *err = "\"commands\" must be a list", false;
            TypeInfo ct;
            if (!type_info("keen::enemy01::AttackCommand", &ct, err)) return false;
            a.commands.resize(v.size());
            for (size_t i = 0; i < v.size(); ++i) {
                RawCommand& c = a.commands[i];
                c.rec.assign(AL.command_size, 0);
                for (const auto& [ck, cv] : v[i].items()) {
                    if (ck == "constraints") {
                        if (!encode_constraints(cv, &c.constraints, uses, err)) return false;
                    } else if (ck == "actions") {
                        if (!encode_actions(cv, &c.actions, uses, err)) return false;
                    } else if (!set_value(ct, ck, cv, &c.rec, nullptr, err)) {
                        return false;
                    }
                }
            }
            p->set_commands = true;
        } else if (k == "data") {
            if (v.is_null()) {
                a.has_data = false;
            } else {
                if (!encode_game_variant(v, &a.data, err)) return false;
                a.has_data = true;
            }
            p->set_data = true;
        } else {
            if (!field("description." + k, v)) return false;
        }
    }
    uint8_t zero[16] = {};
    if (!modifying) {
        if (std::memcmp(a.rec.data(), zero, 16) == 0) return *err = "an attack needs a \"guid\": " + j.dump(), false;
        if (std::memcmp(a.rec.data() + AL.attack_sequence, zero, 16) == 0) return *err = "an attack needs a \"sequence\": " + j.dump(), false;
    }
    return true;
}

bool time_pair(const json& v, json* lo, json* hi, std::string* err) {
    if (v.is_number()) {
        *lo = v;
        *hi = v;
        return true;
    }
    if (v.is_array() && v.size() == 2 && v[0].is_number() && v[1].is_number()) {
        *lo = v[0];
        *hi = v[1];
        return true;
    }
    return *err = "expected seconds or [min, max]: " + v.dump(), false;
}

bool encode_rule(const json& j, PatchRule* p, RuleUses* uses, std::string* err) {
    TypeInfo t;
    if (!type_info("keen::enemy01::BehaviorDesscriptionResource", &t, err)) return false;
    const bool modifying = j.contains("modify") || j.contains("index");
    if (j.contains("modify")) {
        if (!j["modify"].is_string()) return *err = "\"modify\" must be a rule's GUID", false;
        p->modify = j["modify"].get<std::string>();
    } else if (j.contains("index")) {
        if (!j["index"].is_number_integer()) return *err = "\"index\" must be a rule's index", false;
        p->index = j["index"].get<int>();
    }
    RawRule& r = p->rule;
    r.rec.assign(AL.rule_size, 0);
    auto* rec = modifying ? &p->fields : nullptr;
    auto field = [&](const std::string& path, const json& v) { return set_value(t, path, v, &r.rec, rec, err); };
    if (!modifying) {
        if (!field("settings.chance", 1.0)) return false;
    }
    bool chance_added = false;
    for (const auto& [k, v] : j.items()) {
        if (k == "modify" || k == "index" || k == "name") continue;
        if (k == "guid" || k == "mode" || k == "settings") {
            if (!field(k, v)) return false;
        } else if (k == "every") {
            json lo, hi;
            if (!time_pair(v, &lo, &hi, err) || !field("settings.checkIntervalMin", lo) || !field("settings.checkIntervalMax", hi)) return false;
        } else if (k == "cooldown") {
            json lo, hi;
            if (!time_pair(v, &lo, &hi, err) || !field("settings.cooldownMin", lo) || !field("settings.cooldownMax", hi)) return false;
        } else if (k == "chance") {
            chance_added = v.is_number() && v.get<double>() < 1.0;
        } else if (k == "when" || k == "executionConstrain") {
            if (!encode_constraints(v, &r.when, uses, err)) return false;
            p->set_when = true;
        } else if (k == "abort" || k == "abortConstraints") {
            if (!encode_constraints(v, &r.abort, uses, err)) return false;
            p->set_abort = true;
        } else if (k == "do" || k == "actions") {
            if (!encode_actions(v, &r.actions, uses, err)) return false;
            p->set_actions = true;
        } else {
            return *err = "unknown key '" + k + "' in a rule", false;
        }
    }
    if (chance_added) {
        // the rule walker does not read settings.chance: the game's data rolls with this constraint
        RawVar v;
        const json c = {{"type", "RandomChanceConstrain"}, {"chance", j["chance"]}};
        if (!encode_game_variant(c, &v, err)) return false;
        r.when.push_back(std::move(v));
        p->set_when = true;
    }
    if (!modifying) {
        uint8_t zero[16] = {};
        uint32_t off = 0, size = 0;
        if (!field_layout(t, "guid", &off, &size) || std::memcmp(r.rec.data() + off, zero, 16) == 0)
            return *err = "a rule needs a \"guid\": " + j.dump(), false;
    }
    return true;
}

// ---- building ----

void apply_fields(std::vector<uint8_t>* rec, const std::vector<std::pair<uint32_t, std::vector<uint8_t>>>& fields) {
    for (const auto& [off, bytes] : fields)
        if (off + bytes.size() <= rec->size()) std::memcpy(rec->data() + off, bytes.data(), bytes.size());
}

bool apply_layer(const Layer& l, RawArsenal* a, std::string* err) {
    for (const PatchAttack& p : l.attacks) {
        if (p.modify.empty() && p.index < 0) {
            if (a->attacks.size() >= kMaxAttacks) return *err = "more than 32 attacks", false;
            for (const RawAttack& x : a->attacks)
                if (std::memcmp(x.rec.data(), p.attack.rec.data(), 16) == 0) return *err = "two attacks with one GUID", false;
            a->attacks.push_back(p.attack);
            continue;
        }
        RawAttack* t = nullptr;
        if (p.index >= 0) {
            if (static_cast<size_t>(p.index) >= a->attacks.size()) return *err = "no attack with index " + std::to_string(p.index), false;
            t = &a->attacks[p.index];
        } else {
            uint8_t g[16];
            if (!parse_guid(p.modify, g)) return *err = "bad attack GUID " + p.modify, false;
            for (RawAttack& x : a->attacks)
                if (std::memcmp(x.rec.data(), g, 16) == 0) t = &x;
            if (!t) return *err = "no attack with GUID " + p.modify, false;
        }
        apply_fields(&t->rec, p.fields);
        if (p.set_selection) t->selection = p.attack.selection;
        if (p.set_execution) t->execution = p.attack.execution;
        if (p.set_abort) t->abort = p.attack.abort;
        if (p.set_commands) t->commands = p.attack.commands;
        if (p.set_data) {
            t->has_data = p.attack.has_data;
            t->data = p.attack.data;
        }
    }
    uint32_t guid_off = 0, guid_size = 0;
    TypeInfo rt;
    std::string e;
    if (!type_info("keen::enemy01::BehaviorDesscriptionResource", &rt, &e) || !field_layout(rt, "guid", &guid_off, &guid_size)) return *err = e, false;
    for (const PatchRule& p : l.rules) {
        if (p.modify.empty() && p.index < 0) {
            if (a->rules.size() >= kMaxRules) return *err = "more than 32 rules", false;
            for (const RawRule& x : a->rules)
                if (std::memcmp(x.rec.data() + guid_off, p.rule.rec.data() + guid_off, 16) == 0) return *err = "two rules with one GUID", false;
            a->rules.push_back(p.rule);
            continue;
        }
        RawRule* t = nullptr;
        if (p.index >= 0) {
            if (static_cast<size_t>(p.index) >= a->rules.size()) return *err = "no rule with index " + std::to_string(p.index), false;
            t = &a->rules[p.index];
        } else {
            uint8_t g[16];
            if (!parse_guid(p.modify, g)) return *err = "bad rule GUID " + p.modify, false;
            for (RawRule& x : a->rules)
                if (std::memcmp(x.rec.data() + guid_off, g, 16) == 0) t = &x;
            if (!t) return *err = "no rule with GUID " + p.modify, false;
        }
        apply_fields(&t->rec, p.fields);
        if (p.set_when) t->when = p.rule.when;
        if (p.set_abort) t->abort = p.rule.abort;
        if (p.set_actions) t->actions = p.rule.actions;
    }
    return true;
}

void recompute_uses() {
    RuleUses u;
    for (const auto& c : g_all)
        for (const Layer& l : c->layers) {
            u.any_kiln |= l.uses.any_kiln;
            u.buffs |= l.uses.buffs;
            u.time |= l.uses.time;
            u.phase |= l.uses.phase;
            u.attack_action |= l.uses.attack_action;
        }
    g_uses = u;
}

void retire(std::unique_ptr<uint8_t[]> p) {
    if (p) g_retired.push_back(Retired{std::move(p), g_step + kFreeDelay});
}

// True when the composite is done, false to try again later (a "from" that does not exist yet).
bool rebuild(Composite& c) {
    const uintptr_t mapped = map_get(c.guid);
    if (!c.installed) {
        c.game_ptr = mapped;
        c.had_game_entry = mapped != 0;
    }
    if (c.layers.empty()) {
        if (c.installed) {
            // creatures inside one of the rules that go away hold its index
            uint32_t base_rules = 0;
            if (c.had_game_entry) seh_read(reinterpret_cast<const uint8_t*>(c.game_ptr) + AL.rules + 4, &base_rules, 4);
            if (c.rule_count > base_rules) ai_behavior_guard_arm();
            if (c.had_game_entry) map_put(c.guid, c.game_ptr);
            else map_erase(c.guid);
            c.installed = false;
        }
        retire(std::move(c.block));
        c.dirty = false;
        return true;
    }
    RawArsenal a;
    std::memcpy(a.guid, c.guid, 16);
    const uint8_t* base = reinterpret_cast<const uint8_t*>(c.game_ptr);
    if (!base && c.has_from) {
        // The game's record when that arsenal has one: others' additions to it are theirs, and go when they go.
        base = reinterpret_cast<const uint8_t*>(map_get(c.from));
        for (const auto& o : g_all)
            if (o->installed && o->had_game_entry && std::memcmp(o->guid, c.from, 16) == 0) base = reinterpret_cast<const uint8_t*>(o->game_ptr);
        if (!base) {
            if (c.problem.empty()) {
                c.problem = "it starts from " + guid_text(c.from) + ", which is not in the registry yet";
                KC_W("arsenals: %s: %s", guid_text(c.guid).c_str(), c.problem.c_str());
            }
            return false;
        }
    }
    if (base && !read_arsenal(base, &a)) {
        c.problem = "the base arsenal could not be read";
        KC_E("arsenals: %s: %s", guid_text(c.guid).c_str(), c.problem.c_str());
        c.dirty = false;
        return true;
    }
    std::memcpy(a.guid, c.guid, 16);
    c.problem.clear();
    for (Layer& l : c.layers) {
        RawArsenal trial = a;
        l.problem.clear();
        if (!apply_layer(l, &trial, &l.problem)) {
            KC_E("arsenals: %s: the additions of owner %llu were refused: %s", guid_text(c.guid).c_str(),
                 static_cast<unsigned long long>(l.owner), l.problem.c_str());
            continue;
        }
        a = std::move(trial);
    }
    std::vector<uint8_t> bytes = write_arsenal(a);
    // what was written must read back as what was meant
    RawArsenal back;
    if (!read_arsenal(bytes.data(), &back) || !same_arsenal(a, back)) {
        c.problem = "the written record did not read back";
        KC_E("arsenals: %s: %s", guid_text(c.guid).c_str(), c.problem.c_str());
        c.dirty = false;
        return true;
    }
    auto block = std::make_unique<uint8_t[]>(bytes.size() + 16);
    auto* at = reinterpret_cast<uint8_t*>((reinterpret_cast<uintptr_t>(block.get()) + 15) & ~uintptr_t(15));
    std::memcpy(at, bytes.data(), bytes.size());
    if (!map_put(c.guid, reinterpret_cast<uintptr_t>(at))) {
        c.problem = "the registry's map could not be written";
        KC_E("arsenals: %s: %s", guid_text(c.guid).c_str(), c.problem.c_str());
        c.dirty = false;
        return true;
    }
    const bool fewer_rules = c.installed && a.rules.size() < c.rule_count;
    retire(std::move(c.block));
    c.block = std::move(block);
    c.block_size = bytes.size();
    c.attack_count = a.attacks.size();
    c.rule_count = a.rules.size();
    c.installed = true;
    c.dirty = false;
    if (fewer_rules) ai_behavior_guard_arm();
    KC_I("arsenals: %s: %zu attack(s), %zu rule(s), %zu B, %zu owner(s)%s", guid_text(c.guid).c_str(), a.attacks.size(), a.rules.size(),
         bytes.size(), c.layers.size(), c.had_game_entry ? " on the game's arsenal" : "");
    return true;
}

void read_layout() {
    if (g_layout_tried) return;
    g_layout_tried = true;
    TypeInfo ar, at, ru, co;
    std::string err;
    if (!type_info("keen::enemy::EnemyArsenalResource", &ar, &err) || !type_info("keen::enemy01::AttackDesscriptionResource", &at, &err) ||
        !type_info("keen::enemy01::BehaviorDesscriptionResource", &ru, &err) || !type_info("keen::enemy01::AttackCommand", &co, &err)) {
        g_layout_error = err;
        return;
    }
    ArsenalLayout l;
    uint32_t n = 0;
    l.record_size = ar.size;
    l.attack_size = at.size;
    l.rule_size = ru.size;
    l.command_size = co.size;
    const bool ok = field_layout(ar, "attacks", &l.attacks, &n) && field_layout(ar, "behaviors", &l.rules, &n) &&
                    field_layout(at, "description.actionSequence", &l.attack_sequence, &n) && field_layout(at, "description.attackStyle", &l.attack_style, &n) &&
                    field_layout(at, "description.threat", &l.attack_threat, &n) && field_layout(at, "selectionConstrain", &l.attack_selection, &n) &&
                    field_layout(at, "executionConstrain", &l.attack_execution, &n) && field_layout(at, "abortConstraints", &l.attack_abort, &n) &&
                    field_layout(at, "commands", &l.attack_commands, &n) && field_layout(at, "data", &l.attack_data, &n) &&
                    field_layout(ru, "executionConstrain", &l.rule_when, &n) && field_layout(ru, "abortConstraints", &l.rule_abort, &n) &&
                    field_layout(ru, "actions", &l.rule_actions, &n) && field_layout(co, "constraints", &l.command_constraints, &n) &&
                    field_layout(co, "actions", &l.command_actions, &n);
    if (!ok) {
        g_layout_error = "the arsenal's fields are not in the reflection";
        return;
    }
    AL = l;
    g_layout_ok = true;
    rules_read_layout();
}

}  // namespace

void arsenal_init() {
    const auto cst = [](const char* name, int64_t* out) { return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK; };
    g_map_ok = g_api->feature(kstr("creatures.arsenal_writer")) && cst("creatures/arsenals.map_count", &g_m_count) &&
               cst("creatures/arsenals.map_cap", &g_m_cap) && cst("creatures/arsenals.map_bits", &g_m_bits) &&
               cst("creatures/arsenals.map_keys", &g_m_keys) && cst("creatures/arsenals.map_values", &g_m_values);
}

bool arsenal_writer_ready() {
    read_layout();
    return g_layout_ok && g_map_ok && F.reflection;
}

bool arsenal_layout_ready() {
    read_layout();
    return g_layout_ok;
}

const char* arsenal_layout_error() { return g_layout_error.c_str(); }

RuleUses arsenal_uses() { return g_uses; }

uint32_t arsenal_count_kiln() {
    uint32_t n = 0;
    for (const auto& c : g_all) n += c->installed;
    return n;
}

void arsenal_map_stats(uint32_t* capacity, uint32_t* count) {
    MapView m;
    if (!map_view(&m)) return;
    *capacity = static_cast<uint32_t>(m.cap);
    *count = m.count;
}

void arsenal_pre() {
    const uintptr_t w = world();
    if (!w || !g_map_ok) return;
    if (!g_all.empty() || g_reg) {
        const uintptr_t reg = g_api->singleton(w, kstr("g38_enemy::EnemyArsenalRegistry"));
        if (reg != g_reg) {
            // a new world: its registry is the game's again
            g_reg = reg;
            for (auto& c : g_all) {
                c->installed = false;
                c->dirty = true;
            }
        }
    }
    if (g_reg) {
        bool removed = false;
        for (auto it = g_all.begin(); it != g_all.end();) {
            Composite& c = **it;
            if (c.dirty) rebuild(c);
            if (!c.dirty && c.layers.empty()) {
                it = g_all.erase(it);
                removed = true;
                continue;
            }
            ++it;
        }
        if (removed) recompute_uses();
    }
    for (auto it = g_retired.begin(); it != g_retired.end();) it = g_step >= it->at ? g_retired.erase(it) : std::next(it);
}

void arsenal_release_owner(KilnOwner owner) {
    for (auto& c : g_all) {
        const size_t before = c->layers.size();
        std::erase_if(c->layers, [&](const Layer& l) { return l.owner == owner; });
        if (c->layers.size() != before) c->dirty = true;
    }
    recompute_uses();
}

KilnResult KILN_CALL arsenal_define_api(KilnOwner owner, KilnStr text, char* error, size_t cap, size_t* len) {
    std::string err;
    const auto fail = [&](KilnResult rc) {
        if (len) *len = err.size();
        if (error && cap) {
            const size_t n = std::min(cap - 1, err.size());
            std::memcpy(error, err.data(), n);
            error[n] = 0;
        }
        return rc;
    };
    if (!on_tick()) return err = "not on the tick thread", fail(KILN_ERR_WRONG_THREAD);
    if (!owner) return err = "no owner", fail(KILN_ERR_INVALID_ARGUMENT);
    if (!arsenal_writer_ready()) {
        err = g_layout_error.empty() ? "the arsenal writer is not available on this build" : g_layout_error;
        return fail(KILN_ERR_UNAVAILABLE);
    }
    const json doc = json::parse(sv(text), nullptr, false);
    if (doc.is_discarded() || !doc.is_object()) return err = "not a JSON object", fail(KILN_ERR_INVALID_ARGUMENT);
    uint8_t guid[16], from[16];
    if (!doc.contains("guid") || !guid_of(doc["guid"], guid)) return err = "an arsenal needs a \"guid\"", fail(KILN_ERR_INVALID_ARGUMENT);
    bool has_from = false;
    if (doc.contains("from")) {
        if (!guid_of(doc["from"], from)) return err = "\"from\" must be a GUID", fail(KILN_ERR_INVALID_ARGUMENT);
        has_from = true;
    }
    Layer layer;
    layer.owner = owner;
    if (doc.contains("attacks")) {
        if (!doc["attacks"].is_array()) return err = "\"attacks\" must be a list", fail(KILN_ERR_INVALID_ARGUMENT);
        for (const auto& j : doc["attacks"]) {
            PatchAttack p;
            std::string e;
            if (!encode_attack(j, &p, &layer.uses, &e)) return err = "attack: " + e, fail(KILN_ERR_INVALID_ARGUMENT);
            layer.attacks.push_back(std::move(p));
        }
    }
    if (doc.contains("rules")) {
        if (!doc["rules"].is_array()) return err = "\"rules\" must be a list", fail(KILN_ERR_INVALID_ARGUMENT);
        for (const auto& j : doc["rules"]) {
            PatchRule p;
            std::string e;
            if (!encode_rule(j, &p, &layer.uses, &e)) return err = "rule: " + e, fail(KILN_ERR_INVALID_ARGUMENT);
            layer.rules.push_back(std::move(p));
        }
    }
    if (layer.uses.any_kiln && !rules_ensure_hooks()) {
        err = "Kiln's constraint and action kinds are not available on this build";
        return fail(KILN_ERR_UNAVAILABLE);
    }
    if (layer.uses.attack_action && !rules_ensure_attack_hooks()) {
        err = "the attack action is not available on this build";
        return fail(KILN_ERR_UNAVAILABLE);
    }
    Composite* c = nullptr;
    for (auto& x : g_all)
        if (std::memcmp(x->guid, guid, 16) == 0) c = x.get();
    if (!c) {
        g_all.push_back(std::make_unique<Composite>());
        c = g_all.back().get();
        std::memcpy(c->guid, guid, 16);
    }
    if (has_from) {
        std::memcpy(c->from, from, 16);
        c->has_from = true;
    }
    std::erase_if(c->layers, [&](const Layer& l) { return l.owner == owner; });
    c->layers.push_back(std::move(layer));
    c->dirty = true;
    recompute_uses();
    return KILN_OK;
}

KilnResult KILN_CALL arsenal_remove_api(KilnOwner owner, const KilnGuid* guid) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (!owner) return KILN_ERR_INVALID_ARGUMENT;
    bool any = false;
    for (auto& c : g_all) {
        if (guid && std::memcmp(c->guid, guid->b, 16) != 0) continue;
        const size_t before = c->layers.size();
        std::erase_if(c->layers, [&](const Layer& l) { return l.owner == owner; });
        if (c->layers.size() != before) {
            c->dirty = true;
            any = true;
        }
    }
    recompute_uses();
    return any || !guid ? KILN_OK : KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL arsenal_info_api(const KilnGuid* guid, char* buffer, size_t cap, size_t* len) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (!guid) return KILN_ERR_INVALID_ARGUMENT;
    if (!arsenal_writer_ready()) return KILN_ERR_UNAVAILABLE;
    if (!g_reg && world()) g_reg = g_api->singleton(world(), kstr("g38_enemy::EnemyArsenalRegistry"));
    const uintptr_t p = map_get(guid->b);
    if (!p) return KILN_ERR_NOT_FOUND;
    RawArsenal a;
    if (!read_arsenal(reinterpret_cast<const uint8_t*>(p), &a)) return KILN_ERR_INTERNAL;
    json j;
    j["guid"] = guid_text(guid->b);
    bool kiln = false;
    json problems = json::array();
    for (const auto& c : g_all) {
        if (std::memcmp(c->guid, guid->b, 16) != 0) continue;
        kiln = c->installed;
        if (!c->problem.empty()) problems.push_back(c->problem);
        for (const Layer& l : c->layers)
            if (!l.problem.empty()) problems.push_back(l.problem);
    }
    j["kiln"] = kiln;
    j["address"] = p;
    json attacks = json::array();
    for (size_t i = 0; i < a.attacks.size(); ++i) {
        uint16_t style = 0;
        std::memcpy(&style, a.attacks[i].rec.data() + AL.attack_style, 2);
        attacks.push_back({{"index", i},
                           {"guid", guid_text(a.attacks[i].rec.data())},
                           {"sequence", guid_text(a.attacks[i].rec.data() + AL.attack_sequence)},
                           {"style", style},
                           {"selection", a.attacks[i].selection.size()}});
    }
    j["attacks"] = attacks;
    json rules = json::array();
    uint32_t off = 0, size = 0;
    TypeInfo rt;
    std::string e;
    const bool g = type_info("keen::enemy01::BehaviorDesscriptionResource", &rt, &e) && field_layout(rt, "guid", &off, &size);
    for (size_t i = 0; i < a.rules.size(); ++i)
        rules.push_back({{"index", i},
                         {"guid", g ? guid_text(a.rules[i].rec.data() + off) : ""},
                         {"mode", a.rules[i].rec[0]},
                         {"when", a.rules[i].when.size()},
                         {"actions", a.rules[i].actions.size()}});
    j["rules"] = rules;
    j["problems"] = problems;
    const std::string text = j.dump();
    if (len) *len = text.size();
    if (!buffer || cap < text.size() + 1) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buffer, text.data(), text.size());
    buffer[text.size()] = 0;
    return KILN_OK;
}

KilnResult KILN_CALL arsenal_refresh_api(uint32_t* out) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    uint32_t n = 0;
    for (auto& c : g_all) {
        if (c->layers.empty()) continue;
        c->dirty = true;
        ++n;
    }
    if (out) *out = n;
    return KILN_OK;
}

KilnResult KILN_CALL arsenal_selftest_api(uint32_t* checked, uint32_t* bad) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (!arsenal_writer_ready() || !world()) return KILN_ERR_UNAVAILABLE;
    const uintptr_t reg = g_api->singleton(world(), kstr("g38_enemy::EnemyArsenalRegistry"));
    int64_t data_off = 0;
    if (!reg || g_api->constant(kstr("creatures/arsenals.data"), &data_off) != KILN_OK) return KILN_ERR_UNAVAILABLE;
    uint64_t data = 0;
    if (!seh_u64(reg + data_off, &data) || !data) return KILN_ERR_UNAVAILABLE;
    int32_t rel = 0;
    uint32_t n = 0;
    if (!seh_read(reinterpret_cast<void*>(data), &rel, 4) || !seh_read(reinterpret_cast<void*>(data + 4), &n, 4) || n > 100000) return KILN_ERR_INTERNAL;
    uint32_t c = 0, b = 0;
    for (uint32_t i = 0; i < n; ++i) {
        const auto* rec = reinterpret_cast<const uint8_t*>(data + rel + static_cast<uint64_t>(i) * AL.record_size);
        ++c;
        RawArsenal a, back;
        if (!read_arsenal(rec, &a)) {
            ++b;
            continue;
        }
        const std::vector<uint8_t> bytes = write_arsenal(a);
        if (!read_arsenal(bytes.data(), &back) || !same_arsenal(a, back)) ++b;
    }
    if (checked) *checked = c;
    if (bad) *bad = b;
    return KILN_OK;
}

}  // namespace kcr
