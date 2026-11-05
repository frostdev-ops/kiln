// SPDX-License-Identifier: MIT
// keen::ContentHash in C++: the game's own hash of a content blob. Same algorithm as tools/kfc/kfc/contenthash.py, the reference
// checked against 4,473 blobs of the client (docs/re/vfx.md section 6.2). test: tools/vfxcook/tests compares the two on shared vectors.
// A wrong hash on a shader blob is fatal in the game ("error streaming vfx pipeline"), so nothing here may depend on the CPU: this
// is the software path only (the game's 0xf999c0 gives the same result as its AES-NI path 0xf99800).
#pragma once
#include <cstddef>
#include <cstdint>
#include <string>

namespace kvb {

struct ContentHash {
    uint32_t size = 0, h0 = 0, h1 = 0, h2 = 0;
    bool operator==(const ContentHash&) const = default;
};
static_assert(sizeof(ContentHash) == 16);

// The 128-bit hash. seed is 0 for content.
void keen_hash128(const void* data, size_t len, uint8_t out[16], uint64_t seed = 0);

// {size, bytes 4..16 of the 128-bit hash}, as a ResourceContentReference stores it.
ContentHash content_hash(const void* data, size_t len);

// kiln.assets' spelling: <size>-<h0>-<h1>-<h2> in hex.
std::string content_hash_text(const ContentHash& h);

}  // namespace kvb
