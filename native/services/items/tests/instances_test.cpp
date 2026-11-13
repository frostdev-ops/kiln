// SPDX-License-Identifier: MIT
// kiln.items v4 host test (no game; runs under plain Wine): the stamp table (stamps.cpp) and the item-instance reads
// (instances.cpp) against a fake component world. What it proves is the service's own logic: an id never shows an old
// stamp once kiln.items can know the entity behind it changed (a take of its own, a world reset, the id counter going
// down, another item id), the save record's flat-slot numbering, the holder map, the record fields read as the game's
// record builder reads them. The one case only the engine can rule out (the game frees an id and gives it to another
// item entity of the same item, unseen, in the same world) is shown as what the table would do: the engine never hands
// an id out twice in a world (docs/kiln/services/items.md "Item instances"), and the lab test mods/tests/instances-test
// checks it live.
#include <windows.h>

#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <set>
#include <thread>
#include <vector>

#include "../stamps.cpp"
#include "../instances.cpp"

namespace kitems {
Engine E;
void gate_features() {}
void items_log(int level, const char* fmt, ...) {
    char b[512];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    std::printf("  log[%d] %s\n", level, b);
}
uint64_t g_test_step = 100;
uint64_t current_step() { return g_test_step; }
}  // namespace kitems

using namespace kitems;

namespace {

int failures = 0;
void check(bool ok, const char* text) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", text);
    failures += !ok;
}

// ---------------------------------------------------------------------------------------------- the fake world ----
constexpr uint32_t SWORD = 0x1111, AXE = 0x2222, GEM = 0x3333, DYE = 0x4444, CAN = 0x5555;
constexpr uint32_t A_DUR = 0xed71dc01u, A_DURMAX = 0x3c8a292fu, A_FUEL = 0x05b0bf75u, A_FUELMAX = 0xa00e2601u,
                   A_LEVEL = 0x724bf568u;
constexpr uint32_t G_DUR = 0xD0D0, G_FUEL = 0xF0F0, G_LEVEL = 0x1E1E;  // attribute groups (a block's first u32)

std::map<std::pair<uint32_t, uint32_t>, std::vector<uint8_t>> g_comps;  // (hash, entity) -> bytes
std::vector<uint32_t> g_holders;
alignas(16) uint8_t g_world_bytes[0x600];
uint8_t g_desc[5][16];

std::vector<uint8_t>& comp_of(uint32_t hash, uint32_t e, size_t size) {
    auto& v = g_comps[{hash, e}];
    if (v.size() < size) v.resize(size, 0);
    return v;
}
template <typename T>
void put(std::vector<uint8_t>& v, size_t off, T x) { std::memcpy(v.data() + off, &x, sizeof x); }

PtrSize* __fastcall fake_get_component(PtrSize* out, uintptr_t, uint32_t hash, uint32_t e) {
    auto it = g_comps.find({hash, e});
    out->ptr = it == g_comps.end() ? nullptr : it->second.data();
    out->size = it == g_comps.end() ? 0 : it->second.size();
    return out;
}
AttrRef* __fastcall fake_attr_find(AttrRef* out, uint32_t attr, void*) {
    std::memset(out, 0, sizeof *out);
    const uint32_t ids[5] = {A_DUR, A_DURMAX, A_FUEL, A_FUELMAX, A_LEVEL};
    for (int i = 0; i < 5; ++i)
        if (ids[i] == attr) {
            out->desc = g_desc[i];
            return out;
        }
    out->error = 0x16;
    return out;
}
bool fake_list(std::vector<KilnEntity>* out) {
    *out = g_holders;
    return true;
}

void setup_engine() {
    E.get_component = &fake_get_component;
    E.attr_find = &fake_attr_find;
    E.setup_initialized = 0x53;
    E.world_storage = 0;
    E.storage_next_id = 0x498;
    E.attr_durability = A_DUR;
    E.attr_durability_max = A_DURMAX;
    E.attr_fuel = A_FUEL;
    E.attr_fuel_max = A_FUELMAX;
    E.attr_level = A_LEVEL;
    E.instances_ok = true;
    const uint32_t groups[5] = {G_DUR, G_DUR, G_FUEL, G_FUEL, G_LEVEL};
    const uint16_t index[5] = {0, 1, 0, 1, 0};
    for (int i = 0; i < 5; ++i) {
        std::memset(g_desc[i], 0, 16);
        std::memcpy(g_desc[i], &groups[i], 4);
        std::memcpy(g_desc[i] + 0xC, &index[i], 2);
    }
}

void set_counter(uint32_t next) { std::memcpy(g_world_bytes + 0x498, &next, 4); }

// An InventorySetup on `holder`: links {entity, category}.
void setup(uint32_t holder, std::initializer_list<std::pair<uint32_t, uint8_t>> links, bool initialized = true) {
    auto& s = comp_of(kHashInventorySetup, holder, kSetupSize);
    std::memset(s.data(), 0, s.size());
    int i = 0;
    for (auto [e, cat] : links) {
        put<uint32_t>(s, 4 * i, e);
        s[0x40 + i] = cat;
        if (cat != 5) comp_of(kHashInventory, e, kInventorySize);
        ++i;
    }
    s[0x53] = initialized ? 1 : 0;
}

void stack(uint32_t inv, uint32_t k, uint32_t item, uint32_t count, uint32_t pide) {
    auto& v = comp_of(kHashInventory, inv, kInventorySize);
    put<uint32_t>(v, 12 * k, item);
    put<uint32_t>(v, 12 * k + 4, count);
    put<uint32_t>(v, 12 * k + 8, pide);
}

// An item entity `e` of `item` whose ItemState says it sits in inv:index (and, by default, that slot holds it).
void item(uint32_t e, uint32_t it, uint32_t inv, uint8_t index, bool in_slot = true) {
    auto& s = comp_of(kHashItemState, e, kItemStateSize);
    std::memset(s.data(), 0, s.size());
    put<uint32_t>(s, 0, inv);
    s[4] = index;
    put<uint32_t>(s, 8, it);
    put<uint16_t>(s, 0xE, 0x0008);  // IsEquipped, to see the flag
    if (in_slot) stack(inv, index, it, 1, e);
}

void attr_block(uint32_t hash, uint32_t e, uint32_t group, std::initializer_list<int32_t> values, bool has_values = true) {
    auto& v = comp_of(hash, e, 8 + 4 * values.size());
    put<uint32_t>(v, 0, group);
    put<uint16_t>(v, 4, 8);
    v[6] = has_values ? 1 : 0;
    v[7] = static_cast<uint8_t>(values.size());
    int i = 0;
    for (int32_t x : values) put<int32_t>(v, 8 + 4 * i++, x);
}

InstanceEnv env(uint64_t step) {
    InstanceEnv e;
    e.world = reinterpret_cast<uintptr_t>(g_world_bytes);
    e.attributes = g_desc;
    e.step = step;
    e.list = &fake_list;
    return e;
}

KilnItemsInstance read1(uint32_t e, uint64_t step = 100, uint32_t flags = 0) {
    KilnItemsInstance it{};
    it.entity = e;
    instances_read(env(step), &it, 1, sizeof it, flags);
    return it;
}

void reset_all() {
    g_comps.clear();
    g_holders.clear();
    std::memset(g_world_bytes, 0, sizeof g_world_bytes);
    instances_world_reset();
}

// ---------------------------------------------------------------------------------------------- the table ----
void table_tests() {
    StampTable t;
    const uint32_t a = t.stamp(5000, SWORD, 1), b = t.stamp(5001, SWORD, 1);
    check(a != 0 && b != 0 && a != b, "two item entities get two stamps, neither 0");
    check(t.stamp(5000, SWORD, 2) == a && t.peek(5000) == a, "the same item entity keeps its stamp");
    check(t.stamp(5000, 0, 2) == a, "a caller that does not know the item gets the same stamp");
    check(t.stamp(1, SWORD, 1) == 0 && t.stamp(1023, SWORD, 1) == 0 && t.stamp(0x80000001u, SWORD, 1) == 0 && t.stamp(0, SWORD, 1) == 0,
          "ids outside the engine's counter range (players 1..1023, bit 31, 0) are never stamped");
    const uint32_t d = t.destroyed(5000, SWORD, 3);
    check(d == a && t.gone(5000), "a take's commit reports the destroyed item entity's own stamp and leaves a tombstone");
    check(t.stamp(5000, SWORD, 4) == 0 && t.peek(5000) == 0, "a tombstoned id is never stamped again in this world");
    check(t.destroyed(5000, SWORD, 4) == a, "reported destroyed twice: the same stamp");
    const uint32_t e = t.destroyed(5002, AXE, 4);
    check(e != 0 && e != a && e != b && t.gone(5002), "an item entity destroyed before anything read it gets a stamp of its own");
    t.reset();
    const uint32_t a2 = t.stamp(5000, SWORD, 5);
    check(a2 != 0 && a2 != a && !t.gone(5000), "after a world reset the same id is a new item entity with a new stamp");
    check(t.stamp(5001, SWORD, 5) != b, "...every old stamp is forgotten");
    const uint64_t mm = t.mismatches();
    const uint32_t a3 = t.stamp(5000, AXE, 6);
    check(a3 != 0 && a3 != a2 && t.mismatches() == mm + 1, "an id that now holds another item is another item entity: a new stamp");
    // the id counter
    StampTable c;
    const uint32_t c1 = c.stamp(6000, SWORD, 1);
    check(!c.observe_counter(7000) && c.stamp(6000, SWORD, 1) == c1, "the first counter seen resets nothing");
    check(!c.observe_counter(7005) && c.stamp(6000, SWORD, 1) == c1, "a counter that grows resets nothing");
    check(c.observe_counter(1100) && c.stamp(6000, SWORD, 1) != c1, "a counter that went down (a new id space) forgets every stamp");
    c.reset();
    check(!c.observe_counter(1024), "after a world reset the next world's low counter is not a second reset");
    // the sequence never repeats a stamp, and stops instead of wrapping
    StampTable x;
    x.set_next_for_tests(0xFFFFFFFEu);
    const uint32_t x1 = x.stamp(7000, SWORD, 1), x2 = x.stamp(7001, SWORD, 1), x3 = x.stamp(7002, SWORD, 1);
    check(x1 == 0xFFFFFFFEu && x2 == 0xFFFFFFFFu && x3 == 0 && x.exhausted() && x.stamp(7000, SWORD, 1) == x1,
          "the sequence ends at 2^32 - 1 instead of wrapping: no stamp is handed out twice");
    // pruning
    StampTable p;
    const uint32_t live = p.stamp(8000, SWORD, 10), dead = p.stamp(8001, SWORD, 10);
    p.stamp(8002, SWORD, 99);  // young and not in the world yet (a create the engine has queued)
    const size_t dropped = p.prune(100, 4, [](uint32_t e) { return e == 8000; });
    check(dropped == 1 && p.peek(8000) == live && p.peek(8001) == 0 && p.peek(8002) != 0,
          "prune drops old entries of gone entities only; a young one waits for the engine to make its entity");
    check(p.stamp(8001, SWORD, 101) != dead, "(a pruned id that came back would get a new stamp)");
    // two threads: one id, one stamp
    StampTable m;
    std::vector<uint32_t> r1(2000), r2(2000);
    std::thread t1([&] { for (uint32_t i = 0; i < 2000; ++i) r1[i] = m.stamp(10000 + i, SWORD, 1); });
    std::thread t2([&] { for (uint32_t i = 0; i < 2000; ++i) r2[i] = m.stamp(10000 + (1999 - i), SWORD, 1); });
    t1.join();
    t2.join();
    bool same = true;
    std::set<uint32_t> all;
    for (uint32_t i = 0; i < 2000; ++i) {
        same = same && r1[i] == r2[1999 - i] && r1[i] != 0;
        all.insert(r1[i]);
    }
    check(same && all.size() == 2000 && m.issued() == 2000, "two threads stamping the same ids: one stamp per id, 2000 distinct");
}

// ---------------------------------------------------------------------------------------------- the reads ----
void read_tests() {
    reset_all();
    setup_engine();
    // A player-like holder: link 0 Equipment, links 1 and 2 Generic, link 3 Virtual (a nested setup with one Generic).
    constexpr uint32_t H = 3000, INV1 = 3001, INV2 = 3002, V = 3003, INV3 = 3004;
    setup(H, {{H, 2}, {INV1, 4}, {INV2, 4}, {V, 5}});
    setup(V, {{INV3, 4}});
    constexpr uint32_t C = 4000, C2 = 4001, C3 = 4002;  // a chest: three Generic links, the first is the chest itself
    setup(C, {{C, 4}, {C2, 4}, {C3, 4}});
    g_holders = {H, C};
    item(5000, SWORD, INV1, 3);
    item(5001, AXE, INV2, 0);
    item(5002, SWORD, H, 1);
    item(5003, CAN, INV3, 2);
    item(5004, SWORD, C2, 5);
    const KilnItemsInstance s1 = read1(5000), s2 = read1(5001), s3 = read1(5002), s4 = read1(5003), s5 = read1(5004);
    check((s1.flags & KILN_ITEMS_INSTANCE_FOUND) && (s1.flags & KILN_ITEMS_INSTANCE_PLACED) && s1.holder == H && s1.link == 1 &&
              s1.category == 4 && s1.flat_slot == 3 && (s1.flags & KILN_ITEMS_INSTANCE_SAVED_SLOT) && s1.count == 1 &&
              s1.slot == KILN_SLOT_ID(INV1, 3) && s1.item == SWORD && s1.stamp != 0,
          "an item in the first Generic link (after an Equipment link) has the save record's slot 0 x 8 + 3");
    check(s2.flat_slot == 8 + 0 && (s2.flags & KILN_ITEMS_INSTANCE_SAVED_SLOT) && s2.link == 2,
          "the second Generic link's slots start at 8 (only Generic links are numbered, as the savers do)");
    check(s3.flat_slot == 256 + 0 * 8 + 1 && !(s3.flags & KILN_ITEMS_INSTANCE_SAVED_SLOT) && s3.category == 2 && s3.holder == H,
          "an equipment slot is placed, with a unique number the game's save does not use (256 + position x 8 + index)");
    check(s4.holder == H && s4.link == 3 && !(s4.flags & KILN_ITEMS_INSTANCE_SAVED_SLOT) && s4.flat_slot == 256 + 3 * 8 + 2,
          "a nested (Virtual) inventory's item is placed under the outer holder and link, not as a saved slot");
    check(s5.holder == C && s5.flat_slot == 13 && s5.link == 1, "a chest: link 1 index 5 is the save record's slot 13");
    check(s1.stamp != s2.stamp && s2.stamp != s3.stamp && (s1.flags & KILN_ITEMS_INSTANCE_EQUIPPED), "every item entity has its own stamp");
    check(read1(5000).stamp == s1.stamp, "reading it again gives the same stamp");
    // not placed: ItemState points at a slot that does not hold it
    item(5005, SWORD, C3, 7, false);
    const KilnItemsInstance np = read1(5005);
    check((np.flags & KILN_ITEMS_INSTANCE_FOUND) && !(np.flags & KILN_ITEMS_INSTANCE_PLACED) && np.holder == 0 && np.count == 0 &&
              np.slot == KILN_SLOT_ID(C3, 7),
          "an item entity whose slot does not hold it is found but not placed");
    check(!(read1(4000).flags & KILN_ITEMS_INSTANCE_FOUND) && !(read1(999999).flags & KILN_ITEMS_INSTANCE_FOUND),
          "an entity without ItemState, or no entity at all, is not found");
    check(read1(5).flags == 0, "a player id is never read as an item entity");
    // record fields, as the record builder reads them
    attr_block(kHashDurability, 5000, G_DUR, {37, 120});
    attr_block(kHashFuel, 5003, G_FUEL, {1088, 1280});
    attr_block(kHashLevel, 5000, G_LEVEL, {0x104});  // the record keeps the low byte (4)
    auto& dye = comp_of(kHashDye, 5000, 4);
    put<uint32_t>(dye, 0, DYE);
    auto& pn = comp_of(kHashPerkNew, 5000, 2);
    pn[0] = 3;
    pn[1] = 1;
    auto& gem = comp_of(kHashGemSlot, 5000, 12);
    put<uint32_t>(gem, 0, GEM);
    put<uint32_t>(gem, 8, 5100);
    attr_block(kHashLevel, 5100, G_LEVEL, {2});
    auto& po = comp_of(kHashPerkOld, 5001, 25);
    put<uint32_t>(po, 0, 9);
    put<uint32_t>(po, 4, 9);
    put<uint32_t>(po, 12, 9);  // after an empty perk: not counted
    po[0x14] = 1;
    attr_block(kHashDurability, 5002, G_DUR, {5, 10}, false);  // a block without values: no durability in the record
    attr_block(kHashFuel, 5004, 0xBAD, {1, 2});                 // a block of another group: not Fuel's values
    const KilnItemsInstance r1 = read1(5000), r2 = read1(5001), r3 = read1(5002), r4 = read1(5003), r5 = read1(5004);
    check((r1.flags & KILN_ITEMS_INSTANCE_DURABILITY) && r1.durability == 37 && r1.durability_max == 120,
          "durability current / max through the attribute registry");
    check((r1.flags & KILN_ITEMS_INSTANCE_LEVEL) && r1.level == 0x104 && (r1.flags & KILN_ITEMS_INSTANCE_DYE) && r1.dye == DYE,
          "level (whole value; the record keeps the low byte) and dye");
    check((r1.flags & KILN_ITEMS_INSTANCE_PERKS) && r1.perks_available == 3 && r1.perks_unlocked == 1, "PerkContainerNew");
    check((r1.flags & KILN_ITEMS_INSTANCE_GEM) && r1.gem == GEM && r1.gem_level == 2, "the gem and the gem's own level");
    check((r2.flags & KILN_ITEMS_INSTANCE_PERKS) && r2.perks_available == 2 && r2.perks_unlocked == 1,
          "the older PerkContainer: counted up to the first empty perk, as the game does");
    check(!(r3.flags & KILN_ITEMS_INSTANCE_DURABILITY) && !(r5.flags & KILN_ITEMS_INSTANCE_FUEL),
          "a block without values, or of another attribute group, gives no field (as the record builder)");
    check((r4.flags & KILN_ITEMS_INSTANCE_FUEL) && r4.fuel == 1088 && r4.fuel_max == 1280, "fuel current / max");
    const KilnItemsInstance loc = read1(5000, 100, KILN_ITEMS_READ_LOCATION);
    check((loc.flags & KILN_ITEMS_INSTANCE_PLACED) && loc.stamp == r1.stamp && !(loc.flags & KILN_ITEMS_INSTANCE_DURABILITY) &&
              loc.durability == 0 && loc.gem == 0,
          "a location read skips the record fields, same stamp");
    // a batch, with a stride larger than the struct
    struct Wide {
        KilnItemsInstance it;
        uint32_t extra;
    } w[3]{};
    w[0].it.entity = 5000;
    w[1].it.entity = 77;
    w[2].it.entity = 5004;
    w[0].extra = w[1].extra = w[2].extra = 0xC0FFEE;
    instances_read(env(100), &w[0].it, 3, sizeof(Wide), 0);
    check(w[0].it.stamp == r1.stamp && w[1].it.flags == 0 && w[2].it.stamp == r5.stamp && w[1].extra == 0xC0FFEE,
          "a batch with the caller's stride: each item read, bytes beyond the struct left alone");
    // the holder read
    KilnItemsInstance out[8];
    uint32_t n = 0;
    uint8_t init = 0;
    KilnResult rc = instances_read_holder(env(100), H, out, 8, sizeof out[0], &n, &init);
    check(rc == KILN_OK && n == 4 && init == 1 && out[0].entity == 5000 && out[1].entity == 5001 && out[2].entity == 5002 &&
              out[3].entity == 5003 && out[0].stamp == r1.stamp,
          "every item entity of a holder, saved slots first, in flat-slot order, with the same stamps");
    rc = instances_read_holder(env(100), H, out, 2, sizeof out[0], &n, &init);
    check(rc == KILN_ERR_BUFFER_TOO_SMALL && n == 4, "a small buffer: the first ones and the full count");
    check(instances_read_holder(env(100), 4242, out, 8, sizeof out[0], &n, &init) == KILN_ERR_NOT_FOUND, "no InventorySetup: not found");
    setup(4100, {{4100, 4}}, false);
    check(instances_read_holder(env(100), 4100, out, 8, sizeof out[0], &n, &init) == KILN_OK && n == 0 && init == 0,
          "an uninitialized inventory: empty, initialized 0");
    // the holder map follows a re-link (in the next step: one rebuild per step at most)
    constexpr uint32_t C4 = 4003;
    setup(C, {{C, 4}, {C2, 4}, {C3, 4}, {C4, 4}});
    item(5006, AXE, C4, 1);
    const KilnItemsInstance nl = read1(5006, 101);
    check(nl.holder == C && nl.flat_slot == 3 * 8 + 1, "an inventory not in the map yet: the map is rebuilt and finds its holder");
    setup(C, {{C, 4}, {C3, 4}});  // C2 moved away (another holder links it now)
    setup(4500, {{C2, 4}});
    g_holders = {H, C, 4500};
    const KilnItemsInstance moved_link = read1(5004, 102);
    check(moved_link.holder == 4500 && moved_link.flat_slot == 5, "a stale map entry is caught by the holder's setup, and the item is placed under its new holder");
}

// ---------------------------------------------------------------------------------------------- id reuse ----
void reuse_tests() {
    reset_all();
    setup_engine();
    constexpr uint32_t C = 4000;
    setup(C, {{C, 4}});
    g_holders = {C};
    set_counter(6000);
    instances_pre(reinterpret_cast<uintptr_t>(g_world_bytes), 200);
    item(5500, SWORD, C, 0);
    const uint32_t old = read1(5500, 200).stamp;
    // 1. kiln.items takes it (the commit destroys it), and the id names a new item entity in the same step
    check(stamp_destroyed(5500, SWORD) == old, "a take reports the item entity's stamp");
    item(5500, SWORD, C, 0);  // (a new item entity behind the same id, which the engine never does)
    const KilnItemsInstance after_take = read1(5500, 200);
    check(!(after_take.flags & KILN_ITEMS_INSTANCE_FOUND) && (after_take.flags & KILN_ITEMS_INSTANCE_GONE) && after_take.stamp == 0,
          "after a take: the id is gone, in the same step and later, never the old stamp");
    check(!(read1(5500, 201).flags & KILN_ITEMS_INSTANCE_FOUND), "...also in the next step");
    // 2. the id holds another item: another item entity
    item(5501, SWORD, C, 1);
    const uint32_t s2 = read1(5501, 202).stamp;
    item(5501, AXE, C, 1);
    const uint32_t s2b = read1(5501, 202).stamp;
    check(s2b != 0 && s2b != s2, "the id now holds another item id: a new stamp in the same step");
    // 3. the id counter went down (a new world without a world event): every stamp goes
    item(5502, SWORD, C, 2);
    const uint32_t s3 = read1(5502, 203).stamp;
    set_counter(1200);
    instances_pre(reinterpret_cast<uintptr_t>(g_world_bytes), 204);
    check(read1(5502, 204).stamp != s3, "the counter went down: the same id and item get a new stamp");
    // 4. the world closed / started
    const uint32_t s4 = read1(5502, 205).stamp;
    instances_world_reset();
    check(read1(5502, 206).stamp != s4, "a world reset: a new stamp");
    // 5. what the table cannot see by itself: freed by the game and reused for the same item, same world. The engine
    //    never does this (one counter that only grows); the table would give the old stamp. Shown, not hidden.
    const uint32_t s5 = read1(5502, 207).stamp;
    item(5502, SWORD, C, 2);  // "destroyed and recreated" without any signal
    check(read1(5502, 208).stamp == s5,
          "(known limit: an id freed and reused for the same item in one world, with no signal, would keep its stamp; "
          "the engine's counter rules it out, docs/kiln/services/items.md 'Item instances')");
    // the off switch
    E.instances_ok = false;
    check(stamp_for(5502, SWORD) == 0 && stamp_destroyed(5502, SWORD) == 0 && !instances_on(),
          "items.instances off: no stamp anywhere (0, as an older service)");
    E.instances_ok = true;
    instances_pre(reinterpret_cast<uintptr_t>(g_world_bytes), 209);  // the counter seen again after the reset
    KilnItemsInstancesStatus st{};
    st.size = sizeof st;
    instances_status(&st);
    check((st.flags & KILN_ITEMS_INSTANCES_ON) && st.stamps_issued >= 6 && st.resets >= 2 && st.next_entity_id == 1200 &&
              st.mismatches >= 1,
          "the status counts stamps, resets, mismatches and shows the counter");
}

}  // namespace

int main() {
    static_assert(sizeof(KilnItemsInstance) == 88 && offsetof(KilnItemsApi, read_instances) == 200 &&
                      offsetof(KilnItemsApi, equip) == 224 && sizeof(KilnItemsApi) == 240,
                  "v4 appends after peek_result, v5 (equip) after v4; the instance record is 88 bytes");
    setup_engine();
    table_tests();
    read_tests();
    reuse_tests();
    std::printf("items instances test: %d failure(s)\n", failures);
    return failures ? EXIT_FAILURE : EXIT_SUCCESS;
}
