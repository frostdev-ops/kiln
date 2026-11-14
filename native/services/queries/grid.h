// SPDX-License-Identifier: MIT
// The realm's ray grid and the box both endpoints of a ray must be in before the game is asked. The game's ray reads a
// cell outside the grid as loaded and empty, so "available, no hit" from it says nothing about a segment that leaves the
// grid: the box test is what makes a clear answer mean clear. Pure; the reader of game memory is the caller's.
// (docs/re/queries.md; chapter 01 section 2.3.) The path feature uses the same preflight.
#ifndef KILN_QUERIES_GRID_H
#define KILN_QUERIES_GRID_H
#include <cmath>
#include <cstdint>
#include <cstring>

namespace kq {

// Offsets captured by sigdb (queries/...): nothing here is a literal of one game build.
struct GridLayout {
    int64_t realms = 0;       // ecs::VoxelGrid -> realm array
    int64_t realm_count = 0;  // ecs::VoxelGrid -> u64 count
    int64_t realm_stride = 0; // bytes per realm entry; its first qword is the realm's ray grid
    int64_t origin = 0;       // ray grid -> float3 origin
    int64_t shift = 0;        // ray grid -> u32 chunk shift
    int64_t count_h = 0;      // ray grid -> u32 horizontal chunk count (X and Z)
    int64_t count_v = 0;      // ray grid -> u32 vertical chunk count (Y)
};

struct RealmBox {
    float origin[3] = {0, 0, 0};
    uint32_t shift = 0, count_h = 0, count_v = 0;
};

enum class GridRead {
    Ok,
    NoGrid,      // no realm array, or this realm has no grid yet: the world is not ready
    BadRealm,    // the realm index is not below the live count
    BadMetadata, // the count or the box cannot be what the layout says: the capability is unusable
    Fault,       // a read of game memory faulted
};

inline constexpr uint64_t kMaxRealms = 256;

// reads n bytes of game memory; false on a fault
using ReadFn = bool (*)(uintptr_t address, void* out, size_t n);

inline bool sane_layout(const GridLayout& l) {
    auto ok = [](int64_t v, int64_t max) { return v >= 0 && v <= max; };
    return ok(l.realms, 1 << 20) && ok(l.realm_count, 1 << 20) && l.realm_stride >= 8 && l.realm_stride <= (1 << 20) &&
           ok(l.origin, 1 << 16) && ok(l.shift, 1 << 16) && ok(l.count_h, 1 << 16) && ok(l.count_v, 1 << 16);
}

inline bool valid_box(const RealmBox& b) {
    for (float o : b.origin)
        if (!std::isfinite(o)) return false;
    return b.shift <= 30 && b.count_h != 0 && b.count_v != 0;
}

// The box of realm `realm` of the voxel grid singleton at `grid`.
inline GridRead read_realm_box(uintptr_t grid, uint32_t realm, const GridLayout& l, ReadFn read, RealmBox* out) {
    if (!grid) return GridRead::NoGrid;
    uintptr_t realms = 0;
    uint64_t count = 0;
    if (!read(grid + static_cast<uintptr_t>(l.realms), &realms, sizeof realms) ||
        !read(grid + static_cast<uintptr_t>(l.realm_count), &count, sizeof count))
        return GridRead::Fault;
    if (!realms || count == 0) return GridRead::NoGrid;
    if (count > kMaxRealms) return GridRead::BadMetadata;
    if (realm >= count) return GridRead::BadRealm;
    uintptr_t g = 0;
    if (!read(realms + static_cast<uintptr_t>(realm) * static_cast<uintptr_t>(l.realm_stride), &g, sizeof g)) return GridRead::Fault;
    if (!g) return GridRead::NoGrid;
    RealmBox b;
    if (!read(g + static_cast<uintptr_t>(l.origin), b.origin, sizeof b.origin) || !read(g + static_cast<uintptr_t>(l.shift), &b.shift, sizeof b.shift) ||
        !read(g + static_cast<uintptr_t>(l.count_h), &b.count_h, sizeof b.count_h) || !read(g + static_cast<uintptr_t>(l.count_v), &b.count_v, sizeof b.count_v))
        return GridRead::Fault;
    if (!valid_box(b)) return GridRead::BadMetadata;
    *out = b;
    return GridRead::Ok;
}

// The game's own cell test (lower bound inclusive, upper exclusive), on the floats that are passed to it:
// d = p - origin as float; d >= 0; int32(d) >> shift < count.
inline bool axis_inside(float p, float origin, uint32_t shift, uint32_t count) {
    const float d = p - origin;
    if (!std::isfinite(d) || d < 0.0f || !(d < 2147483648.0f)) return false;
    return static_cast<uint32_t>(static_cast<int32_t>(d) >> shift) < count;
}

inline bool box_contains(const RealmBox& b, const float p[3]) {
    return axis_inside(p[0], b.origin[0], b.shift, b.count_h) && axis_inside(p[1], b.origin[1], b.shift, b.count_v) &&
           axis_inside(p[2], b.origin[2], b.shift, b.count_h);
}

}  // namespace kq
#endif
