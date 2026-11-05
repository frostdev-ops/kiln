// SPDX-License-Identifier: MIT
// Teleport the way the game's fast travel does it (EMF docs/engine_parts/re_teleport.md §5 and §8.3, re-verified on
// svn 1285601): kiln.world's own system program pushes a kind-3 "add component" record carrying the engine's 80-byte
// Teleport component into its slice's command buffer; the engine adds it at the end of the step, its `teleport`
// program moves the entity on the next step and bumps TeleportCount, and removes the component the step after.
//
//   Pre (tick thread)     validate the request (components, dead, duplicates), build the payload   -> staged
//   kiln_world program    QueuePush kind 3 (+ kind 0x10 checkpoint for players)                    -> pushed
//   Pre, later steps      TeleportCount changed -> MOVED callback; 10 s without -> kind-4 remove, FAILED
#include <chrono>
#include <cmath>
#include <deque>
#include <memory>

#include "world_internal.h"

namespace kw {
namespace {

constexpr uint32_t kTeleport = 0x23384d40u;          // keen::ecs::Teleport (80 bytes)
constexpr uint32_t kTeleportCount = 0xbffe34efu;     // keen::ecs::TeleportCount (u8 counter @0)
constexpr uint32_t kLastTransform = 0x4b6abcf9u;     // keen::ecs::LastTransform
constexpr uint32_t kCurrentTransform = 0x027ac564u;  // keen::ecs::CurrentTransform
constexpr uint32_t kActor = 0x009e8b34u;             // keen::ecs::Actor (state u64 @actor.state, bit 7 = Dead)
constexpr uint32_t kTeleportSize = 80;
constexpr size_t kMaxPending = 256;
constexpr double kTimeoutMs = 10000.0;  // stuck Teleport: remove it (re_teleport.md §5.2 step 5)
constexpr double kBusyMs = 2000.0;      // the entity already has a Teleport (the game is moving it)
constexpr double kStagedMs = 3000.0;    // our program did not push

enum State { QUEUED, STAGED, PUSHED, REMOVING, DONE };

struct Request {
    uint64_t id = 0;
    KilnOwner owner = 0;
    KilnEntity entity = 0;
    KilnVec3 target{};
    float yaw = 0;
    KilnEntity spawn_point = 0;
    uint32_t flags = 0;
    KilnTeleportFn cb = nullptr;
    void* user = nullptr;
    State state = QUEUED;
    uint64_t step0 = 0;   // tick step when first seen in Pre
    double t0 = 0;        // accepted (ms)
    double t_push = 0;    // pushed (ms)
    uint8_t baseline = 0; // TeleportCount.counter before the push
    uint8_t payload[kTeleportSize] = {};
    bool removed = false; // the kind-4 removal was pushed
    bool forced = false;  // self-test only: staged without the checks (an entity the engine never moves)
};

struct Result {
    KilnTeleportFn cb;
    void* user;
    KilnTeleportResult r;
    std::string reason;
    uint64_t id;
};

std::mutex g_mutex;
std::deque<std::unique_ptr<Request>> g_incoming;
std::vector<std::unique_ptr<Request>> g_active;
std::atomic<uint64_t> g_next_id{1};
std::atomic<bool> g_any{false};   // Pre has work
std::atomic<bool> g_work{false};  // the program has work (staged pushes or removals)
std::atomic<bool> g_broken{false};
uint64_t g_step = 0;

double now_ms() {
    using namespace std::chrono;
    return duration<double, std::milli>(steady_clock::now().time_since_epoch()).count();
}

template <class T>
T rd(const void* p, size_t off = 0) {
    T v;
    std::memcpy(&v, static_cast<const uint8_t*>(p) + off, sizeof v);
    return v;
}

bool pos_of(KilnEntity e, KilnVec3* out) { return position(e, out) == KILN_OK; }

double dist(const KilnVec3& a, const KilnVec3& b) {
    const double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
    return std::sqrt(dx * dx + dy * dy + dz * dz);
}

void finish(Request& q, uint32_t status, uint32_t flags, const std::string& reason, std::vector<Result>& out) {
    q.state = DONE;
    Result res{q.cb, q.user, {}, reason, q.id};
    KilnTeleportResult& r = res.r;
    r.size = sizeof r;
    r.entity = q.entity;
    r.request = q.id;
    r.status = status;
    r.flags = flags;
    r.target = q.target;
    pos_of(q.entity, &r.position);
    r.distance = (status == KILN_TELEPORT_MOVED) ? dist(r.position, q.target) : 0;
    r.steps = static_cast<uint32_t>(g_step >= q.step0 ? g_step - q.step0 : 0);
    if (status == KILN_TELEPORT_MOVED) {
        const double limit = q.spawn_point ? 25.0 : 1.0;
        if (r.distance > limit) r.flags |= KILN_TELEPORT_OFF_TARGET;
        if ((q.flags & KILN_TELEPORT_FIND_GROUND) && !q.spawn_point && r.distance < 0.01)
            r.flags |= KILN_TELEPORT_RAW_TARGET;
        KW_I("teleport #%llu: entity %u moved to (%.2f, %.2f, %.2f), %.2f m from the target, after %u steps%s%s",
             static_cast<unsigned long long>(q.id), q.entity, r.position.x, r.position.y, r.position.z, r.distance,
             r.steps, (r.flags & KILN_TELEPORT_OFF_TARGET) ? " (off target)" : "",
             (r.flags & KILN_TELEPORT_RAW_TARGET) ? " (raw target: no ground found)" : "");
    } else {
        KW_W("teleport #%llu: entity %u not moved: %s", static_cast<unsigned long long>(q.id), q.entity,
             reason.c_str());
    }
    out.push_back(std::move(res));
}

// Validates a queued request and builds the engine's Teleport component. Tick thread, no world job running.
// Returns false and sets `why` when the request must fail now; `wait` = keep it queued (entity busy).
bool stage(Request& q, std::string& why, bool& wait) {
    wait = false;
    if (!exists(q.entity)) {
        why = "no such entity";
        return false;
    }
    uint32_t ct_size = 0, tc_size = 0, lt_size = 0;
    const void* ct = component(q.entity, kCurrentTransform, &ct_size);
    const void* lt = component(q.entity, kLastTransform, &lt_size);
    const void* tc = component(q.entity, kTeleportCount, &tc_size);
    if (!ct || ct_size < 52 || !lt || !tc || tc_size < 1) {
        why = "the entity has no CurrentTransform/LastTransform/TeleportCount (props, chests and altars cannot be "
              "teleported)";
        return false;
    }
    if (F.dead) {
        uint32_t asize = 0;
        const void* actor = component(q.entity, kActor, &asize);
        if (actor && asize >= static_cast<uint32_t>(L.actor_state) + 8 &&
            (rd<uint64_t>(actor, static_cast<size_t>(L.actor_state)) >> 7 & 1)) {
            why = "the entity is dead";
            return false;
        }
    }
    for (auto& o : g_active)
        if (o.get() != &q && o->entity == q.entity && (o->state == STAGED || o->state == PUSHED || o->state == REMOVING)) {
            why = "a teleport for this entity is already in flight (#" + std::to_string(o->id) + ")";
            return false;
        }
    if (component(q.entity, kTeleport, nullptr)) {
        wait = true;
        why = "the entity already has a Teleport component (the game is moving it)";
        return false;
    }
    if (q.spawn_point) {
        if (!component(q.spawn_point, component_hash("PlayerSpawnPoint"), nullptr)) {
            why = "spawn_point " + std::to_string(q.spawn_point) + " has no PlayerSpawnPoint";
            return false;
        }
        if (!pos_of(q.spawn_point, &q.target)) {
            why = "spawn_point has no position";
            return false;
        }
    }
    // keen::ecs::Teleport: WorldTransform targetTransform @0 (i64 x,y,z 32.32; quat @0x18; scale @0x28),
    // EntityId targetEntityId @0x38, Time delayEndTime @0x40, bool applied @0x48, bool searchBestSpawnPosition @0x49.
    uint8_t* p = q.payload;
    std::memset(p, 0, sizeof q.payload);
    const int64_t fx = m_to_fixed(q.target.x), fy = m_to_fixed(q.target.y), fz = m_to_fixed(q.target.z);
    std::memcpy(p + 0, &fx, 8);
    std::memcpy(p + 8, &fy, 8);
    std::memcpy(p + 16, &fz, 8);
    if (q.flags & KILN_TELEPORT_FACE_YAW) {
        const double h = q.yaw * 3.14159265358979323846 / 360.0;
        const float quat[4] = {0.0f, static_cast<float>(std::sin(h)), 0.0f, static_cast<float>(std::cos(h))};
        std::memcpy(p + 0x18, quat, 16);
    } else {
        std::memcpy(p + 0x18, static_cast<const uint8_t*>(ct) + 0x18, 16);
    }
    std::memcpy(p + 0x28, static_cast<const uint8_t*>(ct) + 0x28, 12);  // #701 copies scale too: keep the current one
    std::memcpy(p + L.tp_target_entity, &q.spawn_point, 4);
    p[L.tp_find_ground] = (q.flags & KILN_TELEPORT_FIND_GROUND) ? 1 : 0;
    p[L.tp_applied] = 0;
    q.baseline = rd<uint8_t>(tc);
    return true;
}

void deliver(std::vector<Result>& results) {
    for (Result& res : results) {
        if (!res.cb) continue;
        res.r.reason = KilnStr{res.reason.data(), res.reason.size()};
        unsigned long code = 0;
        if (!seh_teleport_cb(res.cb, &res.r, res.user, &code))
            KW_E("teleport #%llu: the result callback faulted (0x%08lx)", static_cast<unsigned long long>(res.id), code);
    }
}

}  // namespace

// Self-test only (selftest.cpp): a teleport of an entity without TeleportCount/LastTransform, staged without the
// checks. The engine's teleport program never applies it, so it exercises the 10 s watchdog (kind-4 removal).
KilnResult teleport_force_stuck(KilnEntity entity, KilnTeleportFn cb, void* user) {
    if (!teleport_init() || !g_world.load()) return KILN_ERR_UNAVAILABLE;
    uint32_t size = 0;
    const void* ct = component(entity, kCurrentTransform, &size);
    if (!ct || size < 52) return KILN_ERR_NOT_FOUND;
    auto q = std::make_unique<Request>();
    q->id = g_next_id.fetch_add(1);
    q->entity = entity;
    q->target = {fixed_to_m(rd<int64_t>(ct)), fixed_to_m(rd<int64_t>(ct, 8)), fixed_to_m(rd<int64_t>(ct, 16))};
    std::memcpy(q->payload, ct, 52);
    q->cb = cb;
    q->user = user;
    q->t0 = now_ms();
    q->forced = true;
    {
        std::lock_guard lock(g_mutex);
        g_incoming.push_back(std::move(q));
    }
    g_any.store(true, std::memory_order_release);
    return KILN_OK;
}

bool teleport_init() { return F.teleport && F.queue && F.programs && F.entities; }

uint32_t teleports_pending() {
    std::lock_guard lock(g_mutex);
    return static_cast<uint32_t>(g_incoming.size() + g_active.size());
}

KilnResult teleport(KilnOwner owner, const KilnTeleportRequest* req, uint64_t* out) {
    if (out) *out = 0;
    if (!req || req->size < sizeof(KilnTeleportRequest) || !req->entity) return KILN_ERR_INVALID_ARGUMENT;
    if (req->flags & ~(KILN_TELEPORT_FIND_GROUND | KILN_TELEPORT_FACE_YAW | KILN_TELEPORT_CHECKPOINT))
        return KILN_ERR_INVALID_ARGUMENT;
    if (!teleport_init() || g_broken.load() || !g_world.load()) return KILN_ERR_UNAVAILABLE;
    if (!req->spawn_point) {
        const KilnVec3& t = req->target;
        if (!std::isfinite(t.x) || !std::isfinite(t.y) || !std::isfinite(t.z) || t.x < -1000 || t.x > 12000 ||
            t.z < -1000 || t.z > 12000 || t.y < -1000 || t.y > 5000)
            return KILN_ERR_INVALID_ARGUMENT;
    }
    if ((req->flags & KILN_TELEPORT_FACE_YAW) && !std::isfinite(req->yaw_deg)) return KILN_ERR_INVALID_ARGUMENT;
    auto q = std::make_unique<Request>();
    q->id = g_next_id.fetch_add(1);
    q->owner = owner;
    q->entity = req->entity;
    q->target = req->target;
    q->yaw = req->yaw_deg;
    q->spawn_point = req->spawn_point;
    q->flags = req->flags;
    q->cb = req->callback;
    q->user = req->user;
    q->t0 = now_ms();
    const uint64_t id = q->id;
    {
        std::lock_guard lock(g_mutex);
        if (g_incoming.size() + g_active.size() >= kMaxPending) {
            KW_W("teleport refused: %zu requests pending", kMaxPending);
            return KILN_ERR_UNAVAILABLE;
        }
        g_incoming.push_back(std::move(q));
    }
    g_any.store(true, std::memory_order_release);
    if (out) *out = id;
    return KILN_OK;
}

KilnResult teleport_cancel(uint64_t id) {
    std::lock_guard lock(g_mutex);
    for (auto& q : g_incoming)
        if (q->id == id) {
            q->cb = nullptr;
            return KILN_OK;
        }
    for (auto& q : g_active)
        if (q->id == id && q->state != DONE) {
            q->cb = nullptr;
            return KILN_OK;
        }
    return KILN_ERR_NOT_FOUND;
}

void teleport_pre(const KilnTickInfo* tick) {
    g_step = tick->step;
    if (!g_any.load(std::memory_order_acquire)) return;
    std::vector<Result> results;
    {
        std::lock_guard lock(g_mutex);
        while (!g_incoming.empty()) {
            g_incoming.front()->step0 = g_step;
            g_active.push_back(std::move(g_incoming.front()));
            g_incoming.pop_front();
        }
        const double now = now_ms();
        bool work = false;
        for (auto& qp : g_active) {
            Request& q = *qp;
            switch (q.state) {
                case QUEUED: {
                    if (q.forced) {
                        q.state = STAGED;
                        work = true;
                        break;
                    }
                    if (g_broken.load() || g_queue_broken.load()) {
                        finish(q, KILN_TELEPORT_FAILED, 0, "teleport was turned off after a fault", results);
                        break;
                    }
                    std::string why;
                    bool wait = false;
                    if (stage(q, why, wait)) {
                        q.state = STAGED;
                        work = true;
                    } else if (!wait || now - q.t0 > kBusyMs) {
                        finish(q, KILN_TELEPORT_FAILED, 0, why, results);
                    }
                    break;
                }
                case STAGED:
                    if (now - q.t0 > kStagedMs)
                        finish(q, KILN_TELEPORT_FAILED, 0, "kiln.world's program did not run (no server world step)",
                               results);
                    else
                        work = true;
                    break;
                case PUSHED:
                case REMOVING: {
                    uint32_t size = 0;
                    const void* tc = component(q.entity, kTeleportCount, &size);
                    if (!exists(q.entity) || (!tc && !q.forced)) {
                        finish(q, KILN_TELEPORT_FAILED, 0, "the entity disappeared", results);
                        break;
                    }
                    if (tc && rd<uint8_t>(tc) != q.baseline) {
                        finish(q, KILN_TELEPORT_MOVED, 0, "", results);
                        break;
                    }
                    if (q.state == PUSHED && now - q.t_push > kTimeoutMs) {
                        q.state = REMOVING;  // the program pushes the kind-4 removal
                        KW_W("teleport #%llu: entity %u did not move within 10 s; removing its Teleport component",
                             static_cast<unsigned long long>(q.id), q.entity);
                    }
                    if (q.state == REMOVING) {
                        if (q.removed && !component(q.entity, kTeleport, nullptr))
                            finish(q, KILN_TELEPORT_FAILED, KILN_TELEPORT_TIMED_OUT,
                                   "no move within 10 s (the ground search never answered?); the Teleport component "
                                   "was removed",
                                   results);
                        else if (now - q.t_push > kTimeoutMs + 5000)
                            finish(q, KILN_TELEPORT_FAILED, KILN_TELEPORT_TIMED_OUT,
                                   "no move within 10 s and the Teleport component could not be removed", results);
                        else
                            work = true;
                    }
                    break;
                }
                case DONE: break;
            }
        }
        std::erase_if(g_active, [](const std::unique_ptr<Request>& q) { return q->state == DONE; });
        g_work.store(work, std::memory_order_release);
        g_any.store(!g_active.empty() || !g_incoming.empty(), std::memory_order_release);
    }
    deliver(results);
}

// kiln.world's own program update (system program, worker thread, inside the sim step): the slice's command buffer
// is exclusive to us while we run.
void teleport_in_program(KilnProgramCtx* ctx) {
    if (!g_work.load(std::memory_order_acquire) || g_broken.load(std::memory_order_relaxed)) return;
    if (g_queue_broken.load()) {
        g_broken.store(true);  // pending requests fail in the next Pre
        return;
    }
    std::lock_guard lock(g_mutex);
    bool more = false;
    for (auto& qp : g_active) {
        Request& q = *qp;
        if (q.state == STAGED) {
            auto* r = static_cast<uint8_t*>(queue_push(ctx, 3, static_cast<uint32_t>(L.tp_add_size), q.entity));
            if (!r) {
                more = true;  // buffer full: next step
                continue;
            }
            const uint16_t sz = kTeleportSize;
            std::memcpy(r + L.rec_hashes, &kTeleport, 4);
            std::memcpy(r + L.rec_sizes, &sz, 2);
            std::memcpy(r + L.rec_data, q.payload, kTeleportSize);
            if ((q.flags & KILN_TELEPORT_CHECKPOINT) && q.entity >= 1 && q.entity <= 1023) {
                if (auto* c = static_cast<uint8_t*>(queue_push(ctx, 0x10, 0x10, q.entity)))
                    std::memcpy(c + 0xC, &q.entity, 4);
            }
            q.state = PUSHED;
            q.t_push = now_ms();
        } else if (q.state == REMOVING && !q.removed) {
            if (queue_remove(ctx, q.entity, kTeleport) == KILN_OK)
                q.removed = true;
            else
                more = true;
        }
    }
    g_work.store(more, std::memory_order_release);
}

}  // namespace kw
