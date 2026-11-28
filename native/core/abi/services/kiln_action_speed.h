/* SPDX-License-Identifier: MIT */
/*
 * kiln.combat.speed v1 (since Kiln 0.2.0-dev.31.30, mod API 0.2.28): per-player speed of attacks and actions. Published
 * by kiln-combat.dll. Managed face: Kiln.ActionSpeed (Kiln.Combat). docs/kiln/services/combat.md, "Action speed";
 * evidence docs/re/action-speed.md.
 *
 * The table holds effective rates (every mod's values multiplied and clamped by Kiln.ActionSpeed): one per action kind
 * for every player (defaults) and, for players with their own values, one set per entity. A rate of 1 is "no
 * scaling". With every rate at 1 nothing is hooked; the first rate other than 1 installs the links and kiln.world wraps
 * (lazily, once), and from then on a hook whose rates are 1 returns at once.
 *
 * For a classified action of a player (Kiln's routes: the action's trigger and its used item's equipment slot type,
 * docs/re/action-speed.md), Kiln scales together:
 *  - the action sequence clock (gameplay events, combos, recovery): KILN_SPEED_SEQUENCE;
 *  - the animation graph delta of the actor's graph instance (visible and collision poses): KILN_SPEED_GRAPH;
 *  - for a bow, the fresh, skill-adjusted charge and fire durations (divided): KILN_SPEED_BOW. A draw already in
 *    progress keeps the durations it started with (the game latches them at a state change).
 * Players only: entities 1..1023 (the engine's player range). Worlds: the server world of this process (authority:
 * a dedicated server or a host) for every player; a client's prediction storage and client world (presentation) for
 * the local player only (kiln.client's local_player), with the rates this table holds (on a guest: the server's, sent
 * by Kiln.ActionSpeed over kiln.net).
 *
 * Threads: set_rates and set_item_kinds on a world's thread outside its programs (the server tick thread, or the
 * client's main thread); status and item_kinds on any thread.
 * Capabilities (Kiln.Combat): combat.action_speed (SEQUENCE and GRAPH), combat.bow_timing (BOW),
 * combat.melee_sampling (not in this release: never set), combat.action_speed.prediction (PREDICTION).
 */
#ifndef KILN_ACTION_SPEED_H
#define KILN_ACTION_SPEED_H

#include "../kiln_api.h"

#ifdef __cplusplus
extern "C" {
#endif

#define KILN_ACTION_SPEED_SERVICE "kiln.combat.speed"
#define KILN_ACTION_SPEED_VERSION 1u

/* Action kinds (Kiln.ActionKind's numbers; append only). */
enum {
    KILN_ACTION_MELEE = 0, /* melee light and heavy attacks, combos and recovery */
    KILN_ACTION_BOW = 1,   /* bow draw and fire durations and the shot; staffs and wands are not included */
    KILN_ACTION_TOOL = 2,  /* tool swings: axe, pickaxe and the like */
    KILN_ACTION_KINDS = 3,
};

/* Rates Kiln accepts (Kiln.ActionSpeed's Min and Max). */
#define KILN_ACTION_SPEED_MIN 0.5f
#define KILN_ACTION_SPEED_MAX 10.0f

/* KilnActionSpeedStatus.parts: what works on this build and in this process. */
enum {
    KILN_SPEED_SEQUENCE = 1u << 0,     /* the sequence clock site resolved (sigdb combat.action_speed) */
    KILN_SPEED_GRAPH = 1u << 1,        /* the graph binding and evaluation sites resolved (combat.action_speed) */
    KILN_SPEED_BOW = 1u << 2,          /* the bow parameter site resolved (combat.bow_timing) */
    KILN_SPEED_SAMPLING = 1u << 3,     /* melee contact sampling (not in this release: never set) */
    KILN_SPEED_PREDICTION = 1u << 4,   /* a game client: its prediction storage can be wrapped (systems.wraps.prediction) */
    KILN_SPEED_PRESENTATION = 1u << 5, /* a game client: the presentation sites resolved (combat.action_speed.client) */
    KILN_SPEED_WRAPS = 1u << 6,        /* kiln.world's program wraps are there (the runs are told apart by them) */
    KILN_SPEED_ITEMS = 1u << 7,        /* the item classification is there (kiln.items' catalog, or the server's list) */
};

typedef struct KilnActionRate {
    KilnEntity entity;                /* a player entity, 1..1023 */
    float rate[KILN_ACTION_KINDS];    /* effective rates by KILN_ACTION_*: 1 = the game's timing */
} KilnActionRate;

/* One classified item (the routes' item part). kind: KILN_ACTION_MELEE, KILN_ACTION_TOOL, or KILN_ACTION_BOW for a
 * ranged weapon (a bow only while the actor's ranged weapon type is Bow). */
typedef struct KilnActionItemKind {
    uint32_t item;    /* ItemId (FNV-1a-32 of the item's GUID; the same on every side) */
    uint8_t kind;
    uint8_t reserved[3];
} KilnActionItemKind;

typedef struct KilnActionSpeedStatus {
    uint32_t size;                    /* set by the caller: sizeof(KilnActionSpeedStatus) */
    uint32_t parts;                   /* KILN_SPEED_* that can work here */
    uint8_t active;                   /* 1: links and wraps were installed (a rate other than 1 was set once) */
    uint8_t scaling;                  /* 1: a rate other than 1 is set now */
    uint8_t reserved[2];
    uint32_t wraps;                   /* bit k: the wraps of world kind k (KILN_WORLD_*) are active */
    float defaults[KILN_ACTION_KINDS];
    uint32_t players;                 /* entities with rates of their own */
    uint32_t items;                   /* classified items known */
    uint64_t sequence_scaled;         /* sequence steps scaled (every world) */
    uint64_t graph_scaled;            /* graph evaluations scaled */
    uint64_t bow_scaled;              /* bow parameter blocks divided */
    uint64_t unmarked;                /* hook runs outside any wrapped run (left alone) */
    char detail[256];                 /* why a part is off, in words, NUL-terminated; "" when everything works */
} KilnActionSpeedStatus;

typedef struct KilnActionSpeedApi {
    KilnServiceHeader header; /* { sizeof(KilnActionSpeedApi), KILN_ACTION_SPEED_VERSION } */
    /* Replaces the whole table: defaults (KILN_ACTION_KINDS rates for every player) and `count` per-player sets
     * (count <= 64, entities 1..1023, no entity twice). Rates are clamped to [MIN, MAX]; NaN or infinity is
     * KILN_ERR_INVALID_ARGUMENT. The first table with a rate other than 1 installs the links (KILN_ERR_UNAVAILABLE
     * when neither SEQUENCE nor BOW resolved: the table is kept, nothing is scaled). KILN_ERR_WRONG_THREAD: inside a
     * game program. */
    KilnResult(KILN_CALL* set_rates)(const float defaults[KILN_ACTION_KINDS], const KilnActionRate* players, uint32_t count);
    KilnResult(KILN_CALL* status)(KilnActionSpeedStatus* out);
    /* The classified items of this process (kiln.items' catalog; KILN_ERR_UNAVAILABLE until it is read, as on a guest):
     * *count = how many there are; up to `capacity` are copied. Any thread. */
    KilnResult(KILN_CALL* item_kinds)(KilnActionItemKind* out, uint32_t capacity, uint32_t* count);
    /* A guest: the server's classified items (count <= 8192). Replaces what was set before; ignored where kiln.items'
     * catalog is there (a host or a server classifies by its own). */
    KilnResult(KILN_CALL* set_item_kinds)(const KilnActionItemKind* items, uint32_t count);
} KilnActionSpeedApi;

#ifdef __cplusplus
}
#endif

#endif /* KILN_ACTION_SPEED_H */
