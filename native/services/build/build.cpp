// SPDX-License-Identifier: MIT
// kiln.build: world edits the game itself saves and replicates (voxels through the engine's block writer and store
// commit, props with the building helper's components). See native/core/abi/services/kiln_build.h for the contract and
// docs/re/build.md for the evidence.
#include <cstdarg>
#include <cstdio>

#include "internal.h"

namespace kb {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
Layout L;
Fns fn;
Features F;
Stats S;
const KilnSpawnApi* g_spawn = nullptr;
const KilnWorldApi* g_world = nullptr;
const KilnGameplayApi* g_gameplay = nullptr;

namespace {
std::mutex g_detail_mu;
char g_detail[160] = "";
uint64_t g_steps = 0;
KilnHandle g_pre = 0;
}  // namespace

void logf(int level, const char* fmt, ...) {
    char buf[1024];
    std::va_list ap;
    va_start(ap, fmt);
    int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (n >= static_cast<int>(sizeof buf)) n = sizeof buf - 1;
    if (g_api) g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(n)});
}

void set_detail(const char* fmt, ...) {
    std::lock_guard lock(g_detail_mu);
    std::va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(g_detail, sizeof g_detail, fmt, ap);
    va_end(ap);
}

void detail_copy(char* out, size_t n) {
    std::lock_guard lock(g_detail_mu);
    std::snprintf(out, n, "%s", g_detail);
}

uint32_t fnv1a(std::string_view s, uint32_t h) {
    for (unsigned char c : s) {
        h ^= c;
        h *= 0x01000193u;
    }
    return h;
}

int64_t m_to_fixed(double m) { return static_cast<int64_t>(std::llround(m * 4294967296.0)); }

uintptr_t server_world() { return g_api && g_api->server_world ? g_api->server_world() : 0; }
bool tick_thread() { return g_api->on_tick_thread && g_api->on_tick_thread(); }

bool world_running() {
    if (g_spawn && g_spawn->world_state) return g_spawn->world_state() == KILN_SPAWN_WORLD_RUNNING;
    return g_steps > 24 * 60 * 4;  // without kiln.spawn: 4 minutes of steps
}

namespace {

template <class F>
KilnResult guarded(const char* what, F&& f) {
    try {
        return f();
    } catch (const std::exception& e) {
        KB_E("%s: %s", what, e.what());
        return KILN_ERR_INTERNAL;
    } catch (...) {
        KB_E("%s: unknown exception", what);
        return KILN_ERR_INTERNAL;
    }
}

KilnResult KILN_CALL t_status(KilnBuildStatus* out) {
    if (!out || out->size < sizeof(KilnBuildStatus)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->read = F.read;
    out->write = F.write;
    out->props = F.props;
    out->world = server_world() != 0;
    out->reads = S.reads, out->writes = S.writes, out->cells_read = S.cells_read, out->cells_written = S.cells_written;
    out->blocks_written = S.blocks_written, out->props_placed = S.props_placed, out->props_removed = S.props_removed;
    out->refused = S.refused;
    detail_copy(out->detail, sizeof out->detail);
    out->persist = F.persist;
    regions_status(&out->kept_chunks, &out->saves_seen, &out->boxes_added);
    return KILN_OK;
}
KilnResult KILN_CALL t_grid(KilnBuildGrid* out) { return guarded("grid", [&] { return vox_grid(out); }); }
KilnResult KILN_CALL t_ready(const int32_t o[3], const uint32_t d[3]) {
    return guarded("ready", [&]() -> KilnResult {
        uint64_t n = 0;
        if (!box_ok(o, d, &n)) return KILN_ERR_INVALID_ARGUMENT;
        if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
        const VoxelWorld* v = vox();
        if (!v) return KILN_ERR_UNAVAILABLE;
        return vox_ready_box(*v, o, d, true) ? KILN_OK : KILN_ERR_UNAVAILABLE;
    });
}
KilnResult KILN_CALL t_read(const int32_t o[3], const uint32_t d[3], KilnVoxel* out, size_t cap) {
    return guarded("read", [&] { return vox_read(o, d, out, cap); });
}
KilnResult KILN_CALL t_write(const KilnBuildWrite* w, KilnBuildResult* r) {
    return guarded("write", [&] { return vox_write(w, r, KILN_GAMEPLAY_DENY_BUILD | KILN_GAMEPLAY_DENY_TERRAFORM); });
}
KilnResult KILN_CALL t_shape_box(const KilnBuildShape* s, int32_t o[3], uint32_t d[3]) {
    return guarded("shape_box", [&] { return shape_box(s, o, d); });
}
KilnResult KILN_CALL t_shape(const KilnBuildShape* s, KilnBuildResult* r) {
    return guarded("shape", [&] { return shape_run(s, r); });
}
KilnResult KILN_CALL t_prop_place(const KilnBuildProp* p, KilnEntity* out) {
    return guarded("prop_place", [&] { return prop_place(p, out); });
}
KilnResult KILN_CALL t_prop_remove(KilnEntity e) { return guarded("prop_remove", [&] { return prop_remove(e); }); }
KilnResult KILN_CALL t_props_in_box(KilnVec3 lo, KilnVec3 hi, uint32_t filter, KilnBuildPropInfo* out, size_t cap,
                                    size_t* count) {
    return guarded("props_in_box", [&] { return props_in_box(lo, hi, filter, out, cap, count); });
}
KilnResult KILN_CALL t_prop_info(KilnEntity e, KilnBuildPropInfo* out) {
    return guarded("prop_info", [&] { return prop_info(e, out); });
}
KilnResult KILN_CALL t_bases(KilnBuildBase* out, size_t cap, size_t* count) {
    return guarded("bases", [&] { return bases_list(out, cap, count); });
}
uint32_t KILN_CALL t_item_material(uint32_t item) {
    try {
        return item_material(item);
    } catch (...) {
        return 0;
    }
}
KilnResult KILN_CALL t_item_template(uint32_t item, KilnGuid* out) {
    return guarded("item_template", [&] { return item_template(item, out); });
}

KilnResult KILN_CALL t_keep(const int32_t o[3], const uint32_t d[3]) {
    return guarded("keep", [&]() -> KilnResult {
        uint64_t n = 0;
        if (!box_ok(o, d, &n)) return KILN_ERR_INVALID_ARGUMENT;
        if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
        if (!F.persist) return KILN_ERR_UNAVAILABLE;
        regions_keep(o, d);
        return KILN_OK;
    });
}
KilnResult KILN_CALL t_release(const int32_t o[3], const uint32_t d[3], uint64_t* released) {
    return guarded("release", [&]() -> KilnResult {
        uint64_t n = 0;
        if (released) *released = 0;
        if (!box_ok(o, d, &n)) return KILN_ERR_INVALID_ARGUMENT;
        if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
        if (!F.persist) return KILN_ERR_UNAVAILABLE;
        const size_t r = regions_release(o, d);
        if (released) *released = r;
        return KILN_OK;
    });
}

const KilnBuildApi kTable = {
    {sizeof(KilnBuildApi), KILN_BUILD_VERSION},
    &t_status,
    &t_grid,
    &t_ready,
    &t_read,
    &t_write,
    &t_shape_box,
    &t_shape,
    &t_prop_place,
    &t_prop_remove,
    &t_props_in_box,
    &t_prop_info,
    &t_bases,
    &t_item_material,
    &t_item_template,
    &t_keep,
    &t_release,
};

void KILN_CALL on_pre(const KilnTickInfo* tick, void*) {
    ++g_steps;
    if (!server_world()) return;
    try {
        selftest_pre(tick ? tick->step : g_steps);
        if (g_steps % 24 == 0) regions_flush();
    } catch (...) {
    }
}

bool sym(const char* name, uintptr_t* out) {
    *out = 0;
    return g_api->symbol(KilnStr{name, std::strlen(name)}, out) == KILN_OK && *out != 0;
}
bool cst(const char* name, int64_t* out) { return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }
bool feat(const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); }

void resolve() {
    uintptr_t a = 0;
    if (feat("build.read")) {
        uintptr_t base = 0;
        F.read = sym("build/VoxelGridRead", &fn.grid_read) && sym("build/VoxelGridReady", &fn.grid_ready) &&
                 sym("build/voxel.base_size", &base) && cst("build/grid.interface", &L.grid_iface) &&
                 cst("build/wrapper.world", &L.wrapper_world) && cst("build/voxel.level", &L.level) &&
                 cst("build/world.lock", &L.world_lock) && cst("build/world.initialized", &L.world_init) &&
                 cst("build/world.store", &L.world_store) && cst("build/store.unavailable", &L.store_unavailable) &&
                 cst("build/world.origin", &L.world_origin) && cst("build/world.levels", &L.world_levels) &&
                 cst("build/level.stride", &L.level_stride);
        cst("build/world.global_task", &L.world_global_task);
        cst("build/world.resources", &L.world_resources);
        if (F.read) {
            float f = 0;
            F.read = seh_copy(&f, reinterpret_cast<const void*>(base), sizeof f) && f > 0 && f < 1024 && L.level >= 0 &&
                     L.level < 12 && L.grid_iface > 0 && L.grid_iface < 0x1000 && L.level_stride == 40;
            L.base_size = f;
        }
        if (!F.read) set_detail("voxel layout unexpected; reads off");
    }
    if (F.read && feat("build.write") && sym("build/VoxelWrite", &a)) {
        int64_t wl = -1;
        cst("build/voxel.write_level", &wl);
        cst("build/store.layer", &L.store_layer);
        cst("build/store.lock", &L.store_lock);
        fn.write = reinterpret_cast<Fns::WriteFn>(a);
        F.write = wl == L.level;
        if (!F.write) set_detail("the writer's level %lld differs from the grid's %lld; writes off", static_cast<long long>(wl),
                                 static_cast<long long>(L.level));
    }
    if (feat("build.items") && sym("build/ItemFind", &a)) {
        fn.item_find = reinterpret_cast<Fns::ItemFindFn>(a);
        F.items = cst("build/item.material_ref", &L.item_material_ref) && cst("build/item.material_id", &L.item_material_id) &&
                  cst("build/item.placed_entity", &L.item_placed_entity) && L.item_material_id < 0x828 &&
                  L.item_placed_entity < 0x828;
    }
    if (feat("build.zones"))
        F.zones = cst("build/bzs.bases", &L.bzs_bases) && cst("build/zone.stride", &L.zone_stride) &&
                  cst("build/zone.entity", &L.zone_entity) && L.zone_stride >= 0x48 && L.zone_entity + 7 <= L.zone_stride;
    if (!cst("build/zone.abandoned", &L.zone_abandoned) || L.zone_abandoned >= L.zone_stride) L.zone_abandoned = -1;
    if (F.write && feat("build.persist")) {
        int64_t rs = 0;
        F.persist = cst("build/region.size", &rs) && rs == 0x30;
    }
    if (sym("SpawnWrapper", &a)) fn.spawn = reinterpret_cast<Fns::SpawnWrapperFn>(a);
    if (sym("GetEntityTransform", &a)) fn.get_transform = reinterpret_cast<Fns::GetTransformFn>(a);
}

void KILN_CALL on_ready(void*) {
    g_spawn = static_cast<const KilnSpawnApi*>(g_api->service(kstr(KILN_SPAWN_SERVICE), 1));
    g_world = static_cast<const KilnWorldApi*>(g_api->service(kstr(KILN_WORLD_SERVICE), 1));
    g_gameplay = static_cast<const KilnGameplayApi*>(g_api->service(kstr(KILN_GAMEPLAY_SERVICE), 1));
    props_resolve();
    regions_ready();
    if (!g_spawn) KB_W("kiln.spawn is unavailable: props and the world-running check are off");
    if (!g_world) KB_W("kiln.world is unavailable: base protection and prop details are off");
    if (!g_gameplay) KB_W("kiln.gameplay is unavailable: KILN_BUILD_F_RESPECT_ZONES cannot check zones");
    KB_I("kiln.build: voxel read %s, write %s (kept by the save %s), items %s, props %s (spawn %s, world %s, gameplay %s)",
         F.read ? "on" : "off", F.write ? "on" : "off", F.persist ? "on" : "off", F.items ? "on" : "off",
         F.props ? "on" : "off",
         g_spawn ? "yes" : "no", g_world ? "yes" : "no", g_gameplay ? "yes" : "no");
}

}  // namespace
}  // namespace kb

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    using namespace kb;
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{KILN_BUILD_SERVICE, sizeof(KILN_BUILD_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    info->on_ready = &on_ready;
    resolve();
    char d[160];
    detail_copy(d, sizeof d);
    KB_I("kiln.build: read %s, write %s, items %s, base zones %s; level %lld, base cell %.1f m, store +0x%llx%s%s",
         F.read ? "on" : "off", F.write ? "on" : "off", F.items ? "on" : "off", F.zones ? "on" : "off",
         static_cast<long long>(L.level),
         L.base_size, static_cast<unsigned long long>(L.world_store), d[0] ? "; " : "", d);
    if (!F.read) return KILN_ERR_UNAVAILABLE;
    if (F.persist) regions_install();
    if (api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &g_pre) != KILN_OK) return KILN_ERR_UNAVAILABLE;
    selftest_init();
    return api->register_service(owner, KilnStr{KILN_BUILD_SERVICE, sizeof(KILN_BUILD_SERVICE) - 1}, &kTable);
}
