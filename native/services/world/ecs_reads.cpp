// SPDX-License-Identifier: MIT
#include "ecs_reads.h"

#include <cmath>
#include <cstring>

namespace kw::ecs {
namespace {

bool bad_backend(const Backend& b) { return !b.context || !b.entity || !b.component || !b.copy || !b.entity_ids; }

uint32_t get_context(const Backend& b, uint32_t kind, ContextFact& c) {
    c = ContextFact{};
    const uint32_t s = b.context(b.user, kind, &c);
    if (s != KILN_ECS_OK) return s;
    return c.present ? KILN_ECS_OK : KILN_ECS_UNAVAILABLE;
}

// Validate `ref` against the live world. On KILN_ECS_OK, ctx/fact hold the facts the ref was checked against.
uint32_t validate(const Backend& b, const KilnObjectRef& ref, ContextFact& ctx, EntityFact& fact) {
    if (uint32_t s = get_context(b, ref.world_kind, ctx); s != KILN_ECS_OK) return s;
    if (ctx.session != ref.session) return KILN_ECS_STALE;
    if (ctx.reload_epoch && ref.reload_epoch != ctx.reload_epoch) return KILN_ECS_STALE;
    EntityFact f{};
    if (uint32_t s = b.entity(b.user, ctx, ref.entity, &f); s != KILN_ECS_OK) return s;
    if (!f.alive) return KILN_ECS_ABSENT;
    if (f.generation != ref.generation || f.layout_stamp != ref.layout_stamp) return KILN_ECS_STALE;
    ContextFact after{};
    if (uint32_t s = get_context(b, ref.world_kind, after); s != KILN_ECS_OK) return s;
    if (after.session != ctx.session || after.layout_stamp != ctx.layout_stamp) return KILN_ECS_STALE;
    fact = f;
    ctx = after;
    return KILN_ECS_OK;
}

void fill_info(const KilnObjectRef& ref, uint32_t status, const ContextFact& ctx, const EntityFact& fact,
               KilnObjectInfo& out) {
    const uint32_t size = out.size;
    out = KilnObjectInfo{};
    out.size = size;
    out.status = status;
    out.entity = ref.entity;
    out.world_kind = ref.world_kind;
    out.session = ctx.session;
    out.reload_epoch = ref.reload_epoch;
    if (status == KILN_ECS_OK) {
        out.generation = fact.generation;
        out.layout_stamp = fact.layout_stamp;
        out.flags = (uint32_t)KILN_OBJECT_ALIVE | (fact.has_template ? (uint32_t)KILN_OBJECT_HAS_TEMPLATE : 0u);
    }
}

// Two copies of one component, each followed by a fresh validation. The copies must match: a value that changed
// or tore between them is unreadable, and nothing is published. `out` is filled only on success.
uint32_t copy_stable(const Backend& b, const KilnObjectRef& ref, uint32_t hash, const ComponentFact& cf,
                     std::vector<uint8_t>& out) {
    // Stamp, then copy, then stamp, twice. The stamp has to be the same value on both sides of a copy, and the
    // two copies have to be identical. A writer that bumps content_stamp around its store cannot hide a tear.
    auto sample = [&](ComponentFact& got) -> uint32_t {
        ContextFact ctx{};
        EntityFact fact{};
        if (uint32_t s = validate(b, ref, ctx, fact); s != KILN_ECS_OK) return s;
        if (uint32_t s = b.component(b.user, ctx, fact, ref.entity, hash, &got); s != KILN_ECS_OK) return s;
        if (!got.present || got.address != cf.address || got.size != cf.size) return KILN_ECS_STALE;
        if (got.content_stamp != cf.content_stamp) return KILN_ECS_UNREADABLE;
        return KILN_ECS_OK;
    };
    auto take = [&](std::vector<uint8_t>& dest) -> uint32_t {
        ComponentFact before{};
        if (uint32_t s = sample(before); s != KILN_ECS_OK) return s;
        dest.assign(cf.size, 0);
        if (cf.size && !b.copy(b.user, cf.address, dest.data(), cf.size)) return KILN_ECS_UNREADABLE;
        ComponentFact after{};
        return sample(after);
    };
    std::vector<uint8_t> first, second;
    if (uint32_t s = take(first); s != KILN_ECS_OK) return s;
    if (uint32_t s = take(second); s != KILN_ECS_OK) return s;
    if (first != second) return KILN_ECS_UNREADABLE;
    out = std::move(first);
    return KILN_ECS_OK;
}

KilnObjectRef make_ref(const KilnObjectRef& base, const ContextFact& ctx, const EntityFact& f) {
    KilnObjectRef r{};
    r.size = sizeof r;
    r.entity = base.entity;
    r.generation = f.generation;
    r.world_kind = base.world_kind;
    r.session = ctx.session;
    r.layout_stamp = f.layout_stamp;
    r.reload_epoch = base.reload_epoch;
    return r;
}

} // namespace

uint32_t result_of(uint32_t status) {
    switch (status) {
        case KILN_ECS_OK: return KILN_OK;
        case KILN_ECS_ABSENT: return KILN_ERR_NOT_FOUND;
        case KILN_ECS_STALE: return KILN_ECS_ERR_STALE;
        case KILN_ECS_UNREADABLE: return KILN_ECS_ERR_UNREADABLE;
        default: return KILN_ERR_UNAVAILABLE;
    }
}

KilnResult entity_open(const Backend& b, uint64_t reload_epoch, uint32_t world_kind, uint64_t expected_session,
                       KilnEntity entity, KilnObjectRef* out) {
    if (bad_backend(b) || !out || !entity || world_kind >= KILN_WORLD_KINDS) return KILN_ERR_INVALID_ARGUMENT;
    *out = KilnObjectRef{};
    ContextFact ctx{};
    if (uint32_t s = get_context(b, world_kind, ctx); s != KILN_ECS_OK) return result_of(s);
    if (expected_session && expected_session != ctx.session) return KILN_ECS_ERR_STALE;
    EntityFact f{};
    if (uint32_t s = b.entity(b.user, ctx, entity, &f); s != KILN_ECS_OK) return result_of(s);
    if (!f.alive) return KILN_ERR_NOT_FOUND;
    // The same facts, read again: a generation or layout that moved during the open is stale, never a ref
    // that was already wrong when it was handed out.
    ContextFact ctx2{};
    EntityFact again{};
    if (uint32_t s = get_context(b, world_kind, ctx2); s != KILN_ECS_OK) return result_of(s);
    if (ctx2.session != ctx.session || ctx2.layout_stamp != ctx.layout_stamp) return KILN_ECS_ERR_STALE;
    if (uint32_t s = b.entity(b.user, ctx2, entity, &again); s != KILN_ECS_OK) return result_of(s);
    if (!again.alive) return KILN_ERR_NOT_FOUND;
    if (again.generation != f.generation || again.layout_stamp != f.layout_stamp) return KILN_ECS_ERR_STALE;
    KilnObjectRef probe{};
    probe.entity = entity;
    probe.world_kind = world_kind;
    probe.reload_epoch = reload_epoch;
    *out = make_ref(probe, ctx, f);
    return KILN_OK;
}

KilnResult entity_validate(const Backend& b, const KilnObjectRef* ref, KilnObjectInfo* out) {
    if (bad_backend(b) || !ref || ref->size < sizeof(KilnObjectRef) || !ref->entity ||
        ref->world_kind >= KILN_WORLD_KINDS || !out || out->size < sizeof(KilnObjectInfo))
        return KILN_ERR_INVALID_ARGUMENT;
    ContextFact ctx{};
    EntityFact fact{};
    const uint32_t s = validate(b, *ref, ctx, fact);
    fill_info(*ref, s, ctx, fact, *out);
    return result_of(s);
}

KilnResult ecs_context_snapshot(const Backend& b, KilnEcsContextInfo* out) {
    if (bad_backend(b) || !out || out->size < sizeof(KilnEcsContextInfo)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    *out = KilnEcsContextInfo{};
    out->size = size;
    out->status = KILN_ECS_UNAVAILABLE;
    uint32_t result = KILN_ERR_UNAVAILABLE;
    for (uint32_t kind = 0; kind < KILN_WORLD_KINDS; ++kind) {
        ContextFact c{};
        const uint32_t s = b.context(b.user, kind, &c);
        if (s != KILN_ECS_OK) continue;
        // A world that is simply absent is still an answer: the exact-profile flag is a property of the build.
        if (c.exact_profile) out->exact_profile = 1;
        out->status = KILN_ECS_OK;
        result = KILN_OK;
        if (!c.present) continue;
        out->present_mask |= 1u << kind;
        out->sessions[kind] = c.session;
        if (!out->layout_stamp) out->layout_stamp = c.layout_stamp;
    }
    return result;
}

KilnResult ecs_snapshot(const Backend& b, const KilnObjectRef* ref, uint32_t component, void* out,
                        uint32_t capacity, uint32_t* required) {
    if (required) *required = 0;
    if (bad_backend(b) || !ref || ref->size < sizeof(KilnObjectRef) || !component || (capacity && !out))
        return KILN_ERR_INVALID_ARGUMENT;
    ContextFact ctx{};
    EntityFact fact{};
    if (uint32_t s = validate(b, *ref, ctx, fact); s != KILN_ECS_OK) return result_of(s);
    ComponentFact cf{};
    if (uint32_t s = b.component(b.user, ctx, fact, ref->entity, component, &cf); s != KILN_ECS_OK)
        return result_of(s);
    if (!cf.present) return KILN_ERR_NOT_FOUND;
    if (required) *required = cf.size;
    if (capacity < cf.size) return KILN_ERR_BUFFER_TOO_SMALL;
    std::vector<uint8_t> staging;
    if (uint32_t s = copy_stable(b, *ref, component, cf, staging); s != KILN_ECS_OK) return result_of(s);
    if (!staging.empty()) std::memcpy(out, staging.data(), staging.size());
    return KILN_OK;
}

KilnResult ecs_snapshot_many(const Backend& b, const KilnObjectRef* ref, const KilnEcsSnapshotSpec* specs,
                             uint32_t count, void* out, uint32_t capacity, uint32_t* required, uint32_t* presence) {
    if (required) *required = 0;
    if (presence && count) std::memset(presence, 0, count * sizeof(uint32_t));
    if (bad_backend(b) || !ref || ref->size < sizeof(KilnObjectRef) || !count || !specs || (capacity && !out))
        return KILN_ERR_INVALID_ARGUMENT;
    for (uint32_t i = 0; i < count; ++i)
        if (!specs[i].component || specs[i].reserved) return KILN_ERR_INVALID_ARGUMENT;
    ContextFact ctx{};
    EntityFact fact{};
    if (uint32_t s = validate(b, *ref, ctx, fact); s != KILN_ECS_OK) {
        if (presence) for (uint32_t i = 0; i < count; ++i) presence[i] = s;
        return result_of(s);
    }
    std::vector<uint8_t> packed;
    uint32_t fatal = KILN_ECS_OK;
    for (uint32_t i = 0; i < count; ++i) {
        if (fatal != KILN_ECS_OK) {
            if (presence) presence[i] = fatal;
            continue;
        }
        ComponentFact cf{};
        uint32_t s = b.component(b.user, ctx, fact, ref->entity, specs[i].component, &cf);
        if (s != KILN_ECS_OK) { fatal = s; if (presence) presence[i] = s; continue; }
        if (!cf.present) { if (presence) presence[i] = KILN_ECS_ABSENT; continue; }
        std::vector<uint8_t> staging;
        s = copy_stable(b, *ref, specs[i].component, cf, staging);
        if (s != KILN_ECS_OK) { fatal = s; if (presence) presence[i] = s; continue; }
        packed.insert(packed.end(), staging.begin(), staging.end());
        if (presence) presence[i] = KILN_ECS_OK;
    }
    if (fatal != KILN_ECS_OK) {
        if (presence) for (uint32_t i = 0; i < count; ++i) presence[i] = fatal;
        if (required) *required = 0;
        return result_of(fatal);
    }
    if (required) *required = static_cast<uint32_t>(packed.size());
    if (capacity < packed.size()) return KILN_ERR_BUFFER_TOO_SMALL;
    if (!packed.empty()) std::memcpy(out, packed.data(), packed.size());
    return KILN_OK;
}

KilnResult ecs_query(const Backend& b, const KilnEcsQueryRequest* request, KilnObjectRef* out, uint32_t capacity,
                     uint32_t* required, uint32_t* state) {
    if (required) *required = 0;
    const bool bounds = request && (request->flags & KILN_ECS_QUERY_BOUNDS);
    const bool bad_bounds = bounds && (!std::isfinite(request->radius) || request->radius <= 0 ||
                                       !std::isfinite(request->center.x) || !std::isfinite(request->center.y) ||
                                       !std::isfinite(request->center.z) || !std::isfinite(request->radius * request->radius));
    if (bad_backend(b) || !request || request->size < sizeof(KilnEcsQueryRequest) ||
        request->world_kind >= KILN_WORLD_KINDS || (request->component_count && !request->components) ||
        (capacity && !out) || (request->flags & ~KILN_ECS_QUERY_BOUNDS) || bad_bounds)
        return KILN_ERR_INVALID_ARGUMENT;
    if (state) *state = KILN_ECS_UNAVAILABLE;
    ContextFact ctx{};
    if (uint32_t s = get_context(b, request->world_kind, ctx); s != KILN_ECS_OK) {
        if (state) *state = s;
        return result_of(s);
    }
    if ((request->flags & KILN_ECS_QUERY_BOUNDS) && !b.position) {
        if (state) *state = KILN_ECS_UNAVAILABLE;
        return KILN_ERR_UNAVAILABLE;
    }
    // OK = a match (written to *slot when non-NULL). ABSENT = not a match. Anything else fails the query:
    // an unreadable row is not silently dropped.
    auto classify = [&](const ContextFact& world, KilnEntity id, KilnObjectRef* slot) -> uint32_t {
        EntityFact f{};
        const uint32_t es = b.entity(b.user, world, id, &f);
        if (es == KILN_ECS_ABSENT || (es == KILN_ECS_OK && !f.alive)) return KILN_ECS_ABSENT;
        if (es != KILN_ECS_OK) return es;
        for (uint32_t i = 0; i < request->component_count; ++i) {
            ComponentFact cf{};
            const uint32_t cs = b.component(b.user, world, f, id, request->components[i], &cf);
            if (cs == KILN_ECS_ABSENT || (cs == KILN_ECS_OK && !cf.present)) return KILN_ECS_ABSENT;
            if (cs != KILN_ECS_OK) return cs;
        }
        if (request->flags & KILN_ECS_QUERY_BOUNDS) {
            double p[3] = {};
            if (!b.position(b.user, world, id, p)) return KILN_ECS_ABSENT;
            const double dx = p[0] - request->center.x, dy = p[1] - request->center.y, dz = p[2] - request->center.z;
            if (dx * dx + dy * dy + dz * dz > request->radius * request->radius) return KILN_ECS_ABSENT;
        }
        if (slot) {
            KilnObjectRef probe{};
            probe.entity = id;
            probe.world_kind = request->world_kind;
            probe.reload_epoch = world.reload_epoch;
            *slot = make_ref(probe, world, f);
        }
        return KILN_ECS_OK;
    };
    auto collect = [&](const ContextFact& world, std::vector<KilnObjectRef>* into) -> uint32_t {
        std::vector<KilnEntity> list;
        if (uint32_t s = b.entity_ids(b.user, world, &list); s != KILN_ECS_OK) return s;
        if (into) into->clear();
        for (KilnEntity id : list) {
            KilnObjectRef slot{};
            const uint32_t s = classify(world, id, into ? &slot : nullptr);
            if (s == KILN_ECS_ABSENT) continue;
            if (s != KILN_ECS_OK) return s;
            if (into) into->push_back(slot);
        }
        return KILN_ECS_OK;
    };
    std::vector<KilnObjectRef> matches;
    if (uint32_t s = collect(ctx, &matches); s != KILN_ECS_OK) {
        if (state) *state = s;
        return result_of(s);
    }
    // The same query, again. A different set (a component gained or lost, an entity moved into the radius, a
    // generation change) is not a complete result: copy nothing.
    ContextFact ctx2{};
    if (uint32_t s = get_context(b, request->world_kind, ctx2); s != KILN_ECS_OK) {
        if (state) *state = s;
        return result_of(s);
    }
    std::vector<KilnObjectRef> again;
    if (uint32_t s = collect(ctx2, &again); s != KILN_ECS_OK) {
        if (state) *state = s == KILN_ECS_STALE ? (uint32_t)KILN_ECS_UNREADABLE : s;
        return s == KILN_ECS_STALE ? KILN_WORLD_ERR_CHANGED : result_of(s);
    }
    auto same = matches.size() == again.size();
    for (size_t i = 0; same && i < matches.size(); ++i)
        same = matches[i].entity == again[i].entity && matches[i].generation == again[i].generation &&
               matches[i].layout_stamp == again[i].layout_stamp && matches[i].session == again[i].session;
    if (!same || ctx2.session != ctx.session || ctx2.revision != ctx.revision) {
        if (state) *state = KILN_ECS_UNREADABLE;
        return KILN_WORLD_ERR_CHANGED;
    }
    if (state) *state = KILN_ECS_OK;
    if (required) *required = static_cast<uint32_t>(matches.size());
    if (matches.size() > capacity) return KILN_ERR_BUFFER_TOO_SMALL;
    if (!matches.empty()) std::memcpy(out, matches.data(), matches.size() * sizeof(KilnObjectRef));
    return KILN_OK;
}

KilnResult ecs_template_uuid(const Backend& b, const KilnObjectRef* ref, uint8_t guid[16]) {
    if (bad_backend(b) || !ref || ref->size < sizeof(KilnObjectRef) || !guid) return KILN_ERR_INVALID_ARGUMENT;
    ContextFact ctx{};
    EntityFact fact{};
    if (uint32_t s = validate(b, *ref, ctx, fact); s != KILN_ECS_OK) return result_of(s);
    if (!fact.has_template) return KILN_ERR_NOT_FOUND;
    uint8_t staging[16];
    std::memcpy(staging, fact.template_uuid, sizeof staging);
    ContextFact ctx2{};
    EntityFact fact2{};
    if (uint32_t s = validate(b, *ref, ctx2, fact2); s != KILN_ECS_OK) return result_of(s);
    if (!fact2.has_template || std::memcmp(fact2.template_uuid, staging, sizeof staging) != 0)
        return KILN_ECS_ERR_STALE;
    std::memcpy(guid, staging, sizeof staging);
    return KILN_OK;
}

} // namespace kw::ecs
