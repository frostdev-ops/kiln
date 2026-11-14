// SPDX-License-Identifier: MIT
// kiln.creature_control v1 (kiln-creature-control.dll): the service. Publishes the table, runs the layout guard against
// kiln.types, registers the broker's world program, reports the operational row creature_control, and drives the broker
// from the server world's Pre (the transition step, at a job barrier). The game side of the broker (Game) reads bodies
// through kiln.world on the tick thread and holds the broker's AI-off layer through kiln.creatures. Process-pinned:
// a service DLL is never unloaded, so a body it changed is never left without the code that restores it.
// Contract: native/core/abi/services/kiln_creature_control.h; docs/kiln/services/creature-control.md.
#include <windows.h>

#include <algorithm>
#include <cstdarg>
#include <cstdio>
#include <cstring>

#include "internal.h"
#include "services/kiln_caps.h"
#include "services/kiln_creatures.h"
#include "services/kiln_spawn.h"
#include "services/kiln_types.h"

namespace kcc {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
Broker* g_broker = nullptr;
Layout g_layout{};

void logf(int32_t level, const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (n >= static_cast<int>(sizeof buf)) n = sizeof buf - 1;
    if (g_api) g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(n)});
}

bool on_tick() { return g_api && g_api->on_tick_thread && g_api->on_tick_thread(); }

namespace {

constexpr char kRow[] = "creature_control";

const KilnWorldApi* W = nullptr;
const KilnCreaturesApi* C = nullptr;
const KilnSpawnApi* S = nullptr;
int64_t g_world_storage = -1, g_next_id = -1;  // the engine's entity counter (sigdb: world.storage, items/storage.next_id)
const KilnCapsApi* g_caps = nullptr;
bool g_ready = false;        // on_ready ran and everything the broker needs is there
std::string g_off = "kiln.creature_control has not started";
uint64_t g_sim_ns = 0;
std::atomic<uint64_t> g_stopped{0};  // the server world session that is being destroyed (STOPPING), 0 = none
std::atomic<bool> g_program_checked{false};

uint32_t hash(const char* name) { return W ? W->component_hash(KilnStr{name, std::strlen(name)}) : 0; }
uint32_t h_actor = 0, h_input = 0, h_steering = 0, h_dyn = 0, h_player = 0;

// A guarded copy of game memory. Nothing with a destructor lives in this frame.
bool seh_read(const void* p, void* out, size_t n) {
    if (!p) return false;
    __try {
        std::memcpy(out, p, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

class LiveGame final : public Game {
public:
    BodyRead read(KilnEntity e) override {
        BodyRead r;
        if (!W || !W->exists(e)) return r;
        r.exists = true;
        uint32_t na = 0, ni = 0, ns = 0, nd = 0, np = 0;
        r.player = W->component(e, h_player, &np) != nullptr;
        const auto* a = static_cast<const uint8_t*>(W->component(e, h_actor, &na));
        const auto* in = static_cast<const uint8_t*>(W->component(e, h_input, &ni));
        r.actor = a != nullptr;
        r.input = in != nullptr;
        r.steering = W->component(e, h_steering, &ns) != nullptr;
        r.enemy_ai = W->component(e, h_dyn, &nd) != nullptr;
        const Layout& L = g_layout;
        if (a && in && na >= L.actor_state + 8 && na > L.actor_play_state && ni >= L.input_trigger_sequence + 4) {
            uint64_t state = 0;
            uint8_t play = 0;
            uint32_t seq = 0;
            if (seh_read(a + L.actor_state, &state, 8) && seh_read(a + L.actor_play_state, &play, 1) &&
                seh_read(in + L.input_trigger_sequence, &seq, 4)) {
                r.readable = true;
                r.ready = ((state >> L.bit_ready) & 1) != 0;
                r.dead = ((state >> L.bit_dead) & 1) != 0;
                r.action_active = ((play >> L.bit_active) & 1) != 0;
                r.trigger_queued = seq != 0;
            }
        }
        return r;
    }

    bool steering_bytes(KilnEntity e, std::vector<uint8_t>* out) override {
        uint32_t n = 0;
        const void* p = W ? W->component(e, h_steering, &n) : nullptr;
        if (!p || n == 0 || n > 4096) return false;
        out->resize(n);
        if (!seh_read(p, out->data(), n)) {
            out->clear();
            return false;
        }
        return true;
    }

    KilnResult ai_layer(KilnEntity e, bool off) override {
        return C ? C->ai_switch(g_owner, e, off ? 0u : 1u) : KILN_ERR_UNAVAILABLE;
    }

    int ai_enabled(KilnEntity e) override {
        uint32_t on = 0;
        if (!C || !C->ai_switch_get || C->ai_switch_get(e, &on) != KILN_OK) return -1;
        return on ? 1 : 0;
    }

    bool destroy(KilnEntity e) override { return S && S->destroy && S->destroy(e) == KILN_OK; }

    bool entity_counter(uint32_t* out) override {
        if (g_world_storage < 0 || g_next_id <= 0 || !g_api->world) return false;
        // the world's storage is embedded at world.storage; its next-id counter at items/storage.next_id (as kiln.items
        // reads it for its stamps)
        const uintptr_t world = g_api->world(KILN_WORLD_SERVER);
        if (!world) return false;
        return seh_read(reinterpret_cast<const void*>(world + static_cast<uintptr_t>(g_world_storage + g_next_id)), out, 4);
    }

    void log(int32_t level, const std::string& text) override { logf(level, "%s", text.c_str()); }
};

LiveGame g_game;

void report(bool on, uint32_t reason, const std::string& text) {
    if (!g_caps || !g_caps->report) return;
    g_caps->report(g_owner, KilnStr{kRow, sizeof kRow - 1}, on ? KILN_CAP_AVAILABLE : KILN_CAP_UNAVAILABLE,
                   on ? KILN_CAP_REASON_NONE : reason, KilnStr{text.data(), text.size()});
}

void go_off(uint32_t reason, const std::string& why) {
    g_off = why;
    if (g_broker) g_broker->set_operational(false, why);
    report(false, reason, why);
    logf(KILN_LOG_WARN, "creature_control is off: %s", why.c_str());
}

// The server world's epoch: its session number while one runs in this process (and is not being destroyed), else 0.
uint64_t current_epoch() {
    if (!g_api || g_api->size < offsetof(KilnApi, session) + sizeof(g_api->session) || !g_api->session) return 0;
    KilnSessionInfo s{};
    s.size = sizeof s;
    if (g_api->session(&s) != KILN_OK || !s.worlds[KILN_WORLD_SERVER]) return 0;
    const uint64_t epoch = s.sessions[KILN_WORLD_SERVER];
    return epoch == g_stopped.load(std::memory_order_acquire) ? 0 : epoch;
}

void KILN_CALL on_pre(const KilnTickInfo* tick, void*) {
    if (!g_broker) return;
    const uint64_t epoch = current_epoch();
    g_broker->set_world(epoch, "the server world ended");
    // once a server world runs its programs, the broker's program must be in it, or nothing can be moved
    if (g_ready && epoch && (!g_program_checked.load(std::memory_order_relaxed) || tick->step % 25 == 0)) {
        const uint32_t st = program_state();
        if (st == KILN_PROGRAM_ACTIVE) {
            if (!g_program_checked.exchange(true, std::memory_order_relaxed))
                logf(KILN_LOG_INFO, "program kiln_creature_control is active in server world %llu", static_cast<unsigned long long>(epoch));
        } else if (st != KILN_PROGRAM_PENDING) {
            // two-phase withdrawal: nothing can be moved or restored without the program; the row goes off now, every
            // bound body is quarantined (its leases lose authority at once) and retired from the next Pre on
            g_program_checked.store(true, std::memory_order_relaxed);
            g_ready = false;
            const std::string why = "the world program kiln_creature_control is not running (state " + std::to_string(st) +
                                    "): kiln.log says why";
            go_off(KILN_CAP_REASON_FAULTED, why);
            g_broker->withdraw(why);
        }
    }
    const uint32_t delta = tick ? std::min<uint32_t>(tick->clock_delta, 60u) : 0;  // 60 Hz clock; one step at most 1 s
    g_sim_ns += static_cast<uint64_t>(delta) * kSecond / 60;
    std::vector<Op> ops;
    g_broker->pre(g_sim_ns, &ops);
    program_publish(std::move(ops));
}

void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    if (!ev || ev->kind != KILN_WORLD_SERVER) return;
    // the destroying thread: only flags here; the next Pre (of the next world) ends the leases. can_drive is false from now.
    if (ev->event == KILN_WORLD_STOPPING) g_stopped.store(ev->session, std::memory_order_release);
    if (ev->event == KILN_WORLD_STARTED) g_program_checked.store(false, std::memory_order_relaxed);
}

// ---- the table ----

#define TICK_ONLY()                                         \
    do {                                                    \
        if (!g_broker) return KILN_ERR_UNAVAILABLE;         \
        if (!on_tick()) return KILN_ERR_WRONG_THREAD;       \
    } while (0)

KilnResult KILN_CALL t_bind_body(KilnOwner owner, KilnEntity entity, uint32_t kind, uint32_t retire, KilnControlBodyKey* out) {
    TICK_ONLY();
    g_broker->set_world(current_epoch(), "the server world ended");  // a stopping world binds nothing
    return g_broker->bind(owner, entity, kind, retire, out);
}
KilnResult KILN_CALL t_acquire(KilnOwner owner, const KilnControlRequest* request, KilnControlState* out) {
    TICK_ONLY();
    return g_broker->acquire(owner, request, out);
}
KilnResult KILN_CALL t_query(KilnOwner, KilnHandle lease, KilnControlState* out) {
    if (!g_broker) return KILN_ERR_UNAVAILABLE;
    return g_broker->query(lease, out);
}
KilnResult KILN_CALL t_can_drive(KilnOwner owner, KilnHandle lease, KilnControlBodyKey body, uint64_t seq, uint32_t* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    *out = 0;
    if (!g_broker) return KILN_ERR_UNAVAILABLE;
    if (body.world_epoch == g_stopped.load(std::memory_order_acquire)) return KILN_OK;  // that world is being destroyed
    *out = g_broker->can_drive(owner, lease, body, seq);
    return KILN_OK;
}
KilnResult KILN_CALL t_release(KilnOwner owner, KilnHandle lease) {
    TICK_ONLY();
    return g_broker->release(owner, lease);
}
KilnResult KILN_CALL t_notices(KilnOwner owner, KilnControlState* out, uint32_t capacity, uint32_t* written, uint32_t* resync) {
    TICK_ONLY();
    return g_broker->notices(owner, out, capacity, written, resync);
}
KilnResult KILN_CALL t_quarantine(KilnControlBodyKey body, uint32_t cause) {
    if (!g_broker) return KILN_ERR_UNAVAILABLE;
    return g_broker->quarantine(body, cause);  // any thread: atomics only
}
KilnResult KILN_CALL t_body_retired(KilnOwner owner, KilnControlBodyKey body) {
    TICK_ONLY();
    return g_broker->body_retired(owner, body);
}
uint32_t KILN_CALL t_root_policy(uint64_t epoch, KilnEntity root) { return g_broker ? g_broker->root_policy(epoch, root) : 0; }
KilnResult KILN_CALL t_next_request(KilnOwner owner, uint64_t* out) {
    TICK_ONLY();
    return g_broker->next_request(owner, out);
}
KilnResult KILN_CALL t_release_body(KilnOwner owner, KilnControlBodyKey body) {
    TICK_ONLY();
    return g_broker->release_body(owner, body);
}

const KilnCreatureControlApi kTable = {
    {sizeof(KilnCreatureControlApi), KILN_CREATURE_CONTROL_VERSION},
    &t_bind_body, &t_acquire, &t_query, &t_can_drive, &t_release, &t_notices,
    &t_quarantine, &t_body_retired, &t_root_policy, &t_next_request, &t_release_body,
};

void KILN_CALL on_ready(void*) {
    g_caps = static_cast<const KilnCapsApi*>(g_api->service(KilnStr{KILN_CAPS_SERVICE, sizeof(KILN_CAPS_SERVICE) - 1}, 1));
    // 1. the layout guard: every path the broker reads or writes, by the game's own reflection
    const auto* types = static_cast<const KilnTypesApi*>(g_api->service(KilnStr{KILN_TYPES_SERVICE, sizeof(KILN_TYPES_SERVICE) - 1}, 1));
    const GuardResult guard = run_guard(types, &g_layout);
    logf(guard.broker_ok ? KILN_LOG_INFO : KILN_LOG_WARN, "guard: %u of %u field paths matched (broker %s, footprint %s): %s",
         guard.matched, guard.total, guard.broker_ok ? "ok" : "MISMATCH", guard.footprint_ok ? "ok" : "MISMATCH",
         guard.line.c_str());
    if (!guard.broker_ok) return go_off(KILN_CAP_REASON_SYMBOL_MISSING, "layout guard: " + guard.why);
    SteeringLayout sl;
    sl.size = g_layout.steering_size;
    sl.query_id = g_layout.steering_query_id;
    sl.desired_velocity = g_layout.steering_desired_velocity;
    sl.velocity = g_layout.steering_velocity;
    g_broker->set_layout(sl);
    // 2. kiln.world: entity reads, the program, write_other
    W = static_cast<const KilnWorldApi*>(g_api->service(KilnStr{KILN_WORLD_SERVICE, sizeof(KILN_WORLD_SERVICE) - 1}, 1));
    if (!W || W->header.size < offsetof(KilnWorldApi, capabilities) + sizeof(W->capabilities) || !W->capabilities ||
        !(W->capabilities() & KILN_WORLD_CAP_WRITE_OTHER)) {
        W = nullptr;
        return go_off(KILN_CAP_REASON_SERVICE_MISSING, "needs kiln.world with programs and write_other");
    }
    KilnWorldStatus ws{};
    ws.size = sizeof ws;
    if (W->status(&ws) != KILN_OK || !ws.programs)
        return go_off(KILN_CAP_REASON_SERVICE_MISSING, "kiln.world cannot register programs on this build");
    h_actor = hash("Actor");
    h_input = hash("ActorInput");
    h_steering = hash("SteeringAgent");
    h_dyn = hash("DynamicEnemy01BehaviorState");
    h_player = hash("PlayerInput");
    // 3. kiln.creatures: the broker's own AI-off layer
    C = static_cast<const KilnCreaturesApi*>(g_api->service(KilnStr{KILN_CREATURES_SERVICE, sizeof(KILN_CREATURES_SERVICE) - 1}, 1));
    if (!C || C->header.size < offsetof(KilnCreaturesApi, ai_switch) + sizeof(C->ai_switch) || !C->ai_switch) {
        C = nullptr;
        return go_off(KILN_CAP_REASON_SERVICE_MISSING, "needs kiln.creatures with ai_switch");
    }
    // optional: retiring a body the broker could not restore (kiln.spawn), the entity counter's identity check (sigdb)
    S = static_cast<const KilnSpawnApi*>(g_api->service(KilnStr{KILN_SPAWN_SERVICE, sizeof(KILN_SPAWN_SERVICE) - 1}, 1));
    if (S && S->header.size < offsetof(KilnSpawnApi, destroy) + sizeof(S->destroy)) S = nullptr;
    if (!S) logf(KILN_LOG_WARN, "kiln.spawn is not here: a body the broker cannot restore is quarantined but not destroyed");
    int64_t wst = -1, nid = -1;
    if (g_api->constant(KilnStr{"world.storage", 13}, &wst) == KILN_OK && g_api->constant(KilnStr{"items/storage.next_id", 21}, &nid) == KILN_OK &&
        wst >= 0 && nid > 0 && nid < 0x10000 && (nid & 3) == 0) {
        g_world_storage = wst;
        g_next_id = nid;
    } else {
        logf(KILN_LOG_INFO, "the entity counter is not resolved on this build: the identity check (a counter that goes down) is off");
    }
    inject_init();
    // 4. the program
    std::string why;
    if (!program_register(W, &why)) return go_off(KILN_CAP_REASON_FAULTED, why);
    g_ready = true;
    g_off.clear();
    g_broker->set_operational(true, "");
    report(true, KILN_CAP_REASON_NONE, "");
    logf(KILN_LOG_INFO, "kiln.creature_control v%u ready: %u bodies, %u leases, %u tombstones per world at most",
         KILN_CREATURE_CONTROL_VERSION, KILN_CONTROL_MAX_BODIES, KILN_CONTROL_MAX_LEASES, KILN_CONTROL_MAX_TOMBSTONES);
}

}  // namespace
}  // namespace kcc

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    using namespace kcc;
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{KILN_CREATURE_CONTROL_SERVICE, sizeof(KILN_CREATURE_CONTROL_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    info->on_ready = &on_ready;
    static Broker broker(&g_game);  // process lifetime: the DLL stays loaded until the process ends
    g_broker = &broker;
    KilnHandle h = 0;
    if (KilnResult rc = api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h); rc != KILN_OK) return rc;
    if (api->feature && api->feature(KilnStr{"world.lifecycle", 15}) && api->subscribe_world)
        api->subscribe_world(owner, &on_world, nullptr, &h);
    return api->register_service(owner, KilnStr{KILN_CREATURE_CONTROL_SERVICE, sizeof(KILN_CREATURE_CONTROL_SERVICE) - 1}, &kTable);
}
