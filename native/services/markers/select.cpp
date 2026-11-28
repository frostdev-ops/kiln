// SPDX-License-Identifier: MIT
#include "select.h"

#include <algorithm>
#include <cmath>
#include <limits>

namespace kmk {

bool position_valid(double x, double y, double z) {
    return std::isfinite(x) && std::isfinite(y) && std::isfinite(z) && x >= -1000 && x <= 12000 && z >= -1000 && z <= 12000 &&
           y >= -1000 && y <= 5000;
}

namespace {

// The reason a requested surface is not eligible before its room is counted; READY when it is eligible.
uint32_t ineligible(const View& v, const Candidate& c, uint32_t s, double d2) {
    if (!v.world_active) return KILN_MARKER_NO_WORLD;
    if (!v.cap[s]) return KILN_MARKER_CAPABILITY_OFF;
    if (c.source_state == KILN_MARKER_ANCHOR_ENDED) return KILN_MARKER_ANCHOR_ENDED;
    if (c.source_state == KILN_MARKER_FOLLOW_MISSING || c.source_state == KILN_MARKER_SOURCE_STALE) return c.source_state;
    if (c.source_state == KILN_MARKER_BAD_POSITION || !position_valid(c.x, c.y, c.z)) return KILN_MARKER_BAD_POSITION;
    const bool ranged = c.max_distance > 0;
    if (s != kSurfMap && (!v.camera || !v.viewport)) return KILN_MARKER_NO_CAMERA;
    if (ranged && !v.camera) return KILN_MARKER_NO_CAMERA;
    if (ranged && d2 > static_cast<double>(c.max_distance) * c.max_distance) return KILN_MARKER_OUT_OF_RANGE;
    if (s == kSurfWorld) {
        const double dx = c.x - v.cam[0], dy = c.y + c.height - v.cam[1], dz = c.z - v.cam[2];
        if (dx * v.fwd[0] + dy * v.fwd[1] + dz * v.fwd[2] <= v.near_plane) return KILN_MARKER_BEHIND;
    }
    return KILN_MARKER_READY;
}

}  // namespace

void admit(const View& v, const std::vector<Candidate>& rows, std::vector<Admitted>* out, std::vector<uint32_t> order[3]) {
    out->assign(rows.size(), Admitted{});
    std::vector<uint32_t> eligible[3];
    for (uint32_t i = 0; i < rows.size(); ++i) {
        const Candidate& c = rows[i];
        Admitted& a = (*out)[i];
        if (v.camera) {
            const double dx = c.x - v.cam[0], dy = c.y - v.cam[1], dz = c.z - v.cam[2];
            a.d2 = dx * dx + dy * dy + dz * dz;
            if (!std::isfinite(a.d2)) a.d2 = std::numeric_limits<double>::infinity();
        } else {
            a.d2 = std::numeric_limits<double>::infinity();
        }
        for (uint32_t s = 0; s < 3; ++s) {
            if (!(c.requested & kSurfaceBit[s])) continue;
            a.reason[s] = ineligible(v, c, s, a.d2);
            if (a.reason[s] == KILN_MARKER_READY) eligible[s].push_back(i);
        }
    }
    const auto better = [&](uint32_t l, uint32_t r) {
        const Candidate &a = rows[l], &b = rows[r];
        if (a.priority != b.priority) return a.priority > b.priority;
        const double da = (*out)[l].d2, db = (*out)[r].d2;
        if (da != db) return da < db;  // infinities compare equal
        if (a.source_rank != b.source_rank) return a.source_rank < b.source_rank;
        if (const int c = a.ns.compare(b.ns)) return c < 0;
        if (a.publisher_generation != b.publisher_generation) return a.publisher_generation < b.publisher_generation;
        return a.key.compare(b.key) < 0;
    };
    for (uint32_t s = 0; s < 3; ++s) {
        auto& e = eligible[s];
        std::sort(e.begin(), e.end(), better);
        const uint32_t budget = s == kSurfMap ? kMapBudget : s == kSurfCompass ? kCompassBudget : kWorldBudget;
        const uint32_t room = s == kSurfMap ? std::min(budget, v.map_room) : budget;
        order[s].clear();
        for (uint32_t rank = 0; rank < e.size(); ++rank) {
            Admitted& a = (*out)[e[rank]];
            if (rank < room) {
                a.after |= kSurfaceBit[s];
                order[s].push_back(e[rank]);
            } else {
                // the game's list ran out before Kiln's own budget did: capacity; otherwise Kiln's budget
                a.reason[s] = (s == kSurfMap && rank >= v.map_room) ? KILN_MARKER_MAP_CAPACITY : KILN_MARKER_SURFACE_BUDGET;
            }
        }
    }
    // The map's admitted rows go into the game's list in their slot order (select.h): no priority, no distance.
    const auto slot = [&](uint32_t l, uint32_t r) {
        const Candidate &a = rows[l], &b = rows[r];
        if (a.source_rank != b.source_rank) return a.source_rank < b.source_rank;
        if (const int c = a.ns.compare(b.ns)) return c < 0;
        if (a.publisher_generation != b.publisher_generation) return a.publisher_generation < b.publisher_generation;
        return a.key.compare(b.key) < 0;
    };
    std::sort(order[kSurfMap].begin(), order[kSurfMap].end(), slot);
}

double heading_of(const double fwd[3], double previous) {
    const double h = std::sqrt(fwd[0] * fwd[0] + fwd[2] * fwd[2]);
    if (!(h >= 1e-8)) return previous;
    return std::atan2(fwd[0], fwd[2]);
}

bool forward_of(const float q[4], double out[3]) {
    double x = q[0], y = q[1], z = q[2], w = q[3];
    const double n = std::sqrt(x * x + y * y + z * z + w * w);
    if (!(n > 1e-12) || !std::isfinite(n)) return false;
    x /= n, y /= n, z /= n, w /= n;
    out[0] = 2 * (x * z + w * y);
    out[1] = 2 * (y * z - w * x);
    out[2] = 1 - 2 * (x * x + y * y);
    return true;
}

}  // namespace kmk
