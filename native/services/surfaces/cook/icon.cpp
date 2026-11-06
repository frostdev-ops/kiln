// SPDX-License-Identifier: MIT
// A building material's item icon: a block of its surface seen from above at an angle, the top face lit and the two sides shaded, as the game's
// own block icons show a material. Drawn on the CPU from a small mip of each albedo, written as a PNG for kiln.assets' icon registry.
#include <algorithm>
#include <cmath>
#include <cstring>

#include "bc7enc/bc7decomp.h"
#include "surfacecook.h"

namespace ksf {
namespace {

uint32_t crc32(const uint8_t* p, size_t n, uint32_t c = 0) {
    static uint32_t table[256];
    static bool made = false;
    if (!made) {
        for (uint32_t i = 0; i < 256; ++i) {
            uint32_t k = i;
            for (int j = 0; j < 8; ++j) k = k & 1 ? 0xedb88320u ^ (k >> 1) : k >> 1;
            table[i] = k;
        }
        made = true;
    }
    c = ~c;
    for (size_t i = 0; i < n; ++i) c = table[(c ^ p[i]) & 0xff] ^ (c >> 8);
    return ~c;
}

void be32(Bytes* b, uint32_t v) {
    for (int i = 3; i >= 0; --i) b->push_back(static_cast<uint8_t>(v >> (i * 8)));
}

void chunk(Bytes* out, const char* type, const Bytes& data) {
    be32(out, static_cast<uint32_t>(data.size()));
    const size_t at = out->size();
    out->insert(out->end(), type, type + 4);
    out->insert(out->end(), data.begin(), data.end());
    be32(out, crc32(out->data() + at, data.size() + 4));
}

kvb::RawImage sample_down(const kvb::RawImage& s, uint32_t max_side) {
    if (std::max(s.w, s.h) <= max_side) return s;
    const float k = static_cast<float>(max_side) / static_cast<float>(std::max(s.w, s.h));
    return kvb::resize_image(s, std::max(1u, static_cast<uint32_t>(s.w * k)), std::max(1u, static_cast<uint32_t>(s.h * k)));
}

}  // namespace

Bytes png_rgba(uint32_t w, uint32_t h, const Bytes& px) {
    Bytes out = {0x89, 'P', 'N', 'G', '\r', '\n', 0x1a, '\n'};
    Bytes ihdr;
    be32(&ihdr, w);
    be32(&ihdr, h);
    ihdr.insert(ihdr.end(), {8, 6, 0, 0, 0});  // 8 bits, RGBA, deflate, no filter method, no interlace
    chunk(&out, "IHDR", ihdr);
    Bytes raw;
    raw.reserve(size_t{h} * (size_t{w} * 4 + 1));
    for (uint32_t y = 0; y < h; ++y) {
        raw.push_back(0);
        raw.insert(raw.end(), px.begin() + static_cast<std::ptrdiff_t>(size_t{y} * w * 4), px.begin() + static_cast<std::ptrdiff_t>(size_t{y + 1} * w * 4));
    }
    // zlib with stored deflate blocks: no compression, nothing to get wrong
    Bytes z = {0x78, 0x01};
    for (size_t at = 0; at < raw.size() || at == 0;) {
        const size_t n = std::min<size_t>(65535, raw.size() - at);
        const bool last = at + n >= raw.size();
        z.push_back(last ? 1 : 0);
        z.push_back(static_cast<uint8_t>(n));
        z.push_back(static_cast<uint8_t>(n >> 8));
        z.push_back(static_cast<uint8_t>(~n));
        z.push_back(static_cast<uint8_t>(~n >> 8));
        z.insert(z.end(), raw.begin() + static_cast<std::ptrdiff_t>(at), raw.begin() + static_cast<std::ptrdiff_t>(at + n));
        at += n;
        if (last) break;
    }
    uint32_t a = 1, b = 0;
    for (uint8_t x : raw) {
        a = (a + x) % 65521;
        b = (b + a) % 65521;
    }
    be32(&z, b << 16 | a);
    chunk(&out, "IDAT", z);
    chunk(&out, "IEND", {});
    return out;
}

bool decode_level(const Bytes& chain, uint32_t width, uint32_t height, uint8_t format, uint32_t max_side, kvb::RawImage* out) {
    if (format != kvb::kFormatBc7Srgb && format != kvb::kFormatBc7Unorm) return false;
    size_t at = 0;
    uint32_t w = width, h = height;
    while (std::max(w, h) > max_side && !(w == 1 && h == 1)) {
        at += size_t{(w + 3) / 4} * ((h + 3) / 4) * 16;
        w = std::max(1u, w / 2);
        h = std::max(1u, h / 2);
    }
    const uint32_t bw = (w + 3) / 4, bh = (h + 3) / 4;
    if (at + size_t{bw} * bh * 16 > chain.size()) return false;
    out->w = w;
    out->h = h;
    out->px.assign(size_t{w} * h * 4, 0);
    for (uint32_t by = 0; by < bh; ++by)
        for (uint32_t bx = 0; bx < bw; ++bx) {
            bc7decomp::color_rgba c[16];
            if (!bc7decomp::unpack_bc7(chain.data() + at + (size_t{by} * bw + bx) * 16, c)) return false;
            for (uint32_t y = 0; y < 4; ++y)
                for (uint32_t x = 0; x < 4; ++x) {
                    const uint32_t px = bx * 4 + x, py = by * 4 + y;
                    if (px < w && py < h) std::memcpy(&out->px[(size_t{py} * w + px) * 4], &c[y * 4 + x], 4);
                }
        }
    return true;
}

kvb::RawImage block_icon(const kvb::RawImage& side_in, const kvb::RawImage& top_in, uint32_t size) {
    const kvb::RawImage side = sample_down(side_in, 256), top = sample_down(top_in, 256);
    kvb::RawImage img;
    img.w = img.h = size;
    img.px.assign(size_t{size} * size * 4, 0);
    const float S = static_cast<float>(size), s = S * 0.40f, cx = S / 2, cy = S / 2, c30 = 0.8660254f;
    struct Face {
        float ox, oy, ax, ay, bx, by, shade;
        const kvb::RawImage* tex;
    };
    // origin, the u edge, the v edge; u across the texture, v down it
    const Face faces[3] = {
        {cx, cy - s, c30 * s, 0.5f * s, -c30 * s, 0.5f * s, 1.0f, &top},            // top: back corner, towards right, towards left
        {cx - c30 * s, cy - 0.5f * s, c30 * s, 0.5f * s, 0, s, 0.80f, &side},     // left side
        {cx, cy, c30 * s, -0.5f * s, 0, s, 0.62f, &side},                         // right side
    };
    const auto texel = [](const kvb::RawImage& t, float u, float v, int c) {
        const uint32_t x = std::min(t.w - 1, static_cast<uint32_t>(std::clamp(u, 0.0f, 0.9999f) * t.w));
        const uint32_t y = std::min(t.h - 1, static_cast<uint32_t>(std::clamp(v, 0.0f, 0.9999f) * t.h));
        return static_cast<float>(t.px[(size_t{y} * t.w + x) * 4 + c]);
    };
    for (uint32_t y = 0; y < size; ++y)
        for (uint32_t x = 0; x < size; ++x) {
            float acc[3] = {0, 0, 0}, cover = 0;
            for (int sy = 0; sy < 2; ++sy)
                for (int sx = 0; sx < 2; ++sx) {
                    const float px = static_cast<float>(x) + 0.25f + 0.5f * static_cast<float>(sx), py = static_cast<float>(y) + 0.25f + 0.5f * static_cast<float>(sy);
                    for (const Face& f : faces) {
                        const float det = f.ax * f.by - f.ay * f.bx, dx = px - f.ox, dy = py - f.oy;
                        const float u = (dx * f.by - dy * f.bx) / det, v = (f.ax * dy - f.ay * dx) / det;
                        if (u < 0 || u > 1 || v < 0 || v > 1) continue;
                        for (int c = 0; c < 3; ++c) acc[c] += texel(*f.tex, u, v, c) * f.shade;
                        cover += 1;
                        break;
                    }
                }
            if (cover == 0) continue;
            uint8_t* q = &img.px[(size_t{y} * size + x) * 4];
            for (int c = 0; c < 3; ++c) q[c] = static_cast<uint8_t>(std::lround(std::clamp(acc[c] / cover, 0.0f, 255.0f)));
            q[3] = static_cast<uint8_t>(std::lround(cover / 4 * 255));
        }
    return img;
}

}  // namespace ksf
