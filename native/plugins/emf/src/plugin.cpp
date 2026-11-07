// SPDX-License-Identifier: MIT
// kiln-emf.dll: a Kiln plugin (<game>/kiln/plugins/) that loads unmodified EMF mods (EMF 0.3.3 to 0.5.1, EmfApi v26) on Kiln's
// services. In on_ready, after every service is up and before the game's entry point:
//   1. step aside when the real EMF runs in this process (emf_core.dll, or EMF's loader chained by Kiln's proxy);
//   2. find the mods (the active profile's "emf" entries through kiln.profile, and <game>/emf/mods);
//   3. decide each mod (switch, API version, side, dependencies) and order them by their dependencies;
//   4. LoadLibraryExW + emf_mod_load(&table) under a fault guard (never FreeLibrary);
//   5. register what the mods asked for (event programs, chat), subscribe the world ticks;
//   6. write <state>/emf.json (status of every mod, unsupported calls).
// Docs: docs/kiln/emf.md (users), docs/re/emf-compat.md (mapping and evidence).
#include <algorithm>
#include <chrono>

#include <cstdio>

#include "emf_internal.h"
#include "nlohmann/json.hpp"

#include <psapi.h>

namespace kemf {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
uint32_t g_side = KILN_SIDE_SERVER;
Services S;
Paths P;
thread_local Context t_ctx;
std::string g_stepped_aside;

std::string unsupported_json();
std::string calls_json();
std::string aliases_json();
std::string commands_json();

namespace {

std::atomic<bool> g_dirty{true};
std::string g_emf_idle;  // set when the real EMF (that kiln-emf stepped aside for) went idle: no EMF mod runs
KilnHandle g_aside_tick = 0;
uint64_t g_aside_steps = 0;
uint64_t g_last_status = 0;
uint64_t g_steps = 0;

std::filesystem::path api_path(int32_t kind) {
    char buf[1024];
    size_t n = 0;
    if (g_api->path(kind, buf, sizeof buf, &n) != KILN_OK) return {};
    return std::filesystem::path(to_wide(std::string_view(buf, n)));
}

template <class T>
const T* service(const char* name, uint32_t version) {
    const auto* t = static_cast<const T*>(g_api->service(kstr(name), version));
    if (t && t->header.size < sizeof(KilnServiceHeader)) return nullptr;
    return t;
}

// An EMF module in this process: emf_core.dll, or EMF's loader (dinput8.dll exporting emf_loader_identity).
std::string emf_in_process() {
    if (HMODULE h = GetModuleHandleW(L"emf_core.dll")) {
        wchar_t p[MAX_PATH] = L"";
        GetModuleFileNameW(h, p, MAX_PATH);
        return "EMF's core is loaded (" + to_utf8(p) + ")";
    }
    HMODULE mods[1024];
    DWORD needed = 0;
    if (!K32EnumProcessModules(GetCurrentProcess(), mods, sizeof mods, &needed)) return {};
    const DWORD n = std::min<DWORD>(needed / sizeof(HMODULE), 1024);
    for (DWORD i = 0; i < n; ++i) {
        if (GetProcAddress(mods[i], "emf_loader_identity") || GetProcAddress(mods[i], "emf_core_start")) {
            wchar_t p[MAX_PATH] = L"";
            GetModuleFileNameW(mods[i], p, MAX_PATH);
            return "EMF's loader is loaded (" + to_utf8(p) + "), it starts EMF and its mods";
        }
    }
    return {};
}

// EMF's loader listed (an active line) in a chain list, or sitting in kiln/chain/, without being loaded: worth a line
// (it does not run, so kiln-emf loads the mods).
bool is_emf_loader_file(const std::filesystem::path& f) {
    std::string data;
    return read_file(f, &data) && data.find("EMF-LOADER-") != std::string::npos;
}

void note_chain_lists() {
    for (const auto& [list, base] : {std::pair{P.game / L"dinput8-chain.ini", P.game}, std::pair{P.kiln / L"chain.ini", P.kiln}}) {
        std::string t;
        if (!read_file(list, &t)) continue;
        size_t i = 0;
        while (i < t.size()) {
            size_t j = t.find('\n', i);
            if (j == std::string::npos) j = t.size();
            std::string line = t.substr(i, j - i);
            i = j + 1;
            while (!line.empty() && (line.back() == '\r' || line.back() == ' ')) line.pop_back();
            const size_t eq = line.find('=');
            if (eq != std::string::npos) line = line.substr(eq + 1);
            while (!line.empty() && line.front() == ' ') line.erase(0, 1);
            if (line.empty() || line[0] == ';' || line[0] == '#' || line[0] == '[' || line.rfind("//", 0) == 0) continue;
            if (lower(line).size() < 4 || lower(line).substr(line.size() - 4) != ".dll") continue;
            std::replace(line.begin(), line.end(), '/', '\\');
            const std::filesystem::path p = (line.size() > 2 && line[1] == ':') ? std::filesystem::path(to_wide(line)) : base / to_wide(line);
            if (is_emf_loader_file(p))
                logf(KILN_LOG_WARN, "%s lists EMF's loader (%s), but no EMF module is loaded: kiln-emf loads the EMF mods",
                     path_utf8(list).c_str(), line.c_str());
        }
    }
    std::error_code ec;
    for (const auto& e : std::filesystem::directory_iterator(P.kiln / L"chain", ec))
        if (lower(to_utf8(e.path().extension().wstring())) == ".dll" && is_emf_loader_file(e.path()))
            logf(KILN_LOG_INFO, "%s is EMF's loader, not loaded (switched off in kiln/chain.ini): kiln-emf loads the EMF mods",
                 path_utf8(e.path()).c_str());
}

// After stepping aside: EMF 0.3.3 goes idle on a game build whose required signatures it cannot find (it says so in
// emf/emf_core.log). Then no EMF mod runs at all; tell the user how to let kiln-emf load them instead.
void KILN_CALL on_aside_pre(const KilnTickInfo*, void*) {
    if (++g_aside_steps != 50) return;  // ~2 s into the world: EMF has long decided
    std::string log;
    read_file(P.emf / L"emf_core.log", &log);
    if (log.find("EMF stays idle") != std::string::npos) {
        g_emf_idle = "EMF's core went idle on this game build (emf/emf_core.log: \"a required signature is missing: EMF "
                     "stays idle\"): no EMF mod runs";
        logf(KILN_LOG_WARN,
             "%s. To run them with kiln-emf instead, switch EMF's loader off: put ';' in front of its line in "
             "kiln/chain.ini (or remove it from dinput8-chain.ini), then restart",
             g_emf_idle.c_str());
    } else {
        logf(KILN_LOG_INFO, "EMF's core runs the EMF mods (kiln-emf stays out of the way)");
    }
    write_status();
}

// ---------------------------------------------------------------------------------------------- ticks ---------
void KILN_CALL on_post(const KilnTickInfo* tick, void*) {
    const uint32_t kind = tick ? tick->world_kind : KILN_WORLD_SERVER;
    dispatch_after_step(world_of_kind(kind), static_cast<uint8_t>(kind));
}

void KILN_CALL on_world_post(const KilnTickInfo* tick, void* user) {
    const auto kind = static_cast<uint32_t>(reinterpret_cast<uintptr_t>(user));
    (void)tick;
    dispatch_after_step(world_of_kind(kind), static_cast<uint8_t>(kind));
}

void KILN_CALL on_pre(const KilnTickInfo*, void*) {
    ++g_steps;
    dispatch_server_pre();
}

void KILN_CALL on_world_event(const KilnWorldEvent* e, void*) {
    if (!e || e->kind >= KILN_WORLD_KINDS) return;
    if (e->event == KILN_WORLD_STARTED) {
        if (e->kind == KILN_WORLD_SERVER) components_world_started();
        windows_world_started(static_cast<uint8_t>(e->kind));
        return;
    }
    if (e->event != KILN_WORLD_STOPPING) return;
    if (e->kind == KILN_WORLD_SERVER) {
        events_world_stopping();
        components_world_stopping();
    }
    windows_world_stopping(static_cast<uint8_t>(e->kind));
}

void KILN_CALL on_ready(void*) {
    // paths
    P.game = api_path(KILN_PATH_GAME);
    P.kiln = api_path(KILN_PATH_KILN);
    P.logs = api_path(KILN_PATH_LOGS);
    P.emf = P.game / L"emf";
    S.profile = service<KilnProfileApi>(KILN_PROFILE_SERVICE, 1);
    P.config = api_path(KILN_PATH_CONFIG);  // an older core: INVALID_ARGUMENT -> fall back below
    if (S.profile && sv(S.profile->config_dir()).size()) P.config = std::filesystem::path(to_wide(sv(S.profile->config_dir())));
    if (P.config.empty()) P.config = P.kiln / L"config";
    P.state = api_path(KILN_PATH_STATE);
    if (P.state.empty()) {
        P.state = P.logs;
        P.state_is_logs = true;
    }

    // 1. step aside
    g_stepped_aside = emf_in_process();
    if (!g_stepped_aside.empty()) {
        logf(KILN_LOG_WARN, "kiln-emf steps aside: %s; the EMF mods are EMF's to load", g_stepped_aside.c_str());
        g_api->subscribe_tick(g_owner, KILN_TICK_PRE, &on_aside_pre, nullptr, &g_aside_tick);
        write_status();
        return;
    }
    note_chain_lists();

    // services (in-core and DLL services are all published by now)
    S.world = service<KilnWorldApi>(KILN_WORLD_SERVICE, 1);
    S.client = service<KilnClientApi>(KILN_CLIENT_SERVICE, 1);
    S.hooks = service<KilnHooksApi>(KILN_HOOKS_SERVICE, 1);
    S.items = service<KilnItemsApi>(KILN_ITEMS_SERVICE, 1);
    S.content = service<KilnContentApi>(KILN_CONTENT_SERVICE, 1);
    S.spawn = service<KilnSpawnApi>(KILN_SPAWN_SERVICE, 1);
    S.chat = service<KilnChatApi>(KILN_CHAT_SERVICE, 1);
    S.players = service<KilnPlayersApi>(KILN_PLAYERS_SERVICE, 1);
    S.saves = service<KilnSavesApi>(KILN_SAVES_SERVICE, 1);
    S.settings = service<KilnSettingsApi>(KILN_SETTINGS_SERVICE, 1);
    S.combat = service<KilnCombatApi>(KILN_COMBAT_SERVICE, 1);
    S.ui = service<KilnUiApi>(KILN_UI_SERVICE, 1);
    S.progression = service<KilnProgressionApi>(KILN_PROGRESSION_SERVICE, 1);
    logf(KILN_LOG_INFO,
         "kiln-emf %s (EmfApi v%u, %zu bytes) on %s; services: world %d, items %d, content %d, spawn %d, chat %d, players %d, "
         "saves %d, settings %d, combat %d, client %d, ui %d, profile %d, progression %d; config %s, status %s",
         kPluginVersion, kEmfApiVersion, sizeof(EmfApi), g_side == KILN_SIDE_SERVER ? "the server" : "the client",
         !!S.world, !!S.items, !!S.content, !!S.spawn, !!S.chat, !!S.players, !!S.saves, !!S.settings, !!S.combat,
         !!S.client, !!S.ui, !!S.profile, !!S.progression, path_utf8(P.config / L"emf").c_str(), path_utf8(P.state / L"emf.json").c_str());

    // 2-4. find, decide, order, load
    const EmfApi* table = api_table();
    commands_on_ready();  // before the mods: on_chat in emf_mod_load needs to know whether chat lines arrive
    discover_mods();
    load_mods(table);

    // 5. what the mods registered (components first: its E-prompt listener is an event registration)
    components_on_ready();
    events_on_ready();
    game_on_ready();
    hud_on_ready();
    install_ticks();

    size_t loaded = 0;
    for (const auto& m : g_mods) loaded += m->status == EMF_MOD_LOADED;
    logf(KILN_LOG_INFO, "kiln-emf: %zu of %zu EMF mod(s) loaded", loaded, g_mods.size());
    write_status();
}

void KILN_CALL on_unload(void*) {
    components_world_stopping();
    windows_shutdown();
    write_status();
}

}  // namespace

void install_ticks() {
    KilnHandle h = 0;
    if (g_api->subscribe_world(g_owner, &on_world_event, nullptr, &h) != KILN_OK)
        logf(KILN_LOG_WARN, "EMF world cleanup subscription failed");
    const KilnResult a = g_api->subscribe_tick(g_owner, KILN_TICK_POST, &on_post, nullptr, &h);
    const KilnResult b = g_api->subscribe_tick(g_owner, KILN_TICK_PRE, &on_pre, nullptr, &h);
    if (a != KILN_OK || b != KILN_OK) logf(KILN_LOG_WARN, "server-world ticks unavailable (%d/%d): mods get no on_tick there", a, b);
    if (g_side == KILN_SIDE_CLIENT) {
        for (uint32_t kind : {static_cast<uint32_t>(KILN_WORLD_CLIENT), static_cast<uint32_t>(KILN_WORLD_PREDICTION)}) {
            const KilnResult r = g_api->subscribe_world_tick(g_owner, kind, KILN_TICK_POST, &on_world_post,
                                                              reinterpret_cast<void*>(static_cast<uintptr_t>(kind)), &h);
            if (r != KILN_OK) logf(KILN_LOG_WARN, "world kind %u ticks unavailable (%d)", kind, r);
        }
    }
}

void dispatch_server_pre() {
    game_server_pre();
    commands_server_pre();
    const uint64_t now = now_ms();
    if ((g_dirty.load() && now - g_last_status > 5000) || now - g_last_status > 60000) {
        g_last_status = now;
        write_status();
    }
}

void mark_status_dirty() { g_dirty = true; }

void write_status() {
    g_dirty = false;
    using json = nlohmann::json;
    static const char* kStatus[] = {"loaded", "failed", "disabled", "rejected", "script", "pending"};
    json mods = json::array();
    for (const auto& m : g_mods) {
        json j = {{"id", m->id},
                  {"name", m->name},
                  {"version", m->version},
                  {"status", m->status < 6 ? kStatus[m->status] : "?"},
                  {"reason", m->reason},
                  {"source", m->source},
                  {"origin", m->origin},
                  {"folder", path_utf8(m->folder)},
                  {"side", m->side.empty() ? "both" : m->side},
                  {"emf", m->emf_min},
                  {"enabled", m->enabled},
                  {"loadMs", m->load_ms},
                  {"faults", m->faults.load()},
                  {"tickCalls", m->tick_calls.load()},
                  {"tickMsTotal", static_cast<double>(m->tick_ns.load()) / 1e6},
                  {"tickMsPerStep", g_steps ? static_cast<double>(m->tick_ns.load()) / 1e6 / static_cast<double>(g_steps) : 0.0},
                  {"tickMsMax", static_cast<double>(m->tick_max_ns.load()) / 1e6}};
        if (!m->dll.empty()) j["dll"] = path_utf8(m->dll);
        if (!m->main.empty()) j["main"] = m->main;
        json deps = json::array();
        for (const auto& d : m->deps) deps.push_back({{"id", d.id}, {"version", d.range}, {"optional", d.optional}});
        j["depends"] = deps;
        mods.push_back(j);
    }
    json doc = {{"format", 1},
                {"plugin", kPluginId},
                {"version", kPluginVersion},
                {"emfApi", kEmfApiVersion},
                {"side", g_side == KILN_SIDE_SERVER ? "server" : "client"},
                {"steppedAside", !g_stepped_aside.empty()},
                {"steppedAsideReason", g_stepped_aside},
                {"emfIdle", g_emf_idle},
                {"configDir", path_utf8(P.config)},
                {"updatedUnixMs", static_cast<int64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(
                                                           std::chrono::system_clock::now().time_since_epoch())
                                                           .count())},
                {"serverSteps", g_steps},
                {"mods", mods}};
    if (g_stepped_aside.empty()) {
        try {
            doc["unsupported"] = json::parse(unsupported_json());
            doc["commands"] = json::parse(commands_json());
            doc["signatures"] = json::parse(aliases_json());
        } catch (...) {
        }
    }
    const auto file = P.state / L"emf.json";
    if (!write_file_atomic(file, doc.dump(2) + "\n")) logf(KILN_LOG_WARN, "cannot write %s", path_utf8(file).c_str());
}

}  // namespace kemf

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    using namespace kemf;
    if (!api || api->abi_version != KILN_ABI_VERSION || api->size < offsetof(KilnApi, session) + sizeof(void*))
        return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    g_side = api->side();
    info->id = KilnStr{kPluginId, std::strlen(kPluginId)};
    info->version = KilnStr{kPluginVersion, std::strlen(kPluginVersion)};
    if (info->size >= offsetof(KilnPluginInfo, on_ready) + sizeof(void*)) info->on_ready = &on_ready;
    info->on_unload = &on_unload;
    return KILN_OK;
}
