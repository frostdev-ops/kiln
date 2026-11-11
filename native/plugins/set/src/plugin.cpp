// SPDX-License-Identifier: MIT
// kiln-set.dll: runs Shrouded Engine Tweaks (SET 0.2.0, MIT) Lua mods unchanged on Kiln, on dedicated servers and in
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
#include <chrono>
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
#include "contracts.h"
#include "hooks.h"
#include "kiln_api.h"
#include "level.h"
#include "lua_api.h"
#include "overlay.h"
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
kset::ConflictGate g_gate;  // a failed module inventory is retried; only a seen SET or 5 failures in a row step aside
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

// ---- state/set.json (docs/kiln/launcher/contracts.md 5.9): what kiln-set did this start, as its sibling plugins write
// theirs. Written at on_ready and after every mod load (start, reload()), never per frame. ----
std::string json_str(const std::string& v) {
    std::string o = "\"";
    for (const unsigned char c : v) {
        switch (c) {
            case '"': o += "\\\""; break;
            case '\\': o += "\\\\"; break;
            case '\n': o += "\\n"; break;
            case '\r': o += "\\r"; break;
            case '\t': o += "\\t"; break;
            default:
                if (c < 0x20) {
                    char b[8];
                    std::snprintf(b, sizeof b, "\\u%04x", c);
                    o += b;
                } else {
                    o += static_cast<char>(c);
                }
        }
    }
    return o + "\"";
}

std::filesystem::path g_state;
std::string g_state_status = "idle", g_state_reason;
std::mutex g_state_mx;

// The folders with an init.lua, before the mods load (they show as pending until then).
std::vector<rt::ModReport> pending_mods(const std::filesystem::path& dir) {
    std::vector<rt::ModReport> out;
    std::error_code ec;
    if (!std::filesystem::is_directory(dir, ec)) return out;
    for (const auto& e : std::filesystem::directory_iterator(dir, ec)) {
        const std::string name = u8(e.path().filename());
        if (!e.is_directory(ec) || name.empty() || name[0] == '.' || !std::filesystem::exists(e.path() / "init.lua", ec)) continue;
        out.push_back({name, "pending", "", "", "", u8(e.path())});
        if (out.size() >= 1024) break;
    }
    std::sort(out.begin(), out.end(), [](const auto& a, const auto& b) { return a.name < b.name; });
    return out;
}

void write_state(const std::string& status, const std::string& reason, const std::vector<rt::ModReport>& mods) {
    std::lock_guard lock(g_state_mx);
    g_state_status = status;
    g_state_reason = reason;
    if (g_state.empty()) return;
    const auto unix_ms = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
    std::string j = "{\n  \"format\": 1,\n  \"plugin\": \"kiln-set\",\n  \"version\": \"0.2.0\",\n";
    j += "  \"pid\": " + std::to_string(GetCurrentProcessId()) + ",\n";
    j += std::string("  \"side\": \"") + (g_api->side() == KILN_SIDE_SERVER ? "server" : "client") + "\",\n";
    j += "  \"status\": " + json_str(status) + ",\n";
    j += "  \"reason\": " + json_str(reason) + ",\n";
    j += std::string("  \"steppedAside\": ") + (status == "stepped-aside" ? "true" : "false") + ",\n";
    j += "  \"steppedAsideReason\": " + json_str(status == "stepped-aside" ? reason : std::string()) + ",\n";
    j += "  \"modsDir\": " + json_str(u8(g_game / "shrouded_engine_tweaks" / "mods")) + ",\n";
    j += "  \"updatedUnixMs\": " + std::to_string(unix_ms) + ",\n  \"mods\": [";
    for (size_t i = 0; i < mods.size(); ++i) {
        const auto& m = mods[i];
        j += i ? ",\n    {" : "\n    {";
        j += "\"id\": " + json_str(m.name) + ", \"name\": " + json_str(m.title.empty() ? m.name : m.title) +
             ", \"version\": " + json_str(m.version) + ", \"status\": " + json_str(m.status) + ", \"reason\": " + json_str(m.reason) +
             ", \"folder\": " + json_str(m.folder) + "}";
    }
    j += mods.empty() ? "]\n}\n" : "\n  ]\n}\n";
    std::error_code ec;
    std::filesystem::create_directories(g_state, ec);
    const auto file = g_state / "set.json";
    auto tmp = file;
    tmp += ".tmp";
    {
        std::ofstream f(tmp, std::ios::binary | std::ios::trunc);
        if (!f) return;
        f << j;
        if (!f) return;
    }
    std::filesystem::rename(tmp, file, ec);  // replaces the old file in one step
    if (ec) log(KILN_LOG_WARN, "kiln-set: cannot write " + u8(file) + ": " + ec.message());
}

// rt::onModsReported: after every mod load.
void on_mods_reported() {
    const auto mods = rt::modReports();
    const bool any_failed = std::any_of(mods.begin(), mods.end(), [](const auto& m) { return m.status == "failed"; });
    const size_t loaded = static_cast<size_t>(std::count_if(mods.begin(), mods.end(), [](const auto& m) { return m.status == "loaded"; }));
    write_state("running", any_failed ? std::to_string(loaded) + " of " + std::to_string(mods.size()) + " mods loaded; see the failed ones" : "", mods);
}

// SET's sink: an optional level marker byte, then the line. Kiln's log keeps the level.
// SET's debugLog diagnostics go to the file, not the private Lua console.
void ui_sink(const char* line) { log(KILN_LOG_INFO, std::string("[set] ") + line); }
void sink(const char* line) {
    const lv::Level l = lv::of(line);
    // LV_LOG lines (SET: set.log only) are plain lines in kiln.log.
    const char* text = l == lv::none && *line != LV_LOG[0] ? line : line + 1;
    if (*line != LV_LOG[0]) setui::line(text);
    log(l == lv::bad ? KILN_LOG_ERROR : l == lv::warn ? KILN_LOG_WARN : KILN_LOG_INFO, std::string("[set] ") + text);
}

// SET's own DLL in the process (the game imports POWRPROF.dll; SET ships as one in the game folder).
std::string set_in_process() { return kset::runtime_conflict(); }

// Runs on every server step and every client UI frame. The full module check (on_ready's) runs again only after a DLL
// load notification or a change of the loaded-module list (a 3 s PEB-list fingerprint, see kset::ModuleWatch): no
// steady-state lock or wineserver call, and no periodic full check.
bool may_run() {
    if (!g_running.load(std::memory_order_acquire)) return false;
    if (!g_modules.due() && !g_gate.retry_due()) return true;
    std::lock_guard lock(g_coexist_mx);
    if (!g_running.load()) return false;
    g_aside = g_gate.check();
    if (g_aside.empty() && g_gate.failures() == 1)
        log(KILN_LOG_WARN, "kiln-set: the loaded modules could not be inspected once; checking again (stepping aside only after " +
                               std::to_string(kset::ConflictGate::failures_to_step_aside) + " failed checks in a row)");
    if (g_aside.empty()) return true;
    g_running = false;
    setui::stop();
    log(KILN_LOG_WARN, "kiln-set steps aside: " + g_aside);
    rt::gameClosing(true);  // the game runs on: what the mods changed in its resources goes back
    write_state("stepped-aside", g_aside, {});
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
    if (now < next) return;
    if (g_console_busy.exchange(true, std::memory_order_acquire)) return;
    struct Release { ~Release() { g_console_busy.store(false, std::memory_order_release); } } release;
    // Re-read under the busy guard: another caller may have completed a poll while we were acquiring it.
    if (now < g_console_next.load(std::memory_order_relaxed)) return;
    g_console_next.store(now + console_ms, std::memory_order_relaxed);
    poll_console();
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
    setui::draw();
    rt::setOverlayOpen((f && f->menu_open) || setui::isOpen());
    if (!rt::hasDraw()) return;
    if (rt::tryLockDraw()) rt::drawLocked();
}

void KILN_CALL on_ready(void*) {
    g_game = api_path(KILN_PATH_GAME);
    g_control = api_path(KILN_PATH_CONTROL);
    g_state = api_path(KILN_PATH_STATE);
    // Armed before the startup check, so a DLL loading in between is still re-checked.
    const bool notified = g_modules.arm();
    g_gate.reset();
    g_aside = g_gate.startup();
    if (!g_aside.empty()) {
        g_modules.disarm();
        log(KILN_LOG_WARN, "kiln-set steps aside: " + g_aside + "; its Lua mods are SET's to run");
        write_state("stepped-aside", g_aside, {});
        return;
    }
    // play-once without mods and an invalid profile load no mod of any engine (the launcher places a profile's SET
    // mods in shrouded_engine_tweaks/mods, so an active profile needs nothing else here)
    if (const auto* prof = static_cast<const KilnProfileApi*>(
            g_api->service(KilnStr{KILN_PROFILE_SERVICE, sizeof(KILN_PROFILE_SERVICE) - 1}, 1))) {
        const uint32_t mode = prof->mode();
        if (mode == KILN_PROFILE_NO_MODS || mode == KILN_PROFILE_INVALID) {
            g_modules.disarm();
            const std::string why = mode == KILN_PROFILE_NO_MODS ? "play-once without mods" : "the profile is invalid";
            log(KILN_LOG_INFO, "kiln-set: no SET mods this start (" + why + ")");
            write_state("off", "no SET mods this start: " + why, {});
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
    rt::onModsReported(&on_mods_reported);
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
    {  // SET's rule catalog against this exe: an entry that fails is off (Features() says why), the rest keeps working
        const auto base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
        auto all = contracts::check(base, rt::gameBuild(), contracts::catalog());
        size_t ok = 0;
        for (const auto& c : all) {
            ok += c.fn != 0;
            if (c.fn) log(KILN_LOG_INFO, "[set] Catalog: " + c.name + " checked out");
            else log(KILN_LOG_INFO, "[set] Catalog: " + c.name + " off (" + c.why + ")");
        }
        log(KILN_LOG_INFO, "[set] Catalog: " + std::to_string(ok) + " of " + std::to_string(all.size()) + " targets checked out");
        contracts::setChecked(std::move(all));
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
            if (ui) {
                setui::configure(api_path(KILN_PATH_LOGS), g_control, &ui_sink);
                setui::bind(U, g_owner);
            }
        }
        if (UT) UT->subscribe(g_owner, &on_ui_tick, nullptr, &h);
    }
    rt::setFeature("overlay", ui, !client ? "Dedicated server: No overlay" : "kiln.ui is not running");
    std::error_code ec;
    const bool has_dir = std::filesystem::is_directory(mods, ec);
    g_running = a == KILN_OK && b == KILN_OK;
    if (!g_running) write_state("failed", "kiln-set could not subscribe to the game's ticks", {});
    else if (!has_dir) write_state("idle", "no shrouded_engine_tweaks/mods folder: no SET mods", {});
    else write_state("waiting", "", pending_mods(mods));
    log(g_running ? KILN_LOG_INFO : KILN_LOG_WARN,
        std::string("kiln-set 0.2.0 (SET Lua API) on the ") + (client ? "client" : "server") + ": mods in " + u8(mods) +
            (has_dir ? "" : " (folder missing: no SET mods)") + ", ticks " + (g_running ? "on" : "off") + ", overlay " +
            (ui ? "on" : "off") + ", console " + u8(g_control / "set" / "*.lua") +
            (g_modules.fingerprinting()
                 ? (notified ? ", SET re-check on DLL load or module-list change" : ", SET re-check on module-list change (3 s)")
                 : (notified ? ", SET re-check on DLL load and every 3 s" : ", SET re-check every 3 s (no DLL-load notification)")));
}

void KILN_CALL on_unload(void*) {
    g_running = false;
    g_modules.disarm();
    setui::stop();
    if (g_initialized) rt::gameClosing();
}
}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (!api || !info || api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{"kiln-set", 8};
    info->version = KilnStr{"0.2.0", 5};
    info->on_ready = &on_ready;
    info->on_unload = &on_unload;
    if (api->register_service) api->register_service(owner, KilnStr{"kiln.set.ui", 11}, setui::service());
    return KILN_OK;
}
