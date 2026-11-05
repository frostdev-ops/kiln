/* SPDX-License-Identifier: MIT
 * kiln.creatures v1: what one creature or NPC is and does at runtime, server-authoritative.
 *   1. Catalog: every creature/NPC template (enemy AI, wildlife, animals, villagers): name, kind, faction, level,
 *      arsenal, loot, model reference. Shipped data (offline survey) checked against the engine's template list.
 *   2. Per-entity values with per-owner layers: level (health follows), faction, scale, resistances, speed and damage
 *      factors, XP factor, loot labels/slots, enemy feature switches, home position. The last owner to let go
 *      restores the value the entity had before the first change. Revoked owners and despawned entities are cleaned
 *      up by the service.
 *   3. Private template copies: one entity reads its own copy of a template component (Enemy01BehaviorState,
 *      EnemyArsenalReference, Aggro, EnemyScaling, ...) through its per-entity template slot; fields are set by
 *      reflection path ("aggroSetup.viewRange"). Other entities of the template keep the shared data.
 *   4. Behaviour: AI state changes and target changes as events (observe only), commands sent as the game's own
 *      events (attack target, guard, stop guarding, restore health), a lab-only readiness switch.
 * Evidence: docs/re/creatures.md. Usage: docs/kiln/services/creatures.md.
 *
 * Threads: functions marked "tick" touch game state: the tick thread in Pre or Post (KILN_ERR_WRONG_THREAD
 * otherwise; managed mods always call from there). Catalog reads and status are thread-safe.
 * Entities are server-world ids (KilnEntity). The table only grows at the end; check header.size. */
#ifndef KILN_CREATURES_H
#define KILN_CREATURES_H
#include "../kiln_api.h"
#include "kiln_spawn.h" /* KilnGuid */
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_CREATURES_SERVICE "kiln.creatures"
#define KILN_CREATURES_VERSION 1u

/* KilnCreatureTemplate.kind / KilnCreatureInfo.kind */
enum {
    KILN_CREATURE_NONE = 0,
    KILN_CREATURE_ENEMY = 1,    /* enemy AI (program enemy01): monsters, bosses, summons and companions */
    KILN_CREATURE_WILDLIFE = 2, /* wildlife (program wildlife) */
    KILN_CREATURE_ANIMAL = 3,   /* farm animals and pets (program animal_behavior) */
    KILN_CREATURE_VILLAGER = 4, /* villagers and workshop NPCs (program npc_active_behavior) */
};

/* KilnCreatureTemplate.flags */
enum {
    KILN_CREATURE_T_BOSS = 1u << 0,         /* has Boss */
    KILN_CREATURE_T_PHASES = 1u << 1,       /* has EnemyPhases */
    KILN_CREATURE_T_SAVED = 1u << 2,        /* no DoNotSave: its entities can reach the save */
    KILN_CREATURE_T_GUARDS_PLAYER = 1u << 3, /* guardSetup.canGuardPlayer */
    KILN_CREATURE_T_ALLY = 1u << 4,         /* faction Player or PlayerAlly */
    KILN_CREATURE_T_WEAKSPOT = 1u << 5,     /* Enemy_Weakspot_* child */
    KILN_CREATURE_T_BOSS_BAR = 1u << 6,     /* has BossHealthBar */
    KILN_CREATURE_T_CATALOG = 1u << 8,      /* listed in the shipped catalog */
    KILN_CREATURE_T_LOADED = 1u << 9,       /* the running game has this template (kiln.spawn's template list) */
    KILN_CREATURE_T_RUNTIME = 1u << 10,     /* not shipped: found in the engine's template infos (a kiln.content clone,
                                               or a newer game build); name, kind and GUID only */
};

typedef struct KilnCreatureTemplate {
    uint32_t size; /* set to sizeof(KilnCreatureTemplate) before calling */
    uint32_t kind; /* KILN_CREATURE_* */
    KilnGuid guid;
    char name[128];   /* template name, NUL-terminated */
    char display[64]; /* in-game name when known (the shipped catalog), else "" */
    uint32_t flags;   /* KILN_CREATURE_T_* */
    uint8_t faction;  /* keen::FactionType (0 Environment .. 15 Ancients); 255 = none */
    uint8_t enemy_class; /* 0 Melee, 1 Ranger, 2 Mage, 255 = not an enemy */
    uint8_t loot_slots_min, loot_slots_max;
    uint32_t level, level_max; /* the template's Level attribute (spawners and OverrideLevel change the level) */
    float xp_mod;              /* ExperienceSource.xpMod */
    float scale;               /* ScaleComponent.uniformScale (1 when absent) */
    KilnGuid arsenal;          /* EnemyArsenalReference.arsenal (all zero = none) */
    KilnGuid loot_labels;      /* RandomLoot.defaultLabels */
    KilnGuid model;            /* ModelResource.model: client data, for reference */
    KilnGuid animation_graph;  /* AnimationGraphResourceComponent2: templates that share it share a skeleton family */
} KilnCreatureTemplate;

/* Per-entity values (set / get / reset). `index` is used by KILN_CF_RESIST only. */
enum {
    KILN_CF_LEVEL = 1,         /* number: Level (1..level max); health is re-scaled by the game's enemy_scaling */
    KILN_CF_FACTION = 2,       /* number: keen::FactionType */
    KILN_CF_SCALE = 3,         /* number: ScaleComponent and the transform's scale */
    KILN_CF_XP = 4,            /* number: ExperienceSource.xpMod */
    KILN_CF_LOOT_LABELS = 5,   /* guid: RandomLoot.defaultLabels (a DefaultLootLabelCollection) */
    KILN_CF_LOOT_SLOTS = 6,    /* vec: x = slotsMin, y = slotsMax */
    KILN_CF_LOOT_DISABLED = 7, /* number: RandomLoot.disableRandomLoot (0/1) */
    KILN_CF_RESIST = 8,        /* number: DamageSusceptibility slot `index` (0..18; 1 = Physical, 15 = Fire, ...;
                                  1.0 = normal damage, 0 = immune) */
    KILN_CF_SPEED = 9,         /* number: factor on the Movement speeds (walk, run, sprint, combat, patrol, flee,
                                  sneak, custom, swim, dive) against the values before the first change */
    KILN_CF_DAMAGE = 10,       /* number: DamageMod_Multiplier (DamageMod = base * multiplier + adder) */
    KILN_CF_FEATURES_ON = 11,  /* number: DynamicEnemy01BehaviorState.enabledFeatures (EnemyFeature bit mask) */
    KILN_CF_FEATURES_OFF = 12, /* number: DynamicEnemy01BehaviorState.disabledFeatures */
    KILN_CF_HOME = 13,         /* vec (metres): DynamicEnemy01BehaviorState.homeZonePosition (roaming, return home) */
    KILN_CF_COUNT = 14,
};

typedef struct KilnCreatureValue {
    double number;
    KilnGuid guid;
    KilnVec3 vec;
} KilnCreatureValue;

/* KilnCreatureInfo.flags */
enum {
    KILN_CREATURE_I_AI = 1u << 0,      /* has enemy AI state (DynamicEnemy01BehaviorState) */
    KILN_CREATURE_I_DEAD = 1u << 1,    /* Actor dead bit */
    KILN_CREATURE_I_READY = 1u << 2,   /* Actor IsReadyForGameplay bit (set by the game near players) */
    KILN_CREATURE_I_FORCED = 1u << 3,  /* the lab readiness switch holds this entity ready */
    KILN_CREATURE_I_LAYERS = 1u << 4,  /* some owner changed a value of this entity */
    KILN_CREATURE_I_PRIVATE = 1u << 5, /* reads at least one private template copy */
};

typedef struct KilnCreatureInfo {
    uint32_t size;  /* set to sizeof(KilnCreatureInfo) before calling */
    uint32_t kind;  /* KILN_CREATURE_* of its template (catalog), NONE when unknown */
    KilnGuid template_guid;
    uint32_t flags; /* KILN_CREATURE_I_* */
    uint8_t state;            /* enemy01::State (0 when no enemy AI) */
    uint8_t awareness_state;  /* NetworkEnemy.awarenessState */
    uint8_t faction;          /* 255 = no Faction */
    uint8_t loot_slots_min, loot_slots_max, loot_disabled, pad[2];
    float awareness;
    uint32_t level, level_max;
    int32_t health, health_max;
    float scale, xp_mod, speed_factor, damage_multiplier;
    uint32_t features_template, features_enabled, features_disabled; /* EnemyFeature masks */
    KilnEntity target;  /* SelectedTargets.targets[0] (0 = none) */
    KilnEntity guard;   /* DynamicEnemy01BehaviorState.guardEntityId */
    KilnVec3 home;      /* homeZonePosition (metres) */
    KilnGuid loot_labels;
    uint32_t layers;    /* values changed by owners (count) */
    uint32_t private_copies; /* private template copies (count) */
} KilnCreatureInfo;

/* Events (set_listener). Delivered on the tick thread in Pre, for what happened in the previous sim step. */
enum {
    KILN_CREATURE_EV_STATE = 1,  /* enemy01 SetState: from -> to (condition = the StateCondition that led there, 255 =
                                    unknown) */
    KILN_CREATURE_EV_TARGET = 2, /* SelectedTargets.targets[0] changed: previous -> target (0 = lost) */
};
#define KILN_CREATURE_MASK(kind) (1u << (kind))

typedef struct KilnCreatureEvent {
    uint32_t size; /* sizeof(KilnCreatureEvent) */
    uint32_t kind; /* KILN_CREATURE_EV_* */
    KilnEntity entity;
    uint8_t from, to, condition, pad;
    KilnEntity target, previous;
    uint32_t reserved;
    uint64_t step; /* server-world step it was seen in */
} KilnCreatureEvent;

/* Returns the event kinds it wants next (KILN_CREATURE_MASK bits); 0 = the hooks record nothing. */
typedef uint32_t(KILN_CALL* KilnCreatureListenerFn)(const KilnCreatureEvent* events, uint32_t count, void* user);

/* Commands (sent as the game's events by kiln.creatures' program in the next sim step). */
enum {
    KILN_CREATURE_CMD_ATTACK = 1,     /* AddThreatTargetEvent {attacker = entity, target, threat = value} */
    KILN_CREATURE_CMD_GUARD = 2,      /* StartGuardingEvent {guarding = entity, guarded = target} (follow / protect) */
    KILN_CREATURE_CMD_STOP_GUARD = 3, /* StopGuardingEvent {guarding = entity, guarded = target} */
    KILN_CREATURE_CMD_HEAL = 4,       /* RestoreEnemyHealthEvent {enemy = entity, duration = value seconds} */
    KILN_CREATURE_CMD_CONTROL = 5,    /* RegisterControlledCombatTargetEvent {attacker = entity, target} */
};

/* Villager commands (npc_command): the game's NpcCommandEvent, read by npc_active_behavior; it addresses an NPC by NpcId
 * (FNV-1a-32 of its template GUID, ActiveNpcState.npcId), not by entity.
 *  RESET: teleports the NPC to `position` / `rotation`, makes that its home (ActiveNpcStateSaved.spawnPosition) and restarts its
 *         state machine: what the game does when a player moves a placed NPC with the summon tool.
 *  GOTO:  state Roam, decision WantsToRoam, target = `position`: it walks there (needs the path grid: a player nearby). */
enum { KILN_NPC_CMD_RESET = 0, KILN_NPC_CMD_GOTO = 1 };
typedef struct KilnNpcCommand {
    uint32_t size;      /* sizeof(KilnNpcCommand) */
    uint32_t type;      /* KILN_NPC_CMD_* */
    uint32_t npc_id;
    uint32_t reserved;
    KilnVec3 position;
    float rotation[4];  /* quaternion x y z w; all 0 = identity */
} KilnNpcCommand;

typedef struct KilnCreatureCommand {
    uint32_t size; /* sizeof(KilnCreatureCommand) */
    uint32_t kind; /* KILN_CREATURE_CMD_* */
    KilnEntity entity;
    KilnEntity target;
    float value;
    uint32_t pool; /* ATTACK: threat pool id (HashKey32; 0 = default) */
} KilnCreatureCommand;

/* set_ready modes (lab only: needs the file <game>/kiln/data/kiln.creatures/force-ready) */
enum {
    KILN_CREATURE_READY_GAME = 0,  /* the game decides (default) */
    KILN_CREATURE_READY_FORCE = 1, /* Kiln sets IsReadyForGameplay every Pre: the AI leaves Spawning without a player */
};

typedef struct KilnCreaturesStatus {
    uint32_t size; /* set to sizeof(KilnCreaturesStatus) before calling */
    uint8_t slots;      /* private template copies work (template slot layout resolved) */
    uint8_t ai;         /* state hooks installed */
    uint8_t ready;      /* readiness bit resolved */
    uint8_t arsenals;   /* arsenal registry readable */
    uint8_t reflection; /* field paths resolve (game reflection) */
    uint8_t commands;   /* command program active */
    uint8_t marks;      /* replication marks program active */
    uint8_t lab_ready;  /* the force-ready lab switch file exists */
    uint32_t catalog;          /* catalog entries */
    uint32_t catalog_loaded;   /* of those, loaded by the running game */
    uint32_t engine_creatures; /* engine templates with enemy AI / villager state the shipped catalog lacks (clones);
                                  added to the catalog as KILN_CREATURE_T_RUNTIME entries */
    uint32_t arsenal_count;    /* arsenals in the registry resource (201 on svn 1285601) */
    uint32_t entities;         /* entities with layers or private copies */
    uint32_t layers;           /* active value layers */
    uint32_t private_copies;   /* live private template copies */
    uint32_t pending_free;     /* copies waiting to be freed */
    uint64_t state_changes;    /* SetState calls seen (while someone listens) */
    uint64_t events_delivered;
    uint64_t events_dropped;   /* ring overflow */
    uint64_t commands_sent;
    uint64_t marks_written;    /* components marked for replication by the marks program */
    uint64_t copy_reads;       /* behaviour updates that read a private Enemy01BehaviorState copy (diagnostic) */
} KilnCreaturesStatus;

typedef struct KilnCreaturesApi {
    KilnServiceHeader header; /* { sizeof(KilnCreaturesApi), KILN_CREATURES_VERSION } */

    KilnResult(KILN_CALL* status)(KilnCreaturesStatus* out); /* any thread */

    /* ---- catalog (thread-safe) ---- */
    uint32_t(KILN_CALL* template_count)(void);
    KilnResult(KILN_CALL* template_at)(uint32_t index, KilnCreatureTemplate* out); /* sorted by name */
    KilnResult(KILN_CALL* template_get)(const KilnGuid* guid, KilnCreatureTemplate* out);
    KilnResult(KILN_CALL* template_find)(KilnStr name, KilnCreatureTemplate* out); /* name, display name, GUID text */

    /* ---- per entity (tick) ---- */
    /* NOT_FOUND: no such entity. Fields of components the entity lacks read as 0 / 255. */
    KilnResult(KILN_CALL* inspect)(KilnEntity entity, KilnCreatureInfo* out);
    /* Sets a value as `owner`'s layer (owner 0 = a direct write without layer: not reverted by anyone).
     * UNAVAILABLE: the entity lacks the component; INVALID_ARGUMENT: bad field/index/value. */
    KilnResult(KILN_CALL* set)(KilnOwner owner, KilnEntity entity, uint32_t field, uint32_t index,
                               const KilnCreatureValue* value);
    /* The current value. */
    KilnResult(KILN_CALL* get)(KilnEntity entity, uint32_t field, uint32_t index, KilnCreatureValue* out);
    /* Drops owner's layer(s): field 0 = all fields of that entity; entity 0 = all entities. The value goes back to the
     * next owner's layer or the original. Returns the number of layers dropped in *out_count (may be NULL). */
    KilnResult(KILN_CALL* reset)(KilnOwner owner, KilnEntity entity, uint32_t field, uint32_t index,
                                 uint32_t* out_count);

    /* ---- private template copies (tick) ----
     * component: template component short name ("Enemy01BehaviorState"); path: reflection path inside it
     * ("aggroSetup.viewRange", "combatSetup.cooldownGroups[0].cooldown", "enabledFeatures"), or the same with the
     * component in front ("Enemy01BehaviorState.aggroSetup.viewRange", component then empty). value: text (numbers,
     * true/false, enum names, flag names "A|B" or "+A -B", GUIDs, keen::Time in seconds).
     * The first set makes the copy (from the template's current bytes) and repoints the entity's template slot. */
    KilnResult(KILN_CALL* ai_set)(KilnOwner owner, KilnEntity entity, KilnStr component, KilnStr path, KilnStr value);
    /* Text of the field as this entity reads it now (its copy, else the shared template). *is_private = 1 when the
     * entity reads a private copy of that component. */
    KilnResult(KILN_CALL* ai_get)(KilnEntity entity, KilnStr component, KilnStr path, char* buffer, size_t capacity,
                                  size_t* out_len, uint32_t* is_private);
    /* Drops owner's field overrides (component/path empty = all). When no override is left the slot points at the
     * shared template again and the copy is freed later. *out_count = overrides dropped. */
    KilnResult(KILN_CALL* ai_reset)(KilnOwner owner, KilnEntity entity, KilnStr component, KilnStr path,
                                    uint32_t* out_count);
    /* Rebuilds the private copies of one entity (0 = all) from the shared template's current bytes and re-applies
     * the overrides (after a template data change). *out_count = copies rebuilt. */
    KilnResult(KILN_CALL* ai_refresh)(KilnEntity entity, uint32_t* out_count);
    /* Field list of a template component as JSON ([{"path","type","offset","bytes"}...]); thread-safe once the
     * reflection is resolved. *out_len = full length (BUFFER_TOO_SMALL when capacity is short). */
    KilnResult(KILN_CALL* ai_fields)(KilnStr component, char* buffer, size_t capacity, size_t* out_len);

    /* ---- owners ---- */
    /* Drops everything `owner` set (values, copies, readiness): a mod unloading. Any thread (applied at the next
     * Pre when called elsewhere). Returns what was dropped now (0 when deferred). */
    uint32_t(KILN_CALL* release_owner)(KilnOwner owner);

    /* ---- behaviour ---- */
    /* One listener (the managed Kiln.Creatures). NULL removes it. Any thread. */
    KilnResult(KILN_CALL* set_listener)(KilnCreatureListenerFn fn, void* user);
    /* Queues a command (tick). UNAVAILABLE when the command program is not active; BUFFER_TOO_SMALL when 256 are
     * pending. */
    KilnResult(KILN_CALL* command)(const KilnCreatureCommand* cmd);
    /* Lab readiness switch (tick). UNAVAILABLE without the lab switch file. */
    KilnResult(KILN_CALL* set_ready)(KilnOwner owner, KilnEntity entity, uint32_t mode);
    /* The arsenal GUID an entity's enemy AI reads now (its private EnemyArsenalReference copy or the template's), and
     * whether that arsenal exists in the registry (*known). Tick. */
    KilnResult(KILN_CALL* arsenal_of)(KilnEntity entity, KilnGuid* out, uint32_t* known);
    /* Appended: sends the game's NpcCommandEvent in the next sim step (any thread). KILN_ERR_UNAVAILABLE: commands off.
     * Check header.size before calling. */
    KilnResult(KILN_CALL* npc_command)(const KilnNpcCommand* command);
} KilnCreaturesApi;

#ifdef __cplusplus
}
#endif
#endif
