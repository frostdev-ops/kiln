// SPDX-License-Identifier: MIT
// kiln.ui: which of the game's own fonts give Kiln's overlay the glyphs its Latin font lacks: Cyrillic (and the other scripts
// of the game's default language group) for every language, a Chinese, Japanese or Korean language's own group on top
// (docs/kiln/translating-game.md "Cyrillic and the other scripts of the game's default font group" and "Chinese, Japanese
// and Korean"). Plain logic over the bytes of the game's keen::UiFontResource: no Windows, no game, no ImGui, so
// tests/font_group_test.cpp runs it with made-up data.
//
// The game's keen::UiFontResource (one resource) holds `fonts`, a BlobArray of keen::TtfFontResource (a content blob each:
// 28 TTF/OTF files on client 26430625), and `languageGroups`, a BlobArray of keen::UiFontLanguageGroupResource: a language
// mask (keen::UiFontLanguageMask, bit n = keen::LanguageId n), its glyph sets (keen::UiGlyphSetResource: a font index, a
// scale, rune ranges keen::UiFontRange {first, last}, a variant mask: bit 0 Bold, bit 1 Italic) and whether it is the
// default group. The game draws a language with the group whose mask has it and that is not the default (zh-CN: Noto Serif
// SC for runes 0..0xABFF, Noto Serif KR for 0xAC00..0xFFFF; ja-JP: Noto Serif JP Light and KR; ko-KR: KR; zh-TW: TC and KR;
// ru-RU, bg-BG, uk-UA: Noto Serif, KR, SC), every other language with the default group: Harmonique Display (the overlay's
// game font: the Latin source) for 0..0x3FF, 0x500..0xDFF, 0xE80..0x2E7F, then Noto Serif SC 0x2E80..0xABFF, Noto Serif
// 0x400..0x4FF (Cyrillic), U+20A9 and U+2116, Noto Serif KR 0xAC00..0xFFFF, Noto Sans Thai 0xE00..0xE7F.
//
// Kiln takes the regular sets (no variant) of a group, in the group's order, each limited to its own ranges (ImGui's
// GlyphExcludeRanges: the complement), and merges them into its fonts after their Latin source: Latin keeps the overlay's
// font, everything it lacks comes from the game's fonts, as the game routes them. Two layers:
//  - the default group's sets other than its Latin source (pick_default), for every language, limited to small files
//    (font_budget.h admit_default: Noto Serif and Thai, 577 KB; the CJK fallbacks are left to the CJK groups). A Cyrillic
//    language draws Harmonique's Latin and Noto Serif's Cyrillic: the game itself uses Noto Serif for Latin in those
//    languages (their own group), Kiln keeps one Latin look across languages, as it does for CJK.
//  - a Chinese, Japanese or Korean language's own group (pick), after the first layer, while that language is in use.
//
// Offsets: the fields' places come from the game's reflection when kiln.types has them (layout_from_types in style.cpp),
// else the layout below, read from client 26430625's reflection and checked against its UiFontResource offline
// (2026-11-17: 28 fonts, 6 groups; group 24 bytes, glyph set 20, range 8; the masks 0x100000 = zh-CN only, 0x800 = ja-JP,
// 0x2000 = ko-KR, 0x200000 = zh-TW, 0x401000200 = ru-RU, bg-BG, uk-UA; 0x6fef7abfe the default group).
#pragma once

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <string>
#include <string_view>
#include <vector>

namespace kui::fontgroup {

// keen::LanguageId (reflected enum; the bit of a group's language mask)
enum : uint32_t { kNone = 0, kRuRu = 9, kJaJp = 11, kKoKr = 13, kZhCn = 20, kZhTw = 21, kBgBg = 24, kUkUa = 34 };

inline bool cjk(uint32_t id) { return id == kJaJp || id == kKoKr || id == kZhCn || id == kZhTw; }
inline bool cyrillic(uint32_t id) { return id == kRuRu || id == kBgBg || id == kUkUa; }

// The keen::LanguageId of a language tag ("zh-cn", "zh", "zh-hans", "ja", "ko-kr", "ru"; case and '_' do not matter) when
// its script is one the overlay's Latin fonts lack, else kNone. Traditional Chinese: zh-tw, zh-hk, zh-mo, zh-hant.
// Cyrillic: ru 9, bg 24, uk 34; the Cyrillic languages the game has no id for (be, mk, sr but sr-latn, kk, ky, mn, tg)
// count as ru-RU (9): the same glyphs.
inline uint32_t language_id(std::string_view tag) {
    std::string t;
    for (char c : tag) t += c == '_' ? '-' : static_cast<char>(c >= 'A' && c <= 'Z' ? c + 32 : c);
    while (!t.empty() && t.back() == ' ') t.pop_back();
    while (!t.empty() && t.front() == ' ') t.erase(0, 1);
    auto is = [&](const char* p) { return t == p || (t.rfind(p, 0) == 0 && t.size() > std::strlen(p) && t[std::strlen(p)] == '-'); };
    if (is("ja")) return kJaJp;
    if (is("ko")) return kKoKr;
    if (t == "zh-tw" || t == "zh-hk" || t == "zh-mo" || t.rfind("zh-hant", 0) == 0) return kZhTw;
    if (is("zh")) return kZhCn;
    if (is("ru")) return kRuRu;
    if (is("uk")) return kUkUa;
    if (is("bg")) return kBgBg;
    if (is("sr")) return t.rfind("sr-latn", 0) == 0 ? kNone : kRuRu;
    for (const char* c : {"be", "mk", "kk", "ky", "mn", "tg"})
        if (is(c)) return kRuRu;
    return kNone;
}

// Whose Chinese, Japanese or Korean group the overlay loads: Kiln's language when it is CJK (the player forced it, or it
// follows the game), else the game's text language when it is (mods' texts follow it), else none.
inline uint32_t wanted(std::string_view kiln_language, uint32_t game_language) {
    if (const uint32_t k = language_id(kiln_language); cjk(k)) return k;
    return cjk(game_language) ? game_language : kNone;
}

// A UTF-8 text holds a rune only a Chinese, Japanese or Korean group's fonts have (the overlay's Latin font and the default
// group's small fonts lack it): Hangul Jamo (U+1100..U+11FF), CJK radicals and punctuation to ideographs, kana included
// (U+2E80..U+9FFF), Hangul syllables (U+AC00..U+D7AF), compatibility ideographs (U+F900..U+FAFF), fullwidth and halfwidth
// forms (U+FF00..U+FFEF). All of these are three-byte sequences; malformed bytes are skipped.
inline bool cjk_text(const char* s, size_t n) {
    if (!s) return false;
    const auto* p = reinterpret_cast<const unsigned char*>(s);
    for (size_t i = 0; i < n; ++i) {
        const unsigned char b = p[i];
        if (b < 0xE1 || b > 0xEF || i + 2 >= n) continue;
        const unsigned char c1 = p[i + 1], c2 = p[i + 2];
        if ((c1 & 0xC0) != 0x80 || (c2 & 0xC0) != 0x80) continue;
        const uint32_t cp = (static_cast<uint32_t>(b & 0x0F) << 12) | (static_cast<uint32_t>(c1 & 0x3F) << 6) | (c2 & 0x3F);
        if ((cp >= 0x1100 && cp <= 0x11FF) || (cp >= 0x2E80 && cp <= 0x9FFF) || (cp >= 0xAC00 && cp <= 0xD7AF) ||
            (cp >= 0xF900 && cp <= 0xFAFF) || (cp >= 0xFF00 && cp <= 0xFFEF))
            return true;
        i += 2;
    }
    return false;
}

// wanted(), and when that is none while Kiln was asked to draw text that needs a CJK group (text_cjk: kui::note_text saw
// some, a mod's Chinese labels in a German game): the zh-CN group, whose sets cover Chinese, kana and Hangul (the game's
// Noto Serif SC, then KR). HTR, 2026-11-27: Mineral Production Boxes' (Nexus 347) Chinese ShroudForge settings drew as ????.
inline uint32_t wanted(std::string_view kiln_language, uint32_t game_language, bool text_cjk) {
    const uint32_t w = wanted(kiln_language, game_language);
    return w != kNone || !text_cjk ? w : kZhCn;
}

// The language whose glyphs look_info reports (KilnUiLookInfo.glyphs): Kiln's language when its script is not Latin, else
// the game's when its is not, else none (Latin). A CJK one is the group of wanted(); a Cyrillic one is the default layer.
inline uint32_t needed(std::string_view kiln_language, uint32_t game_language) {
    if (const uint32_t k = language_id(kiln_language); k != kNone) return k;
    return cjk(game_language) || cyrillic(game_language) ? game_language : kNone;
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

// A group's regular sets (each font once, in the group's order) into p.sets; false when they are out of the resource.
// `skip_first`: leave out the group's first regular set and its font (the default group's Latin source, the overlay's game
// font, read on its own).
inline bool regular_sets(const uint8_t* res, size_t len, size_t at, uint32_t fonts, const Layout& l, bool skip_first, Pick* p) {
    size_t sbase = 0;
    uint32_t sets = 0;
    if (!blob_array(res, len, at + l.group_sets, l.set_size, &sbase, &sets)) return false;
    uint32_t skipped = UINT32_MAX;
    for (uint32_t s = 0; s < sets; ++s) {
        const size_t sat = sbase + static_cast<size_t>(s) * l.set_size;
        if (res[sat + l.set_variant] != 0) continue;  // bold or italic
        Set set;
        std::memcpy(&set.font, res + sat + l.set_font, 4);
        if (skip_first && skipped == UINT32_MAX) {
            skipped = set.font;
            continue;
        }
        if (set.font >= fonts || set.font == skipped) continue;
        bool seen = false;
        for (const Set& x : p->sets) seen = seen || x.font == set.font;
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
        if (!set.ranges.empty()) p->sets.push_back(std::move(set));
    }
    return true;
}

// The resource's fonts and groups (BlobArrays); false (and p->why) when it has none.
inline bool groups_of(const uint8_t* res, size_t len, const Layout& l, size_t* gbase, uint32_t* groups, Pick* p) {
    size_t fbase = 0;
    uint32_t fonts = 0;
    if (!blob_array(res, len, l.res_fonts, l.font_size, &fbase, &fonts) || fonts == 0) {
        p->why = "no fonts in the UiFontResource";
        return false;
    }
    if (!blob_array(res, len, l.res_groups, l.group_size, gbase, groups) || *groups == 0) {
        p->why = "no language groups in the UiFontResource";
        return false;
    }
    p->font_count = fonts;
    return true;
}

// The sets of `language`'s own group (see above). `res`: the UiFontResource's bytes.
inline Pick pick(const uint8_t* res, size_t len, uint32_t language, const Layout& l = {}) {
    Pick p;
    if (!cjk(language)) {
        p.why = "not a Chinese, Japanese or Korean language";
        return p;
    }
    size_t gbase = 0;
    uint32_t groups = 0;
    if (!groups_of(res, len, l, &gbase, &groups, &p)) return p;
    for (uint32_t g = 0; g < groups; ++g) {
        const size_t at = gbase + static_cast<size_t>(g) * l.group_size;
        uint64_t mask = 0;
        std::memcpy(&mask, res + at + l.group_mask, 8);
        if (res[at + l.group_default] != 0 || language >= 64 || !(mask >> language & 1)) continue;
        if (!regular_sets(res, len, at, p.font_count, l, false, &p)) {
            p.why = "a language group's glyph sets are out of the resource";
            return p;
        }
        p.group = static_cast<int>(g);
        if (p.sets.empty()) p.why = "the language's group has no regular glyph set";
        return p;
    }
    p.why = "the game has no font group of its own for this language";
    return p;
}

// The default group's regular sets after its Latin source (its first regular set), each font once and never the Latin
// source's (see above): what the game draws every default-group language's other scripts with.
inline Pick pick_default(const uint8_t* res, size_t len, const Layout& l = {}) {
    Pick p;
    size_t gbase = 0;
    uint32_t groups = 0;
    if (!groups_of(res, len, l, &gbase, &groups, &p)) return p;
    for (uint32_t g = 0; g < groups; ++g) {
        const size_t at = gbase + static_cast<size_t>(g) * l.group_size;
        if (res[at + l.group_default] == 0) continue;
        if (!regular_sets(res, len, at, p.font_count, l, true, &p)) {
            p.why = "the default group's glyph sets are out of the resource";
            return p;
        }
        p.group = static_cast<int>(g);
        if (p.sets.empty()) p.why = "the default group has no regular glyph set after its Latin one";
        return p;
    }
    p.why = "the UiFontResource has no default language group";
    return p;
}

// Whether every rune of first..last is in one of `sets`' ranges (the Cyrillic check: U+0410..U+044F, Russian's letters).
inline bool covers(const std::vector<Set>& sets, uint32_t first, uint32_t last) {
    for (uint32_t c = first; c <= last; ++c) {
        bool in = false;
        for (const Set& s : sets)
            for (const Range& r : s.ranges) in = in || (c >= r.first && c <= r.last);
        if (!in) return false;
    }
    return true;
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
