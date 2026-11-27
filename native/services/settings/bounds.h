// SPDX-License-Identifier: MIT
// kiln.settings: the difficulty bounds of Kiln's registry (dev.32, the `difficulty` registry; docs/re/settings.md section 8).
// The game keeps a world's own (Custom) settings inside keen::GameSettingsPresetsResource's bounds when it reads them at start,
// and a mod changes those bounds through the registry (difficulty.<setting>.min / .max, restart fields). kiln.settings checks
// a value against its own range table (catalog.cpp); where the registry's report says a mod (or an older Kiln path under the
// registry) set a bound, that bound replaces the table's, so a value the game keeps at start is a value kiln.settings accepts
// and the other way round. Bounds no mod set keep the table's range (the documented one, as before).
//
// Pure (no Windows, no Kiln service; Kiln's result codes only): host-tested by tests/bounds_test.sh.
#pragma once
#include <cstdint>
#include <functional>
#include <map>
#include <optional>
#include <string>

#include "kiln_api.h"

namespace kset {

// One setting's bounds as the registry resolved them: the registry's own units (factors and whole numbers as they are,
// durations in seconds), each with the writer that decided it (registry.json's "winner"; empty: none named).
struct RegistryBound {
    std::optional<double> min, max;
    std::string min_by, max_by;
};

// The difficulty bounds in the body of state/registry.json (kiln.registry's report_json; schema/state-registry.schema.json
// "fields"): every row of registry "difficulty", entry "game:difficulty", field "<setting>.min" or "<setting>.max" whose
// result is a number, by setting name in lower case. A row for a raw path, a row without a numeric result and text that is
// not a report give nothing (the table's ranges stand).
std::map<std::string, RegistryBound> registry_bounds(const std::string& report);

// The range kiln.settings accepts for a setting of `kind` (KILN_SETTING_FLOAT, _INT or _DURATION) whose table range is
// cat_min..cat_max (durations in nanoseconds): each bound the registry gives in place of the table's (durations converted
// from seconds). Where the two cross (a registry max below the table's min, or a registry min above the table's max) the
// registry's bound decides: the range shrinks to it. b may be null (no registry bound).
void effective_range(uint32_t kind, double cat_min, double cat_max, const RegistryBound* b, double* lo, double* hi);

// The whole text of a Kiln reading member (kiln.registry report_json): `fn(buf, cap, &len)` copies at most cap - 1 bytes and
// answers KILN_ERR_BUFFER_TOO_SMALL with the whole length when the text did not fit; buf may be NULL with cap 0 (a size query);
// the text can grow between two calls. false + *rc: not read (the member's error, or it kept growing).
bool read_all(const std::function<KilnResult(char* buf, size_t cap, size_t* len)>& fn, std::string* out, KilnResult* rc);

}  // namespace kset
