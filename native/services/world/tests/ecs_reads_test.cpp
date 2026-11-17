// SPDX-License-Identifier: MIT
// T8 (#53) host test: kiln.world's verified-copy ECS reads over a fake engine memory model. The fake is a byte
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

Status ctx_op(void* u, uint32_t kind, ContextFact* f) {
    auto& F = *static_cast<Fake*>(u);
    if (F.context_status != KILN_ECS_OK) return F.context_status;
    if (kind != KILN_WORLD_SERVER) { f->exact_profile = F.exact; return KILN_ECS_OK; }
    f->session = F.session; f->layout_stamp = F.layout_stamp; f->revision = F.revision;
    f->reload_epoch = F.epoch; f->present = F.present; f->exact_profile = F.exact;
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
        if (F.fault_copy) ok = false;
        else if (F.partial_then_fault) { if (n) std::memset(out, 0xEE, 1); ok = false; }
        else {
            std::memcpy(out, reinterpret_cast<const void*>(at), n);
            // The bytes change after the first copy and the generation does not: the two copies must not agree.
            if (F.diverge && ++F.copies == 1 && n) reinterpret_cast<uint8_t*>(at)[0] ^= 0xff;
            else if (F.bump && ++F.copies == 1) ++F.stamp;
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
Backend backend(Fake& F) {
    return Backend{&F, &ctx_op, &ent_op, &comp_op, &copy_op, &ids_op, &pos_op};
}
KilnObjectRef open_ok(Backend& b, uint32_t id) {
    KilnObjectRef r{};
    check(entity_open(b, 5, KILN_WORLD_SERVER, 0, id, &r) == KILN_OK, "open succeeds");
    return r;
}

constexpr uint32_t kA = 100, kB = 200;

void identity() {
    Fake F; F.add(10, kA, {9, 9, 9, 9}); Backend b = backend(F);
    KilnObjectRef r = open_ok(b, 10);
    check(r.entity == 10 && r.generation == 1 && r.session == 7 && r.layout_stamp == 11 && r.reload_epoch == 5, "ref carries id/generation/session/layout/epoch");
    KilnObjectInfo info{}; info.size = sizeof info;
    check(entity_validate(b, &r, &info) == KILN_OK && info.status == KILN_ECS_OK && (info.flags & KILN_OBJECT_ALIVE), "validate ok");
    // Recycled id: same id, new generation is always stale.
    F.ents[10].generation = 2;
    check(entity_validate(b, &r, &info) == KILN_ECS_ERR_STALE && info.status == KILN_ECS_STALE && info.generation == 2 &&
              info.session == 7 && !(info.flags & KILN_OBJECT_ALIVE) && (info.flags & KILN_OBJECT_HAS_TEMPLATE),
          "recycled id is stale and the info names the generation it is now");
    KilnObjectRef r2 = open_ok(b, 10);
    check(r2.generation == 2 && entity_validate(b, &r2, &info) == KILN_OK, "reopened ref validates");
    // Session change and layout change are stale; a save-loaded entity (no template) opens fine.
    F.session = 8;
    check(entity_validate(b, &r2, &info) == KILN_ECS_ERR_STALE && info.session == 8, "session change is stale and the info names the session it is now");
    F.session = 7; F.ents[10].layout = 12;
    check(entity_validate(b, &r2, &info) == KILN_ECS_ERR_STALE, "layout change is stale");
    F.ents[10].layout = 11; F.ents[10].has_template = false;
    check(entity_validate(b, &r2, &info) == KILN_OK && !(info.flags & KILN_OBJECT_HAS_TEMPLATE), "save-loaded entity validates, no template");
    uint8_t guid[16] = {};
    check(ecs_template_uuid(b, &r2, guid) == KILN_ERR_NOT_FOUND, "no template: absent");
    // Absent entity, unavailable context, expected session.
    KilnObjectRef out{};
    check(entity_open(b, 5, KILN_WORLD_SERVER, 0, 999, &out) == KILN_ERR_NOT_FOUND, "open absent entity");
    check(entity_open(b, 5, KILN_WORLD_SERVER, 42, 10, &out) == KILN_ECS_ERR_STALE, "open with wrong expected session is stale");
    check(entity_open(b, 4, KILN_WORLD_SERVER, 0, 10, &out) == KILN_ECS_ERR_STALE && out.entity == 0, "open with another reload epoch is stale and writes no ref");
    F.present = false;
    check(entity_open(b, 5, KILN_WORLD_SERVER, 0, 10, &out) == KILN_ERR_UNAVAILABLE, "no world: unavailable");
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
    check(ecs_snapshot(b, &r, kA, buf.data(), 8, &req) == KILN_OK && req == 4 && buf[0] == 1 && buf[3] == 4, "snapshot copies component bytes");
    check(ecs_snapshot(b, &r, 777, buf.data(), 8, &req) == KILN_ERR_NOT_FOUND, "snapshot absent component");
    buf.assign(8, 0xAA);
    check(ecs_snapshot(b, &r, kA, buf.data(), 2, &req) == KILN_ERR_BUFFER_TOO_SMALL && req == 4 && all_eq(buf, 0xAA), "short buffer: required set, nothing copied");
    // Generation changes between the validations.
    F.phase = 0; F.hook = [&](int p) { if (p == 3) F.ents[10].generation = 9; };
    buf.assign(8, 0xAA);
    check(ecs_snapshot(b, &r, kA, buf.data(), 8, &req) == KILN_ECS_ERR_STALE && all_eq(buf, 0xAA), "generation change mid-read: stale, nothing copied");
    F.hook = nullptr; F.ents[10].generation = 1;
    // Component removed between the validations.
    F.phase = 0; F.hook = [&](int p) { if (p == 4) F.ents[10].comps.erase(kA); };
    buf.assign(8, 0xAA);
    check(ecs_snapshot(b, &r, kA, buf.data(), 8, &req) == KILN_ECS_ERR_STALE && all_eq(buf, 0xAA), "component removed mid-read: stale, nothing copied");
    F.hook = nullptr; F.add(10, kA, {1, 2, 3, 4});
    // Copy faults (even after a partial write into staging): unreadable, caller buffer untouched.
    F.partial_then_fault = true;
    buf.assign(8, 0xAA);
    check(ecs_snapshot(b, &r, kA, buf.data(), 8, &req) == KILN_ECS_ERR_UNREADABLE && all_eq(buf, 0xAA), "faulting copy: unreadable, no partial buffer");
    F.partial_then_fault = false;
    F.diverge = true; F.copies = 0;
    buf.assign(8, 0xAA);
    check(ecs_snapshot(b, &r, kA, buf.data(), 8, &req) == KILN_ECS_ERR_UNREADABLE && all_eq(buf, 0xAA), "bytes changed without a new generation: unreadable, nothing copied");
    F.diverge = false;
    F.add(10, kA, {1, 2, 3, 4});
    F.bump = true; F.copies = 0;
    buf.assign(8, 0xAA);
    check(ecs_snapshot(b, &r, kA, buf.data(), 8, &req) == KILN_ECS_ERR_UNREADABLE && all_eq(buf, 0xAA), "content stamp moved around the copy: unreadable, nothing copied");
    F.bump = false;
    // Template replaced during its read.
    F.phase = 0; F.hook = [&](int p) { if (p == 2) F.ents[10].uuid[0] = 42; };
    uint8_t guid[16] = {};
    check(ecs_template_uuid(b, &r, guid) == KILN_ECS_ERR_STALE, "template replaced mid-read: stale");
    F.hook = nullptr; F.ents[10].uuid[0] = 1;
    check(ecs_template_uuid(b, &r, guid) == KILN_OK && guid[0] == 1 && guid[2] == 3, "template uuid read");
    // Many: presence per component, packed bytes, absent takes no room.
    KilnEcsSnapshotSpec specs[2] = {{kA, 0}, {777, 0}};
    uint32_t presence[2] = {99, 99}; buf.assign(8, 0xAA);
    check(ecs_snapshot_many(b, &r, specs, 2, buf.data(), 8, &req, presence) == KILN_OK && req == 4 &&
              presence[0] == KILN_ECS_OK && presence[1] == KILN_ECS_ABSENT && buf[0] == 1 && buf[3] == 4,
          "snapshot_many: per-component presence, packed bytes");
    KilnEcsSnapshotSpec both[2] = {{kA, 0}, {kB, 0}};
    buf.assign(8, 0xAA);
    check(ecs_snapshot_many(b, &r, both, 2, buf.data(), 3, &req, presence) == KILN_ERR_BUFFER_TOO_SMALL && req == 6 && all_eq(buf, 0xAA), "snapshot_many short buffer: nothing copied");
    uint32_t kept[2] = {7, 7};
    KilnEcsSnapshotSpec bad_spec[1] = {{kA, 1}};
    check(ecs_snapshot_many(b, &r, bad_spec, 1, buf.data(), 8, &req, kept) == KILN_ERR_INVALID_ARGUMENT && kept[0] == 7 && kept[1] == 7,
          "a bad snapshot_many leaves presence untouched");
}

void queries() {
    Fake F; F.add(10, kA, {1}); F.add(10, kB, {2}); F.add(20, kA, {3}); F.ents[20].pos[0] = 100; Backend b = backend(F);
    KilnEcsQueryRequest q{}; q.size = sizeof q; q.world_kind = KILN_WORLD_SERVER;
    uint32_t comps[1] = {kA}; q.components = comps; q.component_count = 1;
    KilnObjectRef out[4] = {}; uint32_t req = 0, state = 99;
    check(ecs_query(b, &q, out, 4, &req, &state) == KILN_OK && req == 2 && state == KILN_ECS_OK && out[0].entity == 10 && out[1].entity == 20, "query AND of one component");
    uint32_t two[2] = {kA, kB}; q.components = two; q.component_count = 2;
    check(ecs_query(b, &q, out, 4, &req, &state) == KILN_OK && req == 1 && out[0].entity == 10, "query AND of two components");
    q.flags = KILN_ECS_QUERY_BOUNDS; q.center = {0, 0, 0}; q.radius = 10;
    check(ecs_query(b, &q, out, 4, &req, &state) == KILN_OK && req == 1 && out[0].entity == 10, "query with spatial bounds");
    q.flags = 0; q.components = comps; q.component_count = 1;
    for (auto& o : out) o.entity = 0xFFFF;
    check(ecs_query(b, &q, out, 1, &req, &state) == KILN_ERR_BUFFER_TOO_SMALL && req == 2 && out[0].entity == 0xFFFF, "query short buffer: required, nothing copied");
    // Membership changes during the walk: no complete result, nothing copied.
    F.phase = 0; F.hook = [&](int p) { if (p == 2) F.revision = 2; };
    for (auto& o : out) o.entity = 0xFFFF;
    check(ecs_query(b, &q, out, 4, &req, &state) == KILN_WORLD_ERR_CHANGED && state == KILN_ECS_UNREADABLE && out[0].entity == 0xFFFF, "membership change during query: changed, nothing copied");
    F.hook = nullptr;
    q.flags = 0; q.components = comps; q.component_count = 1;
    check(ecs_query(b, &q, out, 4, &req, &state) == KILN_OK && req == 2, "query recovers after the membership change");
    KilnObjectInfo info{}; info.size = sizeof info;
    check(entity_validate(b, &out[0], &info) == KILN_OK && info.status == KILN_ECS_OK, "a query ref carries the context epoch and validates");
    F.listings = 0; F.grow = true;
    for (auto& o : out) o.entity = 0xFFFF;
    uint32_t only_b[1] = {kB}; q.components = only_b;
    check(ecs_query(b, &q, out, 4, &req, &state) == KILN_WORLD_ERR_CHANGED && out[0].entity == 0xFFFF, "component gained during the query: changed, nothing copied");
    F.grow = false;
    q.flags = KILN_ECS_QUERY_BOUNDS; q.center = {std::numeric_limits<double>::quiet_NaN(), 0, 0}; q.radius = 1; q.components = comps;
    state = 7;
    check(ecs_query(b, &q, out, 4, &req, &state) == KILN_ERR_INVALID_ARGUMENT && state == 7, "a NaN centre is an argument error and does not write state");
    q.center = {0, 0, 0}; q.radius = 10; q.components = comps; q.component_count = 1;
    F.pos_fault = true;
    for (auto& o : out) o.entity = 0xFFFF;
    check(ecs_query(b, &q, out, 4, &req, &state) == KILN_ECS_ERR_UNREADABLE && state == KILN_ECS_UNREADABLE && out[0].entity == 0xFFFF,
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
            KilnResult rc = ecs_snapshot(b, &r, kA, buf.data(), 64, &req);
            if (rc == KILN_ECS_ERR_STALE) { stale_seen = true; continue; }
            if (rc != KILN_OK) continue;
            if (!all_eq(buf, 1) && !all_eq(buf, 2)) torn = true;
        }
    });
    reader.join(); stop = true; writer.join();
    check(!torn.load(), "concurrent snapshot: never torn data");
    check(stale_seen.load(), "concurrent snapshot: generation flips are reported stale");
}
} // namespace

int main() {
    identity(); snapshots(); queries(); concurrent();
    std::printf("ecs_reads: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
