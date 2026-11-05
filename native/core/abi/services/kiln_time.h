/* SPDX-License-Identifier: MIT
 * kiln.time v1: time of day on the server world (g38_daytime::Daytime) and a weather re-roll (WeatherSystem).
 *
 * The server owns the day/night clock: a change made here is replicated to every client by the game itself (the
 * GameServer's ServerInfo message carries the Daytime anchor whenever its change counter moves) and is saved with the
 * world (the savegame stores the time of day; loading restores it through the game's setTimeOfDay).
 * See docs/re/time.md for the evidence and docs/kiln/services/time.md for usage. */
#ifndef KILN_TIME_H
#define KILN_TIME_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_TIME_SERVICE "kiln.time"
#define KILN_TIME_VERSION 1u

/* set_time_of_day modes */
enum {
    /* Default. Jump forward to the next time the clock shows `hours` (today if still ahead, else tomorrow). The game
     * clock stays monotonic, so weather and anything else timed on it keeps running. */
    KILN_TIME_SET_FORWARD = 0,
    /* Call the game's own setTimeOfDay (what the in-game admin menu and savegame load do): the game clock restarts at
     * day 0. Weather timed on the old clock may then wait for a long while; follow with reset_weather. */
    KILN_TIME_SET_GAME = 1,
};

typedef struct KilnTimeState {
    uint32_t size;               /* sizeof(KilnTimeState) of the service that filled it */
    uint8_t is_day;              /* day phase (day_start <= time < night_start), as of the last sim step */
    uint8_t weather_available;   /* reset_weather can work (the server WeatherSystem was seen) */
    uint8_t reserved[2];
    double hours;                /* time of day 0..24 at the current game clock (exact; reflects a set at once) */
    double hours_field;          /* the Daytime time-of-day field, written by the game once per sim step */
    double day_start_hours;      /* start of the day phase (6.0 by default) */
    double night_start_hours;    /* start of the night phase (18.0 by default) */
    double day_length_s;         /* real seconds the day phase lasts (GameSettings.dayTimeDuration) */
    double night_length_s;       /* real seconds the night phase lasts (GameSettings.nightTimeDuration) */
    float time_scale;            /* 1.0; raised by the game while players sleep at night */
    uint32_t change_counter;     /* +1 on every re-anchor; the server re-sends the time to clients when it moves */
    int64_t ecs_time_ns;         /* the world's ECS time now (clock period x clock) */
    int64_t game_time_ns;        /* the day/night cycle clock now (game time; one cycle = day + night length) */
    int64_t anchor_ecs_ns;       /* Daytime anchor: ECS time ... */
    int64_t anchor_game_ns;      /* ... and game time at the last re-anchor */
    uint32_t synced_counter;     /* change counter the server last put into a ServerInfo message; UINT32_MAX = unknown */
    uint32_t weather_layers;     /* weather layer count (0 = unknown) */
} KilnTimeState;

typedef struct KilnTimeApi {
    KilnServiceHeader header; /* { sizeof(KilnTimeApi), KILN_TIME_VERSION } */
    /* Current state. Any thread (reads only). KILN_ERR_UNAVAILABLE until the world has been created. */
    KilnResult(KILN_CALL* get)(KilnTimeState* out);
    /* Sets the time of day (hours, wrapped into 0..24). Tick thread only (Pre or Post). mode: KILN_TIME_SET_*. */
    KilnResult(KILN_CALL* set_time_of_day)(double hours, uint32_t mode);
    /* Ends every weather phenomenon and makes each weather layer pick a new one on the server's next weather update
     * (same frame). Clients are told through the game's own weather sync. Tick thread only. */
    KilnResult(KILN_CALL* reset_weather)(void);
} KilnTimeApi;

#ifdef __cplusplus
}
#endif
#endif
