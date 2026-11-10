// SPDX-License-Identifier: MIT
// Kiln save regions: the game saves voxel edits only inside player-base build zones. Its save state machine builds the
// list of regions to save from BuildZoneSystem (build/SaveRegions: entries {BaseId[16], float3 min, pad, float3 max,
// pad}, 0x30 bytes, a game array {data, count, capacity, grow}), and the voxel serializer then writes every stored
// 32-cell chunk that overlaps a region, one blob per region; the loader applies every blob it finds, base or not
// (docs/re/build.md). Kiln remembers the 16 m chunks its own writes touched and appends boxes covering them to that
// list, so the game's own save keeps Kiln's edits outside bases too. The chunk set lives in
// kiln/data/kiln.build/regions/<world GUID>.json, written atomically when it changes.
#include <windows.h>

#include <algorithm>
#include <array>
#include <filesystem>
#include <fstream>
#include <map>
#include <set>
#include <sstream>

#include "internal.h"
#include "kiln_hook.hpp"
#include "services/kiln_saves.h"

namespace kb {
namespace {

constexpr int kChunk = 32;  // cells per store chunk edge (16 m)

struct Box {
    int32_t lo[3], hi[3];  // chunk coordinates, inclusive
};

kiln::RawHook g_hook;
std::mutex g_mu;
std::set<std::array<int32_t, 3>> g_chunks;  // Kiln-kept chunks (cell coordinates / 32)
std::string g_world;                         // world GUID text the set belongs to ("" = unknown yet)
bool g_dirty = false;
std::filesystem::path g_dir;                 // kiln/data/kiln.build/regions
std::vector<Box> g_boxes;   // boxes over g_chunks, recomputed when the set changes
bool g_boxes_stale = true;
size_t g_last_logged = ~size_t{0};
std::filesystem::path g_file;
const KilnSavesApi* g_saves = nullptr;
std::atomic<uint64_t> g_saves_seen{0}, g_boxes_added{0};

std::string guid_text(const uint8_t g[16]) {
    char b[40];
    // Windows GUID text: first three groups little-endian
    std::snprintf(b, sizeof b, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x", g[3], g[2], g[1],
                  g[0], g[5], g[4], g[7], g[6], g[8], g[9], g[10], g[11], g[12], g[13], g[14], g[15]);
    return b;
}

std::string current_world() {
    if (!g_saves) return "";
    KilnSavesStatus st{};
    st.size = sizeof st;
    if (g_saves->status(&st) != KILN_OK || !st.has_guid) return "";
    return guid_text(st.world_guid);
}

// One file per world: kiln/data/kiln.build/regions/<world GUID>.json (a game client hosts several worlds).
void read_file_locked() {
    std::error_code ec;
    if (g_file.empty() || !std::filesystem::exists(g_file, ec)) return;
    std::ifstream f(g_file);
    std::stringstream ss;
    ss << f.rdbuf();
    const std::string s = ss.str();
    // tiny hand parser: {"format":1,"world":"<guid>","chunks":[x,y,z,x,y,z,...]}
    const size_t p = s.find("\"chunks\"");
    if (p == std::string::npos) return;
    const size_t a = s.find('[', p), e = a == std::string::npos ? a : s.find(']', a);
    if (e == std::string::npos) return;
    std::vector<int32_t> v;
    std::stringstream in(s.substr(a + 1, e - a - 1));
    for (std::string tok; std::getline(in, tok, ',');) {
        char* end = nullptr;
        const long n = std::strtol(tok.c_str(), &end, 10);
        if (end != tok.c_str()) v.push_back(static_cast<int32_t>(n));
    }
    for (size_t i = 0; i + 2 < v.size(); i += 3) g_chunks.insert({v[i], v[i + 1], v[i + 2]});
}

void save_locked() {
    if (!g_dirty || g_file.empty()) return;
    std::string s = "{\n  \"format\": 1,\n  \"world\": \"" + g_world + "\",\n  \"chunk_cells\": 32,\n  \"chunks\": [";
    bool first = true;
    for (const auto& c : g_chunks) {
        s += first ? "" : ",";
        s += std::to_string(c[0]) + "," + std::to_string(c[1]) + "," + std::to_string(c[2]);
        first = false;
    }
    s += "]\n}\n";
    std::error_code ec;
    std::filesystem::create_directories(g_file.parent_path(), ec);
    std::filesystem::path tmp = g_file;
    tmp += L".tmp";
    {
        std::ofstream f(tmp, std::ios::binary | std::ios::trunc);
        f << s;
        f.flush();
        if (!f) return;
    }
    if (!MoveFileExW(tmp.c_str(), g_file.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) return;
    g_dirty = false;
}

// Binds the set to the running world (kiln.saves' GUID): a different world saves the old set and loads its own.
// Chunks kept before the GUID is known (a brand-new world before its first save) move into the world's set.
void load_locked() {
    const std::string w = current_world();
    if (w.empty() || w == g_world) return;
    std::set<std::array<int32_t, 3>> pending;
    if (g_world.empty()) pending.swap(g_chunks);
    else save_locked();
    g_chunks.clear();
    g_world = w;
    g_file = g_dir / (w + ".json");
    read_file_locked();
    const size_t loaded = g_chunks.size();
    for (const auto& c : pending) g_chunks.insert(c);
    g_dirty = g_chunks.size() != loaded;
    g_boxes_stale = true;
    g_last_logged = ~size_t{0};
    KB_I("save regions: %zu chunk(s) kept for world %s (%s)", loaded, w.c_str(), reinterpret_cast<const char*>(g_file.u8string().c_str()));
}

// Greedy boxes over the chunk set: runs along x, merged across z when equal, then across y.
std::vector<Box> boxes_locked() {
    std::map<std::pair<int32_t, int32_t>, std::vector<int32_t>> rows;  // (y, z) -> sorted x
    for (const auto& c : g_chunks) rows[{c[1], c[2]}].push_back(c[0]);
    std::vector<Box> runs;
    for (auto& [yz, xs] : rows) {
        std::sort(xs.begin(), xs.end());
        for (size_t i = 0; i < xs.size();) {
            size_t j = i;
            while (j + 1 < xs.size() && xs[j + 1] == xs[j] + 1) ++j;
            runs.push_back(Box{{xs[i], yz.first, yz.second}, {xs[j], yz.first, yz.second}});
            i = j + 1;
        }
    }
    auto merge_axis = [](std::vector<Box> in, int axis) {
        std::sort(in.begin(), in.end(), [&](const Box& a, const Box& b) {
            for (int k = 0; k < 3; ++k) {
                if (k == axis) continue;
                if (a.lo[k] != b.lo[k]) return a.lo[k] < b.lo[k];
                if (a.hi[k] != b.hi[k]) return a.hi[k] < b.hi[k];
            }
            return a.lo[axis] < b.lo[axis];
        });
        std::vector<Box> out;
        for (const Box& b : in) {
            if (!out.empty()) {
                Box& l = out.back();
                bool same = true;
                for (int k = 0; k < 3; ++k)
                    if (k != axis && (l.lo[k] != b.lo[k] || l.hi[k] != b.hi[k])) same = false;
                if (same && l.hi[axis] + 1 == b.lo[axis]) {
                    l.hi[axis] = b.hi[axis];
                    continue;
                }
            }
            out.push_back(b);
        }
        return out;
    };
    runs = merge_axis(std::move(runs), 2);
    runs = merge_axis(std::move(runs), 1);
    // far too many boxes (scattered single edits): fall back to one bounding box per 8x8 chunk area (128 m)
    if (runs.size() > 64) {
        std::map<std::pair<int32_t, int32_t>, Box> coarse;
        for (const Box& b : runs) {
            auto key = std::make_pair(b.lo[0] >> 3, b.lo[2] >> 3);
            auto [it, fresh] = coarse.emplace(key, b);
            if (!fresh)
                for (int k = 0; k < 3; ++k) it->second.lo[k] = std::min(it->second.lo[k], b.lo[k]),
                                            it->second.hi[k] = std::max(it->second.hi[k], b.hi[k]);
        }
        runs.clear();
        for (auto& [k, b] : coarse) runs.push_back(b);
    }
    return runs;
}

struct GameArray {
    uint8_t* data;
    uint64_t count;
    uint64_t capacity;
    bool (*grow)(GameArray*, int64_t, uint8_t);
};

// Appends one region; false when the array cannot grow.
bool append(GameArray* a, const Box& b, uint32_t index) {
    if (a->capacity < a->count + 1) {
        if (!a->grow || !a->grow(a, -static_cast<int64_t>(a->count + 1), 1)) return false;
    }
    uint8_t* e = a->data + a->count * 0x30;
    std::memset(e, 0, 0x30);
    const uint32_t id[4] = {0x4e4c494bu /* "KILN" */, 0x444c4942u /* "BILD" */, index, 0x6b69726eu};
    std::memcpy(e, id, 16);
    const double cs = 0.5 * kChunk;  // metres per chunk
    // a hair inside the chunk box: the serializer counts touching chunks as overlapping
    const float lo[3] = {static_cast<float>(b.lo[0] * cs + 0.05), static_cast<float>(b.lo[1] * cs + 0.05),
                         static_cast<float>(b.lo[2] * cs + 0.05)};
    const float hi[3] = {static_cast<float>((b.hi[0] + 1) * cs - 0.05), static_cast<float>((b.hi[1] + 1) * cs - 0.05),
                         static_cast<float>((b.hi[2] + 1) * cs - 0.05)};
    std::memcpy(e + 0x10, lo, sizeof lo);
    std::memcpy(e + 0x20, hi, sizeof hi);
    ++a->count;
    return true;
}

struct HookCtx {
    GameArray* list;
    const std::vector<Box>* boxes;
    uint32_t added;
};
void append_all(void* p) {
    auto* c = static_cast<HookCtx*>(p);
    for (uint32_t i = 0; i < c->boxes->size(); ++i) {
        if (!append(c->list, (*c->boxes)[i], i)) break;
        ++c->added;
    }
}
bool seh_append(HookCtx* c) {
    __try {
        append_all(c);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

uint8_t* __fastcall hk_save_regions(uint8_t* result, GameArray* list, uintptr_t zones) {
    uint8_t* r = g_hook.call<uint8_t*>(result, list, zones);
    g_saves_seen.fetch_add(1, std::memory_order_relaxed);
    if (!r || r[0] != 0 || !list) return r;  // the game failed itself: leave it alone
    std::vector<Box> boxes;
    try {
        std::lock_guard lock(g_mu);
        load_locked();
        if (g_world.empty() || g_chunks.empty()) return r;
        if (g_boxes_stale) {
            g_boxes = boxes_locked();
            g_boxes_stale = false;
        }
        boxes = g_boxes;
        if (g_dirty) save_locked();
    } catch (...) {
        return r;  // nothing may escape into the game's save
    }
    HookCtx c{list, &boxes, 0};
    const uint64_t before = list->count;
    if (!seh_append(&c)) {
        list->count = before;  // never hand the game a half-written entry
        KB_E("save regions: appending faulted; this save keeps only the game's own regions");
        return r;
    }
    g_boxes_added.fetch_add(c.added, std::memory_order_relaxed);
    if (c.added != g_last_logged) {
        g_last_logged = c.added;
        KB_I("save regions: the game's save now writes %u Kiln box(es) (%zu chunk(s)) after its %llu base region(s)",
             c.added, g_chunks.size(), static_cast<unsigned long long>(before));
    }
    return r;
}

}  // namespace

bool regions_install() {
    if (!F.persist) return false;
    char buf[1024];
    size_t len = 0;
    if (g_api->path(KILN_PATH_DATA, buf, sizeof buf, &len) == KILN_OK)
        g_dir = std::filesystem::path(std::u8string(reinterpret_cast<const char8_t*>(buf), len)) / "kiln.build" / "regions";  // UTF-8
    uintptr_t a = 0;
    if (g_api->symbol(kstr("build/SaveRegions"), &a) != KILN_OK || !a) return F.persist = false;
    if (!g_hook.install(g_api, g_owner, a, reinterpret_cast<void*>(&hk_save_regions), "build/SaveRegions")) {
        KB_E("hooking the save-region builder failed: Kiln edits outside bases are not saved");
        return F.persist = false;
    }
    return true;
}

void regions_ready() {
    g_saves = static_cast<const KilnSavesApi*>(g_api->service(kstr(KILN_SAVES_SERVICE), 1));
    std::lock_guard lock(g_mu);
    load_locked();
}

void regions_keep(const int32_t origin[3], const uint32_t dims[3]) {
    if (!F.persist) return;
    std::lock_guard lock(g_mu);
    load_locked();
    const size_t before = g_chunks.size();
    auto chunk = [](int64_t c) { return static_cast<int32_t>(c >= 0 ? c / kChunk : -((-c + kChunk - 1) / kChunk)); };
    for (int32_t z = chunk(origin[2]); z <= chunk(static_cast<int64_t>(origin[2]) + dims[2] - 1); ++z)
        for (int32_t y = chunk(origin[1]); y <= chunk(static_cast<int64_t>(origin[1]) + dims[1] - 1); ++y)
            for (int32_t x = chunk(origin[0]); x <= chunk(static_cast<int64_t>(origin[0]) + dims[0] - 1); ++x)
                g_chunks.insert({x, y, z});
    if (g_chunks.size() != before) g_dirty = g_boxes_stale = true;
}

size_t regions_release(const int32_t origin[3], const uint32_t dims[3]) {
    std::lock_guard lock(g_mu);
    load_locked();
    size_t n = 0;
    for (auto it = g_chunks.begin(); it != g_chunks.end();) {
        const auto& c = *it;
        bool in = true;
        for (int a = 0; a < 3; ++a) {
            const int64_t lo = static_cast<int64_t>(c[a]) * kChunk, hi = lo + kChunk;
            if (hi <= origin[a] || lo >= static_cast<int64_t>(origin[a]) + dims[a]) in = false;
        }
        if (in) {
            it = g_chunks.erase(it);
            ++n;
        } else {
            ++it;
        }
    }
    if (n) g_dirty = g_boxes_stale = true;
    return n;
}

void regions_flush() {
    std::lock_guard lock(g_mu);
    load_locked();
    if (g_dirty) save_locked();
}

void regions_status(uint64_t* chunks, uint64_t* saves, uint64_t* boxes) {
    std::lock_guard lock(g_mu);
    *chunks = g_chunks.size();
    *saves = g_saves_seen.load();
    *boxes = g_boxes_added.load();
}

}  // namespace kb
