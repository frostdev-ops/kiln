// SPDX-License-Identifier: MIT
// Early plugins: Kiln plugins (<game>/kiln/plugins/kiln-*.dll) that also export `kiln_plugin_early` run right after the
// native mods of phase "early": before sigdb and hooks, in safe idle too, still before any game code. For work that
// must finish before the game reads its data files and needs no hooks, such as EML's pre-game asset patching
// (gpl/kiln-eml, docs/kiln/eml.md). dev.21: kiln.hooks v2 answers api->service() for them already (verified build or
// policy any; hooks::serve_early in boot.cpp), for plugins that hook game code while they initialise (kiln-shroudforge).
//
// Which DLLs: kiln-*.dll whose PE export directory defines "kiln_plugin_early" (checked through a read-only mapping,
// so other plugins are not loaded early). Not with kiln.json "plugins": false, play-once no-mods or an invalid profile.
// Order: every early DLL is loaded first (file-name order), then every kiln_plugin_early, then every on_ready, so one
// early plugin can find another's exports in its kiln_plugin_early (GetModuleHandle + GetProcAddress) whatever the
// names. The same DLL is loaded again later as a normal plugin (kiln_plugin_main at the plugin step, when hooks are on).
// A fault or a result other than KILN_OK revokes the plugin's owner; the game goes on. Signature: KilnPluginMainFn.
#include "early_plugins.h"

#include <algorithm>
#include <cstring>
#include <filesystem>
#include <string>
#include <vector>

#include "api.h"
#include "config.h"
#include "guard.h"
#include "log.h"
#include "profile.h"
#include "registry.h"

namespace kiln::early_plugins {
namespace {

constexpr char kExport[] = "kiln_plugin_early";

// Parse a bounded, read-only PE file view.
bool has_early_export(const std::filesystem::path& file) {
    HANDLE f = CreateFileW(file.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER size{};
    bool found = false;
    if (GetFileSizeEx(f, &size) && size.QuadPart > 0 && size.QuadPart < (1ll << 31)) {
        if (HANDLE m = CreateFileMappingW(f, nullptr, PAGE_READONLY, 0, 0, nullptr)) {
            if (const auto* b = static_cast<const char*>(MapViewOfFile(m, FILE_MAP_READ, 0, 0, 0))) {
                found = exports_early(reinterpret_cast<const uint8_t*>(b), static_cast<size_t>(size.QuadPart));
                UnmapViewOfFile(b);
            }
            CloseHandle(m);
        }
    }
    CloseHandle(f);
    return found;
}

struct Early {
    std::string name;
    HMODULE module = nullptr;
    KilnPluginMainFn main = nullptr;
    KilnOwner owner = 0;
    KilnPluginInfo info{};
    bool ok = false;
};

void KILN_CALL run_ready(void* p) {
    auto* e = static_cast<Early*>(p);
    e->info.on_ready(e->info.user);
}

}  // namespace

void run() {
    const auto& prof = profile::state();
    if (!config().plugins || prof.mode == KILN_PROFILE_NO_MODS || prof.mode == KILN_PROFILE_INVALID) return;
    std::error_code ec;
    if (!std::filesystem::is_directory(paths().plugins, ec)) return;
    std::vector<std::filesystem::path> files;
    for (auto it = std::filesystem::directory_iterator(paths().plugins, ec);
         !ec && it != std::filesystem::directory_iterator(); it.increment(ec)) {
        const auto& e = *it;
        if (!e.is_regular_file(ec)) continue;
        std::wstring n = e.path().filename().wstring();
        CharLowerBuffW(n.data(), static_cast<DWORD>(n.size()));
        if (n.size() < 10 || n.rfind(L"kiln-", 0) != 0 || n.compare(n.size() - 4, 4, L".dll") != 0) continue;
        if (has_early_export(e.path())) files.push_back(e.path());
    }
    if (files.empty()) return;
    std::sort(files.begin(), files.end());
    static std::vector<Early> early;  // lives for the process (on_ready and the plugins keep pointers into it)
    if (!early.empty()) return; // one boot per process; do not invalidate callback storage
    early.reserve(files.size());
    for (const auto& file : files) {
        Early e;
        e.name = to_utf8(file.filename().wstring());
        e.module = LoadLibraryExW(file.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!e.module) {
            KLOG_E("early plugin %s: LoadLibrary failed (%lu)", e.name.c_str(), GetLastError());
            continue;
        }
        e.main = reinterpret_cast<KilnPluginMainFn>(GetProcAddress(e.module, kExport));
        if (!e.main) {
            KLOG_W("early plugin %s: no %s export after all; skipped", e.name.c_str(), kExport);
            FreeLibrary(e.module);
            continue;
        }
        early.push_back(std::move(e));
    }
    // EML must bind its API/profile roots before peer collectors (kiln-emf sorts before kiln-eml).
    // All DLLs are already loaded, so peers can still find exports independently of this initialization order.
    std::stable_sort(early.begin(), early.end(), [](const Early& a, const Early& b) {
        return provider_before(a.name, b.name);
    });
    for (auto& e : early) {
        e.owner = reg::owner_create("early:" + e.name);
        e.info.size = sizeof e.info;
        KilnResult rc = KILN_ERR_INTERNAL;
        unsigned long code = 0;
        if (!guard::plugin_main(e.main, api(), e.owner, &e.info, &rc, &code)) {
            KLOG_E("early plugin %s: %s faulted (0x%08lx); revoked", e.name.c_str(), kExport, code);
            reg::owner_revoke(e.owner);
            continue;
        }
        if (rc != KILN_OK) {
            if (rc == KILN_ERR_UNAVAILABLE)
                // UNAVAILABLE: nothing to do before the game starts (kiln-shroudlink has no EML mods to hand over, for
                // example). Such a plugin often logs nothing here; its plugin step runs later as usual.
                KLOG_I("early plugin %s: no early work this start; its plugin step runs later as usual", e.name.c_str());
            else
                KLOG_W("early plugin %s: %s returned %d; revoked", e.name.c_str(), kExport, rc);
            reg::owner_revoke(e.owner);
            continue;
        }
        // Copy bounded identity strings under the fault guard before logging plugin-owned pointers.
        if (e.info.size < offsetof(KilnPluginInfo, on_unload) || e.info.size > sizeof e.info) {
            KLOG_E("early plugin %s: invalid info size; revoked", e.name.c_str());
            reg::owner_revoke(e.owner);
            continue;
        }
        char id[257]{}, version[129]{};
        if (e.info.id.len > 256 || e.info.version.len > 128 ||
            (e.info.id.len && !guard::copy(id, reinterpret_cast<uintptr_t>(e.info.id.ptr), e.info.id.len)) ||
            (e.info.version.len && !guard::copy(version, reinterpret_cast<uintptr_t>(e.info.version.ptr), e.info.version.len))) {
            KLOG_E("early plugin %s: invalid identity; revoked", e.name.c_str());
            reg::owner_revoke(e.owner);
            continue;
        }
        e.ok = true;
        KLOG_I("early plugin %s: %s %s", e.name.c_str(), id, version);
    }
    for (auto& e : early) {
        if (!e.ok || e.info.size < offsetof(KilnPluginInfo, on_ready) + sizeof(void*) || !e.info.on_ready) continue;
        unsigned long code = 0;
        if (!guard::task(&run_ready, &e, &code)) {
            KLOG_E("early plugin %s: on_ready faulted (0x%08lx); revoked", e.name.c_str(), code);
            e.ok = false;
            reg::owner_revoke(e.owner);
        }
    }
}

}  // namespace kiln::early_plugins
