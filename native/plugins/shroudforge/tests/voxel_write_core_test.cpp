// SPDX-License-Identifier: MIT
// src/voxel_write_core.h over a fake kiln.build (a 32^3 world of cells): ShroudForge's outcome codes for every path.
//   tests/test-voxel-write.sh (g++, ASan/UBSan; no game, no Windows)
#include "../src/voxel_write_core.h"

#include <cstdio>
#include <cstring>

namespace {
int total = 0, failed = 0;
void check(bool ok, const char* what) {
    ++total;
    if (!ok) { ++failed; std::printf("FAIL %s\n", what); }
}

struct World {
    KilnVoxel cells[32 * 32 * 32]{};
    KilnResult ready = KILN_OK, read = KILN_OK, write = KILN_OK, grid = KILN_OK;
    bool corrupt = false;        // the write commits something else (a mismatch on read back)
    int writes = 0, reads = 0, fault_after = -1;  // fault_after: the n-th write commits one block, then faults
    uint32_t flags_seen = 0;
    bool restore_fails = false;
    static std::size_t at(int x, int y, int z) { return (static_cast<std::size_t>(z) * 32 + y) * 32 + x; }
};
KilnResult ready(void* u, const int32_t*, const uint32_t*) { return static_cast<World*>(u)->ready; }
KilnResult read(void* u, const int32_t o[3], const uint32_t d[3], KilnVoxel* out, std::size_t cap) {
    auto& w = *static_cast<World*>(u);
    ++w.reads;
    if (w.read != KILN_OK) return w.read;
    std::size_t j = 0;
    for (uint32_t z = 0; z < d[2]; ++z)
        for (uint32_t y = 0; y < d[1]; ++y)
            for (uint32_t x = 0; x < d[0]; ++x) {
                if (j >= cap) return KILN_ERR_BUFFER_TOO_SMALL;
                out[j++] = w.cells[World::at(o[0] + x, o[1] + y, o[2] + z)];
            }
    return KILN_OK;
}
KilnResult write(void* u, const KilnBuildWrite* bw, KilnBuildResult* r) {
    auto& w = *static_cast<World*>(u);
    ++w.writes;
    w.flags_seen = bw->flags;
    if (w.write != KILN_OK && w.writes != w.fault_after) return w.write;
    if (w.restore_fails && w.writes > 1) return KILN_ERR_UNAVAILABLE;
    std::size_t j = 0, changed = 0;
    for (uint32_t z = 0; z < bw->dims[2]; ++z)
        for (uint32_t y = 0; y < bw->dims[1]; ++y)
            for (uint32_t x = 0; x < bw->dims[0]; ++x) {
                auto& c = w.cells[World::at(bw->origin[0] + x, bw->origin[1] + y, bw->origin[2] + z)];
                const KilnVoxel v = w.corrupt && j == 0 ? KilnVoxel(bw->cells[j] ^ 0x0101) : bw->cells[j];
                if (c != v) ++changed;
                c = v;
                ++j;
                if (w.writes == w.fault_after && j == 1) {  // one cell in, then the writer faults
                    r->blocks_written = 1;
                    return KILN_ERR_INTERNAL;
                }
            }
    r->flags = (changed ? KILN_BUILD_R_CHANGED : 0) | (w.corrupt ? 0 : KILN_BUILD_R_VERIFIED);
    r->blocks_written = changed ? 1 : 0;
    r->cells_changed = changed;
    return KILN_OK;
}
std::string detail(void*) { return "writes are off: test"; }
KilnResult grid(void* u, KilnBuildGrid* g) {  // kiln.build's grid: 32 x 32 cells (x, z); y has no engine bound
    auto& w = *static_cast<World*>(u);
    if (w.grid != KILN_OK) return w.grid;
    g->cells_min[0] = g->cells_min[1] = g->cells_min[2] = 0;
    g->cells_max[0] = 32, g->cells_max[1] = 8192, g->cells_max[2] = 32;
    return KILN_OK;
}
ksf::voxels::Io io(World& w) { return {&w, &ready, &read, &write, &detail, &grid}; }
}  // namespace

int main() {
    using namespace ksf::voxels;
    std::string why;
    const int32_t o[3] = {2, 3, 4};
    const uint32_t d[3] = {4, 2, 3};
    std::vector<uint16_t> v(24);
    for (std::size_t i = 0; i < v.size(); ++i) v[i] = KILN_VOXEL_MAKE(128 + i, 255);

    {  // written: the cells land, the write is IN_BASES|GAME_SAVE, read back by kiln.build
        World w;
        check(write_cells(io(w), o, d, v.data(), v.size(), why) == kWritten && why.empty(), "a write lands: outcome 0");
        check(w.cells[World::at(2, 3, 4)] == v[0] && w.cells[World::at(5, 4, 6)] == v[23], "the box is x fastest, then y, then z");
        check(w.flags_seen == (KILN_BUILD_F_IN_BASES | KILN_BUILD_F_GAME_SAVE), "the game's save rule, no base guard");
        const int writes = w.writes;
        check(write_cells(io(w), o, d, v.data(), v.size(), why) == kWritten && w.writes == writes, "an unchanged box writes nothing");
    }
    {  // refused before anything: shape, readiness, the first read, a kiln.build refusal
        World w;
        const uint32_t empty[3] = {0, 1, 1}, big[3] = {256, 256, 2};
        check(write_cells(io(w), o, empty, v.data(), 0, why) == kRefused && why.find("65,536") != std::string::npos, "an empty box");
        check(write_cells(io(w), o, big, v.data(), 131072, why) == kRefused, "over 65,536 cells");
        check(write_cells(io(w), o, d, v.data(), 23, why) == kRefused && why.find("count") != std::string::npos, "a count that does not match");
        const int32_t edge[3] = {INT32_MAX - 1, 0, 0};
        check(write_cells(io(w), edge, d, v.data(), v.size(), why) == kRefused, "a box past the coordinates");
        w.ready = KILN_ERR_UNAVAILABLE;
        check(write_cells(io(w), o, d, v.data(), v.size(), why) == kRefused && why.find("streamed in") != std::string::npos &&
              why.find("kiln.build: writes are off: test") != std::string::npos, "tiles not loaded: refused, says why");
        w.ready = KILN_OK;
        w.read = KILN_ERR_INTERNAL;
        check(write_cells(io(w), o, d, v.data(), v.size(), why) == kRefused && why.find("reading the box") != std::string::npos, "a failed first read");
        w.read = KILN_OK;
        w.write = KILN_ERR_WRONG_THREAD;
        check(write_cells(io(w), o, d, v.data(), v.size(), why) == kRefused && why.find("wrong thread") != std::string::npos &&
              w.cells[World::at(2, 3, 4)] == 0, "kiln.build's refusal: outcome 1, nothing changed");
    }
    {  // only the voxel grid's cells, as ShroudForge's own writer: nothing below 0, x and z inside the grid; refused before
       // any read or write (kiln.build checks no grid bounds; the engine's writer would commit blocks outside the world)
        World w;
        const int32_t outside[][3] = {{-1, 3, 4}, {2, -1, 4}, {2, 3, -1}, {29, 3, 4}, {2, 3, 30}, {INT32_MIN, 3, 4}};
        for (const auto& at : outside)
            check(write_cells(io(w), at, d, v.data(), v.size(), why) == kRefused && why.find("outside the voxel grid") != std::string::npos,
                  "a box outside the voxel grid is refused");
        check(w.writes == 0 && w.reads == 0, "and nothing was read or written for it");
        const int32_t edge[3] = {28, 3, 29};  // ends exactly at 32 in x and z
        check(write_cells(io(w), edge, d, v.data(), v.size(), why) == kWritten && w.cells[World::at(31, 4, 31)] == v[23],
              "a box that ends at the grid's edge is written");
        const int32_t high[3] = {2, INT32_MAX - 7 - 1, 4};  // y ends past ShroudForge's own bound (INT32_MAX - 7)
        check(write_cells(io(w), high, d, v.data(), v.size(), why) == kRefused, "a box past y's bound is refused");
        World g;
        g.grid = KILN_ERR_UNAVAILABLE;
        check(write_cells(io(g), o, d, v.data(), v.size(), why) == kRefused && g.writes == 0 && g.reads == 0,
              "no grid yet: refused before any change");
    }
    {  // every write reads back wrong, the put-back too: outcome 3
        World w;
        w.cells[World::at(2, 3, 4)] = KILN_VOXEL_MAKE(1, 255);
        w.corrupt = true;
        check(write_cells(io(w), o, d, v.data(), v.size(), why) == kUncertain && why.find("may be in the world") != std::string::npos,
              "a put-back that does not read back: outcome 3");
    }
    {  // one bad write, a good put-back: outcome 2
        World w;
        w.cells[World::at(2, 3, 4)] = KILN_VOXEL_MAKE(1, 255);
        ksf::voxels::Io i = io(w);
        i.write = [](void* u, const KilnBuildWrite* bw, KilnBuildResult* r) {
            auto& ww = *static_cast<World*>(u);
            ww.corrupt = ww.writes == 0;  // the first write commits a wrong cell, the put-back is exact
            return write(u, bw, r);
        };
        check(write_cells(i, o, d, v.data(), v.size(), why) == kRestored && w.cells[World::at(2, 3, 4)] == KILN_VOXEL_MAKE(1, 255) &&
              w.cells[World::at(3, 3, 4)] == 0 && why.find("put back") != std::string::npos, "a mismatch is put back: outcome 2");
    }
    {  // the writer faults after one block: never outcome 1; put back when possible
        World w;
        w.write = KILN_ERR_INTERNAL;
        w.fault_after = 1;
        // the put-back (write 2) returns INTERNAL too (writes are off after a fault): uncertain
        check(write_cells(io(w), o, d, v.data(), v.size(), why) == kUncertain && why.find("stopped after 1 block") != std::string::npos,
              "a fault mid-write with writes off afterwards: outcome 3");
        World w2;
        w2.write = KILN_ERR_INTERNAL;
        w2.fault_after = 1;
        ksf::voxels::Io i = io(w2);
        i.write = [](void* u, const KilnBuildWrite* bw, KilnBuildResult* r) {
            auto& ww = *static_cast<World*>(u);
            if (ww.writes == 1) ww.write = KILN_OK;  // the put-back works
            return write(u, bw, r);
        };
        check(write_cells(i, o, d, v.data(), v.size(), why) == kRestored && w2.cells[World::at(2, 3, 4)] == 0, "a fault mid-write, put back: outcome 2");
    }
    std::printf("voxel write core: %d passed, %d failed\n", total - failed, failed);
    return failed ? 1 : 0;
}
