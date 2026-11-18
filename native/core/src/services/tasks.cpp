// SPDX-License-Identifier: MIT
// kiln.tasks v1 (abi/services/kiln_tasks.h, docs/kiln/services/tasks.md): the in-core service that publishes the
// public table over the ONE Kiln task pool. The behaviour lives in the host-tested kiln::tasks::Service
// (src/tasks.{h,cpp}); this file binds it to the core: the registry's owners, the world threads, the capability row.
//
// Lifetime: the pool, the binding and their environment are created once by init() on the heap and never destroyed,
// not even at static destruction. A static destructor runs at DLL_PROCESS_DETACH, under the loader lock, after
// ExitProcess has already ended every other thread: a pool joined there would wait for workers that no longer exist.
// The pool is never shut down; the process exit ends its workers (tasks::Service::exiting() only seals and signals).
#include <atomic>
#include <cstring>
#include <exception>

#include "../config.h"
#include "../log.h"
#include "../registry.h"
#include "../tasks.h"
#include "../tick.h"
#include "service.h"
#include "services/kiln_tasks.h"
#include "../tasks_host.h"

namespace kiln::caps {
void tasks_pool_source(int (*state)());  // services/caps.cpp: tasks.pool follows it
}  // namespace kiln::caps
namespace kiln {
const char* core_build_stamp();  // api.cpp: this core's build identity ("" without build metadata)
}  // namespace kiln

namespace kiln::tasks_svc {
namespace {

class CoreEnv final : public tasks::ServiceEnv {
public:
    bool on_tick_or_world_thread() override {
        for (uint32_t kind = 0; kind < KILN_WORLD_KINDS; ++kind)
            if (tick::on_world_thread(kind)) return true;
        return false;
    }
    bool owner_alive(KilnOwner owner) override { return reg::owner_alive(owner); }
    std::string owner_name(KilnOwner owner) override { return reg::owner_name(owner); }
    void hop_faulted(KilnOwner owner, KilnLease lease, uint32_t code) override {
        KLOG_E("kiln.tasks: the managed host's tick hop of %s (task 0x%llx) faulted (0x%08x); the task is cancelled and "
               "its owner's tasks are quarantined",
               reg::owner_name(owner).c_str(), static_cast<unsigned long long>(lease), code);
    }
};

tasks::Pool* g_pool = nullptr;  // immortal (see above); set once by init() before g_service
// immortal; published by init() once the binding works (never in safe idle); any thread reads it
std::atomic<tasks::Service*> g_service{nullptr};

KilnResult unavailable(KilnHandle* task, uint32_t* refusal) {
    if (task) *task = 0;
    if (refusal) *refusal = KILN_TASK_REFUSAL_CAPABILITY;
    return KILN_ERR_UNAVAILABLE;
}

// No C++ exception crosses the ABI: an allocation failure inside the binding is KILN_ERR_INTERNAL.
KilnResult KILN_CALL t_submit(KilnOwner owner, const KilnTaskInfo* info, KilnTaskBodyFn body, KilnTaskDoneFn done,
                              void* user, KilnHandle* task, uint32_t* refusal) {
    tasks::Service* service = g_service.load(std::memory_order_acquire);
    if (!service) return owner ? unavailable(task, refusal) : KILN_ERR_INVALID_ARGUMENT;
    try {
        return service->submit(owner, info, body, done, user, task, refusal);
    } catch (...) {
        if (task) *task = 0;
        if (refusal) *refusal = KILN_TASK_REFUSAL_CAPABILITY;
        return KILN_ERR_INTERNAL;
    }
}

KilnResult KILN_CALL t_cancel(KilnOwner owner, KilnHandle task, uint32_t* state) {
    tasks::Service* service = g_service.load(std::memory_order_acquire);
    if (!service) {
        if (state) *state = KILN_TASK_NONE;
        return owner ? KILN_ERR_UNAVAILABLE : KILN_ERR_INVALID_ARGUMENT;
    }
    try {
        return service->cancel(owner, task, state);
    } catch (...) {
        return KILN_ERR_INTERNAL;
    }
}

KilnResult KILN_CALL t_status(KilnOwner owner, KilnHandle task, uint32_t* state) {
    tasks::Service* service = g_service.load(std::memory_order_acquire);
    if (!service) {
        if (state) *state = KILN_TASK_NONE;
        return owner && state ? KILN_ERR_UNAVAILABLE : KILN_ERR_INVALID_ARGUMENT;
    }
    try {
        return service->status(owner, task, state);
    } catch (...) {
        return KILN_ERR_INTERNAL;
    }
}

KilnResult KILN_CALL t_release(KilnOwner owner, KilnHandle task) {
    tasks::Service* service = g_service.load(std::memory_order_acquire);
    if (!service) return owner ? KILN_ERR_UNAVAILABLE : KILN_ERR_INVALID_ARGUMENT;
    try {
        return service->release(owner, task);
    } catch (...) {
        return KILN_ERR_INTERNAL;
    }
}

KilnResult KILN_CALL t_drain_owner(KilnOwner owner, uint32_t timeout_ms, uint32_t* running) {
    tasks::Service* service = g_service.load(std::memory_order_acquire);
    if (!service) {
        if (running) *running = 0;
        return owner ? KILN_ERR_UNAVAILABLE : KILN_ERR_INVALID_ARGUMENT;
    }
    try {
        return service->drain_owner(owner, timeout_ms, running);
    } catch (...) {
        return KILN_ERR_INTERNAL;
    }
}

// The watchdog's kiln.log lines (the pool's maintenance thread, outside its locks; each task once per state). The
// log path is the core's existing one: the maintenance thread lives until ExitProcess ends it, which happens before
// any DLL_PROCESS_DETACH or static destructor, so it never writes into a destroyed log.
void log_watch(const tasks::Watch& w, void*) {
    const bool running = w.kind == tasks::Watch::Kind::RunningTooLong;
    const std::string owner = w.owner_name.empty() ? "owner " + std::to_string(w.owner) : w.owner_name;
    KLOG_W("kiln.tasks: %s/%s (task 0x%llx) has been %s for %.1f s (the watchdog reports it once; nothing is stopped)",
           owner.c_str(), w.name.c_str(), static_cast<unsigned long long>(w.task), running ? "running" : "queued",
           static_cast<double>(w.age_ns) / 1e9);
}

// A worker kind that lost its last worker (or got one back): one kiln.log line each way.
void log_note(const tasks::PoolNote& n, void*) {
    const char* kind = n.slot == tasks::Slot::Normal ? "normal" : "LongRunning";
    if (n.kind == tasks::PoolNote::Kind::SlotLost)
        KLOG_E("kiln.tasks: the %s task workers lost their last worker (its replacement could not start); %zu queued "
               "task(s) wait for a restart, retried every second; submissions refuse until one runs",
               kind, n.queued);
    else
        KLOG_I("kiln.tasks: a %s task worker runs again (%zu queued task(s) resume)", kind, n.queued);
}

// ---- kiln.internal.tasks.host v1 (tasks_host.h): the managed host's leases; resolved only through
// api_for_managed_host() (api.cpp). Every member forwards to the binding; nothing throws across it.
template <class F>
KilnResult with_service(F f) {
    tasks::Service* service = g_service.load(std::memory_order_acquire);
    if (!service) return KILN_ERR_UNAVAILABLE;
    try {
        return f(*service);
    } catch (...) {
        return KILN_ERR_INTERNAL;
    }
}
const char* KILN_CALL h_build_stamp() { return core_build_stamp(); }
KilnResult KILN_CALL h_bind(KilnHostDispatchFn dispatch) {
    return with_service([&](tasks::Service& s) { return s.host_bind(dispatch); });
}
KilnResult KILN_CALL h_admit(KilnOwner owner, const KilnTaskInfo* info, void* host_state, KilnLease* lease,
                             uint32_t* refusal) {
    const KilnResult r = with_service([&](tasks::Service& s) { return s.host_admit(owner, info, host_state, lease, refusal); });
    if (r == KILN_ERR_UNAVAILABLE && !g_service.load(std::memory_order_acquire)) {
        if (lease) *lease = 0;
        if (refusal) *refusal = KILN_TASK_REFUSAL_CAPABILITY;
    }
    return r;
}
KilnResult KILN_CALL h_arm(KilnLease lease, uint64_t* registration) {
    return with_service([&](tasks::Service& s) { return s.host_arm(lease, registration); });
}
KilnResult KILN_CALL h_resume(KilnLease lease, uint64_t registration) {
    return with_service([&](tasks::Service& s) { return s.host_resume(lease, registration); });
}
KilnResult KILN_CALL h_resume_after_step(KilnLease lease, uint64_t registration, uint32_t* refusal) {
    return with_service([&](tasks::Service& s) { return s.host_resume_after_step(lease, registration, refusal); });
}
KilnResult KILN_CALL h_tick_hop(KilnLease lease) {
    return with_service([&](tasks::Service& s) { return s.host_tick_hop(lease); });
}
KilnResult KILN_CALL h_finish(KilnLease lease, uint32_t outcome) {
    return with_service([&](tasks::Service& s) { return s.host_finish(lease, outcome); });
}
uint32_t KILN_CALL h_is_cancelled(KilnLease lease) {
    tasks::Service* service = g_service.load(std::memory_order_acquire);
    return service ? service->host_is_cancelled(lease) : 1;
}
KilnResult KILN_CALL h_seal_owner(KilnOwner owner, uint32_t* outstanding) {
    return with_service([&](tasks::Service& s) { return s.host_seal_owner(owner, outstanding); });
}
KilnResult KILN_CALL h_snapshot(KilnLeaseSnapshot* rows, uint32_t capacity, uint32_t* count) {
    return with_service([&](tasks::Service& s) { return s.host_snapshot(rows, capacity, count); });
}
KilnResult KILN_CALL h_bind_ended(KilnHostEndedFn ended) {
    return with_service([&](tasks::Service& s) { return s.host_bind_ended(ended); });
}
KilnResult KILN_CALL h_resume_after_step_in(KilnLease lease, uint64_t registration, uint32_t session, uint32_t* refusal) {
    return with_service(
        [&](tasks::Service& s) { return s.host_resume_after_step_in(lease, registration, session, refusal); });
}
uint32_t KILN_CALL h_on_pool_thread() {
    const tasks::Service* service = g_service.load(std::memory_order_acquire);
    return service ? service->host_on_pool_thread() : 0;
}
uint32_t KILN_CALL h_server_session() {
    const tasks::Service* service = g_service.load(std::memory_order_acquire);
    try {
        return service ? service->host_server_session() : 0;
    } catch (...) {
        return 0;
    }
}

// ---- appended (issue #49): diagnostics for Kiln.Host's crash reports, out of the pool's
// lock-free stats store (tasks_stats.h). No pool lock, no allocation; a copy that cannot be made
// within its bounded retries is UNAVAILABLE, an index past the last row NOT_FOUND.
tasks_stats::Store* stats_or_null() {
    return g_service.load(std::memory_order_acquire) && g_pool ? &g_pool->stats_store() : nullptr;
}
uint32_t KILN_CALL h_stats_owner_count() {
    const tasks_stats::Store* store = stats_or_null();
    return store ? store->owner_count() : 0;
}
KilnResult KILN_CALL h_stats_owner_row(uint32_t index, KilnTaskStatsOwnerRow* row) {
    if (!row) return KILN_ERR_INVALID_ARGUMENT;
    tasks_stats::Store* store = stats_or_null();
    if (!store) return KILN_ERR_UNAVAILABLE;
    tasks_stats::OwnerSnapshot o;
    if (!store->owner_row(index, &o)) return index >= store->owner_count() ? KILN_ERR_NOT_FOUND : KILN_ERR_UNAVAILABLE;
    *row = KilnTaskStatsOwnerRow{};
    row->size = sizeof(KilnTaskStatsOwnerRow);
    row->owner = o.owner;
    row->instance = o.instance;
    row->queued_current = o.queued_current;
    row->queued_max = o.queued_max;
    row->running_current = o.running_current;
    row->running_max = o.running_max;
    row->completed = o.completed;
    row->faulted = o.faulted;
    row->cancelled = o.cancelled;
    row->cpu_ns = o.cpu_ns;
    row->wall_ns = o.wall_ns;
    row->longest_ns = o.longest_ns;
    row->queued_too_long = o.queued_too_long;
    row->running_too_long = o.running_too_long;
    row->queue_full = o.queue_full;
    row->unload_blocked = o.unload_blocked;
    std::memcpy(row->owner_name, o.owner_name, sizeof(row->owner_name));
    std::memcpy(row->longest_name, o.longest_name, sizeof(row->longest_name));
    return KILN_OK;
}
KilnResult KILN_CALL h_stats_running_row(uint32_t index, KilnTaskStatsRunningRow* row) {
    if (!row) return KILN_ERR_INVALID_ARGUMENT;
    tasks_stats::Store* store = stats_or_null();
    if (!store) return KILN_ERR_UNAVAILABLE;
    tasks_stats::RunningSnapshot r;
    if (!store->running_row(index, g_pool->now_ns(), &r)) {
        const tasks_stats::CrashSnapshot c = store->snapshot_crash(g_pool->now_ns());
        if (c.unavailable) return KILN_ERR_UNAVAILABLE;
        return index >= c.running_total ? KILN_ERR_NOT_FOUND : KILN_ERR_UNAVAILABLE;
    }
    *row = KilnTaskStatsRunningRow{};
    row->size = sizeof(KilnTaskStatsRunningRow);
    row->owner = r.owner;
    row->lease = r.handle;
    row->thread_id = r.thread_id;
    row->started_ns = r.started_ns;
    row->elapsed_ns = r.elapsed_ns;
    row->state = static_cast<uint32_t>(r.state);
    std::memcpy(row->owner_name, r.owner_name, sizeof(row->owner_name));
    std::memcpy(row->task_name, r.task_name, sizeof(row->task_name));
    return KILN_OK;
}
KilnResult KILN_CALL h_stats_fault_row(uint32_t index, KilnTaskStatsFaultRow* row) {
    if (!row) return KILN_ERR_INVALID_ARGUMENT;
    tasks_stats::Store* store = stats_or_null();
    if (!store) return KILN_ERR_UNAVAILABLE;
    tasks_stats::FaultSnapshot f;
    if (!store->fault_row(index, &f)) {
        const tasks_stats::CrashSnapshot c = store->snapshot_crash(0);
        return c.unavailable ? KILN_ERR_UNAVAILABLE : KILN_ERR_NOT_FOUND;
    }
    *row = KilnTaskStatsFaultRow{};
    row->size = sizeof(KilnTaskStatsFaultRow);
    row->sequence = f.sequence;
    row->owner = f.owner;
    row->handle = f.handle;
    row->at_ns = f.at_ns;
    row->state = f.state;
    row->code = f.code;
    std::memcpy(row->owner_name, f.owner_name, sizeof(row->owner_name));
    std::memcpy(row->task_name, f.task_name, sizeof(row->task_name));
    return KILN_OK;
}
KilnResult KILN_CALL h_stats_totals(KilnTaskStatsTotals* totals) {
    if (!totals) return KILN_ERR_INVALID_ARGUMENT;
    tasks_stats::Store* store = stats_or_null();
    if (!store) return KILN_ERR_UNAVAILABLE;
    const tasks_stats::Totals t = store->totals(g_pool->now_ns());
    if (t.unavailable) return KILN_ERR_UNAVAILABLE;
    *totals = KilnTaskStatsTotals{};
    totals->size = sizeof(KilnTaskStatsTotals);
    totals->owners = t.owners;
    totals->workers = t.workers;
    totals->long_running = t.long_running;
    totals->queued = t.queued;
    totals->running = t.running;
    totals->completed = t.completed;
    totals->faulted = t.faulted;
    totals->cancelled = t.cancelled;
    totals->running_tasks = t.running_tasks;
    totals->faults = t.faults;
    return KILN_OK;
}

const KilnTasksHostApi kHostTable = {{sizeof(KilnTasksHostApi), KILN_TASKS_HOST_VERSION},
                                     &h_build_stamp,
                                     &h_bind,
                                     &h_admit,
                                     &h_arm,
                                     &h_resume,
                                     &h_resume_after_step,
                                     &h_tick_hop,
                                     &h_finish,
                                     &h_is_cancelled,
                                     &h_seal_owner,
                                     &h_snapshot,
                                     &h_bind_ended,
                                     &h_resume_after_step_in,
                                     &h_on_pool_thread,
                                     &h_server_session,
                                     &h_stats_owner_count,
                                     &h_stats_owner_row,
                                     &h_stats_running_row,
                                     &h_stats_fault_row,
                                     &h_stats_totals};

// tasks.pool's source (caps.cpp reads it lock-free at every registry call): -1 no pool, 0 no worker, 1 available.
int pool_state() {
    const tasks::Service* service = g_service.load(std::memory_order_acquire);
    return service ? g_pool->health() : -1;
}

const KilnTasksApi kTable = {{sizeof(KilnTasksApi), KILN_TASKS_VERSION}, &t_submit,      &t_cancel,
                             &t_status,                                 &t_release,     &t_drain_owner};

bool init() {
    tasks::Service* service = nullptr;
    try {
        auto* env = new CoreEnv;
        // the production platform (tasks_win.cpp); kiln.json "tasks" sets the global knobs (config.h TasksConfig),
        // the per-owner limits stay the contract's (kiln_tasks.h)
        const TasksConfig& knobs = config().tasks;
        tasks::PoolConfig pc;
        pc.normal_workers = knobs.workers;  // 0: clamp(cores / 4, 1, 4)
        pc.limits.long_running_global = knobs.long_running_global;
        pc.long_running_workers = knobs.long_running_global;
        pc.running_watchdog_ns = uint64_t{knobs.watchdog_running_s} * 1000 * 1000 * 1000;
        pc.queued_watchdog_ns = uint64_t{knobs.watchdog_queued_s} * 1000 * 1000 * 1000;
        g_pool = new tasks::Pool(pc);
        service = new tasks::Service(*g_pool, *env);
    } catch (const std::exception& e) {
        KLOG_E("kiln.tasks: the task pool did not start (%s); tasks.pool is unavailable", e.what());
        return false;
    }
    // Retire an owner's tasks when the registry revokes it (observer owner 0: told of every owner, never removed).
    if (reg::add_owner_retiring(0, &tasks::Service::owner_retiring_observer, service, nullptr) != KILN_OK) {
        KLOG_E("kiln.tasks: could not observe owner retirement; tasks.pool is unavailable");
        return false;  // the pool stays (immortal, idle): nothing can submit to it
    }
    g_pool->set_watch_sink(&log_watch, nullptr);
    g_pool->set_note_sink(&log_note, nullptr);
    g_service.store(service, std::memory_order_release);
    caps::tasks_pool_source(&pool_state);
    if (!g_pool->can_run(tasks::Slot::Normal))
        KLOG_W("kiln.tasks: no task worker could start yet (%llu start failure(s)); retried every second, submissions "
               "refuse CAPABILITY until one runs",
               static_cast<unsigned long long>(g_pool->start_failures()));
    KLOG_I("kiln.tasks: %u worker(s), %u LongRunning", g_pool->normal_workers(), g_pool->long_running_workers());
    return true;
}

}  // namespace

// The core's lifecycle calls (tick.cpp on the server or client world's thread, api.cpp's exit trigger). Before init
// (safe idle, no tick hook) they do nothing.
void session_started(uint32_t world, uint32_t session) {
    if (tasks::Service* service = g_service.load(std::memory_order_acquire)) service->session_started(world, session);
}

void post_edge(uint32_t world, uint32_t session, uint64_t step) {
    tasks::Service* service = g_service.load(std::memory_order_acquire);
    if (!service || service->post_edge(world, session, step)) return;
    // a step that is not above the session's last edge, or of another session: refused and counted; said once
    static std::atomic<bool> said{false};
    if (!said.exchange(true))
        KLOG_W("kiln.tasks: a Post edge (%s world, session %u, step %llu) was refused (not the live session, or not "
               "above its last edge); AFTER_STEP tasks wait for the next valid edge",
               world == KILN_WORLD_CLIENT ? "client" : "server", session, static_cast<unsigned long long>(step));
}

void session_stopping(uint32_t world, uint32_t session) {
    if (tasks::Service* service = g_service.load(std::memory_order_acquire)) service->session_stopping(world, session);
}

const void* host_table() { return g_service.load(std::memory_order_acquire) ? &kHostTable : nullptr; }

void tick_hops(uint32_t world) {
    if (tasks::Service* service = g_service.load(std::memory_order_acquire)) {
        try {
            service->run_tick_hops(world);
        } catch (...) {  // nothing leaves the tick hook
        }
    }
}

// Issue #49: the diagnostics window for a tick-stats line. False when the pool does not exist.
bool tasks_window(uint32_t world, bool apply_only, tasks_stats::WindowSnapshot* out) {
    if (!out) return false;
    tasks::Service* service = g_service.load(std::memory_order_acquire);
    if (!service) return false;
    try {
        *out = service->collect_tasks_window(world, apply_only);
        return true;
    } catch (...) {
        return false;
    }
}

// Issue #49: the crash snapshot (running tasks and the last faults). False when the pool does not
// exist; `out->unavailable` says a copy could not be made within its bounded retries.
bool tasks_crash(tasks_stats::CrashSnapshot* out, uint64_t now_ns) {
    if (!out) return false;
    tasks::Service* service = g_service.load(std::memory_order_acquire);
    if (!service || !g_pool) return false;
    *out = g_pool->stats_store().snapshot_crash(now_ns);
    return true;
}

// The pool's stats store for the managed host's crash supplier (tasks_host.h's appended members).
tasks_stats::Store* tasks_store() {
    return g_service.load(std::memory_order_acquire) && g_pool ? &g_pool->stats_store() : nullptr;
}

bool tasks_present() { return g_service.load(std::memory_order_acquire) != nullptr; }

uint64_t tasks_now_ns() { return g_pool ? g_pool->now_ns() : 0; }

void exit_begins() {  // lock-free: also on a crash's exit
    if (tasks::Service* service = g_service.load(std::memory_order_acquire)) service->refuse_new();
}

void exiting() {
    if (tasks::Service* service = g_service.load(std::memory_order_acquire)) service->exiting();
}

// A normal exit, after exiting() and Kiln's own services' exit handlers (the engine's gate is closed by then): the
// pool parks (no body or done starts from here) and the exiting thread waits up to `wait_ms` for the running ones. One
// kiln.log line says what was running and what is left; a body left running can no longer take the engine's gate and
// ends with the process.
void park(uint32_t wait_ms) {
    tasks::Service* service = g_service.load(std::memory_order_acquire);
    if (!service) return;
    try {
        const tasks::Pool::ParkResult r = service->park(uint64_t{wait_ms} * 1000 * 1000);
        const double ms = static_cast<double>(r.waited_ns) / 1e6;
        if (r.still_running)
            KLOG_W("kiln.tasks: parked at exit; %u task callback(s) still running after %.0f ms (%s): they end with the "
                   "process",
                   r.still_running, ms, r.names.empty() ? "done callbacks" : r.names.c_str());
        else if (r.running_at_start)
            KLOG_I("kiln.tasks: parked at exit; the %u running task callback(s) returned in %.1f ms", r.running_at_start, ms);
        else
            KLOG_I("kiln.tasks: parked at exit; no task callback was running");
    } catch (...) {  // nothing leaves the exit sequence
    }
}

}  // namespace kiln::tasks_svc

KILN_SERVICE(tasks, KILN_TASKS_SERVICE, KILN_TASKS_VERSION, &kiln::tasks_svc::kTable, &kiln::tasks_svc::init);
