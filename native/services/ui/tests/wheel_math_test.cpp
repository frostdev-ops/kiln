// SPDX-License-Identifier: MIT
// Host test of the radial wheel's geometry, hit test, labels and open / select / cancel rules (wheel_math.h): no game, no
// GPU, no Wine. Run: run.sh
#include <cmath>
#include <cstdio>
#include <string>

#include "../wheel_math.h"

static int g_fail = 0, g_checks = 0;
#define CHECK(cond)                                                          \
    do {                                                                     \
        ++g_checks;                                                          \
        if (!(cond)) {                                                       \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);      \
            ++g_fail;                                                        \
        }                                                                    \
    } while (0)
static bool near(float a, float b, float eps = 1e-4f) { return std::fabs(a - b) <= eps; }

using namespace kwheel;

// the offset of the point at `deg` degrees clockwise from the top, `r` pixels out
static void at(float deg, float r, float* dx, float* dy) { polar(deg * kPi / 180.0f, r, dx, dy); }
static int sector_at(float deg, float r, int n, float dead = 40) {
    float dx, dy;
    at(deg, r, &dx, &dy);
    return hit_sector(dx, dy, dead, n);
}

static void test_pages() {
    CHECK(page_count(0) == 0 && page_count(-3) == 0 && page_count(1) == 1 && page_count(8) == 1 && page_count(9) == 2 &&
          page_count(64) == 8);
    CHECK(page_size(20, 0) == 8 && page_size(20, 1) == 8 && page_size(20, 2) == 4 && page_size(20, 3) == 0 && page_size(20, -1) == 0);
    CHECK(page_size(1, 0) == 1 && page_size(0, 0) == 0 && page_size(64, 7) == 8);
    CHECK(turn_page(0, 3, 1) == 1 && turn_page(2, 3, 1) == 0 && turn_page(0, 3, -1) == 2 && turn_page(1, 1, 1) == 0 &&
          turn_page(0, 0, -1) == 0 && turn_page(1, 3, -7) == 0);
}

static void test_rings() {
    Rings r = rings(1920, 1080, 1.0f);
    CHECK(near(r.outer, 280.0f) && near(r.inner, 280.0f * 0.32f));  // 30% of 1080 = 324, capped at 280
    r = rings(800, 600, 1.0f);  // 30% of 600 = 180
    CHECK(near(r.outer, 180.0f));
    r = rings(1024, 480, 1.0f);  // 144 is under the 150 floor, and 46% of 480 = 220.8 does not cap it
    CHECK(near(r.outer, 150.0f));
    r = rings(300, 300, 1.0f);   // a tiny window: the 46% cap beats the floor
    CHECK(near(r.outer, 138.0f));
    r = rings(3840, 2160, 2.0f);  // 30% of 2160 = 648, capped at 280 * 2
    CHECK(near(r.outer, 560.0f));
    r = rings(0, 0, 1.0f);  // nothing sensible: a usable ring anyway
    CHECK(r.outer > 0 && std::isfinite(r.outer) && r.inner > 0 && r.inner < r.outer);
    r = rings(1920, 1080, NAN);
    CHECK(std::isfinite(r.outer) && r.outer > 0);
}

static void test_angles() {
    CHECK(near(clock_angle(0, -1), 0) && near(clock_angle(1, 0), kPi / 2) && near(clock_angle(0, 1), kPi) &&
          near(clock_angle(-1, 0), kPi * 1.5f));
    CHECK(near(sector_span(8), kPi / 4) && near(sector_span(1), 2 * kPi) && near(sector_span(4), kPi / 2));
    CHECK(near(sector_center(0, 8), 0) && near(sector_center(2, 8), kPi / 2) && near(sector_center(3, 3), 2 * kPi));
    float dx, dy;
    polar(0, 100, &dx, &dy);
    CHECK(near(dx, 0) && near(dy, -100));
    polar(kPi / 2, 100, &dx, &dy);
    CHECK(near(dx, 100) && near(dy, 0, 1e-3f));
}

static void test_hit() {
    // eight sectors: 0 at the top, clockwise, each 45 degrees wide centred on its own angle
    for (int s = 0; s < 8; ++s) CHECK(sector_at(45.0f * s, 150, 8) == s);
    CHECK(sector_at(22.0f, 150, 8) == 0 && sector_at(23.0f, 150, 8) == 1);
    CHECK(sector_at(-22.0f, 150, 8) == 0 && sector_at(-23.0f, 150, 8) == 7);
    CHECK(sector_at(359.0f, 150, 8) == 0 && sector_at(337.0f, 150, 8) == 7 && sector_at(338.0f, 150, 8) == 0);
    // the centre hole is nothing, anywhere in it, and so is the edge of it inwards; outside it, any distance counts
    CHECK(sector_at(90, 0.0f, 8) == -1 && sector_at(90, 39.0f, 8) == -1 && sector_at(90, 41.0f, 8) == 2);
    CHECK(sector_at(90, 5000.0f, 8) == 2);
    // three sectors: 120 degrees each, the first centred on the top
    CHECK(sector_at(0, 150, 3) == 0 && sector_at(59, 150, 3) == 0 && sector_at(61, 150, 3) == 1);
    CHECK(sector_at(100, 150, 3) == 1 && sector_at(130, 150, 3) == 1 && sector_at(181, 150, 3) == 2 && sector_at(299, 150, 3) == 2 &&
          sector_at(301, 150, 3) == 0);
    // one sector is everything outside the hole; two split left and right of the vertical (first one on top)
    CHECK(sector_at(0, 150, 1) == 0 && sector_at(180, 150, 1) == 0 && sector_at(271, 150, 1) == 0);
    CHECK(sector_at(10, 150, 2) == 0 && sector_at(350, 150, 2) == 0 && sector_at(170, 150, 2) == 1 && sector_at(190, 150, 2) == 1);
    // the edge cases the game can produce
    CHECK(hit_sector(100, 0, 40, 0) == -1 && hit_sector(100, 0, 40, -2) == -1);
    CHECK(hit_sector(NAN, 0, 40, 8) == -1 && hit_sector(0, INFINITY, 40, 8) == -1);
    CHECK(hit_sector(0, -1e30f, 40, 8) == 0);
    for (int n = 1; n <= 8; ++n)  // every sector is reachable and nothing falls outside 0..n-1, at every angle
        for (int d = 0; d < 360; ++d) {
            const int s = sector_at(static_cast<float>(d), 200, n);
            CHECK(s >= 0 && s < n);
        }
    for (int n = 1; n <= 8; ++n)  // the centre of each sector hits that sector
        for (int s = 0; s < n; ++s) CHECK(sector_at(360.0f * s / n, 200, n) == s);
}

static void test_labels() {
    std::string_view name, details;
    split_label("Wolf (level 3)", &name, &details);
    CHECK(name == "Wolf" && details == "level 3");
    split_label("Wolf", &name, &details);
    CHECK(name == "Wolf" && details.empty());
    split_label("Potion (small) (x3)", &name, &details);
    CHECK(name == "Potion (small)" && details == "x3");
    split_label("Sword (+(1))", &name, &details);
    CHECK(name == "Sword" && details == "+(1)");
    split_label("Smile :)", &name, &details);  // a ')' that closes nothing
    CHECK(name == "Smile :)" && details.empty());
    split_label("(only details)", &name, &details);  // no name before it
    CHECK(name == "(only details)" && details.empty());
    split_label("Name(no space)", &name, &details);
    CHECK(name == "Name(no space)" && details.empty());
    split_label("Name ()", &name, &details);
    CHECK(name == "Name" && details.empty());
    split_label("", &name, &details);
    CHECK(name.empty() && details.empty());
    split_label("A  (two spaces)", &name, &details);
    CHECK(name == "A" && details == "two spaces");
    split_label("Bad (unclosed", &name, &details);
    CHECK(name == "Bad (unclosed" && details.empty());

    CHECK(clean_text("a\nb\tc", 64) == "a b c");
    CHECK(clean_text("abcdef", 3) == "abc");
    CHECK(clean_text("h\xC3\xA9llo", 2) == "h");          // would cut the two-byte e-acute: dropped whole
    CHECK(clean_text("h\xC3\xA9llo", 3) == "h\xC3\xA9");
    CHECK(clean_text("", 5).empty());

    // ellipsize with a fixed 10 px per byte
    auto m = [](std::string_view s) { return 10.0f * static_cast<float>(s.size()); };
    CHECK(ellipsize("short", 100, m) == "short");
    CHECK(ellipsize("abcdefghij", 100, m) == "abcdefghij");
    CHECK(ellipsize("abcdefghijk", 100, m) == "abcdefg...");
    CHECK(ellipsize("abcdefghijk", 30, m) == "...");
    CHECK(ellipsize("abcdefghijk", 20, m).empty());
    const std::string utf = ellipsize("\xC3\xA9\xC3\xA9\xC3\xA9\xC3\xA9\xC3\xA9\xC3\xA9", 70, m);  // six e-acutes (12 bytes)
    CHECK(utf == "\xC3\xA9\xC3\xA9..." || utf == "\xC3\xA9\xC3\xA9\xC3\xA9..." || utf == "\xC3\xA9...");
    CHECK(utf.size() <= 7 && utf.substr(utf.size() - 3) == "...");
    CHECK(!utf8_continuation(utf[0]));
    // valid UTF-8 whatever the width
    for (int w = 0; w <= 130; w += 10) {
        const std::string e = ellipsize("\xE2\x82\xAC""uro \xE2\x82\xAC""uro \xF0\x9F\x92\xA7""x", static_cast<float>(w), m);
        for (size_t i = 0; i < e.size(); ++i)
            if (utf8_continuation(e[i])) CHECK(i > 0);
        CHECK(e.empty() || m(e) <= static_cast<float>(w));
    }
}

static void test_hold_keys() {
    CHECK(valid_hold_key(0) && valid_hold_key(0x10) && valid_hold_key(0x11) && valid_hold_key(0x12) && valid_hold_key('Q') &&
          valid_hold_key(0x20) && valid_hold_key(0xFF));
    CHECK(!valid_hold_key(1) && !valid_hold_key(2) && !valid_hold_key(4) && !valid_hold_key(5) && !valid_hold_key(6));
    CHECK(!valid_hold_key(0xA0) && !valid_hold_key(0xA5) && valid_hold_key(0xA6) && valid_hold_key(7) && !valid_hold_key(0x100));
}

static Inputs ptr(float deg, float r) {
    Inputs in;
    at(deg, r, &in.dx, &in.dy);
    return in;
}

static void test_model_click() {
    // no hold key: a left click on a highlighted entry picks, anything else leaves it open
    Model m(5, false, 1000, 40);
    Step st = m.step(ptr(0, 150));
    CHECK(st.kind == Step::None && st.highlight == 0 && st.page == 0);
    Inputs in = ptr(2 * 72.0f, 150);  // five sectors: 72 degrees each
    st = m.step(in);
    CHECK(st.kind == Step::None && st.highlight == 2);
    in.left_click = true;
    st = m.step(in);
    CHECK(st.kind == Step::Pick && st.choice == 2);
    Inputs centre = ptr(0, 10);
    centre.left_click = true;  // a click in the centre: nothing
    st = m.step(centre);
    CHECK(st.kind == Step::None && st.highlight == -1 && m.highlight() == -1);
    Inputs rel = ptr(0, 150);
    rel.hold_released = true;  // no hold key: a release means nothing
    rel.release_ms = 5000;
    st = m.step(rel);
    CHECK(st.kind == Step::None);
}

static void test_model_cancel() {
    Model m(5, false, 0, 40);
    Inputs in = ptr(0, 150);
    in.left_click = true;
    in.right_click = true;  // cancel wins over a click in the same step
    Step st = m.step(in);
    CHECK(st.kind == Step::Cancel && st.choice == -1);
    Model e(5, true, 0, 40);
    Inputs esc = ptr(0, 150);
    esc.escape = true;
    esc.hold_released = true;
    esc.release_ms = 1000;
    CHECK(e.step(esc).kind == Step::Cancel);
}

static void test_model_hold() {
    // hold key: the release picks what is highlighted ...
    {
        Model m(8, true, 1000, 40);
        Inputs in = ptr(135, 150);
        in.hold_released = true;
        in.release_ms = 1100;  // even a flick (100 ms) picks when something is highlighted
        Step st = m.step(in);
        CHECK(st.kind == Step::Pick && st.choice == 3);
    }
    // ... cancels in the centre after a hold ...
    {
        Model m(8, true, 1000, 40);
        Inputs in = ptr(0, 10);
        in.hold_released = true;
        in.release_ms = 1000 + kTapMs;  // exactly 300 ms is a hold, not a tap
        CHECK(m.step(in).kind == Step::Cancel);
    }
    // ... and a tap (released within 300 ms, nothing highlighted) leaves it open in click mode
    {
        Model m(8, true, 1000, 40);
        Inputs in = ptr(0, 10);
        in.hold_released = true;
        in.release_ms = 1000 + kTapMs - 1;
        Step st = m.step(in);
        CHECK(st.kind == Step::None && !m.hold_mode());
        Inputs again = ptr(0, 10);  // a second release event does nothing now
        again.hold_released = true;
        again.release_ms = 5000;
        CHECK(m.step(again).kind == Step::None);
        Inputs click = ptr(90, 150);
        click.left_click = true;
        st = m.step(click);
        CHECK(st.kind == Step::Pick && st.choice == 2);
    }
    // a click picks in hold mode too
    {
        Model m(8, true, 1000, 40);
        Inputs in = ptr(180, 150);
        in.left_click = true;
        Step st = m.step(in);
        CHECK(st.kind == Step::Pick && st.choice == 4);
    }
    // a release time before the wheel opened (clock skew) is not a tap
    {
        Model m(8, true, 1000, 40);
        Inputs in = ptr(0, 10);
        in.hold_released = true;
        in.release_ms = 900;
        CHECK(m.step(in).kind == Step::Cancel);
    }
}

static void test_model_pages() {
    Model m(20, false, 0, 40);  // pages of 8, 8, 4
    CHECK(m.pages() == 3 && m.page_items() == 8);
    Inputs in = ptr(0, 150);
    Step st = m.step(in);
    CHECK(st.highlight == 0 && st.page == 0);
    in.turn = 1;
    st = m.step(in);
    CHECK(st.page == 1 && st.highlight == 8);
    in.turn = 5;  // only ever one page per step
    st = m.step(in);
    CHECK(st.page == 2 && m.page_items() == 4 && st.highlight == 16);
    in.turn = 1;
    st = m.step(in);
    CHECK(st.page == 0 && st.highlight == 0);  // wraps round
    in.turn = -1;
    st = m.step(in);
    CHECK(st.page == 2);
    // on the short last page the four entries are 90 degrees apart
    in.turn = 0;
    Inputs b = ptr(180, 150);
    b.left_click = true;
    st = m.step(b);
    CHECK(st.kind == Step::Pick && st.choice == 18);
    Inputs c = ptr(270, 150);
    c.left_click = true;
    st = m.step(c);
    CHECK(st.kind == Step::Pick && st.choice == 19);
    // a page turn and a click in one step picks from the new page
    Model p(20, false, 0, 40);
    Inputs both = ptr(0, 150);
    both.turn = 1;
    both.left_click = true;
    st = p.step(both);
    CHECK(st.kind == Step::Pick && st.choice == 8);
    // one entry: one page, turning does nothing, the whole ring is it
    Model one(1, false, 0, 40);
    Inputs o = ptr(200, 150);
    o.turn = 1;
    o.left_click = true;
    st = one.step(o);
    CHECK(st.kind == Step::Pick && st.choice == 0 && st.page == 0);
    // a full 64 entries: eight pages of eight
    Model full(64, false, 0, 40);
    CHECK(full.pages() == 8 && full.entry_of(7) == 7);
    Inputs f = ptr(0, 150);
    f.turn = -1;
    st = full.step(f);
    CHECK(st.page == 7 && st.highlight == 56);
}

int main() {
    test_pages();
    test_rings();
    test_angles();
    test_hit();
    test_labels();
    test_hold_keys();
    test_model_click();
    test_model_cancel();
    test_model_hold();
    test_model_pages();
    if (g_fail == 0) std::printf("wheel_math: %d checks passed\n", g_checks);
    return g_fail == 0 ? 0 : 1;
}
