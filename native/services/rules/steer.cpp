// SPDX-License-Identifier: MIT
// kiln.rules' glider steering (feature rules.glide_steer; docs/re/rules.md 7; docs/kiln/services/rules.md "Gliding"):
// the glider's own pitch, steered per entity without touching the shared ItemInfo.
//
// The game integrates the glider's pitch in actor_rotation (server 0x80720, client 0x26e620: the same code, no
// host-only flag, so a client's prediction runs it too). One mid hook at the roll-rate load right after the pitch step
// (rules/GlideSteer) replaces xmm6, this step's bounded pitch change, with glide_steer_math.h's steer_step for an
// entity whose rules have KILN_RULES_GLIDE_STEER. xmm14 is the measured pitch, xmm2 the game's max step; the frame
// holds the ProgramContext (rbp + rot.ctx), ActorInput (rbp + (int8)rot.actor_input) and PlayerInput (rbp +
// (int8)rot.player_input, null for anything but a player).
//
// The rules are keyed on the entity exactly as the glide step's (rules.cpp glide_rules): the per-entity rules where a
// server world runs (a dedicated server, a hosting game: there the host's prediction sees the same values), else the
// local player's rules for the local player (a joined client's prediction). The world kind is not available in these
// programs (docs/re/rules.md 7, "Each-programs have another context"). The body runs under a structured-exception
// guard: a fault counts, and the game's step stands; only a finite step is written.
#include <windows.h>

#include <atomic>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <string>

#include "creative.h"
#include "glide_steer_math.h"
#include "kiln_hook.hpp"
#include "safetyhook.hpp"  // safetyhook::Context: the mid handlers' register view
#include "services/kiln_client.h"

namespace kiln_rules_steer {
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
CurrentEntityIdFn g_entity_of = nullptr;
kiln::MidHook2 h_steer;
std::atomic<bool> g_active{false};

struct Layout {
    int64_t ctx = 0;           // rbp + this: the ProgramContext's home slot
    int64_t actor_input = 0;   // rbp + (int8)this: ActorInput*
    int64_t player_input = 0;  // rbp + (int8)this: PlayerInput* (null: not a player)
    int64_t move_z = 0;        // ActorInput.desiredLocalMoveInput.z (the stick's forward axis: W +1, S -1)
    int64_t stick_mode = 0;    // PlayerInput.fromClient.movementStickInputMode (u8; 0 = YawSpeedPitchSpeed)
} L;

struct Stats {
    std::atomic<uint64_t> steered{0}, faults{0};
} S;

uint32_t local_player() {
    if (!g_client || !g_client->state) return 0;
    KilnClientState st{};
    st.size = sizeof st;
    return g_client->state(&st) == KILN_OK ? st.local_player : 0;
}

// The same lookup as rules.cpp's glide_rules (the glide step, its gravity and stamina).
bool rules_for(void* ctx, KilnRulesPlayer* out) {
    uint32_t entity = 0;
    g_entity_of(ctx, &entity);
    if (entity == 0) return false;
    if (kiln_rules_bridge::rules_of(entity, out)) return true;
    if (!kiln_rules_bridge::local_rules(out)) return false;
    return entity == local_player();
}

std::atomic<bool> g_told{false};

// At the roll-rate load: xmm6 = the game's pitch step, xmm14 = the pitch, xmm2 = the game's max step.
void steer_body(safetyhook::Context& c) {
    if (!(kiln_rules_bridge::flags_seen() & KILN_RULES_GLIDE_STEER)) return;  // no rule set ever asked for it
    const auto* input = *reinterpret_cast<const uint8_t* const*>(c.rbp + static_cast<int8_t>(L.player_input));
    if (!input) return;  // players only
    void* ctx = *reinterpret_cast<void* const*>(c.rbp + L.ctx);
    if (!ctx) return;
    KilnRulesPlayer r;
    if (!rules_for(ctx, &r) || !(r.flags & KILN_RULES_GLIDE_STEER)) return;
    const auto* ai = *reinterpret_cast<const uint8_t* const*>(c.rbp + static_cast<int8_t>(L.actor_input));
    if (!ai) return;
    float z = 0.0f;
    std::memcpy(&z, ai + L.move_z, sizeof z);
    const uint8_t mode = input[L.stick_mode];
    const float game = c.xmm6.f32[0], pitch = c.xmm14.f32[0];
    const float step = kiln_glide::steer_step(game, pitch, c.xmm2.f32[0], z, mode, r.glide_steer_limit);
    if (!std::isfinite(step) || step == game) return;
    c.xmm6.f32[0] = step;
    S.steered.fetch_add(1, std::memory_order_relaxed);
    if (!g_told.exchange(true))
        logf(KILN_LOG_INFO, "glider steering: first steered step (pitch %.1f degrees, stick %.2f: the game's step %.3f, Kiln's %.3f "
             "degrees; limit %.0f)", pitch / kiln_glide::kRad, z, game / kiln_glide::kRad, step / kiln_glide::kRad, r.glide_steer_limit);
}

void hk_steer(safetyhook::Context& c) {
    __try {
        steer_body(c);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        S.faults.fetch_add(1, std::memory_order_relaxed);
    }
}

}  // namespace

bool active() { return g_active.load(std::memory_order_relaxed); }
uint64_t steered() { return S.steered.load(std::memory_order_relaxed); }
uint64_t faults() { return S.faults.load(std::memory_order_relaxed); }

uint32_t install(const KilnApi* api, KilnOwner owner, std::string& off) {
    g_api = api;
    g_active.store(false);
    g_client = static_cast<const KilnClientApi*>(
        api->service(KilnStr{KILN_CLIENT_SERVICE, sizeof(KILN_CLIENT_SERVICE) - 1}, KILN_CLIENT_VERSION));
    auto note = [&off](const char* why) { off += (off.empty() ? "glider steering (" : ", glider steering (") + std::string(why) + ")"; };
    uintptr_t site = 0, called = 0, current = 0;
    if (!(feat("rules.glide_steer") && sym("rules/GlideSteer", &site) && sym("rules/GlideSteerEntityId", &called) &&
          sym("rules/CurrentEntityId", &current) && cst("rules/rot.ctx", &L.ctx) && cst("rules/rot.actor_input", &L.actor_input) &&
          cst("rules/rot.player_input", &L.player_input) && cst("rules/rot.move_z", &L.move_z) &&
          cst("rules/input.stick_mode", &L.stick_mode))) {
        note("rules.glide_steer");
        return 0;
    }
    // the slot is the ProgramContext only if actor_rotation passes it to CurrentEntityId (the resolver cannot compare)
    if (called != current) {
        logf(KILN_LOG_ERROR, "glider steering: actor_rotation's context slot goes to rva 0x%llx, not CurrentEntityId; it stays off",
             static_cast<unsigned long long>(called - reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr))));
        note("context check failed");
        return 0;
    }
    g_entity_of = reinterpret_cast<CurrentEntityIdFn>(current);
    h_steer.install(api, owner, site, &hk_steer, "rules/GlideSteer");
    if (!h_steer) {
        logf(KILN_LOG_ERROR, "glider steering: hooking actor_rotation's pitch step failed; it stays off");
        note("hook failed");
        return 0;
    }
    g_active.store(true);
    logf(KILN_LOG_INFO, "glider steering: actor_rotation's pitch step at rva 0x%llx (S raises the nose, centred holds, W dives; "
         "for players with GlideSteer)", static_cast<unsigned long long>(site - reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr))));
    return KILN_RULES_HOOK_GLIDE_STEER;
}

}  // namespace kiln_rules_steer
