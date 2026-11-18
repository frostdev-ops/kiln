// SPDX-License-Identifier: MIT
// The kiln.tasks pool: see tasks.h. Worker threads run the model's dequeued work under the platform's
// fault boundary; bodies and done callbacks never run under the pool or model mutex, and the callbacks' modules stay
// referenced until their task is quiescent. The first structured native fault in an owner's callback quarantines that
// owner and retires the faulting OS thread; a replacement starts only after the old thread has exited.
//
// Locks: the pool mutex may be held while calling into the model (the model is a leaf that never calls out); nothing
// holds the model mutex and then takes the pool mutex. No callback runs under either.
#include "tasks.h"

#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <deque>
#include <mutex>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace kiln::tasks {

namespace {
std::atomic<int> g_live_cores{0};
std::size_t slot_index_of(Slot s) { return s == Slot::Normal ? 0 : 1; }

uint32_t default_normal_workers() {
    const unsigned hw = std::max(1u, std::thread::hardware_concurrency());
    return std::clamp(hw / 4, 1u, 4u);
}
}  // namespace

// Per-accepted-task metadata beside the model's record: the callbacks' module references (held until done returns),
// the owner and a copy of the name (thread description, watchdog), and the watchdog clocks. Registered in the same
// pool critical section that publishes the task, so a worker can never see the task without it.
struct Meta {
    ModuleRef body, done;
    KilnOwner owner = 0;
    std::string name;
    uint64_t queued_since = 0;   // when it was accepted (the watchdog checks the model's state too)
    uint64_t running_since = 0;  // when its current body started (valid while `running`)
    bool running = false;
    bool reported_running = false, reported_queued = false;  // the watchdog says each once
    bool lease = false;  // a managed lease (snapshot)
    int stats_slot = -1, stats_entry = -1;  // tasks_stats.h rows (issue #49); -1 when its tables were full
};

struct Worker {
    std::shared_ptr<Pool::Core> core;  // the thread co-owns the pool state
    Slot slot = Slot::Normal;
    uint32_t index = 0;
    uint64_t serial = 0;  // this incarnation, assigned by the pool: an OS thread id/handle value may be reused
    ThreadHandle thread{};
    bool faulted = false;  // guarded by core->mutex: set when this worker must retire
    bool maintenance = false;
};

// The PODs a callback runs with (GuardedHeader first): no destructor is live across the fault boundary.
struct Segment {
    GuardedHeader header;  // header.callback = the body address
    TaskRun run;
};
struct DoneCall {
    GuardedHeader header;  // header.callback = the done address
    TaskDoneFn done;
    KilnHandle task;
    uint32_t state;
    void* user;
};

struct Pool::Core : std::enable_shared_from_this<Pool::Core> {
    Core(PoolConfig c, Platform& p) : platform(p), model(c.limits), config(c) { g_live_cores.fetch_add(1); }
    ~Core() { g_live_cores.fetch_sub(1); }

    Platform& platform;
    Model model;
    tasks_stats::Store stats;  // issue #49: fixed-size, lock-free; read by tick-stats and crash reports
    PoolConfig config;
    uint32_t normal_workers = 0, long_running_workers = 0;

    mutable std::mutex mutex;  // wake generation, metadata, roster, retirement
    std::condition_variable work_cv, exit_cv, maintenance_cv;
    uint64_t generation = 0;
    bool stopping = false;
    // The process exit parked the pool (park): no worker takes another item, the maintenance thread does nothing.
    // Written under `mutex` (every waiter's predicate reads it there), never cleared; atomic for drain_owner's read.
    std::atomic<bool> parking{false};
    uint32_t busy = 0;              // workers past the park's line in run_one: the park waits for them (under mutex)
    uint32_t in_item = 0;           // ... of which have a body or done in hand: what the park reports (under mutex)
    bool maintenance_busy = false;  // the maintenance thread is outside its wait (under mutex)

    std::unordered_map<KilnHandle, std::shared_ptr<Meta>> meta;
    std::vector<std::weak_ptr<Worker>> roster;  // one slot per logical worker (weak: threads own their Worker)
    // Faulted worker threads awaiting join by maintenance. Plain data, never a Worker: a Worker co-owns this Core, so
    // holding one here would be a reference cycle that leaks the Core when a fault lands during or after shutdown.
    struct Retired {
        ThreadHandle thread;
        uint64_t serial;
        Slot slot;
        uint32_t index;
    };
    std::deque<Retired> retired;
    struct Live {
        ThreadHandle thread;
        uint64_t serial;
    };
    std::vector<Live> live_threads;               // every started worker thread not yet joined (one join set)
    std::unordered_set<uint64_t> exited;          // serials of worker incarnations that returned from their loop
    uint64_t next_serial = 0;
    // Per slot kind (Normal, LongRunning): logical workers that run, or whose replacement after a fault is under way.
    // A worker counts out only when its start (or its replacement's) failed, and back in when a retry started it:
    // a fault alone never makes the pool refuse work for the moment its replacement takes.
    uint32_t healthy[2] = {0, 0};
    std::vector<std::pair<Slot, uint32_t>> pending_starts;  // workers that could not start: retried by maintenance
    uint64_t start_failures = 0;
    std::chrono::steady_clock::time_point next_retry{};  // when pending_starts are tried again
    std::chrono::steady_clock::time_point next_sweep{};  // the next watchdog sweep
    WatchFn watch_fn = nullptr;  // the periodic watchdog sweep's sink (set_watch_sink)
    bool maintenance_kick = false;  // set_watch_sink changed the schedule: the maintenance thread re-plans its wait
    NoteFn note_fn = nullptr;  // slot lost / recovered (set_note_sink)
    void* note_user = nullptr;
    bool slot_lost[2] = {false, false};  // a replacement's failed start took the slot's last worker
    bool slot_said[2] = {false, false};  // ...and that was said (once; recovery said once too)
    void* watch_user = nullptr;
    ThreadHandle maintenance_thread{};
    bool maintenance_started = false;
    uint32_t stuck_workers = 0;
    std::function<void(Point)> hook;

    // mutex held; `counted`: the slot is already in `healthy` (a replacement). false: queued in pending_starts for a
    // retry, and out of `healthy`.
    bool start_worker(Slot slot, uint32_t index, bool counted);
    void start_maintenance();                      // mutex held
    bool can_run_locked(Slot slot) const;          // mutex held
    void publish_health_locked();                  // mutex held: `health` follows can_run_locked(Normal)
    std::atomic<int> health{0};                    // lock-free copy for the capability row (1 = normal work runs)
    static void thread_entry(void* context);
    void worker_loop(Worker& self);
    bool run_one(Worker& self);
    void deliver_body(Worker& self, Work& work);
    void deliver_done(Worker& self, Work& work);
    void on_fault(Worker& self, KilnOwner owner);
    void maintenance_loop();
    void sweep_watchdog(WatchFn fn, void* user);
    void wake_all();
    void leave_busy(bool had_item);
    void release_meta(KilnHandle task);
    std::shared_ptr<Meta> find_meta(KilnHandle task);
    void shutdown();
};

namespace {
thread_local Pool::Core* t_core = nullptr;   // the pool core whose worker this thread is, else nullptr
thread_local bool t_segment_armed = false;  // the running body armed a resumption (managed lease): it suspends
thread_local KilnHandle t_running_task = 0;  // the task whose body this worker runs now (park's names)

uint32_t KILN_CALL run_cancelled(const TaskRun* run) {
    // `internal` is the Work* of the running segment, valid only during the callback.
    const auto* work = static_cast<const Work*>(run->internal);
    return work && work->cancel_requested() ? 1 : 0;
}

void invoke_body(void* context) {
    auto* seg = static_cast<Segment*>(context);
    const auto body = reinterpret_cast<TaskBodyFn>(seg->header.callback);
    const auto* work = static_cast<const Work*>(seg->run.internal);
    body(&seg->run, reinterpret_cast<void*>(work->payload.user));
}

void invoke_done(void* context) {
    auto* call = static_cast<DoneCall*>(context);
    call->done(call->task, call->state, call->user);
}
}  // namespace

// ---- threads ----

bool Pool::Core::start_worker(Slot slot, uint32_t index, bool counted) {
    auto w = std::make_shared<Worker>();
    w->core = shared_from_this();
    w->slot = slot;
    w->index = index;
    w->serial = ++next_serial;
    if (index >= roster.size()) roster.resize(index + 1);
    auto* context = new std::shared_ptr<Worker>(w);  // the thread's own reference, released when its loop returns
    w->thread = platform.start_thread(slot == Slot::Normal ? "kiln-task-N" : "kiln-task-L", &Core::thread_entry, context);
    if (w->thread.id == 0) {  // it does not exist: counted, and maintenance tries again (submit refuses meanwhile
        delete context;       // when no worker of the slot is live, so accepted work is never stranded silently)
        ++start_failures;
        if (pending_starts.empty())
            next_retry = std::chrono::steady_clock::now() + std::chrono::nanoseconds(config.restart_retry_ns);
        pending_starts.emplace_back(slot, index);
        if (counted) {
            --healthy[slot_index_of(slot)];
            if (healthy[slot_index_of(slot)] == 0) slot_lost[slot_index_of(slot)] = true;
            publish_health_locked();
        }
        maintenance_cv.notify_all();
        return false;
    }
    live_threads.push_back({w->thread, w->serial});
    roster[index] = w;
    if (!counted) {
        ++healthy[slot_index_of(slot)];
        publish_health_locked();
    }
    return true;
}

void Pool::Core::start_maintenance() {
    auto w = std::make_shared<Worker>();
    w->core = shared_from_this();
    w->maintenance = true;
    auto* context = new std::shared_ptr<Worker>(w);
    maintenance_thread = platform.start_thread("kiln-task-maint", &Core::thread_entry, context);
    maintenance_started = maintenance_thread.id != 0;
    if (!maintenance_started) delete context;
    publish_health_locked();
}

void Pool::Core::thread_entry(void* context) {
    std::unique_ptr<std::shared_ptr<Worker>> own(static_cast<std::shared_ptr<Worker>*>(context));
    const std::shared_ptr<Worker> self = *own;
    const std::shared_ptr<Core> core = self->core;  // keeps the state alive for as long as this thread runs
    if (self->maintenance) core->maintenance_loop();
    else core->worker_loop(*self);
}

void Pool::Core::worker_loop(Worker& self) {
    t_core = this;
    platform.lower_current_priority();
    uint64_t seen = 0;
    bool done = false;
    while (!done) {
        {
            // parked (the process exit): it waits here, holding nothing, until the process ends (or the destructor)
            std::unique_lock lock(mutex);
            work_cv.wait(lock, [&] { return stopping || self.faulted || (!parking.load() && generation != seen); });
            if (stopping || self.faulted) done = true;
            seen = generation;
        }
        while (!done && run_one(self)) {  // drain the ready work for this slot before sleeping again
            std::lock_guard l(mutex);
            if (stopping || self.faulted) done = true;
            seen = generation;
        }
    }
    {
        std::lock_guard lock(mutex);
        exited.insert(self.serial);
    }
    exit_cv.notify_all();
    t_core = nullptr;
}

bool Pool::Core::run_one(Worker& self) {
    {
        // The park's line: an item is taken only while the pool is not parked, and counted from before it is taken,
        // so park() sees every body or done that can still start.
        std::lock_guard lock(mutex);
        if (parking.load()) return false;
        ++busy;
    }
    Work work;
    if (!model.dequeue(self.slot, &work)) {
        leave_busy(false);
        return false;
    }
    {
        // submit registers the metadata in the same critical section that publishes the task, so it is here
        std::lock_guard lock(mutex);
        ++in_item;
        const auto it = meta.find(work.task);
        if (it != meta.end() && work.kind != Work::Kind::Done) {
            it->second->running_since = platform.now_ns();
            it->second->running = true;
        }
    }
    if (work.kind == Work::Kind::Done) deliver_done(self, work);
    else deliver_body(self, work);
    leave_busy(true);
    return true;
}

void Pool::Core::leave_busy(bool had_item) {
    {
        std::lock_guard lock(mutex);
        --busy;
        if (had_item) --in_item;
    }
    exit_cv.notify_all();  // a park waiting for the running callbacks
}

std::shared_ptr<Meta> Pool::Core::find_meta(KilnHandle task) {
    std::lock_guard lock(mutex);
    const auto it = meta.find(task);
    return it == meta.end() ? nullptr : it->second;
}

void Pool::Core::deliver_body(Worker& self, Work& work) {
    const std::shared_ptr<Meta> m = find_meta(work.task);
    const std::string desc = model.owner_name(work.owner) + "/" + (m ? m->name : std::string());
    platform.set_description(desc.c_str());
    Segment seg{};
    seg.header.callback = reinterpret_cast<uintptr_t>(work.payload.body);
    seg.run.size = sizeof(TaskRun);
    seg.run.task = work.task;
    seg.run.owner = work.owner;
    seg.run.cancelled = &run_cancelled;
    seg.run.internal = &work;
    t_segment_armed = false;
    t_running_task = work.task;
    // Diagnostics (issue #49): two clock reads around the segment only — the wall time and this
    // thread's CPU time (awaited IO advances the wall clock, not the CPU clock).
    if (m) stats.segment_started(m->stats_slot, m->stats_entry, platform.current_thread_id(), platform.now_ns());
    const uint64_t wall0 = platform.now_ns(), cpu0 = platform.thread_cpu_ns();
    FaultInfo info{};
    if (work.payload.body) platform.guarded_call(&invoke_body, &seg, &info);
    const uint64_t wall1 = platform.now_ns(), cpu1 = platform.thread_cpu_ns();
    t_running_task = 0;
    const bool suspended = t_segment_armed;  // the body armed a resumption (managed lease): it is not terminal
    t_segment_armed = false;
    if (m) {
        stats.segment_ended(m->stats_slot, m->stats_entry, cpu1 >= cpu0 ? cpu1 - cpu0 : 0,
                            wall1 >= wall0 ? wall1 - wall0 : 0, suspended);
        if (info.faulted) stats.note_fault_code(m->stats_entry, info.code);
    }
    platform.set_description(self.slot == Slot::Normal ? "kiln-task-N" : "kiln-task-L");
    if (m) {
        std::lock_guard lock(mutex);
        m->running = false;
    }
    if (info.faulted) {
        model.segment_ended(work, false, Outcome::Faulted);  // publish Faulted, queue its done for a healthy worker
        on_fault(self, work.owner);
        wake_all();
        return;
    }
    // A native body returns void: it cannot say it stopped because of the cancel, so a normal return is Completed
    // (Completed wins over a late cancel). OwnTokenCancelled comes only from the managed host's explicit outcome.
    model.segment_ended(work, suspended, Outcome::Completed);
    wake_all();
}

void Pool::Core::deliver_done(Worker& self, Work& work) {
    // Diagnostics (issue #49): the terminal count lands exactly once per accepted task, here,
    // before its done runs (a faulting done is still its task's terminal state).
    if (const std::shared_ptr<Meta> m = find_meta(work.task))
        stats.terminal(m->stats_slot, m->stats_entry, static_cast<uint32_t>(work.final_state), 0, platform.now_ns());
    DoneCall call{};
    call.header.callback = work.payload.done;
    call.done = reinterpret_cast<TaskDoneFn>(work.payload.done);
    call.task = work.task;
    call.state = static_cast<uint32_t>(work.final_state);
    call.user = reinterpret_cast<void*>(work.payload.user);
    FaultInfo info{};
    if (call.done) platform.guarded_call(&invoke_done, &call, &info);
    // the one done attempt has happened (faulted or not): quiescent; drop its metadata and module references
    model.done_returned(work);
    release_meta(work.task);
    wake_all();
    if (info.faulted) on_fault(self, work.owner);
}

void Pool::Core::on_fault(Worker& self, KilnOwner owner) {
    model.quarantine(owner);  // first fault: no new roots, queued roots cancelled
    std::lock_guard lock(mutex);
    self.faulted = true;  // this thread retires; maintenance joins it and starts a replacement
    for (auto& weak : roster) {
        if (auto w = weak.lock(); w.get() == &self) {
            retired.push_back({w->thread, w->serial, w->slot, w->index});  // stays counted: replaced next
            weak.reset();
        }
    }
    maintenance_cv.notify_all();
}

void Pool::Core::maintenance_loop() {
    for (;;) {
        std::vector<Retired> to_join;
        struct Start {
            Slot slot;
            uint32_t index;
            bool counted;
        };
        std::vector<Start> to_start;
        WatchFn sweep = nullptr;
        void* sweep_user = nullptr;
        {
            std::unique_lock lock(mutex);
            if (maintenance_busy) {
                maintenance_busy = false;  // back in its wait: a park may go on
                exit_cv.notify_all();
            }
            const auto ready = [&] { return stopping || parking.load() || !retired.empty() || maintenance_kick; };
            // Wake for a retirement, for the retry of a worker that could not start (restart_retry_ns after its
            // failure), and for the watchdog sweep (every watchdog_interval_ns while a sink is set).
            using Clock = std::chrono::steady_clock;
            auto wake = Clock::time_point::max();
            if (!pending_starts.empty()) wake = std::min(wake, next_retry);
            if (watch_fn) wake = std::min(wake, next_sweep);
            if (wake == Clock::time_point::max()) maintenance_cv.wait(lock, ready);
            else maintenance_cv.wait_until(lock, wake, ready);
            // parked (the process exit): no join, no start, no sweep, no note from here on; it waits, holding
            // nothing, until the process ends (or the destructor stops it)
            if (parking.load()) maintenance_cv.wait(lock, [&] { return stopping; });
            if (stopping && retired.empty()) return;
            maintenance_busy = true;
            maintenance_kick = false;
            const auto now = Clock::now();
            for (const Retired& w : retired) {
                to_join.push_back(w);
                // out of the join set under the lock, so shutdown never joins it a second time
                live_threads.erase(std::remove_if(live_threads.begin(), live_threads.end(),
                                                  [&](const Live& l) { return l.serial == w.serial; }),
                                   live_threads.end());
                if (!stopping) to_start.push_back({w.slot, w.index, true});
            }
            // a failed start is tried again when due, or with a retirement's replacement
            if (!stopping && !pending_starts.empty() && (now >= next_retry || !retired.empty())) {
                for (auto [slot, index] : pending_starts) to_start.push_back({slot, index, false});
                pending_starts.clear();
            }
            retired.clear();
            if (watch_fn && now >= next_sweep) {
                sweep = watch_fn;
                sweep_user = watch_user;
                next_sweep = now + std::chrono::nanoseconds(config.watchdog_interval_ns);
            }
        }
        for (const Retired& w : to_join)
            if (w.thread.id) platform.join_thread(w.thread);  // replace only after the old thread has exited
        std::vector<PoolNote> notes;
        NoteFn note = nullptr;
        void* note_to = nullptr;
        {
            std::lock_guard lock(mutex);
            for (const Retired& w : to_join) exited.erase(w.serial);
            if (stopping) return;
            bool started = false;
            for (const Start& st : to_start) started |= start_worker(st.slot, st.index, st.counted);
            if (started) {
                ++generation;  // a new worker: let it look for work accepted while none could run
                work_cv.notify_all();
            }
            // a slot that lost its last worker, or got one back: said once each way (no admission change here)
            for (const Slot slot : {Slot::Normal, Slot::LongRunning}) {
                const std::size_t i = slot_index_of(slot);
                if (slot_lost[i] && !slot_said[i]) {
                    slot_said[i] = true;
                    notes.push_back({PoolNote::Kind::SlotLost, slot, 0});
                } else if (slot_said[i] && healthy[i] > 0) {
                    slot_said[i] = slot_lost[i] = false;
                    notes.push_back({PoolNote::Kind::SlotRecovered, slot, 0});
                }
            }
            if (!notes.empty()) {
                const std::size_t ready = model.counts().ready;  // pool -> model lock order
                for (PoolNote& n : notes) n.queued = ready;
            }
            note = note_fn;
            note_to = note_user;
        }
        if (note)
            for (const PoolNote& n : notes) note(n, note_to);  // outside every lock: the sink logs
        if (sweep) sweep_watchdog(sweep, sweep_user);  // outside every lock: the sink logs
    }
}

void Pool::Core::wake_all() {
    {
        std::lock_guard lock(mutex);
        ++generation;
    }
    work_cv.notify_all();
}

void Pool::Core::release_meta(KilnHandle task) {
    std::shared_ptr<Meta> m;
    {
        std::lock_guard lock(mutex);
        const auto it = meta.find(task);
        if (it == meta.end()) return;
        m = it->second;
        meta.erase(it);  // the watchdog clocks go with it: nothing outlives the task
    }
    platform.release_module(m->body);
    platform.release_module(m->done);
}

void Pool::Core::shutdown() {
    {
        std::lock_guard lock(mutex);
        stopping = true;
    }
    work_cv.notify_all();
    maintenance_cv.notify_all();
    if (maintenance_started) platform.join_thread(maintenance_thread);
    std::vector<Live> to_join;
    std::function<void(Point)> test_hook;
    {
        std::lock_guard lock(mutex);
        to_join.swap(live_threads);
        test_hook = hook;
    }
    if (test_hook) test_hook(Point::MaintenanceStopped);
    // A worker that returned from its loop is joined; one that will not exit within the bound is detached. It keeps
    // this Core alive through its own reference, so it never touches freed state if it resumes.
    // Exits are tracked per incarnation (serial), never by the OS id: a handle value of a joined worker may be reused.
    for (const Live& l : to_join) {
        if (!l.thread.id) continue;
        std::unique_lock lock(mutex);
        const bool exited_in_time = exit_cv.wait_for(lock, std::chrono::nanoseconds(config.shutdown_join_ns),
                                                     [&] { return exited.count(l.serial) != 0; });
        if (!exited_in_time) ++stuck_workers;
        lock.unlock();
        if (exited_in_time) platform.join_thread(l.thread);
        else platform.detach_thread(l.thread);
    }
}

// ---- Pool: forwards to the core ----

Pool::Pool(PoolConfig config, Platform* platform)
    : core_(std::make_shared<Core>(config, platform ? *platform : default_platform())) {
    Core& c = *core_;
    c.normal_workers = config.normal_workers ? config.normal_workers : default_normal_workers();
    c.long_running_workers = config.long_running_workers ? config.long_running_workers : config.limits.long_running_global;
    c.stats.begin(c.platform.now_ns());
    c.stats.set_pool_counts(c.normal_workers, c.long_running_workers);
    std::lock_guard lock(c.mutex);
    c.start_maintenance();
    uint32_t index = 0;
    for (uint32_t i = 0; i < c.normal_workers; ++i) c.start_worker(Slot::Normal, index++, false);
    for (uint32_t i = 0; i < c.long_running_workers; ++i) c.start_worker(Slot::LongRunning, index++, false);
}

Pool::~Pool() { core_->shutdown(); }

KilnResult Pool::open_owner(KilnOwner owner, std::string_view name) { return core_->model.open_owner(owner, name); }
KilnResult Pool::ensure_owner(KilnOwner owner, std::string_view name) { return core_->model.ensure_owner(owner, name); }

KilnResult Pool::submit(KilnOwner owner, const SubmitArgs& in, TaskBodyFn body, TaskDoneFn done, void* user,
                        KilnHandle* task, Refusal* why) {
    Core& c = *core_;
    if (!body || !done) {
        if (task) *task = 0;
        if (why) *why = Refusal::Argument;
        return KILN_ERR_INVALID_ARGUMENT;
    }
    const Slot slot = in.long_running ? Slot::LongRunning : Slot::Normal;
    if (!can_run(slot)) {  // never accept work no worker can run (checked again at publication)
        if (task) *task = 0;
        if (why) *why = Refusal::Capability;
        return KILN_ERR_UNAVAILABLE;
    }
    // A submission is all or nothing. Every allocation happens before publication; the module references are owned
    // by `refs` until the metadata takes them, so an exception (or a refusal) anywhere before that releases them and
    // leaves nothing a worker could see. Publication itself (the model's, transactional too) and the metadata commit
    // into reserved room do not throw.
    struct Refs {
        Platform& platform;
        ModuleRef body{}, done{};
        bool owned = true;
        ~Refs() {
            if (!owned) return;
            platform.release_module(body);
            platform.release_module(done);
        }
    } refs{c.platform};
    refs.body = c.platform.acquire_module(reinterpret_cast<uintptr_t>(body));  // before acceptance
    refs.done = c.platform.acquire_module(reinterpret_cast<uintptr_t>(done));
    auto m = std::make_shared<Meta>();
    m->owner = owner;
    m->name = std::string(in.name);
    m->lease = in.lease;
    std::unordered_map<KilnHandle, std::shared_ptr<Meta>> spare;  // the metadata map's node, allocated here
    spare.emplace(0, m);
    auto node = spare.extract(spare.begin());
    SubmitArgs args = in;
    args.payload = {reinterpret_cast<uintptr_t>(body), reinterpret_cast<uintptr_t>(done), reinterpret_cast<uintptr_t>(user)};
    {
        std::function<void(Point)> prep;
        {
            std::lock_guard lock(c.mutex);
            prep = c.hook;
        }
        if (prep) prep(Point::MetaPrepared);
    }
    KilnHandle handle = 0;
    KilnResult r = KILN_OK;
    Refusal submit_why = Refusal::None;
    const std::string owner_name_copy = c.model.owner_name(owner);  // bounded copy, before the pool mutex
    {
        // Publish and register in ONE pool critical section: a worker that dequeues the task at once (from its drain
        // loop, without waiting for a wake) blocks on this mutex in run_one until the metadata is in place. The
        // capability check again here: a worker may have faulted (and its replacement failed) meanwhile.
        std::lock_guard lock(c.mutex);
        c.meta.reserve(c.meta.size() + 1);  // may throw: still before publication
        if (!c.can_run_locked(slot)) {
            if (why) *why = Refusal::Capability;
            r = KILN_ERR_UNAVAILABLE;
        } else {
            r = c.model.submit(owner, args, &handle, &submit_why);
            if (why) *why = submit_why;
        }
        if (r == KILN_OK) {
            m->body = refs.body;
            m->done = refs.done;
            refs.owned = false;
            m->queued_since = c.platform.now_ns();
            // Diagnostics (issue #49) in the same critical section, so a worker can never run a
            // segment of a task its stats do not know: names copied, queued counted. The handle's
            // top 24 bits are the owner instance (tasks_model.cpp's handle layout).
            const auto admitted = c.stats.admit(owner, handle >> 40, owner_name_copy, in.name, handle, m->queued_since);
            m->stats_slot = admitted.owner_slot;
            m->stats_entry = admitted.entry;
            node.key() = handle;
            c.meta.insert(std::move(node));  // reserved: no allocation, no rehash
            if (c.hook) c.hook(Point::SubmitPublished);
        }
    }
    if (r != KILN_OK && submit_why == Refusal::QueueFull) c.stats.note_queue_full(owner);
    if (r != KILN_OK) {
        if (task) *task = 0;
        return r;  // refs released by its destructor
    }
    if (task) *task = handle;
    c.stats.note_queue_depth(c.model.counts().ready);  // the queue's depth at admission (its window max samples it)
    c.wake_all();
    return KILN_OK;
}

KilnResult Pool::cancel(KilnOwner owner, KilnHandle task, State* state) {
    const KilnResult r = core_->model.cancel(owner, task, state);
    core_->wake_all();  // a queued cancellation produced a done delivery
    return r;
}

KilnResult Pool::status(KilnOwner owner, KilnHandle task, State* state) const {
    return core_->model.status(owner, task, state);
}

KilnResult Pool::release(KilnOwner owner, KilnHandle task) { return core_->model.release(owner, task); }

KilnResult Pool::arm(KilnHandle task, uint64_t* registration) {
    const KilnResult r = core_->model.arm(task, registration);
    if (r == KILN_OK) t_segment_armed = true;  // read by deliver_body when this body returns (same thread)
    return r;
}

KilnResult Pool::resume(KilnHandle task, uint64_t registration) {
    const KilnResult r = core_->model.resume(task, registration);
    if (r == KILN_OK)
        if (const std::shared_ptr<Meta> m = core_->find_meta(task))
            core_->stats.continuation_queued(m->stats_slot, m->stats_entry);
    core_->wake_all();
    return r;
}

void Pool::edge(uint64_t session, uint64_t edge) {
    core_->model.edge(session, edge);
    core_->wake_all();
}

void Pool::end_session(uint64_t session) {
    core_->model.end_session(session);
    core_->wake_all();  // the cancelled tasks' done deliveries
}

KilnResult Pool::quarantine(KilnOwner owner) {
    const KilnResult r = core_->model.quarantine(owner);
    core_->wake_all();
    return r;
}

KilnResult Pool::drain_owner(KilnOwner owner, uint64_t timeout_ns, uint32_t* outstanding) {
    if (outstanding) *outstanding = 0;
    if (is_pool_worker()) return KILN_ERR_WRONG_THREAD;  // any timeout: a body or done never drains (no self-drain)
    Core& c = *core_;
    uint32_t left = 0;
    const KilnResult sealed = c.model.seal(owner, &left);
    c.wake_all();
    if (sealed != KILN_OK && sealed != KILN_ERR_REVOKED) {
        if (outstanding) *outstanding = left;
        return sealed;
    }
    if (timeout_ns > 0) {
        // a wait budget in real time (the platform clock is the watchdog's and may be a test's fake one); none once
        // the process exit parked the pool: no done runs any more, so what is left now stays left
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::nanoseconds(timeout_ns);
        while (left > 0 && !c.parking.load() && std::chrono::steady_clock::now() < deadline) {
            std::this_thread::sleep_for(std::chrono::microseconds(100));
            c.model.outstanding(owner, &left);
        }
    }
    if (outstanding) *outstanding = left;
    if (left > 0) c.stats.note_unload_blocked(owner);  // an unload this owner held up (issue #49)
    else c.stats.close_owner(owner);
    return KILN_OK;
}

KilnResult Pool::retire_owner(KilnOwner owner, uint32_t* outstanding) {
    const KilnResult r = core_->model.retire(owner, outstanding);
    core_->stats.close_owner(owner);
    core_->wake_all();
    return r;
}

tasks_stats::Store& Pool::stats_store() { return core_->stats; }
const tasks_stats::Store& Pool::stats_store() const { return core_->stats; }

tasks_stats::WindowSnapshot Pool::collect_tasks_window(uint64_t backlog) {
    Core& c = *core_;
    c.stats.note_queue_depth(c.model.counts().ready);
    return c.stats.collect_window(c.platform.now_ns(), backlog, c.model.counts().ready);
}

void Pool::run_watchdog(WatchFn fn, void* user) { core_->sweep_watchdog(fn, user); }

void Pool::Core::sweep_watchdog(WatchFn fn, void* user) {
    std::vector<Watch> reports;
    {
        std::lock_guard lock(mutex);
        const uint64_t now = platform.now_ns();
        for (const auto& [task, m] : meta) {
            // by handle, not the owner-scoped public status: a body that outlives its retired owner is still named
            const State s = model.diag_state(task);  // pool -> model lock order; the model never calls back
            if (s == State::Running && m->running && !m->reported_running && now - m->running_since >= config.running_watchdog_ns) {
                m->reported_running = true;
                stats.note_too_long(m->stats_slot, true);
                reports.push_back({Watch::Kind::RunningTooLong, m->owner, task, m->name, now - m->running_since, {}});
            } else if (s == State::Queued && !m->reported_queued && now - m->queued_since >= config.queued_watchdog_ns) {
                m->reported_queued = true;
                stats.note_too_long(m->stats_slot, false);
                reports.push_back({Watch::Kind::QueuedTooLong, m->owner, task, m->name, now - m->queued_since, {}});
            }
        }
        for (Watch& r : reports) r.owner_name = model.owner_name(r.owner);
    }
    for (const Watch& r : reports) fn(r, user);  // outside every lock
}

Counts Pool::counts() const { return core_->model.counts(); }
std::size_t Pool::tracked_tasks() const {
    std::lock_guard lock(core_->mutex);
    return core_->meta.size();
}
uint32_t Pool::items_in_hand() const {
    std::lock_guard lock(core_->mutex);
    return core_->in_item;
}
bool Pool::is_pool_worker() const { return t_core == core_.get(); }
uint32_t Pool::normal_workers() const { return core_->normal_workers; }
bool Pool::can_run(Slot slot) const {
    std::lock_guard lock(core_->mutex);
    return core_->can_run_locked(slot);
}
void Pool::Core::publish_health_locked() {
    health.store(can_run_locked(Slot::Normal) ? 1 : 0, std::memory_order_release);
}
bool Pool::Core::can_run_locked(Slot slot) const {
    // a LongRunning task's done runs on a normal worker
    return maintenance_started && healthy[0] > 0 && (slot == Slot::Normal || healthy[1] > 0);
}
uint64_t Pool::now_ns() const { return core_->platform.now_ns(); }
uint64_t Pool::current_thread_id() const { return core_->platform.current_thread_id(); }
void Pool::guarded(GuardedFn fn, void* context, FaultInfo* info) { core_->platform.guarded_call(fn, context, info); }
int Pool::health() const { return core_->health.load(std::memory_order_acquire); }
uint64_t Pool::start_failures() const {
    std::lock_guard lock(core_->mutex);
    return core_->start_failures;
}
uint32_t Pool::long_running_workers() const { return core_->long_running_workers; }
void Pool::set_test_hook(std::function<void(Point)> hook) {
    std::lock_guard lock(core_->mutex);
    core_->hook = std::move(hook);
}
int Pool::live_cores() { return g_live_cores.load(); }
void Pool::set_note_sink(NoteFn fn, void* user) {
    std::lock_guard lock(core_->mutex);
    core_->note_fn = fn;
    core_->note_user = user;
}
void Pool::set_watch_sink(WatchFn fn, void* user) {
    {
        std::lock_guard lock(core_->mutex);
        core_->watch_fn = fn;
        core_->watch_user = user;
        core_->maintenance_kick = true;
    }
    core_->maintenance_cv.notify_all();  // start (or stop) the periodic sweep now
}
void Pool::set_model_test_hook(std::function<void(Model::Point)> hook) { core_->model.set_test_hook(std::move(hook)); }

}  // namespace kiln::tasks

// ---- kiln.tasks v1 binding (Service) ----
namespace kiln::tasks {

void Pool::close() {
    core_->model.seal_all();
    core_->wake_all();  // the cancelled tasks' dones
}

Pool::ParkResult Pool::park(uint64_t timeout_ns) {
    Core& c = *core_;
    ParkResult r;
    const auto t0 = std::chrono::steady_clock::now();
    // a body that ends the process is one of the busy workers: never wait for the caller itself
    const uint32_t own = t_core == &c ? 1 : 0;
    std::unique_lock lock(c.mutex);
    c.parking.store(true);
    c.work_cv.notify_all();
    c.maintenance_cv.notify_all();
    // the wait is for every worker past the line (it may still take an item); the report counts items in hand
    r.running_at_start = c.in_item - own;
    c.exit_cv.wait_for(lock, std::chrono::nanoseconds(timeout_ns),
                       [&] { return c.busy == own && !c.maintenance_busy; });
    r.still_running = c.in_item - own;
    r.waited_ns = static_cast<uint64_t>(
        std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - t0).count());
    if (r.still_running) {
        // the bodies still in flight, by name (dones are not marked running; they only count)
        std::size_t named = 0;
        for (const auto& [task, m] : c.meta) {
            if (!m->running || (own && task == t_running_task)) continue;
            if (named == 8) {
                r.names += ", ...";
                break;
            }
            r.names += (named++ ? ", " : "") + c.model.owner_name(m->owner) + "/" + m->name;  // pool -> model order
        }
    }
    return r;
}

bool Pool::parked() const { return core_->parking.load(); }

namespace {
KilnResult refuse(KilnHandle* task, uint32_t* refusal, KilnResult result, Refusal why) {
    if (task) *task = 0;
    if (refusal) *refusal = static_cast<uint32_t>(why);
    return result;
}
}  // namespace

Service::Service(Pool& pool, ServiceEnv& env) : pool_(pool), env_(env) {}

void Service::set_test_hook(std::function<void(Point)> hook) { hook_ = std::move(hook); }

// The owner's model instance, opened at its first use. A revoke between the caller's alive check and this point is
// caught by the re-check: the instance is retired again (the registry's observer may have run before it existed).
KilnResult Service::open(KilnOwner owner) {
    const KilnResult r = pool_.ensure_owner(owner, env_.owner_name(owner));
    if (r != KILN_OK) return r;
    if (!env_.owner_alive(owner)) {
        pool_.retire_owner(owner, nullptr);
        return KILN_ERR_REVOKED;
    }
    return KILN_OK;
}

KilnResult Service::submit(KilnOwner owner, const KilnTaskInfo* info, KilnTaskBodyFn body, KilnTaskDoneFn done,
                           void* user, KilnHandle* task, uint32_t* refusal) {
    return submit_as(owner, info, body, done, user, false, task, refusal);
}

KilnResult Service::submit_as(KilnOwner owner, const KilnTaskInfo* info, KilnTaskBodyFn body, KilnTaskDoneFn done,
                              void* user, bool lease, KilnHandle* task, uint32_t* refusal) {
    // every required pointer and field before any side effect
    if (!owner || !info || !body || !done || !task || info->size < sizeof(KilnTaskInfo) ||
        (info->flags & ~uint32_t{KILN_TASK_LONG_RUNNING | KILN_TASK_AFTER_STEP}) || info->priority > 2 ||
        info->reserved != 0 || (info->name.len && !info->name.ptr) ||
        !valid_name(std::string_view(info->name.ptr ? info->name.ptr : "", info->name.len)))
        return refuse(task, refusal, KILN_ERR_INVALID_ARGUMENT, Refusal::Argument);
    if (exiting_.load(std::memory_order_acquire)) return refuse(task, refusal, KILN_ERR_UNAVAILABLE, Refusal::Shutdown);
    if (!env_.owner_alive(owner)) return refuse(task, refusal, KILN_ERR_REVOKED, Refusal::OwnerSealed);
    if (hook_) hook_(Point::AfterAliveCheck);
    SubmitArgs args;
    args.name = std::string_view(info->name.ptr, info->name.len);
    args.priority = static_cast<Priority>(info->priority);
    args.long_running = (info->flags & KILN_TASK_LONG_RUNNING) != 0;
    args.lease = lease;
    if (info->flags & KILN_TASK_AFTER_STEP) {
        const Snapshot snap = snapshot();  // one consistent (session, edge, live)
        if (!snap.live) return refuse(task, refusal, KILN_ERR_UNAVAILABLE, Refusal::NoServerWorld);
        args.after_step = true;
        args.session = snap.session;
        args.edge = snap.edge;
        if (hook_) hook_(Point::AfterCapture);
    }
    if (const KilnResult r = open(owner); r != KILN_OK)
        return refuse(task, refusal, r,
                      r == KILN_ERR_REVOKED ? Refusal::OwnerSealed
                      : r == KILN_ERR_UNAVAILABLE && exiting_.load(std::memory_order_acquire) ? Refusal::Shutdown
                                                                                                : Refusal::Capability);
    Refusal why = Refusal::None;
    const KilnResult r = pool_.submit(owner, args, body, done, user, task, &why);
    if (r != KILN_OK && refusal) *refusal = static_cast<uint32_t>(why);
    return r;
}

KilnResult Service::cancel(KilnOwner owner, KilnHandle task, uint32_t* state) {
    if (state) *state = KILN_TASK_NONE;
    if (!owner) return KILN_ERR_INVALID_ARGUMENT;
    State s = State::None;
    KilnResult r = pool_.cancel(owner, task, &s);
    if (r == KILN_ERR_REVOKED && env_.owner_alive(owner)) r = KILN_ERR_NOT_FOUND;  // never opened: nothing of it
    if (state) *state = static_cast<uint32_t>(s);
    return r;
}

KilnResult Service::status(KilnOwner owner, KilnHandle task, uint32_t* state) {
    if (!owner || !state) return KILN_ERR_INVALID_ARGUMENT;
    State s = State::None;
    KilnResult r = pool_.status(owner, task, &s);
    if (r == KILN_ERR_REVOKED && env_.owner_alive(owner)) r = KILN_ERR_NOT_FOUND;
    *state = static_cast<uint32_t>(s);
    return r;
}

KilnResult Service::release(KilnOwner owner, KilnHandle task) {
    if (!owner) return KILN_ERR_INVALID_ARGUMENT;
    const KilnResult r = pool_.release(owner, task);
    return r == KILN_ERR_REVOKED && env_.owner_alive(owner) ? KILN_ERR_NOT_FOUND : r;
}

KilnResult Service::drain_owner(KilnOwner owner, uint32_t timeout_ms, uint32_t* running) {
    if (running) *running = 0;
    if (!owner) return KILN_ERR_INVALID_ARGUMENT;
    if (pool_.is_pool_worker()) return KILN_ERR_WRONG_THREAD;  // any timeout: no self-drain
    if (timeout_ms > 0 && env_.on_tick_or_world_thread()) return KILN_ERR_WRONG_THREAD;
    const bool alive = env_.owner_alive(owner);
    if (alive) {
        // seal even an owner that never submitted: drain seals for good
        if (const KilnResult r = open(owner); r != KILN_OK && r != KILN_ERR_REVOKED) return r;
    }
    uint32_t left = 0;
    const KilnResult r = pool_.drain_owner(owner, uint64_t{timeout_ms} * 1000 * 1000, &left);
    if (running) *running = left;
    if (r != KILN_OK) return r;
    return alive || left == 0 ? KILN_OK : KILN_ERR_REVOKED;
}

Service::Snapshot Service::snapshot() const {
    std::lock_guard lock(mutex_);
    return snap_;
}

uint64_t Service::refused_edges() const {
    std::lock_guard lock(mutex_);
    return refused_edges_;
}

void Service::session_started(uint32_t session) {
    {
        std::lock_guard lock(mutex_);
        snap_ = {session, 0, true};
    }
    // A world that stopped without a destroy event left its hops queued: taken in the same step that opens the new
    // session, and dispatched cancelled (as at a STOPPING), never carried into the new world.
    std::vector<Hop> old;
    {
        std::lock_guard lock(lease_mutex_);
        for (auto& [owner, q] : hops_)
            for (const Hop& h : q) old.push_back(h);
        hops_.clear();
        hop_turn_ = 0;
        hops_open_ = true;
    }
    const KilnHostDispatchFn dispatch = dispatcher();
    for (const Hop& h : old) {
        if (dispatch) dispatch_hop(dispatch, h.lease, h.host_state, 1);
        else pool_.hop_end(h.lease);
    }
}

bool Service::post_edge(uint32_t session, uint64_t step) {
    {
        std::lock_guard lock(mutex_);
        if (!snap_.live || session != snap_.session || step == 0 || step <= snap_.edge) {
            ++refused_edges_;
            return false;
        }
        snap_.edge = step;
    }
    pool_.edge(session, step);
    return true;
}

void Service::session_stopping(uint32_t session) {
    {
        std::lock_guard lock(mutex_);
        if (session == snap_.session) snap_.live = false;
    }
    pool_.end_session(session);
    dispatch_hops_cancelled();  // on the server world's thread, before its STOPPING is dispatched
}

void Service::owner_retiring(KilnOwner owner) {
    if (owner) pool_.retire_owner(owner, nullptr);  // model only: never waits, never calls out
}

void KILN_CALL Service::owner_retiring_observer(KilnOwner retiring, void* service) {
    try {
        static_cast<Service*>(service)->owner_retiring(retiring);
    } catch (...) {  // nothing leaves an observer (the registry calls it under a fault guard, not an exception one)
    }
}

void Service::refuse_new() { exiting_.store(true, std::memory_order_release); }

// ---- kiln.internal.tasks.host v1: leases ----

KilnResult Pool::resume_after(KilnHandle task, uint64_t registration, uint64_t session, uint64_t edge) {
    const KilnResult r = core_->model.resume_after(task, registration, session, edge);
    if (r == KILN_OK)
        if (const std::shared_ptr<Meta> m = core_->find_meta(task))
            core_->stats.continuation_queued(m->stats_slot, m->stats_entry);
    core_->wake_all();
    return r;
}
KilnResult Pool::finish(KilnHandle task, Outcome outcome) { return core_->model.finish(task, outcome); }
bool Pool::cancel_requested(KilnHandle task) const { return core_->model.cancel_requested(task); }
bool Pool::lease_of(KilnHandle task, KilnOwner* owner, uintptr_t* user) const { return core_->model.lease_of(task, owner, user); }
KilnResult Pool::hop_begin(KilnHandle task) { return core_->model.hop_begin(task); }
KilnResult Pool::hop_end(KilnHandle task) {
    const KilnResult r = core_->model.hop_end(task);
    core_->wake_all();  // the last hop may have released the lease's done
    return r;
}

KilnResult Pool::snapshot_leases(KilnLeaseSnapshot* rows, uint32_t capacity, uint32_t* count) const {
    if (!count || (capacity && !rows)) return KILN_ERR_INVALID_ARGUMENT;
    Core& c = *core_;
    std::lock_guard lock(c.mutex);
    const uint64_t now = c.platform.now_ns();
    uint32_t n = 0;
    for (const auto& [task, m] : c.meta) {
        if (!m->lease || c.model.diag_state(task) != State::Running) continue;
        if (n < capacity) {
            KilnLeaseSnapshot& row = rows[n];
            row = KilnLeaseSnapshot{};
            row.size = sizeof(KilnLeaseSnapshot);
            row.state = KILN_TASK_RUNNING;
            row.owner = m->owner;
            row.lease = task;
            row.running_ns = m->running ? now - m->running_since : 0;  // 0 while suspended
            const std::size_t len = std::min(m->name.size(), sizeof(row.name) - 1);
            std::memcpy(row.name, m->name.data(), len);
            row.name[len] = 0;
        }
        ++n;
    }
    *count = n;
    return n > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

namespace {
// A lease's core-owned trampolines: the pool runs these (never a managed thunk), the segment one calls the host's one
// dispatcher. The context is the core's, freed by the done trampoline, which every accepted lease gets exactly once
// (except at process exit, where nothing is guaranteed).
struct LeaseCtx {
    Service* service;
    void* host_state;
    KilnOwner owner;
};
void KILN_CALL lease_segment(const KilnTaskRun* run, void* user) {
    const auto* ctx = static_cast<const LeaseCtx*>(user);
    if (const KilnHostDispatchFn dispatch = ctx->service->dispatcher())
        dispatch(run->task, KILN_LEASE_SEGMENT, run->cancelled(run), ctx->host_state);
}
// The lease is quiescent (every segment returned, every hop was dispatched). With an end notification bound the host
// hears of it here, once, also for a lease that never got a segment; then the core drops the record (release is
// legal inside done), so the host never releases one.
void KILN_CALL lease_done(KilnHandle task, uint32_t state, void* user) {
    auto* ctx = static_cast<LeaseCtx*>(user);
    if (const KilnHostEndedFn ended = ctx->service->ended()) {
        Service* const service = ctx->service;
        const KilnOwner owner = ctx->owner;
        void* const host_state = ctx->host_state;
        delete ctx;  // before the call: a fault inside the host's callback leaks nothing of the core's
        ended(task, state, host_state);
        service->lease_ended(owner, task);
        return;
    }
    delete ctx;
}
// The host's state of an outstanding lease, from the core context its record carries (alive until its done ran).
void* lease_host_state(uintptr_t user) { return user ? reinterpret_cast<const LeaseCtx*>(user)->host_state : nullptr; }
}  // namespace

KilnResult Service::host_bind(KilnHostDispatchFn dispatch) {
    if (!dispatch) return KILN_ERR_INVALID_ARGUMENT;
    KilnHostDispatchFn none = nullptr;
    if (dispatch_.compare_exchange_strong(none, dispatch, std::memory_order_acq_rel)) return KILN_OK;
    return none == dispatch ? KILN_OK : KILN_ERR_INVALID_ARGUMENT;  // once per process
}

KilnResult Service::host_admit(KilnOwner owner, const KilnTaskInfo* info, void* host_state, KilnLease* lease,
                               uint32_t* refusal) {
    if (!dispatcher()) {
        if (lease) *lease = 0;
        if (refusal) *refusal = KILN_TASK_REFUSAL_CAPABILITY;
        return KILN_ERR_UNAVAILABLE;
    }
    auto* ctx = new LeaseCtx{this, host_state, owner};
    const KilnResult r = submit_as(owner, info, &lease_segment, &lease_done, ctx, true, lease, refusal);
    if (r != KILN_OK) delete ctx;  // a refused admission transfers nothing
    return r;
}

KilnResult Service::host_arm(KilnLease lease, uint64_t* registration) { return pool_.arm(lease, registration); }
KilnResult Service::host_resume(KilnLease lease, uint64_t registration) { return pool_.resume(lease, registration); }

KilnResult Service::host_resume_after_step(KilnLease lease, uint64_t registration, uint32_t* refusal) {
    const Snapshot snap = snapshot();
    if (!snap.live) {
        if (refusal) *refusal = KILN_TASK_REFUSAL_NO_SERVER_WORLD;
        return KILN_ERR_UNAVAILABLE;
    }
    return pool_.resume_after(lease, registration, snap.session, snap.edge);
}

KilnResult Service::host_bind_ended(KilnHostEndedFn ended) {
    if (!ended) return KILN_ERR_INVALID_ARGUMENT;
    KilnHostEndedFn none = nullptr;
    if (ended_.compare_exchange_strong(none, ended, std::memory_order_acq_rel)) return KILN_OK;
    return none == ended ? KILN_OK : KILN_ERR_INVALID_ARGUMENT;  // once per process
}

void Service::lease_ended(KilnOwner owner, KilnLease lease) {
    pool_.release(owner, lease);  // inside its done: terminal, no hop pending; a retired owner's records are gone already
}

KilnResult Service::host_resume_after_step_in(KilnLease lease, uint64_t registration, uint32_t session,
                                              uint32_t* refusal) {
    const Snapshot snap = snapshot();  // one consistent (session, edge, live) for the compare and the arming
    if (!snap.live || !session || snap.session != session) {
        if (refusal) *refusal = KILN_TASK_REFUSAL_NO_SERVER_WORLD;
        return KILN_ERR_UNAVAILABLE;
    }
    return pool_.resume_after(lease, registration, snap.session, snap.edge);
}

uint32_t Service::host_on_pool_thread() const { return pool_.is_pool_worker() ? 1 : 0; }

uint32_t Service::host_server_session() const {
    const Snapshot snap = snapshot();
    return snap.live ? snap.session : 0;
}

KilnResult Service::host_finish(KilnLease lease, uint32_t outcome) {
    if (outcome > 2) return KILN_ERR_INVALID_ARGUMENT;
    return pool_.finish(lease, static_cast<Outcome>(outcome));  // 0 Completed, 1 Faulted, 2 OwnTokenCancelled
}

uint32_t Service::host_is_cancelled(KilnLease lease) { return pool_.cancel_requested(lease) ? 1 : 0; }

KilnResult Service::host_seal_owner(KilnOwner owner, uint32_t* outstanding) { return drain_owner(owner, 0, outstanding); }

// ---- tick hops ----

KilnResult Service::host_tick_hop(KilnLease lease) {
    {
        std::lock_guard lock(lease_mutex_);
        if (!hops_open_) {
            pool_.stats_store().note_hop_refused();
            return KILN_ERR_UNAVAILABLE;  // a pure client or the menu: no server tick will come
        }
    }
    KilnOwner owner = 0;
    uintptr_t user = 0;
    if (!pool_.lease_of(lease, &owner, &user)) return KILN_ERR_INTERNAL;
    if (const KilnResult r = pool_.hop_begin(lease); r != KILN_OK) return r;  // holds the lease's quiescence
    void* const host_state = lease_host_state(user);
    if (hook_) hook_(Point::HopBeforePush);
    try {
        std::unique_lock lock(lease_mutex_);
        if (!hops_open_) {  // its session's STOPPING came between: refused, never carried into the next world
            lock.unlock();
            pool_.hop_end(lease);
            pool_.stats_store().note_hop_refused();
            return KILN_ERR_UNAVAILABLE;
        }
        auto it = std::find_if(hops_.begin(), hops_.end(), [&](const auto& q) { return q.first == owner; });
        if (it == hops_.end()) {
            hops_.emplace_back(owner, std::deque<Hop>{});
            it = std::prev(hops_.end());
        }
        it->second.push_back({lease, host_state});
    } catch (...) {
        pool_.hop_end(lease);  // never queued: nothing holds the lease
        throw;
    }
    return KILN_OK;
}

std::size_t Service::pending_tick_hops() const {
    std::lock_guard lock(lease_mutex_);
    std::size_t n = 0;
    for (const auto& [owner, q] : hops_) n += q.size();
    return n;
}

std::size_t Service::run_tick_hops() {
    const KilnHostDispatchFn dispatch = dispatcher();
    if (!dispatch) return 0;
    const uint64_t step_start = pool_.now_ns();
    std::size_t ran = 0, owners = 0;
    {
        std::lock_guard lock(lease_mutex_);
        owners = hops_.size();
    }
    // one turn per owner a step, starting where the last step stopped; each turn FIFO, bounded, checked between hops
    bool owner_stop = false, step_stop = false;
    uint64_t served = 0, cancelled_ran = 0;
    for (std::size_t turn = 0; turn < owners; ++turn) {
        if (pool_.now_ns() - step_start >= kHopStepNs) {
            step_stop = true;  // counted below only if hops were left behind
            break;
        }
        KilnOwner owner = 0;
        {
            std::lock_guard lock(lease_mutex_);
            if (hops_.empty()) break;
            hop_turn_ %= hops_.size();
            owner = hops_[hop_turn_].first;
            hop_turn_ = (hop_turn_ + 1) % hops_.size();
        }
        const uint64_t owner_start = pool_.now_ns();
        std::size_t n = 0;
        bool emptied = false, step_over = false;
        for (; n < kHopsPerOwner; ++n) {
            const uint64_t now = pool_.now_ns();
            step_over = now - step_start >= kHopStepNs;
            if (step_over || now - owner_start >= kHopOwnerNs) break;
            Hop hop{};
            {
                std::lock_guard lock(lease_mutex_);
                const auto it = std::find_if(hops_.begin(), hops_.end(), [&](const auto& q) { return q.first == owner; });
                if (it == hops_.end() || it->second.empty()) {
                    emptied = true;
                    break;
                }
                hop = it->second.front();
                it->second.pop_front();
            }
            const uint32_t cancelled = pool_.cancel_requested(hop.lease) ? 1 : 0;
            dispatch_hop(dispatch, hop.lease, hop.host_state, cancelled, false);
            if (cancelled) ++cancelled_ran;
            ++ran;
        }
        if (n > 0) ++served;
        if (!emptied) {
            // A budget (its 64 hops, its 2 ms, or the step's 4 ms) ended this owner's turn: a stop is
            // counted only when hops of its own were left behind.
            std::lock_guard lock(lease_mutex_);
            const auto it = std::find_if(hops_.begin(), hops_.end(), [&](const auto& q) { return q.first == owner; });
            if (it != hops_.end() && !it->second.empty()) (step_over ? step_stop : owner_stop) = true;
        }
    }
    uint64_t pending = 0;
    {
        std::lock_guard lock(lease_mutex_);  // owners without pending hops leave the rotation
        const KilnOwner next = hops_.empty() ? 0 : hops_[hop_turn_ % hops_.size()].first;
        hops_.erase(std::remove_if(hops_.begin(), hops_.end(), [](const auto& q) { return q.second.empty(); }), hops_.end());
        hop_turn_ = 0;
        for (std::size_t i = 0; i < hops_.size(); ++i) {
            if (hops_[i].first == next) hop_turn_ = i;
            pending += hops_[i].second.size();  // the same pass: no second walk over the owners
        }
    }
    step_stop = step_stop && pending > 0;
    // The window's apply account (issue #49): this round's dispatches, its whole duration, the
    // owners it served, and the hops a budget left behind (counted once per round they wait out).
    pool_.stats_store().record_apply(ran - cancelled_ran, cancelled_ran, pool_.now_ns() - step_start, served, owner_stop,
                                     step_stop, (owner_stop || step_stop) ? pending : 0);
    return ran;
}

void Service::dispatch_hops_cancelled() {
    const KilnHostDispatchFn dispatch = dispatcher();
    std::vector<Hop> all;
    {
        std::lock_guard lock(lease_mutex_);  // closed and taken in one step: nothing queued after this runs later
        hops_open_ = false;
        for (auto& [owner, q] : hops_)
            for (const Hop& h : q) all.push_back(h);
        hops_.clear();
        hop_turn_ = 0;
    }
    for (const Hop& h : all) {  // the session ends: every pending hop completes cancelled, none carries over
        if (dispatch) dispatch_hop(dispatch, h.lease, h.host_state, 1);
        else pool_.hop_end(h.lease);
    }
}

namespace {
// A hop's call into the host's dispatcher, inside the platform's fault boundary (no destructor across it).
struct HopCall {
    GuardedHeader header;  // header.callback = the dispatcher
    KilnHostDispatchFn dispatch;
    KilnLease lease;
    uint32_t cancelled;
    void* host_state;
};
void invoke_hop(void* context) {
    auto* call = static_cast<HopCall*>(context);
    call->dispatch(call->lease, KILN_LEASE_TICK_HOP, call->cancelled, call->host_state);
}
}  // namespace

// One hop, contained: a structured fault in the host's dispatcher ends that hop all the same, cancels its lease and
// quarantines its owner (as a faulting segment would), is reported once, and the next hop runs.
void Service::dispatch_hop(KilnHostDispatchFn dispatch, KilnLease lease, void* host_state, uint32_t cancelled,
                           bool count_apply) {
    HopCall call{{reinterpret_cast<uintptr_t>(dispatch)}, dispatch, lease, cancelled, host_state};
    // Diagnostics (issue #49): a hop runs on the tick (or, at a world's end, on the thread ending
    // it): its stats entry says on-tick while it runs, suspended again after.
    tasks_stats::Store& stats = pool_.stats_store();
    const int entry = stats.entry_of_handle(lease);
    const uint64_t wall0 = pool_.now_ns();
    stats.set_on_tick(entry, pool_.current_thread_id(), wall0);
    FaultInfo fault{};
    pool_.guarded(&invoke_hop, &call, &fault);
    stats.end_on_tick(entry);
    if (count_apply)
        stats.record_apply(cancelled ? 0 : 1, cancelled ? 1 : 0, pool_.now_ns() - wall0, 0, false, false, 0);
    if (fault.faulted) {
        KilnOwner owner = 0;
        uintptr_t user = 0;
        if (pool_.lease_of(lease, &owner, &user)) {
            pool_.quarantine(owner);
            State state = State::None;
            pool_.cancel(owner, lease, &state);  // the lease sees its cancellation; a waiting continuation is released
        }
        env_.hop_faulted(owner, lease, fault.code);
    }
    pool_.hop_end(lease);
}

KilnResult Service::host_snapshot(KilnLeaseSnapshot* rows, uint32_t capacity, uint32_t* count) {
    return pool_.snapshot_leases(rows, capacity, count);
}

void Service::exiting() {
    exiting_.store(true, std::memory_order_release);
    pool_.close();
}

Pool::ParkResult Service::park(uint64_t timeout_ns) {
    exiting_.store(true, std::memory_order_release);
    return pool_.park(timeout_ns);
}

}  // namespace kiln::tasks
