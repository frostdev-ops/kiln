/* SPDX-License-Identifier: MIT
 * kiln.flame v1: published by kiln-flame.dll (a plugin, so the service exists only when that plugin is installed).
 * Which Flame mods (mods/<id>/flame.json, e.g. Creative Mode) run in this process. Kiln's own features that touch the
 * same game systems as a running Flame mod use it to go idle (Kiln's creative hooks while Creative Mode runs).
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

typedef struct KilnFlameMod {
    uint32_t size;    /* set by the caller: sizeof(KilnFlameMod) */
    uint32_t status;  /* KILN_FLAME_* */
    KilnStr id;       /* mod.json "id" (the folder name when absent) */
    KilnStr version;
    KilnStr folder;   /* absolute Windows path */
} KilnFlameMod;

typedef struct KilnFlameApi {
    KilnServiceHeader header; /* { sizeof(KilnFlameApi), KILN_FLAME_VERSION } */
    uint32_t(KILN_CALL* count)(void);
    KilnResult(KILN_CALL* get)(uint32_t index, KilnFlameMod* out);
    /* true when a mod with this id is pending or started in this process, by kiln-flame or by Flame's own loader
     * (then kiln-flame stepped aside and reports the mods it found in <game>/mods as started by that loader). */
    bool(KILN_CALL* running)(KilnStr id);
    /* true when kiln-flame stepped aside for Flame's own loader. */
    bool(KILN_CALL* stepped_aside)(void);
} KilnFlameApi;

#ifdef __cplusplus
}
#endif
#endif
