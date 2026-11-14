/* SPDX-License-Identifier: MIT
 * kiln.world v4: the server world's ECS for other services and mods.
 *   1. ECS programs: register your own program (system) into the game's scheduler, with declared component,
 *      singleton and event access, and helpers to use inside its update (iterator, command buffer, other entities).
 *   2. Entity access on the tick thread: component lookup by name/hash, transform and position in metres, "all
 *      entities that have component X", entity count.
 *   3. Teleport: moves an entity the way the game's own fast travel does (the engine's Teleport component).
 *   4. Tracked writes (v3): changes to a component of any entity through the game's own write tracking, so the
 *      game's save and its replication to players see them (write_submit; docs/re/world.md "Tracked writes").
 *   5. Game programs (v4): the game's own ECS programs of every world, by name, and the threads they run on
 *      (game_program_*, below).
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
#define KILN_WORLD_VERSION 4u

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
    KILN_SLOT_OTHER_WRITE = 6, /* iter[slot] = other-entity write accessor; use write_other */
    KILN_SLOT_EVENT_IN = 7,    /* iter[slot] = event queue, in event type-index order */
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

/* ======================================================================================= tracked writes ======== */
/* Since 0.2.0-dev.27 (v3). A raw store into component memory (the pointer `component` returns) is invisible to the
 * game: the save skips it on an entity it has already written and players may never be sent it. A tracked write goes
 * through the engine's own WriteOther with the job's tracker, inside one Kiln system program (kiln_world_writes) that
 * kiln.world registers when at least one loaded mod lists a component under "writes" in its kiln.mod.json. The
 * engine fixes a program's access when the world starts, so only components listed at boot can be written; a new
 * entry needs a restart. The program is appended after the game's own programs and conflicts with every program
 * that reads or writes one of its components, so its write is the last of the step for that component. */

/* KilnWorldWriteResult.status */
enum {
    KILN_WORLD_WRITE_DONE = 0,          /* written through the game's tracked path (what that marks: the flags) */
    KILN_WORLD_WRITE_UNCHANGED = 1,     /* the component already held these bytes: nothing written, nothing marked */
    KILN_WORLD_WRITE_NO_COMPONENT = 2,  /* the entity does not exist or has no such component */
    KILN_WORLD_WRITE_OUT_OF_RANGE = 3,  /* the bytes do not fit the live component (its size is in the result) */
    KILN_WORLD_WRITE_NOT_DECLARED = 4,  /* the component is not in the write program's list for this mod (write_info
                                           says which case; the reason says what to add) */
    KILN_WORLD_WRITE_REFUSED = 5,       /* the change callback returned false, or an attribute's header would change */
    KILN_WORLD_WRITE_FAULTED = 6,       /* the change callback or the engine's write faulted; the component is as it was */
    KILN_WORLD_WRITE_DROPPED = 7,       /* never ran: no server world, the write program is off, or the owner unloaded */
};

/* KilnWorldWriteRequest.flags */
enum {
    KILN_WORLD_WRITE_WHOLE = 0x01u,   /* data is the whole component: its length must equal the live size */
    KILN_WORLD_WRITE_VERIFY = 0x02u,  /* for a type the game's own programs write too: hold the result one more step
                                         and say whether they left the bytes as written (KILN_WORLD_WROTE_CHECKED,
                                         _KEPT). A type with no other writer is answered at once, unchecked */
};

/* KilnWorldWriteResult.flags and KilnWorldWriteInfo.flags */
enum {
    KILN_WORLD_WROTE_GAME_WRITES = 0x01u, /* programs of the game write this component type too, every step they run
                                             (`writers` says how many); Kiln's write lands after them in the step */
    KILN_WORLD_WROTE_BITS_KNOWN = 0x02u,  /* SAVED and REPLICATED below were read on this build (else: not known) */
    KILN_WORLD_WROTE_SAVED = 0x04u,       /* the game's write tracking records changes of this type for the save: the
                                             write marked the entity, and the next save writes it. Clear: no write of
                                             this type marks anything, the game's own included */
    KILN_WORLD_WROTE_REPLICATED = 0x08u,  /* ... and for replication. Clear does not say players never get the value:
                                             the game sends many through another component it derives from this one */
    KILN_WORLD_WROTE_ATTRIBUTE = 0x10u,   /* an attribute (Health, Fuel, ...): the game's formulas ran after the write */
    KILN_WORLD_WROTE_CHECKED = 0x20u,     /* result only: VERIFY ran one step later */
    KILN_WORLD_WROTE_KEPT = 0x40u,        /* result only, with CHECKED: the written bytes were still there */
    KILN_WORLD_INFO_NOT_READY = 0x80u,    /* write_info only: what the running server world knows of the type is not
                                             published yet (no world runs, or a new one is being set up): writers,
                                             size and bits are not filled. The state is. Ask again after a step */
};

typedef struct KilnWorldWriteResult {
    uint32_t size;            /* sizeof(KilnWorldWriteResult) of kiln.world */
    uint32_t status;          /* KILN_WORLD_WRITE_* status */
    uint64_t ticket;          /* the id write_submit returned */
    KilnEntity entity;
    uint32_t component;       /* component hash */
    uint32_t offset, length;  /* the bytes the request named (a change callback: 0 and the component's size) */
    uint32_t component_size;  /* the live component's size; 0 when it was not found */
    uint32_t flags;           /* KILN_WORLD_WROTE_* */
    uint32_t steps;           /* sim steps from the request to this result */
    uint32_t writers;         /* game programs that write this component type */
    const void* before;       /* `length` bytes the component held before; NULL when nothing was read */
    const void* after;        /* `length` bytes it holds after the write (DONE: after the game's formulas for an
                                 attribute); NULL when nothing was read. Both valid during the callback */
    KilnStr reason;           /* why not, in words; "" for DONE and UNCHANGED. Valid during the callback */
} KilnWorldWriteResult;

/* Runs exactly once per accepted request, on the tick thread in Pre (game state may be touched). */
typedef void(KILN_CALL* KilnWorldWriteFn)(const KilnWorldWriteResult* result, void* user);

/* Read-modify-write. Runs inside the write program, in the sim step: the tick thread on a dedicated server, maybe a
 * game worker thread on a client host. `data` is a copy of the whole component (`size` bytes) taken after the
 * game's own programs of this step wrote it; edit it in place and return true to have it written, false to leave
 * the component alone (KILN_WORLD_WRITE_REFUSED). Touch nothing else of the game here. */
typedef bool(KILN_CALL* KilnWorldChangeFn)(KilnEntity entity, void* data, uint32_t size, void* user);

typedef struct KilnWorldWriteRequest {
    uint32_t size;              /* sizeof(KilnWorldWriteRequest) */
    uint32_t flags;             /* KILN_WORLD_WRITE_WHOLE | _VERIFY */
    KilnEntity entity;
    uint32_t component;         /* component hash (component_hash) */
    uint32_t offset, length;    /* the bytes to replace; ignored with `change` */
    const void* data;           /* `length` bytes, copied by write_submit; NULL with `change`. length <= 4096 */
    KilnWorldChangeFn change;   /* optional: read-modify-write instead of `data` */
    KilnWorldWriteFn callback;  /* may be NULL */
    void* user;                 /* for both callbacks */
    KilnStr mod_id;             /* the mod whose manifest must list the component; empty: any listed component
                                   (native services, which have no manifest) */
} KilnWorldWriteRequest;

/* KilnWorldWriteInfo.state: can this component be written right now, for this mod? */
enum {
    KILN_WORLD_WRITES_READY = 0,         /* yes */
    KILN_WORLD_WRITES_PENDING = 1,       /* listed, and the write program waits for a server world (which then says
                                            whether the name is a component and within the program's 92) */
    KILN_WORLD_WRITES_NOT_DECLARED = 2,  /* no loaded mod listed it at boot */
    KILN_WORLD_WRITES_NOT_LISTED = 3,    /* another mod listed it, mod_id did not */
    KILN_WORLD_WRITES_UNKNOWN = 4,       /* listed, but the world has no component type of that name */
    KILN_WORLD_WRITES_OVER_LIMIT = 5,    /* listed, but the program's accessor limit was reached before it (names the
                                            world does not have take no place) */
    KILN_WORLD_WRITES_OFF = 6,           /* tracked writes do not work on this build, or the program was refused,
                                            faulted or is skipped by the engine (kiln.log says which) */
    KILN_WORLD_WRITES_LIST_REFUSED = 7,  /* the mod's "writes" could not be read at boot (not a list, a name that is not
                                            a component's, over 64 entries): the mod loaded, none of its writes is
                                            taken; the reason says what is wrong */
};

typedef struct KilnWorldWriteInfo {
    uint32_t size;            /* set to sizeof(KilnWorldWriteInfo) before calling */
    uint32_t state;           /* KILN_WORLD_WRITES_* */
    uint32_t flags;           /* KILN_WORLD_WROTE_GAME_WRITES | _BITS_KNOWN | _SAVED | _REPLICATED | _ATTRIBUTE */
    uint32_t writers;         /* game programs that write this component type each step they run */
    uint32_t init_writers;    /* game programs that write it only when an entity is created (INIT programs) */
    uint32_t component_size;  /* the reflected size (kiln.types); 0 when not known */
    char writer_names[232];   /* the first of them, ", " separated, NUL terminated ("fuel_setup, inventory_actions") */
} KilnWorldWriteInfo;

typedef struct KilnWorldWriteStatus {
    uint32_t size;       /* set to sizeof(KilnWorldWriteStatus) before calling */
    uint32_t program;    /* KILN_PROGRAM_* state of kiln_world_writes; KILN_PROGRAM_PENDING until the first server
                            world registers its programs (the program is made then, when something is listed) */
    uint8_t available;   /* 1: the engine's tracked write path resolved on this build */
    uint8_t running;     /* 1: the program ran in the last step */
    uint8_t attributes;  /* 1: attribute formulas can be re-run after a write */
    uint8_t bits;        /* 1: the per-component save and replication bits can be read */
    uint32_t declared;   /* component types in the program's write list */
    uint32_t refused;    /* names listed by mods that are not in it (unknown, over the limit) */
    uint32_t pending;    /* requests waiting for the program or for their check */
    uint32_t reserved;
    uint64_t submitted, written, unchanged, failed;
    uint64_t updates;    /* updates of the program so far */
} KilnWorldWriteStatus;

/* ======================================================================================= game programs (v4) ===== */
/* Since 0.2.0-dev.28 (v4). The game's own ECS programs, per world kind: a directory of each world's program table
 * (game_program_find, game_program_info) and what decides the threads they run on (game_programs_status).
 *
 * World kinds (KILN_WORLD_*): the SERVER world (a dedicated server's, or a host's) and the CLIENT world each have a
 * program table of their own. PREDICTION has none: it runs the client world's table. Names are unique in a table and
 * are the key that lasts: an index moves when the game (or a mod) inserts programs, and between game builds.
 * The directory of a world is read in its first Pre, after the world finalized its programs; until then, and after
 * the world stopped, game_program_find answers -1.
 *
 * Threads: a server world runs its program slices inline on its own thread while no player is in it, and as tasks on
 * the game's workers once one is (SYSTEM, CHUNKED and PINNED programs stay on the world's thread); the client world
 * always uses tasks. KilnGameProgramsStatus.players_mask is what the server world's scheduler reads. */

/* KilnGameProgramInfo.flags (Kiln's words, not the engine's) */
enum {
    KILN_GAME_PROGRAM_GAME = 0x01u,      /* the game's own program (its descriptor is in the game's image); clear: a
                                            program Kiln or another mod registered */
    KILN_GAME_PROGRAM_NO_UPDATE = 0x02u, /* it has no update in this world (a client program in a server world) */
};

typedef struct KilnGameProgramInfo {
    uint32_t size;         /* set to sizeof(KilnGameProgramInfo) before calling */
    uint32_t world_kind;   /* KILN_WORLD_* of the table */
    int32_t index;         /* position in the world's table, from 0 */
    uint16_t id;           /* the engine's id (index + 1) */
    uint16_t reserved;
    KilnStr name;          /* "fuel_setup"; valid for the life of the process */
    uint32_t desc_flags;   /* the descriptor's flags (0x1 client program, 0x4 SYSTEM, 0x8 INIT, 0x200 CHUNKED, ...) */
    uint32_t entry_flags;  /* the table entry's flags: the descriptor's plus engine bits (0x20 skipped, ...) */
    uint64_t update_rva;   /* the update the table names, relative to the game's image base; 0 when there is none in
                              this world, or it is outside the game's image (a mod's program) */
    uint32_t flags;        /* KILN_GAME_PROGRAM_* */
    uint32_t wraps;        /* wraps active on it in this world now */
    uint32_t faults;       /* wrap stages of it that faulted since this world started */
    uint32_t reserved2;
    uint64_t slices;       /* slices of it that kiln.world's wrap dispatched, since this world started */
    uint64_t entities;     /* entities in those slices */
    uint64_t forwarded;    /* slices kiln.world's trampoline let through to the update untouched (a prediction slice
                              through a client-world wrap, or no stage left) */
} KilnGameProgramInfo;

typedef struct KilnGameProgramsStatus {
    uint32_t size;                         /* set to sizeof(KilnGameProgramsStatus) before calling */
    uint8_t directory[KILN_WORLD_KINDS];   /* 1: the directory of that world kind's table is published */
    uint8_t threads;                       /* 1: the server world's players mask can be read on this build (sigdb
                                              feature world.systems; capability systems.threads) */
    uint32_t programs[KILN_WORLD_KINDS];   /* entries in that kind's table directory */
    uint32_t game_programs[KILN_WORLD_KINDS];  /* of them, the game's own */
    uint32_t no_update[KILN_WORLD_KINDS];  /* of the game's own, those with no update in that world */
    uint16_t players_mask;                 /* the server world's players mask at its last Pre: bit (id - 1) mod 16 per
                                              live player entity; non-zero = its entity programs run on the workers.
                                              0xFFFF when not known */
    uint8_t parallel_forced;               /* 1: the lab switch (lab_parallel) holds bit 15 now */
    uint8_t parallel_switch;               /* 1: the lab switch is enabled in this process */
    uint32_t parallel_steps_left;          /* steps until the lab switch lets go by itself */
    uint32_t wraps_active, wraps_pending;  /* wraps in all worlds */
    char detail[160];                      /* why something is off, in words, NUL-terminated; "" when all works */
} KilnGameProgramsStatus;

/* ---- wraps (game_program_wrap) ----
 * A wrap is one stage of Kiln's code on one program of one world kind's table: BEFORE the game's update, AFTER it, or
 * REPLACE (instead of it; at most one per program). Stages run per slice (one archetype group's range of entities),
 * in the program's own slot, on the thread that runs the slice: the world's thread or a game task worker; two slices
 * of one program can run at once. Befores run high priority first (ties: in the order they were made), the update or
 * the Replace, then the Afters in exactly the reverse order (an onion).
 *
 * Mechanism: kiln.world swaps the program's update pointer in its table entry (entry+0x28, what the engine's program
 * runner calls for each slice) for a trampoline of its own, in that world's Pre, and puts the game's pointer back by
 * compare-exchange when the last stage goes. Another tool's pointer is never stacked on: a program whose update is
 * not its own is refused (KILN_WRAP_FOREIGN), and a trampoline another tool swapped over keeps forwarding.
 * Code that names a program by its update pointer (kiln.types' system switches and profiler) asks wrap_original.
 *
 * Inside a stage: per-slice state only, shared state through atomics; no tick-thread API (kiln.world's own entity,
 * write and wrap calls answer KILN_ERR_WRONG_THREAD there). Write only what the program declares, mark what you write.
 * Leave program_ctx's iteration as you found it; kiln.world puts the index right when a stage faulted mid-way:
 * 0 before the update, the slice's count after it. A Replace that wants the game's update calls
 * game_slice_run_original, once. A stage that faults (a structured exception) or overruns its budget (1 ms per slice)
 * 10 slices in a row is disabled, alone; the slice goes on.
 *
 * Lifetime: install is queued and done in the target world's next Pre (state PENDING -> ACTIVE); an unwrap stops new
 * calls at once and takes the stage out of the chain in the next Pre (-> REMOVED). When the world stops, its wraps go back to PENDING and are applied again, by program name, in the next
 * world of that kind. A server world runs its own wraps; a CLIENT-world wrap needs capability systems.wraps.client;
 * prediction slices that pass a client-world trampoline go to the game's update untouched. While native SET is loaded
 * (kiln.types' external_runner), every wrap is refused and the live ones are removed in the next Pre. */
enum {
    KILN_WRAP_BEFORE = 0,
    KILN_WRAP_REPLACE = 1,
    KILN_WRAP_AFTER = 2,
};

/* KilnGameWrapInfo.state */
enum {
    KILN_WRAP_PENDING = 0, /* queued: applied in the target world's next Pre (or the next world's, by name) */
    KILN_WRAP_ACTIVE = 1,  /* in the program's chain (see `disabled`) */
    KILN_WRAP_REMOVED = 2, /* out for good: unwrapped, or `reason` */
    KILN_WRAP_REFUSED = 3, /* never applied: `reason` */
};

/* KilnGameWrapInfo.reason and .disabled */
enum {
    KILN_WRAP_OK = 0,
    KILN_WRAP_FAULT = 1,        /* disabled: the stage faulted (fault_code) */
    KILN_WRAP_BUDGET = 2,       /* disabled: over its budget 10 slices in a row */
    KILN_WRAP_NATIVE_SET = 3,   /* removed: native SET is loaded */
    KILN_WRAP_FOREIGN = 4,      /* refused: another tool swapped the program's update (detail names its module) */
    KILN_WRAP_NO_PROGRAM = 5,   /* refused: the world has no program of that name (a later world) */
    KILN_WRAP_NO_UPDATE = 6,    /* refused: the program has no update in that world */
    KILN_WRAP_POOL_FULL = 7,    /* refused: every trampoline of that world kind is in use */
    KILN_WRAP_UNWRAPPED = 8,    /* removed: game_program_unwrap or game_wraps_release */
    KILN_WRAP_OWNER_GONE = 9,   /* removed: its owner was revoked */
    KILN_WRAP_REPLACE_TAKEN = 10, /* refused: another wrap replaces the program (detail names its owner and handle) */
};

/* What a stage gets: valid during the call only. */
typedef struct KilnGameSlice {
    uint32_t size;               /* sizeof(KilnGameSlice) of kiln.world */
    uint32_t world_kind;         /* the wrap's world kind */
    KilnProgramCtx* program_ctx; /* the engine's ProgramContext the update gets */
    uint32_t count;              /* entities in the slice (0 for a SYSTEM program) */
    uint32_t thread_is_world;    /* 1: on the world's own thread; 0: a game task worker */
    const uint32_t* entity_ids;  /* the first entity's id; the next one is entity_stride bytes further. NULL when
                                    count is 0. Read only */
    uint32_t entity_stride;      /* bytes between two ids (16) */
    int32_t program_index;       /* in the world's table */
    uint64_t step;               /* the world's step (KilnTickInfo.step of its last Pre) */
    KilnStr program;             /* the program's name */
    int32_t stage;               /* KILN_WRAP_BEFORE / _REPLACE / _AFTER: the stage being called */
    uint32_t reserved;
    void* internal;              /* kiln.world's */
} KilnGameSlice;

/* A stage; runs under kiln.world's structured-exception guard. */
typedef void(KILN_CALL* KilnWrapStageFn)(KilnGameSlice* slice, void* user);

typedef struct KilnGameWrapInfo {
    uint32_t size;          /* set to sizeof(KilnGameWrapInfo) before calling */
    uint32_t state;         /* KILN_WRAP_PENDING / _ACTIVE / _REMOVED / _REFUSED */
    uint32_t disabled;      /* KILN_WRAP_OK, or why an ACTIVE stage is no longer called (KILN_WRAP_FAULT, _BUDGET) */
    uint32_t reason;        /* why REMOVED or REFUSED */
    uint32_t world_kind;
    int32_t stage;
    int32_t priority;
    int32_t program_index;  /* in the current world's table; -1 while not ACTIVE */
    KilnStr program;        /* valid for the life of the process */
    uint64_t calls;         /* slices it ran in */
    uint64_t entities;      /* entities in those slices */
    uint64_t faults, overruns;
    uint64_t total_ns;      /* time spent in the stage */
    uint32_t threads;       /* distinct threads it ran on (counted up to 16) */
    uint32_t fault_code;    /* the exception code of its fault */
    uint8_t remove_queued;  /* 1: unwrapped, still in its program's chain until the next Pre (no call starts) */
    uint8_t gone;           /* 1: no call of the stage starts any more (unwrapped, released, owner revoked) */
    uint16_t reserved;
    uint32_t running;       /* calls of the stage running now (with gone: the last ones finishing) */
    char detail[128];       /* in words, NUL-terminated: why it was refused, removed or disabled */
} KilnGameWrapInfo;

/* ======================================================================================= the table ============= */

typedef void(KILN_CALL* KilnProgramEventFn)(const void* data, uint32_t size, void* user);

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
    /* Appended in v1: tracked other-entity writes. The accessor MUST come from this program's OTHER_WRITE slot.
     * Calls the engine's WriteOther with the job tracker and write flag, so replication/save dirtiness is recorded
     * before returning the mutable component. NULL: absent, undeclared, wrong callback, or unavailable build. */
    void*(KILN_CALL* write_other)(KilnProgramCtx* ctx, uint64_t accessor, KilnEntity entity, uint32_t* out_size);
    /* Disable the callback permanently; engine descriptors remain allocated. Tick thread (or before world setup).
     * After success no callback is in flight. Required before freeing callback/user memory or unloading code. */
    KilnResult(KILN_CALL* unregister_program)(KilnHandle program);
    /* v2: bind the inert descriptor reserved from <mod>/kiln.programs.json at boot. Same lists survive callback
     * unload/reload; edited/new declarations need a restart. Mod identity and key are copied. Tick thread only. */
    KilnResult(KILN_CALL* bind_program)(KilnOwner owner, KilnStr mod_id, KilnStr key, KilnProgramEachFn each,
                                       void* user, KilnHandle* out);
    /* KILN_WORLD_CAP_*; any thread. Check this as well as the table extent. */
    uint32_t(KILN_CALL* capabilities)(void);
    /* v2: all events of the declared input type, in queue order. Copies the complete bounded batch before callbacks.
     * The event bytes last for the callback only. Inside this program's update only. */
    KilnResult(KILN_CALL* program_events)(KilnProgramCtx* ctx, KilnStr name, KilnProgramEventFn callback, void* user);
    /* v2: copy an exact reflected payload into a declared output stream; GameEvent timestamps use program time. */
    KilnResult(KILN_CALL* program_emit)(KilnProgramCtx* ctx, KilnStr name, const void* data, uint32_t size);

    /* ---- tracked writes: appended in v3, since 0.2.0-dev.27. Check header.size and KILN_WORLD_CAP_TRACKED_WRITES ---- */
    /* Queues one write. It runs in the write program's next update (the next sim step; one update runs 512, the rest
     * wait for the next ones) and its result comes one Pre later (two with KILN_WORLD_WRITE_VERIFY; one Pre delivers
     * 1024 results at most). *out_ticket = its id (never 0). Tick thread, outside any program update
     * (KILN_ERR_WRONG_THREAD otherwise): the entity is read as it is between two steps.
     * A request belongs to the server world that runs when it is submitted: before the first one it is DROPPED, and
     * one that has not run when another world registers is DROPPED (a held one completes unchecked). It also carries
     * its entity's generation, the entity's SpawnTime (entities the game or Kiln.Spawn spawns into the world have one;
     * entities loaded from the save and item entities do not, players may not), compared right before the write:
     * another value means the id names
     * another entity now, and the answer is NO_COMPONENT. A request for an entity without a SpawnTime is taken only
     * when it runs in the next update; behind a full update's share it is answered DROPPED at once instead of waiting
     * while its id could come to name another entity. A request whose entity lacks the component at an update while
     * it waits never runs either.
     * KILN_ERR_INVALID_ARGUMENT: a bad size, no entity, no component, neither or both of data and change, length 0
     * or over 4096. KILN_ERR_UNAVAILABLE: tracked writes do not work on this build. KILN_ERR_BUFFER_TOO_SMALL: 4096
     * accepted requests have not had their answer handed over yet (waiting, running, held, answered, or being
     * delivered; refusals included).
     * Everything else (not declared, no such entity, a size that does not fit) comes back through the callback, never
     * as "ok": check the result. */
    KilnResult(KILN_CALL* write_submit)(KilnOwner owner, const KilnWorldWriteRequest* request, uint64_t* out_ticket);
    /* Drops the request's callbacks. A request that has not run yet is not run (its change callback is not called
     * after this returns); a result drained for delivery in this Pre is not delivered (a result callback may cancel
     * another request). Tick thread. KILN_ERR_NOT_FOUND when it already completed. */
    KilnResult(KILN_CALL* write_cancel)(uint64_t ticket);
    /* What kiln.world knows about writing component `component` for `mod_id` (empty: any mod): its state, and what
     * the running world says about the type (the game's own writers, the save and replication bits), also for a type
     * nobody listed, so an author can look before listing it: answered from a catalog kiln.world publishes for the
     * running server world, tagged with it (writers and sizes at its setup, the save and replication bits at its first
     * step), never from the world itself. KILN_WORLD_INFO_NOT_READY when the catalog is not the running world's yet
     * (none runs, or a new one is being set up): only the state is filled then. Any thread. */
    KilnResult(KILN_CALL* write_info)(KilnStr mod_id, uint32_t component, KilnWorldWriteInfo* out);
    KilnResult(KILN_CALL* write_status)(KilnWorldWriteStatus* out);
    /* A native service's own list of component types to write (a service has no manifest), under the name `who`,
     * which it then passes as KilnWorldWriteRequest.mod_id (or it passes none). From kiln_plugin_main or on_ready
     * only: KILN_ERR_UNAVAILABLE once the first server world registered its programs, and when tracked writes do
     * not work on this build. KILN_ERR_INVALID_ARGUMENT: no names, over 64, or a name that is not a component's. */
    KilnResult(KILN_CALL* write_declare)(KilnOwner owner, KilnStr who, const KilnStr* names, uint32_t count);

    /* ---- game programs: appended in v4, since 0.2.0-dev.28. Check header.size ---- */
    /* Index of the program `name` in the table of the live world of `world_kind`, or -1 (no such program, no
     * directory of that kind yet, PREDICTION). Any thread. */
    int32_t(KILN_CALL* game_program_find)(uint32_t world_kind, KilnStr name);
    /* One entry of that directory. KILN_ERR_NOT_FOUND: no such index, or no directory of that kind now;
     * KILN_ERR_INVALID_ARGUMENT: out is NULL or out->size is too small. Any thread. */
    KilnResult(KILN_CALL* game_program_info)(uint32_t world_kind, int32_t index, KilnGameProgramInfo* out);
    /* The directories, the threads report and the wrap counts. Any thread. */
    KilnResult(KILN_CALL* game_programs_status)(KilnGameProgramsStatus* out);
    /* Lab only, never a mod API (managed: Kiln.Testing.LabSystems). Forces the server world's parallel program path
     * as if player 16 were in the world: sets bit 15 of its players mask in its next Pre, and lets go again after
     * `steps` steps (1..3000), at on = 0, or when the world stops. Off unless kiln/control/systems.parallel
     * existed when Kiln started (KILN_ERR_UNAVAILABLE), and only on a dedicated server. A bit 15 that a real player 16
     * set is left alone. Any thread outside a program update; it takes effect in the next Pre. */
    KilnResult(KILN_CALL* lab_parallel)(uint32_t on, uint32_t steps);
    /* Wraps program `index` of the live world of `world_kind` (SERVER or CLIENT) with a `stage` (KILN_WRAP_*) that
     * calls fn(slice, user). *out = its handle. It is PENDING until the target world's next Pre, where kiln.world
     * applies it (or refuses it: game_wrap_info says why); later worlds of that kind get it again by program name.
     * Any thread outside a stage or program update. A second Replace of a program is not an error: *out is a handle
     * that is REFUSED at once (reason KILN_WRAP_REPLACE_TAKEN, detail names the owner and wrap that replace it).
     * KILN_ERR_INVALID_ARGUMENT: no owner, fn or out, a bad stage or kind, a program with no update in that world;
     * KILN_ERR_NOT_FOUND: no such index in the live directory; KILN_ERR_UNAVAILABLE: wraps are off on this build,
     * native SET is loaded, or (CLIENT) capability systems.wraps.client is not available; KILN_ERR_WRONG_THREAD:
     * from inside a stage or program update. */
    KilnResult(KILN_CALL* game_program_wrap)(KilnOwner owner, uint32_t world_kind, int32_t index, int32_t stage,
                                             int32_t priority, KilnWrapStageFn fn, void* user, KilnHandle* out);
    /* Unwraps: no call of fn starts after this returns; one that had started finishes (game_wrap_info.running says
     * when none runs). The stage leaves its program's chain in the next Pre of its world (a PENDING one at once).
     * Any thread outside a stage. */
    KilnResult(KILN_CALL* game_program_unwrap)(KilnHandle wrap);
    /* State and counters of a wrap. KILN_ERR_NOT_FOUND: no such handle (removed and refused wraps are forgotten
     * after 1024 more have finished). Any thread. */
    KilnResult(KILN_CALL* game_wrap_info)(KilnHandle wrap, KilnGameWrapInfo* out);
    /* Unwraps every wrap of `owner`: no call of its stages starts after this returns; calls that had started
     * finish. *out_pending = how many worlds have a slice inside one of its stages now (also while they step; the
     * stages leave their chains in each world's next Pre). 0: none runs or can start; its code and user data may go.
     * Call again to poll. Any thread outside a stage. */
    KilnResult(KILN_CALL* game_wraps_release)(KilnOwner owner, uint32_t* out_pending);
    /* Inside a REPLACE stage: runs the game's update for this slice, once. KILN_ERR_INVALID_ARGUMENT: not a Replace,
     * or it ran already; KILN_ERR_WRONG_THREAD: not this stage's slice. */
    KilnResult(KILN_CALL* game_slice_run_original)(KilnGameSlice* slice);
    /* The game's update behind a table entry kiln.world swapped (entry = the program-table entry, as the engine's
     * program runner gets it at slice+0x10), or 0 when the entry holds no kiln.world trampoline. Any thread; lock-free.
     * For code that names programs by their update pointer. */
    uint64_t(KILN_CALL* wrap_original)(uintptr_t entry);

    /* ---- appended since 0.2.0-dev.28 (version stays 4). Check header.size ---- */
    /* 1 when the calling thread is inside a Kiln world program's update (register_program, bind_program), inside a
     * wrap stage on a game program's slice (game_program_wrap), or inside a kiln.types system_hook callback (kiln.types'
     * in_system_callback; 0 for that part with a kiln.types that lacks it), else 0. The world is in the middle of its
     * step there (maybe on a worker): a service whose calls must not run mid-step refuses them with
     * KILN_ERR_WRONG_THREAD when this answers 1. Any thread; lock-free, no game call. */
    uint32_t(KILN_CALL* in_program)(void);
} KilnWorldApi;

enum {
    KILN_WORLD_CAP_WRITE_OTHER = 1u << 0,
    KILN_WORLD_CAP_MULTI_EVENTS = 1u << 1,
    KILN_WORLD_CAP_BIND_PROGRAM = 1u << 2,
    KILN_WORLD_CAP_TRACKED_WRITES = 1u << 3, /* since 0.2.0-dev.27: write_submit works on this build */
};

#ifdef __cplusplus
}
#endif
#endif
