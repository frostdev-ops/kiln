// SPDX-License-Identifier: MIT
// An image's opaque bounds and where a trimmed image goes (image_rect.h): synthetic RGBA icons with margins, fully
// transparent and fully opaque ones, odd sizes, faint pixels under the threshold; the UVs of a rectangle; fitting a source
// inside a box keeping its aspect ratio. Checked against a plain full scan on random images too.
#include <algorithm>
#include <cstdio>
#include <random>
#include <vector>

#include "../image_rect.h"

namespace {
int g_fail = 0, g_checks = 0;
#define CHECK(c)                                                              \
    do {                                                                      \
        ++g_checks;                                                           \
        if (!(c)) {                                                           \
            std::fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #c); \
            ++g_fail;                                                         \
        }                                                                     \
    } while (0)

using namespace kui::img;

struct Px {
    int w, h;
    std::vector<uint8_t> rgba;
    Px(int w_, int h_, uint8_t a = 0) : w(w_), h(h_), rgba(static_cast<size_t>(w_) * h_ * 4, 0) {
        for (size_t i = 3; i < rgba.size(); i += 4) rgba[i] = a;
    }
    void set(int x, int y, uint8_t a) {
        auto* p = &rgba[(static_cast<size_t>(y) * w + x) * 4];
        p[0] = p[1] = p[2] = 255;  // colour never counts, only alpha
        p[3] = a;
    }
    void fill(int x0, int y0, int x1, int y1, uint8_t a) {
        for (int y = y0; y < y1; ++y)
            for (int x = x0; x < x1; ++x) set(x, y, a);
    }
    Rect bounds(uint8_t t = kOpaqueAlpha) const { return opaque_bounds(rgba.data(), w, h, t); }
};

// The same rule the slow way.
Rect scan(const Px& p, uint8_t t) {
    int x0 = p.w, y0 = p.h, x1 = -1, y1 = -1;
    for (int y = 0; y < p.h; ++y)
        for (int x = 0; x < p.w; ++x)
            if (p.rgba[(static_cast<size_t>(y) * p.w + x) * 4 + 3] >= t) {
                x0 = std::min(x0, x);
                y0 = std::min(y0, y);
                x1 = std::max(x1, x);
                y1 = std::max(y1, y);
            }
    if (x1 < 0) return {0, 0, p.w, p.h};
    return {x0, y0, x1 + 1, y1 + 1};
}

bool near(float a, float b) { return std::fabs(a - b) < 1e-5f; }

void bounds() {
    // an icon with wide margins: art in the middle of 256 x 256 (64 px of margin on the left, 40 on top...)
    Px icon(256, 256);
    icon.fill(64, 40, 200, 230, 255);
    CHECK((icon.bounds() == Rect{64, 40, 200, 230}));
    // irregular art: a diagonal stroke and a lone pixel decide the box, not a rectangle of fill
    Px diag(64, 64);
    for (int i = 10; i < 50; ++i) diag.set(i, 60 - i, 200);
    diag.set(5, 30, 255);
    CHECK((diag.bounds() == Rect{5, 11, 50, 51}));
    // faint pixels (a soft shadow, compression noise) under the threshold are margin; at the threshold they are art
    Px faint(32, 32);
    faint.fill(8, 8, 24, 24, 255);
    faint.set(0, 0, kOpaqueAlpha - 1);
    faint.set(31, 31, 1);
    CHECK((faint.bounds() == Rect{8, 8, 24, 24}));
    faint.set(31, 31, kOpaqueAlpha);
    CHECK((faint.bounds() == Rect{8, 8, 32, 32}));
    CHECK((faint.bounds(0) == Rect{0, 0, 32, 32}));  // threshold 0: every pixel
    // fully transparent: the whole image (nothing to trim to)
    Px empty(40, 24);
    CHECK((empty.bounds() == Rect{0, 0, 40, 24}));
    Px under(16, 16, kOpaqueAlpha - 1);
    CHECK((under.bounds() == Rect{0, 0, 16, 16}));
    // fully opaque: the whole image
    Px full(48, 48, 255);
    CHECK((full.bounds() == Rect{0, 0, 48, 48}));
    // odd sizes, art touching the edges
    Px odd(37, 23);
    odd.set(0, 22, 255);
    odd.set(36, 3, 255);
    CHECK((odd.bounds() == Rect{0, 3, 37, 23}));
    Px one(1, 1, 255);
    CHECK((one.bounds() == Rect{0, 0, 1, 1}));
    Px col(1, 9);
    col.set(0, 4, 255);
    CHECK((col.bounds() == Rect{0, 4, 1, 5}));
    Px row(13, 1);
    row.set(7, 0, 255);
    CHECK((row.bounds() == Rect{7, 0, 8, 1}));
    Px wide(255, 3);
    wide.set(254, 1, 99);
    wide.set(100, 2, 99);
    CHECK((wide.bounds() == Rect{100, 1, 255, 3}));
    // a row in the middle that widens the box after narrower rows
    Px grow(20, 20);
    grow.fill(9, 2, 11, 18, 255);
    grow.set(1, 10, 255);
    grow.set(18, 15, 255);
    CHECK((grow.bounds() == Rect{1, 2, 19, 18}));
    // nothing to read
    CHECK((opaque_bounds(nullptr, 8, 8) == Rect{0, 0, 8, 8}));
    CHECK((opaque_bounds(icon.rgba.data(), 0, 5) == Rect{0, 0, 0, 5}));
    // random images against the full scan
    std::mt19937 rng(29016);
    for (int n = 0; n < 400; ++n) {
        const int w = 1 + static_cast<int>(rng() % 70), h = 1 + static_cast<int>(rng() % 70);
        Px p(w, h);
        const int dots = static_cast<int>(rng() % 6);
        for (int d = 0; d < dots; ++d) p.set(static_cast<int>(rng() % w), static_cast<int>(rng() % h), static_cast<uint8_t>(rng() % 256));
        const uint8_t t = static_cast<uint8_t>(rng() % 3 == 0 ? rng() % 256 : kOpaqueAlpha);
        if (!(p.bounds(t) == scan(p, t))) {
            CHECK(false);
            break;
        }
    }
}

void uvs() {
    const Uv u = uv_of(Rect{64, 40, 200, 230}, 256, 256);
    CHECK(near(u.u0, 0.25f) && near(u.v0, 40 / 256.f) && near(u.u1, 200 / 256.f) && near(u.v1, 230 / 256.f));
    const Uv whole = uv_of(Rect{0, 0, 37, 23}, 37, 23);
    CHECK(near(whole.u0, 0) && near(whole.v0, 0) && near(whole.u1, 1) && near(whole.v1, 1));
    const Uv none = uv_of(Rect{}, 0, 0);
    CHECK(near(none.u1, 1) && near(none.v1, 1));
}

void fits() {
    // a wide source in a square box: full width, centred vertically
    Placed p = fit(40, 40, 136, 68);
    CHECK(near(p.w, 40) && near(p.h, 20) && near(p.x, 0) && near(p.y, 10));
    // a tall one: full height, centred horizontally
    p = fit(40, 40, 30, 120);
    CHECK(near(p.w, 10) && near(p.h, 40) && near(p.x, 15) && near(p.y, 0));
    // same aspect: the box
    p = fit(48, 32, 3, 2);
    CHECK(near(p.w, 48) && near(p.h, 32) && near(p.x, 0) && near(p.y, 0));
    // a small source is scaled up (a trimmed icon fills the box)
    p = fit(40, 40, 8, 8);
    CHECK(near(p.w, 40) && near(p.h, 40));
    // nothing to fit (no size, NaN): the box itself
    p = fit(40, 30, 0, 10);
    CHECK(near(p.w, 40) && near(p.h, 30) && near(p.x, 0));
    p = fit(40, 30, NAN, 10);
    CHECK(near(p.w, 40) && near(p.h, 30));
}
}  // namespace

int main() {
    bounds();
    uvs();
    fits();
    std::printf("image_rect_test: %d checks, %d failed\n", g_checks, g_fail);
    return g_fail ? 1 : 0;
}
