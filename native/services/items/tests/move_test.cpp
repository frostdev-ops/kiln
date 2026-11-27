// SPDX-License-Identifier: MIT
// Host test of kiln.items' moves, ledger, transfer rules, handlers, reports and hooks, with a fake inventory world in
// place of the game (no game, runs under plain Wine: native/services/items/tests/run.sh).
//
// What this proves is the service's own logic and the hook's chaining mechanics; not the game's behaviour (that is
// the lab: mods/tests/items-test). Modes: no argument = everything; "chain-before" / "chain-after" = the hook chain
// with another loader's hook installed before / after ours (hooks are never removed, so each needs its own process).
#include <windows.h>

#include <array>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <string>
#include <thread>
#include <vector>

#include "hooks_shim.h"
#include "../engine.cpp"
#include "../storage_guard.h"
#include "../moves.cpp"
#include "../names.cpp"
#include "../xfer.cpp"
#include "../stamps.cpp"
#include "../instances.cpp"

namespace kitems {
void items_log(int level, const char* fmt, ...) {
    char b[512];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    std::printf("  log[%d] %s\n", level, b);
}
const KilnApi* items_api() { return hooks_shim::api(); }  // the hooks go through the kiln.hooks stand-in
KilnOwner items_owner() { return 7; }
uint64_t g_test_step = 1;
uint64_t current_step() { return g_test_step; }  // v4: an entry's age (instances.cpp)
// engine.cpp's give path calls these; this test never gives, and pide.cpp has its own test (pide_test.cpp)
void pide_commit(void* tx, void(KILN_CALL* commit)(void*), uint32_t, uint8_t, uint32_t) { commit(tx); }
bool g_initial_equipped=false;
void pide_commit(void* tx, void(KILN_CALL* commit)(void*), const PideOptions& o) {
    const bool previous=g_initial_equipped;
    g_initial_equipped=o.equipped && o.equipped_slot;
    __try { commit(tx); } __finally { g_initial_equipped=previous; }
}
}  // namespace kitems

using namespace kitems;

namespace {

int failures = 0;
void check(bool ok, const char* text) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", text);
    failures += !ok;
}

// ---------------------------------------------------------------------------------------------- the fake world ----
enum { T_FUEL = 1, T_INV, T_EMPTY, T_SETUP, T_ITEMSTATE, T_OWNER, T_SEL };
uint16_t g_type[8];
uint8_t g_registry_bytes[64];
uint8_t g_storage_bytes[64];
uint8_t g_item_bytes[8][0x700];
std::map<std::pair<uint32_t, uint32_t>, std::vector<uint8_t>> g_comps;
int g_vanilla_calls = 0;
uint32_t g_fault_inventory_once = 0;
const void* g_readonly_inventory = nullptr;
bool g_vanilla_sees_kept = false;
std::vector<std::string> g_order;

constexpr uint32_t WOOD = 10, IRON = 11, SWORD = 7;
constexpr uint32_t A = 100, A2 = 101, B = 200, B2 = 201, P = 1, PBAR = 2;

std::vector<uint8_t>& comp(uint32_t type, uint32_t entity, size_t size) {
    auto& v = g_comps[{type, entity}];
    if (v.size() < size) v.resize(size, 0);
    return v;
}
uint8_t* stack(uint32_t inventory, uint32_t k) { return comp(T_INV, inventory, 96).data() + 12 * k; }
void put(uint32_t inventory, uint32_t k, uint32_t item, uint32_t count, uint32_t pide = 0) {
    uint8_t* s = stack(inventory, k);
    std::memcpy(s, &item, 4);
    std::memcpy(s + 4, &count, 4);
    std::memcpy(s + 8, &pide, 4);
}
uint32_t item_at(uint32_t inv, uint32_t k) { return *reinterpret_cast<uint32_t*>(stack(inv, k)); }
uint32_t count_at(uint32_t inv, uint32_t k) { return *reinterpret_cast<uint32_t*>(stack(inv, k) + 4); }
uint32_t pide_at(uint32_t inv, uint32_t k) { return *reinterpret_cast<uint32_t*>(stack(inv, k) + 8); }
uint32_t total(uint32_t item, std::initializer_list<uint32_t> invs) {
    uint32_t n = 0;
    for (uint32_t inv : invs)
        for (uint32_t k = 0; k < 8; ++k)
            if (item_at(inv, k) == item) n += count_at(inv, k);
    return n;
}
void make_setup(uint32_t entity, std::initializer_list<uint32_t> links) {
    auto& s = comp(T_SETUP, entity, kSetupSize);
    uint32_t i = 0;
    for (uint32_t l : links) {
        std::memcpy(s.data() + 4 * i, &l, 4);
        s[0x40 + i] = 4;
        ++i;
    }
    s[0x53] = 1;
    for (uint32_t l : links) comp(T_INV, l, 96);
}
void reset_world() {
    g_comps.clear();
    make_setup(A, {A, A2});
    make_setup(B, {B, B2});
    make_setup(P, {P, PBAR});
    auto& sel = comp(T_SEL, P, 216);
    std::memset(sel.data(), 0, sel.size());
    const uint32_t bar = PBAR;
    std::memcpy(sel.data() + 0xa0, &bar, 4);
    const uint32_t eq_entity = P;  // equipment[0]: slot 7 of the backpack
    std::memcpy(sel.data(), &eq_entity, 4);
    sel[4] = 7;
}

// ---------------------------------------------------------------------------------------------- fake game code ----
PtrSize* __fastcall fake_read_other(PtrSize* out, void*, uint64_t accessor, uint32_t entity) {
    const uint16_t type = *reinterpret_cast<const uint16_t*>(accessor);
    if (type == T_INV && entity == g_fault_inventory_once) {
        g_fault_inventory_once = 0;
        RaiseException(EXCEPTION_ACCESS_VIOLATION, 0, 0, nullptr);
    }
    auto it = g_comps.find({type, entity});
    out->ptr = it == g_comps.end() ? nullptr : it->second.data();
    out->size = it == g_comps.end() ? 0 : it->second.size();
    if (type == T_INV && entity == A2 && g_readonly_inventory) out->ptr = const_cast<void*>(g_readonly_inventory);
    return out;
}
void* __fastcall fake_job_storage(void*) { return g_storage_bytes; }
uint16_t* __fastcall fake_component_map_find(void*, const uint32_t* hash) {
    for (int i = 0; i < A_COUNT; ++i)
        if (kAccHashes[i] == *hash) return &g_type[i + 1];
    return nullptr;
}
void* __fastcall fake_item_find(uint32_t id, void*) {
    const bool known = id == WOOD || id == IRON || id == SWORD || id == 12 || id == 13 || id == 14 || id == 8 || id == 9;  // + issue #109's items
    return known ? g_item_bytes[id % 8] : nullptr;
}

// The game's slot transfer: mode 0 moves what fits of the whole stack (item entity and all), 2 moves one item.
Result12* __fastcall fake_transfer(Result12* out, void*, uint64_t dst, uint64_t src, int32_t mode) {
    std::memset(out, 0, 12);
    uint8_t* d = stack(static_cast<uint32_t>(dst), static_cast<uint32_t>(dst >> 32));
    uint8_t* s = stack(static_cast<uint32_t>(src), static_cast<uint32_t>(src >> 32));
    uint32_t sitem, scount, spide, ditem, dcount, dpide;
    std::memcpy(&sitem, s, 4);
    std::memcpy(&scount, s + 4, 4);
    std::memcpy(&spide, s + 8, 4);
    std::memcpy(&ditem, d, 4);
    std::memcpy(&dcount, d + 4, 4);
    std::memcpy(&dpide, d + 8, 4);
    if (scount == 0) return out;
    const uint32_t max = *reinterpret_cast<uint16_t*>(g_item_bytes[sitem % 8] + 0x14);
    if (dcount != 0 && (ditem != sitem || spide != 0 || dpide != 0)) return out->error = 0x15, out;
    uint32_t n = std::min(scount, max - dcount);
    if (mode == 2) n = std::min(n, 1u);
    if (n == 0) return out;
    const uint32_t ncount = dcount + n, rest = scount - n;
    std::memcpy(d, &sitem, 4);
    std::memcpy(d + 4, &ncount, 4);
    std::memcpy(d + 8, &spide, 4);
    std::memcpy(s + 4, &rest, 4);
    if (rest == 0) std::memset(s, 0, 12);
    out->value = n;
    return out;
}

// One whole-inventory move: from's stacks into to's, only the items to already has (quick stack), or all.
__declspec(noinline) uint32_t move_inventory(uint32_t from, uint32_t to, bool only_known) {
    uint32_t moved = 0;
    for (uint32_t fi : {from, from + 1}) {
        for (uint32_t k = 0; k < 8; ++k) {
            if (count_at(fi, k) == 0) continue;  // hidden stacks read as empty
            const uint32_t item = item_at(fi, k);
            if (only_known && total(item, {to, to + 1}) == 0) continue;
            for (uint32_t ti : {to, to + 1}) {
                for (uint32_t j = 0; j < 8 && count_at(fi, k) != 0; ++j) {
                    Result12 r[3] = {};
                    fake_transfer(&r[0], nullptr, KILN_SLOT_ID(ti, j), KILN_SLOT_ID(fi, k), 0);
                    moved += r[0].value;
                }
            }
        }
    }
    return moved;
}

__declspec(noinline) Result12* __fastcall fake_quick_stack(Result12* out, void* tx, uint32_t to, uint32_t from, void*) {
    ++g_vanilla_calls;
    g_order.push_back("orig");
    volatile uint32_t sink = to ^ from ^ static_cast<uint32_t>(reinterpret_cast<uintptr_t>(tx));
    (void)sink;
    std::memset(out, 0, 12);
    out->value = move_inventory(from, to, true);
    return out;
}
__declspec(noinline) Result12* __fastcall fake_move_all(Result12* out, void* tx, uint32_t from, uint32_t to, uint32_t mode, uint8_t flag) {
    ++g_vanilla_calls;
    g_order.push_back("orig");
    volatile uint32_t sink = to ^ from ^ mode ^ flag ^ static_cast<uint32_t>(reinterpret_cast<uintptr_t>(tx));
    (void)sink;
    std::memset(out, 0, 12);
    out->value = move_inventory(from, to, false);
    return out;
}

// The game's transaction as the hooks read it.
struct FakeTx {
    void* words[0x30] = {};
    void* job_holder[2] = {};
    FakeTx() {
        job_holder[0] = g_storage_bytes;  // *ctx = the job; job_storage ignores it
        words[0] = job_holder;
        words[1] = g_item_bytes;
        words[2] = g_item_bytes;
        words[0xF] = g_item_bytes;
        words[7] = &g_type[T_FUEL];
        words[4] = &g_type[T_INV];
        words[0x14] = &g_type[T_EMPTY];
        words[3] = &g_type[T_SETUP];
        words[5] = &g_type[T_ITEMSTATE];
        words[0x12] = &g_type[T_OWNER];
        words[0x1D] = &g_type[T_SEL];
    }
};

void setup_engine() {
    for (int i = 0; i < 8; ++i) g_type[i] = static_cast<uint16_t>(i);  // a component's type index = its enum value
    // A_FUEL..A_SLOTSEL are in kAccNames order: Fuel, Inventory, InventoryEmptyState, InventorySetup, ItemState,
    // OwnerRelationship, SlotSelection = types 1..7 by construction of fake_component_map_find
    std::memset(g_storage_bytes, 0, sizeof g_storage_bytes);
    *reinterpret_cast<void**>(g_storage_bytes) = g_registry_bytes;
    for (auto& i : g_item_bytes) std::memset(i, 0, sizeof i);
    E.item_max_stack = 0x14;
    E.item_flags = 0x5d8;
    E.setup_initialized = 0x53;
    E.world_storage = 0;
    E.registry_components = 0;
    E.hmap_bits = 8;
    *reinterpret_cast<uint16_t*>(g_item_bytes[WOOD % 8] + 0x14) = 250;
    *reinterpret_cast<uint16_t*>(g_item_bytes[IRON % 8] + 0x14) = 100;
    *reinterpret_cast<uint16_t*>(g_item_bytes[SWORD % 8] + 0x14) = 1;
    *reinterpret_cast<uint32_t*>(g_item_bytes[SWORD % 8] + 0x5d8) = 2;  // HasPIDE
    E.read_other = &fake_read_other;
    E.job_storage = &fake_job_storage;
    E.component_map_find = &fake_component_map_find;
    E.item_find = &fake_item_find;
    E.slot_transfer = &fake_transfer;
    E.quick_stack = &fake_quick_stack;
    E.move_all = &fake_move_all;
    E.area_deposit = 1;
    E.transfer_ok = true;
    E.move_ok = true;
    reset_world();
}

// ---------------------------------------------------------------------------------------------- pure logic ----
Page page(uint32_t entity, std::initializer_list<std::array<uint32_t, 3>> stacks) {
    Page p;
    p.entity = entity;
    uint32_t k = 0;
    for (auto s : stacks) {
        p.s[k] = Stack{KILN_SLOT_ID(entity, k), s[0], s[1], s[2]};
        ++k;
    }
    for (; k < 8; ++k) p.s[k].slot = KILN_SLOT_ID(entity, k);
    return p;
}

void logic_tests() {
    // destination
    {
        Page to[2] = {page(200, {{WOOD, 250, 0}, {IRON, 99, 0}, {WOOD, 100, 0}}), page(201, {})};
        uint64_t d = 0;
        check(find_destination(to, 2, WOOD, 250, false, nullptr, 0, 0, &d) && d == KILN_SLOT_ID(200, 2), "a partial stack of the item is chosen before an empty slot");
        check(find_destination(to, 2, IRON, 100, false, nullptr, 0, 0, &d) && d == KILN_SLOT_ID(200, 1), "a partial iron stack takes iron");
        check(find_destination(to, 2, 99, 10, false, nullptr, 0, 0, &d) && d == KILN_SLOT_ID(200, 3), "an unknown item goes to the first empty slot");
        const uint32_t skip[1] = {200};
        check(find_destination(to, 2, 99, 10, false, skip, 1, 0, &d) && d == KILN_SLOT_ID(201, 0), "a skipped page (a player's action bar) is never chosen");
        check(find_destination(to, 2, WOOD, 250, false, nullptr, 0, KILN_SLOT_ID(201, 4), &d) && d == KILN_SLOT_ID(201, 4), "a preferred empty slot is taken first");
        check(find_destination(to, 2, WOOD, 250, false, nullptr, 0, KILN_SLOT_ID(200, 0), &d) && d == KILN_SLOT_ID(200, 2), "a preferred full stack is not");
        check(find_destination(to, 2, SWORD, 1, true, nullptr, 0, 0, &d) && d == KILN_SLOT_ID(200, 3), "gear only ever goes to an empty slot");
        Page full[1] = {page(300, {{WOOD, 250, 0}, {WOOD, 250, 0}, {WOOD, 250, 0}, {WOOD, 250, 0}, {WOOD, 250, 0}, {WOOD, 250, 0}, {WOOD, 250, 0}, {WOOD, 250, 0}})};
        check(!find_destination(full, 1, WOOD, 250, false, nullptr, 0, 0, &d), "a full inventory has no destination");
    }
    // ledger
    {
        Page fb[1] = {page(100, {{WOOD, 9, 0}, {IRON, 5, 0}, {SWORD, 1, 77}})};
        Page fa[1] = {page(100, {{IRON, 5, 0}})};
        fa[0].s[0] = Stack{KILN_SLOT_ID(100, 0), 0, 0, 0};
        fa[0].s[1] = Stack{KILN_SLOT_ID(100, 1), IRON, 5, 0};
        fa[0].s[2] = Stack{KILN_SLOT_ID(100, 2), 0, 0, 0};
        Page tb[1] = {page(200, {{WOOD, 200, 0}})};
        Page ta[1] = {page(200, {{WOOD, 209, 0}, {SWORD, 1, 77}})};
        Change ch[8];
        LedgerTotals t;
        const size_t n = build_ledger(fb, fa, 1, tb, ta, 1, ch, 8, &t);
        check(n == 2 && t.done == 10 && t.stacks == 2 && t.lost == 0 && t.gained == 0, "the ledger pairs 9 wood into a stack and one sword, nothing lost or gained");
        check(ch[0].from_slot == KILN_SLOT_ID(100, 0) && ch[0].to_slot == KILN_SLOT_ID(200, 0) && ch[0].amount == 9 && ch[0].pide == 0, "wood: slot to slot, 9, no item entity");
        check(ch[1].from_slot == KILN_SLOT_ID(100, 2) && ch[1].to_slot == KILN_SLOT_ID(200, 1) && ch[1].amount == 1 && ch[1].pide == 77, "gear: the same item entity 77 arrives");
        // a stack split over two targets, two sources into one
        Page fb2[1] = {page(100, {{WOOD, 100, 0}, {WOOD, 50, 0}})};
        Page fa2[1] = {page(100, {})};
        Page tb2[1] = {page(200, {{WOOD, 200, 0}})};
        Page ta2[1] = {page(200, {{WOOD, 250, 0}, {WOOD, 100, 0}})};
        const size_t m = build_ledger(fb2, fa2, 1, tb2, ta2, 1, ch, 8, &t);
        check(m == 3 && t.done == 150 && t.stacks == 2 && ch[0].amount == 50 && ch[1].amount == 50 && ch[2].amount == 50 && ch[1].from_slot == KILN_SLOT_ID(100, 0) && ch[2].from_slot == KILN_SLOT_ID(100, 1),
              "a 100-stack and a 50-stack into one 200-stack that had 50 room and a new slot: three pieces in slot order");
        // lost and gained show up
        Page fa3[1] = {page(100, {{WOOD, 90, 0}})};
        Page fb3[1] = {page(100, {{WOOD, 100, 0}})};
        Page tb3[1] = {page(200, {})};
        Page ta3[1] = {page(200, {{IRON, 4, 0}})};
        build_ledger(fb3, fa3, 1, tb3, ta3, 1, ch, 8, &t);
        check(t.done == 0 && t.lost == 10 && t.gained == 4, "items that left without arriving, and arrivals from nowhere, are counted");
        // capacity: written is cut, totals are not
        const size_t c = build_ledger(fb2, fa2, 1, tb2, ta2, 1, ch, 1, &t);
        check(c == 1 && t.done == 150, "the ledger is cut at the capacity, its totals are not");
    }
    // rules
    {
        Page from[1] = {page(100, {{WOOD, 9, 0}, {IRON, 5, 0}, {WOOD, 4, 0}})};
        Verdict v;
        RuleView rules[] = {{KILN_TRANSFER_KEEP_SLOT, 0, 0, 0, KILN_SLOT_ID(100, 1)}, {KILN_TRANSFER_KEEP_SLOT, 0, 0, 0, KILN_SLOT_ID(999, 0)},
                            {KILN_TRANSFER_KEEP_ITEM, KILN_TRANSFER_MOVE_ALL, 0, WOOD, 0}};
        apply_rules(rules, 3, KILN_TRANSFER_QUICK_STACK, 100, 200, from, 1, &v);
        check(!v.deny && v.keep_count == 1 && v.keep[0] == KILN_SLOT_ID(100, 1), "a locked slot is kept; a slot of another inventory and a rule for the other kind are not");
        Verdict w;
        apply_rules(rules, 3, KILN_TRANSFER_MOVE_ALL, 100, 200, from, 1, &w);
        check(w.keep_count == 3, "keep item: both wood stacks and the locked iron stack for a move all");
        RuleView own[] = {{KILN_TRANSFER_KEEP_ITEM, 0, 5, WOOD, 0}};
        Verdict x;
        apply_rules(own, 1, KILN_TRANSFER_QUICK_STACK, 100, 200, from, 1, &x);
        check(x.keep_count == 0, "keep item with an owner applies only when that entity is the source");
        RuleView deny[] = {{KILN_TRANSFER_DENY, KILN_TRANSFER_QUICK_STACK, 200, 0, 0}};
        Verdict y, z;
        apply_rules(deny, 1, KILN_TRANSFER_QUICK_STACK, 100, 200, from, 1, &y);
        apply_rules(deny, 1, KILN_TRANSFER_QUICK_STACK, 200, 100, from, 1, &z);
        Verdict q;
        apply_rules(deny, 1, KILN_TRANSFER_MOVE_ALL, 100, 200, from, 1, &q);
        check(y.deny && z.deny && !q.deny, "deny cancels a transfer into or out of the container, for its kinds only");
    }
    // names
    {
        check(fnv1a64(nullptr, 0) == 0xcbf29ce484222325ull && fnv1a64(reinterpret_cast<const uint8_t*>("a"), 1) == 0xaf63dc4c8601ec8cull, "FNV-1a 64 matches the reference vectors");
        alignas(8) uint8_t rec[0x28 + 0x100 + 0x40] = {};
        const char* name = "Ores";
        const uint64_t key = fnv1a64(reinterpret_cast<const uint8_t*>(name), 4);
        auto build = [&](size_t off) {
            std::memset(rec, 0, sizeof rec);
            const uint64_t ptr = reinterpret_cast<uintptr_t>(rec) + off + 0x28, cap = 0x100;
            const uint32_t len = 4;
            std::memcpy(rec + off, &key, 8);
            std::memcpy(rec + off + 8, &ptr, 8);
            std::memcpy(rec + off + 0x10, &len, 4);
            std::memcpy(rec + off + 0x18, &cap, 8);
            std::memcpy(rec + off + 0x28, name, 5);
        };
        char out[64];
        uint32_t len = 0;
        size_t off = 99;
        build(0);
        check(parse_name_record(rec, sizeof rec, reinterpret_cast<uintptr_t>(rec), key, 0x38, out, sizeof out, &len, &off) && !std::strcmp(out, "Ores") && len == 4 && off == 0,
              "a name record is read: key, self-pointer, capacity, length and the text's hash all agree");
        build(0x10);
        check(parse_name_record(rec, sizeof rec, reinterpret_cast<uintptr_t>(rec), key, 0x38, out, sizeof out, &len, &off) && off == 0x10, "the record is found at an offset inside what the table points at");
        check(!parse_name_record(rec, sizeof rec, reinterpret_cast<uintptr_t>(rec), key ^ 1, 0x38, out, sizeof out, &len, &off), "another key finds nothing");
        rec[0x10 + 0x28] = 'X';
        check(!parse_name_record(rec, sizeof rec, reinterpret_cast<uintptr_t>(rec), key, 0x38, out, sizeof out, &len, &off), "a text whose hash is not the key is refused");
        build(0);
        const uint64_t bad_ptr = 0x1234;
        std::memcpy(rec + 8, &bad_ptr, 8);
        check(!parse_name_record(rec, sizeof rec, reinterpret_cast<uintptr_t>(rec), key, 0x38, out, sizeof out, &len, &off), "a record whose text pointer is not its own is refused");
        build(0);
        char small[3];
        check(parse_name_record(rec, sizeof rec, reinterpret_cast<uintptr_t>(rec), key, 0x38, small, sizeof small, &len, &off) && len == 4 && !std::strcmp(small, "Or"),
              "a short buffer gets the cut text and the full length");
        // v9: a 256-byte name fills the record (the game's maximum; the zeroed tail ends it), 257 is no record of the game
        {
            alignas(8) uint8_t big[0x28 + 0x100 + 0x40] = {};
            const std::string text(256, 'q');
            const uint64_t k = fnv1a64(reinterpret_cast<const uint8_t*>(text.data()), text.size());
            const uint64_t ptr = reinterpret_cast<uintptr_t>(big) + 0x28, cap = 0x100;
            uint32_t n = 256;
            std::memcpy(big, &k, 8);
            std::memcpy(big + 8, &ptr, 8);
            std::memcpy(big + 0x10, &n, 4);
            std::memcpy(big + 0x18, &cap, 8);
            std::memcpy(big + 0x28, text.data(), 256);
            char whole[300];
            check(parse_name_record(big, sizeof big, reinterpret_cast<uintptr_t>(big), k, 0x38, whole, sizeof whole, &len, &off) && len == 256 &&
                      text == whole,
                  "a 256-byte name (the game's maximum) is read whole");
            n = 257;
            std::memcpy(big + 0x10, &n, 4);
            check(!parse_name_record(big, sizeof big, reinterpret_cast<uintptr_t>(big), k, 0x38, whole, sizeof whole, &len, &off),
                  "a length past the record's 256 bytes is refused");
        }
    }
}

// ---------------------------------------------------------------------------------------------- engine, fake world ----
void engine_tests() {
    reset_world();
    FakeTx tx;
    Access a;
    check(access_from_tx(&tx, &a) && a.storage == g_storage_bytes, "the game's transaction words check out against the component map");
    FakeTx broken;
    broken.words[4] = &g_type[T_FUEL];  // the Inventory accessor of another type
    Access b;
    check(!access_from_tx(&broken, &b) || g_tx_registry.load() != nullptr, "(a transaction whose accessors do not match is refused once checked)");
    // read_pages / guards
    put(A, 0, WOOD, 9);
    put(A, 3, IRON, 5);
    put(A2, 1, SWORD, 1, 77);
    PageSet ps;
    bool init = false;
    check(read_pages(a, A, &ps, &init) && init && ps.n == 2 && ps.p[0].entity == A && ps.p[0].s[0].count == 9 && ps.p[1].s[1].pide == 77, "pages of an entity are read in link order");
    PlayerGuard pg;
    player_guard(a, P, &pg);
    check(pg.is_player && pg.actionbar[0] == PBAR && pg.protects(KILN_SLOT_ID(P, 7)) && !pg.protects(KILN_SLOT_ID(P, 6)), "a player's selection gives the action bar and the equipped slot");
    player_guard(a, A, &pg);
    check(!pg.is_player, "a chest has no selection");
    // move_stack
    MoveOutcome mo;
    uint32_t moved = move_stack_in(a, &tx, A, KILN_SLOT_ID(A, 0), B, 0, 0, false, false, &mo);
    check(moved == 9 && mo.status == KILN_ITEMS_DONE && total(WOOD, {B, B2}) == 9 && total(WOOD, {A, A2}) == 0, "move_stack: a whole stack moves");
    put(B, 5, WOOD, 3);
    moved = move_stack_in(a, &tx, B, KILN_SLOT_ID(B, 5), A, 2, 0, false, false, &mo);
    check(moved == 2 && mo.status == KILN_ITEMS_DONE && count_at(B, 5) == 1 && total(WOOD, {A, A2}) == 2, "move_items: part of a stack moves, one item at a time");
    moved = move_stack_in(a, &tx, B, KILN_SLOT_ID(B, 5), A, 5, 0, false, false, &mo);
    check(moved == 0 && mo.status == KILN_ITEMS_NOT_ENOUGH, "more than the stack holds is refused");
    moved = move_stack_in(a, &tx, A, KILN_SLOT_ID(A2, 1), B, 0, 0, false, false, &mo);
    std::printf("INFO gear move: moved %u status %d error %u why '%s'\n", moved, mo.status, mo.error, mo.why);
    check(moved == 1 && mo.status == KILN_ITEMS_DONE && item_at(A2, 1) == 0 && total(SWORD, {B, B2}) == 1, "gear moves whole with its item entity");
    uint32_t pide_in_b = 0;
    for (uint32_t k = 0; k < 8; ++k) if (item_at(B, k) == SWORD) pide_in_b = pide_at(B, k);
    check(pide_in_b == 77, "the item entity 77 is the same one in the target slot");
    moved = move_stack_in(a, &tx, A, KILN_SLOT_ID(A, 7), B, 0, 0, false, false, &mo);
    check(moved == 0 && mo.status == KILN_ITEMS_SLOT_CHANGED, "an empty slot is refused");
    moved = move_stack_in(a, &tx, A, KILN_SLOT_ID(999, 0), B, 0, 0, false, false, &mo);
    check(moved == 0 && mo.status == KILN_ITEMS_SLOT_CHANGED, "a slot of another entity is refused");
    moved = move_stack_in(a, &tx, A, KILN_SLOT_ID(A, 3), 12345, 0, 0, false, false, &mo);
    check(moved == 0 && mo.status == KILN_ITEMS_NO_INVENTORY, "a target without an inventory is refused");
    // a player's own stacks
    put(P, 7, SWORD, 1, 88);
    put(P, 6, IRON, 3);
    put(PBAR, 0, IRON, 4);
    moved = move_stack_in(a, &tx, P, KILN_SLOT_ID(P, 7), A, 0, 0, false, false, &mo);
    check(moved == 0 && mo.status == KILN_ITEMS_REFUSED, "an equipped slot is refused");
    moved = move_stack_in(a, &tx, P, KILN_SLOT_ID(PBAR, 0), A, 0, 0, false, false, &mo);
    check(moved == 0 && mo.status == KILN_ITEMS_REFUSED, "a stack on the action bar is refused");
    moved = move_stack_in(a, &tx, P, KILN_SLOT_ID(PBAR, 0), A, 0, 0, false, true, &mo);
    check(moved == 4 && mo.status == KILN_ITEMS_DONE, "...unless the action bar is allowed");
    for (uint32_t k = 0; k < 8; ++k) put(A, k, WOOD, 250);
    for (uint32_t k = 0; k < 8; ++k) put(A2, k, WOOD, 250);
    moved = move_stack_in(a, &tx, P, KILN_SLOT_ID(P, 6), A, 0, 0, false, false, &mo);
    check(moved == 0 && mo.status == KILN_ITEMS_NO_ROOM, "a full target is NO_ROOM and nothing moves");
    // a player's action bar is never filled
    for (uint32_t k = 0; k < 8; ++k) { put(B, k, WOOD, 250); put(B2, k, WOOD, 250); }
    reset_world();
    put(A, 0, IRON, 5);
    for (uint32_t k = 0; k < 8; ++k) put(P, k, WOOD, 250);
    moved = move_stack_in(a, &tx, A, KILN_SLOT_ID(A, 0), P, 0, 0, false, false, &mo);
    check(moved == 0 && mo.status == KILN_ITEMS_NO_ROOM && item_at(PBAR, 0) == 0, "a player's action bar is not used as room");
    moved = move_stack_in(a, &tx, A, KILN_SLOT_ID(A, 0), P, 0, 0, false, true, &mo);
    check(moved == 5 && item_at(PBAR, 0) == IRON, "...unless it is allowed");
    reset_world();
}

// ---------------------------------------------------------------------------------------------- hooks ----
// The hooks sit on fake_quick_stack / fake_move_all. Hooks are never removed, so this runs once per process.
struct SeenEvent {
    int calls = 0;
    uint32_t kind = 0, from = 0, to = 0, player = 0, flags = 0;
};
SeenEvent g_seen;
std::vector<KilnItemsTransferReport> g_reports;

void KILN_CALL on_report(const KilnItemsTransferReport* r, void*) { g_reports.push_back(*r); }

// A handler that keeps a slot, moves one stack itself and reads inventories.
uint32_t g_handler_moved = 0, g_handler_read = 0;
KilnSlotId g_handler_keep = 0, g_handler_move = 0;
bool g_handler_cancel = false;
void KILN_CALL native_handler(const KilnItemsTransferEvent* ev, const KilnItemsTransferHost* host, void*) {
    ++g_seen.calls;
    g_seen.kind = ev->kind;
    g_seen.from = ev->from;
    g_seen.to = ev->to;
    g_seen.player = ev->player;
    g_seen.flags = ev->flags;
    KilnItemsSlot slots[32];
    g_handler_read = host->read(ev->ctx, ev->from, slots, 32);
    if (g_handler_keep) host->keep(ev->ctx, g_handler_keep);
    if (g_handler_move) g_handler_moved = host->move(ev->ctx, g_handler_move, ev->to, 0);
    if (g_handler_cancel) host->cancel(ev->ctx);
}
void KILN_CALL faulting_handler(const KilnItemsTransferEvent*, const KilnItemsTransferHost*, void*) {
    *static_cast<volatile int*>(nullptr) = 1;
}

Result12* call_qs(uint32_t to, uint32_t from, FakeTx& tx, Result12* out) { return E.quick_stack(out, &tx, to, from, nullptr); }
Result12* call_ma(uint32_t from, uint32_t to, FakeTx& tx, Result12* out) { return E.move_all(out, &tx, from, to, 1, 1); }

void fill_for_transfer() {
    reset_world();
    put(A, 0, WOOD, 9);
    put(A, 1, IRON, 5);
    put(A, 2, WOOD, 4);
    put(A2, 0, SWORD, 1, 77);
    put(B, 0, WOOD, 100);
    put(B, 1, IRON, 1);
}

void hook_tests() {
    FakeTx tx;
    Result12 out[3] = {};
    // nothing registered: untouched, and nothing hooked (the original bytes are still there)
    const uint8_t first = *reinterpret_cast<const uint8_t*>(E.quick_stack);
    fill_for_transfer();
    g_vanilla_calls = 0;
    call_qs(B, A, tx, out);
    KilnItemsStatus2 st{};
    st.size = sizeof st;
    xfer_status(&st);
    check(out[0].value == 18 && g_vanilla_calls == 1 && !st.hooks_installed && *reinterpret_cast<const uint8_t*>(E.quick_stack) == first,
          "nothing registered: the game's function is untouched and not hooked");

    // a rule installs the hooks; keep a slot
    fill_for_transfer();
    uint64_t keep_rule = 0, deny_rule = 0, reporter = 0, handler = 0, fault = 0;
    KilnItemsTransferRule kr{sizeof kr, KILN_TRANSFER_KEEP_SLOT, 0, 0, 0, 0, KILN_SLOT_ID(A, 2)};
    check(xfer_add_rule(7, &kr, &keep_rule) == KILN_OK, "the first rule installs the hooks");
    xfer_status(&st);
    check(st.hooks_installed && st.rules == 1, "the hooks are installed and one rule is active");
    check(xfer_add_reporter(7, &on_report, nullptr, &reporter) == KILN_OK, "a report listener registers");
    g_vanilla_calls = 0;
    call_qs(B, A, tx, out);
    check(out[0].error == 0 && out[0].value == 14 && count_at(A, 2) == 4 && item_at(A, 2) == WOOD && count_at(A, 0) == 0 && total(WOOD, {B, B2}) == 109,
          "keep: the locked wood stack (4) stayed in the source, the 9 beside it moved; the game's own function ran once");
    check(g_vanilla_calls == 1, "the game's function ran exactly once");
    xfer_deliver();
    check(g_reports.size() == 1 && g_reports[0].kept == 1 && g_reports[0].moved == 14 && g_reports[0].kind == KILN_TRANSFER_QUICK_STACK && g_reports[0].from == A && g_reports[0].to == B &&
              !(g_reports[0].flags & KILN_TRANSFER_CANCELED),
          "report: one record on the tick thread, from, to, kept and moved");
    g_reports.clear();

    // deny cancels
    fill_for_transfer();
    KilnItemsTransferRule dr{sizeof dr, KILN_TRANSFER_DENY, KILN_TRANSFER_QUICK_STACK, B, 0, 0, 0};
    check(xfer_add_rule(7, &dr, &deny_rule) == KILN_OK, "a deny rule registers");
    g_vanilla_calls = 0;
    out[0].value = 99;
    call_qs(B, A, tx, out);
    check(out[0].error == 0 && out[0].value == 0 && g_vanilla_calls == 0 && count_at(A, 0) == 9, "cancel: the game's function does not run, the caller sees ok and 0 moved");
    xfer_deliver();
    check(g_reports.size() == 1 && (g_reports[0].flags & KILN_TRANSFER_CANCELED), "the report says canceled");
    g_reports.clear();
    call_ma(A, B, tx, out);
    check(g_vanilla_calls == 1 && out[0].value > 0, "the rule names quick stack: move all runs");
    xfer_deliver();
    g_reports.clear();
    check(xfer_end(deny_rule) == KILN_OK && xfer_end(deny_rule) == KILN_ERR_NOT_FOUND, "a rule ends once");

    // a silent kiln.items move sees no rules; a marked one is BY_MOD
    fill_for_transfer();
    check(xfer_end(keep_rule) == KILN_OK, "end the keep rule");
    KilnItemsTransferRule dr2{sizeof dr2, KILN_TRANSFER_DENY, 0, B, 0, 0, 0};
    check(xfer_add_rule(7, &dr2, &deny_rule) == KILN_OK, "deny both kinds");
    g_vanilla_calls = 0;
    xfer_mod_begin(true);
    call_qs(B, A, tx, out);
    const bool silent_canceled = xfer_mod_end();
    check(g_vanilla_calls == 1 && !silent_canceled && out[0].value > 0, "silent: neither rules nor reports see it");
    fill_for_transfer();
    g_vanilla_calls = 0;
    xfer_mod_begin(false);
    call_qs(B, A, tx, out);
    const bool by_mod_canceled = xfer_mod_end();
    xfer_deliver();
    std::printf("INFO by-mod cancel: vanilla %d canceled %d reports %zu flags %x\n", g_vanilla_calls, by_mod_canceled, g_reports.size(), g_reports.empty() ? 0 : g_reports[0].flags);
    check(g_vanilla_calls == 0 && by_mod_canceled && g_reports.size() == 1 && (g_reports[0].flags & KILN_TRANSFER_BY_MOD), "a kiln.items move that a rule cancels is told so, and reported BY_MOD");
    g_reports.clear();
    xfer_end(deny_rule);

    // a native handler: reads, keeps, moves a stack itself first, and the count adds up
    fill_for_transfer();
    check(xfer_add_handler(9, &native_handler, nullptr, &handler) == KILN_OK, "a handler registers");
    g_handler_keep = KILN_SLOT_ID(A, 1);
    g_handler_move = KILN_SLOT_ID(A, 2);
    g_handler_moved = 0;
    g_seen = {};
    g_vanilla_calls = 0;
    call_qs(B, A, tx, out);
    xfer_deliver();
    check(g_seen.calls == 1 && g_seen.kind == KILN_TRANSFER_QUICK_STACK && g_seen.from == A && g_seen.to == B && g_seen.player == 0 && g_handler_read == 16,
          "the handler saw the event: kind, from, to, no player, 16 slots read");
    check(g_handler_moved == 4 && count_at(A, 1) == 5 && item_at(A, 1) == IRON && count_at(A, 2) == 0 && total(WOOD, {B, B2}) == 100 + 4 + 9,
          "the handler's own move (4 wood) ran first and the kept iron stack stayed");
    check(out[0].value == 4 + 9 && g_reports.size() == 1 && g_reports[0].moved == 13 && g_reports[0].kept == 1, "the game's count and the report include what the handler moved");
    g_reports.clear();
    // a handler cancels: only its own moves count
    fill_for_transfer();
    g_handler_keep = 0;
    g_handler_cancel = true;
    g_vanilla_calls = 0;
    call_qs(B, A, tx, out);
    check(g_vanilla_calls == 0 && out[0].value == 4 && count_at(A, 0) == 9, "a handler's cancel skips the game's function; its own move still counts");
    g_handler_cancel = false;
    g_handler_move = 0;
    // a handler that faults is skipped; the transfer goes on
    fill_for_transfer();
    check(xfer_end(handler) == KILN_OK && xfer_add_handler(9, &faulting_handler, nullptr, &fault) == KILN_OK, "a faulting handler registers");
    g_vanilla_calls = 0;
    call_qs(B, A, tx, out);
    check(g_vanilla_calls == 1 && out[0].value == 18, "a handler that faults is isolated: the game's function still runs");
    xfer_end_owner(9);
    xfer_status(&st);
    check(st.handlers == 0 && st.rules == 0 && st.reporters == 1, "end_owner(9) ends the handlers only: owner 7's report listener stays");
    // the registry is empty again: the hooks stay but pass through
    xfer_end_owner(7);
    xfer_status(&st);
    fill_for_transfer();
    g_vanilla_calls = 0;
    call_qs(B, A, tx, out);
    check(st.rules == 0 && st.reporters == 0 && st.hooks_installed && g_vanilla_calls == 1 && out[0].value == 18, "with nothing registered the installed hook passes straight through");
    // cost of the pass-through
    LARGE_INTEGER f, t0, t1;
    QueryPerformanceFrequency(&f);
    fill_for_transfer();
    QueryPerformanceCounter(&t0);
    for (int i = 0; i < 200000; ++i) {
        out[0].value = 0;
        E.quick_stack(out, &tx, B2 + 100, A2 + 100, nullptr);  // inventories nobody has: the fake does nothing
    }
    QueryPerformanceCounter(&t1);
    const double ns = (t1.QuadPart - t0.QuadPart) * 1e9 / f.QuadPart / 200000;
    std::printf("INFO a hooked quick stack with nothing registered: %.0f ns per call (fake work included)\n", ns);
    check(ns < 2000, "the pass-through costs well under 2 microseconds per call");
}

Result12* no_transfer(void* p) { return static_cast<Result12*>(p); }

bool hidden_call_survives(Call* call, Result12* result) {
    __try {
        call_hidden(call, &no_transfer, result);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void hide_fault_test() {
    reset_world();
    put(A, 0, WOOD, 9);
    put(A2, 0, IRON, 4);
    FakeTx tx;
    Call call;
    check(access_from_tx(&tx, &call.a), "hide fault: the transaction accessors check out");
    check(read_pages(call.a, A, &call.from_pages, nullptr), "hide fault: the source inventory membership is known");
    keep_slot(&call.verdict, KILN_SLOT_ID(A, 0));
    keep_slot(&call.verdict, KILN_SLOT_ID(A2, 0));
    g_fault_inventory_once = A2;
    Result12 result{};
    call_hidden(&call, &no_transfer, &result);
    check(count_at(A, 0) == 9 && count_at(A2, 0) == 4,
          "hide fault: a fault on the second stack still restores the first stack's count");
    auto* readonly = VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    check(readonly != nullptr, "hide fault: the read-only component fixture allocates");
    if (!readonly) return;
    std::memcpy(readonly, comp(T_INV, A2, 96).data(), 96);
    DWORD old = 0;
    VirtualProtect(readonly, 4096, PAGE_READONLY, &old);
    g_readonly_inventory = readonly;
    const bool survived = hidden_call_survives(&call, &result);
    g_readonly_inventory = nullptr;
    VirtualFree(readonly, 0, MEM_RELEASE);
    check(survived && count_at(A, 0) == 9 && count_at(A2, 0) == 4,
          "hide fault: a failed write does not try to restore an unchanged read-only stack or lose earlier counts");
}

int hook_factory_calls = 0;
bool fail_second_hook(kiln::RawHook& hook, uintptr_t target, void* replacement, const char* label) {
    return ++hook_factory_calls == 2 ? false : make_hook(hook, target, replacement, label);
}

void partial_install_test() {
    check(!install_locked(&fail_second_hook), "partial install: a failed second hook leaves the feature unavailable");
    check(static_cast<bool>(h_qs) && !static_cast<bool>(h_ma), "partial install: the quick-stack hook keeps its link, move all has none");
    // Do not call through a missing continuation on a broken build: the assertion above exposes the actual bug.
    if (h_qs) {
        fill_for_transfer();
        FakeTx tx;
        Result12 out[3] = {};
        g_vanilla_calls = 0;
        call_qs(B, A, tx, out);
        check(g_vanilla_calls == 1 && out[0].value == 18,
              "partial install: the installed hook passes through safely with no registrations");
    }
}

// the hooks are placed through kiln.hooks (the stand-in here): one link per function, labelled with its sigdb name
void link_test() {
    check(hooks_shim::calls == 0, "kiln.hooks links: nothing is hooked before the first registration");
    KilnItemsTransferRule kr{sizeof kr, KILN_TRANSFER_KEEP_SLOT, 0, 0, 0, 0, KILN_SLOT_ID(A, 2)};
    uint64_t rule = 0;
    check(xfer_add_rule(7, &kr, &rule) == KILN_OK && hooks_shim::calls == 2 && hooks_shim::links == 2 &&
              static_cast<bool>(h_qs) && static_cast<bool>(h_ma),
          "kiln.hooks links: the first registration places two raw links (quick stack, move all)");
    uint64_t rule2 = 0;
    check(xfer_add_rule(7, &kr, &rule2) == KILN_OK && hooks_shim::calls == 2,
          "kiln.hooks links: a second registration places no further link");
    xfer_end(rule);
    xfer_end(rule2);
}

// ---------------------------------------------------------------------------------------------- review regressions ----
struct EndingCallback {
    uint64_t handle = 0;
    uint64_t target = 0;
    KilnOwner owner = 0;
    uint32_t calls = 0;
};
void KILN_CALL ending_reporter(const KilnItemsTransferReport*, void* user) {
    auto* p = static_cast<EndingCallback*>(user);
    ++p->calls;
    if (p->owner) xfer_end_owner(p->owner);
    else xfer_end(p->target ? p->target : p->handle);
}
void KILN_CALL ending_handler(const KilnItemsTransferEvent*, const KilnItemsTransferHost*, void* user) {
    auto* p = static_cast<EndingCallback*>(user);
    ++p->calls;
    if (p->owner) xfer_end_owner(p->owner);
    else xfer_end(p->target);
}
void KILN_CALL counting_handler(const KilnItemsTransferEvent*, const KilnItemsTransferHost*, void* user) {
    ++*static_cast<uint32_t*>(user);
}

struct BlockingCallback { HANDLE entered, release; uint64_t end_self = 0; };
void KILN_CALL blocking_handler(const KilnItemsTransferEvent*, const KilnItemsTransferHost*, void* user) {
    auto* p = static_cast<BlockingCallback*>(user);
    if (p->end_self) xfer_end(p->end_self);
    SetEvent(p->entered);
    WaitForSingleObject(p->release, 5000);
}

std::map<std::pair<uint32_t, uint32_t>, std::vector<uint8_t>> g_tx_before;
void* __fastcall fake_tx_ctor(void* p) {
    new (p) FakeTx();
    g_tx_before = g_comps;
    return p;
}
void __fastcall fake_tx_init(void*, const void*) { }
void __fastcall fake_tx_noop(void*) { }
void __fastcall fake_tx_rollback(void*) { g_comps = g_tx_before; }

// v4: a move's ledger and a handler's read carry the item entity's stamp; it stays with the item entity through the move,
// and another item entity gets another one. Off (items.instances off): 0 everywhere, as an older service.
void stamps_mode_test() {
    E.tx_ctor = &fake_tx_ctor;
    E.tx_init = &fake_tx_init;
    E.commit = &fake_tx_noop;
    E.rollback = &fake_tx_rollback;
    E.free_arrays = &fake_tx_noop;
    FakeTx tx;
    Access access;
    check(access_from_tx(&tx, &access), "stamps: transaction accessors check out");
    auto move_gear = [&](uint32_t from, uint64_t slot, uint32_t to, uint32_t flags) {
        MoveJob job;
        job.r.size = sizeof(KilnItemsMoveRequest);
        job.r.kind = KILN_MOVE_STACK;
        job.r.from = from;
        job.r.to = to;
        job.r.slot = slot;
        job.r.flags = flags;
        KilnItemsMoveResult res{};
        unsigned long code = 0;
        run_move(access, job, reinterpret_cast<uint8_t*>(&tx), &res, &code);
        return res;
    };
    E.instances_ok = false;
    reset_world();
    put(A2, 1, SWORD, 1, 3077);
    KilnItemsMoveResult off = move_gear(A, KILN_SLOT_ID(A2, 1), B, 0);
    check(off.status == KILN_ITEMS_DONE && off.change_count == 1 && off.changes[0].pide == 3077 && off.changes[0].reserved == 0,
          "stamps off: a move's ledger carries the item entity and stamp 0 (as an older service)");
    E.instances_ok = true;
    stamps().reset();
    reset_world();
    put(A2, 1, SWORD, 1, 3077);
    put(A2, 2, SWORD, 1, 3078);
    put(A, 0, WOOD, 9);
    KilnItemsMoveResult r1 = move_gear(A, KILN_SLOT_ID(A2, 1), B, 0);
    const uint32_t s1 = r1.change_count == 1 ? r1.changes[0].reserved : 0;
    check(r1.status == KILN_ITEMS_DONE && r1.changes[0].pide == 3077 && s1 != 0 && s1 == stamps().peek(3077),
          "stamps on: the ledger carries the item entity's stamp, the one the table holds");
    KilnItemsSlot slots[64];
    const uint32_t n = read_slots(access, B, slots, 64);
    uint32_t seen = 0;
    for (uint32_t i = 0; i < n; ++i) if (slots[i].pide == 3077) seen = slots[i].reserved;
    check(seen == s1, "a read of the target (a transfer handler's read) gives the same stamp for the same item entity");
    uint64_t back_slot = 0;
    for (uint32_t i = 0; i < n; ++i) if (slots[i].pide == 3077) back_slot = slots[i].slot;
    KilnItemsMoveResult r2 = move_gear(B, back_slot, A, 0);
    check(r2.status == KILN_ITEMS_DONE && r2.changes[0].pide == 3077 && r2.changes[0].reserved == s1,
          "moved back: still the same item entity, still the same stamp");
    KilnItemsMoveResult r3 = move_gear(A, KILN_SLOT_ID(A2, 2), B, KILN_ITEMS_MOVE_DRY_RUN);
    check(r3.changes[0].pide == 3078 && r3.changes[0].reserved != 0 && r3.changes[0].reserved != s1,
          "another item entity has another stamp (a dry run names it too)");
    KilnItemsMoveResult r4 = move_gear(A, KILN_SLOT_ID(A, 0), B, 0);
    check(r4.status == KILN_ITEMS_DONE && r4.changes[0].pide == 0 && r4.changes[0].reserved == 0,
          "a plain stack has no item entity and no stamp");
    stamps().reset();  // the world closed
    KilnItemsSlot after[64];
    const uint32_t m = read_slots(access, A, after, 64);
    uint32_t again = 0;
    for (uint32_t i = 0; i < m; ++i) if (after[i].pide == 3077) again = after[i].reserved;
    check(again != 0 && again != s1, "after a world reset the same id gets a new stamp, never the old one");
    E.instances_ok = false;
}

void review_tests() {
    FakeTx tx;
    Result12 result[3]{};
    uint64_t handle = 0;
    reset_world();
    put(A, 0, WOOD, 9);
    put(B, 0, IRON, 5);
    g_handler_keep = KILN_SLOT_ID(B, 0);
    g_handler_move = 0;
    check(xfer_add_handler(90, &native_handler, nullptr, &handle) == KILN_OK, "review: destination-keep handler registers");
    call_ma(A, B, tx, result);
    check(total(WOOD, {A, A2, B, B2}) == 9 && total(IRON, {A, A2, B, B2}) == 5 &&
          item_at(B, 0) == IRON && count_at(B, 0) == 5,
          "review: keeping destination B:0 is ignored; different source/destination items are conserved");
    xfer_end(handle);
    g_handler_keep = 0;

    EndingCallback self;
    check(xfer_add_reporter(91, &ending_reporter, &self, &self.handle) == KILN_OK, "review: self-ending reporter registers");
    fill_for_transfer(); call_qs(B, A, tx, result);
    fill_for_transfer(); call_qs(B, A, tx, result);
    xfer_deliver();
    check(self.calls == 1, "review: a reporter ending itself receives no later report from the same delivery batch");

    for (bool by_owner : {false, true}) {
        EndingCallback first;
        uint32_t second_calls = 0;
        uint64_t second = 0;
        check(xfer_add_handler(92, &ending_handler, &first, &first.handle) == KILN_OK &&
              xfer_add_handler(93, &counting_handler, &second_calls, &second) == KILN_OK,
              "review: ordered cancellation handlers register");
        first.target = second;
        first.owner = by_owner ? 93 : 0;
        fill_for_transfer(); call_qs(B, A, tx, result);
        check(first.calls == 1 && second_calls == 0, by_owner
            ? "review: owner invalidation skips a later handler already captured by a registry snapshot"
            : "review: handler A ending B skips B already captured by a registry snapshot");
        xfer_end_owner(92); xfer_end_owner(93);
    }

    for (int mode : {0, 1, 2}) {
        BlockingCallback block{CreateEventW(nullptr, TRUE, FALSE, nullptr), CreateEventW(nullptr, TRUE, FALSE, nullptr)};
        HANDLE ended = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        HANDLE cancel_started = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        check(xfer_add_handler(94, &blocking_handler, &block, &handle) == KILN_OK, "review: blocking handler registers");
        if (mode == 2) block.end_self = handle;
        fill_for_transfer();
        std::thread invoke([&] { call_qs(B, A, tx, result); });
        check(WaitForSingleObject(block.entered, 2000) == WAIT_OBJECT_0, "review: the callback entered before cancellation");
        std::thread cancel([&] { SetEvent(cancel_started); if (mode) xfer_end_owner(94); else xfer_end(handle); SetEvent(ended); });
        check(WaitForSingleObject(cancel_started, 2000) == WAIT_OBJECT_0, "review: cancellation starts while the callback is blocked");
        const bool drained = WaitForSingleObject(ended, 50) == WAIT_TIMEOUT;
        SetEvent(block.release);
        invoke.join(); cancel.join();
        check(drained && WaitForSingleObject(ended, 2000) == WAIT_OBJECT_0, mode == 0
            ? "review: external transfer_end drains an already-running callback before returning"
            : mode == 1 ? "review: external end_owner drains an already-running callback before returning"
            : "review: external end_owner also drains a callback already invalidated by reentrant self-cancellation");
        CloseHandle(block.entered); CloseHandle(block.release); CloseHandle(ended); CloseHandle(cancel_started);
    }

    check(xfer_add_reporter(95, &on_report, nullptr, &handle) == KILN_OK, "review: dry-run reporter registers");
    uint64_t dry_handler = 0;
    g_handler_keep = g_handler_move = 0;
    g_handler_cancel = false;
    check(xfer_add_handler(96, &native_handler, nullptr, &dry_handler) == KILN_OK, "review: dry-run handler registers");
    E.tx_ctor = &fake_tx_ctor;
    E.tx_init = &fake_tx_init;
    E.commit = &fake_tx_noop;
    E.rollback = &fake_tx_rollback;
    E.free_arrays = &fake_tx_noop;
    for (uint32_t kind : {KILN_MOVE_QUICK_STACK, KILN_MOVE_ALL}) {
        g_reports.clear();
        reset_world(); put(A, 0, WOOD, 9); put(B, 0, WOOD, 2);
        Access access;
        check(access_from_tx(&tx, &access), "review: dry-run transaction accessors check out");
        MoveJob job;
        job.r.size = sizeof(KilnItemsMoveRequest);
        job.r.kind = kind;
        job.r.from = A;
        job.r.to = B;
        job.r.flags = KILN_ITEMS_MOVE_DRY_RUN;
        KilnItemsMoveResult moved{};
        unsigned long code = 0;
        g_seen = SeenEvent{};
        bool ok = run_move(access, job, reinterpret_cast<uint8_t*>(&tx), &moved, &code);
        xfer_deliver();
        check(g_seen.calls == 1 && (g_seen.flags & KILN_TRANSFER_DRY_RUN) && (g_seen.flags & KILN_TRANSFER_BY_MOD),
              kind == KILN_MOVE_QUICK_STACK ? "review: a dry-run quick stack tells handlers (KILN_TRANSFER_DRY_RUN)"
                                            : "review: a dry-run move all tells handlers (KILN_TRANSFER_DRY_RUN)");
        g_seen = SeenEvent{};
        reset_world(); put(A, 0, WOOD, 9); put(B, 0, WOOD, 2);
        job.r.flags = 0;
        KilnItemsMoveResult real{};
        run_move(access, job, reinterpret_cast<uint8_t*>(&tx), &real, &code);
        xfer_deliver();
        check(g_seen.calls == 1 && !(g_seen.flags & KILN_TRANSFER_DRY_RUN) && (g_seen.flags & KILN_TRANSFER_BY_MOD),
              "review: a real mod move has no dry-run flag");
        g_reports.clear();
        reset_world(); put(A, 0, WOOD, 9); put(B, 0, WOOD, 2);
        job.r.flags = KILN_ITEMS_MOVE_DRY_RUN;
        ok = run_move(access, job, reinterpret_cast<uint8_t*>(&tx), &moved, &code);
        xfer_deliver();
        check(ok && moved.done == 9 && total(WOOD, {A, A2}) == 9 && total(WOOD, {B, B2}) == 2,
              "review: dry-run uses the real move path and rolls its inventory writes back");
        check(g_reports.empty(), kind == KILN_MOVE_QUICK_STACK
            ? "review: dry-run quick stack emits no committed transfer report"
            : "review: dry-run move all emits no committed transfer report");
    }
    xfer_end_owner(95);
    xfer_end_owner(96);
}

// items.move off, items.transfer on (dev.20 review): the transfer hooks still work, mods' moves are refused cleanly.
void move_off_test() {
    E.move_ok = false;
    E.transfer_ok = false;
    gate_features();
    check(!E.slot_transfer && !E.quick_stack && !E.move_all, "move off, transfer off: no game function is left to call");
    setup_engine();
    E.move_ok = false;
    E.transfer_ok = true;
    gate_features();
    check(!E.slot_transfer && E.quick_stack && E.move_all,
          "move off, transfer on: the slot transfer is dropped, quick stack and move all stay for the hooks");
    FakeTx tx;
    Access a;
    check(access_from_tx(&tx, &a), "move off: the transaction accessors check out");
    fill_for_transfer();
    MoveOutcome mo;
    check(move_stack_in(a, &tx, A, KILN_SLOT_ID(A, 0), B, 0, 0, false, false, &mo) == 0 && mo.status == KILN_ITEMS_UNAVAILABLE &&
              total(WOOD, {A, A2}) == 13,
          "move off: a mod's stack move is refused as unavailable and nothing moves");
    MoveJob job;
    job.r.size = sizeof(KilnItemsMoveRequest);
    job.r.kind = KILN_MOVE_QUICK_STACK;
    job.r.from = A;
    job.r.to = B;
    KilnItemsMoveResult res{};
    unsigned long code = 0;
    run_move(a, job, reinterpret_cast<uint8_t*>(&tx), &res, &code);
    check(res.status == KILN_ITEMS_UNAVAILABLE, "move off: a mod's quick stack is refused as unavailable");
    uint64_t keep_rule = 0;
    KilnItemsTransferRule kr{sizeof kr, KILN_TRANSFER_KEEP_SLOT, 0, 0, 0, 0, KILN_SLOT_ID(A, 2)};
    check(xfer_add_rule(7, &kr, &keep_rule) == KILN_OK, "move off: a transfer rule still installs the hooks");
    fill_for_transfer();
    Result12 out[3] = {};
    g_vanilla_calls = 0;
    call_qs(B, A, tx, out);
    check(out[0].error == 0 && out[0].value == 14 && count_at(A, 2) == 4 && g_vanilla_calls == 1,
          "move off: the game's quick stack runs through the hook and the keep rule holds the locked stack");
    xfer_end(keep_rule);
}

// ---------------------------------------------------------------------------------------------- chaining ----
SafetyHookInline* other_qs = nullptr;
Result12* __fastcall other_qs_detour(Result12* out, void* tx, uint32_t to, uint32_t from, void* fb) {
    g_order.push_back("other");
    return other_qs->call<Result12*>(out, tx, to, from, fb);
}

void chain_test(bool other_first) {
    FakeTx tx;
    Result12 out[3] = {};
    fill_for_transfer();
    if (other_first) other_qs = new SafetyHookInline(safetyhook::create_inline(reinterpret_cast<void*>(E.quick_stack), reinterpret_cast<void*>(&other_qs_detour)));
    check(!other_first || static_cast<bool>(*other_qs), "(another loader's hook is on quick stack first)");
    KilnItemsTransferRule kr{sizeof kr, KILN_TRANSFER_KEEP_SLOT, 0, 0, 0, 0, KILN_SLOT_ID(A, 2)};
    uint64_t rule = 0;
    check(xfer_add_rule(7, &kr, &rule) == KILN_OK, "ours installs");
    if (!other_first) other_qs = new SafetyHookInline(safetyhook::create_inline(reinterpret_cast<void*>(E.quick_stack), reinterpret_cast<void*>(&other_qs_detour)));
    check(static_cast<bool>(*other_qs), other_first ? "(ours sits on top of the other hook)" : "(the other loader's hook is installed after ours)");
    g_order.clear();
    g_vanilla_calls = 0;
    call_qs(B, A, tx, out);
    std::string order;
    for (auto& s : g_order) order += s + " ";
    std::printf("INFO call order: %s\n", order.c_str());
    check(g_vanilla_calls == 1, "the game's function runs exactly once with both hooks on it");
    check(g_order.size() == 2 && g_order[0] == "other" && g_order[1] == "orig", "the other loader's hook runs once, then the original");
    check(count_at(A, 2) == 4 && out[0].value == 14, "our rule still applied (the kept stack stayed, the rest moved)");
    // ours stays installed whatever the other does: it is never uninstalled by us
    xfer_end(rule);
    fill_for_transfer();
    g_order.clear();
    call_qs(B, A, tx, out);
    check(g_order.size() == 2 && out[0].value == 18, "with our rule ended, both hooks still chain and pass through");
}

}  // namespace

// v7 (the gem wave): KILN_ITEMS_REPLACE, a give that first takes one item entity out of its slot on the same
// transaction (Gems.AddSocket re-creates gear this way). Fake addItems / removeItems on the fake world record the
// game's commit ops (op 0 put, op 1 take) where the engine reads them.
std::vector<uint8_t> g_ops;  // kOpStride records
struct OpList { const uint8_t* p; uint64_t n; };
OpList* g_oplist = nullptr;
uint32_t g_next_pide = 900;
bool g_add_fails = false;
void push_op(uint8_t kind, uint64_t slot, uint32_t item, uint16_t amount, uint32_t pide) {
    const size_t at = g_ops.size();
    g_ops.resize(at + kOpStride, 0);
    uint8_t* op = g_ops.data() + at;
    std::memcpy(op, &slot, 8);
    std::memcpy(op + 8, &item, 4);
    std::memcpy(op + 0x20, &pide, 4);
    std::memcpy(op + 0x24, &amount, 2);
    op[0x26] = kind;
    op[0x30] = pide ? 1 : 0;
    g_oplist->p = g_ops.data();
    g_oplist->n = g_ops.size() / kOpStride;
}
uint32_t links_of(uint32_t owner, uint32_t* out) {
    auto it = g_comps.find({T_SETUP, owner});
    uint32_t n = 0;
    for (int k = 0; it != g_comps.end() && k < 16; ++k) {
        uint32_t l;
        std::memcpy(&l, it->second.data() + 4 * k, 4);
        if (l) out[n++] = l;
    }
    return n;
}
Result12* __fastcall fake_remove(Result12* out, void*, uint32_t owner, uint32_t item, uint64_t slot, uint32_t count) {
    *out = {};
    uint32_t links[16];
    const uint32_t n = links_of(owner, links);
    auto take_from = [&](uint32_t inv, uint32_t k) {
        if (!count || item_at(inv, k) != item) return;
        const uint32_t have = count_at(inv, k), pide = pide_at(inv, k), t = have < count ? have : count;
        push_op(1, KILN_SLOT_ID(inv, k), item, static_cast<uint16_t>(t), pide);
        put(inv, k, have - t ? item : 0, have - t, have - t ? pide : 0);
        count -= t;
    };
    if (slot) take_from(KILN_SLOT_INVENTORY(slot), KILN_SLOT_INDEX(slot));
    for (uint32_t i = 0; i < n; ++i)
        for (uint32_t k = 0; k < 8; ++k) take_from(links[i], k);
    out->value = count;
    return out;
}
Result12* __fastcall fake_add(Result12* out, void*, uint32_t owner, uint64_t slot, uint32_t item, uint32_t count, OptU32*,
                              OptU8*, OptF32*) {
    *out = {};
    if (g_add_fails) {
        out->error = 0x27;
        return out;
    }
    uint32_t links[16];
    const uint32_t n = links_of(owner, links);
    auto put_into = [&](uint32_t inv, uint32_t k) {
        if (!count || item_at(inv, k) != 0) return;
        push_op(0, KILN_SLOT_ID(inv, k), item, 1, 0);
        put(inv, k, item, 1, g_next_pide++);  // the item entity the commit creates (the engine reads it after)
        --count;
    };
    if (slot) put_into(KILN_SLOT_INVENTORY(slot), KILN_SLOT_INDEX(slot));
    for (uint32_t i = 0; i < n; ++i)
        for (uint32_t k = 0; k < 8; ++k) put_into(links[i], k);
    out->value = count;
    return out;
}

void replace_test() {
    E.tx_ctor = &fake_tx_ctor;
    E.tx_init = &fake_tx_init;
    E.commit = &fake_tx_noop;
    E.rollback = &fake_tx_rollback;
    E.free_arrays = &fake_tx_noop;
    E.remove_items = &fake_remove;
    E.add_items = &fake_add;
    E.tx_ops = 0x100;  // FakeTx.words[0x20..0x21]
    FakeTx tx;
    Access a;
    check(access_from_tx(&tx, &a), "replace: transaction accessors check out");
    auto run = [&](uint32_t item, uint64_t slot, uint32_t pide, uint32_t flags) {
        g_ops.clear();
        new (&tx) FakeTx();
        g_oplist = reinterpret_cast<OpList*>(reinterpret_cast<uint8_t*>(&tx) + 0x100);
        *g_oplist = {};
        Job job;
        job.r.size = sizeof(KilnItemsRequest);
        job.r.flags = KILN_ITEMS_REPLACE | flags;
        job.r.entity = A;
        job.r.item = item;
        job.r.count = 1;
        job.r.slot = slot;
        job.r.pide = pide;
        KilnItemsResult res{};
        unsigned long code = 0;
        check(run_job(a, job, reinterpret_cast<uint8_t*>(&tx), &res, &code), "replace: the job runs without a fault");
        return res;
    };
    // every item entity the game makes has an ItemState (createPide); a re-creation reads it (the review's guard below)
    auto item_state = [](uint32_t pide, uint16_t bits) {
        auto& is = comp(T_ITEMSTATE, pide, kItemStateSize);
        std::memcpy(is.data() + 0x0E, &bits, 2);
    };
    put(A, 2, SWORD, 1, 77);
    item_state(77, 1u << 4);
    put(A, 0, WOOD, 9);
    auto r = run(SWORD, KILN_SLOT_ID(A, 2), 77, KILN_ITEMS_GEM_SLOT | KILN_ITEMS_GEM_SLOT_ANY);
    check(r.status == KILN_ITEMS_DONE && r.done == 1 && item_at(A, 2) == SWORD && pide_at(A, 2) == 900 &&
              r.change_count == 1 && r.changes[0].slot == KILN_SLOT_ID(A, 2) && r.changes[0].pide == 900 &&
              g_ops.size() == 2 * kOpStride && g_ops[0x26] == 1 && g_ops[kOpStride + 0x26] == 0,
          "replace: one transaction takes item entity 77 out of its slot, then creates the new one in the same slot");
    r = run(SWORD, KILN_SLOT_ID(A, 2), 77, 0);
    check(r.status == KILN_ITEMS_SLOT_CHANGED && pide_at(A, 2) == 900 && g_ops.empty(),
          "replace: a slot that no longer holds that item entity is refused before the transaction");
    r = run(WOOD, KILN_SLOT_ID(A, 0), 77, 0);
    check(r.status == KILN_ITEMS_REFUSED && count_at(A, 0) == 9, "replace: an item without an item entity is refused");
    put(A, 4, SWORD, 1, 55);
    item_state(55, 1u << 4);
    g_add_fails = true;
    r = run(SWORD, KILN_SLOT_ID(A, 4), 55, 0);
    g_add_fails = false;
    check(r.status == KILN_ITEMS_REFUSED && item_at(A, 4) == SWORD && pide_at(A, 4) == 55,
          "replace: when the game refuses the new item, the take is rolled back with it (the old item stays)");
    for (uint32_t k = 0; k < 8; ++k) if (k != 4) put(A, k, IRON, 1);
    for (uint32_t k = 0; k < 8; ++k) put(A2, k, IRON, 1);
    r = run(SWORD, KILN_SLOT_ID(A, 4), 55, KILN_ITEMS_DRY_RUN);
    check(r.status == KILN_ITEMS_DONE && item_at(A, 4) == SWORD && pide_at(A, 4) == 55,
          "replace: a dry run in a full inventory says it would work (the freed slot) and changes nothing");

    // Review (gem wave G4): a re-creation destroys the old item entity, so it never touches gear that is worn or held
    // (the player may have equipped or picked it up after Gems.AddSocket read it) nor gear with a gem slot (a gem in it
    // would be destroyed with its carrier). Checked on the job's step, before the transaction.
    reset_world();
    auto run_on = [&](uint32_t entity, uint64_t slot, uint32_t pide) {
        g_ops.clear();
        new (&tx) FakeTx();
        g_oplist = reinterpret_cast<OpList*>(reinterpret_cast<uint8_t*>(&tx) + 0x100);
        *g_oplist = {};
        Job job;
        job.r.size = sizeof(KilnItemsRequest);
        job.r.flags = KILN_ITEMS_REPLACE | KILN_ITEMS_GEM_SLOT | KILN_ITEMS_GEM_SLOT_ANY;
        job.r.entity = entity;
        job.r.item = SWORD;
        job.r.count = 1;
        job.r.slot = slot;
        job.r.pide = pide;
        KilnItemsResult res{};
        unsigned long code = 0;
        check(run_job(a, job, reinterpret_cast<uint8_t*>(&tx), &res, &code), "replace guard: the job runs without a fault");
        return res;
    };
    put(P, 7, SWORD, 1, 61);  // P's SlotSelection names P:7 as equipment[0] (reset_world)
    item_state(61, 1u << 4);  // the guard must not depend on IsEquipped alone
    r = run_on(P, KILN_SLOT_ID(P, 7), 61);
    check(r.status == KILN_ITEMS_REFUSED && item_at(P, 7) == SWORD && pide_at(P, 7) == 61 && g_ops.empty(),
          "replace guard: gear in a player's equipped slot is refused before the transaction (unequip first)");
    put(PBAR, 3, SWORD, 1, 62);
    item_state(62, 1u << 4);
    {
        auto& sel = comp(T_SEL, P, 216);
        const uint32_t bar = PBAR;
        std::memcpy(sel.data() + 0xa8, &bar, 4);  // the item in hand: PBAR:3
        sel[0xac] = 3;
    }
    r = run_on(P, KILN_SLOT_ID(PBAR, 3), 62);
    check(r.status == KILN_ITEMS_REFUSED && item_at(PBAR, 3) == SWORD && pide_at(PBAR, 3) == 62 && g_ops.empty(),
          "replace guard: the item a player holds in hand is refused");
    put(A, 1, SWORD, 1, 63);
    item_state(63, 1u << 3);  // ItemState IsEquipped
    r = run_on(A, KILN_SLOT_ID(A, 1), 63);
    check(r.status == KILN_ITEMS_REFUSED && item_at(A, 1) == SWORD && pide_at(A, 1) == 63 && g_ops.empty(),
          "replace guard: an item whose ItemState says IsEquipped is refused");
    put(A, 3, SWORD, 1, 64);
    item_state(64, 1u << 8);  // ItemState HasWeaponGemSlot
    r = run_on(A, KILN_SLOT_ID(A, 3), 64);
    check(r.status == KILN_ITEMS_REFUSED && item_at(A, 3) == SWORD && pide_at(A, 3) == 64 && g_ops.empty(),
          "replace guard: an item that has a gem slot is refused (its gem would be destroyed with it)");
    put(A, 6, SWORD, 1, 67);  // no ItemState: nothing to check against, so nothing is destroyed
    r = run_on(A, KILN_SLOT_ID(A, 6), 67);
    check(r.status == KILN_ITEMS_REFUSED && item_at(A, 6) == SWORD && pide_at(A, 6) == 67 && g_ops.empty(),
          "replace guard: an item entity whose ItemState cannot be read is refused (fail closed)");
    put(A, 5, SWORD, 1, 65);
    item_state(65, (1u << 4) | (1u << 6));  // HasLevel, IsNew: an ordinary item
    put(PBAR, 4, SWORD, 1, 66);             // on the action bar, not in hand
    item_state(66, 1u << 4);
    r = run_on(A, KILN_SLOT_ID(A, 5), 65);
    const auto bar = run_on(P, KILN_SLOT_ID(PBAR, 4), 66);
    check(r.status == KILN_ITEMS_DONE && pide_at(A, 5) != 65 && bar.status == KILN_ITEMS_DONE && pide_at(PBAR, 4) != 66,
          "replace guard: ordinary gear in a chest or on a player's action bar (not in hand) is still re-created");
}

// ---------------------------------------------------------------------------------------------- issue #109 ----
// Items.Move into a player with no toSlot took the first empty slot of every page but the action bars, and a player's
// InventorySetup links its Equipment pages first: 4 Active Mycelium went into an equipment slot and the move said Done.
// The layout is the owner's client's (2026-11-16, gems_test.cpp backpack_room): Equipment pages first, the root's Generic
// pages (two of them the action bars), a Virtual link to the worn backpack's own setup with its Generic pages, Currency.
constexpr uint32_t RING = 12, MYC = 13, COIN = 14, ARROW = 8, BAG = 9;
constexpr uint32_t Q = 50, QEQ1 = 51, QEQ2 = 52, QEQ3 = 57, QBAR1 = 53, QBAR2 = 54, QG1 = 55, QG2 = 56, QCUR = 58, QBAG = 60,
                   QBP1 = 61, QBP2 = 62, QCUS = 59;
constexpr uint32_t NPC = 70, NEQ = 71, NG = 72;

// SlotSelection.equipment index i: 0-7 on the first Equipment page, 8-15 on the second, 16-19 on the third (Ring0 is
// index 10 = QEQ2:2, Arrow index 14 = QEQ2:6, Backpack index 19 = QEQ3:3); QEQ3:4-7 are slots no equipment slot shows.
uint64_t q_equip(uint32_t i) { return i < 8 ? KILN_SLOT_ID(QEQ1, i) : i < 16 ? KILN_SLOT_ID(QEQ2, i - 8) : KILN_SLOT_ID(QEQ3, i - 16); }

void make_setup_cats(uint32_t entity, std::initializer_list<std::pair<uint32_t, uint8_t>> links, uint8_t flags) {
    auto& s = comp(T_SETUP, entity, kSetupSize);
    std::memset(s.data(), 0, s.size());
    uint32_t i = 0;
    for (auto [l, cat] : links) {
        std::memcpy(s.data() + 4 * i, &l, 4);
        s[0x40 + i] = cat;
        if (cat != 5) comp(T_INV, l, 96);
        ++i;
    }
    s[0x50] = flags;
    s[0x53] = 1;
}

void make_player_q(bool backpack_worn) {
    if (backpack_worn)
        make_setup_cats(Q, {{QEQ1, 2}, {QEQ2, 2}, {QEQ3, 2}, {QBAR1, 4}, {QBAR2, 4}, {QG1, 4}, {QG2, 4}, {QBAG, 5}, {QCUR, 3}, {QCUS, 1}}, 0x01);
    else
        make_setup_cats(Q, {{QEQ1, 2}, {QEQ2, 2}, {QEQ3, 2}, {QBAR1, 4}, {QBAR2, 4}, {QG1, 4}, {QG2, 4}, {QCUR, 3}, {QCUS, 1}}, 0x01);
    make_setup_cats(QBAG, {{QBP1, 4}, {QBP2, 4}}, 0);
    auto& sel = comp(T_SEL, Q, 216);
    std::memset(sel.data(), 0, sel.size());
    for (uint32_t i = 0; i < 20; ++i) {
        const uint64_t s = q_equip(i);
        const uint32_t inv = KILN_SLOT_INVENTORY(s);
        std::memcpy(sel.data() + i * 8, &inv, 4);
        sel[i * 8 + 4] = static_cast<uint8_t>(KILN_SLOT_INDEX(s));
    }
    std::memcpy(sel.data() + 0xa0, &QBAR1, 4);
    std::memcpy(sel.data() + 0xa4, &QBAR2, 4);
}
void fill_page(uint32_t inv, uint32_t item, uint32_t count) {
    for (uint32_t k = 0; k < 8; ++k) put(inv, k, item, count);
}
void set_item(uint32_t id, uint16_t max_stack, bool gear, uint8_t equip_type, uint8_t category) {
    uint8_t* b = g_item_bytes[id % 8];
    std::memset(b, 0, 0x700);
    std::memcpy(b + 0x14, &max_stack, 2);
    const uint32_t flags = gear ? 2u : 0u;
    std::memcpy(b + 0x5d8, &flags, 4);
    b[0x104] = equip_type;
    b[0xCC] = category;
}
bool page_empty(uint32_t inv) {
    for (uint32_t k = 0; k < 8; ++k) if (count_at(inv, k) != 0) return false;
    return true;
}

// The pure rules, on the reported layout.
void issue109_logic() {
    // pages in walk order: Equipment x3, the two action bars, two root Generic, the worn backpack's two, Currency, Customization
    Page pg[11] = {page(QEQ1, {{RING, 1, 900}}), page(QEQ2, {}), page(QEQ3, {}), page(QBAR1, {}), page(QBAR2, {}),
                   page(QG1, {{WOOD, 250, 0}}), page(QG2, {}), page(QBP1, {}), page(QBP2, {{MYC, 3, 0}}), page(QCUR, {}), page(QCUS, {})};
    const uint8_t cats[11] = {2, 2, 2, 4, 4, 4, 4, 4, 4, 3, 1};
    DestRules r;
    r.person = true;
    r.actionbar_pages[0] = QBAR1;
    r.actionbar_pages[1] = QBAR2;
    for (uint32_t i = 0; i < 20; ++i) r.equipment[i] = q_equip(i);
    const uint32_t bars[2] = {QBAR1, QBAR2};
    uint64_t d = 0;
    const char* why = "";
    // the reported bug: the old search over every page but the action bars takes an equipment slot
    check(find_destination(pg, 11, WOOD + 100, 250, false, bars, 2, 0, &d) && d == KILN_SLOT_ID(QEQ1, 1),
          "#109 before the fix: the search over every page puts a material into the first Equipment page (QEQ1:1)");
    // no toSlot: the first empty Generic slot that is not an action bar
    check(choose_destination(pg, cats, 11, WOOD + 100, 250, false, r, 0, &d, &why) == DestKind::Found && d == KILN_SLOT_ID(QG1, 1),
          "#109 no toSlot: a new item goes to the first empty backpack slot (QG1:1), never an equipment slot");
    // stacking: a partial stack in the worn backpack's page wins over an empty root slot
    check(choose_destination(pg, cats, 11, MYC, 99, false, r, 0, &d, &why) == DestKind::Found && d == KILN_SLOT_ID(QBP2, 0),
          "#109 stacking: a partial stack of the item on the worn backpack's page is topped up first");
    // a partial stack on an equipment page (arrows worn) is not part of the search
    Page arrows[11];
    std::memcpy(arrows, pg, sizeof pg);
    arrows[1].s[6] = Stack{KILN_SLOT_ID(QEQ2, 6), ARROW, 20, 0};
    check(choose_destination(arrows, cats, 11, ARROW, 99, false, r, 0, &d, &why) == DestKind::Found && d == KILN_SLOT_ID(QG1, 1),
          "#109 stacking: worn arrows are not topped up by a search; the arrows go to the backpack");
    // full root Generic pages: the worn backpack's pages
    Page full[11];
    std::memcpy(full, pg, sizeof pg);
    for (uint32_t k = 0; k < 8; ++k) {
        full[5].s[k] = Stack{KILN_SLOT_ID(QG1, k), WOOD, 250, 0};
        full[6].s[k] = Stack{KILN_SLOT_ID(QG2, k), WOOD, 250, 0};
    }
    check(choose_destination(full, cats, 11, IRON, 100, false, r, 0, &d, &why) == DestKind::Found && d == KILN_SLOT_ID(QBP1, 0),
          "#109 full root pages: the worn backpack's first empty slot (QBP1:0)");
    // the backpack not worn (no Virtual link, so its pages are not in the walk) and the root full: no room, although
    // the equipment pages, the action bars and the currency page have empty slots
    check(choose_destination(full, cats, 7, IRON, 100, false, r, 0, &d, &why) == DestKind::NoRoom,
          "#109 full backpack, none worn: NoRoom; empty equipment, action-bar and currency slots are not room");
    Page fullbp[11];
    std::memcpy(fullbp, full, sizeof full);
    for (uint32_t k = 0; k < 8; ++k) {
        fullbp[7].s[k] = Stack{KILN_SLOT_ID(QBP1, k), WOOD, 250, 0};
        fullbp[8].s[k] = Stack{KILN_SLOT_ID(QBP2, k), WOOD, 250, 0};
    }
    check(choose_destination(fullbp, cats, 11, IRON, 100, false, r, 0, &d, &why) == DestKind::NoRoom,
          "#109 full backpack, worn backpack full too: NoRoom");
    // gear: an empty slot only, never an equipment slot
    check(choose_destination(pg, cats, 11, SWORD, 1, true, r, 0, &d, &why) == DestKind::Found && d == KILN_SLOT_ID(QG1, 1),
          "#109 gear without toSlot: an empty backpack slot");
    // the action bar: searched only with ActionBar
    DestRules bar = r;
    bar.actionbar = true;
    check(choose_destination(pg, cats, 11, IRON, 100, false, bar, 0, &d, &why) == DestKind::Found && d == KILN_SLOT_ID(QBAR1, 0),
          "#109 ActionBar: the action bar is searched (in link order)");
    check(choose_destination(pg, cats, 11, IRON, 100, false, r, KILN_SLOT_ID(QBAR2, 3), &d, &why) == DestKind::Refused &&
              std::strstr(why, "action bar"),
          "#109 toSlot on the action bar without ActionBar: refused, with the reason");
    check(choose_destination(pg, cats, 11, IRON, 100, false, bar, KILN_SLOT_ID(QBAR2, 3), &d, &why) == DestKind::Found && d == KILN_SLOT_ID(QBAR2, 3),
          "#109 toSlot on the action bar with ActionBar: taken");
    // named equipment slots: the equipment type rules
    DestRules ring = r;
    ring.item_equip_type = 37;  // Armour_Ring
    check(choose_destination(pg, cats, 11, RING, 1, true, ring, q_equip(10), &d, &why) == DestKind::Refused && std::strstr(why, "Items.Equip"),
          "#109 toSlot an empty Ring0 for a ring: refused (a move is no equip: use Items.Equip)");
    check(choose_destination(pg, cats, 11, RING, 1, true, ring, q_equip(5), &d, &why) == DestKind::Refused && std::strstr(why, "does not fit"),
          "#109 toSlot Armour_Head for a ring: refused (does not fit)");
    check(choose_destination(pg, cats, 11, MYC, 99, false, r, q_equip(10), &d, &why) == DestKind::Refused && std::strstr(why, "does not fit"),
          "#109 toSlot Ring0 for a material: refused, nothing moves");
    check(choose_destination(pg, cats, 11, RING, 1, true, ring, q_equip(0), &d, &why) == DestKind::Refused,
          "#109 toSlot Vanity_Head (QEQ1:0) for a ring: refused");
    Page worn[11];
    std::memcpy(worn, pg, sizeof pg);
    worn[1].s[2] = Stack{KILN_SLOT_ID(QEQ2, 2), RING, 1, 901};
    check(choose_destination(worn, cats, 11, RING, 1, true, ring, q_equip(10), &d, &why) == DestKind::Found && d == KILN_SLOT_ID(QG1, 1),
          "#109 toSlot Ring0 already worn: the ring goes to the backpack (a full named slot gives way to the search)");
    check(choose_destination(pg, cats, 11, RING, 1, true, ring, KILN_SLOT_ID(QEQ3, 5), &d, &why) == DestKind::Refused && std::strstr(why, "Items.Equip"),
          "#109 toSlot on an empty equipment page slot no equipment slot shows (a real empty slot's entry is 0): refused, Items.Equip");
    Page odd[11];
    std::memcpy(odd, pg, sizeof pg);
    odd[2].s[5] = Stack{KILN_SLOT_ID(QEQ3, 5), MYC, 3, 0};
    check(choose_destination(odd, cats, 11, MYC, 99, false, r, KILN_SLOT_ID(QEQ3, 5), &d, &why) == DestKind::Refused && std::strstr(why, "cannot see"),
          "#109 toSlot on an occupied equipment page slot no equipment slot shows: refused, never topped up");
    DestRules bag = r;
    bag.item_equip_type = 38;
    check(choose_destination(pg, cats, 11, BAG, 1, true, bag, q_equip(19), &d, &why) == DestKind::Refused && std::strstr(why, "Items.Equip"),
          "#109 toSlot the backpack slot: refused (Items.Equip)");
    DestRules arrow = r;
    arrow.item_equip_type = 6;
    check(choose_destination(arrows, cats, 11, ARROW, 99, false, arrow, q_equip(14), &d, &why) == DestKind::Found && d == q_equip(14),
          "#109 toSlot the Arrow slot for arrows: the worn arrows are topped up");
    // currency and customization pages
    DestRules coin = r;
    coin.item_category = kItemCategoryCurrency;
    check(choose_destination(pg, cats, 11, COIN, 999, false, coin, KILN_SLOT_ID(QCUR, 0), &d, &why) == DestKind::Found && d == KILN_SLOT_ID(QCUR, 0),
          "#109 toSlot on the currency page for a currency item: taken");
    check(choose_destination(pg, cats, 11, COIN, 999, false, coin, 0, &d, &why) == DestKind::Found && d == KILN_SLOT_ID(QG1, 1),
          "#109 a currency item without toSlot: the backpack (the search never fills the currency page)");
    check(choose_destination(pg, cats, 11, MYC, 99, false, r, KILN_SLOT_ID(QCUR, 0), &d, &why) == DestKind::Refused && std::strstr(why, "currency"),
          "#109 toSlot on the currency page for a material: refused");
    check(choose_destination(pg, cats, 11, MYC, 99, false, r, KILN_SLOT_ID(QCUS, 0), &d, &why) == DestKind::Refused,
          "#109 toSlot on a customization page: refused");
    // a toSlot that does not fit (full, or another item) falls back to the backpack search; one that is no slot of the player is refused
    check(choose_destination(pg, cats, 11, IRON, 100, false, r, KILN_SLOT_ID(QG1, 0), &d, &why) == DestKind::Found && d == KILN_SLOT_ID(QG1, 1),
          "#109 toSlot holding another item: the backpack search, never an equipment slot");
    check(choose_destination(full, cats, 11, IRON, 100, false, r, KILN_SLOT_ID(QG2, 4), &d, &why) == DestKind::Found && d == KILN_SLOT_ID(QBP1, 0),
          "#109 toSlot full: the worn backpack");
    check(choose_destination(pg, cats, 11, IRON, 100, false, r, KILN_SLOT_ID(999, 0), &d, &why) == DestKind::Refused && std::strstr(why, "not one of"),
          "#109 toSlot that is no slot of the player: refused");
    check(choose_destination(pg, cats, 11, IRON, 100, false, r, KILN_SLOT_ID(QBP2, 5), &d, &why) == DestKind::Found && d == KILN_SLOT_ID(QBP2, 5),
          "#109 toSlot on the worn backpack's page: taken");
    // an NPC's inventory (flags bit 1, no SlotSelection): Generic pages only, equipment never even when named
    DestRules npc;
    npc.person = true;
    Page np[2] = {page(NEQ, {}), page(NG, {})};
    const uint8_t ncats[2] = {2, 4};
    check(choose_destination(np, ncats, 2, IRON, 100, false, npc, 0, &d, &why) == DestKind::Found && d == KILN_SLOT_ID(NG, 0),
          "#109 an NPC: the Generic page, not its equipment page");
    check(choose_destination(np, ncats, 2, IRON, 100, false, npc, KILN_SLOT_ID(NEQ, 0), &d, &why) == DestKind::Refused,
          "#109 an NPC, toSlot on its equipment page: refused (no SlotSelection names it)");
    // not a person (a chest): unchanged, every page, and a toSlot that is not its own falls back as before
    DestRules chest;
    Page cp[2] = {page(A, {{WOOD, 250, 0}}), page(A2, {})};
    const uint8_t ccats[2] = {4, 4};
    check(choose_destination(cp, ccats, 2, IRON, 100, false, chest, 0, &d, &why) == DestKind::Found && d == KILN_SLOT_ID(A, 1) &&
              find_destination(cp, 2, IRON, 100, false, nullptr, 0, 0, &d) && d == KILN_SLOT_ID(A, 1),
          "#109 a chest: the same slot as before the fix");
    check(choose_destination(cp, ccats, 2, IRON, 100, false, chest, KILN_SLOT_ID(999, 1), &d, &why) == DestKind::Found && d == KILN_SLOT_ID(A, 1),
          "#109 a chest, a toSlot that is not its own: falls back to the search as before");
    // may_land: where a stack may be after the move
    check(may_land(r, pg, cats, 11, KILN_SLOT_ID(QBP1, 3), 0) && may_land(r, pg, cats, 11, KILN_SLOT_ID(QG2, 0), 0),
          "#109 may_land: the root's Generic pages and the worn backpack's");
    check(!may_land(r, pg, cats, 11, KILN_SLOT_ID(QEQ1, 1), 0) && !may_land(r, pg, cats, 11, KILN_SLOT_ID(QBAR1, 0), 0) &&
              !may_land(r, pg, cats, 11, KILN_SLOT_ID(QCUR, 0), 0) && !may_land(r, pg, cats, 11, KILN_SLOT_ID(999, 0), 0),
          "#109 may_land: never an equipment slot, the action bar (without ActionBar), the currency page or a foreign slot");
    check(may_land(r, pg, cats, 11, q_equip(10), q_equip(10)) && may_land(bar, pg, cats, 11, KILN_SLOT_ID(QBAR1, 0), 0),
          "#109 may_land: the accepted toSlot, and the action bar with ActionBar");
    check(may_land(chest, cp, ccats, 2, KILN_SLOT_ID(A2, 7), 0), "#109 may_land: any page of a chest");
}

// Through the engine on the fake world: the move, its result and the rollback.
Result12* __fastcall fake_transfer_astray(Result12* out, void* tx, uint64_t, uint64_t src, int32_t mode) {
    return fake_transfer(out, tx, KILN_SLOT_ID(QEQ1, 7), src, mode);  // a game that puts the stack somewhere else
}
Result12* __fastcall fake_transfer_eats(Result12* out, void*, uint64_t, uint64_t src, int32_t) {
    std::memset(out, 0, 12);  // a game that takes the stack and puts it nowhere
    uint8_t* s = stack(static_cast<uint32_t>(src), static_cast<uint32_t>(src >> 32));
    std::memcpy(&out->value, s + 4, 4);
    std::memset(s, 0, 12);
    return out;
}

void issue109_engine() {
    E.tx_ctor = &fake_tx_ctor;
    E.tx_init = &fake_tx_init;
    E.commit = &fake_tx_noop;
    E.rollback = &fake_tx_rollback;
    E.free_arrays = &fake_tx_noop;
    set_item(MYC, 99, false, 0, 9);
    set_item(RING, 1, true, 37, 1);
    set_item(COIN, 999, false, 0, 11);
    FakeTx tx;
    Access a;
    check(access_from_tx(&tx, &a), "#109 engine: transaction accessors check out");
    auto move = [&](uint32_t from, uint64_t slot, uint32_t to, uint32_t count, uint64_t to_slot, uint32_t flags) {
        MoveJob job;
        job.r.size = sizeof(KilnItemsMoveRequest);
        job.r.kind = KILN_MOVE_STACK;
        job.r.from = from;
        job.r.to = to;
        job.r.slot = slot;
        job.r.count = count;
        job.r.to_slot = to_slot;
        job.r.flags = flags;
        KilnItemsMoveResult res{};
        unsigned long code = 0;
        run_move(a, job, reinterpret_cast<uint8_t*>(&tx), &res, &code);
        return res;
    };
    auto eq_untouched = [] { return page_empty(QEQ1) && page_empty(QEQ2) && page_empty(QEQ3) && page_empty(QCUR) && page_empty(QCUS); };

    // the report: 4 Active Mycelium from a chest into a player, no toSlot
    reset_world();
    make_player_q(true);
    PageSet ps;
    bool init = false;
    check(read_pages(a, Q, &ps, &init) && ps.n == 11 && ps.cat[0] == 2 && ps.cat[3] == 4 && ps.cat[7] == 4 && ps.p[7].entity == QBP1 &&
              ps.cat[9] == 3 && ps.flags == 0x01,
          "#109 engine: a player's pages carry their categories, the worn backpack's pages in the walk (Virtual expanded)");
    put(A, 0, MYC, 4);
    KilnItemsMoveResult r = move(A, KILN_SLOT_ID(A, 0), Q, 0, 0, 0);
    check(r.status == KILN_ITEMS_DONE && r.done == 4 && r.change_count == 1 && r.changes[0].to_slot == KILN_SLOT_ID(QG1, 0) &&
              item_at(QG1, 0) == MYC && count_at(QG1, 0) == 4 && eq_untouched(),
          "#109 engine: 4 Active Mycelium land in the first backpack slot (QG1:0), the equipment pages stay empty");
    // stacking into an existing stack in the worn backpack
    put(A, 1, MYC, 5);
    put(QG1, 0, 0, 0);
    put(QBP2, 4, MYC, 90);
    r = move(A, KILN_SLOT_ID(A, 1), Q, 0, 0, 0);
    check(r.status == KILN_ITEMS_DONE && r.done == 5 && count_at(QBP2, 4) == 95 && eq_untouched(),
          "#109 engine: a partial stack in the worn backpack is topped up");
    // part of a stack that only partly fits on the partial stack: the rest goes to the first empty backpack slot
    put(A, 2, MYC, 10);
    r = move(A, KILN_SLOT_ID(A, 2), Q, 10, 0, 0);
    check(r.status == KILN_ITEMS_DONE && r.done == 10 && count_at(QBP2, 4) == 99 && item_at(QG1, 0) == MYC && count_at(QG1, 0) == 6 &&
              eq_untouched(),
          "#109 engine: 10 more: 4 top the stack up to 99, 6 go to an empty backpack slot");
    // full root pages: the worn backpack
    reset_world();
    make_player_q(true);
    fill_page(QG1, WOOD, 250);
    fill_page(QG2, WOOD, 250);
    put(A, 0, MYC, 4);
    r = move(A, KILN_SLOT_ID(A, 0), Q, 0, 0, 0);
    check(r.status == KILN_ITEMS_DONE && item_at(QBP1, 0) == MYC && eq_untouched() && page_empty(QBAR1),
          "#109 engine: root pages full: the worn backpack's page");
    // the backpack not worn, the root full: NoRoom, the chest keeps the stack
    reset_world();
    make_player_q(false);
    fill_page(QG1, WOOD, 250);
    fill_page(QG2, WOOD, 250);
    put(A, 0, MYC, 4);
    r = move(A, KILN_SLOT_ID(A, 0), Q, 0, 0, 0);
    check(r.status == KILN_ITEMS_NO_ROOM && r.done == 0 && count_at(A, 0) == 4 && eq_untouched() && page_empty(QBAR1) && page_empty(QBAR2) &&
              std::strstr(r.message, "backpack"),
          "#109 engine: full backpack, none worn: NoRoom ('no room in ... backpack'), the chest keeps all 4");
    // a named equipment slot: a ring into Ring0 is fine, a material is refused
    reset_world();
    make_player_q(true);
    put(A, 3, RING, 1, 4001);
    r = move(A, KILN_SLOT_ID(A, 3), Q, 0, q_equip(10), 0);
    check(r.status == KILN_ITEMS_REFUSED && r.done == 0 && r.change_count == 0 && pide_at(A, 3) == 4001 && page_empty(QEQ2) &&
              std::strstr(r.message, "Items.Equip"),
          "#109 engine: toSlot an empty Ring0 for a ring: Refused (use Items.Equip), the ring stays in the chest");
    set_item(ARROW, 99, false, 6, 8);
    put(QEQ2, 6, ARROW, 20);
    put(A, 4, ARROW, 30);
    r = move(A, KILN_SLOT_ID(A, 4), Q, 0, q_equip(14), 0);
    check(r.status == KILN_ITEMS_DONE && r.done == 30 && count_at(QEQ2, 6) == 50 && r.change_count == 1 && r.changes[0].to_slot == q_equip(14) &&
              count_at(A, 4) == 0,
          "#109 engine: toSlot the Arrow slot holding arrows: the worn stack is topped up (the after-move check accepts it)");
    put(QEQ2, 6, 0, 0);
    put(A, 0, MYC, 4);
    r = move(A, KILN_SLOT_ID(A, 0), Q, 0, q_equip(11), 0);
    check(r.status == KILN_ITEMS_REFUSED && r.done == 0 && count_at(A, 0) == 4 && item_at(QEQ2, 3) == 0 && std::strstr(r.message, "does not fit"),
          "#109 engine: toSlot Ring1 for a material: Refused with the reason, nothing moves");
    r = move(A, KILN_SLOT_ID(A, 0), Q, 0, KILN_SLOT_ID(QBAR1, 2), 0);
    check(r.status == KILN_ITEMS_REFUSED && count_at(A, 0) == 4 && page_empty(QBAR1),
          "#109 engine: toSlot on the action bar without ActionBar: Refused");
    r = move(A, KILN_SLOT_ID(A, 0), Q, 0, KILN_SLOT_ID(QBAR1, 2), KILN_ITEMS_MOVE_ACTIONBAR);
    check(r.status == KILN_ITEMS_DONE && item_at(QBAR1, 2) == MYC, "#109 engine: ... with ActionBar: Done there");
    // a toSlot that holds something else: the backpack, never the equipment
    put(A, 0, MYC, 4);
    put(QG1, 0, WOOD, 250);
    r = move(A, KILN_SLOT_ID(A, 0), Q, 0, KILN_SLOT_ID(QG1, 0), 0);
    check(r.status == KILN_ITEMS_DONE && item_at(QG1, 1) == MYC && page_empty(QEQ1) && page_empty(QEQ3),
          "#109 engine: toSlot that does not fit: the first free backpack slot");
    // a dry run says where it would go and leaves everything as it was
    put(A, 0, MYC, 4);
    r = move(A, KILN_SLOT_ID(A, 0), Q, 0, 0, KILN_ITEMS_MOVE_DRY_RUN);
    check(r.status == KILN_ITEMS_DONE && r.changes[0].to_slot == KILN_SLOT_ID(QG1, 1) && count_at(A, 0) == 4 && count_at(QG1, 1) == 4,
          "#109 engine: a dry run names the backpack slot (the partial stack QG1:1) and moves nothing");
    // the hook side (a transfer handler's move into a player) goes through the same rules
    reset_world();
    make_player_q(true);
    put(A, 0, MYC, 4);
    MoveOutcome mo;
    const uint32_t moved = move_stack_in(a, &tx, A, KILN_SLOT_ID(A, 0), Q, 0, 0, false, false, &mo);
    check(moved == 4 && mo.status == KILN_ITEMS_DONE && item_at(QG1, 0) == MYC && eq_untouched(),
          "#109 engine: move_stack_in (a transfer handler's move) lands in the backpack too");
    // an NPC's inventory (flags bit 1, no SlotSelection)
    make_setup_cats(NPC, {{NEQ, 2}, {NG, 4}}, 0x02);
    put(A, 1, IRON, 3);
    r = move(A, KILN_SLOT_ID(A, 1), NPC, 0, 0, 0);
    check(r.status == KILN_ITEMS_DONE && item_at(NG, 0) == IRON && page_empty(NEQ), "#109 engine: an NPC: its Generic page, not its equipment");
    // a chest is unchanged: its first empty slot
    put(A, 2, IRON, 2);
    r = move(A, KILN_SLOT_ID(A, 2), B, 0, 0, 0);
    check(r.status == KILN_ITEMS_DONE && item_at(B, 0) == IRON, "#109 engine: a chest: its first empty slot, as before");

    // never Done for a stack that did not land where the player can reach it: the result is checked and rolled back
    reset_world();
    make_player_q(true);
    put(A, 0, MYC, 4);
    E.slot_transfer = &fake_transfer_astray;
    r = move(A, KILN_SLOT_ID(A, 0), Q, 0, 0, 0);
    E.slot_transfer = &fake_transfer;
    check(r.status == KILN_ITEMS_REFUSED && r.done == 0 && r.change_count == 0 && count_at(A, 0) == 4 && page_empty(QEQ1) &&
              std::strstr(r.message, "rolled back"),
          "#109 engine: a slot transfer that puts the stack into an equipment slot: Refused and rolled back, the chest keeps it");
    E.slot_transfer = &fake_transfer_eats;
    r = move(A, KILN_SLOT_ID(A, 0), Q, 0, 0, 0);
    E.slot_transfer = &fake_transfer;
    check(r.status == KILN_ITEMS_REFUSED && r.done == 0 && count_at(A, 0) == 4 && std::strstr(r.message, "without arriving"),
          "#109 engine: items that leave the slot and arrive nowhere: Refused and rolled back");
    E.slot_transfer = &fake_transfer_eats;
    r = move(A, KILN_SLOT_ID(A, 0), B, 0, 0, 0);
    E.slot_transfer = &fake_transfer;
    check(r.status == KILN_ITEMS_REFUSED && count_at(A, 0) == 4, "#109 engine: ... into a chest too");
    reset_world();
}

// GiveEquip exercises the production engine path with delayed creation, as InventoryCommit does.
bool ge_equip_fail = false, ge_guard = false;
int ge_ctors = 0, ge_commits = 0, ge_rollbacks = 0, ge_frees = 0;
bool ge_free_fault = false, ge_preflight_fault = false, ge_steal_replaced = false;
bool ge_spill = false, ge_move_new = false, ge_empty_guard = false, ge_invalid_pide = false;
int ge_fault = 0;
void ge_maybe_fault(int stage) {
    if (ge_fault==stage) { ge_fault=0; RaiseException(EXCEPTION_ACCESS_VIOLATION,0,0,nullptr); }
}
void* __fastcall ge_find(uint32_t item, void* items) {
    if (ge_preflight_fault) { ge_preflight_fault=false; RaiseException(EXCEPTION_ACCESS_VIOLATION,0,0,nullptr); }
    return fake_item_find(item,items);
}
Result12* __fastcall ge_remove(Result12* out, void* tx, uint32_t owner, uint32_t item, uint64_t slot, uint32_t count) {
    fake_remove(out,tx,owner,item,slot,count); ge_maybe_fault(3); return out;
}
uint64_t ge_target = 0;
bool ge_transfer_keeps_pide = false;  // a vacated cell that still names the moved instance in its pide field
Result12* __fastcall ge_transfer(Result12* out, void* tx, uint64_t dst, uint64_t src, int32_t mode) {
    const auto pide = pide_at(KILN_SLOT_INVENTORY(src), KILN_SLOT_INDEX(src));
    const auto item = item_at(KILN_SLOT_INVENTORY(src), KILN_SLOT_INDEX(src));
    fake_transfer(out, tx, dst, src, mode);
    if (ge_transfer_keeps_pide && !out->error && !count_at(KILN_SLOT_INVENTORY(src), KILN_SLOT_INDEX(src)))
        std::memcpy(stack(KILN_SLOT_INVENTORY(src), KILN_SLOT_INDEX(src)) + 8, &pide, 4);
    if (!out->error && out->value && pide) push_op(5, dst, item, static_cast<uint16_t>(out->value), pide);
    ge_maybe_fault(2);
    return out;
}
void* __fastcall ge_ctor(void* tx) {
    ++ge_ctors;
    fake_tx_ctor(tx);
    g_ops.clear();
    g_oplist = reinterpret_cast<OpList*>(static_cast<uint8_t*>(tx) + E.tx_ops);
    *g_oplist = {};
    return tx;
}
void __fastcall ge_init(void* tx, const void*) {
    auto* b = static_cast<uint8_t*>(tx);
    b[E.tx_guard_flags] = ge_guard ? 0x40 : 0;
    if (ge_empty_guard) {
        const uint64_t zero=0, one=1;
        std::memcpy(b+E.tx_empty_state,&zero,8); std::memcpy(b+E.tx_touched_count,&one,8);
    }
    ge_maybe_fault(1);
}
Result12* __fastcall ge_add(Result12* out, void*, uint32_t, uint64_t slot, uint32_t item, uint32_t count,
                           OptU32*, OptU8*, OptF32*) {
    *out = {};
    if (g_add_fails) return out->error = 0x27, out;
    push_op(0, ge_spill ? KILN_SLOT_ID(101,7) : slot, item, static_cast<uint16_t>(count), 0);
    put(KILN_SLOT_INVENTORY(slot), KILN_SLOT_INDEX(slot), item, count, 0); // no pide until commit
    ge_maybe_fault(4);
    return out;
}
Result12* __fastcall ge_select(Result12* out, void*, uint8_t remove, uint32_t, const void*, uint8_t slot,
                               uint64_t cell, const void* info, uint32_t pide) {
    *out={};
    if(ge_equip_fail) return out->error=0x11,out;
    uint32_t item; std::memcpy(&item,info,4);
    push_op(7,remove ? 0 : cell,item,0,pide);
    g_ops[g_ops.size()-kOpStride+0x27]=slot;
    ge_target=cell;
    ge_maybe_fault(5);
    return out;
}
// EquipItem's first decision, as the jump table of both approved builds has it (docs/re/items.md section 22):
// types 1-12 and 36 are selected in place in Generic (4); 13-18, 34, 35, 37, 38 move to Equipment (2); others 0.
int ge_kind_override = -1;  // -1: the table below
uint8_t __fastcall ge_kind(const void* info) {
    if (ge_kind_override >= 0) return static_cast<uint8_t>(ge_kind_override);
    const uint8_t t = static_cast<const uint8_t*>(info)[0x104];
    if ((t >= 1 && t <= 12) || t == 36) return 4;
    if ((t >= 13 && t <= 18) || t == 34 || t == 35 || t == 37 || t == 38) return 2;
    return 0;
}
int ge_equip_calls = 0;
Result12* __fastcall ge_equip(Result12* out, void* tx, uint64_t source, uint8_t slot) {
    *out = {};
    ++ge_equip_calls;
    if (ge_equip_fail) return out->error = 0x11, out;
    uint64_t target=0;
    std::memcpy(&target,comp(T_SEL,P,216).data()+selection_index(slot)*8,8);
    // Ammo is selected where it lies, not moved into a spare Equipment cell. This models only that routing;
    // the real selection helper remains runtime-qualified by items.cpp, not by this fake.
    if (slot>=14 && slot<=16) target=source;
    if(!target) { // gear's chooser sees the pending source as occupied: it chooses another free cell
        for(int i=0;i<8;++i) if(!count_at(100,i)) { target=KILN_SLOT_ID(100,i); break; }
    }
    if(!target) return out->error=0x1f,out;
    if(source!=target) ge_transfer(out,tx,target,source,0);
    const auto item=item_at(KILN_SLOT_INVENTORY(target),KILN_SLOT_INDEX(target));
    ge_select(out,tx,0,P,comp(T_SEL,P,216).data(),slot,target,g_item_bytes[item%8],
              pide_at(KILN_SLOT_INVENTORY(target),KILN_SLOT_INDEX(target)));
    if (ge_steal_replaced) put(101,3,0,0);
    if (ge_move_new) {
        put(100,7,item_at(KILN_SLOT_INVENTORY(target),KILN_SLOT_INDEX(target)),count_at(KILN_SLOT_INVENTORY(target),KILN_SLOT_INDEX(target)));
        put(KILN_SLOT_INVENTORY(target),KILN_SLOT_INDEX(target),0,0);
    }
    return out;
}
void __fastcall ge_commit(void*) {
    ++ge_commits;
    ge_maybe_fault(6);
    for (size_t i = 0; i < g_ops.size(); i += kOpStride) {
        const auto* op = g_ops.data() + i;
        uint64_t slot; uint32_t item;
        std::memcpy(&slot, op, 8); std::memcpy(&item, op + 8, 4);
        if(op[0x26]==7) {
            std::memcpy(comp(T_SEL,P,216).data()+selection_index(op[0x27])*8,&slot,8);
            uint32_t existing; std::memcpy(&existing,op+0x20,4);
            if(existing) {
                auto& state=comp(T_ITEMSTATE,existing,20);
                if (slot) state[0x0e] |= 8; else state[0x0e] &= ~8;
            }
            continue;
        }
        if (op[0x26] == 1 && op[0x30]) {
            uint32_t existing; std::memcpy(&existing, op + 0x20, 4);
            for (auto it=g_comps.begin(); it!=g_comps.end();) {
                if (it->first.second==existing) it=g_comps.erase(it); else ++it;
            }
            continue;
        }
        if (op[0x26] == 5) {
            uint32_t existing; std::memcpy(&existing, op + 0x20, 4);
            auto& state=comp(T_ITEMSTATE,existing,20); std::memcpy(state.data(),&slot,8);
            auto& owner=comp(T_OWNER,existing,4); const auto entity=KILN_SLOT_INVENTORY(slot);
            std::memcpy(owner.data(),&entity,4);
            continue;
        }
        if(op[0x26]!=0 || !(*reinterpret_cast<uint32_t*>(g_item_bytes[item % 8] + E.item_flags) & 2)) continue;
        if (ge_invalid_pide) {
            put(KILN_SLOT_INVENTORY(slot), KILN_SLOT_INDEX(slot), item, 1, UINT32_MAX);
            continue;
        }
        const auto pide = g_next_pide++;
        put(KILN_SLOT_INVENTORY(slot), KILN_SLOT_INDEX(slot), item, 1, pide);
        auto& state = comp(T_ITEMSTATE, pide, kItemStateSize);
        std::memcpy(state.data(), &slot, 8);
        std::memcpy(state.data() + 8, &item, 4);
        state[0x0e] = g_initial_equipped ? 0x48 : 0x40;
        state[0x10] = 2; state[0x11] = 3; // instance data only exists after commit
        const auto entity=KILN_SLOT_INVENTORY(slot);
        std::memcpy(comp(T_OWNER,pide,4).data(),&entity,4);
        ge_maybe_fault(7);
    }
}
void __fastcall ge_rollback(void* tx) { ++ge_rollbacks; fake_tx_rollback(tx); ge_maybe_fault(8); }
void __fastcall ge_free(void*) {
    ++ge_frees;
    if (ge_free_fault) { ge_free_fault=false; RaiseException(EXCEPTION_ACCESS_VIOLATION,0,0,nullptr); }
}
// #139: instance-backed Arrow/Spell/FishingBait (eternal spells). The game equips these in place exactly like plain
// ammo (EquipLocationKind 4) and passes the stack's own pide to op7, whose commit ORs ItemState.IsEquipped (0x08).
// GiveEquip creates the instance once at its final Generic cell and gives it that bit through the scoped creation
// hook, as for gear; replacement deselects (op7 remove + pide) and moves or destroys the old instance.
template <class Run, class Reset, class Refusal>
void eternal_ammo_tests(Run& run, Reset& reset, Refusal& unchanged_refusal) {
    constexpr uint32_t ETERNAL = 13, ETERNAL2 = 14, PLAIN = 12;
    const uint32_t flag_sets[] = {0,KILN_ITEMS_GIVE_EQUIP_TAKE,KILN_ITEMS_GIVE_EQUIP_DRY_RUN,
                                  KILN_ITEMS_GIVE_EQUIP_TAKE|KILN_ITEMS_GIVE_EQUIP_DRY_RUN};
    auto set_ids = [] {
        for (uint32_t id : {ETERNAL, ETERNAL2, PLAIN, ARROW}) std::memcpy(g_item_bytes[id%8],&id,4);
    };
    auto eternal_reset = [&](uint8_t slot) {
        reset();
        set_item(ETERNAL,1,true,slot_type_of(slot),8);
        set_item(ETERNAL2,1,true,slot_type_of(slot),8);
        set_item(PLAIN,99,false,slot_type_of(slot),8);
        set_item(ARROW,99,false,slot_type_of(slot),8);
        set_ids();
    };
    // An existing equipped instance in a Generic cell, as the game leaves one after its own in-place Equip.
    auto existing = [&](uint32_t pide, uint32_t item, uint64_t cell, uint8_t flags) {
        put(KILN_SLOT_INVENTORY(cell),KILN_SLOT_INDEX(cell),item,1,pide);
        auto& state=comp(T_ITEMSTATE,pide,20);
        std::memcpy(state.data(),&cell,8); std::memcpy(state.data()+8,&item,4);
        state[0x0e]=flags; state[0x10]=2; state[0x11]=3;
        const uint32_t page=KILN_SLOT_INVENTORY(cell);
        std::memcpy(comp(T_OWNER,pide,4).data(),&page,4);
    };
    auto select = [&](uint8_t slot, uint64_t cell) { std::memcpy(comp(T_SEL,P,216).data()+slot*8,&cell,8); };
    auto sel = [&](uint8_t slot) { return rd<uint64_t>(comp(T_SEL,P,216).data(),slot*8); };
    const uint64_t cell=KILN_SLOT_ID(101,0), spare=KILN_SLOT_ID(101,3);
    KilnItemsEquipResult r{};

    // Zero selection: one instance, created once in the final verified Generic cell, selected and IsEquipped.
    for (uint8_t slot=14; slot<=16; ++slot) for (uint32_t flags : flag_sets) {
        eternal_reset(slot); put(101,0,0,0);
        const auto before=g_comps; const auto next=g_next_pide;
        r=run(flags,slot,ETERNAL,1);
        const bool dry=(flags&KILN_ITEMS_GIVE_EQUIP_DRY_RUN)!=0;
        char text[200];
        std::snprintf(text,sizeof text,"#139 eternal ammo, zero selection (slot=%u flags=0x%x): one instance created in "
                      "its final Generic cell, IsEquipped, selected in place, never Equipment",slot,flags);
        check(r.status==KILN_ITEMS_DONE && r.to==cell && !r.replaced && ge_ctors==1 && ge_frees==1 &&
              ge_commits==!dry && ge_rollbacks==dry && ge_equip_calls==0 &&
              (dry ? g_comps==before && g_next_pide==next && !r.pide
                   : r.pide==next && g_next_pide==next+1 && item_at(101,0)==ETERNAL && count_at(101,0)==1 &&
                     pide_at(101,0)==r.pide && comp(T_ITEMSTATE,r.pide,20)[0x0e]==0x48 &&
                     rd<uint64_t>(comp(T_ITEMSTATE,r.pide,20).data(),0)==cell &&
                     rd<uint32_t>(comp(T_OWNER,r.pide,4).data(),0)==101 && sel(slot)==cell && page_empty(100)),
              text);
    }
    // The same final bytes as the game's own path: the instance given into that backpack cell, then equipped in place
    // (EquipItem kind 4: SelectEquipment with the stack's pide; commit sets IsEquipped).
    eternal_reset(15); put(101,0,0,0); r=run(0,15,ETERNAL,1);
    const auto route_state=comp(T_ITEMSTATE,r.pide,20), route_owner=comp(T_OWNER,r.pide,4), route_sel=comp(T_SEL,P,216);
    eternal_reset(15); existing(777,ETERNAL,cell,0x40);
    alignas(16) uint8_t vanilla_tx[512]{}; ge_ctor(vanilla_tx); Result12 vanilla_out{};
    ge_equip(&vanilla_out,vanilla_tx,cell,15); ge_commit(vanilla_tx);
    check(vanilla_out.error==0 && comp(T_ITEMSTATE,777,20)==route_state && comp(T_OWNER,777,4)==route_owner &&
          comp(T_SEL,P,216)==route_sel && item_at(101,0)==ETERNAL && count_at(101,0)==1,
          "#139 eternal route matches the fixture's vanilla Give + in-place Equip bytes (state, owner, selection)");

    // A selected cell is reused in place. The instance path never calls EquipItem on the pending stack: the new
    // instance is selected with pide 0 (the creation hook sets its bit), so no stale pide field can be marked.
    for (uint8_t slot=14; slot<=16; ++slot) for (uint32_t flags : flag_sets) {
        const bool dry=(flags&KILN_ITEMS_GIVE_EQUIP_DRY_RUN)!=0, take=(flags&KILN_ITEMS_GIVE_EQUIP_TAKE)!=0;
        // empty selected cell whose stale pide field names a live instance elsewhere
        eternal_reset(slot); existing(555,ETERNAL2,KILN_SLOT_ID(101,5),0x40); put(101,0,0,0,555); select(slot,cell);
        auto before=g_comps; auto next=g_next_pide;
        r=run(flags,slot,ETERNAL,1);
        check(r.status==KILN_ITEMS_DONE && r.to==cell && !r.replaced && !r.replaced_to && ge_equip_calls==0 &&
              (dry ? g_comps==before && g_next_pide==next
                   : pide_at(101,0)==r.pide && r.pide==next && comp(T_ITEMSTATE,r.pide,20)[0x0e]==0x48 &&
                     comp(T_ITEMSTATE,555,20)[0x0e]==0x40 && sel(slot)==cell),
              "#139 eternal ammo, empty selected cell: reused in place; a stale pide field is never marked equipped");
        // occupied by an equipped eternal: Backpack keeps its identity (same pide) and clears its IsEquipped
        eternal_reset(slot); existing(555,ETERNAL2,cell,0x48); put(101,3,0,0); select(slot,cell);
        before=g_comps; next=g_next_pide;
        r=run(flags,slot,ETERNAL,1);
        char text[220];
        std::snprintf(text,sizeof text,"#139 eternal replaces an equipped eternal (slot=%u flags=0x%x): old instance %s, "
                      "new one created once in the same cell",slot,flags,take ? "destroyed" : "moved with its identity, unequipped");
        check(r.status==KILN_ITEMS_DONE && r.to==cell && r.replaced==ETERNAL2 && r.replaced_pide==555 && r.reserved[0]==1 &&
              r.replaced_to==(take ? 0 : spare) && ge_equip_calls==0 && ge_commits==!dry && ge_rollbacks==dry &&
              (dry ? g_comps==before && g_next_pide==next
                   : item_at(101,0)==ETERNAL && pide_at(101,0)==r.pide && r.pide==next &&
                     comp(T_ITEMSTATE,r.pide,20)[0x0e]==0x48 && sel(slot)==cell &&
                     (take ? count_at(101,3)==0 && !g_comps.count({T_ITEMSTATE,555}) && !g_comps.count({T_OWNER,555})
                           : item_at(101,3)==ETERNAL2 && count_at(101,3)==1 && pide_at(101,3)==555 &&
                             comp(T_ITEMSTATE,555,20)[0x0e]==0x40 &&
                             rd<uint64_t>(comp(T_ITEMSTATE,555,20).data(),0)==spare)),
              text);
        // plain ammo replaced by an eternal, and an eternal replaced by plain ammo
        eternal_reset(slot); put(101,0,PLAIN,20); put(101,3,0,0); select(slot,cell);
        before=g_comps;
        r=run(flags,slot,ETERNAL,1);
        check(r.status==KILN_ITEMS_DONE && r.replaced==PLAIN && r.reserved[0]==20 && !r.replaced_pide &&
              (dry ? g_comps==before : pide_at(101,0)==r.pide && r.pide &&
                     (take ? count_at(101,3)==0 : item_at(101,3)==PLAIN && count_at(101,3)==20)),
              "#139 an eternal replaces plain ammo: Backpack/Take/DryRun as for plain");
        eternal_reset(slot); existing(555,ETERNAL2,cell,0x48); put(101,3,0,0); select(slot,cell);
        before=g_comps; next=g_next_pide;
        r=run(flags,slot,ARROW,50);
        check(r.status==KILN_ITEMS_DONE && r.replaced==ETERNAL2 && r.replaced_pide==555 && !r.pide && g_next_pide==next &&
              (dry ? g_comps==before
                   : item_at(101,0)==ARROW && count_at(101,0)==50 &&
                     (take ? !g_comps.count({T_ITEMSTATE,555})
                           : pide_at(101,3)==555 && comp(T_ITEMSTATE,555,20)[0x0e]==0x40)),
              "#139 plain ammo replaces an eternal: the old instance is unequipped and kept (Backpack) or destroyed (Take)");
    }
    // Even if the game's transfer left the moved instance's id in the vacated cell, Backpack never marks it equipped
    // again: neither the new eternal nor plain ammo replacing an instance goes through EquipItem's stack read.
    for (uint32_t item : {ETERNAL, ARROW}) {
        eternal_reset(15); existing(555,ETERNAL2,cell,0x48); put(101,3,0,0); select(15,cell); ge_transfer_keeps_pide=true;
        r=run(0,15,item,item==ARROW ? 50 : 1);
        check(r.status==KILN_ITEMS_DONE && ge_equip_calls==0 && pide_at(101,3)==555 && comp(T_ITEMSTATE,555,20)[0x0e]==0x40,
              item==ARROW ? "#139 plain ammo over an eternal: a stale pide field never re-marks the moved instance"
                          : "#139 eternal over an eternal: a stale pide field never re-marks the moved instance");
    }
    // Plain over plain keeps the existing EquipItem route exactly.
    eternal_reset(15); put(101,0,PLAIN,20); put(101,3,0,0); select(15,cell);
    r=run(0,15,ARROW,50);
    check(r.status==KILN_ITEMS_DONE && ge_equip_calls==1 && item_at(101,3)==PLAIN,
          "#139 plain over plain ammo still runs the game's EquipItem in place (unchanged route)");
    // Refusals before any transaction.
    for (uint32_t flags : flag_sets) {
        eternal_reset(15); put(101,0,0,0);
        auto before=g_comps; r=run(flags,15,ETERNAL,2);
        unchanged_refusal(before,r,KILN_ITEMS_REFUSED,"#139 an eternal is one item: count 2 refused");
        eternal_reset(15); put(101,0,0,0); E.give_equip_instance_ammo_ok=false;
        before=g_comps; r=run(flags,15,ETERNAL,1);
        unchanged_refusal(before,r,KILN_ITEMS_REFUSED,"#139 without items.give_equip.instance_ammo an eternal is refused");
        check(std::strstr(r.message,"instance-backed")!=nullptr,"#139 that refusal names instance-backed ammo");
        eternal_reset(15); put(101,0,0,0); E.give_equip_instance_ammo_ok=false; r=run(flags,15,ARROW,50);
        check(r.status==KILN_ITEMS_DONE && r.to==cell,"#139 plain ammo does not need items.give_equip.instance_ammo");
        eternal_reset(15); existing(555,ETERNAL2,cell,0x48); select(15,cell); E.give_equip_instance_ammo_ok=false;
        before=g_comps; r=run(flags,15,ARROW,50);
        unchanged_refusal(before,r,KILN_ITEMS_SLOT_CHANGED,
                          "#139 without the feature an eternal in the selected cell is neither moved nor destroyed");
        for (int kind : {2, 0}) {
            eternal_reset(15); put(101,0,0,0); ge_kind_override=kind;
            before=g_comps; r=run(flags,15,ETERNAL,1);
            unchanged_refusal(before,r,KILN_ITEMS_REFUSED,"#139 the game's own EquipLocationKind must say in place (Generic)");
            eternal_reset(15); put(101,0,0,0); ge_kind_override=kind; r=run(flags,15,ARROW,50);
            check(r.status==KILN_ITEMS_DONE,"#139 plain ammo does not consult EquipLocationKind (unchanged)");
        }
        for (uint32_t stale : {0u, UINT32_MAX}) {
            eternal_reset(15); put(101,0,ETERNAL2,1,stale); put(101,3,0,0); select(15,cell);
            before=g_comps; r=run(flags,15,ETERNAL,1);
            unchanged_refusal(before,r,KILN_ITEMS_SLOT_CHANGED,
                              "#139 a selected instance-backed stack without its item entity is stale: SlotChanged");
        }
        // Review: a nonzero pide field is not yet an item entity. It must have an ItemState that says it lies in this
        // very cell and is this item; otherwise Backpack would move (and op7 unequip) or Take destroy the wrong entity.
        for (int bad=0; bad<4; ++bad) for (uint32_t item : {ETERNAL, ARROW}) {
            eternal_reset(15); put(101,3,0,0); select(15,cell);
            if (bad==0) put(101,0,ETERNAL2,1,999);                                  // no ItemState at all
            if (bad==1) { existing(555,ETERNAL2,KILN_SLOT_ID(101,5),0x48); put(101,0,ETERNAL2,1,555); }  // lies elsewhere
            if (bad==2) { existing(555,ETERNAL,cell,0x48); put(101,0,ETERNAL2,1,555); }  // another item's entity
            if (bad==3) { existing(555,ETERNAL2,cell,0x48); g_comps[{T_ITEMSTATE,555}].resize(12); }  // short component
            const auto before=g_comps; const auto next=g_next_pide;
            r=run(flags,15,item,item==ARROW ? 50 : 1);
            unchanged_refusal(before,r,KILN_ITEMS_SLOT_CHANGED,
                              "#139 review: a selected instance-backed stack whose ItemState is missing, short, elsewhere or "
                              "another item's is refused (SlotChanged) before any change");
            check(g_next_pide==next && ge_ctors==0,"#139 review: that refusal builds no transaction and creates no instance");
        }
    }
    // Failures roll back once with no instance; a post-commit invariant fault never rolls back.
    for (bool occupied : {false,true}) for (bool take : {false,true}) for (int fault=0; fault<6; ++fault) {
        eternal_reset(15); put(101,3,0,0);
        if (occupied) { existing(555,ETERNAL2,cell,0x48); select(15,cell); } else put(101,0,0,0);
        const auto before=g_comps; const auto next=g_next_pide;
        g_add_fails=fault==0; ge_equip_fail=fault==1; ge_spill=fault==2; ge_guard=fault==3; ge_empty_guard=fault==4;
        ge_invalid_pide=fault==5;
        r=run(take ? KILN_ITEMS_GIVE_EQUIP_TAKE : 0,15,ETERNAL,1);
        if (fault==5) check(r.status==KILN_ITEMS_UNAVAILABLE && ge_commits==1 && !ge_rollbacks && ge_frees==1,
                            "#139 eternal: a committed stack without a valid instance turns equipment off, never rolls back");
        else check(r.status==KILN_ITEMS_REFUSED && g_comps==before && g_next_pide==next && !ge_commits &&
                   ge_rollbacks==1 && ge_frees==1,
                   "#139 eternal add/select/spill/guard failure restores every byte once, no instance, old one untouched");
    }
    reset();
}

void give_equip_test() {
    E.tx_ctor = &ge_ctor; E.tx_init = &ge_init; E.add_items = &ge_add; E.remove_items = &ge_remove; E.item_find = &ge_find;
    E.equip_item = &ge_equip; E.select_equipment=&ge_select; E.commit = &ge_commit; E.rollback = &ge_rollback; E.free_arrays = &ge_free;
    E.slot_transfer = &ge_transfer;
    E.equip_location_kind = &ge_kind; E.give_equip_instance_ammo_ok = true;
    E.tx_ops = 0x100; E.tx_guard_flags = 0x170; E.tx_empty_state = 0xa0; E.tx_touched_count = 0x178;
    g_item_bytes[SWORD % 8][0x104] = 37;
    std::memcpy(g_item_bytes[SWORD%8],&SWORD,4);
    auto reset = [&] {
        reset_world();
        make_setup(P, {100,101});
        comp(T_SETUP, P,84)[0x40] = 2;
        comp(T_SEL,P,216);
        for (int k=0;k<8;++k) put(101,k,WOOD,250);
        ge_ctors=ge_commits=ge_rollbacks=ge_frees=0; ge_equip_fail=ge_guard=g_add_fails=false;
        ge_target=0; ge_free_fault=ge_preflight_fault=ge_steal_replaced=false; ge_fault=0;
        ge_spill=ge_move_new=ge_empty_guard=ge_invalid_pide=false;
        ge_equip_calls=0; ge_kind_override=-1; E.give_equip_instance_ammo_ok=true; ge_transfer_keeps_pide=false;
    };
    auto run = [&](uint32_t flags=0, uint8_t slot=10, uint32_t item=SWORD, uint32_t count=1) {
        alignas(16) uint8_t tx[512]{};
        FakeTx access; Access a; access_from_tx(&access,&a);
        KilnItemsGiveEquipRequest r{sizeof r,P,item,count,flags,slot,{}};
        KilnItemsEquipResult out{};
        unsigned long code = 0;
        if (!run_give_equip(a,r,tx,&out,&code)) out.status=KILN_ITEMS_UNAVAILABLE;
        return out;
    };
    reset(); auto r=run();
    check(r.status==KILN_ITEMS_DONE && r.to && r.to==ge_target && r.pide && pide_at(KILN_SLOT_INVENTORY(r.to),KILN_SLOT_INDEX(r.to))==r.pide,
          "give-equip: full backpack, empty target: created pide lands in final equipped slot");
    const auto route_state=comp(T_ITEMSTATE,r.pide,20), route_owner=comp(T_OWNER,r.pide,4), route_selection=comp(T_SEL,P,216);
    check(comp(T_ITEMSTATE,r.pide,20)[0x0e]==0x48 && comp(T_ITEMSTATE,r.pide,20)[0x10]==2 && comp(T_ITEMSTATE,r.pide,20)[0x11]==3 && ge_commits==1 && ge_frees==1,
          "give-equip: one commit creates proper instance data, arrays freed once");
    // A pre-existing identical item in the backpack, equipped through the fixture's vanilla move + op7 path.
    reset(); put(101,0,SWORD,1,777);
    const uint64_t source=KILN_SLOT_ID(101,0);
    auto& vanilla_state=comp(T_ITEMSTATE,777,20);
    std::memcpy(vanilla_state.data(),&source,8); std::memcpy(vanilla_state.data()+8,&SWORD,4);
    vanilla_state[0x0e]=0x40; vanilla_state[0x10]=2; vanilla_state[0x11]=3;
    alignas(16) uint8_t vanilla_tx[512]{}; ge_ctor(vanilla_tx); Result12 vanilla_out{};
    ge_equip(&vanilla_out,vanilla_tx,source,10); ge_commit(vanilla_tx);
    check(vanilla_out.error==0 && comp(T_ITEMSTATE,777,20)==route_state && comp(T_OWNER,777,4)==route_owner &&
          comp(T_SEL,P,216)==route_selection,
          "give-equip: empty route matches vanilla fixture selection, ItemState bytes (equipped, rarity, level, contained slot) and owner");
    reset(); const uint64_t eq=KILN_SLOT_ID(100,0); put(100,0,SWORD,1,77); std::memcpy(comp(T_SEL,P,216).data()+80,&eq,8);
    const auto before=g_comps;
    r=run(); check(r.status==KILN_ITEMS_NO_ROOM && g_comps==before && ge_frees==0,
          "give-equip: occupied target and full Backpack refuses before mutation");
    r=run(KILN_ITEMS_GIVE_EQUIP_TAKE);
    check(r.status==KILN_ITEMS_DONE && r.replaced==SWORD && r.reserved[0]==1 && !r.replaced_to && pide_at(100,0)!=77,
          "give-equip: Take works with full backpack and reports removed item and count");
    reset(); put(100,0,SWORD,1,77); std::memcpy(comp(T_SEL,P,216).data()+80,&eq,8); put(101,3,0,0);
    auto& old_state = comp(T_ITEMSTATE,77,20);
    std::memcpy(old_state.data(), &eq, 8); std::memcpy(old_state.data()+8, &SWORD, 4);
    old_state[0x0e]=0x49; old_state[0x10]=4; old_state[0x11]=5;
    auto expected_old = old_state;
    const uint64_t old_destination = KILN_SLOT_ID(101,3);
    std::memcpy(expected_old.data(), &old_destination, 8); expected_old[0x0e] &= ~8;
    r=run(); check(r.status==KILN_ITEMS_DONE && r.replaced_to==old_destination && pide_at(101,3)==77,
          "give-equip: Backpack preserves old instance in a free Generic slot");
    check(comp(T_ITEMSTATE,77,20)==expected_old && *reinterpret_cast<uint32_t*>(comp(T_OWNER,77,4).data())==101,
          "give-equip: the replaced Backpack instance is unequipped, preserving every other ItemState byte and owner");
    reset(); put(100,0,SWORD,1,77); std::memcpy(comp(T_SEL,P,216).data()+80,&eq,8); put(101,3,0,0);
    const auto before_lost = g_comps;
    E.slot_transfer = &fake_transfer_eats;
    r=run(); E.slot_transfer = &ge_transfer;
    check(r.status==KILN_ITEMS_REFUSED && g_comps==before_lost && ge_commits==0 && ge_rollbacks==1,
          "give-equip: a transfer that loses the replaced item rolls back before adding the new stack");
    reset(); put(100,0,SWORD,1,77); std::memcpy(comp(T_SEL,P,216).data()+80,&eq,8); put(101,3,0,0);
    const auto before_late_loss=g_comps; ge_steal_replaced=true;
    r=run();
    check(r.status==KILN_ITEMS_REFUSED && g_comps==before_late_loss && ge_commits==0 && ge_rollbacks==1,
          "give-equip: the replaced item is guarded again after the final equip operation");
    for (int fault=0;fault<4;++fault) {
        reset(); put(100,0,SWORD,1,77); std::memcpy(comp(T_SEL,P,216).data()+80,&eq,8);
        const auto baseline=g_comps; const auto ids=g_next_pide;
        g_add_fails=fault==0; ge_equip_fail=fault==1; ge_guard=fault==2;
        r=run(KILN_ITEMS_GIVE_EQUIP_TAKE | (fault==3 ? KILN_ITEMS_GIVE_EQUIP_DRY_RUN : 0));
        check(g_comps==baseline && g_next_pide==ids && ge_commits==0 && ge_rollbacks==1 && ge_frees==1 &&
              ((fault==3)==(r.status==KILN_ITEMS_DONE)),
              "give-equip: add/equip/guard refusal or DryRun restores old bytes and creates no orphan");
    }
    reset();
    ge_preflight_fault=true;
    const auto before_preflight_fault=g_comps;
    r=run();
    check(r.status==KILN_ITEMS_UNAVAILABLE && g_comps==before_preflight_fault && ge_rollbacks==0 && ge_frees==0,
          "give-equip: a preflight read fault never rolls back a prior transaction in the reused buffer");
    reset(); ge_invalid_pide=true;
    r=run();
    check(r.status==KILN_ITEMS_UNAVAILABLE && ge_commits==1 && ge_rollbacks==0,
          "give-equip: the invalid item-entity sentinel cannot pass the committed-gear invariant as Done");
    reset(); ge_free_fault=true;
    r=run();
    check(r.status==KILN_ITEMS_UNAVAILABLE && ge_commits==1 && ge_rollbacks==0 && ge_frees==1 && pide_at(100,0)!=0,
          "give-equip: a cleanup fault after commit cannot roll back the item or free arrays twice");
    for (int stage=1; stage<=8; ++stage) {
        reset(); put(100,0,SWORD,1,77); std::memcpy(comp(T_SEL,P,216).data()+80,&eq,8);
        auto& state=comp(T_ITEMSTATE,77,20); std::fill(state.begin(),state.end(),0x59);
        comp(T_OWNER,77,4); comp(T_FUEL,77,24);
        if (stage==2) put(101,3,0,0);
        const auto fault_baseline=g_comps;
        ge_fault=stage;
        r=run(stage==2 ? 0 : stage==8 ? KILN_ITEMS_GIVE_EQUIP_TAKE|KILN_ITEMS_GIVE_EQUIP_DRY_RUN : KILN_ITEMS_GIVE_EQUIP_TAKE);
        if (stage==6 || stage==7) {
            check(r.status==KILN_ITEMS_UNAVAILABLE && ge_commits==1 && ge_rollbacks==0 && ge_frees==1,
                  "give-equip: a commit fault is explicit, never rolled back or committed twice, arrays freed once");
        } else {
            check(r.status==KILN_ITEMS_UNAVAILABLE && g_comps==fault_baseline && ge_commits==0 && ge_rollbacks==1 && ge_frees==1,
                  "give-equip: init/evacuate/take/add/select/rollback faults restore every old byte and free once");
        }
    }
    for (int fault=0; fault<3; ++fault) {
        reset(); put(100,0,SWORD,1,77); std::memcpy(comp(T_SEL,P,216).data()+80,&eq,8);
        const auto before_guard=g_comps; const auto next_id=g_next_pide;
        ge_spill=fault==0; ge_move_new=fault==1; ge_empty_guard=fault==2;
        r=run(KILN_ITEMS_GIVE_EQUIP_TAKE);
        check(r.status==KILN_ITEMS_REFUSED && g_comps==before_guard && g_next_pide==next_id && ge_commits==0 && ge_rollbacks==1 && ge_frees==1,
              "give-equip: spilled creation, a moved final stack and the missing-empty-accessor commit guard all roll back");
    }
    reset(); std::memcpy(comp(T_SEL,P,216).data()+80,&eq,8);
    r=run();
    check(r.status==KILN_ITEMS_DONE && r.to==eq && !r.replaced && ge_commits==1,
          "give-equip: an already selected but empty equipment cell is the final creation cell");
    reset(); std::memcpy(comp(T_SEL,P,216).data()+88,&eq,8);
    r=run();
    check(r.status==KILN_ITEMS_DONE && r.to==KILN_SLOT_ID(100,1),
          "give-equip: a cell reserved by another equipment selection is never reused");
    reset(); const auto before_bad_count=g_comps;
    r=run(0,10,SWORD,2);
    check(r.status==KILN_ITEMS_REFUSED && g_comps==before_bad_count && ge_frees==0,
          "give-equip: more than one gear item is refused before mutation");
    r=run(0,10,0xdeadbeef,1);
    check(r.status==KILN_ITEMS_UNKNOWN_ITEM && g_comps==before_bad_count && ge_frees==0,
          "give-equip: an unknown item is refused before mutation");
    reset(); const auto base=g_comps;
    r=run(0,5); check(r.status==KILN_ITEMS_REFUSED && g_comps==base && ge_frees==0,
          "give-equip: slot type mismatch refused before transaction");
    r=run(0,23); check(r.status==KILN_ITEMS_REFUSED && std::strstr(r.message,"backpack items") && ge_frees==0,
          "give-equip: bag slot refuses clearly before transaction");
    for(int k=0;k<8;++k) put(100,k,WOOD,250);
    const auto full=g_comps; r=run(); check(r.status==KILN_ITEMS_NO_ROOM && g_comps==full && ge_frees==0,
          "give-equip: no empty unselected equipment slot refuses without mutation");
    // #139: production GiveEquip, not a separate destination-policy model. Equipment has free cells first.
    const uint32_t ammo_flags[] = {0,KILN_ITEMS_GIVE_EQUIP_TAKE,KILN_ITEMS_GIVE_EQUIP_DRY_RUN,
                                  KILN_ITEMS_GIVE_EQUIP_TAKE|KILN_ITEMS_GIVE_EQUIP_DRY_RUN};
    auto ammo_reset = [&](uint8_t slot) {
        reset(); set_item(ARROW,99,false,slot_type_of(slot),8);
        std::memcpy(g_item_bytes[ARROW%8],&ARROW,4);
    };
    auto unchanged_refusal = [&](const auto& before, const KilnItemsEquipResult& out, int status, const char* why) {
        check(out.status==status && g_comps==before && !ge_ctors && !ge_commits && !ge_rollbacks && !ge_frees,why);
    };
    for (uint8_t slot=14; slot<=16; ++slot) for (uint32_t flags : ammo_flags) {
        ammo_reset(slot); put(101,0,0,0);
        const auto before_ammo=g_comps; const auto next=g_next_pide;
        const uint64_t cell=KILN_SLOT_ID(101,0);
        r=run(flags,slot,ARROW,50);
        const bool dry=(flags&KILN_ITEMS_GIVE_EQUIP_DRY_RUN)!=0;
        check(r.status==KILN_ITEMS_DONE && r.to==cell && ge_target==cell && !r.pide && !r.replaced &&
              g_next_pide==next && ge_ctors==1 && ge_commits==!dry && ge_rollbacks==dry && ge_frees==1 &&
              (dry ? g_comps==before_ammo : item_at(101,0)==ARROW && count_at(101,0)==50 && page_empty(100) &&
                  rd<uint64_t>(comp(T_SEL,P,216).data(),slot*8)==cell),
              "#139 zero ammo selection: exact Generic stack selected in place, never Equipment, Take/Backpack/DryRun");
        ammo_reset(slot); put(101,0,0,0);
        std::memcpy(comp(T_SEL,P,216).data()+slot*8,&cell,8);
        const auto before_selected=g_comps;
        r=run(flags,slot,ARROW,50);
        check(r.status==KILN_ITEMS_DONE && r.to==cell && !r.replaced && !r.replaced_to &&
              (dry ? g_comps==before_selected : count_at(101,0)==50 && page_empty(100)),
              "#139 selected empty Generic cell: reuse exactly that cell without an evacuation slot");
    }
    // The synthetic vanilla route for an existing ammo stack and the new zero-selection route select the same cell.
    ammo_reset(15); put(101,0,0,0); r=run(0,15,ARROW,50);
    const auto ammo_route=g_comps;
    ammo_reset(15); put(101,0,ARROW,50);
    ge_ctor(vanilla_tx); ge_equip(&vanilla_out,vanilla_tx,KILN_SLOT_ID(101,0),15); ge_commit(vanilla_tx);
    check(vanilla_out.error==0 && g_comps==ammo_route,
          "#139 ammo route matches fixture vanilla in-place selection (not live persistence proof)");
    for (uint8_t slot=14; slot<=16; ++slot) for (uint32_t flags : ammo_flags) {
        for (int bad=0; bad<8; ++bad) {
            ammo_reset(slot);
            if (bad==1) g_comps.erase({T_INV,101});
            if (bad==2) g_comps[{T_INV,101}].resize(95);
            if (bad==3) comp(T_SETUP,P,84)[0x41]=kLinkEquipment;
            if (bad==4) { put(101,0,0,0); std::memcpy(comp(T_SEL,P,216).data()+0xa0,&A2,4); }
            if (bad==5) { put(101,0,0,0); const uint64_t reserved=KILN_SLOT_ID(101,0);
                std::memcpy(comp(T_SEL,P,216).data()+88,&reserved,8); }
            if (bad==6) { const uint32_t zero=0; std::memcpy(comp(T_SETUP,P,84).data()+4,&zero,4); }
            if (bad==7) comp(T_SETUP,P,84)[0x41]=kLinkCurrency;
            const auto before_invalid=g_comps;
            r=run(flags,slot,ARROW,50);
            unchanged_refusal(before_invalid,r,KILN_ITEMS_NO_ROOM,
                "#139 full/missing/short/non-Generic/action-bar/reserved/absent Generic destination refuses before transaction");
        }
        for (int bad=0; bad<8; ++bad) {
            ammo_reset(slot); put(101,0,0,0);
            const uint64_t cell=(bad==0 || bad==6) ? KILN_SLOT_ID(100,0) : bad==1 ? KILN_SLOT_ID(B,0) :
                                bad==2 ? KILN_SLOT_ID(101,8) : KILN_SLOT_ID(101,0);
            if (bad==1) make_setup(B,{B}); // valid Generic elsewhere is not owned by this player
            if (bad==3) g_comps.erase({T_INV,101});
            if (bad==4) std::memcpy(comp(T_SEL,P,216).data()+0xa0,&A2,4);
            if (bad==5) std::memcpy(comp(T_SEL,P,216).data()+88,&cell,8);
            if (bad==6) put(100,0,ARROW,20);
            if (bad==7) g_comps[{T_INV,101}].resize(95);
            std::memcpy(comp(T_SEL,P,216).data()+slot*8,&cell,8);
            const auto before_invalid=g_comps;
            r=run(flags,slot,ARROW,50);
            unchanged_refusal(before_invalid,r,KILN_ITEMS_SLOT_CHANGED,
                "#139 invalid/non-Generic/foreign/action-bar/shared selected ammo cell refuses, never relocates or mints");
        }
    }
    // A reachable Generic cell is not proof that its occupied contents are still the selected ammo.
    // Reject stale references before Backpack can move, or Take can destroy, an unrelated stack.
    constexpr uint32_t OTHER_AMMO=12;
    for (uint8_t slot=14; slot<=16; ++slot) for (uint32_t flags : ammo_flags) {
        for (int bad=0; bad<5; ++bad) {
            ammo_reset(slot);
            set_item(WOOD,250,false,0,9);
            set_item(OTHER_AMMO,99,bad==4,
                     slot_type_of(bad==2 ? (slot==16 ? 14 : slot+1) : slot),bad==3 ? 9 : 8);
            const uint32_t previous=bad==0 ? WOOD : bad==1 ? 0xfedcba98u : OTHER_AMMO;
            const uint64_t cell=KILN_SLOT_ID(101,0);
            put(101,0,previous,bad==4 ? 1 : 20); put(101,3,0,0);
            std::memcpy(comp(T_SEL,P,216).data()+slot*8,&cell,8);
            const auto before_stale=g_comps;
            r=run(flags,slot,ARROW,50);
            char text[160];
            std::snprintf(text,sizeof text,
                "#139 stale selected Generic ammo (slot=%u flags=0x%x case=%d) refuses before transaction",slot,flags,bad);
            unchanged_refusal(before_stale,r,KILN_ITEMS_SLOT_CHANGED,text);
        }
        ammo_reset(slot); set_item(OTHER_AMMO,99,false,slot_type_of(slot),8);
        const uint64_t cell=KILN_SLOT_ID(101,0), spare=KILN_SLOT_ID(101,3);
        put(101,0,OTHER_AMMO,20); put(101,3,0,0);
        std::memcpy(comp(T_SEL,P,216).data()+slot*8,&cell,8);
        const auto before_other=g_comps;
        r=run(flags,slot,ARROW,50);
        const bool dry=(flags&KILN_ITEMS_GIVE_EQUIP_DRY_RUN)!=0, take=(flags&KILN_ITEMS_GIVE_EQUIP_TAKE)!=0;
        check(r.status==KILN_ITEMS_DONE && r.to==cell && r.replaced==OTHER_AMMO && r.reserved[0]==20 &&
              r.replaced_to==(take ? 0 : spare) && !r.pide && ge_commits==!dry && ge_rollbacks==dry &&
              (dry ? g_comps==before_other : item_at(101,0)==ARROW && count_at(101,0)==50 &&
                  (take ? count_at(101,3)==0 : item_at(101,3)==OTHER_AMMO && count_at(101,3)==20)),
              "#139 a different valid spell/arrow/bait of the matching type still supports Backpack/Take/DryRun");
    }
    // A Virtual link is another setup, not an Inventory page. Require a sized, initialized setup and real Inventory.
    for (int bad=0; bad<5; ++bad) {
        ammo_reset(15);
        make_setup_cats(P,{{100,2},{QBAG,5}},1);
        make_setup_cats(QBAG,{{QBP1,4}},0);
        if (bad==1) g_comps.erase({T_SETUP,QBAG});
        if (bad==2) g_comps[{T_SETUP,QBAG}].resize(80);
        if (bad==3) comp(T_SETUP,QBAG,84)[0x53]=0;
        if (bad==4) g_comps.erase({T_INV,QBP1});
        const auto before_nested=g_comps;
        r=run(0,15,ARROW,50);
        if (!bad) check(r.status==KILN_ITEMS_DONE && r.to==KILN_SLOT_ID(QBP1,0) && count_at(QBP1,0)==50 && page_empty(100),
                       "#139 verified Generic pages behind a Virtual backpack link are eligible");
        else unchanged_refusal(before_nested,r,KILN_ITEMS_NO_ROOM,
                       "#139 unverified Virtual setup or Inventory cannot supply an ammo destination");
    }
    for (int bad=0; bad<3; ++bad) {
        ammo_reset(15); put(101,0,0,0);
        if (bad==0) g_item_bytes[ARROW%8][0xCC]=9;
        if (bad==1) g_item_bytes[ARROW%8][E.item_flags]=4;
        if (bad==2) g_item_bytes[ARROW%8][0x104]=6;
        const auto before_type=g_comps; r=run(0,15,ARROW,50);
        unchanged_refusal(before_type,r,KILN_ITEMS_REFUSED,
                          "#139 ammo requires matching physical type and Ammunition category, no own inventory or material");
    }
    eternal_ammo_tests(run, reset, unchanged_refusal);
    // Free evacuation cells must also be real Generic, not action bars or another equipment reference.
    for (bool take : {false,true}) {
        ammo_reset(15); const uint64_t cell=KILN_SLOT_ID(101,0), reserved=KILN_SLOT_ID(101,3);
        put(101,0,ARROW,20); put(101,3,0,0);
        std::memcpy(comp(T_SEL,P,216).data()+15*8,&cell,8);
        std::memcpy(comp(T_SEL,P,216).data()+88,&reserved,8);
        const auto before_reserved=g_comps;
        r=run(take ? KILN_ITEMS_GIVE_EQUIP_TAKE : 0,15,ARROW,50);
        if (take) check(r.status==KILN_ITEMS_DONE && r.to==cell && !r.replaced_to && r.reserved[0]==20 && count_at(101,0)==50,
                        "#139 selected occupied Generic ammo: Take needs no spare backpack cell");
        else unchanged_refusal(before_reserved,r,KILN_ITEMS_NO_ROOM,
                        "#139 Backpack cannot evacuate old ammo into a cell another equipment selection reserves");
    }
    for (bool occupied : {false,true}) for (bool take : {false,true}) for (int fault=0; fault<7; ++fault) {
        ammo_reset(15); put(101,0,occupied ? ARROW : 0,occupied ? 20 : 0); put(101,3,0,0);
        const uint64_t cell=KILN_SLOT_ID(101,0);
        if (occupied) std::memcpy(comp(T_SEL,P,216).data()+15*8,&cell,8);
        const auto before_fault=g_comps; const auto next=g_next_pide;
        g_add_fails=fault==0; ge_equip_fail=fault==1; ge_spill=fault==2;
        ge_guard=fault==3; ge_empty_guard=fault==4; ge_move_new=fault==5 && occupied;
        ge_free_fault=fault==6;
        if (fault==5 && !occupied) ge_fault=5;
        r=run(take ? KILN_ITEMS_GIVE_EQUIP_TAKE : 0,15,ARROW,50);
        if (fault==6) check(r.status==KILN_ITEMS_UNAVAILABLE && ge_commits==1 && !ge_rollbacks && ge_frees==1 &&
                            count_at(101,0)==50 && g_next_pide==next,
                            "#139 ammo cleanup fault after commit never rolls back or creates an instance");
        else check(r.status==(fault==5 && !occupied ? KILN_ITEMS_UNAVAILABLE : KILN_ITEMS_REFUSED) &&
                   g_comps==before_fault && g_next_pide==next && !ge_commits && ge_rollbacks==1 && ge_frees==1,
                   "#139 zero/occupied ammo add/select/spill/guard/movement failure restores bytes, once, with no partial stack");
    }
    // Regular equipment and vanity still use their own categories, even with free Generic cells.
    reset(); put(101,0,0,0); set_item(SWORD,1,true,13,0); const uint32_t sword=SWORD;
    std::memcpy(g_item_bytes[SWORD%8],&sword,4); comp(T_SETUP,P,84)[0x40]=kLinkCustomization;
    std::memset(comp(T_SEL,P,216).data(),0,8);
    r=run(0,0,SWORD,1);
    check(r.status==KILN_ITEMS_DONE && r.to==KILN_SLOT_ID(100,0) && r.pide && !count_at(101,0),
          "#139 vanity still creates its instance in Customization, not Generic");
    for (uint8_t slot=14; slot<=16; ++slot) for (uint32_t flags : ammo_flags) {
        reset(); set_item(ARROW,99,false,slot_type_of(slot),8);
        const uint64_t selected=KILN_SLOT_ID(101,0);
        put(101,0,ARROW,20); put(101,3,0,0);
        std::memcpy(comp(T_SEL,P,216).data()+slot*8,&selected,8);
        const auto before_stack=g_comps;
        r=run(flags,slot,ARROW,50);
        check(r.status==KILN_ITEMS_DONE && r.to==selected && r.reserved[0]==20 && !r.pide &&
              ((flags&KILN_ITEMS_GIVE_EQUIP_DRY_RUN) ? g_comps==before_stack : count_at(101,0)==50) &&
              ((flags&KILN_ITEMS_GIVE_EQUIP_TAKE) ? !r.replaced_to : r.replaced_to==KILN_SLOT_ID(101,3)) &&
              ((flags&KILN_ITEMS_GIVE_EQUIP_DRY_RUN) || (flags&KILN_ITEMS_GIVE_EQUIP_TAKE) ||
                  (item_at(101,3)==ARROW && count_at(101,3)==20)) && page_empty(100),
              "give-equip: worn Generic-cell arrows/spells/bait support Backpack, Take and nonmutating DryRun with exact old count");
    }
}

Result12* __fastcall padded_storage_add(Result12* out,void*,uint32_t,uint64_t,uint32_t item,uint32_t count,OptU32*,OptU8*,OptF32*) {
    *out={};push_op(0,KILN_SLOT_ID(B2,1),item,static_cast<uint16_t>(count),0);
    put(B2,1,item,count_at(B2,1)+count);return out;
}
void storage_guard_test() {
    E.tx_ctor=&fake_tx_ctor;E.tx_init=&fake_tx_init;E.commit=&fake_tx_noop;E.rollback=&fake_tx_rollback;E.free_arrays=&fake_tx_noop;
    static const StorageGuards guards{
        +[](uint32_t e){return e==B||e==B2;}, +[](uint32_t e){return e==B;},
        +[](uint32_t e,uint32_t item,bool,uint64_t slot){if(e!=B&&e!=B2)return true;if(item!=IRON)return false;if(!slot)return true;uint32_t page=KILN_SLOT_INVENTORY(slot);return(page==B||page==B2)&&((page==B2?8u:0u)+KILN_SLOT_INDEX(slot)<9);},
        +[](uint32_t e,uint32_t actor){return e!=B||actor==1;}};
    storage_guards.store(&guards);
    for(uint32_t kind:{uint32_t(KILN_MOVE_STACK),uint32_t(KILN_MOVE_QUICK_STACK),uint32_t(KILN_MOVE_ALL)}) {
        reset_world();put(A,0,WOOD,9);FakeTx tx;Access access;access_from_tx(&tx,&access);
        MoveJob job;job.r.size=sizeof job.r;job.r.kind=kind;job.r.from=A;job.r.to=B;job.r.slot=kind==KILN_MOVE_STACK?KILN_SLOT_ID(A,0):0;
        KilnItemsMoveResult result{};unsigned long code=0;const auto before=g_comps;
        check(run_move(access,job,reinterpret_cast<uint8_t*>(&tx),&result,&code)&&result.status==KILN_ITEMS_FILTERED&&g_comps==before,
              "storage: filtered stack, quick stack and move all refuse without mutation");
    }
    for (bool merging : {false,true}) {
        reset_world();put(A,0,IRON,9);if(merging)put(B2,1,IRON,3);FakeTx tx;Access access;access_from_tx(&tx,&access);
        MoveJob job;job.r.size=sizeof job.r;job.r.kind=KILN_MOVE_STACK;job.r.from=A;job.r.to=B;job.r.slot=KILN_SLOT_ID(A,0);job.r.to_slot=KILN_SLOT_ID(B2,1);
        KilnItemsMoveResult result{};unsigned long code=0;const auto before=g_comps;
        check(run_move(access,job,reinterpret_cast<uint8_t*>(&tx),&result,&code)&&result.status==KILN_ITEMS_FILTERED&&g_comps==before,
              merging?"storage: merge refuses padded logical cell 9":"storage: Move refuses padded logical cell 9");
    }
    for(bool automatic:{false,true}) {
        reset_world();put(B2,1,IRON,3);FakeTx tx;Access access;access_from_tx(&tx,&access);E.tx_ops=0x100;E.add_items=&padded_storage_add;g_ops.clear();g_oplist=reinterpret_cast<OpList*>(reinterpret_cast<uint8_t*>(&tx)+E.tx_ops);*g_oplist={};
        Job job;job.r.size=sizeof job.r;job.r.entity=B;job.r.item=IRON;job.r.count=5;job.r.slot=automatic?0:KILN_SLOT_ID(B2,1);KilnItemsResult result{};unsigned long code=0;const auto before=g_comps;
        check(run_job(access,job,reinterpret_cast<uint8_t*>(&tx),&result,&code)&&result.status==KILN_ITEMS_FILTERED&&g_comps==before,
              automatic?"storage: automatic Give top-up rolls back padded cell 9":"storage: Give refuses padded logical cell 9");
    }
    reset_world();put(A,0,IRON,5);for(unsigned i=0;i<8;++i)put(B,i,IRON,100);put(B2,0,IRON,100);put(B2,1,IRON,3);
    {FakeTx tx;Access access;access_from_tx(&tx,&access);MoveJob job;job.r.size=sizeof job.r;job.r.kind=KILN_MOVE_QUICK_STACK;job.r.from=A;job.r.to=B;KilnItemsMoveResult result{};unsigned long code=0;const auto before=g_comps;
    check(run_move(access,job,reinterpret_cast<uint8_t*>(&tx),&result,&code)&&result.status==KILN_ITEMS_FILTERED&&g_comps==before,"storage: bulk merge rolls back padded cell 9");}
    E.get_component=+[](PtrSize*out,uintptr_t,uint32_t hash,uint32_t entity)->PtrSize*{auto it=g_comps.find({hash==kHashInventorySetup?T_SETUP:T_INV,entity});*out=it==g_comps.end()?PtrSize{}:PtrSize{it->second.data(),it->second.size()};return out;};
    bool matched=true;unsigned long code=0;
    check(inventory_matches(0,B,0,&matched,&code)&&!matched,"storage: default inventory scan excludes holders");
    check(inventory_matches(0,B,KILN_ITEMS_FIND_CONTAINERS,&matched,&code)&&!matched,"storage: Containers excludes holders");
    check(inventory_matches(0,B,KILN_ITEMS_FIND_PLAYER_STORAGE,&matched,&code)&&matched,"storage: explicit player-storage scan includes holders");
    storage_guards.store(nullptr);
}

void checked_move_test() {
    E.tx_ctor = &fake_tx_ctor;
    E.tx_init = &fake_tx_init;
    E.commit = &fake_tx_noop;
    E.rollback = &fake_tx_rollback;
    E.free_arrays = &fake_tx_noop;
    E.instances_ok = true;
    stamps().reset();
    reset_world();
    FakeTx tx;
    Access a;
    check(access_from_tx(&tx, &a), "checked moves: usable transaction");
    MoveJob job{};
    job.r.kind = KILN_MOVE_STACK;
    job.r.flags = KILN_ITEMS_MOVE_CHECKED;
    job.r.from = A;
    job.r.to = B;
    job.r.slot = KILN_SLOT_ID(A, 0);
    job.r.to_slot = KILN_SLOT_ID(B, 5);
    job.r.count = 2;
    job.source = {WOOD, 7, 0, 0};
    auto run = [&] {
        unsigned long code = 0;
        KilnItemsMoveResult result{};
        check(run_move(a, job, reinterpret_cast<uint8_t*>(&tx), &result, &code),
              "checked moves: simulation returned");
        return result;
    };
    auto refused = [&](int status, const char* reason) {
        const auto before = g_comps;
        const auto r = run();
        check(r.status == status && r.done == 0 && before == g_comps, reason);
    };

    put(A, 0, IRON, 7);
    refused(KILN_ITEMS_SLOT_CHANGED, "checked moves: replacing selected type leaves both owners untouched");
    put(A, 0, WOOD, 6);
    refused(KILN_ITEMS_SLOT_CHANGED, "checked moves: changed source count");
    put(A, 0, WOOD, 7);
    put(B, 5, IRON, 7);
    refused(KILN_ITEMS_SLOT_CHANGED, "checked moves: changed destination leaves both owners untouched");
    job.destination = {IRON, 7, 0, 0};
    refused(KILN_ITEMS_NO_ROOM, "checked moves: occupied destination cannot fall back to another free slot");
    put(B, 5, WOOD, 249);
    job.destination = {WOOD, 249, 0, 0};
    // review: a named slot with room for only part of the count is NoRoom, as an occupied or a full one, not Refused
    refused(KILN_ITEMS_NO_ROOM, "checked moves: partial destination cannot spill into another slot (NoRoom, rolled back)");
    put(B, 5, WOOD, 248);
    job.destination = {WOOD, 248, 0, 0};
    auto result = run();
    check(result.status == KILN_ITEMS_DONE && result.done == 2 && count_at(B, 5) == 250 && count_at(A, 0) == 5,
          "checked moves: requested amount merges at exactly the chosen slot");

    reset_world();
    put(A, 0, SWORD, 1, 3077);
    job.source = {SWORD, 1, 3078, stamp_for(3078, SWORD)};
    job.destination = {};
    job.r.count = 1;
    refused(KILN_ITEMS_SLOT_CHANGED, "checked moves: same type, different gear entity");
    const uint32_t stamp = stamp_for(3077, SWORD);
    job.source = {SWORD, 1, 3077, stamp + 1};
    refused(KILN_ITEMS_SLOT_CHANGED, "checked moves: same gear entity id with a different generation");
    job.source.stamp = stamp;
    result = run();
    check(result.status == KILN_ITEMS_DONE && result.done == 1 && pide_at(B, 5) == 3077 &&
          result.changes[0].reserved == stamp,
          "checked moves: exact gear entity and stamp arrive at the specified slot");
}

int main(int argc, char** argv) {
    setup_engine();
    const std::string mode = argc > 1 ? argv[1] : "all";
    if (mode == "checked") { checked_move_test(); return failures ? EXIT_FAILURE : EXIT_SUCCESS; }
    if (mode == "storage") { storage_guard_test(); return failures ? EXIT_FAILURE : EXIT_SUCCESS; }
    if (mode == "give-equip") { give_equip_test(); return failures ? EXIT_FAILURE : EXIT_SUCCESS; }
    if (mode == "review") {
        review_tests();
        std::printf("items move test (%s): %d failure(s)\n", mode.c_str(), failures);
        return failures ? EXIT_FAILURE : EXIT_SUCCESS;
    }
    if (mode == "replace") {
        replace_test();
        std::printf("items move test (%s): %d failure(s)\n", mode.c_str(), failures);
        return failures ? EXIT_FAILURE : EXIT_SUCCESS;
    }
    if (mode == "stamps") {
        stamps_mode_test();
        std::printf("items move test (%s): %d failure(s)\n", mode.c_str(), failures);
        return failures ? EXIT_FAILURE : EXIT_SUCCESS;
    }
    if (mode == "move-off") {
        move_off_test();
        std::printf("items move test (%s): %d failure(s)\n", mode.c_str(), failures);
        return failures ? EXIT_FAILURE : EXIT_SUCCESS;
    }
    if (mode == "links") {
        link_test();
        std::printf("items move test (%s): %d failure(s)\n", mode.c_str(), failures);
        return failures ? EXIT_FAILURE : EXIT_SUCCESS;
    }
    if (mode == "partial-install") {
        partial_install_test();
        std::printf("items move test (%s): %d failure(s)\n", mode.c_str(), failures);
        return failures ? EXIT_FAILURE : EXIT_SUCCESS;
    }
    if (mode == "chain-before" || mode == "chain-after") {
        chain_test(mode == "chain-before");
        std::printf("items move test (%s): %d failure(s)\n", mode.c_str(), failures);
        return failures ? EXIT_FAILURE : EXIT_SUCCESS;
    }
    checked_move_test();
    setup_engine();
    logic_tests();
    issue109_logic();
    setup_engine();
    engine_tests();
    setup_engine();
    issue109_engine();
    setup_engine();
    hide_fault_test();
    setup_engine();
    hook_tests();
    std::printf("items move test: %d failure(s)\n", failures);
    return failures ? EXIT_FAILURE : EXIT_SUCCESS;
}
