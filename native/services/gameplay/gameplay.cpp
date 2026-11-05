// SPDX-License-Identifier: MIT
// kiln.gameplay: player/world interaction events (crafting, interaction, loot, pickups, building, terraforming,
// teleports, natural spawns) with cancellation where the server can refuse safely, plus native protection zones.
// See native/core/abi/services/kiln_gameplay.h for the contract and docs/re/gameplay.md for the evidence.
#include <cmath>
#include <cstdarg>
#include <cstdio>

#include "internal.h"
#include "services/kiln_players.h"
#include "services/kiln_spawn.h"
#include "services/kiln_world.h"

namespace kg {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
Layout L;
Fns fn;
Features F;
EventIds H;
Stats S;
uint64_t g_step = 0;
std::atomic<bool> g_in_step{false};
std::atomic<bool> g_in_sim{false};

void logf(int level, const char* fmt, ...) {
    char buf[1024];
    std::va_list ap;
    va_start(ap, fmt);
    int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (n >= static_cast<int>(sizeof buf)) n = sizeof buf - 1;
    if (g_api) g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(n)});
}

uint32_t fnv1a(std::string_view s, uint32_t h) {
    for (unsigned char c : s) {
        h ^= c;
        h *= 0x01000193u;
    }
    return h;
}

int64_t m_to_fixed(double m) { return static_cast<int64_t>(std::llround(m * 4294967296.0)); }

uintptr_t world() { return g_api && g_api->server_world ? g_api->server_world() : 0; }

bool entity_position(KilnEntity e, KilnVec3* out) {
    *out = KilnVec3{};
    if (!F.transforms || !e) return false;
    const uintptr_t w = world();
    if (!w) return false;
    const int64_t* t = nullptr;
    if (!seh_get_transform(w, e, &t) || !t) return false;
    out->x = fixed_to_m(t[0]);
    out->y = fixed_to_m(t[1]);
    out->z = fixed_to_m(t[2]);
    return true;
}

void* component(KilnEntity e, uint32_t hash, uint32_t* size) {
    if (size) *size = 0;
    if (!F.components || !e) return nullptr;
    const uintptr_t w = world();
    if (!w) return nullptr;
    void* p = nullptr;
    uint32_t n = 0;
    if (!seh_get_component(w, hash, e, &p, &n)) return nullptr;
    if (size) *size = n;
    return p;
}

// ---------------------------------------------------------------------------------------------- subscribers ----
namespace {

struct Sub {
    KilnHandle handle;
    KilnOwner owner;
    uint32_t mask;
    KilnGameplayFn fn;
    void* user;
    bool live;
};
std::vector<Sub> g_subs;
uint64_t g_next_handle = 1;
std::atomic<uint32_t> g_mask{0};  // union of live masks (read by worker-thread hooks)

void recompute_mask() {
    uint32_t m = 0;
    for (auto& s : g_subs)
        if (s.live) m |= s.mask;
    g_mask.store(m, std::memory_order_release);
}

}  // namespace

bool has_subscribers(uint32_t kind) { return kind < 32 && (g_mask.load(std::memory_order_acquire) & (1u << kind)) != 0; }

uint32_t subscriber_count() {
    uint32_t n = 0;
    for (auto& s : g_subs) n += s.live ? 1 : 0;
    return n;
}

bool dispatch(KilnGameplayEvent* ev) {
    const bool cancelable = (ev->flags & KILN_GAMEPLAY_F_CANCELABLE) != 0;
    if (!has_subscribers(ev->kind)) return (ev->flags & KILN_GAMEPLAY_F_CANCELED) != 0;
    ++S.dispatched;
    // Index loop: a subscriber may subscribe or unsubscribe (vector may grow; entries are only marked dead).
    for (size_t i = 0; i < g_subs.size(); ++i) {
        Sub s = g_subs[i];
        if (!s.live || !(s.mask & (1u << ev->kind))) continue;
        uint32_t verdict = KILN_GAMEPLAY_KEEP;
        unsigned long code = 0;
        if (!seh_subscriber(s.fn, ev, s.user, &verdict, &code)) {
            KG_E("subscriber %llu faulted (0x%08lx) on event kind %u and was removed",
                 static_cast<unsigned long long>(s.handle), code, ev->kind);
            g_subs[i].live = false;
            recompute_mask();
            continue;
        }
        if (!cancelable) continue;
        if (verdict == KILN_GAMEPLAY_CANCEL) ev->flags |= KILN_GAMEPLAY_F_CANCELED;
        else if (verdict == KILN_GAMEPLAY_ALLOW) ev->flags &= ~KILN_GAMEPLAY_F_CANCELED;
    }
    return (ev->flags & KILN_GAMEPLAY_F_CANCELED) != 0;
}

namespace {

KilnResult KILN_CALL t_subscribe(KilnOwner owner, uint32_t mask, KilnGameplayFn f, void* user, KilnHandle* out) {
    if (!f || !mask) return KILN_ERR_INVALID_ARGUMENT;
    if (g_api->on_tick_thread && !g_api->on_tick_thread() && g_step != 0) return KILN_ERR_WRONG_THREAD;
    Sub s{g_next_handle++, owner, mask, f, user, true};
    g_subs.push_back(s);
    recompute_mask();
    if (out) *out = s.handle;
    KG_I("subscriber %llu (owner %llu) for kinds 0x%x", static_cast<unsigned long long>(s.handle),
         static_cast<unsigned long long>(owner), mask);
    return KILN_OK;
}

KilnResult KILN_CALL t_unsubscribe(KilnHandle h) {
    for (auto& s : g_subs)
        if (s.handle == h && s.live) {
            s.live = false;
            recompute_mask();
            return KILN_OK;
        }
    return KILN_ERR_NOT_FOUND;
}

bool tick_ok() { return !g_api->on_tick_thread || g_api->on_tick_thread() || g_step == 0; }

KilnResult KILN_CALL t_zone_add(KilnOwner owner, const KilnGameplayZone* z, uint32_t* out) {
    if (!tick_ok()) return KILN_ERR_WRONG_THREAD;
    return zone_add(owner, z, out);
}
KilnResult KILN_CALL t_zone_update(uint32_t id, const KilnGameplayZone* z) {
    if (!tick_ok()) return KILN_ERR_WRONG_THREAD;
    return zone_update(id, z);
}
KilnResult KILN_CALL t_zone_remove(uint32_t id) {
    if (!tick_ok()) return KILN_ERR_WRONG_THREAD;
    return zone_remove(id);
}
bool KILN_CALL t_zone_denies(KilnEntity player, KilnVec3 pos, uint32_t action, uint32_t* out_zone) {
    return zone_denies(player, pos, action, out_zone);
}
KilnResult KILN_CALL t_inject(const KilnGameplayInject* r) {
    if (!tick_ok()) return KILN_ERR_WRONG_THREAD;
    return inject(r);
}
KilnResult KILN_CALL t_watch(KilnEntity e, bool on) {
    if (!tick_ok()) return KILN_ERR_WRONG_THREAD;
    return watch(e, on);
}

KilnResult KILN_CALL t_status(KilnGameplayStatus* out) {
    // Callers built before the entity fields existed pass the shorter size; they get the fields they know.
    if (!out || out->size < offsetof(KilnGameplayStatus, entity_hooks)) return KILN_ERR_INVALID_ARGUMENT;
    KilnGameplayStatus s{};
    s.size = sizeof s;
    s.intercept = F.intercept;
    s.emit = program_active();
    s.craft_hook = F.craft;
    s.build_hooks = F.build;
    s.terraform_hook = F.terraform;
    s.spawn_hook = F.spawn;
    s.teleport = F.components;
    s.world = world() != 0;
    s.zones = static_cast<uint32_t>(zone_count());
    s.subscribers = subscriber_count();
    s.steps = S.steps;
    s.events_seen = S.events_seen;
    s.dispatched = S.dispatched;
    s.canceled = S.canceled;
    s.denied = S.denied.load();
    s.hook_records = S.hook_records;
    s.entity_hooks = entities_ready();
    s.discard = F.discard;
    discard_status(&s.discards_seen, &s.discards_canceled);
    entities_status(&s.entities_joined, &s.entities_left, &s.entities_filtered, &s.entity_categories);
    const uint32_t want = out->size;
    std::memcpy(out, &s, want < sizeof s ? want : sizeof s);
    out->size = want < sizeof s ? want : static_cast<uint32_t>(sizeof s);
    return KILN_OK;
}

KilnResult KILN_CALL t_entity_filter(uint32_t categories) { return entity_filter(categories); }

KilnResult KILN_CALL t_recipe(uint32_t id, KilnGameplayRecipe* out) {
    if (!tick_ok()) return KILN_ERR_WRONG_THREAD;
    return recipe(id, out);
}
KilnResult KILN_CALL t_workshop_recipes(uint32_t ws, uint32_t* out, uint32_t cap, uint32_t* n) {
    if (!tick_ok()) return KILN_ERR_WRONG_THREAD;
    return workshop_recipes(ws, out, cap, n);
}

KilnResult KILN_CALL t_spawn_replace(KilnOwner owner, const uint8_t* from, const uint8_t* to, float chance) {
    const KilnResult rc = hooks_spawn_replace(owner, from, to, chance);
    if (rc == KILN_OK) KG_I("spawn_replace (owner %llu): chance %.2f", static_cast<unsigned long long>(owner), chance);
    return rc;
}

KilnResult KILN_CALL t_spawn_replace_clear(KilnOwner owner, uint32_t* out) {
    const uint32_t n = hooks_spawn_replace_clear(owner);
    if (out) *out = n;
    return KILN_OK;
}

const KilnGameplayApi kTable = {
    {sizeof(KilnGameplayApi), KILN_GAMEPLAY_VERSION},
    &t_subscribe,
    &t_unsubscribe,
    &t_zone_add,
    &t_zone_update,
    &t_zone_remove,
    &t_zone_denies,
    &t_inject,
    &t_watch,
    &t_status,
    &t_recipe,
    &t_workshop_recipes,
    &t_entity_filter,
    &t_spawn_replace,
    &t_spawn_replace_clear,
};

KilnHandle g_pre = 0, g_post = 0, g_late_pre = 0;

void KILN_CALL on_late_pre(const KilnTickInfo*, void*) { g_in_sim.store(true, std::memory_order_release); }

// KILN_GAMEPLAY_TICK: subscribers with bit 0 return their mask for this step's events.
void tick_subscribers() {
    bool changed = false;
    for (size_t i = 0; i < g_subs.size(); ++i) {
        Sub s = g_subs[i];
        if (!s.live || !(s.mask & 1u)) continue;
        KilnGameplayEvent ev;
        std::memset(&ev, 0, sizeof ev);
        ev.size = sizeof ev;
        ev.kind = KILN_GAMEPLAY_TICK;
        ev.step = g_step;
        uint32_t want = 0;
        unsigned long code = 0;
        if (!seh_subscriber(s.fn, &ev, s.user, &want, &code)) {
            KG_E("subscriber %llu faulted (0x%08lx) on the tick event and was removed",
                 static_cast<unsigned long long>(s.handle), code);
            g_subs[i].live = false;
            changed = true;
            continue;
        }
        const uint32_t m = want | 1u;
        if (m != s.mask) {
            g_subs[i].mask = m;
            changed = true;
        }
    }
    if (changed) recompute_mask();
}

void KILN_CALL on_pre(const KilnTickInfo* tick, void*) {
    (void)tick;
    g_in_sim.store(false, std::memory_order_release);
    if (!world()) return;
    ++g_step;
    // The managed host subscribes its Pre when it loads mods (first server steps); a Pre subscription made now is
    // appended after it (the core refreshes its list at the next step), so it marks the end of every Pre.
    if (g_step == 50 && !g_late_pre &&
        g_api->subscribe_tick(g_owner, KILN_TICK_PRE, &on_late_pre, nullptr, &g_late_pre) != KILN_OK)
        g_late_pre = 0;
    tick_subscribers();
    g_in_step.store(false, std::memory_order_relaxed);
    zones_refresh();
    declined_check("next Pre");
    declined_reset();
    if (F.intercept && intercept_wanted()) intercept_pre();
    teleport_pre();
    discard_pre();
    g_in_step.store(true, std::memory_order_relaxed);
    if (!g_late_pre) g_in_sim.store(true, std::memory_order_release);  // until the late Pre exists
}

void KILN_CALL on_post(const KilnTickInfo* tick, void*) {
    (void)tick;
    g_in_sim.store(false, std::memory_order_release);
    g_in_step.store(false, std::memory_order_relaxed);
    if (!world()) return;
    declined_check("Post");
    hooks_post();
    entities_post();
    teleport_post();
    zones_retire();
}

bool sym(const char* name, uintptr_t* out) {
    *out = 0;
    return g_api->symbol(KilnStr{name, std::strlen(name)}, out) == KILN_OK && *out != 0;
}
bool cst(const char* name, int64_t* out) { return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }
bool feat(const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); }

void resolve() {
    H.craft_request = ecs_hash("CraftRecipeEvent");
    H.accept_offer = ecs_hash("AcceptOfferedInteractionEvent");
    H.loot = ecs_hash("LootInteractionEvent");
    H.pickup = ecs_hash("PickupEvent");
    H.accepted = ecs_hash("InteractionAcceptedEvent");
    H.declined = ecs_hash("DeclinedInteraction");
    H.teleport = ecs_hash("Teleport");
    H.teleport_count = ecs_hash("TeleportCount");

    uintptr_t a = 0;
    cst("world.storage", &L.world_storage);
    if (sym("GetComponent", &a)) {
        fn.get_component = reinterpret_cast<Fns::GetComponentFn>(a);
        F.components = L.world_storage > 0;
    }
    if (sym("GetEntityTransform", &a)) {
        fn.get_transform = reinterpret_cast<Fns::GetTransformFn>(a);
        F.transforms = true;
    }
    if (feat("gameplay.intercept") && cst("gameplay/storage.pending", &L.pending) &&
        cst("gameplay/storage.pending_count", &L.pending_count) && cst("gameplay/buffer.used", &L.buffer_used) &&
        cst("gameplay/record.size", &L.record_size)) {
        cst("gameplay/buffer.stride", &L.buffer_stride);
        F.intercept = L.world_storage > 0 && L.buffer_stride >= 0x18 && L.record_size == 6 && L.buffer_used == 0x10;
        if (!F.intercept)
            KG_W("pending-event layout unexpected (record.size %lld, buffer.used %lld, stride %lld): intercept off",
                 static_cast<long long>(L.record_size), static_cast<long long>(L.buffer_used),
                 static_cast<long long>(L.buffer_stride));
    }
    if (feat("gameplay.queues")) {
        F.queues = cst("gameplay/storage.queues", &L.queues) && cst("gameplay/events.map.cap", &L.ev_cap) &&
                   cst("gameplay/events.map.count", &L.ev_count) && cst("gameplay/events.map.bits", &L.ev_bits) &&
                   cst("gameplay/events.map.keys", &L.ev_keys) && cst("gameplay/events.map.values", &L.ev_values);
    }
    if (feat("gameplay.emit") && sym("gameplay/EmitEvent", &a)) {
        fn.emit = reinterpret_cast<Fns::EmitFn>(a);
        uintptr_t p = 0, b = 0;
        if (sym("gameplay/JobProgramId", &p) && sym("gameplay/JobEventBuffer", &b)) {
            fn.job_program = reinterpret_cast<Fns::JobProgramIdFn>(p);
            fn.job_buffer = reinterpret_cast<Fns::JobBufferFn>(b);
            F.emit = true;
        }
    }
    if (feat("gameplay.craft")) F.craft = sym("gameplay/CraftRecipe", &fn.craft);
    if (feat("gameplay.build")) {
        F.build = sym("gameplay/PlaceBlueprint", &fn.place_block) && sym("gameplay/PlaceProp", &fn.place_prop) &&
                  sym("gameplay/DismantleBlock", &fn.dismantle) && sym("gameplay/DismantleBlock2", &fn.dismantle2) &&
                  sym("gameplay/PickUpProp", &fn.pick_up) && cst("gameplay/op.player", &L.op_player) &&
                  L.op_player > 0 && L.op_player < 0x1000;
    }
    if (feat("gameplay.terraform")) {
        F.terraform = sym("gameplay/TerraformImpact", &fn.terraform) && cst("gameplay/impact.arg_offset", &L.arg_offset) &&
                      cst("gameplay/impact.arg_base", &L.arg_base) && cst("gameplay/impact.arg_stride", &L.arg_stride);
    }
    if (feat("gameplay.spawn"))
        F.spawn = sym("gameplay/QueueSpawn", &fn.queue_spawn) && sym("gameplay/RandomSpawnerSpawnRet", &fn.spawner_ret);
    if (feat("gameplay.discard"))
        F.discard = F.components && cst("gameplay/pi.item.version", &L.pi_item_version) &&
                    cst("gameplay/pi.item.type", &L.pi_item_type) && cst("gameplay/pi.item.slot", &L.pi_item_slot) &&
                    cst("gameplay/scpi.item", &L.scpi_item) && cst("gameplay/pi.transfer.version", &L.pi_tr_version) &&
                    cst("gameplay/pi.transfer.type", &L.pi_tr_type) && cst("gameplay/pi.transfer.slot", &L.pi_tr_slot) &&
                    cst("gameplay/scpi.transfer", &L.scpi_tr);
    cst("gameplay/teleport.applied", &L.tp_applied);
    cst("gameplay/teleport.find_ground", &L.tp_find_ground);
    cst("gameplay/teleport.target_entity", &L.tp_target_entity);
}

void KILN_CALL on_ready(void*) {
    const auto* players = static_cast<const KilnPlayersApi*>(
        g_api->service(KilnStr{KILN_PLAYERS_SERVICE, sizeof(KILN_PLAYERS_SERVICE) - 1}, 1));
    zones_set_players_service(players);
    if (!players) KG_W("kiln.players is unavailable: zones allow nobody by Steam ID (admins neither)");
    const auto* spawn = static_cast<const KilnSpawnApi*>(
        g_api->service(KilnStr{KILN_SPAWN_SERVICE, sizeof(KILN_SPAWN_SERVICE) - 1}, 1));
    hooks_set_spawn_service(spawn);
    entities_set_spawn_service(spawn);
    if (!spawn) KG_W("kiln.spawn is unavailable: canceling a natural spawn cannot destroy it");
    const auto* w = g_api->service(KilnStr{KILN_WORLD_SERVICE, sizeof(KILN_WORLD_SERVICE) - 1}, 1);
    program_register(w);
}

}  // namespace
}  // namespace kg

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    using namespace kg;
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{KILN_GAMEPLAY_SERVICE, sizeof(KILN_GAMEPLAY_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    info->on_ready = &on_ready;
    resolve();
    recipes_init();
    hooks_install();
    const bool entity_hooks = entities_install();
    if (api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &g_pre) != KILN_OK ||
        api->subscribe_tick(owner, KILN_TICK_POST, &on_post, nullptr, &g_post) != KILN_OK) {
        KG_E("kiln.gameplay: no tick subscription; the service is unavailable");
        return KILN_ERR_UNAVAILABLE;
    }
    KG_I("kiln.gameplay: intercept %s (queues %s), emit %s, craft hook %s, build hooks %s, terraform hook %s, "
         "spawn hook %s, entity hooks %s, discard %s, components %s, transforms %s; hashes craft %08x accept %08x loot %08x "
         "pickup %08x accepted %08x declined %08x",
         F.intercept ? "on" : "off", F.queues ? "on" : "off", F.emit ? "on" : "off", F.craft ? "on" : "off",
         F.build ? "on" : "off", F.terraform ? "on" : "off", F.spawn ? "on" : "off", entity_hooks ? "on" : "off", F.discard ? "on" : "off",
         F.components ? "on" : "off", F.transforms ? "on" : "off", H.craft_request, H.accept_offer, H.loot, H.pickup, H.accepted, H.declined);
    if (!F.intercept && !F.craft && !F.build && !F.terraform && !F.spawn && !F.components && !entity_hooks)
        return KILN_ERR_UNAVAILABLE;
    return api->register_service(owner, KilnStr{KILN_GAMEPLAY_SERVICE, sizeof(KILN_GAMEPLAY_SERVICE) - 1}, &kTable);
}
