// SPDX-License-Identifier: MIT
// The path half of kiln.queries without the game: requests, the owners' queues, admission, deadlines, cancellation,
// retirement, world close, and what a game answer means (chapter 02 sections 4.2-4.4, 5.1, 5.3). One mutex guards all
// of it; no game call is made under it. The glue (path.cpp) asks `next` what to do for a request, does it in the game,
// and reports back. Pure: sessions, steps and time are the caller's.
#ifndef KILN_QUERIES_PATH_CORE_H
#define KILN_QUERIES_PATH_CORE_H
#include <cmath>
#include <cstdint>
#include <cstring>
#include <deque>
#include <map>
#include <memory>
#include <mutex>
#include <vector>

#include "services/kiln_queries.h"

namespace kq {

inline constexpr uint32_t kPathGlobalSlots = 32, kPathOwnerSlots = 8, kPathCreatesPerUpdate = 8;
inline constexpr uint64_t kPathDeadlineNs = 10'000'000'000ull, kPathDeadlineSteps = 150;
inline constexpr double kReachXZ = 1.5, kReachY = 3.0;

struct RawPoint {  // the game's 16-byte point
    float x, y, z;
    uint8_t move;
    uint8_t pad[3];
};
static_assert(sizeof(RawPoint) == 16);

struct PathOutcome {
    uint32_t status = KQ_UNKNOWN;
    bool ok = false, reaches = false;
    const char* reason = "";
    double length = 0;
    std::vector<KilnQueryPathPoint> points;
};

inline KilnStr kstr_of(const char* s) { return KilnStr{s, std::strlen(s)}; }

// What a readable game result means. `points`: the copied array when result is 1 and the count is 1..4096 and the copy
// worked; `copied` says whether it did. The order is the rule: the raw result, then count, readability and numbers,
// then move kinds, and only then where the route ends: a route that is not all walking is never FOUND.
inline PathOutcome classify(uint8_t result, uint64_t count, bool copied, const std::vector<RawPoint>& points, const double target[3]) {
    PathOutcome o;
    auto fail = [&](uint32_t status, const char* reason) {
        o.status = status;
        o.reason = reason;
        return o;
    };
    switch (result) {
        case 1: break;
        case 0: return fail(KQ_UNKNOWN, "NoEngineAnswer");
        case 2: return fail(KQ_NO_PATH, "NoPathFound");
        case 3: return fail(KQ_NO_PATH, "NoFreePath");
        case 4: return fail(KQ_NO_PATH, "NoOccupiedPath");
        case 5: return fail(KQ_NO_START, "NoStartNodeFound");
        case 6: return fail(KQ_NO_TARGET, "NoTargetNodeFound");
        case 7: return fail(KQ_ITERATION_LIMIT, "MaxIterationsReached");
        case 8: return fail(KQ_OUT_OF_MEMORY, "OpenListOutOfMemory");
        case 9: return fail(KQ_OUT_OF_MEMORY, "NodesOutOfMemory");
        default: return fail(KQ_UNKNOWN, "UnknownGameResult");
    }
    if (count == 0) return fail(KQ_MALFORMED, "EmptyFoundPath");
    if (count > KILN_QUERY_PATH_MAX_POINTS) return fail(KQ_TRUNCATED, "PointLimitExceeded");
    if (!copied || points.size() != count) return fail(KQ_MALFORMED, "MalformedPoints");
    double length = 0;
    bool walk = true;
    std::vector<KilnQueryPathPoint> out;
    out.reserve(points.size());
    for (size_t i = 0; i < points.size(); ++i) {
        const RawPoint& p = points[i];
        if (!std::isfinite(p.x) || !std::isfinite(p.y) || !std::isfinite(p.z)) return fail(KQ_MALFORMED, "MalformedPoints");
        if (i) {
            const double dx = static_cast<double>(p.x) - points[i - 1].x, dy = static_cast<double>(p.y) - points[i - 1].y, dz = static_cast<double>(p.z) - points[i - 1].z;
            length += std::sqrt(dx * dx + dy * dy + dz * dz);
        }
        if (p.move != 0) walk = false;
        out.push_back(KilnQueryPathPoint{KilnVec3{p.x, p.y, p.z}, p.move, 0});
    }
    if (!std::isfinite(length)) return fail(KQ_MALFORMED, "MalformedPoints");
    if (!walk) {
        o.status = KQ_UNSUPPORTED;
        o.reason = "UnsupportedTraversal";
        o.points = std::move(out);  // kept for diagnosis; not a route
        return o;
    }
    const RawPoint& last = points.back();
    const double dx = last.x - target[0], dz = last.z - target[2], dy = last.y - target[1];
    o.reaches = std::sqrt(dx * dx + dz * dz) <= kReachXZ && std::fabs(dy) <= kReachY;
    o.status = o.reaches ? KQ_FOUND : KQ_PARTIAL;
    o.ok = true;
    o.length = length;
    o.points = std::move(out);
    return o;
}

struct PathParams {
    KilnEntity anchor = 0;
    uint64_t stamp = 0;  // the anchor's SpawnTime bits at admission
    float from[3] = {}, to[3] = {};
    double target[3] = {};  // the target as asked, for the endpoint test
    uint32_t hspace = 0, vspace = 0;  // half metres
    uint32_t max_iterations = 2000;
    bool fallback = false;
};

// False: numbers that are no request. Path endpoints may coincide (the game answers a same-start route).
inline bool path_params(const KilnQueryPathRequest& r, PathParams* out) {
    const double f[3] = {r.from.x, r.from.y, r.from.z}, t[3] = {r.to.x, r.to.y, r.to.z};
    double sum = 0;
    for (int i = 0; i < 3; ++i) {
        if (!std::isfinite(f[i]) || !std::isfinite(t[i])) return false;
        out->from[i] = static_cast<float>(f[i]);
        out->to[i] = static_cast<float>(t[i]);
        if (!std::isfinite(out->from[i]) || !std::isfinite(out->to[i])) return false;
        out->target[i] = t[i];
        sum += (t[i] - f[i]) * (t[i] - f[i]);
    }
    const float dx = out->to[0] - out->from[0], dy = out->to[1] - out->from[1], dz = out->to[2] - out->from[2];
    if (!(std::sqrt(sum) <= KILN_QUERY_PATH_MAX_DISTANCE) || !(std::sqrt(dx * dx + dy * dy + dz * dz) <= static_cast<float>(KILN_QUERY_PATH_MAX_DISTANCE))) return false;
    auto half = [](double metres, uint32_t* to) {
        if (!std::isfinite(metres) || metres < 0 || metres > 16) return false;
        *to = static_cast<uint32_t>(std::ceil(metres * 2 - 1e-9));
        return true;
    };
    if (!half(r.horizontal_space, &out->hspace) || !half(r.vertical_space, &out->vspace)) return false;
    if (r.max_iterations < 1 || r.max_iterations > 65535 || r.closest_fallback > 1 || r.anchor == 0) return false;
    out->anchor = r.anchor;
    out->max_iterations = r.max_iterations;
    out->fallback = r.closest_fallback != 0;
    return true;
}

class PathQueue {
public:
    struct Update {
        KilnQueryPathUpdate head{};
        std::vector<KilnQueryPathPoint> points;
    };

    enum class Op { None, Create, Poll, Destroy };
    struct Work {
        Op op = Op::None;
        uint64_t token = 0;
        PathParams params;
        uint32_t game_id = 0;
        bool check_identity = false;
    };

    // ---- admission (tick thread) ----
    // KQ_PENDING: admitted, *token set. KQ_QUEUE_FULL: refused. KQ_OWNER_RETIRED: the lease is retired.
    uint32_t submit(KilnOwner owner, const PathParams& p, uint32_t session, uint64_t step, uint64_t now_ns, uint64_t* token) {
        std::lock_guard lk(m_);
        Lease& lease = leases_[owner];
        if (lease.retired) return KQ_OWNER_RETIRED;
        if (requests_.size() >= kPathGlobalSlots || lease.slots >= kPathOwnerSlots) return KQ_QUEUE_FULL;
        auto r = std::make_shared<Request>();
        r->token = ++tokens_;
        r->owner = owner;
        r->session = session;
        r->params = p;
        r->admitted_step = step;
        r->admitted_ns = now_ns;
        r->stage = KQS_QUEUED;
        requests_[r->token] = r;
        ++lease.slots;
        push(lease, *r, KQS_CREATED, KQS_QUEUED, nullptr);
        *token = r->token;
        return KQ_PENDING;
    }

    bool retired(KilnOwner owner) {
        std::lock_guard lk(m_);
        const auto it = leases_.find(owner);
        return it != leases_.end() && it->second.retired;
    }

    // KILN_OK or KILN_ERR_NOT_FOUND / KILN_ERR_REVOKED.
    KilnResult cancel(KilnOwner owner, uint64_t token) {
        std::lock_guard lk(m_);
        const auto lease = leases_.find(owner);
        if (lease != leases_.end() && lease->second.retired) return KILN_ERR_REVOKED;
        const auto it = requests_.find(token);
        if (it == requests_.end() || it->second->owner != owner || it->second->terminal) return KILN_ERR_NOT_FOUND;
        finish(*it->second, KQS_CANCELLED, simple(KQ_CANCELLED, "Cancelled"), KILN_QUERY_PATH_NO_GAME_RESULT);
        return KILN_OK;
    }

    // ---- delivery (any thread) ----
    KilnResult poll(KilnOwner owner, KilnQueryPathUpdate* out, KilnQueryPathPoint* points, uint32_t capacity, uint32_t* needed) {
        std::lock_guard lk(m_);
        *needed = 0;
        const auto it = leases_.find(owner);
        if (it != leases_.end() && it->second.retired) return KILN_ERR_REVOKED;
        if (it == leases_.end() || it->second.fifo.empty()) return KILN_ERR_NOT_FOUND;
        Update& u = it->second.fifo.front();
        const uint32_t n = static_cast<uint32_t>(u.points.size());
        *needed = n;
        if (n > capacity || (n && !points)) return KILN_ERR_BUFFER_TOO_SMALL;
        const uint32_t size = out->size;
        *out = u.head;
        out->size = size;
        for (uint32_t i = 0; i < n; ++i) points[i] = u.points[i];
        const uint64_t token = u.head.request;
        const bool terminal = u.head.terminal != 0;
        it->second.fifo.pop_front();
        if (terminal) {
            const auto r = requests_.find(token);
            if (r != requests_.end()) {
                r->second->consumed = true;
                release_if_done(r);
            }
        }
        return KILN_OK;
    }

    // The lease is over: its queue is dropped, its requests end with no result, the game's queries are still destroyed.
    void retire(KilnOwner owner) {
        std::lock_guard lk(m_);
        Lease& lease = leases_[owner];
        if (lease.retired) return;
        lease.retired = true;
        lease.fifo.clear();
        for (auto it = requests_.begin(); it != requests_.end();) {
            Request& r = *it->second;
            auto next = std::next(it);
            if (r.owner == owner) {
                r.terminal = true;
                r.consumed = true;  // nobody will poll it
                r.cleanup_only = true;
                release_if_done(it);
            }
            it = next;
        }
    }

    std::vector<KilnOwner> owners() {
        std::lock_guard lk(m_);
        std::vector<KilnOwner> out;
        for (auto& [o, l] : leases_)
            if (!l.retired && l.slots) out.push_back(o);
        return out;
    }

    // ---- the world ----
    // The server world of `session` is going: its game queries go with it, and nothing of it is delivered as a result.
    void world_closed(uint32_t session) {
        std::lock_guard lk(m_);
        for (auto& [owner, lease] : leases_)
            for (Update& u : lease.fifo)
                if (u.head.terminal && u.head.generation == session && u.head.status != KQ_WORLD_CLOSED) {
                    const PathOutcome o = simple(KQ_WORLD_CLOSED, "WorldClosed");
                    fill(u, o, KILN_QUERY_PATH_NO_GAME_RESULT);
                    u.head.after = KQS_CANCELLED;
                }
        for (auto it = requests_.begin(); it != requests_.end();) {
            Request& r = *it->second;
            auto next = std::next(it);
            if (r.session == session) {
                r.game_id = 0;  // the game's record died with its world: never destroyed in another one
                r.destroy_queued = true;
                if (!r.terminal) finish(r, KQS_CANCELLED, simple(KQ_WORLD_CLOSED, "WorldClosed"), KILN_QUERY_PATH_NO_GAME_RESULT);
                release_if_done(requests_.find(r.token));
            }
            it = next;
        }
    }

    // Every request without a result ends REFUSED with `reason` (a game call faulted, the program is gone); no game
    // call is made for any of them again.
    void fail_all(const char* reason) {
        std::lock_guard lk(m_);
        dead_ = true;
        for (auto it = requests_.begin(); it != requests_.end();) {
            Request& r = *it->second;
            auto next = std::next(it);
            r.game_id = 0;
            r.destroy_queued = true;
            if (!r.terminal) finish(r, KQS_COMPLETED, simple(KQ_REFUSED, reason), KILN_QUERY_PATH_NO_GAME_RESULT);
            release_if_done(requests_.find(r.token));
            it = next;
        }
    }

    // Tick thread, every Pre: requests past both limits end TIMED_OUT (also when the program does not run).
    void deadlines(uint32_t session, uint64_t step, uint64_t now_ns) {
        std::lock_guard lk(m_);
        for (auto& [token, r] : requests_)
            if (!r->terminal && r->session == session && step >= r->admitted_step + kPathDeadlineSteps && now_ns >= r->admitted_ns + kPathDeadlineNs)
                finish(*r, KQS_TIMED_OUT, simple(KQ_TIMED_OUT, "TimedOut"), KILN_QUERY_PATH_NO_GAME_RESULT);
    }

    // ---- the program (any thread, one at a time) ----
    std::vector<uint64_t> tokens(uint32_t session) {
        std::lock_guard lk(m_);
        std::vector<uint64_t> out;
        if (dead_) return out;
        for (auto& [token, r] : requests_)
            if (r->session == session) out.push_back(token);
        return out;
    }

    // What the program does for `token` now. `creates` counts this update's Create attempts.
    Work next(uint64_t token, uint32_t session, uint32_t* creates) {
        std::lock_guard lk(m_);
        Work w;
        const auto it = requests_.find(token);
        if (dead_ || it == requests_.end() || it->second->session != session) return w;
        Request& r = *it->second;
        w.token = token;
        w.params = r.params;
        w.game_id = r.game_id;
        if (r.terminal) {
            if (r.game_id && !r.destroy_queued) w.op = Op::Destroy;
            return w;
        }
        ++r.steps;
        w.check_identity = true;
        if (r.game_id) w.op = Op::Poll;
        else if (*creates < kPathCreatesPerUpdate) {
            ++*creates;
            w.op = Op::Create;
        }
        return w;
    }

    // The anchor is gone or is another entity now.
    void owner_gone(uint64_t token) { end(token, KQS_CANCELLED, simple(KQ_OWNER_GONE, "OwnerGone"), KILN_QUERY_PATH_NO_GAME_RESULT); }
    void refused(uint64_t token, uint32_t status, const char* reason) { end(token, KQS_COMPLETED, simple(status, reason), KILN_QUERY_PATH_NO_GAME_RESULT); }

    // Create answered `id` (0: the command buffer was full; it is tried again).
    void created(uint64_t token, uint32_t id) {
        std::lock_guard lk(m_);
        const auto it = requests_.find(token);
        if (it == requests_.end() || !id) return;
        Request& r = *it->second;
        r.game_id = id;
        if (r.terminal) return;  // it ended while the call ran: the id is destroyed by the next update
        r.stage = KQS_SEARCHING;
        const auto lease = leases_.find(r.owner);
        if (lease != leases_.end() && !lease->second.retired) push(lease->second, r, KQS_QUEUED, KQS_SEARCHING, nullptr);
    }

    // The game had a readable result.
    void answered(uint64_t token, uint8_t game_result, PathOutcome outcome) { end(token, KQS_COMPLETED, std::move(outcome), game_result); }

    // Destroy was queued for the request's game id.
    void destroyed(uint64_t token) {
        std::lock_guard lk(m_);
        const auto it = requests_.find(token);
        if (it == requests_.end()) return;
        it->second->destroy_queued = true;
        release_if_done(it);
    }

    // ---- reading (tests, status) ----
    size_t open() {
        std::lock_guard lk(m_);
        return requests_.size();
    }
    size_t queued(KilnOwner owner) {
        std::lock_guard lk(m_);
        const auto it = leases_.find(owner);
        return it == leases_.end() ? 0 : it->second.fifo.size();
    }
    bool dead() {
        std::lock_guard lk(m_);
        return dead_;
    }

private:
    struct Request {
        uint64_t token = 0;
        KilnOwner owner = 0;
        uint32_t session = 0;
        PathParams params;
        uint64_t admitted_step = 0, admitted_ns = 0;
        uint32_t stage = KQS_QUEUED, steps = 0, game_id = 0;
        bool terminal = false, consumed = false, destroy_queued = false, cleanup_only = false;
    };
    struct Lease {
        bool retired = false;
        uint32_t slots = 0;
        std::deque<Update> fifo;
    };

    static PathOutcome simple(uint32_t status, const char* reason) {
        PathOutcome o;
        o.status = status;
        o.reason = reason;
        return o;
    }

    void fill(Update& u, const PathOutcome& o, uint32_t game_result) {
        u.head.status = o.status;
        u.head.terminal = 1;
        u.head.ok = o.ok ? 1 : 0;
        u.head.game_result = game_result;
        u.head.reaches_target = o.reaches ? 1 : 0;
        u.head.length = o.ok ? o.length : 0;
        u.head.reason = o.ok ? KilnStr{"", 0} : kstr_of(o.reason);
        u.points = o.points;
        u.head.point_count = static_cast<uint32_t>(u.points.size());
    }

    void push(Lease& lease, const Request& r, uint32_t before, uint32_t after, const PathOutcome* terminal, uint32_t game_result = KILN_QUERY_PATH_NO_GAME_RESULT) {
        Update u;
        u.head.size = sizeof(KilnQueryPathUpdate);
        u.head.before = before;
        u.head.after = after;
        u.head.status = KQ_PENDING;
        u.head.request = r.token;
        u.head.sequence = ++sequence_;
        u.head.generation = r.session;
        u.head.anchor = r.params.anchor;
        u.head.game_result = KILN_QUERY_PATH_NO_GAME_RESULT;
        u.head.steps = r.steps;
        u.head.reason = KilnStr{"", 0};
        if (terminal) fill(u, *terminal, game_result);
        lease.fifo.push_back(std::move(u));
    }

    // Under the lock: the request's one terminal.
    void finish(Request& r, uint32_t stage, PathOutcome o, uint32_t game_result) {
        if (r.terminal) return;
        r.terminal = true;
        const uint32_t before = r.stage;
        r.stage = stage;
        const auto lease = leases_.find(r.owner);
        if (lease != leases_.end() && !lease->second.retired) push(lease->second, r, before, stage, &o, game_result);
        else r.consumed = true;
    }

    void end(uint64_t token, uint32_t stage, PathOutcome o, uint32_t game_result) {
        std::lock_guard lk(m_);
        const auto it = requests_.find(token);
        if (it != requests_.end()) finish(*it->second, stage, std::move(o), game_result);
    }

    // A slot is free once the result was taken (or nobody will) and the game's query is gone or on its way out.
    void release_if_done(std::map<uint64_t, std::shared_ptr<Request>>::iterator it) {
        if (it == requests_.end()) return;
        Request& r = *it->second;
        if (!r.terminal || !r.consumed || (r.game_id && !r.destroy_queued)) return;
        const auto lease = leases_.find(r.owner);
        if (lease != leases_.end() && lease->second.slots) --lease->second.slots;
        requests_.erase(it);
    }

    std::mutex m_;
    std::map<uint64_t, std::shared_ptr<Request>> requests_;
    std::map<KilnOwner, Lease> leases_;
    uint64_t tokens_ = 0, sequence_ = 0;
    bool dead_ = false;
};

}  // namespace kq
#endif
