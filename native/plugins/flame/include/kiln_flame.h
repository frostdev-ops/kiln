/* SPDX-License-Identifier: MIT
 * kiln.flame v1: published by kiln-flame.dll (a plugin, so the service exists only when that plugin is installed).
 * Which Flame mods (mods/<id>/flame.json, e.g. Creative Mode) run in this process. Kiln's own features that touch the
 * same game systems can consult this service to go idle while a Flame runtime may be active.
 * Look it up in on_ready or later: api->service("kiln.flame", 1). Any thread. Strings live for the process.
 */
#ifndef KILN_FLAME_H
#define KILN_FLAME_H
#include "kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_FLAME_SERVICE "kiln.flame"
#define KILN_FLAME_VERSION 1u

enum {
    KILN_FLAME_PENDING = 0,  /* found, its start thread has not called it yet */
    KILN_FLAME_STARTED = 1,  /* FlameStartRuntime returned */
    KILN_FLAME_FAILED = 2,   /* could not load, no entry point, or it faulted */
    KILN_FLAME_SKIPPED = 3,  /* disabled, rejected, or another Flame loader runs the mods */
};

/* symbols(): what the folder a Flame mod reads its own files from holds for the running game build. */
enum {
    KILN_FLAME_SYMBOLS_UNKNOWN = 0,  /* no such mod, or no folder or build key to look at */
    KILN_FLAME_SYMBOLS_FOUND = 1,    /* sigs\<build key>.json: the mod has its symbols for this build */
    KILN_FLAME_SYMBOLS_RECIPES = 2,  /* no sigs\<build key>.json, but recipes in symbols\ it may resolve the build from */
    KILN_FLAME_SYMBOLS_NONE = 3,     /* neither: by its own rule the mod switches its game features off */
};

typedef struct KilnFlameMod {
    uint32_t size;    /* set by the caller: sizeof(KilnFlameMod) */
    uint32_t status;  /* KILN_FLAME_* */
    KilnStr id;       /* profile id, else mod.json "id" (the folder name when absent) */
    KilnStr version;
    KilnStr folder;   /* absolute Windows path */
} KilnFlameMod;

typedef struct KilnFlameApi {
    KilnServiceHeader header; /* { sizeof(KilnFlameApi), KILN_FLAME_VERSION } */
    uint32_t(KILN_CALL* count)(void);
    KilnResult(KILN_CALL* get)(uint32_t index, KilnFlameMod* out);
    /* true when a mod with this id is pending or may have entered in this process, by kiln-flame or Flame's loader.
     * Failed starts can leave hooks/threads active, so they continue to report true after entering foreign code
     * (then kiln-flame stepped aside and reports the mods it found in <game>/mods as started by that loader). */
    bool(KILN_CALL* running)(KilnStr id);
    /* true when kiln-flame stepped aside for Flame's own loader. */
    bool(KILN_CALL* stepped_aside)(void);
    /* Added in kiln-flame 1.1.0, still version 1: call it only when header.size >= offsetof(KilnFlameApi, symbols) +
     * sizeof(void*) (an older kiln-flame publishes the four functions above, and a caller reads running() from a table
     * of that size too). A Flame mod reads its own files from <game>\mods\<its mod.json id>\, whatever folder its DLL
     * was loaded from (Creative Mode 3.2.0's log: "Mod directory: ...\mods\creative"); its runtime looks there for
     * sigs\<build key>.json, the build key being the game exe's TimeDateStamp and SizeOfImage ("%08X-%X"), else for
     * recipes in symbols\. Which of the two that folder holds for the running game, checked before the mod starts:
     * KILN_FLAME_SYMBOLS_*. `file` (may be NULL) receives the sigs file's absolute path (empty when unknown). */
    uint32_t(KILN_CALL* symbols)(KilnStr id, KilnStr* file);
} KilnFlameApi;

#ifdef __cplusplus
}
#endif
#endif
