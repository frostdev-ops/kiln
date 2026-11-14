// SPDX-License-Identifier: MIT
// Which entity origins are inside a closed sphere or a closed axis-aligned box, nearest first. Origin geometry: a point
// per entity, not its collider. Pure: the caller snapshots ids and positions on the tick thread.
#ifndef KILN_QUERIES_ENTITIES_H
#define KILN_QUERIES_ENTITIES_H
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <vector>

namespace kq {

struct Origin {
    uint32_t id;
    double x, y, z;
};

struct Shape {
    bool box = false;
    double center[3] = {0, 0, 0};
    double half[3] = {0, 0, 0};
    double radius = 0;
};

inline bool valid_shape(const Shape& s) {
    for (double c : s.center)
        if (!std::isfinite(c)) return false;
    if (!s.box) return std::isfinite(s.radius) && s.radius >= 0;
    for (double h : s.half)
        if (!std::isfinite(h) || h < 0) return false;
    return true;
}

// The ids inside, sorted by distance from the centre and then by id. `exclude` (0 = none) is left out.
inline std::vector<uint32_t> origins_inside(const std::vector<Origin>& all, const Shape& s, uint32_t exclude) {
    struct Found {
        double d2;
        uint32_t id;
    };
    std::vector<Found> found;
    for (const Origin& o : all) {
        if (o.id == 0 || o.id == exclude) continue;
        const double dx = o.x - s.center[0], dy = o.y - s.center[1], dz = o.z - s.center[2];
        if (!std::isfinite(dx) || !std::isfinite(dy) || !std::isfinite(dz)) continue;
        const double d2 = dx * dx + dy * dy + dz * dz;
        const bool in = s.box ? std::fabs(dx) <= s.half[0] && std::fabs(dy) <= s.half[1] && std::fabs(dz) <= s.half[2] : d2 <= s.radius * s.radius;
        if (in) found.push_back({d2, o.id});
    }
    std::sort(found.begin(), found.end(), [](const Found& a, const Found& b) { return a.d2 != b.d2 ? a.d2 < b.d2 : a.id < b.id; });
    std::vector<uint32_t> ids;
    ids.reserve(found.size());
    for (const Found& f : found) ids.push_back(f.id);
    return ids;
}

}  // namespace kq
#endif
