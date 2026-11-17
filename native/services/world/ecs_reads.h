// SPDX-License-Identifier: MIT
// T8 (#53): verified-copy ECS reads behind kiln.world's entity_open / entity_validate / ecs_* members
// (kiln_world.h). The logic is engine-free: a Backend supplies one world's facts and a guarded copy, twice per
// read (before and after the copy), and this file compares them. Production wires a Backend over
// kiln.internal.engine (ecs_prod.cpp); host tests wire a fake engine memory model. A read returns a complete
// result or a precise KILN_ECS_* refusal and never copies torn or partial data: component bytes are staged and
// published only after the second validation. No member posts work to a tick, waits, or holds an engine gate.
#pragma once
#include <cstddef>
#include <cstdint>
#include <vector>

#include "services/kiln_world.h"

namespace kw::ecs {

struct ContextFact {
    uint64_t session = 0;       // game_world_session(world_kind)
    uint64_t layout_stamp = 0;  // the world's component-layout stamp (registry identity)
    uint64_t revision = 0;      // membership revision: changes when entities appear or vanish
    uint64_t reload_epoch = 0;  // current reload epoch; 0 = not tracked (epoch checks skipped)
    bool present = false;       // a world of this kind exists
    bool exact_profile = false; // an exact engine profile matched this build
};

struct EntityFact {
    uint32_t generation = 0;
    uint64_t layout_stamp = 0; // this entity's component layout (archetype) stamp
    bool alive = false;
    bool has_template = false;
    uint8_t template_uuid[16] = {};
};

struct ComponentFact {
    bool present = false;
    uintptr_t address = 0;
    uint32_t size = 0;
    // Changes whenever the bytes at `address` change, and not before those bytes are stable. Sampled by
    // `component` before and after each copy. 0 is a real stamp, not "unknown".
    uint64_t content_stamp = 0;
};

// Every op returns a KILN_ECS_* status. `component`/`entity` may be called several times per read; `copy` is the
// engine gate's guarded copy (false = unreadable; the destination may hold anything, callers stage). `position`
// may be null (spatial bounds then report unavailable). Ops never wait and never call back into this file.
struct Backend {
    void* user = nullptr;
    uint32_t (*context)(void* user, uint32_t world_kind, ContextFact* out);
    uint32_t (*entity)(void* user, const ContextFact& ctx, KilnEntity entity, EntityFact* out);
    uint32_t (*component)(void* user, const ContextFact& ctx, const EntityFact& entity, KilnEntity id,
                          uint32_t component, ComponentFact* out);
    bool (*copy)(void* user, uintptr_t address, void* out, size_t bytes);
    uint32_t (*entity_ids)(void* user, const ContextFact& ctx, std::vector<KilnEntity>* out);
    bool (*position)(void* user, const ContextFact& ctx, KilnEntity entity, double out[3]);
};

uint32_t result_of(uint32_t status); // KILN_ECS_* -> KilnResult (changed queries refine it at the call site)

KilnResult entity_open(const Backend& b, uint64_t reload_epoch, uint32_t world_kind, uint64_t expected_session,
                       KilnEntity entity, KilnObjectRef* out);
KilnResult entity_validate(const Backend& b, const KilnObjectRef* ref, KilnObjectInfo* out);
KilnResult ecs_context_snapshot(const Backend& b, KilnEcsContextInfo* out);
KilnResult ecs_query(const Backend& b, const KilnEcsQueryRequest* request, KilnObjectRef* out, uint32_t capacity,
                     uint32_t* required, uint32_t* state);
KilnResult ecs_snapshot(const Backend& b, const KilnObjectRef* ref, uint32_t component, void* out,
                        uint32_t capacity, uint32_t* required);
KilnResult ecs_snapshot_many(const Backend& b, const KilnObjectRef* ref, const KilnEcsSnapshotSpec* specs,
                             uint32_t count, void* out, uint32_t capacity, uint32_t* required, uint32_t* presence);
KilnResult ecs_template_uuid(const Backend& b, const KilnObjectRef* ref, uint8_t guid[16]);

} // namespace kw::ecs
