// SPDX-License-Identifier: MIT
// kiln.items: gear options at creation (pide.cpp): KILN_ITEMS_UNLOCK and KILN_ITEMS_GEM_SLOT.
#pragma once
#include <cstdint>

#include "kiln_api.h"
#include "services/kiln_items.h"

namespace kitems {
bool pide_install(const KilnApi* api);  // feature items.pide_options; false: the options are refused
bool pide_available();
// Between begin and end, item entities this thread creates get the give's options (set around the transaction's
// commit; plain calls, as the engine's functions run under structured-exception guards: run_give ends any leftover
// scope before it starts).
void pide_begin(uint32_t flags, uint8_t unlock);
void pide_end();
void pide_counters(uint64_t* unlocked, uint64_t* gems, uint64_t* faults);
}  // namespace kitems
