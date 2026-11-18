// SPDX-License-Identifier: MIT
// kiln.internal.tasks.host v1 through the binding (src/tasks.cpp Service::host_*): the managed host's leases on the
// one pool. A lease's segments run the core's trampoline, which calls the bound dispatcher; it suspends without a
// worker, resumes (also after a Post edge), ends with finish() inside a segment, counts once against capacity, keeps
// its continuations after a seal, and shows in the bounded snapshot. Built twice by test-tasks.sh (ASan/UBSan, TSan).
#include <atomic>
#include <chrono>
#include <cstring>
#include <functional>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "tasks.h"
#include "tasks_platform_host.h"
#include "tasks_test_support.h"

// The exit test leaves leases that can never end (a parked pool runs nothing): their host state is the process's to
// drop at exit, so LeakSanitizer is told not to count what those admissions allocate.
#if defined(__has_feature)
#if __has_feature(address_sanitizer)
#define KILN_TEST_LSAN 1
#endif
#endif
#if defined(__SANITIZE_ADDRESS__) && !defined(KILN_TEST_LSAN)
#define KILN_TEST_LSAN 1
#endif
#ifdef KILN_TEST_LSAN
#include <sanitizer/lsan_interface.h>
#endif

using namespace kiln::tasks;
using kiln::tasks::test::check;
using kiln::tasks::test::Gate;
using kiln::tasks::test::HostPlatform;

namespace kiln::tasks {
Platform& default_platform() {
    static auto* host = new HostPlatform;  // never used: every pool here gets an explicit platform
    return *host;
}
}  // namespace kiln::tasks

namespace {

class Env : public ServiceEnv {
public:
    bool on_tick_or_world_thread() override { return false; }
    bool owner_alive(KilnOwner owner) override { return owner != 0 && owner != 99; }
    std::string owner_name(KilnOwner owner) override { return "mod." + std::to_string(owner); }
};

// The host's dispatcher: one per process in production; here the test's script for each segment.
struct Host {
    Service* service = nullptr;
    std::mutex mutex;
    std::function<void(KilnLease, uint32_t cancelled)> segment;  // what the next segment does
    std::atomic<int> segments{0};
    std::atomic<uint32_t> last_cancelled{9};
};
Host g_host;

void KILN_CALL dispatch(KilnLease lease, uint32_t what, uint32_t cancelled, void* host_state) {
    check(what == KILN_LEASE_SEGMENT && host_state == &g_host, "the dispatcher gets a segment with the host's state");
    g_host.last_cancelled = cancelled;
    std::function<void(KilnLease, uint32_t)> f;
    {
        std::lock_guard lock(g_host.mutex);
        f = g_host.segment;
    }
    g_host.segments.fetch_add(1);
    if (f) f(lease, cancelled);
}
void KILN_CALL other_dispatch(KilnLease, uint32_t, uint32_t, void*) {}

void on_segment(std::function<void(KilnLease, uint32_t)> f) {
    std::lock_guard lock(g_host.mutex);
    g_host.segment = std::move(f);
}

template <class F>
bool until(F pred, int ms = 5000) {
    for (int i = 0; i < ms * 10 && !pred(); ++i) std::this_thread::sleep_for(std::chrono::microseconds(100));
    return pred();
}

KilnTaskInfo info(const char* name, uint32_t flags = 0) {
    KilnTaskInfo i{};
    i.size = sizeof(KilnTaskInfo);
    i.flags = flags;
    i.name = {name, std::strlen(name)};
    return i;
}

PoolConfig config(uint32_t workers = 2, uint32_t capacity = 64) {
    PoolConfig c;
    c.normal_workers = workers;
    c.long_running_workers = 1;
    c.limits.owner_capacity = capacity;
    return c;
}

uint32_t state_of(Service& s, KilnOwner owner, KilnHandle h) {
    uint32_t st = 99;
    s.status(owner, h, &st);
    return st;
}

// A lease's terminal state is published before its done trampoline runs, and that trampoline reads the Service
// (src/tasks.cpp lease_done). Declared right after a test's Service, this waits on the way out, before the Service
// goes, until every accepted task's done has returned, so no worker still reads a Service the test destroyed.
struct Settled {
    Pool& pool;
    ~Settled() {
        check(until([&] { return pool.counts().outstanding == 0; }), "every done returned before the test's Service went");
    }
};

KILN_TASKS_TEST(bind_and_admit) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const Settled settled{pool};
    const KilnTaskInfo i = info("lease");
    KilnLease l = 5;
    uint32_t why = 0;
    check(s.host_admit(1, &i, &g_host, &l, &why) == KILN_ERR_UNAVAILABLE && why == KILN_TASK_REFUSAL_CAPABILITY && l == 0,
          "no dispatcher bound: admit refuses UNAVAILABLE + CAPABILITY");
    check(s.host_bind(nullptr) == KILN_ERR_INVALID_ARGUMENT, "bind needs a dispatcher");
    check(s.host_bind(&dispatch) == KILN_OK && s.host_bind(&dispatch) == KILN_OK, "bind, and the same one again");
    check(s.host_bind(&other_dispatch) == KILN_ERR_INVALID_ARGUMENT, "a different dispatcher is refused");
    KilnTaskInfo bad = i;
    bad.flags = 1u << 5;
    check(s.host_admit(1, &bad, &g_host, &l, &why) == KILN_ERR_INVALID_ARGUMENT && why == KILN_TASK_REFUSAL_ARGUMENT,
          "admit validates like submit");
    check(s.host_admit(99, &i, &g_host, &l, &why) == KILN_ERR_REVOKED && why == KILN_TASK_REFUSAL_OWNER_SEALED,
          "admit refuses a dead owner like submit");
}

// A lease suspends without holding a worker, resumes, and ends with finish() inside its last segment.
KILN_TASKS_TEST(lease_lifecycle) {
    HostPlatform p;
    Env env;
    Pool pool(config(1), &p);  // one normal worker: a suspended lease must not hold it
    Service s(pool, env);
    const Settled settled{pool};
    s.host_bind(&dispatch);
    std::atomic<uint64_t> reg{0};
    on_segment([&](KilnLease l, uint32_t) {
        uint64_t r = 0;
        check(s.host_arm(l, &r) == KILN_OK && r != 0, "the first segment arms one resumption");
        reg = r;
    });
    const KilnTaskInfo i = info("managed");
    KilnLease l = 0;
    check(s.host_admit(1, &i, &g_host, &l, nullptr) == KILN_OK && l != 0, "a lease is admitted");
    check(until([&] { return reg.load() != 0 && g_host.segments.load() >= 1; }), "its first segment ran");
    check(state_of(s, 1, l) == KILN_TASK_RUNNING, "suspended, it is RUNNING (its public handle works)");
    // the one worker is free: a native task of another owner runs while the lease is suspended
    struct Native {
        std::atomic<int> done{0};
    } native;
    KilnHandle nh = 0;
    s.submit(2, &i, +[](const KilnTaskRun*, void*) {}, +[](KilnHandle, uint32_t, void* u) { static_cast<Native*>(u)->done++; },
             &native, &nh, nullptr);
    check(until([&] { return native.done.load() == 1; }), "a suspended lease occupies no worker");
    check(s.host_finish(l, 0) == KILN_ERR_INTERNAL, "finish outside a running segment: INTERNAL");
    on_segment([&](KilnLease lease, uint32_t cancelled) {
        check(cancelled == 0, "a normal resumption is not cancelled");
        check(s.host_finish(lease, 0) == KILN_OK, "finish(Completed) inside the last segment");
    });
    check(s.host_resume(l, reg.load()) == KILN_OK, "resume from another thread");
    check(s.host_resume(l, reg.load()) == KILN_ERR_INTERNAL, "a duplicate resume is INTERNAL");
    check(until([&] { return state_of(s, 1, l) == KILN_TASK_COMPLETED; }), "the lease is COMPLETED");
    uint32_t left = 9;
    check(until([&] {
        s.host_seal_owner(1, &left);
        return left == 0;
    }), "seal_owner: quiescent (the core's done trampoline ran)");
    // quiescence is counted before the worker releases the references: wait for the release itself
    check(until([&] { return p.acquires() == p.releases(); }), "the trampolines' module references were released");
}

// Continuations are never refused by capacity or a seal; a sealed lease resumes with its cancellation set.
KILN_TASKS_TEST(lease_after_seal_and_capacity) {
    HostPlatform p;
    Env env;
    Pool pool(config(2, 1), &p);  // capacity 1: the lease is the owner's only record
    Service s(pool, env);
    const Settled settled{pool};
    s.host_bind(&dispatch);
    std::atomic<uint64_t> reg{0};
    on_segment([&](KilnLease l, uint32_t) {
        uint64_t r = 0;
        s.host_arm(l, &r);
        reg = r;
    });
    const KilnTaskInfo i = info("sealed");
    KilnLease l = 0;
    s.host_admit(1, &i, &g_host, &l, nullptr);
    check(until([&] { return reg.load() != 0; }), "suspended");
    KilnLease l2 = 7;
    uint32_t why = 0;
    check(s.host_admit(1, &i, &g_host, &l2, &why) == KILN_ERR_UNAVAILABLE && why == KILN_TASK_REFUSAL_QUEUE_FULL,
          "a lease counts once against its owner's capacity");
    uint32_t left = 0;
    s.host_seal_owner(1, &left);
    check(left == 1 && s.host_is_cancelled(l) == 1, "sealed: the lease is outstanding and its cancellation is set");
    on_segment([&](KilnLease lease, uint32_t cancelled) {
        check(cancelled == 1, "the continuation is dispatched with cancelled = 1");
        s.host_finish(lease, 2);  // its own token's cancellation
    });
    check(s.host_resume(l, reg.load()) == KILN_OK, "a continuation is accepted after the seal (and at capacity)");
    check(until([&] {
        s.host_seal_owner(1, &left);
        return left == 0;
    }), "it finished; quiescent");
}

KILN_TASKS_TEST(lease_resume_after_step) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const Settled settled{pool};
    s.host_bind(&dispatch);
    std::atomic<uint64_t> reg{0};
    on_segment([&](KilnLease l, uint32_t) {
        uint64_t r = 0;
        s.host_arm(l, &r);
        reg = r;
    });
    const KilnTaskInfo i = info("next window");
    KilnLease l = 0;
    s.host_admit(1, &i, &g_host, &l, nullptr);
    check(until([&] { return reg.load() != 0; }), "suspended");
    uint32_t why = 0;
    check(s.host_resume_after_step(l, reg.load(), &why) == KILN_ERR_UNAVAILABLE && why == KILN_TASK_REFUSAL_NO_SERVER_WORLD,
          "no live server world: UNAVAILABLE + NO_SERVER_WORLD");
    s.session_started(4);
    s.post_edge(4, 40);
    const int before = g_host.segments.load();
    on_segment([&](KilnLease lease, uint32_t cancelled) {
        check(cancelled == 0, "woken by its session's edge: not cancelled");
        s.host_finish(lease, 0);
    });
    check(s.host_resume_after_step(l, reg.load(), &why) == KILN_OK, "resume after the next step");
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
    check(g_host.segments.load() == before, "nothing runs before the next edge");
    s.post_edge(4, 41);
    check(until([&] { return state_of(s, 1, l) == KILN_TASK_COMPLETED; }), "the next edge resumed it; it finished");
}

KILN_TASKS_TEST(lease_without_an_end_faults) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const Settled settled{pool};
    s.host_bind(&dispatch);
    on_segment(nullptr);  // the host's segment returns without arming or finishing (a host bug)
    const KilnTaskInfo i = info("forgets");
    KilnLease l = 0;
    s.host_admit(1, &i, &g_host, &l, nullptr);
    check(until([&] { return state_of(s, 1, l) == KILN_TASK_FAULTED; }), "a segment that neither arms nor finishes: FAULTED");
}

KILN_TASKS_TEST(lease_snapshot) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const Settled settled{pool};
    s.host_bind(&dispatch);
    std::atomic<uint64_t> reg{0};
    on_segment([&](KilnLease l, uint32_t) {
        uint64_t r = 0;
        s.host_arm(l, &r);
        reg = r;
    });
    const KilnTaskInfo i = info("listed");
    KilnLease l = 0;
    s.host_admit(1, &i, &g_host, &l, nullptr);
    check(until([&] { return reg.load() != 0; }), "suspended");
    uint32_t count = 9;
    check(s.host_snapshot(nullptr, 0, &count) == KILN_ERR_BUFFER_TOO_SMALL && count == 1, "BUFFER_TOO_SMALL says 1 row");
    KilnLeaseSnapshot rows[2]{};
    check(s.host_snapshot(rows, 2, &count) == KILN_OK && count == 1 && rows[0].size == sizeof(KilnLeaseSnapshot) &&
              rows[0].lease == l && rows[0].owner == 1 && rows[0].state == KILN_TASK_RUNNING &&
              std::string(rows[0].name) == "listed" && rows[0].running_ns == 0,
          "one row: the suspended lease, its owner, RUNNING, its name, no segment time while suspended");
    on_segment([&](KilnLease lease, uint32_t) { s.host_finish(lease, 0); });
    s.host_resume(l, reg.load());
    check(until([&] { return state_of(s, 1, l) == KILN_TASK_COMPLETED; }), "finished");
    check(s.host_snapshot(rows, 2, &count) == KILN_OK && count == 0, "a finished lease is not listed");
}


// ---- tick hops ----
struct Hops {
    std::mutex mutex;
    std::vector<std::pair<KilnLease, uint32_t>> seen;  // (lease, cancelled) in dispatch order
    std::vector<uint32_t> what;                        // KILN_LEASE_TICK_HOP / KILN_LEASE_CLIENT_HOP, parallel to seen
    HostPlatform* platform = nullptr;
    uint64_t cost_ns = 0;  // each hop "takes" this long (the fake clock moves)
};
Hops g_hops;
void KILN_CALL hop_dispatch(KilnLease lease, uint32_t what, uint32_t cancelled, void* host_state) {
    if (what == KILN_LEASE_SEGMENT) return dispatch(lease, what, cancelled, host_state);
    std::lock_guard lock(g_hops.mutex);
    g_hops.seen.emplace_back(lease, cancelled);
    g_hops.what.push_back(what);
    if (g_hops.platform) g_hops.platform->advance(g_hops.cost_ns);
}

// A suspended lease of `owner` (its first segment arms and returns).
KilnLease suspended_lease(Service& s, KilnOwner owner, const char* name) {
    std::atomic<uint64_t> reg{0};
    on_segment([&](KilnLease l, uint32_t) {
        uint64_t r = 0;
        s.host_arm(l, &r);
        reg = r;
    });
    const KilnTaskInfo i = info(name);
    KilnLease l = 0;
    s.host_admit(owner, &i, &g_host, &l, nullptr);
    check(until([&] { return reg.load() != 0; }), std::string("lease ") + name + " suspended");
    on_segment(nullptr);
    return l;
}

// Ends a suspended lease (its first registration, 1) so nothing outlives the test.
void end_lease(Service& s, KilnOwner owner, KilnLease l) {
    on_segment([&](KilnLease lease, uint32_t) { s.host_finish(lease, 2); });
    s.host_resume(l, 1);
    check(until([&] {
        uint32_t st = 0;
        s.status(owner, l, &st);
        return st >= KILN_TASK_COMPLETED || st == KILN_TASK_NONE;
    }), "the test's lease ended");
    on_segment(nullptr);
}

std::size_t hops_of(KilnLease l) {
    std::lock_guard lock(g_hops.mutex);
    std::size_t n = 0;
    for (auto& [lease, c] : g_hops.seen) n += lease == l;
    return n;
}

KILN_TASKS_TEST(tick_hops_need_a_server_world) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const Settled settled{pool};
    s.host_bind(&hop_dispatch);
    const KilnLease l = suspended_lease(s, 1, "hops");
    check(s.host_tick_hop(l) == KILN_ERR_UNAVAILABLE, "no live server world: a hop refuses at once (never waits for one)");
    check(s.collect_tasks_window().hops_refused == 1, "the refused hop is counted in the tasks window");
    s.session_started(1);
    check(s.host_tick_hop(l) == KILN_OK && s.pending_tick_hops() == 1, "with one: queued");
    check(s.host_tick_hop(0x12345) == KILN_ERR_INTERNAL, "an unknown lease: INTERNAL");
    s.run_tick_hops();
    end_lease(s, 1, l);
}

KILN_TASKS_TEST(tick_hops_budgets) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const Settled settled{pool};
    s.host_bind(&hop_dispatch);
    {
        std::lock_guard lock(g_hops.mutex);
        g_hops.seen.clear();
        g_hops.platform = &p;
        g_hops.cost_ns = 0;
    }
    s.session_started(1);
    const KilnLease a = suspended_lease(s, 1, "a"), b = suspended_lease(s, 2, "b");
    for (int i = 0; i < 70; ++i) s.host_tick_hop(a);
    for (int i = 0; i < 3; ++i) s.host_tick_hop(b);
    check(s.run_tick_hops() == 67 && hops_of(a) == 64 && hops_of(b) == 3,
          "per owner at most 64 hops a step; the other owner is served in the same step");
    check(s.run_tick_hops() == 6 && hops_of(a) == 70 && s.pending_tick_hops() == 0, "the excess waits for the next step");
    {
        // The window's apply account: 73 applied over two steps; the first step's 64-hop cap stopped
        // owner a with 6 left (an owner-budget stop, those 6 deferred); three owner turns served.
        const auto w = s.collect_tasks_window();
        check(w.apply.applied == 73 && w.apply.cancelled == 0 && w.apply.owners_served == 3 &&
                  w.apply.owner_budget_stops == 1 && w.apply.step_budget_stops == 0 && w.apply.deferred == 6 &&
                  w.apply.backlog == 0,
              "apply account: applied, owners served, the count cap as an owner-budget stop, deferred");
    }
    // time: 1.5 ms a hop: an owner stops after its 2 ms (two hops), all owners after 4 ms; the turn moves on
    {
        std::lock_guard lock(g_hops.mutex);
        g_hops.seen.clear();
        g_hops.cost_ns = 1500 * 1000;
    }
    const KilnLease c = suspended_lease(s, 3, "c");
    for (const KilnLease l : {a, b, c})
        for (int i = 0; i < 5; ++i) s.host_tick_hop(l);
    const std::size_t first = s.run_tick_hops();
    check(first == 3, "4 ms a step in all, checked between callbacks: 3 hops of 1.5 ms (got " + std::to_string(first) + ")");
    check(s.pending_tick_hops() == 12, "the rest wait, none dropped");
    {
        // 1.5 ms hops: the first owner stops at its 2 ms (two hops, an owner stop), the next one's first
        // hop crosses the step's 4 ms (a step stop); 12 left behind, pending at collection too.
        const auto w = s.collect_tasks_window();
        check(w.apply.applied == 3 && w.apply.owners_served == 2 && w.apply.owner_budget_stops == 1 &&
                  w.apply.step_budget_stops == 1 && w.apply.deferred == 12 && w.apply.backlog == 12 &&
                  w.apply.apply_ns >= 4'000'000ull,
              "apply account: the owner and step budget stops, deferred and backlog, the step's time");
    }
    std::size_t steps = 1;
    while (s.pending_tick_hops() && steps < 20) {
        s.run_tick_hops();
        ++steps;
    }
    check(hops_of(a) == 5 && hops_of(b) == 5 && hops_of(c) == 5, "every hop ran, over later steps");
    {
        std::lock_guard lock(g_hops.mutex);
        bool fifo = true;
        // round-robin: no owner gets more than 2 hops (its 2 ms) before another owner with pending hops is served
        int run = 0;
        KilnLease last = 0;
        for (auto& [l, cancelled] : g_hops.seen) {
            run = l == last ? run + 1 : 1;
            last = l;
            fifo = fifo && run <= 2;
        }
        check(fifo, "an owner's turn ends at its 2 ms; owners take turns");
        g_hops.cost_ns = 0;
    }
    end_lease(s, 1, a);
    end_lease(s, 2, b);
    end_lease(s, 3, c);
}

KILN_TASKS_TEST(tick_hops_cancelled_and_quiescence) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const Settled settled{pool};
    s.host_bind(&hop_dispatch);
    {
        std::lock_guard lock(g_hops.mutex);
        g_hops.seen.clear();
        g_hops.platform = nullptr;
    }
    s.session_started(1);
    const KilnLease l = suspended_lease(s, 1, "retiring");
    s.host_tick_hop(l);
    uint32_t left = 0;
    s.host_seal_owner(1, &left);
    s.host_tick_hop(l);  // allowed after the seal
    check(s.run_tick_hops() == 2, "both hops ran");
    end_lease(s, 1, l);  // sealed: its continuation is still accepted
    {
        std::lock_guard lock(g_hops.mutex);
        check(g_hops.seen.size() == 2 && g_hops.seen[0].second == 1 && g_hops.seen[1].second == 1,
              "a retiring lease's hops are dispatched with cancelled = 1");
    }
    // a finished lease is not quiescent while a hop is pending; the STOPPING dispatches it cancelled
    const KilnLease m = suspended_lease(s, 2, "pending");
    s.host_tick_hop(m);
    on_segment([&](KilnLease lease, uint32_t) { s.host_finish(lease, 0); });
    // resume the lease's armed registration (its first, 1)
    check(s.host_resume(m, 1) == KILN_OK, "resumed");
    check(until([&] { return state_of(s, 2, m) == KILN_TASK_COMPLETED; }), "finished");
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
    s.host_seal_owner(2, &left);
    check(left == 1, "finished but not quiescent: a hop is pending");
    s.session_stopping(1);
    check(hops_of(m) == 1 && s.pending_tick_hops() == 0, "the session's STOPPING dispatched the pending hop");
    {
        std::lock_guard lock(g_hops.mutex);
        check(g_hops.seen.back().second == 1, "...with cancelled = 1 (never carried into the next world)");
    }
    check(until([&] {
        s.host_seal_owner(2, &left);
        return left == 0;
    }), "then quiescent");
}

// a fault in the host's dispatcher on a tick hop is contained: that hop still ends, its lease
// is cancelled and its owner quarantined, the fault is reported, the other hops still run; also at STOPPING.
struct FaultEnv : Env {
    std::atomic<int> faults{0};
    std::atomic<KilnLease> lease{0};
    void hop_faulted(KilnOwner, KilnLease l, uint32_t) override {
        lease = l;
        faults++;
    }
};

KILN_TASKS_TEST(tick_hop_fault_contained) {
    for (const bool at_stop : {false, true}) {
        HostPlatform p;
        FaultEnv env;
        Pool pool(config(), &p);
        Service s(pool, env);
        const Settled settled{pool};
        s.host_bind(&hop_dispatch);
        {
            std::lock_guard lock(g_hops.mutex);
            g_hops.seen.clear();
            g_hops.platform = nullptr;
        }
        s.session_started(1);
        const KilnLease a = suspended_lease(s, 1, "faults"), b = suspended_lease(s, 2, "fine");
        s.host_tick_hop(a);
        s.host_tick_hop(b);
        p.fault_next_call_of(reinterpret_cast<uintptr_t>(&hop_dispatch));
        const std::string t = at_stop ? "at STOPPING: " : "on the tick: ";
        bool threw = false;
        try {
            if (at_stop) s.session_stopping(1);
            else s.run_tick_hops();
        } catch (...) {
            threw = true;
        }
        check(!threw, t + "nothing leaves the hop dispatch");
        check(env.faults.load() == 1 && env.lease.load() == a, t + "the fault is reported once, with its lease");
        check(hops_of(b) == 1 && s.pending_tick_hops() == 0, t + "the other owner's hop still ran");
        check(s.host_is_cancelled(a) == 1, t + "the faulting lease is cancelled");
        KilnHandle h = 0;
        uint32_t why = 0;
        const KilnTaskInfo i = info("after");
        check(s.submit(1, &i, +[](const KilnTaskRun*, void*) {}, +[](KilnHandle, uint32_t, void*) {}, nullptr, &h, &why) ==
                      KILN_ERR_UNAVAILABLE &&
                  why == KILN_TASK_REFUSAL_OWNER_QUARANTINED,
              t + "its owner is quarantined");
        end_lease(s, 1, a);
        end_lease(s, 2, b);
        uint32_t left = 9;
        check(until([&] {
            s.host_seal_owner(1, &left);
            return left == 0;
        }), t + "the faulting lease's hop ended: its done arrived, the owner is quiescent");
    }
}

// a hop queued across its session's STOPPING is refused, never carried into the next world.
KILN_TASKS_TEST(tick_hop_across_stopping) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const Settled settled{pool};
    s.host_bind(&hop_dispatch);
    {
        std::lock_guard lock(g_hops.mutex);
        g_hops.seen.clear();
        g_hops.platform = nullptr;
    }
    s.session_started(1);
    const KilnLease l = suspended_lease(s, 1, "late hop");
    s.set_test_hook([&](Service::Point at) {
        if (at == Service::Point::HopBeforePush) s.session_stopping(1);  // the stop lands between the check and the push
    });
    const KilnResult r = s.host_tick_hop(l);
    s.set_test_hook(nullptr);
    check(r == KILN_ERR_UNAVAILABLE && s.pending_tick_hops() == 0, "a hop that met its session's STOPPING is refused");
    s.session_started(2);
    check(s.run_tick_hops() == 0 && hops_of(l) == 0, "nothing of it runs in the next world");
    end_lease(s, 1, l);
    uint32_t left = 9;
    check(until([&] {
        s.host_seal_owner(1, &left);
        return left == 0;
    }), "no hop holds the lease: quiescent");
}

// a server world that stopped without a destroy event (the next one starts a new session) never
// carries its hops over: they are dispatched cancelled when the new session starts, and their leases stay drainable.
KILN_TASKS_TEST(tick_hops_not_carried_without_a_stop) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const Settled settled{pool};
    s.host_bind(&hop_dispatch);
    {
        std::lock_guard lock(g_hops.mutex);
        g_hops.seen.clear();
        g_hops.platform = nullptr;
    }
    s.session_started(1);
    const KilnLease l = suspended_lease(s, 1, "old world");
    check(s.host_tick_hop(l) == KILN_OK && s.pending_tick_hops() == 1, "a hop is queued in session 1");
    s.session_started(2);  // no STOPPING for session 1
    check(s.pending_tick_hops() == 0 && hops_of(l) == 1, "the new session dispatched the old session's hop");
    {
        std::lock_guard lock(g_hops.mutex);
        check(!g_hops.seen.empty() && g_hops.seen.back().second == 1, "...with cancelled = 1");
    }
    check(s.run_tick_hops() == 0 && hops_of(l) == 1, "nothing of it runs again in the new world");
    end_lease(s, 1, l);
    uint32_t left = 9;
    check(until([&] {
        s.host_seal_owner(1, &left);
        return left == 0;
    }), "its hop ended: the lease drains");
}

// ---- the appended members: bind_ended, resume_after_step_in, on_pool_thread, server_session ----
struct Ended {
    Service* service = nullptr;
    std::mutex mutex;
    std::vector<std::pair<KilnLease, uint32_t>> seen;  // (lease, state) in callback order
    std::atomic<int> off_pool{0};                      // callbacks that did not run on a pool worker
    std::function<void(KilnLease)> inside;             // extra work inside the callback
};
Ended g_ended;
void KILN_CALL ended(KilnLease lease, uint32_t state, void* host_state) {
    check(host_state == &g_host, "ended gets the host's state");
    if (g_ended.service && g_ended.service->host_on_pool_thread() != 1) g_ended.off_pool++;
    std::function<void(KilnLease)> f;
    {
        std::lock_guard lock(g_ended.mutex);
        g_ended.seen.emplace_back(lease, state);
        f = g_ended.inside;
    }
    if (f) f(lease);
}
void KILN_CALL other_ended(KilnLease, uint32_t, void*) {}
void reset_ended(Service* s) {
    std::lock_guard lock(g_ended.mutex);
    g_ended.service = s;
    g_ended.seen.clear();
    g_ended.off_pool = 0;
    g_ended.inside = nullptr;
}
int ended_count(KilnLease l, uint32_t* state = nullptr) {
    std::lock_guard lock(g_ended.mutex);
    int n = 0;
    for (auto& [lease, st] : g_ended.seen)
        if (lease == l) {
            ++n;
            if (state) *state = st;
        }
    return n;
}

// The host hears of every accepted lease's end once, on a pool worker, and the core drops the record afterwards.
KILN_TASKS_TEST(ended_notifies_once_and_releases_the_record) {
    HostPlatform p;
    Env env;
    Pool pool(config(2, 2), &p);  // capacity 2: records that stayed would refuse the third lease
    Service s(pool, env);
    const Settled settled{pool};
    reset_ended(&s);
    s.host_bind(&dispatch);
    check(s.host_bind_ended(nullptr) == KILN_ERR_INVALID_ARGUMENT, "bind_ended needs a callback");
    check(s.host_bind_ended(&ended) == KILN_OK && s.host_bind_ended(&ended) == KILN_OK, "bind_ended, and the same one again");
    check(s.host_bind_ended(&other_ended) == KILN_ERR_INVALID_ARGUMENT, "a different one is refused");
    check(s.host_on_pool_thread() == 0, "the test's thread is no pool worker");
    std::atomic<int> on_pool{0};
    const KilnTaskInfo i = info("ends");
    for (int n = 0; n < 5; ++n) {
        const uint32_t outcome = static_cast<uint32_t>(n % 2);  // Completed, Faulted
        on_segment([&, outcome](KilnLease l, uint32_t) {
            on_pool += s.host_on_pool_thread();
            s.host_finish(l, outcome);
        });
        KilnLease l = 0;
        uint32_t why = 0;
        check(s.host_admit(1, &i, &g_host, &l, &why) == KILN_OK, "a lease is admitted (earlier records are gone)");
        uint32_t st = 0;
        check(until([&] { return ended_count(l, &st) == 1; }), "its end is reported");
        check(st == (outcome ? KILN_TASK_FAULTED : KILN_TASK_COMPLETED), "with its final state");
        check(until([&] { return state_of(s, 1, l) == KILN_TASK_NONE; }), "and its record is released by the core");
    }
    check(on_pool.load() == 5, "on_pool_thread is 1 inside a segment");
    check(g_ended.off_pool.load() == 0, "ended runs on a pool worker");
    {
        std::lock_guard lock(g_ended.mutex);
        check(g_ended.seen.size() == 5, "one ended per lease, never two");
    }
    uint32_t left = 9;
    check(until([&] {
        s.host_seal_owner(1, &left);
        return left == 0;
    }), "quiescent");
    reset_ended(nullptr);
}

// A lease cancelled before its first segment never reaches the dispatcher: ended is how the host learns of it,
// whoever cancelled (cancel, seal, the owner retiring).
KILN_TASKS_TEST(ended_reports_a_lease_cancelled_before_its_first_segment) {
    for (int how = 0; how < 3; ++how) {
        HostPlatform p;
        Env env;
        Pool pool(config(1), &p);
        Service s(pool, env);
        const Settled settled{pool};
        reset_ended(&s);
        s.host_bind(&dispatch);
        s.host_bind_ended(&ended);
        // the one worker is held by a native body, so the lease stays queued
        Gate hold;
        std::atomic<int> started{0};
        struct Blocker {
            Gate* hold;
            std::atomic<int>* started;
        } blocker{&hold, &started};
        const KilnTaskInfo normal = info("blocker");
        KilnHandle b1 = 0;
        const auto body = +[](const KilnTaskRun*, void* u) {
            auto* b = static_cast<Blocker*>(u);
            b->started->fetch_add(1);
            b->hold->wait();
        };
        const auto done = +[](KilnHandle, uint32_t, void*) {};
        s.submit(1, &normal, body, done, &blocker, &b1, nullptr);
        check(until([&] { return started.load() == 1; }), "the one worker is busy");
        const int before = g_host.segments.load();
        on_segment([&](KilnLease l, uint32_t) { s.host_finish(l, 0); });
        const KilnTaskInfo i = info("never starts");
        KilnLease l = 0;
        check(s.host_admit(1, &i, &g_host, &l, nullptr) == KILN_OK && state_of(s, 1, l) == KILN_TASK_QUEUED, "queued");
        const std::string t = how == 0 ? "cancel: " : how == 1 ? "seal: " : "retire: ";
        if (how == 0) {
            uint32_t st = 0;
            check(s.cancel(1, l, &st) == KILN_OK && st == KILN_TASK_CANCELLED, t + "CANCELLED at once");
        } else if (how == 1) {
            uint32_t left = 0;
            s.host_seal_owner(1, &left);
        } else {
            s.owner_retiring(1);
        }
        check(ended_count(l) == 0, t + "no ended while the worker is busy (never inline on the cancelling thread)");
        hold.open();
        uint32_t st = 0;
        check(until([&] { return ended_count(l, &st) == 1; }), t + "ended arrives");
        check(st == KILN_TASK_CANCELLED, t + "as CANCELLED");
        check(g_host.segments.load() == before, t + "and no segment was ever dispatched");
        check(g_ended.off_pool.load() == 0, t + "on a pool worker");
        reset_ended(nullptr);
    }
}

// ended is the lease's quiescence: it waits for a pending tick hop, as the done it rides on does.
KILN_TASKS_TEST(ended_waits_for_pending_tick_hops) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const Settled settled{pool};
    reset_ended(&s);
    s.host_bind(&hop_dispatch);
    s.host_bind_ended(&ended);
    {
        std::lock_guard lock(g_hops.mutex);
        g_hops.seen.clear();
        g_hops.platform = nullptr;
    }
    s.session_started(1);
    const KilnLease l = suspended_lease(s, 1, "hop then end");
    check(s.host_tick_hop(l) == KILN_OK, "a hop is queued");
    on_segment([&](KilnLease lease, uint32_t) { s.host_finish(lease, 0); });
    s.host_resume(l, 1);
    check(until([&] { return state_of(s, 1, l) == KILN_TASK_COMPLETED; }), "the lease finished with its hop pending");
    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    check(ended_count(l) == 0, "no ended while a hop is pending");
    check(s.run_tick_hops() == 1 && hops_of(l) == 1, "the hop runs on the tick");
    uint32_t st = 0;
    check(until([&] { return ended_count(l, &st) == 1; }) && st == KILN_TASK_COMPLETED, "then ended, once");
    check(until([&] { return state_of(s, 1, l) == KILN_TASK_NONE; }), "and the record is gone");
    on_segment(nullptr);
    reset_ended(nullptr);
}

// Without bind_ended a lease's record stays until release, as before.
KILN_TASKS_TEST(without_bind_ended_the_record_stays) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const Settled settled{pool};
    reset_ended(&s);
    s.host_bind(&dispatch);
    on_segment([&](KilnLease l, uint32_t) { s.host_finish(l, 0); });
    const KilnTaskInfo i = info("kept");
    KilnLease l = 0;
    s.host_admit(1, &i, &g_host, &l, nullptr);
    uint32_t left = 9;
    check(until([&] { return state_of(s, 1, l) == KILN_TASK_COMPLETED; }), "COMPLETED");
    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    check(state_of(s, 1, l) == KILN_TASK_COMPLETED, "its record stays after its done");
    check(until([&] { return s.release(1, l) == KILN_OK; }) && state_of(s, 1, l) == KILN_TASK_NONE,
          "until the host releases it");
    s.host_seal_owner(1, &left);
    reset_ended(nullptr);
}

// resume_after_step_in arms for the session it names or refuses: a window asked for in one world never moves into
// the next, and a refusal leaves the registration armed.
KILN_TASKS_TEST(resume_after_step_in_checks_the_session) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const Settled settled{pool};
    s.host_bind(&dispatch);
    check(s.host_server_session() == 0, "no live server world: server_session is 0");
    const KilnLease l = suspended_lease(s, 1, "window");
    uint32_t why = 0;
    check(s.host_resume_after_step_in(l, 1, 4, &why) == KILN_ERR_UNAVAILABLE && why == KILN_TASK_REFUSAL_NO_SERVER_WORLD,
          "no live server world: UNAVAILABLE + NO_SERVER_WORLD");
    s.session_started(4);
    check(s.host_server_session() == 4, "server_session is the live session");
    s.post_edge(4, 10);
    s.session_stopping(4);
    check(s.host_server_session() == 0, "0 again once it stops");
    s.session_started(5);
    check(s.host_server_session() == 5, "and the next one");
    why = 0;
    check(s.host_resume_after_step_in(l, 1, 4, &why) == KILN_ERR_UNAVAILABLE && why == KILN_TASK_REFUSAL_NO_SERVER_WORLD,
          "asked for session 4 while 5 is live: refused, not moved into the new world");
    check(s.host_resume_after_step_in(l, 1, 0, &why) == KILN_ERR_UNAVAILABLE, "session 0 is never live");
    s.post_edge(5, 1);
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
    const int before = g_host.segments.load();
    check(before == g_host.segments.load() && state_of(s, 1, l) == KILN_TASK_RUNNING, "the refused window woke nothing");
    on_segment([&](KilnLease lease, uint32_t cancelled) {
        check(cancelled == 0, "woken by its own session's edge");
        s.host_finish(lease, 0);
    });
    check(s.host_resume_after_step_in(l, 1, 5, &why) == KILN_OK, "the registration is still armed: session 5 takes it");
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
    check(g_host.segments.load() == before, "nothing runs before the next edge");
    s.post_edge(5, 2);
    check(until([&] { return state_of(s, 1, l) == KILN_TASK_COMPLETED; }), "the next edge of session 5 resumed it");
    on_segment(nullptr);
    // armed in a session, then that session stops: dispatched cancelled, never in the next world
    const KilnLease m = suspended_lease(s, 1, "stopped");
    check(s.host_resume_after_step_in(m, 1, 5, &why) == KILN_OK, "armed for session 5");
    std::atomic<uint32_t> seen{9};
    on_segment([&](KilnLease lease, uint32_t cancelled) {
        seen = cancelled;
        s.host_finish(lease, 2);
    });
    s.session_stopping(5);
    check(until([&] { return seen.load() == 1; }), "its session's STOPPING resumes it with cancelled = 1");
    check(until([&] { return state_of(s, 1, m) == KILN_TASK_CANCELLED; }), "it ends CANCELLED");
    on_segment(nullptr);
}

// The compare and the arming use one snapshot: a session change racing the call either refuses or arms for the
// session that was asked for; a wake never comes from another session's edge.
KILN_TASKS_TEST(resume_after_step_in_racing_a_session_change) {
    for (int round = 0; round < 50; ++round) {
        HostPlatform p;
        Env env;
        Pool pool(config(), &p);
        Service s(pool, env);
        const Settled settled{pool};
        s.host_bind(&dispatch);
        s.session_started(1);
        s.post_edge(1, 1);
        const KilnLease l = suspended_lease(s, 1, "race");
        std::atomic<uint32_t> seen{9};
        on_segment([&](KilnLease lease, uint32_t cancelled) {
            seen = cancelled;
            s.host_finish(lease, cancelled ? 2 : 0);
        });
        kiln::tasks::test::StartLine line(2);
        KilnResult r = KILN_ERR_INTERNAL;
        std::thread asker([&] {
            line.arrive();
            r = s.host_resume_after_step_in(l, 1, 1, nullptr);
        });
        std::thread world([&] {
            line.arrive();
            s.session_stopping(1);
            s.session_started(2);
            s.post_edge(2, 1);
            s.post_edge(2, 2);
        });
        asker.join();
        world.join();
        if (r == KILN_OK) {
            check(until([&] { return seen.load() != 9; }), "armed for session 1: it is resumed");
            check(seen.load() == 1, "by session 1's end (cancelled), never by an edge of session 2");
        } else {
            check(r == KILN_ERR_UNAVAILABLE, "or refused");
            std::this_thread::sleep_for(std::chrono::milliseconds(2));
            check(seen.load() == 9, "a refused window wakes nothing");
            s.host_resume(l, 1);
            check(until([&] { return seen.load() != 9; }), "its registration is still armed");
        }
        uint32_t left = 9;
        check(until([&] {
            s.host_seal_owner(1, &left);
            return left == 0;
        }), "quiescent");
        on_segment(nullptr);
    }
}

// ---- process exit ----
// The exit parks the pool with a suspended managed task and a pending tick hop: neither holds a worker, so the park
// returns at once; a continuation resumed after the park is queued and never runs; the hop stays the tick's.
KILN_TASKS_TEST(exit_park_with_a_suspended_lease_and_a_pending_hop) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    s.host_bind(&hop_dispatch);
    {
        std::lock_guard lock(g_hops.mutex);
        g_hops.seen.clear();
        g_hops.platform = nullptr;
    }
    s.session_started(1);
#ifdef KILN_TEST_LSAN
    __lsan_disable();  // these two leases never end: the parked pool runs nothing (the process ends them)
#endif
    const KilnLease suspended = suspended_lease(s, 1, "suspended");
    const KilnLease hopping = suspended_lease(s, 2, "hopping");
#ifdef KILN_TEST_LSAN
    __lsan_enable();
#endif
    // suspended_lease returns once a segment armed, while its worker may still be on the way out of that segment; the
    // claim below is about suspended leases, so wait until no worker holds an item at all (no Settled guard here: the
    // parked pool never runs these leases' dones, and a parked worker reads no Service)
    check(until([&] { return pool.items_in_hand() == 0; }), "both first segments returned: no worker holds an item");
    check(s.host_tick_hop(hopping) == KILN_OK && s.pending_tick_hops() == 1, "a tick hop is pending");
    const int segments = g_host.segments.load();
    s.exiting();
    const auto t0 = std::chrono::steady_clock::now();
    const Pool::ParkResult r = s.park(2ull * 1000 * 1000 * 1000);
    check(r.running_at_start == 0 && r.still_running == 0 && std::chrono::steady_clock::now() - t0 < std::chrono::seconds(1),
          "nothing held a worker: the park returned at once");
    const KilnTaskInfo i = info("late");
    KilnLease late = 0;
    uint32_t why = 0;
    check(s.host_admit(3, &i, &g_host, &late, &why) == KILN_ERR_UNAVAILABLE && why == KILN_TASK_REFUSAL_SHUTDOWN,
          "no new lease after the exit began");
    check(s.host_resume(suspended, 1) == KILN_OK, "a continuation is still accepted (never refused) ...");
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    check(g_host.segments.load() == segments, "... but no segment runs after the park");
    uint32_t left = 0;
    const auto t1 = std::chrono::steady_clock::now();
    s.drain_owner(1, 5000, &left);
    check(std::chrono::steady_clock::now() - t1 < std::chrono::seconds(1), "a timed drain after the exit returns at once");
    check(s.pending_tick_hops() == 1, "the pending hop is left to the tick (a world's STOPPING ends it cancelled)");
    s.session_stopping(1);
    {
        std::lock_guard lock(g_hops.mutex);
        check(g_hops.seen.size() == 1 && g_hops.seen[0].first == hopping && g_hops.seen[0].second == 1,
              "the STOPPING after the park dispatched it cancelled, on the stopping thread, not on a worker");
    }
}

// ---- client-world hops (#82): per-world sessions, queues, budgets and STOPPING ----

void reset_hops(HostPlatform* platform = nullptr, uint64_t cost_ns = 0) {
    std::lock_guard lock(g_hops.mutex);
    g_hops.seen.clear();
    g_hops.what.clear();
    g_hops.platform = platform;
    g_hops.cost_ns = cost_ns;
}

// (lease, what, cancelled) of the n-th dispatch.
struct Seen {
    KilnLease lease = 0;
    uint32_t what = 0, cancelled = 9;
};
std::vector<Seen> seen_hops() {
    std::lock_guard lock(g_hops.mutex);
    std::vector<Seen> out;
    for (std::size_t i = 0; i < g_hops.seen.size(); ++i) out.push_back({g_hops.seen[i].first, g_hops.what[i], g_hops.seen[i].second});
    return out;
}

KILN_TASKS_TEST(client_hops_land_on_the_client_world) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const Settled settled{pool};
    s.host_bind(&hop_dispatch);
    reset_hops();
    const KilnLease l = suspended_lease(s, 1, "client");
    check(s.host_tick_hop_in(l, KILN_WORLD_CLIENT, 1) == KILN_ERR_UNAVAILABLE,
          "no client world yet: a client hop is refused at once");
    check(s.collect_tasks_window(KILN_WORLD_CLIENT, true).hops_refused == 1, "counted on the client world's account");
    check(s.collect_tasks_window().hops_refused == 0, "not on the server world's");
    s.session_started(KILN_WORLD_CLIENT, 1);
    check(s.host_tick_hop(l) == KILN_ERR_UNAVAILABLE, "a client world is not a server world: a server hop still refuses");
    check(s.host_tick_hop_in(l, KILN_WORLD_CLIENT, 1) == KILN_OK && s.pending_tick_hops(KILN_WORLD_CLIENT) == 1 &&
              s.pending_tick_hops(KILN_WORLD_SERVER) == 0,
          "queued on the client world only");
    check(s.run_tick_hops(KILN_WORLD_SERVER) == 0, "the server world's step runs none of it");
    check(s.run_tick_hops(KILN_WORLD_CLIENT) == 1, "the client world's step runs it");
    const auto seen = seen_hops();
    check(seen.size() == 1 && seen[0].lease == l && seen[0].what == KILN_LEASE_CLIENT_HOP && seen[0].cancelled == 0,
          "dispatched as KILN_LEASE_CLIENT_HOP, not cancelled");
    check(s.host_tick_hop_in(l, KILN_WORLD_PREDICTION, 1) == KILN_ERR_INVALID_ARGUMENT &&
              s.host_tick_hop_in(l, 9, 1) == KILN_ERR_INVALID_ARGUMENT,
          "the prediction world and unknown kinds take no hops");
    check(s.host_tick_hop_in(0x12345, KILN_WORLD_CLIENT, 1) == KILN_ERR_INTERNAL, "an unknown lease: INTERNAL");
    end_lease(s, 1, l);
}

KILN_TASKS_TEST(tick_hop_in_checks_the_session) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const Settled settled{pool};
    s.host_bind(&hop_dispatch);
    reset_hops();
    const KilnLease l = suspended_lease(s, 1, "bound");
    for (const uint32_t world : {uint32_t{KILN_WORLD_SERVER}, uint32_t{KILN_WORLD_CLIENT}}) {
        const std::string w = world == KILN_WORLD_SERVER ? "server" : "client";
        s.session_started(world, 1);
        check(s.host_tick_hop_in(l, world, 2) == KILN_ERR_UNAVAILABLE, w + ": a session that is not the live one");
        check(s.host_tick_hop_in(l, world, 0) == KILN_ERR_UNAVAILABLE, w + ": session 0 names none");
        s.session_stopping(world, 1);
        check(s.host_tick_hop_in(l, world, 1) == KILN_ERR_UNAVAILABLE, w + ": an ended session takes no hop");
        s.session_started(world, 2);
        check(s.host_tick_hop_in(l, world, 1) == KILN_ERR_UNAVAILABLE && s.pending_tick_hops(world) == 0,
              w + ": a hop for session 1 is never queued into session 2 (the re-host case)");
        check(s.host_tick_hop_in(l, world, 2) == KILN_OK, w + ": session 2's own hop is queued");
        s.run_tick_hops(world);
    }
    end_lease(s, 1, l);
}

KILN_TASKS_TEST(world_stopping_flushes_only_its_own_hops) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const Settled settled{pool};
    s.host_bind(&hop_dispatch);
    reset_hops();
    s.session_started(KILN_WORLD_SERVER, 1);
    s.session_started(KILN_WORLD_CLIENT, 1);
    const KilnLease a = suspended_lease(s, 1, "server side"), b = suspended_lease(s, 2, "client side");
    check(s.host_tick_hop_in(a, KILN_WORLD_SERVER, 1) == KILN_OK && s.host_tick_hop_in(b, KILN_WORLD_CLIENT, 1) == KILN_OK,
          "one hop queued in each world");
    s.session_stopping(KILN_WORLD_CLIENT, 1);  // a hosting client leaves: its client world stops first
    auto seen = seen_hops();
    check(seen.size() == 1 && seen[0].lease == b && seen[0].what == KILN_LEASE_CLIENT_HOP && seen[0].cancelled == 1,
          "the client world's STOPPING dispatched its hop cancelled");
    check(s.pending_tick_hops(KILN_WORLD_SERVER) == 1, "the server world's hop is untouched");
    check(s.run_tick_hops(KILN_WORLD_SERVER) == 1, "and runs at the server world's next step");
    seen = seen_hops();
    check(seen.size() == 2 && seen[1].lease == a && seen[1].what == KILN_LEASE_TICK_HOP && seen[1].cancelled == 0,
          "...as a server tick hop, not cancelled");
    check(s.host_tick_hop_in(a, KILN_WORLD_CLIENT, 1) == KILN_ERR_UNAVAILABLE, "the stopped client world takes no more");
    s.session_stopping(KILN_WORLD_SERVER, 1);
    end_lease(s, 1, a);
    end_lease(s, 2, b);
}

KILN_TASKS_TEST(client_hop_budgets_are_its_own) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const Settled settled{pool};
    s.host_bind(&hop_dispatch);
    reset_hops(&p, 0);
    s.session_started(KILN_WORLD_SERVER, 1);
    s.session_started(KILN_WORLD_CLIENT, 1);
    const KilnLease a = suspended_lease(s, 1, "a"), b = suspended_lease(s, 2, "b");
    for (int i = 0; i < 100; ++i) s.host_tick_hop_in(a, KILN_WORLD_CLIENT, 1);
    for (int i = 0; i < 3; ++i) s.host_tick_hop_in(b, KILN_WORLD_CLIENT, 1);
    for (int i = 0; i < 5; ++i) s.host_tick_hop_in(a, KILN_WORLD_SERVER, 1);
    check(s.run_tick_hops(KILN_WORLD_CLIENT) == 67, "64 hops of a mod a client step; the other mod in the same step");
    check(s.pending_tick_hops(KILN_WORLD_SERVER) == 5 && s.run_tick_hops(KILN_WORLD_SERVER) == 5,
          "the server world's hops are not counted against the client's");
    check(s.run_tick_hops(KILN_WORLD_CLIENT) == 36 && s.pending_tick_hops(KILN_WORLD_CLIENT) == 0, "the rest next step");
    {
        const auto client = s.collect_tasks_window(KILN_WORLD_CLIENT, true);
        const auto server = s.collect_tasks_window();
        check(client.apply_only && client.apply.applied == 103 && client.apply.owner_budget_stops == 1 &&
                  client.apply.deferred == 36,
              "the client world's own apply account");
        check(!server.apply_only && server.apply.applied == 5 && server.apply.owner_budget_stops == 0,
              "the server world's own account, in its whole window");
    }
    // time: 0.4 ms a hop. Client: a mod stops after its 1 ms (3 hops), all mods after 2 ms; server: 2 ms and 4 ms.
    reset_hops(&p, 400 * 1000);
    for (const KilnLease l : {a, b})
        for (int i = 0; i < 10; ++i) s.host_tick_hop_in(l, KILN_WORLD_CLIENT, 1);
    const std::size_t client_step = s.run_tick_hops(KILN_WORLD_CLIENT);
    check(client_step == 5 || client_step == 6,
          "the client step's 2 ms in all, a mod's 1 ms, checked between callbacks (got " + std::to_string(client_step) + ")");
    {
        std::lock_guard lock(g_hops.mutex);
        int run = 0;
        KilnLease last = 0;
        bool turns = true;
        for (auto& [l, cancelled] : g_hops.seen) {
            run = l == last ? run + 1 : 1;
            last = l;
            turns = turns && run <= 3;
        }
        check(turns, "a mod's client turn ends at its 1 ms (3 hops of 0.4 ms)");
    }
    reset_hops(&p, 400 * 1000);
    for (int i = 0; i < 20; ++i) s.host_tick_hop_in(a, KILN_WORLD_SERVER, 1);
    const std::size_t server_step = s.run_tick_hops(KILN_WORLD_SERVER);
    check(server_step == 5 || server_step == 6, "the server world keeps its 2 ms per mod (got " + std::to_string(server_step) + ")");
    reset_hops(&p, 0);
    while (s.pending_tick_hops(KILN_WORLD_CLIENT)) s.run_tick_hops(KILN_WORLD_CLIENT);
    while (s.pending_tick_hops(KILN_WORLD_SERVER)) s.run_tick_hops(KILN_WORLD_SERVER);
    end_lease(s, 1, a);
    end_lease(s, 2, b);
}

KILN_TASKS_TEST(world_session_states) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    uint32_t state = 9;
    check(s.host_world_session(KILN_WORLD_CLIENT, &state) == 0 && state == 0, "no client world yet");
    s.session_started(KILN_WORLD_CLIENT, 3);
    check(s.host_world_session(KILN_WORLD_CLIENT, &state) == 3 && state == 1, "session 3, live");
    check(s.host_world_session(KILN_WORLD_SERVER, &state) == 0 && state == 0, "the server world has none");
    check(s.host_server_session() == 0, "server_session still names only the server world");
    s.session_stopping(KILN_WORLD_CLIENT, 3);
    check(s.host_world_session(KILN_WORLD_CLIENT, &state) == 3 && state == 2, "session 3, its STOPPING came");
    s.session_started(KILN_WORLD_CLIENT, 4);
    check(s.host_world_session(KILN_WORLD_CLIENT, &state) == 4 && state == 1, "session 4, live");
    check(s.host_world_session(KILN_WORLD_PREDICTION, &state) == 0 && state == 0, "the prediction world has no line");
}

KILN_TASKS_TEST(resume_after_step_world_follows_its_world) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const Settled settled{pool};
    s.host_bind(&dispatch);
    s.session_started(KILN_WORLD_SERVER, 3);  // another number than the client's: a window compares its own world
    s.session_started(KILN_WORLD_CLIENT, 1);
    s.post_edge(KILN_WORLD_CLIENT, 1, 1);
    const KilnLease l = suspended_lease(s, 1, "client window");
    std::atomic<uint32_t> seen{9};
    on_segment([&](KilnLease lease, uint32_t cancelled) {
        uint64_t r = 0;
        s.host_arm(lease, &r);  // armed before the test sees the wake, so its next window finds it armed
        seen = cancelled;
    });
    uint32_t why = 0;
    check(s.host_resume_after_step_world(l, 1, KILN_WORLD_SERVER, 1, &why) == KILN_ERR_UNAVAILABLE &&
              why == KILN_TASK_REFUSAL_NO_SERVER_WORLD,
          "server session 1 is not the live server session (3): refused NO_SERVER_WORLD, whatever the client world's is");
    check(s.host_resume_after_step_world(l, 1, KILN_WORLD_CLIENT, 2, &why) == KILN_ERR_UNAVAILABLE &&
              why == KILN_TASK_REFUSAL_NO_WORLD,
          "another client session: refused NO_WORLD, the registration still armed");
    check(s.host_resume_after_step_world(l, 1, KILN_WORLD_PREDICTION, 1, &why) == KILN_ERR_INVALID_ARGUMENT,
          "no window in the prediction world");
    check(s.host_resume_after_step_world(l, 1, KILN_WORLD_CLIENT, 1, &why) == KILN_OK, "armed for client session 1");
    s.post_edge(KILN_WORLD_SERVER, 3, 5);
    std::this_thread::sleep_for(std::chrono::milliseconds(5));
    check(seen.load() == 9, "a server Post edge does not wake a client window");
    s.post_edge(KILN_WORLD_CLIENT, 1, 2);
    check(until([&] { return seen.load() != 9; }) && seen.load() == 0, "the client world's next Post edge does");
    on_segment([&](KilnLease lease, uint32_t cancelled) {
        seen = cancelled;
        s.host_finish(lease, cancelled ? 2 : 0);
    });
    seen = 9;
    check(s.host_resume_after_step_world(l, 2, KILN_WORLD_CLIENT, 1, &why) == KILN_OK, "armed again");
    s.session_stopping(KILN_WORLD_SERVER, 3);
    std::this_thread::sleep_for(std::chrono::milliseconds(5));
    check(seen.load() == 9, "the server world's STOPPING leaves a client window waiting");
    s.session_stopping(KILN_WORLD_CLIENT, 1);
    check(until([&] { return seen.load() != 9; }) && seen.load() == 1, "the client world's STOPPING resumes it cancelled");
    on_segment(nullptr);
}

// The session-change race (TSan; test-tasks.sh runs this binary 20 times in its race pass): a client hop asked for
// session 1 on a worker while the main thread stops session 1 and starts session 2. The hop is queued before the stop
// (then the stop flushes it cancelled) or refused; it never runs in session 2.
KILN_TASKS_TEST(tick_hop_in_racing_a_session_change) {
    for (int round = 0; round < 50; ++round) {
        HostPlatform p;
        Env env;
        Pool pool(config(), &p);
        Service s(pool, env);
        const Settled settled{pool};
        s.host_bind(&hop_dispatch);
        reset_hops();
        s.session_started(KILN_WORLD_CLIENT, 1);
        const KilnLease l = suspended_lease(s, 1, "race");
        kiln::tasks::test::StartLine line(2);
        KilnResult r = KILN_ERR_INTERNAL;
        std::thread asker([&] {
            line.arrive();
            r = s.host_tick_hop_in(l, KILN_WORLD_CLIENT, 1);
        });
        std::thread world([&] {
            line.arrive();
            s.session_stopping(KILN_WORLD_CLIENT, 1);
            s.session_started(KILN_WORLD_CLIENT, 2);
            s.run_tick_hops(KILN_WORLD_CLIENT);  // session 2's first step
        });
        asker.join();
        world.join();
        const auto seen = seen_hops();
        if (r == KILN_OK) {
            check(seen.size() == 1 && seen[0].cancelled == 1, "queued before the stop: flushed cancelled by it");
        } else {
            check(r == KILN_ERR_UNAVAILABLE && seen.empty(), "or refused, and nothing dispatched");
        }
        check(s.pending_tick_hops(KILN_WORLD_CLIENT) == 0 && s.run_tick_hops(KILN_WORLD_CLIENT) == 0,
              "nothing of session 1 waits in session 2");
        end_lease(s, 1, l);
        if (kiln::tasks::test::g_failures.load()) break;
    }
}

// The same window forced open: the stop and the next start land between the hop's session check and its push.
KILN_TASKS_TEST(tick_hop_in_forced_between_check_and_push) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const Settled settled{pool};
    s.host_bind(&hop_dispatch);
    reset_hops();
    s.session_started(KILN_WORLD_CLIENT, 1);
    const KilnLease l = suspended_lease(s, 1, "late");
    s.set_test_hook([&](Service::Point at) {
        if (at != Service::Point::HopBeforePush) return;
        s.session_stopping(KILN_WORLD_CLIENT, 1);
        s.session_started(KILN_WORLD_CLIENT, 2);  // the next world is open when the push comes
    });
    const KilnResult r = s.host_tick_hop_in(l, KILN_WORLD_CLIENT, 1);
    s.set_test_hook(nullptr);
    check(r == KILN_ERR_UNAVAILABLE && s.pending_tick_hops(KILN_WORLD_CLIENT) == 0,
          "refused: an open next session does not take a hop of the ended one");
    check(s.run_tick_hops(KILN_WORLD_CLIENT) == 0 && seen_hops().empty(), "nothing of it runs in session 2");
    end_lease(s, 1, l);
}

// Both worlds at once on their own threads (a hosting client: GameServer and the main thread): one world's sessions
// starting and stopping never touch the other world's hops. TSan sees the shared queues' lock.
KILN_TASKS_TEST(worlds_on_two_threads) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const Settled settled{pool};
    s.host_bind(&hop_dispatch);
    reset_hops();
    s.session_started(KILN_WORLD_CLIENT, 1);
    const KilnLease l = suspended_lease(s, 1, "client hops");
    std::atomic<bool> stop{false};
    std::thread server([&] {
        for (uint32_t session = 1; !stop.load(); ++session) {
            s.session_started(KILN_WORLD_SERVER, session);
            s.post_edge(KILN_WORLD_SERVER, session, 1);
            s.run_tick_hops(KILN_WORLD_SERVER);
            s.session_stopping(KILN_WORLD_SERVER, session);
        }
    });
    std::size_t queued = 0, ran = 0;
    for (int step = 0; step < 300; ++step) {
        if (s.host_tick_hop_in(l, KILN_WORLD_CLIENT, 1) == KILN_OK) ++queued;
        ran += s.run_tick_hops(KILN_WORLD_CLIENT);
    }
    stop = true;
    server.join();
    bool none_cancelled = true;
    for (const Seen& h : seen_hops()) none_cancelled = none_cancelled && h.cancelled == 0 && h.what == KILN_LEASE_CLIENT_HOP;
    check(queued == 300 && ran == 300 && none_cancelled,
          "every client hop queued and ran, none cancelled by the server world's sessions");
    end_lease(s, 1, l);
}
}  // namespace

int main(int argc, char** argv) { return kiln::tasks::test::run(argc, argv, "tasks host"); }
