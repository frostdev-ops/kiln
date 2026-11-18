// SPDX-License-Identifier: MIT
// kiln.ui: the palettes of Kiln's looks (kiln_ui.h KILN_UI_ROLE_*, docs/kiln/ui.md "Looks and themes"). Pure: no ImGui, no
// game. style.cpp turns a palette into ImGui colours; the host test (tests/style_host_test.cpp) checks these rules.
//
// The game look's palette is made at run time from colours the game's UI bundle holds (keen::FbUiBundle, read through
// kiln.types and kiln.assets: style.cpp); none of the game's values are written here. Until they are read, and where they
// cannot be, the game look uses kNeutral: a dark grey stand-in with a soft gold accent, Kiln's own choice.
#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>

#include "services/kiln_ui.h"

namespace kui::look {

constexpr int kRoles = KILN_UI_ROLE_COUNT;
static_assert(kRoles <= 16, "KilnUiThemeDef.roles / KilnUiLookInfo.roles hold 16");

// One colour per KILN_UI_ROLE_*, 0xRRGGBBAA.
struct Palette {
    uint32_t c[16] = {};
    uint32_t& operator[](int r) { return c[r]; }
    uint32_t operator[](int r) const { return c[r]; }
};

inline uint8_t ch(uint32_t rgba, int shift) { return static_cast<uint8_t>((rgba >> shift) & 0xFF); }
inline uint32_t rgba(int r, int g, int b, int a) {
    auto c = [](int v) { return static_cast<uint32_t>(std::clamp(v, 0, 255)); };
    return c(r) << 24 | c(g) << 16 | c(b) << 8 | c(a);
}
inline uint32_t with_alpha(uint32_t c, uint8_t a) { return (c & 0xFFFFFF00u) | a; }
inline uint32_t with_alpha_f(uint32_t c, float a) {
    return with_alpha(c, static_cast<uint8_t>(std::clamp(std::lround(a * 255.0f), 0L, 255L)));
}
// Each of R, G, B times k (the alpha stays).
inline uint32_t scaled(uint32_t c, float kr, float kg, float kb) {
    return rgba(static_cast<int>(std::lround(ch(c, 24) * kr)), static_cast<int>(std::lround(ch(c, 16) * kg)),
                static_cast<int>(std::lround(ch(c, 8) * kb)), ch(c, 0));
}
inline uint32_t scaled(uint32_t c, float k) { return scaled(c, k, k, k); }
// a + (b - a) * t per channel (alpha too).
inline uint32_t mix(uint32_t a, uint32_t b, float t) {
    auto m = [&](int s) { return static_cast<int>(std::lround(ch(a, s) + (ch(b, s) - ch(a, s)) * t)); };
    return rgba(m(24), m(16), m(8), m(0));
}

// keen::UiColor.value (keen::Color, a uint32 packed A B G R from the high byte down) -> 0xRRGGBBAA.
inline uint32_t from_keen(uint32_t abgr) {
    return rgba(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF, (abgr >> 24) & 0xFF);
}

// The game's UI colours the game look is made of, in the order of kGameFields.
enum GameColor : int {
    kLabel = 0,       // menu.tabLabelHighlightColor: a chosen tab's label (text)
    kLabelIdle,       // menu.tabLabelIdleColor: an idle tab's label (muted text, with its alpha)
    kTile,            // menu.tileBackgroundColor: a menu tile (with its alpha)
    kWidget,          // menu.basicWidgetColor: a widget's ground (with its alpha)
    kTab,             // menu.tabHighlightColor: the tab highlight (border, softer accent)
    kOutline,         // menu.tileHighlightOutlineColor: a chosen tile's outline (accent)
    kRowHighlight,    // menu.textTabRowBgHighlightColor: a chosen row's ground (header)
    kAttention,       // globalColors.attentionColor
    kWarning,         // globalColors.warningColor
    kOk,              // globalColors.checkmarkGreen
    kGameColors,
};
constexpr int kRequiredGameColors = kRowHighlight + 1;  // the menu's; the global three fall back one by one

struct GameField {
    const char* path;  // below keen::FbUiBundle; each is a keen::UiColor whose `value` is read
};
inline constexpr GameField kGameFields[kGameColors] = {
    {"menu.tabLabelHighlightColor"},    {"menu.tabLabelIdleColor"},      {"menu.tileBackgroundColor"},
    {"menu.basicWidgetColor"},          {"menu.tabHighlightColor"},      {"menu.tileHighlightOutlineColor"},
    {"menu.textTabRowBgHighlightColor"}, {"globalColors.attentionColor"}, {"globalColors.warningColor"},
    {"globalColors.checkmarkGreen"},
};

// The windows' background opacity of the game and the neutral look, and the alpha of their pop-ups at the least.
constexpr float kGameWindowAlpha = 0.90f;
constexpr float kMinPopupAlpha = 0.85f;

// The neutral stand-in: the game look until (or unless) the game's colours are read. Greys with a soft gold.
inline Palette neutral() {
    Palette p;
    p[KILN_UI_ROLE_TEXT] = 0xebe8e3ffu;
    p[KILN_UI_ROLE_TEXT_MUTED] = 0xb9bec4abu;
    p[KILN_UI_ROLE_WINDOW_BG] = 0x1c1c21e6u;
    p[KILN_UI_ROLE_PANEL_BG] = 0x393841a9u;
    p[KILN_UI_ROLE_FRAME_BG] = 0x1414165au;
    p[KILN_UI_ROLE_BORDER] = 0xb8a26affu;
    p[KILN_UI_ROLE_ACCENT] = 0xdcc27affu;
    p[KILN_UI_ROLE_BUTTON] = 0x393841ffu;
    p[KILN_UI_ROLE_HEADER] = 0x6e5f3cffu;
    p[KILN_UI_ROLE_ATTENTION] = 0xf2d873ffu;
    p[KILN_UI_ROLE_WARNING] = 0xe57373ffu;
    p[KILN_UI_ROLE_OK] = 0x8fc47fffu;
    return p;
}

// Kiln's ember look's roles (branding/palette.json: charcoal bg, text, ember 300..700, gold 400). style.cpp draws the ember
// look itself with its own recipe; these are its colours for drawing (look_info) and what an ember-based theme starts from.
inline Palette ember() {
    Palette p;
    p[KILN_UI_ROLE_TEXT] = 0xf4ece6ffu;
    p[KILN_UI_ROLE_TEXT_MUTED] = 0x94857affu;
    p[KILN_UI_ROLE_WINDOW_BG] = 0x0f0d0cf0u;
    p[KILN_UI_ROLE_PANEL_BG] = 0x2a2421a9u;
    p[KILN_UI_ROLE_FRAME_BG] = 0x1f1b19ffu;
    p[KILN_UI_ROLE_BORDER] = 0xb83f05ffu;
    p[KILN_UI_ROLE_ACCENT] = 0xff6a1affu;
    p[KILN_UI_ROLE_BUTTON] = 0xb83f05d9u;
    p[KILN_UI_ROLE_HEADER] = 0xb83f05ffu;
    p[KILN_UI_ROLE_ATTENTION] = 0xf9c74fffu;
    p[KILN_UI_ROLE_WARNING] = 0xeb5046ffu;
    p[KILN_UI_ROLE_OK] = 0x6ed26effu;
    return p;
}

// The game look's palette from the colours read (0xRRGGBBAA each; `have` bit i: colour i was read). Without every one of
// the menu's colours it is the neutral stand-in; a missing global colour keeps the stand-in's.
inline Palette game(const uint32_t colors[kGameColors], uint32_t have, bool* used = nullptr) {
    Palette p = neutral();
    const bool ok = (have & ((1u << kRequiredGameColors) - 1)) == (1u << kRequiredGameColors) - 1;
    if (used) *used = ok;
    if (!ok) return p;
    // text: the label colour a touch warm (white -> #eeeae2), so long text is not glaring
    p[KILN_UI_ROLE_TEXT] = with_alpha(scaled(colors[kLabel], 0.933f, 0.918f, 0.886f), 0xFF);
    p[KILN_UI_ROLE_TEXT_MUTED] = colors[kLabelIdle];
    // the windows: the tile's hue, darker, at the game look's opacity
    p[KILN_UI_ROLE_WINDOW_BG] = with_alpha_f(scaled(colors[kTile], 0.48f), kGameWindowAlpha);
    p[KILN_UI_ROLE_PANEL_BG] = colors[kTile];
    p[KILN_UI_ROLE_FRAME_BG] = colors[kWidget];
    p[KILN_UI_ROLE_BORDER] = with_alpha(colors[kTab], 0xFF);
    p[KILN_UI_ROLE_ACCENT] = with_alpha(colors[kOutline], 0xFF);
    p[KILN_UI_ROLE_BUTTON] = with_alpha(colors[kTile], 0xFF);
    p[KILN_UI_ROLE_HEADER] = with_alpha(colors[kRowHighlight], 0xFF);
    if (have & (1u << kAttention)) p[KILN_UI_ROLE_ATTENTION] = with_alpha(colors[kAttention], 0xFF);
    if (have & (1u << kWarning)) p[KILN_UI_ROLE_WARNING] = with_alpha(colors[kWarning], 0xFF);
    if (have & (1u << kOk)) p[KILN_UI_ROLE_OK] = with_alpha(colors[kOk], 0xFF);
    return p;
}

// A theme's roles over its base's: the theme gives R, G and B; the alpha stays the base's (KilnUiThemeDef.roles).
inline Palette over(const Palette& base, const uint32_t roles[16], uint32_t mask) {
    Palette p = base;
    for (int r = 0; r < kRoles; ++r)
        if (mask & (1u << r)) p[r] = (roles[r] & 0xFFFFFF00u) | ch(base[r], 0);
    return p;
}

// Clamps of the player's setting (KilnUiLook).
inline float clamp_or(float v, float lo, float hi, float fallback) { return std::isfinite(v) ? std::clamp(v, lo, hi) : fallback; }
inline float clamp_background(float v) { return clamp_or(v, 0.1f, 1.0f, 0.55f); }
inline float clamp_opacity(float v) { return clamp_or(v, 0.3f, 1.0f, 1.0f); }
inline float clamp_scale(float v) { return clamp_or(v, 0.5f, 2.0f, 1.0f); }

// FNV-1a-32 (the game's type hashes: kiln.assets' resource types).
constexpr uint32_t fnv1a(const char* s) {
    uint32_t h = 0x811c9dc5u;
    for (; *s; ++s) h = (h ^ static_cast<uint8_t>(*s)) * 0x01000193u;
    return h;
}
static_assert(fnv1a("keen::UiFontResource") == 0x878aa5adu, "style.cpp's font resource hash");

}  // namespace kui::look
