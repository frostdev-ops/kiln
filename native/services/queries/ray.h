// SPDX-License-Identifier: MIT
// A voxel ray's numbers, before and after the game: what is refused as an argument, what the game is given, and what its
// answer means. Pure (chapter 01 sections 2.1, 2.2, 2.6).
#ifndef KILN_QUERIES_RAY_H
#define KILN_QUERIES_RAY_H
#include <cmath>
#include <cstdint>
#include <cstring>

namespace kq {

inline constexpr double kMaxRayLength = 256.0;
inline constexpr uint32_t kRayMinLengthBits = 0x1e3ce508u;  // 1e-20f: the game's own "no segment" bound, checked against its bytes at start
inline constexpr size_t kHitBytes = 32;                      // the game writes a 21-byte prefix

// The segment as the game gets it: two float4 (w = 0), 16-byte aligned.
struct RayFloats {
    alignas(16) float from[4];
    alignas(16) float to[4];
    float length;
};

inline float min_ray_length() {
    float f;
    const uint32_t bits = kRayMinLengthBits;
    std::memcpy(&f, &bits, sizeof f);
    return f;
}

// False: not a ray the service takes (not finite, no length, too long; also once narrowed to float, where two distinct
// doubles can be the same point). The length is the game's own: float multiply, add, square root, in its order.
inline bool narrow_ray(const double from[3], const double to[3], RayFloats* out) {
    double sum = 0;
    for (int i = 0; i < 3; ++i) {
        if (!std::isfinite(from[i]) || !std::isfinite(to[i])) return false;
        const double d = to[i] - from[i];
        sum += d * d;
    }
    const double length = std::sqrt(sum);
    if (!(length > 0.0) || !(length <= kMaxRayLength)) return false;
    for (int i = 0; i < 3; ++i) {
        out->from[i] = static_cast<float>(from[i]);
        out->to[i] = static_cast<float>(to[i]);
        if (!std::isfinite(out->from[i]) || !std::isfinite(out->to[i])) return false;
    }
    out->from[3] = out->to[3] = 0.0f;
    const float dx = out->to[0] - out->from[0], dy = out->to[1] - out->from[1], dz = out->to[2] - out->from[2];
    const float len = std::sqrt(dx * dx + dy * dy + dz * dz);
    if (!std::isfinite(len) || !(len > min_ray_length()) || !(len <= static_cast<float>(kMaxRayLength))) return false;
    out->length = len;
    return true;
}

enum class RayOutcome { Clear, Hit, NotLoaded, OutOfGrid, InvalidHit };

struct RayHit {
    float position[3];
    uint32_t material;
    uint16_t voxel;
};

// What the game's hit record says, given that both endpoints were inside the grid box before the call. The order is the
// point: an answer whose last cell was outside the grid, or with data not loaded, is never "clear", whatever its hit flag.
inline RayOutcome read_hit(const unsigned char* h, RayHit* out) {
    const bool available = h[0x12] != 0, hit = h[0x13] != 0, outside = h[0x14] != 0;
    if (outside) return RayOutcome::OutOfGrid;
    if (!available) return RayOutcome::NotLoaded;
    if (!hit) return RayOutcome::Clear;
    std::memcpy(out->position, h, sizeof out->position);
    std::memcpy(&out->material, h + 0x0c, sizeof out->material);
    std::memcpy(&out->voxel, h + 0x10, sizeof out->voxel);
    for (float p : out->position)
        if (!std::isfinite(p)) return RayOutcome::InvalidHit;
    return RayOutcome::Hit;
}

}  // namespace kq
#endif
