// SPDX-License-Identifier: MIT
// kiln-greeneye.dll: a Kiln plugin (<game>/kiln/plugins/) that loads unmodified Greeneye Loader mods (GreeneyeHost
// API 2, Greeneye Loader 1.2.2) without Greeneye's own dbgcore.dll. In on_ready, after every service is up and before
// the game's entry point (the moment Greeneye Loader starts its mods):
//   1. step aside when Greeneye's own loader runs in this process (it holds its per-process claim), otherwise take
//      that claim, so a copy of the loader that arrives later stays idle, as two copies of Greeneye Loader do;
//   2. find the mods: the active profile's "greeneye" entries, then <game>/greeneye/<id>/ (compat.h's profile rule);
//   3. decide each mod as Greeneye Loader does (mod.json "enabled", "side", "api"), load its DLL and call
//      GreeneyeModStart with a GreeneyeHost whose hooks, leases and reloads are the ported hook owner (hot.cpp);
//   4. write <state>/greeneye.json.
// Docs: docs/kiln/engines/greeneye.md.
#include <windows.h>

#include <algorithm>
#include <cstdarg>
#include <cerrno>
#include <cstdlib>
#include <limits>
#include <cstdio>
#include <map>
#include <mutex>
#include <set>

#include "../../compat/compat.h"
#include "greeneye_internal.h"

namespace kgreeneye {
namespace {

namespace fs = std::filesystem;
using kcompat::path_utf8;
using kcompat::to_utf8;
using kcompat::to_wide;

kcompat::Host H{nullptr, 0, "greeneye"};
uint32_t g_side = KILN_SIDE_SERVER;
std::mutex g_file_mu;
FILE* g_file = nullptr;  // logs/greeneye-loader.log: the loader.log Greeneye's mod authors ask for, in Kiln's logs
std::mutex g_mod_ids_mu;
std::vector<std::string> g_mod_ids;  // ModLog index -> id
std::string g_stepped_aside;
bool g_hot = false;
fs::path g_state;

struct Entry {
    std::string id, name, version, side, origin, status, reason;
    int api = 1;
    fs::path dir, dll;
};
std::vector<Entry> g_entries;

void file_line(const std::string& text) {
    std::lock_guard lock(g_file_mu);
    if (!g_file) return;
    SYSTEMTIME t;
    GetLocalTime(&t);
    std::fprintf(g_file, "[%02d:%02d:%02d.%03d] %s\n", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds, text.c_str());
    std::fflush(g_file);
}

// --- mod.json: the flat fields Greeneye Loader reads, read the way it reads them (a lenient key search, so a file
// it accepts is accepted here too). Ported from Greeneye Loader src/main.cpp (MIT).
const char* FindKey(const std::string& json, const char* key) {
    std::string quoted = std::string("\"") + key + "\"";
    size_t at = json.find(quoted);
    if (at == std::string::npos) return nullptr;
    at = json.find(':', at + quoted.size());
    if (at == std::string::npos) return nullptr;
    const char* p = json.c_str() + at + 1;
    while (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n') ++p;
    return p;
}

std::string JsonString(const std::string& json, const char* key, const std::string& fallback) {
    const char* p = FindKey(json, key);
    if (!p || *p != '"') return fallback;
    std::string out;
    for (++p; *p && *p != '"'; ++p) {
        if (*p == '\\' && p[1]) ++p;
        out += *p;
    }
    return out;
}

bool JsonBool(const std::string& json, const char* key, bool fallback) {
    const char* p = FindKey(json, key);
    if (!p) return fallback;
    if (std::strncmp(p, "true", 4) == 0) return true;
    if (std::strncmp(p, "false", 5) == 0) return false;
    return fallback;
}

int JsonInt(const std::string& json, const char* key, int fallback) {
    const char* p = FindKey(json, key);
    if (!p || (*p != '-' && (*p < '0' || *p > '9'))) return fallback;
    errno = 0;
    const long long value = std::strtoll(p, nullptr, 10);
    if (errno == ERANGE) return *p == '-' ? (std::numeric_limits<int>::min)() : (std::numeric_limits<int>::max)();
    if (value > (std::numeric_limits<int>::max)()) return (std::numeric_limits<int>::max)();
    if (value < (std::numeric_limits<int>::min)()) return (std::numeric_limits<int>::min)();
    return static_cast<int>(value);
}

// Greeneye Loader's own claim: "Local\Greeneye-Loader-<pid>" (greeneye.h ClaimProcess). Held for the process life.
bool claim_loader(bool* already) {
    wchar_t name[96];
    swprintf_s(name, L"Local\\Greeneye-Loader-%lu", GetCurrentProcessId());
    HANDLE claim = CreateMutexW(nullptr, FALSE, name);
    *already = false;
    if (!claim) return false;
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        CloseHandle(claim);
        *already = true;
        return false;
    }
    return true;
}

// Greeneye Loader files in the game folder that did not start (under Wine its proxy names are not loaded): worth a
// line, so the player knows Kiln runs the mods instead.
void note_idle_loaders(const fs::path& game) {
    for (const wchar_t* n : {L"dbgcore.dll", L"umpdc.dll", L"cfgmgr32.dll"}) {
        const fs::path f = game / n;
        if (!kcompat::exists(f) || !kcompat::file_contains(f, "Greeneye")) continue;
        if (kcompat::module_in_dir(n, game))
            Log("%s is Greeneye Loader and is loaded, but did not claim this process: kiln-greeneye loads the mods",
                path_utf8(f).c_str());
        else
            Log("%s is Greeneye Loader; it did not start in this process (as under Wine): kiln-greeneye loads the mods",
                path_utf8(f).c_str());
    }
}

std::wstring with_slash(fs::path p) {
    std::wstring w = p.make_preferred().wstring();
    if (w.empty() || w.back() != L'\\') w += L'\\';
    return w;
}

// Reads one mod folder into an entry (status "candidate", or "disabled"/"skipped"/"rejected" with a reason).
Entry read_mod(const fs::path& dir, const std::string& profile_id, bool profile_enabled, const std::string& origin) {
    Entry e;
    e.dir = dir;
    e.origin = origin;
    std::string json;
    const std::string folder = to_utf8(dir.filename().wstring());
    if (!kcompat::read_file(dir / L"mod.json", &json)) {
        e.id = profile_id.empty() ? folder : profile_id;
        e.name = e.id;
        e.status = "rejected";
        e.reason = "no mod.json in " + path_utf8(dir);
        return e;
    }
    const std::string manifest_id = JsonString(json, "id", folder);
    e.id = profile_id.empty() ? manifest_id : profile_id;
    e.name = JsonString(json, "name", e.id);
    e.version = JsonString(json, "version", "?");
    e.side = JsonString(json, "side", "client");
    e.api = JsonInt(json, "api", 1);
    e.dll = dir / to_wide(JsonString(json, "dll", manifest_id + ".dll"));
    kcompat::CheckedDll checked;
    const char* side_name = g_side == KILN_SIDE_SERVER ? "server" : "client";
    if (!profile_enabled) {
        e.status = "disabled";
        e.reason = "switched off in the profile";
    } else if (!JsonBool(json, "enabled", true)) {
        e.status = "disabled";
        e.reason = "disabled in mod.json";
    } else if (e.side != "both" && e.side != side_name) {
        e.status = "skipped";
        e.reason = e.side + " only";
    } else if (e.api > static_cast<int>(kGreeneyeApiVersion)) {
        e.status = "rejected";
        e.reason = "needs a newer Greeneye Loader (API " + std::to_string(e.api) + ", this is " +
                   std::to_string(kGreeneyeApiVersion) + ")";
    } else if (!kcompat::exists(e.dll)) {
        e.status = "rejected";
        e.reason = "its DLL is missing (" + path_utf8(e.dll) + ")";
    } else if (!checked.open(dir, e.dll, "GreeneyeModStart")) {
        // Greeneye Loader would load the DLL and then find no entry; Kiln checks the file first and never runs it.
        e.status = "rejected";
        e.reason = checked.error.find("GreeneyeModStart") != std::string::npos
                       ? "no GreeneyeModStart in " + path_utf8(e.dll)
                       : "DLL must stay inside the mod folder without symlinks: " + checked.error;
    } else {
        e.status = "candidate";
    }
    return e;
}

void write_status() {
    using json = nlohmann::json;
    json mods = json::array();
    std::map<std::string, hot::ModState> live;
    for (auto& st : hot::Describe()) live[st.id] = st;
    for (const auto& e : g_entries) {
        json j = {{"id", e.id},          {"name", e.name},     {"version", e.version}, {"side", e.side},
                  {"api", e.api},        {"origin", e.origin}, {"status", e.status},   {"reason", e.reason},
                  {"folder", path_utf8(e.dir)}};
        if (!e.dll.empty()) j["dll"] = path_utf8(e.dll);
        if (auto it = live.find(e.id); it != live.end()) {
            j["generation"] = it->second.generation;
            j["generations"] = it->second.generations;
            j["hooks"] = it->second.hooks;
            j["hotReloadApi"] = it->second.hotApi;
        }
        mods.push_back(std::move(j));
    }
    json doc = {{"format", 1},
                {"plugin", kPluginId},
                {"version", kPluginVersion},
                {"greeneyeApi", kGreeneyeApiVersion},
                {"greeneyeLoader", kUpstreamVersion},
                {"side", g_side == KILN_SIDE_SERVER ? "server" : "client"},
                {"steppedAside", !g_stepped_aside.empty()},
                {"steppedAsideReason", g_stepped_aside},
                {"hotReload", g_hot},
                {"updatedUnixMs", kcompat::unix_ms()},
                {"mods", mods}};
    const auto file = g_state / L"greeneye.json";
    if (!kcompat::write_file_atomic(file, doc.dump(2) + "\n")) Log("cannot write %s", path_utf8(file).c_str());
}

void KILN_CALL on_client_pre(const KilnTickInfo*, void*) { hot::PollFromKiln(); }

void KILN_CALL on_ready(void*) {
    const fs::path game = H.path(KILN_PATH_GAME);
    const fs::path folder = game / L"greeneye";
    g_state = H.state_dir();
    {
        std::error_code ec;
        fs::create_directories(H.path(KILN_PATH_LOGS), ec);
    }

    // 1. one loader per process
    bool already = false;
    if (!claim_loader(&already)) {
        g_stepped_aside = already ? "Greeneye Loader runs in this process (it holds its claim) and starts the Greeneye mods"
                                  : "could not take Greeneye Loader's per-process claim";
        Log("kiln-greeneye steps aside: %s", g_stepped_aside.c_str());
        // The profile's Greeneye mods live in Kiln's Library, which Greeneye Loader never reads: say for each whether
        // it runs there (Kiln copied it into greeneye/<id>/) or not at all.
        const kcompat::EnginePlan plan = kcompat::plan_engine(H, "greeneye");
        std::set<std::string> seen;
        for (const auto& r : plan.listed) {
            Entry e = read_mod(r.path, r.id, r.enabled, "profile");
            if (!seen.insert(kcompat::lower(e.id)).second) continue;
            if (e.status == "candidate") {
                const auto rep = kcompat::aside_report(r.id, r.path, r.enabled, "greeneye", folder, "Greeneye", "greeneye");
                e.status = kcompat::aside_word(rep.state);
                e.reason = rep.reason;
                if (!rep.copy.empty() && rep.copy != e.dir) {
                    if (!e.dll.empty()) e.dll = rep.copy / e.dll.lexically_relative(e.dir);
                    e.dir = rep.copy;
                }
                kcompat::log_aside(H, e.id, rep);
            }
            g_entries.push_back(std::move(e));
        }
        write_status();
        return;
    }
    note_idle_loaders(game);

    // 2. find
    const kcompat::EnginePlan plan = kcompat::plan_engine(H, "greeneye");
    std::set<std::string> seen;
    for (const auto& r : plan.listed) {
        Entry e = read_mod(r.path, r.id, r.enabled, "profile");
        if (!seen.insert(kcompat::lower(e.id)).second) continue;
        g_entries.push_back(std::move(e));
    }
    if (plan.read_folder && kcompat::exists(folder)) {
        std::vector<fs::path> dirs;
        std::error_code ec;
        for (const auto& d : fs::directory_iterator(folder, ec))
            if (d.is_directory(ec) && d.path().filename().wstring()[0] != L'.' &&
                kcompat::contained_path(folder, d.path())) dirs.push_back(d.path());
        std::sort(dirs.begin(), dirs.end());  // Greeneye Loader takes FindFirstFile order: by name on NTFS
        const auto ids = kcompat::listed_ids(plan);
        for (const auto& d : dirs) {
            if (!kcompat::exists(d / L"mod.json")) continue;
            // Kiln's copy of a profile entry (for Greeneye Loader's own use): the profile entry is that mod.
            if (const auto id = kcompat::library_copy_of(d, "greeneye", ids); !id.empty()) {
                Log("%s: Kiln's copy of profile mod %s; the profile entry is used", path_utf8(d).c_str(), id.c_str());
                continue;
            }
            Entry e = read_mod(d, {}, true, "folder");
            if (!seen.insert(kcompat::lower(e.id)).second) {
                Log("%s: also in the profile, the profile's copy is used (%s skipped)", e.id.c_str(), path_utf8(d).c_str());
                continue;
            }
            g_entries.push_back(std::move(e));
        }
    }

    if (g_entries.empty()) {
        Log("kiln-greeneye %s: no Greeneye mods (%s)", kPluginVersion, plan.note.c_str());
        write_status();
        return;
    }

    // 3. start
    {
        std::lock_guard lock(g_file_mu);
        g_file = _wfsopen((H.path(KILN_PATH_LOGS) / L"greeneye-loader.log").c_str(), L"w", _SH_DENYWR);
    }
    g_hot = kcompat::exists(folder / L"hotreload.enable");
    Log("kiln-greeneye %s (GreeneyeHost API %u, as Greeneye Loader %s) on %s: %s; %zu mod(s)%s", kPluginVersion,
        kGreeneyeApiVersion, kUpstreamVersion, g_side == KILN_SIDE_SERVER ? "the server" : "the client",
        plan.note.c_str(), g_entries.size(), g_hot ? "; hot reload on (greeneye\\hotreload.enable)" : "");
    hot::Init(g_side == KILN_SIDE_SERVER ? kGreeneyeServer : kGreeneyeClient, with_slash(game), g_hot);
    size_t started = 0;
    for (auto& e : g_entries) {
        if (e.status != "candidate") {
            Log("%s %s: %s", e.name.c_str(), e.version.c_str(), e.reason.c_str());
            continue;
        }
        hot::ModInfo m;
        m.id = e.id;
        m.name = e.name;
        m.version = e.version;
        m.dir = with_slash(e.dir);
        m.dll = e.dll.wstring();
        {
            std::lock_guard lock(g_mod_ids_mu);
            m.index = g_mod_ids.size();
            g_mod_ids.push_back(e.id);
        }
        bool ok = false;
        const std::string line = hot::StartMod(m, &ok);
        e.status = ok ? "started" : "failed";
        if (!ok) e.reason = line;
        started += ok;
        Log("%s %s (%s): %s", e.name.c_str(), e.version.c_str(), e.origin.c_str(), line.c_str());
    }
    hot::InstallSafePoint();
    hot::StartWatcher();
    if (g_hot && g_side == KILN_SIDE_CLIENT) {
        KilnHandle h = 0;
        if (H.api->subscribe_world_tick(H.owner, KILN_WORLD_CLIENT, KILN_TICK_PRE, &on_client_pre, nullptr, &h) == KILN_OK)
            Log("hot reload: also a safe point at each client-world step");
    }
    Log("kiln-greeneye: %zu of %zu Greeneye mod(s) started", started, g_entries.size());
    write_status();
}

void KILN_CALL on_unload(void*) {
    hot::Stop();
    write_status();
    std::lock_guard lock(g_file_mu);
    if (g_file) std::fclose(g_file), g_file = nullptr;
}

}  // namespace

void Log(const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    const std::string s = kcompat::vformat(fmt, ap);
    va_end(ap);
    H.log(KILN_LOG_INFO, s);
    file_line(s);
}

void ModLog(size_t index, const char* line) {
    std::string id;
    {
        std::lock_guard lock(g_mod_ids_mu);
        id = index < g_mod_ids.size() ? g_mod_ids[index] : std::string("?");
    }
    H.mod_log(KILN_LOG_INFO, id, line);
    file_line("  " + id + ": " + line);
}

std::string Narrow(const std::wstring& w) { return to_utf8(w); }
std::wstring Wide(const std::string& s) { return to_wide(s); }

}  // namespace kgreeneye

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    using namespace kgreeneye;
    if (!api || api->abi_version != KILN_ABI_VERSION || api->size < offsetof(KilnApi, session) + sizeof(void*))
        return KILN_ERR_UNAVAILABLE;
    if (!kcompat::pin_module(reinterpret_cast<const void*>(&kiln_plugin_main))) return KILN_ERR_UNAVAILABLE;
    H.api = api;
    H.owner = owner;
    g_side = api->side();
    info->id = kcompat::kstr(kPluginId);
    info->version = kcompat::kstr(kPluginVersion);
    if (info->size >= offsetof(KilnPluginInfo, on_ready) + sizeof(void*)) info->on_ready = &on_ready;
    info->on_unload = &on_unload;
    return KILN_OK;
}

// Conformance hooks for native/plugins/compat/tests (the host calls them through GetProcAddress): Greeneye's find and
// read as a mod sees them.
extern "C" __declspec(dllexport) uint8_t* kiln_greeneye_test_find(const char* pattern) { return hot::FindForTest(pattern); }
extern "C" __declspec(dllexport) void kiln_greeneye_test_read(const void* p, void* out, size_t n) { hot::ReadForTest(p, out, n); }
