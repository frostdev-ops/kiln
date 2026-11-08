// SPDX-License-Identifier: MIT
// kiln-shroudtopia.dll: a Kiln plugin (<game>/kiln/plugins/) that loads unmodified Shroudtopia mods (Shroudtopia 0.1.1
// and its 1.0 compatibility release; C++ mods exporting CreateModInstance) without Shroudtopia's winmm.dll. It runs
// them the way Shroudtopia's loader does (shroudtopia/dllmain.cpp, mod_loader.h upstream):
//   - one loader thread: wait "bootDelay" ms, then every "updateDelay" ms: reload shroudtopia.json when it changed;
//     while "active", look for new mod DLLs; Load each new mod that supports this side; Activate / Update /
//     Deactivate each loaded mod by "mods.<mod name>.active";
//   - mods: the active profile's "shroudtopia" entries (a mod folder or a DLL), then <game>/mods/<name>/ folders with
//     a mod.json and <game>/mods/*.dll (compat.h's profile rule). Unlike Shroudtopia, a DLL is loaded only when its
//     file exports CreateModInstance, so other loaders' DLLs in mods/ (Flame, EML packages) are never run by it;
//   - each call into a mod runs under a fault guard: a mod that faults is switched off, the game runs on.
// Steps aside when Shroudtopia's own loader (shroudtopia.dll, or a Shroudtopia/Shroudweave winmm.dll) runs in this
// process. Settings: <configDir>/shroudtopia/shroudtopia.json. Docs: docs/kiln/engines/shroudtopia.md.
#include <algorithm>
#include <map>
#include <set>
#include <cwctype>

#include "shroudtopia_internal.h"
#include "../../compat/module_watch.h"

#include <psapi.h>

namespace kshroud {

kcompat::Host H{nullptr, 0, "shroudtopia"};

namespace {

namespace fs = std::filesystem;
using json = nlohmann::json;
using kcompat::path_utf8;
using kcompat::to_utf8;
using kcompat::to_wide;
using CreateModFn = Mod* (*)();

struct Loaded {
    fs::path dll;
    std::string origin;  // "profile" or "folder"
    std::string name;    // GetMetaData().name: the key in shroudtopia.json "mods"
    std::string id;      // mod.json id (or file stem)
    std::string version, author, target;
    bool client = false, server = false;
    Mod* mod = nullptr;
    bool loaded = false, active = false;  // snapshots read only under the foreign-code guard
    std::string status = "registered";  // registered, loaded, active, inactive, skipped, faulted
    std::string reason;
    uint64_t updates = 0;
};

ModContext* g_ctx = nullptr;
std::mutex g_mu;  // g_mods, g_seen (the loop thread writes, write_status reads)
std::vector<std::unique_ptr<Loaded>> g_mods;
std::set<std::wstring> g_seen;           // DLL paths already handled (loaded, or refused for this file stamp)
std::map<std::wstring, std::string> g_refused;  // path -> reason, for the status file
std::string g_stepped_aside;
kcompat::EnginePlan g_plan;
fs::path g_game, g_state;
bool g_server = false;
std::atomic<uint64_t> g_cycles{0};
HANDLE g_stop = nullptr, g_worker = nullptr;
std::atomic<bool> g_unloading{false};
bool g_foreign_started = false;
bool g_handoff = false;

std::string own_loader_in_process();
// The worker observes the loader every wait slice (up to 20 Hz); the full check enumerates every module, stats paths
// and reads DLL files, wineserver round trips under Wine. Re-check only after a DLL load or a module-list change.
kcompat::ModuleWatch g_loader_watch;
bool observe_own_loader() {
    if (!g_loader_watch.due()) return false;
    const auto reason = own_loader_in_process();
    if (reason.empty()) return false;
    {
        std::lock_guard lock(g_mu);
        g_stepped_aside = reason;
    }
    g_handoff = true;
    loader_log(KILN_LOG_WARN, "kiln-shroudtopia steps aside: " + reason +
        (g_foreign_started ? "; stopping scheduling; already-installed mod hooks remain mapped" : "; no mod was started"));
    return true;
}

void write_status() {
    json mods = json::array();
    json refused = json::array();
    std::string stepped_aside;
    {
        std::lock_guard lock(g_mu);
        stepped_aside = g_stepped_aside;
        for (const auto& m : g_mods)
            mods.push_back({{"name", m->name},     {"id", m->id},         {"version", m->version}, {"author", m->author},
                            {"target", m->target}, {"client", m->client}, {"server", m->server},   {"origin", m->origin},
                            {"status", m->status}, {"reason", m->reason}, {"updates", m->updates}, {"dll", path_utf8(m->dll)}});
        for (const auto& [p, why] : g_refused) refused.push_back({{"dll", to_utf8(p)}, {"reason", why}});
    }
    json doc = {{"format", 1},
                {"plugin", kPluginId},
                {"version", kPluginVersion},
                {"shroudtopia", kShroudtopiaVersion},
                {"side", g_server ? "server" : "client"},
                {"steppedAside", !stepped_aside.empty()},
                {"steppedAsideReason", stepped_aside},
                {"settings", path_utf8(config::file())},
                {"cycles", g_cycles.load()},
                {"updatedUnixMs", kcompat::unix_ms()},
                {"mods", mods},
                {"refused", refused}};
    const auto file = g_state / L"shroudtopia.json";
    if (!kcompat::write_file_atomic(file, doc.dump(2) + "\n")) H.logf(KILN_LOG_WARN, "cannot write %s", path_utf8(file).c_str());
}

// ---- fault guards around every call into a mod ----
template <class F>
bool guarded(F&& f, DWORD* code);

struct Thunk {
    void (*fn)(void*);
    void* arg;
};
bool seh_run(const Thunk* t, DWORD* code) {
    __try {
        t->fn(t->arg);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *code = GetExceptionCode();
        return false;
    }
}
template <class F>
bool guarded(F&& f, DWORD* code) {
    // C++ exceptions thrown by a mod are caught here; hardware faults by seh_run.
    using Fn = std::remove_reference_t<F>;
    struct Box {
        Fn* f;
        bool threw = false;
    } box{&f};
    Thunk t{[](void* p) {
                auto* b = static_cast<Box*>(p);
                try {
                    (*b->f)();
                } catch (...) {
                    b->threw = true;
                }
            },
            &box};
    const bool ok = seh_run(&t, code);
    if (ok && box.threw) {
        *code = 0xE06D7363;  // MSVC C++ exception code
        return false;
    }
    return ok;
}

std::string fault_text(DWORD code) {
    char buf[64];
    std::snprintf(buf, sizeof buf, "0x%08lX", code);
    return buf;
}

// ---- finding mod DLLs ----
// The DLL of an EML-style mod folder (mods/<name>/mod.json): <id>.dll, else the first DLL that exports
// CreateModInstance. Folders of other loaders (flame.json, src/mod.lua) are not Shroudtopia's.
struct Candidate {
    fs::path dll;
    fs::path root;
    std::string id;
    std::string origin;
};

bool folder_dll(const fs::path& dir, const std::string& id_hint, Candidate* out, std::string* why) {
    std::string text;
    json manifest = json::object();
    std::string id = id_hint.empty() ? to_utf8(dir.filename().wstring()) : id_hint;
    if (kcompat::read_file(dir / L"mod.json", &text)) {
        try {
            manifest = json::parse(text);
            if (manifest.contains("id") && manifest["id"].is_string()) id = manifest["id"].get<std::string>();
        } catch (...) {
        }
    }
    const fs::path named = dir / to_wide(id + ".dll");
    if (id.empty() || id == "." || id == ".." || id.find_first_of("/\\:") != std::string::npos ||
        id.find('\0') != std::string::npos) {
        if (why) *why = "mod id names a DLL outside its folder";
        return false;
    }
    fs::path dll;
    auto is_mod_dll = [&](const fs::path& f) {
        kcompat::CheckedDll checked;
        return checked.open(dir, f, "CreateModInstance");
    };
    if (kcompat::exists(named) && is_mod_dll(named)) {
        dll = named;
    } else {
        std::vector<fs::path> files;
        std::error_code ec;
        for (const auto& e : fs::directory_iterator(dir, ec))
            if (kcompat::lower(to_utf8(e.path().extension().wstring())) == ".dll") files.push_back(e.path());
        std::sort(files.begin(), files.end());
        for (const auto& f : files)
            if (is_mod_dll(f)) {
                dll = f;
                break;
            }
    }
    if (dll.empty()) {
        if (why) *why = "no DLL exporting CreateModInstance in " + path_utf8(dir);
        return false;
    }
    config::apply_manifest_defaults(id, manifest);
    out->dll = dll;
    out->root = dir;
    out->id = id;
    return true;
}

std::vector<Candidate> scan() {
    std::vector<Candidate> out;
    std::set<std::string> profile_ids;
    for (const auto& r : g_plan.listed) profile_ids.insert(kcompat::lower(r.id));
    auto add_dll_file = [&](const fs::path& f, const fs::path& root, const std::string& id, const char* origin) {
        Candidate c{f, root, id.empty() ? to_utf8(f.stem().wstring()) : id, origin};
        if (std::string_view(origin) == "folder" && profile_ids.count(kcompat::lower(c.id))) return;
        out.push_back(std::move(c));
    };
    for (const auto& r : g_plan.listed) {
        std::error_code ec;
        if (!r.enabled) continue;  // listed but switched off in the profile
        if (fs::is_directory(r.path, ec)) {
            Candidate c;
            std::string why;
            if (folder_dll(r.path, r.id, &c, &why)) {
                c.origin = "profile";
                if (!r.id.empty()) c.id = r.id;  // profile identity does not rename the manifest's DLL/config key
                profile_ids.insert(kcompat::lower(c.id));
                out.push_back(std::move(c));
            } else {
                std::lock_guard lock(g_mu);
                if (!g_refused.count(r.path.wstring())) {
                    g_refused[r.path.wstring()] = why;
                    H.logf(KILN_LOG_WARN, "profile entry %s: %s", r.id.c_str(), why.c_str());
                }
            }
        } else {
            add_dll_file(r.path, r.path.parent_path(), r.id, "profile");
        }
    }
    if (!g_plan.read_folder) return out;
    const fs::path mods = g_game / L"mods";
    std::vector<fs::path> dirs, files;
    std::error_code ec;
    for (const auto& e : fs::directory_iterator(mods, ec)) {
        if (e.is_directory(ec)) dirs.push_back(e.path());
        else if (kcompat::lower(to_utf8(e.path().extension().wstring())) == ".dll") files.push_back(e.path());
    }
    std::sort(dirs.begin(), dirs.end());
    std::sort(files.begin(), files.end());
    for (const auto& d : dirs) {
        if (!kcompat::contained_path(mods, d)) continue;
        if (!kcompat::exists(d / L"mod.json")) continue;
        if (kcompat::exists(d / L"flame.json") || kcompat::exists(d / L"src" / L"mod.lua")) continue;  // other loaders
        Candidate c;
        if (folder_dll(d, {}, &c, nullptr)) {
            if (profile_ids.count(kcompat::lower(c.id))) continue;
            c.origin = "folder";
            out.push_back(std::move(c));
        }
    }
    for (const auto& f : files) add_dll_file(f, mods, {}, "folder");
    return out;
}

// Loads and registers new DLLs (Shroudtopia's CheckAndLoadDlls + LoadAndRegisterMod).
void check_and_load() {
    for (const auto& c : scan()) {
        std::wstring key = c.dll.lexically_normal().wstring();
        std::transform(key.begin(), key.end(), key.begin(), [](wchar_t ch) { return static_cast<wchar_t>(std::towlower(ch)); });
        {
            std::lock_guard lock(g_mu);
            if (g_seen.count(key)) continue;
        }
        kcompat::CheckedDll checked;
        if (!checked.open(c.root, c.dll, "CreateModInstance")) {
            std::lock_guard lock(g_mu);
            g_refused[key] = checked.error + " (the DLL was not loaded)";
            continue;
        }
        DWORD code = 0;
        if (WaitForSingleObject(g_stop, 0) == WAIT_OBJECT_0 || observe_own_loader()) return;
        g_foreign_started = true;
        HMODULE h = checked.load(&code);
        if (!h) {
            const DWORD err = GetLastError();
            loader_log(KILN_LOG_ERROR, "Failed to load DLL: " + path_utf8(c.dll) + " (error " + std::to_string(err) + ")");
            std::lock_guard lock(g_mu);
            if (code) {
                g_seen.insert(key);  // do not repeatedly enter a faulting DllMain
                g_refused[key] = "LoadLibraryExW faulted (" + fault_text(code) + ")";
            }
            continue;
        }
        {
            std::lock_guard lock(g_mu);
            g_seen.insert(key);
            g_refused.erase(key);
        }
        if (observe_own_loader()) return;  // the DLL's dependencies may have started the real loader
        auto create = reinterpret_cast<CreateModFn>(GetProcAddress(h, "CreateModInstance"));
        auto m = std::make_unique<Loaded>();
        m->dll = c.dll;
        m->origin = c.origin;
        m->id = c.id;
        Mod* inst = nullptr;
        ModMetaData meta{};
        const bool ok = create && guarded([&] {
            inst = create();
            if (inst) meta = inst->GetMetaData();
        }, &code);
        if (!ok || !inst) {
            const std::string why = !create ? "CreateModInstance not found" : !ok ? "CreateModInstance faulted (" + fault_text(code) + ")" : "Failed to create mod instance";
            loader_log(KILN_LOG_ERROR, why + " for: " + path_utf8(c.dll));
            std::lock_guard lock(g_mu);
            g_refused[key] = why;
            continue;  // never FreeLibrary: its DllMain may have started something
        }
        m->mod = inst;
        m->name = meta.name;
        m->version = meta.version;
        m->author = meta.author;
        m->target = meta.targetShroudtopiaVersion;
        m->client = meta.hasClientSupport;
        m->server = meta.hasServerSupport;
        if (!m->target.empty() && m->target.rfind("0.", 0) != 0)
            H.logf(KILN_LOG_WARN, "%s targets Shroudtopia %s; this is %s", m->name.c_str(), m->target.c_str(), kShroudtopiaVersion);
        loader_log(KILN_LOG_INFO, "Registered mod: " + m->name + " " + m->version + " by " + m->author + " (" + m->origin + ", " +
                                      path_utf8(c.dll) + ")");
        g_ctx->registeredMods[path_utf8(c.dll)] = inst;  // as Shroudtopia: keyed by the DLL path (only this thread writes)
        std::lock_guard lock(g_mu);
        g_mods.push_back(std::move(m));
    }
}

void drive(Loaded& m, const char* what, std::function<void()> call) {
    set_current_mod(m.name);
    DWORD code = 0;
    const bool ok = guarded([&] {
        call();
        m.loaded = m.mod->loaded;
        m.active = m.mod->active;
    }, &code);
    set_current_mod({});
    if (!ok) {
        m.status = "faulted";
        m.reason = std::string(what) + " faulted (" + fault_text(code) + "); the mod is switched off until the next start";
        H.logf(KILN_LOG_ERROR, "%s: %s", m.name.c_str(), m.reason.c_str());
    }
}

// Shroudtopia's UpdateMods.
void update_mods() {
    // Status readers must not race strings/counters written by the worker. Mods receive only the API context,
    // so none of their callbacks require this loader bookkeeping mutex.
    std::lock_guard lock(g_mu);
    std::vector<Loaded*> mods;
    for (auto& m : g_mods) mods.push_back(m.get());
    for (Loaded* m : mods) {
        if (m->status == "faulted" || m->status == "skipped") continue;
        if (!m->loaded) {
            if ((g_server && !m->server) || (!g_server && !m->client)) {
                m->status = "skipped";
                m->reason = g_server ? "no server support (hasServerSupport false)" : "no client support (hasClientSupport false)";
                loader_log(KILN_LOG_INFO, m->name + ": " + m->reason);
                continue;
            }
            loader_log(KILN_LOG_INFO, "Loading mod: " + m->name);
            drive(*m, "Load", [&] { m->mod->Load(g_ctx); m->mod->loaded = true; });
            if (m->status == "faulted") continue;
            g_ctx->loadedMods.insert(m->mod);
            m->status = "loaded";
        }
    }
    for (Loaded* m : mods) {
        if (m->status == "faulted" || m->status == "skipped" || !m->loaded) continue;
        set_current_mod(m->name);
        const json v = config::mod_get(m->name.c_str(), "active");
        const bool should = v.is_boolean() && v.get<bool>();
        if (should) {
            if (!m->active) {
                loader_log(KILN_LOG_INFO, "Activating: " + m->name);
                drive(*m, "Activate", [&] { m->mod->Activate(g_ctx); });
                if (m->status == "faulted") continue;
            }
            drive(*m, "Update", [&] { m->mod->Update(g_ctx); });
            if (m->status == "faulted") continue;
            ++m->updates;
        } else if (m->active) {
            loader_log(KILN_LOG_INFO, "Deactivating: " + m->name);
            drive(*m, "Deactivate", [&] { m->mod->Deactivate(g_ctx); });
            if (m->status == "faulted") continue;
        }
        m->status = m->active ? "active" : "inactive";
        set_current_mod({});
    }
}

int json_int(const char* key, int fallback) {
    const json v = config::get(key);
    try {
        return v.is_null() ? fallback : v.get<int>();
    } catch (...) {
        return fallback;
    }
}

bool json_bool(const char* key, bool fallback) {
    const json v = config::get(key);
    return v.is_boolean() ? v.get<bool>() : fallback;
}

void loop() {
    loader_log(KILN_LOG_INFO, "Thread started.");
    const int boot = json_int("bootDelay", 1000);
    loader_log(KILN_LOG_INFO, "Boot delay set to: " + std::to_string(boot) + " ms");
    auto wait = [](uint64_t delay) {
        const auto start = GetTickCount64();
        for (;;) {
            if (observe_own_loader() || WaitForSingleObject(g_stop, 0) == WAIT_OBJECT_0) return false;
            const auto elapsed = GetTickCount64() - start;
            if (elapsed >= delay) return true;
            if (WaitForSingleObject(g_stop, static_cast<DWORD>(std::min<uint64_t>(50, delay - elapsed))) == WAIT_OBJECT_0)
                return false;
        }
    };
    if (!wait(static_cast<uint64_t>(std::max(0, boot)))) return;
    uint64_t last_status = 0;
    for (;;) {
        config::reload_if_changed();
        if (WaitForSingleObject(g_stop, 0) == WAIT_OBJECT_0 || observe_own_loader()) break;
        if (json_bool("active", false)) {
            check_and_load();
            if (g_handoff || WaitForSingleObject(g_stop, 0) == WAIT_OBJECT_0) break;
            update_mods();
        }
        ++g_cycles;
        const uint64_t now = GetTickCount64();
        if (now - last_status > 5000) {
            last_status = now;
            write_status();
        }
        if (!wait(static_cast<uint64_t>(std::max(10, json_int("updateDelay", 500))))) break;
    }
}

DWORD WINAPI thread_main(void*) {
    DWORD code = 0;
    if (!guarded([] { loop(); }, &code))
        loader_log(KILN_LOG_ERROR, "Loader worker stopped after a fault (" + fault_text(code) + ")");
    if (g_unloading) stop_memory();
    write_status();
    return 0;
}

// Shroudtopia's loader in this process: shroudtopia.dll (its winmm.dll proxy loads it by that name), or a winmm.dll
// from the game folder carrying Shroudtopia's or Shroudweave's name.
std::string own_loader_in_process() {
    HMODULE modules[2048]{};
    DWORD needed = 0;
    if (!K32EnumProcessModules(GetCurrentProcess(), modules, sizeof modules, &needed)) return {};
    const auto count = std::min<size_t>(needed / sizeof(HMODULE), std::size(modules));
    for (size_t i = 0; i < count; ++i) {
        if (modules[i] == GetModuleHandleW(nullptr)) continue;
        wchar_t p[32768]{};
        const auto n = GetModuleFileNameW(modules[i], p, static_cast<DWORD>(std::size(p)));
        if (!n || n == std::size(p)) continue;
        const fs::path file(p);
        if (kcompat::lower(path_utf8(file.filename())) == "shroudtopia.dll" &&
            !kcompat::pe_exports_name(file, "CreateModInstance"))
            return "Shroudtopia's loader is loaded (" + to_utf8(p) + ")";
        std::error_code ec;
        const bool game = fs::equivalent(file.parent_path(), g_game, ec);
        const bool chain = fs::equivalent(file.parent_path(), g_game / L"kiln" / L"chain", ec);
        if (!game && !chain) continue;
        // Exclude our own plugin and mods with the API strings; a loader is a process-level proxy, not a
        // CreateModInstance-exporting mod. Chain DLLs may be renamed, so inspect every eligible module path.
        if (modules[i] == GetModuleHandleW(L"kiln-shroudtopia.dll") ||
            kcompat::pe_exports_name(file, KILN_PLUGIN_MAIN) || kcompat::pe_exports_name(file, "CreateModInstance")) continue;
        std::string d;
        kcompat::read_file(p, &d);
        const std::string l = kcompat::lower(d);
        if (l.find("shroudtopia") != std::string::npos || l.find("shroudweave") != std::string::npos)
            return "a Shroudtopia loader proxy is loaded (" + to_utf8(p) + ")";
    }
    return {};
}

void note_idle_loader() {
    for (const wchar_t* n : {L"shroudtopia.dll", L"winmm.dll"}) {
        const fs::path f = g_game / n;
        if (!kcompat::exists(f)) continue;
        std::string d;
        kcompat::read_file(f, &d);
        const std::string l = kcompat::lower(d);
        if (l.find("shroudtopia") == std::string::npos && l.find("shroudweave") == std::string::npos) continue;
        H.logf(KILN_LOG_INFO, "%s is Shroudtopia's; it did not start in this process (as under Wine without a winmm "
                              "override): kiln-shroudtopia loads the mods",
               path_utf8(f).c_str());
    }
}

// Anything for this plugin to do: profile entries, Shroudtopia's settings file, or a DLL exporting CreateModInstance in
// <game>/mods (top level or one folder down). Without any, the plugin stays idle: no loop thread, no settings file.
bool anything_to_load() {
    if (!g_plan.listed.empty()) return true;
    if (!g_plan.read_folder) return false;
    if (kcompat::exists(g_game / L"shroudtopia.json")) return true;
    std::error_code ec;
    auto is_mod_dll = [](const fs::path& f) {
        return kcompat::lower(to_utf8(f.extension().wstring())) == ".dll" && kcompat::pe_exports_name(f, "CreateModInstance");
    };
    for (const auto& e : fs::directory_iterator(g_game / L"mods", ec)) {
        if (is_mod_dll(e.path())) return true;
        if (!e.is_directory(ec) || !kcompat::exists(e.path() / L"mod.json") || kcompat::exists(e.path() / L"flame.json")) continue;
        for (const auto& f : fs::directory_iterator(e.path(), ec))
            if (is_mod_dll(f.path())) return true;
    }
    return false;
}

void KILN_CALL on_ready(void*) {
    g_game = H.path(KILN_PATH_GAME);
    g_state = H.state_dir();
    g_server = H.api->side() == KILN_SIDE_SERVER;
    g_plan = kcompat::plan_engine(H, "shroudtopia");
    if (!anything_to_load() && own_loader_in_process().empty()) {
        H.logf(KILN_LOG_INFO, "kiln-shroudtopia %s: no Shroudtopia mods (%s); idle until the next start", kPluginVersion, g_plan.note.c_str());
        return;
    }
    config::init(H.config_dir() / L"shroudtopia" / L"shroudtopia.json", g_game / L"shroudtopia.json");

    g_loader_watch.arm();  // before the startup check, so a DLL loading in between is re-checked
    g_stepped_aside = own_loader_in_process();
    if (!g_stepped_aside.empty()) {
        H.logf(KILN_LOG_WARN, "kiln-shroudtopia steps aside: %s; the Shroudtopia mods are its to load", g_stepped_aside.c_str());
        write_status();
        return;
    }
    note_idle_loader();
    if (g_plan.mode == KILN_PROFILE_NO_MODS || g_plan.mode == KILN_PROFILE_INVALID) {
        H.logf(KILN_LOG_INFO, "kiln-shroudtopia: %s", g_plan.note.c_str());
        write_status();
        return;
    }
    KilnBuildInfo bi{};
    bi.size = sizeof bi;
    std::string game_version = "unknown";
    if (H.api->build_info(&bi) == KILN_OK) game_version = std::string("sha256:") + bi.exe_sha256;
    g_ctx = make_context(g_server, game_version, config::file(), H.path(KILN_PATH_LOGS) / L"shroudtopia.log", g_game / L"mods");
    H.logf(KILN_LOG_INFO, "kiln-shroudtopia %s (Shroudtopia %s API) on %s: %s; settings %s; guarded memory helpers", kPluginVersion,
           kShroudtopiaVersion, g_server ? "the server" : "the client", g_plan.note.c_str(), path_utf8(config::file()).c_str());
    // Both the loop and mod-owned threads/hooks can retain pointers into this API after plugin revocation.
    // Pin the plugin before publishing a context/starting a worker; mod modules themselves are never freed.
    if (!kcompat::pin_module(reinterpret_cast<const void*>(&thread_main))) {
        loader_log(KILN_LOG_ERROR, "Cannot pin the compatibility runtime; no mods were started");
        return;
    }
    g_stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!g_stop || !(g_worker = CreateThread(nullptr, 0, &thread_main, nullptr, 0, nullptr))) {
        loader_log(KILN_LOG_ERROR, "Cannot create the loader worker; no mods were started");
        if (g_stop) CloseHandle(g_stop), g_stop = nullptr;
    }
    write_status();
}

void KILN_CALL on_unload(void*) {
    g_unloading = true;
    if (g_stop) SetEvent(g_stop);
    if (g_worker && WaitForSingleObject(g_worker, 1000) != WAIT_OBJECT_0) {
        // A foreign entry point can block indefinitely. Do not unmap its API or revoke its trampolines while it
        // is still running. The pinned worker will finish cleanup when that entry point returns.
        loader_log(KILN_LOG_WARN, "A mod call is still running; retaining the pinned compatibility runtime");
        return;
    }
    stop_memory();
    write_status();
    if (g_worker) CloseHandle(g_worker), g_worker = nullptr;
    if (g_stop) CloseHandle(g_stop), g_stop = nullptr;
}

}  // namespace
}  // namespace kshroud

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    using namespace kshroud;
    if (!api || api->abi_version != KILN_ABI_VERSION || api->size < offsetof(KilnApi, session) + sizeof(void*))
        return KILN_ERR_UNAVAILABLE;
    H.api = api;
    H.owner = owner;
    info->id = kcompat::kstr(kPluginId);
    info->version = kcompat::kstr(kPluginVersion);
    if (info->size >= offsetof(KilnPluginInfo, on_ready) + sizeof(void*)) info->on_ready = &on_ready;
    info->on_unload = &on_unload;
    return KILN_OK;
}
