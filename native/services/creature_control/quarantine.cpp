// SPDX-License-Identifier: MIT
// kiln.creature_control: quarantine, retirement and the retired-root deny table (19d §4.3 "Quarantine cause codes",
// §4.4 last paragraph, §4.6, §5.2 threads).
//   quarantine    any thread, no game call: latches the body's cause (every lease loses authority at once, can_drive
//                 checks the latch) and denies its root from now on (root_policy). Causes 1-4 (a semantic down) win
//                 over the voluntary 5-9; every cause stays reportable in the cause mask.
//   the Pre       sees the latch: no transition and no restore any more; the leases show Preempted with effective role
//                 Quarantine until the body is gone; the broker's own fatal failure retires it through kiln.spawn.
//   body_retired  the owner destroyed the body: checked absent at this job barrier, its root stays denied for the world
//                 epoch, its leases end Retired, its rows go.
//   root_policy   any thread, POD only: 1 for a root of this epoch that is quarantined or retired.
// A world epoch's table is cleared only when that world ends (set_world), after its teardown discarded every delayed
// effect. A drop of the engine's entity counter within an epoch is an identity fault: every bound body is quarantined
// (never destroyed: its id may name another entity now) and no new body binds until the epoch ends.
#include <algorithm>

#include "registry.h"

namespace kcc {

namespace {
uint32_t slot_of(KilnEntity e) { return (e * 0x9E3779B1u) & (DenyTable::kSlots - 1); }
}  // namespace

// ------------------------------------------------------------------------------------------------------ deny table --

bool DenyTable::insert(KilnEntity e) {
    if (!e) return false;
    uint32_t i = slot_of(e);
    for (uint32_t n = 0; n < kSlots; ++n, i = (i + 1) & (kSlots - 1)) {
        uint32_t cur = slots_[i].load(std::memory_order_acquire);
        if (cur == e) return true;
        if (cur == 0) {
            if (slots_[i].compare_exchange_strong(cur, e, std::memory_order_acq_rel)) {
                count_.fetch_add(1, std::memory_order_relaxed);
                return true;
            }
            if (cur == e) return true;  // another thread put the same id here
        }
    }
    return false;  // full: cannot happen while every entry is reserved at bind
}

bool DenyTable::contains(KilnEntity e) const {
    if (!e) return false;
    uint32_t i = slot_of(e);
    for (uint32_t n = 0; n < kSlots; ++n, i = (i + 1) & (kSlots - 1)) {
        const uint32_t cur = slots_[i].load(std::memory_order_acquire);
        if (cur == e) return true;
        if (cur == 0) return false;
    }
    return false;
}

void DenyTable::clear() {
    for (uint32_t i = 0; i < kSlots; ++i) slots_[i].store(0, std::memory_order_relaxed);
    count_.store(0, std::memory_order_release);
}

// ------------------------------------------------------------------------------------------------------ quarantine --

void Broker::latch(uint32_t b, uint32_t cause) {
    std::lock_guard lock(bodies_pub_[b].mu);
    latch_locked(b, cause);
}

void Broker::latch_locked(uint32_t b, uint32_t cause) {
    BodyPublished& bp = bodies_pub_[b];
    bp.causes.fetch_or(1u << cause, std::memory_order_acq_rel);
    uint32_t cur = bp.latch.load(std::memory_order_acquire);
    // the first cause holds; a semantic down (1-4) overrides a voluntary teardown cause (5-9)
    while ((cur == 0 || (cur >= KILN_CONTROL_CAUSE_DISMISS && cause < KILN_CONTROL_CAUSE_DISMISS)) &&
           !bp.latch.compare_exchange_weak(cur, cause, std::memory_order_acq_rel)) {
    }
    deny_.insert(bp.entity.load(std::memory_order_acquire));
}

KilnResult Broker::quarantine(const KilnControlBodyKey& key, uint32_t cause) {
    if (cause < KILN_CONTROL_CAUSE_NATIVE_LETHAL || cause > KILN_CONTROL_CAUSE_WORLD_END || key.reserved || !key.entity ||
        !key.body_generation)
        return KILN_ERR_INVALID_ARGUMENT;
    if (key.world_epoch == 0 || key.world_epoch != epoch()) return KILN_ERR_NOT_FOUND;
    for (uint32_t b = 0; b < bodies_pub_.size(); ++b) {
        const BodyPublished& bp = bodies_pub_[b];
        std::lock_guard lock(bp.mu);
        if (key.world_epoch != epoch()) return KILN_ERR_NOT_FOUND;
        if (bp.generation.load(std::memory_order_acquire) != key.body_generation) continue;
        if (bp.entity.load(std::memory_order_acquire) != key.entity) return KILN_ERR_NOT_FOUND;
        latch_locked(b, cause);
        return KILN_OK;
    }
    return KILN_ERR_NOT_FOUND;
}

uint32_t Broker::root_policy(uint64_t world_epoch, KilnEntity root) const {
    if (!world_epoch || !root || world_epoch != epoch()) return 0;
    return deny_.contains(root) ? 1u : 0u;
}

void Broker::retire_body(uint32_t b, const std::string& why) {
    Body& body = bodies_[b];
    if (!body.used) return;
    deny_.insert(body.key.entity);  // its root stays denied for the world epoch: delayed hits of it are dropped
    body.retired = true;
    body.gone = true;
    for (uint32_t slot : std::vector<uint32_t>(body.leases)) terminate(slot, KILN_CONTROL_FAILED, KILN_CONTROL_RESULT_RETIRED, why);
    log(KILN_LOG_INFO, "body %u#%llu retired: %s", body.key.entity, static_cast<unsigned long long>(body.key.body_generation),
        why.c_str());
    free_body(b);  // its rows and snapshot go; terminal leases stay queryable
}

KilnResult Broker::body_retired(KilnOwner owner, const KilnControlBodyKey& key) {
    if (!owner || key.reserved) return KILN_ERR_INVALID_ARGUMENT;
    uint32_t b = kNone;
    if (key.world_epoch && key.world_epoch == epoch())
        for (uint32_t i = 0; i < bodies_.size(); ++i)
            if (bodies_[i].used && bodies_[i].key.entity == key.entity && bodies_[i].key.body_generation == key.body_generation) {
                b = i;
                break;
            }
    if (b == kNone) return KILN_ERR_NOT_FOUND;
    if (std::find(bodies_[b].refs.begin(), bodies_[b].refs.end(), owner) == bodies_[b].refs.end()) return KILN_ERR_NOT_FOUND;
    if (game_->read(key.entity).exists) return KILN_ERR_INVALID_ARGUMENT;  // a live body is never retired by word
    retire_body(b, "Retired: the owner destroyed the body");
    return KILN_OK;
}

void Broker::quarantined_step(uint32_t b, uint32_t cause) {
    Body& body = bodies_[b];
    const KilnEntity e = body.key.entity;
    if (!body.quarantined) {
        body.quarantined = true;
        body.winner = kNone;
        body.target = kNone;
        body.phase = Phase::Idle;
        for (uint32_t slot : body.leases) {
            Lease& l = leases_[slot];
            l.reason = "Quarantined: cause " + std::to_string(cause) + "; the body will be retired";
        }
        publish_body(b, true);
        log(KILN_LOG_WARN, "body %u#%llu quarantined (cause %u, causes 0x%x): no lease drives it, it is retired", e,
            static_cast<unsigned long long>(body.key.body_generation), cause,
            bodies_pub_[b].causes.load(std::memory_order_acquire));
    }
    if (!game_->read(e).exists) {
        retire_body(b, "Retired: the quarantined body is gone");
        return;
    }
    if (body.fatal && !identity_fault_) {
        // the broker's own irrecoverable failure, under the binder's retire_on_fatal consent: retired now, at this safe Pre
        // An epoch identity fault is different: this id may now name someone else's body; retain quarantine until teardown.
        if (game_->destroy(e) && !game_->read(e).exists) retire_body(b, "Retired: the broker could not restore the body");
        else log(KILN_LOG_ERROR, "body %u: retiring it through kiln.spawn failed; retried at the next Pre", e);
    }
}

// An irrecoverable restore: the body cannot be given back as it was. It is quarantined and retired (never left without
// its SteeringAgent and with its AI held off for nobody).
void Broker::restore_failed(uint32_t b, const char* why) {
    Body& body = bodies_[b];
    log(KILN_LOG_ERROR, "body %u#%llu: RestoreFailed: %s; the body is quarantined and retired", body.key.entity,
        static_cast<unsigned long long>(body.key.body_generation), why);
    for (uint32_t slot : std::vector<uint32_t>(body.leases))
        if (leases_[slot].releasing)
            terminate(slot, KILN_CONTROL_FAILED, KILN_CONTROL_RESULT_RESTORE_FAILED, std::string("RestoreFailed: ") + why);
    body.fatal = true;
    latch(b, KILN_CONTROL_CAUSE_CONTROL_FAILURE);
    quarantined_step(b, bodies_pub_[b].latch.load(std::memory_order_acquire));
}

void Broker::withdraw(const std::string& why) {
    set_operational(false, why);
    uint32_t n = 0;
    for (uint32_t b = 0; b < bodies_.size(); ++b) {
        if (!bodies_[b].used || bodies_[b].gone) continue;
        bodies_[b].fatal = true;
        latch(b, KILN_CONTROL_CAUSE_CONTROL_FAILURE);
        ++n;
    }
    if (n) log(KILN_LOG_WARN, "withdrawn (%s): %u bound bod%s quarantined, retired at the next Pre", why.c_str(), n, n == 1 ? "y" : "ies");
}

void Broker::check_counter() {
    uint32_t now = 0;
    if (!game_->entity_counter(&now)) return;
    if (counter_ != 0 && now < counter_ && !identity_fault_) {
        identity_fault_ = true;
        uint32_t n = 0;
        for (uint32_t b = 0; b < bodies_.size(); ++b)
            if (bodies_[b].used && !bodies_[b].gone) {
                latch(b, KILN_CONTROL_CAUSE_CONTROL_FAILURE);
                ++n;
            }
        log(KILN_LOG_ERROR, "the entity counter went down (%u -> %u): world identity fault; %u bod%s quarantined, no new binding "
                            "until this world ends", counter_, now, n, n == 1 ? "y" : "ies");
    }
    counter_ = now;
}

}  // namespace kcc
