// SPDX-License-Identifier: MIT
// font_group.h: which of the game's fonts gives the overlay a Chinese, Japanese or Korean language's glyphs, read from made-up
// keen::UiFontResource bytes (no game data): the language's own group, its regular sets in order, each font once, the
// ranges as ImGui's exclusions, a layout other than 26430625's (as the reflection would give it), bad data; and the
// budget of font_budget.h for a group's files.
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
         {{4, 2, {{0, 0x3ff}}}, {0, 0, {{0, 0x3ff}, {0x500, 0xdff}}}, {24, 1, {{0, 0x3ff}}}, {13, 0, {{0x2e80, 0xabff}}}}},
        {bit << 20, false, {{18, 2, {{0, 0xabff}}}, {13, 0, {{0, 0xabff}}}, {5, 1, {{0, 0xabff}}}, {12, 0, {{0xac00, 0xffff}}}}},
        {bit << 11, false, {{3, 0, {{0, 0xabff}}}, {12, 0, {{0xac00, 0xffff}}}}},
        {bit << 13, false, {{20, 2, {{0, 0xffff}}}, {12, 0, {{0, 0xffff}}}}},
        {bit << 21, false, {{21, 0, {{0, 0xabff}}}, {12, 0, {{0xac00, 0xffff}}}, {12, 0, {{0xac00, 0xffff}}}}},
    };
}

int main() {
    // ---- languages ----
    CHECK(fg::language_id("zh-cn") == fg::kZhCn && fg::language_id("ZH_CN") == fg::kZhCn && fg::language_id("zh") == fg::kZhCn);
    CHECK(fg::language_id("zh-Hans") == fg::kZhCn && fg::language_id("zh-sg") == fg::kZhCn);
    CHECK(fg::language_id("zh-tw") == fg::kZhTw && fg::language_id("zh-HK") == fg::kZhTw && fg::language_id("zh-hant-tw") == fg::kZhTw);
    CHECK(fg::language_id("ja") == fg::kJaJp && fg::language_id("ja-jp") == fg::kJaJp && fg::language_id("ko-KR") == fg::kKoKr);
    CHECK(fg::language_id("en-us") == fg::kNone && fg::language_id("") == fg::kNone && fg::language_id("jav") == fg::kNone);
    // whose glyphs: Kiln's language when CJK, else the game's when CJK
    CHECK(fg::wanted("zh-cn", 3) == fg::kZhCn);          // Kiln forced Chinese in a German game
    CHECK(fg::wanted("en-us", fg::kJaJp) == fg::kJaJp);  // Kiln forced English in a Japanese game: mods' texts are Japanese
    CHECK(fg::wanted("de-de", 3) == fg::kNone);
    CHECK(fg::wanted("", fg::kKoKr) == fg::kKoKr);       // before Kiln.Ui said anything

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

    std::printf("font_group_test: %d checks, %d failed\n", g_checks, g_fail);
    return g_fail ? 1 : 0;
}
