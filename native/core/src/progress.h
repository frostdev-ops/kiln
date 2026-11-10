// SPDX-License-Identifier: MIT
// kiln.progress (abi/services/kiln_progress.h; services/progress.cpp): the progress window for work before the game's
// own window. The table answers api->service() in every mode (like kiln.profile); the early step closes what a plugin
// forgot to end.
#pragma once
#include "services/kiln_progress.h"

namespace kiln::progress {

const KilnProgressApi* table();

// Ends every task and waits (briefly) for the window to be gone: the early step is over, the game's window comes next.
void close_all();

}  // namespace kiln::progress
