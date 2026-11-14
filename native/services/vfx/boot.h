// SPDX-License-Identifier: MIT
// kiln.vfx's builder in the game: reads every mod's VFX blocks at boot and serves what cook/ builds through kiln.assets, before the game
// enumerates its VFX resources. See boot.cpp.
#pragma once
#include <cstddef>
#include <cstdint>

#include "kiln_api.h"

namespace kiln::boot {
struct Input;  // kiln_boot_collector.hpp
}

namespace kvx {

// on_ready of the service: the fingerprint, the budget, then every mod in the load order. Nothing here can abort the game: a problem turns
// a block, a mod or all Kiln VFX off with a log line.
// `boot`: a boot generation's prepare input (chapter 08; kiln_boot_collector.hpp): excluded mods are left out, its vfx documents read.
void build_on_ready(const KilnApi* api, KilnOwner self, const kiln::boot::Input* boot = nullptr);
// Takes back what a boot generation's prepare built and served (the generation was not published).
void build_forget();

// The checks that need the game's resident resources: the shader binding list and that what was served is what the game sees. Call it from
// the FxController update, before the queue runs. Cheap after its one run.
void build_on_update();

// True when this GUID is a Kiln VFX that was turned off after it was registered (a binding list the modules were not made for). The guard
// treats it as unknown.
bool build_refuses(const uint8_t guid[16]);

// How long a built one-shot runs, in ns: the block's delay, duration and longest particle life. 0: not a built VFX, or one that runs until
// stopped. Those keep the rule for any other VFX, so an effect played without a lifetime still ends.
int64_t build_one_shot_ns(const uint8_t guid[16]);

// A definition from code: {"format": 1, "mod": "<id>", "dir": "<mod folder>", "vfx": {...}}, the same blocks as a JSON file. Validates and
// builds it. A block the game already has unchanged is "registered"; any other is kept for the next start ("pending"), as the game only
// enumerates VFX once. report: JSON (report_json plus "status" per block). KILN_ERR_INVALID_ARGUMENT: the document or a block is wrong.
KilnResult build_define_json(KilnOwner owner, KilnStr json, char* report, size_t cap, size_t* out_len);

// JSON: what was built, the budget, the fingerprint.
KilnResult build_status_json(char* buf, size_t cap, size_t* out_len);

}  // namespace kvx
