/* SPDX-License-Identifier: MIT
 * kiln.time v2: time of day on the server world (g38_daytime::Daytime), a weather re-roll (WeatherSystem), and (v2, since
 * 0.2.0-dev.27) the weather table, each layer's current phenomenon and choosing one.
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
#define KILN_TIME_VERSION 2u  /* v2 (0.2.0-dev.27): weather_table, weather_layers, weather_set; check header.size */

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

/* ---------------------------------------------------------------------------------------------- weather ------ */

/* since 0.2.0-dev.27 (feature time.weather_choose; KILN_ERR_UNAVAILABLE without it, and until the server's weather has
 * run once after the world started).
 *
 * The weather is the game's WeatherSystem: a few layers (9 on 26430253), each running one phenomenon at a time from its
 * range of the game's WeatherSystemResource (clouds over the whole world in layer 0, rain or snowy rain over one region
 * in each of the others, "nothing" in between). When a layer's time is up the game rolls the next phenomenon by the
 * table's weights and its duration between the phenomenon's min and max (game time: the day/night clock, so a faster
 * clock speeds the weather too). Choosing one calls the game's own start of a phenomenon for that layer, the way the
 * game's own weather state configurations do (SetPhenomenon): the layer's instances end, the chosen one starts with the
 * game's random position, size and drift inside the layer's region, and the game replicates it to every client.
 *
 * Persistence: none. After a restart the layers run what the game rolls anew (lab: a two-hour phenomenon left before an
 * autosave was gone after the load; the load also restarts the game clock in its first cycle). A hold
 * (KILN_TIME_WEATHER_HOLD) is Kiln's and ends with the process. */

/* KilnTimeWeatherRequest.flags */
enum {
    KILN_TIME_WEATHER_HOLD = 0x01u,    /* set: every time the game rolls this layer again, Kiln starts the same choice
                                          (each run lasts the phenomenon's own duration) until a release */
    KILN_TIME_WEATHER_RELEASE = 0x02u, /* hand the layer back: the hold ends; `phenomenon` and `duration_ns` unused */
    KILN_TIME_WEATHER_NOW = 0x04u,     /* with RELEASE: the layer's phenomenon ends and the game rolls a new one in the
                                          same update; without it the running one finishes its time first */
};

#define KILN_TIME_WEATHER_NOTHING (-1) /* KilnTimeWeatherRequest.phenomenon: the layer's own "nothing" */

typedef struct KilnTimeWeatherPhenomenon {
    uint32_t size;            /* sizeof(KilnTimeWeatherPhenomenon) */
    uint16_t index;           /* index in the game's phenomenon table (WeatherSystemResource.phenomenons) */
    uint8_t layer;            /* the layer whose range holds it; 0xFF: no layer rolls it (the game's debug entries) */
    uint8_t effect;           /* keen::WeatherPhenomenonEffect: 0 none, 1 rain, 2 blizzard */
    uint8_t guid[16];
    float probability;        /* its weight in its layer's roll */
    uint32_t flags;           /* keen::WeatherSystemPhenomenonResourceFlags bits */
    int64_t duration_min_ns;  /* game time */
    int64_t duration_max_ns;
    char name[64];            /* the game's debugName ("clouds_few", "rain", ...), UTF-8, NUL-terminated */
} KilnTimeWeatherPhenomenon;

typedef struct KilnTimeWeatherLayer {
    uint32_t size;            /* sizeof(KilnTimeWeatherLayer) */
    uint8_t index;
    uint8_t first;            /* the layer's range in the table: first .. first + count - 1 */
    uint8_t count;
    uint8_t held;             /* 1: Kiln holds a choice on this layer */
    uint8_t restrict_mask;    /* the region mask the layer is limited to (0xFF none) */
    uint8_t exclude_mask;     /* the region mask it avoids (0xFF none) */
    uint8_t reserved[2];
    uint8_t guid[16];
    float nothing_probability;/* the weight of "nothing" in its roll */
    int32_t current;          /* the phenomenon running: a table index, KILN_TIME_WEATHER_NOTHING, or -2 (none yet) */
    int32_t held_phenomenon;  /* the held choice (table index or NOTHING); -2 when not held */
    uint32_t instance;        /* the running instance's handle (0: none, e.g. "nothing") */
    uint32_t kiln_changes;    /* +1 each time Kiln started or ended something on this layer */
    uint32_t reserved2;
    int64_t next_ns;          /* game time of the layer's next roll */
    int64_t now_ns;           /* the weather clock at the last weather update */
} KilnTimeWeatherLayer;

typedef struct KilnTimeWeatherRequest {
    uint32_t size;            /* sizeof(KilnTimeWeatherRequest) */
    uint32_t flags;           /* KILN_TIME_WEATHER_* */
    uint32_t layer;
    int32_t phenomenon;       /* a table index inside the layer's range, or KILN_TIME_WEATHER_NOTHING */
    int64_t duration_ns;      /* 0: the phenomenon's own duration (rolled by the game); > 0: the layer is rolled again
                                 this long after the start (game time; its fade-out follows): Kiln passes it plus the
                                 phenomenon's despawn fade as the override the game's own state configurations use */
} KilnTimeWeatherRequest;

/* KilnTimeWeatherResult.status */
enum {
    KILN_TIME_WEATHER_DONE = 0,        /* the game started it (or released it); `current` says what runs now */
    KILN_TIME_WEATHER_DROPPED = 1,     /* not run: the server world closed, or its weather did not update for 10 s */
    KILN_TIME_WEATHER_FAILED = 2,      /* the game's call faulted (choice is then off until a restart) */
};

typedef struct KilnTimeWeatherResult {
    uint32_t size;
    int32_t status;           /* KILN_TIME_WEATHER_* status */
    uint64_t ticket;
    uint32_t layer;
    uint32_t flags;           /* the request's */
    int32_t current;          /* as KilnTimeWeatherLayer.current, right after */
    uint32_t instance;
    int64_t next_ns;          /* the layer's next roll (game time) */
    int64_t now_ns;           /* the weather clock when it ran */
    char message[128];        /* why, for anything but DONE */
} KilnTimeWeatherResult;

/* Tick thread, Pre of the step after the weather update that ran it. */
typedef void(KILN_CALL* KilnTimeWeatherDoneFn)(const KilnTimeWeatherResult* result, void* user);

typedef struct KilnTimeApi {
    KilnServiceHeader header; /* { sizeof(KilnTimeApi), KILN_TIME_VERSION } */
    /* Current state. Any thread (reads only). KILN_ERR_UNAVAILABLE until the world has been created. */
    KilnResult(KILN_CALL* get)(KilnTimeState* out);
    /* Sets the time of day (hours, wrapped into 0..24). Tick thread only (Pre or Post). mode: KILN_TIME_SET_*. */
    KilnResult(KILN_CALL* set_time_of_day)(double hours, uint32_t mode);
    /* Ends every weather phenomenon and makes each weather layer pick a new one on the server's next weather update
     * (same frame). Clients are told through the game's own weather sync. Tick thread only. */
    KilnResult(KILN_CALL* reset_weather)(void);
    /* Later additions: check header.size before calling. */
    /* Holds the day/night clock's speed: scale 0 stops the clock, 1 is the game's normal speed, more runs it faster
     * (at most 1000). While held, the game's own speed changes (sleeping) are refused. A negative scale gives the
     * speed back to the game. Session state: it ends when the world closes. Clients follow through the game's own
     * daytime sync. Tick thread only. KILN_ERR_UNAVAILABLE when this build has no feature time.scale. */
    KilnResult(KILN_CALL* set_time_scale)(float scale);
    /* The speed Kiln holds; -1 when the game decides; -2 when this build cannot hold it. Any thread. */
    float(KILN_CALL* time_scale_hold)(void);

    /* ---- v2, since 0.2.0-dev.27: choosing the weather (see "weather"; check header.size) ---- */

    /* The game's phenomenon table, `capacity` records of `item_size` bytes (at least the v2 size; the stride).
     * *out_count = the table's size (more than capacity: the first `capacity` are written and KILN_ERR_BUFFER_TOO_SMALL
     * is returned). Tick thread. */
    KilnResult(KILN_CALL* weather_table)(KilnTimeWeatherPhenomenon* out, uint32_t capacity, uint32_t item_size,
                                         uint32_t* out_count);
    /* Every layer's state, as weather_table writes records. Tick thread. */
    KilnResult(KILN_CALL* weather_layers)(KilnTimeWeatherLayer* out, uint32_t capacity, uint32_t item_size,
                                          uint32_t* out_count);
    /* Queues a choice or a release for one layer; it runs in the server's next weather update (the same frame, after the
     * sim step) and `done` (optional) runs in the next Pre. KILN_ERR_INVALID_ARGUMENT: a layer or phenomenon outside
     * the table (a phenomenon must lie in the layer's range), a negative duration, unknown flags; KILN_ERR_UNAVAILABLE:
     * no feature time.weather_choose, or the weather has not run yet; KILN_ERR_WRONG_THREAD off the tick thread. */
    KilnResult(KILN_CALL* weather_set)(KilnOwner owner, const KilnTimeWeatherRequest* request, KilnTimeWeatherDoneFn done,
                                       void* user, uint64_t* out_ticket);
} KilnTimeApi;

#ifdef __cplusplus
}
#endif
#endif
