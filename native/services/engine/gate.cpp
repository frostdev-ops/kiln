// SPDX-License-Identifier: MIT
// Adapted from ShroudForge (MIT, Copyright (c) 2026 bonsaibauer),
// modules/game-runtime/native/engine-context/lock_scene_access.cpp:84-195 (EcsWriteGuard: TryAcquire, Validate,
// ComponentSupported, TrackChange, Release) and :53-63 (tracking_call); gate_write is ecs/read_entity_membership.cpp:52-58
// (guarded_copy) bound to a held gate. Differences, all safety-only:
// - the gate state is the caller's record (no unique_ptr, no allocation at all; ShroudForge allocates before it takes
//   the words, Kiln never does);
// - a caller on a world thread is refused before any word is touched: the server world thread already holds the Scene
//   word Shared for its whole update, so an exclusive try there can never win; a client or prediction world thread is
//   the game's main thread;
// - acquisition/hold counters for kiln.engine gate_stats;
// - Kiln's own admission before any engine word:
//   one Kiln holder at a time (kiln-gate-busy), a per-owner busy latch for the rest of a server step
//   (kiln-gate-busy-this-step), closing at a stopping server world and at process exit (kiln-gate-closed), and the
//   holder raised above normal priority so the engine's normal-priority threads cannot preempt it while the
//   GameServer thread spins on the word (kiln.engine's measurement switch can turn the raise off for a run, to
//   measure what it buys).
#include "gate.h"

#include <atomic>
#include <chrono>
#include <cstring>

#include "engine_state.h"
#include "memory.h"
#include "resolve.h"

namespace kiln::engine {
namespace {
std::uint64_t now_ns() {
    return static_cast<std::uint64_t>(
        std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
}
void max_into(std::atomic<std::uint64_t>& slot, std::uint64_t value) {
    std::uint64_t seen = slot.load(std::memory_order_relaxed);
    while (value > seen && !slot.compare_exchange_weak(seen, value, std::memory_order_relaxed)) {}
}
Reason refuse(GateState& gate, Reason why) {
    gate_release(gate);
    return why;
}

// 1 while a Kiln thread may hold an engine word: from before its first CAS until after its last release.
std::atomic<std::uint32_t> g_holder{0};
std::atomic<bool> g_session_closed{false}, g_exit_closed{false};
bool closed() { return g_exit_closed.load(std::memory_order_seq_cst) || g_session_closed.load(std::memory_order_seq_cst); }

// The busy latch: per owner, the server step of its last busy refusal. A fixed table of atomics (no allocation, no
// lock); owners sharing a slot can at worst cost each other one extra attempt or one early busy, both normal results.
struct Latch {
    std::atomic<std::uint64_t> owner{0}, step{0};
};
constexpr std::size_t kLatchSlots = 256;
Latch g_latch[kLatchSlots];
bool latched(std::uint64_t owner, std::uint64_t step) {
    const Latch& l = g_latch[owner % kLatchSlots];
    return l.step.load(std::memory_order_acquire) == step && l.owner.load(std::memory_order_acquire) == owner;
}
void latch(std::uint64_t owner, std::uint64_t step) {
    Latch& l = g_latch[owner % kLatchSlots];
    l.owner.store(owner, std::memory_order_relaxed);
    l.step.store(step, std::memory_order_release);
}
std::atomic<std::uint64_t> g_server_step{0};
std::atomic<bool> g_priority_raise{true};  // kiln.engine's measurement switch (engine.cpp) may turn it off at boot
}  // namespace

#ifdef KILN_ENGINE_GATE_TEST_POINTS
void (*g_gate_test_point)(GatePoint) = nullptr;
void (*g_gate_release_point)(bool) = nullptr;
#define KILN_GATE_RELEASE_POINT(begin) \
    do {                                 \
        if (g_gate_release_point) g_gate_release_point(begin); \
    } while (0)
#define KILN_GATE_POINT(p) \
    do {                     \
        if (g_gate_test_point) g_gate_test_point(p); \
    } while (0)
#else
#define KILN_GATE_POINT(p) \
    do {                     \
    } while (0)
#define KILN_GATE_RELEASE_POINT(begin) \
    do {                                 \
    } while (0)
#endif

bool gate_close_session(bool stopping, std::uint64_t wait_ns) noexcept {
    // Same Dekker as exit: the flag first, then the holder. STOPPING normally runs while the engine holds the Scene word
    // Shared, so no Kiln holder can be inside; if one is, this bounded look says so.
    g_session_closed.store(stopping, std::memory_order_seq_cst);
    if (!stopping) return true;
    const std::uint64_t deadline = now_ns() + wait_ns;
    while (g_holder.load(std::memory_order_seq_cst) && now_ns() < deadline) {
    }
    return g_holder.load(std::memory_order_seq_cst) == 0;
}
void gate_set_server_step(std::uint64_t step) noexcept { g_server_step.store(step, std::memory_order_relaxed); }
bool gate_holder_taken() noexcept { return g_holder.load(std::memory_order_seq_cst) != 0; }
void gate_set_priority_raise(bool on) noexcept { g_priority_raise.store(on, std::memory_order_relaxed); }
bool gate_priority_raise() noexcept { return g_priority_raise.load(std::memory_order_relaxed); }

bool gate_close_for_exit(std::uint64_t wait_ns) noexcept {
    // Dekker with gate_acquire: it takes g_holder and then reads the flags; this sets the flag and then reads g_holder
    // (all seq_cst), so either the acquirer sees closed or this sees its hold.
    g_exit_closed.store(true, std::memory_order_seq_cst);
    const std::uint64_t deadline = now_ns() + wait_ns;
    while (g_holder.load(std::memory_order_seq_cst) && now_ns() < deadline) {
    }
    return g_holder.load(std::memory_order_seq_cst) == 0;
}

void gate_reset_for_tests() noexcept {
    g_holder.store(0);
    g_session_closed.store(false);
    g_exit_closed.store(false);
    g_server_step.store(0);
    g_priority_raise.store(true);
    for (Latch& l : g_latch) {
        l.owner.store(0);
        l.step.store(0);
    }
}

void gate_release(GateState& gate) noexcept {
    // The Scene exclusive interval, measured on every release path (acquired_ns is set the moment the Scene CAS wins,
    // before revalidation), so the hold stats cover holds that then refuse too, not only fully validated ones.
    const bool held_scene = gate.scene_lock && gate.acquired_ns;
    KILN_GATE_RELEASE_POINT(true);
    // Scene first, then the registry word (lock_scene_access.cpp:93-96): the registry pins the Scene's lifetime.
    if (gate.scene_lock) mem::release_exclusive(gate.scene_lock);
    if (gate.registry_lock) mem::release_shared(gate.registry_lock);
    if (gate.holder) g_holder.store(0, std::memory_order_seq_cst);  // after the engine words
    KILN_GATE_RELEASE_POINT(false);
    if (gate.boosted) mem::restore_priority(gate.saved_priority);
    if (held_scene) {
        const std::uint64_t held = now_ns() - gate.acquired_ns;
        stats().hold_ns_total.fetch_add(held, std::memory_order_relaxed);
        max_into(stats().hold_ns_max, held);
    }
    const std::uint32_t size = gate.size;
    const std::uint64_t owner = gate.owner, step = gate.step;
    const std::uint32_t admission = gate.admission;
    std::memset(&gate, 0, sizeof gate);
    gate.size = size;
    gate.owner = owner;
    gate.step = step;
    gate.admission = admission;
}

Reason gate_acquire(const Layout* layout, const EcsView& expected, GateState& gate, CallerThread caller, std::uintptr_t image) {
    auto& s = stats();
    s.attempts.fetch_add(1, std::memory_order_relaxed);
    if (gate.held || gate.registry_lock || gate.scene_lock || gate.holder) {  // not released: never touch its words
        s.refused_other.fetch_add(1, std::memory_order_relaxed);
        return Reason::EcsWriteGuardAlreadyHeld;
    }
    if (caller != CallerThread::Other) {
        (caller == CallerThread::ServerWorld ? s.refused_in_frame : s.refused_world_thread).fetch_add(1, std::memory_order_relaxed);
        return caller == CallerThread::ServerWorld   ? Reason::InFrame
               : caller == CallerThread::ClientWorld ? Reason::WorldThread
                                                     : Reason::CallerThreadUnknown;
    }
    const auto other = [&](Reason r) {
        s.refused_other.fetch_add(1, std::memory_order_relaxed);
        return refuse(gate, r);
    };
    if (!layout || !layout->write.enabled) return other(Reason::EcsWriteGateDisabled);
    const auto& w = layout->write;
    if (!image || expected.owners.image != image || expected.role != Role::LocalAuthority)
        return other(Reason::EcsWriteRequiresCurrentLocalAuthority);
    // Kiln's admission, still before any engine word: closed, latched, another Kiln holder.
    if (closed()) return other(Reason::KilnGateClosed);
    // The latch keys on the live server step when kiln.engine knows it (its server Pre), else on the caller's step.
    // A RetryKilnHolder caller is never latched (it schedules its own retries).
    const bool retry_holder = gate.admission == static_cast<std::uint32_t>(GateAdmission::RetryKilnHolder);
    const std::uint64_t live = g_server_step.load(std::memory_order_relaxed);
    const std::uint64_t step = live ? live : gate.step;
    const bool keyed = !retry_holder && gate.owner && step;
    if (keyed && latched(gate.owner, step)) return other(Reason::KilnBusyLatched);
    const auto busy = [&](std::atomic<std::uint64_t>& counter, Reason r) {
        counter.fetch_add(1, std::memory_order_relaxed);
        if (keyed) latch(gate.owner, step);
        return refuse(gate, r);
    };
    KILN_GATE_POINT(GatePoint::BeforeHolder);
    // One Kiln holder at a time. For a RetryKilnHolder caller a refusal caused only by another Kiln holder (not an engine
    // word) gets the shared-acquire retry rule: up to 128 attempts, no pause, before any engine word is touched.
    constexpr int kHolderAttempts = 128;
    std::uint32_t free = 0;
    bool won = g_holder.compare_exchange_strong(free, 1, std::memory_order_seq_cst);
    for (int attempt = 1; !won && retry_holder && attempt < kHolderAttempts; ++attempt) {
        KILN_GATE_POINT(GatePoint::HolderRetry);
        free = 0;
        won = g_holder.compare_exchange_strong(free, 1, std::memory_order_seq_cst);
    }
    // Kiln's own contention, not the engine's: counted with the other Kiln refusals, never as Scene contention.
    if (!won) return busy(s.refused_other, Reason::KilnHolderBusy);
    gate.holder = 1;
    if (closed()) return other(Reason::KilnGateClosed);  // exit may have closed between the first check and the hold
    int saved = 0;
    if (g_priority_raise.load(std::memory_order_relaxed) && mem::boost_priority(&saved)) {
        gate.boosted = 1;
        gate.saved_priority = saved;
    }
    std::uintptr_t address{};
    if (!mem::add(image, w.registry_lock, address) || !mem::try_shared(address))
        return busy(s.busy_registry, Reason::EcsThreadRegistryBusy);
    gate.registry_lock = address;
    // No allocation, caller mutex or queue wait from here until release (lock_scene_access.cpp:151-155).
    RegisteredScene selected{};
    if (const Reason r = resolve_registered_scene(*layout, image, selected); r != Reason::None) return other(r);
    if (!mem::add(selected.scene, w.scene_lock, address) || !mem::try_exclusive(address))
        return busy(s.busy_scene, Reason::EcsSceneFrameBusy);
    gate.scene_lock = address;
    gate.acquired_ns = now_ns();  // the exclusive interval starts here, before revalidation; release times it either way
    EcsView current{};
    RegisteredScene registered_again{};
    std::uint8_t mode{};
    if (resolve_registered_scene(*layout, image, registered_again) != Reason::None || !(registered_again == selected) ||
        resolve_ecs(*layout, current, image) != Reason::None || current.scene_owner != selected.scene ||
        !(current == expected) || !mem::field(current.execution_owner, w.execution_mode, mode) ||
        mode != w.authority_mode)
        return other(Reason::EcsWriteOwnerChangedOrNotAuthoritative);
    gate.maximum_bytes = w.maximum_bytes;
    gate.manager = current.manager;
    gate.registry_owner = current.registry_owner;
    gate.tracker = image + w.tracker;
    if (!mem::add(gate.manager, w.tracking_context, gate.tracking_context)) return other(Reason::EcsChangeTrackerContextInvalid);
    gate.tracker_bytes = w.tracker_bytes;
    gate.tracking_manager_offset = w.tracking_manager;
    gate.tracking_registry_a = w.tracking_registry_a;
    gate.tracking_registry_b = w.tracking_registry_b;
    gate.thread = selected.thread;
    gate.handle = selected.handle;
    gate.expected = expected;
    gate.held = 1;
    s.acquired.fetch_add(1, std::memory_order_relaxed);
    return Reason::None;
}

bool gate_validate(const Layout* layout, const GateState& gate, std::uintptr_t image) {
    if (!layout || !gate.held || !gate.registry_lock || !gate.scene_lock) return false;
    const auto& w = layout->write;
    EcsView current{};
    std::uint8_t mode{};
    std::uintptr_t name{}, handle{}, callback{}, context{}, wanted_context{};
    std::uint64_t length{}, capacity{};
    std::uint32_t stop{};
    std::uint8_t running{};
    return mem::field(gate.thread, w.name_pointer, name) && name == gate.thread + w.inline_name &&
           mem::field(gate.thread, w.name_length, length) && length == w.name_size &&
           mem::field(gate.thread, w.name_capacity, capacity) && capacity == w.name_capacity_value &&
           mem::field(gate.thread, w.thread_handle, handle) && handle == gate.handle &&
           mem::field(gate.thread, w.thread_callback, callback) && callback == gate.expected.owners.image + w.callback &&
           mem::add(gate.expected.scene_owner, w.scene_context, wanted_context) &&
           mem::field(gate.thread, w.thread_context, context) && context == wanted_context &&
           mem::field(gate.thread, w.thread_stop, stop) && stop == 0 &&
           mem::field(gate.expected.scene_owner, w.scene_state, running) && running == w.running_state &&
           resolve_ecs(*layout, current, image) == Reason::None && current == gate.expected &&
           mem::field(current.execution_owner, w.execution_mode, mode) && mode == w.authority_mode;
}

bool gate_component_supported(const GateState& gate, std::uint16_t index) {
    if (!gate.held || !gate.registry_lock || !gate.scene_lock || index >= 1024) return false;
    std::uintptr_t owner{};
    return mem::field(gate.manager, 0, owner) && owner == gate.registry_owner &&
           untracked_component(owner, index, gate.tracking_registry_a, gate.tracking_registry_b);
}

GateWrite gate_write(const GateState& gate, std::uintptr_t address, const void* data, std::size_t bytes) {
    if (!gate.held || !gate.registry_lock || !gate.scene_lock || (!data && bytes)) return GateWrite::NotHeld;
    if (!bytes) return GateWrite::Ok;
    // A component larger than the profile's write budget is refused before any mutation (as in ShroudForge)
    // (write_entity_components.cpp:143-146, ecs-write-size-exceeds-gate-budget).
    if (bytes > gate.maximum_bytes) return GateWrite::TooLarge;
    return mem::write_span(address, data, bytes) ? GateWrite::Ok : GateWrite::Faulted;
}

bool gate_track_change(const GateState& gate, std::uintptr_t entity, std::uint16_t index) {
    std::uintptr_t manager{};
    if (!entity || !gate_component_supported(gate, index) ||
        !mem::field(gate.tracking_context, gate.tracking_manager_offset, manager) || manager != gate.manager)
        return false;
    // Both tracking category bits are absent: the original recorder has no queue, stamp, alias or allocator effect for
    // this component; repeat the policy after it returns (lock_scene_access.cpp:116-125).
    std::array<std::uint8_t, 33> bytes{};
    if (!mem::read_span(gate.tracker, bytes.data(), bytes.size()) || bytes != gate.tracker_bytes) return false;
    return mem::call_tracker(gate.tracker, gate.manager, gate.tracking_context, entity, index) &&
           gate_component_supported(gate, index);
}

}  // namespace kiln::engine
