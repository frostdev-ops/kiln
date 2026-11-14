/* SPDX-License-Identifier: MIT
 * Shared value declarations of capacity queries and result-bearing definition registration (since 0.2.0-dev.28).
 * This is not a service: there is no "kiln.capacity" table. The services that own a quota (kiln.vfx, kiln.gamedata,
 * kiln.knowledge, kiln.world, kiln.worldtables) append query and define members to their own tables and use these
 * structs, so every budget and every registration answer has one shape. Availability stays with kiln.caps.
 *
 * A capacity query returns KILN_OK for every structurally valid reply, also when the budget is not ready (no world
 * yet, external usage unknown): `readiness` says so, and then the four counts are 0. KILN_ERR_INVALID_ARGUMENT is a
 * bad selector or name; KILN_ERR_UNAVAILABLE a missing capability.
 *
 * Counts are in units of the queried quota and never overlap: game_used (vanilla usage Kiln cannot have), kiln_used
 * (active Kiln-owned usage, built-in services and compat registrations through Kiln included), reserved (occupied and
 * unavailable for another reason: retained or orphan records, placeholders, declared built-in reservations not yet
 * active, measured usage of another loader). game_used + kiln_used + reserved <= capacity, checked in 64 bits before
 * a reply is published (kiln_capacity_util.hpp); a sum that does not hold is ACCOUNTING_MISMATCH, never clamped. */
#ifndef KILN_CAPACITY_H
#define KILN_CAPACITY_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

/* KilnCapacityInfo.flags. Exactly one of PROCESS and WORLD is set. Other bits are 0. */
enum {
    KILN_CAPACITY_READY = 1u,             /* set iff readiness is KILN_CAPACITY_VALID */
    KILN_CAPACITY_PROCESS = 2u,           /* one budget for the process: world_kind = KILN_CAPACITY_NO_WORLD_KIND,
                                             generation = 0 */
    KILN_CAPACITY_WORLD = 4u,             /* the budget of one world: world_kind and generation name it */
    KILN_CAPACITY_EXPANDED = 8u,          /* the larger quota is really installed (not merely asked for in config) */
    KILN_CAPACITY_EXTERNAL_UNKNOWN = 16u, /* comes with readiness UNKNOWN_EXTERNAL */
};

/* KilnCapacityInfo.readiness */
enum {
    KILN_CAPACITY_NOT_READY = 0,
    KILN_CAPACITY_VALID = 1,
    KILN_CAPACITY_NO_WORLD = 2,
    KILN_CAPACITY_WORLD_ENDED = 3,
    KILN_CAPACITY_UNKNOWN_EXTERNAL = 4,    /* another loader's usage cannot be measured: never reported as free */
    KILN_CAPACITY_ACCOUNTING_MISMATCH = 5, /* the counts do not add up, ownership overlaps, or the table is unreadable */
    KILN_CAPACITY_NOT_FOUND = 6,
};

/* World selectors are the core's values (KILN_WORLD_SERVER 0, KILN_WORLD_CLIENT 2); PREDICTION (1) is not a
 * selector of these queries. */
#define KILN_CAPACITY_NO_WORLD_KIND 0xFFFFFFFFu

typedef struct KilnCapacityInfo {
    uint32_t size;          /* set by the caller: sizeof(KilnCapacityInfo) */
    uint32_t capacity;
    uint32_t game_used;
    uint32_t kiln_used;
    uint32_t reserved;
    uint32_t flags;         /* KILN_CAPACITY_READY | ... */
    uint32_t readiness;     /* KILN_CAPACITY_NOT_READY .. */
    uint32_t world_kind;    /* world scope: 0 or 2; process scope: KILN_CAPACITY_NO_WORLD_KIND */
    uint32_t generation;    /* world scope: the core session's counter of that world kind (never an address); 0 when
                               there is no such world; process scope: 0 */
    uint32_t reserved_zero;
    uint64_t revision;      /* +40: the service's own counter, rising with every published change */
} KilnCapacityInfo;         /* 48 bytes, alignment 8 */

/* ---- result-bearing registration ---- */

/* The `timing` argument of a define member. */
enum {
    KILN_DEFINE_REQUIRE_REGISTERED = 0, /* register in this process or refuse; never writes a pending file */
    KILN_DEFINE_ALLOW_NEXT_START = 1,   /* register now when that is safe; when only timing stands in the way, persist
                                           the definition for the next process start */
};

/* KilnDefinitionReply.disposition */
enum {
    KILN_DEFINITION_REFUSED = 0,
    KILN_DEFINITION_REGISTERED = 1,          /* really registered in this process */
    KILN_DEFINITION_ACCEPTED_NEXT_START = 2, /* definition and receipt persisted for the next start; slots_consumed 0 */
    KILN_DEFINITION_RETAINED = 3,            /* a record that could not be freed stays and occupies its slots */
};

/* KilnDefinitionReply.kind */
enum {
    KILN_DEFINITION_VFX = 0,
    KILN_DEFINITION_BUFF = 1,
    KILN_DEFINITION_BUILDING_MATERIAL_POOL = 2,
};

/* KilnDefinitionReply.reason_code: 0 iff ok is 1. */
enum {
    KILN_DEFINE_NONE = 0,
    KILN_DEFINE_CAPACITY_EXCEEDED = 1,
    KILN_DEFINE_UNKNOWN_BASE = 2,
    KILN_DEFINE_ID_COLLISION = 3,
    KILN_DEFINE_NAME_TAKEN = 4,
    KILN_DEFINE_REGISTRY_CLOSED = 5,
    KILN_DEFINE_NO_WORLD = 6,
    KILN_DEFINE_NOT_READY = 7,
    KILN_DEFINE_STORAGE_FAILURE = 8,
    KILN_DEFINE_NOT_FOUND = 9,
    KILN_DEFINE_OWNER_INACTIVE = 10,
    KILN_DEFINE_REGISTRATION_FAILED_RETAINED = 11,
    KILN_DEFINE_SUPERSEDED = 12,
    KILN_DEFINE_REMOVED = 13,
    KILN_DEFINE_GAME_REFUSED = 14,
};

/* The fixed part of a define or receipt answer. The words come next to it as JSON through the usual grow-buffer
 * pattern: {"reason": null | "text", "warnings": ["..."]}; every refusal has a reason text.
 * A valid game, budget or storage refusal: KILN_OK with ok = 0. A bad argument: KILN_ERR_INVALID_ARGUMENT. A missing
 * capability: KILN_ERR_UNAVAILABLE. KILN_ERR_BUFFER_TOO_SMALL (the report did not fit) has changed nothing: a service
 * validates and sizes the report first, and the retry is idempotent by registration_id. */
typedef struct KilnDefinitionReply {
    uint32_t size;               /* set by the caller: sizeof(KilnDefinitionReply) */
    uint32_t ok;                 /* 1 or 0 */
    uint32_t disposition;        /* KILN_DEFINITION_* */
    uint32_t slots_consumed;     /* the occupancy this definition accounts for now (not an increment) */
    uint8_t registration_id[16]; /* stable: kiln_capacity_util.hpp registration_id() */
    uint8_t definition_guid[16];
    uint64_t snapshot_revision;  /* +48: the service revision this answer was committed in */
    uint32_t reason_code;        /* KILN_DEFINE_* */
    uint32_t kind;               /* KILN_DEFINITION_* */
} KilnDefinitionReply;           /* 64 bytes, alignment 8 */

#ifdef __cplusplus
}
#endif
#endif /* KILN_CAPACITY_H */
