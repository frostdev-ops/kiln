// SPDX-License-Identifier: MIT
// kiln.content: new items and recipes (Forge's DeferredRegister) on the server and in the game client.
//
// A definition names a fixed GUID (id = FNV-1a over its 16 bytes, as the game computes ItemId / RecipeId), a base
// entry to clone and what differs (debug name, display texts, field values, recipe lists). It comes from code
// (define) or from a data pack's "add" list. For every world that runs in this process (the server world, and a
// client's own world) the service waits for that world's registries to load, then, on that world's thread:
//  - items:   copies the base ItemInfo (the 2088-byte struct; every BlobArray / BlobString in it is rebased so it
//             still points at the base's blob data), sets id / GUID / name tags / debug name / overrides, and inserts
//             id -> clone into the registry's id map the way ItemRegistryUpdate phase 1 does (linear probing, bitset,
//             count). One clone per base ItemInfo, shared by the worlds that load the same resource.
//  - recipes: builds a RecipeInfo with its own input / output / props arrays (from a base recipe or a station's
//             first recipe), inserts it into the id map and appends it to its workshop's list.
// Client only (features content.loca, content.ui_texture, content.recipe_ui): the loca lookup answers the new name /
// description tags; the UI texture cache looks a new item's icon texture up under its base item's GUID; the
// crafting UI's all-recipes getter returns the resource array plus the new recipes (unlocked_recipes_ui iterates
// that array, not the maps).
// Client worlds get the definitions when their item registry finishes loading (hook on ItemRegistryUpdate, loading
// screen), before any step: the prediction world steps first and reads the client world's registry (docs/re/content.md
// section 9). Ids this game has no definition for are answered in the client world by a placeholder ("[Unknown item]",
// hook on ItemRegistryFind): dozens of the game's call sites use the lookup's result unchecked.
// Lifecycle: definitions are queued until the registries load (world load, before the save is read: saved stacks of
// the item survive restarts) and inserted again into every new world. A definition whose owner goes away (mod
// unload) stays in the registries until the process ends (Forge's missing mappings: stacks are never deleted);
// its recipes are taken off their workshop lists (no longer craftable or listed). Defining the same GUID again (a
// reload) adopts it. Evidence: docs/re/content.md section 7.
#include <windows.h>
#include <intrin.h>

#include <algorithm>
#include <chrono>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <optional>
#include <set>

#include "barber.h"
#include "content.h"
#include "nlohmann/json.hpp"
#include "refs.h"
#include "services/kiln_assets.h"
#include "services/kiln_ui.h"  // kiln.craftmenu (the crafting menu extension point, implemented here)
#include "safetyhook.hpp"

namespace kc {

using json = nlohmann::ordered_json;

// kiln.assets (client): PNG icons for items (native/core/abi/services/kiln_assets.h part 1). Looked up once.
const KilnAssetsApi* assets_api() {  // also used by refs.cpp
    static std::atomic<const KilnAssetsApi*> t{nullptr};
    static std::atomic<bool> tried{false};
    static std::atomic<uint64_t> next_try{0};
    if (const KilnAssetsApi* a = t.load(std::memory_order_acquire)) return a;
    if (tried.load(std::memory_order_relaxed) && GetTickCount64() < next_try.load(std::memory_order_relaxed)) return nullptr;
    next_try.store(GetTickCount64() + 2000, std::memory_order_relaxed);  // not there (a server, or not loaded yet): retry
    tried.store(true, std::memory_order_relaxed);
    const auto* a = static_cast<const KilnAssetsApi*>(
        g_api->service(KilnStr{KILN_ASSETS_SERVICE, std::strlen(KILN_ASSETS_SERVICE)}, 1));
    if (a && a->header.size < offsetof(KilnAssetsApi, icon_state) + sizeof(void*)) a = nullptr;
    t.store(a, std::memory_order_release);
    return a;
}


namespace {

// ============================================================================================= engine ==========
struct NcEngine {
    bool on = false;  // content.new
    int64_t ws_map = 0, ws_stride = 0, ws_count = 0, ws_max = 0;
    bool loca = false, uitex = false, recipe_ui = false;
    uintptr_t loca_fn = 0, uitex_fn = 0, recipe_all_fn = 0;
    int64_t loca_special = 0x18, uitex_type = 0x10, all_array = 8;
    bool ui_refresh = false;
    bool menu = false;  // content.recipe_menu: the crafting menu's per-workshop category lists (client)
    uintptr_t ui_app = 0;
    int64_t app_config = 0, menu_ws = 0, cat_stride = 0x38, cat_subs = 0x30, sub_stride = 0x28, sub_recipes = 0x20;
    int64_t ui_version = 0;  // G38UiPlayData: the knowledge version unlocked_recipes_ui compares (u32)
    bool loot = false;       // content.loot_new: the random-loot candidate getter (LootItems) is hooked
    uintptr_t loot_items_fn = 0;
    int64_t lootreg_data = 0x38, loot_items = 0x14;
} N;

// Item / recipe layouts (from reflection, built on first use).
struct Layouts {
    bool ok = false;
    std::string why;
    Schema item, recipe;
    std::vector<uint32_t> item_relocs, recipe_relocs;
    uint32_t i_id = 0, i_guid = 4, i_name = 0x48, i_desc = 0x68, i_lore = 0x78, i_dbg = 0x820;
    uint32_t r_guid = 0, r_id = 0x10, r_wguid = 0x14, r_wid = 0x24, r_in = 0x28, r_out = 0x30, r_dur = 0x38,
             r_props = 0x40, r_mask = 0x48, r_know = 0x50, r_done = 0x5c, r_name = 0x68, r_dbg = 0x78;
    uint32_t know_size = 12;
    uint32_t in_stride = 48, in_ref = 0, in_item = 0x10, in_count = 0x14, in_cat_ref = 0x18, in_cat = 0x28,
             in_cat_count = 0x2C;
    uint32_t out_stride = 24, out_ref = 0, out_item = 0x10, out_count = 0x14;
    uint32_t prop_size = 4;
    std::vector<std::string> zero_without_base;  // numeric requirements reset when a station's recipe is the template
} L;

uint32_t special_of(const Schema& s, const char* name, bool* ok) {
    auto it = s.special.find(name);
    if (it == s.special.end()) {
        *ok = false;
        return 0;
    }
    return it->second;
}

bool layouts() {
    static bool tried = false;
    if (tried) return L.ok;
    tried = true;
    std::string err;
    if (!build_schema("keen::ItemInfo", {"itemId", "objectId", "flags"}, &L.item, &err) ||
        !build_schema("keen::RecipeInfo", {"recipeGuid", "recipeId", "workshopGuid", "workshopId", "requiredPropsMask"},
                      &L.recipe, &err) ||
        !self_relative_fields("keen::ItemInfo", &L.item_relocs, &err) ||
        !self_relative_fields("keen::RecipeInfo", &L.recipe_relocs, &err)) {
        L.why = err;
        return false;
    }
    bool ok = true;
    L.i_id = special_of(L.item, "itemId", &ok), L.i_guid = special_of(L.item, "objectId", &ok);
    L.i_name = special_of(L.item, "name", &ok), L.i_desc = special_of(L.item, "description", &ok);
    L.i_lore = special_of(L.item, "lore", &ok), L.i_dbg = special_of(L.item, "debugName", &ok);
    const Schema& R = L.recipe;
    L.r_guid = special_of(R, "recipeGuid", &ok), L.r_id = special_of(R, "recipeId", &ok);
    L.r_wguid = special_of(R, "workshopGuid", &ok), L.r_wid = special_of(R, "workshopId", &ok);
    L.r_in = special_of(R, "input", &ok), L.r_out = special_of(R, "output", &ok);
    L.r_dur = special_of(R, "craftingDuration", &ok), L.r_props = special_of(R, "requiredProps", &ok);
    L.r_mask = special_of(R, "requiredPropsMask", &ok), L.r_know = special_of(R, "knowledgeRequirement", &ok);
    L.r_done = special_of(R, "completionRequirementQuery", &ok), L.r_name = special_of(R, "recipeName", &ok);
    L.r_dbg = special_of(R, "debugName", &ok);
    auto off = [&](const char* t, const char* f) {
        const int64_t o = field_offset(t, f);
        if (o < 0) ok = false;
        return static_cast<uint32_t>(o < 0 ? 0 : o);
    };
    const uint32_t stack = off("keen::RecipeInputResource", "itemStack"),
                   cat = off("keen::RecipeInputResource", "inputItemCategory");
    const uint32_t ref = off("keen::RecipeItemStackResource", "itemRef"), item = off("keen::RecipeItemStackResource", "item"),
                   count = off("keen::RecipeItemStackResource", "count");
    const uint32_t cref = off("keen::RecipeInputCategoryResource", "categoryRef"),
                   ccat = off("keen::RecipeInputCategoryResource", "category"),
                   ccount = off("keen::RecipeInputCategoryResource", "count");
    L.in_stride = type_size("keen::RecipeInputResource");
    L.out_stride = type_size("keen::RecipeItemStackResource");
    L.know_size = type_size("keen::KnowledgeQueryData");
    L.prop_size = type_size("keen::CraftingPropId");
    L.in_ref = stack + ref, L.in_item = stack + item, L.in_count = stack + count;
    L.in_cat_ref = cat + cref, L.in_cat = cat + ccat, L.in_cat_count = cat + ccount;
    L.out_ref = ref, L.out_item = item, L.out_count = count;
    if (!ok || L.item.size < 0x100 || L.recipe.size < 0x80 || L.in_stride < 32 || L.out_stride < 20 ||
        L.know_size == 0 || L.know_size > 32 || L.prop_size == 0 || L.prop_size > 16) {
        L.why = "reflection: an item / recipe field the new-content code needs is missing";
        return false;
    }
    L.zero_without_base = {"level", "comfortLevel", "serverProgressLevel", "requiredHappyNpcCount", "requiredEnergy",
                           "waterAmount", "waterCharges", "isCookingRecipe", "isUpgrade", "requiresSheltered",
                           "showIsImportantLabel"};
    L.ok = true;
    LOGI("new content: keen::ItemInfo %u bytes (%zu blob fields rebased per clone), keen::RecipeInfo %u bytes (%zu), "
         "input %u B, output %u B",
         L.item.size, L.item_relocs.size(), L.recipe.size, L.recipe_relocs.size(), L.in_stride, L.out_stride);
    return true;
}

// ============================================================================================= game maps =======
// The engine's HashMap<u32, V> (bits, capacity, keys, values, count at E.hm_*; values of `vsize` bytes): linear
// probing from key & (cap - 1), stop at the first free slot (docs/re/content.md section 7). All SEH-guarded.
struct MapView {
    uint64_t cap = 0;
    uint32_t count = 0;
    uint64_t* bits = nullptr;
    uint32_t* keys = nullptr;
    uint8_t* values = nullptr;
};
bool map_view(uintptr_t map, MapView* m) {
    uintptr_t b = 0, k = 0, v = 0;
    if (!rdv(map + E.hm_cap, m->cap) || !rdv(map + E.hm_count, m->count) || !rdv(map + E.hm_bits, b) ||
        !rdv(map + E.hm_keys, k) || !rdv(map + E.hm_values, v))
        return false;
    m->bits = reinterpret_cast<uint64_t*>(b), m->keys = reinterpret_cast<uint32_t*>(k), m->values = reinterpret_cast<uint8_t*>(v);
    return m->cap && (m->cap & (m->cap - 1)) == 0 && m->cap <= (1ull << 24) && m->count <= m->cap && b && k && v;
}

// Slot of key, or (not found) the first free slot on its probe path: *found tells which. -1: unreadable / full.
int64_t map_slot(const MapView& m, uint32_t key, bool* found) {
    *found = false;
    __try {
        uint64_t i = key & (m.cap - 1);
        for (uint64_t n = 0; n < m.cap; ++n) {
            if (!((m.bits[i >> 6] >> (i & 63)) & 1)) return static_cast<int64_t>(i);
            if (m.keys[i] == key) {
                *found = true;
                return static_cast<int64_t>(i);
            }
            i = i + 1 < m.cap ? i + 1 : 0;
        }
        return -1;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}

bool map_write(uintptr_t map, const MapView& m, int64_t slot, uint32_t key, uint64_t value) {
    __try {
        // value, key, then the bit and the count: a reader on another thread sees the slot only when it is complete
        reinterpret_cast<uint64_t*>(m.values)[slot] = value;
        m.keys[slot] = key;
        _InterlockedOr64(reinterpret_cast<volatile long long*>(&m.bits[slot >> 6]), static_cast<long long>(1ull << (slot & 63)));
        *reinterpret_cast<volatile uint32_t*>(map + E.hm_count) = m.count + 1;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool set_u64(uint64_t* p, uint64_t v) {
    __try {
        *reinterpret_cast<volatile uint64_t*>(p) = v;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool set_u32(uintptr_t p, uint32_t v) {
    __try {
        *reinterpret_cast<volatile uint32_t*>(p) = v;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

uint64_t map_get(uintptr_t map, uint32_t key) {
    MapView m;
    bool found = false;
    if (!map_view(map, &m)) return 0;
    const int64_t s = map_slot(m, key, &found);
    uint64_t v = 0;
    if (s >= 0 && found) rdv(reinterpret_cast<uintptr_t>(m.values) + static_cast<uintptr_t>(s) * 8, v);
    return v;
}

// 1 inserted, 2 already this value, 3 the key holds another value (*other), 4 replaced (replace = true), 0 failure
// (*no_room: for good, the map is as full as the game lets it be).
int map_put(uintptr_t map, uint32_t key, uint64_t value, bool replace, uint64_t* other, std::string* why, bool* no_room = nullptr) {
    MapView m;
    if (!map_view(map, &m)) {
        *why = "the map is not readable";
        return 0;
    }
    bool found = false;
    const int64_t s = map_slot(m, key, &found);
    if (s < 0) {
        *why = "the map is full";
        if (no_room) *no_room = true;
        return 0;
    }
    if (found) {
        uint64_t v = 0;
        rdv(reinterpret_cast<uintptr_t>(m.values) + static_cast<uintptr_t>(s) * 8, v);
        if (v == value) return 2;
        if (other) *other = v;
        if (!replace) return 3;
        return set_u64(reinterpret_cast<uint64_t*>(m.values) + s, value) ? 4 : 0;
    }
    if (static_cast<uint64_t>(m.count) + 1 > m.cap / 2) {  // the game reserves 2 x next_pow2(n): keep its load factor
        *why = "the map has no room left (count " + std::to_string(m.count) + " of " + std::to_string(m.cap) + ")";
        if (no_room) *no_room = true;
        return 0;
    }
    if (!map_write(map, m, s, key, value)) {
        *why = "the map write faulted";
        return 0;
    }
    return 1;
}

// A BlobArray {i32 self-relative offset, u32 count} in game memory.
struct BlobRef {
    uintptr_t data = 0;
    uint32_t count = 0;
};
BlobRef blob_at(uintptr_t field) {
    BlobRef b;
    int32_t off = 0;
    uint32_t n = 0;
    if (!rdv(field, off) || !rdv(field + 4, n) || n > 100000) return b;
    b.count = n;
    b.data = off ? field + static_cast<intptr_t>(off) : 0;
    if (!b.data) b.count = 0;
    return b;
}

// The workshop map (+N.ws_map): values are SmallDynamicArray<RecipeInfo*, ws_max> of ws_stride bytes, u16 count at
// +ws_count. Returns the array of `workshop`, 0 when the workshop has no list.
uintptr_t ws_array(uintptr_t rreg, uint32_t workshop) {
    MapView m;
    bool found = false;
    if (!map_view(rreg + N.ws_map, &m)) return 0;
    const int64_t s = map_slot(m, workshop, &found);
    if (s < 0 || !found) return 0;
    return reinterpret_cast<uintptr_t>(m.values) + static_cast<uintptr_t>(s) * static_cast<uintptr_t>(N.ws_stride);
}

// The list of a workshop the game's data doesn't know: a new entry of the workshop map. The game reserves it once at load
// (0x923700: 0x40 asked, so 0x80 slots of keys and values, 46 taken) and never inserts after that, so its threshold of half
// the slots doesn't apply here. A quarter stays free so that a lookup, which stops at the first clear bit, stays short.
uintptr_t ws_create(uintptr_t rreg, uint32_t workshop, std::string* why) {
    const uintptr_t map = rreg + N.ws_map;
    MapView m;
    if (!map_view(map, &m)) {
        *why = "the workshop map is not readable";
        return 0;
    }
    bool found = false;
    const int64_t s = map_slot(m, workshop, &found);
    if (s < 0) {
        *why = "the workshop map is full";
        return 0;
    }
    const uintptr_t arr = reinterpret_cast<uintptr_t>(m.values) + static_cast<uintptr_t>(s) * static_cast<uintptr_t>(N.ws_stride);
    if (found) return arr;
    if (static_cast<uint64_t>(m.count) + 1 > m.cap * 3 / 4) {
        *why = "the game has room for " + std::to_string(m.cap * 3 / 4) + " workshops, and all " + std::to_string(m.count) + " are taken";
        return 0;
    }
    __try {
        // the count, the key, then the bit and the map's count: a reader on another thread sees the slot only when it is complete
        *reinterpret_cast<volatile uint16_t*>(arr + N.ws_count) = 0;
        m.keys[s] = workshop;
        _InterlockedOr64(reinterpret_cast<volatile long long*>(&m.bits[s >> 6]), static_cast<long long>(1ull << (s & 63)));
        *reinterpret_cast<volatile uint32_t*>(map + E.hm_count) = m.count + 1;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *why = "the workshop map write faulted";
        return 0;
    }
    return arr;
}

// 1 appended, 2 already there, 0 failure
int ws_add(uintptr_t arr, uint64_t ptr, std::string* why) {
    uint16_t n = 0;
    if (!rdv(arr + N.ws_count, n)) {
        *why = "the workshop list is not readable";
        return 0;
    }
    for (uint16_t i = 0; i < n; ++i) {
        uint64_t p = 0;
        if (rdv(arr + i * 8ull, p) && p == ptr) return 2;
    }
    if (n >= N.ws_max) {
        *why = "the workshop list is full (" + std::to_string(n) + ")";
        return 0;
    }
    if (!set_u64(reinterpret_cast<uint64_t*>(arr) + n, ptr)) return 0;
    __try {
        *reinterpret_cast<volatile uint16_t*>(arr + N.ws_count) = static_cast<uint16_t>(n + 1);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
    return 1;
}

// Replaces `old_ptr` by `new_ptr` (new_ptr 0: removes it, shifting the rest down). true when it was there.
bool ws_swap(uintptr_t arr, uint64_t old_ptr, uint64_t new_ptr) {
    uint16_t n = 0;
    if (!rdv(arr + N.ws_count, n)) return false;
    for (uint16_t i = 0; i < n; ++i) {
        uint64_t p = 0;
        if (!rdv(arr + i * 8ull, p) || p != old_ptr) continue;
        if (new_ptr) return set_u64(reinterpret_cast<uint64_t*>(arr) + i, new_ptr);
        for (uint16_t j = i; j + 1 < n; ++j) {
            uint64_t q = 0;
            rdv(arr + (j + 1) * 8ull, q);
            set_u64(reinterpret_cast<uint64_t*>(arr) + j, q);
        }
        __try {
            *reinterpret_cast<volatile uint16_t*>(arr + N.ws_count) = static_cast<uint16_t>(n - 1);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            return false;
        }
        set_u64(reinterpret_cast<uint64_t*>(arr) + (n - 1), 0);
        return true;
    }
    return false;
}

// ============================================================================================= texts ===========
// Loca tag id -> text, read by the loca hooks on the UI thread: an immutable map behind an atomic pointer, replaced
// on change (old maps, entries and every string are kept for the life of the process: the UI may hold the views).
// A tag has one text per game language (keen::LanguageId, the LocaPool's current language at +loca.language) and a
// fallback; definitions give texts per language key ("en", "de", "pt-BR", "*" = every language), resolved per game
// language when the texts are set: exact key ("pt-br"), then the primary language ("pt"), then "en", then "*".
struct TextView {
    const char* p;
    uint64_t n;
    const uint8_t* rec;  // a keen::LocaTagResource {id, text BlobString, arguments {0,0}, genericArguments 0} + text
};
struct TextEntry {
    TextView all{};                  // the fallback (no language table, or a language without its own text)
    std::vector<TextView> by_lang;   // index = keen::LanguageId value; p == nullptr: use `all`
};
using TextMap = std::unordered_map<uint32_t, const TextEntry*>;
using TextSet = std::map<std::string, std::string>;  // language key -> text
// Copy on write, so that the hooks read without a lock; in shards, so that a change copies a small map: thousands of definitions each
// set their texts. Old maps are never freed (a reader may hold one).
constexpr size_t kShards = 256;
std::atomic<const TextMap*> g_texts[kShards] = {};
std::atomic<const TextMap*>& text_shard(uint32_t tag) { return g_texts[tag & (kShards - 1)]; }
std::mutex g_text_mu;
std::deque<std::string> g_text_arena;
// loca hook diagnostics (client)
std::atomic<uint64_t> g_loca_calls{0}, g_loca_hits{0};
std::atomic<bool> g_loca_trace{false};
std::mutex g_trace_mu;
std::vector<uint32_t> g_trace_ids;

// keen::LanguageId value -> our key ("en-us"); from the game's reflection (names like "En_Us", "De_De", "Zh_Cn").
std::vector<std::string> g_lang_keys;
bool g_lang_tried = false;

void load_languages() {
    if (g_lang_tried) return;
    g_lang_tried = true;
    std::vector<std::pair<std::string, int64_t>> vals;
    if (!enum_values("keen::LanguageId", &vals) || vals.empty()) {
        LOGW("new content: keen::LanguageId is not in the game's reflection: every language shows the same text");
        return;
    }
    std::string list;
    for (auto& [name, v] : vals) {
        if (v < 0 || v > 255 || lower(name) == "count") continue;
        if (g_lang_keys.size() <= static_cast<size_t>(v)) g_lang_keys.resize(static_cast<size_t>(v) + 1);
        std::string k = lower(name);
        for (char& c : k)
            if (c == '_') c = '-';
        g_lang_keys[static_cast<size_t>(v)] = k;
        list += " " + std::to_string(v) + "=" + k;
    }
    LOGI("new content: game languages (keen::LanguageId):%s", list.c_str());
}

// Normalizes a language key: "pt_BR" -> "pt-br"; "*" stays.
std::string lang_key(std::string k) {
    k = lower(k);
    for (char& c : k)
        if (c == '_') c = '-';
    return k;
}

bool valid_lang(const std::string& k) {
    if (k == "*") return true;
    if (k.size() < 2 || k.size() > 12) return false;
    for (char c : k)
        if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) return false;
    return k[0] != '-';
}

const std::string* pick_text(const TextSet& t, const std::string& game_key) {
    if (!game_key.empty()) {
        if (auto it = t.find(game_key); it != t.end()) return &it->second;
        const size_t dash = game_key.find('-');
        if (dash != std::string::npos)
            if (auto it = t.find(game_key.substr(0, dash)); it != t.end()) return &it->second;
        // keenglish (Keen's source strings): treated as English
        if (game_key == "keenglish" || game_key == "invalid" || game_key == "none")
            if (auto it = t.find("en"); it != t.end()) return &it->second;
    }
    if (auto it = t.find("en"); it != t.end()) return &it->second;
    if (auto it = t.find("*"); it != t.end()) return &it->second;
    return nullptr;
}

TextView make_view(uint32_t tag, const std::string& text) {
    g_text_arena.push_back(text);
    // the record the UI asks for first (LocaRecord): 24 bytes, the text right behind it (self-relative BlobString)
    auto* rec = static_cast<uint8_t*>(std::calloc(1, 24 + text.size() + 1));
    if (rec) {
        const int32_t off = 24 - 4;
        const uint32_t len = static_cast<uint32_t>(text.size());
        std::memcpy(rec, &tag, 4);
        std::memcpy(rec + 4, &off, 4);
        std::memcpy(rec + 8, &len, 4);
        std::memcpy(rec + 24, text.data(), text.size());
    }
    return TextView{g_text_arena.back().c_str(), g_text_arena.back().size(), rec};
}

bool same_view(const TextView& v, const std::string* s) {
    if (!s) return v.p == nullptr;
    return v.p && v.n == s->size() && std::memcmp(v.p, s->data(), s->size()) == 0;
}

// Sets the texts of one tag (empty set: nothing). Unchanged texts keep their entry. Texts in some languages only, with no "en" or "*" (a German
// description alone): those languages get theirs and the rest are left to the game, which has no text for the tag, so they show none.
void set_texts(uint32_t tag, const TextSet& texts) {
    if (!tag || texts.empty()) return;
    PhaseTimer pt(PH_TEXTS);
    std::lock_guard lock(g_text_mu);
    load_languages();
    const std::string* all = pick_text(texts, "");  // the fallback; null: none
    if (!all && g_lang_keys.empty()) return;        // no language table to put the others in
    std::atomic<const TextMap*>& shard = text_shard(tag);
    const TextMap* cur = shard.load();
    if (cur) {
        auto it = cur->find(tag);
        if (it != cur->end()) {
            const TextEntry& e = *it->second;
            bool same = same_view(e.all, all) && e.by_lang.size() == g_lang_keys.size();
            for (size_t i = 0; same && i < g_lang_keys.size(); ++i) {
                const std::string* s = pick_text(texts, g_lang_keys[i]);
                same = s == all ? e.by_lang[i].p == nullptr : same_view(e.by_lang[i], s);
            }
            if (same) return;
        }
    }
    auto* e = new TextEntry;
    if (all) e->all = make_view(tag, *all);
    e->by_lang.resize(g_lang_keys.size(), TextView{nullptr, 0, nullptr});
    bool any = all != nullptr;
    for (size_t i = 0; i < g_lang_keys.size(); ++i) {
        const std::string* s = pick_text(texts, g_lang_keys[i]);
        if (s && s != all && (!all || *s != *all)) {
            e->by_lang[i] = make_view(tag, *s);
            any = true;
        }
    }
    if (!any) {  // no game language picks any of these keys
        delete e;
        return;
    }
    auto* next = new TextMap(cur ? *cur : TextMap{});
    (*next)[tag] = e;
    shard.store(next);
}

void set_text(uint32_t tag, const std::string& text) {
    if (!text.empty()) set_texts(tag, TextSet{{"*", text}});
}

// The text a hook answers for `tag` in the pool's current language (nullptr: not one of ours).
int64_t g_loca_lang_off = 0;  // LocaPool +0xd8 on 26404966 (content/loca.language), 0 = unknown
const TextView* text_for(uint32_t tag, const void* pool) {
    const TextMap* m = text_shard(tag).load(std::memory_order_acquire);
    if (!m) return nullptr;
    auto it = m->find(tag);
    if (it == m->end()) return nullptr;
    const TextEntry& e = *it->second;
    if (g_loca_lang_off && pool && !e.by_lang.empty()) {
        const uint32_t lang = *reinterpret_cast<const uint32_t*>(static_cast<const uint8_t*>(pool) + g_loca_lang_off);
        if (lang < e.by_lang.size() && e.by_lang[lang].p) return &e.by_lang[lang];
    }
    return e.all.p ? &e.all : nullptr;  // no text in this language and no fallback: the game's answer (none)
}

// UI texture GUID aliases: new item GUID -> base item GUID (same scheme).
struct GuidKey {
    uint64_t a, b;
    bool operator==(const GuidKey& o) const { return a == o.a && b == o.b; }
};
struct GuidHash {
    size_t operator()(const GuidKey& k) const { return std::hash<uint64_t>()(k.a ^ (k.b * 0x9E3779B97F4A7C15ull)); }
};
using AliasMap = std::unordered_map<GuidKey, GuidKey, GuidHash>;
std::atomic<const AliasMap*> g_alias[kShards] = {};  // as the texts: copy on write, in shards
std::atomic<bool> g_any_alias{false};
std::atomic<const AliasMap*>& alias_shard(const GuidKey& k) { return g_alias[k.a & (kShards - 1)]; }
GuidKey gk(const uint8_t g[16]) {
    GuidKey k;
    std::memcpy(&k.a, g, 8);
    std::memcpy(&k.b, g + 8, 8);
    return k;
}
void set_alias(const uint8_t from[16], const uint8_t to[16]) {
    PhaseTimer pt(PH_TEXTS);
    std::lock_guard lock(g_text_mu);
    std::atomic<const AliasMap*>& shard = alias_shard(gk(from));
    const AliasMap* cur = shard.load();
    if (cur) {
        auto it = cur->find(gk(from));
        if (it != cur->end() && it->second == gk(to)) return;
    }
    auto* next = new AliasMap(cur ? *cur : AliasMap{});
    (*next)[gk(from)] = gk(to);
    shard.store(next);
    g_any_alias.store(true);
}

// A derived GUID for a definition's name / description / lore tag (stable for a GUID, distinct per purpose).
void derive_guid(const uint8_t guid[16], const char* purpose, uint8_t out[16]) {
    std::memcpy(out, guid, 16);
    const uint32_t h = fnv1a(purpose, std::strlen(purpose));
    for (int i = 0; i < 4; ++i) out[i] ^= static_cast<uint8_t>(h >> (8 * i));
    out[15] ^= 0x4B;  // 'K'
}

// ============================================================================================= definitions =====
struct StackSpec {
    std::string item;   // item selector (debug name, 0x id, GUID) or empty for a category
    uint32_t category = 0;
    uint32_t count = 1;
};

// The text slots of an item (ItemInfo LocaTag references; recipes use slot 0 for their recipe name).
constexpr int kTextSlots = 8;
const char* const kTextKeys[kTextSlots] = {"name", "caption", "description", "lore", "acquisitionHint",
                                           "primaryAction", "secondaryAction", "tertiaryAction"};
const char* const kTextPaths[kTextSlots] = {"name", "caption", "description", "lore", "acquisitionHint",
                                            "uiActionHints.primaryAction", "uiActionHints.secondaryAction",
                                            "uiActionHints.tertiaryAction"};
// tag GUID purposes (name / description / lore keep the derivation of the first release)
const char* const kTextPurpose[kTextSlots] = {"kiln.item.name", "kiln.item.caption", "kiln.item.description",
                                              "kiln.item.lore", "kiln.item.acquisitionHint", "kiln.item.primaryAction",
                                              "kiln.item.secondaryAction", "kiln.item.tertiaryAction"};

struct Def {
    uint32_t kind = 0;  // KILN_CONTENT_ITEMS / RECIPES
    uint8_t guid[16] = {};
    uint32_t id = 0;
    std::string guid_s, name, base, station;
    uint32_t menu_workshop = 0;  // recipes: "menu": the workshop whose crafting menu lists it, when that is not its own
    std::string display;  // recipes: the recipe name; items: texts["*"].name
    TextSet texts[kTextSlots];  // items: per slot, language key -> text ("*" = displayName / description / lore)
    json fields = json::object();
    json kitbash = json::object();  // items: reference keys (model, icon, perks, weaponType, refs, ...): refs.cpp
    json distribution = json::object();  // items: loot / drops / knowledge (distribution.cpp)
    std::vector<StackSpec> inputs, outputs;
    double duration = -1;  // recipes: seconds, < 0 = the template's
    bool unlock_base = false;
    KilnOwner owner = 0;
    std::string owner_label, source, text;  // text: the normalized definition (to see whether a redefinition changes it)
    std::string mod;                        // the owning mod's id ("mod <id>" / "pack <id>" owners)
    json body = json::object();             // the normalized definition (sync to Kiln clients, placeholder records)
    bool pack = false, seen = true, orphan = false;
    bool placeholder = false;  // "[Missing: mod:item]": keeps saved stacks of an item whose mod is gone (A4)
    bool internal = false;     // Kiln's own "[Unknown item]" template: client world only, in no list a mod or the sync sees
    uint64_t version = 0;
    uint8_t tag_guid[kTextSlots][16] = {};
    uint32_t tags[kTextSlots] = {};
    // items: clones per base ItemInfo (built for this version); recipes: the built RecipeInfo
    std::unordered_map<uintptr_t, uintptr_t> clones;
    uintptr_t info = 0;
    uint64_t info_version = ~0ull;
    uintptr_t loot_entry = 0;           // its LootableItemInfo (a copy of `like`'s), in the loot map and the rolls
    uint64_t loot_version = ~0ull;
    uint8_t base_guid[16] = {};
    uint8_t icon_guid[16] = {};  // the GUID the UI's icon texture is looked up under (base, or "icon")
    std::string icon_file;       // "icon": "<file>.png": registered with kiln.assets (client) under the item's GUID
    std::string icon_problem;
    std::string error;           // the last failure (one per version and world is logged)
    std::vector<std::string> warnings;  // parts of the definition that were skipped (kitbash problems, ...)
    std::vector<std::string> gear_notes;  // the definition's own choices that break what the recipe tooltip promises
    bool rolled = false;                  // items: "rolledLoot": keeps the base's generateRarity and level range
    bool kitbash_retry = false;         // a reference was not loaded yet: tried again (kiln.assets may serve it later)
    bool no_description = false;  // "description": "": none, rather than the base's
    size_t at = 0;                // its place in g_defs
    std::string listed;           // its entry in definitions(), as of listed_as (a fingerprint of what the entry shows)
    uint64_t listed_as = 0;
    bool has_text(int slot) const { return !texts[slot].empty(); }
};
using DefPtr = std::shared_ptr<Def>;

// Definitions and clones share store_mutex(). A lock of their own deadlocked: the client world's setup held it and waited
// for the store, while a job thread's first drop table held the store and waited for it.
std::vector<DefPtr> g_defs;         // in definition order
std::atomic<uint64_t> g_version{1};  // +1 per change of the definitions

// The definitions by GUID, name and id (under store_mutex), so that a lookup costs the same however many there are: registering
// thousands looks up each one's base and stacks. g_defs only grows, and a definition keeps its kind, GUID and place; a
// redefinition may change its name.
struct DefIndex {
    std::unordered_map<std::string, DefPtr> guid;                    // kind + GUID bytes
    std::unordered_map<std::string, DefPtr> guid_text;               // items: the GUID as text
    std::unordered_map<std::string, std::vector<DefPtr>> name;       // kind + lower-case name, oldest first
    std::unordered_map<uint64_t, std::vector<DefPtr>> id;            // kind << 32 | id, oldest first
    static std::string key(uint32_t kind, const std::string& s) { return static_cast<char>('0' + kind) + s; }
    static std::string key(uint32_t kind, const uint8_t g[16]) { return key(kind, std::string(reinterpret_cast<const char*>(g), 16)); }
    void add_name(const DefPtr& d) {
        auto& v = name[key(d->kind, lower(d->name))];
        v.insert(std::upper_bound(v.begin(), v.end(), d, [](const DefPtr& a, const DefPtr& b) { return a->at < b->at; }), d);
    }
    void add(const DefPtr& d) {
        guid[key(d->kind, d->guid)] = d;
        if (d->kind == KILN_CONTENT_ITEMS) guid_text.emplace(d->guid_s, d);
        add_name(d);
        id[(uint64_t(d->kind) << 32) | d->id].push_back(d);
    }
    void rename(const DefPtr& d, const std::string& from) {
        if (lower(from) == lower(d->name)) return;
        if (auto it = name.find(key(d->kind, lower(from))); it != name.end()) {
            std::erase(it->second, d);
            if (it->second.empty()) name.erase(it);
        }
        add_name(d);
    }
    const std::vector<DefPtr>* named(uint32_t kind, const std::string& n) const {
        auto it = name.find(key(kind, lower(n)));
        return it == name.end() ? nullptr : &it->second;
    }
    const std::vector<DefPtr>* with_id(uint32_t kind, uint32_t i) const {
        auto it = id.find((uint64_t(kind) << 32) | i);
        return it == id.end() ? nullptr : &it->second;
    }
} g_index;

DefPtr find_def(uint32_t kind, const uint8_t guid[16]) {
    auto it = g_index.guid.find(DefIndex::key(kind, guid));
    return it == g_index.guid.end() ? nullptr : it->second;
}

// ============================================================================================= per world =======
struct NameRec {
    uint32_t id;
    uint8_t guid[16];
    uintptr_t info;
};
struct WorldState {
    uintptr_t world = 0;
    uint32_t session = 0;
    uint64_t done = 0;  // the definition version fully handled in this world
    uintptr_t ireg = 0, rreg = 0;
    bool names_built = false;
    std::unordered_map<std::string, NameRec> names;   // lower-case debug name -> item
    uint64_t names_gen = 0;                           // +1 per change of names
    std::vector<uintptr_t> all;                       // world_items() as of all_gen
    uint64_t all_gen = ~0ull;
    std::unordered_map<uint32_t, uintptr_t> items;    // id -> the ItemInfo we put into this world's map
    std::unordered_map<uint32_t, uintptr_t> recipes;  // id -> RecipeInfo in the map
    std::unordered_map<uint32_t, uintptr_t> listed;   // id -> RecipeInfo in its workshop list
    std::unordered_map<uint32_t, uint32_t> recipe_ws; // id -> workshop of the listed RecipeInfo
    std::unordered_map<uint32_t, std::vector<uint8_t>> categories;  // input category id -> GUID
    std::set<std::string> logged;                     // failures logged for this world
    uint64_t steps = 0;
    bool early = false;          // bound while the game set the world up; session = the one its first step will start
    uint64_t bound_ms = 0;       // when (GetTickCount64)
    uint64_t registered_ms = 0;  // when every definition was first in (0: not yet)
};
WorldState g_ws[KILN_WORLD_KINDS];
std::mutex g_ui_mu;  // the recipe-UI hook reads the client / server state from the UI thread

const char* wname(uint32_t k) { return k == KILN_WORLD_CLIENT ? "client" : k == KILN_WORLD_SERVER ? "server" : "prediction"; }

void reset_ws(uint32_t kind, uintptr_t world, uint32_t session) {
    std::lock_guard lock(g_ui_mu);
    g_ws[kind] = WorldState{};
    g_ws[kind].world = world;
    g_ws[kind].session = session;
}

bool items_loaded(uintptr_t ireg) {
    uint8_t loading = 1, started = 0;
    if (!rdv(ireg + E.items_loading, loading) || !rdv(ireg + E.items_started, started)) return false;
    return !loading && started && map_count(ireg + E.items_map) > 0;
}

bool recipes_loaded(uintptr_t rreg) {
    uintptr_t res = 0;
    return rdv(rreg + E.recipes_resource, res) && res && map_count(rreg + E.recipes_map) > 0;
}

void build_names(WorldState& ws) {
    if (ws.names_built) return;
    std::vector<uint32_t> keys;
    std::vector<uint64_t> vals;
    if (!map_read(ws.ireg + E.items_map, &keys, &vals)) return;
    for (size_t i = 0; i < keys.size(); ++i) {
        NameRec r{keys[i], {}, static_cast<uintptr_t>(vals[i])};
        if (!r.info || !rd(r.info + L.i_guid, r.guid, 16)) continue;
        const std::string n = read_blob_string(r.info + L.i_dbg);
        if (!n.empty()) ws.names.emplace(lower(n), r);
    }
    ws.names_built = true;
    ++ws.names_gen;
}

// An item selector -> id / GUID / ItemInfo in this world: a definition's name or GUID, a debug name, "0x" id, GUID.
bool resolve_item(WorldState& ws, const std::string& sel, NameRec* out) {
    PhaseTimer pt(PH_RESOLVE_ITEM);
    uint8_t g[16];
    {
        // the oldest definition with that name or GUID
        std::lock_guard lock(store_mutex());
        const std::string low = lower(sel);
        DefPtr d;
        if (const auto* v = g_index.named(KILN_CONTENT_ITEMS, low)) d = v->front();
        if (auto it = g_index.guid_text.find(low); it != g_index.guid_text.end() && (!d || it->second->at < d->at)) d = it->second;
        if (d) {
            out->id = d->id;
            std::memcpy(out->guid, d->guid, 16);
            out->info = static_cast<uintptr_t>(map_get(ws.ireg + E.items_map, d->id));
            return true;
        }
    }
    build_names(ws);
    if (auto it = ws.names.find(lower(sel)); it != ws.names.end()) {
        *out = it->second;
        return true;
    }
    uint32_t id = 0;
    if (sel.size() == 36 && guid_parse(sel, g)) id = fnv1a(g, 16);
    else if (sel.size() > 2 && sel[0] == '0' && (sel[1] == 'x' || sel[1] == 'X')) id = static_cast<uint32_t>(std::strtoul(sel.c_str() + 2, nullptr, 16));
    if (!id) return false;
    const uintptr_t info = static_cast<uintptr_t>(map_get(ws.ireg + E.items_map, id));
    if (!info || !rd(info + L.i_guid, out->guid, 16)) return false;
    out->id = id;
    out->info = info;
    return true;
}

// A recipe selector -> RecipeInfo* in this world: debug name (scan), "0x" id, GUID.
uintptr_t resolve_recipe(WorldState& ws, const std::string& sel) {
    uint8_t g[16];
    uint32_t id = 0;
    if (sel.size() == 36 && guid_parse(sel, g)) id = fnv1a(g, 16);
    else if (sel.size() > 2 && sel[0] == '0' && (sel[1] == 'x' || sel[1] == 'X')) id = static_cast<uint32_t>(std::strtoul(sel.c_str() + 2, nullptr, 16));
    if (id) return static_cast<uintptr_t>(map_get(ws.rreg + E.recipes_map, id));
    std::vector<uint32_t> keys;
    std::vector<uint64_t> vals;
    if (!map_read(ws.rreg + E.recipes_map, &keys, &vals)) return 0;
    const std::string want = lower(sel);
    for (size_t i = 0; i < keys.size(); ++i)
        if (lower(read_blob_string(static_cast<uintptr_t>(vals[i]) + L.r_dbg)) == want) return static_cast<uintptr_t>(vals[i]);
    return 0;
}

// A recipe of workshop `wid` (the first one listed), for a template.
uintptr_t first_recipe_of(WorldState& ws, uint32_t wid) {
    const uintptr_t arr = ws_array(ws.rreg, wid);
    uint16_t n = 0;
    uint64_t p = 0;
    if (!arr || !rdv(arr + N.ws_count, n) || n == 0 || !rdv(arr, p)) return 0;
    return static_cast<uintptr_t>(p);
}

const std::vector<uint8_t>* category_guid(WorldState& ws, uint32_t cat) {
    if (ws.categories.empty()) {
        std::vector<uint32_t> keys;
        std::vector<uint64_t> vals;
        if (map_read(ws.rreg + E.recipes_map, &keys, &vals))
            for (uint64_t v : vals) {
                int32_t off = 0;
                uint32_t n = 0;
                const uintptr_t f = static_cast<uintptr_t>(v) + L.r_in;
                if (!rdv(f, off) || !rdv(f + 4, n) || !off || n > 64) continue;
                for (uint32_t j = 0; j < n; ++j) {
                    const uintptr_t s = f + off + static_cast<uintptr_t>(j) * L.in_stride;
                    uint32_t c = 0;
                    uint8_t g[16];
                    if (rdv(s + L.in_cat, c) && c && rd(s + L.in_cat_ref, g, 16)) ws.categories.emplace(c, std::vector<uint8_t>(g, g + 16));
                }
            }
    }
    auto it = ws.categories.find(cat);
    return it == ws.categories.end() ? nullptr : &it->second;
}

// ============================================================================================= values ==========
bool json_value(const Field& f, const json& j, KilnContentValue* v, std::string* err) {
    *v = KilnContentValue{};
    v->type = f.vt;
    auto enum_of = [&](const std::string& s, int64_t* x) {
        if (!f.en) return false;
        for (auto& [n, val] : f.en->values)
            if (lower(n) == lower(s)) {
                *x = val;
                return true;
            }
        return false;
    };
    if (j.is_boolean()) {
        v->type = KILN_CV_BOOL, v->i = j.get<bool>(), v->f = static_cast<double>(v->i);
        return true;
    }
    if (j.is_number_integer() && f.vt != KILN_CV_FLOAT && f.vt != KILN_CV_TIME) {
        v->type = KILN_CV_INT;
        v->i = j.is_number_unsigned() ? static_cast<int64_t>(j.get<uint64_t>()) : j.get<int64_t>();
        v->f = static_cast<double>(v->i);
        return true;
    }
    if (j.is_number()) {
        v->type = f.vt == KILN_CV_TIME ? KILN_CV_TIME : KILN_CV_FLOAT;
        v->f = j.get<double>();
        return true;
    }
    if (j.is_string()) {
        int64_t x = 0;
        const std::string s = j.get<std::string>();
        if (f.enum_kind != KILN_CONTENT_PLAIN && enum_of(s, &x)) {
            v->type = KILN_CV_INT, v->i = f.enum_kind == KILN_CONTENT_FLAGS ? (1ll << x) : x;
            return true;
        }
        *err = f.path + ": unknown value \"" + s + "\"";
        return false;
    }
    if (j.is_array() && f.enum_kind == KILN_CONTENT_FLAGS) {
        int64_t bits = 0;
        for (const json& e : j) {
            int64_t x = 0;
            if (e.is_string() && enum_of(e.get<std::string>(), &x)) bits |= 1ll << x;
            else if (e.is_number_integer()) bits |= 1ll << e.get<int64_t>();
            else {
                *err = f.path + ": flag names expected";
                return false;
            }
        }
        v->type = KILN_CV_INT, v->i = bits;
        return true;
    }
    *err = f.path + ": unsupported value " + j.dump();
    return false;
}

bool apply_fields(const Schema& s, const json& fields, uint8_t* base, const char* what, std::string* err) {
    for (auto& [path, val] : fields.items()) {
        const Field* f = s.find(path);
        if (!f) {
            *err = std::string(what) + ": no field " + path;
            return false;
        }
        KilnContentValue v;
        if (!json_value(*f, val, &v, err)) return false;
        uint8_t b[16] = {};
        if (!encode(*f, v, b, err)) return false;
        std::memcpy(base + f->offset, b, f->bytes);
    }
    return true;
}

// Rebases the i32 self-relative offset at `field` (now at dst + o, originally at src + o).
bool rebase(uint8_t* dst, uintptr_t src, uint32_t o) {
    int32_t off = 0;
    std::memcpy(&off, dst + o, 4);
    if (off == 0) return true;
    const int64_t target = static_cast<int64_t>(src + o) + off;
    const int64_t n = target - static_cast<int64_t>(reinterpret_cast<uintptr_t>(dst) + o);
    if (n < INT32_MIN || n > INT32_MAX) return false;
    const int32_t v = static_cast<int32_t>(n);
    std::memcpy(dst + o, &v, 4);
    return true;
}

void set_blob(uint8_t* base, uint32_t field, const void* data, uint32_t count) {
    const int32_t off = data ? static_cast<int32_t>(reinterpret_cast<intptr_t>(data) - reinterpret_cast<intptr_t>(base + field)) : 0;
    std::memcpy(base + field, &off, 4);
    std::memcpy(base + field + 4, &count, 4);
}

// ============================================================================================= items ===========
// Every ItemInfo of a world (the pools that validate kitbash GUIDs come from them).
// Kept between calls: registering many items asks after each one, and sync_world adds each new one to it.
const std::vector<uintptr_t>& world_items(WorldState& ws) {
    PhaseTimer pt(PH_WORLD_ITEMS);
    build_names(ws);
    if (ws.all_gen == ws.names_gen) return ws.all;
    ws.all.clear();
    ws.all.reserve(ws.names.size());
    for (auto& [n, r] : ws.names)
        if (r.info) ws.all.push_back(r.info);
    std::sort(ws.all.begin(), ws.all.end());
    ws.all_gen = ws.names_gen;
    return ws.all;
}

// ---- gear: an item is what its recipe tooltip promises (docs/re/items.md section 10) ----
// The tooltip (client 0xb1fa70) shows ItemInfo.rarity, level and damage at itemLevelRange.minLevel, rarity + 1 perk levels.
// createPide (crafting and a Give without Gear pass no level and no rarity) makes: rarity = generateRarity ? the lowest rarity
// disableRarityGeneration allows : rarity; level = a random roll in the range; perks only with perkReferences[rarity].
// So the promise holds with generateRarity off and a one-level range, as on the game's crafted gear. Loot weapons, the usual
// bases of new swords, are the opposite (generateRarity on, levels 1..55): the loot roll passes both. So a new item takes the
// crafted-gear form unless its definition opts in to the base's ("rolledLoot") or names the field itself.
void gear_note(Def& d, const std::string& w) {
    if (std::find(d.gear_notes.begin(), d.gear_notes.end(), w) != d.gear_notes.end()) return;
    d.gear_notes.push_back(w);
    LOGW("new content: item %s: %s", d.name.c_str(), w.c_str());
}

bool has_item_entity(const uint8_t* p) {
    const Field* f = L.item.find("flags");
    uint32_t flags = 0;
    if (f) std::memcpy(&flags, p + f->offset, std::min<size_t>(f->bytes, 4));
    return (flags & 2u) != 0;  // ItemFlag HasPIDE
}

const char* const kRarityNames[5] = {"Common", "Uncommon", "Rare", "Epic", "Legendary"};

// Every new item with an item entity, when its clone is built.
void settle_gear(Def& d, uint8_t* p) {
    d.gear_notes.clear();
    const Field* gen = L.item.find("generateRarity");
    const Field* rar = L.item.find("rarity");
    const Field* lo = L.item.find("itemLevelRange.minLevel");
    const Field* hi = L.item.find("itemLevelRange.maxLevel");
    if (!gen || !rar || !lo || !hi || !has_item_entity(p)) return;
    uint32_t a = 0, b = 0;
    std::memcpy(&a, p + lo->offset, 4);
    std::memcpy(&b, p + hi->offset, 4);
    const int rarity = p[rar->offset];
    const bool own_gen = d.fields.contains(gen->path), own_hi = d.fields.contains(hi->path);
    if (!d.rolled && (p[gen->offset] || (a != 0 && a != b)) && (!own_gen || !own_hi)) {
        std::string what;
        if (!own_gen && p[gen->offset]) what = "generateRarity off";
        if (!own_hi && a != 0 && a != b) what += (what.empty() ? "" : ", ") + ("levels " + std::to_string(a) + ".." + std::to_string(b) + " -> " + std::to_string(a));
        if (!own_gen) p[gen->offset] = 0;
        if (!own_hi && a != 0) std::memcpy(p + hi->offset, &a, 4);
        if (!what.empty())
            LOGI("new content: item %s (%s, level %u): %s, so it is made as its tooltip shows (base %s; \"rolledLoot\" keeps the base's)",
                 d.name.c_str(), rarity < 5 ? kRarityNames[rarity] : "?", a, what.c_str(), d.base.c_str());
    } else if (d.rolled && b < a && !own_hi) {
        std::memcpy(p + hi->offset, &a, 4);  // a level above the base's range: an empty range makes no Level at all
    }
    // perk slots: the tooltip and the item's level ladder read slots 0..rarity. An instance's rarity is availablePerks - 1:
    // without slot [rarity] createPide gives no perks and rebalance_perks_and_level grants the slots up to the last filled one
    bool any = false, have[5] = {};
    for (int i = 0; i < 5; ++i) {
        const Field* f = L.item.find("perkReferences[" + std::to_string(i) + "]");
        if (!f || f->bytes != 16) return;
        for (int k = 0; k < 16; ++k) have[i] = have[i] || p[f->offset + k] != 0;
        any = any || have[i];
    }
    if (!any || rarity > 4) return;
    for (int i = 0; i <= rarity; ++i)
        if (!have[i]) {
            int filled = rarity;
            while (filled > 0 && !have[filled]) --filled;
            char m[192];
            std::snprintf(m, sizeof m, "%s reads perk slots 0..%d but perk slot %d is empty: %s%s", kRarityNames[rarity], rarity, i,
                          i == rarity ? "the game makes the item " : "the item's enhancements have a gap",
                          i == rarity ? kRarityNames[filled] : "");
            gear_note(d, m);
        }
}

// A recipe makes this item, and its definition opted out of the crafted-gear form: say what the player will see.
void note_crafted(Def& d, const std::string& recipe) {
    const Field* gen = L.item.find("generateRarity");
    const Field* rar = L.item.find("rarity");
    const Field* mask = L.item.find("disableRarityGeneration");
    const Field* lo = L.item.find("itemLevelRange.minLevel");
    const Field* hi = L.item.find("itemLevelRange.maxLevel");
    if (!gen || !rar || !mask || !lo || !hi) return;
    for (auto& [base, clone] : d.clones) {
        const auto* p = reinterpret_cast<const uint8_t*>(clone);
        if (!has_item_entity(p)) continue;
        const int rarity = p[rar->offset];
        if (p[gen->offset] && rarity < 5) {
            const int allowed = ~p[mask->offset] & 0x1f;
            int lowest = 0;
            while (lowest < 5 && !(allowed >> lowest & 1)) ++lowest;
            if (lowest != rarity)
                gear_note(d, "generateRarity is on and recipe " + recipe + " makes it: the game makes it " +
                                 (lowest < 5 ? kRarityNames[lowest] : "no rarity") + " while the tooltip shows " + kRarityNames[rarity]);
        }
        uint32_t a = 0, b = 0;
        std::memcpy(&a, p + lo->offset, 4);
        std::memcpy(&b, p + hi->offset, 4);
        if (a != 0 && a != b)
            gear_note(d, "recipe " + recipe + " makes it with the level range " + std::to_string(a) + ".." + std::to_string(b) +
                             ": the game rolls a level in it for each craft while the tooltip shows " + std::to_string(a));
    }
}

uintptr_t build_clone(Def& d, uintptr_t base, WorldState& ws, std::string* why) {
    PhaseTimer pt(PH_BUILD_CLONE);
    const uint32_t size = L.item.size;
    const size_t total = size + d.name.size() + 1;
    auto* p = static_cast<uint8_t*>(near_alloc(base, total));
    if (!p) {
        *why = "no memory near the base item";
        return 0;
    }
    if (!rd(base, p, size)) {
        *why = "the base item is not readable";
        return 0;
    }
    // From a game item: as the game has it, whatever rules other mods have on it. From a clone: as that clone is.
    uint32_t base_id = 0;
    std::memcpy(&base_id, p + L.i_id, 4);
    const bool game_base = !nc_is_new(KILN_CONTENT_ITEMS, base_id);
    if (game_base) store_originals(base, size, p);
    for (uint32_t o : L.item_relocs)
        if (!rebase(p, base, o)) {
            *why = "a blob field of the base item is out of reach";
            return 0;
        }
    std::memcpy(p + L.i_id, &d.id, 4);
    std::memcpy(p + L.i_guid, d.guid, 16);
    for (int s = 0; s < kTextSlots; ++s)
        if (d.has_text(s))
            if (const Field* f = L.item.find(kTextPaths[s]); f && f->bytes == 16) std::memcpy(p + f->offset, d.tag_guid[s], 16);
    // Where the base is found is not where this item is: its hint is its own or there is none, as on most of the game's items.
    const auto no_text = [&](int slot) {
        if (const Field* f = L.item.find(kTextPaths[slot]); f && f->bytes == 16 && !d.has_text(slot)) std::memset(p + f->offset, 0, 16);
    };
    no_text(4);
    if (d.no_description) no_text(2);
    char* name = reinterpret_cast<char*>(p + size);
    std::memcpy(name, d.name.data(), d.name.size());
    name[d.name.size()] = 0;
    set_blob(p, L.i_dbg, name, static_cast<uint32_t>(d.name.size()));
    if (!apply_fields(L.item, d.fields, p, d.name.c_str(), why)) return 0;
    rd(base + L.i_guid, d.base_guid, 16);
    std::memcpy(d.icon_guid, d.base_guid, 16);
    // kitbash: references to resources the game already loaded (refs.cpp), checked against this world's items
    d.warnings.clear();
    if (!d.kitbash.empty()) {
        PhaseTimer kt(PH_KITBASH);
        const std::vector<uintptr_t> all = world_items(ws);
        const ItemFind find = [&](const std::string& sel) -> uintptr_t {
            NameRec r{};
            if (lower(sel) == lower(d.name) || lower(sel) == d.guid_s) return reinterpret_cast<uintptr_t>(p);
            return resolve_item(ws, sel, &r) ? r.info : 0;
        };
        KitbashOut ko;
        for (auto& [key, val] : d.kitbash.items()) kitbash(L.item, key, val, find, all, &ko, reinterpret_cast<uintptr_t>(p));
        for (const RefWrite& w : ko.writes) std::memcpy(p + w.f->offset, w.bytes, w.f->bytes);
        for (const BlobWrite& b : ko.blobs)
            if (!b.data) set_blob(p, b.offset, nullptr, 0);
            else if (const int64_t rel = static_cast<int64_t>(b.data) - static_cast<int64_t>(reinterpret_cast<uintptr_t>(p) + b.offset);
                     rel >= INT32_MIN && rel <= INT32_MAX)
                set_blob(p, b.offset, reinterpret_cast<const void*>(b.data), b.count);
            else
                ko.problems.push_back(b.path + ": the other item's list is out of reach");
        if (ko.icon) std::memcpy(d.icon_guid, ko.icon_guid, 16);
        d.warnings = ko.problems;
        d.kitbash_retry = std::any_of(ko.problems.begin(), ko.problems.end(),
                                      [](const std::string& w) { return w.find("the game has loaded") != std::string::npos; });
        for (const std::string& w : ko.problems)
            LOGW("new content: item %s: %s (skipped; the base's value stays)", d.name.c_str(), w.c_str());
        if (!ko.writes.empty() || !ko.blobs.empty())
            LOGI("new content: item %s: %zu reference / visual field(s) and %zu list(s) from kitbash%s", d.name.c_str(),
                 ko.writes.size(), ko.blobs.size(), ko.icon ? ", icon of another item" : "");
    }
#if KC_GAMEDATA
    // its lists become its own: a change to one of them must not change the base and every other clone of it
    // Refused rather than kept: a rule on a shared list would change the base item and every other clone of it.
    PhaseTimer ot(PH_OWN_LISTS);
    if (std::string own; !gd_own_lists(reinterpret_cast<uintptr_t>(p), game_base, &own)) {
        *why = "its lists cannot be its own: " + own;
        return 0;
    }
#endif
    settle_gear(d, p);
    return reinterpret_cast<uintptr_t>(p);
}

// ============================================================================================= recipes =========
uintptr_t build_recipe(Def& d, WorldState& ws, std::string* why) {
    PhaseTimer pt(PH_BUILD_RECIPE);
    uintptr_t tmpl = 0;
    bool from_station = false;
    if (!d.base.empty()) {
        tmpl = resolve_recipe(ws, d.base);
        if (!tmpl) {
            *why = "base recipe " + d.base + " not found";
            return 0;
        }
    }
    uint8_t wguid[16] = {};
    uint32_t wid = 0;
    bool own_workshop = false;
    if (!d.station.empty()) {
        // a recipe selector (its station), a workshop id, or "workshop:<guid>": a workshop of its own, new if nothing has it yet
        uintptr_t s = 0;
        if (d.station.rfind("workshop:", 0) == 0) {
            if (!guid_parse(lower(d.station.substr(9)), wguid)) {
                *why = "\"" + d.station + "\": a workshop of its own is \"workshop:<guid>\"";
                return 0;
            }
            own_workshop = true;
            wid = fnv1a(wguid, 16);
            s = first_recipe_of(ws, wid);
            if (!s) s = first_recipe_of(ws, 0);  // no recipes there yet: only the shape of a recipe, what it asks is cleared below
            if (!s) {
                *why = "no recipe to take the shape of a recipe from";
                return 0;
            }
        } else if (d.station.size() > 2 && d.station[0] == '0' && (d.station[1] == 'x' || d.station[1] == 'X')) {
            wid = static_cast<uint32_t>(std::strtoul(d.station.c_str() + 2, nullptr, 16));
            s = first_recipe_of(ws, wid);
            if (!s && wid != 0) {
                *why = "workshop " + d.station + " has no recipes (a workshop of its own is \"workshop:<guid>\")";
                return 0;
            }
        } else if (lower(d.station) == "hand" || lower(d.station) == "player") {
            wid = 0;
            s = first_recipe_of(ws, 0);
        } else {
            s = resolve_recipe(ws, d.station);
            if (!s) {
                *why = "station recipe " + d.station + " not found";
                return 0;
            }
            rdv(s + L.r_wid, wid);
        }
        if (s && !own_workshop) rd(s + L.r_wguid, wguid, 16);
        if (!tmpl) {
            tmpl = s;
            from_station = true;
        }
    }
    if (!tmpl) {
        *why = "a recipe needs \"base\" (a recipe to copy) or \"station\"";
        return 0;
    }
    // inputs / outputs
    struct In {
        NameRec item{};
        const std::vector<uint8_t>* cat = nullptr;
        uint32_t category = 0, count = 0;
    };
    std::vector<In> ins;
    std::vector<In> outs;
    for (const StackSpec& s : d.inputs) {
        In x;
        x.count = s.count;
        if (s.category) {
            x.category = s.category;
            x.cat = category_guid(ws, s.category);
            if (!x.cat) {
                char b[48];
                std::snprintf(b, sizeof b, "input category 0x%08x unknown", s.category);
                *why = b;
                return 0;
            }
        } else if (!resolve_item(ws, s.item, &x.item)) {
            *why = "input item " + s.item + " not found";
            return 0;
        }
        ins.push_back(x);
    }
    for (const StackSpec& s : d.outputs) {
        In x;
        x.count = s.count;
        if (!resolve_item(ws, s.item, &x.item)) {
            *why = "output item " + s.item + " not found";
            return 0;
        }
        outs.push_back(x);
    }
    // requiredProps of the template (copied unless the template is just "a recipe of that station")
    int32_t poff = 0;
    uint32_t pn = 0;
    if (!from_station) {
        rdv(tmpl + L.r_props, poff);
        rdv(tmpl + L.r_props + 4, pn);
        if (!poff || pn > 64) pn = 0;
    }
    const size_t size = L.recipe.size, in_at = (size + 15) & ~size_t(15), out_at = in_at + ins.size() * L.in_stride,
                 props_at = out_at + outs.size() * L.out_stride, name_at = props_at + pn * L.prop_size,
                 total = name_at + d.name.size() + 1;
    auto* p = static_cast<uint8_t*>(near_alloc(tmpl, total));
    if (!p) {
        *why = "no memory near the recipe data";
        return 0;
    }
    std::memset(p, 0, total);
    if (!rd(tmpl, p, size) || (pn && !rd(tmpl + L.r_props + poff, p + props_at, pn * L.prop_size))) {
        *why = "the template recipe is not readable";
        return 0;
    }
    std::memcpy(p + L.r_guid, d.guid, 16);
    std::memcpy(p + L.r_id, &d.id, 4);
    if (!d.station.empty()) {
        std::memcpy(p + L.r_wguid, wguid, 16);
        std::memcpy(p + L.r_wid, &wid, 4);
    }
    for (size_t i = 0; i < ins.size(); ++i) {
        uint8_t* e = p + in_at + i * L.in_stride;
        if (ins[i].category) {
            std::memcpy(e + L.in_cat_ref, ins[i].cat->data(), 16);
            std::memcpy(e + L.in_cat, &ins[i].category, 4);
            std::memcpy(e + L.in_cat_count, &ins[i].count, 4);
        } else {
            std::memcpy(e + L.in_ref, ins[i].item.guid, 16);
            std::memcpy(e + L.in_item, &ins[i].item.id, 4);
            std::memcpy(e + L.in_count, &ins[i].count, 4);
        }
    }
    for (size_t i = 0; i < outs.size(); ++i) {
        uint8_t* e = p + out_at + i * L.out_stride;
        std::memcpy(e + L.out_ref, outs[i].item.guid, 16);
        std::memcpy(e + L.out_item, &outs[i].item.id, 4);
        std::memcpy(e + L.out_count, &outs[i].count, 4);
    }
    set_blob(p, L.r_in, ins.empty() ? nullptr : p + in_at, static_cast<uint32_t>(ins.size()));
    set_blob(p, L.r_out, outs.empty() ? nullptr : p + out_at, static_cast<uint32_t>(outs.size()));
    set_blob(p, L.r_props, pn ? p + props_at : nullptr, pn);
    if (from_station) {
        std::memset(p + L.r_mask, 0, 8);
        for (const std::string& f : L.zero_without_base)
            if (const Field* x = L.recipe.find(f)) std::memset(p + x->offset, 0, x->bytes);
    }
    if (d.duration >= 0) {
        const int64_t ns = static_cast<int64_t>(d.duration * 1e9);
        std::memcpy(p + L.r_dur, &ns, 8);
    }
    if (!d.unlock_base) std::memset(p + L.r_know, 0, L.know_size);  // no knowledge needed (docs: "Unlock")
    std::memset(p + L.r_done, 0, L.know_size);                       // no "crafted once" tracking
    if (!d.display.empty()) std::memcpy(p + L.r_name, d.tag_guid[0], 16);
    else std::memset(p + L.r_name, 0, 16);
    char* name = reinterpret_cast<char*>(p + name_at);
    std::memcpy(name, d.name.data(), d.name.size());
    set_blob(p, L.r_dbg, name, static_cast<uint32_t>(d.name.size()));
    if (!apply_fields(L.recipe, d.fields, p, d.name.c_str(), why)) return 0;
    return reinterpret_cast<uintptr_t>(p);
}

// ============================================================================================= sync ============
void add_kc_entry(uint32_t kind, const Def& d, uintptr_t addr) {
    PhaseTimer pt(PH_KC_ENTRY);
    std::lock_guard lock(store_mutex());
    Registry& r = reg(kind);
    if (r.entries.empty()) return;  // no snapshot yet: build_registries will read the game's map
#if KC_GAMEDATA
    if (kind == KILN_CONTENT_ITEMS) gd_item_registered(d.id, d.name, d.guid, addr);
#endif
    if (const Entry* e = r.get(d.id)) {
        if (e->addr != addr) {  // redefined: the entry follows the new clone
            const uint32_t i = r.by_id[d.id];
            r.by_addr.erase(r.entries[i].addr);
            r.entries[i].addr = addr;
            r.by_addr[addr] = i;
        }
        return;
    }
    Entry e;
    e.id = d.id;
    e.name = d.name;
    e.addr = addr;
    std::memcpy(e.guid, d.guid, 16);
    r.add(std::move(e));
}

void fail_once(WorldState& ws, uint32_t kind, const Def& d, const std::string& why) {
    const std::string key = std::to_string(d.version) + ":" + d.guid_s + ":" + why;
    if (!ws.logged.insert(key).second) return;
    LOGW("new content: %s %s (%s) not registered in the %s world: %s", d.kind == KILN_CONTENT_ITEMS ? "item" : "recipe",
         d.name.c_str(), d.guid_s.c_str(), wname(kind), why.c_str());
    std::lock_guard lock(store_mutex());
    const_cast<Def&>(d).error = why;
}

uint32_t loot_sync(WorldState& ws, uintptr_t world);  // distribution (below)
void drops_sync();
void note_registered(uint32_t kind, uintptr_t world, WorldState& ws);  // early registration (below)
void unknown_enable(WorldState& ws);
uint32_t g_early_kind = KILN_WORLD_CLIENT;  // the kind registered at registry load (<kiln>/data/content/early-test: server)

// Inserts every definition into `world`'s registries (its thread). Returns true when everything is settled.
bool sync_world(uint32_t kind, uintptr_t world) {
    if (!N.on || !world || kind >= KILN_WORLD_KINDS || kind == KILN_WORLD_PREDICTION) return false;
    PhaseTimer pt(PH_SYNC);
    KilnSessionInfo si{};
    si.size = sizeof si;
    g_api->session(&si);
    WorldState& ws = g_ws[kind];
    // bound at set-up (early_bind): its session is the one its first step is about to start
    const bool waiting = ws.early && ws.world == world && ws.session == si.sessions[kind] + 1;
    if (!waiting && (ws.world != world || ws.session != si.sessions[kind])) reset_ws(kind, world, si.sessions[kind]);
    const uint64_t version = g_version.load();
    if (ws.done == version) return true;
    if (!layouts()) {
        static bool said = false;
        if (!said) LOGW("new content unavailable: %s", L.why.c_str());
        said = true;
        ws.done = version;
        return true;
    }
    ws.ireg = g_api->singleton(world, KilnStr{"g38_items::ItemRegistry", 23});
    ws.rreg = E.recipes ? g_api->singleton(world, KilnStr{"g38_items::RecipeRegistry", 25}) : 0;
    if (!ws.ireg || !items_loaded(ws.ireg)) return false;
    std::vector<DefPtr> defs;
    {
        std::lock_guard lock(store_mutex());
        defs = g_defs;
    }
    if (defs.empty()) {
        ws.done = version;
        note_registered(kind, world, ws);
        return true;
    }
    const bool active = active_world() == world;
    bool settled = true;
    uint32_t added_items = 0, added_recipes = 0, listed = 0, unlisted = 0;
    // ---- items ----
    std::optional<PhaseTimer> items_timer(std::in_place, PH_SYNC_ITEMS);
    for (const DefPtr& dp : defs) {
        Def& d = *dp;
        if (d.kind != KILN_CONTENT_ITEMS || (d.internal && kind != g_early_kind)) continue;
        uintptr_t clone = 0;
        {
            std::lock_guard lock(store_mutex());
            NameRec base{};
            if (!resolve_item(ws, d.base, &base) || !base.info || base.id == d.id) {
                fail_once(ws, kind, d, "base item " + d.base + " not found");
                continue;
            }
            auto it = d.clones.find(base.info);
            if (it != d.clones.end()) clone = it->second;
            else {
                std::string why;
                clone = build_clone(d, base.info, ws, &why);
                if (!clone) {
                    fail_once(ws, kind, d, why);
                    continue;
                }
                d.clones.emplace(base.info, clone);
                d.error.clear();
                set_alias(d.guid, d.icon_guid);  // the UI's icon texture: the base item's, or "icon"'s
            }
        }
        auto had = ws.items.find(d.id);
        if (had != ws.items.end() && had->second == clone) continue;
        build_names(ws);
        if (auto nm = ws.names.find(lower(d.name)); nm != ws.names.end() && nm->second.id != d.id) {
            fail_once(ws, kind, d, "the name " + d.name + " is a game item's");
            continue;
        }
        uint64_t other = 0;
        std::string why;
        // a redefinition swaps the map's value (the old clone stays valid); a foreign item under the id is a collision
        const bool ours = had != ws.items.end();
        int rc = 0;
        bool no_room = false;
        {
            PhaseTimer mt(PH_MAP_PUT);
            rc = map_put(ws.ireg + E.items_map, d.id, clone, ours, &other, &why, &no_room);
        }
        if (rc == 3) {
            char b[96];
            std::snprintf(b, sizeof b, "id 0x%08x is taken by another item (%s)", d.id,
                          read_blob_string(static_cast<uintptr_t>(other) + L.i_dbg).c_str());
            fail_once(ws, kind, d, b);
            continue;
        }
        if (!rc) {
            fail_once(ws, kind, d, why);
            if (!no_room) settled = false;  // a full map stays full: trying again each step only costs time
            continue;
        }
        ws.items[d.id] = clone;
        build_names(ws);
        ws.names[lower(d.name)] = NameRec{d.id, {}, clone};
        std::memcpy(ws.names[lower(d.name)].guid, d.guid, 16);
        if (ws.all_gen == ws.names_gen++) {  // world_items() stays current: the new item at its end
            ws.all.push_back(clone);
            ws.all_gen = ws.names_gen;
        }
        if (active && !d.internal) add_kc_entry(KILN_CONTENT_ITEMS, d, clone);
        ++added_items;
        LOGI("new content: item %s (0x%08x, %s) %s the %s world's registry (clone of %s at %p)", d.name.c_str(), d.id,
             d.guid_s.c_str(), rc == 4 ? "replaced in" : "added to", wname(kind), d.base.c_str(), reinterpret_cast<void*>(clone));
    }
    items_timer.reset();
    if (kind == g_early_kind) unknown_enable(ws);  // the registry is complete: from here a miss is an unknown id
    // ---- recipes ----
    std::optional<PhaseTimer> recipes_timer(std::in_place, PH_SYNC_RECIPES);
    if (!ws.rreg || !recipes_loaded(ws.rreg)) {
        for (const DefPtr& dp : defs)
            if (dp->kind == KILN_CONTENT_RECIPES) settled = false;
    } else {
        for (const DefPtr& dp : defs) {
            Def& d = *dp;
            if (d.kind != KILN_CONTENT_RECIPES) continue;
            uintptr_t info = 0;
            {
                std::lock_guard lock(store_mutex());
                if (d.info && d.info_version == d.version) info = d.info;
                else {
                    std::string why;
                    info = build_recipe(d, ws, &why);
                    if (!info) {
                        fail_once(ws, kind, d, why);
                        continue;
                    }
                    d.info = info, d.info_version = d.version, d.error.clear();
                }
            }
            uint32_t wid = 0;
            rdv(info + L.r_wid, wid);
            auto had = ws.recipes.find(d.id);
            if (had == ws.recipes.end() || had->second != info) {
                uint64_t other = 0;
                std::string why;
                int rc = 0;
                {
                    PhaseTimer mt(PH_MAP_PUT);
                    rc = map_put(ws.rreg + E.recipes_map, d.id, info, had != ws.recipes.end(), &other, &why);
                }
                if (rc == 3) {
                    char b[96];
                    std::snprintf(b, sizeof b, "id 0x%08x is taken by recipe %s", d.id,
                                  read_blob_string(static_cast<uintptr_t>(other) + L.r_dbg).c_str());
                    fail_once(ws, kind, d, b);
                    continue;
                }
                if (!rc) {
                    fail_once(ws, kind, d, why);
                    continue;
                }
                std::lock_guard lock(g_ui_mu);
                ws.recipes[d.id] = info;
                if (active) add_kc_entry(KILN_CONTENT_RECIPES, d, info);
                ++added_recipes;
            }
            // the workshop list: listed while the definition's owner is there
            auto lit = ws.listed.find(d.id);
            const bool want = !d.orphan;
            if (lit != ws.listed.end() && (!want || lit->second != info || ws.recipe_ws[d.id] != wid)) {
                if (const uintptr_t arr = ws_array(ws.rreg, ws.recipe_ws[d.id])) ws_swap(arr, lit->second, 0);
                std::lock_guard lock(g_ui_mu);
                ws.listed.erase(lit);
                ++unlisted;
            }
            if (want && !ws.listed.count(d.id)) {
                std::string why;
                uintptr_t arr = ws_array(ws.rreg, wid);
                if (!arr && d.station.rfind("workshop:", 0) == 0) {
                    arr = ws_create(ws.rreg, wid, &why);
                    if (!arr) {
                        fail_once(ws, kind, d, why);
                        continue;
                    }
                    LOGI("new content: workshop 0x%08x (%s) is new in the %s world's recipe registry", wid, d.station.c_str() + 9, wname(kind));
                }
                if (!arr) {
                    char b[64];
                    std::snprintf(b, sizeof b, "workshop 0x%08x has no recipe list", wid);
                    fail_once(ws, kind, d, b);
                    continue;
                }
                bool added = false;
                {
                    PhaseTimer wt(PH_WS_ADD);
                    added = ws_add(arr, info, &why);
                }
                if (!added) {
                    fail_once(ws, kind, d, why);
                    continue;
                }
                std::lock_guard lock(g_ui_mu);
                ws.listed[d.id] = info;
                ws.recipe_ws[d.id] = wid;
                ++listed;
            }
        }
    }
    recipes_timer.reset();
    // an item that opted out and that a new recipe makes: its warnings
    {
        PhaseTimer ct(PH_CRAFTED);
        std::lock_guard lock(store_mutex());
        for (const DefPtr& r : defs) {
            if (r->kind != KILN_CONTENT_RECIPES || r->orphan) continue;
            for (const StackSpec& o : r->outputs) {
                NameRec out{};
                if (o.item.empty() || !resolve_item(ws, o.item, &out)) continue;
                if (const auto* made = g_index.with_id(KILN_CONTENT_ITEMS, out.id))
                    for (const DefPtr& i : *made) note_crafted(*i, r->name);
            }
        }
    }
    if (kind == KILN_WORLD_SERVER && added_items + listed + unlisted + added_recipes >= 0) {
        loot_sync(ws, world);  // random loot of new items (server world only)
        drops_sync();          // drop tables seen so far
    }
    if (kind == KILN_WORLD_CLIENT) {
        // new hair styles and beards in the barber's lists (barber.cpp): each new item that starts from a listed style
        std::vector<BarberItem> barber;
        for (const DefPtr& dp : defs)
            if (dp->kind == KILN_CONTENT_ITEMS && !dp->internal && !dp->placeholder) {
                BarberItem b{};
                std::memcpy(b.guid, dp->guid, 16);
                std::memcpy(b.base, dp->base_guid, 16);
                std::memcpy(b.icon, dp->icon_guid, 16);
                barber.push_back(b);
            }
        barber_sync(barber);
    }
    if ((listed || unlisted) && kind == KILN_WORLD_CLIENT && N.ui_refresh) {
        // the crafting UI rebuilds its recipe lists only when the player's knowledge changes: make it rebuild now
        if (const uintptr_t ui = g_api->singleton(world, KilnStr{"G38UiPlayData", 13})) set_u32(ui + N.ui_version, 0xFFFFFFFFu);
    }
    if (added_items || added_recipes || listed || unlisted)
        LOGI("new content: %s world: %u item(s), %u recipe(s) added, %u recipe(s) listed at their station, %u taken off "
             "(definition version %llu)",
             wname(kind), added_items, added_recipes, listed, unlisted, static_cast<unsigned long long>(version));
    if (settled) {
        ws.done = version;
        note_registered(kind, world, ws);
    }
    return settled;
}

// ============================================================================================= distribution ====
// Random loot (docs/re/content.md 8.4): every roll builds its candidate list from LootableItemsResource.items, read
// through LootItems (lootRegistry +data -> +items); a candidate needs non-empty label groups that match the
// container's tags, a rarity, and LootFind(itemId) for its stack sizes. A new item with "loot" gets a copy of its
// `like` item's LootableItemInfo (labels, rarities, shufflebag type: it drops where that item drops), inserted into
// the loot map, and the getter hook appends those copies to the array it returns. Server world only (the client
// never rolls; a hosting client's server world does).
struct LootLayout {
    bool ok = false, tried = false;
    Schema schema;
    std::vector<uint32_t> relocs;
    uint32_t size = 36, id = 0;
} LL;

bool loot_layout() {
    if (LL.tried) return LL.ok;
    LL.tried = true;
    std::string err;
    if (!build_schema("keen::LootableItemInfo", {"itemId"}, &LL.schema, &err) ||
        !self_relative_fields("keen::LootableItemInfo", &LL.relocs, &err)) {
        LOGW("new content: no random loot for new items: %s", err.c_str());
        return false;
    }
    LL.size = LL.schema.size;
    bool ok = true;
    LL.id = special_of(LL.schema, "itemId", &ok);
    LL.ok = ok && LL.size >= 16 && LL.size <= 256;
    return LL.ok;
}

std::mutex g_loot_mu;
std::vector<uintptr_t> g_loot_extra;  // the entries the getter appends (server world), in definition order
uintptr_t g_loot_reg = 0;             // the loot registry they belong to
uint64_t g_loot_gen = 0;              // +1 per change of g_loot_extra

void loot_world_reset() {
    std::lock_guard lock(g_loot_mu);
    g_loot_extra.clear();
    g_loot_reg = 0;
    ++g_loot_gen;
}

uintptr_t loot_registry(uintptr_t world) {
    if (!E.loot) return 0;
    const uintptr_t lg = g_api->singleton(world, KilnStr{"g38_loot::LootGenerationSystem", 30});
    uintptr_t lreg = 0;
    if (!lg || !rdv(lg + E.lootgen_lootreg, lreg) || !lreg) return 0;
    uintptr_t res = 0;
    return rdv(lreg + N.lootreg_data, res) && res ? lreg : 0;  // the LootableItemsResource is in
}

// Builds / inserts the loot entry of every definition with "loot" (server world thread). Returns how many are in.
uint32_t loot_sync(WorldState& ws, uintptr_t world) {
    if (!N.loot || !loot_layout()) return 0;
    PhaseTimer pt(PH_LOOT);
    const uintptr_t lreg = loot_registry(world);
    if (!lreg) return 0;
    std::vector<DefPtr> defs;
    {
        std::lock_guard lock(store_mutex());
        for (const DefPtr& d : g_defs)
            if (d->kind == KILN_CONTENT_ITEMS && d->distribution.contains("loot") && !d->placeholder && ws.items.count(d->id))
                defs.push_back(d);
    }
    std::vector<uintptr_t> extra;
    for (const DefPtr& dp : defs) {
        Def& d = *dp;
        const json& lj = d.distribution["loot"];
        if (lj.is_boolean() && !lj.get<bool>()) continue;
        if (d.orphan) continue;  // its mod is gone: no new drops (saved stacks stay valid)
        if (!d.loot_entry || d.loot_version != d.version) {
            const std::string like = lj.is_object() && lj.contains("like") && lj["like"].is_string() ? lj["like"].get<std::string>() : d.base;
            NameRec li{};
            uintptr_t src = 0;
            if (resolve_item(ws, like, &li)) src = static_cast<uintptr_t>(map_get(lreg + E.lootreg_map, li.id));
            if (!src) {
                fail_once(ws, KILN_WORLD_SERVER, d, "loot: " + like + " has no random-loot entry (choose \"like\": an item that drops)");
                continue;
            }
            auto* p = static_cast<uint8_t*>(near_alloc(src, LL.size));
            if (!p || !rd(src, p, LL.size)) continue;
            bool ok = true;
            for (uint32_t o : LL.relocs) ok = ok && rebase(p, src, o);
            if (!ok) continue;
            std::memcpy(p + LL.id, &d.id, 4);
            json f = lj.is_object() && lj.contains("fields") && lj["fields"].is_object() ? lj["fields"] : json::object();
            if (lj.is_object())
                for (const char* k : {"stackSizeMin", "stackSizeMax", "stackSizeMaxScaled"})
                    if (lj.contains(k)) f[k] = lj[k];
            std::string why;
            if (!apply_fields(LL.schema, f, p, d.name.c_str(), &why)) {
                fail_once(ws, KILN_WORLD_SERVER, d, "loot: " + why);
                continue;
            }
            d.loot_entry = reinterpret_cast<uintptr_t>(p);
            d.loot_version = d.version;
        }
        uint64_t other = 0;
        std::string why;
        const int rc = map_put(lreg + E.lootreg_map, d.id, d.loot_entry, true, &other, &why);
        if (!rc) {
            fail_once(ws, KILN_WORLD_SERVER, d, "loot: " + why);
            continue;
        }
        if (rc == 1 || rc == 4) {
            LOGI("new content: item %s drops as random loot like %s", d.name.c_str(),
                 (lj.is_object() && lj.contains("like") ? lj["like"].get<std::string>() : d.base).c_str());
            if (active_world() == world) {
                std::lock_guard lock(store_mutex());
                Registry& R = reg(KILN_CONTENT_LOOT);
                if (!R.entries.empty() && !R.get(d.id)) {
                    Entry e;
                    e.id = d.id, e.name = d.name, e.addr = d.loot_entry;
                    std::memcpy(e.guid, d.guid, 16);
                    R.add(std::move(e));
                }
            }
        }
        extra.push_back(d.loot_entry);
    }
    std::lock_guard lock(g_loot_mu);
    if (extra != g_loot_extra || g_loot_reg != lreg) {
        g_loot_extra = extra;
        g_loot_reg = lreg;
        ++g_loot_gen;
    }
    return static_cast<uint32_t>(extra.size());
}

// LootItems(out, lootRegistry) -> {data, count}: the resource's array plus the new items' entries (an immutable copy,
// each entry's label BlobArrays rebased; rebuilt when the source or the set changes, old copies never freed). Job threads.
struct LootSpan {
    uintptr_t data;
    uint64_t count;
};
SafetyHookInline g_lootitems_hook;
struct LootCache {
    uintptr_t src = 0;
    uint64_t count = 0, gen = ~0ull;
    uintptr_t ext = 0;
    uint64_t ext_count = 0;
} g_loot_cache;

std::atomic<uint64_t> g_loot_calls{0}, g_loot_extended{0};  // rolls that read the candidates / got the new items

LootSpan* __fastcall hk_lootitems(LootSpan* out, uintptr_t lreg) {
    LootSpan* r = g_lootitems_hook.call<LootSpan*>(out, lreg);
    g_loot_calls.fetch_add(1, std::memory_order_relaxed);
    if (!r || !r->data || !r->count || !LL.ok) return r;
    std::lock_guard lock(g_loot_mu);
    if (lreg != g_loot_reg || g_loot_extra.empty()) return r;
    g_loot_extended.fetch_add(1, std::memory_order_relaxed);
    LootCache& c = g_loot_cache;
    if (c.src != r->data || c.count != r->count || c.gen != g_loot_gen || !c.ext) {
        const size_t n = r->count + g_loot_extra.size();
        auto* p = static_cast<uint8_t*>(near_alloc(r->data, n * LL.size));
        if (!p || !rd(r->data, p, r->count * LL.size)) return r;
        bool ok = true;
        for (size_t i = 0; i < r->count && ok; ++i)
            for (uint32_t o : LL.relocs) ok = ok && rebase(p + i * LL.size, r->data + i * LL.size, o);
        for (size_t i = 0; i < g_loot_extra.size() && ok; ++i) {
            uint8_t* e = p + (r->count + i) * LL.size;
            ok = rd(g_loot_extra[i], e, LL.size);
            for (uint32_t o : LL.relocs) ok = ok && rebase(e, g_loot_extra[i], o);
        }
        if (!ok) return r;
        c = LootCache{r->data, r->count, g_loot_gen, reinterpret_cast<uintptr_t>(p), n};
        LOGI("new content: random loot rolls see %zu item(s) (%llu of the game's + %zu new)", n,
             static_cast<unsigned long long>(r->count), g_loot_extra.size());
    }
    r->data = c.ext;
    r->count = c.ext_count;
    return r;
}

// Drop tables (DefaultInventoryResource, docs/re/content.md 8.4): the stack applier resolves `item` by id, so a new
// stack needs only the new id. "drops": [{table: GUID | glob | "like:<item>", countMin, countMax, chance, weight}]: a
// table (its root group), or every group that drops <item>. A plain group gets one more child group holding the new
// stacks (each rolls its own chance; the group's own stacks are untouched, so drop rules keep working on them); a
// selector group (one stack picked by weight) gets its stack list copied with the new stacks appended (at most 64
// entries, the game's limit). The header is a layer of kiln.content's own owner (rebuilt when definitions change).
struct DropAdd {
    uint32_t id;
    uint8_t guid[16];
    std::string table;  // selector
    uint32_t cmin, cmax;
    float chance, weight;
};
struct GroupLayout {
    bool ok = false, tried = false;
    uint32_t selector = 0, stacks = 4, groups = 0xC, size = 36, stack_size = 40, s_ref = 0;
    std::vector<uint32_t> relocs;
} GL;

bool group_layout() {
    if (GL.tried) return GL.ok;
    GL.tried = true;
    const int64_t sel = field_offset("keen::ecs::DefaultInventoryGroupResource", "isSelector");
    const int64_t ref = field_offset("keen::ecs::ItemStackInfoResource", "itemRef");
    std::string err;
    std::vector<uint32_t> srel;
    if (sel < 0 || ref < 0 || !self_relative_fields("keen::ecs::DefaultInventoryGroupResource", &GL.relocs, &err) ||
        !self_relative_fields("keen::ecs::ItemStackInfoResource", &srel, &err) || !srel.empty()) {
        LOGW("new content: no drop-table entries for new items (%s)", err.empty() ? "layout" : err.c_str());
        return false;
    }
    GL.selector = static_cast<uint32_t>(sel), GL.s_ref = static_cast<uint32_t>(ref);
    GL.stacks = DL.group_stacks, GL.groups = DL.group_groups, GL.size = DL.group_size, GL.stack_size = DL.stack_size;
    GL.ok = GL.size >= 16 && GL.stack_size >= 0x24;
    return GL.ok;
}

bool glob_match(const char* p, const char* s) {
    const char *star = nullptr, *ss = nullptr;
    auto lo = [](char c) { return (c >= 'A' && c <= 'Z') ? char(c - 'A' + 'a') : c; };
    while (*s) {
        if (*p == '*') star = p++, ss = s;
        else if (*p == '?' || (*p && lo(*p) == lo(*s))) ++p, ++s;
        else if (star) p = star + 1, s = ++ss;
        else return false;
    }
    while (*p == '*') ++p;
    return *p == 0;
}

std::vector<DropAdd> drop_adds() {
    std::vector<DropAdd> out;
    std::lock_guard lock(store_mutex());
    for (const DefPtr& d : g_defs) {
        if (d->kind != KILN_CONTENT_ITEMS || d->orphan || d->placeholder || !d->distribution.contains("drops")) continue;
        if (!g_ws[KILN_WORLD_SERVER].items.count(d->id)) continue;
        for (const json& x : d->distribution["drops"]) {
            if (!x.is_object() || !x.contains("table") || !x["table"].is_string()) continue;
            DropAdd a{};
            a.id = d->id;
            std::memcpy(a.guid, d->guid, 16);
            a.table = x["table"].get<std::string>();
            a.cmin = static_cast<uint32_t>(std::clamp<int64_t>(x.value("countMin", int64_t(1)), 1, 100000));
            a.cmax = static_cast<uint32_t>(std::clamp<int64_t>(x.value("countMax", int64_t(a.cmin)), a.cmin, 100000));
            a.chance = static_cast<float>(std::clamp(x.value("chance", 1.0), 0.0, 1.0));
            a.weight = static_cast<float>(std::max(0.0, x.value("weight", 1.0)));
            out.push_back(a);
        }
    }
    return out;
}

struct GroupRef {
    uintptr_t addr;
    uint32_t depth;
};
void walk_groups(uintptr_t g, uint32_t depth, std::vector<GroupRef>* out) {
    if (depth > 8 || out->size() > 1024) return;
    out->push_back(GroupRef{g, depth});
    const BlobRef gr = blob_at(g + GL.groups);
    for (uint32_t i = 0; i < gr.count && i < 256; ++i) walk_groups(gr.data + i * static_cast<uintptr_t>(GL.size), depth + 1, out);
}

std::map<uintptr_t, uint64_t> g_drop_done;  // group header -> signature of what was added (under store_mutex)

uint32_t drops_for_table(const Entry& table, const std::vector<DropAdd>& adds) {
    if (!group_layout()) return 0;
    std::vector<GroupRef> groups;
    walk_groups(table.addr, 0, &groups);
    // the original groups only: a group we added has no table GUID match and holds only new ids
    std::map<uintptr_t, std::vector<const DropAdd*>> want;
    for (const DropAdd& a : adds) {
        if (a.table.rfind("like:", 0) == 0) {
            const Entry* like = reg(KILN_CONTENT_ITEMS).lookup(a.table.substr(5));
            if (!like) continue;
            for (const GroupRef& g : groups) {
                const BlobRef st = blob_at(g.addr + GL.stacks);
                for (uint32_t i = 0; i < st.count && i < 1024; ++i) {
                    uint32_t item = 0;
                    if (rdv(st.data + i * static_cast<uintptr_t>(GL.stack_size) + DL.s_item, item) && item == like->id) {
                        want[g.addr].push_back(&a);
                        break;
                    }
                }
            }
        } else if (glob_match(lower(a.table).c_str(), table.name.c_str())) {
            want[table.addr].push_back(&a);
        }
    }
    uint32_t n = 0;
    for (auto& [gaddr, list] : want) {
        uint8_t sel = 0;
        rdv(gaddr + GL.selector, sel);
        uint64_t sig = 1469598103934665603ull;
        for (const DropAdd* a : list) sig = (sig ^ a->id ^ (uint64_t(a->cmin) << 32) ^ a->cmax ^ uint64_t(a->chance * 1e6) ^ (uint64_t(a->weight * 1e3) << 20)) * 1099511628211ull;
        const uintptr_t header = gaddr + (sel ? GL.stacks : GL.groups);
        if (g_drop_done.count(header) && g_drop_done[header] == sig) {
            n += static_cast<uint32_t>(list.size());
            continue;
        }
        // the list as the game has it (the original header if we changed it before)
        uint8_t hb[8];
        if (!store_original(header, 8, hb) && !rd(header, hb, 8)) continue;
        int32_t off = 0;
        uint32_t cnt = 0;
        std::memcpy(&off, hb, 4);
        std::memcpy(&cnt, hb + 4, 4);
        const uintptr_t odata = off && cnt ? header + static_cast<intptr_t>(off) : 0;
        if (!odata) cnt = 0;
        auto stack_bytes = [&](const DropAdd& a, uint8_t* p) {
            std::memset(p, 0, GL.stack_size);
            std::memcpy(p + GL.s_ref, a.guid, 16);
            std::memcpy(p + DL.s_item, &a.id, 4);
            std::memcpy(p + DL.s_min, &a.cmin, 4);
            std::memcpy(p + DL.s_max, &a.cmax, 4);
            std::memcpy(p + DL.s_chance, &a.chance, 4);
            std::memcpy(p + DL.s_weight, &a.weight, 4);
        };
        uintptr_t ndata = 0;
        uint32_t ncount = 0;
        if (sel) {  // selector: the stacks copied (from the game's bytes) + the new ones
            const BlobRef og = blob_at(gaddr + GL.groups);
            if (cnt + list.size() + og.count > 64) {
                LOGW("new content: drop table %s: a selector group holds at most 64 entries; %zu new stack(s) left out",
                     table.name.c_str(), list.size());
                continue;
            }
            ncount = cnt + static_cast<uint32_t>(list.size());
            auto* p = static_cast<uint8_t*>(near_alloc(header, ncount * size_t(GL.stack_size)));
            if (!p) continue;
            for (uint32_t i = 0; i < cnt; ++i) {
                const uintptr_t sa = odata + i * static_cast<uintptr_t>(GL.stack_size);
                rd(sa, p + i * GL.stack_size, GL.stack_size);
                // the game's values, not the drop rules' (the rules are applied again to the copy)
                for (uint32_t fo : {DL.s_min, DL.s_max, DL.s_chance, DL.s_weight}) store_original(sa + fo, 4, p + i * GL.stack_size + fo);
            }
            for (size_t i = 0; i < list.size(); ++i) stack_bytes(*list[i], p + (cnt + i) * GL.stack_size);
            ndata = reinterpret_cast<uintptr_t>(p);
        } else {  // plain group: its child groups copied (headers rebased) + one group with the new stacks
            ncount = cnt + 1;
            const size_t gbytes = ncount * size_t(GL.size), sbytes = list.size() * size_t(GL.stack_size);
            auto* p = static_cast<uint8_t*>(near_alloc(header, gbytes + sbytes));
            if (!p) continue;
            std::memset(p, 0, gbytes + sbytes);
            bool ok = true;
            for (uint32_t i = 0; i < cnt && ok; ++i) {
                const uintptr_t ga = odata + i * static_cast<uintptr_t>(GL.size);
                ok = rd(ga, p + i * GL.size, GL.size);
                for (uint32_t o : GL.relocs) ok = ok && rebase(p + i * GL.size, ga, o);
            }
            if (!ok) continue;
            uint8_t* ng = p + cnt * GL.size;  // isSelector 0, no child groups, no labels
            uint8_t* ns = p + gbytes;
            for (size_t i = 0; i < list.size(); ++i) stack_bytes(*list[i], ns + i * GL.stack_size);
            set_blob(ng, GL.stacks, ns, static_cast<uint32_t>(list.size()));
            ndata = reinterpret_cast<uintptr_t>(p);
        }
        const int64_t rel = static_cast<int64_t>(ndata) - static_cast<int64_t>(header);
        if (rel < INT32_MIN || rel > INT32_MAX) continue;
        uint8_t nb[8];
        const int32_t o32 = static_cast<int32_t>(rel);
        std::memcpy(nb, &o32, 4);
        std::memcpy(nb + 4, &ncount, 4);
        if (!store_put(g_owner, next_code_seq(), KILN_CONTENT_DROPS, table.id, -1, kSubBlob, header, 8, nb, "new content: drops"))
            continue;
        g_drop_done[header] = sig;
        n += static_cast<uint32_t>(list.size());
        LOGI("new content: drop table %s: %zu new stack(s) in a %s group (depth %u)", table.name.c_str(), list.size(),
             sel ? "selector" : "plain", 0u);
    }
    // groups that had additions and no longer do: the original header back
    for (auto it = g_drop_done.begin(); it != g_drop_done.end();) {
        bool mine = false;
        for (const GroupRef& g : groups) mine = mine || it->first == g.addr + GL.stacks || it->first == g.addr + GL.groups;
        const bool still = mine && (want.count(it->first - GL.stacks) || want.count(it->first - GL.groups));
        if (mine && !still) {
            store_drop(g_owner, it->first);
            it = g_drop_done.erase(it);
        } else {
            ++it;
        }
    }
    return n;
}

}  // namespace

// content.cpp's DefaultInventoryAcquire hook (job thread, store_mutex held): a table seen for the first time.
void nc_drops_table(const Entry& table) {
    if (!N.on) return;
    const std::vector<DropAdd> adds = drop_adds();
    if (adds.empty() && g_drop_done.empty()) return;
    if (drops_for_table(table, adds)) {
        std::string err;
        store_flush(&err);          // the new headers in memory first: the rules walk the live tree
        drops_apply_rules(table);   // so the new stacks (and selector copies) get the drop rules too
    }
}

namespace {

// After definitions change (server world thread): every table seen so far.
uint64_t g_drops_version = 0;
void drops_sync() {
    const uint64_t v = g_version.load();
    if (v == g_drops_version) return;
    PhaseTimer pt(PH_DROPS);
    g_drops_version = v;
    const std::vector<DropAdd> adds = drop_adds();
    std::lock_guard lock(store_mutex());
    if (adds.empty() && g_drop_done.empty()) return;
    const Registry& D = reg(KILN_CONTENT_DROPS);
    uint32_t n = 0;
    for (const Entry& t : D.entries) {
        const uint32_t k = drops_for_table(t, adds);
        if (k) {
            std::string ferr;
            store_flush(&ferr);
            drops_apply_rules(t);
        }
        n += k;
    }
    std::string err;
    store_flush(&err);
    if (!err.empty()) LOGW("new content: drops: %s", err.c_str());
}

void drops_world_reset() {
    std::lock_guard lock(store_mutex());
    g_drop_done.clear();
    g_drops_version = 0;
}

// ============================================================================================= owners ==========
void KILN_CALL noop(void*) {}

void probe_owners(uint32_t kind) {
    std::vector<DefPtr> gone;
    {
        std::lock_guard lock(store_mutex());
        for (const DefPtr& d : g_defs)
            if (!d->orphan && !d->pack && d->owner && g_api->post_world(d->owner, kind, &noop, nullptr) == KILN_ERR_REVOKED)
                gone.push_back(d);
        for (const DefPtr& d : gone) d->orphan = true;
    }
    if (gone.empty()) return;
    for (const DefPtr& d : gone) {
        bool in = false;
        for (const WorldState& ws : g_ws) in = in || ws.items.count(d->id) || ws.recipes.count(d->id);
        if (!in)
            LOGI("new content: %s went away; %s %s was not registered (%s)", d->owner_label.c_str(),
                 d->kind == KILN_CONTENT_ITEMS ? "item" : "recipe", d->name.c_str(), d->error.empty() ? "pending" : d->error.c_str());
        else
            LOGI("new content: %s went away; %s %s stays registered until the server restarts%s", d->owner_label.c_str(),
                 d->kind == KILN_CONTENT_ITEMS ? "item" : "recipe", d->name.c_str(),
                 d->kind == KILN_CONTENT_RECIPES ? " (taken off its station: no longer craftable)" : " (saved stacks are kept)");
    }
    g_version.fetch_add(1);
}

uint64_t g_tick_count[KILN_WORLD_KINDS] = {};

// References that were not loaded when a clone was built (a mod's resource that kiln.assets registers later): every
// ~2 s the definition's kitbash is applied to its clones again; what resolves now is written. World thread.
void retry_kitbash(WorldState& ws) {
    if (!assets_api()) return;
    std::vector<DefPtr> todo;
    {
        std::lock_guard lock(store_mutex());
        for (const DefPtr& d : g_defs)
            if (d->kind == KILN_CONTENT_ITEMS && d->kitbash_retry && !d->clones.empty()) todo.push_back(d);
    }
    if (todo.empty()) return;
    const std::vector<uintptr_t> all = world_items(ws);
    for (const DefPtr& dp : todo) {
        Def& d = *dp;
        std::lock_guard lock(store_mutex());
        for (auto& [base, clone] : d.clones) {
            auto* p = reinterpret_cast<uint8_t*>(clone);
            const ItemFind find = [&](const std::string& sel) -> uintptr_t {
                NameRec r{};
                if (lower(sel) == lower(d.name) || lower(sel) == d.guid_s) return clone;
                return resolve_item(ws, sel, &r) ? r.info : 0;
            };
            KitbashOut ko;
            for (auto& [key, val] : d.kitbash.items()) kitbash(L.item, key, val, find, all, &ko, clone);
            if (ko.problems.size() >= d.warnings.size()) continue;
            for (const RefWrite& w : ko.writes) std::memcpy(p + w.f->offset, w.bytes, w.f->bytes);
            for (const BlobWrite& b : ko.blobs)
                if (const int64_t rel = static_cast<int64_t>(b.data) - static_cast<int64_t>(clone + b.offset);
                    !b.data || (rel >= INT32_MIN && rel <= INT32_MAX))
                    set_blob(p, b.offset, reinterpret_cast<const void*>(b.data), b.data ? b.count : 0);
            LOGI("new content: item %s: %zu reference(s) resolved late (served through kiln.assets); %zu problem(s) left",
                 d.name.c_str(), d.warnings.size() - ko.problems.size(), ko.problems.size());
            d.warnings = ko.problems;
        }
        d.kitbash_retry = std::any_of(d.warnings.begin(), d.warnings.end(),
                                      [](const std::string& w) { return w.find("the game has loaded") != std::string::npos; });
    }
}

// A template clone went into a world's table since this kind of world last looked: what named it can resolve now.
bool templates_changed(uint32_t kind) {
#if KC_GAMEDATA
    static std::atomic<uint64_t> seen[KILN_WORLD_KINDS] = {};
    const uint64_t g = gd_template_generation();
    return seen[kind].exchange(g, std::memory_order_relaxed) != g;
#else
    (void)kind;
    return false;
#endif
}

// Every step until the definitions are in (a registry becomes usable inside a step; the sooner the insert, the less
// can run before it), then a cheap version check.
void world_tick(uint32_t kind, uintptr_t world, uint64_t step) {
    if (!world) return;
    sync_world(kind, world);
    if (step % 25 == 0) probe_owners(kind);
    const bool fresh = templates_changed(kind);
    if ((fresh || step % 50 == 7) && g_ws[kind].world == world && g_ws[kind].ireg) retry_kitbash(g_ws[kind]);
}

void KILN_CALL on_server_pre(const KilnTickInfo* t, void*) { world_tick(KILN_WORLD_SERVER, g_api->server_world(), t->step); }
// Diagnostics on a client, only with <kiln>/data/content/debug-ui (every ~10 s while a client world runs): loca hook
// traffic, the crafting UI's hand list (G38UiPlayData offsets of build 26404966: debugging aid only) and, with
// <kiln>/data/content/loca-trace too, every distinct tag id the game looks up.
// ---- the crafting menu (client): it lists only recipes named in its UI configuration ----
// Per workshop id a list of categories, each with subcategories holding a BlobArray of recipe ids (docs/re/content.md
// section 7). A new recipe goes into the subcategory that holds its base recipe; else the one that lists the game's
// recipe for the item its result was cloned from (a new chair beside the chair it started from); else, for a result
// that places a thing, the first subcategory in the menu's order that lists placed things of the same item category
// (building tools are blocks and placed things alike: by category alone, a new station went to the stone blocks);
// else one whose recipes make the most items of the same item category; else the workshop's last subcategory. The
// subcategory's 8-byte BlobArray
// header (read-only resource memory) is pointed at a copy with the new ids appended; the original comes back when no
// new recipe is left in it. Main thread (client-world Pre).
struct MenuSub {
    uintptr_t header = 0;
    int32_t orig_off = 0;
    uint32_t orig_count = 0;
    std::vector<uint32_t> orig;
    std::vector<uint32_t> added;  // what the header shows now (beyond orig)
};
std::map<uintptr_t, MenuSub> g_menu;
uint64_t g_menu_done = 0;


uint32_t output_item(uintptr_t recipe) {
    const BlobRef out = blob_at(recipe + L.r_out);
    uint32_t item = 0;
    if (!out.count || !rdv(out.data + L.out_item, item)) return 0;
    return item;
}

uint8_t output_category(uintptr_t ireg, uintptr_t recipe, uint32_t cat_off) {
    const uint32_t item = output_item(recipe);
    if (!item) return 0xFF;
    const uintptr_t info = static_cast<uintptr_t>(map_get(ireg + E.items_map, item));
    uint8_t c = 0xFF;
    if (info) rdv(info + cat_off, c);
    return c;
}

// An item a player places with the build cursor (a prop, a station, furniture): its slot is the build tool's, it names
// a template, and it isn't a building block. (A spell or an arrow names a template too: what it fires.)
bool places_thing(uintptr_t info) {
    static int64_t placed = -1, voxel = -1, slot = -1, build_tool = -1;  // looked up once the schema has them
    if (placed < 0)
        if (const Field* f = L.item.find("equipment.placedEntity")) placed = f->offset;
    if (voxel < 0)
        if (const Field* f = L.item.find("equipment.voxelData.isBuildingVoxel")) voxel = f->offset;
    if (slot < 0)
        if (const Field* f = L.item.find("equipment.slot")) slot = f->offset;
    static bool asked = false;
    if (build_tool < 0 && !asked) {
        asked = true;
        std::vector<std::pair<std::string, int64_t>> values;
        if (enum_values("keen::EquipmentSlotType", &values))
            for (const auto& [name, value] : values)
                if (name == "BuildTool") build_tool = value;
    }
    if (!info || placed < 0 || slot < 0 || build_tool < 0) return false;
    uint8_t s = 0xFF, block = 0;
    uint64_t g[2] = {};
    if (!rdv(info + static_cast<uintptr_t>(slot), s) || s != build_tool) return false;
    if (!rd(info + static_cast<uintptr_t>(placed), g, 16) || !(g[0] | g[1])) return false;
    return !(voxel >= 0 && rdv(info + static_cast<uintptr_t>(voxel), block) && block);
}


// kiln.craftmenu (below): the entries-header addresses of Kiln's own subcategories and the recipes they list; the
// automatic placement of new recipes leaves both alone.
std::mutex g_cm_mu;
std::set<uintptr_t> g_cm_headers;     // entries headers of Kiln subcategories (in Kiln's copies)
std::set<uintptr_t> g_cm_groups;      // Kiln tabs (addresses in Kiln's copies)
std::set<uint32_t> g_cm_recipes;      // recipe ids some Kiln subcategory lists

void menu_sync(const WorldState& ws) {
    if (!N.menu || !ws.rreg || !ws.ireg) return;
    const uint64_t version = g_version.load();
    std::vector<std::pair<uint32_t, uintptr_t>> listed;
    {
        std::lock_guard lock(g_ui_mu);
        for (auto& [id, info] : ws.listed) listed.emplace_back(id, info);
    }
    const uint64_t key = version ^ (static_cast<uint64_t>(listed.size()) << 48) ^ ws.rreg;
    if (key == g_menu_done) return;
    uintptr_t app = 0, cfg = 0;
    if (!rdv(N.ui_app, app) || !app || !rdv(app + N.app_config, cfg) || !cfg) return;  // UI not up yet: retry later
    const BlobRef wss = blob_at(cfg + N.menu_ws);
    const BlobRef cats = blob_at(cfg + N.menu_ws + 8);
    static int said = 0;
    if (said < 3) {
        ++said;
        std::string ids;
        for (uint32_t i = 0; i < wss.count && i < 64; ++i) {
            uint32_t w = 0;
            rdv(wss.data + i * 4ull, w);
            char b[16];
            std::snprintf(b, sizeof b, " %x", w);
            ids += b;
        }
        LOGD("new content: crafting menu config %p: %u workshop(s), %u category list(s); workshops:%s", reinterpret_cast<void*>(cfg),
             wss.count, cats.count, ids.c_str());
    }
    if (!wss.count || cats.count < wss.count) return;
    const Field* catf = L.item.find("category");
    std::map<uintptr_t, std::vector<uint32_t>> want;  // subcategory header -> new ids
    std::set<uintptr_t> kiln_headers, kiln_groups;
    std::set<uint32_t> kiln_recipes;
    {
        std::lock_guard lock(g_cm_mu);
        kiln_headers = g_cm_headers;
        kiln_groups = g_cm_groups;
        kiln_recipes = g_cm_recipes;
    }
    for (auto& [id, info] : listed) {
        if (kiln_recipes.count(id)) continue;  // a Kiln subcategory lists it (kiln.craftmenu)
        DefPtr def;
        {
            std::lock_guard lock(store_mutex());
            for (const DefPtr& d : g_defs)
                if (d->kind == KILN_CONTENT_RECIPES && d->id == id) def = d;
        }
        // the menu it shows in: its own workshop's, or the one its definition names (a workshop of its own that opens another's menu)
        uint32_t wid = 0;
        rdv(info + L.r_wid, wid);
        if (def && def->menu_workshop) wid = def->menu_workshop;
        uint32_t wi = UINT32_MAX;
        for (uint32_t i = 0; i < wss.count; ++i) {
            uint32_t w = 0;
            if (rdv(wss.data + i * 4ull, w) && w == wid) wi = i;
        }
        if (wi == UINT32_MAX) {
            static int miss = 0;
            if (miss++ < 4) LOGI("new content: the crafting menu has no categories for workshop 0x%08x", wid);
            continue;
        }
        const BlobRef cl = blob_at(cats.data + wi * 8ull);
        uintptr_t by_base = 0, by_cat = 0, last = 0;
        uint32_t best = 0;
        uint32_t base_id = 0;
        if (def && !def->base.empty()) {
            uint8_t g[16];
            if (def->base.size() == 36 && guid_parse(lower(def->base), g)) base_id = fnv1a(g, 16);
            else if (const uintptr_t b = resolve_recipe(const_cast<WorldState&>(ws), def->base)) rdv(b + L.r_id, base_id);
        }
        const uint8_t my_cat = catf ? output_category(ws.ireg, info, catf->offset) : 0xFF;
        // the item its result was cloned from, when that is a definition of ours: the game's recipes for it show where it belongs
        const uint32_t my_item = output_item(info);
        uint32_t like_id = 0;
        {
            std::string like;
            {
                std::lock_guard lock(store_mutex());
                for (const DefPtr& d : g_defs)
                    if (d->kind == KILN_CONTENT_ITEMS && d->id == my_item) like = d->base;
            }
            NameRec r{};
            if (!like.empty() && resolve_item(const_cast<WorldState&>(ws), like, &r)) like_id = r.id;
        }
        const bool my_placed = places_thing(static_cast<uintptr_t>(map_get(ws.ireg + E.items_map, my_item)));
        uintptr_t by_like = 0, by_placed = 0;
        for (uint32_t c = 0; c < cl.count && c < 256; ++c) {
            const uintptr_t cat = cl.data + c * static_cast<uintptr_t>(N.cat_stride);
            if (kiln_groups.count(cat)) continue;
            const BlobRef subs = blob_at(cat + N.cat_subs);
            for (uint32_t k = 0; k < subs.count && k < 256; ++k) {
                const uintptr_t sub = subs.data + k * static_cast<uintptr_t>(N.sub_stride);
                const uintptr_t hdr = sub + N.sub_recipes;
                if (kiln_headers.count(hdr)) continue;
                last = hdr;
                auto it = g_menu.find(hdr);
                const BlobRef ids = it != g_menu.end() ? BlobRef{it->second.orig_off ? hdr + it->second.orig_off : 0, it->second.orig_count}
                                                       : blob_at(hdr);
                uint32_t same = 0;
                for (uint32_t r = 0; r < ids.count && r < 1024; ++r) {
                    uint32_t rid = 0;
                    if (!rdv(ids.data + r * 4ull, rid) || !rid) continue;
                    if (base_id && rid == base_id) by_base = hdr;
                    if ((like_id && !by_like) || (my_cat != 0xFF && catf)) {
                        const uintptr_t ri = static_cast<uintptr_t>(map_get(ws.rreg + E.recipes_map, rid));
                        if (!ri) continue;
                        const uint32_t made = output_item(ri);
                        if (like_id && !by_like && made == like_id) by_like = hdr;
                        if (my_cat != 0xFF && catf && output_category(ws.ireg, ri, catf->offset) == my_cat) {
                            ++same;
                            if (my_placed && !by_placed && places_thing(static_cast<uintptr_t>(map_get(ws.ireg + E.items_map, made)))) by_placed = hdr;
                        }
                    }
                }
                if (same > best) best = same, by_cat = hdr;
            }
        }
        const uintptr_t target = by_base ? by_base : by_like ? by_like : by_placed ? by_placed : by_cat ? by_cat : last;
        if (target) want[target].push_back(id);
        LOGD("new content: menu placement of 0x%08x (workshop %x, index %u): base %p, like %p, placed %p, category %p, last %p", id, wid, wi,
             reinterpret_cast<void*>(by_base), reinterpret_cast<void*>(by_like), reinterpret_cast<void*>(by_placed),
             reinterpret_cast<void*>(by_cat), reinterpret_cast<void*>(last));
    }
    // headers that had new ids but no longer do: back to the original
    for (auto& [hdr, m] : g_menu)
        if (!want.count(hdr)) want[hdr];
    std::vector<Write> writes;
    uint32_t placed = 0;
    for (auto& [hdr, ids] : want) {
        auto [it, fresh] = g_menu.try_emplace(hdr);
        MenuSub& m = it->second;
        if (fresh) {
            m.header = hdr;
            rdv(hdr, m.orig_off);
            rdv(hdr + 4, m.orig_count);
            const BlobRef o = blob_at(hdr);
            for (uint32_t r = 0; r < o.count && r < 1024; ++r) {
                uint32_t rid = 0;
                rdv(o.data + r * 4ull, rid);
                m.orig.push_back(rid);
            }
        }
        std::vector<uint32_t> add;
        for (uint32_t id : ids)
            if (std::find(m.orig.begin(), m.orig.end(), id) == m.orig.end() && std::find(add.begin(), add.end(), id) == add.end())
                add.push_back(id);
        if (add == m.added) continue;
        Write w{hdr, 8, {}};
        if (add.empty()) {
            std::memcpy(w.bytes, &m.orig_off, 4);
            std::memcpy(w.bytes + 4, &m.orig_count, 4);
        } else {
            const size_t n = m.orig.size() + add.size();
            auto* arr = static_cast<uint32_t*>(near_alloc(hdr, n * 4));
            if (!arr) continue;
            std::memcpy(arr, m.orig.data(), m.orig.size() * 4);
            std::memcpy(arr + m.orig.size(), add.data(), add.size() * 4);
            const int64_t off = static_cast<int64_t>(reinterpret_cast<uintptr_t>(arr)) - static_cast<int64_t>(hdr);
            if (off < INT32_MIN || off > INT32_MAX) continue;
            const int32_t o32 = static_cast<int32_t>(off);
            const uint32_t c32 = static_cast<uint32_t>(n);
            std::memcpy(w.bytes, &o32, 4);
            std::memcpy(w.bytes + 4, &c32, 4);
        }
        writes.push_back(w);
        m.added = add;
        placed += static_cast<uint32_t>(add.size());
    }
    if (!writes.empty()) {
        uintptr_t failed = 0;
        const size_t done = write_batch(writes, &failed);
        LOGI("new content: the crafting menu lists %u new recipe(s) (%zu of %zu subcategory list(s) rewritten)%s", placed,
             done, writes.size(), failed ? " (a write failed)" : "");
    }
    g_menu_done = key;
}

// ---- kiln.craftmenu: Kiln tabs and subcategories in the crafting menu (kiln.ui's extension point) ----
// The menu's configuration (keen::FbUiCraftingRecipeCollection at cfg + menu_ws): workshops (u32 BlobArray), trees
// (per workshop a BlobArray of keen::FbUiCraftingRecipeGroup = tabs), maxRecipeGroupCount / maxRecipeSetCount (the
// game sizes its menu by these). A workshop that gets Kiln tabs or subcategories has its tree header (8 bytes,
// read-only resource memory, write_batch) pointed at a copy of its tabs in near memory: the game's tabs (their
// subcategory arrays copied too when Kiln adds a subcategory to them, each BlobArray offset rebased) followed by
// Kiln's. Labels are new LocaTagReference GUIDs whose tag ids (FNV-1a of the GUID) the loca hooks answer (set_texts).
// Rebuilt from the original data whenever the set of additions changes; before that, the automatic placements of new
// recipes (g_menu) are put back so that they are redone on the new layout. UI thread (the lookup hook).
struct CmAdd {
    KilnHandle handle;
    KilnOwner owner;
    bool tab;            // a new tab (true) or a subcategory in tab `tab_index`
    uint32_t workshop;
    int32_t tab_index;   // set: target tab (-1 = last); tab: icon source (-1 = last)
    uint8_t label[16];   // LocaTagReference GUID (zero for a new tab's single subcategory)
    uint8_t id[16];      // groupId / recipeSetId
    std::vector<uint32_t> recipes;
    std::string label_json;
};
struct CmSub {
    KilnHandle handle;
    KilnOwner owner;
    KilnCraftMenuFn fn;
    void* user;
};
std::vector<CmAdd> g_cm_adds;
std::vector<CmSub> g_cm_subs;
std::atomic<uint64_t> g_cm_version{0};
uint64_t g_cm_built = 0;
std::atomic<KilnHandle> g_cm_next{1};
std::map<uint32_t, std::pair<uintptr_t, uint64_t>> g_cm_trees;  // workshop -> (tree header, original 8 bytes)
struct CmLayout {
    bool ok = false;
    int64_t max_groups = -1, max_sets = -1;  // offsets in the collection
    int64_t g_label = -1, g_id = -1, s_label = -1, s_id = -1;
    uint32_t gsize = 0, ssize = 0;
} CL;

void cm_layout() {
    static bool tried = false;
    if (tried) return;
    tried = true;
    CL.max_groups = field_offset("keen::FbUiCraftingRecipeCollection", "maxRecipeGroupCount");
    CL.max_sets = field_offset("keen::FbUiCraftingRecipeCollection", "maxRecipeSetCount");
    CL.g_label = field_offset("keen::FbUiCraftingRecipeGroup", "label");
    CL.g_id = field_offset("keen::FbUiCraftingRecipeGroup", "groupId");
    CL.s_label = field_offset("keen::FbUiCraftingRecipeSet", "label");
    CL.s_id = field_offset("keen::FbUiCraftingRecipeSet", "recipeSetId");
    CL.gsize = type_size("keen::FbUiCraftingRecipeGroup");
    CL.ssize = type_size("keen::FbUiCraftingRecipeSet");
    CL.ok = CL.max_groups >= 0 && CL.max_sets >= 0 && CL.g_label >= 0 && CL.g_id >= 0 && CL.s_label >= 0 && CL.s_id >= 0 &&
            CL.gsize == static_cast<uint32_t>(N.cat_stride) && CL.ssize == static_cast<uint32_t>(N.sub_stride);
    LOGI("kiln.craftmenu: layout %s (tab %u B, subcategory %u B, labels +0x%llx / +0x%llx)", CL.ok ? "ok" : "MISMATCH: additions off",
         CL.gsize, CL.ssize, (unsigned long long)CL.g_label, (unsigned long long)CL.s_label);
}

// "text" or {"en": "...", "de": "..."} -> TextSet
TextSet cm_texts(const std::string& s) {
    TextSet t;
    if (!s.empty() && s.front() == '{') {
        try {
            const json j = json::parse(s);
            for (auto it = j.begin(); it != j.end(); ++it)
                if (it.value().is_string()) {
                    const std::string k = lang_key(it.key());
                    if (valid_lang(k)) t[k] = it.value().get<std::string>();
                }
            return t;
        } catch (...) {
        }
    }
    if (!s.empty()) t["*"] = s;
    return t;
}

void cm_guid(const std::string& key, uint8_t out[16]) {
    uint64_t a = 0xcbf29ce484222325ull, b = 0x84222325cbf29ce4ull;
    for (unsigned char c : key) {
        a = (a ^ c) * 0x100000001b3ull;
        b = (b ^ (c + 0x5bu)) * 0x100000001b3ull;
    }
    std::memcpy(out, &a, 8);
    std::memcpy(out + 8, &b, 8);
    out[7] = static_cast<uint8_t>((out[7] & 0x0F) | 0x40);  // a v4-shaped GUID, never a game GUID by chance
}

bool cm_rebase(uintptr_t from_field, uintptr_t to_field, uint8_t* to_bytes) {
    // the BlobArray header copied to to_field keeps pointing at what from_field pointed at
    int32_t off = 0;
    uint32_t n = 0;
    if (!rdv(from_field, off) || !rdv(from_field + 4, n)) return false;
    int32_t o2 = 0;
    if (off) {
        const int64_t target = static_cast<int64_t>(from_field) + off;
        const int64_t d = target - static_cast<int64_t>(to_field);
        if (d < INT32_MIN || d > INT32_MAX) return false;
        o2 = static_cast<int32_t>(d);
    }
    std::memcpy(to_bytes, &o2, 4);
    std::memcpy(to_bytes + 4, &n, 4);
    return true;
}

bool cm_point(uint8_t* field_bytes, uintptr_t field_addr, uintptr_t data, uint32_t count) {
    const int64_t d = data ? static_cast<int64_t>(data) - static_cast<int64_t>(field_addr) : 0;
    if (d < INT32_MIN || d > INT32_MAX) return false;
    const int32_t o = static_cast<int32_t>(d);
    std::memcpy(field_bytes, &o, 4);
    std::memcpy(field_bytes + 4, &count, 4);
    return true;
}

// Puts content's subcategory rewrites back (their headers may move with Kiln's copies) so that menu_sync redoes them.
void cm_reset_placements() {
    std::vector<Write> writes;
    for (auto& [hdr, m] : g_menu) {
        if (m.added.empty()) continue;
        Write w{hdr, 8, {}};
        std::memcpy(w.bytes, &m.orig_off, 4);
        std::memcpy(w.bytes + 4, &m.orig_count, 4);
        writes.push_back(w);
    }
    if (!writes.empty()) {
        uintptr_t failed = 0;
        write_batch(writes, &failed);
    }
    g_menu.clear();
    g_menu_done = 0;
}

// (Re)builds Kiln's tabs and subcategories from the original configuration. UI thread, UI pointer valid.
void cm_build(uintptr_t cfg) {
    cm_layout();
    std::vector<CmAdd> adds;
    {
        std::lock_guard lock(g_cm_mu);
        adds = g_cm_adds;
    }
    cm_reset_placements();
    std::vector<Write> writes;
    // every tree header back to the original first
    for (auto& [wid, tr] : g_cm_trees) {
        Write w{tr.first, 8, {}};
        std::memcpy(w.bytes, &tr.second, 8);
        writes.push_back(w);
    }
    std::set<uintptr_t> kh, kg;
    std::set<uint32_t> kr;
    const uintptr_t coll = cfg + N.menu_ws;
    const BlobRef wss = blob_at(coll);
    uint32_t max_groups = 0, max_sets = 0;
    if (CL.ok) {
        rdv(coll + CL.max_groups, max_groups);
        rdv(coll + CL.max_sets, max_sets);
    }
    std::map<uint32_t, std::vector<const CmAdd*>> by_ws;
    for (const CmAdd& a : adds) by_ws[a.workshop].push_back(&a);
    uint32_t placed_tabs = 0, placed_sets = 0;
    for (auto& [wid, list] : by_ws) {
        if (!CL.ok) break;
        uint32_t wi = UINT32_MAX;
        for (uint32_t i = 0; i < wss.count; ++i) {
            uint32_t w = 0;
            if (rdv(wss.data + i * 4ull, w) && w == wid) wi = i;
        }
        if (wi == UINT32_MAX) {
            LOGW("kiln.craftmenu: the crafting menu has no workshop 0x%08x: %zu addition(s) not shown", wid, list.size());
            continue;
        }
        const uintptr_t tree = blob_at(coll + 8).data + wi * 8ull;
        auto known = g_cm_trees.find(wid);
        uint64_t orig = 0;
        if (known != g_cm_trees.end()) orig = known->second.second;
        else if (!rdv(tree, orig)) continue;
        g_cm_trees[wid] = {tree, orig};
        // the original tabs (read through the original header: the tree header may still show Kiln's copy until the
        // batch below is written)
        int32_t ooff = 0;
        uint32_t ocount = 0;
        std::memcpy(&ooff, &orig, 4);
        std::memcpy(&ocount, reinterpret_cast<const uint8_t*>(&orig) + 4, 4);
        const uintptr_t ogroups = ooff ? tree + static_cast<intptr_t>(ooff) : 0;
        if (!ogroups || !ocount || ocount > 256) continue;
        const uint32_t gs = CL.gsize, ss = CL.ssize;
        // per original tab: Kiln subcategories added to it
        std::vector<std::vector<const CmAdd*>> sets_for(ocount);
        std::vector<const CmAdd*> tabs;
        for (const CmAdd* a : list) {
            if (a->tab) {
                if (ocount + tabs.size() + 1 > max_groups) {
                    LOGW("kiln.craftmenu: workshop 0x%08x already has %u tab(s); the game's menu holds %u: tab refused", wid,
                         static_cast<unsigned>(ocount + tabs.size()), max_groups);
                    continue;
                }
                tabs.push_back(a);
            } else {
                const uint32_t ti = a->tab_index < 0 || static_cast<uint32_t>(a->tab_index) >= ocount ? ocount - 1 : static_cast<uint32_t>(a->tab_index);
                const BlobRef os = blob_at(ogroups + ti * static_cast<uintptr_t>(gs) + N.cat_subs);
                if (os.count + sets_for[ti].size() + 1 > max_sets) {
                    LOGW("kiln.craftmenu: tab %u of workshop 0x%08x already has %u subcategories; the game's menu holds %u: refused", ti,
                         wid, static_cast<unsigned>(os.count + sets_for[ti].size()), max_sets);
                    continue;
                }
                sets_for[ti].push_back(a);
            }
        }
        const uint32_t ng = ocount + static_cast<uint32_t>(tabs.size());
        auto* groups = static_cast<uint8_t*>(near_alloc(tree, size_t(ng) * gs));
        if (!groups) {
            LOGW("kiln.craftmenu: no memory near the menu configuration");
            continue;
        }
        const uintptr_t G = reinterpret_cast<uintptr_t>(groups);
        bool ok = true;
        for (uint32_t i = 0; i < ocount && ok; ++i) {
            const uintptr_t src = ogroups + i * static_cast<uintptr_t>(gs);
            uint8_t* dst = groups + size_t(i) * gs;
            for (uint32_t b = 0; b < gs; ++b) rdv(src + b, dst[b]);
            if (sets_for[i].empty()) {
                ok = cm_rebase(src + N.cat_subs, G + i * uintptr_t(gs) + N.cat_subs, dst + N.cat_subs);
                continue;
            }
            const BlobRef os = blob_at(src + N.cat_subs);
            const uint32_t ns = os.count + static_cast<uint32_t>(sets_for[i].size());
            auto* sets = static_cast<uint8_t*>(near_alloc(tree, size_t(ns) * ss));
            if (!sets) {
                ok = false;
                break;
            }
            const uintptr_t S = reinterpret_cast<uintptr_t>(sets);
            for (uint32_t k = 0; k < os.count && ok; ++k) {
                const uintptr_t s0 = os.data + k * uintptr_t(ss);
                for (uint32_t b = 0; b < ss; ++b) rdv(s0 + b, sets[size_t(k) * ss + b]);
                ok = cm_rebase(s0 + N.sub_recipes, S + k * uintptr_t(ss) + N.sub_recipes, sets + size_t(k) * ss + N.sub_recipes);
            }
            for (size_t j = 0; j < sets_for[i].size() && ok; ++j) {
                const CmAdd* a = sets_for[i][j];
                const uint32_t k = os.count + static_cast<uint32_t>(j);
                uint8_t* s = sets + size_t(k) * ss;
                std::memset(s, 0, ss);
                std::memcpy(s + CL.s_label, a->label, 16);
                std::memcpy(s + CL.s_id, a->id, 16);
                auto* ids = static_cast<uint32_t*>(near_alloc(tree, std::max<size_t>(4, a->recipes.size() * 4)));
                if (!ids) {
                    ok = false;
                    break;
                }
                std::memcpy(ids, a->recipes.data(), a->recipes.size() * 4);
                ok = cm_point(s + N.sub_recipes, S + k * uintptr_t(ss) + N.sub_recipes, reinterpret_cast<uintptr_t>(ids),
                              static_cast<uint32_t>(a->recipes.size()));
                kh.insert(S + k * uintptr_t(ss) + N.sub_recipes);
                for (uint32_t r : a->recipes) kr.insert(r);
                ++placed_sets;
            }
            if (ok) ok = cm_point(dst + N.cat_subs, G + i * uintptr_t(gs) + N.cat_subs, S, ns);
        }
        for (size_t j = 0; j < tabs.size() && ok; ++j) {
            const CmAdd* a = tabs[j];
            const uint32_t i = ocount + static_cast<uint32_t>(j);
            const uint32_t from = a->tab_index < 0 || static_cast<uint32_t>(a->tab_index) >= ocount ? ocount - 1 : static_cast<uint32_t>(a->tab_index);
            uint8_t* dst = groups + size_t(i) * gs;
            std::memcpy(dst, groups + size_t(from) * gs, gs);  // the icon (and anything else) of tab `from`
            std::memcpy(dst + CL.g_id, a->id, 16);
            std::memcpy(dst + CL.g_label, a->label, 16);
            auto* set = static_cast<uint8_t*>(near_alloc(tree, ss));
            auto* ids = static_cast<uint32_t*>(near_alloc(tree, std::max<size_t>(4, a->recipes.size() * 4)));
            if (!set || !ids) {
                ok = false;
                break;
            }
            std::memset(set, 0, ss);  // one unnamed subcategory, like the first one of the game's tabs
            std::memcpy(ids, a->recipes.data(), a->recipes.size() * 4);
            const uintptr_t S = reinterpret_cast<uintptr_t>(set);
            ok = cm_point(set + N.sub_recipes, S + N.sub_recipes, reinterpret_cast<uintptr_t>(ids), static_cast<uint32_t>(a->recipes.size())) &&
                 cm_point(dst + N.cat_subs, G + i * uintptr_t(gs) + N.cat_subs, S, 1);
            kh.insert(S + N.sub_recipes);
            kg.insert(G + i * uintptr_t(gs));
            for (uint32_t r : a->recipes) kr.insert(r);
            ++placed_tabs;
        }
        if (!ok) {
            LOGW("kiln.craftmenu: workshop 0x%08x: copy out of reach; additions not shown", wid);
            continue;
        }
        Write w{tree, 8, {}};
        if (!cm_point(w.bytes, tree, G, ng)) continue;
        // replace the "back to original" write of this tree queued above
        std::erase_if(writes, [&](const Write& x) { return x.addr == tree; });
        writes.push_back(w);
    }
    uintptr_t failed = 0;
    const size_t done = writes.empty() ? 0 : write_batch(writes, &failed);
    {
        std::lock_guard lock(g_cm_mu);
        g_cm_headers = kh;
        g_cm_groups = kg;
        g_cm_recipes = kr;
    }
    LOGI("kiln.craftmenu: %u tab(s) and %u subcategor%s in the crafting menu (%zu of %zu header(s) written%s; limits %u tabs, %u per tab)",
         placed_tabs, placed_sets, placed_sets == 1 ? "y" : "ies", done, writes.size(), failed ? ", a write failed" : "", max_groups, max_sets);
}

void cm_sync(int32_t workshop) {
    uintptr_t app = 0, cfg = 0;
    if (!rdv(N.ui_app, app) || !app || !rdv(app + N.app_config, cfg) || !cfg) return;
    const uint64_t v = g_cm_version.load();
    if (v != g_cm_built) {
        g_cm_built = v;
        cm_build(cfg);
    }
    std::vector<CmSub> subs;
    {
        std::lock_guard lock(g_cm_mu);
        subs = g_cm_subs;
    }
    if (subs.empty()) return;
    cm_layout();
    KilnCraftMenuInfo info{sizeof info, static_cast<uint32_t>(workshop), 0, 0, 0, 0};
    const uintptr_t coll = cfg + N.menu_ws;
    if (CL.ok) {
        rdv(coll + CL.max_groups, info.max_tabs);
        rdv(coll + CL.max_sets, info.max_sets);
    }
    const BlobRef wss = blob_at(coll);
    for (uint32_t i = 0; i < wss.count; ++i) {
        uint32_t w = 0;
        if (rdv(wss.data + i * 4ull, w) && w == static_cast<uint32_t>(workshop)) info.tabs = blob_at(blob_at(coll + 8).data + i * 8ull).count;
    }
    for (const CmSub& s : subs) s.fn(&info, s.user);
}

KilnResult KILN_CALL cm_subscribe(KilnOwner owner, KilnCraftMenuFn fn, void* user, KilnHandle* out) {
    if (!fn) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_cm_mu);
    const KilnHandle h = g_cm_next.fetch_add(1);
    g_cm_subs.push_back({h, owner, fn, user});
    if (out) *out = h;
    return KILN_OK;
}

KilnResult cm_add(KilnOwner owner, bool tab, uint32_t workshop, int32_t index, KilnStr label, const uint32_t* recipes, uint32_t count,
                  KilnHandle* out) {
    if (!N.menu) return KILN_ERR_UNAVAILABLE;
    if (count > 1024 || (count && !recipes)) return KILN_ERR_INVALID_ARGUMENT;
    CmAdd a;
    a.owner = owner;
    a.tab = tab;
    a.workshop = workshop;
    a.tab_index = index;
    a.label_json.assign(label.ptr ? label.ptr : "", label.ptr ? label.len : 0);
    a.recipes.assign(recipes, recipes + count);
    const std::string key = std::string(tab ? "tab:" : "set:") + std::to_string(workshop) + ":" + std::to_string(index) + ":" + a.label_json;
    cm_guid(key + ":label", a.label);
    cm_guid(key + ":id", a.id);
    if (tab) {
        // the tab shows the label; its single subcategory has none
    }
    const TextSet texts = cm_texts(a.label_json);
    if (texts.empty()) return KILN_ERR_INVALID_ARGUMENT;
    set_texts(fnv1a(a.label, 16), texts);
    {
        std::lock_guard lock(g_cm_mu);
        if (g_cm_adds.size() >= 256) return KILN_ERR_UNAVAILABLE;
        a.handle = g_cm_next.fetch_add(1);
        g_cm_adds.push_back(a);
    }
    g_cm_version.fetch_add(1);
    if (out) *out = a.handle;
    LOGI("kiln.craftmenu: %s '%s' for workshop 0x%08x (%s %d, %u recipe(s)); shown when the crafting menu rebuilds", tab ? "tab" : "subcategory",
         a.label_json.c_str(), workshop, tab ? "icon of tab" : "in tab", index, count);
    return KILN_OK;
}

KilnResult KILN_CALL cm_set_add(KilnOwner owner, uint32_t workshop, int32_t tab, KilnStr label, const uint32_t* recipes, uint32_t count,
                                KilnHandle* out) {
    return cm_add(owner, false, workshop, tab, label, recipes, count, out);
}
KilnResult KILN_CALL cm_tab_add(KilnOwner owner, uint32_t workshop, int32_t icon_from, KilnStr label, const uint32_t* recipes,
                                uint32_t count, KilnHandle* out) {
    return cm_add(owner, true, workshop, icon_from, label, recipes, count, out);
}
KilnResult KILN_CALL cm_remove(KilnHandle h) {
    bool hit = false;
    {
        std::lock_guard lock(g_cm_mu);
        const size_t n = g_cm_adds.size();
        std::erase_if(g_cm_adds, [&](const CmAdd& a) { return a.handle == h; });
        hit = g_cm_adds.size() != n;
        const size_t m = g_cm_subs.size();
        std::erase_if(g_cm_subs, [&](const CmSub& s) { return s.handle == h; });
        if (g_cm_subs.size() != m) return KILN_OK;
    }
    if (!hit) return KILN_ERR_NOT_FOUND;
    g_cm_version.fetch_add(1);
    return KILN_OK;
}
KilnResult KILN_CALL cm_describe(uint32_t workshop, char* buf, size_t cap, size_t* out_len) {
    json j;
    j["menu"] = N.menu;
    {
        std::lock_guard lock(g_cm_mu);
        json adds = json::array();
        for (const CmAdd& a : g_cm_adds) {
            if (workshop && a.workshop != workshop) continue;
            char wb[16];
            std::snprintf(wb, sizeof wb, "0x%08x", a.workshop);
            adds.push_back({{"kind", a.tab ? "tab" : "subcategory"}, {"workshop", wb}, {"index", a.tab_index}, {"label", a.label_json},
                            {"labelTag", fnv1a(a.label, 16)}, {"recipes", a.recipes.size()}});
        }
        j["additions"] = adds;
        j["kilnSubcategories"] = g_cm_headers.size();
        j["kilnTabs"] = g_cm_groups.size();
    }
    const std::string s = j.dump();
    if (out_len) *out_len = s.size();
    if (!buf || cap <= s.size()) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buf, s.c_str(), s.size() + 1);
    return KILN_OK;
}
const KilnCraftMenuApi kCraftMenu = {{sizeof(KilnCraftMenuApi), KILN_CRAFTMENU_VERSION}, &cm_subscribe, &cm_set_add, &cm_tab_add,
                                     &cm_remove, &cm_describe};

// A station's own workshop ("workshop:<guid>" recipes) is in none of the crafting menu's lists. The game's screens that ask for it, such as a
// running station's recipe picker ("Change recipe", client 0xbb46e0), would list nothing, and that empty list can't be left. They get the
// lists of the menu its recipes name instead (the station's kind's, where those recipes are placed). UI thread, the UI pointer valid.
int32_t shown_workshop(int32_t workshop) {
    if (!workshop || !N.menu) return workshop;
    uintptr_t app = 0, cfg = 0;
    if (!rdv(N.ui_app, app) || !app || !rdv(app + N.app_config, cfg) || !cfg) return workshop;
    const BlobRef wss = blob_at(cfg + N.menu_ws);
    for (uint32_t i = 0; i < wss.count && i < 256; ++i) {
        uint32_t w = 0;
        if (rdv(wss.data + i * 4ull, w) && w == static_cast<uint32_t>(workshop)) return workshop;  // the game's menu has it
    }
    std::lock_guard lock(store_mutex());
    for (const DefPtr& d : g_defs) {
        if (d->kind != KILN_CONTENT_RECIPES || !d->menu_workshop || d->station.rfind("workshop:", 0) != 0) continue;
        uint8_t g[16];
        if (guid_parse(lower(d->station.substr(9)), g) && fnv1a(g, 16) == static_cast<uint32_t>(workshop)) {
            static std::set<int32_t> said;
            if (said.insert(workshop).second)
                LOGI("new content: the game's menu has no lists for workshop 0x%08x (a station's own): its screens show workshop 0x%08x's",
                     static_cast<uint32_t>(workshop), d->menu_workshop);
            return static_cast<int32_t>(d->menu_workshop);
        }
    }
    return workshop;
}

// The menu's category lookup (UI thread = main thread, while the crafting menu builds): the UI pointer is valid here.
SafetyHookInline g_menu_hook;
uint64_t __fastcall hk_menu(void* lists, void* p2, int32_t workshop, uint8_t p4) {
    const int32_t shown = shown_workshop(workshop);
    cm_sync(shown);  // kiln.craftmenu: Kiln tabs / subcategories first, then the placement of new recipes
    menu_sync(g_ws[KILN_WORLD_CLIENT]);
    return g_menu_hook.call<uint64_t>(lists, p2, shown, p4);
}

void client_diag(uintptr_t world) {
    static auto last = std::chrono::steady_clock::now();
    const auto now = std::chrono::steady_clock::now();
    if (now - last < std::chrono::seconds(10)) return;
    last = now;
    const std::string dflag = data_dir() + "\\content\\debug-ui";
    if (GetFileAttributesA(dflag.c_str()) == INVALID_FILE_ATTRIBUTES) {
        g_loca_trace.store(false);
        return;
    }
    const std::string flag = data_dir() + "\\content\\loca-trace";
    const bool trace = GetFileAttributesA(flag.c_str()) != INVALID_FILE_ATTRIBUTES;
    std::vector<uint32_t> ids;
    {
        std::lock_guard lock(g_trace_mu);
        ids.swap(g_trace_ids);
    }
    g_loca_trace.store(trace);
    std::string ours;
    for (const auto& shard : g_texts)
        if (const TextMap* m = shard.load())
            for (auto& [tag, v] : *m) {
                char b[16];
                std::snprintf(b, sizeof b, " %08x", tag);
                ours += b;
            }
    LOGI("new content (client diag): loca hook %llu lookup(s), %llu answered with new texts; our tags:%s; trace %s",
         (unsigned long long)g_loca_calls.load(), (unsigned long long)g_loca_hits.load(), ours.c_str(), trace ? "on" : "off");
    if (!ids.empty()) {
        std::string line;
        for (size_t i = 0; i < ids.size(); ++i) {
            char b[16];
            std::snprintf(b, sizeof b, "%08x ", ids[i]);
            line += b;
            if (line.size() > 900 || i + 1 == ids.size()) {
                LOGI("new content (loca trace): %s", line.c_str());
                line.clear();
            }
        }
    }
    // the crafting UI's hand list (G38UiPlayData + 0x21e0 + 0x110: u32 ids, u16 count at +0x800; build 26404966)
    if (const uintptr_t ui = g_api->singleton(world, KilnStr{"G38UiPlayData", 13})) {
        uint16_t n = 0;
        rdv(ui + 0x21e0 + 0x110 + 0x800, n);
        std::string have;
        for (uint16_t i = 0; i < n && i < 512; ++i) {
            uint32_t id = 0;
            rdv(ui + 0x21e0 + 0x110 + i * 4ull, id);
            std::lock_guard lock(store_mutex());
            for (const DefPtr& d : g_defs)
                if (d->kind == KILN_CONTENT_RECIPES && d->id == id) have += " " + d->name;
        }
        // recipe state flags (G38UiPlayData + 0x21e0: HashMap<RecipeId, u8>) of the listed recipes
        std::string flags;
        for (uint16_t i = 0; i < n && i < 16; ++i) {
            uint32_t id = 0;
            rdv(ui + 0x21e0 + 0x110 + i * 4ull, id);
            MapView m;
            uint8_t st = 0xEE;
            bool found = false;
            if (map_view(ui + 0x21e0, &m)) {
                const int64_t sl = map_slot(m, id, &found);
                if (sl >= 0 && found) rdv(reinterpret_cast<uintptr_t>(m.values) + static_cast<uintptr_t>(sl), st);
            }
            char bb[32];
            std::snprintf(bb, sizeof bb, " %08x:%02x", id, st);
            flags += bb;
        }
        LOGI("new content (client diag): hand list state flags:%s", flags.c_str());
        uint32_t va = 0, vb = 0;
        rdv(ui + N.ui_version, va);
        rdv(ui + N.ui_version + 4, vb);
        LOGI("new content (client diag): crafting UI hand list %u recipe(s), new ones:%s; ui versions %08x %08x", n,
             have.empty() ? " none" : have.c_str(), va, vb);
    }
}

void KILN_CALL on_client_pre(const KilnTickInfo* t, void*) {
    // probing owners belongs to the server tick while a server world runs here
    const uintptr_t w = g_api->world(KILN_WORLD_CLIENT);
    if (!w) return;
    client_diag(w);
    sync_world(KILN_WORLD_CLIENT, w);
    const bool fresh = templates_changed(KILN_WORLD_CLIENT);
    if ((fresh || t->step % 50 == 7) && g_ws[KILN_WORLD_CLIENT].world == w && g_ws[KILN_WORLD_CLIENT].ireg) retry_kitbash(g_ws[KILN_WORLD_CLIENT]);
    if (t->step % 25 == 0 && !g_api->world(KILN_WORLD_SERVER)) probe_owners(KILN_WORLD_CLIENT);
}

// ============================================================================================= early registration
// A client world's frame (0xa1fae0 on 26430625) steps the prediction world first, then the client world, and the
// prediction world reads the client world's singletons. Registering in the client world's Pre leaves the prediction
// world's first step without the new items, and its programs use ItemRegistryFind's result unchecked
// (apply_armor: docs/re/content.md section 9). So a client world gets the definitions when its item registry finishes
// loading, on the loading screen, whatever world came before it in this process. Each world has its own registry; the
// binding comes from the game's own RegisterSingleton call, so a world at a reused address starts clean.
SafetyHookInline g_regsingleton_hook, g_itemupdate_hook, g_itemfind_hook;
// The trampolines, called directly: InlineHook::call locks a mutex for the whole call, and ItemRegistryFind is hot and
// runs on job threads.
using ItemFindFn = uintptr_t(__fastcall*)(uint32_t, uintptr_t);
using FourArgFn = uintptr_t(__fastcall*)(uintptr_t, uintptr_t, uintptr_t, uintptr_t);
ItemFindFn g_itemfind_orig = nullptr;
FourArgFn g_itemupdate_orig = nullptr, g_regsingleton_orig = nullptr;
int64_t g_kind_off = -1;                  // world + world.storage + storage.kind
std::atomic<uintptr_t> g_early_world{0};  // the world being set up or running, of kind g_early_kind
std::atomic<uintptr_t> g_early_ireg{0};   // its item registry
bool g_early_test = false;                // <kiln>/data/content/early-test: the same path on a server world (labs)
uint64_t g_first_step_ms[KILN_WORLD_KINDS] = {};

// ---- ids this game does not know (client world only) ----
// The server sends item ids, not definitions. A client that lacks a mod (or has not received a server's definitions
// yet: they come after the handshake, later than the first steps) gets NULL from the lookup, and the game derefs that.
// A miss in the client world's registry answers a copy of Kiln_Unknown_Item carrying the asked id. Copies are never
// freed (the game keeps the pointers); a definition that arrives later wins, because the registry map is asked first.
using UnknownMap = std::unordered_map<uint32_t, uintptr_t>;
std::atomic<const UnknownMap*> g_unknown{nullptr};  // immutable, swapped; lookups come from job threads
std::mutex g_unknown_mu;
std::atomic<uintptr_t> g_unknown_reg{0};   // the registry whose misses are answered
std::atomic<uintptr_t> g_unknown_tmpl{0};  // the clone copied per id
uint32_t g_unknown_id = 0;                 // Kiln_Unknown_Item's own id
uintptr_t g_itemfind_fn = 0;
constexpr uint32_t kUnknownProbe = 0x4B494C4E;  // the self-check's id ("KILN"), asked once per world

uintptr_t unknown_make(uint32_t id) {
    const uintptr_t tmpl = g_unknown_tmpl.load(std::memory_order_acquire);
    if (!tmpl) return 0;
    std::lock_guard lock(g_unknown_mu);
    const UnknownMap* cur = g_unknown.load(std::memory_order_acquire);
    if (cur)
        if (auto it = cur->find(id); it != cur->end()) return it->second;
    auto* p = static_cast<uint8_t*>(near_alloc(tmpl, L.item.size));
    if (!p || !rd(tmpl, p, L.item.size)) return 0;
    for (uint32_t o : L.item_relocs)
        if (!rebase(p, tmpl, o)) return 0;
    std::memcpy(p + L.i_id, &id, 4);
    auto* next = cur ? new UnknownMap(*cur) : new UnknownMap();  // the old map stays: a reader may still hold it
    next->emplace(id, reinterpret_cast<uintptr_t>(p));
    g_unknown.store(next, std::memory_order_release);
    if (id != kUnknownProbe)
        LOGW("new content: the game asked the client world's registry for item 0x%08x, which has no definition here (a mod this "
             "game does not have, or a server's definitions that have not arrived): it shows as [Unknown item] until then", id);
    return reinterpret_cast<uintptr_t>(p);
}

uintptr_t __fastcall hk_itemfind(uint32_t id, uintptr_t reg) {
    const uintptr_t r = g_itemfind_orig(id, reg);
    if (r || !id || reg != g_unknown_reg.load(std::memory_order_relaxed)) return r;
    if (const UnknownMap* m = g_unknown.load(std::memory_order_acquire))
        if (auto it = m->find(id); it != m->end()) return it->second;
    return unknown_make(id);
}

// The registry is complete here (every id of the game is in it, so a miss really is an unknown id).
void unknown_enable(WorldState& ws) {
    if (!g_itemfind_hook || !g_unknown_id || g_unknown_reg.load() == ws.ireg) return;
    const auto it = ws.items.find(g_unknown_id);
    if (it == ws.items.end()) return;
    if (g_unknown_tmpl.exchange(it->second) != it->second) {  // the base item moved: copies of the old clone go out of use
        std::lock_guard lock(g_unknown_mu);
        g_unknown.store(nullptr, std::memory_order_release);
    }
    g_unknown_reg.store(ws.ireg);
    uint32_t back = 0;
    const uintptr_t got = reinterpret_cast<uintptr_t(__fastcall*)(uint32_t, uintptr_t)>(g_itemfind_fn)(kUnknownProbe, ws.ireg);
    if (got) rdv(got + L.i_id, back);
    if (back == kUnknownProbe) LOGI("new content: unknown item ids are answered in this registry (%p; self-check through the game's lookup: ok)", reinterpret_cast<void*>(ws.ireg));
    else LOGW("new content: the self-check of unknown item ids failed (lookup = %p); an unknown id stays NULL", reinterpret_cast<void*>(got));
}

bool g_in_update_hook = false;  // early_sync is on the stack (the registry's thread)

// One line per world: when the definitions went in, relative to that world's first step.
void note_registered(uint32_t kind, uintptr_t world, WorldState& ws) {
    if (ws.registered_ms) return;
    ws.registered_ms = GetTickCount64();
    const size_t items = ws.items.size() - (g_unknown_id && ws.items.count(g_unknown_id) ? 1 : 0);
    if (g_api->world(kind) != world)
        LOGI("new content: %s world %p: definitions registered BEFORE its first step, when its item registry finished loading "
             "(%llu ms after the world was set up): %zu item(s), %zu recipe(s)",
             wname(kind), reinterpret_cast<void*>(world), static_cast<unsigned long long>(ws.registered_ms - ws.bound_ms), items, ws.recipes.size());
    else if (kind == KILN_WORLD_SERVER)
        LOGI("new content: server world %p: definitions registered %llu ms after its first step (it loads its registries while it "
             "steps, before the save is read; %s): %zu item(s), %zu recipe(s)",
             reinterpret_cast<void*>(world), static_cast<unsigned long long>(g_first_step_ms[kind] ? ws.registered_ms - g_first_step_ms[kind] : 0),
             g_in_update_hook ? "at registry load" : "in its Pre", items, ws.recipes.size());
    else
        LOGW("new content: %s world %p: definitions registered %llu ms AFTER its first step (the early hook did not run): %zu item(s), %zu recipe(s)",
             wname(kind), reinterpret_cast<void*>(world), static_cast<unsigned long long>(g_first_step_ms[kind] ? ws.registered_ms - g_first_step_ms[kind] : 0), items, ws.recipes.size());
    LOGI("near memory: %s", near_alloc_report().c_str());
}

// The game registered the item registry of a new world (RegisterSingleton, in its world set-up).
void early_bind(uintptr_t world, uintptr_t ireg) {
    KilnSessionInfo si{};
    si.size = sizeof si;
    g_api->session(&si);
    g_unknown_reg.store(0);  // the next registry is still empty: its misses are not unknown ids yet
    g_early_ireg.store(0);
    g_early_world.store(world);
    reset_ws(g_early_kind, world, si.sessions[g_early_kind] + 1);
    g_ws[g_early_kind].early = true;
    g_ws[g_early_kind].bound_ms = GetTickCount64();
    g_early_ireg.store(ireg);
    LOGI("new content: %s world %p is being set up (session %u next): its item registry is %p; definitions go in when it finishes loading",
         wname(g_early_kind), reinterpret_cast<void*>(world), si.sessions[g_early_kind] + 1, reinterpret_cast<void*>(ireg));
}

uintptr_t __fastcall hk_regsingleton(uintptr_t world, uintptr_t name, uintptr_t object, uintptr_t options) {
    const uintptr_t r = g_regsingleton_orig(world, name, object, options);
    if (!(r & 0xFF) || !world || !object) return r;
    struct GameStr {
        const char* ptr;
        uint64_t len;
    } s{};
    static const char kItem[] = "g38_items::ItemRegistry";
    char text[sizeof kItem] = {};
    if (!rd(name, &s, sizeof s) || s.len != sizeof kItem - 1 || !rd(reinterpret_cast<uintptr_t>(s.ptr), text, sizeof kItem - 1) ||
        std::memcmp(text, kItem, sizeof kItem - 1) != 0)
        return r;
    // Any world's new registry: at the old client registry's address (the allocator reuses it, a hosted server world first
    // of all) its misses are not ours to answer.
    uintptr_t expected = object;
    g_unknown_reg.compare_exchange_strong(expected, 0);
    uint8_t kind = 0xFF;
    if (rdv(world + g_kind_off, kind) && kind == g_early_kind) early_bind(world, object);
    return r;
}

void early_sync() {
    try {
        const uintptr_t w = g_early_world.load();
        if (!w) return;
        if (!g_early_test && g_api->world(g_early_kind) == w) return;  // it runs: its Pre takes over
        const WorldState& ws = g_ws[g_early_kind];
        if (ws.world == w && ws.done == g_version.load()) return;
        g_in_update_hook = true;
        sync_world(g_early_kind, w);
        g_in_update_hook = false;
    } catch (...) {
        g_in_update_hook = false;
        LOGE("new content: exception in the early registration (ignored; the world's first Pre tries again)");
    }
}

void early_sync_guarded() {
    __try {
        early_sync();
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        g_early_ireg.store(0);
        LOGE("new content: the early registration faulted (0x%08lx); the world's first Pre tries again", GetExceptionCode());
    }
}

// Every frame of every registry (the client world's, the hosted server world's, the game's menu one): one compare.
uintptr_t __fastcall hk_itemupdate(uintptr_t ireg, uintptr_t a, uintptr_t b, uintptr_t c) {
    const uintptr_t r = g_itemupdate_orig(ireg, a, b, c);
    if (ireg == g_early_ireg.load(std::memory_order_relaxed)) early_sync_guarded();
    return r;
}

// A world's first step, before its first Pre. Last chance for the prediction world's first step, and the log line.
void world_started(const KilnWorldEvent& ev) {
    g_first_step_ms[ev.kind] = GetTickCount64();
    if (ev.kind == KILN_WORLD_SERVER || g_api->side() != KILN_SIDE_CLIENT) return;
    const uintptr_t cw = ev.kind == KILN_WORLD_CLIENT ? ev.world : g_early_world.load();
    if (cw) sync_world(KILN_WORLD_CLIENT, cw);
    const WorldState& ws = g_ws[KILN_WORLD_CLIENT];
    const size_t items = ws.items.size() - (g_unknown_id && ws.items.count(g_unknown_id) ? 1 : 0);
    if (cw && ws.world == cw && ws.registered_ms)
        LOGI("new content: %s world %p first step (session %u): the client world's registry got the definitions %llu ms before it: %zu item(s), %zu recipe(s)",
             wname(ev.kind), reinterpret_cast<void*>(ev.world), ev.session, static_cast<unsigned long long>(g_first_step_ms[ev.kind] - ws.registered_ms), items, ws.recipes.size());
    else
        LOGW("new content: %s world %p first step (session %u): the definitions are NOT in the client world's registry yet; unknown ids answer [Unknown item] if the template is in",
             wname(ev.kind), reinterpret_cast<void*>(ev.world), ev.session);
}

void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    if (!ev || ev->kind >= KILN_WORLD_KINDS) return;
    if (ev->event == KILN_WORLD_STARTED) {
        world_started(*ev);
        return;
    }
    if (ev->event == KILN_WORLD_STOPPING && g_ws[ev->kind].world == ev->world) {
        LOGI("new content: the %s world stops: its registries go with it (definitions are kept for the next world)", wname(ev->kind));
        if (ev->kind == g_early_kind) {
            // the world is still intact and its teardown may ask: unknown ids stay answered until a new registry appears
            // (hk_regsingleton); without that hook nothing would ever stop answering at this address
            if (!g_regsingleton_hook) g_unknown_reg.store(0);
            g_early_ireg.store(0);
            g_early_world.store(0);
        }
        reset_ws(ev->kind, 0, 0);
    }
    if (ev->event == KILN_WORLD_STOPPING) g_first_step_ms[ev->kind] = 0;
}

// ============================================================================================= client hooks ====
SafetyHookInline g_loca_hook, g_locarec_hook, g_uitex_hook, g_all_hook;

struct LocaRecResult {  // the game's Result<const LocaTagResource*>
    uint8_t error;
    uint8_t pad[3];
    uint32_t zero;
    const uint8_t* rec;
};

LocaRecResult* __fastcall hk_locarec(LocaRecResult* out, void* pool, uint32_t id) {
    if (id)
        if (const TextView* t = text_for(id, pool); t && t->rec) {
            g_loca_hits.fetch_add(1, std::memory_order_relaxed);
            out->error = 0;
            out->zero = 0;
            out->rec = t->rec;
            return out;
        }
    return g_locarec_hook.call<LocaRecResult*>(out, pool, id);
}

struct LocaResult {  // the game's Result<StringView>
    uint8_t error;
    uint8_t pad[3];
    uint32_t zero;
    const char* text;
    uint64_t len;
};

LocaResult* __fastcall hk_loca(LocaResult* out, void* pool, uint32_t id) {
    g_loca_calls.fetch_add(1, std::memory_order_relaxed);
    if (g_loca_trace.load(std::memory_order_relaxed)) {
        std::lock_guard lock(g_trace_mu);
        if (g_trace_ids.size() < 4096 && std::find(g_trace_ids.begin(), g_trace_ids.end(), id) == g_trace_ids.end())
            g_trace_ids.push_back(id);
    }
    if (id)
        if (const TextView* t = text_for(id, pool)) {
            g_loca_hits.fetch_add(1, std::memory_order_relaxed);
            out->error = 0;
            out->zero = 0;
            out->text = t->p;
            out->len = t->n;
            return out;
        }
    return g_loca_hook.call<LocaResult*>(out, pool, id);
}

void* __fastcall hk_uitex(void* cache, const uint8_t* key, uint8_t flag) {
    if (key && g_any_alias.load(std::memory_order_acquire)) {
        uint8_t g[16];
        const AliasMap* m = nullptr;
        if (rd(reinterpret_cast<uintptr_t>(key), g, 16) && (m = alias_shard(gk(g)).load(std::memory_order_acquire))) {
            // a PNG icon kiln.assets serves under the item's own GUID: look it up unchanged (the cache knows it);
            // in every other state (none, pending, failed) the alias stays (an unknown key stops the game)
            if (const KilnAssetsApi* a = assets_api(); a && a->icon_state && a->icon_state(g) == KILN_ICON_SERVED)
                return g_uitex_hook.call<void*>(cache, key, flag);
            auto it = m->find(gk(g));
            if (it != m->end()) {
                alignas(16) uint8_t k[32];
                std::memcpy(k, key, 32);  // {guid, type, part, ...}: the callers build 32 bytes
                std::memcpy(k, &it->second.a, 8);
                std::memcpy(k + 8, &it->second.b, 8);
                return g_uitex_hook.call<void*>(cache, k, flag);
            }
        }
    }
    return g_uitex_hook.call<void*>(cache, key, flag);
}

// RecipeRegistry -> {resource recipes, count}. With new recipes listed in that registry: the same array copied
// (blob fields rebased) plus the new RecipeInfos, cached until something changes.
struct Span {
    uintptr_t data;
    uint64_t count;
};
struct ExtCache {
    uintptr_t reg = 0, src = 0;
    uint64_t count = 0;
    std::vector<uintptr_t> added;
    uintptr_t ext = 0;
    uint64_t ext_count = 0;
};
ExtCache g_ext[2];

Span* __fastcall hk_all(Span* out, uintptr_t rreg) {
    Span* r = g_all_hook.call<Span*>(out, rreg);
    if (!r || !r->data || !r->count || !L.ok) return r;
    std::vector<uintptr_t> added;
    {
        std::lock_guard lock(g_ui_mu);
        for (uint32_t k : {uint32_t(KILN_WORLD_CLIENT), uint32_t(KILN_WORLD_SERVER)}) {
            const WorldState& ws = g_ws[k];
            if (ws.rreg != rreg) continue;
            for (auto& [id, info] : ws.listed) added.push_back(info);
        }
    }
    if (added.empty()) return r;
    std::sort(added.begin(), added.end());
    ExtCache& c = g_ext[g_ws[KILN_WORLD_CLIENT].rreg == rreg ? 0 : 1];
    if (c.reg != rreg || c.src != r->data || c.count != r->count || c.added != added || !c.ext) {
        const size_t stride = L.recipe.size, n = r->count + added.size();
        auto* p = static_cast<uint8_t*>(near_alloc(r->data, n * stride));
        if (!p || !rd(r->data, p, r->count * stride)) return r;
        bool ok = true;
        for (size_t i = 0; i < r->count && ok; ++i)
            for (uint32_t o : L.recipe_relocs) ok = ok && rebase(p + i * stride, r->data + i * stride, o);
        for (size_t i = 0; i < added.size() && ok; ++i) {
            uint8_t* e = p + (r->count + i) * stride;
            ok = rd(added[i], e, stride);
            for (uint32_t o : L.recipe_relocs) ok = ok && rebase(e, added[i], o);
        }
        if (!ok) {
            LOGW("new content: the crafting UI's recipe array could not be extended (out of reach)");
            return r;
        }
        c = ExtCache{rreg, r->data, r->count, added, reinterpret_cast<uintptr_t>(p), n};
        LOGI("new content: the crafting UI sees %zu recipe(s) (%llu of the game's + %zu new)", n,
             static_cast<unsigned long long>(r->count), added.size());
    }
    r->data = c.ext;
    r->count = c.ext_count;
    return r;
}

// ============================================================================================= definitions =====
bool parse_stack(const json& j, StackSpec* s, std::string* err) {
    if (j.is_string()) {
        s->item = j.get<std::string>();
        return true;
    }
    if (!j.is_object()) {
        *err = "a stack must be {\"item\": name, \"count\": n} or {\"category\": \"0x...\", \"count\": n}";
        return false;
    }
    if (j.contains("count")) {
        if (!j["count"].is_number_integer() || j["count"].get<int64_t>() < 1 || j["count"].get<int64_t>() > 65535) {
            *err = "count must be 1..65535";
            return false;
        }
        s->count = static_cast<uint32_t>(j["count"].get<int64_t>());
    }
    if (j.contains("item") && j["item"].is_string()) s->item = j["item"].get<std::string>();
    else if (j.contains("category") && j["category"].is_string()) {
        const std::string c = j["category"].get<std::string>();
        s->category = static_cast<uint32_t>(std::strtoul(c.c_str() + (c.rfind("0x", 0) == 0 ? 2 : 0), nullptr, 16));
        if (!s->category) {
            *err = "category must be a \"0x...\" input category id";
            return false;
        }
    } else {
        *err = "a stack needs \"item\" or \"category\"";
        return false;
    }
    return true;
}

std::string str_of(const json& j, const char* k) { return j.contains(k) && j[k].is_string() ? j[k].get<std::string>() : std::string(); }

// "mod newitems" / "pack newitems" -> "newitems"
std::string mod_of_label(const std::string& label) {
    for (const char* p : {"mod ", "pack "})
        if (label.rfind(p, 0) == 0) return label.substr(std::strlen(p));
    return {};
}

bool valid_name(const std::string& s) {
    if (s.empty() || s.size() > 200) return false;
    for (char c : s)
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.')) return false;
    return true;
}

}  // namespace

// Texts for a loca tag id (FNV-1a of a LocaTagReference GUID) from kiln.gamedata's perk / buff clones
// (gamedata_clones.cpp): "text" or {"en": "...", "de": "..."} ("*" = every language). Any thread. C linkage: gamedata
// links to it weakly (/alternatename), so either file builds without the other.
extern "C" __declspec(dllexport) void kc_nc_set_texts(uint32_t tag, const char* json, size_t len) {
    if (tag && json && len) set_texts(tag, cm_texts(std::string(json, len)));
}

KilnResult nc_define(KilnOwner owner, uint32_t kind, const std::string& text, bool pack, const std::string& source,
                     std::string* report) {
    PhaseTimer pt(PH_DEFINE);
    json out = json::object();
    auto fail = [&](const std::string& why) {
        out["ok"] = false;
        out["error"] = why;
        *report = out.dump();
        return KILN_ERR_INVALID_ARGUMENT;
    };
    if (!N.on) return fail("new content is not available on this game build"), KILN_ERR_UNAVAILABLE;
    if (kind != KILN_CONTENT_ITEMS && kind != KILN_CONTENT_RECIPES) return fail("only items and recipes can be added");
    json j = json::parse(text, nullptr, false, true);
    if (j.is_discarded() || !j.is_object()) return fail("the definition is not a JSON object");
    auto d = std::make_shared<Def>();
    d->kind = kind;
    const std::string g = lower(str_of(j, "guid"));
    if (g.size() != 36 || !guid_parse(g, d->guid)) return fail("\"guid\" must be a fixed GUID (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)");
    d->guid_s = g;
    static const uint8_t zero[16] = {};
    if (std::memcmp(d->guid, zero, 16) == 0) return fail("the zero GUID is not an item (the game maps it to id 0)");
    d->id = fnv1a(d->guid, 16);
    if (!d->id) return fail("this GUID hashes to id 0");
    d->name = str_of(j, "name");
    if (!valid_name(d->name)) return fail("\"name\" (the debug name) must be 1..200 of A-Z a-z 0-9 _ - .");
    d->base = str_of(j, "base");
    d->display = str_of(j, "displayName");
    if (j.contains("fields")) {
        if (!j["fields"].is_object()) return fail("\"fields\" must be an object { \"<field path>\": value }");
        d->fields = j["fields"];
    }
    if (kind == KILN_CONTENT_ITEMS) {
        if (d->base.empty()) return fail("an item needs \"base\": the item it is cloned from");
        // texts: displayName / description / lore = every language ("*"); "texts" per language key
        if (!d->display.empty()) d->texts[0]["*"] = d->display;
        if (!str_of(j, "description").empty()) d->texts[2]["*"] = str_of(j, "description");
        else d->no_description = j.contains("description");
        if (!str_of(j, "lore").empty()) d->texts[3]["*"] = str_of(j, "lore");
        if (!str_of(j, "source").empty()) d->texts[4]["*"] = str_of(j, "source");
        if (j.contains("texts")) {
            const json& t = j["texts"];
            if (!t.is_object()) return fail("\"texts\" must be { \"<language>\": { \"name\", \"description\", \"lore\", ... } }");
            for (auto& [lang, slots] : t.items()) {
                const std::string k = lang_key(lang);
                if (!valid_lang(k)) return fail("texts: \"" + lang + "\" is not a language key (\"en\", \"de\", \"pt-BR\", \"*\")");
                if (!slots.is_object()) return fail("texts." + lang + " must be an object of texts");
                for (auto& [slot, text] : slots.items()) {
                    int s = -1;
                    for (int i = 0; i < kTextSlots; ++i)
                        if (slot == kTextKeys[i] || (i == 0 && slot == "displayName")) s = i;
                    if (s < 0) return fail("texts." + lang + "." + slot + ": unknown text (name, caption, description, lore, acquisitionHint, primaryAction, secondaryAction, tertiaryAction)");
                    if (!text.is_string()) return fail("texts." + lang + "." + slot + " must be a string");
                    if (!text.get<std::string>().empty()) d->texts[s][k] = text.get<std::string>();
                }
            }
        }
        // kitbash: references to loaded resources (refs.cpp); checked when the clone is built
        for (const char* k : {"model", "visual", "icon", "perks", "weaponType", "impact", "program", "animation",
                              "collectionModel", "iconModel", "refs", "visualModels"})
            if (j.contains(k)) d->kitbash[k] = j[k];
        // "icon": "<file>.png": a PNG icon of its own (kiln.assets, client); relative paths: the mod's folder (packs)
        // or <game>/kiln (the managed API passes absolute paths)
        if (j.contains("icon") && j["icon"].is_string()) {
            const std::string ic = j["icon"].get<std::string>();
            if (ic.size() > 4 && lower(ic.substr(ic.size() - 4)) == ".png") {
                d->kitbash.erase("icon");
                d->icon_file = ic;
                const bool abs = (ic.size() > 2 && ic[1] == ':') || ic[0] == '/' || ic[0] == '\\';
                if (!abs && pack && !current_pack_dir().empty()) d->icon_file = current_pack_dir() + "\\" + ic;
            }
        }
        // distribution: random loot, drop tables (the "distribution" section below). Item knowledge needs nothing: an
        // item without an ItemKnowledge record counts as known (docs/re/content.md 8.4)
        for (const char* k : {"loot", "drops"})
            if (j.contains(k)) d->distribution[k] = j[k];
        if (j.contains("drops") && !j["drops"].is_array()) return fail("\"drops\" must be a list of {\"table\", \"countMin\", \"countMax\", \"chance\", \"weight\"}");
        if (j.contains("loot") && !j["loot"].is_object() && !j["loot"].is_boolean()) return fail("\"loot\" must be true or {\"like\", \"stackSizeMin\", \"stackSizeMax\", \"fields\"}");
        if (j.contains("placeholder")) {
            if (!j["placeholder"].is_object()) return fail("\"placeholder\" is Kiln's (missing content)");
            d->placeholder = true;
        }
        if (j.contains("rolledLoot")) {
            if (!j["rolledLoot"].is_boolean()) return fail("\"rolledLoot\" must be true or false");
            d->rolled = j["rolledLoot"].get<bool>();
        }
    } else {
        d->station = str_of(j, "station");
        if (d->base.empty() && d->station.empty()) return fail("a recipe needs \"base\" (a recipe to copy) or \"station\"");
        if (const std::string menu = str_of(j, "menu"); !menu.empty()) {
            if (menu.size() <= 2 || menu[0] != '0' || (menu[1] != 'x' && menu[1] != 'X'))
                return fail("\"menu\" must be a workshop id (\"0x...\"): the workshop whose crafting menu lists the recipe");
            d->menu_workshop = static_cast<uint32_t>(std::strtoul(menu.c_str() + 2, nullptr, 16));
        }
        std::string err;
        for (const char* k : {"inputs", "outputs"}) {
            if (!j.contains(k) || !j[k].is_array() || j[k].empty() || j[k].size() > 32)
                return fail(std::string("\"") + k + "\" must be a list of 1..32 stacks");
            for (const json& s : j[k]) {
                StackSpec x;
                if (!parse_stack(s, &x, &err)) return fail(std::string(k) + ": " + err);
                if (k[0] == 'o' && x.category) return fail("outputs cannot be categories");
                (k[0] == 'i' ? d->inputs : d->outputs).push_back(x);
            }
        }
        if (j.contains("duration")) {
            if (!j["duration"].is_number() || j["duration"].get<double>() < 0 || j["duration"].get<double>() > 1e6)
                return fail("\"duration\" must be seconds (0..1e6)");
            d->duration = j["duration"].get<double>();
        }
        const std::string u = lower(str_of(j, "unlock"));
        if (!u.empty() && u != "always" && u != "base") return fail("\"unlock\" must be \"always\" or \"base\"");
        d->unlock_base = u == "base";
    }
    if (kind == KILN_CONTENT_ITEMS) {
        for (int i = 0; i < kTextSlots; ++i) derive_guid(d->guid, kTextPurpose[i], d->tag_guid[i]);
    } else {
        derive_guid(d->guid, "kiln.recipe.name", d->tag_guid[0]);
    }
    for (int i = 0; i < kTextSlots; ++i) d->tags[i] = fnv1a(d->tag_guid[i], 16);
    d->owner = owner;
    d->owner_label = owner_label(owner);
    d->mod = mod_of_label(d->owner_label);
    if (d->placeholder && j["placeholder"].contains("mod") && j["placeholder"]["mod"].is_string())
        d->mod = j["placeholder"]["mod"].get<std::string>();
    d->source = source;
    d->pack = pack;
    j.erase("comment");
    d->text = j.dump();
    d->body = j;
    out["guid"] = d->guid_s;
    char idb[16];
    std::snprintf(idb, sizeof idb, "0x%08x", d->id);
    out["id"] = idb;
    out["name"] = d->name;
    bool changed = false;
    {
        PhaseTimer lt(PH_DEFINE_LOCKED);
        std::lock_guard lock(store_mutex());
        DefPtr cur = find_def(kind, d->guid);
        if (const auto* same_name = g_index.named(kind, d->name))
            for (const DefPtr& x : *same_name)
                if (x != cur && !(x->placeholder || d->placeholder))
                    return fail("the name " + d->name + " is already used by " + x->guid_s + " (" + x->owner_label + ")");
        if (cur && d->placeholder && !cur->placeholder && !cur->orphan) {
            // the item is defined (its mod is back): no placeholder needed
            out["ok"] = true;
            out["state"] = "unchanged";
            out["warning"] = "defined by " + cur->owner_label + ": no placeholder needed";
            *report = out.dump();
            return KILN_OK;
        }
        if (cur) {
            // the same mod again (a reload: the old instance's owner may not be probed yet), a gone owner, a pack, or a
            // placeholder (missing content: the returning mod takes its item back, same GUID, same id)
            const bool same_mod = !cur->owner_label.empty() && cur->owner_label == d->owner_label;
            const bool gone = cur->owner && cur->owner != owner &&
                              g_api->post_world(cur->owner, active_kind(), &noop, nullptr) == KILN_ERR_REVOKED;
            if (!cur->orphan && cur->owner != owner && !(cur->pack && pack) && !same_mod && !gone && !cur->placeholder)
                return fail("GUID " + d->guid_s + " is already defined by " + cur->owner_label);
            const bool same = cur->text == d->text;
            const bool adopt = cur->orphan || cur->owner != owner;
            const bool takeover = cur->placeholder && !d->placeholder;
            cur->owner = owner, cur->owner_label = d->owner_label, cur->source = source, cur->pack = pack;
            cur->seen = true;
            if (cur->orphan) {
                cur->orphan = false;
                changed = true;
            }
            if (!same) {
                // a new version: clones and the recipe are rebuilt, the maps re-pointed (the old memory stays valid)
                d->version = cur->version + 1;
                d->owner = owner;
                const std::string was = cur->name;
                d->at = cur->at;
                *cur = std::move(*d);
                g_index.rename(cur, was);
                cur->clones.clear();
                cur->info = 0;
                changed = true;
            }
            out["state"] = takeover ? "restored" : same ? (adopt ? "adopted" : "unchanged") : "redefined";
            if (takeover)
                LOGI("new content: %s %s is back (%s): it takes its id 0x%08x back from the placeholder", cur->name.c_str(),
                     cur->guid_s.c_str(), cur->owner_label.c_str(), cur->id);
            d = cur;
        } else {
            d->at = g_defs.size();
            g_defs.push_back(d);
            g_index.add(d);
            changed = true;
            out["state"] = d->placeholder ? "placeholder" : "defined";
        }
    }
    for (int i = 0; i < (kind == KILN_CONTENT_ITEMS ? kTextSlots : 0); ++i) set_texts(d->tags[i], d->texts[i]);
    if (kind == KILN_CONTENT_ITEMS && !d->icon_file.empty())
        if (const KilnAssetsApi* a = assets_api(); a && a->icon_register_file) {
            const KilnResult irc = a->icon_register_file(owner, d->guid, KilnStr{d->icon_file.data(), d->icon_file.size()});
            if (irc == KILN_OK) LOGI("new content: item %s: icon %s registered with kiln.assets", d->name.c_str(), d->icon_file.c_str());
            else if (irc != KILN_ERR_UNAVAILABLE) {
                LOGW("new content: item %s: icon %s refused by kiln.assets (%d); the base item's icon stays", d->name.c_str(),
                     d->icon_file.c_str(), irc);
                std::lock_guard lock(store_mutex());
                d->icon_problem = "icon " + d->icon_file + " refused by kiln.assets (" + std::to_string(irc) + ")";
            }
        }
    if (kind == KILN_CONTENT_RECIPES && !d->display.empty()) set_text(d->tags[0], d->display);
    if (changed) {
        g_version.fetch_add(1);
        LOGI("new content: %s %s %s (%s, id %s) by %s%s", kind == KILN_CONTENT_ITEMS ? "item" : "recipe", d->name.c_str(),
             out["state"].get<std::string>().c_str(), d->guid_s.c_str(), idb, d->owner_label.c_str(),
             source.empty() ? "" : (" from " + source).c_str());
    }
    // On the active world's thread with its registries loaded: in now, so the caller (a pack, OnReady) can use it.
    const uintptr_t aw = active_world();
    if (aw && g_api->on_world_thread(active_kind())) sync_world(active_kind(), aw);
    out["ok"] = true;
    {
        std::lock_guard lock(store_mutex());
        if (!d->error.empty()) out["warning"] = d->error;
        if (!d->warnings.empty()) out["warnings"] = d->warnings;
    }
    *report = out.dump();
    return KILN_OK;
}

void nc_pack_add(KilnOwner owner, uint32_t kind, const std::string& list_json, const std::string& source, ApplyResult* out) {
    json list = json::parse(list_json, nullptr, false, true);
    const char* sec = kind == KILN_CONTENT_ITEMS ? "items" : "recipes";
    if (list.is_discarded() || !list.is_array()) {
        out->errors.push_back(std::string(sec) + " add: must be a list of definitions");
        return;
    }
    for (const json& e : list) {
        std::string rep;
        const KilnResult rc = nc_define(owner, kind, e.dump(), true, source, &rep);
        json r = json::parse(rep, nullptr, false);
        const std::string name = e.is_object() && e.contains("name") && e["name"].is_string() ? e["name"].get<std::string>() : "?";
        if (rc != KILN_OK) out->errors.push_back(std::string(sec) + " add " + name + ": " + (r.is_object() && r.contains("error") ? r["error"].get<std::string>() : rep));
        else {
            ++out->applied;
            if (r.is_object() && r.contains("warning")) out->warnings.push_back(std::string(sec) + " add " + name + ": " + r["warning"].get<std::string>());
        }
    }
}

void nc_packs_begin() {
    std::lock_guard lock(store_mutex());
    for (const DefPtr& d : g_defs)
        if (d->pack) d->seen = false;
}

void nc_packs_end() {
    bool changed = false;
    {
        std::lock_guard lock(store_mutex());
        for (const DefPtr& d : g_defs)
            if (d->pack && !d->seen && !d->orphan) {
                d->orphan = true;
                changed = true;
                LOGI("new content: %s %s is no longer in any data pack; it stays registered until the server restarts%s",
                     d->kind == KILN_CONTENT_ITEMS ? "item" : "recipe", d->name.c_str(),
                     d->kind == KILN_CONTENT_RECIPES ? " (taken off its station)" : "");
            }
    }
    if (changed) {
        g_version.fetch_add(1);
        const uintptr_t aw = active_world();
        if (aw && g_api->on_world_thread(active_kind())) sync_world(active_kind(), aw);
    }
}

void nc_world_ready(uintptr_t world, uint32_t kind) {
    if (!N.on) return;
    sync_world(kind, world);
}

void nc_world_stopping(uintptr_t world) {
    for (uint32_t k = 0; k < KILN_WORLD_KINDS; ++k)
        if (g_ws[k].world == world) {
            if (k == KILN_WORLD_SERVER) loot_world_reset(), drops_world_reset();
            reset_ws(k, 0, 0);
        }
}

namespace {
// FNV-1a over what a listing entry shows (nc_definitions_json).
struct Fingerprint {
    uint64_t h = 1469598103934665603ull;
    void add(const void* p, size_t n) {
        for (size_t i = 0; i < n; ++i) h = (h ^ static_cast<const uint8_t*>(p)[i]) * 1099511628211ull;
    }
    template <class T>
    void add(const T& v) { add(&v, sizeof v); }
    void add(const std::string& s) {
        add(s.size());
        add(s.data(), s.size());
    }
    void add(const std::vector<std::string>& v) {
        add(v.size());
        for (const std::string& s : v) add(s);
    }
};
}  // namespace

std::string nc_definitions_json() {
    PhaseTimer pt(PH_LISTING);
    std::lock_guard lock(store_mutex());
    // Each entry's text is kept with its fingerprint (what it shows) and made again only when that changed: tools ask for the listing
    // every second, and making all of it costs a fraction of a millisecond per definition, under the store lock.
    const KilnAssetsApi* icons = assets_api();
    std::string out = "[";
    for (const DefPtr& d : g_defs) {
        if (d->internal) continue;
        uint8_t in_world[KILN_WORLD_KINDS] = {};
        bool listed = false;
        for (uint32_t k = 0; k < KILN_WORLD_KINDS; ++k) {
            const WorldState& ws = g_ws[k];
            listed = listed || ws.listed.count(d->id);
            if (ws.world) in_world[k] = d->kind == KILN_CONTENT_ITEMS ? ws.items.count(d->id) != 0 : ws.recipes.count(d->id) != 0;
        }
        const int32_t icon = !d->icon_file.empty() && icons && icons->icon_state ? icons->icon_state(d->guid) : -2;
        Fingerprint f;
        f.add(in_world), f.add(listed), f.add(icon), f.add(d->version), f.add(d->orphan), f.add(d->placeholder);
        f.add(d->error), f.add(d->warnings), f.add(d->gear_notes), f.add(d->icon_problem), f.add(d->owner_label), f.add(d->source), f.add(d->mod);
        if (f.h != d->listed_as || d->listed.empty()) {
            char idb[16];
            std::snprintf(idb, sizeof idb, "0x%08x", d->id);
            json w = json::array();
            for (uint32_t k = 0; k < KILN_WORLD_KINDS; ++k)
                if (in_world[k]) w.push_back(wname(k));
            json e = {{"kind", d->kind == KILN_CONTENT_ITEMS ? "item" : "recipe"}, {"guid", d->guid_s}, {"id", idb},
                      {"name", d->name}, {"base", d->base}, {"owner", d->owner_label}, {"source", d->source},
                      {"state", d->orphan ? "orphaned" : (w.empty() ? (d->error.empty() ? "pending" : "error") : "registered")},
                      {"worlds", w}, {"version", d->version}, {"mod", d->mod}};
            if (d->placeholder) e["placeholder"] = true;
            if (!d->error.empty()) e["error"] = d->error;
            if (!d->display.empty()) e["displayName"] = d->display;
            json warn = json::array();
            for (const std::string& x : d->warnings) warn.push_back(x);
            for (const std::string& x : d->gear_notes) warn.push_back(x);
            if (!d->icon_problem.empty()) warn.push_back(d->icon_problem);
            if (!warn.empty()) e["warnings"] = warn;
            if (d->kind == KILN_CONTENT_ITEMS) {
                json langs = json::array();
                std::set<std::string> ls;
                for (const TextSet& t : d->texts)
                    for (auto& [k, v] : t) ls.insert(k);
                for (const std::string& k : ls) langs.push_back(k);
                if (!langs.empty()) e["languages"] = langs;
                if (!d->icon_file.empty()) {
                    e["iconFile"] = d->icon_file;
                    if (icon != -2) {
                        static const char* const st[] = {"none", "pending", "served", "failed"};
                        e["iconState"] = icon >= 0 && icon <= 3 ? st[icon] : "?";
                    }
                }
            }
            e["definition"] = d->body;
            if (d->kind == KILN_CONTENT_RECIPES) e["listed"] = listed;
            d->listed = e.dump();
            d->listed_as = f.h;
        }
        if (out.size() > 1) out += ',';
        out += d->listed;
    }
    out += ']';
    return out;
}

std::string nc_info_json(const std::string& refs_json) {
    json j = json::object();
    json langs = json::array();
    {
        std::lock_guard lock(g_text_mu);
        load_languages();
        for (const std::string& k : g_lang_keys) langs.push_back(k);  // index = keen::LanguageId
    }
    j["languages"] = langs;
    j["textLanguage"] = g_loca_lang_off != 0;
    j["loca"] = N.loca;
    j["icons"] = N.uitex;
    j["assets"] = assets_api() != nullptr;
    json ph = json::array();
    {
        std::lock_guard lock(store_mutex());
        for (const DefPtr& d : g_defs)
            if (d->placeholder && !d->orphan) {
                char idb[16];
                std::snprintf(idb, sizeof idb, "0x%08x", d->id);
                ph.push_back({{"guid", d->guid_s}, {"id", idb}, {"name", d->name}, {"mod", d->mod}});
            }
    }
    j["placeholders"] = ph;
    j["loot"] = {{"on", N.loot}, {"rolls", g_loot_calls.load()}, {"extended", g_loot_extended.load()}};
    std::vector<uintptr_t> extra;
    uintptr_t lreg = 0;
    {
        std::lock_guard lock(g_loot_mu);
        extra = g_loot_extra;
        lreg = g_loot_reg;
    }
    j["loot"]["items"] = extra.size();
    // self-check (server world thread): the roll's own getter (through the hook) returns the game's entries followed
    // by the new ones, and LootFind's map has them
    if (N.loot && lreg && g_api->on_world_thread(KILN_WORLD_SERVER)) {
        LootSpan sp{};
        reinterpret_cast<LootSpan*(__fastcall*)(LootSpan*, uintptr_t)>(N.loot_items_fn)(&sp, lreg);
        uint32_t tail_ok = 0, find_ok = 0;
        for (size_t i = 0; i < extra.size() && sp.count >= extra.size(); ++i) {
            uint32_t want = 0, got = 0;
            rdv(extra[i] + LL.id, want);
            rdv(sp.data + (sp.count - extra.size() + i) * LL.size + LL.id, got);
            tail_ok += want && want == got;
            find_ok += map_get(lreg + E.lootreg_map, want) == extra[i];
        }
        j["loot"]["getter"] = {{"count", sp.count}, {"newAtTheEnd", tail_ok}, {"lootFind", find_ok}};
    }
    j["refs"] = json::parse(refs_json, nullptr, false);
    return j.dump();
}

bool nc_is_new(uint32_t kind, uint32_t id) {
    PhaseTimer pt(PH_IS_NEW);
    std::lock_guard lock(store_mutex());
    return g_index.with_id(kind, id) != nullptr;
}

uint32_t nc_undefine_owner(KilnOwner owner) {
    uint32_t n = 0;
    {
        std::lock_guard lock(store_mutex());
        for (const DefPtr& d : g_defs)
            if (d->owner == owner && !d->orphan) d->orphan = true, ++n;
    }
    if (n) {
        g_version.fetch_add(1);
        LOGI("new content: %u definition(s) of %s set aside (items stay registered until the process ends; recipes leave their station)",
             n, owner_label(owner).c_str());
        const uintptr_t aw = active_world();
        if (aw && g_api->on_world_thread(active_kind())) sync_world(active_kind(), aw);
    }
    return n;
}

// Client worlds: registration at registry load, and answers for unknown ids. Everything here fails soft: without a
// hook the definitions go in at the first Pre as before and a miss stays NULL (kiln.log says so).
void early_boot() {
    const auto file = [](const char* n) { return GetFileAttributesA((data_dir() + "\\content\\" + n).c_str()) != INVALID_FILE_ATTRIBUTES; };
    g_early_test = file("early-test");
    if (g_api->side() != KILN_SIDE_CLIENT && !g_early_test) return;
    if (g_early_test) {
        g_early_kind = KILN_WORLD_SERVER;
        LOGW("new content: <kiln>/data/content/early-test: the client worlds' early registration runs on the server world (lab check)");
    }
    const auto sym = [](const char* n, uintptr_t* out) { return g_api->symbol(KilnStr{n, std::strlen(n)}, out) == KILN_OK && *out; };
    int64_t storage = 0, kind = 0;
    uintptr_t reg_fn = 0, upd_fn = 0;
    bool early = !file("no-early-registration") && sym("RegisterSingleton", &reg_fn) && sym("content/ItemRegistryUpdate", &upd_fn) &&
                 g_api->constant(KilnStr{"world.storage", 13}, &storage) == KILN_OK && g_api->constant(KilnStr{"storage.kind", 12}, &kind) == KILN_OK;
    if (early) {
        g_kind_off = storage + kind;
        g_regsingleton_hook = safetyhook::create_inline(reinterpret_cast<void*>(reg_fn), reinterpret_cast<void*>(&hk_regsingleton));
        g_itemupdate_hook = safetyhook::create_inline(reinterpret_cast<void*>(upd_fn), reinterpret_cast<void*>(&hk_itemupdate));
        early = static_cast<bool>(g_regsingleton_hook) && static_cast<bool>(g_itemupdate_hook);
        if (g_regsingleton_hook) g_regsingleton_orig = g_regsingleton_hook.original<FourArgFn>();
        if (g_itemupdate_hook) g_itemupdate_orig = g_itemupdate_hook.original<FourArgFn>();
    }
    bool unknown = !file("no-unknown-items") && sym("content/ItemRegistryFind", &g_itemfind_fn);
    if (unknown) {
        g_itemfind_hook = safetyhook::create_inline(reinterpret_cast<void*>(g_itemfind_fn), reinterpret_cast<void*>(&hk_itemfind));
        unknown = static_cast<bool>(g_itemfind_hook);
        if (unknown) g_itemfind_orig = g_itemfind_hook.original<ItemFindFn>();
    }
    if (unknown) {
        // what a lookup of an unknown id answers. A sword: its record is complete for every reader (hand, armour slot, UI);
        // inert (no perks); no stack is ever cut in the client's own simulation. Only a client world has it.
        static const char kGuid[] = "c7d9a1e4-52b6-4f08-93ad-6e1f0b7c24d8";
        const json def = {{"guid", kGuid}, {"name", "Kiln_Unknown_Item"}, {"base", "Weapon_T1_1H_SwordEpic_01"},
                          {"displayName", "[Unknown item]"},
                          {"description", "An item from a mod this game does not have. It is not lost: the server keeps it. Install the mod to see it."},
                          {"perks", json::array({"none", "none", "none", "none", "none"})},
                          {"fields", {{"maxStackSize", 65535}}}};
        std::string rep;
        const KilnResult rc = nc_define(g_owner, KILN_CONTENT_ITEMS, def.dump(), false, "kiln (unknown items)", &rep);
        uint8_t g[16];
        guid_parse(kGuid, g);
        std::lock_guard lock(store_mutex());
        if (const DefPtr d = find_def(KILN_CONTENT_ITEMS, g); rc == KILN_OK && d) {
            d->internal = true;
            g_unknown_id = d->id;
        } else {
            LOGW("new content: the unknown-item template was refused (%d): %s", rc, rep.c_str());
            unknown = false;
        }
    }
    LOGI("new content: client worlds: registration at registry load %s, unknown item ids %s", early ? "on" : "OFF (at the first Pre)",
         unknown ? "answered by a placeholder" : "NOT answered (a miss stays NULL)");
}

void nc_boot() {
    auto sym = [](const char* n, uintptr_t* out) {
        return g_api->symbol(KilnStr{n, std::strlen(n)}, out) == KILN_OK && *out;
    };
    auto cst = [](const char* n, int64_t* out) { return g_api->constant(KilnStr{n, std::strlen(n)}, out) == KILN_OK; };
    auto feat = [](const char* n) { return g_api->feature(KilnStr{n, std::strlen(n)}); };
    int64_t map2 = 0, all_res = 0;
    N.on = E.items && E.reflection && feat("content.new") && cst("content/items.map2", &map2) && map2 == E.items_map &&
           cst("content/recipes.workshops", &N.ws_map) && cst("content/recipes.ws_stride", &N.ws_stride) &&
           cst("content/recipes.ws_count", &N.ws_count) && cst("content/recipes.ws_max", &N.ws_max) &&
           N.ws_stride >= N.ws_count + 2 && N.ws_count >= N.ws_max * 8 && N.ws_max > 0 && N.ws_max <= 4096;
    if (!N.on) {
        LOGW("new content off on this build (content.new %s)", feat("content.new") ? "inconsistent" : "missing");
        return;
    }
    // client only: names, icons, the crafting UI
    uintptr_t locarec = 0;
    N.loca = feat("content.loca") && sym("content/LocaLookup", &N.loca_fn) && sym("content/LocaRecord", &locarec) &&
             cst("content/loca.special", &N.loca_special);
    // the LocaPool's current language (keen::LanguageId), read by the loca hooks to pick a definition's text
    int64_t lang_off = 0;
    if (N.loca && feat("content.loca_language") && cst("content/loca.language", &lang_off) && lang_off > 0x20 && lang_off < 0x400)
        g_loca_lang_off = lang_off;
    N.uitex = feat("content.ui_texture") && sym("content/UiTextureGet", &N.uitex_fn);
    uintptr_t menu_fn = 0;
    N.menu = feat("content.recipe_menu") && sym("content/CraftMenuCategories", &menu_fn) && sym("content/UiApp", &N.ui_app) &&
             cst("content/ui.app_config", &N.app_config) &&
             cst("content/ui.menu_workshops", &N.menu_ws) && cst("content/ui.menu_category_stride", &N.cat_stride) &&
             cst("content/ui.menu_category_subs", &N.cat_subs) && cst("content/ui.menu_sub_stride", &N.sub_stride) &&
             cst("content/ui.menu_sub_recipes", &N.sub_recipes);
    int64_t vb = 0;
    N.ui_refresh = feat("content.recipe_ui_refresh") && cst("content/ui.version_a", &N.ui_version) &&
                   cst("content/ui.version_b", &vb) && vb == N.ui_version + 4 && N.ui_version > 0 && N.ui_version < 0x100000;
    N.recipe_ui = feat("content.recipe_ui") && sym("content/RecipeAll", &N.recipe_all_fn) &&
                  cst("content/recipes.all_array", &N.all_array) && cst("content/recipes.resource", &all_res) &&
                  all_res == E.recipes_resource;
    if (N.loca) {
        g_loca_hook = safetyhook::create_inline(reinterpret_cast<void*>(N.loca_fn), reinterpret_cast<void*>(&hk_loca));
        g_locarec_hook = safetyhook::create_inline(reinterpret_cast<void*>(locarec), reinterpret_cast<void*>(&hk_locarec));
        N.loca = static_cast<bool>(g_loca_hook) && static_cast<bool>(g_locarec_hook);
    }
    if (N.uitex) {
        g_uitex_hook = safetyhook::create_inline(reinterpret_cast<void*>(N.uitex_fn), reinterpret_cast<void*>(&hk_uitex));
        N.uitex = static_cast<bool>(g_uitex_hook);
    }
    if (N.menu) {
        g_menu_hook = safetyhook::create_inline(reinterpret_cast<void*>(menu_fn), reinterpret_cast<void*>(&hk_menu));
        N.menu = static_cast<bool>(g_menu_hook);
    }
    if (N.menu)  // kiln.craftmenu: kiln.ui's crafting-menu extension point (tabs, subcategories, which workshop)
        g_api->register_service(g_owner, KilnStr{KILN_CRAFTMENU_SERVICE, std::strlen(KILN_CRAFTMENU_SERVICE)}, &kCraftMenu);
    if (N.recipe_ui) {
        g_all_hook = safetyhook::create_inline(reinterpret_cast<void*>(N.recipe_all_fn), reinterpret_cast<void*>(&hk_all));
        N.recipe_ui = static_cast<bool>(g_all_hook);
    }
    // random loot of new items: the candidate getter (server world; on a client it serves a hosted world)
    N.loot = E.loot && feat("content.loot_new") && sym("content/LootItems", &N.loot_items_fn) &&
             cst("content/lootreg.data", &N.lootreg_data) && cst("content/loot.items", &N.loot_items) &&
             N.lootreg_data > 0 && N.lootreg_data < 0x100 && N.loot_items == 0x14;
    if (N.loot && GetFileAttributesA((data_dir() + "\\content\\no-loot-hook").c_str()) != INVALID_FILE_ATTRIBUTES) {
        LOGW("new content: <kiln>/data/content/no-loot-hook: random loot of new items off");
        N.loot = false;
    }
    if (N.loot) {
        g_lootitems_hook = safetyhook::create_inline(reinterpret_cast<void*>(N.loot_items_fn), reinterpret_cast<void*>(&hk_lootitems));
        N.loot = static_cast<bool>(g_lootitems_hook) && loot_layout();
    }
    KilnHandle h = 0;
    g_api->subscribe_tick(g_owner, KILN_TICK_PRE, &on_server_pre, nullptr, &h);
    g_api->subscribe_world(g_owner, &on_world, nullptr, &h);
    if (g_api->side() == KILN_SIDE_CLIENT)
        g_api->subscribe_world_tick(g_owner, KILN_WORLD_CLIENT, KILN_TICK_PRE, &on_client_pre, nullptr, &h);
    LOGI("new content on (workshop lists +0x%llx, %lld x %lld B); client parts: loca %s%s, item icons %s, crafting UI %s%s",
         (unsigned long long)N.ws_map, (long long)N.ws_max, (long long)N.ws_stride, N.loca ? "on" : "off",
         g_loca_lang_off ? " (per language)" : "", N.uitex ? "on" : "off", N.recipe_ui ? "on" : "off",
         N.ui_refresh ? " (+ refresh)" : "");
    {
        std::lock_guard lock(g_text_mu);
        load_languages();
    }
    LOGI("new content: random loot of new items %s, drop tables %s", N.loot ? "on" : "off", E.drops ? "on" : "off");
    if (N.menu) LOGI("new content: crafting menu lists on (app %p +0x%llx, workshops +0x%llx)", reinterpret_cast<void*>(N.ui_app),
                     (unsigned long long)N.app_config, (unsigned long long)N.menu_ws);
    early_boot();
}

}  // namespace kc
