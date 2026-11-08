/* SPDX-License-Identifier: MIT
 * kiln.pacing v1: where the game's loops wait, and how many worker threads its task system starts. What Enshwick's
 * `pacing` API (MIT) hooks, found in this build by native/services/pacing (sigdb server.d/pacing.json, evidence in
 * docs/re/pacing.md). Managed face: managed/Kiln.Api/Pacing.cs (`Pacing.OnLoopWait`, `OnServerWait`, `CapTaskWorkers`).
 *
 * Three places in the game's code:
 *  - the event loop's wait on its handles (server and client): the loop turns the time left before its deadline into
 *    whole milliseconds (truncating) and waits that long; with less than 1 ms left it waits 0 ms, again and again, until
 *    the deadline passes. A receiver sees {ms, left_ns} just before the wait and must leave the waiting to the game
 *    (it may sleep up to left_ns first, never past it). The client's loop has a second wait that also takes window
 *    messages (MESSAGE_WAIT);
 *  - the server loop's wait between two passes, in nanoseconds: 40 ms with nobody online, 1 ms with players, 0.1 ms after a
 *    pass longer than 14 ms. A receiver may change it; an event still ends the wait early and the loop still wakes for its
 *    own timers;
 *  - the task system's worker caps as it starts (dedicated server only: a client has started its pools long before).
 *
 * Cost: no hook is in the game until something asks for it (`arm`, `cap_task_workers`). A hook stays in once armed (it
 * is never taken out, so it cannot take out another loader's hook next to it) and costs one flag check while nothing
 * listens. Receivers run inside the game's loops, on the thread that waits, concurrently from several threads: keep them
 * short and take no lock. */
#ifndef KILN_PACING_H
#define KILN_PACING_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_PACING_SERVICE "kiln.pacing"
#define KILN_PACING_VERSION 1u

/* Sites: bit values shared with the C# PacingSites flags. */
enum {
    KILN_PACING_LOOP_WAIT = 1,    /* the event loop's wait on its handles (server and client) */
    KILN_PACING_MESSAGE_WAIT = 2, /* the client's wait on handles and window messages */
    KILN_PACING_SERVER_WAIT = 4,  /* the server loop's wait between two passes */
    KILN_PACING_TASK_WORKERS = 8, /* the task system's worker caps (dedicated server) */
};

/* KilnPacingLoopWait.kind */
enum {
    KILN_PACING_KIND_HANDLES = 0,
    KILN_PACING_KIND_MESSAGES = 1,
};

/* The nanoseconds a server-wait receiver may leave: the service clamps what it writes into [0, KILN_PACING_MAX_WAIT_NS]. */
#define KILN_PACING_MAX_WAIT_NS 1000000000ull

/* An event loop wait, about to happen. Valid only during the call. */
typedef struct KilnPacingLoopWait {
    uint32_t size;    /* sizeof(KilnPacingLoopWait) */
    uint32_t kind;    /* KILN_PACING_KIND_* */
    uint32_t ms;      /* what the game will wait: the time left, truncated to whole milliseconds */
    uint32_t reserved;
    int64_t left_ns;  /* the time left before the loop's deadline (negative when it has passed) */
} KilnPacingLoopWait;

/* Runs on the thread that waits, before every wait of the kinds armed. */
typedef void(KILN_CALL* KilnPacingLoopWaitFn)(const KilnPacingLoopWait* wait, void* user);
/* Runs on the server loop's thread once a pass: *wait_ns is what the loop picked; write it to change it. */
typedef void(KILN_CALL* KilnPacingServerWaitFn)(uint64_t* wait_ns, void* user);

typedef struct KilnPacingStatus {
    uint32_t size;
    uint32_t found;               /* KILN_PACING_* places this build has (sigdb resolved them) */
    uint32_t armed;               /* places that deliver now */
    uint32_t hooked;              /* places whose hook is in the game's code (never taken out again) */
    uint32_t refused;             /* places whose hook could not be put in (dropped from found) */
    uint32_t default_cap;         /* the cap each task group starts with (0: left to the game) */
    uint32_t background_cap;
    uint8_t task_system_started;  /* the pools have started: caps come too late */
    uint8_t reserved[3];
    uint64_t loop_calls;          /* waits delivered to the receiver */
    uint64_t server_calls;        /* server waits delivered */
} KilnPacingStatus;

typedef struct KilnPacingApi {
    KilnServiceHeader header;
    /* The places this build has (KILN_PACING_*). Any thread. */
    uint32_t(KILN_CALL* sites)(void);
    /* The one receiver of loop and server waits (the managed half; either may be NULL). Set it before `arm`. */
    KilnResult(KILN_CALL* set_receiver)(KilnPacingLoopWaitFn loop, KilnPacingServerWaitFn server, void* user);
    /* Which places deliver now (KILN_PACING_LOOP_WAIT | MESSAGE_WAIT | SERVER_WAIT). The first request for a place puts
     * its hook in the game's code, on the calling thread; later calls only flip a flag. Returns the places that deliver
     * after the call: a place the build lacks, or whose hook was refused, is not in it. Call with the tick thread's
     * rhythm, not from a receiver. */
    uint32_t(KILN_CALL* arm)(uint32_t sites);
    /* Caps the worker threads each task group starts with (0: leave a group). The lowest cap of every caller wins.
     * KILN_ERR_UNAVAILABLE: not a dedicated server, the build lacks the place, the pools have started, or both caps are 0. */
    KilnResult(KILN_CALL* cap_task_workers)(uint32_t default_workers, uint32_t background_workers);
    KilnResult(KILN_CALL* status)(KilnPacingStatus* out);
} KilnPacingApi;

#ifdef __cplusplus
}
#endif
#endif /* KILN_PACING_H */
