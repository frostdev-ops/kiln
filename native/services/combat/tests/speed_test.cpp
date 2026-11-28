// SPDX-License-Identifier: MIT
// kiln.combat.speed (speed_core.h) on the host, over fakes: the rate table (defaults, players, clamping, refusals), Kiln's
// routes, the clock / graph / bow arithmetic, the four hook bodies over a register block (which runs scale, which are left
// alone), the graph bindings between a binding and a later evaluation, and a bow draw in progress keeping its durations.
#include <cstdio>
#include <cstring>
#include <limits>
#include <thread>
#include <vector>

#include <chrono>

#include "../sampler_core.h"
#include "../speed_core.h"

using namespace kc::speed;

static int g_fail = 0;
#define CHECK(cond)                                                          \
    do {                                                                     \
        if (!(cond)) {                                                       \
            ++g_fail;                                                        \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);      \
        }                                                                    \
    } while (0)

static const float kOnes[3] = {1, 1, 1};

static void rate_table() {
    RateTable t;
    bool any = true;
    CHECK(t.set(kOnes, nullptr, 0, &any) && !any && !t.scaling());
    CHECK(t.rate(5, KILN_ACTION_MELEE) == 1.0f);
    // defaults for every player, one player with its own set
    const float d[3] = {2.0f, 1.0f, 3.0f};
    KilnActionRate p[1] = {{7, {4.0f, 1.5f, 1.0f}}};
    CHECK(t.set(d, p, 1, &any) && any && t.scaling());
    CHECK(t.rate(5, KILN_ACTION_MELEE) == 2.0f && t.rate(5, KILN_ACTION_TOOL) == 3.0f && t.rate(5, KILN_ACTION_BOW) == 1.0f);
    CHECK(t.rate(7, KILN_ACTION_MELEE) == 4.0f && t.rate(7, KILN_ACTION_BOW) == 1.5f && t.rate(7, KILN_ACTION_TOOL) == 1.0f);
    // players only: 1..1023
    CHECK(t.rate(0, KILN_ACTION_MELEE) == 1.0f && t.rate(1024, KILN_ACTION_MELEE) == 1.0f && t.rate(5000, KILN_ACTION_MELEE) == 1.0f);
    CHECK(t.rate(5, -1) == 1.0f && t.rate(5, 3) == 1.0f);
    // clamped to [0.5, 10]
    const float wild[3] = {100.0f, 0.01f, 10.0f};
    CHECK(t.set(wild, nullptr, 0, &any) && t.rate(9, KILN_ACTION_MELEE) == 10.0f && t.rate(9, KILN_ACTION_BOW) == 0.5f);
    CHECK(t.rate(7, KILN_ACTION_MELEE) == 10.0f);  // player 7 left the table: the defaults again
    // refusals change nothing
    const float nan[3] = {std::numeric_limits<float>::quiet_NaN(), 1, 1};
    const float inf[3] = {1, std::numeric_limits<float>::infinity(), 1};
    CHECK(!t.set(nan, nullptr, 0, &any) && !t.set(inf, nullptr, 0, &any));
    KilnActionRate npc[1] = {{1024, {2, 2, 2}}};
    CHECK(!t.set(kOnes, npc, 1, &any));
    KilnActionRate twice[2] = {{3, {2, 2, 2}}, {3, {3, 3, 3}}};
    CHECK(!t.set(kOnes, twice, 2, &any));
    std::vector<KilnActionRate> many(RateTable::kSlots + 1);
    for (uint32_t i = 0; i < many.size(); ++i) many[i] = {i + 1, {2, 2, 2}};
    CHECK(!t.set(kOnes, many.data(), static_cast<uint32_t>(many.size()), &any));
    CHECK(t.rate(9, KILN_ACTION_MELEE) == 10.0f);  // still the last good table
    // a full table of 64 players, then back to nothing at all
    CHECK(t.set(kOnes, many.data(), RateTable::kSlots, &any) && any && t.players() == RateTable::kSlots);
    CHECK(t.rate(64, KILN_ACTION_TOOL) == 2.0f && t.rate(65, KILN_ACTION_TOOL) == 1.0f);
    CHECK(t.set(kOnes, nullptr, 0, &any) && !any && !t.scaling() && t.rate(64, KILN_ACTION_TOOL) == 1.0f);
}

static void routes() {
    CHECK(item_kind_of_slot(kSlotMelee) == KILN_ACTION_MELEE);
    CHECK(item_kind_of_slot(kSlotTool) == KILN_ACTION_TOOL);
    CHECK(item_kind_of_slot(kSlotRange) == KILN_ACTION_BOW && item_kind_of_slot(kSlotMidRange) == KILN_ACTION_BOW);
    CHECK(item_kind_of_slot(0) == kNone && item_kind_of_slot(7 /* Spell */) == kNone && item_kind_of_slot(18 /* Shield */) == kNone);
    // melee: light (Primary, tap, hold) and heavy; not block, parry, evade, secondary
    for (uint16_t t : {kPrimary, kPrimaryTap, kPrimaryHold, kHeavy}) CHECK(classify(t, KILN_ACTION_MELEE, kRangedUnknown) == KILN_ACTION_MELEE);
    for (uint16_t t : {0, 5, 7, 8, 9, 10, 12, 20}) CHECK(classify(t, KILN_ACTION_MELEE, kRangedUnknown) == kNone);
    // tools: the attack triggers and the Tool trigger
    for (uint16_t t : {kPrimary, kHeavy, kToolTrigger}) CHECK(classify(t, KILN_ACTION_TOOL, kRangedUnknown) == KILN_ACTION_TOOL);
    CHECK(classify(8, KILN_ACTION_TOOL, kRangedUnknown) == kNone);
    // ranged: a bow only while the actor's ranged weapon is a bow (0); staffs (1) and wands (2) never, unknown never
    CHECK(classify(kPrimary, KILN_ACTION_BOW, kWeaponBow) == KILN_ACTION_BOW);
    CHECK(classify(kPrimary, KILN_ACTION_BOW, 1) == kNone && classify(kPrimary, KILN_ACTION_BOW, 2) == kNone);
    CHECK(classify(kPrimary, KILN_ACTION_BOW, kRangedUnknown) == kNone);
    CHECK(classify(kHeavy, KILN_ACTION_BOW, kWeaponBow) == kNone && classify(kSecondary, KILN_ACTION_BOW, kWeaponBow) == kNone);
    // no item kind: the game's timing (eating, interacting, unarmed)
    CHECK(classify(kPrimary, kNone, kWeaponBow) == kNone);
    // the item list: sorted, deduplicated, bad kinds refused
    ItemKinds items;
    CHECK(!items.ready() && items.kind(42) == kNone);
    const KilnActionItemKind list[] = {{300, KILN_ACTION_TOOL, {}}, {42, KILN_ACTION_MELEE, {}}, {300, KILN_ACTION_TOOL, {}}, {77, KILN_ACTION_BOW, {}}};
    CHECK(items.set(list, 4) && items.ready() && items.size() == 3);
    CHECK(items.kind(42) == KILN_ACTION_MELEE && items.kind(77) == KILN_ACTION_BOW && items.kind(300) == KILN_ACTION_TOOL && items.kind(5) == kNone);
    const KilnActionItemKind bad[] = {{1, 3, {}}};
    CHECK(!items.set(bad, 1) && items.size() == 3);
    KilnActionItemKind out[2];
    CHECK(items.copy(out, 2) == 3 && out[0].item == 42 && out[1].item == 77);
}

static void arithmetic() {
    const int64_t step = 33'333'333;  // one 30 Hz step
    CHECK(scale_elapsed(step, 1.0f) == step);
    CHECK(scale_elapsed(step, 2.0f) == 66'666'666);
    CHECK(scale_elapsed(step, 0.5f) == 16'666'667);  // rounded
    CHECK(scale_elapsed(step, 10.0f) == 333'333'330);
    CHECK(scale_elapsed(0, 4.0f) == 0 && scale_elapsed(-5, 4.0f) == -5);
    CHECK(scale_elapsed(INT64_MAX, 4.0f) == INT64_MAX && scale_elapsed(INT64_MIN, 4.0f) == INT64_MIN);  // sentinels
    CHECK(scale_elapsed(1, 0.5f) == 1);  // never 0 for a running clock
    // bow: divided, at least 1 ns
    const int64_t charge = 1'200'000'000;
    CHECK(divide_duration(charge, 2.0f) == 600'000'000 && divide_duration(charge, 0.5f) == 2'400'000'000);
    CHECK(divide_duration(charge, 1.0f) == charge && divide_duration(0, 3.0f) == 0 && divide_duration(-1, 3.0f) == -1);
    CHECK(divide_duration(1, 10.0f) == 1 && divide_duration(INT64_MAX, 2.0f) == INT64_MAX);
    // graph delta
    CHECK(scale_delta(1.0f / 60, 3.0f) == (1.0f / 60) * 3.0f && scale_delta(0.0f, 3.0f) == 0.0f && scale_delta(0.02f, 1.0f) == 0.02f);
    CHECK(std::isnan(scale_delta(std::numeric_limits<float>::quiet_NaN(), 2.0f)));
}

// ---- the hooks over a register block ----
struct World {
    RateTable rates;
    ActionRecords records;
    RangedTypes ranged;
    Bindings bindings;
    ItemKinds items;
    Counters counters;
    Layout L;
    World() {
        L.seq_reg = 7;          // client: rdi
        L.seq_trigger = 0x18;
        L.seq_item = 0x38;
        L.server_actor = -0x7C;
        L.client_actor = 0x14;
        L.bow_params = 0x1B0;
        const KilnActionItemKind list[] = {{0xAA, KILN_ACTION_MELEE, {}}, {0xBB, KILN_ACTION_BOW, {}}, {0xCC, KILN_ACTION_TOOL, {}}};
        items.set(list, 3);
    }
    Env env(uint8_t scope, uint8_t program, uint32_t step = 100, uint32_t local = 0) {
        Env e;
        e.rates = &rates, e.records = &records, e.ranged = &ranged, e.bindings = &bindings, e.items = &items;
        e.entity_of = [](uintptr_t accessor) -> uint32_t { return accessor ? *reinterpret_cast<uint32_t*>(accessor) : 0; };
        e.local_player = local;
        e.mark = Mark{scope, program, step};
        e.now_ms = 1000;
        return e;
    }
};

struct SeqFrame {
    uint8_t seq[0xBF8] = {};
    int64_t elapsed = 33'333'333;
    uint32_t entity;
    KilnRegisters r{};
    SeqFrame(uint32_t e, uint16_t trigger, uint32_t item) : entity(e) {
        std::memcpy(seq + 0x18, &trigger, 2);
        std::memcpy(seq + 0x38, &item, 4);
        r.rax = reinterpret_cast<uintptr_t>(&elapsed);
        r.rbx = reinterpret_cast<uintptr_t>(&entity);
        r.rdi = reinterpret_cast<uintptr_t>(seq);
    }
};

static void sequence_hook() {
    World w;
    bool any = false;
    // nothing set: never touched, not even counted
    {
        SeqFrame f(3, kPrimary, 0xAA);
        seq_clock(f.r, w.L, w.env(kAuthority, kProgSim), w.counters);
        CHECK(f.elapsed == 33'333'333 && w.counters.unmarked == 0);
    }
    const float d[3] = {2.0f, 1.0f, 4.0f};
    CHECK(w.rates.set(d, nullptr, 0, &any) && any);
    // authority: every player's classified action
    {
        SeqFrame f(3, kPrimary, 0xAA);
        seq_clock(f.r, w.L, w.env(kAuthority, kProgSim), w.counters);
        CHECK(f.elapsed == 66'666'666 && w.counters.seq == 1);
        CHECK(w.records.recent(kAuthority, 3, 100) == 2.0f && w.records.recent(kAuthority, 3, 101) == 2.0f);
        CHECK(w.records.recent(kAuthority, 3, 102) == 1.0f);  // two steps later: gone
        CHECK(w.records.recent(kPrediction, 3, 100) == 1.0f);  // runs are kept apart
    }
    {
        SeqFrame f(9, kToolTrigger, 0xCC);  // a tool swing
        seq_clock(f.r, w.L, w.env(kAuthority, kProgSim), w.counters);
        CHECK(f.elapsed == 133'333'332);
    }
    // unknown routes keep their timing, and record 1 (the graph then stays too)
    for (auto [trigger, item] : {std::pair<uint16_t, uint32_t>{8, 0xAA}, {kPrimary, 0x1234}, {kPrimary, 0}}) {
        SeqFrame f(3, trigger, item);
        seq_clock(f.r, w.L, w.env(kAuthority, kProgSim, 200), w.counters);
        CHECK(f.elapsed == 33'333'333 && w.records.recent(kAuthority, 3, 200) == 1.0f);
    }
    // NPCs and creatures: never
    {
        SeqFrame f(5000, kPrimary, 0xAA);
        seq_clock(f.r, w.L, w.env(kAuthority, kProgSim), w.counters);
        CHECK(f.elapsed == 33'333'333);
    }
    // a run without a wrap's mark (or another program's mark): left alone and counted
    {
        SeqFrame f(3, kPrimary, 0xAA);
        seq_clock(f.r, w.L, w.env(kScopeNone, kProgNone), w.counters);
        seq_clock(f.r, w.L, w.env(kAuthority, kProgGraph), w.counters);
        CHECK(f.elapsed == 33'333'333 && w.counters.unmarked == 2);
    }
    // prediction and presentation: the local player only
    {
        SeqFrame mine(3, kPrimary, 0xAA), other(4, kPrimary, 0xAA), none(3, kPrimary, 0xAA);
        seq_clock(mine.r, w.L, w.env(kPrediction, kProgSim, 100, 3), w.counters);
        seq_clock(other.r, w.L, w.env(kPresentation, kProgSim, 100, 3), w.counters);
        seq_clock(none.r, w.L, w.env(kPrediction, kProgSim, 100, 0), w.counters);  // no local player known
        CHECK(mine.elapsed == 66'666'666 && other.elapsed == 33'333'333 && none.elapsed == 33'333'333);
    }
    // the server exe's contract: the ActionSequence in rsi
    {
        World s;
        s.L.seq_reg = 6;
        CHECK(s.rates.set(d, nullptr, 0, &any));
        SeqFrame f(3, kPrimary, 0xAA);
        f.r.rsi = f.r.rdi;
        f.r.rdi = 0x1234;  // the delta's register on the server exe: never read
        seq_clock(f.r, s.L, s.env(kAuthority, kProgSim), s.counters);
        CHECK(f.elapsed == 66'666'666);
    }
    // a bow shot needs the actor's ranged weapon type to be a bow
    {
        const float b[3] = {1.0f, 3.0f, 1.0f};
        CHECK(w.rates.set(b, nullptr, 0, &any));
        SeqFrame f(3, kPrimary, 0xBB);
        seq_clock(f.r, w.L, w.env(kAuthority, kProgSim), w.counters);
        CHECK(f.elapsed == 33'333'333);  // type unknown
        w.ranged.put(3, 1);              // a staff
        seq_clock(f.r, w.L, w.env(kAuthority, kProgSim), w.counters);
        CHECK(f.elapsed == 33'333'333);
        w.ranged.put(3, kWeaponBow);
        seq_clock(f.r, w.L, w.env(kAuthority, kProgSim), w.counters);
        CHECK(f.elapsed == 99'999'999);
    }
}

static void graph_hooks() {
    World w;
    bool any = false;
    const float d[3] = {2.5f, 1.0f, 1.0f};
    CHECK(w.rates.set(d, nullptr, 0, &any));
    uint8_t frame[0x200] = {};
    uint8_t instance[0x600] = {}, other[0x600] = {};
    const uintptr_t rbp = reinterpret_cast<uintptr_t>(frame) + 0x100;
    auto bind = [&](uint8_t scope, uint32_t actor, uint8_t* inst, uint32_t step, uint32_t local, uint32_t now) {
        std::memcpy(reinterpret_cast<uint8_t*>(rbp) + (scope == kPresentation ? 0x14 : -0x7C), &actor, 4);
        KilnRegisters r{};
        r.rax = reinterpret_cast<uintptr_t>(inst);
        r.rbp = rbp;
        Env e = w.env(scope, kProgGraph, step, local);
        e.now_ms = now;
        graph_bind(r, w.L, e);
    };
    auto eval = [&](uint8_t* inst, uint8_t pass, float dt, uint32_t now) {
        KilnRegisters r{};
        r.rcx = reinterpret_cast<uintptr_t>(inst);
        r.r8 = 0xABCD00 | pass;  // only the low byte is the pass
        std::memcpy(r.xmm3.bytes, &dt, 4);
        const float hi = 7.0f;   // the rest of xmm3 is left as it was
        std::memcpy(r.xmm3.bytes + 4, &hi, 4);
        Env e = w.env(kScopeNone, kProgNone);
        e.now_ms = now;
        graph_eval(r, e, w.counters);
        float out, rest;
        std::memcpy(&out, r.xmm3.bytes, 4);
        std::memcpy(&rest, r.xmm3.bytes + 4, 4);
        CHECK(rest == 7.0f);
        return out;
    };
    // the sequence hook saw player 3's melee swing this step: its graph binds at 2.5x, the evaluation (a job, later) scales
    w.records.put(kAuthority, 3, 2.5f, 50);
    bind(kAuthority, 3, instance, 50, 0, 1000);
    CHECK(eval(instance, 0, 0.02f, 1010) == 0.02f * 2.5f && w.counters.graph == 1);
    CHECK(eval(instance, 1, 0.02f, 1010) == 0.02f);  // a later pass reuses the game's cached (already scaled) delta
    CHECK(eval(other, 0, 0.02f, 1010) == 0.02f);     // another graph: untouched
    // the previous step's record still counts (the graph update may run first), not an older one
    bind(kAuthority, 3, instance, 51, 0, 1030);
    CHECK(eval(instance, 0, 0.02f, 1040) == 0.02f * 2.5f);
    bind(kAuthority, 3, instance, 52, 0, 1060);
    CHECK(eval(instance, 0, 0.02f, 1070) == 0.02f);
    // the action ended (the sequence hook recorded 1): the next binding clears the instance
    w.records.put(kAuthority, 3, 2.5f, 60);
    bind(kAuthority, 3, instance, 60, 0, 2000);
    w.records.put(kAuthority, 3, 1.0f, 61);
    bind(kAuthority, 3, instance, 61, 0, 2030);
    CHECK(eval(instance, 0, 0.02f, 2040) == 0.02f);
    // an instance whose binding is old is not scaled (a stale entry of a graph that went away)
    w.records.put(kAuthority, 3, 2.5f, 70);
    bind(kAuthority, 3, instance, 70, 0, 3000);
    CHECK(eval(instance, 0, 0.02f, 3000 + Bindings::kMaxAgeMs + 1) == 0.02f);
    // a creature's graph reusing that instance clears it at its own binding
    w.records.put(kAuthority, 3, 2.5f, 80);
    bind(kAuthority, 3, instance, 80, 0, 4000);
    bind(kAuthority, 6000, instance, 80, 0, 4001);
    CHECK(eval(instance, 0, 0.02f, 4002) == 0.02f);
    // presentation: the client program's frame slot, the local player only
    w.records.put(kPresentation, 3, 2.5f, 90);
    bind(kPresentation, 3, other, 90, 3, 5000);
    CHECK(eval(other, 0, 0.02f, 5001) == 0.02f * 2.5f);
    w.records.put(kPresentation, 4, 2.5f, 90);
    bind(kPresentation, 4, instance, 90, 3, 5000);
    CHECK(eval(instance, 0, 0.02f, 5001) == 0.02f);
    // nothing set any more: no work at all
    CHECK(w.rates.set(kOnes, nullptr, 0, &any) && !any);
    CHECK(eval(other, 0, 0.02f, 5002) == 0.02f);
}

static void bindings_capacity_and_threads() {
    Bindings b;
    std::vector<uint64_t> objs(Bindings::kSlots + 8);
    uint32_t stored = 0;
    for (auto& o : objs) stored += b.put(reinterpret_cast<uintptr_t>(&o), 2.0f, 100);
    CHECK(stored == Bindings::kSlots);  // full: the rest are not scaled, nothing breaks
    CHECK(b.get(reinterpret_cast<uintptr_t>(&objs[0]), 100) == 2.0f);
    // after kReuseAgeMs, stale slots are taken over by new graphs
    uint64_t late = 0;
    CHECK(b.put(reinterpret_cast<uintptr_t>(&late), 3.0f, 100 + Bindings::kReuseAgeMs + 1));
    CHECK(b.get(reinterpret_cast<uintptr_t>(&late), 100 + Bindings::kReuseAgeMs + 2) == 3.0f);
    // bindings and evaluations on several threads (workers bind, jobs evaluate)
    Bindings c;
    std::vector<uint64_t> inst(64);
    std::vector<std::thread> ts;
    for (int t = 0; t < 4; ++t)
        ts.emplace_back([&, t] {
            for (int round = 0; round < 2000; ++round)
                for (size_t i = t; i < inst.size(); i += 4) {
                    c.put(reinterpret_cast<uintptr_t>(&inst[i]), (round & 1) ? 1.0f : 2.0f, 10);
                    const float r = c.get(reinterpret_cast<uintptr_t>(&inst[i]), 10);
                    if (r != 1.0f && r != 2.0f) ++g_fail;
                }
        });
    for (auto& t : ts) t.join();
}

// The game's ranged state machine (client 0x478490, docs/re/action-speed.md): it copies the parameters into its own
// state only on a state change from Holster, or to a lower state. Kiln divides the fresh block every step; a draw keeps
// what it latched at its start.
struct RangedState {
    uint8_t state = 0;  // 0 Holster, 1 Aim, 2 AimAndCharge, 3 Fire, 4 FireEaseOut
    int64_t charge = 0, fire = 0;
    void step(uint8_t next, int64_t fresh_charge, int64_t fresh_fire) {
        if (next != state && (state == 0 || next < state)) charge = fresh_charge, fire = fresh_fire;
        state = next;
    }
};

static void bow_hook() {
    World w;
    bool any = false;
    uint8_t frame[0x400] = {};
    const uintptr_t rbp = reinterpret_cast<uintptr_t>(frame);
    uint8_t* params = frame + 0x1B0;
    uint32_t entity = 3;
    auto fresh = [&](uint8_t type, int64_t charge, int64_t fire) {
        params[0] = type;
        std::memcpy(params + 8, &charge, 8);
        std::memcpy(params + 0x10, &fire, 8);
    };
    auto run = [&](uint8_t scope, uint32_t local = 0, uintptr_t rax_off = 0x1B0) {
        KilnRegisters r{};
        r.rbp = rbp;
        r.rax = rbp + rax_off;
        r.r12 = reinterpret_cast<uintptr_t>(&entity);
        bow_timing(r, w.L, w.env(scope, kProgRanged, 100, local), w.counters);
        int64_t c, f;
        std::memcpy(&c, params + 8, 8);
        std::memcpy(&f, params + 0x10, 8);
        return std::pair<int64_t, int64_t>{c, f};
    };
    const int64_t C = 1'000'000'000, F = 400'000'000;
    fresh(kWeaponBow, C, F);
    CHECK(run(kAuthority) == std::make_pair(C, F));  // nothing set
    const float d[3] = {1.0f, 2.0f, 1.0f};
    CHECK(w.rates.set(d, nullptr, 0, &any));
    fresh(kWeaponBow, C, F);
    CHECK(run(kAuthority) == std::make_pair(C / 2, F / 2) && w.counters.bow == 1);
    CHECK(w.ranged.get(3) == kWeaponBow);
    // staffs and wands: left to the game (and remembered, so their shots are never Bow)
    fresh(1, C, F);
    CHECK(run(kAuthority) == std::make_pair(C, F) && w.ranged.get(3) == 1);
    // prediction: the local player only; presentation never runs this program
    fresh(kWeaponBow, C, F);
    CHECK(run(kPrediction, 3) == std::make_pair(C / 2, F / 2));
    fresh(kWeaponBow, C, F);
    CHECK(run(kPrediction, 4) == std::make_pair(C, F));
    fresh(kWeaponBow, C, F);
    CHECK(run(kPresentation, 3) == std::make_pair(C, F));
    // rax is not the frame's parameter block: another layout, left alone
    fresh(kWeaponBow, C, F);
    CHECK(run(kAuthority, 0, 0x1B8) == std::make_pair(C, F));
    // a draw in progress keeps its durations: rate 1 at the draw's start, 2 halfway through
    {
        World g;
        RangedState sm;
        auto step = [&](uint8_t state, float bow) {
            const float r[3] = {1.0f, bow, 1.0f};
            g.rates.set(r, nullptr, 0, &any);
            fresh(kWeaponBow, C, F);
            KilnRegisters regs{};
            regs.rbp = rbp, regs.rax = rbp + 0x1B0, regs.r12 = reinterpret_cast<uintptr_t>(&entity);
            bow_timing(regs, g.L, g.env(kAuthority, kProgRanged), g.counters);
            int64_t c, f;
            std::memcpy(&c, params + 8, 8);
            std::memcpy(&f, params + 0x10, 8);
            sm.step(state, c, f);
        };
        step(1, 1.0f);  // aim: latched at rate 1
        step(2, 1.0f);  // charging
        CHECK(sm.charge == C);
        step(2, 2.0f);  // the rate changes mid-draw: the running draw keeps C
        CHECK(sm.charge == C);
        step(3, 2.0f);  // fire (a higher state: not latched)
        step(0, 2.0f);  // holster
        step(1, 2.0f);  // the next draw starts at the new rate
        CHECK(sm.charge == C / 2 && sm.fire == F / 2);
    }
}


// ---- CATT's lab numbers for bows: 1.5x on a 1.0 s charge and 0.5 s fire gave 0.667 s and 0.333 s ----
static void bow_one_and_a_half() {
    CHECK(divide_duration(1'000'000'000, 1.5f) == 666'666'667);
    CHECK(divide_duration(500'000'000, 1.5f) == 333'333'333);
}

// ---- the sampler's logic (not bound to the game in this release) ----
namespace ks = kc::sampler;

// A box swept from angle 0 to 90 degrees around the actor during the swing, measured only at the two step boundaries.
// Poses between them are estimates (position lerped, rotation slerped), so they follow the chord between the measured
// frames: the target sits on that chord's middle, 0.71 m from either end. Only a sample near the middle touches it.
static ks::Pose arc_pose(double frac) {
    ks::Pose p;
    const double a = frac * 1.5707963267948966;
    p.pos[0] = std::cos(a), p.pos[2] = std::sin(a);
    const double h = a / 2;
    p.rot[1] = static_cast<float>(std::sin(h)), p.rot[3] = static_cast<float>(std::cos(h));
    return p;
}
static bool touches(const ks::Pose& p) {
    const double tx = 0.5, tz = 0.5;
    const double dx = p.pos[0] - tx, dz = p.pos[2] - tz;
    return dx * dx + dz * dz < 0.15 * 0.15;  // the box's reach around the target
}

static void sampler() {
    const int64_t ms = 1'000'000;
    // a 41 ms server tick at 10x: 410 ms of sequence per step
    const ks::Interval s1{0, 410 * ms}, s2{410 * ms, 820 * ms}, s3{820 * ms, 1230 * ms};
    // a swing whose hit window (68 ms) starts and ends inside one tick: sampled whole, in that step
    ks::Interval w = ks::window_part(s1, 120 * ms, 68 * ms, true);
    CHECK(w.lo == 120 * ms && w.hi == 188 * ms);
    CHECK(ks::window_part(s2, 120 * ms, 68 * ms, false).empty());
    // a window shorter than two ticks across a step boundary: in two parts
    CHECK(ks::window_part(s1, 380 * ms, 68 * ms, true).lo == 380 * ms && ks::window_part(s1, 380 * ms, 68 * ms, true).hi == 410 * ms);
    w = ks::window_part(s2, 380 * ms, 68 * ms, false);
    CHECK(w.lo == 410 * ms && w.hi == 448 * ms);
    // a collider that appears one step late samples its window from the start on its first sweep
    w = ks::window_part(s2, 380 * ms, 68 * ms, true);
    CHECK(w.lo == 380 * ms && w.hi == 448 * ms);
    // ... and one that appears after its window closed (two steps late) still samples it once, from the pose history;
    // its later sweeps sample nothing
    w = ks::window_part(s3, 380 * ms, 68 * ms, true);
    CHECK(w.lo == 380 * ms && w.hi == 448 * ms);
    CHECK(ks::window_part(s3, 380 * ms, 68 * ms, false).empty());
    // 1x for comparison: a 41 ms tick, the same window spans two steps of the slow swing
    CHECK(ks::window_part(ks::Interval{0, 41 * ms}, 20 * ms, 68 * ms, true).hi == 41 * ms);
    CHECK(ks::window_part(ks::Interval{0, 41 * ms}, 50 * ms, 68 * ms, true).empty());
    CHECK(ks::window_part(s1, 120 * ms, 0, true).empty() && ks::window_part(ks::Interval{5, 5}, 0, 10, true).empty());

    // sample times: evenly inside the part, capped, at least one
    int64_t t[ks::kMaxSamples];
    CHECK(ks::sample_times(ks::Interval{120 * ms, 188 * ms}, 10 * ms, 10, t) == 7);
    CHECK(t[0] > 120 * ms && t[6] < 188 * ms);
    CHECK(ks::sample_times(ks::Interval{0, 1000 * ms}, 1 * ms, 50, t) == ks::kMaxSamples);  // capped at 10
    CHECK(ks::sample_times(ks::Interval{0, 2 * ms}, 10 * ms, 10, t) == 1 && t[0] == 1 * ms);
    CHECK(ks::sample_times(ks::Interval{}, 10 * ms, 10, t) == 0);

    // poses between measured frames: at 10x the hand is measured at the step boundaries only (0 and 410 ms of sequence:
    // the start and the end of the arc). Both endpoints miss the target; the samples inside the window hit it.
    ks::PoseHistory h;
    h.push(0, arc_pose(0.0));
    h.push(410 * ms, arc_pose(1.0));
    ks::Pose p;
    CHECK(h.at(0, &p) && !touches(p));
    CHECK(h.at(410 * ms, &p) && !touches(p));
    // the window [180, 230] ms covers the middle of the arc (205 ms)
    const uint32_t n = ks::sample_times(ks::window_part(s1, 180 * ms, 50 * ms, true), 5 * ms, 10, t);
    uint32_t hits = 0;
    for (uint32_t i = 0; i < n; ++i) hits += h.at(t[i], &p) && touches(p);
    CHECK(n == 10 && hits >= 1);
    // slerp keeps unit quaternions and takes the short way round
    ks::Pose a, b;
    b.rot[3] = -1.0f;  // the same rotation as a, the other sign
    ks::Pose mid = ks::interpolate(a, b, 0.5f);
    CHECK(std::fabs(std::fabs(mid.rot[3]) - 1.0f) < 1e-5f);
    // the history keeps the last kHistory steps and answers between them
    ks::PoseHistory g;
    for (int i = 0; i < 9; ++i) g.push(i * 100, arc_pose(i / 8.0));
    CHECK(g.size() == ks::kHistory && g.at(250, &p) && g.at(-5, &p));
    g.push(50, arc_pose(0));  // a rewind (a new sequence): the history starts over
    CHECK(g.size() == 1);

    // merge: distinct targets only (the stream's and the reversed pair), within the stream's capacity
    ks::Contact stream[4] = {};
    uint32_t count = 1;
    stream[0].impact = 900, stream[0].target = 17;
    ks::Contact pend[5] = {};
    pend[0].impact = 900, pend[0].target = 17;   // already in the stream
    pend[1].impact = 17, pend[1].target = 900;   // the same pair, reversed
    pend[2].impact = 900, pend[2].target = 18;   // new
    pend[3].impact = 900, pend[3].target = 18;   // twice in the pending list
    pend[4].impact = 900, pend[4].target = 19;   // new
    CHECK(ks::merge(stream, &count, 4, pend, 5) == 2 && count == 3 && stream[1].target == 18 && stream[2].target == 19);
    pend[0].target = 20, pend[1].impact = 900, pend[1].target = 21;
    CHECK(ks::merge(stream, &count, 4, pend, 2) == 1 && count == 4);  // full: the rest is dropped, nothing reallocated
    // pending contacts per impact: bounded, taken once
    static ks::Pending pending;
    pending.clear();
    ks::Contact c;
    c.impact = 900;
    for (uint32_t i = 0; i < ks::Pending::kPerImpact + 3; ++i) c.target = 100 + i, pending.add(c);
    uint32_t got = 0;
    pending.take(900, [&](const ks::Contact*, uint32_t k) { got = k; });
    CHECK(got == ks::Pending::kPerImpact && pending.impacts() == 0);
    for (uint32_t i = 0; i < ks::Pending::kImpacts + 2; ++i) c.impact = 1000 + i, c.target = 1, pending.add(c);
    CHECK(pending.impacts() == ks::Pending::kImpacts);
}

// ---- cost: one server step at 10x with 4 players attacking (the four hook bodies, as kiln-combat runs them) ----
static void cost() {
    World w;
    bool any = false;
    const float d[3] = {10.0f, 10.0f, 10.0f};
    w.rates.set(d, nullptr, 0, &any);
    SeqFrame f[4] = {{1, kPrimary, 0xAA}, {2, kHeavy, 0xAA}, {3, kToolTrigger, 0xCC}, {4, kPrimary, 0xBB}};
    uint8_t frame[0x400] = {};
    uint8_t instance[4][16] = {};
    const uintptr_t rbp = reinterpret_cast<uintptr_t>(frame) + 0x100;
    for (uint32_t e = 1; e <= 4; ++e) w.ranged.put(e, kWeaponBow);
    constexpr int kSteps = 200'000;
    const auto t0 = std::chrono::steady_clock::now();
    for (int step = 0; step < kSteps; ++step) {
        for (int i = 0; i < 4; ++i) {
            f[i].elapsed = 41'000'000;
            seq_clock(f[i].r, w.L, w.env(kAuthority, kProgSim, step), w.counters);
            uint32_t actor = i + 1;
            std::memcpy(reinterpret_cast<uint8_t*>(rbp) - 0x7C, &actor, 4);
            KilnRegisters b{};
            b.rax = reinterpret_cast<uintptr_t>(instance[i]), b.rbp = rbp;
            Env ge = w.env(kAuthority, kProgGraph, step);
            ge.now_ms = 1000 + static_cast<uint32_t>(step / 24);
            graph_bind(b, w.L, ge);
            KilnRegisters e{};
            e.rcx = b.rax;
            const float dt = 0.041f;
            std::memcpy(e.xmm3.bytes, &dt, 4);
            graph_eval(e, ge, w.counters);
        }
    }
    const double ns = std::chrono::duration<double, std::nano>(std::chrono::steady_clock::now() - t0).count() / kSteps;
    std::printf("speed: cost of one 10x step with 4 attacking players (sequence + graph bind + evaluation each): %.0f ns\n", ns);
    CHECK(ns < 20'000);  // a generous bound for a sanitizer build; the release build is far below
    CHECK(w.counters.seq == static_cast<uint64_t>(kSteps) * 4);
}

int main() {
    rate_table();
    routes();
    arithmetic();
    sequence_hook();
    graph_hooks();
    bindings_capacity_and_threads();
    bow_hook();
    bow_one_and_a_half();
    sampler();
    cost();
    if (g_fail) {
        std::printf("speed: %d check(s) failed\n", g_fail);
        return 1;
    }
    std::printf("speed: all checks passed\n");
    return 0;
}
