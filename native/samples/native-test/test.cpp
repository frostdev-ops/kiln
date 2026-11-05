// SPDX-License-Identifier: MIT
// native-test: a Kiln-aware native mod for the loader's lab checks (mods/tests/native-test). Logs
//   native-test <id>: <check> PASS|FAIL ...
// for: the context (size, abi, phase vs native.json, id, folders exist), kiln.profile (answered in every phase, also
// safe idle), hooks per phase, and in phase world: on the world's thread with the world live.
#include <cstddef>
#include <cstdio>
#include <cstring>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include "kiln_native.h"
#include "services/kiln_profile.h"

namespace {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
char g_id[96];

void line(bool ok, const char* check, const char* fmt = "", ...) {
    char detail[400];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(detail, sizeof detail, fmt, ap);
    va_end(ap);
    char buf[640];
    const int n = std::snprintf(buf, sizeof buf, "native-test %s: %s %s%s%s", g_id, check, ok ? "PASS" : "FAIL",
                                detail[0] ? " " : "", detail);
    g_api->log(g_owner, ok ? KILN_LOG_INFO : KILN_LOG_ERROR, KilnStr{buf, static_cast<size_t>(n)});
}

bool ends_with(const char* s, const char* suffix) {
    const size_t n = std::strlen(s), m = std::strlen(suffix);
    return n >= m && std::strcmp(s + n - m, suffix) == 0;
}

bool is_dir(KilnStr s) {
    wchar_t w[1024];
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.ptr, static_cast<int>(s.len), w, 1023);
    w[n > 0 ? n : 0] = 0;
    const DWORD a = GetFileAttributesW(w);
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY);
}

const char* phase_name(uint32_t p) {
    static const char* const k[] = {"dllmain", "early", "boot", "world"};
    return p < 4 ? k[p] : "?";
}
}  // namespace

extern "C" __declspec(dllexport) int KILN_CALL kiln_native_init(const KilnNativeContext* ctx) {
    g_api = ctx->api;
    g_owner = ctx->owner;
    const size_t n = ctx->id.len < sizeof g_id - 1 ? ctx->id.len : sizeof g_id - 1;
    std::memcpy(g_id, ctx->id.ptr, n);
    g_id[n] = 0;
    line(ctx->size >= sizeof(KilnNativeContext) && ctx->abi_version == KILN_NATIVE_ABI_VERSION, "context",
         "size %u abi %u", ctx->size, ctx->abi_version);
    // the id's middle part names the phase it was declared for: native-test-<phase>[-...]
    char expected[16] = {};
    std::sscanf(g_id, "native-test-%15[a-z]", expected);
    bool named = false;  // only ids that name a phase are checked (native-test-boot-a, not native-test-copied)
    for (uint32_t p = 0; p < 4; ++p) named |= std::strcmp(expected, phase_name(p)) == 0;
    line(!named || std::strcmp(expected, phase_name(ctx->phase)) == 0, "phase", "%s (declared %s)",
         phase_name(ctx->phase), named ? expected : "-");
    line(is_dir(ctx->directory) && is_dir(ctx->config_dir) && is_dir(ctx->data_dir), "folders", "dir %.*s config %.*s data %.*s",
         static_cast<int>(ctx->directory.len), ctx->directory.ptr, static_cast<int>(ctx->config_dir.len),
         ctx->config_dir.ptr, static_cast<int>(ctx->data_dir.len), ctx->data_dir.ptr);
    const auto* prof = static_cast<const KilnProfileApi*>(g_api->service(KilnStr{KILN_PROFILE_SERVICE, 12}, 1));
    if (prof) {
        static const char* const modes[] = {"legacy", "active", "once", "no-mods", "invalid"};
        const uint32_t mode = prof->mode();
        const KilnStr id = prof->id();
        line(true, "kiln.profile", "mode %s id '%.*s' generation %llu native %u", mode < 5 ? modes[mode] : "?",
             static_cast<int>(id.len), id.ptr, static_cast<unsigned long long>(prof->generation()),
             prof->count(KILN_PROFILE_KIND_NATIVE));
    } else {
        line(false, "kiln.profile", "service missing");
    }
    // hooks: off in early (nothing is hooked yet), as Kiln decided from boot on
    KilnBuildInfo b{};
    b.size = sizeof b;
    g_api->build_info(&b);
    line(ctx->phase != KILN_NATIVE_PHASE_EARLY || ctx->hooks_enabled == 0, "hooks", "ctx %u, core %u (%s build)",
         ctx->hooks_enabled, b.hooks_enabled, b.verified ? "verified" : "unverified");
    if (ctx->phase == KILN_NATIVE_PHASE_WORLD) {
        const uint32_t kind = ctx->side == KILN_SIDE_CLIENT ? KILN_WORLD_CLIENT : KILN_WORLD_SERVER;
        line(g_api->world(kind) != 0 && g_api->on_world_thread(kind), "world", "world %p, on its thread %d",
             reinterpret_cast<void*>(g_api->world(kind)), g_api->on_world_thread(kind) ? 1 : 0);
    }
    if (ends_with(g_id, "-fail")) return KILN_NATIVE_INIT_FAILED;
    if (ends_with(g_id, "-unsupported")) return KILN_NATIVE_INIT_UNSUPPORTED;
    if (ends_with(g_id, "-fault")) {
        volatile int* p = nullptr;
        *p = 1;  // SEH: Kiln marks the mod failed and runs on
    }
    return KILN_NATIVE_INIT_OK;
}
