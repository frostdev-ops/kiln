// SPDX-License-Identifier: MIT
// kiln.creatures: the catalog of creature and NPC templates. Shipped data (catalog_data.inc, generated offline from the
// game's own template resources by gen_catalog.py), marked LOADED when the running game has the template (kiln.spawn's
// template list), and checked once against the engine's template infos: every template whose entities get enemy AI or
// villager state should be in the catalog (docs/kiln/services/creatures.md, "Catalog").
#include <algorithm>
#include <cstdio>
#include <shared_mutex>

#include "internal.h"
#include "services/kiln_spawn.h"

namespace kcr {

namespace {

struct Raw {
    const char* guid;
    const char* name;
    const char* display;
    uint32_t kind, flags, faction, level, level_max, enemy_class;
    const char* arsenal;
    const char* loot;
    uint32_t slots_min, slots_max;
    float xp, scale;
    const char* model;
    const char* graph;
};
const Raw kRaw[] = {
#include "catalog_data.inc"
};

std::vector<KilnCreatureTemplate> g_list;          // sorted by name
std::unordered_map<std::string, uint32_t> g_by_guid; // 16 raw bytes as key
std::unordered_map<std::string, uint32_t> g_by_name; // lower-case name and display name
std::atomic<uint32_t> g_loaded{0}, g_engine_extra{0};
bool g_checked = false;
uint64_t g_engine_count = 0, g_next_check = 0;
std::shared_mutex g_mu;  // the catalog grows when the engine gains templates (kiln.content clones)

std::string lower(std::string_view s);
void reindex();
void reindex_impl() {
    std::sort(g_list.begin(), g_list.end(), [](const auto& a, const auto& b) { return _stricmp(a.name, b.name) < 0; });
    g_by_guid.clear();
    g_by_name.clear();
    for (uint32_t i = 0; i < g_list.size(); ++i) {
        g_by_guid.emplace(std::string(reinterpret_cast<const char*>(g_list[i].guid.b), 16), i);
        g_by_name.emplace(lower(g_list[i].name), i);
    }
    for (uint32_t i = 0; i < g_list.size(); ++i)
        if (g_list[i].display[0]) g_by_name.emplace(lower(g_list[i].display), i);  // first (by name) wins
}
const KilnSpawnApi* g_spawn = nullptr;

std::string lower(std::string_view s) {
    std::string o(s);
    for (char& c : o) c = static_cast<char>(c >= 'A' && c <= 'Z' ? c + 32 : c);
    return o;
}

int hexv(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

void reindex() { reindex_impl(); }

void copy_cstr(char* dst, size_t cap, const char* src) {
    size_t n = std::strlen(src);
    if (n >= cap) n = cap - 1;
    std::memcpy(dst, src, n);
    dst[n] = 0;
}

}  // namespace

bool parse_guid(std::string_view s, uint8_t out[16]) {
    // text 8-4-4-4-12; bytes in Windows layout (first three groups little-endian), as .NET Guid.ToByteArray()
    std::string h;
    for (char c : s) {
        if (c == '-' || c == '{' || c == '}' || c == ' ') continue;
        if (hexv(c) < 0) return false;
        h += c;
    }
    if (h.size() != 32) return false;
    uint8_t b[16];
    for (int i = 0; i < 16; ++i) b[i] = static_cast<uint8_t>(hexv(h[2 * i]) * 16 + hexv(h[2 * i + 1]));
    const int order[16] = {3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15};
    for (int i = 0; i < 16; ++i) out[i] = b[order[i]];
    return true;
}

std::string guid_text(const uint8_t g[16]) {
    char b[40];
    std::snprintf(b, sizeof b, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x", g[3], g[2], g[1],
                  g[0], g[5], g[4], g[7], g[6], g[8], g[9], g[10], g[11], g[12], g[13], g[14], g[15]);
    return b;
}

void catalog_init(const void* spawn_api) {
    g_spawn = static_cast<const KilnSpawnApi*>(spawn_api);
    if (g_spawn && g_spawn->header.size < sizeof(KilnSpawnApi) - sizeof(void*)) g_spawn = nullptr;
    if (!g_list.empty()) return;
    g_list.reserve(sizeof kRaw / sizeof kRaw[0]);
    for (const Raw& r : kRaw) {
        KilnCreatureTemplate t{};
        t.size = sizeof t;
        t.kind = r.kind;
        if (!parse_guid(r.guid, t.guid.b)) continue;
        copy_cstr(t.name, sizeof t.name, r.name);
        copy_cstr(t.display, sizeof t.display, r.display);
        t.flags = r.flags | KILN_CREATURE_T_CATALOG;
        t.faction = static_cast<uint8_t>(r.faction);
        t.enemy_class = static_cast<uint8_t>(r.enemy_class);
        t.loot_slots_min = static_cast<uint8_t>(r.slots_min);
        t.loot_slots_max = static_cast<uint8_t>(r.slots_max);
        t.level = r.level;
        t.level_max = r.level_max;
        t.xp_mod = r.xp;
        t.scale = r.scale;
        parse_guid(r.arsenal, t.arsenal.b);
        parse_guid(r.loot, t.loot_labels.b);
        parse_guid(r.model, t.model.b);
        parse_guid(r.graph, t.animation_graph.b);
        g_list.push_back(t);
    }
    std::unique_lock lock(g_mu);
    reindex();
}

uint32_t catalog_count() {
    std::shared_lock lock(g_mu);
    return static_cast<uint32_t>(g_list.size());
}

namespace {
bool at_locked(uint32_t i, KilnCreatureTemplate* out) {
    if (i >= g_list.size()) return false;
    const uint32_t keep = out->size;
    std::memcpy(out, &g_list[i], std::min<size_t>(keep, sizeof(KilnCreatureTemplate)));
    out->size = keep;
    return true;
}
}  // namespace

bool catalog_at(uint32_t i, KilnCreatureTemplate* out) {
    std::shared_lock lock(g_mu);
    return at_locked(i, out);
}

bool catalog_get(const uint8_t guid[16], KilnCreatureTemplate* out) {
    std::shared_lock lock(g_mu);
    auto it = g_by_guid.find(std::string(reinterpret_cast<const char*>(guid), 16));
    return it != g_by_guid.end() && at_locked(it->second, out);
}

bool catalog_find(std::string_view name, KilnCreatureTemplate* out) {
    uint8_t g[16];
    if (parse_guid(name, g)) return catalog_get(g, out);
    std::shared_lock lock(g_mu);
    auto it = g_by_name.find(lower(name));
    return it != g_by_name.end() && at_locked(it->second, out);
}

void catalog_stats(uint32_t* loaded, uint32_t* extra) {
    *loaded = g_loaded.load();
    *extra = g_engine_extra.load();
}

// Tick, once the engine's templates are loaded: LOADED bits from kiln.spawn, then a walk of the engine's template infos
// (kiln.spawn's template map constants) for templates whose entities get DynamicEnemy01BehaviorState or
// DynamicActiveNpcState that the catalog lacks.
void catalog_check_engine() {
    if (!g_spawn || g_spawn->template_count() == 0 || g_step < g_next_check) return;
    g_next_check = g_step + 250;
    if (!g_checked) {
        uint32_t loaded = 0;
        std::unique_lock lock(g_mu);
        for (auto& t : g_list) {
            KilnTemplateInfo ti{};
            ti.size = sizeof ti;
            if (g_spawn->template_get(&t.guid, &ti) == KILN_OK && (ti.source & 2)) {
                t.flags |= KILN_CREATURE_T_LOADED;
                ++loaded;
            }
        }
        g_loaded = loaded;
    }
    const bool first = !g_checked;
    g_checked = true;
    const uint32_t loaded = g_loaded.load();
    // engine walk
    int64_t tmap = 0, tcap = 0, tvals = 0, cmap = 0, tcomps = 0;
    const bool ok = g_api->constant(kstr("spawn/registry.template_map"), &tmap) == KILN_OK &&
                    g_api->constant(kstr("spawn/registry.template_map_cap"), &tcap) == KILN_OK &&
                    g_api->constant(kstr("spawn/registry.template_map_values"), &tvals) == KILN_OK &&
                    g_api->constant(kstr("spawn/registry.component_map"), &cmap) == KILN_OK &&
                    g_api->constant(kstr("spawn/template.components"), &tcomps) == KILN_OK && fn.component_map_find;
    const uintptr_t w = world();
    if (!ok || !w) {
        if (first)
            KC_I("catalog: %u templates, %u loaded by the game (engine check skipped: layout unavailable)",
                 catalog_count(), loaded);
        g_next_check = UINT64_MAX;
        return;
    }
    uint8_t* registry = nullptr;
    if (!seh_read(reinterpret_cast<void*>(w + L.world_storage), &registry, 8) || !registry) return;
    uint32_t count = 0;  // engine hash map: u32 count at +0x54
    seh_read(registry + tmap + 0x54, &count, 4);
    if (!first && count == g_engine_count) return;
    g_engine_count = count;
    uint32_t idx[2] = {0xFFFF, 0xFFFF};
    const uint32_t hashes[2] = {H::Dyn, ecs_hash("DynamicActiveNpcState")};
    for (int k = 0; k < 2; ++k) {
        uint16_t ti = 0;
        if (seh_component_index(registry + cmap, hashes[k], &ti)) idx[k] = ti;
    }
    uint64_t cap = 0;
    uint8_t* values = nullptr;
    if (!seh_read(registry + tcap, &cap, 8) || !seh_read(registry + tvals, &values, 8) || !values || cap > (1u << 22))
        return;
    uint8_t* bits = nullptr;
    seh_read(registry + tmap + 8, &bits, 8);
    uint32_t enemies = 0, villagers = 0, extra = 0;
    std::string example;
    std::vector<KilnCreatureTemplate> add;
    for (uint64_t i = 0; i < cap; ++i) {
        if (bits) {
            uint64_t word = 0;
            if (!seh_read(bits + (i >> 6) * 8, &word, 8)) break;
            if (!((word >> (i & 63)) & 1)) continue;
        }
        uint8_t* info = nullptr;
        if (!seh_read(values + i * 8, &info, 8) || !info) continue;
        bool has[2] = {false, false};
        for (int k = 0; k < 2; ++k) {
            if (idx[k] >= 0x1000) continue;
            uint64_t bw = 0;
            if (seh_read(info + tcomps + (idx[k] >> 6) * 8, &bw, 8)) has[k] = (bw >> (idx[k] & 63)) & 1;
        }
        if (!has[0] && !has[1]) continue;
        (has[0] ? enemies : villagers)++;
        uint8_t g[16];
        if (!seh_read(info, g, 16)) continue;
        bool known;
        {
            std::shared_lock lock(g_mu);
            auto it = g_by_guid.find(std::string(reinterpret_cast<const char*>(g), 16));
            known = it != g_by_guid.end() && !(g_list[it->second].flags & KILN_CREATURE_T_RUNTIME);
            if (it != g_by_guid.end()) continue;  // shipped, or a runtime entry added before
        }
        if (known) continue;
        ++extra;
        // a template the shipped catalog lacks (a kiln.content clone, or a newer game build): a runtime entry
        KilnCreatureTemplate t{};
        t.size = sizeof t;
        t.kind = has[0] ? KILN_CREATURE_ENEMY : KILN_CREATURE_VILLAGER;
        std::memcpy(t.guid.b, g, 16);
        const char* np = nullptr;
        if (seh_read(info + 0x10, &np, 8) && np) seh_read(np, t.name, sizeof t.name - 1);
        t.flags = KILN_CREATURE_T_LOADED | KILN_CREATURE_T_RUNTIME;
        t.faction = 255;
        t.enemy_class = 255;
        t.scale = 1;
        if (example.empty()) example = t.name;
        add.push_back(t);
    }
    if (!add.empty()) {
        std::unique_lock lock(g_mu);
        for (auto& t : add) g_list.push_back(t);
        reindex();
    }
    g_engine_extra = g_engine_extra.load() + extra;
    if (first || extra)
        KC_I("catalog: %u templates (%u shipped and loaded by the game); engine: %u enemy-AI and %u villager templates, "
             "%u added from the engine%s%s",
             catalog_count(), loaded, enemies, villagers, extra, extra ? ", e.g. " : "", example.c_str());
}

}  // namespace kcr
