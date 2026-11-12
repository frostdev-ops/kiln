/* SPDX-License-Identifier: MIT
 * kiln.playeranim v1: states of mods in the player's animation graph, merged from every enabled mod at boot.
 * Format of a definition: <mod>/assets/player-states.json (schema/player-states.schema.json). Reference merge:
 * tools/animcook/anicook/playergraph.py; this service's portable port is native/services/playeranim/merge.cpp, and the
 * two give the same bytes for the same inputs (native/services/playeranim/tests/run.sh).
 *
 * The player's graph (21319e83-5087-4fff-8795-22997843649e, keen::anim_graph::runtime_graph::AnimationGraphResource2_0)
 * is one resource for every player, so two mods can't each override it. When the game's resource service comes up
 * (kiln.assets' when_resources_ready, on the game's thread, before the game reads it) this service reads the game's
 * graph, adds every enabled mod's states beside the game states they name (`like`), and serves the result with
 * resource_put. Server and clients do the same merge from the same mods and get the same bytes; graph_hash() says
 * which graph a side serves, so the join handshake can compare them.
 *
 * The graph is served once and never changed: an override of it put while a player's graph is live kills the game.
 * A definition that arrives later (from code, define_json) waits for the next start; a mod's unload changes nothing
 * until then.
 *
 * Costs nothing without states: when no enabled mod defines one, nothing is read from the game at boot and nothing is
 * put; the game's graph is the game's own (state() is KILN_PA_UNTOUCHED). The first question about a game state
 * (game_state, game_state_ids) then decodes the game's graph once.
 *
 * Fails closed. A wrong definition refuses that state only (KILN_PA_REFUSED, with the reason). A merge that cannot be
 * proven on this game build (the game's graph does not re-encode to its own bytes, a field the merge writes is gone,
 * the merged graph fails its own checks) serves nothing: the game's graph stays, state() is KILN_PA_OFF, and every
 * mod state is KILN_PA_UNAVAILABLE with the service's reason.
 *
 * Sides: both (a mod's player states need "side": "both" and "clients": "mod" in its kiln.mod.json, or they are
 * refused). Threads: every function may be called from any thread. Strings stay valid for the life of the process.
 * Capability: anim.player_states (decided when the resource service comes up, before mods load).
 */
#ifndef KILN_PLAYERANIM_H
#define KILN_PLAYERANIM_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_PLAYERANIM_SERVICE "kiln.playeranim"
#define KILN_PLAYERANIM_VERSION 1u

/* state() */
enum {
    KILN_PA_OFF = 0,       /* turned off, or the merge cannot be proven on this game build: the game's own graph is served */
    KILN_PA_WAITING = 1,   /* the game's resource service isn't up yet: nothing is merged yet */
    KILN_PA_UNTOUCHED = 2, /* no state to merge (none defined, or every one refused): the game's own graph */
    KILN_PA_MERGED = 3,    /* the merged graph is served */
};

/* How a sequence enters a state: KilnPaGameState.kind, KilnPaState.kind */
enum {
    KILN_PA_KIND_STATE = 0,   /* SetAnimationEvent.animationName = the state's GUID (full-body machines) */
    KILN_PA_KIND_OVERLAY = 1, /* SetAnimationOverlayEvent.overlayName = the state's GUID (overlay machines) */
};

/* KilnPaState.status */
enum {
    KILN_PA_LIVE = 0,        /* in the served graph */
    KILN_PA_REFUSED = 1,     /* not merged: `reason` says why */
    KILN_PA_PENDING = 2,     /* defined from code after the boot merge: in the graph from the next start */
    KILN_PA_UNAVAILABLE = 3, /* the merge is off on this game build: `reason` is the service's */
};

/* A state of the game's own player graph. The graph holds ids, never GUIDs: a sequence that names a game state needs a
 * GUID whose FNV-1a-32 is the id, and only the game's own sequences have one. */
typedef struct KilnPaGameState {
    uint32_t size;      /* set by the caller */
    uint32_t kind;      /* KILN_PA_KIND_* */
    uint32_t id;        /* the id that enters it (the first of id_count); 0 when id_count is 0 */
    uint32_t id_count;  /* 1 for most, 4 for a jump-attack state, 0 for a state no id enters (Idle_Block) */
    KilnStr machine, name;
} KilnPaGameState;

/* A state a mod defines (its assets/player-states.json, or from code). */
typedef struct KilnPaState {
    uint32_t size;      /* set by the caller */
    uint32_t status;    /* KILN_PA_LIVE ... */
    uint32_t kind;      /* KILN_PA_KIND_* */
    uint32_t id;        /* FNV-1a-32 of guid: what the graph compares */
    uint8_t guid[16];   /* game byte order: UUIDv5 "<mod>:state:<name>" (BlockIds.Derive(mod, "state", name)) */
    KilnStr mod, name, like, machine, reason;  /* machine: the debug name of the machine it is in; reason: empty when live */
} KilnPaState;

typedef struct KilnPlayerAnimApi {
    KilnServiceHeader header; /* { sizeof(KilnPlayerAnimApi), KILN_PLAYERANIM_VERSION } */

    int32_t(KILN_CALL* state)(void); /* KILN_PA_* */

    /* 16 hex digits: the first 8 bytes of SHA-256 over the served graph's bytes. "0000000000000000" when the graph is
     * the game's own (untouched, off, waiting): what a side without the service is taken to have. */
    KilnStr(KILN_CALL* graph_hash)(void);

    /* ---- the lookup (fixed for weapon-moves, inbox note player-anim-04) ---- */

    /* name: "State" or "Machine/State". KILN_OK and *out_matches = how many machines have it (out = the first, in the
     * graph's order); KILN_ERR_NOT_FOUND: none; KILN_ERR_UNAVAILABLE: the game's graph cannot be read on this build. */
    KilnResult(KILN_CALL* game_state)(KilnStr name, KilnPaGameState* out, uint32_t* out_matches);
    /* Every id that enters that state (out_count is set even when capacity is too small). */
    KilnResult(KILN_CALL* game_state_ids)(KilnStr name, uint32_t* out, size_t capacity, size_t* out_count);
    /* KILN_ERR_NOT_FOUND: the mod defines no such state. */
    KilnResult(KILN_CALL* mod_state)(KilnStr mod, KilnStr name, KilnPaState* out);
    /* Every state mods define, by mod id, then name (UTF-8 bytes). 0 while waiting. */
    uint32_t(KILN_CALL* state_count)(void);
    KilnResult(KILN_CALL* state_at)(uint32_t index, KilnPaState* out);

    /* ---- definitions from code, status ---- */

    /* A definition from code: {"format": 1, "mod": "<id>", "dir": "<folder>", "states": {...}}, the states as in the
     * file. The game reads its graph once, so a state the boot merge has unchanged is "live"; any other is kept for the
     * next start ("pending"), never applied now. report: JSON with per state "status" and "reason".
     * KILN_ERR_INVALID_ARGUMENT: the JSON or a state is wrong; KILN_ERR_BUFFER_TOO_SMALL sets *out_len. */
    KilnResult(KILN_CALL* define_json)(KilnStr json, char* report, size_t capacity, size_t* out_len);

    /* A mod's code ran: states it kept from code for names other than these (a JSON array) are dropped from the next
     * start. */
    KilnResult(KILN_CALL* prune_pending)(KilnStr mod, KilnStr defined_names_json);

    /* JSON: state, hash, the states with status and reason, the machines touched, bytes before and after, the merge's
     * time. KILN_ERR_BUFFER_TOO_SMALL sets *out_len to the length needed. */
    KilnResult(KILN_CALL* status_json)(char* json, size_t capacity, size_t* out_len);
} KilnPlayerAnimApi;

#ifdef __cplusplus
}
#endif
#endif
