// SPDX-License-Identifier: MIT
// kiln.world's compare-exchange writes from task threads (ecs_write_policy, ecs_compare_exchange, ecs_write_subscribe,
// ecs_write_unsubscribe in kiln_world.h): a whole component of a server-world entity, compared and swapped under the
// engine's own write gate (kiln.internal.engine's EcsGateScope), between frames only. Portable: no Windows and no
// KilnApi here, so the host tests run this exact code over the real gate and a fake engine (tests/ecs_writes_test.cpp);
// ecs_prod.cpp wires it to the running game.
//
// Adapted from ShroudForge (MIT, Copyright (c) 2026 bonsaibauer): modules/game-runtime/native/ecs/
// write_entity_components.cpp (write_payload: the guarded read, the compare, the merge under a mask, the guarded write,
// the readback and the rollback that is read back too, "uncertain" when nothing proves the bytes). Differences: the
// engine's change recorder is not called (the policy admits untracked components only, for which the recorder on both
// exact builds only reads: docs/re/world.md, "The change recorder for untracked components"); the entity is found
// again inside the hold from the caller's ref; an uncertain target is latched for its session.
//
// Inside the hold (ecs_write_hold.cpp, ecs_lookup.cpp): no wait, allocation, lock, log or KilnApi call, and every loop
// over engine data has a fixed bound (tests/lint_hold.py). Everything that may allocate or lock runs before the gate
// is taken or after it is released.
#pragma once
#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "ecs_engine.h"
#include "ecs_lookup.h"
#include "ecs_write_policy.h"
#include "kiln_engine_native.h"
#include "services/kiln_types.h"
#include "services/kiln_world.h"

namespace kw::ecs::writes {

// ---- an uncertain write latches its target for the session ----
struct Target {
    uint64_t session = 0;
    uint32_t world_kind = 0;
    KilnEntity entity = 0;
    uint32_t generation = 0;
    uint32_t component = 0;
};
// A fixed table of kSlots slots, lock-free and allocation-free. A full table latches the whole session instead (fails
// closed). Entries of an earlier session never match and are reused.
class UncertainLatch {
public:
    static constexpr size_t kSlots = 512;
    bool latched(const Target& t) const noexcept;
    // Latch `t`; false when the table was full and the whole session was latched instead.
    bool latch(const Target& t) noexcept;
    uint64_t widened_session() const noexcept { return widened_.load(std::memory_order_acquire); }
    size_t used(uint64_t session) const noexcept;

private:
    struct Slot {
        std::atomic<uint32_t> state{0};  // 0 empty, 1 being written, 2 ready
        std::atomic<uint64_t> session{0}, id{0}, component{0};
    };
    Slot slots_[kSlots];
    std::atomic<uint64_t> widened_{0};
};

// ---- committed-write events, delivered after the gate was released ----
// A bounded multi-producer ring (sequence-numbered cells, no lock, no allocation after construction). A writer reserves
// capacity before the gate (full: busy), claims its position inside the hold only when the write is proven (one atomic
// add, so positions follow the order of the holds), and publishes the event after the release; an outcome without a
// write gives its capacity back. The drain (one thread at a time) delivers in position order and stops at a position
// claimed but not yet published (the next drain delivers it).
class EventRing {
public:
    static constexpr uint64_t kCells = 4096;
    EventRing();
    bool reserve() noexcept;           // before the gate
    void unreserve() noexcept;         // no event after all
    uint64_t claim() noexcept;         // inside the hold, after a proven write: the event's position
    void publish(uint64_t position, const KilnEcsWriteEvent& event) noexcept;  // after the release
    // Takes up to `max` published events in order into `out`; returns how many.
    size_t take(KilnEcsWriteEvent* out, size_t max) noexcept;
    uint64_t in_use() const noexcept { return used_.load(std::memory_order_acquire); }

private:
    struct Cell {
        std::atomic<uint64_t> sequence{0};
        KilnEcsWriteEvent event{};
    };
    std::unique_ptr<Cell[]> cells_;
    std::atomic<uint64_t> used_{0}, head_{0};
    uint64_t tail_ = 0;  // the drain's next position (one draining thread at a time: Writer::drain's lock)
};

// ---- the hold (ecs_write_hold.cpp) ----
enum class HoldPoint : uint32_t { Begin, AfterLookup, AfterCompare, AfterWrite, AfterRollback };
#ifdef KILN_WORLD_HOLD_TEST_POINTS
extern void (*g_hold_test_point)(HoldPoint);
#endif
struct HoldInput {
    const kiln::engine::EcsGateScope* scope = nullptr;  // held
    engine::Reader* reader = nullptr;
    const ContextFact* ctx = nullptr;
    KilnEntity entity = 0;
    uint32_t generation = 0;
    uint64_t layout_stamp = 0;
    uint16_t index = 0, size = 0;  // the registration row and its size, taken before the gate
    uint64_t probe_bound = 0;      // the id probe's fixed bound inside the hold
    const uint8_t* expected = nullptr;
    const uint8_t* mask = nullptr;  // NULL: every byte from desired
    const uint8_t* desired = nullptr;
    uint8_t* before = nullptr;
    uint8_t* merged = nullptr;
    uint8_t* verified = nullptr;
    const std::atomic<uint64_t>* retirements = nullptr;
    uint64_t retirements_seen = 0;
    EventRing* ring = nullptr;
};
struct HoldOutput {
    uint32_t status = KILN_ECS_UNAVAILABLE;
    uint32_t flags = 0;  // KILN_MUTATION_*
    uint32_t why = KILN_ECS_WRITE_WHY_NONE;
    bool claimed = false;
    uint64_t position = 0;
    uint32_t generation = 0;  // the generation the hold saw
};
void hold(const HoldInput& in, HoldOutput& out) noexcept;

// ---- the writer ----
struct WriterSource {
    // The reads' engine backend (its context, entity table and catalog). Called once per call; the reader it returns
    // outlives the call (ecs_prod.cpp never frees one).
    engine::Reader* (*reader)(void* user) = nullptr;
    // kiln.internal.engine with its gate members (header.size covers ecs_gate_release), or NULL.
    const KilnEngineNativeApi* engine = nullptr;
    const KilnTypesApi* types = nullptr;  // kiln.types (reflection) or NULL
    bool exact_profile = false;
    void* user = nullptr;
    // The live server step (KilnSessionInfo.steps[server]); 0 when unknown.
    uint64_t (*server_step)(void* user) = nullptr;
    // Calls fn(ctx) under the host's fault containment (production: seh_call); false when it faulted.
    bool (*guarded)(void (*fn)(void*), void* ctx, unsigned long* code) = nullptr;
    // A line for kiln.log (never called inside a hold).
    void (*log)(void* user, int level, const char* text) = nullptr;
};

class Writer {
public:
    explicit Writer(const WriterSource& source) : src_(source) {}
    Writer(const Writer&) = delete;
    Writer& operator=(const Writer&) = delete;

    // Whether writes can work on this build at all (exact profile, the gate members, an enabled write gate, the reads).
    bool available() const;
    const char* unavailable_reason() const;

    KilnResult policy(uint32_t world_kind, uint32_t component, KilnEcsWritePolicy* out);
    KilnResult compare_exchange(KilnOwner owner, const KilnObjectRef* ref, uint32_t component, const void* expected,
                                const void* mask, const void* desired, uint32_t bytes, void* confirmed,
                                KilnMutationReply* reply);
    KilnResult subscribe(KilnOwner owner, KilnEcsWriteEventFn fn, void* user, KilnHandle* out);
    // No callback of the subscription starts after it returns. Called from a thread other than the draining one, it
    // also waits for a callback of it that is running there to return; from inside a callback it does not wait.
    KilnResult unsubscribe(KilnHandle handle);

    // Delivers the published events to the live subscriptions, in order, under fault containment. One thread at a time
    // (a second caller returns at once). The server tick's Pre and the server world's STOPPING.
    size_t drain();
    // The owner unloads or reloads (kiln.hooks' owner-retiring observer): its calls answer stale from now on and its
    // subscriptions end. It never waits (the observer must not): a callback of the owner already under way on the
    // draining thread may still finish.
    void owner_retired(KilnOwner owner);

    // For tests and diagnostics.
    UncertainLatch& latch() { return latch_; }
    EventRing& ring() { return ring_; }
    size_t subscriptions() const;
    uint64_t faults() const { return faults_.load(); }
    // Bounds and their test switches.
    uint64_t probe_bound = engine::kMaxProbeForWrites;

private:
    struct Subscription {
        KilnHandle handle = 0;
        KilnOwner owner = 0;
        KilnEcsWriteEventFn fn = nullptr;
        void* user = nullptr;
        std::atomic<bool> alive{true};
        std::atomic<uint32_t> calls{0};  // callbacks of it running now (the drain's; unsubscribe waits for them)
    };
    struct CachedPolicy {
        KilnEcsWritePolicy policy{};
        uint16_t index = 0;
    };
    bool retired(KilnOwner owner) const;
    KilnResult policy_for(engine::Reader& reader, const ContextFact& ctx, bool world, uint32_t component,
                          KilnEcsWritePolicy* out, uint16_t* index);
    void log(int level, const char* text) const;

    WriterSource src_;
    UncertainLatch latch_;
    EventRing ring_;
    mutable std::mutex mu_;  // the policy cache, the retired owners and the subscriptions (never across a hold)
    uint64_t cache_session_ = 0;
    std::unordered_map<uint32_t, CachedPolicy> cache_;
    std::unordered_set<KilnOwner> retired_;
    std::vector<std::shared_ptr<Subscription>> subs_;
    KilnHandle next_handle_ = 0;
    std::atomic<uint64_t> retirements_{0};
    std::mutex drain_mu_;
    std::atomic<std::thread::id> drainer_{};  // the thread inside drain() now (none: default)
    std::atomic<uint64_t> faults_{0};
    std::atomic<bool> widened_logged_{false};
};

} // namespace kw::ecs::writes
