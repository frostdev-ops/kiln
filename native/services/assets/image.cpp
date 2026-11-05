// SPDX-License-Identifier: MIT
// kiln.assets: PNG decoding (stb_image, MIT/public domain), resizing, mip chains and encoding to the game's pixel
// formats (RGBA8; BC1/BC3/BC4/BC5 with rgbcx and BC7 with bc7enc, both from richgel999/bc7enc_rdo, MIT/Unlicense).
// Pixel format numbers are keen::PixelFormat (the game's reflection; docs/re/resources.md).
#define STB_IMAGE_IMPLEMENTATION
#define STBI_ONLY_PNG
#define STBI_NO_STDIO
#include "stb/stb_image.h"

#include "bc7enc/bc7enc.h"
#include "bc7enc/rgbcx.h"

#include <algorithm>
#include <cmath>
#include <mutex>

#include "assets.h"

namespace ka {

namespace {
enum PF : uint32_t {
    R8G8B8A8_unorm = 37,
    R8G8B8A8_srgb = 43,
    B8G8R8A8_unorm = 44,
    B8G8R8A8_srgb = 50,
    BC1_RGB_unorm = 131,
    BC1_RGB_srgb = 132,
    BC1_RGBA_unorm = 133,
    BC1_RGBA_srgb = 134,
    BC3_unorm = 137,
    BC3_srgb = 138,
    BC4_unorm = 139,
    BC4_snorm = 140,
    BC5_unorm = 141,
    BC5_snorm = 142,
    BC7_unorm = 145,
    BC7_srgb = 146,
};

bool is_block(uint32_t f) { return f >= 131 && f <= 146; }
uint32_t block_bytes(uint32_t f) {
    switch (f) {
        case BC1_RGB_unorm: case BC1_RGB_srgb: case BC1_RGBA_unorm: case BC1_RGBA_srgb: case BC4_unorm: case BC4_snorm:
            return 8;
        default:
            return 16;
    }
}

std::once_flag g_init;
void init_encoders() {
    std::call_once(g_init, [] {
        rgbcx::init(rgbcx::bc1_approx_mode::cBC1Ideal);
        bc7enc_compress_block_init();
    });
}

// sRGB-aware 2x2 box filter for the next mip (alpha linear).
float to_lin(uint8_t v) {
    const float c = v / 255.0f;
    return c <= 0.04045f ? c / 12.92f : std::pow((c + 0.055f) / 1.055f, 2.4f);
}
uint8_t to_srgb(float l) {
    l = std::clamp(l, 0.0f, 1.0f);
    const float c = l <= 0.0031308f ? l * 12.92f : 1.055f * std::pow(l, 1.0f / 2.4f) - 0.055f;
    return static_cast<uint8_t>(std::lround(c * 255.0f));
}
Rgba half(const Rgba& s, bool srgb) {
    Rgba d;
    d.w = std::max(1u, s.w / 2);
    d.h = std::max(1u, s.h / 2);
    d.px.resize(static_cast<size_t>(d.w) * d.h * 4);
    for (uint32_t y = 0; y < d.h; ++y)
        for (uint32_t x = 0; x < d.w; ++x) {
            float acc[4] = {0, 0, 0, 0};
            int n = 0;
            for (uint32_t dy = 0; dy < 2; ++dy)
                for (uint32_t dx = 0; dx < 2; ++dx) {
                    const uint32_t sx = std::min(s.w - 1, x * 2 + dx), sy = std::min(s.h - 1, y * 2 + dy);
                    const uint8_t* p = &s.px[(static_cast<size_t>(sy) * s.w + sx) * 4];
                    for (int c = 0; c < 3; ++c) acc[c] += srgb ? to_lin(p[c]) : p[c] / 255.0f;
                    acc[3] += p[3] / 255.0f;
                    ++n;
                }
            uint8_t* q = &d.px[(static_cast<size_t>(y) * d.w + x) * 4];
            for (int c = 0; c < 3; ++c)
                q[c] = srgb ? to_srgb(acc[c] / n) : static_cast<uint8_t>(std::lround(std::clamp(acc[c] / n, 0.0f, 1.0f) * 255.0f));
            q[3] = static_cast<uint8_t>(std::lround(std::clamp(acc[3] / n, 0.0f, 1.0f) * 255.0f));
        }
    return d;
}

void encode_level(const Rgba& img, uint32_t f, std::vector<uint8_t>& out) {
    if (!is_block(f)) {
        const bool bgra = f == B8G8R8A8_unorm || f == B8G8R8A8_srgb;
        const size_t at = out.size();
        out.insert(out.end(), img.px.begin(), img.px.end());
        if (bgra)
            for (size_t i = at; i < out.size(); i += 4) std::swap(out[i], out[i + 2]);
        return;
    }
    const uint32_t bw = (img.w + 3) / 4, bh = (img.h + 3) / 4, bb = block_bytes(f);
    const size_t at = out.size();
    out.resize(at + static_cast<size_t>(bw) * bh * bb);
    bc7enc_compress_block_params p7;
    if (f == BC7_unorm || f == BC7_srgb) {
        bc7enc_compress_block_params_init(&p7);
        p7.m_uber_level = 1;
        p7.m_max_partitions = 16;
    }
    uint8_t blk[16 * 4];
    for (uint32_t by = 0; by < bh; ++by)
        for (uint32_t bx = 0; bx < bw; ++bx) {
            for (uint32_t y = 0; y < 4; ++y)
                for (uint32_t x = 0; x < 4; ++x) {
                    const uint32_t sx = std::min(img.w - 1, bx * 4 + x), sy = std::min(img.h - 1, by * 4 + y);
                    std::memcpy(&blk[(y * 4 + x) * 4], &img.px[(static_cast<size_t>(sy) * img.w + sx) * 4], 4);
                }
            uint8_t* dst = &out[at + (static_cast<size_t>(by) * bw + bx) * bb];
            switch (f) {
                case BC1_RGB_unorm: case BC1_RGB_srgb:
                    rgbcx::encode_bc1(10, dst, blk, false, false);
                    break;
                case BC1_RGBA_unorm: case BC1_RGBA_srgb:
                    rgbcx::encode_bc1(10, dst, blk, true, true);
                    break;
                case BC3_unorm: case BC3_srgb:
                    rgbcx::encode_bc3(10, dst, blk);
                    break;
                case BC4_unorm: case BC4_snorm:
                    rgbcx::encode_bc4(dst, blk, 4);
                    break;
                case BC5_unorm: case BC5_snorm:
                    rgbcx::encode_bc5(dst, blk, 0, 1, 4);
                    break;
                default:  // BC7
                    bc7enc_compress_block(dst, blk, &p7);
                    break;
            }
        }
}
}  // namespace

bool format_is_srgb(uint32_t f) {
    return f == 15 || f == 22 || f == 29 || f == 36 || f == R8G8B8A8_srgb || f == B8G8R8A8_srgb || f == 57 ||
           f == BC1_RGB_srgb || f == BC1_RGBA_srgb || f == 136 || f == BC3_srgb || f == BC7_srgb;
}

const char* pixel_format_name(uint32_t f) {
    switch (f) {
        case R8G8B8A8_unorm: return "R8G8B8A8_unorm";
        case R8G8B8A8_srgb: return "R8G8B8A8_srgb";
        case B8G8R8A8_unorm: return "B8G8R8A8_unorm";
        case B8G8R8A8_srgb: return "B8G8R8A8_srgb";
        case BC1_RGB_unorm: return "BC1_RGB_unorm";
        case BC1_RGB_srgb: return "BC1_RGB_srgb";
        case BC1_RGBA_unorm: return "BC1_RGBA_unorm";
        case BC1_RGBA_srgb: return "BC1_RGBA_srgb";
        case BC3_unorm: return "BC3_unorm";
        case BC3_srgb: return "BC3_srgb";
        case BC4_unorm: return "BC4_unorm";
        case BC4_snorm: return "BC4_snorm";
        case BC5_unorm: return "BC5_unorm";
        case BC5_snorm: return "BC5_snorm";
        case 143: return "BC6H_ufloat";
        case 144: return "BC6H_sfloat";
        case BC7_unorm: return "BC7_unorm";
        case BC7_srgb: return "BC7_srgb";
        default: return "other";
    }
}

size_t texture_bytes(uint32_t w, uint32_t h, uint32_t f, uint32_t levels) {
    const bool rgba = f == R8G8B8A8_unorm || f == R8G8B8A8_srgb || f == B8G8R8A8_unorm || f == B8G8R8A8_srgb;
    const bool known = rgba || (is_block(f) && f != 135 && f != 136 && f != 143 && f != 144);
    if (!known || !w || !h || !levels) return 0;
    size_t total = 0;
    for (uint32_t l = 0; l < levels; ++l) {
        const uint32_t lw = std::max(1u, w >> l), lh = std::max(1u, h >> l);
        total += rgba ? static_cast<size_t>(lw) * lh * 4
                      : static_cast<size_t>((lw + 3) / 4) * ((lh + 3) / 4) * block_bytes(f);
    }
    return total;
}

bool png_decode(const void* data, size_t len, Rgba* out, std::string* why) {
    if (!data || len < 8 || len > 64u * 1024 * 1024) {
        *why = "not a PNG (size)";
        return false;
    }
    int w = 0, h = 0, n = 0;
    stbi_uc* px = stbi_load_from_memory(static_cast<const stbi_uc*>(data), static_cast<int>(len), &w, &h, &n, 4);
    if (!px) {
        *why = std::string("PNG decode failed: ") + (stbi_failure_reason() ? stbi_failure_reason() : "?");
        return false;
    }
    if (w <= 0 || h <= 0 || w > 4096 || h > 4096) {
        stbi_image_free(px);
        *why = "PNG larger than 4096x4096";
        return false;
    }
    out->w = static_cast<uint32_t>(w);
    out->h = static_cast<uint32_t>(h);
    out->px.assign(px, px + static_cast<size_t>(w) * h * 4);
    stbi_image_free(px);
    return true;
}

// Bilinear (upscale) / box (downscale) resize in 8-bit space; good enough for icons and retextures.
Rgba resize(const Rgba& s, uint32_t w, uint32_t h) {
    if (s.w == w && s.h == h) return s;
    Rgba d;
    d.w = w, d.h = h;
    d.px.resize(static_cast<size_t>(w) * h * 4);
    const float sx = static_cast<float>(s.w) / w, sy = static_cast<float>(s.h) / h;
    for (uint32_t y = 0; y < h; ++y)
        for (uint32_t x = 0; x < w; ++x) {
            float acc[4] = {0, 0, 0, 0};
            if (sx > 1.0f || sy > 1.0f) {  // box over the source footprint
                const uint32_t x0 = static_cast<uint32_t>(x * sx), x1 = std::max(x0 + 1, static_cast<uint32_t>((x + 1) * sx));
                const uint32_t y0 = static_cast<uint32_t>(y * sy), y1 = std::max(y0 + 1, static_cast<uint32_t>((y + 1) * sy));
                int n = 0;
                for (uint32_t yy = y0; yy < std::min(y1, s.h); ++yy)
                    for (uint32_t xx = x0; xx < std::min(x1, s.w); ++xx, ++n)
                        for (int c = 0; c < 4; ++c) acc[c] += s.px[(static_cast<size_t>(yy) * s.w + xx) * 4 + c];
                for (float& a : acc) a /= std::max(1, n);
            } else {  // bilinear
                const float fx = std::max(0.0f, (x + 0.5f) * sx - 0.5f), fy = std::max(0.0f, (y + 0.5f) * sy - 0.5f);
                const uint32_t x0 = std::min(s.w - 1, static_cast<uint32_t>(fx)), y0 = std::min(s.h - 1, static_cast<uint32_t>(fy));
                const uint32_t x1 = std::min(s.w - 1, x0 + 1), y1 = std::min(s.h - 1, y0 + 1);
                const float tx = fx - x0, ty = fy - y0;
                for (int c = 0; c < 4; ++c) {
                    auto p = [&](uint32_t xx, uint32_t yy) { return static_cast<float>(s.px[(static_cast<size_t>(yy) * s.w + xx) * 4 + c]); };
                    acc[c] = (p(x0, y0) * (1 - tx) + p(x1, y0) * tx) * (1 - ty) + (p(x0, y1) * (1 - tx) + p(x1, y1) * tx) * ty;
                }
            }
            for (int c = 0; c < 4; ++c)
                d.px[(static_cast<size_t>(y) * w + x) * 4 + c] = static_cast<uint8_t>(std::lround(std::clamp(acc[c], 0.0f, 255.0f)));
        }
    return d;
}

std::vector<uint8_t> encode_texture(const Rgba& img, uint32_t format, uint32_t levels, std::string* why) {
    std::vector<uint8_t> out;
    const size_t want = texture_bytes(img.w, img.h, format, levels);
    if (!want) {
        *why = std::string("pixel format ") + std::to_string(format) + " (" + pixel_format_name(format) + ") is not supported";
        return out;
    }
    init_encoders();
    out.reserve(want);
    Rgba cur = img;
    const bool srgb = format_is_srgb(format);
    for (uint32_t l = 0; l < levels; ++l) {
        if (l) cur = half(cur, srgb);
        encode_level(cur, format, out);
    }
    if (out.size() != want) {
        *why = "encoder size mismatch";
        out.clear();
    }
    return out;
}

}  // namespace ka
