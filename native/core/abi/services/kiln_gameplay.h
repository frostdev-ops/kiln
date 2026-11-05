/* SPDX-License-Identifier: MIT
 * kiln.gameplay v1: what players (and NPCs) do in the world, as events a mod can watch and, where the server can
 * refuse safely, cancel (Minecraft Forge's PlayerInteractEvent, BlockEvent, ItemCraftedEvent, EntityItemPickupEvent,
 * EntityTeleportEvent, LivingSpawnEvent.CheckSpawn). Evidence: docs/re/gameplay.md. Usage: docs/kiln/services/gameplay.md.
 *
 * Two mechanisms, chosen per action by where the game decides it:
 *  1. Pending ECS events (tick thread, Pre). The game's programs talk through ECS events that sit in per-job buffers
 *     until the next sim step starts and delivers them. In Pre (before that step) kiln.gameplay reads them: craft
 *     requests, interaction attempts, loot interactions, pickups, interaction answers. Subscribers may cancel the
 *     first three; Kiln then neutralizes the pending event before the game delivers it, so the game's own handler
 *     never runs (crafting: nothing is consumed or made; interaction/loot: the record becomes the game's own
 *     DeclinedInteraction, which ends the guest's interaction sequence as when the game refuses).
 *  2. Hooks inside the step (game worker threads): the building core (place block / prop, dismantle, pick up prop),
 *     the terraforming impact, the craft core, and random spawners' queued spawn. No mod code can run there, so the
 *     only synchronous decision is the native zone table below (a protected area denies an action to every player
 *     not on its list). What happened (and what a zone refused) is reported in Post of the same step.
 *  3. Teleports: a watched entity (every player, plus entities passed to watch()) with an unapplied Teleport
 *     component is reported in Pre, before the game's `teleport` program moves it; cancel marks the component
 *     applied (the game removes it without moving), and a subscriber may change the destination.
 *
 * Threads: every callback runs on the tick thread (KILN_GAMEPLAY_PHASE_* says Pre or Post); game state may be read.
 * Native subscribers must unsubscribe (and remove their zones) before they unload: the service cannot see
 * owner_revoke. The managed Kiln.Gameplay does this per mod.
 * All table functions: tick thread, except status (any thread). A faulting native subscriber is removed (logged).
 * Natural spawns (KILN_GAMEPLAY_SPAWN) run in Post: the creature exists; cancel destroys it before the step returns.
 * Entity creation / destruction (KILN_GAMEPLAY_ENTITY_JOINED / _LEFT): Post, for the categories set with entity_filter.
 *
 * The table only grows at the end; check header.size before using a field added later. */
#ifndef KILN_GAMEPLAY_H
#define KILN_GAMEPLAY_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_GAMEPLAY_SERVICE "kiln.gameplay"
#define KILN_GAMEPLAY_VERSION 1u

/* KilnGameplayEvent.kind; bit (1 << kind) in subscribe's mask. Fields used per kind are listed with each. */
enum {
    /* Pre, once per step, before every other event: lets a subscriber that listens on behalf of others (the managed
     * Kiln.Gameplay) say what it wants. The return value is not a verdict: it becomes the subscriber's mask for kinds
     * 1..31 (bit 0, this kind, stays set). Subscribe with bit 0 to get it. */
    KILN_GAMEPLAY_TICK = 0,
    /* Pre, cancelable. A craft request the game handles this step (CraftRecipeEvent: a player's craft, or a mod's).
     * actor = operator (whose inventory pays and receives: a player, a chest, a villager), target = station (entity
     * with Crafting: a workbench, a villager, the player itself for hand crafting), id = recipe id, amount = how many
     * times, position = station. Cancel: the request is dropped before the game reads it; nothing is consumed. */
    KILN_GAMEPLAY_CRAFT = 1,
    /* Post. The game's craft core ran once (one unit of a craft request, or an automated station's cycle).
     * actor = operator, target = station, id = recipe, id2 = workshop id, result = the game's code (0 crafted, 2 no
     * room for the output, 4 missing materials or requirements, 5 missing crafting props), outputs = the recipe's
     * outputs for one craft, position = station. */
    KILN_GAMEPLAY_CRAFTED = 2,
    /* Pre, cancelable. Someone accepts an interaction offer (AcceptOfferedInteractionEvent; E on a chest, door,
     * workstation, bed, NPC, portal...; villagers send it too). actor = guest, target = host (the entity offering),
     * id = offer id, position = host. Cancel: the attempt becomes the game's DeclinedInteraction. */
    KILN_GAMEPLAY_INTERACT = 3,
    /* Pre. The game's answer to an attempt of the previous step. actor = guest, target = host, id = offer id,
     * op = 1 accepted (InteractionAcceptedEvent) / 0 declined (DeclinedInteraction, incl. ones Kiln made). */
    KILN_GAMEPLAY_INTERACTED = 4,
    /* Pre, cancelable. A loot interaction (LootInteractionEvent: take an item lying in the world, loot all, harvest,
     * gather produce). actor = guest (who receives), target = host (what is looted), id = offer id, op = flags
     * (1 loot all, 2 bypass interaction), position = host. Cancel: becomes a DeclinedInteraction; nothing moves. */
    KILN_GAMEPLAY_LOOT = 5,
    /* Pre. Items arrived in an inventory in the previous step (the game's PickupEvent, the "+N" popup source):
     * looting, harvesting, crafting output, building refunds. actor = receiver (pickedUpBy), target = source entity
     * (0 = none), id = item id, amount = count, extra = target slot's inventory entity, id2 = slot index,
     * op = 1 when hidden in the UI, position = where. */
    KILN_GAMEPLAY_PICKUP = 6,
    /* Post. A building operation of this step. op = KILN_GAMEPLAY_BUILD_*, actor = player, id = item (block /
     * material / prop item), id2 = terraform type (TERRAFORM: 0 remove, 1 add, 2-3 flatten, 4-5 overgrowth, 6-7 decay),
     * target = the prop entity (PICK_UP_PROP), position = where, result = 1 done / 0 the game refused / -1 a zone
     * refused (flags has KILN_GAMEPLAY_F_DENIED, zone = which). */
    KILN_GAMEPLAY_BUILD = 7,
    /* Pre, cancelable. A watched entity is about to be teleported by the game (fast travel, respawn, portal, admin
     * teleport, kiln.world). actor = entity, target = target entity (a spawn point / altar, 0 = none), position =
     * where it is, target_position = the destination (a subscriber may change it: set KILN_GAMEPLAY_F_RETARGET),
     * op = 1 when the game searches the ground near the destination. Cancel: the entity stays. */
    KILN_GAMEPLAY_TELEPORT = 8,
    /* Post. A watched entity was teleported this step (its TeleportCount changed). actor = entity, amount = the new
     * count, position = where it was before, target_position = where it is now. */
    KILN_GAMEPLAY_TELEPORTED = 9,
    /* Post, cancelable. A random spawner (the world's natural enemy / wildlife spawns) created a creature this step.
     * actor = the new entity (0 when a zone refused it), guid = template, position = where. Cancel: the entity is
     * destroyed before the step returns (clients never see it); the spawner treats it as gone. */
    KILN_GAMEPLAY_SPAWN = 10,
    /* Post (or the Post after a tick-thread creation), cancelable for non-players. An entity was created in the server
     * world: by any game path (spawners, NPC logic, loot, child entities, players joining, the save loading) or by
     * Kiln.Spawn. Only categories enabled with entity_filter() are reported. actor = the entity, target = its parent
     * (0 = none), guid = template, op = KILN_GAMEPLAY_ENTITY_* category, extra = 1 when it was created outside the
     * game's sim step (by Kiln: Kiln.Spawn, a service, a mod in Pre/Post), position = where. Cancel: the entity is destroyed again (kiln.spawn), like a canceled natural
     * spawn. (Table v1 size >= offsetof(entity_filter)+8.) */
    KILN_GAMEPLAY_ENTITY_JOINED = 11,
    /* Post, observe. An entity was destroyed (the game's own removal: LifeTime, culling, loot taken, despawns, queued
     * destroys; Kiln.Spawn). Same fields as ENTITY_JOINED, read just before it went. Entities removed in bulk when the
     * world shuts down are not reported. */
    KILN_GAMEPLAY_ENTITY_LEFT = 12,
    /* Pre, cancelable. A player discards an item stack from an inventory (Enshrouded has no world drop: the inventory
     * "destroy" action of a transfer, or the item menu's Trash). Read from the player's PlayerInput against
     * ServerConsumedPlayerInput before inventory_actions handles it. actor = player, extra = the slot's inventory
     * entity, id2 = slot index, id = item id in that slot, amount = count, target = the stack's item entity (gear),
     * op = KILN_GAMEPLAY_DISCARD_*. Cancel: the action is marked consumed, so the game never runs it; the item stays. */
    KILN_GAMEPLAY_DISCARD = 13,
};

/* KilnGameplayEvent.op for KILN_GAMEPLAY_DISCARD */
enum {
    KILN_GAMEPLAY_DISCARD_DESTROY = 1, /* InventoryTransferAction with InventoryActionType::Destroy (source slot) */
    KILN_GAMEPLAY_DISCARD_TRASH = 2,   /* ItemAction with ItemActionType::Trash */
};

/* entity_filter() categories and KilnGameplayEvent.op of ENTITY_JOINED / ENTITY_LEFT (template kind via kiln.spawn) */
enum {
    KILN_GAMEPLAY_ENTITY_PLAYER = 0x01u,   /* player entities (1..1023) */
    KILN_GAMEPLAY_ENTITY_CREATURE = 0x02u, /* enemies, animals */
    KILN_GAMEPLAY_ENTITY_NPC = 0x04u,      /* villagers */
    KILN_GAMEPLAY_ENTITY_PROP = 0x08u,     /* Prop_* (chests, stations, decoration, building props) */
    KILN_GAMEPLAY_ENTITY_PICKUP = 0x10u,   /* loose item pickups */
    KILN_GAMEPLAY_ENTITY_OTHER = 0x20u,    /* the rest: VFX, attacks, projectiles, loot containers, spawners, weakspots */
    KILN_GAMEPLAY_ENTITY_ALL = 0x3Fu,
};

/* KilnGameplayEvent.op for KILN_GAMEPLAY_BUILD */
enum {
    KILN_GAMEPLAY_BUILD_PLACE_BLOCK = 1,  /* a voxel blueprint block (walls, floors, ...) */
    KILN_GAMEPLAY_BUILD_PLACE_PROP = 2,   /* a prop item (chest, workstation, torch, bed, ...) */
    KILN_GAMEPLAY_BUILD_DISMANTLE = 3,    /* tear down a built block */
    KILN_GAMEPLAY_BUILD_PICK_UP_PROP = 4, /* pick a placed prop up (target = its entity) */
    KILN_GAMEPLAY_BUILD_TERRAFORM = 5,    /* dig / add / flatten terrain (pickaxe, shovel, terrain tools) */
};

/* KilnGameplayEvent.flags */
enum {
    KILN_GAMEPLAY_F_CANCELABLE = 0x01u, /* returning KILN_GAMEPLAY_CANCEL has an effect */
    KILN_GAMEPLAY_F_CANCELED = 0x02u,   /* canceled so far (by a zone or an earlier subscriber) */
    KILN_GAMEPLAY_F_DENIED = 0x04u,     /* a zone denied it (zone = its id) */
    KILN_GAMEPLAY_F_RETARGET = 0x08u,   /* TELEPORT: set by a subscriber that changed target_position */
    KILN_GAMEPLAY_F_INJECTED = 0x10u,   /* sent by inject() (tests, mods) */
    KILN_GAMEPLAY_F_REPLACED = 0x20u,   /* SPAWN: a spawn_replace rule swapped the spawner's template (guid = the new one) */
};

/* Subscriber verdicts */
enum {
    KILN_GAMEPLAY_KEEP = 0,   /* keep the current state (flags & KILN_GAMEPLAY_F_CANCELED) */
    KILN_GAMEPLAY_CANCEL = 1, /* cancel (cancelable kinds only) */
    KILN_GAMEPLAY_ALLOW = 2,  /* un-cancel, also a zone's denial of a Pre event */
};

typedef struct KilnGameplayItem {
    uint32_t item;  /* item id */
    uint32_t count;
} KilnGameplayItem;

#define KILN_GAMEPLAY_MAX_OUTPUTS 8u

typedef struct KilnGameplayEvent {
    uint32_t size;          /* sizeof(KilnGameplayEvent) */
    uint32_t kind;          /* KILN_GAMEPLAY_* */
    uint32_t flags;         /* KILN_GAMEPLAY_F_* */
    uint32_t zone;          /* zone id that denied it (0 = none) */
    uint64_t step;          /* server-world step */
    KilnEntity actor;
    KilnEntity target;
    uint32_t id;
    uint32_t id2;
    uint32_t amount;
    int32_t result;
    uint32_t op;
    uint32_t extra;
    KilnVec3 position;          /* metres */
    KilnVec3 target_position;   /* metres */
    uint8_t guid[16];           /* SPAWN: template GUID (Windows byte order, as kiln.spawn) */
    uint32_t output_count;
    uint32_t phase;             /* KILN_GAMEPLAY_PHASE_* */
    KilnGameplayItem outputs[KILN_GAMEPLAY_MAX_OUTPUTS];
} KilnGameplayEvent;

enum {
    KILN_GAMEPLAY_PHASE_PRE = 0,
    KILN_GAMEPLAY_PHASE_POST = 1,
};

/* Return a verdict (KILN_GAMEPLAY_KEEP / _CANCEL / _ALLOW). TELEPORT subscribers may write target_position and set
 * KILN_GAMEPLAY_F_RETARGET. The event pointer is valid during the call only. */
typedef uint32_t(KILN_CALL* KilnGameplayFn)(KilnGameplayEvent* event, void* user);

/* ---- zones: synchronous protection, checked in the game's own code paths ---- */

/* KilnGameplayZone.deny: actions refused to players that are not allowed. */
enum {
    KILN_GAMEPLAY_DENY_BUILD = 0x01u,     /* place blocks and props */
    KILN_GAMEPLAY_DENY_BREAK = 0x02u,     /* dismantle blocks, pick props up */
    KILN_GAMEPLAY_DENY_TERRAFORM = 0x04u, /* dig, add, flatten terrain */
    KILN_GAMEPLAY_DENY_INTERACT = 0x08u,  /* accept interaction offers of hosts inside (open chests, doors, stations) */
    KILN_GAMEPLAY_DENY_LOOT = 0x10u,      /* loot / harvest / pick up hosts inside */
    KILN_GAMEPLAY_DENY_CRAFT = 0x20u,     /* craft at stations inside */
    KILN_GAMEPLAY_DENY_SPAWN = 0x40u,     /* natural spawns (random spawners) inside: for everyone */
    KILN_GAMEPLAY_DENY_ALL = 0x7Fu,
};

enum {
    KILN_GAMEPLAY_ZONE_SPHERE = 0,   /* center + radius */
    KILN_GAMEPLAY_ZONE_CYLINDER = 1, /* center x/z + radius, from min.y to max.y */
    KILN_GAMEPLAY_ZONE_BOX = 2,      /* min .. max */
};

enum {
    KILN_GAMEPLAY_ZONE_ALLOW_ADMINS = 0x01u, /* players with the game's admin bit are allowed */
};

typedef struct KilnGameplayZone {
    uint32_t size;      /* sizeof(KilnGameplayZone) */
    uint32_t deny;      /* KILN_GAMEPLAY_DENY_* */
    uint32_t shape;     /* KILN_GAMEPLAY_ZONE_* */
    uint32_t flags;     /* KILN_GAMEPLAY_ZONE_ALLOW_* */
    KilnVec3 center;
    double radius;
    KilnVec3 min;
    KilnVec3 max;
    const uint64_t* allowed_steam_ids; /* players allowed in this zone (copied) */
    uint32_t allowed_count;
    uint32_t priority;  /* reserved, 0 */
    KilnStr name;       /* copied */
} KilnGameplayZone;

/* ---- diagnostics / mods: send the game's own request events from Kiln's program ---- */

enum {
    KILN_GAMEPLAY_INJECT_CRAFT = 1,    /* CraftRecipeEvent {operator = actor, station = target, recipe = id, amount} */
    KILN_GAMEPLAY_INJECT_INTERACT = 2, /* AcceptOfferedInteractionEvent {guest = actor, host = target, offer = id} */
    KILN_GAMEPLAY_INJECT_LOOT = 3,     /* LootInteractionEvent {guest = actor, host = target, offer = id, flags} */
};

typedef struct KilnGameplayInject {
    uint32_t size;  /* sizeof(KilnGameplayInject) */
    uint32_t kind;  /* KILN_GAMEPLAY_INJECT_* */
    KilnEntity actor;
    KilnEntity target;
    uint32_t id;
    uint32_t amount;
    uint32_t flags; /* LOOT: 1 loot all, 2 bypass interaction */
    uint32_t reserved;
} KilnGameplayInject;

/* ---- recipes (read-only view of the game's recipe registry) ---- */

typedef struct KilnGameplayRecipe {
    uint32_t size;          /* set to sizeof(KilnGameplayRecipe) before calling */
    uint32_t id;
    uint32_t workshop;      /* the workshop (station kind) that offers it; 0 = hand crafting */
    uint32_t input_count;   /* entries in inputs (at most 8 are copied); item 0 = a category input ("any wood") */
    uint32_t output_count;
    uint32_t reserved;
    int64_t duration_ns;    /* crafting time (automated stations; player crafts ignore it) */
    uint64_t required_props; /* crafting props the station needs nearby (mask) */
    KilnGameplayItem inputs[KILN_GAMEPLAY_MAX_OUTPUTS];
    KilnGameplayItem outputs[KILN_GAMEPLAY_MAX_OUTPUTS];
} KilnGameplayRecipe;

typedef struct KilnGameplayStatus {
    uint32_t size;          /* set to sizeof(KilnGameplayStatus) before calling */
    uint8_t intercept;      /* 1: pending events are read in Pre */
    uint8_t emit;           /* 1: inject works (Kiln's program is registered and the game's emitter resolved) */
    uint8_t craft_hook;     /* 1: the craft core is hooked */
    uint8_t build_hooks;    /* 1: the building core is hooked */
    uint8_t terraform_hook; /* 1: the terraforming impact is hooked */
    uint8_t spawn_hook;     /* 1: random spawners' spawn is hooked */
    uint8_t teleport;       /* 1: Teleport components are watched */
    uint8_t world;          /* 1: a server world steps */
    uint32_t zones;
    uint32_t subscribers;
    uint64_t steps;         /* Pre passes that read pending events */
    uint64_t events_seen;   /* pending records read (all types) */
    uint64_t dispatched;    /* events handed to subscribers */
    uint64_t canceled;      /* Pre events neutralized */
    uint64_t denied;        /* hook operations refused by zones */
    uint64_t hook_records;  /* hook records reported in Post */
    /* --- added after v1's first release (check size) --- */
    uint8_t entity_hooks;   /* 1: createEntity / destroyEntity are hooked */
    uint8_t reserved8[3];
    uint32_t entity_categories; /* KILN_GAMEPLAY_ENTITY_* reported now */
    uint64_t entities_joined, entities_left; /* creations / destructions reported */
    uint64_t entities_filtered; /* seen by the hooks but not in the categories */
    uint8_t discard;        /* 1: discard actions are read from PlayerInput */
    uint8_t reserved9[7];
    uint64_t discards_seen, discards_canceled;
} KilnGameplayStatus;

typedef struct KilnGameplayApi {
    KilnServiceHeader header; /* { sizeof(KilnGameplayApi), KILN_GAMEPLAY_VERSION } */

    /* kinds_mask: bit (1 << KILN_GAMEPLAY_*). Subscribers run in subscription order. */
    KilnResult(KILN_CALL* subscribe)(KilnOwner owner, uint32_t kinds_mask, KilnGameplayFn fn, void* user,
                                     KilnHandle* out);
    KilnResult(KILN_CALL* unsubscribe)(KilnHandle handle);

    /* Zones. *out_id never 0. Tick thread. */
    KilnResult(KILN_CALL* zone_add)(KilnOwner owner, const KilnGameplayZone* zone, uint32_t* out_id);
    KilnResult(KILN_CALL* zone_update)(uint32_t id, const KilnGameplayZone* zone);
    KilnResult(KILN_CALL* zone_remove)(uint32_t id);
    /* Would `action` (one KILN_GAMEPLAY_DENY_* bit) by `player` at `position` be denied? *out_zone = the zone.
     * Uses the player list of the last Pre. Tick thread. */
    bool(KILN_CALL* zone_denies)(KilnEntity player, KilnVec3 position, uint32_t action, uint32_t* out_zone);

    /* Queues one request event; Kiln's program sends it in the next step, so it is seen (and may be canceled) in the
     * Pre after that, exactly like the game's own. KILN_ERR_UNAVAILABLE without the emitter. Tick thread. */
    KilnResult(KILN_CALL* inject)(const KilnGameplayInject* request);
    /* Adds / removes an entity to the teleport watch list (players are always watched). Tick thread. */
    KilnResult(KILN_CALL* watch)(KilnEntity entity, bool on);

    KilnResult(KILN_CALL* status)(KilnGameplayStatus* out);

    /* Recipes. KILN_ERR_NOT_FOUND: no such recipe / workshop. Tick thread. */
    KilnResult(KILN_CALL* recipe)(uint32_t id, KilnGameplayRecipe* out);
    /* The recipe ids a workshop offers; *out_count = how many (BUFFER_TOO_SMALL when more than capacity). */
    KilnResult(KILN_CALL* workshop_recipes)(uint32_t workshop, uint32_t* out, uint32_t capacity, uint32_t* out_count);

    /* --- added later in v1: check header.size --- */
    /* Which KILN_GAMEPLAY_ENTITY_* categories ENTITY_JOINED / ENTITY_LEFT report (0 = none: the hooks only test a flag).
     * One value for the whole service (the managed Kiln.Gameplay sets the union of what its mods asked for). Any
     * thread. UNAVAILABLE: the entity hooks are off on this build. */
    KilnResult(KILN_CALL* entity_filter)(uint32_t categories);

    /* --- added later in v1: check header.size --- */
    /* Natural-spawn template replacement, decided inside random_spawner's QueueSpawn call (worker thread, from this
     * rule table: no subscriber runs there). A natural spawn of template `from` uses `to` instead with probability
     * `chance` (0..1; the first matching rule that wins its roll applies). One rule per (owner, from, to); chance 0
     * removes it. The spawner keeps its bookkeeping (it gets the new entity's id). The SPAWN event reports the new
     * template and has KILN_GAMEPLAY_F_REPLACED. Zones that deny spawns still win. Any thread.
     * UNAVAILABLE: gameplay.spawn is off on this build. */
    KilnResult(KILN_CALL* spawn_replace)(KilnOwner owner, const uint8_t* from_guid, const uint8_t* to_guid, float chance);
    /* Removes every replacement rule of `owner` (a mod unloading). Any thread. Returns the number removed in *out. */
    KilnResult(KILN_CALL* spawn_replace_clear)(KilnOwner owner, uint32_t* out_removed);
} KilnGameplayApi;

#ifdef __cplusplus
}
#endif
#endif
