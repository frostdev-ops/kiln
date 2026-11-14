// SPDX-License-Identifier: MIT
// kiln.queries (services/kiln_queries.h): the first voxel surface on a segment, and the entities whose origin is inside
// a sphere or a box. Read-only; server world; tick thread. docs/kiln/services/queries.md, docs/re/queries.md.
//
// The ray is the game's own (queries/VoxelRay), called with fixed options {realm, 0, 1, 1}: stop at data that is not
// loaded, and skip thin terrain as the game's ground and sight tests do. Three things the game does not do are done here:
//  - both endpoints are checked against the realm's grid box first (grid.h): outside it the game reads "loaded, empty";
//  - a segment is refused when it has no length as floats (ray.h): the game returns "available, no hit" without looking;
//  - calls are admitted against one budget per published server step (budget.h), because the ray writes the realm's
//    cell cache and may only run on the tick thread.
// A fault in the ray, a grid whose numbers cannot be right, or other bytes at the game's minimum-length constant turn
// queries.voxel_ray off for the process; the entity question does not depend on any of it.
#include <windows.h>

#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

#include "kiln_api.h"
#include "services/kiln_caps.h"
#include "services/kiln_queries.h"
#include "services/kiln_world.h"

#include "budget.h"
#include "config.h"
#include "entities.h"
#include "grid.h"
#include "ray.h"
#include "seh.h"

#include "internal.h"

namespace kq {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
const KilnWorldApi* g_world = nullptr;
const KilnCapsApi* g_caps = nullptr;
GridLayout g_layout{};
std::atomic<bool> g_grid_ok{false};
}  // namespace kq

namespace {

using kq::g_api;
using kq::g_caps;
using kq::g_layout;
using kq::g_owner;
using kq::g_world;

constexpr char kRayFeature[] = "queries.voxel_ray";

#define KQ_STR(s) KilnStr{s, sizeof(s) - 1}

}  // namespace

void kq::logf(int32_t level, const char* fmt, ...) {
    char text[768];
    va_list v;
    va_start(v, fmt);
    const int n = std::vsnprintf(text, sizeof text, fmt, v);
    va_end(v);
    if (n > 0) g_api->log(g_owner, level, KilnStr{text, static_cast<size_t>(n < static_cast<int>(sizeof text) ? n : static_cast<int>(sizeof text) - 1)});
}

namespace {
using kq::logf;

// ---- the ray: what was resolved, and whether it may still be used -------------------------------------------------

uintptr_t g_ray = 0;
std::atomic<bool> g_ray_on{false};  // false from the start, or latched off: never back on in this process
std::string g_ray_start_failure;    // retained until on_ready can report through kiln.caps
kq::Budget g_budget;                // tick thread
// world lifecycle. STARTED comes on the tick thread; STOPPING on whichever thread destroys the world.
std::atomic<uint32_t> g_stopped_session{0};
bool g_in_ray = false;              // tick thread
double g_qpc_ns = 0;                // nanoseconds per performance-counter tick

uint32_t g_grid_session = 0;  // tick thread: the session g_grid was resolved for
uintptr_t g_grid = 0;

void report_ray_off(uint32_t reason, KilnStr why) {
    if (!g_caps || !g_caps->report) return;
    // These compositions cannot outlive the ray. Report them here too, so raw capability readers see a fault at once
    // rather than waiting for the managed library's next periodic refresh (or for someone to call a member).
    for (const char* name : {kRayFeature, "queries.ground", "queries.line_of_sight"})
        g_caps->report(g_owner, KilnStr{name, std::strlen(name)}, KILN_CAP_UNAVAILABLE, reason, why);
}

void ray_off(uint32_t reason, const char* why) {
    if (!g_ray_on.exchange(false)) return;
    logf(KILN_LOG_ERROR, "queries: %s: %s is off for this process", why, kRayFeature);
    report_ray_off(reason, KilnStr{why, std::strlen(why)});
}

bool constant(const char* name, int64_t* out) { return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }

// Everything the ray needs from sigdb, and the game's minimum-length constant as its bytes are now.
bool resolve_ray(const char** why) {
    *why = nullptr;
    if (!g_api->feature || !g_api->feature(KQ_STR(kRayFeature))) return *why = "the feature did not resolve on this game build", false;
    if (g_api->symbol(KQ_STR("queries/VoxelRay"), &g_ray) != KILN_OK || !g_ray) return *why = "symbol queries/VoxelRay is missing", false;
    if (!constant("queries/grid.realms", &g_layout.realms) || !constant("queries/grid.realm_count", &g_layout.realm_count) ||
        !constant("queries/realm.stride", &g_layout.realm_stride) || !constant("queries/ray_grid.origin", &g_layout.origin) ||
        !constant("queries/ray_grid.shift", &g_layout.shift) || !constant("queries/ray_grid.horizontal_count", &g_layout.count_h) ||
        !constant("queries/ray_grid.vertical_count", &g_layout.count_v))
        return *why = "a grid layout constant is missing", false;
    if (!kq::sane_layout(g_layout)) return *why = "the captured grid layout is not a layout", false;
    // sigdb says what the constant should be; only the game's memory says what it is
    int64_t expected = 0;
    uintptr_t at = 0;
    uint32_t bits = 0;
    if (!constant("queries/ray.min_length_bits", &expected) || static_cast<uint32_t>(expected) != kq::kRayMinLengthBits)
        return *why = "constant queries/ray.min_length_bits is not the value this service validates rays against", false;
    if (g_api->symbol(KQ_STR("queries/RayMinLength"), &at) != KILN_OK || !kq::seh_copy(at, &bits, sizeof bits))
        return *why = "the game's minimum ray length cannot be read", false;
    if (bits != kq::kRayMinLengthBits) return *why = "the game's minimum ray length is not 1e-20", false;
    return true;
}

using kq::Clock;

}  // namespace

kq::Clock kq::now() {
    Clock c;
    KilnSessionInfo s{};
    s.size = sizeof s;
    if (g_api->session(&s) == KILN_OK) {
        c.session = s.sessions[KILN_WORLD_SERVER];
        c.world = s.worlds[KILN_WORLD_SERVER] != 0;
    }
    KilnTickInfo t{};
    if (g_api->tick_info(&t) == KILN_OK) c.step = t.step;
    return c;
}

namespace {
using kq::now;
}  // namespace

// There is a server world, it is not being destroyed, and its first Pre was published.
bool kq::world_ready(const Clock& c) { return c.world && c.session != g_stopped_session.load(std::memory_order_acquire) && g_budget.ready(c.session, c.step); }

namespace {
using kq::world_ready;

void KILN_CALL on_world(const KilnWorldEvent* e, void*) {
    if (!e || e->kind != KILN_WORLD_SERVER) return;
    if (e->event == KILN_WORLD_STOPPING) {
        g_stopped_session.store(e->session, std::memory_order_release);
        kq::path_world_closed(e->session);
        return;
    }
    if (e->event != KILN_WORLD_STARTED) return;
    // tick thread, before the world's first Pre: the step published now is the one to get past
    KilnTickInfo t{};
    g_api->tick_info(&t);
    g_budget.world_started(e->session, t.step);
    g_grid = 0;
    g_grid_session = 0;
}

uintptr_t grid_of(const Clock& c) {
    if (g_grid_session != c.session || !g_grid) {
        g_grid = g_api->singleton(g_api->server_world(), KQ_STR("ecs::VoxelGrid"));
        g_grid_session = c.session;
    }
    return g_grid;
}

KilnResult refuse(KilnQueryRayResult* out, const char* reason, size_t len) {
    out->ok = 0;
    out->reason = KilnStr{reason, len};
    return KILN_OK;
}
#define KQ_REFUSE(out, reason) refuse(out, reason, sizeof(reason) - 1)

KilnResult KILN_CALL t_ray_voxels(const KilnQueryRayRequest* req, KilnQueryRayResult* out) {
    if (!req || !out || req->size < sizeof(KilnQueryRayRequest) || out->size < sizeof(KilnQueryRayResult)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof(KilnQueryRayResult));
    out->size = size;
    out->reason = KilnStr{"", 0};
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    // on the world's thread too: inside a program's update or a wrap stage the world is mid-step (internal.h)
    if (kq::in_program()) return out->reason = KQ_STR(kq::kInsideProgram), KILN_ERR_WRONG_THREAD;
    const double from[3] = {req->from.x, req->from.y, req->from.z}, to[3] = {req->to.x, req->to.y, req->to.z};
    for (int i = 0; i < 3; ++i)
        if (!std::isfinite(from[i]) || !std::isfinite(to[i])) return KILN_ERR_INVALID_ARGUMENT;
    if (req->realm >= kq::kMaxRealms) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_ray_on.load(std::memory_order_acquire)) return KILN_ERR_UNAVAILABLE;
    const Clock c = now();
    if (!world_ready(c)) return KQ_REFUSE(out, "WorldNotReady");
    kq::RayFloats ray;
    if (!kq::narrow_ray(from, to, &ray)) return KILN_ERR_INVALID_ARGUMENT;
    if (g_in_ray) return KQ_REFUSE(out, "ReentrantQuery");

    kq::RealmBox box;
    const uintptr_t grid = grid_of(c);
    switch (kq::read_realm_box(grid, req->realm, g_layout, &kq::seh_copy, &box)) {
        case kq::GridRead::Ok: break;
        case kq::GridRead::NoGrid: return KQ_REFUSE(out, "WorldNotReady");
        case kq::GridRead::BadRealm: return KILN_ERR_INVALID_ARGUMENT;
        case kq::GridRead::BadMetadata: kq::g_grid_ok.store(false, std::memory_order_release); ray_off(KILN_CAP_REASON_FAULTED, "the voxel grid's realm table is not what this game build's layout says"); return KILN_ERR_UNAVAILABLE;
        case kq::GridRead::Fault: kq::g_grid_ok.store(false, std::memory_order_release); ray_off(KILN_CAP_REASON_FAULTED, "reading the voxel grid faulted"); return KILN_ERR_UNAVAILABLE;
    }
    if (!kq::box_contains(box, ray.from) || !kq::box_contains(box, ray.to)) return KQ_REFUSE(out, "OutOfGrid");

    switch (g_budget.admit(c.session, c.step)) {
        case kq::Admit::Ok: break;
        case kq::Admit::WorldNotReady: return KQ_REFUSE(out, "WorldNotReady");
        case kq::Admit::BudgetExceeded: return KQ_REFUSE(out, "BudgetExceeded");
    }
    alignas(16) unsigned char hit[kq::kHitBytes] = {};
    const uint8_t options[4] = {static_cast<uint8_t>(req->realm), 0, 1, 1};
    unsigned long code = 0;
    LARGE_INTEGER t0, t1;
    g_in_ray = true;
    QueryPerformanceCounter(&t0);
    const bool returned = kq::seh_ray(g_ray, hit, grid, ray.from, ray.to, options, &code);
    QueryPerformanceCounter(&t1);
    g_in_ray = false;
    g_budget.charge(static_cast<uint64_t>(static_cast<double>(t1.QuadPart - t0.QuadPart) * g_qpc_ns));
    if (!returned) {
        char why[96];
        std::snprintf(why, sizeof why, "the game's voxel ray faulted (0x%08lx)", code);
        ray_off(KILN_CAP_REASON_FAULTED, why);
        return KILN_ERR_UNAVAILABLE;
    }
    kq::RayHit h{};
    switch (kq::read_hit(hit, &h)) {
        case kq::RayOutcome::OutOfGrid: return KQ_REFUSE(out, "OutOfGrid");
        case kq::RayOutcome::NotLoaded: return KQ_REFUSE(out, "NotLoaded");
        case kq::RayOutcome::InvalidHit: return KQ_REFUSE(out, "InvalidHit");
        case kq::RayOutcome::Clear: out->ok = 1; return KILN_OK;
        case kq::RayOutcome::Hit: break;
    }
    out->ok = 1;
    out->hit = 1;
    out->material = h.material;
    out->voxel = h.voxel;
    out->position = KilnVec3{h.position[0], h.position[1], h.position[2]};
    const double dx = static_cast<double>(h.position[0]) - ray.from[0], dy = static_cast<double>(h.position[1]) - ray.from[1], dz = static_cast<double>(h.position[2]) - ray.from[2];
    out->distance = std::sqrt(dx * dx + dy * dy + dz * dz);
    return KILN_OK;
}

// ---- entity origins ----------------------------------------------------------------------------------------------------

uint32_t g_transform_hash = 0;
std::vector<KilnEntity> g_ids;       // tick thread scratch
std::vector<kq::Origin> g_origins;

KilnResult KILN_CALL t_entities(const KilnQueryEntityRequest* req, KilnEntity* ids, uint32_t capacity, KilnQueryEntityResult* out) {
    if (!req || !out || req->size < sizeof(KilnQueryEntityRequest) || out->size < sizeof(KilnQueryEntityResult) || (capacity && !ids)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof(KilnQueryEntityResult));
    out->size = size;
    out->reason = KilnStr{"", 0};
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (req->shape != KILN_QUERY_SHAPE_SPHERE && req->shape != KILN_QUERY_SHAPE_BOX) return KILN_ERR_INVALID_ARGUMENT;
    kq::Shape shape;
    shape.box = req->shape == KILN_QUERY_SHAPE_BOX;
    shape.center[0] = req->center.x, shape.center[1] = req->center.y, shape.center[2] = req->center.z;
    shape.half[0] = req->half_extents.x, shape.half[1] = req->half_extents.y, shape.half[2] = req->half_extents.z;
    shape.radius = req->radius;
    if (!shape.box) shape.half[0] = shape.half[1] = shape.half[2] = 0;
    if (!kq::valid_shape(shape)) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_world || !g_transform_hash || !g_api->feature(KQ_STR("world.entities")) || !g_api->feature(KQ_STR("world.find"))) return KILN_ERR_UNAVAILABLE;
    const Clock c = now();
    if (!world_ready(c)) {
        out->reason = KQ_STR("WorldNotReady");
        return KILN_OK;
    }
    // every entity with a transform, then each one's position in the same callback: nothing moves in between
    uint32_t count = 0;
    if (g_ids.size() < 4096) g_ids.resize(4096);
    for (int attempt = 0; attempt < 4; ++attempt) {
        const KilnResult rc = g_world->find(g_transform_hash, g_ids.data(), static_cast<uint32_t>(g_ids.size()), &count);
        if (rc == KILN_OK) break;
        if (rc == KILN_ERR_NOT_FOUND) {
            count = 0;
            break;
        }
        if (rc == KILN_ERR_UNAVAILABLE || rc == KILN_ERR_WRONG_THREAD) return rc;
        if (rc != KILN_ERR_BUFFER_TOO_SMALL || attempt == 3) return KILN_ERR_INTERNAL;
        g_ids.resize(static_cast<size_t>(count) + 1024);
    }
    g_origins.clear();
    for (uint32_t i = 0; i < count; ++i) {
        KilnVec3 p{};
        const KilnResult rc = g_world->position(g_ids[i], &p);
        if (rc == KILN_ERR_NOT_FOUND) continue;  // gone, or it never had a place
        if (rc != KILN_OK) return rc;           // unavailable is not a successful empty overlap
        g_origins.push_back({g_ids[i], p.x, p.y, p.z});
    }
    const std::vector<uint32_t> inside = kq::origins_inside(g_origins, shape, req->exclude);
    out->ok = 1;
    out->total = static_cast<uint32_t>(inside.size());
    out->written = out->total < capacity ? out->total : capacity;
    for (uint32_t i = 0; i < out->written; ++i) ids[i] = inside[i];
    return KILN_OK;
}

const KilnQueriesApi kTable = {{sizeof(KilnQueriesApi), KILN_QUERIES_VERSION}, &t_ray_voxels, &t_entities, &kq::t_path_submit, &kq::t_path_cancel, &kq::t_path_poll, &kq::t_path_retire};
static_assert(sizeof(KilnQueriesApi) == 56, "kiln.queries v1 is 56 bytes");

// ---- start -------------------------------------------------------------------------------------------------------------

void read_config() {
    char dir[1024];
    size_t n = 0;
    std::string text, warning;
    bool have = false;
    if (g_api->path(KILN_PATH_CONFIG, dir, sizeof dir, &n) == KILN_OK && n < sizeof dir) {
        try {
            const std::u8string u8(dir, dir + n);
            std::ifstream f(std::filesystem::path(u8) / "kiln.queries.json", std::ios::binary);
            if (f) {
                text.assign(std::istreambuf_iterator<char>(f), std::istreambuf_iterator<char>());
                have = true;
            }
        } catch (const std::exception&) {
        }
    }
    kq::Limits limits{};
    if (have) limits = kq::parse_limits(text, &warning);
    if (!warning.empty()) logf(KILN_LOG_WARN, "queries: %s", warning.c_str());
    g_budget.set_limits(limits);
    logf(KILN_LOG_INFO, "queries: ray budget %u rays and %u microseconds per server step (%s)", limits.max_rays, limits.max_microseconds,
         have ? "kiln.queries.json" : "defaults; no kiln.queries.json");
}

void KILN_CALL on_ready(void*) {
    g_world = static_cast<const KilnWorldApi*>(g_api->service(KQ_STR(KILN_WORLD_SERVICE), KILN_WORLD_VERSION));
    g_caps = static_cast<const KilnCapsApi*>(g_api->service(KQ_STR(KILN_CAPS_SERVICE), KILN_CAPS_VERSION));
    // sigdb can resolve every symbol while the mandatory live byte/layout check fails. The table still serves origins,
    // but every capability reader must see that its ray is unusable, including callers that never try a ray themselves.
    if (!g_ray_on.load(std::memory_order_acquire) && !g_ray_start_failure.empty() && g_api->feature(KQ_STR(kRayFeature)))
        report_ray_off(KILN_CAP_REASON_FAULTED, KilnStr{g_ray_start_failure.data(), g_ray_start_failure.size()});
    if (g_world && g_world->component_hash) g_transform_hash = g_world->component_hash(KQ_STR("CurrentTransform"));
    if (!g_world || !g_transform_hash) logf(KILN_LOG_WARN, "queries: kiln.world is not here: entities() is unavailable");
    // A game client's server world waits for its game test (native/core/gates/dev28-01-world-queries.json). The ray is held
    // through its feature; the entity question has no feature of its own, so its row is asked here.
    if (kiln_caps_held(g_caps, KQ_STR("queries.overlap_origins"))) {
        g_transform_hash = 0;
        logf(KILN_LOG_INFO, "queries: entities() is held until its game test (queries.overlap_origins)");
    }
    kq::path_ready();
}

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KQ_STR(KILN_QUERIES_SERVICE);
    info->version = KilnStr{"1", 1};
    info->on_ready = &on_ready;
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    g_qpc_ns = 1e9 / static_cast<double>(f.QuadPart);
    // Both questions need to know which server world is live and when it goes: without the lifecycle there is no service.
    KilnHandle h = 0;
    if (!api->feature || !api->feature(KQ_STR("world.lifecycle")) || !api->subscribe_world || !api->session ||
        api->subscribe_world(owner, &on_world, nullptr, &h) != KILN_OK) {
        logf(KILN_LOG_WARN, "queries: world lifecycle is unavailable on this build: kiln.queries is unavailable");
        return KILN_ERR_UNAVAILABLE;
    }
    read_config();
    const char* why = nullptr;
    if (resolve_ray(&why)) {
        g_ray_start_failure.clear();
        g_ray_on.store(true, std::memory_order_release);
        kq::g_grid_ok.store(true, std::memory_order_release);
        logf(KILN_LOG_INFO, "queries: voxel ray resolved (realms +0x%llx, count +0x%llx, stride 0x%llx; ray grid origin +0x%llx, shift +0x%llx, counts +0x%llx/+0x%llx)",
             static_cast<unsigned long long>(g_layout.realms), static_cast<unsigned long long>(g_layout.realm_count), static_cast<unsigned long long>(g_layout.realm_stride),
             static_cast<unsigned long long>(g_layout.origin), static_cast<unsigned long long>(g_layout.shift), static_cast<unsigned long long>(g_layout.count_h),
             static_cast<unsigned long long>(g_layout.count_v));
    } else {
        g_ray_start_failure = why;
        logf(KILN_LOG_WARN, "queries: %s: ray_voxels is unavailable; entities() still works", why);
    }
    kq::path_resolve();
    return api->register_service(owner, KQ_STR(KILN_QUERIES_SERVICE), &kTable);
}
