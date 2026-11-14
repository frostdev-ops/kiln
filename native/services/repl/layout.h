// SPDX-License-Identifier: MIT
// kiln.repl: the game's replication structures (docs/re/network.md, dev.28 replication notes; the dossier's chapter 11,
// §2.2 "Layouts" and §2.6-2.7). The offsets that a game update is most likely to move are captured by sigdb
// (repl/world.objects, repl/obj.stride, repl/obj.client_ctx, repl/obj.pred_ctx, repl/world.slot_mask, repl/world.tick,
// repl/ctx.heap; net/gs.machines; world.storage; spawn/registry.template_map*), so they fail loudly at boot. The ones here
// are the same on both exes, read in the lab against the live game and fixed per build of the structures they belong
// to: they sit next to a captured offset of the same structure (a context, an object, the registry) and move with it.
#pragma once
#include <cstddef>
#include <cstdint>

namespace repl {

// ---- send step: one 12-byte item per machine the frame replicates to (Prepare's input) ----
#pragma pack(push, 1)
struct Item12 {
    uint8_t machine;  // machine index (peer & 0x7f)
    uint8_t slot;     // player slot
    uint16_t pad;
    uint32_t max4;    // the session's message size limit on channel 4 / 5 (not a bandwidth budget)
    uint32_t max5;
};
#pragma pack(pop)
static_assert(sizeof(Item12) == 12);
struct ItemList {
    Item12* ptr;
    uint64_t count;
};

// ---- the send callback's batch: one per (machine, channel) ----
struct MsgEntry {          // 32 bytes
    uint8_t type;          // 0 frame, 2 handshake, 4 entity, 5 destroy list
    uint8_t pad[3];
    uint32_t seq;          // what acks name
    uint8_t* data;
    uint64_t size;
    void* trailer;
};
static_assert(sizeof(MsgEntry) == 32 && offsetof(MsgEntry, data) == 8 && offsetof(MsgEntry, size) == 16);
struct Batch {             // 24 bytes
    MsgEntry* msgs;
    uint32_t count;
    uint32_t pad;
    uint8_t machine;       // +0x10
    uint8_t pred;          // +0x11: 0 client channel (4), 1 prediction channel (5)
    uint16_t pad2;
    uint32_t sent;         // +0x14: messages the session accepted (written by the callback)
};
static_assert(sizeof(Batch) == 24 && offsetof(Batch, machine) == 0x10 && offsetof(Batch, sent) == 0x14);
struct CbPair {            // SendCallback's argument
    uintptr_t gs;          // the GameServer
    Batch* batch;
};

struct Slice {
    const uint8_t* ptr;
    uint64_t size;
};

// ---- states (§2.7) ----
struct StateRef {
    uint8_t* state;
    uint8_t temp;
    uint8_t pad[7];
};
// TemplateState's scratch tracker: zero it, set `alloc`, use it for one call, then free every block that is not the
// returned state.
struct Holder {
    void* alloc;
    void* blocks[4];
    uint8_t n;
    uint8_t pad[7];
};
static_assert(sizeof(Holder) == 48);
struct TemplateArgs {
    Holder* holder;
    uintptr_t registry;
    uintptr_t template_info;
    uint8_t pred;
    uint8_t pad[7];
};

// ---- EntityWriter (§2.6) ----
// bool EntityWriter(WriterOut* out, const WriterArgs* args); WriterArgs lives on the queue reader's stack.
constexpr size_t kWriterWorld = 0x08;    // World*
constexpr size_t kWriterEntry = 0x18;    // {u32 entity; u8 kind}*
constexpr size_t kWriterCtx = 0x20;      // the (player, channel) context
constexpr size_t kWriterMaxSize = 0x30;  // u64 channel maximum (0 = none)
constexpr size_t kWriterTick = 0x38;     // u32 server tick
constexpr size_t kWriterSeq = 0x3c;      // u32 this message's seq
constexpr size_t kWriterChannel = 0x41;  // u8 0 client, 1 prediction
struct WriterOut {
    uintptr_t record;
    uint8_t* state;
    uint8_t skipped;
};

// ---- a replication context (one per machine and channel; the object's +repl/obj.client_ctx / +repl/obj.pred_ctx) ----
constexpr uintptr_t kCtxHandshakeAcked = 0x1;   // u8: the type-2 handshake was acknowledged
constexpr uintptr_t kCtxStateMap = 0x858;       // engine map u32 entity -> PerPlayerState*
constexpr uintptr_t kCtxUnchanged = 0x8db78;    // u64: writer skips because nothing changed
constexpr uintptr_t kCtxNewList = 0x8dba0;      // newly relevant entities, 0x14 bytes each {u32 entity; ...; +0x10 u8 opts}
constexpr uintptr_t kCtxNewCount = 0x92ba0;     // u16 count of that list
constexpr uint32_t kCtxNewEntry = 0x14;
constexpr uint32_t kCtxNewMax = 1000;           // the game's list holds 1024; leave room for the game's own entries

// ---- a replication object (objects + machine * repl/obj.stride) ----
constexpr uintptr_t kObjActive = 0x0;  // u8
constexpr uintptr_t kObjSlot = 0x4;    // u8 player slot

// ---- the GameServer's machine slots (+net/gs.machines, 8 bytes each: u32 peer handle, u8 flags) ----
constexpr uintptr_t kMachineSlotStride = 8;

// ---- the ECS storage (world + world.storage) ----
// u16 mask of live player entities (ids 1..1023, bit (id - 1) mod 16): the server world steps its programs in parallel
// only while it is not 0 (the dossier's chapter 06a §2.4 [static], proposed world/storage.players_mask). The harness
// only logs it (a fake has no player entity).
constexpr uintptr_t kStoragePlayersMask = 0x7ddc0a;

// ---- the component registry R = *(world + world.storage) ----
constexpr uintptr_t kRegDescriptors = 0x08;    // 0x100 bytes per component
constexpr uintptr_t kRegCount = 0x10;          // u64
constexpr uintptr_t kRegSizes = 0xe8;          // u16* per component
constexpr uintptr_t kRegCodecs = 0x118;        // 0x28 bytes per component: pack, unpack, delta, undelta, u8 flag
constexpr uintptr_t kRegReplicated = 0x190;    // 640-bit mask (also world/registry.replicated)
constexpr uintptr_t kRegVersioned = 0x1e0;     // 640-bit mask: a u8 version travels with the component
constexpr uintptr_t kDescStride = 0x100;
constexpr uintptr_t kDescName = 0x00;          // const char*
constexpr uintptr_t kDescNameLen = 0x08;       // u64
constexpr uintptr_t kDescHash = 0x20;          // u32
constexpr uintptr_t kDescUsage = 0xb0;         // u8: & 7 client channel, & 0x38 prediction channel
constexpr uintptr_t kCodecStride = 0x28;
constexpr uint32_t kMaxComponents = 640;

// ---- the engine's open-addressing hash map ----
constexpr uintptr_t kMapBits = 0x08, kMapCap = 0x10, kMapKeys = 0x28, kMapValues = 0x40, kMapCount = 0x54;

// The engine's u32 key hash (docs/re/spawn.md; net and spawn read the same maps).
inline uint64_t map_slot(uint32_t key, uint64_t cap) {
    uint32_t x = ((key >> 16) ^ key) * 0x45d9f3bu;
    x = ((x >> 16) ^ x) * 0x45d9f3bu;
    return ((static_cast<uint64_t>(x) >> 16) ^ x) & (cap - 1);
}

// ---- a state blob {u16 size, u16 count, u32 tick, list 1, whole set, data} and its index (StateIndex) ----
constexpr size_t kStateHeader = 8;
constexpr size_t kIndexBytes = 0x7d0;       // the caller's buffer, zeroed before each call
constexpr size_t kIndexOffsets = 0x280;     // u16 per component index: data at state + 8 + offset
constexpr size_t kIndexWholeSet = 0x780;    // 640-bit mask of the entity's whole component set

// ---- entity message header (§2.3) ----
enum : uint32_t { kKindFrame = 0, kKindCreate = 2, kKindUpdate = 3, kKindDestroyList = 4 };
enum : uint8_t { kMsgFrame = 0, kMsgHandshake = 2, kMsgEntity = 4, kMsgList = 5 };

}  // namespace repl
