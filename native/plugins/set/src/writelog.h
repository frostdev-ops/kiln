// SPDX-License-Identifier: MIT
// After Shrouded Engine Tweaks 0.2.0 runtime/writelog.h, Copyright (c) 2026 Cacaida, MIT
// (licenses/ShroudedEngineTweaks-LICENSE.txt): the same rules and log lines, on Kiln's resources.
// The write log: what mods changed in the game's resources, so it can be undone when they stop running. One record
// per value, whichever mods wrote it: the game's original bytes, the latest logged bytes and the mods that wrote it.
// When the last of them leaves, the original comes back, unless something outside the log (the console, the game)
// changed the value since. Lua's threads only, under the Lua lock.
// Kiln's differences: a record is keyed by the resource and the offset inside it, not by an address, because
// kiln.assets may hand out another buffer for the same resource on a later tick. Only resources are logged: Kiln has no
// GameOptions, and Systems.enable is undone by kiln.types' owners (lua_api.cpp gSystemOwners).
#pragma once
#include <cstddef>
#include <cstdint>
#include <string>

namespace res {
struct Entry;
}

namespace wlog {

using Sink = void (*)(const char* line);

// Before mod writes n bytes at off inside resource e, whose bytes are at data now. Saves the game's original of every
// byte not logged yet and adds mod as a writer. False if that needs a new record and the budget is full: the write
// must not happen.
bool reserve(const res::Entry* e, const uint8_t* data, uint32_t off, uint32_t n, const std::string& mod);
void wrote(const res::Entry* e, uint32_t off, const void* src, uint32_t n);  // the write reserve allowed happened
void leave(const std::string& mod, Sink sink);  // mod stopped while the others go on (it failed while loading)
void leaveAll(Sink sink);                       // every mod stopped: reload, Lua off, kiln-set stepping aside
void forget();                                  // the game is closing: nothing is restored
size_t records();
void setBudget(size_t bytes);  // tests only

}  // namespace wlog
