// SPDX-License-Identifier: MIT
// emf_probe: an EMF mod (EMF C API v26, emf_api.h only) that checks kiln-emf's EmfApi table in a lab and logs
// one "PASS <what>" / "FAIL <what>" line per check, then "emf-probe done: N PASS, M FAIL". Player-free: it uses a
// villager of the copied world (teleport, put/take items), spawns one Vukah next to it and kills it (damage ->
// EntityDiedEvent), changes and resets a world setting, asks for a save, runs its own command through
// emf/commands.txt, and hooks SimStep (chained over Kiln's own SimStep hook). Install: <game>/emf/mods/emf_probe/
// (mod.json + emf_probe.dll); tools/emf/lab-check.sh does it. Not for real servers: it changes a villager's
// position for a moment, spawns a creature and saves the world.
// v22-v26: the level curve and its refusals, change_component (ScaleComponent on a second Vukah), E-prompt offers
// (offer / offer_active / offer_remove on it, spawn_offer), and the window / picture refusals of a dedicated server.
#include <windows.h>

#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "emf/emf_api.h"

namespace {

const EmfApi* A = nullptr;
constexpr const char* kMod = "emf_probe";
int g_pass = 0, g_fail = 0;

void check(bool ok, const char* what, const std::string& detail = {}) {
    (ok ? g_pass : g_fail)++;
    A->log(kMod, "%s %s%s%s", ok ? "PASS" : "FAIL", what, detail.empty() ? "" : ": ", detail.c_str());
}
void skip(const char* what, const char* why) { A->log(kMod, "SKIP %s: %s", what, why); }

uint32_t fnv(const char* s) {
    uint32_t h = 0x811c9dc5u;
    while (*s) h = (h ^ static_cast<uint8_t>(*s++)) * 0x01000193u;
    return h;
}
uint32_t comp(const char* name) { return fnv((std::string("keen::ecs::") + name).c_str()); }

// "d5d05217-52dc-467a-9192-26b4f661d8d9" (Enemy_Vukah_Melee) in the game's (Windows) byte order
const uint8_t kVukah[16] = {0x17, 0x52, 0xd0, 0xd5, 0xdc, 0x52, 0x7a, 0x46, 0x91, 0x92, 0x26, 0xb4, 0xf6, 0x61, 0xd8, 0xd9};
constexpr uint32_t kWood = 0x2b0cf1d5u;  // Material_T1_Wood_Logs

struct Pos {
    double x, y, z;
};
bool position(EmfWorld* w, uint32_t e, Pos* out) {
    uint64_t size = 0;
    const auto* t = static_cast<const int64_t*>(A->get_component(w, comp("CurrentTransform"), e, &size));
    if (!t || size != 56) return false;
    *out = {t[0] / 4294967296.0, t[1] / 4294967296.0, t[2] / 4294967296.0};
    return true;
}

// ---- state shared by the callbacks (all on the server world's thread except where noted) ----
std::atomic<uint64_t> g_simsteps{0};
using SimStepFn = void (*)(void*, void*, void*, void*);
SimStepFn g_simstep_orig = nullptr;
void simstep_detour(void* a, void* b, void* c, void* d) {
    g_simsteps.fetch_add(1, std::memory_order_relaxed);
    g_simstep_orig(a, b, c, d);
}

int64_t g_level_target = 7;
std::atomic<int> g_loads{0}, g_saves{0}, g_setting_changes{0}, g_ws_changes{0}, g_cmd_runs{0};
std::atomic<bool> g_thread_run_ok{false}, g_thread_run_called{false};
std::atomic<uint64_t> g_prog_updates{0};
std::atomic<void*> g_prog_daytime{nullptr};
std::atomic<uint32_t> g_died_target{0};
std::atomic<bool> g_died_seen{false};
uint32_t g_hits = 0;

struct Flow {
    int phase = 0;
    uint64_t ticks = 0, phase_tick = 0, start_tick = 0;
    uint32_t villager = 0, container = 0, vukah = 0;
    Pos vpos{};
    bool tp1 = false, tp1_ok = false, tp2 = false, put = false, put_ok = false, take = false, take_ok = false;
    bool killed = false, ws_set = false, ws_checked = false, ws_reset = false, save_asked = false;
    bool done = false;
} F;

void tp_cb(EmfWorld*, const EmfTeleportResult* r, void* user) {
    const int which = static_cast<int>(reinterpret_cast<uintptr_t>(user));
    if (which == 1) {
        F.tp1 = true;
        F.tp1_ok = r->status == EMF_TELEPORT_MOVED && (r->flags & EMF_TELEPORT_LAST);
        char b[160];
        std::snprintf(b, sizeof b, "status %u flags 0x%x at %.2f %.2f %.2f, %.2f m from the target", r->status, r->flags, r->x, r->y,
                      r->z, r->distance);
        check(F.tp1_ok, "teleport: the villager moved 2 m (MOVED + LAST in one callback)", b);
    } else {
        F.tp2 = true;
        check(r->status == EMF_TELEPORT_MOVED, "teleport back to where it stood");
    }
}

void put_cb(EmfWorld* w, uint32_t entity, uint32_t item, uint32_t requested, uint32_t added, void*) {
    F.put = true;
    F.put_ok = added == requested && item == kWood && entity == F.container;
    char b[96];
    std::snprintf(b, sizeof b, "added %u of %u", added, requested);
    check(F.put_ok, "put_items: 1 wood log into a container's inventory", b);
    check(w == A->server_world(), "put_items: the result callback gets the server world");
}

void take_cb(EmfWorld*, uint32_t, uint32_t, uint32_t requested, uint32_t done, void*) {
    F.take = true;
    F.take_ok = done == requested;
    char b[96];
    std::snprintf(b, sizeof b, "took %u of %u", done, requested);
    check(F.take_ok, "take_items: the wood log back out (inventory as before)", b);
}

void died_cb(EmfWorld* w, const void* e, uint32_t size, void*) {
    uint32_t dead = 0;
    if (size >= 12) std::memcpy(&dead, static_cast<const uint8_t*>(e) + 8, 4);
    if (dead && dead == g_died_target.load()) {
        g_died_seen = true;
        check(size == 16 && w == A->server_world(), "on_event EntityDiedEvent for the probe's Vukah (16 bytes, server world)");
    }
}

void hit_cb(EmfWorld*, const void*, uint32_t, void*) { ++g_hits; }

void cmd_cb(EmfWorld* w, int argc, const char* const* argv, void*) {
    ++g_cmd_runs;
    EmfCommandSender s{};
    s.size = sizeof s;
    const bool sender = A->command_sender(&s);
    check(argc == 3 && std::strcmp(argv[0], "probe.cmd") == 0 && std::strcmp(argv[1], "alpha") == 0 &&
              std::strcmp(argv[2], "beta gamma") == 0,
          "register_command: emf/commands.txt ran probe.cmd with its arguments (quotes kept together)");
    check(sender && s.admin == 1 && s.from_chat == 0, "command_sender: the console (admin, not from chat)");
    check(w == A->server_world(), "the command runs on the server world");
    A->command_reply("probe.cmd: reply line %d", 1);
}

// emit_event / read_events round trip: a StopGuardingEvent with a marker no entity has (enemy01 compares the guarding
// entity with itself, so nobody acts on it), read back by the same program in the next step
constexpr uint32_t kMarker = 0x7FFFFF01u;
std::atomic<uint32_t> g_emitted{0}, g_echoed{0};
std::atomic<uint32_t> g_emit_size{0};
void echo_fn(void* ctx, void*) {
    A->read_events(ctx, "StopGuardingEvent", 0, [](const void* e, void*) {
        uint32_t who = 0;
        std::memcpy(&who, static_cast<const uint8_t*>(e) + 8, 4);
        if (who == kMarker) g_echoed.fetch_add(1);
    }, nullptr);
    if (g_emitted.load() < 3) {
        auto* e = static_cast<uint8_t*>(A->emit_event(ctx, "StopGuardingEvent", 0));
        if (e) {
            int64_t ts = 0;
            std::memcpy(&ts, e, 8);
            if (ts > 0) g_emit_size = 1;  // the core stamped it
            std::memcpy(e + 8, &kMarker, 4);
            g_emitted.fetch_add(1);
        }
    }
}

void prog_fn(void* ctx, void*) {
    g_prog_updates.fetch_add(1, std::memory_order_relaxed);
    if (!g_prog_daytime.load()) g_prog_daytime = A->program_singleton(ctx, "g38_daytime::Daytime");
}

void setting_cb(EmfWorld*, const char* mod, const char* key, void*) {
    if (mod && std::strcmp(mod, kMod) == 0 && key && std::strcmp(key, "level") == 0) ++g_setting_changes;
}

void ws_cb(const char* key, double, void*) {
    if (key && std::strcmp(key, "experienceCombatFactor") == 0) ++g_ws_changes;
}

void sync_checks(EmfWorld* w) {
    check(A->world_kind(w) == EMF_WORLD_SERVER && A->server_world() == w && A->client_world() == nullptr,
          "worlds: on_tick gets the server world; no client world on a dedicated server");
    const uint32_t t0 = A->world_tick(w);
    const int64_t ns = A->ecs_time_ns(w);
    char b[200];
    std::snprintf(b, sizeof b, "tick %u, ecs time %.1f s", t0, ns / 1e9);
    check(t0 > 0 && ns > 0 && std::fabs(ns / 1e9 - t0 / 60.0) < 1.0, "world_tick / ecs_time_ns (60 Hz clock)", b);
    void* day = A->singleton(w, "g38_daytime::Daytime");
    int64_t tod = -1;
    if (day) std::memcpy(&tod, static_cast<uint8_t*>(day) + 0x48, 8);
    std::snprintf(b, sizeof b, "Daytime %p, time of day %.2f h", day, tod / 3600e9);
    check(day && tod >= 0 && tod < 24ll * 3600 * 1000000000, "singleton g38_daytime::Daytime (the clock mod's field)", b);
    check(A->server_session() >= 1, "server_session >= 1");
    check(A->local_player() == 0, "local_player is 0 on a dedicated server");
    // entities
    struct Acc {
        uint32_t n = 0, with_transform = 0, villager = 0, container = 0, tpl_npc = 0, tc = 0, inv = 0, dyn_npc = 0;
        EmfWorld* w;
    } acc;
    acc.w = w;
    A->for_each_entity(w, [](uint32_t id, void* u) {
        auto* a = static_cast<Acc*>(u);
        ++a->n;
        uint64_t sz = 0;
        if (A->get_component(a->w, comp("CurrentTransform"), id, &sz) && sz == 56) ++a->with_transform;
        const bool tn = A->get_template_component(a->w, comp("NpcCommon"), id, nullptr) != nullptr;
        const bool dn = A->get_component(a->w, comp("DynamicActiveNpcState"), id, nullptr) != nullptr;
        const bool tc = A->get_component(a->w, comp("TeleportCount"), id, nullptr) != nullptr;
        const bool iv = A->get_component(a->w, comp("InventorySetup"), id, nullptr) != nullptr;
        a->tpl_npc += tn, a->dyn_npc += dn, a->tc += tc, a->inv += iv;
        // a villager (teleport, spawn anchor): TeleportCount and an NPC state (NpcCommon is template data); a container
        // (put / take): an InventorySetup that is no player
        if (!a->villager && id > 1023 && (tn || dn) && tc) a->villager = id;
        if (!a->container && id > 1023 && iv) a->container = id;
    }, &acc);
    A->log(kMod, "entities: template NpcCommon %u, DynamicActiveNpcState %u, TeleportCount %u, InventorySetup %u", acc.tpl_npc,
           acc.dyn_npc, acc.tc, acc.inv);
    std::snprintf(b, sizeof b, "%u entities, %u with a CurrentTransform (56 bytes), villager %u, container %u", acc.n, acc.with_transform,
                  acc.villager, acc.container);
    check(acc.n > 500 && acc.with_transform > 100, "for_each_entity + get_component(CurrentTransform)", b);
    F.villager = acc.villager;
    F.container = acc.container;
    if (F.villager) {
        uint64_t tsz = 0;
        const void* tpl = A->get_template_component(w, comp("ActiveNpcState"), F.villager, &tsz);
        std::snprintf(b, sizeof b, "%p, %llu bytes", tpl, static_cast<unsigned long long>(tsz));
        check(tpl && tsz > 0, "get_template_component(ActiveNpcState) of the villager, with the game's size", b);
        check(position(w, F.villager, &F.vpos), "the villager's position");
    }
    // items
    uint32_t count = 0;
    A->for_each_item(w, [](const EmfItemInfo*, void* u) { ++*static_cast<uint32_t*>(u); }, &count);
    const uint32_t wood = A->find_item(w, "Material_T1_Wood_Logs");
    const uint32_t wood_ci = A->find_item(w, "material_t1_wood_logs");
    EmfItemInfo info{};
    info.size = sizeof info;
    const bool have = A->item_info(w, kWood, &info);
    std::snprintf(b, sizeof b, "%u items; wood 0x%08x, max stack %u, name %s", count, wood, info.max_stack, info.name);
    check(count > 4000 && wood == kWood && wood_ci == kWood && have && std::strcmp(info.name, "Material_T1_Wood_Logs") == 0,
          "for_each_item / find_item (exact and any case) / item_info", b);
    uint64_t isz = 0;
    void* idata = A->item_data(w, kWood, &isz);
    uint16_t raw_stack = 0;
    if (idata) std::memcpy(&raw_stack, static_cast<uint8_t*>(idata) + 0x14, 2);
    std::snprintf(b, sizeof b, "%p, %llu bytes, maxStackSize at +0x14 = %u", idata, static_cast<unsigned long long>(isz), raw_stack);
    check(idata && isz == 2088 && raw_stack == info.max_stack, "item_data(wood): the game's ItemInfo, same max stack", b);
    if (idata) {
        check(A->write_memory(static_cast<uint8_t*>(idata) + 0x14, &raw_stack, 2), "write_memory into item data (same value back)");
    }
    check(!A->write_memory(A->resolve("SimStep"), "\xC3", 1), "write_memory into code is refused");
    // world settings
    EmfWorldSettingInfo wi{};
    uint32_t nset = 0;
    for (;; ++nset) {
        wi = {};
        wi.size = sizeof wi;
        if (!A->world_setting_info(nset, &wi)) break;
    }
    double loot = 0, dmin = 0;
    const bool g1 = A->world_setting_get("loot", &loot), g2 = A->world_setting_get("day_minutes", &dmin);
    std::snprintf(b, sizeof b, "%u settings; loot (alias) %.2f, day_minutes %.1f", nset, loot, dmin);
    check(nset >= 30 && g1 && g2 && dmin >= 2 && dmin <= 60, "world_setting_info / world_setting_get with short names, minutes", b);
    check(!A->world_setting_set("xp_combat", 99, 0), "world_setting_set out of range is refused");
    // screen / chat on a server
    check(A->hud_set("emf_probe.line", "probe", EMF_HUD_TOP_RIGHT, 0) && A->hud_set("emf_probe.line", "", 0, 0),
          "hud_set keeps / removes a line (nothing to draw on a server)");
    EmfWheel wh{};
    wh.size = sizeof wh;
    check(!A->wheel_open(&wh), "wheel_open is refused on a server");
    A->notify("emf-probe note %d", 42);
    check(A->chat_broadcast("[probe] a line for everyone (nobody is online)"), "chat_broadcast queues a line");
    check(!A->chat_tell(w, 1, "x"), "chat_tell to a player who is not there is refused");
    char name[64];
    check(!A->player_name(w, 1, name, sizeof name), "player_name of a missing player is false");
    check(A->spawn(nullptr, kVukah, 0, 0, 0) == 0, "spawn refuses a world that is not the callback's");
}

// ---- v22-v26 ----
constexpr uint32_t kScale = 0x2A0C9602u, kPause = 0xA64742EDu, kOfferRuntime = 0x78EE0F23u, kRange = 0x50311280u;
struct V26 {
    bool started = false, removed = false, checked_on = false, done = false;
    uint32_t prop = 0, prop2 = 0;
    std::atomic<int> scale_status{-1}, pause_status{-1}, remove_status{-1};
} V;
// user: 1 = ScaleComponent add, 2 = InteractionPause add, 3 = InteractionPause remove
void comp_cb(EmfWorld* w, uint32_t entity, uint32_t hash, uint32_t status, const char* why, void* user) {
    const int which = static_cast<int>(reinterpret_cast<uintptr_t>(user));
    (which == 1 ? V.scale_status : which == 2 ? V.pause_status : V.remove_status) = static_cast<int>(status);
    check(w != nullptr && entity == V.prop && hash == (which == 1 ? kScale : kPause),
          "change_component callback: the server world, the entity and the hash",
          std::string("status ") + std::to_string(status) + (why && *why ? std::string(", ") + why : std::string()));
}
void offer_cb(EmfWorld*, uint32_t, uint32_t, void*) {}
void ui_cb(EmfWorld*, const EmfUiEvent*, void*) {}

void v26_start(EmfWorld* w) {
    V.started = true;
    const uint32_t cap = A->player_level_cap(w);
    const uint32_t x1 = A->player_xp_required(w, 1);
    char b[96];
    std::snprintf(b, sizeof b, "cap %u, level 1 needs %u, level 2 needs %u", cap, x1, A->player_xp_required(w, 2));
    check(cap >= 10 && x1 > 0 && A->player_xp_required(w, cap + 1) == 0 && A->player_xp_required(w, 0) == 0,
          "player_level_cap / player_xp_required: the game's curve, 0 outside 1..cap", b);
    check(A->player_level_cap(nullptr) == 0, "player_level_cap without a server world is 0");
    check(!A->player_level_set(w, 1, 2, 0), "player_level_set of a player who is not in game is refused");
    check(!A->player_level_set(w, 1, cap + 1, 0), "player_level_set above the cap is refused");
    int32_t lv = 0;
    check(!A->attribute_get(w, 1, EMF_ATTR_LEVEL, &lv), "attribute_get(EMF_ATTR_LEVEL) of a missing player is false");
    check(!A->attribute_add(w, 5000, EMF_ATTR_LEVEL, 1) && !A->attribute_add(w, 5000, EMF_ATTR_XP_PENDING, -5),
          "attribute_add refuses EMF_ATTR_LEVEL (read-only) and a negative pending XP");
    // windows and pictures: a dedicated server has no overlay
    EmfUiWidget wd[1] = {};
    wd[0].type = EMF_UI_TEXT;
    wd[0].text = "hello";
    EmfWindowDef def{};
    def.size = sizeof def;
    def.id = "emf_probe.win";
    def.widgets = wd;
    def.count = 1;
    def.widget_size = sizeof(EmfUiWidget);
    def.world_kind = EMF_WORLD_SERVER;
    def.cb = &ui_cb;
    check(!A->window_open(&def) && !A->window_update(&def) && !A->window_close("emf_probe.win"),
          "window_open / window_update / window_close are refused on a dedicated server");
    check(A->image_load("pics/none.png") == 0, "image_load answers 0 on a dedicated server");
    // change_component refusals
    EmfComponentChange c{};
    c.size = sizeof c;
    c.op = EMF_COMPONENT_ADD;
    c.entity = F.vukah ? F.vukah : 5000;
    c.hash = 0x12345678u;
    check(!A->change_component(w, &c), "change_component refuses a component EMF does not list");
    c.hash = 0x1399E0BAu;  // IsTame: saved with the world, refused until checked in game
    c.flags = EMF_COMPONENT_PERSISTENT;
    check(!A->change_component(w, &c), "change_component refuses IsTame (not checked in game)");
    c.hash = kScale;
    c.flags = 0;
    c.entity = 1;
    check(!A->change_component(w, &c), "change_component refuses a player");
    float bad = 50.f;
    c.entity = F.vukah ? F.vukah : 5000;
    c.data = &bad;
    c.data_size = 4;
    check(!A->change_component(w, &c), "change_component refuses ScaleComponent 50 (0.1..10)");
    c.size = 8;
    check(!A->change_component(w, &c), "change_component refuses a too small size field");
    if (!F.villager) {
        skip("change_component / offers", "no villager in this world");
        V.done = true;
        return;
    }
    V.prop = A->spawn(w, kVukah, F.vpos.x + 14, F.vpos.y + 0.5, F.vpos.z + 4);
    check(V.prop > 1023, "a second Vukah for v23/v25", std::to_string(V.prop));
    float scale = 1.5f;
    c = {};
    c.size = sizeof c;
    c.op = EMF_COMPONENT_ADD;
    c.entity = V.prop;
    c.hash = kScale;
    c.data = &scale;
    c.data_size = 4;
    c.cb = &comp_cb;
    c.user = reinterpret_cast<void*>(1);
    // a Vukah is made with a ScaleComponent: EMF answers ALREADY and keeps its data
    check(A->change_component(w, &c), "change_component ADD ScaleComponent 1.5 queued");
    check(!A->change_component(w, &c), "a second change of the same component under way is refused");
    EmfComponentChange pz{};
    pz.size = sizeof pz;
    pz.op = EMF_COMPONENT_ADD;
    pz.entity = V.prop;
    pz.hash = kPause;  // NULL data: zeros of its size
    pz.cb = &comp_cb;
    pz.user = reinterpret_cast<void*>(2);
    check(A->change_component(w, &pz), "change_component ADD InteractionPause (zeros) queued");
    EmfOffer o{};
    o.size = sizeof o;
    o.verb = EMF_VERB_OPEN;
    check(A->offer(w, V.prop, &o, &offer_cb, nullptr), "offer(EMF_VERB_OPEN) on the second Vukah");
    check(A->offer_active(w, V.prop), "offer_active right after offer");
    check(!A->offer(w, 1, &o, &offer_cb, nullptr), "offer on a player is refused");
    o.flags = 0x80;
    check(!A->offer(w, V.prop, &o, &offer_cb, nullptr), "offer with an unknown flag is refused");
    o.flags = 0;
    o.verb = EMF_VERB_TALK;
    V.prop2 = A->spawn_offer(w, kVukah, F.vpos.x + 14, F.vpos.y + 0.5, F.vpos.z - 4, &o, &offer_cb, nullptr);
    check(V.prop2 > 1023 && A->offer_active(w, V.prop2), "spawn_offer: a Vukah with an offer", std::to_string(V.prop2));
}

// Phase 1 of v22-v26; true while it still waits.
bool v26_results(EmfWorld* w, uint64_t age) {
    if (!V.started || V.done) return false;
    uint64_t sz = 0;
    if (!V.checked_on && age >= 8) {
        V.checked_on = true;
        check(V.scale_status.load() == EMF_COMPONENT_ALREADY, "change_component ADD of a component the Vukah has: ALREADY",
              std::to_string(V.scale_status.load()));
        const void* sc = A->get_component(w, kScale, V.prop, &sz);
        float v = 0;
        if (sc && sz >= 4) std::memcpy(&v, sc, 4);
        check(sc && std::fabs(v - 1.0f) < 1e-4f, "ALREADY keeps the data (ScaleComponent stays 1.0)", std::to_string(v));
        check(V.pause_status.load() == EMF_COMPONENT_DONE && A->get_component(w, kPause, V.prop, &sz) != nullptr,
              "change_component ADD InteractionPause: DONE, and the entity has it", std::to_string(V.pause_status.load()));
        const bool ior = A->get_component(w, kOfferRuntime, V.prop, &sz) != nullptr;
        const bool rng = A->get_component(w, kRange, V.prop, &sz) != nullptr;
        check(ior && rng, "offer: InteractionOfferRuntime and CheckPlayerInRange are on the entity");
        check(A->get_component(w, kOfferRuntime, V.prop2, &sz) != nullptr, "spawn_offer: its offer components arrived");
        check(A->offer_remove(w, V.prop), "offer_remove");
        check(!A->offer_active(w, V.prop) && !A->offer_remove(w, V.prop), "after offer_remove: not active, a second remove is false");
        EmfComponentChange c{};
        c.size = sizeof c;
        c.op = EMF_COMPONENT_REMOVE;
        c.entity = V.prop;
        c.hash = kPause;
        c.cb = &comp_cb;
        c.user = reinterpret_cast<void*>(3);
        check(A->change_component(w, &c), "change_component REMOVE of what the mod added (InteractionPause)");
        c.hash = kScale;
        c.cb = nullptr;
        check(!A->change_component(w, &c), "change_component REMOVE of ScaleComponent the mod did not add is refused");
        return true;
    }
    if (V.checked_on && !V.removed && age >= 16) {
        V.removed = true;
        check(A->get_component(w, kOfferRuntime, V.prop, &sz) == nullptr, "offer_remove: the offer components are gone");
        check(V.remove_status.load() == EMF_COMPONENT_DONE && A->get_component(w, kPause, V.prop, &sz) == nullptr,
              "change_component REMOVE: DONE, InteractionPause is gone", std::to_string(V.remove_status.load()));
        check(A->offer_active(w, V.prop2), "the spawn_offer offer is still active");
        A->destroy(w, V.prop);
        A->destroy(w, V.prop2);
        V.done = true;
        return false;
    }
    return true;
}

void on_tick(EmfWorld* w, void*) {
    if (A->world_kind(w) != EMF_WORLD_SERVER || F.done) return;
    ++F.ticks;
    if (F.phase == 0) {
        if (g_loads.load() == 0) return;
        if (!F.start_tick) F.start_tick = F.ticks;
        if (F.ticks - F.start_tick < 150) return;  // ~6 s after the save loaded
        check(g_simsteps.load() > 100, "hook(SimStep) chained over Kiln's own SimStep hook: the detour runs every step",
              std::to_string(g_simsteps.load()) + " calls");
        check(g_prog_updates.load() > 50, "register_program_def: the mod program runs every step", std::to_string(g_prog_updates.load()));
        check(g_prog_daytime.load() && g_prog_daytime.load() == A->singleton(w, "g38_daytime::Daytime"),
              "program_singleton(Daytime) inside the program = singleton(Daytime)");
        check(g_thread_run_ok.load(), "run_on_world from another thread ran on the server world");
        check(g_emitted.load() == 3 && g_echoed.load() >= 3 && g_emit_size.load() == 1,
              "emit_event + read_events: 3 StopGuardingEvents sent (time-stamped) and read back the next step",
              std::to_string(g_emitted.load()) + " sent, " + std::to_string(g_echoed.load()) + " read");
        sync_checks(w);
        // async work (v22-v26 below, after the villager search)
        if (F.villager) {
            EmfTeleport t{};
            t.size = sizeof t;
            t.entity = F.villager;
            t.x = F.vpos.x + 2;
            t.y = F.vpos.y + 0.5;
            t.z = F.vpos.z;
            t.flags = EMF_TELEPORT_FIND_GROUND;
            t.cb = &tp_cb;
            t.user = reinterpret_cast<void*>(1);
            check(A->teleport(w, &t), "teleport accepted");
            if (F.container) check(A->put_items(w, F.container, kWood, 1, &put_cb, nullptr), "put_items accepted");
            else skip("put / take", "no container in this world");
            F.vukah = A->spawn(w, kVukah, F.vpos.x + 6, F.vpos.y + 0.5, F.vpos.z);
            check(F.vukah > 1023, "spawn Enemy_Vukah_Melee 6 m from the villager", std::to_string(F.vukah));
        } else {
            skip("teleport / put / take / spawn", "no villager in this world");
        }
        v26_start(w);
        F.ws_set = A->world_setting_set("xp_combat", 1.25, 0);
        check(F.ws_set, "world_setting_set xp_combat 1.25 (queued)");
        F.save_asked = A->request_save();
        check(F.save_asked, "request_save");
        const std::string lv = std::to_string(g_level_target);
        check(A->setting_set(kMod, "level", lv.c_str()), "setting_set level", lv);
        check(!A->setting_set(kMod, "level", "11"), "setting_set out of range is refused");
        check(A->setting_int(kMod, "level", -1) == g_level_target, "setting_int reads the new value");
        // the command arrives through the console file
        char exe[MAX_PATH];
        GetModuleFileNameA(nullptr, exe, MAX_PATH);
        std::string dir(exe);
        dir = dir.substr(0, dir.find_last_of("\\/"));
        if (FILE* f = std::fopen((dir + "\\emf\\commands.txt").c_str(), "ab")) {
            std::fputs("probe.cmd alpha \"beta gamma\"\n", f);
            std::fclose(f);
        }
        F.phase = 1;
        F.phase_tick = F.ticks;
        return;
    }
    // phase 1: results
    const uint64_t age = F.ticks - F.phase_tick;
    if (F.vukah && !F.killed && age >= 3) {
        int32_t hp = 0, max = 0;
        const bool h = A->attribute_get(w, F.vukah, EMF_ATTR_HEALTH, &hp) && A->attribute_get(w, F.vukah, EMF_ATTR_MAX_HEALTH, &max);
        check(h && hp > 0 && max >= hp, "attribute_get health / max health of the Vukah", std::to_string(hp) + " / " + std::to_string(max));
        g_died_target = F.vukah;
        check(A->damage(w, 0, F.vukah, hp + 1000), "damage: a killing hit (no attacker)");
        F.killed = true;
    }
    if (F.tp1_ok && !F.tp2 && age >= 1) {
        static bool asked = false;
        if (!asked) {
            asked = true;
            EmfTeleport t{};
            t.size = sizeof t;
            t.entity = F.villager;
            t.x = F.vpos.x;
            t.y = F.vpos.y;
            t.z = F.vpos.z;
            t.cb = &tp_cb;
            t.user = reinterpret_cast<void*>(2);
            A->teleport(w, &t);
        }
    }
    if (F.put_ok && !F.take) {
        static bool asked = false;
        if (!asked) {
            asked = true;
            EmfItemsRequest r{};
            r.size = sizeof r;
            r.entity = F.container;
            r.item = kWood;
            r.count = 1;
            r.cb = &take_cb;
            check(A->take_items(w, &r), "take_items accepted");
        }
    }
    if (F.ws_set && !F.ws_checked && age >= 2) {
        double v = 0;
        A->world_setting_get("experienceCombatFactor", &v);
        check(std::fabs(v - 1.25) < 1e-6 && g_ws_changes.load() >= 1, "world setting written in the next step; on_world_setting_changed fired",
              std::to_string(v));
        F.ws_checked = true;
        F.ws_reset = A->world_setting_reset("xp_combat", 0);
    }
    const bool v26_waiting = v26_results(w, age);
    const bool waiting = v26_waiting || (F.villager && (!F.tp2 || (!g_died_seen.load() && age < 250))) || (F.container && !F.take) || !F.ws_checked || g_saves.load() == 0 ||
                         g_cmd_runs.load() == 0 || g_setting_changes.load() == 0;
    if (waiting && age < 750) return;  // ~30 s
    if (F.villager) {
        // The game sends EntityDiedEvent only for actors it simulates (near a player): a lab without players gets none
        // (kiln.combat's own reader neither). The HitEvent of the killing hit proves the event path instead.
        if (!g_died_seen) skip("on_event EntityDiedEvent delivery", "the game sends it only for actors near a player (client checklist)");
        if (!F.tp1) check(false, "teleport result", "no callback in 30 s");
        if (F.container && !F.take) check(false, "put/take results", "no callback in 30 s");
        if (F.vukah) A->destroy(w, F.vukah);  // the corpse (or a survivor)
    }
    double v = 0;
    A->world_setting_get("experienceCombatFactor", &v);
    check(g_saves.load() >= 1, "on_save after request_save", std::to_string(g_saves.load()) + " save(s)");
    check(g_cmd_runs.load() == 1, "probe.cmd ran once");
    check(g_setting_changes.load() >= 1, "on_setting_changed(emf_probe) fired for level");
    char b[64];
    std::snprintf(b, sizeof b, "%u HitEvent(s) seen", g_hits);
    check(!F.vukah || g_hits >= 1, "on_event HitEvent: the killing hit's HitEvent (248 bytes on this build) reached the callback", b);
    A->log(kMod, "emf-probe done: %d PASS, %d FAIL", g_pass, g_fail);
    F.done = true;
}

}  // namespace

extern "C" __declspec(dllexport) bool emf_mod_load(const EmfApi* api) {
    if (!api || api->version < 26 || api->size < sizeof(EmfApi)) return false;
    A = api;
    check(api->version == 26 && api->size == sizeof(EmfApi) && sizeof(EmfApi) == 688, "the table is EmfApi v26 of the SDK's size",
          std::to_string(api->version) + ", " + std::to_string(api->size) + " bytes");
    bool all = true;
    for (size_t i = 0; i < (sizeof(EmfApi) - 8) / sizeof(void*); ++i)
        all = all && reinterpret_cast<void* const*>(reinterpret_cast<const uint8_t*>(api) + 8)[i] != nullptr;
    check(all, "every function slot is filled");
    EmfGameBuild b{};
    b.size = sizeof b;
    char d[200];
    const bool gb = api->game_build(&b);
    std::snprintf(d, sizeof d, "number %u, known %u, server %u, sha %.16s..., base 0x%llx, .text 0x%x+0x%x", b.number, b.known, b.server, b.sha256,
                  static_cast<unsigned long long>(b.module_base), b.text_rva, b.text_size);
    check(gb && b.known == 1 && b.server == 1 && b.number > 1280000 && b.text_size > 0, "game_build: a known server build", d);
    EmfModInfo mi{};
    mi.size = sizeof mi;
    check(api->mod_info(kMod, &mi) && mi.status == EMF_MOD_PENDING && std::strcmp(mi.source, "emf_probe\\") == 0,
          "mod_info(emf_probe) from emf_mod_load: PENDING, source emf_probe\\");
    uint32_t listed = 0;
    for (;; ++listed) {
        mi = {};
        mi.size = sizeof mi;
        if (!api->mod_info_at(listed, &mi)) break;
    }
    check(listed >= 2, "mod_info_at lists every mod", std::to_string(listed));
    // signatures and patterns
    const char* names[] = {"SimStep", "GetComponent", "SpawnWrapper", "AttrFind", "AttrAdd", "QueuePush", "NpcWorker", "WriteOther",
                           "ServerWorldSetup", "RequestServerSave", "ChatLogAdd", "InventoryCommit"};
    int resolved = 0;
    for (const char* n : names) resolved += api->resolve(n) != nullptr;
    check(resolved == 12, "resolve: 12 EMF signature names (Kiln aliases and kiln-emf's own)", std::to_string(resolved) + " of 12");
    EmfPattern p{};
    p.size = sizeof p;
    p.bytes = "40 55 53 56 41 55 48 8D AC 24 F8 FF FD FF B8 08 01 02 00 E8 ?? ?? ?? ??";  // EMF's SimStep signature
    const uintptr_t ss = api->find_pattern(&p);
    check(ss && reinterpret_cast<void*>(ss) == api->resolve("SimStep"),
          "find_pattern finds SimStep on disk although Kiln's hook patched it in memory");
    p.bytes = "48 8B 89 60 79 E1 00 E9 ?? ?? ?? ??";  // WaitWorldJobs: mov rcx,[rcx+world.jobs]; jmp WaitTaskGroup
    p.offset = 8;
    p.follow = EMF_FOLLOW_REL32;
    const uintptr_t wtg = api->find_pattern(&p);
    check(wtg != 0, "find_pattern with EMF_FOLLOW_REL32 (the jump target of WaitWorldJobs)");
    p = {};
    p.size = sizeof p;
    p.bytes = "48 89 5C 24 08";
    check(api->find_pattern(&p) == 0, "find_pattern refuses a pattern that matches more than once");
    check(api->hook("SimStep", reinterpret_cast<void*>(&simstep_detour), reinterpret_cast<void**>(&g_simstep_orig)) &&
              g_simstep_orig != nullptr,
          "hook(SimStep): installed, original set");
    // settings from mod.json
    const int64_t lvl = api->setting_int(kMod, "level", -1);
    check(lvl >= 0 && lvl <= 10 && api->setting_float(kMod, "ratio", -1) == 0.5, "settings declared from mod.json (int 0..10, float 0.5)",
          "level " + std::to_string(lvl));
    g_level_target = lvl == 7 ? 6 : 7;
    char t[16] = {};
    t[1] = 1;
    const uint32_t n = api->setting_text(kMod, "tint", t, sizeof t);
    check(n == 7 && std::strcmp(t, "#ffaa00") == 0, "setting_text color normalized to #ffaa00", t);
    char sel[16];
    api->setting_text(kMod, "mode", sel, sizeof sel);
    check(std::strcmp(sel, "b") == 0, "select setting default by value", sel);
    check(api->setting_int(kMod, "nope", 42) == 42, "an undeclared setting gives the fallback");
    EmfSettingDef sd{};
    sd.size = sizeof sd;
    sd.key = "declared";
    sd.type = EMF_SETTING_INT;
    sd.default_value = "5";
    sd.min = 0;
    sd.max = 9;
    sd.flags = EMF_SETTING_HAS_MIN | EMF_SETTING_HAS_MAX;
    check(api->setting_declare(kMod, &sd) && api->setting_int(kMod, "declared", -1) == 5, "setting_declare at run time");
    api->on_setting_changed(kMod, &setting_cb, nullptr);
    check(api->on_world_setting_changed(&ws_cb, nullptr), "on_world_setting_changed");
    // events, programs, commands, saves
    check(api->on_event(EMF_WORLD_SERVER, "EntityDiedEvent", 16, &died_cb, nullptr), "on_event(EntityDiedEvent)");
    check(api->on_event(EMF_WORLD_SERVER, "HitEvent", 0, &hit_cb, nullptr), "on_event(HitEvent, no size check)");
    check(!api->on_event(EMF_WORLD_CLIENT, "HitEvent", 0, &hit_cb, nullptr), "on_event on the client world is refused on a server");
    static const char* singletons[] = {"g38_daytime::Daytime", nullptr};
    EmfProgramDef pd{};
    pd.size = sizeof pd;
    pd.name = "emf_probe_program";
    pd.world_kind = EMF_WORLD_SERVER;
    pd.fn = &prog_fn;
    pd.singletons = singletons;
    check(api->register_program_def(&pd), "register_program_def with a singleton");
    static const char* echo_ev[] = {"StopGuardingEvent", nullptr};
    EmfProgramDef ed{};
    ed.size = sizeof ed;
    ed.name = "emf_probe_echo";
    ed.world_kind = EMF_WORLD_SERVER;
    ed.fn = &echo_fn;
    ed.events_in = echo_ev;
    ed.events_out = echo_ev;
    check(api->register_program_def(&ed), "register_program_def with one event in and out");
    check(api->register_command("probe.cmd", "<a> <b>: the probe's own command", &cmd_cb, nullptr), "register_command probe.cmd");
    check(!api->register_command("probe.cmd", "again", reinterpret_cast<EmfCommandCallback>(&prog_fn), nullptr),
          "a second probe.cmd with another callback is refused");
    api->on_load([](uint16_t slot, void*) {
        ++g_loads;
        A->log(kMod, "on_load: world slot 0x%04x", slot);
    }, nullptr);
    api->on_save([](uint16_t, void*) { ++g_saves; }, nullptr);
    api->on_tick(&on_tick, nullptr);
    // run_on_world from a thread of our own, a few seconds in
    std::thread([] {
        Sleep(8000);
        A->run_on_world(EMF_WORLD_SERVER, [](EmfWorld* w, void*) {
            g_thread_run_called = true;
            g_thread_run_ok = w && w == A->server_world() && A->world_kind(w) == EMF_WORLD_SERVER;
        }, nullptr);
    }).detach();
    A->log(kMod, "emf-probe loaded: %d PASS, %d FAIL so far; the rest runs once the world has loaded", g_pass, g_fail);
    return true;
}
