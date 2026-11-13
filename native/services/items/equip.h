// SPDX-License-Identifier: MIT
// kiln.items v5: equip and unequip a player's items through the game's own functions, inside a second program of the
// service ("kiln_items_equip", an entity program over the player characters, registered through kiln.world). See
// native/core/abi/services/kiln_items.h "equipment" and docs/re/items.md section 16.
#pragma once
#include <cstdint>

#include "kiln_api.h"
#include "services/kiln_items.h"
#include "services/kiln_world.h"

namespace kitems {

// A ticket from the service's one counter (items.cpp), so cancel() can tell every kind of request apart.
uint64_t items_next_ticket();

// on_ready: registers kiln_items_equip through kiln.world. Without kiln.world (w null) or without feature items.equip
// equip stays off and equip_status says why.
void equip_register(const KilnWorldApi* w);
// Tick thread, Pre: the program's state, requests for an entity that is no player character, timeouts, callbacks.
void equip_pre(uintptr_t world, uint64_t step);
// The server world closed: waiting requests end as DROPPED; the next world's program verifies its accessors again.
void equip_world_closed();

KilnResult equip_submit(KilnOwner owner, const KilnItemsEquipRequest* r, KilnItemsEquipDoneFn done, void* user,
                        uint64_t* out_ticket);
KilnResult equip_status(KilnItemsEquipStatus* out);
bool equip_cancel(uint64_t ticket);    // drops the callback; false: not an equip ticket
void equip_cancel_owner(KilnOwner owner);

}  // namespace kitems
