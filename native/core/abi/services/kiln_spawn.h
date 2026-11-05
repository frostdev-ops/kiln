/* SPDX-License-Identifier: MIT
 * kiln.spawn v1: create and destroy server-world entities from templates (creatures, item drops, props), and keep a
 * per-world record of what Kiln spawned so an admin can remove it again (spawned entities can persist in the save).
 * Evidence and engine notes: docs/re/spawn.md. Usage: docs/kiln/services/spawn.md.
 *
 * Threads: functions marked "tick" touch game state and must run on the tick thread in Pre or Post (posted tasks
 * count as Pre); elsewhere they return KILN_ERR_WRONG_THREAD. Managed mods always call from there. Catalog and
 * tracked-list reads are thread-safe.
 *
 * Templates are identified by their 16-byte GUID in the game's (Windows) layout: the text form
 * a56d2777-daf1-4164-90d4-b7b12749c723 is the bytes 77 27 6d a5 f1 da 64 41 90 d4 b7 b1 27 49 c7 23. Names come from
 * the engine's own template infos (all loaded templates) plus the catalog Kiln ships (in-game display names).
 *
 * Entity ids: the id is known when spawn returns (the engine creates the entity at once; game programs see it from the
 * next sim step). Ids are not stable across a server restart (entities loaded from the save are renumbered). Tracked
 * records are re-found after a restart by template and a spawn-time marker (see docs/kiln/services/spawn.md). */
#ifndef KILN_SPAWN_H
#define KILN_SPAWN_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_SPAWN_SERVICE "kiln.spawn"
#define KILN_SPAWN_VERSION 1u

/* A template GUID, game byte order. All zero = none. */
typedef struct KilnGuid {
    uint8_t b[16];
} KilnGuid;

/* KilnSpawnRequest.flags (also accepted by spawn_item) */
enum {
    KILN_SPAWN_NO_TRACK = 1u << 0,      /* do not record the entity in the sidecar (the caller cleans up itself) */
    KILN_SPAWN_TRANSIENT = 1u << 1,     /* add the engine's DoNotSave tag: the entity is never written to the save */
    KILN_SPAWN_ALLOW_UNKNOWN = 1u << 2, /* spawn a GUID even when the engine's template list could not be read */
};

/* KilnTemplateInfo.kind (from the template name) */
enum {
    KILN_TEMPLATE_UNKNOWN = 0,
    KILN_TEMPLATE_CREATURE = 1, /* Enemy_*, Animal_*, FakePlayer_* ... */
    KILN_TEMPLATE_PICKUP = 2,   /* loose item pickups */
    KILN_TEMPLATE_PROP = 3,     /* Prop_* */
    KILN_TEMPLATE_NPC = 4,      /* villagers (unique per template: a second copy stays idle) */
    KILN_TEMPLATE_OTHER = 5,    /* everything else (VFX, attacks, spawners, buildings ...) */
};

/* An extra spawn component (KilnSpawnRequest.components; appended later in v1). Same layout as the engine's own
 * SpawnComponent: the engine copies `size` bytes from `data` into the new entity at once. */
typedef struct KilnSpawnComponent {
    uint32_t hash;    /* qualifiedHash = FNV-1a-32("keen::ecs::<Name>"), e.g. Faction 0xa1f30f21 */
    uint32_t flags;   /* 0 (reserved) */
    const void* data; /* the component's bytes; read during the call only */
    uint64_t size;    /* must equal the engine's component size, else the engine refuses the spawn (INTERNAL) */
} KilnSpawnComponent;

#define KILN_SPAWN_MAX_COMPONENTS 64u

typedef struct KilnSpawnRequest {
    uint32_t size;          /* sizeof(KilnSpawnRequest); KILN_SPAWN_REQUEST_V1_SIZE (104) for callers without
                               the appended fields */
    uint32_t flags;         /* KILN_SPAWN_* */
    KilnGuid template_guid; /* used when not all zero */
    KilnStr template_name;  /* else: template name (exact, then case-insensitive, then display name) */
    KilnVec3 position;      /* metres; placed exactly here (no ground snapping: pick a point on the ground) */
    float yaw_degrees;      /* rotation about +Y (up) */
    int32_t level;          /* > 0: OverrideLevel; 0 = template default */
    KilnStr tag;            /* optional label kept with the tracked record (e.g. "admin", "raids") */
    KilnStr origin;         /* who spawned it (mod id); the managed API fills it in */
    /* ---- appended later in v1 (read only when size covers them; check capabilities() & KILN_SPAWN_CAP_COMPONENTS) */
    const KilnSpawnComponent* components; /* extra spawn components (NULL = none); a hash Kiln adds itself
                                             (CurrentTransform, OverrideLevel, DoNotSave, PickupItem) replaces Kiln's
                                             value; SpawnTime is refused for tracked spawns (Kiln's marker) */
    uint32_t component_count;             /* <= KILN_SPAWN_MAX_COMPONENTS */
    uint32_t reserved;                    /* 0 */
} KilnSpawnRequest;

#define KILN_SPAWN_REQUEST_V1_SIZE 104u

/* capabilities() bits */
enum {
    KILN_SPAWN_CAP_COMPONENTS = 1u << 0, /* KilnSpawnRequest.components is honoured */
};

typedef struct KilnTemplateInfo {
    uint32_t size; /* set to sizeof(KilnTemplateInfo) before calling */
    uint32_t kind; /* KILN_TEMPLATE_* */
    KilnGuid guid;
    char name[128];   /* template name, NUL-terminated */
    char display[64]; /* in-game display name when the shipped catalog has one, else "" */
    uint32_t source;  /* bit 0: shipped catalog, bit 1: loaded by the engine */
    uint32_t flags;   /* KILN_TEMPLATE_FLAG_*, from the template's own component set */
} KilnTemplateInfo;

/* KilnTemplateInfo.flags */
enum {
    KILN_TEMPLATE_FLAG_KNOWN = 1u << 0,       /* the flags below were read from the engine */
    KILN_TEMPLATE_FLAG_DO_NOT_SAVE = 1u << 1, /* the template carries DoNotSave: its entities never reach the save */
    KILN_TEMPLATE_FLAG_LIFETIME = 1u << 2,    /* LifeTime: the game removes its entities after a while */
    KILN_TEMPLATE_FLAG_TRANSFORM = 1u << 3,   /* has CurrentTransform (placeable in the world) */
};

/* KilnTrackedEntity.state */
enum {
    KILN_TRACKED_LIVE = 0,    /* exists now under `entity` */
    KILN_TRACKED_MISSING = 1, /* not found in the world (gone, not saved, not re-found yet after a restart) */
};

typedef struct KilnTrackedEntity {
    uint32_t size;     /* set to sizeof(KilnTrackedEntity) before calling */
    KilnEntity entity; /* current id, 0 when missing */
    uint64_t serial;   /* Kiln's id for the record, stable across restarts */
    KilnGuid template_guid;
    KilnVec3 position;       /* last known position */
    KilnVec3 spawn_position;
    int64_t spawned_unix;    /* UTC seconds */
    uint32_t state;          /* KILN_TRACKED_* */
    uint32_t flags;          /* request flags */
    char tag[64];
    char origin[64];
    char name[128];          /* template name */
} KilnTrackedEntity;

typedef struct KilnEntitySnapshot {
    uint32_t size;         /* set to sizeof(KilnEntitySnapshot) before calling */
    uint32_t has_template; /* 1 when template_guid is valid */
    KilnGuid template_guid;
    KilnVec3 position;
    float rotation[4]; /* quaternion x, y, z, w */
    float yaw_degrees;
    uint32_t tracked;  /* 1 when this entity is a live tracked record */
    uint64_t serial;   /* its record serial, else 0 */
    int64_t spawn_time; /* the entity's SpawnTime component (ECS ns), if has_spawn_time */
    uint32_t has_spawn_time;
    KilnEntity parent;  /* the entity's parent in the engine's entity record (0 = none) */
} KilnEntitySnapshot;

typedef struct KilnSpawnCleanup {
    uint32_t size;      /* set to sizeof(KilnSpawnCleanup) before calling */
    uint32_t destroyed; /* records whose entity was destroyed */
    uint32_t missing;   /* records whose entity was not in the world (dropped) */
    uint32_t kept;      /* records that did not match the filter */
} KilnSpawnCleanup;

typedef struct KilnSpawnApi {
    KilnServiceHeader header; /* { sizeof(KilnSpawnApi), KILN_SPAWN_VERSION } */

    /* tick. Spawns one entity; *out = its id. NOT_FOUND: unknown template; INVALID_ARGUMENT: bad position or request;
     * INTERNAL: the engine refused (kiln.log has the game's reason); UNAVAILABLE: the world is not running yet. */
    KilnResult(KILN_CALL* spawn)(const KilnSpawnRequest* req, KilnEntity* out);

    /* tick. Drops `amount` (1..65535) of item `item_id` (the game's ItemId = FNV-1a-32 over the item GUID bytes) as a
     * loose pickup at `position` (the engine's generic pickup with gravity; players collect it like loot). level 0 =
     * item default. Not checked against the item registry (look ids up with kiln.items). */
    KilnResult(KILN_CALL* spawn_item)(uint32_t item_id, uint32_t amount, KilnVec3 position, int32_t level,
                                      uint32_t flags, KilnStr tag, KilnStr origin, KilnEntity* out);

    /* tick. Destroys a non-player entity (ids 1..1023 are refused) and drops its tracked record if it has one.
     * NOT_FOUND: no such entity. */
    KilnResult(KILN_CALL* destroy)(KilnEntity entity);

    /* tick. Whether the entity exists, with its template and transform. NOT_FOUND if it does not exist. */
    KilnResult(KILN_CALL* entity_info)(KilnEntity entity, KilnEntitySnapshot* out);

    /* tick. Entities of one template (guid not all zero), or whose template name contains name_filter
     * (case-insensitive; empty = any), within radius metres of center (radius <= 0: anywhere), nearest first.
     * Writes up to capacity ids; *count = all matches. */
    KilnResult(KILN_CALL* find)(const KilnGuid* guid, KilnStr name_filter, KilnVec3 center, double radius,
                                KilnEntity* out, size_t capacity, size_t* count);

    /* ---- templates (thread-safe) ---- */
    KilnResult(KILN_CALL* template_find)(KilnStr name, KilnTemplateInfo* out); /* name, display name or GUID text */
    KilnResult(KILN_CALL* template_get)(const KilnGuid* guid, KilnTemplateInfo* out);
    size_t(KILN_CALL* template_count)(void);
    KilnResult(KILN_CALL* template_at)(size_t index, KilnTemplateInfo* out); /* 0..count-1, sorted by name */

    /* ---- tracking ---- */
    size_t(KILN_CALL* tracked_count)(void);                                  /* thread-safe */
    KilnResult(KILN_CALL* tracked_at)(size_t index, KilnTrackedEntity* out); /* thread-safe */
    /* tick. Destroys tracked entities: tag empty = all, else only that tag; origin empty = any, else only that
     * origin. Re-checks the world first; records whose entity is gone are dropped (counted in missing). */
    KilnResult(KILN_CALL* cleanup)(KilnStr tag, KilnStr origin, KilnSpawnCleanup* out);
    /* tick. Re-checks every record now (positions of live ones; re-finds missing ones). *live = live records.
     * Kiln also does this by itself every few seconds. */
    KilnResult(KILN_CALL* refresh)(uint32_t* live);
    /* thread-safe. The world the sidecar belongs to (world GUID from the save header, as text). */
    KilnResult(KILN_CALL* world_key)(char* buffer, size_t capacity, size_t* out_len);
    /* thread-safe. KILN_SPAWN_WORLD_*: spawning works only once the world runs (the server steps its world while it
     * still loads the save; the loader creates entities of its own then). */
    uint32_t(KILN_CALL* world_state)(void);

    /* ---- appended later in v1 (check header.size) ---- */
    /* thread-safe. KILN_SPAWN_CAP_* bits of this build. */
    uint32_t(KILN_CALL* capabilities)(void);
} KilnSpawnApi;

/* world_state() */
enum {
    KILN_SPAWN_WORLD_NONE = 0,    /* no server world yet */
    KILN_SPAWN_WORLD_LOADING = 1, /* stepping, but the save is still loading: spawn returns KILN_ERR_UNAVAILABLE */
    KILN_SPAWN_WORLD_RUNNING = 2, /* "[game_server] Switching state from LoadEcsScene to Run" was logged */
};

#ifdef __cplusplus
}
#endif
#endif
