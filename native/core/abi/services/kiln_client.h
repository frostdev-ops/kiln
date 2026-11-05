/* SPDX-License-Identifier: MIT
 * kiln.client v1: what a mod needs to know about the game client it runs in (in-core service, both sides).
 *  - state: side, session (main menu / hosting / joined a remote world), the local player's entity. Any thread.
 *  - the client world's tick (Pre/Post on the game's main thread, ~60 Hz while in game) and a task queue to it.
 * On a dedicated server the service is available too: side KILN_SIDE_SERVER, session KILN_SESSION_DEDICATED, no
 * client world, local_player 0. See docs/kiln/client.md. */
#ifndef KILN_CLIENT_H
#define KILN_CLIENT_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_CLIENT_SERVICE "kiln.client"
#define KILN_CLIENT_VERSION 1u

enum {
    KILN_CLIENT_LOCAL_PLAYER = 1u << 0, /* local_player is known (LocalPlayerData resolved in the client world) */
    KILN_CLIENT_LIFECYCLE = 1u << 1,    /* world stops are seen (feature world.lifecycle) */
};

typedef struct KilnClientState {
    uint32_t size;            /* set by the caller: sizeof(KilnClientState) */
    uint32_t side;            /* KILN_SIDE_* */
    uint32_t session;         /* KILN_SESSION_*: NONE (menu, loading), HOST (single player / co-op host), REMOTE */
    uint32_t generation;      /* changes whenever a world starts or stops */
    KilnEntity local_player;  /* the local player's entity, 0 when none. Ids are shared by the client and server
                                 worlds on a host, so this id also works with the server world's services there. */
    uint32_t flags;           /* KILN_CLIENT_* */
    uint32_t client_session;  /* client worlds since boot (each joined/hosted game is one) */
    uint32_t server_session;  /* server worlds since boot (each hosted game is one) */
    uint64_t client_steps;    /* client-world steps since boot */
    uint64_t server_steps;    /* server-world steps since boot */
} KilnClientState;

typedef struct KilnClientApi {
    KilnServiceHeader header; /* { sizeof(KilnClientApi), KILN_CLIENT_VERSION } */
    KilnResult(KILN_CALL* state)(KilnClientState* out);
    /* Pre/Post of the client world's steps, on the main thread (KILN_TICK_PRE / KILN_TICK_POST). The client world
     * exists while in game (hosting or joined); in the main menu nothing is called. Remove with unsubscribe. */
    KilnResult(KILN_CALL* subscribe_tick)(KilnOwner owner, int32_t phase, KilnTickFn fn, void* user, KilnHandle* out);
    KilnResult(KILN_CALL* unsubscribe)(KilnHandle handle);
    /* Runs `fn` on the main thread before the client world's next Pre (dropped with the owner). */
    KilnResult(KILN_CALL* post)(KilnOwner owner, KilnTaskFn fn, void* user);
    bool(KILN_CALL* on_client_thread)(void);
    /* The client world, 0 when none. Game memory: main thread only, inside a client tick. */
    uintptr_t(KILN_CALL* client_world)(void);
} KilnClientApi;

#ifdef __cplusplus
}
#endif
#endif
