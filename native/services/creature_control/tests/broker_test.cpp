// SPDX-License-Identifier: MIT
// kiln.creature_control on the host (no game, no Wine): the ABI sizes, the body registry (S1) and the life of one lease
// with the transition of its body (S2), on a fake game whose world program applies the broker's ops at the end of a
// step, as the engine applies queued component changes. Mirrors the broker part of out/pets/probes/contract_model.py
// (gate 2: Pending cannot drive, matching Active can, the last release restores only what the broker changed).
//   native/services/creature_control/tests/run.sh
#include <algorithm>
#include <cassert>
#include <cstdio>
#include <cstring>
#include <map>
#include <string>
#include <vector>
#include <thread>
#include <condition_variable>

#include "../registry.h"
#include "../snapshot.h"

using namespace kcc;

namespace {

int g_fail = 0, g_pass = 0;
#define CHECK(cond)                                                              \
    do {                                                                         \
        if (cond) ++g_pass;                                                      \
        else {                                                                   \
            ++g_fail;                                                            \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);          \
        }                                                                        \
    } while (0)

struct FakeBody {
    bool player = false, actor = true, input = true, steering = true, enemy_ai = true;
    bool ready = true, dead = false, action = false, trigger = false;
    std::vector<uint8_t> steering_bytes;
    int ai_off = 0;           // layers held off by the broker
    bool other_ai_off = false;
    int zeroed = 0;           // ZeroInput ops applied
    std::vector<int> zero_steps;  // the steps they were applied in
    bool refuse_remove = false, refuse_add = false, refuse_ai = false, refuse_snapshot = false;
    int behavior_removals = 0;
};

class FakeGame final : public Game {
public:
    std::map<KilnEntity, FakeBody> bodies;
    std::vector<std::string> lines;
    int step = 0;

    BodyRead read(KilnEntity e) override {
        BodyRead r;
        auto it = bodies.find(e);
        if (it == bodies.end()) return r;
        const FakeBody& b = it->second;
        r.exists = true;
        r.player = b.player;
        r.actor = b.actor;
        r.input = b.input;
        r.steering = b.steering;
        r.enemy_ai = b.enemy_ai;
        r.readable = b.actor && b.input;
        r.ready = b.ready;
        r.dead = b.dead;
        r.action_active = b.action;
        r.trigger_queued = b.trigger;
        return r;
    }
    bool steering_bytes(KilnEntity e, std::vector<uint8_t>* out) override {
        auto it = bodies.find(e);
        if (it == bodies.end() || !it->second.steering || it->second.refuse_snapshot) return false;
        *out = it->second.steering_bytes;
        return true;
    }
    KilnResult ai_layer(KilnEntity e, bool off) override {
        auto it = bodies.find(e);
        if (it == bodies.end()) return KILN_ERR_NOT_FOUND;
        if (it->second.refuse_ai) return KILN_ERR_UNAVAILABLE;
        if (off) it->second.ai_off = 1;
        else it->second.ai_off = 0;
        return KILN_OK;
    }
    void log(int32_t, const std::string& text) override { lines.push_back(text); }
    int ai_enabled(KilnEntity e) override {
        auto it = bodies.find(e);
        if (it == bodies.end() || !it->second.enemy_ai) return -1;
        return it->second.ai_off == 0 && !it->second.other_ai_off ? 1 : 0;
    }
    bool destroy(KilnEntity e) override {
        ++destroyed;
        return !refuse_destroy && bodies.erase(e) == 1;
    }
    bool entity_counter(uint32_t* out) override {
        if (!counter) return false;
        *out = counter;
        return true;
    }
    int destroyed = 0;
    bool refuse_destroy = false;
    uint32_t counter = 0;

    // the world program in the step after a Pre, then the engine's end of the step
    void run_step(const std::vector<Op>& ops) {
        ++step;
        for (const Op& op : ops) {
            auto it = bodies.find(op.entity);
            if (it == bodies.end()) continue;
            FakeBody& b = it->second;
            switch (op.kind) {
                case OpKind::ZeroInput:
                    ++b.zeroed;
                    b.zero_steps.push_back(step);
                    break;
                case OpKind::RemoveSteering:
                    if (!b.refuse_remove) b.steering = false;
                    break;
                case OpKind::AddSteering:
                    if (!b.refuse_add && !b.steering) {
                        b.steering = true;
                        b.steering_bytes = op.data;
                    }
                    break;
                case OpKind::RemoveBehavior:
                    b.enemy_ai = false;
                    ++b.behavior_removals;
                    break;
            }
        }
    }
    bool ai_on(KilnEntity e) { return bodies[e].ai_off == 0 && !bodies[e].other_ai_off; }
};

constexpr KilnOwner kOwner = 7, kOther = 9;
constexpr uint64_t kStepNs = kSecond / 25;  // a dedicated server's ~25 steps a second

struct World {
    FakeGame game;
    Broker broker{&game};
    uint64_t now = 0;
    std::vector<Op> last_ops;

    World() {
        broker.set_world(1, "test");
        broker.set_operational(true, "");
    }
    // one Pre (the broker's step) and the step it hands its ops to
    void step(int n = 1) {
        for (int i = 0; i < n; ++i) {
            now += kStepNs;
            broker.pre(now, &last_ops);
            game.run_step(last_ops);
        }
    }
    FakeBody& add(KilnEntity e) {
        FakeBody& b = game.bodies[e];
        b.steering_bytes.resize(80);
        for (int i = 0; i < 80; ++i) b.steering_bytes[i] = static_cast<uint8_t>(i + 1);
        return b;
    }
    KilnControlBodyKey bind(KilnEntity e, KilnOwner o = kOwner) {
        KilnControlBodyKey k{};
        const KilnResult rc = broker.bind(o, e, KILN_CONTROL_BASELINE_ENEMY, 1, &k);
        CHECK(rc == KILN_OK);
        return k;
    }
    KilnControlState acquire(const KilnControlBodyKey& k, uint32_t role = KILN_CONTROL_ROLE_PET_FOLLOW, KilnOwner o = kOwner,
                             KilnResult* rc_out = nullptr, uint64_t id = 0) {
        if (!id) CHECK(broker.next_request(o, &id) == KILN_OK);
        KilnControlRequest q{};
        q.size = sizeof q;
        q.role = role;
        q.body = k;
        q.request_id = id;
        KilnControlState s{};
        s.size = sizeof s;
        const KilnResult rc = broker.acquire(o, &q, &s);
        if (rc_out) *rc_out = rc;
        return s;
    }
    KilnControlState query(KilnHandle h) {
        KilnControlState s{};
        s.size = sizeof s;
        CHECK(broker.query(h, &s) == KILN_OK);
        return s;
    }
};

// ------------------------------------------------------------------------------------------------------------ ABI --

void test_abi() {
    CHECK(sizeof(KilnControlBodyKey) == 24);
    CHECK(sizeof(KilnControlRequest) == 48);
    CHECK(sizeof(KilnControlState) == 248);
    CHECK(offsetof(KilnControlState, body) == 24);
    CHECK(offsetof(KilnControlState, lease) == 48);
    CHECK(offsetof(KilnControlState, request_id) == 64);
    CHECK(offsetof(KilnControlState, transition_sequence) == 72);
    CHECK(offsetof(KilnControlState, notice_sequence) == 80);
    CHECK(offsetof(KilnControlState, reason) == 88);
    CHECK(offsetof(KilnControlRequest, body) == 8);
    CHECK(offsetof(KilnControlRequest, request_id) == 32);
    CHECK(sizeof(KilnCreatureControlApi) == 8 + 11 * sizeof(void*));
    CHECK(offsetof(KilnCreatureControlApi, release_body) == 8 + 10 * sizeof(void*));
    // the numbers riding and pets code against (19d §5.2)
    CHECK(KILN_CONTROL_ROLE_RIDDEN == 3 && KILN_CONTROL_ROLE_VEHICLE == 5 && KILN_CONTROL_ROLE_ENGINE_GUARD == 7);
    CHECK(KILN_CONTROL_RESULT_AI_INHIBITED == 14 && KILN_CONTROL_RESULT_EXPIRED_HANDLE == 13);
    CHECK(KILN_CONTROL_FAILED == 4 && KILN_CONTROL_CAUSE_WORLD_END == 9);
}

// ------------------------------------------------------------------------------------------------------- registry --

void test_bind() {
    World w;
    w.add(2000);
    w.add(2001);
    const KilnControlBodyKey a = w.bind(2000);
    CHECK(a.world_epoch == 1 && a.entity == 2000 && a.body_generation != 0 && a.reserved == 0);
    const KilnControlBodyKey a2 = w.bind(2000);  // the same owner again: the same key
    CHECK(std::memcmp(&a, &a2, sizeof a) == 0);
    const KilnControlBodyKey a3 = w.bind(2000, kOther);  // another owner shares it
    CHECK(std::memcmp(&a, &a3, sizeof a) == 0);
    const KilnControlBodyKey b = w.bind(2001);
    CHECK(b.body_generation != a.body_generation && b.body_generation != 0);
    CHECK(w.broker.bodies() == 2);
    // the baseline kind of the first binding stands
    KilnControlBodyKey k{};
    CHECK(w.broker.bind(kOwner, 2000, KILN_CONTROL_BASELINE_EXPLICIT_NO_AI, 1, &k) == KILN_ERR_INVALID_ARGUMENT);
    // unbind and bind again: a new generation, never one used before
    CHECK(w.broker.release_body(kOwner, a) == KILN_OK);
    CHECK(w.broker.bodies() == 2);  // kOther still holds it
    CHECK(w.broker.release_body(kOther, a) == KILN_OK);
    CHECK(w.broker.bodies() == 1);
    CHECK(w.broker.release_body(kOther, a) == KILN_ERR_NOT_FOUND);
    const KilnControlBodyKey a4 = w.bind(2000);
    CHECK(a4.body_generation != a.body_generation && a4.body_generation != b.body_generation && a4.body_generation != 0);
}

void test_bind_refusals() {
    World w;
    w.add(2000);
    KilnControlBodyKey k{};
    CHECK(w.broker.bind(0, 2000, 0, 1, &k) == KILN_ERR_INVALID_ARGUMENT);          // owner 0
    CHECK(w.broker.bind(kOwner, 2000, 0, 0, &k) == KILN_ERR_INVALID_ARGUMENT);     // no consent to retire
    CHECK(w.broker.bind(kOwner, 2000, 2, 1, &k) == KILN_ERR_INVALID_ARGUMENT);     // unknown baseline kind
    CHECK(w.broker.bind(kOwner, 2000, 0, 1, nullptr) == KILN_ERR_INVALID_ARGUMENT);
    w.add(5).player = true;
    CHECK(w.broker.bind(kOwner, 5, 0, 1, &k) == KILN_ERR_INVALID_ARGUMENT);        // the player range
    w.add(1023);
    CHECK(w.broker.bind(kOwner, 1023, 0, 1, &k) == KILN_ERR_INVALID_ARGUMENT);     // below the counter range
    w.add(0x80000001u);
    CHECK(w.broker.bind(kOwner, 0x80000001u, 0, 1, &k) == KILN_ERR_INVALID_ARGUMENT);  // bit 31
    w.add(3000).player = true;
    CHECK(w.broker.bind(kOwner, 3000, 0, 1, &k) == KILN_ERR_INVALID_ARGUMENT);     // a player component
    CHECK(w.broker.bind(kOwner, 4000, 0, 1, &k) == KILN_ERR_NOT_FOUND);
    w.add(4001).actor = false;
    CHECK(w.broker.bind(kOwner, 4001, 0, 1, &k) == KILN_ERR_UNAVAILABLE);          // not a creature body
    w.add(4002).steering = false;
    CHECK(w.broker.bind(kOwner, 4002, 0, 1, &k) == KILN_ERR_UNAVAILABLE);          // ExternalController
    CHECK(w.broker.bind(kOwner, 4002, KILN_CONTROL_BASELINE_EXPLICIT_NO_AI, 1, &k) == KILN_OK);  // a vehicle may lack one
    CHECK(w.broker.bodies() == 1);
    w.broker.set_operational(false, "test");
    CHECK(w.broker.bind(kOwner, 2000, 0, 1, &k) == KILN_ERR_UNAVAILABLE);
    w.broker.set_operational(true, "");
    w.broker.set_world(0, "test");
    CHECK(w.broker.bind(kOwner, 2000, 0, 1, &k) == KILN_ERR_UNAVAILABLE);          // no server world
}

void test_capacity() {
    World w;
    for (KilnEntity e = 10000; e < 10000 + KILN_CONTROL_MAX_BODIES + 1; ++e) w.add(e);
    KilnControlBodyKey k{};
    for (KilnEntity e = 10000; e < 10000 + KILN_CONTROL_MAX_BODIES; ++e) CHECK(w.broker.bind(kOwner, e, 0, 1, &k) == KILN_OK);
    CHECK(w.broker.bodies() == KILN_CONTROL_MAX_BODIES);
    CHECK(w.broker.tombstones_reserved() == KILN_CONTROL_MAX_BODIES);  // one pre-reserved deny entry per binding
    k = KilnControlBodyKey{};
    CHECK(w.broker.bind(kOwner, 10000 + KILN_CONTROL_MAX_BODIES, 0, 1, &k) == KILN_ERR_BUFFER_TOO_SMALL);
    CHECK(k.body_generation == 0);  // refused before allocation
    CHECK(w.broker.bodies() == KILN_CONTROL_MAX_BODIES);
    // a bound body still binds (no new allocation)
    CHECK(w.broker.bind(kOther, 10000, 0, 1, &k) == KILN_OK);
}

void test_request_ids() {
    World w;
    w.add(2000);
    const KilnControlBodyKey k = w.bind(2000);
    uint64_t a = 0, b = 0;
    CHECK(w.broker.next_request(kOwner, &a) == KILN_OK && a != 0);
    CHECK(w.broker.next_request(kOwner, &b) == KILN_OK && b > a);
    KilnResult rc = 0;
    const KilnControlState s = w.acquire(k, KILN_CONTROL_ROLE_PET_FOLLOW, kOwner, &rc, b);
    CHECK(rc == KILN_OK && s.lease != 0 && s.lease_state == KILN_CONTROL_PENDING);
    // the same id and payload: the same lease
    const KilnControlState s2 = w.acquire(k, KILN_CONTROL_ROLE_PET_FOLLOW, kOwner, &rc, b);
    CHECK(rc == KILN_OK && s2.lease == s.lease);
    // the same id, another payload: RequestConflict, no lease
    const KilnControlState s3 = w.acquire(k, KILN_CONTROL_ROLE_RIDDEN, kOwner, &rc, b);
    CHECK(rc == KILN_ERR_INVALID_ARGUMENT && s3.lease == 0 && s3.lease_state == KILN_CONTROL_FAILED &&
          std::strncmp(s3.reason, "RequestConflict", 15) == 0);
    // an id below the high-water mark: never a new lease
    const KilnControlState s4 = w.acquire(k, KILN_CONTROL_ROLE_PET_FOLLOW, kOwner, &rc, a);
    CHECK(rc == KILN_OK && s4.lease == 0 && s4.result == KILN_CONTROL_RESULT_EXPIRED_HANDLE);
    // next_request stays above what was admitted
    uint64_t c = 0;
    CHECK(w.broker.next_request(kOwner, &c) == KILN_OK && c > b);
    // request id 0, role None / Quarantine: invalid
    KilnControlRequest q{};
    q.size = sizeof q;
    q.role = KILN_CONTROL_ROLE_QUARANTINE;
    q.body = k;
    q.request_id = c;
    KilnControlState out{};
    out.size = sizeof out;
    CHECK(w.broker.acquire(kOwner, &q, &out) == KILN_ERR_INVALID_ARGUMENT);
    q.role = KILN_CONTROL_ROLE_PET_FOLLOW;
    q.request_id = 0;
    CHECK(w.broker.acquire(kOwner, &q, &out) == KILN_ERR_INVALID_ARGUMENT);
    // an owner without a binding of the body
    q.request_id = c;
    CHECK(w.broker.acquire(kOther, &q, &out) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(std::strncmp(out.reason, "NotBound", 8) == 0);
}

// -------------------------------------------------------------------------------------------------------- one lease --

void test_grant_drive_release() {
    World w;
    FakeBody& body = w.add(2000);
    const KilnControlBodyKey k = w.bind(2000);
    const KilnControlState p = w.acquire(k);
    CHECK(p.lease_state == KILN_CONTROL_PENDING && p.lease != 0 && p.transition_sequence == 0);
    // Pending is admission, never permission
    CHECK(w.broker.can_drive(kOwner, p.lease, k, 0) == 0);
    CHECK(w.broker.can_drive(kOwner, p.lease, k, 1) == 0);
    w.step();  // Pre: AI layer, quiescent, snapshot, removal queued; the step removes it
    CHECK(body.ai_off == 1);
    CHECK(!body.steering);
    CHECK(w.query(p.lease).lease_state == KILN_CONTROL_PENDING);  // not verified yet
    w.step();  // Pre: removal verified -> Active
    const KilnControlState a = w.query(p.lease);
    CHECK(a.lease_state == KILN_CONTROL_ACTIVE && a.result == KILN_CONTROL_RESULT_GRANTED);
    CHECK(a.transition_sequence == 1 && a.winning_lease == p.lease && a.effective_role == KILN_CONTROL_ROLE_PET_FOLLOW);
    CHECK((a.changed_mask & (KILN_CONTROL_CHANGED_STEERING | KILN_CONTROL_CHANGED_AI_LAYER)) ==
          (KILN_CONTROL_CHANGED_STEERING | KILN_CONTROL_CHANGED_AI_LAYER));
    CHECK(w.broker.can_drive(kOwner, p.lease, k, 1) == 1);
    CHECK(w.broker.can_drive(kOwner, p.lease, k, 0) == 0);   // a stale sequence
    CHECK(w.broker.can_drive(kOwner, p.lease, k, 2) == 0);
    CHECK(w.broker.can_drive(kOther, p.lease, k, 1) == 0);   // another owner
    KilnControlBodyKey stale = k;
    stale.body_generation += 1;
    CHECK(w.broker.can_drive(kOwner, p.lease, stale, 1) == 0);
    stale = k;
    stale.world_epoch = 2;
    CHECK(w.broker.can_drive(kOwner, p.lease, stale, 1) == 0);
    w.step(10);  // steady: nothing is written by the broker while the lease drives
    CHECK(body.zeroed == 1);  // only the removal step
    CHECK(w.query(p.lease).lease_state == KILN_CONTROL_ACTIVE);
    // release: no permission from this call on, even before the restore
    CHECK(w.broker.release(kOwner, p.lease) == KILN_OK);
    CHECK(w.broker.can_drive(kOwner, p.lease, k, 1) == 0);
    const KilnControlState r0 = w.query(p.lease);
    CHECK(r0.winning_lease == 0 && r0.lease_state != KILN_CONTROL_RELEASED);
    const int zero_before = body.zeroed;
    w.step();
    w.step();
    CHECK(body.zeroed == zero_before + 2 && !body.steering);  // input zeroed, SteeringAgent not back yet
    CHECK(w.query(p.lease).lease_state != KILN_CONTROL_RELEASED);
    w.step();  // the third zeroed step puts the SteeringAgent back at its end
    CHECK(body.zeroed == zero_before + 3 && body.steering);
    CHECK(body.ai_off == 1);  // AI only after the SteeringAgent is verified
    CHECK(w.query(p.lease).lease_state != KILN_CONTROL_RELEASED);
    w.step();  // verified: AI layer released, Released
    CHECK(body.ai_off == 0);
    const KilnControlState r = w.query(p.lease);
    CHECK(r.lease_state == KILN_CONTROL_RELEASED && r.result == KILN_CONTROL_RESULT_RELEASED);
    CHECK(r.transition_sequence == 1);
    // the restored bytes: the saved 80, with queryId and both velocity vectors zeroed
    std::vector<uint8_t> want(80);
    for (int i = 0; i < 80; ++i) want[i] = static_cast<uint8_t>(i + 1);
    std::memset(want.data() + 0, 0, 4);
    std::memset(want.data() + 8, 0, 12);
    std::memset(want.data() + 0x1c, 0, 12);
    CHECK(body.steering_bytes == want);
    // a retained terminal lease: query returns it, release is idempotent
    CHECK(w.broker.release(kOwner, p.lease) == KILN_OK);
    CHECK(w.query(p.lease).lease_state == KILN_CONTROL_RELEASED);
    // the binding can go now; then the body is unbound
    CHECK(w.broker.release_body(kOwner, k) == KILN_OK);
    CHECK(w.broker.bodies() == 0);
}

void test_quiescence() {
    World w;
    FakeBody& body = w.add(2000);
    body.action = true;  // a swing is playing
    const KilnControlBodyKey k = w.bind(2000);
    const KilnControlState p = w.acquire(k);
    w.step(5);
    CHECK(body.ai_off == 1);       // no new decision while it ends
    CHECK(body.steering);          // nothing removed during an old action
    CHECK(w.query(p.lease).lease_state == KILN_CONTROL_PENDING);
    body.action = false;
    body.trigger = true;           // and a queued one
    w.step(3);
    CHECK(body.steering && w.query(p.lease).lease_state == KILN_CONTROL_PENDING);
    body.trigger = false;
    w.step(2);
    CHECK(!body.steering && w.query(p.lease).lease_state == KILN_CONTROL_ACTIVE);
}

void test_quiescence_timeout() {
    World w;
    FakeBody& body = w.add(2000);
    body.action = true;
    const KilnControlBodyKey k = w.bind(2000);
    const KilnControlState p = w.acquire(k);
    w.step(5 * 25 - 2);
    CHECK(w.query(p.lease).lease_state == KILN_CONTROL_PENDING);
    w.step(3);
    const KilnControlState f = w.query(p.lease);
    CHECK(f.lease_state == KILN_CONTROL_FAILED && f.result == KILN_CONTROL_RESULT_TIMED_OUT);
    CHECK(std::strncmp(f.reason, "Quiescence", 10) == 0);
    w.step();  // rollback: only the broker's own changes
    CHECK(body.ai_off == 0 && body.steering);
    // not ready for gameplay: BodyNotReady
    World w2;
    FakeBody& b2 = w2.add(2000);
    b2.ready = false;
    const KilnControlState p2 = w2.acquire(w2.bind(2000));
    w2.step(5 * 25 + 2);
    const KilnControlState f2 = w2.query(p2.lease);
    CHECK(f2.result == KILN_CONTROL_RESULT_TIMED_OUT && std::strncmp(f2.reason, "BodyNotReady", 12) == 0);
}

// review B1: an Enemy body is never granted without the broker's AI-off layer
void test_ai_layer_refused() {
    World w;
    FakeBody& body = w.add(2000);
    body.refuse_ai = true;
    const KilnControlState p = w.acquire(w.bind(2000));
    w.step(5 * 25 - 2);
    CHECK(w.query(p.lease).lease_state == KILN_CONTROL_PENDING);
    CHECK(body.steering && body.zeroed == 0);  // nothing removed while the AI may still decide
    body.refuse_ai = false;                    // the layer is asked for again at every Pre
    w.step(2);
    CHECK(w.query(p.lease).lease_state == KILN_CONTROL_ACTIVE && body.ai_off == 1);
    World w2;
    FakeBody& b2 = w2.add(2000);
    b2.refuse_ai = true;
    const KilnControlState p2 = w2.acquire(w2.bind(2000));
    w2.step(5 * 25 + 2);
    const KilnControlState f = w2.query(p2.lease);
    CHECK(f.lease_state == KILN_CONTROL_FAILED && f.result == KILN_CONTROL_RESULT_TIMED_OUT && std::strncmp(f.reason, "AiLayer", 7) == 0);
    CHECK(b2.steering && b2.zeroed == 0);
}

void test_transition_timeout() {
    World w;
    FakeBody& body = w.add(2000);
    body.refuse_remove = true;
    const KilnControlState p = w.acquire(w.bind(2000));
    w.step(13);  // the removal Pre and 12 more: 480 ms
    CHECK(w.query(p.lease).lease_state == KILN_CONTROL_PENDING);
    w.step();    // 520 ms after the removal was queued
    const KilnControlState f = w.query(p.lease);
    CHECK(f.lease_state == KILN_CONTROL_FAILED && f.result == KILN_CONTROL_RESULT_TIMED_OUT);
    CHECK(std::strncmp(f.reason, "Transition", 10) == 0);
    w.step(2);
    CHECK(body.ai_off == 0 && body.steering);
}

void test_cancel_pending() {
    World w;
    FakeBody& body = w.add(2000);
    body.action = true;
    const KilnControlState p = w.acquire(w.bind(2000));
    w.step(2);
    CHECK(body.ai_off == 1);
    CHECK(w.broker.release(kOwner, p.lease) == KILN_OK);
    w.step();
    const KilnControlState c = w.query(p.lease);
    CHECK(c.lease_state == KILN_CONTROL_RELEASED && c.result == KILN_CONTROL_RESULT_CANCELED);
    CHECK(body.ai_off == 0 && body.steering && body.zeroed == 0);  // nothing was removed: nothing zeroed
}

void test_body_lost() {
    World w;
    FakeBody& body = w.add(2000);
    (void)body;
    const KilnControlBodyKey k = w.bind(2000);
    const KilnControlState p = w.acquire(k);
    w.step(2);
    CHECK(w.broker.can_drive(kOwner, p.lease, k, 1) == 1);
    w.game.bodies.erase(2000);
    w.step();
    const KilnControlState g = w.query(p.lease);
    CHECK(g.lease_state == KILN_CONTROL_FAILED && g.result == KILN_CONTROL_RESULT_BODY_GONE);
    CHECK(w.broker.can_drive(kOwner, p.lease, k, 1) == 0);
    CHECK(w.broker.release(kOwner, p.lease) == KILN_OK);  // terminal: nothing to do
    CHECK(w.broker.release_body(kOwner, k) == KILN_OK);
    CHECK(w.broker.bodies() == 0);
}

void test_restore_failed() {
    World w;
    FakeBody& body = w.add(2000);
    const KilnControlState p = w.acquire(w.bind(2000));
    w.step(2);
    body.refuse_add = true;
    CHECK(w.broker.release(kOwner, p.lease) == KILN_OK);
    w.step(3 + 13);
    const KilnControlState f = w.query(p.lease);
    CHECK(f.lease_state == KILN_CONTROL_FAILED && f.result == KILN_CONTROL_RESULT_RESTORE_FAILED);
    // never left without its SteeringAgent for nobody: quarantined and retired through kiln.spawn, its root denied
    CHECK(w.game.destroyed == 1 && w.game.bodies.count(2000) == 0);
    CHECK(w.broker.root_policy(1, 2000) == 1 && w.broker.bodies() == 0);
}

// A refused release of our layer must not be reported as a restored, permanently AI-disabled body.
void test_ai_release_refused() {
    for (bool combat : {false, true}) {
        World w;
        FakeBody& body = w.add(2000);
        const auto key = w.bind(2000);
        const auto follow = w.acquire(key);
        w.step(2);
        body.refuse_ai = true;
        KilnHandle ending = follow.lease;
        if (combat) ending = w.acquire(key, KILN_CONTROL_ROLE_PET_COMBAT).lease;
        else CHECK(w.broker.release(kOwner, ending) == KILN_OK);
        w.step(8);
        CHECK(w.query(ending).lease_state == KILN_CONTROL_PENDING ||
              w.query(ending).lease_state == KILN_CONTROL_ACTIVE);
        CHECK(w.broker.can_drive(kOwner, ending, key, 1) == 0);
        body.refuse_ai = false;
        w.step(2);
        CHECK(body.ai_off == 0);
        CHECK(w.query(ending).lease_state == (combat ? KILN_CONTROL_ACTIVE : KILN_CONTROL_RELEASED));
    }
    World fatal;
    auto& body = fatal.add(2000);
    const auto lease = fatal.acquire(fatal.bind(2000));
    fatal.step(2);
    body.refuse_ai = true;
    CHECK(fatal.broker.release(kOwner, lease.lease) == KILN_OK);
    fatal.step(25);
    CHECK(fatal.game.bodies.count(2000) == 0);
    CHECK(fatal.query(lease.lease).result == KILN_CONTROL_RESULT_RESTORE_FAILED);
}

// The removal settles at the end of the first step, before the next Pre can grant Vehicle.
void test_vehicle_canceled_during_removal() {
    for (bool refuse_steering : {false, true}) {
        World w;
        w.add(2000).refuse_remove = refuse_steering;
        const auto key = w.bind(2000);
        const auto vehicle = w.acquire(key, KILN_CONTROL_ROLE_VEHICLE);
        w.step();
        CHECK(!w.game.bodies[2000].enemy_ai);
        CHECK(w.broker.release(kOwner, vehicle.lease) == KILN_OK);
        w.step(20);
        CHECK(w.game.bodies.count(2000) == 0);
        CHECK(w.query(vehicle.lease).lease_state == KILN_CONTROL_FAILED);
        CHECK(w.broker.root_policy(1, 2000) == 1);
    }
}

void test_active_external_steering_conflict() {
    World w;
    auto& body = w.add(2000);
    const auto key = w.bind(2000);
    const auto follow = w.acquire(key);
    w.step(2);
    body.steering = true;  // another controller put steering back while this lease owned direct input
    w.step();
    CHECK(w.broker.can_drive(kOwner, follow.lease, key, 1) == 0);
    CHECK(w.query(follow.lease).lease_state == KILN_CONTROL_FAILED);
    CHECK(std::strncmp(w.query(follow.lease).reason, "ExternalController", 18) == 0);
    w.step(2);
    CHECK(body.steering && body.ai_off == 0);
    World idle;
    auto& idle_body = idle.add(2000);
    const auto idle_key = idle.bind(2000);
    idle_body.steering = false;  // removed after binding, before the first snapshot
    const auto pending = idle.acquire(idle_key);
    idle.step(3);
    CHECK(idle.query(pending.lease).lease_state == KILN_CONTROL_FAILED);
    CHECK(std::strncmp(idle.query(pending.lease).reason, "ExternalController", 18) == 0);
}

void test_snapshot_failure_is_bounded() {
    World w;
    auto& body = w.add(2000);
    body.refuse_snapshot = true;
    const auto pending = w.acquire(w.bind(2000));
    w.step(130);
    CHECK(w.query(pending.lease).lease_state == KILN_CONTROL_FAILED);
    CHECK(w.query(pending.lease).result == KILN_CONTROL_RESULT_TIMED_OUT);
    CHECK(body.ai_off == 0 && body.steering);
}

void test_owner_retirement() {
    World w;
    auto& body = w.add(2000);
    const auto key = w.bind(2000);
    w.bind(2000, kOther);
    const auto follow = w.acquire(key);
    w.step(2);
    const auto ride = w.acquire(key, KILN_CONTROL_ROLE_RIDDEN, kOther);
    w.step();
    w.broker.owner_retiring(kOther);
    CHECK(w.broker.can_drive(kOther, ride.lease, key, 2) == 0);
    w.step(2);
    CHECK(w.query(ride.lease).result == KILN_CONTROL_RESULT_OWNER_UNLOADED);
    CHECK(w.query(follow.lease).lease_state == KILN_CONTROL_ACTIVE);
    CHECK(!body.steering && body.ai_off == 1);
    CHECK(w.broker.release_body(kOther, key) == KILN_ERR_NOT_FOUND);
    w.broker.owner_retiring(kOwner);
    w.step(5);
    CHECK(w.query(follow.lease).result == KILN_CONTROL_RESULT_OWNER_UNLOADED);
    CHECK(body.steering && body.ai_off == 0);
    CHECK(w.broker.bodies() == 0);
    KilnControlBodyKey ignored{};
    CHECK(w.broker.bind(kOwner, 2000, 0, 1, &ignored) == KILN_ERR_REVOKED);
    World lost;
    lost.add(2000);
    lost.bind(2000);
    const auto l = lost.acquire(lost.bind(2000));
    lost.step(2);
    lost.game.bodies.erase(2000);
    lost.step();
    CHECK(lost.query(l.lease).result == KILN_CONTROL_RESULT_BODY_GONE);
    lost.broker.owner_retiring(kOwner);
    lost.step();
    CHECK(lost.broker.bodies() == 0);
}

void test_terminal_pressure_keeps_retained_leases() {
    World w;
    w.add(2000);
    const auto key = w.bind(2000);
    const auto first = w.acquire(key);
    for (uint32_t i = 1; i < kLeaseSlots; ++i) {
        CHECK(w.acquire(key, i % 2 ? KILN_CONTROL_ROLE_PET_STAY : KILN_CONTROL_ROLE_PET_FOLLOW).lease != 0);
        if (i % 500 == 0) w.step();
    }
    const auto refused = w.acquire(key, KILN_CONTROL_ROLE_PET_FOLLOW);
    CHECK(refused.lease == 0 && std::strncmp(refused.reason, "Capacity", 8) == 0);
    CHECK(w.query(first.lease).lease_state == KILN_CONTROL_RELEASED);
    const auto replay = w.acquire(key, KILN_CONTROL_ROLE_PET_FOLLOW, kOwner, nullptr, first.request_id);
    CHECK(replay.lease == first.lease);
}

void test_quarantine_during_body_slot_reuse() {
    World w;
    uint64_t generation = 0;
    std::atomic<bool> stop{false};
    std::mutex reader_mu;
    std::condition_variable drained;
    int reading = 0;
    std::vector<std::thread> readers;
    for (int i = 0; i < 5; ++i) readers.emplace_back([&] {
        while (!stop.load()) {
            uint64_t g;
            {
                std::lock_guard lock(reader_mu);
                g = generation;
                if (g) ++reading;
            }
            if (g) {
                w.broker.quarantine(KilnControlBodyKey{1, g, static_cast<KilnEntity>(9999 + g), 0}, KILN_CONTROL_CAUSE_RELEASE);
                std::lock_guard lock(reader_mu);
                if (--reading == 0) drained.notify_one();
            } else std::this_thread::yield();
        }
    });
    bool wrong_body_denied = false;
    for (uint32_t i = 0; i < 10000 && !wrong_body_denied; ++i) {
        const KilnEntity a = 10000 + 2 * i, b = a + 1;
        w.add(a);
        const auto key = w.bind(a);
        {
            std::lock_guard lock(reader_mu);
            generation = key.body_generation;
        }
        w.game.bodies.erase(a);
        CHECK(w.broker.body_retired(kOwner, key) == KILN_OK);
        w.add(b);
        const auto next = w.bind(b);
        {
            std::unique_lock lock(reader_mu);
            generation = 0;
            drained.wait(lock, [&] { return reading == 0; });
        }
        wrong_body_denied = w.broker.root_policy(1, b) != 0;
        if (!wrong_body_denied) CHECK(w.broker.release_body(kOwner, next) == KILN_OK);
    }
    stop.store(true);
    for (auto& thread : readers) thread.join();
    CHECK(!wrong_body_denied);  // a stale key can never quarantine the different body that took its slot
}

void test_one_lease_and_takeover() {
    World w;
    FakeBody& body = w.add(2000);
    const KilnControlBodyKey k = w.bind(2000);
    w.bind(2000, kOther);
    const KilnControlState p = w.acquire(k);
    KilnResult rc = 0;
    const KilnControlState b = w.acquire(k, KILN_CONTROL_ROLE_PET_FOLLOW, kOther, &rc);
    CHECK(rc == KILN_OK && b.lease == 0 && b.result == KILN_CONTROL_RESULT_BUSY);  // one lease per body in this slice
    w.step(2);
    CHECK(w.query(p.lease).lease_state == KILN_CONTROL_ACTIVE);
    // released while another owner asks: the new lease takes the body over without a restore in between
    CHECK(w.broker.release(kOwner, p.lease) == KILN_OK);
    const KilnControlState n = w.acquire(k, KILN_CONTROL_ROLE_PET_FOLLOW, kOther, &rc);
    CHECK(rc == KILN_OK && n.lease != 0 && n.lease_state == KILN_CONTROL_PENDING);
    w.step();
    const KilnControlState na = w.query(n.lease);
    CHECK(na.lease_state == KILN_CONTROL_ACTIVE && na.transition_sequence == 2);
    CHECK(w.query(p.lease).lease_state == KILN_CONTROL_RELEASED);
    CHECK(!body.steering && body.ai_off == 1);
    CHECK(w.broker.can_drive(kOther, n.lease, k, 2) == 1 && w.broker.can_drive(kOwner, p.lease, k, 1) == 0);
    // a new lease on the restored live body takes a fresh baseline
    CHECK(w.broker.release(kOther, n.lease) == KILN_OK);
    w.step(5);
    CHECK(body.steering && body.ai_off == 0);
    body.steering_bytes[50] = 0xEE;  // the game changed its configuration meanwhile
    const KilnControlState m = w.acquire(k);
    w.step(2);
    CHECK(w.query(m.lease).lease_state == KILN_CONTROL_ACTIVE && w.query(m.lease).transition_sequence == 3);
    CHECK(w.broker.release(kOwner, m.lease) == KILN_OK);
    w.step(5);
    CHECK(body.steering_bytes[50] == 0xEE);
}

void test_explicit_no_ai() {
    World w;
    FakeBody& body = w.add(2000);
    body.steering = false;
    body.enemy_ai = false;
    KilnControlBodyKey k{};
    CHECK(w.broker.bind(kOwner, 2000, KILN_CONTROL_BASELINE_EXPLICIT_NO_AI, 1, &k) == KILN_OK);
    const KilnControlState p = w.acquire(k, KILN_CONTROL_ROLE_RIDDEN);
    w.step();
    const KilnControlState a = w.query(p.lease);
    CHECK(a.lease_state == KILN_CONTROL_ACTIVE && a.changed_mask == 0);  // absent at baseline stays absent
    CHECK(body.ai_off == 0);
    CHECK(w.broker.release(kOwner, p.lease) == KILN_OK);
    w.step();
    CHECK(w.query(p.lease).lease_state == KILN_CONTROL_RELEASED && !body.steering && body.zeroed == 0);
}

void test_world_end() {
    World w;
    FakeBody& body = w.add(2000);
    const KilnControlBodyKey k = w.bind(2000);
    const KilnControlState p = w.acquire(k);
    w.step(2);
    w.broker.set_world(2, "test");
    const KilnControlState e = w.query(p.lease);
    CHECK(e.lease_state == KILN_CONTROL_FAILED && e.result == KILN_CONTROL_RESULT_WORLD_ENDED);
    CHECK(w.broker.can_drive(kOwner, p.lease, k, 1) == 0);
    CHECK(w.broker.bodies() == 0);
    w.step(5);
    CHECK(!body.steering);  // nothing restored into another world
}

void test_notices_and_capacity() {
    World w;
    w.add(2000);
    const KilnControlBodyKey k = w.bind(2000);
    const KilnControlState p = w.acquire(k);
    w.step(2);
    KilnControlState out[16]{};
    uint32_t n = 0, resync = 9;
    CHECK(w.broker.notices(kOwner, out, 16, &n, &resync) == KILN_OK);
    CHECK(n >= 2 && resync == 0);
    CHECK(out[0].lease == p.lease && out[0].lease_state == KILN_CONTROL_PENDING);
    CHECK(out[n - 1].lease_state == KILN_CONTROL_ACTIVE && out[n - 1].transition_sequence == 1);
    for (uint32_t i = 1; i < n; ++i) CHECK(out[i].notice_sequence == out[i - 1].notice_sequence + 1);
    CHECK(out[n - 1].size == sizeof(KilnControlState));
    // a full notice queue drops and asks for a resync; the registry stays the truth
    w.broker.set_notice_capacity(0);
    CHECK(w.broker.release(kOwner, p.lease) == KILN_OK);
    w.step(6);
    CHECK(w.broker.notices(kOwner, out, 16, &n, &resync) == KILN_OK && n == 0 && resync == 1);
    CHECK(w.query(p.lease).lease_state == KILN_CONTROL_RELEASED);
    CHECK(w.broker.notices(kOwner, out, 16, &n, &resync) == KILN_OK && resync == 0);  // reported once
    // request queue capacity 0 refuses before allocation
    w.broker.set_request_capacity(0);
    KilnResult rc = 0;
    const KilnControlState q = w.acquire(k, KILN_CONTROL_ROLE_PET_FOLLOW, kOwner, &rc);
    CHECK(rc == KILN_OK && q.lease == 0 && q.result == KILN_CONTROL_RESULT_BUSY && std::strncmp(q.reason, "QueueFull", 9) == 0);
    CHECK(w.broker.live_leases() == 0);
}

void test_retention() {
    World w;
    w.add(2000);
    const KilnControlBodyKey k = w.bind(2000);
    const KilnControlState p = w.acquire(k);
    w.step(2);
    CHECK(w.broker.release(kOwner, p.lease) == KILN_OK);
    w.step(5);
    CHECK(w.query(p.lease).lease_state == KILN_CONTROL_RELEASED);
    w.step(25 * 590);
    CHECK(w.query(p.lease).lease_state == KILN_CONTROL_RELEASED);  // under 10 minutes
    w.step(25 * 15);
    const KilnControlState x = w.query(p.lease);
    CHECK(x.lease_state == KILN_CONTROL_FAILED && x.result == KILN_CONTROL_RESULT_EXPIRED_HANDLE);
    CHECK(w.broker.release(kOwner, p.lease) == KILN_ERR_NOT_FOUND);
    // an unknown handle: ExpiredHandle, no authority
    const KilnControlState u = w.query(0x12345);
    CHECK(u.result == KILN_CONTROL_RESULT_EXPIRED_HANDLE && w.broker.can_drive(kOwner, 0x12345, k, 1) == 0);
    KilnControlState s{};
    s.size = sizeof s;
    CHECK(w.broker.query(0, &s) == KILN_ERR_INVALID_ARGUMENT);
}


// ------------------------------------------------------------------------- roles (S5), mirroring contract_model.py --

KilnControlState get(World& w, KilnHandle h) { return w.query(h); }

// test_14: Ridden preempts Follow without a second snapshot; its release resumes Follow (new sequence, AI still off)
void test_ridden_preempts_and_resumes() {
    World w;
    FakeBody& body = w.add(2000);
    const KilnControlBodyKey k = w.bind(2000);
    w.bind(2000, kOther);
    const KilnControlState f = w.acquire(k, KILN_CONTROL_ROLE_PET_FOLLOW);
    w.step(2);
    CHECK(get(w, f.lease).lease_state == KILN_CONTROL_ACTIVE);
    const int snaps = static_cast<int>(std::count_if(w.game.lines.begin(), w.game.lines.end(), [](const std::string&) { return false; }));
    (void)snaps;
    const KilnControlState r = w.acquire(k, KILN_CONTROL_ROLE_RIDDEN, kOther);
    CHECK(r.lease_state == KILN_CONTROL_PENDING);
    body.steering_bytes[40] = 0x99;  // a second snapshot would take this
    w.step();
    const KilnControlState fp = get(w, f.lease), ra = get(w, r.lease);
    CHECK(fp.lease_state == KILN_CONTROL_PREEMPTED && fp.result == KILN_CONTROL_RESULT_PREEMPTED);
    CHECK(w.broker.can_drive(kOwner, f.lease, k, 1) == 0);
    CHECK(ra.lease_state == KILN_CONTROL_ACTIVE && ra.transition_sequence == 2 && ra.effective_role == KILN_CONTROL_ROLE_RIDDEN);
    CHECK(fp.effective_role == KILN_CONTROL_ROLE_RIDDEN && fp.winning_lease == r.lease);  // a Preempted lease sees the winner
    CHECK(!body.steering && body.ai_off == 1);
    CHECK(w.broker.release(kOther, r.lease) == KILN_OK);
    w.step();
    const KilnControlState back = get(w, f.lease);
    CHECK(back.lease_state == KILN_CONTROL_ACTIVE && back.transition_sequence == 3);  // resumed through verification
    CHECK(get(w, r.lease).lease_state == KILN_CONTROL_RELEASED);
    CHECK(!body.steering && body.ai_off == 1);  // AI still off, SteeringAgent still absent
    CHECK(w.broker.release(kOwner, f.lease) == KILN_OK);
    w.step(5);
    CHECK(body.steering && body.steering_bytes[40] != 0x99 && body.steering_bytes[40] == 41);  // the one original snapshot
}

// test_15: the last release releases only the broker's layer; another owner's stays
void test_last_release_only_own_layer() {
    World w;
    FakeBody& body = w.add(2000);
    const KilnControlBodyKey k = w.bind(2000);
    const KilnControlState f = w.acquire(k);
    w.step(2);
    body.other_ai_off = true;
    CHECK(w.broker.release(kOwner, f.lease) == KILN_OK);
    w.step(5);
    CHECK(body.steering && body.ai_off == 0 && !w.game.ai_on(2000));
}

// test_16 + test_23: a Vehicle stays the body's no-AI base when the driver leaves; its release needs retirement
void test_vehicle() {
    World w;
    FakeBody& body = w.add(2000);
    const KilnControlBodyKey k = w.bind(2000);
    const KilnControlState v = w.acquire(k, KILN_CONTROL_ROLE_VEHICLE);
    w.step(2);
    const KilnControlState va = get(w, v.lease);
    CHECK(va.lease_state == KILN_CONTROL_ACTIVE && !body.steering && !body.enemy_ai && body.behavior_removals == 1);
    CHECK((va.changed_mask & KILN_CONTROL_CHANGED_VEHICLE_BEHAVIOR) != 0);
    const KilnControlState r = w.acquire(k, KILN_CONTROL_ROLE_RIDDEN);
    w.step();
    CHECK(get(w, r.lease).lease_state == KILN_CONTROL_ACTIVE && get(w, v.lease).lease_state == KILN_CONTROL_PREEMPTED);
    CHECK(w.broker.release(kOwner, r.lease) == KILN_OK);
    w.step(2);
    CHECK(get(w, v.lease).lease_state == KILN_CONTROL_ACTIVE);
    CHECK(!body.steering && !body.enemy_ai);  // no AI, no steering: never put back
    CHECK(w.broker.release(kOwner, v.lease) == KILN_ERR_INVALID_ARGUMENT);  // RetireRequired
    CHECK(get(w, v.lease).lease_state == KILN_CONTROL_ACTIVE);
    // the owner destroys the vehicle and says so: Retired, its root denied for the epoch
    CHECK(w.broker.body_retired(kOwner, k) == KILN_ERR_INVALID_ARGUMENT);  // alive: refused
    w.game.bodies.erase(2000);
    CHECK(w.broker.body_retired(kOwner, k) == KILN_OK);
    CHECK(get(w, v.lease).lease_state == KILN_CONTROL_FAILED && get(w, v.lease).result == KILN_CONTROL_RESULT_RETIRED);
    CHECK(w.broker.root_policy(1, 2000) == 1 && w.broker.root_policy(2, 2000) == 0 && w.broker.bodies() == 0);
}

// test_26: the same owner's new base role replaces the old atomically, without a restore in between
void test_same_owner_replacement() {
    World w;
    FakeBody& body = w.add(2000);
    const KilnControlBodyKey k = w.bind(2000);
    const KilnControlState f = w.acquire(k, KILN_CONTROL_ROLE_PET_FOLLOW);
    w.step(2);
    const KilnControlState st = w.acquire(k, KILN_CONTROL_ROLE_PET_STAY);
    const KilnControlState old = get(w, f.lease);
    CHECK(st.lease_state == KILN_CONTROL_PENDING);
    CHECK(old.lease_state == KILN_CONTROL_RELEASED && old.result == KILN_CONTROL_RESULT_CANCELED && std::strncmp(old.reason, "Replaced", 8) == 0);
    CHECK(w.broker.can_drive(kOwner, f.lease, k, 1) == 0);
    const int zero_before = body.zeroed;
    w.step();
    CHECK(get(w, st.lease).lease_state == KILN_CONTROL_ACTIVE && get(w, st.lease).transition_sequence == 2);
    CHECK(!body.steering && body.ai_off == 1 && body.zeroed == zero_before);  // no restore, no zeroing in between
    // a release of the replaced lease is idempotent and keeps its reason
    CHECK(w.broker.release(kOwner, f.lease) == KILN_OK && std::strncmp(get(w, f.lease).reason, "Replaced", 8) == 0);
}

// test_27: another owner's base request is refused and the winner keeps driving
void test_foreign_base_conflict() {
    World w;
    w.add(2000);
    const KilnControlBodyKey k = w.bind(2000);
    w.bind(2000, kOther);
    const KilnControlState f = w.acquire(k);
    w.step(2);
    KilnResult rc = 0;
    const KilnControlState x = w.acquire(k, KILN_CONTROL_ROLE_PET_STAY, kOther, &rc);
    CHECK(rc == KILN_OK && x.lease == 0 && x.result == KILN_CONTROL_RESULT_BUSY);
    CHECK(w.broker.can_drive(kOwner, f.lease, k, 1) == 1);
    // and one Ridden lease per body: the second rider of any owner shares the first (the mount service's job)
    const KilnControlState r1 = w.acquire(k, KILN_CONTROL_ROLE_RIDDEN, kOther);
    const KilnControlState r2 = w.acquire(k, KILN_CONTROL_ROLE_RIDDEN, kOwner);
    CHECK(r1.lease != 0 && r2.lease == 0 && r2.result == KILN_CONTROL_RESULT_BUSY);
}

// a lease admitted under a higher one is Preempted from the start
void test_admitted_preempted() {
    World w;
    w.add(2000);
    const KilnControlBodyKey k = w.bind(2000);
    const KilnControlState r = w.acquire(k, KILN_CONTROL_ROLE_RIDDEN);
    w.step(2);
    const KilnControlState f = w.acquire(k, KILN_CONTROL_ROLE_PET_FOLLOW);
    CHECK(f.lease != 0 && f.lease_state == KILN_CONTROL_PREEMPTED && f.result == KILN_CONTROL_RESULT_PREEMPTED);
    w.step(3);
    CHECK(get(w, f.lease).lease_state == KILN_CONTROL_PREEMPTED && get(w, r.lease).lease_state == KILN_CONTROL_ACTIVE);
}

// AI-on roles: the SteeringAgent back, the broker's layer released, Active only when the AI runs
void test_ai_on_roles() {
    World w;
    FakeBody& body = w.add(2000);
    const KilnControlBodyKey k = w.bind(2000);
    const KilnControlState f = w.acquire(k, KILN_CONTROL_ROLE_PET_FOLLOW);
    w.step(2);
    const KilnControlState c = w.acquire(k, KILN_CONTROL_ROLE_PET_COMBAT);
    w.step();
    CHECK(get(w, f.lease).lease_state == KILN_CONTROL_PREEMPTED);
    CHECK(get(w, c.lease).lease_state == KILN_CONTROL_PENDING);  // input zeroed while the SteeringAgent comes back
    w.step(3);
    const KilnControlState ca = get(w, c.lease);
    CHECK(ca.lease_state == KILN_CONTROL_ACTIVE && ca.effective_role == KILN_CONTROL_ROLE_PET_COMBAT && ca.changed_mask == 0);
    CHECK(body.steering && body.ai_off == 0 && w.game.ai_on(2000));
    // combat over: Follow resumes and takes the SteeringAgent away again (the same baseline)
    CHECK(w.broker.release(kOwner, c.lease) == KILN_OK);
    w.step(2);
    CHECK(get(w, f.lease).lease_state == KILN_CONTROL_ACTIVE && !body.steering && body.ai_off == 1);
    // another owner holds the AI off: an AI-on role fails AiInhibited, never overriding it
    body.other_ai_off = true;
    const KilnControlState g = w.acquire(k, KILN_CONTROL_ROLE_ENGINE_GUARD);  // replaces Follow (same owner, base group)
    w.step(6);
    const KilnControlState gi = get(w, g.lease);
    CHECK(gi.lease_state == KILN_CONTROL_FAILED && gi.result == KILN_CONTROL_RESULT_AI_INHIBITED);
    w.step(3);
    CHECK(body.ai_off == 0 && body.other_ai_off && body.steering);  // only the broker's own layer went
    // a body without AI cannot take an AI-on role
    World w2;
    FakeBody& b2 = w2.add(2000);
    b2.enemy_ai = false;
    const KilnControlState c2 = w2.acquire(w2.bind(2000), KILN_CONTROL_ROLE_PET_COMBAT);
    w2.step(2);
    CHECK(get(w2, c2.lease).result == KILN_CONTROL_RESULT_AI_INHIBITED);
}

// ------------------------------------------------------------------- quarantine, retirement, deny table (S6) --

void test_quarantine() {
    World w;
    FakeBody& body = w.add(2000);
    const KilnControlBodyKey k = w.bind(2000);
    const KilnControlState f = w.acquire(k);
    w.step(2);
    CHECK(w.broker.can_drive(kOwner, f.lease, k, 1) == 1);
    CHECK(w.broker.root_policy(1, 2000) == 0);
    CHECK(w.broker.quarantine(k, 0) == KILN_ERR_INVALID_ARGUMENT && w.broker.quarantine(k, 10) == KILN_ERR_INVALID_ARGUMENT);
    KilnControlBodyKey stale = k;
    stale.body_generation += 7;
    CHECK(w.broker.quarantine(stale, KILN_CONTROL_CAUSE_DISMISS) == KILN_ERR_NOT_FOUND);
    // a voluntary cause: revoked in the same step (no Pre in between), its root denied at once
    CHECK(w.broker.quarantine(k, KILN_CONTROL_CAUSE_DISMISS) == KILN_OK);
    CHECK(w.broker.can_drive(kOwner, f.lease, k, 1) == 0);
    CHECK(w.broker.root_policy(1, 2000) == 1);
    const KilnControlState q = get(w, f.lease);
    CHECK(q.lease_state == KILN_CONTROL_PREEMPTED && q.effective_role == KILN_CONTROL_ROLE_QUARANTINE && q.winning_lease == 0);
    // a semantic down overrides it; a later voluntary cause does not
    CHECK(w.broker.quarantine(k, KILN_CONTROL_CAUSE_NATIVE_LETHAL) == KILN_OK);
    CHECK(w.broker.quarantine(k, KILN_CONTROL_CAUSE_RELEASE) == KILN_OK);
    CHECK(std::strstr(get(w, f.lease).reason, "cause 1") != nullptr);
    w.step(3);
    CHECK(get(w, f.lease).lease_state == KILN_CONTROL_PREEMPTED);
    CHECK(!body.steering);  // never restored: a quarantined body is retired
    CHECK(w.broker.release_body(kOwner, k) == KILN_ERR_INVALID_ARGUMENT);  // never unbound early
    CHECK(w.broker.release(kOwner, f.lease) == KILN_OK);  // the release waits for the retirement, the result stays
    KilnResult rc = 0;
    const KilnControlState again = w.acquire(k, KILN_CONTROL_ROLE_PET_STAY, kOwner, &rc);
    CHECK(again.lease == 0 && std::strncmp(again.reason, "Quarantined", 11) == 0);
    w.game.bodies.erase(2000);  // the owner (pets) destroys it
    w.step();
    const KilnControlState rt = get(w, f.lease);
    CHECK(rt.lease_state == KILN_CONTROL_FAILED && rt.result == KILN_CONTROL_RESULT_RETIRED);
    CHECK(w.broker.root_policy(1, 2000) == 1 && w.broker.bodies() == 0);
    // a later body of the same world keeps the old root denied, and is not itself denied
    w.add(2001);
    const KilnControlBodyKey k2 = w.bind(2001);
    CHECK(k2.body_generation != k.body_generation && w.broker.root_policy(1, 2000) == 1 && w.broker.root_policy(1, 2001) == 0);
    // the world's end clears the table
    w.broker.set_world(2, "test");
    CHECK(w.broker.root_policy(1, 2000) == 0 && w.broker.root_policy(2, 2000) == 0 && w.broker.denied_roots() == 0);
}

void test_tombstone_ceiling() {
    World w;
    KilnControlBodyKey k{};
    // every retired body keeps its entry for the epoch; an unbound live one gives its reservation back
    w.add(3000);
    CHECK(w.broker.bind(kOwner, 3000, 0, 1, &k) == KILN_OK && w.broker.tombstones_reserved() == 1);
    CHECK(w.broker.release_body(kOwner, k) == KILN_OK && w.broker.tombstones_reserved() == 0);
    for (uint32_t i = 0; i < KILN_CONTROL_MAX_TOMBSTONES; ++i) {
        const KilnEntity e = 10000 + i;
        w.add(e);
        if (w.broker.bind(kOwner, e, 0, 1, &k) != KILN_OK) break;
        w.game.bodies.erase(e);
        w.broker.body_retired(kOwner, k);
    }
    CHECK(w.broker.tombstones_reserved() == KILN_CONTROL_MAX_TOMBSTONES && w.broker.denied_roots() == KILN_CONTROL_MAX_TOMBSTONES);
    w.add(90000);
    k = KilnControlBodyKey{};
    CHECK(w.broker.bind(kOwner, 90000, 0, 1, &k) == KILN_ERR_BUFFER_TOO_SMALL && k.body_generation == 0);
    CHECK(w.broker.root_policy(1, 10000) == 1 && w.broker.root_policy(1, 10000 + KILN_CONTROL_MAX_TOMBSTONES - 1) == 1);
}

void test_identity_fault() {
    World w;
    w.add(2000);
    w.add(2001);
    const KilnControlBodyKey k = w.bind(2000);
    const KilnControlState f = w.acquire(k);
    w.game.counter = 5000;
    w.step(2);
    CHECK(get(w, f.lease).lease_state == KILN_CONTROL_ACTIVE);
    w.game.counter = 4000;  // the counter went down: ids may name other entities now
    w.step();
    CHECK(w.broker.identity_fault() && w.broker.can_drive(kOwner, f.lease, k, 1) == 0);
    CHECK(w.game.destroyed == 0);  // quarantined, never destroyed by the broker
    KilnControlBodyKey k2{};
    CHECK(w.broker.bind(kOwner, 2001, 0, 1, &k2) == KILN_ERR_UNAVAILABLE);
    w.broker.withdraw("the program stopped after the counter identity fault");
    w.step();
    CHECK(w.game.destroyed == 0);  // withdrawal cannot destroy an id that may now name another entity
    w.broker.set_operational(true, "test: dependencies recovered");
    w.broker.set_world(2, "test");
    CHECK(!w.broker.identity_fault() && w.broker.bind(kOwner, 2001, 0, 1, &k2) == KILN_OK);
}

// ----------------------------------------------------------------- injection, withdrawal, world end (S7, gate 3) --

void test_injection_and_withdrawal() {
    World w;
    FakeBody& body = w.add(2000);
    w.add(2001);
    const KilnControlBodyKey k = w.bind(2000);
    const KilnControlBodyKey k1 = w.bind(2001);
    // a transition bound of one step: a removal that does not verify at once fails
    w.broker.set_transition_timeout(kStepNs);
    body.refuse_remove = true;
    const KilnControlState t = w.acquire(k);
    w.step(3);
    CHECK(get(w, t.lease).result == KILN_CONTROL_RESULT_TIMED_OUT);
    w.broker.set_transition_timeout(0);
    body.refuse_remove = false;
    w.step(3);
    // a restore that never verifies: RestoreFailed, the body quarantined and retired; the lease never fails open
    const KilnControlState a = w.acquire(k);
    w.step(2);
    w.broker.set_fail_restore(true);
    CHECK(w.broker.release(kOwner, a.lease) == KILN_OK);
    w.step(3 + 13);
    CHECK(get(w, a.lease).result == KILN_CONTROL_RESULT_RESTORE_FAILED && w.game.bodies.count(2000) == 0 &&
          w.broker.root_policy(1, 2000) == 1);
    w.broker.set_fail_restore(false);
    // two-phase withdrawal: revoked at once, retired at the next Pre, no new admission
    const KilnControlState b = w.acquire(k1);
    w.step(2);
    CHECK(w.broker.can_drive(kOwner, b.lease, k1, get(w, b.lease).transition_sequence) == 1);
    w.broker.withdraw("test");
    CHECK(w.broker.can_drive(kOwner, b.lease, k1, get(w, b.lease).transition_sequence) == 0);
    w.step();
    CHECK(get(w, b.lease).result == KILN_CONTROL_RESULT_RETIRED && w.game.bodies.count(2001) == 0);
    w.add(2002);
    KilnControlBodyKey k2{};
    CHECK(w.broker.bind(kOwner, 2002, 0, 1, &k2) == KILN_ERR_UNAVAILABLE);
}

// world end with leases in every state: all WorldEnded, nothing restored into the next world
void test_world_switch() {
    World w;
    FakeBody& a = w.add(2000);
    w.add(2001);
    const KilnControlBodyKey ka = w.bind(2000);
    const KilnControlBodyKey kb = w.bind(2001);
    const KilnControlState active = w.acquire(ka);
    w.step(2);
    const KilnControlState preempted = w.acquire(ka, KILN_CONTROL_ROLE_PET_FOLLOW, kOwner);  // replaces: becomes Pending
    w.game.bodies[2001].action = true;
    const KilnControlState pending = w.acquire(kb);
    w.step();
    const KilnControlState ridden = w.acquire(ka, KILN_CONTROL_ROLE_RIDDEN);
    w.broker.set_world(2, "test");
    for (KilnHandle h : {active.lease, preempted.lease, pending.lease, ridden.lease}) {
        const KilnControlState s = get(w, h);
        CHECK(s.lease_state == KILN_CONTROL_FAILED ? s.result == KILN_CONTROL_RESULT_WORLD_ENDED : s.result == KILN_CONTROL_RESULT_CANCELED);
    }
    CHECK(get(w, preempted.lease).result == KILN_CONTROL_RESULT_WORLD_ENDED && get(w, ridden.lease).result == KILN_CONTROL_RESULT_WORLD_ENDED);
    w.step(6);
    CHECK(!a.steering && w.broker.bodies() == 0);
    KilnControlState s{};
    s.size = sizeof s;
    KilnControlRequest q{};
    q.size = sizeof q;
    q.role = KILN_CONTROL_ROLE_PET_FOLLOW;
    q.body = ka;
    q.request_id = 1000;
    CHECK(w.broker.acquire(kOwner, &q, &s) == KILN_OK && s.result == KILN_CONTROL_RESULT_WORLD_ENDED);
}

void test_snapshot_helpers() {
    uint8_t input[456];
    std::memset(input, 0x7F, sizeof input);
    CHECK(zero_input(input, sizeof input, InputLayout{}));
    CHECK(input[0x18c] == 0 && input[0x18c + 11] == 0 && input[0x198] == 0 && input[0x198 + 11] == 0 && input[0x1c4] == 0);
    CHECK(input[0x1b8] == 0x7F && input[0x1c5] == 0x7F && input[0x1a4] == 0x7F && input[0x18b] == 0x7F);  // look kept
    CHECK(!zero_input(input, 100, InputLayout{}));  // a smaller component than the layout: not touched
}

}  // namespace

int main() {
    test_abi();
    test_bind();
    test_bind_refusals();
    test_capacity();
    test_request_ids();
    test_grant_drive_release();
    test_quiescence();
    test_quiescence_timeout();
    test_ai_layer_refused();
    test_transition_timeout();
    test_cancel_pending();
    test_body_lost();
    test_restore_failed();
    test_ai_release_refused();
    test_vehicle_canceled_during_removal();
    test_active_external_steering_conflict();
    test_snapshot_failure_is_bounded();
    test_owner_retirement();
    test_terminal_pressure_keeps_retained_leases();
    test_quarantine_during_body_slot_reuse();
    test_one_lease_and_takeover();
    test_explicit_no_ai();
    test_world_end();
    test_notices_and_capacity();
    test_retention();
    test_snapshot_helpers();
    test_ridden_preempts_and_resumes();
    test_last_release_only_own_layer();
    test_vehicle();
    test_same_owner_replacement();
    test_foreign_base_conflict();
    test_admitted_preempted();
    test_ai_on_roles();
    test_quarantine();
    test_tombstone_ceiling();
    test_identity_fault();
    test_injection_and_withdrawal();
    test_world_switch();
    std::printf("creature_control broker: %d passed, %d failed\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
