// SPDX-License-Identifier: MIT
// kiln-flame.dll: a Kiln plugin (<game>/kiln/plugins/) that starts Flame mods, the format of Creative Mode (Nexus 146):
// a folder with mod.json, flame.json {"dll": "<file>"}, and the mod's own data (sigs/, symbols/, settings). The mod
// DLL carries its whole runtime; the contract between a Flame loader and a mod is one export, FlameStartRuntime,
// called with no arguments and no result. This plugin is Kiln's own code, written from that contract (no code of
// Flame's loader is used; docs/kiln/engines/flame.md has the evidence):
//   1. step aside when Flame's own loader runs in this process (it starts the mods in <game>/mods itself);
//   2. find the mods: the active profile's "flame" entries, then <game>/mods/<id>/flame.json (compat.h's rule);
//      say where each one runs from and whether that is <game>\mods\<its id>, the folder a Flame mod reads its own
//      files from (sigs\<build key>.json, symbols\, its settings) whatever folder its DLL was loaded from, and what
//      that folder holds for this game build (the launcher places the profile's mods there; docs/kiln/engines/flame.md);
//   3. on one worker thread, as Flame's loader does: LoadLibraryExW(dll, LOAD_WITH_ALTERED_SEARCH_PATH), then
//      FlameStartRuntime() under a fault guard, one mod after the other; a DLL is never unloaded;
//   4. publish kiln.flame (who runs, and whether its folder has symbols for this build) and write <state>/flame.json.
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
constexpr const char* kPluginVersion = "1.1.0";

kcompat::Host H{nullptr, 0, "flame"};
fs::path g_state;
std::string g_stepped_aside;

struct Mod {
    std::string id, name, version, origin, reason;
    std::string manifest_id;  // mod.json "id" (may differ from a profile entry's Library id)
    std::string state;        // state-file word when it is not the status's ("own-loader", "not-loaded")
    bool own_loader = false;  // a profile entry that Flame's own loader runs from Kiln's copy in <game>/mods
    fs::path folder, dll;
    std::atomic<uint32_t> status{KILN_FLAME_PENDING};
    std::atomic<bool> entered{false};  // a failed start may already have installed hooks or launched threads
    double start_ms = 0;
    std::string folder_u8;
    // <game>\mods\<mod.json id, else the entry's id>: where the mod reads its own files (set by assess)
    std::string reads_u8, note, sigs_u8;
    uint32_t symbols = KILN_FLAME_SYMBOLS_UNKNOWN;
};
std::vector<std::unique_ptr<Mod>> g_mods;
std::mutex g_mu;  // mutable mod state and step-aside reason
std::atomic<bool> g_stop{false};

std::string g_build_key;  // the game exe's "%08X-%X" (TimeDateStamp, SizeOfImage), as a Flame runtime names the build

const char* symbols_word(uint32_t s) {
    return s == KILN_FLAME_SYMBOLS_FOUND ? "found" : s == KILN_FLAME_SYMBOLS_RECIPES ? "recipes" : s == KILN_FLAME_SYMBOLS_NONE ? "none" : "";
}

void write_status() {
    using json = nlohmann::json;
    static const char* kStatus[] = {"pending", "started", "failed", "skipped"};
    std::lock_guard lock(g_mu);
    json mods = json::array();
    {
        for (const auto& m : g_mods) {
            const uint32_t st = m->status.load();
            json j = {{"id", m->id},         {"name", m->name},     {"version", m->version},
                      {"origin", m->origin}, {"status", !m->state.empty() ? m->state.c_str() : st < 4 ? kStatus[st] : "?"},
                      {"reason", m->reason}, {"folder", path_utf8(m->folder)}, {"startMs", m->start_ms}};
            if (!m->dll.empty()) j["dll"] = path_utf8(m->dll);
            j["readsFrom"] = m->reads_u8;
            j["note"] = m->note;
            j["buildKey"] = g_build_key;
            j["symbols"] = symbols_word(m->symbols);
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
        if (kcompat::lower(m->id) != want && kcompat::lower(m->manifest_id) != want) continue;
        if (m->entered.load() || m->own_loader) return true;
        if (st == KILN_FLAME_PENDING || st == KILN_FLAME_STARTED) return true;
        if (st == KILN_FLAME_SKIPPED && !g_stepped_aside.empty() && m->origin == "folder" && m->reason.empty()) return true;
    }
    return false;
}
bool KILN_CALL f_stepped_aside(void) { std::lock_guard lock(g_mu); return !g_stepped_aside.empty(); }
uint32_t KILN_CALL f_symbols(KilnStr id, KilnStr* file) {
    if (file) *file = KilnStr{"", 0};
    const std::string want = kcompat::lower(kcompat::sv(id));
    std::lock_guard lock(g_mu);
    for (const auto& m : g_mods) {
        if (kcompat::lower(m->id) != want && kcompat::lower(m->manifest_id) != want) continue;
        if (file) *file = kcompat::kstr(m->sigs_u8);
        return m->symbols;
    }
    return KILN_FLAME_SYMBOLS_UNKNOWN;
}

const KilnFlameApi kTable = {{sizeof(KilnFlameApi), KILN_FLAME_VERSION}, &f_count, &f_get, &f_running, &f_stepped_aside, &f_symbols};

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
        if (kcompat::kiln_own_module(mods[i], f)) continue;  // Kiln's own modules (this plugin names the entry point too)
        // Wine's builtin under the proxy's name reports the file's path without running it: compare the images.
        if (kcompat::module_is_file(mods[i], f) && kcompat::file_contains(f, "FlameStartRuntime")) return path_utf8(f);
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
            if (j.contains("id") && j["id"].is_string()) m->manifest_id = j["id"].get<std::string>();
            if (profile_id.empty() && !m->manifest_id.empty()) m->id = m->manifest_id;
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

// ---- where a mod reads its own files ---------------------------------------------------------------------------
// Creative Mode 3.2.0, loaded by kiln-flame from the launcher's Library, logged "Mod directory: S:\...\Enshrouded\mods\
// creative", looked for "...\mods\creative\sigs\6AEB144C-2A3F000.json" and "...\mods\creative\symbols", found
// neither and switched its game features off: a Flame runtime takes <game>\mods\<its id> for its folder, not the folder
// of its module. The build key is the game exe's header (its log: "build key 6AEB144C-2A3F000" for 1.0.1.2).
std::string game_build_key() {
    const auto* base = reinterpret_cast<const uint8_t*>(GetModuleHandleW(nullptr));
    if (!base) return {};
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0 || dos->e_lfanew > 4096) return {};
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return {};
    char b[32];
    std::snprintf(b, sizeof b, "%08lX-%lX", static_cast<unsigned long>(nt->FileHeader.TimeDateStamp),
                  static_cast<unsigned long>(nt->OptionalHeader.SizeOfImage));
    return b;
}

// Fills reads_u8, sigs_u8, symbols and note (g_mu held): where the mod reads its own files, whether that is the folder
// it runs from (else whether Kiln's copy is there), and what it holds for this game build. Files only; nothing runs.
void assess(Mod& m, const fs::path& game) {
    const std::string id = !m.manifest_id.empty() ? m.manifest_id : m.id;
    if (!kcompat::safe_component(id)) return;
    const fs::path reads = game / L"mods" / to_wide(id);
    m.reads_u8 = path_utf8(reads);
    std::error_code ec;
    const bool there = fs::is_directory(reads, ec);
    if (!g_build_key.empty() && there) {
        const fs::path sigs = reads / L"sigs" / to_wide(g_build_key + ".json");
        m.sigs_u8 = path_utf8(sigs);
        if (fs::is_regular_file(sigs, ec)) {
            m.symbols = KILN_FLAME_SYMBOLS_FOUND;
        } else {
            m.symbols = KILN_FLAME_SYMBOLS_NONE;
            for (const auto& e : fs::directory_iterator(reads / L"symbols", ec))
                if (e.is_regular_file(ec) && kcompat::lower(to_utf8(e.path().extension().wstring())) == ".json") {
                    m.symbols = KILN_FLAME_SYMBOLS_RECIPES;
                    break;
                }
        }
    } else if (!g_build_key.empty()) {
        m.sigs_u8 = path_utf8(reads / L"sigs" / to_wide(g_build_key + ".json"));
        m.symbols = KILN_FLAME_SYMBOLS_NONE;
    }
    // where it ran from: only for a mod that runs this start (kiln-flame's, or Flame's own loader's)
    const uint32_t st = m.status.load();
    const bool runs = st == KILN_FLAME_PENDING || m.own_loader || (st == KILN_FLAME_SKIPPED && m.reason.empty());
    if (!runs || m.folder.empty() || (there && fs::equivalent(m.folder, reads, ec))) return;
    std::string kind, marker_id;
    const bool kilns = there && kcompat::read_library_marker(reads, &kind, &marker_id) && kind == "flame" &&
                       kcompat::lower(marker_id) == kcompat::lower(m.id);
    if (kilns) return;  // Kiln's copy of this mod is where it reads (a profile naming another copy of it, as copy mode does
                        // for a runtime the launcher cannot tell): it finds its files there
    m.note = "ran from " + m.folder_u8 + ", but a Flame mod reads its own files from " + m.reads_u8 + ", which " +
             (there ? std::string("is not Kiln's copy of it (a folder of yours, or one the mod made there earlier)")
                    : std::string("does not exist")) +
             ": it may not find its files there (Creative Mode: sigs\\<build key>.json, symbols\\) and then switches "
             "its game features off. Activate the profile again in the Kiln launcher: it places the mod in that folder";
}

// One line per mod Kiln (or Flame's loader) runs: where it runs from and reads, and its symbols for this build.
void log_reads(const Mod& m) {
    if (m.reads_u8.empty()) return;
    if (!m.note.empty()) H.mod_log(KILN_LOG_WARN, m.id, m.note);
    const std::string sigs = "sigs\\" + g_build_key + ".json";
    if (m.symbols == KILN_FLAME_SYMBOLS_NONE)
        H.mod_log(KILN_LOG_WARN, m.id, m.reads_u8 + " has no symbol file for this game build (" + sigs + ") and no recipes "
                                       "(symbols\\): by its own rule the mod switches its game features off");
    else if (m.symbols == KILN_FLAME_SYMBOLS_RECIPES)
        H.mod_log(KILN_LOG_INFO, m.id, m.reads_u8 + " has no symbol file for this game build (" + sigs + "); the mod may "
                                       "resolve the build from its recipes (symbols\\)");
    else if (m.symbols == KILN_FLAME_SYMBOLS_FOUND && m.note.empty())
        H.mod_log(KILN_LOG_INFO, m.id, "reads its own files from " + m.reads_u8 + " (" + sigs + " is there for this game build)");
}

// A pending profile entry while kiln-flame stands aside (g_mu held): Flame's loader reads only <game>/mods, so the
// entry runs there only from Kiln's copy (mods/<id>/.kiln-library.json); otherwise it does not load at all.
void mark_aside(Mod& m) {
    m.status = KILN_FLAME_SKIPPED;
    if (m.origin == "folder") {
        m.reason.clear();  // Flame's loader reads <game>/mods itself: those mods run there (empty reason: "run by Flame")
        return;
    }
    const auto rep = kcompat::aside_report(m.id, m.folder, true, "flame", H.path(KILN_PATH_GAME) / L"mods", "Flame", "mods");
    m.state = kcompat::aside_word(rep.state);
    m.reason = rep.reason;
    m.own_loader = rep.state == kcompat::Aside::OwnLoader;
    kcompat::log_aside(H, m.id, rep);
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
                if (pending->status.load() == KILN_FLAME_PENDING) mark_aside(*pending);
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
    g_build_key = game_build_key();
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
            const auto ids = kcompat::listed_ids(plan);
            for (const auto& d : dirs) {
                // Kiln's copy of a profile entry (for Flame's own loader): the profile entry is that mod.
                if (const auto id = kcompat::library_copy_of(d, "flame", ids); !id.empty()) {
                    H.logf(KILN_LOG_INFO, "%s: Kiln's copy of profile mod %s; the profile entry is used", path_utf8(d).c_str(), id.c_str());
                    continue;
                }
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
            for (auto& m : g_mods)
                if (m->status.load() == KILN_FLAME_PENDING) mark_aside(*m);
        }
        for (auto& m : g_mods) assess(*m, game);
    }
    H.logf(KILN_LOG_INFO, "kiln-flame %s on %s: %s; %zu Flame mod(s)", kPluginVersion,
           H.api->side() == KILN_SIDE_SERVER ? "the server" : "the client", plan.note.c_str(), g_mods.size());
    for (const auto& m : g_mods) {
        if (m->status.load() == KILN_FLAME_SKIPPED && !m->reason.empty() && m->state.empty())
            H.logf(KILN_LOG_INFO, "%s %s: %s", m->name.c_str(), m->version.c_str(), m->reason.c_str());
        // the mods that run this start (kiln-flame's, or Flame's own loader's; assess's rule)
        const uint32_t st = m->status.load();
        if (st == KILN_FLAME_PENDING || m->own_loader || (st == KILN_FLAME_SKIPPED && m->reason.empty())) log_reads(*m);
    }
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
