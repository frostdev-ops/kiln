// SPDX-License-Identifier: MIT
// kiln.tasks' lifecycle edges through the binding (src/tasks.cpp Service): the live server session snapshot that
// AFTER_STEP captures whole, Post edges (KilnTickInfo.step, strictly increasing, never 0), the session's STOPPING,
// no server world, and the process exit. Over the host test platform; built twice by test-tasks.sh (ASan/UBSan, TSan).
#include <atomic>
#include <chrono>
#include <cstring>
#include <mutex>
#include <set>
#include <string>
#include <thread>

#include "tasks.h"
#include "tasks_platform_host.h"
#include "tasks_test_support.h"

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
    bool owner_alive(KilnOwner owner) override { return owner != 0; }
    std::string owner_name(KilnOwner owner) override { return "mod." + std::to_string(owner); }
};

struct Job {
    std::atomic<int> body_runs{0}, done_calls{0};
    std::atomic<uint32_t> done_state{0}, saw_cancel{0};
    Gate release;
    bool block = false;
};

void KILN_CALL body(const KilnTaskRun* run, void* user) {
    auto* j = static_cast<Job*>(user);
    j->body_runs.fetch_add(1);
    if (j->block) j->release.wait();
    j->saw_cancel.store(run->cancelled(run));
}
void KILN_CALL done(KilnHandle, uint32_t state, void* user) {
    auto* j = static_cast<Job*>(user);
    j->done_state.store(state);
    j->done_calls.fetch_add(1);
}

template <class F>
bool until(F pred, int ms = 5000) {
    for (int i = 0; i < ms * 10 && !pred(); ++i) std::this_thread::sleep_for(std::chrono::microseconds(100));
    return pred();
}

KilnTaskInfo after_step(const char* name) {
    KilnTaskInfo i{};
    i.size = sizeof(KilnTaskInfo);
    i.flags = KILN_TASK_AFTER_STEP;
    i.name = {name, std::strlen(name)};
    return i;
}

PoolConfig config() {
    PoolConfig c;
    c.normal_workers = 2;
    c.long_running_workers = 1;
    return c;
}

KILN_TASKS_TEST(no_server_world) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const KilnTaskInfo i = after_step("needs a world");
    Job j;
    KilnHandle h = 9;
    uint32_t why = 0;
    check(s.submit(1, &i, &body, &done, &j, &h, &why) == KILN_ERR_UNAVAILABLE && why == KILN_TASK_REFUSAL_NO_SERVER_WORLD &&
              h == 0,
          "before any server session: UNAVAILABLE + NO_SERVER_WORLD");
    s.session_started(1);
    s.session_stopping(1);
    h = 9;
    check(s.submit(1, &i, &body, &done, &j, &h, &why) == KILN_ERR_UNAVAILABLE && why == KILN_TASK_REFUSAL_NO_SERVER_WORLD,
          "after the session stopped (main menu): NO_SERVER_WORLD");
    KilnTaskInfo plain = i;
    plain.flags = 0;
    j.release.open();
    check(s.submit(1, &plain, &body, &done, &j, &h, nullptr) == KILN_OK, "a plain task needs no world");
    check(until([&] { return j.done_calls.load() == 1; }), "and runs");
}

KILN_TASKS_TEST(after_step_runs_after_the_next_edge) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    s.session_started(1);
    check(s.post_edge(1, 10), "edge 10 of session 1");
    const KilnTaskInfo i = after_step("next step");
    Job j;
    KilnHandle h = 0;
    check(s.submit(1, &i, &body, &done, &j, &h, nullptr) == KILN_OK, "AFTER_STEP accepted in a live session");
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
    check(j.body_runs.load() == 0, "it does not run before a later Post edge");
    check(s.post_edge(1, 11), "edge 11");
    check(until([&] { return j.done_calls.load() == 1; }) && j.done_state.load() == KILN_TASK_COMPLETED,
          "the next edge of its session runs it");
    // a task accepted before the session's first edge waits for that first edge
    s.session_started(2);
    Job k;
    KilnHandle hk = 0;
    s.submit(1, &i, &body, &done, &k, &hk, nullptr);
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
    check(k.body_runs.load() == 0, "accepted before session 2's first edge: waits");
    check(s.post_edge(2, 12), "session 2's first edge");
    check(until([&] { return k.done_calls.load() == 1; }) && k.done_state.load() == KILN_TASK_COMPLETED, "runs");
}

KILN_TASKS_TEST(stopping_cancels_never_carries_over) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    s.session_started(1);
    s.post_edge(1, 5);
    const KilnTaskInfo i = after_step("in session 1");
    Job j;
    KilnHandle h = 0;
    s.submit(1, &i, &body, &done, &j, &h, nullptr);
    s.session_stopping(1);
    check(until([&] { return j.done_calls.load() == 1; }) && j.done_state.load() == KILN_TASK_CANCELLED,
          "the session's STOPPING cancels it; its done says CANCELLED");
    s.session_started(2);
    s.post_edge(2, 6);
    s.post_edge(2, 7);
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
    check(j.body_runs.load() == 0, "it never runs in the next world");
}

// Note B: edges are KilnTickInfo.step: > 0, strictly increasing within the live session; anything else is refused.
KILN_TASKS_TEST(edges_validated) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    check(!s.post_edge(1, 1), "no live session: refused");
    s.session_started(1);
    check(!s.post_edge(1, 0), "step 0: refused");
    check(s.post_edge(1, 10), "step 10 accepted");
    check(!s.post_edge(1, 10) && !s.post_edge(1, 9), "a repeated or older step: refused");
    check(!s.post_edge(2, 11), "an edge of a session that is not the live one: refused");
    check(s.refused_edges() == 5 && s.snapshot().edge == 10 && s.snapshot().session == 1 && s.snapshot().live,
          "refusals counted; the snapshot is unchanged");
}

// Note A: AFTER_STEP captures (session, edge, live) as ONE snapshot. The tick moves through sessions whose edges are
// in disjoint ranges (session s: s*1000+1 ..); a reader must never see an edge of another session than its session.
KILN_TASKS_TEST(snapshot_is_consistent) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    std::atomic<bool> stop{false};
    std::atomic<uint64_t> torn{0}, reads{0};
    std::thread tick([&] {
        for (uint32_t session = 1; session <= 200; ++session) {
            // let the reader in between two sessions (bounded), so reads overlap every transition
            const uint64_t seen = reads.load();
            const auto until_ = std::chrono::steady_clock::now() + std::chrono::milliseconds(20);
            while (reads.load() == seen && std::chrono::steady_clock::now() < until_) std::this_thread::yield();
            s.session_started(session);
            for (uint64_t e = 1; e <= 20; ++e) s.post_edge(session, uint64_t{session} * 1000 + e);
            s.session_stopping(session);
        }
        stop = true;
    });
    std::thread reader([&] {
        while (!stop.load()) {
            const Service::Snapshot snap = s.snapshot();
            reads++;
            if (snap.edge != 0 && snap.edge / 1000 != snap.session) torn++;
        }
    });
    tick.join();
    reader.join();
    check(reads.load() > 0 && torn.load() == 0,
          "no snapshot pairs a session with another session's edge (" + std::to_string(torn.load()) + " torn of " +
              std::to_string(reads.load()) + ")");
}

// The capture window forced open: a task that captured (session 1, edge 10) while the tick stops session 1 and starts
// session 2 is cancelled at once; one whose window saw only a newer edge of its own session is ready at once.
KILN_TASKS_TEST(capture_window) {
    for (const bool next_session : {true, false}) {
        HostPlatform p;
        Env env;
        Pool pool(config(), &p);
        Service s(pool, env);
        s.session_started(1);
        s.post_edge(1, 10);
        s.set_test_hook([&](Service::Point at) {
            if (at != Service::Point::AfterCapture) return;
            if (next_session) {
                s.session_stopping(1);
                s.session_started(2);
                s.post_edge(2, 11);
            } else {
                s.post_edge(1, 11);
            }
        });
        const KilnTaskInfo i = after_step("raced");
        Job j;
        KilnHandle h = 0;
        check(s.submit(1, &i, &body, &done, &j, &h, nullptr) == KILN_OK, "accepted");
        s.set_test_hook(nullptr);
        if (next_session) {
            check(until([&] { return j.done_calls.load() == 1; }) && j.done_state.load() == KILN_TASK_CANCELLED &&
                      j.body_runs.load() == 0,
                  "its session ended inside the window: cancelled at once, never run in session 2");
        } else {
            check(until([&] { return j.done_calls.load() == 1; }) && j.done_state.load() == KILN_TASK_COMPLETED,
                  "a newer edge of its own session inside the window is not lost: it runs");
        }
    }
}

KILN_TASKS_TEST(exit_signals_and_refuses) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    s.session_started(1);
    s.post_edge(1, 1);
    KilnTaskInfo plain = after_step("running");
    plain.flags = 0;
    Job running;
    running.block = true;
    KilnHandle hr = 0;
    s.submit(1, &plain, &body, &done, &running, &hr, nullptr);
    check(until([&] { return running.body_runs.load() == 1; }), "a body runs");
    const KilnTaskInfo waiting_info = after_step("waiting");
    Job waiting;
    KilnHandle hw = 0;
    s.submit(2, &waiting_info, &body, &done, &waiting, &hw, nullptr);
    s.exiting();
    uint32_t why = 0;
    KilnHandle h = 1;
    Job late;
    check(s.submit(1, &plain, &body, &done, &late, &h, &why) == KILN_ERR_UNAVAILABLE && why == KILN_TASK_REFUSAL_SHUTDOWN,
          "after exiting(): SHUTDOWN");
    running.release.open();
    check(until([&] { return running.done_calls.load() == 1; }) && running.saw_cancel.load() == 1,
          "the running body was signalled");
    check(until([&] { return waiting.done_calls.load() == 1; }) && waiting.done_state.load() == KILN_TASK_CANCELLED,
          "a waiting AFTER_STEP task was cancelled (its done ran here; at a real exit it may not)");
}

// A plugin's retry loop (a body that resubmits itself AFTER_STEP until its work is done: the native way to wait for a
// later step) meets its owner's drain, as an unload does. The copy waiting for its edge is cancelled (its done says
// CANCELLED); the running body sees cancelled(), and its own resubmit, made after its check, is refused OWNER_SEALED.
// That refusal is the drain's answer, not a fault: the body returns as usual (COMPLETED), nothing faults or is
// quarantined, and the owner is quiescent at once after.
struct Retry {
    Service* s = nullptr;
    Gate release;
    Job spare;  // the user of a resubmit the drain should have refused (never accepted)
    std::atomic<int> done_calls{0};
    std::atomic<uint32_t> done_state{0}, saw_cancel{0}, why{99};
    std::atomic<KilnResult> resubmit{KILN_OK};
    std::atomic<KilnHandle> next{77};
};
void KILN_CALL retry_body(const KilnTaskRun* run, void* user) {
    auto* r = static_cast<Retry*>(user);
    r->release.wait();  // the drain lands here, after the round's check of cancelled()
    r->saw_cancel.store(run->cancelled(run));
    const KilnTaskInfo i = after_step("retry");
    KilnHandle h = 77;
    uint32_t why = 99;
    r->resubmit.store(r->s->submit(run->owner, &i, &body, &done, &r->spare, &h, &why));
    r->why.store(why);
    r->next.store(h);
}
void KILN_CALL retry_done(KilnHandle, uint32_t state, void* user) {
    auto* r = static_cast<Retry*>(user);
    r->done_state.store(state);
    r->done_calls.fetch_add(1);
}

KILN_TASKS_TEST(drain_meets_an_after_step_retry_quietly) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    s.session_started(1);
    s.post_edge(1, 1);
    const KilnTaskInfo waits = after_step("retry");
    Job waiting;
    KilnHandle hw = 0;
    check(s.submit(1, &waits, &body, &done, &waiting, &hw, nullptr) == KILN_OK, "a retry waits for its next edge");
    KilnTaskInfo plain = after_step("retry");
    plain.flags = 0;
    Retry running;
    running.s = &s;
    KilnHandle hr = 0;
    check(s.submit(1, &plain, &retry_body, &retry_done, &running, &hr, nullptr) == KILN_OK, "a retry runs");
    check(until([&] {
              uint32_t st = 0;
              s.status(1, hr, &st);
              return st == KILN_TASK_RUNNING;
          }),
          "its body is running");
    uint32_t left = 0;
    check(s.drain_owner(1, 0, &left) == KILN_OK && left >= 1, "the owner's drain: sealed, the running body outstanding");
    check(until([&] { return waiting.done_calls.load() == 1; }) && waiting.done_state.load() == KILN_TASK_CANCELLED &&
              waiting.body_runs.load() == 0,
          "the retry waiting for its edge is cancelled, its done says CANCELLED, its body never runs");
    running.release.open();
    check(until([&] { return running.done_calls.load() == 1; }), "the running retry returns");
    check(running.saw_cancel.load() == 1, "it saw cancelled()");
    check(running.resubmit.load() == KILN_ERR_UNAVAILABLE && running.why.load() == KILN_TASK_REFUSAL_OWNER_SEALED &&
              running.next.load() == 0 && running.spare.body_runs.load() == 0,
          "its resubmit is refused UNAVAILABLE + OWNER_SEALED and nothing was accepted (got " +
              std::to_string(running.resubmit.load()) + "/" + std::to_string(running.why.load()) + ")");
    check(running.done_state.load() == KILN_TASK_COMPLETED, "the refusal is no fault: the body's done says COMPLETED");
    const kiln::tasks_stats::Totals t = pool.stats_store().totals(0);
    check(!t.unavailable && t.faulted == 0 && t.faults == 0, "no fault counted, no fault recorded");
    check(s.drain_owner(1, 5000, &left) == KILN_OK && left == 0, "the owner is quiescent");
    Job other;
    KilnHandle ho = 0;
    check(s.submit(2, &plain, &body, &done, &other, &ho, nullptr) == KILN_OK &&
              until([&] { return other.done_calls.load() == 1; }),
          "another owner's tasks run on: nothing was quarantined");
}


// ---- submit_in (#82): a task bound to a world's session; AFTER_STEP follows that world's edges ----
KILN_TASKS_TEST(submit_in_binds_a_world_session) {
    HostPlatform p;
    Env env;
    Pool pool(config(), &p);
    Service s(pool, env);
    const KilnTaskInfo i = after_step("client step");
    KilnTaskInfo plain = i;
    plain.flags = 0;
    Job none;
    KilnHandle h = 9;
    uint32_t why = 0;
    check(s.submit_in(1, &i, KILN_WORLD_CLIENT, 0, &body, &done, &none, &h, &why) == KILN_ERR_UNAVAILABLE &&
              why == KILN_TASK_REFUSAL_NO_WORLD && h == 0,
          "no client world: UNAVAILABLE + NO_WORLD");
    check(s.submit_in(1, &plain, KILN_WORLD_CLIENT, 0, &body, &done, &none, &h, &why) == KILN_ERR_UNAVAILABLE &&
              why == KILN_TASK_REFUSAL_NO_WORLD,
          "a plain task bound to a world that is not there: NO_WORLD too");
    check(s.submit_in(1, &i, KILN_WORLD_PREDICTION, 0, &body, &done, &none, &h, &why) == KILN_ERR_INVALID_ARGUMENT &&
              why == KILN_TASK_REFUSAL_ARGUMENT,
          "the prediction world: ARGUMENT");
    s.session_started(KILN_WORLD_SERVER, 1);
    s.session_started(KILN_WORLD_CLIENT, 4);
    s.post_edge(KILN_WORLD_CLIENT, 4, 10);
    check(s.submit_in(1, &i, KILN_WORLD_CLIENT, 3, &body, &done, &none, &h, &why) == KILN_ERR_UNAVAILABLE &&
              why == KILN_TASK_REFUSAL_NO_WORLD,
          "a session that is not the live one: NO_WORLD");
    Job j;
    check(s.submit_in(1, &i, KILN_WORLD_CLIENT, 4, &body, &done, &j, &h, &why) == KILN_OK, "bound to client session 4");
    s.post_edge(KILN_WORLD_SERVER, 1, 11);
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
    check(j.body_runs.load() == 0, "a server Post edge does not wake a client AFTER_STEP task");
    s.post_edge(KILN_WORLD_CLIENT, 4, 11);
    check(until([&] { return j.done_calls.load() == 1; }) && j.done_state.load() == KILN_TASK_COMPLETED,
          "the client world's next Post edge runs it");
    Job k;
    check(s.submit_in(1, &i, KILN_WORLD_CLIENT, 0, &body, &done, &k, &h, nullptr) == KILN_OK, "session 0: the live one");
    s.session_stopping(KILN_WORLD_SERVER, 1);
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
    check(k.done_calls.load() == 0, "the server world's STOPPING leaves it waiting");
    s.session_stopping(KILN_WORLD_CLIENT, 4);
    check(until([&] { return k.done_calls.load() == 1; }) && k.done_state.load() == KILN_TASK_CANCELLED && k.body_runs == 0,
          "its client session's STOPPING cancels it");
    s.session_started(KILN_WORLD_CLIENT, 5);
    Job q;
    q.release.open();
    check(s.submit_in(1, &plain, KILN_WORLD_CLIENT, 5, &body, &done, &q, &h, nullptr) == KILN_OK &&
              until([&] { return q.done_calls.load() == 1; }),
          "a plain task bound to the live session runs at once");
    Job r;
    check(s.submit(1, &i, &body, &done, &r, &h, &why) == KILN_ERR_UNAVAILABLE && why == KILN_TASK_REFUSAL_NO_SERVER_WORLD,
          "submit's AFTER_STEP stays the server world's: NO_SERVER_WORLD with only a client world");
}

// submit_in without AFTER_STEP: the binding holds until the body starts. A bound task still queued when its session
// ends (its STOPPING, or a new session of that world without one) is CANCELLED with its done and its body never runs;
// one that captured a session ending before it was published is cancelled at once; a running body sees cancelled().
KILN_TASKS_TEST(submit_in_plain_never_starts_after_its_session) {
    for (const int how : {0, 1, 2}) {  // 0: STOPPING; 1: a new session without a destroy event; 2: the capture window
        HostPlatform p;
        Env env;
        Pool pool(config(), &p);
        Service s(pool, env);
        s.session_started(KILN_WORLD_SERVER, 1);
        s.session_started(KILN_WORLD_CLIENT, 5);
        KilnTaskInfo plain = after_step("bound");
        plain.flags = 0;
        Job busy1, busy2, bound, server;
        busy1.block = busy2.block = true;
        KilnHandle h = 0;
        if (how == 2) {
            s.set_test_hook([&](Service::Point at) {
                if (at != Service::Point::AfterCapture) return;
                s.session_stopping(KILN_WORLD_CLIENT, 5);
                s.session_started(KILN_WORLD_CLIENT, 6);
            });
            check(s.submit_in(1, &plain, KILN_WORLD_CLIENT, 0, &body, &done, &bound, &h, nullptr) == KILN_OK,
                  "accepted for session 5");
            s.set_test_hook(nullptr);
        } else {
            // the owner's two slots taken: the bound task stays queued
            check(s.submit_in(1, &plain, KILN_WORLD_CLIENT, 5, &body, &done, &busy1, &h, nullptr) == KILN_OK &&
                      s.submit_in(1, &plain, KILN_WORLD_CLIENT, 5, &body, &done, &busy2, &h, nullptr) == KILN_OK &&
                      until([&] { return busy1.body_runs.load() == 1 && busy2.body_runs.load() == 1; }),
                  "two bound bodies running");
            KilnHandle hb = 0;
            check(s.submit_in(1, &plain, KILN_WORLD_CLIENT, 5, &body, &done, &bound, &hb, nullptr) == KILN_OK &&
                      s.submit_in(1, &plain, KILN_WORLD_SERVER, 1, &body, &done, &server, &h, nullptr) == KILN_OK,
                  "a client-bound and a server-bound task queued behind them");
            if (how == 0) s.session_stopping(KILN_WORLD_CLIENT, 5);
            else s.session_started(KILN_WORLD_CLIENT, 6);
            uint32_t state = 0;
            check(s.status(1, hb, &state) == KILN_OK && state == KILN_TASK_CANCELLED,
                  "the queued client-bound task is cancelled at its session's end, before its body starts");
            busy1.release.open();
            busy2.release.open();
            check(until([&] { return busy1.done_calls.load() == 1 && busy2.done_calls.load() == 1; }) &&
                      busy1.saw_cancel.load() == 1 && busy2.saw_cancel.load() == 1,
                  "the running client-bound bodies saw cancelled()");
            check(until([&] { return server.done_calls.load() == 1; }) && server.done_state.load() == KILN_TASK_COMPLETED &&
                      server.saw_cancel.load() == 0,
                  "the server-bound task is untouched by the client world's end");
        }
        check(until([&] { return bound.done_calls.load() == 1; }) && bound.done_state.load() == KILN_TASK_CANCELLED &&
                  bound.body_runs.load() == 0,
              how == 2 ? "its session ended inside the capture window: cancelled at once, never run in session 6"
                       : "never started after its session ended");
    }
}
}  // namespace

int main(int argc, char** argv) { return kiln::tasks::test::run(argc, argv, "tasks edges"); }
