// SPDX-License-Identifier: MIT
// kiln-core's side of <game>/kiln/state/progress.json (the model and writer are progress_file.h; this is the process's
// one writer, services/progress_file.cpp). boot() begins it and walks the boot's steps; api.cpp, early_plugins.cpp
// and tick.cpp report into it; everything else reports through kiln.progress v2 (`step`, `mark`).
// Every call is cheap (a lock and a copy) and never throws; before begin() and after the file's end it does nothing.
#pragma once
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

#include "kiln_api.h"

namespace kiln::progress_state {

// The file's first step ("start") began at boot's start: `started_qpc_ms` (now_ms()) and `started_utc` (RFC 3339 with
// milliseconds) are that moment. `kiln` is the full version with the build stamp.
void begin(const std::string& kiln, const std::string& side, double started_qpc_ms, const std::string& started_utc,
           std::vector<std::string> plan);
bool begun();

// The running step or the next one of the plan (Model::step); false when refused or over.
bool step(std::string_view id, std::string_view label = {}, std::string_view detail = {}, uint64_t done = 0,
          uint64_t total = 0);
// step(), and when the plan has steps before `id` this start never began (a plan made from other settings than the
// ones that applied: a Kiln bug), those are taken out of the plan with a warning in kiln.log, and `id` begins.
void step_or_skip(std::string_view id, std::string_view label = {}, std::string_view detail = {});
void part(std::string_view label, uint64_t took_ms);  // kept from 500 ms
void loading();                                       // boot is complete
void mark(uint32_t what);                             // KILN_PROGRESS_MARK_* and the core's own world_started (3)
void idle(std::string_view why);
void failed(std::string_view why);

// The early plugins' progress window (kiln.progress v1): its text is the early-plugins step's detail.
void window_text(std::string_view text);

// RFC 3339 UTC with milliseconds ("2026-11-13T17:28:19.975Z").
std::string utc_now_ms();

// kiln.progress v2 entries (services/progress.cpp's table).
KilnResult KILN_CALL api_step(KilnStr id, KilnStr label, KilnStr detail, uint64_t done, uint64_t total);
KilnResult KILN_CALL api_mark(uint32_t what);

}  // namespace kiln::progress_state
