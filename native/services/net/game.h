// SPDX-License-Identifier: MIT
// kiln.net: everything that touches game memory or calls game functions (docs/re/network.md). Every function here
// runs under a structured-exception guard and returns failure instead of crashing; none of them allocates.
#pragma once
#include <cstddef>
#include <cstdint>

namespace knet {

// Layout constants from sigdb (net/*). -1 = unknown.
struct Layout {
    int64_t gs_sockets = -1;        // GameServer -> socket set
    int64_t sockets_machines = -1;  // socket set -> machine table
    int64_t machine_stride = -1;    // bytes per machine table entry
    int64_t machine_sockets = -1;   // entry -> Socket*[] (count at +8 after it)
    int64_t channel = -1;           // the game channel Kiln uses (12)
    int64_t gs_machines = -1;       // GameServer -> 17 machine slots {u32 peer; u8 flags}
    int64_t machine_count = -1;
    int64_t gs_records = -1;        // GameServer -> 16 player records
    int64_t rec_stride = -1, rec_count = -1, rec_state = -1, rec_peer = -1, rec_account = -1, rec_name = -1,
            rec_entity = -1;
    int64_t gc_sockets = -1;            // GameClient -> socket set
    int64_t client_server_sockets = -1; // client socket set -> Socket*[] toward the server machine
    int64_t entity_map = -1;            // ECS storage -> entity map (kiln.spawn's constants)
    int64_t entity_map_cap = -1, entity_records = -1, rec_parent = -1, rec_template = -1, rec_id = -1;
    int64_t player_state_map = -1;      // a player's replication object -> entity id to state map
    int64_t create_entity = -1, create_parent = -1;  // create reader's frame: rbp + these
};

struct Functions {
    uintptr_t read = 0, free = 0, alloc = 0, commit = 0, abort = 0, login_result = 0;
};

extern Layout L;
extern Functions F;

constexpr uint32_t kMaxMessagesPerDrain = 512;  // the game's own per-socket per-frame bound

// Receives one message read from a game socket. Called inside the guarded loop: must not throw.
using FrameSink = void (*)(const uint8_t* data, uint64_t size, void* user);

struct MachineSlot {
    uint32_t peer;
    uint8_t flags;
};

struct Record {
    uint32_t slot;
    uint32_t connection;
    uint32_t peer;
    uint32_t state;
    uint32_t entity;
    uint64_t account;
    char name[64];
};

// Copies `n` bytes; false on a bad address.
bool read_mem(uintptr_t address, void* out, size_t n);

// Server half. `gs` = GameServer.
int read_machine_slots(uintptr_t gs, MachineSlot* out, int capacity);   // -1 on failure
int read_records(uintptr_t gs, Record* out, int capacity);              // -1 on failure
// The game socket of channel L.channel toward machine `peer` (0 when none), and the session that owns it.
uintptr_t server_socket(uintptr_t gs, uint32_t peer, uintptr_t* session);
// Client half. `gc` = GameClient. Socket of channel L.channel toward the server (0 when not connected).
uintptr_t client_socket(uintptr_t gc, uintptr_t* session, uintptr_t* socket_set);

// Reads every waiting message of `socket` into `sink`. Returns messages read, -1 when the game faulted.
int drain(uintptr_t session, uintptr_t socket, FrameSink sink, void* user);

// Sends one message. Returns the game's allocation status (0 = sent), 0xFE = too big for the socket (capacity in
// *capacity), 0xFF = fault.
uint32_t send(uintptr_t session, uintptr_t socket, const uint8_t* data, uint32_t len, uint64_t* capacity);

// The game's login-failure message to machine `peer` (kick). False on fault.
bool login_failure(uintptr_t gs, uint32_t peer, uint8_t reason);

// `fn` (the hook's trampoline of the game's machine login) with a login message of `type`. False on fault.
using MachineLoginFn = void(__fastcall*)(uintptr_t gs, uint32_t peer, const uint8_t* message);
bool machine_login(MachineLoginFn fn, uintptr_t gs, uint32_t peer, uint8_t type);

// The template GUID and parent id of an entity of the ECS storage `storage`. False: no such entity, or a fault.
bool entity_template(uintptr_t storage, uint32_t id, uint8_t guid[16], uint32_t* parent);

// The state of `id` in a player's replication object, 0 if it has none. *flags = the state's flag byte.
uintptr_t player_entity_state(uintptr_t player, uint32_t id, uint8_t* flags);

}  // namespace knet
