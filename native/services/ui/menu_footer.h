// SPDX-License-Identifier: MIT
// kiln.ui: Kiln's line in the main menu's footer (menu_footer.cpp; docs/kiln/ui.md "Kiln's line in the main menu's
// footer", docs/re/game-ui.md "The main menu's footer"). Plain logic: no Windows, no game memory, so
// tests/menu_footer_test.cpp runs it on the build machine.
//
// The main menu ends with the game's footer (its window "LegalLine": the build number and the legal line, bottom
// aligned). Kiln's line goes right above the build number, where XHL-Core puts its "All mods loaded" line, and takes that
// line's place: Kiln runs its own copy of the footer function (code_clone.h), so a footer line another module patched
// into the game's code is not drawn while Kiln's is.
//
// The line: "Kiln <version> - <N> Mods Loaded" ("1 Mod Loaded"), the version without a "+build" suffix. With the overlay
// drawing, it starts with a gap of no-break spaces where the overlay draws Kiln's mark (the logo is a picture; the game's
// text cannot show one), sized from the line's own rectangle in the game's layout.
#pragma once

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <string>
#include <string_view>

#include "ui_text.h"

namespace menu_footer {

// The version a player sees: the release version, without build metadata ("0.2.0-dev.29.11+20261116.ab12" ->
// "0.2.0-dev.29.11"). Empty stays empty.
inline std::string release_version(std::string_view v) {
    const size_t plus = v.find('+');
    std::string_view s = plus == std::string_view::npos ? v : v.substr(0, plus);
    while (!s.empty() && (s.back() == ' ' || s.back() == '\n' || s.back() == '\r')) s.remove_suffix(1);
    return std::string(s);
}

// The game font's metrics (Harmonique Display Regular, font 0 of the game's keen::UiFontResource, the default
// language group's regular face; measured from the font file with fontTools on 2026-11-16): units per em 1000, ascent
// 980, descent 220 (a line is 1.2 em), the space and the no-break space both advance 191 units.
constexpr float kLineEm = 1.2f;
constexpr float kSpaceEm = 0.191f;
// The gap for the mark: 7 no-break spaces (U+00A0) = 1.337 em, the mark is 1 em square, 0.337 em to the text.
constexpr int kGapSpaces = 7;
constexpr float kMarkEm = 1.0f;
constexpr char kNoBreakSpace[] = "\xC2\xA0";

// The line's text. `mods` < 0: not known yet (no line). `gap`: start with the mark's gap. `tmpl`: the text in the language
// of Kiln's texts (native.footer.loaded.one / .other, ui_text.h), with {version} and {count}; empty: English.
inline std::string line_text(std::string_view version, int64_t mods, bool gap, std::string_view tmpl = {}) {
    if (mods < 0) return {};
    std::string out;
    if (gap)
        for (int i = 0; i < kGapSpaces; ++i) out += kNoBreakSpace;
    const std::string v = release_version(version);
    const std::string english = kui::text::english(mods == 1 ? "native.footer.loaded.one" : "native.footer.loaded.other");
    const std::string count = std::to_string(static_cast<long long>(mods));
    std::string line = kui::text::fill(tmpl.empty() ? std::string_view(english) : tmpl, {{"version", v}, {"count", count}});
    if (v.empty()) {  // no version (a build without one): no double space where it would be
        size_t at;
        while ((at = line.find("  ")) != std::string::npos) line.erase(at, 1);
        while (!line.empty() && line.front() == ' ') line.erase(0, 1);
    }
    return out + line;
}

// A rectangle of the game's layout (UI units: x, y, w, h) that can be a line of text.
inline bool plausible_line(const float r[4]) {
    for (int i = 0; i < 4; ++i)
        if (!std::isfinite(r[i]) || std::fabs(r[i]) > 100000.0f) return false;
    return r[2] > 1.0f && r[3] > 1.0f && r[3] < 400.0f;
}

// Where the overlay draws the mark, in pixels: a square of kMarkEm, at the line's left edge (where the gap is),
// centred on the line. The canvas's origin is the viewport's (vx, vy); `ppu` pixels per UI unit. False: no mark.
struct Box {
    float x0 = 0, y0 = 0, x1 = 0, y1 = 0;
};
inline bool mark_box(const float r[4], float vx, float vy, float ppu, Box* out) {
    if (!plausible_line(r) || !std::isfinite(ppu) || ppu <= 0.0f || !std::isfinite(vx) || !std::isfinite(vy)) return false;
    const float em = r[3] / kLineEm;
    const float side = em * kMarkEm;
    const float cy = r[1] + r[3] * 0.5f;
    out->x0 = vx + r[0] * ppu;
    out->y0 = vy + (cy - side * 0.5f) * ppu;
    out->x1 = out->x0 + side * ppu;
    out->y1 = out->y0 + side * ppu;
    return out->x1 - out->x0 >= 2.0f;
}

// Whether a drawn line is recent enough to draw its mark: the UI frame it was drawn in against the UI frames finished
// since (the render thread lags the UI thread by up to a frame; a page that no longer draws the footer loses the mark
// after two frames).
inline bool recent(uint64_t drawn_frame, uint64_t frames_now) {
    return drawn_frame != 0 && frames_now <= drawn_frame + 2;
}

// What the footer hook does in a frame.
enum class Take {
    original,  // the game's function (with any other module's line in it): Kiln hidden, not ready, or off
    copy,      // Kiln's copy: the game's lines and Kiln's, nobody else's
};
// `ready`: the copy exists and passed its check. `shown`: the player's setting. `mods`: the count, < 0 unknown.
// `failed`: Kiln's line faulted earlier in this process.
inline Take take(bool ready, bool shown, int64_t mods, bool failed) {
    return ready && shown && mods >= 0 && !failed ? Take::copy : Take::original;
}

}  // namespace menu_footer
