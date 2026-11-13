// SPDX-License-Identifier: MIT
// PNG -> BC7 mip chain -> keen::RenderTextureResource. Every texture a vanilla VFX names is BC7 or BC5 and bindless, so that is what this
// writes: one Texture2D, full mip chain, largest level first (docs/re/vfx.md 2.7). Encoder: bc7enc (native/third_party, as kiln.assets).
#define STB_IMAGE_STATIC
#define STB_IMAGE_IMPLEMENTATION
#define STBI_ONLY_PNG
#define STBI_NO_STDIO
#include "stb/stb_image.h"

#include "bc7enc/bc7enc.h"
#include "bc7enc/rgbcx.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <mutex>
#include <thread>

#include "vfxcook.h"

namespace kvb {
namespace {

constexpr uint32_t kCategoryTexture = 4010512636u;              // content.category on all 145 textures vanilla VFX name
constexpr uint8_t kTexture2D = 1;                               // keen::TextureType

struct Image {
    uint32_t w = 0, h = 0;
    Bytes px;  // RGBA8, top row first
};

float to_linear(uint8_t v) {
    const float c = static_cast<float>(v) / 255.0f;
    return c <= 0.04045f ? c / 12.92f : std::pow((c + 0.055f) / 1.055f, 2.4f);
}
uint8_t to_srgb(float l) {
    l = std::clamp(l, 0.0f, 1.0f);
    const float c = l <= 0.0031308f ? l * 12.92f : 1.055f * std::pow(l, 1.0f / 2.4f) - 0.055f;
    return static_cast<uint8_t>(std::lround(c * 255.0f));
}

Image half(const Image& s, bool srgb) {
    Image d;
    d.w = std::max(1u, s.w / 2);
    d.h = std::max(1u, s.h / 2);
    d.px.resize(size_t{d.w} * d.h * 4);
    for (uint32_t y = 0; y < d.h; ++y)
        for (uint32_t x = 0; x < d.w; ++x) {
            float acc[4] = {0, 0, 0, 0};
            for (uint32_t dy = 0; dy < 2; ++dy)
                for (uint32_t dx = 0; dx < 2; ++dx) {
                    const uint32_t sx = std::min(s.w - 1, x * 2 + dx), sy = std::min(s.h - 1, y * 2 + dy);
                    const uint8_t* p = &s.px[(size_t{sy} * s.w + sx) * 4];
                    for (int c = 0; c < 3; ++c) acc[c] += srgb ? to_linear(p[c]) : static_cast<float>(p[c]) / 255.0f;
                    acc[3] += static_cast<float>(p[3]) / 255.0f;
                }
            uint8_t* q = &d.px[(size_t{y} * d.w + x) * 4];
            for (int c = 0; c < 3; ++c)
                q[c] = srgb ? to_srgb(acc[c] / 4) : static_cast<uint8_t>(std::lround(std::clamp(acc[c] / 4, 0.0f, 1.0f) * 255.0f));
            q[3] = static_cast<uint8_t>(std::lround(std::clamp(acc[3] / 4, 0.0f, 1.0f) * 255.0f));
        }
    return d;
}

void encode_level(const Image& img, uint8_t* dst, uint8_t format = kFormatBc7Srgb) {
    static std::once_flag once;
    std::call_once(once, [] {
        bc7enc_compress_block_init();
        rgbcx::init(rgbcx::bc1_approx_mode::cBC1Ideal);
    });
    bc7enc_compress_block_params params;
    bc7enc_compress_block_params_init(&params);
    params.m_uber_level = 1;
    params.m_max_partitions = 16;
    const uint32_t bw = (img.w + 3) / 4, bh = (img.h + 3) / 4;
    const uint32_t block_bytes = format_block_bytes(format);
    std::atomic<uint32_t> next{0};
    auto work = [&] {
        for (uint32_t by; (by = next.fetch_add(1)) < bh;)
            for (uint32_t bx = 0; bx < bw; ++bx) {
                uint8_t block[16 * 4];
                for (uint32_t y = 0; y < 4; ++y)
                    for (uint32_t x = 0; x < 4; ++x) {
                        const uint32_t sx = std::min(img.w - 1, bx * 4 + x), sy = std::min(img.h - 1, by * 4 + y);
                        std::memcpy(&block[(y * 4 + x) * 4], &img.px[(size_t{sy} * img.w + sx) * 4], 4);
                    }
                uint8_t* out = dst + (size_t{by} * bw + bx) * block_bytes;
                if (format == kFormatBc1Unorm || format == kFormatBc1Srgb) rgbcx::encode_bc1(10, out, block, false, false);
                else if (format == kFormatBc4Unorm) rgbcx::encode_bc4(out, block, 4);
                else if (format == kFormatBc5Unorm) rgbcx::encode_bc5(out, block, 0, 1, 4);
                else bc7enc_compress_block(out, block, &params);
            }
    };
    const unsigned threads = bh < 16 ? 1u : std::clamp(std::thread::hardware_concurrency(), 1u, 8u);
    std::vector<std::thread> pool;
    for (unsigned i = 1; i < threads; ++i) pool.emplace_back(work);
    work();
    for (auto& t : pool) t.join();
}

uint32_t level_bytes(uint32_t w, uint32_t h, uint32_t block = 16) { return ((w + 3) / 4) * ((h + 3) / 4) * block; }

bool power_of_two(uint32_t x) { return x && !(x & (x - 1)); }

}  // namespace

bool decode_png(const Bytes& png, uint32_t max_side, RawImage* out, std::string* why) {
    int iw = 0, ih = 0, comp = 0;
    if (png.size() < 8 || std::memcmp(png.data(), "\x89PNG\r\n\x1a\n", 8) != 0 ||
        !stbi_info_from_memory(png.data(), static_cast<int>(png.size()), &iw, &ih, &comp)) {
        *why = "not a PNG";
        return false;
    }
    const uint32_t w = static_cast<uint32_t>(iw), h = static_cast<uint32_t>(ih);
    if (!power_of_two(w) || !power_of_two(h)) {
        *why = std::to_string(w) + "x" + std::to_string(h) + " is not a power of two on both sides";
        return false;
    }
    if (std::min(w, h) < kMinTextureSide || std::max(w, h) > max_side) {
        *why = std::to_string(w) + "x" + std::to_string(h) + " is outside " + std::to_string(kMinTextureSide) + ".." + std::to_string(max_side);
        return false;
    }
    int dw = 0, dh = 0, dc = 0;
    stbi_uc* px = stbi_load_from_memory(png.data(), static_cast<int>(png.size()), &dw, &dh, &dc, 4);
    if (!px) {
        *why = std::string("not a readable PNG: ") + stbi_failure_reason();
        return false;
    }
    out->w = w;
    out->h = h;
    out->px.assign(px, px + size_t{w} * h * 4);
    stbi_image_free(px);
    return true;
}

RawImage solid_image(uint8_t r, uint8_t g, uint8_t b, uint8_t a) {
    RawImage d;
    d.w = d.h = kSolidTextureSide;
    d.px.resize(size_t{d.w} * d.h * 4);
    for (size_t i = 0; i < d.px.size(); i += 4) {
        d.px[i] = r;
        d.px[i + 1] = g;
        d.px[i + 2] = b;
        d.px[i + 3] = a;
    }
    return d;
}

uint8_t linear_to_srgb_byte(float linear) { return to_srgb(linear); }

void multiply_srgb(RawImage* image, const float linear[3]) {
    if (linear[0] == 1 && linear[1] == 1 && linear[2] == 1) return;
    uint8_t table[3][256];
    for (int c = 0; c < 3; ++c)
        for (int v = 0; v < 256; ++v) table[c][v] = to_srgb(to_linear(static_cast<uint8_t>(v)) * linear[c]);
    for (size_t i = 0; i < image->px.size(); i += 4)
        for (int c = 0; c < 3; ++c) image->px[i + c] = table[c][image->px[i + c]];
}

RawImage resize_image(const RawImage& s, uint32_t w, uint32_t h) {
    RawImage d;
    d.w = w;
    d.h = h;
    d.px.resize(size_t{w} * h * 4);
    for (uint32_t y = 0; y < h; ++y) {
        const float fy = (static_cast<float>(y) + 0.5f) * static_cast<float>(s.h) / static_cast<float>(h) - 0.5f;
        const uint32_t y0 = static_cast<uint32_t>(std::clamp(std::floor(fy), 0.0f, static_cast<float>(s.h - 1)));
        const uint32_t y1 = std::min(y0 + 1, s.h - 1);
        const float ty = std::clamp(fy - static_cast<float>(y0), 0.0f, 1.0f);
        for (uint32_t x = 0; x < w; ++x) {
            const float fx = (static_cast<float>(x) + 0.5f) * static_cast<float>(s.w) / static_cast<float>(w) - 0.5f;
            const uint32_t x0 = static_cast<uint32_t>(std::clamp(std::floor(fx), 0.0f, static_cast<float>(s.w - 1)));
            const uint32_t x1 = std::min(x0 + 1, s.w - 1);
            const float tx = std::clamp(fx - static_cast<float>(x0), 0.0f, 1.0f);
            for (int c = 0; c < 4; ++c) {
                const auto at = [&](uint32_t ax, uint32_t ay) { return static_cast<float>(s.px[(size_t{ay} * s.w + ax) * 4 + c]); };
                const float top = at(x0, y0) + (at(x1, y0) - at(x0, y0)) * tx;
                const float bottom = at(x0, y1) + (at(x1, y1) - at(x0, y1)) * tx;
                d.px[(size_t{y} * w + x) * 4 + c] = static_cast<uint8_t>(std::lround(top + (bottom - top) * ty));
            }
        }
    }
    return d;
}

namespace {
// The levels of a w x h chain of `format`: each level's size, and their total.
std::vector<uint32_t> chain_levels(uint32_t w, uint32_t h, uint8_t format, uint64_t* total) {
    const uint32_t block = format_block_bytes(format);
    std::vector<uint32_t> sizes;
    *total = 0;
    for (uint32_t lw = w, lh = h;; lw = std::max(1u, lw / 2), lh = std::max(1u, lh / 2)) {
        sizes.push_back(level_bytes(lw, lh, block));
        *total += sizes.back();
        if (lw == 1 && lh == 1) break;
    }
    return sizes;
}

// One range per level with more than 4 KiB left to the end of the blob, each running to the end. What no range starts at is the mip tail. It holds for
// all 13,207 images of the game's materials, as for VFX textures (docs/re/vfx.md 2.7, 5).
void chain_ranges(EncodedChain* c, const std::vector<uint32_t>& sizes, uint64_t total) {
    uint64_t cum = 0;
    c->ranges.clear();
    c->tail_start = 0;
    for (uint32_t s : sizes) {
        if (total - cum > 4096) {
            c->ranges.push_back({static_cast<uint32_t>(cum), static_cast<uint32_t>(total - cum)});
            c->tail_start = static_cast<size_t>(cum + s);
        }
        cum += s;
    }
}
}  // namespace

std::string chain_cache_key(const ContentHash& pixels, uint32_t w, uint32_t h, uint8_t format) {
    return content_hash_text(pixels) + "-" + std::to_string(w) + "x" + std::to_string(h) + "-f" + std::to_string(format) + "-" + kEncoderVersion;
}

EncodedChain encode_chain(const RawImage& image, uint8_t format, const TextureCache& cache) {
    EncodedChain c;
    c.width = image.w;
    c.height = image.h;
    c.format = format;
    uint64_t total = 0;
    const std::vector<uint32_t> sizes = chain_levels(image.w, image.h, format, &total);
    c.levels = static_cast<uint32_t>(sizes.size());
    const ContentHash pixels = content_hash(image.px.data(), image.px.size());
    const std::string key = chain_cache_key(pixels, image.w, image.h, format);
    c.cache_key = key;
    if (!cache.get || !cache.get(key, &c.data) || c.data.size() != total) {
        c.data.assign(total, 0);
        Image img;
        img.w = image.w;
        img.h = image.h;
        img.px = image.px;
        size_t at = 0;
        for (size_t level = 0; level < sizes.size(); ++level) {
            encode_level(img, c.data.data() + at, format);
            at += sizes[level];
            if (level + 1 < sizes.size()) img = half(img, format_srgb(format));
        }
        if (cache.put) cache.put(key, c.data);
    }
    chain_ranges(&c, sizes, total);
    return c;
}

bool chain_from_cache(const std::string& key, uint32_t w, uint32_t h, uint8_t format, const TextureCache& cache, EncodedChain* out) {
    if (!cache.get || w < 1 || h < 1 || w > (1u << 15) || h > (1u << 15)) return false;
    if (format != kFormatBc1Unorm && format != kFormatBc1Srgb && format != kFormatBc4Unorm && format != kFormatBc5Unorm && format != kFormatBc7Unorm && format != kFormatBc7Srgb) return false;
    EncodedChain c;
    c.width = w;
    c.height = h;
    c.format = format;
    c.cache_key = key;
    uint64_t total = 0;
    const std::vector<uint32_t> sizes = chain_levels(w, h, format, &total);
    c.levels = static_cast<uint32_t>(sizes.size());
    if (!cache.get(key, &c.data) || c.data.size() != total) return false;
    chain_ranges(&c, sizes, total);
    *out = std::move(c);
    return true;
}

bool build_texture(const Bytes& png, const std::string& mod, const std::string& rel, bool linear, const TextureCache& cache, BuiltTexture* out,
                   std::string* why) {
    int iw = 0, ih = 0, comp = 0;
    if (png.size() < 8 || std::memcmp(png.data(), "\x89PNG\r\n\x1a\n", 8) != 0 ||
        !stbi_info_from_memory(png.data(), static_cast<int>(png.size()), &iw, &ih, &comp)) {
        *why = "not a PNG";
        return false;
    }
    const uint32_t w = static_cast<uint32_t>(iw), h = static_cast<uint32_t>(ih);
    if (!power_of_two(w) || !power_of_two(h)) {
        *why = std::to_string(w) + "x" + std::to_string(h) + " is not a power of two on both sides";
        return false;
    }
    if (std::min(w, h) < kMinTextureSide || std::max(w, h) > kMaxTextureSide) {
        *why = std::to_string(w) + "x" + std::to_string(h) + " is outside " + std::to_string(kMinTextureSide) + ".." + std::to_string(kMaxTextureSide);
        return false;
    }
    // sizes
    std::vector<uint32_t> sizes;
    uint64_t total = 0;
    for (uint32_t lw = w, lh = h;; lw = std::max(1u, lw / 2), lh = std::max(1u, lh / 2)) {
        sizes.push_back(level_bytes(lw, lh));
        total += sizes.back();
        if (lw == 1 && lh == 1) break;
    }

    const std::string key = content_hash_text(content_hash(png.data(), png.size())) + (linear ? "-unorm-" : "-srgb-") + kEncoderVersion;
    Bytes chain;
    if (!cache.get || !cache.get(key, &chain) || chain.size() != total) {
        int dw = 0, dh = 0, dc = 0;
        stbi_uc* px = stbi_load_from_memory(png.data(), static_cast<int>(png.size()), &dw, &dh, &dc, 4);
        if (!px) {
            *why = std::string("not a readable PNG: ") + stbi_failure_reason();
            return false;
        }
        Image img;
        img.w = static_cast<uint32_t>(dw);
        img.h = static_cast<uint32_t>(dh);
        img.px.assign(px, px + size_t{img.w} * img.h * 4);
        stbi_image_free(px);
        chain.assign(total, 0);
        size_t at = 0;
        for (size_t level = 0; level < sizes.size(); ++level) {
            encode_level(img, chain.data() + at);
            at += sizes[level];
            if (level + 1 < sizes.size()) img = half(img, !linear);
        }
        if (cache.put) cache.put(key, chain);
    }

    // The resource (72 B). mipChainContentRanges: one range per level with more than 4 KiB left to its end, each running to the end of the
    // blob. It holds for every vanilla 2D texture with a mip chain (docs/re/vfx.md 2.7).
    const ContentHash hash = content_hash(chain.data(), chain.size());
    struct R {
        uint32_t start, count;
    };
    std::vector<R> ranges;
    uint64_t cum = 0;
    for (uint32_t s : sizes) {
        if (total - cum > 4096) ranges.push_back({static_cast<uint32_t>(cum), static_cast<uint32_t>(total - cum)});
        cum += s;
    }
    std::string stem = rel;
    const size_t slash = stem.find_last_of("/\\");
    if (slash != std::string::npos) stem.erase(0, slash + 1);
    const size_t dot = stem.find_last_of('.');
    if (dot != std::string::npos) stem.erase(dot);
    const std::string debug = "kiln_" + mod + "_" + stem;

    Bytes r(72, 0);
    auto u16 = [&](size_t at, uint16_t v) { std::memcpy(&r[at], &v, 2); };
    auto u32 = [&](size_t at, uint32_t v) { std::memcpy(&r[at], &v, 4); };
    u16(0x00, static_cast<uint16_t>(w));
    u16(0x02, static_cast<uint16_t>(h));
    u16(0x04, 1);
    u16(0x06, 1);
    r[0x08] = static_cast<uint8_t>(sizes.size());
    r[0x09] = kTexture2D;
    r[0x0a] = linear ? kFormatBc7Unorm : kFormatBc7Srgb;
    // channelMapping 0x0b: Identity x 4
    u32(0x10, hash.size);
    u32(0x14, hash.h0);
    u32(0x18, hash.h1);
    u32(0x1c, hash.h2);
    u32(0x20, kCategoryTexture);
    // bundleId, sortKey 0
    if (!ranges.empty()) {
        const size_t at = r.size();
        r.resize(at + ranges.size() * 8);
        u32(0x30, static_cast<uint32_t>(at - 0x30));
        u32(0x34, static_cast<uint32_t>(ranges.size()));
        std::memcpy(&r[at], ranges.data(), ranges.size() * 8);
    }
    r[0x38] = 1;  // bindless: the image patch point writes a bindless index
    {
        const size_t at = r.size();
        r.insert(r.end(), debug.begin(), debug.end());
        u32(0x3c, static_cast<uint32_t>(at - 0x3c));
        u32(0x40, static_cast<uint32_t>(debug.size()));
    }

    out->guid = block_id(mod, "vfx-texture", rel + (linear ? "#linear" : ""));
    out->resource = std::move(r);
    out->blob = std::move(chain);
    out->hash = hash;
    out->width = w;
    out->height = h;
    out->levels = static_cast<uint32_t>(sizes.size());
    return true;
}

}  // namespace kvb
