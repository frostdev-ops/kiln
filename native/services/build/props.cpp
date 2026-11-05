// SPDX-License-Identifier: MIT
// Props the way the game's building helper places them (Building_spawnProp, build RVA 0x2248e0 on svn 1285601): the
// template plus CurrentTransform, DisableRandomLootOnDestroy and, when an item is known, UsedItem (what dismantling
// gives back). The helper queues the create from inside a program; Kiln creates it at once through SpawnWrapper (the
// core symbol kiln.spawn uses too) on the tick thread, with the same components. Items: ItemInfo +item.placed_entity
// (EquipmentSetup.placedEntity, the template a prop item places) and +item.material_id (ChangeVoxelData.
// placeVoxelMaterialId, the voxel material a building-material item places), read through the game's ItemFind.
#include <algorithm>
#include <array>
#include <map>
#include <unordered_map>

#include "internal.h"
#include "services/kiln_items.h"

namespace kb {
namespace {

constexpr uint32_t kCurrentTransform = 0x027ac564u, kUsedItem = 0x0b02361du, kDisableLoot = 0x72462ce1u,
                   kBaseId = 0x4927f4e2u;

#pragma pack(push, 1)
struct WorldTransform {
    int64_t pos[3];
    float rot[4];
    float scale[3];
    uint32_t pad;
};
struct SpawnComponent {
    uint32_t hash, pad;
    const void* data;
    uint64_t size;
};
struct SpawnSlice {
    const SpawnComponent* data;
    uint64_t count;
};
#pragma pack(pop)
static_assert(sizeof(WorldTransform) == 56 && sizeof(SpawnComponent) == 24);

const KilnItemsApi* g_items = nullptr;
std::mutex g_item_mu;
std::map<std::array<uint8_t, 16>, uint32_t> g_tpl_item;  // template GUID -> item that places it
bool g_tpl_map_built = false;

bool guid_zero(const KilnGuid& g) {
    for (uint8_t b : g.b)
        if (b) return false;
    return true;
}

uintptr_t item_registry() {
    const uintptr_t w = server_world();
    return w ? g_api->singleton(w, kstr("g38_items::ItemRegistry")) : 0;
}

const uint8_t* item_info(uint32_t item) {
    if (!F.items || !item) return nullptr;
    const uintptr_t reg = item_registry();
    if (!reg) return nullptr;
    const uint8_t* p = nullptr;
    if (!seh_item(item, reg, &p)) return nullptr;
    return p;
}

void build_template_map() {
    if (g_tpl_map_built || !g_items || !F.items || !item_registry()) return;
    g_tpl_map_built = true;
    const uint32_t n = g_items->item_count();
    uint32_t mapped = 0;
    for (uint32_t i = 0; i < n; ++i) {
        KilnItemInfo info{};
        info.size = sizeof info;
        if (g_items->item_at(i, &info) != KILN_OK) continue;
        const uint8_t* p = item_info(info.id);
        std::array<uint8_t, 16> g{};
        if (!p || !seh_copy(g.data(), p + L.item_placed_entity, 16)) continue;
        bool zero = true;
        for (uint8_t b : g) zero = zero && !b;
        if (zero) continue;
        g_tpl_item.emplace(g, info.id);  // first (by name) wins
        ++mapped;
    }
    KB_I("props: %u of %u items place a template", mapped, n);
}

bool fill_info(KilnEntity e, KilnBuildPropInfo* out) {
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->entity = e;
    KilnEntitySnapshot snap{};
    snap.size = sizeof snap;
    if (!g_spawn || g_spawn->entity_info(e, &snap) != KILN_OK) return false;
    if (snap.has_template) out->template_guid = snap.template_guid;
    out->position = snap.position;
    std::memcpy(out->rotation, snap.rotation, sizeof out->rotation);
    out->scale[0] = out->scale[1] = out->scale[2] = 1.f;
    if (g_world) {
        KilnTransform t{};
        if (g_world->transform(e, &t) == KILN_OK) std::memcpy(out->scale, t.scale, sizeof out->scale);
        uint32_t sz = 0;
        if (const void* u = g_world->component(e, kUsedItem, &sz); u && sz >= 4) {
            seh_copy(&out->item_id, u, 4);
            if (out->item_id) out->flags |= KILN_BUILD_PROP_PLACED;
        }
        if (g_world->component(e, kBaseId, &sz)) out->flags |= KILN_BUILD_PROP_ALTAR;
    }
    KilnTemplateInfo ti{};
    ti.size = sizeof ti;
    if (snap.has_template && g_spawn->template_get(&snap.template_guid, &ti) == KILN_OK) {
        out->kind = ti.kind;
        std::memcpy(out->name, ti.name, sizeof out->name);
        out->name[sizeof out->name - 1] = 0;
        if ((ti.flags & KILN_TEMPLATE_FLAG_KNOWN) && !(ti.flags & KILN_TEMPLATE_FLAG_DO_NOT_SAVE))
            out->flags |= KILN_BUILD_PROP_SAVED;
    }
    return true;
}

}  // namespace

void props_resolve() {
    g_items = static_cast<const KilnItemsApi*>(g_api->service(kstr(KILN_ITEMS_SERVICE), 1));
    F.props = fn.spawn && fn.get_transform && g_spawn && g_world;
    if (!g_items) KB_W("kiln.items is unavailable: props get no UsedItem unless the caller names the item");
}

uint32_t item_material(uint32_t item) {
    std::lock_guard lock(g_item_mu);
    const uint8_t* p = item_info(item);
    uint8_t m = 0;
    uint64_t ref[2] = {};
    if (!p || !seh_copy(ref, p + L.item_material_ref, sizeof ref) || (!ref[0] && !ref[1])) return 0;
    if (!seh_copy(&m, p + L.item_material_id, 1)) return 0;
    return m;
}

KilnResult item_template(uint32_t item, KilnGuid* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    if (!F.items) return KILN_ERR_UNAVAILABLE;
    std::lock_guard lock(g_item_mu);
    const uint8_t* p = item_info(item);
    if (!p) return KILN_ERR_NOT_FOUND;
    if (!seh_copy(out->b, p + L.item_placed_entity, 16)) return KILN_ERR_INTERNAL;
    return guid_zero(*out) ? KILN_ERR_NOT_FOUND : KILN_OK;
}

KilnResult prop_info(KilnEntity e, KilnBuildPropInfo* out) {
    if (!out || out->size < sizeof(KilnBuildPropInfo)) return KILN_ERR_INVALID_ARGUMENT;
    if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!g_spawn) return KILN_ERR_UNAVAILABLE;
    return fill_info(e, out) ? KILN_OK : KILN_ERR_NOT_FOUND;
}

KilnResult prop_place(const KilnBuildProp* p, KilnEntity* out) {
    if (out) *out = 0;
    if (!p || p->size < sizeof(KilnBuildProp)) return KILN_ERR_INVALID_ARGUMENT;
    if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!F.props) return KILN_ERR_UNAVAILABLE;
    const uintptr_t w = server_world();
    if (!w || !world_running()) return KILN_ERR_UNAVAILABLE;
    if (!std::isfinite(p->position.x) || !std::isfinite(p->position.y) || !std::isfinite(p->position.z))
        return KILN_ERR_INVALID_ARGUMENT;
    // template: GUID, template name, or an item that places a template
    KilnGuid guid = p->template_guid;
    uint32_t item = p->item_id;
    KilnTemplateInfo ti{};
    ti.size = sizeof ti;
    if (guid_zero(guid)) {
        const std::string_view name = sv(p->template_name);
        if (name.empty()) return KILN_ERR_INVALID_ARGUMENT;
        if (g_spawn->template_find(p->template_name, &ti) == KILN_OK) {
            guid = ti.guid;
        } else if (g_items) {
            const uint32_t id = g_items->find_item(p->template_name);
            if (!id || item_template(id, &guid) != KILN_OK) return KILN_ERR_NOT_FOUND;
            if (!item) item = id;
        } else {
            return KILN_ERR_NOT_FOUND;
        }
    }
    if (g_spawn->template_get(&guid, &ti) != KILN_OK) return KILN_ERR_NOT_FOUND;
    if (ti.kind == KILN_TEMPLATE_CREATURE || ti.kind == KILN_TEMPLATE_NPC) return KILN_ERR_INVALID_ARGUMENT;
    if (!item) {
        std::lock_guard lock(g_item_mu);
        build_template_map();
        std::array<uint8_t, 16> g;
        std::memcpy(g.data(), guid.b, 16);
        if (auto it = g_tpl_item.find(g); it != g_tpl_item.end()) item = it->second;
    }
    if (item == ~0u) item = 0;
    // policy
    const KilnVec3 lo{p->position.x - 0.5, p->position.y - 0.5, p->position.z - 0.5};
    const KilnVec3 hi{p->position.x + 0.5, p->position.y + 0.5, p->position.z + 0.5};
    if (p->flags & KILN_BUILD_F_RESPECT_ZONES)
        if (zone_denies_box(p->actor, lo, hi, KILN_GAMEPLAY_DENY_BUILD)) {
            S.refused.fetch_add(1, std::memory_order_relaxed);
            return KILN_ERR_REVOKED;
        }
    if (!(p->flags & KILN_BUILD_F_IN_BASES)) {
        KilnEntity altar = 0;
        if (touches_base(lo, hi, &altar)) {
            S.refused.fetch_add(1, std::memory_order_relaxed);
            return KILN_ERR_REVOKED;
        }
    }
    if (p->flags & KILN_BUILD_F_DRY_RUN) return KILN_OK;
    // spawn with the building helper's components
    WorldTransform xf{};
    xf.pos[0] = m_to_fixed(p->position.x), xf.pos[1] = m_to_fixed(p->position.y), xf.pos[2] = m_to_fixed(p->position.z);
    float q[4] = {p->rotation[0], p->rotation[1], p->rotation[2], p->rotation[3]};
    const float len = std::sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
    if (!(len > 1e-6f) || !std::isfinite(len)) q[0] = q[1] = q[2] = 0, q[3] = 1;
    else for (float& c : q) c /= len;
    std::memcpy(xf.rot, q, sizeof q);
    xf.scale[0] = xf.scale[1] = xf.scale[2] = 1.f;
    uint8_t disable = 0;
    SpawnComponent comps[3] = {{kCurrentTransform, 0, &xf, sizeof xf}, {kDisableLoot, 0, &disable, 1},
                               {kUsedItem, 0, &item, 4}};
    const SpawnSlice slice{comps, item ? 3u : 2u};
    uint32_t id = 0;
    unsigned long code = 0;
    if (!seh_spawn(w, guid.b, &slice, &id, &code)) {
        KB_E("prop spawn of %s faulted (0x%08lx)", ti.name, code);
        return KILN_ERR_INTERNAL;
    }
    if (!id) {
        KB_W("the engine refused to create %s (see the game log)", ti.name);
        return KILN_ERR_INTERNAL;
    }
    // a template that founds a base: undo at once unless asked for (no program ran in between)
    uint32_t sz = 0;
    if (!(p->flags & KILN_BUILD_F_ALLOW_ALTARS) && g_world->component(id, kBaseId, &sz)) {
        g_spawn->destroy(id);
        KB_W("prop %s has BaseIdComponent (a flame altar): refused without KILN_BUILD_F_ALLOW_ALTARS", ti.name);
        S.refused.fetch_add(1, std::memory_order_relaxed);
        return KILN_ERR_REVOKED;
    }
    S.props_placed.fetch_add(1, std::memory_order_relaxed);
    if (out) *out = id;
    KB_D("prop %s placed as %u at (%.2f, %.2f, %.2f), item %u", ti.name, id, p->position.x, p->position.y,
         p->position.z, item);
    return KILN_OK;
}

KilnResult prop_remove(KilnEntity e) {
    if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!g_spawn) return KILN_ERR_UNAVAILABLE;
    if (e >= 1 && e <= 1023) return KILN_ERR_INVALID_ARGUMENT;
    KilnBuildPropInfo info{};
    info.size = sizeof info;
    if (!fill_info(e, &info)) return KILN_ERR_NOT_FOUND;
    if (info.kind == KILN_TEMPLATE_CREATURE || info.kind == KILN_TEMPLATE_NPC) return KILN_ERR_INVALID_ARGUMENT;
    const KilnResult rc = g_spawn->destroy(e);
    if (rc == KILN_OK) S.props_removed.fetch_add(1, std::memory_order_relaxed);
    return rc;
}

KilnResult props_in_box(KilnVec3 lo, KilnVec3 hi, uint32_t filter, KilnBuildPropInfo* out, size_t capacity,
                        size_t* count) {
    if (count) *count = 0;
    if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!g_spawn) return KILN_ERR_UNAVAILABLE;
    if (!(hi.x >= lo.x && hi.y >= lo.y && hi.z >= lo.z)) return KILN_ERR_INVALID_ARGUMENT;
    for (size_t i = 0; i < capacity && out; ++i)
        if (out[i].size < sizeof(KilnBuildPropInfo)) return KILN_ERR_INVALID_ARGUMENT;
    const KilnVec3 c{(lo.x + hi.x) / 2, (lo.y + hi.y) / 2, (lo.z + hi.z) / 2};
    const double r = std::sqrt((hi.x - c.x) * (hi.x - c.x) + (hi.y - c.y) * (hi.y - c.y) + (hi.z - c.z) * (hi.z - c.z)) + 0.01;
    std::vector<KilnEntity> ids(8192);
    size_t n = 0;
    KilnResult rc = g_spawn->find(nullptr, KilnStr{"", 0}, c, r, ids.data(), ids.size(), &n);
    if (rc == KILN_ERR_BUFFER_TOO_SMALL || n > ids.size()) {
        ids.resize(n);
        rc = g_spawn->find(nullptr, KilnStr{"", 0}, c, r, ids.data(), ids.size(), &n);
    }
    if (rc != KILN_OK) return rc;
    n = std::min(n, ids.size());
    size_t hits = 0;
    for (size_t i = 0; i < n; ++i) {
        const KilnEntity e = ids[i];
        if (e >= 1 && e <= 1023) continue;
        KilnBuildPropInfo info{};
        info.size = sizeof info;
        KilnEntitySnapshot snap{};
        snap.size = sizeof snap;
        if (g_spawn->entity_info(e, &snap) != KILN_OK || snap.parent) continue;  // children come with their parent
        if (!fill_info(e, &info)) continue;
        const KilnVec3& p = info.position;
        if (p.x < lo.x || p.x > hi.x || p.y < lo.y || p.y > hi.y || p.z < lo.z || p.z > hi.z) continue;
        if (info.kind == KILN_TEMPLATE_CREATURE || info.kind == KILN_TEMPLATE_NPC) continue;
        if (!(filter & KILN_BUILD_PROPS_ALL) && info.kind == KILN_TEMPLATE_PICKUP) continue;
        if ((filter & KILN_BUILD_PROPS_PLACED) && !(info.flags & KILN_BUILD_PROP_PLACED)) continue;
        if ((filter & KILN_BUILD_PROPS_SAVED) && !(info.flags & KILN_BUILD_PROP_SAVED)) continue;
        if (out && hits < capacity) {
            info.size = out[hits].size;
            out[hits] = info;
        }
        ++hits;
    }
    if (count) *count = hits;
    return out && hits > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

}  // namespace kb
