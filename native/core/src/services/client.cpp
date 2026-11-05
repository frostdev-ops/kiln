// SPDX-License-Identifier: MIT
// kiln.client: side, session state and the local player for mods; the client world's tick and task queue.
// The local player: the client world's "LocalPlayerData" singleton holds the local player's entity id at
// client.local_player.entity (EMF ENGINE_MAP 1.5: isLocalPlayer(id, lpd) = *(u32*)(lpd+4) == id; re-derived for the
// client build in docs/re/client.md). Read on the main thread at each client-world Pre and cached for any thread.
#include <atomic>

#include "../guard.h"
#include "../image.h"
#include "../log.h"
#include "../sigdb.h"
#include "../tick.h"
#include "../worldinfo.h"
#include "services/kiln_client.h"
#include "service.h"

namespace kiln {
namespace {

int64_t g_lpd_entity = -1;               // offset of the entity id in LocalPlayerData; -1 = unknown
std::atomic<uint32_t> g_local{0};        // cached local player entity
uintptr_t g_lpd_world = 0, g_lpd = 0;    // main thread
bool g_lpd_logged = false;

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
}

void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    if (ev->kind == KILN_WORLD_CLIENT && ev->event == KILN_WORLD_STOPPING) {
        g_local.store(0, std::memory_order_release);
        g_lpd_world = g_lpd = 0;  // main thread: the client world is destroyed there
    }
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
};

bool init() {
    if (!tick::installed()) return false;
    if (image().side != KILN_SIDE_CLIENT) return true;  // state() still answers: dedicated server
    KilnHandle h = 0;
    tick::subscribe_world(0, &on_world, nullptr, &h);
    if (sigdb::feature("client.local_player") && sigdb::feature("singletons") &&
        sigdb::constant("client.local_player.entity", &g_lpd_entity)) {
        tick::subscribe_world_tick(0, KILN_WORLD_CLIENT, KILN_TICK_PRE, &on_client_pre, nullptr, &h);
    } else {
        g_lpd_entity = -1;
        KLOG_W("client: client.local_player unavailable on this build; local_player stays 0");
    }
    return true;
}

}  // namespace
}  // namespace kiln

KILN_SERVICE(client, KILN_CLIENT_SERVICE, KILN_CLIENT_VERSION, &kiln::kTable, &kiln::init);
