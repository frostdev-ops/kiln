// SPDX-License-Identifier: MIT
// kiln.content gamedata: changes as layers in kiln.content's store, and the data-pack sections.
//
//  - A value change is one store slot per leaf (kind GD_KIND_BASE + section, id = entry id, field = an index into the
//    path table). The store keeps the game's original bytes and one layer per owner, as for items.
//  - Tooltips: a config with a loca tag that sits next to a `uiValues` list (ItemInfo, Perk, BuffType, SkillTreeNode)
//    has a uiValue whose locaId = FNV-1a(locaTag); it follows the config, scaled by the config's valueFormat
//    (Normal x1, Percentage x100, Duration / DurationMilliseconds x1000 (ms), Float x100), as the same owner's layer.
//  - Config adds: a config a Configurations array lacks is added by pointing the array's 8-byte header at a copy in
//    Kiln's memory (each element's self-relative offset rebased) plus a new element built from an existing entry of
//    that config id elsewhere in the game data. The header is the owner's layer; revert = the original 8 bytes.
//  - Event order: the sequence walker (server 0x34620) stops at the first event at or after the window's end, so a
//    sub-sequence's events must stay sorted by time. After time changes, the events header points at a sorted copy
//    (an internal layer), recomputed from the effective times whenever layers change.
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <map>
#include <mutex>
#include <set>
#include <unordered_set>

#include "gamedata.h"

namespace kc {
namespace gd {

namespace {

constexpr int32_t kSubAdd = 200;    // Slot.sub: a Configurations array header (config adds)
constexpr int32_t kSubOrder = 201;  // Slot.sub: a sub-sequence's events header (sorted copy)
constexpr int32_t kSubUi = 202;     // Slot.sub: the header of a list of rows (tooltip rows, a buff's VFX entries), a whole new list

std::vector<std::string> g_paths;
std::unordered_map<std::string, int32_t> g_path_index;

struct SlotInfo {
    uint32_t section = 0, id = 0;
    std::string name, path;
    Field f;
    int32_t sub = -1;
    uintptr_t configs = 0;   // kSubAdd: the Configurations struct
    const char* array = "";  // kSubAdd: "simple" / "scaled"
};
std::unordered_map<uintptr_t, SlotInfo> g_info;

// config adds: (owner, header) -> the element data this owner added, in order
struct Added {
    uint32_t id;
    uintptr_t data;
    uint32_t hash, size;
};
std::map<std::pair<KilnOwner, uintptr_t>, std::vector<Added>> g_added;
std::unordered_map<uintptr_t, uintptr_t> g_added_data;  // an added element's data -> its Configurations array header

// sub-sequences whose event times were changed: events header -> (entry id)
std::map<uintptr_t, uint32_t> g_touched;
KilnOwner g_internal = 0;
uint64_t g_seq = 0;
bool g_modified_stale = true;  // entry_modified's cache

int32_t path_index(const std::string& p) {
    auto it = g_path_index.find(p);
    if (it != g_path_index.end()) return it->second;
    const int32_t i = static_cast<int32_t>(g_paths.size());
    g_paths.push_back(p);
    g_path_index.emplace(p, i);
    return i;
}

std::string last_name(const std::string& path) {
    size_t i = path.find_last_of(".]");
    if (i == std::string::npos) return path;
    return path.substr(i + 1);
}

bool put(KilnOwner owner, uint64_t seq, uint32_t section, const GEntry& e, const std::string& path, const Field& f,
         uintptr_t addr, const uint8_t* bytes, const std::string& source, int32_t sub = -1) {
    if (!store_put(owner, seq, GD_KIND_BASE + section, e.id, path_index(path), sub, addr, f.bytes, bytes, source)) return false;
    SlotInfo& si = g_info[addr];
    si.section = section, si.id = e.id, si.name = e.name, si.path = path, si.f = f, si.sub = sub;
    return true;
}

bool original_bytes(uintptr_t addr, uint8_t n, uint8_t* out) {
    std::memset(out, 0, 16);
    return store_original(addr, n, out) || rd(addr, out, n);
}

// ---- tooltips ----
int64_t ui_scale(uint8_t format, double v, bool* ok) {
    *ok = true;
    switch (format) {
        case 0: return std::llround(v);              // Normal
        case 1: return std::llround(v * 100.0);      // Percentage
        case 2: case 3: return std::llround(v * 1000.0);  // Duration, DurationMilliseconds (ms)
        case 4: return std::llround(v * 100.0);      // Float
        default: *ok = false; return 0;
    }
}

void pair_tooltip(KilnOwner owner, uint64_t seq, uint32_t section, const GEntry& e, const Resolved& r, const Field& f,
                  const uint8_t* bytes, bool reset, const std::string& source) {
    if (!r.config || !r.uivalues_owner || !r.uivalues_type || !r.config_type) return;
    // only the config's value has a tooltip (not its scaleFactor, ...)
    const TField* own_value = r.config_type->field("value");
    if (!own_value || r.config + own_value->offset != r.node.addr) return;
    const TType* ui_t = type_named("keen::ImpactUiValue");
    // ImpactConfig base: locaTag (16 B), valueFormat, at the same offsets in every derived config
    const TType* ct = r.config_type;
    if (!ui_t) return;
    const TField* lt = ct->field("locaTag");
    const TField* vf = ct->field("valueFormat");
    const TField* uiv = r.uivalues_type->field("uiValues");
    const TField* lid = ui_t->field("locaId");
    const TField* uval = ui_t->field("value");
    if (!lt || !vf || !uiv || !lid || !uval) return;
    uint8_t tag[16];
    if (!rd(r.config + lt->offset, tag, 16)) return;
    bool any = false;
    for (uint8_t b : tag) any = any || b;
    if (!any) return;
    uint8_t format = 0;
    rdv(r.config + vf->offset, format);
    const uint32_t loca = fnv1a(tag, 16);
    uintptr_t data = 0;
    uint32_t n = 0;
    if (!blob_array(r.uivalues_owner + uiv->offset, &data, &n)) return;
    for (uint32_t i = 0; i < n; ++i) {
        const uintptr_t u = data + static_cast<uintptr_t>(i) * ui_t->size;
        uint32_t id = 0;
        if (!rdv(u + lid->offset, id) || id != loca) continue;
        const uintptr_t at = u + uval->offset;
        if (reset) {
            store_drop(owner, at);
            return;
        }
        bool ok = false;
        const int64_t v = ui_scale(format, as_double(decode(f, bytes)), &ok);
        if (!ok) return;
        Field uf;
        uf.bytes = 4, uf.vt = KILN_CV_INT, uf.is_signed = true, uf.type_name = "keen::sint32", uf.writable = true;
        const int32_t v32 = static_cast<int32_t>(std::clamp<int64_t>(v, INT32_MIN, INT32_MAX));
        uint8_t b[16] = {};
        std::memcpy(b, &v32, 4);
        // the tooltip path sits next to the configs: "<owner path>.uiValues[i].value"
        std::string base = r.path;
        const size_t cut = base.find(".impactValues");
        const size_t cut2 = base.find(".configValues");
        std::string prefix = cut != std::string::npos ? base.substr(0, cut) : cut2 != std::string::npos ? base.substr(0, cut2) : "";
        if (base.rfind("impactValues", 0) == 0 || base.rfind("configValues", 0) == 0) prefix.clear();
        put(owner, seq, section, e, (prefix.empty() ? "" : prefix + ".") + "uiValues[" + std::to_string(i) + "].value", uf, at, b,
            source);
        return;
    }
}

// ---- config adds ----
bool add_config(KilnOwner owner, uint64_t seq, uint32_t section, const GEntry& e, const Resolved& r, const gjson& value,
                const std::string& source, std::string* err) {
    Exemplar ex;
    if (!config_exemplar(r.add_id, &ex, err)) return false;
    const TType* vt = type_hash(ex.hash);
    vt = vt ? vt->resolved() : nullptr;
    const TType* cfgs = type_named("keen::impact::Configurations");
    const char* array = ex.scaled ? "scaled" : "simple";
    const TField* af = cfgs ? cfgs->field(array) : nullptr;
    if (!vt || !af) {
        *err = "the config's type is not in the reflection";
        return false;
    }
    const uintptr_t header = r.add_configs + af->offset;
    auto& mine = g_added[{owner, header}];
    uintptr_t data = 0;
    for (const Added& a : mine)
        if (a.id == r.add_id) data = a.data;
    const bool fresh = data == 0;
    if (fresh) {
        data = reinterpret_cast<uintptr_t>(near_alloc(header, ex.size));
        if (!data) {
            *err = "no memory near the configs";
            return false;
        }
        std::memcpy(reinterpret_cast<void*>(data), ex.bytes.data(), ex.size);
        if (const TField* lt = vt->field("locaTag")) std::memset(reinterpret_cast<void*>(data + lt->offset), 0, 16);  // no tooltip
    }
    // the value inside the new element (Kiln's memory: written directly, it is this owner's element)
    std::vector<Resolved> rs;
    std::string rerr;
    const std::string sub = r.add_rest.empty() ? "value" : r.add_rest;
    if (!resolve(Root::Plain, Node{vt, data}, sub, &rs, &rerr) || rs.size() != 1) {
        *err = "the config has no " + sub + " (" + rerr + ")";
        return false;
    }
    Field f;
    if (!leaf_of(rs[0].node.t, &f)) {
        *err = sub + " is not a single value";
        return false;
    }
    uint8_t b[16];
    bool reset = false;
    if (!parse_leaf(f, value, reinterpret_cast<const uint8_t*>(rs[0].node.addr + f.offset), b, &reset, err)) return false;
    if (reset) {  // {"reset": true} on an added config: this owner's adds of that array come off
        g_added.erase({owner, header});
        store_drop(owner, header);
        return true;
    }
    std::memcpy(reinterpret_cast<void*>(rs[0].node.addr + f.offset), b, f.bytes);
    if (fresh) {
        mine.push_back(Added{r.add_id, data, ex.hash, ex.size});
        g_added_data[data] = header;
    }
    // the array: the game's original elements (rebased) + this owner's added ones
    uint8_t ob[16];
    original_bytes(header, 8, ob);
    int32_t orel = 0;
    uint32_t on = 0;
    std::memcpy(&orel, ob, 4);
    std::memcpy(&on, ob + 4, 4);
    const uintptr_t odata = orel ? header + static_cast<intptr_t>(orel) : 0;
    const uint32_t total = on + static_cast<uint32_t>(mine.size());
    auto* arr = static_cast<uint8_t*>(near_alloc(header, total * 12));
    if (!arr) {
        *err = "no memory near the configs";
        return false;
    }
    const uintptr_t abase = reinterpret_cast<uintptr_t>(arr);
    auto write_el = [&](uint32_t i, uint32_t hash, uintptr_t d, uint32_t size) {
        const uintptr_t el = abase + i * 12;
        const int64_t rel = static_cast<int64_t>(d) - static_cast<int64_t>(el + 4);
        const int32_t r32 = static_cast<int32_t>(rel);
        std::memcpy(arr + i * 12, &hash, 4);
        std::memcpy(arr + i * 12 + 4, &r32, 4);
        std::memcpy(arr + i * 12 + 8, &size, 4);
        return rel >= INT32_MIN && rel <= INT32_MAX;
    };
    for (uint32_t i = 0; i < on; ++i) {
        uint32_t h = 0, sz = 0;
        uintptr_t d = 0;
        if (!blob_variant(odata + i * 12, &h, &d, &sz) || !write_el(i, h, d, sz)) {
            *err = "the configs are out of reach";
            return false;
        }
    }
    for (size_t k = 0; k < mine.size(); ++k)
        if (!write_el(on + static_cast<uint32_t>(k), mine[k].hash, mine[k].data, mine[k].size)) {
            *err = "the configs are out of reach";
            return false;
        }
    const int64_t hrel = static_cast<int64_t>(abase) - static_cast<int64_t>(header);
    if (hrel < INT32_MIN || hrel > INT32_MAX) {
        *err = "the configs are out of reach";
        return false;
    }
    uint8_t hb[16] = {};
    const int32_t h32 = static_cast<int32_t>(hrel);
    std::memcpy(hb, &h32, 4);
    std::memcpy(hb + 4, &total, 4);
    Field hf;
    hf.bytes = 8, hf.vt = KILN_CV_NONE, hf.type_name = "keen::impact::ConfigurationArray";
    const std::string prefix = r.path.substr(0, r.path.size() - (config_name(r.add_id).size() + (r.add_rest.empty() ? 0 : r.add_rest.size() + 1)));
    std::string ppath = prefix;
    if (!ppath.empty() && ppath.back() == '.') ppath.pop_back();
    if (!put(owner, seq, section, e, ppath + (ppath.empty() ? "" : ".") + array, hf, header, hb, source, kSubAdd)) {
        *err = "the configs are not readable";
        return false;
    }
    SlotInfo& si = g_info[header];
    si.configs = r.add_configs;
    si.array = array;
    return true;
}

// ---- a whole list of rows ----
// "uiValues": [ {"locaId": "0xc9d82dea", "valueFormat": "Float", "value": 300}, ... ]: the rows of an item, a buff or a perk, in any number, in
// place of the game's. A buff's "whileApplied" and "onCreate" work the same way: [ {"slot": "Chest", "vfx": "<guid>", "offset.localOffset.y": 1} ].
// The list is Kiln's memory near the entry: the rows are written directly (they are this owner's), and only the list's header is a layered slot,
// so taking the owner off puts the game's list back and the content sync exports the list as one value. A field a row does not name is zero.
bool is_ui_values(const TType* t) {
    const TType* r = t ? t->resolved() : nullptr;
    if (!r || r->kind != K_BLOBARRAY) return false;
    const TType* in = r->inner();
    in = in ? in->resolved() : nullptr;
    return in && (in->name == "keen::ImpactUiValue" || in->name == "keen::WhileAppliedHelper" || in->name == "keen::OnCreateHelper");
}

// One row for the content sync: every field that isn't zero, by its path, which is what a row takes.
void row_leaves(const TType* decl, uintptr_t addr, const std::string& prefix, gjson* row, int depth) {
    Field f;
    if (leaf_of(decl, &f)) {
        uint8_t b[16] = {}, zero[16] = {};
        if (rd(addr + f.offset, b, f.bytes) && std::memcmp(b, zero, sizeof b) != 0) (*row)[prefix] = leaf_json(f, b, true);
        return;
    }
    const TType* t = decl ? decl->resolved() : nullptr;
    if (!t || depth > 8) return;
    if (t->kind == K_STRUCT) {
        for (const TField& x : t->fields) row_leaves(x.type(), addr + x.offset, prefix.empty() ? x.name : prefix + "." + x.name, row, depth + 1);
    } else if (t->kind == K_ARRAY) {
        const TType* el = t->inner();
        el = el ? el->resolved() : nullptr;
        if (!el || !el->size) return;
        const uint32_t n = t->count ? t->count : t->size / el->size;
        for (uint32_t i = 0; i < n && i < 256; ++i) row_leaves(el, addr + static_cast<uintptr_t>(i) * el->size, prefix + "[" + std::to_string(i) + "]", row, depth + 1);
    }
}

struct UiList {
    uintptr_t data = 0;
    uint32_t capacity = 0;
};
std::map<std::pair<KilnOwner, uintptr_t>, UiList> g_ui_lists;

bool rewrite_ui_values(KilnOwner owner, uint64_t seq, uint32_t section, const GEntry& e, const Resolved& r, const gjson& rows,
                       const std::string& source, std::string* err) {
    const TType* ui_t = r.node.t->resolved()->inner();
    ui_t = ui_t ? ui_t->resolved() : nullptr;
    if (!ui_t || ui_t->size == 0) {
        *err = "the tooltip row's type is not in the reflection";
        return false;
    }
    if (rows.size() > 64) {
        *err = "more than 64 tooltip rows";
        return false;
    }
    const uintptr_t header = r.node.addr;
    const uint32_t n = static_cast<uint32_t>(rows.size());
    UiList& list = g_ui_lists[{owner, header}];
    if (n > list.capacity) {
        auto* mem = static_cast<uint8_t*>(near_alloc(header, static_cast<size_t>(n) * ui_t->size));
        if (!mem) {
            *err = "no memory near the entry";
            return false;
        }
        list.data = reinterpret_cast<uintptr_t>(mem);
        list.capacity = n;
    }
    if (list.data) std::memset(reinterpret_cast<void*>(list.data), 0, static_cast<size_t>(list.capacity) * ui_t->size);
    for (uint32_t i = 0; i < n; ++i) {
        const gjson& row = rows[i];
        const std::string at = "row " + std::to_string(i);
        if (!row.is_object()) {
            *err = at + " is not an object of the row's fields";
            return false;
        }
        const Node el{ui_t, list.data + static_cast<uintptr_t>(i) * ui_t->size};
        for (auto& [k, v] : row.items()) {
            std::vector<Resolved> rs;
            std::string rerr;
            Field f;
            if (!resolve(Root::Plain, el, k, &rs, &rerr) || rs.size() != 1 || !rs[0].node.t || !leaf_of(rs[0].node.t, &f)) {
                *err = at + ": a row has no field " + k;
                return false;
            }
            uint8_t zero[16] = {}, b[16] = {};
            bool reset = false;
            std::string perr;
            if (!parse_leaf(f, v, zero, b, &reset, &perr)) {
                *err = at + "." + k + ": " + perr;
                return false;
            }
            if (!reset) std::memcpy(reinterpret_cast<void*>(rs[0].node.addr + f.offset), b, f.bytes);
        }
    }
    const int64_t rel = n ? static_cast<int64_t>(list.data) - static_cast<int64_t>(header) : 0;
    if (rel < INT32_MIN || rel > INT32_MAX) {
        *err = "the rows are out of reach";
        return false;
    }
    uint8_t hb[16] = {};
    const int32_t r32 = static_cast<int32_t>(rel);
    std::memcpy(hb, &r32, 4);
    std::memcpy(hb + 4, &n, 4);
    Field hf;
    hf.bytes = 8, hf.vt = KILN_CV_NONE, hf.type_name = r.node.t->resolved()->name;
    if (!put(owner, seq, section, e, r.path, hf, header, hb, source, kSubUi)) {
        *err = "the list is not readable";
        return false;
    }
    return true;
}

// {"mul": 1} on the list (the content sync's "match the server"): this owner's layer puts the game's own list back.
bool game_ui_values(KilnOwner owner, uint64_t seq, uint32_t section, const GEntry& e, const Resolved& r, const std::string& source, std::string* err) {
    uint8_t ob[16];
    original_bytes(r.node.addr, 8, ob);
    Field hf;
    hf.bytes = 8, hf.vt = KILN_CV_NONE, hf.type_name = r.node.t->resolved()->name;
    if (put(owner, seq, section, e, r.path, hf, r.node.addr, ob, source, kSubUi)) return true;
    *err = "the list is not readable";
    return false;
}

// ---- event order ----
uint32_t event_time_offset(uint32_t hash) {
    static std::unordered_map<uint32_t, uint32_t> cache;
    auto it = cache.find(hash);
    if (it != cache.end()) return it->second;
    uint32_t off = 0xFFFFFFFF;
    const TType* t = type_hash(hash);
    t = t ? t->resolved() : nullptr;
    if (t && t->kind == K_STRUCT)
        if (const TField* f = t->field("time")) off = f->offset;
    cache.emplace(hash, off);
    return off;
}

void reorder(uintptr_t header, uint32_t id) {
    uint8_t ob[16];
    original_bytes(header, 8, ob);
    int32_t orel = 0;
    uint32_t n = 0;
    std::memcpy(&orel, ob, 4);
    std::memcpy(&n, ob + 4, 4);
    if (!orel || n < 2 || n > 4096) return;
    const uintptr_t odata = header + static_cast<intptr_t>(orel);
    struct Ev {
        uint32_t hash, size;
        uintptr_t data;
        int64_t time;
        uint32_t index;
    };
    std::vector<Ev> evs;
    for (uint32_t i = 0; i < n; ++i) {
        Ev v{};
        if (!blob_variant(odata + i * 12, &v.hash, &v.data, &v.size)) return;
        const uint32_t to = event_time_offset(v.hash);
        if (to == 0xFFFFFFFF || !rdv(v.data + to, v.time)) return;
        v.index = i;
        evs.push_back(v);
    }
    std::vector<Ev> sorted = evs;
    std::stable_sort(sorted.begin(), sorted.end(), [](const Ev& a, const Ev& b) { return a.time < b.time; });
    bool same = true;
    for (uint32_t i = 0; i < n; ++i) same = same && sorted[i].index == i;
    if (same) {
        store_drop(g_internal, header);
        return;
    }
    // the current internal order already this one?
    uint8_t cb[8];
    if (rd(header, cb, 8)) {
        int32_t crel = 0;
        uint32_t cn = 0;
        std::memcpy(&crel, cb, 4);
        std::memcpy(&cn, cb + 4, 4);
        if (crel != orel && cn == n) {
            bool match = true;
            for (uint32_t i = 0; i < n && match; ++i) {
                uint32_t h = 0, sz = 0;
                uintptr_t d = 0;
                match = blob_variant(header + crel + i * 12, &h, &d, &sz) && d == sorted[i].data;
            }
            if (match) return;
        }
    }
    auto* arr = static_cast<uint8_t*>(near_alloc(header, n * 12));
    if (!arr) return;
    const uintptr_t abase = reinterpret_cast<uintptr_t>(arr);
    for (uint32_t i = 0; i < n; ++i) {
        const int64_t rel = static_cast<int64_t>(sorted[i].data) - static_cast<int64_t>(abase + i * 12 + 4);
        if (rel < INT32_MIN || rel > INT32_MAX) return;
        const int32_t r32 = static_cast<int32_t>(rel);
        std::memcpy(arr + i * 12, &sorted[i].hash, 4);
        std::memcpy(arr + i * 12 + 4, &r32, 4);
        std::memcpy(arr + i * 12 + 8, &sorted[i].size, 4);
    }
    const int64_t hrel = static_cast<int64_t>(abase) - static_cast<int64_t>(header);
    if (hrel < INT32_MIN || hrel > INT32_MAX) return;
    uint8_t hb[16] = {};
    const int32_t h32 = static_cast<int32_t>(hrel);
    std::memcpy(hb, &h32, 4);
    std::memcpy(hb + 4, &n, 4);
    const GEntry* e = greg(KILN_GD_SEQUENCES).get(id);
    if (!e) return;
    Field hf;
    hf.bytes = 8, hf.vt = KILN_CV_NONE, hf.type_name = "keen::SequenceEvents";
    put(g_internal, next_code_seq(), KILN_GD_SEQUENCES, *e, "events", hf, header, hb, "gamedata: event order", kSubOrder);
}

void reorder_all() {
    for (auto& [h, id] : g_touched) reorder(h, id);
}

}  // namespace

void forget_added(KilnOwner owner, uintptr_t header) { g_added.erase({owner, header}); }

KilnOwner internal_owner() { return g_internal; }
std::string slot_path(int32_t field) {
    return field >= 0 && static_cast<size_t>(field) < g_paths.size() ? g_paths[static_cast<size_t>(field)] : "?";
}

void after_changes() {
    std::lock_guard lock(store_mutex());
    g_modified_stale = true;
    std::string err;
    store_flush(&err);
    if (!g_touched.empty()) {
        reorder_all();
        store_flush(&err);
    }
    if (!err.empty()) LOGW("gamedata: writing: %s", err.c_str());
    buffs_sync();  // the game reads buffs from Kiln's copy once there are buff clones: it follows the layers
}

uint32_t apply_value(KilnOwner owner, uint64_t seq, uint32_t section, const GEntry& e, const std::string& path,
                     const gjson& value, const std::string& source, std::vector<std::string>* errs) {
    const GRegistry& R = greg(section);
    std::vector<Resolved> rs;
    std::string err;
    if (!resolve(R.root, root_of(section, e), path, &rs, &err)) {
        errs->push_back(std::string(section_name(section)) + " " + e.name + ": " + err);
        return 0;
    }
    uint32_t n = 0;
    for (const Resolved& r : rs) {
        if (!r.node.t) {  // a config this struct does not have: add it
            // {"mul": 1} is "back to the game's", and the game's has none: not a reason to add one with an exemplar's value (the content sync's "match the server" sends these)
            if (value.is_object() && value.contains("mul") && value["mul"] == 1 && !value.contains("add")) {
                ++n;
                continue;
            }
            std::string aerr;
            if (add_config(owner, seq, section, e, r, value, source, &aerr)) ++n;
            else errs->push_back(std::string(section_name(section)) + " " + e.name + "." + r.path + ": " + aerr);
            continue;
        }
        if (is_ui_values(r.node.t) && (value.is_array() || (value.is_object() && value.value("mul", 0) == 1))) {
            std::string uerr;
            if (value.is_array() ? rewrite_ui_values(owner, seq, section, e, r, value, source, &uerr) : game_ui_values(owner, seq, section, e, r, source, &uerr)) ++n;
            else errs->push_back(std::string(section_name(section)) + " " + e.name + "." + r.path + ": " + uerr);
            continue;
        }
        Field f;
        if (!leaf_of(r.node.t, &f)) {
            if (value.is_object() && !value.contains("mul") && !value.contains("set") && !value.contains("reset")) {
                for (auto& [k, v] : value.items()) {
                    if (k.empty() || k[0] == '@') continue;
                    n += apply_value(owner, seq, section, e, r.path + (k[0] == '[' ? "" : ".") + k, v, source, errs);
                }
            } else {
                errs->push_back(std::string(section_name(section)) + " " + e.name + "." + r.path + ": a " + r.node.t->brief +
                                (r.node.t->kind == K_STRUCT ? " (give an object of its fields)" : " is not a value Kiln can set"));
            }
            continue;
        }
        // {"mul": 1} on a config that only exists because some owner added it (the content sync's "match the server"):
        // this owner's layer puts the game's own list back
        if (r.config && value.is_object() && value.contains("mul") && value["mul"] == 1 && !value.contains("add")) {
            if (auto ad = g_added_data.find(r.config); ad != g_added_data.end()) {
                uint8_t ob[16];
                original_bytes(ad->second, 8, ob);
                Field hf;
                hf.bytes = 8, hf.vt = KILN_CV_NONE, hf.type_name = "keen::impact::ConfigurationArray";
                auto inf = g_info.find(ad->second);
                const std::string ap = inf != g_info.end() ? inf->second.path : r.path;
                if (put(owner, seq, section, e, ap, hf, ad->second, ob, source, kSubAdd)) ++n;
                continue;
            }
        }
        if (identity_field(last_name(r.path))) {
            errs->push_back(std::string(section_name(section)) + " " + e.name + "." + r.path + " is read-only (an identity field)");
            continue;
        }
        const uintptr_t at = r.node.addr + f.offset;
        uint8_t orig[16], b[16];
        original_bytes(at, f.bytes, orig);
        bool reset = false;
        std::string perr;
        if (!parse_leaf(f, value, orig, b, &reset, &perr)) {
            errs->push_back(std::string(section_name(section)) + " " + e.name + "." + r.path + ": " + perr);
            continue;
        }
        if (reset) store_drop(owner, at);
        else if (!put(owner, seq, section, e, r.path, f, at, b, source)) {
            errs->push_back(std::string(section_name(section)) + " " + e.name + "." + r.path + ": not readable");
            continue;
        }
        pair_tooltip(owner, seq, section, e, r, f, b, reset, source);
        // a sequence reference next to its id (ItemSequence {sequenceId, sequence}): the id follows (FNV-1a of the GUID)
        if (f.ref && last_name(r.path) == "sequence" && r.path.size() > 8) {
            const std::string idp = r.path.substr(0, r.path.size() - 8) + "sequenceId";
            std::vector<Resolved> ir;
            std::string ierr;
            Field idf;
            if (resolve(R.root, root_of(section, e), idp, &ir, &ierr) && ir.size() == 1 && ir[0].node.t && leaf_of(ir[0].node.t, &idf) &&
                idf.bytes == 4) {
                const uintptr_t ia = ir[0].node.addr + idf.offset;
                if (reset) store_drop(owner, ia);
                else {
                    uint8_t ib[16] = {};
                    const uint32_t sid = fnv1a(b, 16);
                    std::memcpy(ib, &sid, 4);
                    put(owner, seq, section, e, ir[0].path, idf, ia, ib, source);
                }
            }
        }
        // a program reference next to its id (ecs::Impact {program, programId}): the id follows (FNV-1a of the GUID); the game
        // only computes it when it is 0, so a template clone with another program would run its base's without this
        if (f.ref && last_name(r.path) == "program" && r.path.size() > 7) {
            const std::string idp = r.path.substr(0, r.path.size() - 7) + "programId";
            std::vector<Resolved> ir;
            std::string ierr;
            Field idf;
            if (resolve(R.root, root_of(section, e), idp, &ir, &ierr) && ir.size() == 1 && ir[0].node.t && leaf_of(ir[0].node.t, &idf) &&
                idf.bytes == 4) {
                const uintptr_t ia = ir[0].node.addr + idf.offset;
                if (reset) store_drop(owner, ia);
                else {
                    uint8_t ib[16] = {};
                    const uint32_t pid = fnv1a(b, 16);
                    std::memcpy(ib, &pid, 4);
                    put(owner, seq, section, e, ir[0].path, idf, ia, ib, source);
                }
            }
        }
        // a perk reference next to its id (ItemInfo.perkReferences[i] / perkIds[i]): the id follows (FNV-1a of the
        // GUID, the PerkRegistry's key), as in kiln.content's kitbash "perks"
        if (f.ref && r.path.size() > 15) {
            const size_t ls = r.path.find_last_of('.');
            const std::string seg = ls == std::string::npos ? r.path : r.path.substr(ls + 1);
            if (seg.rfind("perkReferences[", 0) == 0 && seg.back() == ']') {
                const std::string idp = r.path.substr(0, r.path.size() - seg.size()) + "perkIds" + seg.substr(14);
                std::vector<Resolved> ir;
                std::string ierr;
                Field idf;
                if (resolve(R.root, root_of(section, e), idp, &ir, &ierr) && ir.size() == 1 && ir[0].node.t &&
                    leaf_of(ir[0].node.t, &idf) && idf.bytes == 4) {
                    const uintptr_t ia = ir[0].node.addr + idf.offset;
                    if (reset) store_drop(owner, ia);
                    else {
                        bool none = true;
                        for (int k = 0; k < 16; ++k) none = none && !b[k];
                        uint8_t ib[16] = {};
                        const uint32_t pid = none ? 0 : fnv1a(b, 16);
                        std::memcpy(ib, &pid, 4);
                        put(owner, seq, section, e, ir[0].path, idf, ia, ib, source);
                    }
                }
            }
        }
        if (section == KILN_GD_SEQUENCES && last_name(r.path) == "time" && r.path.find("events[") != std::string::npos) {
            const TType* st = greg(KILN_GD_SEQUENCES).type;
            if (const TField* ev = st ? st->field("events") : nullptr) g_touched[e.addr + ev->offset] = e.id;
        }
        ++n;
    }
    return n;
}

bool reset_path(KilnOwner owner, uint32_t section, const GEntry& e, const std::string& path) {
    const GRegistry& R = greg(section);
    std::vector<Resolved> rs;
    std::string err;
    bool any = false;
    if (resolve(R.root, root_of(section, e), path, &rs, &err)) {
        for (const Resolved& r : rs) {
            if (!r.node.t) continue;
            Field f;
            if (leaf_of(r.node.t, &f)) {
                any = store_drop(owner, r.node.addr + f.offset) || any;
                pair_tooltip(owner, 0, section, e, r, f, nullptr, true, "");
                continue;
            }
            enumerate(R.root, root_of(section, e), r.path, 100000, [&](const std::string&, const Node& n, const Field& lf) {
                any = store_drop(owner, n.addr + lf.offset) || any;
            });
        }
    }
    // config adds below this path
    for (auto it = g_added.begin(); it != g_added.end();) {
        auto inf = g_info.find(it->first.second);
        if (it->first.first == owner && inf != g_info.end() && inf->second.id == e.id && inf->second.section == section &&
            inf->second.path.rfind(path.substr(0, path.find('.')), 0) == 0) {
            any = store_drop(owner, it->first.second) || any;
            it = g_added.erase(it);
        } else {
            ++it;
        }
    }
    return any;
}

namespace {
// (section, id) of entries with a changed value, rebuilt when it is older than 250 ms or after changes made here
std::unordered_set<uint64_t> g_modified;
std::chrono::steady_clock::time_point g_modified_at{};
}  // namespace

bool entry_modified(uint32_t section, uint32_t id) {
    const auto now = std::chrono::steady_clock::now();
    if (g_modified_stale || now - g_modified_at > std::chrono::milliseconds(250)) {
        g_modified.clear();
        store_for_each([&](uintptr_t, const Slot& s) {
            if (s.kind >= GD_KIND_BASE && s.sub != kSubOrder && std::memcmp(s.current, s.original, s.n) != 0)
                g_modified.insert((uint64_t(s.kind - GD_KIND_BASE) << 32) | s.id);
        });
        g_modified_at = now;
        g_modified_stale = false;
    }
    return g_modified.count((uint64_t(section) << 32) | id) != 0;
}

uint32_t changed_entries(uint32_t section, std::vector<uint32_t>* ids) {
    std::set<uint32_t> s;
    store_for_each([&](uintptr_t, const Slot& sl) {
        if (sl.kind == GD_KIND_BASE + section && sl.sub != kSubOrder && std::memcmp(sl.current, sl.original, sl.n) != 0) s.insert(sl.id);
    });
    if (ids) ids->assign(s.begin(), s.end());
    return static_cast<uint32_t>(s.size());
}

// ============================================================================================= external sections =

namespace {
std::mutex g_ext_mu;
std::vector<External> g_ext;
struct Applied {
    std::string section, owner, source;
    KilnOwner o;
    std::string json;
    bool operator<(const Applied& b) const { return std::tie(section, owner, source) < std::tie(b.section, b.owner, b.source); }
};
std::set<Applied> g_ext_prev, g_ext_cur, g_ext_code;
std::vector<Applied> g_stash;  // sections of services that did not register yet (replayed at registration)
bool g_in_packs = false;
const char* kKnownExternal[] = {"creatures", "abilities"};

std::string owner_text(KilnOwner o) {
    const std::string l = owner_label(o);
    if (l.rfind("pack ", 0) == 0) return "pack:" + l.substr(5);
    if (l.rfind("mod ", 0) == 0) return "mod:" + l.substr(4);
    if (l.find("sync") != std::string::npos) return "sync";
    return l;
}

KilnStr ks(const std::string& s) { return KilnStr{s.data(), s.size()}; }

bool call_apply(const External& x, const Applied& a, uint64_t gen, bool reload, ApplyResult* out) {
    std::string rep(8192, '\0');
    size_t len = 0;
    KilnResult rc = KILN_ERR_INTERNAL;
    rc = x.apply(x.user, ks(x.name), ks(a.json), ks(a.owner), ks(a.source), gen, reload ? 1 : 0, rep.data(), rep.size(), &len);
    rep.resize(std::min(len, rep.size() > 0 ? rep.size() - 1 : 0));
    const gjson r = gjson::parse(rep, nullptr, false);
    if (!r.is_discarded() && r.is_object()) {
        if (out) {
            out->applied += r.value("applied", 0u);
            if (r.contains("errors") && r["errors"].is_array())
                for (const gjson& e : r["errors"]) out->errors.push_back(x.name + ": " + (e.is_string() ? e.get<std::string>() : e.dump()));
            if (r.contains("warnings") && r["warnings"].is_array())
                for (const gjson& e : r["warnings"]) out->warnings.push_back(x.name + ": " + (e.is_string() ? e.get<std::string>() : e.dump()));
        }
    } else if (rc != KILN_OK && out) {
        out->errors.push_back(x.name + ": the section was refused (" + std::to_string(rc) + ")");
    }
    return rc == KILN_OK;
}

void call_revert(const External& x, const std::string& owner, const std::string& source) {
    if (x.revert) x.revert(x.user, ks(x.name), ks(owner), ks(source));
}

const External* find_ext(const std::string& name) {
    for (const External& x : g_ext)
        if (x.name == name) return &x;
    return nullptr;
}
}  // namespace

KilnResult register_external(const External& x) {
    if (x.name.empty() || !x.apply || section_of(x.name) || x.name == "items" || x.name == "recipes" || x.name == "loot" ||
        x.name == "drops" || x.name == "options" || x.name == "format")
        return KILN_ERR_INVALID_ARGUMENT;
    std::vector<Applied> replay;
    {
        std::lock_guard lock(g_ext_mu);
        std::erase_if(g_ext, [&](const External& e) { return e.name == x.name; });
        g_ext.push_back(x);
        for (const Applied& a : g_stash)
            if (a.section == x.name) replay.push_back(a);
        std::erase_if(g_stash, [&](const Applied& a) { return a.section == x.name; });
    }
    LOGI("gamedata: pack section \"%s\" registered%s", x.name.c_str(),
         replay.empty() ? "" : (" (" + std::to_string(replay.size()) + " waiting section(s) applied now)").c_str());
    for (const Applied& a : replay) {
        ApplyResult r;
        call_apply(x, a, generation(), false, &r);
        for (const std::string& e : r.errors) LOGW("pack %s: %s", a.source.c_str(), e.c_str());
        std::lock_guard lock(g_ext_mu);
        (a.owner.rfind("pack:", 0) == 0 ? g_ext_prev : g_ext_code).insert(a);
    }
    return KILN_OK;
}

KilnResult unregister_external(const std::string& name) {
    std::lock_guard lock(g_ext_mu);
    const size_t before = g_ext.size();
    std::erase_if(g_ext, [&](const External& e) { return e.name == name; });
    return g_ext.size() != before ? KILN_OK : KILN_ERR_NOT_FOUND;
}

std::string waiting_json() {
    std::lock_guard lock(g_ext_mu);
    std::set<std::string> names;
    for (const Applied& a : g_stash) names.insert(a.section);
    return gjson(std::vector<std::string>(names.begin(), names.end())).dump();
}

std::string externals_json() {
    std::lock_guard lock(g_ext_mu);
    gjson a = gjson::array();
    for (const External& x : g_ext) a.push_back(x.name);
    return a.dump();
}

}  // namespace gd

// ============================================================================================= hooks ============

using namespace gd;

void gd_packs_begin() {
    clones_packs_begin();
    // apply_all_packs drops every pack owner's layers: their config adds go with them
    for (auto it = g_added.begin(); it != g_added.end();)
        it = owner_label(it->first.first).rfind("pack ", 0) == 0 ? g_added.erase(it) : std::next(it);
    std::lock_guard lock(g_ext_mu);
    g_in_packs = true;
    g_ext_cur.clear();
    std::erase_if(g_stash, [](const Applied& a) { return a.owner.rfind("pack:", 0) == 0; });  // re-stashed if still there
}

void gd_packs_end() {
    std::vector<std::pair<const External*, Applied>> gone;
    {
        std::lock_guard lock(g_ext_mu);
        g_in_packs = false;
        for (const Applied& a : g_ext_prev)
            if (!g_ext_cur.count(a))
                if (const External* x = find_ext(a.section)) gone.emplace_back(x, a);
        g_ext_prev = g_ext_cur;
    }
    for (auto& [x, a] : gone) call_revert(*x, a.owner, a.source);
    clones_packs_end();
    after_changes();
}

void gd_owner_gone(KilnOwner owner) {
    for (auto it = g_added.begin(); it != g_added.end();) it = it->first.first == owner ? g_added.erase(it) : std::next(it);
    const std::string ot = owner_text(owner);
    std::vector<const External*> xs;
    {
        std::lock_guard lock(g_ext_mu);
        for (const External& x : g_ext) xs.push_back(&x);
        std::erase_if(g_ext_code, [&](const Applied& a) { return a.o == owner; });
        std::erase_if(g_ext_prev, [&](const Applied& a) { return a.o == owner; });
        std::erase_if(g_stash, [&](const Applied& a) { return a.o == owner; });
    }
    for (const External* x : xs) call_revert(*x, ot, "");
    after_changes();
}

bool gd_is_section(const std::string& k) {
    if (section_of(k)) return true;
    for (const char* n : kKnownExternal)
        if (k == n) return true;
    std::lock_guard lock(g_ext_mu);
    return find_ext(k) != nullptr;
}

namespace {
const gjson* g_adds_done = nullptr;  // the document whose "add" lists gd_pack_adds already registered
}

void gd_pack_adds(KilnOwner owner, uint64_t seq_base, const gjson& doc, const std::string& source, ApplyResult* out) {
    (void)seq_base;
    std::lock_guard lock(store_mutex());
    g_adds_done = &doc;
    for (auto& [k, body] : doc.items()) {
        const uint32_t sec = section_of(k);
        if (!sec || !body.is_object() || !body.contains("add") || !greg(sec).available) continue;
        gd_pack_add(owner, sec, body["add"], source, out);
    }
}

void gd_pack_sections(KilnOwner owner, uint64_t seq_base, const gjson& doc, const std::string& source, ApplyResult* out) {
    std::lock_guard lock(store_mutex());
    auto seq = [&] { return seq_base ? seq_base + (++g_seq & 0xFFFFFFFFull) : next_code_seq(); };
    bool any = false;
    // paths are resolved in memory (list headers: added configs, event order): it must hold the effective layers, also
    // right after a re-application dropped the pack owners' layers
    for (auto& [k, body] : doc.items())
        if (section_of(k)) {
            std::string ferr;
            store_flush(&ferr);
            break;
        }
    for (auto& [k, body] : doc.items()) {
        const uint32_t sec = section_of(k);
        if (!sec) continue;
        any = true;
        GRegistry& R = greg(sec);
        if (!body.is_object()) {
            out->errors.push_back(k + ": must be an object { \"<selector>\": { \"<path>\": value } }");
            continue;
        }
        if (!R.available) {
            out->errors.push_back(k + ": not available on this build (" + std::string(R.qtype) + ")");
            continue;
        }
        for (auto& [sel, rule] : body.items()) {
            if (sel == "add") {
                if (g_adds_done != &doc) gd_pack_add(owner, sec, rule, source, out);
                continue;
            }
            if (!sel.empty() && sel[0] == '@') continue;
            if (!rule.is_object()) {
                out->errors.push_back(k + " " + sel + ": must be an object of paths");
                continue;
            }
            const std::vector<const GEntry*> hits = R.select(sel);
            if (hits.empty()) {
                out->warnings.push_back(k + " " + sel + ": matches nothing");
                continue;
            }
            const gjson* where = rule.contains("@where") ? &rule["@where"] : nullptr;
            // itemdata: a reference of a game item needs the opt-in, as in kiln.content's items section (vanilla
            // clients keep the game's reference); the content sync's own document carries it
            const bool refs_ok = rule.value("@refs", false) == true || owner_label(owner) == "server sync";
            uint32_t matched = 0;
            std::vector<std::string> errs;
            const std::string src = source + ": " + k + " " + sel;
            for (const GEntry* e : hits) {
                if (where) {
                    std::string werr;
                    if (!where_ok(sec, *e, *where, &werr)) {
                        if (!werr.empty()) {
                            out->errors.push_back(k + " " + sel + ": " + werr);
                            break;
                        }
                        continue;
                    }
                }
                ++matched;
                for (auto& [path, val] : rule.items()) {
                    if (path.empty() || path[0] == '@') continue;
                    if (sec == KILN_GD_ITEMS && !refs_ok && !nc_is_new(KILN_CONTENT_ITEMS, e->id)) {
                        std::vector<Resolved> rr;
                        std::string rerr;
                        Field lf;
                        if (resolve(R.root, root_of(sec, *e), path, &rr, &rerr) && !rr.empty() && rr[0].node.t && leaf_of(rr[0].node.t, &lf) && lf.ref) {
                            errs.push_back("itemdata " + e->name + "." + path + ": a reference of a game item; changing it needs \"@refs\": true in the rule");
                            continue;
                        }
                    }
                    out->applied += apply_value(owner, seq(), sec, *e, path, val, src, &errs);
                }
            }
            std::sort(errs.begin(), errs.end());
            errs.erase(std::unique(errs.begin(), errs.end()), errs.end());
            if (errs.size() > 20) {
                const size_t more = errs.size() - 20;
                errs.resize(20);
                errs.push_back("... and " + std::to_string(more) + " more");
            }
            for (auto& e : errs) out->errors.push_back(e);
            if (where && matched == 0) out->warnings.push_back(k + " " + sel + ": @where matches nothing");
        }
    }
    // other services' sections ("creatures", ...)
    for (auto& [k, body] : doc.items()) {
        if (section_of(k) || k == "items" || k == "recipes" || k == "loot" || k == "drops" || k == "options" || k == "format" ||
            k.empty() || k[0] == '@' || k == "comment")
            continue;
        Applied a{k, owner_text(owner), source, owner, body.dump()};
        const External* x = nullptr;
        {
            std::lock_guard l2(g_ext_mu);
            x = find_ext(k);
            if (!x) {  // kept: applied when a service registers the section later (managed services load after packs)
                bool known = false;
                for (const char* n : kKnownExternal) known = known || k == n;
                std::erase_if(g_stash, [&](const Applied& s) { return s.section == k && s.owner == a.owner && s.source == a.source; });
                if (g_stash.size() < 4096) g_stash.push_back(a);
                if (known) out->warnings.push_back("section \"" + k + "\" waits for its service (it applies when the service registers)");
                continue;
            }
        }
        call_apply(*x, a, generation(), g_in_packs, out);
        std::lock_guard l2(g_ext_mu);
        (g_in_packs ? g_ext_cur : g_ext_code).insert(a);
    }
    g_adds_done = nullptr;
    if (any) after_changes();
}

// ---- an item clone's own lists ----
// An item clone starts out sharing its lists with the base (the struct is copied, its offsets rebased: docs/re/content.md section 7). A change to an
// element of such a list (impactValues.ManaCost, uiValues[0].value, sequences[0].sequence) would change the game's item and every clone of it.
// So a clone gets its own copy of every list, recursively; strings stay shared, nothing writes to them.
namespace {
// Each header is read at dst: there it holds what src held, offset included, and for a game item what rules changed is already back to the game's.
void own_node(const TType* decl, uintptr_t src, uintptr_t dst, int depth, bool game, std::string* err) {
    const TType* t = decl ? decl->resolved() : nullptr;
    if (!t || depth > 12 || !err->empty()) return;
    const auto copy_near = [&](uintptr_t from, size_t bytes, uintptr_t near) -> uintptr_t {
        auto* mem = static_cast<uint8_t*>(near_alloc(near, bytes));
        if (!mem) *err = "no memory near the item";
        else if (!rd(from, mem, bytes)) *err = "the base item's list is not readable";
        else if (game) store_originals(from, bytes, mem);
        return *err == "" ? reinterpret_cast<uintptr_t>(mem) : 0;
    };
    const auto point = [&](uintptr_t field, uintptr_t relative_to, uintptr_t target) {
        const int64_t rel = static_cast<int64_t>(target) - static_cast<int64_t>(relative_to);
        if (rel < INT32_MIN || rel > INT32_MAX) {
            *err = "a list is out of reach";
            return;
        }
        const int32_t r32 = static_cast<int32_t>(rel);
        std::memcpy(reinterpret_cast<void*>(field), &r32, 4);
    };
    switch (t->kind) {
        case K_STRUCT:
            for (const TField& f : t->fields) own_node(f.type(), src + f.offset, dst + f.offset, depth + 1, game, err);
            return;
        case K_ARRAY: {
            const TType* el = t->inner();
            el = el ? el->resolved() : nullptr;
            if (!el || !el->size) return;
            const uint32_t n = t->count ? t->count : t->size / el->size;
            for (uint32_t i = 0; i < n && i < 4096; ++i)
                own_node(el, src + static_cast<uintptr_t>(i) * el->size, dst + static_cast<uintptr_t>(i) * el->size, depth + 1, game, err);
            return;
        }
        case K_BLOBSTRING: {
            int32_t rel = 0;
            if (src != dst && rdv(dst, rel) && rel) point(dst, dst, src + static_cast<intptr_t>(rel));
            return;
        }
        case K_BLOBARRAY: {
            int32_t rel = 0;
            uint32_t n = 0;
            if (!rdv(dst, rel) || !rdv(dst + 4, n) || !rel || !n) return;
            if (n > 100000) {
                *err = "a list of " + std::to_string(n) + " elements";
                return;
            }
            const TType* el = t->inner();
            el = el ? el->resolved() : nullptr;
            const uint32_t es = el && el->size ? el->size : 12;  // a variant's record has no size of its own
            const uintptr_t data = src + static_cast<intptr_t>(rel);
            const uintptr_t mem = copy_near(data, static_cast<size_t>(n) * es, dst);
            if (!mem) return;
            for (uint32_t i = 0; i < n; ++i)
                own_node(el, data + static_cast<uintptr_t>(i) * es, mem + static_cast<uintptr_t>(i) * es, depth + 1, game, err);
            point(dst, dst, mem);
            return;
        }
        case K_BLOBOPT: {
            int32_t rel = 0;
            if (!rdv(dst, rel) || !rel) return;
            const TType* el = t->inner();
            el = el ? el->resolved() : nullptr;
            if (!el || !el->size) return;
            const uintptr_t data = src + static_cast<intptr_t>(rel);
            const uintptr_t mem = copy_near(data, el->size, dst);
            if (!mem) return;
            own_node(el, data, mem, depth + 1, game, err);
            point(dst, dst, mem);
            return;
        }
        case K_BLOBVAR: {
            uint32_t h = 0, sz = 0;
            int32_t rel = 0;
            if (!rdv(dst, h) || !rdv(dst + 4, rel) || !rdv(dst + 8, sz) || !h || !sz || !rel) return;
            const uintptr_t data = src + 4 + static_cast<intptr_t>(rel);
            const uintptr_t mem = copy_near(data, sz, dst);
            if (!mem) return;
            if (const TType* vt = type_hash(h)) own_node(vt, data, mem, depth + 1, game, err);
            point(dst + 4, dst + 4, mem);
            return;
        }
        default:
            return;
    }
}
}  // namespace

void gd_item_registered(uint32_t id, const std::string& name, const uint8_t guid[16], uintptr_t addr) {
    GRegistry& I = greg(KILN_GD_ITEMS);
    if (I.entries.empty()) return;  // no list yet: refresh_items reads kiln.content's
    if (const auto it = I.by_id.find(id); it != I.by_id.end()) {
        I.entries[it->second].addr = addr;
        return;
    }
    GEntry g;
    g.id = id, g.name = name, g.addr = addr;
    std::memcpy(g.guid, guid, 16);
    I.add(std::move(g));
}

bool gd_own_lists(uintptr_t item, bool game_base, std::string* err) {
    std::lock_guard lock(store_mutex());
    const TType* t = type_named("keen::ItemInfo");
    if (!t) {
        *err = "keen::ItemInfo is not in the reflection yet";
        return false;
    }
    err->clear();
    own_node(t, item, item, 0, game_base, err);
    return err->empty();
}

int gd_item_path(KilnOwner owner, uint64_t seq, const Entry& e, const std::string& path, const gjson& val,
                 const std::string& source, std::string* err) {
    std::lock_guard lock(store_mutex());
    const GRegistry& I = greg(KILN_GD_ITEMS);
    if (!I.type) return 0;
    {
        std::string ferr;
        store_flush(&ferr);  // resolve against the effective layers (see gd_pack_sections)
    }
    GEntry g;
    g.id = e.id, g.name = e.name, g.addr = e.addr;
    std::memcpy(g.guid, e.guid, 16);
    std::vector<Resolved> rs;
    std::string rerr;
    bool missing = false;
    if (!resolve(Root::Plain, Node{I.type, e.addr}, path, &rs, &rerr, &missing) && missing) return 0;
    std::vector<std::string> errs;
    const uint32_t n = apply_value(owner, seq, KILN_GD_ITEMS, g, path, val, source, &errs);
    if (!errs.empty()) {
        *err = errs[0];
        for (size_t i = 1; i < errs.size() && i < 4; ++i) *err += "; " + errs[i];
        if (n == 0) return -1;
    }
    return n ? static_cast<int>(n) : -1;
}

uint32_t gd_export(gjson& doc, uint32_t kinds) {
    if (!(kinds & KILN_CONTENT_EXPORT_GAMEDATA)) return 0;
    std::lock_guard lock(store_mutex());
    uint32_t n = 0;
    // clone definitions first: the receiver registers them before it applies values to them
    for (uint32_t sec = KILN_GD_TEMPLATES; sec <= KILN_GD_SKILLS; ++sec) {
        gjson adds = clones_export(sec);
        if (!adds.empty()) doc[section_name(sec)]["add"] = std::move(adds);
    }
    store_for_each([&](uintptr_t addr, const Slot& s) {
        if (s.kind < GD_KIND_BASE || s.sub == kSubOrder) return;
        auto it = g_info.find(addr);
        if (it == g_info.end()) return;
        const SlotInfo& si = it->second;
        const uint32_t sec = s.kind - GD_KIND_BASE;
        const GEntry* e = greg(sec).get(s.id);
        if (std::memcmp(s.current, s.original, s.n) == 0) return;
        // what follows another field (Impact.programId after Impact.program): applying the field brings it, and a pack cannot set it itself
        if (s.sub < 0 && identity_field(last_name(si.path))) return;
        GEntry fallback;
        if (!e) {
            fallback.id = s.id, fallback.name = si.name;
            e = &fallback;
        }
        // item references go under "itemdata" (kiln.gamedata's paths on the receiver: a perk slot keeps its id with it);
        // kiln.content's "items" section would refuse a game item's reference without its own opt-in
        const bool item_ref = sec == KILN_GD_ITEMS && si.f.ref && s.sub != kSubAdd;
        gjson& body = doc[item_ref ? "itemdata" : section_name(sec)][entry_key(sec, *e)];
        if (!body.contains("@name")) body["@name"] = e->name;
        if (item_ref) body["@refs"] = true;
        if (s.sub == kSubUi) {  // a whole list of rows: the receiver makes the same list
            int32_t crel = 0;
            uint32_t cn = 0;
            std::memcpy(&crel, s.current, 4), std::memcpy(&cn, s.current + 4, 4);
            const TType* list_t = type_named(si.f.type_name);
            list_t = list_t ? list_t->resolved() : nullptr;
            const TType* ui_t = list_t ? list_t->inner() : nullptr;
            ui_t = ui_t ? ui_t->resolved() : nullptr;
            gjson rows = gjson::array();
            for (uint32_t i = 0; ui_t && i < cn; ++i) {
                const uintptr_t el = addr + static_cast<intptr_t>(crel) + static_cast<uintptr_t>(i) * ui_t->size;
                gjson row = gjson::object();
                if (ui_t->name != "keen::ImpactUiValue") {
                    row_leaves(ui_t, el, "", &row, 0);
                    rows.push_back(std::move(row));
                    continue;
                }
                for (const char* name : {"locaId", "valueFormat", "value", "isSigned"}) {
                    const TField* tf = ui_t->field(name);
                    Field lf;
                    uint8_t b[16] = {};
                    if (!tf || !leaf_of(tf->type(), &lf) || !rd(el + tf->offset + lf.offset, b, lf.bytes)) continue;
                    row[name] = leaf_json(lf, b, true);
                }
                rows.push_back(std::move(row));
            }
            body[si.path] = std::move(rows);
            ++n;
            return;
        }
        if (s.sub == kSubAdd) {  // added configs: path.<config> = value, for each element that is not the game's
            uint8_t ob[16];
            std::memcpy(ob, s.original, 8);
            int32_t orel = 0, crel = 0;
            uint32_t on = 0, cn = 0;
            std::memcpy(&orel, ob, 4), std::memcpy(&on, ob + 4, 4), std::memcpy(&crel, s.current, 4), std::memcpy(&cn, s.current + 4, 4);
            std::set<uintptr_t> game;
            for (uint32_t i = 0; i < on; ++i) {
                uint32_t h = 0, sz = 0;
                uintptr_t d = 0;
                if (blob_variant(addr + orel + i * 12, &h, &d, &sz)) game.insert(d);
            }
            const std::string prefix = si.path.substr(0, si.path.size() - std::strlen(si.array) - (si.path.size() > std::strlen(si.array) ? 1 : 0));
            for (uint32_t i = 0; i < cn; ++i) {
                uint32_t h = 0, sz = 0;
                uintptr_t d = 0;
                if (!blob_variant(addr + crel + i * 12, &h, &d, &sz) || game.count(d)) continue;
                const TType* vt = type_hash(h);
                vt = vt ? vt->resolved() : nullptr;
                const TField* idf = vt ? vt->field("configId") : nullptr;
                const TField* vf = vt ? vt->field("value") : nullptr;
                Field lf;
                uint32_t id = 0;
                uint8_t b[16] = {};
                if (!idf || !vf || !rdv(d + idf->offset, id) || !leaf_of(vf->type(), &lf) || !rd(d + vf->offset + lf.offset, b, lf.bytes)) continue;
                body[(prefix.empty() ? "" : prefix + ".") + config_name(id)] = leaf_json(lf, b, true);
                ++n;
            }
            return;
        }
        body[si.path] = leaf_json(si.f, s.current, true);
        ++n;
    });
    return n;
}

std::string gd_slot_what(const Slot& s, std::string* field_out) {
    std::lock_guard lock(store_mutex());
    const uint32_t sec = s.kind - GD_KIND_BASE;
    std::string name = std::to_string(s.id), path = slot_path(s.field);
    for (auto& [a, si] : g_info)
        if (si.section == sec && si.id == s.id) {
            name = si.name;
            break;
        }
    if (s.sub == kSubAdd) path += " (added configs)";
    *field_out = path;
    return std::string(section_name(sec)) + " " + name + "." + path;
}

std::string gd_layer_value(const Slot& s, const uint8_t* bytes) {
    std::lock_guard lock(store_mutex());
    if (s.sub == kSubAdd || s.sub == kSubOrder) {
        uint32_t n = 0;
        std::memcpy(&n, bytes + 4, 4);
        return "list of " + std::to_string(n);
    }
    for (auto& [a, si] : g_info)
        if (si.section == s.kind - GD_KIND_BASE && si.id == s.id && si.path == slot_path(s.field)) {
            const gjson j = leaf_json(si.f, bytes);
            return j.is_string() ? j.get<std::string>() : j.dump();
        }
    return "?";
}

namespace gd {

// ============================================================================================= @where ============

bool where_ok(uint32_t section, const GEntry& e, const gjson& w, std::string* err) {
    if (!w.is_object()) {
        *err = "@where must be an object";
        return false;
    }
    const GRegistry& R = greg(section);
    for (auto& [k, c] : w.items()) {
        if (k == "name") {
            if (!c.is_string() || lower(e.name).find(lower(c.get<std::string>())) == std::string::npos) {
                // glob semantics when wildcards are used
                const std::string p = c.is_string() ? c.get<std::string>() : "";
                if (p.find_first_of("*?") == std::string::npos) return false;
                std::vector<const GEntry*> v;
                bool hit = false;
                for (const GEntry* x : R.select(p)) hit = hit || x == &e;
                if (!hit) return false;
            }
            continue;
        }
        std::vector<Resolved> rs;
        std::string rerr;
        if (!resolve(R.root, root_of(section, e), k, &rs, &rerr) || rs.empty() || !rs[0].node.t) return false;  // absent: no match
        Field f;
        if (!leaf_of(rs[0].node.t, &f)) {
            *err = "@where " + k + ": not a single value";
            return false;
        }
        uint8_t ob[16];
        original_bytes(rs[0].node.addr + f.offset, f.bytes, ob);
        const KilnContentValue v = decode(f, ob);
        auto one = [&](const gjson& x) -> bool {
            const double a = as_double(v);
            if (x.is_boolean()) return (v.i != 0) == x.get<bool>();
            if (x.is_number()) return std::fabs(a - x.get<double>()) < 1e-6;
            if (!x.is_string()) return false;
            std::string s = x.get<std::string>();
            for (const char* op : {">=", "<=", "!=", ">", "<"}) {
                const size_t n = std::strlen(op);
                if (s.compare(0, n, op) == 0) {
                    char* end = nullptr;
                    const double b = std::strtod(s.c_str() + n, &end);
                    if (end == s.c_str() + n) break;
                    if (!std::strcmp(op, ">=")) return a >= b;
                    if (!std::strcmp(op, "<=")) return a <= b;
                    if (!std::strcmp(op, "!=")) return a != b;
                    if (!std::strcmp(op, ">")) return a > b;
                    return a < b;
                }
            }
            const bool neg = !s.empty() && s[0] == '!';
            if (neg) s = s.substr(1);
            if (f.vt == KILN_CV_GUID) {
                uint8_t g[16];
                return (guid_parse(s, g) && std::memcmp(g, v.guid, 16) == 0) != neg;
            }
            if (f.en)
                for (auto& [n, x2] : f.en->values)
                    if (lower(n) == lower(s))
                        return (f.enum_kind == KILN_CONTENT_FLAGS ? (((v.i >> x2) & 1) != 0) : v.i == x2) != neg;
            *err = "@where " + k + ": cannot use \"" + x.get<std::string>() + "\"";
            return false;
        };
        bool ok = false;
        if (c.is_array()) {
            for (const gjson& x : c) ok = ok || one(x);
        } else {
            ok = one(c);
        }
        if (!err->empty()) return false;
        if (!ok) return false;
    }
    return true;
}

void ext_world_reset() {
    std::vector<const External*> xs;
    {
        std::lock_guard lock(g_ext_mu);
        for (const External& x : g_ext) xs.push_back(&x);
        g_ext_prev.clear(), g_ext_cur.clear(), g_ext_code.clear(), g_stash.clear();
    }
    for (const External* x : xs) call_revert(*x, "", "");
}

void changes_world_reset() {
    g_added.clear();
    g_added_data.clear();
    g_ui_lists.clear();
    g_touched.clear();
    g_info.clear();
}

void changes_tick() {
    if (g_touched.empty()) return;
    std::lock_guard lock(store_mutex());
    reorder_all();
    std::string err;
    store_flush(&err);
}

void changes_boot() {
    if (!g_internal) {
        g_internal = g_api->owner_create(KilnStr{"gamedata:internal", 17});
        set_owner_label(g_internal, "gamedata (event order)");
    }
}

}  // namespace gd
}  // namespace kc
