// SPDX-License-Identifier: MIT
#define KILN_ITEMS_HOST_TEST
#include "../pide.cpp"

#include <cstdlib>
#include <thread>

namespace {
int failures = 0;
void check(bool ok, const char* text) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", text);
    failures += !ok;
}
uint32_t level_index = 0;
struct Fixture {
    alignas(16) uint8_t frame[256]{};
    uint8_t info[256]{};
    uint8_t perks[2] = {4, 0};
    uint32_t level[4] = {0, 0, 30, 30};
    uint32_t item = 7;
    kitems::Override list[8]{};
    safetyhook::Context context{};
    Fixture() {
        context.rbp = reinterpret_cast<uintptr_t>(frame + 128);
        context.rdi = reinterpret_cast<uintptr_t>(info);
        list[0] = {kitems::kUsedItem, 0, &item, 4};
        list[1] = {kitems::kPerkContainerNew, 0, perks, 2};
        list[2] = {kitems::kLevel, 0, level, 16};
        *reinterpret_cast<kitems::Override**>(frame + 24) = list;
        *reinterpret_cast<uint64_t*>(frame + 32) = 3;
        *reinterpret_cast<uint64_t*>(frame + 40) = 8;
        info[0xcc] = kitems::kWeapons;
    }
    uint64_t count() const { return *reinterpret_cast<const uint64_t*>(frame + 32); }
};
void spawn(void* arg) { kitems::hk_spawn(static_cast<Fixture*>(arg)->context); }
void fail_commit(void* arg) { spawn(arg); throw 1; }
}

int main() {
    using namespace kitems;
    static_assert(sizeof(KilnItemsRequest) == 40 && offsetof(KilnItemsRequest, unlock) == 25 &&
                  offsetof(KilnItemsRequest, pide) == 28 && offsetof(KilnItemsRequest, slot) == 32);
    check(pide_request_valid(0, 0), "legacy requests keep the same layout and zero reserved bytes");
    check(layout_ok(0x98, 0xa0, 0xcc) && !layout_ok(0x198, 0x1a0, 0xcc) && !layout_ok(0x98, 0xa1, 0xcc),
          "layout validation sign-extends real d8 captures without accepting truncated or mismatched offsets");
    bool bounds = true;
    for (int i = 0; i <= 255; ++i)
        bounds &= pide_request_valid(KILN_ITEMS_UNLOCK, static_cast<uint8_t>(i)) == ((i >= 1 && i <= 5) || i == 255);
    check(bounds && !pide_request_valid(0, 1), "native unlock accepts exactly 1..5 or 255 with its flag");
    check(!pide_request_valid(KILN_ITEMS_RECIPE | KILN_ITEMS_UNLOCK, 1) &&
          !pide_request_valid(KILN_ITEMS_RECIPE | KILN_ITEMS_GEM_SLOT, 0), "recipe results cannot acquire give-only options");
    g_ok = true;
    g_list = -104; g_count = -96; g_category = 0xcc; g_level_index = &level_index;

    Fixture plain;
    spawn(&plain);
    check(plain.perks[1] == 0 && plain.level[2] == 30 && plain.count() == 3, "ordinary loot and crafting spawns are unchanged");
    Fixture gear;
    pide_commit(&gear, &spawn, KILN_ITEMS_UNLOCK | KILN_ITEMS_GEM_SLOT, 255, 7);
    check(gear.perks[1] == 4 && gear.level[2] == 34 && gear.level[3] == 30 && gear.count() == 4 &&
          gear.list[3].hash == kWeaponGemSlot && gear.list[3].size == 12,
          "a give unlocks only available perks, raises the current level and adds an empty weapon gem slot");
    spawn(&plain);
    check(plain.perks[1] == 0 && plain.count() == 3, "successful commit clears the options before the next unrelated spawn");
    Fixture failed;
    try { pide_commit(&failed, &fail_commit, KILN_ITEMS_UNLOCK | KILN_ITEMS_GEM_SLOT, 2, 7); } catch (...) {}
    spawn(&plain);
    check(!t_flags && !t_unlock && !t_item && plain.perks[1] == 0 && plain.count() == 3,
          "a faulted commit clears the options immediately, without waiting for another give");
    Fixture unrelated;
    pide_commit(&unrelated, &spawn, KILN_ITEMS_UNLOCK | KILN_ITEMS_GEM_SLOT, 2, 99);
    check(unrelated.perks[1] == 0 && unrelated.count() == 3, "a different item created during a give cannot inherit its options");
    Fixture armour;
    armour.info[0xcc] = 1;
    pide_commit(&armour, &spawn, KILN_ITEMS_UNLOCK | KILN_ITEMS_GEM_SLOT, 2, 7);
    check(armour.perks[1] == 2 && armour.level[2] == 32 && armour.count() == 3, "armour can unlock perks but never gets a weapon gem slot");
    Fixture full;
    *reinterpret_cast<uint64_t*>(full.frame + 40) = 3;
    pide_commit(&full, &spawn, KILN_ITEMS_GEM_SLOT, 0, 7);
    check(full.count() == 3, "a full override list is never overrun");
    Fixture gem;
    gem.list[3] = {kWeaponGemSlot, 0, g_zero_gem, 12};
    *reinterpret_cast<uint64_t*>(gem.frame + 32) = 4;
    pide_commit(&gem, &spawn, KILN_ITEMS_GEM_SLOT, 0, 7);
    check(gem.count() == 4, "a weapon that already rolled a gem slot never gets a second one");
    Fixture missing_level;
    missing_level.list[2].size = 0;
    pide_commit(&missing_level, &spawn, KILN_ITEMS_UNLOCK, 2, 7);
    check(missing_level.perks[1] == 0, "a missing level override cannot leave perks unlocked without their level increments");
    level_index = 2;
    Fixture bad_index;
    pide_commit(&bad_index, &spawn, KILN_ITEMS_UNLOCK, 2, 7);
    check(bad_index.perks[1] == 0 && bad_index.level[2] == 30, "an unknown level index fails closed without a partial upgrade");
    level_index = 0;
    bool isolated = false;
    pide_commit(&isolated, [](void* arg) {
        std::thread worker([arg] {
            Fixture other;
            spawn(&other);
            *static_cast<bool*>(arg) = other.perks[1] == 0 && other.count() == 3;
        });
        worker.join();
        Fixture nested;
        pide_commit(&nested, &spawn, 0, 0, 7);
        *static_cast<bool*>(arg) &= nested.perks[1] == 0 && t_flags == KILN_ITEMS_UNLOCK && t_unlock == 2;
    }, KILN_ITEMS_UNLOCK, 2, 7);
    check(isolated && !t_flags, "give options are thread local and nested plain commits restore the outer scope");
    std::printf("pide options: %d failure(s)\n", failures);
    return failures ? EXIT_FAILURE : EXIT_SUCCESS;
}
