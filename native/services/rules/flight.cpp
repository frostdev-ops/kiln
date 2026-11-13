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
// KILN_RULES_NOCLIP flies with the same three hooks (flight_math.h says where it differs); noclip.cpp adds the
// collision step and asks noclip_flight() here whose it may skip.
//
// Only entities with KILN_RULES_CHARACTER_FLIGHT or KILN_RULES_NOCLIP, or still in a flight owned by Kiln (their rules
// just ended), are touched: the building spark keeps its own state. Every body runs under a structured-exception guard; a fault counts,
// and the game's own decision stands.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>

#include "creative.h"
#include "flight_math.h"
#include "kiln_hook.hpp"
#include "safetyhook.hpp"  // safetyhook::Context: the mid handlers' register view
#include "services/kiln_client.h"

namespace kiln_rules_flight {
namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
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
kiln::MidHook2 h_state, h_grav;
kiln::RawHook h_mover;

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
                  kUnderWater = 44;
constexpr int kJump = 32, kSprint = 53, kSneak = 55;
constexpr uint32_t kFlies = KILN_RULES_CHARACTER_FLIGHT | KILN_RULES_NOCLIP;  // the rule flags that fly the character

struct Stats {
    std::atomic<uint64_t> takeoffs{0}, landings{0}, ends{0}, faults{0};
} S;

// Ownership is keyed by the locomotion object as well as the entity. It does not expire with wall time: a paused
// prediction can resume after its rule was revoked. A player id has one ownership slot per world with characters: the
// server world's record and the other world's (a client's prediction; a joined client has only that one). The bounded
// extra history serves simulated characters, which live in the server world only (at most 16 extra rule ids).
//
// The worlds are told apart on the server tick, between two server steps, where kiln.world may read: it gives the
// server world's record of every player Kiln flies or may fly (server_tick), and a record that is that one is the
// server world's; any other record of the player belongs to the other world. The locomotion programs' contexts cannot
// tell the worlds apart themselves (docs/re/rules.md 7: their job copies have no storage pointer).
//
// The object moves. The game keeps an entity's components by archetype, so adding a component or taking one away
// copies all of them to another place: every teleport adds the Teleport component and takes it away the step after
// (docs/re/creatures.md 1.4, docs/re/rules.md 8), and other components come and go too. The record keeps its bytes,
// BuildingFlying included, but no longer its address. So a BuildingFlying record of a player at an address that is not
// one of its own is its flight moved, when Kiln has a flight of that player in the record's own world that it has not
// seen end: it takes that world's slot. A flight in one world never vouches for a record in the other. Without this, a
// teleport left the character in the sticky BuildingFlying state with no hook to end it: no walking and no animation,
// with collision back. Nothing is adopted while Kiln's flight steps aside for Creative Mode.
//
// Ownership lives as long as the character: it is forgotten when the server world no longer has the player's
// character (server_tick), when the player leaves or its connection is replaced (retire, from Kiln.Rules), and when a
// world with characters stops. Rules ending alone keep it, so the hooks can still land or end the flight.
constexpr int kRecent = 64;
constexpr uint32_t kMaxPlayerId = 1023;
std::atomic<bool> g_flew{false};
std::atomic<uintptr_t> g_player_dyn[kMaxPlayerId + 1][2]{};  // [kServer, kOther]
std::atomic<uint32_t> g_recent_id[kRecent]{};
std::atomic<uintptr_t> g_recent_dyn[kRecent]{};
std::atomic<uint64_t> g_recent_seen[kRecent]{};
std::atomic<unsigned> g_next_recent{0};
std::atomic<uint64_t> g_seen{0};
std::atomic<uint32_t> g_local_player{0};  // refreshed once per client tick, never a service call per actor
// the server tick's view: its step count (0: no server world steps here), and per player the server world's record and
// the step it was read at
constexpr int kServer = 0, kOther = 1;
std::atomic<uint64_t> g_server_step{0};
std::atomic<uintptr_t> g_server_dyn[kMaxPlayerId + 1]{};
std::atomic<uint64_t> g_server_read[kMaxPlayerId + 1]{};
std::atomic<uint64_t> g_want[(kMaxPlayerId + 1) / 64]{};  // players the next server tick reads (their rules may fly them)
std::atomic<bool> g_reads_work{false};                       // the server tick's reading has found a character
// the moves followed, and the last one, for the log (refresh() reports them once a tick)
std::atomic<uint64_t> g_moves{0};
std::atomic<uint32_t> g_move_entity{0};
std::atomic<uintptr_t> g_move_from{0}, g_move_to{0};

uint64_t stamp() { return g_seen.fetch_add(1, std::memory_order_relaxed) + 1; }

void want(uint32_t e) {
    if (e && e <= kMaxPlayerId) g_want[e / 64].fetch_or(uint64_t{1} << (e % 64), std::memory_order_relaxed);
}

// The world of player `e`'s record `dyn`: kServer, kOther, or -1 when the server tick has not read the player lately
// (the next one does). Without a server world in this process every record is the other world's.
int world_of(uint32_t e, const void* dyn) {
    const uint64_t now = g_server_step.load(std::memory_order_acquire);
    if (now == 0) return kOther;
    const uint64_t at = g_server_read[e].load(std::memory_order_acquire);
    const bool server = g_server_dyn[e].load(std::memory_order_relaxed) == reinterpret_cast<uintptr_t>(dyn);
    if (at == now) return server ? kServer : kOther;
    // read one step ago: still enough to say a record is not the server's (it moves only at the end of a server
    // step, to an address the server world holds); "it is the server's" needs this step's reading
    if (at != 0 && at + 1 == now && !server) return kOther;
    want(e);
    return -1;
}

void note_flying(uint32_t e, const void* dyn) {
    g_flew.store(true, std::memory_order_relaxed);
    const auto ptr = reinterpret_cast<uintptr_t>(dyn);
    if (e && e <= kMaxPlayerId) {
        const int w = world_of(e, dyn);
        if (w >= 0) g_player_dyn[e][w].store(ptr);
        return;
    }
    for (int i = 0; i < kRecent; ++i)
        if (g_recent_id[i].load() == e && g_recent_dyn[i].load() == ptr) {
            g_recent_seen[i].store(stamp(), std::memory_order_relaxed);
            return;
        }
    const unsigned i = g_next_recent.fetch_add(1) % kRecent;
    g_recent_dyn[i].store(ptr);
    g_recent_id[i].store(e);
    g_recent_seen[i].store(stamp(), std::memory_order_relaxed);
}

// `dyn` is the record of a flight of `e` that Kiln has not seen end, moved there (see above): it takes the slot of its
// own world (a simulated character's: the entry seen least recently). False when Kiln has no such flight of `e` there.
bool follow_move(uint32_t e, const void* dyn) {
    if (!e || kiln_rules_creative::idle()) return false;
    const auto ptr = reinterpret_cast<uintptr_t>(dyn);
    uintptr_t from = 0;
    if (e <= kMaxPlayerId) {
        const int w = world_of(e, dyn);
        if (w < 0) return false;
        auto& slot = g_player_dyn[e][w];
        from = slot.load();
        if (from == 0 || !slot.compare_exchange_strong(from, ptr)) return false;
    } else {
        int pick = -1;
        for (int i = 0; i < kRecent; ++i)
            if (g_recent_id[i].load() == e && g_recent_dyn[i].load() != 0 &&
                (pick < 0 || g_recent_seen[i].load() < g_recent_seen[pick].load()))
                pick = i;
        if (pick < 0) return false;
        from = g_recent_dyn[pick].exchange(ptr);
        g_recent_seen[pick].store(stamp(), std::memory_order_relaxed);
    }
    g_move_entity.store(e, std::memory_order_relaxed);
    g_move_from.store(from, std::memory_order_relaxed);
    g_move_to.store(ptr, std::memory_order_relaxed);
    g_moves.fetch_add(1, std::memory_order_relaxed);
    return true;
}

bool flown_recently(uint32_t e, const void* dyn) {
    if (e && e <= kMaxPlayerId) return g_player_dyn[e][0].load() == reinterpret_cast<uintptr_t>(dyn) ||
                                       g_player_dyn[e][1].load() == reinterpret_cast<uintptr_t>(dyn);
    for (int i = 0; i < kRecent; ++i)
        if (g_recent_id[i].load() == e && g_recent_dyn[i].load() == reinterpret_cast<uintptr_t>(dyn)) return true;
    return false;
}

void forget_flying(uint32_t e, const void* dyn) {
    auto ptr = reinterpret_cast<uintptr_t>(dyn);
    if (e && e <= kMaxPlayerId) {
        g_player_dyn[e][0].compare_exchange_strong(ptr, 0);
        ptr = reinterpret_cast<uintptr_t>(dyn);
        g_player_dyn[e][1].compare_exchange_strong(ptr, 0);
    } else {
        for (int i = 0; i < kRecent; ++i)
            if (g_recent_id[i].load() == e) { ptr = reinterpret_cast<uintptr_t>(dyn); g_recent_dyn[i].compare_exchange_strong(ptr, 0); }
    }
}

// Whether a take-off of `e` at `dyn` can be told to a world now (a player's record whose world is not known yet waits
// one server step).
bool placeable(uint32_t e, const void* dyn) { return !(e && e <= kMaxPlayerId) || world_of(e, dyn) >= 0; }

// Kiln's flight of `e` is in `dyn`: a record Kiln took off with, or (`flying`: `dyn` is in BuildingFlying) the record of
// a flight of `e` that has not ended, moved there.
bool owned(uint32_t e, const void* dyn, bool flying) { return flown_recently(e, dyn) || (flying && follow_move(e, dyn)); }

// The log hears of moves once a tick, the first few of a world only (a flight through many teleports is quiet).
constexpr uint64_t kMovesTold = 8;
std::atomic<uint64_t> g_moves_told{0};
void report_moves() {
    const uint64_t n = g_moves.load(std::memory_order_relaxed);
    uint64_t told = g_moves_told.load(std::memory_order_relaxed);
    if (n == told || told >= kMovesTold || !g_moves_told.compare_exchange_strong(told, n)) return;
    logf(KILN_LOG_INFO, "character flight: entity %u's locomotion record moved (0x%llx -> 0x%llx: the game moved its components, as a "
         "teleport does); still in Kiln's flight (%llu move(s) followed%s)", g_move_entity.load(), (unsigned long long)g_move_from.load(),
         (unsigned long long)g_move_to.load(), (unsigned long long)n, n >= kMovesTold ? "; further ones without a line" : "");
}

bool flight_possible() {
    return (!kiln_rules_creative::idle() && (kiln_rules_bridge::flags_seen() & kFlies) != 0) ||
           g_flew.load(std::memory_order_relaxed);
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
    return e == g_local_player.load(std::memory_order_relaxed) && kiln_rules_bridge::local_rules(out);
}

flight_math::Actor actor_of(void* actor) {
    flight_math::Actor a;
    a.grounded = g_actor_state(actor, kGrounded);
    a.dead = g_actor_state(actor, kDead);
    a.gliding = g_actor_state(actor, kHangGliding);
    a.water = g_actor_state(actor, kFloating) || g_actor_state(actor, kUnderWater);
    a.climbing = g_actor_state(actor, kClimbing) || g_actor_state(actor, kAttached);
    // Building-area eligibility is authored in the server snapshot, never inferred from the forced actor zone bit.
    return a;
}

// armed: the rules let the character fly, by CHARACTER_FLIGHT or by NOCLIP (which flies everywhere: flight_math.h).
bool armed(const KilnRulesPlayer& r) { return (r.flags & kFlies) != 0; }
bool flight(const KilnRulesPlayer& r) { return (r.flags & KILN_RULES_CHARACTER_FLIGHT) != 0; }
bool noclip(const KilnRulesPlayer& r) { return (r.flags & KILN_RULES_NOCLIP) != 0; }
bool base_only(const KilnRulesPlayer& r) { return (r.flags & KILN_RULES_FLIGHT_BASE_ONLY) != 0; }

// ---- the state, right after LocoNextState ----
void state_body(safetyhook::Context& c) {
    if (!flight_possible()) return;
    auto* dyn = *reinterpret_cast<uint8_t**>(c.rbp + static_cast<int8_t>(L.loco_dynloco));
    void* actor = *reinterpret_cast<void**>(c.rsp + static_cast<uint8_t>(L.loco_actor));
    if (!dyn || !actor) return;
    const uint8_t cur = dyn[L.loco_state];
    if (cur != flight_math::kBuildingFlying && (kiln_rules_creative::idle() ||
        !(kiln_rules_bridge::flags_seen() & kFlies))) return;
    const uint8_t game = static_cast<uint8_t>(c.rax & 0xff);
    uint32_t e = 0;
    KilnRulesPlayer r{};
    const bool has = rules_for(reinterpret_cast<void*>(c.rsi), &e, &r);
    const bool was_ours = owned(e, dyn, cur == flight_math::kBuildingFlying);
    const bool enabled = has && armed(r) && !kiln_rules_creative::idle();
    if (enabled) want(e);  // the server tick keeps telling this player's records apart
    if (!enabled && !(cur == flight_math::kBuildingFlying && was_ours)) return;
    float input_vy = 0.0f;
    std::memcpy(&input_vy, dyn + 4, sizeof input_vy);  // inputVelocity.y
    // A BuildingFlying state Kiln did not start (another mod's flight, a game feature) is not Kiln's to keep, to end
    // or to count as its own: only a take-off from here makes a flight Kiln's, wherever the game moves its record
    // afterwards (owned). NoClip's collision step asks exactly that (noclip_flight below).
    const bool ours = cur != flight_math::kBuildingFlying || was_ours;
    const uint8_t next = flight_math::next_state(cur, game, ours, enabled && flight(r), base_only(r), actor_of(actor), input_vy,
                                                 enabled && noclip(r));
    // a take-off waits until its world is known (one server step at most): ownership is per world
    if (next == flight_math::kBuildingFlying && cur != flight_math::kBuildingFlying && !placeable(e, dyn)) return;
    if (next == flight_math::kBuildingFlying && ours) note_flying(e, dyn);
    else if (ours) forget_flying(e, dyn);
    if (next == game) return;
    if (next == flight_math::kBuildingFlying) S.takeoffs.fetch_add(1, std::memory_order_relaxed);
    else if (next == flight_math::kWalking) S.landings.fetch_add(1, std::memory_order_relaxed);
    else S.ends.fetch_add(1, std::memory_order_relaxed);
    c.rax = (c.rax & ~uintptr_t{0xff}) | next;
}

// ---- gravity, after the strength is chosen and the step's time read ----
void grav_body(safetyhook::Context& c) {
    if (kiln_rules_creative::idle() || c.xmm6.f32[0] == 0.0f || !(kiln_rules_bridge::flags_seen() & kFlies)) return;
    uint32_t e = 0;
    KilnRulesPlayer r{};
    if (!rules_for(reinterpret_cast<void*>(c.rdi), &e, &r) || !armed(r)) return;
    void* actor = *reinterpret_cast<void**>(c.rbp + static_cast<int8_t>(L.grav_actor));
    if (!actor) return;
    const auto a = actor_of(actor);
    if (!flight_math::allowed_noclip(noclip(r), a) && !flight_math::allowed(flight(r), base_only(r), a)) return;
    c.xmm6.f32[0] = 0.0f;
}

// ---- the vertical input, before the BuildingFlying mover ----
void mover_pre(uint8_t* it) {
    if (kiln_rules_creative::idle() || !(kiln_rules_bridge::flags_seen() & kFlies)) return;
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

// noclip.cpp: is `dyn` (an entity's DynamicLocomotion) in a flight Kiln started, and do `ctx`'s rules carry NoClip now.
bool noclip_flight(void* ctx, const uint8_t* dyn, uint32_t* entity) {
    if (kiln_rules_creative::idle() || !(kiln_rules_bridge::flags_seen() & KILN_RULES_NOCLIP)) return false;
    if (!g_entity_of || dyn[L.loco_state] != flight_math::kBuildingFlying) return false;
    KilnRulesPlayer r{};
    return rules_for(ctx, entity, &r) && noclip(r) && owned(*entity, dyn, true);
}

void forget_local() { g_local_player.store(0); }

// The player (or simulated character) `e` left, or its connection was replaced: its records' ownership goes, so whoever
// gets the id next starts with no flight of Kiln's (Kiln.Rules' Leave; the server tick when the character is gone).
void retire(uint32_t e) {
    if (!e) return;
    if (e <= kMaxPlayerId) {
        g_player_dyn[e][kServer].store(0);
        g_player_dyn[e][kOther].store(0);
        g_server_dyn[e].store(0);
        g_server_read[e].store(0);
        return;
    }
    for (int i = 0; i < kRecent; ++i)
        if (g_recent_id[i].load() == e) g_recent_dyn[i].store(0);
}

// Once a server step, before it (the server tick, where kiln.world may read): `record(e)` is the server world's
// DynamicLocomotion of `e` now (0: no such character). Read for every player Kiln flies or may fly; a flown character
// the server world no longer has is retired.
void server_tick(uintptr_t (*record)(uint32_t entity)) {
    if (!record) return;  // without kiln.world every record counts as one world's, as on a dedicated server
    const uint64_t now = g_server_step.fetch_add(1, std::memory_order_acq_rel) + 1;
    if (!g_flew.load(std::memory_order_relaxed) && !(kiln_rules_bridge::flags_seen() & kFlies)) return;
    for (uint32_t block = 0; block < (kMaxPlayerId + 1) / 64; ++block) {
        uint64_t bits = g_want[block].exchange(0, std::memory_order_relaxed);
        for (uint32_t e = block * 64; e < block * 64 + 64; ++e) {
            const bool flown = g_player_dyn[e][kServer].load() != 0 || g_player_dyn[e][kOther].load() != 0;
            if (!e || (!flown && !((bits >> (e % 64)) & 1))) continue;
            const uintptr_t was = g_server_dyn[e].load(std::memory_order_relaxed), mine = record(e);
            if (mine) g_reads_work.store(true, std::memory_order_relaxed);
            g_server_dyn[e].store(mine, std::memory_order_relaxed);
            g_server_read[e].store(now, std::memory_order_release);
            // the server world had this character and has it no more: gone (a reading that never finds anything
            // retires nothing; every record then counts as the other world's, as without kiln.world)
            if (!mine && was && g_player_dyn[e][kServer].load() != 0) retire(e);
        }
    }
    // a simulated character the server world no longer has (once the reading has been seen to find characters)
    for (int i = 0; i < kRecent; ++i) {
        const uint32_t e = g_recent_id[i].load();
        if (e && g_recent_dyn[i].load() != 0) {
            if (record(e)) g_reads_work.store(true, std::memory_order_relaxed);
            else if (g_reads_work.load(std::memory_order_relaxed)) g_recent_dyn[i].store(0);
        }
    }
}

// A server world started here and no prediction world has since: a host's session is beginning.
static std::atomic<bool> g_server_began{false};

// Flight ownership is keyed by a character's objects, and the locomotion hooks cannot tell which world a character is
// in. So the table is one for all worlds, and the world events decide when it is forgotten:
// - A world with characters in it (server, prediction) stops: everything goes; its objects die with it, and on a host
//   the worlds of a session stop together. The client's own world (camera, presentation) has no locomotion: when
//   only that one stops, nothing is forgotten.
// - A world starts: it has no flights yet, and another world may be running with characters in Kiln's flight (a host's
//   server world when its prediction starts). Forgetting ownership then orphaned those flights: still flying, and no
//   longer Kiln's to land or to end. So a start forgets nothing, with one exception: where the core does not see
//   world stops (`stops_seen` false: KilnSessionInfo.lifecycle) and this is not the first world of its kind
//   (`session` > 1), it may replace one whose stop nobody saw, and ownership from that one must not meet a new
//   character at an old address.
//   A host's worlds start in one order: the server's, then the prediction. That prediction start belongs to the
//   session the server start began (and already forgot everything for): it forgets nothing, or a character that got
//   NoClip in the new server world in between were left flying inside something with collision back on.
// The cached local identity is dropped at every event.
void world(uint32_t event, uint32_t kind, uint32_t session, bool stops_seen) {
    const bool has_characters = kind != KILN_WORLD_CLIENT;
    bool forget = false;
    if (event == KILN_WORLD_STOPPING && has_characters) {
        forget = true;
        if (kind == KILN_WORLD_SERVER) g_server_began.store(false);
    } else if (event == KILN_WORLD_STARTED && has_characters) {
        const bool same_session = kind == KILN_WORLD_PREDICTION && g_server_began.exchange(false);
        if (kind == KILN_WORLD_SERVER) g_server_began.store(true);
        forget = !stops_seen && session > 1 && !same_session;
    }
    if (forget) reset();
    else forget_local();
}

void reset() {
    g_local_player.store(0);
    g_flew.store(false);
    for (auto& slots : g_player_dyn) for (auto& ptr : slots) ptr.store(0);
    for (int i = 0; i < kRecent; ++i) { g_recent_id[i].store(0); g_recent_dyn[i].store(0); g_recent_seen[i].store(0); }
    g_server_step.store(0);
    for (auto& d : g_server_dyn) d.store(0);
    for (auto& at : g_server_read) at.store(0);
    for (auto& w : g_want) w.store(0);
    g_reads_work.store(false);
    g_moves.store(0);
    g_moves_told.store(0);
}

void refresh() {
    if (!g_api) return;
    report_moves();
    (void)kiln_rules_creative::flame_creative();
    KilnClientState st{};
    st.size = sizeof st;
    // Hosts use the authoritative per-entity snapshots in both worlds. A stale local snapshot must never regrant
    // flight to the server after that player's rule has been cleared.
    const uint32_t p = g_client && g_client->state && g_client->state(&st) == KILN_OK && st.session == KILN_SESSION_REMOTE
        ? st.local_player : 0;
    g_local_player.store(p, std::memory_order_relaxed);
}

void KILN_CALL client_pre(const KilnTickInfo*, void*) {
    refresh();
    // A joined game has no server tick: NoClip's "active" ages with the client's (a host's ages with its server's).
    if (g_local_player.load(std::memory_order_relaxed) != 0) kiln_rules_noclip::tick();
}

uint32_t install(const KilnApi* api, KilnOwner owner, std::string& off) {
    g_api = api;
    g_owner = owner;
    g_client = static_cast<const KilnClientApi*>(
        api->service(KilnStr{KILN_CLIENT_SERVICE, sizeof(KILN_CLIENT_SERVICE) - 1}, KILN_CLIENT_VERSION));
    if (g_client && g_client->subscribe_tick) {
        KilnHandle handle = 0;
        g_client->subscribe_tick(0, KILN_TICK_PRE, &client_pre, nullptr, &handle);
    }
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
    h_state.install(api, owner, state_site, &hk_state, "rules/LocoStateWrite");
    h_grav.install(api, owner, grav_site, &hk_grav, "rules/FlightGravity");
    h_mover.install(api, owner, mover, reinterpret_cast<void*>(&hk_mover), "rules/FlightMover");
    if (!h_state || !h_grav || !h_mover) {
        logf(KILN_LOG_ERROR, "character flight: hooking failed (state %d, gravity %d, mover %d); it stays off", bool(h_state),
             bool(h_grav), bool(h_mover));
        h_state = {};
        h_grav = {};
        h_mover = {};
        off += off.empty() ? "character flight (hook failed)" : ", character flight (hook failed)";
        return 0;
    }
    refresh();
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
