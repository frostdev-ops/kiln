// SPDX-License-Identifier: MIT
// kiln.items: the logic of moves, transfer rules and container names that needs no game memory (so a host test can run
// it): where a stack goes, who gave what to whom (the ledger), which stacks a transfer rule keeps, what a name record
// looks like. The game side (engine.cpp, xfer.cpp, names.cpp) feeds it snapshots read from inventories.
//
// The destination search and the protection of a player's own stacks follow shroudlink's `move_stack_in` /
// `find_destination` (MIT, bjck/shroudlink v0.8.0, native/plugins/shroudlink/upstream/server/src/game_api.rs); the name
// record layout is the one its heap scan (names.rs) recognises. Credit: docs/kiln/engines/shroudlink.md.
#pragma once
#include <cstddef>
#include <cstdint>

#include "services/kiln_items.h"

namespace kitems {

constexpr uint32_t kSlotsPerPage = 8;

// One slot of an 8-slot inventory as the game stores it ({item, count, pide}).
struct Stack {
    uint64_t slot = 0;  // KILN_SLOT_ID(inventory entity, index)
    uint32_t item = 0;
    uint32_t count = 0;
    uint32_t pide = 0;  // gear: the item entity (0 or 0xFFFFFFFF = none)
};

// An 8-slot inventory entity with its stacks.
struct Page {
    uint32_t entity = 0;
    Stack s[kSlotsPerPage];
};

inline uint32_t pide_of(const Stack& s) { return s.pide == 0xFFFFFFFFu ? 0u : s.pide; }
inline bool is_empty(const Stack& s) { return s.item == 0 || s.count == 0; }

// ---- destination ----
// A partial stack of `item` (below `max_stack`), otherwise the first empty slot; pages in `skip` are never chosen
// (a player's action bar); `prefer` (a slot id, 0 = none) is taken first when it can hold the item. Gear (a stack
// that carries an item entity) has max_stack 1 in effect: it only ever goes to an empty slot.
// Returns false when nothing fits.
bool find_destination(const Page* pages, size_t n, uint32_t item, uint32_t max_stack, bool gear, const uint32_t* skip,
                      size_t skip_n, uint64_t prefer, uint64_t* out);

// ---- the ledger: who gave what to whom ----
struct Change {
    uint64_t from_slot = 0;
    uint64_t to_slot = 0;
    uint32_t item = 0;
    uint32_t amount = 0;
    uint32_t pide = 0;
};

struct LedgerTotals {
    uint32_t done = 0;    // items that left `from` and arrived in `to`
    uint32_t stacks = 0;  // source stacks that gave something
    uint32_t lost = 0;    // items that left `from` without arriving anywhere (should be 0)
    uint32_t gained = 0;  // items that arrived in `to` that did not come from `from` (should be 0)
};

// Pairs what left the `from` pages with what arrived in the `to` pages, from snapshots taken before and after (the
// pages of one entity appear in the same order in both). Gear is paired by its item entity, plain stacks per item in
// slot order. Writes at most `capacity` entries; the totals count all of them.
size_t build_ledger(const Page* from_before, const Page* from_after, size_t from_n, const Page* to_before,
                    const Page* to_after, size_t to_n, Change* out, size_t capacity, LedgerTotals* totals);

// ---- transfer rules ----
struct RuleView {
    uint32_t type;      // KILN_TRANSFER_KEEP_SLOT / KEEP_ITEM / DENY
    uint32_t kinds;     // mask, 0 = both
    uint32_t entity;
    uint32_t item;
    uint64_t slot;
};

constexpr size_t kMaxKeeps = 512;  // stacks one transfer may keep (a player's inventory has far fewer)

struct Verdict {
    bool deny = false;
    uint32_t keep_count = 0;
    uint64_t keep[kMaxKeeps];
};

// What the rules say about a transfer of `kind` (KILN_TRANSFER_QUICK_STACK / MOVE_ALL) from `from` to `to`, whose
// source pages are `from_pages`: DENY rules that name either end, KEEP_SLOT slots that lie in the source pages,
// KEEP_ITEM stacks of the item there. Appends to `v`.
void apply_rules(const RuleView* rules, size_t n, uint32_t kind, uint32_t from, uint32_t to, const Page* from_pages,
                 size_t from_n, Verdict* v);

inline void keep_slot(Verdict* v, uint64_t slot) {
    for (uint32_t i = 0; i < v->keep_count; ++i)
        if (v->keep[i] == slot) return;
    if (v->keep_count < kMaxKeeps) v->keep[v->keep_count++] = slot;
}

// ---- container names ----
// The game's custom strings: id = FNV-1a 64 of the UTF-8 text.
uint64_t fnv1a64(const uint8_t* text, size_t len);

// Looks for a name record for `key` inside the `size` bytes at `rec` (which live at address `addr`, so the record's
// self-pointer can be checked): `{u64 key; char* text = &record + 0x28; u32 len; u32 pad; u64 capacity = 0x100; ...;
// char text[256]}`, found at any 8-aligned offset below `max_offset`. Accepts it only when the key matches, the text is
// 1..255 bytes, NUL-terminated and its FNV-1a 64 is the key. On success copies the text to `out` (NUL-terminated,
// capacity `out_cap`), sets *len to its full length and *offset to where the record starts, and returns true.
bool parse_name_record(const uint8_t* rec, size_t size, uintptr_t addr, uint64_t key, size_t max_offset, char* out,
                       size_t out_cap, uint32_t* len, size_t* offset);

}  // namespace kitems
