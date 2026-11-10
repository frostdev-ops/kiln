// SPDX-License-Identifier: MIT
// Closed-source DLL mods (docs/kiln/engines/README.md): the overlap report and the compatibility rules in kiln-core.
//  - overlaps: where another module's hook and a Kiln-hosted hook meet (foreign-before / foreign-over from the hook map,
//    refused from ShroudForge's state), added to state/hooks.json as "overlaps" with one kiln.log warning each;
//  - rules (schema/compat-rules.schema.json, compat_rules.h): evaluated whenever the map is written (boot end, first
//    world, changes), reported as "rules"; a `disable` of a mod the profile lists is applied before anything reads the
//    profile (apply_early); `late` and `disable` of chain entries belong to the launcher (it writes chain.ini) and are
//    reported as not applied. A `disable` of a dllmain native mod the proxy loaded already is not applied either (its DLL
//    runs); a `late` of EMF's loader, or of a DLL another loader started first, is not applied (it cannot be deferred).
#pragma once
#include <set>
#include <string>
#include <string_view>
#include <utility>
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
    bool late = false;   // named in chain.ini [late]: the proxy left it to the core
    // [late] had no effect: the DLL was in the process before the late phase (another loader started it). The row is
    // `loaded`, not a late entry.
    bool late_ignored = false;
    // EMF's own loader (a [chain] entry): it holds the game's start by patching the exe's entry point, so the proxy never
    // defers it and a `late` for it cannot apply.
    bool loader = false;
    // A dinput8-chain.ini entry while EMF's loader is chained: that loader starts the DLL itself, before [late] can.
    bool started_by_loader = false;
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

// A string member of a JSON object: empty when `obj` is no object, has no such key or the value is not a string (null
// included). The reports are built from files and maps other modules shape; a `.value(key, std::string())` throws on null.
std::string json_text(const ojson& obj, const char* key);
// True only for a boolean true member.
bool json_flag(const ojson& obj, const char* key);
// What identifies an overlap for "log it once": kind, symbol (else hook), the other side's module and offset. Never throws.
std::string overlap_key(const ojson& overlap);

// The "overlaps" array for a hook map document (hooks.json: "targets", "foreign") with the chain entries and refusals.
// Pure: no process state is read.
ojson compute_overlaps(const ojson& map, const std::vector<ChainRow>& chain, const std::vector<Refused>& refused);

// What a rule became in this start (the "rules" section of hooks.json): applied, and a note saying what was done or why not.
struct Outcome {
    bool applied = true;
    std::string note;
};
// What the report needs to know beyond the chain rows and the facts.
struct RuleContext {
    std::vector<std::pair<std::string, std::string>> early;  // rule id -> what apply_early() did for it
    std::set<std::string> rule_off;                          // "<engine>:<id>" a rule switched off (still part of this start)
    std::set<std::string> proxy_loaded;                      // ids of the `disable` rules whose mod's DLL the proxy loaded already
};
// Pure: nothing of the process is read.
Outcome outcome_of(const Rule& rule, const std::vector<ChainRow>& rows, const Facts& facts, const RuleContext& ctx);

// Boot, in order (boot.cpp): init() after the profile is read (rule files, hook-map extension); apply_early() before the
// native loader and the plugins read the profile; boot_done() after hooks.json was first written.
void init();
void apply_early();
void boot_done();

}  // namespace kiln::compat
