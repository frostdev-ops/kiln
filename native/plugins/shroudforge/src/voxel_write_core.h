// SPDX-License-Identifier: MIT
// ShroudForge's runtime.world.voxel.write through kiln.build (the fork's abi/runtime_host.h, voxel_write): the part that
// runs on the server world's thread, portable (no Windows, no KilnApi), so tests/voxel_write_core_test.cpp runs this exact
// code on Linux over a fake kiln.build. host_voxels.cpp brings the call there from ShroudForge's mods thread.
//
// The outcome codes are ShroudForge's own (KfcRuntimeWorldVoxelWrite; modules/game-runtime/bridge/windows/voxels/
// write_voxels.rs maps them to its texts): 0 the world holds the cells; 1 refused before anything changed ("voxel write
// was rejected before changing the world"); 2 the write did not read back as asked and the earlier cells were written
// back and read back ("... previous voxel region was restored"); 3 anything else ("outcome is uncertain"). As
// ShroudForge's own writer (read_and_write_voxels.cpp, write_registered_voxel_grid.cpp): at most 65,536 cells, the box is
// read first, an unchanged box is success without a write, the write is read back, and a mismatch is put back once.
// The box must lie in the voxel grid as ShroudForge's own writer requires (read_registered_voxel_grid.cpp region(): no
// coordinate below 0, x and z inside the grid's tiles, y at most INT32_MAX - 7): kiln.build itself checks no grid bounds,
// and the engine's block writer would commit blocks outside the world.
// Differences: kiln.build commits only the 8x8x8 blocks whose cells change (ShroudForge rewrites the whole widened box)
// and asks the game for tiles that are not streamed in (then refuses; the mod tries again).
//
// The write is KILN_BUILD_F_IN_BASES | KILN_BUILD_F_GAME_SAVE: ShroudForge has neither Kiln's base guard nor its save
// regions, so cells are saved where the game saves them (base build zones) and regrow elsewhere, as without Kiln.
#pragma once
#include <algorithm>
#include <climits>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

#include "services/kiln_build.h"

namespace ksf::voxels {

constexpr std::size_t kMaximumCells = 65536;  // ShroudForge's own limit (lua_world_voxel_write)
constexpr int64_t kMaximumCoordinate = INT32_MAX - 7;  // ShroudForge's own bound of a box's end (read_registered_voxel_grid.cpp)

enum Outcome : uint32_t { kWritten = 0, kRefused = 1, kRestored = 2, kUncertain = 3 };

// kiln.build's tick members (or a test's): ready, read, write and grid as kiln_build.h has them; detail: kiln.build's
// status text (why writes are off), may be null. Without grid every write is refused.
struct Io {
    void* user = nullptr;
    KilnResult (*ready)(void* user, const int32_t origin[3], const uint32_t dims[3]) = nullptr;
    KilnResult (*read)(void* user, const int32_t origin[3], const uint32_t dims[3], KilnVoxel* out, std::size_t capacity) = nullptr;
    KilnResult (*write)(void* user, const KilnBuildWrite* write, KilnBuildResult* result) = nullptr;
    std::string (*detail)(void* user) = nullptr;
    KilnResult (*grid)(void* user, KilnBuildGrid* out) = nullptr;
};

inline const char* result_name(KilnResult rc) {
    switch (rc) {
    case KILN_OK: return "ok";
    case KILN_ERR_INVALID_ARGUMENT: return "invalid argument";
    case KILN_ERR_NOT_FOUND: return "not found";
    case KILN_ERR_UNAVAILABLE: return "unavailable";
    case KILN_ERR_WRONG_THREAD: return "wrong thread";
    case KILN_ERR_REVOKED: return "refused by a protection";
    case KILN_ERR_INTERNAL: return "the engine's writer faulted";
    default: return "an unknown result";
    }
}

inline std::string with_detail(const Io& io, std::string why) {
    if (io.detail) {
        const std::string d = io.detail(io.user);
        if (!d.empty()) why += " (kiln.build: " + d + ")";
    }
    return why;
}

// The box ShroudForge's mod asked for, its cells (x fastest, then y, then z), and why when the outcome is not kWritten.
inline uint32_t write_cells(const Io& io, const int32_t origin[3], const uint32_t dims[3], const uint16_t* values,
                            std::size_t count, std::string& why) {
    why.clear();
    if (!origin || !dims || !values) {
        why = "the request has no box or no cells";
        return kRefused;
    }
    std::size_t cells = 1;
    for (int a = 0; a < 3; ++a) {
        if (!dims[a] || dims[a] > kMaximumCells || cells * dims[a] > kMaximumCells) {
            why = "a voxel write must contain between 1 and 65,536 cells";
            return kRefused;
        }
        cells *= dims[a];
    }
    if (count != cells) {
        why = "the cell count does not match the box";
        return kRefused;
    }
    // inside the voxel grid (ShroudForge's region(): kiln.build checks no grid bounds)
    KilnBuildGrid grid{};
    grid.size = sizeof grid;
    KilnResult rc = io.grid ? io.grid(io.user, &grid) : KILN_ERR_UNAVAILABLE;
    if (rc != KILN_OK) {
        why = with_detail(io, std::string("the voxel grid's size is not known yet: ") + result_name(rc));
        return kRefused;
    }
    for (int a = 0; a < 3; ++a) {
        const int64_t lo = origin[a], hi = lo + dims[a];
        const int64_t top = a == 1 ? kMaximumCoordinate : std::min<int64_t>(grid.cells_max[a], kMaximumCoordinate);
        if (lo < 0 || lo < grid.cells_min[a] || hi > top) {
            char text[160];
            std::snprintf(text, sizeof text, "the box is outside the voxel grid (%c from %lld to %lld; the grid's cells are %d to %lld)",
                          "xyz"[a], static_cast<long long>(lo), static_cast<long long>(hi), std::max<int32_t>(0, grid.cells_min[a]),
                          static_cast<long long>(top));
            why = text;
            return kRefused;
        }
    }
    rc = io.ready(io.user, origin, dims);
    if (rc == KILN_ERR_UNAVAILABLE) {
        why = with_detail(io, "the voxel tiles of the box are not streamed in, or the world does not run (Kiln asked the "
                              "game for the tiles; the mod may try again)");
        return kRefused;
    }
    if (rc != KILN_OK) {
        why = with_detail(io, std::string("kiln.build cannot write here: ") + result_name(rc));
        return kRefused;
    }
    std::vector<KilnVoxel> before(cells);
    if ((rc = io.read(io.user, origin, dims, before.data(), before.size())) != KILN_OK) {
        why = with_detail(io, std::string("reading the box before the write failed: ") + result_name(rc));
        return kRefused;
    }
    if (std::equal(before.begin(), before.end(), values)) return kWritten;  // nothing to change, as ShroudForge

    KilnBuildWrite w{};
    w.size = sizeof w;
    w.flags = KILN_BUILD_F_IN_BASES | KILN_BUILD_F_GAME_SAVE;
    for (int a = 0; a < 3; ++a) w.origin[a] = origin[a], w.dims[a] = dims[a];
    w.cells = values;
    w.origin_mod = KilnStr{"shroudforge", sizeof("shroudforge") - 1};
    KilnBuildResult r{};
    r.size = sizeof r;
    rc = io.write(io.user, &w, &r);
    if (rc == KILN_OK && (r.flags & KILN_BUILD_R_VERIFIED)) return kWritten;
    // INTERNAL: the engine's writer faulted, possibly inside the first block: never "nothing changed"
    const bool touched = (r.flags & KILN_BUILD_R_CHANGED) || r.blocks_written > 0 || rc == KILN_ERR_INTERNAL;
    if (rc != KILN_OK && !touched) {
        std::string what = std::string("kiln.build refused the write: ") + result_name(rc);
        if (r.flags & KILN_BUILD_R_NOT_LOADED) what += ", the voxel tiles are not streamed in (Kiln asked the game for them)";
        why = with_detail(io, what);
        return kRefused;
    }
    char text[160];
    if (rc == KILN_OK)
        std::snprintf(text, sizeof text, "the write did not read back as asked (%u block(s) written)", r.blocks_written);
    else
        std::snprintf(text, sizeof text, "the write stopped after %u block(s): %s", r.blocks_written, result_name(rc));
    why = text;
    // put the earlier cells back once, and read them back
    w.cells = before.data();
    KilnBuildResult back_result{};
    back_result.size = sizeof back_result;
    std::vector<KilnVoxel> back(cells);
    if (io.write(io.user, &w, &back_result) == KILN_OK && io.read(io.user, origin, dims, back.data(), back.size()) == KILN_OK &&
        back == before) {
        why += "; the earlier cells were put back";
        return kRestored;
    }
    why = with_detail(io, why + "; putting the earlier cells back failed too: part of the write may be in the world");
    return kUncertain;
}

}  // namespace ksf::voxels
