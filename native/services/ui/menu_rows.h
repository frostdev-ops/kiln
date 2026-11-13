// SPDX-License-Identifier: MIT
// kiln.ui: which of Kiln's rows a game menu draws before its Settings entry (game.cpp). Plain logic: no Windows, no game
// memory, so tests/menu_rows_test.cpp runs it on the build machine.
//
// The game's menus do not scroll. Every row drawn before Settings pushes the game's own entries down, and with enough of
// them the last ones (Quit) leave a 1080p screen (a player's report, 2026-11-13; the arrow keys still reached Quit).
// Kiln's own MODS row is always drawn: it opens the Mods page, where every mod's row is listed and can be chosen
// (KilnUiApi.menu_entries / menu_activate). Mods' rows follow the setting (KILN_UI_MENU_SHOW_*):
//  - FEW (the default): a menu draws its mods' rows while it has at most kFew of them, else none of them;
//  - ALWAYS: every row, as before 0.2.0-dev.27;
//  - NEVER: none.
// kFew is a guess: neither the code nor docs/re/game-ui.md gives the height of a menu row or the menus' top on a 1080p
// screen, so the number of rows that still leave Quit on the screen is not known. 3 rows plus MODS is what a guided
// check (mods/tests/client-pass) is asked to confirm.
#pragma once

#include <cstddef>
#include <cstdint>

namespace kui::menu_rows {

inline constexpr uint32_t kFew = 3;  // a guess, see above

inline constexpr uint32_t show_few = 0;     // KILN_UI_MENU_SHOW_FEW
inline constexpr uint32_t show_always = 1;  // KILN_UI_MENU_SHOW_ALWAYS
inline constexpr uint32_t show_never = 2;   // KILN_UI_MENU_SHOW_NEVER

// Does a menu with `mods_rows` mods' rows (visible ones: a row its mod hid is not counted) draw them? An unknown mode
// is FEW.
constexpr bool draws_mods(uint32_t mode, size_t mods_rows) {
    if (mode == show_always) return true;
    if (mode == show_never) return false;
    return mods_rows <= kFew;
}

// Keeps the rows a menu draws from `rows` (the menu's visible rows, in order): Kiln's own (is_kiln) always, the mods'
// as draws_mods says. Container: a std::vector-like sequence.
template <typename Rows, typename IsKiln>
Rows pick(const Rows& rows, uint32_t mode, IsKiln is_kiln) {
    size_t mods = 0;
    for (const auto& r : rows)
        if (!is_kiln(r)) ++mods;
    if (draws_mods(mode, mods)) return rows;
    Rows out;
    for (const auto& r : rows)
        if (is_kiln(r)) out.push_back(r);
    return out;
}

}  // namespace kui::menu_rows
