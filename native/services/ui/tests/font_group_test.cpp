// SPDX-License-Identifier: MIT
// font_group.h: which of the game's fonts give the overlay the glyphs its Latin font lacks, read from made-up
// keen::UiFontResource bytes (no game data): a CJK language's own group, its regular sets in order, each font once, the
// ranges as ImGui's exclusions; the default group's sets after its Latin source (Cyrillic, Thai) and whether they cover
// Cyrillic; the languages (CJK, Cyrillic); a layout other than 26430625's (as the reflection would give it), bad data; and
// the budgets of font_budget.h for a group's files and the default group's.
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "../font_budget.h"
#include "../font_group.h"

static int g_fail = 0, g_checks = 0;
#define CHECK(c)                                                     \
    do {                                                             \
        ++g_checks;                                                  \
        if (!(c)) {                                                  \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #c); \
            ++g_fail;                                                \
        }                                                            \
    } while (0)

namespace fg = kui::fontgroup;

// A UiFontResource writer for a layout: fonts (sizes), groups (mask, default, sets (font, variant, ranges)).
struct FakeSet {
    uint32_t font;
    uint8_t variant;
    std::vector<fg::Range> ranges;
};
struct FakeGroup {
    uint64_t mask;
    bool is_default;
    std::vector<FakeSet> sets;
};

static void put32(std::vector<uint8_t>& b, size_t at, uint32_t v) { std::memcpy(&b[at], &v, 4); }
static void put_array(std::vector<uint8_t>& b, size_t field, size_t base, uint32_t n) {
    put32(b, field, static_cast<uint32_t>(static_cast<int32_t>(base - field)));
    put32(b, field + 4, n);
}

static std::vector<uint8_t> resource(const fg::Layout& l, const std::vector<uint32_t>& font_sizes, const std::vector<FakeGroup>& groups) {
    std::vector<uint8_t> b(4096, 0);
    size_t at = 64;  // data after the header
    const size_t fonts = at;
    for (size_t i = 0; i < font_sizes.size(); ++i) {
        put32(b, fonts + i * l.font_size, font_sizes[i]);                         // the content hash's size
        put32(b, fonts + i * l.font_size + 4, static_cast<uint32_t>(1000 + i));  // hash0: tells the files apart
    }
    put_array(b, l.res_fonts, fonts, static_cast<uint32_t>(font_sizes.size()));
    at += font_sizes.size() * l.font_size;
    const size_t gbase = at;
    put_array(b, l.res_groups, gbase, static_cast<uint32_t>(groups.size()));
    at += groups.size() * l.group_size;
    for (size_t g = 0; g < groups.size(); ++g) {
        const size_t ga = gbase + g * l.group_size;
        std::memcpy(&b[ga + l.group_mask], &groups[g].mask, 8);
        b[ga + l.group_default] = groups[g].is_default ? 1 : 0;
        const size_t sbase = at;
        put_array(b, ga + l.group_sets, sbase, static_cast<uint32_t>(groups[g].sets.size()));
        at += groups[g].sets.size() * l.set_size;
        for (size_t s = 0; s < groups[g].sets.size(); ++s) {
            const FakeSet& fs = groups[g].sets[s];
            const size_t sa = sbase + s * l.set_size;
            put32(b, sa + l.set_font, fs.font);
            b[sa + l.set_variant] = fs.variant;
            const size_t rbase = at;
            put_array(b, sa + l.set_ranges, rbase, static_cast<uint32_t>(fs.ranges.size()));
            for (size_t r = 0; r < fs.ranges.size(); ++r) {
                put32(b, rbase + r * l.range_size + l.range_first, fs.ranges[r].first);
                put32(b, rbase + r * l.range_size + l.range_last, fs.ranges[r].last);
            }
            at += fs.ranges.size() * l.range_size;
        }
    }
    b.resize(at);
    return b;
}

// The shape of client 26430625's resource: the default group (Latin, with CJK fallbacks), zh-CN, ja-JP, ko-KR and zh-TW groups.
static std::vector<FakeGroup> game_like() {
    const uint64_t bit = 1;
    return {
        {(bit << 1) | (bit << 3) | (bit << 20) | (bit << 11), true,
         {{4, 2, {{0, 0x3ff}}}, {0, 0, {{0, 0x3ff}, {0x500, 0xdff}}}, {24, 1, {{0, 0x3ff}}}, {13, 0, {{0x2e80, 0xabff}}},
          {26, 1, {{0x400, 0x4ff}}}, {10, 0, {{0x400, 0x4ff}, {0x20a9, 0x20a9}, {0x2116, 0x2116}}}, {12, 0, {{0xac00, 0xffff}}},
          {6, 0, {{0xe00, 0xe7f}}}, {0, 0, {{0x2000, 0x206f}}}}},
        {bit << 20, false, {{18, 2, {{0, 0xabff}}}, {13, 0, {{0, 0xabff}}}, {5, 1, {{0, 0xabff}}}, {12, 0, {{0xac00, 0xffff}}}}},
        {bit << 11, false, {{3, 0, {{0, 0xabff}}}, {12, 0, {{0xac00, 0xffff}}}}},
        {bit << 13, false, {{20, 2, {{0, 0xffff}}}, {12, 0, {{0, 0xffff}}}}},
        {bit << 21, false, {{21, 0, {{0, 0xabff}}}, {12, 0, {{0xac00, 0xffff}}}, {12, 0, {{0xac00, 0xffff}}}}},
        {(bit << 9) | (bit << 24) | (bit << 34), false, {{10, 0, {{0, 0xabff}}}, {12, 0, {{0xac00, 0xffff}}}, {13, 0, {{0x500, 0xabff}}}}},
    };
}

int main() {
    // ---- languages ----
    CHECK(fg::language_id("zh-cn") == fg::kZhCn && fg::language_id("ZH_CN") == fg::kZhCn && fg::language_id("zh") == fg::kZhCn);
    CHECK(fg::language_id("zh-Hans") == fg::kZhCn && fg::language_id("zh-sg") == fg::kZhCn);
    CHECK(fg::language_id("zh-tw") == fg::kZhTw && fg::language_id("zh-HK") == fg::kZhTw && fg::language_id("zh-hant-tw") == fg::kZhTw);
    CHECK(fg::language_id("ja") == fg::kJaJp && fg::language_id("ja-jp") == fg::kJaJp && fg::language_id("ko-KR") == fg::kKoKr);
    CHECK(fg::language_id("en-us") == fg::kNone && fg::language_id("") == fg::kNone && fg::language_id("jav") == fg::kNone);
    // Cyrillic: ru, uk, bg have the game's ids; the others count as ru-RU; Serbian in Latin script needs nothing
    CHECK(fg::language_id("ru") == fg::kRuRu && fg::language_id("ru-RU") == fg::kRuRu && fg::language_id("uk_ua") == fg::kUkUa);
    CHECK(fg::language_id("bg-bg") == fg::kBgBg && fg::language_id("be") == fg::kRuRu && fg::language_id("sr-cyrl") == fg::kRuRu);
    CHECK(fg::language_id("sr-latn-rs") == fg::kNone && fg::language_id("run") == fg::kNone && fg::language_id("pl-pl") == fg::kNone);
    CHECK(fg::cyrillic(fg::kRuRu) && fg::cyrillic(fg::kUkUa) && fg::cyrillic(fg::kBgBg) && !fg::cyrillic(fg::kZhCn) && !fg::cjk(fg::kRuRu));
    // whose group: Kiln's language when CJK, else the game's when CJK
    CHECK(fg::wanted("zh-cn", 3) == fg::kZhCn);          // Kiln forced Chinese in a German game
    CHECK(fg::wanted("en-us", fg::kJaJp) == fg::kJaJp);  // Kiln forced English in a Japanese game: mods' texts are Japanese
    CHECK(fg::wanted("de-de", 3) == fg::kNone);
    CHECK(fg::wanted("", fg::kKoKr) == fg::kKoKr);       // before Kiln.Ui said anything
    CHECK(fg::wanted("ru", fg::kZhCn) == fg::kZhCn);     // Kiln in Russian in a Chinese game: mods' Chinese still drawn
    CHECK(fg::wanted("ru", 3) == fg::kNone);             // Cyrillic needs no group of its own (the default group's fonts)
    // whose glyphs look_info reports: Kiln's language when not Latin, else the game's when not Latin
    CHECK(fg::needed("ru-ru", 1) == fg::kRuRu && fg::needed("ru", fg::kZhCn) == fg::kRuRu && fg::needed("zh", fg::kRuRu) == fg::kZhCn);
    CHECK(fg::needed("en-us", fg::kUkUa) == fg::kUkUa && fg::needed("en-us", fg::kJaJp) == fg::kJaJp);
    CHECK(fg::needed("de", 3) == fg::kNone && fg::needed("en", 17) == fg::kNone);  // Thai: the game draws it with the default group too
    // a text that needs a CJK group (HTR, 2026-11-27: Nexus 347's Chinese ShroudForge labels in an English game drew as ????)
    auto cjk = [](const char* t) { return fg::cjk_text(t, std::strlen(t)); };
    CHECK(cjk("\xe7\xa1\xab\xe7\xa3\xba | \xe7\xa1\xab\xe7\xa3\xba" "4 / Archaic Essence 4"));  // 硫磺 | 硫磺4 / ...
    CHECK(cjk("\xe3\x83\xa2\xe3\x83\x83\xe3\x83\x89") && cjk("\xeb\xaa\xa8\xeb\x93\x9c"));  // モッド, 모드
    CHECK(cjk("\xef\xbc\x81") && cjk("\xe3\x80\x82") && cjk("\xe1\x84\x80"));  // fullwidth !, ideographic full stop, a Jamo
    CHECK(!cjk("Sulfur, Coal and Iron Ore") && !cjk("") && !cjk("caf\xc3\xa9 \xe2\x80\x94 \xe2\x80\xa6 \xc2\xb7"));  // é — … ·
    CHECK(!cjk("\xd0\x96\xd0\xb8\xd0\xb7\xd0\xbd\xd1\x8c") && !cjk("\xe0\xb8\x81") && !cjk("\xef\xbf\xbd"));  // Cyrillic, Thai, U+FFFD
    CHECK(!cjk("\xe7\xa1") && !cjk("\xe7\x41\xab") && !fg::cjk_text(nullptr, 3));  // cut and malformed sequences
    CHECK(fg::cjk_text("\xe7\xa1\xab", 3) && !fg::cjk_text("\xe7\xa1\xab", 2));
    // whose group with such a text: Kiln's or the game's CJK language first, else zh-CN's (Chinese, kana and Hangul)
    CHECK(fg::wanted("en-us", 3, true) == fg::kZhCn && fg::wanted("en-us", 3, false) == fg::kNone);
    CHECK(fg::wanted("ru", 3, true) == fg::kZhCn && fg::wanted("ja", 3, true) == fg::kJaJp && fg::wanted("en", fg::kKoKr, true) == fg::kKoKr);
    CHECK(fg::needed("en-us", 3) == fg::kNone);  // look_info's glyphs stay about Kiln's and the game's language

    // ---- the group of a language, with 26430625's layout ----
    const fg::Layout l;
    std::vector<uint32_t> sizes(28, 70000);
    sizes[13] = 11214632;
    sizes[12] = 7384364;
    const auto res = resource(l, sizes, game_like());
    fg::Pick p = fg::pick(res.data(), res.size(), fg::kZhCn, l);
    CHECK(p.group == 1 && p.font_count == 28);
    CHECK(p.sets.size() == 2 && p.sets[0].font == 13 && p.sets[1].font == 12);  // regular only, in the group's order
    CHECK(p.sets[0].ranges.size() == 1 && p.sets[0].ranges[0].last == 0xabff && p.sets[1].ranges[0].first == 0xac00);
    p = fg::pick(res.data(), res.size(), fg::kJaJp, l);
    CHECK(p.group == 2 && p.sets.size() == 2 && p.sets[0].font == 3);
    p = fg::pick(res.data(), res.size(), fg::kKoKr, l);
    CHECK(p.group == 3 && p.sets.size() == 1 && p.sets[0].font == 12);  // the italic set is no regular one
    p = fg::pick(res.data(), res.size(), fg::kZhTw, l);
    CHECK(p.group == 4 && p.sets.size() == 2 && p.sets[1].font == 12);  // font 12 once
    p = fg::pick(res.data(), res.size(), 3, l);                          // German: no group of its own needed
    CHECK(p.group == -1 && p.sets.empty() && !p.why.empty());
    p = fg::pick(res.data(), res.size(), fg::kRuRu, l);                  // Russian: its own group is never taken (default's fonts)
    CHECK(p.group == -1 && p.sets.empty());

    // ---- the default group's fonts after its Latin source ----
    p = fg::pick_default(res.data(), res.size(), l);
    CHECK(p.group == 0 && p.font_count == 28);
    // regular only, in the group's order, the Latin source (font 0, also its second set) left out, each font once
    CHECK(p.sets.size() == 4 && p.sets[0].font == 13 && p.sets[1].font == 10 && p.sets[2].font == 12 && p.sets[3].font == 6);
    CHECK(p.sets[1].ranges.size() == 3 && p.sets[1].ranges[0].first == 0x400 && p.sets[1].ranges[2].last == 0x2116);
    CHECK(fg::covers(p.sets, 0x410, 0x44f) && !fg::covers(p.sets, 0x370, 0x3ff));
    CHECK(!fg::covers({p.sets[0], p.sets[2], p.sets[3]}, 0x410, 0x44f));  // without Noto Serif: no Cyrillic
    CHECK((fg::exclude_ranges(p.sets[1].ranges) == std::vector<uint16_t>{1, 0x3ff, 0x500, 0x20a8, 0x20aa, 0x2115, 0x2117, 0xffff, 0}));
    {  // no default group: why
        auto groups = game_like();
        groups.erase(groups.begin());
        const auto nodef = resource(l, sizes, groups);
        p = fg::pick_default(nodef.data(), nodef.size(), l);
        CHECK(p.group == -1 && p.sets.empty() && p.why.find("no default") != std::string::npos);
        groups = game_like();
        groups[0].sets = {{0, 0, {{0, 0xffff}}}, {24, 1, {{0, 0xffff}}}};  // only its Latin source
        const auto latin = resource(l, sizes, groups);
        p = fg::pick_default(latin.data(), latin.size(), l);
        CHECK(p.group == 0 && p.sets.empty() && !p.why.empty());
    }
    CHECK(fg::pick_default(res.data(), 4, l).sets.empty());  // too short: nothing, never out of bounds
    // the default group is never taken, even when its mask has the language
    auto only_default = game_like();
    only_default.resize(1);
    p = fg::pick(resource(l, sizes, only_default).data(), resource(l, sizes, only_default).size(), fg::kZhCn, l);
    CHECK(p.group == -1 && p.why.find("no font group") != std::string::npos);

    // ---- another layout (as another build's reflection would give it) ----
    fg::Layout o;
    o.res_fonts = 0x10, o.res_groups = 0x0, o.font_size = 40;
    o.group_size = 32, o.group_mask = 0x8, o.group_sets = 0x10, o.group_default = 0x1c;
    o.set_size = 24, o.set_font = 0x4, o.set_ranges = 0x8, o.set_variant = 0x14;
    o.range_size = 8, o.range_first = 0x4, o.range_last = 0x0;
    const auto other = resource(o, sizes, game_like());
    p = fg::pick(other.data(), other.size(), fg::kZhCn, o);
    CHECK(p.group == 1 && p.sets.size() == 2 && p.sets[0].font == 13 && p.sets[0].ranges[0].first == 0 && p.sets[0].ranges[0].last == 0xabff);
    p = fg::pick_default(other.data(), other.size(), o);
    CHECK(p.group == 0 && p.sets.size() == 4 && p.sets[1].font == 10 && fg::covers(p.sets, 0x410, 0x44f));
    // read with the wrong layout: nothing found or nothing sensible, never out of bounds (ASan)
    p = fg::pick(other.data(), other.size(), fg::kZhCn, l);
    CHECK(p.sets.empty() || p.sets[0].font < 28);

    // ---- bad data ----
    CHECK(fg::pick(res.data(), 4, fg::kZhCn, l).sets.empty());
    std::vector<uint8_t> broken = res;
    put32(broken, l.res_groups, 0x7fffff00);  // groups far past the end
    CHECK(fg::pick(broken.data(), broken.size(), fg::kZhCn, l).sets.empty());
    std::vector<uint8_t> none(64, 0);
    CHECK(fg::pick(none.data(), none.size(), fg::kZhCn, l).why == "no fonts in the UiFontResource");
    // a set naming a font past the resource's fonts is left out
    auto bad_font = game_like();
    bad_font[1].sets[1].font = 99;
    p = fg::pick(resource(l, sizes, bad_font).data(), resource(l, sizes, bad_font).size(), fg::kZhCn, l);
    CHECK(p.sets.size() == 1 && p.sets[0].font == 12);

    // ---- ImGui's exclusions: every rune outside the set's ranges ----
    std::vector<uint16_t> ex = fg::exclude_ranges({{0, 0xabff}});
    CHECK((ex == std::vector<uint16_t>{0xac00, 0xffff, 0}));
    ex = fg::exclude_ranges({{0xac00, 0xffff}});
    CHECK((ex == std::vector<uint16_t>{1, 0xabff, 0}));
    ex = fg::exclude_ranges({{0x500, 0xdff}, {0, 0x3ff}, {0x300, 0x450}});  // unordered, overlapping
    CHECK((ex == std::vector<uint16_t>{0x451, 0x4ff, 0xe00, 0xffff, 0}));
    CHECK(fg::exclude_ranges({{0, 0xffff}}).empty());       // takes every rune: nothing to leave out
    CHECK(fg::exclude_ranges({{0, 0x10ffff}}).empty());
    std::vector<fg::Range> many;
    for (uint32_t i = 0; i < 40; ++i) many.push_back({i * 100, i * 100 + 10});
    CHECK(fg::exclude_ranges(many).empty());                // more pairs than ImGui takes: no exclusion

    // ---- the budget of a group's files ----
    namespace fonts = kui::fonts;
    const size_t zh[2] = {11214632, 7384364};
    CHECK(fonts::admit_group(zh, 2) == 2);                  // zh-CN: 18.6 MB
    const size_t big[2] = {fonts::kMaxGroupFontBytes + 1, 100000};
    CHECK(fonts::admit_group(big, 2) == 0);
    const size_t over[3] = {12u << 20, 12u << 20, 1u << 20};
    CHECK(fonts::admit_group(over, 3) == 2);                // 24 MiB exactly fits, the third passes it
    const size_t tiny[1] = {12};
    CHECK(fonts::admit_group(tiny, 1) == 0);
    const size_t five[5] = {2000000, 2000000, 2000000, 2000000, 2000000};
    CHECK(fonts::admit_group(five, 5) == fonts::kMaxGroupFonts);

    // ---- the budget of the default group's files: small ones only, the large CJK fallbacks between them skipped ----
    {
        const size_t game[4] = {11214632, 515892, 7384364, 61636};  // 26430625: SC, Noto Serif, KR, Thai
        bool keep[4] = {};
        CHECK(fonts::admit_default(game, 4, keep) == 2 && !keep[0] && keep[1] && !keep[2] && keep[3]);
        const size_t many[6] = {1500000, 1500000, 1500000, 600000, 2000, 2000};  // 4 MiB together, 4 files at most
        bool k6[6] = {};
        CHECK(fonts::admit_default(many, 6, k6) == 4 && k6[0] && k6[1] && !k6[2] && k6[3] && k6[4] && !k6[5]);
        const size_t odd[2] = {12, fonts::kMaxDefaultFontBytes};
        bool k2[2] = {};
        CHECK(fonts::admit_default(odd, 2, k2) == 1 && !k2[0] && k2[1]);
    }

    std::printf("font_group_test: %d checks, %d failed\n", g_checks, g_fail);
    return g_fail ? 1 : 0;
}
