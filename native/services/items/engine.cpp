// SPDX-License-Identifier: MIT
// kiln.items: calls into the game's inventory code. Plain data only (no destructors), so every entry point can wrap
// its body in __try. Layouts: docs/re/items.md (svn 1285601, cross-checked against EMF 0.3.3's notes for 1269579 /
// 1273043, MIT).
#include "engine.h"
#include "pide.h"

#include <windows.h>

#include <cstdio>
#include <cstring>

namespace kitems {

Engine E;

const char* const kAccNames[A_COUNT] = {"Fuel",     "Inventory",         "InventoryEmptyState", "InventorySetup",
                                        "ItemState", "OwnerRelationship", "SlotSelection"};
const uint32_t kAccHashes[A_COUNT] = {0x04182abbu, 0xb19528a9u, 0x37c4cfe7u, 0xc24df82au,
                                      0x3a2fac75u, 0xd7361788u, 0x34bc3e2fu};

namespace {

template <typename T>
T rd(const void* p, int64_t off) {
    T v;
    std::memcpy(&v, static_cast<const uint8_t*>(p) + off, sizeof v);
    return v;
}

void say(char* why, const char* text) {
    if (!why) return;
    std::snprintf(why, 160, "%s", text);
}

// ---- inventory trees ----
// The 8-slot inventories under an InventorySetup: links in order until entity 0; Virtual (5) links are expanded.
struct Tree {
    KilnEntity ids[KILN_ITEMS_MAX_INVENTORIES];
    uint8_t cats[KILN_ITEMS_MAX_INVENTORIES];
    uint8_t links[KILN_ITEMS_MAX_INVENTORIES];
    uint32_t n;
};

using SetupOf = const uint8_t* (*)(const void* ctx, KilnEntity e);

void walk(const uint8_t* setup, SetupOf get, const void* ctx, Tree* t, int depth, int outer_link) {
    for (int k = 0; k < 16; ++k) {
        const KilnEntity id = rd<uint32_t>(setup, 4 * k);
        if (id == 0) break;
        const uint8_t cat = setup[0x40 + k];
        const int link = outer_link >= 0 ? outer_link : k;
        if (cat == 5) {  // Virtual: another InventorySetup
            if (depth < 3) {
                if (const uint8_t* inner = get(ctx, id)) walk(inner, get, ctx, t, depth + 1, link);
            }
            continue;
        }
        bool dup = false;
        for (uint32_t j = 0; j < t->n; ++j) dup = dup || t->ids[j] == id;
        if (dup || t->n >= KILN_ITEMS_MAX_INVENTORIES) continue;
        t->ids[t->n] = id;
        t->cats[t->n] = cat;
        t->links[t->n] = static_cast<uint8_t>(link);
        ++t->n;
    }
}

// ---- inside the program: other-entity accessors ----
void* other(const Access& a, Acc which, KilnEntity e) {
    PtrSize ps{};
    E.read_other(&ps, a.storage, a.acc[which], e);
    return ps.ptr;
}

const uint8_t* setup_via_access(const void* ctx, KilnEntity e) {
    return static_cast<const uint8_t*>(other(*static_cast<const Access*>(ctx), A_SETUP, e));
}

// ---- on the tick thread: GetComponent ----
struct WorldCtx {
    uintptr_t world;
};

const void* component(uintptr_t world, uint32_t hash, KilnEntity e, uint64_t min_size) {
    PtrSize ps{};
    E.get_component(&ps, world, hash, e);
    if (!ps.ptr || ps.size < min_size) return nullptr;
    return ps.ptr;
}

const uint8_t* setup_via_world(const void* ctx, KilnEntity e) {
    return static_cast<const uint8_t*>(component(static_cast<const WorldCtx*>(ctx)->world, kHashInventorySetup, e,
                                                 kSetupSize));
}

// ---- program iterator checks ----
bool verify_impl(const uint64_t* it, const int* sing, const int* accs, const int* evs, void* ctx, Access* a, bool* ok,
                 char* why) {
    *ok = false;
    a->ctx = ctx;
    a->job = *static_cast<void**>(ctx);
    if (!a->job) return say(why, "program context without a job"), true;
    a->storage = E.job_storage(a->job);
    if (!a->storage) return say(why, "the job has no entity storage"), true;
    if (rd<uint8_t>(a->storage, 8) != 0) return say(why, "the program ran in a world that is not the server world"), true;
    a->balancing = reinterpret_cast<void*>(it[sing[0]]);
    a->items = reinterpret_cast<void*>(it[sing[1]]);
    a->attributes = reinterpret_cast<void*>(it[sing[2]]);
    a->knowledge = reinterpret_cast<void*>(it[sing[3]]);
    if (!a->balancing || !a->items || !a->attributes)
        return say(why, "a singleton slot is empty (BalancingRegistry / ItemRegistry / AttributeRegistry)"), true;
    // The item registry slot must hold an item registry: a sane HashMap at registry_items.
    const uint8_t* map = static_cast<const uint8_t*>(a->items) + E.registry_items;
    const uint64_t cap = rd<uint64_t>(map, E.hmap_capacity);
    const uint32_t cnt = rd<uint32_t>(map, E.hmap_count);
    if ((cap & (cap - 1)) != 0 || cnt > cap || cap > (1u << 24))
        return say(why, "the item-registry slot does not hold an item registry (iterator layout?)"), true;
    // Accessors: each is a pointer to the component's u16 type index; match them to names through the world's
    // component map, so the iterator order does not matter.
    void* registry = *static_cast<void**>(a->storage);
    if (!registry) return say(why, "no ECS registry"), true;
    uint16_t want[A_COUNT];
    for (int i = 0; i < A_COUNT; ++i) {
        const uint32_t h = kAccHashes[i];
        const uint16_t* p = E.component_map_find(static_cast<uint8_t*>(registry) + E.registry_components, &h);
        if (!p) {
            char b[160];
            std::snprintf(b, sizeof b, "component %s is not in this world", kAccNames[i]);
            return say(why, b), true;
        }
        want[i] = *p;
        a->acc[i] = 0;
    }
    for (int s = 0; s < A_COUNT; ++s) {
        const uint64_t accessor = it[accs[s]];
        if (!accessor) return say(why, "an accessor slot is empty"), true;
        const uint16_t idx = *reinterpret_cast<const uint16_t*>(accessor);
        int hit = -1;
        for (int i = 0; i < A_COUNT; ++i)
            if (want[i] == idx) hit = i;
        if (hit < 0 || a->acc[hit] != 0) {
            char b[160];
            std::snprintf(b, sizeof b, "accessor slot %d has type index %u, which is none of the declared components",
                          accs[s], idx);
            return say(why, b), true;
        }
        a->acc[hit] = accessor;
    }
    a->ev_pickup = reinterpret_cast<void*>(it[evs[0]]);
    a->ev_full = reinterpret_cast<void*>(it[evs[1]]);
    if (a->ev_pickup != a->job || a->ev_full != a->job)
        return say(why, "the events-out slots do not hold the job (iterator layout?)"), true;
    *ok = true;
    return true;
}

// ---- transactions ----
void fill_setup(const Access& a, const KilnItemsRequest& r, uint8_t flags, uint8_t* s) {
    std::memset(s, 0, kSetupBlock);
    const bool player = r.entity >= 1 && r.entity <= kMaxPlayer;
    s[0x00] = flags;
    s[0x01] = 0;  // stock mode: own inventory only
    auto put = [s](int off, const void* v) { std::memcpy(s + off, &v, 8); };
    auto put_acc = [s](int off, uint64_t v) { std::memcpy(s + off, &v, 8); };
    put(0x08, a.ctx);
    put(0x10, a.items);
    put(0x18, a.balancing);   // gem-slot chances for new gear
    put(0x20, a.attributes);  // Fuel attribute
    put_acc(0x28, a.acc[A_SETUP]);
    put_acc(0x30, a.acc[A_INVENTORY]);
    put_acc(0x38, a.acc[A_SLOTSEL]);
    put_acc(0x40, a.acc[A_ITEMSTATE]);
    put_acc(0x48, a.acc[A_OWNER]);
    put_acc(0x50, a.acc[A_SETUP]);   // write accessor: clears links when a container item dies
    put_acc(0x58, a.acc[A_EMPTY]);   // required: commit rolls back without it
    put_acc(0x70, a.acc[A_FUEL]);
    put(0xA0, a.ev_pickup);
    put(0xA8, a.ev_full);
    put(0xC0, player ? a.knowledge : nullptr);  // "item obtained" knowledge only for players
    const uint32_t op = player ? r.entity : 0;
    std::memcpy(s + E.setup_operator, &op, 4);  // PickupEvent receiver; the source entity (+4) stays 0
}

struct Ops {
    const uint8_t* p;
    uint64_t n;
};
Ops ops_of(const uint8_t* tx) { return {rd<const uint8_t*>(tx, E.tx_ops), rd<uint64_t>(tx, E.tx_ops + 8)}; }

void add_change(KilnItemsResult* res, uint64_t slot, uint32_t item, uint32_t amount, uint32_t pide) {
    for (uint32_t i = 0; i < res->change_count; ++i) {
        KilnItemsChange& c = res->changes[i];
        if (c.slot == slot && c.item == item && (pide == 0 || c.pide == pide)) {
            c.amount += amount;
            return;
        }
    }
    if (res->change_count >= KILN_ITEMS_MAX_CHANGES) return;
    KilnItemsChange& c = res->changes[res->change_count++];
    c.slot = slot;
    c.item = item;
    c.amount = amount;
    c.pide = pide;
}

void fail(KilnItemsResult* res, int32_t status, const char* fmt, uint32_t a = 0, uint32_t b = 0) {
    res->status = status;
    std::snprintf(res->message, sizeof res->message, fmt, a, b);
}

// Is `slot` one of the 8-slot inventories under the owner's setup?
bool slot_in_tree(const Access& a, const uint8_t* setup, uint64_t slot) {
    if ((slot >> 40) != 0 || KILN_SLOT_INDEX(slot) > 7) return false;
    Tree t{};
    walk(setup, &setup_via_access, &a, &t, 0, -1);
    for (uint32_t i = 0; i < t.n; ++i)
        if (t.ids[i] == KILN_SLOT_INVENTORY(slot)) return true;
    return false;
}

const uint8_t* stack_at(const Access& a, uint64_t slot) {
    const auto* inv = static_cast<const uint8_t*>(other(a, A_INVENTORY, KILN_SLOT_INVENTORY(slot)));
    return inv ? inv + 12 * KILN_SLOT_INDEX(slot) : nullptr;
}

void run_give(const Access& a, const KilnItemsRequest& r, uint8_t* tx, KilnItemsResult* res) {
    const auto* setup = static_cast<const uint8_t*>(other(a, A_SETUP, r.entity));
    if (!setup) return fail(res, KILN_ITEMS_NO_INVENTORY, "entity %u has no inventory (no InventorySetup)", r.entity);
    if (!setup[E.setup_initialized])
        return fail(res, KILN_ITEMS_NO_INVENTORY, "entity %u's inventory is not initialized yet", r.entity);
    const auto* info = static_cast<const uint8_t*>(E.item_find(r.item, a.items));
    if (!info) return fail(res, KILN_ITEMS_UNKNOWN_ITEM, "item 0x%08x is not in the item registry", r.item);
    if (r.slot && !slot_in_tree(a, setup, r.slot))
        return fail(res, KILN_ITEMS_SLOT_CHANGED, "the slot (inventory %u) is not one of entity %u's slots",
                    KILN_SLOT_INVENTORY(r.slot), r.entity);
    const bool gear = (rd<uint32_t>(info, E.item_flags) & 2u) != 0;
    const bool player = r.entity >= 1 && r.entity <= kMaxPlayer;
    uint8_t flags = 0;
    if ((r.flags & KILN_ITEMS_NOTIFY) && player) flags |= 0x01;  // PickupEvent + "+N" popup
    if (r.flags & KILN_ITEMS_NO_MERGE) flags |= 0x80;
    alignas(16) uint8_t s[kSetupBlock];
    fill_setup(a, r, flags, s);
    E.tx_ctor(tx);
    E.tx_init(tx, s);
    OptU32 level{r.level, static_cast<uint8_t>((r.flags & KILN_ITEMS_LEVEL) ? 1 : 0), {}};
    OptU8 rarity{r.rarity, static_cast<uint8_t>((r.flags & KILN_ITEMS_RARITY) ? 1 : 0)};
    OptF32 fuel{0.0f, 0, {}};
    Result12 out{};
    E.add_items(&out, tx, r.entity, r.slot, r.item, r.count, &level, &rarity, &fuel);
    const Ops ops = ops_of(tx);
    uint32_t queued = 0;
    for (uint64_t i = 0; i < ops.n && ops.p; ++i) {
        const uint8_t* op = ops.p + i * kOpStride;
        if (op[0x26] != 0) continue;  // op 0 = stack put into slot
        const uint32_t amount = rd<uint16_t>(op, 0x24);
        queued += amount;
        add_change(res, rd<uint64_t>(op, 0), rd<uint32_t>(op, 8), amount, 0);
    }
    const uint32_t remaining = out.error ? r.count : (out.value > r.count ? r.count : out.value);
    res->done = r.count - remaining;
    res->game_error = out.error;
    const bool dry = (r.flags & KILN_ITEMS_DRY_RUN) != 0;
    const bool all = (r.flags & KILN_ITEMS_ALL_OR_NOTHING) != 0;
    const bool commit = out.error == 0 && res->done > 0 && !(all && remaining > 0) && !dry;
    if (commit) {
        pide_commit(tx, E.commit, r.flags, r.unlock, r.item); // createPide runs inside the commit
    } else {
        E.rollback(tx);
    }
    E.free_arrays(tx);
    if (out.error) {
        res->done = 0;
        res->change_count = 0;
        return fail(res, KILN_ITEMS_REFUSED, "the game refused it (error 0x%02x)", out.error);
    }
    if (queued != res->done) {  // the op list and the result disagree: report it, trust the game's count
        std::snprintf(res->message, sizeof res->message, "note: %u queued vs %u reported", queued, res->done);
    }
    if (commit && gear) {  // the game created each item entity at commit
        for (uint32_t i = 0; i < res->change_count; ++i)
            if (const uint8_t* st = stack_at(a, res->changes[i].slot)) res->changes[i].pide = rd<uint32_t>(st, 8);
    }
    if (res->done == 0) {
        res->change_count = 0;
        return fail(res, KILN_ITEMS_NO_ROOM, "no room for item 0x%08x in entity %u", r.item, r.entity);
    }
    if (remaining > 0 && all) {
        res->done = dry ? res->done : 0;
        if (!dry) res->change_count = 0;
        return fail(res, KILN_ITEMS_NO_ROOM, "only %u of %u fit (all or nothing: nothing given)", r.count - remaining,
                    r.count);
    }
    if (remaining > 0) return fail(res, KILN_ITEMS_PARTIAL, "only %u of %u fit", res->done, r.count);
    res->status = KILN_ITEMS_DONE;
}

// KILN_ITEMS_RECIPE: the game's own recipe result function, with the transaction flag that skips the inputs (0x10). It
// calls addItems per output with no level, rarity or fuel, as crafting does: docs/re/items.md section 10.
void run_recipe(const Access& a, const KilnItemsRequest& r, uint8_t* tx, KilnItemsResult* res) {
    if (!E.recipe_apply || !E.recipe_find || !a.recipes)
        return fail(res, KILN_ITEMS_UNAVAILABLE, "the game's recipe functions are not available on this build");
    const auto* setup = static_cast<const uint8_t*>(other(a, A_SETUP, r.entity));
    if (!setup) return fail(res, KILN_ITEMS_NO_INVENTORY, "entity %u has no inventory (no InventorySetup)", r.entity);
    if (!setup[E.setup_initialized])
        return fail(res, KILN_ITEMS_NO_INVENTORY, "entity %u's inventory is not initialized yet", r.entity);
    const auto* recipe = static_cast<const uint8_t*>(E.recipe_find(r.item, a.recipes));
    if (!recipe) return fail(res, KILN_ITEMS_UNKNOWN_ITEM, "recipe 0x%08x is not in the recipe registry", r.item);
    uint8_t flags = 0x10;  // no inputs
    if ((r.flags & KILN_ITEMS_NOTIFY) && r.entity >= 1 && r.entity <= kMaxPlayer) flags |= 0x01;
    if (r.flags & KILN_ITEMS_NO_MERGE) flags |= 0x80;
    alignas(16) uint8_t s[kSetupBlock];
    fill_setup(a, r, flags, s);
    E.tx_ctor(tx);
    E.tx_init(tx, s);
    const uint64_t no_inputs[2] = {};
    // The station's state, as the recipe wants it: {u32 level; bool has} x 3 (level, comfort, server progress), u16
    // happy NPCs, bool has. No station here, and these checks are not what this path is for.
    struct { uint32_t v; uint32_t has; } st3[3];
    alignas(8) uint8_t station[64] = {};
    for (int i = 0; i < 3; ++i) st3[i] = {rd<uint32_t>(recipe, 0x80 + 4 * i), 1};
    std::memcpy(station, st3, sizeof st3);
    const auto npcs = static_cast<uint16_t>(rd<uint32_t>(recipe, 0x8c));
    std::memcpy(station + 0x18, &npcs, 2);
    station[0x1a] = 1;
    const uint32_t rc = E.recipe_apply(tx, recipe, no_inputs, ~0ull, station,
                                       reinterpret_cast<const uint32_t*>(recipe + kRecipeWorkshopId), r.entity,
                                       static_cast<int32_t>(r.count));
    const Ops ops = ops_of(tx);
    for (uint64_t i = 0; i < ops.n && ops.p; ++i) {
        const uint8_t* op = ops.p + i * kOpStride;
        if (op[0x26] == 0) add_change(res, rd<uint64_t>(op, 0), rd<uint32_t>(op, 8), rd<uint16_t>(op, 0x24), 0);
    }
    res->game_error = rc;
    const bool dry = (r.flags & KILN_ITEMS_DRY_RUN) != 0;
    if (rc == 0 && !dry) E.commit(tx);
    else E.rollback(tx);
    E.free_arrays(tx);
    if (rc != 0) {
        res->change_count = 0;
        if (rc == 2) return fail(res, KILN_ITEMS_NO_ROOM, "no room for the outputs of recipe 0x%08x in entity %u", r.item, r.entity);
        return fail(res, KILN_ITEMS_REFUSED, "the game refused the recipe (%u)", rc);
    }
    res->done = r.count;
    if (!dry)
        for (uint32_t i = 0; i < res->change_count; ++i)
            if (const uint8_t* st = stack_at(a, res->changes[i].slot)) res->changes[i].pide = rd<uint32_t>(st, 8);
    res->status = KILN_ITEMS_DONE;
}

void run_take(const Access& a, const KilnItemsRequest& r, uint8_t* tx, KilnItemsResult* res) {
    const auto* setup = static_cast<const uint8_t*>(other(a, A_SETUP, r.entity));
    if (!setup) return fail(res, KILN_ITEMS_NO_INVENTORY, "entity %u has no inventory (no InventorySetup)", r.entity);
    const bool exact = (r.flags & KILN_ITEMS_EXACT_SLOT) != 0;
    if (r.slot) {
        if (!slot_in_tree(a, setup, r.slot))
            return fail(res, KILN_ITEMS_SLOT_CHANGED, "the slot is not one of entity %u's inventory slots", r.entity);
        if (exact) {
            const uint8_t* st = stack_at(a, r.slot);
            if (!st) return fail(res, KILN_ITEMS_SLOT_CHANGED, "inventory %u could not be read", KILN_SLOT_INVENTORY(r.slot));
            const uint32_t id = rd<uint32_t>(st, 0), cnt = rd<uint32_t>(st, 4), pide = rd<uint32_t>(st, 8);
            if (id != r.item || cnt == 0)
                return fail(res, KILN_ITEMS_SLOT_CHANGED, "the slot holds item 0x%08x, not 0x%08x", id, r.item);
            if (r.pide && pide != r.pide)
                return fail(res, KILN_ITEMS_SLOT_CHANGED, "the slot holds item entity %u, not %u", pide, r.pide);
            const uint32_t have = pide ? 1 : cnt;
            if (have < r.count) return fail(res, KILN_ITEMS_NOT_ENOUGH, "the slot holds %u, not %u", have, r.count);
        }
    }
    alignas(16) uint8_t s[kSetupBlock];
    fill_setup(a, r, 0, s);
    E.tx_ctor(tx);
    E.tx_init(tx, s);
    Result12 out{};
    E.remove_items(&out, tx, r.entity, r.item, r.slot, r.count);
    const Ops ops = ops_of(tx);
    bool off_slot = false;
    for (uint64_t i = 0; i < ops.n && ops.p; ++i) {
        const uint8_t* op = ops.p + i * kOpStride;
        if (op[0x26] != 1) continue;  // op 1 = take (pide destroyed when the stack empties)
        const uint64_t slot = rd<uint64_t>(op, 0);
        if (exact && slot != r.slot) off_slot = true;
        add_change(res, slot, rd<uint32_t>(op, 8), rd<uint16_t>(op, 0x24), op[0x30] ? rd<uint32_t>(op, 0x20) : 0);
    }
    const uint32_t missing = out.error ? r.count : (out.value > r.count ? r.count : out.value);
    res->game_error = out.error;
    const bool dry = (r.flags & KILN_ITEMS_DRY_RUN) != 0;
    const bool commit = out.error == 0 && missing == 0 && !off_slot && !dry;
    if (commit) E.commit(tx);
    else E.rollback(tx);
    E.free_arrays(tx);
    if (out.error) {
        res->change_count = 0;
        return fail(res, KILN_ITEMS_REFUSED, "the game refused it (error 0x%02x)", out.error);
    }
    if (off_slot) {
        res->change_count = 0;
        return fail(res, KILN_ITEMS_SLOT_CHANGED, "the game would have taken from other slots; nothing taken");
    }
    if (missing > 0) {
        res->change_count = 0;
        return fail(res, KILN_ITEMS_NOT_ENOUGH, "only %u of %u there; nothing taken", r.count - missing, r.count);
    }
    res->done = r.count;
    res->status = KILN_ITEMS_DONE;
}

// ---- reading ----
void read_impl(uintptr_t world, void* registry, KilnEntity e, ReadOut* out) {
    const WorldCtx wc{world};
    const auto* setup = setup_via_world(&wc, e);
    out->found = setup != nullptr;
    if (!setup) return;
    KilnItemsInventory& inf = out->info;
    inf.entity = e;
    inf.initialized = setup[E.setup_initialized];
    inf.setup_flags = setup[0x50];
    inf.generic_slots = setup[0x51];
    inf.available_slots = setup[0x52];
    Tree t{};
    walk(setup, &setup_via_world, &wc, &t, 0, -1);
    inf.inventory_count = t.n;
    inf.slot_total = 8 * t.n;
    for (uint32_t i = 0; i < t.n; ++i) {
        inf.inventories[i] = t.ids[i];
        inf.categories[i] = t.cats[i];
        const auto* inv = static_cast<const uint8_t*>(component(world, kHashInventory, t.ids[i], kInventorySize));
        for (uint32_t k = 0; k < 8; ++k) {
            KilnItemsSlot sl{};
            sl.slot = KILN_SLOT_ID(t.ids[i], k);
            sl.link = t.links[i];
            sl.category = t.cats[i];
            sl.rarity = 0xFF;
            if (inv) {
                const uint8_t* st = inv + 12 * k;
                sl.item = rd<uint32_t>(st, 0);
                sl.count = rd<uint32_t>(st, 4);
                sl.pide = rd<uint32_t>(st, 8);
                if (sl.pide == 0xFFFFFFFFu) sl.pide = 0;  // "no entity"
            }
            if (sl.item && sl.count) {
                ++inf.used_slots;
                if (registry) {
                    if (const auto* info = static_cast<const uint8_t*>(E.item_find(sl.item, registry))) {
                        sl.rarity = info[0x17];
                        if (rd<uint32_t>(info, E.item_flags) & 2u) sl.flags |= KILN_ITEMS_SLOT_GEAR;
                    }
                }
                if (sl.pide) {
                    sl.flags |= KILN_ITEMS_SLOT_GEAR;
                    if (const auto* is = static_cast<const uint8_t*>(component(world, kHashItemState, sl.pide,
                                                                                kItemStateSize))) {
                        sl.state = rd<uint16_t>(is, 0x0E);
                        sl.rarity = is[0x10];
                        sl.level = is[0x11];
                        if (sl.state & (1u << 3)) sl.flags |= KILN_ITEMS_SLOT_EQUIPPED;
                        if (sl.state & (1u << 0)) sl.flags |= KILN_ITEMS_SLOT_BROKEN;
                    }
                }
            }
            if (out->written < out->capacity) out->slots[out->written++] = sl;
        }
    }
}

bool matches(uintptr_t world, KilnEntity e, uint32_t flags) {
    const auto* setup = static_cast<const uint8_t*>(component(world, kHashInventorySetup, e, kSetupSize));
    if (!setup || !setup[E.setup_initialized]) return false;
    const uint8_t f = setup[0x50];
    const uint32_t kinds = flags & 7u;
    if (kinds) {
        const bool player = (f & 1u) != 0, npc = (f & 2u) != 0;
        const bool ok = ((flags & KILN_ITEMS_FIND_PLAYERS) && player) || ((flags & KILN_ITEMS_FIND_NPCS) && npc) ||
                        ((flags & KILN_ITEMS_FIND_CONTAINERS) && !player && !npc);
        if (!ok) return false;
    }
    if (flags & KILN_ITEMS_FIND_NONEMPTY) {
        const WorldCtx wc{world};
        Tree t{};
        walk(setup, &setup_via_world, &wc, &t, 0, -1);
        for (uint32_t i = 0; i < t.n; ++i) {
            const auto* inv = static_cast<const uint8_t*>(component(world, kHashInventory, t.ids[i], kInventorySize));
            if (!inv) continue;
            for (uint32_t k = 0; k < 8; ++k)
                if (rd<uint32_t>(inv, 12 * k) != 0 && rd<uint32_t>(inv, 12 * k + 4) != 0) return true;
        }
        return false;
    }
    return true;
}

void scan_impl(uintptr_t world, uint32_t flags, KilnEntity* ids, uint32_t capacity, uint32_t* total) {
    const uint8_t* map = reinterpret_cast<const uint8_t*>(world) + E.world_storage + E.storage_entities;
    const uint64_t cap = rd<uint64_t>(map, E.hmap_capacity);
    const uint32_t count = rd<uint32_t>(map, E.hmap_count);
    const auto* bits = rd<const uint64_t*>(map, E.hmap_bits);
    const auto* keys = rd<const uint32_t*>(map, E.hmap_keys);
    *total = 0;
    if (!bits || !keys || (cap & (cap - 1)) != 0 || count > cap || cap > (1ull << 26)) return;
    for (uint64_t k = 0; k < cap; ++k) {
        if (!((bits[k >> 6] >> (k & 63)) & 1)) continue;
        const KilnEntity e = keys[k];
        if (e == 0 || !matches(world, e, flags)) continue;
        if (*total < capacity) ids[*total] = e;
        ++*total;
    }
}

// ---- catalog ----
bool registry_map(void* registry, const uint8_t** map, uint64_t* cap) {
    *map = static_cast<const uint8_t*>(registry) + E.registry_items;
    *cap = rd<uint64_t>(*map, E.hmap_capacity);
    const uint32_t cnt = rd<uint32_t>(*map, E.hmap_count);
    return (*cap & (*cap - 1)) == 0 && cnt <= *cap && *cap <= (1u << 24);
}

void copy_impl(void* registry, RawItem* rows, uint32_t capacity, uint32_t* written) {
    const uint8_t* map;
    uint64_t cap;
    *written = 0;
    if (!registry_map(registry, &map, &cap)) return;
    const auto* bits = rd<const uint64_t*>(map, E.hmap_bits);
    const auto* keys = rd<const uint32_t*>(map, E.hmap_keys);
    const auto* vals = rd<const uint8_t* const*>(map, E.hmap_values);
    if (!bits || !keys || !vals) return;
    for (uint64_t k = 0; k < cap && *written < capacity; ++k) {
        if (!((bits[k >> 6] >> (k & 63)) & 1)) continue;
        const uint8_t* info = vals[k];
        if (!info) continue;
        RawItem& r = rows[*written];
        std::memset(&r, 0, sizeof r);
        r.id = rd<uint32_t>(info, 0);
        if (r.id != keys[k]) continue;  // map key and ItemInfo.itemId must agree
        std::memcpy(r.guid, info + 4, 16);
        r.max_stack = rd<uint16_t>(info, E.item_max_stack);
        r.rarity = info[0x17];
        r.category = info[0xCC];
        r.equip_slot = info[0x104];
        r.item_flags = rd<uint32_t>(info, E.item_flags);
        r.gear = (r.item_flags & 2u) ? 1 : 0;
        r.level_min = rd<uint32_t>(info, 0x674);
        r.level_max = rd<uint32_t>(info, 0x678);
        r.guid_id = E.guid_to_id ? E.guid_to_id(info + 4) : r.id;
        // debugName: BlobString {u32 offset from the field itself, u32 length}
        const uint32_t off = rd<uint32_t>(info, 0x820), len = rd<uint32_t>(info, 0x824);
        if (off != 0 && len > 0 && len < sizeof r.name) {
            const char* s = reinterpret_cast<const char*>(info + 0x820 + off);
            bool printable = true;
            for (uint32_t i = 0; i < len; ++i) printable = printable && s[i] > 0x20 && s[i] < 0x7F;
            if (printable) {
                std::memcpy(r.name, s, len);
                r.name_len = static_cast<uint16_t>(len);
            }
        }
        ++*written;
    }
}

// ---- own program registration ----
void register_impl(uintptr_t world, void* desc, int* outcome, char* why) {
    *outcome = 0;
    auto* w = reinterpret_cast<uint8_t*>(world);
    if (w[0] != 0) return say(why, "the world already finalized its programs");
    const uint8_t* storage = w + E.world_storage;
    if (storage[8] != 0) return say(why, "not the server world");
    const uint16_t count = rd<uint16_t>(w, E.program_count);
    if (count >= 0x380 - 1) return say(why, "the program table is full");
    void* registry = *reinterpret_cast<void* const*>(storage);
    if (!registry) return say(why, "no ECS registry");
    for (int i = 0; i < A_COUNT; ++i) {
        const uint32_t h = kAccHashes[i];
        if (!E.component_map_find(static_cast<uint8_t*>(registry) + E.registry_components, &h)) {
            char b[160];
            std::snprintf(b, sizeof b, "component %s is not in this world", kAccNames[i]);
            return say(why, b);
        }
    }
    const uint8_t ok = E.register_program(world, desc, 0);
    const uint16_t after = rd<uint16_t>(w, E.program_count);
    if (!(ok & 1) || after != count + 1) return say(why, "RegisterProgram refused it (duplicate name?)");
    *outcome = 1;
}


void fail_fuel(KilnItemsFuelResult* res, int32_t status, const char* fmt, uint32_t a = 0) {
    res->status = status;
    std::snprintf(res->message, sizeof res->message, fmt, a);
}

// ---- water: tracked Fuel writes (docs/re/items.md section 12) ----
// A Fuel component is an attribute block: {u32 group; u16 values offset; u8 flags (bit 0: has values); u8 count;
// s32 values[count]}. The attribute registry gives each attribute's index into the values.
struct FuelSlots {
    int32_t cur = -1, max = -1;  // value indices
};

bool fuel_slots(void* attributes, const uint8_t* comp, FuelSlots* fs) {
    if (!comp || !(comp[6] & 1)) return false;
    AttrRef r{};
    E.attr_find(&r, kAttrFuel, attributes);
    if (r.error || !r.desc || rd<uint32_t>(r.desc, 0) != rd<uint32_t>(comp, 0)) return false;
    fs->cur = rd<uint16_t>(r.desc, 0xC);
    AttrRef m{};
    E.attr_find(&m, kAttrFuelMax, attributes);
    if (m.error || !m.desc || rd<uint32_t>(m.desc, 0) != rd<uint32_t>(comp, 0)) return false;
    fs->max = rd<uint16_t>(m.desc, 0xC);
    const uint8_t count = comp[7];
    return fs->cur < count && fs->max < count;
}

int32_t* fuel_values(uint8_t* comp) { return reinterpret_cast<int32_t*>(comp + rd<uint16_t>(comp, 4)); }

int32_t fuel_target(const KilnItemsFuelRequest& r, int32_t cur, int32_t max) {
    const int32_t top = r.limit > 0 && r.limit < max ? r.limit : max;
    int64_t t = cur;
    if (r.flags & KILN_ITEMS_FUEL_FILL) t = top;
    else if (r.flags & KILN_ITEMS_FUEL_ADD) t = static_cast<int64_t>(cur) + r.units;
    else t = r.units;
    const bool raise = (r.flags & KILN_ITEMS_FUEL_FILL) || ((r.flags & KILN_ITEMS_FUEL_ADD) && r.units > 0);
    if (raise && cur >= top) return cur;  // already at or above the level: left as it is
    const int32_t upper = (r.flags & KILN_ITEMS_FUEL_ADD) && r.units < 0 ? max : top;
    if (t < 0) t = 0;
    if (t > upper) t = upper;
    return static_cast<int32_t>(t);
}

// One container. Returns true when it holds water (matched).
bool fuel_one(const Access& a, const KilnItemsFuelRequest& r, KilnEntity e, KilnItemId item, uint64_t slot,
              KilnItemsFuelResult* res) {
    PtrSize ps{};
    E.read_other(&ps, a.storage, a.acc[A_FUEL], e);
    auto* comp = static_cast<uint8_t*>(ps.ptr);
    FuelSlots fs;
    if (!comp || !fuel_slots(a.attributes, comp, &fs)) return false;
    ++res->matched;
    int32_t* v = fuel_values(comp);
    const int32_t before = v[fs.cur], max = v[fs.max];
    const int32_t after = fuel_target(r, before, max);
    if (after == before) return true;
    if (!(r.flags & KILN_ITEMS_FUEL_DRY_RUN)) {
        // The same component through the tracked path: WriteOther marks it written for this job (replication to
        // players, the next save), as the game's own programs do before they change another entity's Fuel.
        PtrSize w{};
        E.write_other(&w, a.storage, E.job_tracker(a.job), a.acc[A_FUEL], e, E.job_write_flag(a.ctx));
        if (w.ptr != comp) return true;  // not the block we read: leave it alone
        fuel_values(comp)[fs.cur] = after;
        E.attr_changed(comp, a.attributes);  // the attribute's own formulas: the 0..max clamp
    }
    ++res->changed;
    if (res->change_count < KILN_ITEMS_MAX_FUEL_CHANGES) {
        KilnItemsFuelChange& c = res->changes[res->change_count++];
        c.container = e;
        c.item = item;
        c.slot = slot;
        c.before = before;
        c.after = (r.flags & KILN_ITEMS_FUEL_DRY_RUN) ? after : fuel_values(comp)[fs.cur];
        c.max = max;
    }
    return true;
}

void run_fuel_impl(const Access& a, const KilnItemsFuelRequest& r, KilnItemsFuelResult* res) {
    if (!(r.flags & KILN_ITEMS_FUEL_INVENTORIES)) {
        if (!fuel_one(a, r, r.entity, 0, 0, res))
            return fail_fuel(res, KILN_ITEMS_SLOT_CHANGED, "entity %u holds no water (no Fuel)", r.entity);
        res->status = KILN_ITEMS_DONE;
        return;
    }
    const auto* setup = static_cast<const uint8_t*>(other(a, A_SETUP, r.entity));
    if (!setup) return fail_fuel(res, KILN_ITEMS_NO_INVENTORY, "entity %u has no inventory (no InventorySetup)", r.entity);
    if (!setup[E.setup_initialized])
        return fail_fuel(res, KILN_ITEMS_NO_INVENTORY, "entity %u's inventory is not initialized yet", r.entity);
    Tree t{};
    walk(setup, &setup_via_access, &a, &t, 0, -1);
    for (uint32_t i = 0; i < t.n; ++i) {
        const auto* inv = static_cast<const uint8_t*>(other(a, A_INVENTORY, t.ids[i]));
        if (!inv) continue;
        for (uint32_t k = 0; k < 8; ++k) {
            const uint8_t* st = inv + 12 * k;
            const KilnItemId item = rd<uint32_t>(st, 0);
            const KilnEntity pide = rd<uint32_t>(st, 8);
            if (!item || !pide || rd<uint32_t>(st, 4) == 0) continue;
            if (r.item && item != r.item) continue;
            fuel_one(a, r, pide, item, KILN_SLOT_ID(t.ids[i], k), res);
        }
    }
    res->status = KILN_ITEMS_DONE;
}

}  // namespace

// ---- guarded entry points ----

bool verify_access(const uint64_t* it, const int* sing, const int* accs, const int* evs, void* ctx, Access* a,
                   bool* ok, char* why, unsigned long* code) {
    __try {
        return verify_impl(it, sing, accs, evs, ctx, a, ok, why);
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        *ok = false;
        return false;
    }
}

bool run_job(const Access& a, const Job& job, uint8_t* tx, KilnItemsResult* res, unsigned long* code) {
    __try {
        if (job.take) run_take(a, job.r, tx, res);
        else if (job.r.flags & KILN_ITEMS_RECIPE) run_recipe(a, job.r, tx, res);
        else run_give(a, job.r, tx, res);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool run_fuel(const Access& a, const FuelJob& job, KilnItemsFuelResult* res, unsigned long* code) {
    __try {
        run_fuel_impl(a, job.r, res);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool read_fuel(uintptr_t world, void* attributes, KilnEntity entity, int32_t* current, int32_t* max, bool* found,
               unsigned long* code) {
    __try {
        *found = false;
        PtrSize ps{};
        E.get_component(&ps, world, kHashFuel, entity);
        auto* comp = static_cast<uint8_t*>(ps.ptr);
        FuelSlots fs;
        if (!comp || !fuel_slots(attributes, comp, &fs)) return true;
        const int32_t* v = fuel_values(comp);
        *current = v[fs.cur];
        *max = v[fs.max];
        *found = true;
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void abandon_tx(uint8_t* tx) {
    __try {
        E.rollback(tx);
        E.free_arrays(tx);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
    }
}

bool read_inventory(uintptr_t world, void* registry, KilnEntity entity, ReadOut* out, unsigned long* code) {
    __try {
        read_impl(world, registry, entity, out);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool scan_inventories(uintptr_t world, uint32_t flags, KilnEntity* ids, uint32_t capacity, uint32_t* total,
                      unsigned long* code) {
    __try {
        scan_impl(world, flags, ids, capacity, total);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool inventory_matches(uintptr_t world, KilnEntity e, uint32_t flags, bool* match, unsigned long* code) {
    __try {
        *match = matches(world, e, flags);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool registry_count(void* registry, uint32_t* count, unsigned long* code) {
    __try {
        const uint8_t* map;
        uint64_t cap;
        *count = registry_map(registry, &map, &cap) ? rd<uint32_t>(map, E.hmap_count) : 0;
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool copy_registry(void* registry, RawItem* rows, uint32_t capacity, uint32_t* written, unsigned long* code) {
    __try {
        copy_impl(registry, rows, capacity, written);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool register_program(uintptr_t world, void* desc, int* outcome, char* why, unsigned long* code) {
    __try {
        register_impl(world, desc, outcome, why);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

}  // namespace kitems
