/* SPDX-License-Identifier: MIT
 * kiln.registry.live v1 (since mod API 0.2.17): explicit owner-scoped live registry changes. Kiln ids/fields only;
 * kiln.registry v1 is unchanged. Layers are above packs/start-time registry edits and below Content.* code.
 * begin/submit draft copied IR; commit retains the change until revert, owner revoke or its world's end.
 * Later commits win and are reported. Revert recomposes the remaining layers, not an old memory snapshot.
 * Writes AND reverts compare against Kiln's last bytes. Another writer's bytes are skipped and reported, never restored
 * over. Native owner retirement only queues cleanup; the world thread applies it. No callback quiescence is promised.
 *
 * Entry points are declared by the caller: MOD_LOAD (process inputs, may wait for a world), WORLD_LOAD (world settings),
 * HOT_RELOAD (reloaded mod/config; world-scoped when a world exists), MANUAL (explicit runtime transaction).
 * GAME_START uses kiln.registry's existing add/alter/final stage, not this table. Later entry points never write restart
 * fields: they report RESTART_REQUIRED. A worldLoad field committed into a running world by any point but WORLD_LOAD is
 * APPLIED and written, with a reason naming it: its full effect starts at the next world load (the save was already read).
 * WORLD_LOAD and MANUAL need a world. A world-bound draft cannot cross worlds.
 * MOD_LOAD changes persist across worlds; other world-bound changes expire when that world stops. Settings-dependent
 * inputs belong only to WORLD_LOAD, HOT_RELOAD with a world, or MANUAL. Callers include settings/world/point identity
 * in their batch source. The managed context integration supplies that identity; there is no live-plan cache in v1.
 *
 * Threads: begin/submit/status any thread. commit/revert/read on the active world's thread while a world exists;
 * without a world they touch only owned archive/copied data, never world slots. Kiln.Host names the mods whose code uses
 * the registry API, and the stage then prepares the stable base at resources-ready. Otherwise (a native plugin alone) the
 * first begin/read prepares it, outside kiln.content's store lock, from the game's bytes with Kiln's own writes taken back
 * out (another loader's writes stay); status/schema alone do not force archive decoding.
 * Drafts and committed handles are owner-bound; owner 0 is invalid;
 * revoked owners return REVOKED. Unknown/stale handles return NOT_FOUND, another owner's handle INVALID_ARGUMENT, and an
 * IR whose "mod" is not the caller's bound mod INVALID_ARGUMENT (this table's form of PERMISSION). Submit or commit on a handle
 * already committed returns UNAVAILABLE. A commit that accepts nothing (REFUSED, RESTART_REQUIRED) consumes the handle, as
 * revert does; a WORLD_REQUIRED commit leaves the draft for the caller to revert.
 * Limits: 64 open drafts and 1024 retained changes in the process, 16 open drafts and 256 retained changes per owner (begin
 * returns UNAVAILABLE at any of them; kiln.log names the cap), each registry's edits per change at most its schema's
 * editsPerCall (4096 unless the schema declares more; randomLoot 16384; since 0.2.0-dev.32, before: 4096 edits in all, a submit
 * past it INVALID_ARGUMENT): a submit past a registry's cap is KILN_OK with nothing accepted and its batch refused whole, one
 * refused row per registry it names (the over-cap ones first), 64 MiB copied IR total. Revert releases
 * the handle.
 * Entry add/remove and raw game paths are refused by this stable API (the existing Advanced APIs are unchanged).
 *
 * IR/schema/buffer rules are kiln_registry.h's. Reading text uses BUFFER_TOO_SMALL and the whole required length;
 * submit's optional report never changes its result to BUFFER_TOO_SMALL. Result out is optional; when supplied, size
 * must cover v1, larger records are accepted and size is set to v1. Refused edits are reported per edit. MOD_LOAD,
 * WORLD_LOAD and HOT_RELOAD changes refuse per entry, like the stage: a selected entry that cannot take an edit (an exclusive
 * conflict, an entry missing in this world, a config it lacks, an inexact whole-number value with reason "not_exact: ") is
 * refused alone, counted in refused, and the rest applies (APPLIED, reason = the first refusal); an entry refused at binding
 * is left out for that world only. A MANUAL transaction is all-or-nothing: a binding or evaluation refusal of any entry
 * refuses the whole commit. A candidate's own failed write rolls it back; UNVERIFIED means rollback could not be proved,
 * never success.
 * A retained change that cannot apply at a later application (a binding or evaluation failure, or a failed write of one of
 * its own slots) is inactive for that world only: its handle stays valid, a MOD_LOAD change is tried again in the next world,
 * and registry.json names its mod. A failed write is charged to the change owning that slot, never to another change. A slot
 * another writer took after Kiln's layer stays with that writer for the world; the change's other fields stay applied.
 * read returns actual live bytes through Kiln's field mappings; restart fields use the kept start-time composition,
 * and pre-world reads include composed pending process changes. Select ids/schema/report/ready events through kiln.registry. Transactions queue a report/generation update for
 * the next registry pump (a world tick of kiln.content; a change committed before any world is published once a world is
 * ready); multiple updates may coalesce. They never send WORLDLOAD or dispatch ready callbacks from
 * commit/revert. Only real world/pack applications send WORLDLOAD. Result.generation is the last published snapshot.
 * A nested commit/revert while another transaction is in flight returns UNAVAILABLE.
 */
#ifndef KILN_REGISTRY_LIVE_H
#define KILN_REGISTRY_LIVE_H
#include "kiln_registry.h"
#ifdef __cplusplus
extern "C" {
#endif
#define KILN_REGISTRY_LIVE_SERVICE "kiln.registry.live"
#define KILN_REGISTRY_LIVE_VERSION 1u
/* Stable reason token: REFUSED + reason beginning "not_exact: " means a valid numeric value cannot be represented
 * by this entry's whole-number cost storage. Float-config entries retain floats. Never infer this from human text. */
#define KILN_REGISTRY_LIVE_REASON_NOT_EXACT "not_exact"

enum { KILN_REGISTRY_LIVE_MOD_LOAD = 1, KILN_REGISTRY_LIVE_WORLD_LOAD = 2,
       KILN_REGISTRY_LIVE_HOT_RELOAD = 3, KILN_REGISTRY_LIVE_MANUAL = 4 };
enum { KILN_REGISTRY_LIVE_APPLIED = 0, KILN_REGISTRY_LIVE_PENDING_WORLD = 1,
       KILN_REGISTRY_LIVE_REFUSED = 2, KILN_REGISTRY_LIVE_RESTART_REQUIRED = 3,
       KILN_REGISTRY_LIVE_WORLD_REQUIRED = 4, KILN_REGISTRY_LIVE_REVERTED = 5,
       KILN_REGISTRY_LIVE_SKIPPED = 6, KILN_REGISTRY_LIVE_UNVERIFIED = 7,
       KILN_REGISTRY_LIVE_PENDING_RESOURCE = 8 };

typedef struct KilnRegistryLiveResult {
    uint32_t size, outcome, accepted, refused, skipped, reserved;
    uint64_t world; /* Kiln-owned process-local world token, 0 without a world; NEVER a game pointer */
    uint64_t generation;
    char reason[192]; /* UTF-8, NUL-terminated, cut to fit */
} KilnRegistryLiveResult;
typedef struct KilnRegistryLiveStatus {
    uint32_t size, world_kind; /* KILN_WORLD_KINDS when no world */
    uint64_t world, generation;
    uint32_t drafts, active;
} KilnRegistryLiveStatus;
typedef struct KilnRegistryLiveApi {
    KilnServiceHeader header;
    KilnResult(KILN_CALL* begin)(KilnOwner owner, uint32_t entry_point, KilnHandle* out);
    KilnResult(KILN_CALL* submit)(KilnOwner owner, KilnHandle change, KilnStr ir_json, KilnRegistryLiveResult* out,
                                char* report, size_t cap, size_t* out_len);
    KilnResult(KILN_CALL* commit)(KilnOwner owner, KilnHandle change, KilnRegistryLiveResult* out);
    KilnResult(KILN_CALL* revert)(KilnOwner owner, KilnHandle change, KilnRegistryLiveResult* out);
    KilnResult(KILN_CALL* read)(KilnOwner owner, KilnStr registry, KilnStr id, KilnStr field,
                              char* buf, size_t cap, size_t* out_len);
    KilnResult(KILN_CALL* status)(KilnRegistryLiveStatus* out);
} KilnRegistryLiveApi;
#ifdef __cplusplus
}
#define KILN_REG_LIVE_ASSERT static_assert
#else
#define KILN_REG_LIVE_ASSERT _Static_assert
#endif
KILN_REG_LIVE_ASSERT(sizeof(KilnRegistryLiveResult) == 232 && offsetof(KilnRegistryLiveResult, outcome) == 4 &&
    offsetof(KilnRegistryLiveResult, accepted) == 8 && offsetof(KilnRegistryLiveResult, refused) == 12 &&
    offsetof(KilnRegistryLiveResult, skipped) == 16 && offsetof(KilnRegistryLiveResult, reserved) == 20 &&
    offsetof(KilnRegistryLiveResult, world) == 24 && offsetof(KilnRegistryLiveResult, generation) == 32 &&
    offsetof(KilnRegistryLiveResult, reason) == 40, "KilnRegistryLiveResult v1");
KILN_REG_LIVE_ASSERT(sizeof(KilnRegistryLiveStatus) == 32 && offsetof(KilnRegistryLiveStatus, world_kind) == 4 &&
    offsetof(KilnRegistryLiveStatus, world) == 8 && offsetof(KilnRegistryLiveStatus, generation) == 16 &&
    offsetof(KilnRegistryLiveStatus, drafts) == 24 && offsetof(KilnRegistryLiveStatus, active) == 28, "KilnRegistryLiveStatus v1");
KILN_REG_LIVE_ASSERT(sizeof(KilnRegistryLiveApi) == 56 && offsetof(KilnRegistryLiveApi, begin) == 8 &&
    offsetof(KilnRegistryLiveApi, submit) == 16 && offsetof(KilnRegistryLiveApi, commit) == 24 &&
    offsetof(KilnRegistryLiveApi, revert) == 32 && offsetof(KilnRegistryLiveApi, read) == 40 &&
    offsetof(KilnRegistryLiveApi, status) == 48, "KilnRegistryLiveApi v1");
#undef KILN_REG_LIVE_ASSERT
#endif
