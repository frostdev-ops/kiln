/* SPDX-License-Identifier: MIT */
/*
 * kiln.toolwear v1 (since 0.2.0-dev.28): what one of the game's wear debits costs a tool. Published by kiln-items.dll.
 * docs/kiln/services/items.md, "Tool wear".
 *
 * The game debits a durable tool by 1 in two places a rule reaches: when a HitEvent debits the attacker's weapon, and
 * after a projectile was spawned with the tool as the used item. A rule replaces that 1 by a cost of 0..32767 for one
 * item id. It is per game debit: no debit of the game, nothing spent; two debits in one swing, twice the cost. The
 * game's own skill roll, clamp, break notification, save and repair stay as they are. Armor debits, torches (their
 * durability burns down by another path) and tools without an item entity are never touched.
 *
 * Only the authoritative server world of this process is acted on (a dedicated server, or the server half of a hosted
 * game): a prediction or client world's debit is forwarded untouched.
 *
 * Threads: define, remove and changes on the tick thread; status on any thread.
 * Capability: items.tool_wear_debit (sigdb; lowered by the service when its guards do not pass; held on the client
 * side by the client gate until check 16b-g).
 */
#ifndef KILN_TOOLWEAR_H
#define KILN_TOOLWEAR_H

#include "../kiln_api.h"

#ifdef __cplusplus
extern "C" {
#endif

#define KILN_TOOLWEAR_SERVICE "kiln.toolwear"
#define KILN_TOOLWEAR_VERSION 1

/* define/remove results besides KILN_OK and the common codes (KILN_ERR_INVALID_ARGUMENT: a cost over 32767, item 0,
 * a mod id that is empty, over 127 bytes or not ASCII; KILN_ERR_NOT_FOUND: no such item, or no such rule handle;
 * KILN_ERR_UNAVAILABLE: the capability is off; KILN_ERR_WRONG_THREAD). */
#define KILN_TOOLWEAR_UNSUPPORTED_TOOL 100 /* not a tool with an item entity and durability of its own (a torch, a build tool without one, another category) */
#define KILN_TOOLWEAR_NO_AUTHORITATIVE_WORLD 101 /* no server world in this process now */
#define KILN_TOOLWEAR_TOO_MANY_RULES 102 /* 256 tools have had a rule in this process */
#define KILN_TOOLWEAR_NOT_OWNER 103      /* remove: the handle is another owner's */
#define KILN_TOOLWEAR_PENDING 104        /* the item catalog is not read yet (no world has loaded): try again */

/* KilnToolWearChange.source */
enum {
    KILN_TOOLWEAR_WEAPON_HIT = 1,       /* the attacker's weapon of a HitEvent */
    KILN_TOOLWEAR_PROJECTILE_SPAWN = 2, /* the used item of a spawned projectile */
};

/* One real change of a tool's durability under a rule. Values only. */
typedef struct KilnToolWearChange {
    uint32_t size;   /* sizeof(KilnToolWearChange) as the service wrote it */
    uint32_t source; /* KILN_TOOLWEAR_* */
    uint64_t world_session; /* the server world's number since boot (KilnSessionInfo.sessions[KILN_WORLD_SERVER]) */
    uint64_t step;          /* the sim step the debit was seen in */
    uint32_t actor;          /* the entity whose action debited (the one the game notifies when the tool breaks) */
    uint32_t item_pide;      /* the tool's item entity */
    uint32_t item_id;
    uint32_t instance_stamp; /* kiln.items' stamp of that item entity, captured at the debit: (item_pide, stamp) names it */
    int32_t before, after, maximum;
    int32_t original_delta;  /* what the game asked for: -1 */
    int32_t requested_delta; /* -cost, limited only to avoid signed overflow. after - before may differ (the game clamps at 0) */
    /* PROJECTILE_SPAWN: the action that spawned (the actor's current ActionSequence). 0 for WEAPON_HIT: no join of a
     * hit to an action is established. Never a promise that two records of one action are one use. */
    uint32_t sequence_id;
    uint32_t call_index;
    int64_t trigger_time;
    uint16_t trigger_type;
    uint16_t reserved;
} KilnToolWearChange;

typedef struct KilnToolWearState {
    uint32_t size;      /* in: sizeof(KilnToolWearState) */
    uint32_t ok;        /* 1: the capability works here and the item is a tool a rule can be set for */
    uint32_t pending;   /* 1: the item catalog is not read yet */
    uint32_t has_rule;  /* 1: a rule is in effect for the item */
    uint32_t per_debit; /* its cost (has_rule) */
    uint64_t generation;     /* grows by one with every change of any item's effective rule */
    uint64_t dropped_events; /* change records the full queue did not take, since the process started */
    char owner_mod_id[128];  /* the mod whose rule is in effect; "" without one */
    char reason[192];        /* why ok is 0; "" otherwise */
    /* ---- appended in 0.2.0-dev.28 (lab counters; written only when `size` covers them) ---- */
    uint32_t hook_threads;   /* distinct threads the hooks have run on since the process started (up to 8 are told apart) */
    uint32_t reserved2;
    uint64_t hit_seen, hit_applied;     /* WearItem at the weapon site: calls looked at, debits a rule changed */
    uint64_t spawn_seen, spawn_applied; /* the projectile spawn's debit: calls looked at, debits a rule changed */
} KilnToolWearState;

typedef struct KilnToolWearApi {
    KilnServiceHeader header;
    /* A rule of `owner` for `item`: every qualifying debit costs `per_debit` (0..32767). `defining_mod_id` is the mod
     * the rule is reported under (copied). An owner's second define for the same item replaces its first and returns
     * the same handle. Across owners the latest defined rule is in effect; removing it reveals the one before. */
    KilnResult(KILN_CALL* define)(KilnOwner owner, KilnStr defining_mod_id, uint32_t item, uint32_t per_debit, KilnHandle* out);
    /* Removes `caller`'s rule. An owner that is revoked loses its rules by itself. */
    KilnResult(KILN_CALL* remove)(KilnOwner caller, KilnHandle rule);
    /* Drains up to `capacity` change records of `stride` bytes each into `out` (the smaller of stride and the
     * service's record size is written per record), oldest first; *count = how many. Records of a world that ended,
     * or of an item entity that is gone, are dropped, never delivered late. */
    KilnResult(KILN_CALL* changes)(KilnToolWearChange* out, uint32_t capacity, uint32_t stride, uint32_t* count);
    /* The state for `item` (0: only the capability's and the counters). A copy; any thread. */
    KilnResult(KILN_CALL* status)(uint32_t item, KilnToolWearState* out);
} KilnToolWearApi;

#ifdef __cplusplus
}
#endif

#ifdef __cplusplus
static_assert(sizeof(KilnToolWearChange) == 88, "KilnToolWearChange is 88 bytes");
static_assert(sizeof(KilnToolWearState) == 400, "KilnToolWearState is 400 bytes (360 in its first form)");
static_assert(offsetof(KilnToolWearState, hook_threads) == 360, "the lab counters follow the first form");
#endif

#endif /* KILN_TOOLWEAR_H */
