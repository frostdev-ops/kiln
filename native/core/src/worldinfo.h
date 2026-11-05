// SPDX-License-Identifier: MIT
// The live worlds (server world, and on a client the client/prediction worlds) and their singletons, for services
// (KilnApi.server_world / world / singleton). Set by the tick hook (a world's first step) and the lifecycle hook (a
// world's destruction).
#pragma once
#include <string_view>

#include "common.h"

namespace kiln::worldinfo {

void set_world(uint32_t kind, uintptr_t world);  // KILN_WORLD_*; 0 = gone
uintptr_t world(uint32_t kind);                  // 0 when none (or kind out of range)
inline void set_server_world(uintptr_t w) { set_world(KILN_WORLD_SERVER, w); }
inline uintptr_t server_world() { return world(KILN_WORLD_SERVER); }
// Object registered under `name` (exact registered name, e.g. "g38_daytime::Daytime"); 0 when absent or when the
// "singletons" feature is off. Reads game memory: tick thread (Pre/Post) only.
uintptr_t singleton(uintptr_t world, std::string_view name);

}  // namespace kiln::worldinfo
