/* SPDX-License-Identifier: MIT
 * kiln.settings v1: the server's game settings (the "gameSettings" block of enshrouded_server.json), read and changed
 * while the server runs.
 *
 * What it changes: the running world's keen::ecs::GameSettings, the 144-byte block every game system reads (the same
 * memory the game's own admin menu writes). After a change kiln.settings sets the game's own "send the settings to
 * every player" flag, so connected players get the new block with the next server-info message. Nothing is written to
 * disk unless you call save(). Evidence and the per-setting table: docs/re/settings.md; usage: docs/kiln/services/
 * settings.md.
 *
 * Readiness: the settings block is found through the GameServer instance, which kiln.settings sees at the end of the
 * server's first world step. Until then count() is 0, status.ready is 0 and get/set return KILN_ERR_UNAVAILABLE (mods
 * load during that first step: wait one tick).
 *
 * Threads: info/get/set/reset/resend/save on the tick thread (KILN_ERR_WRONG_THREAD otherwise; managed mods are always
 * there). status, count, find, option, last_error: any thread.
 *
 * Strings returned (names, groups, options, paths) stay valid for the life of the process. The table only grows at
 * the end. */
#ifndef KILN_SETTINGS_H
#define KILN_SETTINGS_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_SETTINGS_SERVICE "kiln.settings"
#define KILN_SETTINGS_VERSION 1u

/* Value kinds. KilnSettingValue carries every kind in `integer` and `number` (both filled on output). */
enum {
    KILN_SETTING_BOOL = 1,     /* integer 0 / 1                                                                  */
    KILN_SETTING_FLOAT = 2,    /* number (a float32 factor in the game)                                          */
    KILN_SETTING_INT = 3,      /* integer (whole number, e.g. flameLevelFogRemoval)                              */
    KILN_SETTING_ENUM = 4,     /* integer = the option's value; names through option()                           */
    KILN_SETTING_DURATION = 5, /* integer = nanoseconds (keen::Time), as enshrouded_server.json stores it        */
};

/* KilnSettingInfo.flags */
enum {
    KILN_SETTING_F_LIVE = 0x01,     /* the server uses a new value without a restart                                */
    KILN_SETTING_F_CLIENT = 0x02,   /* players' games read it too: they follow once the resent block arrives       */
    KILN_SETTING_F_NEXT = 0x04,     /* applies to what happens next (a spawn, a death, a loot roll), not to the past */
    KILN_SETTING_F_RANGE = 0x08,    /* min/max are the documented range of enshrouded_server_readme.txt            */
    KILN_SETTING_F_UNKNOWN = 0x10,  /* a field of this game build that kiln.settings does not know: no range, set
                                       needs KILN_SETTINGS_SET_ANY_VALUE                                          */
    KILN_SETTING_F_CHANGED = 0x20,  /* value differs from the world's own value (`initial`)                        */
    KILN_SETTING_F_VERIFIED = 0x40, /* the live effect was observed in a lab (docs/re/settings.md)                 */
};

typedef struct KilnSettingValue {
    uint32_t kind;     /* KILN_SETTING_*; on input 0 = take the setting's kind (integer or number, see set) */
    uint32_t reserved;
    int64_t integer;   /* BOOL, INT, ENUM, DURATION (FLOAT: the value truncated)                               */
    double number;     /* FLOAT (the others: the integer as a double)                                          */
} KilnSettingValue;

typedef struct KilnSettingInfo {
    uint32_t size;             /* set to sizeof(KilnSettingInfo) before calling                                   */
    uint32_t index;
    KilnStr name;              /* the enshrouded_server.json key, e.g. "enemyDamageFactor"                        */
    KilnStr group;             /* "Player", "Survival", "World", "Time", "Rates", "Enemies", "Other"               */
    KilnStr effect;            /* when a change takes effect, in words                                            */
    uint32_t kind;             /* KILN_SETTING_*                                                                   */
    uint32_t flags;            /* KILN_SETTING_F_*                                                                 */
    uint32_t offset;           /* byte offset in keen::ecs::GameSettings (diagnostics)                            */
    uint32_t option_count;     /* ENUM: number of options (option(index, 0..option_count-1))                      */
    double min, max;           /* what set accepts without SET_ANY_VALUE (DURATION: ns; ENUM: lowest/highest value) */
    KilnSettingValue value;    /* now                                                                              */
    KilnSettingValue initial;  /* the world's own value: what the block held when kiln.settings first read it      */
    KilnSettingValue default_value; /* the game's "Default" preset (readme); kind 0 when unknown                   */
} KilnSettingInfo;

typedef struct KilnSettingsStatus {
    uint32_t size;             /* set to sizeof(KilnSettingsStatus) before calling                                */
    uint8_t ready;             /* 1: the live block was found and its layout read from the game's reflection     */
    uint8_t resend;            /* 1: changes are sent to players (the game's send flag is known on this build)   */
    uint8_t daytime;           /* 1: the day/night clock fields below are valid                                   */
    uint8_t verified;          /* 1: the block is the world's registered "ecs::GameSettings" singleton           */
    uint32_t count;            /* settings                                                                        */
    uint64_t revision;         /* +1 for every change seen, by kiln.settings or by the game (an in-game admin)   */
    uint64_t sets;             /* values changed through kiln.settings                                           */
    uint64_t resends_requested;/* times kiln.settings set the send flag                                          */
    uint64_t resends_sent;     /* times the game sent the block and cleared the flag afterwards                  */
    int32_t preset;            /* gameSettingsPreset the server started with: 0 Default, 1 Relaxed, 2 Hard,
                                  3 Survival, 4 Custom; -1 unknown                                                 */
    uint32_t reserved;
    int64_t day_duration_ns;   /* what the game's day/night clock uses now (copied from the settings each step)  */
    int64_t night_duration_ns;
    int64_t time_of_day_ns;    /* 0 .. 24 h                                                                       */
    uint32_t daytime_changes;  /* the clock's re-anchor counter: +1 when it took new durations (or a time skip)  */
    uint32_t reserved2;
    KilnStr config_path;       /* the enshrouded_server.json save() writes                                       */
} KilnSettingsStatus;

/* set / reset flags */
enum {
    KILN_SETTINGS_SET_NO_RESEND = 0x1u,  /* do not send the block to players yet (batch several sets, then resend())  */
    KILN_SETTINGS_SET_ANY_VALUE = 0x2u,  /* skip the documented range; the value must still be sane for its kind
                                            (factor 0..100, duration 1 s..24 h, a valid option)                      */
};

typedef struct KilnSettingsApi {
    KilnServiceHeader header; /* { sizeof(KilnSettingsApi), KILN_SETTINGS_VERSION } */

    KilnResult(KILN_CALL* status)(KilnSettingsStatus* out);
    /* Number of settings (0 until ready). Indices are stable for the life of the process. */
    uint32_t(KILN_CALL* count)(void);
    /* Everything about one setting, with its current value. KILN_ERR_NOT_FOUND for a bad index. */
    KilnResult(KILN_CALL* info)(uint32_t index, KilnSettingInfo* out);
    /* Index of a setting by its enshrouded_server.json name (case-insensitive). KILN_ERR_NOT_FOUND. */
    KilnResult(KILN_CALL* find)(KilnStr name, uint32_t* out_index);
    /* ENUM options: name and value of option `ordinal` (0-based, in the game's order). */
    KilnResult(KILN_CALL* option)(uint32_t index, uint32_t ordinal, KilnStr* out_name, int64_t* out_value);
    KilnResult(KILN_CALL* get)(uint32_t index, KilnSettingValue* out);
    /* Writes the value into the live block and (unless SET_NO_RESEND) asks the game to send the block to every player.
     * value->kind 0 or the setting's own kind; a FLOAT setting takes `number`, every other kind takes `integer` (a
     * FLOAT value with a whole number is accepted for them too). KILN_ERR_INVALID_ARGUMENT when refused: last_error
     * says why ("enemyDamageFactor: allowed 0.25 to 5"). out_before (may be NULL) = the value before. Setting the value
     * it already has is OK and changes nothing. */
    KilnResult(KILN_CALL* set)(KilnOwner owner, uint32_t index, const KilnSettingValue* value, uint32_t flags,
                               KilnSettingValue* out_before);
    /* Back to the world's own value (`initial`); index UINT32_MAX = every setting. */
    KilnResult(KILN_CALL* reset)(KilnOwner owner, uint32_t index, uint32_t flags);
    /* Asks the game to send the settings block to every player with the next server-info message. */
    KilnResult(KILN_CALL* resend)(void);
    /* Writes the live values into enshrouded_server.json (status.config_path) with "gameSettingsPreset": "Custom",
     * so they survive a restart; every other key is kept. The previous file is kept as <name>.kiln-backup. A message
     * ("saved 38 settings to ...") goes to `message` (may be NULL). flags: 0 (reserved). The game reads the file only
     * when it starts. */
    KilnResult(KILN_CALL* save)(uint32_t flags, char* message, size_t capacity, size_t* out_len);
    /* The calling thread's last refusal or error text of set/reset/save ("" if none). */
    KilnResult(KILN_CALL* last_error)(char* buffer, size_t capacity, size_t* out_len);
} KilnSettingsApi;

#ifdef __cplusplus
}
#endif
#endif
