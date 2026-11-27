/* SPDX-License-Identifier: MIT
 * kiln.internal.registry v3: what Kiln.Host tells kiln.registry (kiln_registry.h) before the stage, a runtime pass's reads, and
 * (v3) Registry.Reauthor's turn prepared off the world tick. Kiln-internal
 * (docs/kiln/services/README.md "Kiln-internal tables"): NOT a public ABI, never in native/core/abi, kiln-abi or the SDK;
 * it may change between Kiln builds. Published by kiln-content.dll (a kiln/services DLL of this very build) and meant for
 * one consumer, the managed host: Kiln.Host checks header.version == KILN_REGISTRY_HOST_VERSION and header.size ==
 * sizeof(KilnRegistryHostApi) and, on a mismatch, logs it and runs the stage without its order (orderSource "folders").
 * Resolving it from the managed host's KilnApi needs the core to hand out this one published internal name to it
 * (native/core/src/api.cpp, api_service_managed_host), as it does kiln.internal.tasks.host: registry slice S11.
 *
 * Kiln mods' C# stage work (Mod.OnRegistry) needs nothing more here: Kiln.Host subscribes one stage callback per such mod
 * through the public kiln.registry subscribe_stage, with that mod's owner and id, and submits the edits its callbacks make
 * through the public submit; it releases what it held for the stage (the throwaway load contexts) at the START event.
 *
 * Threads: both members before the stage (KILN_REGISTRY_PHASE_NONE), any thread; Kiln.Host calls them from Init, after
 * its resolver ordered the mods. Once the stage began: KILN_ERR_UNAVAILABLE, nothing changes.
 */
#ifndef KILN_REGISTRY_HOST_H
#define KILN_REGISTRY_HOST_H
#include <stddef.h>
#include <stdint.h>

#include "kiln_api.h"
#include "services/kiln_registry_live.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_REGISTRY_HOST_SERVICE "kiln.internal.registry"
#define KILN_REGISTRY_HOST_VERSION 3u

/* set_mode: where the stage runs (P1-PLAN.md section 1). */
enum {
    KILN_REGISTRY_MODE_RESOURCES_READY = 0, /* option B, the default: at kiln.assets' resources-ready, over the live bytes */
    KILN_REGISTRY_MODE_ONBOOT = 1           /* option A: with OnBoot, over the committed boot generation's base view */
};

/* v3 (dev.32 LOOT-2): one call of a re-authored pass (one OnRegistry callback's IR, as kiln.registry.live submit takes it), and
 * a retained change a turn takes out. */
typedef struct KilnRegistryPassCall {
    KilnOwner owner; /* the pass's mod (a bound owner) */
    uint32_t point;  /* KILN_REGISTRY_LIVE_WORLD_LOAD */
    uint32_t reserved;
    KilnStr ir;
} KilnRegistryPassCall;
typedef struct KilnRegistryTurnItem {
    KilnOwner owner;
    uint64_t handle;
} KilnRegistryTurnItem;
#define KILN_REGISTRY_INSTALL_ANYWAY 1u /* install: a preparation stale only in its bindings lands the classic way */

typedef struct KilnRegistryHostApi {
    KilnServiceHeader header; /* { sizeof(KilnRegistryHostApi), KILN_REGISTRY_HOST_VERSION } */
    /* Kiln.Host's runtime load order (the launcher profile's order: required and optional dependencies, loadAfter,
     * loadBefore, ties by id) as a JSON array [{"mod": "<id>", "version": "<version>"}], every loaded Kiln mod once. A
     * row may add "registry": true: the mod's code uses the registry API (OnRegistry, Registry.Change/Read), so the stage
     * decodes the game's base at resources-ready, before packs write resident pages, not at first use. A row may add
     * "side": "server" | "client" for a one-side mod (kiln.mod.json's side; "both" or absent: both sides): the join's
     * registry fingerprint (bootSync) leaves out every boot field such a mod writes (docs/kiln/services/README.md). An
     * older kiln-content.dll ignores the key. A second call
     * replaces the first. KILN_ERR_INVALID_ARGUMENT: not such an array, or a mod twice (nothing changes). */
    KilnResult(KILN_CALL* set_order)(KilnStr order_json);
    /* KILN_REGISTRY_MODE_*. KILN_ERR_INVALID_ARGUMENT: another value. */
    KilnResult(KILN_CALL* set_mode)(uint32_t mode);
    /* v2 (review4 NS5): an automatic runtime registry pass's read (ModLoad, WorldLoad, HotReload). As kiln.registry.live
     * read, but the value leaves out every registry row of owner's bound mod (its stage edits and live changes): a pass
     * reads the pre-pass values without its own layers, so read-derived assignments never stack. World thread rules,
     * errors and the text contract are kiln.registry.live read's. Any time after START. */
    KilnResult(KILN_CALL* read_pass)(KilnOwner owner, KilnStr registry, KilnStr id, KilnStr field, char* buf, size_t cap,
                                     size_t* out_len);
    /* v3 (dev.32 LOOT-2, Registry.Reauthor off the world tick): Kiln.Host runs a re-authored WorldLoad pass on a worker thread
     * of its own and lands its turn on the world thread in one step (registry_live.h has the rules).
     * epoch: moves with every change of what a pass reads (any composition, a world's begin or end, a write below the
     * registry). Any thread. */
    uint64_t(KILN_CALL* epoch)(void);
    /* read_pass for a pass off the world thread: in world `world` (kiln.registry.live status's token) only, else
     * KILN_ERR_UNAVAILABLE. Any thread. */
    KilnResult(KILN_CALL* read_pass_in)(KilnOwner owner, uint64_t world, KilnStr registry, KilnStr id, KilnStr field, char* buf,
                                        size_t cap, size_t* out_len);
    /* Prepares a turn: the calls (in commit order) become the pass's changes, the replaced retained changes come out; parsed,
     * composed, bound and evaluated from the registry as it is at `epoch`. *ticket: what install takes. KILN_ERR_UNAVAILABLE
     * with *stale 1: the registry moved since epoch (run the pass again), 2: while preparing (prepare again). Any thread. */
    KilnResult(KILN_CALL* prepare)(const KilnRegistryPassCall* calls, size_t call_count, const KilnRegistryTurnItem* replaced,
                                   size_t replaced_count, uint64_t epoch, uint64_t* ticket, uint32_t* stale);
    /* Lands a prepared turn on the world thread. results: call_count + replaced_count (each call's, then each replaced
     * change's: REVERTED); handles: call_count (each call's retained change, 0: none). KILN_ERR_UNAVAILABLE with *stale (the
     * ticket is used up): 1 the pass's reads are stale, 2 only the preparation; KILN_REGISTRY_INSTALL_ANYWAY lands a
     * preparation stale in its bindings the classic way (composed and written on the world thread). */
    KilnResult(KILN_CALL* install)(uint64_t ticket, uint32_t flags, KilnRegistryLiveResult* results, size_t result_count,
                                   uint64_t* handles, size_t handle_count, uint32_t* stale);
    KilnResult(KILN_CALL* discard)(uint64_t ticket);
    /* Builds the registry.json report a landed turn owes, off the world thread. KILN_ERR_UNAVAILABLE: the registry moved
     * meanwhile (the world tick builds it a few seconds later if no later call does). Any thread. */
    KilnResult(KILN_CALL* build_report)(void);
} KilnRegistryHostApi;

#ifdef __cplusplus
}
#endif

#ifdef __cplusplus
static_assert(sizeof(KilnRegistryHostApi) == 80 && offsetof(KilnRegistryHostApi, set_order) == 8 &&
                  offsetof(KilnRegistryHostApi, set_mode) == 16 && offsetof(KilnRegistryHostApi, read_pass) == 24 &&
                  offsetof(KilnRegistryHostApi, epoch) == 32 && offsetof(KilnRegistryHostApi, read_pass_in) == 40 &&
                  offsetof(KilnRegistryHostApi, prepare) == 48 && offsetof(KilnRegistryHostApi, install) == 56 &&
                  offsetof(KilnRegistryHostApi, discard) == 64 && offsetof(KilnRegistryHostApi, build_report) == 72 &&
                  sizeof(KilnRegistryPassCall) == 32 && sizeof(KilnRegistryTurnItem) == 16,
              "kiln.internal.registry v3 (Kiln-internal; checked with the build stamp, not an ABI promise)");
#else
_Static_assert(sizeof(KilnRegistryHostApi) == 80 && offsetof(KilnRegistryHostApi, set_order) == 8 &&
                   offsetof(KilnRegistryHostApi, set_mode) == 16 && offsetof(KilnRegistryHostApi, read_pass) == 24 &&
                   offsetof(KilnRegistryHostApi, epoch) == 32 && offsetof(KilnRegistryHostApi, read_pass_in) == 40 &&
                   offsetof(KilnRegistryHostApi, prepare) == 48 && offsetof(KilnRegistryHostApi, install) == 56 &&
                   offsetof(KilnRegistryHostApi, discard) == 64 && offsetof(KilnRegistryHostApi, build_report) == 72 &&
                   sizeof(KilnRegistryPassCall) == 32 && sizeof(KilnRegistryTurnItem) == 16,
               "kiln.internal.registry v3 (Kiln-internal; checked with the build stamp, not an ABI promise)");
#endif

#endif /* KILN_REGISTRY_HOST_H */
