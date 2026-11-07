// SPDX-License-Identifier: MIT
// Symbol database: resolves game functions and layout constants on the running executable.
//
// Definitions (kiln/sigdb/<side>.json) give each symbol a list of strategies tried in order. A strategy is a byte
// pattern with optional typed holes that capture values while matching:
//   "48 8D BA {world.storage:d32} 44 89"   d8/d16/d32 = unsigned constant, s32 = signed constant
//   "E9 {WaitTaskGroup:rel32}"            rel32 = the absolute target of a 32-bit relative jump/call, as a symbol
// A strategy may be scoped to the first N bytes of another symbol ("scope": {"symbol": "SimStep", "size": 8192}).
// The scope symbol may be one that only rel32 holes capture: the definitions that capture it are resolved first.
// A constant must get at least `min_votes` captures and all captures must agree; a fixed `value` is checked against
// any captures. Features list the symbols and constants they need; one missing piece disables only that feature.
//
// Fragments: a mod may ship its own definitions (kiln.hooks loads <mod>/sigdb/server.json). Every name a fragment
// defines or captures carries the prefix "<id>/" (probes "@<id>/"); its strategies may be scoped to core symbols.
// A fragment resolves against the running exe with exactly the rules above, in its own namespace, at runtime.
//
// Which bytes: patterns match the exe's .text as the file on disk has it (mapped once by init_image, relocated like
// the live image), and each hit is translated to its live address (image base + RVA). Another engine's inline hook
// (a jmp over a prologue) therefore cannot hide a function from Kiln. When the file can't be used (unreadable, or its
// .text is not the loaded one) the live .text is scanned, as before. A symbol whose live bytes differ from the file's
// is reported (kiln.log, sigdb-report.json "live"): someone hooked or patched it.
#pragma once
#include <string>
#include <string_view>
#include <vector>

#include "common.h"
#include "sigdb_pattern.h"  // Hole, Pattern, parse_pattern, scan, hole_value

namespace kiln::sigdb {

bool load_and_resolve(const std::filesystem::path& definitions);
bool symbol(std::string_view name, uintptr_t* out);  // absolute address (core definitions only)
bool constant(std::string_view name, int64_t* out);
bool feature(std::string_view name);
std::vector<std::string> features();       // all defined features
std::vector<std::string> failed_features();
void write_report(const std::filesystem::path& file);
double resolve_ms();

// ---- fragments (thread-safe; resolution scans the image, so call it outside hot paths) ----
struct FragmentSummary {
    size_t symbols = 0, symbols_ok = 0;      // excluding @probes
    size_t constants = 0, constants_ok = 0;
    size_t features = 0, features_ok = 0;
    double ms = 0;
    std::vector<std::string> problems;       // "name: reason" for every unresolved symbol/constant
};
// Loads `file` as fragment `id` (replacing an earlier one with that id), resolves it, writes `report` (JSON, same
// shape as sigdb-report.json) when not empty. False with `error` when the file cannot be used at all (parse error,
// a name without the "<id>/" prefix, ...); true otherwise, even when some symbols stay unresolved.
bool load_fragment(std::string_view id, const std::filesystem::path& file, const std::filesystem::path& report,
                   FragmentSummary* summary, std::string* error);
bool unload_fragment(std::string_view id);
// Core first, then the fragment named by the part before the first '/'.
bool any_symbol(std::string_view name, uintptr_t* out);
bool any_constant(std::string_view name, int64_t* out);
bool any_feature(std::string_view name);

// The code sigdb scans (see above), for kiln.hooks' scan API: matches of `p` in .text as live addresses, at most
// `limit`, and a hole's value at a live match address, read from the same bytes the match was found in.
std::vector<uintptr_t> scan_code(const Pattern& p, size_t limit);
bool hole_value_at(const Pattern& p, uintptr_t live_match, std::string_view name, int64_t* out);
bool code_from_disk();  // true when the file's .text is in use, false on the live fallback

}  // namespace kiln::sigdb
