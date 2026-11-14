// SPDX-License-Identifier: MIT
// kiln.creature_control on the host (no game, no Wine): the ABI sizes, the body registry (S1) and the life of one lease
// with the transition of its body (S2), on a fake game whose world program applies the broker's ops at the end of a
// step, as the engine applies queued component changes. Mirrors the broker part of out/pets/probes/contract_model.py
// (gate 2: Pending cannot drive, matching Active can, the last release restores only what the broker changed).
//   native/services/creature_control/tests/run.sh
#include <cassert>
#include <cstdio>
#include <cstring>
#include <map>
#include <string>
#include <vector>

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
    bool refuse_remove = false, refuse_add = false, refuse_ai = false;
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
        if (it == bodies.end() || !it->second.steering) return false;
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
    test_one_lease_and_takeover();
    test_explicit_no_ai();
    test_world_end();
    test_notices_and_capacity();
    test_retention();
    test_snapshot_helpers();
    std::printf("creature_control broker: %d passed, %d failed\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
