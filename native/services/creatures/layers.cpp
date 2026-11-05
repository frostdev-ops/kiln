// SPDX-License-Identifier: MIT
// kiln.creatures: per-entity values with per-owner layers, and private template copies.
//
// Values: every (entity, field, index) keeps the value it had before the first change (`original`) and a stack of
// owner layers; the top layer is applied, and when the last layer goes the original comes back. Feature masks are a
// union instead (two mods switching different features off both win). Enemy AI may toggle feature bits itself
// (arsenal Enable/DisableFeatureAction), so a release only clears the bits Kiln added.
//
// Private copies: the engine reads template components through an 8-byte per-entity slot in the entity's chunk
// (docs/re/creatures.md 1.4). Pointing it at a copy changes that component for that entity only. Slots move with the
// entity (chunk moves carry the pointer), so the slot address is looked up again every time, never cached. A copy is
// freed only after its slot no longer points at it and a delay has passed (no worker can hold it).
//
// Despawn: every Pre, each entity with state is checked by its engine record (template info pointer); when it is gone
// (or the id now names another entity) its state is dropped without touching the world.
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <map>
#include <malloc.h>

#include "internal.h"

namespace kcr {

namespace {

constexpr uint64_t kFreeDelaySteps = 250;  // ~10 s

struct Layer {
    KilnOwner owner;
    KilnCreatureValue v;
};

struct FieldState {
    KilnCreatureValue original{};
    std::vector<Layer> stack;
    float speed_orig[18] = {};
    uint32_t mask_added = 0;  // FEATURES_*: bits Kiln added to the game's mask
};

struct FieldOv {
    std::string path;
    FieldRef ref;
    std::vector<std::pair<KilnOwner, std::vector<uint8_t>>> stack;
};

struct Copy {
    uint32_t hash = 0;
    std::string component;
    TypeInfo type;
    void* original = nullptr;  // the shared template data the slot held
    uint8_t* data = nullptr;   // the private copy
    std::vector<FieldOv> fields;
    bool warned = false;
};

struct EntityState {
    KilnEntity e = 0;
    uintptr_t template_info = 0;
    std::map<uint32_t, FieldState> fields;  // key field << 8 | index
    std::vector<Copy> copies;
};

std::unordered_map<KilnEntity, EntityState> g_ents;
struct Pending {
    uint8_t* p;
    uint64_t at;
};
std::vector<Pending> g_free;
std::mutex g_release_mu;
std::vector<KilnOwner> g_release;
std::mutex g_mark_mu;
struct MarkReq {
    uint32_t comps = 0;
    uint64_t step = 0;
};
std::unordered_map<KilnEntity, MarkReq> g_marks;
std::atomic<bool> g_marks_pending{false};

// DynamicEnemy01BehaviorState offsets resolved by reflection (home position, guard entity).
int64_t g_off_home = -1, g_off_guard = -1;
bool g_dyn_resolved = false;

uint32_t key(uint32_t field, uint32_t index) { return field << 8 | (index & 0xFF); }

void resolve_dyn() {
    if (g_dyn_resolved) return;
    g_dyn_resolved = true;
    TypeInfo t;
    std::string err;
    FieldRef f;
    if (!type_info("keen::ecs::DynamicEnemy01BehaviorState", &t, &err)) return;
    if (resolve_path(t, "homeZonePosition.x", &f, &err) && f.kind == VKind::Float) g_off_home = f.offset;
    if (resolve_path(t, "guardEntityId", &f, &err)) g_off_guard = f.offset;
}

template <class T>
bool rdc(const uint8_t* comp, uint32_t size, uint32_t off, T* out) {
    if (!comp || off + sizeof(T) > size) return false;
    return seh_read(comp + off, out, sizeof(T));
}
template <class T>
bool wrc(uint8_t* comp, uint32_t size, uint32_t off, const T& v) {
    if (!comp || off + sizeof(T) > size) return false;
    return seh_write(comp + off, &v, sizeof(T));
}

// Movement speed slots (dataStorage index): sneak, walk, run, sprint, combat, custom, patrol, flee, swim/dive speeds.
constexpr int kSpeedSlots[] = {2, 3, 4, 5, 6, 7, 8, 9, 12, 13, 14, 15};

// Reads the current game value of a field. false = the entity lacks the component.
bool read_field(KilnEntity e, uint32_t field, uint32_t index, KilnCreatureValue* v, FieldState* fs = nullptr) {
    *v = KilnCreatureValue{};
    uint32_t n = 0;
    switch (field) {
        case KILN_CF_LEVEL: {
            auto* c = static_cast<uint8_t*>(component(e, H::Level, &n));
            uint32_t lv = 0;
            if (!rdc(c, n, 8, &lv)) return false;
            v->number = lv;
            return true;
        }
        case KILN_CF_FACTION: {
            auto* c = static_cast<uint8_t*>(component(e, H::Faction, &n));
            uint8_t f = 0;
            if (!rdc(c, n, 0, &f)) return false;
            v->number = f;
            return true;
        }
        case KILN_CF_SCALE: {
            auto* c = static_cast<uint8_t*>(component(e, H::Scale, &n));
            float s = 0;
            if (rdc(c, n, 0, &s)) {
                v->number = s;
                return true;
            }
            int64_t* t = nullptr;
            float ts = 0;
            if (!seh_transform(world(), e, &t) || !seh_read(reinterpret_cast<uint8_t*>(t) + 0x28, &ts, 4)) return false;
            v->number = ts;
            return true;
        }
        case KILN_CF_XP: {
            auto* c = static_cast<uint8_t*>(component(e, H::Xp, &n));
            float x = 0;
            if (!rdc(c, n, 4, &x)) return false;
            v->number = x;
            return true;
        }
        case KILN_CF_LOOT_LABELS: {
            auto* c = static_cast<uint8_t*>(component(e, H::Loot, &n));
            return c && n >= 20 && seh_read(c + 4, v->guid.b, 16);
        }
        case KILN_CF_LOOT_SLOTS: {
            auto* c = static_cast<uint8_t*>(component(e, H::LootSlots, &n));
            uint8_t s[2];
            if (!c || n < 2 || !seh_read(c, s, 2)) return false;
            v->vec = KilnVec3{static_cast<double>(s[0]), static_cast<double>(s[1]), 0};
            return true;
        }
        case KILN_CF_LOOT_DISABLED: {
            auto* c = static_cast<uint8_t*>(component(e, H::Loot, &n));
            uint8_t b = 0;
            if (!rdc(c, n, 0, &b)) return false;
            v->number = b ? 1 : 0;
            return true;
        }
        case KILN_CF_RESIST: {
            auto* c = static_cast<uint8_t*>(component(e, H::Resist, &n));
            float x = 0;
            if (index >= 19 || !rdc(c, n, 8 + 4 * index, &x)) return false;
            v->number = x;
            return true;
        }
        case KILN_CF_SPEED: {
            auto* c = static_cast<uint8_t*>(component(e, H::Movement, &n));
            float walk = 0;
            if (!rdc(c, n, 8 + 4 * 3, &walk)) return false;
            // factor against the originals when Kiln changed it, else 1
            v->number = fs && fs->speed_orig[3] > 0 ? walk / fs->speed_orig[3] : 1.0;
            return true;
        }
        case KILN_CF_DAMAGE: {
            auto* c = static_cast<uint8_t*>(component(e, H::Damage, &n));
            float m = 0;
            if (!rdc(c, n, 8 + 4 * 2, &m)) return false;
            v->number = m;
            return true;
        }
        case KILN_CF_FEATURES_ON:
        case KILN_CF_FEATURES_OFF: {
            auto* c = static_cast<uint8_t*>(component(e, H::Dyn, &n));
            uint32_t m = 0;
            const int64_t off = field == KILN_CF_FEATURES_ON ? L.dyn_enabled : L.dyn_disabled;
            if (off < 0 || !rdc(c, n, static_cast<uint32_t>(off), &m)) return false;
            v->number = m;
            return true;
        }
        case KILN_CF_HOME: {
            resolve_dyn();
            auto* c = static_cast<uint8_t*>(component(e, H::Dyn, &n));
            float p[3];
            if (g_off_home < 0 || !c || static_cast<uint32_t>(g_off_home) + 12 > n || !seh_read(c + g_off_home, p, 12))
                return false;
            v->vec = KilnVec3{p[0], p[1], p[2]};
            return true;
        }
        default: return false;
    }
}

// Writes a value into the game's component; requests replication marks.
bool write_field(KilnEntity e, uint32_t field, uint32_t index, const KilnCreatureValue& v, FieldState* fs) {
    uint32_t n = 0;
    switch (field) {
        case KILN_CF_LEVEL: {
            auto* c = static_cast<uint8_t*>(component(e, H::Level, &n));
            uint32_t mx = 0;
            if (!rdc(c, n, 12, &mx)) return false;
            uint32_t lv = static_cast<uint32_t>(std::llround(v.number));
            if (lv < 1) lv = 1;
            if (mx && lv > mx) lv = mx;
            if (!wrc(c, n, 8, lv)) return false;
            // force the next enemy_scaling update (health and damage follow the level): docs/re/creatures.md 1.5
            uint32_t sn = 0;
            auto* sc = static_cast<uint8_t*>(component(e, H::Scaling, &sn));
            if (sc) wrc(sc, sn, 0, int64_t{0});
            mark_request(e, MK_LEVEL);
            return true;
        }
        case KILN_CF_FACTION: {
            auto* c = static_cast<uint8_t*>(component(e, H::Faction, &n));
            const double f = v.number;
            if (!(f >= 0 && f <= 15)) return false;
            if (!wrc(c, n, 0, static_cast<uint8_t>(f))) return false;
            mark_request(e, MK_FACTION);
            return true;
        }
        case KILN_CF_SCALE: {
            const float s = static_cast<float>(v.number);
            if (!(s > 0.01f && s < 100.f)) return false;
            bool any = false;
            auto* c = static_cast<uint8_t*>(component(e, H::Scale, &n));
            if (c) any |= wrc(c, n, 0, s);
            int64_t* t = nullptr;
            if (seh_transform(world(), e, &t)) {
                const float sc[3] = {s, s, s};
                any |= seh_write(reinterpret_cast<uint8_t*>(t) + 0x28, sc, 12);
            }
            if (any) mark_request(e, MK_SCALE | MK_TRANSFORM);
            return any;
        }
        case KILN_CF_XP: {
            auto* c = static_cast<uint8_t*>(component(e, H::Xp, &n));
            if (!wrc(c, n, 4, static_cast<float>(v.number))) return false;
            mark_request(e, MK_XP);
            return true;
        }
        case KILN_CF_LOOT_LABELS: {
            auto* c = static_cast<uint8_t*>(component(e, H::Loot, &n));
            if (!c || n < 20 || !seh_write(c + 4, v.guid.b, 16)) return false;
            mark_request(e, MK_LOOT);
            return true;
        }
        case KILN_CF_LOOT_SLOTS: {
            auto* c = static_cast<uint8_t*>(component(e, H::LootSlots, &n));
            const double a = v.vec.x, b = v.vec.y;
            if (!(a >= 0 && b >= a && b <= 255)) return false;
            const uint8_t s[2] = {static_cast<uint8_t>(a), static_cast<uint8_t>(b)};
            if (!c || n < 2 || !seh_write(c, s, 2)) return false;
            mark_request(e, MK_LOOT_SLOTS);
            return true;
        }
        case KILN_CF_LOOT_DISABLED: {
            auto* c = static_cast<uint8_t*>(component(e, H::Loot, &n));
            if (!wrc(c, n, 0, static_cast<uint8_t>(v.number != 0 ? 1 : 0))) return false;
            mark_request(e, MK_LOOT);
            return true;
        }
        case KILN_CF_RESIST: {
            auto* c = static_cast<uint8_t*>(component(e, H::Resist, &n));
            const float x = static_cast<float>(v.number);
            if (index >= 19 || !(x >= 0 && x <= 100) || !wrc(c, n, 8 + 4 * index, x)) return false;
            mark_request(e, MK_RESIST);
            return true;
        }
        case KILN_CF_SPEED: {
            auto* c = static_cast<uint8_t*>(component(e, H::Movement, &n));
            const double f = v.number;
            if (!c || n < 8 + 4 * 16 || !(f >= 0 && f <= 20) || !fs) return false;
            for (int s : kSpeedSlots) wrc(c, n, 8 + 4 * s, static_cast<float>(fs->speed_orig[s] * f));
            mark_request(e, MK_MOVEMENT);
            return true;
        }
        case KILN_CF_DAMAGE: {
            auto* c = static_cast<uint8_t*>(component(e, H::Damage, &n));
            float base = 0, add = 0;
            const float m = static_cast<float>(v.number);
            if (!(m >= 0 && m <= 1000) || !rdc(c, n, 8 + 4, &base) || !rdc(c, n, 8 + 12, &add)) return false;
            wrc(c, n, 8 + 8, m);
            wrc(c, n, 8, base * m + add);  // DamageMod = base * multiplier + adder (the attribute's own formula)
            mark_request(e, MK_DAMAGE);
            return true;
        }
        case KILN_CF_FEATURES_ON:
        case KILN_CF_FEATURES_OFF: {
            auto* c = static_cast<uint8_t*>(component(e, H::Dyn, &n));
            const int64_t off = field == KILN_CF_FEATURES_ON ? L.dyn_enabled : L.dyn_disabled;
            uint32_t cur = 0;
            if (off < 0 || !rdc(c, n, static_cast<uint32_t>(off), &cur)) return false;
            const uint32_t want = static_cast<uint32_t>(static_cast<int64_t>(v.number));
            // keep what the game set since, minus the bits only Kiln had added, plus the wanted union
            const uint32_t base = cur & ~(fs ? fs->mask_added : 0);
            const uint32_t added = want & ~base;
            if (!wrc(c, n, static_cast<uint32_t>(off), base | want)) return false;
            if (fs) fs->mask_added = added;
            mark_request(e, MK_AI);
            return true;
        }
        case KILN_CF_HOME: {
            resolve_dyn();
            auto* c = static_cast<uint8_t*>(component(e, H::Dyn, &n));
            const float p[3] = {static_cast<float>(v.vec.x), static_cast<float>(v.vec.y), static_cast<float>(v.vec.z)};
            if (g_off_home < 0 || !c || static_cast<uint32_t>(g_off_home) + 12 > n || !std::isfinite(p[0]) ||
                !std::isfinite(p[1]) || !std::isfinite(p[2]) || !seh_write(c + g_off_home, p, 12))
                return false;
            mark_request(e, MK_AI);
            return true;
        }
        default: return false;
    }
}

bool is_mask(uint32_t field) { return field == KILN_CF_FEATURES_ON || field == KILN_CF_FEATURES_OFF; }

// Applies the effective value of one field state (top layer, mask union, or the original).
void apply(KilnEntity e, uint32_t field, uint32_t index, FieldState& fs) {
    if (is_mask(field)) {
        uint32_t u = 0;
        for (auto& l : fs.stack) u |= static_cast<uint32_t>(static_cast<int64_t>(l.v.number));
        KilnCreatureValue v{};
        v.number = u;
        write_field(e, field, index, v, &fs);
        return;
    }
    if (field == KILN_CF_SPEED) {
        KilnCreatureValue v{};
        v.number = fs.stack.empty() ? 1.0 : fs.stack.back().v.number;
        write_field(e, field, index, v, &fs);
        return;
    }
    write_field(e, field, index, fs.stack.empty() ? fs.original : fs.stack.back().v, &fs);
}

bool alive(EntityState& st, uintptr_t w) {
    uintptr_t ti = 0;
    return seh_record_template(w, st.e, &ti) && ti && ti == st.template_info;
}

EntityState* state_of(KilnEntity e, bool create) {
    auto it = g_ents.find(e);
    if (it != g_ents.end()) {
        if (alive(it->second, world())) return &it->second;
        // stale (despawned and the id reused before the Pre check): drop it
        for (auto& c : it->second.copies) g_free.push_back({c.data, g_step + kFreeDelaySteps});
        g_ents.erase(it);
    }
    if (!create) return nullptr;
    uintptr_t ti = 0;
    if (!seh_record_template(world(), e, &ti) || !ti) return nullptr;
    EntityState s;
    s.e = e;
    s.template_info = ti;
    return &g_ents.emplace(e, std::move(s)).first->second;
}

void drop_if_empty(KilnEntity e) {
    auto it = g_ents.find(e);
    if (it != g_ents.end() && it->second.fields.empty() && it->second.copies.empty()) g_ents.erase(it);
}

// ---- private copies ----

std::string split_component(std::string_view component, std::string_view path, std::string* comp_out) {
    if (!component.empty()) {
        *comp_out = std::string(component);
        return std::string(path);
    }
    const size_t dot = path.find('.');
    if (dot == std::string_view::npos) return {};
    *comp_out = std::string(path.substr(0, dot));
    return std::string(path.substr(dot + 1));
}

void** slot_of(KilnEntity e, uint32_t hash) {
    void** s = nullptr;
    if (!seh_template_slot(world(), hash, e, &s)) return nullptr;
    return s;
}

void write_override(Copy& c, FieldOv& f) {
    if (f.stack.empty()) {
        // back to the shared template's current value
        seh_read(static_cast<uint8_t*>(c.original) + f.ref.offset, c.data + f.ref.offset, f.ref.bytes);
    } else {
        std::memcpy(c.data + f.ref.offset, f.stack.back().second.data(), f.ref.bytes);
    }
}

// Returns the copy's slot to the shared data (when it still points at the copy) and queues the copy for freeing.
void retire_copy(KilnEntity e, Copy& c, bool entity_alive) {
    if (entity_alive) {
        void** s = slot_of(e, c.hash);
        void* cur = nullptr;
        if (s && seh_read(s, &cur, 8) && cur == c.data) seh_write(s, &c.original, 8);
        mark_request(e, MK_AI);
    }
    g_free.push_back({c.data, g_step + kFreeDelaySteps});
    c.data = nullptr;
}

}  // namespace

// ---------------------------------------------------------------------------------------------- values ----

KilnResult layer_set(KilnOwner owner, KilnEntity e, uint32_t field, uint32_t index, const KilnCreatureValue* v) {
    if (!v || field == 0 || field >= KILN_CF_COUNT || (field != KILN_CF_RESIST && index != 0) || index >= 19)
        return KILN_ERR_INVALID_ARGUMENT;
    KilnCreatureValue cur{};
    if (!read_field(e, field, index, &cur)) return entity_exists(e) ? KILN_ERR_UNAVAILABLE : KILN_ERR_NOT_FOUND;
    if (owner == 0) {  // direct write, no layer
        FieldState tmp;
        if (field == KILN_CF_SPEED) {
            uint32_t n = 0;
            auto* c = static_cast<uint8_t*>(component(e, H::Movement, &n));
            for (int s : kSpeedSlots) rdc(c, n, 8 + 4 * s, &tmp.speed_orig[s]);
        }
        return write_field(e, field, index, *v, &tmp) ? KILN_OK : KILN_ERR_INVALID_ARGUMENT;
    }
    EntityState* st = state_of(e, true);
    if (!st) return KILN_ERR_NOT_FOUND;
    const bool fresh = !st->fields.count(key(field, index));
    FieldState& fs = st->fields[key(field, index)];
    if (fresh) {
        fs.original = cur;
        if (field == KILN_CF_SPEED) {
            uint32_t n = 0;
            auto* c = static_cast<uint8_t*>(component(e, H::Movement, &n));
            for (int s : kSpeedSlots) rdc(c, n, 8 + 4 * s, &fs.speed_orig[s]);
        }
    }
    auto it = std::find_if(fs.stack.begin(), fs.stack.end(), [&](const Layer& l) { return l.owner == owner; });
    if (it != fs.stack.end()) fs.stack.erase(it);
    fs.stack.push_back({owner, *v});
    // validate by writing; on refusal drop the layer again
    bool ok;
    if (is_mask(field) || field == KILN_CF_SPEED) {
        apply(e, field, index, fs);
        ok = true;
        if (field == KILN_CF_SPEED && !(v->number >= 0 && v->number <= 20)) ok = false;
    } else {
        ok = write_field(e, field, index, *v, &fs);
    }
    if (!ok) {
        fs.stack.pop_back();
        apply(e, field, index, fs);
        if (fs.stack.empty()) st->fields.erase(key(field, index));
        drop_if_empty(e);
        return KILN_ERR_INVALID_ARGUMENT;
    }
    return KILN_OK;
}

KilnResult layer_get(KilnEntity e, uint32_t field, uint32_t index, KilnCreatureValue* out) {
    if (!out || field == 0 || field >= KILN_CF_COUNT) return KILN_ERR_INVALID_ARGUMENT;
    EntityState* st = state_of(e, false);
    FieldState* fs = nullptr;
    if (st) {
        auto it = st->fields.find(key(field, index));
        if (it != st->fields.end()) fs = &it->second;
    }
    if (!read_field(e, field, index, out, fs)) return entity_exists(e) ? KILN_ERR_UNAVAILABLE : KILN_ERR_NOT_FOUND;
    return KILN_OK;
}

namespace {
uint32_t reset_in(EntityState& st, KilnOwner owner, uint32_t field, uint32_t index, bool entity_alive) {
    uint32_t n = 0;
    for (auto it = st.fields.begin(); it != st.fields.end();) {
        const uint32_t f = it->first >> 8, idx = it->first & 0xFF;
        if (field && (f != field || (field == KILN_CF_RESIST && idx != index))) {
            ++it;
            continue;
        }
        auto& stk = it->second.stack;
        const size_t before = stk.size();
        if (owner) stk.erase(std::remove_if(stk.begin(), stk.end(), [&](const Layer& l) { return l.owner == owner; }), stk.end());
        else stk.clear();
        n += static_cast<uint32_t>(before - stk.size());
        if (before != stk.size() && entity_alive) apply(st.e, f, idx, it->second);
        if (stk.empty()) it = st.fields.erase(it);
        else ++it;
    }
    return n;
}

uint32_t copy_reset_in(EntityState& st, KilnOwner owner, std::string_view comp, std::string_view path, bool entity_alive) {
    uint32_t n = 0;
    for (auto ci = st.copies.begin(); ci != st.copies.end();) {
        Copy& c = *ci;
        if (!comp.empty() && _stricmp(c.component.c_str(), std::string(comp).c_str()) != 0) {
            ++ci;
            continue;
        }
        for (auto fi = c.fields.begin(); fi != c.fields.end();) {
            if (!path.empty() && _stricmp(fi->path.c_str(), std::string(path).c_str()) != 0) {
                ++fi;
                continue;
            }
            const size_t before = fi->stack.size();
            if (owner) fi->stack.erase(std::remove_if(fi->stack.begin(), fi->stack.end(), [&](auto& l) { return l.first == owner; }), fi->stack.end());
            else fi->stack.clear();
            n += static_cast<uint32_t>(before - fi->stack.size());
            if (before != fi->stack.size() && c.data) write_override(c, *fi);
            if (fi->stack.empty()) fi = c.fields.erase(fi);
            else ++fi;
        }
        if (c.fields.empty()) {
            retire_copy(st.e, c, entity_alive);
            ci = st.copies.erase(ci);
        } else {
            ++ci;
        }
    }
    return n;
}
}  // namespace

KilnResult layer_reset(KilnOwner owner, KilnEntity e, uint32_t field, uint32_t index, uint32_t* out_count) {
    uint32_t n = 0;
    if (e == 0) {
        for (auto& [id, st] : g_ents) n += reset_in(st, owner, field, index, alive(st, world()));
        for (auto it = g_ents.begin(); it != g_ents.end();)
            it = it->second.fields.empty() && it->second.copies.empty() ? g_ents.erase(it) : std::next(it);
    } else if (EntityState* st = state_of(e, false)) {
        n = reset_in(*st, owner, field, index, true);
        drop_if_empty(e);
    }
    if (out_count) *out_count = n;
    return KILN_OK;
}

// ---------------------------------------------------------------------------------------------- copies ----

KilnResult copy_set(KilnOwner owner, KilnEntity e, std::string_view component_in, std::string_view path_in,
                    std::string_view value) {
    if (!F.slots || !F.reflection) return KILN_ERR_UNAVAILABLE;
    std::string comp;
    const std::string path = split_component(component_in, path_in, &comp);
    if (comp.empty() || path.empty()) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t hash = ecs_hash(comp);
    TypeInfo type;
    std::string err;
    if (!type_info(component_qname(comp), &type, &err)) {
        KC_W("ai_set %s.%s on %u: %s", comp.c_str(), path.c_str(), e, err.c_str());
        return KILN_ERR_NOT_FOUND;
    }
    FieldRef ref;
    if (!resolve_path(type, path, &ref, &err)) {
        KC_W("ai_set %s.%s on %u: %s", comp.c_str(), path.c_str(), e, err.c_str());
        return KILN_ERR_INVALID_ARGUMENT;
    }
    void** slot = slot_of(e, hash);
    void* cur = nullptr;
    if (!slot || !seh_read(slot, &cur, 8) || !cur) return entity_exists(e) ? KILN_ERR_UNAVAILABLE : KILN_ERR_NOT_FOUND;
    EntityState* st = state_of(e, owner != 0);
    if (owner == 0) {
        // no layer: write the value into the entity's private copy if it has one, else refuse (would edit the template)
        if (!st) return KILN_ERR_INVALID_ARGUMENT;
        auto ci = std::find_if(st->copies.begin(), st->copies.end(), [&](const Copy& c) { return c.hash == hash; });
        if (ci == st->copies.end()) return KILN_ERR_INVALID_ARGUMENT;
        return parse_value(ref, value, ci->data + ref.offset, &err) ? KILN_OK : KILN_ERR_INVALID_ARGUMENT;
    }
    if (!st) return KILN_ERR_NOT_FOUND;
    auto ci = std::find_if(st->copies.begin(), st->copies.end(), [&](const Copy& c) { return c.hash == hash; });
    // parse first, against the bytes the entity reads now (relative flag edits start from there)
    std::vector<uint8_t> bytes(ref.bytes);
    if (!seh_read(static_cast<uint8_t*>(ci != st->copies.end() ? ci->data : cur) + ref.offset, bytes.data(), ref.bytes))
        return KILN_ERR_INTERNAL;
    if (!parse_value(ref, value, bytes.data(), &err)) {
        KC_W("ai_set %s.%s = '%.*s' on %u: %s", comp.c_str(), path.c_str(), static_cast<int>(value.size()),
             value.data(), e, err.c_str());
        drop_if_empty(e);
        return KILN_ERR_INVALID_ARGUMENT;
    }
    if (ci == st->copies.end()) {
        Copy c;
        c.hash = hash;
        c.component = comp;
        c.type = type;
        c.original = cur;
        c.data = static_cast<uint8_t*>(_aligned_malloc(type.size ? type.size : 16, 16));
        if (!c.data) return KILN_ERR_INTERNAL;
        if (!seh_read(cur, c.data, type.size)) {
            _aligned_free(c.data);
            return KILN_ERR_INTERNAL;
        }
        if (!seh_write(slot, &c.data, 8)) {
            _aligned_free(c.data);
            return KILN_ERR_INTERNAL;
        }
        st->copies.push_back(std::move(c));
        ci = st->copies.end() - 1;
        KC_D("entity %u reads a private %s copy now (%u bytes)", e, comp.c_str(), type.size);
    }
    auto fi = std::find_if(ci->fields.begin(), ci->fields.end(),
                           [&](const FieldOv& f) { return _stricmp(f.path.c_str(), path.c_str()) == 0; });
    if (fi == ci->fields.end()) {
        ci->fields.push_back(FieldOv{path, ref, {}});
        fi = ci->fields.end() - 1;
    }
    auto li = std::find_if(fi->stack.begin(), fi->stack.end(), [&](auto& l) { return l.first == owner; });
    if (li != fi->stack.end()) fi->stack.erase(li);
    fi->stack.emplace_back(owner, std::move(bytes));
    write_override(*ci, *fi);
    mark_request(e, MK_AI);
    return KILN_OK;
}

KilnResult copy_get(KilnEntity e, std::string_view component_in, std::string_view path_in, std::string* out,
                    uint32_t* is_private) {
    if (is_private) *is_private = 0;
    if (!F.reflection) return KILN_ERR_UNAVAILABLE;
    std::string comp;
    const std::string path = split_component(component_in, path_in, &comp);
    if (comp.empty() || path.empty()) return KILN_ERR_INVALID_ARGUMENT;
    TypeInfo type;
    std::string err;
    FieldRef ref;
    if (!type_info(component_qname(comp), &type, &err)) return KILN_ERR_NOT_FOUND;
    if (!resolve_path(type, path, &ref, &err)) return KILN_ERR_INVALID_ARGUMENT;
    void* data = nullptr;
    if (!seh_template_component(world(), ecs_hash(comp), e, &data) || !data)
        return entity_exists(e) ? KILN_ERR_UNAVAILABLE : KILN_ERR_NOT_FOUND;
    uint8_t buf[16];
    if (!seh_read(static_cast<uint8_t*>(data) + ref.offset, buf, ref.bytes)) return KILN_ERR_INTERNAL;
    *out = format_value(ref, buf);
    if (is_private) {
        if (EntityState* st = state_of(e, false))
            for (auto& c : st->copies)
                if (c.hash == ecs_hash(comp) && c.data == data) *is_private = 1;
    }
    return KILN_OK;
}

KilnResult copy_reset(KilnOwner owner, KilnEntity e, std::string_view comp_in, std::string_view path_in,
                      uint32_t* out_count) {
    std::string comp;
    std::string path;
    if (!comp_in.empty() || !path_in.empty()) {
        path = split_component(comp_in, path_in, &comp);
        if (comp.empty()) comp = std::string(comp_in.empty() ? path_in : comp_in);  // component only
    }
    uint32_t n = 0;
    if (e == 0) {
        const uintptr_t w = world();
        for (auto& [id, st] : g_ents) n += copy_reset_in(st, owner, comp, path, alive(st, w));
        for (auto it = g_ents.begin(); it != g_ents.end();)
            it = it->second.fields.empty() && it->second.copies.empty() ? g_ents.erase(it) : std::next(it);
    } else if (EntityState* st = state_of(e, false)) {
        n = copy_reset_in(*st, owner, comp, path, true);
        drop_if_empty(e);
    }
    if (out_count) *out_count = n;
    return KILN_OK;
}

KilnResult copy_refresh(KilnEntity e, uint32_t* out_count) {
    uint32_t n = 0;
    for (auto& [id, st] : g_ents) {
        if (e && id != e) continue;
        for (auto& c : st.copies) {
            if (!c.data || !seh_read(c.original, c.data, c.type.size)) continue;
            for (auto& f : c.fields) write_override(c, f);
            ++n;
        }
    }
    if (out_count) *out_count = n;
    return KILN_OK;
}

bool is_private_copy(const void* p) {
    for (auto& [id, st] : g_ents)
        for (auto& c : st.copies)
            if (c.data == p) return true;
    return false;
}

// ---------------------------------------------------------------------------------------------- owners, Pre ----

uint32_t release_owner_now(KilnOwner owner) {
    if (!owner) return 0;
    uint32_t n = 0;
    const uintptr_t w = world();
    for (auto& [id, st] : g_ents) {
        const bool a = alive(st, w);
        n += reset_in(st, owner, 0, 0, a);
        n += copy_reset_in(st, owner, {}, {}, a);
    }
    for (auto it = g_ents.begin(); it != g_ents.end();)
        it = it->second.fields.empty() && it->second.copies.empty() ? g_ents.erase(it) : std::next(it);
    ai_release_owner(owner);
    rules_release_owner(owner);
    if (n) KC_I("owner %llu released: %u change(s) reverted", static_cast<unsigned long long>(owner), n);
    return n;
}

void request_release(KilnOwner owner) {
    std::lock_guard lock(g_release_mu);
    g_release.push_back(owner);
}

void layers_pre() {
    {
        std::vector<KilnOwner> rel;
        {
            std::lock_guard lock(g_release_mu);
            rel.swap(g_release);
        }
        for (KilnOwner o : rel) release_owner_now(o);
    }
    const uintptr_t w = world();
    if (w) {
        for (auto it = g_ents.begin(); it != g_ents.end();) {
            EntityState& st = it->second;
            if (!alive(st, w)) {
                KC_D("entity %u is gone: %zu value(s) and %zu copy(ies) dropped", st.e, st.fields.size(), st.copies.size());
                for (auto& c : st.copies) g_free.push_back({c.data, g_step + kFreeDelaySteps});
                it = g_ents.erase(it);
                continue;
            }
            // a copy whose slot no longer points at it: re-point it (the engine re-initialized the slot) once
            for (auto& c : st.copies) {
                void** s = slot_of(st.e, c.hash);
                void* cur = nullptr;
                if (!s || !seh_read(s, &cur, 8) || cur == c.data) continue;
                if (!c.warned) {
                    KC_W("entity %u: its %s slot held 0x%llx instead of the private copy; re-pointed", st.e,
                         c.component.c_str(), static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(cur)));
                    c.warned = true;
                }
                if (cur) c.original = cur;
                seh_write(s, &c.data, 8);
            }
            ++it;
        }
    }
    marks_expire();
    for (auto it = g_free.begin(); it != g_free.end();) {
        if (g_step >= it->at) {
            if (it->p) _aligned_free(it->p);
            it = g_free.erase(it);
        } else {
            ++it;
        }
    }
}

void layers_info(KilnEntity e, KilnCreatureInfo* io) {
    resolve_dyn();
    auto it = g_ents.find(e);
    if (it != g_ents.end()) {
        uint32_t layers = 0;
        for (auto& [k, fs] : it->second.fields) layers += static_cast<uint32_t>(fs.stack.size());
        for (auto& c : it->second.copies)
            for (auto& f : c.fields) layers += static_cast<uint32_t>(f.stack.size());
        io->layers = layers;
        io->private_copies = static_cast<uint32_t>(it->second.copies.size());
        if (layers) io->flags |= KILN_CREATURE_I_LAYERS;
        if (!it->second.copies.empty()) io->flags |= KILN_CREATURE_I_PRIVATE;
        auto sp = it->second.fields.find(key(KILN_CF_SPEED, 0));
        if (sp != it->second.fields.end() && sp->second.speed_orig[3] > 0) {
            uint32_t n = 0;
            auto* c = static_cast<uint8_t*>(component(e, H::Movement, &n));
            float walk = 0;
            if (rdc(c, n, 8 + 12, &walk)) io->speed_factor = walk / sp->second.speed_orig[3];
        }
    }
    uint32_t n = 0;
    auto* d = static_cast<uint8_t*>(component(e, H::Dyn, &n));
    if (d) {
        float p[3];
        if (g_off_home >= 0 && static_cast<uint32_t>(g_off_home) + 12 <= n && seh_read(d + g_off_home, p, 12))
            io->home = KilnVec3{p[0], p[1], p[2]};
        if (g_off_guard >= 0) rdc(d, n, static_cast<uint32_t>(g_off_guard), &io->guard);
    }
}

void layers_stats(uint32_t* entities, uint32_t* layers, uint32_t* copies, uint32_t* pending_free) {
    uint32_t l = 0, c = 0;
    for (auto& [id, st] : g_ents) {
        for (auto& [k, fs] : st.fields) l += static_cast<uint32_t>(fs.stack.size());
        for (auto& cp : st.copies) {
            ++c;
            for (auto& f : cp.fields) l += static_cast<uint32_t>(f.stack.size());
        }
    }
    *entities = static_cast<uint32_t>(g_ents.size());
    *layers = l;
    *copies = c;
    *pending_free = static_cast<uint32_t>(g_free.size());
}

// ---------------------------------------------------------------------------------------------- marks ----

void mark_request(KilnEntity e, uint32_t comps) {
    std::lock_guard lock(g_mark_mu);
    auto& m = g_marks[e];
    m.comps |= comps;
    m.step = g_step;
    g_marks_pending.store(true, std::memory_order_release);
}

// The marks program runs once per slice (archetype group), possibly in parallel: each slice takes a snapshot, marks
// the entities it iterates and removes those; what no slice saw stays (dropped after ~4 s: no Faction, gone).
void mark_snapshot(std::unordered_map<KilnEntity, uint32_t>* out) {
    std::lock_guard lock(g_mark_mu);
    out->clear();
    for (auto& [e, m] : g_marks) (*out)[e] = m.comps;
}

void mark_done(const std::vector<KilnEntity>& done) {
    std::lock_guard lock(g_mark_mu);
    for (KilnEntity e : done) g_marks.erase(e);
    g_marks_pending.store(!g_marks.empty(), std::memory_order_release);
}

void marks_expire() {
    std::lock_guard lock(g_mark_mu);
    for (auto it = g_marks.begin(); it != g_marks.end();)
        it = g_step - it->second.step > 100 ? g_marks.erase(it) : std::next(it);
    g_marks_pending.store(!g_marks.empty(), std::memory_order_release);
}

bool marks_pending() { return g_marks_pending.load(std::memory_order_acquire); }

}  // namespace kcr
