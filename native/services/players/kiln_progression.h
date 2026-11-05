/* SPDX-License-Identifier: MIT
 * kiln.progression v1: character progression of the players in game (level, experience, skill tree, skill points).
 * Published by kiln-players.dll next to kiln.players. Evidence: docs/re/progression.md; usage:
 * docs/kiln/services/progression.md. (Kept next to the service while it is new; it belongs with the other service
 * headers in native/core/abi/services/.)
 *
 * What the game keeps (server world, the player entity's components):
 *  - Level (UInt32Attribute): the level. Experience (UInt32Attribute): experience into the level, experience granted
 *    and not yet counted, experience the level needs. The game's player_level program moves granted experience into
 *    the level every step and raises the level by one (LevelUpEvent) while experience >= needed, up to the level cap
 *    (BalancingTable.playerLevelCap); at the cap it discards granted experience.
 *  - UnlockedSkillNodes: 256 node ids + a level byte each (3 high bits). PlayerSkillsUi: the masks the game derives
 *    from it (rebuilt by player_skilltree when UnlockedSkillNodes.changeCount differs from the masks' copy).
 *  - Skill points are not stored: total = skillPointsPerLevel * (level - 1) + the amounts of the player's skill-point
 *    knowledge (GameKnowledgeResource.skillPoints: "additional_skillpoint_*" +1, Elixir wells +3), available = total -
 *    the costs of the unlocked nodes. A tree that costs more than the total is reset by the game when the player
 *    logs in next.
 *
 * How Kiln changes it (the game's own code wherever there is some):
 *  - Requests are queued (submit) and applied by Kiln's program kiln_players_progression (kiln.world) in the next
 *    server-world step, inside the step, with the components marked written (replication to the client, the
 *    character save). Experience is granted like the game's quest rewards (granted += n, the attribute formulas
 *    re-run, XPGainedEvent), so the game's player_level does the level-ups. A level is set like the game's admin
 *    command AdminSetCharacterLevel (level, experience 0, granted 0) with the needed experience of natural leveling.
 *    Nodes are unlocked / locked with the game's unlock function and the tree re-derived by player_skilltree. Skill
 *    points are given as unowned +1 skill-point knowledge (KnowledgeTriggerQueue, the game's knowledge path).
 *  - Results come back on the tick thread in Pre of the step after the change (callback), and stay readable for a
 *    while with result().
 *
 * Threads: status, result, events, last_seq, tree (after the world's first step) any thread; get, nodes, xp_for_level,
 * simulate: tick thread (KILN_ERR_WRONG_THREAD elsewhere); get from another thread answers the last snapshot (taken
 * every 8 steps for the players in game). submit: any thread.
 * Fail soft: on an unknown build the missing parts answer KILN_ERR_UNAVAILABLE (status says which). */
#ifndef KILN_PROGRESSION_H
#define KILN_PROGRESSION_H
#include "kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_PROGRESSION_SERVICE "kiln.progression"
#define KILN_PROGRESSION_VERSION 1u

/* KilnProgression.flags */
enum {
    KILN_PROG_HAS_LEVEL = 0x01u,     /* level, level_cap, experience_* are valid */
    KILN_PROG_HAS_SKILLS = 0x02u,    /* points_spent, nodes */
    KILN_PROG_HAS_POINTS = 0x04u,    /* points_total, points_from_*, points_available */
    KILN_PROG_HAS_KNOWLEDGE = 0x08u, /* points_givable */
    KILN_PROG_OVER_BUDGET = 0x10u,   /* points_spent > points_total: the game resets the tree at the next login */
    KILN_PROG_AT_CAP = 0x20u,        /* level >= level_cap: the game discards new experience */
    KILN_PROG_SNAPSHOT = 0x40u,      /* read from another thread: the last snapshot (step says when) */
};

typedef struct KilnProgression {
    uint32_t size;               /* sizeof(KilnProgression) */
    KilnEntity entity;
    uint32_t flags;              /* KILN_PROG_* */
    uint32_t level;
    uint32_t level_cap;          /* the game's player level cap */
    uint32_t experience;         /* into the current level */
    uint32_t experience_needed;  /* what the current level needs for the next (0: the game has not set it yet) */
    uint32_t experience_pending; /* granted, counted by the game's player_level in the next step */
    uint32_t points_total;       /* points_from_level + points_from_knowledge */
    uint32_t points_from_level;  /* points_per_level * (level - 1) */
    uint32_t points_from_knowledge;
    uint32_t points_spent;
    int32_t points_available;    /* total - spent (negative: over budget) */
    uint32_t points_per_level;
    uint32_t nodes;              /* unlocked nodes (a node with levels counts once) */
    uint32_t points_givable;     /* what KILN_PROG_OP_GIVE_POINTS can still add (unowned +1 skill-point knowledge) */
    uint64_t step;               /* server-world step of the read */
    uint32_t reserved[4];
} KilnProgression;

/* One node of the skill tree (the game's SkillTreeResource). */
typedef struct KilnSkillNode {
    uint32_t id;                  /* SkillNodeId (FNV-1a of the editor GUID) */
    uint16_t cost;                /* skill points (per level for nodes with levels) */
    uint8_t type;                 /* 0 Root, 1 Attribute, 2 ActiveSkill, 3 PassiveSkill */
    uint8_t class_alignment;      /* ClassType: 0 Warrior, 1 Mage, 2 Ranger, 3 Neutral */
    uint8_t max_level;            /* 0: one unlock; else levels 1..max_level */
    uint8_t level_index;
    uint8_t incoming, outgoing;   /* link counts */
    uint32_t active_skill_unlock; /* ItemId of the ability item the node unlocks, 0 none */
    uint32_t index;               /* position in the resource */
    float ui_x, ui_y;
} KilnSkillNode;

/* One unlocked node of a player. */
typedef struct KilnSkillNodeState {
    uint32_t id;
    uint8_t level;      /* 1 for a plain node; 0..max_level for a node with levels */
    uint8_t max_level;
    uint16_t cost;      /* what it costs now (level * cost for nodes with levels) */
} KilnSkillNodeState;

/* KilnProgressionRequest.op */
enum {
    KILN_PROG_OP_SET_LEVEL = 1,        /* value = level (1..cap): level, experience 0, granted 0, needed for the level */
    KILN_PROG_OP_ADD_EXPERIENCE = 2,   /* value = amount: granted += amount (player_level counts it next step) */
    KILN_PROG_OP_SET_EXPERIENCE = 3,   /* value = experience into the level (>= needed: the game levels up) */
    KILN_PROG_OP_GIVE_POINTS = 4,      /* value = points: unowned +1 skill-point knowledge, as many as there are */
    KILN_PROG_OP_UNLOCK = 5,           /* node: unlock it, or one more level of a node with levels */
    KILN_PROG_OP_LOCK = 6,             /* node: lock it, or one level less */
    KILN_PROG_OP_RESPEC = 7,           /* every node back (free, like the game's own reset at login) */
};

/* KilnProgressionRequest.flags */
enum {
    KILN_PROG_FORCE = 0x01u,    /* unlock: skip the link and the cost check; lock: skip the "rest stays connected" check */
    KILN_PROG_NO_EVENT = 0x02u, /* add_experience: no XPGainedEvent */
};

typedef struct KilnProgressionResult {
    uint32_t size;      /* sizeof(KilnProgressionResult) */
    uint32_t op;
    uint64_t request;   /* the id submit() returned */
    KilnEntity entity;
    KilnResult status;  /* KILN_OK, or why not (reason says it in words) */
    uint32_t applied;   /* what it did: levels/experience/points/nodes changed (op-specific, see the docs) */
    uint32_t steps;     /* steps from submit to the result */
    KilnProgression before, after; /* after: read in Pre of the step after the change */
    char reason[160];   /* "" on success, else why, NUL-terminated */
} KilnProgressionResult;

typedef void(KILN_CALL* KilnProgressionDoneFn)(const KilnProgressionResult* result, void* user);

typedef struct KilnProgressionRequest {
    uint32_t size;      /* sizeof(KilnProgressionRequest) */
    uint32_t op;        /* KILN_PROG_OP_* */
    KilnEntity entity;  /* a player in game */
    uint32_t value;
    uint32_t node;      /* UNLOCK / LOCK: SkillNodeId */
    uint32_t flags;     /* KILN_PROG_FORCE | KILN_PROG_NO_EVENT */
    uint32_t source;    /* ADD_EXPERIENCE: ExperienceSourceType for XPGainedEvent (0 None, 1 Exploration, 2 Lore,
                           3 Quest, 4 Combat, 5 Mining, 6 ItemSetCompleted) */
    uint32_t reserved;
    KilnProgressionDoneFn callback; /* may be NULL; runs once on the tick thread (Pre) */
    void* user;
} KilnProgressionRequest;

/* KilnProgressionEvent.kind */
enum {
    KILN_PROG_EVENT_LEVEL = 1,  /* level changed: old_value -> new_value */
    KILN_PROG_EVENT_SKILLS = 2, /* the unlocked nodes changed: old/new = points spent */
};

typedef struct KilnProgressionEvent {
    uint64_t seq;       /* 1, 2, 3, ... */
    uint32_t kind;      /* KILN_PROG_EVENT_* */
    KilnEntity entity;
    uint64_t step;
    uint32_t old_value, new_value;
    uint64_t request;   /* the Kiln request that caused it, 0 = the game (or not known) */
} KilnProgressionEvent;

typedef struct KilnProgressionStatus {
    uint32_t size;           /* set before calling */
    uint8_t level;           /* 1: levels and experience work (feature players.progression + runtime checks) */
    uint8_t skills;          /* 1: the skill tree works (players.skills) */
    uint8_t knowledge;       /* 1: skill-point knowledge works (players.skill_knowledge) */
    uint8_t events;          /* 1: XPGainedEvent is sent (players.progression_events) */
    uint8_t program;         /* 1: kiln_players_progression runs */
    uint8_t world;           /* 1: the server world's singletons were found */
    uint8_t reserved8[2];
    uint32_t tree_nodes;     /* nodes of the skill tree */
    uint32_t knowledge_entries; /* skill-point knowledge entries (+1 and +3) */
    uint32_t knowledge_plus1;   /* of them +1 (what GIVE_POINTS uses) */
    uint32_t level_cap;
    uint32_t points_per_level;
    uint32_t pending;        /* requests not applied yet */
    uint64_t submitted, applied, failed;
    uint64_t program_updates;
    uint64_t last_seq;       /* newest progression event */
    char reason[160];        /* why something is off ("" when everything works) */
} KilnProgressionStatus;

/* simulate(): the game's skill functions on a scratch UnlockedSkillNodes/PlayerSkillsUi pair (no player needed). */
enum { KILN_SIM_UNLOCK = 1, KILN_SIM_LOCK = 2, KILN_SIM_RESPEC = 3, KILN_SIM_CHECK = 4 };
typedef struct KilnSkillSimStep {
    uint32_t op;       /* KILN_SIM_* */
    uint32_t node;
} KilnSkillSimStep;
typedef struct KilnSkillSimResult {
    uint32_t op, node;
    uint8_t ok;        /* the step did what it asked */
    uint8_t reachable; /* before the step: a linked node is unlocked (the game's check) */
    uint8_t connected; /* after the step: every unlocked node is still connected to a root */
    uint8_t level;     /* the node's level after the step */
    uint32_t spent;    /* points spent after the step (the game's sum) */
    uint32_t nodes;    /* unlocked nodes after the step */
} KilnSkillSimResult;

/* attr_probe(): the game's attribute lookup and formula re-run on a scratch Experience / Level component laid out as
 * the game initialises one (rootId from the registry, storage at +8). */
typedef struct KilnAttrProbe {
    uint32_t size;        /* sizeof(KilnAttrProbe) */
    uint32_t component;   /* 0 Experience (3 values), 1 Level (2 values) */
    uint32_t storage[4];  /* in: the starting values; out: after the write and the game's formula re-run */
    uint32_t set_id;      /* attribute to write (0x724bf568 level, 0x8dcddaf4 experience, 0x1d7dd102 granted,
                             0x04723f62 needed), 0 = none */
    uint32_t set_value;
    uint32_t root;        /* out: the component's attribute root id */
    int32_t slots[4];     /* out: storage slot of level, experience, granted, needed (-1: not in this component) */
} KilnAttrProbe;

typedef struct KilnProgressionApi {
    KilnServiceHeader header; /* { sizeof(KilnProgressionApi), KILN_PROGRESSION_VERSION } */
    KilnResult(KILN_CALL* status)(KilnProgressionStatus* out);
    /* KILN_ERR_NOT_FOUND: not a player in game; KILN_ERR_UNAVAILABLE: progression does not work on this build. */
    KilnResult(KILN_CALL* get)(KilnEntity entity, KilnProgression* out);
    /* Unlocked nodes of a player (tick thread). *out_count = how many; BUFFER_TOO_SMALL when more than capacity. */
    KilnResult(KILN_CALL* nodes)(KilnEntity entity, KilnSkillNodeState* out, uint32_t capacity, uint32_t* out_count);
    /* The skill tree (static; after the server world's first step). */
    KilnResult(KILN_CALL* tree)(KilnSkillNode* out, uint32_t capacity, uint32_t* out_count);
    /* Queues a change. *out_request = its id. Checked at once: INVALID_ARGUMENT (op, value range), NOT_FOUND (not a
     * player in game), UNAVAILABLE; everything else comes back in the result. */
    KilnResult(KILN_CALL* submit)(KilnOwner owner, const KilnProgressionRequest* request, uint64_t* out_request);
    /* The result of a request: KILN_OK and *out, KILN_ERR_NOT_FOUND while it is pending (or long gone: the last 64
     * results are kept). */
    KilnResult(KILN_CALL* result)(uint64_t request, KilnProgressionResult* out);
    /* Events with seq > after_seq from a ring of the last 64 (level and skill changes of players in game, seen in the
     * snapshots every 8 steps and after Kiln's own changes). */
    KilnResult(KILN_CALL* events)(uint64_t after_seq, KilnProgressionEvent* out, uint32_t capacity, uint32_t* out_count);
    uint64_t(KILN_CALL* last_seq)(void);
    /* Diagnostics, tick thread: the game's experience a level needs, and its level cap. */
    KilnResult(KILN_CALL* xp_for_level)(uint32_t level, uint32_t* out_needed, uint32_t* out_cap);
    /* Diagnostics, tick thread: runs `steps` with the game's skill functions on a scratch node list. */
    KilnResult(KILN_CALL* simulate)(const KilnSkillSimStep* steps, uint32_t count, KilnSkillSimResult* out);
    /* Diagnostics, tick thread: see KilnAttrProbe. */
    KilnResult(KILN_CALL* attr_probe)(KilnAttrProbe* probe);
    /* Lab: this entity (a player template, no client) counts as a player in game for get, nodes, submit; 0 ends it. */
    KilnResult(KILN_CALL* lab_player)(KilnEntity entity);
} KilnProgressionApi;

#ifdef __cplusplus
}
#endif
#endif
