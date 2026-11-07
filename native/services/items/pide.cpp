// SPDX-License-Identifier: MIT
// kiln.items: gear options at creation (KILN_ITEMS_UNLOCK, KILN_ITEMS_GEM_SLOT; docs/re/items.md section 10,
// docs/re/creative-mode-146.md Part 3 section 2). createPide builds a list of component overrides
// {u32 component hash; void* data; u64 size} and spawns the item's pide template with it. A mid hook right before it
// copies the list (items/PideSpawn) changes it, only while a give of this service that asked for an option commits on
// this thread (pide_scope):
//  - UNLOCK: PerkContainerNew {availablePerks, unlockedPerks}: unlocked = min(available, asked); the Level override's
//    current value += the newly unlocked count (the workbench's upgrade does both, one at a time; docs/re/items.md).
//  - GEM_SLOT: on a Weapon (ItemInfo category 2) whose list has no WeaponGemSlot (the game's roll did not give one),
//    one entry {WeaponGemSlot, 12 zero bytes = an empty ItemStack} is appended while the list has room.
// Component hashes are FNV-1a-32 of the reflected type names, so they hold across builds. Faults count; the item is
// then created as the game made it.
#include <windows.h>

#include <atomic>
#include <cstring>

#include "pide.h"
#include "safetyhook.hpp"

namespace kitems {
namespace {

constexpr uint32_t kPerkContainerNew = 0xe288d50cu;  // keen::ecs::PerkContainerNew {u8 available, u8 unlocked}
constexpr uint32_t kLevel = 0xe550da37u;             // keen::ecs::Level: 8-byte attribute header, u32 values[2]
constexpr uint32_t kWeaponGemSlot = 0x84ba9b52u;     // keen::ecs::WeaponGemSlot {ItemStack weaponGem} (12 bytes)
constexpr uint8_t kWeapons = 2;                      // ItemCategory.Weapons
constexpr uint32_t kMaxPerks = 5;

struct Override {
    uint32_t hash;
    uint32_t pad;
    void* data;
    uint64_t size;
};
static_assert(sizeof(Override) == 24);

alignas(16) uint8_t g_zero_gem[16] = {};  // the appended gem slot's data (read by the spawn, never written)
SafetyHookMid h_spawn;
int64_t g_list = 0, g_count = 0, g_category = 0;  // rbp displacements (int8), ItemInfo offset
const uint32_t* g_level_index = nullptr;         // createPide's current-level index into Level's values
bool g_ok = false;

thread_local uint32_t t_flags = 0;
thread_local uint8_t t_unlock = 0;

struct Counters {
    std::atomic<uint64_t> unlocked{0}, gems{0}, faults{0};
} C;

void apply(safetyhook::Context& c) {
    const auto rbp = static_cast<uintptr_t>(c.rbp);
    auto*& list = *reinterpret_cast<Override**>(rbp + static_cast<int8_t>(g_list));
    auto& count = *reinterpret_cast<uint64_t*>(rbp + static_cast<int8_t>(g_count));
    const uint64_t cap = *reinterpret_cast<const uint64_t*>(rbp + static_cast<int8_t>(g_count) + 8);
    const auto* info = reinterpret_cast<const uint8_t*>(c.rdi);
    if (!list || count > 64 || cap < count) return;
    Override* perks = nullptr;
    Override* level = nullptr;
    bool gem = false;
    for (uint64_t i = 0; i < count; ++i) {
        if (list[i].hash == kPerkContainerNew && list[i].size >= 2) perks = &list[i];
        else if (list[i].hash == kLevel && list[i].size >= 16) level = &list[i];
        else if (list[i].hash == kWeaponGemSlot) gem = true;
    }
    if ((t_flags & KILN_ITEMS_UNLOCK) && perks && perks->data) {
        auto* p = static_cast<uint8_t*>(perks->data);
        const uint32_t available = p[0] > kMaxPerks ? kMaxPerks : p[0];
        const uint32_t want = t_unlock == 255 ? available : (t_unlock < available ? t_unlock : available);
        const uint32_t added = want > p[1] ? want - p[1] : 0;
        if (added) {
            p[1] = static_cast<uint8_t>(want);
            if (level && level->data && g_level_index && *g_level_index < 2) {
                auto* v = reinterpret_cast<uint32_t*>(static_cast<uint8_t*>(level->data) + 8);
                v[*g_level_index] += added;
            }
            C.unlocked.fetch_add(added, std::memory_order_relaxed);
        }
    }
    if ((t_flags & KILN_ITEMS_GEM_SLOT) && !gem && info && info[g_category] == kWeapons && count < cap) {
        list[count] = Override{kWeaponGemSlot, 0, g_zero_gem, 12};
        ++count;
        C.gems.fetch_add(1, std::memory_order_relaxed);
    }
}

void apply_guarded(safetyhook::Context& c) {
    __try {
        apply(c);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        C.faults.fetch_add(1, std::memory_order_relaxed);
    }
}

void hk_spawn(safetyhook::Context& c) {
    if (t_flags & (KILN_ITEMS_UNLOCK | KILN_ITEMS_GEM_SLOT)) apply_guarded(c);
}

}  // namespace

bool pide_install(const KilnApi* api) {
    auto sym = [api](const char* n, uintptr_t* out) {
        return api->symbol(KilnStr{n, std::strlen(n)}, out) == KILN_OK && *out;
    };
    auto cst = [api](const char* n, int64_t* out) { return api->constant(KilnStr{n, std::strlen(n)}, out) == KILN_OK; };
    uintptr_t site = 0, index = 0;
    if (!api->feature(KilnStr{"items.pide_options", 18}) || !sym("items/PideSpawn", &site) || !sym("items/PideLevelIndex", &index) ||
        !cst("items/pide.list", &g_list) || !cst("items/pide.count", &g_count) || !cst("items/item.category", &g_category))
        return false;
    // plausibility: rbp-relative slots of the list (pointer, count, capacity follow each other), a byte field of ItemInfo
    if (static_cast<int8_t>(g_count) != static_cast<int8_t>(g_list) + 8 || g_category <= 0 || g_category > 0x1000) return false;
    g_level_index = reinterpret_cast<const uint32_t*>(index);
    h_spawn = safetyhook::create_mid(reinterpret_cast<void*>(site), &hk_spawn);
    g_ok = static_cast<bool>(h_spawn);
    return g_ok;
}

bool pide_available() { return g_ok; }

void pide_begin(uint32_t flags, uint8_t unlock) {
    t_flags = g_ok ? (flags & (KILN_ITEMS_UNLOCK | KILN_ITEMS_GEM_SLOT)) : 0;
    t_unlock = unlock;
}

void pide_end() {
    t_flags = 0;
    t_unlock = 0;
}

void pide_counters(uint64_t* unlocked, uint64_t* gems, uint64_t* faults) {
    *unlocked = C.unlocked.load();
    *gems = C.gems.load();
    *faults = C.faults.load();
}

}  // namespace kitems
