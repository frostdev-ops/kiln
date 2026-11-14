// SPDX-License-Identifier: MIT
// The game's durability debit, as kiln.items knows it (docs/re/items.md; the wave's chapter 16b): the functions, the
// two layouts they pass, and what a build must have before any of it is used. Facts only: nothing here hooks or calls.
// kiln.toolwear (toolwear.cpp) changes the cost of a tool's debit with them; a cost of Kiln's own (taming) calls
// WearDelta inside its own program with them.
//
// The game debits durability in three places, all through WearDelta:
//   - durability_loss, per HitEvent: the target's armor, and the attacker's weapon while hitCounter <= the cap, each
//     through WearItem (an item entity's tracked Durability, the actor's skills);
//   - actor_spawn_entity, after a projectile was spawned: WearDelta directly, delta -1;
//   - durability_burndown (torches): its own caller, never gated by the world's enableDurability.
// WearItem's argument order is load-bearing: the ACTOR is third, the ITEM entity fourth. WearDelta's fourth argument
// is the actor to notify when the item breaks, not the item.
#pragma once
#include <cstddef>
#include <cstdint>
#include <cstring>

namespace kitems::wear {

// What the debit functions carry through one program invocation. Valid only inside that invocation: never kept.
struct WearEnv {
    uint64_t notify_output;     // +0x00 the NotifyEvent output accessor
    uint16_t durability_index;  // +0x08 the durability attribute's index in its container
    uint8_t pad[6];
    void* ctx;                  // +0x10 the ProgramCtx of this invocation
    void* attributes;           // +0x18 AttributeRegistry
    void* skill_tree;           // +0x20 SkillTree
};
static_assert(sizeof(WearEnv) == 0x28 && offsetof(WearEnv, durability_index) == 0x08 && offsetof(WearEnv, ctx) == 0x10 &&
              offsetof(WearEnv, attributes) == 0x18 && offsetof(WearEnv, skill_tree) == 0x20);

// void WearItem(durability other-write accessor, PlayerSkillsUi other-read accessor, actor, item entity, delta, env)
using WearItemFn = void(__fastcall*)(uint64_t durability_write, uint64_t skills_read, uint32_t actor, uint32_t item_entity,
                                     int32_t delta, WearEnv* env);
// void WearDelta(Durability*, const PlayerSkillsUi* or null, delta, the actor to notify, env). A null or read-only
// Durability is ignored by the game. With skills given, a negative delta may be saved by the game's skill roll.
using WearDeltaFn = void(__fastcall*)(void* durability, const void* skills, int32_t delta, uint32_t notify_actor, WearEnv* env);
// bool EnableDurability(const BalancingRegistry*): the world's setting. The hit and the spawn producer ask it; burndown does not.
using EnableDurabilityFn = bool(__fastcall*)(const void* balancing);

// An attribute container's header (the Durability component is one, 16 bytes with its two values).
struct AttrHeader {
    uint32_t schema;       // +0 the attribute group's id (a tool's Durability: kAttrDurability)
    uint16_t data_offset;  // +4 where the i32 values start
    uint8_t flags;         // +6 bit 0: writable
    uint8_t count;         // +7 how many values
};
static_assert(sizeof(AttrHeader) == 8);

constexpr uint32_t kAttrDurability = 0xed71dc01u;    // the durability attribute's id (AttrFind's key)
constexpr uint32_t kTypeDurability = 0xdffb1608u;    // qualified component hashes (ComponentMapFind's key)
constexpr uint32_t kTypeItemState = 0x3a2fac75u;
constexpr size_t kDurabilitySize = 16, kItemStateSize = 20, kItemStateItemId = 8;
constexpr size_t kHitIterator = 0x50, kSpawnIterator = 0x218;

// The current and the largest value of a Durability container of `size` bytes, by the durability attribute's index
// (the maximum is the next value, as the game's containers have it). false: not a writable container with both in range.
inline bool durability_values(const void* durability, size_t size, uint16_t index, int32_t* current, int32_t* max) {
    if (!durability || size < sizeof(AttrHeader)) return false;
    AttrHeader h;
    std::memcpy(&h, durability, sizeof h);
    if (!(h.flags & 1) || index >= h.count) return false;
    const size_t at = h.data_offset + size_t{4} * index;
    if (h.data_offset < sizeof(AttrHeader) || at + 4 > size) return false;
    std::memcpy(current, static_cast<const uint8_t*>(durability) + at, 4);
    if (max) {
        *max = *current;
        if (index + 1u < h.count && at + 8 <= size) std::memcpy(max, static_cast<const uint8_t*>(durability) + at + 4, 4);
    }
    return true;
}

// The game builds whose producer frames were read and checked in a lab (the weapon return site and the spawn call's
// frame slots are compiler-specific): a signature that resolves on another build is not enough to use them.
struct ApprovedBuild {
    const char* exe_sha256;
    uint32_t side;  // KILN_SIDE_SERVER 0, KILN_SIDE_CLIENT 1
    const char* what;
};
inline constexpr ApprovedBuild kApproved[] = {
    {"f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2", 0, "server, Steam build 26430253"},
    {"5d9ef2f140b8ed49e30dbee655841b89d08baec20f799a1817e171a18a850c77", 1, "client, Steam build 26430625"},
};
inline const ApprovedBuild* approved_build(const char* exe_sha256, uint32_t side) {
    if (!exe_sha256) return nullptr;
    for (const auto& b : kApproved)
        if (b.side == side && std::strcmp(b.exe_sha256, exe_sha256) == 0) return &b;
    return nullptr;
}

// Everything sigdb has for the debit, resolved. `ok` only when every address and constant is there AND the build is
// approved AND the constants are what the approved frames have; otherwise `why` says what is not.
struct Facts {
    bool ok = false;
    char why[160] = "";
    WearItemFn wear_item = nullptr;
    WearDeltaFn wear_delta = nullptr;
    EnableDurabilityFn enable_durability = nullptr;
    uintptr_t hit_update = 0;      // items/ToolWearUpdate: durability_loss' update
    uintptr_t spawn_update = 0;    // items/ActorSpawnUpdate: actor_spawn_entity's update
    uintptr_t weapon_return = 0;   // items/WeaponWearReturn: where WearItem returns to for the attacker's weapon
    uintptr_t spawn_call = 0;      // items/SpawnWearCall: the call of WearDelta after a projectile spawn
    int64_t spawn_request_local = 0;     // [rbp + this]: the actor's current ActionSequence
    int64_t spawn_durability_local = 0;  // [rbp + this]: the Durability other-write accessor
    int64_t spawn_request_pide = 0;      // ActionSequence + this: usedItem's item entity
    int64_t spawn_iter_size = 0, spawn_iter_base = 0, spawn_itemstate_slot = 0;
    int64_t world_storage = 0, storage_kind = 0, registry_components = 0;
};

// The constants the approved frames have: a build that resolves others is not approved, whatever its hash.
inline const char* check_constants(const Facts& f) {
    if (f.spawn_iter_size != static_cast<int64_t>(kSpawnIterator)) return "actor_spawn_entity's iterator is not 0x218 bytes";
    if (f.spawn_itemstate_slot != 0x1f0) return "the ItemState slot of the spawn iterator is not +0x1f0";
    if (f.spawn_itemstate_slot + 8 > f.spawn_iter_size) return "the ItemState slot is outside the spawn iterator";
    if (f.spawn_request_local != 0x390 || f.spawn_durability_local != 0x458) return "the spawn frame's slots moved";
    if (f.spawn_durability_local - f.spawn_iter_base != 0x1d8 || f.spawn_request_local - f.spawn_iter_base != 0x110)
        return "the spawn frame's slots are not the iterator's +0x110 and +0x1d8";
    if (f.spawn_request_pide != 0x3c) return "ActionSequence.usedItem's entity is not at +0x3c";
    if (f.storage_kind != 8) return "the storage's kind is not at +8";
    if (f.world_storage <= 0 || f.registry_components <= 0) return "the world's storage or the registry's component map did not resolve";
    return nullptr;
}

}  // namespace kitems::wear

struct KilnApi;

namespace kitems::wear {

inline constexpr char kFeature[] = "items.tool_wear_debit";

// Resolves the facts once, at the service's start (wear.cpp). false: the feature is off, with the reason logged and,
// where sigdb had it on, reported to kiln.caps as this owner's lowering.
bool resolve(const KilnApi* api, uint64_t owner);
// The facts as resolved; ok false before resolve and whenever it failed.
const Facts& facts();
// The durability attribute's index in its container, through the game's own lookup (items/AttrFind). -1: not found.
int durability_index(void* attributes);

}  // namespace kitems::wear
