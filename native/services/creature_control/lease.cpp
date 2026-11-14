// SPDX-License-Identifier: MIT
// kiln.creature_control: the life of a lease and the transition of its body (19d §4.3).
//   acquire  admission only: Pending (or Preempted under a higher lease), never permission. Within the base group
//            (PetFollow, PetStay, EngineGuard) one lease per body: the same owner's new base role replaces its old one at
//            once (Released/Canceled "Replaced", no restore in between), another owner's is Busy. One lease per other role.
//   Pre      per body, at a job barrier: the winner is the highest priority (Quarantine > Ridden > Vehicle > PetCombat >
//            base), the earlier on a tie; the others are Preempted and kept. The winner's transition:
//            - input roles (PetFollow, PetStay, Ridden, Vehicle): the broker's AI-off layer first (no new decision), then
//              the wait for the body's current action to end (5 s), the SteeringAgent snapshot (80 bytes, once per round of
//              leases), its removal through the broker's own world program (a Vehicle also loses its
//              DynamicEnemy01BehaviorState, for good), the check that it is gone (0.5 s), and only then a new
//              transition_sequence and Active;
//            - AI-on roles (PetCombat, EngineGuard): the input zeroed for 3 steps and the SteeringAgent put back (when the
//              broker had removed it), the broker's layer released, and Active only when the AI then runs: another
//              owner's layer still holding it off fails the lease AiInhibited (never overridden).
//   release  drive permission ends at once; when no lease is left the input is zeroed for 3 steps, the saved
//            SteeringAgent goes back with queryId and both velocity vectors zeroed, the broker's AI layer is released,
//            the baseline is used up, and only then the lease reports Released. A Vehicle that was Active is never
//            released on a live body: the body is retired (body_retired).
#include <algorithm>

#include "registry.h"

namespace kcc {

namespace {
bool terminal(uint32_t state) { return state == KILN_CONTROL_RELEASED || state == KILN_CONTROL_FAILED; }
bool same_key(const KilnControlBodyKey& a, const KilnControlBodyKey& b) {
    return a.world_epoch == b.world_epoch && a.body_generation == b.body_generation && a.entity == b.entity &&
           a.reserved == b.reserved;
}
bool base_role(uint32_t role) {
    return role == KILN_CONTROL_ROLE_PET_FOLLOW || role == KILN_CONTROL_ROLE_PET_STAY || role == KILN_CONTROL_ROLE_ENGINE_GUARD;
}
// The game's AI drives the body; the broker only gives the body back to it.
bool ai_role(uint32_t role) { return role == KILN_CONTROL_ROLE_PET_COMBAT || role == KILN_CONTROL_ROLE_ENGINE_GUARD; }
int priority(uint32_t role) {
    switch (role) {
        case KILN_CONTROL_ROLE_QUARANTINE: return 5;
        case KILN_CONTROL_ROLE_RIDDEN: return 4;
        case KILN_CONTROL_ROLE_VEHICLE: return 3;
        case KILN_CONTROL_ROLE_PET_COMBAT: return 2;
        case KILN_CONTROL_ROLE_PET_FOLLOW:
        case KILN_CONTROL_ROLE_PET_STAY:
        case KILN_CONTROL_ROLE_ENGINE_GUARD: return 1;
        default: return 0;
    }
}
}  // namespace

void Broker::refuse(KilnControlState* out, const KilnControlRequest& request, uint32_t result, const char* reason) const {
    KilnControlState s{};
    s.size = sizeof s;
    s.lease_state = KILN_CONTROL_FAILED;
    s.result = result;
    s.requested_role = request.role;
    s.body = request.body;
    s.request_id = request.request_id;
    set_reason(&s, reason);
    *out = s;
}

KilnResult Broker::acquire(KilnOwner owner, const KilnControlRequest* request, KilnControlState* out) {
    if (!out || out->size < sizeof(KilnControlState)) return KILN_ERR_INVALID_ARGUMENT;
    if (!owner || !request || request->size < sizeof(KilnControlRequest) || !request->request_id || request->flags ||
        request->reserved || request->body.reserved || request->role == KILN_CONTROL_ROLE_NONE ||
        request->role == KILN_CONTROL_ROLE_QUARANTINE || request->role > KILN_CONTROL_ROLE_ENGINE_GUARD) {
        KilnControlRequest r{};
        if (request && request->size >= sizeof r) r = *request;
        refuse(out, r, KILN_CONTROL_RESULT_NONE, "InvalidArgument: size, role, flags, reserved fields or request id");
        return KILN_ERR_INVALID_ARGUMENT;
    }
    const KilnControlRequest& q = *request;
    OwnerRec& o = owners_[owner];
    // the same request id again: the same lease (also a terminal one) for the same payload
    if (const auto it = o.requests.find(q.request_id); it != o.requests.end()) {
        Lease* l = lease_by_handle(it->second);
        if (l && l->role == q.role && same_key(l->key, q.body) && l->flags == q.flags) return query(l->handle, out);
        refuse(out, q, KILN_CONTROL_RESULT_NONE, "RequestConflict: this request id was used for another request");
        return KILN_ERR_INVALID_ARGUMENT;
    }
    if (q.request_id <= o.high) {
        refuse(out, q, KILN_CONTROL_RESULT_EXPIRED_HANDLE,
               "ExpiredRequest: the request id is not above the owner's last admitted one; take a new one with next_request");
        return KILN_OK;
    }
    if (!operational_) {
        refuse(out, q, KILN_CONTROL_RESULT_CAPABILITY_OFF, ("CapabilityOff: creature_control is off: " + off_why_).c_str());
        return KILN_OK;
    }
    if (!epoch() || q.body.world_epoch != epoch()) {
        refuse(out, q, KILN_CONTROL_RESULT_WORLD_ENDED, epoch() ? "WorldEnded: the key is of another server world"
                                                                : "WorldEnded: no server world runs here");
        return KILN_OK;
    }
    const uint32_t b = find_body(q.body);
    if (b == kNone || bodies_[b].gone) {
        refuse(out, q, KILN_CONTROL_RESULT_BODY_GONE, "BodyGone: the body is not bound under this key (gone, or stale)");
        return KILN_OK;
    }
    Body& body = bodies_[b];
    if (std::find(body.refs.begin(), body.refs.end(), owner) == body.refs.end()) {
        refuse(out, q, KILN_CONTROL_RESULT_NONE, "NotBound: the owner holds no binding of this body (bind_body first)");
        return KILN_ERR_INVALID_ARGUMENT;
    }
    if (body.quarantined || bodies_pub_[b].latch.load(std::memory_order_acquire)) {
        refuse(out, q, KILN_CONTROL_RESULT_BUSY, "Quarantined: the body is quarantined and will be retired");
        return KILN_OK;
    }
    // the roles already on the body
    uint32_t replace = kNone;
    for (uint32_t slot : body.leases) {
        const Lease& l = leases_[slot];
        if (l.releasing) continue;
        if (base_role(q.role) && base_role(l.role)) {
            if (l.owner != owner) {
                refuse(out, q, KILN_CONTROL_RESULT_BUSY, "Busy: another owner holds the body's base lease");
                return KILN_OK;
            }
            replace = slot;  // the same owner's: replaced atomically once this admission is sure
        } else if (l.role == q.role) {
            refuse(out, q, KILN_CONTROL_RESULT_BUSY, (std::string("Busy: the body has a ") + role_name(q.role) + " lease already").c_str());
            return KILN_OK;
        }
    }
    if (replace == kNone && live_leases_.load(std::memory_order_relaxed) >= KILN_CONTROL_MAX_LEASES) {
        refuse(out, q, KILN_CONTROL_RESULT_BUSY, "Capacity: 1024 leases are open");
        return KILN_OK;
    }
    if (queued_ >= request_cap_) {
        refuse(out, q, KILN_CONTROL_RESULT_BUSY, "QueueFull: the request queue is full this step; try again");
        return KILN_OK;
    }
    const uint32_t slot = alloc_lease();
    if (slot == kNone) {
        refuse(out, q, KILN_CONTROL_RESULT_BUSY, "Capacity: no lease slot is free");
        return KILN_OK;
    }
    // admission is sure: the old base lease of this owner goes now, with no restore in between
    if (replace != kNone)
        terminate(replace, KILN_CONTROL_RELEASED, KILN_CONTROL_RESULT_CANCELED, "Replaced: the owner asked for another base role");
    int top = 0;
    uint32_t top_role = KILN_CONTROL_ROLE_NONE;
    for (uint32_t s : body.leases)
        if (!leases_[s].releasing && priority(leases_[s].role) > top) {
            top = priority(leases_[s].role);
            top_role = leases_[s].role;
        }
    Lease& l = leases_[slot];
    l = Lease{};
    l.used = true;
    l.handle = (++serial_ << kLeaseSlotBits) | slot;
    l.owner = owner;
    l.request_id = q.request_id;
    l.role = q.role;
    l.flags = q.flags;
    l.body = b;
    l.key = body.key;
    l.order = ++order_;
    if (top >= priority(q.role)) {  // a lease of the same or higher priority holds the body: kept, not active
        l.state = KILN_CONTROL_PREEMPTED;
        l.result = KILN_CONTROL_RESULT_PREEMPTED;
        l.reason = std::string("Preempted: a ") + role_name(top_role) + " lease holds the body";
    } else {
        l.state = KILN_CONTROL_PENDING;
        l.result = KILN_CONTROL_RESULT_NONE;
        l.reason = "Pending: waiting for the body's transition";
    }
    body.leases.push_back(slot);
    o.high = q.request_id;
    o.next = std::max(o.next, o.high);
    o.requests[q.request_id] = l.handle;
    ++queued_;
    live_leases_.fetch_add(1, std::memory_order_relaxed);
    publish(slot, true);
    return query(l.handle, out);
}

KilnResult Broker::release(KilnOwner owner, KilnHandle handle) {
    if (!owner || !handle) return KILN_ERR_INVALID_ARGUMENT;
    Lease* l = lease_by_handle(handle);
    if (!l || l->owner != owner) return KILN_ERR_NOT_FOUND;
    if (terminal(l->state) || l->releasing) return KILN_OK;  // idempotent
    const uint32_t slot = static_cast<uint32_t>(handle & (kLeaseSlots - 1));
    Body* body = l->body != kNone && bodies_[l->body].used ? &bodies_[l->body] : nullptr;
    // a Vehicle that held the body keeps it for good: its behaviour is gone, so the body is retired, not handed back
    if (l->role == KILN_CONTROL_ROLE_VEHICLE && l->seq != 0 && body && !body->gone) {
        log(KILN_LOG_WARN, "lease %llu: RetireRequired: a Vehicle lease ends with its body (destroy it, then body_retired)",
            static_cast<unsigned long long>(handle));
        return KILN_ERR_INVALID_ARGUMENT;
    }
    l->releasing = true;
    l->was_active = l->state == KILN_CONTROL_ACTIVE;
    l->reason = "Releasing: drive permission ended; the body is being restored";
    if (body) {
        if (body->winner == slot) body->winner = kNone;  // can_drive is false from this call on
        if (body->target == slot) body->target = kNone;
        publish_body(l->body, true);
    } else {
        publish(slot, true);
    }
    return KILN_OK;
}

void Broker::terminate(uint32_t slot, uint32_t state, uint32_t result, const std::string& reason) {
    Lease& l = leases_[slot];
    if (!l.used || terminal(l.state)) return;
    l.state = state;
    l.result = result;
    l.reason = reason;
    l.releasing = false;
    l.terminal_ns = now_;
    if (l.body != kNone && bodies_[l.body].used) {
        Body& b = bodies_[l.body];
        b.leases.erase(std::remove(b.leases.begin(), b.leases.end(), slot), b.leases.end());
        if (b.winner == slot) b.winner = kNone;
        if (b.target == slot) b.target = kNone;
    }
    live_leases_.fetch_sub(1, std::memory_order_relaxed);
    publish(slot, true);
}

uint32_t Broker::pick_winner(const Body& b) const {
    uint32_t best = kNone;
    for (uint32_t slot : b.leases) {
        const Lease& l = leases_[slot];
        if (l.releasing) continue;
        if (best == kNone || priority(l.role) > priority(leases_[best].role) ||
            (priority(l.role) == priority(leases_[best].role) && l.order < leases_[best].order))
            best = slot;
    }
    return best;
}

std::vector<uint8_t> Broker::restore_bytes(const Body& b) const { return steering_restore_bytes(b.steering, layout_); }

void Broker::grant(uint32_t b, uint32_t slot) {
    Body& body = bodies_[b];
    Lease& l = leases_[slot];
    l.seq = ++body.seq;
    l.state = KILN_CONTROL_ACTIVE;
    l.result = KILN_CONTROL_RESULT_GRANTED;
    l.reason = "Granted";
    body.winner = slot;
    body.phase = Phase::Idle;
    body.target = kNone;
    publish_body(b, true);
}

void Broker::lose_body(uint32_t b, uint32_t result, const std::string& why) {
    Body& body = bodies_[b];
    for (uint32_t slot : std::vector<uint32_t>(body.leases)) terminate(slot, KILN_CONTROL_FAILED, result, why);
    body.gone = true;
    body.winner = kNone;
    body.target = kNone;
    body.phase = Phase::Idle;
    body.have_base = false;
    body.steering.clear();
    body.ai_layer = false;  // the entity is gone: kiln.creatures drops its layers itself
    body.steering_out = false;
    log(KILN_LOG_INFO, "body %u#%llu lost: %s", body.key.entity, static_cast<unsigned long long>(body.key.body_generation),
        why.c_str());
    maybe_free_body(b);
}

// Releasing leases end when the body is settled: another lease won it, or it is back at its baseline.
void Broker::settle_releasing(uint32_t b) {
    Body& body = bodies_[b];
    const bool settled = body.winner != kNone || (body.phase == Phase::Idle && !body.have_base && !body.ai_layer);
    if (!settled) return;
    for (uint32_t slot : std::vector<uint32_t>(body.leases)) {
        const Lease& l = leases_[slot];
        if (!l.releasing) continue;
        terminate(slot, KILN_CONTROL_RELEASED, l.was_active ? KILN_CONTROL_RESULT_RELEASED : KILN_CONTROL_RESULT_CANCELED,
                  l.was_active ? "Released" : "Canceled: released before it was Active");
    }
}

void Broker::enter(uint32_t b, const BodyRead& r, uint32_t want, std::vector<Op>* ops) {
    Body& body = bodies_[b];
    const KilnEntity e = body.key.entity;
    const bool vehicle = leases_[want].role == KILN_CONTROL_ROLE_VEHICLE;
    if (body.target != want || (body.phase != Phase::Quiesce && body.phase != Phase::Removing)) {
        body.phase = Phase::Quiesce;
        body.target = want;
        body.wait_start = now_;
        body.asked_behavior = false;
    }
    // the broker's AI-off layer first: no new decision while the current action ends. Without it an Enemy body is never
    // granted: the entry waits (asking again each Pre) and the 5 s bound fails it.
    if (!body.ai_layer && r.enemy_ai) {
        const KilnResult rc = game_->ai_layer(e, true);
        if (rc == KILN_OK) {
            body.ai_layer = true;
            publish_body(b, true);
        } else if (body.wait_start == now_) {
            log(KILN_LOG_WARN, "body %u: the AI-off layer was refused (%d); the lease waits for it", e, rc);
        }
    }
    const bool ai_off = body.ai_layer || !r.enemy_ai;
    // the body is ours already (another lease had its SteeringAgent removed): hold it still while it changes hands
    const bool ours = body.have_base && body.base_steering && !r.steering;
    if (body.phase == Phase::Quiesce) {
        const bool quiet = ai_off && r.readable && r.ready && !r.dead && !r.action_active && !r.trigger_queued;
        if (!quiet) {
            if (now_ - body.wait_start >= kQuiescenceNs) {
                const char* why = !ai_off      ? "AiLayer: the AI-off layer was refused for 5 s (kiln.creatures ai_switch)"
                                  : !r.readable ? "BodyNotReady: the body's Actor could not be read"
                                  : r.dead      ? "BodyNotReady: the body is dead"
                                  : !r.ready    ? "BodyNotReady: the body is not ready for gameplay (no player near?) after 5 s"
                                                : "Quiescence: the body's current action did not end within 5 s";
                body.phase = Phase::Idle;
                body.target = kNone;
                terminate(want, KILN_CONTROL_FAILED, KILN_CONTROL_RESULT_TIMED_OUT, why);
            } else if (ours) {
                ops->push_back(Op{e, OpKind::ZeroInput, {}});
            }
            return;
        }
        if (!body.have_base) {
            // the snapshot, at this job barrier: the SteeringAgent as it is now, and whether it is there
            body.base_steering = r.steering;
            body.steering.clear();
            if (r.steering && !game_->steering_bytes(e, &body.steering)) return;  // read again next Pre
            body.have_base = true;
        }
        const bool remove_behavior = vehicle && r.enemy_ai;
        if (!r.steering && !remove_behavior) {  // nothing to remove: a body without them, or the broker removed them already
            grant(b, want);
            return;
        }
        body.phase = Phase::Removing;
        body.op_start = now_;
        if (r.steering) ops->push_back(Op{e, OpKind::RemoveSteering, {}});
        if (remove_behavior) {
            ops->push_back(Op{e, OpKind::RemoveBehavior, {}});
            body.asked_behavior = true;
        }
        ops->push_back(Op{e, OpKind::ZeroInput, {}});
        return;
    }
    // Removing: the program queued the removals; the engine applied them at the end of that step
    if (!r.steering && !(vehicle && r.enemy_ai)) {
        body.steering_out = body.base_steering;
        if (body.asked_behavior) body.behavior_removed = true;  // a Vehicle's: never put back on a live body
        grant(b, want);
        return;
    }
    if (now_ - body.op_start >= transition_ns_) {
        body.phase = Phase::Idle;
        body.target = kNone;
        terminate(want, KILN_CONTROL_FAILED, KILN_CONTROL_RESULT_TIMED_OUT,
                  vehicle && r.enemy_ai && !r.steering ? "Transition: the vehicle's behaviour was not removed within 0.5 s"
                                                       : "Transition: the SteeringAgent was not removed within 0.5 s");
        return;
    }
    if (r.steering) ops->push_back(Op{e, OpKind::RemoveSteering, {}});
    if (vehicle && r.enemy_ai) ops->push_back(Op{e, OpKind::RemoveBehavior, {}});
    ops->push_back(Op{e, OpKind::ZeroInput, {}});
}

// The SteeringAgent the broker removed goes back: the input zeroed for 3 steps, the saved bytes (transient fields
// zeroed) queued at the end of the third, then checked (0.5 s). Done also when nothing was removed.
Broker::Back Broker::steering_back(uint32_t b, const BodyRead& r, std::vector<Op>* ops) {
    Body& body = bodies_[b];
    const KilnEntity e = body.key.entity;
    const bool out = body.have_base && body.base_steering && !r.steering;
    if (body.phase != Phase::Zeroing && body.phase != Phase::Restoring) {
        body.phase = Phase::Zeroing;
        body.zero_left = out ? kZeroSteps : 0;
    }
    if (body.phase == Phase::Zeroing) {
        if (body.zero_left > 0) {
            ops->push_back(Op{e, OpKind::ZeroInput, {}});
            if (--body.zero_left > 0) return Back::Waiting;
            body.phase = Phase::Restoring;
            body.op_start = now_;
            if (out && !fail_restore_) ops->push_back(Op{e, OpKind::AddSteering, restore_bytes(body)});
            return Back::Waiting;
        }
        body.phase = Phase::Restoring;
        body.op_start = now_;
    }
    if (out) {
        if (now_ - body.op_start >= transition_ns_) return Back::Failed;
        if (!fail_restore_) ops->push_back(Op{e, OpKind::AddSteering, restore_bytes(body)});  // kept if already there
        return Back::Waiting;
    }
    if (body.steering_out) {
        body.steering_out = false;
        publish_body(b, true);
    }
    return Back::Done;
}

void Broker::ai_on(uint32_t b, const BodyRead& r, uint32_t want, std::vector<Op>* ops) {
    Body& body = bodies_[b];
    const KilnEntity e = body.key.entity;
    if (body.target != want) {
        if (body.phase == Phase::Quiesce || body.phase == Phase::Removing) body.phase = Phase::Idle;
        body.target = want;
        body.wait_start = now_;
    }
    if (!r.enemy_ai || body.behavior_removed) {
        body.phase = Phase::Idle;
        body.target = kNone;
        terminate(want, KILN_CONTROL_FAILED, KILN_CONTROL_RESULT_AI_INHIBITED,
                  "AiInhibited: the body has no AI to run (no DynamicEnemy01BehaviorState)");
        return;
    }
    const Back back = steering_back(b, r, ops);
    if (back == Back::Waiting) return;
    if (back == Back::Failed) {
        body.target = kNone;
        restore_failed(b, "the SteeringAgent did not come back within 0.5 s");
        return;
    }
    if (body.ai_layer) {
        const KilnResult rc = game_->ai_layer(e, false);  // only the broker's own layer
        if (rc != KILN_OK) log(KILN_LOG_WARN, "body %u: releasing the AI-off layer answered %d", e, rc);
        body.ai_layer = false;
        publish_body(b, true);
    }
    const int on = game_->ai_enabled(e);
    body.phase = Phase::Idle;
    body.target = kNone;
    if (on != 1) {
        terminate(want, KILN_CONTROL_FAILED, KILN_CONTROL_RESULT_AI_INHIBITED,
                  on == 0 ? "AiInhibited: another owner holds the creature's AI off; the broker does not override it"
                          : "AiInhibited: the creature's AI switch cannot be read");
        return;
    }
    grant(b, want);
}

void Broker::restore(uint32_t b, const BodyRead& r, std::vector<Op>* ops) {
    Body& body = bodies_[b];
    const KilnEntity e = body.key.entity;
    if (body.phase == Phase::Quiesce || body.phase == Phase::Removing) body.phase = Phase::Idle;  // an entry nobody wants
    body.target = kNone;
    if (body.phase == Phase::Idle && !body.have_base && !body.ai_layer) return;  // nothing of the broker's on the body
    const Back back = steering_back(b, r, ops);
    if (back == Back::Waiting) return;
    if (back == Back::Failed) {
        restore_failed(b, "the SteeringAgent did not come back within 0.5 s");
        return;
    }
    if (body.ai_layer) {
        const KilnResult rc = game_->ai_layer(e, false);  // only the broker's own layer: another owner's stays
        if (rc != KILN_OK) log(KILN_LOG_WARN, "body %u: releasing the AI-off layer answered %d", e, rc);
        body.ai_layer = false;
    }
    // the baseline is used up: a later lease of this live generation takes a new snapshot
    body.have_base = false;
    body.base_steering = false;
    body.steering.clear();
    body.phase = Phase::Idle;
    log(KILN_LOG_INFO, "body %u#%llu restored (SteeringAgent back, queryId and velocities zeroed; AI layer released)", e,
        static_cast<unsigned long long>(body.key.body_generation));
    publish_body(b, true);
}

void Broker::step_body(uint32_t b, std::vector<Op>* ops) {
    Body& body = bodies_[b];
    if (!body.used || body.gone) return;
    if (const uint32_t cause = bodies_pub_[b].latch.load(std::memory_order_acquire)) {
        quarantined_step(b, cause);
        return;
    }
    if (body.leases.empty() && body.phase == Phase::Idle && !body.have_base && !body.ai_layer) {
        maybe_free_body(b);
        return;
    }
    const BodyRead r = game_->read(body.key.entity);
    if (!r.exists) {
        lose_body(b, KILN_CONTROL_RESULT_BODY_GONE, "BodyGone: the body no longer exists");
        return;
    }
    const bool out = body.have_base && body.base_steering && !r.steering;
    if (out != body.steering_out) {
        body.steering_out = out;
        publish_body(b, true);
    }
    const uint32_t want = pick_winner(body);
    if (body.winner != kNone && body.winner != want) {  // the winner lost the body to a higher lease: kept, Preempted
        Lease& old = leases_[body.winner];
        body.winner = kNone;
        if (!old.releasing && want != kNone) {
            old.state = KILN_CONTROL_PREEMPTED;
            old.result = KILN_CONTROL_RESULT_PREEMPTED;
            old.reason = std::string("Preempted: a ") + role_name(leases_[want].role) + " lease holds the body";
        }
        publish_body(b, true);
    }
    if (want != kNone)
        for (uint32_t slot : body.leases) {
            Lease& l = leases_[slot];
            if (slot == want || l.releasing || l.state != KILN_CONTROL_PENDING) continue;
            l.state = KILN_CONTROL_PREEMPTED;
            l.result = KILN_CONTROL_RESULT_PREEMPTED;
            l.reason = std::string("Preempted: a ") + role_name(leases_[want].role) + " lease holds the body";
            publish(slot, true);
        }
    if (want == kNone) {
        restore(b, r, ops);
    } else if (body.winner != want) {
        Lease& w = leases_[want];
        if (w.state == KILN_CONTROL_PREEMPTED) {  // resumed: through verification, with a new transition sequence
            w.state = KILN_CONTROL_PENDING;
            w.result = KILN_CONTROL_RESULT_NONE;
            w.reason = "Resuming: waiting for the body's transition";
            publish(want, true);
        }
        if (ai_role(w.role)) ai_on(b, r, want, ops);
        else enter(b, r, want, ops);
    }
    if (body.used && !body.gone) {
        settle_releasing(b);
        maybe_free_body(b);
    }
}

void Broker::pre(uint64_t now_ns, std::vector<Op>* ops) {
    now_ = now_ns;
    ops->clear();
    queued_ = 0;
    if (epoch()) check_counter();
    for (uint32_t b = 0; b < bodies_.size(); ++b) step_body(b, ops);
    if ((++pre_count_ & 31) == 0) expire_terminal();
}

}  // namespace kcc
