// SPDX-License-Identifier: MIT
// Production wiring of kiln.world's verified-copy ECS reads (ecs_reads.cpp): the engine backend (ecs_engine.cpp) over
// kiln.internal.engine, Kiln's own world facts (KilnApi.session and game_world_session, sampled twice), each owner's
// reload epoch (ecs_epochs.h, dropped when kiln.hooks says the owner retires) and the world.ecs_read capability row.
//
// Entity reads are on only when all of these hold, decided once in on_ready (the row says which one failed):
//   - kiln.engine matched an exact engine profile and kiln.internal.engine is here (a Kiln service DLL of this build);
//   - the profile's read section carries the entity id index and every record and layout-table offset the reads use;
//   - kiln.world's own resolution of the same fields on the running exe (its sigdb constants: the entity map in the
//     world's entity manager, the hash map's fields, the record's id/layout/chunk/row, the layout's offsets and
//     strides) agrees with the profile field by field.
// At run time every read checks more (ecs_engine.cpp): the view names the very world Kiln sees for the kind, every
// registration row's hash is its name's, every stride is its registered size; a check that fails refuses that read.
// Before on_ready, and on every build where reads are off, the context still answers and entity reads refuse
// unavailable.
#include <atomic>
#include <cstring>
#include <string>

#include "ecs_engine.h"
#include "ecs_epochs.h"
#include "ecs_reads.h"
#include "kiln_engine_native.h"
#include "services/kiln_caps.h"
#include "services/kiln_engine.h"
#include "services/kiln_hooks.h"
#include "world_internal.h"
#include "wraps.h"  // game_world_session

namespace kw {
namespace {

ecs::OwnerEpochs g_epochs;
std::atomic<bool> g_reads{false};
std::string g_profile_id;

// Kiln's own facts of a world kind, sampled twice: a session clock or a world that moved between the samples is
// unreadable, and a live world with session 0 would alias "no session" on a ref.
uint32_t world_fact(void*, uint32_t kind, ecs::engine::WorldFact* out) {
    if (kind >= KILN_WORLD_KINDS || !g_api || g_api->size < offsetof(KilnApi, session) + sizeof(void*) || !g_api->session)
        return KILN_ECS_UNAVAILABLE;
    auto read = [](KilnSessionInfo& s) -> bool {
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
    if (first.worlds[kind] != 0 && session_a == 0) return KILN_ECS_UNREADABLE;
    out->world = first.worlds[kind];
    out->session = session_a;
    return KILN_ECS_OK;
}
// The core's step counter of a world kind (bumped in the sim step's Pre, inside the GameServer frame): atomics only.
bool steps_of(void*, uint32_t kind, uint64_t* out) {
    if (kind >= KILN_WORLD_KINDS || !g_api || g_api->size < offsetof(KilnApi, session) + sizeof(void*) || !g_api->session)
        return false;
    KilnSessionInfo s{};
    s.size = sizeof s;
    if (g_api->session(&s) != KILN_OK) return false;
    *out = s.steps[kind];
    return true;
}
uint64_t epoch_of(void*, KilnOwner owner) { return g_epochs.current(owner); }
uint64_t open_epoch(void*, KilnOwner owner, uint64_t epoch) { return g_epochs.open(owner, epoch); }
void KILN_CALL owner_retiring(KilnOwner owner, void*) { g_epochs.retire(owner); }

ecs::engine::Source base_source() {
    ecs::engine::Source s;
    s.world = &world_fact;
    s.epoch = &epoch_of;
    s.open_epoch = &open_epoch;
    s.steps = &steps_of;
    return s;
}

// Readers are never freed: a task thread may be inside one while on_ready publishes its successor.
ecs::engine::Reader g_boot_reader(base_source());
std::atomic<ecs::engine::Reader*> g_reader{&g_boot_reader};

ecs::Backend backend() { return g_reader.load(std::memory_order_acquire)->backend(); }
bool bad_owner(KilnOwner owner) { return owner == 0; }

int64_t constant(const char* name) {
    int64_t v = -1;
    return g_api && g_api->constant && g_api->constant(KilnStr{name, std::strlen(name)}, &v) == KILN_OK ? v : -1;
}

void report(uint32_t state, const std::string& text) {
    if (!g_api || !g_api->service) return;
    const auto* caps = static_cast<const KilnCapsApi*>(
        g_api->service(KilnStr{KILN_CAPS_SERVICE, sizeof(KILN_CAPS_SERVICE) - 1}, KILN_CAPS_VERSION));
    if (!caps || caps->header.size < offsetof(KilnCapsApi, report) + sizeof(void*) || !caps->report) return;
    caps->report(g_owner, KilnStr{"world.ecs_read", sizeof "world.ecs_read" - 1}, state,
                 state == KILN_CAP_AVAILABLE ? KILN_CAP_REASON_NONE : KILN_CAP_REASON_SYMBOL_MISSING,
                 KilnStr{text.data(), text.size()});
}

}  // namespace

bool ecs_prod_available() { return g_reads.load(std::memory_order_acquire); }

void ecs_prod_init() {}  // the boot reader answers the context until on_ready (ecs_prod_ready)

void ecs_prod_ready() {
    ecs::engine::Source s = base_source();
    std::string why;
    if (g_api && g_api->service) {
        const auto* engine = static_cast<const KilnEngineApi*>(
            g_api->service(KilnStr{KILN_ENGINE_SERVICE, sizeof(KILN_ENGINE_SERVICE) - 1}, KILN_ENGINE_VERSION));
        if (engine && engine->identity) {
            KilnEngineIdentity id{};
            id.size = sizeof id;
            if (engine->identity(&id) == KILN_OK && id.status == KILN_ENGINE_EXACT) {
                s.exact_profile = true;
                g_profile_id = id.profile_id;
            }
        }
    }
    char refused[160] = {};
    if (!s.exact_profile) {
        why = "no exact engine profile for this build; entity reads report unavailable";
    } else if (!(s.engine = kiln_engine_native(g_api, g_owner, offsetof(KilnEngineNativeApi, ecs_write_supported),
                                               refused, sizeof refused))) {
        why = std::string(refused) + "; entity reads report unavailable";
    } else {
        s.layout = s.engine->layout();
        if (const char* r = ecs::engine::layout_refusal(s.layout)) {
            why = std::string(r) + "; entity reads report unavailable";
        } else {
            ecs::engine::OwnResolution own;
            own.storage = L.world_storage;
            own.map = L.storage_entities;
            own.map_cap = L.map.cap;
            own.map_values = L.map.values;
            own.map_bits = L.map.bits;
            own.map_keys = L.map.keys;
            own.entity_id = L.entity_id;
            own.entity_layout = L.entity_layout;
            own.entity_chunk = constant("world/entity.chunk");
            own.entity_row = constant("world/entity.index");
            own.layout_offsets = constant("world/layout.offsets");
            own.layout_strides = constant("world/layout.strides");
            const std::string d = ecs::engine::disagreement(*s.layout, own);
            if (!d.empty()) why = d + "; entity reads report unavailable";
            else s.reads = true;
        }
    }
    auto* reader = new ecs::engine::Reader(s);  // process lifetime (see g_reader)
    g_reader.store(reader, std::memory_order_release);
    g_reads.store(s.reads, std::memory_order_release);
    // An owner that retires takes its epoch with it (without kiln.hooks' observer the table stays bounded anyway).
    if (g_api && g_api->service) {
        const auto* hooks = static_cast<const KilnHooksApi*>(
            g_api->service(KilnStr{KILN_HOOKS_SERVICE, sizeof(KILN_HOOKS_SERVICE) - 1}, KILN_HOOKS_VERSION));
        KilnHandle h = 0;
        if (!hooks || hooks->header.size < offsetof(KilnHooksApi, unsubscribe_owner_retiring) + sizeof(void*) ||
            !hooks->subscribe_owner_retiring || hooks->subscribe_owner_retiring(g_owner, &owner_retiring, nullptr, &h) != KILN_OK)
            KW_I("world.ecs_read: no owner-retiring observer here; reload epochs of retired owners age out of a bounded table");
    }
    if (s.reads) {
        const std::string text = "entity and component reads from any thread on " + g_profile_id +
                                 " (the profile's entity table agrees with kiln.world's own resolution); the server "
                                 "world on a dedicated server or a hosting client, and the client world on a client";
        report(KILN_CAP_AVAILABLE, text);
        KW_I("world.ecs_read: on (%s)", g_profile_id.c_str());
    } else {
        report(KILN_CAP_UNAVAILABLE, why);
        KW_I("world.ecs_read: off (%s)", why.c_str());
    }
}

const char* ecs_prod_last_refusal() { return g_reader.load(std::memory_order_acquire)->last_refusal(); }

uint32_t ecs_capability() { return ecs_prod_available() ? KILN_WORLD_CAP_ECS_READ : 0; }

KilnResult KILN_CALL t_entity_open(KilnOwner owner, uint64_t reload_epoch, uint32_t world_kind,
                                   uint64_t expected_session, KilnEntity entity, KilnObjectRef* out) {
    if (bad_owner(owner)) return KILN_ERR_INVALID_ARGUMENT;
    return ecs::entity_open(backend(), owner, reload_epoch, world_kind, expected_session, entity, out);
}
KilnResult KILN_CALL t_entity_validate(KilnOwner owner, const KilnObjectRef* ref, KilnObjectInfo* out) {
    if (bad_owner(owner)) return KILN_ERR_INVALID_ARGUMENT;
    return ecs::entity_validate(backend(), owner, ref, out);
}
KilnResult KILN_CALL t_ecs_context_snapshot(KilnEcsContextInfo* out) {
    return ecs::ecs_context_snapshot(backend(), out);
}
KilnResult KILN_CALL t_ecs_query(KilnOwner owner, const KilnEcsQueryRequest* request, KilnObjectRef* out,
                                 uint32_t capacity, uint32_t* required, uint32_t* state) {
    if (bad_owner(owner)) return KILN_ERR_INVALID_ARGUMENT;
    return ecs::ecs_query(backend(), owner, request, out, capacity, required, state);
}
KilnResult KILN_CALL t_ecs_snapshot(KilnOwner owner, const KilnObjectRef* ref, uint32_t component, void* out,
                                    uint32_t capacity, uint32_t* required) {
    if (bad_owner(owner)) return KILN_ERR_INVALID_ARGUMENT;
    return ecs::ecs_snapshot(backend(), owner, ref, component, out, capacity, required);
}
KilnResult KILN_CALL t_ecs_snapshot_many(KilnOwner owner, const KilnObjectRef* ref,
                                         const KilnEcsSnapshotSpec* specs, uint32_t count, void* out,
                                         uint32_t capacity, uint32_t* required, uint32_t* presence) {
    if (bad_owner(owner)) return KILN_ERR_INVALID_ARGUMENT;
    return ecs::ecs_snapshot_many(backend(), owner, ref, specs, count, out, capacity, required, presence);
}
KilnResult KILN_CALL t_ecs_template_uuid(KilnOwner owner, const KilnObjectRef* ref, uint8_t guid[16]) {
    if (bad_owner(owner)) return KILN_ERR_INVALID_ARGUMENT;
    return ecs::ecs_template_uuid(backend(), owner, ref, guid);
}

}  // namespace kw
