// SPDX-License-Identifier: MIT
// kiln.pacing: where the game waits, and how many worker threads its task system starts. Contract:
// native/core/abi/services/kiln_pacing.h; evidence: docs/re/pacing.md; usage: docs/kiln/modding/lifecycle.md (Pacing).
// The semantics are Enshwick's (MIT, crates/internal/loader/src/pacing.rs, v1.0.0-alpha.13): the same places, the same
// registers, the same meaning of the values; the sites are found by sigdb patterns (server.d/pacing.json) instead of
// Enshwick's build tables.
//
// Five mid-hooks, none of them in the game until something asks:
//   pacing/LoopWait     after the event loop clamped the ns left into ms: r9d = ms, rbx = ns left.
//   pacing/MessageWait  the client's second wait (handles and window messages): r8d = ms, rbx = ns left.
//   pacing/ServerWait   the server loop's call that takes its wait in r9 (ns): the receiver may rewrite it.
//   pacing/DefaultWorkers, pacing/BackgroundWorkers   the task system's constructor loads each group's worker limit into
//                       rax; the hook lowers it. Only a dedicated server: a client's pools started long ago.
// A hook, once in, stays in for the life of the process (never disabled or removed: removing it could take out another
// loader's hook that went on top, e.g. Enshwick's own, and a trampoline may still be running on another thread). While
// nothing listens a hook is one relaxed load and a return. The receiver (the managed half) runs inside the game's loops
// on the thread that waits, concurrently from several threads; this file takes no lock there.
#ifndef KILN_PACING_HOST_TEST
#include <windows.h>
#endif

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>

#include "kiln_api.h"
#ifdef KILN_PACING_HOST_TEST
namespace safetyhook {
struct Context {
    uintptr_t rax = 0, rbx = 0, r8 = 0, r9 = 0;
};
}  // namespace safetyhook
#else
#include "nlohmann/json.hpp"
#include "safetyhook.hpp"
#endif
#include "services/kiln_pacing.h"

namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
bool g_server = false;
std::mutex g_mu;  // places, caps, arm: never taken by a hook

void logf(int32_t level, const char* fmt, ...) {
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (n >= static_cast<int>(sizeof buf)) n = sizeof buf - 1;
    std::string line = "pacing: ";
    line.append(buf, static_cast<size_t>(n));
#ifdef KILN_PACING_HOST_TEST
    std::printf("[log %d] %s\n", level, line.c_str());
#else
    g_api->log(g_owner, level, KilnStr{line.data(), line.size()});
#endif
}

// ----------------------------------------------------------------------------------------------- the places ----

enum Place { kLoop, kMessage, kServerWait, kDefaultWorkers, kBackgroundWorkers, kPlaces };

struct Info {
    const char* symbol;  // sigdb name (server.d/pacing.json, client.d/pacing.json)
    const char* what;
    uint32_t bit;        // KILN_PACING_*
};
constexpr Info kInfo[kPlaces] = {
    {"pacing/LoopWait", "the event loop's wait on its handles", KILN_PACING_LOOP_WAIT},
    {"pacing/MessageWait", "the client loop's wait on handles and window messages", KILN_PACING_MESSAGE_WAIT},
    {"pacing/ServerWait", "the server loop's wait between two passes", KILN_PACING_SERVER_WAIT},
    {"pacing/DefaultWorkers", "the default task group's worker limit", KILN_PACING_TASK_WORKERS},
    {"pacing/BackgroundWorkers", "the background task group's worker limit", KILN_PACING_TASK_WORKERS},
};

struct Slot {
    uintptr_t at = 0;
    bool found = false;    // sigdb resolved it (and the side has it)
    bool hooked = false;   // the hook is in the game's code
    bool refused = false;  // putting the hook in failed: never retried
    void* hook = nullptr;  // safetyhook::MidHook, leaked on purpose (see above)
};
Slot g_slot[kPlaces];

// What the hooks read. Written under g_mu (or at boot), read anywhere.
std::atomic<uint32_t> g_armed{0};  // KILN_PACING_LOOP_WAIT | MESSAGE_WAIT | SERVER_WAIT: places that deliver
std::atomic<KilnPacingLoopWaitFn> g_loop_fn{nullptr};
std::atomic<KilnPacingServerWaitFn> g_server_fn{nullptr};
std::atomic<void*> g_user{nullptr};
std::atomic<uint32_t> g_cap_default{0}, g_cap_background{0};  // 0: left to the game
std::atomic<bool> g_tasks_started{false};
std::atomic<uint64_t> g_loop_calls{0}, g_server_calls{0};

// ----------------------------------------------------------------------------------------------- the hooks ----
// Observers of registers, mid-function: the game's own instructions run after them, with whatever registers say then.

void deliver_loop(uint32_t ms, int64_t left_ns, uint32_t kind, uint32_t bit) {
    if (!(g_armed.load(std::memory_order_relaxed) & bit)) return;
    const KilnPacingLoopWaitFn fn = g_loop_fn.load(std::memory_order_acquire);
    if (!fn) return;
    const KilnPacingLoopWait wait{sizeof(KilnPacingLoopWait), kind, ms, 0, left_ns};
    g_loop_calls.fetch_add(1, std::memory_order_relaxed);
    fn(&wait, g_user.load(std::memory_order_relaxed));
}

void hk_loop_wait(safetyhook::Context& c) {
    deliver_loop(static_cast<uint32_t>(c.r9), static_cast<int64_t>(c.rbx), KILN_PACING_KIND_HANDLES, KILN_PACING_LOOP_WAIT);
}

void hk_message_wait(safetyhook::Context& c) {
    deliver_loop(static_cast<uint32_t>(c.r8), static_cast<int64_t>(c.rbx), KILN_PACING_KIND_MESSAGES, KILN_PACING_MESSAGE_WAIT);
}

// What the loop may be told to wait: never negative as a signed value, never longer than KILN_PACING_MAX_WAIT_NS (the loop
// still wakes for its own timers, but a wild value is never what anyone meant).
uint64_t clamp_wait(uint64_t wait) { return wait > KILN_PACING_MAX_WAIT_NS ? KILN_PACING_MAX_WAIT_NS : wait; }

void hk_server_wait(safetyhook::Context& c) {
    if (!(g_armed.load(std::memory_order_relaxed) & KILN_PACING_SERVER_WAIT)) return;
    const KilnPacingServerWaitFn fn = g_server_fn.load(std::memory_order_acquire);
    if (!fn) return;
    const uint64_t before = c.r9;
    uint64_t wait = before;
    g_server_calls.fetch_add(1, std::memory_order_relaxed);
    fn(&wait, g_user.load(std::memory_order_relaxed));
    if (wait != before) c.r9 = static_cast<uintptr_t>(clamp_wait(wait));
}

// rax is the group's own limit, a byte the game loaded from its settings; the cap only lowers it.
uint64_t capped(uint64_t limit, uint32_t cap) { return cap != 0 && limit > cap ? cap : limit; }

void hk_default_workers(safetyhook::Context& c) {
    g_tasks_started.store(true, std::memory_order_relaxed);
    c.rax = static_cast<uintptr_t>(capped(c.rax, g_cap_default.load(std::memory_order_relaxed)));
}

void hk_background_workers(safetyhook::Context& c) {
    g_tasks_started.store(true, std::memory_order_relaxed);
    c.rax = static_cast<uintptr_t>(capped(c.rax, g_cap_background.load(std::memory_order_relaxed)));
}

using HookFn = void (*)(safetyhook::Context&);
constexpr HookFn kHooks[kPlaces] = {&hk_loop_wait, &hk_message_wait, &hk_server_wait, &hk_default_workers,
                                    &hk_background_workers};

// -------------------------------------------------------------------------------------------- installing ----

#ifdef KILN_PACING_HOST_TEST
bool g_test_refuse = false;
int g_test_installs = 0;
bool put_in(Slot& s, HookFn) {
    if (g_test_refuse) return false;
    ++g_test_installs;
    s.hook = &s;
    return true;
}
#else
// Where a jump at the start of the site leads, as "jmp to module+0xoffset": another loader's hook that is already there
// (Enshwick's is a 5-byte jmp to a relay in its own image). Empty when the site holds the game's own instruction.
std::string foreign_jump(uintptr_t at) {
    const uint8_t* p = reinterpret_cast<const uint8_t*>(at);
    uintptr_t target = 0;
    if (p[0] == 0xE9) {
        int32_t rel = 0;
        std::memcpy(&rel, p + 1, sizeof rel);
        target = at + 5 + static_cast<intptr_t>(rel);
    } else if (p[0] == 0xFF && p[1] == 0x25) {
        int32_t rel = 0;
        std::memcpy(&rel, p + 2, sizeof rel);
        std::memcpy(&target, reinterpret_cast<const void*>(at + 6 + static_cast<intptr_t>(rel)), sizeof target);
    } else {
        return {};
    }
    HMODULE mod = nullptr;
    char name[MAX_PATH] = "?";
    uintptr_t base = 0;
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(target), &mod) && mod) {
        base = reinterpret_cast<uintptr_t>(mod);
        wchar_t wide[MAX_PATH];
        if (const DWORD n = GetModuleFileNameW(mod, wide, MAX_PATH); n > 0 && n < MAX_PATH) {
            const wchar_t* leaf = wide;
            for (const wchar_t* c = wide; *c; ++c)
                if (*c == L'\\' || *c == L'/') leaf = c + 1;
            WideCharToMultiByte(CP_UTF8, 0, leaf, -1, name, sizeof name, nullptr, nullptr);
        }
    }
    char text[MAX_PATH + 48];
    if (base == 0) std::snprintf(text, sizeof text, "jmp to 0x%llx, outside any module", static_cast<unsigned long long>(target));
    else std::snprintf(text, sizeof text, "jmp to %s+0x%llx", name, static_cast<unsigned long long>(target - base));
    return text;
}

// Created enabled, on the live bytes of the moment: if another loader's jump is already at the site, the decoder moves it
// into this hook's trampoline and both run (docs/kiln/engines/enshwick.md, "Hooks next to Kiln").
bool put_in(Slot& s, HookFn fn) {
    const std::string other = foreign_jump(s.at);
    if (!other.empty()) logf(KILN_LOG_INFO, "%s is already hooked by someone else (%s); this hook chains with it", kInfo[&s - g_slot].symbol, other.c_str());
    auto hook = safetyhook::MidHook::create(reinterpret_cast<void*>(s.at), fn);
    if (!hook) return false;
    s.hook = new safetyhook::MidHook(std::move(*hook));
    return true;
}
#endif

// Under g_mu. True when the place's hook is in the game's code.
bool install(Place p) {
    Slot& s = g_slot[p];
    if (s.hooked) return true;
    if (!s.found || s.refused) return false;
    if (!put_in(s, kHooks[p])) {
        s.refused = true;
        logf(KILN_LOG_WARN, "the hook on %s (%s) was refused; that place stays unpaced", kInfo[p].symbol, kInfo[p].what);
        return false;
    }
    s.hooked = true;
    logf(KILN_LOG_INFO, "hook on %s (%s) at 0x%llx", kInfo[p].symbol, kInfo[p].what, static_cast<unsigned long long>(s.at));
    return true;
}

uint32_t found_mask() {
    uint32_t m = 0;
    for (int p = 0; p < kPlaces; ++p)
        if (g_slot[p].found && !g_slot[p].refused) m |= kInfo[p].bit;
    // the worker caps need both groups' limits
    if (!(g_slot[kDefaultWorkers].found && g_slot[kBackgroundWorkers].found && g_server)) m &= ~static_cast<uint32_t>(KILN_PACING_TASK_WORKERS);
    return m;
}

// ---------------------------------------------------------------------------------------------- the table ----

bool tasks_started() {
    if (g_tasks_started.load(std::memory_order_relaxed)) return true;
#ifndef KILN_PACING_HOST_TEST
    // The pools start before the first server step; a step seen means they have.
    KilnTickInfo t{};
    t.size = sizeof t;
    if (g_api->tick_info && g_api->tick_info(&t) == KILN_OK && t.step >= 1) return true;
#endif
    return false;
}

uint32_t KILN_CALL t_sites() {
    std::lock_guard lk(g_mu);
    return found_mask();
}

KilnResult KILN_CALL t_set_receiver(KilnPacingLoopWaitFn loop, KilnPacingServerWaitFn server, void* user) {
    g_user.store(user, std::memory_order_relaxed);
    g_loop_fn.store(loop, std::memory_order_release);
    g_server_fn.store(server, std::memory_order_release);
    return KILN_OK;
}

uint32_t KILN_CALL t_arm(uint32_t sites) {
    std::lock_guard lk(g_mu);
    uint32_t now = 0;
    for (const Place p : {kLoop, kMessage, kServerWait})
        if ((sites & kInfo[p].bit) != 0 && install(p)) now |= kInfo[p].bit;
    g_armed.store(now, std::memory_order_release);
    return now;
}

void lower(std::atomic<uint32_t>& cap, uint32_t to) {
    const uint32_t now = cap.load();
    if (now == 0 || to < now) cap.store(to);
}

KilnResult KILN_CALL t_cap_task_workers(uint32_t default_workers, uint32_t background_workers) {
    if (default_workers == 0 && background_workers == 0) return KILN_ERR_INVALID_ARGUMENT;
    // the game's limits are bytes
    default_workers = std::min<uint32_t>(default_workers, 255);
    background_workers = std::min<uint32_t>(background_workers, 255);
    std::lock_guard lk(g_mu);
    if ((found_mask() & KILN_PACING_TASK_WORKERS) == 0 || tasks_started()) return KILN_ERR_UNAVAILABLE;
    bool any = false;
    if (default_workers != 0 && install(kDefaultWorkers)) {
        lower(g_cap_default, default_workers);
        any = true;
    }
    if (background_workers != 0 && install(kBackgroundWorkers)) {
        lower(g_cap_background, background_workers);
        any = true;
    }
    return any ? KILN_OK : KILN_ERR_UNAVAILABLE;
}

KilnResult KILN_CALL t_status(KilnPacingStatus* out) {
    if (!out || out->size < sizeof(KilnPacingStatus)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    std::lock_guard lk(g_mu);
    out->found = found_mask();
    out->armed = g_armed.load();
    for (int p = 0; p < kPlaces; ++p) {
        if (g_slot[p].hooked) out->hooked |= kInfo[p].bit;
        if (g_slot[p].refused) out->refused |= kInfo[p].bit;
    }
    out->default_cap = g_cap_default.load();
    out->background_cap = g_cap_background.load();
    out->task_system_started = tasks_started() ? 1 : 0;
    out->loop_calls = g_loop_calls.load();
    out->server_calls = g_server_calls.load();
    return KILN_OK;
}

const KilnPacingApi kTable = {{sizeof(KilnPacingApi), KILN_PACING_VERSION}, &t_sites, &t_set_receiver, &t_arm,
                              &t_cap_task_workers, &t_status};

#ifndef KILN_PACING_HOST_TEST

// Process exit: the managed receiver may be gone by the time a game thread waits once more. The hooks stay in the code
// (they cannot be taken out safely) but deliver nothing.
void KILN_CALL on_unload(void*) {
    g_armed.store(0, std::memory_order_release);
    g_loop_fn.store(nullptr, std::memory_order_release);
    g_server_fn.store(nullptr, std::memory_order_release);
}

bool sym(const char* name, uintptr_t* out) { return g_api->symbol(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }
bool feat(const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); }

// <config>/kiln-pacing.json, the operator's caps: {"taskWorkers": 4} or {"taskWorkers": {"default": 4, "background": 2}}.
// A Kiln mod loads after the game's task system has started, so this is where a cap for the pools is set on a Kiln server.
void read_config() {
    char b[1024];
    size_t n = 0;
    if (g_api->path(KILN_PATH_CONFIG, b, sizeof b, &n) != KILN_OK || n == 0 || n >= sizeof b) return;
    try {
        const std::string dir(b, n);
        const std::filesystem::path file = std::filesystem::path(std::u8string(dir.begin(), dir.end())) / "kiln-pacing.json";
        std::ifstream in(file, std::ios::binary);
        if (!in) return;
        const nlohmann::json doc = nlohmann::json::parse(in, nullptr, true, true);
        if (!doc.is_object() || !doc.contains("taskWorkers")) return;
        const auto& tw = doc["taskWorkers"];
        uint32_t d = 0, bg = 0;
        auto number = [](const nlohmann::json& v) -> uint32_t {
            return v.is_number_integer() && v.get<int64_t>() > 0 ? static_cast<uint32_t>(std::min<int64_t>(v.get<int64_t>(), 255)) : 0;
        };
        if (tw.is_object()) {
            d = tw.contains("default") ? number(tw["default"]) : 0;
            bg = tw.contains("background") ? number(tw["background"]) : 0;
        } else {
            d = bg = number(tw);
        }
        if (d == 0 && bg == 0) {
            logf(KILN_LOG_WARN, "kiln-pacing.json: taskWorkers wants a whole number of 1 or more (or {\"default\": n, \"background\": n}); ignored");
            return;
        }
        const KilnResult rc = t_cap_task_workers(d, bg);
        logf(rc == KILN_OK ? KILN_LOG_INFO : KILN_LOG_WARN, "kiln-pacing.json: task workers capped at default %u, background %u: %s", d, bg,
             rc == KILN_OK ? "the game's task system starts with at most that many" : "not applied (no such place on this build, or the pools have started)");
    } catch (const std::exception& e) {
        logf(KILN_LOG_WARN, "kiln-pacing.json is not usable (%s); ignored", e.what());
    }
}

#endif  // !KILN_PACING_HOST_TEST

}  // namespace

#ifndef KILN_PACING_HOST_TEST
extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < offsetof(KilnApi, side) + sizeof(void*)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    g_server = api->side() == KILN_SIDE_SERVER;
    info->id = KilnStr{KILN_PACING_SERVICE, sizeof(KILN_PACING_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    info->on_unload = &on_unload;

    for (int p = 0; p < kPlaces; ++p) {
        uintptr_t at = 0;
        if (sym(kInfo[p].symbol, &at) && at != 0) {
            g_slot[p].at = at;
            g_slot[p].found = true;
        }
    }
    // The game's task system exists only on a server in a way a mod could reach (a client's pools started before its window).
    if (!g_server) g_slot[kDefaultWorkers].found = g_slot[kBackgroundWorkers].found = false;
    const uint32_t found = found_mask();
    if (found == 0) {
        logf(KILN_LOG_WARN, "none of the game's waits or task limits were found on this build (features %s %s %s %s): kiln.pacing is off",
             feat("pacing.loop_wait") ? "loop_wait" : "-", feat("pacing.message_wait") ? "message_wait" : "-",
             feat("pacing.server_wait") ? "server_wait" : "-", feat("pacing.task_workers") ? "task_workers" : "-");
        return KILN_ERR_UNAVAILABLE;
    }
    logf(KILN_LOG_INFO, "found: loop wait %s, message wait %s, server wait %s, task workers %s; hooks go in when a mod or kiln-pacing.json asks",
         found & KILN_PACING_LOOP_WAIT ? "yes" : "no", found & KILN_PACING_MESSAGE_WAIT ? "yes" : "no",
         found & KILN_PACING_SERVER_WAIT ? "yes" : "no", found & KILN_PACING_TASK_WORKERS ? "yes" : "no");
    read_config();
    // Lab only: put the wait hooks in at boot instead of when a mod asks, to see what another loader that starts after Kiln
    // (kiln-enshwick "start": "ready") makes of sites Kiln already hooked. The hooks stay inert until a receiver is armed.
    char lab[16];
    if (const DWORD n = GetEnvironmentVariableA("KILN_PACING_LAB", lab, sizeof lab); n > 0 && n < sizeof lab && std::strcmp(lab, "boot") == 0) {
        const uint32_t armed = t_arm(KILN_PACING_LOOP_WAIT | KILN_PACING_MESSAGE_WAIT | KILN_PACING_SERVER_WAIT);
        g_armed.store(0);
        logf(KILN_LOG_INFO, "KILN_PACING_LAB=boot: wait hooks put in at boot (places 0x%x), inert until a mod listens", armed);
    }
    return api->register_service(owner, KilnStr{KILN_PACING_SERVICE, sizeof(KILN_PACING_SERVICE) - 1}, &kTable);
}
#endif  // !KILN_PACING_HOST_TEST
