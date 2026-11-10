// SPDX-License-Identifier: MIT
// kiln.items: gear options at creation (pide.cpp): KILN_ITEMS_UNLOCK and KILN_ITEMS_GEM_SLOT.
#pragma once
#include <cstdint>

#include "kiln_api.h"
#include "services/kiln_items.h"

namespace kitems {
bool pide_install(const KilnApi* api, KilnOwner owner);  // feature items.pide_options; false: the options are refused
bool pide_available();
bool pide_request_valid(uint32_t flags, uint8_t unlock);
// Scoped to this give's item and thread. Cleanup also runs when the engine commit raises a structured exception.
void pide_commit(void* tx, void(KILN_CALL* commit)(void*), uint32_t flags, uint8_t unlock, uint32_t item);
void pide_begin(uint32_t flags, uint8_t unlock, uint32_t item);
void pide_end();
void pide_counters(uint64_t* unlocked, uint64_t* gems, uint64_t* faults);
}  // namespace kitems
