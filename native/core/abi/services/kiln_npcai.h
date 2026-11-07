/* SPDX-License-Identifier: MIT
 * kiln.npcai v1: who fights whom. The game decides it in four small functions over keen::FactionType (one byte in each
 * entity's Faction component): Hostile(a, b) "a would fight b" (enemy targeting's candidate filter and the sensors call
 * it) and Friend(a, b) "b is on a's side", each by pointer and by value. kiln.npcai hooks them and answers from:
 *   1. pairs:     one entity toward one other entity (a creature's SenseFilter),
 *   2. rows:      one entity toward a faction (a creature's own relations),
 *   3. the table: a faction toward a faction (the world's relations),
 * and otherwise lets the game answer. Rows and pairs work on the pointer forms only (the game passes the Faction
 * components of the two entities; Kiln maps them back in Pre, when no job runs); the by-value forms see the table.
 * Relations are directional: a's answer about b. Hooks go in when the first relation is set and come out when the last
 * is gone. docs/kiln/modding/npc-ai.md, docs/re/npc-internals.md 3.6. */
#ifndef KILN_NPCAI_H
#define KILN_NPCAI_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_NPCAI_SERVICE "kiln.npcai"
#define KILN_NPCAI_VERSION 1u

/* A relation: what a's check about b answers. */
#define KILN_REL_GAME 0u     /* the game's own answer (clears a setting) */
#define KILN_REL_HOSTILE 1u  /* a fights b; b is not on a's side */
#define KILN_REL_FRIENDLY 2u /* b is on a's side; a does not fight b */
#define KILN_REL_NEUTRAL 3u  /* a ignores b: no fight, not on its side */

typedef struct KilnNpcAiStatus {
    uint32_t size;            /* in: sizeof(KilnNpcAiStatus) */
    uint32_t resolved;        /* bit per hook found on this build: 1 Hostile, 2 HostileValue, 4 Friend, 8 FriendOpt, 16 FriendValue */
    uint32_t installed;       /* the same bits, hooks in now */
    uint32_t relations;       /* table cells set (any owner) */
    uint32_t rows;            /* entities with relations of their own */
    uint32_t pairs;           /* entity pairs set */
    uint32_t mapped;          /* Faction components mapped back to entities in the last Pre */
    uint32_t reserved;
    uint64_t calls;           /* hooked calls, all four */
    uint64_t answered;        /* of those, answered by Kiln (not the game) */
    uint64_t by_pair, by_row, by_table;
} KilnNpcAiStatus;

typedef struct KilnNpcAiApi {
    KilnServiceHeader header; /* { sizeof(KilnNpcAiApi), KILN_NPCAI_VERSION } */
    /* An owner for a mod's settings: the same name gives the same owner. Any thread. */
    uint32_t(KILN_CALL* owner_open)(KilnStr name);
    /* Drops everything the owner set. Tick thread. */
    KilnResult(KILN_CALL* owner_release)(uint32_t owner);
    /* Faction a toward faction b (0..255). KILN_REL_GAME clears this owner's cell. Several owners: the last set wins. Tick
     * thread. KILN_ERR_UNAVAILABLE: the hooks are not on this build. */
    KilnResult(KILN_CALL* relation_set)(uint32_t owner, uint8_t a, uint8_t b, uint32_t relation);
    /* The table's relation of a toward b (KILN_REL_GAME when none). Any thread. */
    uint32_t(KILN_CALL* relation_get)(uint8_t a, uint8_t b);
    /* Entity e toward every faction: 256 relations (KILN_REL_*), or NULL to clear. One owner per entity: the last wins.
     * Tick thread. */
    KilnResult(KILN_CALL* entity_row)(uint32_t owner, KilnEntity e, const uint8_t* row);
    /* Entity e toward entity other, for `seconds` (0: until cleared). KILN_REL_GAME clears. Tick thread. */
    KilnResult(KILN_CALL* pair_set)(uint32_t owner, KilnEntity e, KilnEntity other, uint32_t relation, float seconds);
    /* Clears every pair of e this owner set. Tick thread. */
    KilnResult(KILN_CALL* pair_clear)(uint32_t owner, KilnEntity e);
    /* What the game's checks answer for entity a about entity b, as the hooks answer them (Kiln's relation, else the game's
     * own Hostile(a, b) and FriendOpt(a, b)). A row or pair set now counts from the next Pre, the table at once. Tick thread. */
    KilnResult(KILN_CALL* query)(KilnEntity a, KilnEntity b, uint32_t* hostile, uint32_t* friendly);
    /* The same by faction value, the table, else HostileValue(a, b) and FriendValue(a, b). Any thread. */
    KilnResult(KILN_CALL* query_factions)(uint8_t a, uint8_t b, uint32_t* hostile, uint32_t* friendly);
    KilnResult(KILN_CALL* status)(KilnNpcAiStatus* out);
} KilnNpcAiApi;

#ifdef __cplusplus
}
#endif
#endif
