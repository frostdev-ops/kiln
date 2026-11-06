// SPDX-License-Identifier: MIT
// kiln.screen: the service. The effect list is core.cpp, the Vulkan is pass.cpp; this file ties them to kiln.ui's present (kiln_ui.h) and to
// the C table (kiln_screen.h).
//
// Nothing playing means no demand on kiln.ui, which then doesn't call here at all: its present costs one relaxed load. A few seconds after the
// last effect the copy of the frame is freed as well.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cctype>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>

#include "core.h"
#include "kiln_api.h"
#include "pass.h"
#include "services/kiln_screen.h"
#include "services/kiln_ui.h"

namespace {

constexpr uint32_t kTransferSrcUsage = 0x1;  // VK_IMAGE_USAGE_TRANSFER_SRC_BIT
// Kiln.Vfx's mirror (ScreenNative.cs) checks the same sizes.
static_assert(sizeof(KilnScreenEffect) == 100 && sizeof(KilnScreenStatus) == 192 && sizeof(KilnScreenApi) == 56);
constexpr double kLingerSeconds = 3.0;       // so that a run of flashes doesn't rebuild the copy each time

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
const KilnUiApi* g_ui = nullptr;
KilnHandle g_subscription = 0;
kscreen::Mixer g_mixer;
std::mutex g_demand_mu;  // play against "nothing is playing, so no more calls": one or the other wins
bool g_enabled = false;
const char* g_reason = "kiln.ui is not ready yet";  // why it is off before the pass says anything
std::atomic<uint64_t> g_plays{0}, g_refused{0};
std::atomic<bool> g_warned_full{false};
double g_last_active = 0;

void logf(int level, const char* fmt, ...) {
    char buf[640];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n > 0) g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(std::min<int>(n, sizeof buf - 1))});
}

void log_text(int level, const char* text) { logf(level, "%s", text); }

kscreen::Pass& pass() {
    static kscreen::Pass p(&log_text);
    return p;
}

double now_seconds() {
    static const double freq = [] {
        LARGE_INTEGER f{};
        QueryPerformanceFrequency(&f);
        return static_cast<double>(f.QuadPart);
    }();
    LARGE_INTEGER c{};
    QueryPerformanceCounter(&c);
    return static_cast<double>(c.QuadPart) / freq;
}

void set_demand(uint32_t n) {
    if (g_ui) g_ui->set_pre_overlay_demand(n);
}

// Render thread: the game's frame, inside its present. False leaves the present exactly as the game asked for it.
bool KILN_CALL on_present(const KilnUiPreOverlay* p, void** out, void*) {
    const double now = now_seconds();
    if (pass().off()) {
        std::lock_guard<std::mutex> lk(g_demand_mu);
        g_mixer.clear();
        set_demand(0);
        return false;
    }
    if (g_mixer.count() == 0) {
        if (now - g_last_active > kLingerSeconds && pass().release_when_idle()) {
            std::lock_guard<std::mutex> lk(g_demand_mu);
            if (g_mixer.count() == 0) set_demand(0);
        }
        return false;
    }
    g_last_active = now;
    if (!pass().prepare(*p)) return false;
    kscreen::GpuFrame frame;
    if (g_mixer.compose(now, frame) == 0) return false;
    return pass().present(*p, frame, out);
}

void KILN_CALL on_gone(void* device, void* swapchain, void*) { pass().gone(device, swapchain); }

KilnResult KILN_CALL t_status(KilnScreenStatus* out) {
    if (!out || out->size < sizeof(KilnScreenStatus)) return KILN_ERR_INVALID_ARGUMENT;
    const kscreen::PassStatus s = pass().status();
    out->enabled = g_enabled && !s.off;
    out->ready = s.ready;
    out->transfer = static_cast<uint8_t>(s.transfer);
    out->playing = g_mixer.count();
    out->format = s.format;
    out->width = s.width;
    out->height = s.height;
    out->plays = g_plays.load();
    out->refused = g_refused.load();
    out->frames = s.frames;
    out->skipped = s.skipped;
    out->pipeline_ms = s.pipeline_ms;
    std::snprintf(out->reason, sizeof out->reason, "%s", s.off ? s.reason.c_str() : g_enabled ? "" : g_reason);
    return KILN_OK;
}

uint32_t KILN_CALL t_play(KilnOwner owner, const KilnScreenEffect* effect) {
    if (!effect || effect->size < sizeof(KilnScreenEffect) || !g_enabled || !g_ui || pass().off()) {
        g_refused.fetch_add(1);
        return 0;
    }
    uint32_t h;
    {
        std::lock_guard<std::mutex> lk(g_demand_mu);
        h = g_mixer.play(owner, *effect);
        if (h) set_demand(1);
    }
    if (!h) {
        g_refused.fetch_add(1);
        if (!g_warned_full.exchange(true)) logf(KILN_LOG_WARN, "screen: %u effects are playing, the most it takes: further plays are refused", KILN_SCREEN_MAX_EFFECTS);
        return 0;
    }
    g_plays.fetch_add(1);
    logf(KILN_LOG_INFO, "screen: #%u plays: fade in %.2f s, %s, fade out %.2f s", h, effect->fade_in,
         effect->seconds < 0 ? "until stopped" : "for a set time", effect->fade_out);
    return h;
}

KilnResult KILN_CALL t_stop(uint32_t handle, int32_t immediate) { return g_mixer.stop(handle, immediate != 0) ? KILN_OK : KILN_ERR_NOT_FOUND; }
KilnResult KILN_CALL t_set_strength(uint32_t handle, float strength) { return g_mixer.set_strength(handle, strength) ? KILN_OK : KILN_ERR_NOT_FOUND; }
int32_t KILN_CALL t_is_playing(uint32_t handle) { return g_mixer.playing(handle) ? 1 : 0; }
uint32_t KILN_CALL t_stop_owner(KilnOwner owner, int32_t immediate) { return g_mixer.stop_owner(owner, immediate != 0); }

const KilnScreenApi kTable = {{sizeof(KilnScreenApi), KILN_SCREEN_VERSION}, &t_status, &t_play, &t_stop, &t_set_strength, &t_is_playing, &t_stop_owner};

std::string config_dir() {
    char b[1024];
    size_t n = 0;
    if (g_api->path(KILN_PATH_CONFIG, b, sizeof b, &n) != KILN_OK) return {};
    return std::string(b, n);
}

// {"enabled": false} and nothing else is read. A file that can't be read leaves it on: the switch has to be said to count.
bool switched_off() {
    const std::string dir = config_dir();
    if (dir.empty()) return false;
    try {
        const std::u8string u8(dir.begin(), dir.end());
        std::ifstream f(std::filesystem::path(u8) / "screen.json");
        if (!f) return false;
        std::string text((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
        text.erase(std::remove_if(text.begin(), text.end(), [](unsigned char c) { return std::isspace(c) != 0; }), text.end());
        return text.find("\"enabled\":false") != std::string::npos;
    } catch (const std::exception&) {
        return false;
    }
}

void on_ready(void*) {
    KilnStr name{KILN_UI_SERVICE, sizeof(KILN_UI_SERVICE) - 1};
    const auto* ui = static_cast<const KilnUiApi*>(g_api->service(name, KILN_UI_VERSION));
    if (!ui || ui->header.size < offsetof(KilnUiApi, add_swapchain_usage) + sizeof(void*)) {
        g_reason = "no kiln.ui with a pre-overlay callback here";
        logf(KILN_LOG_WARN, "screen: %s: screen effects are off", g_reason);
        return;
    }
    if (ui->subscribe_pre_overlay(g_owner, &on_present, &on_gone, nullptr, &g_subscription) != KILN_OK) {
        g_reason = "kiln.ui refused the pre-overlay callback";
        logf(KILN_LOG_WARN, "screen: %s: screen effects are off", g_reason);
        return;
    }
    ui->add_swapchain_usage(kTransferSrcUsage);
    g_ui = ui;
    g_enabled = true;
    logf(KILN_LOG_INFO, "screen: ready (it draws over the game's UI at this level); kiln/config/screen.json {\"enabled\": false} turns it off");
}

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < offsetof(KilnApi, side) + sizeof(void*)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{KILN_SCREEN_SERVICE, sizeof(KILN_SCREEN_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    if (api->side() != KILN_SIDE_CLIENT) return KILN_ERR_UNAVAILABLE;  // no picture on a dedicated server
    if (switched_off()) {
        logf(KILN_LOG_INFO, "screen: off by kiln/config/screen.json: the swapchain stays as the game asks for it");
        return KILN_ERR_UNAVAILABLE;
    }
    info->on_ready = &on_ready;
    return api->register_service(owner, KilnStr{KILN_SCREEN_SERVICE, sizeof(KILN_SCREEN_SERVICE) - 1}, &kTable);
}
