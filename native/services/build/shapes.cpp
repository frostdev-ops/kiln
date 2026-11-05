// SPDX-License-Identifier: MIT
// Shape edits on the cell grid: read the box, compute the new cells, write them through vox_write (the engine's own
// block writer and store commit). The shape gives every cell a coverage s in 0..1 from its centre's signed distance d
// to the surface (d > 0 inside): s = clamp(0.5 + d / cell, 0, 1), a one-cell ramp like the game's own brushes leave.
// Cell fill is 0..255 (255 solid), material 0 is air; see docs/re/build.md for the cell format.
#include <algorithm>
#include <array>

#include "internal.h"

namespace kb {
namespace {

struct Ctx {
    const VoxelWorld* v;
    const KilnBuildShape* s;
    int32_t o[3];
    uint32_t d[3];
};

bool shape_valid(const KilnBuildShape* s) {
    if (!s || s->size < sizeof(KilnBuildShape)) return false;
    if (s->op < KILN_BUILD_OP_DIG || s->op > KILN_BUILD_OP_REPLACE) return false;
    if (s->shape < KILN_BUILD_SHAPE_BOX || s->shape > KILN_BUILD_SHAPE_CYLINDER) return false;
    const double c[3] = {s->center.x, s->center.y, s->center.z}, h[3] = {s->half.x, s->half.y, s->half.z};
    for (int a = 0; a < 3; ++a)
        if (!std::isfinite(c[a])) return false;
    if (!(s->half.x > 0 && s->half.x <= 256)) return false;
    if (s->shape != KILN_BUILD_SHAPE_SPHERE && !(s->half.y > 0 && s->half.y <= 256)) return false;
    if (s->shape == KILN_BUILD_SHAPE_BOX && !(s->half.z > 0 && s->half.z <= 256)) return false;
    (void)h;
    if ((s->op == KILN_BUILD_OP_ADD || s->op == KILN_BUILD_OP_PAINT || s->op == KILN_BUILD_OP_REPLACE) && !s->material)
        return false;
    if (s->op == KILN_BUILD_OP_REPLACE && !s->from_material) return false;
    return true;
}

// half extents per axis of the shape's bounding box
void extents(const KilnBuildShape* s, double h[3]) {
    switch (s->shape) {
        case KILN_BUILD_SHAPE_SPHERE: h[0] = h[1] = h[2] = s->half.x; break;
        case KILN_BUILD_SHAPE_CYLINDER: h[0] = h[2] = s->half.x, h[1] = s->half.y; break;
        default: h[0] = s->half.x, h[1] = s->half.y, h[2] = s->half.z; break;
    }
}

// signed distance (metres, > 0 inside) of point p to the shape
double sdist(const KilnBuildShape* s, double x, double y, double z) {
    const double dx = x - s->center.x, dy = y - s->center.y, dz = z - s->center.z;
    switch (s->shape) {
        case KILN_BUILD_SHAPE_SPHERE: return s->half.x - std::sqrt(dx * dx + dy * dy + dz * dz);
        case KILN_BUILD_SHAPE_CYLINDER:
            return std::min(s->half.x - std::sqrt(dx * dx + dz * dz), s->half.y - std::fabs(dy));
        default:
            return std::min({s->half.x - std::fabs(dx), s->half.y - std::fabs(dy), s->half.z - std::fabs(dz)});
    }
}

// 2D footprint distance for FLATTEN
double sdist_xz(const KilnBuildShape* s, double x, double z) {
    const double dx = x - s->center.x, dz = z - s->center.z;
    switch (s->shape) {
        case KILN_BUILD_SHAPE_SPHERE:
        case KILN_BUILD_SHAPE_CYLINDER: return s->half.x - std::sqrt(dx * dx + dz * dz);
        default: return std::min(s->half.x - std::fabs(dx), s->half.z - std::fabs(dz));
    }
}

// Materials 1..127 are terrain (the store commit compresses a block of one of them at fill 255 to a single value),
// 128..255 the building-block materials (an item's ChangeVoxelData.placeVoxelMaterialId: Rough Wood Block 143,
// Rough Stone Block 155, ...). Matter is fill > 0; the game leaves the material of a surface on the air cells next to
// it (fill 0, material set), so material without fill is "air that knows its neighbour's surface".
inline bool solid_mat(uint8_t m) { return m != 0; }
inline bool terrain_mat(uint8_t m) { return m >= 1 && m < 128; }
inline double raw_cover(double d, double cell) { return 0.5 + d / cell; }
inline double cover(double d, double cell) { return std::clamp(0.5 + d / cell, 0.0, 1.0); }
inline uint8_t to_fill(double s) { return static_cast<uint8_t>(std::lround(std::clamp(s, 0.0, 1.0) * 255.0)); }

size_t idx(const uint32_t d[3], uint32_t x, uint32_t y, uint32_t z) {
    return (static_cast<size_t>(z) * d[1] + y) * d[0] + x;
}

}  // namespace

KilnResult shape_box(const KilnBuildShape* s, int32_t origin[3], uint32_t dims[3]) {
    if (!shape_valid(s) || !origin || !dims) return KILN_ERR_INVALID_ARGUMENT;
    if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
    const VoxelWorld* v = vox();
    if (!v) return KILN_ERR_UNAVAILABLE;
    double h[3];
    extents(s, h);
    const double c[3] = {s->center.x, s->center.y, s->center.z};
    double lo[3], hi[3];
    for (int a = 0; a < 3; ++a) lo[a] = c[a] - h[a], hi[a] = c[a] + h[a];
    if (s->op == KILN_BUILD_OP_FLATTEN) {
        const double level = std::isfinite(s->level_y) ? s->level_y : s->center.y;
        lo[1] = std::min(lo[1], level), hi[1] = std::max(hi[1], level);
    }
    const int pad = s->op == KILN_BUILD_OP_SMOOTH ? 2 : 1;
    for (int a = 0; a < 3; ++a) {
        const int32_t c0 = cell_of(*v, a, lo[a]) - pad, c1 = cell_of(*v, a, hi[a]) + pad;
        origin[a] = c0;
        dims[a] = static_cast<uint32_t>(c1 - c0 + 1);
    }
    uint64_t n = 0;
    return box_ok(origin, dims, &n) ? KILN_OK : KILN_ERR_INVALID_ARGUMENT;
}

KilnResult shape_run(const KilnBuildShape* s, KilnBuildResult* result) {
    int32_t o[3];
    uint32_t d[3];
    if (KilnResult rc = shape_box(s, o, d); rc != KILN_OK) return rc;
    const VoxelWorld* v = vox();
    if (!v) return KILN_ERR_UNAVAILABLE;
    uint64_t n = 0;
    box_ok(o, d, &n);
    std::vector<KilnVoxel> cells(n);
    if (!(s->flags & KILN_BUILD_F_UNLOADED) && !vox_ready_box(*v, o, d, true)) {
        if (result && result->size >= sizeof(KilnBuildResult)) result->flags = KILN_BUILD_R_NOT_LOADED;
        return KILN_ERR_UNAVAILABLE;
    }
    if (KilnResult rc = vox_read(o, d, cells.data(), cells.size()); rc == KILN_ERR_INTERNAL || rc == KILN_ERR_INVALID_ARGUMENT)
        return rc;
    const double cs = v->cell;
    auto cx = [&](uint32_t i) { return v->origin[0] + (o[0] + static_cast<int64_t>(i) + 0.5) * cs; };
    auto cy = [&](uint32_t j) { return v->origin[1] + (o[1] + static_cast<int64_t>(j) + 0.5) * cs; };
    auto cz = [&](uint32_t k) { return v->origin[2] + (o[2] + static_cast<int64_t>(k) + 0.5) * cs; };
    std::vector<KilnVoxel> out = cells;
    uint32_t action = KILN_GAMEPLAY_DENY_TERRAFORM;

    switch (s->op) {
        case KILN_BUILD_OP_DIG:
        case KILN_BUILD_OP_ADD:
        case KILN_BUILD_OP_PAINT:
        case KILN_BUILD_OP_REPLACE: {
            if (s->op != KILN_BUILD_OP_DIG && s->op != KILN_BUILD_OP_ADD) action |= KILN_GAMEPLAY_DENY_BUILD;
            for (uint32_t k = 0; k < d[2]; ++k)
                for (uint32_t j = 0; j < d[1]; ++j)
                    for (uint32_t i = 0; i < d[0]; ++i) {
                        const double raw = raw_cover(sdist(s, cx(i), cy(j), cz(k)), cs);
                        const double cov = std::clamp(raw, 0.0, 1.0);
                        const size_t at = idx(d, i, j, k);
                        const KilnVoxel c = cells[at];
                        uint8_t mat = KILN_VOXEL_MATERIAL(c), fill = KILN_VOXEL_FILL(c);
                        if (cov <= 0) {
                            // ADD: the air cells just outside the new surface carry its material, like the game's
                            if (s->op == KILN_BUILD_OP_ADD && raw > -1.0 && !fill && !mat)
                                out[at] = KILN_VOXEL_MAKE(s->material, 0);
                            continue;
                        }
                        if (s->op == KILN_BUILD_OP_DIG) {
                            const uint8_t keep = to_fill(1.0 - cov);
                            if (fill <= keep) continue;
                            fill = keep;
                            // emptied cells next to the new surface keep its material; deep inside they become air
                            if (!fill && cov >= 1.0) mat = 0;
                            out[at] = KILN_VOXEL_MAKE(mat, fill);
                            continue;
                        } else if (s->op == KILN_BUILD_OP_ADD) {
                            const uint8_t want = to_fill(cov);
                            if (want <= fill && solid_mat(mat)) continue;
                            const bool was_air = fill == 0;
                            if (want > fill) fill = want;
                            if (was_air || !solid_mat(mat) || cov >= 0.5) mat = s->material;
                        } else if (s->op == KILN_BUILD_OP_PAINT) {
                            if (cov < 0.5 || !mat || !fill) continue;
                            mat = s->material;
                        } else {
                            if (cov < 0.5 || mat != s->from_material) continue;
                            mat = s->material;
                        }
                        out[at] = fill ? KILN_VOXEL_MAKE(mat, fill) : KILN_VOXEL_AIR;
                    }
            break;
        }
        case KILN_BUILD_OP_FLATTEN: {
            double h[3];
            extents(s, h);
            const double level = std::isfinite(s->level_y) ? s->level_y : s->center.y;
            const double ylo = std::min(s->center.y - h[1], level - cs), yhi = std::max(s->center.y + h[1], level + cs);
            for (uint32_t k = 0; k < d[2]; ++k)
                for (uint32_t i = 0; i < d[0]; ++i) {
                    if (sdist_xz(s, cx(i), cz(k)) < 0) continue;
                    // the column's material: the configured one, else the highest solid cell's, else the most solid
                    uint8_t colmat = s->material;
                    if (!colmat)
                        for (int64_t j = d[1] - 1; j >= 0 && !colmat; --j) {
                            const KilnVoxel c = cells[idx(d, i, static_cast<uint32_t>(j), k)];
                            if (terrain_mat(KILN_VOXEL_MATERIAL(c)) && KILN_VOXEL_FILL(c) >= 128) colmat = KILN_VOXEL_MATERIAL(c);
                        }
                    if (!colmat) colmat = 2;
                    for (uint32_t j = 0; j < d[1]; ++j) {
                        const double y = cy(j);
                        if (y < ylo || y > yhi) continue;
                        const size_t at = idx(d, i, j, k);
                        const uint8_t fill = to_fill(cover(level - y, cs));
                        const KilnVoxel c = cells[at];
                        uint8_t mat = KILN_VOXEL_MATERIAL(c);
                        if (!KILN_VOXEL_FILL(c) || s->material) mat = colmat;
                        // the air row right above the new level keeps the column's surface material
                        out[at] = fill ? KILN_VOXEL_MAKE(mat, fill) : (y - level < cs ? KILN_VOXEL_MAKE(colmat, 0) : KILN_VOXEL_AIR);
                    }
                }
            break;
        }
        case KILN_BUILD_OP_SMOOTH: {
            const double strength = std::clamp(static_cast<double>(s->strength), 0.0, 1.0);
            const uint32_t passes = std::clamp<uint32_t>(s->passes ? s->passes : 1, 1, 8);
            std::vector<float> f(n), g(n);
            for (size_t a = 0; a < n; ++a) f[a] = KILN_VOXEL_FILL(cells[a]) / 255.f;
            std::vector<float> w(n, 0.f);
            for (uint32_t k = 0; k < d[2]; ++k)
                for (uint32_t j = 0; j < d[1]; ++j)
                    for (uint32_t i = 0; i < d[0]; ++i)
                        w[idx(d, i, j, k)] = static_cast<float>(cover(sdist(s, cx(i), cy(j), cz(k)), cs) * strength);
            for (uint32_t p = 0; p < passes; ++p) {
                g = f;
                for (uint32_t k = 1; k + 1 < d[2]; ++k)
                    for (uint32_t j = 1; j + 1 < d[1]; ++j)
                        for (uint32_t i = 1; i + 1 < d[0]; ++i) {
                            const size_t at = idx(d, i, j, k);
                            if (w[at] <= 0) continue;
                            float sum = 0;
                            for (int dz = -1; dz <= 1; ++dz)
                                for (int dy = -1; dy <= 1; ++dy)
                                    for (int dx = -1; dx <= 1; ++dx) sum += f[idx(d, i + dx, j + dy, k + dz)];
                            g[at] = f[at] + w[at] * (sum / 27.f - f[at]);
                        }
                f.swap(g);
            }
            for (uint32_t k = 1; k + 1 < d[2]; ++k)
                for (uint32_t j = 1; j + 1 < d[1]; ++j)
                    for (uint32_t i = 1; i + 1 < d[0]; ++i) {
                        const size_t at = idx(d, i, j, k);
                        if (w[at] <= 0) continue;
                        const uint8_t fill = to_fill(f[at]);
                        uint8_t mat = KILN_VOXEL_MATERIAL(cells[at]);
                        if (fill && !KILN_VOXEL_FILL(cells[at]) && !mat) {  // grew into air: the neighbours' most common material
                            std::array<uint16_t, 256> votes{};
                            for (int dz = -1; dz <= 1; ++dz)
                                for (int dy = -1; dy <= 1; ++dy)
                                    for (int dx = -1; dx <= 1; ++dx) {
                                        const KilnVoxel c = cells[idx(d, i + dx, j + dy, k + dz)];
                                        if (KILN_VOXEL_FILL(c) && KILN_VOXEL_MATERIAL(c)) ++votes[KILN_VOXEL_MATERIAL(c)];
                                    }
                            mat = static_cast<uint8_t>(std::max_element(votes.begin() + 1, votes.end()) - votes.begin());
                            if (!votes[mat]) continue;
                        }
                        out[at] = KILN_VOXEL_MAKE(mat, fill);
                    }
            break;
        }
        default: return KILN_ERR_INVALID_ARGUMENT;
    }
    KilnBuildWrite w{};
    w.size = sizeof w;
    w.flags = s->flags;
    std::memcpy(w.origin, o, sizeof o);
    std::memcpy(w.dims, d, sizeof d);
    w.cells = out.data();
    w.actor = s->actor;
    w.origin_mod = s->origin_mod;
    return vox_write(&w, result, action);
}

}  // namespace kb
