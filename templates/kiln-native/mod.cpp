// SPDX-License-Identifier: MIT
// KilnNative1: Kiln calls kiln_native_init once, right after loading the DLL (phase "boot", the game's main thread),
// with the Kiln API, an owner for everything it registers, its folder, a config folder and a data folder.
// Everything registered with ctx->owner is revoked when init fails or faults. Kiln never unloads a native mod.
#include <cstddef>
#include <cstdio>

#include "kiln_native.h"

namespace {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
unsigned long long g_steps = 0;

void say(const char* text) {
    size_t n = 0;
    while (text[n]) ++n;
    g_api->log(g_owner, KILN_LOG_INFO, KilnStr{text, n});
}

// Server tick (the world's thread), before the sim step.
void KILN_CALL on_pre(const KilnTickInfo* tick, void*) {
    if (++g_steps % 1470 == 0) {  // about once a minute on a dedicated server (24.5 steps/s)
        char buf[128];
        std::snprintf(buf, sizeof buf, "KilnNative1: step %llu", static_cast<unsigned long long>(tick->step));
        say(buf);
    }
}
}  // namespace

extern "C" __declspec(dllexport) int KILN_CALL kiln_native_init(const KilnNativeContext* ctx) {
    if (ctx->size < sizeof(KilnNativeContext)) return KILN_NATIVE_INIT_UNSUPPORTED;  // an older Kiln core
    g_api = ctx->api;
    g_owner = ctx->owner;
    char buf[512];
    std::snprintf(buf, sizeof buf, "KilnNative1 %.*s loaded (%s, hooks %s); data in %.*s", static_cast<int>(ctx->version.len),
                  ctx->version.ptr, ctx->side == KILN_SIDE_CLIENT ? "client" : "server", ctx->hooks_enabled ? "on" : "off",
                  static_cast<int>(ctx->data_dir.len), ctx->data_dir.ptr);
    say(buf);
    // Services and ticks need hooks (phase boot or later, not in safe idle): ctx->api->service(KilnStr{"kiln.players", 12}, 1), ...
    KilnHandle h = 0;
    if (ctx->hooks_enabled && g_api->subscribe_tick(g_owner, KILN_TICK_PRE, &on_pre, nullptr, &h) == KILN_OK)
        say("KilnNative1: subscribed to the server tick");
    return KILN_NATIVE_INIT_OK;
}
