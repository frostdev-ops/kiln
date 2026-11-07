// SPDX-License-Identifier: MIT
// kiln-enshwick.dll: a Kiln plugin (<game>/kiln/plugins/) that hosts Enshwick's own loader (enshwick.dll, MIT, built
// from source) in place of Enshwick's powrprof.dll stub, so every Enshwick mod (DLL, Luau, data pack) runs unchanged
// next to Kiln. In on_ready (main thread, before the game's entry point, the moment the stub would start the loader):
//   1. play-once no-mods or an invalid profile: nothing starts;
//   2. step aside when Enshwick's own stub runs in this process; otherwise take the stub's per-process claim, so a
//      stub that starts later stays idle (the stub's own rule for two copies);
//   3. copy the profile's enabled Enshwick mods into <game>/mods/<folder>/ (the only folder the loader reads), with a
//      marker; remove copies the profile no longer enables; never touch folders the player installed;
//   4. pick the loader (the game's own mods/enshwick/enshwick.dll, else the copy Kiln ships in kiln/plugins/enshwick/)
//      and call enshwick_start(2, 0, release) on a thread, as the stub does. On the dedicated server the main thread
//      waits here until the loader has armed its hooks (release) or 10 s pass, as the stub's entry hold does;
//   5. write <state>/enshwick.json and copy the loader's key log lines into kiln.log.
// Docs: docs/kiln/engines/enshwick.md.
#include <windows.h>

#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <mutex>
#include <set>
#include <thread>

#include "../../compat/compat.h"
#include "logic.h"

namespace kenshwick {
namespace {

namespace fs = std::filesystem;
using kcompat::path_utf8;
using kcompat::to_utf8;
using kcompat::to_wide;

constexpr const char* kPluginId = "kiln-enshwick";
constexpr const char* kPluginVersion = "1.0.0";
constexpr const char* kUpstream = "Enshwick v1.0.0-alpha.13";

kcompat::Host H{nullptr, 0, "enshwick"};
uint32_t g_side = KILN_SIDE_SERVER;
fs::path g_game, g_state;

std::mutex g_mu;  // guards the status fields below (the start thread writes them)
std::string g_status = "idle";  // idle, stepped-aside, no-loader, nothing, starting, started, failed
std::string g_reason;
std::string g_loader, g_loader_origin, g_loader_version;
std::string g_plan_note;
std::string g_enshwick_log;
uint32_t g_release_answer = 0;
uint64_t g_start_ms = 0;
struct ModLine {
    std::string folder, form, origin, action, note;
};
std::vector<ModLine> g_mods;

HANDLE g_claim = nullptr;
HANDLE g_release_event = nullptr;
std::atomic<int> g_hold{static_cast<int>(Hold::None)};
std::atomic<bool> g_flow_done{false};    // run_flow ran (early or ready)
std::atomic<bool> g_thread_done{false};  // enshwick_start returned
std::string g_phase;

void Log(int level, const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    const std::string s = kcompat::vformat(fmt, ap);
    va_end(ap);
    H.log(level, s);
}

// --- status file -------------------------------------------------------------------------------------------------

void write_status() {
    using json = nlohmann::json;
    std::lock_guard lock(g_mu);
    json mods = json::array();
    for (const auto& m : g_mods)
        mods.push_back({{"folder", m.folder}, {"form", m.form}, {"origin", m.origin}, {"action", m.action},
                        {"note", m.note}});
    json doc = {{"format", 1},
                {"plugin", kPluginId},
                {"version", kPluginVersion},
                {"upstream", kUpstream},
                {"side", g_side == KILN_SIDE_SERVER ? "server" : "client"},
                {"status", g_status},
                {"reason", g_reason},
                {"steppedAside", g_status == "stepped-aside"},
                {"phase", g_phase},
                {"loader", g_loader},
                {"loaderOrigin", g_loader_origin},
                {"loaderVersion", g_loader_version},
                {"profile", g_plan_note},
                {"release", g_release_answer},
                {"startMs", g_start_ms},
                {"enshwickLog", g_enshwick_log},
                {"updatedUnixMs", kcompat::unix_ms()},
                {"mods", mods}};
    if (g_state.empty()) return;
    std::error_code ec;
    fs::create_directories(g_state, ec);
    const auto file = g_state / L"enshwick.json";
    if (!kcompat::write_file_atomic(file, doc.dump(2) + "\n")) H.logf(KILN_LOG_WARN, "cannot write %s", path_utf8(file).c_str());
}

void set_status(const std::string& status, const std::string& reason) {
    std::lock_guard lock(g_mu);
    g_status = status;
    g_reason = reason;
}

// --- step aside --------------------------------------------------------------------------------------------------

std::wstring claim_name() { return std::wstring(kStubMutexPrefix) + std::to_wstring(GetCurrentProcessId()); }

bool has_stub_marker(const fs::path& file) {
    for (const char* m : kStubMarkers)
        if (kcompat::file_contains(file, m)) return true;
    return false;
}

// A loaded module that exports enshwick_start (Enshwick's loader, whatever its path).
bool loader_module_loaded() {
    HMODULE h = GetModuleHandleW(L"enshwick.dll");
    return h && GetProcAddress(h, kStartExport);
}

StepAsideFacts gather_step_aside(std::string* note) {
    StepAsideFacts f;
    const fs::path pp = g_game / L"powrprof.dll";
    if (kcompat::exists(pp)) {
        const bool enshwick = has_stub_marker(pp);
        const bool loaded = kcompat::module_in_dir(L"powrprof.dll", g_game) != nullptr;
        if (enshwick && loaded) f.stub_loaded = true;
        else if (enshwick)
            *note = "powrprof.dll is Enshwick's stub but did not load (under Wine it needs WINEDLLOVERRIDES=powrprof=n,b): "
                    "Kiln runs Enshwick instead";
        else
            *note = "powrprof.dll belongs to another tool (SET, Shared XP, Creative Mode's server build, Ember...); it "
                    "keeps running, and Kiln runs Enshwick";
    }
    if (HANDLE m = OpenMutexW(SYNCHRONIZE, FALSE, claim_name().c_str())) {
        CloseHandle(m);
        f.stub_claimed = true;
    }
    f.loader_loaded = loader_module_loaded();
    if (!f.stub_loaded && !f.stub_claimed && !f.loader_loaded) {
        g_claim = CreateMutexW(nullptr, FALSE, claim_name().c_str());
        if (!g_claim) f.claim_failed = true;
        else if (GetLastError() == ERROR_ALREADY_EXISTS) {
            CloseHandle(g_claim);
            g_claim = nullptr;
            f.stub_claimed = true;
        }
    }
    return f;
}

// --- profile -> <game>/mods --------------------------------------------------------------------------------------

std::string folder_fingerprint(const fs::path& dir) {
    std::vector<FileStamp> files;
    std::error_code ec;
    for (auto it = fs::recursive_directory_iterator(dir, fs::directory_options::skip_permission_denied, ec);
         !ec && it != fs::recursive_directory_iterator(); it.increment(ec)) {
        if (!it->is_regular_file(ec)) continue;
        FileStamp s;
        s.rel = to_utf8(fs::relative(it->path(), dir, ec).generic_wstring());
        s.size = it->file_size(ec);
        s.time = static_cast<uint64_t>(it->last_write_time(ec).time_since_epoch().count());
        if (s.rel == kManagedMarker) continue;
        files.push_back(std::move(s));
    }
    return fingerprint(std::move(files));
}

ModForm form_of(const fs::path& dir) {
    bool dll = false;
    std::error_code ec;
    for (const auto& e : fs::directory_iterator(dir, ec)) {
        if (!e.is_regular_file(ec) || kcompat::lower(to_utf8(e.path().extension().wstring())) != ".dll") continue;
        if (kcompat::pe_exports_name(e.path(), "enshwick_mod_abi")) dll = true;
    }
    return classify(kcompat::exists(dir / L"pack.toml"), kcompat::exists(dir / L"main.luau"), dll);
}

bool copy_tree(const fs::path& from, const fs::path& to, std::string* why) {
    std::error_code ec;
    fs::remove_all(to, ec);
    fs::create_directories(to, ec);
    if (ec) return *why = "cannot create " + path_utf8(to) + ": " + ec.message(), false;
    fs::copy(from, to, fs::copy_options::recursive | fs::copy_options::overwrite_existing, ec);
    if (ec) return *why = "cannot copy " + path_utf8(from) + ": " + ec.message(), false;
    return true;
}

// Brings <game>/mods in line with the profile's Enshwick list and records what runs in g_mods.
void sync_profile(const kcompat::EnginePlan& plan, const fs::path& mods) {
    std::vector<Wanted> wanted;
    std::set<std::string> listed_folders;
    for (const auto& r : plan.listed) {
        const std::string folder = r.id.empty() ? to_utf8(r.path.filename().wstring()) : r.id;
        std::string why;
        if (!safe_folder_name(folder, &why)) {
            g_mods.push_back({folder, "unknown", "profile", "conflict", "not a usable folder name: " + why});
            continue;
        }
        listed_folders.insert(kcompat::lower(folder));
        if (!r.enabled) {
            g_mods.push_back({folder, "unknown", "profile", "off", "switched off in the profile"});
            continue;
        }
        // The library folder is the source as it is; a profile entry pointing into <game>/mods itself is used in place.
        std::error_code ec;
        if (fs::equivalent(r.path.parent_path(), mods, ec)) {
            g_mods.push_back({folder, form_name(form_of(r.path)), "profile", "in-place", "already in mods/"});
            continue;
        }
        if (!fs::is_directory(r.path, ec)) {
            g_mods.push_back({folder, "unknown", "profile", "missing", "library folder missing: " + path_utf8(r.path)});
            continue;
        }
        wanted.push_back({folder, path_utf8(r.path), folder_fingerprint(r.path)});
    }
    std::vector<Existing> existing;
    std::error_code ec;
    for (const auto& d : fs::directory_iterator(mods, ec)) {
        if (!d.is_directory(ec)) continue;
        Existing e;
        e.folder = to_utf8(d.path().filename().wstring());
        if (kcompat::lower(e.folder) == kLoaderFolder) continue;
        std::string text;
        if (kcompat::read_file(d.path() / kManagedMarker, &text)) e.managed = parse_marker(text, &e.source, &e.fingerprint);
        existing.push_back(std::move(e));
    }
    for (const auto& a : plan_sync(wanted, existing)) {
        const fs::path dst = mods / to_wide(a.folder);
        std::string note = a.note;
        std::string action = act_name(a.act);
        if (a.act == Act::Copy || a.act == Act::Refresh) {
            // Copy into a sibling first and swap, so a failed copy never leaves half a mod in mods/.
            const fs::path tmp = mods / (L"." + to_wide(a.folder) + L".kiln-tmp");
            std::string why;
            if (copy_tree(fs::path(to_wide(a.source)), tmp, &why) &&
                kcompat::write_file_atomic(tmp / kManagedMarker, marker_text(a.source, a.fingerprint))) {
                std::error_code rc;
                fs::remove_all(dst, rc);
                fs::rename(tmp, dst, rc);
                if (rc) action = "failed", note = "cannot move the copy into place: " + rc.message();
            } else {
                action = "failed";
                note = why.empty() ? "cannot write the marker" : why;
            }
            std::error_code rc;
            fs::remove_all(tmp, rc);
        } else if (a.act == Act::Remove) {
            std::error_code rc;
            fs::remove_all(dst, rc);
            if (rc) action = "failed", note = "cannot remove the old copy: " + rc.message();
        }
        const std::string form = a.act == Act::Remove ? "" : form_name(form_of(kcompat::exists(dst) ? dst : fs::path(to_wide(a.source))));
        g_mods.push_back({a.folder, form, a.act == Act::Remove ? "kiln" : "profile", action, note});
    }
    // Folders the player put into mods/ by hand: Enshwick's loader reads every one of them.
    for (const auto& e : existing) {
        if (e.managed || listed_folders.count(kcompat::lower(e.folder))) continue;
        const fs::path dir = mods / to_wide(e.folder);
        const ModForm form = form_of(dir);
        if (form == ModForm::Unknown) continue;  // another engine's folder (Flame, ...): not Enshwick's
        std::string note = plan.read_folder ? "" : "installed by hand: Enshwick's loader runs every folder in mods/, so it runs "
                                                   "although the profile does not list it";
        g_mods.push_back({e.folder, form_name(form), "folder", "run", note});
    }
}

// --- the loader --------------------------------------------------------------------------------------------------

std::string read_version_file(const fs::path& dir) {
    std::string text;
    if (!kcompat::read_file(dir / L"enshwick-version.txt", &text)) return {};
    const auto nl = text.find('\n');
    return text.substr(0, nl);
}

uint32_t __stdcall kiln_release() {
    const Hold state = static_cast<Hold>(g_hold.load());
    if (g_release_event) SetEvent(g_release_event);
    return release_answer(state);
}

using StartFn = void(__stdcall*)(uint32_t stub_abi, uint32_t missing_forwards, uint32_t(__stdcall* release)());

HMODULE load_loader(const fs::path& dll, DWORD* error, DWORD* fault) {
    *error = 0;
    *fault = 0;
    __try {
        // The stub's flags: imports from the DLL's own folder and the system, never the current directory or PATH.
        HMODULE h = LoadLibraryExW(dll.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!h) *error = GetLastError();
        else kcompat::pin_module(h);
        return h;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *fault = GetExceptionCode();
        return nullptr;
    }
}

// The newest logs/enshwick_*.log next to the game, written by the loader (log.rs).
fs::path newest_enshwick_log(fs::file_time_type since) {
    fs::path best;
    fs::file_time_type best_time{};
    std::error_code ec;
    for (const auto& e : fs::directory_iterator(g_game / L"logs", ec)) {
        const std::wstring name = e.path().filename().wstring();
        if (name.rfind(L"enshwick_", 0) != 0 || e.path().extension() != L".log") continue;
        const auto t = e.last_write_time(ec);
        if (ec || t < since) continue;  // a log of an earlier run
        if (best.empty() || t > best_time) best = e.path(), best_time = t;
    }
    return best;
}

// Copies the loader's key lines into kiln.log for the first minute after its start (it writes its log from a thread
// of its own, so the lines arrive after enshwick_start returns).
void tail_log(fs::file_time_type since) {
    fs::path file;
    size_t offset = 0, shown = 0;
    for (int i = 0; i < 120 && shown < 400; ++i) {
        if (file.empty()) {
            file = newest_enshwick_log(since);
            if (!file.empty()) {
                std::lock_guard lock(g_mu);
                g_enshwick_log = path_utf8(file);
            }
        }
        std::string text;
        if (!file.empty() && kcompat::read_file(file, &text))
            for (const auto& line : new_lines(text, &offset))
                if (worth_mirroring(line)) H.mod_log(KILN_LOG_INFO, "log", line), ++shown;
        Sleep(500);
    }
}

void start_thread(fs::path dll) {
    DWORD error = 0, fault = 0;
    const uint64_t began = GetTickCount64();
    const auto since = fs::file_time_type::clock::now() - std::chrono::seconds(2);
    HMODULE h = load_loader(dll, &error, &fault);
    if (!h) {
        set_status("failed", fault ? "loading " + path_utf8(dll) + " faulted (exception 0x" + [&] {
            char b[16];
            std::snprintf(b, sizeof b, "%08lX", fault);
            return std::string(b);
        }() + ")" : "LoadLibrary failed for " + path_utf8(dll) + " (error " + std::to_string(error) + ")");
        Log(KILN_LOG_ERROR, "kiln-enshwick: %s; Enshwick mods stay off, the game runs on", g_reason.c_str());
        kiln_release();
        write_status();
        return;
    }
    auto start = reinterpret_cast<StartFn>(GetProcAddress(h, kStartExport));
    if (!start) {
        set_status("failed", path_utf8(dll) + " has no enshwick_start");
        Log(KILN_LOG_ERROR, "kiln-enshwick: %s; Enshwick mods stay off", g_reason.c_str());
        kiln_release();
        write_status();
        return;
    }
    Log(KILN_LOG_INFO, "kiln-enshwick: enshwick_start(stub_abi %u) on its own thread", kStubAbi);
    // The loader catches its own panics (lib.rs enshwick_start); it calls release once its hooks are armed.
    start(kStubAbi, 0, &kiln_release);
    // A loader that stopped early (unknown build, no mods) never called release: let the server go on.
    g_thread_done.store(true);
    kiln_release();
    {
        std::lock_guard lock(g_mu);
        g_status = "started";
        g_start_ms = GetTickCount64() - began;
    }
    Log(KILN_LOG_INFO, "kiln-enshwick: Enshwick's start returned after %llu ms; its log is <game>\\logs\\enshwick_*.log",
        static_cast<unsigned long long>(g_start_ms));
    write_status();
    tail_log(since);
    write_status();
}

nlohmann::json read_config() {
    std::string text;
    if (!kcompat::read_file(H.config_dir() / L"kiln-enshwick.json", &text)) return nlohmann::json::object();
    try {
        auto cfg = nlohmann::json::parse(text, nullptr, true, true);
        if (cfg.is_object()) return cfg;
    } catch (...) {
    }
    Log(KILN_LOG_WARN, "kiln-enshwick: kiln-enshwick.json does not parse; the defaults apply");
    return nlohmann::json::object();
}

std::string config_string(const nlohmann::json& cfg, const char* key, const char* fallback) {
    return cfg.contains(key) && cfg[key].is_string() ? cfg[key].get<std::string>() : fallback;
}

// On the client in the early phase: until the loader has found its game entries (its log says "entries found"), so
// Kiln's hooks, armed next, never change code the loader is still reading; at most kResolveWaitMs.
constexpr uint32_t kResolveWaitMs = 5000;
void wait_for_resolution(fs::file_time_type since) {
    const uint64_t t0 = GetTickCount64();
    fs::path file;
    while (GetTickCount64() - t0 < kResolveWaitMs && !g_thread_done.load()) {
        if (file.empty()) file = newest_enshwick_log(since);
        std::string text;
        if (!file.empty() && kcompat::read_file(file, &text) &&
            (text.find("entries found") != std::string::npos || text.find("runs vanilla") != std::string::npos))
            break;
        Sleep(10);
    }
    Log(KILN_LOG_INFO, "kiln-enshwick: waited %llu ms for Enshwick to find its game entries",
        static_cast<unsigned long long>(GetTickCount64() - t0));
}

// The whole start, once per process: in the early phase (before Kiln resolves its signatures and arms its hooks, so
// Enshwick reads the game's code as it is on disk) or, with "start": "ready", in on_ready.
void run_flow(const char* phase) {
    if (g_flow_done.exchange(true)) return;
    try {
        g_game = H.path(KILN_PATH_GAME);
        g_state = H.state_dir();
        const kcompat::EnginePlan plan = kcompat::plan_engine(H, "enshwick");
        g_plan_note = plan.note;
        {
            std::lock_guard lock(g_mu);
            g_phase = phase;
        }
        Log(KILN_LOG_INFO, "kiln-enshwick %s (hosts %s) on %s, %s phase: %s", kPluginVersion, kUpstream,
            g_side == KILN_SIDE_SERVER ? "the server" : "the client", phase, plan.note.c_str());

        // 1. nothing to load
        if (plan.mode == KILN_PROFILE_NO_MODS || plan.mode == KILN_PROFILE_INVALID) {
            set_status("nothing", plan.note);
            Log(KILN_LOG_INFO, "kiln-enshwick: %s; Enshwick's loader does not start", plan.note.c_str());
            write_status();
            return;
        }

        // 2. one Enshwick loader per process
        std::string note;
        const std::string aside = step_aside_reason(gather_step_aside(&note));
        if (!note.empty()) Log(KILN_LOG_INFO, "kiln-enshwick: %s", note.c_str());
        if (!aside.empty()) {
            set_status("stepped-aside", aside);
            Log(KILN_LOG_INFO, "kiln-enshwick steps aside: %s", aside.c_str());
            write_status();
            return;
        }

        // 3. the profile's mods into <game>/mods
        const fs::path mods = g_game / L"mods";
        std::error_code ec;
        fs::create_directories(mods, ec);
        sync_profile(plan, mods);
        for (const auto& m : g_mods)
            Log(KILN_LOG_INFO, "kiln-enshwick: mods/%s (%s, %s): %s%s%s", m.folder.c_str(), m.form.c_str(), m.origin.c_str(),
                m.action.c_str(), m.note.empty() ? "" : ": ", m.note.c_str());

        // 4. the loader
        const nlohmann::json cfg = read_config();
        const bool always = cfg.contains("always") && cfg["always"].is_boolean() && cfg["always"].get<bool>();
        size_t running = 0;
        for (const auto& m : g_mods) running += runs(m.action);
        if (!should_start(running, always)) {
            set_status("nothing", "no Enshwick mod in mods/ or the profile");
            Log(KILN_LOG_INFO, "kiln-enshwick: no Enshwick mods; the loader does not start (\"always\": true starts it anyway)");
            write_status();
            return;
        }
        const std::string pref = config_string(cfg, "loader", "auto");
        const fs::path game_dll = mods / L"enshwick" / L"enshwick.dll";
        const fs::path bundled_dir = H.path(KILN_PATH_KILN) / L"plugins" / L"enshwick";
        const fs::path bundled_dll = bundled_dir / L"enshwick.dll";
        const bool game_ok = kcompat::exists(game_dll) && kcompat::pe_exports_name(game_dll, kStartExport);
        const bool bundled_ok = kcompat::exists(bundled_dll) && kcompat::pe_exports_name(bundled_dll, kStartExport);
        const LoaderPick pick = pick_loader(pref, game_ok, bundled_ok);
        if (pick == LoaderPick::None) {
            set_status("no-loader", "no enshwick.dll exporting enshwick_start in mods/enshwick/ or kiln/plugins/enshwick/ (loader: " + pref + ")");
            Log(KILN_LOG_WARN, "kiln-enshwick: %s; Enshwick mods stay off", g_reason.c_str());
            write_status();
            return;
        }
        const fs::path dll = pick == LoaderPick::Game ? game_dll : bundled_dll;
        {
            std::lock_guard lock(g_mu);
            g_loader = path_utf8(dll);
            g_loader_origin = pick == LoaderPick::Game ? "game" : "bundled";
            g_loader_version = pick == LoaderPick::Bundled ? read_version_file(bundled_dir) : "";
            g_status = "starting";
        }
        Log(KILN_LOG_INFO, "kiln-enshwick: loader %s (%s%s%s)", g_loader.c_str(), g_loader_origin.c_str(),
            g_loader_version.empty() ? "" : ", ", g_loader_version.c_str());
        write_status();

        // On the server the main thread waits for release, as the stub's entry hold does; the client never waits (the
        // loader arms after the game's window shows, which needs the entry point to run).
        const bool hold = g_side == KILN_SIDE_SERVER;
        g_release_event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (hold && g_release_event) g_hold.store(static_cast<int>(Hold::Holding));
        const auto since = fs::file_time_type::clock::now() - std::chrono::seconds(2);
        std::thread(start_thread, dll).detach();
        if (!hold && std::string_view(phase) == "early") wait_for_resolution(since);
        if (hold && g_release_event) {
            const uint64_t t0 = GetTickCount64();
            const DWORD w = WaitForSingleObject(g_release_event, kLongestHoldMs);
            if (w == WAIT_TIMEOUT) {
                g_hold.store(static_cast<int>(Hold::Expired));
                {
                    std::lock_guard lock(g_mu);
                    g_release_answer = 2;
                }
                Log(KILN_LOG_WARN, "kiln-enshwick: the loader did not arm within %u ms; the server starts without waiting",
                    kLongestHoldMs);
            } else {
                g_hold.store(static_cast<int>(Hold::None));
                std::lock_guard lock(g_mu);
                g_release_answer = 1;
            }
            Log(KILN_LOG_INFO, "kiln-enshwick: server held %llu ms for Enshwick's hooks",
                static_cast<unsigned long long>(GetTickCount64() - t0));
        }
    } catch (const std::exception& e) {
        set_status("failed", std::string("exception: ") + e.what());
        Log(KILN_LOG_ERROR, "kiln-enshwick: %s; Enshwick mods stay off, the game runs on", g_reason.c_str());
        write_status();
    } catch (...) {
        set_status("failed", "unknown exception");
        Log(KILN_LOG_ERROR, "kiln-enshwick: unknown exception; Enshwick mods stay off, the game runs on");
        write_status();
    }
}

void KILN_CALL on_early_ready(void*) {
    if (lower(config_string(read_config(), "start", "early")) == "ready") {
        Log(KILN_LOG_INFO, "kiln-enshwick: \"start\": \"ready\": Enshwick starts in on_ready, after Kiln's hooks");
        return;
    }
    run_flow("early");
}

void KILN_CALL on_ready(void*) {
    if (g_flow_done.load()) {
        std::string status, phase;
        {
            std::lock_guard lock(g_mu);
            status = g_status;
            phase = g_phase;
        }
        Log(KILN_LOG_INFO, "kiln-enshwick: handled in the %s phase (%s)", phase.c_str(), status.c_str());
        return;
    }
    run_flow("ready");
}

void KILN_CALL on_unload(void*) { write_status(); }

}  // namespace
}  // namespace kenshwick

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    using namespace kenshwick;
    if (!api || api->abi_version != KILN_ABI_VERSION || api->size < offsetof(KilnApi, session) + sizeof(void*))
        return KILN_ERR_UNAVAILABLE;
    // The loader keeps calling kiln_release's address space and runs for the process: never unload.
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

// The early phase (native/core/src/early_plugins.cpp): before sigdb and hooks. The same DLL is loaded again as a
// normal plugin later; its on_ready then only reports.
extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_early(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    using namespace kenshwick;
    if (!api || api->abi_version != KILN_ABI_VERSION || api->size < offsetof(KilnApi, session) + sizeof(void*))
        return KILN_ERR_UNAVAILABLE;
    if (!kcompat::pin_module(reinterpret_cast<const void*>(&kiln_plugin_early))) return KILN_ERR_UNAVAILABLE;
    H.api = api;
    H.owner = owner;
    g_side = api->side();
    info->id = kcompat::kstr(kPluginId);
    info->version = kcompat::kstr(kPluginVersion);
    if (info->size >= offsetof(KilnPluginInfo, on_ready) + sizeof(void*)) info->on_ready = &on_early_ready;
    return KILN_OK;
}
