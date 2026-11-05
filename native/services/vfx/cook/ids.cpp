// SPDX-License-Identifier: MIT
// Block ids: Kiln.Blocks' BlockIds in C++. A C# handle and a native boot build must agree on every GUID.
#include <cstdio>
#include <cstring>

#include "vfxcook.h"

namespace kvb {
namespace {

void sha1(const uint8_t* data, size_t len, uint8_t out[20]) {
    uint32_t h[5] = {0x67452301u, 0xEFCDAB89u, 0x98BADCFEu, 0x10325476u, 0xC3D2E1F0u};
    Bytes m(data, data + len);
    m.push_back(0x80);
    while (m.size() % 64 != 56) m.push_back(0);
    const uint64_t bits = static_cast<uint64_t>(len) * 8;
    for (int i = 7; i >= 0; --i) m.push_back(static_cast<uint8_t>(bits >> (i * 8)));
    auto rol = [](uint32_t x, int n) { return x << n | x >> (32 - n); };
    for (size_t o = 0; o < m.size(); o += 64) {
        uint32_t w[80];
        for (int t = 0; t < 16; ++t)
            w[t] = uint32_t{m[o + t * 4]} << 24 | uint32_t{m[o + t * 4 + 1]} << 16 | uint32_t{m[o + t * 4 + 2]} << 8 | uint32_t{m[o + t * 4 + 3]};
        for (int t = 16; t < 80; ++t) w[t] = rol(w[t - 3] ^ w[t - 8] ^ w[t - 14] ^ w[t - 16], 1);
        uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4];
        for (int t = 0; t < 80; ++t) {
            uint32_t f, k;
            if (t < 20) f = (b & c) | (~b & d), k = 0x5A827999u;
            else if (t < 40) f = b ^ c ^ d, k = 0x6ED9EBA1u;
            else if (t < 60) f = (b & c) | (b & d) | (c & d), k = 0x8F1BBCDCu;
            else f = b ^ c ^ d, k = 0xCA62C1D6u;
            const uint32_t x = rol(a, 5) + f + e + k + w[t];
            e = d;
            d = c;
            c = rol(b, 30);
            b = a;
            a = x;
        }
        h[0] += a;
        h[1] += b;
        h[2] += c;
        h[3] += d;
        h[4] += e;
    }
    for (int i = 0; i < 5; ++i)
        for (int j = 0; j < 4; ++j) out[i * 4 + j] = static_cast<uint8_t>(h[i] >> (24 - j * 8));
}

}  // namespace

std::string guid_text(const Guid& g) {
    char s[40];
    std::snprintf(s, sizeof s, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x", g.b[3], g.b[2], g.b[1], g.b[0], g.b[5], g.b[4], g.b[7],
                  g.b[6], g.b[8], g.b[9], g.b[10], g.b[11], g.b[12], g.b[13], g.b[14], g.b[15]);
    return s;
}

Guid block_id(const std::string& mod, const std::string& kind, const std::string& name) {
    // Namespace 6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77, big-endian as RFC 4122 hashes it.
    static const uint8_t ns[16] = {0x6f, 0x6d, 0x2a, 0x44, 0x53, 0xc1, 0x5c, 0x52, 0x9d, 0x51, 0x0a, 0x3c, 0x6e, 0x1f, 0x2b, 0x77};
    const std::string text = mod + ":" + kind + ":" + name;
    Bytes all(ns, ns + 16);
    all.insert(all.end(), text.begin(), text.end());
    uint8_t d[20];
    sha1(all.data(), all.size(), d);
    d[6] = static_cast<uint8_t>((d[6] & 0x0f) | 0x50);
    d[8] = static_cast<uint8_t>((d[8] & 0x3f) | 0x80);
    // To the game's order: the first three fields little-endian.
    Guid g;
    const uint8_t order[16] = {3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15};
    for (int i = 0; i < 16; ++i) g.b[i] = d[order[i]];
    return g;
}

}  // namespace kvb
