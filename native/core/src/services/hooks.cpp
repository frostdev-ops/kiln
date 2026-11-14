// SPDX-License-Identifier: MIT
// kiln.hooks: the v1/v2 service table. Hooks live in the registry engine (hooks_chain.h: one physical detour per
// target, every handler a link in an ordered chain); this file is the C table around it: ABI argument handling, the
// v1 functions on top of the engine, SEH-guarded memory read/write, auto-restoring byte patches, image pattern scan,
// sigdb symbol/constant lookup (core + mod-shipped fragments), and the hook map (state/hooks.json).
//
// In-core service: registered with KILN_SERVICE, picked up by the CMake glob.
//
// Notes:
//  - The registry never removes a physical detour (removing one while threads run is riskier than an empty chain that
//    just reaches the original): revoking leaves the chain without the owner's links, atomically.
//  - The first link on a target stops the other threads while the detour is written and moves any thread paused inside the
//    replaced bytes (hooks_chain.cpp, enable_detour); later links on it stop nothing.
//  - hooks.json is written at boot end, at the first world, and when links change (at most once per second).
#include <algorithm>
#include <array>
#include <atomic>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

#include "../common.h"
#include "../image.h"
#include "../log.h"
#include "../registry.h"
#include "../sigdb.h"
#include "../tick.h"
#include "hooks_chain.h"
#include "hooks_internal.h"
#include "service.h"
#include "services/kiln_hooks.h"

namespace kiln::hooks {
namespace seh {
bool copy(void* dst, const void* src, size_t n);
}  // namespace seh
namespace {

struct Patch {
    KilnHandle handle = 0;
    KilnOwner owner = 0;
    uintptr_t addr = 0;
    std::vector<uint8_t> original;
};

// Process lifetime, just like the installed detours.
std::vector<Patch>& patches() {
    static auto& p = *new std::vector<Patch>;
    return p;
}
std::mutex g_mutex;  // patches
std::atomic<KilnHandle> g_next_handle{1};  // patch handles: small numbers; link handles carry their index in the high half

// Nothing may escape a C ABI function or a thread-pool callback: runs `f`, and on an exception (bad_alloc, a json error)
// logs once per site and returns `fallback`.
template <class R, class F>
R guarded(const char* what, std::atomic<bool>& once, R fallback, F&& f) noexcept {
    try {
        return f();
    } catch (const std::exception& e) {
        if (!once.exchange(true)) KLOG_E("hooks: %s failed: %s", what, e.what());
    } catch (...) {
        if (!once.exchange(true)) KLOG_E("hooks: %s failed (unknown exception)", what);
    }
    return fallback;
}
#define KILN_GUARD(what, fallback, ...)                                                                                \
    do {                                                                                                               \
        static std::atomic<bool> once_{false};                                                                         \
        return guarded(what, once_, static_cast<KilnResult>(fallback), [&]() -> KilnResult { __VA_ARGS__ });          \
    } while (0)

// ---- memory ----
bool poke(uintptr_t addr, const void* bytes, size_t n) {
    if (!addr || n == 0) return false;
    DWORD old = 0;
    if (!VirtualProtect(reinterpret_cast<void*>(addr), n, PAGE_EXECUTE_READWRITE, &old)) return false;
    const bool ok = seh::copy(reinterpret_cast<void*>(addr), bytes, n);
    FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<void*>(addr), n);
    DWORD tmp = 0;
    VirtualProtect(reinterpret_cast<void*>(addr), n, old, &tmp);
    return ok;
}

// ---- table functions ----
uintptr_t KILN_CALL h_image_base(void) { return image().base; }
size_t KILN_CALL h_image_size(void) { return image().size; }

KilnResult KILN_CALL h_symbol(KilnStr name, uintptr_t* out) {
    return sigdb::any_symbol(sv(name), out) ? KILN_OK : KILN_ERR_NOT_FOUND;
}
KilnResult KILN_CALL h_constant(KilnStr name, int64_t* out) {
    return sigdb::any_constant(sv(name), out) ? KILN_OK : KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL h_scan(KilnStr pattern, uintptr_t* out, uint32_t cap, uint32_t* count) {
    sigdb::Pattern p;
    std::string err;
    if (!sigdb::parse_pattern(sv(pattern), p, err)) return KILN_ERR_INVALID_ARGUMENT;
    // The exe file's code (sigdb.h), so another engine's hooks don't hide a match; the hits are live addresses.
    const auto hits = sigdb::scan_code(p, cap ? size_t{cap} + 1 : 64u);
    if (hits.empty()) {
        if (count) *count = 0;
        return KILN_ERR_NOT_FOUND;
    }
    const uint32_t writable = out ? std::min<uint32_t>(cap, static_cast<uint32_t>(hits.size())) : 0;
    for (uint32_t i = 0; i < writable; ++i) out[i] = hits[i];
    if (count) *count = writable;
    return hits.size() > cap ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult KILN_CALL h_scan_hole(KilnStr pattern, uintptr_t match, KilnStr hole, int64_t* out) {
    sigdb::Pattern p;
    std::string err;
    if (!sigdb::parse_pattern(sv(pattern), p, err)) return KILN_ERR_INVALID_ARGUMENT;
    if (match < image().base || match >= image().base + image().size) return KILN_ERR_INVALID_ARGUMENT;
    return sigdb::hole_value_at(p, match, sv(hole), out) ? KILN_OK : KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL h_load_symbols(KilnOwner owner, KilnStr modid, KilnStr json_path, KilnHooksSigSummary* summary) {
    const std::string id(sv(modid));
    const std::string path(sv(json_path));
    if (id.empty() || path.empty()) return KILN_ERR_INVALID_ARGUMENT;
    sigdb::FragmentSummary fs;
    std::string err;
    const auto report = paths().logs / (L"sigdb-" + to_wide(id) + L".json");
    if (!sigdb::load_fragment(id, to_wide(path), report, &fs, &err)) {
        KLOG_W("hooks: mod %s symbols rejected: %s", id.c_str(), err.c_str());
        return KILN_ERR_INVALID_ARGUMENT;
    }
    KLOG_I("hooks: mod %s symbols: %zu/%zu symbols, %zu/%zu constants, %zu/%zu features (%.1f ms)", id.c_str(),
           fs.symbols_ok, fs.symbols, fs.constants_ok, fs.constants, fs.features_ok, fs.features, fs.ms);
    for (const std::string& problem : fs.problems) KLOG_W("hooks: mod %s symbol %s", id.c_str(), problem.c_str());
    chain::refresh_symbols();  // hooked addresses may have a name now
    if (summary && summary->size >= sizeof(KilnHooksSigSummary)) {
        summary->symbols = static_cast<uint32_t>(fs.symbols);
        summary->symbols_ok = static_cast<uint32_t>(fs.symbols_ok);
        summary->constants = static_cast<uint32_t>(fs.constants);
        summary->constants_ok = static_cast<uint32_t>(fs.constants_ok);
        summary->features = static_cast<uint32_t>(fs.features);
        summary->features_ok = static_cast<uint32_t>(fs.features_ok);
    }
    (void)owner;
    return KILN_OK;
}

KilnResult KILN_CALL h_unload_symbols(KilnStr modid) {
    if (!sigdb::unload_fragment(sv(modid))) return KILN_ERR_NOT_FOUND;
    chain::refresh_symbols();
    return KILN_OK;
}

KilnResult KILN_CALL h_read(uintptr_t addr, void* dst, size_t n) {
    if (!dst || n == 0) return KILN_ERR_INVALID_ARGUMENT;
    return seh::copy(dst, reinterpret_cast<const void*>(addr), n) ? KILN_OK : KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL h_write(uintptr_t addr, const void* src, size_t n) {
    if (!src || n == 0) return KILN_ERR_INVALID_ARGUMENT;
    return poke(addr, src, n) ? KILN_OK : KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL h_patch(KilnOwner owner, uintptr_t addr, const void* bytes, size_t n, KilnHandle* out) {
    if (!bytes || n == 0 || n > 4096) return KILN_ERR_INVALID_ARGUMENT;
    std::vector<uint8_t> original(n);
    if (!seh::copy(original.data(), reinterpret_cast<const void*>(addr), n)) return KILN_ERR_NOT_FOUND;
    if (!poke(addr, bytes, n)) return KILN_ERR_INTERNAL;
    std::lock_guard lock(g_mutex);
    const KilnHandle handle = g_next_handle.fetch_add(1);
    patches().push_back(Patch{handle, owner, addr, std::move(original)});
    if (out) *out = handle;
    KLOG_I("hooks: patch %zu bytes at rva 0x%llx (handle %llu, owner %llu)", n,
           static_cast<unsigned long long>(addr - image().base), static_cast<unsigned long long>(handle),
           static_cast<unsigned long long>(owner));
    return KILN_OK;
}

// restore + drop a patch; g_mutex held
void restore_patch_locked(std::vector<Patch>::iterator it) {
    poke(it->addr, it->original.data(), it->original.size());
    patches().erase(it);
}

KilnResult KILN_CALL h_unpatch(KilnHandle h) {
    std::lock_guard lock(g_mutex);
    auto it = std::find_if(patches().begin(), patches().end(), [&](const Patch& p) { return p.handle == h; });
    if (it == patches().end()) return KILN_ERR_NOT_FOUND;
    restore_patch_locked(it);
    return KILN_OK;
}

// ---- hooks (the registry engine does the work) ----
bool valid_target(uintptr_t target) { return target >= image().base && target < image().base + image().size; }

chain::Order order_of(const KilnHookOrder* o) {
    chain::Order r;
    // read as far as the caller's `size` covers: a plugin built before `flags` was appended passes the smaller size
    constexpr uint32_t kBase = static_cast<uint32_t>(offsetof(KilnHookOrder, flags));
    if (o && o->size >= kBase) {
        r.priority = o->priority;
        r.label = std::string(sv(o->label));
        r.before = std::string(sv(o->before));
        r.after = std::string(sv(o->after));
        if (o->size >= kBase + sizeof(o->flags)) r.newest_first = (o->flags & KILN_HOOK_NEWEST_FIRST) != 0;
    }
    return r;
}

KilnResult KILN_CALL h_detour(KilnOwner owner, uintptr_t target, int32_t priority, KilnDetourFn fn, void* user,
                              KilnHandle* out) {
    if (!fn || !valid_target(target)) return KILN_ERR_INVALID_ARGUMENT;
    KILN_GUARD("detour", KILN_ERR_INTERNAL, return chain::install_v1(owner, target, priority, fn, user, out););
}

KilnResult KILN_CALL h_mid(KilnOwner owner, uintptr_t target, int32_t priority, KilnMidFn fn, void* user,
                           KilnHandle* out) {
    if (!fn || !valid_target(target)) return KILN_ERR_INVALID_ARGUMENT;
    KILN_GUARD("mid", KILN_ERR_INTERNAL, chain::Order o; o.priority = priority;
               return chain::install_mid(owner, target, o, fn, user, out););
}

uintptr_t KILN_CALL h_call_original(KilnHookCall* call) { return chain::call_original_v1(call); }

KilnResult KILN_CALL h_raw_detour(KilnOwner owner, uintptr_t target, const KilnHookOrder* order, void* fn,
                                  void** original, KilnHandle* out) {
    if (!fn || !valid_target(target)) return KILN_ERR_INVALID_ARGUMENT;
    KILN_GUARD("raw_detour", KILN_ERR_INTERNAL, return chain::install_raw(owner, target, order_of(order), fn, original, out););
}

uintptr_t KILN_CALL h_caller(KilnHandle h) { return chain::caller_of(h); }

KilnResult KILN_CALL h_detour2(KilnOwner owner, uintptr_t target, const KilnHookOrder* order, uint32_t arg_count,
                               KilnDetourFn2 fn, void* user, KilnHandle* out) {
    if (!fn || arg_count > 12 || !valid_target(target)) return KILN_ERR_INVALID_ARGUMENT;
    KILN_GUARD("detour2", KILN_ERR_INTERNAL,
               return chain::install_structured(owner, target, order_of(order), arg_count, fn, user, out););
}

uintptr_t KILN_CALL h_call_original2(KilnHookCall2* call) { return chain::call_original2(call); }

KilnResult KILN_CALL h_mid2(KilnOwner owner, uintptr_t target, const KilnHookOrder* order, KilnMidFn fn, void* user,
                            KilnHandle* out) {
    if (!fn || !valid_target(target)) return KILN_ERR_INVALID_ARGUMENT;
    KILN_GUARD("mid2", KILN_ERR_INTERNAL, return chain::install_mid(owner, target, order_of(order), fn, user, out););
}

// ---- typed links (dev.28) ----
// The positions a KilnSig describes (the sret buffer included), or -1 when it is malformed.
int sig_positions(const KilnSig* sig) {
    if (!sig || sig->size < sizeof(KilnSig)) return -1;
    if (sig->argc > KILN_SIG_MAX_ARGS || (sig->argc && !sig->args)) return -1;
    for (uint32_t i = 0; i < sig->argc; ++i) {
        const KilnSigArg& a = sig->args[i];
        if (a.kind < KILN_SIG_S8 || a.kind > KILN_SIG_BYVAL) return -1;
        if (a.kind == KILN_SIG_BYVAL && a.size == 0) return -1;
        if (a.name.len && !a.name.ptr) return -1;
    }
    if (sig->ret_kind > KILN_SIG_STRUCT) return -1;
    if (sig->sret > 1) return -1;
    if (sig->sret && (sig->ret_kind != KILN_SIG_STRUCT || sig->ret_size == 0)) return -1;
    if (!sig->sret && sig->ret_kind == KILN_SIG_STRUCT && sig->ret_size != 1 && sig->ret_size != 2 &&
        sig->ret_size != 4 && sig->ret_size != 8)
        return -1;
    const uint32_t positions = sig->argc + sig->sret;
    return positions > KILN_SIG_MAX_ARGS ? -1 : static_cast<int>(positions);
}

KilnResult KILN_CALL h_detour_typed(KilnOwner owner, uintptr_t target, const KilnHookOrder* order, const KilnSig* sig,
                                    KilnTypedFn fn, void* user, KilnHandle* out) {
    const int positions = sig_positions(sig);
    if (!fn || positions < 0 || !valid_target(target)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t nstack = positions > 4 ? static_cast<uint32_t>(positions - 4) : 0;
    KILN_GUARD("detour_typed", KILN_ERR_INTERNAL,
               return chain::install_typed(owner, target, order_of(order), nstack, fn, user, out););
}

KilnResult KILN_CALL h_call_original_frame(KilnCallFrame* frame) { return chain::call_original_frame(frame); }

KilnResult KILN_CALL h_call_with_frame(uintptr_t fn, KilnCallFrame* frame, uint32_t nstack) {
    if (!fn || !frame || nstack > KILN_SIG_MAX_ARGS - 4 || (nstack && !frame->stack)) return KILN_ERR_INVALID_ARGUMENT;
    kiln_hk_call_frame(reinterpret_cast<void*>(fn), frame, nstack);
    return KILN_OK;
}

KilnResult KILN_CALL h_fingerprint(uintptr_t, char*, size_t, size_t*) { return KILN_ERR_UNAVAILABLE; }

KilnResult KILN_CALL h_revoke(KilnHandle handle) {
    if (chain::is_link_handle(handle)) KILN_GUARD("revoke", KILN_ERR_INTERNAL, return chain::revoke(handle););
    return h_unpatch(handle);
}

KilnResult KILN_CALL h_revoke_owner(KilnOwner owner) {
    static std::atomic<bool> once{false};
    size_t removed = guarded("revoke_owner", once, size_t{0}, [&] { return chain::revoke_owner(owner); });
    {
        std::lock_guard lock(g_mutex);
        auto& ps = patches();
        for (auto it = ps.begin(); it != ps.end();) {
            if (it->owner == owner) {
                poke(it->addr, it->original.data(), it->original.size());
                it = ps.erase(it);
                ++removed;
            } else {
                ++it;
            }
        }
    }
    if (removed) KLOG_I("hooks: revoked %zu hook(s)/patch(es) of owner %llu", removed,
                        static_cast<unsigned long long>(owner));
    return KILN_OK;
}

KilnResult KILN_CALL h_stats(KilnHooksStats* out) {
    constexpr uint32_t kV1 = static_cast<uint32_t>(offsetof(KilnHooksStats, raw_links));  // the v1 layout's size
    constexpr uint32_t kV2 = static_cast<uint32_t>(offsetof(KilnHooksStats, links_used));  // dev.21's first v2 layout
    constexpr uint32_t kLinks = static_cast<uint32_t>(offsetof(KilnHooksStats, targets_cap));  // ... with links_used / links_cap
    if (!out || out->size < kV1) return KILN_ERR_INVALID_ARGUMENT;
    static std::atomic<bool> once{false};
    chain::Stats c;
    if (!guarded("stats", once, false, [&] { return c = chain::stats(), true; })) return KILN_ERR_INTERNAL;
    out->detour_targets = c.detour_targets;
    out->mid_targets = c.mid_targets;
    out->handlers = c.links;
    {
        std::lock_guard lock(g_mutex);
        out->patches = static_cast<uint32_t>(patches().size());
    }
    out->detour_calls = c.detour_calls;
    out->mid_calls = c.mid_calls;
    if (out->size >= kV2) {
        out->raw_links = c.raw_links;
        out->foreign_before = c.foreign_before;
        out->foreign_over = c.foreign_over;
        out->targets = c.targets;
    }
    if (out->size >= kLinks) {
        out->links_used = c.links_used;
        out->links_cap = c.links_cap;
    }
    if (out->size >= kLinks + sizeof(out->targets_cap)) out->targets_cap = c.targets_cap;
    return KILN_OK;
}

// ---- the hook map ----
std::vector<chain::ForeignSym> foreign_symbols() {
    std::vector<chain::ForeignSym> out;
    for (const sigdb::LiveDiff& d : sigdb::live_differs()) {
        chain::ForeignSym f;
        f.symbol = d.symbol;
        f.rva = d.address - image().base;
        if (d.bytes_ok) f.dest = chain::analyze(d.at, d.bytes);
        if (f.dest.own) continue;  // a Kiln module's own jump (a fragment resolved after Kiln hooked)
        out.push_back(std::move(f));
    }
    return out;
}

std::mutex g_map_mutex;  // one writer at a time
std::atomic<bool> g_map_enabled{false};  // after boot end: changes arm the timer
std::atomic<bool> g_timer_armed{false};
std::atomic<bool> g_world_seen{false};
HANDLE g_timer = nullptr;

KilnResult KILN_CALL h_map_json(char* buf, size_t cap, size_t* len) {
    KILN_GUARD("map_json", KILN_ERR_INTERNAL, {
        const std::string text = chain::map_text(foreign_symbols(), "query");
        if (len) *len = text.size();
        if (!buf || cap < text.size() + 1) return KILN_ERR_BUFFER_TOO_SMALL;
        std::memcpy(buf, text.data(), text.size());
        buf[text.size()] = 0;
        return KILN_OK;
    });
}

void CALLBACK on_map_timer(PVOID, BOOLEAN) {
    g_timer_armed.store(false, std::memory_order_release);
    static std::atomic<bool> once{false};
    guarded("the hook-map timer", once, false, [] {
        chain::sweep();
        write_map("change");
        return true;
    });
}

// Called by the engine with its mutex held: only arms a one-shot timer, so a burst of installs writes once.
void map_changed() {
    if (!g_map_enabled.load(std::memory_order_acquire)) return;
    if (g_timer_armed.exchange(true, std::memory_order_acq_rel)) return;
    // The previous one-shot timer has fired (it cleared g_timer_armed first): free it without waiting for its callback
    // (non-blocking delete; the callback takes the registry mutex, which our caller may hold).
    if (g_timer) DeleteTimerQueueTimer(nullptr, g_timer, nullptr), g_timer = nullptr;
    if (!CreateTimerQueueTimer(&g_timer, nullptr, &on_map_timer, nullptr, 1000, 0, WT_EXECUTEONLYONCE))
        g_timer_armed.store(false, std::memory_order_release);
}

void world_check();

void KILN_CALL on_world_event(const KilnWorldEvent* e, void*) {
    if (!e || e->event != KILN_WORLD_STARTED || g_world_seen.exchange(true)) return;
    static std::atomic<bool> once{false};
    guarded("the first-world hook check", once, false, [] {
        world_check();
        return true;
    });
}

void world_check() {
    chain::refresh_symbols();
    chain::sweep();
    const size_t over = chain::check_foreign();
    chain::note_world_checked();
    const chain::Stats st = chain::stats();
    KLOG_I("hooks: first world: %u target(s), %u link(s); %zu hooked over by another module", st.targets, st.links, over);
    write_map("world");
}

// ---- wiring to sigdb / the owner registry ----
bool env_file_bytes(uintptr_t a, uint8_t* out, size_t n) { return sigdb::file_bytes(a, out, n); }
std::string env_symbol_at(uintptr_t a) { return sigdb::symbol_name_at(a); }
std::string env_owner_name(KilnOwner o) { return reg::owner_name(o); }

const KilnHooksApi kTable = {
    {sizeof(KilnHooksApi), KILN_HOOKS_VERSION},
    &h_image_base,
    &h_image_size,
    &h_symbol,
    &h_constant,
    &h_scan,
    &h_scan_hole,
    &h_load_symbols,
    &h_unload_symbols,
    &h_read,
    &h_write,
    &h_patch,
    &h_unpatch,
    &h_detour,
    &h_mid,
    &h_call_original,
    &h_revoke,
    &h_revoke_owner,
    &h_stats,
    // v2
    &h_raw_detour,
    &h_caller,
    &h_detour2,
    &h_call_original2,
    &h_mid2,
    &h_map_json,
    // typed links (dev.28)
    &h_detour_typed,
    &h_call_original_frame,
    &h_call_with_frame,
    &h_fingerprint,
};

std::mutex g_ready_mutex;

bool init() {
    if (!ensure_ready()) return false;
    chain::refresh_symbols();  // sigdb has resolved by now; links an early plugin placed get their target's name
    KilnHandle h = 0;
    tick::subscribe_world(0, &on_world_event, nullptr, &h);  // first world: foreign check + hooks.json
    KLOG_I("hooks: ready (v2 registry, %zu target slots)", chain::kMaxTargets);
    return true;
}

}  // namespace

// ---- the rest of the core ----
bool ensure_ready() {
    std::lock_guard lock(g_ready_mutex);
    if (chain::ready()) return true;
    if (!image().text || image().text_size == 0) {
        KLOG_W("hooks: no image text; kiln.hooks unavailable");
        return false;
    }
    chain::Env env;
    env.file_bytes = &env_file_bytes;
    env.symbol_at = &env_symbol_at;
    env.owner_name = &env_owner_name;
    env.image_lo = image().base;
    env.image_hi = image().base + image().size;
    env.changed = &map_changed;
    return chain::init(env);
}

void write_map(const char* phase) {
    if (!chain::ready()) return;
    static std::atomic<bool> once{false};
    guarded("writing state/hooks.json", once, false, [&] {
        std::lock_guard lock(g_map_mutex);
        const std::string text = chain::map_text(foreign_symbols(), phase);
        const auto file = paths().state / L"hooks.json";
        if (!write_file_atomic(file, text)) KLOG_W("hooks: cannot write %s", u8(file).c_str());
        return true;
    });
}

static void boot_check();

void boot_complete() {
    if (!chain::ready()) return;
    static std::atomic<bool> once{false};
    guarded("the boot-end hook check", once, false, [] {
        boot_check();
        return true;
    });
}

static void boot_check() {
    chain::refresh_symbols();
    chain::sweep();
    const size_t over = chain::check_foreign();
    g_map_enabled.store(true, std::memory_order_release);
    write_map("boot");
    const chain::Stats st = chain::stats();
    KLOG_I("hooks: %u target(s), %u link(s) (%u raw); %u chained over another module's hook (%u over Kiln's own), %zu "
           "hooked over by another module; map state/hooks.json",
           st.targets, st.links, st.raw_links, st.foreign_before, st.own_before, over);
}

void add_map_extension(void (*fn)(void* json, const char* phase)) { chain::add_map_extension(fn); }

void mark_map_dirty() { map_changed(); }

}  // namespace kiln::hooks

KILN_SERVICE(hooks, KILN_HOOKS_SERVICE, KILN_HOOKS_VERSION, &kiln::hooks::kTable, &kiln::hooks::init);

namespace kiln::hooks {
// dev.21 (WP-F4): early plugins (kiln_plugin_early) run before sigdb and the services, but ShroudForge's native runtime
// installs its engine hooks while it initialises there. boot() calls serve_early() before them so that api->service
// ("kiln.hooks", 2) answers already; init() later (svc::init_all) is unchanged and adds the first-world subscription.
// withdraw_early() takes the service away again when the start turns out to be safe idle (nothing is hooked then; links
// the early plugins already placed stay).
bool serve_early() {
    if (!ensure_ready()) return false;
    kiln_service_hooks.available = true;
    KLOG_I("hooks: serving early plugins (v2 registry, before sigdb and the services)");
    return true;
}

void withdraw_early() { kiln_service_hooks.available = false; }
}  // namespace kiln::hooks
