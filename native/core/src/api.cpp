// SPDX-License-Identifier: MIT
// The KilnApi table and native plugin loading.
#include "api.h"

#include <intrin.h>

#include <algorithm>
#include <atomic>
#include <cstddef>
#include <cstring>
#include <cwchar>
#include <mutex>
#include <vector>

#include "crash.h"
#include "gates.h"
#include "guard.h"
#include "image.h"
#include "log.h"
#include "profile.h"
#include "progress.h"
#include "progress_file.h"  // module_name
#include "progress_state.h"
#include "registry.h"
#include "sigdb.h"
#include "config.h"
#include "compat.h"
#include "tick.h"
#include "worldinfo.h"
#include "services/kiln_caps.h"
#include "services/kiln_hooks.h"
#include "services/service.h"
#include "tasks_host.h"

// kiln.caps, the capability registry (services/caps.cpp)
namespace kiln::caps {
const KilnCapsApi* table();
void owner_gone(KilnOwner owner);
void feature_off(std::string_view name);
void service_published(KilnOwner owner, std::string_view name, uint32_t version);
void service_dll(const std::filesystem::path& file, std::string_view status, const std::string& error);
void service_internal_refused(const std::filesystem::path& file, const std::string& text);
bool flush();  // a last write of capabilities.json when something is dirty; waits about 2 s at most
}  // namespace kiln::caps

#include "kiln_version.h"
// The build metadata native/cmake/kiln_build_stamp.sh writes for every build ("+<yyyymmdd>.<git>", or "+nogit.<nonce>"
// in a source export without git); absent, no DLL gets Kiln-internal access (build_stamp() below).
#if __has_include("kiln_build.h")
#include "kiln_build.h"
#endif
#ifndef KILN_BUILD
#define KILN_BUILD ""
#endif

namespace kiln::tasks_svc {
void exit_begins();  // services/tasks.cpp: any exit: kiln.tasks refuses new tasks (one atomic store, no lock)
void exiting();      // a normal exit: kiln.tasks seals every owner too (no wait)
void park(uint32_t wait_ms);  // a normal exit, later: no task body or done starts any more; bounded wait for the running
const void* host_table();  // kiln.internal.tasks.host v1 (tasks_host.h); nullptr while kiln.tasks is not running
}  // namespace kiln::tasks_svc

namespace kiln {
namespace {

bool g_verified = false;
bool g_hooks = false;

KilnStr KILN_CALL api_version() { return kstr(kVersion); }

void KILN_CALL api_log(KilnOwner owner, int32_t level, KilnStr text) {
    log::write(level, reg::owner_name(owner), sv(text));
}

KilnResult KILN_CALL api_build_info(KilnBuildInfo* out) {
    if (!out || out->size < offsetof(KilnBuildInfo, kiln_version) + sizeof(KilnStr)) return KILN_ERR_INVALID_ARGUMENT;
    out->side = image().side;
    std::memset(out->exe_sha256, 0, sizeof out->exe_sha256);
    std::memcpy(out->exe_sha256, image().sha256.data(), std::min<size_t>(64, image().sha256.size()));
    out->verified = g_verified;
    out->hooks_enabled = g_hooks;
    out->kiln_version = kstr(kVersion);
    return KILN_OK;
}

bool KILN_CALL api_feature(KilnStr name) {
    const std::string_view n = sv(name);
    // A feature held by the client gate (gates.h) answers false until its game test, like its kiln.caps row.
    const bool on = g_hooks && sigdb::feature(n) && (n.substr(0, 4) != "tick" || tick::installed()) &&
                    (n != "world.lifecycle" || tick::lifecycle()) && !gates::is_held(n);
    if (!on && g_hooks) caps::feature_off(n);  // a tick hook that faulted since boot: its kiln.caps rows follow
    return on;
}

KilnResult KILN_CALL api_path(int32_t kind, char* buffer, size_t capacity, size_t* out_len) {
    const std::filesystem::path* p = nullptr;
    switch (kind) {
        case KILN_PATH_GAME: p = &paths().game; break;
        case KILN_PATH_KILN: p = &paths().kiln; break;
        case KILN_PATH_MODS: p = &paths().mods; break;
        case KILN_PATH_LOGS: p = &paths().logs; break;
        case KILN_PATH_DATA: p = &paths().data; break;
        case KILN_PATH_CONTROL: p = &paths().control; break;
        case KILN_PATH_CONFIG: p = &paths().config; break;
        case KILN_PATH_STATE: p = &paths().state; break;
        default: return KILN_ERR_INVALID_ARGUMENT;
    }
    const std::string s = to_utf8(p->wstring());
    if (out_len) *out_len = s.size();
    if (!buffer || capacity < s.size()) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buffer, s.data(), s.size());
    return KILN_OK;
}

KilnOwner KILN_CALL api_owner_create(KilnStr id) { return reg::owner_create(sv(id)); }
void KILN_CALL api_owner_revoke(KilnOwner owner) {
    reg::owner_revoke(owner);
    caps::owner_gone(owner);
}

KilnResult KILN_CALL api_subscribe_tick(KilnOwner owner, int32_t phase, KilnTickFn fn, void* user, KilnHandle* out) {
    if (!tick::installed()) return KILN_ERR_UNAVAILABLE;
    return reg::add_tick(owner, phase, fn, user, out);
}

KilnResult KILN_CALL api_unsubscribe(KilnHandle handle) {
    return (handle & tick::kHandleTag) ? tick::remove(handle) : reg::remove(handle);
}

KilnResult KILN_CALL api_post(KilnOwner owner, KilnTaskFn fn, void* user) {
    if (!tick::installed()) return KILN_ERR_UNAVAILABLE;
    return reg::post(owner, fn, user);
}

bool KILN_CALL api_on_tick_thread() { return tick::on_tick_thread(); }
KilnResult KILN_CALL api_tick_info(KilnTickInfo* out) { return tick::last(out); }

KilnResult KILN_CALL api_subscribe_log(KilnOwner owner, KilnLogLineFn fn, void* user, KilnHandle* out) {
    if (!g_hooks || !sigdb::feature("log.mirror")) return KILN_ERR_UNAVAILABLE;
    return reg::add_log(owner, fn, user, out);
}

KilnResult KILN_CALL api_symbol(KilnStr name, uintptr_t* out) {
    return sigdb::symbol(sv(name), out) ? KILN_OK : KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL api_constant(KilnStr name, int64_t* out) {
    return sigdb::constant(sv(name), out) ? KILN_OK : KILN_ERR_NOT_FOUND;
}

// ---- service lookup (begin)
struct Published {
    KilnOwner owner;
    std::string name;
    const void* table;
};
std::mutex g_published_mutex;
std::vector<Published> g_published;

// Kiln-internal tables (docs/kiln/services/README.md "Kiln-internal tables"): a name under "kiln.internal." is
// published and resolved only through the KilnApi the service loader hands the DLLs of kiln/services (Kiln's own, same
// build), and published only for an owner that loader created for one. Plugins, managed mods and foreign DLLs get the
// other KilnApi, which neither publishes nor resolves such a name. Not a sandbox: it keeps a table that hands out game
// addresses or lock primitives out of every other caller's reach.
constexpr std::string_view kInternalPrefix = "kiln.internal.";
bool internal_name(std::string_view name) { return name.size() > kInternalPrefix.size() && name.starts_with(kInternalPrefix); }
std::vector<KilnOwner> g_service_owners;  // under g_published_mutex: owners load_one created for kiln/services DLLs

// This core's build identity, the stamp every kiln_service/kiln_plugin DLL of the same build exports as
// kiln_build_stamp() (native/cmake/kiln_build_stamp.cpp): "<version><build metadata>", e.g.
// "0.2.0-dev+20261115.ddb9bd4". Without build metadata it is "": a matching version alone is not a build, so such a
// core grants no DLL internal access (fail closed).
std::string build_stamp() {
    constexpr std::string_view build = KILN_BUILD;
    return build.empty() ? std::string() : std::string(KILN_VERSION).append(build);
}

// A kiln/services DLL gets internal access only when it was built with this core: its kiln_build_stamp() equals the
// core's build identity. It keeps a stale or foreign service DLL out; it is no defence against native code already in
// the process.
bool service_stamp_trusted(std::string_view dll_stamp) {
    const std::string core = build_stamp();
    return !core.empty() && dll_stamp == core;
}

void service_owner_add(KilnOwner owner) {
    std::lock_guard lock(g_published_mutex);
    g_service_owners.push_back(owner);
}

// load_one's choice for a kiln/services DLL: a service owner (and the services' KilnApi) only for a DLL of this very
// build; every other DLL loads with the plugins' KilnApi and no internal access. `why` says which case refused.
bool grant_internal(KilnOwner owner, std::string_view dll_stamp, std::string& why) {
    const std::string core = build_stamp();
    if (core.empty()) {
        why = "this core was built without build metadata (no KILN_BUILD), so no service DLL gets Kiln-internal tables";
        return false;
    }
    if (dll_stamp != core) {
        why = "build stamp '" + (dll_stamp.empty() ? std::string("none") : std::string(dll_stamp)) + "' is not this core's '" +
              core + "' (a stale or foreign service DLL)";
        return false;
    }
    service_owner_add(owner);
    return true;
}

const void* lookup_service(KilnStr name, uint32_t min_version, bool services) {
    // The core's own original-code table (compat.h) predates the internal-name rule and is resolved by the shroudtopia
    // plugin through the plugins' KilnApi: it stays reachable from every view, ahead of any published table.
    if (sv(name) == compat::kOriginalCodeService)
        return min_version <= compat::kOriginalCodeVersion ? compat::original_code_table() : nullptr;
    if (internal_name(sv(name)) && !services) return nullptr;

    // kiln.profile works in safe idle too: early native mods and the managed host read it to find their mods/configs.
    if (sv(name) == KILN_PROFILE_SERVICE) return min_version <= KILN_PROFILE_VERSION ? profile::table() : nullptr;
    // kiln.progress too: the early plugins show their long work with it, before hooks are decided (and in safe idle).
    if (sv(name) == KILN_PROGRESS_SERVICE) return min_version <= KILN_PROGRESS_VERSION ? progress::table() : nullptr;
    // kiln.caps too: in safe idle it says why everything is off.
    if (sv(name) == KILN_CAPS_SERVICE) return min_version <= KILN_CAPS_VERSION ? caps::table() : nullptr;
    // kiln.hooks is the one service early plugins may use before hooks are decided (hooks::serve_early; its `available`
    // flag says whether it is on, and boot clears it again in safe idle).
    // Only the core's own table, never a published one of the same name.
    if (!g_hooks) return sv(name) == KILN_HOOKS_SERVICE ? svc::find(sv(name), min_version) : nullptr;
    if (const void* t = svc::find(sv(name), min_version)) return t;
    std::lock_guard lock(g_published_mutex);
    for (const auto& p : g_published) {
        if (p.name != sv(name)) continue;
        const auto* h = static_cast<const KilnServiceHeader*>(p.table);
        return h->version >= min_version && reg::owner_alive(p.owner) ? p.table : nullptr;
    }
    return nullptr;
}

const void* KILN_CALL api_service(KilnStr name, uint32_t min_version) { return lookup_service(name, min_version, false); }
const void* KILN_CALL api_service_services(KilnStr name, uint32_t min_version) {
    return lookup_service(name, min_version, true);
}

// Registration is split like lookup. The owner is whatever the caller passes (owner IDs are small sequential integers),
// so it cannot tell a service from a plugin; the KilnApi the call came through can. The plugins' register_service
// refuses every internal name, whatever the owner; the services' one also requires an owner load_one made for a
// kiln/services DLL.
KilnResult register_published(KilnOwner owner, KilnStr name, const void* table, bool services) {
    if (!table || !name.ptr || name.len == 0) return KILN_ERR_INVALID_ARGUMENT;
    // The managed host's lease table is the core's own: nobody publishes that name, not even a Kiln service.
    if (sv(name) == KILN_TASKS_HOST_SERVICE) {
        KLOG_W("service %s: reserved for the core, refused for %s", KILN_TASKS_HOST_SERVICE, reg::owner_name(owner).c_str());
        return KILN_ERR_INVALID_ARGUMENT;
    }
    const auto* h = static_cast<const KilnServiceHeader*>(table);
    if (h->size < sizeof(KilnServiceHeader)) return KILN_ERR_INVALID_ARGUMENT;
    {
        std::lock_guard lock(g_published_mutex);
        if (internal_name(sv(name)) &&
            (!services ||
             std::find(g_service_owners.begin(), g_service_owners.end(), owner) == g_service_owners.end())) {
            KLOG_W("service %.*s: a Kiln-internal name, refused for %s (only Kiln's own services publish one)",
                   static_cast<int>(name.len), name.ptr, reg::owner_name(owner).c_str());
            return KILN_ERR_INVALID_ARGUMENT;
        }
        for (const auto& p : g_published)
            if (p.name == sv(name)) return KILN_ERR_INVALID_ARGUMENT;
        g_published.push_back({owner, std::string(sv(name)), table});
    }
    KLOG_I("service %.*s v%u published by %s", static_cast<int>(name.len), name.ptr, h->version,
           reg::owner_name(owner).c_str());
    // Kiln-internal tables stay out of the public capability rows and capabilities.json.
    if (!internal_name(sv(name))) caps::service_published(owner, sv(name), h->version);  // unlocked: subscribers may call api_service
    return KILN_OK;
}

KilnResult KILN_CALL api_register_service(KilnOwner owner, KilnStr name, const void* table) {
    return register_published(owner, name, table, false);
}
KilnResult KILN_CALL api_register_service_services(KilnOwner owner, KilnStr name, const void* table) {
    return register_published(owner, name, table, true);
}

// The managed host's KilnApi (api_for_managed_host, tasks_host.h): the plugins' lookup, plus exactly the core's own
// kiln.internal.tasks.host table (never a published one: nobody may publish that name); every other internal name
// stays unresolved, and registration is the plugins' (the host publishes no internal table either).
const void* KILN_CALL api_service_managed_host(KilnStr name, uint32_t min_version) {
    if (sv(name) == KILN_TASKS_HOST_SERVICE)
        return min_version <= KILN_TASKS_HOST_VERSION ? tasks_svc::host_table() : nullptr;
    return lookup_service(name, min_version, false);
}
KilnApi managed_host_api_from(const KilnApi& plugins) {
    KilnApi host = plugins;
    host.service = &api_service_managed_host;
    return host;
}

// The services' KilnApi: the plugins' one with the lookup and the registration that also take Kiln-internal tables.
KilnApi services_api_from(const KilnApi& plugins) {
    KilnApi services = plugins;
    services.service = &api_service_services;
    services.register_service = &api_register_service_services;
    return services;
}
// ---- service lookup (end)

uintptr_t KILN_CALL api_server_world() { return g_hooks ? worldinfo::server_world() : 0; }
uintptr_t KILN_CALL api_singleton(uintptr_t world, KilnStr name) {
    return g_hooks ? worldinfo::singleton(world, sv(name)) : 0;
}

// ---- sides and worlds ----
uint32_t KILN_CALL api_side() { return image().side; }
uintptr_t KILN_CALL api_world(uint32_t kind) { return g_hooks ? worldinfo::world(kind) : 0; }
bool KILN_CALL api_on_world_thread(uint32_t kind) { return tick::on_world_thread(kind); }

KilnResult KILN_CALL api_subscribe_world_tick(KilnOwner owner, uint32_t kind, int32_t phase, KilnTickFn fn, void* user,
                                              KilnHandle* out) {
    if (!g_hooks) return KILN_ERR_UNAVAILABLE;
    return tick::subscribe_world_tick(owner, kind, phase, fn, user, out);
}

KilnResult KILN_CALL api_post_world(KilnOwner owner, uint32_t kind, KilnTaskFn fn, void* user) {
    if (!g_hooks) return KILN_ERR_UNAVAILABLE;
    return tick::post_world(owner, kind, fn, user);
}

KilnResult KILN_CALL api_subscribe_world(KilnOwner owner, KilnWorldEventFn fn, void* user, KilnHandle* out) {
    if (!g_hooks) return KILN_ERR_UNAVAILABLE;
    return tick::subscribe_world(owner, fn, user, out);
}

KilnResult KILN_CALL api_session(KilnSessionInfo* out) {
    if (!out || out->size < sizeof(KilnSessionInfo)) return KILN_ERR_INVALID_ARGUMENT;
    if (g_hooks) return tick::session(out);
    *out = KilnSessionInfo{};  // safe idle: nothing runs, nothing is known
    out->size = sizeof(KilnSessionInfo);
    out->side = image().side;
    return KILN_OK;
}

const KilnApi g_api = {
    sizeof(KilnApi),
    KILN_ABI_VERSION,
    &api_version,
    &api_log,
    &api_build_info,
    &api_feature,
    &api_path,
    &api_owner_create,
    &api_owner_revoke,
    &api_subscribe_tick,
    &api_unsubscribe,
    &api_post,
    &api_on_tick_thread,
    &api_tick_info,
    &api_subscribe_log,
    &api_symbol,
    &api_constant,
    &api_service,
    &api_register_service,
    &api_server_world,
    &api_singleton,
    &api_side,
    &api_world,
    &api_on_world_thread,
    &api_subscribe_world_tick,
    &api_post_world,
    &api_subscribe_world,
    &api_session,
};

const KilnApi g_service_api = services_api_from(g_api);
const KilnApi g_managed_host_api = managed_host_api_from(g_api);

}  // namespace

const KilnApi* api() { return &g_api; }
const KilnApi* api_for_managed_host() { return &g_managed_host_api; }
// This core's build identity for the managed host (KilnTasksHostApi.build_stamp): "" without build metadata.
const char* core_build_stamp() {
    static const std::string stamp = build_stamp();
    return stamp.c_str();
}

void set_runtime_state(bool verified, bool hooks_enabled) {
    g_verified = verified;
    g_hooks = hooks_enabled;
}

bool hooks_enabled() { return g_hooks; }

namespace {
struct LoadedModule {
    std::string name;
    KilnPluginInfo info;
    HMODULE module;
    bool own_service = false;  // a kiln/services DLL of this very build (Kiln-internal access): its on_unload is Kiln's
};
std::vector<LoadedModule> g_modules;
std::vector<PluginRecord> g_plugin_records;
std::vector<std::wstring> g_plugin_paths;  // lower-case full paths already handled (dedupe)

std::wstring lower_path(const std::filesystem::path& p) {
    std::wstring s = p.lexically_normal().wstring();
    CharLowerBuffW(s.data(), static_cast<DWORD>(s.size()));
    return s;
}

void record(const char* kind, const std::filesystem::path& file, const char* status, std::string error,
            const KilnPluginInfo* info = nullptr) {
    if (std::string_view(kind) == "service") {  // boot.json "plugins" lists plugins only; kiln.caps keeps these
        caps::service_dll(file, status, error);
        return;
    }
    PluginRecord r;
    r.file = u8(file.filename());
    r.path = u8(file);
    r.status = status;
    r.error = std::move(error);
    if (info && info->id.ptr) r.id.assign(info->id.ptr, info->id.len);
    if (info && info->version.ptr) r.version.assign(info->version.ptr, info->version.len);
    g_plugin_records.push_back(std::move(r));
}

// ---- service stamp (begin)
// A DLL's kiln_build_stamp() string (empty without the export), called and copied under the fault guard.
struct StampCall {
    const char*(KILN_CALL* fn)(void);
    const char* text;
};
void KILN_CALL call_stamp(void* user) {
    auto* c = static_cast<StampCall*>(user);
    c->text = c->fn();
}
std::string dll_build_stamp(HMODULE h) {
    StampCall call{reinterpret_cast<const char*(KILN_CALL*)(void)>(GetProcAddress(h, "kiln_build_stamp")), nullptr};
    unsigned long code = 0;
    if (!call.fn || !guard::task(&call_stamp, &call, &code) || !call.text) return {};
    char text[129]{};
    for (size_t i = 0; i < sizeof text - 1; ++i) {
        if (!guard::copy(&text[i], reinterpret_cast<uintptr_t>(call.text + i), 1)) return {};
        if (!text[i]) break;
    }
    return text;
}
// ---- service stamp (end)

void load_one(const std::filesystem::path& file, const char* kind) {
    const std::string name = to_utf8(file.filename().wstring());
    const std::wstring key = lower_path(file);
    if (std::find(g_plugin_paths.begin(), g_plugin_paths.end(), key) != g_plugin_paths.end()) return;
    g_plugin_paths.push_back(key);
    HMODULE h = LoadLibraryExW(file.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!h) {
        const DWORD error = GetLastError();
        KLOG_E("%s %s: LoadLibrary failed (%lu)", kind, name.c_str(), error);
        record(kind, file, "failed", "LoadLibrary failed (" + std::to_string(error) + ")");
        return;
    }
    for (const auto& m : g_modules)
        if (m.module == h) {  // the same DLL under another path (e.g. a profile entry pointing into kiln/plugins)
            KLOG_I("%s %s: already loaded as %s", kind, name.c_str(), m.name.c_str());
            FreeLibrary(h);   // drops only the extra reference
            return;
        }
    auto main = reinterpret_cast<KilnPluginMainFn>(GetProcAddress(h, KILN_PLUGIN_MAIN));
    if (!main) {
        KLOG_E("%s %s: no %s export; unloaded", kind, name.c_str(), KILN_PLUGIN_MAIN);
        record(kind, file, "failed", std::string("no ") + KILN_PLUGIN_MAIN + " export");
        FreeLibrary(h);
        return;
    }
    const KilnOwner owner = reg::owner_create(std::string(kind) + ":" + name);
    // kiln/services DLLs of this very build are Kiln's own: their owner may publish Kiln-internal tables and their
    // KilnApi resolves them. Another build's DLL loads as before, without that access.
    bool service = false;
    if (std::string_view(kind) == "service") {
        std::string why;
        service = grant_internal(owner, dll_build_stamp(h), why);
        if (!service) {
            const std::string text = name + ": " + why + ": loaded without Kiln-internal tables";
            KLOG_W("service %s", text.c_str());
            caps::service_internal_refused(file, text);
        }
    }
    KilnPluginInfo info{};
    info.size = sizeof info;
    KilnResult rc = KILN_ERR_INTERNAL;
    unsigned long code = 0;
    if (!guard::plugin_main(main, service ? &g_service_api : &g_api, owner, &info, &rc, &code)) {
        KLOG_E("%s %s: kiln_plugin_main faulted (0x%08lx); its registrations were revoked", kind, name.c_str(), code);
        char text[64];
        std::snprintf(text, sizeof text, "kiln_plugin_main faulted (0x%08lx)", code);
        record(kind, file, "failed", text);
        reg::owner_revoke(owner);
        caps::owner_gone(owner);
        return;  // keep the module mapped: it may still be on a stack we cannot see
    }
    if (rc != KILN_OK) {
        // KILN_ERR_UNAVAILABLE: not for this build or side, or switched off by its config, by design (boot.json "skipped", not an
        // error). The plugin's own line before this one says why.
        if (rc == KILN_ERR_UNAVAILABLE)
            KLOG_I("%s %s: not used here (off on this game build or side, or by its config); skipped", kind, name.c_str());
        else
            KLOG_W("%s %s: kiln_plugin_main returned %d; revoked", kind, name.c_str(), rc);
        record(kind, file, rc == KILN_ERR_UNAVAILABLE ? "skipped" : "failed",
               "kiln_plugin_main returned " + std::to_string(rc) + (rc == KILN_ERR_UNAVAILABLE ? " (unavailable)" : ""), &info);
        reg::owner_revoke(owner);
        caps::owner_gone(owner);
        return;
    }
    KLOG_I("%s %s loaded: %.*s %.*s", kind, name.c_str(), static_cast<int>(info.id.len),
           info.id.ptr ? info.id.ptr : "", static_cast<int>(info.version.len), info.version.ptr ? info.version.ptr : "");
    record(kind, file, "loaded", {}, &info);
    g_modules.push_back({name, info, h, service});
}

// The DLLs of `dir` in file-name order. `which`: 0 every DLL, 1 Kiln's own (kiln-*.dll), 2 the others (drop-ins).
std::vector<std::filesystem::path> list_dlls(const std::filesystem::path& dir, int which = 0) {
    std::vector<std::filesystem::path> files;
    std::error_code ec;
    if (!std::filesystem::is_directory(dir, ec)) return files;
    for (auto& e : std::filesystem::directory_iterator(dir, ec)) {
        std::wstring n = e.path().filename().wstring();
        CharLowerBuffW(n.data(), static_cast<DWORD>(n.size()));
        // Windows file names are case-insensitive: MyPlugin.DLL is a DLL too
        if (n.size() < 5 || n.compare(n.size() - 4, 4, L".dll") != 0) continue;
        const bool own = n.rfind(L"kiln-", 0) == 0;
        if (which == 0 || (which == 1) == own) files.push_back(e.path());
    }
    std::sort(files.begin(), files.end());
    return files;
}

// load_one with what progress.json says: the module's name as the step's detail (and the count, for services), and a
// part when it took 500 ms or more.
void load_reported(const std::filesystem::path& file, const char* kind, const char* step, uint64_t* done, uint64_t total) {
    const std::string name = progress_file::module_name(u8(file.filename()));
    if (total) progress_state::step(step, {}, name, *done, total);
    else progress_state::step(step, {}, name);
    const double t0 = now_ms();
    load_one(file, kind);
    progress_state::part(name + ": starting", static_cast<uint64_t>(now_ms() - t0));
    ++*done;
}

void KILN_CALL run_ready(void* p) {
    auto* m = static_cast<LoadedModule*>(p);
    m->info.on_ready(m->info.user);
}
}  // namespace

const std::vector<PluginRecord>& plugin_records() { return g_plugin_records; }

std::vector<std::string> published_services() {
    std::vector<std::string> out;
    std::lock_guard lock(g_published_mutex);
    for (const auto& p : g_published) {
        const auto* h = static_cast<const KilnServiceHeader*>(p.table);
        if (reg::owner_alive(p.owner)) out.push_back(p.name + "@" + std::to_string(h->version));
    }
    return out;
}

// Services (kiln/services/*.dll, Kiln's own), then with kiln.json "plugins" on and not play-once no-mods: Kiln's own
// plugins (kiln/plugins/kiln-*.dll), the profile's plugin entries, then the plugin drop-ins (the other DLLs in
// kiln/plugins: LEGACY, or a profile with dropins incl. "plugin"). Every on_ready after all kiln_plugin_main calls.
// progress.json: step "services" runs while the service DLLs run kiln_plugin_main (count: service DLLs), step "plugins"
// while the plugins do and then while every module runs on_ready ("Getting Kiln's services and plugins ready", count:
// modules with an on_ready; kiln-vfx builds the mods' materials and VFX there and says so itself).
void load_plugins() {
    const auto services = list_dlls(paths().kiln / L"services");
    uint64_t done = 0;
    for (const auto& file : services) load_reported(file, "service", "services", &done, services.size());
    progress_state::step("services", {}, {}, done, services.size());
    progress_state::step("plugins");
    const auto& prof = profile::state();
    if (!config().plugins) {
        KLOG_I("plugins: off (kiln.json \"plugins\": false)");
    } else if (prof.mode == KILN_PROFILE_NO_MODS) {
        KLOG_I("plugins: none this start (play-once no-mods)");
    } else {
        done = 0;
        for (const auto& file : list_dlls(paths().plugins, 1)) load_reported(file, "plugin", "plugins", &done, 0);
        for (const auto& e : prof.mods[KILN_PROFILE_KIND_PLUGIN]) {
            const std::filesystem::path file = to_wide(e.path);
            if (!e.enabled) {
                KLOG_I("plugin %s: disabled in the profile", u8(file.filename()).c_str());
                record("plugin", file, "skipped", "disabled in the profile");
                continue;
            }
            load_reported(file, "plugin", "plugins", &done, 0);
        }
        if (profile::dropins(KILN_PROFILE_KIND_PLUGIN))
            for (const auto& file : list_dlls(paths().plugins, 2)) load_reported(file, "plugin", "plugins", &done, 0);
    }
    const auto has_ready = [](const LoadedModule& m) {
        return m.info.size >= offsetof(KilnPluginInfo, on_ready) + sizeof(void*) && m.info.on_ready;
    };
    const uint64_t ready_total = static_cast<uint64_t>(std::count_if(g_modules.begin(), g_modules.end(), has_ready));
    uint64_t ready_done = 0;
    if (ready_total) progress_state::step("plugins", "Getting Kiln's services and plugins ready", {}, 0, ready_total);
    for (auto& m : g_modules) {
        if (!has_ready(m)) continue;
        const std::string name = progress_file::module_name(m.name);
        progress_state::step("plugins", {}, name, ready_done, ready_total);
        const double t0 = now_ms();
        unsigned long code = 0;
        if (!guard::task(&run_ready, &m, &code)) KLOG_E("%s: on_ready faulted (0x%08lx)", m.name.c_str(), code);
        progress_state::part(name + ": getting ready", static_cast<uint64_t>(now_ms() - t0));
        ++ready_done;
    }
    if (ready_total) progress_state::step("plugins", {}, {}, ready_done, ready_total);
}

// ---- on_unload at process exit (kiln_api.h, KilnPluginInfo.on_unload) ----
// boot.cpp puts the trigger on the process's exit call (ntdll!RtlExitUserProcess) and calls unload_run there, on the
// way in: before the loader lock is taken, while every thread still runs. Each module of g_modules whose on_unload the
// exit calls is called once, each on a thread of its own while the exiting thread waits: a handler that faults is
// caught (crash report) and one that blocks is given up after its limit, and the next one runs either way. Nothing
// else is torn down (no FreeLibrary, no owner revoke, no hook removed): the process is ending.
//
// Kiln's own exit steps come first, wherever the link is placed (under "auto" not on native Windows in this version):
// kiln.tasks refuses new tasks and seals its owners, Kiln's own services' handlers run (kiln.engine closes its write
// gate for good), then the task pool parks (no body or done starts any more; a bounded wait for the running ones), and
// only then the plugins' handlers, last loaded first.
namespace {

constexpr DWORD kUnloadHandlerMs = 3000;  // kiln-shroudtopia's own handler waits up to 1 s for its worker
constexpr DWORD kUnloadTotalMs = 10000;
constexpr DWORD kUnloadLogWaitMs = 500;   // for the log thread to write the last line (it has no flush)
constexpr DWORD kUnloadFlushMs = 2000;   // includes the last file write, even if the file system stalls
constexpr DWORD kUnloadMarginMs = 1000;
constexpr DWORD kUnloadParkMs = 500;     // the task pool's wait for its running bodies and dones (kiln.tasks)

enum : LONG { kJobIdle, kJobRunning, kJobReturned, kJobFaulted, kJobExited };

// One per handler, for the life of the process: a handler thread that was given up on still points at its job.
struct UnloadJob {
    const char* name;        // g_modules' file name (g_modules is not changed after load_plugins)
    bool own_service;        // Kiln's own service: called with Kiln's exit steps, before the pool parks
    void(KILN_CALL* fn)(void*);
    void* user;
    HANDLE done;             // manual-reset: the handler returned, faulted, or called the exit itself
    volatile DWORD thread;   // the handler thread's id, written by that thread before the call
    volatile LONG state;
    unsigned long code;      // the exception code when it faulted
    double ms;
};
UnloadJob* g_unload_jobs = nullptr;  // reverse load order
size_t g_unload_count = 0;
DWORD g_unload_handler_ms = kUnloadHandlerMs, g_unload_total_ms = kUnloadTotalMs;
std::atomic<int> g_unload_state{0};  // 0 not run, 1 running, 2 over
std::atomic<double> g_unload_started_ms{-1};  // published before state 1, shared by all exit callers
HANDLE g_unload_over = nullptr;      // manual-reset, set when the sequence is over

// KILN_UNLOAD_HANDLER_MS / KILN_UNLOAD_TOTAL_MS: shorter limits (for tests); a value above the default is ignored.
DWORD unload_limit(const wchar_t* name, DWORD fallback) {
    wchar_t text[16] = {};
    const DWORD n = GetEnvironmentVariableW(name, text, 16);
    if (n == 0 || n >= 16) return fallback;
    wchar_t* end = nullptr;
    const unsigned long v = std::wcstoul(text, &end, 10);
    if (end == text || *end || v == 0 || v > fallback) {
        KLOG_W("unload: %ls=%ls ignored (1..%lu ms)", name, text, fallback);
        return fallback;
    }
    return static_cast<DWORD>(v);
}

// No C++ objects with destructors here (__try).
bool call_unload_guarded(void(KILN_CALL* fn)(void*), void* user, unsigned long* code) {
    crash::Capture cap{};
    __try {
        fn(user);
        return true;
    } __except (crash::filter(GetExceptionInformation(), &cap, code)) {
        crash::report(cap, "plugin on_unload at process exit", reinterpret_cast<const void*>(fn));
        return false;
    }
}

DWORD WINAPI unload_thread(void* p) {
    auto* j = static_cast<UnloadJob*>(p);
    j->thread = GetCurrentThreadId();
    unsigned long code = 0;
    const double t0 = now_ms();
    const bool ok = call_unload_guarded(j->fn, j->user, &code);
    j->ms = now_ms() - t0;
    j->code = code;
    InterlockedCompareExchange(&j->state, ok ? kJobReturned : kJobFaulted, kJobRunning);
    SetEvent(j->done);
    return 0;
}

// PEB.LoaderLock (x64 offset 0x110, the same on Windows and Wine) is held by this thread: an exit call from a DllMain.
bool holds_loader_lock() {
    const auto* peb = reinterpret_cast<const uint8_t*>(__readgsqword(0x60));
    const auto* cs = *reinterpret_cast<RTL_CRITICAL_SECTION* const*>(peb + 0x110);
    return cs && reinterpret_cast<uintptr_t>(cs->OwningThread) == GetCurrentThreadId();
}

DWORD WINAPI unload_flush_thread(void*) { return caps::flush() ? 0 : 1; }

// flush() bounds lock acquisition, but the file write itself can stall. Keep it off the exiting thread and include
// its whole wait in the sequence's bound. A worker still writing is left to the OS when the process ends.
bool unload_flush() {
    HANDLE th = CreateThread(nullptr, 0, &unload_flush_thread, nullptr, 0, nullptr);
    if (!th) return false;
    DWORD code = 1;
    const bool ok = WaitForSingleObject(th, kUnloadFlushMs) == WAIT_OBJECT_0 && GetExitCodeThread(th, &code) && code == 0;
    CloseHandle(th);
    return ok;
}

bool kiln_log_has(std::string_view needle) {
    HANDLE h = CreateFileW((paths().logs / L"kiln.log").c_str(), GENERIC_READ,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return true;  // no kiln.log to wait for
    LARGE_INTEGER size{}, at{};
    GetFileSizeEx(h, &size);
    const LONGLONG take = std::min<LONGLONG>(size.QuadPart, 16384);
    at.QuadPart = size.QuadPart - take;
    SetFilePointerEx(h, at, nullptr, FILE_BEGIN);
    std::string tail(static_cast<size_t>(take), '\0');
    DWORD got = 0;
    ReadFile(h, tail.data(), static_cast<DWORD>(take), &got, nullptr);
    CloseHandle(h);
    tail.resize(got);
    return tail.find(needle) != std::string::npos;
}

// The last line has been logged; the process ends right after this returns and the log thread's queue goes with it
// (log.cpp has no flush). Waits, bounded, until that line is in kiln.log: the thread writes in order, so every line
// before it is there too.
void unload_finish(int level, const std::string& last) {
    log::write(level, "kiln", last);
    if (level <= config().log_level) {
        const double until = now_ms() + kUnloadLogWaitMs;
        while (!kiln_log_has(last) && now_ms() < until) Sleep(5);
    }
    g_unload_state.store(2);
    SetEvent(g_unload_over);
}

}  // namespace

namespace {
bool has_unload(const LoadedModule& m) {
    return m.info.size >= offsetof(KilnPluginInfo, on_unload) + sizeof(void*) && m.info.on_unload;
}
}  // namespace

// The modules with an on_unload: Kiln's own services (`own_services`) or the others.
size_t unload_handlers(bool own_services) {
    return static_cast<size_t>(std::count_if(g_modules.begin(), g_modules.end(), [&](const LoadedModule& m) {
        return has_unload(m) && m.own_service == own_services;
    }));
}

// Called by boot.cpp before it arms the trigger: one job per module whose on_unload the exit calls, last loaded first:
// Kiln's own services always, the plugins only with `plugins`. Returns the number of jobs; unload_prepared() says
// whether the sequence exists (it may have no job: Kiln's own steps still run).
size_t unload_prepare(bool plugins) {
    if (g_unload_over) return g_unload_count;
    size_t n = 0;
    for (const auto& m : g_modules)
        if (has_unload(m) && (plugins || m.own_service)) ++n;
    g_unload_over = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!g_unload_over) return 0;
    g_unload_jobs = new UnloadJob[n ? n : 1]{};
    for (size_t i = g_modules.size(); i-- > 0;) {
        const auto& m = g_modules[i];
        if (!has_unload(m) || (!plugins && !m.own_service)) continue;
        UnloadJob& j = g_unload_jobs[g_unload_count];
        j.name = m.name.c_str();
        j.own_service = m.own_service;
        j.fn = m.info.on_unload;
        j.user = m.info.user;
        j.done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (j.done) ++g_unload_count;
    }
    g_unload_handler_ms = unload_limit(L"KILN_UNLOAD_HANDLER_MS", kUnloadHandlerMs);
    g_unload_total_ms = unload_limit(L"KILN_UNLOAD_TOTAL_MS", kUnloadTotalMs);
    return g_unload_count;
}

DWORD unload_handler_ms() { return g_unload_handler_ms; }
DWORD unload_total_ms() { return g_unload_total_ms; }

// boot.cpp's catch: a fault in the sequence itself must also release every other exit caller.
void unload_abandon() noexcept {
    int running = 1;
    if (g_unload_state.compare_exchange_strong(running, 2)) SetEvent(g_unload_over);
}

namespace {
// One run of the handlers: their time, what was called, and how each ended.
struct UnloadCalls {
    double t0 = 0;  // the handlers' clock: the sequence's start, moved on by the time the task pool's park took
    std::string calls;
    unsigned faulted = 0, given_up = 0, skipped = 0;
};
enum class Pick { OwnServices, Plugins };
bool picked(const UnloadJob& j, Pick pick) { return (pick == Pick::OwnServices) == j.own_service; }
size_t own_service_jobs() {
    size_t n = 0;
    for (size_t i = 0; i < g_unload_count; ++i) n += g_unload_jobs[i].own_service;
    return n;
}

// Calls the jobs that `pick` selects, in list order (last loaded first), each on its own thread within the limits.
void call_jobs(UnloadCalls& c, Pick pick) {
    char part[160];
    for (size_t i = 0; i < g_unload_count; ++i) {
        UnloadJob& j = g_unload_jobs[i];
        if (!picked(j, pick)) continue;
        const double used = now_ms() - c.t0;
        if (used >= g_unload_total_ms) {
            std::string rest;
            unsigned n = 0;
            for (size_t k = i; k < g_unload_count; ++k)
                if (picked(g_unload_jobs[k], pick)) {
                    rest += std::string(rest.empty() ? "" : ", ") + g_unload_jobs[k].name;
                    ++n;
                }
            KLOG_W("unload: the %lu ms for all handlers are used up; not called: %s", g_unload_total_ms, rest.c_str());
            c.skipped += n;
            break;
        }
        const DWORD wait = std::min<DWORD>(g_unload_handler_ms, static_cast<DWORD>(g_unload_total_ms - used));
        InterlockedExchange(&j.state, kJobRunning);
        HANDLE th = CreateThread(nullptr, 0, &unload_thread, &j, 0, nullptr);
        if (!th) {
            InterlockedExchange(&j.state, kJobIdle);
            KLOG_E("unload: %s: no thread for its on_unload (error %lu); not called", j.name, GetLastError());
            ++c.skipped;
            continue;
        }
        const DWORD w = WaitForSingleObject(j.done, wait);
        CloseHandle(th);
        const LONG state = j.state;
        if (w == WAIT_OBJECT_0 && state == kJobReturned) {
            std::snprintf(part, sizeof part, "%s%s %.1f ms", c.calls.empty() ? "" : ", ", j.name, j.ms);
        } else if (w == WAIT_OBJECT_0 && state == kJobFaulted) {
            KLOG_E("unload: %s: on_unload faulted (0x%08lx); crash report in kiln/crash-reports; the next one runs",
                   j.name, j.code);
            ++c.faulted;
            std::snprintf(part, sizeof part, "%s%s faulted", c.calls.empty() ? "" : ", ", j.name);
        } else if (w == WAIT_OBJECT_0 && state == kJobExited) {
            KLOG_W("unload: %s: its on_unload ended the process itself; the sequence goes on first", j.name);
            std::snprintf(part, sizeof part, "%s%s exited", c.calls.empty() ? "" : ", ", j.name);
        } else {
            KLOG_E("unload: %s: on_unload has not returned after %lu ms; given up (its thread ends with the process), "
                   "the next one runs",
                   j.name, wait);
            ++c.given_up;
            std::snprintf(part, sizeof part, "%s%s given up", c.calls.empty() ? "" : ", ", j.name);
        }
        c.calls += part;
    }
}
}  // namespace

bool unload_prepared() { return g_unload_over != nullptr; }

// The trigger: the process is exiting with `exit_code` (through `via`). Returns when the caller may go on into the
// real exit.
void unload_run(uint32_t exit_code, const char* via) {
    // kiln.tasks first. Every exit closes its admission with one lock-free store (also a crash's: a stranded lock or
    // heap is never touched); a normal exit also cancels the queued tasks and signals the running ones (model lock).
    // Neither waits; the pool's park, later in the sequence, is the one bounded wait for its running callbacks.
    tasks_svc::exit_begins();
    if (exit_code == 0) tasks_svc::exiting();
    if (!g_unload_over) return;
    if (exit_code != 0) {
        // A crash may have stranded the heap or the logger's mutex. Do not allocate, log, poll, or wait, even when
        // another thread is already running the normal sequence. The game's non-zero exits must reach the OS now.
        g_unload_state.store(2);
        SetEvent(g_unload_over);
        return;
    }
    double unstarted = -1;
    g_unload_started_ms.compare_exchange_strong(unstarted, now_ms());
    int was = 0;
    if (!g_unload_state.compare_exchange_strong(was, 1)) {
        // Once per process. A handler that ends the process from its on_unload is done (the sequence goes on); any
        // other exit call during the sequence waits for it, then goes on into the exit.
        const DWORD me = GetCurrentThreadId();
        for (size_t i = 0; i < g_unload_count; ++i) {
            UnloadJob& j = g_unload_jobs[i];
            if (j.thread == me && InterlockedCompareExchange(&j.state, kJobExited, kJobRunning) == kJobRunning) {
                SetEvent(j.done);
                break;
            }
        }
        if (was == 1) {
            const double bound =
                g_unload_total_ms + kUnloadParkMs + kUnloadFlushMs + kUnloadLogWaitMs + kUnloadMarginMs;
            const double left = bound - (now_ms() - g_unload_started_ms.load());
            WaitForSingleObject(g_unload_over, static_cast<DWORD>(std::max(0.0, left)));
        }
        return;
    }
    char line[256];
    if (holds_loader_lock()) {
        std::snprintf(line, sizeof line,
                      "unload: the process exits from under the loader lock (%s, thread %lu: a DllMain called it): no "
                      "on_unload handler is called (their threads could not start) and the task pool is not parked "
                      "(nothing waits under the loader lock)",
                      via, GetCurrentThreadId());
        unload_finish(KILN_LOG_WARN, line);
        return;
    }
    UnloadCalls c;
    c.t0 = g_unload_started_ms.load();
    KLOG_I("unload: the process is exiting (%s, exit code 0, thread %lu): Kiln's own exit steps (%zu service "
           "handler(s), then the task pool parks within %lu ms), then %zu plugin on_unload handler(s), last loaded "
           "first, each on its own thread, up to %lu ms each and %lu ms in all",
           via, GetCurrentThreadId(), own_service_jobs(), kUnloadParkMs, g_unload_count - own_service_jobs(),
           g_unload_handler_ms, g_unload_total_ms);
    call_jobs(c, Pick::OwnServices);  // kiln.engine closes its write gate for good: no task can take it any more
    const double park0 = now_ms();
    tasks_svc::park(kUnloadParkMs);   // no task body or done runs from here; the plugins' handlers come after
    c.t0 += now_ms() - park0;         // the park's wait is not the handlers' time
    call_jobs(c, Pick::Plugins);
    // The handlers may have reported rows (kiln.caps): capabilities.json is written a last time when something is
    // dirty. Its worker's whole wait is bounded at 2 s, outside the handlers' limits.
    if (!unload_flush()) KLOG_W("unload: capabilities.json could not be written a last time");
    std::snprintf(line, sizeof line,
                  "unload: done in %.0f ms (%u faulted, %u given up, %u not called); the process exits",
                  now_ms() - g_unload_started_ms.load(), c.faulted, c.given_up, c.skipped);
    KLOG_I("unload: called in this order: %s", c.calls.c_str());
    unload_finish(c.faulted || c.given_up || c.skipped ? KILN_LOG_WARN : KILN_LOG_INFO, line);
}

}  // namespace kiln
