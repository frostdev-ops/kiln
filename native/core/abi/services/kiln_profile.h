/* SPDX-License-Identifier: MIT
 * kiln.profile v1 (in-core service): the mod profile this process booted with.
 *
 * The core reads it once, early in kiln_boot, from (in this order):
 *   1. play-once mode=profile: the profile file the proxy passed (KilnBootInfo.play_once_profile);
 *   2. play-once mode=no-mods: no profile, no mods of any kind (mode NO_MODS);
 *   3. <game>/kiln/profile.json (written by the Kiln Launcher; schema/profile.schema.json);
 *   4. none: the legacy layout (kiln/mods, kiln/plugins, emf/mods), mode LEGACY.
 * A profile.json that exists but cannot be read or validated gives mode INVALID: no mods load, kiln.log says why.
 * Entries whose folder (plugin: DLL) does not exist are left out of count()/get() (kiln.log warns; state/boot.json
 * profile.missing[] lists them). This service answers in safe idle too (KilnApi.service), unlike the hooked ones.
 * The profile never changes while the process runs. Every string stays valid for the life of the process. Any thread.
 * Paths are absolute, in Windows form (Z:\... under Proton), UTF-8; relative entries in the file are resolved
 * against <game>/kiln/ (copy mode). Format and rules: docs/kiln/launcher/contracts.md.
 */
#ifndef KILN_PROFILE_H
#define KILN_PROFILE_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_PROFILE_SERVICE "kiln.profile"
#define KILN_PROFILE_VERSION 1u

/* mode() */
enum {
    KILN_PROFILE_LEGACY = 0,  /* no profile.json: today's folders (kiln/mods etc.); counts are the drop-in folders' */
    KILN_PROFILE_ACTIVE = 1,  /* kiln/profile.json */
    KILN_PROFILE_ONCE = 2,    /* play-once mode=profile: a profile for this launch only */
    KILN_PROFILE_NO_MODS = 3, /* play-once mode=no-mods: every count is 0 */
    KILN_PROFILE_INVALID = 4, /* profile.json unreadable or invalid: every count is 0, json() holds the error text */
};

/* Mod kinds: count(kind), get(kind, ...), and the bits of dropins(). Same names as the launcher library's folders
 * (library/{kiln,native,plugin,emf}). Other engines' lists (profile.json mods.eml, mods.shroudforge, mods.set,
 * mods.greeneye, mods.flame, mods.shroudtopia; reserved enshwick, shroudlink, ember) are not kinds here: each engine's
 * plugin reads its own list from json(). The core validates them like these and counts them in kiln.log/boot.json. */
enum {
    KILN_PROFILE_KIND_KILN = 0,   /* managed Kiln mods (kiln.mod.json folders), loaded by Kiln.Host */
    KILN_PROFILE_KIND_NATIVE = 1, /* native mods (native.json folders, kiln_native.h) */
    KILN_PROFILE_KIND_PLUGIN = 2, /* Kiln plugin DLLs exporting kiln_plugin_main (kiln_api.h) */
    KILN_PROFILE_KIND_EMF = 3,    /* EMF mods, loaded by kiln-emf.dll */
    KILN_PROFILE_KINDS = 4,
};

/* One entry of a profile list, in the profile's order (the loaders still sort by their resolver rules). */
typedef struct KilnProfileMod {
    uint32_t size;     /* set by the caller: sizeof(KilnProfileMod) */
    uint32_t kind;     /* KILN_PROFILE_KIND_* */
    KilnStr id;        /* the entry's "id" (manifest id); may be empty for a bare plugin DLL */
    KilnStr version;   /* the entry's "version", empty when not given */
    KilnStr path;      /* the folder (kiln, native, emf) or DLL file (plugin), absolute Windows form */
    uint32_t enabled;  /* 0 = listed but off: loaders report it as disabled and skip it */
    uint32_t reserved;
} KilnProfileMod;

typedef struct KilnProfileApi {
    KilnServiceHeader header;              /* { sizeof(KilnProfileApi), KILN_PROFILE_VERSION } */
    uint32_t(KILN_CALL* mode)(void);       /* KILN_PROFILE_* */
    KilnStr(KILN_CALL* id)(void);          /* profile "id" (launcher profile id), empty for LEGACY / NO_MODS / INVALID */
    KilnStr(KILN_CALL* name)(void);        /* profile "name" for logs and the launcher, empty when none */
    uint64_t(KILN_CALL* generation)(void); /* profile "generation" (bumped on every write; echoed in boot.json), 0 */
    uint32_t(KILN_CALL* count)(uint32_t kind);
    /* KILN_ERR_NOT_FOUND past the end or for an unknown kind; KILN_ERR_INVALID_ARGUMENT when out is NULL or its
     * size is smaller than the v1 struct. */
    KilnResult(KILN_CALL* get)(uint32_t kind, uint32_t index, KilnProfileMod* out);
    /* Bit (1u << KILN_PROFILE_KIND_*) set: also load what sits in that kind's drop-in folder (kiln/mods,
     * kiln/native, kiln/plugins, emf/mods) after the profile's entries. LEGACY: all bits; NO_MODS / INVALID: 0. */
    uint32_t(KILN_CALL* dropins)(void);
    KilnStr(KILN_CALL* config_dir)(void);  /* mod configs: profile "configDir", else <game>/kiln/config */
    KilnStr(KILN_CALL* data_dir)(void);    /* per-mod data root: profile "dataDir", else <game>/kiln/data */
    KilnStr(KILN_CALL* json)(void);        /* the profile file's text as read (INVALID: the error), empty when none */
} KilnProfileApi;

#ifdef __cplusplus
}
#endif
#endif
