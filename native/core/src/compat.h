// SPDX-License-Identifier: MIT
// Closed-source DLL mods (docs/kiln/engines/README.md): the overlap report and the compatibility rules in kiln-core.
//  - overlaps: where another module's hook and a Kiln-hosted hook meet (foreign-before / foreign-over from the hook map,
//    refused from ShroudForge's state), added to state/hooks.json as "overlaps" with one kiln.log warning each;
//  - rules (schema/compat-rules.schema.json, compat_rules.h): evaluated whenever the map is written (boot end, first
//    world, changes), reported as "rules"; a `disable` of a mod the profile lists is applied before anything reads the
//    profile (apply_early); `late` and `disable` of chain entries belong to the launcher (it writes chain.ini) and are
//    reported as not applied.
#pragma once
#include <string>
#include <string_view>
#include <vector>

#include "compat_rules.h"
#include "nlohmann/json.hpp"

namespace kiln::compat {

using ojson = nlohmann::ordered_json;

// A chain entry as the proxy and the late phase left it.
struct ChainRow {
    std::string path;    // the proxy's canonical path (UTF-8, Windows form)
    std::string file;    // its file name
    std::string source;  // chain | emf | profile
    std::string status;  // loaded | failed | missing | refused | duplicate | skipped | late (not loaded yet)
    bool late = false;   // named in chain.ini [late]
    bool on() const { return status == "loaded" || status == "duplicate" || status == "late"; }
};

// A hook ShroudForge could not place (its state/shroudforge.json "native" status: `game_thread=unavailable(<hook>:<reason>)`).
struct Refused {
    std::string hook, reason;
};

// Owner names in the report: the registry's `service:` / `plugin:` prefix, a leading `kiln-` and `.dll` are dropped.
std::string normalize_owner(std::string_view owner);
// The refused hooks in <state>/shroudforge.json's text; sf_running: its mode is hosted or self-hosted.
std::vector<Refused> parse_sf_status(std::string_view text);
bool sf_running(std::string_view text);

// The "overlaps" array for a hook map document (hooks.json: "targets", "foreign") with the chain entries and refusals.
// Pure: no process state is read.
ojson compute_overlaps(const ojson& map, const std::vector<ChainRow>& chain, const std::vector<Refused>& refused);

// Boot, in order (boot.cpp): init() after the profile is read (rule files, hook-map extension); apply_early() before the
// native loader and the plugins read the profile; boot_done() after hooks.json was first written.
void init();
void apply_early();
void boot_done();

}  // namespace kiln::compat
