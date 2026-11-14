// SPDX-License-Identifier: MIT
// kiln.items v4: item instances. The stamp table (stamps.h) and the reads of item entities: where an item entity sits
// (holder, slot, link, the save record's flat slot) and the fields the game's save record builder writes for it
// (docs/kiln/services/items.md "Item instances"). Reads only; tick thread, except the stamp calls, which the commits
// make on the program's worker thread.
#pragma once
#include <cstdint>
#include <vector>

#include "kiln_api.h"
#include "services/kiln_items.h"
#include "stamps.h"

namespace kitems {

// The one table of the process.
StampTable& stamps();

// The current step (items.cpp), for an entry's age.
uint64_t current_step();

// True when feature items.instances resolved (E.instances_ok) and the stamps can be handed out.
bool instances_on();

// The stamp to deliver with a stack's item entity: 0 when instances are off, no pide, or an unstampable id.
uint32_t stamp_for(uint32_t pide, uint32_t item);
// A take's commit destroyed the item entity: its stamp for the result; the id is reported gone from now on.
uint32_t stamp_destroyed(uint32_t pide, uint32_t item);

// Lists every entity with an InventorySetup (items.cpp: kiln.world's find, else the service's own scan).
using ListHolders = bool (*)(std::vector<KilnEntity>* out);

struct InstanceEnv {
    uintptr_t world = 0;
    void* attributes = nullptr;  // impact::AttributeRegistry
    uint64_t step = 0;
    ListHolders list = nullptr;
};

// read_instances / read_holder (kiln_items.h). The env's world must be the server world; tick thread.
KilnResult instances_read(const InstanceEnv& env, KilnItemsInstance* items, uint32_t count, uint32_t stride,
                          uint32_t flags);
KilnResult instances_read_holder(const InstanceEnv& env, KilnEntity holder, KilnItemsInstance* out, uint32_t capacity,
                                 uint32_t stride, uint32_t* out_count, uint8_t* out_initialized);

// Pre: the id counter check (a counter that went down forgets every stamp) and the occasional prune.
void instances_pre(uintptr_t world, uint64_t step);
// A server world closed or started: every stamp and the holder map are forgotten.
void instances_world_reset();
void instances_status(KilnItemsInstancesStatus* out);
// kiln.toolwear: an item entity's stamp and the stamp table's epoch as one answer (StampTable::capture), any thread;
// the epoch now; an entity's stamp without making one (0: none, or gone).
bool instances_capture(uint32_t entity, uint32_t item, uint64_t step, uint32_t* stamp, uint64_t* epoch);
uint64_t instances_epoch();
uint32_t instances_peek(uint32_t entity);

}  // namespace kitems
