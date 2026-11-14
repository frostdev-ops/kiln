// SPDX-License-Identifier: MIT
// kiln.creature_control: the body registry. Body keys and their binding references (bind_body / release_body), owners
// and their request ids (next_request), the lease slots and what any thread reads of them (query_control, can_drive),
// notices. The transition of a body and the lease life are in lease.cpp.
#include <algorithm>
#include <cstdarg>
#include <cstdio>

#include "registry.h"

namespace kcc {

void set_reason(KilnControlState* s, const std::string& text) {
    size_t n = std::min(text.size(), sizeof s->reason - 1);
    while (n > 0 && n < text.size() && (static_cast<unsigned char>(text[n]) & 0xC0) == 0x80) --n;  // UTF-8 boundary
    std::memcpy(s->reason, text.data(), n);
    std::memset(s->reason + n, 0, sizeof s->reason - n);
}

Broker::Broker(Game* game) : game_(game), leases_(kLeaseSlots), pub_(new PublishedLease[kLeaseSlots]) {
    free_slots_.reserve(kLeaseSlots);
    for (uint32_t i = kLeaseSlots; i-- > 0;) free_slots_.push_back(i);
}

void Broker::log(int32_t level, const char* fmt, ...) {
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n > 0 && game_) game_->log(level, std::string(buf, std::min<size_t>(static_cast<size_t>(n), sizeof buf - 1)));
}

const char* Broker::state_name(uint32_t s) {
    switch (s) {
        case KILN_CONTROL_PENDING: return "Pending";
        case KILN_CONTROL_ACTIVE: return "Active";
        case KILN_CONTROL_PREEMPTED: return "Preempted";
        case KILN_CONTROL_RELEASED: return "Released";
        case KILN_CONTROL_FAILED: return "Failed";
        default: return "?";
    }
}

const char* Broker::role_name(uint32_t r) {
    switch (r) {
        case KILN_CONTROL_ROLE_PET_FOLLOW: return "PetFollow";
        case KILN_CONTROL_ROLE_PET_COMBAT: return "PetCombat";
        case KILN_CONTROL_ROLE_RIDDEN: return "Ridden";
        case KILN_CONTROL_ROLE_QUARANTINE: return "Quarantine";
        case KILN_CONTROL_ROLE_VEHICLE: return "Vehicle";
        case KILN_CONTROL_ROLE_PET_STAY: return "PetStay";
        case KILN_CONTROL_ROLE_ENGINE_GUARD: return "EngineGuard";
        default: return "None";
    }
}

// ---------------------------------------------------------------------------------------------------- world, state --

void Broker::set_operational(bool on, const std::string& why) {
    operational_ = on;
    off_why_ = on ? std::string() : why;
}

void Broker::set_world(uint64_t epoch, const char* why) {
    const uint64_t was = epoch_.load(std::memory_order_relaxed);
    if (was == epoch) return;
    // readers (can_drive, root_policy) compare the epoch first: from here on nothing of the old world grants or denies
    epoch_.store(epoch, std::memory_order_release);
    // Every lease of the old world ends, no body is restored: its fields must never be written into another world.
    uint32_t ended = 0;
    for (uint32_t b = 0; b < bodies_.size(); ++b) {
        if (!bodies_[b].used) continue;
        for (uint32_t slot : std::vector<uint32_t>(bodies_[b].leases)) {
            terminate(slot, KILN_CONTROL_FAILED, KILN_CONTROL_RESULT_WORLD_ENDED,
                      std::string("WorldEnded: ") + (why ? why : "the server world ended"));
            ++ended;
        }
        free_body(b);
    }
    by_entity_.clear();
    tomb_reserved_ = 0;
    deny_.clear();  // the world's teardown discarded every delayed effect of its roots
    identity_fault_ = false;
    counter_ = 0;
    if (was || ended) log(KILN_LOG_INFO, "world epoch %llu -> %llu: %u lease(s) ended (WorldEnded), every binding dropped",
                          static_cast<unsigned long long>(was), static_cast<unsigned long long>(epoch), ended);
}

// ---------------------------------------------------------------------------------------------------------- bodies --

uint32_t Broker::find_body(const KilnControlBodyKey& key) const {
    if (key.world_epoch == 0 || key.world_epoch != epoch() || key.reserved) return kNone;
    const auto it = by_entity_.find(key.entity);
    if (it == by_entity_.end()) return kNone;
    const Body& b = bodies_[it->second];
    return b.used && b.key.body_generation == key.body_generation ? it->second : kNone;
}

KilnResult Broker::bind(KilnOwner owner, KilnEntity entity, uint32_t baseline_kind, uint32_t retire_on_fatal,
                        KilnControlBodyKey* out) {
    if (!out || !owner || !entity || retire_on_fatal != 1 ||
        (baseline_kind != KILN_CONTROL_BASELINE_ENEMY && baseline_kind != KILN_CONTROL_BASELINE_EXPLICIT_NO_AI))
        return KILN_ERR_INVALID_ARGUMENT;
    if (!operational_) {
        log(KILN_LOG_DEBUG, "bind %u refused: creature_control is off (%s)", entity, off_why_.c_str());
        return KILN_ERR_UNAVAILABLE;
    }
    const uint64_t ep = epoch();
    if (!ep) return KILN_ERR_UNAVAILABLE;  // no server world runs here (a joined client, the menu)
    if (identity_fault_) {
        log(KILN_LOG_WARN, "bind %u refused: the entity counter went down in this world (identity fault)", entity);
        return KILN_ERR_UNAVAILABLE;
    }
    if (entity < kFirstBodyId || entity > kLastBodyId) return KILN_ERR_INVALID_ARGUMENT;  // players, reserved, bit 31
    if (const auto it = by_entity_.find(entity); it != by_entity_.end()) {
        Body& b = bodies_[it->second];
        if (!b.gone) {
            if (b.kind != baseline_kind) return KILN_ERR_INVALID_ARGUMENT;  // the first binding decided the baseline
            if (std::find(b.refs.begin(), b.refs.end(), owner) == b.refs.end()) b.refs.push_back(owner);
            *out = b.key;  // the same key; never a new snapshot of a bound body
            return KILN_OK;
        }
    }
    const BodyRead r = game_->read(entity);
    if (!r.exists) return KILN_ERR_NOT_FOUND;
    if (r.player) return KILN_ERR_INVALID_ARGUMENT;
    if (!r.actor || !r.input) {
        log(KILN_LOG_INFO, "bind %u refused: it has no %s (not a creature body)", entity, !r.actor ? "Actor" : "ActorInput");
        return KILN_ERR_UNAVAILABLE;
    }
    if (baseline_kind == KILN_CONTROL_BASELINE_ENEMY && !r.steering) {
        log(KILN_LOG_WARN, "bind %u refused: ExternalController: the creature has no SteeringAgent (another mod or loader "
                           "removed it?); Kiln does not claim a removal it did not make", entity);
        return KILN_ERR_UNAVAILABLE;
    }
    if (body_count_.load(std::memory_order_relaxed) >= KILN_CONTROL_MAX_BODIES) {
        log(KILN_LOG_WARN, "bind %u refused: Capacity: %u bodies are bound", entity, KILN_CONTROL_MAX_BODIES);
        return KILN_ERR_BUFFER_TOO_SMALL;
    }
    if (tomb_reserved_ >= KILN_CONTROL_MAX_TOMBSTONES) {
        log(KILN_LOG_WARN, "bind %u refused: Capacity: the retired-root table of this world is full (%u)", entity,
            KILN_CONTROL_MAX_TOMBSTONES);
        return KILN_ERR_BUFFER_TOO_SMALL;
    }
    uint32_t slot = kNone;
    for (uint32_t i = 0; i < bodies_.size(); ++i)
        if (!bodies_[i].used) {
            slot = i;
            break;
        }
    if (slot == kNone) return KILN_ERR_BUFFER_TOO_SMALL;
    // A gone body of the same id (the counter never gives an id twice in a world, but a reference may still hold it)
    // keeps its slot until its owners release it; the new binding takes the entity's name in the index.
    Body& b = bodies_[slot];
    b = Body{};
    b.used = true;
    b.key = KilnControlBodyKey{ep, ++generation_, entity, 0};
    b.kind = baseline_kind;
    b.refs.push_back(owner);
    b.tomb = true;  // pre-reserved: a later quarantine or retirement cannot fail to deny its root
    ++tomb_reserved_;
    by_entity_[entity] = slot;
    bodies_pub_[slot].latch.store(0, std::memory_order_relaxed);
    bodies_pub_[slot].causes.store(0, std::memory_order_relaxed);
    bodies_pub_[slot].entity.store(entity, std::memory_order_relaxed);
    bodies_pub_[slot].generation.store(b.key.body_generation, std::memory_order_release);
    body_count_.fetch_add(1, std::memory_order_relaxed);
    *out = b.key;
    log(KILN_LOG_INFO, "body %u#%llu bound (%s) by owner %llu", entity, static_cast<unsigned long long>(b.key.body_generation),
        baseline_kind == KILN_CONTROL_BASELINE_ENEMY ? "Enemy" : "ExplicitNoAi", static_cast<unsigned long long>(owner));
    return KILN_OK;
}

void Broker::free_body(uint32_t b) {
    Body& body = bodies_[b];
    if (!body.used) return;
    if (const auto it = by_entity_.find(body.key.entity); it != by_entity_.end() && it->second == b) by_entity_.erase(it);
    // an unbound live body's root is never denied: its reservation goes; a quarantined or retired one keeps its entry
    const bool denied = body.quarantined || body.retired || bodies_pub_[b].latch.load(std::memory_order_acquire) != 0;
    if (body.tomb && tomb_reserved_ && !denied) --tomb_reserved_;
    bodies_pub_[b].generation.store(0, std::memory_order_release);
    bodies_pub_[b].latch.store(0, std::memory_order_relaxed);
    bodies_pub_[b].causes.store(0, std::memory_order_relaxed);
    bodies_pub_[b].entity.store(0, std::memory_order_relaxed);
    body = Body{};
    body_count_.fetch_sub(1, std::memory_order_relaxed);
}

// An unused binding goes when no reference, lease or change of the broker's is left on the body.
void Broker::maybe_free_body(uint32_t b) {
    Body& body = bodies_[b];
    if (!body.used || !body.refs.empty() || !body.leases.empty()) return;
    if (!body.gone && (body.phase != Phase::Idle || body.have_base || body.ai_layer)) return;
    log(KILN_LOG_INFO, "body %u#%llu unbound", body.key.entity, static_cast<unsigned long long>(body.key.body_generation));
    free_body(b);
}

KilnResult Broker::release_body(KilnOwner owner, const KilnControlBodyKey& key) {
    if (!owner || key.reserved) return KILN_ERR_INVALID_ARGUMENT;
    // a gone body keeps its key until its owners let go
    uint32_t b = kNone;
    if (key.world_epoch == epoch())
        for (uint32_t i = 0; i < bodies_.size(); ++i)
            if (bodies_[i].used && bodies_[i].key.entity == key.entity && bodies_[i].key.body_generation == key.body_generation) {
                b = i;
                break;
            }
    if (b == kNone) return KILN_ERR_NOT_FOUND;
    Body& body = bodies_[b];
    const auto it = std::find(body.refs.begin(), body.refs.end(), owner);
    if (it == body.refs.end()) return KILN_ERR_NOT_FOUND;
    // a quarantined live body is retired, never unbound early
    if (!body.gone && (body.quarantined || bodies_pub_[b].latch.load(std::memory_order_acquire))) return KILN_ERR_INVALID_ARGUMENT;
    for (uint32_t slot : body.leases)
        if (leases_[slot].owner == owner) return KILN_ERR_INVALID_ARGUMENT;  // a nonterminal lease of the owner
    body.refs.erase(it);
    maybe_free_body(b);
    return KILN_OK;
}

// ---------------------------------------------------------------------------------------------------------- owners --

KilnResult Broker::next_request(KilnOwner owner, uint64_t* out) {
    if (!owner || !out) return KILN_ERR_INVALID_ARGUMENT;
    OwnerRec& o = owners_[owner];
    const uint64_t last = std::max(o.next, o.high);
    if (last == UINT64_MAX) return KILN_ERR_BUFFER_TOO_SMALL;  // exhausted: never wraps
    o.next = last + 1;
    *out = o.next;
    return KILN_OK;
}

void Broker::notify(OwnerRec& o, const KilnControlState& s) {
    if (o.queue.size() >= notice_cap_) {
        o.resync = true;  // dropped: the gap in notice_sequence tells the consumer to query every lease
        return;
    }
    o.queue.push_back(s);
}

KilnResult Broker::notices(KilnOwner owner, KilnControlState* out, uint32_t capacity, uint32_t* written,
                           uint32_t* needs_resync) {
    if (!owner || !written || (capacity && !out)) return KILN_ERR_INVALID_ARGUMENT;
    *written = 0;
    if (needs_resync) *needs_resync = 0;
    const auto it = owners_.find(owner);
    if (it == owners_.end()) return KILN_OK;
    OwnerRec& o = it->second;
    uint32_t n = 0;
    while (n < capacity && !o.queue.empty()) {
        out[n++] = o.queue.front();
        o.queue.pop_front();
    }
    *written = n;
    if (needs_resync) *needs_resync = o.resync ? 1 : 0;
    o.resync = false;
    return KILN_OK;
}

// ---------------------------------------------------------------------------------------------------------- leases --

uint32_t Broker::alloc_lease() {
    if (free_slots_.empty()) {
        // every slot holds a lease: let the oldest terminal one go early (it then answers ExpiredHandle)
        uint32_t oldest = kNone;
        for (uint32_t i = 0; i < kLeaseSlots; ++i) {
            const Lease& l = leases_[i];
            if (l.used && (l.state == KILN_CONTROL_RELEASED || l.state == KILN_CONTROL_FAILED) &&
                (oldest == kNone || l.terminal_ns < leases_[oldest].terminal_ns))
                oldest = i;
        }
        if (oldest == kNone) return kNone;
        free_lease(oldest);
    }
    const uint32_t slot = free_slots_.back();
    free_slots_.pop_back();
    return slot;
}

void Broker::free_lease(uint32_t slot) {
    Lease& l = leases_[slot];
    if (!l.used) return;
    if (const auto it = owners_.find(l.owner); it != owners_.end()) {
        auto r = it->second.requests.find(l.request_id);
        if (r != it->second.requests.end() && r->second == l.handle) it->second.requests.erase(r);
    }
    {
        std::lock_guard lock(pub_[slot].mu);
        pub_[slot].handle = 0;
        pub_[slot].owner = 0;
        pub_[slot].body = kNone;
        pub_[slot].generation = 0;
        pub_[slot].state = KilnControlState{};
    }
    l = Lease{};
    free_slots_.push_back(slot);
}

Broker::Lease* Broker::lease_by_handle(KilnHandle handle) {
    if (!handle) return nullptr;
    Lease& l = leases_[handle & (kLeaseSlots - 1)];
    return l.used && l.handle == handle ? &l : nullptr;
}

uint32_t Broker::changed_mask(const Body& b) const {
    uint32_t m = 0;
    if (b.steering_out) m |= KILN_CONTROL_CHANGED_STEERING;
    if (b.ai_layer) m |= KILN_CONTROL_CHANGED_AI_LAYER;
    if (b.behavior_removed) m |= KILN_CONTROL_CHANGED_VEHICLE_BEHAVIOR;
    return m;
}

void Broker::publish(uint32_t slot, bool notify_owner) {
    Lease& l = leases_[slot];
    if (!l.used) return;
    KilnControlState s{};
    s.size = sizeof s;
    s.lease_state = l.state;
    s.result = l.result;
    s.requested_role = l.role;
    s.body = l.key;
    s.lease = l.handle;
    s.request_id = l.request_id;
    s.transition_sequence = l.seq;
    const bool terminal = l.state == KILN_CONTROL_RELEASED || l.state == KILN_CONTROL_FAILED;
    if (l.body != kNone && bodies_[l.body].used && !terminal) {
        const Body& b = bodies_[l.body];
        s.changed_mask = changed_mask(b);
        if (bodies_pub_[l.body].latch.load(std::memory_order_acquire)) {
            s.lease_state = KILN_CONTROL_PREEMPTED;  // the broker's internal policy wins: lease 0, role Quarantine
            s.result = KILN_CONTROL_RESULT_PREEMPTED;
            s.effective_role = KILN_CONTROL_ROLE_QUARANTINE;
        } else if (b.winner != kNone) {
            s.winning_lease = leases_[b.winner].handle;
            s.effective_role = leases_[b.winner].role;
        }
    }
    set_reason(&s, l.reason);
    // a notice only when something a consumer acts on changed
    const KilnControlState& p = l.last;
    const bool changed = !l.published || p.lease_state != s.lease_state || p.result != s.result ||
                         p.effective_role != s.effective_role || p.changed_mask != s.changed_mask ||
                         p.winning_lease != s.winning_lease || p.transition_sequence != s.transition_sequence ||
                         std::strcmp(p.reason, s.reason) != 0;
    OwnerRec& o = owners_[l.owner];
    if (changed && notify_owner) s.notice_sequence = ++o.notice_seq;
    else s.notice_sequence = l.published ? p.notice_sequence : 0;
    {
        std::lock_guard lock(pub_[slot].mu);
        pub_[slot].handle = l.handle;
        pub_[slot].owner = l.owner;
        pub_[slot].body = l.body;
        pub_[slot].generation = l.key.body_generation;
        pub_[slot].state = s;
    }
    if (changed && (!l.published || p.lease_state != s.lease_state || p.result != s.result ||
                    p.winning_lease != s.winning_lease || p.transition_sequence != s.transition_sequence))
        log(KILN_LOG_INFO, "lease %llu owner %llu body %u#%llu %s: %s -> %s (result %u) seq %llu win %llu: %s",
            static_cast<unsigned long long>(l.handle), static_cast<unsigned long long>(l.owner), l.key.entity,
            static_cast<unsigned long long>(l.key.body_generation), role_name(l.role),
            l.published ? state_name(p.lease_state) : "-", state_name(s.lease_state), s.result,
            static_cast<unsigned long long>(s.transition_sequence), static_cast<unsigned long long>(s.winning_lease),
            s.reason);
    l.last = s;
    l.published = true;
    if (changed && notify_owner) notify(o, s);
}

void Broker::publish_body(uint32_t b, bool notify_owner) {
    for (uint32_t slot : bodies_[b].leases) publish(slot, notify_owner);
}

KilnResult Broker::query(KilnHandle handle, KilnControlState* out) const {
    if (!handle || !out || out->size < sizeof(KilnControlState)) return KILN_ERR_INVALID_ARGUMENT;
    const PublishedLease& p = pub_[handle & (kLeaseSlots - 1)];
    uint32_t body = kNone;
    uint64_t generation = 0;
    bool found = false;
    {
        std::lock_guard lock(p.mu);
        if (p.handle == handle) {
            *out = p.state;
            body = p.body;
            generation = p.generation;
            found = true;
        }
    }
    if (found) {
        // a quarantine latched since the last publish (any thread) shows at once: Preempted, effective Quarantine, lease 0
        const bool live = out->lease_state != KILN_CONTROL_RELEASED && out->lease_state != KILN_CONTROL_FAILED;
        if (live && body < bodies_pub_.size() && bodies_pub_[body].generation.load(std::memory_order_acquire) == generation) {
            if (const uint32_t cause = bodies_pub_[body].latch.load(std::memory_order_acquire)) {
                out->lease_state = KILN_CONTROL_PREEMPTED;
                out->result = KILN_CONTROL_RESULT_PREEMPTED;
                out->effective_role = KILN_CONTROL_ROLE_QUARANTINE;
                out->winning_lease = 0;
                if (std::strncmp(out->reason, "Quarantined", 11) != 0)
                    set_reason(out, "Quarantined: cause " + std::to_string(cause) + "; the body will be retired");
            }
        }
        return KILN_OK;
    }
    KilnControlState s{};
    s.size = sizeof s;
    s.lease_state = KILN_CONTROL_FAILED;
    s.result = KILN_CONTROL_RESULT_EXPIRED_HANDLE;
    s.lease = handle;
    set_reason(&s, "ExpiredHandle: no such lease (unknown, or terminal for more than 10 minutes)");
    *out = s;
    return KILN_OK;
}

uint32_t Broker::can_drive(KilnOwner owner, KilnHandle handle, const KilnControlBodyKey& key, uint64_t seq) const {
    if (!handle || !owner || !seq) return 0;
    const PublishedLease& p = pub_[handle & (kLeaseSlots - 1)];
    uint32_t body = kNone;
    uint64_t generation = 0;
    {
        std::lock_guard lock(p.mu);
        const KilnControlState& s = p.state;
        if (p.handle != handle || p.owner != owner || s.lease_state != KILN_CONTROL_ACTIVE || s.winning_lease != handle ||
            s.transition_sequence != seq || s.body.world_epoch != key.world_epoch ||
            s.body.body_generation != key.body_generation || s.body.entity != key.entity || key.reserved)
            return 0;
        body = p.body;
        generation = p.generation;
    }
    if (body >= bodies_pub_.size() || key.world_epoch != epoch()) return 0;
    const BodyPublished& bp = bodies_pub_[body];
    // the fatal latch (quarantine) beats every cached Active state
    if (bp.latch.load(std::memory_order_acquire) != 0) return 0;
    return bp.generation.load(std::memory_order_acquire) == generation ? 1u : 0u;
}

void Broker::expire_terminal() {
    for (uint32_t i = 0; i < kLeaseSlots; ++i) {
        const Lease& l = leases_[i];
        if (l.used && (l.state == KILN_CONTROL_RELEASED || l.state == KILN_CONTROL_FAILED) && now_ >= l.terminal_ns &&
            now_ - l.terminal_ns >= kRetainNs)
            free_lease(i);
    }
}

}  // namespace kcc
