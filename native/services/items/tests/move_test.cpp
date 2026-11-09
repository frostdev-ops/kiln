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

#include "../engine.cpp"
#include "../moves.cpp"
#include "../names.cpp"
#include "../xfer.cpp"

namespace kitems {
void items_log(int level, const char* fmt, ...) {
    char b[512];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    std::printf("  log[%d] %s\n", level, b);
}
const KilnApi* items_api() { return nullptr; }
// engine.cpp's give path calls these; this test never gives, and pide.cpp has its own test (pide_test.cpp)
void pide_commit(void* tx, void(KILN_CALL* commit)(void*), uint32_t, uint8_t, uint32_t) { commit(tx); }
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
void* __fastcall fake_item_find(uint32_t id, void*) { return id == WOOD || id == IRON || id == SWORD ? g_item_bytes[id % 8] : nullptr; }

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
SafetyHookInline* fail_second_hook(void* target, void* replacement) {
    return ++hook_factory_calls == 2 ? new SafetyHookInline() : make_hook(target, replacement);
}

void partial_install_test() {
    check(!install_locked(&fail_second_hook), "partial install: a failed second hook leaves the feature unavailable");
    auto* qs = h_qs.load();
    check(qs != nullptr && static_cast<bool>(*qs), "partial install: the quick-stack hook keeps its trampoline");
    // Do not call through a missing trampoline on the unfixed build: the assertion above exposes the actual bug.
    if (qs) {
        fill_for_transfer();
        FakeTx tx;
        Result12 out[3] = {};
        g_vanilla_calls = 0;
        call_qs(B, A, tx, out);
        check(g_vanilla_calls == 1 && out[0].value == 18,
              "partial install: the installed hook passes through safely with no registrations");
    }
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
        const bool ok = run_move(access, job, reinterpret_cast<uint8_t*>(&tx), &moved, &code);
        xfer_deliver();
        check(ok && moved.done == 9 && total(WOOD, {A, A2}) == 9 && total(WOOD, {B, B2}) == 2,
              "review: dry-run uses the real move path and rolls its inventory writes back");
        check(g_reports.empty(), kind == KILN_MOVE_QUICK_STACK
            ? "review: dry-run quick stack emits no committed transfer report"
            : "review: dry-run move all emits no committed transfer report");
    }
    xfer_end_owner(95);
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

int main(int argc, char** argv) {
    setup_engine();
    const std::string mode = argc > 1 ? argv[1] : "all";
    if (mode == "review") {
        review_tests();
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
    logic_tests();
    setup_engine();
    engine_tests();
    setup_engine();
    hide_fault_test();
    setup_engine();
    hook_tests();
    std::printf("items move test: %d failure(s)\n", failures);
    return failures ? EXIT_FAILURE : EXIT_SUCCESS;
}
