/* SPDX-License-Identifier: MIT
 * kiln.knowledge v1: the game's knowledge (every unlock, quest step, dialogue line heard, NPC rescued), per world and
 * per player, read, granted, revoked and watched from mods; the journal's vanilla quests; the dialogue-select event;
 * an opt-in veto of what clients ask for. Published by kiln-quests.dll (native/services/quests). Evidence:
 * docs/re/quests.md, docs/re/creatures.md section 7; usage: docs/kiln/services/knowledge.md. (Kept next to the service
 * while it is new; it belongs with the other service headers in native/core/abi/services/.)
 *
 * What the game keeps (server world, singleton G38Knowledge -> its registry):
 *  - a WORLD map (shared progress: NPC rescues, world quests, dialogue lines heard by anyone) and one map per player
 *    slot (personal knowledge: quest rewards, skill-point knowledge, ...), each id -> state. A state's low 32 bits are
 *    the value: Bool 0/1, Counter n, Flags a bit mask. Only ids listed in GameKnowledgeResource exist; an id can be a
 *    world object, a player object or both (a "twin": hasOptionalPlayerProgression).
 *  - The world map and the player maps of the players on the server are saved in the world save and replicated to
 *    every client (their journal, NPC directory, dialogue filters and map markers follow it).
 *  - Programs grant knowledge by pushing {id, type, value, player context} into KnowledgeTriggerQueue; the program
 *    update_knowledge_system_server applies it once per step (the world object if the id has one, then the player
 *    object for the player of the context), pays the object's XP, runs the query triggers and replicates. The queue
 *    can only raise values (Bool -> 1, Counter + n, Flags | bit).
 *
 * How Kiln changes it:
 *  - GRANT: the game's own apply function on {id, type, value, context} (exactly what the queue does), run by Kiln
 *    inside update_knowledge_system_server right after the game's own queue, in request order. A player grant of a
 *    twin id therefore also sets the world object (the game's rule). XP, query triggers, replication: the game's.
 *  - REVOKE / SET: a direct write of the map entry, the change-list entry and the version, the three things the
 *    game's apply writes (there is no game path that lowers a value), at the same point. The change list replicates:
 *    a client erases an id it receives with value 0. A revoked id is not written into the next save (the game saves
 *    non-zero values only). Re-granting a revoked player object pays its XP again. SET writes any value (no XP).
 *  - Without the in-step point (feature quests.changes off) GRANT falls back to a queue push from Kiln's program
 *    kiln_knowledge, REVOKE / SET answer KILN_ERR_UNAVAILABLE.
 *
 * Targets: 0 = the world map; a player entity (1..16 on a dedicated server: entity = slot + 1) = that player's map,
 * available while the player is on the server (their map is loaded): KILN_ERR_UNAVAILABLE otherwise.
 *
 * Threads: status, info, submit, result, events, last_seq, held_ops, decide, listen, settings, configure: any thread.
 * get, list, query, resolve, name_of, catalog_*, journal_*, npc: the tick thread (KILN_ERR_WRONG_THREAD elsewhere);
 * they read game memory the knowledge program writes during the step. Results and events are produced in Post (tick
 * thread) of the step in which the game applied the change.
 * Fail soft: on an unknown build the missing parts answer KILN_ERR_UNAVAILABLE (status says which). */
#ifndef KILN_KNOWLEDGE_H
#define KILN_KNOWLEDGE_H
#include "kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_KNOWLEDGE_SERVICE "kiln.knowledge"
#define KILN_KNOWLEDGE_VERSION 1u

/* The game's KnowledgeType. */
enum {
    KILN_KNOW_BOOL = 0,
    KILN_KNOW_COUNTER = 1,
    KILN_KNOW_FLAGS = 2,
    KILN_KNOW_NONE = 0xff, /* no such object */
};

/* KilnKnowledgeInfo.flags: what GameKnowledgeResource says about an id. 0 = the game does not know it. */
enum {
    KILN_KNOW_WORLD = 0x01,       /* a world object (shared progress) */
    KILN_KNOW_PLAYER = 0x02,      /* a player object (per character) */
    KILN_KNOW_TWIN = 0x04,        /* world object with a player twin for personal progression (same id) */
    KILN_KNOW_ANALYTICS = 0x08,   /* the game reports it to its analytics when set */
    KILN_KNOW_EXPERIENCE = 0x10,  /* player object: the game pays XP when it is set (experience_id) */
    KILN_KNOW_SKILL_POINTS = 0x20 /* player object: gives skill points (skill_points) */
};

typedef struct KilnKnowledgeInfo {
    uint32_t size;          /* sizeof(KilnKnowledgeInfo) */
    uint32_t id;
    uint32_t flags;         /* KILN_KNOW_WORLD | ... */
    uint8_t world_type;     /* KILN_KNOW_BOOL/COUNTER/FLAGS of the world object, KILN_KNOW_NONE */
    uint8_t player_type;    /* the same for the player object */
    uint8_t skill_points;   /* player object: unlockedSkillPoints */
    uint8_t reserved;
    uint32_t world_flags;   /* Flags world object: the bits that exist (expectedFlags) */
    uint32_t player_flags;  /* Flags player object: expectedFlags */
    uint32_t experience_id; /* player object: experienceGainId (a BalancingRegistry experience value), 0 none */
} KilnKnowledgeInfo;        /* 28 bytes */

/* One id in one target's map. */
typedef struct KilnKnowledgeState {
    uint32_t size;      /* sizeof(KilnKnowledgeState) */
    KilnEntity target;  /* 0 world, else the player */
    uint32_t id;
    uint32_t value;     /* Bool 0/1, Counter n, Flags mask; 0 = not set */
    uint8_t type;       /* the object's type in this target (KILN_KNOW_*) */
    uint8_t new_mask;   /* the game's "new" markers (state bits 32..39); 0xff for an id without an entry */
    uint8_t present;    /* 1: the map has an entry for the id (it may hold 0 after a revoke) */
    uint8_t reserved;
    uint32_t reserved2;
    uint64_t step;      /* server-world step of the read */
} KilnKnowledgeState;   /* 32 bytes */

typedef struct KilnKnowledgeEntry {
    uint32_t id;
    uint32_t value;
} KilnKnowledgeEntry;

/* ---------------------------------------------------------------------------------------------- requests ------ */

/* KilnKnowledgeRequest.op */
enum {
    KILN_KNOW_OP_GRANT = 1,  /* value: Counter amount to add / Flags flag index (0..31) to set / Bool: ignored */
    KILN_KNOW_OP_REVOKE = 2, /* value: Flags flag index to clear, or KILN_KNOW_ALL (every flag; Bool / Counter: 0) */
    KILN_KNOW_OP_SET = 3,    /* value: the exact new value (a direct write: no XP, no "new" marker) */
};
#define KILN_KNOW_ALL 0xffffffffu

typedef struct KilnKnowledgeResult {
    uint32_t size;       /* sizeof(KilnKnowledgeResult) */
    uint32_t op;
    uint64_t request;    /* the id submit() returned */
    KilnEntity target;
    uint32_t id;
    KilnResult status;   /* KILN_OK, or why not (reason says it in words) */
    uint32_t steps;      /* steps from submit to the apply */
    uint32_t before;     /* the target's value before and after the apply */
    uint32_t after;
    uint8_t changed;     /* 1: the value changed */
    uint8_t reserved[7];
    uint64_t step;       /* server-world step of the apply */
    char reason[128];    /* "" on success, else why, NUL-terminated */
} KilnKnowledgeResult;   /* 184 bytes */

typedef void(KILN_CALL* KilnKnowledgeDoneFn)(const KilnKnowledgeResult* result, void* user);

enum { KILN_KNOW_REQ_UNCHECKED = 1 }; /* flags: a GRANT goes to the game's apply even when the id is in no static map (the
                                         game drops it; tests, see docs/kiln/services/npcs.md "The negative test") */

typedef struct KilnKnowledgeRequest {
    uint32_t size;      /* sizeof(KilnKnowledgeRequest) */
    uint32_t op;        /* KILN_KNOW_OP_* */
    KilnEntity target;  /* 0 world, else a player in game */
    uint32_t id;
    uint32_t value;     /* see KILN_KNOW_OP_* */
    uint32_t flags;     /* KILN_KNOW_REQ_* or 0 */
    KilnKnowledgeDoneFn callback; /* may be NULL; runs once on the tick thread (Post of the apply step) */
    void* user;
} KilnKnowledgeRequest; /* 40 bytes */

/* ---------------------------------------------------------------------------------------------- queries ------- */

/* The game's KnowledgeQueryData (12 bytes, as in its data): a knowledge object compared to a value, or a named query
 * of GameKnowledgeQueryResourceDb (type EXTERN). */
enum { KILN_KNOW_Q_EXTERN = 0, KILN_KNOW_Q_BOOL = 1, KILN_KNOW_Q_COUNT = 2, KILN_KNOW_Q_FLAG = 3 };
enum { KILN_KNOW_CMP_GREATER = 0, KILN_KNOW_CMP_LESS = 1, KILN_KNOW_CMP_EQUALS = 2 };
typedef struct KilnKnowledgeQuery {
    uint32_t id;            /* knowledge id, or query id (EXTERN) */
    uint32_t compare_value;
    uint8_t compare;        /* KILN_KNOW_CMP_* */
    uint8_t type;           /* KILN_KNOW_Q_* */
    uint8_t player_only;    /* isExplicitPlayerKnowledgeQuery: the player's object even in shared progression */
    uint8_t reserved;
} KilnKnowledgeQuery;

/* query() mode */
enum {
    KILN_KNOW_MODE_SHARED = 0,   /* twins read the world object (shared progression) */
    KILN_KNOW_MODE_PLAYER = 1,   /* follow the player's own setting (personal progression on or off) */
    KILN_KNOW_MODE_PERSONAL = 2, /* twins read the player object (what the game's quest rewards use) */
};

/* ---------------------------------------------------------------------------------------------- names --------- */

/* KilnKnowledgeName.kind / catalog kinds */
enum {
    KILN_KNOW_NAME_KNOWLEDGE = 1, /* id is a knowledge id */
    KILN_KNOW_NAME_QUERY = 2,     /* id is a query id of GameKnowledgeQueryResourceDb (evaluate with query()) */
};
enum {
    KILN_KNOW_CAT_NPCS = 1,    /* NpcCollection: "npc:<debugName>" -> its unlock knowledge */
    KILN_KNOW_CAT_QUESTS = 2,  /* the journal's quests: "quest:0x<entryId>" -> the quest's "rewarded" knowledge */
    KILN_KNOW_CAT_QUERIES = 3, /* GameKnowledgeQueryResourceDb: "query:<name>" -> the query */
};

typedef struct KilnKnowledgeName {
    uint32_t size;            /* sizeof(KilnKnowledgeName) */
    uint32_t kind;            /* KILN_KNOW_NAME_* */
    uint32_t id;
    uint32_t catalog;         /* KILN_KNOW_CAT_* it came from, 0 raw */
    uint32_t index;           /* index in that catalog */
    KilnKnowledgeQuery query; /* the full requirement (npc unlock, quest steps): compare with query(); a plain
                                 knowledge name: {id, 1, EQUALS, BOOL} */
} KilnKnowledgeName;          /* 32 bytes */

/* ---------------------------------------------------------------------------------------------- journal ------- */

typedef struct KilnJournalQuest {
    uint32_t size;          /* sizeof(KilnJournalQuest) */
    uint32_t entry_id;      /* JournalEntryId; also the quest's player Bool knowledge "rewarded" */
    uint32_t name;          /* LocaTagId of the quest's name (texts are client data) */
    uint32_t priority;
    uint8_t source;         /* QuestSourceType: 0 None, 1 Flame, 2 Blacksmith, 3 Alchemist, 4 Huntress, 5 Farmer, ... */
    uint8_t type;           /* QuestType: 0 Auto, 1 WorldQuest, 2 PlayerQuest */
    uint8_t main;           /* isMainQuest */
    uint8_t tutorial;
    uint8_t all_players;    /* unlockForAllPlayers */
    uint8_t reserved[3];
    uint32_t steps;         /* journal entries (steps) */
    uint32_t rewards;
    uint32_t reward_xp[4];  /* experienceValueId of the first rewards (BalancingRegistry), 0 none */
    uint32_t reward_item[4];/* itemId of the first rewards, 0 none */
} KilnJournalQuest;         /* 64 bytes */

typedef struct KilnJournalStep {
    uint32_t size;          /* sizeof(KilnJournalStep) */
    uint32_t entry_id;
    uint32_t name;          /* LocaTagIds */
    uint32_t text;
    uint32_t map_marker;    /* the MapMarker.markerId tag of the step's marker, 0 none */
    KilnKnowledgeQuery visible;  /* knowledgeRequirement */
    KilnKnowledgeQuery done;     /* completionRequirement */
    uint32_t progress_steps;     /* progressStepsRequirement entries */
    uint32_t recommended_level;
    uint32_t item_icon;
} KilnJournalStep;          /* 56 bytes */

typedef struct KilnKnowledgeNpc {
    uint32_t size;          /* sizeof(KilnKnowledgeNpc) */
    uint32_t npc_id;        /* NpcId = FNV-1a-32 of the NPC template's GUID */
    uint32_t name;          /* LocaTagId */
    uint32_t workshop;      /* WorkshopId, 0 none */
    uint32_t item;          /* the summon tool's ItemId */
    KilnKnowledgeQuery unlock; /* unlockRequirement (id 0: always unlocked) */
    uint8_t unlocked;       /* the game's NpcRegistry says unlocked (npc_unlock_state_server), 0xff unknown */
    uint8_t crafting_assistant;
    uint8_t reserved[2];
} KilnKnowledgeNpc;         /* 36 bytes */

/* ---------------------------------------------------------------------------------------------- events -------- */

/* KilnKnowledgeEvent.kind */
enum {
    KILN_KNOW_EVENT_CHANGED = 1,        /* target's id: old_value -> new_value (request: the Kiln request, 0 = game) */
    KILN_KNOW_EVENT_DIALOGUE = 2,       /* ServerSelectDialogOptionEvent: target = player, npc, id = line id (0
                                           farewell, 1 missing requirement), old_value = 0 Start / 1 Stop */
    KILN_KNOW_EVENT_CLIENT = 3,         /* a client's TriggerKnowledgeAction: target = player, id, old_value = type,
                                           new_value = value; request = held id (0 = it went through) */
    KILN_KNOW_EVENT_DIALOGUE_HELD = 4,  /* a dialogue line's knowledge held for a decision: target = player, npc, id =
                                           line id, request = held id, flags = KILN_KNOW_CHECK_* that failed, extra =
                                           operations (held_ops), distance */
    KILN_KNOW_EVENT_RESOLVED = 5,       /* a held entry was decided: request = held id, new_value = 1 applied / 0
                                           dropped, flags: 1 = by a decide() call, 0 = by the default */
};

/* KilnKnowledgeEvent.flags for DIALOGUE_HELD */
enum {
    KILN_KNOW_CHECK_REQUIREMENT = 0x01, /* the line's knowledgeRequirement is false for that player */
    KILN_KNOW_CHECK_DISTANCE = 0x02,    /* the player is farther from the NPC than settings.dialogue_range */
    KILN_KNOW_CHECK_ERROR = 0x04,       /* a check could not run (evaluator error, no position): counted as passed */
};

typedef struct KilnKnowledgeEvent {
    uint64_t seq;       /* 1, 2, 3, ... */
    uint64_t step;
    uint32_t kind;      /* KILN_KNOW_EVENT_* */
    KilnEntity target;
    uint32_t id;
    uint32_t old_value;
    uint32_t new_value;
    KilnEntity npc;
    uint64_t request;
    uint32_t flags;
    uint32_t extra;     /* CHANGED: the object's type */
    float distance;     /* DIALOGUE_HELD: metres, -1 unknown */
    uint32_t reserved;
} KilnKnowledgeEvent;   /* 64 bytes */

/* A held operation (the game's KnowledgeUnlockOperation + its player context). */
typedef struct KilnKnowledgeOp {
    uint32_t id;
    uint8_t type;       /* KILN_KNOW_BOOL/COUNTER/FLAGS */
    uint8_t reserved[3];
    uint32_t value;
    uint32_t context;   /* the game's KnowledgeTriggerEntityWrapper: (player << 16) | 1 */
} KilnKnowledgeOp;

/* decide() */
enum { KILN_KNOW_DENY = 0, KILN_KNOW_ALLOW = 1, KILN_KNOW_DEFAULT = -1 };
/* listen() kinds */
enum { KILN_KNOW_LISTEN_CLIENT = 0x1, KILN_KNOW_LISTEN_DIALOGUE = 0x2 };

/* ---------------------------------------------------------------------------------------------- settings ------ */

/* Security settings (kiln/config/knowledge.json, read at boot and when the file changes; configure() overrides them
 * until the file changes again). Both checks are OFF by default: see docs/kiln/services/knowledge.md "Security". */
typedef struct KilnKnowledgeSettings {
    uint32_t size;            /* sizeof(KilnKnowledgeSettings) */
    uint8_t veto_client;      /* hold every knowledge operation a client sends (TriggerKnowledgeAction); run it only
                                 if its id is allowed (allow list) or a listener allows it */
    uint8_t check_dialogue;   /* hold a dialogue line's knowledge; run it only if the line's requirement holds for the
                                 player and the player is within dialogue_range of the NPC (or a listener allows it) */
    uint8_t log_client;       /* log each distinct id clients send, once */
    uint8_t reserved;
    float dialogue_range;     /* metres; 0 = no distance check */
    uint32_t allow_count;     /* settings(): ids in the allow list (configure ignores it; use allow()) */
    uint32_t reserved2;
} KilnKnowledgeSettings;      /* 20 bytes */

/* ---------------------------------------------------------------------------------------------- status -------- */

typedef struct KilnKnowledgeStatus {
    uint32_t size;          /* sizeof(KilnKnowledgeStatus) */
    uint8_t knowledge;      /* reads, grants (feature quests.knowledge) */
    uint8_t changes;        /* in-step apply: revoke, set, ordered grants, change events (quests.changes) */
    uint8_t journal;        /* journal + query evaluator (quests.journal) */
    uint8_t npc;            /* NpcRegistry unlock states (quests.npc) */
    uint8_t dialogue;       /* dialogue events / hold (quests.dialogue + the kiln_knowledge program) */
    uint8_t client_input;   /* client knowledge input hold (quests.client_input) */
    uint8_t program;        /* kiln_knowledge runs */
    uint8_t world;          /* a server world with a knowledge registry exists */
    uint32_t world_objects; /* GameKnowledgeResource: static world objects */
    uint32_t player_objects;
    uint32_t world_entries; /* entries in the world map now */
    uint32_t players;       /* player maps loaded now */
    uint32_t npcs, quests, queries; /* catalog sizes */
    uint32_t pending;       /* requests waiting for the next apply */
    uint64_t submitted, applied, failed;
    uint64_t changes_seen;  /* KILN_KNOW_EVENT_CHANGED events produced */
    uint64_t client_ops, client_vetoed, dialogue_events, dialogue_held, dialogue_dropped;
    uint64_t last_seq;
    uint32_t queue_max;     /* most entries seen in KnowledgeTriggerQueue at the apply */
    uint32_t applying;      /* the game's knowledge update has run in this world (the world runs, the save's knowledge
                             * is in the maps; before that the maps read empty and changes wait). 0 without quests.changes */
    char reason[160];       /* why a part is off, "" when all is on */
} KilnKnowledgeStatus;      /* 296 bytes */

/* ---------------------------------------------------------------------------------------------- the table ----- */

typedef struct KilnKnowledgeApi {
    KilnServiceHeader header; /* { sizeof(KilnKnowledgeApi), KILN_KNOWLEDGE_VERSION } */

    KilnResult(KILN_CALL* status)(KilnKnowledgeStatus* out);
    /* What the game's GameKnowledgeResource says about an id. KILN_ERR_NOT_FOUND: no such object (flags 0). */
    KilnResult(KILN_CALL* info)(uint32_t id, KilnKnowledgeInfo* out);
    /* The id in target's map. KILN_ERR_NOT_FOUND: the id is not an object of that kind of target (out->type NONE).
     * KILN_ERR_UNAVAILABLE: no world, or the player's map is not loaded. Tick thread. */
    KilnResult(KILN_CALL* get)(KilnEntity target, uint32_t id, KilnKnowledgeState* out);
    /* Every id with a non-zero value in target's map; *count = how many there are (BUFFER_TOO_SMALL when more than
     * cap; the first cap are written). Tick thread. */
    KilnResult(KILN_CALL* list)(KilnEntity target, KilnKnowledgeEntry* out, uint32_t cap, uint32_t* count);

    /* Queues a change; *out_request = its id. Checked now: the id must be an object of the target's kind (world / player
     * object; NOT_FOUND), a flag must exist (INVALID_ARGUMENT), REVOKE/SET need the in-step point (UNAVAILABLE). Applied
     * in the next server-world step (while the world loads: in its first knowledge update); the result arrives through
     * the callback / result(). Any thread. */
    KilnResult(KILN_CALL* submit)(KilnOwner owner, const KilnKnowledgeRequest* request, uint64_t* out_request);
    /* KILN_ERR_NOT_FOUND while pending or after it was forgotten (the last 256 results are kept). Any thread. */
    KilnResult(KILN_CALL* result)(uint64_t request, KilnKnowledgeResult* out);
    /* Shortcuts for submit without a callback. */
    KilnResult(KILN_CALL* grant)(KilnOwner owner, KilnEntity target, uint32_t id, uint32_t value, uint64_t* out_request);
    KilnResult(KILN_CALL* revoke)(KilnOwner owner, KilnEntity target, uint32_t id, uint32_t flag, uint64_t* out_request);

    /* Evaluates a requirement with the game's evaluator (named queries, NPC / inventory / flame runtime queries
     * included): target 0 = the world (player queries refused: INVALID_ARGUMENT), else that player; mode
     * KILN_KNOW_MODE_*. *out = 1 true / 0 false. KILN_ERR_NOT_FOUND: the game does not know the id. Tick thread. */
    KilnResult(KILN_CALL* query)(KilnEntity target, const KilnKnowledgeQuery* q, uint32_t mode, uint8_t* out);

    /* Names: "0x1234abcd" / decimal, "npc:<debugName>[.unlock]", "quest:0x<entryId>[.step<n>.visible|.done]",
     * "query:<name>", "line:<template name>/<init|option|transition|farewell><k>". Case-insensitive. Tick thread (the
     * catalog is built at the world's first step: before that only raw ids resolve). */
    KilnResult(KILN_CALL* resolve)(KilnStr name, KilnKnowledgeName* out);
    /* The catalog name of an id (kind KILN_KNOW_NAME_*), "" when it has none. len excludes the NUL. */
    KilnResult(KILN_CALL* name_of)(uint32_t id, uint32_t kind, char* buf, size_t cap, size_t* out_len);
    uint32_t(KILN_CALL* catalog_count)(uint32_t catalog);
    /* Entry `index` of a catalog: its resolved name and its text name ("npc:...", "quest:...", "query:..."). */
    KilnResult(KILN_CALL* catalog_at)(uint32_t catalog, uint32_t index, KilnKnowledgeName* out, char* name, size_t cap,
                                      size_t* out_len);
    /* The journal's quests and steps (the server's JournalRegistry). */
    KilnResult(KILN_CALL* journal_quest)(uint32_t index, KilnJournalQuest* out);
    KilnResult(KILN_CALL* journal_step)(uint32_t quest_index, uint32_t step, KilnJournalStep* out);
    /* The NPC roster (NpcCollection, in NpcRegistry order) with the registry's unlock state. name: debugName. */
    KilnResult(KILN_CALL* npc)(uint32_t index, KilnKnowledgeNpc* out, char* name, size_t cap, size_t* out_len);

    /* Event ring (the last 4096): events with seq > after, oldest first. Any thread. */
    KilnResult(KILN_CALL* events)(uint64_t after, KilnKnowledgeEvent* out, uint32_t cap, uint32_t* count);
    uint64_t(KILN_CALL* last_seq)(void);

    /* Held entries (client knowledge with veto_client, dialogue knowledge with check_dialogue or a dialogue listener):
     * their operations, and the decision. A held entry is decided by decide() or, 8 steps after it was held (at once
     * when nobody listens), by its default: client knowledge dropped unless allowed, dialogue knowledge applied unless
     * a check failed. KILN_ERR_NOT_FOUND: already decided. */
    KilnResult(KILN_CALL* held_ops)(uint64_t held, KilnKnowledgeOp* out, uint32_t cap, uint32_t* count);
    KilnResult(KILN_CALL* decide)(uint64_t held, int32_t decision);
    /* A listener (Kiln.Quests' event pump) takes the decisions of these kinds (KILN_KNOW_LISTEN_*; 0 = none). With
     * LISTEN_DIALOGUE, dialogue knowledge is held even without check_dialogue. */
    KilnResult(KILN_CALL* listen)(uint32_t kinds);

    KilnResult(KILN_CALL* settings)(KilnKnowledgeSettings* out);
    KilnResult(KILN_CALL* configure)(const KilnKnowledgeSettings* in);
    /* Replaces the runtime allow list (ids that pass veto_client); the file's list is merged in again when it
     * changes. */
    KilnResult(KILN_CALL* allow)(const uint32_t* ids, uint32_t count);

    /* Appended: mods' native quests and NPCs. The boot build's manifest as JSON (mods: knowledge ids, quests, lines, NPCs,
     * markers; errors); see docs/kiln/services/npcs.md. out_len is set even when the buffer is too small
     * (KILN_ERR_BUFFER_TOO_SMALL). Any thread. Check header.size before calling. */
    KilnResult(KILN_CALL* native_manifest)(char* buffer, size_t capacity, size_t* out_len);
    /* Diagnostics, tick thread, lab only (no client in a lab): plays what a joining player's client causes on a FREE player
     * block, so the change events' baseline can be tested. Phase 0 activates the block as the game does at login (valid 1,
     * version 0, record flag 0); phase 1 writes the record into the map and raises the record flag as the game's load does;
     * phase 2 takes the block back. KILN_ERR_INVALID_ARGUMENT when the block has a player. The game's own knowledge update
     * then consumes the flag, in the same step's update, exactly as for a real record. */
    KilnResult(KILN_CALL* lab_record)(KilnEntity target, uint32_t phase, const uint32_t* ids, const uint32_t* values, uint32_t count);
} KilnKnowledgeApi;

#ifdef __cplusplus
}
#endif
#endif
