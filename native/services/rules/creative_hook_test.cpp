// SPDX-License-Identifier: MIT
// Execute the production hook bodies with fake trampolines and the real synchronized rule snapshots.
#define KILN_RULES_HOST_TEST
#include "rules.cpp"
#include "creative.cpp"

#include <cstdlib>
#include <string>
#include <thread>

// flight.cpp is not part of this test (flight_math_test.cpp checks its decisions)
namespace kiln_rules_flight {
void counters(uint64_t* a, uint64_t* b, uint64_t* c, uint64_t* d) { *a = *b = *c = *d = 0; }
}  // namespace kiln_rules_flight

namespace {
int failures = 0, payments = 0, removals = 0, fog_calls = 0;
void check(bool ok, const char* message) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", message);
    failures += !ok;
}
uint32_t pay(void*, int64_t*, uint32_t, uint32_t, void*) { ++payments; return 9; }
kiln_rules_creative::Result12* remove_items(kiln_rules_creative::Result12* out, void*, uint32_t, uint32_t, uint64_t, uint32_t) {
    ++removals; out->error = 1; out->value = 1; return out;
}
uint32_t craftable(void*, uint32_t, uint32_t, const void*) { return 2; }
void fog(void*, const float*, float range) {
    ++fog_calls;
    if (range > 120) throw 1; // widened discovery can fault while the game's original range still works
}
void fog_record(void*, const float*, float) { ++fog_calls; }

// kiln.flame and a game API with no symbols, for the Creative Mode check
bool flame_on = false;
int flame_asks = 0;
bool KILN_CALL flame_running(KilnStr id) { ++flame_asks; return flame_on && std::string(id.ptr, id.len) == "creative"; }
const KilnFlameApi flame_table = {{sizeof(KilnFlameApi), KILN_FLAME_VERSION}, nullptr, nullptr, &flame_running, nullptr};
bool flame_published = true;
const void* KILN_CALL api_service(KilnStr name, uint32_t) {
    return flame_published && std::string(name.ptr, name.len) == KILN_FLAME_SERVICE ? &flame_table : nullptr;
}
uint32_t KILN_CALL api_side() { return KILN_SIDE_CLIENT; }
bool KILN_CALL api_feature(KilnStr) { return false; }
KilnResult KILN_CALL api_symbol(KilnStr, uintptr_t*) { return KILN_ERR_NOT_FOUND; }
KilnResult KILN_CALL api_constant(KilnStr, int64_t*) { return KILN_ERR_NOT_FOUND; }
}

int main() {
    namespace c = kiln_rules_creative;
    auto rules = kDefault;
    rules.flags = KILN_RULES_FREE_CRAFTING | KILN_RULES_KEEP_CONSUMABLES;
    store(g_players[3], &rules);
    alignas(16) uint8_t tx[256]{};
    c::g_tx_operator = 224;
    auto& actor = *reinterpret_cast<uint32_t*>(tx + 224);
    actor = 3;
    uint8_t before[sizeof tx];
    std::memcpy(before, tx, sizeof tx);
    c::g_recipe_ret = 100;
    c::h_alt.target = reinterpret_cast<void*>(&pay);
    check(c::pay_free(100, tx, 3), "only the recipe payment by its authorized inventory owner is free");
    check(!storage_payment_free(400, 400, 3, 3), "free crafting alone never makes a storage upgrade free");
    check(!c::pay_free(101, tx, 3) && !c::pay_free(100, tx, 4) && !c::pay_free(100, tx, 2000),
          "trades, upgrades, building, other players and station inventories cannot borrow a waiver");
    actor = 4;
    check(!c::pay_free(100, tx, 4), "a second player's craft still pays");
    actor = 2000;
    check(!c::pay_free(100, tx, 2000), "an autonomous station still pays");
    actor = 3;
    kiln::test::caller = 107;
    check(c::hk_alt(tx, nullptr, 3, 10, nullptr) == 0, "flagged recipe payer accepts its exact recipe call");
    kiln::test::caller = 108;
    check(c::hk_alt(tx, nullptr, 3, 1, nullptr) == 9, "flagged recipe payer refuses unrelated return addresses");
    c::g_recipe_ret = 0;
    kiln::test::caller = 7;
    check(c::hk_alt(tx, nullptr, 3, 1, nullptr) == 9 && !c::pay_free(100, tx, 3),
          "a missing recipe call disables both waivers");
    check(payments == 2, "both refused alternate payments ran the original payer exactly once");
    check(std::memcmp(before, tx, sizeof tx) == 0, "waivers never queue removals or outputs, including multi-craft payments");
    c::g_recipe_ret = 100;
    check(!c::pay_free(100, nullptr, 3), "a missing transaction does not get a free payment");
    store(g_players[3], nullptr);
    check(!c::pay_free(100, tx, 3), "revoking free crafting takes effect on the next repeated craft");
    store(g_players[3], &rules);

    c::h_remove.target = reinterpret_cast<void*>(&remove_items);
    c::g_ammo_ret = 200;
    c::g_cost_ret = 300;
    c::g_buff_ret = 250;  // food and potions: ApplyBuffEvent's consume
    c::Result12 out{};
    for (auto ret : {uintptr_t{200}, uintptr_t{300}, uintptr_t{250}}) {
        kiln::test::caller = ret;
        out = {1, {}, 1, 1};
        check(c::hk_remove(&out, tx, 3, 7, uint64_t{8}, 1) == &out && out.error == 0 && out.zero == 0 && out.value == 0,
              "each consume site returns success with no removal for the rule's player");
    }
    kiln::test::caller = 201;
    check(c::hk_remove(&out, tx, 3, 7, uint64_t{8}, 1)->error == 1, "other inventory-removal callers still remove items");
    kiln::test::caller = 251;
    check(c::hk_remove(&out, tx, 3, 7, uint64_t{8}, 1)->error == 1, "buff application outside the exact consume call cannot borrow a waiver");
    kiln::test::caller = 250;
    check(c::hk_remove(&out, tx, 4, 7, uint64_t{8}, 1)->error == 1, "food consumed by another player is still removed");
    kiln::test::caller = 200;
    check(c::hk_remove(&out, tx, 4, 7, uint64_t{8}, 1)->error == 1, "consumption by another player still removes items");
    check(c::hk_remove(&out, tx, 3, 7, uint64_t{8}, 2)->error == 1, "consume waiver requires the pinned one-item removal");
    store(g_players[3], nullptr);
    check(c::hk_remove(&out, tx, 3, 7, uint64_t{8}, 1)->error == 1, "revoking kept consumables takes effect immediately");
    check(removals == 6, "unrelated removals ran the game's path exactly once each");
    check(std::memcmp(before, tx, sizeof tx) == 0, "kept consumables queue neither removals nor duplicate replacement items");

    c::h_craftable.target = reinterpret_cast<void*>(&craftable);
    store(g_local, nullptr);
    check(c::hk_craftable(nullptr, 7, 10, nullptr) == 2, "a server player's rule cannot enable the local craft buttons");
    store(g_local, &rules);
    check(c::hk_craftable(nullptr, 7, 10, nullptr) == 10 && c::hk_craftable(nullptr, 7, 1, nullptr) == 2,
          "only the local free-crafting rule raises the buttons, without lowering the game's count");
    check(c::hk_craftable(nullptr, 7, 0x1000a, nullptr) == 10, "the UI cap uses only the game's u16 argument");
    store(g_local, nullptr);
    check(c::hk_craftable(nullptr, 7, 10, nullptr) == 2, "local rule revocation restores the game's buttons");

    c::h_fog.target = reinterpret_cast<void*>(&fog);
    c::g_map_mode = KILN_RULES_MAP_RADIUS;
    c::g_map_radius = 200;
    bool fault = false;
    try { c::hk_fog(nullptr, nullptr, 120); } catch (...) { fault = true; }
    check(!fault && fog_calls == 2, "a widened discovery fault falls back to the game's original range");
    c::g_sweep_fow = c::g_done_fow = tx;
    c::reset_map();
    check(c::g_map_mode == KILN_RULES_MAP_DEFAULT && !c::g_sweep_fow && !c::g_done_fow,
          "world lifecycle clears the map mode and sweep identity even if the next world reuses the address");
    c::h_fog.target = reinterpret_cast<void*>(&fog_record);
    c::g_map_mode = KILN_RULES_MAP_WHOLE;
    float map[2] = {10240, 10240};
    bool bounded = true;
    for (int frame = 0; frame < 120; ++frame) {
        const auto was = fog_calls;
        c::hk_fog(map, nullptr, 120);
        bounded &= fog_calls - was <= 1 + creative_math::kSweepPerFrame;
    }
    check(bounded && c::g_done_fow == map, "the whole map completes with at most four added discover calls per frame");
    const auto was = fog_calls;
    c::hk_fog(map, nullptr, 120);
    check(fog_calls == was + 1, "a completed sweep adds no more discovery work");
    c::reset_map();
    c::g_map_mode = KILN_RULES_MAP_WHOLE;
    map[0] = 0;
    const auto faults = c::S.faults.load();
    c::hk_fog(map, nullptr, 120);
    check(c::S.faults == faults + 1 && c::g_done_fow == map, "an unknown map layout disables the sweep for that object");
    c::g_hooks = KILN_RULES_HOOK_CRAFT | KILN_RULES_HOOK_CONSUME;
    c::pay_hook_failed();
    check(c::g_hooks == KILN_RULES_HOOK_CONSUME && !c::h_alt && !c::pay_free(100, tx, 3),
          "failure to install the shared payer disables both recipe waivers while leaving consumption available");

    KilnApi api{};
    api.service = &api_service;
    api.side = &api_side;
    api.feature = &api_feature;
    api.symbol = &api_symbol;
    api.constant = &api_constant;
    std::string off;
    flame_published = false;
    check(c::install(&api, 0, off) == 0 && off.find("Creative Mode") == std::string::npos && !c::flame_creative(),
          "without kiln-flame the creative hooks install as before (here: no symbols)");
    flame_published = true;
    off.clear();
    check(c::install(&api, 0, off) == 0 && off.find("Creative Mode") == std::string::npos, "kiln-flame without Creative Mode changes nothing");
    flame_on = true;
    off.clear();
    const int asks = flame_asks;
    check(c::install(&api, 0, off) == 0 && off.find("(Creative Mode runs)") != std::string::npos && flame_asks == asks + 1,
          "with Creative Mode running nothing is hooked, before any symbol lookup, and status says why");
    flame_on = false;
    check(c::flame_creative() && flame_asks == asks + 1, "once seen running, Creative Mode stays seen (its hooks stay too)");
    store(g_players[3], &rules);
    c::g_recipe_ret = 100;
    check(!c::pay_free(100, tx, 3), "late Creative Mode disables an already installed recipe waiver");
    const int old_removals = removals;
    kiln::test::caller = 250;
    check(c::hk_remove(&out, tx, 3, 7, uint64_t{8}, 1)->error == 1 && removals == old_removals + 1,
          "late Creative Mode makes installed food hooks call the original exactly once");
    store(g_local, &rules);
    check(c::hk_craftable(nullptr, 7, 10, nullptr) == 2, "late Creative Mode restores the original craft buttons");
    const int old_fog = fog_calls;
    c::hk_fog(map, nullptr, 120);
    check(fog_calls == old_fog + 1, "late Creative Mode disables installed map widening and sweeps");
    struct { uint64_t prefix; uint64_t canary; } short_status{8, 0x12345678};
    check(c::creative_status(reinterpret_cast<KilnRulesCreative*>(&short_status)) == KILN_OK && short_status.canary == 0x12345678,
          "the new creative status fields never overrun an older caller's prefix");
    static_assert(sizeof(KilnRulesCreative) == 80 && offsetof(KilnRulesCreative, flight_takeoffs) == 56);
    std::printf("creative hooks: %d failure(s)\n", failures);
    return failures ? EXIT_FAILURE : EXIT_SUCCESS;
}
