// SPDX-License-Identifier: MIT
// kiln.items v5 host test: the parts of equip and unequip that need no game memory (equip_logic.h): which values are
// equipment slots and where they sit in SlotSelection.equipment, the slot types of the game's equip, request
// validation, the meaning of the game's error codes, and where an item went after a change.
#include <cstdio>
#include <cstring>

#include "../equip_logic.h"

namespace {
int failures = 0;
void check(bool ok, const char* what) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", what);
    failures += !ok;
}

KilnItemsEquipRequest equip(uint8_t slot, uint64_t from) {
    KilnItemsEquipRequest r{};
    r.size = sizeof r;
    r.player = 1;
    r.equipment_slot = slot;
    r.slot = from;
    return r;
}

kitems::Page page(uint32_t entity) {
    kitems::Page p{};
    p.entity = entity;
    for (uint32_t k = 0; k < kitems::kSlotsPerPage; ++k) p.s[k].slot = KILN_SLOT_ID(entity, k);
    return p;
}
}  // namespace

int main() {
    using namespace kitems;
    static_assert(sizeof(KilnItemsEquipRequest) == 32 && sizeof(KilnItemsEquipResult) == 256 &&
                      sizeof(KilnItemsEquipStatus) == 192,
                  "the v5 records keep their sizes (the managed mirror depends on them)");

    // SlotSelection.equipment: 0-16 in order, then HookShot 21, Glider 22, Backpack 23 at 17-19
    bool round = true;
    for (int i = 0; i < 20; ++i) round = round && selection_index(game_slot_of(i)) == i;
    check(round, "every SlotSelection.equipment index maps to a game slot and back");
    check(selection_index(11) == 11 && selection_index(21) == 17 && selection_index(22) == 18 && selection_index(23) == 19,
          "Ring1 is entry 11, HookShot 17, Glider 18, Backpack 19");
    check(selection_index(17) < 0 && selection_index(20) < 0 && selection_index(24) < 0 && selection_index(25) < 0,
          "the building materials, Invalid and MainHand are no equipment slots");
    check(game_slot_of(-1) == 0xFF && game_slot_of(20) == 0xFF, "no game slot outside 0-19");

    // the game's slot types (0x936c30)
    check(slot_type_of(0) == 13 && slot_type_of(5) == 13 && slot_type_of(9) == 17, "vanity and armour slots take armour of their part");
    check(slot_type_of(10) == 37 && slot_type_of(11) == 37, "both ring slots take Armour_Ring");
    check(slot_type_of(12) == 5 && slot_type_of(13) == 18 && slot_type_of(14) == 6 && slot_type_of(15) == 7 && slot_type_of(16) == 8,
          "ranged weapon, shield, arrow, spell, bait");
    check(slot_type_of(21) == 34 && slot_type_of(22) == 35 && slot_type_of(23) == 38, "hook shot, glider, backpack");
    check(slot_type_of(24) == 0 && slot_type_of(0xFF) == 0, "no type outside the table");

    // request validation
    const uint64_t s = KILN_SLOT_ID(2000, 3);
    check(equip_request_valid(equip(11, s)), "an equip of a slot into Ring1");
    check(equip_request_valid(equip(KILN_ITEMS_EQUIP_ANY, s)), "an equip into the item's own slot");
    check(!equip_request_valid(equip(11, 0)), "an equip needs a slot");
    check(!equip_request_valid(equip(18, s)), "a building material is refused");
    check(!equip_request_valid(equip(11, KILN_SLOT_ID(2000, 8))), "a slot index past 7 is refused");
    {
        auto r = equip(11, s);
        r.player = 0;
        check(!equip_request_valid(r), "player 0 is refused");
        r = equip(11, s);
        r.flags = 0x80;
        check(!equip_request_valid(r), "unknown flags are refused");
        r = equip(11, s);
        r.reserved2 = 1;
        check(!equip_request_valid(r), "non-zero reserved fields are refused");
        r = equip(11, 0);
        r.flags = KILN_ITEMS_EQUIP_UNEQUIP;
        check(equip_request_valid(r), "an unequip names the equipment slot only");
        r.equipment_slot = KILN_ITEMS_EQUIP_ANY;
        check(!equip_request_valid(r), "an unequip of ANY is refused");
        r = equip(11, s);
        r.flags = KILN_ITEMS_EQUIP_UNEQUIP;
        check(!equip_request_valid(r), "an unequip with a slot is refused");
        r = equip(11, 0);
        r.flags = KILN_ITEMS_EQUIP_UNEQUIP | KILN_ITEMS_EQUIP_DRY_RUN;
        r.pide = 7;
        check(!equip_request_valid(r), "an unequip with an item entity is refused");
    }

    // the game's error codes
    check(equip_status_of(0, false, true) == KILN_ITEMS_DONE, "0 is done");
    check(equip_status_of(0x15, false, true) == KILN_ITEMS_SLOT_CHANGED, "0x15: no item where the game looked");
    check(equip_status_of(0x11, true, false) == KILN_ITEMS_NO_ROOM, "an unequip refused with no free Generic slot is no room");
    check(equip_status_of(0x11, true, true) == KILN_ITEMS_REFUSED, "an unequip refused with room is refused");
    check(equip_status_of(0x11, false, false) == KILN_ITEMS_REFUSED && equip_status_of(0x1f, false, true) == KILN_ITEMS_REFUSED,
          "an equip refusal is refused");
    check(std::strstr(equip_error_text(0x11, false, false, true), "does not fit") != nullptr, "a wrong slot type says so");
    check(std::strstr(equip_error_text(0x11, true, true, false), "no free slot") != nullptr, "no room says so");
    check(equip_error_text(0, false, true, true)[0] == 0, "no text for success");

    // where an item went
    Page before[2] = {page(100), page(101)}, after[2] = {page(100), page(101)};
    before[0].s[2] = {KILN_SLOT_ID(100, 2), 0x55, 1, 7000};  // the ring, item entity 7000, in the backpack
    after[1].s[0] = {KILN_SLOT_ID(101, 0), 0x55, 1, 7000};   // now in the equipment page
    check(where_now(before, 2, after, 2, 0x55, 7000, 0) == KILN_SLOT_ID(101, 0), "an item entity is found where it is now");
    check(where_now(before, 2, after, 2, 0x55, 7000, KILN_SLOT_ID(101, 0)) == 0, "the excluded slot is never returned");
    before[0].s[5] = {KILN_SLOT_ID(100, 5), 0x66, 10, 0};    // arrows
    after[0].s[5] = {KILN_SLOT_ID(100, 5), 0x66, 10, 0};
    after[0].s[6] = {KILN_SLOT_ID(100, 6), 0x66, 20, 0};     // 20 arrows arrived in slot 6
    check(where_now(before, 2, after, 2, 0x66, 0, 0) == KILN_SLOT_ID(100, 6), "a plain stack is found where its count grew");
    after[0].s[6] = {};
    after[0].s[5].count = 30;                                // ... or merged into the stack it had
    check(where_now(before, 2, after, 2, 0x66, 0, 0) == KILN_SLOT_ID(100, 5), "a merged plain stack is found");
    check(where_now(before, 2, after, 2, 0x77, 0, 0) == 0 && where_now(before, 2, after, 2, 0, 0, 0) == 0,
          "nothing found: 0");

    std::printf("equip-test: %s\n", failures ? "FAILED" : "all passed");
    return failures ? 1 : 0;
}
