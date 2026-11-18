// SPDX-License-Identifier: MIT
// Host test: kiln.world's production wiring of the write events (ecs_prod.cpp) over a fake core with kiln.hooks'
// owner-retiring observer and nothing else (no engine profile: the writes stay off, the subscriptions work). A
// subscription made before on_ready carries over to on_ready's writer, and one whose owner was revoked before on_ready
// (a plugin whose kiln_plugin_main failed after it subscribed) ends then, as every retired owner's does. Under
// ASan/UBSan and ThreadSanitizer.
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <string_view>

#include "kiln_api.h"
#include "services/kiln_hooks.h"
#include "../world_internal.h"
#include "../wraps.h"

namespace kw {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
Layout L{};
void logf(int, const char* fmt, ...) {
    va_list a;
    va_start(a, fmt);
    std::vfprintf(stderr, fmt, a);
    std::fputc('\n', stderr);
    va_end(a);
}
bool seh_call(void (*fn)(void*), void* ctx, unsigned long*) {
    fn(ctx);
    return true;
}
uint64_t KILN_CALL game_world_session(uint32_t) { return 0; }
}  // namespace kw

namespace {
int g_failed = 0;
void check(bool ok, const char* what) {
    std::printf("%s %s\n", ok ? "ok  " : "FAIL", what);
    if (!ok) ++g_failed;
}

KilnOwnerRetiringFn g_observer = nullptr;
void* g_observer_user = nullptr;
int g_observers = 0;
KilnResult KILN_CALL subscribe_owner_retiring(KilnOwner, KilnOwnerRetiringFn fn, void* user, KilnHandle* out) {
    g_observer = fn;
    g_observer_user = user;
    ++g_observers;
    *out = 1;
    return KILN_OK;
}
KilnResult KILN_CALL unsubscribe_owner_retiring(KilnHandle) { return KILN_OK; }
KilnHooksApi g_hooks{};

const void* KILN_CALL service(KilnStr name, uint32_t) {
    return std::string_view(name.ptr, name.len) == KILN_HOOKS_SERVICE ? &g_hooks : nullptr;
}
KilnApi g_core{};

int g_events = 0;
void KILN_CALL on_event(const KilnEcsWriteEvent*, void*) { ++g_events; }
}  // namespace

int main() {
    g_hooks.header.size = sizeof g_hooks;
    g_hooks.header.version = KILN_HOOKS_VERSION;
    g_hooks.subscribe_owner_retiring = &subscribe_owner_retiring;
    g_hooks.unsubscribe_owner_retiring = &unsubscribe_owner_retiring;
    g_core.abi_version = KILN_ABI_VERSION;
    g_core.size = sizeof g_core;
    g_core.service = &service;
    kw::g_api = &g_core;
    kw::g_owner = 7;

    kw::ecs_prod_init();  // kiln_plugin_main
    check(g_observer != nullptr, "kiln_plugin_main subscribes to kiln.hooks' owner-retiring observer");
    constexpr KilnOwner plugin = 50, other = 51;
    KilnHandle failed = 0, kept = 0;
    check(kw::t_ecs_write_subscribe(plugin, &on_event, nullptr, &failed) == KILN_OK && failed != 0 &&
              kw::t_ecs_write_subscribe(other, &on_event, nullptr, &kept) == KILN_OK && kept != 0 && kept != failed,
          "a subscription before on_ready is taken");
    if (g_observer) g_observer(plugin, g_observer_user);  // the plugin's kiln_plugin_main failed: the core revoked it
    kw::ecs_prod_ready();  // on_ready
    check(g_observers == 1, "the observer is subscribed once");
    KilnHandle again = 0;
    check(kw::t_ecs_write_subscribe(plugin, &on_event, nullptr, &again) == KILN_ERR_UNAVAILABLE && again == 0,
          "an owner revoked before on_ready stays retired after it");
    check(kw::t_ecs_write_unsubscribe(failed) == KILN_ERR_NOT_FOUND,
          "the subscription of an owner revoked before on_ready ended with it (no callback into a revoked plugin)");
    check(kw::t_ecs_write_unsubscribe(kept) == KILN_OK, "the other early subscription carried over to on_ready's writer");
    KilnHandle late = 0;
    check(kw::t_ecs_write_subscribe(other, &on_event, nullptr, &late) == KILN_OK && late != kept && late != failed,
          "a subscription after on_ready gets a new handle");
    if (g_observer) g_observer(other, g_observer_user);
    check(kw::t_ecs_write_unsubscribe(late) == KILN_ERR_NOT_FOUND, "an owner revoked after on_ready ends its subscription");
    check(g_events == 0, "no event without a write");
    std::printf(g_failed ? "ecs_prod_test: %d FAILED\n" : "ecs_prod_test: all passed\n", g_failed);
    return g_failed ? 1 : 0;
}
