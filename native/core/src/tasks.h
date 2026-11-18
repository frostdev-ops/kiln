// SPDX-License-Identifier: MIT
// The kiln.tasks pool: real Kiln-owned worker threads that run the work the model (tasks_model.h) hands
// out, each body and done guarded by the platform's fault boundary, with module references held for the callbacks'
// lifetime, first-fault owner quarantine with OS-thread retirement, LongRunning workers, a watchdog clock and off-tick
// maintenance joins. services/tasks.cpp binds the public kiln.tasks table to this pool; the managed host's leases run
// on it too. No user callback ever runs under the pool/model mutex.
//
// Lifetime: all pool state lives in a heap Core that the Pool and every worker thread co-own. A worker that will not
// exit at shutdown (an uncatchable stack overflow under Wine) is detached after a bound and keeps the Core alive, so it
// never touches freed state if it ever resumes; the Core is freed when that thread exits, or never if it does not
// (leaked by design). Nothing in the Core refers back to a worker, so there is no cycle. The Platform must outlive the
// Core. The production pool lives for the whole process, so this only matters at exit.
//
// Done deliveries count toward an owner's normal running cap (2), as in the model: an owner whose two normal bodies
// never return also holds back its queued-cancelled dones, so its drain reports outstanding > 0 until the timeout.
// That is bounded and reported (the watchdog names the long-running bodies), not a leak.
//
// A body that never returns (a stuck mod, or a stack overflow the platform cannot contain) holds only its own owner's
// running slot: other owners keep running, the watchdog names it, and shutdown detaches its worker within the bound.
#pragma once
#include <atomic>
#include <vector>
#include <unordered_map>
#include <deque>
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>

#include "kiln_api.h"
#include "services/kiln_tasks.h"
#include "tasks_host.h"
#include "tasks_model.h"
#include "tasks_platform.h"
#include "tasks_stats.h"

namespace kiln::tasks {

// The body/done callback shapes are the public kiln_tasks.h v1 ones: the pool calls exactly what a plugin passed.
// TaskRun.internal is the pool's per-segment context, never a game pointer.
using TaskRun = KilnTaskRun;
using TaskBodyFn = KilnTaskBodyFn;
using TaskDoneFn = KilnTaskDoneFn;

struct PoolConfig {
    Limits limits;
    uint32_t normal_workers = 0;        // 0 = clamp(cores / 4, 1, 4)
    uint32_t long_running_workers = 0;  // 0 = limits.long_running_global
    uint64_t running_watchdog_ns = 30ull * 1000 * 1000 * 1000;
    uint64_t queued_watchdog_ns = 30ull * 1000 * 1000 * 1000;
    uint64_t shutdown_join_ns = 2ull * 1000 * 1000 * 1000;  // per worker at shutdown; past it the worker is detached
    uint64_t restart_retry_ns = 1ull * 1000 * 1000 * 1000;  // a worker that could not start is tried again after this
    uint64_t watchdog_interval_ns = 1ull * 1000 * 1000 * 1000;  // the maintenance thread's sweep (with a sink set)
};

// Watchdog reports: a task running, or queued, past its age. Report only, never a kill; each task is reported at
// most once per kind (it never repeats every sweep). services/tasks.cpp logs them (kiln.log, one line each).
struct Watch {
    enum class Kind : uint32_t { RunningTooLong, QueuedTooLong };
    Kind kind;
    KilnOwner owner;
    KilnHandle task;
    std::string name;
    uint64_t age_ns;
    std::string owner_name;  // the owner's bounded name (kept after it was revoked)
};
using WatchFn = void (*)(const Watch&, void* user);

// A worker kind lost its last worker (a replacement's start failed: queued work waits for a retried start), or got
// one back. Said once each way, from the maintenance thread outside every lock; admission is the capability's.
struct PoolNote {
    enum class Kind : uint32_t { SlotLost, SlotRecovered };
    Kind kind;
    Slot slot;
    std::size_t queued;  // work ready to run, all owners, when it was noted
};
using NoteFn = void (*)(const PoolNote&, void* user);

class Pool {
public:
    explicit Pool(PoolConfig config = {}, Platform* platform = nullptr);
    ~Pool();  // stops the pool; joins every worker within the bound, detaches one that will not exit
    Pool(const Pool&) = delete;
    Pool& operator=(const Pool&) = delete;

    // ---- owners ----
    KilnResult open_owner(KilnOwner owner, std::string_view name);
    KilnResult ensure_owner(KilnOwner owner, std::string_view name);  // Model::ensure_owner
    KilnResult quarantine(KilnOwner owner);
    // drain_owner: seal, cancel queued, signal running, then wait up to timeout_ns for quiescence. Refused
    // (WRONG_THREAD) on a pool worker for any timeout, so a body or done never drains (the binding adds the tick/world rule).
    // *outstanding = body + done still in flight (0 = quiescent).
    KilnResult drain_owner(KilnOwner owner, uint64_t timeout_ns, uint32_t* outstanding);
    KilnResult retire_owner(KilnOwner owner, uint32_t* outstanding);
    // Process exit: seal every owner (queued tasks cancelled, running ones signalled). Never waits.
    void close();
    // Process exit, after close(): the pool parks for good. From the call on no worker takes another body or done
    // and the maintenance thread stops (no watchdog sweep, no worker start, no log); then the caller waits up to
    // `timeout_ns` for the bodies and dones already running to return. Parked threads wait holding no lock until the
    // process ends them (or the destructor joins them). Never waits for its caller: a body that ends the process
    // parks the pool around itself. Once parked a timed drain_owner no longer waits (no done runs any more).
    struct ParkResult {
        uint32_t running_at_start = 0;  // bodies and dones running when the park began (the caller's own not counted)
        uint32_t still_running = 0;     // ... and still running when it returned (bound reached)
        uint64_t waited_ns = 0;
        std::string names;              // the bodies still running: "<owner>/<task>", comma-separated, at most 8
    };
    ParkResult park(uint64_t timeout_ns);
    bool parked() const;  // lock-free

    // ---- task operations (reentrant: safe to call from inside a body or done) ----
    KilnResult submit(KilnOwner owner, const SubmitArgs& args, TaskBodyFn body, TaskDoneFn done, void* user,
                      KilnHandle* task, Refusal* why);
    KilnResult cancel(KilnOwner owner, KilnHandle task, State* state);
    KilnResult status(KilnOwner owner, KilnHandle task, State* state) const;
    KilnResult release(KilnOwner owner, KilnHandle task);

    // ---- managed leases (the managed host's tasks) ----
    KilnResult arm(KilnHandle task, uint64_t* registration);
    KilnResult resume(KilnHandle task, uint64_t registration);
    KilnResult resume_after(KilnHandle task, uint64_t registration, uint64_t session, uint64_t edge);
    KilnResult resume_after(KilnHandle task, uint64_t registration, uint32_t world, uint64_t session, uint64_t edge);
    KilnResult finish(KilnHandle task, Outcome outcome);
    bool cancel_requested(KilnHandle task) const;
    KilnResult hop_begin(KilnHandle task);
    KilnResult hop_end(KilnHandle task);
    bool lease_of(KilnHandle task, KilnOwner* owner, uintptr_t* user) const;
    // A bounded copy of the running/suspended leases (at most `capacity` rows); BUFFER_TOO_SMALL: *count = needed.
    KilnResult snapshot_leases(KilnLeaseSnapshot* rows, uint32_t capacity, uint32_t* count) const;
    void edge(uint64_t session, uint64_t edge);
    void end_session(uint64_t session);
    void edge(uint32_t world, uint64_t session, uint64_t edge);  // per world (Model::edge)
    void end_session(uint32_t world, uint64_t session);

    // A watchdog sweep against the platform clock: one report per task over its age, with owner and name, delivered
    // to `fn` outside every lock. Gated on the model's real state, so a cancelled or finished task never reports.
    void run_watchdog(WatchFn fn, void* user);
    // The maintenance thread sweeps every watchdog_interval_ns and hands each new report to `fn` (outside every lock,
    // on the maintenance thread). nullptr stops it.
    void set_watch_sink(WatchFn fn, void* user);
    // PoolNote lines (slot lost / recovered), on the maintenance thread outside every lock. nullptr stops them.
    void set_note_sink(NoteFn fn, void* user);

    // Diagnostics (issue #49): this pool's lock-free stats store (tasks_stats.h). The tick-stats
    // writer and crash reports read it without the pool or model lock; collect_tasks_window is the
    // once-a-minute window copy (backlog: tick hops pending then).
    tasks_stats::Store& stats_store();
    const tasks_stats::Store& stats_store() const;
    tasks_stats::WindowSnapshot collect_tasks_window(uint64_t backlog);
    // The same with `world`'s apply account (the pool-wide parts are taken out either way); `apply_only`: only that
    // world's apply account and refused hops (a hosting client's client-world line, beside the server line's window).
    tasks_stats::WindowSnapshot collect_tasks_window(uint32_t world, uint64_t backlog, bool apply_only);

    // Diagnostics / test seams.
    Counts counts() const;
    std::size_t tracked_tasks() const;  // tasks whose metadata (module references, watchdog clocks) is still held
    // Test seam (nothing in production calls it): the bodies and dones a worker holds right now, under the pool mutex;
    // the same count park() reports as running_at_start for a caller that is not a worker.
    uint32_t items_in_hand() const;
    bool is_pool_worker() const;  // the calling thread is one of this pool's workers
    uint32_t normal_workers() const;
    uint32_t long_running_workers() const;
    // Whether work of `slot` can run now: the maintenance thread started and at least one worker of that slot (and,
    // for LongRunning, also a normal worker for its done) is live. submit refuses CAPABILITY otherwise (the
    // tasks.pool capability row reads it). start_failures() counts worker starts that failed (each retried later).
    bool can_run(Slot slot) const;
    int health() const;  // lock-free: 1 while normal work can run (can_run(Normal)), else 0
    uint64_t now_ns() const;  // the platform clock (the tick hops' budget clock; a test's fake one in tests)
    uint64_t current_thread_id() const;  // the platform's id of the calling thread
    // Runs fn(context) inside the platform's fault boundary (the tick hops' dispatcher calls).
    void guarded(GuardedFn fn, void* context, FaultInfo* info);
    uint64_t start_failures() const;
    enum class Point : uint32_t {
        MetaPrepared,        // inside submit, everything allocated, before publication (a test may throw here)
        SubmitPublished,     // inside submit, after the model published, before it returns (under the pool mutex)
        MaintenanceStopped,  // inside shutdown, after the maintenance thread was joined, before the workers are
    };
    void set_test_hook(std::function<void(Point)> hook);
    void set_model_test_hook(std::function<void(Model::Point)> hook);
    static int live_cores();  // pool cores not yet freed (a detached worker keeps its core alive)

    struct Core;  // all state; co-owned by the Pool and its worker threads

private:
    std::shared_ptr<Core> core_;
};

// ---- kiln.tasks v1: the public table's behaviour over one Pool (services/tasks.cpp binds it to the core) ----

// What the binding needs from the core, injectable so host tests drive it without Windows or a game.
class ServiceEnv {
public:
    virtual ~ServiceEnv() = default;
    // The calling thread is the tick thread or any world thread (KilnApi.on_world_thread for any kind).
    virtual bool on_tick_or_world_thread() = 0;
    virtual bool owner_alive(KilnOwner owner) = 0;
    virtual std::string owner_name(KilnOwner owner) = 0;
    // The host's dispatcher faulted on a tick hop of `lease` (structured fault `code`): the binding contained it.
    virtual void hop_faulted(KilnOwner owner, KilnLease lease, uint32_t code) { (void)owner, (void)lease, (void)code; }
};

class Service {
public:
    Service(Pool& pool, ServiceEnv& env);
    Service(const Service&) = delete;
    Service& operator=(const Service&) = delete;

    // ---- kiln_tasks.h v1 members (the contract is the header's) ----
    KilnResult submit(KilnOwner owner, const KilnTaskInfo* info, KilnTaskBodyFn body, KilnTaskDoneFn done, void* user,
                      KilnHandle* task, uint32_t* refusal);
    KilnResult cancel(KilnOwner owner, KilnHandle task, uint32_t* state);
    KilnResult status(KilnOwner owner, KilnHandle task, uint32_t* state);
    KilnResult release(KilnOwner owner, KilnHandle task);
    KilnResult drain_owner(KilnOwner owner, uint32_t timeout_ms, uint32_t* running);
    // ---- appended to kiln_tasks.h v1: submit bound to a world's session ----
    KilnResult submit_in(KilnOwner owner, const KilnTaskInfo* info, uint32_t world, uint32_t session, KilnTaskBodyFn body,
                         KilnTaskDoneFn done, void* user, KilnHandle* task, uint32_t* refusal);

    // ---- the core's lifecycle edges, per world (KILN_WORLD_SERVER and KILN_WORLD_CLIENT; others are ignored) ----
    // A world's sessions and Post edges (on its thread): one snapshot per world that AFTER_STEP captures whole.
    void session_started(uint32_t world, uint32_t session);
    // A Post edge: `step` is KilnTickInfo.step (from 1, strictly increasing). Refused (false, nothing changes) for a
    // session that is not that world's live one, a step 0 or one not above the session's last edge.
    bool post_edge(uint32_t world, uint32_t session, uint64_t step);
    // The world's STOPPING (on the destroying thread): that world's waiting tasks are cancelled and its pending tick
    // hops are dispatched cancelled; another world's are untouched.
    void session_stopping(uint32_t world, uint32_t session);
    // The server world's (the forms the core used before client-world hops; tests keep them).
    void session_started(uint32_t session) { session_started(KILN_WORLD_SERVER, session); }
    bool post_edge(uint32_t session, uint64_t step) { return post_edge(KILN_WORLD_SERVER, session, step); }
    void session_stopping(uint32_t session) { session_stopping(KILN_WORLD_SERVER, session); }
    void owner_retiring(KilnOwner owner);  // the registry's owner-retiring observer: nonblocking
    // The registry callback (reg::add_owner_retiring, observer owner 0, user = the Service): retires the owner's tasks
    // (no new ones, queued ones cancelled with their dones, running ones signalled). It never waits and never calls
    // into the registry, so owner_revoke stays nonblocking; revocation is not callback quiescence (drain_owner is).
    static void KILN_CALL owner_retiring_observer(KilnOwner retiring, void* service);
    void exiting();                        // the process exit begins: SHUTDOWN from now on, every owner sealed
    void refuse_new();                     // any exit, also a crash's: SHUTDOWN from now on; one atomic store, no lock
    // A normal exit, after exiting(): the pool parks (Pool::park); tick hops stay the tick's (a STOPPING ends them).
    Pool::ParkResult park(uint64_t timeout_ns);

    struct Snapshot {
        uint32_t session = 0;  // the newest session this world started (kept after its STOPPING)
        uint64_t edge = 0;
        bool live = false;
    };
    Snapshot snapshot(uint32_t world = KILN_WORLD_SERVER) const;
    uint64_t refused_edges(uint32_t world = KILN_WORLD_SERVER) const;
    // tick-stats (issue #49): the pool's diagnostics window, collected without a pool lock.
    tasks_stats::WindowSnapshot collect_tasks_window() {
        return pool_.collect_tasks_window(KILN_WORLD_SERVER, pending_tick_hops(KILN_WORLD_SERVER), false);
    }
    // A world's line: the whole window with that world's apply account, or (apply_only) that account alone.
    tasks_stats::WindowSnapshot collect_tasks_window(uint32_t world, bool apply_only) {
        return pool_.collect_tasks_window(world, pending_tick_hops(world), apply_only);
    }

    // ---- kiln.internal.tasks.host v1 (tasks_host.h): the managed host's leases on the same pool ----
    KilnResult host_bind(KilnHostDispatchFn dispatch);
    KilnResult host_admit(KilnOwner owner, const KilnTaskInfo* info, void* host_state, KilnLease* lease,
                          uint32_t* refusal);
    KilnResult host_arm(KilnLease lease, uint64_t* registration);
    KilnResult host_resume(KilnLease lease, uint64_t registration);
    KilnResult host_resume_after_step(KilnLease lease, uint64_t registration, uint32_t* refusal);
    KilnResult host_finish(KilnLease lease, uint32_t outcome);
    uint32_t host_is_cancelled(KilnLease lease);
    KilnResult host_seal_owner(KilnOwner owner, uint32_t* outstanding);
    KilnResult host_snapshot(KilnLeaseSnapshot* rows, uint32_t capacity, uint32_t* count);
    KilnHostDispatchFn dispatcher() const { return dispatch_.load(std::memory_order_acquire); }
    // The appended members (tasks_host.h): the end notification (with it bound the core releases an ended lease's
    // record itself), the after-step resumption for a named session, and the thread and session facts.
    KilnResult host_bind_ended(KilnHostEndedFn ended);
    KilnHostEndedFn ended() const { return ended_.load(std::memory_order_acquire); }
    KilnResult host_resume_after_step_in(KilnLease lease, uint64_t registration, uint32_t session, uint32_t* refusal);
    uint32_t host_on_pool_thread() const;
    uint32_t host_server_session() const;
    // ---- appended (client-world hops) ----
    // The newest session of `world` and its state (*state: 0 none yet, 1 live, 2 its STOPPING came).
    uint32_t host_world_session(uint32_t world, uint32_t* state) const;
    // A tick hop for `world`'s `session`: queued only while that session is the live, open one (one lock for the
    // compare and the push); UNAVAILABLE otherwise, INVALID_ARGUMENT for another world kind, INTERNAL as host_tick_hop.
    KilnResult host_tick_hop_in(KilnLease lease, uint32_t world, uint32_t session);
    // host_resume_after_step_in for `world` (UNAVAILABLE + NO_SERVER_WORLD / NO_WORLD with the registration armed).
    KilnResult host_resume_after_step_world(KilnLease lease, uint64_t registration, uint32_t world, uint32_t session,
                                            uint32_t* refusal);
    // The core's done trampoline of a lease, after the host's end notification returned: drops the record.
    void lease_ended(KilnOwner owner, KilnLease lease);
    // A bounded tick hop for the lease: dispatched as KILN_LEASE_TICK_HOP on the live server world's tick, before its
    // Pre subscribers (run_tick_hops). UNAVAILABLE with no live server world (never waits for one); INTERNAL for a
    // lease that is unknown or already quiescent. Allowed after a seal; a retiring lease's hop runs with cancelled=1.
    KilnResult host_tick_hop(KilnLease lease);
    // A world's Pre (tick.cpp), before its subscribers: that world's queued hops, FIFO per owner, owners round-robin
    // (the turn moves on from step to step); per owner at most 64 hops and its owner budget, all owners at most the
    // step budget, checked between callbacks; the rest wait for the next step, never dropped. Returns the hops
    // dispatched. The server world: 2 ms per owner and 4 ms per step; the client world: 1 ms and 2 ms (the game's
    // main thread also builds the UI and draws faster than the client world steps). The worlds never share a budget.
    std::size_t run_tick_hops(uint32_t world);
    std::size_t pending_tick_hops(uint32_t world) const;
    std::size_t run_tick_hops() { return run_tick_hops(KILN_WORLD_SERVER); }
    std::size_t pending_tick_hops() const { return pending_tick_hops(KILN_WORLD_SERVER); }

    static constexpr std::size_t kHopsPerOwner = 64;
    static constexpr uint64_t kHopOwnerNs = 2ull * 1000 * 1000, kHopStepNs = 4ull * 1000 * 1000;
    static constexpr uint64_t kClientHopOwnerNs = 1ull * 1000 * 1000, kClientHopStepNs = 2ull * 1000 * 1000;

    // Test seams (outside every lock): submit passed its owner_alive check / AFTER_STEP captured its snapshot.
    enum class Point : uint32_t { AfterAliveCheck, AfterCapture, HopBeforePush };
    void set_test_hook(std::function<void(Point)> hook);

private:
    KilnResult open(KilnOwner owner);
    // `bound` (submit_in): the task is for `world`'s `session` (0: the live one): refused NO_WORLD unless it is live,
    // and an AFTER_STEP task waits for that world's edge. Unbound: AFTER_STEP is the live server session's.
    KilnResult submit_as(KilnOwner owner, const KilnTaskInfo* info, KilnTaskBodyFn body, KilnTaskDoneFn done, void* user,
                         bool lease, KilnHandle* task, uint32_t* refusal, uint32_t world = KILN_WORLD_SERVER,
                         uint32_t session = 0, bool bound = false);

    Pool& pool_;
    ServiceEnv& env_;
    mutable std::mutex mutex_;  // the session snapshots
    Snapshot snap_[kWorldLines];  // per world_line
    uint64_t refused_edges_[kWorldLines] = {};
    std::atomic<bool> exiting_{false};
    std::atomic<KilnHostDispatchFn> dispatch_{nullptr};  // the managed host's one dispatcher (host_bind)
    std::atomic<KilnHostEndedFn> ended_{nullptr};        // its end notification (host_bind_ended)
    // Pending tick hops (the lease and the host's state for it), per owner.
    struct Hop {
        KilnLease lease;
        void* host_state;
    };
    mutable std::mutex lease_mutex_;
    // One world's hop queue (all under lease_mutex_): per owner, in the order owners first queued; the owner whose turn
    // comes first next step; whether a live session takes hops (its STARTED opens, its STOPPING closes), and which.
    struct HopQueue {
        std::vector<std::pair<KilnOwner, std::deque<Hop>>> owners;
        std::size_t turn = 0;
        bool open = false;
        uint32_t session = 0;
    };
    HopQueue hops_[kWorldLines];
    KilnResult push_hop(KilnLease lease, uint32_t world, uint32_t session, bool check_session);
    // `count_apply`: the dispatch is accounted in the window's apply stats here (the tick's
    // run_tick_hops accounts its whole round itself, so it passes false).
    void dispatch_hop(KilnHostDispatchFn dispatch, uint32_t world, KilnLease lease, void* host_state, uint32_t cancelled,
                      bool count_apply = true);
    void dispatch_hops_cancelled(uint32_t world);  // a session's STOPPING: every pending hop of that world, cancelled

    std::function<void(Point)> hook_;
};

}  // namespace kiln::tasks
