// SPDX-License-Identifier: MIT
// kiln.toolwear's hook logic (toolwear.h) and the stamp table's capture, with no game: a made-up world (a storage with
// its kind byte and registry, a component map, accessors, component bytes by entity) stands behind toolwear::Ops.
// What is checked is what the hooks decide: which calls are changed, which are forwarded untouched, what is queued, and
// that totals are exact (a debit of 7 is 7, never "changed at all"). Every byte below is invented.
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <map>
#include <thread>
#include <utility>
#include <vector>

#include "../stamps.h"
#include "../toolwear.h"

using namespace kitems;
using namespace kitems::toolwear;

static int g_fail = 0;
#define CHECK(c) do { if (!(c)) { std::printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); g_fail++; } } while (0)

// ---- the made-up world ----------------------------------------------------------------------------------------------
namespace fake {
constexpr uint32_t kPickaxe = 0x2525bb44u, kOther = 0x11111111u, kTool = 2032, kActor = 2022;

struct World {
    uint8_t storage[64] = {};          // +0 registry pointer, +8 kind
    uint8_t registry[0x100] = {};      // +0x88 the component map (the fake find ignores its bytes)
    uint16_t type_item_state = 41, type_durability = 77, type_other = 9;
    uint8_t world_base[0x100] = {};    // server_world() + world_storage == storage
};
World g_world, g_prediction;
uintptr_t g_server_world = 0;
void* g_job_storage = nullptr;                       // what job_storage answers
std::map<std::pair<uint64_t, uint32_t>, std::vector<uint8_t>> g_components;  // (accessor, entity) -> bytes
uint64_t g_iter[10] = {};
uint32_t g_stamp = 500;
uint64_t g_epoch = 1, g_step = 90;
int g_captures = 0;
StampTable g_table;

void* job_storage(void*) { return g_job_storage; }
Slice* read_other(Slice* out, void*, uint64_t accessor, uint32_t entity) {
    auto it = g_components.find({accessor, entity});
    out->ptr = it == g_components.end() ? nullptr : it->second.data();
    out->size = it == g_components.end() ? 0 : it->second.size();
    return out;
}
void iter_begin(void*, uint64_t* iter, uint32_t size) { std::memcpy(iter, g_iter, size < sizeof g_iter ? size : sizeof g_iter); }
uint16_t* component_map_find(void*, const uint32_t* hash) {
    if (*hash == wear::kTypeItemState) return &g_world.type_item_state;
    if (*hash == wear::kTypeDurability) return &g_world.type_durability;
    return nullptr;
}
uintptr_t server_world() { return g_server_world; }
bool capture(uint32_t, uint32_t, uint64_t, uint32_t* stamp, uint64_t* epoch) {
    ++g_captures;
    *stamp = g_stamp;
    *epoch = g_epoch;
    return true;
}
uint64_t step() { return g_step; }
int max_index(void*) { return 1; }

// accessors: pointers to the component's type index, as the game's are
uint64_t acc_state() { return reinterpret_cast<uint64_t>(&g_world.type_item_state); }
uint64_t acc_durability() { return reinterpret_cast<uint64_t>(&g_world.type_durability); }
uint64_t acc_other() { return reinterpret_cast<uint64_t>(&g_world.type_other); }

std::vector<uint8_t> durability(int32_t current, int32_t max, bool writable = true) {
    std::vector<uint8_t> d(16, 0);
    const uint32_t schema = wear::kAttrDurability;
    const uint16_t offset = 8;
    std::memcpy(d.data(), &schema, 4);
    std::memcpy(d.data() + 4, &offset, 2);
    d[6] = writable ? 1 : 0;
    d[7] = 2;
    std::memcpy(d.data() + 8, &current, 4);
    std::memcpy(d.data() + 12, &max, 4);
    return d;
}
std::vector<uint8_t> item_state(uint32_t item) {
    std::vector<uint8_t> s(20, 0);
    std::memcpy(s.data() + 8, &item, 4);
    return s;
}
int32_t current(uint32_t entity) {
    int32_t v = 0;
    std::memcpy(&v, g_components[{acc_durability(), entity}].data() + 8, 4);
    return v;
}
// the game's WearDelta on the fake tool: adds the delta (never below 0), unless `saved` (the skill roll)
void game_debit(uint32_t entity, int32_t delta, bool saved = false) {
    if (saved) return;
    int32_t v = current(entity) + delta;
    if (v < 0) v = 0;
    std::memcpy(g_components[{acc_durability(), entity}].data() + 8, &v, 4);
}

void* g_job = &g_world;      // any non-null "job"
void* g_ctx_storage[2] = {}; // a ProgramCtx: its first word is the job

void reset(State& s) {
    g_world = World{};
    g_prediction = World{};
    g_prediction.storage[8] = 1;  // a prediction world's storage
    void* registry = g_world.registry;
    std::memcpy(g_world.storage, &registry, sizeof registry);
    std::memcpy(g_prediction.storage, &registry, sizeof registry);
    g_server_world = reinterpret_cast<uintptr_t>(g_world.storage) - 0x40;
    g_job_storage = g_world.storage;
    g_ctx_storage[0] = g_job;
    g_components.clear();
    g_components[{acc_state(), kTool}] = item_state(kPickaxe);
    g_components[{acc_durability(), kTool}] = durability(180, 180);
    std::memset(g_iter, 0, sizeof g_iter);
    g_iter[5] = acc_state();
    g_stamp = 500;
    g_epoch = 1;
    g_step = 90;
    g_captures = 0;
    s.ops = Ops{};
    s.ops.job_storage = &job_storage;
    s.ops.read_other = &read_other;
    s.ops.iter_begin = &iter_begin;
    s.ops.component_map_find = &component_map_find;
    s.ops.server_world = &server_world;
    s.ops.capture = &capture;
    s.ops.step = &step;
    s.ops.max_index = &max_index;
    s.ops.world_storage = 0x40;
    s.ops.storage_kind = 8;
    s.ops.registry_components = 0x88;
    s.ops.weapon_return = 0x56854;
    s.ops.spawn_request_local = 0x390;
    s.ops.spawn_durability_local = 0x458;
    s.ops.spawn_request_pide = 0x3c;
    s.ops.spawn_iter_base = 0x280;
    s.ops.spawn_itemstate_slot = 0x1f0;
    s.on.store(true);
    s.rules.set(kPickaxe, -1);  // every case starts without a rule
    s.rules.set(kOther, -1);
    Change drop[16];
    while (s.queue.drain(drop, 16)) {}
}

wear::WearEnv env() {
    wear::WearEnv e{};
    e.durability_index = 0;
    e.ctx = g_ctx_storage;
    e.attributes = &g_world;
    return e;
}
}  // namespace fake

static State* g_state = new State;  // large (the queue): not on the stack

// One call of the game's WearItem through the detour's logic, as toolwear.cpp runs it. Returns the delta the game got.
static int32_t wear_item(uintptr_t caller, uint32_t actor, uint32_t entity, int32_t delta, bool saved = false, uint64_t dur_acc = 0) {
    State& s = *g_state;
    auto e = fake::env();
    if (!dur_acc) dur_acc = fake::acc_durability();
    HitPending p;
    const int32_t passed = hit_before(s, caller, dur_acc, actor, entity, delta, &e, &p);
    if (fake::g_components.count({dur_acc, entity})) fake::game_debit(entity, passed, saved);
    hit_after(s, p, dur_acc, &e);
    return passed;
}

static size_t drained(Change* out, size_t max = 8) { return g_state->queue.drain(out, max); }

static void test_weapon_site() {
    State& s = *g_state;
    Change c[8];
    fake::reset(s);
    // no rule: the game's own debit, nothing queued, no stamp taken
    CHECK(wear_item(0x56854, fake::kActor, fake::kTool, -1) == -1 && fake::current(fake::kTool) == 179 && drained(c) == 0 && fake::g_captures == 0);

    // cost 7: exactly 7, one record with the exact values and the captured identity
    fake::reset(s);
    CHECK(s.rules.set(fake::kPickaxe, 7));
    CHECK(wear_item(0x56854, fake::kActor, fake::kTool, -1) == -7 && fake::current(fake::kTool) == 173);
    CHECK(drained(c) == 1 && c[0].source == kSourceWeaponHit && c[0].before == 180 && c[0].after == 173 && c[0].maximum == 180 &&
          c[0].original_delta == -1 && c[0].requested_delta == -7 && c[0].actor == fake::kActor && c[0].item_entity == fake::kTool &&
          c[0].item_id == fake::kPickaxe && c[0].stamp == 500 && c[0].epoch == 1 && c[0].step == 90);
    // two eligible debits spend twice: 14 in all, two records
    CHECK(wear_item(0x56854, fake::kActor, fake::kTool, -1) == -7 && wear_item(0x56854, fake::kActor, fake::kTool, -1) == -7);
    CHECK(fake::current(fake::kTool) == 159 && drained(c) == 2 && c[0].before == 173 && c[0].after == 166 && c[1].before == 166 && c[1].after == 159);

    // another caller of WearItem (the armor site, another loader, Kiln's own cost): forwarded untouched, nothing queued
    CHECK(wear_item(0x567c3, fake::kActor, fake::kTool, -1) == -1 && wear_item(0, fake::kActor, fake::kTool, -1) == -1 && drained(c) == 0);
    CHECK(fake::current(fake::kTool) == 157);
    // a positive delta and an unexpected negative one at the weapon site: forwarded
    CHECK(wear_item(0x56854, fake::kActor, fake::kTool, 5) == 5 && wear_item(0x56854, fake::kActor, fake::kTool, -3) == -3 && drained(c) == 0);
    // cost 0 spends nothing and says nothing
    CHECK(s.rules.set(fake::kPickaxe, 0));
    const int32_t at = fake::current(fake::kTool);
    CHECK(wear_item(0x56854, fake::kActor, fake::kTool, -1) == 0 && fake::current(fake::kTool) == at && drained(c) == 0);
    // the skill roll saved the debit: the rule asked for 7, nothing changed, no event
    CHECK(s.rules.set(fake::kPickaxe, 7));
    CHECK(wear_item(0x56854, fake::kActor, fake::kTool, -1, true) == -7 && fake::current(fake::kTool) == at && drained(c) == 0);
    // a rule taken away: the game's debit again
    CHECK(s.rules.set(fake::kPickaxe, -1) && wear_item(0x56854, fake::kActor, fake::kTool, -1) == -1 && drained(c) == 0);

    // no ItemState for the entity: forwarded unchanged (the game debits it without its filter; that stays the game's)
    fake::reset(s);
    s.rules.set(fake::kPickaxe, 7);
    fake::g_components.erase({fake::acc_state(), fake::kTool});
    CHECK(wear_item(0x56854, fake::kActor, fake::kTool, -1) == -1 && drained(c) == 0 && fake::g_captures == 0);
    // the entity holds another item: no rule for it
    fake::reset(s);
    s.rules.set(fake::kPickaxe, 7);
    fake::g_components[{fake::acc_state(), fake::kTool}] = fake::item_state(fake::kOther);
    CHECK(wear_item(0x56854, fake::kActor, fake::kTool, -1) == -1 && drained(c) == 0);
    // a read-only or short Durability: forwarded
    fake::reset(s);
    s.rules.set(fake::kPickaxe, 7);
    fake::g_components[{fake::acc_durability(), fake::kTool}] = fake::durability(180, 180, false);
    CHECK(wear_item(0x56854, fake::kActor, fake::kTool, -1) == -1 && drained(c) == 0);
    // an accessor that is not Durability's (a frame that moved): forwarded
    fake::reset(s);
    s.rules.set(fake::kPickaxe, 7);
    fake::g_components[{fake::acc_other(), fake::kTool}] = fake::durability(180, 180);
    CHECK(wear_item(0x56854, fake::kActor, fake::kTool, -1, false, fake::acc_other()) == -1 && drained(c) == 0);
    // ... and the iterator's slot 5 not being ItemState's
    fake::reset(s);
    s.rules.set(fake::kPickaxe, 7);
    fake::g_iter[5] = fake::acc_other();
    CHECK(wear_item(0x56854, fake::kActor, fake::kTool, -1) == -1 && drained(c) == 0);
    // no stamp to give (the sequence is used up, or the entity is gone): no rule is applied, no made-up identity
    fake::reset(s);
    s.rules.set(fake::kPickaxe, 7);
    fake::g_stamp = 0;
    CHECK(wear_item(0x56854, fake::kActor, fake::kTool, -1) == -1 && fake::current(fake::kTool) == 179 && drained(c) == 0);
    // switched off: everything is forwarded
    fake::reset(s);
    s.rules.set(fake::kPickaxe, 7);
    s.on.store(false);
    CHECK(wear_item(0x56854, fake::kActor, fake::kTool, -1) == -1 && drained(c) == 0);
}

static void test_authority() {
    State& s = *g_state;
    Change c[8];
    // a prediction world's storage (kind 1): never touched, the server's rule is not consulted
    fake::reset(s);
    s.rules.set(fake::kPickaxe, 7);
    fake::g_job_storage = fake::g_prediction.storage;
    CHECK(wear_item(0x56854, fake::kActor, fake::kTool, -1) == -1 && drained(c) == 0 && fake::g_captures == 0);
    // kind 0 but not the server world of this process (another world's storage)
    fake::reset(s);
    s.rules.set(fake::kPickaxe, 7);
    fake::g_server_world += 0x10;
    CHECK(wear_item(0x56854, fake::kActor, fake::kTool, -1) == -1 && drained(c) == 0);
    // no server world at all (a joined client)
    fake::reset(s);
    s.rules.set(fake::kPickaxe, 7);
    fake::g_server_world = 0;
    CHECK(wear_item(0x56854, fake::kActor, fake::kTool, -1) == -1 && drained(c) == 0);
    // a context without a job
    fake::reset(s);
    s.rules.set(fake::kPickaxe, 7);
    fake::g_ctx_storage[0] = nullptr;
    CHECK(wear_item(0x56854, fake::kActor, fake::kTool, -1) == -1 && drained(c) == 0);
    // the world went between the debit and its record: the debit happened at the rule's cost, nothing is said about it
    fake::reset(s);
    s.rules.set(fake::kPickaxe, 7);
    {
        auto e = fake::env();
        HitPending p;
        const int32_t passed = hit_before(s, 0x56854, fake::acc_durability(), fake::kActor, fake::kTool, -1, &e, &p);
        fake::game_debit(fake::kTool, passed);
        fake::g_server_world = 0;
        hit_after(s, p, fake::acc_durability(), &e);
        CHECK(passed == -7 && fake::current(fake::kTool) == 173 && drained(c) == 0);
    }
}

// The spawn site's frame: rbp-relative locals and the fifth argument on the stack, laid out as the approved build has them.
struct SpawnFrame {
    uint8_t stack[0x40] = {};
    uint8_t frame[0x500] = {};
    uint8_t action[0x60] = {};
    wear::WearEnv env{};
    SpawnRegs regs{};

    explicit SpawnFrame(uint32_t item = fake::kPickaxe, uint32_t entity = fake::kTool, uint32_t actor = fake::kActor) {
        env = fake::env();
        const uint32_t sequence = 0xabcd1234u, call = 3;
        const int64_t trigger = 123456789;
        const uint16_t trigger_type = 1;
        std::memcpy(action, &sequence, 4);
        std::memcpy(action + 8, &trigger, 8);
        std::memcpy(action + 0x10, &actor, 4);
        std::memcpy(action + 0x14, &call, 4);
        std::memcpy(action + 0x18, &trigger_type, 2);
        std::memcpy(action + 0x38, &item, 4);
        std::memcpy(action + 0x3c, &entity, 4);
        const uint8_t* a = action;
        const uint64_t dur = fake::acc_durability(), state = fake::acc_state();
        std::memcpy(frame + 0x390, &a, 8);
        std::memcpy(frame + 0x458, &dur, 8);
        std::memcpy(frame + 0x280 + 0x1f0, &state, 8);
        const wear::WearEnv* e = &env;
        std::memcpy(stack + 0x20, &e, 8);
        regs.rbp = reinterpret_cast<uintptr_t>(frame);
        regs.rsp = reinterpret_cast<uintptr_t>(stack);
        regs.rcx = reinterpret_cast<uintptr_t>(fake::g_components[{fake::acc_durability(), entity}].data());
        regs.r8 = 0xFFFFFFFFu;
        regs.r9 = actor;
        regs.r12 = reinterpret_cast<uintptr_t>(env.ctx);
        regs.r14 = dur;
    }
};

// The spawn site and the WearDelta call that follows it, as toolwear.cpp runs them. Returns the delta the game got.
static int32_t spawn(SpawnFrame& f, Frame* frame, bool saved = false) {
    State& s = *g_state;
    spawn_before(s, f.regs, frame);
    const int32_t delta = static_cast<int32_t>(static_cast<uint32_t>(f.regs.r8));
    void* dur = reinterpret_cast<void*>(f.regs.rcx);
    const bool ours = delta_before(frame, dur, static_cast<uint32_t>(f.regs.r9), &f.env);
    fake::game_debit(fake::kTool, delta, saved);
    if (ours) delta_after(s, frame, dur);
    return delta;
}

static void test_spawn_site() {
    State& s = *g_state;
    Change c[8];
    Frame frame;
    // no rule: -1 stays, nothing armed
    fake::reset(s);
    { SpawnFrame f; CHECK(spawn(f, &frame) == -1 && !frame.armed && drained(c) == 0 && fake::current(fake::kTool) == 179); }
    // cost 7: exactly 7, with the action's identity
    fake::reset(s);
    s.rules.set(fake::kPickaxe, 7);
    {
        SpawnFrame f;
        CHECK(spawn(f, &frame) == -7 && fake::current(fake::kTool) == 173 && !frame.armed);
        CHECK(drained(c) == 1 && c[0].source == kSourceProjectileSpawn && c[0].before == 180 && c[0].after == 173 && c[0].maximum == 180 &&
              c[0].original_delta == -1 && c[0].requested_delta == -7 && c[0].item_id == fake::kPickaxe && c[0].item_entity == fake::kTool &&
              c[0].actor == fake::kActor && c[0].sequence_id == 0xabcd1234u && c[0].call_index == 3 && c[0].trigger_time == 123456789 &&
              c[0].trigger_type == 1 && c[0].stamp == 500);
    }
    // cost 0, and a debit the skill roll saved: no record
    s.rules.set(fake::kPickaxe, 0);
    { SpawnFrame f; CHECK(spawn(f, &frame) == 0 && drained(c) == 0); }
    s.rules.set(fake::kPickaxe, 7);
    { SpawnFrame f; CHECK(spawn(f, &frame, true) == -7 && drained(c) == 0 && !frame.armed); }

    // each promise of the approved frame, broken in turn: the delta stays what the game asked and nothing is armed
    auto refused = [&](void (*breakit)(SpawnFrame&)) {
        fake::reset(s);
        s.rules.set(fake::kPickaxe, 7);
        SpawnFrame f;
        breakit(f);
        Frame fr;
        const uintptr_t asked = f.regs.r8;
        spawn_before(s, f.regs, &fr);
        return f.regs.r8 == asked && !fr.armed && fake::g_captures == 0;
    };
    CHECK(refused([](SpawnFrame& f) { f.regs.r12 += 8; }));                                   // env.ctx is not R12
    CHECK(refused([](SpawnFrame& f) { f.regs.r9 = 7; }));                                     // the notified actor is not the action's
    CHECK(refused([](SpawnFrame& f) { f.regs.r14 = fake::acc_other(); }));                    // the accessor local is not R14
    CHECK(refused([](SpawnFrame& f) { f.regs.rcx += 16; }));                                  // RCX is not the tool's Durability
    CHECK(refused([](SpawnFrame& f) { f.regs.r8 = static_cast<uint32_t>(-3); }));             // not the game's -1
    CHECK(refused([](SpawnFrame& f) { std::memset(f.stack + 0x20, 0, 8); }));                 // no env
    CHECK(refused([](SpawnFrame& f) { std::memset(f.frame + 0x390, 0, 8); }));                // no current action
    CHECK(refused([](SpawnFrame& f) { const uint32_t z = 0; std::memcpy(f.action + 0x3c, &z, 4); }));  // no used item entity
    CHECK(refused([](SpawnFrame& f) { const uint64_t o = fake::acc_other(); std::memcpy(f.frame + 0x280 + 0x1f0, &o, 8); }));  // slot +0x1f0 is not ItemState
    CHECK(refused([](SpawnFrame&) { fake::g_components[{fake::acc_state(), fake::kTool}] = fake::item_state(fake::kOther); }));  // the entity holds another item now
    CHECK(refused([](SpawnFrame&) { fake::g_job_storage = fake::g_prediction.storage; }));    // a prediction world
    CHECK(refused([](SpawnFrame&) { fake::g_server_world = 0; }));                            // no authoritative world
    // no stamp: refused after the checks (one capture was asked for), -1 stays
    fake::reset(s);
    s.rules.set(fake::kPickaxe, 7);
    fake::g_stamp = 0;
    { SpawnFrame f; Frame fr; spawn_before(s, f.regs, &fr); CHECK(static_cast<uint32_t>(f.regs.r8) == 0xFFFFFFFFu && !fr.armed); }
}

static void test_wear_delta_of_others() {
    State& s = *g_state;
    Change c[8];
    fake::reset(s);
    s.rules.set(fake::kPickaxe, 7);
    auto e = fake::env();
    void* dur = fake::g_components[{fake::acc_durability(), fake::kTool}].data();
    // a WearDelta call nobody armed (Kiln's own cost in its own program, the torch burndown, another loader): not ours
    Frame frame;
    CHECK(!delta_before(&frame, dur, fake::kActor, &e));
    fake::game_debit(fake::kTool, -5);
    CHECK(drained(c) == 0 && fake::current(fake::kTool) == 175);
    // a frame left armed by a spawn site whose call never came, then another WearDelta on this thread: dropped, not attributed
    SpawnFrame f;
    spawn_before(s, f.regs, &frame);
    CHECK(frame.armed);
    uint8_t other[16] = {};
    CHECK(!delta_before(&frame, other, fake::kActor, &e) && !frame.armed);
    // the right Durability but another actor to notify, or another program's context: not the armed call
    spawn_before(s, f.regs, &frame);
    CHECK(!delta_before(&frame, dur, 99, &e) && !frame.armed);
    spawn_before(s, f.regs, &frame);
    wear::WearEnv elsewhere = e;
    void* other_ctx[2] = {&elsewhere, nullptr};
    elsewhere.ctx = other_ctx;
    CHECK(!delta_before(&frame, dur, fake::kActor, &elsewhere) && !frame.armed);
    CHECK(drained(c) == 0);
}

static void test_rules_and_queue() {
    auto* rules = new Rules;
    CHECK(rules->find(5) == -1 && rules->find(0) == -1);
    CHECK(rules->set(5, 7) && rules->find(5) == 7 && rules->set(5, 0) && rules->find(5) == 0);
    CHECK(!rules->set(5, 32768) && !rules->set(5, -2) && !rules->set(0, 1) && rules->find(5) == 0);
    // ids that land on one slot: each found, and still found after the one before it was taken away
    CHECK(rules->set(5 + kMaxRules, 3) && rules->set(5 + 2 * kMaxRules, 4));
    CHECK(rules->set(5, -1) && rules->find(5) == -1 && rules->find(5 + kMaxRules) == 3 && rules->find(5 + 2 * kMaxRules) == 4);
    CHECK(rules->set(5, 9) && rules->find(5) == 9);   // its slot is used again, not a new one
    // the table takes 256 tools, then says no
    auto* full = new Rules;
    bool all = true;
    for (uint32_t i = 1; i <= kMaxRules; ++i) all = all && full->set(i * 7919u, 1);
    CHECK(all && !full->set(0x7fffffffu, 1) && full->find(0x7fffffffu) == -1 && full->find(7919u) == 1);

    // the queue: full means counted as dropped, never waited for; the oldest come out first
    auto* q = new Queue;
    Change c{};
    for (uint32_t i = 0; i < kQueue + 5; ++i) {
        c.before = static_cast<int32_t>(i);
        q->push(c);
    }
    CHECK(q->size() == kQueue && q->dropped() == 5);
    Change out[4];
    CHECK(q->drain(out, 4) == 4 && out[0].before == 0 && out[3].before == 3 && q->size() == kQueue - 4);
    q->clear();
    CHECK(q->size() == 0 && q->drain(out, 4) == 0 && q->dropped() == 5);  // a world's end is not an overflow
}

static void test_capture() {
    StampTable t;
    // one answer under one lock: the pair names the entity in this world
    const auto a = t.capture(2000, 0x10, 1);
    CHECK(a.stamp != 0 && a.epoch == 1 && t.peek(2000) == a.stamp && t.epoch() == 1);
    CHECK(t.capture(2000, 0x10, 2).stamp == a.stamp);            // the same entity: the same stamp
    CHECK(t.capture(5, 0x10, 1).stamp == 0);                     // a player's id is never stamped
    t.destroyed(2000, 0x10, 3);
    CHECK(t.capture(2000, 0x10, 4).stamp == 0);                  // gone: no identity to give
    t.reset();
    const auto b = t.capture(2000, 0x10, 5);
    CHECK(b.epoch == 2 && b.stamp != 0 && b.stamp != a.stamp);   // the next world: the id means another entity
    // a queued record of the earlier world is dropped by its epoch, also when the id was stamped again
    CHECK(a.epoch != t.epoch() && t.peek(2000) != a.stamp);

    // captures against resets on two threads: every pair is of one world (within an epoch an entity has one stamp)
    StampTable shared;
    std::vector<StampTable::Captured> got;
    got.reserve(200000);
    std::thread resetter([&] {
        for (int i = 0; i < 2000; ++i) shared.reset();
    });
    for (int i = 0; i < 200000; ++i) got.push_back(shared.capture(3000, 0x10, static_cast<uint64_t>(i)));
    resetter.join();
    bool ok = true;
    for (size_t i = 1; i < got.size(); ++i) {
        if (got[i].epoch < got[i - 1].epoch) ok = false;                                           // epochs only grow
        if (got[i].epoch == got[i - 1].epoch && got[i].stamp != got[i - 1].stamp) ok = false;      // one stamp per world
        if (got[i].epoch != got[i - 1].epoch && got[i].stamp == got[i - 1].stamp) ok = false;      // a new world, a new stamp
        if (!got[i].stamp) ok = false;
    }
    CHECK(ok);
}

int main() {
    test_weapon_site();
    test_authority();
    test_spawn_site();
    test_wear_delta_of_others();
    test_rules_and_queue();
    test_capture();
    std::printf("items-toolwear-test: %s\n", g_fail ? "FAILED" : "ok");
    return g_fail ? 1 : 0;
}
