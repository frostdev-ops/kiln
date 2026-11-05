// SPDX-License-Identifier: MIT
// kiln.playerstate inside kiln-players.dll (kiln_playerstate.h): what players.cpp calls.
#pragma once
#include "kiln_api.h"

namespace kiln::playerstate {

// kiln_plugin_main: resolves the symbols and publishes "kiln.playerstate".
void main(const KilnApi* api, KilnOwner owner);
// on_ready: looks up the services it reads (kiln.build's base list, kiln.client's local player).
void ready();

}  // namespace kiln::playerstate
