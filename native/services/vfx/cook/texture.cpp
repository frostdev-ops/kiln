// SPDX-License-Identifier: MIT
// PNG -> BC7 mip chain -> keen::RenderTextureResource. Every texture a vanilla VFX names is BC7 or BC5 and bindless, so that is what this
// writes: one Texture2D, full mip chain, largest level first (docs/re/vfx.md 2.7). Encoder: bc7enc (native/third_party, as kiln.assets).
#define STB_IMAGE_STATIC
#define STB_IMAGE_IMPLEMENTATION
#define STBI_ONLY_PNG
#define STBI_NO_STDIO
#include "stb/stb_image.h"

#include "bc7enc/bc7enc.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <mutex>
#include <thread>

#include "vfxcook.h"

namespace kvb {
namespace {

constexpr uint8_t kFormatBc7Unorm = 145, kFormatBc7Srgb = 146;  // keen::PixelFormat
constexpr uint32_t kCategoryTexture = 4010512636u;              // content.category on all 145 textures vanilla VFX name
constexpr uint8_t kTexture2D = 1;                               // keen::TextureType
constexpr const char* kEncoderVersion = "bc7-1";                // part of the cache key

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

void encode_level(const Image& img, uint8_t* dst) {
    static std::once_flag once;
    std::call_once(once, [] { bc7enc_compress_block_init(); });
    bc7enc_compress_block_params params;
    bc7enc_compress_block_params_init(&params);
    params.m_uber_level = 1;
    params.m_max_partitions = 16;
    const uint32_t bw = (img.w + 3) / 4, bh = (img.h + 3) / 4;
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
                bc7enc_compress_block(dst + (size_t{by} * bw + bx) * 16, block, &params);
            }
    };
    const unsigned threads = bh < 16 ? 1u : std::clamp(std::thread::hardware_concurrency(), 1u, 8u);
    std::vector<std::thread> pool;
    for (unsigned i = 1; i < threads; ++i) pool.emplace_back(work);
    work();
    for (auto& t : pool) t.join();
}

uint32_t level_bytes(uint32_t w, uint32_t h) { return ((w + 3) / 4) * ((h + 3) / 4) * 16; }

bool power_of_two(uint32_t x) { return x && !(x & (x - 1)); }

}  // namespace

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
