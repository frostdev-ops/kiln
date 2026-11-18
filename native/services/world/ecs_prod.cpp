// SPDX-License-Identifier: MIT
// Production wiring for the verified-copy ECS reads (ecs_reads.cpp). The engine gate (kiln.internal.engine)
// resolves the owner/Scene/ECS view of an exact build, but no Kiln code yet interprets the profile's entity-table
// fields (entity id/generation/layout/storage, component bits/offsets/strides) as task-thread reads: entity-table
// reads are not implemented or validated against a running game yet. Until they are, the production backend reports every entity
// read unavailable and the capability row says why; it never guesses an offset. Context facts are real (world
// presence and sessions from the core, the exact-profile flag from kiln.engine), so ecs_context_snapshot works
// and every other member refuses precisely. Validating the entity-table semantics on a real build flips
// ecs_prod_available() and fills the entity/component ops; the verified-copy logic does not change.
#include <atomic>
#include <cstring>

#include "ecs_reads.h"
#include "wraps.h" // game_world_session
#include "services/kiln_caps.h"
#include "services/kiln_engine.h"
#include "world_internal.h"

namespace kw {
namespace {

std::atomic<bool> g_exact_profile{false};

uint32_t prod_context(void*, uint32_t kind, ecs::ContextFact* out) {
    out->exact_profile = g_exact_profile.load(std::memory_order_acquire);
    if (kind >= KILN_WORLD_KINDS || !g_api) return KILN_ECS_UNAVAILABLE;
    // Two samples. A session() that fails is unavailable, not an empty world. A world that stops between the
    // two samples is unreadable: the snapshot must not publish a world under a session that has already moved.
    auto read = [](KilnSessionInfo& s) -> bool {
        if (!g_api->session) return false;
        s = KilnSessionInfo{};
        s.size = sizeof s;
        return g_api->session(&s) == KILN_OK;
    };
    KilnSessionInfo first{}, second{};
    const uint64_t session_a = game_world_session(kind);
    if (!read(first) || !read(second)) return KILN_ECS_UNAVAILABLE;
    const uint64_t session_b = game_world_session(kind);
    if (session_a != session_b || first.worlds[kind] != second.worlds[kind] ||
        first.sessions[kind] != second.sessions[kind])
        return KILN_ECS_UNREADABLE;
    // A live world with session 0 would alias "no session" on a ref. The two clocks disagree: unreadable.
    if (first.worlds[kind] != 0 && session_a == 0) return KILN_ECS_UNREADABLE;
    out->present = first.worlds[kind] != 0;
    out->session = session_a;
    // Absence is an answer (present = false). Entity reads still refuse: get_context requires a live world.
    return KILN_ECS_OK;
}
uint32_t prod_unavailable(void*, const ecs::ContextFact&, KilnEntity, ecs::EntityFact*) { return KILN_ECS_UNAVAILABLE; }
uint32_t prod_unavailable_component(void*, const ecs::ContextFact&, const ecs::EntityFact&, KilnEntity, uint32_t,
                                    ecs::ComponentFact*) { return KILN_ECS_UNAVAILABLE; }
uint32_t prod_unavailable_ids(void*, const ecs::ContextFact&, std::vector<KilnEntity>*) { return KILN_ECS_UNAVAILABLE; }
bool prod_copy(void*, uintptr_t, void*, size_t) { return false; }

const ecs::Backend kBackend{nullptr, &prod_context, &prod_unavailable, &prod_unavailable_component,
                            &prod_copy, &prod_unavailable_ids, nullptr};

bool bad_owner(KilnOwner owner) { return owner == 0; }

} // namespace

bool ecs_prod_available() { return false; } // entity-table read semantics are not validated on any build yet

void ecs_prod_init() {
    if (g_api && g_api->service) {
        const auto* engine = static_cast<const KilnEngineApi*>(
            g_api->service(KilnStr{KILN_ENGINE_SERVICE, sizeof(KILN_ENGINE_SERVICE) - 1}, KILN_ENGINE_VERSION));
        if (engine && engine->identity) {
            KilnEngineIdentity id{};
            id.size = sizeof id;
            if (engine->identity(&id) == KILN_OK && id.status == KILN_ENGINE_EXACT) g_exact_profile.store(true);
        }
    }
    if (g_api && g_api->service) {
        const auto* caps = static_cast<const KilnCapsApi*>(
            g_api->service(KilnStr{KILN_CAPS_SERVICE, sizeof(KILN_CAPS_SERVICE) - 1}, KILN_CAPS_VERSION));
        if (caps && caps->header.size >= offsetof(KilnCapsApi, report) + sizeof(void*) && caps->report) {
            const char* text = g_exact_profile.load()
                ? "exact engine profile, but the profile's entity-table reads are not validated for task "
                  "threads on this build; reads report unavailable"
                : "no exact engine profile for this build; reads report unavailable";
            caps->report(g_owner, KilnStr{"world.ecs_read", sizeof "world.ecs_read" - 1}, KILN_CAP_UNAVAILABLE,
                         KILN_CAP_REASON_SYMBOL_MISSING, KilnStr{text, std::strlen(text)});
        }
    }
    KW_I("world.ecs_read: off (%s)", g_exact_profile.load() ? "entity-table reads not validated" : "no exact engine profile");
}

uint32_t ecs_capability() { return ecs_prod_available() ? KILN_WORLD_CAP_ECS_READ : 0; }

KilnResult KILN_CALL t_entity_open(KilnOwner owner, uint64_t reload_epoch, uint32_t world_kind,
                                   uint64_t expected_session, KilnEntity entity, KilnObjectRef* out) {
    if (bad_owner(owner)) return KILN_ERR_INVALID_ARGUMENT;
    return ecs::entity_open(kBackend, reload_epoch, world_kind, expected_session, entity, out);
}
KilnResult KILN_CALL t_entity_validate(KilnOwner owner, const KilnObjectRef* ref, KilnObjectInfo* out) {
    if (bad_owner(owner)) return KILN_ERR_INVALID_ARGUMENT;
    return ecs::entity_validate(kBackend, ref, out);
}
KilnResult KILN_CALL t_ecs_context_snapshot(KilnEcsContextInfo* out) {
    return ecs::ecs_context_snapshot(kBackend, out);
}
KilnResult KILN_CALL t_ecs_query(KilnOwner owner, const KilnEcsQueryRequest* request, KilnObjectRef* out,
                                 uint32_t capacity, uint32_t* required, uint32_t* state) {
    if (bad_owner(owner)) return KILN_ERR_INVALID_ARGUMENT;
    return ecs::ecs_query(kBackend, request, out, capacity, required, state);
}
KilnResult KILN_CALL t_ecs_snapshot(KilnOwner owner, const KilnObjectRef* ref, uint32_t component, void* out,
                                    uint32_t capacity, uint32_t* required) {
    if (bad_owner(owner)) return KILN_ERR_INVALID_ARGUMENT;
    return ecs::ecs_snapshot(kBackend, ref, component, out, capacity, required);
}
KilnResult KILN_CALL t_ecs_snapshot_many(KilnOwner owner, const KilnObjectRef* ref,
                                         const KilnEcsSnapshotSpec* specs, uint32_t count, void* out,
                                         uint32_t capacity, uint32_t* required, uint32_t* presence) {
    if (bad_owner(owner)) return KILN_ERR_INVALID_ARGUMENT;
    return ecs::ecs_snapshot_many(kBackend, ref, specs, count, out, capacity, required, presence);
}
KilnResult KILN_CALL t_ecs_template_uuid(KilnOwner owner, const KilnObjectRef* ref, uint8_t guid[16]) {
    if (bad_owner(owner)) return KILN_ERR_INVALID_ARGUMENT;
    return ecs::ecs_template_uuid(kBackend, ref, guid);
}

} // namespace kw
