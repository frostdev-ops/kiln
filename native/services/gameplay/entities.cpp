// SPDX-License-Identifier: MIT
// Every entity the server world creates or destroys (Forge: EntityJoinLevelEvent / EntityLeaveLevelEvent), not only
// Kiln.Spawn's: creatures from spawners, NPCs, props, pickups, loot, child entities, players, and the entities the save
// loads. Two raw kiln.hooks links on the engine's immediate entity functions (docs/re/spawn.md, docs/re/gameplay.md section 9):
//  - createEntity(storage, id, SpawnRequest*, mask*) -> id: every creation path ends here (SpawnWrapper for Kiln.Spawn
//    and admin spawns, the command executor for the queued creates of game programs, the save loader).
//  - destroyEntity(storage, id, mode, skip_culled) -> bool: the command executor's queued destroys, the game's own
//    removal loops (LifeTime, culling) and Kiln.Spawn.
// Both may run on game worker threads inside the step. With nobody subscribed the hooks only test an atomic mask. With
// subscribers they read the entity record (template, parent) and position, classify the template through kiln.spawn
// (thread-safe) against the category filter, and append a record; Post reports them on the tick thread.
#include <windows.h>

#include "internal.h"
#include "kiln_hook.hpp"
#include "services/kiln_spawn.h"

namespace kg {
namespace {

using CreateFn = uint32_t(__fastcall*)(uintptr_t storage, uint32_t id, void* request, void* mask);
using DestroyFn = uint8_t(__fastcall*)(uintptr_t storage, uint32_t id, uint8_t mode, uint8_t skip_culled);

kiln::RawHook h_create, h_destroy;
const KilnSpawnApi* g_spawn = nullptr;

struct Offs {
    int64_t entity_map = -1, entity_cap = -1, entity_records = -1, rec_id = 0x10, rec_parent = 0x14, rec_template = -1;
} O;
constexpr int64_t kMapBits = 0x08, kMapKeys = 0x28, kMapCount = 0x54;

std::atomic<uint32_t> g_categories{0};  // KILN_GAMEPLAY_ENTITY_* wanted (0 = hooks idle)
std::atomic<uint64_t> g_joined{0}, g_left{0}, g_filtered{0};

struct Rec {
    bool joined;
    uint32_t id, parent, category;
    bool has_guid;
    uint8_t guid[16];
    KilnVec3 pos;
    bool tick;  // created/destroyed outside the game's sim step (Kiln.Spawn, other services, mods), not by the game
};
std::mutex g_mu;
std::vector<Rec> g_recs;
std::atomic<uint32_t> g_dropped{0};

uint32_t entity_hash(uint32_t k) {  // the entity map's hash (0x650c50)
    uint32_t x = ((k >> 16) ^ k) * 0x45d9f3bu;
    x = ((x >> 16) ^ x) * 0x45d9f3bu;
    return (x >> 16) ^ x;
}

// Entity map lookup (storage + spawn/storage.entity_map): template GUID and parent of a live entity.
bool seh_record(uintptr_t storage, uint32_t id, uint8_t* guid, bool* has_guid, uint32_t* parent) {
    __try {
        *has_guid = false;
        *parent = 0;
        const uintptr_t map = storage + static_cast<uintptr_t>(O.entity_map);
        const uint64_t cap = *reinterpret_cast<const uint64_t*>(storage + O.entity_cap);
        if (cap == 0 || (cap & (cap - 1)) != 0) return false;
        if (*reinterpret_cast<const uint32_t*>(map + kMapCount) == 0) return false;
        const uint64_t* bits = *reinterpret_cast<uint64_t* const*>(map + kMapBits);
        const uint32_t* keys = *reinterpret_cast<uint32_t* const*>(map + kMapKeys);
        const uintptr_t* recs = *reinterpret_cast<uintptr_t* const*>(storage + O.entity_records);
        uint64_t i = entity_hash(id) & (cap - 1);
        for (uint64_t steps = 0; steps < cap && steps < 4096; ++steps) {
            if (!((bits[i >> 6] >> (i & 63)) & 1)) return false;
            if (keys[i] == id) {
                const uintptr_t rec = recs[i];
                if (!rec || *reinterpret_cast<const uint32_t*>(rec + O.rec_id) != id) return false;
                *parent = *reinterpret_cast<const uint32_t*>(rec + O.rec_parent);
                const uintptr_t ti = *reinterpret_cast<const uintptr_t*>(rec + O.rec_template);
                if (ti) {
                    for (int k = 0; k < 16; ++k) guid[k] = reinterpret_cast<const uint8_t*>(ti)[k];
                    *has_guid = true;
                }
                return true;
            }
            i = i + 1 < cap ? i + 1 : 0;
        }
        return false;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

uint32_t category_of(bool has_guid, const uint8_t* guid, uint32_t id) {
    if (id >= 1 && id <= 1023) return KILN_GAMEPLAY_ENTITY_PLAYER;
    if (!has_guid || !g_spawn) return KILN_GAMEPLAY_ENTITY_OTHER;
    KilnTemplateInfo ti{};
    ti.size = sizeof ti;
    KilnGuid g;
    std::memcpy(g.b, guid, 16);
    if (g_spawn->template_get(&g, &ti) != KILN_OK) return KILN_GAMEPLAY_ENTITY_OTHER;
    switch (ti.kind) {
    case KILN_TEMPLATE_CREATURE: return KILN_GAMEPLAY_ENTITY_CREATURE;
    case KILN_TEMPLATE_NPC: return KILN_GAMEPLAY_ENTITY_NPC;
    case KILN_TEMPLATE_PROP: return KILN_GAMEPLAY_ENTITY_PROP;
    case KILN_TEMPLATE_PICKUP: return KILN_GAMEPLAY_ENTITY_PICKUP;
    default: return KILN_GAMEPLAY_ENTITY_OTHER;
    }
}

bool server_storage(uintptr_t storage) {
    const uintptr_t w = world();
    return w && storage == storage_of(w);
}

// Reads what an event needs while the entity exists. false: not reported (unknown entity or filtered out).
bool capture(uintptr_t storage, uint32_t id, bool joined, Rec* r) {
    r->joined = joined;
    r->id = id;
    if (!seh_record(storage, id, r->guid, &r->has_guid, &r->parent)) return false;
    r->category = category_of(r->has_guid, r->guid, id);
    if (!(g_categories.load(std::memory_order_relaxed) & r->category)) {
        g_filtered.fetch_add(1, std::memory_order_relaxed);
        return false;
    }
    entity_position(id, &r->pos);
    r->tick = !g_in_sim.load(std::memory_order_acquire);  // outside the step: a Kiln service or mod did it
    return true;
}

void push(const Rec& r) {
    std::lock_guard lock(g_mu);
    if (g_recs.size() >= 65536) {
        g_dropped.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    g_recs.push_back(r);
}

uint32_t __fastcall hk_create(uintptr_t storage, uint32_t id, void* request, void* mask) {
    const uint32_t made = h_create.call<uint32_t>(storage, id, request, mask);
    if (made && g_categories.load(std::memory_order_relaxed) && server_storage(storage)) {
        Rec r{};
        if (capture(storage, made, true, &r)) push(r);
    }
    return made;
}

uint8_t __fastcall hk_destroy(uintptr_t storage, uint32_t id, uint8_t mode, uint8_t skip_culled) {
    if (!g_categories.load(std::memory_order_relaxed) || !server_storage(storage))
        return h_destroy.call<uint8_t>(storage, id, mode, skip_culled);
    Rec r{};
    const bool have = capture(storage, id, false, &r);
    const uint8_t ok = h_destroy.call<uint8_t>(storage, id, mode, skip_culled);
    if (ok && have) push(r);
    return ok;
}

bool g_ready = false;

}  // namespace

void entities_set_spawn_service(const void* p) { g_spawn = static_cast<const KilnSpawnApi*>(p); }

bool entities_install() {
    uintptr_t create = 0, destroy = 0;
    auto sym = [](const char* n, uintptr_t* out) {
        return g_api->symbol(KilnStr{n, std::strlen(n)}, out) == KILN_OK && *out;
    };
    auto cst = [](const char* n, int64_t* out) { return g_api->constant(KilnStr{n, std::strlen(n)}, out) == KILN_OK; };
    if (!g_api->feature(kstr("gameplay.entities"))) return false;
    if (!sym("spawn/CreateEntity", &create) || !sym("spawn/DestroyEntity", &destroy)) return false;
    if (!cst("spawn/storage.entity_map", &O.entity_map) || !cst("spawn/storage.entity_map_cap", &O.entity_cap) ||
        !cst("spawn/storage.entity_records", &O.entity_records) || !cst("spawn/record.template", &O.rec_template))
        return false;
    cst("spawn/record.id", &O.rec_id);
    cst("spawn/record.parent", &O.rec_parent);
    if (O.entity_cap != O.entity_map + 0x10 || O.entity_records != O.entity_map + 0x40 || O.rec_template <= 0 ||
        O.rec_template > 0x100 || L.world_storage <= 0) {
        KG_W("entity map layout unexpected (map %lld cap %lld records %lld template %lld): entity events off",
             static_cast<long long>(O.entity_map), static_cast<long long>(O.entity_cap),
             static_cast<long long>(O.entity_records), static_cast<long long>(O.rec_template));
        return false;
    }
    if (!h_create.install(g_api, g_owner, create, reinterpret_cast<void*>(&hk_create), "spawn/CreateEntity")) {
        KG_E("hooking createEntity at %p failed", reinterpret_cast<void*>(create));
        return false;
    }
    if (!h_destroy.install(g_api, g_owner, destroy, reinterpret_cast<void*>(&hk_destroy), "spawn/DestroyEntity")) {
        KG_E("hooking destroyEntity at %p failed", reinterpret_cast<void*>(destroy));
        h_create = {};
        return false;
    }
    g_ready = true;
    return true;
}

bool entities_ready() { return g_ready; }

KilnResult entity_filter(uint32_t categories) {
    if (!g_ready) return KILN_ERR_UNAVAILABLE;
    const uint32_t old = g_categories.exchange(categories & KILN_GAMEPLAY_ENTITY_ALL);
    if (old != (categories & KILN_GAMEPLAY_ENTITY_ALL))
        KG_I("entity events: categories 0x%x (was 0x%x)", categories & KILN_GAMEPLAY_ENTITY_ALL, old);
    return KILN_OK;
}

void entities_status(uint64_t* joined, uint64_t* left, uint64_t* filtered, uint32_t* categories) {
    *joined = g_joined.load();
    *left = g_left.load();
    *filtered = g_filtered.load();
    *categories = g_categories.load();
}

// Post: report what the hooks saw (in order: a creature created and removed in one step gives JOINED, then LEFT).
void entities_post() {
    if (!g_ready) return;
    std::vector<Rec> recs;
    {
        std::lock_guard lock(g_mu);
        if (g_recs.empty()) return;
        recs.swap(g_recs);
    }
    if (const uint32_t d = g_dropped.exchange(0)) KG_W("%u entity record(s) dropped (more than 65536 in one step)", d);
    const bool want_join = has_subscribers(KILN_GAMEPLAY_ENTITY_JOINED), want_left = has_subscribers(KILN_GAMEPLAY_ENTITY_LEFT);
    for (const Rec& r : recs) {
        (r.joined ? g_joined : g_left).fetch_add(1, std::memory_order_relaxed);
        if (r.joined ? !want_join : !want_left) continue;
        KilnGameplayEvent ev;
        std::memset(&ev, 0, sizeof ev);
        ev.size = sizeof ev;
        ev.kind = r.joined ? KILN_GAMEPLAY_ENTITY_JOINED : KILN_GAMEPLAY_ENTITY_LEFT;
        ev.step = g_step;
        ev.phase = KILN_GAMEPLAY_PHASE_POST;
        ev.actor = r.id;
        ev.target = r.parent;
        ev.op = r.category;
        ev.extra = r.tick ? 1u : 0u;
        ev.position = r.pos;
        if (r.has_guid) std::memcpy(ev.guid, r.guid, 16);
        // Canceling a join removes the entity again (not players; Kiln.Spawn destroys it like a canceled natural spawn).
        const bool cancelable = r.joined && r.category != KILN_GAMEPLAY_ENTITY_PLAYER && g_spawn;
        if (cancelable) ev.flags |= KILN_GAMEPLAY_F_CANCELABLE;
        if (dispatch(&ev) && cancelable) {
            const KilnResult rc = g_spawn->destroy(r.id);
            if (rc == KILN_OK) ++S.canceled;
            KG_D("entity %u join canceled: %s", r.id, rc == KILN_OK ? "destroyed" : "could not destroy it");
        }
    }
}

}  // namespace kg
