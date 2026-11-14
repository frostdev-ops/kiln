// SPDX-License-Identifier: MIT
// kiln.net game access (docs/re/network.md). Each __try function keeps only plain data in its frame.
#include "game.h"

#include <windows.h>

#include <cstring>

namespace knet {

Layout L;
Functions F;

namespace {

// Result of SessionRead (0x28 bytes): +0 status (0 = a message), +8 message handle (SessionFree takes &res.handle),
// +0x18 data, +0x20 size in bytes.
struct ReadRes {
    uint8_t status;
    uint8_t pad[7];
    uint64_t handle;
    uint64_t handle2;
    const uint8_t* data;
    uint64_t size;
};
static_assert(sizeof(ReadRes) == 0x28);

// Result of SessionAlloc (0x38 bytes): +0 status, +8 handle (Commit/Abort take &res.handle), +0x20 final size (u32,
// set before Commit), +0x28 buffer, +0x30 capacity in bytes.
struct AllocRes {
    uint8_t status;
    uint8_t pad[7];
    uint64_t handle;
    uint64_t h1;
    uint64_t h2;
    uint32_t size;
    uint32_t pad2;
    uint8_t* data;
    uint64_t capacity;
};
static_assert(sizeof(AllocRes) == 0x38);
static_assert(offsetof(AllocRes, size) == 0x20 && offsetof(AllocRes, data) == 0x28);

// SessionAlloc's request: explicit = 0 asks for the socket's maximum (8 KiB on channel 12).
struct AllocSpec {
    uint64_t min_size;
    uint64_t max_size;
    uint8_t explicit_size;
    uint8_t pad[7];
};
static_assert(sizeof(AllocSpec) == 0x18);

using ReadFn = ReadRes*(__fastcall*)(ReadRes* out, uintptr_t session, uintptr_t socket);
using FreeFn = void(__fastcall*)(uintptr_t session, uint64_t* handle);
using AllocFn = AllocRes*(__fastcall*)(AllocRes* out, uintptr_t session, uintptr_t socket, const AllocSpec* spec);
using CommitFn = void(__fastcall*)(uintptr_t session, uint64_t* handle);
using LoginResultFn = uint64_t(__fastcall*)(uintptr_t gs, uint32_t peer, uint8_t reason);
// 0xa02c90 (server f0d824be): builds command 3 {+0x604 machine, +0x608 ban} on its stack and pushes it into the session's
// command queue (spin-locked ring; it waits only while the ring is full, as for every command the game queues).
using SessionKickFn = void(__fastcall*)(uintptr_t session, uint32_t machine, uint8_t ban);

bool valid_ptr(uintptr_t p) { return p >= 0x10000 && p < 0x00007FFFFFFFFFFFull; }

}  // namespace

bool read_mem(uintptr_t address, void* out, size_t n) {
    if (!valid_ptr(address) || address > UINTPTR_MAX - n) return false;
    __try {
        std::memcpy(out, reinterpret_cast<const void*>(address), n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

int read_machine_slots(uintptr_t gs, MachineSlot* out, int capacity) {
    if (!valid_ptr(gs) || L.gs_machines < 0 || L.machine_count <= 0) return -1;
    const int n = static_cast<int>(L.machine_count) < capacity ? static_cast<int>(L.machine_count) : capacity;
    __try {
        for (int i = 0; i < n; ++i) {
            const uint8_t* s = reinterpret_cast<const uint8_t*>(gs + L.gs_machines + static_cast<uintptr_t>(i) * 8);
            out[i].peer = *reinterpret_cast<const uint32_t*>(s);
            out[i].flags = s[4];
        }
        return n;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}

int read_records(uintptr_t gs, Record* out, int capacity) {
    if (!valid_ptr(gs) || L.gs_records < 0 || L.rec_stride <= 0 || L.rec_count <= 0 || L.rec_peer < 0 ||
        L.rec_state < 0 || L.rec_account < 0 || L.rec_name < 0 || L.rec_entity < 0)
        return -1;
    const int n = static_cast<int>(L.rec_count) < capacity ? static_cast<int>(L.rec_count) : capacity;
    __try {
        for (int i = 0; i < n; ++i) {
            const uint8_t* r = reinterpret_cast<const uint8_t*>(gs + L.gs_records + static_cast<uintptr_t>(i) * L.rec_stride);
            Record& o = out[i];
            o.slot = static_cast<uint32_t>(i);
            o.connection = *reinterpret_cast<const uint32_t*>(r);
            o.peer = *reinterpret_cast<const uint32_t*>(r + L.rec_peer);
            o.state = r[L.rec_state];
            o.entity = *reinterpret_cast<const uint32_t*>(r + L.rec_entity);
            o.account = *reinterpret_cast<const uint64_t*>(r + L.rec_account);
            o.name[0] = 0;
            const char* np = *reinterpret_cast<const char* const*>(r + L.rec_name);
            const uint64_t nl = *reinterpret_cast<const uint64_t*>(r + L.rec_name + 8);
            if (o.connection != 0 && np && valid_ptr(reinterpret_cast<uintptr_t>(np)) && nl > 0 && nl < 1024) {
                const size_t k = nl < sizeof(o.name) - 1 ? static_cast<size_t>(nl) : sizeof(o.name) - 1;
                std::memcpy(o.name, np, k);
                o.name[k] = 0;
            }
        }
        return n;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}

uintptr_t server_socket(uintptr_t gs, uint32_t peer, uintptr_t* session) {
    if (!valid_ptr(gs) || peer == 0 || L.gs_sockets < 0 || L.sockets_machines < 0 || L.machine_stride <= 0 ||
        L.machine_sockets < 0 || L.channel < 0)
        return 0;
    __try {
        const uintptr_t set = *reinterpret_cast<const uintptr_t*>(gs + L.gs_sockets);
        if (!valid_ptr(set)) return 0;
        const uintptr_t table = *reinterpret_cast<const uintptr_t*>(set + L.sockets_machines);
        if (!valid_ptr(table)) return 0;
        const uintptr_t entry = table + static_cast<uintptr_t>(peer & 0x7F) * static_cast<uintptr_t>(L.machine_stride);
        if (*reinterpret_cast<const uint32_t*>(entry) != peer) return 0;
        const uintptr_t sockets = *reinterpret_cast<const uintptr_t*>(entry + L.machine_sockets);
        const uint64_t count = *reinterpret_cast<const uint64_t*>(entry + L.machine_sockets + 8);
        if (!valid_ptr(sockets) || count <= static_cast<uint64_t>(L.channel) || count > 64) return 0;
        const uintptr_t socket = reinterpret_cast<const uintptr_t*>(sockets)[L.channel];
        if (!valid_ptr(socket)) return 0;
        if (session) *session = *reinterpret_cast<const uintptr_t*>(set);
        return socket;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

uintptr_t client_socket(uintptr_t gc, uintptr_t* session, uintptr_t* socket_set) {
    if (!valid_ptr(gc) || L.gc_sockets < 0 || L.client_server_sockets < 0 || L.channel < 0) return 0;
    __try {
        const uintptr_t set = *reinterpret_cast<const uintptr_t*>(gc + L.gc_sockets);
        if (socket_set) *socket_set = set;
        if (!valid_ptr(set)) return 0;
        const uintptr_t sockets = *reinterpret_cast<const uintptr_t*>(set + L.client_server_sockets);
        if (!valid_ptr(sockets)) return 0;
        const uintptr_t socket = reinterpret_cast<const uintptr_t*>(sockets)[L.channel];
        if (!valid_ptr(socket)) return 0;
        if (session) *session = *reinterpret_cast<const uintptr_t*>(set);
        return socket;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

int drain(uintptr_t session, uintptr_t socket, FrameSink sink, void* user) {
    if (!valid_ptr(session) || !valid_ptr(socket) || !F.read || !F.free) return -1;
    const auto read = reinterpret_cast<ReadFn>(F.read);
    const auto release = reinterpret_cast<FreeFn>(F.free);
    int n = 0;
    __try {
        for (uint32_t i = 0; i < kMaxMessagesPerDrain; ++i) {
            ReadRes r;
            std::memset(&r, 0, sizeof(r));
            read(&r, session, socket);
            if (r.status != 0) break;
            if (r.data && r.size > 0) sink(r.data, r.size, user);
            release(session, &r.handle);
            ++n;
        }
        return n;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}

uint32_t send(uintptr_t session, uintptr_t socket, const uint8_t* data, uint32_t len, uint64_t* capacity) {
    if (!valid_ptr(session) || !valid_ptr(socket) || !F.alloc || !F.commit || !F.abort) return 0xFF;
    const auto alloc = reinterpret_cast<AllocFn>(F.alloc);
    const auto commit = reinterpret_cast<CommitFn>(F.commit);
    const auto abort = reinterpret_cast<CommitFn>(F.abort);
    __try {
        AllocRes r;
        std::memset(&r, 0, sizeof(r));
        AllocSpec spec;
        std::memset(&spec, 0, sizeof(spec));
        alloc(&r, session, socket, &spec);
        if (capacity) *capacity = r.capacity;
        if (r.status != 0) return r.status ? r.status : 0xFF;
        // The game's bit writers flush whole 32-bit words: keep the size a multiple of 4 (zero padding).
        const uint32_t padded = (len + 3u) & ~3u;
        if (!r.data || r.capacity < padded) {
            abort(session, &r.handle);
            return 0xFE;
        }
        std::memcpy(r.data, data, len);
        if (padded > len) std::memset(r.data + len, 0, padded - len);
        r.size = padded;
        commit(session, &r.handle);
        return 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0xFF;
    }
}

bool login_failure(uintptr_t gs, uint32_t peer, uint8_t reason) {
    if (!valid_ptr(gs) || !F.login_result || peer == 0) return false;
    const auto fn = reinterpret_cast<LoginResultFn>(F.login_result);
    __try {
        fn(gs, peer, reason);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool session_kick(uintptr_t gs, uint32_t machine) {
    if (!valid_ptr(gs) || !F.session_kick || L.gs_session < 0) return false;
    const auto fn = reinterpret_cast<SessionKickFn>(F.session_kick);
    __try {
        const uintptr_t session = *reinterpret_cast<const uintptr_t*>(gs + static_cast<uintptr_t>(L.gs_session));
        if (!valid_ptr(session)) return false;
        fn(session, machine, 0);  // ban 0: nothing goes on the server's ban list
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool machine_login(MachineLoginFn fn, uintptr_t gs, uint32_t peer, uint8_t type) {
    if (!valid_ptr(gs) || !fn || peer == 0) return false;
    // The game reads the message's first byte only (0x7af4e0: "movzx ecx, byte ptr [r8]"; r8 is then overwritten).
    uint8_t message[16] = {type};
    __try {
        fn(gs, peer, message);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

namespace {
// The engine's hash map (docs/re/spawn.md): +0x08 occupancy bits, +0x10 capacity, +0x28 keys, +0x40 values. The value of
// `id`, 0 if it has none. Runs inside the caller's guard.
uintptr_t map_find(uintptr_t map, uint32_t id) {
    const uint64_t cap = *reinterpret_cast<const uint64_t*>(map + 0x10);
    if (cap == 0 || (cap & (cap - 1)) != 0) return 0;
    const uint64_t* bits = *reinterpret_cast<uint64_t* const*>(map + 0x08);
    const uint32_t* keys = *reinterpret_cast<uint32_t* const*>(map + 0x28);
    const uintptr_t* values = *reinterpret_cast<uintptr_t* const*>(map + 0x40);
    uint32_t x = ((id >> 16) ^ id) * 0x45d9f3bu;
    x = ((x >> 16) ^ x) * 0x45d9f3bu;
    // 64-bit like the game (0x650c50: "shr rax, 0x10; xor rax, rcx"): the high half shifted in is zero.
    uint64_t i = ((static_cast<uint64_t>(x) >> 16) ^ x) & (cap - 1);
    for (uint64_t probes = 0; probes < cap; ++probes) {
        if (!((bits[i >> 6] >> (i & 63)) & 1)) return 0;
        if (keys[i] == id) return values[i];
        i = (i + 1) & (cap - 1);
    }
    return 0;
}
}  // namespace

bool entity_template(uintptr_t storage, uint32_t id, uint8_t guid[16], uint32_t* parent) {
    if (!valid_ptr(storage) || L.entity_map < 0 || L.entity_map_cap != L.entity_map + 0x10 || L.entity_records != L.entity_map + 0x40 ||
        L.rec_template < 0 || L.rec_parent < 0 || L.rec_id < 0 || id == 0)
        return false;
    __try {
        const uintptr_t rec = map_find(storage + static_cast<uintptr_t>(L.entity_map), id);
        if (!rec || *reinterpret_cast<const uint32_t*>(rec + L.rec_id) != id) return false;
        const uintptr_t ti = *reinterpret_cast<const uintptr_t*>(rec + L.rec_template);
        if (!ti) return false;
        std::memcpy(guid, reinterpret_cast<const void*>(ti), 16);  // TemplateInfo starts with its GUID
        *parent = *reinterpret_cast<const uint32_t*>(rec + L.rec_parent);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

uintptr_t player_entity_state(uintptr_t player, uint32_t id, uint8_t* flags) {
    if (!valid_ptr(player) || L.player_state_map < 0 || id == 0) return 0;
    __try {
        const uintptr_t state = map_find(player + static_cast<uintptr_t>(L.player_state_map), id);
        if (state) *flags = *reinterpret_cast<const uint8_t*>(state + 1);
        return state;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

}  // namespace knet
