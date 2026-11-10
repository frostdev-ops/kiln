// SPDX-License-Identifier: MIT
// kiln.hooks as seen from the rest of kiln-core (boot.cpp, other core services). Plugins use the C table instead
// (abi/services/kiln_hooks.h).
#pragma once
#include <vector>

#include "hooks_chain.h"

namespace kiln::hooks {

// A snapshot of every hooked address with its links and the foreign hooks found on it (what hooks.json lists).
inline std::vector<chain::TargetInfo> hooked_targets() { return chain::targets(); }


// Makes the registry usable now (idempotent). The service's init() calls it; core code that hooks before services start
// (tick.cpp) calls it first, then places links with chain::install_raw / install_mid / install_structured (owner 0 is
// "kiln"). False when the image is unusable or no thread-local slot is left: hook with safetyhook then, or not at all.
bool ensure_ready();

// Boot end, after every plugin and native mod started: checks nothing hooked over Kiln's detours, writes
// <kiln>/state/hooks.json (phase "boot") and logs the summary. No-op when the service is off (safe idle).
void boot_complete();

// Writes hooks.json now (phase is recorded in the file). The first world and link changes write it on their own.
void write_map(const char* phase);

// Asks for a debounced rewrite (at most once per second) after boot_complete.
void mark_map_dirty();

// Appends sections to hooks.json ("overlaps", "rules"). `json` is a nlohmann::ordered_json*; the callback runs without
// the registry lock, after the base sections are built. Register once, before boot_complete() writes the first file
// (core boot code, service init); call write_map("...") yourself if you register later.
void add_map_extension(void (*fn)(void* json, const char* phase));

}  // namespace kiln::hooks
