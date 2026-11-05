// SPDX-License-Identifier: MIT
// kiln.abilities: engine layouts and guarded engine calls (svn 1285601 layouts, resolved through
// sigdb/symbols/server.d/abilities.json; evidence in docs/re/abilities.md). No C++ object with a destructor lives in a
// __try frame; every raw read of game memory is guarded.
#pragma once
#include <cstddef>
#include <cstdint>
#include <cstring>

#include "kiln_api.h"

namespace kab {

constexpr uint32_t fnv1a(const char* s) {
    uint32_t h = 0x811c9dc5u;
    for (; *s; ++s) h = (h ^ static_cast<uint8_t>(*s)) * 0x01000193u;
    return h;
}
inline uint32_t fnv1a(const void* p, size_t n) {
    uint32_t h = 0x811c9dc5u;
    for (size_t i = 0; i < n; ++i) h = (h ^ static_cast<const uint8_t*>(p)[i]) * 0x01000193u;
    return h;
}

constexpr uint32_t kImpactHierarchy = fnv1a("keen::ecs::ImpactHierarchy");  // 0xcd3947e0
constexpr uint32_t kActor = fnv1a("keen::ecs::Actor");                        // 0x009e8b34
constexpr uint32_t kTypeImpactProgram = fnv1a("keen::ImpactProgram");        // 0xd4922d88
static_assert(kImpactHierarchy == 0xcd3947e0u && kActor == 0x009e8b34u && kTypeImpactProgram == 0xd4922d88u);

// ---- the native ABI (interpreter 0x990920: Result* fn(Result* out, Slice<u32>* args, NativeCtx* ctx)) ----
struct NativeResult {
    uint8_t error;  // 0 ok (value pushed), 1..3 suspend, else error (pushed, instance flag Error)
    uint8_t pad[3];
    uint32_t zero;
    uint8_t value;  // pushed when error == 0
    uint8_t pad2[7];
};
struct ArgSlice {
    const uint32_t* data;  // args[0] = the top of the stack (the last push)
    uint64_t count;
};
struct NativeCtx {
    const uint8_t* layout;  // ImpactVariable[layout_count] (24 B each)
    uint64_t layout_count;
    uint8_t* data;          // instance data
    uint64_t data_size;
    uint64_t r20, r28;
    uintptr_t iter_ctx;     // EcsIterCtx (+0x110: the native registry)
    uint8_t rng[8];
    uint32_t impact_id;     // +0x40
    uint8_t program_guid[16];
    uint32_t program_id;    // +0x54
    uint32_t site;          // +0x58: the ECALL's call-site id
    uint8_t b5c;
};
static_assert(offsetof(NativeCtx, iter_ctx) == 0x30 && offsetof(NativeCtx, impact_id) == 0x40 &&
              offsetof(NativeCtx, program_id) == 0x54 && offsetof(NativeCtx, site) == 0x58);
using NativeFn = NativeResult*(__fastcall*)(NativeResult* out, ArgSlice* args, NativeCtx* ctx);

// keen::ImpactVariable (24 B)
struct ImpactVariable {
    uint32_t name;
    uint32_t config_id;
    uint32_t type;
    uint16_t size;
    uint16_t offset;
    int32_t dbg_off;  // BlobString: offset relative to this field, byte count
    uint32_t dbg_len;
};
static_assert(sizeof(ImpactVariable) == 24);

// BlobArray {int32 offset relative to the field, uint32 count}
struct BlobArray {
    int32_t offset;
    uint32_t count;
    const uint8_t* ptr() const {
        return offset ? reinterpret_cast<const uint8_t*>(this) + offset : nullptr;
    }
};
// keen::ImpactProgram header (64 B)
struct ProgramHeader {
    uint32_t id;
    uint8_t guid[16];
    uint16_t stack_size;
    uint8_t streams;
    uint8_t pad;
    BlobArray code, shutdown, layout, data, query_calls;
};
static_assert(sizeof(ProgramHeader) == 64 && offsetof(ProgramHeader, code) == 0x18 &&
              offsetof(ProgramHeader, layout) == 0x28 && offsetof(ProgramHeader, data) == 0x30);

// Generic engine hash map (occupancy bits +0x08, capacity +0x10, keys +0x28, values +0x40, count +0x54), identity hash
// with linear probing (getProgram 0x992f80, the interpreter's native lookup).
struct MapView {
    uint64_t* bits;
    uint64_t cap;
    uint32_t* keys;
    uint8_t* values;  // stride per map
    uint32_t* count;
};

struct Engine {
    using RegisterNativeFn = uint64_t(__fastcall*)(uintptr_t registry, uint32_t hash, NativeFn fn, const void* name,
                                                    uint8_t argc);
    using GetProgramFn = const uint8_t*(__fastcall*)(uintptr_t program_registry, uint32_t id);
    using CreateInstanceFn = uint32_t(__fastcall*)(uintptr_t system, uint32_t program_id, uint32_t entity);
    using RunImpactFn = void(__fastcall*)(uintptr_t system, uint32_t impact_id, uintptr_t iter_ctx);
    using InstanceLookupFn = uint8_t*(__fastcall*)(uintptr_t system, uint32_t impact_id);
    struct Slice {
        void* ptr;
        uint64_t size;
    };
    using GetComponentFn = void(__fastcall*)(Slice* out, uintptr_t world, uint32_t hash, uint32_t entity);
    struct AttrFindResult {
        uint8_t err;
        uint8_t pad[3];
        uint32_t zero;
        const uint8_t* info;  // +0 u32 rootId, +0xC u16 slot
    };
    using AttrFindFn = void*(__fastcall*)(AttrFindResult* out, uint32_t id, const void* registry);
    using AttrAddFn = void(__fastcall*)(void* comp, const void* registry, int32_t delta, uint32_t id);
    // 0x369d0 builds SequenceRuntimeId {sequenceId = r9, triggerTime = [r8+8], actorEntityId = [r8], callIndex =
    // ActorInput.triggerCallCounter} into ActorInput.triggerContext (cleared first) and *out.
    struct EntityTime {
        uint32_t entity;
        uint32_t pad;
        int64_t time;
    };
    using TriggerSequenceFn = void*(__fastcall*)(void* out_runtime_id, void* actor_input, const EntityTime* who,
                                                 uint32_t sequence, uint16_t trigger, uint8_t input);

    RegisterNativeFn register_native = nullptr;
    GetProgramFn get_program = nullptr;
    CreateInstanceFn create_instance = nullptr;
    RunImpactFn run_impact = nullptr;
    InstanceLookupFn instance_lookup = nullptr;
    GetComponentFn get_component = nullptr;
    AttrFindFn attr_find = nullptr;
    AttrAddFn attr_add = nullptr;
    TriggerSequenceFn trigger_sequence = nullptr;
    int64_t trigger_counter = 0x178, world_storage = -1, storage_clock = -1, storage_period = -1;

    int64_t name_cap = 0x20, natives_map = 0x20;
    int64_t prog_count = 0xbc, prog_cap = 0x78, prog_bits = 0x70, prog_keys = 0x90, prog_values = 0xa8;
    int64_t system_registry = 8;
    int64_t inst_program = 4, inst_entity = 8, inst_flags = 0x1f2;
    static constexpr int64_t inst_data = 0x18, inst_data_size = 0x20;  // checked against the program at run time
    static constexpr int64_t actor_sequence = 0xc08;                   // Actor.sequenceRuntimeId (reflection)
};
extern Engine E;

// ---- guarded access (engine.cpp) ----
bool read_bytes(const void* src, void* dst, size_t n);
bool write_bytes(void* dst, const void* src, size_t n);
const uint8_t* get_program(uintptr_t program_registry, uint32_t id);
uint8_t* instance(uintptr_t system, uint32_t impact_id);
void* component(uintptr_t world, uint32_t hash, uint32_t entity, uint64_t* size = nullptr);
bool attr_slot(const void* registry, uint8_t* comp, uint32_t attr, int32_t** slot);
bool attr_add(const void* registry, void* comp, uint32_t attr, int32_t delta);
bool trigger_sequence(void* actor_input, uint32_t entity, uint32_t sequence, int64_t now, uint16_t trigger);
int64_t ecs_now(uintptr_t world);
// registerNative(registry, hash, fn, Slice<char>{name}, argc); false on a fault or when the map is full.
bool register_native(uintptr_t registry, uint32_t hash, NativeFn fn, const char* name, size_t name_len, uint8_t argc);
// The ImpactRegistry's program map.
bool program_map(uintptr_t program_registry, MapView* out);
// Inserts (or finds) key in the map; values are 8-byte pointers. false: full / fault / layout mismatch.
bool program_map_insert(uintptr_t program_registry, uint32_t id, const void* program, bool* existed);
// Copies up to cap (id, program) pairs of the map; returns the number of occupied slots (SIZE_MAX on a fault).
size_t program_map_snapshot(uintptr_t program_registry, uint32_t* ids, const uint8_t** programs, size_t cap);

}  // namespace kab
