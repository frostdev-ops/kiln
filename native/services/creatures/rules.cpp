// SPDX-License-Identifier: MIT
// kiln.creatures: what runs on the game's job threads for rules and overrides, and the table the tick publishes for it.
//
// The arsenal's constraints and actions are BlobVariants dispatched on a type hash by two functions; an unknown hash
// is false or a no-op. Kiln's kinds carry the hashes of kiln::npc::Constraint and kiln::npc::Action, so without these
// hooks they do nothing, safely.
//
// Threads. ConstraintEval and ActionExec run on job threads inside the sim step, where the tick API faults. So nothing
// here calls anything of the tick API or managed code:
//  - a condition reads the snapshot the tick published before the step (conditions, buffs, phases, overrides);
//  - an action is queued; the tick hands it to its owner in the next Pre (KILN_RULE_WORK_ACTION);
//  - a creature with no row yet asks for one through the same queue, and reads as false until it has it.
// A snapshot is rebuilt in Pre, when no job runs, and swapped with one atomic store; job threads never see a half-built
// one. Three buffers, because a reader of the previous snapshot may in principle still be returning.
//
// Attack overrides need three more hooks (docs/re/npc-internals.md 4.4 has the start paths): SelectAttack and
// AttackAvailable, so that the game's own selection picks the forced attack with all its bookkeeping; the follow-up
// lookup, so that a combo cannot start another attack; and SetCurrentAttack as the one place every attack passes, which
// counts the starts and corrects anything that got past the others.
//
// Hooks go in at first use and come out when nothing uses them, so a server without rule mods runs none of this.
#include <windows.h>

#include <intrin.h>

#include <cstdio>
#include <deque>

#include "internal.h"
#include "safetyhook.hpp"
#include "services/kiln_time.h"

namespace kcr {

namespace {

constexpr uint32_t kSlots = 256;
constexpr uint32_t kWords = kSlots / 64;
constexpr uint32_t kRowBuffs = 12;
constexpr uint32_t kHandlers = 64;
constexpr size_t kMaxQueue = 8192;
constexpr uint32_t kRefreshSteps = 5;  // buffs, phases and the hour are re-read this often
constexpr uint64_t kGoneCheckSteps = 25;
constexpr uint8_t kNoAttack = 0x1f;  // SelectAttack's "nothing available"
constexpr uint32_t kPlaySequenceHash = fnv1a("keen::enemy01::PlayActionSequenceAction");

struct Row {
    KilnEntity e = 0;
    uint32_t allow = ~0u;
    int16_t forced = -1;
    uint8_t has_override = 0;
    uint8_t nbuffs = 0;
    uint32_t phase = 0, game_phase = 0;
    uint32_t buffs[kRowBuffs] = {};
    uint64_t known[kWords] = {}, value[kWords] = {};
};

struct Snapshot {
    std::vector<Row> rows;
    uint32_t mask = 0, count = 0;
};
Snapshot g_snap[3];
uint32_t g_next = 0;
std::atomic<const Snapshot*> g_pub{nullptr};
std::atomic<float> g_hour{-1.f};
std::atomic<bool> g_any_override{false};

struct Owned {
    KilnOwner owner;
    int32_t forced;
    uint32_t allow;
};
struct Track {
    uint32_t phase = 0, game_phase = 0;
    std::vector<Owned> overrides;  // the newest wins
    std::vector<uint32_t> buffs;
    uint64_t known[kWords] = {}, value[kWords] = {}, asked[kWords] = {};
    bool pick_asked = false;
};
std::unordered_map<KilnEntity, Track> g_tracks;
bool g_dirty = false;

enum WantKind : uint32_t { W_TRACK, W_CONDITION, W_PICK, W_ACTION, W_PHASE };
struct Want {
    uint32_t kind;
    KilnEntity e;
    uint32_t id;
    KilnEntity target;
    uint64_t life = 0, target_life = 0;
};
std::mutex g_want_mu;
std::vector<Want> g_want;

struct Work {
    KilnRuleWork work;
    uint64_t life, target_life;
};
std::deque<Work> g_work;  // tick only; single-item drains must not move the rest of the queue
std::atomic<uint32_t> g_pump{0};

// Same entry observer and monotonic lifetimes as npcai. Its entity_lifetime API is tick-only, but actions originate
// on jobs (including newly spawned creatures), so capture here under the queue lock instead of calling that API.
std::unordered_map<KilnEntity, uint64_t> g_lives;  // protected by g_want_mu, including the destroy observer
uint64_t g_next_life = 1;  // never reset across worlds
std::atomic<uintptr_t> g_life_storage{0};
safetyhook::MidHook g_life_destroy;
bool g_life_warned = false;

uint64_t capture_life(KilnEntity e) {
    if (!e || !g_life_storage.load(std::memory_order_acquire)) return 0;
    auto [it, fresh] = g_lives.try_emplace(e, 0);
    if (fresh) it->second = g_next_life++;
    return it->second;
}

void hk_life_destroy(safetyhook::Context& ctx) {
    const auto storage = g_life_storage.load(std::memory_order_acquire);
    if (!storage || ctx.rcx != storage) return;
    std::lock_guard lock(g_want_mu);
    g_lives.erase(static_cast<KilnEntity>(ctx.rdx));
}

void clear_rule_work() {
    g_life_storage.store(0, std::memory_order_release);
    std::lock_guard lock(g_want_mu);
    g_lives.clear();
    g_want.clear();
    g_work.clear();
}

void KILN_CALL on_rules_world(const KilnWorldEvent* ev, void*) {
    if (ev->kind == KILN_WORLD_SERVER && ev->event == KILN_WORLD_STOPPING) clear_rule_work();
}

std::atomic<uint64_t> g_slot_eval[kSlots], g_slot_true[kSlots];

struct Stats {
    std::atomic<uint64_t> constraints{0}, constraints_true{0}, actions{0}, actions_dropped{0}, selects{0}, selects_forced{0},
        selects_filtered{0}, picks_waiting{0}, followups_refused{0}, starts{0}, starts_off{0}, sequences_refused{0},
        pre_ns{0}, pre_calls{0}, job_ticks{0}, job_calls{0};
} S;
std::atomic<uint32_t> g_timing{0};
uint64_t g_tick_hz = 0;

struct Handler {
    std::atomic<KilnRuleJobFn> fn{nullptr};
    std::atomic<void*> user{nullptr};
    KilnOwner owner = 0;
};
Handler g_handlers[kHandlers];

// Arsenals whose attack selection waits for an override. Written on the tick, read on job threads.
constexpr uint32_t kMaxPick = 32;
struct PickEntry {
    uint64_t lo = 0, hi = 0;
    KilnOwner owner = 0;
};
PickEntry g_pick[kMaxPick];
std::atomic<uint32_t> g_pick_n{0};

// Targets being held for a creature.
struct Prefer {
    KilnOwner owner;
    KilnEntity target;
    uint64_t until_ms;
};
std::unordered_map<KilnEntity, Prefer> g_prefer;

// AI switch: creatures Kiln turned off, with the owners who want it so.
std::unordered_map<KilnEntity, std::vector<KilnOwner>> g_ai_off;

// Layout read from the game's reflection at resolve time.
int64_t g_off_enabled = -1, g_off_behavior_index = -1, g_off_next_target_change = -1, g_off_target_entity = -1, g_off_target_counter = -1;
int64_t g_off_phase = -1;
struct BuffList {
    int64_t first = -1;
    uint32_t count = 0;
};
BuffList g_buff_lists[5];
int64_t g_buff_stride = 12, g_buff_cleansed = 4;
constexpr uint32_t kEnemyPhasesDyn = ecs_hash("DynamicEnemyPhases"), kBuffContainer = ecs_hash("BuffContainer"),
                   kAggroDyn = ecs_hash("DynamicAggro");

const KilnTimeApi* g_time = nullptr;
bool g_time_warned = false;

// ---- hooks ----

using ConstraintFn = uint8_t* (*)(uint8_t*, uint8_t*, uint32_t*, void*);
using ActionFn = uint64_t (*)(uint8_t*, uint32_t*, uint32_t*, uint64_t);
using SelectFn = uint8_t* (*)(uint8_t*, uint8_t*, uint64_t);
using AvailableFn = uint32_t (*)(uint8_t*, uint64_t);
using FindFn = int64_t* (*)(int64_t*, uint8_t*, uint32_t);
using BeginFn = void (*)(uint8_t*, const uint64_t*, uint32_t, uint64_t);
using TargetFn = uint32_t (*)(uint8_t*);

safetyhook::InlineHook g_h_constraint, g_h_action, g_h_select, g_h_available, g_h_find;
safetyhook::MidHook g_h_start;
ConstraintFn g_o_constraint = nullptr;
ActionFn g_o_action = nullptr;
SelectFn g_o_select = nullptr;
AvailableFn g_o_available = nullptr;
FindFn g_o_find = nullptr;
BeginFn g_begin = nullptr;
TargetFn g_current_target = nullptr;
bool g_rule_hooks = false, g_attack_hooks = false;
uintptr_t g_s_constraint = 0, g_s_action = 0, g_s_select = 0, g_s_available = 0, g_s_find = 0, g_s_start = 0;
int64_t g_ctx_arsenal = -1;

struct Pick {
    bool active = false;
    int32_t forced = -1;
    uint32_t allow = ~0u;
};
thread_local Pick t_pick;

inline KilnEntity ctx_entity(uint8_t* ctx) {
    uintptr_t program = 0;
    std::memcpy(&program, ctx + L.ctx_program, 8);
    uint32_t e = 0;
    seh_current_entity(reinterpret_cast<void*>(program), &e);
    return e;
}

inline const Row* row_of(KilnEntity e) {
    const Snapshot* s = g_pub.load(std::memory_order_acquire);
    if (!s || !s->count || !e) return nullptr;
    uint32_t i = (e * 2654435761u) & s->mask;
    for (;;) {
        const Row& r = s->rows[i];
        if (r.e == e) return &r;
        if (!r.e) return nullptr;
        i = (i + 1) & s->mask;
    }
}

void want(uint32_t kind, KilnEntity e, uint32_t id = 0, KilnEntity target = 0) {
    std::lock_guard lock(g_want_mu);
    if (g_want.size() >= kMaxQueue) {
        if (kind == W_ACTION) S.actions_dropped.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    Want w{kind, e, id, target};
    if (kind == W_ACTION) {
        w.life = capture_life(e);
        w.target_life = capture_life(target);
    }
    g_want.push_back(w);
}

inline bool hour_in(float h, float from, float to) {
    if (h < 0) return false;
    return from <= to ? (h >= from && h < to) : (h >= from || h < to);
}

bool eval_kiln(uint8_t* ctx, const RuleCond* c) {
    switch (c->kind) {
        case KILN_RC_CONDITION: {
            const uint32_t slot = c->slot;
            if (slot >= kSlots) return false;
            const KilnEntity e = ctx_entity(ctx);
            const Row* r = row_of(e);
            g_slot_eval[slot].fetch_add(1, std::memory_order_relaxed);
            const uint32_t w = slot / 64;
            const uint64_t bit = 1ull << (slot % 64);
            if (!r || !(r->known[w] & bit)) {
                want(W_CONDITION, e, slot);
                return false;
            }
            const bool v = (r->value[w] & bit) != 0;
            if (v) g_slot_true[slot].fetch_add(1, std::memory_order_relaxed);
            return v;
        }
        case KILN_RC_BUFF: {
            const KilnEntity e = ctx_entity(ctx);
            const Row* r = row_of(e);
            if (!r) {
                want(W_TRACK, e);
                return false;
            }
            for (uint32_t i = 0; i < r->nbuffs; ++i)
                if (r->buffs[i] == c->id) return true;
            return false;
        }
        case KILN_RC_TIME: return hour_in(g_hour.load(std::memory_order_relaxed), c->a, c->b);
        case KILN_RC_PHASE: {
            const KilnEntity e = ctx_entity(ctx);
            const Row* r = row_of(e);
            if (!r) {
                want(W_TRACK, e);
                return c->id == 0;
            }
            return r->phase == c->id || (c->id && r->game_phase == c->id);
        }
        case KILN_RC_JOB: {
            if (c->id >= kHandlers) return false;
            const KilnRuleJobFn fn = g_handlers[c->id].fn.load(std::memory_order_acquire);
            if (!fn) return false;
            return fn(ctx_entity(ctx), c->id, c->slot, g_handlers[c->id].user.load(std::memory_order_relaxed)) != 0;
        }
        default: return false;
    }
}

uint8_t* hk_constraint(uint8_t* out, uint8_t* ctx, uint32_t* variant, void* extra) {
    if (*variant != kRuleConstraintHash) return g_o_constraint(out, ctx, variant, extra);
    const bool timing = g_timing.load(std::memory_order_relaxed) != 0;
    const uint64_t t0 = timing ? __rdtsc() : 0;
    const int32_t rel = static_cast<int32_t>(variant[1]);
    const auto* c = reinterpret_cast<const RuleCond*>(reinterpret_cast<const uint8_t*>(variant + 1) + rel);
    const bool v = rel != 0 && eval_kiln(ctx, c);
    S.constraints.fetch_add(1, std::memory_order_relaxed);
    if (v) S.constraints_true.fetch_add(1, std::memory_order_relaxed);
    std::memset(out, 0, 8);
    out[8] = v ? 1 : 0;
    if (timing) {
        S.job_ticks.fetch_add(__rdtsc() - t0, std::memory_order_relaxed);
        S.job_calls.fetch_add(1, std::memory_order_relaxed);
    }
    return out;
}

// The record of attack `index`, or null. Needs the record layout: without it AL is all zeros, and the arsenal's own
// GUID would read as the attack list's offset and count (a wild pointer).
inline uint8_t* attack_at(uint8_t* arsenal, uint32_t index) {
    if (!arsenal || !AL.attack_size) return nullptr;
    int32_t rel = 0;
    uint32_t n = 0;
    std::memcpy(&rel, arsenal + AL.attacks, 4);
    std::memcpy(&n, arsenal + AL.attacks + 4, 4);
    if (index >= n || !rel) return nullptr;
    return arsenal + AL.attacks + rel + static_cast<size_t>(index) * AL.attack_size;
}

inline uint8_t* ctx_arsenal(uint8_t* ctx) {
    uint8_t* a = nullptr;
    std::memcpy(&a, ctx + g_ctx_arsenal, 8);
    return a;
}

// Runs the game's selection so that it picks `index`: its bookkeeping (use counters, cooldown group, speed, the chosen
// target) goes to that attack. AttackAvailable refuses the others; the style goes to one the attack has.
uint8_t* force_select(uint8_t* out, uint8_t* ctx, uint64_t style, int32_t index) {
    uint8_t* attack = attack_at(ctx_arsenal(ctx), static_cast<uint32_t>(index));
    if (!attack) return g_o_select(out, ctx, style);
    uint16_t mask = 0;
    std::memcpy(&mask, attack + AL.attack_style, 2);
    uint64_t st = style & 0xFF;
    if (!((mask >> (st & 15)) & 1) && mask) {
        st = 0;
        while (!((mask >> st) & 1)) ++st;
    }
    uint8_t* res = out;
    t_pick = Pick{true, index, ~0u};
    for (int i = 0; i < 3; ++i) {  // selectionChance can refuse a roll
        res = g_o_select(out, ctx, st);
        if (!out[0] && out[8] == static_cast<uint8_t>(index)) break;
    }
    t_pick = Pick{};
    if (!out[0] && out[8] == static_cast<uint8_t>(index)) {
        S.selects_forced.fetch_add(1, std::memory_order_relaxed);
        return res;
    }
    // Chance 0 or no style bit at all: the loop never reached it. Answer it directly, if there is someone to hit.
    const uint32_t target = g_current_target ? g_current_target(ctx) : 0;
    if (!target) return res;
    float threat = 0;
    std::memcpy(&threat, attack + AL.attack_threat, 4);
    std::memset(out, 0, 8);
    const uint32_t idx = static_cast<uint32_t>(index);
    std::memcpy(out + 8, &idx, 4);
    std::memcpy(out + 12, &threat, 4);
    std::memcpy(out + 16, &target, 4);
    S.selects_forced.fetch_add(1, std::memory_order_relaxed);
    return out;
}

uint8_t* hk_select(uint8_t* out, uint8_t* ctx, uint64_t style) {
    S.selects.fetch_add(1, std::memory_order_relaxed);
    if (!g_any_override.load(std::memory_order_relaxed)) return g_o_select(out, ctx, style);
    const KilnEntity e = ctx_entity(ctx);
    const Row* r = row_of(e);
    if (!r || !r->has_override) {
        const uint32_t n = g_pick_n.load(std::memory_order_acquire);
        uint8_t* arsenal = ctx_arsenal(ctx);
        if (n && arsenal) {
            uint64_t lo = 0, hi = 0;
            std::memcpy(&lo, arsenal, 8);
            std::memcpy(&hi, arsenal + 8, 8);
            for (uint32_t i = 0; i < n; ++i) {
                if (g_pick[i].lo != lo || g_pick[i].hi != hi) continue;
                S.picks_waiting.fetch_add(1, std::memory_order_relaxed);
                want(W_PICK, e);
                std::memset(out, 0, 0x14);  // what the game's own "nothing available" writes
                out[0] = kNoAttack;
                return out;
            }
        }
        return g_o_select(out, ctx, style);
    }
    if (r->forced >= 0) return force_select(out, ctx, style, r->forced);
    if (r->allow == ~0u) return g_o_select(out, ctx, style);
    t_pick = Pick{true, -1, r->allow};
    uint8_t* res = g_o_select(out, ctx, style);
    t_pick = Pick{};
    return res;
}

uint32_t hk_available(uint8_t* ctx, uint64_t index) {
    const Pick p = t_pick;
    if (!p.active) return g_o_available(ctx, index);
    const uint32_t i = static_cast<uint32_t>(index & 0xFF);
    if (p.forced >= 0) {
        if (i != static_cast<uint32_t>(p.forced)) return 0;
        const uint32_t r = g_o_available(ctx, index);
        if (r) return r;
        return g_current_target ? g_current_target(ctx) : 0;  // forced past its range, cooldown and constraints
    }
    if (i < 32 && !((p.allow >> i) & 1)) {
        S.selects_filtered.fetch_add(1, std::memory_order_relaxed);
        return 0;
    }
    return g_o_available(ctx, index);
}

inline bool refused(const Row* r, uint32_t index) {
    if (!r || !r->has_override) return false;
    if (r->forced >= 0) return index != static_cast<uint32_t>(r->forced);
    return index < 32 && !((r->allow >> index) & 1);
}

// The combo path: inside AttackExecution, a sequence's follow-up that names another attack starts that attack without
// any selection. out = { attack record, index }. One caller.
int64_t* hk_find(int64_t* out, uint8_t* ctx, uint32_t hash) {
    int64_t* res = g_o_find(out, ctx, hash);
    if (!g_any_override.load(std::memory_order_relaxed) || !out[0]) return res;
    if (refused(row_of(ctx_entity(ctx)), static_cast<uint32_t>(out[1] & 0xFF))) {
        S.followups_refused.fetch_add(1, std::memory_order_relaxed);
        out[0] = 0;
        out[1] = 0;
    }
    return res;
}

// SetCurrentAttack(ctx, index): every attack that becomes the creature's selected attack goes through here.
void hk_start(safetyhook::Context& c) {
    const uint8_t index = static_cast<uint8_t>(c.rdx & 0xFF);
    if (index == 0xFF) return;
    S.starts.fetch_add(1, std::memory_order_relaxed);
    if (!g_any_override.load(std::memory_order_relaxed)) return;
    const Row* r = row_of(ctx_entity(reinterpret_cast<uint8_t*>(c.rcx)));
    if (!refused(r, index)) return;
    S.starts_off.fetch_add(1, std::memory_order_relaxed);
    if (r->forced >= 0) c.rdx = (c.rdx & ~0xFFull) | static_cast<uint8_t>(r->forced);
}

// A rule of the game's data may play an attack's sequence directly: with an override on, one it refuses does not start.
bool sequence_refused(uint8_t* ctx, uint32_t* variant) {
    const int32_t rel = static_cast<int32_t>(variant[1]);
    if (!rel) return false;
    const auto* guid = reinterpret_cast<const uint8_t*>(variant + 1) + rel;
    const Row* r = row_of(ctx_entity(ctx));
    if (!r || !r->has_override) return false;
    uint8_t* arsenal = ctx_arsenal(ctx);
    for (uint32_t i = 0; i < 64; ++i) {
        const uint8_t* a = attack_at(arsenal, i);
        if (!a) return false;
        if (std::memcmp(a + AL.attack_sequence, guid, 16) == 0) return refused(r, i);
    }
    return false;
}

void run_attack(uint8_t* ctx, const uint8_t guid[16]) {
    uint8_t* arsenal = ctx_arsenal(ctx);
    int32_t index = -1;
    for (uint32_t i = 0; i < 255; ++i) {
        const uint8_t* a = attack_at(arsenal, i);
        if (!a) break;
        if (std::memcmp(a, guid, 16) == 0) {
            index = static_cast<int32_t>(i);
            break;
        }
    }
    if (index < 0 || !g_o_select || !g_begin) return;
    uint8_t out[32] = {};
    force_select(out, ctx, 0, index);
    if (out[0]) return;
    // as the game's SelectAttackAction: the selection's index and target go on to BeginAttack
    uint64_t sel[2] = {};
    std::memcpy(sel, out + 8, 12);
    g_begin(ctx, sel, 0, 1);
}

uint64_t hk_action(uint8_t* ctx, uint32_t* rng, uint32_t* variant, uint64_t first) {
    const uint32_t h = *variant;
    if (h != kRuleActionHash) {
        if (h == kPlaySequenceHash && g_any_override.load(std::memory_order_relaxed) && g_attack_hooks &&
            sequence_refused(ctx, variant)) {
            S.sequences_refused.fetch_add(1, std::memory_order_relaxed);
            return 0;
        }
        return g_o_action(ctx, rng, variant, first);
    }
    const bool timing = g_timing.load(std::memory_order_relaxed) != 0;
    const uint64_t t0 = timing ? __rdtsc() : 0;
    S.actions.fetch_add(1, std::memory_order_relaxed);
    const int32_t rel = static_cast<int32_t>(variant[1]);
    if (rel) {
        const auto* a = reinterpret_cast<const RuleAct*>(reinterpret_cast<const uint8_t*>(variant + 1) + rel);
        switch (a->kind) {
            case KILN_RA_QUEUE:
                want(W_ACTION, ctx_entity(ctx), a->id, g_current_target ? g_current_target(ctx) : 0);
                break;
            case KILN_RA_PHASE: want(W_PHASE, ctx_entity(ctx), a->id); break;
            case KILN_RA_ATTACK: run_attack(ctx, a->guid); break;
            case KILN_RA_JOB:
                if (a->id < kHandlers)
                    if (const KilnRuleJobFn fn = g_handlers[a->id].fn.load(std::memory_order_acquire))
                        fn(ctx_entity(ctx), a->id, a->slot, g_handlers[a->id].user.load(std::memory_order_relaxed));
                break;
            default: break;
        }
    }
    if (timing) {
        S.job_ticks.fetch_add(__rdtsc() - t0, std::memory_order_relaxed);
        S.job_calls.fetch_add(1, std::memory_order_relaxed);
    }
    return 0;
}

bool make_inline(safetyhook::InlineHook& h, uintptr_t at, void* detour, const char* what) {
    if (!at) return false;
    auto r = safetyhook::create_inline(reinterpret_cast<void*>(at), detour);
    if (!r.operator bool()) {
        KC_W("rules: the hook on %s (0x%llx) failed", what, static_cast<unsigned long long>(at));
        return false;
    }
    h = std::move(r);
    return true;
}

void refresh_override_flag() {
    bool any = g_pick_n.load(std::memory_order_relaxed) != 0;
    if (!any)
        for (const auto& [e, t] : g_tracks)
            if (!t.overrides.empty()) {
                any = true;
                break;
            }
    g_any_override.store(any, std::memory_order_relaxed);
}

// ---- tick side: reading what the table needs ----

uint32_t read_buffs(KilnEntity e, std::vector<uint32_t>* out) {
    out->clear();
    uint32_t n = 0;
    auto* c = static_cast<uint8_t*>(component(e, kBuffContainer, &n));
    if (!c) return 0;
    for (const BuffList& l : g_buff_lists) {
        if (l.first < 0) continue;
        for (uint32_t i = 0; i < l.count; ++i) {
            const int64_t off = l.first + static_cast<int64_t>(i) * g_buff_stride;
            if (static_cast<uint32_t>(off) + 8 > n) break;
            uint32_t id = 0;
            uint8_t cleansed = 0;
            std::memcpy(&id, c + off, 4);
            std::memcpy(&cleansed, c + off + g_buff_cleansed, 1);
            if (id && !cleansed) out->push_back(id);
        }
    }
    return static_cast<uint32_t>(out->size());
}

void refresh_tables() {
    const RuleUses u = arsenal_uses();
    if (u.time) {
        if (!g_time && !g_time_warned) {
            g_time = static_cast<const KilnTimeApi*>(g_api->service(kstr(KILN_TIME_SERVICE), 1));
            if (!g_time) {
                g_time_warned = true;
                KC_W("rules: kiln.time is not available: time-of-day conditions stay false");
            }
        }
        if (g_time) {
            KilnTimeState st{};
            st.size = sizeof st;
            if (g_time->get(&st) == KILN_OK) g_hour.store(static_cast<float>(st.hours), std::memory_order_relaxed);
        }
    }
    if (!u.buffs && !u.phase) return;
    std::vector<uint32_t> buffs;
    for (auto& [e, t] : g_tracks) {
        if (u.buffs) {
            read_buffs(e, &buffs);
            if (buffs != t.buffs) {
                t.buffs = buffs;
                g_dirty = true;
            }
        }
        if (u.phase && g_off_phase >= 0) {
            uint32_t n = 0;
            auto* d = static_cast<uint8_t*>(component(e, kEnemyPhasesDyn, &n));
            uint32_t ph = 0;
            if (d && static_cast<uint32_t>(g_off_phase) + 16 <= n) {
                uint8_t g[16];
                std::memcpy(g, d + g_off_phase, 16);
                static const uint8_t zero[16] = {};
                if (std::memcmp(g, zero, 16) != 0) ph = fnv1a(std::string_view(reinterpret_cast<const char*>(g), 16));
            }
            if (ph != t.game_phase) {
                t.game_phase = ph;
                g_dirty = true;
            }
        }
    }
}

void publish_snapshot() {
    uint32_t cap = 64;
    while (cap < g_tracks.size() * 2 + 1) cap <<= 1;
    Snapshot& s = g_snap[g_next];
    g_next = (g_next + 1) % 3;
    s.rows.assign(cap, Row{});
    s.mask = cap - 1;
    s.count = static_cast<uint32_t>(g_tracks.size());
    for (const auto& [e, t] : g_tracks) {
        uint32_t i = (e * 2654435761u) & s.mask;
        while (s.rows[i].e) i = (i + 1) & s.mask;
        Row& r = s.rows[i];
        r.e = e;
        r.phase = t.phase;
        r.game_phase = t.game_phase;
        r.nbuffs = static_cast<uint8_t>(std::min<size_t>(t.buffs.size(), kRowBuffs));
        for (uint32_t k = 0; k < r.nbuffs; ++k) r.buffs[k] = t.buffs[k];
        std::memcpy(r.known, t.known, sizeof r.known);
        std::memcpy(r.value, t.value, sizeof r.value);
        if (!t.overrides.empty()) {
            const Owned& o = t.overrides.back();
            r.has_override = 1;
            r.forced = static_cast<int16_t>(o.forced);
            r.allow = o.allow;
        }
    }
    g_pub.store(&s, std::memory_order_release);
}

void push_work(uint32_t kind, KilnEntity e, uint32_t id, KilnEntity target, uint64_t life = 0, uint64_t target_life = 0) {
    if (g_work.size() >= kMaxQueue) {
        if (kind == KILN_RULE_WORK_ACTION) S.actions_dropped.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    g_work.push_back(Work{{kind, e, id, target, g_step}, life, target_life});
}

// enemy_combat_targets switches to its best candidate only once now passes DynamicAggro.nextAllowedTargetChangeTime: with
// that at "never" it keeps what SelectedTargets holds, and post_enemy_combat_targets fills in where that entity is.
void hold_target(KilnEntity e, KilnEntity target) {
    uint32_t na = 0, ns = 0;
    auto* aggro = static_cast<uint8_t*>(component(e, kAggroDyn, &na));
    auto* sel = static_cast<uint8_t*>(component(e, H::Targets, &ns));
    if (!aggro || !sel || static_cast<uint32_t>(g_off_next_target_change) + 8 > na || static_cast<uint32_t>(g_off_target_entity) + 4 > ns) return;
    const int64_t never = INT64_MAX;
    seh_write(aggro + g_off_next_target_change, &never, 8);
    uint32_t cur = 0;
    seh_read(sel + g_off_target_entity, &cur, 4);
    if (cur == target) return;
    seh_write(sel + g_off_target_entity, &target, 4);
    if (g_off_target_counter >= 0 && static_cast<uint32_t>(g_off_target_counter) + 3 <= ns) {
        // as the game does on a change
        uint16_t counter = 0;
        seh_read(sel + g_off_target_counter, &counter, 2);
        ++counter;
        const uint8_t flags = 0;
        seh_write(sel + g_off_target_counter, &counter, 2);
        seh_write(sel + g_off_target_counter + 2, &flags, 1);
    }
}

void let_target_go(KilnEntity e) {
    uint32_t n = 0;
    auto* aggro = static_cast<uint8_t*>(component(e, kAggroDyn, &n));
    if (!aggro || static_cast<uint32_t>(g_off_next_target_change) + 8 > n) return;
    const int64_t now = 0;
    seh_write(aggro + g_off_next_target_change, &now, 8);
}

void ai_switch_write(KilnEntity e, bool on) {
    if (g_off_enabled < 0) return;
    uint32_t n = 0;
    auto* d = static_cast<uint8_t*>(component(e, H::Dyn, &n));
    if (!d || static_cast<uint32_t>(g_off_enabled) >= n) return;
    const uint8_t v = on ? 1 : 0;
    seh_write(d + g_off_enabled, &v, 1);
}

}  // namespace

void rules_lifetime_init() {
    KilnHandle h = 0;
    uintptr_t at = 0;
    if (g_api->size < offsetof(KilnApi, subscribe_world) + sizeof(g_api->subscribe_world) ||
        !g_api->feature || !g_api->feature(kstr("world.lifecycle")) || !g_api->subscribe_world || L.world_storage <= 0 ||
        g_api->symbol(kstr("spawn/DestroyEntity"), &at) != KILN_OK || !at ||
        g_api->subscribe_world(g_owner, &on_rules_world, nullptr, &h) != KILN_OK) return;
    auto hook = safetyhook::MidHook::create(reinterpret_cast<void*>(at), &hk_life_destroy);
    if (hook) g_life_destroy = std::move(*hook);
}

void rules_lifetime_unload(void*) {
    clear_rule_work();
    g_life_destroy = {};
}

void rules_resolve() {
    const auto sym = [](const char* name, uintptr_t* out) {
        return g_api->symbol(KilnStr{name, std::strlen(name)}, out) == KILN_OK && *out;
    };
    const auto feat = [](const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); };
    int64_t v = 0;
    if (g_api->constant(kstr("creatures/ctx.arsenal"), &v) == KILN_OK) g_ctx_arsenal = v;
    if (feat("creatures.rules") && L.ctx_dyn >= 0) {
        sym("creatures/ConstraintEval", &g_s_constraint);
        sym("creatures/ActionExec", &g_s_action);
    }
    if (feat("creatures.attacks") && g_ctx_arsenal >= 0 && L.ctx_dyn >= 0) {
        uintptr_t b = 0, t = 0;
        sym("creatures/SelectAttack", &g_s_select);
        sym("creatures/AttackAvailable", &g_s_available);
        sym("creatures/FindAttackBySequence", &g_s_find);
        sym("creatures/SetCurrentAttack", &g_s_start);
        if (sym("creatures/BeginAttack", &b)) g_begin = reinterpret_cast<BeginFn>(b);
        if (sym("creatures/CurrentTarget", &t)) g_current_target = reinterpret_cast<TargetFn>(t);
    }
}

// Offsets by name, from the game's reflection. Called once the reflection works; missing ones turn off what needs them.
void rules_read_layout() {
    TypeInfo t;
    std::string err;
    FieldRef f;
    if (type_info("keen::ecs::DynamicEnemy01BehaviorState", &t, &err)) {
        if (resolve_path(t, "enabled", &f, &err)) g_off_enabled = f.offset;
        if (resolve_path(t, "currentBehaviorIndex", &f, &err)) g_off_behavior_index = f.offset;
    }
    if (type_info("keen::ecs::DynamicAggro", &t, &err) && resolve_path(t, "nextAllowedTargetChangeTime", &f, &err))
        g_off_next_target_change = f.offset;
    if (type_info("keen::ecs::SelectedTargets", &t, &err)) {
        if (resolve_path(t, "targets[0].targetEntityId", &f, &err)) g_off_target_entity = f.offset;
        if (resolve_path(t, "targets[0].changeCounter", &f, &err)) g_off_target_counter = f.offset;
    }
    if (type_info("keen::ecs::DynamicEnemyPhases", &t, &err) && resolve_path(t, "currentPhase", &f, &err))
        g_off_phase = f.offset;
    if (type_info("keen::ecs::BuffContainer", &t, &err)) {
        static const struct {
            const char* name;
            uint32_t count;
        } lists[5] = {{"foodBuffs", 4}, {"genericBuffs", 16}, {"genericDebuffs", 16}, {"restedBuff", 1}, {"terrainDebuff", 1}};
        FieldRef id, cl;
        if (resolve_path(t, "genericBuffs[1].typeId", &id, &err) && resolve_path(t, "genericBuffs[0].typeId", &f, &err) &&
            resolve_path(t, "genericBuffs[0].cleansed", &cl, &err)) {
            g_buff_stride = static_cast<int64_t>(id.offset) - f.offset;
            g_buff_cleansed = static_cast<int64_t>(cl.offset) - f.offset;
            for (int i = 0; i < 5; ++i) {
                const std::string path = std::string(lists[i].name) + "[0].typeId";
                if (resolve_path(t, path, &f, &err)) g_buff_lists[i] = BuffList{static_cast<int64_t>(f.offset), lists[i].count};
            }
        }
    }
    KC_I("rules: layout: AI switch +0x%llx, behaviour index +0x%llx, target change +0x%llx, phase +0x%llx, buff stride %lld",
         static_cast<unsigned long long>(g_off_enabled), static_cast<unsigned long long>(g_off_behavior_index),
         static_cast<unsigned long long>(g_off_next_target_change), static_cast<unsigned long long>(g_off_phase),
         static_cast<long long>(g_buff_stride));
}

bool rules_dispatcher_resolved() { return g_s_constraint && g_s_action; }
bool rules_attacks_resolved() { return g_s_select && g_s_available && g_s_find && g_s_start && g_begin; }

bool rules_ensure_hooks() {
    if (g_rule_hooks) return true;
    if (!rules_dispatcher_resolved()) return false;
    const uint64_t t0 = GetTickCount64();
    if (!make_inline(g_h_constraint, g_s_constraint, reinterpret_cast<void*>(&hk_constraint), "ConstraintEval")) return false;
    g_o_constraint = g_h_constraint.original<ConstraintFn>();
    if (!make_inline(g_h_action, g_s_action, reinterpret_cast<void*>(&hk_action), "ActionExec")) {
        g_h_constraint = {};
        return false;
    }
    g_o_action = g_h_action.original<ActionFn>();
    g_rule_hooks = true;
    KC_I("rules: constraint and action hooks installed (%llu ms)", static_cast<unsigned long long>(GetTickCount64() - t0));
    return true;
}

bool rules_ensure_attack_hooks() {
    if (g_attack_hooks) return true;
    if (!rules_attacks_resolved()) return false;
    // The hooks read attack records (forced pick, refused follow-ups and sequences): no layout, no hooks.
    if (!arsenal_layout_ready()) return false;
    // The rule-played sequence check sits in the action hook.
    if (rules_dispatcher_resolved() && !rules_ensure_hooks()) return false;
    const uint64_t t0 = GetTickCount64();
    if (!make_inline(g_h_select, g_s_select, reinterpret_cast<void*>(&hk_select), "SelectAttack")) return false;
    g_o_select = g_h_select.original<SelectFn>();
    if (!make_inline(g_h_available, g_s_available, reinterpret_cast<void*>(&hk_available), "AttackAvailable")) {
        g_h_select = {};
        return false;
    }
    g_o_available = g_h_available.original<AvailableFn>();
    if (!make_inline(g_h_find, g_s_find, reinterpret_cast<void*>(&hk_find), "FindAttackBySequence")) {
        g_h_select = {};
        g_h_available = {};
        return false;
    }
    g_o_find = g_h_find.original<FindFn>();
    auto m = safetyhook::MidHook::create(reinterpret_cast<void*>(g_s_start), &hk_start);
    if (!m) {
        KC_W("rules: the mid hook on SetCurrentAttack failed");
        g_h_select = {};
        g_h_available = {};
        g_h_find = {};
        return false;
    }
    g_h_start = std::move(*m);
    g_attack_hooks = true;
    KC_I("rules: attack hooks installed (%llu ms)", static_cast<unsigned long long>(GetTickCount64() - t0));
    return true;
}

// Called in Pre when no job runs. Nothing left uses the hooks: take them out, the game's code is as it was.
void rules_unhook_if_idle() {
    if (!g_rule_hooks && !g_attack_hooks) return;
    const RuleUses u = arsenal_uses();
    bool handlers = false;
    for (const Handler& h : g_handlers) handlers |= h.fn.load(std::memory_order_relaxed) != nullptr;
    bool overrides = g_pick_n.load(std::memory_order_relaxed) != 0;
    for (const auto& [e, t] : g_tracks) overrides |= !t.overrides.empty();
    const bool need_attacks = overrides || u.attack_action;
    const bool need_rules = need_attacks || handlers || u.any_kiln;
    if (g_attack_hooks && !need_attacks) {
        g_attack_hooks = false;
        g_h_start = {};
        g_h_find = {};
        g_h_available = {};
        g_h_select = {};
        g_o_select = nullptr;
        KC_I("rules: attack hooks removed");
    }
    if (g_rule_hooks && !need_rules) {
        g_rule_hooks = false;
        g_h_action = {};
        g_h_constraint = {};
        KC_I("rules: constraint and action hooks removed");
    }
}

void rules_pre_work() {
    LARGE_INTEGER t0{};
    QueryPerformanceCounter(&t0);
    if (g_life_destroy) g_life_storage.store(world() + static_cast<uintptr_t>(L.world_storage), std::memory_order_release);
    std::vector<Want> wants;
    {
        std::lock_guard lock(g_want_mu);
        wants.swap(g_want);
    }
    for (const Want& w : wants) {
        if (w.kind == W_ACTION) {
            if (!w.life && !g_life_warned) {
                g_life_warned = true;
                KC_W("rules: entity lifetime observer unavailable; queued actions use entity IDs without lifetime checks");
            }
            push_work(KILN_RULE_WORK_ACTION, w.e, w.id, w.target, w.life, w.target_life);
            continue;
        }
        auto [it, fresh] = g_tracks.try_emplace(w.e);
        if (fresh) g_dirty = true;
        Track& t = it->second;
        switch (w.kind) {
            case W_CONDITION: {
                const uint32_t i = w.id / 64;
                const uint64_t bit = 1ull << (w.id % 64);
                if (!(t.asked[i] & bit)) {
                    t.asked[i] |= bit;
                    push_work(KILN_RULE_WORK_CONDITION, w.e, w.id, 0);
                }
                break;
            }
            case W_PICK:
                if (!t.pick_asked) {
                    t.pick_asked = true;
                    push_work(KILN_RULE_WORK_PICK, w.e, 0, 0);
                }
                break;
            case W_PHASE:
                if (t.phase != w.id) {
                    t.phase = w.id;
                    g_dirty = true;
                }
                break;
            default: break;
        }
    }
    if (!g_prefer.empty()) {
        const uint64_t now = GetTickCount64();
        for (auto it = g_prefer.begin(); it != g_prefer.end();) {
            const bool alive = entity_exists(it->first);
            if (!alive || now >= it->second.until_ms || !entity_exists(it->second.target)) {
                if (alive) let_target_go(it->first);
                it = g_prefer.erase(it);
                continue;
            }
            hold_target(it->first, it->second.target);
            ++it;
        }
    }
    if (g_step % kGoneCheckSteps == 0) {
        for (auto it = g_tracks.begin(); it != g_tracks.end();) {
            if (entity_exists(it->first)) {
                ++it;
                continue;
            }
            push_work(KILN_RULE_WORK_GONE, it->first, 0, 0);
            it = g_tracks.erase(it);
            g_dirty = true;
        }
        refresh_override_flag();
        for (auto it = g_ai_off.begin(); it != g_ai_off.end();) {
            if (!entity_exists(it->first)) {
                it = g_ai_off.erase(it);
                continue;
            }
            ai_switch_write(it->first, false);  // kept off: the game may turn it back on
            ++it;
        }
    }
    if (g_step % kRefreshSteps == 0 && !g_tracks.empty()) refresh_tables();
    else if (g_step % kRefreshSteps == 0) {
        const RuleUses u = arsenal_uses();
        if (u.time && g_time) {
            KilnTimeState st{};
            st.size = sizeof st;
            if (g_time->get(&st) == KILN_OK) g_hour.store(static_cast<float>(st.hours), std::memory_order_relaxed);
        } else if (u.time) {
            refresh_tables();
        }
    }
    LARGE_INTEGER t1{}, f{};
    QueryPerformanceCounter(&t1);
    QueryPerformanceFrequency(&f);
    S.pre_ns.fetch_add(static_cast<uint64_t>((t1.QuadPart - t0.QuadPart) * 1000000000 / f.QuadPart), std::memory_order_relaxed);
    S.pre_calls.fetch_add(1, std::memory_order_relaxed);
}

void rules_publish() {
    if (!g_dirty) return;
    LARGE_INTEGER t0{};
    QueryPerformanceCounter(&t0);
    g_dirty = false;
    publish_snapshot();
    LARGE_INTEGER t1{}, f{};
    QueryPerformanceCounter(&t1);
    QueryPerformanceFrequency(&f);
    S.pre_ns.fetch_add(static_cast<uint64_t>((t1.QuadPart - t0.QuadPart) * 1000000000 / f.QuadPart), std::memory_order_relaxed);
}

bool rules_wants_listener() { return !g_work.empty() || g_pump.load(std::memory_order_relaxed) != 0; }

void rules_release_owner(KilnOwner owner) {
    for (auto& [e, t] : g_tracks) {
        const size_t before = t.overrides.size();
        std::erase_if(t.overrides, [&](const Owned& o) { return o.owner == owner; });
        if (t.overrides.size() != before) g_dirty = true;
    }
    for (Handler& h : g_handlers)
        if (h.owner == owner) {
            h.fn.store(nullptr, std::memory_order_release);
            h.owner = 0;
        }
    uint32_t n = g_pick_n.load(std::memory_order_relaxed);
    for (uint32_t i = 0; i < n;) {
        if (g_pick[i].owner == owner) {
            g_pick[i] = g_pick[--n];
            continue;
        }
        ++i;
    }
    g_pick_n.store(n, std::memory_order_release);
    refresh_override_flag();
    for (auto it = g_prefer.begin(); it != g_prefer.end();) {
        if (it->second.owner == owner) {
            if (entity_exists(it->first)) let_target_go(it->first);
            it = g_prefer.erase(it);
        } else {
            ++it;
        }
    }
    for (auto it = g_ai_off.begin(); it != g_ai_off.end();) {
        std::erase(it->second, owner);
        if (it->second.empty()) {
            ai_switch_write(it->first, true);
            it = g_ai_off.erase(it);
        } else {
            ++it;
        }
    }
    arsenal_release_owner(owner);
}

uint32_t KILN_CALL rules_drain_api(KilnRuleWork* out, uint32_t capacity) {
    if (!on_tick() || !out) return 0;
    uint32_t n = 0;
    while (!g_work.empty() && n < capacity) {
        const Work w = g_work.front();
        g_work.pop_front();
        const auto alive = [](KilnEntity e, uint64_t life) {
            if (!life) return true;  // unknown build: preserve the previous behaviour
            std::lock_guard lock(g_want_mu);
            auto it = g_lives.find(e);
            return it != g_lives.end() && it->second == life && entity_exists(e);
        };
        if (w.work.kind == KILN_RULE_WORK_ACTION &&
            (!alive(w.work.entity, w.life) || !alive(w.work.target, w.target_life))) {
            S.actions_dropped.fetch_add(1, std::memory_order_relaxed);
            KC_D("rules: dropped queued action %u for entity %u: entity or target lifetime changed", w.work.id, w.work.entity);
            continue;
        }
        out[n++] = w.work;
    }
    return n;
}

KilnResult KILN_CALL rules_condition_set_api(KilnEntity e, uint32_t slot, uint32_t value) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (slot >= kSlots || !e) return KILN_ERR_INVALID_ARGUMENT;
    auto [it, fresh] = g_tracks.try_emplace(e);
    Track& t = it->second;
    const uint32_t i = slot / 64;
    const uint64_t bit = 1ull << (slot % 64);
    const uint64_t k0 = t.known[i], v0 = t.value[i];
    t.known[i] |= bit;
    t.asked[i] |= bit;
    if (value) t.value[i] |= bit;
    else t.value[i] &= ~bit;
    if (fresh || k0 != t.known[i] || v0 != t.value[i]) g_dirty = true;
    return KILN_OK;
}

KilnResult KILN_CALL rules_condition_clear_api(uint32_t slot) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (slot >= kSlots) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t i = slot / 64;
    const uint64_t bit = 1ull << (slot % 64);
    for (auto& [e, t] : g_tracks) {
        if ((t.known[i] | t.asked[i]) & bit) g_dirty = true;
        t.known[i] &= ~bit;
        t.value[i] &= ~bit;
        t.asked[i] &= ~bit;
    }
    g_slot_eval[slot].store(0, std::memory_order_relaxed);
    g_slot_true[slot].store(0, std::memory_order_relaxed);
    return KILN_OK;
}

KilnResult KILN_CALL rules_condition_stats_api(uint32_t slot, uint64_t* evaluated, uint64_t* truthy) {
    if (slot >= kSlots) return KILN_ERR_INVALID_ARGUMENT;
    if (evaluated) *evaluated = g_slot_eval[slot].load(std::memory_order_relaxed);
    if (truthy) *truthy = g_slot_true[slot].load(std::memory_order_relaxed);
    return KILN_OK;
}

void KILN_CALL rules_pump_api(uint32_t flags) {
    g_pump.store(flags & 1, std::memory_order_relaxed);
    const bool timing = (flags & 2) != 0;
    if (timing && !g_tick_hz) {  // rdtsc ticks per second, over 10 ms, once
        LARGE_INTEGER f{}, a{}, b{};
        QueryPerformanceFrequency(&f);
        const uint64_t t0 = __rdtsc();
        QueryPerformanceCounter(&a);
        do QueryPerformanceCounter(&b);
        while (b.QuadPart - a.QuadPart < f.QuadPart / 100);
        g_tick_hz = (__rdtsc() - t0) * 100;
    }
    g_timing.store(timing ? 1 : 0, std::memory_order_relaxed);
}

KilnResult KILN_CALL rules_phase_set_api(KilnEntity e, uint32_t phase) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (!entity_exists(e)) return KILN_ERR_NOT_FOUND;
    auto [it, fresh] = g_tracks.try_emplace(e);
    if (fresh || it->second.phase != phase) g_dirty = true;
    it->second.phase = phase;
    return KILN_OK;
}

KilnResult KILN_CALL rules_phase_get_api(KilnEntity e, uint32_t* out) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    *out = 0;
    auto it = g_tracks.find(e);
    if (it != g_tracks.end()) *out = it->second.phase ? it->second.phase : it->second.game_phase;
    return KILN_OK;
}

KilnResult KILN_CALL rules_status_api(KilnRulesStatus* out) {
    if (!out || out->size < 8) return KILN_ERR_INVALID_ARGUMENT;
    KilnRulesStatus s{};
    s.size = sizeof s;
    s.writer = arsenal_writer_ready();
    s.dispatcher = rules_dispatcher_resolved();
    s.attacks = rules_attacks_resolved();
    s.rule_hooks = g_rule_hooks;
    s.attack_hooks = g_attack_hooks;
    s.ai_switch = g_off_enabled >= 0;
    s.arsenals = arsenal_count_kiln();
    s.tracked = on_tick() ? static_cast<uint32_t>(g_tracks.size()) : 0;
    if (on_tick()) {
        for (const auto& [e, t] : g_tracks) s.overrides += !t.overrides.empty();
        s.ai_off = static_cast<uint32_t>(g_ai_off.size());
    }
    for (const Handler& h : g_handlers) s.handlers += h.fn.load(std::memory_order_relaxed) != nullptr;
    arsenal_map_stats(&s.map_capacity, &s.map_count);
    s.constraints = S.constraints;
    s.constraints_true = S.constraints_true;
    s.actions = S.actions;
    s.actions_dropped = S.actions_dropped;
    s.selects = S.selects;
    s.selects_forced = S.selects_forced;
    s.selects_filtered = S.selects_filtered;
    s.picks_waiting = S.picks_waiting;
    s.followups_refused = S.followups_refused;
    s.starts = S.starts;
    s.starts_off_override = S.starts_off;
    s.sequences_refused = S.sequences_refused;
    s.pre_ns = S.pre_ns;
    s.pre_calls = S.pre_calls;
    s.job_ticks = S.job_ticks;
    s.job_calls = S.job_calls;
    s.job_tick_hz = g_tick_hz;
    const uint32_t keep = out->size;
    std::memcpy(out, &s, std::min<size_t>(keep, sizeof s));
    out->size = keep;
    return KILN_OK;
}

KilnResult KILN_CALL rules_job_handler_api(KilnOwner owner, uint32_t id, KilnRuleJobFn fn, void* user) {
    if (id == 0 || id >= kHandlers || !owner) return KILN_ERR_INVALID_ARGUMENT;
    Handler& h = g_handlers[id];
    if (fn && h.owner && h.owner != owner) return KILN_ERR_INVALID_ARGUMENT;
    h.user.store(user, std::memory_order_relaxed);
    h.fn.store(fn, std::memory_order_release);
    h.owner = fn ? owner : 0;
    if (fn && on_tick() && !rules_ensure_hooks()) return KILN_ERR_UNAVAILABLE;
    return KILN_OK;
}

KilnResult KILN_CALL attack_override_api(KilnOwner owner, KilnEntity e, const KilnAttackOverride* o) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (!owner || !e) return KILN_ERR_INVALID_ARGUMENT;
    if (!o) {
        auto it = g_tracks.find(e);
        if (it != g_tracks.end()) {
            std::erase_if(it->second.overrides, [&](const Owned& x) { return x.owner == owner; });
            it->second.pick_asked = false;
            g_dirty = true;
        }
        refresh_override_flag();
        return KILN_OK;
    }
    if (o->size < sizeof(KilnAttackOverride) || o->forced > 254 || o->forced < -1) return KILN_ERR_INVALID_ARGUMENT;
    if (!entity_exists(e)) return KILN_ERR_NOT_FOUND;
    if (!rules_ensure_attack_hooks()) return KILN_ERR_UNAVAILABLE;
    if (o->forced >= 0) {
        // Refuse what cannot be forced: no arsenal at all, or an arsenal without that attack. An arsenal the registry
        // does not have yet (written this step) is accepted; on the job thread a forced index the arsenal lacks falls
        // back to the game's own choice, as it does after an arsenal swap.
        const uint8_t* arsenal = nullptr;
        const KilnResult rc = arsenal_record_of(e, &arsenal);
        if (rc == KILN_ERR_UNAVAILABLE) return rc;
        uint32_t n = 0;
        if (rc == KILN_OK && seh_read(arsenal + AL.attacks + 4, &n, 4) && static_cast<uint32_t>(o->forced) >= n)
            return KILN_ERR_INVALID_ARGUMENT;
    }
    Track& t = g_tracks[e];
    std::erase_if(t.overrides, [&](const Owned& x) { return x.owner == owner; });
    t.overrides.push_back(Owned{owner, o->forced, o->allow});
    g_dirty = true;
    g_any_override.store(true, std::memory_order_relaxed);
    return KILN_OK;
}

KilnResult KILN_CALL attack_pick_arsenal_api(KilnOwner owner, const KilnGuid* arsenal, uint32_t enabled) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (!owner || !arsenal) return KILN_ERR_INVALID_ARGUMENT;
    uint64_t lo = 0, hi = 0;
    std::memcpy(&lo, arsenal->b, 8);
    std::memcpy(&hi, arsenal->b + 8, 8);
    uint32_t n = g_pick_n.load(std::memory_order_relaxed);
    for (uint32_t i = 0; i < n; ++i) {
        if (g_pick[i].lo != lo || g_pick[i].hi != hi || g_pick[i].owner != owner) continue;
        if (!enabled) {
            g_pick[i] = g_pick[n - 1];
            g_pick_n.store(n - 1, std::memory_order_release);
            refresh_override_flag();
        }
        return KILN_OK;
    }
    if (!enabled) return KILN_OK;
    if (n >= kMaxPick) return KILN_ERR_BUFFER_TOO_SMALL;
    if (!rules_ensure_attack_hooks()) return KILN_ERR_UNAVAILABLE;
    g_pick[n] = PickEntry{lo, hi, owner};
    g_pick_n.store(n + 1, std::memory_order_release);
    g_any_override.store(true, std::memory_order_relaxed);
    return KILN_OK;
}

KilnResult KILN_CALL ai_switch_api(KilnOwner owner, KilnEntity e, uint32_t enabled) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (g_off_enabled < 0) return KILN_ERR_UNAVAILABLE;
    if (!owner) return KILN_ERR_INVALID_ARGUMENT;
    if (!entity_exists(e)) return KILN_ERR_NOT_FOUND;
    if (!component(e, H::Dyn)) return KILN_ERR_UNAVAILABLE;
    auto it = g_ai_off.find(e);
    if (enabled) {
        if (it == g_ai_off.end()) return KILN_OK;
        std::erase(it->second, owner);
        if (it->second.empty()) {
            ai_switch_write(e, true);
            g_ai_off.erase(it);
        }
        return KILN_OK;
    }
    auto& owners = g_ai_off[e];
    std::erase(owners, owner);
    owners.push_back(owner);
    ai_switch_write(e, false);
    return KILN_OK;
}

KilnResult KILN_CALL ai_switch_get_api(KilnEntity e, uint32_t* out) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    if (g_off_enabled < 0) return KILN_ERR_UNAVAILABLE;
    uint32_t n = 0;
    auto* d = static_cast<uint8_t*>(component(e, H::Dyn, &n));
    if (!d) return entity_exists(e) ? KILN_ERR_UNAVAILABLE : KILN_ERR_NOT_FOUND;
    uint8_t v = 0;
    if (!seh_read(d + g_off_enabled, &v, 1)) return KILN_ERR_INTERNAL;
    *out = v != 0;
    return KILN_OK;
}

KilnResult KILN_CALL target_prefer_api(KilnOwner owner, KilnEntity e, KilnEntity target, float seconds) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (g_off_next_target_change < 0 || g_off_target_entity < 0) return KILN_ERR_UNAVAILABLE;
    if (!owner) return KILN_ERR_INVALID_ARGUMENT;
    if (!target) {
        auto it = g_prefer.find(e);
        if (it != g_prefer.end() && it->second.owner == owner) {
            let_target_go(e);
            g_prefer.erase(it);
        }
        return KILN_OK;
    }
    if (!entity_exists(e) || !entity_exists(target)) return KILN_ERR_NOT_FOUND;
    if (!component(e, kAggroDyn) || !component(e, H::Targets)) return KILN_ERR_UNAVAILABLE;
    g_prefer[e] = Prefer{owner, target, GetTickCount64() + static_cast<uint64_t>(std::max(seconds, 0.f) * 1000.f)};
    hold_target(e, target);
    return KILN_OK;
}

// BehaviorUpdate(ctx), armed once an arsenal got fewer rules: a creature can still hold the index of a rule that is gone,
// and the function that continues a running rule does not check it against the list.
void rules_behavior_guard(uint8_t* ctx) {
    if (g_off_behavior_index < 0 || g_ctx_arsenal < 0 || !AL.rule_size) return;
    uint8_t* dyn = nullptr;
    uint8_t* arsenal = nullptr;
    std::memcpy(&dyn, ctx + L.ctx_dyn, 8);
    std::memcpy(&arsenal, ctx + g_ctx_arsenal, 8);
    if (!dyn) return;
    const uint8_t index = dyn[g_off_behavior_index];
    if (index == 0xFF) return;
    uint32_t count = 0;
    if (arsenal) std::memcpy(&count, arsenal + AL.rules + 4, 4);
    if (index >= count) {
        const uint16_t none = 0xFFFF;
        std::memcpy(dyn + g_off_behavior_index, &none, 2);
    }
}

}  // namespace kcr
