/* SPDX-License-Identifier: MIT
 * Kiln C ABI, version 1.
 *
 * One table of function pointers (KilnApi) handed to every consumer: the managed host and native plugins.
 * Rules (stable for the life of ABI major 1):
 *  - The table only grows at the end. Check `size` before using a field added later than your build.
 *  - Functions return KilnResult codes (int32). Strings are UTF-8 (ptr, len) and need not be NUL-terminated.
 *  - Game memory never outlives a callback: no pointer you receive is valid after the callback returns.
 *  - Everything a consumer registers belongs to an owner; owner_revoke(owner) removes all of it.
 *  - Tick callbacks run on the game's server-world thread. Use `post` to get there from any other thread. Client-side
 *    worlds (client, prediction) step on the game's main thread: subscribe_world_tick / post_world.
 *  - The `advanced` section is unstable: names and values depend on the game build.
 */
#ifndef KILN_API_H
#define KILN_API_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define KILN_ABI_VERSION 1u

#if defined(_WIN32)
#define KILN_CALL __cdecl
#define KILN_EXPORT __declspec(dllexport)
#else
#define KILN_CALL
#define KILN_EXPORT __attribute__((visibility("default")))
#endif

typedef int32_t KilnResult;
enum {
    KILN_OK = 0,
    KILN_ERR_INVALID_ARGUMENT = 1,
    KILN_ERR_NOT_FOUND = 2,
    KILN_ERR_UNAVAILABLE = 3,      /* the feature is off on this game build or by policy */
    KILN_ERR_WRONG_THREAD = 4,
    KILN_ERR_BUFFER_TOO_SMALL = 5,
    KILN_ERR_REVOKED = 6,
    KILN_ERR_INTERNAL = 7,
};

typedef struct KilnStr {
    const char* ptr;
    size_t len;
} KilnStr;

typedef uint64_t KilnOwner;  /* 0 = the core itself */
typedef uint64_t KilnHandle; /* 0 = invalid */

enum {
    KILN_LOG_ERROR = 1,
    KILN_LOG_WARN = 2,
    KILN_LOG_INFO = 3,
    KILN_LOG_DEBUG = 4,
    KILN_LOG_TRACE = 5,
};

enum {
    KILN_TICK_PRE = 0,  /* before the server world's sim step; none of its jobs are running */
    KILN_TICK_POST = 1, /* after the step and after the world's jobs finished */
};

/* ---- sides and worlds (client support; see docs/kiln/client.md) ---- */
enum {
    KILN_SIDE_SERVER = 0, /* enshrouded_server.exe (dedicated server) */
    KILN_SIDE_CLIENT = 1, /* enshrouded.exe: the game client, which also runs the world when it hosts */
};

/* World kinds (the u8 at world + world.storage + storage.kind). A dedicated server has one server world. A client has
 * a client and a prediction world while in game, plus the server world when it hosts (single player or co-op host). */
enum {
    KILN_WORLD_SERVER = 0,     /* authoritative gameplay world; on a client only while it hosts */
    KILN_WORLD_PREDICTION = 1, /* client-side prediction: only a storage (world(1) + world.storage), owned by the client
                                  world; no singleton map or program table of its own */
    KILN_WORLD_CLIENT = 2,     /* the client's own world: local player, camera, presentation */
    KILN_WORLD_KINDS = 3,
};

enum {
    KILN_SESSION_NONE = 0,      /* no world: booting, main menu, loading screen */
    KILN_SESSION_DEDICATED = 1, /* dedicated server, its world running */
    KILN_SESSION_HOST = 2,      /* client hosting the world (single player or co-op host): server world here too */
    KILN_SESSION_REMOTE = 3,    /* client joined another machine's world: no server world in this process */
};

enum {
    KILN_WORLD_STARTED = 1,  /* first sim step of a new world, before its first Pre, on the world's thread */
    KILN_WORLD_STOPPING = 2, /* the game is destroying the world; it is still intact. On the destroying thread */
};

enum {
    KILN_PATH_GAME = 0,    /* directory of the game executable */
    KILN_PATH_KILN = 1,    /* <game>/kiln */
    KILN_PATH_MODS = 2,    /* <game>/kiln/mods */
    KILN_PATH_LOGS = 3,    /* <game>/kiln/logs */
    KILN_PATH_DATA = 4,    /* <game>/kiln/data: per-mod persistent data lives below this */
    KILN_PATH_CONTROL = 5, /* <game>/kiln/control: command drop folder */
    /* appended later (an older core answers KILN_ERR_INVALID_ARGUMENT for these) */
    KILN_PATH_CONFIG = 6,  /* mod config files (<id>.json, .schema/): <game>/kiln/config, or the active profile's
                              configDir (docs/kiln/launcher/contracts.md) */
    KILN_PATH_STATE = 7,   /* <game>/kiln/state: status files Kiln writes for the launcher (boot.json, mods.json,
                              native.json, last-server.json); created at boot */
};

typedef struct KilnTickInfo {
    uint32_t size;          /* sizeof(KilnTickInfo) of the core that filled it */
    uint32_t world_kind;    /* 0 = server world */
    uint64_t step;          /* sim steps of this world since boot, starting at 1 */
    uint32_t clock;         /* the game's 60 Hz tick clock */
    uint32_t clock_delta;   /* clock advance since the previous step (2-3 on a dedicated server) */
    int64_t clock_period;   /* the game's tick period value (raw; see docs/re/emf-core.md) */
    double wall_delta_ms;   /* wall time since the previous step started */
} KilnTickInfo;

typedef struct KilnBuildInfo {
    uint32_t size;
    uint32_t side;          /* KILN_SIDE_SERVER (0) or KILN_SIDE_CLIENT (1) */
    char exe_sha256[65];    /* lower-case hex, NUL-terminated */
    uint8_t verified;       /* 1 if this exe is in the verified-builds list */
    uint8_t hooks_enabled;  /* 0 = "safe idle": nothing is hooked, the game runs vanilla */
    uint8_t reserved[5];
    KilnStr kiln_version;
} KilnBuildInfo;

typedef struct KilnWorldEvent {
    uint32_t size;
    uint32_t event;   /* KILN_WORLD_STARTED / KILN_WORLD_STOPPING */
    uint32_t kind;    /* KILN_WORLD_* */
    uint32_t session; /* worlds of this kind seen since boot, from 1 */
    uintptr_t world;
} KilnWorldEvent;

typedef struct KilnSessionInfo {
    uint32_t size;                       /* set by the caller: sizeof(KilnSessionInfo) */
    uint32_t state;                      /* KILN_SESSION_* */
    uint32_t side;                       /* KILN_SIDE_* */
    uint32_t generation;                 /* changes whenever a world starts or stops */
    uintptr_t worlds[KILN_WORLD_KINDS];  /* live world per kind, 0 = none */
    uint32_t sessions[KILN_WORLD_KINDS]; /* worlds per kind since boot */
    uint32_t lifecycle;                  /* 1 = world stops are seen (hook on the game's world destroy) */
    uint64_t steps[KILN_WORLD_KINDS];    /* sim steps per kind since boot */
} KilnSessionInfo;

typedef void(KILN_CALL* KilnTickFn)(const KilnTickInfo* tick, void* user);
typedef void(KILN_CALL* KilnWorldEventFn)(const KilnWorldEvent* event, void* user);
typedef void(KILN_CALL* KilnTaskFn)(void* user);
/* level: KILN_LOG_*; text: one game log line without the trailing newline. Runs on Kiln's log thread. */
typedef void(KILN_CALL* KilnLogLineFn)(int32_t level, KilnStr text, void* user);

typedef struct KilnApi {
    uint32_t size;
    uint32_t abi_version;

    /* ---- basics ---- */
    KilnStr(KILN_CALL* version)(void);
    void(KILN_CALL* log)(KilnOwner owner, int32_t level, KilnStr text);
    KilnResult(KILN_CALL* build_info)(KilnBuildInfo* out);
    bool(KILN_CALL* feature)(KilnStr name);
    /* The folder of `kind` (KILN_PATH_*) as an absolute Windows-form path in UTF-8 (not NUL-terminated). Convert it with a
     * wide-character call before it reaches a file API: a narrow call reads UTF-8 bytes in the ANSI code page, which
     * lacks Cyrillic, CJK and most other non-Latin letters. KILN_ERR_BUFFER_TOO_SMALL sets *out_len to the length needed. */
    KilnResult(KILN_CALL* path)(int32_t kind, char* buffer, size_t capacity, size_t* out_len);

    /* ---- ownership ---- */
    KilnOwner(KILN_CALL* owner_create)(KilnStr id);
    void(KILN_CALL* owner_revoke)(KilnOwner owner); /* call on the tick thread, or before the first tick */

    /* ---- tick ---- */
    KilnResult(KILN_CALL* subscribe_tick)(KilnOwner owner, int32_t phase, KilnTickFn fn, void* user,
                                          KilnHandle* out);
    KilnResult(KILN_CALL* unsubscribe)(KilnHandle handle);
    KilnResult(KILN_CALL* post)(KilnOwner owner, KilnTaskFn fn, void* user);
    bool(KILN_CALL* on_tick_thread)(void);
    KilnResult(KILN_CALL* tick_info)(KilnTickInfo* out);

    /* ---- game log mirror ---- */
    KilnResult(KILN_CALL* subscribe_log)(KilnOwner owner, KilnLogLineFn fn, void* user, KilnHandle* out);

    /* ---- advanced (unstable) ---- */
    KilnResult(KILN_CALL* symbol)(KilnStr name, uintptr_t* out);  /* absolute address */
    KilnResult(KILN_CALL* constant)(KilnStr name, int64_t* out);  /* layout constant, e.g. "world.storage" */

    /* ---- services ----
     * Returns the function table of a named service (e.g. "kiln.teleport") whose version >= min_version, or NULL when
     * it does not exist or is unavailable on this game build / policy. Every table starts with
     * { uint32_t size; uint32_t version; } and is valid for the life of the process. Public headers:
     * native/core/abi/services/kiln_<name>.h. */
    const void*(KILN_CALL* service)(KilnStr name, uint32_t min_version);

    /* Publishes a service table under `name` (call from kiln_plugin_main). `table` starts with KilnServiceHeader and
     * must stay valid for the life of the process. A name can be registered once. */
    KilnResult(KILN_CALL* register_service)(KilnOwner owner, KilnStr name, const void* table);

    /* ---- server world (advanced) ----
     * server_world: the server world pointer, 0 before its first sim step. singleton: the object registered under
     * `name` (exact registered name, e.g. "g38_daytime::Daytime") in that world's singleton map, 0 when absent or
     * when feature "singletons" is off. Game memory: call on the tick thread. */
    uintptr_t(KILN_CALL* server_world)(void);
    uintptr_t(KILN_CALL* singleton)(uintptr_t world, KilnStr name);

    /* ---- sides and worlds (appended for client support: check `size` before use) ----
     * side: KILN_SIDE_* of this process (from the exe's identity), also when hooks are off.
     * world: the live world of a kind (KILN_WORLD_*), 0 when there is none (main menu, loading, a joined client's
     *   missing server world). world(KILN_WORLD_SERVER) == server_world(). Game memory: on that world's thread.
     * on_world_thread: true on the thread that steps worlds of `kind` (the server world: the tick thread; the client and
     *   prediction worlds: the game's main thread).
     * subscribe_world_tick: Pre/Post of one world kind's sim steps, on that world's thread. KILN_WORLD_SERVER is
     *   subscribe_tick. Client and prediction worlds finish their jobs inside the step, so their Post waits for
     *   nothing. Remove with unsubscribe.
     * post_world: run `fn` once on that world's thread before its next Pre (KILN_WORLD_SERVER: same as post).
     * subscribe_world: world lifecycle (KilnWorldEvent: STARTED, STOPPING). STOPPING needs the "world.lifecycle"
     *   feature; without it a world just stops stepping. Remove with unsubscribe.
     * session: what runs in this process (KilnSessionInfo; set out->size first). Any thread. */
    uint32_t(KILN_CALL* side)(void);
    uintptr_t(KILN_CALL* world)(uint32_t kind);
    bool(KILN_CALL* on_world_thread)(uint32_t kind);
    KilnResult(KILN_CALL* subscribe_world_tick)(KilnOwner owner, uint32_t kind, int32_t phase, KilnTickFn fn,
                                                void* user, KilnHandle* out);
    KilnResult(KILN_CALL* post_world)(KilnOwner owner, uint32_t kind, KilnTaskFn fn, void* user);
    KilnResult(KILN_CALL* subscribe_world)(KilnOwner owner, KilnWorldEventFn fn, void* user, KilnHandle* out);
    KilnResult(KILN_CALL* session)(KilnSessionInfo* out);
} KilnApi;

/* Common header of every service table. */
typedef struct KilnServiceHeader {
    uint32_t size;
    uint32_t version;
} KilnServiceHeader;

/* Server-world entity id (u32). 0 = none. On a dedicated server player entities are slot + 1 (1..16 with 16 slots);
 * 1..1023 is the engine's reserved player range. Not generation-checked in ABI 1: an id can be reused
 * after the entity is destroyed. */
typedef uint32_t KilnEntity;

/* World position in metres (the engine stores 32.32 fixed point; services convert). */
typedef struct KilnVec3 {
    double x, y, z;
} KilnVec3;

/* ---- native modules ----
 * Service DLLs (<game>/kiln/services/kiln-<name>.dll, shipped with Kiln) and plugins export `kiln_plugin_main`.
 * Load order: all services, then (kiln.json "plugins" on, not play-once no-mods) Kiln's own plugins
 * (<game>/kiln/plugins/kiln-*.dll), the profile's "plugin" entries (services/kiln_profile.h), then the plugin drop-ins
 * (the other DLLs in kiln/plugins: no profile, or profile dropins incl. "plugin"); each folder in file-name order; then
 * every on_ready in the same order. Everything runs on the main thread before the game's entry point, and only when
 * hooks are enabled (verified build or policy "any"). DLL mods without kiln_plugin_main: kiln_native.h. */
typedef struct KilnPluginInfo {
    uint32_t size;
    KilnStr id;
    KilnStr version;
    /* optional: called once when the process exits normally (exit code 0: a dedicated server stopped with Ctrl-C,
     * SIGINT or by the launcher, the player quitting the game), at the process's exit call: after the game saved,
     * closed its worlds and returned from main, before Windows ends the other threads and before any
     * DLL_PROCESS_DETACH, never under the loader lock. A plugin stays loaded for the life of the process; nothing is
     * unloaded after the call. Every service and plugin that loaded is called, in reverse load order (plugins before
     * the services they use), each on a thread kiln-core starts for the call (not the main thread, not a world
     * thread) while the exiting thread waits. A fault is caught (kiln.log and a crash report) and the next handler
     * runs; a handler that has not returned after 3 s is given up (named in kiln.log; its thread ends with the
     * process), and once 10 s have gone by in all, the rest are not called. Do: write your own files (atomically),
     * stop your own threads and wait for them briefly. Do not: touch game memory or call game code, post to or wait
     * for a world thread or the main thread, LoadLibrary or FreeLibrary, restore patched game code, end the process.
     * Not called: a killed process (TerminateProcess, kill -9, a forced quit), a crash (also one the game catches and
     * ends with a non-zero exit code), safe idle, kiln_plugin_early (early plugins) and native mods (kiln_native.h).
     * Where: under Wine (Linux servers, Proton clients, the Steam Deck) it is on. On native Windows it is off by
     * default in this release (not yet proven there): kiln.json "unloadAtExit": "on" turns it on, "off" turns it off
     * everywhere, "auto" (the default) is the rule above. kiln.log's "unload:" line at boot says which and why.
     * Test hosts may call it at any time. */
    void(KILN_CALL* on_unload)(void* user);
    void* user;
    /* optional: runs once after every service DLL and plugin has run kiln_plugin_main, still before the game's entry
     * point. Look up other services here (api->service), not in kiln_plugin_main. */
    void(KILN_CALL* on_ready)(void* user);
} KilnPluginInfo;

typedef KilnResult(KILN_CALL* KilnPluginMainFn)(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info);
#define KILN_PLUGIN_MAIN "kiln_plugin_main"

/* ---- proxy -> core ----
 * The proxy (dinput8.dll) fills KilnBootInfo and passes it to kiln_boot. Versioned by `size`:
 *  - v1 (older proxies): size 16, {size, reserved, proxy_module}.
 *  - v2: appends the play-once decision and the chain-load results (DLLs the proxy loaded in DllMain).
 *  - v3 (dev.24): appends addon_log_size.
 * The core reads a field only when `size` covers it (KILN_BOOT_INFO_HAS); a v1 proxy reads as play_once NONE and an
 * empty chain. Everything the proxy points to (entries, strings) stays valid for the life of the process: the proxy is
 * never unloaded and never calls FreeLibrary on a chained DLL. Formats: docs/kiln/launcher/contracts.md. */

/* KilnBootInfo.play_once: what <game>/kiln/play-once.ini asked for (the proxy reads it, then deletes it). */
enum {
    KILN_PLAY_ONCE_NONE = 0,     /* no play-once.ini: normal boot from kiln/profile.json (or the legacy layout) */
    KILN_PLAY_ONCE_VANILLA = 1,  /* mode=vanilla: the proxy loads nothing, so the core never sees this value */
    KILN_PLAY_ONCE_NO_MODS = 2,  /* mode=no-mods: Kiln and its own services run; no managed, native, EMF or chained mod */
    KILN_PLAY_ONCE_PROFILE = 3,  /* mode=profile: boot from play_once_profile instead of kiln/profile.json, once */
    /* a play-once.ini was found but not applied (boot as NONE): */
    KILN_PLAY_ONCE_IGNORED_EXPIRED = 16,     /* "created" older than 600 s, or more than 60 s in the future */
    KILN_PLAY_ONCE_IGNORED_UNDELETABLE = 17, /* the file could not be deleted (a stuck flag must not stick) */
    KILN_PLAY_ONCE_IGNORED_SERVER = 18,      /* dedicated servers ignore (and delete) it */
    KILN_PLAY_ONCE_IGNORED_INVALID = 19,     /* unreadable, unknown mode, missing field */
};

/* KilnChainEntry.status */
enum {
    KILN_CHAIN_LOADED = 0,    /* LoadLibrary succeeded; module is set */
    KILN_CHAIN_FAILED = 1,    /* LoadLibrary failed; error = GetLastError() */
    KILN_CHAIN_MISSING = 2,   /* the file does not exist */
    KILN_CHAIN_REFUSED = 3,   /* a Kiln or ShroudForge proxy, or not a PE DLL: never loaded */
    KILN_CHAIN_DUPLICATE = 4, /* same canonical path as an earlier entry: loaded once */
    KILN_CHAIN_SKIPPED = 5,   /* not loaded on purpose (play-once no-mods, entry disabled) */
    /* dev.21, appended: every proxy check passed, but the entry's DLL is also named in kiln/chain.ini [late]: the proxy did
     * not load it. kiln-core loads it after Kiln's services, plugins and native mods ran (module is NULL here;
     * state/boot.json and state/native.json report the outcome). An older core reads an unknown status as failed. EMF's own
     * loader (it starts EMF itself, which kiln-emf has to see when the plugins start) is never LATE: the proxy loads it in
     * its place. */
    KILN_CHAIN_LATE = 6,
};

/* KilnChainEntry.source: which list named the DLL. */
enum {
    KILN_CHAIN_SOURCE_CHAIN = 0,   /* kiln/chain.ini [chain]: a proxy the installer displaced (EMF's loader, unknown) */
    KILN_CHAIN_SOURCE_PROFILE = 1, /* kiln/chain.ini [profile]: a native mod with phase "dllmain" */
    KILN_CHAIN_SOURCE_EMF = 2,     /* EMF's dinput8-chain.ini */
};

/* KilnChainEntry.detail when status is REFUSED: why the proxy did not load it (checked through a read-only file
 * mapping, never by loading the DLL). */
enum {
    KILN_CHAIN_REFUSED_SELF = 1,        /* this proxy itself (<game>\dinput8.dll) */
    KILN_CHAIN_REFUSED_SYSTEM = 2,      /* the system dinput8, or a Wine builtin DLL */
    KILN_CHAIN_REFUSED_KILN = 3,        /* another Kiln proxy, or kiln-core.dll */
    KILN_CHAIN_REFUSED_SHROUDFORGE = 4, /* ShroudForge's dinput8 (ShroudForge runs through its dbghelp.dll) */
    KILN_CHAIN_REFUSED_NOT_DLL = 5,     /* not a 64-bit PE DLL */
    KILN_CHAIN_REFUSED_OUTSIDE = 6,     /* dinput8-chain.ini: absolute path, drive, UNC or '..' (EMF's rule) */
    KILN_CHAIN_REFUSED_TOO_MANY = 7,    /* more than 256 entries in its list */
};

/* One DLL the proxy handled, in load order. Entries never grow: a later version adds a new array instead. */
typedef struct KilnChainEntry {
    KilnStr path;     /* canonical full path as the proxy resolved it (UTF-8, Windows form) */
    uint32_t status;  /* KILN_CHAIN_* */
    int32_t error;    /* Win32 error code when status is FAILED (or the exception code of a load that faulted), else 0 */
    void* module;     /* HMODULE when LOADED (also for DUPLICATE: the first load's module), else NULL */
    uint32_t source;  /* KILN_CHAIN_SOURCE_* */
    uint32_t detail;  /* KILN_CHAIN_REFUSED_* when status is REFUSED, else 0 (was `reserved`; same layout) */
} KilnChainEntry;

typedef struct KilnBootInfo {
    uint32_t size;          /* sizeof(KilnBootInfo) of the proxy that filled it */
    uint32_t reserved;
    void* proxy_module;
    /* ---- v2 ---- */
    uint32_t play_once;              /* KILN_PLAY_ONCE_* */
    uint32_t chain_count;            /* entries in chain */
    const KilnChainEntry* chain;     /* NULL when chain_count is 0 */
    KilnStr play_once_nonce;         /* the play-once.ini "nonce" (echoed into state/boot.json), empty when none */
    KilnStr play_once_profile;       /* KILN_PLAY_ONCE_PROFILE: the profile file to boot from (absolute game-side path;
                                        default <game>\kiln\profile.once.json) */
    /* ---- v3 ---- */
    uint64_t addon_log_size;         /* bytes in <game>\shroudlink_client.log before the proxy loaded any chained DLL (0:
                                        no such file; KILN_BOOT_LOG_UNKNOWN: it could not tell). That addon only appends
                                        to its log, so what follows is this start's: kiln-core's wait for it reads only
                                        that part (core/src/foreign_scan.h). */
} KilnBootInfo;

#define KILN_BOOT_INFO_V1_SIZE 16u
#define KILN_BOOT_INFO_V2_SIZE ((uint32_t)offsetof(KilnBootInfo, addon_log_size))
#define KILN_BOOT_INFO_V3_SIZE ((uint32_t)sizeof(KilnBootInfo))
#define KILN_BOOT_LOG_UNKNOWN UINT64_MAX
/* True when the KilnBootInfo `info` points to is large enough to hold `field`. */
#define KILN_BOOT_INFO_HAS(info, field) \
    ((info)->size >= offsetof(KilnBootInfo, field) + sizeof(((const KilnBootInfo*)0)->field))
typedef KilnResult(KILN_CALL* KilnBootFn)(const KilnBootInfo* info);
#define KILN_BOOT "kiln_boot"

/* The proxy's own identity: `const KilnProxyInfoData* KilnProxyInfo(void)` (export of abi 2+ proxies; older
 * proxies have none and count as abi 1). Without loading the DLL, the same facts are in the file: the NUL-terminated
 * ASCII marker "KilnProxy/<abi> <semver>" (e.g. "KilnProxy/2 0.2.0-dev"); every Kiln proxy also contains the UTF-16LE
 * text "\kiln\kiln-core.dll". */
#define KILN_PROXY_ABI 2u
typedef struct KilnProxyInfoData {
    uint32_t size;           /* sizeof(KilnProxyInfoData) */
    uint32_t abi;            /* KILN_PROXY_ABI: 2 = chain loading + play-once + KilnBootInfo v2 or later
                                (boot_info_size says which: 64 bytes v2, 72 bytes v3) */
    KilnStr version;         /* the Kiln version the proxy was built from */
    KilnStr marker;          /* "KilnProxy/2 <version>" */
    uint32_t boot_info_size; /* sizeof(KilnBootInfo) this proxy passes to kiln_boot */
    uint32_t reserved;
} KilnProxyInfoData;
typedef const KilnProxyInfoData*(KILN_CALL* KilnProxyInfoFn)(void);
#define KILN_PROXY_INFO "KilnProxyInfo"

#ifdef __cplusplus
}
#endif

#endif /* KILN_API_H */
