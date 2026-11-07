// SPDX-License-Identifier: MIT
// Host checks of kiln.rules' creative math (creative_math.h): the map-reveal range and the whole-map sweep.
#include "creative_math.h"

#include <cstdio>
#include <limits>

namespace {
int failures = 0;
void check(bool ok, const char* text) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", text);
    if (!ok) ++failures;
}
}  // namespace

int main() {
    using namespace creative_math;
    check(fog_range(0, 400, 120) == 120, "default mode keeps the game's range");
    check(fog_range(1, 200, 120) == 400, "a 200 m radius is a 400 m discover range (the range is a diameter)");
    check(fog_range(1, 400, 1000) == 1000, "a wider game range is never narrowed");
    check(fog_range(1, 1e9f, 10) == 2 * kMaxRadius && fog_range(1, 1, 10) == 2 * kMinRadius, "the radius is clamped");
    check(fog_range(1, std::numeric_limits<float>::quiet_NaN(), 120) == 120, "a NaN radius keeps the game's range");
    check(fog_range(2, 400, 120) == 120, "whole-map mode leaves the per-frame range alone (the sweep adds calls)");
    check(!map_ok(0, 1000) && !map_ok(1e9f, 1000) && !map_ok(std::numeric_limits<float>::infinity(), 1000) && map_ok(10240, 10240),
          "map sizes outside 100 m .. 100 km are refused");
    const Sweep s = sweep_for(10240, 10240);
    check(s.count() > 0 && s.count() <= 400, "a 10 km map sweeps in a few hundred circles");
    check(s.count() / kSweepPerFrame < 120, "and in under 120 frames");
    // every point of the map lies inside some circle (grid-cell corners are the farthest points)
    bool covered = true;
    for (float x = 0; x <= 10240 && covered; x += 97)
        for (float z = 0; z <= 10240 && covered; z += 97) {
            bool in = false;
            const int cx = std::min(static_cast<int>(x / s.step), s.nx - 1), cz = std::min(static_cast<int>(z / s.step), s.ny - 1);
            float px, pz;
            sweep_center(s, cz * s.nx + cx, &px, &pz);
            in = (px - x) * (px - x) + (pz - z) * (pz - z) <= kSweepRadius * kSweepRadius;
            covered = in;
        }
    check(covered, "every point of the map lies inside its cell's circle");
    check(sweep_for(50, 50).count() == 0, "a map this hook does not understand is not swept");
    std::printf("creative math: %d failure(s)\n", failures);
    return failures ? 1 : 0;
}
