// SPDX-License-Identifier: MIT
#include "tick.h"
#include "tick_stats.h"
#include "tick_watch.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <string>
#include <thread>
#include <cstdio>
#include <memory>
#include <mutex>
#include <vector>

#include "guard.h"
#include "image.h"
#include "log.h"
#include "registry.h"
#include "services/hooks_internal.h"
#include "services/window_clock.h"
#include "sigdb.h"
#include "progress_state.h"
#include "worldinfo.h"

namespace kiln::tasks_svc {  // services/tasks.cpp: the server and client worlds' sessions and Post edges for kiln.tasks
void session_started(uint32_t world, uint32_t session);
void post_edge(uint32_t world, uint32_t session, uint64_t step);
void session_stopping(uint32_t world, uint32_t session);
void tick_hops(uint32_t world);  // the managed host's queued tick hops of that world, bounded, before its Pre
// Issue #49: the task pool's diagnostics window for this line; false when the pool does not exist
// (the line then keeps its older shape). Never takes a pool lock. `world`: whose hop account the line
// carries; `apply_only`: that account alone (the pool-wide parts ride on the line that drives the mods).
bool tasks_window(uint32_t world, bool apply_only, kiln::tasks_stats::WindowSnapshot* out);
}  // namespace kiln::tasks_svc

namespace kiln::tick {
namespace {

constexpr int32_t kBasePriority = -1000000;  // below every other link: the tick is the innermost, see Link::install

// A kiln.hooks v2 raw link (owner 0 = "kiln"): `original` is the chain's continuation (the rest of the chain, then the
// game's function). Links live for the process, like the safetyhook hooks they replace.
struct Link {
    void* original = nullptr;
    KilnHandle handle = 0;
    template <class R, class... A>
    R call(A... a) const { return reinterpret_cast<R (*)(A...)>(original)(a...); }
    explicit operator bool() const { return handle != 0; }
    bool install(uintptr_t target, void* fn, const char* label) {
        if (!hooks::ensure_ready()) return false;
        hooks::chain::Order o;
        o.label = label;
        // The tick wraps the game's function itself, as it did when it was the first hook put on it: every link added
        // later (a service's, a mod's) runs before it.
        o.priority = kBasePriority;
        return hooks::chain::install_raw(0, target, o, fn, &original, &handle) == KILN_OK;
    }
};
Link g_hook;
Link g_destroy_hook;
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

// The watch over hk_world_destroy's wait for a world's jobs (tick_watch.h): a thread started with the tick hook that only
// logs "still waiting" while that wait lasts (sparsely); it never ends the wait or frees anything.
WaitWatch g_wait_watch;
std::mutex g_watchdog_mu;
std::condition_variable g_watchdog_cv;
void wait_watchdog() {
    for (;;) {
        {
            std::unique_lock lock(g_watchdog_mu);
            g_watchdog_cv.wait_for(lock, std::chrono::milliseconds(250));
        }
        std::string line;
        if (g_wait_watch.due(now_ms(), &line)) KLOG_W("%s", line.c_str());
    }
}
bool g_post_warned = false;

thread_local uint32_t g_typed_pre = 0;
struct TypedPreScope {
    bool active;
    explicit TypedPreScope(bool enabled) : active(enabled) { if (active) ++g_typed_pre; }
    ~TypedPreScope() { if (active) --g_typed_pre; }
};
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
    // since 0.2.0-dev.29, the parts of Pre and Post (tick_stats.h): the server world's job wait per step that waited, the
    // window services' end, and the worst step
    std::vector<float> wait;
    stats::Part wait_part, windows;
    stats::Worst worst;
};
// A new window. The vectors keep their capacity: after the first minute a step allocates nothing for its numbers.
void reset(Window& w) {
    w.start = 0;
    w.steps = 0;
    w.clock = 0;
    for (auto* v : {&w.step, &w.pre, &w.post, &w.interval, &w.wait}) v->clear();
    w.wait_part = stats::Part{};
    w.windows = stats::Part{};
    stats::reset(w.worst);
}
Window g_win;
stats::Subs g_slots;  // the server world's subscribers' times (tick thread), parallel to g_pre / g_post
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
    stats::Subs slots;  // parallel to pre / post
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

// Which part of the kiln.tasks window (issue #49) a tick-stats line carries: none (the prediction world), the whole
// window with this world's hop account (the server world; a joined client's client world), or that account alone (a
// hosting client's client world: the pool-wide parts are on its server line).
enum class TasksPart { None, Whole, ApplyOnly };

// One tick-stats.jsonl line of the window (write_window), the kiln.tasks part collected here, once a minute, never on
// a step.
void write_line(Window& w, const stats::Subs& slots, const char* world, double span, size_t pre_subs, size_t post_subs,
                bool waits, TasksPart tasks, uint32_t kind) {
    SYSTEMTIME t;
    GetLocalTime(&t);
    stats::Line l;
    l.year = t.wYear;
    l.month = t.wMonth;
    l.day = t.wDay;
    l.hour = t.wHour;
    l.minute = t.wMinute;
    l.second = t.wSecond;
    l.world = world;
    l.steps = static_cast<unsigned long long>(w.steps);
    l.rate = w.steps * 1000.0 / span;
    l.clock_rate = w.clock * 1000.0 / span;
    l.step = {pct(w.step, 0.5), pct(w.step, 0.99), maxof(w.step)};
    l.pre = {pct(w.pre, 0.5), pct(w.pre, 0.99), maxof(w.pre)};
    l.post = {pct(w.post, 0.5), pct(w.post, 0.99), maxof(w.post)};
    l.interval = {pct(w.interval, 0.5), pct(w.interval, 0.99), maxof(w.interval)};
    l.pre_count = pre_subs;
    l.post_count = post_subs;
    l.waits = waits;
    l.wait = {pct(w.wait, 0.5), pct(w.wait, 0.99), maxof(w.wait)};
    l.wait_part = w.wait_part;
    l.windows = w.windows;
    auto name = [](uint64_t owner) { return reg::owner_name(owner); };
    l.post_subs = stats::by_owner(slots.post, slots.gone_post, name);
    l.pre_subs = stats::by_owner(slots.pre, slots.gone_pre, name);
    stats::top(l.pre_subs, stats::kPreTop);
    if (w.worst.total >= 0) {
        l.has_worst = true;
        l.worst_pre = w.worst.pre;
        l.worst_step = w.worst.step;
        l.worst_post = w.worst.post;
        l.worst_wait = w.worst.wait;
        l.worst_windows = w.worst.windows;
        l.worst_hour = w.worst.hour;
        l.worst_minute = w.worst.minute;
        l.worst_second = w.worst.second;
        l.worst_milli = w.worst.milli;
        l.worst_post_subs = stats::by_owner(w.worst.post_subs, name);
        l.worst_pre_subs = stats::by_owner(w.worst.pre_subs, name);
        stats::top(l.worst_pre_subs, stats::kPreTop);
    }
    if (tasks != TasksPart::None) l.has_tasks = tasks_svc::tasks_window(kind, tasks == TasksPart::ApplyOnly, &l.tasks);
    const std::string line = stats::format(l);
    {
        std::lock_guard lock(g_stats_mutex);
        if (g_stats) {
            std::fputs(line.c_str(), g_stats);
            std::fflush(g_stats);
        }
    }
}

// Writes one tick-stats.jsonl line when the window spans 60 s (the line: tick_stats.h). `world` names the kind on a client
// ("server", ...); null on a dedicated server, whose lines keep their original shape. `waits`: this world's Post waits for
// its jobs (the server world with WaitWorldJobs), so the line has the wait and the window services' end. Owner names are
// looked up here, once a minute, not on each step.
void write_window(Window& w, stats::Subs& slots, const char* world, size_t pre_subs, size_t post_subs, bool waits,
                  TasksPart tasks = TasksPart::None, uint32_t kind = KILN_WORLD_SERVER) {
    const double now = now_ms();
    if (w.start == 0) w.start = now;
    const double span = now - w.start;
    if (span < 60000.0) return;
    try {  // a line that cannot be built (out of memory) is skipped; it never stops the tick
        write_line(w, slots, world, span, pre_subs, post_subs, waits, tasks, kind);
    } catch (...) {
    }
    reset(w);
    stats::clear(slots);
    w.start = now;
}

// The step's numbers into its window: the worst step keeps its breakdown and the local time it ended.
void account_worst(Window& w, const stats::Subs& slots, float pre, float step, float post, float wait, float windows,
                   bool post_ran) {
    if (!stats::beat(w.worst, pre, step, post, wait, windows, slots, post_ran)) return;
    SYSTEMTIME t;
    GetLocalTime(&t);
    w.worst.hour = t.wHour;
    w.worst.minute = t.wMinute;
    w.worst.second = t.wSecond;
    w.worst.milli = t.wMilliseconds;
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
        if (kind == KILN_WORLD_SERVER) {
            reset(g_win);
            stats::clear(g_slots);
        } else {
            reset(g_kind[kind].win);
            stats::clear(g_kind[kind].slots);
        }
    }
    g_kind_thread[kind].store(GetCurrentThreadId());
    if (kind == KILN_WORLD_SERVER) g_thread.store(GetCurrentThreadId());
    g_generation.fetch_add(1);
    if (kind == KILN_WORLD_SERVER) window_clock::world_started();  // a new world epoch, before any subscriber hears of it
    if (kind == KILN_WORLD_SERVER && session == 1) KLOG_I("tick: first server-world step on thread %lu", GetCurrentThreadId());
    // progress.json: the game's first world (any kind): a dedicated server without .NET is ready here, and so is a client
    // whose main menu mark never came (progress_file.h, Mark::world_started; later worlds change nothing)
    if (session == 1) progress_state::mark(3);
    KLOG_I("tick: %s world 0x%llx started (session %u, thread %lu%s)", kKindName[kind],
           static_cast<unsigned long long>(world), session, GetCurrentThreadId(),
           old ? "; the previous one stopped without a destroy event" : "");
    if (kind == KILN_WORLD_SERVER || kind == KILN_WORLD_CLIENT) tasks_svc::session_started(kind, session);
    dispatch_event(KILN_WORLD_STARTED, kind, world, session);
}

// ---- server world (kind 0) ----
// Runs the subscribers in order, each one's time into its slot (`slots` is parallel to `subs`; one clock read per
// subscriber: each end is the next one's start, `t` the first start).
void run(std::vector<std::shared_ptr<reg::TickSub>>& subs, std::vector<stats::Slot>& slots, const KilnTickInfo& info,
         double t) {
    const bool timed = slots.size() == subs.size();
    for (size_t i = 0; i < subs.size(); ++i) {
        auto& s = subs[i];
        if (!s->live.load(std::memory_order_acquire)) {
            if (timed) slots[i].cur = 0;
            continue;
        }
        unsigned long code = 0;
        if (!guard::tick(s->fn, &info, s->user, &code)) {
            s->live.store(false);
            reg::remove(s->handle);
            KLOG_E("tick subscriber of '%s' faulted (0x%08lx) and was removed", reg::owner_name(s->owner).c_str(),
                   code);
        }
        const double e = now_ms();
        if (timed) {
            slots[i].cur = static_cast<float>(e - t);
            slots[i].part.add(slots[i].cur);
        }
        t = e;
    }
}

uintptr_t server_step(uintptr_t storage, uintptr_t ticked, uintptr_t r8, uintptr_t r9) {
    const double t0 = now_ms();
    KilnTickInfo info{};
    try {
        TypedPreScope safe(true);
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
            stats::rebind(g_slots.pre, g_slots.gone_pre, g_pre);
            stats::rebind(g_slots.post, g_slots.gone_post, g_post);
            stats::reserve(g_win.worst, g_slots);
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
        window_clock::pre(info.step);
        tasks_svc::tick_hops(KILN_WORLD_SERVER);  // kiln.tasks: the managed host's tick hops (budgeted), before Pre
        run(g_pre, g_slots.pre, info, now_ms());
        // After the last Pre subscriber: the window services sample the world (kiln_window_clock.h, phase 2), then Sim.
        window_clock::begin();
    } catch (...) {
        disable("server Pre");
        return g_hook.call<uintptr_t>(storage, ticked, r8, r9);  // no Post with incomplete tick info
    }

    const double t1 = now_ms();
    const uintptr_t result = g_hook.call<uintptr_t>(storage, ticked, r8, r9);
    const double t2 = now_ms();
    if (g_faulted.load()) return result;

    try {
        // A window service begun in this step makes it wait for the world's jobs also when nobody subscribed to Post
        // (kiln.decisions with no listener and nothing held is not begun: window_wanted).
        float wait_ms = 0, windows_ms = 0;
        bool post_ran = false;
        if (!g_post.empty() || window_clock::wants_end()) {
            if (g_wait_jobs) {
                g_wait_jobs(storage - static_cast<uintptr_t>(g_world_storage));
                const double tw = now_ms();
                window_clock::end();  // phase 4: the window services compare and release; then Post (phase 5)
                const double te = now_ms();
                run(g_post, g_slots.post, info, te);
                wait_ms = static_cast<float>(tw - t2);
                windows_ms = static_cast<float>(te - tw);
                post_ran = true;
                g_win.wait.push_back(wait_ms);
                g_win.wait_part.add(wait_ms);
                g_win.windows.add(windows_ms);
            } else if (!g_post_warned) {
                g_post_warned = true;
                KLOG_W("tick: Post subscribers exist but WaitWorldJobs is unresolved; Post is not delivered");
            }
        }
        // kiln.tasks' AFTER_STEP: Kiln's Post of this step ran (a scheduling hint); queues the released tasks for
        // the pool, never runs one here
        tasks_svc::post_edge(KILN_WORLD_SERVER, g_sessions[KILN_WORLD_SERVER].load(), info.step);
        const double t3 = now_ms();
        if (g_step > 1 && info.wall_delta_ms > 0) g_win.interval.push_back(static_cast<float>(t0 - g_prev_start));
        g_win.step.push_back(static_cast<float>(t2 - t1));
        g_win.pre.push_back(static_cast<float>(t1 - t0));
        g_win.post.push_back(static_cast<float>(t3 - t2));
        account_worst(g_win, g_slots, static_cast<float>(t1 - t0), static_cast<float>(t2 - t1), static_cast<float>(t3 - t2),
                      wait_ms, windows_ms, post_ran);
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
    stats::rebind(K.slots.pre, K.slots.gone_pre, K.pre);
    stats::rebind(K.slots.post, K.slots.gone_post, K.post);
    stats::reserve(K.win.worst, K.slots);
    K.sub_version = g_sub_version.load(std::memory_order_acquire);
    K.reg_version = rv;
}

// As run() for the server world: each subscriber's time into its slot, one clock read per subscriber.
void run_subs(std::vector<std::shared_ptr<Sub>>& subs, std::vector<stats::Slot>& slots, const KilnTickInfo& info,
              double t) {
    const bool timed = slots.size() == subs.size();
    for (size_t i = 0; i < subs.size(); ++i) {
        auto& s = subs[i];
        if (!s->live.load(std::memory_order_acquire)) {
            if (timed) slots[i].cur = 0;
            continue;
        }
        unsigned long code = 0;
        if (!guard::tick(s->tick, &info, s->user, &code)) {
            remove(s->handle);
            KLOG_E("%s-world tick subscriber of '%s' faulted (0x%08lx) and was removed", kKindName[info.world_kind],
                   reg::owner_name(s->owner).c_str(), code);
        }
        const double e = now_ms();
        if (timed) {
            slots[i].cur = static_cast<float>(e - t);
            slots[i].part.add(slots[i].cur);
        }
        t = e;
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
        TypedPreScope safe(kind == KILN_WORLD_CLIENT && !worldinfo::server_world());
        const uintptr_t world = storage - static_cast<uintptr_t>(g_world_storage);
        bool fresh = false;
        if (world != K.world || worldinfo::world(kind) != world) {
            K.world = world;
            begin_world(kind, world);
            fresh = true;
        }
        refresh(kind, K);
        drain_tasks(kind);
        // kiln.tasks: the client world's tick hops (budgeted, its own budget), before its Pre subscribers
        if (kind == KILN_WORLD_CLIENT) tasks_svc::tick_hops(KILN_WORLD_CLIENT);
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
        if (!K.pre.empty()) run_subs(K.pre, K.slots.pre, info, now_ms());
    } catch (...) {
        disable("client/prediction Pre");
        return g_hook.call<uintptr_t>(storage, ticked, r8, r9);
    }

    const double t1 = now_ms();
    const uintptr_t result = g_hook.call<uintptr_t>(storage, ticked, r8, r9);
    const double t2 = now_ms();
    if (g_faulted.load()) return result;

    try {
        const bool post_ran = !K.post.empty();
        if (post_ran) run_subs(K.post, K.slots.post, info, t2);  // a client-side world's step finished its own jobs
        // kiln.tasks' AFTER_STEP and NextGateWindow of the client world: its Post ran (a scheduling hint)
        if (kind == KILN_WORLD_CLIENT) tasks_svc::post_edge(KILN_WORLD_CLIENT, g_sessions[KILN_WORLD_CLIENT].load(), info.step);
        const double t3 = now_ms();
        Window& w = K.win;
        if (K.step > 1 && info.wall_delta_ms > 0) w.interval.push_back(static_cast<float>(t0 - K.prev_start));
        w.step.push_back(static_cast<float>(t2 - t1));
        w.pre.push_back(static_cast<float>(t1 - t0));
        w.post.push_back(static_cast<float>(t3 - t2));
        account_worst(w, K.slots, static_cast<float>(t1 - t0), static_cast<float>(t2 - t1), static_cast<float>(t3 - t2), 0, 0,
                      post_ran);
        w.clock += info.clock_delta;
        ++w.steps;
        K.prev_clock = info.clock;
        K.prev_start = t0;
        // the client world's line carries its hop account; the pool-wide parts too while no server world drives the mods
        const TasksPart part = kind != KILN_WORLD_CLIENT ? TasksPart::None
                               : worldinfo::server_world() ? TasksPart::ApplyOnly
                                                           : TasksPart::Whole;
        write_window(w, K.slots, kKindName[kind], K.pre.size(), K.post.size(), false, part, kind);
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
            // The engine's own wait for the world's task queue, before anyone sees STOPPING (tick_drain.h): the server
            // world, and since 0.2.0-dev.29 the client world (its own queue; the prediction world stops with it, after).
            // WaitWorldJobs is the game's (mov rcx,[world+world.jobs]; jmp WaitTaskGroup) and has no time limit, as at
            // the end of every GameServer update: it returns when the queue's tasks have finished. No program slice is
            // in that queue here (StepPrograms waited for its own); a task that never finished would hang the game's
            // next wait on that queue the same way. Safe early and twice (EMF notes).
            if (waits_before_stop(kind) && g_wait_jobs) {
                const double w0 = now_ms();
                KLOG_I("tick: %s world 0x%llx: waiting for world jobs before STOPPING (WaitWorldJobs)",
                       kKindName[kind], static_cast<unsigned long long>(world));
                g_wait_watch.arm(kKindName[kind], world, w0);  // the watchdog says so if it lasts (tick_watch.h)
                g_wait_jobs(world);
                const uint32_t said = g_wait_watch.disarm();
                KLOG_I("tick: %s world 0x%llx: its jobs are drained by the engine (WaitWorldJobs, %.0f us) before STOPPING%s",
                       kKindName[kind], static_cast<unsigned long long>(world), (now_ms() - w0) * 1000.0,
                       said ? " (after the watchdog's \"still waiting\" lines)" : "");
            } else if (waits_before_stop(kind)) {
                KLOG_W("tick: %s world 0x%llx: STOPPING without the engine's wait (tick.wait_jobs did not resolve); "
                       "subscribers have their own bounded wait only", kKindName[kind], static_cast<unsigned long long>(world));
            }
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
            if (kind == KILN_WORLD_SERVER) window_clock::stopping();
            if (kind == KILN_WORLD_SERVER || kind == KILN_WORLD_CLIENT) tasks_svc::session_stopping(kind, session);
            dispatch_event(KILN_WORLD_STOPPING, kind, world, session);
            if (kind == KILN_WORLD_SERVER) window_clock::stopped();
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
    window_clock::enable_windows(g_wait_jobs != nullptr);
    if (g_wait_jobs) {
        try {
            std::thread(&wait_watchdog).detach();
        } catch (...) {
            KLOG_W("tick: no watchdog thread for the wait before a world's STOPPING; a wait that does not return is only "
                   "named by its \"waiting for world jobs\" line");
        }
    }
    if (!g_hook.install(sim_step, reinterpret_cast<void*>(&hk_sim_step), "tick/SimStep")) {
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
        g_lifecycle = g_destroy_hook.install(destroy, reinterpret_cast<void*>(&hk_world_destroy), "tick/WorldDestroy");
        if (g_lifecycle)
            KLOG_I("tick: WorldDestroy hooked (rva 0x%llx): world stops are seen",
                   static_cast<unsigned long long>(rva_of(destroy)));
        else
            KLOG_W("tick: hooking WorldDestroy failed; world stops are not seen");
    }
    return true;
}

bool installed() { return g_installed && !g_faulted.load(); }
bool waits_jobs() { return g_wait_jobs != nullptr; }
StopDrain stop_drain(uint32_t kind) {
    return stop_drain_of(kind, image().side, installed(), lifecycle(), waits_jobs());
}
bool lifecycle() { return g_lifecycle && !g_faulted.load(); }

bool typed_install_pre() { return !g_faulted.load() && g_typed_pre != 0; }

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
    write_window(g_win, g_slots, image().side == KILN_SIDE_CLIENT ? "server" : nullptr, g_pre.size(), g_post.size(),
                 g_wait_jobs != nullptr, TasksPart::Whole, KILN_WORLD_SERVER);
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
