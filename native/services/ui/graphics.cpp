// SPDX-License-Identifier: MIT
// kiln.ui: Kiln's own graphics tools in the game (docs/kiln/graphics.md; what the game does with DLSS:
// docs/re/client-upscale.md).
//  - The frame cap: right after each of the game's presents (Kiln's present hook, vk.cpp), the presenting thread waits
//    for the game's next slot (frame_pacer.h) before it goes back to the game: a timer for most of the wait, a short
//    spin on the clock for the rest. The frame is shown as soon as it is done; the game starts its next one later, as a
//    limiter in the game would. So the cap adds no latency to a finished frame, and display times follow the game's
//    render times (a frame that takes longer shows later).
//  - DLSS settings: the game links NVIDIA's NGX SDK statically and reaches NGX's core (_nvngx.dll, NVIDIA's driver
//    component; Proton ships it too) through GetProcAddress. Kiln hooks five of the core's exports once the game loads it:
//      GetCapabilityParameters  Kiln puts its own optimal-settings callback in front of DLSS's (the documented
//                               "DLSSOptimalSettingsCallback" parameter): the render size the game then asks for;
//      CreateFeature(1)         the render preset hints ("DLSS.Hint.Render.Preset.*"), set after the game's own;
//      EvaluateFeature          what DLSS renders at this frame (the readout);
//      ReleaseFeature           DLSS gone.
//    Only documented NGX parameters are set, through NGX's own parameter object (ngx_param.h); the game's code is not
//    patched. The game sets DLSS up again (and so asks again) when a world loads and when its DLSS mode or resolution
//    changes: the settings follow then.
//  - The readout: a few lines of ImGui text in the top right corner of the overlay.
// Nothing of it runs for a player who changes nothing: no NGX hook and no new LoadLibraryExW hook unless a DLSS setting
// or the readout is on at start; with the cap and the readout off, a present costs one relaxed load here (ui.h
// graphics_after_present) and one in the overlay's want_draw (g_graphics_demand). Every tool steps aside for tools that
// do the same (graphics_aside.h).
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>
#include <tuple>
#include <vector>

#include "frame_pacer.h"
#include "graphics_aside.h"
#include "graphics_settings.h"
#include "imgui.h"
#include "ngx_param.h"
#include "nlohmann/json.hpp"
#include "safetyhook.hpp"
#include "ui.h"
#include "ui_text.h"

namespace kui {

std::atomic<uint32_t> g_graphics_demand{0};
std::atomic<bool> g_graphics_present{false};

namespace {

std::mutex g_mu;          // g_settings, g_logged_*
gfx::Settings g_settings; // the player's, clamped
std::atomic<uint32_t> g_fps_cap{0};
std::atomic<bool> g_readout{false};
uint32_t g_aside = 0;     // gfx::Aside found at start (files, the overlay's mode); fixed after graphics_start
std::atomic<uint32_t> g_aside_run{0};  // found later (NGX patched by another module)
bool g_ngx_wanted = false;             // hook NGX when its core loads (decided at start)
std::atomic<bool> g_ngx_tried{false}, g_ngx_hooked{false};
bool g_started = false;

std::string api_path(int kind) {
    char b[2048];
    size_t n = 0;
    if (!g_api || g_api->path(kind, b, sizeof b, &n) != KILN_OK || n >= sizeof b) return {};
    return std::string(b, n);
}
std::filesystem::path u8path(const std::string& s) { return std::filesystem::path(std::u8string(s.begin(), s.end())); }

// ---------------------------------------------------------------------------------------------------------------
// The frame cap and the frame counts (render thread)

gfx::FramePacer g_pacer;
gfx::FrameStats g_stats;
gfx::TimerMargin g_margin;
int64_t g_freq = 0;
HANDLE g_timer = nullptr;
bool g_timer_tried = false;
std::atomic<bool> g_counting{false};
uint64_t g_last_count_ms = 0;
std::atomic<float> g_rendered_fps{0}, g_output_fps{0}, g_frame_ms{0};
std::atomic<bool> g_cap_active{false};

int64_t qpc() {
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return t.QuadPart;
}

// Waits until `target` (QPC ticks): the timer first, then a spin (frame_pacer.h). Never longer than 1 s (a clock jump).
void wait_until(int64_t target) {
    int64_t now = qpc();
    if (target - now > g_freq) target = now + g_freq;
    const auto plan = gfx::wait_plan(target - now, g_margin.margin(g_freq));
    if (plan.timer > 0) {
        if (!g_timer && !g_timer_tried) {
            g_timer_tried = true;
            // a high-resolution timer where the system has one (Windows 10 1803+), else a plain one (the spin covers it)
            g_timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            const bool high = g_timer != nullptr;
            if (!g_timer) g_timer = CreateWaitableTimerExW(nullptr, nullptr, 0, TIMER_ALL_ACCESS);
            logf(KILN_LOG_INFO, "graphics: frame cap timer: %s", g_timer ? (high ? "high resolution" : "plain") : "none (spin only)");
        }
        if (g_timer) {
            const int64_t due = now + plan.timer;
            LARGE_INTEGER rel;
            rel.QuadPart = -std::max<int64_t>(1, plan.timer * 10'000'000 / g_freq);  // 100 ns units, relative
            if (SetWaitableTimer(g_timer, &rel, 0, nullptr, nullptr, FALSE)) {
                WaitForSingleObject(g_timer, static_cast<DWORD>(plan.timer * 1000 / g_freq + 50));
                g_margin.observe(qpc() - due);
            }
        }
    }
    if (plan.spin)
        while (qpc() < target) YieldProcessor();
}

// ---------------------------------------------------------------------------------------------------------------
// DLSS (NGX) hooks

using GetCapsFn = ngx::Result (*)(void** out);
using CreateFn = ngx::Result (*)(void* cmd, int32_t feature, void* params, void** out_handle);
using Create1Fn = ngx::Result (*)(void* device, void* cmd, int32_t feature, void* params, void** out_handle);
using EvaluateFn = ngx::Result (*)(void* cmd, const void* handle, const void* params, void* progress);
using ReleaseFn = ngx::Result (*)(void* handle);

std::atomic<GetCapsFn> o_get_caps{nullptr};
std::atomic<CreateFn> o_create{nullptr};
std::atomic<Create1Fn> o_create1{nullptr};
std::atomic<EvaluateFn> o_evaluate{nullptr};
std::atomic<ReleaseFn> o_release{nullptr};
std::atomic<ngx::OptimalSettingsFn> g_dlss_optimal{nullptr};  // DLSS's own optimal-settings callback

// The DLSS in use (status, readout)
std::atomic<const void*> g_dlss_handle{nullptr};
std::atomic<int32_t> g_dlss_quality{-1};
std::atomic<uint32_t> g_dlss_preset{0}, g_dlss_flags{0};
std::atomic<uint32_t> g_render_w{0}, g_render_h{0}, g_dlss_out_w{0}, g_dlss_out_h{0};
std::atomic<uint64_t> g_last_eval_ms{0};
// The last size Kiln's callback set, to tell whether the DLSS created after it uses it.
std::mutex g_scaled_mu;
struct Scaled {
    int quality = -2;
    gfx::Size output, render;
    bool applied = false;  // Kiln's step set `render` (else it is DLSS's own)
    bool limited = false;  // DLSS's range for the mode limited the player's scale
} g_scaled;

uint32_t g_fault_code = 0;
int fault_filter(EXCEPTION_POINTERS* ep) {
    g_fault_code = ep->ExceptionRecord->ExceptionCode;
    return EXCEPTION_EXECUTE_HANDLER;
}
// Kiln's own work inside an NGX call runs under a fault guard: a fault turns Kiln's part off, never the game's call.
std::atomic<bool> g_ngx_faulted{false};
void said_fault(const char* where) {
    if (g_ngx_faulted.exchange(true)) return;
    logf(KILN_LOG_ERROR, "graphics: Kiln's DLSS step faulted (0x%08x in %s): Kiln's DLSS settings are off for this session", g_fault_code, where);
}

gfx::Settings settings_now() {
    std::lock_guard lk(g_mu);
    return g_settings;
}

// DLSS's optimal-settings answer, then Kiln's render size (graphics_settings.h render_size).
void scale_answer(void* params) {
    const gfx::Settings s = settings_now();
    int32_t quality = -1;
    gfx::Size out, dlss, min, max;
    if (!ngx::ok(ngx::get_i(params, ngx::kPerfQualityValue, &quality))) return;
    ngx::get_ui(params, ngx::kWidth, &out.w);
    ngx::get_ui(params, ngx::kHeight, &out.h);
    ngx::get_ui(params, ngx::kOutWidth, &dlss.w);
    ngx::get_ui(params, ngx::kOutHeight, &dlss.h);
    ngx::get_ui(params, ngx::kMinWidth, &min.w);
    ngx::get_ui(params, ngx::kMinHeight, &min.h);
    ngx::get_ui(params, ngx::kMaxWidth, &max.w);
    ngx::get_ui(params, ngx::kMaxHeight, &max.h);
    const auto pick = gfx::pick_render_size(s, quality, out, dlss, min, max);
    const auto& size = pick.set;
    {
        std::lock_guard lk(g_scaled_mu);
        g_scaled = {quality, out, size ? *size : dlss, size.has_value(), pick.limited};
    }
    if (pick.limited) {
        static std::mutex lim_mu;
        static std::vector<std::tuple<int, uint32_t, uint32_t, uint32_t, uint32_t>> said_lim;
        std::lock_guard lk(lim_mu);
        const auto key = std::make_tuple(int(quality), out.w, out.h, pick.wanted.w, pick.wanted.h);
        if (std::find(said_lim.begin(), said_lim.end(), key) == said_lim.end() && said_lim.size() < 32) {
            said_lim.push_back(key);
            logf(KILN_LOG_INFO, "graphics: DLSS %s for %ux%u: Kiln's scale asks for %ux%u, outside DLSS's range %ux%u..%ux%u for this mode: "
                 "it renders at %ux%u", gfx::mode_name(quality), out.w, out.h, pick.wanted.w, pick.wanted.h, min.w, min.h, max.w, max.h,
                 size ? size->w : dlss.w, size ? size->h : dlss.h);
        }
    }
    if (!size) return;
    ngx::set_ui(params, ngx::kOutWidth, size->w);
    ngx::set_ui(params, ngx::kOutHeight, size->h);
    static std::mutex said_mu;
    static std::vector<std::tuple<int, uint32_t, uint32_t, uint32_t, uint32_t>> said;
    std::lock_guard lk(said_mu);
    const auto key = std::make_tuple(int(quality), out.w, out.h, size->w, size->h);
    if (std::find(said.begin(), said.end(), key) == said.end() && said.size() < 32) {
        said.push_back(key);
        logf(KILN_LOG_INFO, "graphics: DLSS %s for %ux%u renders at %ux%u (Kiln's %s scale; DLSS's own: %ux%u, its range %ux%u..%ux%u)",
             gfx::mode_name(quality), out.w, out.h, size->w, size->h, gfx::scale_name(s.dlss_scale), dlss.w, dlss.h, min.w, min.h, max.w, max.h);
    }
}
bool guarded_scale_answer(void* params) {
    __try {
        scale_answer(params);
        return true;
    } __except (fault_filter(GetExceptionInformation())) {
        return false;
    }
}

// In front of DLSS's own callback: the game calls it for the render size of its DLSS mode.
ngx::Result optimal_settings(void* params) {
    const auto real = g_dlss_optimal.load();
    if (!real) return ngx::kFail;
    const ngx::Result r = real(params);
    if (ngx::ok(r) && params && !g_ngx_faulted.load(std::memory_order_relaxed) && !guarded_scale_answer(params))
        said_fault("the optimal-settings callback");
    return r;
}

void take_callback(void* params) {
    void* cb = nullptr;
    if (!ngx::ok(ngx::get_ptr(params, ngx::kOptimalSettingsCallback, &cb)) || !cb) {
        static std::atomic<bool> said{false};
        if (!said.exchange(true))
            logf(KILN_LOG_WARN, "graphics: DLSS's optimal-settings callback is not in NGX's capability parameters: Kiln's DLSS render scale does nothing in this session");
        return;
    }
    if (cb == reinterpret_cast<void*>(&optimal_settings)) return;
    g_dlss_optimal.store(reinterpret_cast<ngx::OptimalSettingsFn>(cb));
    ngx::set_ptr(params, ngx::kOptimalSettingsCallback, reinterpret_cast<void*>(&optimal_settings));
    static std::atomic<bool> said{false};
    if (!said.exchange(true)) logf(KILN_LOG_INFO, "graphics: Kiln's render-size step is in front of DLSS's optimal-settings callback");
}
bool guarded_take_callback(void* params) {
    __try {
        take_callback(params);
        return true;
    } __except (fault_filter(GetExceptionInformation())) {
        return false;
    }
}

ngx::Result hk_get_caps(void** out) {
    const ngx::Result r = o_get_caps.load()(out);
    if (ngx::ok(r) && out && *out && !g_ngx_faulted.load(std::memory_order_relaxed) && !guarded_take_callback(*out))
        said_fault("GetCapabilityParameters");
    return r;
}

// Before DLSS is created: Kiln's render preset (not for DLAA), and what the status shows.
void before_create(void* params) {
    const gfx::Settings s = settings_now();
    int32_t quality = -1;
    ngx::get_i(params, ngx::kPerfQualityValue, &quality);
    uint32_t game_preset = 0;
    if (const char* hint = ngx::preset_hint_for(quality)) ngx::get_ui(params, hint, &game_preset);
    uint32_t flags = 0, preset = game_preset;
    if (s.dlss_preset != gfx::kPresetDefault && quality != gfx::kDlaa) {
        for (const char* hint : ngx::kPresetHints) ngx::set_ui(params, hint, s.dlss_preset);
        preset = s.dlss_preset;
        flags |= KILN_UI_GFX_DLSS_PRESET;
    }
    gfx::Size render, out;
    ngx::get_ui(params, ngx::kWidth, &render.w);
    ngx::get_ui(params, ngx::kHeight, &render.h);
    ngx::get_ui(params, ngx::kOutWidth, &out.w);
    ngx::get_ui(params, ngx::kOutHeight, &out.h);
    {
        std::lock_guard lk(g_scaled_mu);
        if (g_scaled.quality == quality && g_scaled.output == out && g_scaled.render == render) {
            if (g_scaled.applied) flags |= KILN_UI_GFX_DLSS_SCALED;    // created at the size Kiln's step gave
            if (g_scaled.limited) flags |= KILN_UI_GFX_DLSS_LIMITED;   // DLSS's range limited the player's scale
        }
    }
    g_dlss_quality.store(quality);
    g_dlss_preset.store(preset);
    g_dlss_flags.store(flags);
    g_render_w.store(render.w);
    g_render_h.store(render.h);
    g_dlss_out_w.store(out.w);
    g_dlss_out_h.store(out.h);
    logf(KILN_LOG_INFO, "graphics: DLSS %s created: %ux%u -> %ux%u, render preset %s%s", gfx::mode_name(quality), render.w, render.h,
         out.w, out.h, gfx::preset_name(preset).c_str(),
         (flags & KILN_UI_GFX_DLSS_PRESET) ? " (Kiln's)" : quality == gfx::kDlaa && s.dlss_preset ? " (the game's: DLAA keeps it)" : " (the game's)");
}
bool guarded_before_create(void* params) {
    __try {
        before_create(params);
        return true;
    } __except (fault_filter(GetExceptionInformation())) {
        return false;
    }
}
void created(int32_t feature, void* params, ngx::Result r, void** out_handle) {
    if (feature != ngx::kFeatureSuperSampling || !params) return;
    if (ngx::ok(r) && out_handle && *out_handle) g_dlss_handle.store(*out_handle);
}

ngx::Result hk_create(void* cmd, int32_t feature, void* params, void** out_handle) {
    if (feature == ngx::kFeatureSuperSampling && params && !g_ngx_faulted.load(std::memory_order_relaxed) && !guarded_before_create(params))
        said_fault("CreateFeature");
    const ngx::Result r = o_create.load()(cmd, feature, params, out_handle);
    created(feature, params, r, out_handle);
    return r;
}
ngx::Result hk_create1(void* device, void* cmd, int32_t feature, void* params, void** out_handle) {
    if (feature == ngx::kFeatureSuperSampling && params && !g_ngx_faulted.load(std::memory_order_relaxed) && !guarded_before_create(params))
        said_fault("CreateFeature1");
    const ngx::Result r = o_create1.load()(device, cmd, feature, params, out_handle);
    created(feature, params, r, out_handle);
    return r;
}

void read_subrect(const void* params) {
    uint32_t w = 0, h = 0;
    if (ngx::ok(ngx::get_ui(params, ngx::kSubrectWidth, &w)) && ngx::ok(ngx::get_ui(params, ngx::kSubrectHeight, &h)) && w && h) {
        g_render_w.store(w, std::memory_order_relaxed);
        g_render_h.store(h, std::memory_order_relaxed);
    }
}
bool guarded_read_subrect(const void* params) {
    __try {
        read_subrect(params);
        return true;
    } __except (fault_filter(GetExceptionInformation())) {
        return false;
    }
}
ngx::Result hk_evaluate(void* cmd, const void* handle, const void* params, void* progress) {
    if (handle && handle == g_dlss_handle.load(std::memory_order_relaxed)) {
        g_last_eval_ms.store(now_ms(), std::memory_order_relaxed);
        if (params && g_readout.load(std::memory_order_relaxed) && !g_ngx_faulted.load(std::memory_order_relaxed) &&
            !guarded_read_subrect(params))
            said_fault("EvaluateFeature");
    }
    return o_evaluate.load()(cmd, handle, params, progress);
}
ngx::Result hk_release(void* handle) {
    const void* mine = handle;
    if (handle && g_dlss_handle.compare_exchange_strong(mine, nullptr)) {
        g_dlss_quality.store(-1);
        g_dlss_flags.store(0);
    }
    return o_release.load()(handle);
}

bool read_bytes(const void* at, uint8_t* out, size_t n) {
    __try {
        std::memcpy(out, at, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

std::string module_file(HMODULE m) {
    wchar_t w[MAX_PATH] = {};
    if (!m || !GetModuleFileNameW(m, w, MAX_PATH)) return {};
    const auto u = std::filesystem::path(w).u8string();
    return std::string(u.begin(), u.end());
}

// Where a function's first instruction jumps (patched_entry's forms); null when it cannot be read.
const uint8_t* jump_target(const uint8_t* at) {
    uint8_t b[16] = {};
    if (!read_bytes(at, b, sizeof b)) return nullptr;
    int32_t rel = 0;
    if (b[0] == 0xE9) {
        std::memcpy(&rel, b + 1, 4);
        return at + 5 + rel;
    }
    if (b[0] == 0xEB) return at + 2 + static_cast<int8_t>(b[1]);
    if (b[0] == 0xFF && b[1] == 0x25) {
        std::memcpy(&rel, b + 2, 4);
        uint64_t abs = 0;
        return read_bytes(at + 6 + rel, reinterpret_cast<uint8_t*>(&abs), 8) ? reinterpret_cast<const uint8_t*>(abs) : nullptr;
    }
    if (b[0] == 0x48 || b[0] == 0x49) {
        uint64_t abs = 0;
        std::memcpy(&abs, b + 2, 8);
        return reinterpret_cast<const uint8_t*>(abs);
    }
    return nullptr;
}

HMODULE module_at(const void* at) {
    HMODULE m = nullptr;
    if (!at || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                   reinterpret_cast<LPCWSTR>(at), &m))
        return nullptr;
    return m;
}

std::string file_name(HMODULE m) {
    const auto f = module_file(m);
    const auto slash = f.find_last_of("\\/");
    return slash == std::string::npos ? f : f.substr(slash + 1);
}

struct Export {
    const char* name;
    void* detour;
    bool required;
    void* target = nullptr;
};

// NGX's core is loaded: hook its exports, or step aside when another module patched them first. Once for each load of
// the core (graphics_after_load).
void hook_ngx(HMODULE core) {
    const std::string file = module_file(core);
    Export ex[] = {
        {"NVSDK_NGX_VULKAN_GetCapabilityParameters", reinterpret_cast<void*>(&hk_get_caps), true},
        {"NVSDK_NGX_VULKAN_CreateFeature", reinterpret_cast<void*>(&hk_create), true},
        {"NVSDK_NGX_VULKAN_CreateFeature1", reinterpret_cast<void*>(&hk_create1), false},
        {"NVSDK_NGX_VULKAN_EvaluateFeature", reinterpret_cast<void*>(&hk_evaluate), true},
        {"NVSDK_NGX_VULKAN_ReleaseFeature", reinterpret_cast<void*>(&hk_release), true},
    };
    for (auto& e : ex) {
        e.target = reinterpret_cast<void*>(GetProcAddress(core, e.name));
        if (!e.target && e.required) {
            logf(KILN_LOG_WARN, "graphics: NVIDIA's DLSS core %s has no %s: Kiln's DLSS settings are off", file.c_str(), e.name);
            return;
        }
        uint8_t b[16] = {};
        if (!e.target) continue;
        const bool readable = read_bytes(e.target, b, sizeof b);
        if (readable && !gfx::patched_entry(b)) continue;
        // a jump at the entry: a thunk into the core itself (a linker's, an NVIDIA build's) is NVIDIA's own code; one
        // into another module, or one that cannot be followed, is another tool's hook
        const HMODULE into = readable ? module_at(jump_target(static_cast<const uint8_t*>(e.target))) : nullptr;
        if (into == core) continue;
        const std::string who = into ? file_name(into) : std::string();
        g_aside_run.fetch_or(gfx::kAsideNgxPatched);
        logf(KILN_LOG_INFO, "graphics: %s in %s is already patched (%s%s): another tool hooks DLSS, so Kiln's DLSS settings step aside "
             "(the frame cap and the readout stay)", e.name, file.c_str(), who.empty() ? "it jumps outside any module Kiln can name" : "it jumps into ",
             who.c_str());
        return;
    }
    // made disabled, the originals published, then enabled: a call on another thread never meets a half-made hook. The
    // hooks are never removed (a trampoline may be running; kiln.ui is never unloaded).
    std::vector<SafetyHookInline*> made;
    for (size_t i = 0; i < std::size(ex); ++i) {
        if (!ex[i].target) {
            made.push_back(nullptr);
            continue;
        }
        auto* h = new SafetyHookInline(safetyhook::create_inline(ex[i].target, ex[i].detour, SafetyHookInline::StartDisabled));
        if (!*h) {
            logf(KILN_LOG_ERROR, "graphics: hooking %s failed: Kiln's DLSS settings are off", ex[i].name);
            return;  // nothing enabled yet; the made ones stay disabled (never freed: a trampoline is never freed)
        }
        switch (i) {
            case 0: o_get_caps.store(h->original<GetCapsFn>()); break;
            case 1: o_create.store(h->original<CreateFn>()); break;
            case 2: o_create1.store(h->original<Create1Fn>()); break;
            case 3: o_evaluate.store(h->original<EvaluateFn>()); break;
            default: o_release.store(h->original<ReleaseFn>()); break;
        }
        made.push_back(h);
    }
    for (size_t i = 0; i < made.size(); ++i) {
        if (!made[i]) continue;
        if (!made[i]->enable()) {
            logf(KILN_LOG_ERROR, "graphics: enabling the hook on %s failed", ex[i].name);
            if (ex[i].required) {
                for (size_t j = 0; j < i; ++j)
                    if (made[j]) (void)made[j]->disable();
                logf(KILN_LOG_ERROR, "graphics: Kiln's DLSS settings are off");
                return;
            }
        }
    }
    g_ngx_hooked = true;
    logf(KILN_LOG_INFO, "graphics: hooked NVIDIA's DLSS core %s (GetCapabilityParameters, CreateFeature%s, EvaluateFeature, ReleaseFeature)",
         file.c_str(), ex[2].target ? ", CreateFeature1" : "");
}

// ---------------------------------------------------------------------------------------------------------------

std::string describe(const gfx::Settings& s) {
    std::string out;
    char b[96];
    if (s.fps_cap) { std::snprintf(b, sizeof b, "frame cap %u fps", s.fps_cap); out += b; }
    if (s.dlss_preset) { out += out.empty() ? "" : ", "; out += "DLSS preset " + gfx::preset_name(s.dlss_preset); }
    if (s.dlss_scale == gfx::DlssScale::custom) {
        std::snprintf(b, sizeof b, "DLSS render scale %.2f for every mode", s.custom_scale);
        out += out.empty() ? "" : ", ";
        out += b;
    } else if (s.dlss_scale == gfx::DlssScale::per_mode) {
        std::snprintf(b, sizeof b, "DLSS render scale per mode %.2f/%.2f/%.2f/%.2f", s.mode_scale[0], s.mode_scale[1], s.mode_scale[2], s.mode_scale[3]);
        out += out.empty() ? "" : ", ";
        out += b;
    }
    if (s.readout) out += out.empty() ? "readout on" : ", readout on";
    return out.empty() ? "all off" : out;
}

// The frame cap (not stepped aside) or the readout is on: the present hook calls graphics_present. Any thread.
bool present_wanted() {
    return (g_fps_cap.load() && !(g_aside & gfx::kAsideCap)) || g_readout.load();
}

void publish(const gfx::Settings& s) {
    g_fps_cap.store(s.fps_cap);
    g_readout.store(s.readout);
    g_graphics_demand.store(s.readout && overlay_drawing_on() ? 1u : 0u, std::memory_order_relaxed);
    // turned off: left on until graphics_present clears the counts and turns it off itself (render thread)
    if (present_wanted()) g_graphics_present.store(true);
}

std::string fmt_fps(float v) {
    char b[16];
    std::snprintf(b, sizeof b, "%.0f", v);
    return b;
}
std::string fmt_ms(float v) {
    char b[16];
    std::snprintf(b, sizeof b, "%.1f", v);
    return b;
}
std::string fmt_size(uint32_t w, uint32_t h) {
    char b[32];
    std::snprintf(b, sizeof b, "%ux%u", w, h);
    return b;
}

}  // namespace

void graphics_start(bool optiscaler, bool streamline) {
    std::vector<std::string> warnings;
    gfx::Settings s;
    try {
        std::ifstream in(u8path(api_path(KILN_PATH_KILN)) / "kiln.json", std::ios::binary);
        if (in) {
            const auto doc = nlohmann::json::parse(in, nullptr, false, true);  // JSONC, as the core reads it
            if (!doc.is_discarded()) s = gfx::parse_settings(doc, &warnings);
        }
    } catch (...) {
    }
    for (const auto& w : warnings) logf(KILN_LOG_WARN, "graphics: kiln.json %s", w.c_str());
    gfx::AsideFiles files;
    try {
        files = gfx::detect_aside_files(u8path(api_path(KILN_PATH_GAME)), optiscaler, streamline);
    } catch (...) {
    }
    {
        std::lock_guard lk(g_mu);
        g_settings = s;
        g_aside = files.bits | (overlay_drawing_on() ? 0u : static_cast<uint32_t>(gfx::kAsideOverlayOff));
        g_ngx_wanted = s.wants_ngx() && !(g_aside & gfx::kAsideDlss);
        g_started = true;
    }
    publish(s);
    std::string line = describe(s);
    if (g_ngx_wanted) line += "; DLSS is hooked when the game loads NVIDIA's DLSS core";
    if (const uint32_t aside = g_aside & gfx::kAsideDlss) {
        std::string found;
        for (const auto& f : files.files) found += (found.empty() ? "" : ", ") + f;
        line += (g_aside & gfx::kAsideCap) ? "; Kiln's frame cap and DLSS settings step aside for " : "; Kiln's DLSS settings step aside for ";
        line += gfx::aside_names(aside);
        if (!found.empty()) line += " (" + found + ")";
        if (aside & gfx::kAsideOverlayOff) line += ": " + g_overlay_reason;
        if (s.readout && (aside & gfx::kAsideOverlayOff)) line += "; the readout needs Kiln's overlay too";
    }
    // only when the player turned something on: a player who changed nothing gets no line, whatever is installed
    if (s.fps_cap || s.wants_ngx()) logf(KILN_LOG_INFO, "graphics: %s", line.c_str());
}

bool graphics_wants_load_hook() {
    std::lock_guard lk(g_mu);
    return g_ngx_wanted;
}

void graphics_after_load() {
    if (!g_ngx_wanted) return;
    // stepped aside, or the hooks could not be made: so for the session
    if (g_ngx_tried.load(std::memory_order_relaxed) && !g_ngx_hooked.load(std::memory_order_relaxed)) return;
    HMODULE core = GetModuleHandleW(L"_nvngx.dll");
    if (!core) core = GetModuleHandleW(L"nvngx.dll");
    const auto* create = core ? reinterpret_cast<const uint8_t*>(GetProcAddress(core, "NVSDK_NGX_VULKAN_CreateFeature")) : nullptr;
    if (!create) return;
    // one thread at a time; another thread's load passes (nothing here waits: the thread may hold the loader lock)
    static std::atomic_flag busy;
    if (busy.test_and_set()) return;
    if (!g_ngx_tried.exchange(true)) {
        hook_ngx(core);
    } else if (g_ngx_hooked.load()) {
        // Kiln's hook puts a jump at CreateFeature's entry. Plain code there again: the game unloaded NGX's core and loaded
        // it anew (Kiln's hooks went with the old one), so it is hooked again, by the same rules as the first time.
        uint8_t b[16] = {};
        if (read_bytes(create, b, sizeof b) && !gfx::patched_entry(b)) {
            logf(KILN_LOG_INFO, "graphics: NVIDIA's DLSS core %s was loaded again: Kiln hooks it again", module_file(core).c_str());
            g_ngx_hooked = false;
            hook_ngx(core);
        }
    }
    busy.clear();
}

void graphics_present() {
    // The pacer, the counts and the timer are the presenting thread's. A game presents from one thread; should a second
    // present at the same moment, it passes unpaced instead of racing for them.
    static std::atomic_flag busy;
    if (busy.test_and_set(std::memory_order_acquire)) return;
    struct Leave { ~Leave() { busy.clear(std::memory_order_release); } } leave;
    const uint32_t fps = g_fps_cap.load(std::memory_order_relaxed);
    const bool readout = g_readout.load(std::memory_order_relaxed);
    const bool cap = fps && !(g_aside & gfx::kAsideCap);
    if (!cap && !readout) {
        if (g_counting || g_pacer.on()) {
            g_counting = false;
            g_pacer.set(0, g_freq);
            g_cap_active.store(false, std::memory_order_relaxed);
            g_rendered_fps.store(0, std::memory_order_relaxed);
            g_output_fps.store(0, std::memory_order_relaxed);
            g_frame_ms.store(0, std::memory_order_relaxed);
        }
        // off from the next present on (one relaxed load in vk.cpp); a setting turned on meanwhile on another thread
        // (graphics_set) turns it on again: both sides store, then read the other's (sequentially consistent)
        g_graphics_present.store(false);
        if (present_wanted()) g_graphics_present.store(true);
        return;
    }
    if (!g_freq) {
        LARGE_INTEGER f;
        QueryPerformanceFrequency(&f);
        g_freq = f.QuadPart > 0 ? f.QuadPart : 10'000'000;
    }
    int64_t now = qpc();
    g_pacer.set(cap ? fps : 0, g_freq);
    g_cap_active.store(cap, std::memory_order_relaxed);
    if (cap) {
        const int64_t go = g_pacer.plan(now);
        if (go > now) {
            wait_until(go);
            now = qpc();
        }
    }
    // a gap of over a second (loading, a minimised window) starts the counts again instead of counting one long frame
    const uint64_t t = now_ms();
    if (!g_counting || t - g_last_count_ms > 1000) g_stats.reset();
    g_counting = true;
    g_last_count_ms = t;
    g_stats.frame(now, g_freq, true);
    g_rendered_fps.store(g_stats.rendered_fps(), std::memory_order_relaxed);
    g_output_fps.store(g_stats.output_fps(), std::memory_order_relaxed);
    g_frame_ms.store(g_stats.frame_ms(), std::memory_order_relaxed);
}

void graphics_overlay() {
    if (!g_readout.load(std::memory_order_relaxed)) return;
    KilnUiGraphicsStatus st{};
    st.size = sizeof st;
    graphics_status(&st);
    const ImVec2 ds = ImGui::GetIO().DisplaySize;
    const float pad = 10.0f * style_scale();
    ImGui::SetNextWindowPos(ImVec2(ds.x - pad, pad), ImGuiCond_Always, ImVec2(1.0f, 0.0f));
    ImGui::SetNextWindowBgAlpha(0.6f);
    constexpr ImGuiWindowFlags flags = ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoInputs | ImGuiWindowFlags_NoNav |
                                       ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags_NoSavedSettings |
                                       ImGuiWindowFlags_NoFocusOnAppearing | ImGuiWindowFlags_NoMove;
    if (ImGui::Begin("##kiln.graphics.readout", nullptr, flags)) {
        ImGui::TextUnformatted(text::fill(text::get("native.graphics.fps"), {{"rendered", fmt_fps(st.rendered_fps)},
            {"output", fmt_fps(st.output_fps)}, {"ms", fmt_ms(st.frame_ms)}}).c_str());
        const std::string output = fmt_size(st.output_width, st.output_height);
        if (st.flags & KILN_UI_GFX_DLSS_ACTIVE) {
            const std::string preset = st.dlss_preset ? gfx::preset_name(st.dlss_preset) : text::get("native.graphics.preset_default");
            ImGui::TextUnformatted(text::fill(text::get("native.graphics.dlss"), {{"mode", gfx::mode_name(st.dlss_quality)},
                {"render", fmt_size(st.render_width, st.render_height)}, {"output", fmt_size(st.dlss_output_width, st.dlss_output_height)},
                {"preset", preset}}).c_str());
        } else if (st.aside_dlss) {
            ImGui::TextUnformatted(text::fill(text::get("native.graphics.aside"), {{"tools", gfx::aside_names(st.aside_dlss)}, {"output", output}}).c_str());
        } else if (st.flags & KILN_UI_GFX_DLSS_RESTART) {
            // the readout was turned on in this session: DLSS is read from the next start
            ImGui::TextUnformatted(text::fill(text::get("native.graphics.unknown"), {{"output", output}}).c_str());
        } else {
            // DLSS hooked and not in use now, or the game never set DLSS up (another GPU, or another upscaler chosen)
            ImGui::TextUnformatted(text::fill(text::get("native.graphics.not_dlss"), {{"output", output}}).c_str());
        }
        if (st.fps_cap) {
            if (st.aside_cap)
                ImGui::TextUnformatted(text::fill(text::get("native.graphics.cap_aside"), {{"fps", std::to_string(st.fps_cap)},
                    {"tools", gfx::aside_names(st.aside_cap)}}).c_str());
            else
                ImGui::TextUnformatted(text::fill(text::get("native.graphics.cap"), {{"fps", std::to_string(st.fps_cap)}}).c_str());
        }
    }
    ImGui::End();
}

KilnResult graphics_set(const KilnUiGraphics* in) {
    if (!in || in->size < sizeof(KilnUiGraphics)) return KILN_ERR_INVALID_ARGUMENT;
    gfx::Settings s;
    s.fps_cap = in->fps_cap;
    s.dlss_preset = in->dlss_preset;
    s.dlss_scale = static_cast<gfx::DlssScale>(in->dlss_scale);
    s.custom_scale = in->custom_scale;
    for (size_t i = 0; i < 4; ++i) s.mode_scale[i] = in->mode_scale[i];
    s.readout = in->readout != 0;
    s = gfx::clamped(s);
    bool say = false;
    {
        std::lock_guard lk(g_mu);
        static uint64_t last_said = 0;
        const uint64_t t = now_ms();
        if (s != g_settings && (t - last_said >= 2000 || s.dlss_preset != g_settings.dlss_preset || s.dlss_scale != g_settings.dlss_scale ||
                                s.readout != g_settings.readout)) {
            say = true;
            last_said = t;
        }
        g_settings = s;
    }
    publish(s);
    if (say) logf(KILN_LOG_INFO, "graphics: settings now: %s", describe(s).c_str());
    return KILN_OK;
}

KilnResult graphics_status(KilnUiGraphicsStatus* out) {
    if (!out || out->size < sizeof(KilnUiGraphicsStatus)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof(KilnUiGraphicsStatus));
    out->size = size;
    gfx::Settings s;
    uint32_t aside = 0;
    bool wanted = false, started = false;
    {
        std::lock_guard lk(g_mu);
        s = g_settings;
        aside = g_aside | g_aside_run.load();
        wanted = g_ngx_wanted;
        started = g_started;
    }
    const bool hooked = g_ngx_hooked.load();
    uint32_t flags = 0;
    if (g_cap_active.load(std::memory_order_relaxed)) flags |= KILN_UI_GFX_CAP_ACTIVE;
    if (g_counting) flags |= KILN_UI_GFX_COUNTING;
    if (hooked) flags |= KILN_UI_GFX_DLSS_HOOKED;
    if (started && s.wants_ngx() && !wanted && !(aside & gfx::kAsideDlss)) flags |= KILN_UI_GFX_DLSS_RESTART;
    out->aside_cap = aside & gfx::kAsideCap;
    out->aside_dlss = aside & gfx::kAsideDlss;
    out->fps_cap = s.fps_cap;
    out->rendered_fps = g_rendered_fps.load(std::memory_order_relaxed);
    out->output_fps = g_output_fps.load(std::memory_order_relaxed);
    out->frame_ms = g_frame_ms.load(std::memory_order_relaxed);
    vk_extent(&out->output_width, &out->output_height);
    out->dlss_quality = -1;
    if (hooked && g_dlss_handle.load() && now_ms() - g_last_eval_ms.load(std::memory_order_relaxed) < 2000) {
        flags |= KILN_UI_GFX_DLSS_ACTIVE | g_dlss_flags.load();
        out->dlss_quality = g_dlss_quality.load();
        out->dlss_preset = g_dlss_preset.load();
        out->render_width = g_render_w.load(std::memory_order_relaxed);
        out->render_height = g_render_h.load(std::memory_order_relaxed);
        out->dlss_output_width = g_dlss_out_w.load();
        out->dlss_output_height = g_dlss_out_h.load();
    }
    out->flags = flags;
    return KILN_OK;
}

}  // namespace kui
