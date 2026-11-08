// SPDX-License-Identifier: MIT
// kiln-set.dll: runs Shrouded Engine Tweaks (SET 0.1.1, MIT) Lua mods unchanged on Kiln, on dedicated servers and in
// the game. Mods load from <game>/shrouded_engine_tweaks/mods/<name>/init.lua, as with SET. The Lua runtime and API
// are SET's (lua_api.cpp, adapted); the game side is Kiln's services (game.cpp, admin.inl, hooks.cpp, lua_imgui.cpp).
// Kiln drives it: the server world's Pre tick runs Tick callbacks, its Post runs Observe handlers, the UI tick runs the
// mods in the game's menu, and the overlay frame runs Draw callbacks.
// When SET's own powrprof.dll runs in the process, kiln-set steps aside: the mods are SET's to run.
// Console: each <game>/kiln/control/set/*.lua file's lines run as Lua at the next tick, then the file is deleted (the
// overlay console's job in SET; kiln/control/*.cmd belongs to Kiln.Host).
#include <windows.h>
#include <tlhelp32.h>

#include <algorithm>
#include <atomic>
#include <vector>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <mutex>
#include <sstream>
#include <string>

#include "game.h"
#include "coexist.h"
#include "hooks.h"
#include "kiln_api.h"
#include "level.h"
#include "lua_api.h"
#include "services/kiln_profile.h"
#include "services/kiln_ui.h"

namespace kset {
void bindHooks(const KilnApi* api, KilnOwner owner);
void bindUi(const KilnUiApi* ui);
}

namespace {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
std::filesystem::path g_game, g_control;
std::string g_aside;
std::atomic<bool> g_running{false};
bool g_initialized = false;
// Taken only when a re-check is due (a DLL has loaded), never on the steady-state frame/tick path.
std::mutex g_coexist_mx;
kset::ModuleWatch g_modules;
// The console folder is checked at most every console_ms, by one thread at a time (the UI and the tick thread both
// drive it in a hosted game).
constexpr uint64_t console_ms = 500;
std::atomic<uint64_t> g_console_next{0};
std::atomic<bool> g_console_busy{false};

std::filesystem::path api_path(int32_t kind) {
    char buf[1024];
    size_t n = 0;
    if (g_api->path(kind, buf, sizeof buf, &n) != KILN_OK) return {};
    return std::filesystem::u8path(std::string(buf, n));
}

std::string u8(const std::filesystem::path& p) {
    const auto s = p.u8string();
    return std::string(s.begin(), s.end());
}

void log(int level, const std::string& s) { g_api->log(g_owner, level, KilnStr{s.data(), s.size()}); }

// SET's sink: an optional level marker byte, then the line. Kiln's log keeps the level.
void sink(const char* line) {
    const lv::Level l = lv::of(line);
    const char* text = l == lv::none ? line : line + 1;
    log(l == lv::bad ? KILN_LOG_ERROR : l == lv::warn ? KILN_LOG_WARN : KILN_LOG_INFO, std::string("[set] ") + text);
}

// SET's own DLL in the process (the game imports POWRPROF.dll; SET ships as one in the game folder).
std::string set_in_process() { return kset::runtime_conflict(); }

// Runs on every server step and every client UI frame. The full module check (on_ready's) runs again only once a DLL
// has loaded since (see kset::ModuleWatch): the steady state is two atomic loads, no lock, no wineserver call.
bool may_run() {
    if (!g_running.load(std::memory_order_acquire)) return false;
    if (!g_modules.due()) return true;
    std::lock_guard lock(g_coexist_mx);
    if (!g_running.load()) return false;
    g_aside = set_in_process();
    if (g_aside.empty()) return true;
    g_running = false;
    log(KILN_LOG_WARN, "kiln-set steps aside: " + g_aside);
    rt::gameClosing();
    return false;
}

void poll_console() {
    std::error_code ec;
    const auto dir = g_control / "set";
    if (!std::filesystem::is_directory(dir, ec)) return;
    std::vector<std::filesystem::path> files;
    for (const auto& e : std::filesystem::directory_iterator(dir, ec))
        if (e.path().extension() == ".lua") files.push_back(e.path());
    std::sort(files.begin(), files.end());
    for (const auto& cmd : files) {
        std::ifstream f(cmd);
        std::stringstream ss;
        ss << f.rdbuf();
        f.close();
        std::filesystem::remove(cmd, ec);
        std::string line;
        while (std::getline(ss, line)) {
            while (!line.empty() && (line.back() == '\r' || line.back() == ' ')) line.pop_back();
            if (line.empty() || line.rfind("--", 0) == 0) continue;
            log(KILN_LOG_INFO, "[set] > " + line);
            rt::queue(line);
        }
    }
}

void maybe_poll_console() {
    const uint64_t now = GetTickCount64();
    uint64_t next = g_console_next.load(std::memory_order_relaxed);
    if (now < next || !g_console_next.compare_exchange_strong(next, now + console_ms, std::memory_order_acq_rel)) return;
    if (g_console_busy.exchange(true, std::memory_order_acquire)) return;
    poll_console();
    g_console_busy.store(false, std::memory_order_release);
}

void KILN_CALL on_pre(const KilnTickInfo*, void*) {
    if (!may_run()) return;
    maybe_poll_console();
    const uintptr_t w = g_api->server_world();
    if (w) rt::tick(w);
}

void KILN_CALL on_post(const KilnTickInfo*, void*) {
    if (!g_running.load()) return;
    const uintptr_t w = g_api->server_world();
    if (w) rt::afterTick(w);
}

void KILN_CALL on_world(const KilnWorldEvent* e, void*) {
    if (!g_running.load()) return;
    if (e && e->kind == KILN_WORLD_SERVER && e->event == KILN_WORLD_STOPPING) rt::worldLeft();
}

// Client only: the game's UI frame on the main thread (mods run here in the menu), and the overlay frame (Draw).
void KILN_CALL on_ui_tick(uint64_t, void*) {
    if (!may_run()) return;
    maybe_poll_console();
    rt::frame();
}

void KILN_CALL on_frame(const KilnUiFrame* f, void*) {
    if (!g_running.load()) return;
    rt::setOverlayOpen(f && f->menu_open);
    if (!rt::hasDraw()) return;
    if (rt::tryLockDraw()) rt::drawLocked();
}

void KILN_CALL on_ready(void*) {
    g_game = api_path(KILN_PATH_GAME);
    g_control = api_path(KILN_PATH_CONTROL);
    // Armed before the startup check, so a DLL loading in between is still re-checked.
    const bool notified = g_modules.arm();
    g_aside = set_in_process();
    if (!g_aside.empty()) {
        g_modules.disarm();
        log(KILN_LOG_WARN, "kiln-set steps aside: " + g_aside + "; its Lua mods are SET's to run");
        return;
    }
    // play-once without mods and an invalid profile load no mod of any engine (the launcher places a profile's SET
    // mods in shrouded_engine_tweaks/mods, so an active profile needs nothing else here)
    if (const auto* prof = static_cast<const KilnProfileApi*>(
            g_api->service(KilnStr{KILN_PROFILE_SERVICE, sizeof(KILN_PROFILE_SERVICE) - 1}, 1))) {
        const uint32_t mode = prof->mode();
        if (mode == KILN_PROFILE_NO_MODS || mode == KILN_PROFILE_INVALID) {
            g_modules.disarm();
            log(KILN_LOG_INFO, std::string("kiln-set: no SET mods this start (") +
                                   (mode == KILN_PROFILE_NO_MODS ? "play-once without mods" : "the profile is invalid") + ")");
            return;
        }
    }
    const std::string missing = kset::bind(g_api);
    if (!missing.empty()) log(KILN_LOG_WARN, "kiln-set: unavailable here: " + missing + " (the matching Lua functions raise an error)");
    kset::bindHooks(g_api, g_owner);
    rt::setSystemsAvailable(kset::systemsReady());
    rt::bindKiln(g_api, g_owner);
    hk::setLog(&sink);
    const auto mods = g_game / "shrouded_engine_tweaks" / "mods";
    rt::configure(u8(mods), &sink);
    g_initialized = true;
    {  // GetGameBuild(): the exe's link time stamp, as SET reports it
        const auto base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
        IMAGE_DOS_HEADER dos{};
        IMAGE_NT_HEADERS64 nt{};
        SIZE_T copied = 0;
        if (ReadProcessMemory(GetCurrentProcess(), reinterpret_cast<const void*>(base), &dos, sizeof dos, &copied) &&
            copied == sizeof dos && dos.e_magic == IMAGE_DOS_SIGNATURE && dos.e_lfanew >= sizeof dos &&
            dos.e_lfanew <= 0x100000 && base <= UINTPTR_MAX - static_cast<uintptr_t>(dos.e_lfanew) &&
            ReadProcessMemory(GetCurrentProcess(), reinterpret_cast<const void*>(base + dos.e_lfanew), &nt, sizeof nt, &copied) &&
            copied == sizeof nt && nt.Signature == IMAGE_NT_SIGNATURE)
            rt::setGameBuild(nt.FileHeader.TimeDateStamp);
    }
    KilnHandle h = 0;
    const KilnResult a = g_api->subscribe_tick(g_owner, KILN_TICK_PRE, &on_pre, nullptr, &h);
    const KilnResult b = g_api->subscribe_tick(g_owner, KILN_TICK_POST, &on_post, nullptr, &h);
    g_api->subscribe_world(g_owner, &on_world, nullptr, &h);
    const bool client = g_api->side() == KILN_SIDE_CLIENT;
    bool ui = false;
    if (client) {
        const auto* U = static_cast<const KilnUiApi*>(g_api->service(KilnStr{KILN_UI_SERVICE, sizeof(KILN_UI_SERVICE) - 1}, 1));
        const auto* UT = static_cast<const KilnUiTickApi*>(g_api->service(KilnStr{KILN_UI_TICK_SERVICE, sizeof(KILN_UI_TICK_SERVICE) - 1}, 1));
        if (U) {
            kset::bindUi(U);
            ui = U->subscribe_frame(g_owner, &on_frame, nullptr, &h) == KILN_OK;
        }
        if (UT) UT->subscribe(g_owner, &on_ui_tick, nullptr, &h);
    }
    std::error_code ec;
    const bool has_dir = std::filesystem::is_directory(mods, ec);
    g_running = a == KILN_OK && b == KILN_OK;
    log(g_running ? KILN_LOG_INFO : KILN_LOG_WARN,
        std::string("kiln-set 0.1.1 (SET Lua API) on the ") + (client ? "client" : "server") + ": mods in " + u8(mods) +
            (has_dir ? "" : " (folder missing: no SET mods)") + ", ticks " + (g_running ? "on" : "off") + ", overlay " +
            (ui ? "on" : "off") + ", console " + u8(g_control / "set" / "*.lua") +
            (notified ? ", SET re-check on DLL load" : ", SET re-check every 3 s (no DLL-load notification)"));
}

void KILN_CALL on_unload(void*) {
    g_running = false;
    g_modules.disarm();
    if (g_initialized) rt::gameClosing();
}
}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (!api || !info || api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{"kiln-set", 8};
    info->version = KilnStr{"0.1.1", 5};
    info->on_ready = &on_ready;
    info->on_unload = &on_unload;
    return KILN_OK;
}
