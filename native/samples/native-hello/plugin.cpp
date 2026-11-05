// SPDX-License-Identifier: MIT
// Smallest useful native Kiln plugin: logs the tick rate every ~60 s and counts game log lines.
// Install: <game>/kiln/plugins/kiln-native-hello.dll
#include <cstddef>
#include <cstdio>

#include "kiln_api.h"

namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
unsigned long long g_lines = 0;  // written on Kiln's log thread, read on the tick thread (approximate is fine)

void say(const char* text) {
    size_t n = 0;
    while (text[n]) ++n;
    g_api->log(g_owner, KILN_LOG_INFO, KilnStr{text, n});
}

void KILN_CALL on_pre(const KilnTickInfo* tick, void*) {
    if (tick->step == 50 && g_api->size >= offsetof(KilnApi, singleton) + sizeof(void*)) {
        const uintptr_t world = g_api->server_world();
        char buf[200];
        std::snprintf(buf, sizeof buf, "server world %p, ServerPlayers singleton %p", reinterpret_cast<void*>(world),
                      reinterpret_cast<void*>(g_api->singleton(world, KilnStr{"ServerPlayers", 13})));
        say(buf);
    }
    if (tick->step % 1470 != 0) return;  // ~60 s at 24.5 Hz
    char buf[200];
    std::snprintf(buf, sizeof buf, "step %llu clock %u (+%u) wall %.2f ms, %llu game log lines so far",
                  static_cast<unsigned long long>(tick->step), tick->clock, tick->clock_delta, tick->wall_delta_ms,
                  g_lines);
    say(buf);
}

void KILN_CALL on_line(int32_t, KilnStr, void*) { ++g_lines; }

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{"native-hello", 12};
    info->version = KilnStr{"0.1.0", 5};
    KilnHandle h = 0;
    const KilnResult tick = api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h);
    const KilnResult log = api->subscribe_log(owner, &on_line, nullptr, &h);
    char buf[120];
    std::snprintf(buf, sizeof buf, "hello from a native plugin (tick: %d, log mirror: %d)", tick, log);
    say(buf);
    return KILN_OK;
}
