// SPDX-License-Identifier: MIT
// kiln.vfx, the game layer: symbols from sigdb/symbols/client.d/vfx.json, three inline hooks and SEH-guarded calls into the VFX
// system and the EcsFxController (docs/re/vfx.md section 4, client 26430625 RVAs in brackets).
//  - VfxCreateInstance [0x197de0] (guard): VfxResolve aborts on a GUID the system did not register. Every VFX of the game reaches
//    it through RenderCreateVfx, so an unknown GUID stops here with handle 0, the path an empty instance pool takes.
//  - FxStart [0x2a1b50] (guard): the FxController retries a start whose create returned 0 every frame for ever, holding a pool
//    slot that sounds and VFX share. An empty GUID finishes the entry instead (the game's own code path), so the guard empties
//    an unknown one here. VfxCreateInstance is still needed for the callers that skip the FxController [0x2421d0, 0x122e010].
//  - FxUpdate [0x2a28c0, the update of client_update_fx_controller] (capture): the FxController, the game's time and the place
//    to run the queue, with the same access rights as the game's own FX calls.
// The guard reads the system's map like VfxResolve does. If that layout does not check out, or a read faults, the guard turns
// off and the game runs as vanilla; kiln.vfx then plays nothing, because it can't tell a safe GUID from an unsafe one.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>

#include "game.h"
#include "safetyhook.hpp"

namespace kvx {

namespace {

const KilnApi* g_api = nullptr;

struct Layout {
    int64_t map_count = -1, map_cap = -1, map_bits = -1, map_keys = -1, map_vals = -1, sys_table = -1, slot_resource = -1, scene_sys = -1;
    ResLayout res;
    int64_t render_holder = -1, holder_scene = -1, fxc_render = -1, fxc_world = -1, fxc_pool = -1;
    int64_t fx_guid = -1, fx_vfx_handle = -1, fx_has_end = -1, fx_end_time = -1, fx_has_finish = -1, fx_finish_time = -1;
} L;

using CreateInstanceFn = uint32_t* (*)(uint32_t* out, uintptr_t scene, const uint8_t* guid, const void* transform, const void* params);
using FxStartFn = void (*)(uintptr_t fxc, uint8_t* entry, int64_t now, void* timing);
using FxUpdateFn = void (*)(uintptr_t fxc, int64_t now, uint16_t mask);
using FxPlayAtFn = uint32_t (*)(uintptr_t fxc, int64_t now, const uint8_t* guid, const WorldTransform* t, const FxParams* p, const FxTiming* d);
using FxPlayOnFn = uint32_t (*)(uintptr_t fxc, int64_t now, const uint8_t* guid, const FxAttach* a, const FxParams* p, const FxTiming* d);
using FxStopFn = void (*)(uintptr_t fxc, uint32_t* handle, int64_t now, const FxTiming* fade);
using FxLookupFn = uint8_t* (*)(uintptr_t pool, uint32_t handle);
struct ParamSpan {
    const void* data;
    size_t size;
};
using SetParameterFn = void (*)(uintptr_t scene, uint32_t instance, uint32_t hash, uint32_t type, const ParamSpan* value);
using RenderCreateFn = uint32_t (*)(uintptr_t render, const uint8_t* guid, const FxParams* p, const WorldTransform* t);
using ResolveFn = uint32_t (*)(uintptr_t sys, const uint8_t* guid);
using GetTransformFn = const int64_t* (*)(uintptr_t world, uint32_t entity);

struct Fns {
    FxPlayAtFn play_at = nullptr;
    FxPlayOnFn play_on = nullptr;
    FxStopFn stop = nullptr;
    FxLookupFn lookup = nullptr;
    SetParameterFn set_param = nullptr;
    RenderCreateFn render_create = nullptr;
    ResolveFn resolve = nullptr;
    GetTransformFn get_transform = nullptr;
} F;

SafetyHookInline g_create_hook, g_start_hook, g_update_hook;

std::atomic<bool> g_guard_on{false};    // hooked and not found wrong

// kiln/config/vfx.json "trace": for a few seconds after each of Kiln's plays, every VFX instance the game creates is logged with its GUID,
// whether it got a parameter block, and where it was created from. For finding what else draws next to a mod's effect.
std::atomic<uint64_t> g_trace_ms{0}, g_trace_until{0};
std::atomic<int> g_trace_lines{0};
thread_local bool t_kiln_play = false;
constexpr int kTraceLines = 400;
std::atomic<bool> g_play_on{false};     // the FxController path is hooked and nothing faulted
std::atomic<bool> g_map_checked{false};
std::atomic<uintptr_t> g_sys{0};        // the last VFX system seen

bool sym(const char* n, uintptr_t* out) {
    *out = 0;
    return g_api->symbol(KilnStr{n, std::strlen(n)}, out) == KILN_OK && *out;
}
bool cst(const char* n, int64_t* out) { return g_api->constant(KilnStr{n, std::strlen(n)}, out) == KILN_OK; }
bool feat(const char* n) { return g_api->feature(KilnStr{n, std::strlen(n)}); }

// ---- SEH helpers (no C++ objects with destructors in these frames) ----
bool seh_read(uintptr_t a, void* out, size_t n) {
    __try {
        std::memcpy(out, reinterpret_cast<const void*>(a), n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool seh_find(const MapView* v, const uint8_t* guid, int* out) {
    __try {
        *out = map_find(*v, guid);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool seh_valid(const MapView* v, bool* out) {
    __try {
        *out = map_valid(*v);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
// VfxResolve's walk from a map slot to the VfxResource: values[slot] -> table[value * 0xc0] -> +slot.resource.
bool seh_lengths(uintptr_t sys, const MapView* v, const uint8_t* guid, VfxLengths* out) {
    __try {
        const int64_t slot = map_slot(*v, guid);
        if (slot < 0 || !v->values) return false;
        const uintptr_t table = *reinterpret_cast<const uintptr_t*>(sys + L.sys_table);
        const uint8_t* res = *reinterpret_cast<const uint8_t* const*>(table + v->values[slot] * 0xc0ull + L.slot_resource);
        if (!res) return false;
        read_lengths(res, L.res, out);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool seh_resource(uintptr_t sys, const MapView* v, const uint8_t* guid, void* out, size_t n) {
    __try {
        const int64_t slot = map_slot(*v, guid);
        if (slot < 0 || !v->values) return false;
        const uintptr_t table = *reinterpret_cast<const uintptr_t*>(sys + L.sys_table);
        const uint8_t* res = *reinterpret_cast<const uint8_t* const*>(table + v->values[slot] * 0xc0ull + L.slot_resource);
        if (!res) return false;
        std::memcpy(out, res, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool seh_params(uintptr_t sys, const MapView* v, const uint8_t* guid, VfxParamLayout* out) {
    __try {
        const int64_t slot = map_slot(*v, guid);
        if (slot < 0 || !v->values) return false;
        const uintptr_t table = *reinterpret_cast<const uintptr_t*>(sys + L.sys_table);
        const uint8_t* res = *reinterpret_cast<const uint8_t* const*>(table + v->values[slot] * 0xc0ull + L.slot_resource);
        if (!res) return false;
        read_params(res, L.res, out);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool seh_play_at(uintptr_t fxc, int64_t now, const uint8_t* g, const WorldTransform* t, const FxParams* p, const FxTiming* d, uint32_t* out) {
    __try {
        *out = F.play_at(fxc, now, g, t, p, d);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *out = 0;
        return false;
    }
}
bool seh_play_on(uintptr_t fxc, int64_t now, const uint8_t* g, const FxAttach* a, const FxParams* p, const FxTiming* d, uint32_t* out) {
    __try {
        *out = F.play_on(fxc, now, g, a, p, d);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *out = 0;
        return false;
    }
}
bool seh_stop(uintptr_t fxc, uint32_t fx, int64_t now) {
    __try {
        uint32_t h = fx;
        const FxTiming fade{};
        F.stop(fxc, &h, now, &fade);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
// The entry behind a handle: nullptr = gone.
bool seh_lookup(uintptr_t fxc, uint32_t fx, uint8_t** out) {
    __try {
        *out = F.lookup(fxc + L.fxc_pool, fx);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *out = nullptr;
        return false;
    }
}
bool seh_finish_now(uint8_t* entry, int64_t now) {  // what the game writes when an effect has died: the next update destroys it
    __try {
        *reinterpret_cast<int64_t*>(entry + L.fx_finish_time) = now;
        entry[L.fx_has_finish] = 1;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool seh_set_param(uintptr_t scene, uint32_t inst, uint32_t hash, uint32_t type, const void* data, size_t size) {
    __try {
        const ParamSpan s{data, size};
        F.set_param(scene, inst, hash, type, &s);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool seh_render_create(uintptr_t render, const uint8_t* g, const FxParams* p, const WorldTransform* t, uint32_t* out) {
    __try {
        *out = F.render_create(render, g, p, t);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *out = 0;
        return false;
    }
}
bool seh_resolve(uintptr_t sys, const uint8_t* g) {
    __try {
        F.resolve(sys, g);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool seh_transform(uintptr_t world, uint32_t e, int64_t out[3]) {
    __try {
        const int64_t* t = F.get_transform(world, e);
        if (!t) return false;
        out[0] = t[0];
        out[1] = t[1];
        out[2] = t[2];
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// ---- the game's objects ----
bool read_view(uintptr_t sys, MapView* v) {
    uint32_t count = 0;
    uint64_t cap = 0;
    uintptr_t bits = 0, keys = 0, vals = 0;
    if (!sys || !seh_read(sys + L.map_count, &count, sizeof count) || !seh_read(sys + L.map_cap, &cap, sizeof cap) ||
        !seh_read(sys + L.map_bits, &bits, sizeof bits) || !seh_read(sys + L.map_keys, &keys, sizeof keys))
        return false;
    if (L.map_vals >= 0) seh_read(sys + L.map_vals, &vals, sizeof vals);
    v->count = count;
    v->capacity = cap;
    v->bits = reinterpret_cast<const uint64_t*>(bits);
    v->keys = reinterpret_cast<const uint64_t*>(keys);
    v->values = reinterpret_cast<const uint32_t*>(vals);
    return true;
}

bool render_of(uintptr_t fxc, uintptr_t* render) { return fxc && seh_read(fxc + L.fxc_render, render, sizeof *render) && *render; }

// FxController -> renderer -> VFX scene. 0 with vfx disabled (--disable-vfx) or on a read fault.
uintptr_t scene_of(uintptr_t fxc) {
    uintptr_t render = 0, holder = 0, scene = 0;
    if (!render_of(fxc, &render) || !seh_read(render + L.render_holder, &holder, sizeof holder) || !holder ||
        !seh_read(holder + L.holder_scene, &scene, sizeof scene))
        return 0;
    return scene;
}

uintptr_t sys_of_scene(uintptr_t scene) {
    uintptr_t sys = 0;
    if (!scene || !seh_read(scene + L.scene_sys, &sys, sizeof sys)) return 0;
    if (sys) g_sys.store(sys, std::memory_order_relaxed);
    return sys;
}

void guard_off(const char* why) {
    if (g_guard_on.exchange(false)) {
        g_play_on.store(false);
        KV_E("vfx: %s: the guard is off and kiln.vfx plays nothing for this session; the game runs as vanilla", why);
    }
}

// The first look at a filled map: does the layout hold what the signatures promised? Called once the world runs, when the
// enumeration has finished, so the count can't be moving.
void check_map(uintptr_t sys) {
    if (g_map_checked.load(std::memory_order_relaxed)) return;
    MapView v;
    if (!read_view(sys, &v)) {
        guard_off("the VFX system could not be read");
        g_map_checked.store(true);
        return;
    }
    if (!v.count) return;
    g_map_checked.store(true);
    bool ok = false;
    if (!seh_valid(&v, &ok) || !ok) guard_off("the VFX system's map does not read as expected (a game update?)");
}

// 1 known, 0 unknown, -1 not ready. -2: the read faulted, so the guard is off.
int guard_lookup(uintptr_t sys, const uint8_t* guid) {
    MapView v;
    int r = -1;
    if (!read_view(sys, &v) || !seh_find(&v, guid, &r)) {
        guard_off("reading the VFX system's map faulted");
        return -2;
    }
    return r;
}

// ---- hooks ----
void trace_create(const uint8_t* guid, const void* params) {
    if (g_trace_lines.fetch_add(1, std::memory_order_relaxed) >= kTraceLines) return;
    uint8_t g[16] = {}, block[0x44] = {};
    seh_read(reinterpret_cast<uintptr_t>(guid), g, 16);
    const bool has_block = params && seh_read(reinterpret_cast<uintptr_t>(params), block, sizeof block) && block[0x40] != 0;
    char text[40], data[33] = "";
    guid_text(g, text);
    if (has_block)
        for (int i = 0; i < 16; ++i) std::snprintf(data + i * 2, 3, "%02x", block[i]);
    void* frames[8] = {};
    const USHORT n = RtlCaptureStackBackTrace(1, 8, frames, nullptr);
    const uintptr_t exe = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    char from[160] = "";
    size_t at = 0;
    for (USHORT i = 0; i < n && at + 16 < sizeof from; ++i) {
        const uintptr_t a = reinterpret_cast<uintptr_t>(frames[i]);
        const bool in_exe = a > exe && a - exe < 0x10000000;  // the game's image; anything else is printed whole
        at += std::snprintf(from + at, sizeof from - at, "%s%s%llx", i ? " " : "", in_exe ? "+" : "",
                            static_cast<unsigned long long>(in_exe ? a - exe : a));
    }
    const uint64_t late = GetTickCount64() + g_trace_ms.load(std::memory_order_relaxed) - g_trace_until.load(std::memory_order_relaxed);
    KV_I("vfx: trace: %s created %s, %s%s, %llu ms after Kiln's last play, from %s", t_kiln_play ? "Kiln" : "the game", text,
         has_block ? "parameter block " : "the VFX's own defaults", data, static_cast<unsigned long long>(late), from);
}

uint32_t* hk_create(uint32_t* out, uintptr_t scene, const uint8_t* guid, const void* transform, const void* params) {
    if (g_trace_ms.load(std::memory_order_relaxed) && GetTickCount64() < g_trace_until.load(std::memory_order_relaxed)) trace_create(guid, params);
    if (g_guard_on.load(std::memory_order_relaxed)) {
        const uintptr_t sys = sys_of_scene(scene);
        const int r = sys ? guard_lookup(sys, guid) : -2;
        if (r == 0 || r == -1 || core_build_refuses(guid)) {  // -1: the system has no entries yet, and VfxResolve would abort just the same
            uint8_t g[16] = {};
            seh_read(reinterpret_cast<uintptr_t>(guid), g, 16);
            guard_refused(g, "VfxCreateInstance");
            *out = 0;
            return out;
        }
    }
    return g_create_hook.call<uint32_t*>(out, scene, guid, transform, params);
}

void hk_fx_start(uintptr_t fxc, uint8_t* entry, int64_t now, void* timing) {
    if (g_guard_on.load(std::memory_order_relaxed) && entry && entry[0] == 1) {
        uint8_t* guid = entry + L.fx_guid;
        if (!zero16(guid)) {
            const uintptr_t sys = sys_of_scene(scene_of(fxc));
            // not ready (-1): leave it; the create above refuses it and the FxController asks again next frame
            if (sys && (guard_lookup(sys, guid) == 0 || core_build_refuses(guid))) {
                guard_refused(guid, "FxStart");
                std::memset(guid, 0, 16);  // the game finishes an entry with an empty GUID at once
            }
        }
    }
    g_start_hook.call<void>(fxc, entry, now, timing);
}

void hk_fx_update(uintptr_t fxc, int64_t now, uint16_t mask) {
    if (fxc) {
        const uintptr_t sys = sys_of_scene(scene_of(fxc));
        if (sys) check_map(sys);
        core_update(reinterpret_cast<void*>(fxc), now);
    }
    g_update_hook.call<void>(fxc, now, mask);  // the game's own code: a fault there stays the game's, not swallowed
}

// ---- the Game interface ----
class RealGame final : public Game {
public:
    bool can_play() const override { return g_play_on.load(std::memory_order_relaxed); }
    bool has_guard() const override { return g_guard_on.load(std::memory_order_relaxed); }
    bool is_sim() const override { return false; }

    MapView map(void* fxc) override {
        MapView v;
        uintptr_t sys = fxc ? sys_of_scene(scene_of(reinterpret_cast<uintptr_t>(fxc))) : 0;
        if (!sys) sys = g_sys.load(std::memory_order_relaxed);
        if (sys) read_view(sys, &v);
        return v;
    }

    int is_registered(void* fxc, const uint8_t guid[16]) override {
        uintptr_t sys = fxc ? sys_of_scene(scene_of(reinterpret_cast<uintptr_t>(fxc))) : 0;
        if (!sys) sys = g_sys.load(std::memory_order_relaxed);
        if (!sys) return -1;
        const int r = guard_lookup(sys, guid);
        return r == -2 ? -1 : r;
    }

    bool vfx_lengths(void* fxc, const uint8_t guid[16], VfxLengths* out) override {
        const uintptr_t sys = sys_of_scene(scene_of(reinterpret_cast<uintptr_t>(fxc)));
        MapView v;
        return sys && L.sys_table >= 0 && read_view(sys, &v) && seh_lengths(sys, &v, guid, out);
    }

    bool vfx_params(void* fxc, const uint8_t guid[16], VfxParamLayout* out) override {
        const uintptr_t sys = sys_of_scene(scene_of(reinterpret_cast<uintptr_t>(fxc)));
        MapView v;
        return sys && L.sys_table >= 0 && read_view(sys, &v) && seh_params(sys, &v, guid, out);
    }

    bool read_resource(void* fxc, const uint8_t guid[16], void* out, size_t n) override {
        uintptr_t sys = fxc ? sys_of_scene(scene_of(reinterpret_cast<uintptr_t>(fxc))) : 0;
        if (!sys) sys = g_sys.load(std::memory_order_relaxed);
        MapView v;
        return sys && L.sys_table >= 0 && read_view(sys, &v) && seh_resource(sys, &v, guid, out, n);
    }

    uint32_t play_at(const Frame& f, const uint8_t guid[16], const WorldTransform& t, const FxParams& p, const FxTiming& d) override {
        uint32_t fx = 0;
        trace_window();
        t_kiln_play = true;
        if (!seh_play_at(reinterpret_cast<uintptr_t>(f.fxc), f.now, guid, &t, &p, &d, &fx)) fault("FxPlayAt");
        t_kiln_play = false;
        return fx;
    }

    uint32_t play_on(const Frame& f, const uint8_t guid[16], const FxAttach& a, const FxParams& p, const FxTiming& d) override {
        uint32_t fx = 0;
        trace_window();
        t_kiln_play = true;
        if (!seh_play_on(reinterpret_cast<uintptr_t>(f.fxc), f.now, guid, &a, &p, &d, &fx)) fault("FxPlayAttached");
        t_kiln_play = false;
        return fx;
    }

    void stop(const Frame& f, uint32_t fx, bool immediate) override {
        const uintptr_t fxc = reinterpret_cast<uintptr_t>(f.fxc);
        uint8_t* entry = nullptr;
        if (immediate && !seh_lookup(fxc, fx, &entry)) fault("FxLookup");
        if (!seh_stop(fxc, fx, f.now)) fault("FxStop");
        if (entry && !seh_finish_now(entry, f.now)) fault("writing the FX entry");
    }

    bool lookup(const Frame& f, uint32_t fx, uint32_t* instance) override {
        uint8_t* entry = nullptr;
        if (!seh_lookup(reinterpret_cast<uintptr_t>(f.fxc), fx, &entry)) {
            fault("FxLookup");
            return false;
        }
        if (!entry) return false;
        uint32_t inst = 0;
        if (!seh_read(reinterpret_cast<uintptr_t>(entry) + L.fx_vfx_handle, &inst, sizeof inst)) {
            fault("reading the FX entry");
            return false;
        }
        *instance = inst;
        return true;
    }

    void set_param(const Frame& f, uint32_t instance, uint32_t hash, uint32_t type, const void* data, size_t size) override {
        const uintptr_t scene = scene_of(reinterpret_cast<uintptr_t>(f.fxc));
        if (scene && !seh_set_param(scene, instance, hash, type, data, size)) fault("VfxSetParameter");
    }

    bool entity_position(const Frame& f, uint32_t entity, KilnVec3* out) override {
        uintptr_t world = 0;
        if (!F.get_transform || !entity || !seh_read(reinterpret_cast<uintptr_t>(f.fxc) + L.fxc_world, &world, sizeof world) || !world)
            return false;
        int64_t p[3];
        if (!seh_transform(world, entity, p)) return false;
        out->x = fixed_to_m(p[0]);
        out->y = fixed_to_m(p[1]);
        out->z = fixed_to_m(p[2]);
        return std::fabs(out->x) < 1e6 && std::fabs(out->y) < 1e6 && std::fabs(out->z) < 1e6;
    }

    uint32_t create_direct(const Frame& f, const uint8_t guid[16]) override {
        uintptr_t render = 0;
        if (!render_of(reinterpret_cast<uintptr_t>(f.fxc), &render)) return 0;
        const FxParams p{};
        WorldTransform t{};
        t.rot[3] = 1.f;
        t.scale[0] = t.scale[1] = t.scale[2] = 1.f;
        uint32_t inst = 0;
        if (!seh_render_create(render, guid, &p, &t, &inst)) fault("RenderCreateVfx");
        return inst;
    }

    void preload(const Frame& f, const uint8_t guid[16]) override {
        const uintptr_t sys = sys_of_scene(scene_of(reinterpret_cast<uintptr_t>(f.fxc)));
        if (sys && !seh_resolve(sys, guid)) fault("VfxResolve");
    }

private:
    static void trace_window() {
        if (const uint64_t ms = g_trace_ms.load(std::memory_order_relaxed)) g_trace_until.store(GetTickCount64() + ms, std::memory_order_relaxed);
    }
    static void fault(const char* what) {
        g_play_on.store(false);
        KV_E("vfx: %s faulted: kiln.vfx plays nothing more this session (the guard stays on)", what);
    }
};

RealGame g_real;

}  // namespace

void real_game_trace(double seconds) {
    const uint64_t ms = seconds > 0 ? static_cast<uint64_t>(std::min(seconds, 60.0) * 1000) : 0;
    g_trace_ms.store(ms);
    if (ms) KV_I("vfx: trace on (kiln/config/vfx.json): every VFX the game creates within %.1f s of a play from Kiln is logged, %d lines at most", ms / 1000.0, kTraceLines);
}

Game* real_game(const KilnApi* api) {
    g_api = api;
    if (!feat("vfx.guard")) {
        KV_W("vfx: the guard's signatures did not resolve on this game build (sigdb client.d/vfx.json): kiln.vfx is off, mods play nothing");
        return nullptr;
    }
    uintptr_t a_create = 0, a_resolve = 0;
    if (!sym("vfx/CreateInstance", &a_create) || !sym("vfx/Resolve", &a_resolve) || !cst("vfx/scene.sys", &L.scene_sys) ||
        !cst("vfx/map.count", &L.map_count) || !cst("vfx/map.cap", &L.map_cap) || !cst("vfx/map.bits", &L.map_bits) ||
        !cst("vfx/map.keys", &L.map_keys)) {
        KV_W("vfx: a guard symbol is missing although vfx.guard is on: kiln.vfx is off, mods play nothing");
        return nullptr;
    }
    F.resolve = reinterpret_cast<ResolveFn>(a_resolve);
    g_create_hook = safetyhook::create_inline(reinterpret_cast<void*>(a_create), reinterpret_cast<void*>(&hk_create));
    if (!g_create_hook) {
        KV_W("vfx: could not hook VfxCreateInstance: kiln.vfx is off, mods play nothing");
        return nullptr;
    }
    g_guard_on.store(true);

    uintptr_t a_start = 0, a_update = 0, a_at = 0, a_on = 0, a_stop = 0, a_lookup = 0, a_param = 0, a_render = 0, a_tf = 0;
    const bool play = feat("vfx.play") && sym("vfx/FxStart", &a_start) && sym("vfx/FxUpdate", &a_update) && sym("vfx/FxPlayAt", &a_at) &&
                      sym("vfx/FxPlayAttached", &a_on) && sym("vfx/FxStop", &a_stop) && sym("vfx/FxLookup", &a_lookup) &&
                      sym("vfx/SetParameter", &a_param) && sym("vfx/RenderCreateVfx", &a_render) &&
                      cst("vfx/render.holder", &L.render_holder) && cst("vfx/holder.scene", &L.holder_scene) &&
                      cst("vfx/fxc.render", &L.fxc_render) && cst("vfx/fxc.world", &L.fxc_world) && cst("vfx/fxc.pool", &L.fxc_pool) &&
                      cst("vfx/fx.guid", &L.fx_guid) && cst("vfx/fx.vfx_handle", &L.fx_vfx_handle) && cst("vfx/fx.has_end", &L.fx_has_end) &&
                      cst("vfx/fx.end_time", &L.fx_end_time) && cst("vfx/fx.has_finish", &L.fx_has_finish) &&
                      cst("vfx/fx.finish_time", &L.fx_finish_time) && cst("vfx/map.vals", &L.map_vals) &&
                      cst("vfx/sys.vfx_table", &L.sys_table) && cst("vfx/vfx_slot.resource", &L.slot_resource) &&
                      cst("vfx/res.kill_time", &L.res.kill) && cst("vfx/res.params", &L.res.params) &&
                      cst("vfx/res.defaults", &L.res.defaults);
    if (!play) {
        KV_W("vfx: feature vfx.play is off on this game build (sigdb client.d/vfx.json): the guard runs, mods play nothing");
        return &g_real;
    }
    F.play_at = reinterpret_cast<FxPlayAtFn>(a_at);
    F.play_on = reinterpret_cast<FxPlayOnFn>(a_on);
    F.stop = reinterpret_cast<FxStopFn>(a_stop);
    F.lookup = reinterpret_cast<FxLookupFn>(a_lookup);
    F.set_param = reinterpret_cast<SetParameterFn>(a_param);
    F.render_create = reinterpret_cast<RenderCreateFn>(a_render);
    if (sym("GetEntityTransform", &a_tf)) F.get_transform = reinterpret_cast<GetTransformFn>(a_tf);
    else KV_W("vfx: GetEntityTransform missing: effects on an entity won't start");
    g_start_hook = safetyhook::create_inline(reinterpret_cast<void*>(a_start), reinterpret_cast<void*>(&hk_fx_start));
    g_update_hook = safetyhook::create_inline(reinterpret_cast<void*>(a_update), reinterpret_cast<void*>(&hk_fx_update));
    if (!g_start_hook || !g_update_hook) {
        KV_W("vfx: could not hook the FxController: the guard runs, mods play nothing");
        return &g_real;
    }
    g_play_on.store(true);
    return &g_real;
}

}  // namespace kvx
