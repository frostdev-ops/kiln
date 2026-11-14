// SPDX-License-Identifier: MIT
// kiln.repl internals shared by its source files (not an ABI).
#pragma once
#include <atomic>
#include <cstdint>
#include <string>

#include "guard.h"
#include "kiln_api.h"
#include "layout.h"
#include "services/kiln_repl.h"

namespace repl {

extern const KilnApi* g_api;
extern KilnOwner g_owner;

void logf(int level, const char* fmt, ...);

// Layout values from sigdb (-1 = unresolved).
struct Offsets {
    int64_t world_objects = -1;   // repl/world.objects: World -> replication objects (+8: their count)
    int64_t obj_stride = -1;      // repl/obj.stride
    int64_t obj_client_ctx = -1;  // repl/obj.client_ctx
    int64_t obj_pred_ctx = -1;    // repl/obj.pred_ctx
    int64_t obj_count = -1;       // repl/obj.count (17)
    int64_t world_slot_mask = -1; // repl/world.slot_mask: u16 of player slots being replicated
    int64_t world_tick = -1;      // repl/world.tick
    int64_t ctx_heap = -1;        // repl/ctx.heap
    int64_t gs_machines = -1;     // net/gs.machines: GameServer -> 17 machine slots
    int64_t world_storage = -1;   // world.storage: *(world + it) = the component registry
    int64_t reg_template_map = -1;  // spawn/registry.template_map (cap +0x10, values +0x40)
    int64_t reg_replicated = -1;  // world/registry.replicated (the registry's replication bits), when it resolves
};
extern Offsets L;

// Game functions (absolute addresses; 0 = unresolved).
struct Functions {
    uintptr_t send_step = 0, send_callback = 0, entity_writer = 0, delta_writer = 0, state_index = 0, ack_receive = 0,
              ecs_reset = 0, prepare = 0, reset_machine = 0, relevance_driver = 0, relevance_step = 0, ack_process = 0,
              record_sent = 0, template_state = 0, decode = 0;
};
extern Functions F;

// The server world and the GameServer as the last frame saw them (0 = none). Set on the tick thread (world) and in the
// send step (gs); read anywhere.
extern std::atomic<uintptr_t> g_world;
extern std::atomic<uintptr_t> g_gs;

// One component of the registry of the world kiln.repl last read (built in its first Pre).
struct ComponentFacts {
    uint32_t hash = 0;
    uint16_t size = 0;
    uint8_t usage = 0;
    uint32_t flags = 0;   // KILN_REPL_COMPONENT_*
};
// The registry of the current world: facts by registry index, its address, and its component count. Tick thread
// (written there in Pre; readers off the tick thread take component_info's lock).
struct Registry {
    uintptr_t address = 0;
    uint32_t count = 0;
    ComponentFacts at[kMaxComponents];
    std::string names[kMaxComponents];
};
const Registry* registry();  // tick thread; nullptr until a world's registry was read

// Guarded copies of game memory (false on a bad address).
bool read_mem(uintptr_t address, void* out, size_t n);
template <class T>
inline T read(uintptr_t address, T fallback = T{}) {
    T v{};
    return read_mem(address, &v, sizeof v) ? v : fallback;
}

// "enshrouded_server.exe+0x64bd12" / "kiln-repl.dll+0x2f10" / "0x1".
void describe(uintptr_t where, char* out, size_t cap);

// Calls the game's StateIndex into `index` (kIndexBytes, zeroed here first). False when it faulted.
bool state_index(uint8_t* index, const uint8_t* state, uintptr_t registry_address);

// A guard the tick thread's flush must look at.
void add_guard(Guard* g);

// The peer of machine m as the last Pre saw it (kiln_repl.h "Machines and peers"); flags KILN_REPL_PEER_*.
uint32_t peer_of(uint32_t machine, uint8_t* flags);

// ---- the loopback harness (loopback.cpp) ----
bool lab_init();                    // kiln_plugin_main, after the features resolved: what the harness needs
void lab_ready(uint32_t side);      // on_ready: may the lab functions answer (policy "any" + kiln.net's lab port)
bool lab_allowed();
void lab_pre(uintptr_t world);      // tick thread, every Pre: the fakes' last frame decoded, compared, acknowledged
void lab_world_stopping();          // the server world goes: the fakes and Kiln's states with it
uint32_t lab_fake_mask();           // bit per machine
uint32_t lab_peer_of(uint32_t machine, uint8_t* flags);  // a fake's peer (kiln_repl.h "Machines and peers")
KilnResult KILN_CALL lab_add_fakes(uint32_t count, KilnReplFake* out, uint32_t cap, uint32_t* added);
KilnResult KILN_CALL lab_force_relevant(uint32_t machine, KilnEntity entity);
KilnResult KILN_CALL lab_remove_fakes();
KilnResult KILN_CALL lab_stats(KilnReplLoopbackStats* out);
KilnResult KILN_CALL lab_decoded(uint32_t machine, KilnEntity entity, uint32_t hash, uint8_t* out, uint32_t cap, uint32_t* len,
                                 uint32_t* tick);

}  // namespace repl
