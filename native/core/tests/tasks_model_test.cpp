// SPDX-License-Identifier: MIT
// kiln.tasks model (src/tasks_model.cpp): admission versus seal/revoke, dequeue versus cancel, terminal
// precedence, capacity with retained records and continuations, done quiescence, fair ordering, bounded bookkeeping.
// Built twice by test-tasks.sh (ASan/UBSan and ThreadSanitizer); `tasks_model_test <name>` runs one test.
#include <algorithm>
#include <atomic>
#include <new>
#include <chrono>
#include <string>
#include <thread>
#include <tuple>
#include <vector>

#include "tasks_model.h"
#include "tasks_test_support.h"

using namespace kiln::tasks;
using kiln::tasks::test::check;

namespace {

SubmitArgs named(const char* name, Priority priority = Priority::Normal) {
    SubmitArgs a;
    a.name = name;
    a.priority = priority;
    a.payload = {0x1000, 0x2000, 0x3000};
    return a;
}

KilnHandle submit_ok(Model& m, KilnOwner owner, const SubmitArgs& a, const std::string& what) {
    KilnHandle h = 0;
    Refusal why = Refusal::Capability;  // must stay untouched on OK
    const KilnResult r = m.submit(owner, a, &h, &why);
    check(r == KILN_OK && h != 0 && why == Refusal::Capability, what + ": submit accepted, refusal untouched");
    return h;
}

void refused(Model& m, KilnOwner owner, const SubmitArgs& a, KilnResult want, Refusal want_why, const std::string& what) {
    KilnHandle h = 12345;
    Refusal why = Refusal::None;
    const KilnResult r = m.submit(owner, a, &h, &why);
    check(r == want && why == want_why && h == 0, what + " (got result " + std::to_string(r) + ", refusal " +
                                                      std::to_string(static_cast<uint32_t>(why)) + ")");
}

State state_of(const Model& m, KilnOwner owner, KilnHandle h) {
    State s = State::None;
    m.status(owner, h, &s);
    return s;
}

// Runs every ready piece of work of `slot` to the end: bodies and continuations complete, done deliveries return.
int drive(Model& m, Slot slot = Slot::Normal, Outcome outcome = Outcome::Completed) {
    int ran = 0;
    Work w;
    while (m.dequeue(slot, &w)) {
        ++ran;
        if (w.kind == Work::Kind::Done) m.done_returned(w);
        else m.segment_ended(w, false, outcome);
    }
    return ran;
}

// ---------------------------------------------------------------------------------------------------------------
// 1. Acceptance held just before publication while another thread seals or revokes.
KILN_TASKS_TEST(submit_vs_seal) {
    for (const bool revoke : {false, true}) {
        const std::string t = revoke ? "submit vs retire: " : "submit vs seal: ";
        Model m;
        check(m.open_owner(7, "mod.seven") == KILN_OK, t + "owner opened");
        kiln::tasks::test::Gate in_window, sealed;
        bool lock_free_in_window = false;
        m.set_test_hook([&](Model::Point p) {
            if (p != Model::Point::SubmitBeforePublish) return;
            lock_free_in_window = !m.lock_held_by_anyone();
            in_window.open();
            sealed.wait();
        });
        KilnHandle h = 99;
        Refusal why = Refusal::None;
        KilnResult r = KILN_OK;
        std::thread submitter([&] { r = m.submit(7, named("late"), &h, &why); });
        in_window.wait();
        uint32_t outstanding = 77;
        if (revoke) m.retire(7, &outstanding);
        else m.seal(7, &outstanding);
        sealed.open();
        submitter.join();
        check(lock_free_in_window, t + "the hook runs outside the model mutex");
        check(r == (revoke ? KILN_ERR_REVOKED : KILN_ERR_UNAVAILABLE) && why == Refusal::OwnerSealed && h == 0,
              t + "a submission published after the seal is refused, never accepted");
        check(outstanding == 0 && m.counts().outstanding == 0 && m.counts().ready == 0,
              t + "nothing tracked or queued for it");
    }
    // stress: two threads, one submitting, one sealing; every acceptance is counted and cancelled, none starts
    for (int round = 0; round < 200; ++round) {
        Model m;
        m.open_owner(1, "a");
        kiln::tasks::test::StartLine go(2);
        std::vector<KilnHandle> accepted;
        uint32_t outstanding = 0;
        std::thread submitter([&] {
            go.arrive();
            for (int i = 0; i < 64; ++i) {
                KilnHandle h = 0;
                Refusal why = Refusal::None;
                if (m.submit(1, named("t"), &h, &why) == KILN_OK) accepted.push_back(h);
            }
        });
        std::thread sealer([&] {
            go.arrive();
            m.seal(1, &outstanding);
        });
        submitter.join();
        sealer.join();
        bool all_cancelled = true;
        for (KilnHandle h : accepted) all_cancelled &= state_of(m, 1, h) == State::None;  // sealed: auto-released
        int bodies = 0, dones = 0;
        Work w;
        while (m.dequeue(Slot::Normal, &w)) {
            if (w.kind == Work::Kind::Body) ++bodies;
            if (w.kind == Work::Kind::Done) {
                ++dones;
                all_cancelled &= w.final_state == State::Cancelled;
                m.done_returned(w);
            }
        }
        check(bodies == 0 && dones == static_cast<int>(accepted.size()) && all_cancelled && m.counts().outstanding == 0,
              "submit vs seal stress round " + std::to_string(round) + ": " + std::to_string(accepted.size()) +
                  " accepted, all cancelled with one done each, no body started");
        if (kiln::tasks::test::g_failures.load()) break;
    }
}

// 2. Dequeue versus cancel. Both orders are forced deterministically (no sleep used as synchronisation), then a stress
// loop checks the invariant each round: exactly one of started / cancelled-first, never both and never neither, and
// done is delivered exactly once.
//
// Drives one task through a fresh model in a chosen order and returns whether its body started. `cancel_first` true
// cancels before any dequeue; false dequeues (starting the body) before cancelling.
bool dequeue_cancel_order(bool cancel_first, const std::string& what) {
    Model m;
    m.open_owner(1, "a");
    const KilnHandle h = submit_ok(m, 1, named("t"), what);
    bool started = false;
    State delivered = State::None;
    int dones = 0;
    if (cancel_first) {
        // (a) cancel before dequeue: the queued task never starts; its done is Cancelled.
        State s = State::None;
        check(m.cancel(1, h, &s) == KILN_OK && s == State::Cancelled, what + ": cancel before dequeue reports Cancelled");
        Work w;
        check(!m.dequeue(Slot::Normal, &w) || w.kind != Work::Kind::Body, what + ": no body is ever dequeued");
        if (w.kind == Work::Kind::Done) {
            ++dones;
            delivered = w.final_state;
            m.done_returned(w);
        }
    } else {
        // (b) dequeue before cancel: the body starts, a late cancel only requests (the running task is unaffected).
        Work body;
        check(m.dequeue(Slot::Normal, &body) && body.kind == Work::Kind::Body && state_of(m, 1, h) == State::Running,
              what + ": the body started before the cancel");
        started = true;
        State s = State::None;
        check(m.cancel(1, h, &s) == KILN_OK && s == State::Running, what + ": a cancel after dequeue is too late (Running)");
        check(body.cancel_requested(), what + ": the running segment still sees the request");
        m.segment_ended(body, false, Outcome::Completed);  // a normal finish wins over the late request
    }
    Work w;
    while (m.dequeue(Slot::Normal, &w)) {
        if (w.kind == Work::Kind::Body) check(false, what + ": a cancelled task must not start a body");
        if (w.kind == Work::Kind::Done) {
            ++dones;
            delivered = w.final_state;
            m.done_returned(w);
        }
    }
    check(dones == 1, what + ": exactly one done delivered");
    check(delivered == (started ? State::Completed : State::Cancelled) && state_of(m, 1, h) == delivered,
          what + ": the outcome matches the order");
    check(m.counts().outstanding == 0, what + ": quiescent");
    return started;
}

KILN_TASKS_TEST(dequeue_vs_cancel) {
    check(!dequeue_cancel_order(true, "forced cancel-first"), "forced cancel-first: the body did not start");
    check(dequeue_cancel_order(false, "forced dequeue-first"), "forced dequeue-first: the body started");
    // stress: both threads race through the barrier; the model must give exactly one outcome per round.
    for (int round = 0; round < 2000; ++round) {
        Model m;
        m.open_owner(1, "a");
        const KilnHandle h = submit_ok(m, 1, named("t"), "stress");
        kiln::tasks::test::StartLine go(2);
        std::atomic<int> bodies{0}, worker_dones{0};
        std::atomic<bool> worker_done_ok{true};
        std::thread worker([&] {
            go.arrive();
            Work w;
            if (m.dequeue(Slot::Normal, &w) && w.kind == Work::Kind::Body) {
                bodies.fetch_add(1);
                m.segment_ended(w, false, Outcome::Completed);
            } else if (w.kind == Work::Kind::Done) {
                worker_dones.fetch_add(1);  // the worker took the sole done itself (cancel won the race)
                if (m.done_returned(w) != KILN_OK) worker_done_ok.store(false);
            }
        });
        std::thread canceller([&] {
            go.arrive();
            State s = State::None;
            m.cancel(1, h, &s);
        });
        worker.join();
        canceller.join();
        int dones = 0, more_bodies = 0;
        bool drain_done_ok = true;
        State delivered = State::None;
        Work w;
        while (m.dequeue(Slot::Normal, &w)) {
            if (w.kind == Work::Kind::Body) ++more_bodies;
            if (w.kind == Work::Kind::Done) {
                ++dones;
                delivered = w.final_state;
                if (m.done_returned(w) != KILN_OK) drain_done_ok = false;
            }
        }
        const int started = bodies.load() + more_bodies;
        const State final = state_of(m, 1, h);
        check(started <= 1 && more_bodies == 0, "stress round " + std::to_string(round) + ": the body started at most once");
        check(final == (started == 1 ? State::Completed : State::Cancelled), "stress: the final state matches the outcome");
        // exactly one done in total, whoever delivered it, and each done_returned accepted
        check(worker_dones.load() + dones == 1, "stress round " + std::to_string(round) + ": exactly one done delivered (" +
                                                    std::to_string(worker_dones.load()) + " by the worker, " +
                                                    std::to_string(dones) + " by the drain)");
        check(worker_done_ok.load() && drain_done_ok, "stress: done_returned accepted every delivery");
        check(delivered == State::None || delivered == final, "stress: the drained done carries the final state");
        check(m.counts().outstanding == 0, "stress: quiescent");
        if (kiln::tasks::test::g_failures.load()) break;
    }
}

// 3. Cancel racing every classification; status only moves forward.
KILN_TASKS_TEST(terminal_precedence) {
    struct Case {
        bool start, cancel;
        Outcome outcome;
        State want;
        const char* what;
    };
    const Case cases[] = {
        {false, true, Outcome::Completed, State::Cancelled, "never started + cancelled -> Cancelled"},
        {true, true, Outcome::Completed, State::Completed, "normal finish wins over a late cancel"},
        {true, true, Outcome::OwnTokenCancelled, State::Cancelled, "own-token cancellation after a request -> Cancelled"},
        {true, false, Outcome::OwnTokenCancelled, State::Faulted, "own-token cancellation without a request -> Faulted"},
        {true, true, Outcome::Faulted, State::Faulted, "a fault after a request stays Faulted"},
        {true, false, Outcome::Completed, State::Completed, "plain completion"},
    };
    for (const Case& c : cases) {
        Model m;
        m.open_owner(1, "a");
        const KilnHandle h = submit_ok(m, 1, named("t"), c.what);
        Work w;
        if (c.start) {
            check(m.dequeue(Slot::Normal, &w) && w.kind == Work::Kind::Body && state_of(m, 1, h) == State::Running,
                  std::string(c.what) + ": running");
            check(!w.cancel_requested(), std::string(c.what) + ": no cancel yet");
        }
        if (c.cancel) {
            State s = State::None;
            check(m.cancel(1, h, &s) == KILN_OK && s == (c.start ? State::Running : State::Cancelled),
                  std::string(c.what) + ": cancel reports the state after it");
            if (c.start) check(w.cancel_requested(), std::string(c.what) + ": the running segment sees the request");
        }
        if (c.start) m.segment_ended(w, false, c.outcome);
        check(state_of(m, 1, h) == c.want, c.what);
        Work done;
        check(m.dequeue(Slot::Normal, &done) && done.kind == Work::Kind::Done && done.final_state == c.want,
              std::string(c.what) + ": the done delivery carries the same final state");
        m.done_returned(done);
        State after = State::None;
        m.cancel(1, h, &after);
        check(after == c.want, std::string(c.what) + ": cancelling a terminal task changes nothing");
    }
    // status observed concurrently never goes backwards and a terminal state never changes
    for (int round = 0; round < 300; ++round) {
        Model m;
        m.open_owner(1, "a");
        const KilnHandle h = submit_ok(m, 1, named("t"), "monotonic status");
        std::atomic<bool> stop{false};
        std::vector<State> seen;
        std::thread poller([&] {
            while (!stop.load()) seen.push_back(state_of(m, 1, h));
            seen.push_back(state_of(m, 1, h));
        });
        std::thread canceller([&] {
            State s;
            m.cancel(1, h, &s);
        });
        Work w;
        if (m.dequeue(Slot::Normal, &w) && w.kind == Work::Kind::Body)
            m.segment_ended(w, false, round % 2 ? Outcome::OwnTokenCancelled : Outcome::Completed);
        canceller.join();
        stop = true;
        poller.join();
        bool monotonic = true;
        for (std::size_t i = 1; i < seen.size(); ++i) {
            if (terminal(seen[i - 1])) monotonic &= seen[i] == seen[i - 1];
            else monotonic &= static_cast<uint32_t>(seen[i]) >= static_cast<uint32_t>(seen[i - 1]);
        }
        check(monotonic && !seen.empty() && terminal(seen.back()), "status moves only forward, round " + std::to_string(round));
        drive(m);
        if (kiln::tasks::test::g_failures.load()) break;
    }
}

// 4. Capacity counts retained terminal records; release restores a slot; continuations never spend quota.
KILN_TASKS_TEST(capacity_and_continuations) {
    Limits small;
    small.owner_capacity = 4;
    {
        Model m(small);
        m.open_owner(1, "a");
        std::vector<KilnHandle> hs;
        for (int i = 0; i < 4; ++i) hs.push_back(submit_ok(m, 1, named("t"), "fill capacity"));
        refused(m, 1, named("t"), KILN_ERR_UNAVAILABLE, Refusal::QueueFull, "the fifth submission refuses QUEUE_FULL");
        drive(m);
        check(state_of(m, 1, hs[0]) == State::Completed && m.counts().records == 4, "terminal records are retained");
        refused(m, 1, named("t"), KILN_ERR_UNAVAILABLE, Refusal::QueueFull,
                "retained terminal records still count against capacity (no eviction)");
        check(m.release(1, hs[0]) == KILN_OK, "release a terminal record");
        submit_ok(m, 1, named("t"), "release restored one slot");
        refused(m, 1, named("t"), KILN_ERR_UNAVAILABLE, Refusal::QueueFull, "and only one");
    }
    {
        // a full owner of suspended leases: every continuation still resumes, also after the owner is sealed
        Limits wide = small;
        wide.owner_running = 4;
        Model m(wide);
        m.open_owner(1, "a");
        std::vector<KilnHandle> hs;
        std::vector<uint64_t> regs;
        for (int i = 0; i < 4; ++i) hs.push_back(submit_ok(m, 1, named("lease"), "lease"));
        std::vector<Work> bodies(4);
        for (Work& w : bodies) check(m.dequeue(Slot::Normal, &w) && w.kind == Work::Kind::Body, "lease body");
        for (Work& w : bodies) {
            uint64_t reg = 0;
            check(m.arm(w.task, &reg) == KILN_OK && reg != 0, "the segment arms one resumption");
            uint64_t again = 0;
            check(m.arm(w.task, &again) == KILN_ERR_INTERNAL, "arming twice is an internal bug");
            const std::size_t ready = m.counts().ready;
            check(m.resume(w.task, reg) == KILN_OK, "a resumption before the segment returned is accepted");
            check(m.counts().ready == ready, "but it is not runnable while the arming segment still runs");
            check(m.segment_ended(w, true, Outcome::Completed) == KILN_OK && m.counts().ready == ready + 1,
                  "the segment suspends; now its continuation is runnable");
        }
        for (int i = 0; i < 4; ++i) {
            Work cont;
            check(m.dequeue(Slot::Normal, &cont) && cont.kind == Work::Kind::Continuation && cont.task == hs[i],
                  "the continuations run in order");
            uint64_t reg2 = 0;
            check(m.arm(cont.task, &reg2) == KILN_OK && reg2 > 1, "it awaits again: a new registration");
            check(m.segment_ended(cont, true, Outcome::Completed) == KILN_OK, "and suspends");
            regs.push_back(reg2);
        }
        refused(m, 1, named("new"), KILN_ERR_UNAVAILABLE, Refusal::QueueFull, "new roots refuse at capacity");
        uint32_t outstanding = 0;
        m.seal(1, &outstanding);
        check(outstanding == 4, "sealed with four suspended leases outstanding");
        for (int i = 0; i < 4; ++i) check(m.resume(hs[i], regs[i]) == KILN_OK, "a continuation resumes after the seal");
        check(m.resume(hs[0], regs[0]) == KILN_ERR_INTERNAL, "a second resumption of one registration is refused");
        check(m.resume(hs[0], regs[0] + 100) == KILN_ERR_INTERNAL, "an unknown registration is refused");
        int conts = 0, dones = 0;
        Work w;
        while (m.dequeue(Slot::Normal, &w)) {
            if (w.kind == Work::Kind::Continuation) {
                ++conts;
                check(w.cancel_requested(), "the seal signalled the suspended task");
                m.segment_ended(w, false, Outcome::OwnTokenCancelled);
            } else if (w.kind == Work::Kind::Done) {
                ++dones;
                check(w.final_state == State::Cancelled, "it finishes Cancelled by its own token");
                m.done_returned(w);
            }
        }
        check(conts == 4 && dones == 4 && m.counts().outstanding == 0, "every admitted continuation ran and finished");
    }
}

// 5. Done in flight while drain, release and queries run.
KILN_TASKS_TEST(done_quiescence) {
    Model m;
    m.open_owner(1, "a");
    m.open_owner(2, "b");
    const KilnHandle h = submit_ok(m, 1, named("t"), "done");
    const KilnHandle other = submit_ok(m, 2, named("t"), "other owner");
    Work body;
    m.dequeue(Slot::Normal, &body);
    check(m.release(1, h) == KILN_ERR_UNAVAILABLE && state_of(m, 1, h) == State::Running,
          "releasing a running task refuses and changes nothing");
    m.segment_ended(body, false, Outcome::Completed);
    Work done;
    while (m.dequeue(Slot::Normal, &done) && done.kind != Work::Kind::Done) m.segment_ended(done, false, Outcome::Completed);
    check(done.kind == Work::Kind::Done && done.task == h, "the done delivery of the task");
    uint32_t count = 0;
    m.outstanding(1, &count);
    check(count == 1, "done in flight: still outstanding");
    check(m.release(1, h) == KILN_OK, "release inside done is legal");
    check(state_of(m, 1, h) == State::None && m.status(1, h, nullptr) != KILN_OK, "released: no longer queryable");
    m.outstanding(1, &count);
    check(count == 1, "released, but the done has not returned: still not quiescent");
    check(m.release(1, h) == KILN_OK, "a second release of an issued handle is idempotent");
    check(m.release(1, h + 1000) == KILN_ERR_NOT_FOUND, "a never-issued handle is not found");
    check(m.release(1, other) == KILN_ERR_NOT_FOUND, "another owner's handle is foreign");
    check(done.record != nullptr && done.final_state == State::Completed, "the delivery still holds its record");
    m.done_returned(done);
    m.outstanding(1, &count);
    check(count == 0, "quiescent only after done returned");
    // concurrently: the count never reaches zero before done returns
    for (int round = 0; round < 500; ++round) {
        Model c;
        c.open_owner(1, "a");
        const KilnHandle t = submit_ok(c, 1, named("t"), "concurrent done");
        Work b;
        c.dequeue(Slot::Normal, &b);
        c.segment_ended(b, false, Outcome::Completed);
        Work d;
        c.dequeue(Slot::Normal, &d);
        std::atomic<bool> returning{false}, early_zero{false}, stop{false};
        std::thread drainer([&] {
            while (!stop.load()) {
                uint32_t n = 0;
                c.seal(1, &n);
                // zero may only be seen once done_returned has been entered (returning is set just before it)
                if (n == 0 && !returning.load()) early_zero = true;
                State s;
                c.status(1, t, &s);
                c.release(1, t);
            }
        });
        std::this_thread::yield();
        returning = true;
        c.done_returned(d);
        stop = true;
        drainer.join();
        check(!early_zero.load(), "drain never reports zero while done runs, round " + std::to_string(round));
        if (kiln::tasks::test::g_failures.load()) break;
    }
}

// 6. Fair ordering: round-robin owners, priority inside an owner, caps, LongRunning, sealed/after-step backlog.
KILN_TASKS_TEST(fair_ordering) {
    {
        Model m;  // owner_running 2
        for (KilnOwner o : {1, 2, 3}) m.open_owner(o, "o");
        std::vector<KilnHandle> h1, h2, h3;
        for (int i = 0; i < 3; ++i) {
            h1.push_back(submit_ok(m, 1, named("a"), "a"));
            h2.push_back(submit_ok(m, 2, named("b"), "b"));
            h3.push_back(submit_ok(m, 3, named("c"), "c"));
        }
        std::vector<KilnHandle> order;
        Work w;
        std::vector<Work> running;
        while (m.dequeue(Slot::Normal, &w)) {
            order.push_back(w.task);
            running.push_back(w);
        }
        const std::vector<KilnHandle> want{h1[0], h2[0], h3[0], h1[1], h2[1], h3[1]};
        check(order == want, "round-robin across owners, FIFO inside each, two running per owner at most");
        if (running.size() < 2) return;
        m.segment_ended(running[1], false, Outcome::Completed);  // owner 2 frees a slot
        check(m.dequeue(Slot::Normal, &w) && w.kind == Work::Kind::Done && w.owner == 2,
              "the freed owner's done delivery comes before its next body");
        m.done_returned(w);
        check(m.dequeue(Slot::Normal, &w) && w.task == h2[2], "then its next body");
    }
    {
        Model m;
        m.open_owner(1, "a");
        const KilnHandle low = submit_ok(m, 1, named("low", Priority::Low), "low");
        const KilnHandle n1 = submit_ok(m, 1, named("n1"), "normal 1");
        const KilnHandle high = submit_ok(m, 1, named("high", Priority::High), "high");
        const KilnHandle n2 = submit_ok(m, 1, named("n2"), "normal 2");
        std::vector<KilnHandle> order;
        Work w;
        for (int i = 0; i < 4; ++i) {
            check(m.dequeue(Slot::Normal, &w), "dequeue by priority");
            order.push_back(w.task);
            m.segment_ended(w, false, Outcome::Completed);
            Work d;
            m.dequeue(Slot::Normal, &d);
            m.done_returned(d);
        }
        check(order == std::vector<KilnHandle>{high, n1, n2, low}, "priority orders within one owner, FIFO for equal");
    }
    {
        Model m;
        m.open_owner(1, "a");
        m.open_owner(2, "b");
        // priority never raises an owner's share: owner 1's High tasks do not jump owner 2's Normal one
        submit_ok(m, 1, named("h1", Priority::High), "high 1");
        submit_ok(m, 1, named("h2", Priority::High), "high 2");
        const KilnHandle b = submit_ok(m, 2, named("b"), "b");
        Work w1, w2;
        m.dequeue(Slot::Normal, &w1);
        m.dequeue(Slot::Normal, &w2);
        check(w1.owner == 1 && w2.task == b, "High orders only inside owner 1; owner 2 still gets its turn");
    }
    {
        Limits l;
        l.long_running_per_owner = 2;
        l.long_running_global = 3;
        Model m(l);
        m.open_owner(1, "a");
        m.open_owner(2, "b");
        SubmitArgs lr = named("lr");
        lr.long_running = true;
        for (int i = 0; i < 3; ++i) submit_ok(m, 1, lr, "a long");
        for (int i = 0; i < 2; ++i) submit_ok(m, 2, lr, "b long");
        const KilnHandle normal = submit_ok(m, 1, named("n"), "a normal");
        std::vector<KilnOwner> owners;
        Work w;
        while (m.dequeue(Slot::LongRunning, &w)) owners.push_back(w.owner);
        check(owners == std::vector<KilnOwner>{1, 2, 1}, "LongRunning: round-robin, global cap 3");
        check(m.dequeue(Slot::Normal, &w) && w.task == normal, "LongRunning caps never block normal work");
        check(!m.dequeue(Slot::Normal, &w), "no LongRunning task runs on a normal slot");
    }
    {
        Model m;
        m.open_owner(1, "a");
        m.open_owner(2, "b");
        SubmitArgs after = named("after");
        after.after_step = true;
        after.session = 1;
        after.edge = 0;
        const KilnHandle waiting = submit_ok(m, 1, after, "after step");
        const KilnHandle b = submit_ok(m, 2, named("b"), "b");
        Work w;
        check(m.dequeue(Slot::Normal, &w) && w.task == b && !m.dequeue(Slot::Normal, &w),
              "an after-step backlog does not hide another owner's work");
        check(m.counts().waiting == 1 && state_of(m, 1, waiting) == State::Queued, "waiting is Queued");
        m.edge(1, 1);
        check(m.dequeue(Slot::Normal, &w) && w.task == waiting, "the edge makes it runnable");
        // captured before the latest edge: runnable at once (no lost wakeup)
        after.edge = 0;
        const KilnHandle late = submit_ok(m, 1, after, "captured an old edge");
        check(m.dequeue(Slot::Normal, &w) && w.task == late, "a submission that captured an old edge is ready at once");
        after.edge = 1;
        submit_ok(m, 1, after, "captured the current edge");
        check(!m.dequeue(Slot::Normal, &w) || w.kind != Work::Kind::Body, "the current edge does not release it");
        m.edge(1, 1);
        check(m.counts().waiting == 1, "the same edge again changes nothing");
        m.edge(1, 2);
        check(m.counts().waiting == 0, "a newer edge does");
    }
    {
        Model m;
        m.open_owner(1, "a");
        m.open_owner(2, "b");
        for (int i = 0; i < 5; ++i) submit_ok(m, 1, named("a"), "a backlog");
        const KilnHandle b = submit_ok(m, 2, named("b"), "b");
        uint32_t n = 0;
        m.seal(1, &n);
        std::vector<KilnOwner> owners;
        Work w;
        while (m.dequeue(Slot::Normal, &w)) {
            owners.push_back(w.owner);
            if (w.kind == Work::Kind::Done) m.done_returned(w);
            else m.segment_ended(w, false, Outcome::Completed);
        }
        check(owners.size() > 1 && std::count(owners.begin(), owners.end(), 2) == 2 && owners[1] == 2,
              "a sealed owner's cancelled backlog (done deliveries) does not starve another owner");
        check(state_of(m, 2, b) == State::Completed, "the other owner's task completed");
    }
}

// 7. Bounded bookkeeping, handle exhaustion without wrap, no stale alias after recreation.
KILN_TASKS_TEST(bounded_bookkeeping) {
    Model m;
    m.open_owner(1, "a");
    for (int i = 0; i < 20000; ++i) {
        const KilnHandle h = submit_ok(m, 1, named("t"), "cycle");
        drive(m);
        if (m.release(1, h) != KILN_OK) {
            check(false, "release in cycle " + std::to_string(i));
            break;
        }
    }
    const Counts c = m.counts();
    check(c.records == 0 && c.ready == 0 && c.outstanding == 0 && c.running == 0 && c.ready_max <= 2,
          "20000 submit/release cycles leave no bookkeeping behind (ready max " + std::to_string(c.ready_max) + ")");
    Limits l;
    l.max_sequence = 3;
    Model e(l);
    e.open_owner(1, "a");
    KilnHandle last = 0;
    for (int i = 0; i < 3; ++i) {
        last = submit_ok(e, 1, named("t"), "handle");
        drive(e);
        e.release(1, last);
    }
    refused(e, 1, named("t"), KILN_ERR_UNAVAILABLE, Refusal::QueueFull, "handle exhaustion refuses, never wraps");
    check(e.release(1, last) == KILN_OK, "the last issued handle is still recognised as released");
    // owner recreation: the old instance's handles are foreign to the new one
    Model r;
    r.open_owner(5, "five");
    const KilnHandle old = submit_ok(r, 5, named("t"), "old instance");
    uint32_t n = 0;
    r.retire(5, &n);
    check(r.open_owner(5, "five again") == KILN_OK, "a retired owner id opens a new instance");
    const KilnHandle fresh = submit_ok(r, 5, named("t"), "new instance");
    State s = State::Running;
    check(fresh != old && r.status(5, old, &s) == KILN_ERR_NOT_FOUND && r.release(5, old) == KILN_ERR_NOT_FOUND,
          "the old instance's handle is foreign to the new one");
    check(r.open_owner(5, "twice") == KILN_ERR_INVALID_ARGUMENT, "an open owner cannot be opened again");
}

// Argument and owner-state refusals, name rules.
KILN_TASKS_TEST(refusals) {
    Model m;
    check(m.open_owner(0, "core") == KILN_ERR_INVALID_ARGUMENT, "owner 0 is the core, never a task owner");
    m.open_owner(1, "a");
    refused(m, 2, named("t"), KILN_ERR_REVOKED, Refusal::OwnerSealed, "an unknown owner is refused as dead");
    const std::string too_long(129, 'x');
    for (const std::string& bad : {std::string(), too_long, std::string("a\0b", 3), std::string("tab\there"),
                                   std::string("\xff\xfe"), std::string("\xc3")}) {
        SubmitArgs a = named("x");
        a.name = bad;
        refused(m, 1, a, KILN_ERR_INVALID_ARGUMENT, Refusal::Argument, "a bad task name is refused");
    }
    SubmitArgs ok = named("x");
    const std::string longest(128, 'y');
    ok.name = longest;
    submit_ok(m, 1, ok, "a 128-byte name is fine");
    ok.name = "caf\xc3\xa9 \xe2\x9c\x93";
    submit_ok(m, 1, ok, "multi-byte UTF-8 is fine");
    SubmitArgs prio = named("p");
    prio.priority = static_cast<Priority>(3);
    refused(m, 1, prio, KILN_ERR_INVALID_ARGUMENT, Refusal::Argument, "an unknown priority is refused");
    m.quarantine(1);
    refused(m, 1, named("t"), KILN_ERR_UNAVAILABLE, Refusal::OwnerQuarantined, "a quarantined owner refuses");
    m.open_owner(3, "c");
    uint32_t n = 0;
    m.seal(3, &n);
    refused(m, 3, named("t"), KILN_ERR_UNAVAILABLE, Refusal::OwnerSealed, "a sealed owner refuses");
    m.retire(3, &n);
    refused(m, 3, named("t"), KILN_ERR_REVOKED, Refusal::OwnerSealed, "a retired owner refuses as dead");
    const std::string long_owner = std::string(126, 'o') + "\xe2\x9c\x93";  // 129 bytes, the last char is 3 bytes
    m.open_owner(4, long_owner);
    check(m.owner_name(4) == std::string(126, 'o'), "owner names are cut at a character boundary to 128 bytes");
    m.retire(4, &n);
    check(m.owner_name(4) == std::string(126, 'o'), "and survive retirement");
}

// Quarantine: queued roots cancelled, running ones finish, records retained.
KILN_TASKS_TEST(quarantine) {
    Model m;
    m.open_owner(1, "a");
    const KilnHandle running = submit_ok(m, 1, named("r"), "running");
    const KilnHandle queued = submit_ok(m, 1, named("q"), "queued");
    Work w;
    m.dequeue(Slot::Normal, &w);
    check(m.quarantine(1) == KILN_OK, "quarantine");
    check(state_of(m, 1, queued) == State::Cancelled && state_of(m, 1, running) == State::Running && !w.cancel_requested(),
          "queued roots are cancelled; the running one is neither cancelled nor signalled");
    m.segment_ended(w, false, Outcome::Completed);
    drive(m);
    check(state_of(m, 1, running) == State::Completed && m.release(1, running) == KILN_OK && m.release(1, queued) == KILN_OK,
          "records are retained and released normally");
}

// A task still running when its owner is sealed or retired releases itself when it finishes: nobody may release it
// any more, so no record stays behind once its done returned (the deterministic witness; retire_vs_queries races it).
KILN_TASKS_TEST(late_terminal_auto_releases) {
    for (const bool retire : {false, true}) {
        const std::string how = retire ? " (retired)" : " (sealed)";
        Model m;
        m.open_owner(1, "a");
        const KilnHandle h = submit_ok(m, 1, named("r"), "running");
        Work w;
        check(m.dequeue(Slot::Normal, &w) && w.kind == Work::Kind::Body && w.task == h, "the body is running" + how);
        uint32_t n = 0;
        if (retire) m.retire(1, &n);
        else m.seal(1, &n);
        check(n >= 1 && m.counts().records == 1, "the owner closed with its task in flight" + how);
        m.segment_ended(w, false, Outcome::Completed);
        drive(m);
        check(m.counts().outstanding == 0 && m.counts().records == 0,
              "the task that ended after the close released itself" + how + ": " +
                  std::to_string(m.counts().records) + " records left");
        if (!retire) check(state_of(m, 1, h) == State::None, "and its handle no longer resolves" + how);
    }
}

// Owner cleanup while other threads query: no freed record is ever touched (ASan/TSan).
KILN_TASKS_TEST(retire_vs_queries) {
    for (int round = 0; round < 200; ++round) {
        Model m;
        m.open_owner(1, "a");
        std::vector<KilnHandle> hs;
        for (int i = 0; i < 32; ++i) hs.push_back(submit_ok(m, 1, named("t"), "retire vs queries"));
        kiln::tasks::test::StartLine go(3);
        std::atomic<bool> stop{false};
        std::thread queries([&] {
            go.arrive();
            while (!stop.load()) {
                for (KilnHandle h : hs) {
                    State s;
                    m.status(1, h, &s);
                    m.cancel(1, h, &s);
                    m.release(1, h);
                }
            }
        });
        std::thread executor([&] {
            go.arrive();
            for (int i = 0; i < 200; ++i) drive(m);
        });
        go.arrive();
        uint32_t n = 0;
        m.retire(1, &n);
        executor.join();
        stop = true;
        queries.join();
        drive(m);
        check(m.counts().outstanding == 0 && m.counts().records == 0, "retired and quiescent, round " + std::to_string(round));
        if (kiln::tasks::test::g_failures.load()) break;
    }
}


// ---------------------------------------------------------------------------------------------------------------
// AFTER_STEP is bound to the server session it captured (kiln_tasks.h "AFTER_STEP and sessions").
SubmitArgs after_step(const char* name, uint64_t session, uint64_t edge) {
    SubmitArgs a = named(name);
    a.after_step = true;
    a.session = session;
    a.edge = edge;
    return a;
}

// Runs every ready piece of work; returns the terminal state each done delivery reported, by task.
std::vector<std::pair<KilnHandle, State>> drive_dones(Model& m) {
    std::vector<std::pair<KilnHandle, State>> dones;
    Work w;
    while (m.dequeue(Slot::Normal, &w)) {
        if (w.kind == Work::Kind::Done) {
            dones.emplace_back(w.task, w.final_state);
            m.done_returned(w);
        } else {
            m.segment_ended(w, false, Outcome::Completed);
        }
    }
    return dones;
}

bool done_as(const std::vector<std::pair<KilnHandle, State>>& dones, KilnHandle h, State s) {
    return std::count(dones.begin(), dones.end(), std::make_pair(h, s)) == 1 &&
           std::count_if(dones.begin(), dones.end(), [&](const auto& d) { return d.first == h; }) == 1;
}

KILN_TASKS_TEST(after_step_sessions) {
    {
        Model m;
        m.open_owner(1, "a");
        m.edge(3, 10);
        const KilnHandle same = submit_ok(m, 1, after_step("same", 3, 10), "captured session 3, edge 10");
        m.edge(2, 11);
        m.edge(3, 10);
        check(m.counts().waiting == 1, "an older session's edge and the same edge again release nothing");
        m.edge(3, 11);
        auto dones = drive_dones(m);
        check(done_as(dones, same, State::Completed), "a later edge of the same session wakes it and it runs");
    }
    {
        Model m;
        m.open_owner(1, "a");
        m.edge(3, 10);
        const KilnHandle a = submit_ok(m, 1, after_step("in A", 3, 10), "captured in session 3");
        m.edge(4, 1);  // a later session's edge: session 3 is over
        check(m.counts().waiting == 0, "an edge of another session releases none of session 3's tasks");
        Work w;
        bool body_ran = false;
        auto dones = std::vector<std::pair<KilnHandle, State>>{};
        while (m.dequeue(Slot::Normal, &w)) {
            if (w.kind == Work::Kind::Done) {
                dones.emplace_back(w.task, w.final_state);
                m.done_returned(w);
            } else {
                body_ran |= w.task == a;
                m.segment_ended(w, false, Outcome::Completed);
            }
        }
        check(!body_ran, "a task captured in session A never runs in session B");
        check(done_as(dones, a, State::Cancelled) && state_of(m, 1, a) == State::Cancelled,
              "its done is delivered as Cancelled");
    }
    {
        Model m;
        m.open_owner(1, "a");
        m.edge(5, 7);
        const KilnHandle waiting = submit_ok(m, 1, after_step("waits", 5, 7), "captured session 5");
        const KilnHandle ready = submit_ok(m, 1, after_step("old edge", 5, 6), "captured an older edge of session 5");
        m.end_session(5);
        check(m.counts().waiting == 0, "end_session leaves nothing waiting");
        check(state_of(m, 1, waiting) == State::Cancelled, "a task waiting on the ended session is cancelled");
        check(state_of(m, 1, ready) == State::Queued, "a task already released before the end still runs");
        auto dones = drive_dones(m);
        check(done_as(dones, waiting, State::Cancelled), "the cancelled one's done is delivered as Cancelled");
        check(done_as(dones, ready, State::Completed), "the released one completes");
        m.edge(5, 8);
        m.edge(6, 1);
        check(m.counts().outstanding == 0, "no edge after the end brings anything back");
    }
    {
        Model m;
        m.open_owner(1, "a");
        m.edge(2, 4);
        const KilnHandle late = submit_ok(m, 1, after_step("late", 2, 3), "captured edge 3, session's last is 4");
        Work w;
        check(m.dequeue(Slot::Normal, &w) && w.task == late && w.kind == Work::Kind::Body,
              "lost wakeup: an edge older than its session's last one is ready at once");
        m.segment_ended(w, false, Outcome::Completed);
        drive_dones(m);
        const KilnHandle first = submit_ok(m, 1, after_step("first edge", 9, 0), "a session no edge was seen of yet");
        check(m.counts().waiting == 1, "a session newer than any edge waits for its own first edge");
        m.edge(2, 5);
        check(m.counts().waiting == 1, "a newer edge of the live (older) session does not release it");
        m.edge(9, 1);
        auto dones = drive_dones(m);
        check(done_as(dones, first, State::Completed), "its first edge wakes it");
    }
    {
        Model m;
        m.open_owner(1, "a");
        m.edge(4, 2);
        m.end_session(4);
        const KilnHandle ended = submit_ok(m, 1, after_step("ended", 4, 2), "captured a session that already ended");
        check(state_of(m, 1, ended) == State::Cancelled && m.counts().waiting == 0,
              "a captured session already ended at queueing: cancelled at once, never waiting");
        m.edge(5, 1);
        auto dones = drive_dones(m);
        check(done_as(dones, ended, State::Cancelled), "its done is still delivered, as Cancelled");
        const KilnHandle older = submit_ok(m, 1, after_step("older", 3, 0), "captured an older session than the live one");
        check(state_of(m, 1, older) == State::Cancelled, "an older session is over too: cancelled at once");
        drive_dones(m);
    }
    {
        Model m;
        m.open_owner(1, "a");
        refused(m, 1, after_step("no session", 0, 0), KILN_ERR_INVALID_ARGUMENT, Refusal::Argument,
                "AFTER_STEP without a server session is a malformed request");
        check(m.counts().records == 0, "nothing was accepted");
    }
}

// The capture/queue window forced open: submit holds its captured (session, edge) just before publication while
// another thread moves the session on. The task must be ready, waiting or cancelled exactly as if it had been queued
// after that move, and TSan sees the overlap.
KILN_TASKS_TEST(after_step_session_race) {
    enum class Move { NewerEdge, EndSession, NextSession };
    for (const Move move : {Move::NewerEdge, Move::EndSession, Move::NextSession}) {
        Model m;
        m.open_owner(1, "a");
        m.edge(7, 20);
        kiln::tasks::test::Gate in_window, moved;
        m.set_test_hook([&](Model::Point p) {
            if (p != Model::Point::SubmitBeforePublish) return;
            in_window.open();
            moved.wait();
        });
        KilnHandle h = 0;
        Refusal why = Refusal::None;
        KilnResult r = KILN_ERR_INTERNAL;
        std::thread submitter([&] { r = m.submit(1, after_step("raced", 7, 20), &h, &why); });
        in_window.wait();
        if (move == Move::NewerEdge) m.edge(7, 21);
        else if (move == Move::EndSession) m.end_session(7);
        else m.edge(8, 1);
        moved.open();
        submitter.join();
        m.set_test_hook(nullptr);
        check(r == KILN_OK && h != 0, "accepted");
        Work w;
        if (move == Move::NewerEdge) {
            check(m.counts().waiting == 0 && m.dequeue(Slot::Normal, &w) && w.task == h && w.kind == Work::Kind::Body,
                  "an edge of its session inside the window is not lost: ready at once");
            m.segment_ended(w, false, Outcome::Completed);
        } else {
            check(m.counts().waiting == 0 && state_of(m, 1, h) == State::Cancelled,
                  std::string(move == Move::EndSession ? "its session ended" : "the next session started") +
                      " inside the window: cancelled at once, never carried over");
            m.edge(8, 2);
        }
        drive_dones(m);
        check(m.counts().outstanding == 0, "quiescent");
    }
    // free-running overlap: submitters capture the live (session, edge) while the tick thread moves edges and ends
    // sessions; whatever the interleaving, every accepted task gets exactly one done and nothing waits forever
    Model m;
    m.open_owner(1, "a");
    std::atomic<uint64_t> live{(uint64_t{1} << 32) | 0};  // session << 32 | edge, published by the "tick"
    std::atomic<bool> stop{false};
    std::atomic<int> accepted{0}, dones{0};
    std::thread tick([&] {
        for (uint64_t session = 1; session <= 40; ++session) {
            for (uint64_t edge = 1; edge <= 25; ++edge) {
                // let at least one more submission in between two moves, so captures interleave with every move
                // (bounded: a model that stops accepting fails the final check instead of hanging the test)
                const int seen = accepted.load();
                const auto until = std::chrono::steady_clock::now() + std::chrono::milliseconds(20);
                while (accepted.load() == seen && std::chrono::steady_clock::now() < until) std::this_thread::yield();
                live.store((session << 32) | edge);
                m.edge(session, edge);
            }
            m.end_session(session);
            live.store(((session + 1) << 32) | 0);
        }
        stop = true;
    });
    std::vector<std::thread> submitters;
    for (int t = 0; t < 2; ++t)
        submitters.emplace_back([&] {
            while (!stop.load()) {
                const uint64_t now = live.load();
                KilnHandle h = 0;
                Refusal why = Refusal::None;
                if (m.submit(1, after_step("free", now >> 32, now & 0xFFFFFFFF), &h, &why) == KILN_OK) accepted++;
                Work w;
                while (m.dequeue(Slot::Normal, &w)) {
                    if (w.kind == Work::Kind::Done) {
                        dones++;
                        m.done_returned(w);
                        m.release(1, w.task);
                    } else {
                        m.segment_ended(w, false, Outcome::Completed);
                    }
                }
            }
        });
    tick.join();
    for (auto& t : submitters) t.join();
    m.end_session(41);
    dones += static_cast<int>(drive_dones(m).size());
    check(accepted.load() > 0 && dones.load() == accepted.load() && m.counts().waiting == 0 &&
              m.counts().outstanding == 0,
          "every accepted task got one done (" + std::to_string(accepted.load()) + " accepted, " +
              std::to_string(dones.load()) + " dones), nothing left waiting");
}

// Normal and LongRunning dequeues keep their own round-robin turn. With one
// shared cursor, B's LongRunning lease and C's normal lease resuming in turn kept moving the cursor past A, whose
// ready normal work never ran.
KILN_TASKS_TEST(slot_cursors_independent) {
    Model m;
    m.open_owner(1, "A");
    m.open_owner(2, "B");
    m.open_owner(3, "C");
    const KilnHandle b = submit_ok(m, 2, named("b lease", Priority::Normal), "B");
    SubmitArgs lr = named("b long");
    lr.long_running = true;
    const KilnHandle bl = submit_ok(m, 2, lr, "B LongRunning lease");
    Work w;
    // drain B's normal task first so only the leases and A's work matter
    check(m.dequeue(Slot::Normal, &w) && w.task == b, "B's normal task");
    m.segment_ended(w, false, Outcome::Completed);
    check(m.dequeue(Slot::Normal, &w) && w.kind == Work::Kind::Done, "its done");
    m.done_returned(w);
    const KilnHandle cl = submit_ok(m, 3, named("c lease"), "C normal lease");
    // one LongRunning segment of B: the cursor moves past A
    check(m.dequeue(Slot::LongRunning, &w) && w.task == bl, "B's LongRunning lease runs");
    uint64_t reg = 0;
    m.arm(bl, &reg);
    m.segment_ended(w, true, Outcome::Completed);
    m.resume(bl, reg);
    check(m.dequeue(Slot::Normal, &w) && w.task == cl, "C's lease runs");
    m.arm(cl, &reg);
    m.segment_ended(w, true, Outcome::Completed);
    m.resume(cl, reg);
    const KilnHandle a = submit_ok(m, 1, named("a work"), "A's ready normal work");
    bool a_served = false;
    for (int round = 0; round < 6 && !a_served; ++round) {
        if (m.dequeue(Slot::LongRunning, &w)) {  // B's lease segment, suspends and resumes again
            m.arm(w.task, &reg);
            m.segment_ended(w, true, Outcome::Completed);
            m.resume(w.task, reg);
        }
        if (m.dequeue(Slot::Normal, &w)) {
            if (w.task == a) {
                a_served = true;
                m.segment_ended(w, false, Outcome::Completed);
            } else {  // C's lease segment, suspends and resumes again
                m.arm(w.task, &reg);
                m.segment_ended(w, true, Outcome::Completed);
                m.resume(w.task, reg);
            }
        }
    }
    check(a_served, "A's normal work is served while B's LongRunning and C's normal leases keep resuming");
}

// a submission is all or nothing. An allocation failing while the record is being published
// (here: thrown at the point between the owner's table and the queue) leaves no record, no outstanding count and no
// queued work, and the owner keeps working.
KILN_TASKS_TEST(submit_is_transactional) {
    for (const bool after_step : {false, true}) {
        Model m;
        m.open_owner(1, "a");
        m.set_test_hook([](Model::Point p) {
            if (p == Model::Point::SubmitPublishing) throw std::bad_alloc();
        });
        SubmitArgs a = named("fails");
        a.after_step = after_step;
        a.session = 1;
        KilnHandle h = 0;
        Refusal why = Refusal::None;
        bool threw = false;
        try {
            m.submit(1, a, &h, &why);
        } catch (const std::bad_alloc&) {
            threw = true;
        }
        m.set_test_hook(nullptr);
        const Counts c = m.counts();
        check(threw && c.records == 0 && c.outstanding == 0 && c.ready == 0 && c.waiting == 0,
              std::string("a failed publication leaves nothing") + (after_step ? " (AFTER_STEP)" : ""));
        const KilnHandle ok = submit_ok(m, 1, named("next"), "the owner submits again");
        Work w;
        check(m.dequeue(Slot::Normal, &w) && w.task == ok, "and that task is the only work");
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Managed leases (kiln.internal.tasks.host): the outcome comes from finish(), inside the running segment, once.
SubmitArgs lease_args(const char* name) {
    SubmitArgs a = named(name);
    a.lease = true;
    return a;
}

// Runs one segment of `task`: `inside` runs while it is "running" (as the host's dispatcher would).
template <class F>
bool run_segment(Model& m, KilnHandle task, bool* suspended, F inside) {
    Work w;
    if (!m.dequeue(Slot::Normal, &w) || w.task != task || (w.kind != Work::Kind::Body && w.kind != Work::Kind::Continuation))
        return false;
    *suspended = inside();
    m.segment_ended(w, *suspended, Outcome::Completed);  // what the pool passes for an ordinary return
    return true;
}

State done_state(Model& m, KilnHandle task) {
    Work w;
    while (m.dequeue(Slot::Normal, &w)) {
        if (w.kind == Work::Kind::Done && w.task == task) {
            const State s = w.final_state;
            m.done_returned(w);
            return s;
        }
        if (w.kind == Work::Kind::Done) m.done_returned(w);
        else m.segment_ended(w, false, Outcome::Completed);
    }
    return State::None;
}

KILN_TASKS_TEST(lease_outcome_from_finish) {
    for (const auto& [outcome, cancel, want] :
         {std::tuple{Outcome::Completed, false, State::Completed}, std::tuple{Outcome::Completed, true, State::Completed},
          std::tuple{Outcome::Faulted, false, State::Faulted},
          std::tuple{Outcome::OwnTokenCancelled, true, State::Cancelled},
          std::tuple{Outcome::OwnTokenCancelled, false, State::Faulted}}) {
        Model m;
        m.open_owner(1, "a");
        const KilnHandle h = submit_ok(m, 1, lease_args("lease"), "a lease");
        bool suspended = true;
        check(run_segment(m, h, &suspended, [&] {
            uint64_t reg = 0;
            check(m.arm(h, &reg) == KILN_OK, "the first segment arms");
            return true;
        }) && suspended, "the first segment suspended");
        if (cancel) {
            State st;
            m.cancel(1, h, &st);
        }
        uint64_t reg = 0;
        (void)reg;
        // resume the armed registration (the first one is 1)
        check(m.resume(h, 1) == KILN_OK, "resumed");
        check(run_segment(m, h, &suspended, [&] {
            check(m.cancel_requested(h) == cancel, "the lease's cancellation state is visible to its segment");
            check(m.finish(h, outcome) == KILN_OK, "finish inside the running segment");
            check(m.finish(h, outcome) == KILN_ERR_INTERNAL, "a second finish is INTERNAL");
            return false;
        }), "the continuation ran");
        check(done_state(m, h) == want, "the published state follows finish's outcome with the usual precedence");
    }
    {
        Model m;
        m.open_owner(1, "a");
        const KilnHandle h = submit_ok(m, 1, lease_args("no end"), "a lease");
        check(m.finish(h, Outcome::Completed) == KILN_ERR_INTERNAL, "finish outside a running segment: INTERNAL");
        bool suspended = false;
        Work w;
        check(m.dequeue(Slot::Normal, &w) && w.task == h, "its segment runs");
        check(m.segment_ended(w, false, Outcome::Completed) == KILN_ERR_INTERNAL,
              "a lease segment that returns neither armed nor finished: INTERNAL");
        (void)suspended;
        check(done_state(m, h) == State::Faulted, "...and the lease is Faulted (its done still delivered)");
    }
    {
        Model m;
        m.open_owner(1, "a");
        const KilnHandle h = submit_ok(m, 1, named("native"), "a native task");
        Work w;
        m.dequeue(Slot::Normal, &w);
        check(m.finish(h, Outcome::Completed) == KILN_ERR_INTERNAL, "finish is for leases only");
        m.segment_ended(w, false, Outcome::Completed);
    }
}

KILN_TASKS_TEST(lease_resume_after_step) {
    Model m;
    m.open_owner(1, "a");
    m.edge(3, 10);
    const KilnHandle h = submit_ok(m, 1, lease_args("waits a step"), "a lease");
    bool suspended = false;
    // armed and resume_after inside the running segment: parked until the segment returns
    run_segment(m, h, &suspended, [&] {
        uint64_t reg = 0;
        m.arm(h, &reg);
        check(m.resume_after(h, reg, 3, 10) == KILN_OK, "resume_after inside the arming segment");
        return true;
    });
    Work w;
    check(!m.dequeue(Slot::Normal, &w) && m.counts().waiting == 1, "it waits for a later edge, not runnable now");
    m.edge(3, 11);
    check(run_segment(m, h, &suspended, [&] {
        check(!m.cancel_requested(h), "a normal wake: not cancelled");
        uint64_t reg = 0;
        m.arm(h, &reg);
        m.resume_after(h, reg, 3, 11);
        return true;
    }), "the next edge of its session resumes it");
    m.end_session(3);
    check(run_segment(m, h, &suspended, [&] {
        check(m.cancel_requested(h), "its session ended: resumed with the cancellation set");
        m.finish(h, Outcome::OwnTokenCancelled);
        return false;
    }), "the session's end resumes it at once (never carried over)");
    check(done_state(m, h) == State::Cancelled, "and it ends Cancelled");
    // a waiting continuation is released (cancelled) by a cancel, a seal and a retire
    for (int how = 0; how < 3; ++how) {
        Model n;
        n.open_owner(1, "a");
        n.edge(1, 1);
        const KilnHandle l = submit_ok(n, 1, lease_args("waiting"), "a lease");
        Work x;
        n.dequeue(Slot::Normal, &x);
        uint64_t reg = 0;
        n.arm(l, &reg);
        n.segment_ended(x, true, Outcome::Completed);
        n.resume_after(l, reg, 1, 1);
        check(n.counts().waiting == 1, "a suspended lease waits for its edge");
        State st;
        uint32_t left = 0;
        if (how == 0) n.cancel(1, l, &st);
        else if (how == 1) n.seal(1, &left);
        else n.retire(1, &left);
        check(n.counts().waiting == 0 && n.dequeue(Slot::Normal, &x) && x.task == l && x.kind == Work::Kind::Continuation &&
                  x.cancel_requested(),
              std::string("its continuation is released with the cancellation set by a ") +
                  (how == 0 ? "cancel" : how == 1 ? "seal" : "retire"));
        n.finish(l, Outcome::OwnTokenCancelled);
        n.segment_ended(x, false, Outcome::Completed);
    }
}

KILN_TASKS_TEST(lease_hops_hold_quiescence) {
    Model m;
    m.open_owner(1, "a");
    const KilnHandle h = submit_ok(m, 1, lease_args("hops"), "a lease");
    Work w;
    m.dequeue(Slot::Normal, &w);
    check(m.hop_begin(h) == KILN_OK, "a hop is pending");
    m.finish(h, Outcome::Completed);
    m.segment_ended(w, false, Outcome::Completed);
    Work d;
    check(!m.dequeue(Slot::Normal, &d), "finished, but its done is held back while a hop is pending");
    uint32_t left = 0;
    m.outstanding(1, &left);
    check(left == 1, "not quiescent while a hop is pending");
    check(m.hop_end(h) == KILN_OK && m.dequeue(Slot::Normal, &d) && d.kind == Work::Kind::Done && d.task == h,
          "the last hop's end releases the done");
    m.done_returned(d);
    m.outstanding(1, &left);
    check(left == 0 && m.hop_end(h) == KILN_ERR_INTERNAL && m.hop_begin(h) == KILN_ERR_INTERNAL,
          "quiescent; no hop on a quiescent lease");
}

// a done held back by a pending hop is never lost when its record would leave the owner's
// table (a seal or a retire before the lease ends, or a release after it ended): the last hop_end still queues it.
KILN_TASKS_TEST(held_done_survives_seal_retire_release) {
    for (int how = 0; how < 3; ++how) {
        const std::string t = how == 0 ? "seal then finish: " : how == 1 ? "retire then finish: " : "finish then release: ";
        Model m;
        m.open_owner(1, "a");
        const KilnHandle h = submit_ok(m, 1, lease_args("held"), "a lease");
        Work w;
        m.dequeue(Slot::Normal, &w);
        check(m.hop_begin(h) == KILN_OK, t + "a hop is pending");
        uint32_t left = 0;
        if (how == 0) m.seal(1, &left);
        if (how == 1) m.retire(1, &left);
        m.finish(h, Outcome::Completed);
        m.segment_ended(w, false, Outcome::Completed);
        if (how == 2) check(m.release(1, h) == KILN_ERR_UNAVAILABLE, t + "a lease whose done is held is not released");
        check(m.hop_end(h) == KILN_OK, t + "the last hop_end finds the record");
        Work d;
        check(m.dequeue(Slot::Normal, &d) && d.kind == Work::Kind::Done && d.task == h, t + "and queues its done");
        m.done_returned(d);
        check(m.counts().outstanding == 0, t + "quiescent");
        if (how == 2) check(m.release(1, h) == KILN_OK, t + "then it is released");
    }
}

// at its session's end, a continuation released by that session's edge but not yet started,
// and a segment running from it, see their cancellation.
KILN_TASKS_TEST(session_end_cancels_released_continuations) {
    Model m;
    m.open_owner(1, "a");
    m.edge(2, 5);
    const KilnHandle a = submit_ok(m, 1, lease_args("ready"), "lease a");
    const KilnHandle b = submit_ok(m, 1, lease_args("running"), "lease b");
    Work wa, wb;
    m.dequeue(Slot::Normal, &wa);
    m.dequeue(Slot::Normal, &wb);
    uint64_t ra = 0, rb = 0;
    m.arm(a, &ra);
    m.arm(b, &rb);
    m.segment_ended(wa, true, Outcome::Completed);
    m.segment_ended(wb, true, Outcome::Completed);
    m.resume_after(a, ra, 2, 5);
    m.resume_after(b, rb, 2, 5);
    m.edge(2, 6);  // both continuations are released by session 2's edge
    Work rb_run;
    check(m.dequeue(Slot::Normal, &rb_run) && rb_run.task == a, "a continuation starts (a)");
    check(!rb_run.cancel_requested(), "...not cancelled yet");
    m.end_session(2);  // b's continuation is ready, not started; a's segment runs
    check(rb_run.cancel_requested(), "the running segment resumed by the ended session is signalled");
    Work wb2;
    check(m.dequeue(Slot::Normal, &wb2) && wb2.task == b && wb2.cancel_requested(),
          "the released, not yet started continuation runs with its cancellation set");
    m.finish(a, Outcome::OwnTokenCancelled);
    m.segment_ended(rb_run, false, Outcome::Completed);
    m.finish(b, Outcome::OwnTokenCancelled);
    m.segment_ended(wb2, false, Outcome::Completed);
}

// a segment that finished cannot arm a resumption.
KILN_TASKS_TEST(arm_after_finish_refused) {
    Model m;
    m.open_owner(1, "a");
    const KilnHandle h = submit_ok(m, 1, lease_args("ends"), "a lease");
    Work w;
    m.dequeue(Slot::Normal, &w);
    check(m.finish(h, Outcome::Completed) == KILN_OK, "finished");
    uint64_t reg = 0;
    check(m.arm(h, &reg) == KILN_ERR_INTERNAL, "arm after finish in the same segment is INTERNAL");
    m.segment_ended(w, false, Outcome::Completed);
}

// resume_after inside the arming segment, with its session's newer edge already seen: the continuation is runnable,
// but only once the arming segment returned (segments of one lease never overlap).
KILN_TASKS_TEST(resume_after_never_overlaps) {
    Model m;
    m.open_owner(1, "a");
    m.edge(1, 9);
    const KilnHandle h = submit_ok(m, 1, lease_args("no overlap"), "a lease");
    Work w;
    m.dequeue(Slot::Normal, &w);
    uint64_t reg = 0;
    m.arm(h, &reg);
    check(m.resume_after(h, reg, 1, 3) == KILN_OK, "resume_after with an edge older than the session's last");
    Work x;
    check(!m.dequeue(Slot::Normal, &x), "nothing of it is runnable while the arming segment still runs");
    m.segment_ended(w, true, Outcome::Completed);
    check(m.dequeue(Slot::Normal, &x) && x.task == h && x.kind == Work::Kind::Continuation, "runnable once it returned");
    m.finish(h, Outcome::Completed);
    m.segment_ended(x, false, Outcome::Completed);
}

// ---------------------------------------------------------------------------------------------------------------
// Client-world hops (#82): the server and the client world each have their own session line. An edge or an end of
// one world never releases or cancels what waits on the other, and a new server session does not end a client one.
SubmitArgs after_step_in(const char* name, uint32_t world, uint64_t session, uint64_t edge) {
    SubmitArgs a = after_step(name, session, edge);
    a.world = world;
    return a;
}

KILN_TASKS_TEST(world_session_lines_are_independent) {
    {
        Model m;
        m.open_owner(1, "a");
        m.edge(KILN_WORLD_CLIENT, 2, 4);
        m.edge(KILN_WORLD_SERVER, 1, 9);
        const KilnHandle client = submit_ok(m, 1, after_step_in("client", KILN_WORLD_CLIENT, 2, 4), "client session 2");
        const KilnHandle server = submit_ok(m, 1, after_step_in("server", KILN_WORLD_SERVER, 1, 9), "server session 1");
        m.edge(KILN_WORLD_SERVER, 5, 1);  // a newer server session: server session 1 is over, client session 2 is not
        check(state_of(m, 1, server) == State::Cancelled, "a newer server session ends the server task's session");
        check(state_of(m, 1, client) == State::Queued && m.counts().waiting == 1,
              "it does not end a client session (red with one session line for both)");
        m.end_session(KILN_WORLD_SERVER, 5);
        check(m.counts().waiting == 1, "nor does a server session's end");
        m.edge(KILN_WORLD_SERVER, 6, 1);
        check(m.counts().waiting == 1, "nor a server edge of any session");
        m.edge(KILN_WORLD_CLIENT, 2, 5);
        auto dones = drive_dones(m);
        check(done_as(dones, client, State::Completed), "the client world's own next edge wakes it");
        check(done_as(dones, server, State::Cancelled), "the server task's done says Cancelled");
    }
    {
        Model m;
        m.open_owner(1, "a");
        const KilnHandle server = submit_ok(m, 1, after_step_in("server", KILN_WORLD_SERVER, 3, 0), "server session 3");
        const KilnHandle client = submit_ok(m, 1, after_step_in("client", KILN_WORLD_CLIENT, 3, 0), "client session 3");
        m.end_session(KILN_WORLD_CLIENT, 3);  // a hosting client leaves: the client world stops first
        check(state_of(m, 1, client) == State::Cancelled && state_of(m, 1, server) == State::Queued,
              "the client world's end cancels only what waits on it");
        m.edge(KILN_WORLD_CLIENT, 4, 1);
        check(state_of(m, 1, server) == State::Queued, "a new client session's edge does not wake a server task");
        m.edge(KILN_WORLD_SERVER, 3, 1);
        auto dones = drive_dones(m);
        check(done_as(dones, server, State::Completed) && done_as(dones, client, State::Cancelled),
              "the server world's edge wakes the server task");
    }
    {
        Model m;
        m.open_owner(1, "a");
        refused(m, 1, after_step_in("prediction", KILN_WORLD_PREDICTION, 1, 0), KILN_ERR_INVALID_ARGUMENT,
                Refusal::Argument, "the prediction world has no session line: a malformed request");
        refused(m, 1, after_step_in("unknown", 7, 1, 0), KILN_ERR_INVALID_ARGUMENT, Refusal::Argument,
                "an unknown world kind: a malformed request");
        m.edge(KILN_WORLD_PREDICTION, 1, 1);  // ignored
        m.end_session(KILN_WORLD_PREDICTION, 1);
        check(m.counts().records == 0, "nothing was accepted");
    }
}

KILN_TASKS_TEST(lease_resume_after_step_per_world) {
    Model m;
    m.open_owner(1, "a");
    m.edge(KILN_WORLD_CLIENT, 1, 3);
    m.edge(KILN_WORLD_SERVER, 1, 3);
    const KilnHandle h = submit_ok(m, 1, lease_args("client window"), "a lease");
    bool suspended = false;
    run_segment(m, h, &suspended, [&] {
        uint64_t reg = 0;
        m.arm(h, &reg);
        check(m.resume_after(h, reg, KILN_WORLD_CLIENT, 1, 3) == KILN_OK, "a client-world window");
        return true;
    });
    m.edge(KILN_WORLD_SERVER, 1, 4);
    Work w;
    check(!m.dequeue(Slot::Normal, &w) && m.counts().waiting == 1, "a server edge does not wake a client window");
    m.edge(KILN_WORLD_CLIENT, 1, 4);
    check(run_segment(m, h, &suspended, [&] {
        check(!m.cancel_requested(h), "woken by the client edge, not cancelled");
        uint64_t reg = 0;
        m.arm(h, &reg);
        m.resume_after(h, reg, KILN_WORLD_CLIENT, 1, 4);
        return true;
    }), "the client world's next edge resumes it");
    m.end_session(KILN_WORLD_SERVER, 1);
    check(m.counts().waiting == 1, "the server world's end leaves a client window waiting");
    m.end_session(KILN_WORLD_CLIENT, 1);
    check(run_segment(m, h, &suspended, [&] {
        check(m.cancel_requested(h), "its client session ended: resumed with the cancellation set");
        m.finish(h, Outcome::OwnTokenCancelled);
        return false;
    }), "the client session's end resumes it at once");
    check(done_state(m, h) == State::Cancelled, "and it ends Cancelled");
    uint64_t reg = 0;
    check(m.resume_after(h, reg, KILN_WORLD_PREDICTION, 1, 1) == KILN_ERR_INTERNAL, "no window in the prediction world");
}

// A continuation released by a client edge is signalled by its client session's end, never by the server world's.
KILN_TASKS_TEST(released_continuation_follows_its_own_world) {
    Model m;
    m.open_owner(1, "a");
    m.edge(KILN_WORLD_CLIENT, 2, 1);
    const KilnHandle a = submit_ok(m, 1, lease_args("released"), "a lease");
    Work w;
    m.dequeue(Slot::Normal, &w);
    uint64_t reg = 0;
    m.arm(a, &reg);
    m.segment_ended(w, true, Outcome::Completed);
    m.resume_after(a, reg, KILN_WORLD_CLIENT, 2, 1);
    m.edge(KILN_WORLD_CLIENT, 2, 2);
    Work run;
    check(m.dequeue(Slot::Normal, &run) && run.task == a && !run.cancel_requested(), "released by the client edge");
    m.end_session(KILN_WORLD_SERVER, 2);  // the same number, the other world
    check(!run.cancel_requested(), "a server session's end does not signal a client continuation");
    m.end_session(KILN_WORLD_CLIENT, 2);
    check(run.cancel_requested(), "its own client session's end does");
    m.finish(a, Outcome::OwnTokenCancelled);
    m.segment_ended(run, false, Outcome::Completed);
}
}  // namespace

int main(int argc, char** argv) { return kiln::tasks::test::run(argc, argv, "tasks model"); }
