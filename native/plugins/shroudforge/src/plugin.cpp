// SPDX-License-Identifier: MIT
// kiln-shroudforge.dll: a Kiln plugin (<game>/kiln/plugins/) that runs ShroudForge's Lua mods unchanged by driving a
// ShroudForge runtime (github.com/bonsaibauer/shroudforge, MIT) through its exported C functions. Docs:
// docs/kiln/shroudforge-compat.md. This DLL is MIT and holds no GPL implementation; processes.cpp credits the adapted SF MIT helper launcher.
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
//   on a client, before the worker: the game window's key records from kiln.ui go to the runtime's host key feed, when
//     it has one, in place of ShroudForge's shroudforge.exe helper (key_feed.cpp). The worker starts from the early
//     on_ready, before kiln.ui exists: the feed begins there and attaches to kiln.ui at Kiln's plugin step, whose
//     on_ready comes later (on_ready below, the g_runtime branch);
//   a worker thread (ShroudForge's own mods loop, same timing): shroudforge_create(<game>), then every 16 ms
//     KfcRuntimeTick + shroudforge_update(handle, dt); next to it a writer thread, every 2 s: the heartbeat file
//     ShroudForge's tools read, ShroudForge's log mirrored into kiln.log ("[sf] ..."), <state>/shroudforge.json for the
//     launcher (so slow file I/O never holds up the loop).
// Fail soft: anything missing or failing is logged and written to shroudforge.json; Kiln and its mods run on.
// Not done here: ShroudForge's startup update check and pending-update install (Kiln owns updates, so ShroudForge's
// updater never replaces Kiln's dinput8.dll) and its Debug Console window (kiln.log carries ShroudForge's log).
#include <algorithm>
#include <atomic>
#include <ctime>
#include <cstring>
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
#include "contract.h"
#include "kfc_host.h"
#include "services/kiln_eml.h"

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
HANDLE g_worker = nullptr;
bool g_initialized = false;
bool g_early = false;
bool g_prepared = false;
HANDLE g_install_lock = INVALID_HANDLE_VALUE; // lifetime lock when kiln-eml is not installed

// DLL exports are foreign code. SEH guards use POD-only frames; C++ exceptions are caught at the worker boundary.
template<class F, class R, class... A> bool invoke(F fn, R* result, A... args) {
    __try { *result = fn(args...); return true; }
    __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}
template<class F, class... A> bool invoke_void(F fn, A... args) {
    __try { fn(args...); return true; }
    __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}

bool inside_root(const std::filesystem::path& p, const std::filesystem::path& root) {
    auto pi = p.begin();
    for (auto ri = root.begin(); ri != root.end(); ++ri, ++pi)
        if (pi == p.end() || _wcsicmp(pi->c_str(), ri->c_str())) return false;
    for (; pi != p.end(); ++pi) {
        const auto part = pi->native();
        if (part == L".." || part.find(L':') != std::wstring::npos) return false;
    }
    return true;
}

bool safe_path(const std::filesystem::path& p, const std::filesystem::path& root) {
    auto base = root, full = p;
    while (base != base.root_path() && !base.empty() && base.filename().empty()) base = base.parent_path();
    while (full != full.root_path() && !full.empty() && full.filename().empty()) full = full.parent_path();
    if (!full.is_absolute() || !base.is_absolute() || !inside_root(full, base)) return false;
    // The root and every component below it must not be links. Ancestors belong to the player's layout;
    // in particular, Wine's Z:\ drive is itself a reparse point and must not be judged here.
    for (auto part = full; !part.empty();) {
        const DWORD a = GetFileAttributesW(part.c_str()); // by-name query also exposes Wine directory symlinks
        if (a == INVALID_FILE_ATTRIBUTES || (a & FILE_ATTRIBUTE_REPARSE_POINT)) return false;
        if (!_wcsicmp(part.c_str(), base.c_str())) break;
        const auto parent = part.parent_path();
        if (parent == part) return false;
        part = parent;
    }
    std::error_code ec;
    const auto resolved_root = std::filesystem::canonical(base, ec);
    if (ec) return false;
    const auto resolved = std::filesystem::canonical(full, ec);
    return !ec && inside_root(resolved, resolved_root);
}

HMODULE load_runtime_dll(const std::filesystem::path& p) {
    if (!safe_path(p, p.parent_path())) return nullptr;
    HMODULE h = LoadLibraryExW(p.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!h) return nullptr;
    wchar_t actual[32768]{};
    const DWORD n = GetModuleFileNameW(h, actual, 32768);
    std::error_code ec;
    if (!n || n == 32768 || !std::filesystem::equivalent(p, actual, ec)) { FreeLibrary(h); return nullptr; }
    return h;
}

// what shroudforge.json says
std::mutex g_status_mutex;
nlohmann::json g_status = nlohmann::json::object();

void set_status(const char* key, nlohmann::json value) {
    std::lock_guard lock(g_status_mutex);
    g_status[key] = std::move(value);
}

// The status writer thread (HeartbeatWriter) and finish() on any thread share shroudforge.json.tmp: one write at a
// time, and each takes its snapshot inside that turn, so the file never goes back to an older state. set_status never
// waits for this I/O (it holds only g_status_mutex, for the dump).
std::mutex g_status_file_mutex;

void write_status() {
    if (g_state.empty()) return;
    std::lock_guard file_lock(g_status_file_mutex);
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
    if (std::strcmp(mode, "failed") == 0) set_status("runtime", "stopped");
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
    wchar_t p[32768] = L"";
    const auto n = GetModuleFileNameW(h, p, 32768);
    if (!n || n == 32768) return "<loaded module path unavailable>";
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

// A bundled DLL alone must not take EML-only installations away from kiln-eml's private/profile roots.
bool legacy_wanted(std::string* reason) {
    std::error_code ec;
    const auto dir = sf_directory(g_game, "mods", "mods");
    for (auto it = std::filesystem::directory_iterator(dir, ec);
         !ec && it != std::filesystem::directory_iterator(); it.increment(ec)) {
        const auto extended = it->path() / L"extended.mod.json";
        std::error_code file_ec;
        if (!std::filesystem::is_regular_file(extended, file_ec)) continue;
        try {
            const auto j = nlohmann::json::parse(read_text(extended));
            if (j.value("enabled", true) && j.value("launcher", std::string{}) != "EML") return true;
        } catch (...) { return true; } // let SF diagnose its own malformed package
    }
    try {
        // ShroudForge's state file: <paths.state>/state.json, by default <game>/shroudforge/state.json (its own default
        // state folder is its data folder, not shroudforge/state)
        const auto state = nlohmann::json::parse(read_text(sf_directory(g_game, "state", "shroudforge") / L"state.json"));
        if (const auto it = state.find("assets"); it != state.end()) {
            const auto status = it->value("status", std::string{});
            if (status == "applied" || status == "preparing" || status == "failed") return true;
        }
    } catch (...) {}
    *reason = "no enabled ShroudForge packages or previous startup patch; EML stays with kiln-eml";
    return false;
}

// Should this start host ShroudForge? reason explains a "no".
bool wanted(std::string* reason) {
    wchar_t env[8] = L"";
    const DWORD n = GetEnvironmentVariableW(L"KILN_SHROUDFORGE", env, 8);
    if (n == 1 && env[0] == L'0') return *reason = "KILN_SHROUDFORGE=0", false;
    if (n == 1 && env[0] == L'1') return true;
    const auto* profile = static_cast<const KilnProfileApi*>(g_api->service(KilnStr{KILN_PROFILE_SERVICE, sizeof(KILN_PROFILE_SERVICE) - 1}, 1));
    if (profile && (profile->header.version < 1 || profile->header.size < offsetof(KilnProfileApi, json) + sizeof profile->json ||
                    !profile->mode || !profile->json)) { *reason = "incompatible profile service"; return false; }
    if (!profile) return legacy_wanted(reason);  // no profile service: the legacy layout
    const uint32_t mode = profile->mode();
    if (mode == KILN_PROFILE_NO_MODS) return *reason = "play-once without mods", false;
    if (mode == KILN_PROFILE_INVALID) return *reason = "the profile is invalid (kiln.log says why): no mods load", false;
    if (mode == KILN_PROFILE_LEGACY) return legacy_wanted(reason);
    // an active profile: on when its "mods.shroudforge" list has an enabled entry (the launcher writes each mod's
    // switch into ShroudForge's own extended.mod.json; this list says that the profile uses ShroudForge at all)
    // Drop-ins on for ShroudForge ("dropins": true, or a "dropins" list naming "shroudforge"): the packages in its own
    // mods folder run too, so ShroudForge is on when there is one (legacy_wanted's check), as without a profile.
    const KilnStr js = profile->json();
    bool dropins = false;
    try {
        const auto doc = nlohmann::json::parse(std::string(js.ptr, js.len));
        const auto mods = doc.value("mods", nlohmann::json::object());
        const auto it = mods.find("shroudforge");
        if (it != mods.end() && it->is_array())
            for (const auto& e : *it)
                if (e.is_object() && e.value("enabled", true)) return true;
        if (const auto d = doc.find("dropins"); d != doc.end()) {
            if (d->is_boolean()) dropins = d->get<bool>();
            else if (d->is_array())
                for (const auto& k : *d)
                    if (k.is_string() && k.get<std::string>() == "shroudforge") dropins = true;
        }
    } catch (...) {
    }
    if (dropins) {
        if (legacy_wanted(reason)) return true;
        *reason = "the active profile lists no ShroudForge mods, and ShroudForge's own mods folder (drop-ins on) has " + *reason;
        return false;
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
        if (partial.size() > 65536) partial.erase(0, partial.size() - 65536);
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

// The mods loop's periodic filesystem I/O must not hold up native/Lua ticks: every 2 s this thread writes the
// heartbeat, mirrors ShroudForge's new log lines into kiln.log and writes shroudforge.json (what the loop last put in
// it with set_status). It never calls either runtime ABI; the heartbeat's timestamp is the update loop's last turn,
// not its own wall clock, so a stalled loop cannot look alive just because I/O runs. g_tail is only polled here while
// this thread runs (the worker polls it again only after stop() joined it).
class HeartbeatWriter {
    HANDLE stop_ = nullptr;
    std::thread thread_;
    std::atomic<int64_t> last_turn_{0};
public:
    void start() {
        last_turn_.store(static_cast<int64_t>(std::time(nullptr)), std::memory_order_relaxed);
        stop_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (stop_) {
            try {
                thread_ = std::thread([this] {
                    while (WaitForSingleObject(stop_, 0) == WAIT_TIMEOUT) {
                        try { write_heartbeat(last_turn_.load(std::memory_order_relaxed)); }
                        catch (...) { logf(KILN_LOG_WARN, "ShroudForge heartbeat write failed"); }
                        try { g_tail.poll(); }
                        catch (...) { logf(KILN_LOG_WARN, "ShroudForge log mirror failed"); }
                        try { write_status(); }
                        catch (...) { logf(KILN_LOG_WARN, "ShroudForge status write failed"); }
                        if (WaitForSingleObject(stop_, 2000) != WAIT_TIMEOUT) break;
                    }
                });
                return;
            } catch (...) {
                CloseHandle(stop_);
                stop_ = nullptr;
            }
        }
        // Do not fall back to blocking the mods loop. Missing heartbeat and status data is
        // safer than stale steering/input while a synchronous write stalls it.
        logf(KILN_LOG_WARN, "ShroudForge status writer could not start: tools may report it inactive, shroudforge.json "
                            "and the [sf] lines update only when its runtime stops");
    }
    void turn() { last_turn_.store(static_cast<int64_t>(std::time(nullptr)), std::memory_order_relaxed); }
    void stop() {
        if (stop_) SetEvent(stop_);
        if (thread_.joinable()) thread_.join();
        if (stop_) { CloseHandle(stop_); stop_ = nullptr; }
    }
    ~HeartbeatWriter() { stop(); }
};

// keys in shroudforge.json and its kiln.log line (key_feed.cpp says which source and why).
void report_keys(const KeySource& keys) {
    set_status("keys", nlohmann::json{{"source", keys.source}, {"reason", keys.reason}});
    const char* from = !std::strcmp(keys.source, "kiln")      ? "Kiln"
                       : !std::strcmp(keys.source, "waiting") ? "Kiln, waiting for kiln.ui"
                       : !std::strcmp(keys.source, "helper")  ? "ShroudForge's own input"
                                                               : "nowhere";
    logf(KILN_LOG_INFO, "ShroudForge mod shortcuts: key state from %s: %s", from, keys.reason.c_str());
}

// The key feed's teardown, before shroudforge_destroy. A feed still waiting for the plugin step gets its last word here.
void stop_key_feed() {
    KeySource last{};
    if (!key_feed_stop(&last)) return;
    report_keys(last);
    write_status();
}

std::string kfc_status() {
    if (!g_kfc_status) return {};
    char text[2048] = {};
    if (!invoke_void(g_kfc_status, text, sizeof text)) return "state=faulted";
    text[sizeof text - 1] = 0;
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
    struct Cleanup {
        void* handle = nullptr;
        HeartbeatWriter heartbeat;
        ~Cleanup() {
            // Only this serial worker joins, never on_unload/DllMain. Even on
            // an update exception, finish the writer before runtime teardown.
            heartbeat.stop();
            stop_key_feed();  // the last host key feed call, before the runtime goes (key_feed.cpp)
            if (handle) invoke_void(g_destroy, handle);
            if (g_initialized && g_kfc_shutdown) invoke_void(g_kfc_shutdown);
            g_initialized = false;
            stop_helper_processes();
            try {
                std::error_code ec;
                std::filesystem::remove(sf_directory(g_game, "runtime", "shroudforge/runtime") / L"heartbeat.json", ec);
            } catch (...) {}
        }
    } cleanup;
    const auto root = g_game.wstring();
    const auto t0 = std::chrono::steady_clock::now();
    void* handle = nullptr;
    invoke(g_create, &handle, root.c_str());
    cleanup.handle = handle;
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
    set_status("runtime", "running");  // start_worker wrote mode "hosted"; the writer's first turn writes this
    cleanup.heartbeat.start();
    auto previous = std::chrono::steady_clock::now();
    auto next_status = previous;
    std::string last_state;
    uint64_t turns = 0;
    while (WaitForSingleObject(g_stop, 16) == WAIT_TIMEOUT) {
        const auto now = std::chrono::steady_clock::now();
        const double dt = std::chrono::duration<double>(now - previous).count();
        previous = now;
        cleanup.heartbeat.turn();
        bool updated = false;
        const bool tick_ok = !g_initialized || !g_kfc_tick || invoke_void(g_kfc_tick);
        if (!tick_ok || !invoke(g_update, &updated, handle, dt) || !updated) {
            logf(KILN_LOG_ERROR, "ShroudForge's runtime update failed: its Lua runtime stops (Kiln runs on)");
            set_status("runtime", "stopped");
            finish("failed", "shroudforge_update failed (see ShroudForge's log)");
            break;
        }
        ++turns;
        if (now >= next_status) {  // no file I/O here: the writer thread writes this out
            next_status = now + std::chrono::seconds(2);
            const std::string st = kfc_status();
            const std::string state = status_field(st, "state");
            if (state != last_state) {
                logf(KILN_LOG_INFO, "ShroudForge native runtime state: %s", state.empty() ? "?" : state.c_str());
                last_state = state;
            }
            set_status("native", st);
            set_status("menu", helper_menu());
            set_status("turns", turns);
        }
    }
    cleanup.heartbeat.stop();
    g_tail.poll();
    set_status("runtime", "stopped");
    write_status();
}

DWORD WINAPI worker(void*) {
    try { run_loop(); }
    catch (...) {
        logf(KILN_LOG_ERROR, "ShroudForge worker failed; stopping its runtime");
        finish("failed", "ShroudForge worker threw an exception");
    }
    return 0;
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
    if (!safe_path(src, src)) return;
    for (auto it = std::filesystem::recursive_directory_iterator(src, ec); !ec && it != std::filesystem::recursive_directory_iterator();
         it.increment(ec)) {
        if (!it->is_regular_file(ec) || !safe_path(it->path(), src)) continue;
        const auto rel = it->path().lexically_relative(src);
        if (rel.empty() || rel.is_absolute()) continue;
        const auto target = dst / rel;
        auto parent = target.parent_path();
        while (!parent.empty() && !std::filesystem::exists(parent, ec)) parent = parent.parent_path();
        if (parent.empty() || (inside_root(parent, dst) && !safe_path(parent, dst))) continue;
        if (std::filesystem::exists(target, ec)) continue;
        std::filesystem::create_directories(target.parent_path(), ec);
        if (ec || !safe_path(target.parent_path(), dst)) continue;
        if (std::filesystem::copy_file(it->path(), target, ec)) ++copied;
    }
    if (copied) logf(KILN_LOG_INFO, "copied %u bundled game profile(s) to %s", copied, to_utf8(dst.wstring()).c_str());
}

// dev.21 (WP-F4): the bundled native runtime can take its engine hooks from kiln.hooks (kfc_host.h), so that a DLL which
// hooked one of its places first no longer turns it off. It asks through the optional export KfcRuntimeSetHost, before
// KfcRuntimeInitialize (the hooks are placed there). A runtime without the export (the player's installed ShroudForge,
// an older bundle), a start without kiln.hooks v2 (safe idle) or KILN_SF_ENGINE_HOOKS=self leave it hooking the game
// itself, exactly as before.
void offer_engine_hooks() {
    const auto set_host = reinterpret_cast<KfcSetHost>(GetProcAddress(g_kfc, "KfcRuntimeSetHost"));
    wchar_t env[16] = L"";
    const DWORD n = GetEnvironmentVariableW(L"KILN_SF_ENGINE_HOOKS", env, 16);
    const char* why = nullptr;
    const KfcHost* table = nullptr;
    if (n > 0 && n < 16 && std::wstring(env) == L"self") why = "KILN_SF_ENGINE_HOOKS=self";
    else if (!set_host) why = "this kfc-runtime.dll has no host interface (KfcRuntimeSetHost)";
    else if (!(table = host_table(g_api, g_owner))) why = "kiln.hooks v2 is not available to this plugin (safe idle?)";
    if (!why) {
        bool accepted = false;
        if (invoke(set_host, &accepted, table) && accepted) {
            logf(KILN_LOG_INFO, "ShroudForge's native runtime takes its engine hooks from kiln.hooks (host interface v%u)",
                 table->version);
            set_status("engineHooks", "kiln.hooks");
            return;
        }
        why = "kfc-runtime.dll refused the host interface";
    }
    logf(KILN_LOG_INFO, "ShroudForge's native runtime hooks the game itself: %s", why);
    set_status("engineHooks", "self");
}

// plugin_step_later: called by the early on_ready (kiln_plugin_early), before Kiln has loaded kiln.ui; Kiln's plugin
// step and its on_ready come later in this start, unless it is safe idle.
void start_worker(bool plugin_step_later) {
    if (!g_prepared || g_worker) return;
    g_stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!g_stop) {
        if (g_initialized && g_kfc_shutdown) invoke_void(g_kfc_shutdown);
        g_initialized = g_prepared = false;
        stop_helper_processes();
        return finish("failed", "could not create the stop event");
    }
    // Mod shortcuts: Kiln's host key feed, ShroudForge's own helper, or none. The feed begins before the worker's
    // shroudforge_create, as input_host.h asks (mods ask for input while they load).
    report_keys(key_feed_start(g_api, g_owner, g_runtime, g_game, g_server, plugin_step_later));
    set_status("runtime", "starting");
    finish("hosted", "");
    g_worker = CreateThread(nullptr, 0, worker, nullptr, 0, nullptr);
    if (!g_worker) {
        stop_key_feed();
        if (g_initialized && g_kfc_shutdown) invoke_void(g_kfc_shutdown);
        g_initialized = g_prepared = false;
        stop_helper_processes();
        CloseHandle(g_stop); g_stop = nullptr;
        finish("failed", "could not start the mods thread");
    }
}

bool reserve_writer() {
    HMODULE eml = GetModuleHandleW(L"kiln-eml.dll");
    if (!eml) {
        // SF is also supported without the optional EML DLL. Use the same OS lock as Rust's File::try_lock,
        // and retain it until process exit. A busy install must not proceed into the game as a reader either.
        const auto path = g_game / L".kiln-eml.lock";
        HANDLE lock = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                  nullptr, OPEN_ALWAYS, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        BY_HANDLE_FILE_INFORMATION info{};
        OVERLAPPED overlapped{};
        bool available = lock != INVALID_HANDLE_VALUE && GetFileInformationByHandle(lock, &info) &&
                         !(info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) &&
                         LockFileEx(lock, LOCKFILE_EXCLUSIVE_LOCK | LOCKFILE_FAIL_IMMEDIATELY, 0,
                                    MAXDWORD, MAXDWORD, &overlapped);
        DWORD pids[65536], needed = 0;
        if (available) {
            available = K32EnumProcesses(pids, sizeof pids, &needed) && needed < sizeof pids;
            std::error_code ec;
            const auto game = std::filesystem::canonical(g_game, ec);
            if (ec) available = false;
            for (DWORD i = 0; available && i < needed / sizeof(DWORD); ++i) {
                if (!pids[i] || pids[i] == GetCurrentProcessId()) continue;
                HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pids[i]);
                if (!process) continue;
                wchar_t name[32768]; DWORD len = 32768;
                const bool queried = QueryFullProcessImageNameW(process, 0, name, &len);
                CloseHandle(process);
                if (!queried) continue;
                const auto image = std::filesystem::canonical(std::filesystem::path(std::wstring(name, len)), ec);
                if (ec) continue;
                const auto file = image.filename().wstring();
                if ((!_wcsicmp(file.c_str(), L"enshrouded.exe") || !_wcsicmp(file.c_str(), L"enshrouded_server.exe")) &&
                    !_wcsicmp(image.parent_path().c_str(), game.c_str())) available = false;
            }
        }
        if (!available) {
            if (lock != INVALID_HANDLE_VALUE) CloseHandle(lock);
            finish("failed", "game data is in use or cannot be locked; stopping this start");
            ExitProcess(ERROR_SHARING_VIOLATION);
        }
        g_install_lock = lock;
        return true;
    }
    auto get = reinterpret_cast<KilnEmlServiceFn>(GetProcAddress(eml, KILN_EML_SERVICE_EXPORT));
    const KilnEmlApi* table = nullptr;
    if (!get || !invoke(get, &table, KILN_EML_VERSION) || !table || table->header.version < 2 ||
        table->header.size < sizeof(KilnEmlApi) || !table->stand_aside) return false;
    constexpr char reason[] = "ShroudForge owns startup asset patching and runs EML packages in its mods folder";
    KilnResult result = KILN_ERR_INTERNAL;
    return invoke(table->stand_aside, &result, KilnStr{reason, sizeof reason - 1}) && result == KILN_OK;
}

void KILN_CALL on_ready(void*) {
    if (g_runtime) {
        // Kiln's plugin step, after the early pass prepared the runtime (and its on_ready started the worker): kiln.ui is
        // published now, so a key feed that began early attaches to it.
        start_worker(false);
        KeySource keys{};
        if (key_feed_attach(g_api, g_owner, &keys)) {
            report_keys(keys);
            write_status();
        }
        return;
    }
    g_game = api_path(KILN_PATH_GAME);
    g_state = api_path(KILN_PATH_STATE);
    if (g_game.empty() || !safe_path(g_game, g_game)) return finish("failed", "invalid game path");
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
    if (const std::string other = sf_in_process(); !other.empty()) {
        logf(KILN_LOG_INFO, "ShroudForge already runs in this process (%s, started by its own dbghelp.dll): it hosts "
                            "its mods itself next to Kiln; kiln-shroudforge stays out of the way",
             other.c_str());
        set_status("source", "installed");
        return finish("self-hosted", "ShroudForge's own bootstrap runs (" + other + ")");
    }
    if (!bundled.present && !installed.present) {
        logf(KILN_LOG_INFO, "no ShroudForge runtime: neither Kiln's bundled one (kiln/plugins/shroudforge/) nor an "
                            "installed ShroudForge (shroudforge/): nothing to host");
        return finish("not-installed", "no ShroudForge runtime (bundled component missing, ShroudForge not installed)");
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
        if (installed.present && bundled.present) {
            order.push_back(&installed);
            logf(KILN_LOG_INFO, "an installed ShroudForge %s is here too; Kiln uses its bundled runtime (switch: launcher "
                                "Settings \"Use my installed ShroudForge\", kiln.json shroudforge.useInstalled)",
                 installed.version.c_str());
        }
    }
    PrepareStartup prepare = nullptr;
    Candidate* chosen = nullptr;
    std::string last_error;
    for (Candidate* c : order) {
        const auto dll = c->dir / L"shroudforge-runtime.dll";
        if (!supported_version(c->version) || !safe_path(c->dir / L"kfc-runtime.dll", c->dir)) {
            last_error = c->name + " runtime has an unsupported/missing version or unsafe DLL path";
            logf(KILN_LOG_WARN, "%s", last_error.c_str());
            continue;
        }
        HMODULE kfc = load_runtime_dll(c->dir / L"kfc-runtime.dll");
        const auto abi = kfc ? reinterpret_cast<KfcAbi>(GetProcAddress(kfc, "KfcRuntimeAbi")) : nullptr;
        uint32_t native_abi = 0;
        if (!abi || !invoke(abi, &native_abi) || !supported_abi(native_abi) ||
            !GetProcAddress(kfc, "KfcRuntimeInitialize") || !GetProcAddress(kfc, "KfcRuntimeTick") ||
            !GetProcAddress(kfc, "KfcRuntimeShutdown") || !GetProcAddress(kfc, "KfcRuntimeStatus")) {
            last_error = c->name + " kfc-runtime.dll is corrupt or incompatible (requires ABI 12)";
            if (kfc) FreeLibrary(kfc);
            logf(KILN_LOG_WARN, "%s", last_error.c_str());
            continue;
        }
        HMODULE h = load_runtime_dll(dll);
        if (!h) {
            last_error = c->name + " shroudforge-runtime.dll could not be loaded (error " + std::to_string(GetLastError()) + ")";
            logf(KILN_LOG_WARN, "%s", last_error.c_str());
            FreeLibrary(kfc);
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
            FreeLibrary(kfc);
            continue;
        }
        g_kfc = kfc;
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
    if (!reserve_writer()) {
        logf(KILN_LOG_ERROR, "ShroudForge cannot reserve the startup data writer (requires kiln-eml v2 in its early phase)");
        return finish("failed", "EML writer is already active or incompatible; refusing a second KFC writer");
    }
    g_tail.start();
    start_helper_processes(g_game, g_server);
    set_status("menu", helper_menu());
    set_status("menuKey", helper_menu_key(g_game));
    const auto t0 = std::chrono::steady_clock::now();
    bool prepared = false;
    invoke(prepare, &prepared, g_game.wstring().c_str());
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
    // For readers of ShroudForge's own record of its startup pass (state.json "assets"): ShroudForge either applied its
    // startup asset mods just now or found the pass of an earlier start still current for this start's mods (it then
    // leaves the record as it is, with that start's time). Either way the record describes the game data of this start.
    set_status("startupPrepared", true);
    // Both DLLs and ABI were validated together before startup preparation.

    if (g_kfc) {
        const auto abi = reinterpret_cast<KfcAbi>(GetProcAddress(g_kfc, "KfcRuntimeAbi"));
        const auto init = reinterpret_cast<KfcBool>(GetProcAddress(g_kfc, "KfcRuntimeInitialize"));
        g_kfc_tick = reinterpret_cast<KfcVoid>(GetProcAddress(g_kfc, "KfcRuntimeTick"));
        g_kfc_shutdown = reinterpret_cast<KfcVoid>(GetProcAddress(g_kfc, "KfcRuntimeShutdown"));
        g_kfc_status = reinterpret_cast<KfcStatusFn>(GetProcAddress(g_kfc, "KfcRuntimeStatus"));
        uint32_t v = 0;
        if (abi) invoke(abi, &v);
        set_status("nativeAbi", v);
        if (abi && init && supported_abi(v)) offer_engine_hooks();
        if (!abi || !init || !g_kfc_tick || !g_kfc_shutdown || !g_kfc_status) {
            logf(KILN_LOG_WARN, "kfc-runtime.dll lacks the provider exports: ShroudForge mods get no live game access");
            g_kfc_tick = g_kfc_shutdown = nullptr;
            g_kfc_status = nullptr;
        } else if (!supported_abi(v) || !invoke(init, &g_initialized) || !g_initialized) {
            g_kfc_tick = nullptr;
            invoke_void(g_kfc_shutdown);
            logf(KILN_LOG_WARN, "ShroudForge's native runtime (ABI %u) did not initialize: %s", v, kfc_status().c_str());
        } else {
            logf(KILN_LOG_INFO, "ShroudForge's native runtime (ABI %u) initialized: %s", v, kfc_status().c_str());
        }
    } else {
        logf(KILN_LOG_WARN, "kfc-runtime.dll could not be loaded (error %lu): ShroudForge mods get no live game access",
             GetLastError());
    }
    g_prepared = true;
    if (!g_early) start_worker(false);
}

void KILN_CALL on_unload(void*) {
    // kiln-core never calls this (kiln_api.h); a test host may. Never wait under a possible loader lock. The pinned worker owns
    // destroy/shutdown serially; retain its event, thread handle and modules until process teardown.
    if (g_stop) SetEvent(g_stop);
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
    if (!api || !info || api->abi_version != KILN_ABI_VERSION ||
        api->size < offsetof(KilnApi, server_world) || info->size < sizeof(KilnPluginInfo) ||
        !api->path || !api->service || !api->log) return KILN_ERR_UNAVAILABLE;
    // Keep callback code mapped while the runtime worker or its own background jobs can still call into it.
    HMODULE pinned = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                           reinterpret_cast<LPCWSTR>(&kiln_plugin_main), &pinned)) return KILN_ERR_UNAVAILABLE;
    if (!ksf::g_api) { ksf::g_api = api; ksf::g_owner = owner; }
    info->id = KilnStr{"shroudforge", 11};
    info->version = KilnStr{"0.1.0", 5};
    info->on_ready = &ksf::on_ready;
    info->on_unload = &ksf::on_unload;
    return KILN_OK;
}

// At process termination Windows has already stopped other threads. Never join or destroy runtimes under loader lock.
BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_DETACH && ksf::g_stop) SetEvent(ksf::g_stop);
    return TRUE;
}

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_early(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    const auto result = kiln_plugin_main(api, owner, info);
    if (result != KILN_OK) return result;
    // The core runs this right after the early phase, before any service DLL: the worker starts here (in safe idle too,
    // where Kiln's plugin step never comes); kiln.ui is published only at that step (on_ready, g_runtime branch).
    info->on_ready = [](void*) { ksf::start_worker(true); };
    info->on_unload = nullptr;
    ksf::g_early = true;
    try { ksf::on_ready(nullptr); }
    catch (...) { ksf::g_early = false; return KILN_ERR_INTERNAL; }
    ksf::g_early = false;
    return KILN_OK;
}
