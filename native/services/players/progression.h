// SPDX-License-Identifier: MIT
// kiln.progression inside kiln-players.dll (kiln_progression.h): what players.cpp calls.
#pragma once
#include "kiln_api.h"

namespace kiln::progression {

// kiln_plugin_main: resolves the layout and publishes "kiln.progression" (unless nothing of it works on this build).
void main(const KilnApi* api, KilnOwner owner);
// on_ready: registers the program kiln_players_progression with kiln.world.
void ready();
// Every server-world Pre, after kiln.players' scan (tick thread).
void pre(uint64_t step);
// The server world's session ended (a hosted game went back to the menu): drop what belongs to it.
void session_end();

}  // namespace kiln::progression

namespace kiln::players {
// A player in game with this entity (kiln.players' snapshot of this step). Any thread.
bool in_game(KilnEntity entity);
}  // namespace kiln::players
