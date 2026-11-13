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
// - A crash guard: before the first begin in a process a "trying" marker is written; it goes once a scrolled menu was
//   seen for kTrialMs and then closed. A start that finds it (the game ended while trying) turns it into an "off"
//   record naming the Kiln version and the game build it happened with: scrolling stays off on every start with that
//   version and build; another version or build tries once more. Deleting the file tries again.
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

// ---- the crash guard ----
inline constexpr uint64_t kTrialMs = 3000;      // a scrolled menu seen this long, then closed: the trial passed
inline constexpr uint64_t kClosedFrames = 30;   // UI frames without a scrolled menu: it was closed

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

// `trying`: the trying marker exists; `off`: the off record's content, or null when there is none (an unreadable off
// record counts as this version and build: off, never a loop).
inline Start decide(bool trying, const std::string* off, std::string_view kiln, std::string_view build) {
    if (trying) return Start::off_after_crash;
    if (!off) return Start::try_it;
    Record r;
    if (!parse_marker(*off, r)) return Start::off_recorded;
    return r.kiln == kiln && r.build == build ? Start::off_recorded : Start::try_it;
}

// The trial: called after every UI frame while the marker is written. Returns true once, when the marker may go.
struct Trial {
    uint64_t first_ms = 0;     // when a scrolled frame was first seen
    uint64_t last_frame = 0;   // the last UI frame that scrolled
    bool seen_long = false;    // scrolled frames covered kTrialMs
    bool done = false;
};

constexpr bool trial_step(Trial& t, uint64_t frame, uint64_t now_ms, bool scrolled) {
    if (t.done) return false;
    if (scrolled) {
        if (!t.first_ms) t.first_ms = now_ms ? now_ms : 1;
        t.last_frame = frame;
        if (now_ms - t.first_ms >= kTrialMs) t.seen_long = true;
        return false;
    }
    if (t.seen_long && t.last_frame && frame >= t.last_frame + kClosedFrames) {
        t.done = true;
        return true;
    }
    return false;
}

// ---- sanity of what the game's layout says (a frame's rectangle, UI units) ----
inline bool sane_rect(const float r[4]) {
    for (int i = 0; i < 4; ++i)
        if (!std::isfinite(r[i]) || std::fabs(r[i]) > 100000.0f) return false;
    return r[2] > 0.0f && r[3] > 0.0f;
}

}  // namespace kui::menu_scroll
