// SPDX-License-Identifier: MIT
// kiln.items v5: the parts of equip and unequip that need no game memory (so the host test can run them): which
// keen::EquipmentSlot values are equipment slots, where each sits in SlotSelection.equipment, which EquipmentSlotType
// the game's equip demands for it, what an error of the game's functions means, and where an item went. The tables
// are the game's own (server 26430253; docs/re/items.md section 16): the slot-type switch 0x936c30 and the
// SlotSelection switch 0x936d80.
#pragma once
#include <cstddef>
#include <cstdint>

#include "moves.h"
#include "services/kiln_items.h"
#include "services/kiln_players.h"

namespace kitems {

constexpr uint8_t kEquipSlotChoose = 24;  // keen::EquipmentSlot::Invalid: the game's equip picks the item's own slot
constexpr uint32_t kEquipSlots = 20;      // SlotSelection.equipment entries

// The index of a keen::EquipmentSlot in SlotSelection.equipment (0..19), or -1 for a value that is no equipment slot
// (17-20 are the building materials, 24 and up Invalid / MainHand).
inline int selection_index(uint8_t game_slot) {
    if (game_slot <= 16) return game_slot;
    if (game_slot >= 21 && game_slot <= 23) return game_slot - 4;
    return -1;
}

// The keen::EquipmentSlot of SlotSelection.equipment entry `index` (0..19); 0xFF for anything else.
inline uint8_t game_slot_of(int index) {
    if (index >= 0 && index <= 16) return static_cast<uint8_t>(index);
    if (index >= 17 && index <= 19) return static_cast<uint8_t>(index + 4);
    return 0xFF;
}

// The keen::EquipmentSlotType an item must have to go into `game_slot` (0 = none: not an equipment slot). The vanity
// slots take armour of the same body part.
inline uint8_t slot_type_of(uint8_t game_slot) {
    static const uint8_t t[24] = {13, 14, 15, 16, 17,   // Vanity_Head .. Vanity_Feet: Armour_Head .. Armour_Feet
                                  13, 14, 15, 16, 17,   // Armour_Head .. Armour_Feet
                                  37, 37,               // Ring0, Ring1: Armour_Ring
                                  5,  18, 6,  7,  8,    // RangedWeapon RangeWeapon, Shield, Arrow, Spell, FishingBait
                                  9,  10, 11, 12,       // the building materials (not equipment slots)
                                  34, 35, 38};          // HookShot, Glider, Backpack
    return game_slot < 24 ? t[game_slot] : 0;
}

// The request's flags, slot and ids as the C ABI allows them (kiln_items.h "equipment").
inline bool equip_request_valid(const KilnItemsEquipRequest& r) {
    if (r.player == 0 || (r.flags & ~(KILN_ITEMS_EQUIP_UNEQUIP | KILN_ITEMS_EQUIP_DRY_RUN))) return false;
    if (r.reserved[0] || r.reserved[1] || r.reserved[2] || r.reserved2) return false;
    const bool any = r.equipment_slot == KILN_ITEMS_EQUIP_ANY;
    if (!any && selection_index(r.equipment_slot) < 0) return false;
    if (r.flags & KILN_ITEMS_EQUIP_UNEQUIP) return !any && r.slot == 0 && r.pide == 0;
    return r.slot != 0 && (r.slot >> 40) == 0 && KILN_SLOT_INDEX(r.slot) <= 7 && KILN_SLOT_INVENTORY(r.slot) != 0;
}

// What an error code of the game's equip / unequip means for the caller. `room`: the player has a free Generic slot
// (unequip; true when unknown).
inline int32_t equip_status_of(uint8_t error, bool unequip, bool room) {
    if (error == 0) return KILN_ITEMS_DONE;
    if (error == 0x15) return KILN_ITEMS_SLOT_CHANGED;  // no item where the game looked
    if (unequip && error == 0x11 && !room) return KILN_ITEMS_NO_ROOM;
    return KILN_ITEMS_REFUSED;
}

// The message for it. `fits`: the item's EquipmentSlotType is the slot's (equip; true when unknown).
inline const char* equip_error_text(uint8_t error, bool unequip, bool fits, bool room) {
    if (error == 0) return "";
    if (error == 0x15) return "the game found no item there (error 0x15)";
    if (unequip && error == 0x11 && !room) return "no free slot in the player's backpack (error 0x11)";
    if (!unequip && !fits) return "the item does not fit that equipment slot: the game refused it";
    if (error == 0x1f) return "the game found no equipment or inventory for this player, or no free slot (error 0x1f)";
    return unequip ? "the game refused the unequip" : "the game refused the equip";
}

// The slot holding the item after a change. With an item entity (`pide`): the slot that holds it in `after`. A plain
// stack: the first slot of `after` whose count of `item` is higher than the same slot's in `before`. `exclude` (a slot
// id, 0 = none) is never returned. 0 when nothing matches.
inline uint64_t where_now(const Page* before, size_t n_before, const Page* after, size_t n_after, uint32_t item,
                          uint32_t pide, uint64_t exclude) {
    if (item == 0) return 0;
    for (size_t i = 0; i < n_after; ++i)
        for (uint32_t k = 0; k < kSlotsPerPage; ++k) {
            const Stack& s = after[i].s[k];
            if (s.slot == exclude || is_empty(s) || s.item != item) continue;
            if (pide) {
                if (pide_of(s) == pide) return s.slot;
                continue;
            }
            uint32_t was = 0;
            for (size_t j = 0; j < n_before; ++j)
                if (before[j].entity == after[i].entity) {
                    const Stack& b = before[j].s[k];
                    if (!is_empty(b) && b.item == item) was = b.count;
                }
            if (s.count > was) return s.slot;
        }
    return 0;
}

// The slot of a connection handle (slot | generation << 6).
inline uint32_t slot_of_handle(uint32_t handle) { return handle & 0x3fu; }

// The session a request was made for still holds: the game's own player record of the handle's slot, read now (not
// kiln.players' last scan, which may come later in the step), has the same handle (slot and generation), is in game
// (state 3) and drives the player entity. A new connection in the slot has another generation: its player never gets a
// change asked for the one before.
inline bool session_holds(const KilnPlayerSlotRaw& now, uint32_t handle, KilnEntity player) {
    return handle != 0 && now.handle == handle && now.state == 3 && now.entity == player;
}

// Where equip can run, from the core's session query: a live server world in this process (a dedicated server's, or the
// host's). An unanswered query (the core's tick delivery faulted) or no live server world: nothing would complete a
// request, so it is refused.
enum class EquipHere { Yes, NoServerWorld, Unknown };
inline EquipHere equip_here(KilnResult session_rc, uint32_t session_state) {
    if (session_rc != KILN_OK) return EquipHere::Unknown;
    return session_state == KILN_SESSION_DEDICATED || session_state == KILN_SESSION_HOST ? EquipHere::Yes : EquipHere::NoServerWorld;
}

}  // namespace kitems
