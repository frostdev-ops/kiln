// SPDX-License-Identifier: MIT
// Native mods (abi/kiln_native.h, schema/native.schema.json, docs/kiln/native-mods.md): DLL mods described by a
// native.json, loaded with LoadLibrary at their phase. Sources: the profile's "native" entries, then the drop-in
// folder kiln/native/<folder>/ (LEGACY or dropins incl. native). The player's switches: <configDir>/native.json.
//   dllmain  the proxy loaded them in DllMain (kiln/chain.ini [profile]); the core only reports KilnBootInfo.chain
//   early    first in kiln_boot, after paths, log, kiln.json and the profile; runs in safe idle too
//   boot     after Kiln's service DLLs and plugins; runs without hooks too
//   world    at the first world start (server world on a server, client world on a client); needs hooks
// Within a phase: requires + after/before edges, the managed resolver's rules (cycles fail together), ties by id.
// A drop-in whose id the profile also lists is skipped (the profile wins). kiln.json native.policy (always | verified-only | never) gates every load. Never FreeLibrary.
// State: <kiln>/state/native.json, rewritten after each phase.
#pragma once
#include "common.h"

namespace kiln::native_mods {

// Reads every native.json and the user state, resolves, reports dllmain results. `verified`: the build record says
// verified (native.policy verified-only); before sigdb, after init_image/apply_side.
void discover(bool verified);
void run_early();
void run_boot();
// Hooks on: subscribes to the first world start (phase world). Hooks off: world mods become "skipped".
void arm_world(bool hooks);

struct Summary {
    int loaded = 0, failed = 0, pending = 0;
};
Summary summary();

}  // namespace kiln::native_mods
