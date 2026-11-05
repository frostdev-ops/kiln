/* SPDX-License-Identifier: MIT
 * kiln.skills v1: expands the game's own skill tree (keen::SkillTreeResource) with mod nodes and links, and guards
 * players' saved unlocks against nodes that are gone. Published by kiln-skills.dll. Evidence: docs/re/abilities.md
 * section 4 ("Expanding the skill tree"); usage: docs/kiln/services/skills.md. (Kept next to the service while it is
 * new; it belongs with the other service headers in native/core/abi/services/.)
 *
 * How it works
 *  - Definitions (JSON, one object per node; see define()) come from every mod folder's skills/ *.json files ("gameTree":
 *    {"nodes": [...]}), read natively at boot, and from define() (Kiln.Skills). A definition made by code is also
 *    cached in <kiln>/data/skills/defined/<owner>.json and read again at the next boot for a mod in the load order, so
 *    code definitions on a dedicated server (managed mods load after the world read its tree) are in from the next
 *    start.
 *  - Each world's g38_skilltree::SkillTree reads the SkillTreeResource once, in its loader (sigdb skills/TreeLoad),
 *    and builds every map from it (node id -> node, link id -> link, id -> index, the id list by index). Kiln's
 *    detour builds the expanded resource (the game's bytes untouched, the node and link arrays copied and extended at
 *    the end of the copy) and serves it through kiln.assets as an override of the game's resource right before the
 *    original loader runs: the game builds its own maps from it, in every world of the process (dedicated server;
 *    client: the hosted server world and the client world, so the game's own skill-tree screen draws the nodes).
 *  - Limits of the game (hard caps, by index): PlayerSkillsUi keeps 256 mask bits (one per tree index) and 100 level
 *    bytes (one per levelIndex). The vanilla tree uses 243 nodes and levelIndex 0..90, so 13 nodes, 9 of them with
 *    levels, fit natively. Nodes past that are SHADOW nodes (index >= 256, up to 4096 nodes): the game's tree has them
 *    (its screen draws them, its NodeById finds them) but their unlock state lives in Kiln's shadow table, never in
 *    UnlockedSkillNodes / PlayerSkillsUi / the character save. Hooks make the game's IsUnlocked (every IsSkillUnlocked
 *    native and requiredSkill check), NodeSet (unlock / unlearn), Reachable, PointsSpent and Reset (respec) consult
 *    it, and the client's skill-tree screen shows it. Without every one of those hooks shadow nodes are refused
 *    (hidden; the game's tree keeps working). Kiln.Skills keeps the table per character in Kiln.Saves player data,
 *    sends it to the player's client and runs the shadow nodes' effects (the game's handle_skill_impacts does not see
 *    them). Links that touch a shadow node are only in that node's own lists (and the link list the screen draws).
 *  - New nodes are ordered by node id (FNV-1a of the node GUID) behind the game's nodes, so every process with the
 *    same definitions builds the same index order (mask bits are by index: server and client must agree).
 *  - Login guard (sigdb skills/LoginCheck): when a player's UnlockedSkillNodes is checked at join, an id the tree does
 *    not know (a mod's node, the mod gone) makes the game reset the whole tree. Kiln removes only the unknown ids
 *    first (their points come back: points are derived, never stored); the rest of the tree stays.
 *
 * Threads: status, define, undefine_owner, definitions, strips: any thread. tree, simulate: the tick thread (server
 * world). Fail soft: on an unknown build the missing parts answer KILN_ERR_UNAVAILABLE (status says which). */
#ifndef KILN_SKILLS_H
#define KILN_SKILLS_H
#include "kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_SKILLS_SERVICE "kiln.skills"
#define KILN_SKILLS_VERSION 1u

#define KILN_SKILLS_NODE_CAP 256u  /* PlayerSkillsUi.skillUnlockMasks: 4 x u64, one bit per tree index */
#define KILN_SKILLS_LEVEL_CAP 100u /* PlayerSkillsUi.unlockLevel: one byte per levelIndex */

typedef struct KilnSkillsStatus {
    uint32_t size;             /* set before calling */
    uint8_t tree_load;         /* 1: the loader detour is on (feature skills.tree_load + kiln.assets) */
    uint8_t login_guard;       /* 1: unknown ids are stripped at join (feature skills.login_guard) */
    uint8_t functions;         /* 1: the game's skill functions resolved (simulate, tree) */
    uint8_t texts;             /* 1: node texts reach the game's loca (kiln-content's kc_nc_set_texts export) */
    uint32_t vanilla_nodes;    /* the game's resource (0 before the first world read it) */
    uint32_t vanilla_links;
    uint32_t vanilla_level_max;/* highest levelIndex the game uses (90 on svn 1287369) */
    uint32_t nodes;            /* nodes in the resource Kiln serves now (vanilla + added) */
    uint32_t links;
    uint32_t added_nodes;
    uint32_t added_links;
    uint32_t free_nodes;       /* KILN_SKILLS_NODE_CAP - nodes */
    uint32_t free_levels;      /* level slots left */
    uint32_t defined;          /* definitions known (all owners) */
    uint32_t refused;          /* definitions that are not in the tree (errors, over the caps) */
    uint32_t pending;          /* defined after every live world read its tree: in at the next world */
    uint32_t worlds;           /* tree loads Kiln served */
    uint32_t builds;           /* resources built */
    uint64_t stripped;         /* unknown ids removed at joins */
    uint64_t strip_seq;        /* newest strip record */
    uint32_t shadow_nodes;     /* nodes past the caps in the tree Kiln serves (0 when the shadow hooks are off) */
    uint8_t shadow;            /* 1: the shadow hooks are on (server side); 2: and the client's screen hook */
    uint8_t reserved8[3];
    char reason[160];          /* why something is off ("" when everything works) */
} KilnSkillsStatus;

/* One login-guard record: a player's saved tree had ids the tree does not know. */
typedef struct KilnSkillsStrip {
    uint64_t seq;
    uint64_t step;        /* server-world step (0 when unknown) */
    KilnEntity entity;    /* the player (resolved in the next Pre; 0 when not found) */
    uint32_t count;       /* ids removed */
    uint32_t ids[16];     /* the first 16 */
    uint8_t levels[16];   /* their level bytes (level = byte >> 5) */
} KilnSkillsStrip;

/* simulate(): the game's own skill functions on a scratch UnlockedSkillNodes / PlayerSkillsUi pair (no player). */
enum {
    KILN_SKILLS_SIM_UNLOCK = 1,       /* the game's reachability check, then its NodeSet(unlock) */
    KILN_SKILLS_SIM_UNLOCK_FORCE = 2, /* NodeSet(unlock) without the reachability check */
    KILN_SKILLS_SIM_LOCK = 3,         /* NodeSet(lock) */
    KILN_SKILLS_SIM_CHECK = 4,        /* nothing: report the checks */
    KILN_SKILLS_SIM_ADD_RAW = 5,      /* write `node` into a free slot as the save would (an id the tree may not know) */
    KILN_SKILLS_SIM_STRIP = 6,        /* the login guard's strip */
};
typedef struct KilnSkillsSimStep {
    uint32_t op;
    uint32_t node;
} KilnSkillsSimStep;
typedef struct KilnSkillsSimResult {
    uint32_t op, node;
    uint8_t ok;          /* the step did what it asked */
    uint8_t reachable;   /* before the step: the game's Reachable(node) on the current masks */
    uint8_t rebuild;     /* after: the game's SkillUiRebuild succeeded (fails on an id the tree does not know) */
    uint8_t connected;   /* after: the game's connectivity check (every unlocked node linked to a root) */
    uint8_t levels;      /* after: the game's level check (each id known, levels within maxLevel) */
    uint8_t level;       /* the node's level byte >> 5 after the step */
    uint8_t known;       /* the game's NodeById(node) found it */
    uint8_t reserved;
    int32_t spent;       /* after: the game's SkillPointsSpent over the rebuilt masks (-1: the rebuild failed) */
    uint32_t nodes;      /* after: mask bits set */
    uint32_t stripped;   /* STRIP: ids removed */
    int32_t index;       /* the node's tree index (the mask bit), -1 when unknown */
} KilnSkillsSimResult;

/* Shadow table (nodes past the game's caps). world: KILN_WORLD_SERVER (the server world's players; also the hosting
 * client) or KILN_WORLD_CLIENT (a joined client's local player). */
typedef struct KilnSkillsShadowEntry {
    uint32_t node;
    uint8_t level;      /* 1 for a node without levels; 1..maxLevel */
    uint8_t reserved[3];
} KilnSkillsShadowEntry;

enum {
    KILN_SKILLS_SHADOW_GAME = 1,    /* the game's unlock / unlearn path (NodeSet) */
    KILN_SKILLS_SHADOW_RESET = 2,   /* a respec: the game's paid one (Reset hook) or shadow_reset (kiln.progression) */
    KILN_SKILLS_SHADOW_MIGRATE = 3, /* a shadow node's id found in UnlockedSkillNodes at join: moved into the table */
    KILN_SKILLS_SHADOW_SET = 4,     /* shadow_set */
    KILN_SKILLS_SHADOW_LOGIN = 5,   /* the game's free reset at a join: its join check failed for the native tree */
};

typedef struct KilnSkillsShadowChange {
    uint64_t seq;
    uint32_t world;
    KilnEntity entity;
    uint32_t node;
    uint8_t old_level, new_level;
    uint8_t reason;     /* KILN_SKILLS_SHADOW_* */
    uint8_t reserved;
} KilnSkillsShadowChange;

typedef struct KilnSkillsApi {
    KilnServiceHeader header; /* { sizeof(KilnSkillsApi), KILN_SKILLS_VERSION } */
    KilnResult(KILN_CALL* status)(KilnSkillsStatus* out);
    /* Defines game-tree nodes as `owner`'s: `json` is one node object, an array of them, or {"nodes": [...]}. The node
     * object (docs/kiln/services/skills.md has the full list):
     *   {"guid": fixed GUID (the node id is FNV-1a of its 16 bytes), "name": debug name,
     *    "type": "PassiveSkill" | "Attribute" | "ActiveSkill", "class": "Warrior" | "Mage" | "Ranger" | "Neutral",
     *    "position": [x, y] | "near": <node>, "offset": [dx, dy], "cost": n, "maxLevel": 0..7,
     *    "from": [<node>, ...] (links into this node), "to": [<node>, ...] (links out), "bidirectional": bool,
     *    "like": <node> (copy its effect: program, configs, tooltip values, icons, texts),
     *    "program": <ImpactProgram GUID>, "configs": {"0x<configId>": number, ...}, "scales": {"0x<id>": factor},
     *    "icon": <UiTexture GUID> (e.g. a kiln.assets icon), "texts": {"en": {"name", "description", "perLevel"}},
     *    "unlocksAbility": <ItemInfo GUID> (activeSkillUnlockRef; its ItemId follows)}
     * <node> = "0x<id>" (a game node), a node GUID, or the "name" of another Kiln node. report (optional): JSON
     * {"ok", "nodes": [{"guid", "id", "state": "defined" | "redefined" | "pending" | "error", "error"?}]}.
     * KILN_ERR_INVALID_ARGUMENT when nothing could be defined. Definitions take effect when a world next reads its
     * tree (status.pending says which wait). Any thread. */
    KilnResult(KILN_CALL* define)(KilnOwner owner, KilnStr json, char* report, size_t cap, size_t* out_len);
    /* Sets `owner`'s definitions aside (the owner went away). Nodes a world already has stay in it until the world
     * ends (players keep their unlocks); the next world is built without them (the login guard refunds them).
     * Returns how many. Any thread. */
    uint32_t(KILN_CALL* undefine_owner)(KilnOwner owner);
    /* Every definition as a JSON array {guid, id, name, owner, source, state, index?, levelIndex?, error?}. */
    KilnResult(KILN_CALL* definitions)(char* buffer, size_t cap, size_t* out_len);
    /* The server world's tree as the game holds it (its id list by index and NodeById): JSON {"world", "nodes":
     * [{"index", "id", "type", "class", "cost", "maxLevel", "levelIndex", "x", "y", "in", "out", "kiln"}],
     * "links", "maps": {...counts and capacities...}}. `flags` 1 = the added nodes only. Tick thread. */
    KilnResult(KILN_CALL* tree)(uint32_t flags, char* buffer, size_t cap, size_t* out_len);
    /* Diagnostics, tick thread: `count` steps with the game's skill functions on a scratch node list. */
    KilnResult(KILN_CALL* simulate)(const KilnSkillsSimStep* steps, uint32_t count, KilnSkillsSimResult* out);
    /* Login-guard records with seq > after_seq (a ring of the last 32). */
    KilnResult(KILN_CALL* strips)(uint64_t after_seq, KilnSkillsStrip* out, uint32_t capacity, uint32_t* out_count);
    /* The resource Kiln serves (or would build now): `which` 0 = the expanded bytes, 1 = the game's own bytes. With
     * report (optional): the round-trip check as JSON {"ok", "nodes", "links", "vanillaIdentical", "problems"}: the
     * expanded resource is parsed back and every game node, link and sub-array is compared with the game's.
     * BUFFER_TOO_SMALL with *out_len set when cap is too small (buffer NULL: size only). Any thread. */
    KilnResult(KILN_CALL* resource)(uint32_t which, void* buffer, size_t cap, size_t* out_len, char* report,
                                    size_t report_cap);
    /* ---- shadow table ----
     * shadow_on: 1 when every shadow hook is in (status.reason says which is missing otherwise).
     * shadow_nodes: the shadow nodes of the tree Kiln serves now (ids in index order), any thread.
     * shadow_get / shadow_set: a player's shadow levels (set replaces them; entries for unknown nodes are dropped; a
     *   level above a node's max is cut). Any thread; the hooks see the change at once.
     * shadow_changes: changes since after_seq (a ring of 256), any thread.
     * spent / is_unlocked: diagnostics, tick thread: the game's own PointsSpent / IsUnlocked on the server-world
     *   player's real components (so the shadow hooks answer). */
    uint32_t(KILN_CALL* shadow_on)(void);
    KilnResult(KILN_CALL* shadow_nodes)(uint32_t* out_ids, uint32_t capacity, uint32_t* out_count);
    KilnResult(KILN_CALL* shadow_get)(uint32_t world, KilnEntity entity, KilnSkillsShadowEntry* out, uint32_t capacity,
                                      uint32_t* out_count);
    KilnResult(KILN_CALL* shadow_set)(uint32_t world, KilnEntity entity, const KilnSkillsShadowEntry* entries,
                                      uint32_t count);
    KilnResult(KILN_CALL* shadow_changes)(uint64_t after_seq, KilnSkillsShadowChange* out, uint32_t capacity,
                                          uint32_t* out_count);
    KilnResult(KILN_CALL* spent)(KilnEntity entity, int32_t* out_points);
    KilnResult(KILN_CALL* is_unlocked)(KilnEntity entity, uint32_t node, int32_t* out_unlocked, int32_t* out_reachable);
    /* Diagnostics, tick thread (Pre): asks the game to unlock (op 0) / unlearn (op 1) / reset the tree (op 2) for a
     * server-world player entity exactly as its client would: PlayerInput.fromClient.unlockSkillAction {skillId,
     * unlearnSkill, resetAllSkills} with a new version; the game's player_skilltree handles it in this step (cost,
     * links, the shadow hooks). For player entities without a client behind them (lab tests); a real client overwrites
     * its own input. Op 3 marks the entity's UnlockedSkillNodes initialized: the game's join check waits for the
     * player's skill-point knowledge, which a player-template entity never has, and reads no input until then.
     * Lab tests of what depends on equipment: op 4 sets an item entity's PerkContainerNew.unlockedPerks to `node` (without
     * the game's cost and without replication), op 5 a player entity's selected hotbar slot.
     * Lab tests of a tree that changed under a character: op 6 | level << 8 writes `node` into UnlockedSkillNodes as a character's
     * save holds it; op 7 does to the list what the login guard does with the shadow ids it finds there.
     * Op 8 | slot << 8 says which inventory slot an equipment slot is (node: the inventory's entity, the slot's index in the top byte), on a player entity with none set.
     * Ops 3 and up answer KILN_ERR_UNAVAILABLE unless <kiln>/data/skills/lab-ops exists. */
    KilnResult(KILN_CALL* game_request)(KilnEntity entity, uint32_t op, uint32_t node);
    /* ---- for code that calls the game's skill functions on its own copy of a player's components (kiln.progression).
     * A scratch copy has an address the shadow hooks don't know, so they would treat it as nobody's.
     * shadow_scope: until shadow_scope(0, 0) on this thread, the shadow hooks read every component address as this
     *   player's, so the game's PointsSpent and Reachable count the player's shadow nodes. Any thread.
     * shadow_level: KILN_OK with the node's level and maxLevel (0 = one unlock) when `node` is a shadow node (the level
     *   may be 0), KILN_ERR_NOT_FOUND when it is not. Any thread, no allocation.
     * shadow_reset: clears a player's table, recorded as KILN_SKILLS_SHADOW_RESET. out_levels (optional): the levels
     *   cleared. Any thread.
     * shadow_linked: *out_ok = every shadow node of the player stays linked (to a root, to a native node unlocked in
     *   the masks at `ui`, or through other taken shadow nodes) when `without` (0: nothing) is gone. Any thread. */
    KilnResult(KILN_CALL* shadow_scope)(uint32_t world, KilnEntity entity);
    KilnResult(KILN_CALL* shadow_level)(uint32_t world, KilnEntity entity, uint32_t node, uint32_t* out_level,
                                        uint32_t* out_max_level);
    KilnResult(KILN_CALL* shadow_reset)(uint32_t world, KilnEntity entity, uint32_t* out_levels);
    KilnResult(KILN_CALL* shadow_linked)(uint32_t world, KilnEntity entity, const void* ui, void* tree, uint32_t without,
                                         int32_t* out_ok);
} KilnSkillsApi;

#ifdef __cplusplus
}
#endif
#endif
