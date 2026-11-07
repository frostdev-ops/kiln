// SPDX-License-Identifier: MIT
// kiln.hooks: inline detours (callable original, priority chains), mid-function hooks (full register context),
// SEH-guarded memory read/write, auto-restoring byte patches, image pattern scan and sigdb symbol/constant lookup
// (core + mod-shipped fragments). In-core service: registered with KILN_SERVICE, picked up by the CMake glob.
//
// Design notes:
//  - One inline detour per target address. Several handlers on the same target form a priority chain (highest first,
//    stable within a priority). A handler calls call_original to run the rest of the chain and then the real
//    function, or returns without it to replace the function. The detour destination is a compile-time slot function
//    (detour_dest<N>); dispatch reads an immutable, atomically-swapped snapshot of the chain, so revoking a mod's
//    handlers never races a dispatch on a game thread. The safetyhook detour itself is never removed (removing a hook
//    while threads run is riskier than leaving an empty chain that just calls the original).
//  - safetyhook freezes the other threads while installing, so a hook can go in while the game runs.
#include <algorithm>
#include <array>
#include <atomic>
#include <cstddef>
#include <cstdio>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

#include "../common.h"
#include "../image.h"
#include "../log.h"
#include "../sigdb.h"
#include "safetyhook.hpp"
#include "service.h"
#include "services/kiln_hooks.h"

namespace kiln::hooks {
namespace {

constexpr size_t kMaxDetourTargets = 256;
constexpr size_t kMaxMidTargets = 128;

// SEH primitives (hooks_seh.cpp).
}  // namespace
namespace seh {
bool call_detour(KilnDetourFn fn, KilnHookCall* call, void* user, uintptr_t* out, unsigned long* code);
bool call_mid(KilnMidFn fn, KilnRegisters* regs, void* user, unsigned long* code);
bool copy(void* dst, const void* src, size_t n);
}  // namespace seh
namespace {

struct Handler {
    KilnHandle handle = 0;
    KilnOwner owner = 0;
    int32_t priority = 0;
    void* fn = nullptr;   // KilnDetourFn or KilnMidFn
    void* user = nullptr;
    std::atomic<bool> live{true};
};
using Chain = std::vector<std::shared_ptr<Handler>>;

// Dispatch reads the live chain through a plain atomic raw pointer (genuinely lock-free on x64 — no internal
// spinlock, unlike std::atomic<shared_ptr>, whose spinlock could be held by a game thread the CLR then suspends for a
// collectible-context unload, deadlocking the tick thread). Replaced chains are retired and freed later on the tick
// thread, after a grace period long enough that no in-flight dispatch can still hold the old pointer.
struct DetourSlot {
    void* target = nullptr;
    bool used = false;
    safetyhook::InlineHook hook;
    std::vector<std::shared_ptr<Handler>> handlers;  // guarded by g_mutex
    std::atomic<const Chain*> chain{nullptr};        // snapshot for dispatch (never null-dereferenced)
};

struct MidSlot {
    void* target = nullptr;
    bool used = false;
    safetyhook::MidHook hook;
    std::vector<std::shared_ptr<Handler>> handlers;
    std::atomic<const Chain*> chain{nullptr};
};

struct Patch {
    KilnHandle handle = 0;
    KilnOwner owner = 0;
    uintptr_t addr = 0;
    std::vector<uint8_t> original;
};

enum class Kind { Detour, Mid, Patch };
struct Desc {
    Kind kind;
    size_t index;       // slot index (Detour/Mid) or position is looked up for Patch by handle
    KilnOwner owner;
};

// Process lifetime, just like the installed detours: never let static destruction restore through a later
// engine's jump or free a trampoline that engine may still call.
auto& g_detours = *new std::array<DetourSlot, kMaxDetourTargets>;
auto& g_mids = *new std::array<MidSlot, kMaxMidTargets>;
std::vector<Patch> g_patches;

std::mutex g_mutex;
std::atomic<KilnHandle> g_next_handle{1};
std::map<KilnHandle, Desc> g_by_handle;

std::atomic<uint64_t> g_detour_calls{0}, g_mid_calls{0};

// Retired chains awaiting free; drained on the tick thread (under g_mutex) once older than the grace period.
constexpr double kChainGraceMs = 1000.0;
std::vector<std::pair<double, std::unique_ptr<const Chain>>> g_retired;

// g_mutex held. Publishes a fresh immutable snapshot (live handlers, highest priority first) and retires the old one.
void rebuild(std::vector<std::shared_ptr<Handler>>& handlers, std::atomic<const Chain*>& out) {
    auto chain = std::make_unique<Chain>();
    for (auto& h : handlers)
        if (h->live.load(std::memory_order_acquire)) chain->push_back(h);
    std::stable_sort(chain->begin(), chain->end(),
                     [](const std::shared_ptr<Handler>& a, const std::shared_ptr<Handler>& b) {
                         return a->priority > b->priority;
                     });
    const Chain* old = out.exchange(chain.release(), std::memory_order_acq_rel);
    if (old) g_retired.emplace_back(now_ms(), std::unique_ptr<const Chain>(old));
}

// g_mutex held, tick thread. Frees retired chains no dispatch can still be reading.
void reclaim_chains() {
    const double now = now_ms();
    std::erase_if(g_retired, [&](const auto& e) { return now - e.first > kChainGraceMs; });
}

uintptr_t dispatch_detour(size_t slot, uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d);
void dispatch_mid(size_t slot, safetyhook::Context& ctx);

// ---- compile-time slot destinations ----
template <size_t N>
uintptr_t __fastcall detour_dest(uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d) {
    return dispatch_detour(N, a, b, c, d);
}
template <size_t N>
void mid_dest(safetyhook::Context& ctx) {
    dispatch_mid(N, ctx);
}
using DetourDest = uintptr_t(__fastcall*)(uintptr_t, uintptr_t, uintptr_t, uintptr_t);
template <size_t... I>
std::array<DetourDest, sizeof...(I)> make_detour_dests(std::index_sequence<I...>) {
    return {{&detour_dest<I>...}};
}
template <size_t... I>
std::array<safetyhook::MidHookFn, sizeof...(I)> make_mid_dests(std::index_sequence<I...>) {
    return {{&mid_dest<I>...}};
}
const auto g_detour_dest = make_detour_dests(std::make_index_sequence<kMaxDetourTargets>{});
const auto g_mid_dest = make_mid_dests(std::make_index_sequence<kMaxMidTargets>{});

// KilnRegisters must alias safetyhook::Context64 with no copy.
static_assert(sizeof(KilnRegisters) == sizeof(safetyhook::Context64), "KilnRegisters must mirror Context64");
static_assert(offsetof(KilnRegisters, rax) == offsetof(safetyhook::Context64, rax), "rax offset");
static_assert(offsetof(KilnRegisters, rcx) == offsetof(safetyhook::Context64, rcx), "rcx offset");
static_assert(offsetof(KilnRegisters, rip) == offsetof(safetyhook::Context64, rip), "rip offset");

struct DetourCursor {
    const Chain* chain;
    size_t pos;
    DetourSlot* slot;
};

uintptr_t invoke_detour(DetourCursor* c, KilnHookCall* call) {
    for (;;) {
        const size_t i = c->pos++;
        if (c->chain && i < c->chain->size()) {
            const std::shared_ptr<Handler>& h = (*c->chain)[i];
            if (!h->live.load(std::memory_order_acquire)) continue;
            call->hook = h->handle;
            uintptr_t ret = 0;
            unsigned long code = 0;
            if (seh::call_detour(reinterpret_cast<KilnDetourFn>(h->fn), call, h->user, &ret, &code)) return ret;
            h->live.store(false, std::memory_order_release);
            KLOG_E("hooks: detour handle %llu faulted (0x%08lx) and was disabled",
                   static_cast<unsigned long long>(h->handle), code);
            continue;  // skip to the next handler / the original
        }
        return c->slot->hook.call<uintptr_t>(call->args[0], call->args[1], call->args[2], call->args[3]);
    }
}

uintptr_t dispatch_detour(size_t slot, uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d) {
    DetourSlot& s = g_detours[slot];
    g_detour_calls.fetch_add(1, std::memory_order_relaxed);
    const Chain* chain = s.chain.load(std::memory_order_acquire);
    DetourCursor cur{chain, 0, &s};
    KilnHookCall call{};
    call.size = sizeof call;
    call.arg_count = 4;
    call.hook = 0;
    call.args[0] = a;
    call.args[1] = b;
    call.args[2] = c;
    call.args[3] = d;
    call.reserved = &cur;
    return invoke_detour(&cur, &call);
}

void dispatch_mid(size_t slot, safetyhook::Context& ctx) {
    MidSlot& s = g_mids[slot];
    g_mid_calls.fetch_add(1, std::memory_order_relaxed);
    const Chain* chain = s.chain.load(std::memory_order_acquire);
    if (!chain) return;
    auto* regs = reinterpret_cast<KilnRegisters*>(&ctx);
    for (const std::shared_ptr<Handler>& h : *chain) {
        if (!h->live.load(std::memory_order_acquire)) continue;
        unsigned long code = 0;
        if (!seh::call_mid(reinterpret_cast<KilnMidFn>(h->fn), regs, h->user, &code)) {
            h->live.store(false, std::memory_order_release);
            KLOG_E("hooks: mid handle %llu faulted (0x%08lx) and was disabled",
                   static_cast<unsigned long long>(h->handle), code);
        }
    }
}

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
    return sigdb::unload_fragment(sv(modid)) ? KILN_OK : KILN_ERR_NOT_FOUND;
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
    g_patches.push_back(Patch{handle, owner, addr, std::move(original)});
    g_by_handle[handle] = Desc{Kind::Patch, 0, owner};
    if (out) *out = handle;
    KLOG_I("hooks: patch %zu bytes at rva 0x%llx (handle %llu, owner %llu)", n,
           static_cast<unsigned long long>(addr - image().base), static_cast<unsigned long long>(handle),
           static_cast<unsigned long long>(owner));
    return KILN_OK;
}

// restore + drop a patch; g_mutex held
void restore_patch_locked(std::vector<Patch>::iterator it) {
    poke(it->addr, it->original.data(), it->original.size());
    g_by_handle.erase(it->handle);
    g_patches.erase(it);
}

KilnResult KILN_CALL h_unpatch(KilnHandle h) {
    std::lock_guard lock(g_mutex);
    auto it = std::find_if(g_patches.begin(), g_patches.end(), [&](const Patch& p) { return p.handle == h; });
    if (it == g_patches.end()) return KILN_ERR_NOT_FOUND;
    restore_patch_locked(it);
    return KILN_OK;
}

// Find (or allocate + install) the slot for a detour target. g_mutex held. Returns slot index or -1.
long detour_slot_for(void* target) {
    for (size_t i = 0; i < g_detours.size(); ++i)
        if (g_detours[i].used && g_detours[i].target == target) return static_cast<long>(i);
    for (size_t i = 0; i < g_detours.size(); ++i) {
        if (g_detours[i].used) continue;
        auto hook = safetyhook::create_inline(target, reinterpret_cast<void*>(g_detour_dest[i]));
        if (!hook) {
            KLOG_E("hooks: safetyhook could not detour %p", target);
            return -1;
        }
        g_detours[i].hook = std::move(hook);
        g_detours[i].target = target;
        g_detours[i].used = true;
        return static_cast<long>(i);
    }
    KLOG_W("hooks: no free detour slot (%zu in use)", kMaxDetourTargets);
    return -1;
}

long mid_slot_for(void* target) {
    for (size_t i = 0; i < g_mids.size(); ++i)
        if (g_mids[i].used && g_mids[i].target == target) return static_cast<long>(i);
    for (size_t i = 0; i < g_mids.size(); ++i) {
        if (g_mids[i].used) continue;
        auto hook = safetyhook::create_mid(target, g_mid_dest[i]);
        if (!hook) {
            KLOG_E("hooks: safetyhook could not mid-hook %p", target);
            return -1;
        }
        g_mids[i].hook = std::move(hook);
        g_mids[i].target = target;
        g_mids[i].used = true;
        return static_cast<long>(i);
    }
    KLOG_W("hooks: no free mid slot (%zu in use)", kMaxMidTargets);
    return -1;
}

KilnResult KILN_CALL h_detour(KilnOwner owner, uintptr_t target, int32_t priority, KilnDetourFn fn, void* user,
                              KilnHandle* out) {
    if (!fn || target < image().base || target >= image().base + image().size) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mutex);
    reclaim_chains();
    const long slot = detour_slot_for(reinterpret_cast<void*>(target));
    if (slot < 0) return KILN_ERR_UNAVAILABLE;
    auto h = std::make_shared<Handler>();
    h->handle = g_next_handle.fetch_add(1);
    h->owner = owner;
    h->priority = priority;
    h->fn = reinterpret_cast<void*>(fn);
    h->user = user;
    g_detours[slot].handlers.push_back(h);
    rebuild(g_detours[slot].handlers, g_detours[slot].chain);
    g_by_handle[h->handle] = Desc{Kind::Detour, static_cast<size_t>(slot), owner};
    if (out) *out = h->handle;
    KLOG_I("hooks: detour on rva 0x%llx prio %d (handle %llu, owner %llu, %zu on target)",
           static_cast<unsigned long long>(target - image().base), priority,
           static_cast<unsigned long long>(h->handle), static_cast<unsigned long long>(owner),
           g_detours[slot].handlers.size());
    return KILN_OK;
}

KilnResult KILN_CALL h_mid(KilnOwner owner, uintptr_t target, int32_t priority, KilnMidFn fn, void* user,
                           KilnHandle* out) {
    if (!fn || target < image().base || target >= image().base + image().size) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mutex);
    reclaim_chains();
    const long slot = mid_slot_for(reinterpret_cast<void*>(target));
    if (slot < 0) return KILN_ERR_UNAVAILABLE;
    auto h = std::make_shared<Handler>();
    h->handle = g_next_handle.fetch_add(1);
    h->owner = owner;
    h->priority = priority;
    h->fn = reinterpret_cast<void*>(fn);
    h->user = user;
    g_mids[slot].handlers.push_back(h);
    rebuild(g_mids[slot].handlers, g_mids[slot].chain);
    g_by_handle[h->handle] = Desc{Kind::Mid, static_cast<size_t>(slot), owner};
    if (out) *out = h->handle;
    KLOG_I("hooks: mid hook on rva 0x%llx prio %d (handle %llu, owner %llu)",
           static_cast<unsigned long long>(target - image().base), priority,
           static_cast<unsigned long long>(h->handle), static_cast<unsigned long long>(owner));
    return KILN_OK;
}

uintptr_t KILN_CALL h_call_original(KilnHookCall* call) {
    if (!call || !call->reserved) return 0;
    return invoke_detour(static_cast<DetourCursor*>(call->reserved), call);
}

// Remove a handler from a slot's list; g_mutex held.
bool remove_handler_locked(std::vector<std::shared_ptr<Handler>>& handlers, KilnHandle handle) {
    auto it = std::find_if(handlers.begin(), handlers.end(),
                           [&](const std::shared_ptr<Handler>& h) { return h->handle == handle; });
    if (it == handlers.end()) return false;
    (*it)->live.store(false, std::memory_order_release);
    handlers.erase(it);
    return true;
}

KilnResult KILN_CALL h_revoke(KilnHandle handle) {
    std::lock_guard lock(g_mutex);
    reclaim_chains();
    auto d = g_by_handle.find(handle);
    if (d == g_by_handle.end()) return KILN_ERR_NOT_FOUND;
    switch (d->second.kind) {
        case Kind::Detour: {
            auto& slot = g_detours[d->second.index];
            remove_handler_locked(slot.handlers, handle);
            rebuild(slot.handlers, slot.chain);
            break;
        }
        case Kind::Mid: {
            auto& slot = g_mids[d->second.index];
            remove_handler_locked(slot.handlers, handle);
            rebuild(slot.handlers, slot.chain);
            break;
        }
        case Kind::Patch: {
            auto it = std::find_if(g_patches.begin(), g_patches.end(),
                                   [&](const Patch& p) { return p.handle == handle; });
            if (it != g_patches.end()) restore_patch_locked(it);
            break;
        }
    }
    g_by_handle.erase(d);
    return KILN_OK;
}

KilnResult KILN_CALL h_revoke_owner(KilnOwner owner) {
    std::lock_guard lock(g_mutex);
    reclaim_chains();
    size_t removed = 0;
    for (auto& slot : g_detours) {
        const size_t before = slot.handlers.size();
        std::erase_if(slot.handlers, [&](const std::shared_ptr<Handler>& h) {
            if (h->owner != owner) return false;
            h->live.store(false, std::memory_order_release);
            g_by_handle.erase(h->handle);
            return true;
        });
        if (slot.handlers.size() != before) rebuild(slot.handlers, slot.chain);
        removed += before - slot.handlers.size();
    }
    for (auto& slot : g_mids) {
        const size_t before = slot.handlers.size();
        std::erase_if(slot.handlers, [&](const std::shared_ptr<Handler>& h) {
            if (h->owner != owner) return false;
            h->live.store(false, std::memory_order_release);
            g_by_handle.erase(h->handle);
            return true;
        });
        if (slot.handlers.size() != before) rebuild(slot.handlers, slot.chain);
        removed += before - slot.handlers.size();
    }
    for (auto it = g_patches.begin(); it != g_patches.end();) {
        if (it->owner == owner) {
            poke(it->addr, it->original.data(), it->original.size());
            g_by_handle.erase(it->handle);
            it = g_patches.erase(it);
            ++removed;
        } else {
            ++it;
        }
    }
    if (removed) KLOG_I("hooks: revoked %zu hook(s)/patch(es) of owner %llu", removed,
                        static_cast<unsigned long long>(owner));
    return KILN_OK;
}

KilnResult KILN_CALL h_stats(KilnHooksStats* out) {
    if (!out || out->size < sizeof(KilnHooksStats)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mutex);
    out->detour_targets = out->mid_targets = out->handlers = 0;
    for (auto& s : g_detours)
        if (s.used) {
            ++out->detour_targets;
            out->handlers += static_cast<uint32_t>(s.handlers.size());
        }
    for (auto& s : g_mids)
        if (s.used) {
            ++out->mid_targets;
            out->handlers += static_cast<uint32_t>(s.handlers.size());
        }
    out->patches = static_cast<uint32_t>(g_patches.size());
    out->detour_calls = g_detour_calls.load(std::memory_order_relaxed);
    out->mid_calls = g_mid_calls.load(std::memory_order_relaxed);
    return KILN_OK;
}

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
};

bool init() {
    if (!image().text || image().text_size == 0) {
        KLOG_W("hooks: no image text; kiln.hooks unavailable");
        return false;
    }
    KLOG_I("hooks: ready (%zu detour slots, %zu mid slots)", kMaxDetourTargets, kMaxMidTargets);
    return true;
}

}  // namespace
}  // namespace kiln::hooks

KILN_SERVICE(hooks, KILN_HOOKS_SERVICE, KILN_HOOKS_VERSION, &kiln::hooks::kTable, &kiln::hooks::init);
