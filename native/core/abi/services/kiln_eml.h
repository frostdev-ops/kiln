/* SPDX-License-Identifier: MIT
 * kiln.eml v2: the EML runner (kiln-eml.dll, GPL-3.0-or-later, built from gpl/kiln-eml; docs/kiln/eml.md).
 *
 * This header is Kiln's own (MIT): it only describes the table kiln-eml.dll hands out. Code that includes it stays
 * under its own licence; it talks to the GPL plugin through this table and never links GPL code.
 *
 * kiln-eml runs EML mods (mod.json + src/mod.lua, Lua API "game/buffer/io/image/...") once per start, before the game
 * reads its data: it is an early plugin (kiln_plugin_early, native/core/src/early_plugins.cpp) and patches
 * <game>/<exe>.kfc in place, the way EML itself does (backup <exe>.kfc.bak, re-patch only when a mod or game file
 * changed). One run serves every engine whose mods use EML's API (EML, ShroudForge packages, EMF content mods), so the
 * game files have one writer.
 *
 * Getting the table:
 *  - in your own kiln_plugin_early (early phase, before the run; services are not served yet there):
 *        HMODULE m = GetModuleHandleW(L"kiln-eml.dll");
 *        KilnEmlServiceFn get = m ? (KilnEmlServiceFn)GetProcAddress(m, KILN_EML_SERVICE_EXPORT) : NULL;
 *        const KilnEmlApi* eml = get ? get(KILN_EML_VERSION) : NULL;
 *    The core loads every early plugin before it calls any kiln_plugin_early, so this works whatever the file names.
 *  - later (kiln_plugin_main / on_ready, hooks on): api->service({"kiln.eml", 8}, KILN_EML_VERSION).
 * Check header.size >= sizeof(KilnEmlApi), header.version >= KILN_EML_VERSION and the required slots before use.
 * A v1 reader requests get(1), checks version >= 1 and size >= its own v1 prefix (through state_json), and reads
 * only that prefix. v2 appends stand_aside without changing any v1 slot or signature; it still serves get(1).
 * A missing DLL/export, null table or incompatible header means the service is unavailable.
 * Any thread. Strings are UTF-8; paths are absolute Windows paths.
 */
#ifndef KILN_EML_H
#define KILN_EML_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_EML_SERVICE "kiln.eml"
#define KILN_EML_VERSION 2u
#define KILN_EML_SERVICE_EXPORT "kiln_eml_service"

/* phase() */
enum {
    KILN_EML_COLLECTING = 0, /* before the run: add_mod() is accepted */
    KILN_EML_DONE = 1,       /* the run finished (patched, or nothing changed since the last run) */
    KILN_EML_FAILED = 2,     /* the run failed: the game data was put back to vanilla, state_json() says why */
    KILN_EML_ASIDE = 3,      /* EML's own dbghelp.dll/dinput8.dll (or ShroudForge's) is loaded and ran instead */
    KILN_EML_RUNNING = 5,   /* collection is closed; the run is in progress */
    KILN_EML_IDLE = 4,       /* nothing to do: no EML mods and the game files were never patched */
};

typedef struct KilnEmlApi {
    KilnServiceHeader header; /* { sizeof(KilnEmlApi), KILN_EML_VERSION } */
    /* Adds one EML mod folder (with mod.json) or .zip to this start's run, from an engine plugin that owns the mod
     * (e.g. ShroudForge's packages, EMF content mods). `source` names the caller in logs and state ("shroudforge").
     * Only while phase() is COLLECTING, i.e. in kiln_plugin_early. A mod id already added wins over a later one.
     * KILN_ERR_INVALID_ARGUMENT: invalid UTF-8, NUL, relative/traversing path, link/reparse point, or outside
     * <game>/mods, <game>/kiln/{mods,eml/mods} and paths explicitly authorized by the active profile.
     * Roots and their descendants cannot be links/junctions; ancestors above a root are the player's layout and
     * are not inspected. Wine cannot expose Unix file symlinks; the installer must not create them.
     * KILN_ERR_UNAVAILABLE: too late (the run started). */
    KilnResult(KILN_CALL* add_mod)(KilnStr path, KilnStr source);
    uint32_t(KILN_CALL* phase)(void); /* KILN_EML_* */
    /* The run's result as JSON, the same text as <game>/kiln/state/eml.json (format 1: mods[] with id, version,
     * source, path, state loaded|failed|skipped|duplicate, reason; patched; cached; error). Copies up to `capacity`
     * bytes and sets *out_len to the full length; KILN_ERR_BUFFER_TOO_SMALL when it did not fit. */
    KilnResult(KILN_CALL* state_json)(char* buffer, size_t capacity, size_t* out_len);
    /* v2: reserve the startup data writer for a runtime that runs EML itself. Only while collecting.
     * Marks the EML outcome aside; later add_mod calls are unavailable. Nonempty UTF-8 reason, max 1024 bytes. */
    KilnResult(KILN_CALL* stand_aside)(KilnStr reason);
} KilnEmlApi;

typedef const KilnEmlApi*(KILN_CALL* KilnEmlServiceFn)(uint32_t min_version);

#ifdef __cplusplus
}
#endif
#endif
