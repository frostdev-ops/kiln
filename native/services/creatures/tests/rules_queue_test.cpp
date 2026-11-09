// SPDX-License-Identifier: MIT
// The real job queue, destruction observer, Pre transfer and ABI drain, with no game or Wine.
#include <algorithm>
#include <cassert>
#include <cstdarg>
#include <iostream>
#include <thread>
#include "../rules.cpp"

namespace {
bool test_exists[4] = {false, true, true, true};
bool test_lifecycle = true, test_symbol = true;
int warnings = 0, drops = 0;
KilnWorldEventFn world_event = nullptr;
void destroyed(uint32_t e, uintptr_t storage = 0x1020) {
    safetyhook::Context ctx{storage, e};
    kcr::hk_life_destroy(ctx);
}
void pre() { kcr::rules_pre_work(); }
}

// kiln.hooks v2 stand-in: kiln::MidHook2 needs api->service("kiln.hooks", 2) with mid2 and revoke.
KilnHooksApi g_hooks{};
int g_mid_links = 0;  // live mid links

namespace kcr {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 1;
uint64_t g_step = 1;
Layout L;
Fns fn;
Features F;
ArsenalLayout AL;
void logf(int level, const char*, ...) {
    if (level == KILN_LOG_WARN) ++warnings;
    if (level == KILN_LOG_DEBUG) ++drops;
}
bool on_tick() { return true; }
uintptr_t world() { return 0x1000; }
bool entity_exists(KilnEntity e) { return e < 4 && test_exists[e]; }
void* component(KilnEntity, uint32_t, uint32_t*) { return nullptr; }
bool seh_read(const void*, void*, size_t) { return false; }
bool seh_write(void*, const void*, size_t) { return false; }
RuleUses arsenal_uses() { return {}; }
}

int main() {
    using namespace kcr;
    KilnApi api{};
    api.size = sizeof api;
    api.feature = [](KilnStr) { return test_lifecycle; };
    api.symbol = [](KilnStr, uintptr_t* out) -> KilnResult {
        *out = test_symbol ? 0x2000 : 0;
        return test_symbol ? KILN_OK : KILN_ERR_UNAVAILABLE;
    };
    g_hooks.header = {sizeof g_hooks, KILN_HOOKS_VERSION};
    g_hooks.mid2 = [](KilnOwner, uintptr_t, const KilnHookOrder*, KilnMidFn, void*, KilnHandle* out) -> KilnResult {
        *out = ++g_mid_links;
        return KILN_OK;
    };
    g_hooks.revoke = [](KilnHandle) -> KilnResult {
        --g_mid_links;
        return KILN_OK;
    };
    api.service = [](KilnStr, uint32_t) -> const void* { return &g_hooks; };
    api.subscribe_world = [](KilnOwner, KilnWorldEventFn fn, void*, KilnHandle*) -> KilnResult {
        world_event = fn;
        return KILN_OK;
    };
    g_api = &api;
    L.world_storage = 0x20;
    rules_lifetime_init();
    assert(g_mid_links == 1);  // the destroy observer is one mid link on spawn/DestroyEntity
    pre();
    assert(g_life_storage == 0x1020 && world_event);
    KilnRuleWork out[2]{};

    // The job captures tokens before the Pre transfer. No exists=false gap, same ID and storage address.
    std::thread job([] { want(W_ACTION, 1, 10, 2); });
    job.join();
    auto original = g_want.front().life;
    assert(original && g_want.front().target_life);
    destroyed(1);
    want(W_ACTION, 1, 11, 2);
    assert(g_want.back().life != original);
    pre();
    assert(rules_drain_api(out, 1) == 1 && out[0].id == 11);  // skips stale work without ending the drain early
    assert(rules_drain_api(out, 1) == 0 && drops == 1);

    // Destruction between Pre and drain also invalidates work; the replacement has its own fresh token.
    want(W_ACTION, 1, 12, 2);
    pre();
    destroyed(1);
    want(W_ACTION, 1, 13, 2);
    assert(rules_drain_api(out, 1) == 0 && drops == 2);
    pre();
    assert(rules_drain_api(out, 1) == 1 && out[0].id == 13);

    // A replaced target cannot inherit a queued action either.
    want(W_ACTION, 1, 14, 2);
    pre();
    destroyed(2);
    assert(rules_drain_api(out, 1) == 0 && drops == 3);

    // A different world's destruction must not invalidate the server entity.
    want(W_ACTION, 1, 15);
    destroyed(1, 0x9000);
    pre();
    assert(rules_drain_api(out, 1) == 1 && out[0].id == 15 && drops == 3);

    // Missing live entity is refused even if its destruction notification has not arrived.
    want(W_ACTION, 1, 16);
    pre();
    test_exists[1] = false;
    assert(rules_drain_api(out, 1) == 0 && drops == 4);
    test_exists[1] = true;
    assert(warnings == 0);

    want(W_ACTION, 1, 17);
    pre();
    want(W_ACTION, 1, 18);
    KilnWorldEvent stopped{};
    stopped.kind = KILN_WORLD_SERVER;
    stopped.event = KILN_WORLD_STOPPING;
    world_event(&stopped, nullptr);
    assert(g_work.empty() && g_want.empty() && g_lives.empty() && !g_life_storage);
    pre();
    want(W_ACTION, 1, 19);
    assert(g_want.back().life != original);
    rules_lifetime_unload(nullptr);
    assert(g_want.empty() && g_lives.empty() && !g_life_destroy && g_mid_links == 0);  // the unload revoked its link

    // Unknown builds retain ID-based behaviour, with one warning for repeated actions.
    test_symbol = false;
    rules_lifetime_init();
    want(W_ACTION, 1, 20);
    want(W_ACTION, 1, 21);
    pre();
    assert(rules_drain_api(out, 2) == 2 && out[0].id == 20 && out[1].id == 21 && warnings == 1);
    want(W_ACTION, 1, 22);
    pre();
    assert(rules_drain_api(out, 1) == 1 && warnings == 1);
    test_symbol = true;
    test_lifecycle = false;
    rules_lifetime_init();
    assert(!g_life_destroy);
    api.size = offsetof(KilnApi, side);  // old core: do not read appended lifecycle entries
    rules_lifetime_init();
    assert(!g_life_destroy);
    std::cout << "creatures rule queue: normal, replacement, target, lifecycle and fallback tests passed\n";
}
