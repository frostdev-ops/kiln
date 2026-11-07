// SPDX-License-Identifier: MIT
// kiln-ember.dll: Ember mods on Kiln (docs/kiln/engines/ember.md). Kiln's own code, written from Ember's documents
// (zachthedev/enshrouded-ember, MIT: README.md, docs/usage.md, docs/install.md, the module docs of ember-loader and
// ember-sdk 0.1.0).
//
// State of Ember (2026-11-07, commit f7434d87): the loader and the SDK are designed but not written. ember-sdk 0.1.0
// and ember-loader 0.1.0 on crates.io contain module documentation only: no `declare` macro, no export names, no
// host table. Its one mod, private-chests (zachthedev/enshrouded-mods), is a description with no code. So there is no
// mod ABI a host could implement yet. What is known and implemented here:
//   - layout: one directory per mod holding one library, under <server>/ember/mods/<mod>/ (the mod's config lives
//     in the same folder); Ember's own config is ember/config.json, its logs ember/logs/;
//   - a mod library exports an ABI-version symbol, read first ("a mismatch is refused by name before anything else in
//     the library runs"), and a register call returning a manifest;
//   - dedicated server only; the loader is a proxy named POWRPROF.dll (or IPHLPAPI.DLL / dbghelp.dll) next to
//     enshrouded_server.exe.
// This plugin:
//   1. steps aside when Ember's loader is in the process (a proxy DLL from the server folder or kiln/chain/ whose
//      file names "ember-loader"/"ember_loader", the marker Kiln's proxy classification uses);
//   2. finds the mods: the profile's "ember" entries, then ember/mods/<mod>/ (compat.h's rule);
//   3. never loads a mod library (its exports cannot be called without the ABI): it reads each library's export
//      table from the file and logs what it carries, so a released ABI can be checked against real mods;
//   4. writes kiln/state/ember.json and logs one line per mod. Nothing is hooked; the server runs on.
#include <windows.h>

#include <psapi.h>

#include <set>

#include "../../compat/compat.h"

namespace kember {
namespace {

namespace fs = std::filesystem;
using kcompat::path_utf8;
using kcompat::to_utf8;

constexpr const char* kPluginId = "kiln-ember";
constexpr const char* kPluginVersion = "0.1.0";
// Ember's design, as its documents state it: no host may call into a mod before its ABI version is checked, and that
// version and the register export are not published yet.
constexpr const char* kNoAbi =
    "not loaded: Ember has not published its mod ABI yet (ember-sdk 0.1.0 holds documentation only), so no host can "
    "start this library safely";

kcompat::Host H{nullptr, 0, "ember"};

struct Mod {
    std::string id, origin, status, reason;
    fs::path folder, library;
    std::vector<std::string> exports;
};
std::vector<Mod> g_mods;
std::string g_stepped_aside;

// Ember's loader: a proxy under one of the names Ember documents, from the server folder or Kiln's chain folder, whose
// file carries Ember's marker text. Read from disk only.
std::string ember_loader_in_process(const fs::path& game, const fs::path& kiln) {
    HMODULE mods[1024];
    DWORD needed = 0;
    if (!K32EnumProcessModules(GetCurrentProcess(), mods, sizeof mods, &needed)) return {};
    const DWORD n = std::min<DWORD>(needed / sizeof(HMODULE), 1024);
    std::error_code ec;
    for (DWORD i = 0; i < n; ++i) {
        wchar_t p[MAX_PATH] = L"";
        if (!GetModuleFileNameW(mods[i], p, MAX_PATH) || mods[i] == GetModuleHandleW(nullptr)) continue;
        const fs::path f(p);
        const fs::path dir = f.parent_path();
        if (!fs::equivalent(dir, game, ec) && !fs::equivalent(dir, kiln / L"chain", ec)) continue;
        if (GetProcAddress(mods[i], KILN_PLUGIN_MAIN) || GetProcAddress(mods[i], "KilnProxyInfo")) continue;
        if (kcompat::file_contains(f, "ember-loader") || kcompat::file_contains(f, "ember_loader")) return path_utf8(f);
    }
    return {};
}

// The one library of a mod folder (Ember: "each mod is one directory holding one library").
Mod read_mod(const fs::path& folder, const std::string& id, bool enabled, const char* origin, const fs::path& root) {
    Mod m;
    m.folder = folder;
    m.origin = origin;
    m.id = id.empty() ? to_utf8(folder.filename().wstring()) : id;
    m.status = "skipped";
    if (!enabled) { m.reason = "switched off in the profile"; return m; }
    std::error_code ec;
    if (fs::is_regular_file(folder, ec)) {  // a profile entry may name the library itself
        m.library = folder;
        m.folder = folder.parent_path();
    } else {
        if (!kcompat::contained_path(root, folder)) { m.reason = "mod folder is outside its root or is a symlink"; return m; }
        std::vector<fs::path> libs;
        for (const auto& e : fs::directory_iterator(folder, ec)) {
            auto ext = kcompat::lower(to_utf8(e.path().extension().wstring()));
            if (e.is_regular_file(ec) && ext == ".dll") libs.push_back(e.path());
        }
        if (libs.empty()) { m.reason = "no library (.dll) in the mod folder"; return m; }
        if (libs.size() > 1) { m.reason = "more than one library in the mod folder (Ember allows one)"; return m; }
        m.library = libs.front();
    }
    for (const auto& e : kcompat::pe_exports(m.library)) m.exports.push_back(e);
    m.status = "waiting-for-abi";
    m.reason = kNoAbi;
    return m;
}

void write_status() {
    using json = nlohmann::json;
    json mods = json::array();
    for (const auto& m : g_mods)
        mods.push_back({{"id", m.id}, {"origin", m.origin}, {"status", m.status}, {"reason", m.reason},
                        {"folder", path_utf8(m.folder)}, {"library", path_utf8(m.library)}, {"exports", m.exports}});
    json doc = {{"format", 1},
                {"plugin", kPluginId},
                {"version", kPluginVersion},
                {"side", H.api->side() == KILN_SIDE_SERVER ? "server" : "client"},
                {"abi", nullptr},
                {"steppedAside", !g_stepped_aside.empty()},
                {"steppedAsideReason", g_stepped_aside},
                {"updatedUnixMs", kcompat::unix_ms()},
                {"mods", mods}};
    const auto file = H.state_dir() / L"ember.json";
    if (!kcompat::write_file_atomic(file, doc.dump(2) + "\n")) H.logf(KILN_LOG_WARN, "cannot write %s", path_utf8(file).c_str());
}

void KILN_CALL on_ready(void*) {
    const fs::path game = H.path(KILN_PATH_GAME);
    const fs::path kiln = H.path(KILN_PATH_KILN);
    const auto plan = kcompat::plan_engine(H, "ember");
    std::set<std::string> seen;
    for (const auto& r : plan.listed) {
        Mod m = read_mod(r.path, r.id, r.enabled, "profile", r.path);
        if (seen.insert(kcompat::lower(m.id)).second) g_mods.push_back(std::move(m));
    }
    if (plan.read_folder) {
        const fs::path root = game / L"ember" / L"mods";
        std::vector<fs::path> dirs;
        std::error_code ec;
        for (const auto& d : fs::directory_iterator(root, ec))
            if (d.is_directory(ec)) dirs.push_back(d.path());
        std::sort(dirs.begin(), dirs.end());
        for (const auto& d : dirs) {
            Mod m = read_mod(d, {}, true, "folder", root);
            if (!seen.insert(kcompat::lower(m.id)).second) continue;  // the profile's copy wins
            g_mods.push_back(std::move(m));
        }
    }
    const std::string loader = ember_loader_in_process(game, kiln);
    if (!loader.empty()) {
        g_stepped_aside = "Ember's own loader (" + loader + ") runs in this process and starts the mods in ember/mods";
        for (auto& m : g_mods)
            if (m.status == "waiting-for-abi") m.status = "skipped", m.reason = m.origin == "folder" ? "" : g_stepped_aside;
    }
    H.logf(KILN_LOG_INFO, "kiln-ember %s on %s: %s; %zu Ember mod(s)", kPluginVersion,
           H.api->side() == KILN_SIDE_SERVER ? "the server" : "the client", plan.note.c_str(), g_mods.size());
    if (!g_stepped_aside.empty()) H.logf(KILN_LOG_INFO, "kiln-ember steps aside: %s", g_stepped_aside.c_str());
    if (H.api->side() != KILN_SIDE_SERVER && !g_mods.empty())
        H.log(KILN_LOG_INFO, "Ember is for dedicated servers only; its mods never run in the game client");
    for (const auto& m : g_mods) {
        std::string ex;
        for (const auto& e : m.exports) ex += (ex.empty() ? "" : ", ") + e;
        H.mod_log(m.status == "waiting-for-abi" ? KILN_LOG_WARN : KILN_LOG_INFO, m.id,
                  m.origin + ": " + (m.reason.empty() ? std::string("runs under Ember's own loader") : m.reason) +
                      (m.library.empty() ? std::string() : " (" + path_utf8(m.library) + "; exports: " + (ex.empty() ? "none" : ex) + ")"));
    }
    write_status();
}

}  // namespace
}  // namespace kember

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    using namespace kember;
    if (!api || api->abi_version != KILN_ABI_VERSION || api->size < offsetof(KilnApi, session) + sizeof(void*))
        return KILN_ERR_UNAVAILABLE;
    if (!info || info->size < offsetof(KilnPluginInfo, on_ready) + sizeof(void*)) return KILN_ERR_UNAVAILABLE;
    H.api = api;
    H.owner = owner;
    info->id = kcompat::kstr(kPluginId);
    info->version = kcompat::kstr(kPluginVersion);
    info->on_ready = &on_ready;
    return KILN_OK;
}
