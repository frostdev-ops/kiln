// SPDX-License-Identifier: MIT
#pragma once
#include <cstdarg>
#include <string_view>

#include "common.h"

namespace kiln::log {

// Opens <kiln>/logs/kiln.log (the previous one becomes kiln.prev.log) and starts the writer thread.
void start();
void set_level(int level);  // KILN_LOG_*; lines above it are dropped

// Thread-safe. Never blocks on file I/O: lines go to a queue that the writer thread drains.
void write(int level, std::string_view source, std::string_view text);
void writef(int level, const char* source, const char* fmt, ...);

#define KLOG_E(...) ::kiln::log::writef(KILN_LOG_ERROR, "kiln", __VA_ARGS__)
#define KLOG_W(...) ::kiln::log::writef(KILN_LOG_WARN, "kiln", __VA_ARGS__)
#define KLOG_I(...) ::kiln::log::writef(KILN_LOG_INFO, "kiln", __VA_ARGS__)
#define KLOG_D(...) ::kiln::log::writef(KILN_LOG_DEBUG, "kiln", __VA_ARGS__)

// Mirror of the game's own log. `install_game_listener` registers with the game's addLogListener; the listener only
// copies the line into the queue (it runs under the game's log lock, on any thread), and the writer thread hands it
// to subscribers (registry).
bool install_game_listener(uintptr_t add_log_listener);
uint64_t game_lines_seen();
uint64_t game_lines_dropped();

}  // namespace kiln::log
