// SPDX-License-Identifier: MIT
// kiln.tasks diagnostics store (src/tasks_stats.{h,cpp}; issue #49): per-instance owner counters under a
// seqlock, window collection that resets only the window, running/fault snapshots for crash reports, and
// the bounded fallback when a writer is stuck mid-update. Fake clocks are just the ns values handed in.
#include <chrono>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>

#include <nlohmann/json.hpp>

#include "../src/tasks.h"
#include "../src/tasks_stats.h"
#include "tasks_platform_host.h"
#include "tasks_test_support.h"

// default_platform() is never called (the pool below gets the test platform); it only has to link.
namespace kiln::tasks {
Platform& default_platform() {
    static auto* host = new test::HostPlatform;
    return *host;
}
}  // namespace kiln::tasks

namespace {
int checks = 0, failures = 0;
void check(bool ok, const char* what) {
    ++checks;
    if (!ok) {
        ++failures;
        std::printf("FAIL: %s\n", what);
    }
}
using namespace kiln::tasks_stats;
}  // namespace

int main() {
    Store store;
    store.begin(1'000'000'000ull);
    store.set_pool_counts(3, 1);

    // One instance: queue, run a segment (CPU excludes the awaited wall gap by construction of the caller),
    // complete. A second instance of the same owner never mixes with the first.
    auto a = store.admit(7, 1, "kiln-auto-loot", "loot-scan", 0xA1, 1'000'000'000ull);
    check(a.owner_slot >= 0 && a.entry >= 0, "admission claims an owner slot and a running entry");
    store.segment_started(a.owner_slot, a.entry, 42, 1'100'000'000ull);
    store.segment_ended(a.owner_slot, a.entry, 40'000'000ull, 50'000'000ull, false);
    auto b = store.admit(7, 2, "kiln-auto-loot", "loot-scan-2", 0xA2, 1'200'000'000ull);
    check(b.owner_slot >= 0 && b.owner_slot != a.owner_slot, "a reload (new instance) gets its own slot");
    store.terminal(a.owner_slot, a.entry, 3 /*Completed*/, 0, 1'300'000'000ull);

    WindowSnapshot w = store.collect_window(61'000'000'000ull, 0, 12);
    check(w.id == 1 && w.span_ns == 60'000'000'000ull, "the first window: id and span from the fake wall clock");
    check(w.pool.workers == 3 && w.pool.long_running == 1 && w.pool.queue_max == 12, "pool fields");
    check(w.owner_count == 2, "both instances are rows");
    const OwnerSnapshot* first = nullptr;
    for (size_t i = 0; i < w.owner_count; ++i)
        if (w.owners[i].instance == 1) first = &w.owners[i];
    check(first && first->completed == 1 && first->faulted == 0 && first->cpu_ns == 40'000'000ull &&
              first->wall_ns == 50'000'000ull && first->longest_ns == 50'000'000ull &&
              std::string(first->longest_name) == "loot-scan" && first->running_current == 0,
          "instance 1 keeps its own counters, CPU and wall apart, longest named");
    WindowSnapshot w2 = store.collect_window(62'000'000'000ull, 0, 0);
    bool first_again = false;
    for (size_t i = 0; i < w2.owner_count; ++i)
        if (w2.owners[i].instance == 1 && w2.owners[i].completed) first_again = true;
    check(!first_again, "the second window does not repeat the first window's counters");
    check(w2.id == 2, "window ids increase");

    // Running snapshot: running, suspended and on-tick are different states of one entry.
    store.segment_started(b.owner_slot, b.entry, 43, 62'000'000'000ull);
    CrashSnapshot crash = store.snapshot_crash(62'500'000'000ull);
    check(!crash.unavailable && crash.running_count == 1 && crash.running[0].state == EntryState::Running &&
              crash.running[0].thread_id == 43 && crash.running[0].elapsed_ns == 500'000'000ull,
          "a running segment: thread and elapsed");
    store.segment_ended(b.owner_slot, b.entry, 1'000'000ull, 2'000'000ull, true);
    crash = store.snapshot_crash(63'000'000'000ull);
    check(crash.running_count == 1 && crash.running[0].state == EntryState::Suspended, "a suspended lease holds no thread");
    store.set_on_tick(b.entry, 99, 63'000'000'000ull);
    crash = store.snapshot_crash(63'250'000'000ull);
    check(crash.running_count == 1 && crash.running[0].state == EntryState::OnTick && crash.running[0].thread_id == 99,
          "a tick hop marks the entry on-tick");

    // Faults: the last 16, oldest first.
    for (uint64_t i = 0; i < 20; ++i) {
        auto f = store.admit(9, 1, "mod-nine", "boom", 0xB0 + i, 64'000'000'000ull);
        store.note_fault_code(f.entry, 0xC0000005u);
        store.terminal(f.owner_slot, f.entry, 4 /*Faulted*/, 0xC0000005u, 64'000'000'000ull);
    }
    crash = store.snapshot_crash(65'000'000'000ull);
    check(crash.fault_count == 16 && crash.faults[0].sequence == 5 && crash.faults[15].sequence == 20,
          "the fault ring keeps the last 16 in order");

    // A writer stuck at an odd sequence: bounded retries, then unavailable (never a spin).
    store.force_seq_odd_for_test(b.owner_slot);
    check(store.totals(66'000'000'000ull).unavailable, "a slot stuck mid-update makes totals unavailable, bounded");
    store.clear_seq_odd_for_test(b.owner_slot);
    store.force_entry_seq_odd_for_test(b.entry);
    const auto t0 = std::chrono::steady_clock::now();
    crash = store.snapshot_crash(66'000'000'000ull);
    const auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - t0).count();
    check(crash.unavailable && ms < 5000, "a stuck seqlock falls back, bounded");
    store.clear_entry_seq_odd_for_test(b.entry);

    // The cap: 64 owner rows, the rest truncated and said so.
    Store capped;
    capped.begin(0);
    int admitted = 0;
    for (uint64_t i = 1; i <= 70; ++i)
        if (capped.admit(i, 1, "owner", "task", i, 0).owner_slot >= 0) ++admitted;
    WindowSnapshot cw = capped.collect_window(60'000'000'000ull, 0, 0);
    check(admitted == 64 && cw.owner_count == 64 && cw.owners_truncated, "owner rows are capped at 64 with owners_truncated");

    // ---- the pool over the fake clocks: CPU excludes the awaited wall time ----
    {
        using namespace kiln::tasks;
        test::HostPlatform platform;
        platform.set_clock(10'000'000'000ull);
        PoolConfig cfg;
        cfg.normal_workers = 1;
        cfg.long_running_workers = 1;
        Pool pool(cfg, &platform);
        pool.open_owner(5, "mod-five");
        struct Job {
            test::HostPlatform* platform;
            std::atomic<int> done{0};
        } job{&platform};
        auto body = [](const KilnTaskRun*, void* user) {
            auto* j = static_cast<Job*>(user);
            j->platform->advance_cpu(5'000'000ull);   // 5 ms of this thread's CPU
            j->platform->advance(9'000'000ull);       // 9 ms of wall (4 of it awaited IO)
        };
        auto done = [](KilnHandle, uint32_t, void* user) { static_cast<Job*>(user)->done.fetch_add(1); };
        SubmitArgs args;
        args.name = "scan \"quoted\" \\ name";
        KilnHandle h = 0;
        Refusal why = Refusal::None;
        check(pool.submit(5, args, body, done, &job, &h, &why) == KILN_OK, "pool: submit with a quoted name is accepted");
        for (int i = 0; i < 5000 && !job.done.load(); ++i) std::this_thread::sleep_for(std::chrono::milliseconds(1));
        check(job.done.load() == 1, "pool: the task and its done ran");
        WindowSnapshot pw = pool.collect_tasks_window(0);
        check(pw.owner_count == 1 && pw.owners[0].completed == 1 && pw.owners[0].cpu_ns == 5'000'000ull &&
                  pw.owners[0].wall_ns == 9'000'000ull && pw.owners[0].longest_ns == 9'000'000ull,
              "pool: the segment's CPU and wall land on its owner's window row");
        const std::string json = format_window_json(pw);
        check(json.find("scan \\\"quoted\\\" \\\\ name") != std::string::npos,
              "pool: a quoted task name reaches the JSON only escaped");
    }

    // ---- a crash snapshot while a body runs, and while a worker holds the pool lock ----
    {
        using namespace kiln::tasks;
        test::HostPlatform platform;
        PoolConfig cfg;
        cfg.normal_workers = 1;
        cfg.long_running_workers = 1;
        Pool pool(cfg, &platform);
        pool.open_owner(6, "mod-six");
        test::Gate gate;
        struct Job {
            test::Gate* gate;
            std::atomic<int> started{0}, done{0};
        } job{&gate};
        auto body = [](const KilnTaskRun*, void* user) {
            auto* j = static_cast<Job*>(user);
            j->started.fetch_add(1);
            j->gate->wait();
        };
        auto done = [](KilnHandle, uint32_t, void* user) { static_cast<Job*>(user)->done.fetch_add(1); };
        SubmitArgs args;
        args.name = "blocked";
        KilnHandle h = 0;
        Refusal why = Refusal::None;
        check(pool.submit(6, args, body, done, &job, &h, &why) == KILN_OK, "crash race: submit");
        for (int i = 0; i < 5000 && !job.started.load(); ++i) std::this_thread::sleep_for(std::chrono::milliseconds(1));
        CrashSnapshot snap = pool.stats_store().snapshot_crash(platform.now_ns());
        check(!snap.unavailable && snap.running_count == 1 && snap.running[0].state == EntryState::Running,
              "crash race: a snapshot taken while the body runs sees it running");
        // Inside submit's publication (the pool mutex is held here): a snapshot must not take it.
        std::atomic<bool> snapshotted{false};
        pool.set_test_hook([&](Pool::Point p) {
            if (p != Pool::Point::SubmitPublished) return;
            CrashSnapshot inner = pool.stats_store().snapshot_crash(platform.now_ns());
            snapshotted.store(!inner.unavailable);
        });
        Job job2{&gate};
        job2.gate = nullptr;
        auto body2 = [](const KilnTaskRun*, void*) {};
        KilnHandle h2 = 0;
        SubmitArgs args2;
        args2.name = "second";
        check(pool.submit(6, args2, body2, done, &job2, &h2, &why) == KILN_OK && snapshotted.load(),
              "crash race: a snapshot under the pool lock returns (it never takes that lock)");
        pool.set_test_hook(nullptr);
        // Retiring the owner while its body still runs does not lose the running task either.
        uint32_t outstanding = 0;
        pool.retire_owner(6, &outstanding);
        snap = pool.stats_store().snapshot_crash(platform.now_ns());
        check(!snap.unavailable && snap.running_total >= 1, "crash race: a snapshot after retire still sees the running body");
        gate.open();
        for (int i = 0; i < 5000 && job.done.load() < 1; ++i) std::this_thread::sleep_for(std::chrono::milliseconds(1));
        check(job.done.load() == 1, "crash race: the blocked task finishes after the snapshot");

        // Concurrent window collectors (the server and a client world write in the same minute):
        // every terminal count is collected exactly once across all of them.
        std::atomic<uint64_t> completed_sum{0};
        auto collect = [&] {
            for (int i = 0; i < 50; ++i) {
                WindowSnapshot w = pool.collect_tasks_window(0);
                for (size_t k = 0; k < w.owner_count; ++k) completed_sum.fetch_add(w.owners[k].completed);
            }
        };
        std::thread t1(collect), t2(collect);
        t1.join();
        t2.join();
        check(completed_sum.load() == 1, "concurrent windows: the completed task is counted once in total (the retired owner's queued task was cancelled, not completed)");
    }


    // ---- a writer's update between a reader's check and its copy ----
    {
        // totals(): a retried copy must not add the slot's numbers twice.
        Store t;
        t.begin(0);
        const auto x = t.admit(1, 1, "one", "a", 1, 0);
        t.admit(1, 1, "one", "b", 2, 0);
        struct Bump {
            Store* store;
            int slot;
            int calls = 0;
        } bump{&t, x.owner_slot};
        t.set_copy_hook_for_test(
            [](void* u) {
                auto* b = static_cast<Bump*>(u);
                if (b->calls++ == 0) {  // one whole update lands mid-copy: the copy retries
                    b->store->force_seq_odd_for_test(b->slot);
                    b->store->clear_seq_odd_for_test(b->slot);
                }
            },
            &bump);
        const Totals tt = t.totals(0);
        t.set_copy_hook_for_test(nullptr, nullptr);
        check(!tt.unavailable && bump.calls >= 2 && tt.owners == 1 && tt.queued == 2,
              "totals: a retried copy counts the slot once (owners 1, queued 2)");

        // snapshot_crash(): a task ending between the active check and the copy is skipped, not
        // reported as an unavailable snapshot.
        Store c;
        c.begin(0);
        const auto first = c.admit(2, 1, "two", "ends", 3, 0);
        c.admit(2, 1, "two", "stays", 4, 0);
        struct End {
            Store* store;
            AdmitResult r;
            int calls = 0;
        } end{&c, first};
        c.set_copy_hook_for_test(
            [](void* u) {
                auto* e = static_cast<End*>(u);
                if (e->calls++ == 0) e->store->terminal(e->r.owner_slot, e->r.entry, kStateCompleted, 0, 0);
            },
            &end);
        const CrashSnapshot cs = c.snapshot_crash(0);
        c.set_copy_hook_for_test(nullptr, nullptr);
        check(!cs.unavailable && cs.running_count == 1 && std::string(cs.running[0].task_name) == "stays",
              "crash snapshot: a task ending mid-copy is skipped; the snapshot stays available");
    }

    // ---- names at the 128-byte limit: never cut inside a UTF-8 character ----
    {
        Store n;
        n.begin(0);
        const std::string task = std::string(126, 'a') + "\xc3\xa9";          // 128 bytes, ends multi-byte
        const std::string owner = std::string(127, 'o') + "\xc3\xa9";         // 129 bytes: cut to 127
        const auto r = n.admit(3, 1, owner, task, 5, 0);
        n.segment_started(r.owner_slot, r.entry, 1, 0);
        CrashSnapshot cs = n.snapshot_crash(0);
        check(cs.running_count == 1 && std::string(cs.running[0].task_name) == task,
              "a 128-byte task name ending in a multi-byte character is copied whole (running row)");
        check(cs.running_count == 1 && std::string(cs.running[0].owner_name) == std::string(127, 'o'),
              "an over-long owner name is cut before its last whole character, never inside one");
        n.segment_ended(r.owner_slot, r.entry, 1, 1, false);
        n.terminal(r.owner_slot, r.entry, kStateFaulted, 0xC0000005u, 0);
        cs = n.snapshot_crash(0);
        check(cs.fault_count == 1 && std::string(cs.faults[0].task_name) == task &&
                  std::string(cs.faults[0].owner_name) == std::string(127, 'o'),
              "the fault row keeps the whole names");
        const WindowSnapshot w = n.collect_window(1, 0, 0);
        check(w.owner_count == 1 && std::string(w.owners[0].longest_name) == task &&
                  std::string(w.owners[0].owner_name) == std::string(127, 'o'),
              "the window row keeps the whole names");
        const std::string json = "{" + format_window_json(w) + "}";
        check(json.find('?') == std::string::npos, "no name byte was replaced in the JSON");
    }

    // ---- name safety through the pool: refused at submit, escaped in JSON, flattened in crash text ----
    {
        using namespace kiln::tasks;
        test::HostPlatform platform;
        PoolConfig cfg;
        cfg.normal_workers = 1;
        cfg.long_running_workers = 1;
        Pool pool(cfg, &platform);
        pool.open_owner(8, "mod-eight");
        auto body = [](const KilnTaskRun*, void*) {};
        auto done = [](KilnHandle, uint32_t, void*) {};
        for (const std::string& bad : {std::string("tab\there"), std::string("nl\nhere"), std::string("del\x7f"),
                                       std::string("c1\xc2\x85"), std::string("bad\xff"), std::string("cut\xc3"),
                                       std::string("over\xc0\xaf"), std::string("sur\xed\xa0\x80")}) {
            SubmitArgs a;
            a.name = bad;
            KilnHandle h = 0;
            Refusal why = Refusal::None;
            check(pool.submit(8, a, body, done, nullptr, &h, &why) == KILN_ERR_INVALID_ARGUMENT && why == Refusal::Argument,
                  "a task name with a control character or invalid UTF-8 is refused at submit");
        }
        check(pool.stats_store().owner_count() == 0 && pool.stats_store().snapshot_crash(0).running_total == 0,
              "a refused name never reaches the diagnostics store");

        // A name with quotes and backslashes: escaped in the JSON (which still parses, and gives the
        // name back), flattened to '?' in crash text.
        test::Gate gate;
        struct Held {
            test::Gate* gate;
            test::HostPlatform* platform;
        } held_job{&gate, &platform};
        auto held = [](const KilnTaskRun*, void* user) {
            auto* j = static_cast<Held*>(user);
            j->gate->wait();
            j->platform->advance(2'000'000ull);  // a segment with wall time names the window's longest
        };
        SubmitArgs a;
        const std::string quoted = "say \"hi\" \\ back";
        a.name = quoted;
        KilnHandle h = 0;
        Refusal why = Refusal::None;
        check(pool.submit(8, a, held, done, &held_job, &h, &why) == KILN_OK, "a quoted name is accepted");
        for (int i = 0; i < 5000 && pool.stats_store().snapshot_crash(0).running_total == 0; ++i)
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        for (int i = 0; i < 5000; ++i) {
            const CrashSnapshot s = pool.stats_store().snapshot_crash(0);
            if (s.running_count == 1 && s.running[0].state == EntryState::Running) break;
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
        static char text[8192];
        const CrashSnapshot s = pool.stats_store().snapshot_crash(platform.now_ns());
        const std::size_t len = format_crash(s, text, sizeof text);
        const std::string crash(text, len);
        check(crash.find("task=\"say ?hi? ? back\"") != std::string::npos && crash.find("owner=\"mod-eight\"") != std::string::npos,
              "crash text flattens quotes and backslashes to '?'");
        gate.open();
        for (int i = 0; i < 5000 && pool.stats_store().snapshot_crash(0).running_total != 0; ++i)
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        platform.advance(1'000'000ull);
        const WindowSnapshot w = pool.collect_tasks_window(0);
        const std::string line = "{" + format_window_json(w) + "}";
        bool parsed = false, same = false;
        try {
            const auto j = nlohmann::json::parse(line);
            parsed = true;
            same = j["tasks"]["owners"].size() == 1 && j["tasks"]["owners"][0]["longest_name"] == quoted &&
                   j["tasks"]["owners"][0]["owner"] == "mod-eight";
        } catch (...) {
        }
        check(parsed && same, "the tasks JSON parses and gives the quoted name back exactly");
        if (!(parsed && same)) std::printf("  line: %s\n", line.c_str());
    }

    // ---- crash path: another thread holds the pool lock; a slot stuck at an odd sequence ----
    {
        using namespace kiln::tasks;
        test::HostPlatform platform;
        PoolConfig cfg;
        cfg.normal_workers = 1;
        cfg.long_running_workers = 1;
        Pool pool(cfg, &platform);
        pool.open_owner(9, "mod-nine");
        test::Gate inside, release;
        pool.set_test_hook([&](Pool::Point p) {
            if (p != Pool::Point::SubmitPublished) return;
            inside.open();
            release.wait();  // the pool mutex stays held until the main thread's report is written
        });
        std::thread submitter([&] {
            SubmitArgs a;
            a.name = "held";
            KilnHandle h = 0;
            Refusal why = Refusal::None;
            pool.submit(9, a, [](const KilnTaskRun*, void*) {}, [](KilnHandle, uint32_t, void*) {}, nullptr, &h, &why);
        });
        inside.wait();
        static char text[8192];
        const auto t0 = std::chrono::steady_clock::now();
        CrashSnapshot s = pool.stats_store().snapshot_crash(platform.now_ns());
        std::size_t len = format_crash(s, text, sizeof text);
        const auto took = std::chrono::steady_clock::now() - t0;
        check(!s.unavailable && std::string(text, len).find("task=\"held\"") != std::string::npos &&
                  took < std::chrono::seconds(2),
              "a crash snapshot while another thread holds the pool lock returns at once and lists the task");
        // The same task's entry stuck mid-update (a writer that crashed inside it): bounded, then the fallback.
        const int entry = pool.stats_store().entry_of_handle(s.running_count ? s.running[0].handle : 0);
        pool.stats_store().force_entry_seq_odd_for_test(entry);
        s = pool.stats_store().snapshot_crash(platform.now_ns());
        len = format_crash(s, text, sizeof text);
        check(entry >= 0 && std::string(text, len) == "(task snapshot unavailable)\n",
              "a running entry stuck at an odd sequence: the section reads (task snapshot unavailable)");
        pool.stats_store().clear_entry_seq_odd_for_test(entry);
        release.open();
        submitter.join();
        pool.set_test_hook(nullptr);
    }


    // ---- the window take: nothing lost between the copy and the reset; a wedged writer never stalls it ----
    {
        Store t;
        t.begin(0);
        const auto r = t.admit(4, 1, "four", "x", 1, 0);
        struct Late {
            Store* store;
            AdmitResult r;
            int calls = 0;
        } late{&t, r};
        t.set_copy_hook_for_test(
            [](void* u) {
                auto* l = static_cast<Late*>(u);
                if (++l->calls == 2) l->store->terminal(l->r.owner_slot, l->r.entry, kStateCompleted, 0, 0);  // after the copy
            },
            &late);
        const WindowSnapshot w1 = t.collect_window(1, 0, 0);
        t.set_copy_hook_for_test(nullptr, nullptr);
        const WindowSnapshot w2 = t.collect_window(2, 0, 0);
        uint64_t completed = 0;
        for (std::size_t i = 0; i < w1.owner_count; ++i) completed += w1.owners[i].completed;
        for (std::size_t i = 0; i < w2.owner_count; ++i) completed += w2.owners[i].completed;
        check(late.calls >= 2 && completed == 1, "a task ending between the window copy and its reset is still counted once");

        // A writer flag held for good (a thread wedged inside an update, sequence even): the
        // once-a-minute collection still returns, and keeps the slot's numbers.
        const auto b = t.admit(5, 1, "five", "y", 2, 0);
        t.terminal(b.owner_slot, b.entry, kStateCompleted, 0, 0);
        check(t.try_hold_writer_for_test(b.owner_slot), "test: the writer flag is held");
        std::atomic<bool> returned{false};
        WindowSnapshot w3;
        std::thread collector([&] {
            w3 = t.collect_window(3, 0, 0);
            returned.store(true);
        });
        for (int i = 0; i < 2000 && !returned.load(); ++i) std::this_thread::sleep_for(std::chrono::milliseconds(1));
        const bool ok = returned.load();
        t.release_writer_for_test(b.owner_slot);  // let a hung collector finish so the test can end
        collector.join();
        bool five = false;
        for (std::size_t i = 0; i < w3.owner_count; ++i) five = five || (w3.owners[i].owner == 5 && w3.owners[i].completed == 1);
        check(ok && five, "a wedged writer flag never stalls the window collection, and its row is kept");
    }

    std::printf("tasks_stats_test: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
