// SPDX-License-Identifier: MIT
// kiln.rules' character flight (feature rules.flight; docs/re/creative-mode-146.md Part 4; docs/kiln/services/rules.md):
// the character itself flies, in the game's BuildingFlying locomotion state (11), the building spark's. Three hooks, in
// the server world and in the player's prediction (the same rules and the same replicated input on both sides):
//
//  - the state: a mid hook right after locomotion_state's LocoNextState call (al = the game's next state, rsi the
//    ProgramContext, the Actor and DynamicLocomotion in the frame) applies flight_math::next_state: take off when
//    airborne, keep flying, land when grounded while sinking, fall when the rule or the conditions end.
//  - gravity: a mid hook in gravity_apply_velocity_actor after the step's time is read (xmm6 = the strength; a site of
//    its own, the glider's is earlier) zeroes it for an actor that may fly.
//  - the vertical input: an inline hook on the BuildingFlying mover sets DynamicLocomotion.inputVelocity.y from the
//    held Jump (climb) and Sneak (sink) bits of PlayerInput, faster with Sprint, before the mover steers toward it.
//
// Only entities with KILN_RULES_CHARACTER_FLIGHT, or flown by Kiln within the last seconds (their rules just ended), are
// touched: the building spark keeps its own state. Every body runs under a structured-exception guard; a fault counts,
// and the game's own decision stands.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>

#include "creative.h"
#include "flight_math.h"
#include "safetyhook.hpp"
#include "services/kiln_client.h"

namespace kiln_rules_flight {
namespace {

const KilnApi* g_api = nullptr;
const KilnClientApi* g_client = nullptr;

void logf(int level, const char* fmt, ...) {
    char b[512];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    if (n > 0) kiln_rules_bridge::log(level, b);
}

template <typename F>
bool sym(const char* name, F* out) {
    uintptr_t a = 0;
    if (g_api->symbol(KilnStr{name, std::strlen(name)}, &a) != KILN_OK || !a) return false;
    *out = reinterpret_cast<F>(a);
    return true;
}
bool cst(const char* name, int64_t* out) { return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }
bool feat(const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); }

using CurrentEntityIdFn = uint32_t*(__fastcall*)(void* ctx, uint32_t* out);
using ActorStateTestFn = bool(__fastcall*)(void* actor, uint8_t flag);
using MoverFn = void(__fastcall*)(uint8_t* it);

CurrentEntityIdFn g_entity_of = nullptr;
ActorStateTestFn g_actor_state = nullptr;
SafetyHookMid h_state, h_grav;
SafetyHookInline h_mover;

struct Layout {
    int64_t loco_actor = 0;    // locomotion_state: the Actor slot, rsp + (int8)
    int64_t loco_dynloco = 0;  // locomotion_state: the DynamicLocomotion slot, rbp + (int8)
    int64_t loco_state = 0;    // DynamicLocomotion.state
    int64_t grav_actor = 0;    // gravity_apply_velocity_actor: the Actor slot, rbp + (int8)
    int64_t mover_dynloco = 0;  // locomotion_execution's iterator: DynamicLocomotion
    int64_t mover_actor = 0;    // the iterator's Actor
    int64_t player_input = 0;   // the iterator's PlayerInput
    int64_t input_mask = 0;     // PlayerInput's digital input (the held bits follow at +8)
} L;

// keen::actor::StateFlag and PlayerInputType bits
constexpr uint8_t kGrounded = 0, kDead = 7, kClimbing = 34, kAttached = 36, kHangGliding = 37, kFloating = 43,
                  kUnderWater = 44, kNearZone = 54;
constexpr int kJump = 32, kSprint = 53, kSneak = 55;

struct Stats {
    std::atomic<uint64_t> takeoffs{0}, landings{0}, ends{0}, faults{0};
} S;

// Entities Kiln flew within the last kRecentMs: when their rules end mid-flight they still fall instead of keeping the
// sticky state (on a hosting game the server world and the prediction both see the same id).
constexpr int kRecent = 64;
constexpr uint64_t kRecentMs = 5000;
std::atomic<uint64_t> g_last_flying{0};
std::atomic<uint32_t> g_recent_id[kRecent];
std::atomic<uint64_t> g_recent_at[kRecent];

void note_flying(uint32_t e) {
    const uint64_t now = GetTickCount64();
    g_last_flying.store(now, std::memory_order_relaxed);
    int free_at = -1, oldest = 0;
    for (int i = 0; i < kRecent; ++i) {
        const uint32_t id = g_recent_id[i].load(std::memory_order_relaxed);
        if (id == e) {
            g_recent_at[i].store(now, std::memory_order_relaxed);
            return;
        }
        if (free_at < 0 && (id == 0 || now - g_recent_at[i].load(std::memory_order_relaxed) > kRecentMs)) free_at = i;
        if (g_recent_at[i].load(std::memory_order_relaxed) < g_recent_at[oldest].load(std::memory_order_relaxed)) oldest = i;
    }
    const int at = free_at >= 0 ? free_at : oldest;
    g_recent_at[at].store(now, std::memory_order_relaxed);
    g_recent_id[at].store(e, std::memory_order_relaxed);
}

bool flown_recently(uint32_t e) {
    const uint64_t now = GetTickCount64();
    for (int i = 0; i < kRecent; ++i)
        if (g_recent_id[i].load(std::memory_order_relaxed) == e && now - g_recent_at[i].load(std::memory_order_relaxed) <= kRecentMs)
            return true;
    return false;
}

// The local player (client), asked at most every half second: the hooks run for every actor in every step.
std::atomic<uint32_t> g_local_player{0};
std::atomic<uint64_t> g_local_at{0};
uint32_t local_player() {
    if (!g_client) return 0;
    const uint64_t now = GetTickCount64();
    if (now - g_local_at.load(std::memory_order_relaxed) < 500) return g_local_player.load(std::memory_order_relaxed);
    KilnClientState st{};
    st.size = sizeof st;
    const uint32_t p = g_client->state(&st) == KILN_OK ? st.local_player : 0;
    g_local_player.store(p, std::memory_order_relaxed);
    g_local_at.store(now, std::memory_order_relaxed);
    return p;
}

// Anything to do at all: some rule set has carried CHARACTER_FLIGHT, or Kiln flew someone moments ago.
bool flight_possible() {
    return (kiln_rules_bridge::flags_seen() & KILN_RULES_CHARACTER_FLIGHT) != 0 ||
           GetTickCount64() - g_last_flying.load(std::memory_order_relaxed) <= kRecentMs;
}

// The rules of the entity a program runs for: the server world's per-entity rules, else (a joined client's prediction)
// the local player's for the local player. The locomotion programs' contexts do not tell the worlds apart; they need
// not: per-entity rules exist only where a server world runs (a dedicated server, a hosting game, where the host's
// prediction sees the same values).
bool rules_for(void* ctx, uint32_t* entity, KilnRulesPlayer* out) {
    *entity = 0;
    if (!ctx) return false;
    g_entity_of(ctx, entity);
    const uint32_t e = *entity;
    if (e == 0) return false;
    if (kiln_rules_bridge::may_have_rules(e) && kiln_rules_bridge::rules_of(e, out)) return true;
    return e == local_player() && kiln_rules_bridge::local_rules(out);
}

flight_math::Actor actor_of(void* actor) {
    flight_math::Actor a;
    a.grounded = g_actor_state(actor, kGrounded);
    a.dead = g_actor_state(actor, kDead);
    a.gliding = g_actor_state(actor, kHangGliding);
    a.water = g_actor_state(actor, kFloating) || g_actor_state(actor, kUnderWater);
    a.climbing = g_actor_state(actor, kClimbing) || g_actor_state(actor, kAttached);
    a.near_zone = g_actor_state(actor, kNearZone);
    return a;
}

bool armed(const KilnRulesPlayer& r) { return (r.flags & KILN_RULES_CHARACTER_FLIGHT) != 0; }
bool base_only(const KilnRulesPlayer& r) { return (r.flags & KILN_RULES_FLIGHT_BASE_ONLY) != 0; }

// ---- the state, right after LocoNextState ----
void state_body(safetyhook::Context& c) {
    if (!flight_possible()) return;
    auto* dyn = *reinterpret_cast<uint8_t**>(c.rbp + static_cast<int8_t>(L.loco_dynloco));
    void* actor = *reinterpret_cast<void**>(c.rsp + static_cast<uint8_t>(L.loco_actor));
    if (!dyn || !actor) return;
    const uint8_t cur = dyn[L.loco_state];
    const uint8_t game = static_cast<uint8_t>(c.rax & 0xff);
    uint32_t e = 0;
    KilnRulesPlayer r{};
    const bool has = rules_for(reinterpret_cast<void*>(c.rsi), &e, &r);
    if (!has && !(cur == flight_math::kBuildingFlying && e && flown_recently(e))) {
        // nothing of ours: the common case leaves before the actor's flags are read
        return;
    }
    if (!armed(r) && cur != flight_math::kBuildingFlying) return;
    float input_vy = 0.0f;
    std::memcpy(&input_vy, dyn + 4, sizeof input_vy);  // inputVelocity.y
    const uint8_t next = flight_math::next_state(cur, game, true, has && armed(r), base_only(r), actor_of(actor), input_vy);
    if (next == flight_math::kBuildingFlying) note_flying(e);
    if (next == game) return;
    if (next == flight_math::kBuildingFlying) S.takeoffs.fetch_add(1, std::memory_order_relaxed);
    else if (next == flight_math::kWalking) S.landings.fetch_add(1, std::memory_order_relaxed);
    else S.ends.fetch_add(1, std::memory_order_relaxed);
    c.rax = (c.rax & ~uintptr_t{0xff}) | next;
}

// ---- gravity, after the strength is chosen and the step's time read ----
void grav_body(safetyhook::Context& c) {
    if (c.xmm6.f32[0] == 0.0f || !(kiln_rules_bridge::flags_seen() & KILN_RULES_CHARACTER_FLIGHT)) return;
    uint32_t e = 0;
    KilnRulesPlayer r{};
    if (!rules_for(reinterpret_cast<void*>(c.rdi), &e, &r) || !armed(r)) return;
    void* actor = *reinterpret_cast<void**>(c.rbp + static_cast<int8_t>(L.grav_actor));
    if (!actor || !flight_math::allowed(true, base_only(r), actor_of(actor))) return;
    c.xmm6.f32[0] = 0.0f;
}

// ---- the vertical input, before the BuildingFlying mover ----
void mover_pre(uint8_t* it) {
    if (!(kiln_rules_bridge::flags_seen() & KILN_RULES_CHARACTER_FLIGHT)) return;
    void* ctx = *reinterpret_cast<void**>(it);
    auto* dyn = *reinterpret_cast<float* const*>(it + L.mover_dynloco);
    const auto* input = *reinterpret_cast<const uint8_t* const*>(it + L.player_input);
    if (!ctx || !dyn || !input) return;  // players only (the spark has no PlayerInput)
    uint32_t e = 0;
    KilnRulesPlayer r{};
    if (!rules_for(ctx, &e, &r) || !armed(r)) return;
    const uint64_t held = *reinterpret_cast<const uint64_t*>(input + L.input_mask + 8);
    dyn[1] = flight_math::climb((held >> kJump) & 1, (held >> kSneak) & 1, (held >> kSprint) & 1);
}

template <void (*Body)(safetyhook::Context&)>
void mid_guarded(safetyhook::Context& c) {
    __try {
        Body(c);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        S.faults.fetch_add(1, std::memory_order_relaxed);
    }
}
void hk_state(safetyhook::Context& c) { mid_guarded<state_body>(c); }
void hk_grav(safetyhook::Context& c) { mid_guarded<grav_body>(c); }

void mover_guarded(uint8_t* it) {
    __try {
        mover_pre(it);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        S.faults.fetch_add(1, std::memory_order_relaxed);
    }
}

void __fastcall hk_mover(uint8_t* it) {
    if (it) mover_guarded(it);
    h_mover.call<void>(it);
}

}  // namespace

uint32_t install(const KilnApi* api, std::string& off) {
    g_api = api;
    g_client = static_cast<const KilnClientApi*>(
        api->service(KilnStr{KILN_CLIENT_SERVICE, sizeof(KILN_CLIENT_SERVICE) - 1}, KILN_CLIENT_VERSION));
    uintptr_t state_site = 0, grav_site = 0, mover = 0;
    if (!(feat("rules.flight") && sym("rules/CurrentEntityId", &g_entity_of) && sym("rules/ActorStateTest", &g_actor_state) &&
          sym("rules/LocoStateWrite", &state_site) && sym("rules/FlightGravity", &grav_site) && sym("rules/FlightMover", &mover) &&
          cst("rules/loco.actor", &L.loco_actor) && cst("rules/loco.dynloco", &L.loco_dynloco) && cst("rules/loco.state", &L.loco_state) &&
          cst("rules/flight.grav_actor", &L.grav_actor) && cst("rules/flight.dynloco", &L.mover_dynloco) &&
          cst("rules/flight.actor", &L.mover_actor) && cst("rules/loco.player_input", &L.player_input) &&
          cst("rules/input.mask", &L.input_mask))) {
        off += off.empty() ? "character flight (rules.flight)" : ", character flight (rules.flight)";
        return 0;
    }
    if (kiln_rules_creative::flame_creative()) {
        off += off.empty() ? "character flight (Creative Mode runs)" : ", character flight (Creative Mode runs)";
        return 0;
    }
    // all three or none: a state without its gravity or input would leave a player stuck in the air
    h_state = safetyhook::create_mid(reinterpret_cast<void*>(state_site), &hk_state);
    h_grav = safetyhook::create_mid(reinterpret_cast<void*>(grav_site), &hk_grav);
    h_mover = safetyhook::create_inline(reinterpret_cast<void*>(mover), reinterpret_cast<void*>(&hk_mover));
    if (!h_state || !h_grav || !h_mover) {
        logf(KILN_LOG_ERROR, "character flight: hooking failed (state %d, gravity %d, mover %d); it stays off", bool(h_state),
             bool(h_grav), bool(h_mover));
        h_state = {};
        h_grav = {};
        h_mover = {};
        off += off.empty() ? "character flight (hook failed)" : ", character flight (hook failed)";
        return 0;
    }
    const auto base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    logf(KILN_LOG_INFO, "character flight: state rva 0x%llx, gravity 0x%llx, mover 0x%llx (BuildingFlying for players with the rule)",
         (unsigned long long)(state_site - base), (unsigned long long)(grav_site - base), (unsigned long long)(mover - base));
    return KILN_RULES_HOOK_FLIGHT;
}

void counters(uint64_t* takeoffs, uint64_t* landings, uint64_t* ends, uint64_t* faults) {
    *takeoffs = S.takeoffs.load();
    *landings = S.landings.load();
    *ends = S.ends.load();
    *faults = S.faults.load();
}

}  // namespace kiln_rules_flight
