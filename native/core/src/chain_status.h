// SPDX-License-Identifier: MIT
// The chain entry status names, and the status a report shows for an entry, in one place. state/boot.json (boot.cpp),
// state/hooks.json (compat.cpp) and state/native.json (native_mods.cpp) all name KilnChainEntry.status the same way and
// all show the late phase's final status for an entry the proxy deferred (chain.ini [late]).
#pragma once
#include <cstddef>
#include <cstdint>
#include <string>

#include "kiln_api.h"

namespace kiln {

// KILN_CHAIN_* as the reports spell it; a value this core does not know reads as "failed" (as an older core does).
inline const char* chain_status_name(uint32_t status) {
    static const char* const kNames[] = {"loaded", "failed", "missing", "refused", "duplicate", "skipped", "late"};
    return status < sizeof kNames / sizeof *kNames ? kNames[status] : "failed";
}

// The late phase's status of a deferred entry that means "the DLL was in the process already" (native_mods.h LateRow).
inline constexpr const char* kLateAlreadyLoaded = "already-loaded";

// What the late phase did with chain entry `index` when the proxy deferred it (KILN_CHAIN_LATE); `pending` when it has
// not decided yet. Any other entry: the proxy's own status. `late_rows`: anything with .chain_index and .status
// (native_mods::LateRow). The raw late status can be `already-loaded` and `skipped` besides the proxy's names.
template <class Rows>
std::string chain_final_status(const KilnChainEntry& e, size_t index, const Rows& late_rows, const char* pending) {
    if (e.status != KILN_CHAIN_LATE) return chain_status_name(e.status);
    std::string status = pending;
    for (const auto& row : late_rows)
        if (row.chain_index == index && row.status != "pending") status = row.status;
    return status;
}

// For readers that know the six statuses the proxy writes (boot.json): `already-loaded` is a DLL that is loaded.
inline std::string chain_reported_status(const std::string& final_status) {
    return final_status == kLateAlreadyLoaded ? "loaded" : final_status;
}

}  // namespace kiln
