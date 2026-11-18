/* SPDX-License-Identifier: MIT
 * kiln.engine v1: which exact game build runs, against Kiln's per-build engine profiles, and how the engine gates
 * fared (docs/kiln/services/engine.md).
 *
 * A Kiln service member that reads engine memory from the caller's thread or takes the engine's own gates is
 * available only on an exact build: the exe file's sha256, its PE TimeDateStamp and SizeOfImage, its file name and
 * the KFC4 header of its .kfc archive all match one of Kiln's per-build profiles (sigdb/engine/, game-build profiles
 * adapted from ShroudForge, MIT): exact executable identity, no structural fallback. Nothing structural stands in for
 * a missing profile.
 *
 * This table has data members only (category D, any thread, no game memory). The primitives that read engine memory and
 * hold the engine's gates are Kiln-internal and not part of this ABI.
 *
 * Capability rows (static; per-call readiness stays dynamic, every engine member returns its own refusal):
 *   engine.profile    an exact profile matched this image (UNAVAILABLE, policy: why not);
 *   engine.ecs_read   that profile's owner and ECS read layouts are validated;
 *   engine.ecs_write  ... and its original native-thread Scene write gate is enabled;
 *   engine.gate_priority_raise  a write gate's holder is raised above normal priority while it holds the gate
 *                     (UNAVAILABLE, policy: the measurement switch kiln/data/kiln.engine/gate-priority-raise turned the
 *                     raise off for this run; docs/kiln/services/engine.md).
 */
#ifndef KILN_ENGINE_H
#define KILN_ENGINE_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_ENGINE_SERVICE "kiln.engine"
#define KILN_ENGINE_VERSION 1u

/* KilnEngineIdentity.status */
enum {
    KILN_ENGINE_EXACT = 1,        /* one ported profile matches this image exactly */
    KILN_ENGINE_UNKNOWN_BUILD = 2, /* identified, but no profile matches (a new game build, or a modified exe) */
    KILN_ENGINE_UNIDENTIFIED = 3, /* the image could not be identified (no file hash, unreadable headers) */
    KILN_ENGINE_AMBIGUOUS = 4     /* more than one profile matches (never shipped; refused like an unknown build) */
};

/* KilnEngineIdentity.sections: what the selected profile carries, each validated at build time. */
enum {
    KILN_ENGINE_SECTION_OWNERS = 1u << 0,
    KILN_ENGINE_SECTION_ECS_READ = 1u << 1,
    KILN_ENGINE_SECTION_ECS_AUTHORITY = 1u << 2,
    KILN_ENGINE_SECTION_ECS_WRITE = 1u << 3,
    KILN_ENGINE_SECTION_RESOURCE_READ = 1u << 4,
    KILN_ENGINE_SECTION_RESOURCE_WRITE = 1u << 5,
    KILN_ENGINE_SECTION_SERVICES = 1u << 6,
    KILN_ENGINE_SECTION_COMMAND_QUEUE = 1u << 7,
    KILN_ENGINE_SECTION_PROP_SPAWN = 1u << 8,
    KILN_ENGINE_SECTION_SYSTEM_DEPENDENCIES = 1u << 9,
    KILN_ENGINE_SECTION_ITEM_ID_MAP = 1u << 10
};

/* Set size first (sizeof(KilnEngineIdentity)); strings are NUL-terminated, "" when unknown. */
typedef struct KilnEngineIdentity {
    uint32_t size, status;           /* KILN_ENGINE_* */
    uint32_t side, archive_format;   /* KILN_SIDE_*; the game archive format number from the <exe>.kfc header (KFC4 = 4,
                                        KFC3 = 3), 0 when unreadable. */
    uint32_t timestamp, size_of_image; /* the loaded image's PE TimeDateStamp and SizeOfImage */
    uint32_t sections, schema_version; /* the selected profile's; 0 without one */
    char target[32];                 /* exe file name, cut to 31 bytes */
    char sha256[65];                 /* lower-case hex of the exe file, as KilnBuildInfo.exe_sha256 */
    char profile_id[63];             /* the selected profile's id, e.g. "enshrouded-client-26430625" */
} KilnEngineIdentity;

/* Counters of the engine gates since boot (Kiln-internal primitives), for diagnostics.
 * Each counter is read on its own (not one coherent snapshot): compare two copies, not fields of one. */
typedef struct KilnEngineGateStats {
    uint32_t size, reserved;
    uint64_t attempts;              /* write-gate acquisitions tried */
    uint64_t acquired;              /* ... that got both gates */
    uint64_t busy_registry;         /* refused: the thread-registry word was held exclusively */
    uint64_t busy_scene;            /* refused: the Scene word was held (the engine's frame, or another writer) */
    uint64_t refused_in_frame;      /* refused untouched: called inside the server world's update */
    uint64_t refused_world_thread;  /* refused untouched: called on a client or prediction world thread */
    uint64_t refused_other;         /* refused: no profile, owner changed, not authoritative, ... */
    uint64_t hold_ns_total, hold_ns_max; /* time from acquisition to release */
    uint64_t views, view_failures;  /* owner/ECS view resolutions and how many were refused */
} KilnEngineGateStats;

typedef struct KilnEngineApi {
    KilnServiceHeader header;
    /* The image and the selected profile. Any thread, from kiln_plugin_main on; the value never changes after boot.
     * KILN_OK whatever the status; KILN_ERR_INVALID_ARGUMENT: out NULL or out->size too small. */
    KilnResult(KILN_CALL* identity)(KilnEngineIdentity* out);
    /* A copy of the counters. Any thread. KILN_ERR_INVALID_ARGUMENT: out NULL or out->size too small. */
    KilnResult(KILN_CALL* gate_stats)(KilnEngineGateStats* out);
} KilnEngineApi;

#ifdef __cplusplus
}
static_assert(sizeof(KilnEngineIdentity) == 192, "KilnEngineIdentity v1");
static_assert(offsetof(KilnEngineIdentity, status) == 4 && offsetof(KilnEngineIdentity, side) == 8 &&
                  offsetof(KilnEngineIdentity, archive_format) == 12 && offsetof(KilnEngineIdentity, timestamp) == 16 &&
                  offsetof(KilnEngineIdentity, size_of_image) == 20 && offsetof(KilnEngineIdentity, sections) == 24 &&
                  offsetof(KilnEngineIdentity, schema_version) == 28,
              "identity fields");
static_assert(offsetof(KilnEngineIdentity, target) == 32, "identity strings");
static_assert(offsetof(KilnEngineIdentity, sha256) == 64, "identity sha256");
static_assert(offsetof(KilnEngineIdentity, profile_id) == 129, "identity profile id");
static_assert(sizeof(KilnEngineGateStats) == 96, "KilnEngineGateStats v1");
static_assert(offsetof(KilnEngineGateStats, reserved) == 4 && offsetof(KilnEngineGateStats, attempts) == 8 &&
                  offsetof(KilnEngineGateStats, acquired) == 16 && offsetof(KilnEngineGateStats, busy_registry) == 24 &&
                  offsetof(KilnEngineGateStats, busy_scene) == 32 && offsetof(KilnEngineGateStats, refused_in_frame) == 40 &&
                  offsetof(KilnEngineGateStats, refused_world_thread) == 48 &&
                  offsetof(KilnEngineGateStats, refused_other) == 56 && offsetof(KilnEngineGateStats, hold_ns_total) == 64 &&
                  offsetof(KilnEngineGateStats, hold_ns_max) == 72 && offsetof(KilnEngineGateStats, views) == 80 &&
                  offsetof(KilnEngineGateStats, view_failures) == 88,
              "gate stats fields");
static_assert(sizeof(KilnEngineApi) == 24, "kiln.engine v1 table");
static_assert(offsetof(KilnEngineApi, identity) == 8 && offsetof(KilnEngineApi, gate_stats) == 16, "kiln.engine v1");
#endif
#endif
