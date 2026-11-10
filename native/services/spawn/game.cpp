// SPDX-License-Identifier: MIT
#include "game.h"

#include <windows.h>

#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>

#include "kiln_hook.hpp"

namespace kspawn {

bool Guid::zero() const {
    for (uint8_t v : b)
        if (v) return false;
    return true;
}
bool Guid::operator==(const Guid& o) const { return std::memcmp(b, o.b, 16) == 0; }
bool Guid::operator<(const Guid& o) const { return std::memcmp(b, o.b, 16) < 0; }

std::string Guid::text() const {
    // Windows layout: Data1 (u32 LE), Data2 (u16 LE), Data3 (u16 LE), then 8 bytes in order.
    char s[40];
    std::snprintf(s, sizeof s, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x", b[3], b[2], b[1],
                  b[0], b[5], b[4], b[7], b[6], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]);
    return s;
}

bool Guid::parse(std::string_view s, Guid* out) {
    uint8_t hex[16];
    int n = 0, half = -1;
    for (char c : s) {
        int v;
        if (c >= '0' && c <= '9') v = c - '0';
        else if (c >= 'a' && c <= 'f') v = c - 'a' + 10;
        else if (c >= 'A' && c <= 'F') v = c - 'A' + 10;
        else if (c == '-' || c == '{' || c == '}' || c == ' ') continue;
        else return false;
        if (n >= 16) return false;
        if (half < 0) half = v;
        else {
            hex[n++] = static_cast<uint8_t>(half << 4 | v);
            half = -1;
        }
    }
    if (n != 16 || half >= 0) return false;
    // text order -> Windows memory layout
    const int order[16] = {3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15};
    for (int i = 0; i < 16; ++i) out->b[i] = hex[order[i]];
    return true;
}

double fixed_to_m(int64_t v) { return static_cast<double>(v) / 4294967296.0; }
int64_t m_to_fixed(double m) { return std::llround(m * 4294967296.0); }

namespace game {
namespace {

using SpawnWrapperFn = uint32_t(__cdecl*)(uintptr_t world, const Guid*, const SpawnSlice*);
using DestroyEntityFn = uint8_t(__cdecl*)(uintptr_t storage, uint32_t id, uint8_t mode, uint8_t skip_culled);
using GetEntityTransformFn = WorldTransform*(__cdecl*)(uintptr_t world, uint32_t id);
struct ComponentRef {
    void* data;
    uint64_t size;
};
using GetComponentFn = ComponentRef*(__cdecl*)(ComponentRef* out, uintptr_t world, uint32_t hash, uint32_t id);

struct Offsets {
    int64_t storage = 0;  // world -> entity storage
    int64_t kind = 8;
    int64_t clock = 0, period = -1;
    int64_t entity_map = 0, entity_cap = 0, entity_records = 0;
    int64_t rec_id = 0x10, rec_parent = 0x14, rec_template = 0;
    int64_t tmap = 0, tmap_cap = 0, tmap_values = 0, tname = 0x10, tfamily = 0x20;
    int64_t cmap = -1, tcomps = 0x28;  // registry: hash -> dynamic type index map; template info: type bitset
};

// Generic engine hash map (keen HashMap: occupancy bitset ptr +0x08, capacity +0x10, keys ptr +0x28, values ptr
// +0x40, count +0x54). Seen in the lookups 0x650c50 (EntityId -> record) and 0x6510b0 (Guid -> template info).
constexpr int64_t kMapBits = 0x08, kMapKeys = 0x28, kMapCount = 0x54;

Offsets g_o;
SpawnWrapperFn g_spawn = nullptr;
DestroyEntityFn g_destroy = nullptr;
GetEntityTransformFn g_transform = nullptr;
GetComponentFn g_component = nullptr;
bool g_have_entities = false, g_have_templates = false;

// The server world: from the core (KilnApi.server_world, set at the first server-world step) or, with an older core
// that lacks it, from a capture-only hook chained in front of the core's SimStep hook.
uintptr_t(KILN_CALL* g_core_world)(void) = nullptr;
kiln::RawHook g_capture;
std::atomic<uintptr_t> g_captured{0};  // storage pointer seen by the fallback hook

uintptr_t __fastcall hk_sim_step(uintptr_t storage, uintptr_t ticked, uintptr_t r8, uintptr_t r9) {
    if (*reinterpret_cast<const uint8_t*>(storage + g_o.kind) == 0) g_captured.store(storage, std::memory_order_relaxed);
    return g_capture.call<uintptr_t>(storage, ticked, r8, r9);
}

uintptr_t world_ptr() {
    if (g_core_world) return g_core_world();
    const uintptr_t s = g_captured.load(std::memory_order_relaxed);
    return s ? s - static_cast<uintptr_t>(g_o.storage) : 0;
}

uintptr_t storage_ptr() {
    const uintptr_t w = world_ptr();
    return w ? w + static_cast<uintptr_t>(g_o.storage) : 0;
}

// ---- SEH-guarded raw calls (no C++ objects with destructors in these functions) ----

uint32_t seh_spawn(uintptr_t world, const Guid* g, const SpawnSlice* slice, unsigned long* code) {
    __try {
        return g_spawn(world, g, slice);
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

bool seh_destroy(uintptr_t storage, uint32_t id, unsigned long* code) {
    __try {
        return g_destroy(storage, id, 1, 0) != 0;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool seh_transform(uintptr_t world, uint32_t id, WorldTransform* out) {
    __try {
        const WorldTransform* t = g_transform(world, id);
        if (!t) return false;
        *out = *t;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool seh_component(uintptr_t world, uint32_t id, uint32_t hash, ComponentRef* out) {
    __try {
        ComponentRef r{nullptr, 0};
        g_component(&r, world, hash, id);
        *out = r;
        return r.data != nullptr;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

uint32_t entity_hash(uint32_t k) {  // 0x650c50
    uint32_t x = ((k >> 16) ^ k) * 0x45d9f3bu;
    x = ((x >> 16) ^ x) * 0x45d9f3bu;
    return (x >> 16) ^ x;
}

// Entity map lookup -> template info pointer (0 if no entity). *found = entity exists.
bool seh_entity_template(uintptr_t storage, uint32_t id, Guid* out, uint32_t* parent, bool* found) {
    __try {
        *found = false;
        const uintptr_t map = storage + static_cast<uintptr_t>(g_o.entity_map);
        const uint64_t cap = *reinterpret_cast<const uint64_t*>(storage + g_o.entity_cap);
        if (cap == 0 || (cap & (cap - 1)) != 0) return false;
        if (*reinterpret_cast<const uint32_t*>(map + kMapCount) == 0) return true;
        const uint64_t* bits = *reinterpret_cast<uint64_t* const*>(map + kMapBits);
        const uint32_t* keys = *reinterpret_cast<uint32_t* const*>(map + kMapKeys);
        const uintptr_t* recs = *reinterpret_cast<uintptr_t* const*>(storage + g_o.entity_records);
        uint64_t i = entity_hash(id) & (cap - 1);
        for (uint64_t steps = 0; steps < cap; ++steps) {
            if (!((bits[i >> 6] >> (i & 63)) & 1)) return true;  // empty slot: not present
            if (keys[i] == id) {
                const uintptr_t rec = recs[i];
                if (!rec || *reinterpret_cast<const uint32_t*>(rec + g_o.rec_id) != id) return false;
                *found = true;
                *parent = *reinterpret_cast<const uint32_t*>(rec + g_o.rec_parent);
                const uintptr_t ti = *reinterpret_cast<const uintptr_t*>(rec + g_o.rec_template);
                if (!ti) return false;
                for (int k = 0; k < 16; ++k) out->b[k] = reinterpret_cast<const uint8_t*>(ti)[k];
                return true;
            }
            i = i + 1 < cap ? i + 1 : 0;
        }
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *found = false;
        return false;
    }
}

struct EntityRaw {
    uint32_t id;
    uint32_t has;
    uint32_t parent;
    uint8_t guid[16];
};

// Walks the entity map. Returns entries written (<= cap_out); *total = occupied slots; *bad = key/record mismatches.
size_t seh_entities(uintptr_t storage, EntityRaw* out, size_t cap_out, size_t* total, size_t* bad, unsigned long* code) {
    __try {
        *total = 0;
        *bad = 0;
        const uintptr_t map = storage + static_cast<uintptr_t>(g_o.entity_map);
        const uint64_t cap = *reinterpret_cast<const uint64_t*>(storage + g_o.entity_cap);
        if (cap == 0 || (cap & (cap - 1)) != 0 || cap > (1ull << 26)) {
            *bad = 1;
            return 0;
        }
        const uint64_t* bits = *reinterpret_cast<uint64_t* const*>(map + kMapBits);
        const uint32_t* keys = *reinterpret_cast<uint32_t* const*>(map + kMapKeys);
        const uintptr_t* recs = *reinterpret_cast<uintptr_t* const*>(storage + g_o.entity_records);
        size_t n = 0;
        for (uint64_t i = 0; i < cap; ++i) {
            if ((i & 63) == 0 && bits[i >> 6] == 0) {  // skip empty words (the map has 2^20 slots)
                i += 63;
                continue;
            }
            if (!((bits[i >> 6] >> (i & 63)) & 1)) continue;
            const uintptr_t rec = recs[i];
            if (!rec) continue;
            ++*total;
            const uint32_t id = keys[i];
            if (*reinterpret_cast<const uint32_t*>(rec + g_o.rec_id) != id) {
                ++*bad;
                continue;
            }
            if (n >= cap_out) continue;
            out[n].id = id;
            out[n].parent = *reinterpret_cast<const uint32_t*>(rec + g_o.rec_parent);
            const uintptr_t ti = *reinterpret_cast<const uintptr_t*>(rec + g_o.rec_template);
            out[n].has = ti != 0;
            for (int k = 0; k < 16; ++k) out[n].guid[k] = ti ? reinterpret_cast<const uint8_t*>(ti)[k] : 0;
            ++n;
        }
        return n;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

struct TemplateRaw {
    uint8_t guid[16];
    uint8_t family;
    uint8_t name_len;  // copied length (<= 127)
    uint8_t bits;      // 1 DoNotSave, 2 LifeTime, 4 CurrentTransform, 0x80 bits valid
    char name[128];
};

// Dynamic component type index of `hash` (registry map at +0x88, identity hash; see 0x64dc40). -1 if unknown.
int seh_type_index(uintptr_t registry, uint32_t hash) {
    __try {
        if (g_o.cmap < 0) return -1;
        const uintptr_t map = registry + static_cast<uintptr_t>(g_o.cmap);
        const uint64_t cap = *reinterpret_cast<const uint64_t*>(map + 0x10);
        if (cap == 0 || (cap & (cap - 1)) != 0 || *reinterpret_cast<const uint32_t*>(map + kMapCount) == 0) return -1;
        const uint64_t* bits = *reinterpret_cast<uint64_t* const*>(map + kMapBits);
        const uint32_t* keys = *reinterpret_cast<uint32_t* const*>(map + kMapKeys);
        const uint16_t* vals = *reinterpret_cast<uint16_t* const*>(map + 0x40);
        uint64_t i = hash & (cap - 1);
        for (uint64_t steps = 0; steps < cap; ++steps) {
            if (!((bits[i >> 6] >> (i & 63)) & 1)) return -1;
            if (keys[i] == hash) return vals[i];
            i = i + 1 < cap ? i + 1 : 0;
        }
        return -1;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}

bool tbit(uintptr_t ti, int idx) {
    if (idx < 0 || idx >= 4096) return false;
    const uint64_t w = *reinterpret_cast<const uint64_t*>(ti + g_o.tcomps + (idx >> 6) * 8);
    return (w >> (idx & 63)) & 1;
}

size_t seh_templates(uintptr_t registry, TemplateRaw* out, size_t cap_out, size_t* total, size_t* bad,
                     const int* idx /* DoNotSave, LifeTime, CurrentTransform */, unsigned long* code) {
    __try {
        *total = 0;
        *bad = 0;
        const uintptr_t map = registry + static_cast<uintptr_t>(g_o.tmap);
        const uint64_t cap = *reinterpret_cast<const uint64_t*>(registry + g_o.tmap_cap);
        if (cap == 0 || (cap & (cap - 1)) != 0 || cap > (1ull << 24)) {
            *bad = 1;
            return 0;
        }
        const uint64_t* bits = *reinterpret_cast<uint64_t* const*>(map + kMapBits);
        const uint8_t* keys = *reinterpret_cast<uint8_t* const*>(map + kMapKeys);
        const uintptr_t* vals = *reinterpret_cast<uintptr_t* const*>(registry + g_o.tmap_values);
        size_t n = 0;
        for (uint64_t i = 0; i < cap; ++i) {
            if (!((bits[i >> 6] >> (i & 63)) & 1)) continue;
            const uintptr_t ti = vals[i];
            if (!ti) continue;
            ++*total;
            const uint8_t* key = keys + i * 16;
            const uint8_t* tg = reinterpret_cast<const uint8_t*>(ti);
            bool same = true;
            for (int k = 0; k < 16; ++k) same = same && key[k] == tg[k];
            if (!same) {
                ++*bad;
                continue;
            }
            if (n >= cap_out) continue;
            TemplateRaw& r = out[n++];
            for (int k = 0; k < 16; ++k) r.guid[k] = key[k];
            r.family = *reinterpret_cast<const uint8_t*>(ti + g_o.tfamily);
            r.bits = 0;
            if (idx[0] >= 0 && idx[2] >= 0) {
                r.bits = 0x80 | (tbit(ti, idx[0]) ? 1 : 0) | (tbit(ti, idx[1]) ? 2 : 0) | (tbit(ti, idx[2]) ? 4 : 0);
            }
            const char* nm = *reinterpret_cast<const char* const*>(ti + g_o.tname);
            const uint64_t len = *reinterpret_cast<const uint64_t*>(ti + g_o.tname + 8);
            size_t L = 0;
            if (nm && len < 4096) {
                while (L < len && L < 127 && nm[L]) {
                    r.name[L] = nm[L];
                    ++L;
                }
            }
            r.name[L] = 0;
            r.name_len = static_cast<uint8_t>(L);
        }
        return n;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

int64_t seh_period(uintptr_t storage) {
    __try {
        return g_o.period < 0 ? 0 : *reinterpret_cast<const int64_t*>(storage + g_o.period);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

int64_t seh_now(uintptr_t storage) {
    __try {
        if (g_o.period < 0) return 0;
        const int64_t period = *reinterpret_cast<const int64_t*>(storage + g_o.period);
        const uint32_t tick = *reinterpret_cast<const uint32_t*>(storage + g_o.clock);
        return tick ? period * static_cast<int64_t>(tick) : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

bool sym(const KilnApi* api, const char* name, uintptr_t* out) {
    return api->symbol(KilnStr{name, std::strlen(name)}, out) == KILN_OK && *out;
}
bool cst(const KilnApi* api, const char* name, int64_t* out) {
    return api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK;
}

}  // namespace

bool init(const KilnApi* api, KilnOwner owner, std::string& why) {
    uintptr_t sim = 0, spawn = 0, xform = 0, comp = 0, destroy = 0;
    if (!sym(api, "SimStep", &sim)) return why = "SimStep unresolved", false;
    if (!sym(api, "SpawnWrapper", &spawn)) return why = "SpawnWrapper unresolved", false;
    if (!sym(api, "GetEntityTransform", &xform)) return why = "GetEntityTransform unresolved", false;
    if (!cst(api, "world.storage", &g_o.storage) || g_o.storage <= 0) return why = "world.storage unresolved", false;
    cst(api, "storage.kind", &g_o.kind);
    if (!cst(api, "storage.clock", &g_o.clock)) g_o.clock = 0;
    if (!cst(api, "storage.clock_period", &g_o.period)) g_o.period = -1;
    g_spawn = reinterpret_cast<SpawnWrapperFn>(spawn);
    g_transform = reinterpret_cast<GetEntityTransformFn>(xform);
    if (sym(api, "GetComponent", &comp)) g_component = reinterpret_cast<GetComponentFn>(comp);
    if (sym(api, "spawn/DestroyEntity", &destroy)) g_destroy = reinterpret_cast<DestroyEntityFn>(destroy);

    g_have_entities = cst(api, "spawn/storage.entity_map", &g_o.entity_map) &&
                      cst(api, "spawn/storage.entity_map_cap", &g_o.entity_cap) &&
                      cst(api, "spawn/storage.entity_records", &g_o.entity_records) &&
                      cst(api, "spawn/record.template", &g_o.rec_template) && cst(api, "spawn/record.id", &g_o.rec_id) &&
                      g_o.entity_cap == g_o.entity_map + 0x10 && g_o.entity_records == g_o.entity_map + 0x40;
    g_have_templates = cst(api, "spawn/registry.template_map", &g_o.tmap) &&
                       cst(api, "spawn/registry.template_map_cap", &g_o.tmap_cap) &&
                       cst(api, "spawn/registry.template_map_values", &g_o.tmap_values) &&
                       cst(api, "spawn/template.name", &g_o.tname) && g_o.tmap_cap == g_o.tmap + 0x10 &&
                       g_o.tmap_values == g_o.tmap + 0x40;
    cst(api, "spawn/template.family", &g_o.tfamily);
    cst(api, "spawn/record.parent", &g_o.rec_parent);
    if (!cst(api, "spawn/registry.component_map", &g_o.cmap)) g_o.cmap = -1;
    cst(api, "spawn/template.components", &g_o.tcomps);

    if (api->size >= offsetof(KilnApi, server_world) + sizeof(api->server_world) && api->server_world) {
        g_core_world = api->server_world;
        return true;
    }
    // Runs before the core's own SimStep link, as the old layered hook did.
    if (!g_capture.install(api, owner, sim, reinterpret_cast<void*>(&hk_sim_step), "spawn/SimStep", 0, "kiln"))
        return why = "could not hook SimStep for the world pointer (old core without server_world)", false;
    return true;
}

bool can_destroy() { return g_destroy != nullptr; }
bool can_list_templates() { return g_have_templates; }
bool can_list_entities() { return g_have_entities; }
bool world_ready() { return world_ptr() != 0; }
bool world_from_core() { return g_core_world != nullptr; }

int64_t ecs_now() {
    const uintptr_t s = storage_ptr();
    return s ? seh_now(s) : 0;
}

int64_t tick_period() {
    const uintptr_t s = storage_ptr();
    return s ? seh_period(s) : 0;
}

uint32_t create(const Guid& tmpl, const SpawnComponent* comps, size_t count, unsigned long* fault) {
    *fault = 0;
    const uintptr_t w = world_ptr();
    if (!w) return 0;
    const SpawnSlice slice{comps, count};
    return seh_spawn(w, &tmpl, &slice, fault);
}

bool destroy(uint32_t id, unsigned long* fault) {
    *fault = 0;
    const uintptr_t s = storage_ptr();
    if (!s || !g_destroy || id == 0) return false;
    return seh_destroy(s, id, fault);
}

bool transform(uint32_t id, WorldTransform* out) {
    const uintptr_t w = world_ptr();
    return w && id && seh_transform(w, id, out);
}

bool component(uint32_t id, uint32_t hash, void** data, uint64_t* size) {
    const uintptr_t w = world_ptr();
    if (!w || !g_component || !id) return false;
    ComponentRef r{};
    if (!seh_component(w, id, hash, &r)) return false;
    *data = r.data;
    *size = r.size;
    return true;
}

bool entity_template(uint32_t id, Guid* out, uint32_t* parent) {
    const uintptr_t s = storage_ptr();
    if (!s || !g_have_entities || !id) return false;
    bool found = false;
    uint32_t p = 0;
    const bool ok = seh_entity_template(s, id, out, &p, &found) && found;
    if (parent) *parent = p;
    return ok;
}

bool templates(std::vector<TemplateRow>& out) {
    out.clear();
    const uintptr_t s = storage_ptr();
    if (!s || !g_have_templates) return false;
    const uintptr_t registry = *reinterpret_cast<const uintptr_t*>(s);  // storage[0]: component/template registry
    std::vector<TemplateRaw> raw(1024);
    size_t total = 0, bad = 0;
    unsigned long code = 0;
    const int idx[3] = {seh_type_index(registry, kDoNotSave), seh_type_index(registry, fnv1a("keen::ecs::LifeTime")),
                        seh_type_index(registry, kCurrentTransform)};
    for (int pass = 0; pass < 3; ++pass) {
        const size_t n = seh_templates(registry, raw.data(), raw.size(), &total, &bad, idx, &code);
        if (code || bad) return false;
        if (n == total) {
            raw.resize(n);
            break;
        }
        raw.resize(total + 64);
    }
    out.reserve(raw.size());
    for (const TemplateRaw& r : raw) {
        TemplateRow row;
        std::memcpy(row.guid.b, r.guid, 16);
        row.name.assign(r.name, r.name_len);
        row.family = r.family;
        row.flags_known = (r.bits & 0x80) != 0;
        row.do_not_save = (r.bits & 1) != 0;
        row.lifetime = (r.bits & 2) != 0;
        row.transform = (r.bits & 4) != 0;
        out.push_back(std::move(row));
    }
    return true;
}

bool entities(std::vector<EntityRow>& out) {
    out.clear();
    const uintptr_t s = storage_ptr();
    if (!s || !g_have_entities) return false;
    std::vector<EntityRaw> raw(32768);
    size_t total = 0, bad = 0;
    unsigned long code = 0;
    for (int pass = 0; pass < 3; ++pass) {
        const size_t n = seh_entities(s, raw.data(), raw.size(), &total, &bad, &code);
        if (code || bad) return false;
        if (n == total) {
            raw.resize(n);
            break;
        }
        raw.resize(total + 1024);
    }
    out.reserve(raw.size());
    for (const EntityRaw& r : raw) {
        EntityRow row;
        row.id = r.id;
        row.has_tmpl = r.has != 0;
        row.parent = r.parent;
        std::memcpy(row.tmpl.b, r.guid, 16);
        out.push_back(row);
    }
    return true;
}

namespace {
bool seh_map_header(uintptr_t s, uint64_t* cap, uint32_t* cnt) {
    __try {
        *cap = *reinterpret_cast<const uint64_t*>(s + g_o.entity_cap);
        *cnt = *reinterpret_cast<const uint32_t*>(s + g_o.entity_map + kMapCount);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
}  // namespace

std::string entity_map_stats() {
    const uintptr_t s = storage_ptr();
    if (!s || !g_have_entities) return "no world";
    uint64_t cap = 0;
    uint32_t cnt = 0;
    if (!seh_map_header(s, &cap, &cnt)) return "fault reading the map header";
    size_t total = 0, bad = 0;
    unsigned long code = 0;
    EntityRaw one{};
    seh_entities(s, &one, 1, &total, &bad, &code);
    char buf[160];
    std::snprintf(buf, sizeof buf, "cap %llu, count %u, occupied %zu, mismatched %zu, fault 0x%lx",
                  static_cast<unsigned long long>(cap), cnt, total, bad, code);
    return buf;
}

}  // namespace game
}  // namespace kspawn
