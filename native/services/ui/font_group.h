// SPDX-License-Identifier: MIT
// kiln.ui: which of the game's own fonts gives Kiln's overlay the glyphs of a Chinese, Japanese or Korean text
// (docs/kiln/translating-game.md "Chinese, Japanese and Korean"). Plain logic over the bytes of the game's
// keen::UiFontResource: no Windows, no game, no ImGui, so tests/font_group_test.cpp runs it with made-up data.
//
// The game's keen::UiFontResource (one resource) holds `fonts`, a BlobArray of keen::TtfFontResource (a content blob each:
// 28 TTF/OTF files on client 26430625), and `languageGroups`, a BlobArray of keen::UiFontLanguageGroupResource: a language
// mask (keen::UiFontLanguageMask, bit n = keen::LanguageId n), its glyph sets (keen::UiGlyphSetResource: a font index, a
// scale, rune ranges keen::UiFontRange {first, last}, a variant mask: bit 0 Bold, bit 1 Italic) and whether it is the
// default group. The game draws a language with the group whose mask has it and that is not the default (zh-CN: Noto Serif
// SC for runes 0..0xABFF, Noto Serif KR for 0xAC00..0xFFFF; ja-JP: Noto Serif JP Light and KR; ko-KR: KR; zh-TW: TC and KR),
// every other language with the default group (Harmonique Display, with CJK fallbacks).
//
// Kiln takes the regular sets (no variant) of that group, in the group's order, each limited to its own ranges (ImGui's
// GlyphExcludeRanges: the complement), and merges them into its fonts after their Latin source: Latin keeps the overlay's
// font, everything it lacks comes from the game's font for that language, as the game routes it.
//
// Offsets: the fields' places come from the game's reflection when kiln.types has them (layout_from_types in style.cpp),
// else the layout below, read from client 26430625's reflection and checked against its UiFontResource offline
// (2026-11-17: 28 fonts, 6 groups; group 24 bytes, glyph set 20, range 8; the masks 0x100000 = zh-CN only, 0x800 = ja-JP,
// 0x2000 = ko-KR, 0x200000 = zh-TW).
#pragma once

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <string>
#include <string_view>
#include <vector>

namespace kui::fontgroup {

// keen::LanguageId (reflected enum; the bit of a group's language mask)
enum : uint32_t { kNone = 0, kJaJp = 11, kKoKr = 13, kZhCn = 20, kZhTw = 21 };

inline bool cjk(uint32_t id) { return id == kJaJp || id == kKoKr || id == kZhCn || id == kZhTw; }

// The keen::LanguageId of a language tag ("zh-cn", "zh", "zh-hans", "ja", "ko-kr"; case and '_' do not matter) when it is
// one of the CJK languages, else kNone. Traditional Chinese: zh-tw, zh-hk, zh-mo, zh-hant.
inline uint32_t language_id(std::string_view tag) {
    std::string t;
    for (char c : tag) t += c == '_' ? '-' : static_cast<char>(c >= 'A' && c <= 'Z' ? c + 32 : c);
    while (!t.empty() && t.back() == ' ') t.pop_back();
    while (!t.empty() && t.front() == ' ') t.erase(0, 1);
    auto starts = [&](const char* p) { return t.rfind(p, 0) == 0; };
    if (t == "ja" || starts("ja-")) return kJaJp;
    if (t == "ko" || starts("ko-")) return kKoKr;
    if (t == "zh-tw" || t == "zh-hk" || t == "zh-mo" || starts("zh-hant")) return kZhTw;
    if (t == "zh" || starts("zh-")) return kZhCn;
    return kNone;
}

// Whose glyphs the overlay needs: Kiln's language when it is CJK (the player forced it, or it follows the game), else the
// game's text language (mods' texts follow it), else none.
inline uint32_t wanted(std::string_view kiln_language, uint32_t game_language) {
    if (const uint32_t k = language_id(kiln_language); k != kNone) return k;
    return cjk(game_language) ? game_language : kNone;
}

// Field places (bytes). The defaults are client 26430625's.
struct Layout {
    uint32_t res_fonts = 0x0, res_groups = 0x8;       // keen::UiFontResource: fonts, languageGroups (BlobArray, 8 bytes)
    uint32_t font_size = 32;                          // keen::TtfFontResource (fontFileData: a 16-byte content hash first)
    uint32_t group_size = 24, group_mask = 0x0, group_sets = 0x8, group_default = 0x14;
    uint32_t set_size = 20, set_font = 0x0, set_ranges = 0x8, set_variant = 0x10;
    uint32_t range_size = 8, range_first = 0x0, range_last = 0x4;
};

struct Range {
    uint32_t first, last;
};

struct Set {
    uint32_t font;               // index into the resource's fonts
    std::vector<Range> ranges;   // the runes the game takes from it
};

struct Pick {
    int group = -1;              // the group's index, -1 none
    uint32_t font_count = 0;     // fonts in the resource
    std::vector<Set> sets;       // the group's regular sets, each font once, in the group's order
    std::string why;             // when none: why (for kiln.log)
};

// A BlobArray: an offset relative to the field itself, then a count.
inline bool blob_array(const uint8_t* res, size_t len, size_t at, size_t elem, size_t* base, uint32_t* count) {
    if (at + 8 > len) return false;
    int32_t off = 0;
    uint32_t n = 0;
    std::memcpy(&off, res + at, 4);
    std::memcpy(&n, res + at + 4, 4);
    const long long b = static_cast<long long>(at) + off;
    if (n == 0) {
        *base = 0;
        *count = 0;
        return true;
    }
    if (b < 0 || n > 4096 || static_cast<unsigned long long>(b) + static_cast<unsigned long long>(n) * elem > len) return false;
    *base = static_cast<size_t>(b);
    *count = n;
    return true;
}

// The sets of `language`'s own group (see above). `res`: the UiFontResource's bytes.
inline Pick pick(const uint8_t* res, size_t len, uint32_t language, const Layout& l = {}) {
    Pick p;
    if (!cjk(language)) {
        p.why = "not a Chinese, Japanese or Korean language";
        return p;
    }
    size_t fbase = 0, gbase = 0;
    uint32_t fonts = 0, groups = 0;
    if (!blob_array(res, len, l.res_fonts, l.font_size, &fbase, &fonts) || fonts == 0) {
        p.why = "no fonts in the UiFontResource";
        return p;
    }
    if (!blob_array(res, len, l.res_groups, l.group_size, &gbase, &groups) || groups == 0) {
        p.why = "no language groups in the UiFontResource";
        return p;
    }
    p.font_count = fonts;
    for (uint32_t g = 0; g < groups; ++g) {
        const size_t at = gbase + static_cast<size_t>(g) * l.group_size;
        uint64_t mask = 0;
        std::memcpy(&mask, res + at + l.group_mask, 8);
        if (res[at + l.group_default] != 0 || language >= 64 || !(mask >> language & 1)) continue;
        size_t sbase = 0;
        uint32_t sets = 0;
        if (!blob_array(res, len, at + l.group_sets, l.set_size, &sbase, &sets)) {
            p.why = "a language group's glyph sets are out of the resource";
            return p;
        }
        for (uint32_t s = 0; s < sets; ++s) {
            const size_t sat = sbase + static_cast<size_t>(s) * l.set_size;
            if (res[sat + l.set_variant] != 0) continue;  // bold or italic
            Set set;
            std::memcpy(&set.font, res + sat + l.set_font, 4);
            if (set.font >= fonts) continue;
            bool seen = false;
            for (const Set& x : p.sets) seen = seen || x.font == set.font;
            if (seen) continue;
            size_t rbase = 0;
            uint32_t ranges = 0;
            if (!blob_array(res, len, sat + l.set_ranges, l.range_size, &rbase, &ranges)) continue;
            for (uint32_t r = 0; r < ranges && r < 64; ++r) {
                Range x{};
                std::memcpy(&x.first, res + rbase + static_cast<size_t>(r) * l.range_size + l.range_first, 4);
                std::memcpy(&x.last, res + rbase + static_cast<size_t>(r) * l.range_size + l.range_last, 4);
                if (x.first <= x.last) set.ranges.push_back(x);
            }
            if (!set.ranges.empty()) p.sets.push_back(std::move(set));
        }
        p.group = static_cast<int>(g);
        if (p.sets.empty()) p.why = "the language's group has no regular glyph set";
        return p;
    }
    p.why = "the game has no font group of its own for this language";
    return p;
}

// ImGui's GlyphExcludeRanges for a set: every rune of 1..0xFFFF (ImGui's 16-bit ImWchar) outside its ranges, as pairs,
// zero-terminated. Empty when the set takes every rune, or when it would need more pairs than ImGui allows (32): then the set
// takes every rune it has.
inline std::vector<uint16_t> exclude_ranges(const std::vector<Range>& ranges) {
    std::vector<Range> in;
    for (Range r : ranges) {
        if (r.first > 0xFFFF) continue;
        r.first = r.first < 1 ? 1 : r.first;
        r.last = r.last > 0xFFFF ? 0xFFFF : r.last;
        if (r.first > r.last) continue;
        size_t i = 0;
        while (i < in.size() && in[i].first < r.first) ++i;
        in.insert(in.begin() + static_cast<std::ptrdiff_t>(i), r);
    }
    std::vector<uint16_t> out;
    uint32_t next = 1;  // the first rune not yet covered
    for (const Range& r : in) {
        if (r.first > next) {
            out.push_back(static_cast<uint16_t>(next));
            out.push_back(static_cast<uint16_t>(r.first - 1));
        }
        if (r.last + 1 > next) next = r.last + 1;
    }
    if (next <= 0xFFFF) {
        out.push_back(static_cast<uint16_t>(next));
        out.push_back(0xFFFF);
    }
    if (out.empty() || out.size() > 64) return {};  // nothing to leave out, or too many pairs for ImGui
    out.push_back(0);
    return out;
}

}  // namespace kui::fontgroup
