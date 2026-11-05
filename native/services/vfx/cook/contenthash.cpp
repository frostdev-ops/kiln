// SPDX-License-Identifier: MIT
// See contenthash.h. Four 16-byte lanes start as the bytes 00..0f, 10..1f, 20..2f, 30..3f. Every whole 64 bytes goes through one
// aesdec round per lane; the tail (len & 15 bytes, zero padded) goes into d, the remaining whole 16-byte blocks into c, b, a; the
// finish mixes the lanes with K = {u64 seed - len, u64 len + 1 + seed}. aesdec is the x86 instruction: InvShiftRows, InvSubBytes,
// InvMixColumns, then xor with the key.
#include "contenthash.h"

#include <array>
#include <cstdio>
#include <cstring>

namespace kvb {
namespace {

struct Tables {
    std::array<uint32_t, 256> t0{}, t1{}, t2{}, t3{};
};

constexpr uint8_t gmul(uint8_t a, uint8_t b) {
    uint8_t r = 0;
    while (b) {
        if (b & 1) r ^= a;
        a = static_cast<uint8_t>((a << 1) ^ ((a & 0x80) ? 0x1b : 0));
        b >>= 1;
    }
    return r;
}

constexpr uint8_t rotl8(uint8_t x, int n) { return static_cast<uint8_t>((x << n) | (x >> (8 - n))); }

constexpr Tables make_tables() {
    // The AES S-box from its definition (inverse in GF(2^8), then the affine map), then its inverse.
    std::array<uint8_t, 256> sbox{}, inv{};
    uint8_t p = 1, q = 1;
    do {
        p = static_cast<uint8_t>(p ^ (p << 1) ^ ((p & 0x80) ? 0x1b : 0));
        q = static_cast<uint8_t>(q ^ (q << 1));
        q = static_cast<uint8_t>(q ^ (q << 2));
        q = static_cast<uint8_t>(q ^ (q << 4));
        if (q & 0x80) q ^= 0x09;
        sbox[p] = static_cast<uint8_t>(q ^ rotl8(q, 1) ^ rotl8(q, 2) ^ rotl8(q, 3) ^ rotl8(q, 4) ^ 0x63);
    } while (p != 1);
    sbox[0] = 0x63;
    for (int i = 0; i < 256; ++i) inv[sbox[i]] = static_cast<uint8_t>(i);
    Tables t{};
    for (int i = 0; i < 256; ++i) {
        const uint8_t s = inv[i];
        const uint32_t v = uint32_t{gmul(s, 0x0e)} | uint32_t{gmul(s, 0x09)} << 8 | uint32_t{gmul(s, 0x0d)} << 16 | uint32_t{gmul(s, 0x0b)} << 24;
        t.t0[i] = v;
        t.t1[i] = v << 8 | v >> 24;
        t.t2[i] = v << 16 | v >> 16;
        t.t3[i] = v << 24 | v >> 8;
    }
    return t;
}

constexpr Tables kT = make_tables();

struct Lane {
    uint32_t w[4];
};

inline Lane aesdec(const Lane& s, const Lane& k) {
    Lane r;
    r.w[0] = kT.t0[s.w[0] & 0xff] ^ kT.t1[(s.w[3] >> 8) & 0xff] ^ kT.t2[(s.w[2] >> 16) & 0xff] ^ kT.t3[s.w[1] >> 24] ^ k.w[0];
    r.w[1] = kT.t0[s.w[1] & 0xff] ^ kT.t1[(s.w[0] >> 8) & 0xff] ^ kT.t2[(s.w[3] >> 16) & 0xff] ^ kT.t3[s.w[2] >> 24] ^ k.w[1];
    r.w[2] = kT.t0[s.w[2] & 0xff] ^ kT.t1[(s.w[1] >> 8) & 0xff] ^ kT.t2[(s.w[0] >> 16) & 0xff] ^ kT.t3[s.w[3] >> 24] ^ k.w[2];
    r.w[3] = kT.t0[s.w[3] & 0xff] ^ kT.t1[(s.w[2] >> 8) & 0xff] ^ kT.t2[(s.w[1] >> 16) & 0xff] ^ kT.t3[s.w[0] >> 24] ^ k.w[3];
    return r;
}

inline Lane load(const uint8_t* p) {
    Lane l;
    for (int i = 0; i < 4; ++i)
        l.w[i] = uint32_t{p[i * 4]} | uint32_t{p[i * 4 + 1]} << 8 | uint32_t{p[i * 4 + 2]} << 16 | uint32_t{p[i * 4 + 3]} << 24;
    return l;
}

Lane first_lane(uint8_t first) {
    uint8_t b[16];
    for (int i = 0; i < 16; ++i) b[i] = static_cast<uint8_t>(first + i);
    return load(b);
}

}  // namespace

void keen_hash128(const void* data, size_t len, uint8_t out[16], uint64_t seed) {
    const uint8_t* p = static_cast<const uint8_t*>(data);
    Lane a = first_lane(0), b = first_lane(16), c = first_lane(32), d = first_lane(48);
    const size_t whole = len >> 6;
    for (size_t i = 0; i < whole; ++i) {
        const uint8_t* m = p + i * 64;
        a = aesdec(a, load(m));
        b = aesdec(b, load(m + 16));
        c = aesdec(c, load(m + 32));
        d = aesdec(d, load(m + 48));
    }
    const uint8_t* o = p + whole * 64;
    const size_t rem = len & 0x30, tail = len & 0xf;
    if (tail) {
        uint8_t block[16] = {};
        std::memcpy(block, o + rem, tail);
        d = aesdec(d, load(block));
    }
    if (rem == 0x30) c = aesdec(c, load(o + 0x20));
    if (rem >= 0x20) b = aesdec(b, load(o + 0x10));
    if (rem >= 0x10) a = aesdec(a, load(o));
    const uint64_t k0 = seed - static_cast<uint64_t>(len), k1 = static_cast<uint64_t>(len) + 1 + seed;
    const Lane k{{static_cast<uint32_t>(k0), static_cast<uint32_t>(k0 >> 32), static_cast<uint32_t>(k1), static_cast<uint32_t>(k1 >> 32)}};
    a = aesdec(a, k);
    b = aesdec(b, k);
    c = aesdec(c, k);
    d = aesdec(d, k);
    c = aesdec(c, d);
    a = aesdec(a, b);
    c = aesdec(c, k);
    a = aesdec(a, c);
    a = aesdec(a, k);
    for (int i = 0; i < 4; ++i)
        for (int j = 0; j < 4; ++j) out[i * 4 + j] = static_cast<uint8_t>(a.w[i] >> (8 * j));
}

ContentHash content_hash(const void* data, size_t len) {
    uint8_t h[16];
    keen_hash128(data, len, h);
    ContentHash r;
    r.size = static_cast<uint32_t>(len);
    std::memcpy(&r.h0, h + 4, 4);
    std::memcpy(&r.h1, h + 8, 4);
    std::memcpy(&r.h2, h + 12, 4);
    return r;
}

std::string content_hash_text(const ContentHash& h) {
    char b[64];
    std::snprintf(b, sizeof b, "%x-%x-%x-%x", h.size, h.h0, h.h1, h.h2);
    return b;
}

}  // namespace kvb
