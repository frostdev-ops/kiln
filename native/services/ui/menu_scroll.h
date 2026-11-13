// SPDX-License-Identifier: MIT
// kiln.ui: the pause menu in the game's own scroll view (menu_scroll.cpp; docs/re/game-ui.md "Layout and scrolling").
// Plain logic: no Windows, no game memory, so tests/menu_scroll_test.cpp runs it on the build machine.
//
// The game's menus do not scroll, and mods' rows before Settings pushed Quit off a 1080p screen (2026-11-13). The
// pause menu's builder lays its entries out in a column frame (a vertical stack it opens at one place and closes at
// one place, with no way out of the function in between: static analysis of 26430625). Kiln begins the game's own
// scrolling layout (keen::GameUiScrollingLayout, as the credits use it) right after the column is set up and ends it
// right before the column closes, so the entries, the spacers and the build number go into the scroll view.
//
// - Balanced by construction: a begin happens only at the column's open site, an end only at its close site, and the
//   close site ends exactly what the open site of the same builder call began (Pair). An open site that finds a begin
//   still open (the close site did not run: something this analysis missed) says "unbalanced", and the experiment
//   stops for the process.
// - A menu that fits is drawn as it always was: Kiln wraps the column only while the menu has more mods' rows than
//   the rule of menu_rows.h lets it show (kFew), the case where Quit leaves the screen.
// - A crash guard: before the first begin in a process a "trying" marker is written; it goes once the wrapped builder
//   has run to its close site kTrialFrames times in this process (a wrong nesting is fatal within the first frames;
//   no wall-clock time, no "then closed": a player who presses Quit afterwards does not count as a crash). A start
//   that finds it (the game ended while trying) turns it into an "off" record naming the Kiln version and the game
//   build it happened with: scrolling stays off on every start with that version and build; another version or build
//   tries once more. Deleting the file tries again. A marker that exists but cannot be read counts as there (off).
// - After a begin that succeeded, every way out closes what was opened, in order (Nest, unwind_order); when even that
//   faults, nesting cannot be restored and the process ends on purpose (the marker is on the disk: the next start
//   stays off) rather than handing the game a column it would fail on.
#pragma once

#include <cmath>
#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>

#include "menu_rows.h"

namespace kui::menu_scroll {

// ---- one builder call: the column's open site and close site ----
struct Pair {
    bool open = false;  // a scroll view was begun at the open site and not ended yet
};

enum class At {
    nothing,     // nothing to do
    begin,       // begin the scroll view now
    end,         // end the scroll view now (the one this call began)
    unbalanced,  // the open site came twice without the close site: stop, begin nothing
};

// The column's open site. `want`: wrap the column in this frame.
constexpr At at_open(Pair& p, bool want) {
    if (p.open) return At::unbalanced;
    if (!want) return At::nothing;
    p.open = true;
    return At::begin;
}

// The column's close site: ends exactly what the open site of this call began.
constexpr At at_close(Pair& p) {
    if (!p.open) return At::nothing;
    p.open = false;
    return At::end;
}

// Wrap the pause menu's column in this frame? Only where the menu would otherwise run off the screen: the modes that
// draw every mod's row (ALWAYS, AUTO) with more of them than the rule lets a menu show.
constexpr bool wants(uint32_t mode, size_t mods_rows) {
    return (mode == menu_rows::show_always || mode == menu_rows::show_auto) && mods_rows > menu_rows::kFew;
}

// ---- what a begin opened, and the order to close it in ----
struct Nest {
    bool layout = false;  // the game's scroll view was begun
    bool inner = false;   // Kiln's vertical stack inside it was opened
};

enum class Close { inner, layout };

// The closes that restore the nesting, innermost first; `n` is how many (0..2).
constexpr int unwind_order(const Nest& nest, Close out[2]) {
    int n = 0;
    if (nest.inner) out[n++] = Close::inner;
    if (nest.layout) out[n++] = Close::layout;
    return n;
}

// ---- the crash guard ----
inline constexpr uint32_t kTrialFrames = 48;  // wrapped builder calls that reached their close site: the trial passed

struct Record {
    std::string kiln, build;  // Kiln's version, the game exe's sha256
};

inline std::string marker_text(std::string_view kiln, std::string_view build) {
    std::string s = "kiln=";
    s.append(kiln);
    s += "\nbuild=";
    s.append(build);
    s += '\n';
    return s;
}

inline bool parse_marker(std::string_view text, Record& out) {
    out = {};
    bool k = false, b = false;
    while (!text.empty()) {
        const size_t nl = text.find('\n');
        std::string_view line = text.substr(0, nl);
        text = nl == std::string_view::npos ? std::string_view{} : text.substr(nl + 1);
        if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
        if (line.substr(0, 5) == "kiln=") out.kiln = std::string(line.substr(5)), k = true;
        else if (line.substr(0, 6) == "build=") out.build = std::string(line.substr(6)), b = true;
    }
    return k && b && !out.kiln.empty() && !out.build.empty();
}

enum class Start {
    try_it,           // no marker, or an off record of another version or build (the caller deletes it)
    off_after_crash,  // the "trying" marker of an earlier start: the game ended while trying (it becomes the off record)
    off_recorded,     // an off record of this version and build
};

// What reading a marker file found.
enum class File {
    absent,       // not there (file or folder not found)
    read,         // read; its text is in hand
    unreadable,   // there, but it could not be read (access denied, a sharing error, ...): counts as there
};

// `trying`: the trying marker; `off` and `off_text`: the off record (an unreadable or unparsable off record counts as
// this version and build: off, never a loop).
inline Start decide(File trying, File off, const std::string& off_text, std::string_view kiln, std::string_view build) {
    if (trying != File::absent) return Start::off_after_crash;
    if (off == File::absent) return Start::try_it;
    Record r;
    if (off == File::unreadable || !parse_marker(off_text, r)) return Start::off_recorded;
    return r.kiln == kiln && r.build == build ? Start::off_recorded : Start::try_it;
}

// Turning the trying marker into the off record: the trying marker goes only once the off record is on the disk (moved
// there, or written there), so the evidence is never lost; when neither worked both stay as they are (the next start
// finds the trying marker and stays off again).
constexpr bool may_delete_trying(bool moved, bool off_written) { return !moved && off_written; }

// The trial: once per wrapped builder call that reached its close site. Returns true once, when the marker may go.
struct Trial {
    uint32_t frames = 0;
    bool done = false;
};

constexpr bool trial_step(Trial& t) {
    if (t.done) return false;
    if (++t.frames < kTrialFrames) return false;
    t.done = true;
    return true;
}

// ---- sanity of what the game's layout says (a frame's rectangle, UI units) ----
inline bool sane_rect(const float r[4]) {
    for (int i = 0; i < 4; ++i)
        if (!std::isfinite(r[i]) || std::fabs(r[i]) > 100000.0f) return false;
    return r[2] > 0.0f && r[3] > 0.0f;
}

}  // namespace kui::menu_scroll
