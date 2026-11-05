// SPDX-License-Identifier: MIT
// kiln.build internals. Engine facts come from sigdb/symbols/server.d/build.json; docs/re/build.md has the evidence.
#pragma once
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

#include "kiln_api.h"
#include "services/kiln_build.h"
#include "services/kiln_gameplay.h"
#include "services/kiln_spawn.h"
#include "services/kiln_world.h"

namespace kb {

extern const KilnApi* g_api;
extern KilnOwner g_owner;

void logf(int level, const char* fmt, ...);
#define KB_E(...) ::kb::logf(KILN_LOG_ERROR, __VA_ARGS__)
#define KB_W(...) ::kb::logf(KILN_LOG_WARN, __VA_ARGS__)
#define KB_I(...) ::kb::logf(KILN_LOG_INFO, __VA_ARGS__)
#define KB_D(...) ::kb::logf(KILN_LOG_DEBUG, __VA_ARGS__)

inline KilnStr kstr(std::string_view s) { return KilnStr{s.data(), s.size()}; }
inline std::string_view sv(KilnStr s) { return s.ptr ? std::string_view(s.ptr, s.len) : std::string_view(); }

uint32_t fnv1a(std::string_view s, uint32_t h = 0x811c9dc5u);
inline uint32_t ecs_hash(std::string_view name) { return fnv1a(name, fnv1a("keen::ecs::")); }
inline double fixed_to_m(int64_t v) { return static_cast<double>(v) / 4294967296.0; }
int64_t m_to_fixed(double m);

// ---- engine layout (resolved at boot) ----
struct Layout {
    int64_t grid_iface = -1;       // ecs::VoxelGrid + this = {wrapper, read, region, origin, ready, occupied, slot4}
    int64_t wrapper_world = -1;    // wrapper + this = VoxelWorld
    int64_t level = -1;            // edit level (6)
    int64_t world_lock = -1, world_init = -1, world_global_task = -1, world_resources = -1, world_store = -1;
    int64_t world_origin = -1, world_levels = -1, level_stride = 40;
    int64_t store_unavailable = -1, store_layer = -1, store_lock = -1;
    int64_t item_material_ref = -1, item_material_id = -1, item_placed_entity = -1;
    int64_t bzs_bases = -1, zone_stride = 72, zone_entity = -1, zone_abandoned = -1;  // BuildZoneSystem base zones
    float base_size = 0;           // level-0 cell size (32 m); cell size = base_size / 2^level
};
extern Layout L;

struct Fns {
    // ecs::VoxelGrid's read slot: bool (Request*, wrapper)
    uintptr_t grid_read = 0, grid_ready = 0;
    using WriteFn = void (*)(uintptr_t world, void* span, const uint32_t* dims, const int32_t* origin);
    WriteFn write = nullptr;
    using ItemFindFn = const uint8_t* (*)(uint32_t item, uintptr_t registry);
    ItemFindFn item_find = nullptr;
    using SpawnWrapperFn = uint32_t (*)(uintptr_t world, const uint8_t* guid, const void* slice);
    SpawnWrapperFn spawn = nullptr;
    using GetTransformFn = const int64_t* (*)(uintptr_t world, uint32_t entity);
    GetTransformFn get_transform = nullptr;
};
extern Fns fn;

struct Features {
    bool read = false, write = false, items = false, props = false, zones = false, persist = false;
};
extern Features F;

struct Stats {
    std::atomic<uint64_t> reads{0}, writes{0}, cells_read{0}, cells_written{0}, blocks_written{0}, props_placed{0},
        props_removed{0}, refused{0};
};
extern Stats S;

// other services (on_ready; may be null)
extern const KilnSpawnApi* g_spawn;
extern const KilnWorldApi* g_world;
extern const KilnGameplayApi* g_gameplay;

uintptr_t server_world();
bool tick_thread();
bool world_running();  // the save finished loading (kiln.spawn's Run state, or after the first Pre when it is absent)
void set_detail(const char* fmt, ...);
void detail_copy(char* out, size_t n);

// ---- voxels (voxel.cpp; tick thread) ----
constexpr uint32_t kBlock = 8;
constexpr uint64_t kMaxCells = 1ull << 22;
struct VoxelWorld {
    uintptr_t world = 0;     // server world this was resolved for
    uintptr_t grid = 0, wrapper = 0, voxels = 0;
    uintptr_t read = 0, ready = 0;
    double cell = 0.5;
    double origin[3] = {0, 0, 0};
    uint32_t tile[2] = {0, 0}, tiles[2] = {0, 0};
    bool ok = false;
};
const VoxelWorld* vox();  // resolves (once per server world); nullptr when unavailable
KilnResult vox_grid(KilnBuildGrid* out);
// true when every streaming tile under the box is loaded; with `request`, touches the missing ones (loads them)
bool vox_ready_box(const VoxelWorld& v, const int32_t origin[3], const uint32_t dims[3], bool request = false);
KilnResult vox_read(const int32_t origin[3], const uint32_t dims[3], KilnVoxel* out, size_t capacity);
// Writes `cells` (dims, x fastest; mask optional). Fills *result.
KilnResult vox_write(const KilnBuildWrite* w, KilnBuildResult* result, uint32_t zone_action);
bool box_ok(const int32_t origin[3], const uint32_t dims[3], uint64_t* count);

// world <-> cell helpers
inline int32_t cell_of(const VoxelWorld& v, int axis, double m) {
    return static_cast<int32_t>(std::floor((m - v.origin[axis]) / v.cell));
}
inline double cell_lo(const VoxelWorld& v, int axis, int32_t c) { return v.origin[axis] + c * v.cell; }

// checks shared by voxel and prop edits (policy.cpp)
uint32_t zone_denies_box(KilnEntity actor, KilnVec3 lo, KilnVec3 hi, uint32_t action);  // 0 = allowed
bool touches_base(KilnVec3 lo, KilnVec3 hi, KilnEntity* altar);
KilnResult bases_list(KilnBuildBase* out, size_t capacity, size_t* count);

// ---- shapes (shapes.cpp; tick thread) ----
KilnResult shape_box(const KilnBuildShape* s, int32_t origin[3], uint32_t dims[3]);
KilnResult shape_run(const KilnBuildShape* s, KilnBuildResult* result);

// ---- props (props.cpp; tick thread) ----
void props_resolve();
KilnResult prop_place(const KilnBuildProp* p, KilnEntity* out);
KilnResult prop_remove(KilnEntity e);
KilnResult props_in_box(KilnVec3 min, KilnVec3 max, uint32_t filter, KilnBuildPropInfo* out, size_t capacity,
                        size_t* count);
KilnResult prop_info(KilnEntity e, KilnBuildPropInfo* out);
uint32_t item_material(uint32_t item);
KilnResult item_template(uint32_t item, KilnGuid* out);

// ---- Kiln save regions (regions.cpp) ----
bool regions_install();  // kiln_plugin_main: hook the game's save-region builder
void regions_ready();    // on_ready
void regions_keep(const int32_t origin[3], const uint32_t dims[3]);      // tick: chunks of a committed write
size_t regions_release(const int32_t origin[3], const uint32_t dims[3]);  // chunks inside the box, forgotten
void regions_flush();    // Pre: write regions.json when it changed
void regions_status(uint64_t* chunks, uint64_t* saves, uint64_t* boxes);

// ---- self-test (selftest.cpp; labs: <kiln>/data/kiln.build/selftest) ----
void selftest_init();
void selftest_pre(uint64_t step);

// ---- SEH guards (seh.cpp; no C++ objects with destructors in these frames) ----
struct ReadRequest {
    uint16_t* data;
    uint64_t count;
    uint32_t dims[3];
    uint32_t level;
    int32_t origin[3];
    uint32_t pad;
};
static_assert(sizeof(ReadRequest) == 48, "engine voxel read request");
struct CellSpan {
    uint16_t* data;
    uint64_t count;
};
bool seh_read(uintptr_t fn, ReadRequest* req, uintptr_t wrapper, bool* ok);
bool seh_ready(uintptr_t fn, uintptr_t wrapper, float* point4, bool* ok);
bool seh_write(Fns::WriteFn fn, uintptr_t world, CellSpan* span, const uint32_t* dims, const int32_t* origin,
               unsigned long* code);
bool seh_copy(void* dst, const void* src, size_t n);
bool seh_item(uint32_t item, uintptr_t registry, const uint8_t** out);
bool seh_spawn(uintptr_t world, const uint8_t* guid, const void* slice, uint32_t* out, unsigned long* code);
bool seh_transform(uintptr_t world, uint32_t entity, const int64_t** out);

}  // namespace kb
