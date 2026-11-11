// SPDX-License-Identifier: MIT
// From Shrouded Engine Tweaks 0.2.0 runtime/modsettings.h, Copyright (c) 2026 Cacaida, MIT
// (licenses/ShroudedEngineTweaks-LICENSE.txt). Kiln's changes: the file is written through the mod sandbox
// (sandbox.h: no links, no reparse points, UTF-8 paths) and swapped in with std::filesystem::rename. Kiln has no Mod
// Settings window: nothing calls set(..., edit = true) in the game, so onChange only runs in the tests.
// Typed mod settings (Settings.define): each mod's values live here, in C++, so the overlay's Mod Settings window
// reads and edits them without waiting for Lua. Saved to mods\<mod>\settings.json, written to a .tmp file and swapped
// in. A mutex guards the values; the files are written outside it, only from the Lua side (under the Lua lock).
#pragma once
#include <cmath>
#include <map>
#include <string>
#include <utility>
#include <vector>

namespace mset {

enum Type { Bool, Number, Int, Choice, Text };

struct Field {
    std::string key, label;
    Type type;
    double lo = -HUGE_VAL, hi = HUGE_VAL;  // number, int: the range
    size_t maxLen = 256;                   // text: bytes
    std::vector<std::string> values;       // choice
    double num = 0;                        // bool 0 or 1, number, int, choice: the index into values
    std::string text;                      // text
};

struct Page {
    std::string mod, path;  // path: the mod's folder (UTF-8); the file is settings.json in it
    std::vector<Field> fields;
    std::map<std::string, std::string> unknown;  // keys the file has and the mod doesn't define: JSON text, kept
    bool dirty = false;                          // changed since the last save
    bool told = false;                           // a failed save was reported
};

using Sink = void (*)(const char* line);

void define(Page page);                                                    // replaces the mod's page
bool has(const std::string& mod);
// num and text into f, clamped to its range or length; false (f unchanged) for NaN or a choice index out of range.
bool assign(Field& f, double num, const std::string& text);
bool get(const std::string& mod, const std::string& key, Field& out);     // a copy of the field
// assign, on the mod's page. edit: from the overlay, so onChange gets it at the next tick.
bool set(const std::string& mod, const std::string& key, double num, const std::string& text, bool edit);
std::vector<std::pair<std::string, std::string>> takeEdits();             // mod, key; each once
std::vector<Page> pages();                                                // copies, by mod name
std::string toJson(const Page& page);
void save(bool now, Sink sink);           // the changed pages, at most once a second unless now
void drop(const std::string& mod, Sink sink);  // saves the mod's page if changed, then removes it
void dropAll(Sink sink);

}  // namespace mset
