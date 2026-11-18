// SPDX-License-Identifier: MIT
// Host test: kiln.world's compare-exchange writes (ecs_writes.cpp, ecs_write_hold.cpp, ecs_write_policy.cpp) over the
// real kiln.engine (booted through kiln_plugin_main with a fake core: its write gate, views and guarded copies and
// writes) and a fake engine laid out from each exact profile, with the entity world of ecs_engine_test.cpp in it.
// Every outcome is forced: ok (written, no-op, masked), conflict, busy (a frame holding the Scene word, the busy latch,
// a call on the server thread, a second Kiln holder, a full event queue, an owner retiring mid-call), stale (generation,
// layout, session, epoch, another owner's ref, a retired owner, a generation bump inside the hold), not writable
// (policy rules, a tracking bit set after the policy was cached, the size), uncertain (a rogue write and a failed
// rollback, a fault that hides the bytes; then the latch for the session, a full latch table), unavailable, unreadable
// (a faulting read; a rolled-back write that is proven). Inside the hold: no allocation and no call into the core, a
// bounded id probe. Events after the release, in order, under fault containment, unsubscribe during a drain (also from
// another thread while the callback runs), owner retirement. Racing writers against a simulated frame and a drain under
// ThreadSanitizer. The policy's plain-bytes rule over
// sigdb's reflection of both exact builds, and the hold time of the largest writable component of each.
#include <algorithm>
#include <cstdlib>
#include <new>
#include <sys/mman.h>
#include <unistd.h>

#include "ecs_harness.h"
#include "fake_types.h"
#include "gate.h"

#include "../ecs_lookup.h"
#include "../ecs_writes.h"

std::atomic<long> g_allocations_in_hold{0};
void* operator new(std::size_t n) {
    if (g_in_hold) g_allocations_in_hold.fetch_add(1, std::memory_order_relaxed);
    if (void* p = std::malloc(n ? n : 1)) return p;
    throw std::bad_alloc();
}
void operator delete(void* p) noexcept { std::free(p); }
void operator delete(void* p, std::size_t) noexcept { std::free(p); }

namespace kiln::engine::mem {
extern std::atomic<std::uint64_t> test_writes;
}

namespace {
using namespace kw::ecs;
using namespace kw::ecs::writes;
using namespace fakeworld;
using kiln::engine::Profile;

constexpr KilnOwner kOwner = 21, kOther = 22;

// kiln.internal.engine's copy with failure injection in front of the harness's hook: a read overlapping
// [g_fail_lo, g_fail_hi) fails as a fault does.
std::atomic<uintptr_t> g_fail_lo{0}, g_fail_hi{0};
std::atomic<uint64_t> g_hold_copies{0}, g_hold_copy_bytes{0};  // guarded copies made inside a hold
KilnResult KILN_CALL failing_copy(std::uintptr_t a, void* out, std::size_t n) {
    if (g_in_hold) {
        g_hold_copies.fetch_add(1, std::memory_order_relaxed);
        g_hold_copy_bytes.fetch_add(n, std::memory_order_relaxed);
    }
    if (n && a < g_fail_hi.load() && a + n > g_fail_lo.load()) return KILN_ERR_UNAVAILABLE;
    return hooked_copy(a, out, n);
}
// A profile layout with its write gate switched off, served through layout().
kiln::engine::Layout g_disabled_layout{};
const kiln::engine::Layout* KILN_CALL disabled_layout() { return &g_disabled_layout; }

// Hold points and the in-hold flag.
std::function<void(HoldPoint)> g_point;
void on_hold_point(HoldPoint p) {
    if (p == HoldPoint::Begin) g_in_hold = true;
    if (g_point) {
        const bool was = g_in_hold;
        g_in_hold = false;  // the test's own action is not the hold's
        const auto point = g_point;  // the action may replace g_point
        point(p);
        g_in_hold = was;
    }
}
std::atomic<int> g_releases_begun{0};
void on_release(bool begin) {
    if (begin) {
        g_in_hold = false;
        g_releases_begun.fetch_add(1);
    }
}

struct Rig {
    Reader* reader = nullptr;
    Worlds* worlds = nullptr;
    std::vector<std::string> logs;
    std::mutex log_mu;
};
Reader* rig_reader(void* u) { return static_cast<Rig*>(u)->reader; }
uint64_t rig_step(void* u) { return static_cast<Rig*>(u)->worlds->steps[KILN_WORLD_SERVER].load(); }
void rig_log(void* u, int, const char* text) {
    auto* r = static_cast<Rig*>(u);
    std::lock_guard lock(r->log_mu);
    r->logs.emplace_back(text);
}
bool guarded(void (*fn)(void*), void* c, unsigned long* code) {
    try {
        fn(c);
        return true;
    } catch (...) {
        *code = 0xE06D7363;  // what SEH reports for a C++ exception
        return false;
    }
}

// The types of the world's components: plain ones, one with a dynamic array two levels down, one whose reflected size
// is not its registered size.
void plain_types(faketypes::Types& t, const World& w) {
    t.add("keen::uint8", KILN_TYPE_U8, 1);
    t.add("keen::uint32", KILN_TYPE_U32, 4);
    t.add("keen::uint64", KILN_TYPE_U64, 8);
    t.add("keen::float32", KILN_TYPE_F32, 4);
    t.add("keen::Guid", KILN_TYPE_GUID, 16);
    t.add("keen::Angle", KILN_TYPE_TYPEDEF, 4, "keen::float32");
    t.add("keen::Bytes48", KILN_TYPE_STATIC_ARRAY, 48, "keen::uint8");
    t.add("keen::Bytes56", KILN_TYPE_STATIC_ARRAY, 56, "keen::uint8");
    t.add("keen::DsArray", KILN_TYPE_DS_ARRAY, 16, "keen::uint32");
    t.add("keen::Inner", KILN_TYPE_STRUCT, 16, "", {{"list", "keen::DsArray"}});
    t.add("keen::Outer", KILN_TYPE_STRUCT, 16, "", {{"inner", "keen::Inner"}});
    t.add("keen::BaseOf8", KILN_TYPE_STRUCT, 4, "", {{"a", "keen::uint32"}});
    for (const auto& c : w.comps) {
        if (c.name == "keen::ecs::Counter") t.add(c.name, KILN_TYPE_STRUCT, 8, "", {{"value", "keen::uint64"}});
        else if (c.name == "keen::ecs::Pair") t.add(c.name, KILN_TYPE_STRUCT, 8, "keen::BaseOf8", {{"b", "keen::Angle"}});
        else if (c.name == "keen::ecs::Listy") t.add(c.name, KILN_TYPE_STRUCT, 16, "", {{"o", "keen::Outer"}});
        else if (c.name == "keen::ecs::Resized") t.add(c.name, KILN_TYPE_STRUCT, 12, "", {{"a", "keen::uint64"}});
        else if (c.name == "keen::ecs::Inventory") t.add(c.name, KILN_TYPE_STRUCT, 48, "", {{"b", "keen::Bytes48"}});
        else if (c.name == "keen::ecs::CurrentTransform") t.add(c.name, KILN_TYPE_STRUCT, 56, "", {{"b", "keen::Bytes56"}});
        else if (c.name == "keen::ecs::Health") t.add(c.name, KILN_TYPE_STRUCT, 8, "", {{"a", "keen::uint32"}, {"b", "keen::float32"}});
        else if (c.name == "keen::ecs::Unknowable") continue;  // kiln.types has no type for it
        else t.add(c.name, KILN_TYPE_STRUCT, c.size, "", {});
    }
    t.link();
}

uintptr_t component_address(const World& w, uint32_t id, const std::string& short_name) {
    const uintptr_t rec = w.rec_of.at(id);
    return w.row_address(get<uint64_t>(rec + w.L.entities.entity_layout), get<uint32_t>(rec + w.L.entities.entity_row),
                         "keen::ecs::" + short_name);
}
std::vector<uint8_t> bytes_at(uintptr_t a, size_t n) {
    std::vector<uint8_t> v(n);
    std::memcpy(v.data(), reinterpret_cast<const void*>(a), n);
    return v;
}
uint64_t u64_at(uintptr_t a) { return get<uint64_t>(a); }
void set_tracking_bit(const kiln::engine::Layout& L, uintptr_t registry, uint16_t index, bool a, bool on) {
    const uintptr_t word = registry + (a ? L.write.tracking_registry_a : L.write.tracking_registry_b) + (index / 64) * 8;
    uint64_t v = get<uint64_t>(word);
    v = on ? (v | (uint64_t{1} << (index % 64))) : (v & ~(uint64_t{1} << (index % 64)));
    put<uint64_t>(word, v);
}

struct Call {
    KilnResult rc = KILN_OK;
    KilnMutationReply reply{};
    std::vector<uint8_t> confirmed;
};
Call cx(Writer& wr, KilnOwner owner, const KilnObjectRef& ref, uint32_t hash, const std::vector<uint8_t>& expected,
        const std::vector<uint8_t>& desired, const std::vector<uint8_t>* mask = nullptr) {
    Call c;
    c.reply.size = sizeof c.reply;
    c.confirmed.assign(expected.size(), 0xEE);
    c.rc = wr.compare_exchange(owner, &ref, hash, expected.data(), mask ? mask->data() : nullptr, desired.data(),
                               static_cast<uint32_t>(expected.size()), c.confirmed.data(), &c.reply);
    return c;
}
std::vector<uint8_t> le64(uint64_t v) {
    std::vector<uint8_t> b(8);
    std::memcpy(b.data(), &v, 8);
    return b;
}

struct Seen {
    std::vector<KilnEcsWriteEvent> events;
    bool holder_free_at_every_call = true;
};
void KILN_CALL record(const KilnEcsWriteEvent* e, void* u) {
    auto* s = static_cast<Seen*>(u);
    s->events.push_back(*e);
    if (kiln::engine::gate_holder_taken()) s->holder_free_at_every_call = false;
}

// ---- the policy's pure rules ----
void decide_rules() {
    g_case = "policy rules";
    PolicyFacts f;
    f.world_kind = KILN_WORLD_SERVER;
    f.exact_profile = f.gate_enabled = f.world = f.registered = f.tracking_known = true;
    f.registered_size = f.reflected_size = 65536;
    f.plain = Plain::Yes;
    f.budget = 65536;
    KilnEcsWritePolicy p{};
    p.size = sizeof p;
    check(decide(f, 7, &p) == KILN_OK && p.status == KILN_ECS_OK && (p.flags & KILN_ECS_WRITE_POLICY_WRITABLE) &&
              p.component_size == 65536 && p.budget == 65536 && p.why_code == 0,
          "the budget's edge: 64 KiB is writable");
    f.registered_size = f.reflected_size = 65537;
    check(decide(f, 7, &p) == KILN_OK && p.status == KILN_ECS_NOT_WRITABLE && p.why_code == KILN_ECS_WRITE_WHY_OVER_BUDGET &&
              (p.flags & KILN_ECS_WRITE_POLICY_OVER_BUDGET) && !(p.flags & KILN_ECS_WRITE_POLICY_WRITABLE),
          "64 KiB + 1 is over the budget");
    f.registered_size = f.reflected_size = 8;
    f.world_kind = KILN_WORLD_CLIENT;
    check(decide(f, 7, &p) == KILN_ERR_UNAVAILABLE && p.status == KILN_ECS_UNAVAILABLE &&
              p.why_code == KILN_ECS_WRITE_WHY_NOT_SERVER_WORLD && p.why[0],
          "the client world: unavailable, with a reason");
    f.world_kind = KILN_WORLD_SERVER;
    f.gate_enabled = false;
    check(decide(f, 7, &p) == KILN_ERR_UNAVAILABLE && p.why_code == KILN_ECS_WRITE_WHY_NO_PROFILE, "no gate: unavailable");
    f.gate_enabled = true;
    f.world = false;
    check(decide(f, 7, &p) == KILN_ERR_UNAVAILABLE && p.why_code == KILN_ECS_WRITE_WHY_NO_WORLD, "no world: unavailable");
    f.world = true;
    f.registered = false;
    check(decide(f, 7, &p) == KILN_OK && p.why_code == KILN_ECS_WRITE_WHY_UNREGISTERED, "unregistered");
    f.registered = true;
    f.tracking_known = false;
    check(decide(f, 7, &p) == KILN_OK && p.why_code == KILN_ECS_WRITE_WHY_TRACKING_UNKNOWN &&
              !(p.flags & KILN_ECS_WRITE_POLICY_TRACKING_KNOWN),
          "tracking unknown is not writable");
    f.tracking_known = true;
    f.tracked_b = true;
    check(decide(f, 7, &p) == KILN_OK && p.why_code == KILN_ECS_WRITE_WHY_TRACKED && (p.flags & KILN_ECS_WRITE_POLICY_TRACKED_B),
          "tracked (second category)");
    f.tracked_b = false;
    f.plain = Plain::No;
    f.plain_why = "x";
    check(decide(f, 7, &p) == KILN_OK && p.why_code == KILN_ECS_WRITE_WHY_NOT_PLAIN, "not plain");
    f.plain = Plain::Yes;
    f.reflected_size = 12;
    check(decide(f, 7, &p) == KILN_OK && p.why_code == KILN_ECS_WRITE_WHY_SIZE, "reflected size differs");
}

// ---- one exact profile ----
struct Env {
    const Profile& p;
    fake::Engine& e;
    World& w;
    Worlds& worlds;
    Reader& reader;
    Writer& wr;
    Rig& rig;
    Backend b;
};

KilnObjectRef open(Env& v, KilnOwner owner, uint32_t id) {
    KilnObjectRef r{};
    entity_open(v.b, owner, owner, KILN_WORLD_SERVER, 0, id, &r);
    return r;
}
void next_step(Env& v) { v.worlds.steps[KILN_WORLD_SERVER].fetch_add(1); }

void policy_cases(Env& v) {
    const std::string k = std::string(v.p.id) + ": policy: ";
    KilnEcsWritePolicy p{};
    p.size = sizeof p;
    check(v.wr.policy(KILN_WORLD_SERVER, H(v.w, "Counter"), &p) == KILN_OK && p.status == KILN_ECS_OK &&
              p.flags == (KILN_ECS_WRITE_POLICY_WRITABLE | KILN_ECS_WRITE_POLICY_TRACKING_KNOWN | KILN_ECS_WRITE_POLICY_PLAIN) &&
              p.component_size == 8 && p.budget == 65536,
          k + "Counter (a struct of a uint64) is writable");
    check(v.wr.policy(KILN_WORLD_SERVER, H(v.w, "Pair"), &p) == KILN_OK && p.status == KILN_ECS_OK,
          k + "a base class and a typedef of plain values are plain");
    check(v.wr.policy(KILN_WORLD_SERVER, H(v.w, "Tracked"), &p) == KILN_OK && p.status == KILN_ECS_NOT_WRITABLE &&
              p.why_code == KILN_ECS_WRITE_WHY_TRACKED && (p.flags & KILN_ECS_WRITE_POLICY_TRACKED_A),
          k + "a component with the first tracking bit set in the engine");
    check(v.wr.policy(KILN_WORLD_SERVER, H(v.w, "Listy"), &p) == KILN_OK && p.why_code == KILN_ECS_WRITE_WHY_NOT_PLAIN &&
              std::string(p.why).find("dynamic array") != std::string::npos,
          k + "a dynamic array two levels down is not plain: " + p.why);
    check(v.wr.policy(KILN_WORLD_SERVER, H(v.w, "Resized"), &p) == KILN_OK && p.why_code == KILN_ECS_WRITE_WHY_SIZE,
          k + "a reflected size that is not the registered one");
    check(v.wr.policy(KILN_WORLD_SERVER, H(v.w, "Unknowable"), &p) == KILN_OK && p.why_code == KILN_ECS_WRITE_WHY_NOT_PLAIN,
          k + "a component kiln.types cannot describe");
    check(v.wr.policy(KILN_WORLD_SERVER, 0x12345678u, &p) == KILN_OK && p.why_code == KILN_ECS_WRITE_WHY_UNREGISTERED,
          k + "no component under the hash");
    check(v.wr.policy(KILN_WORLD_CLIENT, H(v.w, "Counter"), &p) == KILN_ERR_UNAVAILABLE &&
              p.why_code == KILN_ECS_WRITE_WHY_NOT_SERVER_WORLD,
          k + "the client world is unavailable");
    check(v.wr.policy(9, H(v.w, "Counter"), &p) == KILN_ERR_INVALID_ARGUMENT, k + "an unknown world kind");
    // The offsets the policy and the hold read are the profile's (both builds name 0x190 and 0x230).
    check(v.p.layout.write.tracking_registry_a == 0x190 && v.p.layout.write.tracking_registry_b == 0x230,
          k + "the tracking bit words at the profile's offsets 0x190 and 0x230");
}

void ok_cases(Env& v) {
    const std::string k = std::string(v.p.id) + ": ";
    const uint32_t counter = H(v.w, "Counter");
    const uintptr_t at = component_address(v.w, 7000, "Counter");
    put<uint64_t>(at, 5);
    Seen seen;
    KilnHandle sub = 0;
    check(v.wr.subscribe(kOther, &record, &seen, &sub) == KILN_OK && sub, k + "subscribe");
    KilnObjectRef r = open(v, kOwner, 7000);
    check(r.reload_epoch == kOwner && r.generation == 2, k + "the counter entity opened with the owner as its epoch");
    const uint64_t writes = kiln::engine::mem::test_writes.load();
    const long allocs = g_allocations_in_hold.load(), core = g_core_calls_in_hold.load();
    Call c = cx(v.wr, kOwner, r, counter, le64(5), le64(6));
    check(c.rc == KILN_OK && c.reply.status == KILN_ECS_OK &&
              c.reply.flags == (KILN_MUTATION_WRITTEN | KILN_MUTATION_CONFIRMED | KILN_MUTATION_EVENT_QUEUED) &&
              u64_at(at) == 6 && c.confirmed == le64(6) && c.reply.hold_ns > 0 && c.reply.session == 41 &&
              c.reply.generation == 2 && c.reply.step == v.worlds.steps[KILN_WORLD_SERVER].load(),
          k + "ok: written, read back, confirmed, an event queued, the hold timed");
    check(kiln::engine::mem::test_writes.load() == writes + 1, k + "ok: one guarded write");
    check(g_allocations_in_hold.load() == allocs && g_core_calls_in_hold.load() == core,
          k + "no allocation and no core call inside the hold");
    check(seen.events.empty(), k + "no event before the drain");
    check(v.wr.drain() == 1 && seen.events.size() == 1 && seen.events[0].entity == 7000 && seen.events[0].component == counter &&
              seen.events[0].owner == kOwner && seen.events[0].session == 41 && seen.events[0].generation == 2 &&
              seen.events[0].world_kind == KILN_WORLD_SERVER && seen.events[0].size == sizeof(KilnEcsWriteEvent) &&
              seen.holder_free_at_every_call,
          k + "the event is delivered once at the drain, after the gate was released");
    check(v.wr.drain() == 0 && seen.events.size() == 1, k + "exactly once");
    // no-op: the merged value equals the current one
    Call n = cx(v.wr, kOwner, r, counter, le64(6), le64(6));
    check(n.rc == KILN_OK && n.reply.flags == (KILN_MUTATION_NO_OP | KILN_MUTATION_CONFIRMED) && n.confirmed == le64(6) &&
              kiln::engine::mem::test_writes.load() == writes + 1 && v.wr.drain() == 0,
          k + "no-op: nothing written, no event");
    const std::vector<uint8_t> none(8, 0);
    Call m0 = cx(v.wr, kOwner, r, counter, le64(6), le64(99), &none);
    check(m0.rc == KILN_OK && (m0.reply.flags & KILN_MUTATION_NO_OP) && u64_at(at) == 6, k + "a mask that selects nothing is a no-op");
    // a mask: the low byte from desired, the rest kept
    std::vector<uint8_t> low(8, 0);
    low[0] = 1;
    Call m = cx(v.wr, kOwner, r, counter, le64(6), le64(0x1122334455667788ull), &low);
    check(m.rc == KILN_OK && (m.reply.flags & KILN_MUTATION_WRITTEN) && u64_at(at) == 0x88 && m.confirmed == le64(0x88),
          k + "masked: only the selected byte changes");
    // conflict at the first, a middle and the last byte
    for (size_t i : {size_t{0}, size_t{3}, size_t{7}}) {
        std::vector<uint8_t> exp = le64(0x88);
        exp[i] ^= 0x40;
        const uint64_t before = kiln::engine::mem::test_writes.load();
        Call c2 = cx(v.wr, kOwner, r, counter, exp, le64(1));
        check(c2.rc == KILN_ECS_ERR_CONFLICT && c2.reply.status == KILN_ECS_CONFLICT &&
                  c2.reply.why_code == KILN_ECS_WRITE_WHY_COMPARE && c2.reply.flags == KILN_MUTATION_CONFIRMED &&
                  c2.confirmed == le64(0x88) && u64_at(at) == 0x88 && kiln::engine::mem::test_writes.load() == before,
              k + "conflict at byte " + std::to_string(i) + ": the current bytes, nothing written");
    }
    check(v.wr.drain() == 1 && seen.events.size() == 2, k + "only the masked write made an event");
    // order: three writes, three events in commit order
    for (uint64_t x = 0x88; x < 0x8b; ++x) cx(v.wr, kOwner, r, counter, le64(x), le64(x + 1));
    check(v.wr.drain() == 3 && seen.events.size() == 5, k + "three writes, three events");
    // a reply without a confirmed buffer
    KilnMutationReply rep{};
    rep.size = sizeof rep;
    const auto e8b = le64(0x8b), d = le64(0x8c);
    check(v.wr.compare_exchange(kOwner, &r, counter, e8b.data(), nullptr, d.data(), 8, nullptr, &rep) == KILN_OK &&
              rep.flags == (KILN_MUTATION_WRITTEN | KILN_MUTATION_EVENT_QUEUED),
          k + "no confirmed buffer: no CONFIRMED flag");
    v.wr.drain();
    // argument errors leave the reply untouched
    KilnMutationReply untouched{};
    untouched.size = sizeof untouched;
    untouched.status = 77;
    check(v.wr.compare_exchange(0, &r, counter, e8b.data(), nullptr, d.data(), 8, nullptr, &untouched) == KILN_ERR_INVALID_ARGUMENT &&
              v.wr.compare_exchange(kOwner, &r, counter, nullptr, nullptr, d.data(), 8, nullptr, &untouched) == KILN_ERR_INVALID_ARGUMENT &&
              v.wr.compare_exchange(kOwner, &r, counter, e8b.data(), nullptr, d.data(), 0, nullptr, &untouched) == KILN_ERR_INVALID_ARGUMENT &&
              untouched.status == 77,
          k + "invalid arguments: the reply untouched");
    Call sz = cx(v.wr, kOwner, r, counter, std::vector<uint8_t>(4, 0), std::vector<uint8_t>(4, 1));
    check(sz.rc == KILN_ECS_ERR_NOT_WRITABLE && sz.reply.why_code == KILN_ECS_WRITE_WHY_SIZE, k + "bytes is not the registered size");
    v.wr.unsubscribe(sub);
    put<uint64_t>(at, 0);
}

void busy_cases(Env& v) {
    const std::string k = std::string(v.p.id) + ": busy: ";
    const uint32_t counter = H(v.w, "Counter");
    const uintptr_t at = component_address(v.w, 7000, "Counter");
    put<uint64_t>(at, 10);
    KilnObjectRef r = open(v, kOwner, 7000);
    next_step(v);
    // a frame: the engine holds the Scene word Shared
    *fake::word(v.e.scene_word()) = 1;
    Call c = cx(v.wr, kOwner, r, counter, le64(10), le64(11));
    check(c.rc == KILN_ECS_ERR_BUSY && c.reply.why_code == KILN_ECS_WRITE_WHY_GATE &&
              c.reply.engine_reason == static_cast<uint32_t>(kiln::engine::Reason::EcsSceneFrameBusy) && u64_at(at) == 10,
          k + "a frame holds the Scene word (" + std::to_string(c.rc) + " why " + std::to_string(c.reply.why_code) +
              " reason " + std::to_string(c.reply.engine_reason) + ")");
    *fake::word(v.e.scene_word()) = 0;
    Call l = cx(v.wr, kOwner, r, counter, le64(10), le64(11));
    check(l.rc == KILN_ECS_ERR_BUSY && l.reply.engine_reason == static_cast<uint32_t>(kiln::engine::Reason::KilnBusyLatched),
          k + "the same owner in the same step is latched");
    KilnObjectRef ro = open(v, kOther, 7000);
    Call o = cx(v.wr, kOther, ro, counter, le64(10), le64(11));
    check(o.rc == KILN_OK && u64_at(at) == 11, k + "another owner in the same step is not");
    next_step(v);
    Call a = cx(v.wr, kOwner, r, counter, le64(11), le64(12));
    check(a.rc == KILN_OK && u64_at(at) == 12, k + "a later step clears the latch");
    // a call on the server thread (a tick handler) is refused before any engine word, and does not latch
    g_on_server_thread = true;
    Call t = cx(v.wr, kOwner, r, counter, le64(12), le64(13));
    g_on_server_thread = false;
    check(t.rc == KILN_ECS_ERR_BUSY && t.reply.why_code == KILN_ECS_WRITE_WHY_IN_FRAME &&
              t.reply.engine_reason == static_cast<uint32_t>(kiln::engine::Reason::InFrame) && u64_at(at) == 12,
          k + "on the server thread: in frame (" + std::to_string(t.rc) + " why " + std::to_string(t.reply.why_code) +
              " reason " + std::to_string(t.reply.engine_reason) + ")");
    Call t2 = cx(v.wr, kOwner, r, counter, le64(12), le64(13));
    check(t2.rc == KILN_OK, k + "in frame does not latch");
    // a second Kiln holder: a write started inside another write's hold
    Call inner;
    g_point = [&](HoldPoint p) {
        if (p != HoldPoint::AfterLookup) return;
        g_point = nullptr;
        inner = cx(v.wr, kOther, ro, counter, le64(13), le64(14));
    };
    Call outer = cx(v.wr, kOwner, r, counter, le64(13), le64(15));
    g_point = nullptr;
    check(inner.rc == KILN_ECS_ERR_BUSY &&
              inner.reply.engine_reason == static_cast<uint32_t>(kiln::engine::Reason::KilnHolderBusy) && outer.rc == KILN_OK &&
              u64_at(at) == 15,
          k + "one Kiln holder at a time");
    // the event queue is full
    v.wr.drain();
    uint64_t reserved = 0;
    while (v.wr.ring().reserve()) ++reserved;
    next_step(v);
    Call f = cx(v.wr, kOwner, r, counter, le64(15), le64(16));
    check(reserved == EventRing::kCells && f.rc == KILN_ECS_ERR_BUSY && f.reply.why_code == KILN_ECS_WRITE_WHY_EVENTS_FULL &&
              u64_at(at) == 15,
          k + "a full event queue");
    for (uint64_t i = 0; i < reserved; ++i) v.wr.ring().unreserve();
    Call f2 = cx(v.wr, kOwner, r, counter, le64(15), le64(16));
    check(f2.rc == KILN_OK, k + "a full queue does not latch");
    // success, conflict and uncertain never latch: the same owner writes again in the same step
    next_step(v);
    put<uint64_t>(at, 16);
    Call s1 = cx(v.wr, kOwner, r, counter, le64(16), le64(17));
    Call s2 = cx(v.wr, kOwner, r, counter, le64(99), le64(18));
    KilnObjectRef pr = open(v, kOwner, 7000);
    KilnObjectRef pair = pr;
    const uintptr_t pat = component_address(v.w, 7000, "Pair");
    put<uint64_t>(pat, 1);
    g_point = [&](HoldPoint p) {
        if (p == HoldPoint::AfterWrite || p == HoldPoint::AfterRollback) put<uint64_t>(pat, 777);
    };
    Call s3 = cx(v.wr, kOwner, pair, H(v.w, "Pair"), le64(1), le64(2));
    g_point = nullptr;
    Call s4 = cx(v.wr, kOwner, r, counter, le64(17), le64(16));
    check(s1.rc == KILN_OK && s2.rc == KILN_ECS_ERR_CONFLICT && s3.rc == KILN_ECS_ERR_UNCERTAIN && s4.rc == KILN_OK &&
              u64_at(at) == 16,
          k + "ok, conflict and uncertain in one step: the same owner is not latched");
    put<uint64_t>(pat, 0);
    // an owner retires during the call: nothing written, busy
    g_point = [&](HoldPoint p) {
        if (p == HoldPoint::AfterCompare) v.wr.owner_retired(999);
    };
    Call rr = cx(v.wr, kOwner, r, counter, le64(16), le64(17));
    g_point = nullptr;
    check(rr.rc == KILN_ECS_ERR_BUSY && rr.reply.why_code == KILN_ECS_WRITE_WHY_RETIRE_RACE && u64_at(at) == 16,
          k + "an owner retired during the hold");
    v.wr.drain();
    put<uint64_t>(at, 0);
}

void stale_cases(Env& v) {
    const std::string k = std::string(v.p.id) + ": stale: ";
    const uint32_t counter = H(v.w, "Counter");
    const uintptr_t at = component_address(v.w, 7000, "Counter");
    const uintptr_t rec = v.w.rec_of.at(7000);
    const auto& E = v.p.layout.entities;
    put<uint64_t>(at, 20);
    next_step(v);
    KilnObjectRef r = open(v, kOwner, 7000);
    put<uint32_t>(rec + E.entity_generation, 3);
    Call g = cx(v.wr, kOwner, r, counter, le64(20), le64(21));
    check(g.rc == KILN_ECS_ERR_STALE && g.reply.why_code == KILN_ECS_WRITE_WHY_REF && u64_at(at) == 20, k + "a new generation");
    put<uint32_t>(rec + E.entity_generation, 2);
    // the layout (archetype) changed
    const uint64_t lay = get<uint64_t>(rec + E.entity_layout);
    put<uint64_t>(rec + E.entity_layout, lay + 1);
    Call l = cx(v.wr, kOwner, r, counter, le64(20), le64(21));
    check(l.rc == KILN_ECS_ERR_STALE && u64_at(at) == 20, k + "another layout");
    put<uint64_t>(rec + E.entity_layout, lay);
    // another owner's ref, an earlier epoch
    KilnObjectRef theirs = open(v, kOther, 7000);
    Call o = cx(v.wr, kOwner, theirs, counter, le64(20), le64(21));
    check(o.rc == KILN_ECS_ERR_STALE && o.reply.why_code == KILN_ECS_WRITE_WHY_NOT_OWNERS_REF && u64_at(at) == 20,
          k + "another owner's ref");
    KilnObjectRef old = r;
    old.reload_epoch = 1;
    Call e = cx(v.wr, kOwner, old, counter, le64(20), le64(21));
    check(e.rc == KILN_ECS_ERR_STALE && u64_at(at) == 20, k + "a ref of another epoch");
    // the session moved on
    v.worlds.session[KILN_WORLD_SERVER] = 44;
    Call s = cx(v.wr, kOwner, r, counter, le64(20), le64(21));
    check(s.rc == KILN_ECS_ERR_STALE && u64_at(at) == 20, k + "a new session");
    v.worlds.session[KILN_WORLD_SERVER] = 41;
    // a generation bump inside the hold, between the compare and the write: nothing written
    g_point = [&](HoldPoint p) {
        if (p == HoldPoint::AfterCompare) put<uint32_t>(rec + E.entity_generation, 3);
    };
    const uint64_t writes = kiln::engine::mem::test_writes.load();
    Call h = cx(v.wr, kOwner, r, counter, le64(20), le64(21));
    g_point = nullptr;
    check(h.rc == KILN_ECS_ERR_STALE && u64_at(at) == 20 && kiln::engine::mem::test_writes.load() == writes,
          k + "a generation bump inside the hold: nothing written");
    put<uint32_t>(rec + E.entity_generation, 2);
    // the same before the hold's first lookup (the id reused for a new entity at the same place, after the ref was
    // checked and before the gate was taken): stale, nothing written
    g_point = [&](HoldPoint p) {
        if (p == HoldPoint::Begin) put<uint32_t>(rec + E.entity_generation, 3);
    };
    Call first = cx(v.wr, kOwner, r, counter, le64(20), le64(21));
    g_point = nullptr;
    put<uint32_t>(rec + E.entity_generation, 2);
    check(first.rc == KILN_ECS_ERR_STALE && first.reply.why_code == KILN_ECS_WRITE_WHY_REF && u64_at(at) == 20 &&
              kiln::engine::mem::test_writes.load() == writes,
          k + "a generation bump before the hold's first lookup: nothing written");
    // an entity that is gone
    v.w.remove(7000);
    Call gone = cx(v.wr, kOwner, r, counter, le64(20), le64(21));
    check(gone.rc == KILN_ERR_NOT_FOUND && gone.reply.status == KILN_ECS_ABSENT, k + "a removed entity is absent");
    v.w.add(7000, 2, lay, 0, nullptr);
    // a retired owner: stale, and its subscriptions end; it cannot subscribe again
    Seen seen;
    KilnHandle h1 = 0;
    v.wr.subscribe(kOwner + 100, &record, &seen, &h1);
    const size_t subs = v.wr.subscriptions();
    v.wr.owner_retired(kOwner + 100);
    KilnObjectRef rr = open(v, kOwner + 100, 7000);
    Call ret = cx(v.wr, kOwner + 100, rr, counter, le64(20), le64(21));
    KilnHandle h2 = 0;
    check(ret.rc == KILN_ECS_ERR_STALE && ret.reply.why_code == KILN_ECS_WRITE_WHY_OWNER_RETIRED && u64_at(at) == 20 &&
              v.wr.subscriptions() == subs - 1 && v.wr.subscribe(kOwner + 100, &record, &seen, &h2) == KILN_ERR_UNAVAILABLE,
          k + "a retired owner: stale, subscriptions ended");
    put<uint64_t>(at, 0);
}

void not_writable_cases(Env& v) {
    const std::string k = std::string(v.p.id) + ": not writable: ";
    const uint32_t counter = H(v.w, "Counter");
    const uintptr_t at = component_address(v.w, 7000, "Counter");
    put<uint64_t>(at, 30);
    next_step(v);
    KilnObjectRef r = open(v, kOwner, 7000);
    KilnEcsWritePolicy p{};
    p.size = sizeof p;
    check(v.wr.policy(KILN_WORLD_SERVER, counter, &p) == KILN_OK && p.status == KILN_ECS_OK, k + "cached writable");
    const uint16_t index = v.w.comp("keen::ecs::Counter").index;
    for (bool a : {true, false}) {
        set_tracking_bit(v.p.layout, v.e.registry_owner(), index, a, true);
        const uint64_t writes = kiln::engine::mem::test_writes.load();
        Call c = cx(v.wr, kOwner, r, counter, le64(30), le64(31));
        check(c.rc == KILN_ECS_ERR_NOT_WRITABLE && c.reply.why_code == KILN_ECS_WRITE_WHY_TRACKED && u64_at(at) == 30 &&
                  kiln::engine::mem::test_writes.load() == writes,
              k + std::string("tracking bit ") + (a ? "A" : "B") + " set after the policy was cached: refused in the hold");
        set_tracking_bit(v.p.layout, v.e.registry_owner(), index, a, false);
    }
    Call ok = cx(v.wr, kOwner, r, counter, le64(30), le64(31));
    check(ok.rc == KILN_OK, k + "bits cleared: writable again");
    KilnObjectRef player = open(v, kOwner, 1);
    Call t = cx(v.wr, kOwner, player, H(v.w, "Listy"), std::vector<uint8_t>(16, 0), std::vector<uint8_t>(16, 1));
    check(t.rc == KILN_ECS_ERR_NOT_WRITABLE && t.reply.why_code == KILN_ECS_WRITE_WHY_NOT_PLAIN, k + "a policy refusal");
    put<uint64_t>(at, 0);
}

void uncertain_cases(Env& v) {
    const std::string k = std::string(v.p.id) + ": ";
    const uint32_t counter = H(v.w, "Counter");
    const uintptr_t at = component_address(v.w, 7000, "Counter");
    put<uint64_t>(at, 40);
    next_step(v);
    v.worlds.session[KILN_WORLD_SERVER] = 43;  // this case's latches stay in a session of their own
    KilnObjectRef r = open(v, kOwner, 7000);
    v.wr.drain();
    Seen seen;
    KilnHandle sub = 0;
    v.wr.subscribe(kOther, &record, &seen, &sub);
    // a rogue write between the write and its readback; the rollback is proven
    g_point = [&](HoldPoint p) {
        if (p == HoldPoint::AfterWrite) put<uint64_t>(at, 777);
    };
    Call rb = cx(v.wr, kOwner, r, counter, le64(40), le64(41));
    g_point = nullptr;
    check(rb.rc == KILN_ECS_ERR_UNREADABLE && rb.reply.flags == KILN_MUTATION_ROLLED_BACK &&
              rb.reply.why_code == KILN_ECS_WRITE_WHY_READBACK && u64_at(at) == 40 && !v.wr.latch().latched({43, 0, 7000, 2, counter}),
          k + "unreadable: a write that did not read back, rolled back and proven, no latch");
    check(v.wr.drain() == 0 && seen.events.empty(), k + "no event for a rolled-back write");
    // the rollback is overwritten too: uncertain, and the target latched
    g_point = [&](HoldPoint p) {
        if (p == HoldPoint::AfterWrite || p == HoldPoint::AfterRollback) put<uint64_t>(at, 777);
    };
    Call u = cx(v.wr, kOwner, r, counter, le64(40), le64(41));
    g_point = nullptr;
    check(u.rc == KILN_ECS_ERR_UNCERTAIN && u.reply.status == KILN_ECS_UNCERTAIN &&
              (u.reply.flags & KILN_MUTATION_TARGET_LATCHED) && v.wr.latch().latched({43, 0, 7000, 2, counter}),
          k + "uncertain: neither the readback nor the rollback proven; latched");
    put<uint64_t>(at, 40);
    const int releases = g_releases_begun.load();
    next_step(v);
    Call again = cx(v.wr, kOwner, r, counter, le64(40), le64(41));
    check(again.rc == KILN_ECS_ERR_UNCERTAIN && again.reply.why_code == KILN_ECS_WRITE_WHY_TARGET_LATCHED &&
              again.reply.flags == KILN_MUTATION_TARGET_LATCHED && again.reply.hold_ns == 0 &&
              g_releases_begun.load() == releases && u64_at(at) == 40,
          k + "a latched target answers uncertain without the gate");
    KilnObjectRef player = open(v, kOwner, 1);
    const uintptr_t hp = component_address(v.w, 1, "Health");
    const auto hb = bytes_at(hp, 8);
    auto hn = hb;
    hn[0] ^= 1;
    Call other = cx(v.wr, kOwner, player, H(v.w, "Health"), hb, hn);
    check(other.rc == KILN_OK && bytes_at(hp, 8) == hn, k + "another target is not latched");
    cx(v.wr, kOwner, player, H(v.w, "Health"), hn, hb);
    // a new session clears it (entries are keyed by session)
    v.worlds.session[KILN_WORLD_SERVER] = 45;
    KilnObjectRef r45 = open(v, kOwner, 7000);
    Call fresh = cx(v.wr, kOwner, r45, counter, le64(40), le64(41));
    check(fresh.rc == KILN_OK && u64_at(at) == 41, k + "a new session: the target writable again");
    // a write fault that hides the bytes: uncertain
    g_point = [&](HoldPoint p) {
        if (p == HoldPoint::AfterWrite) { g_fail_lo = at; g_fail_hi = at + 8; }
    };
    Call hid = cx(v.wr, kOwner, r45, counter, le64(41), le64(42));
    g_point = nullptr;
    g_fail_lo = g_fail_hi = 0;
    check(hid.rc == KILN_ECS_ERR_UNCERTAIN && v.wr.latch().latched({45, 0, 7000, 2, counter}),
          k + "a readback that faults: uncertain");
    // the entity changes under the hold after a write that read back: uncertain, and no rollback to an address that
    // may no longer be the entity's
    v.worlds.session[KILN_WORLD_SERVER] = 46;
    KilnObjectRef r46 = open(v, kOwner, 7000);
    put<uint64_t>(at, 60);
    const uintptr_t rec = v.w.rec_of.at(7000);
    g_point = [&](HoldPoint p) {
        if (p == HoldPoint::AfterWrite) put<uint32_t>(rec + v.p.layout.entities.entity_generation, 9);
    };
    Call moved = cx(v.wr, kOwner, r46, counter, le64(60), le64(61));
    g_point = nullptr;
    put<uint32_t>(rec + v.p.layout.entities.entity_generation, 2);
    check(moved.rc == KILN_ECS_ERR_UNCERTAIN && moved.reply.why_code == KILN_ECS_WRITE_WHY_READBACK && u64_at(at) == 61 &&
              !(moved.reply.flags & KILN_MUTATION_ROLLED_BACK) && v.wr.latch().latched({46, 0, 7000, 2, counter}),
          k + "the entity changed after a proven write: uncertain, latched, not rolled back");
    // the gate stops validating after a write that read back (the GameServer thread told to stop): uncertain, latched
    v.worlds.session[KILN_WORLD_SERVER] = 47;
    KilnObjectRef r47 = open(v, kOwner, 7000);
    const uintptr_t stop = v.e.thread + v.p.layout.write.thread_stop;
    g_point = [&](HoldPoint p) {
        if (p == HoldPoint::AfterWrite) put<uint32_t>(stop, 1);
    };
    Call lost = cx(v.wr, kOwner, r47, counter, le64(61), le64(62));
    g_point = nullptr;
    put<uint32_t>(stop, 0);
    check(lost.rc == KILN_ECS_ERR_UNCERTAIN && lost.reply.why_code == KILN_ECS_WRITE_WHY_READBACK && u64_at(at) == 62 &&
              !(lost.reply.flags & KILN_MUTATION_ROLLED_BACK) && v.wr.latch().latched({47, 0, 7000, 2, counter}),
          k + "the gate stopped validating after a proven write: uncertain, latched, not rolled back");
    v.worlds.session[KILN_WORLD_SERVER] = 41;
    v.wr.drain();
    v.wr.unsubscribe(sub);
    put<uint64_t>(at, 0);
}

// A component across a page boundary: the second page read-only, so the write faults part-way.
void fault_cases(Env& v) {
    const std::string k = std::string(v.p.id) + ": faults: ";
    const uint32_t big = H(v.w, "Span");
    const uintptr_t at = component_address(v.w, 7001, "Span");
    const uintptr_t page = (at + 4095) & ~uintptr_t{4095};
    check(page > at && page < at + 256, k + "the Span component crosses a page boundary");
    std::vector<uint8_t> cur(256);
    for (size_t i = 0; i < cur.size(); ++i) cur[i] = static_cast<uint8_t>(i);
    std::memcpy(reinterpret_cast<void*>(at), cur.data(), cur.size());
    next_step(v);
    KilnObjectRef r = open(v, kOwner, 7001);
    auto want = cur;
    for (auto& b : want) b ^= 0xff;
    mprotect(reinterpret_cast<void*>(page), 4096, PROT_READ);
    Call c = cx(v.wr, kOwner, r, big, cur, want);
    check(c.rc == KILN_ECS_ERR_UNREADABLE && c.reply.flags == KILN_MUTATION_ROLLED_BACK && bytes_at(at, 256) == cur,
          k + "a write that faults part-way is rolled back and proven");
    // the first read faults: nothing written
    g_fail_lo = page;
    g_fail_hi = page + 1;
    const uint64_t writes = kiln::engine::mem::test_writes.load();
    Call f = cx(v.wr, kOwner, r, big, cur, want);
    g_fail_lo = g_fail_hi = 0;
    check(f.rc == KILN_ECS_ERR_UNREADABLE && f.reply.why_code == KILN_ECS_WRITE_WHY_FAULT && f.reply.flags == 0 &&
              kiln::engine::mem::test_writes.load() == writes,
          k + "a read that faults: unreadable, nothing written");
    // the write faults and the page then becomes unreadable: uncertain
    g_point = [&](HoldPoint p) {
        if (p == HoldPoint::AfterWrite) mprotect(reinterpret_cast<void*>(page), 4096, PROT_NONE);
    };
    Call u = cx(v.wr, kOwner, r, big, cur, want);
    g_point = nullptr;
    mprotect(reinterpret_cast<void*>(page), 4096, PROT_READ | PROT_WRITE);
    check(u.rc == KILN_ECS_ERR_UNCERTAIN, k + "a write fault whose rollback cannot be read back: uncertain");
}

void unavailable_cases(Env& v) {
    const std::string k = std::string(v.p.id) + ": unavailable: ";
    const uint32_t counter = H(v.w, "Counter");
    const uintptr_t at = component_address(v.w, 7000, "Counter");
    put<uint64_t>(at, 50);
    next_step(v);
    KilnObjectRef r = open(v, kOwner, 7000);
    KilnObjectRef client = r;
    client.world_kind = KILN_WORLD_CLIENT;
    Call c = cx(v.wr, kOwner, client, counter, le64(50), le64(51));
    check(c.rc == KILN_ERR_UNAVAILABLE && c.reply.why_code == KILN_ECS_WRITE_WHY_NOT_SERVER_WORLD, k + "the client world");
    kiln::engine::gate_close_session(true, 0);
    Call s = cx(v.wr, kOwner, r, counter, le64(50), le64(51));
    kiln::engine::gate_close_session(false, 0);
    check(s.rc == KILN_ERR_UNAVAILABLE && s.reply.engine_reason == static_cast<uint32_t>(kiln::engine::Reason::KilnGateClosed) &&
              u64_at(at) == 50,
          k + "the server world is stopping");
    // the gate stops validating after it was taken (the GameServer thread is stopping): nothing written
    const uintptr_t stop = v.e.thread + v.p.layout.write.thread_stop;
    g_point = [&](HoldPoint p) {
        if (p == HoldPoint::Begin) put<uint32_t>(stop, 1);
    };
    const uint64_t writes = kiln::engine::mem::test_writes.load();
    Call iv = cx(v.wr, kOwner, r, counter, le64(50), le64(51));
    g_point = nullptr;
    put<uint32_t>(stop, 0);
    check(iv.rc == KILN_ERR_UNAVAILABLE && iv.reply.why_code == KILN_ECS_WRITE_WHY_GATE && u64_at(at) == 50 &&
              kiln::engine::mem::test_writes.load() == writes,
          k + "the gate no longer validates inside the hold: nothing written");
    const uintptr_t world = v.worlds.world[KILN_WORLD_SERVER];
    v.worlds.world[KILN_WORLD_SERVER] = 0;
    Call n = cx(v.wr, kOwner, r, counter, le64(50), le64(51));
    v.worlds.world[KILN_WORLD_SERVER] = world;
    check(n.rc == KILN_ERR_UNAVAILABLE && n.reply.why_code == KILN_ECS_WRITE_WHY_NO_WORLD, k + "no server world");
    // a profile whose write gate is off
    g_disabled_layout = v.p.layout;
    g_disabled_layout.write.enabled = false;
    KilnEngineNativeApi off = *v.rig.reader->source().engine;
    off.layout = &disabled_layout;
    WriterSource ws;
    ws.reader = &rig_reader;
    ws.engine = &off;
    ws.exact_profile = true;
    ws.user = &v.rig;
    ws.server_step = &rig_step;
    Writer w2(ws);
    Call d = cx(w2, kOwner, r, counter, le64(50), le64(51));
    KilnEcsWritePolicy p{};
    p.size = sizeof p;
    check(!w2.available() && d.rc == KILN_ERR_UNAVAILABLE && d.reply.why_code == KILN_ECS_WRITE_WHY_NO_PROFILE &&
              w2.policy(KILN_WORLD_SERVER, counter, &p) == KILN_ERR_UNAVAILABLE && u64_at(at) == 50,
          k + "a profile without a write gate");
    ws.engine = v.rig.reader->source().engine;
    ws.exact_profile = false;
    Writer w3(ws);
    check(!w3.available() && cx(w3, kOwner, r, counter, le64(50), le64(51)).rc == KILN_ERR_UNAVAILABLE,
          k + "no exact profile");
    // no kiln.types (the policy's reflection): unavailable, as the world.ecs_write row says, not "not writable"
    ws.exact_profile = true;
    ws.types = nullptr;
    Writer w4(ws);
    Call nt = cx(w4, kOwner, r, counter, le64(50), le64(51));
    check(!w4.available() && nt.rc == KILN_ERR_UNAVAILABLE && nt.reply.why_code == KILN_ECS_WRITE_WHY_NO_PROFILE &&
              u64_at(at) == 50,
          k + "no kiln.types (" + std::to_string(nt.rc) + ")");
    put<uint64_t>(at, 0);
}

// The id probe inside the hold has a fixed bound: an id further along than the bound is unreadable there.
void bounded_probe(Env& v) {
    const std::string k = std::string(v.p.id) + ": ";
    std::vector<uint32_t> same;
    for (uint32_t id = 5000; same.size() < 3; ++id)
        if ((id_hash(id) & (v.w.cap - 1)) == (id_hash(5000) & (v.w.cap - 1))) same.push_back(id);
    next_step(v);
    KilnObjectRef r = open(v, kOwner, same[2]);  // the third of three colliding ids: its probe visits 3 slots
    const uint32_t ct = H(v.w, "CurrentTransform");
    const auto cur = bytes_at(component_address(v.w, same[2], "CurrentTransform"), 56);
    auto want = cur;
    want[55] ^= 1;
    v.wr.probe_bound = 2;
    const uint64_t writes = kiln::engine::mem::test_writes.load();
    Call c = cx(v.wr, kOwner, r, ct, cur, want);
    check(c.rc == KILN_ECS_ERR_UNREADABLE && kiln::engine::mem::test_writes.load() == writes,
          k + "a probe beyond the hold's bound is unreadable, nothing written");
    v.wr.probe_bound = kw::ecs::engine::kMaxProbeForWrites;
    Call ok = cx(v.wr, kOwner, r, ct, cur, want);
    check(ok.rc == KILN_OK, k + "within the bound it writes");
    cx(v.wr, kOwner, r, ct, want, cur);
    check(kw::ecs::engine::kMaxProbeForWrites == 8192, k + "the hold's probe bound is 8192 slots");
    v.wr.drain();
}

// Events: unsubscribe during a drain, a subscriber that faults, owners retiring.
struct Chain {
    Writer* wr = nullptr;
    KilnHandle other = 0, self = 0;
    int calls = 0;
    bool throws = false;
};
void KILN_CALL unsub_other(const KilnEcsWriteEvent*, void* u) {
    auto* c = static_cast<Chain*>(u);
    ++c->calls;
    if (c->other) c->wr->unsubscribe(c->other);
    c->other = 0;
}
struct Slow {
    std::atomic<int> calls{0};
    std::atomic<bool> inside{false}, go{false};
};
void KILN_CALL slow_cb(const KilnEcsWriteEvent*, void* u) {
    auto* s = static_cast<Slow*>(u);
    s->calls.fetch_add(1);
    s->inside.store(true);
    while (!s->go.load()) std::this_thread::yield();
    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    s->inside.store(false);
}
void KILN_CALL counting(const KilnEcsWriteEvent*, void* u) {
    auto* c = static_cast<Chain*>(u);
    ++c->calls;
    if (c->throws) throw std::runtime_error("subscriber fault");
    if (c->self) {
        c->wr->unsubscribe(c->self);
        c->self = 0;
    }
}
void event_cases(Env& v) {
    const std::string k = std::string(v.p.id) + ": events: ";
    const uint32_t counter = H(v.w, "Counter");
    const uintptr_t at = component_address(v.w, 7000, "Counter");
    put<uint64_t>(at, 0);
    next_step(v);
    KilnObjectRef r = open(v, kOwner, 7000);
    Chain a, b, self, bad, after;
    a.wr = b.wr = self.wr = bad.wr = after.wr = &v.wr;
    KilnHandle ha = 0, hb = 0, hs = 0, hbad = 0, hafter = 0;
    v.wr.subscribe(kOwner, &unsub_other, &a, &ha);
    v.wr.subscribe(kOwner, &counting, &b, &hb);
    v.wr.subscribe(kOwner, &counting, &self, &hs);
    v.wr.subscribe(kOther, &counting, &bad, &hbad);
    v.wr.subscribe(kOther + 1, &counting, &after, &hafter);
    a.other = hb;
    self.self = hs;
    bad.throws = true;
    for (uint64_t x = 0; x < 3; ++x) cx(v.wr, kOwner, r, counter, le64(x), le64(x + 1));
    const size_t n = v.wr.drain();
    check(n == 3 && a.calls == 3 && b.calls == 0 && self.calls == 1 && bad.calls == 1 && after.calls == 3,
          k + "unsubscribed during the drain (another's and its own): no later event; a faulting one ended, the rest go on (" +
              std::to_string(a.calls) + "," + std::to_string(b.calls) + "," + std::to_string(self.calls) + "," +
              std::to_string(bad.calls) + "," + std::to_string(after.calls) + ")");
    bool logged = false;
    {
        std::lock_guard lock(v.rig.log_mu);
        for (const auto& l : v.rig.logs) logged = logged || l.find("faulted") != std::string::npos;
    }
    check(v.wr.faults() >= 1 && logged && v.wr.unsubscribe(hbad) == KILN_ERR_NOT_FOUND,
          k + "the fault is logged and the subscription is gone");
    check(v.wr.unsubscribe(hb) == KILN_ERR_NOT_FOUND && v.wr.unsubscribe(hs) == KILN_ERR_NOT_FOUND, k + "unsubscribed handles are gone");
    // an owner's subscriptions end at its retirement
    v.wr.owner_retired(kOther + 1);
    cx(v.wr, kOwner, r, counter, le64(3), le64(4));
    v.wr.drain();
    check(after.calls == 3 && a.calls == 4 && v.wr.unsubscribe(hafter) == KILN_ERR_NOT_FOUND,
          k + "a retired owner's subscription gets no later event");
    v.wr.unsubscribe(ha);
    // unsubscribing from another thread while the callback runs on the draining one: it returns after the callback did
    Slow slow;
    KilnHandle hslow = 0;
    v.wr.subscribe(kOwner, &slow_cb, &slow, &hslow);
    cx(v.wr, kOwner, r, counter, le64(4), le64(5));
    cx(v.wr, kOwner, r, counter, le64(5), le64(6));
    std::thread drainer([&] { v.wr.drain(); });
    const auto give_up = std::chrono::steady_clock::now() + std::chrono::seconds(10);
    while (!slow.inside.load() && std::chrono::steady_clock::now() < give_up) std::this_thread::yield();
    slow.go.store(true);
    const KilnResult unsub = v.wr.unsubscribe(hslow);
    const bool still_inside = slow.inside.load();
    drainer.join();
    check(unsub == KILN_OK && !still_inside && slow.calls.load() == 1,
          k + "unsubscribed from another thread during its callback: returns after it, no later event (" +
              std::to_string(slow.calls.load()) + " calls" + (still_inside ? ", still inside" : "") + ")");
    // a write whose event is claimed but not yet published holds back the later ones (order kept)
    EventRing ring;
    KilnEcsWriteEvent ev{};
    KilnEcsWriteEvent out[4];
    ring.reserve();
    ring.reserve();
    const uint64_t p0 = ring.claim(), p1 = ring.claim();
    ev.entity = 2;
    ring.publish(p1, ev);
    check(ring.take(out, 4) == 0, k + "the later event waits for the earlier one");
    ev.entity = 1;
    ring.publish(p0, ev);
    check(ring.take(out, 4) == 2 && out[0].entity == 1 && out[1].entity == 2 && ring.in_use() == 0, k + "then both, in order");
    // the latch table fills: the whole session fails closed
    UncertainLatch latch;
    bool all = true;
    for (uint32_t i = 0; i < UncertainLatch::kSlots; ++i) all = latch.latch({9, 0, 100 + i, 1, 7}) && all;
    check(all && latch.used(9) == UncertainLatch::kSlots && !latch.latched({9, 0, 99, 1, 7}), k + "512 targets latched");
    check(!latch.latch({9, 0, 99, 1, 7}) && latch.widened_session() == 9 && latch.latched({9, 0, 12345, 1, 8}) &&
              !latch.latched({10, 0, 12345, 1, 8}),
          k + "the 513th widens the latch to its whole session, not the next");
    check(!latch.latched({10, 0, 100, 1, 7}) && !latch.latched({10, 0, 100 + UncertainLatch::kSlots - 1, 1, 7}),
          k + "a target latched in an earlier session is not latched in the next (every slot holds the old session)");
    check(latch.latch({10, 0, 1, 1, 7}) && latch.latched({10, 0, 1, 1, 7}), k + "a new session reuses the old slots");
    put<uint64_t>(at, 0);
}

// kiln.world's boot writer answers until on_ready publishes the real one: a subscription made before then, and an
// owner retired before then, carry over to it (Writer::adopt), so an early subscriber misses no event.
void adopt_cases(Env& v, const WriterSource& ready) {
    const std::string k = std::string(v.p.id) + ": adopt: ";
    WriterSource early;  // as ecs_prod.cpp's boot writer: the reader, nothing of the engine
    early.reader = ready.reader;
    early.user = ready.user;
    early.server_step = ready.server_step;
    early.guarded = ready.guarded;
    early.log = ready.log;
    Writer boot(early);
    Seen first, later;
    KilnHandle h1 = 0, h2 = 0, h3 = 0;
    check(!boot.available() && boot.subscribe(kOther, &record, &first, &h1) == KILN_OK && h1 != 0,
          k + "the boot writer takes a subscription before kiln.world is ready");
    boot.owner_retired(kOwner + 200);
    Writer real(ready);
    real.adopt(boot);
    check(real.subscriptions() == 1 && boot.subscriptions() == 1, k + "the subscription is the real writer's now");
    check(boot.subscribe(kOther, &record, &later, &h2) == KILN_OK && h2 != h1 && real.subscriptions() == 2,
          k + "a subscribe on the boot writer after on_ready lands in the real one");
    check(real.subscribe(kOwner + 200, &record, &later, &h3) == KILN_ERR_UNAVAILABLE, k + "an owner retired before on_ready stays retired");
    // a write through the real writer reaches the early subscriber
    const uint32_t counter = H(v.w, "Counter");
    const uintptr_t at = component_address(v.w, 7000, "Counter");
    put<uint64_t>(at, 70);
    next_step(v);
    KilnObjectRef r = open(v, kOwner, 7000);
    Call c = cx(real, kOwner, r, counter, le64(70), le64(71));
    check(c.rc == KILN_OK && real.drain() == 1 && first.events.size() == 1 && later.events.size() == 1 &&
              first.events[0].entity == 7000,
          k + "a write after on_ready reaches the subscriber that came before it");
    check(boot.unsubscribe(h1) == KILN_OK && real.subscriptions() == 1 && boot.unsubscribe(h1) == KILN_ERR_NOT_FOUND,
          k + "the early handle still unsubscribes, through either writer");
    // a retirement that reaches the boot writer after on_ready (one that loaded it before the switch) ends the owner's
    // subscriptions in the real one, and the owner stays retired there
    KilnHandle h4 = 0;
    check(boot.subscribe(kOther + 1, &record, &later, &h4) == KILN_OK, k + "another early-style subscription");
    boot.owner_retired(kOther + 1);
    KilnHandle h5 = 0;
    check(real.unsubscribe(h4) == KILN_ERR_NOT_FOUND && real.subscribe(kOther + 1, &record, &later, &h5) == KILN_ERR_UNAVAILABLE,
          k + "a retirement through the boot writer after on_ready ends the subscription in the real one");
    real.unsubscribe(h2);
    // #79's rule across the switch: an unsubscribe through the boot writer, from another thread, while the adopted
    // subscription's callback runs on the real writer's draining thread, returns after the callback
    {
        WriterSource early2 = early;
        Writer boot2(early2);
        Slow slow;
        KilnHandle hs = 0;
        check(boot2.subscribe(kOther, &slow_cb, &slow, &hs) == KILN_OK, k + "a slow subscriber before on_ready");
        Writer real2(ready);
        real2.adopt(boot2);
        put<uint64_t>(at, 71);
        next_step(v);
        KilnObjectRef r2 = open(v, kOwner, 7000);
        Call c2 = cx(real2, kOwner, r2, counter, le64(71), le64(72));
        Call c3 = cx(real2, kOwner, r2, counter, le64(72), le64(73));
        std::thread drainer([&] { real2.drain(); });
        const auto give_up = std::chrono::steady_clock::now() + std::chrono::seconds(10);
        while (!slow.inside.load() && std::chrono::steady_clock::now() < give_up) std::this_thread::yield();
        slow.go.store(true);
        const KilnResult unsub = boot2.unsubscribe(hs);
        const bool still_inside = slow.inside.load();
        drainer.join();
        check(c2.rc == KILN_OK && c3.rc == KILN_OK && unsub == KILN_OK && !still_inside && slow.calls.load() == 1,
              k + "unsubscribed through the boot writer during the callback: returns after it, no later event (" +
                  std::to_string(slow.calls.load()) + " calls" + (still_inside ? ", still inside" : "") + ")");
    }
    put<uint64_t>(at, 0);
}

// Subscribes, unsubscribes and retirements on the boot writer racing on_ready's adoption (under ThreadSanitizer): every
// handle answered is valid afterwards through either writer, exactly once, none is lost or duplicated, and a write after
// the switch reaches each survivor once.
void adopt_race(Env& v, const WriterSource& ready) {
    const std::string k = std::string(v.p.id) + ": adopt race: ";
    WriterSource early;
    early.reader = ready.reader;
    early.user = ready.user;
    early.server_step = ready.server_step;
    early.guarded = ready.guarded;
    early.log = ready.log;
    constexpr int kThreads = 4, kEach = 200;
    bool all_ok = true, handles_unique = true, once = true, delivered_once = true;
    size_t survivors_total = 0;
    for (int round = 0; round < 5; ++round) {
        Writer boot(early);
        Writer real(ready);
        std::atomic<bool> start{false};
        std::vector<std::vector<KilnHandle>> kept(kThreads);
        std::vector<std::vector<KilnHandle>> dropped(kThreads);
        std::vector<std::atomic<int>> calls(kThreads);
        std::atomic<bool> failed{false};
        auto counter_cb = [](const KilnEcsWriteEvent*, void* u) { static_cast<std::atomic<int>*>(u)->fetch_add(1); };
        std::vector<std::thread> threads;
        for (int t = 0; t < kThreads; ++t) {
            threads.emplace_back([&, t] {
                while (!start.load()) std::this_thread::yield();
                const KilnOwner retiring = kOther + 1000 + static_cast<KilnOwner>(t);
                for (int i = 0; i < kEach; ++i) {
                    KilnHandle h = 0;
                    // the survivors' owner: one per thread; the retiring owner's subscriptions end at its retirement
                    const bool keep = i % 3 != 2;
                    const KilnOwner owner = keep ? kOther + static_cast<KilnOwner>(t) : retiring;
                    const KilnResult rc = boot.subscribe(owner, +counter_cb, &calls[t], &h);
                    if (!keep) continue;
                    if (rc != KILN_OK || h == 0) {
                        failed.store(true);
                        continue;
                    }
                    if (i % 3 == 1) {
                        if (boot.unsubscribe(h) != KILN_OK) failed.store(true);
                        dropped[t].push_back(h);
                    } else {
                        kept[t].push_back(h);
                    }
                }
                boot.owner_retired(retiring);
            });
        }
        start.store(true);
        std::this_thread::sleep_for(std::chrono::microseconds(50 * round));
        real.adopt(boot);
        for (auto& th : threads) th.join();
        all_ok = all_ok && !failed.load();
        std::vector<KilnHandle> every;
        size_t survivors = 0;
        for (int t = 0; t < kThreads; ++t) {
            survivors += kept[t].size();
            every.insert(every.end(), kept[t].begin(), kept[t].end());
            every.insert(every.end(), dropped[t].begin(), dropped[t].end());
        }
        std::sort(every.begin(), every.end());
        handles_unique = handles_unique && std::adjacent_find(every.begin(), every.end()) == every.end();
        survivors_total += survivors;
        all_ok = all_ok && real.subscriptions() == survivors && boot.subscriptions() == survivors;
        // a write after the switch reaches every survivor once (each thread's survivors count on its own counter)
        const uint32_t counter = H(v.w, "Counter");
        const uintptr_t at = component_address(v.w, 7000, "Counter");
        put<uint64_t>(at, 80);
        next_step(v);
        KilnObjectRef r = open(v, kOwner, 7000);
        Call c = cx(real, kOwner, r, counter, le64(80), le64(81));
        all_ok = all_ok && c.rc == KILN_OK && real.drain() == 1;
        for (int t = 0; t < kThreads; ++t)
            delivered_once = delivered_once && calls[t].load() == static_cast<int>(kept[t].size());
        put<uint64_t>(at, 0);
        // each survivor unsubscribes exactly once, through either writer; the dropped ones are gone
        for (int t = 0; t < kThreads; ++t) {
            for (size_t i = 0; i < kept[t].size(); ++i) {
                Writer& through = i % 2 ? boot : real;
                once = once && through.unsubscribe(kept[t][i]) == KILN_OK && real.unsubscribe(kept[t][i]) == KILN_ERR_NOT_FOUND;
            }
            for (KilnHandle h : dropped[t]) once = once && boot.unsubscribe(h) == KILN_ERR_NOT_FOUND;
        }
        all_ok = all_ok && real.subscriptions() == 0;
        for (int t = 0; t < kThreads; ++t) {
            KilnHandle h = 0;
            all_ok = all_ok && real.subscribe(kOther + 1000 + static_cast<KilnOwner>(t), +counter_cb, &calls[t], &h) == KILN_ERR_UNAVAILABLE;
        }
    }
    check(all_ok, k + "every subscribe and unsubscribe answered as if there were one writer, the retired owners stay retired");
    check(handles_unique, k + "no handle is answered twice across the switch");
    check(delivered_once && survivors_total > 0, k + "a write after the switch reaches each surviving subscriber once");
    check(once, k + "each survivor unsubscribes exactly once, through either writer");
}

// Racing writers (each its own owner) incrementing one counter by compare-exchange while a simulated frame holds the
// Scene word Shared and reads the counter, and a tick bumps the step. Under ThreadSanitizer.
void race(Env& v) {
    const std::string k = std::string(v.p.id) + ": race: ";
    const uint32_t counter = H(v.w, "Counter");
    const uintptr_t at = component_address(v.w, 7000, "Counter");
    put<uint64_t>(at, 0);
    std::atomic<bool> stop{false};
    std::atomic<long> frames{0}, torn{0};
    std::thread frame([&] {
        auto* word = fake::word(v.e.scene_word());
        while (!stop.load()) {
            std::int32_t free = 0;
            if (!__atomic_compare_exchange_n(word, &free, 1, false, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST)) continue;
            uint8_t b[8];
            std::memcpy(b, reinterpret_cast<const void*>(at), 8);  // the engine's own plain read inside its frame
            uint64_t x = 0;
            std::memcpy(&x, b, 8);
            if (x > 1000000) ++torn;
            ++frames;
            __atomic_fetch_sub(word, 1, __ATOMIC_SEQ_CST);
        }
    });
    std::thread tick([&] {
        while (!stop.load()) {
            v.worlds.steps[KILN_WORLD_SERVER].fetch_add(1);
            std::this_thread::sleep_for(std::chrono::microseconds(50));
        }
    });
    // The drain runs while the writers publish (the ring's consumer against its producers): every ok gets exactly one
    // event.
    struct Delivered {
        std::atomic<long> events{0}, wrong{0};
    } delivered;
    KilnHandle drain_sub = 0;
    v.wr.subscribe(kOther, [](const KilnEcsWriteEvent* e, void* u) {
        auto* d = static_cast<Delivered*>(u);
        d->events.fetch_add(1);
        if (e->entity != 7000 || e->size != sizeof(KilnEcsWriteEvent) || e->session != 41) d->wrong.fetch_add(1);
    }, &delivered, &drain_sub);
    std::thread drainer([&] {
        while (!stop.load()) v.wr.drain();
    });
    constexpr int kThreads = 4, kEach = 150;
    std::atomic<long> oks{0}, conflicts{0}, busy{0}, bad{0}, impossible{0};
    std::vector<std::thread> writers;
    for (int t = 0; t < kThreads; ++t) {
        writers.emplace_back([&, t] {
            const KilnOwner owner = 300 + t;
            KilnObjectRef r = open(v, owner, 7000);
            uint64_t guess = 0;
            int done = 0;
            for (int attempts = 0; done < kEach && attempts < 200000; ++attempts) {
                Call c = cx(v.wr, owner, r, counter, le64(guess), le64(guess + 1));
                if (c.rc == KILN_OK) { ++oks; ++done; guess += 1; continue; }
                if (c.rc == KILN_ECS_ERR_CONFLICT) {
                    ++conflicts;
                    uint64_t seen = 0;
                    std::memcpy(&seen, c.confirmed.data(), 8);
                    if (seen > kThreads * kEach) ++impossible;  // a value the counter never held
                    guess = seen;
                    continue;
                }
                if (c.rc == KILN_ECS_ERR_BUSY) { ++busy; continue; }
                ++bad;
            }
        });
    }
    for (auto& w : writers) w.join();
    stop = true;
    frame.join();
    tick.join();
    drainer.join();
    v.wr.drain();
    v.wr.unsubscribe(drain_sub);
    check(delivered.events.load() == oks.load() && delivered.wrong.load() == 0 && v.wr.ring().in_use() == 0,
          k + "a drain racing the writers: one event per ok (" + std::to_string(delivered.events.load()) + " events, " +
              std::to_string(oks.load()) + " ok)");
    std::printf("  [%s] race: %ld ok, %ld conflicts, %ld busy, %ld frames\n", v.p.id, oks.load(), conflicts.load(), busy.load(),
                frames.load());
    check(bad.load() == 0 && impossible.load() == 0 && torn.load() == 0, k + "only ok, conflict and busy; every confirmed value real");
    check(static_cast<long>(u64_at(at)) == oks.load() && oks.load() == kThreads * kEach,
          k + "the counter equals the number of ok replies (" + std::to_string(oks.load()) + ", " +
              std::to_string(conflicts.load()) + " conflicts, " + std::to_string(busy.load()) + " busy, " +
              std::to_string(frames.load()) + " frames)");
    put<uint64_t>(at, 0);
}

// The hold time of a component of `size` bytes over `runs` writes: median, 99th percentile and worst; and the guarded
// copies one write makes inside its hold.
struct Timing {
    uint64_t median = 0, p99 = 0, worst = UINT64_MAX;
    uint64_t copies = 0, bytes = 0;
};
Timing hold_time(Env& v, const std::string& short_name, uint32_t id, uint32_t size, int runs) {
    const uint32_t hash = H(v.w, short_name.c_str());
    const uintptr_t at = component_address(v.w, id, short_name);
    KilnObjectRef r = open(v, kOwner, id);
    std::vector<uint8_t> cur(size, 0), next(size, 0);
    std::memcpy(reinterpret_cast<void*>(at), cur.data(), size);
    std::vector<uint64_t> times;
    Timing t;
    for (int i = 0; i < runs; ++i) {
        std::fill(next.begin(), next.end(), static_cast<uint8_t>(i + 1));
        next_step(v);
        const uint64_t copies = g_hold_copies.load(), bytes = g_hold_copy_bytes.load();
        Call c = cx(v.wr, kOwner, r, hash, cur, next);
        if (c.rc != KILN_OK) return t;
        t.copies = g_hold_copies.load() - copies;
        t.bytes = g_hold_copy_bytes.load() - bytes;
        times.push_back(c.reply.hold_ns);
        cur = next;
        v.wr.drain();
    }
    std::sort(times.begin(), times.end());
    t.median = times[times.size() / 2];
    t.p99 = times[std::min(times.size() - 1, times.size() * 99 / 100)];
    t.worst = times.back();
    return t;
}

uint32_t g_largest[2] = {0, 0};  // the largest writable component of the server and the client build (sigdb)

void exact(const Profile& p) {
    g_case = p.id;
    KilnApi api = make_api();
    const KilnEngineNativeApi* native = boot_engine(p, api);
    check(native != nullptr, "the real kiln.internal.engine table");
    if (!native) return;
    fake::Engine e(p.layout);
    kiln::engine::mem::test_image = e.image.at();
    Worlds worlds;
    worlds.world[KILN_WORLD_SERVER] = e.exec.at();
    worlds.session[KILN_WORLD_SERVER] = 41;
    worlds.steps[KILN_WORLD_SERVER] = 100;
    World server(p.layout, e.image, e.registry_owner(), e.manager());
    populate(server);
    server.reg("Counter", 8);
    server.reg("Pair", 8);
    server.reg("Tracked", 8);
    server.reg("Listy", 16);
    server.reg("Resized", 8);
    server.reg("Unknowable", 8);
    server.reg("Span", 256);
    const bool dedicated = std::string(p.target) == "enshrouded_server.exe";
    const uint32_t largest = std::max<uint32_t>(g_largest[dedicated ? 0 : 1], 8);
    server.reg("Largest", static_cast<uint16_t>(largest));
    server.reg("Max", 65535);
    set_tracking_bit(p.layout, e.registry_owner(), server.comp("keen::ecs::Tracked").index, true, true);
    const uintptr_t counters = server.archetype({"keen::ecs::Counter", "keen::ecs::Pair", "keen::ecs::Tracked"});
    server.add(7000, 2, counters, 0, nullptr);
    // Span at offset 4096 - 128 of its chunk, so it crosses a page boundary
    const uintptr_t span = server.archetype({"keen::ecs::Span"});
    put<uint16_t>(span + p.layout.entities.component_offsets + server.comp("keen::ecs::Span").index * 2, 4096 - 128);
    server.add(7001, 1, span, 0, nullptr);
    const uintptr_t big = server.archetype({"keen::ecs::Largest"});
    server.add(7002, 1, big, 0, nullptr);
    const uintptr_t max = server.archetype({"keen::ecs::Max"});
    server.add(7003, 1, max, 0, nullptr);
    faketypes::Types types;
    plain_types(types, server);
    types.add("keen::ecs::Span", KILN_TYPE_STATIC_ARRAY, 256, "keen::uint8");
    types.add("keen::ecs::Largest", KILN_TYPE_STATIC_ARRAY, largest, "keen::uint8");
    types.add("keen::ecs::Max", KILN_TYPE_STATIC_ARRAY, 65535, "keen::uint8");
    types.link();
    g_real = native;
    KilnEngineNativeApi hooked = *native;
    hooked.copy = &failing_copy;
    Reader reader(source_of(&hooked, worlds));
    Rig rig;
    rig.reader = &reader;
    rig.worlds = &worlds;
    WriterSource ws;
    ws.reader = &rig_reader;
    ws.engine = &hooked;
    ws.types = faketypes::api(types);
    ws.exact_profile = true;
    ws.user = &rig;
    ws.server_step = &rig_step;
    ws.guarded = &guarded;
    ws.log = &rig_log;
    Writer wr(ws);
    check(wr.available(), std::string(p.id) + ": writes are available on the fake engine");
    Env v{p, e, server, worlds, reader, wr, rig, reader.backend()};
    policy_cases(v);
    ok_cases(v);
    busy_cases(v);
    stale_cases(v);
    not_writable_cases(v);
    uncertain_cases(v);
    fault_cases(v);
    unavailable_cases(v);
    bounded_probe(v);
    event_cases(v);
    adopt_cases(v, ws);
    adopt_race(v, ws);
    race(v);
    // The hold time: the largest writable component of this build (from sigdb's reflection) and the 64 KiB worst case.
    const Timing a = hold_time(v, "Largest", 7002, largest, 1000);
    const Timing b = hold_time(v, "Max", 7003, 65535, 200);
    std::printf("  [%s] hold, %u bytes (the largest writable component): median %.1f us, p99 %.1f us, worst %.1f us; "
                "%llu guarded copies, %llu bytes read inside it\n",
                p.id, largest, a.median / 1000.0, a.p99 / 1000.0, a.worst / 1000.0,
                static_cast<unsigned long long>(a.copies), static_cast<unsigned long long>(a.bytes));
    std::printf("  [%s] hold, 65535 bytes: median %.1f us, p99 %.1f us, worst %.1f us; %llu guarded copies, %llu bytes\n",
                p.id, b.median / 1000.0, b.p99 / 1000.0, b.worst / 1000.0, static_cast<unsigned long long>(b.copies),
                static_cast<unsigned long long>(b.bytes));
    check(a.worst != UINT64_MAX && b.worst != UINT64_MAX, std::string(p.id) + ": the timed writes all succeeded");
    // Fixed work inside the hold: two lookups of the entity and the reads of the gate's checks, one copy of the
    // component, and its readback. The count does not grow with the component.
    check(a.copies == b.copies && a.bytes - largest * 2 == b.bytes - 65535u * 2,
          std::string(p.id) + ": the hold's copies do not depend on the component's size");
    kiln::engine::mem::test_image = 0;
    kiln::engine::gate_reset_for_tests();
}

// The plain-bytes rule over sigdb's reflection of an exact build, against an independent walk of the same JSON.
int json_plain(const nlohmann::json& by_name, const std::string& name, int depth) {  // 1 plain, 0 not, -1 unknown
    if (depth > 32 || !by_name.contains(name)) return -1;
    const auto& t = by_name.at(name);
    const int kind = t.at("kind").get<int>();
    if ((kind >= KILN_TYPE_BOOL && kind <= KILN_TYPE_BITMASK64) || kind == KILN_TYPE_GUID) return 1;
    if (kind == KILN_TYPE_TYPEDEF || kind == KILN_TYPE_STATIC_ARRAY)
        return t.contains("inner") ? json_plain(by_name, t.at("inner").get<std::string>(), depth + 1) : -1;
    if (kind == KILN_TYPE_STRUCT) {
        int r = 1;
        if (t.contains("inner")) r = std::min(r, json_plain(by_name, t.at("inner").get<std::string>(), depth + 1));
        if (t.contains("fields"))
            for (const auto& f : t.at("fields")) r = std::min(r, json_plain(by_name, f.at("type").get<std::string>(), depth + 1));
        return r;
    }
    return 0;
}
void sigdb_policy(const std::string& dir) {
    for (const auto& p : generated::kProfiles) {
        g_case = std::string(p.id) + " sigdb";
        const bool server = std::string(p.target) == "enshrouded_server.exe";
        const std::string path = dir + (server ? "/server/" : "/client/") + p.sha256 + ".json";
        faketypes::Types types;
        std::vector<std::string> components;
        nlohmann::json by_name = nlohmann::json::object();
        try {
            components = types.load_sigdb(path);
            std::ifstream in(path);
            const auto doc = nlohmann::json::parse(in);
            for (const auto& t : doc.at("types")) by_name[t.at("qualifiedName").get<std::string>()] = t;
        } catch (const std::exception& ex) {
            check(false, "read " + path + ": " + ex.what());
            continue;
        }
        const KilnTypesApi* api = faketypes::api(types);
        size_t plain = 0, not_plain = 0, unknown = 0, agree = 0;
        uint32_t largest = 0;
        std::string largest_name;
        for (const auto& name : components) {
            uint32_t size = 0;
            const char* why = nullptr;
            const Plain got = classify_plain(api, faketypes::fnv1a(name), &size, &why);
            const int ref = json_plain(by_name, name, 0);
            if ((got == Plain::Yes) == (ref == 1)) ++agree;
            else std::fprintf(stderr, "  disagree on %s\n", name.c_str());
            if (got == Plain::Yes) {
                ++plain;
                if (size > largest && size <= 65536) { largest = size; largest_name = name; }
            } else if (got == Plain::No) {
                ++not_plain;
            } else {
                ++unknown;
            }
        }
        g_largest[server ? 0 : 1] = largest;
        std::printf("  [%s] sigdb: %zu components: %zu plain, %zu not plain, %zu not describable; largest plain %s (%u bytes)\n",
                    p.id, components.size(), plain, not_plain, unknown, largest_name.c_str(), largest);
        check(components.size() > 600 && agree == components.size() && plain > 0 && not_plain > 0,
              "the runtime rule and an independent walk of the reflection agree on every component");
    }
}

}  // namespace

int main(int argc, char** argv) {
    g_caps.header = {sizeof g_caps, KILN_CAPS_VERSION};
    g_caps.report = &cap_report;
    kw::ecs::writes::g_hold_test_point = &on_hold_point;
    kiln::engine::g_gate_release_point = &on_release;
    decide_rules();
    if (argc > 1) sigdb_policy(argv[1]);
    else check(false, "pass sigdb/types");
    for (const auto& p : generated::kProfiles) exact(p);
    std::printf("ecs_writes: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
