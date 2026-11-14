// SPDX-License-Identifier: MIT
// kiln.toolwear's hooks (toolwear.h has what they decide): a detour of WearItem that acts at the weapon site of
// durability_loss, a mid hook at the call of WearDelta after a projectile spawn, and a detour of WearDelta that only
// looks: it sees the debit the spawn site armed on this thread and passes every other call on untouched (Kiln's own
// costs call WearDelta in their own programs; the torch burndown has its own caller).
//
// Each hook runs inside a game program, on whichever thread the scheduler gave it. The original is called exactly once
// on every path, with the game's own arguments unless every check of toolwear.h passed. A fault in Kiln's part turns the
// hooks off for this process (they keep forwarding) and lowers items.tool_wear_debit; the game's call still happens.
#include "toolwear.h"

#include <windows.h>

#include <atomic>
#include <cstdio>
#include <cstring>

#include "engine.h"
#include "instances.h"
#include "kiln_api.h"
#include "kiln_hook.hpp"
#include "services/kiln_caps.h"
#include "services/kiln_hooks.h"
#include "services/kiln_world.h"
#include "xfer.h"

namespace kitems {
uint64_t current_step();
}

namespace kitems::toolwear {
namespace {

State g;  // static storage: the queue is large
kiln::RawHook g_item_hook, g_delta_hook;
kiln::MidHook2 g_spawn_hook;
thread_local Frame t_frame;

const KilnApi* g_api = nullptr;
uint64_t g_owner = 0;
std::atomic<bool> g_installed{false}, g_faulted{false};
char g_why[160] = "not installed";
std::atomic<uint32_t> g_thread_ids[8] = {};
std::atomic<uint32_t> g_threads{0};

KilnStr str(const char* s) { return KilnStr{s, std::strlen(s)}; }

// The distinct threads the hooks ran on (the proof that they are worker-safe needs more than one): lock-free, up to 8.
void seen_thread() {
    const uint32_t id = GetCurrentThreadId();
    for (auto& slot : g_thread_ids) {
        uint32_t have = slot.load(std::memory_order_relaxed);
        if (have == id) return;
        if (have == 0) {
            if (slot.compare_exchange_strong(have, id)) {
                g_threads.fetch_add(1, std::memory_order_relaxed);
                return;
            }
            if (have == id) return;
        }
    }
}

// Off for this process. Called from a hook that faulted: no allocation; the log line and the capability row are
// written by the next tick (status() says faulted, items.cpp's Pre reports it).
void fault() {
    g.on.store(false, std::memory_order_release);
    g_faulted.store(true, std::memory_order_release);
}

// ---- guarded steps: plain data in and out, no C++ object with a destructor in a frame that has __try ---------------

int32_t guarded_hit_before(uintptr_t caller, uint64_t dur_write, uint32_t actor, uint32_t item_entity, int32_t delta, wear::WearEnv* env,
                           HitPending* p) {
    __try {
        return hit_before(g, caller, dur_write, actor, item_entity, delta, env, p);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *p = HitPending{};
        fault();
        return delta;
    }
}

void guarded_hit_after(const HitPending* p, uint64_t dur_write, wear::WearEnv* env) {
    __try {
        hit_after(g, *p, dur_write, env);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        fault();
    }
}

void guarded_spawn_before(SpawnRegs* r, Frame* f) {
    const uintptr_t asked = r->r8;
    __try {
        spawn_before(g, *r, f);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        r->r8 = asked;
        *f = Frame{};
        fault();
    }
}

bool guarded_delta_before(Frame* f, void* durability, uint32_t notify_actor, wear::WearEnv* env) {
    __try {
        return delta_before(f, durability, notify_actor, env);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *f = Frame{};
        fault();
        return false;
    }
}

void guarded_delta_after(Frame* f, void* durability) {
    __try {
        delta_after(g, f, durability);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *f = Frame{};
        fault();
    }
}

// ---- the hooks --------------------------------------------------------------------------------------------------------

void __fastcall on_wear_item(uint64_t dur_write, uint64_t skills_read, uint32_t actor, uint32_t item_entity, int32_t delta, wear::WearEnv* env) {
    const uintptr_t caller = g_item_hook.caller();  // first: the registry knows the game's return address until the next call
    HitPending p;
    int32_t pass = delta;
    if (g.on.load(std::memory_order_acquire)) {
        seen_thread();
        pass = guarded_hit_before(caller, dur_write, actor, item_entity, delta, env, &p);
    }
    g_item_hook.original<wear::WearItemFn>()(dur_write, skills_read, actor, item_entity, pass, env);
    if (p.applied) guarded_hit_after(&p, dur_write, env);
}

void on_spawn_call(KilnRegisters& r) {
    if (!g.on.load(std::memory_order_acquire)) return;
    seen_thread();
    SpawnRegs s{r.rcx, r.rdx, r.r8, r.r9, r.r12, r.r14, r.rbp, r.rsp};
    guarded_spawn_before(&s, &t_frame);
    r.r8 = s.r8;  // the only register a rule changes: -1 or -cost, as a 32-bit value
}

void __fastcall on_wear_delta(void* durability, const void* skills, int32_t delta, uint32_t notify_actor, wear::WearEnv* env) {
    if (g.on.load(std::memory_order_acquire)) seen_thread();  // also the calls it only passes on (the torch's burndown runs on workers)
    Frame active = t_frame;
    const bool ours = active.armed && guarded_delta_before(&t_frame, durability, notify_actor, env);
    g_delta_hook.original<wear::WearDeltaFn>()(durability, skills, delta, notify_actor, env);
    if (ours) guarded_delta_after(&active, durability);
}

// ---- Ops over the item service's engine ---------------------------------------------------------------------------------

void* op_job_storage(void* job) { return E.job_storage(job); }
Slice* op_read_other(Slice* out, void* storage, uint64_t accessor, uint32_t entity) {
    PtrSize ps{};
    E.read_other(&ps, storage, accessor, entity);
    out->ptr = ps.ptr;
    out->size = ps.size;
    return out;
}
void op_iter_begin(void* ctx, uint64_t* iter, uint32_t size) { E.iter_begin(ctx, iter, size); }
uint16_t* op_component_map_find(void* map, const uint32_t* hash) { return E.component_map_find(map, hash); }
uintptr_t op_server_world() { return g_api->server_world(); }
bool op_capture(uint32_t entity, uint32_t item, uint64_t step, uint32_t* stamp, uint64_t* epoch) {
    return instances_capture(entity, item, step, stamp, epoch);
}
uint64_t op_step() { return current_step(); }
int op_max_index(void* attributes) { return wear::attribute_index(attributes, E.attr_durability_max); }

void off(uint32_t reason, const char* why) {
    std::snprintf(g_why, sizeof g_why, "%s", why);
    items_log(KILN_LOG_WARN, "tool wear: off: %s", why);
    const auto* caps = static_cast<const KilnCapsApi*>(g_api->service(str(KILN_CAPS_SERVICE), KILN_CAPS_VERSION));
    if (caps && caps->report) caps->report(g_owner, str(wear::kFeature), KILN_CAP_UNAVAILABLE, reason, str(why));
}

}  // namespace

bool install(const KilnApi* api, uint64_t owner) {
    g_api = api;
    g_owner = owner;
    const wear::Facts& f = wear::facts();
    if (!f.ok) {
        std::snprintf(g_why, sizeof g_why, "%s", f.why);  // wear::resolve said so and lowered the row
        return false;
    }
    if (!E.job_storage || !E.read_other || !E.iter_begin || !E.component_map_find || !E.attr_find || !E.instances_ok) {
        off(KILN_CAP_REASON_SYMBOL_MISSING, "the item service's reads or item instances are not available on this build");
        return false;
    }
    g.ops = Ops{};
    g.ops.job_storage = &op_job_storage;
    g.ops.read_other = &op_read_other;
    g.ops.iter_begin = &op_iter_begin;
    g.ops.component_map_find = &op_component_map_find;
    g.ops.server_world = &op_server_world;
    g.ops.capture = &op_capture;
    g.ops.step = &op_step;
    g.ops.max_index = &op_max_index;
    g.ops.world_storage = f.world_storage;
    g.ops.storage_kind = f.storage_kind;
    g.ops.registry_components = f.registry_components;
    g.ops.weapon_return = f.weapon_return;
    g.ops.spawn_request_local = f.spawn_request_local;
    g.ops.spawn_durability_local = f.spawn_durability_local;
    g.ops.spawn_request_pide = f.spawn_request_pide;
    g.ops.spawn_iter_base = f.spawn_iter_base;
    g.ops.spawn_itemstate_slot = f.spawn_itemstate_slot;
    // The observing detour first: were a later hook to fail, a WearDelta detour that only looks changes nothing.
    if (!g_delta_hook.install(api, owner, reinterpret_cast<uintptr_t>(f.wear_delta), reinterpret_cast<void*>(&on_wear_delta), "items/toolwear-delta") ||
        !g_item_hook.install(api, owner, reinterpret_cast<uintptr_t>(f.wear_item), reinterpret_cast<void*>(&on_wear_item), "items/toolwear-item") ||
        !g_spawn_hook.install<KilnRegisters>(api, owner, f.spawn_call, &on_spawn_call, "items/toolwear-spawn")) {
        g_item_hook.revoke();
        g_delta_hook.revoke();
        off(KILN_CAP_REASON_FAULTED, "a hook of the durability debit could not be placed (kiln.log has the hook registry's reason)");
        return false;
    }
    g_why[0] = 0;
    g_installed.store(true);
    g.on.store(true, std::memory_order_release);
    items_log(KILN_LOG_INFO, "tool wear: hooks in (WearItem at the weapon site, the projectile spawn's debit, WearDelta observed)");
    return true;
}

void verify_programs(const void* world_api) {
    static bool done = false;
    if (done || !g_installed.load() || !world_api) return;
    const auto* w = static_cast<const KilnWorldApi*>(world_api);
    if (w->header.size < offsetof(KilnWorldApi, game_program_info) + sizeof(void*) || !w->game_program_find || !w->game_program_info) {
        done = true;  // an older kiln.world: no directory to ask
        return;
    }
    const wear::Facts& f = wear::facts();
    const uintptr_t base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    const struct {
        const char* name;
        uintptr_t update;
    } want[] = {{"durability_loss", f.hit_update}, {"actor_spawn_entity", f.spawn_update}};
    for (const auto& p : want) {
        const int32_t index = w->game_program_find(KILN_WORLD_SERVER, str(p.name));
        if (index < 0) return;  // no directory of a server world yet: asked again in the next Pre
        KilnGameProgramInfo info{};
        info.size = sizeof info;
        if (w->game_program_info(KILN_WORLD_SERVER, index, &info) != KILN_OK) return;
        if (info.update_rva != p.update - base) {
            done = true;
            g.on.store(false, std::memory_order_release);
            char why[160];
            std::snprintf(why, sizeof why, "the game's program %s updates at +0x%llx, the debit's signature found +0x%llx", p.name,
                          static_cast<unsigned long long>(info.update_rva), static_cast<unsigned long long>(p.update - base));
            off(KILN_CAP_REASON_SYMBOL_MISSING, why);
            return;
        }
    }
    done = true;
    items_log(KILN_LOG_INFO, "tool wear: durability_loss and actor_spawn_entity are the programs the signatures found");
}

void world_reset() { g.queue.clear(); }

bool set_rule(uint32_t item, int32_t cost) { return g.rules.set(item, cost); }
int32_t rule(uint32_t item) { return g.rules.find(item); }

void tick() {
    // a fault is said once, here on the tick thread, never in the hook
    static bool told = false;
    if (g_faulted.load(std::memory_order_acquire) && !told) {
        told = true;
        off(KILN_CAP_REASON_FAULTED, "a hook of the durability debit faulted: tool wear rules are off until the game restarts, the game's own debits go on");
    }
}

size_t drain(Change* out, size_t max) {
    size_t n = g.queue.drain(out, max), kept = 0;
    const uint64_t epoch = instances_epoch();
    for (size_t i = 0; i < n; ++i)
        if (out[i].epoch == epoch && out[i].stamp != 0 && instances_peek(out[i].item_entity) == out[i].stamp) out[kept++] = out[i];
    return kept;
}

Status status() {
    Status s;
    s.on = g.on.load(std::memory_order_acquire);
    s.faulted = g_faulted.load(std::memory_order_acquire);
    std::snprintf(s.why, sizeof s.why, "%s", s.faulted ? "a hook faulted" : g_why);
    s.dropped = g.queue.dropped();
    s.hit_seen = g.hit_seen.load(std::memory_order_relaxed);
    s.hit_applied = g.hit_applied.load(std::memory_order_relaxed);
    s.spawn_seen = g.spawn_seen.load(std::memory_order_relaxed);
    s.spawn_applied = g.spawn_applied.load(std::memory_order_relaxed);
    s.threads = g_threads.load(std::memory_order_relaxed);
    return s;
}

}  // namespace kitems::toolwear
