// SPDX-License-Identifier: MIT
// <game>/kiln/kiln.json (all keys optional):
//   { "policy": "verified-only" | "any", "clr": true, "plugins": true, "log_level": "info",
//     "native": { "policy": "always" | "verified-only" | "never" },
//     "server": { ...same keys... }, "client": { ...same keys... } }
// The "server" / "client" section, when present, overrides the top-level keys on that side (apply_side, once the
// exe's identity is known), so one kiln.json can say e.g. verified-only for servers and any for a test client.
// policy verified-only: hooks are installed only when kiln/sigdb/builds/<side>/<exe sha256>.json says
// "verified": true. Otherwise Kiln stays in "safe idle": it resolves and reports, and the game runs vanilla.
// native.policy (native mods, docs/kiln/native-mods.md): always (default: they load in safe idle too), verified-only
// (only on a verified build), never.
#pragma once
#include <string>

#include "common.h"

namespace kiln {

enum class NativePolicy { Always, VerifiedOnly, Never };

struct Config {
    bool policy_any = false;
    bool clr = true;
    bool plugins = true;
    int log_level = KILN_LOG_INFO;
    NativePolicy native_policy = NativePolicy::Always;
};

const char* native_policy_name(NativePolicy p);  // "always" | "verified-only" | "never"

const Config& config();
void load_config();
void apply_side(uint32_t side);  // KILN_SIDE_*: applies the side's section of kiln.json
bool build_verified(uint32_t side, const std::string& sha256, std::string* note);

}  // namespace kiln
