// SPDX-License-Identifier: MIT
// Host test of menu_scroll.h: the pairing of the scroll view's begin and end, when the pause menu is wrapped, the crash
// guard's marker and trial, and the rectangle sanity check.
#include <cmath>
#include <cstring>
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

    // review of 235bcebb: one decision per builder call. The open site declined (3 mods' rows); a fourth entry is added
    // before the Settings entry is drawn: the rows drawn follow this call's decision (not wrapped: the rule few, none of
    // the four), never every entry into an unwrapped menu
    {
        Pair p;
        check(at_open(p, wants(rows::show_auto, 3)) == At::nothing, "three rows: not wrapped");
        check(!rows::draws_mods(rows::show_auto, 4, p.open), "a fourth row in the same call: the rule few, not all");
        at_close(p);
        check(at_open(p, wants(rows::show_auto, 4)) == At::begin && rows::draws_mods(rows::show_auto, 4, p.open),
              "the next call wraps and draws all four");
        at_close(p);
    }

    // ---- the crash guard ----
    {
        const std::string m = marker_text("0.2.0-dev.27", "5d9ef2f1");
        Record r;
        check(parse_marker(m, r) && r.kiln == "0.2.0-dev.27" && r.build == "5d9ef2f1", "a marker reads back");
        check(parse_marker("kiln=a\r\nbuild=b\r\n", r) && r.kiln == "a" && r.build == "b", "CRLF lines read");
        check(!parse_marker("", r) && !parse_marker("kiln=a\n", r) && !parse_marker("kiln=\nbuild=b\n", r),
              "an incomplete marker does not read");
        const std::string none;
        check(decide(File::absent, File::absent, none, "v", "b") == Start::try_it, "no file: try");
        check(decide(File::read, File::absent, none, "v", "b") == Start::off_after_crash, "a trying marker: the game ended while trying");
        check(decide(File::read, File::read, m, "0.2.0-dev.27", "5d9ef2f1") == Start::off_after_crash, "trying wins over an off record");
        check(decide(File::read, File::read, m, "0.2.0-dev.28", "5d9ef2f1") == Start::off_after_crash,
              "a trying marker and an old off record (an earlier failed conversion): still off");
        check(decide(File::absent, File::read, m, "0.2.0-dev.27", "5d9ef2f1") == Start::off_recorded, "off: same version and build");
        check(decide(File::absent, File::read, m, "0.2.0-dev.28", "5d9ef2f1") == Start::try_it, "another Kiln version tries again");
        check(decide(File::absent, File::read, m, "0.2.0-dev.27", "0123") == Start::try_it, "another game build tries again");
        const std::string junk = "garbage";
        check(decide(File::absent, File::read, junk, "v", "b") == Start::off_recorded, "an unparsable off record stays off (never a loop)");
        // review of 235bcebb: a marker that is there but cannot be read is not "absent"
        check(decide(File::unreadable, File::absent, none, "v", "b") == Start::off_after_crash, "an unreadable trying marker: off");
        check(decide(File::absent, File::unreadable, none, "v", "b") == Start::off_recorded, "an unreadable off record: off");
        // review of 235bcebb: the trying marker goes only once the off record is on the disk
        check(!may_delete_trying(true, false), "moved: nothing left to delete");
        check(may_delete_trying(false, true), "written: the trying marker may go");
        check(!may_delete_trying(false, false), "neither: the trying marker stays (the evidence is kept)");
    }
    {
        // review of 235bcebb: the trial counts wrapped builder calls that reached their close site, nothing else
        Trial t;
        bool passed = false;
        uint32_t at = 0;
        for (uint32_t k = 1; k <= 100; ++k)
            if (trial_step(t)) {
                check(!passed, "the trial passes once");
                passed = true, at = k;
            }
        check(passed && at == kTrialFrames, "the marker goes on the 48th wrapped frame");
        check(kTrialFrames >= 24 && kTrialFrames <= 120, "a few dozen frames");
        Trial s;
        for (uint32_t k = 1; k < kTrialFrames; ++k) check(!trial_step(s), "fewer wrapped frames keep the marker");
    }
    {
        // review of 235bcebb: after a begin, every way out closes what was opened, innermost first
        Close out[2];
        Nest none, layout_only{true, false}, both{true, true};
        check(unwind_order(none, out) == 0, "nothing begun: nothing to close");
        check(unwind_order(layout_only, out) == 1 && out[0] == Close::layout, "only the view begun: end it");
        check(unwind_order(both, out) == 2 && out[0] == Close::inner && out[1] == Close::layout,
              "view and stack: the stack first, then the view");
    }

    // ---- the parameter block: the scrollbar's look and the focus (owner's first try, 2026-11-13) ----
    {
        uint8_t params[kParamsSize] = {}, bar[kScrollBarSize] = {}, cfg[0x10000] = {};
        for (size_t i = 0; i < sizeof cfg; ++i) cfg[i] = static_cast<uint8_t>(i * 7 + 3);
        params[kHotKeyMask] = 1;  // the game's default
        style_params(params, bar, cfg, 0xfa84, 0xfac4, 0xfb04);
        check(std::memcmp(params + kScrollBar + kBarFromCfgA, cfg + 0xfa84, 32) == 0, "bar +0x44: 32 bytes of the config at +0xfa84");
        check(std::memcmp(params + kScrollBar + kBarFromCfgB, cfg + 0xfac4, 32) == 0, "bar +0x24: 32 bytes at +0xfac4");
        check(std::memcmp(params + kScrollBar + kBarFromCfgC, cfg + 0xfb04, 8) == 0, "bar +0x4: 8 bytes at +0xfb04");
        float sizes[4];
        std::memcpy(sizes, params + kScrollBar + kBarSizes, sizeof sizes);
        check(sizes[0] == 24 && sizes[1] == 24 && sizes[2] == 24 && sizes[3] == 20, "bar sizes 24, 24, 24, 20");
        check(params[kScrollBar + kBarFlagA] == 1 && params[kScrollBar + kBarFlagB] == 1, "bar flags +0x7c and +0x90");
        check(params[kUseGlobalFocus] == 1, "the view follows the global focus");
        check(params[kHotKeyMask] == (1 | kHotKeys), "the scroll keys added to the default mask");
        check(kScrollBar + kScrollBarSize == kParamsSize, "the bar block ends the parameter block");
    }
    // ---- the view's height ----
    check(view_too_short(1020, 311, 1522), "the owner's first try (a view a third of its column) is caught");
    check(!view_too_short(1020, 933, 1522), "a view of nearly the column's height passes");
    check(!view_too_short(1020, 311, 300), "entries that fit: no complaint");

    // ---- rectangles ----
    {
        const float ok[4] = {660, 120, 300, 840}, zero_h[4] = {0, 0, 300, 0};
        const float nan[4] = {0, std::numeric_limits<float>::quiet_NaN(), 300, 840}, huge[4] = {0, 0, 300, 1e9f};
        check(sane_rect(ok) && !sane_rect(zero_h) && !sane_rect(nan) && !sane_rect(huge), "rectangle sanity");
    }

    std::printf("menu_scroll_test: %d/%d checks passed\n", checks - failures, checks);
    return failures ? 1 : 0;
}
