// SPDX-License-Identifier: MIT
// The voxel world through the engine's own interfaces:
//  - ecs::VoxelGrid (world singleton) holds the interface the GameServer publishes at +grid.interface:
//    {wrapper, read, origin, ready, occupied, slot4, region, allocator, 0x100}. read(Request*, wrapper) fills cells of
//    a box (the store's edited blocks, else the generated terrain); ready(wrapper, float4* point) says whether the
//    streaming tile under a point is loaded.
//  - The block writer (build/VoxelWrite) takes the VoxelWorld (wrapper + wrapper.world), a cell span, dims and origin,
//    cuts the box into 8x8x8 blocks and commits each through the store commit (build/StoreCommit): the same sink the
//    game's building and terraforming kernels use, which tracks changes per peer (replication) and for the save.
// Everything here runs on the tick thread (no world job runs in Pre/Post).
#include <windows.h>

#include <algorithm>

#include "internal.h"

namespace kb {
namespace {

VoxelWorld g_vox;
uintptr_t g_failed_world = 0;  // a world whose voxel interface did not check out (logged once)

double now_ms() {
    LARGE_INTEGER f, c;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&c);
    return static_cast<double>(c.QuadPart) * 1000.0 / static_cast<double>(f.QuadPart);
}

template <class T>
bool rd(uintptr_t a, T* out) {
    return a > 0x10000 && seh_copy(out, reinterpret_cast<const void*>(a), sizeof(T));
}

bool resolve(uintptr_t w, VoxelWorld* v) {
    *v = VoxelWorld{};
    v->world = w;
    v->grid = g_api->singleton(w, kstr("ecs::VoxelGrid"));
    if (!v->grid) {
        set_detail("no ecs::VoxelGrid singleton in the server world");
        return false;
    }
    uint64_t iface[7] = {};
    if (!seh_copy(iface, reinterpret_cast<const void*>(v->grid + static_cast<uintptr_t>(L.grid_iface)), sizeof iface)) {
        set_detail("ecs::VoxelGrid interface unreadable");
        return false;
    }
    v->wrapper = iface[0];
    v->read = iface[1];
    v->ready = iface[3];
    if (!v->wrapper || v->read != fn.grid_read || v->ready != fn.grid_ready) {
        set_detail("ecs::VoxelGrid interface does not match sigdb (read %p vs %p, ready %p vs %p)",
                   reinterpret_cast<void*>(v->read), reinterpret_cast<void*>(fn.grid_read),
                   reinterpret_cast<void*>(v->ready), reinterpret_cast<void*>(fn.grid_ready));
        return false;
    }
    if (!rd(v->wrapper + static_cast<uintptr_t>(L.wrapper_world), &v->voxels) || !v->voxels) {
        set_detail("VoxelWorld pointer unreadable");
        return false;
    }
    uint32_t init = 0;
    float o[3] = {};
    uint32_t lvl[4] = {};
    uintptr_t store = 0;
    const uintptr_t vw = v->voxels;
    if (!rd(vw + static_cast<uintptr_t>(L.world_init), &init) || !init) {
        set_detail("VoxelWorld not initialized yet");
        return false;
    }
    if (!seh_copy(o, reinterpret_cast<const void*>(vw + static_cast<uintptr_t>(L.world_origin)), sizeof o) ||
        !seh_copy(lvl, reinterpret_cast<const void*>(vw + static_cast<uintptr_t>(L.world_levels + L.level * L.level_stride)),
                  sizeof lvl) ||
        !rd(vw + static_cast<uintptr_t>(L.world_store), &store) || !store) {
        set_detail("VoxelWorld layout unreadable");
        return false;
    }
    if (!std::isfinite(o[0]) || !std::isfinite(o[1]) || !std::isfinite(o[2]) || lvl[0] < 8 || lvl[1] < 8 ||
        lvl[0] % 8 || lvl[1] % 8 || !lvl[2] || !lvl[3] || lvl[2] > 65536 || lvl[3] > 65536) {
        set_detail("VoxelWorld level %lld looks wrong (tile %u x %u, %u x %u tiles)", static_cast<long long>(L.level),
                   lvl[0], lvl[1], lvl[2], lvl[3]);
        return false;
    }
    v->origin[0] = o[0], v->origin[1] = o[1], v->origin[2] = o[2];
    v->tile[0] = lvl[0], v->tile[1] = lvl[1], v->tiles[0] = lvl[2], v->tiles[1] = lvl[3];
    v->cell = static_cast<double>(L.base_size) / static_cast<double>(1u << L.level);
    if (!(v->cell > 0.01 && v->cell < 64)) {
        set_detail("cell size %.4f looks wrong", v->cell);
        return false;
    }
    v->ok = true;
    return true;
}

}  // namespace

bool box_ok(const int32_t origin[3], const uint32_t dims[3], uint64_t* count) {
    if (!origin || !dims) return false;
    uint64_t n = 1;
    for (int a = 0; a < 3; ++a) {
        if (dims[a] == 0 || dims[a] > 4096) return false;
        const int64_t hi = static_cast<int64_t>(origin[a]) + dims[a];
        if (origin[a] < -(1 << 24) || hi > (1 << 24)) return false;
        n *= dims[a];
        if (n > kMaxCells) return false;
    }
    if (count) *count = n;
    return true;
}

const VoxelWorld* vox() {
    if (!F.read) return nullptr;
    const uintptr_t w = server_world();
    if (!w) return nullptr;
    if (g_vox.ok && g_vox.world == w) return &g_vox;
    VoxelWorld v;
    if (!resolve(w, &v)) {
        if (g_failed_world != w) {
            char d[160];
            detail_copy(d, sizeof d);
            KB_D("voxel world not available yet: %s", d);
            g_failed_world = w;
        }
        return nullptr;
    }
    g_vox = v;
    KB_I("voxel world: VoxelWorld %p, origin (%.2f, %.2f, %.2f), level %lld cell %.3f m, tiles %u x %u of %u x %u "
         "cells (%.0f x %.0f m)",
         reinterpret_cast<void*>(v.voxels), v.origin[0], v.origin[1], v.origin[2], static_cast<long long>(L.level),
         v.cell, v.tiles[0], v.tiles[1], v.tile[0], v.tile[1], v.tiles[0] * v.tile[0] * v.cell,
         v.tiles[1] * v.tile[1] * v.cell);
    set_detail("");
    return &g_vox;
}

KilnResult vox_grid(KilnBuildGrid* out) {
    if (!out || out->size < sizeof(KilnBuildGrid)) return KILN_ERR_INVALID_ARGUMENT;
    if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
    const VoxelWorld* v = vox();
    if (!v) return KILN_ERR_UNAVAILABLE;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->level = static_cast<uint32_t>(L.level);
    out->cell_size = v->cell;
    out->origin = KilnVec3{v->origin[0], v->origin[1], v->origin[2]};
    out->cells_min[0] = 0, out->cells_min[1] = 0, out->cells_min[2] = 0;
    out->cells_max[0] = static_cast<int32_t>(v->tile[0] * v->tiles[0]);
    out->cells_max[1] = 8192;
    out->cells_max[2] = static_cast<int32_t>(v->tile[1] * v->tiles[1]);
    out->tile_cells[0] = v->tile[0], out->tile_cells[1] = v->tile[1];
    out->tiles[0] = v->tiles[0], out->tiles[1] = v->tiles[1];
    out->block = kBlock;
    return KILN_OK;
}

bool vox_ready_box(const VoxelWorld& v, const int32_t origin[3], const uint32_t dims[3], bool request) {
    bool all = true;
    uint32_t requested = 0;
    (void)requested;
    const int64_t x0 = std::max<int64_t>(0, origin[0]) / v.tile[0];
    const int64_t z0 = std::max<int64_t>(0, origin[2]) / v.tile[1];
    const int64_t x1 = std::max<int64_t>(0, static_cast<int64_t>(origin[0]) + dims[0] - 1) / v.tile[0];
    const int64_t z1 = std::max<int64_t>(0, static_cast<int64_t>(origin[2]) + dims[2] - 1) / v.tile[1];
    for (int64_t z = z0; z <= z1; ++z)
        for (int64_t x = x0; x <= x1; ++x) {
            alignas(16) float p[4] = {
                static_cast<float>(v.origin[0] + (x * v.tile[0] + v.tile[0] * 0.5) * v.cell),
                static_cast<float>(v.origin[1] + (std::max<int64_t>(0, origin[1]) + 0.5) * v.cell),
                static_cast<float>(v.origin[2] + (z * v.tile[1] + v.tile[1] * 0.5) * v.cell), 0.f};
            bool ok = false;
            if (!seh_ready(v.ready, v.wrapper, p, &ok)) return false;
            if (!ok) {
                all = false;
                if (request) {
                    // reading touches the tile's chunk resource, which makes the resource system load it (seen loaded
                    // ~1-3 s later); the game itself only streams around players and bases on a dedicated server
                    KilnVoxel c = 0;
                    const int32_t o1[3] = {static_cast<int32_t>(x * v.tile[0]), std::max<int32_t>(0, origin[1]),
                                           static_cast<int32_t>(z * v.tile[1])};
                    ReadRequest req{&c, 1, {1, 1, 1}, static_cast<uint32_t>(L.level), {o1[0], o1[1], o1[2]}, 0};
                    bool r = false;
                    seh_read(v.read, &req, v.wrapper, &r);
                    ++requested;
                } else {
                    return false;
                }
            }
        }
    return all;
}

KilnResult vox_read(const int32_t origin[3], const uint32_t dims[3], KilnVoxel* out, size_t capacity) {
    uint64_t n = 0;
    if (!box_ok(origin, dims, &n) || !out || capacity < n) return KILN_ERR_INVALID_ARGUMENT;
    if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
    const VoxelWorld* v = vox();
    if (!v) return KILN_ERR_UNAVAILABLE;
    ReadRequest req{out, n, {dims[0], dims[1], dims[2]}, static_cast<uint32_t>(L.level),
                    {origin[0], origin[1], origin[2]}, 0};
    bool all_ready = false;
    if (!seh_read(v->read, &req, v->wrapper, &all_ready)) {
        KB_E("voxel read faulted (box %d,%d,%d + %u,%u,%u)", origin[0], origin[1], origin[2], dims[0], dims[1], dims[2]);
        return KILN_ERR_INTERNAL;
    }
    S.reads.fetch_add(1, std::memory_order_relaxed);
    S.cells_read.fetch_add(n, std::memory_order_relaxed);
    return all_ready ? KILN_OK : KILN_ERR_UNAVAILABLE;
}

namespace {

// [lo, hi) widened to whole blocks
void widen(const int32_t origin[3], const uint32_t dims[3], int32_t wo[3], uint32_t wd[3]) {
    for (int a = 0; a < 3; ++a) {
        const int64_t lo = origin[a], hi = lo + dims[a];
        const int64_t blo = (lo >= 0 ? lo / kBlock : -((-lo + kBlock - 1) / kBlock)) * kBlock;
        const int64_t bhi = (hi >= 0 ? (hi + kBlock - 1) / kBlock : -((-hi) / kBlock)) * kBlock;
        wo[a] = static_cast<int32_t>(blo);
        wd[a] = static_cast<uint32_t>(bhi - blo);
    }
}

}  // namespace

KilnResult vox_write(const KilnBuildWrite* w, KilnBuildResult* result, uint32_t zone_action) {
    if (!w || w->size < sizeof(KilnBuildWrite) || !w->cells) return KILN_ERR_INVALID_ARGUMENT;
    if (result && result->size < sizeof(KilnBuildResult)) return KILN_ERR_INVALID_ARGUMENT;
    KilnBuildResult r{};
    r.size = sizeof r;
    std::memcpy(r.box_origin, w->origin, sizeof r.box_origin);
    std::memcpy(r.box_dims, w->dims, sizeof r.box_dims);
    auto done = [&](KilnResult rc) {
        if (result) {
            const uint32_t sz = result->size;
            *result = r;
            result->size = sz;
        }
        if (rc != KILN_OK) S.refused.fetch_add(1, std::memory_order_relaxed);
        return rc;
    };
    uint64_t n = 0;
    if (!box_ok(w->origin, w->dims, &n)) return done(KILN_ERR_INVALID_ARGUMENT);
    if (!tick_thread()) return done(KILN_ERR_WRONG_THREAD);
    if (!F.write) return done(KILN_ERR_UNAVAILABLE);
    const VoxelWorld* v = vox();
    if (!v || !world_running()) return done(KILN_ERR_UNAVAILABLE);

    int32_t wo[3];
    uint32_t wd[3];
    widen(w->origin, w->dims, wo, wd);
    uint64_t wn = 0;
    if (!box_ok(wo, wd, &wn)) return done(KILN_ERR_INVALID_ARGUMENT);
    if (!(w->flags & KILN_BUILD_F_UNLOADED) && !vox_ready_box(*v, wo, wd, true)) {
        r.flags |= KILN_BUILD_R_NOT_LOADED;
        return done(KILN_ERR_UNAVAILABLE);
    }
    std::vector<KilnVoxel> old(wn), cur;
    if (KilnResult rc = vox_read(wo, wd, old.data(), old.size());
        rc != KILN_OK && !(rc == KILN_ERR_UNAVAILABLE && (w->flags & KILN_BUILD_F_UNLOADED))) {
        if (rc == KILN_ERR_UNAVAILABLE) r.flags |= KILN_BUILD_R_NOT_LOADED;
        return done(rc);
    }
    cur = old;
    const uint32_t ox = static_cast<uint32_t>(w->origin[0] - wo[0]), oy = static_cast<uint32_t>(w->origin[1] - wo[1]),
                   oz = static_cast<uint32_t>(w->origin[2] - wo[2]);
    uint64_t changed = 0;
    // which blocks change: bit per block
    const uint32_t bx = wd[0] / kBlock, by = wd[1] / kBlock, bz = wd[2] / kBlock;
    std::vector<uint8_t> dirty(static_cast<size_t>(bx) * by * bz, 0);
    for (uint32_t z = 0; z < w->dims[2]; ++z)
        for (uint32_t y = 0; y < w->dims[1]; ++y) {
            const size_t src = (static_cast<size_t>(z) * w->dims[1] + y) * w->dims[0];
            const size_t dst = ((static_cast<size_t>(z) + oz) * wd[1] + y + oy) * wd[0] + ox;
            for (uint32_t x = 0; x < w->dims[0]; ++x) {
                if (w->mask && !w->mask[src + x]) continue;
                const KilnVoxel nv = w->cells[src + x];
                if (cur[dst + x] == nv) continue;
                cur[dst + x] = nv;
                ++changed;
                const uint32_t gx = (x + ox) / kBlock, gy = (y + oy) / kBlock, gz = (z + oz) / kBlock;
                dirty[(static_cast<size_t>(gz) * by + gy) * bx + gx] = 1;
            }
        }
    r.cells_changed = changed;
    if (!changed) {
        r.flags |= KILN_BUILD_R_VERIFIED;
        return done(KILN_OK);
    }
    // policy, on the box actually changed (metres)
    const KilnVec3 lo{cell_lo(*v, 0, w->origin[0]), cell_lo(*v, 1, w->origin[1]), cell_lo(*v, 2, w->origin[2])};
    const KilnVec3 hi{cell_lo(*v, 0, w->origin[0] + static_cast<int32_t>(w->dims[0])),
                      cell_lo(*v, 1, w->origin[1] + static_cast<int32_t>(w->dims[1])),
                      cell_lo(*v, 2, w->origin[2] + static_cast<int32_t>(w->dims[2]))};
    if (w->flags & KILN_BUILD_F_RESPECT_ZONES) {
        if (const uint32_t zone = zone_denies_box(w->actor, lo, hi, zone_action)) {
            r.flags |= KILN_BUILD_R_ZONE;
            r.zone = zone;
            return done(KILN_ERR_REVOKED);
        }
    }
    if (!(w->flags & KILN_BUILD_F_IN_BASES)) {
        KilnEntity altar = 0;
        if (touches_base(lo, hi, &altar)) {
            r.flags |= KILN_BUILD_R_BASE;
            r.zone = altar;
            return done(KILN_ERR_REVOKED);
        }
    }
    if (w->flags & KILN_BUILD_F_DRY_RUN) {
        r.flags |= KILN_BUILD_R_DRY_RUN;
        return done(KILN_OK);
    }

    // commit the changed blocks only (unchanged blocks would turn generated terrain into stored edits)
    const double t0 = now_ms();
    std::vector<KilnVoxel> blk(kBlock * kBlock * kBlock);
    const uint32_t bdims[3] = {kBlock, kBlock, kBlock};
    uint32_t blocks = 0;
    for (uint32_t gz = 0; gz < bz; ++gz)
        for (uint32_t gy = 0; gy < by; ++gy)
            for (uint32_t gx = 0; gx < bx; ++gx) {
                if (!dirty[(static_cast<size_t>(gz) * by + gy) * bx + gx]) continue;
                for (uint32_t z = 0; z < kBlock; ++z)
                    for (uint32_t y = 0; y < kBlock; ++y)
                        std::memcpy(&blk[(z * kBlock + y) * kBlock],
                                    &cur[((static_cast<size_t>(gz) * kBlock + z) * wd[1] + gy * kBlock + y) * wd[0] +
                                         gx * kBlock],
                                    kBlock * sizeof(KilnVoxel));
                const int32_t borg[3] = {wo[0] + static_cast<int32_t>(gx * kBlock),
                                         wo[1] + static_cast<int32_t>(gy * kBlock),
                                         wo[2] + static_cast<int32_t>(gz * kBlock)};
                CellSpan span{blk.data(), blk.size()};
                unsigned long code = 0;
                if (!seh_write(fn.write, v->voxels, &span, bdims, borg, &code)) {
                    KB_E("voxel write faulted (0x%08lx) at block %d,%d,%d after %u block(s); voxel writes are off",
                         code, borg[0], borg[1], borg[2], blocks);
                    F.write = false;
                    set_detail("voxel writer faulted (0x%08lx)", code);
                    r.blocks_written = blocks;
                    return done(KILN_ERR_INTERNAL);
                }
                ++blocks;
            }
    r.milliseconds = now_ms() - t0;
    r.blocks_written = blocks;
    r.flags |= KILN_BUILD_R_CHANGED;
    if (!(w->flags & KILN_BUILD_F_TRANSIENT)) {
        regions_keep(wo, wd);
        r.flags |= KILN_BUILD_R_KEPT;
    }
    S.writes.fetch_add(1, std::memory_order_relaxed);
    S.cells_written.fetch_add(changed, std::memory_order_relaxed);
    S.blocks_written.fetch_add(blocks, std::memory_order_relaxed);
    // read back
    std::vector<KilnVoxel> back(wn);
    if (vox_read(wo, wd, back.data(), back.size()) != KILN_ERR_INTERNAL && back == cur) r.flags |= KILN_BUILD_R_VERIFIED;
    else KB_W("voxel write at %d,%d,%d (+%u,%u,%u) did not read back equal", w->origin[0], w->origin[1], w->origin[2],
              w->dims[0], w->dims[1], w->dims[2]);
    return done(KILN_OK);
}

}  // namespace kb
