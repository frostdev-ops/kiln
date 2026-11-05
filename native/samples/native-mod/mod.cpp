// SPDX-License-Identifier: MIT
// The smallest Kiln-aware native mod (docs/kiln/native-mods.md). native.json names this export in "init"; Kiln calls
// it once, right after loading the DLL, on the game's main thread, with everything a mod needs: the Kiln API, an owner
// for its registrations, its folder, a config folder and a data folder.
#include <cstddef>
#include <cstdio>

#include "kiln_native.h"
#include "services/kiln_profile.h"

namespace {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
unsigned long long g_steps = 0;

void say(const char* text) {
    size_t n = 0;
    while (text[n]) ++n;
    g_api->log(g_owner, KILN_LOG_INFO, KilnStr{text, n});
}

void KILN_CALL on_pre(const KilnTickInfo* tick, void*) {
    if (++g_steps == 100) {
        char buf[128];
        std::snprintf(buf, sizeof buf, "100 server steps seen (step %llu)", static_cast<unsigned long long>(tick->step));
        say(buf);
    }
}
}  // namespace

extern "C" __declspec(dllexport) int KILN_CALL kiln_native_init(const KilnNativeContext* ctx) {
    if (ctx->size < sizeof(KilnNativeContext)) return KILN_NATIVE_INIT_UNSUPPORTED;  // an older core
    g_api = ctx->api;
    g_owner = ctx->owner;
    char buf[512];
    std::snprintf(buf, sizeof buf, "hello from %.*s %.*s (%s, hooks %s); config in %.*s", static_cast<int>(ctx->id.len),
                  ctx->id.ptr, static_cast<int>(ctx->version.len), ctx->version.ptr,
                  ctx->side == KILN_SIDE_CLIENT ? "client" : "server", ctx->hooks_enabled ? "on" : "off",
                  static_cast<int>(ctx->config_dir.len), ctx->config_dir.ptr);
    say(buf);
    // Services work from phase boot on, when hooks are on. Everything registered belongs to ctx->owner.
    KilnHandle h = 0;
    if (ctx->hooks_enabled && g_api->subscribe_tick(g_owner, KILN_TICK_PRE, &on_pre, nullptr, &h) == KILN_OK)
        say("subscribed to the server tick");
    return KILN_NATIVE_INIT_OK;
}
