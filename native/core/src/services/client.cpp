// SPDX-License-Identifier: MIT
// kiln.client: side, session state and the local player for mods; the client world's tick and task queue.
// The local player: the client world's "LocalPlayerData" singleton holds the local player's entity id at
// client.local_player.entity (EMF ENGINE_MAP 1.5: isLocalPlayer(id, lpd) = *(u32*)(lpd+4) == id; re-derived for the
// client build in docs/re/client.md). Read on the main thread at each client-world Pre and cached for any thread; it is
// also the local input selection (client_input.h).
// The building input members are client_building_input.cpp's; this file gives them the
// live facts and the world and owner ends, and their producer: the local input observation (client_input.h) as one
// kiln.hooks mid link at the sigdb point client/InputObserved, bound with the build's layout. The link goes in at the
// first submit (client_building_input.h, the lazy producer), never at the start: a game without a mod that builds never
// has it. A held client gate row (native/core/gates) would still keep it out entirely.
#include <atomic>
#include <mutex>

#include "../api.h"
#include "../gates.h"
#include "../guard.h"
#include "../image.h"
#include "../log.h"
#include "../registry.h"
#include "../sigdb.h"
#include "../tick.h"
#include "../worldinfo.h"
#include "client_building_input.h"
#include "client_input.h"
#include "hooks_chain.h"
#include "hooks_internal.h"
#include "services/kiln_client.h"
#include "service.h"

namespace kiln {
namespace {

int64_t g_lpd_entity = -1;               // offset of the entity id in LocalPlayerData; -1 = unknown
std::atomic<uint32_t> g_local{0};        // cached local player entity
uintptr_t g_lpd_world = 0, g_lpd = 0;    // main thread
bool g_lpd_logged = false;

uint32_t live_client_session() {
    KilnSessionInfo s{};
    s.size = sizeof s;
    tick::session(&s);
    const bool in_game = s.state == KILN_SESSION_HOST || s.state == KILN_SESSION_REMOTE;
    return in_game && s.worlds[KILN_WORLD_CLIENT] ? s.sessions[KILN_WORLD_CLIENT] : 0;
}

void KILN_CALL on_client_pre(const KilnTickInfo*, void*) {
    const uintptr_t world = worldinfo::world(KILN_WORLD_CLIENT);
    if (world != g_lpd_world) {
        g_lpd_world = world;
        g_lpd = world ? worldinfo::singleton(world, "LocalPlayerData") : 0;
        if (!g_lpd_logged || !g_lpd) {
            g_lpd_logged = true;
            KLOG_I("client: LocalPlayerData in client world 0x%llx: %s", static_cast<unsigned long long>(world),
                   g_lpd ? "found" : "not found");
        }
    }
    uint32_t id = 0;
    if (g_lpd && !guard::copy(&id, g_lpd + static_cast<uintptr_t>(g_lpd_entity), sizeof id)) id = 0;
    if (id != g_local.load(std::memory_order_relaxed)) {
        g_local.store(id, std::memory_order_release);
        KLOG_I("client: local player entity %u", id);
    }
    client_input::publish(id, id ? live_client_session() : 0);
}

void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    if (ev->kind == KILN_WORLD_CLIENT && ev->event == KILN_WORLD_STOPPING) {
        g_local.store(0, std::memory_order_release);
        g_lpd_world = g_lpd = 0;  // main thread: the client world is destroyed there
        client_input::publish(0, 0);
        building_input::session_ended(ev->session);
    }
}

void KILN_CALL on_owner_retiring(KilnOwner owner, void*) { building_input::owner_gone(owner); }

// ---- the building input producer: the local input observation ----
constexpr const char* kBuildingCap = "client.building_input";
constexpr const char* kObservation = "client.input_observation";
client_input::Site g_site{};

// At client/InputObserved (the local input writer, after it filled the ClientPlayerInput): rbp = the client game's input
// object, r14 = the ClientPlayerInput, ebx = its entity. Reads registers only; kiln.rules' camera check on the same
// instruction sees them unchanged.
void KILN_CALL on_input_observed(KilnRegisters* r, void*) {
    client_input::visit(r->rbp, r->r14, static_cast<KilnEntity>(r->rbx & 0xffffffffu), g_site);
}

// The layout the producer writes and the place the version is read, from client.input_observation's constants.
// nullptr when every one resolved inside the reflected component; else the reason.
const char* read_layout(building_input::Layout* l) {
    struct Want {
        const char* name;
        int64_t lo, hi;  // the value must lie in [lo, hi]
        int64_t* out;
    };
    constexpr int64_t kInput = 1536;  // sizeof(keen::ecs::ClientPlayerInput) on this build (sigdb/types)
    int64_t v[14] = {};
    const Want want[] = {
        {"client/input.world", 0, 0xff, &v[0]},       {"client/input.step", 0, 0x7ffffff0, &v[1]},
        {"client/cpi.cursor", 0, kInput - 112, &v[2]}, {"client/cpi.digital", 0, kInput - 8, &v[3]},
        {"client/cpi.create", 0, kInput - 12, &v[4]},  {"client/cpi.stock", 0, kInput - 12, &v[5]},
        {"client/pit.mainhand", 0, 63, &v[6]},         {"client/pit.mainhand_tap", 0, 63, &v[7]},
        {"client/pit.mainhand_hold", 0, 63, &v[8]},    {"client/pit.mainhand_release", 0, 63, &v[9]},
        {"client/pit.secondary", 0, 63, &v[10]},       {"client/pit.contextual", 0, 63, &v[11]},
        {"client/pit.contextual_hold", 0, 63, &v[12]}, {"client/pit.undo", 0, 63, &v[13]},
    };
    for (const Want& w : want)
        if (!sigdb::constant(w.name, w.out) || *w.out < w.lo || *w.out > w.hi)
            return "a client.input_observation layout constant did not resolve or is outside keen::ecs::ClientPlayerInput";
    g_site = {static_cast<uint32_t>(v[0]), static_cast<uint32_t>(v[1])};
    *l = {static_cast<uint32_t>(v[2]), static_cast<uint32_t>(v[3]), static_cast<uint32_t>(v[4]), static_cast<uint32_t>(v[5]),
          static_cast<uint8_t>(v[6]),  static_cast<uint8_t>(v[7]),  static_cast<uint8_t>(v[8]),  static_cast<uint8_t>(v[9]),
          static_cast<uint8_t>(v[10]), static_cast<uint8_t>(v[11]), static_cast<uint8_t>(v[12]), static_cast<uint8_t>(v[13])};
    return nullptr;
}

uintptr_t g_observation = 0;           // client/InputObserved, when the build has it (ready_observation)
const char* g_observation_why = "kiln.client has not started";  // observation_ready()'s reason while 0
building_input::Layout g_layout_ready{};  // the build's layout, read at the start

// The observation link, shared by building input and game input (client_input.h): installed once, at the first need of
// either. A checked installation: when it is the first link on the instruction (kiln.rules' camera check, at the same
// instruction, may already have placed the detour; then only kiln.hooks' cells change), the detour is written with
// every other thread stopped and checked to stand outside the replaced bytes, and refused, nothing written, when any
// thread could not be checked. Its handler reads registers only, so kiln.rules' link sees them unchanged before and after.
std::mutex g_link_mu;
client_input::Link g_link = client_input::Link::Again;  // Again: not installed (yet); under g_link_mu
bool g_link_tried = false;

// Once (observation_ready(), from whichever service starts first): whether the build has the observation point and the
// local player can be known. Reads sigdb itself, so it does not depend on the order the services start in.
std::once_flag g_observation_once;
void ready_observation() {
    uintptr_t site = 0;
    int64_t lpd = -1;
    if (!hooks_enabled()) {
        g_observation_why = "hooks are off (safe idle)";
    } else if (!sigdb::feature("client.local_player") || !sigdb::feature("singletons") ||
               !sigdb::constant("client.local_player.entity", &lpd) || lpd < 0) {
        g_observation_why = "the local player is not known on this game build (sigdb client.local_player)";
    } else if (!sigdb::feature(kObservation) || !sigdb::symbol("client/InputObserved", &site) || !site) {
        g_observation_why = "sigdb client.input_observation (the local input writer's observation point) is not resolved "
                            "on this game build";
    } else {
        int64_t world = -1, step = -1;
        if (!sigdb::constant("client/input.world", &world) || world < 0 || world > 0xff ||
            !sigdb::constant("client/input.step", &step) || step < 0 || step > 0x7ffffff0) {
            g_observation_why = "a client.input_observation constant (the client world's pointer or its step) did not "
                                "resolve";
            return;
        }
        g_site = {static_cast<uint32_t>(world), static_cast<uint32_t>(step)};  // the visit's own reads, for every consumer
        g_observation = site;
        g_observation_why = nullptr;
    }
}

}  // namespace

namespace client_input {

bool observation_ready(const char** why) {
    std::call_once(g_observation_once, &ready_observation);
    if (g_observation) return true;
    if (why) *why = g_observation_why;
    return false;
}

Link ensure_link(const char** why) {
    std::call_once(g_observation_once, &ready_observation);
    std::lock_guard lock(g_link_mu);
    if (g_link != Link::Again && g_link_tried) {
        if (g_link == Link::Failed && why) *why = "hooking the local input observation point failed (kiln.log says why)";
        return g_link;
    }
    if (!g_observation) {
        if (why) *why = g_observation_why;
        return Link::Failed;
    }
    g_link_tried = true;
    hooks::chain::Order o;
    o.label = "kiln.client/InputObserved";
    KilnHandle h = 0;
    uint32_t unchecked = 0;
    const KilnResult rc = hooks::ensure_ready()
        ? hooks::chain::install_mid(0, g_observation, o, &on_input_observed, nullptr, &h, &unchecked)
        : KILN_ERR_UNAVAILABLE;
    if (rc != KILN_OK && unchecked) {
        // nothing written: kiln.hooks could not check every thread (it names them above); a later request tries again
        KLOG_W("client: hooking the local input observation point (client/InputObserved) was refused: %u thread(s) "
               "could not be checked; a later request tries again",
               static_cast<unsigned>(unchecked));
        if (why) *why = "hooking the local input observation point was refused: threads that could not be checked "
                        "(kiln.log says which)";
        g_link = Link::Again;
        return g_link;
    }
    if (rc != KILN_OK) {
        KLOG_E("client: hooking the local input observation point (client/InputObserved) failed (%d); building input and "
               "game input stay off until the game restarts", rc);
        if (why) *why = "hooking the local input observation point failed (kiln.log says why)";
        g_link = Link::Failed;
        return g_link;
    }
    KLOG_I("client: the local input observation point is hooked (+0x%llx)",
           static_cast<unsigned long long>(g_observation - image().base));
    g_link = Link::Bound;
    return g_link;
}

}  // namespace client_input

namespace {

// The lazy building input producer's installer: the first submit's thread, under building_input's install mutex (once
// at a time; a refusal for threads that could not be checked is tried again by later submits, up to
// building_input::kInstallAttempts, any other failure is final). The observation link may already be in (game input).
building_input::Install install_producer(building_input::Layout* layout, const char** why) {
    switch (client_input::ensure_link(why)) {
        case client_input::Link::Again:
            if (!*why || !**why)
                *why = "hooking the local input observation point was refused on every attempt: threads that could not "
                       "be checked (kiln.log says which)";
            return building_input::Install::Again;
        case client_input::Link::Failed:
            return building_input::Install::Failed;
        case client_input::Link::Bound:
            break;
    }
    *layout = g_layout_ready;
    KLOG_I("client: building input bound at its first request (observation +0x%llx; cursorInput %u, digitalInput %u, "
           "createBuildingItemAction %u, buildingStockCycleAction %u; undo bit %u)",
           static_cast<unsigned long long>(g_observation - image().base), layout->cursor, layout->digital,
           layout->create, layout->stock, static_cast<unsigned>(layout->undo));
    return building_input::Install::Bound;
}

// Once, from init() on the game client: readies the lazy producer (nothing is hooked here), or leaves it unbound with
// the reason kiln.caps shows.
void install_building_input() {
    building_input::Layout layout{};
    const char* why = nullptr;  // still nullptr after the checks: ready for the first submit
    if (!hooks_enabled()) {
        why = "hooks are off (safe idle)";
    } else if (g_lpd_entity < 0) {
        why = "the local player is not known on this game build (sigdb client.local_player)";
    } else if (!g_observation) {
        building_input::unbound_reason(nullptr);  // the default reason: no observation point on this build
        KLOG_I("client: building input unavailable: sigdb %s is not resolved on this game build", kObservation);
        return;
    } else if (gates::is_held(kBuildingCap)) {
        why = "held by the client gate until its guided client checks pass (kiln.json \"preview\" lifts it)";
    } else {
        why = read_layout(&layout);
    }
    if (why) {
        building_input::unbound_reason(why);
        KLOG_I("client: building input not bound: %s", why);
        return;
    }
    g_layout_ready = layout;
    building_input::set_installer(&install_producer);
    KLOG_I("client: building input ready on this build (observation +0x%llx); hooked at the first request",
           static_cast<unsigned long long>(g_observation - image().base));
}

building_input::Env env_for(KilnOwner owner) {
    return {image().side == KILN_SIDE_CLIENT, hooks_enabled(), reg::owner_alive(owner), live_client_session(),
            &reg::owner_alive};
}

KilnResult KILN_CALL c_building_input_submit(KilnOwner owner, uint64_t expected_session, const KilnBuildingInput* input,
                                             uint32_t* request_id) {
    return building_input::submit(env_for(owner), owner, expected_session, input, request_id);
}
KilnResult KILN_CALL c_building_input_status(KilnOwner owner, uint32_t request_id, uint32_t* state) {
    return building_input::status(env_for(owner), owner, request_id, state);
}
KilnResult KILN_CALL c_building_input_cancel(KilnOwner owner, uint32_t request_id, uint32_t* cancelled) {
    return building_input::cancel(env_for(owner), owner, request_id, cancelled);
}

KilnResult KILN_CALL c_state(KilnClientState* out) {
    if (!out || out->size < sizeof(KilnClientState)) return KILN_ERR_INVALID_ARGUMENT;
    KilnSessionInfo s{};
    s.size = sizeof s;
    tick::session(&s);
    KilnClientState c{};
    c.size = sizeof c;
    c.side = s.side;
    c.session = s.state;
    c.generation = s.generation;
    c.local_player = (s.state == KILN_SESSION_HOST || s.state == KILN_SESSION_REMOTE) ? g_local.load() : 0;
    c.flags = (g_lpd_entity >= 0 ? KILN_CLIENT_LOCAL_PLAYER : 0u) | (s.lifecycle ? KILN_CLIENT_LIFECYCLE : 0u);
    c.client_session = s.sessions[KILN_WORLD_CLIENT];
    c.server_session = s.sessions[KILN_WORLD_SERVER];
    c.client_steps = s.steps[KILN_WORLD_CLIENT];
    c.server_steps = s.steps[KILN_WORLD_SERVER];
    *out = c;
    return KILN_OK;
}

KilnResult KILN_CALL c_subscribe_tick(KilnOwner owner, int32_t phase, KilnTickFn fn, void* user, KilnHandle* out) {
    if (image().side != KILN_SIDE_CLIENT) return KILN_ERR_UNAVAILABLE;
    return tick::subscribe_world_tick(owner, KILN_WORLD_CLIENT, phase, fn, user, out);
}

KilnResult KILN_CALL c_unsubscribe(KilnHandle h) { return tick::remove(h); }

KilnResult KILN_CALL c_post(KilnOwner owner, KilnTaskFn fn, void* user) {
    if (image().side != KILN_SIDE_CLIENT) return KILN_ERR_UNAVAILABLE;
    return tick::post_world(owner, KILN_WORLD_CLIENT, fn, user);
}

bool KILN_CALL c_on_client_thread() { return tick::on_world_thread(KILN_WORLD_CLIENT); }
uintptr_t KILN_CALL c_client_world() { return worldinfo::world(KILN_WORLD_CLIENT); }

const KilnClientApi kTable = {
    {sizeof(KilnClientApi), KILN_CLIENT_VERSION},
    &c_state,
    &c_subscribe_tick,
    &c_unsubscribe,
    &c_post,
    &c_on_client_thread,
    &c_client_world,
    &c_building_input_submit,
    &c_building_input_status,
    &c_building_input_cancel,
};

bool init() {
    if (!tick::installed()) return false;
    if (image().side != KILN_SIDE_CLIENT) return true;  // state() still answers: dedicated server
    KilnHandle h = 0;
    tick::subscribe_world(0, &on_world, nullptr, &h);
    reg::add_owner_retiring(0, &on_owner_retiring, nullptr, &h);  // a revoked owner's building input request ends
    if (sigdb::feature("client.local_player") && sigdb::feature("singletons") &&
        sigdb::constant("client.local_player.entity", &g_lpd_entity)) {
        tick::subscribe_world_tick(0, KILN_WORLD_CLIENT, KILN_TICK_PRE, &on_client_pre, nullptr, &h);
    } else {
        g_lpd_entity = -1;
        KLOG_W("client: client.local_player unavailable on this build; local_player stays 0");
    }
    client_input::observation_ready(nullptr);
    install_building_input();
    return true;
}

}  // namespace
}  // namespace kiln

KILN_SERVICE(client, KILN_CLIENT_SERVICE, KILN_CLIENT_VERSION, &kiln::kTable, &kiln::init);
