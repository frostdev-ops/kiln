// SPDX-License-Identifier: MIT
// Exercise the real service with fake game leaves and hook patching; no game process is needed.
#include <cassert>
#include <thread>
#include <limits>
#include <iostream>
#include "../npcai.cpp"

namespace {
thread_local bool test_tick = true;
uint8_t factions[4] = {0, 3, 5, 3};
bool exists[4] = {false, true, true, true};
bool game_ptr(const uint8_t* a, const uint8_t* b) { return a && b && *a != *b; }
bool game_val(uint8_t a, uint8_t b) { return a != b; }
bool game_friend(const uint8_t* a, const uint8_t* b) { return a && b && *a == *b; }
bool game_opt(const uint8_t* a, const uint8_t* b, const void*) { return game_friend(a, b); }
bool game_friend_val(uint8_t a, uint8_t b) { return a == b; }
void tick() { on_pre(nullptr, nullptr); }
void died(uint32_t e) { safetyhook::Context c{0x1000, e}; hk_destroy(c); }
}

int main() {
    KilnApi api{};
    api.on_tick_thread = [] { return test_tick; };
    api.server_world = [] { return uintptr_t{0x1000}; };
    api.log = [](KilnOwner, int32_t, KilnStr) {};
    g_api = &api;
    KilnWorldApi world{};
    world.exists = [](KilnEntity e) { return e < 4 && exists[e]; };
    world.component = [](KilnEntity e, uint32_t, uint32_t* n) -> const void* {
        *n = 1; return e < 4 && exists[e] ? &factions[e] : nullptr;
    };
    g_world = &world; g_faction_hash = 1; g_storage_offset = 0; g_lifecycle = true;
    // The disable guard reads the hook site's bytes, so the observer needs a readable address (as in the game,
    // where the hook exists only once spawn/DestroyEntity resolved).
    static uint8_t destroy_site[32] = {};
    g_destroy_at = reinterpret_cast<uintptr_t>(destroy_site);
    g_destroy = *safetyhook::MidHook::create(destroy_site, nullptr, safetyhook::MidHook::StartDisabled);
    g_at[0] = reinterpret_cast<uintptr_t>(&game_ptr);
    g_at[1] = reinterpret_cast<uintptr_t>(&game_val);
    g_at[2] = reinterpret_cast<uintptr_t>(&game_friend);
    g_at[3] = reinterpret_cast<uintptr_t>(&game_opt);
    g_at[4] = reinterpret_cast<uintptr_t>(&game_friend_val);
    g_resolved = 1;
    assert(!usable() && !prepare());
    assert(relation_set(1, 3, 5, 3) == KILN_ERR_UNAVAILABLE);
    g_resolved = 31;
    assert(prepare());
    assert(g_installed == 0);
    for (auto& hook : g_h) assert(!hook.enabled());
    g_table[(3 << 8) | 5] = KILN_REL_NEUTRAL; g_table_n = 1;
    safetyhook::test_enabled = [](int) { assert(hk_hostile(&factions[1], &factions[2])); }; // no partial-family answers
    safetyhook::test_enable_fail = 2;
    assert(!install() && g_installed == 0);
    for (auto& hook : g_h) assert(!hook.enabled());
    assert(g_install_failed && relation_set(1, 3, 5, 3) == KILN_ERR_UNAVAILABLE);
    safetyhook::test_enable_fail = -1;
    g_install_failed = false;
    safetyhook::test_enabled = nullptr;

    assert(relation_set(1, 3, 5, KILN_REL_NEUTRAL) == KILN_OK);
    tick(); assert(g_installed == 31);
    assert(!hk_hostile(&factions[1], &factions[2]));
    assert(!hk_friend(&factions[1], &factions[2]));
    assert(!hk_friend_opt(&factions[1], &factions[2], nullptr));
    assert(!hk_hostile_value(3, 5) && !hk_friend_value(3, 5));
    assert(relation_set(2, 3, 5, KILN_REL_FRIENDLY) == KILN_OK);
    assert(hk_friend_value(3, 5));
    owner_release(2); assert(relation_get(3, 5) == KILN_REL_NEUTRAL);
    owner_release(1); tick(); assert(g_installed == 0);
    assert(hk_hostile_value(3, 5)); // original trampoline remains callable after removal

    uint8_t row[256]{};
    assert(entity_row(1, 1, row) == KILN_OK);
    tick(); assert(g_installed == 0 && g_rows.empty());
    row[5] = KILN_REL_FRIENDLY;
    g_lifecycle = false;
    assert(entity_row(1, 1, row) == KILN_ERR_UNAVAILABLE);
    g_lifecycle = true;
    assert(entity_row(1, 1, row) == KILN_OK);
    entity_row(2, 1, nullptr); assert(g_rows.size() == 1); // an owner cannot clear another's row
    tick(); assert(hk_friend(&factions[1], &factions[2]));
    auto before_query = g_by_row.load();
    uint32_t h = 0, f = 0;
    assert(query(1, 2, &h, &f) == KILN_OK && !h && f);
    assert(g_by_row == before_query); // explicit diagnostics must not count as worker answers
    auto old = g_live.load();
    auto lifetime = entity_lifetime(1);
    assert(pair_set(1, 1, 2, KILN_REL_NEUTRAL, 0) == KILN_OK);
    tick(); assert(!hk_friend(&factions[1], &factions[2]));
    pair_set(2, 1, 2, KILN_REL_GAME, 0); assert(g_pairs.size() == 1);
    assert(pair_set(1, 1, 2, 1, std::numeric_limits<float>::quiet_NaN()) == KILN_ERR_INVALID_ARGUMENT);
    assert(pair_set(1, 1, 2, 1, -1) == KILN_ERR_INVALID_ARGUMENT);
    // Same id and exact same component address, with no observable exists=false gap.
    died(1);
    assert(!find_row(old.get(), &factions[1]));
    assert(hk_hostile(&factions[1], &factions[2]));
    assert(entity_lifetime(1) != lifetime);
    tick(); assert(g_rows.empty() && g_pairs.empty() && g_installed == 0);
    assert(entity_row(1, 1, row) == KILN_OK);
    tick(); assert(hk_friend(&factions[1], &factions[2]));
    assert(pair_set(1, 1, 2, KILN_REL_NEUTRAL, 0.1f) == KILN_OK);
    tick(); test_now += 200; tick(); assert(g_pairs.empty());

    owner_release(1); tick(); assert(g_installed == 0);
    pair_set(1, 1, 2, KILN_REL_NEUTRAL, 0.1f); tick(); assert(g_installed == 31);
    test_now += 200; tick(); assert(g_pairs.empty() && g_installed == 0); // the last expiring pair removes all five

    std::atomic<bool> stop{false};
    std::thread worker([&] {
        test_tick = false;
        while (!stop.load()) {
            uint32_t h = 0, f = 0;
            assert(query_factions(3, 5, &h, &f) == KILN_OK);
            assert(relation_get(3, 5) <= 3);
            hk_hostile(&factions[1], &factions[2]);
            KilnNpcAiStatus status_value{}; status_value.size = sizeof status_value;
            assert(status(&status_value) == KILN_OK);
        }
    });
    for (int i = 0; i < 1000; ++i) {
        relation_set(1, 3, 5, i % 4);
        row[5] = i % 4;
        entity_row(1, 1, row);
        tick();
        if (i % 3 == 0) died(1);
    }
    stop = true; worker.join();
    KilnWorldEvent stopped{};
    stopped.kind = KILN_WORLD_SERVER; stopped.event = KILN_WORLD_STOPPING;
    on_world(&stopped, nullptr);
    assert(g_rows.empty() && g_pairs.empty() && g_cells.empty() && g_table_n == 0);
    on_unload(nullptr);
    assert(g_installed == 0 && !g_destroy.enabled() && !g_live.load() && !g_life_index.load());
    for (auto& hook : g_h) assert(!hook.enabled() && hook.original<void*>());
    std::cout << "npcai host tests passed\n";
}
