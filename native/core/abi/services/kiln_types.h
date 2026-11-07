/* SPDX-License-Identifier: MIT
 * kiln.types v1: the game's own type registry, its ECS systems, and its ECS event streams, for every service and mod.
 *   1. Types: every reflected type of the running build (about 11,000 on the server, 12,600 on the client): names,
 *      sizes, fields with offsets, base classes, enum values and the game's default values. Read from the static type
 *      table the exe carries in .rdata, located by data alone (no code pattern), so it survives code moves.
 *   2. Systems: every ECS system by name (the .rdata system descriptors), switched off and on, and profiled. One
 *      detour on the game's program runner does both; while nothing is off and the profiler is idle it costs two
 *      atomic loads per program slice.
 *   3. Events: read a world's ECS events of one type after its step, and post events into its queue (checked).
 * Fail soft: each part reports itself in status(); a part that is not found returns KILN_ERR_UNAVAILABLE.
 * Technique credit: Shrouded Engine Tweaks (MIT, Cacaida) documents the type table, the system descriptors, the
 * program runner and the event streams; Kiln's implementation is its own. Evidence: docs/re/shrouded-engine-tweaks.md.
 * Usage: docs/kiln/services/types.md.
 *
 * Threads: type queries are any thread (immutable snapshot). System switches/releases: server tick thread.
 * Profiler: any thread. Events: that world's tick thread, Post (event_each) or Pre/Post (event_post). */
#ifndef KILN_TYPES_H
#define KILN_TYPES_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_TYPES_SERVICE "kiln.types"
#define KILN_TYPES_VERSION 1u

/* KilnTypeInfo.kind: the game's primitive kind. */
enum {
    KILN_TYPE_BOOL = 1, KILN_TYPE_U8, KILN_TYPE_S8, KILN_TYPE_U16, KILN_TYPE_S16, KILN_TYPE_U32, KILN_TYPE_S32,
    KILN_TYPE_U64, KILN_TYPE_S64, KILN_TYPE_F32, KILN_TYPE_F64, KILN_TYPE_ENUM, KILN_TYPE_BITMASK8, KILN_TYPE_BITMASK16,
    KILN_TYPE_BITMASK32, KILN_TYPE_BITMASK64, KILN_TYPE_TYPEDEF, KILN_TYPE_STRUCT, KILN_TYPE_STATIC_ARRAY,
    KILN_TYPE_DS_ARRAY, KILN_TYPE_DS_STRING, KILN_TYPE_DS_OPTIONAL, KILN_TYPE_DS_VARIANT, KILN_TYPE_BLOB_ARRAY,
    KILN_TYPE_BLOB_STRING, KILN_TYPE_BLOB_OPTIONAL, KILN_TYPE_BLOB_VARIANT, KILN_TYPE_OBJECT_REFERENCE, KILN_TYPE_GUID,
};

/* A type is an opaque handle (const KilnType*): valid for the whole process, compare by pointer. */
typedef struct KilnType KilnType;

typedef struct KilnTypeInfo {
    uint32_t size;            /* set to sizeof(KilnTypeInfo) before calling */
    uint32_t index;           /* position in the game's table */
    KilnStr name;             /* "Health" */
    KilnStr qualified_name;   /* "keen::ecs::Health" */
    uint32_t type_size;       /* bytes */
    uint16_t alignment;
    uint8_t kind;             /* KILN_TYPE_* */
    uint8_t flags;            /* the game's flags byte */
    uint32_t hash;            /* FNV-1a-32 of qualified_name: the key of the game's component and event lookups */
    uint32_t field_count;     /* own fields (struct) or values (enum, bitmask) */
    const KilnType* inner;    /* struct: base class; array/optional/typedef/reference: element or target; NULL */
    const void* defaults;     /* the game's default value, type_size bytes, or NULL */
} KilnTypeInfo;

typedef struct KilnTypeField {
    KilnStr name;
    const KilnType* type;
    uint64_t offset;          /* in the owning struct (base-class fields: in the base) */
} KilnTypeField;

typedef struct KilnSystemInfo {
    uint32_t size;            /* set to sizeof(KilnSystemInfo) before calling */
    uint32_t index;           /* sorted by name */
    KilnStr name;             /* "stamina_depletion_sprint" */
    uint64_t rva;             /* the system's update function, relative to the image base */
    uint8_t enabled;          /* 0: the game skips it */
    uint8_t world_kinds;      /* while profiling: bit k = ran in a world of kind k (0 server, 1 prediction, 2 client) */
    uint16_t reserved;
    uint64_t calls;           /* since the profiler started */
    double ms;                /* CPU milliseconds since the profiler started, summed over every thread */
} KilnSystemInfo;

typedef struct KilnTypesStatus {
    uint32_t size;            /* set to sizeof(KilnTypesStatus) before calling */
    uint8_t types;            /* 1: the type table was found and checked */
    uint8_t systems;          /* 1: system names found and the runner hooked (switching and profiling work) */
    uint8_t events;           /* 1: event streams found (event_each / event_post work) */
    uint8_t profiling;
    uint32_t type_count, component_count, event_type_count, system_count, systems_off;
    double profile_seconds;   /* how long the profiler has run (or ran) */
    KilnStr detail;           /* what is missing, in words; "" when everything works */
} KilnTypesStatus;

/* Called once per event of the type, in queue order. data is the event (type_size bytes), valid during the call. */
typedef void(KILN_CALL* KilnEventFn)(const void* data, uint32_t size, void* user);

typedef struct KilnTypesApi {
    KilnServiceHeader header; /* { sizeof(KilnTypesApi), KILN_TYPES_VERSION } */
    KilnResult(KILN_CALL* status)(KilnTypesStatus* out);
    /* ---- types ---- */
    uint32_t(KILN_CALL* type_count)(void);
    const KilnType*(KILN_CALL* type_at)(uint32_t index);
    const KilnType*(KILN_CALL* type_by_hash)(uint32_t hash);
    /* "keen::ecs::Health" exactly; a name without "::" is tried as "keen::ecs::<name>", then as the only type with that
     * short name outside keen::ds, then as the only type with it at all. NULL when unknown or ambiguous. */
    const KilnType*(KILN_CALL* type_by_name)(KilnStr name);
    KilnResult(KILN_CALL* type_info)(const KilnType* type, KilnTypeInfo* out);
    /* Own field i (0-based, < field_count) of a struct. Base-class fields: walk KilnTypeInfo.inner. */
    KilnResult(KILN_CALL* type_field)(const KilnType* type, uint32_t index, KilnTypeField* out);
    /* Value i of an enum or bitmask. */
    KilnResult(KILN_CALL* type_enum)(const KilnType* type, uint32_t index, KilnStr* name, int64_t* value);
    /* The game's lists of ECS component types and event types (687 / 132 on svn 1287369). */
    uint32_t(KILN_CALL* component_type_count)(void);
    const KilnType*(KILN_CALL* component_type_at)(uint32_t index);
    uint32_t(KILN_CALL* event_type_count)(void);
    const KilnType*(KILN_CALL* event_type_at)(uint32_t index);
    /* ---- systems ---- */
    uint32_t(KILN_CALL* system_count)(void);
    KilnResult(KILN_CALL* system_info)(uint32_t index, KilnSystemInfo* out);
    int32_t(KILN_CALL* system_find)(KilnStr name);            /* index, or -1 */
    /* Only stamina_depletion_sprint and fall_damage_infliction may be disabled (unique update functions only).
     * owner must be a live, nonzero owner. A system stays off while any owner requests it; enabling releases this
     * owner's request. Revoked owners are released before skipping another slice; server world stop and service
     * unload restore every switch. Server tick thread. */
    KilnResult(KILN_CALL* system_set_enabled)(KilnOwner owner, uint32_t index, bool enabled);
    /* On: starts timing from zero. Off: keeps the numbers. */
    KilnResult(KILN_CALL* profile)(bool on);
    /* ---- events ---- */
    /* Every event of type `hash` the world produced in the step that just ended. KILN_ERR_NOT_FOUND: the world has no
     * stream for it. world = the world pointer (api->world(kind)). Tick thread, Post. */
    KilnResult(KILN_CALL* event_each)(uintptr_t world, uint32_t hash, KilnEventFn fn, void* user);
    /* Queues exactly the registered type's bytes; inline scalars/structs/static arrays/GUIDs/references only,
     * with bool/enum/range/layout validation. The game handles it at the start of the next step. Refused (KILN_ERR_NOT_FOUND) when the world has no stream for the hash: an unknown hash would
     * crash the game's distributor. KILN_ERR_BUFFER_TOO_SMALL: the queue is full this step. Tick thread. */
    KilnResult(KILN_CALL* event_post)(uintptr_t world, uint32_t hash, const void* data, uint32_t size);
    /* Appended in v1: release all of this owner's disable requests (tick thread). Check header.size first. */
    KilnResult(KILN_CALL* system_release_owner)(KilnOwner owner);
} KilnTypesApi;

#ifdef __cplusplus
}
#endif
#endif
