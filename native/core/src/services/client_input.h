// SPDX-License-Identifier: MIT
// kiln.client's local input selection: the one place Kiln decides which keen::ecs::ClientPlayerInput is the local
// player's, and the one entry of its observation on the client's main thread. Every reader and writer of the local input
// goes through it (the building-input producer, client_building_input.h, and the game input sample,
// client_game_input.h), so each engine function on the input path gets at most one Kiln link, owned here.
//
// The selection: the local player's entity (client.cpp reads LocalPlayerData at each client-world Pre) and the client
// session it was read in. The observation point (sigdb client.input_observation, docs/kiln/services/
// client-building-input.md): the client's local input writer, right after it filled the frame's ClientPlayerInput
// (digitalInput for this client world step, the camera) and before the step's input message copies it; the client
// world's programs (the cursor among them) run after that message. client.cpp installs it as one kiln.hooks mid link,
// which shares its detour with kiln.rules' read-only camera check, at the first building input request (never at the
// start), and binds the producer with the build's layout read when the point resolved. The link calls visit().
#pragma once
#include <cstdint>

#include "client_building_input.h"

namespace kiln::client_input {

struct Selection {
    KilnEntity entity;  // the local player's entity, 0 = none
    uint32_t session;   // the client session (KilnSessionInfo.sessions[KILN_WORLD_CLIENT]) it belongs to, 0 = none
};
void publish(KilnEntity entity, uint32_t session);  // client.cpp, on the client's main thread
Selection selection();                              // any thread: one consistent copy

// The observation's entry, on the client's main thread: one ClientPlayerInput the input writer filled, its entity and
// the game's building input version for the visit (the client world's step, as the game stamps its own input actions).
// Only the selected (local) player's input goes on to the producer, through SEH-guarded copies.
void observe(uintptr_t record, KilnEntity entity, uint32_t version);

// Where the observation point finds the version (sigdb client.input_observation): the client world's pointer in the
// client game's input object, and the step counter in that world (byte offsets).
struct Site {
    uint32_t world;  // client/input.world
    uint32_t step;   // client/input.step
};
// One visit at the observation point (the link's handler; the client's main thread): `input` is the client game's input
// object, `record` the ClientPlayerInput the writer just filled and `entity` its entity. Reads the step through guarded
// copies (a fault skips the visit and is counted), hands the selected (local) player's visit to the game input consumer
// first (it reads the game's own sample before anything changes it) and goes on to observe().
void visit(uintptr_t input, uintptr_t record, KilnEntity entity, const Site& site);

// The second reader and writer of the local input: kiln.client.game_input (client_game_input.h), given at its first use.
// Called from visit() on the client's main thread for the selected player only. nullptr: none (the default).
using Consumer = void (*)(uintptr_t record, uintptr_t input, uintptr_t world, KilnEntity entity, uint32_t session,
                          uint32_t version);
void set_consumer(Consumer consumer);

// The observation link itself (client.cpp): one kiln.hooks mid link at client/InputObserved, installed at the first need
// of either consumer (building input's first submit, game input's first use) and never removed. observation_ready():
// the build has the point and the local player can be known (the reason when not, a static string). ensure_link():
// installs it once; Again: refused because some thread could not be checked (nothing written; a later call tries again);
// Failed: final for the process. Both from any thread.
enum class Link { Bound, Again, Failed };
bool observation_ready(const char** why);
Link ensure_link(const char** why);

struct Stats {
    uint64_t visits;  // visits at the observation point
    uint64_t faults;  // visits whose step could not be read (skipped)
};
Stats stats();
void reset_stats_for_tests();

// Copies of game memory under a structured-exception guard (guard::copy): a fault answers false.
building_input::Memory guarded_memory();

}  // namespace kiln::client_input
