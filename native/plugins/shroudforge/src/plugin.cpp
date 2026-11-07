// SPDX-License-Identifier: MIT
// kiln-shroudforge.dll: a Kiln plugin (<game>/kiln/plugins/) that runs ShroudForge's Lua mods unchanged by driving a
// ShroudForge runtime (github.com/bonsaibauer/shroudforge, MIT) through its exported C functions. Docs:
// docs/kiln/shroudforge-compat.md. This DLL is MIT and holds no ShroudForge or GPL code.
//
// Two runtimes, one adapter:
//   bundled    <game>/kiln/plugins/shroudforge/ : Kiln's build of the frostdev-ops/shroudforge fork (third_party/
//              shroudforge, a separate GPL-3 component because it links kfc-parser). The default.
//   installed  <game>/shroudforge/ : the player's own ShroudForge. Used when kiln.json "shroudforge": {"useInstalled":
//              true} (launcher Settings "Use my installed ShroudForge"; env KILN_SHROUDFORGE_RUNTIME overrides).
// A missing or broken chosen runtime falls back to the other one with a warning. Either way the data folder is
// ShroudForge's own (<game>/shroudforge/: config, logs, state, profiles) and mods come from its mods folder.
//
// ShroudForge normally starts from its own winmm.dll bootstrap (loaded by its dbghelp.dll or dinput8.dll proxy). Kiln
// owns dinput8.dll, so this plugin does what that bootstrap does, with Kiln providing the start, paths, profile, log:
//   on_ready (main thread, the game's entry point is still held by Kiln's gate, like ShroudForge's own startup gate):
//     1. step aside when ShroudForge's bootstrap already runs in this process (its dbghelp.dll loaded it);
//     2. decide: the profile (no-mods/invalid: off; an active profile: on when it lists ShroudForge mods; no profile:
//        on), KILN_SHROUDFORGE=0/1 overrides;
//     3. pick the runtime (above), LoadLibrary its shroudforge-runtime.dll, copy missing bundled game profiles,
//        shroudforge_prepare_startup(<game>) (startup asset mods, DLL Manager), then kfc-runtime.dll:
//        KfcRuntimeInitialize (native game access; a failure leaves metadata-only mode, as in ShroudForge);
//   a worker thread (ShroudForge's own mods loop, same timing): shroudforge_create(<game>), then every 16 ms
//     KfcRuntimeTick + shroudforge_update(handle, dt); the heartbeat file ShroudForge's tools read; ShroudForge's log
//     mirrored into kiln.log ("[sf] ..."); <state>/shroudforge.json for the launcher.
// Fail soft: anything missing or failing is logged and written to shroudforge.json; Kiln and its mods run on.
// Not done here: ShroudForge's startup update check and pending-update install (Kiln owns updates, so ShroudForge's
// updater never replaces Kiln's dinput8.dll) and its Debug Console window (kiln.log carries ShroudForge's log).
#include <algorithm>
#include <atomic>
#include <ctime>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <fstream>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "nlohmann/json.hpp"
#include "services/kiln_profile.h"
#include "sf_internal.h"

#include <psapi.h>

namespace ksf {

namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
bool g_server = true;
std::filesystem::path g_game, g_state;

using PrepareStartup = bool(__cdecl*)(const wchar_t*);
using CreateRuntime = void*(__cdecl*)(const wchar_t*);
using UpdateRuntime = bool(__cdecl*)(void*, double);
using DestroyRuntime = void(__cdecl*)(void*);
using KfcAbi = uint32_t(__cdecl*)();
using KfcBool = bool(__cdecl*)();
using KfcVoid = void(__cdecl*)();
using KfcStatusFn = void(__cdecl*)(char*, size_t);

HMODULE g_runtime = nullptr, g_kfc = nullptr;
CreateRuntime g_create = nullptr;
UpdateRuntime g_update = nullptr;
DestroyRuntime g_destroy = nullptr;
KfcVoid g_kfc_tick = nullptr, g_kfc_shutdown = nullptr;
KfcStatusFn g_kfc_status = nullptr;

HANDLE g_stop = nullptr;
std::thread g_thread;

// what shroudforge.json says
std::mutex g_status_mutex;
nlohmann::json g_status = nlohmann::json::object();

void set_status(const char* key, nlohmann::json value) {
    std::lock_guard lock(g_status_mutex);
    g_status[key] = std::move(value);
}

void write_status() {
    if (g_state.empty()) return;
    std::string text;
    {
        std::lock_guard lock(g_status_mutex);
        g_status["updated"] = static_cast<int64_t>(std::time(nullptr));
        text = g_status.dump(2);
    }
    std::error_code ec;
    std::filesystem::create_directories(g_state, ec);
    const auto path = g_state / L"shroudforge.json";
    const auto tmp = g_state / L"shroudforge.json.tmp";
    {
        std::ofstream out(tmp, std::ios::binary | std::ios::trunc);
        if (!out) return;
        out << text;
    }
    MoveFileExW(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING);
}

// mode: hosted | self-hosted | not-installed | off | failed
void finish(const char* mode, const std::string& reason) {
    set_status("mode", mode);
    set_status("reason", reason);
    write_status();
}

std::filesystem::path api_path(int32_t kind) {
    char buf[2048];
    size_t n = 0;
    if (g_api->path(kind, buf, sizeof buf, &n) != KILN_OK) return {};
    return std::filesystem::path(to_wide(std::string(buf, n)));
}

std::string module_file(HMODULE h) {
    wchar_t p[MAX_PATH] = L"";
    GetModuleFileNameW(h, p, MAX_PATH);
    return to_utf8(p);
}

// ShroudForge's bootstrap (exports ShroudforgeStop) or its runtime already in this process.
std::string sf_in_process() {
    HMODULE mods[1024];
    DWORD needed = 0;
    if (!K32EnumProcessModules(GetCurrentProcess(), mods, sizeof mods, &needed)) return {};
    const DWORD n = needed / sizeof(HMODULE) < 1024 ? needed / sizeof(HMODULE) : 1024;
    for (DWORD i = 0; i < n; ++i)
        if (GetProcAddress(mods[i], "ShroudforgeStop")) return module_file(mods[i]);
    if (HMODULE h = GetModuleHandleW(L"shroudforge-runtime.dll")) return module_file(h);
    return {};
}

std::string read_text(const std::filesystem::path& p) {
    std::ifstream in(p, std::ios::binary);
    if (!in) return {};
    return std::string((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
}

// Should this start host ShroudForge? reason explains a "no".
bool wanted(std::string* reason) {
    wchar_t env[8] = L"";
    const DWORD n = GetEnvironmentVariableW(L"KILN_SHROUDFORGE", env, 8);
    if (n == 1 && env[0] == L'0') return *reason = "KILN_SHROUDFORGE=0", false;
    if (n == 1 && env[0] == L'1') return true;
    const auto* profile = static_cast<const KilnProfileApi*>(g_api->service(KilnStr{KILN_PROFILE_SERVICE, sizeof(KILN_PROFILE_SERVICE) - 1}, 1));
    if (!profile) return true;  // no profile service: the legacy layout
    const uint32_t mode = profile->mode();
    if (mode == KILN_PROFILE_NO_MODS) return *reason = "play-once without mods", false;
    if (mode == KILN_PROFILE_INVALID) return *reason = "the profile is invalid (kiln.log says why): no mods load", false;
    if (mode == KILN_PROFILE_LEGACY) return true;
    // an active profile: on when its "mods.shroudforge" list has an enabled entry (the launcher writes each mod's
    // switch into ShroudForge's own extended.mod.json; this list says that the profile uses ShroudForge at all)
    const KilnStr js = profile->json();
    try {
        const auto doc = nlohmann::json::parse(std::string(js.ptr, js.len));
        const auto mods = doc.value("mods", nlohmann::json::object());
        const auto it = mods.find("shroudforge");
        if (it != mods.end() && it->is_array())
            for (const auto& e : *it)
                if (e.is_object() && e.value("enabled", true)) return true;
    } catch (...) {
    }
    *reason = "the active profile lists no ShroudForge mods";
    return false;
}

// ---- ShroudForge's log mirrored into kiln.log ----
struct LogTail {
    std::filesystem::path file;
    uint64_t offset = 0;
    std::string partial;
    void start() {
        file = sf_directory(g_game, "logs", "shroudforge/logs") /
               (g_server ? L"shroudforge-server.log" : L"shroudforge-client.log");
        std::error_code ec;
        offset = std::filesystem::exists(file, ec) ? std::filesystem::file_size(file, ec) : 0;
    }
    void poll() {
        std::error_code ec;
        const uint64_t size = std::filesystem::exists(file, ec) ? std::filesystem::file_size(file, ec) : 0;
        if (size < offset) offset = 0;  // rotated
        if (size == offset) return;
        std::ifstream in(file, std::ios::binary);
        if (!in) return;
        in.seekg(static_cast<std::streamoff>(offset));
        std::string chunk(static_cast<size_t>(std::min<uint64_t>(size - offset, 1 << 20)), '\0');
        in.read(chunk.data(), static_cast<std::streamsize>(chunk.size()));
        chunk.resize(static_cast<size_t>(in.gcount()));
        offset += chunk.size();
        partial += chunk;
        size_t start = 0, nl;
        while ((nl = partial.find('\n', start)) != std::string::npos) {
            std::string line = partial.substr(start, nl - start);
            start = nl + 1;
            while (!line.empty() && (line.back() == '\r' || line.back() == ' ')) line.pop_back();
            if (line.empty()) continue;
            const int32_t level = line.find("[E]") != std::string::npos || line.find(" ERROR ") != std::string::npos
                                      ? KILN_LOG_ERROR
                                  : line.find("[W]") != std::string::npos || line.find(" WARN ") != std::string::npos
                                      ? KILN_LOG_WARN
                                      : KILN_LOG_INFO;
            logf(level, "[sf] %s", line.c_str());
        }
        partial.erase(0, start);
    }
};
LogTail g_tail;

void write_heartbeat(int64_t updated_at) {
    // the same file and shape as ShroudForge's bootstrap (its Modloader window and tools read it)
    const auto dir = sf_directory(g_game, "runtime", "shroudforge/runtime");
    std::error_code ec;
    std::filesystem::create_directories(dir, ec);
    const auto tmp = dir / L"heartbeat.json.tmp";
    {
        std::ofstream out(tmp, std::ios::trunc);
        if (!out) return;
        out << "{\"schemaVersion\":1,\"pid\":" << GetCurrentProcessId() << ",\"mode\":\""
            << (g_server ? "SERVER" : "CLIENT") << "\",\"updatedAt\":" << updated_at << ",\"host\":\"kiln\"}";
    }
    MoveFileExW(tmp.c_str(), (dir / L"heartbeat.json").c_str(), MOVEFILE_REPLACE_EXISTING);
}

std::string kfc_status() {
    if (!g_kfc_status) return {};
    char text[2048] = {};
    g_kfc_status(text, sizeof text);
    return text;
}

// "state=..." from the provider's status text (ShroudForge logs the same field)
std::string status_field(const std::string& s, const char* key) {
    const std::string k = std::string(key) + "=";
    size_t at = s.find(k);
    if (at == std::string::npos) return {};
    at += k.size();
    const size_t end = s.find_first_of(" ,;", at);
    return s.substr(at, end == std::string::npos ? std::string::npos : end - at);
}

void run_loop() {
    const auto root = g_game.wstring();
    const auto t0 = std::chrono::steady_clock::now();
    void* handle = g_create(root.c_str());
    const double create_ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
    if (!handle) {
        logf(KILN_LOG_ERROR, "ShroudForge's runtime returned no handle (shroudforge_create, %.0f ms): its mods do not run; "
                             "see the [sf] lines above",
             create_ms);
        finish("failed", "shroudforge_create returned no handle (see ShroudForge's log)");
        g_tail.poll();
        return;
    }
    logf(KILN_LOG_INFO, "ShroudForge runtime ready (%.0f ms): its mods loop runs every 16 ms", create_ms);
    set_status("runtime", "running");
    finish("hosted", "");
    auto previous = std::chrono::steady_clock::now();
    auto next_status = previous;
    std::string last_state;
    uint64_t turns = 0;
    while (WaitForSingleObject(g_stop, 16) == WAIT_TIMEOUT) {
        const auto now = std::chrono::steady_clock::now();
        const double dt = std::chrono::duration<double>(now - previous).count();
        previous = now;
        if (g_kfc_tick) g_kfc_tick();
        if (!g_update(handle, dt)) {
            logf(KILN_LOG_ERROR, "ShroudForge's runtime update failed: its Lua runtime stops (Kiln runs on)");
            set_status("runtime", "stopped");
            finish("failed", "shroudforge_update failed (see ShroudForge's log)");
            break;
        }
        ++turns;
        if (now >= next_status) {
            next_status = now + std::chrono::seconds(2);
            write_heartbeat(static_cast<int64_t>(std::time(nullptr)));
            g_tail.poll();
            const std::string st = kfc_status();
            const std::string state = status_field(st, "state");
            if (state != last_state) {
                logf(KILN_LOG_INFO, "ShroudForge native runtime state: %s", state.empty() ? "?" : state.c_str());
                last_state = state;
            }
            set_status("native", st);
            set_status("turns", turns);
            write_status();
        }
    }
    g_tail.poll();
    g_destroy(handle);
    if (g_kfc_shutdown) g_kfc_shutdown();
    stop_helper_processes();
    std::error_code ec;
    std::filesystem::remove(sf_directory(g_game, "runtime", "shroudforge/runtime") / L"heartbeat.json", ec);
}

struct Candidate {
    std::string name;
    std::filesystem::path dir;
    bool present = false;
    std::string version, build;
};

// kiln.json "shroudforge": {"useInstalled": true}; KILN_SHROUDFORGE_RUNTIME=installed|bundled overrides it.
bool prefer_installed(const std::filesystem::path& kiln_dir) {
    wchar_t env[16] = L"";
    const DWORD n = GetEnvironmentVariableW(L"KILN_SHROUDFORGE_RUNTIME", env, 16);
    if (n > 0 && n < 16) return std::wstring(env) == L"installed";
    try {
        const std::string t = read_text(kiln_dir / L"kiln.json");
        if (t.empty()) return false;
        const auto j = nlohmann::json::parse(t, nullptr, true, true);
        const auto it = j.find("shroudforge");
        if (it != j.end() && it->is_object()) return it->value("useInstalled", false);
    } catch (...) {
    }
    return false;
}

// The bundled runtime reads game-build profiles from ShroudForge's runtime folder (<game>/shroudforge/runtime/profiles,
// or its configured paths.runtime). Copy the bundled ones that are missing there; never overwrite a player's file.
void seed_profiles(const std::filesystem::path& src) {
    const auto dst = sf_directory(g_game, "runtime", "shroudforge/runtime") / L"profiles";
    std::error_code ec;
    unsigned copied = 0;
    for (auto it = std::filesystem::recursive_directory_iterator(src, ec); !ec && it != std::filesystem::recursive_directory_iterator();
         it.increment(ec)) {
        if (!it->is_regular_file(ec)) continue;
        const auto rel = std::filesystem::relative(it->path(), src, ec);
        const auto target = dst / rel;
        if (std::filesystem::exists(target, ec)) continue;
        std::filesystem::create_directories(target.parent_path(), ec);
        if (std::filesystem::copy_file(it->path(), target, ec)) ++copied;
    }
    if (copied) logf(KILN_LOG_INFO, "copied %u bundled game profile(s) to %s", copied, to_utf8(dst.wstring()).c_str());
}

void KILN_CALL on_ready(void*) {
    g_game = api_path(KILN_PATH_GAME);
    g_state = api_path(KILN_PATH_STATE);
    g_server = !(g_api->size >= offsetof(KilnApi, side) + sizeof(void*) && g_api->side() == KILN_SIDE_CLIENT);
    set_status("format", 1);
    set_status("side", g_server ? "server" : "client");
    // where ShroudForge reads its packages (EML packages there run under ShroudForge: other EML runners skip them)
    set_status("modsDir", to_utf8(sf_directory(g_game, "mods", "mods").wstring()));
    // ---- which runtime: Kiln's bundled build (default) or the player's installed ShroudForge ----
    const auto kiln_dir = api_path(KILN_PATH_KILN);
    Candidate bundled{"bundled", kiln_dir / L"plugins" / L"shroudforge"};
    Candidate installed{"installed", g_game / L"shroudforge"};
    for (Candidate* c : {&bundled, &installed}) {
        std::error_code ec;
        c->present = std::filesystem::is_regular_file(c->dir / L"shroudforge-runtime.dll", ec);
        try {
            const std::string v = read_text(c->dir / L"version.json");
            if (!v.empty()) {
                const auto j = nlohmann::json::parse(v);
                c->version = j.value("version", "");
                if (j.contains("build") && j["build"].is_string()) c->build = j["build"].get<std::string>();
            }
        } catch (...) {
        }
    }
    set_status("bundled", bundled.present ? nlohmann::json{{"version", bundled.version}, {"build", bundled.build}} : nlohmann::json());
    set_status("installed", installed.present ? nlohmann::json{{"version", installed.version}} : nlohmann::json());
    const bool use_installed = prefer_installed(kiln_dir);
    set_status("useInstalled", use_installed);
    if (!bundled.present && !installed.present) {
        logf(KILN_LOG_INFO, "no ShroudForge runtime: neither Kiln's bundled one (kiln/plugins/shroudforge/) nor an "
                            "installed ShroudForge (shroudforge/): nothing to host");
        return finish("not-installed", "no ShroudForge runtime (bundled component missing, ShroudForge not installed)");
    }
    if (const std::string other = sf_in_process(); !other.empty()) {
        logf(KILN_LOG_INFO, "ShroudForge already runs in this process (%s, started by its own dbghelp.dll): it hosts "
                            "its mods itself next to Kiln; kiln-shroudforge stays out of the way",
             other.c_str());
        set_status("source", "installed");
        return finish("self-hosted", "ShroudForge's own bootstrap runs (" + other + ")");
    }
    std::string reason;
    if (!wanted(&reason)) {
        logf(KILN_LOG_INFO, "ShroudForge mods are off for this start: %s", reason.c_str());
        return finish("off", reason);
    }
    // order of attempts: the chosen one, then the other as a fallback
    std::vector<Candidate*> order;
    if (use_installed) {
        if (installed.present) order.push_back(&installed);
        else logf(KILN_LOG_WARN, "\"Use my installed ShroudForge\" is on, but no ShroudForge is installed in this game "
                                 "folder: using Kiln's bundled ShroudForge runtime");
        if (bundled.present) order.push_back(&bundled);
    } else {
        if (bundled.present) order.push_back(&bundled);
        else if (installed.present) {
            logf(KILN_LOG_WARN, "Kiln's bundled ShroudForge runtime is not installed (kiln/plugins/shroudforge/): using "
                                "the installed ShroudForge %s", installed.version.c_str());
            order.push_back(&installed);
        }
        if (installed.present && bundled.present)
            logf(KILN_LOG_INFO, "an installed ShroudForge %s is here too; Kiln uses its bundled runtime (switch: launcher "
                                "Settings \"Use my installed ShroudForge\", kiln.json shroudforge.useInstalled)",
                 installed.version.c_str());
    }
    PrepareStartup prepare = nullptr;
    Candidate* chosen = nullptr;
    std::string last_error;
    for (Candidate* c : order) {
        const auto dll = c->dir / L"shroudforge-runtime.dll";
        HMODULE h = LoadLibraryExW(dll.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (!h) {
            last_error = c->name + " shroudforge-runtime.dll could not be loaded (error " + std::to_string(GetLastError()) + ")";
            logf(KILN_LOG_WARN, "%s", last_error.c_str());
            continue;
        }
        const auto p = reinterpret_cast<PrepareStartup>(GetProcAddress(h, "shroudforge_prepare_startup"));
        const auto cr = reinterpret_cast<CreateRuntime>(GetProcAddress(h, "shroudforge_create"));
        const auto up = reinterpret_cast<UpdateRuntime>(GetProcAddress(h, "shroudforge_update"));
        const auto de = reinterpret_cast<DestroyRuntime>(GetProcAddress(h, "shroudforge_destroy"));
        if (!p || !cr || !up || !de) {
            last_error = c->name + " shroudforge-runtime.dll lacks the bootstrap exports (an unsupported ShroudForge version)";
            logf(KILN_LOG_WARN, "%s", last_error.c_str());
            FreeLibrary(h);
            continue;
        }
        g_runtime = h;
        prepare = p;
        g_create = cr;
        g_update = up;
        g_destroy = de;
        chosen = c;
        break;
    }
    if (!chosen) {
        logf(KILN_LOG_ERROR, "no usable ShroudForge runtime (%s): ShroudForge mods do not run, Kiln runs on", last_error.c_str());
        return finish("failed", last_error);
    }
    if (chosen != order.front())
        logf(KILN_LOG_WARN, "falling back to the %s ShroudForge runtime", chosen->name.c_str());
    set_status("source", chosen->name);
    set_status("runtimeDir", to_utf8(chosen->dir.wstring()));
    set_status("version", chosen->version);
    logf(KILN_LOG_INFO, "ShroudForge runtime: %s %s%s%s from %s", chosen->name.c_str(),
         chosen->version.empty() ? "?" : chosen->version.c_str(), chosen->build.empty() ? "" : " ",
         chosen->build.c_str(), to_utf8(chosen->dir.wstring()).c_str());
    if (chosen == &bundled) seed_profiles(bundled.dir / L"profiles");
    const auto kfc_dll = chosen->dir / L"kfc-runtime.dll";
    g_tail.start();
    start_helper_processes(g_game, g_server);
    const auto t0 = std::chrono::steady_clock::now();
    const bool prepared = prepare(g_game.wstring().c_str());
    const double prep_ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
    g_tail.poll();
    if (!prepared) {
        // ShroudForge itself stops the game here; Kiln starts it without ShroudForge instead
        logf(KILN_LOG_ERROR, "ShroudForge's startup preparation failed (%.0f ms; see the [sf] lines): its mods do not "
                             "run this start, the game starts without them",
             prep_ms);
        stop_helper_processes();
        return finish("failed", "ShroudForge's startup preparation failed (startup asset mods or its DLL Manager)");
    }
    logf(KILN_LOG_INFO, "ShroudForge startup preparation done (%.0f ms)", prep_ms);
    g_kfc = LoadLibraryExW(kfc_dll.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (g_kfc) {
        const auto abi = reinterpret_cast<KfcAbi>(GetProcAddress(g_kfc, "KfcRuntimeAbi"));
        const auto init = reinterpret_cast<KfcBool>(GetProcAddress(g_kfc, "KfcRuntimeInitialize"));
        g_kfc_tick = reinterpret_cast<KfcVoid>(GetProcAddress(g_kfc, "KfcRuntimeTick"));
        g_kfc_shutdown = reinterpret_cast<KfcVoid>(GetProcAddress(g_kfc, "KfcRuntimeShutdown"));
        g_kfc_status = reinterpret_cast<KfcStatusFn>(GetProcAddress(g_kfc, "KfcRuntimeStatus"));
        const uint32_t v = abi ? abi() : 0;
        set_status("nativeAbi", v);
        if (!abi || !init || !g_kfc_tick || !g_kfc_shutdown || !g_kfc_status) {
            logf(KILN_LOG_WARN, "kfc-runtime.dll lacks the provider exports: ShroudForge mods get no live game access");
            g_kfc_tick = g_kfc_shutdown = nullptr;
            g_kfc_status = nullptr;
        } else if (!init()) {
            logf(KILN_LOG_WARN, "ShroudForge's native runtime (ABI %u) did not initialize: %s", v, kfc_status().c_str());
        } else {
            logf(KILN_LOG_INFO, "ShroudForge's native runtime (ABI %u) initialized: %s", v, kfc_status().c_str());
        }
    } else {
        logf(KILN_LOG_WARN, "kfc-runtime.dll could not be loaded (error %lu): ShroudForge mods get no live game access",
             GetLastError());
    }
    g_stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    set_status("runtime", "starting");
    finish("hosted", "");
    try {
        g_thread = std::thread(run_loop);
    } catch (...) {
        logf(KILN_LOG_ERROR, "could not start ShroudForge's mods thread");
        finish("failed", "could not start the mods thread");
    }
}

void KILN_CALL on_unload(void*) {
    // process exit: like ShroudForge's bootstrap on DLL_PROCESS_DETACH, only signal (threads are gone or frozen)
    if (g_stop) SetEvent(g_stop);
    if (g_thread.joinable()) g_thread.detach();
}

}  // namespace

void logf(int32_t level, const char* fmt, ...) {
    if (!g_api) return;
    char buf[2048];
    va_list ap;
    va_start(ap, fmt);
    int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (n >= static_cast<int>(sizeof buf)) n = sizeof buf - 1;
    g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(n)});
}

std::string to_utf8(const std::wstring& w) {
    if (w.empty()) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), nullptr, 0, nullptr, nullptr);
    std::string s(static_cast<size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), s.data(), n, nullptr, nullptr);
    return s;
}

std::wstring to_wide(const std::string& s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

}  // namespace ksf

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner,
                                                                       KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < offsetof(KilnApi, server_world)) return KILN_ERR_UNAVAILABLE;
    ksf::g_api = api;
    ksf::g_owner = owner;
    info->id = KilnStr{"shroudforge", 11};
    info->version = KilnStr{"0.1.0", 5};
    info->on_ready = &ksf::on_ready;
    info->on_unload = &ksf::on_unload;
    return KILN_OK;
}
