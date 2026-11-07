// SPDX-License-Identifier: MIT
// kiln.rules' creative hooks (docs/re/creative-mode-146.md, "Part 3 research"; docs/kiln/services/rules.md):
//
//  - map reveal (client): the fog of war's per-frame discover call (FogDiscover, its only caller the fog_of_war
//    system) is called with a wider range (MAP_RADIUS), or, once per FogOfWar object (MAP_WHOLE), with a grid of
//    circles over the map, a few per frame, so no frame marks every fog tile dirty. The game saves what is uncovered.
//  - free crafting (server world): the recipe function pays through the inventory payment (rules.cpp hooks it for the
//    storage upgrade and asks pay_free for every other call) or, for recipes with flag 0x40, through a second payer
//    with the same arguments (its only caller is the recipe function; hooked here). Answering 0 is the payment's "paid":
//    nothing goes into the transaction, the recipe's output still does, all in the game's own transaction. Only when
//    the transaction's operator crafts from its own inventory (operator == owner) and has FREE_CRAFTING.
//  - craft buttons (client): the crafting menu enables its craft actions from UiCraftableAmount(gameUi, recipe, cap,
//    selections); with the local player's FREE_CRAFTING it answers at least `cap`.
//  - kept consumables (server world): the spawn path's `consume` and PayUsageCost's `consume` each remove the used item
//    with InventoryRemoveItems(out, tx, actor, item, slot, 1) and commit only on {error 0, missing 0}. For those two
//    calls (return addresses) and an actor with KEEP_CONSUMABLES the removal answers {0, 0} without queueing anything.
//
// Every hook body runs under a structured-exception guard; a fault counts, and the game's own path runs.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>

#include "creative.h"
#include "creative_math.h"
#include "safetyhook.hpp"

namespace kiln_rules_creative {
namespace {

const KilnApi* g_api = nullptr;

void logf(int level, const char* fmt, ...) {
    char b[512];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    if (n > 0) kiln_rules_bridge::log(level, b);
}

template <typename F>
bool sym(const char* name, F* out) {
    uintptr_t a = 0;
    if (g_api->symbol(KilnStr{name, std::strlen(name)}, &a) != KILN_OK || !a) return false;
    *out = reinterpret_cast<F>(a);
    return true;
}
bool cst(const char* name, int64_t* out) { return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }
bool feat(const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); }
uintptr_t rva(uintptr_t a) { return a - reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr)); }

// A resolved return address must follow a direct call (E8 rel32) to `target`: a stale symbol only turns a waiver off.
bool call_returns_to(uintptr_t ret, uintptr_t target) {
    __try {
        if (!ret || !target) return false;
        const auto* p = reinterpret_cast<const uint8_t*>(ret);
        int32_t rel = 0;
        std::memcpy(&rel, p - 4, 4);
        return p[-5] == 0xe8 && ret + static_cast<intptr_t>(rel) == target;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

struct Stats {
    std::atomic<uint64_t> map{0}, crafts{0}, craft_ui{0}, consumes{0}, faults{0};
    std::atomic<uint32_t> revealed{0};
} S;
std::atomic<uint32_t> g_hooks{0};

// =========================================================================================== map reveal ========

using FogDiscoverFn = void(__fastcall*)(void* fow, const float* pos, float range);
SafetyHookInline h_fog;
std::atomic<uint32_t> g_map_mode{KILN_RULES_MAP_DEFAULT};
std::atomic<float> g_map_radius{400.0f};
// the sweep (only the fog_of_war system calls FogDiscover, once a frame, so one thread at a time; the mutex is a guard)
std::mutex g_sweep_mu;
void* g_sweep_fow = nullptr;  // the FogOfWar being swept
void* g_done_fow = nullptr;   // the FogOfWar swept to the end
int g_sweep_next = 0;
creative_math::Sweep g_sweep;

bool read_map(void* fow, float* sx, float* sy) {
    __try {
        const auto* f = static_cast<const float*>(fow);
        *sx = f[0];
        *sy = f[1];
        return creative_math::map_ok(*sx, *sy);
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

bool call_fog(void* fow, const float* pos, float range) {
    __try {
        h_fog.call<void>(fow, pos, range);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

void sweep_step(void* fow) {
    std::unique_lock lock(g_sweep_mu, std::try_to_lock);
    if (!lock.owns_lock() || fow == g_done_fow) return;
    if (fow != g_sweep_fow) {
        float sx = 0, sy = 0;
        if (!read_map(fow, &sx, &sy)) {
            g_done_fow = fow;  // not a map this hook understands: leave it to the game
            S.faults.fetch_add(1, std::memory_order_relaxed);
            logf(KILN_LOG_WARN, "map reveal: the fog of war's map size is not sane; the whole-map reveal is off for this world");
            return;
        }
        g_sweep_fow = fow;
        g_sweep_next = 0;
        g_sweep = creative_math::sweep_for(sx, sy);
        logf(KILN_LOG_INFO, "map reveal: uncovering the whole map (%.0f x %.0f m, %d circles of %.0f m, %d a frame)", sx, sy,
             g_sweep.count(), creative_math::kSweepRadius, creative_math::kSweepPerFrame);
    }
    for (int k = 0; k < creative_math::kSweepPerFrame && g_sweep_next < g_sweep.count(); ++k, ++g_sweep_next) {
        float c[2];
        creative_math::sweep_center(g_sweep, g_sweep_next, &c[0], &c[1]);
        if (!call_fog(fow, c, 2.0f * creative_math::kSweepRadius)) {
            S.faults.fetch_add(1, std::memory_order_relaxed);
            g_done_fow = fow;
            return;
        }
        S.map.fetch_add(1, std::memory_order_relaxed);
    }
    if (g_sweep_next >= g_sweep.count()) {
        g_done_fow = fow;
        S.revealed.fetch_add(1, std::memory_order_relaxed);
        logf(KILN_LOG_INFO, "map reveal: the whole map is uncovered");
    }
}

void __fastcall hk_fog(void* fow, const float* pos, float range) {
    const uint32_t mode = g_map_mode.load(std::memory_order_relaxed);
    const float r = creative_math::fog_range(mode, g_map_radius.load(std::memory_order_relaxed), range);
    if (r != range) S.map.fetch_add(1, std::memory_order_relaxed);
    if (r != range) {
        if (!call_fog(fow, pos, r)) {
            S.faults.fetch_add(1, std::memory_order_relaxed);
            h_fog.call<void>(fow, pos, range);
            return;
        }
    } else h_fog.call<void>(fow, pos, range);
    if (mode == KILN_RULES_MAP_WHOLE && fow) sweep_step(fow);
}

// ============================================================================================ free crafting ======

using PayFn = uint32_t(__fastcall*)(void* tx, int64_t* costs, uint32_t owner, uint32_t multiplier, void* extra);
SafetyHookInline h_alt;
uintptr_t g_recipe_ret = 0;  // the recipe function's call to the inventory payment returns here
int64_t g_tx_operator = 0;

bool read_operator(void* tx, uint32_t* out) {
    __try {
        *out = *reinterpret_cast<const uint32_t*>(static_cast<const uint8_t*>(tx) + g_tx_operator);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// The operator crafts from its own inventory and has FREE_CRAFTING.
bool craft_free(void* tx, uint32_t owner) {
    uint32_t op = 0;
    if (!tx || g_tx_operator <= 0 || !read_operator(tx, &op)) return false;
    if (!op || op != owner) return false;
    KilnRulesPlayer r{};
    return kiln_rules_bridge::rules_of(op, &r) && (r.flags & KILN_RULES_FREE_CRAFTING) != 0;
}

uint32_t __fastcall hk_alt(void* tx, int64_t* costs, uint32_t owner, uint32_t multiplier, void* extra) {
    // This payer must be scoped just as tightly as InventoryPay: other callers must still pay.
    if (g_recipe_ret && reinterpret_cast<uintptr_t>(_ReturnAddress()) == g_recipe_ret + 7 && craft_free(tx, owner)) {
        S.crafts.fetch_add(1, std::memory_order_relaxed);
        return 0;
    }
    return h_alt.call<uint32_t>(tx, costs, owner, multiplier, extra);
}

// ============================================================================================ craft buttons ======

using CraftableFn = uint32_t(__fastcall*)(void* ui, uint32_t recipe, uint32_t cap, const void* selections);
SafetyHookInline h_craftable;

uint32_t __fastcall hk_craftable(void* ui, uint32_t recipe, uint32_t cap, const void* selections) {
    const uint32_t n = h_craftable.call<uint32_t>(ui, recipe, cap, selections);
    KilnRulesPlayer r{};
    const uint32_t want = cap & 0xffffu;  // the cap is a u16 (r8w)
    if (n < want && kiln_rules_bridge::local_rules(&r) && (r.flags & KILN_RULES_FREE_CRAFTING) != 0) {
        S.craft_ui.fetch_add(1, std::memory_order_relaxed);
        return want;
    }
    return n;
}

// ============================================================================================ consumables ========

struct Result12 {  // InventoryRemoveItems' result: {u8 error; pad; u32 zero; u32 still missing}
    uint8_t error;
    uint8_t pad[3];
    uint32_t zero;
    uint32_t value;
};
static_assert(sizeof(Result12) == 12);
using RemoveFn = Result12*(__fastcall*)(Result12* out, void* tx, uint32_t owner, uint32_t item, uint64_t slot, uint32_t count);
SafetyHookInline h_remove;
uintptr_t g_ammo_ret = 0, g_cost_ret = 0;

bool keep_removal(Result12* out, uintptr_t ret, uint32_t owner, uint32_t count) {
    __try {
        if (!out || !ret || (ret != g_ammo_ret && ret != g_cost_ret) || count != 1) return false;
        KilnRulesPlayer r{};
        if (owner && kiln_rules_bridge::rules_of(owner, &r) && (r.flags & KILN_RULES_KEEP_CONSUMABLES) != 0) {
            std::memset(out, 0, sizeof *out);
            S.consumes.fetch_add(1, std::memory_order_relaxed);
            return true;
        }
        return false;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        S.faults.fetch_add(1, std::memory_order_relaxed);
        return false;
    }
}

Result12* __fastcall hk_remove(Result12* out, void* tx, uint32_t owner, uint32_t item, uint64_t slot, uint32_t count) {
    const auto ret = reinterpret_cast<uintptr_t>(_ReturnAddress());
    if (tx && item && keep_removal(out, ret, owner, count)) return out;
    return h_remove.call<Result12*>(out, tx, owner, item, slot, count);
}

template <typename H>
bool inline_hook(H& h, uintptr_t target, void* detour, const char* what) {
    h = safetyhook::create_inline(reinterpret_cast<void*>(target), detour);
    if (!h) logf(KILN_LOG_ERROR, "hooking %s failed", what);
    return static_cast<bool>(h);
}

}  // namespace

bool pay_free(uintptr_t ret, void* tx, uint32_t owner) {
    if (!g_recipe_ret || ret != g_recipe_ret) return false;
    if (!craft_free(tx, owner)) return false;
    S.crafts.fetch_add(1, std::memory_order_relaxed);
    return true;
}

void pay_hook_failed() {
    g_recipe_ret = 0;
    h_alt = {};
    g_hooks &= ~KILN_RULES_HOOK_CRAFT;
}

uint32_t install(const KilnApi* api, std::string& off) {
    g_api = api;
    auto note = [&off](const char* s) { off += off.empty() ? s : std::string(", ") + s; };
    const bool client = api->side() == KILN_SIDE_CLIENT;
    uint32_t hooks = 0;

    // free crafting: both payers, or neither (a recipe of either kind must not stay paid while the other is free)
    uintptr_t pay = 0, alt = 0, recipe_ret = 0;
    if (feat("rules.craft") && cst("items/tx.operator", &g_tx_operator) && sym("rules/InventoryPay", &pay) &&
        sym("rules/RecipePaid", &recipe_ret) && sym("rules/RecipePayAlt", &alt) && call_returns_to(recipe_ret, pay) &&
        call_returns_to(recipe_ret + 7, alt) && inline_hook(h_alt, alt, reinterpret_cast<void*>(&hk_alt), "the second recipe payer")) {
        g_recipe_ret = recipe_ret;
        hooks |= KILN_RULES_HOOK_CRAFT;
        logf(KILN_LOG_INFO, "free crafting: the recipe's payments (rva 0x%llx returns to 0x%llx; second payer 0x%llx)",
             (unsigned long long)rva(pay), (unsigned long long)rva(recipe_ret), (unsigned long long)rva(alt));
    } else {
        note("free crafting (rules.craft)");
    }

    // kept consumables: the two consume removals
    uintptr_t remove = 0, ammo = 0, cost = 0;
    if (feat("rules.consume") && sym("items/InventoryRemoveItems", &remove) && sym("rules/AmmoConsumed", &ammo) &&
        sym("rules/CostConsumed", &cost) && call_returns_to(ammo, remove) && call_returns_to(cost, remove) &&
        inline_hook(h_remove, remove, reinterpret_cast<void*>(&hk_remove), "the inventory removal")) {
        g_ammo_ret = ammo;
        g_cost_ret = cost;
        hooks |= KILN_RULES_HOOK_CONSUME;
        logf(KILN_LOG_INFO, "kept consumables: the consume removals (rva 0x%llx; calls return to 0x%llx and 0x%llx)",
             (unsigned long long)rva(remove), (unsigned long long)rva(ammo), (unsigned long long)rva(cost));
    } else {
        note("kept consumables (rules.consume)");
    }

    if (client) {
        uintptr_t fog = 0, craftable = 0;
        if (feat("rules.map_reveal") && sym("rules/client.FogDiscover", &fog) &&
            inline_hook(h_fog, fog, reinterpret_cast<void*>(&hk_fog), "the fog of war's discover call"))
            hooks |= KILN_RULES_HOOK_MAP;
        else note("map reveal (rules.map_reveal)");
        if (feat("rules.craft_ui") && sym("rules/client.CraftableAmount", &craftable) &&
            inline_hook(h_craftable, craftable, reinterpret_cast<void*>(&hk_craftable), "the craftable amount"))
            hooks |= KILN_RULES_HOOK_CRAFT_UI;
        else note("craft buttons (rules.craft_ui)");
    }
    logf(KILN_LOG_INFO, "creative hooks: free crafting %s, kept consumables %s%s%s%s",
         (hooks & KILN_RULES_HOOK_CRAFT) ? "on" : "OFF", (hooks & KILN_RULES_HOOK_CONSUME) ? "on" : "OFF",
         client ? ", map reveal " : "", client ? ((hooks & KILN_RULES_HOOK_MAP) ? "on" : "OFF") : "",
         client ? ((hooks & KILN_RULES_HOOK_CRAFT_UI) ? ", craft buttons on" : ", craft buttons OFF") : "");
    g_hooks = hooks;
    return hooks;
}

KilnResult KILN_CALL map_reveal(uint32_t mode, float radius) {
    if (mode > KILN_RULES_MAP_WHOLE) return KILN_ERR_INVALID_ARGUMENT;
    if (mode == KILN_RULES_MAP_RADIUS && !(radius >= creative_math::kMinRadius && radius <= creative_math::kMaxRadius))
        return KILN_ERR_INVALID_ARGUMENT;
    if (!(g_hooks.load() & KILN_RULES_HOOK_MAP)) return KILN_ERR_UNAVAILABLE;
    if (mode == KILN_RULES_MAP_RADIUS) g_map_radius = radius;
    const uint32_t was = g_map_mode.exchange(mode);
    if (mode == KILN_RULES_MAP_WHOLE && was != KILN_RULES_MAP_WHOLE) {
        std::lock_guard lock(g_sweep_mu);
        g_done_fow = g_sweep_fow = nullptr;  // asked again: sweep again (the game keeps what is already uncovered)
    }
    if (was != mode) logf(KILN_LOG_INFO, "map reveal: %s", mode == KILN_RULES_MAP_WHOLE ? "the whole map" : mode == KILN_RULES_MAP_RADIUS ? "a wider circle" : "the game's range");
    return KILN_OK;
}

void reset_map() {
    std::lock_guard lock(g_sweep_mu);
    g_map_mode = KILN_RULES_MAP_DEFAULT;
    g_done_fow = g_sweep_fow = nullptr;
    g_sweep_next = 0;
}

KilnResult KILN_CALL creative_status(KilnRulesCreative* out) {
    if (!out || out->size < 8) return KILN_ERR_INVALID_ARGUMENT;
    KilnRulesCreative v{};
    v.size = sizeof v;
    v.map_mode = g_map_mode.load();
    v.map_radius = g_map_radius.load();
    v.map_revealed = S.revealed.load();
    v.map_calls = S.map.load();
    v.crafts_free = S.crafts.load();
    v.craft_ui = S.craft_ui.load();
    v.consumes_kept = S.consumes.load();
    v.faults = S.faults.load();
    const uint32_t n = std::min<uint32_t>(out->size, sizeof v);
    v.size = n;
    std::memcpy(out, &v, n);
    return KILN_OK;
}

}  // namespace kiln_rules_creative
