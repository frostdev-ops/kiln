// SPDX-License-Identifier: MIT
// kiln.combat: health, hits, invulnerability and combat events on the server world.
//
// Engine path (docs/re/combat.md): impact programs queue HitCandidates (0x150 B) in impact::ImpactSystem's pending
// array; the program impact_hit_conclusion (#511, SYSTEM, the only consumer) dedupes them per (root target, impact),
// then calls applyHitToEntity per target and per ImpactHierarchy parent; that function changes Health through attrAdd,
// flags the killing blow and emits the HitEvent clients see. The program `death` (#38) turns Health < 1 into the
// Actor's dead bit and an EntityDiedEvent on the next step; network_health_attribute copies Health into the
// replicated NetworkHealth every step.
//
// Kiln hooks two functions:
//  - impact_hit_conclusion's update: before the original runs (worker thread, the program owns the ImpactSystem), it
//    takes the step's entity hits out of the list when the listener wants to see them first (held one step), puts
//    back the hits the listener decided on in the previous Pre, and appends damage() requests.
//  - applyHitToEntity: drops damage hits on invulnerable entities, and records health before/after (DAMAGED events,
//    the killing blow's attacker for the death event, damage() results).
// EntityDiedEvents are read by an own SYSTEM program registered through kiln.world (events_in EntityDiedEvent).
// The listener (managed Kiln.Combat) is called once per step in Pre, where every decision is applied.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "attr_table.h"
#include "combat_internal.h"
#include "hold_filter.h"
#include "kiln_api.h"
#include "kiln_hook.hpp"
#include "safetyhook.hpp"  // Context only: mid handlers keep their safetyhook::Context& signature
#include "services/kiln_assets.h"
#include "services/kiln_combat.h"
#include "services/kiln_spawn.h"
#include "services/kiln_types.h"
#include "services/kiln_world.h"

using namespace kc;
static_assert(sizeof(KilnCombatEvent) == 144, "KilnCombatEvent layout (managed mirror: Kiln.Combat/Native.cs)");
static_assert(sizeof(KilnAttributeIds) == 28 && sizeof(KilnAttributeValue) == 40, "attribute layouts (managed mirror: Kiln.Combat/Native.cs)");

namespace {

constexpr const char* kName = "kiln.combat";
constexpr uint32_t kKilnImpactTag = 0xCB000000u;  // impact ids of damage() hits: 0xCBxxxxxx
constexpr uint64_t kKillMemorySteps = 750;        // ~30 s: a killing blow names the killer of a death this recent
constexpr uint64_t kStatusSteps = 1500;           // ~60 s: hit_status keeps finished requests this long
constexpr uint64_t kDyingSteps = 50;              // a candidate not marked dead within ~2 s survived
constexpr uint64_t kCorpseSteps = 750;            // ~30 s: loot containers near a corpse count as its drops
constexpr uint32_t kMaxImpactsPerTarget = 4;      // the conclusion's per-target impact map holds 4 (docs/re/combat.md)
constexpr uint32_t kPlayerSlots = 16;             // dedicated server: player entities are slot + 1
constexpr uint32_t kDestroyOnLootHash = fnv1a("keen::ecs::DestroyOnLoot");
constexpr uint32_t kInventoryHash = fnv1a("keen::ecs::Inventory");
// ExperienceSource {lastHitPlayerId +0, xpMod +4}: combat_experience_source sets lastHitPlayerId from each HitEvent whose
// root source is a player (or a player's summon or guard) and gives a death's XP to it; a hit from no one leaves it.
constexpr uint32_t kExperienceSourceHash = fnv1a("keen::ecs::ExperienceSource");

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;

void logf(int32_t level, const char* fmt, ...) {
    char buf[768];
    va_list ap;
    va_start(ap, fmt);
    int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (static_cast<size_t>(n) >= sizeof buf) n = sizeof buf - 1;
    g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(n)});
}
#define LOGE(...) logf(KILN_LOG_ERROR, __VA_ARGS__)
#define LOGW(...) logf(KILN_LOG_WARN, __VA_ARGS__)
#define LOGI(...) logf(KILN_LOG_INFO, __VA_ARGS__)

// ---- feature state ----
bool g_health_ok = false, g_hits_ok = false, g_deaths_ok = false;
bool g_falls_ok = false, g_regen_ok = false, g_attr_ok = false;
kiln::RawHook g_conclusion_hook, g_apply_hook;
kiln::MidHook2 g_fall_check_hook, g_fall_store_hook, g_regen_hook, g_attr_hook;
const KilnWorldApi* g_world_api = nullptr;
const KilnSpawnApi* g_spawn_api = nullptr;
KilnHandle g_died_program = 0;
int32_t g_died_slot = -1;
bool g_watch_all = false;  // lab switch (<data>/kiln.combat/watch-all): RESPAWNED for every revived victim

std::atomic<uintptr_t> g_world{0};
std::atomic<void*> g_registry{nullptr};  // impact::AttributeRegistry
std::atomic<void*> g_system{nullptr};    // impact::ImpactSystem
std::atomic<uint64_t> g_step{0};
std::atomic<uint64_t> g_pre_runs{0};

// ---- listener ----
std::atomic<KilnCombatListenerFn> g_listener{nullptr};
std::atomic<void*> g_listener_user{nullptr};
std::atomic<uint32_t> g_interest{0};
std::atomic<bool> g_holding{false};
static_assert(kHoldOffsets[0] == HitOffsets::target && kHoldOffsets[1] == HitOffsets::root_target &&
              kHoldOffsets[2] == HitOffsets::source && kHoldOffsets[3] == HitOffsets::root_source);
HoldFilter g_hold_filter;  // g_mu: the entities the handlers watch (hold_filter, since 0.2.0-dev.27); off: hold every hit
std::atomic<uint64_t> c_unheld{0};  // hits the filter let through without holding (logged with the filter)

// ---- counters ----
std::atomic<uint64_t> c_seen{0}, c_held{0}, c_released{0}, c_canceled{0}, c_modified{0}, c_kiln{0}, c_god{0},
    c_god_restored{0}, c_deaths{0}, c_delivered{0}, c_game_died{0}, c_deferred{0}, c_drops{0}, c_drops_canceled{0}, c_repeats{0};
std::atomic<uint64_t> c_falls{0}, c_falls_canceled{0}, c_falls_modified{0}, c_regen{0}, c_regen_canceled{0}, c_attr{0},
    c_attr_corrected{0}, c_kb{0}, c_kb_modified{0};

struct HitBuf {
    alignas(16) uint8_t b[kMaxHit];
};
struct Held {
    uint64_t id;
    uint64_t step;
    uint64_t request;  // damage() id for AS_GAME_HIT requests, else 0
    HitBuf rec;
};
struct Request {
    uint64_t id;
    KilnOwner owner;
    uint32_t flags;
    bool deferred;  // already counted in c_deferred
    HitBuf rec;
};
struct Kill {
    uint32_t attacker, source;
    uint64_t step;
    uint8_t origin;
    uint32_t flags;
};
struct ReqState {
    KilnHitStatus st;
    uint64_t step_req, step_done;
    KilnOwner owner;
};

std::mutex g_mu;  // everything below
std::vector<Held> g_held_new;  // taken from the pending list during this step's conclusion
std::vector<Held> g_release;   // decided in Pre; put back in the next conclusion
std::vector<Request> g_requests;
std::unordered_map<uint64_t, ReqState> g_req_state;
std::unordered_map<uint32_t, uint64_t> g_req_by_impact;
std::vector<uint64_t> g_injected;  // damage() requests put into the list by the running conclusion
std::vector<KilnCombatEvent> g_damaged;
std::unordered_map<uint32_t, Kill> g_kills;
std::vector<uint32_t> g_died;                                           // the game's EntityDiedEvents
std::unordered_map<uint32_t, uint64_t> g_dying;                         // may have died (killing blow, set 0) -> step
std::unordered_map<uint32_t, uint64_t> g_reported;                      // DIED sent -> step (until alive again)
std::unordered_map<uint32_t, uint64_t> g_watch;                         // dead players -> step they died
struct Corpse {
    KilnVec3 at;
    uint64_t step;
    uint32_t killer;
    bool baseline = true;                     // next scan records what was already there
    std::unordered_set<uint32_t> before;      // loot entities near it at death (never reported)
    std::unordered_set<uint32_t> sighted;     // seen once; reported on the next scan
    std::unordered_set<uint32_t> reported;
};
std::unordered_map<uint32_t, Corpse> g_corpses;
std::unordered_map<uint32_t, std::unordered_set<KilnOwner>> g_god;     // invulnerable -> holders
std::unordered_map<uint32_t, int32_t> g_god_floor;                      // last health seen
std::atomic<uint32_t> g_god_count{0};
// Fall damage held for the listener: seen in fall_damage_infliction (worker) with its pct zeroed, reported in Pre,
// decided, and applied by the same program in the next step (docs/re/combat.md "Fall damage").
struct FallHeld {
    uint32_t entity;
    float pct, dist;
    uint64_t step;
};
struct FallDecision {
    bool canceled;
    float orig_pct;   // the game's fraction (restored, negated, after the infliction: the game's "applied" mark)
    float pct;        // the fraction to apply now (orig_pct, or the decided amount converted)
    uint64_t step;
};
std::vector<FallHeld> g_fall_held;
std::unordered_map<uint32_t, FallDecision> g_fall_decided;
std::atomic<uint32_t> g_fall_pending{0};  // entries in g_fall_decided + g_fall_held (workers skip their lock at 0)
struct RegenRec {
    uint32_t entity;
    int32_t amount;
};
std::vector<RegenRec> g_regen;  // health_recharge points withheld this step (re-added in Pre)
struct AttrRec {
    uint32_t entity;
    int32_t before, after;
};
std::vector<AttrRec> g_attr;    // changeAttributeValue health gains of this step
uint64_t g_next_id = 1;
uint32_t g_next_impact = 1;

template <typename T>
T& at(uint8_t* p, int64_t off) {
    return *reinterpret_cast<T*>(p + off);
}
template <typename T>
T get(const uint8_t* p, int64_t off) {
    T v;
    std::memcpy(&v, p + off, sizeof v);
    return v;
}

bool on_tick() { return g_api->on_tick_thread(); }
uintptr_t world() { return g_world.load(std::memory_order_acquire); }
const void* registry() { return g_registry.load(std::memory_order_acquire); }
bool is_player_id(uint32_t e) { return e >= 1 && e <= 1023; }

bool god_has(uint32_t e) {
    if (g_god_count.load(std::memory_order_relaxed) == 0) return false;
    std::lock_guard lock(g_mu);
    return g_god.count(e) != 0;
}

bool position_of(uint32_t e, KilnVec3* out) {
    WorldTransform t{};
    if (!transform(world(), e, &t)) return false;
    *out = KilnVec3{fixed_to_m(t.pos[0]), fixed_to_m(t.pos[1]), fixed_to_m(t.pos[2])};
    return true;
}

KilnCombatEvent blank(uint32_t kind) {
    KilnCombatEvent ev{};
    ev.size = sizeof ev;
    ev.kind = kind;
    ev.health_before = ev.health_after = ev.health_max = -1;
    ev.step = g_step.load();
    return ev;
}

void fill_from_hit(KilnCombatEvent& ev, const uint8_t* r) {
    const uint32_t target = get<uint32_t>(r, HitOffsets::target);
    const uint32_t root = get<uint32_t>(r, HitOffsets::root_target);
    ev.target = root ? root : target;
    ev.part = target;
    ev.attacker = get<uint32_t>(r, HitOffsets::root_source);
    ev.source = get<uint32_t>(r, HitOffsets::source);
    ev.hit_flags = get<uint32_t>(r, H.flags);
    ev.damage_types = get<uint16_t>(r, HitOffsets::damage_types);
    ev.reaction = get<uint16_t>(r, HitOffsets::reaction);
    ev.attack_category = get<uint8_t>(r, HitOffsets::attack_category);
    ev.weapon_category = static_cast<uint8_t>(get<uint32_t>(r, HitOffsets::weapon_category));
    ev.impact_id = get<uint32_t>(r, HitOffsets::impact_id);
    ev.origin = (ev.impact_id & 0xFF000000u) == kKilnImpactTag ? KILN_ORIGIN_KILN : KILN_ORIGIN_GAME;
    ev.is_player = is_player_id(ev.target);
    const float* p = reinterpret_cast<const float*>(r + HitOffsets::position);
    ev.position = KilnVec3{p[0], p[1], p[2]};
    const float* d = reinterpret_cast<const float*>(r + HitOffsets::direction);
    ev.direction = KilnVec3{d[0], d[1], d[2]};
    ev.push_back = get<uint8_t>(r, HitOffsets::push_back);
}

// ============================================================================ the conclusion hook (worker thread) ==

void finalize_kill(HitBuf& r) {
    // KILN_DAMAGE_KILL: the amount is the target's health right now (the conclusion owns Health writes).
    int32_t cur = 0, max = 0;
    const uint32_t target = get<uint32_t>(r.b, HitOffsets::target);
    if (read_health(world(), registry(), target, &cur, &max) && cur > 0) at<int32_t>(r.b, H.health_change) = -cur;
}

bool inject(void* sys, const HitBuf& r) {
    uint8_t* slot = alloc_hit(sys);
    if (!slot) return false;
    return copy_bytes(slot, r.b, static_cast<size_t>(H.size));
}

void before_conclusion(void* sys) {
    std::lock_guard lock(g_mu);
    const uint64_t step = g_step.load();
    if (g_holding.load()) {
        uint8_t* data = nullptr;
        uint64_t count = 0;
        if (read_list(sys, &data, &count) && count) {
            uint64_t kept = 0;
            for (uint64_t i = 0; i < count; ++i) {
                uint8_t* rec = data + i * H.size;
                uint8_t kind = 0xFF;
                if (!read_u8(rec + H.kind, &kind)) break;
                if (kind == 0 && !g_hold_filter.wants(rec)) c_unheld.fetch_add(1, std::memory_order_relaxed);
                else if (kind == 0) {  // entity hit: hold it for the listener
                    Held h{g_next_id++, step, 0, {}};
                    if (copy_bytes(h.rec.b, rec, static_cast<size_t>(H.size))) {
                        g_held_new.push_back(h);
                        c_held.fetch_add(1, std::memory_order_relaxed);
                        continue;
                    }
                }
                if (kept != i) move_bytes(data + kept * H.size, rec, static_cast<size_t>(H.size));
                ++kept;
            }
            if (kept != count) write_count(sys, kept);
        }
    }
    // Hits held earlier that no Pre has looked at for two steps (listener gone, Pre not running) go back unchanged.
    for (auto it = g_held_new.begin(); it != g_held_new.end();) {
        if (step > it->step + 2) {
            g_release.push_back(*it);
            it = g_held_new.erase(it);
        } else {
            ++it;
        }
    }
    for (auto& h : g_release) {
        if (inject(sys, h.rec)) c_released.fetch_add(1, std::memory_order_relaxed);
        if (h.request) {
            auto s = g_req_state.find(h.request);
            if (s != g_req_state.end() && s->second.st.state == KILN_HIT_HELD) {
                s->second.st.state = KILN_HIT_QUEUED;
                g_injected.push_back(h.request);
            }
        }
    }
    g_release.clear();
    // Impacts per root target already in the list: the conclusion applies at most kMaxImpactsPerTarget per target.
    std::unordered_map<uint32_t, std::unordered_set<uint32_t>> impacts;
    if (!g_requests.empty()) {
        uint8_t* data = nullptr;
        uint64_t count = 0;
        if (read_list(sys, &data, &count))
            for (uint64_t i = 0; i < count && i < 4096; ++i) {
                uint32_t t = 0, rt = 0, imp = 0;
                uint8_t kind = 0xFF;
                const uint8_t* rec = data + i * H.size;
                if (!read_u8(rec + H.kind, &kind) || kind != 0 || !read_u32(rec + HitOffsets::target, &t) ||
                    !read_u32(rec + HitOffsets::root_target, &rt) || !read_u32(rec + HitOffsets::impact_id, &imp))
                    continue;
                impacts[rt ? rt : t].insert(imp);
            }
        for (const auto& h : g_held_new) {  // this step's held hits come back next step, together with those below
            if (h.step != step) continue;
            const uint32_t t = get<uint32_t>(h.rec.b, HitOffsets::target), rt = get<uint32_t>(h.rec.b, HitOffsets::root_target);
            impacts[rt ? rt : t].insert(get<uint32_t>(h.rec.b, HitOffsets::impact_id));
        }
    }
    std::vector<Request> later;
    for (auto& q : g_requests) {
        const uint32_t target = get<uint32_t>(q.rec.b, HitOffsets::target);
        auto& set = impacts[target];
        if (set.size() >= kMaxImpactsPerTarget) {
            if (!q.deferred) c_deferred.fetch_add(1, std::memory_order_relaxed);
            q.deferred = true;
            later.push_back(q);
            continue;
        }
        set.insert(get<uint32_t>(q.rec.b, HitOffsets::impact_id));
        if ((q.flags & KILN_DAMAGE_KILL)) finalize_kill(q.rec);
        const bool as_game = (q.flags & KILN_DAMAGE_AS_GAME_HIT) && g_holding.load();
        if (as_game && !g_hold_filter.wants(q.rec.b)) c_unheld.fetch_add(1, std::memory_order_relaxed);
        if (as_game && g_hold_filter.wants(q.rec.b)) {
            g_held_new.push_back(Held{g_next_id++, step, q.id, q.rec});
            c_held.fetch_add(1, std::memory_order_relaxed);
            auto s = g_req_state.find(q.id);
            if (s != g_req_state.end()) s->second.st.state = KILN_HIT_HELD;
            continue;
        }
        if (inject(sys, q.rec)) {
            g_injected.push_back(q.id);
        } else {
            auto s = g_req_state.find(q.id);
            if (s != g_req_state.end()) s->second.st.state = KILN_HIT_DROPPED, s->second.step_done = step;
        }
    }
    g_requests.swap(later);
}

void after_conclusion() {
    std::lock_guard lock(g_mu);
    const uint64_t step = g_step.load();
    for (uint64_t id : g_injected) {
        auto s = g_req_state.find(id);
        if (s == g_req_state.end() || s->second.st.state != KILN_HIT_QUEUED) continue;
        s->second.st.state = KILN_HIT_DROPPED;  // the conclusion did not apply it (target gone, no handleHits, deduped)
        s->second.step_done = step;
        s->second.st.steps = static_cast<uint32_t>(step - s->second.step_req);
    }
    g_injected.clear();
}

void __fastcall hk_conclusion(void* ctx) {
    void* sys = g_system.load(std::memory_order_acquire);
    if (sys) before_conclusion(sys);
    g_conclusion_hook.call<void>(ctx);
    if (sys) after_conclusion();
}

void __fastcall hk_apply_hit(uint8_t* prop, void* handle, void* ctx, uint8_t* hit) {
    uint32_t target = 0, impact = 0, flags = 0;
    uint8_t kind = 0xFF;
    int32_t hc = 0;
    const bool readable = read_u32(hit + HitOffsets::target, &target) && read_u8(hit + H.kind, &kind) &&
                          read_u32(hit + H.health_change, reinterpret_cast<uint32_t*>(&hc)) &&
                          read_u32(hit + HitOffsets::impact_id, &impact) && read_u32(hit + H.flags, &flags);
    if (!readable || kind != 0) {
        g_apply_hook.call<void>(prop, handle, ctx, hit);
        return;
    }
    c_seen.fetch_add(1, std::memory_order_relaxed);
    const bool kiln = (impact & 0xFF000000u) == kKilnImpactTag;
    if (hc < 0 && god_has(target)) {
        c_god.fetch_add(1, std::memory_order_relaxed);
        if (kiln) {
            std::lock_guard lock(g_mu);
            if (auto r = g_req_by_impact.find(impact); r != g_req_by_impact.end()) {
                auto s = g_req_state.find(r->second);
                if (s != g_req_state.end() && s->second.st.state == KILN_HIT_QUEUED) {
                    s->second.st.state = KILN_HIT_DROPPED;
                    s->second.step_done = g_step.load();
                }
            }
        }
        return;  // the hit never happened (no health change, no HitEvent, no reaction)
    }
    const uint32_t interest = g_interest.load(std::memory_order_relaxed);
    const bool track = hc != 0 && (kiln || (interest & (KILN_COMBAT_MASK(KILN_COMBAT_DAMAGED) |
                                                         KILN_COMBAT_MASK(KILN_COMBAT_DIED))));
    int32_t before = -1, after = -1, max = -1;
    if (track && !read_health(world(), registry(), target, &before, &max)) before = -1;
    g_apply_hook.call<void>(prop, handle, ctx, hit);
    if (!track) return;
    if (before >= 0 && !read_health(world(), registry(), target, &after, &max)) after = -1;
    const bool killing = before > 0 && after >= 0 && after <= 0;
    std::lock_guard lock(g_mu);
    if (killing) {
        uint32_t attacker = 0, source = 0;
        read_u32(hit + HitOffsets::root_source, &attacker);
        read_u32(hit + HitOffsets::source, &source);
        g_kills[target] = Kill{attacker, source, g_step.load(), static_cast<uint8_t>(kiln ? KILN_ORIGIN_KILN : KILN_ORIGIN_GAME),
                               flags};
        g_dying[target] = g_step.load();
    }
    if (kiln) {
        if (auto r = g_req_by_impact.find(impact); r != g_req_by_impact.end()) {
            auto s = g_req_state.find(r->second);
            if (s != g_req_state.end() && s->second.st.state == KILN_HIT_QUEUED) {
                s->second.st.state = KILN_HIT_APPLIED;
                s->second.st.health_before = before;
                s->second.st.health_after = after;
                s->second.st.killing_blow = killing;
                s->second.step_done = g_step.load();
                s->second.st.steps = static_cast<uint32_t>(s->second.step_done - s->second.step_req);
                c_kiln.fetch_add(1, std::memory_order_relaxed);
            }
        }
    }
    if (before >= 0 && (interest & KILN_COMBAT_MASK(KILN_COMBAT_DAMAGED)) && g_damaged.size() < 20000) {
        KilnCombatEvent ev = blank(KILN_COMBAT_DAMAGED);
        HitBuf copy;
        if (copy_bytes(copy.b, hit, static_cast<size_t>(H.size))) fill_from_hit(ev, copy.b);
        ev.target = target;  // the entity whose health changed (each hierarchy level reports itself)
        ev.amount = after >= 0 ? before - after : -hc;
        ev.health_before = before;
        ev.health_after = after;
        ev.health_max = max;
        ev.lethal = killing;
        if (killing) ev.hit_flags |= kFlagKillingBlow;
        ev.id = g_next_id++;
        g_damaged.push_back(ev);
    }
}

// ============================================================================ death program (worker thread) =====

void KILN_CALL died_each(KilnProgramCtx*, uint64_t* iter, KilnEntity, void*) {
    if (g_died_slot < 0) return;
    uint32_t ids[512];
    uint32_t n = 0;
    if (!walk_died_queue(reinterpret_cast<const uint8_t*>(iter[g_died_slot]), ids, 512, &n) || n == 0) return;
    std::lock_guard lock(g_mu);
    for (uint32_t i = 0; i < n; ++i)
        if (g_died.size() < 20000) g_died.push_back(ids[i]);
}

// ============================================================================ falls, regeneration, attribute heals (worker threads)

uint32_t seh_current_entity(void* program_ctx) {
    __try {
        uint32_t id = 0;
        const uint32_t* p = E.current_entity(program_ctx, &id);
        return p ? *p : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

float* seh_fall_pct(uintptr_t rbp) {
    __try {
        auto* dfd = *reinterpret_cast<uint8_t**>(rbp + E.fall_frame);
        return dfd ? reinterpret_cast<float*>(dfd + E.fall_pct) : nullptr;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

struct FallRestore {
    uintptr_t dfd = 0;
    float value = 0;
};
thread_local FallRestore tl_fall_restore;

// fall_damage_infliction, per FallDamage entity, before `if (DynamicFallDamage.pct <= 0) skip`.
void hk_fall_check(safetyhook::Context& c) {
    const bool want = (g_interest.load(std::memory_order_relaxed) & KILN_COMBAT_MASK(KILN_COMBAT_FALL)) != 0 &&
                      g_listener.load(std::memory_order_relaxed);
    if (!want && g_fall_pending.load(std::memory_order_relaxed) == 0) return;
    float* pct = seh_fall_pct(c.rbp);
    if (!pct) return;
    const float v = *pct;
    if (v <= 0 && g_fall_pending.load(std::memory_order_relaxed) == 0) return;
    const uint32_t e = seh_current_entity(reinterpret_cast<void*>(c.r15));
    if (!e) return;
    std::lock_guard lock(g_mu);
    if (auto d = g_fall_decided.find(e); d != g_fall_decided.end()) {
        const FallDecision f = d->second;
        g_fall_decided.erase(d);
        g_fall_pending.fetch_sub(1, std::memory_order_relaxed);
        if (f.canceled) {
            *pct = -f.orig_pct;  // what the game leaves after applying it: the next calculation yields 0
        } else {
            *pct = f.pct;        // applied by the original code below; its sign flip is replaced by -orig_pct
            tl_fall_restore = FallRestore{reinterpret_cast<uintptr_t>(pct) - static_cast<uintptr_t>(E.fall_pct), -f.orig_pct};
        }
        return;
    }
    if (v <= 0 || !want) return;
    for (const FallHeld& h : g_fall_held)
        if (h.entity == e) {
            *pct = 0;  // still waiting for the listener
            return;
        }
    if (g_fall_held.size() >= 4096) return;
    g_fall_held.push_back(FallHeld{e, v, pct[-1], g_step.load()});
    g_fall_pending.fetch_add(1, std::memory_order_relaxed);
    *pct = 0;  // skipped this step: no damage, no landing sequence, no sign flip
}

// fall_damage_infliction, the store of the flipped pct (movss [rax+pct], xmm6): keep the game's own "applied" value
// when Kiln applied a different amount.
void hk_fall_store(safetyhook::Context& c) {
    FallRestore& r = tl_fall_restore;
    if (!r.dfd) return;
    if (c.rax == r.dfd) c.xmm6.f32[0] = r.value;
    r.dfd = 0;
}

// health_recharge, before `Health[current] += points`: the points are withheld and re-added in Pre after the listener.
void hk_regen(safetyhook::Context& c) {
    if (!(g_interest.load(std::memory_order_relaxed) & KILN_COMBAT_MASK(KILN_COMBAT_HEAL)) ||
        !g_listener.load(std::memory_order_relaxed))
        return;
    const int32_t amount = static_cast<int32_t>(c.rax & 0xFFFFFFFFu);
    if (amount <= 0) return;
    const uint32_t e = seh_current_entity(reinterpret_cast<void*>(c.r13));
    if (!e) return;
    {
        std::lock_guard lock(g_mu);
        if (g_regen.size() >= 20000) return;
        g_regen.push_back(RegenRec{e, amount});
    }
    c.rax &= ~static_cast<uintptr_t>(0xFFFFFFFFu);  // add 0 (the attribute recalculation still runs)
}

// changeAttributeValue, its record of a Health/Stamina/Mana change (ebx attribute, edi entity, r13d old, r14d new).
void hk_attr(safetyhook::Context& c) {
    if (!(g_interest.load(std::memory_order_relaxed) & KILN_COMBAT_MASK(KILN_COMBAT_HEAL))) return;
    if (static_cast<uint32_t>(c.rbx) != kAttrHealth) return;
    const int32_t before = static_cast<int32_t>(c.r13 & 0xFFFFFFFFu), after = static_cast<int32_t>(c.r14 & 0xFFFFFFFFu);
    if (after <= before) return;
    std::lock_guard lock(g_mu);
    if (g_attr.size() < 20000) g_attr.push_back(AttrRec{static_cast<uint32_t>(c.rdi), before, after});
}

// ============================================================================ Pre (tick thread) ==================

bool seh_call_listener(KilnCombatListenerFn fn, KilnCombatEvent* ev, uint32_t n, void* user, uint32_t* out) {
    __try {
        *out = fn(ev, n, user);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// The world the singletons below belong to (tick thread). A new server world (a client hosting again) re-reads them
// at its first Pre, before its first conclusion: never the previous world's ImpactSystem or AttributeRegistry.
uintptr_t g_bound_world = 0;

// Per-world state ends with its server world (docs/kiln/client.md, "For service authors"): a client can host several
// worlds one after another, and entity ids repeat (the host's player is entity 1 in every world). Held hits and
// damage requests of the old world never reach the next one's ImpactSystem; deaths, kills, corpses, god-mode holds and
// falls are forgotten. Runs on the server world's thread (its destroy, or the next world's first Pre), so no conclusion
// or program of that world runs meanwhile.
void reset_world(const char* why) {
    size_t held = 0, requests = 0, gods = 0, tracked = 0;
    {
        std::lock_guard lock(g_mu);
        held = g_held_new.size() + g_release.size();
        requests = g_requests.size();
        gods = g_god.size();
        tracked = g_dying.size() + g_reported.size() + g_watch.size() + g_corpses.size() + g_kills.size();
        const uint64_t step = g_step.load();
        for (auto& [id, s] : g_req_state)
            if (s.st.state != KILN_HIT_APPLIED && s.st.state != KILN_HIT_DROPPED)
                s.st.state = KILN_HIT_DROPPED, s.step_done = step;
        g_held_new.clear();
        g_release.clear();
        g_requests.clear();
        g_injected.clear();
        g_damaged.clear();
        g_kills.clear();
        g_died.clear();
        g_dying.clear();
        g_reported.clear();
        g_watch.clear();
        g_corpses.clear();
        g_god.clear();
        g_god_floor.clear();
        g_god_count.store(0);
        g_fall_held.clear();
        g_fall_decided.clear();
        g_fall_pending.store(0);
        g_regen.clear();
        g_attr.clear();
    }
    g_registry.store(nullptr);
    g_system.store(nullptr);
    g_world.store(0, std::memory_order_release);
    g_bound_world = 0;
    LOGI("server world closed (%s): %zu held hit(s) and %zu damage request(s) dropped, %zu god-mode hold(s) and %zu "
         "tracked entit(ies) forgotten; waiting for the next server world", why, held, requests, gods, tracked);
}

void KILN_CALL on_world_event(const KilnWorldEvent* ev, void*) {
    if (ev && ev->kind == KILN_WORLD_SERVER && ev->event == KILN_WORLD_STOPPING && g_bound_world)
        reset_world("the game is closing it");
}

void refresh_singletons(uintptr_t w) {
    static uint64_t last = 0;
    const uint64_t step = g_step.load();
    if (w == g_bound_world && g_registry.load() && g_system.load() && step < last + 250) return;
    last = step;
    g_bound_world = w;
    auto get_s = [&](const char* n) {
        return reinterpret_cast<void*>(g_api->singleton(w, KilnStr{n, std::strlen(n)}));
    };
    void* reg = get_s("impact::AttributeRegistry");
    void* sys = get_s("impact::ImpactSystem");
    if (reg != g_registry.load() || sys != g_system.load()) {
        LOGI("world singletons: AttributeRegistry %p, ImpactSystem %p", reg, sys);
        g_registry.store(reg);
        g_system.store(sys);
    }
}

void god_floors() {
    if (g_god_count.load() == 0) return;
    std::vector<std::pair<uint32_t, int32_t>> fix;
    {
        std::lock_guard lock(g_mu);
        for (auto& [e, floor] : g_god_floor) {
            int32_t cur = 0, max = 0;
            if (!read_health(world(), registry(), e, &cur, &max)) continue;
            if (cur < floor)
                fix.emplace_back(e, floor);
            else
                floor = cur;
        }
    }
    for (auto [e, floor] : fix) {
        auto* comp = static_cast<uint8_t*>(component(world(), kHealthHash, e));
        int32_t *pc = nullptr, *pm = nullptr;
        if (!comp || !health_slots(registry(), comp, &pc, &pm)) continue;
        int32_t cur = 0, max = 0;
        read_health(world(), registry(), e, &cur, &max);
        const int32_t want = std::min(floor, max);
        if (want > cur && add_health(registry(), comp, want - cur)) c_god_restored.fetch_add(1);
    }
}

// Groups the held hits per (root target, impact) as the conclusion does, one event per group (its strongest hit).
struct Group {
    size_t leader;
    std::vector<size_t> members;
    int32_t ev = -1;
    int32_t kb = -1;  // KNOCKBACK event index
};


// A creature drop: the corpse itself, or an entity of a LootContainer_* template (kiln.spawn's template infos).
bool is_loot_container(uint32_t e) {
    if (!g_spawn_api) return false;
    KilnEntitySnapshot snap{};
    snap.size = sizeof snap;
    if (g_spawn_api->entity_info(e, &snap) != KILN_OK || !snap.has_template) return false;
    KilnTemplateInfo ti{};
    ti.size = sizeof ti;
    if (g_spawn_api->template_get(&snap.template_guid, &ti) != KILN_OK) return false;
    return std::strncmp(ti.name, "LootContainer", 13) == 0;
}

// Loot near watched corpses. Containers must be seen in two scans (>= 12 steps old) before they are reported, so
// the game's default_inventory init program has filled them.
void scan_corpses(uintptr_t w, uint64_t step, uint32_t interest, std::vector<KilnCombatEvent>& events) {
    static std::vector<KilnEntity> buf(4096);
    uint32_t n = 0;
    KilnResult rc = g_world_api->find(kDestroyOnLootHash, buf.data(), static_cast<uint32_t>(buf.size()), &n);
    if (rc == KILN_ERR_BUFFER_TOO_SMALL) {
        buf.resize(n + 256);
        rc = g_world_api->find(kDestroyOnLootHash, buf.data(), static_cast<uint32_t>(buf.size()), &n);
    }
    if (rc != KILN_OK && rc != KILN_ERR_NOT_FOUND) return;
    if (rc == KILN_ERR_NOT_FOUND) n = 0;
    std::vector<std::pair<uint32_t, KilnVec3>> loot;
    for (uint32_t i = 0; i < n; ++i) {
        KilnVec3 p{};
        if (position_of(buf[i], &p)) loot.emplace_back(buf[i], p);
    }
    std::lock_guard lock(g_mu);
    for (auto it = g_corpses.begin(); it != g_corpses.end();) {
        Corpse& c = it->second;
        std::vector<uint32_t> nearby;
        for (auto& [e, p] : loot) {
            const double dx = p.x - c.at.x, dy = p.y - c.at.y, dz = p.z - c.at.z;
            if (dx * dx + dy * dy + dz * dz <= 16.0) nearby.push_back(e);
        }
        if (component(w, kInventoryHash, it->first)) nearby.push_back(it->first);
        if (c.baseline) {
            c.baseline = false;
            c.before.insert(nearby.begin(), nearby.end());
            ++it;
            continue;
        }
        for (uint32_t e : nearby) {
            if (c.before.count(e) || c.reported.count(e)) continue;
            if (e != it->first && !is_loot_container(e)) {  // e.g. a player's grave next to the corpse: never touch it
                c.before.insert(e);
                continue;
            }
            if (c.sighted.insert(e).second) continue;  // first sighting: report on the next scan (inventory filled)
            c.reported.insert(e);
            c_drops.fetch_add(1);
            if (!(interest & KILN_COMBAT_MASK(KILN_COMBAT_DROPS))) continue;
            KilnCombatEvent ev = blank(KILN_COMBAT_DROPS);
            ev.id = g_next_id++;
            ev.target = it->first;
            ev.part = e;
            ev.attacker = c.killer;
            ev.origin = KILN_ORIGIN_GAME;
            position_of(e, &ev.position);
            events.push_back(ev);
        }
        it = step > c.step + kCorpseSteps ? g_corpses.erase(it) : std::next(it);
    }
}

void resolve_attributes_if_due(uint64_t step);  // attributes (v2) section below

void on_pre_impl(const KilnTickInfo* tick) {
    g_step.store(tick->step);
    g_pre_runs.fetch_add(1);
    const uintptr_t w = g_api->server_world ? g_api->server_world() : 0;
    if (!w) return;
    if (g_bound_world && w != g_bound_world) reset_world("a new server world started without a stop event");
    g_world.store(w, std::memory_order_release);
    refresh_singletons(w);
    if (!registry()) return;
    resolve_attributes_if_due(tick->step);
    god_floors();

    const KilnCombatListenerFn fn = g_listener.load();
    const uint32_t interest = fn ? g_interest.load() : 0;
    std::vector<Held> held;
    std::vector<KilnCombatEvent> events;
    std::vector<uint32_t> died;
    {
        std::lock_guard lock(g_mu);
        held.swap(g_held_new);
        events.swap(g_damaged);
        died.swap(g_died);
        // Prune memory.
        const uint64_t step = tick->step;
        for (auto it = g_kills.begin(); it != g_kills.end();)
            it = step > it->second.step + kKillMemorySteps ? g_kills.erase(it) : std::next(it);
        for (auto it = g_req_state.begin(); it != g_req_state.end();) {
            const bool done = it->second.st.state == KILN_HIT_APPLIED || it->second.st.state == KILN_HIT_DROPPED;
            if (done && step > it->second.step_done + kStatusSteps) {
                g_req_by_impact.erase(static_cast<uint32_t>(kKilnImpactTag | (it->first & 0xFFFFFF)));
                it = g_req_state.erase(it);
            } else {
                ++it;
            }
        }
    }
    if (!(interest & KILN_COMBAT_MASK(KILN_COMBAT_DAMAGED))) events.clear();

    // HURT / HEAL for held hits.
    std::vector<Group> groups;
    if (!held.empty()) {
        std::unordered_map<uint64_t, size_t> by_key;
        for (size_t i = 0; i < held.size(); ++i) {
            const uint8_t* r = held[i].rec.b;
            const uint32_t t = get<uint32_t>(r, HitOffsets::target), rt = get<uint32_t>(r, HitOffsets::root_target);
            const uint64_t key = (static_cast<uint64_t>(rt ? rt : t) << 32) | get<uint32_t>(r, HitOffsets::impact_id);
            auto [it, fresh] = by_key.try_emplace(key, groups.size());
            if (fresh) groups.push_back(Group{i, {}, -1, -1});
            Group& g = groups[it->second];
            g.members.push_back(i);
            const int32_t a = std::abs(get<int32_t>(held[g.leader].rec.b, H.health_change));
            if (std::abs(get<int32_t>(r, H.health_change)) > a) g.leader = i;
        }
        for (auto& g : groups) {
            const uint8_t* r = held[g.leader].rec.b;
            const int32_t hc = get<int32_t>(r, H.health_change);
            const uint32_t kind = hc < 0 ? KILN_COMBAT_HURT : hc > 0 ? KILN_COMBAT_HEAL : 0;
            const bool want_kind = kind && (interest & KILN_COMBAT_MASK(kind));
            const bool want_kb = kind != KILN_COMBAT_HEAL && (interest & KILN_COMBAT_MASK(KILN_COMBAT_KNOCKBACK)) &&
                                 (get<uint8_t>(r, HitOffsets::push_back) != 0 || get<uint16_t>(r, HitOffsets::reaction) != 0);
            if (!want_kind && !want_kb) continue;
            // A repeat hit of an impact that already hit this target (a swing spanning several steps): the conclusion
            // will skip it, so it is not reported.
            if (get<uint8_t>(r, HitOffsets::ignore_consecutive)) {
                void* sys = g_system.load();
                const uint32_t imp = get<uint32_t>(r, HitOffsets::impact_id);
                if (already_hit(sys, imp, get<uint32_t>(r, HitOffsets::target)) ||
                    already_hit(sys, imp, get<uint32_t>(r, HitOffsets::root_target))) {
                    c_repeats.fetch_add(1, std::memory_order_relaxed);
                    continue;
                }
            }
            KilnCombatEvent ev = blank(kind);
            fill_from_hit(ev, r);
            ev.id = held[g.leader].id;
            ev.step = held[g.leader].step;
            ev.amount = std::abs(hc);
            ev.cause = KILN_HEAL_HIT;
            if (held[g.leader].request) ev.origin = KILN_ORIGIN_KILN;
            int32_t cur = 0, max = 0;
            if (read_health(w, registry(), ev.target, &cur, &max)) {
                ev.health_before = cur;
                ev.health_max = max;
                ev.lethal = kind == KILN_COMBAT_HURT && ev.amount >= cur && cur > 0;
            }
            const float* dir = reinterpret_cast<const float*>(r + HitOffsets::direction);
            ev.direction = KilnVec3{dir[0], dir[1], dir[2]};
            ev.push_back = get<uint8_t>(r, HitOffsets::push_back);
            if (want_kind) {
                g.ev = static_cast<int32_t>(events.size());
                events.push_back(ev);
            }
            if (want_kb) {  // after its HURT event: the listener skips it when the hit was canceled
                ev.kind = KILN_COMBAT_KNOCKBACK;
                g.kb = static_cast<int32_t>(events.size());
                events.push_back(ev);
                c_kb.fetch_add(1, std::memory_order_relaxed);
            }
        }
    }

    // FALL, regeneration and attribute HEAL events.
    std::vector<FallHeld> falls;
    std::vector<RegenRec> regen;
    std::vector<AttrRec> attr;
    {
        std::lock_guard lock(g_mu);
        falls.swap(g_fall_held);
        regen.swap(g_regen);
        attr.swap(g_attr);
        // decisions the program never applied (the entity went away): forget them after ~2 s
        for (auto it = g_fall_decided.begin(); it != g_fall_decided.end();) {
            if (tick->step > it->second.step + 50) {
                it = g_fall_decided.erase(it);
                g_fall_pending.fetch_sub(1, std::memory_order_relaxed);
            } else {
                ++it;
            }
        }
    }
    const size_t fall_base = events.size();
    for (const FallHeld& f : falls) {
        c_falls.fetch_add(1, std::memory_order_relaxed);
        KilnCombatEvent ev = blank(KILN_COMBAT_FALL);
        ev.id = g_next_id++;
        ev.target = ev.part = f.entity;
        ev.is_player = is_player_id(f.entity);
        ev.origin = KILN_ORIGIN_GAME;
        ev.step = f.step;
        ev.fall_distance = f.dist;
        ev.fall_fraction = f.pct;
        int32_t cur = 0, max = 0;
        if (read_health(w, registry(), f.entity, &cur, &max)) {
            ev.health_before = cur;
            ev.health_max = max;
            const double after = std::max(0.0, std::floor(static_cast<double>(cur) - static_cast<double>(max) * f.pct));
            ev.amount = cur - static_cast<int32_t>(after);
            ev.lethal = cur > 0 && after <= 0;
        }
        if (god_has(f.entity)) ev.canceled = 1;
        position_of(f.entity, &ev.position);
        events.push_back(ev);
    }
    const size_t regen_base = events.size();
    for (const RegenRec& r : regen) {
        c_regen.fetch_add(1, std::memory_order_relaxed);
        KilnCombatEvent ev = blank(KILN_COMBAT_HEAL);
        ev.id = g_next_id++;
        ev.target = ev.part = r.entity;
        ev.is_player = is_player_id(r.entity);
        ev.origin = KILN_ORIGIN_GAME;
        ev.cause = KILN_HEAL_REGENERATION;
        ev.amount = r.amount;
        int32_t cur = 0, max = 0;
        if (read_health(w, registry(), r.entity, &cur, &max)) ev.health_before = cur, ev.health_max = max;
        position_of(r.entity, &ev.position);
        events.push_back(ev);
    }
    const size_t attr_base = events.size();
    for (const AttrRec& a : attr) {
        c_attr.fetch_add(1, std::memory_order_relaxed);
        KilnCombatEvent ev = blank(KILN_COMBAT_HEAL);
        ev.id = g_next_id++;
        ev.target = ev.part = a.entity;
        ev.is_player = is_player_id(a.entity);
        ev.origin = KILN_ORIGIN_GAME;
        ev.cause = KILN_HEAL_ATTRIBUTE;
        ev.amount = a.after - a.before;
        ev.health_before = a.before;
        ev.health_after = a.after;
        int32_t cur = 0, max = 0;
        if (read_health(w, registry(), a.entity, &cur, &max)) ev.health_max = max;
        position_of(a.entity, &ev.position);
        events.push_back(ev);
    }

    // DIED. The game's death program marks an actor dead (Actor state bit 7) in the step after its Health fell
    // below 1, but sends its EntityDiedEvent only once the actor is in state 12 (its death sequence; only for
    // simulated actors, i.e. near a player) or alive again. Kiln reports the dead mark: for candidates it saw coming
    // (killing blows, set_health 0, players polled every step) at once, and for anything else when the game's
    // EntityDiedEvent arrives. One report per death; the entity is reported again only after it was alive again.
    {
        if (!died.empty()) {
            c_game_died.fetch_add(died.size());
            static bool logged = false;
            if (!logged) {
                logged = true;
                LOGI("first EntityDiedEvent read: entity %u (%zu this step)", died[0], died.size());
            }
        }
        std::vector<uint32_t> now_dead;
        std::lock_guard lock(g_mu);
        for (uint32_t p = 1; p <= kPlayerSlots; ++p)
            if (!g_reported.count(p) && !g_dying.count(p)) {
                bool has_actor = false;
                if (actor_dead(w, p, &has_actor)) g_dying[p] = tick->step;
            }
        for (auto it = g_dying.begin(); it != g_dying.end();) {
            bool has_actor = false;
            const bool dead = actor_dead(w, it->first, &has_actor);
            if (dead && !g_reported.count(it->first)) {
                now_dead.push_back(it->first);
                it = g_dying.erase(it);
            } else if (!has_actor || dead || tick->step > it->second + kDyingSteps) {
                it = g_dying.erase(it);  // no actor (a prop), already reported, or it survived
            } else {
                ++it;
            }
        }
        for (uint32_t e : died)
            if (!g_reported.count(e) && std::find(now_dead.begin(), now_dead.end(), e) == now_dead.end())
                now_dead.push_back(e);
        // Forget reports of entities that are alive again or gone (the respawn watch reports the former itself).
        for (auto it = g_reported.begin(); it != g_reported.end();) {
            bool has_actor = false;
            const bool dead = actor_dead(w, it->first, &has_actor);
            it = (!has_actor || !dead) && it->second != tick->step ? g_reported.erase(it) : std::next(it);
        }
        for (uint32_t e : now_dead) {
            g_reported[e] = tick->step;
            c_deaths.fetch_add(1);
            if (is_player_id(e) || g_watch_all) g_watch[e] = tick->step;
            KilnCombatEvent ev = blank(KILN_COMBAT_DIED);
            ev.id = g_next_id++;
            ev.target = ev.part = e;
            ev.is_player = is_player_id(e);
            ev.origin = KILN_ORIGIN_OTHER;
            if (auto k = g_kills.find(e); k != g_kills.end()) {
                ev.attacker = k->second.attacker;
                ev.source = k->second.source;
                ev.origin = k->second.origin;
                ev.hit_flags = k->second.flags | kFlagKillingBlow;
                g_kills.erase(k);
            }
            int32_t cur = 0, max = 0;
            if (read_health(w, registry(), e, &cur, &max)) ev.health_after = cur, ev.health_max = max;
            position_of(e, &ev.position);
            if ((interest & KILN_COMBAT_MASK(KILN_COMBAT_DROPS)) && !ev.is_player && g_world_api)
                g_corpses[e] = Corpse{ev.position, tick->step, ev.attacker, {}, {}};
            if (interest & KILN_COMBAT_MASK(KILN_COMBAT_DIED)) events.push_back(ev);
        }
    }

    // RESPAWNED: a watched dead entity whose dead bit cleared (the death program clears it once Health >= 1 again).
    {
        std::vector<uint32_t> back, gone;
        {
            std::lock_guard lock(g_mu);
            for (auto& [e, since] : g_watch) {
                if (since == tick->step) continue;
                bool has_actor = false;
                const bool dead = actor_dead(w, e, &has_actor);
                if (!has_actor) gone.push_back(e);
                else if (!dead) back.push_back(e);
            }
            for (uint32_t e : gone) g_watch.erase(e);
            for (uint32_t e : back) g_watch.erase(e);
        }
        if (interest & KILN_COMBAT_MASK(KILN_COMBAT_RESPAWNED))
            for (uint32_t e : back) {
                KilnCombatEvent ev = blank(KILN_COMBAT_RESPAWNED);
                ev.id = g_next_id++;
                ev.target = ev.part = e;
                ev.is_player = is_player_id(e);
                ev.origin = KILN_ORIGIN_OTHER;
                int32_t cur = 0, max = 0;
                if (read_health(w, registry(), e, &cur, &max)) ev.health_after = cur, ev.health_max = max;
                position_of(e, &ev.position);
                events.push_back(ev);
            }
    }

    // DROPS: loot containers that appear near a corpse (one scan of the DestroyOnLoot entities every 12 steps while
    // a corpse is watched; the corpse itself counts once it has an Inventory: transform_to_loot).
    bool baseline = false;
    {
        std::lock_guard lock(g_mu);
        for (auto& [e, c] : g_corpses) baseline |= c.baseline;
    }
    if (!g_corpses.empty() && (baseline || tick->step % 12 == 0)) scan_corpses(w, tick->step, interest, events);

    // Deliver. Every step, so the listener can change its interest.
    uint32_t next = interest;
    if (fn) {
        if (!seh_call_listener(fn, events.data(), static_cast<uint32_t>(events.size()), g_listener_user.load(), &next)) {
            LOGE("the combat listener faulted; removed (held hits go back unchanged)");
            g_listener.store(nullptr);
            next = 0;
            for (auto& ev : events) ev.canceled = 0;
        } else {
            c_delivered.fetch_add(events.size());
        }
    }

    // Canceled drops: destroy the container (kiln.spawn, tick thread).
    for (auto& ev : events)
        if (ev.kind == KILN_COMBAT_DROPS && ev.canceled && g_spawn_api && g_spawn_api->destroy(ev.part) == KILN_OK)
            c_drops_canceled.fetch_add(1);

    // Falls: the decision is applied by fall_damage_infliction in the next step (hk_fall_check).
    {
        std::lock_guard lock(g_mu);
        for (size_t i = 0; i < falls.size(); ++i) {
            const KilnCombatEvent& ev = events[fall_base + i];
            const FallHeld& f = falls[i];
            FallDecision d{false, f.pct, f.pct, tick->step};
            if (ev.canceled) {
                d.canceled = true;
                c_falls_canceled.fetch_add(1, std::memory_order_relaxed);
            } else if (ev.health_before >= 0 && ev.health_max > 0) {
                // the game's amount (its own rounding) unless a handler changed it; then floor(hp - max * pct) = hp - amount
                const double after = std::max(0.0, std::floor(static_cast<double>(ev.health_before) -
                                                              static_cast<double>(ev.health_max) * f.pct));
                const int32_t orig = ev.health_before - static_cast<int32_t>(after);
                if (ev.amount <= 0) {
                    d.canceled = true;
                    c_falls_canceled.fetch_add(1, std::memory_order_relaxed);
                } else if (ev.amount != orig) {
                    d.pct = static_cast<float>((static_cast<double>(ev.amount) - 0.5) / static_cast<double>(ev.health_max));
                    c_falls_modified.fetch_add(1, std::memory_order_relaxed);
                }
                if (!d.canceled && ev.health_before > 0 && ev.amount >= ev.health_before) g_dying[f.entity] = tick->step + 2;
            }
            // the decision replaces the held entry in the pending count
            if (!g_fall_decided.insert_or_assign(f.entity, d).second) g_fall_pending.fetch_sub(1, std::memory_order_relaxed);
        }
    }
    // Regeneration: add the (decided) points now; attribute heals: take back what a handler removed.
    for (size_t i = 0; i < regen.size(); ++i) {
        const KilnCombatEvent& ev = events[regen_base + i];
        if (ev.canceled) {
            c_regen_canceled.fetch_add(1, std::memory_order_relaxed);
            continue;
        }
        const int32_t amount = std::max(0, ev.amount);
        if (amount == 0) continue;
        auto* comp = static_cast<uint8_t*>(component(w, kHealthHash, regen[i].entity));
        int32_t cur = 0, max = 0;
        if (!comp || !read_health(w, registry(), regen[i].entity, &cur, &max)) continue;
        const int32_t add = std::min(amount, std::max(0, max - cur));
        if (add > 0) add_health(registry(), comp, add);
    }
    for (size_t i = 0; i < attr.size(); ++i) {
        const KilnCombatEvent& ev = events[attr_base + i];
        const int32_t given = attr[i].after - attr[i].before;
        const int32_t want = ev.canceled ? 0 : std::max(0, ev.amount);
        if (want == given) continue;
        auto* comp = static_cast<uint8_t*>(component(w, kHealthHash, attr[i].entity));
        int32_t cur = 0, max = 0;
        if (!comp || !read_health(w, registry(), attr[i].entity, &cur, &max)) continue;
        const int32_t target = std::clamp(cur + (want - given), cur >= 1 ? 1 : 0, std::max(max, 0));
        if (target != cur && add_health(registry(), comp, target - cur)) c_attr_corrected.fetch_add(1, std::memory_order_relaxed);
    }

    // Apply HURT / HEAL decisions, then queue every held hit for this step's conclusion.
    std::lock_guard lock(g_mu);
    std::vector<bool> drop(held.size(), false);
    for (auto& g : groups) {
        if (g.ev < 0) continue;
        const KilnCombatEvent& ev = events[g.ev];
        const int32_t hc = get<int32_t>(held[g.leader].rec.b, H.health_change);
        if (ev.canceled) {
            for (size_t m : g.members) drop[m] = true;
            c_canceled.fetch_add(1);
            continue;
        }
        const int32_t amount = std::max(0, ev.amount);
        if (amount == std::abs(hc)) continue;
        // Every hit of the group gets the decided amount, so whichever one the conclusion keeps applies it.
        for (size_t m : g.members) at<int32_t>(held[m].rec.b, H.health_change) = hc < 0 ? -amount : amount;
        c_modified.fetch_add(1);
    }
    // Knockback decisions (after the hit's own): strength, reaction and direction on every hit of the group.
    for (auto& g : groups) {
        if (g.kb < 0 || drop[g.leader]) continue;
        const KilnCombatEvent& kb = events[g.kb];
        const uint8_t* r = held[g.leader].rec.b;
        const uint8_t pb = kb.canceled ? 0 : kb.push_back;
        const uint16_t reaction = kb.canceled ? 0 : kb.reaction;
        float dir[3] = {static_cast<float>(kb.direction.x), static_cast<float>(kb.direction.y), static_cast<float>(kb.direction.z)};
        const double len = std::sqrt(static_cast<double>(dir[0]) * dir[0] + static_cast<double>(dir[1]) * dir[1] +
                                     static_cast<double>(dir[2]) * dir[2]);
        const float* old_dir = reinterpret_cast<const float*>(r + HitOffsets::direction);
        const bool dir_changed = std::isfinite(len) && len > 1e-6 &&
                                 (dir[0] != old_dir[0] || dir[1] != old_dir[1] || dir[2] != old_dir[2]);
        if (dir_changed) for (float& d : dir) d = static_cast<float>(d / len);
        if (pb == get<uint8_t>(r, HitOffsets::push_back) && reaction == get<uint16_t>(r, HitOffsets::reaction) && !dir_changed)
            continue;
        for (size_t m : g.members) {
            at<uint8_t>(held[m].rec.b, HitOffsets::push_back) = pb;
            at<uint16_t>(held[m].rec.b, HitOffsets::reaction) = reaction;
            if (dir_changed) std::memcpy(held[m].rec.b + HitOffsets::direction, dir, sizeof dir);
        }
        c_kb_modified.fetch_add(1, std::memory_order_relaxed);
    }
    for (size_t i = 0; i < held.size(); ++i) {
        if (drop[i]) {
            if (held[i].request) {
                auto s = g_req_state.find(held[i].request);
                if (s != g_req_state.end()) s->second.st.state = KILN_HIT_DROPPED, s->second.step_done = tick->step;
            }
            continue;
        }
        g_release.push_back(held[i]);
    }
    const uint32_t old = g_interest.exchange(next);
    const bool hold = (next & (KILN_COMBAT_MASK(KILN_COMBAT_HURT) | KILN_COMBAT_MASK(KILN_COMBAT_HEAL) |
                               KILN_COMBAT_MASK(KILN_COMBAT_KNOCKBACK))) != 0 &&
                      g_hits_ok && g_listener.load();
    if (g_holding.exchange(hold) != hold)
        LOGI("%s entity hits for the listener (interest 0x%x)", hold ? "holding" : "no longer holding", next);
    else if (old != next)
        LOGI("listener interest 0x%x", next);
}

void KILN_CALL on_pre(const KilnTickInfo* tick, void*) { on_pre_impl(tick); }

// ============================================================================ attributes (v2) ====================
// Health, stamina, mana, body heat and oxygen of any entity, by the game's attribute id. The ids, the slot order and the
// clamp are read from the game's attribute resource (AttributeContainerResource) at the first server world, laid out as
// kiln.types reflects the types, and every id is checked against the registry's own lookup (docs/re/combat.md
// "Attributes"). Nothing here is hard-coded but the five names.

const KilnTypesApi* g_types_api = nullptr;
const KilnAssetsApi* g_assets_api = nullptr;
KilnResult t_set_health(KilnEntity e, int32_t value, int32_t* applied);  // below

struct AttrPair {
    attr::Pair p;
    uint32_t comp_hash = 0;  // FNV-1a of keen::ecs::<root>
    uint32_t comp_size = 0;
};
std::vector<AttrPair> g_attr_pairs;  // written once on the tick thread (state 1), then read-only
std::atomic<int> g_attr_state{0};     // 0 not resolved yet, 1 ready, 2 failed (logged)
int g_attr_tries = 0;
uint64_t g_attr_next_step = 0;

KilnStr ks(const char* s) { return KilnStr{s, std::strlen(s)}; }
std::string ss(KilnStr s) { return std::string(s.ptr ? s.ptr : "", s.ptr ? s.len : 0); }

// The reflected layout of the types the parser reads. false (with `why`) when a type is missing or not the shape the
// parser handles; the parser then stays off instead of misreading the resource.
bool reflect_layout(attr::Layout* L, uint32_t* container_type, std::string* why) {
    const KilnTypesApi* T = g_types_api;
    auto info = [&](const KilnType* t, KilnTypeInfo* i) {
        *i = KilnTypeInfo{};
        i->size = sizeof *i;
        return t && T->type_info(t, i) == KILN_OK;
    };
    auto field = [&](const KilnType* t, const char* name, KilnTypeField* f) {
        KilnTypeInfo i;
        if (!info(t, &i)) return false;
        for (uint32_t k = 0; k < i.field_count; ++k)
            if (T->type_field(t, k, f) == KILN_OK && ss(f->name) == name) return true;
        return false;
    };
    // `name` of struct `t` must be a BlobArray (kind) of a type of `elem_size` bytes (0: any); returns its element.
    auto blob_array = [&](const KilnType* t, const char* name, uint32_t* offset, const KilnType** elem, KilnTypeInfo* elem_info) {
        KilnTypeField f;
        KilnTypeInfo fi;
        if (!field(t, name, &f) || !info(f.type, &fi) || fi.kind != KILN_TYPE_BLOB_ARRAY || fi.type_size != 8 || !fi.inner) {
            *why = std::string(name) + " is not a BlobArray";
            return false;
        }
        *offset = static_cast<uint32_t>(f.offset);
        *elem = fi.inner;
        return info(fi.inner, elem_info);
    };
    KilnTypeInfo ci, ei, ii, ni, si, ai;
    const KilnType* container = T->type_by_name(ks("keen::AttributeContainerResource"));
    const KilnType *group = nullptr, *idt = nullptr, *namet = nullptr, *st = nullptr, *cmd = nullptr;
    if (!info(container, &ci)) return *why = "keen::AttributeContainerResource is not in the type table", false;
    *container_type = ci.hash;
    if (!blob_array(container, "attributes", &L->container_attrs, &group, &ei)) return false;
    L->elem_size = ei.type_size;
    KilnTypeField tf;
    if (!field(group, "type", &tf)) return *why = "BaseAttributeResource has no type field", false;
    L->off_type = static_cast<uint32_t>(tf.offset);
    if (!blob_array(group, "ids", &L->off_ids, &idt, &ii) || !blob_array(group, "debugNames", &L->off_names, &namet, &ni) ||
        !blob_array(group, "structure", &L->off_structure, &st, &si))
        return false;
    L->id_size = ii.type_size;
    L->name_size = ni.type_size;
    L->st_size = si.type_size;
    // keen::string is a typedef of keen::BlobString
    KilnTypeInfo bs = ni;
    for (int depth = 0; depth < 4 && bs.kind == KILN_TYPE_TYPEDEF && bs.inner; ++depth)
        if (!info(bs.inner, &bs)) break;
    if (bs.kind != KILN_TYPE_BLOB_STRING || bs.type_size != L->name_size) return *why = "debugNames holds no BlobStrings", false;
    if (L->id_size != 4) return *why = "attribute ids are not 4 bytes", false;
    if (!blob_array(st, "calculation", &L->st_calc, &cmd, &ai) || ai.type_size != 4) return *why = "AttributeStructure.calculation is not a BlobArray of 4-byte commands", false;
    L->cmd_size = ai.type_size;
    const KilnType* ops = T->type_by_name(ks("keen::AttributeOps"));
    KilnTypeInfo oi;
    if (!info(ops, &oi) || oi.kind != KILN_TYPE_ENUM) return *why = "keen::AttributeOps is not in the type table", false;
    int found = 0;
    for (uint32_t k = 0; k < oi.field_count; ++k) {
        KilnStr n{};
        int64_t v = 0;
        if (T->type_enum(ops, k, &n, &v) != KILN_OK) continue;
        if (ss(n) == "Load") L->op_load = static_cast<uint32_t>(v), ++found;
        if (ss(n) == "Clamp") L->op_clamp = static_cast<uint32_t>(v), ++found;
    }
    if (found != 2) return *why = "keen::AttributeOps has no Load / Clamp", false;
    return true;
}

// Checks the component type of a pair in kiln.types: a struct that derives from keen::ecs::AttributeComponent and holds
// the pair's slots.
bool check_component(AttrPair* ap, std::string* why) {
    const KilnTypesApi* T = g_types_api;
    const std::string name = "keen::ecs::" + ap->p.root;
    const KilnType* t = T->type_by_name(ks(name.c_str()));
    KilnTypeInfo i{};
    i.size = sizeof i;
    if (!t || T->type_info(t, &i) != KILN_OK || i.kind != KILN_TYPE_STRUCT || ss(i.qualified_name) != name)
        return *why = name + " is not a component type", false;
    if (i.type_size < 8 + 4 * ap->p.attrs.size()) return *why = name + " is too small for its attributes", false;
    bool derived = false;
    int depth = 0;
    for (const KilnType* b = i.inner; b && !derived && depth < 16; ++depth) {  // a bounded walk: reflection is data
        KilnTypeInfo bi{};
        bi.size = sizeof bi;
        if (T->type_info(b, &bi) != KILN_OK) break;
        derived = ss(bi.qualified_name) == "keen::ecs::AttributeComponent";
        b = bi.inner == b ? nullptr : bi.inner;
    }
    if (!derived) return *why = name + " is not an AttributeComponent", false;
    ap->comp_hash = i.hash;
    ap->comp_size = i.type_size;
    return true;
}

bool resolve_attributes(std::string* why) {
    if (!g_types_api || !g_assets_api) return *why = "kiln.types or kiln.assets is not available", false;
    if (!g_health_ok || !registry()) return *why = "no attribute registry (health access is off)", false;
    attr::Layout L;
    uint32_t container_type = 0;
    if (!reflect_layout(&L, &container_type, why)) return false;
    KilnAssetKey keys[8];
    size_t nkeys = 0;
    if (g_assets_api->resource_list(container_type, keys, 8, &nkeys) != KILN_OK || nkeys == 0)
        return *why = "no keen::AttributeContainerResource resource", false;
    std::vector<AttrPair> pairs;
    std::string last_why = "the resource holds no attribute groups";
    for (size_t k = 0; k < std::min<size_t>(nkeys, 8) && pairs.size() < std::size(attr::kKinds); ++k) {
        size_t need = 0;
        g_assets_api->resource_read(&keys[k], nullptr, 0, &need);
        if (!need || need > (8u << 20)) continue;
        std::vector<uint8_t> bytes(need);
        if (g_assets_api->resource_read(&keys[k], bytes.data(), bytes.size(), &need) != KILN_OK || need != bytes.size()) continue;
        std::vector<attr::Group> groups;
        if (!attr::parse_container(bytes.data(), bytes.size(), L, &groups, &last_why)) continue;
        for (const attr::KindSpec& spec : attr::kKinds) {
            if (std::any_of(pairs.begin(), pairs.end(), [&](const AttrPair& a) { return a.p.kind == spec.kind; })) continue;
            AttrPair ap;
            if (attr::resolve_pair(groups, spec, L, &ap.p, &last_why)) pairs.push_back(std::move(ap));
        }
    }
    for (const attr::KindSpec& spec : attr::kKinds)
        if (std::none_of(pairs.begin(), pairs.end(), [&](const AttrPair& a) { return a.p.kind == spec.kind; }))
            return *why = last_why + " (" + spec.root + ")", false;
    // The registry's own lookup must agree with the resource for every id (group root and slot), and the component
    // type must exist.
    for (AttrPair& ap : pairs) {
        for (size_t i = 0; i < ap.p.attrs.size(); ++i) {
            uint32_t root = 0;
            uint16_t slot = 0;
            if (!attr_lookup(registry(), ap.p.attrs[i].id, &root, &slot) || root != ap.p.root_id || slot != i)
                return *why = ap.p.attrs[i].name + ": the registry does not map its id to slot " + std::to_string(i) + " of " + ap.p.root, false;
        }
        if (!check_component(&ap, why)) return false;
    }
    std::sort(pairs.begin(), pairs.end(), [](const AttrPair& a, const AttrPair& b) { return a.p.kind < b.p.kind; });
    for (const AttrPair& ap : pairs) {
        char min[16] = "none";
        if (ap.p.min_id) std::snprintf(min, sizeof min, "0x%08X", ap.p.min_id);
        LOGI("attributes: %s: component keen::ecs::%s (0x%08X), current 0x%08X, min %s, max 0x%08X, %zu slots, the game's formula %s",
             ap.p.root.c_str(), ap.p.root.c_str(), ap.comp_hash, ap.p.cur_id, min, ap.p.max_id, ap.p.attrs.size(),
             ap.p.game_clamp ? "clamps current to [min, max]" : "does NOT clamp (Kiln clamps)");
        if (ap.p.kind == KILN_ATTR_HEALTH && (ap.p.cur_id != kAttrHealth || ap.p.max_id != kAttrHealthMax))
            LOGW("attributes: the resolved Health ids differ from the constants Kiln was built with (0x%08X / 0x%08X)", kAttrHealth,
                 kAttrHealthMax);
    }
    g_attr_pairs = std::move(pairs);
    return true;
}

// Once the first server world runs (and again, a few times, if it failed); tick thread.
void resolve_attributes_if_due(uint64_t step) {
    if (g_attr_state.load(std::memory_order_acquire) != 0 || g_attr_tries >= 3 || step < g_attr_next_step) return;
    ++g_attr_tries;
    g_attr_next_step = step + 250;
    std::string why;
    if (resolve_attributes(&why)) {
        g_attr_state.store(1, std::memory_order_release);
        LOGI("attributes: on (%zu pairs resolved from the game's attribute resource)", g_attr_pairs.size());
    } else if (g_attr_tries >= 3) {
        g_attr_state.store(2, std::memory_order_release);
        LOGW("attributes: off: %s", why.c_str());
    } else {
        LOGI("attributes: not resolved yet (%s), trying again", why.c_str());
    }
}

// Where an attribute id lives: its pair and its slot.
const AttrPair* attr_pair_of(uint32_t id, int* slot) {
    for (const AttrPair& ap : g_attr_pairs)
        for (size_t i = 0; i < ap.p.attrs.size(); ++i)
            if (ap.p.attrs[i].id == id) return *slot = static_cast<int>(i), &ap;
    return nullptr;
}

struct AttrLoaded {
    uint8_t* comp = nullptr;
    int32_t* values = nullptr;
    uint8_t count = 0;
};
bool attr_load(uint32_t entity, const AttrPair& ap, AttrLoaded* out) {
    uint64_t size = 0;
    auto* comp = static_cast<uint8_t*>(component(world(), ap.comp_hash, entity, &size));
    if (!comp) return false;
    out->comp = comp;
    return attr_values(comp, size, ap.p.root_id, &out->values, &out->count) && out->count > ap.p.cur_slot &&
           out->count > ap.p.max_slot;
}
bool attr_slot(const AttrLoaded& l, int slot, int32_t* v) { return slot >= 0 && slot < l.count && read_i32(l.values + slot, v); }

void attr_fill(const AttrPair& ap, const AttrLoaded& l, uint32_t id, int slot, KilnAttributeValue* out) {
    const uint32_t size = out->size;
    *out = KilnAttributeValue{};
    out->size = size;
    out->id = id;
    out->kind = ap.p.kind;
    out->current_id = ap.p.cur_id;
    out->max_id = ap.p.max_id;
    out->is_current = id == ap.p.cur_id;
    attr_slot(l, slot, &out->value);
    attr_slot(l, ap.p.cur_slot, &out->current);
    out->has_max = attr_slot(l, ap.p.max_slot, &out->max);
    out->has_min = ap.p.min_slot >= 0 && attr_slot(l, ap.p.min_slot, &out->min);
}

// Common start of the four functions: availability, thread, the id's pair.
KilnResult attr_enter(uint32_t id, bool write, const AttrPair** ap, int* slot) {
    if (!g_health_ok || !world() || !registry()) return KILN_ERR_UNAVAILABLE;
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (g_attr_state.load(std::memory_order_acquire) != 1) return KILN_ERR_UNAVAILABLE;
    *ap = attr_pair_of(id, slot);
    if (!*ap) return KILN_ERR_INVALID_ARGUMENT;
    if (write && id != (*ap)->p.cur_id) return KILN_ERR_INVALID_ARGUMENT;
    return KILN_OK;
}

KilnResult t_attribute_ids(uint32_t kind, KilnAttributeIds* out) {
    if (!out || out->size < sizeof(KilnAttributeIds)) return KILN_ERR_INVALID_ARGUMENT;
    if (g_attr_state.load(std::memory_order_acquire) != 1) return KILN_ERR_UNAVAILABLE;
    for (const AttrPair& ap : g_attr_pairs) {
        if (ap.p.kind != kind) continue;
        const uint32_t size = out->size;
        *out = KilnAttributeIds{};
        out->size = size;
        out->kind = kind;
        out->current_id = ap.p.cur_id;
        out->min_id = ap.p.min_id;
        out->max_id = ap.p.max_id;
        out->component = ap.comp_hash;
        out->game_clamp = ap.p.game_clamp;
        return KILN_OK;
    }
    return KILN_ERR_INVALID_ARGUMENT;
}

KilnResult t_attribute_get(KilnEntity e, uint32_t id, KilnAttributeValue* out) {
    if (!out || out->size < sizeof(KilnAttributeValue)) return KILN_ERR_INVALID_ARGUMENT;
    const AttrPair* ap = nullptr;
    int slot = 0;
    if (KilnResult rc = attr_enter(id, false, &ap, &slot); rc != KILN_OK) return rc;
    AttrLoaded l;
    if (!attr_load(e, *ap, &l)) return KILN_ERR_NOT_FOUND;
    attr_fill(*ap, l, id, slot, out);
    return KILN_OK;
}

// Sets the pair's current value to `want` (already clamped). Health goes through set_health (god mode, dying list).
KilnResult attr_write(KilnEntity e, const AttrPair& ap, AttrLoaded& l, int64_t want, int32_t cur) {
    int32_t min = 0, max = 0;
    const bool has_max = attr_slot(l, ap.p.max_slot, &max);
    if (ap.p.min_slot >= 0) attr_slot(l, ap.p.min_slot, &min);
    if (has_max) want = std::min<int64_t>(std::max<int64_t>(want, min), std::max(max, min));
    else want = std::max<int64_t>(want, min);
    want = std::min<int64_t>(want, INT32_MAX);
    if (ap.p.kind == KILN_ATTR_HEALTH) {
        int32_t applied = 0;
        return t_set_health(e, static_cast<int32_t>(want), &applied);
    }
    // want and cur are both s32, so want - cur can need 33 bits (an extreme current value): then a second add from the
    // value the first one left (the game's formula may have clamped it).
    for (int pass = 0; pass < 2 && want != cur; ++pass) {
        const int64_t step = std::clamp<int64_t>(want - cur, INT32_MIN, INT32_MAX);
        if (!attr_add_id(registry(), l.comp, static_cast<int32_t>(step), ap.p.cur_id)) return KILN_ERR_INTERNAL;
        if (step == want - cur) break;  // the usual case: one add
        if (!attr_slot(l, ap.p.cur_slot, &cur)) return KILN_ERR_INTERNAL;
    }
    return KILN_OK;
}

KilnResult attr_change(KilnEntity e, uint32_t id, int64_t value, bool relative, KilnAttributeValue* out) {
    if (out && out->size < sizeof(KilnAttributeValue)) return KILN_ERR_INVALID_ARGUMENT;
    const AttrPair* ap = nullptr;
    int slot = 0;
    if (KilnResult rc = attr_enter(id, true, &ap, &slot); rc != KILN_OK) return rc;
    AttrLoaded l;
    int32_t cur = 0;
    if (!attr_load(e, *ap, &l) || !attr_slot(l, ap->p.cur_slot, &cur)) return KILN_ERR_NOT_FOUND;
    if (KilnResult rc = attr_write(e, *ap, l, relative ? static_cast<int64_t>(cur) + value : value, cur); rc != KILN_OK) return rc;
    if (out) {
        AttrLoaded after;
        if (!attr_load(e, *ap, &after)) return KILN_ERR_NOT_FOUND;
        attr_fill(*ap, after, id, slot, out);
    }
    return KILN_OK;
}

KilnResult t_attribute_set(KilnEntity e, uint32_t id, int32_t value, KilnAttributeValue* out) {
    return attr_change(e, id, value, false, out);
}
KilnResult t_attribute_add(KilnEntity e, uint32_t id, int32_t delta, KilnAttributeValue* out) {
    return attr_change(e, id, delta, true, out);
}

// ============================================================================ table ==============================

KilnResult t_health(KilnEntity e, KilnHealth* out) {
    if (!out || out->size < sizeof(KilnHealth)) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_health_ok || !world() || !registry()) return KILN_ERR_UNAVAILABLE;
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    int32_t cur = 0, max = 0;
    if (!read_health(world(), registry(), e, &cur, &max)) return KILN_ERR_NOT_FOUND;
    bool has_actor = false;
    out->current = cur;
    out->max = max;
    // Not the raw mark: the game's death program re-derives it from Health once per step, so right after a heal of a
    // dead entity (or SetHealth(0) of a living one) the raw mark is one step behind. `dead` is the mark as that run
    // will leave it (docs/re/combat.md "Death").
    out->dead = actor_dead_after_check(world(), e, cur, &has_actor);
    out->has_actor = has_actor;
    out->invulnerable = god_has(e);
    return KILN_OK;
}

KilnResult t_set_health(KilnEntity e, int32_t value, int32_t* applied) {
    if (!g_health_ok || !world() || !registry()) return KILN_ERR_UNAVAILABLE;
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    auto* comp = static_cast<uint8_t*>(component(world(), kHealthHash, e));
    int32_t *pc = nullptr, *pm = nullptr;
    if (!comp || !health_slots(registry(), comp, &pc, &pm)) return KILN_ERR_NOT_FOUND;
    int32_t cur = 0, max = 0;
    if (!read_health(world(), registry(), e, &cur, &max)) return KILN_ERR_NOT_FOUND;
    int32_t want = std::clamp(value, 0, std::max(max, 0));
    // God mode: never 0 (the game's death check runs in the step before the next Pre could restore it); a lower value
    // is undone at the next Pre like any other loss.
    if (want < 1 && god_has(e)) want = std::min(1, std::max(max, 0));
    if (want != cur && !add_health(registry(), comp, want - cur)) return KILN_ERR_INTERNAL;
    read_health(world(), registry(), e, &cur, &max);
    {
        std::lock_guard lock(g_mu);
        if (auto f = g_god_floor.find(e); f != g_god_floor.end() && cur > f->second) f->second = cur;
        if (cur <= 0) g_dying[e] = g_step.load();
    }
    if (applied) *applied = cur;
    return KILN_OK;
}

KilnResult t_heal(KilnEntity e, int32_t amount, int32_t* healed) {
    if (amount < 0) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_health_ok || !world() || !registry()) return KILN_ERR_UNAVAILABLE;
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    int32_t cur = 0, max = 0;
    if (!read_health(world(), registry(), e, &cur, &max)) return KILN_ERR_NOT_FOUND;
    const int64_t want = std::min<int64_t>(static_cast<int64_t>(cur) + amount, max);
    int32_t now = cur;
    if (KilnResult rc = t_set_health(e, static_cast<int32_t>(want), &now); rc != KILN_OK) return rc;
    if (healed) *healed = now - cur;
    return KILN_OK;
}

KilnResult t_damage(KilnOwner owner, const KilnDamageRequest* req, uint64_t* out_id) {
    if (!req || req->size < sizeof(KilnDamageRequest) || !req->target) return KILN_ERR_INVALID_ARGUMENT;
    if (!(req->flags & KILN_DAMAGE_KILL) && req->amount == 0) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_hits_ok || !world() || !registry() || !g_system.load()) return KILN_ERR_UNAVAILABLE;
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    WorldTransform tt{};
    if (!transform(world(), req->target, &tt)) return KILN_ERR_NOT_FOUND;
    if (req->flags & KILN_DAMAGE_NO_CREDIT) {
        // Tick thread, Pre: no job reads it now. The component is server-only (nothing to replicate).
        uint64_t size = 0;
        uint32_t was = 0;
        if (void* xs = component(world(), kExperienceSourceHash, req->target, &size); xs && size >= 4 && read_u32(xs, &was) && was) {
            const uint32_t none = 0;
            if (copy_bytes(xs, &none, 4)) LOGI("damage(): kill credit of %u cleared (was player %u)", req->target, was);
        }
    }
    Request q{};
    q.owner = owner;
    q.flags = req->flags;
    uint8_t* r = q.rec.b;
    // The engine's allocation defaults (pending-hit push): unit scales, identity rotations.
    at<float>(r, 0x2C) = 1.0f;
    at<float>(r, 0x30) = at<float>(r, 0x34) = at<float>(r, 0x38) = 1.0f;
    at<float>(r, 0x12C) = 1.0f;
    at<float>(r, 0x130) = at<float>(r, 0x134) = at<float>(r, 0x138) = 1.0f;
    std::memcpy(r + HitOffsets::target_transform, &tt, sizeof tt);
    const double tx = fixed_to_m(tt.pos[0]), ty = fixed_to_m(tt.pos[1]) + 1.0, tz = fixed_to_m(tt.pos[2]);
    float* pos = reinterpret_cast<float*>(r + HitOffsets::position);
    pos[0] = static_cast<float>(tx), pos[1] = static_cast<float>(ty), pos[2] = static_cast<float>(tz);
    float* nrm = reinterpret_cast<float*>(r + HitOffsets::normal);
    nrm[0] = 0, nrm[1] = 1, nrm[2] = 0;
    WorldTransform at_t{};
    if (req->attacker && transform(world(), req->attacker, &at_t)) {
        std::memcpy(r + HitOffsets::source_transform, &at_t, sizeof at_t);
        double dx = tx - fixed_to_m(at_t.pos[0]), dz = tz - fixed_to_m(at_t.pos[2]);
        const double len = std::sqrt(dx * dx + dz * dz);
        float* dir = reinterpret_cast<float*>(r + HitOffsets::direction);
        if (len > 1e-3) dir[0] = static_cast<float>(dx / len), dir[2] = static_cast<float>(dz / len);
    }
    at<uint32_t>(r, HitOffsets::root_source) = req->attacker;
    at<uint32_t>(r, HitOffsets::target) = req->target;
    at<uint32_t>(r, HitOffsets::root_target) = req->target;
    at<uint32_t>(r, HitOffsets::source) = req->source ? req->source : req->attacker;
    at<uint32_t>(r, HitOffsets::permission_source) = req->attacker;
    uint32_t flags = 0;
    if (req->flags & KILN_DAMAGE_FORCE_INTERRUPT) flags |= kFlagForceInterrupt;
    if (!(req->flags & KILN_DAMAGE_KILL) && req->amount < 0) flags |= kFlagHealing;
    at<uint32_t>(r, H.flags) = flags;
    at<int32_t>(r, H.health_change) = (req->flags & KILN_DAMAGE_KILL) ? -1 : -req->amount;
    at<float>(r, HitOffsets::aggro) = req->aggro;
    at<uint16_t>(r, HitOffsets::reaction) = req->reaction;
    at<uint8_t>(r, HitOffsets::push_back) = req->push_back;
    uint8_t faction = 0;
    if (const void* f = component(world(), kFactionHash, req->target)) read_u8(f, &faction);
    at<uint8_t>(r, HitOffsets::target_faction) = faction;
    at<uint8_t>(r, H.kind) = 0;
    at<uint8_t>(r, HitOffsets::send_hit_event) = (req->flags & KILN_DAMAGE_SILENT) ? 0 : 1;
    at<uint8_t>(r, HitOffsets::ignore_consecutive) = 0;
    if (req->flags & KILN_DAMAGE_KILL) {
        int32_t cur = 0, max = 0;
        if (read_health(world(), registry(), req->target, &cur, &max) && cur > 0)
            at<int32_t>(r, H.health_change) = -cur;
    }
    std::lock_guard lock(g_mu);
    q.id = g_next_id++;
    const uint32_t impact = kKilnImpactTag | (g_next_impact++ & 0xFFFFFF);
    at<uint32_t>(r, HitOffsets::impact_id) = impact;
    ReqState s{};
    s.st.size = sizeof s.st;
    s.st.state = KILN_HIT_QUEUED;
    s.st.health_before = s.st.health_after = -1;
    s.step_req = g_step.load();
    s.owner = owner;
    g_req_state[q.id] = s;
    g_req_by_impact[impact] = q.id;
    g_requests.push_back(q);
    if (out_id) *out_id = q.id;
    static bool logged = false;
    if (!logged) {
        logged = true;
        LOGI("first damage() request: target %u attacker %u amount %d flags 0x%x (impact 0x%08x)", req->target,
             req->attacker, req->amount, req->flags, impact);
    }
    return KILN_OK;
}

KilnResult t_hit_status(uint64_t id, KilnHitStatus* out) {
    if (!out || out->size < sizeof(KilnHitStatus)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    auto s = g_req_state.find(id);
    if (s == g_req_state.end()) return KILN_ERR_NOT_FOUND;
    const uint32_t size = out->size;
    *out = s->second.st;
    out->size = size;
    if (out->state == KILN_HIT_QUEUED || out->state == KILN_HIT_HELD)
        out->steps = static_cast<uint32_t>(g_step.load() - s->second.step_req);
    return KILN_OK;
}

KilnResult t_set_invulnerable(KilnOwner owner, KilnEntity e, bool on) {
    if (!e) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_health_ok) return KILN_ERR_UNAVAILABLE;
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    int32_t cur = -1, max = 0;
    if (on && !read_health(world(), registry(), e, &cur, &max)) return KILN_ERR_NOT_FOUND;
    std::lock_guard lock(g_mu);
    if (on) {
        auto& holders = g_god[e];
        holders.insert(owner);
        if (!g_god_floor.count(e)) g_god_floor[e] = cur;
    } else if (auto it = g_god.find(e); it != g_god.end()) {
        it->second.erase(owner);
        if (it->second.empty()) {
            g_god.erase(it);
            g_god_floor.erase(e);
        }
    }
    g_god_count.store(static_cast<uint32_t>(g_god.size()));
    return KILN_OK;
}

bool t_invulnerable(KilnEntity e) { return god_has(e); }

KilnResult t_release_owner(KilnOwner owner) {
    std::lock_guard lock(g_mu);
    for (auto it = g_god.begin(); it != g_god.end();) {
        it->second.erase(owner);
        if (it->second.empty()) {
            g_god_floor.erase(it->first);
            it = g_god.erase(it);
        } else {
            ++it;
        }
    }
    g_god_count.store(static_cast<uint32_t>(g_god.size()));
    std::erase_if(g_requests, [&](const Request& q) {
        if (q.owner != owner) return false;
        if (auto s = g_req_state.find(q.id); s != g_req_state.end()) s->second.st.state = KILN_HIT_DROPPED;
        return true;
    });
    return KILN_OK;
}

KilnResult t_set_listener(KilnCombatListenerFn fn, void* user) {
    g_listener_user.store(user);
    g_listener.store(fn);
    if (!fn) g_interest.store(0);
    LOGI("listener %s", fn ? "set" : "removed");
    return KILN_OK;
}

KilnResult t_status(KilnCombatStatus* out) {
    // Callers built before the fall/regen fields pass the shorter size and get the fields they know.
    if (!out || out->size < offsetof(KilnCombatStatus, falls)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t want = out->size;
    KilnCombatStatus full{};
    KilnCombatStatus* o = &full;
    o->size = want < sizeof full ? want : static_cast<uint32_t>(sizeof full);
    o->falls = g_falls_ok;
    o->regen = g_regen_ok;
    o->attr_heals = g_attr_ok;
    o->falls_seen = c_falls.load();
    o->falls_canceled = c_falls_canceled.load();
    o->falls_modified = c_falls_modified.load();
    o->regen_seen = c_regen.load();
    o->regen_canceled = c_regen_canceled.load();
    o->attr_heals_seen = c_attr.load();
    o->attr_heals_corrected = c_attr_corrected.load();
    o->knockbacks_seen = c_kb.load();
    o->knockbacks_modified = c_kb_modified.load();
    KilnCombatStatus* const user = out;
    out = o;
    out->health = g_health_ok && registry();
    out->hits = g_hits_ok && g_system.load();
    out->deaths = g_deaths_ok;
    out->holding = g_holding.load();
    out->interest = g_interest.load();
    out->hits_seen = c_seen.load();
    out->hits_held = c_held.load();
    out->hits_released = c_released.load();
    out->hits_canceled = c_canceled.load();
    out->hits_modified = c_modified.load();
    out->kiln_hits = c_kiln.load();
    out->god_blocked = c_god.load();
    out->god_restored = c_god_restored.load();
    out->deaths_seen = c_deaths.load();
    out->events_delivered = c_delivered.load();
    out->invulnerable = g_god_count.load();
    out->game_died_events = c_game_died.load();
    out->hits_deferred = c_deferred.load();
    out->drops_seen = c_drops.load();
    out->drops_canceled = c_drops_canceled.load();
    out->repeat_hits = c_repeats.load();
    std::memcpy(user, o, o->size);
    return KILN_OK;
}

// A handler added between two steps: the kinds it wants count from now, not from the listener's next return (which sets
// the whole mask as before, and is the only place the mask narrows). Without this a hit on a new watcher's entity was
// held by the widened filter and then let go unannounced, because its kind was not in the mask yet.
void widen_interest(uint32_t mask) {
    if (!mask || !g_listener.load()) return;
    const uint32_t old = g_interest.fetch_or(mask);
    const uint32_t next = old | mask;
    if (next == old) return;
    const bool hold = (next & (KILN_COMBAT_MASK(KILN_COMBAT_HURT) | KILN_COMBAT_MASK(KILN_COMBAT_HEAL) |
                               KILN_COMBAT_MASK(KILN_COMBAT_KNOCKBACK))) != 0 &&
                      g_hits_ok;
    if (hold && !g_holding.exchange(true)) LOGI("holding entity hits for the listener (interest 0x%x, a handler was added)", next);
    else LOGI("listener interest 0x%x (a handler was added)", next);
}

KilnResult KILN_CALL t_hold_filter(const KilnEntity* entities, uint32_t count, uint32_t interest) {
    if (count > HoldFilter::kMax || (count && !entities)) return KILN_ERR_INVALID_ARGUMENT;
    {
        std::lock_guard lock(g_mu);
        // said only when it changes: it is pushed at every added subscription, whatever the event
        if (g_hold_filter.set(entities, count))
            LOGI("hold filter: %s (%llu hit(s) let through unheld so far)",
                 g_hold_filter.all() ? "every hit" : (std::to_string(g_hold_filter.size()) + " watched entit(ies)").c_str(),
                 static_cast<unsigned long long>(c_unheld.load()));
    }
    widen_interest(interest);
    return KILN_OK;
}

const KilnCombatApi kTable = {
    {sizeof(KilnCombatApi), KILN_COMBAT_VERSION},
    &t_health, &t_set_health, &t_heal, &t_damage, &t_hit_status, &t_set_invulnerable, &t_invulnerable,
    &t_release_owner, &t_set_listener, &t_status,
    &t_attribute_ids, &t_attribute_get, &t_attribute_set, &t_attribute_add,
    &t_hold_filter,  // since 0.2.0-dev.27
};

// ============================================================================ boot ===============================

bool sym(const char* name, uintptr_t* out) {
    return g_api->symbol(KilnStr{name, std::strlen(name)}, out) == KILN_OK && *out;
}
bool cst(const char* name, int64_t* out) { return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }
bool feat(const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); }

void resolve() {
    uintptr_t a = 0;
    if (sym("combat/AttrFind", &a)) E.attr_find = reinterpret_cast<AttrFindFn>(a);
    if (sym("combat/AttrAdd", &a)) E.attr_add = reinterpret_cast<AttrAddFn>(a);
    if (sym("GetComponent", &a)) E.get_component = reinterpret_cast<GetComponentFn>(a);
    if (sym("GetEntityTransform", &a)) E.get_transform = reinterpret_cast<GetEntityTransformFn>(a);
    if (sym("combat/AllocPendingHit", &a)) E.alloc_hit = reinterpret_cast<AllocPendingHitFn>(a);
    if (sym("combat/AlreadyHit", &a)) E.already_hit = reinterpret_cast<AlreadyHitFn>(a);
    sym("combat/ApplyHit", &E.apply_hit);
    sym("combat/HitConclusion", &E.conclusion);
    cst("combat/actor.state", &E.actor_state);
    // The override layer of the game's state test; without it the raw state bits are read (as before).
    int64_t af = -1, on = -1, off = -1;
    uintptr_t state_test = 0;
    if (feat("combat.actor_states") && sym("combat/ActorStateTest", &state_test) && cst("combat/actor.state_flags", &af) &&
        cst("combat/actor.state_on", &on) && cst("combat/actor.state_off", &off) && af > 0 && on > 0 && off > 0)
        E.actor_flags = af, E.actor_on = on, E.actor_off = off;
    LOGI("actor state: +0x%llx%s", static_cast<unsigned long long>(E.actor_state),
         E.actor_flags >= 0 ? " through the game's state test (override flag, on/off layers resolved)" : " raw bits (state test unresolved)");
    const bool singletons = g_api->size >= offsetof(KilnApi, singleton) + sizeof(void*) && g_api->singleton &&
                            g_api->server_world && feat("singletons");
    g_health_ok = feat("combat.health") && E.attr_find && E.attr_add && E.get_component && singletons;
    int64_t size = 0, kind = 0, flags = 0, hc = 0;
    g_hits_ok = g_health_ok && feat("combat.hits") && E.alloc_hit && E.apply_hit && E.conclusion && E.get_transform &&
                cst("combat/hits.pool", &E.hits_pool) && cst("combat/hits.count", &E.hits_count) &&
                cst("combat/hit.size", &size) && cst("combat/hit.kind", &kind) && cst("combat/hit.flags", &flags) &&
                cst("combat/hit.health_change", &hc) && E.hits_count == E.hits_pool + 8 &&
                // the fixed (uncaptured) fields sit between these; a different record means a different layout
                size == 0x150 && kind == 0x144 && flags == 0x98 && hc == 0xBC;
    if (g_hits_ok) H.size = size, H.kind = kind, H.flags = flags, H.health_change = hc;
    g_deaths_ok = feat("combat.deaths") && cst("combat/evq.first", &E.evq_first) &&
                  cst("combat/evq.blocks", &E.evq_blocks) && cst("combat/evq.count", &E.evq_count) &&
                  cst("combat/evq.next", &E.evq_next) && cst("combat/evq.block_shift", &E.evq_shift) &&
                  E.evq_shift >= 8 && E.evq_shift <= 16 && E.actor_state > 0;
    // Falls, regeneration, attribute heals (mid points; docs/re/combat.md "Fall damage", "Regeneration").
    uintptr_t ce = 0, fall_eid = 0;
    if (sym("world/CurrentEntityId", &ce)) E.current_entity = reinterpret_cast<Engine::CurrentEntityFn>(ce);
    int64_t frame = 0, pct = -1, pct2 = -1;
    g_falls_ok = g_health_ok && feat("combat.falls") && E.current_entity && sym("combat/FallCheck", &E.fall_check) &&
                 sym("combat/FallStore", &E.fall_store) && sym("combat/FallEntityId", &fall_eid) && fall_eid == ce &&
                 cst("combat/fall.frame", &frame) && cst("combat/fall.pct", &pct) && cst("combat/fall.pct2", &pct2) &&
                 pct == pct2 && pct == 0xC && frame >= 0x80 && frame <= 0xFF;
    if (g_falls_ok) {
        E.fall_frame = frame - 0x100;  // a negative rbp displacement (disp8)
        E.fall_pct = pct;
    }
    g_regen_ok = g_health_ok && feat("combat.regen") && E.current_entity && sym("combat/RegenAdd", &E.regen_add);
    g_attr_ok = g_health_ok && feat("combat.attr_heals") && sym("combat/AttrChangeRecord", &E.attr_record);
    LOGI("resolved: falls %s, regeneration %s, attribute heals %s", g_falls_ok ? "on" : "OFF", g_regen_ok ? "on" : "OFF",
         g_attr_ok ? "on" : "OFF");
    LOGI("resolved: health %s, hits %s (record 0x%llx), deaths %s", g_health_ok ? "on" : "OFF",
         g_hits_ok ? "on" : "OFF", static_cast<unsigned long long>(size), g_deaths_ok ? "on" : "OFF");
}

bool install_mid(kiln::MidHook2& h, uintptr_t at, void (*fn)(safetyhook::Context&), const char* what, const char* label) {
    if (!h.install(g_api, g_owner, at, fn, label)) {
        LOGE("mid hook %s at %p failed (%s)", what, reinterpret_cast<void*>(at), h.error());
        return false;
    }
    return true;
}

void install_mid_hooks() {
    if (g_falls_ok) {
        g_falls_ok = install_mid(g_fall_check_hook, E.fall_check, &hk_fall_check, "fall check", "combat/FallCheck") &&
                     install_mid(g_fall_store_hook, E.fall_store, &hk_fall_store, "fall store", "combat/FallStore");
        if (!g_falls_ok) g_fall_check_hook = {}, g_fall_store_hook = {};
    }
    if (g_regen_ok) g_regen_ok = install_mid(g_regen_hook, E.regen_add, &hk_regen, "regeneration add", "combat/RegenAdd");
    if (g_attr_ok)
        g_attr_ok = install_mid(g_attr_hook, E.attr_record, &hk_attr, "attribute change record", "combat/AttrChangeRecord");
    LOGI("mid hooks: fall_damage_infliction %s, health_recharge %s, changeAttributeValue %s", g_falls_ok ? "on" : "off",
         g_regen_ok ? "on" : "off", g_attr_ok ? "on" : "off");
}

bool install_hooks() {
    if (!g_hits_ok) return false;
    if (!g_conclusion_hook.install(g_api, g_owner, E.conclusion, reinterpret_cast<void*>(&hk_conclusion), "combat/HitConclusion")) {
        LOGE("hooking impact_hit_conclusion failed");
        return false;
    }
    if (!g_apply_hook.install(g_api, g_owner, E.apply_hit, reinterpret_cast<void*>(&hk_apply_hit), "combat/ApplyHit")) {
        LOGE("hooking applyHitToEntity failed");
        g_conclusion_hook.revoke();  // both or neither, as before
        return false;
    }
    return true;
}

void KILN_CALL on_ready(void*) {
    char path[1024] = {};
    size_t len = 0;
    if (g_api->path(KILN_PATH_DATA, path, sizeof path - 32, &len) == KILN_OK) {
        std::string p(path, len);
        p += "\\kiln.combat\\watch-all";
        // p is UTF-8: a narrow Win32 call would read it in the ANSI code page
        std::wstring w(static_cast<size_t>(MultiByteToWideChar(CP_UTF8, 0, p.data(), static_cast<int>(p.size()), nullptr, 0)), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, p.data(), static_cast<int>(p.size()), w.data(), static_cast<int>(w.size()));
        g_watch_all = GetFileAttributesW(w.c_str()) != INVALID_FILE_ATTRIBUTES;
        if (g_watch_all) LOGI("lab switch watch-all: RESPAWNED for every revived victim");
    }
    g_spawn_api = static_cast<const KilnSpawnApi*>(
        g_api->service(KilnStr{KILN_SPAWN_SERVICE, std::strlen(KILN_SPAWN_SERVICE)}, 1));
    if (g_spawn_api && g_spawn_api->header.size < sizeof(KilnSpawnApi)) g_spawn_api = nullptr;
    g_types_api = static_cast<const KilnTypesApi*>(g_api->service(KilnStr{KILN_TYPES_SERVICE, std::strlen(KILN_TYPES_SERVICE)}, 1));
    if (g_types_api && g_types_api->header.size < offsetof(KilnTypesApi, type_enum) + sizeof(void*)) g_types_api = nullptr;
    g_assets_api = static_cast<const KilnAssetsApi*>(g_api->service(KilnStr{KILN_ASSETS_SERVICE, std::strlen(KILN_ASSETS_SERVICE)}, 1));
    if (g_assets_api && g_assets_api->header.size < offsetof(KilnAssetsApi, resource_list) + sizeof(void*)) g_assets_api = nullptr;
    LOGI("attributes: kiln.types %s, kiln.assets %s", g_types_api ? "found" : "MISSING", g_assets_api ? "found" : "MISSING");
    if (!g_deaths_ok) return;
    g_world_api = static_cast<const KilnWorldApi*>(
        g_api->service(KilnStr{KILN_WORLD_SERVICE, std::strlen(KILN_WORLD_SERVICE)}, 1));
    if (!g_world_api || g_world_api->header.size < sizeof(KilnWorldApi)) {
        LOGW("kiln.world is not available: no death events");
        g_deaths_ok = false;
        return;
    }
    static const KilnStr kEvents[] = {{"EntityDiedEvent", 15}};
    KilnProgramDesc d{};
    d.size = sizeof d;
    d.flags = KILN_PROGRAM_SYSTEM | KILN_PROGRAM_HOST_ONLY;
    d.name = KilnStr{"kiln_combat_deaths", 18};
    d.each = &died_each;
    d.events_in = KilnNames{kEvents, 1, 0};
    KilnHandle h = 0;
    if (KilnResult rc = g_world_api->register_program(g_owner, &d, &h); rc != KILN_OK) {
        LOGW("kiln.world refused program kiln_combat_deaths (%d): no death events", rc);
        g_deaths_ok = false;
        return;
    }
    g_died_program = h;
    g_died_slot = g_world_api->program_slot(h, KILN_SLOT_EVENT_IN, kEvents[0]);
    if (g_died_slot < 0) {
        LOGW("program kiln_combat_deaths: no events-in slot; no death events");
        g_deaths_ok = false;
        return;
    }
    LOGI("program kiln_combat_deaths registered through kiln.world (EntityDiedEvent in iterator slot %d)", g_died_slot);
}

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < offsetof(KilnApi, register_service) + sizeof(void*))
        return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{kName, std::strlen(kName)};
    info->version = KilnStr{"1", 1};
    info->on_ready = &on_ready;
    resolve();
    if (g_hits_ok && !install_hooks()) g_hits_ok = false;
    install_mid_hooks();
    if (g_hits_ok) LOGI("hooked impact_hit_conclusion %p and applyHitToEntity %p", reinterpret_cast<void*>(E.conclusion),
                        reinterpret_cast<void*>(E.apply_hit));
    KilnHandle h = 0;
    if (KilnResult rc = api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h); rc != KILN_OK) return rc;
    // A hosting client destroys its server world when the player leaves (menu, another server); a core without world
    // events still gets the reset at the next world's first Pre (a new world pointer).
    KilnHandle wh = 0;
    const KilnResult wrc = api->size >= offsetof(KilnApi, subscribe_world) + sizeof(void*) && api->subscribe_world
                               ? api->subscribe_world(owner, &on_world_event, nullptr, &wh)
                               : KILN_ERR_UNAVAILABLE;
    if (wrc != KILN_OK) LOGI("no world events (rc %d): per-world state resets when a new server world starts", wrc);
    // Published even when parts are off on this build: those functions return KILN_ERR_UNAVAILABLE, status() says so.
    return api->register_service(owner, KilnStr{KILN_COMBAT_SERVICE, std::strlen(KILN_COMBAT_SERVICE)}, &kTable);
}
