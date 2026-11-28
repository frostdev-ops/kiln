// SPDX-License-Identifier: MIT
// kiln.combat.speed's logic without the game (header-only, so tests/speed_test.cpp checks the same code on the host):
// the rate table, Kiln's routes (which action is Melee, Bow or Tool), the clock, graph and bow arithmetic, the per-run
// action records and graph bindings, and the bodies of the four mid hooks over a register block. action_speed.cpp binds
// them to the game (kiln.hooks links, kiln.world wraps, kiln.items, kiln.client). Evidence: docs/re/action-speed.md.
#pragma once
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <memory>
#include <mutex>
#include <utility>
#include <vector>

#include "kiln_api.h"
#include "services/kiln_action_speed.h"
#include "services/kiln_hooks.h"

namespace kc::speed {

constexpr int kKinds = KILN_ACTION_KINDS;
constexpr int kNone = -1;
constexpr uint32_t kPlayerMax = 1023;  // the engine's player entity range is 1..1023 (EntityId.IsPlayer)
inline bool is_player(uint32_t e) { return e >= 1 && e <= kPlayerMax; }

// ---- the runs a hook can be in (marked by kiln.world wrap stages around the game's update) ----
enum Scope : uint8_t { kScopeNone = 0, kAuthority = 1, kPrediction = 2, kPresentation = 3, kScopes = 4 };
enum Program : uint8_t { kProgNone = 0, kProgSim = 1, kProgGraph = 2, kProgRanged = 3 };
struct Mark {
    uint8_t scope = kScopeNone;
    uint8_t program = kProgNone;
    uint32_t step = 0;  // the world's step (KilnGameSlice.step), low 32 bits
};

// ---- the game's enums (reflected on 26430625 / 26430253: keen::actor::ActionSequenceTrigger,
// keen::EquipmentSlotType, keen::ranged_weapon::WeaponType) ----
enum Trigger : uint16_t { kPrimary = 1, kPrimaryTap = 2, kPrimaryHold = 3, kHeavy = 4, kSecondary = 5, kToolTrigger = 6 };
enum EquipSlot : uint8_t { kSlotMelee = 1, kSlotTool = 2, kSlotMidRange = 4, kSlotRange = 5 };
constexpr uint8_t kWeaponBow = 0, kRangedUnknown = 0xFF;

// The item part of the routes: an equipment slot type to the kind its actions can be (Bow = any ranged weapon; the
// actor's ranged weapon type decides at classification).
inline int item_kind_of_slot(uint8_t slot) {
    switch (slot) {
    case kSlotMelee: return KILN_ACTION_MELEE;
    case kSlotTool: return KILN_ACTION_TOOL;
    case kSlotMidRange:
    case kSlotRange: return KILN_ACTION_BOW;
    default: return kNone;
    }
}

// Kiln's routes: the running action's trigger, its used item's kind and the actor's ranged weapon type to an action
// kind, or kNone (the game's timing). Blocks, parries, dodges, interactions, spells and anything without a classified
// item stay out.
inline int classify(uint16_t trigger, int item_kind, uint8_t ranged_type) {
    const bool attack = trigger == kPrimary || trigger == kPrimaryTap || trigger == kPrimaryHold || trigger == kHeavy;
    switch (item_kind) {
    case KILN_ACTION_MELEE: return attack ? KILN_ACTION_MELEE : kNone;
    case KILN_ACTION_TOOL: return attack || trigger == kToolTrigger ? KILN_ACTION_TOOL : kNone;
    case KILN_ACTION_BOW:
        return (trigger == kPrimary || trigger == kPrimaryTap || trigger == kPrimaryHold) && ranged_type == kWeaponBow
                   ? KILN_ACTION_BOW
                   : kNone;
    default: return kNone;
    }
}

// ---- arithmetic ----
inline bool valid_rate(float r) { return std::isfinite(r); }
inline float clamp_rate(float r) { return std::clamp(r, KILN_ACTION_SPEED_MIN, KILN_ACTION_SPEED_MAX); }
constexpr int64_t kTimeLimit = INT64_C(1) << 56;  // larger magnitudes are the game's sentinels (±INT64_MAX): untouched

// The step's elapsed sequence time (signed ns) at `rate`. Zero, negative and sentinel values are the game's.
inline int64_t scale_elapsed(int64_t ns, float rate) {
    if (rate == 1.0f || ns <= 0 || ns >= kTimeLimit) return ns;
    const double v = std::round(static_cast<double>(ns) * rate);
    return v >= static_cast<double>(kTimeLimit) ? kTimeLimit - 1 : std::max<int64_t>(1, static_cast<int64_t>(v));
}

// A bow duration (charge or fire, ns) at `rate`: divided, at least 1 ns. Non-positive and sentinel values are the game's.
inline int64_t divide_duration(int64_t ns, float rate) {
    if (rate == 1.0f || ns <= 0 || ns >= kTimeLimit) return ns;
    const double v = std::round(static_cast<double>(ns) / rate);
    return v < 1.0 ? 1 : v >= static_cast<double>(kTimeLimit) ? kTimeLimit - 1 : static_cast<int64_t>(v);
}

// The graph evaluator's prepared delta (seconds) at `rate`.
inline float scale_delta(float s, float rate) { return rate == 1.0f || !(s > 0.0f) || !std::isfinite(s) ? s : s * rate; }

inline uint32_t bits(float f) {
    uint32_t u;
    std::memcpy(&u, &f, 4);
    return u;
}
inline float from_bits(uint32_t u) {
    float f;
    std::memcpy(&f, &u, 4);
    return f;
}
constexpr uint32_t kOneBits = 0x3F800000u;

// ---- the rate table: effective rates (every mod's product, from Kiln.ActionSpeed), read by the hooks on any thread ----
class RateTable {
public:
    static constexpr uint32_t kSlots = 64;  // players with rates of their own (a world holds 16)

    // Replaces the table. false: a bad argument (nothing changes). *any = a rate other than 1 is set now.
    bool set(const float defaults[kKinds], const KilnActionRate* players, uint32_t count, bool* any) {
        if (!defaults || (count && !players) || count > kSlots) return false;
        float d[kKinds];
        for (int k = 0; k < kKinds; ++k) {
            if (!valid_rate(defaults[k])) return false;
            d[k] = clamp_rate(defaults[k]);
        }
        for (uint32_t i = 0; i < count; ++i) {
            if (!is_player(players[i].entity)) return false;
            for (uint32_t j = 0; j < i; ++j)
                if (players[j].entity == players[i].entity) return false;
            for (int k = 0; k < kKinds; ++k)
                if (!valid_rate(players[i].rate[k])) return false;
        }
        std::lock_guard lock(mu_);
        bool scaling = false;
        for (int k = 0; k < kKinds; ++k) {
            defaults_[k].store(bits(d[k]), std::memory_order_relaxed);
            scaling |= d[k] != 1.0f;
        }
        // entities that keep a slot keep it (readers see their old or new rates, never another entity's)
        for (auto& s : slots_) {
            const uint32_t e = s.entity.load(std::memory_order_relaxed);
            if (!e) continue;
            const KilnActionRate* p = find(players, count, e);
            if (!p) s.entity.store(0, std::memory_order_release);
        }
        for (uint32_t i = 0; i < count; ++i) {
            Slot* s = slot_of(players[i].entity);
            if (!s) s = free_slot();
            if (!s) return false;  // cannot happen: count <= kSlots and departures were freed above
            for (int k = 0; k < kKinds; ++k) {
                const float r = clamp_rate(players[i].rate[k]);
                s->rate[k].store(bits(r), std::memory_order_relaxed);
                scaling |= r != 1.0f;
            }
            s->entity.store(players[i].entity, std::memory_order_release);
        }
        players_ = count;
        scaling_.store(scaling, std::memory_order_release);
        *any = scaling;
        return true;
    }

    // A rate other than 1 is set somewhere: the hooks' first test.
    bool scaling() const { return scaling_.load(std::memory_order_relaxed); }

    float rate(uint32_t entity, int kind) const {
        if (kind < 0 || kind >= kKinds || !is_player(entity)) return 1.0f;
        for (const auto& s : slots_)
            if (s.entity.load(std::memory_order_acquire) == entity) return from_bits(s.rate[kind].load(std::memory_order_relaxed));
        return from_bits(defaults_[kind].load(std::memory_order_relaxed));
    }
    float default_rate(int kind) const { return from_bits(defaults_[kind].load(std::memory_order_relaxed)); }
    uint32_t players() const { return players_; }

private:
    struct Slot {
        std::atomic<uint32_t> entity{0};
        std::atomic<uint32_t> rate[kKinds] = {kOneBits, kOneBits, kOneBits};
    };
    static const KilnActionRate* find(const KilnActionRate* p, uint32_t n, uint32_t e) {
        for (uint32_t i = 0; i < n; ++i)
            if (p[i].entity == e) return &p[i];
        return nullptr;
    }
    Slot* slot_of(uint32_t e) {
        for (auto& s : slots_)
            if (s.entity.load(std::memory_order_relaxed) == e) return &s;
        return nullptr;
    }
    Slot* free_slot() {
        for (auto& s : slots_)
            if (!s.entity.load(std::memory_order_relaxed)) return &s;
        return nullptr;
    }
    std::mutex mu_;
    std::atomic<uint32_t> defaults_[kKinds] = {kOneBits, kOneBits, kOneBits};
    Slot slots_[kSlots];
    uint32_t players_ = 0;
    std::atomic<bool> scaling_{false};
};

// ---- per run and entity: the rate of the classified action the sequence hook saw, and when (the graph hook reads it) ----
class ActionRecords {
public:
    void put(uint8_t scope, uint32_t entity, float rate, uint32_t step) {
        if (scope >= kScopes || !is_player(entity)) return;
        rec_[scope][entity].store((static_cast<uint64_t>(bits(rate)) << 32) | step, std::memory_order_relaxed);
    }
    // The rate recorded in this step or the previous one of the same world (the graph update may run before the
    // sequence advance in a step), else 1.
    float recent(uint8_t scope, uint32_t entity, uint32_t step) const {
        if (scope >= kScopes || !is_player(entity)) return 1.0f;
        const uint64_t v = rec_[scope][entity].load(std::memory_order_relaxed);
        if (!v) return 1.0f;
        const uint32_t at = static_cast<uint32_t>(v);
        return step - at <= 1u ? from_bits(static_cast<uint32_t>(v >> 32)) : 1.0f;
    }

private:
    std::atomic<uint64_t> rec_[kScopes][kPlayerMax + 1] = {};
};

// ---- the actor's ranged weapon type, as the bow site last saw it (any run; one value per entity) ----
class RangedTypes {
public:
    RangedTypes() {
        for (auto& t : t_) t.store(kRangedUnknown, std::memory_order_relaxed);
    }
    void put(uint32_t e, uint8_t type) {
        if (is_player(e)) t_[e].store(type, std::memory_order_relaxed);
    }
    uint8_t get(uint32_t e) const { return is_player(e) ? t_[e].load(std::memory_order_relaxed) : kRangedUnknown; }

private:
    std::atomic<uint8_t> t_[kPlayerMax + 1];
};

// ---- graph instance -> rate, written at the binding (a program's slice) and read at the evaluation (a job, later) ----
class Bindings {
public:
    static constexpr uint32_t kSlots = 512;
    static constexpr uint32_t kMaxAgeMs = 250;     // an evaluation never trails its binding by this much
    static constexpr uint32_t kReuseAgeMs = 10000; // a slot this old belongs to a graph that is gone

    // The binding of `instance` for this step. A rate of 1 clears an existing entry (never adds one). false: no room.
    bool put(uintptr_t instance, float rate, uint32_t now_ms) {
        if (!instance) return false;
        const uint32_t h = hash(instance);
        int64_t reuse = -1;
        for (uint32_t i = 0; i < kSlots; ++i) {
            Slot& s = slots_[(h + i) & (kSlots - 1)];
            const uintptr_t k = s.key.load(std::memory_order_acquire);
            if (k == instance) return store(s, rate, now_ms), true;
            if (k == 0) {
                if (rate == 1.0f) return true;  // nothing to clear
                uintptr_t expected = 0;
                if (s.key.compare_exchange_strong(expected, instance, std::memory_order_acq_rel))
                    return store(s, rate, now_ms), true;
                if (expected == instance) return store(s, rate, now_ms), true;
                continue;
            }
            if (reuse < 0 && now_ms - s.stamp.load(std::memory_order_relaxed) > kReuseAgeMs)
                reuse = static_cast<int64_t>((h + i) & (kSlots - 1));
        }
        if (rate == 1.0f) return true;
        if (reuse >= 0) {
            Slot& s = slots_[reuse];
            uintptr_t old = s.key.load(std::memory_order_acquire);
            if (now_ms - s.stamp.load(std::memory_order_relaxed) > kReuseAgeMs &&
                s.key.compare_exchange_strong(old, instance, std::memory_order_acq_rel))
                return store(s, rate, now_ms), true;
        }
        return false;
    }

    float get(uintptr_t instance, uint32_t now_ms) const {
        if (!instance) return 1.0f;
        const uint32_t h = hash(instance);
        for (uint32_t i = 0; i < kSlots; ++i) {
            const Slot& s = slots_[(h + i) & (kSlots - 1)];
            const uintptr_t k = s.key.load(std::memory_order_acquire);
            if (k == 0) return 1.0f;
            if (k == instance) {
                if (now_ms - s.stamp.load(std::memory_order_relaxed) > kMaxAgeMs) return 1.0f;
                return from_bits(s.rate.load(std::memory_order_relaxed));
            }
        }
        return 1.0f;
    }

private:
    struct Slot {
        std::atomic<uintptr_t> key{0};
        std::atomic<uint32_t> rate{kOneBits};
        std::atomic<uint32_t> stamp{0};
    };
    static uint32_t hash(uintptr_t p) { return static_cast<uint32_t>((p >> 4) * 0x9E3779B97F4A7C15ull >> 40); }
    static void store(Slot& s, float rate, uint32_t now_ms) {
        s.rate.store(bits(rate), std::memory_order_relaxed);
        s.stamp.store(now_ms, std::memory_order_relaxed);
    }
    Slot slots_[kSlots];
};

// ---- classified items: an immutable sorted list, swapped whole (old lists are kept until the process ends) ----
class ItemKinds {
public:
    using List = std::vector<std::pair<uint32_t, uint8_t>>;
    // false: count over the limit or a bad kind (nothing changes)
    bool set(const KilnActionItemKind* items, uint32_t count) {
        if (count > kMax || (count && !items)) return false;
        auto next = std::make_unique<List>();
        next->reserve(count);
        for (uint32_t i = 0; i < count; ++i) {
            if (items[i].kind >= kKinds || !items[i].item) return false;
            next->emplace_back(items[i].item, items[i].kind);
        }
        std::sort(next->begin(), next->end());
        next->erase(std::unique(next->begin(), next->end(), [](auto& a, auto& b) { return a.first == b.first; }), next->end());
        std::lock_guard lock(mu_);
        cur_.store(next.get(), std::memory_order_release);
        keep_.push_back(std::move(next));
        return true;
    }
    int kind(uint32_t item) const {
        const List* l = cur_.load(std::memory_order_acquire);
        if (!l || !item) return kNone;
        auto it = std::lower_bound(l->begin(), l->end(), std::make_pair(item, uint8_t{0}));
        return it != l->end() && it->first == item ? it->second : kNone;
    }
    uint32_t size() const {
        const List* l = cur_.load(std::memory_order_acquire);
        return l ? static_cast<uint32_t>(l->size()) : 0;
    }
    bool ready() const { return cur_.load(std::memory_order_acquire) != nullptr; }
    uint32_t copy(KilnActionItemKind* out, uint32_t cap) const {
        const List* l = cur_.load(std::memory_order_acquire);
        if (!l) return 0;
        const uint32_t n = std::min<uint32_t>(cap, static_cast<uint32_t>(l->size()));
        for (uint32_t i = 0; i < n && out; ++i) out[i] = KilnActionItemKind{(*l)[i].first, (*l)[i].second, {}};
        return static_cast<uint32_t>(l->size());
    }
    static constexpr uint32_t kMax = 8192;

private:
    std::mutex mu_;
    std::atomic<const List*> cur_{nullptr};
    std::vector<std::unique_ptr<List>> keep_;
};

// ---- the hooks' bodies ----
// Game-side layout the service resolved (sigdb constants and the reflected ActionSequence / WeaponParameters).
struct Layout {
    int seq_reg = -1;           // 6 rsi (server exe), 7 rdi (client exe): the ActionSequence at the clock site
    int64_t seq_trigger = -1;   // ActionSequence.triggerContext.triggerType (u16)
    int64_t seq_item = -1;      // ActionSequence.triggerContext.usedItem.itemId (u32)
    int64_t server_actor = 0;   // rbp-relative entity slot of server_update_animation_graph_2 (signed)
    int64_t client_actor = 0;   // the same in client_update_animation_graph_2
    int64_t bow_params = -1;    // rbp-relative WeaponParameters at the bow site
    int64_t bow_type = 0, bow_charge = 8, bow_fire = 0x10;  // WeaponParameters fields
};

// What a hook needs from the process (action_speed.cpp: world/CurrentEntityId, kiln.client, the wrap marks).
struct Env {
    const RateTable* rates = nullptr;
    ActionRecords* records = nullptr;
    RangedTypes* ranged = nullptr;
    Bindings* bindings = nullptr;
    const ItemKinds* items = nullptr;
    uint32_t (*entity_of)(uintptr_t accessor) = nullptr;  // the row's entity (0 when unknown)
    uint32_t local_player = 0;                             // 0 on a dedicated server or without one
    Mark mark{};
    uint32_t now_ms = 0;
};

struct Counters {
    std::atomic<uint64_t> seq{0}, graph{0}, bow{0}, unmarked{0};
};

inline uintptr_t reg_by_index(const KilnRegisters& r, int i) { return i == 6 ? r.rsi : i == 7 ? r.rdi : 0; }

// Whether the run may scale this entity: authority scales every player; prediction and presentation only the local one.
inline bool run_allows(uint8_t scope, uint32_t entity, uint32_t local) {
    if (!is_player(entity)) return false;
    if (scope == kAuthority) return true;
    return (scope == kPrediction || scope == kPresentation) && local != 0 && entity == local;
}

// Sites 01/02: rax -> the step's elapsed ns; rbx = the row accessor; seq_reg = the ActionSequence.
inline void seq_clock(KilnRegisters& r, const Layout& L, const Env& env, Counters& c) {
    if (!env.rates->scaling()) return;
    if (env.mark.program != kProgSim || env.mark.scope == kScopeNone) {
        c.unmarked.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    const uint32_t entity = env.entity_of(r.rbx);
    if (!run_allows(env.mark.scope, entity, env.local_player)) return;
    const auto* seq = reinterpret_cast<const uint8_t*>(reg_by_index(r, L.seq_reg));
    auto* elapsed = reinterpret_cast<int64_t*>(r.rax);
    if (!seq || !elapsed || L.seq_trigger < 0 || L.seq_item < 0) return;
    uint16_t trigger = 0;
    uint32_t item = 0;
    std::memcpy(&trigger, seq + L.seq_trigger, 2);
    std::memcpy(&item, seq + L.seq_item, 4);
    const int kind = classify(trigger, env.items->kind(item), env.ranged->get(entity));
    const float rate = kind == kNone ? 1.0f : env.rates->rate(entity, kind);
    env.records->put(env.mark.scope, entity, rate, env.mark.step);
    if (rate == 1.0f) return;
    *elapsed = scale_elapsed(*elapsed, rate);
    c.seq.fetch_add(1, std::memory_order_relaxed);
}

// Site 06: rax = the graph instance being bound; the caller's rbp frame holds the actor.
inline void graph_bind(KilnRegisters& r, const Layout& L, const Env& env) {
    if (!env.rates->scaling() || env.mark.program != kProgGraph || env.mark.scope == kScopeNone || !r.rax) return;
    const int64_t slot = env.mark.scope == kPresentation ? L.client_actor : L.server_actor;
    if (!slot || !r.rbp) return;
    uint32_t actor = 0;
    std::memcpy(&actor, reinterpret_cast<const uint8_t*>(r.rbp) + slot, 4);
    const float rate = run_allows(env.mark.scope, actor, env.local_player) ? env.records->recent(env.mark.scope, actor, env.mark.step) : 1.0f;
    env.bindings->put(r.rax, rate, env.now_ms);
}

// Site 09 (the evaluator's entry): rcx = the instance, r8b = the pass (0 = first), xmm3's low float = elapsed seconds.
inline void graph_eval(KilnRegisters& r, const Env& env, Counters& c) {
    if (!env.rates->scaling() || (r.r8 & 0xFF) != 0) return;
    const float rate = env.bindings->get(r.rcx, env.now_ms);
    if (rate == 1.0f) return;
    float s;
    std::memcpy(&s, r.xmm3.bytes, 4);
    const float t = scale_delta(s, rate);
    std::memcpy(r.xmm3.bytes, &t, 4);
    c.graph.fetch_add(1, std::memory_order_relaxed);
}

// Site 10: rax = rbp + bow_params = the fresh WeaponParameters; r12 = the row accessor.
inline void bow_timing(KilnRegisters& r, const Layout& L, const Env& env, Counters& c) {
    if (!env.rates->scaling() || env.mark.program != kProgRanged ||
        (env.mark.scope != kAuthority && env.mark.scope != kPrediction))
        return;
    if (L.bow_params < 0 || !r.rbp || r.rax != r.rbp + static_cast<uintptr_t>(L.bow_params)) return;
    const uint32_t entity = env.entity_of(r.r12);
    if (!run_allows(env.mark.scope, entity, env.local_player)) return;
    auto* p = reinterpret_cast<uint8_t*>(r.rax);
    const uint8_t type = p[L.bow_type];
    env.ranged->put(entity, type);
    if (type != kWeaponBow) return;
    const float rate = env.rates->rate(entity, KILN_ACTION_BOW);
    if (rate == 1.0f) return;
    int64_t charge = 0, fire = 0;
    std::memcpy(&charge, p + L.bow_charge, 8);
    std::memcpy(&fire, p + L.bow_fire, 8);
    charge = divide_duration(charge, rate);
    fire = divide_duration(fire, rate);
    std::memcpy(p + L.bow_charge, &charge, 8);
    std::memcpy(p + L.bow_fire, &fire, 8);
    c.bow.fetch_add(1, std::memory_order_relaxed);
}

}  // namespace kc::speed
