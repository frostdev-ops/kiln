/* SPDX-License-Identifier: MIT
 * kiln.queries v1 (kiln-queries.dll): read-only questions about the server world.
 *   ray_voxels   the first voxel surface (terrain or building voxels) on a segment. Not a physics ray: it does not see
 *                entity colliders, props or creatures.
 *   entities     the entities whose origin (CurrentTransform position) is inside a sphere or an axis-aligned box.
 *                Origin geometry only: not a collider overlap.
 *   path_*       the game's path search as an owner-scoped pull queue (below, "paths").
 *
 * Server world only: a dedicated server, or the server world of a game client that hosts. Nothing here reads a
 * client's rendering or prediction world.
 *
 * Results. A bad argument is KILN_ERR_INVALID_ARGUMENT, the wrong thread KILN_ERR_WRONG_THREAD, a capability that is
 * off KILN_ERR_UNAVAILABLE. Every other answer is KILN_OK with `ok` in the result: ok = 0 and a reason when the game
 * or the service refuses (reasons below), ok = 1 otherwise. A ray's ok = 1 with hit = 0 means the whole segment was
 * read and is clear; it is never the answer for data that is not loaded, outside the grid or over budget.
 * Reason strings are constants of the service, valid for the life of the process.
 *
 * Ray reasons: "WorldNotReady" (no server world, or before its first published step), "OutOfGrid" (an endpoint is
 * outside the realm's voxel grid; the game is not called), "NotLoaded" (voxel data on the segment is not loaded:
 * load the area with kiln.build and ask again in a later step), "BudgetExceeded" (the step's shared ray budget is
 * used up: ask again next step), "ReentrantQuery", "InvalidHit" (the game answered a hit that cannot be read).
 *
 * The ray budget is shared by every caller: 64 rays and 500 microseconds of ray time per published server step by
 * default (<config>/kiln.queries.json: {"format":1,"maxRaysPerStep":64,"maxRayMicrosecondsPerStep":500}; read once
 * at start). A refusal that makes no game call uses none of it.
 *
 * Threads: ray_voxels and entities on the tick thread (Pre, Post, a posted task).
 *          ray_voxels and path_submit also answer KILN_ERR_WRONG_THREAD, with reason "InsideProgram", from inside a
 *          Kiln world program's update, a wrap stage on a game program's slice or a kiln.types system_hook callback,
 *          on the world's own thread too (the world is mid-step there; kiln.world's in_program, since 0.2.0-dev.28):
 *          cast the ray or submit the request in a tick handler and hand the result in.
 * Capabilities: "kiln.queries" (this table), "queries.voxel_ray" (ray_voxels; a sigdb feature).
 */
#ifndef KILN_QUERIES_H
#define KILN_QUERIES_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_QUERIES_SERVICE "kiln.queries"
#define KILN_QUERIES_VERSION 1u

#define KILN_QUERY_RAY_MAX_LENGTH 256.0 /* metres */
#define KILN_QUERY_SHAPE_SPHERE 0u
#define KILN_QUERY_SHAPE_BOX 1u

typedef struct KilnQueryRayRequest {
    uint32_t size;  /* sizeof(KilnQueryRayRequest) */
    uint32_t realm; /* 0 = the overworld */
    KilnVec3 from, to; /* metres; the segment is longer than 0 and at most KILN_QUERY_RAY_MAX_LENGTH, also as floats */
} KilnQueryRayRequest;

typedef struct KilnQueryRayResult {
    uint32_t size; /* set by the caller: sizeof(KilnQueryRayResult) */
    uint32_t ok;
    KilnStr reason;    /* empty when ok */
    uint32_t hit;      /* 1: the fields below are the first voxel surface; 0 with ok: the segment is clear */
    uint32_t material; /* the game's material id of the voxel that was hit */
    uint16_t voxel;    /* the raw packed voxel */
    uint16_t reserved;
    KilnVec3 position; /* where the segment meets the voxel */
    double distance;   /* metres from `from` to `position` */
} KilnQueryRayResult;

typedef struct KilnQueryEntityRequest {
    uint32_t size;
    uint32_t shape; /* KILN_QUERY_SHAPE_*: closed sphere (radius) or closed box (half_extents) around center */
    KilnVec3 center, half_extents;
    double radius;
    KilnEntity exclude; /* 0 = none */
    uint32_t reserved;
} KilnQueryEntityRequest;

typedef struct KilnQueryEntityResult {
    uint32_t size;
    uint32_t ok;
    KilnStr reason;
    uint32_t written; /* ids written, nearest first (then by id) */
    uint32_t total;   /* ids that matched: more than written = the buffer was too small */
} KilnQueryEntityResult;

/* ---- paths (since 0.2.0-dev.28) ---------------------------------------------------------------------------------
 * The game's own path search between two points, as an owner-scoped pull queue. Nothing is moved; a result is a copy
 * of the route's points as they were at the step the game answered.
 *
 * A request belongs to `owner` (the caller's core owner: its lease) and names an `anchor` entity the game's query is
 * created for. The anchor is not moved and need not be alive; it must have a CurrentTransform and a SpawnTime
 * component (entities the game or kiln.spawn spawned into the world have one; saved entities, items and players may
 * not). The pair (entity id, SpawnTime) is its identity: an anchor that goes, or whose id comes to name another
 * entity, ends the request with OWNER_GONE.
 *
 * path_submit  tick thread. KILN_OK with ok = 1 and a token: the request is queued and the first update
 *              (CREATED -> QUEUED) waits in the owner's queue. KILN_OK with ok = 0: refused, nothing was queued; status
 *              and reason say why (QUEUE_FULL: 32 open requests in all or 8 for this owner, counting results not yet
 *              polled; IDENTITY_UNAVAILABLE; OUT_OF_GRID: an endpoint outside realm 0's voxel grid).
 *              KILN_ERR_INVALID_ARGUMENT: numbers that are no request (not finite, more than 200 m apart, clearance
 *              outside 0..16 m, iterations outside 1..65535). KILN_ERR_UNAVAILABLE: "queries.path" is off or there is
 *              no server world. KILN_ERR_REVOKED: the owner is gone or retired.
 * path_cancel  tick thread. KILN_OK: an own request that had no result yet ends CANCELLED (one terminal update).
 *              KILN_ERR_NOT_FOUND: unknown, another owner's, or already ended.
 * path_poll    any thread, one consumer per owner. Copies the oldest update and, for a terminal one, its points, and
 *              takes it out of the queue. KILN_ERR_NOT_FOUND: the queue is empty (*needed = 0).
 *              KILN_ERR_BUFFER_TOO_SMALL: *needed = the head's point count; nothing is taken. Works with no world.
 * path_retire  any thread, no game call. The owner's queue is dropped and its requests end without a result; the
 *              game's queries are still destroyed by the service. Every later call for the owner is KILN_ERR_REVOKED.
 *
 * A request lives at most 10 s and 150 server steps (both must pass); then it ends TIMED_OUT. Each request yields at
 * most three updates: CREATED -> QUEUED, QUEUED -> SEARCHING (the game took the query), and one terminal.
 * Only walking routes are results: a route with any other move kind is UNSUPPORTED (its points are kept for
 * diagnosis). FOUND: the last point is within 1.5 m (XZ) and 3 m (Y) of `to`; PARTIAL: a walking route that ends
 * elsewhere. Capability: "queries.path" (a sigdb feature). */
typedef enum KilnQueryPathStatus {
    KQ_PENDING = 0, KQ_FOUND = 1, KQ_PARTIAL = 2, KQ_NO_PATH = 3, KQ_NO_START = 4, KQ_NO_TARGET = 5,
    KQ_ITERATION_LIMIT = 6, KQ_OUT_OF_MEMORY = 7, KQ_UNKNOWN = 8, KQ_CANCELLED = 9, KQ_OWNER_GONE = 10,
    KQ_TIMED_OUT = 11, KQ_TRUNCATED = 12, KQ_REFUSED = 13, KQ_UNSUPPORTED = 14, KQ_MALFORMED = 15,
    KQ_QUEUE_FULL = 16, KQ_IDENTITY_UNAVAILABLE = 17, KQ_OUT_OF_GRID = 18, KQ_WORLD_CLOSED = 19,
    KQ_OWNER_RETIRED = 20
} KilnQueryPathStatus;

typedef enum KilnQueryPathStage {
    KQS_CREATED = 0, KQS_QUEUED = 1, KQS_SEARCHING = 2, KQS_COMPLETED = 3, KQS_CANCELLED = 4, KQS_TIMED_OUT = 5
} KilnQueryPathStage;

#define KILN_QUERY_PATH_MAX_POINTS 4096u
#define KILN_QUERY_PATH_MAX_DISTANCE 200.0 /* metres between from and to */
#define KILN_QUERY_PATH_NO_GAME_RESULT 0xffffffffu

typedef struct KilnQueryPathPoint { /* 32 bytes */
    KilnVec3 position;
    uint32_t raw_move; /* the game's PathMoveType of the step to this point: 0 = walk */
    uint32_t reserved;
} KilnQueryPathPoint;

typedef struct KilnQueryPathRequest { /* 80 bytes */
    uint32_t size;
    KilnEntity anchor;
    KilnVec3 from, to;
    double horizontal_space, vertical_space; /* free space the route needs, metres, as the game's query takes it */
    uint32_t max_iterations;                 /* the search's iteration limit, 1..65535 */
    uint32_t closest_fallback;               /* 1: when the target has no node, end at the closest one (PARTIAL) */
} KilnQueryPathRequest;

typedef struct KilnQueryPathAdmission { /* 40 bytes */
    uint32_t size, ok;
    uint64_t request; /* the token; 0 when refused */
    uint32_t status;  /* KQ_PENDING when ok; else the refusal */
    uint32_t reserved;
    KilnStr reason;
} KilnQueryPathAdmission;

typedef struct KilnQueryPathUpdate { /* 88 bytes */
    uint32_t size;
    uint32_t before, after; /* KilnQueryPathStage */
    uint32_t status;        /* KQ_PENDING for a nonterminal update */
    uint64_t request, sequence;
    uint32_t generation;    /* the server session the request belongs to */
    KilnEntity anchor;
    uint32_t terminal, ok;
    uint32_t game_result;   /* the game's raw PathfindingResult, or KILN_QUERY_PATH_NO_GAME_RESULT */
    uint32_t reaches_target;
    uint32_t point_count, steps;
    double length;          /* metres along the points (FOUND and PARTIAL) */
    KilnStr reason;
} KilnQueryPathUpdate;

typedef struct KilnQueriesApi {
    KilnServiceHeader header; /* { sizeof(KilnQueriesApi) = 56, KILN_QUERIES_VERSION } */
    KilnResult(KILN_CALL* ray_voxels)(const KilnQueryRayRequest* request, KilnQueryRayResult* out);
    KilnResult(KILN_CALL* entities)(const KilnQueryEntityRequest* request, KilnEntity* out, uint32_t capacity,
                                    KilnQueryEntityResult* result);
    KilnResult(KILN_CALL* path_submit)(KilnOwner owner, const KilnQueryPathRequest* request,
                                       KilnQueryPathAdmission* out);
    KilnResult(KILN_CALL* path_cancel)(KilnOwner owner, uint64_t request);
    KilnResult(KILN_CALL* path_poll)(KilnOwner owner, KilnQueryPathUpdate* update, KilnQueryPathPoint* points,
                                     uint32_t capacity, uint32_t* needed);
    KilnResult(KILN_CALL* path_retire)(KilnOwner owner);
} KilnQueriesApi;

#ifdef __cplusplus
}
#endif
#endif
