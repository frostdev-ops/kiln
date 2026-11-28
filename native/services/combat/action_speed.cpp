// SPDX-License-Identifier: MIT
// kiln.combat.speed (native/core/abi/services/kiln_action_speed.h; Kiln.ActionSpeed in Kiln.Combat): per-player speed of
// attacks and actions. Published by kiln-combat.dll next to kiln.combat, kept apart from the hit and fall code.
//
// Mechanism (docs/re/action-speed.md):
//  - four mid links through kiln.hooks, owned by kiln-combat: the sequence clock (CATT site 01 client / 02 server), the
//    graph binding (06), the graph evaluator's entry (09) and the bow parameters (10);
//  - kiln.world wraps (BEFORE and AFTER stages) on the programs those sites run in, in the server world, the client's
//    prediction storage and the client world: they mark the thread with the run (authority, prediction, presentation),
//    the program and the step, so a hook knows where it is. A hook outside a marked run does nothing;
//  - speed_core.h decides, with the effective rates Kiln.ActionSpeed set, the classified items (kiln.items' catalog,
//    or on a guest the server's list) and kiln.client's local player.
// Nothing is hooked while every rate is 1: the first table with another rate installs the links and wraps (once).
#include <windows.h>

#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>

#include "kiln_api.h"
#include "kiln_hook.hpp"
#include "speed_core.h"
#include "services/kiln_action_speed.h"
#include "services/kiln_client.h"
#include "services/kiln_items.h"
#include "services/kiln_types.h"
#include "services/kiln_world.h"

namespace kc::speed {
void boot(const KilnApi* api, KilnOwner owner);
void on_ready();
}  // namespace kc::speed

namespace {
using namespace kc::speed;

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
const KilnWorldApi* g_world = nullptr;
const KilnClientApi* g_client = nullptr;
const KilnItemsApi* g_items_api = nullptr;
const KilnTypesApi* g_types = nullptr;

void logf(int32_t level, const char* fmt, ...) {
    char buf[768];
    va_list ap;
    va_start(ap, fmt);
    int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (static_cast<size_t>(n) >= sizeof buf) n = sizeof buf - 1;
    g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(n)});
}
#define LOGW(...) logf(KILN_LOG_WARN, "speed: " __VA_ARGS__)
#define LOGI(...) logf(KILN_LOG_INFO, "speed: " __VA_ARGS__)

// ---- state ----
RateTable g_rates;
ActionRecords g_records;
RangedTypes g_ranged;
Bindings g_bindings;
ItemKinds g_item_kinds;
Counters g_counters;
Layout g_layout;
bool g_local_catalog = false;  // kiln.items' catalog classifies here (a server or a host): a guest's list is ignored

using CurrentEntityFn = uint32_t*(__fastcall*)(void* accessor, uint32_t* out);
CurrentEntityFn g_current_entity = nullptr;
uintptr_t g_seq_site = 0, g_bind_site = 0, g_eval_site = 0, g_bow_site = 0;
uint32_t g_parts = 0;
std::atomic<uint32_t> g_local_player{0};
std::atomic<bool> g_active{false};
std::atomic<uint32_t> g_wraps_bits{0};
std::mutex g_mu;  // activation, wraps, detail
std::string g_detail;
kiln::MidHook2 g_seq_hook, g_bind_hook, g_eval_hook, g_bow_hook;
bool g_seq_on = false, g_graph_on = false, g_bow_on = false;

thread_local Mark t_mark;

uint32_t entity_of(uintptr_t accessor) {
    if (!accessor || !g_current_entity) return 0;
    uint32_t out = 0;
    const uint32_t* p = g_current_entity(reinterpret_cast<void*>(accessor), &out);
    return p ? *p : 0;
}

Env env_now() {
    Env e;
    e.rates = &g_rates, e.records = &g_records, e.ranged = &g_ranged, e.bindings = &g_bindings, e.items = &g_item_kinds;
    e.entity_of = &entity_of;
    e.local_player = g_local_player.load(std::memory_order_relaxed);
    e.mark = t_mark;
    e.now_ms = static_cast<uint32_t>(GetTickCount64());
    return e;
}

void hk_seq(KilnRegisters& r) {
    if (!g_rates.scaling()) return;
    seq_clock(r, g_layout, env_now(), g_counters);
}
void hk_bind(KilnRegisters& r) {
    if (!g_rates.scaling()) return;
    graph_bind(r, g_layout, env_now());
}
void hk_eval(KilnRegisters& r) {
    if (!g_rates.scaling()) return;
    graph_eval(r, env_now(), g_counters);
}
void hk_bow(KilnRegisters& r) {
    if (!g_rates.scaling()) return;
    bow_timing(r, g_layout, env_now(), g_counters);
}

// ---- wraps: the runs ----
struct WrapSpec {
    uint32_t world_kind;   // the wrap's kind (PREDICTION wraps take the CLIENT directory's index)
    const char* program;
    uint8_t scope, prog;
    KilnHandle before = 0, after = 0;
    bool refused = false;  // permanently (logged once)
};
WrapSpec g_wraps[] = {
    {KILN_WORLD_SERVER, "actor_simulation_start", kAuthority, kProgSim},
    {KILN_WORLD_SERVER, "server_update_animation_graph_2", kAuthority, kProgGraph},
    {KILN_WORLD_SERVER, "server_player_ranged_weapon", kAuthority, kProgRanged},
    {KILN_WORLD_PREDICTION, "actor_simulation_start", kPrediction, kProgSim},
    {KILN_WORLD_PREDICTION, "server_update_animation_graph_2", kPrediction, kProgGraph},
    {KILN_WORLD_PREDICTION, "server_player_ranged_weapon", kPrediction, kProgRanged},
    {KILN_WORLD_CLIENT, "actor_client_play", kPresentation, kProgSim},
    {KILN_WORLD_CLIENT, "client_update_animation_graph_2", kPresentation, kProgGraph},
};

void KILN_CALL stage_before(KilnGameSlice* slice, void* user) {
    const auto* w = static_cast<const WrapSpec*>(user);
    t_mark = Mark{w->scope, w->prog, static_cast<uint32_t>(slice ? slice->step : 0)};
}
void KILN_CALL stage_after(KilnGameSlice*, void*) { t_mark = Mark{}; }

bool wraps_api() {
    return g_world && g_world->header.size >= offsetof(KilnWorldApi, game_wrap_info) + sizeof(void*) &&
           g_world->game_program_find && g_world->game_program_wrap && g_world->game_wrap_info;
}

bool wanted(const WrapSpec& w) {
    const bool client = g_api->side() == KILN_SIDE_CLIENT;
    if (w.world_kind != KILN_WORLD_SERVER && !client) return false;
    if (w.world_kind == KILN_WORLD_CLIENT && !(g_parts & KILN_SPEED_PRESENTATION)) return false;
    if (w.prog == kProgRanged) return g_bow_on;
    return g_seq_on;
}

// Applies what can be applied for the world kinds whose directory is there now (any world thread outside programs).
void try_wraps() {
    if (!g_active.load() || !wraps_api()) return;
    std::lock_guard lock(g_mu);
    for (WrapSpec& w : g_wraps) {
        if (w.refused || (w.before && w.after) || !wanted(w)) continue;
        const uint32_t dir = w.world_kind == KILN_WORLD_PREDICTION ? KILN_WORLD_CLIENT : w.world_kind;
        const int32_t index = g_world->game_program_find(dir, KilnStr{w.program, std::strlen(w.program)});
        if (index < 0) continue;  // no live directory of that kind yet: the next Pre tries again
        // the BEFORE stage first in its chain, the AFTER stage last: the mark spans every other stage and the update
        KilnResult rc = w.before ? KILN_OK
                                 : g_world->game_program_wrap(g_owner, w.world_kind, index, KILN_WRAP_BEFORE, 1000000, &stage_before, &w, &w.before);
        if (rc == KILN_OK && !w.after)
            rc = g_world->game_program_wrap(g_owner, w.world_kind, index, KILN_WRAP_AFTER, 1000000, &stage_after, &w, &w.after);
        if (rc != KILN_OK) {
            w.refused = true;
            const char* kind = w.world_kind == KILN_WORLD_SERVER ? "server" : w.world_kind == KILN_WORLD_CLIENT ? "client" : "prediction";
            LOGW("kiln.world refused the %s wrap of %s (%d): %s runs of it are not scaled", kind, w.program, rc,
                 w.world_kind == KILN_WORLD_PREDICTION ? "the local player's predicted" : kind);
            g_detail += std::string("the ") + kind + " wrap of " + w.program + " was refused; ";
        }
    }
}

// Which world kinds have every wanted wrap active (game_wrap_info), for status().
uint32_t wrap_bits() {
    if (!wraps_api()) return 0;
    std::lock_guard lock(g_mu);
    uint32_t ok = 0x7, any = 0;
    for (const WrapSpec& w : g_wraps) {
        if (!wanted(w)) continue;
        any |= 1u << w.world_kind;
        bool on = w.before && w.after;
        for (KilnHandle h : {w.before, w.after}) {
            if (!on) break;
            KilnGameWrapInfo info{};
            info.size = sizeof info;
            on = g_world->game_wrap_info(h, &info) == KILN_OK && info.state == KILN_WRAP_ACTIVE && info.disabled == KILN_WRAP_OK;
        }
        if (!on) ok &= ~(1u << w.world_kind);
    }
    return ok & any;
}

// ---- activation (the first table with a rate other than 1) ----
template <class Fn>
bool install(kiln::MidHook2& h, uintptr_t at, Fn fn, const char* label) {
    const KilnHooksApi* hv = kiln::hooks_v2(g_api);
    const bool checked = hv && hv->header.size >= offsetof(KilnHooksApi, mid2_checked) + sizeof(void*) && hv->mid2_checked;
    const bool ok = checked ? h.install_checked(g_api, g_owner, at, fn, label) : h.install(g_api, g_owner, at, fn, label);
    if (!ok) LOGW("mid link %s at %p failed (%s)", label, reinterpret_cast<void*>(at), h.error());
    return ok;
}

KilnResult activate() {
    {
        std::lock_guard lock(g_mu);
        if (g_active.load()) return KILN_OK;
        if (!(g_parts & (KILN_SPEED_SEQUENCE | KILN_SPEED_BOW)) || !(g_parts & KILN_SPEED_WRAPS)) return KILN_ERR_UNAVAILABLE;
        if (g_parts & KILN_SPEED_SEQUENCE) {
            g_seq_on = install(g_seq_hook, g_seq_site, &hk_seq, "speed/SeqClock");
            if (g_seq_on && (g_parts & KILN_SPEED_GRAPH)) {
                g_graph_on = install(g_bind_hook, g_bind_site, &hk_bind, "speed/GraphBind") &&
                             install(g_eval_hook, g_eval_site, &hk_eval, "speed/GraphEval");
                if (!g_graph_on) g_bind_hook.revoke(), g_eval_hook.revoke();
            }
        }
        if (g_parts & KILN_SPEED_BOW) g_bow_on = install(g_bow_hook, g_bow_site, &hk_bow, "speed/BowTiming");
        if (!g_seq_on) g_detail += "the sequence clock link could not be installed; ";
        if ((g_parts & KILN_SPEED_GRAPH) && !g_graph_on) g_detail += "the graph links could not be installed; ";
        if ((g_parts & KILN_SPEED_BOW) && !g_bow_on) g_detail += "the bow link could not be installed; ";
        g_active.store(true);
        LOGI("active: sequence clock %s, graph %s, bow timing %s (links owned by kiln-combat; runs marked by kiln.world wraps)",
             g_seq_on ? "on" : "off", g_graph_on ? "on" : "off", g_bow_on ? "on" : "off");
    }
    try_wraps();
    return g_seq_on || g_bow_on ? KILN_OK : KILN_ERR_UNAVAILABLE;
}

// ---- items: kiln.items' catalog where it is (a server or a host) ----
void build_items() {
    if (g_local_catalog || !g_items_api || !g_items_api->item_count || !g_items_api->item_at) return;
    const uint32_t n = g_items_api->item_count();
    if (!n) return;  // not read yet
    std::vector<KilnActionItemKind> list;
    for (uint32_t i = 0; i < n; ++i) {
        KilnItemInfo info{};
        info.size = sizeof info;
        if (g_items_api->item_at(i, &info) != KILN_OK) continue;
        const int kind = item_kind_of_slot(info.equip_slot);
        if (kind != kNone) list.push_back(KilnActionItemKind{info.id, static_cast<uint8_t>(kind), {}});
    }
    if (list.size() > ItemKinds::kMax) list.resize(ItemKinds::kMax);
    if (g_item_kinds.set(list.data(), static_cast<uint32_t>(list.size()))) {
        g_local_catalog = true;
        LOGI("%zu of %u items classified (melee weapons, tools, ranged weapons) from kiln.items' catalog", list.size(), n);
    }
}

void refresh_local_player() {
    if (!g_client || !g_client->state) return;
    KilnClientState s{};
    s.size = sizeof s;
    if (g_client->state(&s) == KILN_OK) g_local_player.store(s.local_player, std::memory_order_relaxed);
}

void KILN_CALL on_server_pre(const KilnTickInfo* tick, void*) {
    refresh_local_player();
    if (!g_local_catalog && (tick->step & 63) == 1) build_items();
    if (g_active.load(std::memory_order_relaxed) && (tick->step & 15) == 3) try_wraps();
}
void KILN_CALL on_client_pre(const KilnTickInfo* tick, void*) {
    refresh_local_player();
    if (g_active.load(std::memory_order_relaxed) && (tick->step & 15) == 5) try_wraps();
}

// ---- reflection: the ActionSequence fields the routes read ----
bool field_path(const char* type, const char* path, int64_t* out) {
    if (!g_types || !g_types->type_by_name || !g_types->type_info || !g_types->type_field) return false;
    const KilnType* t = g_types->type_by_name(KilnStr{type, std::strlen(type)});
    int64_t base = 0;
    std::string_view rest(path);
    while (t) {
        const size_t dot = rest.find('.');
        const std::string_view head = rest.substr(0, dot);
        bool found = false;
        for (const KilnType* s = t; s && !found;) {
            KilnTypeInfo info{};
            info.size = sizeof info;
            if (g_types->type_info(s, &info) != KILN_OK) return false;
            for (uint32_t i = 0; i < info.field_count && !found; ++i) {
                KilnTypeField f{};
                if (g_types->type_field(s, i, &f) != KILN_OK) continue;
                if (std::string_view(f.name.ptr, f.name.len) == head) {
                    base += static_cast<int64_t>(f.offset);
                    t = f.type;
                    found = true;
                }
            }
            if (!found) s = info.inner;  // a base class
        }
        if (!found) return false;
        if (dot == std::string_view::npos) {
            *out = base;
            return true;
        }
        rest = rest.substr(dot + 1);
        // through typedefs to the struct
        for (int i = 0; i < 4 && t; ++i) {
            KilnTypeInfo info{};
            info.size = sizeof info;
            if (g_types->type_info(t, &info) != KILN_OK) return false;
            if (info.kind != KILN_TYPE_TYPEDEF) break;
            t = info.inner;
        }
    }
    return false;
}

// ---- the table ----
KilnResult KILN_CALL t_set_rates(const float defaults[KILN_ACTION_KINDS], const KilnActionRate* players, uint32_t count) {
    if (g_world && g_world->header.size >= offsetof(KilnWorldApi, in_program) + sizeof(void*) && g_world->in_program &&
        g_world->in_program())
        return KILN_ERR_WRONG_THREAD;
    bool any = false;
    if (!g_rates.set(defaults, players, count, &any)) return KILN_ERR_INVALID_ARGUMENT;
    if (any && !g_active.load()) return activate();
    return KILN_OK;
}

KilnResult KILN_CALL t_status(KilnActionSpeedStatus* out) {
    if (!out || out->size < sizeof(KilnActionSpeedStatus)) return KILN_ERR_INVALID_ARGUMENT;
    std::memset(reinterpret_cast<uint8_t*>(out) + sizeof(uint32_t), 0, sizeof *out - sizeof(uint32_t));
    out->parts = g_parts | (g_item_kinds.ready() ? KILN_SPEED_ITEMS : 0u);
    out->active = g_active.load() ? 1 : 0;
    out->scaling = g_rates.scaling() ? 1 : 0;
    out->wraps = g_active.load() ? wrap_bits() : 0;
    for (int k = 0; k < kKinds; ++k) out->defaults[k] = g_rates.default_rate(k);
    out->players = g_rates.players();
    out->items = g_item_kinds.size();
    out->sequence_scaled = g_counters.seq.load();
    out->graph_scaled = g_counters.graph.load();
    out->bow_scaled = g_counters.bow.load();
    out->unmarked = g_counters.unmarked.load();
    std::lock_guard lock(g_mu);
    std::snprintf(out->detail, sizeof out->detail, "%s", g_detail.c_str());
    return KILN_OK;
}

KilnResult KILN_CALL t_item_kinds(KilnActionItemKind* out, uint32_t capacity, uint32_t* count) {
    if (!count || (capacity && !out)) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_local_catalog) return *count = 0, KILN_ERR_UNAVAILABLE;
    *count = g_item_kinds.copy(out, capacity);
    return KILN_OK;
}

KilnResult KILN_CALL t_set_item_kinds(const KilnActionItemKind* items, uint32_t count) {
    if (g_local_catalog) return KILN_OK;  // a host or server classifies by its own catalog
    return g_item_kinds.set(items, count) ? KILN_OK : KILN_ERR_INVALID_ARGUMENT;
}

const KilnActionSpeedApi kTable = {
    {sizeof(KilnActionSpeedApi), KILN_ACTION_SPEED_VERSION}, &t_set_rates, &t_status, &t_item_kinds, &t_set_item_kinds,
};

bool sym(const char* name, uintptr_t* out) { return g_api->symbol(KilnStr{name, std::strlen(name)}, out) == KILN_OK && *out; }
bool cst(const char* name, int64_t* out) { return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }
bool feat(const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); }
int64_t disp8(int64_t v) { return v >= 0x80 ? v - 0x100 : v; }

void note(const char* what) {
    LOGW("%s", what);
    g_detail += what;
    g_detail += "; ";
}

}  // namespace

namespace kc::speed {

void boot(const KilnApi* api, KilnOwner owner) {
    g_api = api;
    g_owner = owner;
    const bool client = api->size >= offsetof(KilnApi, side) + sizeof(void*) && api->side && api->side() == KILN_SIDE_CLIENT;
    uintptr_t ce = 0;
    if (sym("world/CurrentEntityId", &ce)) g_current_entity = reinterpret_cast<CurrentEntityFn>(ce);
    int64_t modrm = -1, time = -1, srv = 0, cli = 0, params = -1;
    const bool seq = feat("combat.action_speed") && g_current_entity && sym("speed/SeqClock", &g_seq_site) &&
                     sym("speed/GraphBind", &g_bind_site) && sym("speed/GraphEval", &g_eval_site) &&
                     cst("speed/seq.modrm", &modrm) && cst("speed/seq.time", &time) && cst("speed/graph.server_actor", &srv) &&
                     (modrm >> 6) == 2 && ((modrm & 7) == 6 || (modrm & 7) == 7) && time == 0x180;
    if (seq) {
        g_layout.seq_reg = static_cast<int>(modrm & 7);
        g_layout.server_actor = disp8(srv);
        g_parts |= KILN_SPEED_SEQUENCE | KILN_SPEED_GRAPH;
    } else {
        note("combat.action_speed is off on this game build (its sites did not resolve): action speed scales nothing");
    }
    if (client) {
        if (seq && feat("combat.action_speed.client") && cst("speed/graph.client_actor", &cli)) {
            g_layout.client_actor = disp8(cli);
            g_parts |= KILN_SPEED_PRESENTATION;
        } else if (seq) {
            note("combat.action_speed.client is off on this game client: the local player's swing is shown at the game's speed");
        }
    }
    if (feat("combat.bow_timing") && g_current_entity && sym("speed/BowTiming", &g_bow_site) && cst("speed/bow.params", &params) &&
        params > 0 && params < 0x1000) {
        g_layout.bow_params = params;
        g_parts |= KILN_SPEED_BOW;
    } else {
        note("combat.bow_timing is off on this game build: bow draw and fire durations keep the game's timing");
    }
    g_detail += "melee contact sampling: planned, it needs an in-game check first (Kiln samples nothing between frames in this release; hits land as the game finds them); ";
    LOGI("resolved: sequence clock %s, graph %s, bow timing %s%s", (g_parts & KILN_SPEED_SEQUENCE) ? "yes" : "NO",
         (g_parts & KILN_SPEED_GRAPH) ? "yes" : "NO", (g_parts & KILN_SPEED_BOW) ? "yes" : "NO",
         client ? ((g_parts & KILN_SPEED_PRESENTATION) ? ", presentation yes" : ", presentation NO") : "");
    KilnHandle h = 0;
    api->subscribe_tick(owner, KILN_TICK_PRE, &on_server_pre, nullptr, &h);
    if (client && api->size >= offsetof(KilnApi, subscribe_world_tick) + sizeof(void*) && api->subscribe_world_tick)
        api->subscribe_world_tick(owner, KILN_WORLD_CLIENT, KILN_TICK_PRE, &on_client_pre, nullptr, &h);
    api->register_service(owner, KilnStr{KILN_ACTION_SPEED_SERVICE, std::strlen(KILN_ACTION_SPEED_SERVICE)}, &kTable);
}

void on_ready() {
    g_world = static_cast<const KilnWorldApi*>(g_api->service(KilnStr{KILN_WORLD_SERVICE, std::strlen(KILN_WORLD_SERVICE)}, 4));
    g_client = static_cast<const KilnClientApi*>(g_api->service(KilnStr{KILN_CLIENT_SERVICE, std::strlen(KILN_CLIENT_SERVICE)}, 1));
    if (g_client && g_client->header.size < offsetof(KilnClientApi, state) + sizeof(void*)) g_client = nullptr;
    g_items_api = static_cast<const KilnItemsApi*>(g_api->service(KilnStr{KILN_ITEMS_SERVICE, std::strlen(KILN_ITEMS_SERVICE)}, 1));
    if (g_items_api && g_items_api->header.size < offsetof(KilnItemsApi, item_info) + sizeof(void*)) g_items_api = nullptr;
    g_types = static_cast<const KilnTypesApi*>(g_api->service(KilnStr{KILN_TYPES_SERVICE, std::strlen(KILN_TYPES_SERVICE)}, 1));
    if (g_types && g_types->header.size < offsetof(KilnTypesApi, type_field) + sizeof(void*)) g_types = nullptr;
    std::lock_guard lock(g_mu);
    if (wraps_api()) {
        g_parts |= KILN_SPEED_WRAPS;
        if (g_api->side() == KILN_SIDE_CLIENT) g_parts |= KILN_SPEED_PREDICTION;
    } else if (g_parts & (KILN_SPEED_SEQUENCE | KILN_SPEED_BOW)) {
        g_parts &= ~(KILN_SPEED_SEQUENCE | KILN_SPEED_GRAPH | KILN_SPEED_BOW | KILN_SPEED_PRESENTATION);
        note("kiln.world's program wraps are not there (update Kiln): action speed cannot tell its runs apart and scales nothing");
    }
    // the routes read the action's trigger and used item; their offsets come from the game's reflection when it is there
    int64_t trig = 0x18, item = 0x38, cur = 0x180, wt = 0, ch = 8, fi = 0x10;
    const bool refl = field_path("keen::actor::ActionSequence", "triggerContext.triggerType", &trig) &&
                      field_path("keen::actor::ActionSequence", "triggerContext.usedItem.itemId", &item) &&
                      field_path("keen::actor::ActionSequence", "playbackContext.currentTime", &cur);
    if (refl && cur != 0x180) {
        g_parts &= ~(KILN_SPEED_SEQUENCE | KILN_SPEED_GRAPH | KILN_SPEED_PRESENTATION);
        note("the game's ActionSequence layout does not match the clock site: action speed is off");
    }
    g_layout.seq_trigger = trig;
    g_layout.seq_item = item;
    if (field_path("keen::ranged_weapon::WeaponParameters", "weaponType", &wt) &&
        field_path("keen::ranged_weapon::WeaponParameters", "chargeDuration", &ch) &&
        field_path("keen::ranged_weapon::WeaponParameters", "fireDuration", &fi))
        g_layout.bow_type = wt, g_layout.bow_charge = ch, g_layout.bow_fire = fi;
    LOGI("routes: ActionSequence trigger +0x%llx, used item +0x%llx (%s); kiln.items %s, kiln.client %s",
         static_cast<unsigned long long>(trig), static_cast<unsigned long long>(item), refl ? "reflection" : "the reviewed build's offsets",
         g_items_api ? "found" : "MISSING", g_client ? "found" : "MISSING");
}

}  // namespace kc::speed
