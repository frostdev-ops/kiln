// SPDX-License-Identifier: MIT
// Plain math of kiln.rules' creative hooks (creative.cpp), kept apart so the host tests check it without a game.
//  - the fog of war's discover range for a map-reveal mode (the game's discoveryRange is a diameter in metres:
//    FogDiscover paints a circle of radius range/2; docs/re/creative-mode-146.md Part 3, section 1)
//  - the whole-map sweep: a grid of circles over the map, a few per frame, so no frame marks every fog tile dirty
#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>

namespace creative_math {

constexpr float kMinRadius = 50.0f, kMaxRadius = 5000.0f;
constexpr float kSweepRadius = 512.0f;  // metres per sweep circle: ~25 fog tiles (256 m each) per call
constexpr int kSweepPerFrame = 4;       // circles per discover call (the system calls once per frame)
constexpr float kMinMap = 100.0f, kMaxMap = 100000.0f;

// The range FogDiscover is called with: the game's own, or at least a circle of `radius` metres (mode 1).
inline float fog_range(uint32_t mode, float radius, float game_range) {
    if (!std::isfinite(game_range) || game_range < 0.0f) return game_range;
    if (mode != 1 || !std::isfinite(radius)) return game_range;
    const float r = std::clamp(radius, kMinRadius, kMaxRadius);
    return std::max(game_range, 2.0f * r);
}

// A sane map size (the FogOfWar's float2 at +0).
inline bool map_ok(float sx, float sy) {
    return std::isfinite(sx) && std::isfinite(sy) && sx >= kMinMap && sy >= kMinMap && sx <= kMaxMap && sy <= kMaxMap;
}

// The sweep grid: square cells of side radius*sqrt(2), so each cell lies inside its circle.
struct Sweep {
    int nx = 0, ny = 0;
    float step = 0.0f;
    int count() const { return nx * ny; }
};

inline Sweep sweep_for(float sx, float sy, float radius = kSweepRadius) {
    Sweep s;
    if (!map_ok(sx, sy) || !(radius > 0.0f)) return s;
    s.step = radius * 1.41421356f * 0.99f;
    s.nx = static_cast<int>(std::ceil(sx / s.step));
    s.ny = static_cast<int>(std::ceil(sy / s.step));
    return s;
}

// The centre of circle i (row-major), in the discover call's (x, z) metres.
inline void sweep_center(const Sweep& s, int i, float* x, float* z) {
    const int cx = i % s.nx, cz = i / s.nx;
    *x = (static_cast<float>(cx) + 0.5f) * s.step;
    *z = (static_cast<float>(cz) + 0.5f) * s.step;
}

}  // namespace creative_math
