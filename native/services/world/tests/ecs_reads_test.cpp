// SPDX-License-Identifier: MIT
// Host test: kiln.world's verified-copy ECS reads over a fake engine memory model. The fake is a byte
// buffer with entity records, archetype component bytes and a registry revision; tests mutate it between the two
// validations of a read (generation bump, session/layout change, component removal, template swap, membership
// change, copy fault) and require a precise refusal with nothing copied. Barriers, never loops-and-hope, drive the
// concurrent case. No game, no KilnApi.
#include <atomic>
#include <limits>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <functional>
#include <map>
#include <mutex>
#include <thread>
#include <vector>

#include "../ecs_reads.h"

namespace {
using namespace kw::ecs;
using Status = uint32_t;
int checks = 0, failures = 0;
void check(bool ok, const char* what) {
    ++checks;
    if (!ok) { ++failures; std::fprintf(stderr, "FAIL %s\n", what); }
}
bool all_eq(const std::vector<uint8_t>& v, uint8_t b) { for (auto x : v) if (x != b) return false; return true; }

struct Fake {
    std::vector<uint8_t> mem = std::vector<uint8_t>(1 << 16, 0);
    struct Ent {
        uint32_t generation = 1;
        uint64_t layout = 11;
        bool alive = true, has_template = true;
        uint8_t uuid[16] = {1, 2, 3};
        std::map<uint32_t, std::pair<uintptr_t, uint32_t>> comps; // hash -> (address, size)
        double pos[3] = {0, 0, 0};
        bool has_pos = true;
    };
    std::map<uint32_t, Ent> ents;
    uint64_t session = 7, layout_stamp = 99, revision = 1, epoch = 5;
    bool present = true, exact = true, fault_copy = false, partial_then_fault = false, diverge = false, bump = false,
         grow = false, pos_fault = false;
    int copies = 0, listings = 0;
    int copy_calls = 0, component_calls = 0, has_calls = 0;
    bool owner_epochs = false;           // per-owner reload epochs (open_epoch), as the production backend keeps them
    std::map<KilnOwner, uint64_t> epochs; // owner -> current epoch
    int move_on_component = 0;           // comp_op number N (from 1) moves entity 10's kA bytes to a new address
    bool client_present = false;         // a client world (the same entities) beside the server world
    bool frame = false;                  // a server frame runs (its word is held)
    uint64_t step = 1;                   // the server step
    bool step_on_copy = false;           // every copy lets a whole frame run (the step moves, the word is free again)
    int recycle_on_copy = 0;  // copy_op number N (from 1) recycles entity 10 right after copying its bytes
    uint32_t recycle_on = 0; // comp_op for this hash first recycles entity 10 (new generation), then looks it up
    uint64_t stamp = 1;
    Status context_status = KILN_ECS_OK, entity_status = KILN_ECS_OK;
    std::function<void(int)> hook; // called with a phase counter after each backend op
    int phase = 0;
    std::mutex mu; // guards ents/mem for the concurrency test (backend ops lock it; hooks run outside it)

    size_t next_alloc = 1024;
    uintptr_t alloc_bytes(const std::vector<uint8_t>& bytes) {
        size_t next = next_alloc;
        const uintptr_t at = reinterpret_cast<uintptr_t>(mem.data()) + next;
        std::memcpy(mem.data() + next, bytes.data(), bytes.size());
        next_alloc = next + ((bytes.size() + 15) & ~size_t{15});
        return at;
    }
    void add(uint32_t id, uint32_t hash, const std::vector<uint8_t>& bytes, uint32_t gen = 1) {
        Ent& e = ents[id];
        e.generation = gen;
        if (!bytes.empty()) e.comps[hash] = {alloc_bytes(bytes), (uint32_t)bytes.size()};
    }
    void fire() { if (hook) { auto h = hook; const int p = ++phase; h(p); } }
};

Status ctx_op(void* u, KilnOwner owner, uint32_t kind, ContextFact* f) {
    auto& F = *static_cast<Fake*>(u);
    if (F.context_status != KILN_ECS_OK) return F.context_status;
    if (kind != KILN_WORLD_SERVER && !(kind == KILN_WORLD_CLIENT && F.client_present)) { f->exact_profile = F.exact; return KILN_ECS_OK; }
    f->session = F.session; f->layout_stamp = F.layout_stamp; f->revision = F.revision;
    if (F.owner_epochs) {
        std::unique_lock lock(F.mu);
        f->reload_epoch = owner ? F.epochs[owner] : 0;
        f->epoch_tracked = owner != 0;
    } else {
        f->reload_epoch = F.epoch;
        f->epoch_tracked = F.epoch != 0;
    }
    f->present = F.present; f->exact_profile = F.exact;
    F.fire();
    return KILN_ECS_OK;
}
Status ent_op(void* u, const ContextFact&, KilnEntity id, EntityFact* f) {
    auto& F = *static_cast<Fake*>(u);
    std::unique_lock lock(F.mu);
    if (F.entity_status != KILN_ECS_OK) return F.entity_status;
    auto it = F.ents.find(id);
    Status s = KILN_ECS_OK;
    if (it == F.ents.end() || !it->second.alive) s = KILN_ECS_ABSENT;
    else {
        f->generation = it->second.generation; f->layout_stamp = it->second.layout;
        f->alive = true; f->has_template = it->second.has_template;
        std::memcpy(f->template_uuid, it->second.uuid, 16);
    }
    lock.unlock();
    F.fire();
    return s;
}
Status comp_op(void* u, const ContextFact&, const EntityFact&, KilnEntity id, uint32_t hash, ComponentFact* f) {
    auto& F = *static_cast<Fake*>(u);
    std::unique_lock lock(F.mu);
    ++F.component_calls;
    if (F.recycle_on && hash == F.recycle_on && F.ents.count(10)) { ++F.ents[10].generation; F.recycle_on = 0; }
    if (F.move_on_component && F.component_calls == F.move_on_component && F.ents.count(10) && F.ents[10].comps.count(hash)) {
        // Same entity, same generation and layout, its row moved: the bytes are now somewhere else.
        auto& c = F.ents[10].comps[hash];
        std::vector<uint8_t> bytes(reinterpret_cast<const uint8_t*>(c.first), reinterpret_cast<const uint8_t*>(c.first) + c.second);
        c.first = F.alloc_bytes(bytes);
    }
    auto it = F.ents.find(id);
    Status s = KILN_ECS_OK;
    if (it == F.ents.end() || !it->second.alive) s = KILN_ECS_ABSENT;
    else {
        auto c = it->second.comps.find(hash);
        if (c == it->second.comps.end()) { f->present = false; }
        else { f->present = true; f->address = c->second.first; f->size = c->second.second; f->content_stamp = F.stamp; }
    }
    lock.unlock();
    F.fire();
    return s;
}
bool copy_op(void* u, uintptr_t at, void* out, size_t n) {
    auto& F = *static_cast<Fake*>(u);
    bool ok = true;
    {
        std::unique_lock lock(F.mu);
        ++F.copy_calls;
        if (F.fault_copy) ok = false;
        else if (F.partial_then_fault) { if (n) std::memset(out, 0xEE, 1); ok = false; }
        else {
            std::memcpy(out, reinterpret_cast<const void*>(at), n);
            // The bytes change after the first copy and the generation does not: the two copies must not agree.
            if (F.diverge && ++F.copies == 1 && n) reinterpret_cast<uint8_t*>(at)[0] ^= 0xff;
            else if (F.bump && ++F.copies == 1) ++F.stamp;
            else if (F.recycle_on_copy && ++F.copies == F.recycle_on_copy && F.ents.count(10)) ++F.ents[10].generation;
            if (F.step_on_copy) ++F.step;
        }
    }
    F.fire();
    return ok;
}
Status ids_op(void* u, const ContextFact&, std::vector<KilnEntity>* out) {
    auto& F = *static_cast<Fake*>(u);
    {
        std::unique_lock lock(F.mu);
        // On the second listing, entity 20 gains component 200. The two passes of a query must not both succeed.
        if (F.grow && ++F.listings == 2 && F.ents.count(10) && F.ents[10].comps.count(200) && F.ents.count(20))
            F.ents[20].comps[200] = F.ents[10].comps[200];
        for (auto& [id, e] : F.ents) if (e.alive) out->push_back(id);
    }
    F.fire();
    return KILN_ECS_OK;
}
uint32_t pos_op(void* u, const ContextFact&, KilnEntity id, double out[3]) {
    auto& F = *static_cast<Fake*>(u);
    if (F.pos_fault) return KILN_ECS_UNREADABLE;
    std::unique_lock lock(F.mu);
    auto it = F.ents.find(id);
    if (it == F.ents.end() || !it->second.has_pos) return KILN_ECS_ABSENT;
    std::memcpy(out, it->second.pos, sizeof it->second.pos);
    return KILN_ECS_OK;
}
Status has_op(void* u, const ContextFact&, const EntityFact&, KilnEntity id, uint32_t hash, bool* present) {
    auto& F = *static_cast<Fake*>(u);
    std::unique_lock lock(F.mu);
    ++F.has_calls;
    auto it = F.ents.find(id);
    if (it == F.ents.end() || !it->second.alive) return KILN_ECS_ABSENT;
    *present = it->second.comps.count(hash) != 0;
    return KILN_ECS_OK;
}
uint64_t open_epoch_op(void* u, KilnOwner owner, uint64_t epoch) {
    auto& F = *static_cast<Fake*>(u);
    std::unique_lock lock(F.mu);
    uint64_t& current = F.epochs[owner];
    if (epoch > current) current = epoch;
    return current;
}
Status quiet_begin_op(void* u, const ContextFact& ctx, uint64_t* token) {
    auto& F = *static_cast<Fake*>(u);
    if (ctx.world_kind != KILN_WORLD_SERVER) return KILN_ECS_UNSUPPORTED;
    std::unique_lock lock(F.mu);
    if (F.frame) return KILN_ECS_BUSY;
    *token = F.step;
    return KILN_ECS_OK;
}
Status quiet_end_op(void* u, const ContextFact&, uint64_t token) {
    auto& F = *static_cast<Fake*>(u);
    std::unique_lock lock(F.mu);
    return !F.frame && F.step == token ? KILN_ECS_OK : KILN_ECS_BUSY;
}
Backend backend(Fake& F) {
    return Backend{&F, &ctx_op, &ent_op, &comp_op, &copy_op, &ids_op, &pos_op};
}
Backend owner_backend(Fake& F) {
    F.owner_epochs = true;
    Backend b = backend(F);
    b.has = &has_op;
    b.open_epoch = &open_epoch_op;
    return b;
}
Backend quiet_backend(Fake& F) {
    Backend b = backend(F);
    b.quiet_begin = &quiet_begin_op;
    b.quiet_end = &quiet_end_op;
    return b;
}
constexpr KilnOwner kOwner = 3, kOther = 4;
KilnObjectRef open_ok(Backend& b, uint32_t id) {
    KilnObjectRef r{};
    check(entity_open(b, kOwner, 5, KILN_WORLD_SERVER, 0, id, &r) == KILN_OK, "open succeeds");
    return r;
}

constexpr uint32_t kA = 100, kB = 200;

void identity() {
    Fake F; F.add(10, kA, {9, 9, 9, 9}); Backend b = backend(F);
    KilnObjectRef r = open_ok(b, 10);
    check(r.entity == 10 && r.generation == 1 && r.session == 7 && r.layout_stamp == 11 && r.reload_epoch == 5, "ref carries id/generation/session/layout/epoch");
    KilnObjectInfo info{}; info.size = sizeof info;
    check(entity_validate(b, kOwner, &r, &info) == KILN_OK && info.status == KILN_ECS_OK && (info.flags & KILN_OBJECT_ALIVE), "validate ok");
    // Recycled id: same id, new generation is always stale.
    F.ents[10].generation = 2;
    check(entity_validate(b, kOwner, &r, &info) == KILN_ECS_ERR_STALE && info.status == KILN_ECS_STALE && info.generation == 2 &&
              info.session == 7 && !(info.flags & KILN_OBJECT_ALIVE) && (info.flags & KILN_OBJECT_HAS_TEMPLATE),
          "recycled id is stale and the info names the generation it is now");
    KilnObjectRef r2 = open_ok(b, 10);
    check(r2.generation == 2 && entity_validate(b, kOwner, &r2, &info) == KILN_OK, "reopened ref validates");
    // Session change and layout change are stale; a save-loaded entity (no template) opens fine.
    F.session = 8;
    check(entity_validate(b, kOwner, &r2, &info) == KILN_ECS_ERR_STALE && info.session == 8, "session change is stale and the info names the session it is now");
    F.session = 7; F.ents[10].layout = 12;
    check(entity_validate(b, kOwner, &r2, &info) == KILN_ECS_ERR_STALE, "layout change is stale");
    F.ents[10].layout = 11; F.ents[10].has_template = false;
    check(entity_validate(b, kOwner, &r2, &info) == KILN_OK && !(info.flags & KILN_OBJECT_HAS_TEMPLATE), "save-loaded entity validates, no template");
    uint8_t guid[16] = {};
    check(ecs_template_uuid(b, kOwner, &r2, guid) == KILN_ERR_NOT_FOUND, "no template: absent");
    // Absent entity, unavailable context, expected session.
    KilnObjectRef out{};
    check(entity_open(b, kOwner, 5, KILN_WORLD_SERVER, 0, 999, &out) == KILN_ERR_NOT_FOUND, "open absent entity");
    check(entity_open(b, kOwner, 5, KILN_WORLD_SERVER, 42, 10, &out) == KILN_ECS_ERR_STALE, "open with wrong expected session is stale");
    check(entity_open(b, kOwner, 4, KILN_WORLD_SERVER, 0, 10, &out) == KILN_ECS_ERR_STALE && out.entity == 0, "open with another reload epoch is stale and writes no ref");
    F.present = false;
    check(entity_open(b, kOwner, 5, KILN_WORLD_SERVER, 0, 10, &out) == KILN_ERR_UNAVAILABLE, "no world: unavailable");
    F.present = true; F.context_status = KILN_ECS_UNAVAILABLE;
    KilnEcsContextInfo ci{}; ci.size = sizeof ci;
    check(ecs_context_snapshot(b, &ci) == KILN_ERR_UNAVAILABLE && ci.status == KILN_ECS_UNAVAILABLE, "context unavailable on unprofiled build");
    F.context_status = KILN_ECS_OK;
    check(ecs_context_snapshot(b, &ci) == KILN_OK && ci.present_mask == (1u << KILN_WORLD_SERVER) && ci.sessions[KILN_WORLD_SERVER] == 7 && ci.exact_profile == 1, "context snapshot: worlds, sessions, exact profile");
    F.present = false;
    check(ecs_context_snapshot(b, &ci) == KILN_OK && ci.present_mask == 0 && ci.exact_profile == 1, "no world is an answer: exact profile still reported");
    F.present = true;
    F.phase = 0;
    F.hook = [&](int p) { if (p == 1) F.session = 99; };
    check(ecs_context_snapshot(b, &ci) == KILN_ECS_ERR_UNREADABLE && ci.status == KILN_ECS_UNREADABLE && ci.present_mask == 0,
          "a world that changes between the two context samples is unreadable and publishes nothing");
    F.hook = nullptr;
    F.session = 7;
}

void snapshots() {
    Fake F; F.add(10, kA, {1, 2, 3, 4}); F.add(10, kB, {5, 6}); Backend b = backend(F);
    KilnObjectRef r = open_ok(b, 10);
    std::vector<uint8_t> buf(8, 0xAA); uint32_t req = 0;
    check(ecs_snapshot(b, kOwner, &r, kA, buf.data(), 8, &req) == KILN_OK && req == 4 && buf[0] == 1 && buf[3] == 4, "snapshot copies component bytes");
    check(ecs_snapshot(b, kOwner, &r, 777, buf.data(), 8, &req) == KILN_ERR_NOT_FOUND, "snapshot absent component");
    buf.assign(8, 0xAA);
    check(ecs_snapshot(b, kOwner, &r, kA, buf.data(), 2, &req) == KILN_ERR_BUFFER_TOO_SMALL && req == 4 && all_eq(buf, 0xAA), "short buffer: required set, nothing copied");
    // Generation changes between the validations.
    F.phase = 0; F.hook = [&](int p) { if (p == 3) F.ents[10].generation = 9; };
    buf.assign(8, 0xAA);
    check(ecs_snapshot(b, kOwner, &r, kA, buf.data(), 8, &req) == KILN_ECS_ERR_STALE && all_eq(buf, 0xAA), "generation change mid-read: stale, nothing copied");
    F.hook = nullptr; F.ents[10].generation = 1;
    // Component removed between the validations.
    F.phase = 0; F.hook = [&](int p) { if (p == 4) F.ents[10].comps.erase(kA); };
    buf.assign(8, 0xAA);
    check(ecs_snapshot(b, kOwner, &r, kA, buf.data(), 8, &req) == KILN_ECS_ERR_STALE && all_eq(buf, 0xAA), "component removed mid-read: stale, nothing copied");
    F.hook = nullptr; F.add(10, kA, {1, 2, 3, 4});
    // Copy faults (even after a partial write into staging): unreadable, caller buffer untouched.
    F.partial_then_fault = true;
    buf.assign(8, 0xAA);
    check(ecs_snapshot(b, kOwner, &r, kA, buf.data(), 8, &req) == KILN_ECS_ERR_UNREADABLE && all_eq(buf, 0xAA), "faulting copy: unreadable, no partial buffer");
    F.partial_then_fault = false;
    F.diverge = true; F.copies = 0;
    buf.assign(8, 0xAA);
    check(ecs_snapshot(b, kOwner, &r, kA, buf.data(), 8, &req) == KILN_ECS_ERR_UNREADABLE && all_eq(buf, 0xAA), "bytes changed without a new generation: unreadable, nothing copied");
    F.diverge = false;
    F.add(10, kA, {1, 2, 3, 4});
    F.bump = true; F.copies = 0;
    buf.assign(8, 0xAA);
    check(ecs_snapshot(b, kOwner, &r, kA, buf.data(), 8, &req) == KILN_ECS_ERR_UNREADABLE && all_eq(buf, 0xAA), "content stamp moved around the copy: unreadable, nothing copied");
    F.bump = false;
    // The id is recycled during the last copy (same bytes at the same place): only the check after that copy sees it.
    F.copies = 0; F.recycle_on_copy = 2;
    buf.assign(8, 0xAA);
    check(ecs_snapshot(b, kOwner, &r, kA, buf.data(), 8, &req) == KILN_ECS_ERR_STALE && all_eq(buf, 0xAA), "recycled during the second copy: stale, nothing copied");
    F.recycle_on_copy = 0; F.ents[10].generation = 1;
    // Template replaced during its read.
    F.phase = 0; F.hook = [&](int p) { if (p == 2) F.ents[10].uuid[0] = 42; };
    uint8_t guid[16] = {};
    check(ecs_template_uuid(b, kOwner, &r, guid) == KILN_ECS_ERR_STALE, "template replaced mid-read: stale");
    F.hook = nullptr; F.ents[10].uuid[0] = 1;
    check(ecs_template_uuid(b, kOwner, &r, guid) == KILN_OK && guid[0] == 1 && guid[2] == 3, "template uuid read");
    // Many: presence per component, packed bytes, absent takes no room.
    KilnEcsSnapshotSpec specs[2] = {{kA, 0}, {777, 0}};
    uint32_t presence[2] = {99, 99}; buf.assign(8, 0xAA);
    check(ecs_snapshot_many(b, kOwner, &r, specs, 2, buf.data(), 8, &req, presence) == KILN_OK && req == 4 &&
              presence[0] == KILN_ECS_OK && presence[1] == KILN_ECS_ABSENT && buf[0] == 1 && buf[3] == 4,
          "snapshot_many: per-component presence, packed bytes");
    KilnEcsSnapshotSpec both[2] = {{kA, 0}, {kB, 0}};
    buf.assign(8, 0xAA);
    check(ecs_snapshot_many(b, kOwner, &r, both, 2, buf.data(), 3, &req, presence) == KILN_ERR_BUFFER_TOO_SMALL && req == 6 && all_eq(buf, 0xAA), "snapshot_many short buffer: nothing copied");
    // The id is recycled after the validation and before the component lookup: the new occupant has no such
    // component. That answer is about another entity, so it is stale, never absent.
    F.recycle_on = 777;
    check(ecs_snapshot(b, kOwner, &r, 777, buf.data(), 8, &req) == KILN_ECS_ERR_STALE, "absent component of a recycled id: stale, not absent");
    F.ents[10].generation = 1;
    F.recycle_on = 777; buf.assign(8, 0xAA); presence[0] = presence[1] = 99;
    check(ecs_snapshot_many(b, kOwner, &r, specs, 2, buf.data(), 8, &req, presence) == KILN_ECS_ERR_STALE && req == 0 &&
              presence[0] == KILN_ECS_STALE && presence[1] == KILN_ECS_STALE && all_eq(buf, 0xAA),
          "snapshot_many: an absent spec read after the id was recycled is stale, every spec says so, nothing copied");
    F.ents[10].generation = 1;
    // A copy failure after an absent spec: no spec keeps an answer, absent included (nothing re-checked it).
    KilnEcsSnapshotSpec absent_first[2] = {{777, 0}, {kA, 0}};
    F.fault_copy = true; presence[0] = presence[1] = 99;
    check(ecs_snapshot_many(b, kOwner, &r, absent_first, 2, buf.data(), 8, &req, presence) == KILN_ECS_ERR_UNREADABLE &&
              presence[0] == KILN_ECS_UNREADABLE && presence[1] == KILN_ECS_UNREADABLE && all_eq(buf, 0xAA),
          "snapshot_many: a later fault marks every spec, nothing copied");
    F.fault_copy = false;
    // A ref naming no world kind is an argument error on every member, as entity_validate says.
    KilnObjectRef wild = r; wild.world_kind = 99;
    check(ecs_snapshot(b, kOwner, &wild, kA, buf.data(), 8, &req) == KILN_ERR_INVALID_ARGUMENT &&
              ecs_snapshot_many(b, kOwner, &wild, specs, 2, buf.data(), 8, &req, presence) == KILN_ERR_INVALID_ARGUMENT &&
              ecs_template_uuid(b, kOwner, &wild, guid) == KILN_ERR_INVALID_ARGUMENT,
          "a ref with an unknown world kind is refused as an argument");
    uint32_t kept[2] = {7, 7};
    KilnEcsSnapshotSpec bad_spec[1] = {{kA, 4}};  // a flag bit no Kiln knows
    check(ecs_snapshot_many(b, kOwner, &r, bad_spec, 1, buf.data(), 8, &req, kept) == KILN_ERR_INVALID_ARGUMENT && kept[0] == 7 && kept[1] == 7,
          "a bad snapshot_many leaves presence untouched");
}

void queries() {
    Fake F; F.add(10, kA, {1}); F.add(10, kB, {2}); F.add(20, kA, {3}); F.ents[20].pos[0] = 100; Backend b = backend(F);
    KilnEcsQueryRequest q{}; q.size = sizeof q; q.world_kind = KILN_WORLD_SERVER;
    uint32_t comps[1] = {kA}; q.components = comps; q.component_count = 1;
    KilnObjectRef out[4] = {}; uint32_t req = 0, state = 99;
    check(ecs_query(b, kOwner, &q, out, 4, &req, &state) == KILN_OK && req == 2 && state == KILN_ECS_OK && out[0].entity == 10 && out[1].entity == 20, "query AND of one component");
    uint32_t two[2] = {kA, kB}; q.components = two; q.component_count = 2;
    check(ecs_query(b, kOwner, &q, out, 4, &req, &state) == KILN_OK && req == 1 && out[0].entity == 10, "query AND of two components");
    q.flags = KILN_ECS_QUERY_BOUNDS; q.center = {0, 0, 0}; q.radius = 10;
    check(ecs_query(b, kOwner, &q, out, 4, &req, &state) == KILN_OK && req == 1 && out[0].entity == 10, "query with spatial bounds");
    q.flags = 0; q.components = comps; q.component_count = 1;
    for (auto& o : out) o.entity = 0xFFFF;
    check(ecs_query(b, kOwner, &q, out, 1, &req, &state) == KILN_ERR_BUFFER_TOO_SMALL && req == 2 && out[0].entity == 0xFFFF, "query short buffer: required, nothing copied");
    // Membership changes during the walk: no complete result, nothing copied.
    F.phase = 0; F.hook = [&](int p) { if (p == 2) F.revision = 2; };
    for (auto& o : out) o.entity = 0xFFFF;
    check(ecs_query(b, kOwner, &q, out, 4, &req, &state) == KILN_WORLD_ERR_CHANGED && state == KILN_ECS_UNREADABLE && out[0].entity == 0xFFFF, "membership change during query: changed, nothing copied");
    F.hook = nullptr;
    q.flags = 0; q.components = comps; q.component_count = 1;
    check(ecs_query(b, kOwner, &q, out, 4, &req, &state) == KILN_OK && req == 2, "query recovers after the membership change");
    KilnObjectInfo info{}; info.size = sizeof info;
    check(entity_validate(b, kOwner, &out[0], &info) == KILN_OK && info.status == KILN_ECS_OK, "a query ref carries the context epoch and validates");
    F.listings = 0; F.grow = true;
    for (auto& o : out) o.entity = 0xFFFF;
    uint32_t only_b[1] = {kB}; q.components = only_b;
    check(ecs_query(b, kOwner, &q, out, 4, &req, &state) == KILN_WORLD_ERR_CHANGED && out[0].entity == 0xFFFF, "component gained during the query: changed, nothing copied");
    F.grow = false;
    q.flags = KILN_ECS_QUERY_BOUNDS; q.center = {std::numeric_limits<double>::quiet_NaN(), 0, 0}; q.radius = 1; q.components = comps;
    state = 7;
    check(ecs_query(b, kOwner, &q, out, 4, &req, &state) == KILN_ERR_INVALID_ARGUMENT && state == 7, "a NaN centre is an argument error and does not write state");
    q.center = {0, 0, 0}; q.radius = 10; q.components = comps; q.component_count = 1;
    F.pos_fault = true;
    for (auto& o : out) o.entity = 0xFFFF;
    check(ecs_query(b, kOwner, &q, out, 4, &req, &state) == KILN_ECS_ERR_UNREADABLE && state == KILN_ECS_UNREADABLE && out[0].entity == 0xFFFF,
          "a position read that fails fails the query and copies nothing");
}

void concurrent() {
    Fake F; F.add(10, kA, std::vector<uint8_t>(64, 1)); Backend b = backend(F);
    KilnObjectRef r = open_ok(b, 10);
    std::atomic<bool> stop{false}, torn{false}, stale_seen{false};
    std::thread writer([&] {
        bool flip = false;
        while (!stop.load()) {
            {
                std::unique_lock lock(F.mu);
                auto& e = F.ents[10];
                std::memset(reinterpret_cast<void*>(e.comps[kA].first), flip ? 1 : 2, 64);
                ++e.generation;
            }
            flip = !flip;
        }
    });
    std::thread reader([&] {
        std::vector<uint8_t> buf(64);
        for (int i = 0; i < 2000; ++i) {
            uint32_t req = 0;
            KilnResult rc = ecs_snapshot(b, kOwner, &r, kA, buf.data(), 64, &req);
            if (rc == KILN_ECS_ERR_STALE) { stale_seen = true; continue; }
            if (rc != KILN_OK) continue;
            if (!all_eq(buf, 1) && !all_eq(buf, 2)) torn = true;
        }
    });
    reader.join(); stop = true; writer.join();
    check(!torn.load(), "concurrent snapshot: never torn data");
    check(stale_seen.load(), "concurrent snapshot: generation flips are reported stale");
}

// Per-owner reload epochs: the owner's current epoch is the newest it opened a ref with; refs of another epoch are
// stale, and a query's refs carry the caller's epoch, not the world's.
void epochs() {
    Fake F; F.add(10, kA, {1, 2}); F.add(20, kA, {3, 4}); Backend b = owner_backend(F);
    KilnObjectRef r5{}, r6{}, out{};
    check(entity_open(b, kOwner, 5, KILN_WORLD_SERVER, 0, 10, &r5) == KILN_OK && r5.reload_epoch == 5, "epochs: open under 5");
    KilnObjectInfo info{}; info.size = sizeof info;
    check(entity_validate(b, kOwner, &r5, &info) == KILN_OK, "epochs: a ref of the owner's epoch validates");
    check(entity_open(b, kOwner, 6, KILN_WORLD_SERVER, 0, 10, &r6) == KILN_OK && r6.reload_epoch == 6, "epochs: a reload opens under 6");
    check(entity_validate(b, kOwner, &r5, &info) == KILN_ECS_ERR_STALE && info.status == KILN_ECS_STALE && info.reload_epoch == 5,
          "epochs: a ref from before the reload validates stale");
    std::vector<uint8_t> buf(4, 0xAA); uint32_t req = 0;
    check(ecs_snapshot(b, kOwner, &r5, kA, buf.data(), 4, &req) == KILN_ECS_ERR_STALE && all_eq(buf, 0xAA),
          "epochs: a snapshot through a ref from before the reload is stale, nothing copied");
    check(entity_validate(b, kOwner, &r6, &info) == KILN_OK, "epochs: the new epoch's ref validates");
    check(entity_open(b, kOwner, 4, KILN_WORLD_SERVER, 0, 10, &out) == KILN_ECS_ERR_STALE && out.entity == 0,
          "epochs: opening under an older epoch than the owner's is stale");
    check(entity_validate(b, kOther, &r6, &info) == KILN_ECS_ERR_STALE,
          "epochs: another owner (at its own epoch 0) sees the ref as stale");
    KilnEcsQueryRequest q{}; q.size = sizeof q; q.world_kind = KILN_WORLD_SERVER;
    uint32_t comps[1] = {kA}; q.components = comps; q.component_count = 1;
    KilnObjectRef refs[4] = {}; uint32_t state = 0;
    check(ecs_query(b, kOwner, &q, refs, 4, &req, &state) == KILN_OK && req == 2 && refs[0].reload_epoch == 6 &&
              refs[1].reload_epoch == 6,
          "epochs: query refs carry the caller's epoch, not the world's");
    check(entity_validate(b, kOwner, &refs[1], &info) == KILN_OK, "epochs: a query ref validates for its owner");
    check(ecs_query(b, kOther, &q, refs, 4, &req, &state) == KILN_OK && refs[0].reload_epoch == 0,
          "epochs: another owner's query refs carry its own epoch");
}

// snapshot_many sizes the result before it copies: a short buffer copies nothing at all.
void many_sizes_first() {
    Fake F; F.add(10, kA, {1, 2, 3, 4}); F.add(10, kB, {5, 6}); Backend b = backend(F);
    KilnObjectRef r = open_ok(b, 10);
    KilnEcsSnapshotSpec both[2] = {{kA, 0}, {kB, 0}};
    std::vector<uint8_t> buf(8, 0xAA); uint32_t req = 0, presence[2] = {99, 99};
    F.copy_calls = 0;
    check(ecs_snapshot_many(b, kOwner, &r, both, 2, buf.data(), 5, &req, presence) == KILN_ERR_BUFFER_TOO_SMALL &&
              req == 6 && all_eq(buf, 0xAA) && presence[0] == KILN_ECS_OK && presence[1] == KILN_ECS_OK,
          "snapshot_many short buffer: required and presence set, nothing copied");
    check(F.copy_calls == 0, "snapshot_many short buffer: no component was copied before the size check");
    F.copy_calls = 0;
    check(ecs_snapshot_many(b, kOwner, &r, both, 2, buf.data(), 6, &req, presence) == KILN_OK && req == 6 &&
              buf[0] == 1 && buf[4] == 5 && buf[5] == 6 && F.copy_calls > 0,
          "snapshot_many exact buffer: both components packed");
}

// Same entity, same generation and layout, but its row moved during the read: the world changed under the read
// (unreadable, try again), not a different entity (stale).
void moved_row() {
    Fake F; F.add(10, kA, {1, 2, 3, 4}); Backend b = backend(F);
    KilnObjectRef r = open_ok(b, 10);
    std::vector<uint8_t> buf(4, 0xAA); uint32_t req = 0;
    F.component_calls = 0; F.move_on_component = 2;
    check(ecs_snapshot(b, kOwner, &r, kA, buf.data(), 4, &req) == KILN_ECS_ERR_UNREADABLE && all_eq(buf, 0xAA),
          "a row moved mid-read: unreadable, nothing copied");
    F.move_on_component = 0;
    check(ecs_snapshot(b, kOwner, &r, kA, buf.data(), 4, &req) == KILN_OK && buf[0] == 1, "after the move the read succeeds");
}

// A backend with a presence op: queries test presence without asking for content stamps.
void query_presence() {
    Fake F; F.add(10, kA, {1}); F.add(20, kB, {2}); Backend b = owner_backend(F);
    KilnEcsQueryRequest q{}; q.size = sizeof q; q.world_kind = KILN_WORLD_SERVER;
    uint32_t comps[1] = {kA}; q.components = comps; q.component_count = 1;
    KilnObjectRef refs[4] = {}; uint32_t req = 0, state = 0;
    F.component_calls = F.has_calls = 0;
    check(ecs_query(b, kOwner, &q, refs, 4, &req, &state) == KILN_OK && req == 1 && refs[0].entity == 10,
          "query with a presence op");
    check(F.component_calls == 0 && F.has_calls > 0, "the query asked presence only");
}

// Frame-quiet reads: refused while a frame holds the word or when the step moved across the copy (busy, nothing
// copied); unsupported off the server world and on a backend without the window (never a stable copy instead).
void frame_quiet() {
    Fake F; F.add(10, kA, {1, 2, 3, 4}); F.add(20, kA, {5, 6, 7, 8}); F.client_present = true;
    Backend b = quiet_backend(F);
    KilnObjectRef r = open_ok(b, 10);
    KilnEcsSnapshotSpec quiet[1] = {{kA, KILN_ECS_READ_FRAME_QUIET}};
    std::vector<uint8_t> buf(4, 0xAA); uint32_t req = 0, presence[1] = {99};
    check(ecs_snapshot_many(b, kOwner, &r, quiet, 1, buf.data(), 4, &req, presence) == KILN_OK && req == 4 &&
              presence[0] == KILN_ECS_OK && buf[0] == 1 && buf[3] == 4,
          "frame-quiet: between frames the read succeeds");
    F.frame = true; F.copy_calls = 0; buf.assign(4, 0xAA);
    check(ecs_snapshot_many(b, kOwner, &r, quiet, 1, buf.data(), 4, &req, presence) == KILN_ECS_ERR_BUSY &&
              presence[0] == KILN_ECS_BUSY && req == 0 && all_eq(buf, 0xAA) && F.copy_calls == 0,
          "frame-quiet: a frame holding the word refuses busy before any copy");
    KilnEcsSnapshotSpec plain[1] = {{kA, 0}};
    check(ecs_snapshot_many(b, kOwner, &r, plain, 1, buf.data(), 4, &req, presence) == KILN_OK && buf[0] == 1,
          "a stable copy does not look at frames");
    F.frame = false; F.step_on_copy = true; buf.assign(4, 0xAA);
    check(ecs_snapshot_many(b, kOwner, &r, quiet, 1, buf.data(), 4, &req, presence) == KILN_ECS_ERR_BUSY &&
              presence[0] == KILN_ECS_BUSY && all_eq(buf, 0xAA),
          "frame-quiet: the step moved across the copy: busy, nothing copied");
    F.step_on_copy = false;
    KilnObjectRef c{};
    check(entity_open(b, kOwner, 5, KILN_WORLD_CLIENT, 0, 20, &c) == KILN_OK, "frame-quiet: open in the client world");
    presence[0] = 99;
    check(ecs_snapshot_many(b, kOwner, &c, quiet, 1, buf.data(), 4, &req, presence) == KILN_ECS_ERR_UNSUPPORTED &&
              presence[0] == KILN_ECS_UNSUPPORTED && all_eq(buf, 0xAA),
          "frame-quiet on a client world: unsupported, nothing copied");
    check(ecs_snapshot_many(b, kOwner, &c, plain, 1, buf.data(), 4, &req, presence) == KILN_OK && buf[0] == 5,
          "a stable copy in the client world still works");
    buf.assign(4, 0xAA);
    Backend no_window = backend(F);
    check(ecs_snapshot_many(no_window, kOwner, &r, quiet, 1, buf.data(), 4, &req, presence) == KILN_ECS_ERR_UNSUPPORTED &&
              all_eq(buf, 0xAA),
          "frame-quiet without a window: unsupported, never a stable copy instead");
    KilnEcsSnapshotSpec unknown[1] = {{kA, 2}};
    uint32_t kept[1] = {7};
    check(ecs_snapshot_many(b, kOwner, &r, unknown, 1, buf.data(), 4, &req, kept) == KILN_ERR_INVALID_ARGUMENT && kept[0] == 7,
          "an unknown spec flag is an invalid argument");
    // Queries.
    KilnEcsQueryRequest q{}; q.size = sizeof q; q.world_kind = KILN_WORLD_SERVER; q.flags = KILN_ECS_QUERY_FRAME_QUIET;
    uint32_t comps[1] = {kA}; q.components = comps; q.component_count = 1;
    KilnObjectRef refs[4] = {}; uint32_t state = 0;
    check(ecs_query(b, kOwner, &q, refs, 4, &req, &state) == KILN_OK && req == 2 && state == KILN_ECS_OK,
          "frame-quiet query between frames");
    F.frame = true;
    for (auto& o : refs) o.entity = 0xFFFF;
    check(ecs_query(b, kOwner, &q, refs, 4, &req, &state) == KILN_ECS_ERR_BUSY && state == KILN_ECS_BUSY &&
              refs[0].entity == 0xFFFF,
          "frame-quiet query during a frame: busy, nothing copied");
    F.frame = false; F.step_on_copy = false;
    F.hook = [&](int p) { if (p == 3) { std::unique_lock lock(F.mu); ++F.step; } };
    F.phase = 0;
    check(ecs_query(b, kOwner, &q, refs, 4, &req, &state) == KILN_ECS_ERR_BUSY && state == KILN_ECS_BUSY &&
              refs[0].entity == 0xFFFF,
          "frame-quiet query across a step: busy, nothing copied");
    F.hook = nullptr;
    q.world_kind = KILN_WORLD_CLIENT;
    check(ecs_query(b, kOwner, &q, refs, 4, &req, &state) == KILN_ECS_ERR_UNSUPPORTED && state == KILN_ECS_UNSUPPORTED,
          "frame-quiet query on a client world: unsupported");
    q.world_kind = KILN_WORLD_SERVER; q.flags = 1u << 2; state = 7;
    check(ecs_query(b, kOwner, &q, refs, 4, &req, &state) == KILN_ERR_INVALID_ARGUMENT && state == 7,
          "an unknown query flag is an invalid argument");
}
} // namespace

int main() {
    identity(); snapshots(); queries(); concurrent(); epochs(); many_sizes_first(); moved_row(); query_presence(); frame_quiet();
    std::printf("ecs_reads: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
