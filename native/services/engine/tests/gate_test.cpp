// SPDX-License-Identifier: MIT
// kiln.engine: the original native-thread Scene write gate over a fake engine from both real profiles.
// - every refusal, immediate, with zero waits; the words restored on every path;
// - a simulated GameServer update holding the Scene word Shared exactly as the engine does (try-shared, 128 spins,
//   yield, retry) overlapping gate writers (ThreadSanitizer proves the exclusion: the writer's
//   plain stores and the update's plain loads of the same "component" would race without it);
// - in-frame, client-thread and unknown-thread callers refused without touching a word (no read, no CAS);
// - inside the critical section only engine reads, the tracker call and guarded writes: no allocation (global operator
//   new counter), no CAS beyond acquire's, bounded reads; tests/lint_gate.py checks the sources for waits, locks, logs
//   and KilnApi calls, plugin_host_test counts KilnApi calls through the real table.
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <new>
#include <string>
#include <thread>
#include <vector>

#include "engine_state.h"
#include "fake_engine.h"
#include "gate.h"
#include "memory.h"
#include "profiles.g.h"
#include "resolve.h"

namespace kiln::engine::mem {
extern std::atomic<std::uint64_t> test_reads, test_cas, test_writes, test_releases, test_releases_without_holder;
extern bool (*test_holder_probe)();
extern std::atomic<std::uint64_t> test_gate_shared_releases;
extern thread_local bool test_in_gate_release;
extern thread_local int test_priority;
extern thread_local std::uint64_t test_boost_cas, test_restore_releases;
extern thread_local int test_boosts, test_restores;
extern std::atomic<int> test_tracker_calls;
extern std::atomic<bool> test_tracker_fault;
}

std::atomic<long> g_allocations{0};
void* operator new(std::size_t n) {
    g_allocations.fetch_add(1, std::memory_order_relaxed);
    if (void* p = std::malloc(n ? n : 1)) return p;
    throw std::bad_alloc();
}
void operator delete(void* p) noexcept { std::free(p); }
void operator delete(void* p, std::size_t) noexcept { std::free(p); }

namespace {
using namespace kiln::engine;
std::atomic<int> checks{0}, failures{0};
void check(bool ok, const std::string& what) {
    ++checks;
    if (!ok) { ++failures; std::fprintf(stderr, "FAIL %s\n", what.c_str()); }
}
const Layout& layout_of(const char* target) {
    for (const auto& p : generated::kProfiles)
        if (std::string(p.target) == target) return p.layout;
    std::abort();
}
// A new record for owner 1 at a new server step each time (so one test's busy latch never leaks into the next).
std::atomic<std::uint64_t> g_step{1000};
GateState fresh() { GateState g{}; g.size = sizeof g; g.owner = 1; g.step = ++g_step; return g; }
GateState gate_for(std::uint64_t owner, std::uint64_t step) { GateState g{}; g.size = sizeof g; g.owner = owner; g.step = step; return g; }
std::int32_t word(std::uintptr_t a) { return __atomic_load_n(fake::word(a), __ATOMIC_SEQ_CST); }

void refusals(const char* target) {
    const Layout& L = layout_of(target);
    const std::string t = std::string(target) + ": ";
    fake::Engine e(L);
    EcsView v{};
    check(ecs_view(&L, ViewKind::Authority, v, e.image.at()) == Reason::None, t + "authority view");
    const auto acquire = [&](GateState& g, CallerThread c = CallerThread::Other) { return gate_acquire(&L, v, g, c, e.image.at()); };
    {
        // buffers for the budget writes, allocated before the measured section so they do not count as its allocations
        std::vector<std::uint8_t> big(L.write.maximum_bytes + 1, 0xab);
        fake::Region dst(L.write.maximum_bytes + 4096);
        GateState g = fresh();
        // inside the critical section: what a gate member calls between acquire and release; results are kept and
        // checked afterwards so the test's own strings do not count as allocations of the section
        const int calls = mem::test_tracker_calls.load();
        const long allocations = g_allocations.load();
        const std::uint64_t reads = mem::test_reads.load();
        const Reason acquired = acquire(g);
        const bool valid = gate_validate(&L, g, e.image.at());
        const bool supported = gate_component_supported(g, 3), beyond = gate_component_supported(g, 2000);
        const bool tracked = gate_track_change(g, 0x5000, 3);
        const bool no_entity = gate_track_change(g, 0, 3), no_index = gate_track_change(g, 0x5000, 2000);
        mem::test_tracker_fault = true;
        const bool faulted = gate_track_change(g, 0x5000, 3);
        const std::uint64_t cas = mem::test_cas.load(), writes = mem::test_writes.load();
        const std::uint64_t payload = 0x1122334455667788ull;
        const GateWrite wrote = gate_write(g, e.table.at(512), &payload, sizeof payload);
        const GateWrite write_fault = gate_write(g, fake::unmapped(), &payload, sizeof payload);
        const GateWrite no_data = gate_write(g, e.table.at(512), nullptr, 8), empty = gate_write(g, e.table.at(512), nullptr, 0);
        // the write budget: exactly the maximum writes, one over refuses before touching memory
        const std::uint64_t writes_at_limit = mem::test_writes.load();
        const GateWrite at_limit = gate_write(g, dst.at(), big.data(), L.write.maximum_bytes);
        const std::uint64_t writes_after_limit = mem::test_writes.load();
        const GateWrite over_limit = gate_write(g, dst.at(), big.data(), L.write.maximum_bytes + 1);
        const bool over_untouched = mem::test_writes.load() == writes_after_limit;
        const bool still_valid = gate_validate(&L, g, e.image.at());
        const std::int32_t registry_in_section = word(e.registry_word());
        const long section_allocations = g_allocations.load() - allocations;
        const std::uint64_t section_reads = mem::test_reads.load() - reads;
        const std::uint64_t section_cas = mem::test_cas.load() - cas, section_writes = mem::test_writes.load() - writes;
        check(acquired == Reason::None && g.held, t + "acquire");
        check(word(e.registry_word()) == 1 && word(e.scene_word()) == static_cast<std::int32_t>(0x80000000u),
              t + "registry Shared, Scene exclusive");
        check(valid, t + "validate");
        check(supported && !beyond, t + "component policy under the gate");
        check(tracked && mem::test_tracker_calls.load() == calls + 2, t + "track change calls the recorder");
        check(!no_entity && !no_index, t + "no entity / unsupported index");
        check(!faulted, t + "a faulting recorder is reported");
        check(wrote == GateWrite::Ok && fake::get<std::uint64_t>(e.table.at(512)) == payload, t + "guarded write under the gate");
        check(write_fault == GateWrite::Faulted && no_data == GateWrite::NotHeld && empty == GateWrite::Ok && still_valid,
              t + "a faulting write fails, the gate stays held and valid");
        check(at_limit == GateWrite::Ok && writes_after_limit == writes_at_limit + 1, t + "a write at the budget is allowed");
        check(over_limit == GateWrite::TooLarge && over_untouched, t + "a write over the budget refuses before touching memory");
        // ShroudForge's Validate re-resolves the view; on the dedicated server that takes the registry word Shared again
        // (nested under our own share: nonblocking, released at once). A client never touches a word inside the hold.
        check(L.dedicated_server ? section_cas > 0 && registry_in_section == 1 : section_cas == 0,
              t + "inside the section only nested registry shares, all released (" + std::to_string(section_cas) + " CAS)");
        check(section_writes == 3, t + "only the member's own writes (payload, a faulted write, the at-budget write)");
        check(section_allocations == 0, t + "no allocation in the critical section (" + std::to_string(section_allocations) + ")");
        check(section_reads < 1024, t + "bounded engine reads in the section (" + std::to_string(section_reads) + ")");
        fake::put<std::uint8_t>(e.image.at(L.write.tracker), 0xcc);
        const int before_guard = mem::test_tracker_calls.load();
        check(!gate_track_change(g, 0x5000, 3) && mem::test_tracker_calls.load() == before_guard,
              t + "tracker guard bytes changed: refused before the call");
        fake::put<std::uint8_t>(e.image.at(L.write.tracker), L.write.tracker_bytes[0]);
        // another Kiln writer meanwhile: one Kiln holder at a time, refused before any engine word
        GateState second = fresh();
        const std::uint64_t reads_before = mem::test_reads.load(), cas_before = mem::test_cas.load();
        check(acquire(second) == Reason::KilnHolderBusy && !second.held && word(e.registry_word()) == 1 &&
                  mem::test_reads.load() == reads_before && mem::test_cas.load() == cas_before,
              t + "a second Kiln writer is refused kiln-gate-busy without touching the engine");
        check(acquire(g) == Reason::EcsWriteGuardAlreadyHeld && word(e.registry_word()) == 1, t + "already held refused untouched");
        gate_release(g);
        check(word(e.registry_word()) == 0 && word(e.scene_word()) == 0 && !g.held, t + "release restores both words");
        check(gate_write(g, e.table.at(512), &payload, sizeof payload) == GateWrite::NotHeld, t + "no write without a held gate");
        gate_release(g);
        check(word(e.registry_word()) == 0 && word(e.scene_word()) == 0, t + "a second release changes nothing");
        check(!gate_validate(&L, g, e.image.at()) && !gate_component_supported(g, 3), t + "released gate validates nothing");
    }
    const auto refused = [&](auto edit, Reason want, const std::string& what, CallerThread caller = CallerThread::Other) {
        GateState g = fresh();
        const std::int32_t reg_before = word(e.registry_word()), scene_before = word(e.scene_word());
        edit();
        const std::uint64_t reads = mem::test_reads.load(), cas = mem::test_cas.load();
        const Reason got = acquire(g, caller);
        check(got == want && !g.held, t + what + " (got " + reason_name(got) + ")");
        check(word(e.registry_word()) == reg_before && word(e.scene_word()) == scene_before, t + what + ": words as before");
        if (caller != CallerThread::Other)
            check(mem::test_reads.load() == reads && mem::test_cas.load() == cas, t + what + ": no engine read, no CAS");
    };
    // the engine's own holders
    __atomic_store_n(fake::word(e.registry_word()), static_cast<std::int32_t>(0x80000000u), __ATOMIC_SEQ_CST);
    refused([] {}, Reason::EcsThreadRegistryBusy, "a thread registering");
    __atomic_store_n(fake::word(e.registry_word()), 0, __ATOMIC_SEQ_CST);
    __atomic_store_n(fake::word(e.scene_word()), 1, __ATOMIC_SEQ_CST);  // the GameServer update holds it Shared
    refused([] {}, Reason::EcsSceneFrameBusy, "the update's frame");
    refused([] {}, Reason::InFrame, "called inside the server world's update", CallerThread::ServerWorld);
    refused([] {}, Reason::WorldThread, "called on the client world thread", CallerThread::ClientWorld);
    refused([] {}, Reason::CallerThreadUnknown, "the core cannot name the thread", CallerThread::Unknown);
    __atomic_store_n(fake::word(e.scene_word()), 0, __ATOMIC_SEQ_CST);
    // in-frame never touches a word even when they are free
    refused([] {}, Reason::InFrame, "in-frame, words free", CallerThread::ServerWorld);
    // owner/authority checks
    {
        EcsView stale = v;
        stale.manager += 8;
        GateState g = fresh();
        check(gate_acquire(&L, stale, g, CallerThread::Other, e.image.at()) == Reason::EcsWriteOwnerChangedOrNotAuthoritative &&
                  word(e.registry_word()) == 0 && word(e.scene_word()) == 0,
              t + "a stale expected view: refused after taking the words, both released");
        EcsView replica = v;
        replica.role = Role::ClientReplica;
        check(gate_acquire(&L, replica, g, CallerThread::Other, e.image.at()) == Reason::EcsWriteRequiresCurrentLocalAuthority,
              t + "a replica view never writes");
        EcsView other_image = v;
        other_image.owners.image += 0x1000;
        check(gate_acquire(&L, other_image, g, CallerThread::Other, e.image.at()) == Reason::EcsWriteRequiresCurrentLocalAuthority,
              t + "another image's view never writes");
        check(gate_acquire(nullptr, v, g, CallerThread::Other, e.image.at()) == Reason::EcsWriteGateDisabled,
              t + "no exact profile");
        Layout off = L;
        off.write.enabled = false;
        check(gate_acquire(&off, v, g, CallerThread::Other, e.image.at()) == Reason::EcsWriteGateDisabled, t + "write gate off");
    }
    refused([&] { fake::put<std::uint8_t>(e.exec.at(L.write.execution_mode), 1); },
            Reason::EcsWriteOwnerChangedOrNotAuthoritative, "execution mode not authoritative");
    fake::put<std::uint8_t>(e.exec.at(L.write.execution_mode), L.write.authority_mode);
    refused([&] { e.set_rows(false); }, Reason::EcsServerThreadUnregistered, "the GameServer thread unregistered");
    e.set_rows(true);
    // validate notices a stopping thread and a stopping Scene while the gate is held
    {
        GateState g = fresh();
        check(acquire(g) == Reason::None, t + "acquire for validate");
        fake::put<std::uint32_t>(e.thread + L.write.thread_stop, 1);
        check(!gate_validate(&L, g, e.image.at()), t + "validate refuses a stopping thread");
        fake::put<std::uint32_t>(e.thread + L.write.thread_stop, 0);
        fake::put<std::uint8_t>(e.scene.at(L.write.scene_state), 2);
        check(!gate_validate(&L, g, e.image.at()), t + "validate refuses a Scene leaving the running state");
        fake::put<std::uint8_t>(e.scene.at(L.write.scene_state), L.write.running_state);
        check(gate_validate(&L, g, e.image.at()), t + "validate again");
        gate_release(g);
    }
    check(word(e.registry_word()) == 0 && word(e.scene_word()) == 0, t + "all words free at the end");
}

// Deterministic: while a writer holds the Scene word exclusive, a simulated update cannot take it Shared; after the
// writer releases, the update enters. No timing, bounded, and it releases the words on every exit.
void blocked_update(const char* target) {
    const Layout& L = layout_of(target);
    const std::string t = std::string(target) + ": blocked update: ";
    fake::Engine e(L);
    EcsView v{};
    check(ecs_view(&L, ViewKind::Authority, v, e.image.at()) == Reason::None, t + "view");
    GateState g = fresh();
    check(gate_acquire(&L, v, g, CallerThread::Other, e.image.at()) == Reason::None &&
              word(e.scene_word()) == static_cast<std::int32_t>(0x80000000u),
          t + "the writer holds the Scene word exclusive");
    std::atomic<int> phase{0};  // 0 = trying, 1 = entered
    std::atomic<bool> give_up{false};
    std::thread update([&] {
        std::int32_t* scene = fake::word(e.scene_word());
        while (!give_up.load()) {
            std::int32_t cur = __atomic_load_n(scene, __ATOMIC_SEQ_CST);
            if (cur >= 0 && __atomic_compare_exchange_n(scene, &cur, cur + 1, false, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST)) {
                phase.store(1, std::memory_order_release);
                __atomic_fetch_sub(scene, 1, __ATOMIC_SEQ_CST);
                return;
            }
            std::this_thread::yield();
        }
    });
    // give the update many chances to (wrongly) enter while we hold the word
    for (int i = 0; i < 100000 && phase.load() == 0; ++i) std::this_thread::yield();
    const bool blocked = phase.load() == 0;
    gate_release(g);
    for (int i = 0; i < 1000000 && phase.load() == 0; ++i) std::this_thread::yield();
    give_up = true;  // in case release somehow did not let it in, never hang
    update.join();
    check(blocked, t + "the update could not take the Scene word while the writer held it");
    check(phase.load() == 1, t + "the update entered after the writer released");
    check(word(e.scene_word()) == 0 && word(e.registry_word()) == 0, t + "words free at the end");
}

// The engine's update (shared acquire with 128 pause spins then SwitchToThread, the frame, lock dec)
// against gate writers on two threads. A writer stores a pair inside its critical section; the update reads it.
void overlap(const char* target) {
    const Layout& L = layout_of(target);
    fake::Engine e(L);
    EcsView v{};
    check(ecs_view(&L, ViewKind::Authority, v, e.image.at()) == Reason::None, "overlap view");
    struct Pair { std::uint64_t a, b; };
    auto* component = reinterpret_cast<Pair*>(e.table.at(256));  // plain engine memory, no atomics on purpose
    std::atomic<bool> stop{false};
    std::atomic<long> frames{0}, torn{0}, waits_in_update{0};
    std::thread update([&] {
        std::int32_t* scene = fake::word(e.scene_word());
        while (!stop.load()) {
            for (;;) {  // 0x140578de0 / 0x1408b8a20 and the caller's SwitchToThread loop
                bool got = false;
                for (int spin = 0; spin < 128 && !got; ++spin) {
                    std::int32_t cur = __atomic_load_n(scene, __ATOMIC_SEQ_CST);
                    got = cur >= 0 && __atomic_compare_exchange_n(scene, &cur, cur + 1, false, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST);
                }
                if (got) break;
                ++waits_in_update;
                std::this_thread::yield();
            }
            if (component->a != component->b) ++torn;  // Kiln's Pre/Post would run here
            ++frames;
            __atomic_fetch_sub(scene, 1, __ATOMIC_SEQ_CST);
        }
    });
    std::atomic<long> writes{0}, busy{0};
    const auto writer = [&](std::uint64_t seed) {
        for (int i = 0; i < 4000; ++i) {
            GateState g = fresh();
            const Reason r = gate_acquire(&L, v, g, CallerThread::Other, e.image.at());
            if (r != Reason::None) {
                check(r == Reason::EcsSceneFrameBusy || r == Reason::EcsThreadRegistryBusy || r == Reason::KilnHolderBusy,
                      std::string("overlap: only busy refusals, got ") + reason_name(r));
                ++busy;
                continue;
            }
            component->a = seed + i;
            component->b = seed + i;
            ++writes;
            gate_release(g);
        }
    };
    std::thread w1(writer, 1'000'000), w2(writer, 2'000'000);
    w1.join();
    w2.join();
    stop = true;
    update.join();
    check(!torn.load(), std::string(target) + " overlap: the update never saw a half-written component");
    check(writes.load() + busy.load() == 8000, std::string(target) + " overlap: every attempt immediate");
    // real overlap happened: both writers made progress and the update ran frames (not an all-refused or no-update pass)
    check(writes.load() > 0 && busy.load() > 0 && frames.load() > 0,
          std::string(target) + " overlap: writers and the update all made progress (" + std::to_string(writes.load()) +
              " writes, " + std::to_string(frames.load()) + " frames)");
    check(word(e.scene_word()) == 0 && word(e.registry_word()) == 0, std::string(target) + " overlap: words free at the end");
    std::printf("%s overlap: %ld writes, %ld busy, %ld frames, %ld update waits behind a writer\n", target, writes.load(),
                busy.load(), frames.load(), waits_in_update.load());
}
// gate_stats hold time covers the whole Scene exclusive interval, on every release path: a successful empty hold and a
// refusal after both words were taken (a stale view) both add to the total; a refusal before the Scene word does not.
void hold_time(const char* target) {
    const Layout& L = layout_of(target);
    const std::string t = std::string(target) + ": hold time: ";
    fake::Engine e(L);
    EcsView v{};
    check(ecs_view(&L, ViewKind::Authority, v, e.image.at()) == Reason::None, t + "view");
    {
        const std::uint64_t total = stats().hold_ns_total.load();
        GateState g = fresh();
        check(gate_acquire(&L, v, g, CallerThread::Other, e.image.at()) == Reason::None, t + "empty hold acquires");
        gate_release(g);
        check(stats().hold_ns_total.load() > total, t + "a successful empty hold adds to the hold total");
    }
    {
        // a stale expected view: refused after both words are taken; the exclusive interval is still counted
        const std::uint64_t total = stats().hold_ns_total.load();
        EcsView stale = v;
        stale.manager += 8;
        GateState g = fresh();
        check(gate_acquire(&L, stale, g, CallerThread::Other, e.image.at()) == Reason::EcsWriteOwnerChangedOrNotAuthoritative,
              t + "a stale view is refused after the Scene word");
        check(stats().hold_ns_total.load() > total, t + "a hold that then refuses is still counted");
    }
    {
        // the Scene word held by the update: refused before our Scene CAS; no hold interval
        const std::uint64_t total = stats().hold_ns_total.load();
        __atomic_store_n(fake::word(e.scene_word()), 1, __ATOMIC_SEQ_CST);
        GateState g = fresh();
        check(gate_acquire(&L, v, g, CallerThread::Other, e.image.at()) == Reason::EcsSceneFrameBusy,
              t + "Scene busy before our CAS");
        check(stats().hold_ns_total.load() == total, t + "a refusal before the Scene word adds no hold time");
        __atomic_store_n(fake::word(e.scene_word()), 0, __ATOMIC_SEQ_CST);
    }
    check(word(e.registry_word()) == 0 && word(e.scene_word()) == 0, t + "words free at the end");
}

// Kiln's admission: one Kiln holder, the per-owner busy latch, closing at STOPPING and exit, the holder's priority.
void hardening(const char* target) {
    const Layout& L = layout_of(target);
    const std::string t = std::string(target) + ": hardening: ";
    fake::Engine e(L);
    EcsView v{};
    check(ecs_view(&L, ViewKind::Authority, v, e.image.at()) == Reason::None, t + "view");
    const auto acquire = [&](GateState& g) { return gate_acquire(&L, v, g, CallerThread::Other, e.image.at()); };
    // runs `call` and says whether it read no engine memory and took no word
    const auto untouched = [&](auto call) {
        const std::uint64_t reads = mem::test_reads.load(), cas = mem::test_cas.load();
        call();
        return mem::test_reads.load() == reads && mem::test_cas.load() == cas;
    };
    Reason r{};
    // one Kiln holder: the second is refused at once, and that busy latches its owner for the step
    GateState a = gate_for(1, 10), b = gate_for(2, 10);
    check(acquire(a) == Reason::None, t + "first holder");
    check(untouched([&] { r = acquire(b); }) && r == Reason::KilnHolderBusy, t + "second Kiln holder: kiln-gate-busy, untouched");
    gate_release(a);
    check(untouched([&] { r = acquire(b); }) && r == Reason::KilnBusyLatched, t + "the same owner in the same step: latched, untouched");
    b.step = 11;
    check(acquire(b) == Reason::None && b.owner == 2 && b.step == 11, t + "a later step clears the latch");
    gate_release(b);
    check(b.size == sizeof b && b.owner == 2 && b.step == 11, t + "release keeps size, owner and step");
    // the engine's own holders latch too, per owner and step; successes never latch
    __atomic_store_n(fake::word(e.scene_word()), 1, __ATOMIC_SEQ_CST);  // the update holds the Scene word Shared
    GateState c = gate_for(3, 20), d = gate_for(4, 20);
    check(!untouched([&] { r = acquire(c); }) && r == Reason::EcsSceneFrameBusy, t + "Scene busy: the engine word was tried");
    check(untouched([&] { r = acquire(c); }) && r == Reason::KilnBusyLatched, t + "then latched for that owner and step");
    check(!untouched([&] { r = acquire(d); }) && r == Reason::EcsSceneFrameBusy, t + "another owner is not latched");
    c.step = 21;
    check(!untouched([&] { r = acquire(c); }) && r == Reason::EcsSceneFrameBusy, t + "the next step tries the engine again");
    __atomic_store_n(fake::word(e.scene_word()), 0, __ATOMIC_SEQ_CST);
    check(untouched([&] { r = acquire(c); }) && r == Reason::KilnBusyLatched, t + "still latched in step 21 after the word frees");
    c.step = 22;
    check(acquire(c) == Reason::None, t + "step 22 acquires");
    gate_release(c);
    check(acquire(c) == Reason::None, t + "a success does not latch: a second write in the same step");
    gate_release(c);
    __atomic_store_n(fake::word(e.registry_word()), static_cast<std::int32_t>(0x80000000u), __ATOMIC_SEQ_CST);
    GateState f = gate_for(5, 30);
    check(acquire(f) == Reason::EcsThreadRegistryBusy && untouched([&] { r = acquire(f); }) && r == Reason::KilnBusyLatched,
          t + "registry busy latches as well");
    __atomic_store_n(fake::word(e.registry_word()), 0, __ATOMIC_SEQ_CST);
    // closing: a stopping server world closes until it starts again; exit closes for good. A closed refusal is caught
    // before the holder and the priority boost, so a concurrent acquire still sees the gate free and nobody is boosted.
    GateState g = gate_for(6, 40);
    mem::test_priority = -1;
    const int closed_boosts = mem::test_boosts;
    gate_close_session(true);
    check(untouched([&] { r = acquire(g); }) && r == Reason::KilnGateClosed && mem::test_priority == -1 &&
              mem::test_boosts == closed_boosts,
          t + "STOPPING: kiln-gate-closed, untouched, no holder taken, not boosted");
    gate_close_session(false);
    check(acquire(g) == Reason::None, t + "STARTED: open again");
    {
        bool done = false;
        std::uint64_t took = 0;
        std::thread exiting([&] {
            const auto begin = std::chrono::steady_clock::now();
            done = gate_close_for_exit(20'000);
            took = static_cast<std::uint64_t>(
                std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - begin).count());
        });
        exiting.join();
        check(!done && took < 50'000'000, t + "exit with a holder: gives up after its bound (" + std::to_string(took) + " ns)");
    }
    gate_release(g);
    check(gate_close_for_exit(20'000), t + "exit with no holder: done");
    gate_close_session(false);
    check(untouched([&] { r = acquire(g); }) && r == Reason::KilnGateClosed, t + "exit stays closed after a session reopens");
    gate_reset_for_tests();
    // the holder's priority: raised before the first engine CAS, restored after the last release
    mem::test_priority = -1;
    const int boosts = mem::test_boosts, restores = mem::test_restores;
    GateState h = gate_for(7, 50);
    const std::uint64_t cas = mem::test_cas.load();
    check(acquire(h) == Reason::None && mem::test_priority == 1 && mem::test_boost_cas == cas,
          t + "held above normal; boosted before the first CAS");
    const std::uint64_t releases = mem::test_releases.load();
    gate_release(h);
    check(mem::test_priority == -1 && mem::test_restore_releases == releases + 2 && mem::test_restores == restores + 1,
          t + "restored after both words were released");
    __atomic_store_n(fake::word(e.scene_word()), 1, __ATOMIC_SEQ_CST);
    GateState k = gate_for(8, 60);
    check(acquire(k) == Reason::EcsSceneFrameBusy && mem::test_priority == -1 && mem::test_restores == restores + 2,
          t + "a busy refusal after the registry word restores the priority");
    const int boosts_before = mem::test_boosts;
    check(acquire(k) == Reason::KilnBusyLatched && mem::test_boosts == boosts_before, t + "refusals before any word never boost");
    __atomic_store_n(fake::word(e.scene_word()), 0, __ATOMIC_SEQ_CST);
    mem::test_priority = 1;
    GateState m = gate_for(9, 70);
    const int restores_before = mem::test_restores;
    check(acquire(m) == Reason::None && mem::test_boosts == boosts_before, t + "already above normal: no boost");
    gate_release(m);
    check(mem::test_priority == 1 && mem::test_restores == restores_before, t + "and nothing to restore");
    mem::test_priority = -1;
    check(boosts < mem::test_boosts && word(e.registry_word()) == 0 && word(e.scene_word()) == 0, t + "words free at the end");
    // kiln.engine's measurement switch turned the raise off: a holder keeps its thread's own priority
    gate_set_priority_raise(false);
    const int off_boosts = mem::test_boosts, off_restores = mem::test_restores;
    GateState o = gate_for(10, 80);
    check(acquire(o) == Reason::None && mem::test_priority == -1 && mem::test_boosts == off_boosts,
          t + "raise off: held at the thread's own priority");
    gate_release(o);
    check(mem::test_priority == -1 && mem::test_restores == off_restores, t + "raise off: nothing to restore");
    gate_set_priority_raise(true);
    gate_reset_for_tests();
    check(gate_priority_raise(), t + "a reset turns the raise back on");
}

// The closing protocol under TSan: once exit's close returned "no holder", no acquire may succeed any more.
void exit_race(const char* target) {
    const Layout& L = layout_of(target);
    fake::Engine e(L);
    EcsView v{};
    check(ecs_view(&L, ViewKind::Authority, v, e.image.at()) == Reason::None, "exit race: view");
    std::atomic<bool> closed{false}, stop{false};
    std::atomic<long> late{0}, holds{0};
    std::thread spinner([&] {
        for (std::uint64_t step = 1; !stop.load(); ++step) {
            GateState g = gate_for(11, step);
            if (gate_acquire(&L, v, g, CallerThread::Other, e.image.at()) == Reason::None) {
                if (closed.load()) ++late;
                ++holds;
                gate_release(g);
            }
        }
    });
    while (holds.load() < 100) std::this_thread::yield();
    const bool done = gate_close_for_exit(1'000'000'000);  // the test's own bound: the protocol, not the timing
    closed = true;
    for (int i = 0; i < 2000; ++i) std::this_thread::yield();
    stop = true;
    spinner.join();
    check(done && !late.load(), std::string(target) + " exit race: no hold after close returned (" +
                                    std::to_string(holds.load()) + " holds before)");
    check(word(e.registry_word()) == 0 && word(e.scene_word()) == 0, std::string(target) + " exit race: words free");
    gate_reset_for_tests();
}

// Exit closes in the window between the first closed() check and the holder CAS.
// The recheck after the CAS must refuse: no hold may begin after exit decided there is no Kiln holder.
std::atomic<int> g_point_calls{0};
void exit_window(const char* target) {
    const Layout& L = layout_of(target);
    const std::string t = std::string(target) + ": exit window: ";
    fake::Engine e(L);
    EcsView v{};
    check(ecs_view(&L, ViewKind::Authority, v, e.image.at()) == Reason::None, t + "view");
    static std::atomic<bool> closed_in_window;
    closed_in_window = false;
    g_gate_test_point = +[](GatePoint p) {
        if (p == GatePoint::BeforeHolder && !closed_in_window.exchange(true)) closed_in_window = gate_close_for_exit(0);
    };
    GateState g = gate_for(21, 1);
    const Reason r = gate_acquire(&L, v, g, CallerThread::Other, e.image.at());
    g_gate_test_point = nullptr;
    check(closed_in_window.load(), t + "exit closed inside the window and saw no holder");
    check(r == Reason::KilnGateClosed && !g.held && !gate_holder_taken(), t + "the acquire refuses kiln-gate-closed (got " +
                                                                            std::string(reason_name(r)) + ")");
    check(word(e.registry_word()) == 0 && word(e.scene_word()) == 0, t + "no engine word was taken");
    gate_release(g);
    gate_reset_for_tests();
    // an already-closed gate refuses before it ever reaches for Kiln's holder (a concurrent acquirer sees it free)
    g_point_calls = 0;
    g_gate_test_point = +[](GatePoint p) { if (p == GatePoint::BeforeHolder) ++g_point_calls; };
    gate_close_session(true);
    GateState c = gate_for(22, 1);
    const Reason closed = gate_acquire(&L, v, c, CallerThread::Other, e.image.at());
    g_gate_test_point = nullptr;
    check(closed == Reason::KilnGateClosed && g_point_calls.load() == 0, t + "a closed gate refuses before the holder");
    gate_reset_for_tests();
}

// A RetryKilnHolder caller is never latched, and a refusal caused only by another Kiln holder is retried (bounded,
// no pause) before any engine word.
void retry_holder_admission(const char* target) {
    const Layout& L = layout_of(target);
    const std::string t = std::string(target) + ": RetryKilnHolder admission: ";
    fake::Engine e(L);
    EcsView v{};
    check(ecs_view(&L, ViewKind::Authority, v, e.image.at()) == Reason::None, t + "view");
    const auto acquire = [&](GateState& g) { return gate_acquire(&L, v, g, CallerThread::Other, e.image.at()); };
    const auto retrying = [](std::uint64_t owner, std::uint64_t step) {
        GateState g = gate_for(owner, step);
        g.admission = static_cast<std::uint32_t>(GateAdmission::RetryKilnHolder);
        return g;
    };
    // another Kiln holder for the whole attempt: refused after the bounded retry, no engine word, and not latched
    GateState kiln = gate_for(30, 7);
    check(acquire(kiln) == Reason::None, t + "a Kiln holder");
    GateState a = retrying(31, 7);
    g_point_calls = 0;
    g_gate_test_point = +[](GatePoint p) { if (p == GatePoint::HolderRetry) ++g_point_calls; };
    const std::uint64_t cas = mem::test_cas.load();
    const Reason busy = acquire(a);
    g_gate_test_point = nullptr;
    check(busy == Reason::KilnHolderBusy && mem::test_cas.load() == cas && !a.held,
          t + "refused busy without touching an engine word (got " + std::string(reason_name(busy)) + ")");
    check(g_point_calls.load() == 127, t + "retried 128 times in all (" + std::to_string(g_point_calls.load()) + " retries)");
    gate_release(kiln);
    check(acquire(a) == Reason::None, t + "the same owner in the same step is not latched: it acquires");
    gate_release(a);
    // the other holder leaves during the retries: the retrying caller wins
    GateState k2 = gate_for(30, 8);
    check(acquire(k2) == Reason::None, t + "a second Kiln holder");
    static GateState* leaving;
    leaving = &k2;
    g_gate_test_point = +[](GatePoint p) { if (p == GatePoint::HolderRetry && leaving) { gate_release(*leaving); leaving = nullptr; } };
    GateState b = retrying(32, 8);
    const Reason won = acquire(b);
    g_gate_test_point = nullptr;
    check(won == Reason::None && b.held, t + "acquired once the other Kiln holder left (got " + std::string(reason_name(won)) + ")");
    gate_release(b);
    // engine-word busy (the update holds the Scene word): refused, and not latched for a retrying caller
    __atomic_store_n(fake::word(e.scene_word()), 1, __ATOMIC_SEQ_CST);
    GateState c = retrying(33, 9);
    check(acquire(c) == Reason::EcsSceneFrameBusy, t + "Scene busy");
    __atomic_store_n(fake::word(e.scene_word()), 0, __ATOMIC_SEQ_CST);
    check(acquire(c) == Reason::None, t + "the next attempt in the same step tries the engine again and wins");
    gate_release(c);
    // a Kiln mod caller in the same situation is latched
    __atomic_store_n(fake::word(e.scene_word()), 1, __ATOMIC_SEQ_CST);
    GateState d = gate_for(34, 9);
    check(acquire(d) == Reason::EcsSceneFrameBusy, t + "Kiln mod: Scene busy");
    __atomic_store_n(fake::word(e.scene_word()), 0, __ATOMIC_SEQ_CST);
    check(acquire(d) == Reason::KilnBusyLatched, t + "Kiln mod: latched for the step");
    check(word(e.registry_word()) == 0 && word(e.scene_word()) == 0, t + "words free at the end");
    gate_reset_for_tests();
}

// The gate's own registry release is probed too, on a successful hold and on a
// refusal that holds only the registry word (Scene busy); Kiln's holder is still taken at each.
void holder_order(const char* target) {
    const Layout& L = layout_of(target);
    const std::string t = std::string(target) + ": holder order: ";
    fake::Engine e(L);
    EcsView v{};
    check(ecs_view(&L, ViewKind::Authority, v, e.image.at()) == Reason::None, t + "view");
    const auto acquire = [&](GateState& g) { return gate_acquire(&L, v, g, CallerThread::Other, e.image.at()); };
    const std::uint64_t late = mem::test_releases_without_holder.load();
    std::uint64_t shared = mem::test_gate_shared_releases.load();
    GateState ok = gate_for(60, 1);
    check(acquire(ok) == Reason::None, t + "a hold");
    gate_release(ok);
    check(mem::test_gate_shared_releases.load() > shared, t + "a successful hold's registry release was probed");
    shared = mem::test_gate_shared_releases.load();
    __atomic_store_n(fake::word(e.scene_word()), 1, __ATOMIC_SEQ_CST);  // the update holds the Scene word
    GateState busy = gate_for(61, 1);
    check(acquire(busy) == Reason::EcsSceneFrameBusy, t + "Scene busy: a refusal holding only the registry word");
    __atomic_store_n(fake::word(e.scene_word()), 0, __ATOMIC_SEQ_CST);
    check(mem::test_gate_shared_releases.load() > shared, t + "the registry-only refusal's release was probed");
    check(mem::test_releases_without_holder.load() == late, t + "both released their words before Kiln's holder");
    check(word(e.registry_word()) == 0 && word(e.scene_word()) == 0, t + "words free at the end");
    gate_reset_for_tests();
}

// With the live server step known, the latch keys on it, not on the caller's step.
void live_step_latch(const char* target) {
    const Layout& L = layout_of(target);
    const std::string t = std::string(target) + ": live step: ";
    fake::Engine e(L);
    EcsView v{};
    check(ecs_view(&L, ViewKind::Authority, v, e.image.at()) == Reason::None, t + "view");
    const auto acquire = [&](GateState& g) { return gate_acquire(&L, v, g, CallerThread::Other, e.image.at()); };
    gate_set_server_step(500);
    __atomic_store_n(fake::word(e.scene_word()), 1, __ATOMIC_SEQ_CST);
    GateState g = gate_for(40, 1);
    check(acquire(g) == Reason::EcsSceneFrameBusy, t + "busy at live step 500");
    __atomic_store_n(fake::word(e.scene_word()), 0, __ATOMIC_SEQ_CST);
    g.step = 2;  // a caller bumping its own step does not bypass the latch
    check(acquire(g) == Reason::KilnBusyLatched, t + "a bumped caller step is still latched in the live step");
    gate_set_server_step(501);
    g.step = 1;  // a stale caller step does not keep it latched once the server moved on
    check(acquire(g) == Reason::None, t + "the next live step clears it, whatever step the caller passed");
    gate_release(g);
    check(word(e.registry_word()) == 0 && word(e.scene_word()) == 0, t + "words free at the end");
    gate_reset_for_tests();
}

// STOPPING waits a bounded few microseconds for a holder in flight and says
// so; kiln-gate-busy counts as a Kiln refusal, not as engine Scene contention.
void stopping_and_stats(const char* target) {
    const Layout& L = layout_of(target);
    const std::string t = std::string(target) + ": stopping/stats: ";
    fake::Engine e(L);
    EcsView v{};
    check(ecs_view(&L, ViewKind::Authority, v, e.image.at()) == Reason::None, t + "view");
    const auto acquire = [&](GateState& g) { return gate_acquire(&L, v, g, CallerThread::Other, e.image.at()); };
    GateState h = gate_for(50, 1);
    check(acquire(h) == Reason::None, t + "a holder");
    const std::uint64_t scene = stats().busy_scene.load(), other = stats().refused_other.load();
    GateState x = gate_for(51, 1);
    check(acquire(x) == Reason::KilnHolderBusy, t + "a second Kiln caller");
    check(stats().busy_scene.load() == scene && stats().refused_other.load() == other + 1,
          t + "kiln-gate-busy is counted in refused_other, not busy_scene");
    check(!gate_close_session(true, 20'000), t + "STOPPING with a holder in flight reports it after its bound");
    gate_release(h);
    gate_close_session(false);
    check(gate_close_session(true, 20'000), t + "STOPPING with no holder: none in flight");
    gate_reset_for_tests();
}
}  // namespace

namespace kiln::engine {
ImageFacts gather_facts(const KilnApi*) { return {}; }  // engine.cpp is linked for stats(); never booted here
}  // namespace kiln::engine

int main() {
    mem::test_holder_probe = &gate_holder_taken;  // every engine word is released while Kiln's holder is still taken
    g_gate_release_point = +[](bool begin) { mem::test_in_gate_release = begin; };
    for (const char* t : {"enshrouded.exe", "enshrouded_server.exe"}) {
        refusals(t);
        blocked_update(t);
        hold_time(t);
        overlap(t);
        hardening(t);
        exit_race(t);
        exit_window(t);
        retry_holder_admission(t);
        live_step_latch(t);
        stopping_and_stats(t);
        holder_order(t);
    }
    check(mem::test_releases_without_holder.load() == 0,
          "the gate's engine words were always released before Kiln's holder (" +
              std::to_string(mem::test_releases_without_holder.load()) + " after)");
    const Stats& s = stats();
    check(s.attempts.load() > 0 && s.acquired.load() > 0 && s.busy_scene.load() > 0 && s.busy_registry.load() > 0 &&
              s.refused_in_frame.load() > 0 && s.refused_world_thread.load() > 0 && s.hold_ns_max.load() > 0 &&
              s.hold_ns_total.load() >= s.hold_ns_max.load(),
          "gate stats count every kind and the hold time");
    std::printf("engine gate: %d checks, %d failures (max hold %llu ns)\n", checks.load(), failures.load(),
                static_cast<unsigned long long>(s.hold_ns_max.load()));
    return failures ? 1 : 0;
}
