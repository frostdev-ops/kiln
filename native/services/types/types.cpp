// SPDX-License-Identifier: MIT
// kiln.types: the game's type registry, ECS systems and ECS event streams (native/core/abi/services/kiln_types.h).
//  - Types: scan.h finds the static type table in the loaded image (no code pattern). Lookups by hash and name are
//    built once at boot.
//  - Systems: scan.h lists the system descriptors; one detour on the program runner (sigdb types/ProgramRunner) skips
//    switched-off systems and times every slice while the profiler runs.
//  - Events: the world's streams and the schema's event map, at offsets captured by sigdb (@types/EventStreams).
//    Reads walk the stream pages, posts append to the world's first poster, as the game's own code does. The event
//    hash is checked against the world's map first: an unknown hash crashes the game's distributor.
// Technique credit: Shrouded Engine Tweaks (MIT, Cacaida); docs/re/shrouded-engine-tweaks.md.
#include <windows.h>

#include <atomic>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>

#include "kiln_api.h"
#include "safetyhook.hpp"
#include "scan.h"
#include "services/kiln_types.h"

namespace {

using namespace ktypes;

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
Image g_im;
TypeTable g_tt;
std::unordered_map<uint32_t, uint64_t> g_by_hash;
std::unordered_map<std::string_view, uint64_t> g_by_qname;
std::unordered_map<std::string_view, uint64_t> g_by_short;  // unique short names; 0 = ambiguous
std::unordered_map<std::string_view, uint64_t> g_by_short_nods;
std::unordered_map<uint64_t, uint32_t> g_index_of;
std::string g_detail;

// Systems
std::vector<System> g_sys;
std::vector<std::pair<uint64_t, uint32_t>> g_sys_by_fn;  // update fn VA -> index
std::unique_ptr<std::atomic<bool>[]> g_off;
std::unique_ptr<std::atomic<uint64_t>[]> g_calls, g_ticks;
std::unique_ptr<std::atomic<uint8_t>[]> g_kinds;
std::atomic<uint32_t> g_off_count{0};
std::atomic<bool> g_prof{false};
int64_t g_prof_start = 0, g_prof_stop = 0, g_qpf = 1;
SafetyHookInline g_runner;
bool g_systems_ok = false;

// Events
int64_t g_ev_map = 0, g_ev_streams = 0, g_storage = 0;  // world.storage: Kiln's world pointer -> the ECS storage
bool g_events_ok = false;
constexpr uint64_t kStreamStride = 0x48, kStreamCount = 8, kPosters = 0x70, kPageSize = 0x800, kPageEnd = 0x7f8;

void logf(int level, const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    g_api->log(g_owner, level, KilnStr{buf, n < 0 ? 0 : std::min<size_t>(static_cast<size_t>(n), sizeof buf - 1)});
}
KilnStr ks(std::string_view s) { return KilnStr{s.data(), s.size()}; }
std::string_view sv(KilnStr s) { return s.ptr ? std::string_view(s.ptr, s.len) : std::string_view(); }

bool image_of_process(Image& im) {
    const auto base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    im.mem = reinterpret_cast<const uint8_t*>(base);
    im.base = base;
    im.all = {base, base + nt->OptionalHeader.SizeOfImage};
    const IMAGE_SECTION_HEADER* s = IMAGE_FIRST_SECTION(nt);
    for (unsigned i = 0; i < nt->FileHeader.NumberOfSections; ++i, ++s) {
        char name[9] = {};
        std::memcpy(name, s->Name, 8);
        const Range r{base + s->VirtualAddress, base + s->VirtualAddress + s->Misc.VirtualSize};
        if (!std::strcmp(name, ".text")) im.text = r;
        if (!std::strcmp(name, ".rdata")) im.rdata = r;
    }
    return im.text.lo && im.rdata.lo;
}

std::string_view tname(uint64_t t) { return str_at(g_im, t + td::name); }
std::string_view tqname(uint64_t t) { return str_at(g_im, t + td::qname); }
const KilnType* handle(uint64_t t) { return reinterpret_cast<const KilnType*>(t); }
uint64_t raw(const KilnType* t) { return reinterpret_cast<uint64_t>(t); }
bool known(const KilnType* t) { return t && g_index_of.count(raw(t)); }

void index_types() {
    for (uint32_t i = 0; i < g_tt.count; ++i) {
        const uint64_t t = g_im.q(g_tt.table + 8ull * i);
        g_index_of[t] = i;
        g_by_hash.emplace(g_im.d(t + td::hash), t);
        const std::string_view qn = tqname(t), n = tname(t);
        g_by_qname.emplace(qn, t);
        auto add = [&](std::unordered_map<std::string_view, uint64_t>& m) {
            auto [it, fresh] = m.emplace(n, t);
            if (!fresh && it->second != t) it->second = 0;
        };
        add(g_by_short);
        if (qn.rfind("keen::ds::", 0) != 0) add(g_by_short_nods);
    }
}

// ---- types ----
KilnResult KILN_CALL status(KilnTypesStatus* out) {
    if (!out || out->size < sizeof(KilnTypesStatus)) return KILN_ERR_INVALID_ARGUMENT;
    out->types = g_tt.error.empty() && g_tt.count;
    out->systems = g_systems_ok;
    out->events = g_events_ok;
    out->profiling = g_prof.load();
    out->type_count = g_tt.count;
    out->component_count = g_tt.component_count;
    out->event_type_count = g_tt.event_count;
    out->system_count = static_cast<uint32_t>(g_sys.size());
    out->systems_off = g_off_count.load();
    const int64_t end = g_prof ? [] { LARGE_INTEGER n; QueryPerformanceCounter(&n); return n.QuadPart; }() : g_prof_stop;
    out->profile_seconds = g_prof_start ? double(end - g_prof_start) / double(g_qpf) : 0.0;
    out->detail = ks(g_detail);
    return KILN_OK;
}
uint32_t KILN_CALL type_count() { return g_tt.error.empty() ? g_tt.count : 0; }
const KilnType* KILN_CALL type_at(uint32_t i) { return i < type_count() ? handle(g_im.q(g_tt.table + 8ull * i)) : nullptr; }
const KilnType* KILN_CALL type_by_hash(uint32_t h) {
    auto it = g_by_hash.find(h);
    return it == g_by_hash.end() ? nullptr : handle(it->second);
}
const KilnType* KILN_CALL type_by_name(KilnStr name) {
    const std::string_view n = sv(name);
    if (n.empty()) return nullptr;
    if (n.find("::") != std::string_view::npos) {
        auto it = g_by_qname.find(n);
        return it == g_by_qname.end() ? nullptr : handle(it->second);
    }
    if (const KilnType* t = type_by_hash(fnv1a(std::string("keen::ecs::") + std::string(n)))) return t;
    for (auto* m : {&g_by_short_nods, &g_by_short}) {
        auto it = m->find(n);
        if (it != m->end()) return it->second ? handle(it->second) : nullptr;
    }
    return nullptr;
}
KilnResult KILN_CALL type_info(const KilnType* type, KilnTypeInfo* out) {
    if (!out || out->size < sizeof(KilnTypeInfo)) return KILN_ERR_INVALID_ARGUMENT;
    if (!known(type)) return KILN_ERR_NOT_FOUND;
    const uint64_t t = raw(type);
    out->index = g_index_of[t];
    out->name = ks(tname(t));
    out->qualified_name = ks(tqname(t));
    out->type_size = g_im.d(t + td::size);
    out->alignment = g_im.w(t + td::align);
    out->kind = g_im.b(t + td::kind);
    out->flags = g_im.b(t + td::flags);
    out->hash = g_im.d(t + td::hash);
    out->field_count = g_im.d(t + td::count);
    const uint64_t inner = g_im.q(t + td::inner);
    out->inner = g_index_of.count(inner) ? handle(inner) : nullptr;
    const uint64_t dp = g_im.q(t + td::defaults), dn = g_im.q(t + td::defaults + 8);
    out->defaults = dp && dn == out->type_size && g_im.all.has(dp, dn) ? reinterpret_cast<const void*>(dp) : nullptr;
    return KILN_OK;
}
KilnResult KILN_CALL type_field(const KilnType* type, uint32_t index, KilnTypeField* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    if (!known(type)) return KILN_ERR_NOT_FOUND;
    const uint64_t t = raw(type), f = g_im.q(t + td::fields);
    if (!f || index >= g_im.d(t + td::count) || g_im.b(t + td::kind) != KILN_TYPE_STRUCT) return KILN_ERR_NOT_FOUND;
    const uint64_t e = f + uint64_t(index) * td::field_stride;
    if (!g_im.all.has(e, td::field_stride)) return KILN_ERR_NOT_FOUND;
    out->name = ks(str_at(g_im, e));
    const uint64_t ft = g_im.q(e + td::field_type);
    out->type = g_index_of.count(ft) ? handle(ft) : nullptr;
    out->offset = g_im.q(e + td::field_offset);
    return out->type ? KILN_OK : KILN_ERR_NOT_FOUND;
}
KilnResult KILN_CALL type_enum(const KilnType* type, uint32_t index, KilnStr* name, int64_t* value) {
    if (!known(type)) return KILN_ERR_NOT_FOUND;
    const uint64_t t = raw(type), e0 = g_im.q(t + td::enums);
    if (!e0 || index >= g_im.d(t + td::count)) return KILN_ERR_NOT_FOUND;
    const uint64_t e = e0 + uint64_t(index) * td::enum_stride;
    if (!g_im.all.has(e, td::enum_stride)) return KILN_ERR_NOT_FOUND;
    if (name) *name = ks(str_at(g_im, e));
    if (value) *value = static_cast<int64_t>(g_im.q(e + td::enum_value));
    return KILN_OK;
}
uint32_t KILN_CALL component_type_count() { return g_tt.component_count; }
const KilnType* KILN_CALL component_type_at(uint32_t i) {
    return i < g_tt.component_count ? handle(g_im.q(g_tt.components + 8ull * i)) : nullptr;
}
uint32_t KILN_CALL event_type_count() { return g_tt.event_count; }
const KilnType* KILN_CALL event_type_at(uint32_t i) {
    return i < g_tt.event_count ? handle(g_im.q(g_tt.events + 8ull * i)) : nullptr;
}

// ---- systems ----
int64_t sys_index_of_job(const void* job) {
    // job + 0x10 = the program entry; its update function at +0x28 identifies the system.
    const uint64_t prog = *reinterpret_cast<const uint64_t*>(reinterpret_cast<uintptr_t>(job) + 0x10);
    if (!prog) return -1;
    const uint64_t fn = *reinterpret_cast<const uint64_t*>(prog + 0x28);
    auto it = std::lower_bound(g_sys_by_fn.begin(), g_sys_by_fn.end(), std::pair<uint64_t, uint32_t>{fn, 0});
    return it != g_sys_by_fn.end() && it->first == fn ? int64_t(it->second) : -1;
}

void runner_detour(uintptr_t world, void* job, uint64_t index, uint64_t slot) {
    if (g_off_count.load(std::memory_order_relaxed)) {
        const int64_t i = sys_index_of_job(job);
        // Skipped whole: the game's "every entity updated" check lives in this function too, so it never runs.
        if (i >= 0 && g_off[size_t(i)].load(std::memory_order_relaxed)) return;
    }
    if (!g_prof.load(std::memory_order_relaxed)) return g_runner.call<void>(world, job, index, slot);
    LARGE_INTEGER a, b;
    QueryPerformanceCounter(&a);
    g_runner.call<void>(world, job, index, slot);
    QueryPerformanceCounter(&b);
    if (!g_prof.load(std::memory_order_relaxed)) return;
    const int64_t i = sys_index_of_job(job);
    if (i < 0) return;
    g_calls[size_t(i)].fetch_add(1, std::memory_order_relaxed);
    g_ticks[size_t(i)].fetch_add(uint64_t(b.QuadPart - a.QuadPart), std::memory_order_relaxed);
    const uint8_t kind = *reinterpret_cast<const uint8_t*>(world + 8);
    if (kind < 8) g_kinds[size_t(i)].fetch_or(uint8_t(1u << kind), std::memory_order_relaxed);
}

uint32_t KILN_CALL system_count() { return static_cast<uint32_t>(g_sys.size()); }
KilnResult KILN_CALL system_info(uint32_t i, KilnSystemInfo* out) {
    if (!out || out->size < sizeof(KilnSystemInfo)) return KILN_ERR_INVALID_ARGUMENT;
    if (i >= g_sys.size()) return KILN_ERR_NOT_FOUND;
    out->index = i;
    out->name = ks(g_sys[i].name);
    out->rva = g_sys[i].fn - g_im.base;
    out->enabled = !g_off[i].load();
    out->world_kinds = g_kinds[i].load();
    out->calls = g_calls[i].load();
    out->ms = double(g_ticks[i].load()) * 1000.0 / double(g_qpf);
    return KILN_OK;
}
int32_t KILN_CALL system_find(KilnStr name) {
    const std::string_view n = sv(name);
    auto it = std::lower_bound(g_sys.begin(), g_sys.end(), n, [](const System& s, std::string_view x) { return s.name < x; });
    return it != g_sys.end() && it->name == n ? int32_t(it - g_sys.begin()) : -1;
}
KilnResult KILN_CALL system_set_enabled(KilnOwner owner, uint32_t i, bool enabled) {
    if (!g_systems_ok) return KILN_ERR_UNAVAILABLE;
    if (i >= g_sys.size()) return KILN_ERR_NOT_FOUND;
    if (g_off[i].exchange(!enabled) == !enabled) return KILN_OK;
    if (enabled) g_off_count.fetch_sub(1);
    else g_off_count.fetch_add(1);
    logf(enabled ? KILN_LOG_INFO : KILN_LOG_WARN, "system %.*s switched %s (owner %llu); %u off", int(g_sys[i].name.size()),
         g_sys[i].name.data(), enabled ? "on" : "off", static_cast<unsigned long long>(owner), g_off_count.load());
    return KILN_OK;
}
KilnResult KILN_CALL profile(bool on) {
    if (!g_systems_ok) return KILN_ERR_UNAVAILABLE;
    LARGE_INTEGER n;
    QueryPerformanceCounter(&n);
    if (on) {
        for (size_t i = 0; i < g_sys.size(); ++i) g_calls[i] = 0, g_ticks[i] = 0, g_kinds[i] = 0;
        g_prof_start = n.QuadPart;
        g_prof_stop = 0;
    } else if (g_prof) {
        g_prof_stop = n.QuadPart;
    }
    g_prof = on;
    return KILN_OK;
}

// ---- events (guarded: a world pointer can be stale on a build Kiln has not seen) ----
template <class T>
T rd(uint64_t a) { return *reinterpret_cast<const T*>(a); }

// (world here is the ECS storage.) keen hash map: +8 occupancy bitmap, +0x10 capacity (power of two), +0x28 u32 keys, +0x40 values, +0x54 count.
int64_t map_find(uint64_t m, uint32_t k) {
    const uint64_t cap = rd<uint64_t>(m + 0x10);
    if (!rd<uint32_t>(m + 0x54) || !cap || (cap & (cap - 1))) return -1;
    const auto* used = rd<const uint64_t*>(m + 8);
    const auto* keys = rd<const uint32_t*>(m + 0x28);
    for (uint64_t s = k & (cap - 1), n = 0; n < cap && (used[s >> 6] >> (s & 63) & 1); ++n, s = (s + 1) & (cap - 1))
        if (keys[s] == k) return int64_t(s);
    return -1;
}

int event_index_raw(uint64_t world, uint32_t hash) {
    const uint64_t m = rd<uint64_t>(world) + uint64_t(g_ev_map);
    const int64_t s = map_find(m, hash);
    if (s < 0) return -1;
    const int idx = rd<const uint16_t*>(m + 0x40)[s];
    return uint64_t(idx) < rd<uint64_t>(world + uint64_t(g_ev_streams) + kStreamCount) ? idx : -1;
}

int event_index(uint64_t world, uint32_t hash) {
    __try {
        return event_index_raw(world, hash);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -2;
    }
}

struct EachArgs {
    uint64_t world;
    int idx;
    KilnEventFn fn;
    void* user;
};

void each_raw(const EachArgs* a) {
    const uint64_t st = rd<uint64_t>(a->world + uint64_t(g_ev_streams)) + uint64_t(a->idx) * kStreamStride;
    const uint64_t pool = rd<uint64_t>(st + 0x30);
    const uint32_t size = rd<uint32_t>(st + 0x14), count = rd<uint32_t>(st + 0x18);
    uint16_t page = rd<uint16_t>(st + 0x40);
    if (size == 0 || size >= 0x100) return;
    uint32_t off = 0;
    for (uint32_t i = 0; i < count && page; ++i) {
        if (off + size > kPageEnd) {
            page = rd<uint16_t>(pool + (page - 1u) * kPageSize + 2);
            off = 0;
            if (!page) return;
        }
        a->fn(reinterpret_cast<const void*>(pool + (page - 1u) * kPageSize + 8 + off), size, a->user);
        off += size;
    }
}

bool each_guarded(const EachArgs* a) {
    __try {
        each_raw(a);
        return true;
    } __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        return false;
    }
}

KilnResult KILN_CALL event_each(uintptr_t world, uint32_t hash, KilnEventFn fn, void* user) {
    if (!g_events_ok) return KILN_ERR_UNAVAILABLE;
    if (!world || !fn) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    world += uintptr_t(g_storage);
    const int idx = event_index(world, hash);
    if (idx == -2) return KILN_ERR_INTERNAL;
    if (idx < 0) return KILN_ERR_NOT_FOUND;
    const EachArgs a{world, idx, fn, user};
    return each_guarded(&a) ? KILN_OK : KILN_ERR_INTERNAL;
}

int post_raw(uint64_t world, uint32_t hash, const void* data, uint32_t size, int idx) {
    const uint32_t padded = (size + 7) & ~7u;
    const uint64_t st = rd<uint64_t>(world + uint64_t(g_ev_streams)) + uint64_t(idx) * kStreamStride;
    if (rd<uint32_t>(st + 0x14) > padded) return 1;  // the stream's element is bigger than what was given
    const uint64_t poster = rd<uint64_t>(world + uint64_t(g_ev_streams) + kPosters);
    auto* buf = poster ? rd<uint8_t*>(poster) : nullptr;
    const uint32_t used = poster ? rd<uint32_t>(poster + 0x10) : 0, end = used + 8 + padded;
    if (!buf || end > rd<uint64_t>(poster + 8)) return 2;
    uint8_t* rec = buf + used;
    std::memcpy(rec, &hash, 4);
    rec[4] = rec[5] = rec[7] = 0;
    rec[6] = static_cast<uint8_t>(size);
    std::memset(rec + 8, 0, padded);
    std::memcpy(rec + 8, data, size);
    *reinterpret_cast<uint32_t*>(poster + 0x10) = end;
    return 0;
}

int post_guarded(uint64_t world, uint32_t hash, const void* data, uint32_t size, int idx) {
    __try {
        return post_raw(world, hash, data, size, idx);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 3;
    }
}

KilnResult KILN_CALL event_post(uintptr_t world, uint32_t hash, const void* data, uint32_t size) {
    if (!g_events_ok) return KILN_ERR_UNAVAILABLE;
    if (!world || !data || size == 0 || size >= 0x100) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    world += uintptr_t(g_storage);
    const int idx = event_index(world, hash);
    if (idx == -2) return KILN_ERR_INTERNAL;
    if (idx < 0) return KILN_ERR_NOT_FOUND;
    switch (post_guarded(world, hash, data, size, idx)) {
        case 0: return KILN_OK;
        case 1: return KILN_ERR_INVALID_ARGUMENT;
        case 2: return KILN_ERR_BUFFER_TOO_SMALL;
        default: return KILN_ERR_INTERNAL;
    }
}

const KilnTypesApi kTable = {
    {sizeof(KilnTypesApi), KILN_TYPES_VERSION}, &status, &type_count, &type_at, &type_by_hash, &type_by_name, &type_info,
    &type_field, &type_enum, &component_type_count, &component_type_at, &event_type_count, &event_type_at,
    &system_count, &system_info, &system_find, &system_set_enabled, &profile, &event_each, &event_post,
};

bool sym(const char* n, uintptr_t* out) { return g_api->symbol(KilnStr{n, std::strlen(n)}, out) == KILN_OK; }
bool cst(const char* n, int64_t* out) { return g_api->constant(KilnStr{n, std::strlen(n)}, out) == KILN_OK; }
bool feat(const char* n) { return g_api->feature(KilnStr{n, std::strlen(n)}); }

void add_detail(const std::string& s) { g_detail += (g_detail.empty() ? "" : "; ") + s; }

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{KILN_TYPES_SERVICE, sizeof(KILN_TYPES_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    g_qpf = f.QuadPart ? f.QuadPart : 1;
    const auto t0 = std::chrono::steady_clock::now();
    if (!image_of_process(g_im)) {
        logf(KILN_LOG_ERROR, "types: the exe has no .text/.rdata: unavailable");
        return KILN_ERR_UNAVAILABLE;
    }
    g_tt = find_type_table(g_im);
    if (!g_tt.error.empty()) {
        add_detail("type table: " + g_tt.error);
        g_tt.count = 0;
    } else {
        index_types();
    }
    if (!g_tt.component_count) add_detail("the ECS component/event lists were not found");
    g_sys = scan_systems(g_im);
    const size_t n = g_sys.size();
    g_off = std::make_unique<std::atomic<bool>[]>(n);
    g_calls = std::make_unique<std::atomic<uint64_t>[]>(n);
    g_ticks = std::make_unique<std::atomic<uint64_t>[]>(n);
    g_kinds = std::make_unique<std::atomic<uint8_t>[]>(n);
    for (size_t i = 0; i < n; ++i) g_sys_by_fn.emplace_back(g_sys[i].fn, static_cast<uint32_t>(i));
    std::sort(g_sys_by_fn.begin(), g_sys_by_fn.end());
    uintptr_t runner = 0;
    if (n && feat("types.systems") && sym("types/ProgramRunner", &runner)) {
        g_runner = safetyhook::create_inline(reinterpret_cast<void*>(runner), reinterpret_cast<void*>(&runner_detour));
        g_systems_ok = static_cast<bool>(g_runner);
        if (!g_systems_ok) add_detail("hooking the program runner failed");
    } else {
        add_detail(n ? "the program runner was not found (sigdb types.systems)" : "no system descriptors found");
    }
    // The streams and the map pointer live in the world's ECS storage (world + world.storage, a core constant); the
    // event map is in the registry that storage points to.
    g_events_ok = feat("types.events") && cst("types/events.map", &g_ev_map) && cst("types/events.streams", &g_ev_streams) &&
                  cst("world.storage", &g_storage);
    if (g_events_ok) g_ev_streams += 0x28;  // the capture is the lea's displacement; the streams start 0x28 later
    else add_detail("event streams not found (sigdb types.events)");
    const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
    logf(g_detail.empty() ? KILN_LOG_INFO : KILN_LOG_WARN,
         "types: %u types (%u components, %u events), %zu systems%s, events %s; %.1f ms%s%s", g_tt.count,
         g_tt.component_count, g_tt.event_count, n, g_systems_ok ? " (runner hooked)" : " (switching off)",
         g_events_ok ? "on" : "off", ms, g_detail.empty() ? "" : "; ", g_detail.c_str());
    return api->register_service(owner, KilnStr{KILN_TYPES_SERVICE, sizeof(KILN_TYPES_SERVICE) - 1}, &kTable);
}
