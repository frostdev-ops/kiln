// SPDX-License-Identifier: MIT
// Lab-only debug channel for engine work: when <kiln>/data/kiln.build/debug exists at boot, kiln.build polls
// <kiln>/data/kiln.build/cmd once a second (tick thread, Pre), runs each line and renames the file to cmd.done.
// Results go to kiln.log ("build debug: ..."). Commands:
//   info                                  grid geometry
//   column <x> <z> [y0 y1]                cells of one column (metres), non-air runs
//   dump <x> <y> <z> <dx> <dy> <dz>       cells of a cell box (cell coordinates), hex
//   bases                                 flame altars and their build zones
//   props <x> <y> <z> <r>                 entities around a point
//   item <id>                             material / template an item places
#include <cstdarg>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <sstream>

#include "internal.h"

namespace kb {
namespace {

bool g_enabled = false;
std::filesystem::path g_dir;
uint64_t g_last = 0;

void column(double x, double z, double y0, double y1) {
    const VoxelWorld* v = vox();
    if (!v) return KB_I("build debug: no voxel world");
    const int32_t cx = cell_of(*v, 0, x), cz = cell_of(*v, 2, z);
    const int32_t c0 = cell_of(*v, 1, y0), c1 = cell_of(*v, 1, y1);
    const int32_t o[3] = {cx, c0, cz};
    const uint32_t d[3] = {1, static_cast<uint32_t>(std::max(1, c1 - c0 + 1)), 1};
    std::vector<KilnVoxel> cells(d[1]);
    const KilnResult rc = vox_read(o, d, cells.data(), cells.size());
    KB_I("build debug: column (%.2f, %.2f) = cell %d,%d, y %d..%d, read rc %d, tile ready %d", x, z, cx, cz, c0, c1, rc,
         vox_ready_box(*v, o, d));
    // print from the top: runs of equal cells
    int32_t j = static_cast<int32_t>(d[1]) - 1;
    int lines = 0;
    while (j >= 0 && lines < 40) {
        int32_t k = j;
        while (k - 1 >= 0 && cells[k - 1] == cells[j]) --k;
        KB_I("build debug:   y %8.2f..%8.2f (cells %d..%d) = %04x (material %u fill %u) x%d", cell_lo(*v, 1, c0 + k),
             cell_lo(*v, 1, c0 + j + 1), c0 + k, c0 + j, cells[j], KILN_VOXEL_MATERIAL(cells[j]),
             KILN_VOXEL_FILL(cells[j]), j - k + 1);
        j = k - 1;
        ++lines;
    }
}

void dump(int32_t x, int32_t y, int32_t z, uint32_t dx, uint32_t dy, uint32_t dz) {
    const int32_t o[3] = {x, y, z};
    const uint32_t d[3] = {dx, dy, dz};
    uint64_t n = 0;
    if (!box_ok(o, d, &n) || n > 4096) return KB_I("build debug: dump box too big");
    std::vector<KilnVoxel> cells(n);
    const KilnResult rc = vox_read(o, d, cells.data(), cells.size());
    KB_I("build debug: dump %d,%d,%d +%u,%u,%u rc %d", x, y, z, dx, dy, dz, rc);
    for (uint32_t k = 0; k < dz; ++k)
        for (uint32_t j = 0; j < dy; ++j) {
            std::string line;
            char b[8];
            for (uint32_t i = 0; i < dx && i < 32; ++i) {
                std::snprintf(b, sizeof b, " %04x", cells[(static_cast<size_t>(k) * dy + j) * dx + i]);
                line += b;
            }
            KB_I("build debug:   z%+d y%+d:%s", static_cast<int>(k), static_cast<int>(j), line.c_str());
        }
}

void run(const std::string& line) {
    std::istringstream in(line);
    std::string cmd;
    in >> cmd;
    if (cmd.empty() || cmd[0] == '#') return;
    KB_I("build debug: > %s", line.c_str());
    if (cmd == "info") {
        KilnBuildGrid g{};
        g.size = sizeof g;
        const KilnResult rc = vox_grid(&g);
        KB_I("build debug: grid rc %d level %u cell %.3f origin (%.2f, %.2f, %.2f) cells %d..%d x %d..%d, tiles %u x %u "
             "of %u x %u",
             rc, g.level, g.cell_size, g.origin.x, g.origin.y, g.origin.z, g.cells_min[0], g.cells_max[0],
             g.cells_min[2], g.cells_max[2], g.tiles[0], g.tiles[1], g.tile_cells[0], g.tile_cells[1]);
    } else if (cmd == "column") {
        double x = 0, z = 0, y0 = 700, y1 = 1100;
        in >> x >> z;
        if (!(in >> y0 >> y1)) y0 = 600, y1 = 1200;
        column(x, z, y0, y1);
    } else if (cmd == "dump") {
        int32_t x, y, z;
        uint32_t dx, dy, dz;
        if (in >> x >> y >> z >> dx >> dy >> dz) dump(x, y, z, dx, dy, dz);
    } else if (cmd == "bases") {
        KilnBuildBase b[64];
        for (auto& e : b) e.size = sizeof e;
        size_t n = 0;
        const KilnResult rc = bases_list(b, 64, &n);
        KB_I("build debug: bases rc %d count %zu", rc, n);
        for (size_t i = 0; i < n && i < 64; ++i)
            KB_I("build debug:   altar %u at (%.1f, %.1f, %.1f) zone half (%.1f, %.1f, %.1f) abandoned %u", b[i].altar,
                 b[i].center.x, b[i].center.y, b[i].center.z, b[i].half.x, b[i].half.y, b[i].half.z, b[i].abandoned);
    } else if (cmd == "props") {
        double x, y, z, r;
        if (!(in >> x >> y >> z >> r)) return;
        std::vector<KilnBuildPropInfo> out(256);
        for (auto& e : out) e.size = sizeof e;
        size_t n = 0;
        const KilnResult rc = props_in_box(KilnVec3{x - r, y - r, z - r}, KilnVec3{x + r, y + r, z + r},
                                           KILN_BUILD_PROPS_ALL, out.data(), out.size(), &n);
        KB_I("build debug: props rc %d count %zu", rc, n);
        for (size_t i = 0; i < n && i < out.size() && i < 80; ++i)
            KB_I("build debug:   %u %s kind %u item %u flags %x at (%.2f, %.2f, %.2f) rot (%.3f %.3f %.3f %.3f)",
                 out[i].entity, out[i].name, out[i].kind, out[i].item_id, out[i].flags, out[i].position.x,
                 out[i].position.y, out[i].position.z, out[i].rotation[0], out[i].rotation[1], out[i].rotation[2],
                 out[i].rotation[3]);
    } else if (cmd == "surface") {
        // surface x0 z0 x1 z1 step: ground height (top solid cell, material < 128) on a grid, metres
        double x0, z0, x1, z1, st;
        if (!(in >> x0 >> z0 >> x1 >> z1 >> st) || st < 0.5) return;
        const VoxelWorld* v = vox();
        if (!v) return;
        for (double z = z0; z <= z1 + 1e-6; z += st) {
            std::string row;
            for (double x = x0; x <= x1 + 1e-6; x += st) {
                const int32_t o[3] = {cell_of(*v, 0, x), cell_of(*v, 1, 200.0), cell_of(*v, 2, z)};
                const uint32_t d[3] = {1, 3200, 1};
                std::vector<KilnVoxel> c(d[1]);
                vox_read(o, d, c.data(), c.size());
                double h = -1;
                for (int32_t j = static_cast<int32_t>(d[1]) - 1; j >= 0; --j) {
                    const KilnVoxel cv = c[j];
                    if (KILN_VOXEL_FILL(cv) && KILN_VOXEL_MATERIAL(cv) && KILN_VOXEL_MATERIAL(cv) < 128) {
                        h = cell_lo(*v, 1, o[1] + j) + KILN_VOXEL_FILL(cv) / 255.0 * v->cell;
                        break;
                    }
                }
                char b[16];
                std::snprintf(b, sizeof b, " %7.1f", h);
                row += b;
            }
            KB_I("build debug: z %7.1f:%s", z, row.c_str());
        }
    } else if (cmd == "load") {
        // load x0 z0 x1 z1: request every tile of the area (metres)
        double x0, z0, x1, z1;
        if (!(in >> x0 >> z0 >> x1 >> z1)) return;
        const VoxelWorld* v = vox();
        if (!v) return;
        const int32_t o[3] = {cell_of(*v, 0, x0), 1600, cell_of(*v, 2, z0)};
        const uint32_t d[3] = {static_cast<uint32_t>(cell_of(*v, 0, x1) - o[0] + 1), 1,
                               static_cast<uint32_t>(cell_of(*v, 2, z1) - o[2] + 1)};
        KB_I("build debug: load: ready %d", vox_ready_box(*v, o, d, true));
    } else if (cmd == "sing") {
        // sing <name> <offset> <len> [deref-offset]: hex dump of a world singleton (optionally of *(p+off)+deref)
        std::string name;
        uint64_t off = 0, len = 64;
        if (!(in >> name >> std::hex >> off >> len)) return;
        int64_t deref = -1;
        in >> deref;
        uintptr_t p = g_api->singleton(server_world(), KilnStr{name.data(), name.size()});
        if (!p) return KB_I("build debug: no singleton %s", name.c_str());
        if (deref >= 0) {
            uintptr_t q = 0;
            if (!seh_copy(&q, reinterpret_cast<const void*>(p + off), 8) || !q) return KB_I("build debug: null pointer");
            p = q, off = static_cast<uint64_t>(deref);
        }
        std::vector<uint8_t> b(std::min<uint64_t>(len, 1024));
        if (!seh_copy(b.data(), reinterpret_cast<const void*>(p + off), b.size())) return KB_I("build debug: unreadable");
        for (size_t i = 0; i < b.size(); i += 32) {
            std::string line;
            char t[8];
            for (size_t j = i; j < i + 32 && j < b.size(); ++j) {
                std::snprintf(t, sizeof t, j % 8 == 0 ? " %02x" : "%02x", b[j]);
                line += t;
            }
            KB_I("build debug:   +%04llx:%s", static_cast<unsigned long long>(off + i), line.c_str());
        }
    } else if (cmd == "shape") {
        // shape <op 1..6> <shape 1..3> cx cy cz hx hy hz [material [level_y [flags]]]
        KilnBuildShape sh{};
        sh.size = sizeof sh;
        unsigned mat = 0, flags = 0;
        double level = NAN;
        if (!(in >> sh.op >> sh.shape >> sh.center.x >> sh.center.y >> sh.center.z >> sh.half.x >> sh.half.y >> sh.half.z))
            return;
        in >> mat >> level >> flags;
        sh.material = static_cast<uint8_t>(mat);
        sh.level_y = level;
        sh.flags = flags;
        sh.strength = 1.f;
        sh.passes = 2;
        KilnBuildResult r{};
        r.size = sizeof r;
        const KilnResult rc = shape_run(&sh, &r);
        KB_I("build debug: shape rc %d flags 0x%x changed %llu blocks %u box %d,%d,%d +%u,%u,%u %.2f ms zone %u", rc,
             r.flags, static_cast<unsigned long long>(r.cells_changed), r.blocks_written, r.box_origin[0],
             r.box_origin[1], r.box_origin[2], r.box_dims[0], r.box_dims[1], r.box_dims[2], r.milliseconds, r.zone);
    } else if (cmd == "vw") {
        // vw <offset> <len> [deref-offset]: hex dump of the VoxelWorld (or of *(vw+off)+deref)
        uint64_t off = 0, len = 64;
        if (!(in >> std::hex >> off >> len)) return;
        int64_t deref = -1;
        in >> deref;
        const VoxelWorld* v = vox();
        if (!v) return;
        uintptr_t p = v->voxels;
        if (deref >= 0) {
            uintptr_t q = 0;
            if (!seh_copy(&q, reinterpret_cast<const void*>(p + off), 8) || !q) return KB_I("build debug: null pointer");
            p = q, off = static_cast<uint64_t>(deref);
        }
        std::vector<uint8_t> b(std::min<uint64_t>(len, 1024));
        if (!seh_copy(b.data(), reinterpret_cast<const void*>(p + off), b.size())) return KB_I("build debug: unreadable");
        for (size_t i = 0; i < b.size(); i += 32) {
            std::string line;
            char t[8];
            for (size_t j = i; j < i + 32 && j < b.size(); ++j) {
                std::snprintf(t, sizeof t, j % 8 == 0 ? " %02x" : "%02x", b[j]);
                line += t;
            }
            KB_I("build debug:   +%04llx:%s", static_cast<unsigned long long>(off + i), line.c_str());
        }
    } else if (cmd == "tiles") {
        // tiles: readiness map of every streaming tile ('#' loaded, '.' not), north row first
        const VoxelWorld* v = vox();
        if (!v) return;
        uint32_t loaded = 0;
        for (int64_t tz = static_cast<int64_t>(v->tiles[1]) - 1; tz >= 0; --tz) {
            std::string row;
            for (uint32_t tx = 0; tx < v->tiles[0]; ++tx) {
                const int32_t o[3] = {static_cast<int32_t>(tx * v->tile[0]), 1600, static_cast<int32_t>(tz * v->tile[1])};
                const uint32_t d[3] = {1, 1, 1};
                const bool r = vox_ready_box(*v, o, d);
                loaded += r;
                row += r ? '#' : '.';
            }
            KB_I("build debug: %3lld %s", static_cast<long long>(tz), row.c_str());
        }
        KB_I("build debug: %u of %u tiles loaded", loaded, v->tiles[0] * v->tiles[1]);
    } else if (cmd == "item") {
        uint32_t id = 0;
        in >> id;
        KilnGuid g{};
        const KilnResult rc = item_template(id, &g);
        KB_I("build debug: item %u material %u template rc %d %02x%02x%02x%02x-...", id, item_material(id), rc, g.b[3],
             g.b[2], g.b[1], g.b[0]);
    } else {
        KB_I("build debug: unknown command");
    }
}

}  // namespace

void selftest_init() {
    char buf[1024];
    size_t len = 0;
    if (g_api->path(KILN_PATH_DATA, buf, sizeof buf, &len) != KILN_OK) return;
    g_dir = std::filesystem::path(std::u8string(reinterpret_cast<const char8_t*>(buf), len)) / "kiln.build";  // UTF-8
    std::error_code ec;
    g_enabled = std::filesystem::exists(g_dir / "debug", ec);
    if (g_enabled) KB_I("build debug channel on: %s", reinterpret_cast<const char*>((g_dir / "cmd").u8string().c_str()));
}

void selftest_pre(uint64_t step) {
    if (!g_enabled || step < g_last + 24) return;
    g_last = step;
    std::error_code ec;
    const auto cmd = g_dir / "cmd";
    if (!std::filesystem::exists(cmd, ec)) return;
    std::vector<std::string> lines;
    {
        std::ifstream f(cmd);
        for (std::string l; std::getline(f, l);) {
            if (!l.empty() && l.back() == '\r') l.pop_back();
            lines.push_back(l);
        }
    }
    std::filesystem::rename(cmd, g_dir / "cmd.done", ec);
    if (ec) std::filesystem::remove(cmd, ec);
    for (const auto& l : lines) run(l);
}

}  // namespace kb
