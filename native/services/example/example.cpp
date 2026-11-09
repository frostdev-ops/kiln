// SPDX-License-Identifier: MIT
// Template service (kiln.example). A service is a DLL in kiln/services/ that:
//  1. in kiln_plugin_main: checks the ABI, resolves what it needs (api->symbol / api->constant / api->feature from its
//     sigdb/symbols/server.d/<name>.json), installs its hooks through kiln.hooks v2 (kiln_hook.hpp), subscribes ticks, and publishes its
//     table with api->register_service. Returning non-OK (or not registering) = the service is unavailable.
//  2. optionally in on_ready: looks up other services (api->service) it depends on.
// Game state is only touched on the tick thread (Pre, or Post after the world's jobs) or inside an ECS program.
#include <atomic>

#include "kiln_api.h"
#include "services/kiln_example.h"

namespace {
const KilnApi* g_api = nullptr;
std::atomic<uint64_t> g_steps{0};

void KILN_CALL on_pre(const KilnTickInfo*, void*) { g_steps.fetch_add(1, std::memory_order_relaxed); }
uint64_t KILN_CALL steps() { return g_steps.load(std::memory_order_relaxed); }

const KilnExampleApi kTable = {{sizeof(KilnExampleApi), KILN_EXAMPLE_VERSION}, &steps};

void log(const char* s) {
    size_t n = 0;
    while (s[n]) ++n;
    g_api->log(0, KILN_LOG_INFO, KilnStr{s, n});
}
}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    info->id = KilnStr{KILN_EXAMPLE_SERVICE, sizeof(KILN_EXAMPLE_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    KilnHandle h = 0;
    if (KilnResult rc = api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h); rc != KILN_OK) return rc;
    return api->register_service(owner, KilnStr{KILN_EXAMPLE_SERVICE, sizeof(KILN_EXAMPLE_SERVICE) - 1}, &kTable);
}
