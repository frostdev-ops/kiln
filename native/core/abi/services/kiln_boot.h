/* SPDX-License-Identifier: MIT
 * kiln.boot v1 (in-core service, since 0.2.0-dev.28): Mod.OnBoot's definition phase. Mods whose kiln.mod.json says
 * "boot": true stage plain-data block definitions before the game is entered; six collectors prepare everything that
 * belongs to the generation (staged code, the mods' data, baked and pending files) and the core publishes it once,
 * before the game's entry point runs, so a first start already has the content.
 *
 * Deferred mode. The core decides it once, before the service DLLs load (kiln_plugin_main): it is on when hooks are
 * on, .NET is on, an enabled Kiln mod of this side has "boot": true or "boot": "required" in its kiln.mod.json, the
 * row "mods.boot" is available and, on a game client, the row "mods.boot.client" is available too (the client gate
 * holds it until the guided client checks pass). Without deferred mode nothing changes: collector() and the staging
 * calls answer KILN_ERR_UNAVAILABLE and every collector keeps its on_ready path.
 *
 * The phase, in order (one process, one generation; every step on the boot thread, before the game's entry point):
 *   1. bind      each collector calls collector() once from kiln_plugin_main or on_ready. KILN_OK: its prepare will be
 *                called and it must not load its definitions in on_ready. KILN_ERR_UNAVAILABLE: no deferred mode (or
 *                bind is over): keep the on_ready path. Collector ids, in their fixed prepare order: "assets",
 *                "worldtables", "content", "skills", "knowledge", "vfx".
 *   2. authors   Kiln.Host calls begin_owner() for each boot mod it runs, the mod's OnBoot stages definitions with
 *                stage(), finish_owner() closes the mod's frame (accepted, or excluded when OnBoot failed).
 *   3. lowering  Kiln.Host freezes all participants (files, baked files, staged code, pending caches), reserves ids and
 *                lowers the blocks into the IR envelope, which it stages as its own (stage(), kind "ir", see below).
 *   4. commit    commit() prepares every registered collector in the fixed order with the envelope, then publishes
 *                one generation root (status PUBLISHED). Consumers read their plan with active_plan().
 *   5. check     after .NET started the core reads status(): anything but PUBLISHED holds the game's entry point,
 *                writes <state>/boot.json and kiln.log, and ends the process with exit code KILN_BOOT_EXIT_CODE
 *                (0x4B01; a Unix wrapper sees its low byte, 1). No save or world is loaded after it.
 *
 * Failure policy (dev.28 coordinator ruling 7, owner decision 10). A mod marks its boot content required with
 * "boot": "required"; Kiln.Host passes KILN_BOOT_ROLE_REQUIRED to begin_owner. Content a required mod contributes that
 * fails (its OnBoot, a required dependency, a collector's refusal of one of its definitions) stops the start. Any
 * other failure drops that mod's whole contribution in one transaction: the plans prepared so far are destroyed in
 * reverse order, the mod (and every participant whose envelope entry "requires" it) leaves the envelope, its id goes
 * into "excluded" so collectors skip its files too, and every collector prepares again; nothing of a dropped mod is
 * published. A refusal no mod can be blamed for (the issue names no participant, a collector faulted or broke the
 * ABI) stops the start. Every step is logged and listed in the issues and in boot.json.
 *
 * The IR envelope (canonical UTF-8 JSON, schema 1). Kiln.Host stages it once as kind "ir", key "envelope", from the
 * owner that later commits (an owner that is not an author):
 *   { "schema": 1,
 *     "owners": [ { "id": "<mod id>", "roles": <KILN_BOOT_ROLE_* bits 0-2>, "fingerprint": "<hex>",
 *                   "requires": ["<mod id>", ...] } ],
 *     "reservations": { ... },
 *     "collectors": { "assets": [ { "owner": "<mod id>", "key": "<key>", "document": { ... } } ], "worldtables": [...],
 *                     "content": [...], "skills": [...], "knowledge": [...], "vfx": [...] } }
 * Each document is the JSON the corresponding service already takes from Kiln.Blocks. The core writes three things
 * into the copy each prepare sees: "generation", "excluded" (ids whose definitions and files must be left out) and,
 * per owner, "owner" (the KilnOwner of the process owner "boot:<mod id>", which lives until the process exits). With
 * no envelope staged, collectors see { "schema": 1, "generation", "owners": [], "excluded", "reservations": {},
 * "collectors": {} } and prepare from their own files.
 *
 * Prepare. Called on the boot thread with the entry point held, without any lock of this service held. It may read
 * files and other services' tables, cook and allocate; it must not publish anything, register game content, call
 * when_resources_ready or wait for any readiness (the game's resources start only after the entry point: such a wait
 * never ends). It returns KILN_OK with prepared->ok = 1 and a non-null plan (the empty dedicated-server vfx plan is a
 * non-null empty plan), or KILN_OK with prepared->ok = 0 and *issue filled (mod_id names the participant the refusal
 * is about; empty when it is about none). A warning travels with ok = 1 in *issue with bit 31 of code set. Any other
 * result code, ok = 1 with a null plan, ok = 1 with an error code in *issue, or a fault is a broken collector and stops
 * the start. A plan lives until the process exits once published; an unpublished one goes to its destroy function.
 *
 * Threads and buffers. status, begin_owner, stage, finish_owner, read_resource, commit and collector are boot-thread
 * calls (serialized); owner_state, issues and active_plan may be called from any thread at any time. Every string in
 * is UTF-8 and copied. Every out struct starts with its size, set by the caller; a size smaller than the v1 struct is
 * KILN_ERR_INVALID_ARGUMENT and nothing is written. Stage and resource capacities are bytes (the resource's required
 * count includes its NUL); issue capacity and required are records, written `stride` bytes apart. A too-small output
 * writes nothing.
 */
#ifndef KILN_BOOT_H
#define KILN_BOOT_H
#include <stddef.h>
#include <stdint.h>

#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif
#pragma pack(push, 8)

#define KILN_BOOT_SERVICE "kiln.boot"
#define KILN_BOOT_VERSION 1u

#define KILN_BOOT_EXIT_CODE 0x4B01u      /* the start stopped before the game's entry point (boot.json says why) */
#define KILN_BOOT_DEFINITION_MAX (1u << 20) /* bytes of one staged definition */
#define KILN_BOOT_ENVELOPE_MAX (64u << 20)  /* bytes of the IR envelope */
#define KILN_BOOT_OWNER_DEFINITIONS 4096u   /* definitions one author may stage */
#define KILN_BOOT_RESULT_MAX (4u << 20)     /* bytes of one read_resource result (the offline game-data view) */
#define KILN_BOOT_AUTHOR_READ_MAX (16u << 20) /* bytes of decoded game data one author may read in all */

/* KilnBootCommitResultV1.status: the process's boot phase. */
enum {
    KILN_BOOT_STATUS_COLLECTING = 0,       /* deferred mode; authors stage, nothing is published */
    KILN_BOOT_STATUS_PREPARING = 1,        /* commit() is running the collectors */
    KILN_BOOT_STATUS_PUBLISHED = 2,        /* the generation root is published; active_plan() answers */
    KILN_BOOT_STATUS_ABORTED = 3,          /* nothing was published; the start stops (exit KILN_BOOT_EXIT_CODE) */
    KILN_BOOT_STATUS_ACTIVATION_FAULT = 4, /* published, then a consumer faulted while binding it (not undone) */
};

/* KilnBootOwnerStateV1.state: one mod's part in the generation (the same numbers as Kiln.BootModState). */
enum {
    KILN_BOOT_STATE_NOT_PARTICIPATING = 0,
    KILN_BOOT_STATE_STAGED = 1,              /* its OnBoot finished; waiting for the commit */
    KILN_BOOT_STATE_COMMITTED = 2,           /* its definitions are in the published generation */
    KILN_BOOT_STATE_EXCLUDED_CALLBACK = 3,   /* its OnBoot failed: none of its definitions or files are in */
    KILN_BOOT_STATE_EXCLUDED_DEPENDENCY = 4, /* a mod it requires was excluded or rejected */
    KILN_BOOT_STATE_REJECTED_PREPARE = 5,    /* a collector refused one of its definitions */
    KILN_BOOT_STATE_GENERATION_ABORTED = 6,  /* the whole generation was not published (another mod's failure) */
    KILN_BOOT_STATE_MISSED_PHASE = 7,        /* enabled after the phase (Kiln.Host records it; restart needed) */
    KILN_BOOT_STATE_ACTIVATION_FAULT = 8,    /* published, then its content faulted while a consumer bound it */
};

/* begin_owner roles and the envelope's "roles": where the mod runs (bits 0-2), plus the failure rule. */
enum {
    KILN_BOOT_ROLE_DEDICATED = 1u,      /* a dedicated server */
    KILN_BOOT_ROLE_HOSTED = 2u,         /* the server world a game client hosts */
    KILN_BOOT_ROLE_CLIENT = 4u,         /* the game client */
    KILN_BOOT_ROLE_REQUIRED = 1u << 31, /* "boot": "required": a failure of this mod's content stops the start */
};

/* finish_owner accept */
enum {
    KILN_BOOT_FINISH_CALLBACK_FAILED = 0, /* OnBoot threw or faulted: KILN_BOOT_STATE_EXCLUDED_CALLBACK */
    KILN_BOOT_FINISH_ACCEPT = 1,          /* keep what it staged: KILN_BOOT_STATE_STAGED */
    KILN_BOOT_FINISH_DEPENDENCY = 2,      /* a dependency it requires is out: KILN_BOOT_STATE_EXCLUDED_DEPENDENCY */
};

/* KilnBootIssueV1.code. Bit 31 marks a warning (it never stops or drops anything); every other code is an error.
 * Collectors may use their own codes from 0x10000 up (bit 31 for their warnings). */
#define KILN_BOOT_ISSUE_WARNING 0x80000000u
enum {
    KILN_BOOT_ISSUE_DUPLICATE_DEFINITION = 1, /* stage: another definition under the same kind and key */
    KILN_BOOT_ISSUE_CALLBACK_FAILED = 2,      /* the mod's OnBoot failed */
    KILN_BOOT_ISSUE_DEPENDENCY = 3,           /* a mod it requires is out */
    KILN_BOOT_ISSUE_PREPARE_REFUSED = 4,      /* a collector refused (its reason says what) */
    KILN_BOOT_ISSUE_COLLECTOR_FAULT = 5,      /* a collector faulted or broke the prepare contract */
    KILN_BOOT_ISSUE_COLLECTOR_MISSING = 6,    /* definitions for a collector that did not register */
    KILN_BOOT_ISSUE_GENERATION_ABORTED = 7,   /* not published because of another failure */
    KILN_BOOT_ISSUE_NOT_COMMITTED = 8,        /* .NET or Kiln.Host never committed the generation */
    KILN_BOOT_ISSUE_WRONG_ROLE = 9,           /* lowered content needs a role the mod does not have */
    KILN_BOOT_ISSUE_ACTIVATION_FAULT = 10,    /* a consumer faulted binding published content */
};
#define KILN_BOOT_ISSUE_SHADOWED_BY_FILE (KILN_BOOT_ISSUE_WARNING | 1u) /* a file definition won over staged code */

/* all inputs UTF8 and copied; key/kind grammars and limits in chapter08 */
typedef struct KilnBootStageResultV1 {
    uint32_t size, ok;
    char reason[256]; /* "<Code>: <words>" when ok is 0 ("DuplicateDefinition: ...", "PhaseClosed: ...") */
} KilnBootStageResultV1;

typedef struct KilnBootIssueV1 {
    uint32_t size, code;
    char mod_id[128], kind[32], key[192], phase[32], reason[256];
} KilnBootIssueV1;

/* process status: 0 Collecting,1 Preparing,2 Published,3 Aborted,4 ActivationFault */
typedef struct KilnBootCommitResultV1 {
    uint32_t size, status, owner_count, issue_count;
    uint64_t generation;
} KilnBootCommitResultV1;

/* owner state numeric values equal BootModState in chapter08 */
typedef struct KilnBootOwnerStateV1 {
    uint32_t size, state, definition_count, issue_count;
    uint64_t generation;
    char mod_id[128];
} KilnBootOwnerStateV1;

typedef struct KilnBootPrepareInputV1 {
    uint32_t size, version; /* version: KILN_BOOT_VERSION */
    uint64_t generation;
    KilnStr canonical_ir_json; /* the envelope (see above); valid for the call only */
} KilnBootPrepareInputV1;

typedef struct KilnBootPreparedV1 {
    uint32_t size, ok;
    const void* plan;                       /* opaque; the collector's own */
    void(KILN_CALL* destroy)(const void*);  /* frees an unpublished plan; may be NULL */
} KilnBootPreparedV1;

typedef KilnResult(KILN_CALL* KilnBootPrepareFnV1)(void* user, const KilnBootPrepareInputV1*, KilnBootPreparedV1*,
                                                   KilnBootIssueV1*);

typedef struct KilnBootApiV1 {
    KilnServiceHeader header; /* { sizeof(KilnBootApiV1), KILN_BOOT_VERSION } */

    /* The boot phase. KILN_ERR_UNAVAILABLE when this process has no deferred generation (out is left alone). */
    KilnResult(KILN_CALL* status)(KilnBootCommitResultV1*);

    /* Opens `mod_id`'s author frame (Manifest.IdPattern, ^[a-z0-9][a-z0-9._-]{0,63}$) for the transient author owner
     * `runtime_author` and writes the generation all authors stage into. roles: KILN_BOOT_ROLE_* (bits 0-2 not 0).
     * KILN_ERR_UNAVAILABLE: no deferred mode, or the authors' step is over. KILN_ERR_INVALID_ARGUMENT: a bad id or
     * roles, owner 0, an id or owner that began already. */
    KilnResult(KILN_CALL* begin_owner)(KilnOwner runtime_author, KilnStr mod_id, uint32_t roles, uint64_t* generation);

    /* Stages one definition: kind is one of the boot kinds (statuses, effects, items, spells, talents, talentTrees,
     * npcs, creatures, materialTweaks, buildingMaterials, terrainMaterials, blueprints, props, stations, vfx,
     * materials, quests, dialogues, bodies, animations, ais, behaviours, perks, abilities, looks, strengths,
     * surfaces, resourcePatches), key matches ^[a-z][a-z0-9_.-]{0,95}$ without "..", canonical_json is one JSON object of
     * at most
     * KILN_BOOT_DEFINITION_MAX bytes. From the host (an owner that is not an author) kind "ir", key "envelope" stages
     * the IR envelope, once. KILN_OK with ok 1: staged (the same bytes again are fine); KILN_OK with ok 0: refused
     * (DuplicateDefinition, PhaseClosed, TooMany), reason says why. KILN_ERR_INVALID_ARGUMENT: a bad kind, key, JSON,
     * generation or owner, out too small. KILN_ERR_UNAVAILABLE: no deferred mode. */
    KilnResult(KILN_CALL* stage)(KilnOwner, uint64_t generation, KilnStr kind, KilnStr key, KilnStr canonical_json,
                                 KilnBootStageResultV1*);

    /* Closes an author frame: accept is KILN_BOOT_FINISH_*. A frame still open at commit() counts as failed. */
    KilnResult(KILN_CALL* finish_owner)(KilnOwner, uint64_t generation, uint32_t accept);

    /* The bounded offline game-data view (one query, see Kiln.IReadOnlyGameData.Read) for an open author frame:
     * item:<debugName>, template:<debugName>, skill:0x<8 hex digits>, material:<name or CellN>,
     * resource:<canonical GUID>/<8 hex typeHash>/<decimal part>. Writes {resourceId,typeHash,part,value} as JSON with
     * its NUL. KILN_ERR_BUFFER_TOO_SMALL sets *required and writes nothing; KILN_ERR_NOT_FOUND: no such record;
     * KILN_ERR_REVOKED: the owner's frame is closed; KILN_ERR_INVALID_ARGUMENT: a query outside the grammar;
     * KILN_ERR_UNAVAILABLE: no reader (row "mods.boot.game_data"), a type it cannot decode, or the author's budget
     * (KILN_BOOT_RESULT_MAX, KILN_BOOT_AUTHOR_READ_MAX) is spent. */
    KilnResult(KILN_CALL* read_resource)(KilnOwner, KilnStr query, char* json, size_t capacity, size_t* required);

    /* Prepares and publishes the generation (see "The phase" and "Failure policy"). KILN_OK: the protocol ran and
     * *out says PUBLISHED or ABORTED. KILN_ERR_UNAVAILABLE: no deferred mode, or committed already (out left alone).
     * KILN_ERR_INVALID_ARGUMENT: host 0 or an author, another owner staged the envelope, a wrong generation. */
    KilnResult(KILN_CALL* commit)(KilnOwner host, uint64_t generation, KilnBootCommitResultV1*);

    /* One mod's state (any thread). An id that takes no part: KILN_OK, NOT_PARTICIPATING. */
    KilnResult(KILN_CALL* owner_state)(KilnStr mod_id, KilnBootOwnerStateV1*);

    /* The issues of `generation` (any thread), oldest first. stride >= sizeof(KilnBootIssueV1); each record's size is
     * set. KILN_ERR_BUFFER_TOO_SMALL sets *required (records) and writes nothing. */
    KilnResult(KILN_CALL* issues)(uint64_t generation, KilnBootIssueV1*, uint32_t capacity, uint32_t stride,
                                  uint32_t* required);

    /* the native collectors (assets, worldtables, content, skills, knowledge, vfx, patches) register once during bind;
     * no publication callback */
    KilnResult(KILN_CALL* collector)(KilnOwner, KilnStr collector_id, KilnBootPrepareFnV1, void* user);

    /* consumer hooks read through the same atomic committed generation; borrowed until process exit. NULL before the
     * generation is published, for a collector that did not register and in a process without deferred mode. */
    const void*(KILN_CALL* active_plan)(KilnStr collector_id, uint64_t* generation);

    /* Appended (0.2.0-dev.28, resource patches); check header.size before calling. A registered collector reports that
     * published content of `mod_id` could not be applied after the publication (kind and key name the definition,
     * reason says why, in words a mod author can act on). Recorded as an issue of code KILN_BOOT_ISSUE_ACTIVATION_FAULT,
     * phase "activation"; the mod's state and the phase status become ACTIVATION_FAULT; nothing is undone. For a mod
     * whose boot content is required the start stops here as it does before the entry point: the state files are
     * written and the process ends with KILN_BOOT_EXIT_CODE (the call does not return). Any thread.
     * KILN_ERR_UNAVAILABLE: no deferred mode, or nothing is published yet. KILN_ERR_INVALID_ARGUMENT: the caller is not
     * a registered collector's owner, an unknown mod, an empty reason. */
    KilnResult(KILN_CALL* activation_fault)(KilnOwner collector, KilnStr mod_id, KilnStr kind, KilnStr key, KilnStr reason);
} KilnBootApiV1;

#pragma pack(pop)
#ifdef __cplusplus
}
#endif
#endif
