// SPDX-License-Identifier: MIT
// kiln.ui: which part of an overlay image to draw and where (style.cpp's image_draw_ex, kiln_ui.h image_ex and
// image_opaque_rect, 0.2.0-dev.29.16). Pure: the host test (tests/image_rect_test.cpp) checks it.
//
// The game's item icons have wide transparent margins: drawn whole at 32-40 px their art is small. An image's opaque
// bounds (alpha at or above kOpaqueAlpha) are found once, when its pixels are decoded, and kept with the image; a trimmed
// draw samples only that rectangle (uv0/uv1) and fits it inside the box asked for, keeping its aspect ratio.
#pragma once
#include <cmath>
#include <cstdint>

namespace kui::img {

constexpr uint8_t kOpaqueAlpha = 16;  // below this a pixel counts as margin (BC7 noise, the faintest edge of a glow)

// Pixels, x1 / y1 exclusive.
struct Rect {
    int x0 = 0, y0 = 0, x1 = 0, y1 = 0;
    constexpr int w() const { return x1 - x0; }
    constexpr int h() const { return y1 - y0; }
    constexpr bool operator==(const Rect&) const = default;
};

// The smallest rectangle holding every pixel whose alpha is >= `threshold` (RGBA8, rows top down, w * h * 4 bytes).
// Nothing that opaque (a fully transparent image), or no pixels: the whole image.
inline Rect opaque_bounds(const uint8_t* rgba, int w, int h, uint8_t threshold = kOpaqueAlpha) {
    const Rect whole{0, 0, w > 0 ? w : 0, h > 0 ? h : 0};
    if (!rgba || w <= 0 || h <= 0) return whole;
    auto opaque = [&](int x, int y) { return rgba[(static_cast<size_t>(y) * w + x) * 4 + 3] >= threshold; };
    auto row_has = [&](int y) {
        for (int x = 0; x < w; ++x)
            if (opaque(x, y)) return true;
        return false;
    };
    int y0 = 0;
    while (y0 < h && !row_has(y0)) ++y0;
    if (y0 == h) return whole;  // nothing opaque
    int y1 = h - 1;
    while (y1 > y0 && !row_has(y1)) --y1;
    // between them a row can only widen the box: look left of it and right of it, never inside
    int x0 = w, x1 = -1;
    for (int y = y0; y <= y1; ++y) {
        for (int x = 0; x < x0; ++x)
            if (opaque(x, y)) {
                x0 = x;
                break;
            }
        for (int x = w - 1; x > x1; --x)
            if (opaque(x, y)) {
                x1 = x;
                break;
            }
    }
    return Rect{x0, y0, x1 + 1, y1 + 1};
}

struct Uv {
    float u0 = 0, v0 = 0, u1 = 1, v1 = 1;
};

// The texture coordinates of `r` in a w x h image.
inline Uv uv_of(const Rect& r, int w, int h) {
    if (w <= 0 || h <= 0) return {};
    return {static_cast<float>(r.x0) / w, static_cast<float>(r.y0) / h, static_cast<float>(r.x1) / w, static_cast<float>(r.y1) / h};
}

// Where a source of src_w x src_h goes in a box of box_w x box_h keeping its aspect ratio: as large as fits, centred.
// x, y: its offset in the box. A source of no size fills the box.
struct Placed {
    float x = 0, y = 0, w = 0, h = 0;
};
inline Placed fit(float box_w, float box_h, float src_w, float src_h) {
    if (!(src_w > 0) || !(src_h > 0) || !(box_w > 0) || !(box_h > 0)) return {0, 0, box_w, box_h};
    const float k = std::fmin(box_w / src_w, box_h / src_h);
    const float w = src_w * k, h = src_h * k;
    return {(box_w - w) * 0.5f, (box_h - h) * 0.5f, w, h};
}

}  // namespace kui::img
