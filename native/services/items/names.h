// SPDX-License-Identifier: MIT
// kiln.items: custom names of containers (the Rename button), read from the game's string table. shroudlink finds
// them by scanning the heap for the record layout (names.rs, MIT, bjck/shroudlink v0.8.0); Kiln reaches the table
// itself, the `CustomPlayerStringsSystem` singleton's hash map, and checks every record the same way: the text's
// FNV-1a 64 is the id the entity carries.
#pragma once
#include "kiln_api.h"
#include "services/kiln_items.h"

namespace kitems {

// The name of `entity` in the server `world`. KILN_OK with the text in buf (NUL-terminated, cut to capacity - 1) and
// *out_len its full length; KILN_ERR_NOT_FOUND no name; KILN_ERR_BUFFER_TOO_SMALL cut; KILN_ERR_UNAVAILABLE feature
// off, no table, or a table layout this build does not match. Guarded against faults; the tick thread (the table
// singleton is looked up there once per world); other threads get names only once it has been.
KilnResult name_of(uintptr_t world, KilnEntity entity, char* buf, uint32_t capacity, uint32_t* out_len);
void names_world_closed();                      // forget the cached table pointer
uint32_t names_found();                         // lookups that found a name
void names_detail(char* out, uint32_t cap);     // why names are off or what the table looks like

// Lab: names `entity` through the game's own set_string and writes the id into its component. Returns a message.
bool name_set_for_lab(uintptr_t world, KilnEntity entity, const char* text, char* msg, uint32_t cap);

}  // namespace kitems
