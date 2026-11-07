// SPDX-License-Identifier: MIT
#include "tick.h"

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <memory>
#include <mutex>
#include <vector>

#include "guard.h"
#include "image.h"
#include "log.h"
#include "registry.h"
#include "safetyhook.hpp"
#include "sigdb.h"
#include "worldinfo.h"

namespace kiln::tick {
namespace {

SafetyHookInline g_hook;
SafetyHookInline g_destroy_hook;
bool g_installed = false;
bool g_lifecycle = false;
std::atomic<bool> g_faulted{false};

void disable(const char* where) {
    if (g_faulted.exchange(true)) return;
    // With lifecycle delivery stopped these pointers must not outlive the game's worlds.
    for (uint32_t kind = 0; kind < KILN_WORLD_KINDS; ++kind) worldinfo::set_world(kind, 0);
    KLOG_E("tick: exception in %s; tick delivery disabled until restart", where);
}
int64_t g_world_storage = 0;  // world -> entity storage (the sim step's first argument)
int64_t g_off_kind = 0, g_off_clock = 0, g_off_period = -1;
using WaitWorldJobsFn = void(__cdecl*)(uintptr_t world);
WaitWorldJobsFn g_wait_jobs = nullptr;
bool g_post_warned = false;

std::atomic<DWORD> g_thread{0};  // the server world's thread (on_tick_thread)
std::mutex g_last_mutex;
KilnTickInfo g_last{};

uint64_t g_step = 0;
uint32_t g_prev_clock = 0;
double g_prev_start = 0;

uint64_t g_version = 0;
std::vector<std::shared_ptr<reg::TickSub>> g_pre, g_post;

struct Window {
    double start = 0;
    uint64_t steps = 0;
    uint64_t clock = 0;
    std::vector<float> step, pre, post, interval;
};
Window g_win;
std::FILE* g_stats = nullptr;
std::mutex g_stats_mutex;  // the server and the client worlds write tick-stats.jsonl from different threads

const char* kKindName[KILN_WORLD_KINDS] = {"server", "prediction", "client"};

// ---- world sessions (all kinds) ----
std::atomic<uint32_t> g_sessions[KILN_WORLD_KINDS]{};
std::atomic<uint64_t> g_steps[KILN_WORLD_KINDS]{};
std::atomic<DWORD> g_kind_thread[KILN_WORLD_KINDS]{};
std::atomic<int64_t> g_last_step_ms[KILN_WORLD_KINDS]{};
std::atomic<uint32_t> g_generation{0};

// ---- subscribers of client-side kinds and of world events (the registry keeps the server world's) ----
struct Sub {
    KilnHandle handle = 0;
    KilnOwner owner = 0;
    uint32_t kind = 0;
    int phase = -1;  // KILN_TICK_PRE / KILN_TICK_POST; -1 = world events
    KilnTickFn tick = nullptr;
    KilnWorldEventFn event = nullptr;
    void* user = nullptr;
    std::atomic<bool> live{true};
};
std::mutex g_sub_mutex;
std::vector<std::shared_ptr<Sub>> g_subs;
std::atomic<uint64_t> g_sub_version{1};
KilnHandle g_next_sub = 1;

struct Task {
    KilnOwner owner;
    KilnTaskFn fn;
    void* user;
};
std::mutex g_task_mutex;
std::vector<Task> g_tasks[KILN_WORLD_KINDS];  // [0] unused: the server world uses the registry's queue
std::atomic<size_t> g_task_count[KILN_WORLD_KINDS]{};

// Per client-side kind, touched only on that world's thread.
struct KindState {
    uintptr_t world = 0;
    uint64_t sub_version = 0, reg_version = 0;
    std::vector<std::shared_ptr<Sub>> pre, post;
    uint32_t prev_clock = 0;
    double prev_start = 0;
    uint64_t step = 0;
    Window win;
};
KindState g_kind[KILN_WORLD_KINDS];
uintptr_t g_server_world_seen = 0;  // tick thread

int64_t now_i() { return static_cast<int64_t>(now_ms()); }

float pct(std::vector<float>& v, double q) {
    if (v.empty()) return 0;
    const size_t k = std::min(v.size() - 1, static_cast<size_t>(q * static_cast<double>(v.size())));
    std::nth_element(v.begin(), v.begin() + static_cast<ptrdiff_t>(k), v.end());
    return v[k];
}

float maxof(const std::vector<float>& v) { return v.empty() ? 0 : *std::max_element(v.begin(), v.end()); }

// Writes one tick-stats.jsonl line when the window spans 60 s. `world` names the kind on a client ("server", ...);
// null on a dedicated server, whose lines keep their original shape.
void write_window(Window& w, const char* world, size_t pre_subs, size_t post_subs) {
    const double now = now_ms();
    if (w.start == 0) w.start = now;
    const double span = now - w.start;
    if (span < 60000.0) return;
    {
        std::lock_guard lock(g_stats_mutex);
        if (g_stats) {
            SYSTEMTIME t;
            GetLocalTime(&t);
            char tag[48] = "";
            if (world) std::snprintf(tag, sizeof tag, "\"world\":\"%s\",", world);
            std::fprintf(
                g_stats,
                "{\"time\":\"%04u-%02u-%02uT%02u:%02u:%02u\",%s\"steps\":%llu,\"rate\":%.2f,\"clock_rate\":%.2f,"
                "\"step_ms\":{\"p50\":%.3f,\"p99\":%.3f,\"max\":%.3f},"
                "\"pre_ms\":{\"p50\":%.4f,\"p99\":%.4f,\"max\":%.3f},"
                "\"post_ms\":{\"p50\":%.4f,\"p99\":%.4f,\"max\":%.3f},"
                "\"interval_ms\":{\"p50\":%.2f,\"p99\":%.2f,\"max\":%.2f},\"subs\":{\"pre\":%zu,\"post\":%zu}}\n",
                t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, tag,
                static_cast<unsigned long long>(w.steps), w.steps * 1000.0 / span, w.clock * 1000.0 / span,
                pct(w.step, 0.5), pct(w.step, 0.99), maxof(w.step), pct(w.pre, 0.5), pct(w.pre, 0.99), maxof(w.pre),
                pct(w.post, 0.5), pct(w.post, 0.99), maxof(w.post), pct(w.interval, 0.5), pct(w.interval, 0.99),
                maxof(w.interval), pre_subs, post_subs);
            std::fflush(g_stats);
        }
    }
    w = Window{};
    w.start = now;
}

// ---- world events ----
struct EventCall {
    const Sub* sub;
    const KilnWorldEvent* ev;
};
void KILN_CALL call_event(void* p) {
    auto* c = static_cast<EventCall*>(p);
    c->sub->event(c->ev, c->sub->user);
}

void dispatch_event(uint32_t event, uint32_t kind, uintptr_t world, uint32_t session) {
    std::vector<std::shared_ptr<Sub>> subs;
    {
        std::lock_guard lock(g_sub_mutex);
        for (auto& s : g_subs)
            if (s->phase < 0 && s->live.load(std::memory_order_acquire)) subs.push_back(s);
    }
    if (subs.empty()) return;
    KilnWorldEvent ev{sizeof(KilnWorldEvent), event, kind, session, world};
    for (auto& s : subs) {
        if (!s->live.load(std::memory_order_acquire) || !reg::owner_alive(s->owner)) continue;
        EventCall c{s.get(), &ev};
        unsigned long code = 0;
        if (!guard::task(&call_event, &c, &code)) {
            remove(s->handle);
            KLOG_E("world event subscriber of '%s' faulted (0x%08lx) and was removed",
                   reg::owner_name(s->owner).c_str(), code);
        }
    }
}

// First step of a world we have not seen (a new world pointer of this kind): a new session.
void begin_world(uint32_t kind, uintptr_t world) {
    const uintptr_t old = worldinfo::world(kind);
    const uint32_t session = g_sessions[kind].fetch_add(1) + 1;
    worldinfo::set_world(kind, world);
    if (session > 1) {  // tick-stats windows per session: a window must not span the main menu between two worlds
        if (kind == KILN_WORLD_SERVER) g_win = Window{};
        else g_kind[kind].win = Window{};
    }
    g_kind_thread[kind].store(GetCurrentThreadId());
    if (kind == KILN_WORLD_SERVER) g_thread.store(GetCurrentThreadId());
    g_generation.fetch_add(1);
    if (kind == KILN_WORLD_SERVER && session == 1)
        KLOG_I("tick: first server-world step on thread %lu", GetCurrentThreadId());
    KLOG_I("tick: %s world 0x%llx started (session %u, thread %lu%s)", kKindName[kind],
           static_cast<unsigned long long>(world), session, GetCurrentThreadId(),
           old ? "; the previous one stopped without a destroy event" : "");
    dispatch_event(KILN_WORLD_STARTED, kind, world, session);
}

// ---- server world (kind 0) ----
void run(std::vector<std::shared_ptr<reg::TickSub>>& subs, const KilnTickInfo& info) {
    for (auto& s : subs) {
        if (!s->live.load(std::memory_order_acquire)) continue;
        unsigned long code = 0;
        if (!guard::tick(s->fn, &info, s->user, &code)) {
            s->live.store(false);
            reg::remove(s->handle);
            KLOG_E("tick subscriber of '%s' faulted (0x%08lx) and was removed", reg::owner_name(s->owner).c_str(),
                   code);
        }
    }
}

uintptr_t server_step(uintptr_t storage, uintptr_t ticked, uintptr_t r8, uintptr_t r9) {
    const double t0 = now_ms();
    KilnTickInfo info{};
    try {
        const uintptr_t world = storage - static_cast<uintptr_t>(g_world_storage);
        // A new world, or the same address again after the game destroyed the previous one (allocator reuse).
        bool fresh = false;
        if (world != g_server_world_seen || worldinfo::server_world() != world) {
            g_server_world_seen = world;
            begin_world(KILN_WORLD_SERVER, world);
            fresh = true;
        }
        const uint64_t v = reg::version();
        if (v != g_version) {
            g_pre = reg::tick_subs(KILN_TICK_PRE);
            g_post = reg::tick_subs(KILN_TICK_POST);
            g_version = v;
        }
        reg::drain_posts();
        info.size = sizeof info;
        info.world_kind = 0;
        info.step = ++g_step;
        info.clock = *reinterpret_cast<const uint32_t*>(storage + g_off_clock);
        // A new world's clock restarts (a client hosting again): no delta across worlds, none backwards.
        info.clock_delta = (g_step == 1 || fresh || info.clock < g_prev_clock) ? 0 : info.clock - g_prev_clock;
        info.clock_period = g_off_period >= 0 ? *reinterpret_cast<const int64_t*>(storage + g_off_period) : 0;
        info.wall_delta_ms = (g_step == 1 || fresh) ? 0 : t0 - g_prev_start;
        g_steps[0].store(g_step, std::memory_order_relaxed);
        g_last_step_ms[0].store(static_cast<int64_t>(t0), std::memory_order_relaxed);
        {
            std::lock_guard lock(g_last_mutex);
            g_last = info;
        }
        run(g_pre, info);
    } catch (...) {
        disable("server Pre");
        return g_hook.call<uintptr_t>(storage, ticked, r8, r9);  // no Post with incomplete tick info
    }

    const double t1 = now_ms();
    const uintptr_t result = g_hook.call<uintptr_t>(storage, ticked, r8, r9);
    const double t2 = now_ms();
    if (g_faulted.load()) return result;

    try {
        if (!g_post.empty()) {
            if (g_wait_jobs) {
                g_wait_jobs(storage - static_cast<uintptr_t>(g_world_storage));
                run(g_post, info);
            } else if (!g_post_warned) {
                g_post_warned = true;
                KLOG_W("tick: Post subscribers exist but WaitWorldJobs is unresolved; Post is not delivered");
            }
        }
        const double t3 = now_ms();
        if (g_step > 1 && info.wall_delta_ms > 0) g_win.interval.push_back(static_cast<float>(t0 - g_prev_start));
        g_win.step.push_back(static_cast<float>(t2 - t1));
        g_win.pre.push_back(static_cast<float>(t1 - t0));
        g_win.post.push_back(static_cast<float>(t3 - t2));
        g_win.clock += info.clock_delta;
        ++g_win.steps;
        g_prev_clock = info.clock;
        g_prev_start = t0;
        write_stats_if_due();
    } catch (...) {
        disable("server Post");
    }
    return result;
}

// ---- client-side worlds (kinds 1, 2) ----
void refresh(uint32_t kind, KindState& K) {
    const uint64_t sv = g_sub_version.load(std::memory_order_acquire), rv = reg::version();
    if (sv == K.sub_version && rv == K.reg_version) return;
    K.pre.clear();
    K.post.clear();
    std::vector<KilnHandle> dead;
    {
        std::lock_guard lock(g_sub_mutex);
        for (auto& s : g_subs) {
            if (s->phase < 0 || s->kind != kind || !s->live.load(std::memory_order_acquire)) continue;
            if (!reg::owner_alive(s->owner)) {  // owner_revoke bumps the registry version
                dead.push_back(s->handle);
                continue;
            }
            (s->phase == KILN_TICK_PRE ? K.pre : K.post).push_back(s);
        }
    }
    for (KilnHandle h : dead) remove(h);
    K.sub_version = g_sub_version.load(std::memory_order_acquire);
    K.reg_version = rv;
}

void run_subs(std::vector<std::shared_ptr<Sub>>& subs, const KilnTickInfo& info) {
    for (auto& s : subs) {
        if (!s->live.load(std::memory_order_acquire)) continue;
        unsigned long code = 0;
        if (!guard::tick(s->tick, &info, s->user, &code)) {
            remove(s->handle);
            KLOG_E("%s-world tick subscriber of '%s' faulted (0x%08lx) and was removed", kKindName[info.world_kind],
                   reg::owner_name(s->owner).c_str(), code);
        }
    }
}

void drain_tasks(uint32_t kind) {
    if (g_task_count[kind].load(std::memory_order_acquire) == 0) return;
    std::vector<Task> tasks;
    {
        std::lock_guard lock(g_task_mutex);
        tasks.swap(g_tasks[kind]);
        g_task_count[kind].store(0, std::memory_order_release);
    }
    for (const Task& t : tasks) {
        if (!reg::owner_alive(t.owner)) continue;
        unsigned long code = 0;
        if (!guard::task(t.fn, t.user, &code))
            KLOG_E("task posted to the %s world by '%s' faulted (0x%08lx)", kKindName[kind],
                   reg::owner_name(t.owner).c_str(), code);
    }
}

uintptr_t other_step(uint32_t kind, uintptr_t storage, uintptr_t ticked, uintptr_t r8, uintptr_t r9) {
    KindState& K = g_kind[kind];
    const double t0 = now_ms();
    KilnTickInfo info{};
    try {
        const uintptr_t world = storage - static_cast<uintptr_t>(g_world_storage);
        bool fresh = false;
        if (world != K.world || worldinfo::world(kind) != world) {
            K.world = world;
            begin_world(kind, world);
            fresh = true;
        }
        refresh(kind, K);
        drain_tasks(kind);
        info.size = sizeof info;
        info.world_kind = kind;
        info.step = ++K.step;
        info.clock = *reinterpret_cast<const uint32_t*>(storage + g_off_clock);
        // The prediction world's clock can step back (resimulation): no negative deltas, none across worlds.
        info.clock_delta = (K.step == 1 || fresh || info.clock < K.prev_clock) ? 0 : info.clock - K.prev_clock;
        info.clock_period = g_off_period >= 0 ? *reinterpret_cast<const int64_t*>(storage + g_off_period) : 0;
        info.wall_delta_ms = (K.step == 1 || fresh) ? 0 : t0 - K.prev_start;
        g_steps[kind].store(K.step, std::memory_order_relaxed);
        g_last_step_ms[kind].store(static_cast<int64_t>(t0), std::memory_order_relaxed);
        if (!K.pre.empty()) run_subs(K.pre, info);
    } catch (...) {
        disable("client/prediction Pre");
        return g_hook.call<uintptr_t>(storage, ticked, r8, r9);
    }

    const double t1 = now_ms();
    const uintptr_t result = g_hook.call<uintptr_t>(storage, ticked, r8, r9);
    const double t2 = now_ms();
    if (g_faulted.load()) return result;

    try {
        if (!K.post.empty()) run_subs(K.post, info);
        const double t3 = now_ms();
        Window& w = K.win;
        if (K.step > 1 && info.wall_delta_ms > 0) w.interval.push_back(static_cast<float>(t0 - K.prev_start));
        w.step.push_back(static_cast<float>(t2 - t1));
        w.pre.push_back(static_cast<float>(t1 - t0));
        w.post.push_back(static_cast<float>(t3 - t2));
        w.clock += info.clock_delta;
        ++w.steps;
        K.prev_clock = info.clock;
        K.prev_start = t0;
        write_window(w, kKindName[kind], K.pre.size(), K.post.size());
    } catch (...) {
        disable("client/prediction Post");
    }
    return result;
}

uintptr_t __fastcall hk_sim_step(uintptr_t storage, uintptr_t ticked, uintptr_t r8, uintptr_t r9) {
    if (g_faulted.load() || !(ticked & 0xFF)) return g_hook.call<uintptr_t>(storage, ticked, r8, r9);
    const uint8_t kind = *reinterpret_cast<const uint8_t*>(storage + g_off_kind);
    if (kind == KILN_WORLD_SERVER) return server_step(storage, ticked, r8, r9);
    if (kind < KILN_WORLD_KINDS) return other_step(kind, storage, ticked, r8, r9);
    return g_hook.call<uintptr_t>(storage, ticked, r8, r9);
}

// The game destroys a world (leaving a hosted or joined game; process exit). Runs on the destroying thread: the
// server world's own thread for the server world, the main thread for the client world. The world is intact here.
void __fastcall hk_world_destroy(uintptr_t world) {
    if (g_faulted.load()) return g_destroy_hook.call<void>(world);
    try {
        uint8_t kind = 0xFF;
        if (world && guard::copy(&kind, world + static_cast<uintptr_t>(g_world_storage + g_off_kind), 1) &&
            kind < KILN_WORLD_KINDS && worldinfo::world(kind) == world) {
            const uint32_t session = g_sessions[kind].load();
            const uint64_t steps = g_steps[kind].load();
            KLOG_I("tick: %s world 0x%llx stopping (session %u, %llu steps since boot)", kKindName[kind],
                   static_cast<unsigned long long>(world), session, static_cast<unsigned long long>(steps));
            if (kind == KILN_WORLD_SERVER && g_wait_jobs) g_wait_jobs(world);  // safe early and twice (EMF notes)
            // The prediction world is not a world object of its own: its storage lives in an object at
            // client world + 0x15F56C0, which this destroy frees (docs/re/client.md). It stops with the client world.
            const uintptr_t prediction = worldinfo::world(KILN_WORLD_PREDICTION);
            if (kind == KILN_WORLD_CLIENT && prediction) {
                const uint32_t ps = g_sessions[KILN_WORLD_PREDICTION].load();
                KLOG_I("tick: prediction world 0x%llx stopping with the client world (session %u, %llu steps since boot)",
                       static_cast<unsigned long long>(prediction), ps,
                       static_cast<unsigned long long>(g_steps[KILN_WORLD_PREDICTION].load()));
                dispatch_event(KILN_WORLD_STOPPING, KILN_WORLD_PREDICTION, prediction, ps);
                worldinfo::set_world(KILN_WORLD_PREDICTION, 0);
            }
            dispatch_event(KILN_WORLD_STOPPING, kind, world, session);
            worldinfo::set_world(kind, 0);  // the next step of this kind (even at the same address) starts a session
            g_generation.fetch_add(1);
        }
    } catch (...) {
        disable("world destroy");
    }
    g_destroy_hook.call<void>(world);
}

}  // namespace

bool install() {
    uintptr_t sim_step = 0;
    if (!sigdb::feature("tick") || !sigdb::symbol("SimStep", &sim_step) ||
        !sigdb::constant("world.storage", &g_world_storage) || !sigdb::constant("storage.kind", &g_off_kind) ||
        !sigdb::constant("storage.clock", &g_off_clock)) {
        KLOG_W("tick: feature unavailable on this build; no tick hook");
        return false;
    }
    if (!sigdb::constant("storage.clock_period", &g_off_period)) g_off_period = -1;
    uintptr_t wait = 0;
    if (sigdb::feature("tick.wait_jobs") && sigdb::symbol("WaitWorldJobs", &wait))
        g_wait_jobs = reinterpret_cast<WaitWorldJobsFn>(wait);
    g_hook = safetyhook::create_inline(reinterpret_cast<void*>(sim_step), reinterpret_cast<void*>(&hk_sim_step));
    if (!g_hook) {
        KLOG_E("tick: hooking SimStep at %p failed", reinterpret_cast<void*>(sim_step));
        return false;
    }
    g_installed = true;
    g_stats = _wfopen((paths().logs / L"tick-stats.jsonl").c_str(), L"w");
    KLOG_I("tick: SimStep hooked (world.storage=0x%llx kind@+0x%llx clock@+0x%llx period@%s, Post %s)",
           static_cast<unsigned long long>(g_world_storage), static_cast<unsigned long long>(g_off_kind),
           static_cast<unsigned long long>(g_off_clock), g_off_period >= 0 ? "resolved" : "-",
           g_wait_jobs ? "waits for world jobs" : "disabled");
    uintptr_t destroy = 0;
    if (sigdb::feature("world.lifecycle") && sigdb::symbol("WorldDestroy", &destroy)) {
        g_destroy_hook =
            safetyhook::create_inline(reinterpret_cast<void*>(destroy), reinterpret_cast<void*>(&hk_world_destroy));
        g_lifecycle = static_cast<bool>(g_destroy_hook);
        if (g_lifecycle)
            KLOG_I("tick: WorldDestroy hooked (rva 0x%llx): world stops are seen",
                   static_cast<unsigned long long>(rva_of(destroy)));
        else
            KLOG_W("tick: hooking WorldDestroy failed; world stops are not seen");
    }
    return true;
}

bool installed() { return g_installed && !g_faulted.load(); }
bool lifecycle() { return g_lifecycle && !g_faulted.load(); }

bool on_tick_thread() { return !g_faulted.load() && g_thread.load(std::memory_order_relaxed) == GetCurrentThreadId(); }

bool on_world_thread(uint32_t kind) {
    if (g_faulted.load()) return false;
    if (kind == KILN_WORLD_SERVER) return on_tick_thread();
    return kind < KILN_WORLD_KINDS && g_kind_thread[kind].load(std::memory_order_relaxed) == GetCurrentThreadId();
}

KilnResult last(KilnTickInfo* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    if (!installed()) return KILN_ERR_UNAVAILABLE;
    std::lock_guard lock(g_last_mutex);
    *out = g_last;
    return KILN_OK;
}

void write_stats_if_due() {
    write_window(g_win, image().side == KILN_SIDE_CLIENT ? "server" : nullptr, g_pre.size(), g_post.size());
}

KilnResult subscribe_world_tick(KilnOwner owner, uint32_t kind, int32_t phase, KilnTickFn fn, void* user,
                                KilnHandle* out) {
    if (out) *out = 0;
    if (!installed()) return KILN_ERR_UNAVAILABLE;
    if (kind == KILN_WORLD_SERVER) return reg::add_tick(owner, phase, fn, user, out);
    if (kind >= KILN_WORLD_KINDS || !fn || (phase != KILN_TICK_PRE && phase != KILN_TICK_POST))
        return KILN_ERR_INVALID_ARGUMENT;
    if (!reg::owner_alive(owner)) return KILN_ERR_REVOKED;
    auto s = std::make_shared<Sub>();
    s->owner = owner;
    s->kind = kind;
    s->phase = phase;
    s->tick = fn;
    s->user = user;
    {
        std::lock_guard lock(g_sub_mutex);
        s->handle = kHandleTag | g_next_sub++;
        g_subs.push_back(s);
    }
    g_sub_version.fetch_add(1, std::memory_order_acq_rel);
    if (out) *out = s->handle;
    return KILN_OK;
}

KilnResult post_world(KilnOwner owner, uint32_t kind, KilnTaskFn fn, void* user) {
    if (!installed()) return KILN_ERR_UNAVAILABLE;
    if (kind == KILN_WORLD_SERVER) return reg::post(owner, fn, user);
    if (kind >= KILN_WORLD_KINDS || !fn) return KILN_ERR_INVALID_ARGUMENT;
    if (!reg::owner_alive(owner)) return KILN_ERR_REVOKED;
    std::lock_guard lock(g_task_mutex);
    g_tasks[kind].push_back(Task{owner, fn, user});
    g_task_count[kind].store(g_tasks[kind].size(), std::memory_order_release);
    return KILN_OK;
}

KilnResult subscribe_world(KilnOwner owner, KilnWorldEventFn fn, void* user, KilnHandle* out) {
    if (out) *out = 0;
    if (!installed()) return KILN_ERR_UNAVAILABLE;
    if (!fn) return KILN_ERR_INVALID_ARGUMENT;
    if (!reg::owner_alive(owner)) return KILN_ERR_REVOKED;
    auto s = std::make_shared<Sub>();
    s->owner = owner;
    s->event = fn;
    s->user = user;
    {
        std::lock_guard lock(g_sub_mutex);
        s->handle = kHandleTag | g_next_sub++;
        g_subs.push_back(s);
    }
    g_sub_version.fetch_add(1, std::memory_order_acq_rel);
    if (out) *out = s->handle;
    return KILN_OK;
}

KilnResult remove(KilnHandle handle) {
    bool found = false;
    {
        std::lock_guard lock(g_sub_mutex);
        std::erase_if(g_subs, [&](const std::shared_ptr<Sub>& s) {
            if (s->handle != handle) return false;
            s->live.store(false, std::memory_order_release);
            found = true;
            return true;
        });
    }
    if (found) g_sub_version.fetch_add(1, std::memory_order_acq_rel);
    return found ? KILN_OK : KILN_ERR_NOT_FOUND;
}

KilnResult session(KilnSessionInfo* out) {
    if (!out || out->size < sizeof(KilnSessionInfo)) return KILN_ERR_INVALID_ARGUMENT;
    if (g_faulted.load()) return KILN_ERR_UNAVAILABLE;
    KilnSessionInfo s{};
    s.size = sizeof s;
    s.side = image().side;
    s.generation = g_generation.load();
    s.lifecycle = g_lifecycle ? 1 : 0;
    const int64_t now = now_i();
    bool live[KILN_WORLD_KINDS]{};
    for (uint32_t k = 0; k < KILN_WORLD_KINDS; ++k) {
        s.worlds[k] = worldinfo::world(k);
        s.sessions[k] = g_sessions[k].load();
        s.steps[k] = g_steps[k].load();
        // Without the destroy hook a world that stopped stepping 3 s ago counts as gone.
        live[k] = s.worlds[k] && (g_lifecycle || now - g_last_step_ms[k].load() < 3000);
    }
    if (s.side == KILN_SIDE_SERVER)
        s.state = live[KILN_WORLD_SERVER] ? KILN_SESSION_DEDICATED : KILN_SESSION_NONE;
    else if (live[KILN_WORLD_SERVER])
        s.state = KILN_SESSION_HOST;
    else if (live[KILN_WORLD_CLIENT])
        s.state = KILN_SESSION_REMOTE;
    else
        s.state = KILN_SESSION_NONE;
    *out = s;
    return KILN_OK;
}

}  // namespace kiln::tick
