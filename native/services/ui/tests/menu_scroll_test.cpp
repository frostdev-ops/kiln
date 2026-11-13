// SPDX-License-Identifier: MIT
// Host test of menu_scroll.h: the pairing of the scroll view's begin and end, when the pause menu is wrapped, the crash
// guard's marker and trial, and the rectangle sanity check.
#include <cmath>
#include <cstdio>
#include <limits>
#include <string>

#include "../menu_scroll.h"
#include "services/kiln_ui.h"

using namespace kui::menu_scroll;
namespace rows = kui::menu_rows;

static_assert(rows::show_auto == uint32_t{KILN_UI_MENU_SHOW_AUTO}, "menu_rows.h follows kiln_ui.h (auto)");

namespace {
int failures = 0, checks = 0;
void check(bool ok, const char* what) {
    ++checks;
    if (!ok) { ++failures; std::printf("FAIL %s\n", what); }
}
}  // namespace

int main() {
    // ---- pairing: begin only at the open site, end only at the close site, the one this call began ----
    {
        Pair p;
        check(at_open(p, false) == At::nothing && !p.open, "a menu that fits: nothing at the open site");
        check(at_close(p) == At::nothing, "and nothing at the close site");
        check(at_open(p, true) == At::begin && p.open, "wrapped: begin at the open site");
        check(at_close(p) == At::end && !p.open, "end at the close site");
        check(at_close(p) == At::nothing, "a second close ends nothing");
        // the close site did not run (something the static analysis missed): the next open says so, begins nothing
        check(at_open(p, true) == At::begin, "begin");
        check(at_open(p, true) == At::unbalanced && p.open, "an open without its close is unbalanced");
        check(at_close(p) == At::end, "the late close still ends the open begin");
        // many frames stay balanced
        int begun = 0, ended = 0;
        for (int i = 0; i < 1000; ++i) {
            if (at_open(p, i % 3 != 0) == At::begin) ++begun;
            if (at_close(p) == At::end) ++ended;
        }
        check(begun == ended && begun == 666 && !p.open, "1000 frames: every begin ended once");
    }

    // ---- when the pause menu is wrapped ----
    check(!wants(rows::show_auto, 3) && wants(rows::show_auto, 4), "auto: wrapped past kFew mods' rows");
    check(wants(rows::show_always, 12) && !wants(rows::show_always, 0), "always: wrapped past kFew");
    check(!wants(rows::show_few, 12) && !wants(rows::show_never, 12), "few and never draw few rows: never wrapped");
    check(rows::draws_mods(rows::show_auto, 12, true) && !rows::draws_mods(rows::show_auto, 4, false) &&
              rows::draws_mods(rows::show_auto, 3, false),
          "auto draws every row where the menu scrolls, else the rule few");

    // ---- the crash guard ----
    {
        const std::string m = marker_text("0.2.0-dev.27", "5d9ef2f1");
        Record r;
        check(parse_marker(m, r) && r.kiln == "0.2.0-dev.27" && r.build == "5d9ef2f1", "a marker reads back");
        check(parse_marker("kiln=a\r\nbuild=b\r\n", r) && r.kiln == "a" && r.build == "b", "CRLF lines read");
        check(!parse_marker("", r) && !parse_marker("kiln=a\n", r) && !parse_marker("kiln=\nbuild=b\n", r),
              "an incomplete marker does not read");
        check(decide(false, nullptr, "v", "b") == Start::try_it, "no file: try");
        check(decide(true, nullptr, "v", "b") == Start::off_after_crash, "a trying marker: the game ended while trying");
        check(decide(true, &m, "0.2.0-dev.27", "5d9ef2f1") == Start::off_after_crash, "trying wins over an off record");
        check(decide(false, &m, "0.2.0-dev.27", "5d9ef2f1") == Start::off_recorded, "off: same version and build");
        check(decide(false, &m, "0.2.0-dev.28", "5d9ef2f1") == Start::try_it, "another Kiln version tries again");
        check(decide(false, &m, "0.2.0-dev.27", "0123") == Start::try_it, "another game build tries again");
        const std::string junk = "garbage";
        check(decide(false, &junk, "v", "b") == Start::off_recorded, "an unreadable off record stays off (never a loop)");
    }
    {
        Trial t;
        check(!trial_step(t, 1, 0, false), "nothing scrolled: no end of trial");
        uint64_t f = 10, ms = 1000;
        for (; ms < 4100; ms += 16, ++f) check(!trial_step(t, f, ms, true), "scrolling: the trial goes on");
        check(t.seen_long, "3 s of scrolled frames seen");
        bool passed = false;
        uint64_t closed_at = 0;
        for (uint64_t k = 0; k < 40; ++k)
            if (trial_step(t, f + k, ms + k * 16, false) && !passed) passed = true, closed_at = k;
        check(passed && closed_at + 1 == kClosedFrames, "closed for 30 frames after 3 s: the marker goes on the 30th, once");
        check(!trial_step(t, f + 100, ms + 2000, false), "only once");
        Trial s;  // a short look: the menu closed after one second, the marker stays
        for (uint64_t k = 0; k < 60; ++k) trial_step(s, k + 1, 1000 + k * 16, true);
        bool early = false;
        for (uint64_t k = 0; k < 100; ++k) early |= trial_step(s, 61 + k, 2000 + k * 16, false);
        check(!early && !s.done, "a menu closed before 3 s keeps the marker");
    }

    // ---- rectangles ----
    {
        const float ok[4] = {660, 120, 300, 840}, zero_h[4] = {0, 0, 300, 0};
        const float nan[4] = {0, std::numeric_limits<float>::quiet_NaN(), 300, 840}, huge[4] = {0, 0, 300, 1e9f};
        check(sane_rect(ok) && !sane_rect(zero_h) && !sane_rect(nan) && !sane_rect(huge), "rectangle sanity");
    }

    std::printf("menu_scroll_test: %d/%d checks passed\n", checks - failures, checks);
    return failures ? 1 : 0;
}
