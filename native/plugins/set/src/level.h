// SPDX-License-Identifier: MIT
// From Shrouded Engine Tweaks 0.2.0 runtime/level.h, Copyright (c) 2026 Cacaida, MIT
// (licenses/ShroudedEngineTweaks-LICENSE.txt).
// Output levels. A line sent to the runtime's sink may start with one marker byte: the sink strips it, colors the
// line in the overlay and the console window (green, yellow, red) and tags set.log lines [W] or [E]. A line without
// one is plain output (prints, dumps). Status rows take the same levels. LV_LOG instead marks a line for the log only
// (debug lines), which SET keeps out of its overlay and console window; Kiln writes it to kiln.log like the rest.
#pragma once

namespace lv {
enum Level : char { none, ok, warn, bad };
inline Level of(const char* line) { return *line >= ok && *line <= bad ? Level(*line) : none; }
}  // namespace lv

// Prefixes, as their own literals: LV_OK "Abc" is safe where "\x01Abc" would read as the escape \x01A.
#define LV_OK "\x01"
#define LV_WARN "\x02"
#define LV_BAD "\x03"
#define LV_LOG "\x04"
