/* SPDX-License-Identifier: MIT
 * kiln.playerstate v1: what a player is doing right now, read from the game's own components.
 *
 * Every state is a value of a "source": a field of a component plus the game enum that names its bits or values. The
 * field offset and the names come from the game's reflection on the running build, so a state the game adds appears
 * here with its own name and a state it renumbers keeps working. Usage: docs/kiln/services/player-state.md.
 *
 * Threads: call on the thread that steps the world you want, with no sim step running.
 *  - The server world's thread (Pre/Post): the authoritative state of every player, also on a host.
 *  - A joined client's main thread: the local player from the prediction world, others from replicated components.
 *  Anything else answers KILN_ERR_WRONG_THREAD. */
#ifndef KILN_PLAYERSTATE_H
#define KILN_PLAYERSTATE_H
#include "kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_PLAYERSTATE_SERVICE "kiln.playerstate"
#define KILN_PLAYERSTATE_VERSION 1u
#define KILN_PLAYERSTATE_SOURCES 24u  /* source ids are 1..23 */
#define KILN_PLAYERSTATE_SLOTS 24u
#define KILN_PLAYERSTATE_CLIENT_ACTIONS 48u
#define KILN_PLAYERSTATE_KNOWN_CLIENT_ACTIONS 23u /* KilnPlayerSnapshot.known bit: client_versions was read */

/* scan() want mask: what to read. Each component read is a cost, so ask for what you need. */
enum {
    KILN_PLAYERSTATE_WANT_ACTOR = 1u << 0,  /* Actor: flags, abilities, the running action, collider mode */
    KILN_PLAYERSTATE_WANT_EXTRA = 1u << 1,  /* Climb, HookShot, ranged weapon, fishing, cooking, revive, environment, buff counts */
    KILN_PLAYERSTATE_WANT_VITALS = 1u << 2, /* NetworkPlayerAttributes */
    KILN_PLAYERSTATE_WANT_BASE_ZONE = 1u << 3, /* in_base (kiln.build's base list, the player's position) */
    KILN_PLAYERSTATE_WANT_MOTION = 1u << 4, /* DynamicLocomotion, PlayerState */
    KILN_PLAYERSTATE_WANT_INPUT = 1u << 5,  /* PlayerInput (held inputs, client UI flags, client action versions), ActorInput */
};

/* KilnPlayerSnapshot.world */
enum {
    KILN_PLAYERSTATE_SERVER = 0,     /* the server world: authoritative */
    KILN_PLAYERSTATE_PREDICTION = 1, /* a joined client's prediction of its own player */
    KILN_PLAYERSTATE_REPLICATED = 2, /* a joined client's replicated copy (NetworkActor) */
};

typedef struct KilnPlayerAction {
    uint32_t sequence;     /* FNV-1a of the sequence GUID; 0 = none */
    uint16_t trigger;      /* keen::actor::ActionSequenceTrigger */
    uint8_t input;         /* keen::ecs::PlayerInputType that triggered it */
    uint8_t call_index;
    int64_t trigger_time;  /* ECS time, ns: identifies this start together with sequence */
    int64_t played_ns;     /* how far it has played */
    int64_t length_ns;
    uint64_t used_slot;    /* InventorySlotId: u32 inventory entity, then u8 index */
    uint32_t used_item, used_pide, ammo_item, cost_item;
    uint32_t sub_index;
    uint32_t host;         /* interaction host entity, 0 = none */
} KilnPlayerAction;

typedef struct KilnPlayerSnapshot {
    uint32_t size;
    KilnEntity entity;
    uint32_t world;        /* KILN_PLAYERSTATE_* */
    uint32_t known;        /* bit n set: values[n] was read */
    uint64_t values[KILN_PLAYERSTATE_SOURCES];
    KilnPlayerAction action;
    uint8_t has_action, has_vitals, has_jump, in_base; /* in_base: 0 no, 1 yes, 2 unknown */
    uint16_t health, health_max, stamina, stamina_max, mana, mana_max, body_heat, body_heat_max, special, special_max,
        fog, fog_max, oxygen, oxygen_max, level, comfort;
    uint8_t jump_count, can_swim, can_dive, has_buffs;
    uint8_t food_buffs, buffs, debuffs, rested_buff, terrain_debuff, hungry, reserved[2]; /* food_buffs: real food, not the hunger marker */
    int64_t last_jump_time, server_time, glider_time; /* server_time: the world's ECS time, ns */
    float water_depth, reserved2;
    uint32_t client_versions[KILN_PLAYERSTATE_CLIENT_ACTIONS]; /* version of each versioned client action: see client_action_info */
} KilnPlayerSnapshot;

typedef struct KilnPlayerSourceInfo {
    uint32_t size;
    uint32_t id;
    uint32_t kind;       /* 0 enum (one value at a time), 1 bitmask */
    uint32_t count;      /* entries in the game's enum */
    uint32_t tier;       /* KILN_PLAYERSTATE_WANT_* that reads it */
    uint32_t resolved;   /* 1: the field and the enum were found on this build */
    char component[64];
    char field[64];
    char enum_type[64];
} KilnPlayerSourceInfo;

typedef struct KilnPlayerItemInfo {
    uint32_t size;
    uint32_t id;
    uint32_t category;     /* keen::ItemCategory */
    uint32_t ammunition;   /* keen::AmmunitionType */
    uint32_t slot;         /* keen::EquipmentSlotType */
    uint32_t buff;         /* id of equipment.appliedBuff; 0 none */
    uint32_t buff_slot;    /* keen::BuffSlotType of that buff; 0xFFFFFFFF unknown */
    uint32_t name_len;
    char name[128];
    char category_name[32], slot_name[32], ammunition_name[32], buff_slot_name[32]; /* the game's enum names */
} KilnPlayerItemInfo;

typedef struct KilnPlayerEquipment {
    uint32_t size;
    uint32_t count;        /* entries of slots / items below */
    uint64_t slots[KILN_PLAYERSTATE_SLOTS];
    uint32_t items[KILN_PLAYERSTATE_SLOTS];
    uint32_t stack[KILN_PLAYERSTATE_SLOTS];
    uint64_t held_slot;    /* in hand: what the running action uses, else the selected action bar slot */
    uint32_t held_item;
    int32_t hotbar;        /* selected action bar index 0..15; -1 unknown */
} KilnPlayerEquipment;

typedef struct KilnPlayerBuff {
    uint32_t id;        /* BuffTypeId */
    uint32_t entity;    /* the buff's entity */
    uint32_t slot;      /* keen::BuffSlotType */
    uint32_t cleansed;
    int64_t life_ns, end_ns; /* BuffNetworkData of the buff entity; end 0 = unknown */
    uint32_t level, reserved;
} KilnPlayerBuff;

/* A versioned action the client sends (PlayerInput.fromClient and fromAdminClient, named by the game's own field): it
 * counts a version up, and a game program runs the action once when that differs from ServerConsumedPlayerInput. */
typedef struct KilnPlayerClientAction {
    uint32_t size;
    uint32_t index;
    uint32_t bytes;       /* size of the action's struct */
    uint32_t cancelable;  /* Kiln can hold it for a listener on this build (client_action_hold): its consumed version is
                             known, the action fits KilnPlayerHeldAction, and the hold point resolved */
    char name[48];        /* "changeEquipmentAction", "adminTeleport" */
} KilnPlayerClientAction;

/* A client action Kiln holds for its listener (client_action_hold). */
typedef struct KilnPlayerHeldAction {
    uint32_t size;        /* set to sizeof(KilnPlayerHeldAction) before calling */
    KilnEntity entity;
    uint32_t index;       /* client_action_info index */
    uint32_t version;     /* the version the client sent */
    uint32_t bytes;       /* bytes of payload used */
    uint32_t reserved;
    uint8_t payload[160]; /* the action's struct as the client sent it */
} KilnPlayerHeldAction;

typedef struct KilnPlayerStateStatus {
    uint32_t size;
    uint8_t reflection;  /* field offsets and enum names came from the game */
    uint8_t cancel;      /* actions can be stopped on this build */
    uint8_t names;       /* sequence names can be read */
    uint8_t items;       /* item details can be read */
    uint32_t sources;    /* sources resolved */
    uint8_t client_hold; /* client actions can be held, and so canceled, on this build (client_action_hold) */
    uint8_t reserved[3];
    uint64_t scans, entities_read;
    uint64_t reserved2;
    char detail[160];    /* what did not resolve */
} KilnPlayerStateStatus;

typedef struct KilnPlayerStateApi {
    KilnServiceHeader header;
    KilnResult(KILN_CALL* status)(KilnPlayerStateStatus* out);
    /* The sources (index 1..KILN_PLAYERSTATE_SOURCES-1) and the names of their values, as this build has them. */
    KilnResult(KILN_CALL* source_info)(uint32_t id, KilnPlayerSourceInfo* out);
    KilnResult(KILN_CALL* enum_entry)(uint32_t id, uint32_t index, uint64_t* value, char* name, uint32_t capacity, uint32_t* name_len);
    /* Reads `count` players into `out` (each with out[i].size set by the caller). One bad entity does not stop the rest:
     * its known stays 0. */
    KilnResult(KILN_CALL* scan)(const KilnEntity* entities, uint32_t count, uint32_t want, KilnPlayerSnapshot* out);
    KilnResult(KILN_CALL* equipment)(KilnEntity entity, KilnPlayerEquipment* out);
    KilnResult(KILN_CALL* equipment_slot_name)(uint32_t index, char* name, uint32_t capacity, uint32_t* name_len);
    KilnResult(KILN_CALL* buffs)(KilnEntity entity, KilnPlayerBuff* out, uint32_t capacity, uint32_t* count);
    /* An item stack in a slot (InventorySlotId as in KilnPlayerAction.used_slot). */
    KilnResult(KILN_CALL* slot_item)(uint64_t slot, uint32_t* item, uint32_t* stack, uint32_t* pide);
    KilnResult(KILN_CALL* item_info)(uint32_t item, KilnPlayerItemInfo* out);
    /* The running sub-sequence's name in the game's data: "UseItem_Food_Buff", "Movement_Tool_HangGlider_Start". */
    KilnResult(KILN_CALL* sequence_name)(uint32_t sequence, uint32_t sub, char* name, uint32_t capacity, uint32_t* name_len);
    /* Stops the sequence the actor runs, the way the game ends one whose keep-alive failed, if it is still this
     * start. Server world, Pre. KILN_ERR_NOT_FOUND: it already moved on. */
    KilnResult(KILN_CALL* cancel_action)(KilnEntity entity, uint32_t sequence, int64_t trigger_time);
    /* Test labs only (environment KILN_PLAYERSTATE_LAB=1, else KILN_ERR_UNAVAILABLE): starts a sequence with a used item
     * in the trigger context, as the game does for a real item use. */
    KilnResult(KILN_CALL* lab_use_item)(KilnEntity entity, uint32_t sequence, uint32_t trigger, uint32_t input, uint64_t slot, uint32_t item);
    /* A joined client's or a host's own player; 0 on a dedicated server, in the menu. Any thread. */
    KilnEntity(KILN_CALL* local_player)(void);
    /* The versioned client actions of this build, 0..n-1; KILN_ERR_NOT_FOUND past the end. */
    KilnResult(KILN_CALL* client_action_info)(uint32_t index, KilnPlayerClientAction* out);
    /* The action's struct as PlayerInput holds it now. */
    KilnResult(KILN_CALL* client_action_bytes)(KilnEntity entity, uint32_t index, void* out, uint32_t capacity, uint32_t* bytes);
    /* Cancels the held action (entity, index): client_action_decide with cancel 1 for its version. KILN_ERR_NOT_FOUND
     * when none is held: a version seen in Pre without a hold has already run. Server world, Pre. */
    KilnResult(KILN_CALL* client_action_cancel)(KilnEntity entity, uint32_t index);

    /* ---- added after the first release: check header.size ----
     * Holding client actions, so a listener can cancel them. Server world, Pre.
     * The game copies a client's input into PlayerInput inside the sim step, after Pre and before any program, so a new
     * version runs in the step it arrives in and Pre never sees one in time. While a hold is on, Kiln checks right
     * before the step's programs: a cancelable action with a new version is held for one step (PlayerInput's copy shows
     * the consumed version, so the game's program sees nothing new) and listed by client_action_held in the next Pre.
     * client_action_decide answers it: run (the game's program runs it in that step, one step late) or cancel (its
     * version is marked consumed: the game skips it as it skips an old version, and it never runs). A held action
     * nobody answers runs. ServerConsumedPlayerInput, which the client gets back, only moves when the game runs an
     * action or Kiln cancels one.
     * hold: the players to hold for, every Pre while a listener wants it. count 0 ends it, and so do 8 steps without a
     * call. KILN_ERR_UNAVAILABLE: the hold point did not resolve on this build (status client_hold 0). */
    KilnResult(KILN_CALL* client_action_hold)(const KilnEntity* entities, uint32_t count);
    /* The held actions not listed before (each out[i].size set by the caller). KILN_ERR_BUFFER_TOO_SMALL: more are
     * left for the next call. */
    KilnResult(KILN_CALL* client_action_held)(KilnPlayerHeldAction* out, uint32_t capacity, uint32_t* count);
    /* cancel 0: let it run; 1: cancel it. KILN_ERR_NOT_FOUND: no such held action (already answered, or the player left). */
    KilnResult(KILN_CALL* client_action_decide)(KilnEntity entity, uint32_t index, uint32_t version, uint32_t cancel);
} KilnPlayerStateApi;

#ifdef __cplusplus
}
#endif
#endif
