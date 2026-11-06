// SPDX-License-Identifier: MIT
// kiln.vfx: the real game (game.cpp) and the simulated one (sim.cpp), both behind kvx::Game (core.h).
#pragma once
#include "core.h"

namespace kvx {

// Resolves symbols and installs the hooks: the guard (features vfx.guard), the FxController capture and calls (vfx.play). nullptr:
// the guard's signatures did not resolve; nothing was hooked and the service stays off.
Game* real_game(const KilnApi* api);
// kiln/config/vfx.json "trace": log every VFX the game creates within `seconds` of a play from Kiln (0 = off). Call before real_game.
void real_game_trace(double seconds);

// A simulated game: its own registered set (sim_known), FX pool, an entity that exists and one that goes away. For tests and
// labs (kiln/config/vfx.json "sim"), so the whole path from Kiln.Vfx to the game's door runs without a client.
Game* sim_game();
void sim_update();  // one simulated FxController update
uint64_t sim_params_applied();
void sim_last_param(uint32_t* hash, uint32_t* type, float* first_float);
uint64_t sim_preloads();
FxParams sim_last_start_params();  // the parameter block of the last simulated start

// The GUIDs the simulated game has registered, in the game's byte order: Guid 5f5f5f5f-0000-4000-8000-00000000000<n + 1>. The first
// has a kill time of 10 s and no parameters, the second kill time 0, duration 2 and lifetime 1.5 (a 3.5 s one-shot) and a "color", magenta by default.
constexpr int kSimKnown = 2;
void sim_known_guid(int n, uint8_t out[16]);

// Entities of the simulated world: this one always exists at (1, 2, 3); the next goes away 30 updates after it is first asked for.
constexpr uint32_t kSimEntity = 0x70000001u;
constexpr uint32_t kSimEntityGoes = 0x70000002u;
// This one exists, but its slots never resolve: the start keeps retrying and no instance comes, as on a body without that slot.
constexpr uint32_t kSimEntityNoSlot = 0x70000003u;

}  // namespace kvx
