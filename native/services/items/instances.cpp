// SPDX-License-Identifier: MIT
// kiln.items v4: item instances (instances.h). The game reads below copy plain data out of game memory under a
// structured-exception guard (raw_* functions, no C++ objects with destructors); the rest works on those copies.
//
// What is read, and where it comes from (docs/kiln/services/items.md "Item instances"):
//  - ItemState {containedInSlotId {u32 inventory, u8 index}, +8 itemId, +0xE itemState bits}: the item and its slot;
//    the slot's Inventory stack must hold this item entity, else the item is FOUND but not PLACED.
//  - the holder: the entity whose InventorySetup links the slot's inventory (a map, rebuilt from every InventorySetup
//    when an inventory is not in it, at most once per step, and checked against the holder's setup on every use);
//  - the flat slot as the world and character savers number it: only top-level Generic (4) links are saved, each adds
//    8 to the base (0x72a520, 0x72c6eb);
//  - the record fields as the record builder 0x72a9b0 reads them: Durability and Fuel {current, max} and Level through
//    the attribute registry (the attribute's group must be the block's first u32, the block must have values),
//    ColorPaletteItem, PerkContainerNew {available, unlocked} or else PerkContainer (5 perks, counted up to the first
//    empty one), WeaponGemSlot {gem item id, +8 the gem's own item entity, whose Level is the gem level}.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstring>
#include <unordered_map>

#include "engine.h"
#include "instances.h"

namespace kitems {
void items_log(int level, const char* fmt, ...);

namespace {

StampTable g_stamps;
std::atomic<uint64_t> g_reads{0};
std::atomic<uint32_t> g_rebuilds{0};
std::atomic<uint32_t> g_faults{0};

constexpr uint32_t kHashDurability = 0xdffb1608u;    // keen::ecs::Durability (SInt32 attribute block)
constexpr uint32_t kHashLevel = 0xe550da37u;         // keen::ecs::Level (attribute block)
constexpr uint32_t kHashPerkNew = 0xe288d50cu;       // keen::ecs::PerkContainerNew {u8 available, u8 unlocked}
constexpr uint32_t kHashPerkOld = 0xbb8a4ba2u;       // keen::ecs::PerkContainer {u32 perks[5]; u8 unlocked[5]}
constexpr uint32_t kHashGemSlot = 0x84ba9b52u;       // keen::ecs::WeaponGemSlot {ItemStack weaponGem}
constexpr uint32_t kHashDye = 0x103e50eau;           // keen::ecs::ColorPaletteItem {u32 paletteItemId}
constexpr uint32_t kAttrHeader = 8;                  // {u32 group; u16 values offset; u8 flags; u8 count}
constexpr uint32_t kUnsavedBase = 256;               // flat slots of links the game's savers do not number

template <typename T>
T rdv(const void* p, int64_t off) {
    T v;
    std::memcpy(&v, static_cast<const uint8_t*>(p) + off, sizeof v);
    return v;
}

// ---- raw game reads (plain data, SEH-guarded) ----

const uint8_t* comp_at(uintptr_t world, uint32_t hash, uint32_t e, uint64_t min_size) {
    PtrSize ps{};
    E.get_component(&ps, world, hash, e);
    return ps.ptr && ps.size >= min_size ? static_cast<const uint8_t*>(ps.ptr) : nullptr;
}

// One attribute value of an attribute block, as the record builder reads it. false: not there.
bool attr_value(void* attributes, const uint8_t* c, uint32_t attr, int32_t* out) {
    if (!c || !attr || !(c[6] & 1)) return false;
    AttrRef r{};
    E.attr_find(&r, attr, attributes);
    if (r.error || !r.desc || rdv<uint32_t>(r.desc, 0) != rdv<uint32_t>(c, 0)) return false;
    const uint16_t index = rdv<uint16_t>(r.desc, 0xC);
    if (index >= c[7]) return false;
    *out = rdv<int32_t>(c, rdv<uint16_t>(c, 4) + 4ll * index);
    return true;
}

struct InstItem {
    uint8_t has_state;
    uint32_t inv;
    uint8_t index;
    uint32_t item;
    uint16_t state;
    uint8_t has_stack;
    uint32_t stack_item, stack_count, stack_pide;
};

void raw_item_impl(uintptr_t world, uint32_t e, InstItem* r) {
    const uint8_t* is = comp_at(world, kHashItemState, e, kItemStateSize);
    if (!is) return;
    r->has_state = 1;
    r->inv = rdv<uint32_t>(is, 0);
    r->index = is[4];
    r->item = rdv<uint32_t>(is, 8);
    r->state = rdv<uint16_t>(is, 0x0E);
    if (r->inv == 0 || r->index > 7) return;
    const uint8_t* inv = comp_at(world, kHashInventory, r->inv, kInventorySize);
    if (!inv) return;
    const uint8_t* st = inv + 12 * r->index;
    r->has_stack = 1;
    r->stack_item = rdv<uint32_t>(st, 0);
    r->stack_count = rdv<uint32_t>(st, 4);
    r->stack_pide = rdv<uint32_t>(st, 8);
}

bool raw_item(uintptr_t world, uint32_t e, InstItem* r) {
    __try {
        std::memset(r, 0, sizeof *r);
        raw_item_impl(world, e, r);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

struct InstRecord {
    uint32_t flags;
    uint32_t level;
    int32_t dur, dur_max, fuel, fuel_max;
    uint32_t dye;
    uint8_t perks_available, perks_unlocked, gem_level;
    uint32_t gem;
};

void raw_record_impl(uintptr_t world, void* attributes, uint32_t e, InstRecord* r) {
    int32_t a = 0, b = 0;
    if (const uint8_t* c = comp_at(world, kHashDurability, e, kAttrHeader)) {
        if (attr_value(attributes, c, E.attr_durability, &a) && attr_value(attributes, c, E.attr_durability_max, &b)) {
            r->flags |= KILN_ITEMS_INSTANCE_DURABILITY;
            r->dur = a;
            r->dur_max = b;
        }
    }
    if (const uint8_t* c = comp_at(world, kHashFuel, e, kAttrHeader)) {
        if (attr_value(attributes, c, E.attr_fuel, &a) && attr_value(attributes, c, E.attr_fuel_max, &b)) {
            r->flags |= KILN_ITEMS_INSTANCE_FUEL;
            r->fuel = a;
            r->fuel_max = b;
        }
    }
    if (const uint8_t* c = comp_at(world, kHashLevel, e, kAttrHeader)) {
        if (attr_value(attributes, c, E.attr_level, &a)) {
            r->flags |= KILN_ITEMS_INSTANCE_LEVEL;
            r->level = static_cast<uint32_t>(a);
        }
    }
    if (const uint8_t* c = comp_at(world, kHashDye, e, 4)) {
        r->flags |= KILN_ITEMS_INSTANCE_DYE;
        r->dye = rdv<uint32_t>(c, 0);
    }
    if (const uint8_t* c = comp_at(world, kHashPerkNew, e, 2)) {
        r->flags |= KILN_ITEMS_INSTANCE_PERKS;
        r->perks_available = c[0];
        r->perks_unlocked = c[1];
    } else if (const uint8_t* o = comp_at(world, kHashPerkOld, e, 25)) {
        r->flags |= KILN_ITEMS_INSTANCE_PERKS;
        for (int i = 0; i < 5 && rdv<uint32_t>(o, 4 * i) != 0; ++i) {  // counted up to the first empty perk, as the game does
            ++r->perks_available;
            if (o[0x14 + i]) ++r->perks_unlocked;
        }
    }
    if (const uint8_t* c = comp_at(world, kHashGemSlot, e, 12)) {
        r->flags |= KILN_ITEMS_INSTANCE_GEM;
        r->gem = rdv<uint32_t>(c, 0);
        const uint32_t gem_pide = rdv<uint32_t>(c, 8);
        if (r->gem && gem_pide && gem_pide != 0xFFFFFFFFu) {
            if (const uint8_t* l = comp_at(world, kHashLevel, gem_pide, kAttrHeader))
                if (attr_value(attributes, l, E.attr_level, &a)) r->gem_level = static_cast<uint8_t>(a);
        }
    }
}

bool raw_record(uintptr_t world, void* attributes, uint32_t e, InstRecord* r) {
    __try {
        std::memset(r, 0, sizeof *r);
        raw_record_impl(world, attributes, e, r);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// An inventory in a holder's link tree, numbered as the game's savers number it.
struct LinkPos {
    uint32_t inv;
    uint8_t link;      // the top-level link (outer for Virtual)
    uint8_t category;
    uint8_t pos;       // position in the tree
    int8_t generic;    // ordinal among the top-level Generic links (the saved ones); -1 = not saved
};

struct InstTree {
    uint8_t found;
    uint8_t initialized;
    uint32_t n;
    LinkPos p[KILN_ITEMS_MAX_INVENTORIES];
};

void walk_tree(uintptr_t world, const uint8_t* setup, InstTree* t, int depth, int outer, int* generic_next) {
    for (int k = 0; k < 16; ++k) {
        const uint32_t id = rdv<uint32_t>(setup, 4 * k);
        const uint8_t cat = setup[0x40 + k];
        // the savers skip every link that is not Generic and count each Generic one (0x72a565: cmp category, 4)
        const bool saved = depth == 0 && cat == 4;
        const int ordinal = saved ? (*generic_next)++ : -1;
        if (id == 0) continue;
        const int link = outer >= 0 ? outer : k;
        if (cat == 5) {  // Virtual: another InventorySetup (never saved by these savers)
            if (depth < 3)
                if (const uint8_t* inner = comp_at(world, kHashInventorySetup, id, kSetupSize))
                    walk_tree(world, inner, t, depth + 1, link, generic_next);
            continue;
        }
        bool dup = false;
        for (uint32_t j = 0; j < t->n; ++j) dup = dup || t->p[j].inv == id;
        if (dup || t->n >= KILN_ITEMS_MAX_INVENTORIES) continue;
        LinkPos& p = t->p[t->n];
        p.inv = id;
        p.link = static_cast<uint8_t>(link);
        p.category = cat;
        p.pos = static_cast<uint8_t>(t->n);
        p.generic = static_cast<int8_t>(ordinal);
        ++t->n;
    }
}

bool raw_tree(uintptr_t world, uint32_t holder, InstTree* t) {
    __try {
        std::memset(t, 0, sizeof *t);
        const uint8_t* setup = comp_at(world, kHashInventorySetup, holder, kSetupSize);
        if (!setup) return true;
        t->found = 1;
        t->initialized = setup[E.setup_initialized];
        int generic = 0;
        walk_tree(world, setup, t, 0, -1, &generic);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        std::memset(t, 0, sizeof *t);
        return false;
    }
}

struct InstStacks {
    uint8_t ok;
    uint32_t item[8], count[8], pide[8];
};

bool raw_stacks(uintptr_t world, uint32_t inv, InstStacks* s) {
    __try {
        std::memset(s, 0, sizeof *s);
        const uint8_t* c = comp_at(world, kHashInventory, inv, kInventorySize);
        if (!c) return true;
        s->ok = 1;
        for (int k = 0; k < 8; ++k) {
            s->item[k] = rdv<uint32_t>(c, 12 * k);
            s->count[k] = rdv<uint32_t>(c, 12 * k + 4);
            s->pide[k] = rdv<uint32_t>(c, 12 * k + 8);
        }
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool raw_counter(uintptr_t world, uint32_t* out) {
    __try {
        *out = *reinterpret_cast<const uint32_t*>(world + static_cast<uintptr_t>(E.world_storage + E.storage_next_id));
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool raw_is_item(uintptr_t world, uint32_t e) {
    __try {
        return comp_at(world, kHashItemState, e, kItemStateSize) != nullptr;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void read_fault(const char* what, uint32_t e) {
    if (g_faults.fetch_add(1) < 8) items_log(KILN_LOG_WARN, "item instances: reading %s of entity %u faulted; reported as not found", what, e);
}

uint32_t flat_of(const LinkPos& p, uint32_t index) {
    return p.generic >= 0 ? static_cast<uint32_t>(p.generic) * 8 + index : kUnsavedBase + p.pos * 8u + index;
}

// ---- the inventory -> holder map (tick thread) ----
struct Where {
    uint32_t holder;
    LinkPos lp;
};
std::unordered_map<uint32_t, Where> g_where;
uint64_t g_where_step = ~0ull;  // the step of the last rebuild

struct Lookup {
    const InstanceEnv& env;
    std::unordered_map<uint32_t, InstTree> trees;  // holder -> its tree, read once per call
    bool rebuilt = false;

    explicit Lookup(const InstanceEnv& e) : env(e) {}

    const InstTree* tree(uint32_t holder) {
        auto it = trees.find(holder);
        if (it != trees.end()) return &it->second;
        InstTree t;
        if (!raw_tree(env.world, holder, &t)) read_fault("an InventorySetup", holder);
        return &trees.emplace(holder, t).first->second;
    }

    bool rebuild() {
        if (rebuilt || g_where_step == env.step || !env.list) return false;
        rebuilt = true;
        g_where_step = env.step;
        std::vector<KilnEntity> holders;
        if (!env.list(&holders)) return false;
        g_where.clear();
        for (KilnEntity h : holders) {
            const InstTree* t = tree(h);
            for (uint32_t i = 0; i < t->n; ++i) g_where.emplace(t->p[i].inv, Where{h, t->p[i]});
        }
        g_rebuilds.fetch_add(1, std::memory_order_relaxed);
        return true;
    }

    // Where the inventory `inv` hangs, checked against its holder's setup now.
    bool locate(uint32_t inv, Where* out) {
        for (int pass = 0; pass < 2; ++pass) {
            auto it = g_where.find(inv);
            if (it != g_where.end()) {
                const InstTree* t = tree(it->second.holder);
                for (uint32_t i = 0; i < t->n; ++i)
                    if (t->p[i].inv == inv) {
                        *out = Where{it->second.holder, t->p[i]};
                        it->second.lp = t->p[i];
                        return true;
                    }
                g_where.erase(it);  // stale: the holder no longer links it there
            }
            if (pass == 0 && !rebuild()) return false;
        }
        return false;
    }
};

void zero_instance(KilnItemsInstance* it) {
    const uint32_t e = it->entity;
    std::memset(it, 0, sizeof *it);
    it->entity = e;
}

// Reads one item entity into `it` (entity set). `where`: its slot's placement when the caller already knows it.
void read_one(const InstanceEnv& env, Lookup& look, KilnItemsInstance* it, uint32_t flags, const Where* known,
              uint32_t known_index) {
    const uint32_t e = it->entity;
    if (!StampTable::stampable(e)) return;
    if (g_stamps.gone(e)) {
        it->flags = KILN_ITEMS_INSTANCE_GONE;
        return;
    }
    InstItem r;
    if (!raw_item(env.world, e, &r)) return read_fault("the ItemState", e);
    if (!r.has_state) return;
    const uint32_t stamp = g_stamps.stamp(e, r.item, env.step);
    if (!stamp) {
        if (g_stamps.gone(e)) it->flags = KILN_ITEMS_INSTANCE_GONE;
        return;
    }
    g_reads.fetch_add(1, std::memory_order_relaxed);
    it->flags = KILN_ITEMS_INSTANCE_FOUND;
    it->stamp = stamp;
    it->item = r.item;
    it->state = r.state;
    if (r.state & (1u << 3)) it->flags |= KILN_ITEMS_INSTANCE_EQUIPPED;
    Where w{};
    uint32_t index = r.index;
    bool placed = false;
    if (known) {
        w = *known;
        index = known_index;
        placed = true;
        it->count = 1;
        InstStacks s;
        if (raw_stacks(env.world, w.lp.inv, &s) && s.ok && index < 8) it->count = s.count[index];
    } else if (r.has_stack && r.stack_pide == e && r.stack_count != 0) {
        placed = look.locate(r.inv, &w);
        it->count = r.stack_count;
    }
    it->slot = KILN_SLOT_ID(placed ? w.lp.inv : r.inv, placed ? index : r.index);
    if (placed) {
        it->flags |= KILN_ITEMS_INSTANCE_PLACED;
        it->holder = w.holder;
        it->link = w.lp.link;
        it->category = w.lp.category;
        it->flat_slot = flat_of(w.lp, index);
        if (w.lp.generic >= 0) it->flags |= KILN_ITEMS_INSTANCE_SAVED_SLOT;
    } else {
        it->count = 0;
    }
    if (flags & KILN_ITEMS_READ_LOCATION) return;
    InstRecord rec;
    if (!raw_record(env.world, env.attributes, e, &rec)) return read_fault("the record fields", e);
    it->flags |= rec.flags;
    it->level = rec.level;
    it->durability = rec.dur;
    it->durability_max = rec.dur_max;
    it->fuel = rec.fuel;
    it->fuel_max = rec.fuel_max;
    it->dye = rec.dye;
    it->perks_available = rec.perks_available;
    it->perks_unlocked = rec.perks_unlocked;
    it->gem = rec.gem;
    it->gem_level = rec.gem_level;
}

uint32_t g_prune_at = 4096;

}  // namespace

StampTable& stamps() { return g_stamps; }

bool instances_on() { return E.instances_ok && !g_stamps.exhausted(); }

uint32_t stamp_for(uint32_t pide, uint32_t item) {
    if (!instances_on() || pide == 0 || pide == 0xFFFFFFFFu) return 0;
    return g_stamps.stamp(pide, item, current_step());
}

uint32_t stamp_destroyed(uint32_t pide, uint32_t item) {
    if (!instances_on() || pide == 0 || pide == 0xFFFFFFFFu) return 0;
    return g_stamps.destroyed(pide, item, current_step());
}

KilnResult instances_read(const InstanceEnv& env, KilnItemsInstance* items, uint32_t count, uint32_t stride,
                          uint32_t flags) {
    Lookup look(env);
    for (uint32_t i = 0; i < count; ++i) {
        auto* it = reinterpret_cast<KilnItemsInstance*>(reinterpret_cast<uint8_t*>(items) + static_cast<size_t>(i) * stride);
        zero_instance(it);
        read_one(env, look, it, flags, nullptr, 0);
    }
    return KILN_OK;
}

KilnResult instances_read_holder(const InstanceEnv& env, KilnEntity holder, KilnItemsInstance* out, uint32_t capacity,
                                 uint32_t stride, uint32_t* out_count, uint8_t* out_initialized) {
    Lookup look(env);
    const InstTree* t = look.tree(holder);
    if (!t->found) return KILN_ERR_NOT_FOUND;
    if (out_initialized) *out_initialized = t->initialized;
    std::vector<KilnItemsInstance> found;
    for (uint32_t i = 0; i < t->n; ++i) {
        const LinkPos& lp = t->p[i];
        g_where[lp.inv] = Where{holder, lp};  // a fresh look at this holder's links
        InstStacks s;
        if (!raw_stacks(env.world, lp.inv, &s)) {
            read_fault("an Inventory", lp.inv);
            continue;
        }
        if (!s.ok) continue;
        for (uint32_t k = 0; k < 8; ++k) {
            const uint32_t pide = s.pide[k];
            if (!s.item[k] || !s.count[k] || !pide || pide == 0xFFFFFFFFu) continue;
            KilnItemsInstance it{};
            it.entity = pide;
            const Where w{holder, lp};
            read_one(env, look, &it, 0, &w, k);
            if (it.flags & KILN_ITEMS_INSTANCE_FOUND) found.push_back(it);
        }
    }
    std::stable_sort(found.begin(), found.end(),
                     [](const KilnItemsInstance& a, const KilnItemsInstance& b) { return a.flat_slot < b.flat_slot; });
    const uint32_t n = static_cast<uint32_t>(found.size());
    for (uint32_t i = 0; i < n && i < capacity; ++i) {
        auto* dst = reinterpret_cast<KilnItemsInstance*>(reinterpret_cast<uint8_t*>(out) + static_cast<size_t>(i) * stride);
        *dst = found[i];
    }
    if (out_count) *out_count = n;
    return n > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

void instances_pre(uintptr_t world, uint64_t step) {
    if (!E.instances_ok || !world) return;
    uint32_t next = 0;
    if (raw_counter(world, &next) && next != 0 && g_stamps.observe_counter(next)) {
        g_where.clear();
        g_where_step = ~0ull;
        items_log(KILN_LOG_WARN, "item instances: the entity id counter went down (now %u): every stamp is forgotten", next);
    }
    const size_t n = g_stamps.size();
    if (n > g_prune_at) {
        const size_t dropped = g_stamps.prune(step, 4, [world](uint32_t e) { return raw_is_item(world, e); });
        const size_t left = g_stamps.size();
        g_prune_at = static_cast<uint32_t>(std::min<size_t>(0xFFFFFFFFu, std::max<size_t>(4096, 2 * left)));
        items_log(KILN_LOG_INFO, "item instances: %zu stamp(s) of gone item entities dropped, %zu kept", dropped, left);
    }
}

bool instances_capture(uint32_t entity, uint32_t item, uint64_t step, uint32_t* stamp, uint64_t* epoch) {
    const StampTable::Captured c = g_stamps.capture(entity, item, step);
    *stamp = c.stamp;
    *epoch = c.epoch;
    return c.stamp != 0;
}
uint64_t instances_epoch() { return g_stamps.epoch(); }
uint32_t instances_peek(uint32_t entity) { return g_stamps.peek(entity); }

void instances_world_reset() {
    g_stamps.reset();
    g_where.clear();
    g_where_step = ~0ull;
    g_prune_at = 4096;
}

void instances_status(KilnItemsInstancesStatus* out) {
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    if (instances_on()) out->flags |= KILN_ITEMS_INSTANCES_ON;
    if (g_stamps.exhausted()) out->flags |= KILN_ITEMS_INSTANCES_EXHAUSTED;
    out->stamps_issued = g_stamps.issued();
    out->reads = g_reads.load();
    out->table_size = static_cast<uint32_t>(g_stamps.size());
    out->resets = static_cast<uint32_t>(g_stamps.resets());
    out->next_entity_id = g_stamps.counter();
    out->mismatches = static_cast<uint32_t>(g_stamps.mismatches());
    out->holder_rebuilds = g_rebuilds.load();
}

}  // namespace kitems
