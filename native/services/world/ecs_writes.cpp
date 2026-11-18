// SPDX-License-Identifier: MIT
// kiln.world's compare-exchange writes: everything before the gate and after its release; see ecs_writes.h. The hold
// itself is ecs_write_hold.cpp.
#include "ecs_writes.h"

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstring>

#include "kiln_engine_view.h"

namespace kw::ecs::writes {

namespace {
using kiln::engine::EcsGateScope;
using kiln::engine::EcsView;
using kiln::engine::GateAdmission;
using kiln::engine::Reason;
using kiln::engine::Role;

uint64_t now_ns() {
    return static_cast<uint64_t>(
        std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
}
uint64_t mix3(uint64_t a, uint64_t b, uint64_t c) {
    uint64_t h = 0xcbf29ce484222325ull;
    for (uint64_t v : {a, b, c}) {
        h ^= v + 0x9e3779b97f4a7c15ull + (h << 6) + (h >> 2);
        h *= 0x100000001b3ull;
    }
    return h;
}
KilnResult result_of(uint32_t status) {
    switch (status) {
        case KILN_ECS_OK: return KILN_OK;
        case KILN_ECS_ABSENT: return KILN_ERR_NOT_FOUND;
        case KILN_ECS_STALE: return KILN_ECS_ERR_STALE;
        case KILN_ECS_UNREADABLE: return KILN_ECS_ERR_UNREADABLE;
        case KILN_ECS_BUSY: return KILN_ECS_ERR_BUSY;
        case KILN_ECS_CONFLICT: return KILN_ECS_ERR_CONFLICT;
        case KILN_ECS_NOT_WRITABLE: return KILN_ECS_ERR_NOT_WRITABLE;
        case KILN_ECS_UNCERTAIN: return KILN_ECS_ERR_UNCERTAIN;
        default: return KILN_ERR_UNAVAILABLE;
    }
}
// The gate's refusals: busy (a later attempt can win) or unavailable (it cannot from here, or not now at all).
uint32_t gate_status(Reason r, uint32_t* why) {
    switch (r) {
        case Reason::InFrame:
            *why = KILN_ECS_WRITE_WHY_IN_FRAME;
            return KILN_ECS_BUSY;
        case Reason::KilnHolderBusy:
        case Reason::KilnBusyLatched:
        case Reason::EcsSceneFrameBusy:
        case Reason::EcsThreadRegistryBusy:
        case Reason::EcsEngineOwnerChangedDuringRead:
        case Reason::EcsServerThreadChangedDuringRead:
        case Reason::EcsActiveSceneListChanged:
        case Reason::EcsReadySceneContextChanged:
        case Reason::EcsWriteOwnerChangedOrNotAuthoritative:
            *why = KILN_ECS_WRITE_WHY_GATE;
            return KILN_ECS_BUSY;
        default:
            *why = KILN_ECS_WRITE_WHY_GATE;
            return KILN_ECS_UNAVAILABLE;
    }
}
bool has_gate(const KilnEngineNativeApi* e) {
    return e && e->header.size >= offsetof(KilnEngineNativeApi, ecs_gate_release) + sizeof(void*) && e->layout &&
           e->ecs_view && e->copy && e->ecs_gate_acquire && e->ecs_gate_validate && e->ecs_gate_component_supported &&
           e->ecs_gate_write && e->ecs_gate_release;
}
}  // namespace

// ---- UncertainLatch ----
bool UncertainLatch::latched(const Target& t) const noexcept {
    if (t.session && widened_.load(std::memory_order_acquire) == t.session) return true;
    const uint64_t id = uint64_t{t.entity} << 32 | t.generation, comp = uint64_t{t.world_kind} << 32 | t.component;
    const uint64_t h = mix3(t.session, id, comp);
    for (size_t i = 0; i < kSlots; ++i) {
        const Slot& s = slots_[(h + i) % kSlots];
        const uint32_t state = s.state.load(std::memory_order_acquire);
        if (state == 0) return false;
        if (state == 2 && s.session.load(std::memory_order_relaxed) == t.session &&
            s.id.load(std::memory_order_relaxed) == id && s.component.load(std::memory_order_relaxed) == comp)
            return true;
    }
    return false;
}
bool UncertainLatch::latch(const Target& t) noexcept {
    if (latched(t)) return true;
    const uint64_t id = uint64_t{t.entity} << 32 | t.generation, comp = uint64_t{t.world_kind} << 32 | t.component;
    const uint64_t h = mix3(t.session, id, comp);
    for (size_t i = 0; i < kSlots; ++i) {
        Slot& s = slots_[(h + i) % kSlots];
        uint32_t state = s.state.load(std::memory_order_acquire);
        // A free slot, or one of an earlier session (it never matches again).
        const bool reuse = state == 2 && s.session.load(std::memory_order_relaxed) != t.session;
        if (state != 0 && !reuse) continue;
        if (!s.state.compare_exchange_strong(state, 1, std::memory_order_acq_rel)) continue;
        s.session.store(t.session, std::memory_order_relaxed);
        s.id.store(id, std::memory_order_relaxed);
        s.component.store(comp, std::memory_order_relaxed);
        s.state.store(2, std::memory_order_release);
        return true;
    }
    widened_.store(t.session, std::memory_order_release);  // full: the whole session fails closed
    return false;
}
size_t UncertainLatch::used(uint64_t session) const noexcept {
    size_t n = 0;
    for (const Slot& s : slots_)
        if (s.state.load(std::memory_order_acquire) == 2 && s.session.load(std::memory_order_relaxed) == session) ++n;
    return n;
}

// ---- EventRing ----
EventRing::EventRing() : cells_(new Cell[kCells]) {
    for (uint64_t i = 0; i < kCells; ++i) cells_[i].sequence.store(i, std::memory_order_relaxed);
}
bool EventRing::reserve() noexcept {
    uint64_t used = used_.load(std::memory_order_acquire);
    while (used < kCells)
        if (used_.compare_exchange_weak(used, used + 1, std::memory_order_acq_rel)) return true;
    return false;
}
void EventRing::unreserve() noexcept { used_.fetch_sub(1, std::memory_order_acq_rel); }
uint64_t EventRing::claim() noexcept { return head_.fetch_add(1, std::memory_order_acq_rel); }
void EventRing::publish(uint64_t position, const KilnEcsWriteEvent& event) noexcept {
    // The cell is free: at most kCells positions are reserved, claimed or not yet taken (reserve's bound), and taking
    // is in order, so position - kCells was taken before this position could be claimed.
    Cell& c = cells_[position % kCells];
    c.event = event;
    c.sequence.store(position + 1, std::memory_order_release);
}
size_t EventRing::take(KilnEcsWriteEvent* out, size_t max) noexcept {
    size_t n = 0;
    while (n < max) {
        Cell& c = cells_[tail_ % kCells];
        if (c.sequence.load(std::memory_order_acquire) != tail_ + 1) break;  // not published yet
        out[n++] = c.event;
        c.sequence.store(tail_ + kCells, std::memory_order_release);
        ++tail_;
        used_.fetch_sub(1, std::memory_order_acq_rel);
    }
    return n;
}

// ---- Writer ----
bool Writer::available() const { return unavailable_reason() == nullptr; }

const char* Writer::unavailable_reason() const {
    if (!src_.exact_profile) return "no exact engine profile for this build";
    if (!has_gate(src_.engine)) return "kiln.internal.engine has no write gate here";
    const kiln::engine::Layout* layout = src_.engine->layout();
    if (!layout || !layout->write.enabled) return "this build's engine profile has no write gate";
    if (!layout->write.tracking_registry_a || !layout->write.tracking_registry_b)
        return "this build's engine profile does not locate the change-tracking bits";
    engine::Reader* reader = src_.reader ? src_.reader(src_.user) : nullptr;
    if (!reader || !reader->source().reads) return "entity reads are off on this build (see world.ecs_read)";
    return nullptr;
}

void Writer::log(int level, const char* text) const {
    if (src_.log) src_.log(src_.user, level, text);
}

bool Writer::retired(KilnOwner owner) const {
    std::lock_guard lock(mu_);
    return retired_.count(owner) != 0;
}

KilnResult Writer::policy_for(engine::Reader& reader, const ContextFact& ctx, bool world, uint32_t component,
                              KilnEcsWritePolicy* out, uint16_t* index) {
    if (world) {
        std::lock_guard lock(mu_);
        if (cache_session_ != ctx.session) {
            cache_.clear();
            cache_session_ = ctx.session;
        }
        const auto it = cache_.find(component);
        if (it != cache_.end()) {
            const uint32_t size = out->size;
            *out = it->second.policy;
            out->size = size;
            *index = it->second.index;
            return out->status == KILN_ECS_UNAVAILABLE ? KILN_ERR_UNAVAILABLE : KILN_OK;
        }
    }
    PolicyFacts f;
    f.world_kind = KILN_WORLD_SERVER;
    f.exact_profile = src_.exact_profile;
    const kiln::engine::Layout* layout = has_gate(src_.engine) ? src_.engine->layout() : nullptr;
    f.gate_enabled = available() && layout;
    f.budget = layout ? static_cast<uint32_t>(std::min<size_t>(layout->write.maximum_bytes, UINT32_MAX)) : 0;
    f.world = world;
    if (f.gate_enabled && f.world) {
        bool present = false;
        uint16_t idx = 0, size = 0;
        if (reader.registration(ctx, component, &present, &idx, &size) != KILN_ECS_OK) {
            const uint32_t keep = out->size;
            *out = KilnEcsWritePolicy{};
            out->size = keep;
            out->status = KILN_ECS_UNREADABLE;
            out->world_kind = KILN_WORLD_SERVER;
            out->component = component;
            std::snprintf(out->why, sizeof out->why, "%s", "the world's component registry could not be read now");
            return KILN_ECS_ERR_UNREADABLE;
        }
        f.registered = present;
        f.registered_size = size;
        *index = idx;
        if (present) {
            bool a = false, b = false;
            f.tracking_known = reader.tracking(ctx.registry, idx, layout->write.tracking_registry_a,
                                               layout->write.tracking_registry_b, &a, &b);
            f.tracked_a = a;
            f.tracked_b = b;
            f.plain = classify_plain(src_.types, component, &f.reflected_size, &f.plain_why);
        }
    }
    const KilnResult rc = decide(f, component, out);
    // Cached for the session: the registration and the type never change in a process, the tracking bits are read
    // again inside every hold. An unreadable tracking word is not an answer to keep.
    if (world && f.gate_enabled && out->why_code != KILN_ECS_WRITE_WHY_TRACKING_UNKNOWN) {
        std::lock_guard lock(mu_);
        if (cache_session_ == ctx.session) cache_[component] = CachedPolicy{*out, *index};
    }
    return rc;
}

KilnResult Writer::policy(uint32_t world_kind, uint32_t component, KilnEcsWritePolicy* out) {
    if (!out || out->size < sizeof(KilnEcsWritePolicy) || world_kind >= KILN_WORLD_KINDS) return KILN_ERR_INVALID_ARGUMENT;
    if (world_kind != KILN_WORLD_SERVER) {
        PolicyFacts f;
        f.world_kind = world_kind;
        return decide(f, component, out);
    }
    engine::Reader* reader = src_.reader ? src_.reader(src_.user) : nullptr;
    ContextFact ctx{};
    bool world = false;
    if (reader && available()) {
        const uint32_t s = reader->context(0, KILN_WORLD_SERVER, &ctx);
        world = s == KILN_ECS_OK && ctx.present && ctx.engine_status == KILN_ECS_OK;
    }
    uint16_t index = 0;
    if (!reader) {
        PolicyFacts f;
        f.exact_profile = src_.exact_profile;
        return decide(f, component, out);
    }
    return policy_for(*reader, ctx, world, component, out, &index);
}

KilnResult Writer::compare_exchange(KilnOwner owner, const KilnObjectRef* ref, uint32_t component, const void* expected,
                                    const void* mask, const void* desired, uint32_t bytes, void* confirmed,
                                    KilnMutationReply* reply) {
    if (!owner || !ref || ref->size < sizeof(KilnObjectRef) || !ref->entity || ref->world_kind >= KILN_WORLD_KINDS ||
        !expected || !desired || !bytes || !reply || reply->size < sizeof(KilnMutationReply))
        return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t reply_size = reply->size;
    *reply = KilnMutationReply{};
    reply->size = reply_size;
    reply->generation = ref->generation;
    reply->session = ref->session;
    auto finish = [&](uint32_t status, uint32_t why, uint32_t flags = 0) -> KilnResult {
        reply->status = status;
        reply->why_code = why;
        reply->flags |= flags;
        return result_of(status);
    };
    // Owner scoping: a retired owner, another owner's ref.
    const uint64_t retirements = retirements_.load(std::memory_order_acquire);
    if (retired(owner)) return finish(KILN_ECS_STALE, KILN_ECS_WRITE_WHY_OWNER_RETIRED);
    if (ref->reload_epoch != owner) return finish(KILN_ECS_STALE, KILN_ECS_WRITE_WHY_NOT_OWNERS_REF);
    if (ref->world_kind != KILN_WORLD_SERVER) return finish(KILN_ECS_UNAVAILABLE, KILN_ECS_WRITE_WHY_NOT_SERVER_WORLD);
    if (!available()) return finish(KILN_ECS_UNAVAILABLE, KILN_ECS_WRITE_WHY_NO_PROFILE);
    engine::Reader& reader = *src_.reader(src_.user);
    // The world and the component's policy.
    ContextFact ctx{};
    const uint32_t cs = reader.context(owner, KILN_WORLD_SERVER, &ctx);
    if (cs != KILN_ECS_OK) return finish(cs, KILN_ECS_WRITE_WHY_NO_WORLD);
    if (!ctx.present) return finish(KILN_ECS_UNAVAILABLE, KILN_ECS_WRITE_WHY_NO_WORLD);
    if (ctx.engine_status != KILN_ECS_OK) return finish(ctx.engine_status, KILN_ECS_WRITE_WHY_NO_WORLD);
    reply->step = src_.server_step ? src_.server_step(src_.user) : 0;
    KilnEcsWritePolicy pol{};
    pol.size = sizeof pol;
    uint16_t index = 0;
    const KilnResult prc = policy_for(reader, ctx, true, component, &pol, &index);
    if (prc == KILN_ECS_ERR_UNREADABLE) return finish(KILN_ECS_UNREADABLE, KILN_ECS_WRITE_WHY_FAULT);
    if (pol.status != KILN_ECS_OK) return finish(pol.status, pol.why_code);
    if (bytes != pol.component_size) return finish(KILN_ECS_NOT_WRITABLE, KILN_ECS_WRITE_WHY_SIZE);
    // An earlier uncertain write to this target (or a full latch table) this session: no gate.
    const Target target{ref->session, ref->world_kind, ref->entity, ref->generation, component};
    if (latch_.latched(target))
        return finish(KILN_ECS_UNCERTAIN, KILN_ECS_WRITE_WHY_TARGET_LATCHED, KILN_MUTATION_TARGET_LATCHED);
    // The ref through the reads' logic: session, reload epoch, generation, layout.
    KilnObjectInfo info{};
    info.size = sizeof info;
    entity_validate(reader.backend(), owner, ref, &info);
    if (info.status != KILN_ECS_OK) return finish(info.status, KILN_ECS_WRITE_WHY_REF);
    // Staging: the caller's buffers are copied once and never read inside the hold.
    std::vector<uint8_t> stage(static_cast<size_t>(bytes) * 6);
    uint8_t* const exp = stage.data();
    uint8_t* const msk = exp + bytes;
    uint8_t* const des = msk + bytes;
    uint8_t* const before = des + bytes;
    uint8_t* const merged = before + bytes;
    uint8_t* const verified = merged + bytes;
    std::memcpy(exp, expected, bytes);
    std::memcpy(des, desired, bytes);
    if (mask) std::memcpy(msk, mask, bytes);
    if (!ring_.reserve()) return finish(KILN_ECS_BUSY, KILN_ECS_WRITE_WHY_EVENTS_FULL);
    // The view the gate takes: the local authority's, naming the context's world.
    EcsView view{};
    uint32_t reason = 0;
    if (src_.engine->ecs_view(0, &view, &reason) != KILN_OK) {
        ring_.unreserve();
        reply->engine_reason = reason;
        uint32_t why = 0;
        const uint32_t status = gate_status(static_cast<Reason>(reason), &why);
        return finish(status, why);
    }
    if (view.role != Role::LocalAuthority) {
        ring_.unreserve();
        return finish(KILN_ECS_UNAVAILABLE, KILN_ECS_WRITE_WHY_NOT_SERVER_WORLD);
    }
    if (view.manager != ctx.manager || view.registry_owner != ctx.registry) {
        ring_.unreserve();
        return finish(KILN_ECS_UNREADABLE, KILN_ECS_WRITE_WHY_REF);
    }
    HoldOutput out{};
    uint64_t hold_ns = 0;
    {
        EcsGateScope scope(*src_.engine, view, owner, reply->step ? reply->step : 1, GateAdmission::KilnMod);
        if (!scope.held()) {
            const Reason r = scope.reason();
            scope.release();
            ring_.unreserve();
            reply->engine_reason = static_cast<uint32_t>(r);
            uint32_t why = 0;
            const uint32_t status = gate_status(r, &why);
            return finish(status, why);
        }
        const uint64_t acquired = scope.gate().acquired_ns;
        HoldInput in;
        in.scope = &scope;
        in.reader = &reader;
        in.ctx = &ctx;
        in.entity = ref->entity;
        in.generation = ref->generation;
        in.layout_stamp = ref->layout_stamp;
        in.index = index;
        in.size = static_cast<uint16_t>(bytes);
        in.probe_bound = probe_bound;
        in.expected = exp;
        in.mask = mask ? msk : nullptr;
        in.desired = des;
        in.before = before;
        in.merged = merged;
        in.verified = verified;
        in.retirements = &retirements_;
        in.retirements_seen = retirements;
        in.ring = &ring_;
        hold(in, out);
        scope.release();
        hold_ns = acquired ? now_ns() - acquired : 0;
    }
    // After the release: the event, the latch, the caller's buffers.
    reply->hold_ns = hold_ns;
    reply->generation = out.generation;
    if (out.claimed) {
        KilnEcsWriteEvent ev{};
        ev.size = sizeof ev;
        ev.world_kind = KILN_WORLD_SERVER;
        ev.entity = ref->entity;
        ev.generation = ref->generation;
        ev.component = component;
        ev.owner = owner;
        ev.session = ref->session;
        ev.step = reply->step;
        ring_.publish(out.position, ev);
        reply->flags |= KILN_MUTATION_EVENT_QUEUED;
    } else {
        ring_.unreserve();
    }
    if (out.status == KILN_ECS_UNCERTAIN) {
        reply->flags |= KILN_MUTATION_TARGET_LATCHED;
        if (!latch_.latch(target) && !widened_logged_.exchange(true)) {
            char line[200];
            std::snprintf(line, sizeof line,
                          "world.ecs_write: the uncertain-target table is full; every compare-exchange of server session "
                          "%llu answers uncertain (first target: entity %u, component 0x%08x)",
                          static_cast<unsigned long long>(ref->session), ref->entity, component);
            log(KILN_LOG_WARN, line);
        }
    }
    if (confirmed && (out.flags & KILN_MUTATION_CONFIRMED))
        std::memcpy(confirmed, out.status == KILN_ECS_OK && (out.flags & KILN_MUTATION_WRITTEN) ? merged : before, bytes);
    else
        out.flags &= ~static_cast<uint32_t>(KILN_MUTATION_CONFIRMED);
    return finish(out.status, out.why, out.flags);
}

KilnResult Writer::subscribe(KilnOwner owner, KilnEcsWriteEventFn fn, void* user, KilnHandle* out) {
    if (!owner || !fn || !out) return KILN_ERR_INVALID_ARGUMENT;
    *out = 0;
    std::lock_guard lock(mu_);
    if (retired_.count(owner)) return KILN_ERR_UNAVAILABLE;
    auto s = std::make_shared<Subscription>();
    s->handle = ++next_handle_;
    s->owner = owner;
    s->fn = fn;
    s->user = user;
    subs_.push_back(s);
    *out = s->handle;
    return KILN_OK;
}

KilnResult Writer::unsubscribe(KilnHandle handle) {
    std::lock_guard lock(mu_);
    for (auto it = subs_.begin(); it != subs_.end(); ++it) {
        if ((*it)->handle != handle) continue;
        (*it)->alive.store(false, std::memory_order_release);
        subs_.erase(it);
        return KILN_OK;
    }
    return KILN_ERR_NOT_FOUND;
}

size_t Writer::subscriptions() const {
    std::lock_guard lock(mu_);
    return subs_.size();
}

void Writer::owner_retired(KilnOwner owner) {
    {
        std::lock_guard lock(mu_);
        constexpr size_t kMaxRetired = 4096;  // owners are never reused; the oldest (lowest) is forgotten first
        if (retired_.size() >= kMaxRetired) retired_.erase(std::min_element(retired_.begin(), retired_.end()));
        retired_.insert(owner);
        for (auto it = subs_.begin(); it != subs_.end();) {
            if ((*it)->owner == owner) {
                (*it)->alive.store(false, std::memory_order_release);
                it = subs_.erase(it);
            } else {
                ++it;
            }
        }
    }
    retirements_.fetch_add(1, std::memory_order_acq_rel);
}

namespace {
struct Delivery {
    KilnEcsWriteEventFn fn;
    const KilnEcsWriteEvent* event;
    void* user;
};
void deliver(void* p) {
    auto* d = static_cast<Delivery*>(p);
    d->fn(d->event, d->user);
}
}  // namespace

size_t Writer::drain() {
    std::unique_lock drain(drain_mu_, std::try_to_lock);
    if (!drain.owns_lock()) return 0;
    size_t total = 0;
    KilnEcsWriteEvent batch[64];
    for (size_t n; (n = ring_.take(batch, 64)) > 0;) {
        std::vector<std::shared_ptr<Subscription>> subs;
        {
            std::lock_guard lock(mu_);
            subs = subs_;
        }
        for (size_t i = 0; i < n; ++i) {
            for (const auto& s : subs) {
                if (!s->alive.load(std::memory_order_acquire)) continue;  // unsubscribed, also during this drain
                Delivery d{s->fn, &batch[i], s->user};
                unsigned long code = 0;
                const bool ok = src_.guarded ? src_.guarded(&deliver, &d, &code) : (deliver(&d), true);
                if (ok) continue;
                // A faulting subscriber is ended and the drain goes on.
                faults_.fetch_add(1, std::memory_order_relaxed);
                s->alive.store(false, std::memory_order_release);
                unsubscribe(s->handle);
                char line[160];
                std::snprintf(line, sizeof line,
                              "world.ecs_write: a write-event subscriber of owner %llu faulted (0x%08lx); its "
                              "subscription ended",
                              static_cast<unsigned long long>(s->owner), code);
                log(KILN_LOG_ERROR, line);
            }
        }
        total += n;
    }
    return total;
}

} // namespace kw::ecs::writes
