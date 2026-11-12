// SPDX-License-Identifier: MIT
// The KilnApi table and native plugin loading.
#include "api.h"

#include <algorithm>
#include <cstddef>
#include <cstring>
#include <mutex>
#include <vector>

#include "guard.h"
#include "image.h"
#include "log.h"
#include "profile.h"
#include "progress.h"
#include "registry.h"
#include "sigdb.h"
#include "config.h"
#include "tick.h"
#include "worldinfo.h"
#include "services/kiln_caps.h"
#include "services/kiln_hooks.h"
#include "services/service.h"

// kiln.caps, the capability registry (services/caps.cpp)
namespace kiln::caps {
const KilnCapsApi* table();
void owner_gone(KilnOwner owner);
void feature_off(std::string_view name);
void service_published(KilnOwner owner, std::string_view name, uint32_t version);
void service_dll(const std::filesystem::path& file, std::string_view status, const std::string& error);
}  // namespace kiln::caps

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
    const bool on = g_hooks && sigdb::feature(n) && (n.substr(0, 4) != "tick" || tick::installed()) &&
                    (n != "world.lifecycle" || tick::lifecycle());
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

struct Published {
    KilnOwner owner;
    std::string name;
    const void* table;
};
std::mutex g_published_mutex;
std::vector<Published> g_published;

const void* KILN_CALL api_service(KilnStr name, uint32_t min_version) {
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

KilnResult KILN_CALL api_register_service(KilnOwner owner, KilnStr name, const void* table) {
    if (!table || !name.ptr || name.len == 0) return KILN_ERR_INVALID_ARGUMENT;
    const auto* h = static_cast<const KilnServiceHeader*>(table);
    if (h->size < sizeof(KilnServiceHeader)) return KILN_ERR_INVALID_ARGUMENT;
    {
        std::lock_guard lock(g_published_mutex);
        for (const auto& p : g_published)
            if (p.name == sv(name)) return KILN_ERR_INVALID_ARGUMENT;
        g_published.push_back({owner, std::string(sv(name)), table});
    }
    KLOG_I("service %.*s v%u published by %s", static_cast<int>(name.len), name.ptr, h->version,
           reg::owner_name(owner).c_str());
    caps::service_published(owner, sv(name), h->version);  // unlocked: its subscribers may call api_service
    return KILN_OK;
}

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

}  // namespace

const KilnApi* api() { return &g_api; }

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
    KilnPluginInfo info{};
    info.size = sizeof info;
    KilnResult rc = KILN_ERR_INTERNAL;
    unsigned long code = 0;
    if (!guard::plugin_main(main, &g_api, owner, &info, &rc, &code)) {
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
    g_modules.push_back({name, info, h});
}

// `which`: 0 every DLL, 1 Kiln's own (kiln-*.dll), 2 the others (drop-ins).
void load_dir(const std::filesystem::path& dir, const char* kind, int which = 0) {
    std::error_code ec;
    if (!std::filesystem::is_directory(dir, ec)) return;
    std::vector<std::filesystem::path> files;
    for (auto& e : std::filesystem::directory_iterator(dir, ec)) {
        std::wstring n = e.path().filename().wstring();
        CharLowerBuffW(n.data(), static_cast<DWORD>(n.size()));
        // Windows file names are case-insensitive: MyPlugin.DLL is a DLL too
        if (n.size() < 5 || n.compare(n.size() - 4, 4, L".dll") != 0) continue;
        const bool own = n.rfind(L"kiln-", 0) == 0;
        if (which == 0 || (which == 1) == own) files.push_back(e.path());
    }
    std::sort(files.begin(), files.end());
    for (const auto& file : files) load_one(file, kind);
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
void load_plugins() {
    load_dir(paths().kiln / L"services", "service");
    const auto& prof = profile::state();
    if (!config().plugins) {
        KLOG_I("plugins: off (kiln.json \"plugins\": false)");
    } else if (prof.mode == KILN_PROFILE_NO_MODS) {
        KLOG_I("plugins: none this start (play-once no-mods)");
    } else {
        load_dir(paths().plugins, "plugin", 1);
        for (const auto& e : prof.mods[KILN_PROFILE_KIND_PLUGIN]) {
            const std::filesystem::path file = to_wide(e.path);
            if (!e.enabled) {
                KLOG_I("plugin %s: disabled in the profile", u8(file.filename()).c_str());
                record("plugin", file, "skipped", "disabled in the profile");
                continue;
            }
            load_one(file, "plugin");
        }
        if (profile::dropins(KILN_PROFILE_KIND_PLUGIN)) load_dir(paths().plugins, "plugin", 2);
    }
    for (auto& m : g_modules) {
        if (m.info.size < offsetof(KilnPluginInfo, on_ready) + sizeof(void*) || !m.info.on_ready) continue;
        unsigned long code = 0;
        if (!guard::task(&run_ready, &m, &code)) KLOG_E("%s: on_ready faulted (0x%08lx)", m.name.c_str(), code);
    }
}

}  // namespace kiln
