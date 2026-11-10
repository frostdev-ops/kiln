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
    // The observer's link goes in only once spawn/DestroyEntity resolved (as in the game).
    static uint8_t destroy_site[32] = {};
    g_destroy_at = reinterpret_cast<uintptr_t>(destroy_site);
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
    for (auto& hook : g_h) assert(!hook);
    g_table[(3 << 8) | 5] = KILN_REL_NEUTRAL; g_table_n = 1;
    kiln::test::on_install = [](uintptr_t) { assert(hk_hostile(&factions[1], &factions[2])); }; // no partial-family answers
    kiln::test::fail_target = g_at[2];
    const int revokes = kiln::test::revokes;
    assert(!install() && g_installed == 0);
    for (auto& hook : g_h) assert(!hook);
    assert(kiln::test::revokes == revokes + 2); // the two links already in were revoked, not left half-installed
    assert(g_install_failed && relation_set(1, 3, 5, 3) == KILN_ERR_UNAVAILABLE);
    kiln::test::fail_target = 0;
    g_install_failed = false;
    kiln::test::on_install = nullptr;

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
    {   // a world stop before the cap takes the links out (with the observer); the next relation puts them back
        KilnWorldEvent early{};
        early.kind = KILN_WORLD_SERVER; early.event = KILN_WORLD_STOPPING;
        assert(pair_set(1, 1, 2, KILN_REL_NEUTRAL, 0) == KILN_OK);
        tick(); assert(g_installed == 31 && g_destroy);
        const int revokes0 = kiln::test::revokes;
        on_world(&early, nullptr);
        assert(g_installed == 0 && !g_destroy && !g_answering && kiln::test::revokes == revokes0 + 6);
    }

    // kiln.hooks never frees a link: a relation that keeps coming and going (timed pairs) stops adding links after
    // kMaxCycles sets, and the links then stay in through the idle spells without answering.
    const int installs0 = kiln::test::installs;
    for (int i = 0; i < 200; ++i) {
        assert(pair_set(1, 1, 2, KILN_REL_NEUTRAL, 0.1f) == KILN_OK);
        tick(); assert(g_answering && !hk_hostile(&factions[1], &factions[2]));
        test_now += 200; tick(); assert(g_pairs.empty() && !g_answering);
        if (g_cycles >= kMaxCycles) assert(g_installed == 31 && g_destroy);
    }
    assert(g_cycles == kMaxCycles && g_installed == 31);
    assert(kiln::test::installs - installs0 <= 6 * static_cast<int>(kMaxCycles));
    assert(hk_hostile(&factions[1], &factions[2]));  // idle: the game decides
    assert(pair_set(1, 1, 2, KILN_REL_NEUTRAL, 0) == KILN_OK);
    tick(); assert(g_answering && !hk_hostile(&factions[1], &factions[2]));  // the same links answer again
    assert(kiln::test::installs - installs0 <= 6 * static_cast<int>(kMaxCycles));
    owner_release(1); tick(); assert(!g_answering && g_installed == 31);

    // A world stop goes through the same cap: past kMaxCycles its links stay in (answers stop), so the next world's first
    // relation adds none; before the cap it takes them out like any idle spell.
    KilnWorldEvent stopped{};
    stopped.kind = KILN_WORLD_SERVER; stopped.event = KILN_WORLD_STOPPING;
    {
        const int installs1 = kiln::test::installs, revokes1 = kiln::test::revokes;
        assert(pair_set(1, 1, 2, KILN_REL_NEUTRAL, 0) == KILN_OK);
        tick(); assert(g_answering);
        on_world(&stopped, nullptr);
        assert(g_installed == 31 && !g_answering && g_destroy && kiln::test::revokes == revokes1);
        assert(pair_set(1, 1, 2, KILN_REL_NEUTRAL, 0) == KILN_OK);  // the next world
        tick(); assert(g_answering && g_installed == 31 && kiln::test::installs == installs1);
        owner_release(1); tick(); assert(!g_answering && g_installed == 31);
    }

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
    on_world(&stopped, nullptr);
    assert(g_installed == 31 && !g_answering);  // at the cap the links stay through the world stop
    assert(g_rows.empty() && g_pairs.empty() && g_cells.empty() && g_table_n == 0);
    on_unload(nullptr);
    assert(g_installed == 0 && !g_destroy && !g_live.load() && !g_life_index.load());
    for (auto& hook : g_h) assert(!hook && hook.original<void*>());
    std::cout << "npcai host tests passed\n";
}
