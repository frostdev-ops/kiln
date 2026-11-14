// SPDX-License-Identifier: MIT
// kiln.queries, paths (services/kiln_queries.h, "paths"): the game's own path search for callers that are not creatures.
// The game's path queries are created, read and destroyed through a world program's command buffer and context
// (docs/re/queries.md), so the work is done by one program of Kiln's own, kiln_queries_path, once per server step:
//   - a request's anchor is checked (still the entity it was: CurrentTransform there, SpawnTime unchanged);
//   - a new request gets its game query (at most 8 a step); id 0 means the command buffer was full: next step;
//   - a searching request is asked for its result (never a dirty one); a readable result is copied and checked before
//     the game's query is destroyed, and the destroy is retried until it is queued, whoever still waits for the result;
//   - a request that ended otherwise (cancelled, timed out, anchor gone) has its game query destroyed.
// Everything a caller sees goes through PathQueue (path_core.h): no game pointer leaves the program, and no lock is held
// while the game is called. A fault in one of the game's functions ends the path half for the process.
#include <windows.h>

#include <atomic>
#include <cstring>
#include <vector>

#include "internal.h"
#include "path_core.h"
#include "seh.h"

namespace kq {
namespace {

#define KQP_STR(s) KilnStr{s, sizeof(s) - 1}

constexpr char kPathFeature[] = "queries.path";
constexpr size_t kQuerySize = 0x110, kResultSize = 0x28;
PathQueue g_queue;
std::atomic<bool> g_path_on{false};
uintptr_t g_defaults = 0, g_create = 0, g_result = 0, g_destroy = 0;
int64_t c_start = 0, c_target = 0, c_iterations = 0, c_moves = 0, c_job_realm = 0;
// The fields Kiln sets beside those (sigdb captures too; docs/re/queries.md): the target type, the vertical start and
// target search distances and the free space, in half metres.
int64_t c_target_type = 0, c_start_search_v = 0, c_target_search_v = 0, c_space_h = 0, c_space_v = 0;
KilnHandle g_program = 0;
int32_t s_transform = -1, s_stamp = -1, s_grid = -1;
uint32_t h_transform = 0, h_stamp = 0;
double g_qpc_ns = 0;
std::atomic<uint32_t> g_session{0};  // the server session the program may work for; 0 between worlds
std::atomic<uint32_t> g_program_thread{0};  // the thread the program last ran on (0: it has not run)
bool g_thread_said = false;                 // tick thread

uint64_t now_ns() {
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return static_cast<uint64_t>(static_cast<double>(t.QuadPart) * g_qpc_ns);
}

// The path half is over for this process: what waits ends refused, and the game is not called again.
void path_off(uint32_t cap_reason, const char* reason, const char* why) {
    if (!g_path_on.exchange(false)) return;
    g_queue.fail_all(reason);
    logf(KILN_LOG_ERROR, "queries: %s: %s is off for this process", why, kPathFeature);
    if (g_caps && g_caps->report) g_caps->report(g_owner, KQP_STR(kPathFeature), KILN_CAP_UNAVAILABLE, cap_reason, KilnStr{why, std::strlen(why)});
}

template <class T>
void put(unsigned char* q, int64_t at, T v) { std::memcpy(q + at, &v, sizeof v); }

// ---- the program -------------------------------------------------------------------------------------------------------

// The anchor is still the entity the request was admitted for.
bool same_anchor(KilnProgramCtx* ctx, uint64_t* iter, const PathParams& p) {
    uint32_t n = 0;
    if (!g_world->read_other(ctx, iter[s_transform], p.anchor, &n)) return false;
    const void* stamp = g_world->read_other(ctx, iter[s_stamp], p.anchor, &n);
    uint64_t bits = 0;
    if (!stamp || n != sizeof bits) return false;
    std::memcpy(&bits, stamp, sizeof bits);
    return bits == p.stamp;
}

void KILN_CALL each(KilnProgramCtx* ctx, uint64_t* iter, KilnEntity, void*) {
    g_program_thread.store(GetCurrentThreadId(), std::memory_order_relaxed);
    const uint32_t session = g_session.load(std::memory_order_acquire);
    if (!session || !g_path_on.load(std::memory_order_acquire)) return;
    const std::vector<uint64_t> tokens = g_queue.tokens(session);
    if (tokens.empty()) return;
    void* const cb = g_world->command_buffer(ctx);
    if (!cb) return;
    uint32_t creates = 0;
    for (const uint64_t token : tokens) {
        const PathQueue::Work w = g_queue.next(token, session, &creates);
        if (w.op == PathQueue::Op::None && !w.check_identity) continue;
        if (w.check_identity && !same_anchor(ctx, iter, w.params)) {
            g_queue.owner_gone(token);  // its game query, if any, is destroyed by the next update
            continue;
        }
        switch (w.op) {
            case PathQueue::Op::None: break;
            case PathQueue::Op::Create: {
                // the realm of this job, and the endpoints against the grid as it is now
                uintptr_t job = 0;
                uint8_t realm = 0xff;
                if (!seh_copy(reinterpret_cast<uintptr_t>(ctx), &job, sizeof job) || !seh_copy(job + static_cast<uintptr_t>(c_job_realm), &realm, 1)) {
                    path_off(KILN_CAP_REASON_FAULTED, "GameCallFault", "the program's job could not be read");
                    return;
                }
                if (realm != 0) {
                    g_queue.refused(token, KQ_REFUSED, "RealmUnsupported");
                    break;
                }
                RealmBox box;
                const GridRead grid = read_realm_box(static_cast<uintptr_t>(iter[s_grid]), 0, g_layout, &seh_copy, &box);
                if (grid == GridRead::BadMetadata || grid == GridRead::Fault) {
                    g_grid_ok.store(false, std::memory_order_release);
                    path_off(KILN_CAP_REASON_FAULTED, "ProgramUnavailable", "the voxel grid is not what this game build's layout says");
                    return;
                }
                if (grid != GridRead::Ok) break;  // not ready this step: the deadline bounds the wait
                if (!box_contains(box, w.params.from) || !box_contains(box, w.params.to)) {
                    g_queue.refused(token, KQ_OUT_OF_GRID, "OutOfGrid");
                    break;
                }
                alignas(16) unsigned char q[kQuerySize] = {};
                if (!seh_path_defaults(g_defaults, q)) {  // once, on zeroed bytes; then only the tested fields
                    path_off(KILN_CAP_REASON_FAULTED, "GameCallFault", "the game's path query defaults faulted");
                    return;
                }
                put<uint32_t>(q, 0, w.params.anchor);
                std::memcpy(q + c_start, w.params.from, 12);
                std::memcpy(q + c_target, w.params.to, 12);
                q[c_target_type] = w.params.fallback ? 2 : 0;  // UseClosestTargetNodeAsFallback
                put<uint32_t>(q, c_iterations, w.params.max_iterations);
                put<uint16_t>(q, c_moves, 1);  // walk only
                put<uint32_t>(q, c_start_search_v, 4);
                put<uint32_t>(q, c_target_search_v, 4);
                put<uint32_t>(q, c_space_h, w.params.hspace);
                put<uint32_t>(q, c_space_v, w.params.vspace);
                uint32_t id = 0;
                if (!seh_path_create(g_create, cb, &id, q)) {
                    path_off(KILN_CAP_REASON_FAULTED, "GameCallFault", "the game's path query create faulted");
                    return;
                }
                g_queue.created(token, id);
                break;
            }
            case PathQueue::Op::Poll: {
                alignas(16) unsigned char out[kResultSize] = {};
                bool readable = false;
                if (!seh_path_result(g_result, ctx, out, w.game_id, &readable)) {
                    path_off(KILN_CAP_REASON_FAULTED, "GameCallFault", "the game's path query result faulted");
                    return;
                }
                if (!readable) break;  // no final result yet: never "no path"
                uintptr_t points = 0;
                uint64_t count = 0;
                std::memcpy(&points, out, sizeof points);
                std::memcpy(&count, out + 8, sizeof count);
                const uint8_t result = out[0x24];
                // copied and checked now, before the destroy: the array is the game's and goes with its query
                std::vector<RawPoint> copy;
                bool copied = false;
                if (result == 1 && count >= 1 && count <= KILN_QUERY_PATH_MAX_POINTS && points) {
                    copy.resize(static_cast<size_t>(count));
                    copied = seh_copy(points, copy.data(), copy.size() * sizeof(RawPoint));
                }
                g_queue.answered(token, result, classify(result, count, copied, copy, w.params.target));
                [[fallthrough]];
            }
            case PathQueue::Op::Destroy: {
                bool queued = false;
                if (!seh_path_destroy(g_destroy, cb, w.params.anchor, w.game_id, &queued)) {
                    path_off(KILN_CAP_REASON_FAULTED, "GameCallFault", "the game's path query destroy faulted");
                    return;
                }
                if (queued) g_queue.destroyed(token);  // else the command buffer was full: again next update
                break;
            }
        }
    }
}

// ---- the tick: deadlines, owners that went, the program's health -----------------------------------------------------

void KILN_CALL noop(void*) {}

void KILN_CALL on_pre(const KilnTickInfo* tick, void*) {
    const Clock c = now();
    const bool ready = world_ready(c);
    g_session.store(ready ? c.session : 0, std::memory_order_release);
    if (ready) g_queue.deadlines(c.session, tick ? tick->step : c.step, now_ns());
    if (!g_thread_said)
        if (const uint32_t t = g_program_thread.load(std::memory_order_relaxed)) {
            g_thread_said = true;
            logf(KILN_LOG_INFO, "queries: kiln_queries_path runs on thread %u (the tick thread is %u)", t, static_cast<uint32_t>(GetCurrentThreadId()));
        }
    // an owner that was revoked without path_retire: found by a post that the core refuses
    for (const KilnOwner owner : g_queue.owners())
        if (g_api->post(owner, &noop, nullptr) == KILN_ERR_REVOKED) g_queue.retire(owner);
    if (g_path_on.load(std::memory_order_acquire) && g_program && tick && tick->step % 64 == 0) {
        KilnProgramInfo info{};
        info.size = sizeof info;
        if (g_world->program_info(g_program, &info) == KILN_OK && (info.state == KILN_PROGRAM_REFUSED || info.state == KILN_PROGRAM_FAULTED))
            path_off(KILN_CAP_REASON_FAULTED, "ProgramUnavailable", info.state == KILN_PROGRAM_FAULTED ? "the program kiln_queries_path faulted" : "the world refused the program kiln_queries_path");
    }
}

bool constant(const char* name, int64_t* out) { return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }

}  // namespace

void path_resolve() {
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    g_qpc_ns = 1e9 / static_cast<double>(f.QuadPart);
    const char* why = nullptr;
    if (!g_api->feature || !g_api->feature(KQP_STR(kPathFeature))) why = "the feature did not resolve on this game build (or is held)";
    else if (!g_grid_ok.load()) why = "the voxel grid it checks endpoints against did not resolve";
    else if (g_api->symbol(KQP_STR("queries/PathQueryDefaults"), &g_defaults) != KILN_OK || g_api->symbol(KQP_STR("queries/CreatePathQuery"), &g_create) != KILN_OK ||
             g_api->symbol(KQP_STR("queries/GetPathQueryResult"), &g_result) != KILN_OK || g_api->symbol(KQP_STR("queries/DestroyPathQuery"), &g_destroy) != KILN_OK ||
             !g_defaults || !g_create || !g_result || !g_destroy)
        why = "a path query function is missing";
    else if (!constant("queries/query.start", &c_start) || !constant("queries/query.target", &c_target) || !constant("queries/query.max_iterations", &c_iterations) ||
             !constant("queries/query.moves", &c_moves) || !constant("queries/job.realm", &c_job_realm) ||
             !constant("queries/query.target_type", &c_target_type) || !constant("queries/query.start_search_v", &c_start_search_v) ||
             !constant("queries/query.target_search_v", &c_target_search_v) || !constant("queries/query.space_h", &c_space_h) ||
             !constant("queries/query.space_v", &c_space_v))
        why = "a path query layout constant is missing";
    else {
        auto inside = [](int64_t at, size_t n) { return at >= 4 && at + static_cast<int64_t>(n) <= static_cast<int64_t>(kQuerySize); };
        if (!inside(c_start, 12) || !inside(c_target, 12) || !inside(c_iterations, 4) || !inside(c_moves, 2) || c_job_realm < 0 || c_job_realm > 0x1000 ||
            !inside(c_target_type, 1) || !inside(c_start_search_v, 4) || !inside(c_target_search_v, 4) || !inside(c_space_h, 4) || !inside(c_space_v, 4))
            why = "the captured path query layout is not a layout";
    }
    if (why) {
        logf(KILN_LOG_WARN, "queries: %s: paths are unavailable", why);
        return;
    }
    g_path_on.store(true, std::memory_order_release);
}

void path_ready() {
    if (!g_path_on.load()) return;
    auto off = [](const char* why) {
        g_path_on.store(false);
        logf(KILN_LOG_WARN, "queries: %s: paths are unavailable", why);
        if (g_caps && g_caps->report) g_caps->report(g_owner, KQP_STR(kPathFeature), KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_SERVICE_MISSING, KilnStr{why, std::strlen(why)});
    };
    if (!g_world || !g_world->register_program || !g_world->read_other || !g_world->program_slot || !g_world->command_buffer)
        return off("kiln.world's programs are not here");
    static const KilnStr singletons[] = {KQP_STR("ecs::PathGridSystem"), KQP_STR("ecs::VoxelGrid"), KQP_STR("ecs::ColliderSystem")};
    static const KilnStr others[] = {KQP_STR("CurrentTransform"), KQP_STR("SpawnTime")};
    KilnProgramDesc d{};
    d.size = sizeof d;
    d.flags = KILN_PROGRAM_SYSTEM | KILN_PROGRAM_HOST_ONLY | KILN_PROGRAM_BARRIER;
    d.name = KQP_STR("kiln_queries_path");
    d.each = &each;
    d.singletons_read = KilnNames{singletons, 3, 0};
    d.other_read = KilnNames{others, 2, 0};
    if (g_world->register_program(g_owner, &d, &g_program) != KILN_OK) return off("kiln.world refused the program kiln_queries_path");
    s_transform = g_world->program_slot(g_program, KILN_SLOT_OTHER_READ, others[0]);
    s_stamp = g_world->program_slot(g_program, KILN_SLOT_OTHER_READ, others[1]);
    s_grid = g_world->program_slot(g_program, KILN_SLOT_SINGLETON, singletons[1]);
    if (s_transform < 0 || s_stamp < 0 || s_grid < 0) return off("the program kiln_queries_path has no slot for what it reads");
    h_transform = g_world->component_hash(others[0]);
    h_stamp = g_world->component_hash(others[1]);
    KilnHandle h = 0;
    if (g_api->subscribe_tick(g_owner, KILN_TICK_PRE, &on_pre, nullptr, &h) != KILN_OK) return off("no Pre tick");
    logf(KILN_LOG_INFO, "queries: paths on: program kiln_queries_path accepted (slots: transform %d, stamp %d, grid %d); %u open, %u per owner, %u creates a step",
         s_transform, s_stamp, s_grid, kPathGlobalSlots, kPathOwnerSlots, kPathCreatesPerUpdate);
}

void path_world_closed(uint32_t session) {
    g_session.store(0, std::memory_order_release);
    g_queue.world_closed(session);
}

// ---- the table ---------------------------------------------------------------------------------------------------------

KilnResult KILN_CALL t_path_submit(KilnOwner owner, const KilnQueryPathRequest* request, KilnQueryPathAdmission* out) {
    if (!out || out->size < sizeof(KilnQueryPathAdmission)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof(KilnQueryPathAdmission));
    out->size = size;
    out->reason = KilnStr{"", 0};
    PathParams p;
    if (!owner || !request || request->size < sizeof(KilnQueryPathRequest) || !path_params(*request, &p)) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    // It reads realm 0's voxel grid box and the anchor's components below: not while the world is mid-step inside a
    // program's update or a wrap stage (internal.h), where kiln.world would also answer a present anchor as none.
    if (in_program()) return out->reason = KQP_STR(kInsideProgram), KILN_ERR_WRONG_THREAD;
    if (!g_path_on.load(std::memory_order_acquire) || !g_grid_ok.load(std::memory_order_acquire)) return KILN_ERR_UNAVAILABLE;
    const Clock c = now();
    if (!world_ready(c)) return KILN_ERR_UNAVAILABLE;
    if (g_queue.retired(owner) || g_api->post(owner, &noop, nullptr) == KILN_ERR_REVOKED) return KILN_ERR_REVOKED;
    auto refuse = [&](uint32_t status, const char* reason) {
        out->status = status;
        out->reason = kstr_of(reason);
        return KILN_OK;
    };
    // the domain: both endpoints inside realm 0's voxel grid, as it is now
    RealmBox box;
    const uintptr_t grid = g_api->singleton(g_api->server_world(), KQP_STR("ecs::VoxelGrid"));
    switch (read_realm_box(grid, 0, g_layout, &seh_copy, &box)) {
        case GridRead::Ok: break;
        case GridRead::NoGrid:
        case GridRead::BadRealm: return KILN_ERR_UNAVAILABLE;
        default:
            g_grid_ok.store(false, std::memory_order_release);
            path_off(KILN_CAP_REASON_FAULTED, "ProgramUnavailable", "the voxel grid is not what this game build's layout says");
            return KILN_ERR_UNAVAILABLE;
    }
    if (!box_contains(box, p.from) || !box_contains(box, p.to)) return refuse(KQ_OUT_OF_GRID, "OutOfGrid");
    // the anchor's identity: there, with a transform, and stamped
    uint32_t n = 0;
    if (!g_world->exists(p.anchor) || !g_world->component(p.anchor, h_transform, &n)) return refuse(KQ_OWNER_GONE, "OwnerGone");
    const void* stamp = g_world->component(p.anchor, h_stamp, &n);
    if (!stamp || n != sizeof p.stamp) return refuse(KQ_IDENTITY_UNAVAILABLE, "OwnerIdentityUnavailable");
    std::memcpy(&p.stamp, stamp, sizeof p.stamp);
    uint64_t token = 0;
    switch (g_queue.submit(owner, p, c.session, c.step, now_ns(), &token)) {
        case KQ_PENDING: break;
        case KQ_OWNER_RETIRED: return KILN_ERR_REVOKED;
        default: return refuse(KQ_QUEUE_FULL, "QueueFull");
    }
    out->ok = 1;
    out->request = token;
    out->status = KQ_PENDING;
    return KILN_OK;
}

KilnResult KILN_CALL t_path_cancel(KilnOwner owner, uint64_t request) {
    if (!owner || !request) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    return g_queue.cancel(owner, request);
}

KilnResult KILN_CALL t_path_poll(KilnOwner owner, KilnQueryPathUpdate* update, KilnQueryPathPoint* points, uint32_t capacity, uint32_t* needed) {
    if (!owner || !update || update->size < sizeof(KilnQueryPathUpdate) || !needed) return KILN_ERR_INVALID_ARGUMENT;
    return g_queue.poll(owner, update, points, capacity, needed);
}

KilnResult KILN_CALL t_path_retire(KilnOwner owner) {
    if (!owner) return KILN_ERR_INVALID_ARGUMENT;
    g_queue.retire(owner);
    return KILN_OK;
}

}  // namespace kq
