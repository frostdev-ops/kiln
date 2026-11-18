// SPDX-License-Identifier: MIT
// The engine's original write gates, held by a Kiln service on a non-world caller thread (any-thread primitive).
#pragma once
#include <cstddef>
#include <cstdint>

#include "include/kiln_engine_layout.h"
#include "include/kiln_engine_view.h"

namespace kiln::engine {

// The caller-owned record (kiln_engine_view.h). No allocation anywhere in the gate.
using GateState = EcsGate;

// Where the caller runs, from KilnApi.on_world_thread: never inside a world's own thread. Unknown (the core has no
// on_world_thread) refuses like a world thread. Engine job workers running a world program are none of the world
// threads: they reach the exclusive try and get ecs-scene-frame-busy (the update holds the word), never a wait.
enum class CallerThread : std::uint32_t { Other = 0, ServerWorld = 1, ClientWorld = 2, Unknown = 3 };

// EcsWriteGuard::TryAcquire (adapted from ShroudForge) with Kiln's world-thread refusal first. `layout` NULL: no exact profile.
Reason gate_acquire(const Layout* layout, const EcsView& expected, GateState& gate, CallerThread caller, std::uintptr_t image);
bool gate_validate(const Layout* layout, const GateState& gate, std::uintptr_t image);
bool gate_component_supported(const GateState& gate, std::uint16_t index);
bool gate_track_change(const GateState& gate, std::uintptr_t entity, std::uint16_t index);
// guarded_copy (adapted from ShroudForge) for a member's write inside the hold (write_entity_components.cpp:87-98):
// - NotHeld: the gate is not held, or data is NULL with bytes > 0 (no mutation);
// - TooLarge: bytes over the profile's per-component budget (gate.maximum_bytes), refused before any mutation
//   (write_entity_components.cpp:143-146);
// - Faulted: the copy faulted; a partial write may have happened, so the member restores the original, reads it back
//   and latches "uncertain" when the readback does not prove it;
// - Ok: the bytes were written (or bytes was 0).
enum class GateWrite { Ok, NotHeld, TooLarge, Faulted };
GateWrite gate_write(const GateState& gate, std::uintptr_t address, const void* data, std::size_t bytes);
void gate_release(GateState& gate) noexcept;

// Closing: a stopping server world closes the gate until it starts again (world STOPPING/STARTED);
// process exit closes it for good and waits at most `wait_ns` for a Kiln holder to finish (true: none left). Both
// make later acquires refuse kiln-gate-closed before any engine word.
// gate_close_session(true, wait_ns) also waits at most `wait_ns` for a Kiln holder already in flight (false: one was
// still holding when it gave up; the caller logs it, outside any hold).
bool gate_close_session(bool stopping, std::uint64_t wait_ns = 0) noexcept;
bool gate_close_for_exit(std::uint64_t wait_ns) noexcept;
// The live server step (from the core's server Pre); the busy latch keys on it when known (nonzero), else on the
// caller's EcsGate.step.
void gate_set_server_step(std::uint64_t step) noexcept;
// True while a Kiln thread may hold an engine word (tests: the holder is released only after both words).
bool gate_holder_taken() noexcept;
// The holder's priority raise (on by default). Turned off only by kiln.engine's measurement switch, read once at boot
// before any service can hold a gate (engine.cpp); it applies to the whole process. Off, gate_acquire never changes
// the calling thread's priority, so a holder keeps the priority its thread had (a Kiln worker's is below normal).
void gate_set_priority_raise(bool on) noexcept;
bool gate_priority_raise() noexcept;
void gate_reset_for_tests() noexcept;

#ifdef KILN_ENGINE_GATE_TEST_POINTS
// Host tests only: called at fixed points of gate_acquire, before any engine word, to force the racy windows open.
enum class GatePoint : std::uint32_t { BeforeHolder, HolderRetry };
extern void (*g_gate_test_point)(GatePoint);
// Host tests only: gate_release's start (true) and end (false), so a test can tell the gate's own word releases apart.
extern void (*g_gate_release_point)(bool begin);
#endif

}  // namespace kiln::engine
