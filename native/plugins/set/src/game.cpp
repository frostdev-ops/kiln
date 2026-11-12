// SPDX-License-Identifier: MIT
// kiln-set: SET's game interface (game.h) on Kiln's services. The refl:: helpers that walk type descriptors and the
// res:: blob/label/GUID helpers are adapted from Shrouded Engine Tweaks runtime/game.cpp, Copyright (c) 2026 Cacaida,
// MIT (licenses/ShroudedEngineTweaks-LICENSE.txt). Table lookups, systems, entities, events and resources go through
// kiln.types, kiln.world and kiln.assets.
#include "game.h"
#include "lua_budget.h"

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstring>
#include <map>
#include <mutex>
#include <unordered_map>

#include "kiln_api.h"
#include "services/kiln_assets.h"
#include "services/kiln_players.h"
#include "services/kiln_time.h"
#include "services/kiln_types.h"
#include "services/kiln_world.h"

namespace {
const KilnApi* g_api = nullptr;
const KilnTypesApi* T = nullptr;
const KilnWorldApi* W = nullptr;
const KilnAssetsApi* A = nullptr;
const KilnPlayersApi* P = nullptr;
const KilnTimeApi* TM = nullptr;

template <class Tbl>
const Tbl* service(const char* name, uint32_t version) {
    const auto* t = static_cast<const Tbl*>(g_api->service(KilnStr{name, std::strlen(name)}, version));
    return t && t->header.size >= sizeof(Tbl) && t->header.version >= version ? t : nullptr;
}
KilnStr ks(std::string_view s) { return KilnStr{s.data(), s.size()}; }
const uint8_t* desc(const KilnType* t) { return reinterpret_cast<const uint8_t*>(t); }
}  // namespace

bool kset::tickThread() { return g_api && g_api->on_tick_thread(); }
bool kset::systemsReady() {
    KilnTypesStatus st{sizeof st};
    return T && T->status(&st) == KILN_OK && st.systems;
}

bool kset::has(std::string_view service) {
    return service == "types" ? T != nullptr : service == "world" ? W != nullptr : service == "assets" ? A != nullptr :
           service == "players" ? P != nullptr : service == "time" ? TM != nullptr : false;
}

std::string kset::bind(const KilnApi* api) {
    g_api = api;
    T = service<KilnTypesApi>(KILN_TYPES_SERVICE, 1);
    W = service<KilnWorldApi>(KILN_WORLD_SERVICE, 1);
    A = service<KilnAssetsApi>(KILN_ASSETS_SERVICE, 1);
    P = service<KilnPlayersApi>(KILN_PLAYERS_SERVICE, 1);
    TM = service<KilnTimeApi>(KILN_TIME_SERVICE, 1);
    if (A && A->header.size < offsetof(KilnAssetsApi, resource_data) + sizeof(void*)) A = nullptr;
    std::string missing;
    auto need = [&](bool ok, const char* what) { if (!ok) missing += (missing.empty() ? "" : ", ") + std::string(what); };
    need(T && T->type_count() > 0, "kiln.types (types)");
    need(W != nullptr, "kiln.world (Ecs)");
    need(A != nullptr, "kiln.assets (Res)");
    return missing;
}

// ---------------------------------------------------------------- refl (helpers adapted from SET)
namespace {
refl::Field fieldOf(refl::Type t, uint32_t i) {
    KilnTypeField f{};
    if (!T || T->type_field(reinterpret_cast<const KilnType*>(t.p), i, &f) != KILN_OK || f.offset > UINT32_MAX) return {};
    return {std::string_view(f.name.ptr, f.name.len), {desc(f.type)}, static_cast<uint32_t>(f.offset)};
}
}  // namespace

namespace {
KilnTypeInfo infoOf(refl::Type t) {
    KilnTypeInfo i{sizeof i};
    if (!t || !T || T->type_info(reinterpret_cast<const KilnType*>(t.p), &i) != KILN_OK) return {};
    return i;
}
}
std::string_view refl::Type::str(size_t off) const {
    const auto i = infoOf(*this);
    const auto s = off == 0 ? i.name : i.qualified_name;
    return s.ptr ? std::string_view(s.ptr, s.len) : std::string_view();
}
uint32_t refl::Type::u32(size_t off) const {
    const auto i = infoOf(*this);
    switch (off) { case 0x40: return i.type_size; case 0x48: return i.field_count; case 0x50: return i.hash; default: return 0; }
}
uint8_t refl::Type::prim() const { return infoOf(*this).kind; }
refl::Type refl::Type::inner() const { return {desc(infoOf(*this).inner)}; }
refl::Type refl::Type::resolved() const {
    Type t = *this;
    for (int i = 0; i < 32 && t && t.prim() == Typedef; i++) t = t.inner();
    return t && t.prim() != Typedef ? t : Type{};
}
const uint8_t* refl::Type::defaults() const {
    KilnTypeInfo info{sizeof info};
    if (!T || T->type_info(reinterpret_cast<const KilnType*>(p), &info) != KILN_OK) return nullptr;
    return static_cast<const uint8_t*>(info.defaults);
}
size_t refl::count() { return T ? T->type_count() : 0; }
refl::Type refl::at(size_t i) { return {T ? desc(T->type_at(static_cast<uint32_t>(i))) : nullptr}; }
refl::Type refl::byHash(uint32_t h) { return {T ? desc(T->type_by_hash(h)) : nullptr}; }
refl::Type refl::byName(std::string_view name) {
    if (!T) return {};
    std::string q = name.find("::") == std::string_view::npos ? "keen::ecs::" + std::string(name) : std::string(name);
    return {desc(T->type_by_hash(fnv(q)))};
}
refl::Type refl::lookup(std::string_view name) {
    if (Type t = byName(name)) return t;
    return {T ? desc(T->type_by_name(ks(name))) : nullptr};
}
namespace {
bool plainDepth(refl::Type t, unsigned depth, unsigned& budget) {
    using namespace refl;
    t = t.resolved();
    if (!t || depth > 32 || budget == 0 || !t.size() || t.size() > (1u << 20)) return false;
    --budget;
    uint8_t k = t.prim();
    if (k == StaticArray) return t.inner() && t.inner().size() && t.size() % t.inner().size() == 0 && plainDepth(t.inner(), depth + 1, budget);
    if (k == Struct) {
        Type bases[32]; size_t n = 0;
        for (Type base = t; base && base.prim() == Struct; base = base.inner()) {
            if (n == 32 || base.size() > t.size() || std::any_of(bases, bases + n, [&](Type x) { return x.p == base.p; })) return false;
            bases[n++] = base;
        }
    }
    if (k != Struct) return (k >= Bool && k <= Bitmask64) || ((k == ObjectReference || k == Guid) && t.size() == 16);
    bool ok = true;
    forFields(t, [&](const Field& f) { ok = ok && f.offset <= t.size() && f.type.size() <= t.size() - f.offset && plainDepth(f.type, depth + 1, budget); });
    return ok;
}
}
bool refl::plain(Type t) { unsigned budget = 4096; return plainDepth(t, 0, budget); }
bool refl::field(Type t, std::string_view name, Field& out) {
    for (int depth = 0; t && t.prim() == Struct && depth < 32; depth++, t = t.inner()) {
        for (uint32_t i = 0; i < t.fieldCount(); i++) {
            Field f = fieldOf(t, i);
            if (f.type && f.name == name && f.offset <= t.size() && f.type.size() <= t.size() - f.offset) {
                out = f;
                return true;
            }
        }
    }
    return false;
}
void refl::forFields(Type t, const std::function<void(const Field&)>& fn) {
    Type chain[32];
    size_t n = 0;
    for (Type base = t; base && base.prim() == Struct; base = base.inner()) {
        if (n == 32 || std::any_of(chain, chain + n, [&](Type x) { return x.p == base.p; })) return;
        chain[n++] = base;
    }
    while (n) {
        Type base = chain[--n];
        for (uint32_t i = 0; i < base.fieldCount(); i++) {
            Field f = fieldOf(base, i);
            if (f.type && f.offset <= base.size() && f.type.size() <= base.size() - f.offset &&
                f.offset <= t.size() && f.type.size() <= t.size() - f.offset) fn(f);
        }
    }
}
void refl::forEnum(Type t, const std::function<void(std::string_view, int64_t)>& fn) {
    if (!t || !T) return;
    for (uint32_t i = 0; i < t.fieldCount(); i++) {
        KilnStr n{};
        int64_t v = 0;
        if (T->type_enum(reinterpret_cast<const KilnType*>(t.p), i, &n, &v) != KILN_OK) break;
        fn({n.ptr, n.len}, v);
    }
}
refl::Type refl::unwrap(Type u) {
    for (int i = 0; i < 4 && u.prim() == Struct && !u.inner() && u.fieldCount() == 1; i++) {
        Field only{};
        forFields(u, [&](const Field& f) { only = f; });
        Type v = only.type.resolved();
        bool named = only.name != "value" && only.name != "id" && only.name != "index";
        if (named || only.offset != 0 || !v || v.size() != u.size() || v.prim() == StaticArray) break;
        u = v;
    }
    return u;
}
// From Shrouded Engine Tweaks 0.2.0 runtime/game.cpp (refl::path), Copyright (c) 2026 Cacaida, MIT.
bool refl::path(Type t, std::string_view p, uint32_t& off, Type& leaf) {
    off = 0;
    t = t.resolved();
    while (!p.empty()) {
        if (p[0] == '[') {
            size_t end = p.find(']');
            if (end == std::string_view::npos || end < 2 || t.prim() != StaticArray || !t.inner()) return false;
            uint32_t i = 0;
            for (char c : p.substr(1, end - 1)) {
                if (c < '0' || c > '9' || i > 100000) return false;
                i = i * 10 + uint32_t(c - '0');
            }
            Type e = t.inner();
            if (!e.size() || i >= t.size() / e.size()) return false;
            off += i * e.size();
            t = e.resolved();
            p.remove_prefix(end + 1);
            if (!p.empty() && p[0] == '.') p.remove_prefix(1);
            continue;
        }
        size_t end = p.find_first_of(".[");
        Field f;
        if (!field(t, p.substr(0, end), f)) return false;
        off += f.offset;
        t = f.type.resolved();
        p.remove_prefix(end == std::string_view::npos ? p.size() : end);
        if (!p.empty() && p[0] == '.') p.remove_prefix(1);
    }
    leaf = unwrap(t);  // a wrapper's only field sits at offset 0
    return bool(leaf);
}
uint32_t refl::fnv(std::string_view s) {
    uint32_t h = 0x811c9dc5;
    for (unsigned char c : s) h = (h ^ c) * 0x01000193;
    return h;
}
bool refl::enumHas(Type t, int64_t v) {
    bool found = false;
    forEnum(t, [&](std::string_view, int64_t e) { found |= e == v; });
    return found;
}
std::string refl::text(const uint8_t* p, Type u) {
    uint8_t k = u.prim();
    char buf[64];
    if (k == Bool) return *p ? "true" : "false";
    if (k == F32 || k == F64) {
        double d = 0;
        if (k == F32) {
            float f;
            memcpy(&f, p, 4);
            d = f;
        } else {
            memcpy(&d, p, 8);
        }
        snprintf(buf, sizeof buf, "%g", d);
        return buf;
    }
    uint64_t v = 0;
    uint32_t n = std::min<uint32_t>(u.size(), 8);
    memcpy(&v, p, n);
    bool sign = k == S8 || k == S16 || k == S32 || k == S64;
    if (sign && n < 8 && (v >> (n * 8 - 1) & 1)) v |= ~0ull << (n * 8);
    bool mask = k >= Bitmask8 && k <= Bitmask64;
    snprintf(buf, sizeof buf, mask ? "0x%llx" : sign ? "%lld" : "%llu", static_cast<unsigned long long>(v));
    std::string s = buf;
    if (k == Enum) {
        forEnum(u, [&](std::string_view name, int64_t e) {
            if (uint64_t(e) == v) s = std::string(name) + " (" + s + ")";
        });
    }
    return s;
}
namespace {
refl::Field badDepth(refl::Type t, const uint8_t* p, unsigned depth, unsigned& budget) {
    using namespace refl;
    t = unwrap(t.resolved());
    const Field invalid{"<invalid value>", t, 0};
    if (!t || depth > 32 || !budget || !t.size()) return invalid;
    --budget;
    if (t.prim() == Struct) {
        Field bad{};
        forFields(t, [&](const Field& f) {
            if (!bad.name.empty()) return;
            if (f.offset > t.size() || f.type.size() > t.size() - f.offset) { bad = f; return; }
            Field nested = badDepth(f.type, p + f.offset, depth + 1, budget);
            if (!nested.name.empty()) bad = {f.name, nested.type, f.offset + nested.offset};
        });
        return bad;
    }
    if (t.prim() == StaticArray) {
        Type e = t.inner();
        if (!e || !e.size() || t.size() % e.size()) return invalid;
        for (uint32_t i = 0; i < t.size() / e.size(); ++i) {
            Field bad = badDepth(e, p + i * e.size(), depth + 1, budget);
            if (!bad.name.empty()) { bad.offset += i * e.size(); return bad; }
        }
        return {};
    }
    if (t.prim() == Bool) return *p > 1 ? invalid : Field{};
    if (t.prim() == Enum) {
        if (t.size() > 8) return invalid;
        uint64_t raw = 0; memcpy(&raw, p, t.size());
        uint64_t mask = t.size() == 8 ? UINT64_MAX : (1ull << (t.size() * 8)) - 1;
        for (uint32_t i = 0; i < t.fieldCount(); ++i) {
            if (!budget) return invalid;
            --budget; KilnStr name{}; int64_t value = 0;
            if (!T || T->type_enum(reinterpret_cast<const KilnType*>(t.p), i, &name, &value) != KILN_OK) return invalid;
            if ((uint64_t(value) & mask) == raw) return {};
        }
        return invalid;
    }
    if (t.prim() == F32) { float v; memcpy(&v, p, 4); return std::isfinite(v) ? Field{} : invalid; }
    if (t.prim() == F64) { double v; memcpy(&v, p, 8); return std::isfinite(v) ? Field{} : invalid; }
    return {};
}
}
refl::Field refl::badField(Type t, const uint8_t* p) { unsigned budget = 4096; return badDepth(t, p, 0, budget); }

// ---------------------------------------------------------------- sys (kiln.types)
namespace {
std::vector<sys::System> g_sys;
bool g_sys_loaded = false;
}  // namespace
const std::vector<sys::System>& sys::all() {
    if (!g_sys_loaded && T) {
        g_sys_loaded = true;
        for (uint32_t i = 0; i < T->system_count(); ++i) {
            KilnSystemInfo s{sizeof s};
            if (T->system_info(i, &s) == KILN_OK)
                g_sys.push_back({{s.name.ptr, s.name.len}, static_cast<uintptr_t>(s.rva), i});
        }
    }
    return g_sys;
}
const sys::System* sys::find(std::string_view name) {
    const auto& v = all();
    auto it = std::lower_bound(v.begin(), v.end(), name, [](const System& s, std::string_view n) { return s.name < n; });
    return it != v.end() && it->name == name ? &*it : nullptr;
}
bool sys::isOn(const System& s) {
    KilnSystemInfo i{sizeof i};
    return !T || T->system_info(s.index, &i) != KILN_OK || i.enabled;
}
int sys::setOn(const System& s, bool on, uint64_t owner) {
    return T ? T->system_set_enabled(owner, s.index, on) : KILN_ERR_UNAVAILABLE;
}
uint64_t sys::createOwner(std::string_view name) { return g_api ? g_api->owner_create(ks(name)) : 0; }
void sys::releaseOwner(uint64_t owner) {
    if (!owner) return;
    if (T) T->system_release_owner(owner);
    if (g_api) g_api->owner_revoke(owner);
}
size_t sys::offCount() {
    KilnTypesStatus st{sizeof st};
    return T && T->status(&st) == KILN_OK ? st.systems_off : 0;
}
void sys::setProfiling(bool on) {
    if (T) T->profile(on);
}
bool sys::profiling() {
    KilnTypesStatus st{sizeof st};
    return T && T->status(&st) == KILN_OK && st.profiling;
}
std::vector<sys::Stat> sys::stats() {
    std::vector<Stat> out;
    for (const System& s : all()) {
        KilnSystemInfo i{sizeof i};
        if (T->system_info(s.index, &i) == KILN_OK && i.calls) out.push_back({&s, i.calls, i.ms, i.world_kinds});
    }
    std::sort(out.begin(), out.end(), [](const Stat& a, const Stat& b) { return a.ms > b.ms; });
    return out;
}
double sys::profiledSecs() {
    KilnTypesStatus st{sizeof st};
    return T && T->status(&st) == KILN_OK ? st.profile_seconds : 0.0;
}

// ---------------------------------------------------------------- opts (not ported yet)
bool opts::ready() { return false; }
uint8_t* opts::machine() { return nullptr; }
uint8_t* opts::user() { return nullptr; }
refl::Type opts::machineType() { return refl::byName("keen::ds::g38_options::pc::machine::Settings"); }
refl::Type opts::userType() { return refl::byName("keen::ds::g38_options::pc::user::Settings"); }
size_t opts::dump(std::string_view, const std::function<void(const std::string&)>&) { return 0; }

// ---------------------------------------------------------------- ecs (kiln.world, kiln.types)
// A component "index" here is a slot in g_comp_hashes: Lua refs keep it, and every access looks the component up
// again through kiln.world, so a ref never holds a pointer.
namespace {
std::vector<uint32_t> g_comp_hashes;
std::unordered_map<uint32_t, int> g_comp_idx;
}  // namespace
int ecs::compIndex(uintptr_t, uint32_t typeHash) {
    if (!W || !T) return -1;
    if (auto it = g_comp_idx.find(typeHash); it != g_comp_idx.end()) return it->second;
    bool listed = false;
    for (uint32_t i = 0, n = T->component_type_count(); i < n && !listed; ++i) {
        KilnTypeInfo info{sizeof info};
        listed = T->type_info(T->component_type_at(i), &info) == KILN_OK && info.hash == typeHash;
    }
    if (!listed) return -1;
    const int idx = static_cast<int>(g_comp_hashes.size());
    g_comp_hashes.push_back(typeHash);
    g_comp_idx.emplace(typeHash, idx);
    return idx;
}
uint8_t* ecs::component(uintptr_t, uint32_t entity, int idx, uint32_t* size_out) {
    if (size_out) *size_out = 0;
    if (!W || !g_api->on_tick_thread() || idx < 0 || size_t(idx) >= g_comp_hashes.size()) return nullptr;
    uint32_t size = 0;
    // kiln.world hands out game memory; writes on the tick thread are what SET does (they skip dirty-marking).
    auto* p = const_cast<uint8_t*>(static_cast<const uint8_t*>(W->component(entity, g_comp_hashes[size_t(idx)], &size)));
    if (size_out) *size_out = size;
    return p;
}
namespace {
std::vector<uint32_t> findAll(int idx) {
    std::vector<uint32_t> out(4096);
    uint32_t n = 0;
    KilnResult r = W->find(g_comp_hashes[size_t(idx)], out.data(), uint32_t(out.size()), &n);
    if (r == KILN_ERR_BUFFER_TOO_SMALL) {
        out.resize(n + 256);
        r = W->find(g_comp_hashes[size_t(idx)], out.data(), uint32_t(out.size()), &n);
    }
    if (r != KILN_OK) return {};
    out.resize(n);
    return out;
}
}  // namespace
uint32_t ecs::firstWith(uintptr_t, int idx) {
    if (!W || idx < 0 || size_t(idx) >= g_comp_hashes.size()) return 0;
    auto all = findAll(idx);
    return all.empty() ? 0 : all.front();
}
std::vector<uint32_t> ecs::allWith(uintptr_t w, const int* idx, size_t n) {
    if (!W || !n || std::any_of(idx, idx + n, [](int i) { return i < 0 || size_t(i) >= g_comp_hashes.size(); })) return {};
    std::vector<uint32_t> out;
    for (uint32_t e : findAll(idx[0]))
        if (std::all_of(idx + 1, idx + n, [&](int i) { return component(w, e, i) != nullptr; })) out.push_back(e);
    return out;
}
uint32_t ecs::entityCount(uintptr_t) { return W ? W->entity_count() : 0; }
bool ecs::exists(uint32_t entity) { return W && W->exists && g_api->on_tick_thread() && W->exists(entity); }
std::vector<refl::Type> ecs::componentTypes() {
    std::vector<refl::Type> out;
    if (T) for (uint32_t i = 0, n = std::min(T->component_type_count(), 4096u); i < n; ++i)
        out.push_back({reinterpret_cast<const uint8_t*>(T->component_type_at(i))});
    return out;
}
std::vector<uint32_t> ecs::findBounded(uint32_t hash, uint32_t limit, uint32_t& total) {
    total = 0;
    if (!W || !W->find || !g_api->on_tick_thread()) return {};
    std::vector<uint32_t> out(std::min(limit, 65536u));
    auto r = W->find(hash, out.data(), static_cast<uint32_t>(out.size()), &total);
    if (r != KILN_OK && r != KILN_ERR_BUFFER_TOO_SMALL) return {};
    out.resize(std::min(total, static_cast<uint32_t>(out.size())));
    return out;
}
bool ecs::position(uint32_t entity, double* xyz) {
    KilnVec3 p{};
    if (!W || !W->position || !g_api->on_tick_thread() || W->position(entity, &p) != KILN_OK ||
        !std::isfinite(p.x) || !std::isfinite(p.y) || !std::isfinite(p.z)) return false;
    xyz[0] = p.x; xyz[1] = p.y; xyz[2] = p.z; return true;
}
namespace {
// Event stream lookups per (world, type), so a tick runs one event_each per observed type instead of a probe and a
// read. Only the tick thread under the Lua lock touches it. A new world, a world leave, or kiln.types refusing the
// world (INVALID_ARGUMENT: not a known world) drops it. Only answers that hold for the world's lifetime are kept: OK,
// NOT_FOUND (the type table and the world's stream map are fixed once it exists) and UNAVAILABLE (decided at startup).
// WRONG_THREAD and INTERNAL (a failed read of the stream) say nothing lasting about the stream: never cached.
struct EventCache {
    uintptr_t world = 0;
    std::unordered_map<uint32_t, KilnResult> streams;
    uint64_t probes = 0;
} g_events;
void eventsFor(uintptr_t world) {
    if (g_events.world == world) return;
    g_events.world = world;
    g_events.streams.clear();
}
void eventResult(uint32_t typeHash, KilnResult r) {
    if (r == KILN_ERR_INVALID_ARGUMENT) g_events.streams.clear();  // the world may be gone: look everything up again
    if (r == KILN_OK || r == KILN_ERR_NOT_FOUND || r == KILN_ERR_UNAVAILABLE) g_events.streams[typeHash] = r;
    else g_events.streams.erase(typeHash);
}
}  // namespace
int ecs::eventIndex(uintptr_t world, uint32_t typeHash) {
    // Any non-negative value means "has a stream", a negative one is the KilnResult negated (KILN_ERR_NOT_FOUND: no
    // stream, WRONG_THREAD, UNAVAILABLE, INVALID_ARGUMENT: unknown world or layout). Probes only on a cache miss.
    if (!T || !world) return -int(KILN_ERR_UNAVAILABLE);
    eventsFor(world);
    if (auto it = g_events.streams.find(typeHash); it != g_events.streams.end())
        return it->second == KILN_OK ? 0 : -int(it->second);
    struct Probe { static void KILN_CALL none(const void*, uint32_t, void*) {} };
    g_events.probes++;
    const KilnResult r = T->event_each(world, typeHash, &Probe::none, nullptr);
    eventResult(typeHash, r);
    return r == KILN_OK ? 0 : -int(r);
}
int ecs::forEachEvent(uintptr_t world, uint32_t typeHash, const std::function<void(const uint8_t* ev)>& fn) {
    if (!T || !world) return -int(KILN_ERR_UNAVAILABLE);
    eventsFor(world);
    // A cached failure skips the call; otherwise the read itself is the lookup (no separate probe).
    if (auto it = g_events.streams.find(typeHash); it != g_events.streams.end() && it->second != KILN_OK)
        return -int(it->second);
    struct Tramp {
        static void KILN_CALL call(const void* data, uint32_t, void* user) {
            (*static_cast<const std::function<void(const uint8_t*)>*>(user))(static_cast<const uint8_t*>(data));
        }
    };
    const KilnResult r = T->event_each(world, typeHash, &Tramp::call, const_cast<std::function<void(const uint8_t*)>*>(&fn));
    eventResult(typeHash, r);
    return r == KILN_OK ? 0 : -int(r);
}
void ecs::forgetEvents() { eventsFor(0); }
uint64_t ecs::eventProbes() { return g_events.probes; }
bool ecs::postEvent(uintptr_t world, uint32_t typeHash, const uint8_t* data, uint32_t size) {
    if (!T) return false;
    const KilnResult r = T->event_post(world, typeHash, data, size);
    // Only NOT_FOUND says something about the stream: INVALID_ARGUMENT here is usually the payload, BUFFER_TOO_SMALL a
    // full queue this step.
    if (r == KILN_ERR_NOT_FOUND && g_events.world == world) eventResult(typeHash, r);
    return r == KILN_OK;
}
int64_t ecs::simTime(uintptr_t world) {
    KilnTimeState ts{sizeof ts};
    return world && g_api && TM && world == g_api->server_world() && TM->get(&ts) == KILN_OK ? ts.ecs_time_ns : 0;
}
uint32_t ecs::player() {
    if (!P) return 0;
    KilnPlayer buf[64];
    for (auto& b : buf) b.size = sizeof(KilnPlayer);
    uint32_t n = 0;
    if (P->list(0, buf, 64, &n) != KILN_OK && n == 0) return 0;
    for (uint32_t i = 0; i < n && i < 64; ++i)
        if (buf[i].entity) return buf[i].entity;
    return 0;
}

// ---------------------------------------------------------------- res (kiln.assets)
namespace {
std::mutex g_res_mx;
std::map<uint32_t, std::vector<const res::Entry*>> g_of_type;
std::vector<std::unique_ptr<res::Entry>> g_entries;
bool g_res_scanned = false;
std::atomic<res::Scan> g_res_state{res::Scan::Unchecked};  // what the last ready() saw, for any thread without the lock
std::mutex g_write_mx;

int hexVal(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}
void swapGroups(uint8_t g[16]) {  // text order <-> the game's (.NET) byte order
    std::swap(g[0], g[3]);
    std::swap(g[1], g[2]);
    std::swap(g[4], g[5]);
    std::swap(g[6], g[7]);
}
const std::vector<const res::Entry*>& listType(uint32_t type) {
    static const std::vector<const res::Entry*> kNone;
    auto it = g_of_type.find(type);
    if (it != g_of_type.end()) return it->second;
    auto& v = g_of_type[type];
    if (!A) return v;
    size_t n = 0;
    if (A->resource_list(type, nullptr, 0, &n) != KILN_ERR_BUFFER_TOO_SMALL && n == 0) return v;
    std::vector<KilnAssetKey> keys(n);
    if (A->resource_list(type, keys.data(), keys.size(), &n) != KILN_OK) return v;
    for (size_t i = 0; i < std::min(n, keys.size()); ++i) {
        const void* p = nullptr;
        size_t len = 0;
        if (A->resource_data(&keys[i], &p, &len) != KILN_OK || !p || len > UINT32_MAX) continue;
        auto e = std::make_unique<res::Entry>();
        std::memcpy(e->guid, keys[i].guid, 16);
        e->type = keys[i].type;
        e->part = keys[i].part;
        e->off = 0;
        e->size = static_cast<uint32_t>(len);
        v.push_back(e.get());
        g_entries.push_back(std::move(e));
    }
    return v;
}
void scanAll() {
    if (g_res_scanned || !T || !A) return;
    g_res_scanned = true;
    const auto t0 = std::chrono::steady_clock::now();
    // Only the first scan is Kiln's work. Cached calls must not extend a mod's callback budget.
    struct Excuse {
        std::chrono::steady_clock::time_point started;
        ~Excuse() { kset::budget::excuse(std::chrono::steady_clock::now() - started); }
    } excuse{t0};
    for (uint32_t i = 0; i < T->type_count(); ++i) {
        KilnTypeInfo info{sizeof info};
        if (T->type_info(T->type_at(i), &info) != KILN_OK || info.kind != KILN_TYPE_STRUCT) continue;
        listType(info.hash);
    }
    for (auto it = g_of_type.begin(); it != g_of_type.end();) it = it->second.empty() ? g_of_type.erase(it) : std::next(it);
    char line[160];
    snprintf(line, sizeof line, "resources: %zu of %zu types in %.0f ms", g_entries.size(), g_of_type.size(),
             std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count());
    g_api->log(0, KILN_LOG_INFO, KilnStr{line, std::strlen(line)});
}
}  // namespace

bool res::ready() {
    if (!A) return false;
    std::lock_guard lock(g_res_mx);
    scanAll();
    const bool found = !g_entries.empty();
    g_res_state = found ? Scan::Found : Scan::None;
    return found;
}
res::Scan res::scanned() { return g_res_state.load(std::memory_order_acquire); }
namespace {
bool copyGuarded(void* dst, const void* src, size_t n) {
    __try { std::memcpy(dst, src, n); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
}
bool res::write(void* dst, const void* src, size_t n, uint32_t& prot) {
    std::lock_guard lock(g_write_mx);
    uintptr_t at = reinterpret_cast<uintptr_t>(dst);
    if (!dst || !src || !n || n > UINTPTR_MAX - at) return false;
    const uintptr_t end = at + n;
    struct Changed { void* at; size_t size; DWORD old; };
    std::vector<Changed> changed;
    auto restore = [&] {
        bool ok = true;
        for (auto it = changed.rbegin(); it != changed.rend(); ++it) {
            DWORD ignored;
            if (!VirtualProtect(it->at, it->size, it->old, &ignored)) ok = false;
        }
        changed.clear();
        return ok;
    };
    struct RestoreOnExit { decltype(restore)& fn; ~RestoreOnExit() { fn(); } } cleanup{restore};
    while (at < end) {
        MEMORY_BASIC_INFORMATION region{};
        if (!VirtualQuery(reinterpret_cast<void*>(at), &region, sizeof region) || region.State != MEM_COMMIT ||
            (region.Protect & (PAGE_GUARD | PAGE_NOACCESS))) { restore(); return false; }
        uintptr_t base = reinterpret_cast<uintptr_t>(region.BaseAddress);
        if (base > at || region.RegionSize > UINTPTR_MAX - base || base + region.RegionSize <= at) { restore(); return false; }
        const size_t take = std::min<size_t>(end - at, base + region.RegionSize - at);
        changed.push_back({reinterpret_cast<void*>(at), take, 0});
        if (!VirtualProtect(reinterpret_cast<void*>(at), take, PAGE_READWRITE, &changed.back().old)) { changed.pop_back(); restore(); return false; }
        at += take;
    }
    prot = changed.front().old;
    const bool copied = copyGuarded(dst, src, n);
    return restore() && copied;
}
size_t res::count() {
    std::lock_guard lock(g_res_mx);
    scanAll();
    return g_entries.size();
}
uint8_t* res::data(const Entry& e, uint32_t* size) {
    if (size) *size = 0;
    if (!A || !kset::tickThread()) return nullptr;
    KilnAssetKey key{};
    std::memcpy(key.guid, e.guid, sizeof key.guid); key.type = e.type; key.part = e.part;
    const void* p = nullptr; size_t n = 0;
    if (A->resource_data(&key, &p, &n) != KILN_OK || !p || n > UINT32_MAX) return nullptr;
    if (size) *size = static_cast<uint32_t>(n);
    return const_cast<uint8_t*>(static_cast<const uint8_t*>(p));
}
const res::Entry* res::find(const uint8_t guid[16], uint32_t type, uint32_t part) {
    std::lock_guard lock(g_res_mx);
    scanAll();
    auto match = [&](const Entry* e) { return !std::memcmp(e->guid, guid, 16) && e->part == part; };
    if (type) {
        for (const Entry* e : listType(type)) if (match(e)) return e;
        return nullptr;
    }
    for (auto& [t, list] : g_of_type)
        for (const Entry* e : list) if (match(e)) return e;
    return nullptr;
}
const std::vector<const res::Entry*>& res::ofType(uint32_t type) {
    std::lock_guard lock(g_res_mx);
    scanAll();
    return listType(type);
}
std::vector<std::pair<refl::Type, size_t>> res::types() {
    std::lock_guard lock(g_res_mx);
    scanAll();
    std::vector<std::pair<refl::Type, size_t>> out;
    for (auto& [hash, list] : g_of_type)
        if (refl::Type t = refl::byHash(hash); t && !list.empty()) out.emplace_back(t, list.size());
    std::sort(out.begin(), out.end(), [](const auto& a, const auto& b) { return a.first.qname() < b.first.qname(); });
    return out;
}
refl::Type res::typeByName(std::string_view name) {
    for (auto& [t, n] : types())
        if (t.qname() == name || t.name() == name) return t;
    return {};
}
res::Blob res::blob(const uint8_t* p, uint8_t prim) {  // adapted from SET
    uint32_t a, b, c;
    memcpy(&a, p, 4);
    if (prim == refl::BlobOptional) return {a, a ? 1u : 0u, 0};
    memcpy(&b, p + 4, 4);
    if (prim != refl::BlobVariant) return a ? Blob{a, b, 0} : Blob{};
    memcpy(&c, p + 8, 4);
    return a && b && b <= UINT32_MAX - 4 ? Blob{4 + b, c, a} : Blob{};
}
std::string_view res::label(const Entry& e) {  // adapted from SET
    refl::Type t = refl::byHash(e.type);
    const uint8_t* p = data(e);
    for (const char* name : {"debugName", "name"}) {
        refl::Field f;
        if (!p || !refl::field(t, name, f) || f.type.resolved().prim() != refl::BlobString || !inside(e, p + f.offset, 8))
            continue;
        Blob b = blob(p + f.offset, refl::BlobString);
        const uint8_t* s = p + f.offset + b.rel;
        if (b.count && inside(e, s, b.count)) return {reinterpret_cast<const char*>(s), b.count};
    }
    return {};
}
bool res::inside(const Entry& e, const uint8_t* p, uint64_t n) {
    uint32_t size = 0;
    auto lo = reinterpret_cast<uintptr_t>(data(e, &size)), at = reinterpret_cast<uintptr_t>(p);
    return lo && at >= lo && at - lo <= size && n <= size - (at - lo);
}
bool res::parseGuid(std::string_view s, uint8_t out[16]) {  // adapted from SET
    uint8_t g[16] = {};
    if (s.size() != 36) return false;
    for (size_t i = 0, n = 0; i < s.size(); i++) {
        if (i == 8 || i == 13 || i == 18 || i == 23) {
            if (s[i] != '-') return false;
            continue;
        }
        int v = hexVal(s[i]);
        if (v < 0) return false;
        g[n / 2] = uint8_t(g[n / 2] << 4 | v);
        n++;
    }
    swapGroups(g);
    memcpy(out, g, 16);
    return true;
}
std::string res::guidStr(const uint8_t g[16]) {  // adapted from SET
    static constexpr char kHex[] = "0123456789abcdef";
    uint8_t b[16];
    memcpy(b, g, 16);
    swapGroups(b);
    std::string s;
    for (int i = 0; i < 16; i++) {
        if (i == 4 || i == 6 || i == 8 || i == 10) s += '-';
        s += kHex[b[i] >> 4];
        s += kHex[b[i] & 15];
    }
    return s;
}
bool res::isText(refl::Type t) {
    refl::Type u = t.resolved();
    return u && u.prim() == refl::ObjectReference && u.inner() && u.inner().qname() == "keen::LocaTag";
}
// English text: not ported yet (SET reads the En_Us table from the data files).
std::string_view res::text(const uint8_t[16]) { return {}; }
std::string_view res::nameText(const Entry&) { return {}; }
