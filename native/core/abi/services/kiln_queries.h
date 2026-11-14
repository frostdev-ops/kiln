/* SPDX-License-Identifier: MIT
 * kiln.queries v1 (kiln-queries.dll): read-only questions about the server world.
 *   ray_voxels   the first voxel surface (terrain or building voxels) on a segment. Not a physics ray: it does not see
 *                entity colliders, props or creatures.
 *   entities     the entities whose origin (CurrentTransform position) is inside a sphere or an axis-aligned box.
 *                Origin geometry only: not a collider overlap.
 *   path_*       the game's path search as an owner-scoped pull queue (kiln_queries.h's path structs and their rules
 *                are added by the path feature; until then every path member answers KILN_ERR_UNAVAILABLE).
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

typedef struct KilnQueryPathRequest KilnQueryPathRequest;
typedef struct KilnQueryPathAdmission KilnQueryPathAdmission;
typedef struct KilnQueryPathUpdate KilnQueryPathUpdate;
typedef struct KilnQueryPathPoint KilnQueryPathPoint;

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
