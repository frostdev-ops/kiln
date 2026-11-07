// SPDX-License-Identifier: MIT
// kiln-flame.dll: a Kiln plugin (<game>/kiln/plugins/) that starts Flame mods, the format of Creative Mode (Nexus 146):
// a folder with mod.json, flame.json {"dll": "<file>"}, and the mod's own data (sigs/, symbols/, settings). The mod
// DLL carries its whole runtime; the contract between a Flame loader and a mod is one export, FlameStartRuntime,
// called with no arguments and no result. This plugin is Kiln's own code, written from that contract (no code of
// Flame's loader is used; docs/kiln/engines/flame.md has the evidence):
//   1. step aside when Flame's own loader runs in this process (it starts the mods in <game>/mods itself);
//   2. find the mods: the active profile's "flame" entries, then <game>/mods/<id>/flame.json (compat.h's rule);
//   3. on one worker thread, as Flame's loader does: LoadLibraryExW(dll, LOAD_WITH_ALTERED_SEARCH_PATH), then
//      FlameStartRuntime() under a fault guard, one mod after the other; a DLL is never unloaded;
//   4. publish kiln.flame (who runs) and write <state>/flame.json.
// Running a Flame mod runs that mod's own code in the game process, as with any native mod.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <set>

#include "../../compat/compat.h"
#include "kiln_flame.h"

#include <psapi.h>

namespace kflame {
namespace {

namespace fs = std::filesystem;
using kcompat::path_utf8;
using kcompat::to_utf8;
using kcompat::to_wide;

constexpr const char* kPluginId = "kiln-flame";
constexpr const char* kPluginVersion = "1.0.0";

kcompat::Host H{nullptr, 0, "flame"};
fs::path g_state;
std::string g_stepped_aside;

struct Mod {
    std::string id, name, version, origin, reason;
    fs::path folder, dll;
    std::atomic<uint32_t> status{KILN_FLAME_PENDING};
    std::atomic<bool> entered{false};  // a failed start may already have installed hooks or launched threads
    double start_ms = 0;
    std::string folder_u8;
};
std::vector<std::unique_ptr<Mod>> g_mods;
std::mutex g_mu;  // mutable mod state and step-aside reason
std::atomic<bool> g_stop{false};

void write_status() {
    using json = nlohmann::json;
    static const char* kStatus[] = {"pending", "started", "failed", "skipped"};
    std::lock_guard lock(g_mu);
    json mods = json::array();
    {
        for (const auto& m : g_mods) {
            const uint32_t st = m->status.load();
            json j = {{"id", m->id},         {"name", m->name},     {"version", m->version},
                      {"origin", m->origin}, {"status", st < 4 ? kStatus[st] : "?"},
                      {"reason", m->reason}, {"folder", path_utf8(m->folder)}, {"startMs", m->start_ms}};
            if (!m->dll.empty()) j["dll"] = path_utf8(m->dll);
            mods.push_back(std::move(j));
        }
    }
    json doc = {{"format", 1},
                {"plugin", kPluginId},
                {"version", kPluginVersion},
                {"side", H.api->side() == KILN_SIDE_SERVER ? "server" : "client"},
                {"steppedAside", !g_stepped_aside.empty()},
                {"steppedAsideReason", g_stepped_aside},
                {"updatedUnixMs", kcompat::unix_ms()},
                {"mods", mods}};
    const auto file = g_state / L"flame.json";
    if (!kcompat::write_file_atomic(file, doc.dump(2) + "\n")) H.logf(KILN_LOG_WARN, "cannot write %s", path_utf8(file).c_str());
}

// ---- kiln.flame -------------------------------------------------------------------------------------------------
uint32_t KILN_CALL f_count(void) {
    std::lock_guard lock(g_mu);
    return static_cast<uint32_t>(g_mods.size());
}
KilnResult KILN_CALL f_get(uint32_t index, KilnFlameMod* out) {
    if (!out || out->size < sizeof(KilnFlameMod)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    if (index >= g_mods.size()) return KILN_ERR_NOT_FOUND;
    const Mod& m = *g_mods[index];
    out->status = m.status.load();
    out->id = kcompat::kstr(m.id);
    out->version = kcompat::kstr(m.version);
    out->folder = kcompat::kstr(m.folder_u8);
    return KILN_OK;
}
bool KILN_CALL f_running(KilnStr id) {
    const std::string want = kcompat::lower(kcompat::sv(id));
    std::lock_guard lock(g_mu);
    for (const auto& m : g_mods) {
        const uint32_t st = m->status.load();
        if (kcompat::lower(m->id) != want) continue;
        if (m->entered.load()) return true;
        if (st == KILN_FLAME_PENDING || st == KILN_FLAME_STARTED) return true;
        if (st == KILN_FLAME_SKIPPED && !g_stepped_aside.empty() && m->origin == "folder" && m->reason.empty()) return true;
    }
    return false;
}
bool KILN_CALL f_stepped_aside(void) { std::lock_guard lock(g_mu); return !g_stepped_aside.empty(); }

const KilnFlameApi kTable = {{sizeof(KilnFlameApi), KILN_FLAME_VERSION}, &f_count, &f_get, &f_running, &f_stepped_aside};

// ---- step aside -------------------------------------------------------------------------------------------------
// Flame's loader is a proxy DLL (dinput8.dll on clients, powrprof.dll on servers per its guide) whose file names the
// entry point it calls, "FlameStartRuntime", without exporting it (a Flame mod exports it). Only modules in the game
// folder or in kiln/chain/ are read (proxies live there), and only from disk.
std::string flame_loader_in_process(const fs::path& game, const fs::path& kiln) {
    HMODULE mods[1024];
    DWORD needed = 0;
    if (!K32EnumProcessModules(GetCurrentProcess(), mods, sizeof mods, &needed)) return {};
    const DWORD n = std::min<DWORD>(needed / sizeof(HMODULE), 1024);
    std::error_code ec;
    for (DWORD i = 0; i < n; ++i) {
        wchar_t p[MAX_PATH] = L"";
        if (!GetModuleFileNameW(mods[i], p, MAX_PATH)) continue;
        const fs::path f(p);
        const fs::path dir = f.parent_path();
        if (!fs::equivalent(dir, game, ec) && !fs::equivalent(dir, kiln / L"chain", ec)) continue;
        if (mods[i] == GetModuleHandleW(nullptr)) continue;
        if (GetProcAddress(mods[i], "FlameStartRuntime")) continue;  // a Flame mod, not a loader
        if (GetProcAddress(mods[i], "kiln_boot") || GetProcAddress(mods[i], "KilnProxyInfo") ||
            GetProcAddress(mods[i], KILN_PLUGIN_MAIN))
            continue;  // Kiln's own modules (this plugin names the entry point too)
        if (kcompat::file_contains(f, "FlameStartRuntime")) return path_utf8(f);
    }
    return {};
}

// ---- find -------------------------------------------------------------------------------------------------------
std::unique_ptr<Mod> read_mod(const fs::path& folder, const std::string& profile_id, bool enabled, const char* origin) {
    auto m = std::make_unique<Mod>();
    m->folder = folder;
    m->folder_u8 = path_utf8(folder);
    m->origin = origin;
    const std::string dir_name = to_utf8(folder.filename().wstring());
    m->id = profile_id.empty() ? dir_name : profile_id;
    std::string text;
    if (kcompat::read_file(folder / L"mod.json", &text)) {
        try {
            const auto j = nlohmann::json::parse(text, nullptr, true, true);
            if (profile_id.empty() && j.contains("id") && j["id"].is_string()) m->id = j["id"].get<std::string>();
            if (j.contains("name") && j["name"].is_string()) m->name = j["name"].get<std::string>();
            if (j.contains("version") && j["version"].is_string()) m->version = j["version"].get<std::string>();
        } catch (...) {
        }
    }
    if (m->name.empty()) m->name = m->id;
    auto skip = [&](std::string why) {
        m->status = KILN_FLAME_SKIPPED;
        m->reason = std::move(why);
        return std::move(m);
    };
    if (!enabled) return skip("switched off in the profile");
    if (!kcompat::contained_path(std::string(origin) == "folder" ? H.path(KILN_PATH_GAME) / L"mods" : folder, folder))
        return skip("mod folder is outside its root or is a symlink");
    if (!kcompat::read_file(folder / L"flame.json", &text)) return skip("no flame.json");
    std::string dll;
    try {
        const auto j = nlohmann::json::parse(text, nullptr, true, true);
        if (j.contains("dll") && j["dll"].is_string()) dll = j["dll"].get<std::string>();
    } catch (const std::exception& e) {
        return skip(std::string("flame.json is not valid JSON: ") + e.what());
    }
    if (dll.empty()) return skip("flame.json names no \"dll\"");
    if (dll.find('\0') != std::string::npos || dll.find("..") != std::string::npos || dll.find(':') != std::string::npos || dll[0] == '\\' || dll[0] == '/')
        return skip("flame.json \"dll\" must stay inside the mod folder (" + dll + ")");
    m->dll = folder / to_wide(dll);
    if (!kcompat::exists(m->dll)) return skip("its DLL is missing (" + path_utf8(m->dll) + ")");
    kcompat::CheckedDll checked;
    if (!checked.open(folder, m->dll, "FlameStartRuntime"))
        return skip(checked.error.find("FlameStartRuntime") != std::string::npos
                        ? "no FlameStartRuntime in " + path_utf8(m->dll)
                        : checked.error + " (" + path_utf8(m->dll) + ")");
    return m;
}

// ---- start ------------------------------------------------------------------------------------------------------
using FlameStartFn = void (*)();

bool guarded_start(FlameStartFn fn, DWORD* code) {
    __try {
        fn();
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *code = GetExceptionCode();
        return false;
    }
}

DWORD WINAPI worker(void*) {
    for (auto& mp : g_mods) {
        Mod& m = *mp;
        if (m.status.load() != KILN_FLAME_PENDING) continue;
        if (g_stop.load()) {
            std::lock_guard lock(g_mu);
            for (auto& pending : g_mods)
                if (pending->status.load() == KILN_FLAME_PENDING) {
                    pending->reason = "plugin stopped before dispatch";
                    pending->status = KILN_FLAME_SKIPPED;
                }
            break;
        }
        // Recheck after on_ready and between starts: a proxy may have arrived on another thread.
        const std::string loader = flame_loader_in_process(H.path(KILN_PATH_GAME), H.path(KILN_PATH_KILN));
        if (!loader.empty()) {
            std::lock_guard lock(g_mu);
            g_stepped_aside = "Flame's own loader (" + loader + ") arrived before dispatch";
            for (auto& pending : g_mods)
                if (pending->status.load() == KILN_FLAME_PENDING) {
                    pending->reason = pending->origin == "folder" ? "" : g_stepped_aside;
                    pending->status = KILN_FLAME_SKIPPED;
                }
            break;
        }
        const auto t0 = std::chrono::steady_clock::now();
        kcompat::CheckedDll checked;
        DWORD code = 0;
        HMODULE h = nullptr;
        if (checked.open(m.folder, m.dll, "FlameStartRuntime")) {
            m.entered = true;
            h = checked.load(&code);
        }
        std::string reason;
        uint32_t status = KILN_FLAME_STARTED;
        if (!h) {
            reason = checked.error.empty() ? "could not load " + path_utf8(m.dll) + " (error " + std::to_string(GetLastError()) + ", fault " + std::to_string(code) + ")" : checked.error;
            status = KILN_FLAME_FAILED;
        } else if (const auto loader = flame_loader_in_process(H.path(KILN_PATH_GAME), H.path(KILN_PATH_KILN)); !loader.empty()) {
            // Loading the mod's dependencies can itself start the real loader. Yield before invoking its runtime.
            std::lock_guard lock(g_mu);
            g_stepped_aside = "Flame's own loader (" + loader + ") arrived while loading a dependency";
            reason = m.origin == "folder" ? "" : g_stepped_aside;
            status = KILN_FLAME_SKIPPED;
        } else if (auto fn = reinterpret_cast<FlameStartFn>(GetProcAddress(h, "FlameStartRuntime")); !fn) {
            reason = "no FlameStartRuntime export after loading";
            status = KILN_FLAME_FAILED;
        } else {
            if (!guarded_start(fn, &code)) {
                char buf[64];
                std::snprintf(buf, sizeof buf, "FlameStartRuntime faulted (0x%08lX)", code);
                reason = buf;
                status = KILN_FLAME_FAILED;
            }
        }
        const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
        {
            std::lock_guard lock(g_mu);
            m.reason = reason;
            m.start_ms = ms;
        }
        m.status = status;
        if (status == KILN_FLAME_STARTED)
            H.mod_log(KILN_LOG_INFO, m.id, m.name + " " + m.version + " (" + m.origin + "): FlameStartRuntime returned after " +
                                               std::to_string(static_cast<int>(ms)) + " ms");
        else
            H.mod_log(KILN_LOG_WARN, m.id, m.name + " " + m.version + ": " + reason);
    }
    size_t started = 0;
    for (auto& m : g_mods) started += m->status.load() == KILN_FLAME_STARTED;
    H.logf(KILN_LOG_INFO, "kiln-flame: %zu of %zu Flame mod(s) started", started, g_mods.size());
    write_status();
    return 0;
}

void KILN_CALL on_ready(void*) {
    const fs::path game = H.path(KILN_PATH_GAME);
    const fs::path kiln = H.path(KILN_PATH_KILN);
    g_state = H.state_dir();
    const kcompat::EnginePlan plan = kcompat::plan_engine(H, "flame");

    // 1. Flame's own loader
    const std::string loader = flame_loader_in_process(game, kiln);
    if (!loader.empty()) {
        std::lock_guard lock(g_mu);
        g_stepped_aside = "Flame's own loader (" + loader + ") runs in this process and starts the mods in <game>/mods";
    }

    // 2. find
    std::set<std::string> seen;
    {
        std::lock_guard lock(g_mu);
        for (const auto& r : plan.listed) {
            auto m = read_mod(r.path, r.id, r.enabled, "profile");
            if (!seen.insert(kcompat::lower(m->id)).second) continue;
            g_mods.push_back(std::move(m));
        }
        if (plan.read_folder) {
            std::vector<fs::path> dirs;
            std::error_code ec;
            for (const auto& d : fs::directory_iterator(game / L"mods", ec))
                if (d.is_directory(ec) && kcompat::exists(d.path() / L"flame.json")) dirs.push_back(d.path());
            std::sort(dirs.begin(), dirs.end());
            for (const auto& d : dirs) {
                auto m = read_mod(d, {}, true, "folder");
                if (!seen.insert(kcompat::lower(m->id)).second) {
                    H.logf(KILN_LOG_INFO, "%s: also in the profile, the profile's copy is used (%s skipped)", m->id.c_str(),
                           path_utf8(d).c_str());
                    continue;
                }
                g_mods.push_back(std::move(m));
            }
        }
        if (!g_stepped_aside.empty()) {
            for (auto& m : g_mods) {
                if (m->status.load() != KILN_FLAME_PENDING) continue;
                m->status = KILN_FLAME_SKIPPED;
                // Flame's loader reads <game>/mods itself: those mods run there (empty reason marks "run by Flame").
                if (m->origin != "folder") m->reason = "Flame's own loader runs in this process; it starts only <game>/mods";
            }
        }
    }
    H.logf(KILN_LOG_INFO, "kiln-flame %s on %s: %s; %zu Flame mod(s)", kPluginVersion,
           H.api->side() == KILN_SIDE_SERVER ? "the server" : "the client", plan.note.c_str(), g_mods.size());
    for (const auto& m : g_mods)
        if (m->status.load() == KILN_FLAME_SKIPPED && !m->reason.empty())
            H.logf(KILN_LOG_INFO, "%s %s: %s", m->name.c_str(), m->version.c_str(), m->reason.c_str());
    if (!g_stepped_aside.empty()) {
        H.logf(KILN_LOG_WARN,
               "kiln-flame steps aside: %s. To start them with Kiln instead (and from Kiln profiles), switch Flame's "
               "loader off: put ';' in front of its line in kiln/chain.ini, then restart",
               g_stepped_aside.c_str());
        write_status();
        return;
    }

    // 3. start, on one worker thread (the way Flame's loader starts its mods: from a thread, not the loader lock)
    bool any = false;
    for (const auto& m : g_mods) any |= m->status.load() == KILN_FLAME_PENDING;
    if (any) {
        if (HANDLE t = CreateThread(nullptr, 0, &worker, nullptr, 0, nullptr)) {
            CloseHandle(t);
        } else {
            for (auto& m : g_mods)
                if (m->status.load() == KILN_FLAME_PENDING) m->status = KILN_FLAME_FAILED, m->reason = "no start thread";
            H.logf(KILN_LOG_ERROR, "kiln-flame: could not create its start thread (error %lu)", GetLastError());
        }
    }
    write_status();
}

void KILN_CALL on_unload(void*) { g_stop = true; write_status(); }

}  // namespace
}  // namespace kflame

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    using namespace kflame;
    if (!api || api->abi_version != KILN_ABI_VERSION || api->size < offsetof(KilnApi, session) + sizeof(void*))
        return KILN_ERR_UNAVAILABLE;
    if (!info || info->size < offsetof(KilnPluginInfo, on_unload) + sizeof(void*) ||
        !kcompat::pin_module(reinterpret_cast<const void*>(&kiln_plugin_main))) return KILN_ERR_UNAVAILABLE;
    H.api = api;
    H.owner = owner;
    info->id = kcompat::kstr(kPluginId);
    info->version = kcompat::kstr(kPluginVersion);
    if (info->size >= offsetof(KilnPluginInfo, on_ready) + sizeof(void*)) info->on_ready = &on_ready;
    info->on_unload = &on_unload;
    // Published here, as kiln_api.h asks; the table answers from the mod list on_ready fills.
    if (api->register_service(owner, kcompat::kstr(KILN_FLAME_SERVICE), &kTable) != KILN_OK)
        H.log(KILN_LOG_WARN, "could not publish kiln.flame (another plugin took the name)");
    return KILN_OK;
}
