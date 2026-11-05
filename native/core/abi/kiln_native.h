/* SPDX-License-Identifier: MIT
 * Kiln native mods, ABI 1: DLL mods described by a native.json (docs/kiln/launcher/contracts.md, schema/native.schema.json).
 *
 * A native mod is one or more DLLs that Kiln loads with LoadLibrary at a chosen phase. It does not need to know Kiln:
 * a plain DLL mod (no "init" in native.json) is only loaded, and its DllMain does the rest. A Kiln-aware native mod
 * names an exported init function in native.json "init"; Kiln calls it once, right after loading the DLL that exports
 * it, with a KilnNativeContext.
 *
 * Phases (native.json "phase"):
 *   dllmain  loaded by the proxy inside its DllMain, under the loader lock, before the core exists (kiln/chain.ini
 *            [profile]). No init call: "init" must be absent. Risky (loader-lock deadlocks); the launcher warns.
 *   early    (default) at the start of kiln_boot, after paths, log and kiln.json; runs even in safe idle.
 *   boot     after Kiln's service DLLs and plugins ran (kiln_plugin_main, on_ready); runs without hooks too.
 *   world    at the first world step (the server world on a server, the first client world on a client); needs hooks.
 * Each load and init call is guarded by a structured-exception handler: a fault marks that mod failed, the game runs
 * on. Kiln never calls FreeLibrary on a native mod. Status goes to <kiln>/state/native.json.
 */
#ifndef KILN_NATIVE_H
#define KILN_NATIVE_H

#include "kiln_api.h"

#ifdef __cplusplus
extern "C" {
#endif

#define KILN_NATIVE_ABI_VERSION 1u

/* KilnNativeContext.phase */
enum {
    KILN_NATIVE_PHASE_DLLMAIN = 0,
    KILN_NATIVE_PHASE_EARLY = 1,
    KILN_NATIVE_PHASE_BOOT = 2,
    KILN_NATIVE_PHASE_WORLD = 3,
};

/* Return values of the init function. Anything else counts as KILN_NATIVE_INIT_FAILED. */
enum {
    KILN_NATIVE_INIT_OK = 0,
    KILN_NATIVE_INIT_FAILED = 1,      /* the mod could not start; it stays loaded, its status is "failed" */
    KILN_NATIVE_INIT_UNSUPPORTED = 2, /* this game build / side is not supported; status "unsupported", not an error */
};

/* Passed to init. Filled by the core; the struct and every string in it stay valid for the life of the process. */
typedef struct KilnNativeContext {
    uint32_t size;          /* sizeof(KilnNativeContext) of the core that filled it: check before using later fields */
    uint32_t abi_version;   /* KILN_NATIVE_ABI_VERSION */
    uint32_t phase;         /* KILN_NATIVE_PHASE_* this call runs in */
    uint32_t side;          /* KILN_SIDE_SERVER or KILN_SIDE_CLIENT */
    const KilnApi* api;     /* the Kiln API (same table as kiln_plugin_main gets) */
    KilnOwner owner;        /* owner of everything this mod registers (created for it, id "native:<id>") */
    void* module;           /* HMODULE of the DLL that exports init */
    KilnStr id;             /* native.json "id" */
    KilnStr version;        /* native.json "version" */
    KilnStr directory;      /* the mod's folder (absolute, Windows form, UTF-8) */
    KilnStr config_dir;     /* <configDir>/native/<id>: the mod's own settings files; created before init */
    KilnStr data_dir;       /* <dataDir>/<id>: persistent per-mod data (dataDir = profile dataDir or kiln/data) */
    uint32_t hooks_enabled; /* 0 = safe idle: hooks and game-memory functions of api return KILN_ERR_UNAVAILABLE */
    uint32_t play_once;     /* KILN_PLAY_ONCE_* of this launch (KilnBootInfo.play_once) */
} KilnNativeContext;

/* int __cdecl <init>(const KilnNativeContext* ctx); the export name comes from native.json "init". Runs on the game's
 * main thread (phase world: on that world's thread). Look services up with ctx->api->service. */
typedef int(KILN_CALL* KilnNativeInitFn)(const KilnNativeContext* ctx);

/* The export Kiln's templates use; native.json may name any other. */
#define KILN_NATIVE_INIT_DEFAULT "kiln_native_init"

#ifdef __cplusplus
}
#endif

#endif /* KILN_NATIVE_H */
