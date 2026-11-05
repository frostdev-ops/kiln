// SPDX-License-Identifier: MIT
// Read-only view of g38_items::RecipeRegistry (the server world's singleton): RecipeFind (the game's own lookup) and
// the workshop map at registry+0xF8 (keen map: workshop id -> SmallDynamicArray<RecipeInfo*, 512>, 0x1008 bytes, u16
// count at +0x1000). RecipeInfo: +0x10 id, +0x24 workshop, +0x28 inputs and +0x30 outputs (BlobArray {i32 self-relative
// offset; u32 count}; inputs 48-byte items, outputs 24-byte items, item id at +0x10, count at +0x14), +0x38 duration ns,
// +0x48 required props mask. Evidence: docs/re/gameplay.md (EMF inventory_crafting.md, checked on svn 1285601).
#include "internal.h"

namespace kg {
namespace {

using RecipeFindFn = const uint8_t* (*)(uint32_t id, uintptr_t registry);
RecipeFindFn g_find = nullptr;
int64_t g_workshops = 0xF8;
bool g_ok = false;

uintptr_t registry() {
    const uintptr_t w = world();
    if (!w || !g_api->singleton) return 0;
    static const char kName[] = "g38_items::RecipeRegistry";
    return g_api->singleton(w, KilnStr{kName, sizeof kName - 1});
}

struct FindCtx {
    uintptr_t reg;
    uint32_t id;
    KilnGameplayRecipe* out;
    bool found;
};

void copy_items(const uint8_t* field, uint32_t stride, KilnGameplayItem* out, uint32_t* n) {
    int32_t rel;
    uint32_t count;
    std::memcpy(&rel, field, 4);
    std::memcpy(&count, field + 4, 4);
    *n = count > 64 ? 0 : count;
    const uint8_t* items = field + rel;
    for (uint32_t i = 0; i < *n && i < KILN_GAMEPLAY_MAX_OUTPUTS; ++i) {
        std::memcpy(&out[i].item, items + i * stride + 0x10, 4);
        std::memcpy(&out[i].count, items + i * stride + 0x14, 4);
    }
}

void find_one(void* p) {
    auto* c = static_cast<FindCtx*>(p);
    const uint8_t* r = g_find(c->id, c->reg);
    c->found = r != nullptr;
    if (!r) return;
    KilnGameplayRecipe& o = *c->out;
    std::memcpy(&o.id, r + 0x10, 4);
    std::memcpy(&o.workshop, r + 0x24, 4);
    copy_items(r + 0x28, 48, o.inputs, &o.input_count);
    copy_items(r + 0x30, 24, o.outputs, &o.output_count);
    std::memcpy(&o.duration_ns, r + 0x38, 8);
    std::memcpy(&o.required_props, r + 0x48, 8);
}

struct ListCtx {
    uintptr_t reg;
    uint32_t workshop;
    uint32_t* out;
    uint32_t cap, count;
    bool found;
};

void list_one(void* p) {
    auto* c = static_cast<ListCtx*>(p);
    const uintptr_t map = c->reg + static_cast<uintptr_t>(g_workshops);
    const uint64_t* bits = *reinterpret_cast<uint64_t* const*>(map + 0x08);
    const uint64_t cap = *reinterpret_cast<const uint64_t*>(map + 0x10);
    const uint32_t* keys = *reinterpret_cast<uint32_t* const*>(map + 0x28);
    const uint8_t* values = *reinterpret_cast<uint8_t* const*>(map + 0x40);
    c->found = false;
    c->count = 0;
    if (!bits || !keys || !values || !cap || (cap & (cap - 1))) return;
    uint64_t i = c->workshop & (cap - 1);
    for (uint64_t n = 0; n < cap && ((bits[i >> 6] >> (i & 63)) & 1); ++n, i = (i + 1) & (cap - 1)) {
        if (keys[i] != c->workshop) continue;
        const uint8_t* arr = values + i * 0x1008ull;
        uint16_t count;
        std::memcpy(&count, arr + 0x1000, 2);
        c->found = true;
        for (uint32_t k = 0; k < count && k < 512; ++k) {
            const uint8_t* r = *reinterpret_cast<const uint8_t* const*>(arr + k * 8ull);
            if (!r) continue;
            uint32_t ws, id;
            std::memcpy(&ws, r + 0x24, 4);
            std::memcpy(&id, r + 0x10, 4);
            if (ws != c->workshop) continue;  // consistency check of the layout
            if (c->count < c->cap && c->out) c->out[c->count] = id;
            ++c->count;
        }
        return;
    }
}

}  // namespace

void recipes_init() {
    uintptr_t a = 0;
    int64_t ws = 0;
    g_ok = g_api->feature(KilnStr{"gameplay.recipes", 16}) &&
           g_api->symbol(KilnStr{"gameplay/RecipeFind", 19}, &a) == KILN_OK && a;
    if (g_ok) g_find = reinterpret_cast<RecipeFindFn>(a);
    if (g_api->constant(KilnStr{"gameplay/recipes.workshops", 26}, &ws) == KILN_OK) g_workshops = ws;
}

KilnResult recipe(uint32_t id, KilnGameplayRecipe* out) {
    if (!out || out->size < sizeof(KilnGameplayRecipe) || !id) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_ok) return KILN_ERR_UNAVAILABLE;
    const uintptr_t reg = registry();
    if (!reg) return KILN_ERR_UNAVAILABLE;
    KilnGameplayRecipe r{};
    r.size = sizeof r;
    FindCtx c{reg, id, &r, false};
    unsigned long code = 0;
    if (!seh_call(&find_one, &c, &code)) return KILN_ERR_INTERNAL;
    if (!c.found) return KILN_ERR_NOT_FOUND;
    *out = r;
    return KILN_OK;
}

KilnResult workshop_recipes(uint32_t workshop, uint32_t* out, uint32_t capacity, uint32_t* out_count) {
    if (out_count) *out_count = 0;
    if (!g_ok) return KILN_ERR_UNAVAILABLE;
    const uintptr_t reg = registry();
    if (!reg) return KILN_ERR_UNAVAILABLE;
    ListCtx c{reg, workshop, out, capacity, 0, false};
    unsigned long code = 0;
    if (!seh_call(&list_one, &c, &code)) return KILN_ERR_INTERNAL;
    if (!c.found) return KILN_ERR_NOT_FOUND;
    if (out_count) *out_count = c.count;
    return c.count > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

}  // namespace kg
