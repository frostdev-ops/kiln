/* SPDX-License-Identifier: MIT
 * kiln.world v1: the server world's ECS for other services and mods.
 *   1. ECS programs: register your own program (system) into the game's scheduler, with declared component,
 *      singleton and event access, and helpers to use inside its update (iterator, command buffer, other entities).
 *   2. Entity access on the tick thread: component lookup by name/hash, transform and position in metres, "all
 *      entities that have component X", entity count.
 *   3. Teleport: moves an entity the way the game's own fast travel does (the engine's Teleport component).
 * Only kiln.world hooks the game's program registration; everyone else registers programs through this table.
 * Evidence and engine notes: docs/re/world.md. Usage: docs/kiln/services/world.md.
 *
 * Threads
 *  - register_program: before the server world finalizes its programs, i.e. from kiln_plugin_main or on_ready (main
 *    thread, before the game's entry point). Later calls return KILN_ERR_UNAVAILABLE.
 *  - Inside a program update you are on a game worker thread, inside the sim step, in parallel with every program
 *    whose declared access does not conflict with yours. Touch only what your descriptor declares; use the ctx
 *    helpers below. Never create or destroy entities there (use the command buffer).
 *  - Entity access (component, transform, find, ...) and teleport bookkeeping: the tick thread in Pre or Post (no
 *    world job running), or inside one of your program updates for data the program declared.
 *    KILN_ERR_WRONG_THREAD otherwise. Managed mods are always called on the tick thread.
 *  - component_hash and status are thread-safe.
 *
 * Names: components are the engine's short names without "keen::ecs::" ("CurrentTransform", "Inventory"); the hash
 * is FNV-1a-32 of "keen::ecs::" + name (types.json qualifiedHash). A name that already contains "::" is hashed as
 * given. Singletons are named as the game registers them ("ecs::PathGridSystem", "g38_items::ItemRegistry"). Events
 * are short names ("EntityDiedEvent"). Every name is checked against the world's own maps before the program is
 * handed to the engine: one unknown name refuses that program (logged), because the engine would otherwise build a
 * half-empty access mask for it and hand the update a misaligned iterator.
 *
 * Positions are metres (KilnVec3; the engine stores 32.32 fixed point: round(m * 2^32)). The table only grows at
 * the end; check header.size before using a field added later. */
#ifndef KILN_WORLD_H
#define KILN_WORLD_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_WORLD_SERVICE "kiln.world"
#define KILN_WORLD_VERSION 1u

/* ======================================================================================= programs ============== */

/* The engine's ProgramContext (opaque): what the game passes to every program update. */
typedef struct KilnProgramCtx KilnProgramCtx;

/* A list of names. items may be NULL when count is 0. kiln.world copies the strings. */
typedef struct KilnNames {
    const KilnStr* items;
    uint32_t count;
    uint32_t reserved;
} KilnNames;

/* KilnProgramDesc.flags (the engine's own program flag bits; others are refused). */
enum {
    KILN_PROGRAM_SYSTEM = 0x0004u,    /* no entity query: one update per step with no entities (singletons, other-
                                         entity access and the command buffer still work) */
    KILN_PROGRAM_INIT = 0x0008u,      /* runs on newly created entities that match, instead of every step */
    KILN_PROGRAM_HOST_ONLY = 0x0010u, /* not in a client's prediction world. On a dedicated server there is none; set it
                                         anyway for server logic */
    KILN_PROGRAM_EXCLUSIVE = 0x0080u, /* its own exclusive stage (stage scheduler) */
    KILN_PROGRAM_CHUNKED = 0x0200u,   /* one task per archetype chunk (more parallel) instead of one task for all */
    KILN_PROGRAM_PINNED = 0x0400u,    /* task affinity 1 (the engine's main worker); does not stop other programs */
    KILN_PROGRAM_BARRIER = 0x0800u,   /* conflicts with every program: runs after all earlier programs, alone */
};

/* Per-entity update (KilnProgramDesc.each): kiln.world runs the iteration and calls you once per entity of the slice
 * (entity = its id, iter slots point at its components), or once per step with entity 0 for a SYSTEM program. A
 * fault in your callback disables the program (logged); the iteration is still completed, so the engine never sees a
 * partial slice. */
typedef void(KILN_CALL* KilnProgramEachFn)(KilnProgramCtx* ctx, uint64_t* iter, KilnEntity entity, void* user);

/* Raw update (KilnProgramDesc.update): called once per slice; you call iter_begin / iter_next yourself and must run
 * iter_next until it returns false (or never call it). Stopping early is fatal in the engine; kiln.world repairs that
 * only after a fault in your callback (and disables the program). */
typedef void(KILN_CALL* KilnProgramUpdateFn)(KilnProgramCtx* ctx, void* user);

typedef struct KilnProgramDesc {
    uint32_t size;  /* sizeof(KilnProgramDesc) */
    uint32_t flags; /* KILN_PROGRAM_* */
    KilnStr name;   /* unique in the world; prefix it with your service: "kiln_items_grant" */
    KilnProgramEachFn each;      /* exactly one of each / update */
    KilnProgramUpdateFn update;
    void* user;
    /* Components of the iterated entity. tpl = template (read-only, per archetype); read / write = per-entity data;
     * *_optional = may be absent (slot NULL). Required lists also filter: only entities that have them are iterated.
     * An entity query with no list at all matches EVERY entity: use KILN_PROGRAM_SYSTEM if you iterate nothing. */
    KilnNames tpl, tpl_optional;
    KilnNames read, read_optional;
    KilnNames write, write_optional; /* call mark_written before writing through a write slot */
    /* Singletons (world objects). */
    KilnNames singletons_read, singletons_write;
    uint64_t singletons_read_optional;  /* bit k: singletons_read.items[k] may be missing (slot NULL) */
    uint64_t singletons_write_optional; /* bit k: singletons_write.items[k] may be missing */
    /* Random access to OTHER entities' components (read_other). other_write makes the program serial. */
    KilnNames other_tpl, other_read, other_write;
    /* Archetype filters (template-level component names): must have all of `with`, none of `without`, and at least one
     * of `any_of` (when not empty). No data access. */
    KilnNames with, without, any_of;
    /* Events the program receives / sends (short names). */
    KilnNames events_in, events_out;
} KilnProgramDesc;

/* program_slot kinds. The iterator is an array of qwords:
 *   [0] ctx | TPL | READ | WRITE (6 qwords each) | SINGLETON | OTHER_TPL | OTHER_READ, OTHER_WRITE | EVENT_IN |
 *   EVENT_OUT
 * Inside TPL, READ and WRITE the engine orders components by type index (ascending "keen::ecs::<name>", byte
 * compare), required and optional mixed; OTHER_TPL, OTHER_READ and OTHER_WRITE likewise each; singletons are in
 * descriptor order (read list, then write list). program_slot does this arithmetic for you. */
enum {
    KILN_SLOT_TPL = 0,         /* iter[slot] = template data of the current entity's archetype (NULL if optional+absent) */
    KILN_SLOT_READ = 1,        /* iter[slot] = the current entity's component (NULL if optional+absent) */
    KILN_SLOT_WRITE = 2,       /* iter[slot .. slot+5] = write record; iter[slot] = data; see mark_written */
    KILN_SLOT_SINGLETON = 3,   /* iter[slot] = singleton object (NULL if optional+missing) */
    KILN_SLOT_OTHER_TPL = 4,   /* iter[slot] = other-entity template accessor */
    KILN_SLOT_OTHER_READ = 5,  /* iter[slot] = other-entity accessor; use read_other */
    KILN_SLOT_OTHER_WRITE = 6, /* iter[slot] = other-entity write accessor (writes: not in v1) */
    KILN_SLOT_EVENT_IN = 7,    /* iter[slot] = event queue; only resolved when events_in has one entry */
    KILN_SLOT_EVENT_OUT = 8,   /* iter[slot] = event sender */
};

/* KilnProgramInfo.state */
enum {
    KILN_PROGRAM_PENDING = 0,  /* accepted; waits for the server world's program registration */
    KILN_PROGRAM_ACTIVE = 1,   /* registered into the server world (runs once the world ticks) */
    KILN_PROGRAM_REFUSED = 2,  /* not registered: unknown name, table full, too late, ... (reason in kiln.log) */
    KILN_PROGRAM_FAULTED = 3,  /* disabled after a fault in its callback */
    KILN_PROGRAM_SKIPPED = 4,  /* registered, but the engine skips it (a required singleton is missing) */
};

typedef struct KilnProgramInfo {
    uint32_t size;        /* set to sizeof(KilnProgramInfo) before calling */
    uint32_t state;       /* KILN_PROGRAM_* state */
    uint32_t engine_id;   /* the program's id in the world (1-based; 0 before registration) */
    uint32_t iter_size;   /* bytes of the iterator (kiln.world passes one at least this big to `each`) */
    uint64_t updates;     /* update calls so far (slices; one per step for SYSTEM programs) */
    uint64_t entities;    /* entities iterated so far (each-mode only) */
} KilnProgramInfo;

/* ======================================================================================= entities ============== */

typedef struct KilnTransform {
    KilnVec3 position;    /* metres */
    float rotation[4];    /* quaternion x, y, z, w */
    float scale[3];
    float reserved;
} KilnTransform;

/* ======================================================================================= teleport ============== */

/* KilnTeleportRequest.flags */
enum {
    KILN_TELEPORT_FIND_GROUND = 0x01u, /* the game's ground search: the nearest walkable path-grid cell within about 10 m
                                          (up or down) of the target; none found = the raw target, silently
                                          (KILN_TELEPORT_RAW_TARGET). Waits for the destination to stream in */
    KILN_TELEPORT_FACE_YAW = 0x02u,    /* turn to yaw_deg about +Y; otherwise keep the current orientation */
    KILN_TELEPORT_CHECKPOINT = 0x04u,  /* players: also a replication checkpoint, as fast travel sets one */
};

/* KilnTeleportResult.status */
enum {
    KILN_TELEPORT_FAILED = 0, /* not moved: refused when checked, or given up (reason says why) */
    KILN_TELEPORT_MOVED = 1,  /* moved: position is where the entity is now */
};

/* KilnTeleportResult.flags */
enum {
    KILN_TELEPORT_OFF_TARGET = 0x01u, /* ended more than 1 m from the target (25 m with spawn_point) */
    KILN_TELEPORT_RAW_TARGET = 0x02u, /* FIND_GROUND found nothing: the entity stands exactly at the target */
    KILN_TELEPORT_TIMED_OUT = 0x04u,  /* FAILED after 10 s without a move; the stuck Teleport component was removed */
};

typedef struct KilnTeleportResult {
    uint32_t size;      /* sizeof(KilnTeleportResult) of kiln.world */
    KilnEntity entity;
    uint64_t request;   /* the id teleport() returned */
    uint32_t status;    /* KILN_TELEPORT_FAILED / _MOVED */
    uint32_t flags;     /* KILN_TELEPORT_OFF_TARGET | _RAW_TARGET | _TIMED_OUT */
    KilnVec3 position;  /* where the entity is now (FAILED: last known position, or 0) */
    KilnVec3 target;    /* what was asked (spawn_point: that entity's position) */
    double distance;    /* metres between position and target */
    uint32_t steps;     /* sim steps from the request to this result */
    uint32_t reserved;
    KilnStr reason;     /* FAILED: why, in words; "" otherwise. Valid during the callback */
} KilnTeleportResult;

/* Runs exactly once per accepted request, on the tick thread in Pre (game state may be touched). */
typedef void(KILN_CALL* KilnTeleportFn)(const KilnTeleportResult* result, void* user);

typedef struct KilnTeleportRequest {
    uint32_t size;          /* sizeof(KilnTeleportRequest) */
    KilnEntity entity;      /* a player, villager, animal or creature: needs CurrentTransform, LastTransform and
                               TeleportCount (props, chests, altars have none: FAILED) */
    KilnVec3 target;        /* metres, world space, y up. x/z within -1000..12000, y within -1000..5000. Ignored with
                               spawn_point */
    float yaw_deg;          /* with KILN_TELEPORT_FACE_YAW */
    KilnEntity spawn_point; /* optional: an entity with PlayerSpawnPoint (flame altar, spire): the game lands the entity
                               on one of its landing pads, as when respawning there */
    uint32_t flags;         /* KILN_TELEPORT_* */
    uint32_t reserved;      /* 0 */
    KilnTeleportFn callback; /* may be NULL */
    void* user;
} KilnTeleportRequest;

/* ======================================================================================= status ================ */

typedef struct KilnWorldStatus {
    uint32_t size;            /* set to sizeof(KilnWorldStatus) before calling */
    uint8_t programs;         /* 1: program registration works on this build */
    uint8_t world;            /* 1: a server world exists and its programs ran in the last step */
    uint8_t teleport;         /* 1: teleport works on this build */
    uint8_t find;             /* 1: find / entity_count work on this build */
    uint32_t kiln_programs;   /* programs registered through kiln.world (incl. its own) */
    uint32_t engine_programs; /* programs in the server world's table */
    uint32_t engine_max;      /* the table's capacity */
    uint32_t teleports_pending;
    uint64_t world_steps;     /* server-world steps in which kiln.world's own program ran */
} KilnWorldStatus;

/* ======================================================================================= the table ============= */

typedef struct KilnWorldApi {
    KilnServiceHeader header; /* { sizeof(KilnWorldApi), KILN_WORLD_VERSION } */

    /* ---- programs ---- */
    /* Accepts a program for the server world. *out = its handle (for program_slot / program_info). Copies the
     * descriptor. Main thread, before the world finalizes (kiln_plugin_main / on_ready). Refusals (bad flags, bad
     * names, table full) are logged; names are checked when the world registers its programs, so a program can be
     * accepted here and still end up KILN_PROGRAM_REFUSED: check program_info after the first step if you care. */
    KilnResult(KILN_CALL* register_program)(KilnOwner owner, const KilnProgramDesc* desc, KilnHandle* out);
    /* Qword index into the iterator of `name` in list `kind` (KILN_SLOT_*), or -1 when it is not declared there. The
     * layout is computed from the descriptor at registration and is the same for every update. Any thread. */
    int32_t(KILN_CALL* program_slot)(KilnHandle program, int32_t kind, KilnStr name);
    KilnResult(KILN_CALL* program_info)(KilnHandle program, KilnProgramInfo* out);

    /* ---- inside a program update (ctx = the argument your update got) ---- */
    /* Raw mode only: fills `iter` (size bytes; see program_info.iter_size). KILN_ERR_BUFFER_TOO_SMALL if size is less. */
    KilnResult(KILN_CALL* iter_begin)(KilnProgramCtx* ctx, uint64_t* iter, uint32_t size);
    /* Raw mode only: advances to the next entity of the slice; false at the end. */
    bool(KILN_CALL* iter_next)(KilnProgramCtx* ctx, uint64_t* iter, uint32_t size);
    /* The entity iter_next stopped at (0 before the first iter_next and in SYSTEM programs). */
    KilnEntity(KILN_CALL* current_entity)(KilnProgramCtx* ctx);
    /* The slice's command buffer (opaque; exclusive to this update while it runs). Applied by the engine at the end of
     * the sim step, in program order. */
    void*(KILN_CALL* command_buffer)(KilnProgramCtx* ctx);
    /* Reserves a zeroed record of `size` bytes (16-byte header included: +0 u16 size, +2 u8 kind, +4 u16 program id,
     * +8 u32 entity, payload from +0xC) in the command buffer; NULL when the buffer is full (retry next step). Known
     * kinds: 3 add components, 4 remove components, 0xF streaming hot point, 0x10 replication checkpoint. */
    void*(KILN_CALL* queue_push)(KilnProgramCtx* ctx, uint8_t kind, uint32_t size, KilnEntity entity);
    /* Kind-3 record: adds component `hash` with `size` bytes of `data` to `entity` at the end of the step. If the
     * entity already has the component the engine keeps the old data. size <= 4096. */
    KilnResult(KILN_CALL* queue_add)(KilnProgramCtx* ctx, KilnEntity entity, uint32_t hash, const void* data,
                                     uint32_t size);
    /* Kind-4 record: removes component `hash` from `entity` at the end of the step. */
    KilnResult(KILN_CALL* queue_remove)(KilnProgramCtx* ctx, KilnEntity entity, uint32_t hash);
    /* Marks the component behind a write slot changed (replication to clients, saving) before you write through it:
     * write_record = &iter[program_slot(..., KILN_SLOT_WRITE, name)]. */
    void(KILN_CALL* mark_written)(KilnProgramCtx* ctx, const uint64_t* write_record);
    /* Another entity's component through a declared other-read accessor: iter[slot] with slot from
     * program_slot(KILN_SLOT_OTHER_READ, name). NULL if the entity has none. Read only; valid during the update. */
    const void*(KILN_CALL* read_other)(KilnProgramCtx* ctx, uint64_t accessor, KilnEntity entity, uint32_t* out_size);

    /* ---- entities ---- */
    /* FNV-1a-32 of "keen::ecs::" + name (as given if it contains "::"). Any thread. */
    uint32_t(KILN_CALL* component_hash)(KilnStr name);
    /* The entity's per-entity component data, or NULL (no such entity or component). *out_size = its size. The
     * pointer is game memory: read it in the same callback only. */
    const void*(KILN_CALL* component)(KilnEntity entity, uint32_t hash, uint32_t* out_size);
    bool(KILN_CALL* exists)(KilnEntity entity);
    /* CurrentTransform in metres. KILN_ERR_NOT_FOUND: no such entity or it has no transform. */
    KilnResult(KILN_CALL* transform)(KilnEntity entity, KilnTransform* out);
    KilnResult(KILN_CALL* position)(KilnEntity entity, KilnVec3* out);
    /* Every entity that has per-entity component `hash`, in no particular order. *out_count = how many there are; when
     * that is more than capacity, the first `capacity` are written and KILN_ERR_BUFFER_TOO_SMALL is returned (call
     * again with a bigger buffer). KILN_ERR_NOT_FOUND: no such component type in this world. Cost: one pass over
     * the entity map (measured 0.43 ms for 2,800 entities, including a one-time cross-check with GetComponent). */
    KilnResult(KILN_CALL* find)(uint32_t hash, KilnEntity* out, uint32_t capacity, uint32_t* out_count);
    uint32_t(KILN_CALL* entity_count)(void);
    /* Advanced: the server world object (0 when there is none). Layout depends on the game build. */
    uintptr_t(KILN_CALL* server_world)(void);

    /* ---- teleport ---- */
    /* Queues a teleport. *out_request = its id (never 0). KILN_ERR_INVALID_ARGUMENT for bad numbers/flags,
     * KILN_ERR_UNAVAILABLE when teleport does not work on this build or no server world runs. Everything else
     * (missing components, a dead entity, a teleport already in flight for it) comes back as FAILED through the
     * callback. It moves within 2-3 steps (longer while FIND_GROUND waits for terrain). Any thread. */
    KilnResult(KILN_CALL* teleport)(KilnOwner owner, const KilnTeleportRequest* request, uint64_t* out_request);
    /* Drops the request's callback (the move may still happen). KILN_ERR_NOT_FOUND when it already completed. */
    KilnResult(KILN_CALL* teleport_cancel)(uint64_t request);

    KilnResult(KILN_CALL* status)(KilnWorldStatus* out);
} KilnWorldApi;

#ifdef __cplusplus
}
#endif
#endif
