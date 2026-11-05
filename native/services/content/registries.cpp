// SPDX-License-Identifier: MIT
// kiln.content: snapshots of the game's registries (svn 1285601 layouts in docs/re/content.md):
//  - items:   world singleton g38_items::ItemRegistry, HashMap<ItemId, ItemInfo*> at +items.map
//  - recipes: g38_items::RecipeRegistry, HashMap<RecipeId, RecipeInfo*> at +recipes.map (RecipeInfo lives inline in
//             the RecipeRegistryResource's array, read-only resource memory)
//  - loot:    g38_loot::LootGenerationSystem +lootgen.lootreg -> the random-loot registry, HashMap<ItemId,
//             LootableItemInfo*> at +lootreg.map (the roll reads stackSizeMin/Max live through LootFind)
//  - drops:   DefaultInventoryResource blobs, registered by the DefaultInventoryAcquire hook as the game loads them
#include <cstdio>
#include <cstdlib>
#include <cstring>

#include "content.h"

namespace kc {

namespace {
Registry g_regs[5];
}  // namespace

RecipeLayout RL;
DropLayout DL;
std::unordered_map<uint32_t, std::vector<uint8_t>> g_category_guid;

bool valid_kind(uint32_t kind) { return kind >= KILN_CONTENT_ITEMS && kind <= KILN_CONTENT_DROPS; }
Registry& reg(uint32_t kind) { return g_regs[valid_kind(kind) ? kind : 0]; }

void Registry::add(Entry e) {
    const uint32_t i = static_cast<uint32_t>(entries.size());
    by_id.emplace(e.id, i);
    by_addr.emplace(e.addr, i);
    if (!e.name.empty()) {
        by_name.emplace(e.name, i);
        by_lower.emplace(lower(e.name), i);
    }
    entries.push_back(std::move(e));
}

const Entry* Registry::get(uint32_t id) const {
    auto it = by_id.find(id);
    return it == by_id.end() ? nullptr : &entries[it->second];
}

const Entry* Registry::lookup(const std::string& key) const {
    if (key.empty()) return nullptr;
    if (auto it = by_name.find(key); it != by_name.end()) return &entries[it->second];
    if (auto it = by_lower.find(lower(key)); it != by_lower.end()) return &entries[it->second];
    uint8_t g[16];
    if (key.size() == 36 && guid_parse(key, g)) return get(fnv1a(g, 16));
    char* end = nullptr;
    unsigned long long v = 0;
    if (key.size() > 2 && key[0] == '0' && (key[1] == 'x' || key[1] == 'X')) v = std::strtoull(key.c_str() + 2, &end, 16);
    else if (key[0] >= '0' && key[0] <= '9') v = std::strtoull(key.c_str(), &end, 10);
    if (end && *end == 0 && v <= 0xFFFFFFFFull) return get(static_cast<uint32_t>(v));
    return nullptr;
}

namespace {

bool layouts(std::string* why) {
    auto off = [&](const char* t, const char* f, uint32_t* out) {
        const int64_t o = field_offset(t, f);
        if (o < 0) {
            *why = std::string("reflection: ") + t + "." + f + " missing";
            return false;
        }
        *out = static_cast<uint32_t>(o);
        return true;
    };
    uint32_t stack = 0, cat = 0, item = 0, count = 0, ref = 0, ccat = 0, ccount = 0, cref = 0;
    if (!off("keen::RecipeInfo", "input", &RL.input) || !off("keen::RecipeInfo", "output", &RL.output) ||
        !off("keen::RecipeInfo", "debugName", &RL.debug_name) || !off("keen::RecipeInfo", "recipeGuid", &RL.guid) ||
        !off("keen::RecipeInputResource", "itemStack", &stack) ||
        !off("keen::RecipeInputResource", "inputItemCategory", &cat) ||
        !off("keen::RecipeItemStackResource", "item", &item) || !off("keen::RecipeItemStackResource", "count", &count) ||
        !off("keen::RecipeItemStackResource", "itemRef", &ref) ||
        !off("keen::RecipeInputCategoryResource", "category", &ccat) ||
        !off("keen::RecipeInputCategoryResource", "count", &ccount) ||
        !off("keen::RecipeInputCategoryResource", "categoryRef", &cref))
        return false;
    RL.in_stride = type_size("keen::RecipeInputResource");
    RL.out_stride = type_size("keen::RecipeItemStackResource");
    RL.in_item_ref = stack + ref;
    RL.in_item = stack + item;
    RL.in_count = stack + count;
    RL.in_cat_ref = cat + cref;
    RL.in_cat = cat + ccat;
    RL.in_cat_count = cat + ccount;
    RL.out_item_ref = ref;
    RL.out_item = item;
    RL.out_count = count;
    if (RL.in_stride < 32 || RL.in_stride > 256 || RL.out_stride < 20 || RL.out_stride > 128) {
        *why = "reflection: recipe stack sizes look wrong";
        return false;
    }
    uint32_t root = 0;
    if (!off("keen::ecs::DefaultInventoryResource", "rootGroup", &root) ||
        !off("keen::ecs::DefaultInventoryGroupResource", "stacks", &DL.group_stacks) ||
        !off("keen::ecs::DefaultInventoryGroupResource", "groups", &DL.group_groups) ||
        !off("keen::ecs::ItemStackInfoResource", "item", &DL.s_item) ||
        !off("keen::ecs::ItemStackInfoResource", "countMin", &DL.s_min) ||
        !off("keen::ecs::ItemStackInfoResource", "countMax", &DL.s_max) ||
        !off("keen::ecs::ItemStackInfoResource", "spawnProbability", &DL.s_chance) ||
        !off("keen::ecs::ItemStackInfoResource", "weight", &DL.s_weight)) {
        E.drops = false;  // drop tables only
        why->clear();
    } else {
        DL.group_size = type_size("keen::ecs::DefaultInventoryGroupResource");
        DL.stack_size = type_size("keen::ecs::ItemStackInfoResource");
        if (root != 0 || DL.group_size < 16 || DL.stack_size < 32) E.drops = false;
    }
    return true;
}

struct BlobArr {
    uintptr_t data = 0;
    uint32_t count = 0;
};
BlobArr blob_array(uintptr_t field) {
    int32_t off = 0;
    uint32_t n = 0;
    BlobArr a;
    if (!rdv(field, off) || !rdv(field + 4, n) || n > 100000) return a;
    if (n) a.data = field + static_cast<intptr_t>(off);
    a.count = a.data ? n : 0;
    return a;
}

bool schemas(std::string* why) {
    static bool done = false;
    if (done) return true;
    std::string err;
    Registry& I = reg(KILN_CONTENT_ITEMS);
    Registry& R = reg(KILN_CONTENT_RECIPES);
    Registry& L = reg(KILN_CONTENT_LOOT);
    Registry& D = reg(KILN_CONTENT_DROPS);
    I.kind = KILN_CONTENT_ITEMS, I.label = "items";
    R.kind = KILN_CONTENT_RECIPES, R.label = "recipes";
    L.kind = KILN_CONTENT_LOOT, L.label = "loot";
    D.kind = KILN_CONTENT_DROPS, D.label = "drops";
    // Identity and structure fields stay read-only: changing them would break the registries' maps, saves or the
    // item-entity logic (flags: HasPIDE / IsContainer decide how stacks and gear entities are handled).
    if (!build_schema("keen::ItemInfo", {"itemId", "objectId", "flags"}, &I.schema, &err)) {
        *why = err;
        return false;
    }
    if (!build_schema("keen::RecipeInfo", {"recipeGuid", "recipeId", "workshopGuid", "workshopId", "requiredPropsMask"},
                      &R.schema, &err)) {
        *why = err;
        return false;
    }
    if (!build_schema("keen::LootableItemInfo", {"itemId"}, &L.schema, &err)) {
        LOGW("loot registry off: %s", err.c_str());
        E.loot = false;
    }
    if (!build_schema("keen::ecs::ItemStackInfoResource", {"itemRef", "item", "lootLabelsId"}, &D.schema, &err)) {
        LOGW("drop tables off: %s", err.c_str());
        E.drops = false;
    }
    if (!layouts(why)) return false;
    done = true;
    LOGI("reflection: keen::ItemInfo %u bytes, %zu fields; keen::RecipeInfo %u bytes, %zu fields (inputs +0x%x x%u B, "
         "outputs +0x%x x%u B); keen::LootableItemInfo %u bytes, %zu fields; keen::ecs::ItemStackInfoResource %u bytes, "
         "%zu fields",
         I.schema.size, I.schema.fields.size(), R.schema.size, R.schema.fields.size(), RL.input, RL.in_stride, RL.output,
         RL.out_stride, L.schema.size, L.schema.fields.size(), D.schema.size, D.schema.fields.size());
    return true;
}

uint32_t special(const Registry& r, const char* name, uint32_t fallback) {
    auto it = r.schema.special.find(name);
    return it == r.schema.special.end() ? fallback : it->second;
}

}  // namespace

bool build_registries(uintptr_t world, std::string* why) {
    if (!schemas(why)) return false;
    Registry& I = reg(KILN_CONTENT_ITEMS);
    Registry& R = reg(KILN_CONTENT_RECIPES);
    Registry& L = reg(KILN_CONTENT_LOOT);
    Registry& D = reg(KILN_CONTENT_DROPS);
    std::vector<uint32_t> keys;
    std::vector<uint64_t> vals;

    // ---- items ----
    const uintptr_t ireg = g_api->singleton(world, KilnStr{"g38_items::ItemRegistry", 23});
    if (!ireg || !map_read(ireg + E.items_map, &keys, &vals)) {
        *why = "the item registry is not readable";
        return false;
    }
    const uint32_t i_id = special(I, "itemId", 0), i_guid = special(I, "objectId", 4), i_name = special(I, "debugName", 0x820);
    I.entries.clear(), I.by_id.clear(), I.by_name.clear(), I.by_lower.clear(), I.by_addr.clear();
    uint32_t bad = 0;
    for (size_t k = 0; k < keys.size(); ++k) {
        Entry e;
        e.id = keys[k];
        e.addr = static_cast<uintptr_t>(vals[k]);
        uint32_t id = 0;
        if (!e.addr || !rdv(e.addr + i_id, id) || id != e.id || !rd(e.addr + i_guid, e.guid, 16)) {
            ++bad;
            continue;
        }
        e.name = read_blob_string(e.addr + i_name);
        I.add(std::move(e));
    }
    I.available = !I.entries.empty() && bad < 16;
    if (!I.available) {
        *why = "the item registry's entries do not match keen::ItemInfo";
        return false;
    }

    // ---- recipes ----
    R.entries.clear(), R.by_id.clear(), R.by_name.clear(), R.by_lower.clear(), R.by_addr.clear();
    g_category_guid.clear();
    const uintptr_t rreg = E.recipes ? g_api->singleton(world, KilnStr{"g38_items::RecipeRegistry", 25}) : 0;
    if (rreg && map_read(rreg + E.recipes_map, &keys, &vals)) {
        const uint32_t r_id = special(R, "recipeId", 0x10);
        bad = 0;
        for (size_t k = 0; k < keys.size(); ++k) {
            Entry e;
            e.id = keys[k];
            e.addr = static_cast<uintptr_t>(vals[k]);
            uint32_t id = 0;
            if (!e.addr || !rdv(e.addr + r_id, id) || id != e.id || !rd(e.addr + RL.guid, e.guid, 16)) {
                ++bad;
                continue;
            }
            e.name = read_blob_string(e.addr + RL.debug_name);
            if (e.name.empty()) {
                char b[24];
                std::snprintf(b, sizeof b, "recipe_%08x", e.id);
                e.name = b;
            }
            // category GUIDs (id -> reference) for set_stacks
            const BlobArr in = blob_array(e.addr + RL.input);
            for (uint32_t j = 0; j < in.count && j < 64; ++j) {
                const uintptr_t s = in.data + static_cast<uintptr_t>(j) * RL.in_stride;
                uint32_t cat = 0;
                uint8_t g[16];
                if (rdv(s + RL.in_cat, cat) && cat && rd(s + RL.in_cat_ref, g, 16))
                    g_category_guid.emplace(cat, std::vector<uint8_t>(g, g + 16));
            }
            R.add(std::move(e));
        }
        R.available = !R.entries.empty() && bad < 16;
    }
    if (!R.available) LOGW("recipes off: the recipe registry is not readable");

    // ---- random-loot items ----
    L.entries.clear(), L.by_id.clear(), L.by_name.clear(), L.by_lower.clear(), L.by_addr.clear();
    const uintptr_t lg = E.loot ? g_api->singleton(world, KilnStr{"g38_loot::LootGenerationSystem", 30}) : 0;
    uintptr_t lreg = 0;
    if (lg && rdv(lg + E.lootgen_lootreg, lreg) && lreg && map_read(lreg + E.lootreg_map, &keys, &vals)) {
        const uint32_t l_id = special(L, "itemId", 0);
        bad = 0;
        for (size_t k = 0; k < keys.size(); ++k) {
            Entry e;
            e.id = keys[k];
            e.addr = static_cast<uintptr_t>(vals[k]);
            uint32_t id = 0;
            if (!e.addr || !rdv(e.addr + l_id, id) || id != e.id) {
                ++bad;
                continue;
            }
            if (const Entry* it = I.get(e.id)) {
                e.name = it->name;
                std::memcpy(e.guid, it->guid, 16);
            }
            L.add(std::move(e));
        }
        L.available = !L.entries.empty() && bad < 16;
    }
    if (E.loot && !L.available) LOGW("random-loot items off: the loot registry is not readable");
    D.available = E.drops;
    return true;
}

bool read_recipe_stacks(const Entry& e, uint32_t which, std::vector<KilnContentStack>* out) {
    out->clear();
    const BlobArr a = blob_array(e.addr + (which == KILN_CONTENT_INPUTS ? RL.input : RL.output));
    if (a.count > 256) return false;
    for (uint32_t j = 0; j < a.count; ++j) {
        KilnContentStack s{};
        s.chance = 1;
        if (which == KILN_CONTENT_INPUTS) {
            const uintptr_t p = a.data + static_cast<uintptr_t>(j) * RL.in_stride;
            uint32_t item = 0, count = 0, cat = 0, ccount = 0;
            if (!rdv(p + RL.in_item, item) || !rdv(p + RL.in_count, count) || !rdv(p + RL.in_cat, cat) ||
                !rdv(p + RL.in_cat_count, ccount))
                return false;
            s.item = item;
            s.category = cat;
            s.count = item ? count : ccount;
        } else {
            const uintptr_t p = a.data + static_cast<uintptr_t>(j) * RL.out_stride;
            if (!rdv(p + RL.out_item, s.item) || !rdv(p + RL.out_count, s.count)) return false;
        }
        s.count_max = s.count;
        out->push_back(s);
    }
    return true;
}

namespace {
void walk_group(uintptr_t g, int depth, uint32_t* group_counter, std::vector<DropStackRef>* out) {
    if (depth > 8 || out->size() > 4096) return;
    const uint32_t me = (*group_counter)++;
    const BlobArr st = blob_array(g + DL.group_stacks);
    for (uint32_t i = 0; i < st.count && i < 1024; ++i)
        out->push_back(DropStackRef{st.data + static_cast<uintptr_t>(i) * DL.stack_size, me});
    const BlobArr gr = blob_array(g + DL.group_groups);
    for (uint32_t i = 0; i < gr.count && i < 256; ++i)
        walk_group(gr.data + static_cast<uintptr_t>(i) * DL.group_size, depth + 1, group_counter, out);
}
}  // namespace

bool walk_drop_table(uintptr_t resource, std::vector<DropStackRef>* out) {
    out->clear();
    uint32_t groups = 0;
    walk_group(resource, 0, &groups, out);
    return true;
}

void read_drop_stack(uintptr_t addr, uint32_t group, KilnContentStack* s) {
    *s = KilnContentStack{};
    rdv(addr + DL.s_item, s->item);
    rdv(addr + DL.s_min, s->count);
    rdv(addr + DL.s_max, s->count_max);
    rdv(addr + DL.s_chance, s->chance);
    rdv(addr + DL.s_weight, s->weight);
    s->group = group;
}

}  // namespace kc
