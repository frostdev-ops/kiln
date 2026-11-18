// SPDX-License-Identifier: MIT
// kiln.ui: how many mod fonts can exist (font_load, kiln_ui.h 0.2.0-dev.29.16). Pure: the host test
// (tests/style_host_test.cpp) checks the rules.
//
// Every font shares Dear ImGui's one font atlas, whose glyphs are made on demand at the sizes drawn (ImGui 1.92+), so a font
// takes no overlay image and no descriptor set of its own (the atlas's textures are in image_budget.h's reserve). What a
// font costs is its file's bytes, kept while it lives (the atlas reads glyphs from them), and the atlas space of the glyphs
// drawn. So the budget counts fonts and bytes.
#pragma once
#include <cstddef>
#include <cstdint>
#include <cstring>

namespace kui::fonts {

constexpr size_t kMaxFonts = 8;                    // mod fonts at once (every owner; freed ones count until removed)
constexpr size_t kMaxFontBytes = 8u << 20;         // one file
constexpr size_t kMaxTotalBytes = 24u << 20;       // all of them

enum Refusal : int {
    kOk = 0,
    kNotAFont = 1,   // its first four bytes are not a TrueType / OpenType tag
    kTooLarge = 2,   // over kMaxFontBytes
    kTooMany = 3,    // kMaxFonts exist
    kTooMuch = 4,    // the bytes would pass kMaxTotalBytes
};

// A TrueType (0x00010000, "true"), OpenType CFF ("OTTO") font. Collections ("ttcf") are refused: ImGui would take the first
// face only, which is rarely what a mod means.
inline bool is_font(const void* data, size_t len) {
    if (!data || len < 12) return false;
    const auto* b = static_cast<const uint8_t*>(data);
    static const uint8_t ttf[4] = {0, 1, 0, 0};
    return std::memcmp(b, ttf, 4) == 0 || std::memcmp(b, "OTTO", 4) == 0 || std::memcmp(b, "true", 4) == 0;
}

// Whether one more font of `len` bytes may load while `count` fonts of `bytes` bytes together exist.
inline Refusal admit(const void* data, size_t len, size_t count, size_t bytes) {
    if (!is_font(data, len)) return kNotAFont;
    if (len > kMaxFontBytes) return kTooLarge;
    if (count >= kMaxFonts) return kTooMany;
    if (bytes + len > kMaxTotalBytes) return kTooMuch;
    return kOk;
}

// The game's own font of a Chinese, Japanese or Korean language group (font_group.h), read through kiln.assets when Kiln's
// language or the game's is one of them and kept for the process while that group is in use. They are no mod fonts and do not
// count against the limits above. Client 26430625's groups: zh-CN 18.6 MB (Noto Serif SC 11.2 MB + KR 7.4 MB), ja-JP 13.5 MB,
// zh-TW 13.2 MB, ko-KR 7.4 MB. One group at a time: another language's group replaces it.
constexpr size_t kMaxGroupFontBytes = 16u << 20;   // one file (the game's largest CJK file: 11.7 MB)
constexpr size_t kMaxGroupBytes = 24u << 20;       // a group's files together
constexpr size_t kMaxGroupFonts = 4;               // files of a group (each is merged into the overlay's fonts once)

// Which of a group's files (sizes in the group's order) load: each up to kMaxGroupFontBytes, at most kMaxGroupFonts, and in
// order while the total stays within kMaxGroupBytes (a file past it is left out; so are the ones after it). The count.
inline size_t admit_group(const size_t* sizes, size_t n) {
    size_t total = 0, k = 0;
    for (; k < n && k < kMaxGroupFonts; ++k) {
        if (sizes[k] < 1024 || sizes[k] > kMaxGroupFontBytes || total + sizes[k] > kMaxGroupBytes) break;
        total += sizes[k];
    }
    return k;
}

inline const char* refusal_text(Refusal r) {
    switch (r) {
        case kNotAFont: return "not a TrueType or OpenType font";
        case kTooLarge: return "larger than 8 MiB";
        case kTooMany: return "8 mod fonts exist already";
        case kTooMuch: return "the mod fonts would pass 24 MiB together";
        default: return "ok";
    }
}

}  // namespace kui::fonts
