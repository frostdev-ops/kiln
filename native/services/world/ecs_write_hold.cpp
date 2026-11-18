// SPDX-License-Identifier: MIT
// The part of a compare-exchange write that runs while kiln.world holds the engine's write gate (ecs_writes.h). No
// wait, allocation, lock, log or KilnApi call in this file, and every loop has a fixed bound: the component's size
// (at most the profile's write budget, checked before the gate) or the id probe's bound (tests/lint_hold.py checks the
// file). Adapted from ShroudForge's write_payload (MIT, Copyright (c) 2026 bonsaibauer;
// modules/game-runtime/native/ecs/write_entity_components.cpp), without the change recorder (see ecs_writes.h).
#include <cstring>

#include "ecs_writes.h"

namespace kw::ecs::writes {

#ifdef KILN_WORLD_HOLD_TEST_POINTS
void (*g_hold_test_point)(HoldPoint) = nullptr;
#define KILN_HOLD_POINT(p) \
    do {                     \
        if (g_hold_test_point) g_hold_test_point(p); \
    } while (0)
#else
#define KILN_HOLD_POINT(p) \
    do {                     \
    } while (0)
#endif

namespace {
using engine::Reader;

// The entity found again from the ref, with the registration row taken before the gate: the same generation and
// layout (archetype) as the ref, the component present, its stride the registered size. Hold-safe (ecs_lookup.cpp).
uint32_t find_again(const HoldInput& in, Reader::Located& at, uint32_t* why) {
    const uint32_t s = in.reader->locate(*in.ctx, in.entity, in.index, in.size, in.probe_bound, &at);
    if (s == KILN_ECS_ABSENT) {
        *why = KILN_ECS_WRITE_WHY_REF;
        return KILN_ECS_ABSENT;
    }
    if (s != KILN_ECS_OK) {
        *why = KILN_ECS_WRITE_WHY_FAULT;
        return KILN_ECS_UNREADABLE;
    }
    if (at.generation != in.generation || at.layout_stamp != in.layout_stamp) {
        *why = KILN_ECS_WRITE_WHY_REF;
        return KILN_ECS_STALE;
    }
    if (!at.present) {
        *why = KILN_ECS_WRITE_WHY_REF;
        return KILN_ECS_ABSENT;
    }
    return KILN_ECS_OK;
}
bool same_place(const Reader::Located& a, const Reader::Located& b) {
    return a.present == b.present && a.address == b.address && a.generation == b.generation &&
           a.layout_stamp == b.layout_stamp;
}
}  // namespace

void hold(const HoldInput& in, HoldOutput& out) noexcept {
    out = HoldOutput{};
    out.generation = in.generation;
    KILN_HOLD_POINT(HoldPoint::Begin);
    const kiln::engine::EcsGateScope& scope = *in.scope;
    const size_t n = in.size;
    // 1. The gate: the GameServer thread, its running Scene and the authority view still hold.
    if (!scope.validate()) {
        out.status = KILN_ECS_UNAVAILABLE;
        out.why = KILN_ECS_WRITE_WHY_GATE;
        return;
    }
    // The view the gate holds is the context's: the same entity manager and registry owner.
    if (scope.gate().manager != in.ctx->manager || scope.gate().registry_owner != in.ctx->registry) {
        out.status = KILN_ECS_STALE;
        out.why = KILN_ECS_WRITE_WHY_REF;
        return;
    }
    // 2. The target again, from the ref.
    Reader::Located at{};
    if (const uint32_t s = find_again(in, at, &out.why); s != KILN_ECS_OK) {
        out.status = s;
        return;
    }
    out.generation = at.generation;
    KILN_HOLD_POINT(HoldPoint::AfterLookup);
    // 3. Still untracked: the gate reads the registry owner's two tracking bit words at the profile's offsets.
    if (!scope.component_supported(in.index)) {
        out.status = KILN_ECS_NOT_WRITABLE;
        out.why = KILN_ECS_WRITE_WHY_TRACKED;
        return;
    }
    // 4. The current bytes (a fault: nothing written).
    if (!in.reader->copy(at.address, in.before, n)) {
        out.status = KILN_ECS_UNREADABLE;
        out.why = KILN_ECS_WRITE_WHY_FAULT;
        return;
    }
    // 5. Compared in full with the caller's expected bytes.
    if (std::memcmp(in.before, in.expected, n) != 0) {
        out.status = KILN_ECS_CONFLICT;
        out.why = KILN_ECS_WRITE_WHY_COMPARE;
        out.flags = KILN_MUTATION_CONFIRMED;
        return;
    }
    KILN_HOLD_POINT(HoldPoint::AfterCompare);
    // 6. The desired bytes merged under the mask; nothing to change is done.
    for (size_t i = 0; i < n; ++i) in.merged[i] = (!in.mask || in.mask[i]) ? in.desired[i] : in.before[i];
    if (std::memcmp(in.merged, in.before, n) == 0) {
        out.status = KILN_ECS_OK;
        out.flags = KILN_MUTATION_NO_OP | KILN_MUTATION_CONFIRMED;
        return;
    }
    // An owner retired since the call began: it may be the caller, so nothing is written.
    if (in.retirements && in.retirements->load(std::memory_order_acquire) != in.retirements_seen) {
        out.status = KILN_ECS_BUSY;
        out.why = KILN_ECS_WRITE_WHY_RETIRE_RACE;
        return;
    }
    // The entity is still where the bytes were read (nothing may move it under the exclusive word; checked anyway).
    {
        Reader::Located again{};
        uint32_t why = 0;
        if (const uint32_t s = find_again(in, again, &why); s != KILN_ECS_OK || !same_place(at, again)) {
            out.status = s != KILN_ECS_OK ? s : static_cast<uint32_t>(KILN_ECS_STALE);
            out.why = s != KILN_ECS_OK ? why : static_cast<uint32_t>(KILN_ECS_WRITE_WHY_REF);
            return;
        }
    }
    // 7. Written under the gate (the budget refuses before any byte).
    const KilnResult w = scope.write(at.address, in.merged, n);
    if (w == KILN_ERR_BUFFER_TOO_SMALL) {
        out.status = KILN_ECS_NOT_WRITABLE;
        out.why = KILN_ECS_WRITE_WHY_OVER_BUDGET;
        return;
    }
    if (w == KILN_ERR_INVALID_ARGUMENT) {  // no held gate: nothing was written
        out.status = KILN_ECS_UNAVAILABLE;
        out.why = KILN_ECS_WRITE_WHY_GATE;
        return;
    }
    KILN_HOLD_POINT(HoldPoint::AfterWrite);
    // 8. Proven: the bytes read back, the gate still valid, the entity still where it was.
    const bool written = w == KILN_OK;
    const bool proven = written && in.reader->copy(at.address, in.verified, n) && std::memcmp(in.verified, in.merged, n) == 0;
    if (proven) {
        Reader::Located again{};
        uint32_t why = 0;
        if (scope.validate() && find_again(in, again, &why) == KILN_ECS_OK && same_place(at, again)) {
            out.status = KILN_ECS_OK;
            out.flags = KILN_MUTATION_WRITTEN | KILN_MUTATION_CONFIRMED;
            out.position = in.ring->claim();
            out.claimed = true;
            return;
        }
        // The gate or the entity moved under an exclusive hold: the address cannot be trusted for a rollback.
        out.status = KILN_ECS_UNCERTAIN;
        out.why = KILN_ECS_WRITE_WHY_READBACK;
        return;
    }
    // 9. Not proven: rolled back in the same hold, and the rollback read back.
    // The readback is the proof, whatever the rollback write returned (a fault may have left part of it written).
    scope.write(at.address, in.before, n);
    KILN_HOLD_POINT(HoldPoint::AfterRollback);
    if (in.reader->copy(at.address, in.verified, n) && std::memcmp(in.verified, in.before, n) == 0) {
        out.status = KILN_ECS_UNREADABLE;
        out.why = written ? KILN_ECS_WRITE_WHY_READBACK : KILN_ECS_WRITE_WHY_FAULT;
        out.flags = KILN_MUTATION_ROLLED_BACK;
        return;
    }
    out.status = KILN_ECS_UNCERTAIN;
    out.why = written ? KILN_ECS_WRITE_WHY_READBACK : KILN_ECS_WRITE_WHY_FAULT;
}

} // namespace kw::ecs::writes
