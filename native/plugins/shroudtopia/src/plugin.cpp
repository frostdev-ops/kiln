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
#include <thread>

#include "shroudtopia_internal.h"

namespace kshroud {

kcompat::Host H{nullptr, 0, "shroudtopia"};
const KilnHooksApi* g_hooks = nullptr;

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

void write_status() {
    json mods = json::array();
    json refused = json::array();
    {
        std::lock_guard lock(g_mu);
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
                {"steppedAside", !g_stepped_aside.empty()},
                {"steppedAsideReason", g_stepped_aside},
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
    fs::path dll;
    if (kcompat::exists(named) && kcompat::pe_exports_name(named, "CreateModInstance")) {
        dll = named;
    } else {
        std::vector<fs::path> files;
        std::error_code ec;
        for (const auto& e : fs::directory_iterator(dir, ec))
            if (kcompat::lower(to_utf8(e.path().extension().wstring())) == ".dll") files.push_back(e.path());
        std::sort(files.begin(), files.end());
        for (const auto& f : files)
            if (kcompat::pe_exports_name(f, "CreateModInstance")) {
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
    out->id = id;
    return true;
}

std::vector<Candidate> scan() {
    std::vector<Candidate> out;
    auto add_dll_file = [&](const fs::path& f, const std::string& id, const char* origin) {
        Candidate c{f, id.empty() ? to_utf8(f.stem().wstring()) : id, origin};
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
                out.push_back(std::move(c));
            } else if (!g_refused.count(r.path.wstring())) {
                g_refused[r.path.wstring()] = why;
                H.logf(KILN_LOG_WARN, "profile entry %s: %s", r.id.c_str(), why.c_str());
            }
        } else {
            add_dll_file(r.path, r.id, "profile");
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
        if (!kcompat::exists(d / L"mod.json")) continue;
        if (kcompat::exists(d / L"flame.json") || kcompat::exists(d / L"src" / L"mod.lua")) continue;  // other loaders
        Candidate c;
        if (folder_dll(d, {}, &c, nullptr)) {
            c.origin = "folder";
            out.push_back(std::move(c));
        }
    }
    for (const auto& f : files) add_dll_file(f, {}, "folder");
    return out;
}

// Loads and registers new DLLs (Shroudtopia's CheckAndLoadDlls + LoadAndRegisterMod).
void check_and_load() {
    std::set<std::string> names_now;
    for (const auto& c : scan()) {
        const std::wstring key = c.dll.lexically_normal().wstring();
        {
            std::lock_guard lock(g_mu);
            if (g_seen.count(key)) continue;
            g_seen.insert(key);
        }
        if (!kcompat::pe_exports_name(c.dll, "CreateModInstance")) {
            std::lock_guard lock(g_mu);
            g_refused[key] = "CreateModInstance not found (the DLL was not loaded)";
            continue;
        }
        HMODULE h = LoadLibraryExW(c.dll.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (!h) {
            const DWORD err = GetLastError();
            loader_log(KILN_LOG_ERROR, "Failed to load DLL: " + path_utf8(c.dll) + " (error " + std::to_string(err) + ")");
            std::lock_guard lock(g_mu);
            g_seen.erase(key);  // retryable, as in Shroudtopia 1.0 (a dependency may arrive later)
            continue;
        }
        auto create = reinterpret_cast<CreateModFn>(GetProcAddress(h, "CreateModInstance"));
        auto m = std::make_unique<Loaded>();
        m->dll = c.dll;
        m->origin = c.origin;
        m->id = c.id;
        DWORD code = 0;
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
    const bool ok = guarded(call, &code);
    set_current_mod({});
    if (!ok) {
        m.status = "faulted";
        m.reason = std::string(what) + " faulted (" + fault_text(code) + "); the mod is switched off until the next start";
        H.logf(KILN_LOG_ERROR, "%s: %s", m.name.c_str(), m.reason.c_str());
    }
}

// Shroudtopia's UpdateMods.
void update_mods() {
    std::vector<Loaded*> mods;
    {
        std::lock_guard lock(g_mu);
        for (auto& m : g_mods) mods.push_back(m.get());
    }
    for (Loaded* m : mods) {
        if (m->status == "faulted" || m->status == "skipped") continue;
        if (!m->mod->loaded) {
            if ((g_server && !m->server) || (!g_server && !m->client)) {
                m->status = "skipped";
                m->reason = g_server ? "no server support (hasServerSupport false)" : "no client support (hasClientSupport false)";
                loader_log(KILN_LOG_INFO, m->name + ": " + m->reason);
                continue;
            }
            loader_log(KILN_LOG_INFO, "Loading mod: " + m->name);
            drive(*m, "Load", [&] { m->mod->Load(g_ctx); });
            if (m->status == "faulted") continue;
            m->mod->loaded = true;
            g_ctx->loadedMods.insert(m->mod);
            m->status = "loaded";
        }
    }
    for (Loaded* m : mods) {
        if (m->status == "faulted" || m->status == "skipped" || !m->mod->loaded) continue;
        set_current_mod(m->name);
        const json v = config::mod_get(m->name.c_str(), "active");
        const bool should = v.is_boolean() && v.get<bool>();
        if (should) {
            if (!m->mod->active) {
                loader_log(KILN_LOG_INFO, "Activating: " + m->name);
                drive(*m, "Activate", [&] { m->mod->Activate(g_ctx); });
                if (m->status == "faulted") continue;
            }
            drive(*m, "Update", [&] { m->mod->Update(g_ctx); });
            if (m->status == "faulted") continue;
            ++m->updates;
        } else if (m->mod->active) {
            loader_log(KILN_LOG_INFO, "Deactivating: " + m->name);
            drive(*m, "Deactivate", [&] { m->mod->Deactivate(g_ctx); });
            if (m->status == "faulted") continue;
        }
        m->status = m->mod->active ? "active" : "inactive";
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
    std::this_thread::sleep_for(std::chrono::milliseconds(std::max(0, boot)));
    uint64_t last_status = 0;
    for (;;) {
        config::reload_if_changed();
        if (json_bool("active", false)) {
            check_and_load();
            update_mods();
        }
        ++g_cycles;
        const uint64_t now = GetTickCount64();
        if (now - last_status > 5000) {
            last_status = now;
            write_status();
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(std::max(10, json_int("updateDelay", 500))));
    }
}

// Shroudtopia's loader in this process: shroudtopia.dll (its winmm.dll proxy loads it by that name), or a winmm.dll
// from the game folder carrying Shroudtopia's or Shroudweave's name.
std::string own_loader_in_process() {
    if (HMODULE h = GetModuleHandleW(L"shroudtopia.dll")) {
        wchar_t p[MAX_PATH] = L"";
        GetModuleFileNameW(h, p, MAX_PATH);
        return "Shroudtopia's loader is loaded (" + to_utf8(p) + ")";
    }
    if (HMODULE h = kcompat::module_in_dir(L"winmm.dll", g_game)) {
        wchar_t p[MAX_PATH] = L"";
        GetModuleFileNameW(h, p, MAX_PATH);
        std::string d;
        kcompat::read_file(p, &d);
        const std::string l = kcompat::lower(d);
        if (l.find("shroudtopia") != std::string::npos || l.find("shroudweave") != std::string::npos)
            return "a Shroudtopia winmm.dll is loaded (" + to_utf8(p) + ")";
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
    g_hooks = H.service<KilnHooksApi>(KILN_HOOKS_SERVICE, 1);
    g_plan = kcompat::plan_engine(H, "shroudtopia");
    if (!anything_to_load() && own_loader_in_process().empty()) {
        H.logf(KILN_LOG_INFO, "kiln-shroudtopia %s: no Shroudtopia mods (%s); idle until the next start", kPluginVersion, g_plan.note.c_str());
        return;
    }
    config::init(H.config_dir() / L"shroudtopia" / L"shroudtopia.json", g_game / L"shroudtopia.json");

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
    H.logf(KILN_LOG_INFO, "kiln-shroudtopia %s (Shroudtopia %s API) on %s: %s; settings %s; memory helpers on %s", kPluginVersion,
           kShroudtopiaVersion, g_server ? "the server" : "the client", g_plan.note.c_str(), path_utf8(config::file()).c_str(),
           g_hooks ? "kiln.hooks" : "their own");
    std::thread(loop).detach();
    write_status();
}

void KILN_CALL on_unload(void*) { write_status(); }

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
