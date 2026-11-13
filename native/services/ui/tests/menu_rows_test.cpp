// SPDX-License-Identifier: MIT
// Host test of menu_rows.h: which of Kiln's rows a game menu draws before its Settings entry. A player's pause menu with
// many mods' rows pushed Quit off a 1080p screen (2026-11-13): with the default mode a menu with more than a few mods'
// rows draws only Kiln's own Mods row, and the others are reached from the Mods page.
#include <cstdio>
#include <string>
#include <vector>

#include "../menu_rows.h"
#include "services/kiln_ui.h"

using namespace kui::menu_rows;

static_assert(show_few == uint32_t{KILN_UI_MENU_SHOW_FEW} && show_always == uint32_t{KILN_UI_MENU_SHOW_ALWAYS} &&
                  show_never == uint32_t{KILN_UI_MENU_SHOW_NEVER},
              "menu_rows.h follows kiln_ui.h");

namespace {
int failures = 0, checks = 0;
void check(bool ok, const char* what) {
    ++checks;
    if (!ok) { ++failures; std::printf("FAIL %s\n", what); }
}

struct Row {
    std::string text;
    bool kiln;
};
using Rows = std::vector<Row>;
bool is_kiln(const Row& r) { return r.kiln; }

// a pause menu's rows in drawing order: n mods' rows, then Kiln's Mods row (order 1000)
Rows menu(size_t n) {
    Rows r;
    for (size_t i = 0; i < n; ++i) r.push_back({"mod " + std::to_string(i), false});
    r.push_back({"Mods", true});
    return r;
}

std::string texts(const Rows& rows) {
    std::string s;
    for (const Row& r : rows) s += (s.empty() ? "" : ",") + r.text;
    return s;
}
}  // namespace

int main() {
    // the default (few): up to kFew mods' rows are drawn, with Kiln's
    check(kFew == 3, "kFew is 3 (a guess; the client check confirms it)");
    check(pick(menu(0), show_few, is_kiln).size() == 1, "few: no mods' rows, Kiln's Mods row only");
    check(texts(pick(menu(1), show_few, is_kiln)) == "mod 0,Mods", "few: one mod's row is drawn");
    check(texts(pick(menu(3), show_few, is_kiln)) == "mod 0,mod 1,mod 2,Mods", "few: three mods' rows are drawn");
    // the player's case: more than a few rows push the game's own entries down; none of them is drawn then
    check(texts(pick(menu(4), show_few, is_kiln)) == "Mods", "few: four mods' rows, only Kiln's Mods row is drawn");
    check(texts(pick(menu(12), show_few, is_kiln)) == "Mods", "few: twelve mods' rows, only Kiln's Mods row is drawn");
    // Kiln's row stays wherever it is in the order
    {
        Rows r = {{"Mods", true}, {"a", false}, {"b", false}, {"c", false}, {"d", false}};
        check(texts(pick(r, show_few, is_kiln)) == "Mods", "few: Kiln's row first, the mods' rows dropped");
    }
    // always: as before 0.2.0-dev.27
    check(pick(menu(12), show_always, is_kiln).size() == 13, "always: every row is drawn");
    // never: Kiln's row only, even with one mod's row
    check(texts(pick(menu(1), show_never, is_kiln)) == "Mods", "never: Kiln's Mods row only");
    check(pick(Rows{}, show_never, is_kiln).empty(), "never: an empty menu stays empty");
    // a value the setting does not know is the default
    check(texts(pick(menu(4), 7, is_kiln)) == "Mods", "an unknown mode is few (four rows: Kiln's only)");
    check(pick(menu(2), 7, is_kiln).size() == 3, "an unknown mode is few (two rows: drawn)");
    // draws_mods on its own (menu_entries' drawn field)
    check(draws_mods(show_few, 3) && !draws_mods(show_few, 4), "draws_mods: few is at most kFew");
    check(draws_mods(show_always, 64) && !draws_mods(show_never, 0), "draws_mods: always and never");

    std::printf("menu_rows_test: %d/%d checks passed\n", checks - failures, checks);
    return failures ? 1 : 0;
}
