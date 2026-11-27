/* SPDX-License-Identifier: MIT
 * kiln.items v4 (v1 + water in containers: fuel, fuel_read, fuel_keep, fuel_keep_end; v3 + item moves that keep item state,
 * container names, inventory-transfer events and rules; v4 + item instances: a stamp per item entity, its location and
 * its save record's fields in one read; v5 + equip and unequip through the game's own functions; v6 + equipment sets: a
 * player's equipment slots changed in one transaction): put items into and take them out of inventories (players'
 * backpacks, chests, villagers), gear with a level and rarity included; read an entity's inventory; the game's item
 * catalog.
 *
 * How it works: every give/take goes through the game's own inventory transaction (the code its admin "create item",
 * loot pickup and crafting use): stacks are topped up first, gear gets its own item entity ("pide", with level, perks
 * and durability), a commit applies everything or a rollback undoes it. The transaction needs a ProgramContext and
 * other-entity accessors, so kiln.items runs it inside its own ECS system program ("kiln_items") during the sim step.
 * Requests are therefore asynchronous: submit on the tick thread (or any thread), the transaction runs in the next sim
 * step, and the callback comes on the tick thread in the Pre phase of the step after that (usually one step = ~41 ms
 * later on a dedicated server). Reads and the catalog are synchronous.
 *
 * Threads
 *  - give / take / cancel / status / catalog functions: any thread.
 *  - read / find_inventories: the tick thread, in Pre or Post (no world job running). KILN_ERR_WRONG_THREAD otherwise.
 *  - KilnItemsDoneFn: the tick thread, Pre; game state may be read there (read() works inside the callback).
 *
 * Ids: an item id (KilnItemId) is FNV-1a-32 over the item's 16 GUID bytes, as the game computes it. A slot (KilnSlotId) is the game's InventorySlotId: bits 0-31
 * the 8-slot inventory entity (one of the links of the owner's InventorySetup), bits 32-39 the slot index 0-7.
 *
 * Fail soft: on an unknown game build, or when the program, an accessor or a symbol cannot be verified, give/take
 * return KILN_ERR_UNAVAILABLE and nothing is touched. A fault inside the game's transaction switches give/take off
 * until the server restarts (logged). Evidence: docs/re/items.md. Usage: docs/kiln/services/items.md. */
#ifndef KILN_ITEMS_H
#define KILN_ITEMS_H
#include "../kiln_api.h"
#include "kiln_decisions.h" /* v6: KilnEventEntityKey and KilnSetOpV1, shared with kiln.decisions' kind 18 */
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_ITEMS_SERVICE "kiln.items"
#define KILN_ITEMS_VERSION 9u  /* v2 appends the fuel functions, v3 the move, name and transfer functions, v4 the item
                                  instances (stamps, locations, records), v5 (0.2.0-dev.27) equip and unequip, v6
                                  (0.2.0-dev.29) equipment sets, v7 (0.2.0-dev.30, the gem wave) gem sockets on any gear:
                                  KILN_ITEMS_GEM_SLOT_ANY, REPLACE, DYE, DURABILITY and socket_roll, v8 (0.2.0-dev.29.19,
                                  the equipment wave) gear_write, gear_read and gear_status, then rarity_rolls and
                                  roll_status, v9 (0.2.0-dev.30.3) container_rename and rename_status; check header.size */

typedef uint32_t KilnItemId;  /* 0 = none */
typedef uint64_t KilnSlotId;  /* 0 = none / let the game choose */

#define KILN_SLOT_ID(inventory, index) ((uint64_t)(uint32_t)(inventory) | ((uint64_t)(uint8_t)(index) << 32))
#define KILN_SLOT_INVENTORY(slot) ((KilnEntity)((slot) & 0xFFFFFFFFu))
#define KILN_SLOT_INDEX(slot) ((uint32_t)(((slot) >> 32) & 0xFFu))

/* ---------------------------------------------------------------------------------------------- requests ----- */

/* KilnItemsRequest.flags */
enum {
    KILN_ITEMS_LEVEL = 0x0001u,          /* give: create gear with `level`; the game clamps it to the item's level range */
    KILN_ITEMS_RARITY = 0x0002u,         /* give: create gear with `rarity` (0 Common .. 4 Legendary); without it the
                                            item's default: ItemInfo.rarity, or with generateRarity set the lowest
                                            rarity disableRarityGeneration allows (docs/re/items.md section 10) */
    KILN_ITEMS_EXACT_SLOT = 0x0004u,     /* take: only from `slot`, and only while it still holds `item` (and `pide` when
                                            not 0); otherwise KILN_ITEMS_SLOT_CHANGED and nothing is taken */
    KILN_ITEMS_ALL_OR_NOTHING = 0x0008u, /* give: commit only when every item fits (take is always all or nothing) */
    KILN_ITEMS_NOTIFY = 0x0010u,         /* give to a player: the game's "+N item" popup and PickupEvent */
    KILN_ITEMS_NO_MERGE = 0x0020u,       /* give: do not top up existing stacks, use empty slots only */
    KILN_ITEMS_DRY_RUN = 0x0040u,        /* give/take: run the transaction and roll it back: the result says what would
                                            happen (changes are listed), nothing changes */
    KILN_ITEMS_RECIPE = 0x0080u,         /* give: `item` is a recipe id: run the game's recipe result function `count`
                                            times for `entity`, so the outputs are created exactly as crafting creates
                                            them. Inputs are not taken, a station's requirements count as met.
                                            `done` counts recipe runs, `changes` lists the stacks. Refused with
                                            LEVEL, RARITY or `slot`. Status UNAVAILABLE without feature items.recipe */
    /* since 0.2.0-dev.16 (feature items.pide_options; without it submit returns KILN_ERR_UNAVAILABLE) */
    KILN_ITEMS_UNLOCK = 0x0100u,         /* give gear with `unlock` of its upgrades (perks) unlocked, as the workbench's
                                            upgrade does it (each one raises the level by 1); 255 = all of them */
    KILN_ITEMS_GEM_SLOT = 0x0200u,       /* give weapons with an empty gem slot when the game's own roll gave none */
    /* since 0.2.0-dev.30 (v7, the gem wave; feature items.pide_options, like the two above) */
    KILN_ITEMS_GEM_SLOT_ANY = 0x0400u,   /* with GEM_SLOT: any gear gets the empty gem slot (rings, armour, shields,
                                            tools...), not only weapons. Gems, dyes and customization items never do */
    KILN_ITEMS_REPLACE = 0x0800u,        /* give: in the same transaction first take the item entity `pide` out of
                                            `slot` (it must hold `item` and that entity), then create the new item,
                                            preferably in that slot: both or neither (Gems.AddSocket re-creates gear this
                                            way). `count` 1, a gear item, `slot` and `pide` required; not with RECIPE.
                                            KILN_ITEMS_REFUSED, nothing changed: the item is worn or held (a player's
                                            equipment slot or item in hand, or ItemState IsEquipped), has a gem slot
                                            (its gem would be destroyed with it), or has no readable ItemState */
    KILN_ITEMS_DYE = 0x1000u,            /* give gear dyed with KilnItemsRequestEx.dye (needs the Ex request) */
    KILN_ITEMS_DURABILITY = 0x2000u,     /* give gear at KilnItemsRequestEx.durability (0..1 of its max; needs the Ex
                                            request), as the game's DurabilityPercentageInit sets it */
};

typedef struct KilnItemsRequest {
    uint32_t size;       /* sizeof(KilnItemsRequest) */
    uint32_t flags;      /* KILN_ITEMS_* above; other bits are refused */
    KilnEntity entity;   /* whose inventory: a player (1..1023), chest, villager: any entity with an InventorySetup */
    KilnItemId item;
    uint32_t count;      /* 1..100000 (give spreads over as many slots as needed) */
    uint32_t level;      /* give with KILN_ITEMS_LEVEL: 1..255 */
    uint8_t rarity;      /* give with KILN_ITEMS_RARITY: 0..4 */
    uint8_t unlock;      /* give with KILN_ITEMS_UNLOCK: upgrades to unlock, 1..5 or 255 (all); else 0 */
    uint8_t reserved[2]; /* 0 */
    KilnEntity pide;     /* take with KILN_ITEMS_EXACT_SLOT: the item entity the slot must hold (0 = any) */
    KilnSlotId slot;     /* give: the slot to fill first (0 = the game's choice); take: the slot to take from first
                            (0 = the game's order: a player's held action-bar slot, then everything) */
} KilnItemsRequest;

/* v7: a give with KILN_ITEMS_DYE or KILN_ITEMS_DURABILITY passes this, base.size = sizeof(KilnItemsRequestEx). */
typedef struct KilnItemsRequestEx {
    KilnItemsRequest base;
    KilnItemId dye;      /* KILN_ITEMS_DYE: the dye item (ColorPaletteItem.paletteItemId); 0 = undyed */
    float durability;    /* KILN_ITEMS_DURABILITY: 0..1 of the item's max durability (0 = broken) */
    uint32_t reserved[2];/* 0 */
} KilnItemsRequestEx;

/* KilnItemsResult.status */
enum {
    KILN_ITEMS_DONE = 0,         /* everything requested was given / taken (dry run: would be) */
    KILN_ITEMS_PARTIAL = 1,      /* give: only `done` of `requested` fit; the rest was never created */
    KILN_ITEMS_NO_ROOM = 2,      /* give: nothing fit (or not all, with KILN_ITEMS_ALL_OR_NOTHING); nothing changed */
    KILN_ITEMS_NOT_ENOUGH = 3,   /* take: fewer than requested are there; nothing taken */
    KILN_ITEMS_NO_INVENTORY = 4, /* no such entity, or it has no (initialized) InventorySetup */
    KILN_ITEMS_UNKNOWN_ITEM = 5, /* the item id is not in the game's item registry */
    KILN_ITEMS_SLOT_CHANGED = 6, /* the slot is not one of the entity's, or no longer holds the item / pide */
    KILN_ITEMS_REFUSED = 7,      /* the game's transaction returned an error (game_error); nothing changed */
    KILN_ITEMS_UNAVAILABLE = 8,  /* give/take switched off since the request was accepted; nothing changed */
    KILN_ITEMS_DROPPED = 9,      /* not run: the server world went away or did not step for 10 s */
};

/* One slot the transaction touched. */
typedef struct KilnItemsChange {
    KilnSlotId slot;
    KilnItemId item;
    uint32_t amount;  /* items put into / taken out of this slot */
    KilnEntity pide;  /* gear: the item entity (give: the one the game created at commit, 0 in a dry run; take: the
                         one the game destroys) */
    uint32_t reserved;  /* v4: the stamp of `pide` (see "item instances"), captured at the commit; 0 = no pide, an
                           older service, or items.instances off */
} KilnItemsChange;

#define KILN_ITEMS_MAX_CHANGES 16u

typedef struct KilnItemsResult {
    uint32_t size;          /* sizeof(KilnItemsResult) of kiln.items */
    int32_t status;         /* KILN_ITEMS_DONE .. */
    uint64_t ticket;        /* what give/take returned */
    KilnEntity entity;
    KilnItemId item;
    uint32_t requested;
    uint32_t done;          /* items given / taken (with KILN_ITEMS_DRY_RUN: would be) */
    uint32_t game_error;    /* the game's error code (0x15 unknown item / setup not initialized, 0x16 no InventorySetup,
                               0x1F no target inventory, 0x27 op buffer full), 0 = none */
    uint32_t change_count;  /* entries in changes (slots beyond KILN_ITEMS_MAX_CHANGES are not listed; `done` counts
                               them) */
    uint32_t flags;         /* the request's flags */
    uint32_t steps;         /* sim steps between the request and the transaction */
    KilnItemsChange changes[KILN_ITEMS_MAX_CHANGES];
    char message[160];      /* why it failed or was partial, in words; "" when DONE. NUL-terminated */
} KilnItemsResult;

/* Runs exactly once per accepted request (unless cancelled), on the tick thread in Pre. The result is valid during
 * the call. */
typedef void(KILN_CALL* KilnItemsDoneFn)(const KilnItemsResult* result, void* user);

/* ---------------------------------------------------------------------------------------------- reading ------ */

/* KilnItemsSlot.flags */
enum {
    KILN_ITEMS_SLOT_EQUIPPED = 0x01u, /* gear the owner wears / holds (ItemState IsEquipped) */
    KILN_ITEMS_SLOT_BROKEN = 0x02u,   /* gear with durability 0 (ItemState Broken) */
    KILN_ITEMS_SLOT_GEAR = 0x04u,     /* the item has its own item entity (pide) */
};

typedef struct KilnItemsSlot {
    KilnSlotId slot;
    KilnItemId item;      /* 0 = empty */
    uint32_t count;
    KilnEntity pide;      /* gear: its item entity; 0 otherwise */
    uint16_t level;       /* gear: the item level (ItemState); 0 = none */
    uint8_t rarity;       /* gear: ItemState rarity; stacks: the item's default rarity; 0xFF = unknown */
    uint8_t flags;        /* KILN_ITEMS_SLOT_* */
    uint8_t link;         /* index of the InventorySetup link the inventory came from (0..15; nested Virtual links:
                             the outer index) */
    uint8_t category;     /* InventoryCategory of that link: 1 Customization, 2 Equipment, 3 Currency, 4 Generic */
    uint16_t state;       /* gear: raw ItemState.itemState bits */
    uint32_t reserved;    /* v4: the stamp of `pide` (see "item instances"), captured at the read; 0 = no pide, an older
                             service, or items.instances off */
} KilnItemsSlot;

#define KILN_ITEMS_MAX_INVENTORIES 32u

typedef struct KilnItemsInventory {
    uint32_t size;             /* in: sizeof(KilnItemsInventory) */
    KilnEntity entity;
    uint8_t initialized;       /* InventorySetup.isInitialized (give/take need it) */
    uint8_t setup_flags;       /* InventorySetup.inventoryFlags: 0x01 IsPlayer, 0x02 IsNpc, 0x04 ShowContentPreview,
                                  0x08 IndestructibleWhileNotEmpty */
    uint8_t generic_slots;     /* InventorySetup.genericSlotCount */
    uint8_t available_slots;   /* InventorySetup.availableSlotCount */
    uint32_t inventory_count;  /* 8-slot inventories found (Virtual links expanded) */
    uint32_t slot_total;       /* 8 * inventory_count: the capacity read() needs */
    uint32_t used_slots;       /* slots holding an item */
    KilnEntity inventories[KILN_ITEMS_MAX_INVENTORIES];
    uint8_t categories[KILN_ITEMS_MAX_INVENTORIES];
} KilnItemsInventory;

/* KilnItemsApi.find_inventories flags */
enum {
    KILN_ITEMS_FIND_CONTAINERS = 0x01u, /* neither IsPlayer nor IsNpc: chests, stations, ... */
    KILN_ITEMS_FIND_PLAYERS = 0x02u,
    KILN_ITEMS_FIND_NPCS = 0x04u,
    KILN_ITEMS_FIND_NONEMPTY = 0x08u,   /* only inventories holding at least one item */
};

/* ---------------------------------------------------------------------------------------------- catalog ------ */

typedef struct KilnItemInfo {
    uint32_t size;           /* in: sizeof(KilnItemInfo) */
    KilnItemId id;
    uint8_t guid[16];        /* the item's GUID bytes as the game stores them (id = FNV-1a-32 of these) */
    uint16_t max_stack;
    uint8_t category;        /* ItemCategory: 0 Customization, 1 Equipment, 2 Weapons, 3 Tools, 4 Instrument,
                                5 FishingRod, 6 BuildTools, 7 Consumables, 8 Ammunition, 9 Materials, ... */
    uint8_t rarity;          /* default rarity 0..4 */
    uint32_t item_flags;     /* the game's ItemFlag bits (0x2 HasPIDE, 0x4 IsContainer, 0x8 IsBlueprint, ...) */
    uint32_t level_min;      /* gear: the level range new items roll in (0 = none) */
    uint32_t level_max;
    uint8_t equip_slot;      /* EquipmentSlotType (13 head .. 17 feet, 1 melee weapon, ...) */
    uint8_t gear;            /* 1: every item is unique with its own item entity (level, perks, durability) */
    uint8_t reserved[2];
    KilnStr name;            /* the game's debug name, e.g. "Material_T1_Wood_Logs"; Kiln's copy, valid for the
                                life of the process */
} KilnItemInfo;

/* ---------------------------------------------------------------------------------------------- status ------- */

/* KilnItemsStatus.program */
enum {
    KILN_ITEMS_PROGRAM_NONE = 0,     /* not registered (symbols missing, or registration refused) */
    KILN_ITEMS_PROGRAM_PENDING = 1,  /* waiting for the server world to register programs */
    KILN_ITEMS_PROGRAM_ACTIVE = 2,   /* registered; not run yet */
    KILN_ITEMS_PROGRAM_RUNNING = 3,  /* ran, accessors and singletons verified: give/take work */
    KILN_ITEMS_PROGRAM_BROKEN = 4,   /* ran, but a check failed or the transaction faulted: give/take are off */
};

typedef struct KilnItemsStatus {
    uint32_t size;           /* in: sizeof(KilnItemsStatus) */
    uint8_t transactions;    /* 1: give/take can run */
    uint8_t read;            /* 1: read/find_inventories work */
    uint8_t catalog;         /* 1: the item catalog is loaded */
    uint8_t program;         /* KILN_ITEMS_PROGRAM_* */
    uint32_t pending;        /* requests waiting for the program */
    uint32_t items;          /* items in the catalog */
    uint64_t transactions_committed;
    uint64_t transactions_rolled_back;
    uint64_t program_updates;
    char detail[160];        /* the reason when transactions == 0; NUL-terminated */
} KilnItemsStatus;

/* ---------------------------------------------------------------------------------------------- water ------- */

/* Water in containers ("Fuel" in the game: watering cans, pouches, the water pump, barrels, wells; docs/re/items.md
 * sections 11 and 12). Units: 64 per charge (FuelSetup.maxFuelCharges x 64 is the max). Writes run inside the
 * kiln_items program through the game's tracked other-entity write (WriteOther + the attribute's formulas), so the
 * new amount reaches players and the save like the game's own changes. Feature items.fuel. */

/* KilnItemsFuelRequest.flags */
enum {
    KILN_ITEMS_FUEL_SET = 0x0000u,         /* current := units (clamped to 0..max) */
    KILN_ITEMS_FUEL_FILL = 0x0001u,        /* current := max (units ignored) */
    KILN_ITEMS_FUEL_ADD = 0x0002u,         /* current += units (may be negative; clamped to 0..max) */
    KILN_ITEMS_FUEL_INVENTORIES = 0x0010u, /* entity is a holder (player, chest, ...): every container in its
                                              inventories (Virtual links expanded), only `item` when not 0; without
                                              it entity is the container itself (an item entity, a barrel, a well) */
    KILN_ITEMS_FUEL_DRY_RUN = 0x0040u,     /* say what would change, change nothing */
};

typedef struct KilnItemsFuelRequest {
    uint32_t size;      /* sizeof(KilnItemsFuelRequest) */
    uint32_t flags;     /* one of SET / FILL / ADD, plus INVENTORIES, DRY_RUN; other bits are refused */
    KilnEntity entity;
    KilnItemId item;    /* with INVENTORIES: only containers of this item (0 = every container that holds water) */
    int32_t units;      /* SET: the new amount; ADD: the change (fuel_keep: units per second, > 0) */
    int32_t limit;      /* no higher than this many units (0 = the container's max); a container already above it is
                           left as it is by FILL and ADD */
} KilnItemsFuelRequest;

typedef struct KilnItemsFuelChange {
    KilnEntity container; /* the item entity (pide) or the placed container */
    KilnItemId item;      /* the container's item (0 = not in an inventory) */
    KilnSlotId slot;      /* where it sits (0 = not in an inventory) */
    int32_t before;       /* units */
    int32_t after;        /* units (with DRY_RUN: what it would be) */
    int32_t max;
    uint32_t reserved;  /* 0 */
} KilnItemsFuelChange;

#define KILN_ITEMS_MAX_FUEL_CHANGES 16u

typedef struct KilnItemsFuelResult {
    uint32_t size;      /* sizeof(KilnItemsFuelResult) of kiln.items */
    int32_t status;     /* KILN_ITEMS_DONE (also when nothing needed a change), NO_INVENTORY (no such entity / no
                           inventory with INVENTORIES), SLOT_CHANGED (the entity holds no water: no Fuel),
                           UNAVAILABLE, DROPPED */
    uint64_t ticket;
    KilnEntity entity;
    KilnItemId item;
    uint32_t flags;
    uint32_t matched;   /* containers found */
    uint32_t changed;   /* containers whose amount changed (with DRY_RUN: would change) */
    uint32_t steps;     /* sim steps between the request and the write */
    uint32_t change_count;
    uint32_t reserved;
    KilnItemsFuelChange changes[KILN_ITEMS_MAX_FUEL_CHANGES]; /* the changed containers, first 16 */
    char message[160];  /* "" when DONE; NUL-terminated */
} KilnItemsFuelResult;

typedef void(KILN_CALL* KilnItemsFuelDoneFn)(const KilnItemsFuelResult* result, void* user);

/* What a keep changed since the last call: one entry per container (before = its units before the first write, after =
 * now). Tick thread, Pre. */
typedef void(KILN_CALL* KilnItemsFuelKeepFn)(uint64_t handle, const KilnItemsFuelChange* changes, uint32_t count,
                                             void* user);

/* ---------------------------------------------------------------------------------------------- moves ------- */

/* Moving items between inventories, keeping their state (v3, feature items.move; KILN_ERR_UNAVAILABLE without it).
 * A give/take pair would destroy a piece of gear and create a new one. A move hands the item entity (the "pide" with
 * its level, upgrades, gem, durability and anything a mod keyed to it) over to the new slot through the game's own slot
 * transfer, inside the program and its commit, so replication and the save follow like a player's drag and drop.
 * STACK moves one stack (or part of it), QUICK_STACK and MOVE_ALL run the game's own quick stack ("refill stacks",
 * what a player's R key does) and move-all between two inventories. All three go through the game's functions, so
 * transfer events and rules (below) see them too, flagged KILN_TRANSFER_BY_MOD. Requests run in the next sim step in
 * the kiln_items program, like give and take; the callback comes on the tick thread in Pre. Evidence and what is not
 * known: docs/re/items.md section 13. Usage: docs/kiln/modding/inventory.md. Ported from shroudlink's `move_stack`,
 * `move_items` and `chest_name` (MIT, bjck/shroudlink); credit in docs/kiln/engines/shroudlink.md. */

/* KilnItemsMoveRequest.kind */
enum {
    KILN_MOVE_STACK = 0,        /* one stack of `from` (the slot `slot`), all of it or `count` items, into `to` */
    KILN_MOVE_QUICK_STACK = 1,  /* the game's quick stack: stacks of `from` that `to` already holds a stack of */
    KILN_MOVE_ALL = 2,          /* the game's move all: every stack of `from` that fits into `to` */
};

/* KilnItemsMoveRequest.flags */
enum {
    KILN_ITEMS_MOVE_DRY_RUN = 0x01u,   /* run and roll back: the result says what would move; no transfer report;
                                          handlers see KILN_TRANSFER_DRY_RUN */
    KILN_ITEMS_MOVE_EQUIPPED = 0x02u,  /* STACK: a slot a player has equipped or holds may move (default: refused) */
    KILN_ITEMS_MOVE_ACTIONBAR = 0x04u, /* STACK: may take from and fill a player's action-bar pages (default: refused
                                          as a source, skipped as a destination); MOVE_ALL: takes the action bar along
                                          (default: it stays) */
    KILN_ITEMS_MOVE_CHECKED = 0x10u,   /* STACK: requires KilnItemsCheckedMoveRequest and exact slot witnesses */
    KILN_ITEMS_MOVE_SILENT = 0x08u,    /* QUICK_STACK / MOVE_ALL: no transfer rules or events (the caller is a handler) */
};

typedef struct KilnItemsMoveRequest {
    uint32_t size;       /* sizeof(KilnItemsMoveRequest) */
    uint32_t flags;      /* KILN_ITEMS_MOVE_*; other bits are refused */
    uint32_t kind;       /* KILN_MOVE_* */
    KilnEntity from;     /* the inventory the items leave: any entity with an InventorySetup */
    KilnEntity to;       /* the inventory they enter; not the same entity as `from` */
    uint32_t count;      /* STACK: items to move, 0 = the whole stack; must not exceed the stack. Otherwise 0 */
    KilnSlotId slot;     /* STACK: the slot of `from` (KILN_SLOT_ID(inventory, index)) */
    KilnSlotId to_slot;  /* STACK: a slot of `to` to fill first (0 = Kiln's choice: partial stacks of the item,
                            then the first empty slot). Into a player or an NPC (issue #109) the choice uses only the
                            Generic pages (the backpack, the worn backpack's too), never equipment, currency or
                            customization pages nor the action bar without KILN_ITEMS_MOVE_ACTIONBAR; a to_slot the
                            item may not enter (an empty equipment slot: a move is no equip, use equip; an equipment
                            slot of another type or one no SlotSelection entry shows, the backpack slot, a currency page
                            for a non-currency, not a slot of `to`) is KILN_ITEMS_REFUSED; a worn equipment slot of the
                            item's type tops up the worn stack; one it may enter that is full gives way to the choice. A stack that
                            arrived anywhere else, or items lost on the way, roll the move back as REFUSED. */
} KilnItemsMoveRequest;

/* Read from a slot of its holder. Plain items have pide/stamp zero. For a named destination only, all zeros
 * means it must still be empty. Item entity stamps are those returned in the inventory slot by kiln.items v4. */
typedef struct KilnItemsSlotWitness {
    KilnItemId item;
    uint32_t count;
    KilnEntity pide;
    uint32_t stamp;
} KilnItemsSlotWitness;

/* The original 40-byte request remains valid. Setting CHECKED adds this tail and requires KILN_MOVE_STACK.
 * A specified destination must still match and receive the entire requested count in that exact slot. */
typedef struct KilnItemsCheckedMoveRequest {
    KilnItemsMoveRequest base;
    KilnItemsSlotWitness source;
    KilnItemsSlotWitness destination;
} KilnItemsCheckedMoveRequest;

/* KilnItemsStatus2.reserved bits. Older runtimes report zero and refuse the checked request flag. */
#define KILN_ITEMS_HAS_CHECKED_MOVE 0x01u

/* One piece of the ledger: `amount` of `item` left `from_slot` and arrived in `to_slot`. Gear (a stack with an item
 * entity) moves whole: `pide` is the item entity that moved with it (the same entity before and after). */
typedef struct KilnItemsMoveChange {
    KilnSlotId from_slot;
    KilnSlotId to_slot;
    KilnItemId item;
    uint32_t amount;
    KilnEntity pide;
    uint32_t reserved;  /* v4: the stamp of `pide` (see "item instances"), captured at the commit; 0 = no pide, an
                           older service, or items.instances off */
} KilnItemsMoveChange;

#define KILN_ITEMS_MAX_MOVE_CHANGES 16u

typedef struct KilnItemsMoveResult {
    uint32_t size;          /* sizeof(KilnItemsMoveResult) of kiln.items */
    int32_t status;         /* KILN_ITEMS_DONE (all moved), PARTIAL (only `done` of `requested` fit / were allowed),
                               NO_ROOM (nothing moved: no room, or nothing to move), NOT_ENOUGH (STACK: `count` is more
                               than the stack holds), NO_INVENTORY (an entity has no initialized InventorySetup),
                               SLOT_CHANGED (the slot is not one of `from`'s, or holds nothing), REFUSED (the game
                               refused; an equipped slot without KILN_ITEMS_MOVE_EQUIPPED; a transfer rule or handler
                               canceled it: `canceled`), UNAVAILABLE, DROPPED */
    uint64_t ticket;
    KilnEntity from;
    KilnEntity to;
    uint32_t kind;
    uint32_t flags;         /* the request's flags */
    uint32_t requested;     /* STACK: the count asked (the stack's size when 0); otherwise 0 */
    uint32_t done;          /* items moved (dry run: would be) */
    uint32_t stacks;        /* source stacks that gave something */
    uint32_t game_error;    /* the game's error code, 0 = none */
    uint32_t change_count;  /* entries in changes (the first KILN_ITEMS_MAX_MOVE_CHANGES; `done` counts them all) */
    uint32_t canceled;      /* 1: a transfer rule or handler canceled the game's quick stack / move all */
    uint32_t steps;         /* sim steps between the request and the move */
    uint32_t reserved;
    KilnItemsMoveChange changes[KILN_ITEMS_MAX_MOVE_CHANGES];
    char message[160];      /* why it failed or was partial, in words; "" when DONE. NUL-terminated */
} KilnItemsMoveResult;

typedef void(KILN_CALL* KilnItemsMoveDoneFn)(const KilnItemsMoveResult* result, void* user);

/* ------------------------------------------------------------------------------ transfer events and rules ---- */

/* The game's quick stack (a player's R key, a Quick Stack Station, once per chest) and move all run through two
 * functions. kiln.items hooks both, chained with any other hook on them (shroudlink's among them: either order, and
 * Kiln never removes its hook), and only once something below is registered: until then nothing is hooked. With
 * nothing registered a hook costs nothing; with one thing it reads one flag per call.
 *
 * Three ways in, from cheapest to most flexible:
 *  - RULES (transfer_rule): data the hook reads on the game's thread. KEEP_SLOT / KEEP_ITEM leave stacks of the source
 *    inventory out of the game's transfer, DENY cancels transfers into or out of a container. No code of yours runs.
 *  - REPORTS (transfer_report): after the game's transfer, a small record of what it did, on the tick thread in Pre.
 *  - HANDLERS (transfer_listen): your function runs inside the game's call, before its transfer, on the game's
 *    worker thread inside the sim step (the thread the game's inventory program runs on). It may read inventories,
 *    keep slots, move stacks itself and cancel; see KilnItemsTransferHost. Native code only: managed mods cannot run
 *    there (Kiln.Items offers rules and reports). Keep it short; it must not block or call the tick-thread-only
 *    functions of kiln.items.
 * Keep/cancel semantics are shroudlink's (`keep`, `on_transfer`): kept stacks are hidden from the game's transfer while
 * it runs (their count reads 0, then is put back), the handler's own moves happen first and are added to the count the
 * game reports, and a cancel skips the game's transfer entirely (the caller sees "ok, moved what the handlers moved"). */

/* KilnItemsTransferEvent.kind, KilnItemsTransferRule.kinds (a mask) */
enum {
    KILN_TRANSFER_QUICK_STACK = 1u,
    KILN_TRANSFER_MOVE_ALL = 2u,
    KILN_TRANSFER_ANY = 3u,
};

/* KilnItemsTransferEvent.flags, KilnItemsTransferReport.flags */
enum {
    KILN_TRANSFER_BY_MOD = 0x1u,    /* a kiln.items move (KILN_MOVE_QUICK_STACK / MOVE_ALL), not the game's own caller */
    KILN_TRANSFER_CANCELED = 0x2u,  /* reports: a rule or handler canceled it */
    KILN_TRANSFER_DRY_RUN = 0x4u,   /* events (dev.21, appended): a KILN_ITEMS_MOVE_DRY_RUN move; everything it does is rolled
                                     * back after, so a handler must not count it or act outside the transfer. Dry runs
                                     * send no report. Older services never set it. */
};

typedef struct KilnItemsTransferCtx KilnItemsTransferCtx; /* opaque; valid only during the handler call */

typedef struct KilnItemsTransferEvent {
    uint32_t size;
    uint32_t kind;         /* KILN_TRANSFER_QUICK_STACK or MOVE_ALL (one bit) */
    KilnEntity from;       /* the inventory the items leave */
    KilnEntity to;         /* the inventory they enter */
    KilnEntity player;     /* whichever of from / to is a player's inventory, else 0 */
    uint32_t flags;        /* KILN_TRANSFER_BY_MOD, KILN_TRANSFER_DRY_RUN */
    KilnItemsTransferCtx* ctx;
} KilnItemsTransferEvent;

/* What a handler may call. Valid during the handler call only, on its thread. */
typedef struct KilnItemsTransferHost {
    uint32_t size;
    /* Up to `capacity` slots of any entity's inventories (empty ones included), like KilnItemsApi.read. Returns the
     * count written. */
    uint32_t(KILN_CALL* read)(KilnItemsTransferCtx* ctx, KilnEntity entity, KilnItemsSlot* out, uint32_t capacity);
    /* The game's transfer leaves this stack of the source inventory alone. Slots outside the source inventory
     * pages (including destination slots) are ignored. */
    void(KILN_CALL* keep)(KilnItemsTransferCtx* ctx, KilnSlotId slot);
    /* The game's transfer does not run. */
    void(KILN_CALL* cancel)(KilnItemsTransferCtx* ctx);
    /* Moves `count` items (0 = all) of the stack in `slot` of the event's source inventory into `to`, keeping item
     * state, through the game's slot transfer in the game's own transaction. Never takes a player's equipped or held
     * items, never fills a player's action bar. Returns the items moved. */
    uint32_t(KILN_CALL* move)(KilnItemsTransferCtx* ctx, KilnSlotId slot, KilnEntity to, uint32_t count);
    /* The custom name of a container (the Rename button), UTF-8, NUL not counted. Returns its length; 0 = none. */
    uint32_t(KILN_CALL* name)(KilnItemsTransferCtx* ctx, KilnEntity entity, char* buf, uint32_t capacity);
} KilnItemsTransferHost;

typedef void(KILN_CALL* KilnItemsTransferFn)(const KilnItemsTransferEvent* event, const KilnItemsTransferHost* host,
                                             void* user);

/* KilnItemsTransferRule.type */
enum {
    KILN_TRANSFER_KEEP_SLOT = 1, /* the stack in `slot` stays where it is while it is in the source inventory */
    KILN_TRANSFER_KEEP_ITEM = 2, /* stacks of `item` stay in the source inventory; `entity` 0 = any source, else only
                                    when that entity's inventory is the source */
    KILN_TRANSFER_DENY = 3,      /* a transfer into or out of `entity` does not run */
};

typedef struct KilnItemsTransferRule {
    uint32_t size;      /* sizeof(KilnItemsTransferRule) */
    uint32_t type;      /* KILN_TRANSFER_* above */
    uint32_t kinds;     /* mask of KILN_TRANSFER_* kinds it applies to (0 = both) */
    KilnEntity entity;  /* see type */
    KilnItemId item;    /* KEEP_ITEM */
    uint32_t reserved;  /* 0 */
    KilnSlotId slot;    /* KEEP_SLOT: KILN_SLOT_ID(inventory entity, index) */
} KilnItemsTransferRule;

typedef struct KilnItemsTransferReport {
    uint32_t size;
    uint32_t kind;       /* KILN_TRANSFER_QUICK_STACK or MOVE_ALL */
    KilnEntity from;
    KilnEntity to;
    KilnEntity player;   /* as in the event */
    uint32_t flags;      /* KILN_TRANSFER_BY_MOD, KILN_TRANSFER_CANCELED */
    uint32_t moved;      /* items the game's transfer moved, plus what handlers moved */
    uint32_t kept;       /* stacks hidden from the game's transfer */
    uint64_t step;       /* sim step of the transfer */
    uint32_t reserved[2];
} KilnItemsTransferReport;

/* Tick thread, in Pre (a burst is delivered together; at most 256 are kept, older ones dropped). */
typedef void(KILN_CALL* KilnItemsTransferReportFn)(const KilnItemsTransferReport* report, void* user);

/* Status of the v3 parts (KilnItemsApi.status2). */
typedef struct KilnItemsStatus2 {
    uint32_t size;               /* in: sizeof(KilnItemsStatus2) */
    uint8_t moves;               /* 1: move works (feature items.move; the program runs) */
    uint8_t names;               /* 1: container_name works (feature items.names) */
    uint8_t transfer_hooks;      /* 1: the quick-stack / move-all hooks can be installed (feature items.transfer) */
    uint8_t hooks_installed;     /* 1: they are installed (something was registered once) */
    uint32_t rules;              /* active rules */
    uint32_t handlers;           /* active handlers */
    uint32_t reporters;          /* active report listeners */
    uint64_t transfers_seen;     /* calls of the game's quick stack / move all since the hooks went in */
    uint64_t transfers_canceled; /* of them canceled by a rule or handler */
    uint64_t moves_done;         /* move requests that moved something */
    uint32_t names_found;        /* container_name lookups that found a name */
    uint32_t reserved;
    char detail[160];            /* the reason when a part is off; NUL-terminated */
} KilnItemsStatus2;

/* ---------------------------------------------------------------------------------------------- item instances */

/* v4, feature items.instances (KILN_ERR_UNAVAILABLE without it; an unknown build turns it off, no offset is guessed).
 *
 * The stamp. Every item entity (the "pide" of a piece of gear, a tool, a can) has a stamp: a 32-bit sequence number,
 * never 0, new for every item entity this process sees. It travels where the item entity does: KilnItemsSlot.reserved
 * (captured at the read), KilnItemsChange.reserved and KilnItemsMoveChange.reserved (captured at the commit). The pair
 * (entity, stamp) names one item entity; compare both, never the id alone. Why an id can never show the old stamp for
 * a new item entity: in one server world the engine hands out every non-player entity id from one counter that only
 * grows (createEntity with id 0 and the queued creates of game programs, which make the item entities, both take it
 * from the same storage counter), so an id is never given to a second entity while the world runs; the stamps are
 * forgotten when a server world closes or starts, and whenever that counter is seen to go down. A destroyed item
 * entity (a take of kiln.items) keeps its stamp as a tombstone: the id is reported as gone. Evidence:
 * docs/kiln/services/items.md "Item instances". Ids outside the counter's range (1..1023 players, ids with bit 31
 * set) are never stamped.
 *
 * The reads. read_instances reads N item entities in one call: the stamp, where each sits (its holder, slot, link and
 * the flat slot the game's save record uses) and the fields the game writes into its save record, read the way the
 * game's record builder reads them. read_holder does the same for every item entity in one holder's inventories.
 * Reads only: nothing here writes a game component. Tick thread (Pre / Post). */

/* KilnItemsInstance.flags */
enum {
    KILN_ITEMS_INSTANCE_FOUND = 0x0001u,      /* the entity is an item entity now (it has ItemState), with this stamp */
    KILN_ITEMS_INSTANCE_PLACED = 0x0002u,     /* the slot of its ItemState holds it, and that inventory's holder is
                                                 known: holder, link, category, flat_slot and count are set */
    KILN_ITEMS_INSTANCE_SAVED_SLOT = 0x0004u, /* flat_slot is the slot number the game's world / character save record
                                                 uses: a top-level Generic link */
    KILN_ITEMS_INSTANCE_EQUIPPED = 0x0008u,   /* ItemState IsEquipped */
    KILN_ITEMS_INSTANCE_LEVEL = 0x0010u,      /* the record has a level (the item entity has a Level with values) */
    KILN_ITEMS_INSTANCE_DURABILITY = 0x0020u, /* ... durability current / max */
    KILN_ITEMS_INSTANCE_FUEL = 0x0040u,       /* ... fuel current / max */
    KILN_ITEMS_INSTANCE_DYE = 0x0080u,        /* ... a dye (ColorPaletteItem) */
    KILN_ITEMS_INSTANCE_PERKS = 0x0100u,      /* ... perks available / unlocked (PerkContainerNew, or PerkContainer) */
    KILN_ITEMS_INSTANCE_GEM = 0x0200u,        /* ... a gem slot (WeaponGemSlot; gem 0 = empty) */
    KILN_ITEMS_INSTANCE_GONE = 0x0400u,       /* not FOUND because kiln.items saw it destroyed (a take's commit) */
};

typedef struct KilnItemsInstance {
    KilnEntity entity;        /* in: the item entity */
    uint32_t stamp;           /* FOUND: its stamp (never 0) */
    uint32_t flags;           /* KILN_ITEMS_INSTANCE_* */
    KilnItemId item;          /* FOUND: ItemState.itemId */
    KilnSlotId slot;          /* FOUND: ItemState.containedInSlotId (KILN_SLOT_ID(inventory, index)) */
    KilnEntity holder;        /* PLACED: the entity whose InventorySetup links the slot's inventory (a chest's first link
                                 is the chest itself) */
    uint32_t flat_slot;       /* PLACED: with SAVED_SLOT the save record's slot: (Generic links before this one) x 8 +
                                 index, as the game's world and character savers number them; otherwise 256 + (the
                                 inventory's position in the holder's link tree) x 8 + index, unique in the holder but
                                 not a number the game saves (equipment, currency, customization, nested links) */
    uint8_t link;             /* PLACED: the InventorySetup link (0..15; Virtual links: the outer index) */
    uint8_t category;         /* PLACED: that link's InventoryCategory (1 Customization .. 4 Generic) */
    uint16_t state;           /* FOUND: ItemState.itemState bits */
    uint32_t count;           /* PLACED: the stack's count (an item entity's stack is 1) */
    uint32_t level;           /* LEVEL: the Level attribute's value (the record keeps its low byte) */
    int32_t durability;       /* DURABILITY: current */
    int32_t durability_max;   /* DURABILITY: max */
    int32_t fuel;             /* FUEL: current (units, 64 per charge) */
    int32_t fuel_max;         /* FUEL: max */
    KilnItemId dye;           /* DYE: ColorPaletteItem.paletteItemId */
    uint8_t perks_available;  /* PERKS */
    uint8_t perks_unlocked;   /* PERKS */
    uint8_t gem_level;        /* GEM: the gem's own Level (low byte); 0 when the slot is empty */
    uint8_t reserved0;        /* 0 */
    KilnItemId gem;           /* GEM: the gem's item id; 0 = an empty gem slot */
    uint32_t reserved[4];     /* 0 */
} KilnItemsInstance;

/* read_instances flags */
enum {
    KILN_ITEMS_READ_LOCATION = 0x01u, /* stamp, item, slot, state and placement only: no record fields (cheaper) */
};

/* KilnItemsInstancesStatus.flags */
enum {
    KILN_ITEMS_INSTANCES_ON = 0x01u,        /* read_instances, read_holder and the stamps work */
    KILN_ITEMS_INSTANCES_EXHAUSTED = 0x02u, /* 2^32 - 1 stamps were handed out: no new one until a restart */
};

typedef struct KilnItemsInstancesStatus {
    uint32_t size;            /* in: sizeof(KilnItemsInstancesStatus) */
    uint32_t flags;           /* KILN_ITEMS_INSTANCES_* */
    uint64_t stamps_issued;   /* stamps handed out since the process started */
    uint64_t reads;           /* item entities read by read_instances and read_holder */
    uint32_t table_size;      /* item entities with a stamp (or a tombstone) now */
    uint32_t resets;          /* times every stamp was forgotten (a world closed or started, the id counter went down) */
    uint32_t next_entity_id;  /* the engine's entity id counter at the last Pre */
    uint32_t mismatches;      /* reads where a stamped id held another item id (it got a new stamp) */
    uint32_t holder_rebuilds; /* times the inventory -> holder map was built */
    uint32_t reserved;
    char detail[160];         /* why it is off; NUL-terminated */
} KilnItemsInstancesStatus;

/* ---------------------------------------------------------------------------------------------- equipment ---- */

/* since 0.2.0-dev.27 (v5; feature items.equip; check header.size before calling `equip` or `equip_status`).
 *
 * Puts an item a player has on (into one of the player's equipment slots) or takes one off, through the game's own
 * equip and unequip: the two functions its equipment_actions program runs for a player's changeEquipmentAction, with
 * all their checks (the item's EquipmentSlotType must be the slot's, a backpack swap moves the old backpack's contents,
 * an unequip needs a free Generic slot). They take the player from the running program's current entity, so they run
 * inside kiln.items' second program "kiln_items_equip": an entity program over the player characters (PlayerInput),
 * registered through kiln.world (without kiln.world equip is unavailable). Each request is one transaction of its own,
 * set up as equipment_actions sets up its own: the game's error rolls the whole of it back, so a refused equip leaves
 * the inventory as it was.
 *
 * Order in a step: the game's equipment_actions runs a player's own equip first, kiln_items_equip later in the same
 * step, on what that left. Both happen, in that order; a request whose slot the player's own action emptied comes back
 * KILN_ITEMS_SLOT_CHANGED. The player's input (PlayerInput, ServerConsumedPlayerInput) is never touched, so a client's
 * next action is never skipped or replayed.
 *
 * Slots are the game's keen::EquipmentSlot values that SlotSelection.equipment holds: 0-16 (Vanity_Head .. FishingBait),
 * 21 HookShot, 22 Glider, 23 Backpack. 17-20 (the building materials) are not equipment slots and are refused. */

#define KILN_ITEMS_EQUIP_ANY 0xFFu /* KilnItemsEquipRequest.equipment_slot of an equip: the slot the item names itself */

/* KilnItemsEquipRequest.flags */
enum {
    KILN_ITEMS_EQUIP_UNEQUIP = 0x01u, /* take off what `equipment_slot` holds (`slot` and `pide` must be 0) */
    KILN_ITEMS_EQUIP_DRY_RUN = 0x02u, /* run the game's call and roll it back: the result says what would happen */
};

typedef struct KilnItemsEquipRequest {
    uint32_t size;            /* sizeof(KilnItemsEquipRequest) */
    uint32_t flags;           /* KILN_ITEMS_EQUIP_* above; other bits are refused */
    KilnEntity player;        /* a player character: an entity with PlayerInput */
    uint8_t equipment_slot;   /* keen::EquipmentSlot (see above); KILN_ITEMS_EQUIP_ANY for an equip */
    uint8_t reserved[3];      /* 0 */
    KilnSlotId slot;          /* equip: the slot of the player's inventories that holds the item; unequip: 0 */
    KilnEntity pide;          /* equip: the item entity that slot must hold (0 = whatever it holds); unequip: 0 */
    uint32_t reserved2;       /* 0 */
} KilnItemsEquipRequest;

typedef struct KilnItemsEquipResult {
    uint32_t size;            /* sizeof(KilnItemsEquipResult) of kiln.items */
    int32_t status;           /* KILN_ITEMS_DONE; SLOT_CHANGED (equip: the slot is not one of the player's, is empty or
                                 holds another item entity; unequip: nothing is equipped there); NO_INVENTORY (no player
                                 character with that id in the world); NO_ROOM (unequip: no free Generic slot);
                                 REFUSED (the game refused: game_error, message); UNAVAILABLE; DROPPED. Only DONE
                                 changed anything (a dry run: would have) */
    uint64_t ticket;          /* what equip returned */
    KilnEntity player;
    uint32_t flags;           /* the request's flags */
    uint8_t equipment_slot;   /* the slot asked for; for KILN_ITEMS_EQUIP_ANY the slot the item went to (0xFF unknown) */
    uint8_t game_error;       /* the game's error code (0x11 refused, 0x15 no item, 0x1f not a player / no room); 0 none */
    uint16_t reserved0;
    uint32_t steps;           /* sim steps between submit and run */
    KilnItemId item;          /* the item put on or taken off */
    KilnEntity pide;          /* its item entity (0: a plain stack, arrows or bait) */
    uint32_t stamp;           /* the stamp of pide (v4 item instances; 0 when off or no pide) */
    KilnItemId replaced;      /* equip: what the equipment slot held before (0 = it was empty) */
    KilnSlotId from;          /* where the item was */
    KilnSlotId to;            /* where it is now (DONE; a dry run: where it would be) */
    KilnEntity replaced_pide; /* equip: the item entity of `replaced` */
    uint32_t replaced_stamp;  /* its stamp */
    KilnSlotId replaced_to;   /* equip: where `replaced` went (0 = not found) */
    uint32_t reserved[4];
    char message[160];        /* why, for anything but DONE; NUL-terminated */
} KilnItemsEquipResult;

/* Since 0.2.0-dev.30.6: creates one stack straight into equipment, then runs the game's equipment-selection path in the same
 * transaction. The old stack goes to an empty Generic slot (default), or is taken and reported (TAKE). Every refusal
 * and DRY_RUN rolls back everything. Count must fit one stack; gear count must be 1. Backpack equipment slot 23 is
 * refused: its inventory does not exist until commit. flags in the result use these bits; reserved[0] at +80 reports
 * replaced_count (0 for an ordinary equip). from is 0: there was no intermediate inventory placement. */
#define KILN_ITEMS_GIVE_EQUIP_DRY_RUN 0x02u
#define KILN_ITEMS_GIVE_EQUIP_TAKE 0x04u
typedef struct KilnItemsGiveEquipRequest {
    uint32_t size, player, item, count;
    uint32_t flags;
    uint8_t equipment_slot;
    uint8_t reserved[3];
} KilnItemsGiveEquipRequest;

/* Tick thread, Pre of the step after the one the request ran in. */
typedef void(KILN_CALL* KilnItemsEquipDoneFn)(const KilnItemsEquipResult* result, void* user);

typedef struct KilnItemsEquipStatus {
    uint32_t size;            /* in: sizeof(KilnItemsEquipStatus) */
    uint8_t available;        /* 1: equip runs now (feature items.equip, kiln.world, the program verified or waiting
                                 for its first player) */
    uint8_t program;          /* KILN_ITEMS_PROGRAM_* of kiln_items_equip */
    uint16_t reserved;
    uint32_t pending;         /* requests waiting */
    uint32_t reserved2;
    uint64_t done;            /* requests that changed equipment (dry runs not counted) */
    uint64_t refused;         /* requests that came back with anything but DONE */
    char detail[160];         /* why it is off; NUL-terminated */
} KilnItemsEquipStatus;

/* ---------------------------------------------------------------------------------------------- equipment sets */

/* since 0.2.0-dev.29 (v6; feature items.equip_sets; check header.size before calling a v6 member).
 *
 * A player's 20 physical equipment slots (the ones SlotSelection.equipment holds: keen::EquipmentSlot 0-16, 21-23)
 * changed in ONE inventory transaction inside kiln_items_equip: every slot changes or none does. Chapter 10 of the
 * dev.28 dossier is the contract; docs/kiln/services/items.md "Equipment sets" the guide.
 *
 * Snapshots. set_capture copies a canonical snapshot of the player's inventory tree, equipment selection and item
 * entities (normalised bytes, compared byte for byte; never a revision counter or a hash) and stores it under a token:
 * nonzero, only growing within the process, never handed out twice. At most 256 snapshots live in all and 16 per owner;
 * a full store refuses (KILN_ERR_BUFFER_TOO_SMALL, refusal BUDGET), it never evicts another. A snapshot can be used
 * through captured_step + 250; the server world's stop, a new world and a reset of the item stamps (the engine's id
 * counter went down) forget every one. A snapshot belongs to the owner that captured it. */

/* KilnSetSnapshotV1.refusal (and later results' refusal codes) */
#define KILN_SET_REFUSAL_NONE 0u
#define KILN_SET_REFUSAL_NO_PLAYER 1u   /* no player character with an initialised inventory and equipment */
#define KILN_SET_REFUSAL_NO_IDENTITY 2u /* kiln.decisions has no identity for the player (KilnEventEntityKey) */
#define KILN_SET_REFUSAL_SHAPE 3u       /* the inventory tree is beyond the bounds (64 setups, depth 3, 32 inventories), links
                                           back into itself or names an entity that cannot be read */
#define KILN_SET_REFUSAL_BUDGET 4u      /* 256 snapshots live, or 16 of this owner */
#define KILN_SET_REFUSAL_STALE 5u       /* the snapshot is unknown, used, expired, another owner's or another world's, or from
                                           before a reset of the item stamps */
#define KILN_SET_REFUSAL_NOT_HERE 6u    /* a selected item or stack is not where the set says, or not the player's */
#define KILN_SET_REFUSAL_SLOT_TYPE 7u   /* an item does not fit its equipment slot */
#define KILN_SET_REFUSAL_NO_ROOM 8u     /* an unequip needs a free Generic slot and there is none */
#define KILN_SET_REFUSAL_CYCLE 9u       /* items move between equipment slots in a cycle: refused until the rollback matrix
                                           passes (UnsupportedCycle) */
#define KILN_SET_REFUSAL_BACKPACK 10u   /* the backpack would change (its contents move with it): refused until the rollback
                                           matrix passes */
#define KILN_SET_REFUSAL_LOCKED 11u     /* an item is worn in a slot the set leaves as it is */
#define KILN_SET_REFUSAL_MERGE 12u      /* a stack of the item type its slot already holds (the game could merge them) */
#define KILN_SET_REFUSAL_TOO_MANY_STEPS 13u /* more than 128 steps */
#define KILN_SET_REFUSAL_SWAPS 14u      /* more than one slot that holds an item gets another one, or a slot is emptied after
                                           such a swap: the game's commit would point a slot at the item that came off
                                           (UnsupportedSwaps; the rollback matrix, docs/kiln/services/items.md) */

typedef struct KilnSetSnapshotV1 {
    uint32_t size;               /* in: sizeof the caller's struct (at least the chapter's 88 bytes, up to `reason`) */
    uint32_t player;
    uint64_t token;              /* 0 when refused */
    uint64_t world_epoch;        /* kiln.window_clock's epoch of the server world */
    uint64_t captured_step;      /* the step it was taken in; usable through captured_step + 250 */
    uint8_t process_session[16]; /* kiln.window_clock's process nonce */
    KilnEventEntityKey identity; /* the player's identity (kiln.decisions resolve_entity) */
    /* appended (written only when `size` covers them): why a capture was refused */
    uint32_t refusal;            /* KILN_SET_REFUSAL_* */
    uint32_t reserved;
    char reason[160];            /* NUL-terminated; "" when captured */
} KilnSetSnapshotV1;

/* Planning (set_plan). The planner works on a snapshot (it does not take it): the game's equip and unequip calls in the
 * order one transaction would run them, or a refusal before anything is touched. Advisory for a caller: equip_set plans
 * again from its own snapshot and trusts no step a caller sends. Order: targets whose item is in a non-equipment slot,
 * in numeric slot order (equip: a swap, the slot's old stack goes to the source slot); then targets to be emptied
 * (unequip: into the first free slot of a top-level Generic link); a cycle (an item worn in one named slot and asked
 * for in another) is refused (CYCLE). Each source is found again by its item entity after the steps before it. */

/* KilnSetPlanV1.status */
#define KILN_SET_PLAN_PLANNED 0u
#define KILN_SET_PLAN_NO_CHANGE 1u /* every entry holds already (or no entries): no step */
#define KILN_SET_PLAN_STALE 4u     /* the snapshot cannot be used (refusal STALE) */
#define KILN_SET_PLAN_REFUSED 5u   /* refusal and failed_entry say why */

#define KILN_SET_STEP_EQUIP 1u
#define KILN_SET_STEP_UNEQUIP 2u

typedef struct KilnSetStepV1 {
    uint32_t index;
    uint8_t op;             /* KILN_SET_STEP_* */
    uint8_t equipment_slot; /* keen::EquipmentSlot */
    uint8_t entry;          /* the request entry it serves; 0xFF: a temporary unequip that frees a slot for a cycle */
    uint8_t kind;           /* what goes in: 0 nothing (an unequip), 1 an item entity, 2 a plain stack */
    uint32_t item_id, item_entity;
    uint64_t instance_stamp;
    uint64_t source_slot;   /* an equip: where the stack is when the step runs (as planned) */
    uint32_t count, reserved;
} KilnSetStepV1;

#define KILN_SET_MAX_STEPS 128u

typedef struct KilnSetPlanV1 {
    uint32_t size;                 /* in: sizeof(KilnSetPlanV1) */
    uint32_t status;               /* KILN_SET_PLAN_* */
    uint32_t refusal;              /* KILN_SET_REFUSAL_* */
    uint32_t failed_entry;         /* the entry the refusal is about; UINT32_MAX for none */
    uint32_t step_count;
    uint32_t required_free_slots;  /* the most free Generic slots the steps use at one time */
    uint64_t valid_through_step;   /* the snapshot's captured_step + 250 */
    char reason[256];              /* NUL-terminated; "" when planned */
    KilnSetStepV1 steps[KILN_SET_MAX_STEPS];
} KilnSetPlanV1;

/* Applying (equip_set). One request is one inventory transaction inside kiln_items_equip, with one decision: the
 * service files a kiln.decisions kind-18 request (KilnSetDecisionRequestV1) in the Pre after the call, so mods see one
 * PlayerEquippingEvent for the whole set (cancel: Canceled, nothing runs); without a decision consumer, a full queue, a
 * timeout or a released capability the set goes on (kiln.decisions fails open). In the Pre of the step it runs in, the
 * player's session and identity are checked again; inside the program the snapshot's bytes are compared before the
 * transaction (a handler or the game that changed the inventory: StaleSnapshot), the plan is made again from it, every
 * step is staged on one transaction, and the terminal state is checked against the request and the snapshot
 * (chapter 10 §4.4). FIFO with the player's single equip requests; at most one set per player and step.
 *
 * Completion: `done` is called exactly once, on the tick thread in Pre (or when the server world stops), with a result
 * and its changes that are valid only during the call. Never for a call that did not return KILN_OK. Owners: the
 * snapshot must be the effective origin's: a call by owner 0 (Kiln's managed bridge) names the asking mod in
 * origin_owner; any other caller may name 0 (itself) or itself, never another owner. cancel(ticket) drops the callback
 * (v1 semantics), cancel_owner(owner) drops an owner's queued sets and their callbacks, set_cancel(ticket) ends a queued
 * set Dropped and keeps its callback. A set that did not run within 250 steps ends Dropped. */

#define KILN_SET_REQUEST_DRY_RUN 1u /* KilnSetRequestV1.flags: run and roll back, never commit */
/* Lab only: accepted when <kiln data>/kiln.items/sets-lab existed when the server started, refused as bad flags
 * otherwise (never on a player's server). Faults injected into a real run, for the rollback matrix of the lab test. */
#define KILN_SET_REQUEST_LAB_FAIL_STEP 0x100u      /* the step in bits 16-23: its equip is called with an empty source */
#define KILN_SET_REQUEST_LAB_GUARD_BIT 0x200u      /* the commit's own rollback bit is set: refused before the commit */
#define KILN_SET_REQUEST_LAB_COMMIT_ANYWAY 0x400u  /* the bit set and committed anyway: the commit rolls back by itself */
#define KILN_SET_REQUEST_LAB_FAKE_MISMATCH 0x800u  /* the result read as unmet after the commit: InvariantFailure */

typedef struct KilnSetRequestV1 {
    uint32_t size;                /* sizeof(KilnSetRequestV1) */
    uint32_t player;
    uint32_t flags;               /* KILN_SET_REQUEST_*; other bits are refused */
    uint32_t count;               /* 0..20 entries; slots not named stay as they are */
    const KilnSetOpV1* entries;   /* copied by equip_set */
    uint64_t snapshot_token;      /* set_capture's token; taken (used up) by an accepted call */
    KilnOwner origin_owner;       /* the snapshot's owner: see "Applying" */
    uint8_t process_session[16];  /* must be this process's nonce (checked before the token is looked up) */
} KilnSetRequestV1;

typedef struct KilnSetSlotValueV1 {
    uint32_t item_id, item_entity, count, reserved;
    uint64_t instance_stamp;      /* the item entity's stamp, zero-extended; 0 for a plain stack or an empty slot */
    uint64_t inventory_slot;      /* the slot that holds it (an equipment page's); 0 when empty */
} KilnSetSlotValueV1;

typedef struct KilnSetChangeV1 {
    uint32_t size, equipment_slot; /* keen::EquipmentSlot */
    KilnSetSlotValueV1 before, after;
} KilnSetChangeV1;

/* KilnSetResultV1.status */
#define KILN_SET_DONE 0u
#define KILN_SET_NO_CHANGE 1u
#define KILN_SET_SIMULATED 2u
#define KILN_SET_CANCELED 3u
#define KILN_SET_STALE_SNAPSHOT 4u
#define KILN_SET_REFUSED 5u
#define KILN_SET_COMMIT_ROLLED_BACK 6u
#define KILN_SET_DROPPED 7u
#define KILN_SET_INVARIANT_FAILURE 8u
#define KILN_SET_UNAVAILABLE 9u
/* KilnSetResultV1.flags */
#define KILN_SET_RESULT_DRY_RUN 1u
#define KILN_SET_RESULT_COMMITTED 2u
#define KILN_SET_RESULT_MAY_HAVE_MUTATED 4u

typedef struct KilnSetResultV1 {
    uint32_t size, status, game_error;
    uint32_t failed_operation;    /* the step the game refused; UINT32_MAX if none */
    uint64_t ticket, decision_id, world_epoch, applied_step; /* decision_id 0: no decision was filed */
    uint32_t player, flags, change_count;
    uint32_t refusal;             /* KILN_SET_REFUSAL_* of a refusal or a stale snapshot; 0 otherwise */
    char reason[256];             /* NUL-terminated; "" for Done, NoChange, Simulated */
    KilnSetChangeV1 changes[20];  /* the slots a commit changed (also after an invariant failure, when they could be read) */
} KilnSetResultV1;

typedef void(KILN_CALL* KilnSetDoneV1)(const KilnSetResultV1* result, void* user);

typedef struct KilnSetStatusV1 {
    uint32_t size;          /* in: sizeof(KilnSetStatusV1) */
    uint8_t available;      /* 1: sets can run now (feature items.equip_sets, equip, item instances, kiln.decisions'
                               kind 18, kiln.window_clock, this build's component layouts; not switched off) */
    uint8_t broken;         /* 1: switched off until the server, or the hosting player's game, restarts (an invariant failure or a fault) */
    uint16_t reserved;
    uint32_t snapshots;     /* live snapshots */
    uint32_t pending;       /* set requests queued or deciding */
    uint64_t captured;      /* snapshots taken since the start */
    uint64_t done;          /* sets committed */
    uint64_t refused;       /* sets that ended with anything but Done, NoChange, Simulated */
    uint64_t invariant_failures;
    char detail[160];       /* why it is off; NUL-terminated */
} KilnSetStatusV1;

/* ---------------------------------------------------------------------------------------------- gem sockets -- */

/* v7 (the gem wave; feature items.pide_options). The game gives a new weapon an empty gem slot (WeaponGemSlot) by a
 * chance per rarity or when its ItemInfo sets gemSlotOverride; every other kind of gear never gets one, although the
 * rest of the gem system does not care: equipment_impacts runs the gem of the item in any of its 11 impact slots, and
 * the save keeps a socket on any item (docs/kiln/services/gems-sockets.md). kiln.items' createPide hook widens this:
 *  - KILN_ITEMS_GEM_SLOT_ANY on a give (above);
 *  - gemSlotOverride is honoured on every gear item, not only weapons (no game item outside the weapons sets it: a
 *    data pack sets the item field "gemSlotOverride": true on its own ring);
 *  - socket_roll: new gear of a kind rolls an empty gem slot with a chance whenever the game creates it (loot, crafting,
 *    a give). The chance of a kind is the highest any owner asked for. Weapons keep the game's own roll.
 * Nothing changes for an item that already has a gem slot. */

/* kinds (KilnItemsSocketRoll's fields): by ItemInfo category and equipment slot type */
typedef struct KilnItemsSocketRoll {
    uint32_t size;  /* sizeof(KilnItemsSocketRoll) */
    float rings;    /* 0..1: category Equipment, slot type Armour_Ring (37) */
    float armour;   /* Equipment, Armour_Head .. Armour_Feet (13-17) */
    float shields;  /* Equipment, Shield (18) */
    float other;    /* any other gear (tools, backpacks, gliders...) except weapons, gems, dyes and customization */
    uint32_t reserved; /* 0 */
} KilnItemsSocketRoll;

/* ---------------------------------------------------------------------------------------------- gear -------- */

/* v8, since 0.2.0-dev.29.19 (the equipment wave; feature items.gear; check header.size before calling a v8 member).
 *
 * An item entity's rarity, enhancements and level, written in place. The game keeps an instance's rarity as its
 * enhancement count: PerkContainerNew {u8 availablePerks, u8 unlockedPerks} on the item entity, and itemRarityOf is
 * availablePerks - 1 (Common 0 .. Legendary 4) when that is 0..4, else the type's ItemInfo.rarity. The shown rarity,
 * level and damage (ItemState.itemRarityUi, itemLevelUi, baseDamageUi) are derived from it by the game's own
 * item_state_init in the next sim step, and replicated from there. An unlocked enhancement is one level: the Level
 * attribute moves with unlockedPerks, as the game's UnlockPerk and its free +1 / -1 action do. Max durability is
 * computed once by item_durability_init from rarity and level; clearing ItemState's DurabilityInitialized bit makes it
 * run again in the next step, keeping the current / max ratio (docs/re/items.md section 19).
 *
 * gear_write queues one write; it runs in the next sim step inside the serial program kiln_items_gear (registered
 * through kiln.world) as tracked writes (WriteOther, the attribute's formulas), so players and the save get it like the
 * game's own changes. The request names the item entity with its v4 stamp (read_instances, a slot's reserved field):
 * an item entity that is gone, or another item now, ends STALE and nothing changes. Server world only (a dedicated
 * server, or the hosting player's game). */

/* KilnItemsGearRequest.flags */
enum {
    KILN_ITEMS_GEAR_AVAILABLE = 0x0001u,     /* availablePerks := available (1..5; the rarity is available - 1). Lower
                                                than unlockedPerks: unlockedPerks (and Level) come down with it */
    KILN_ITEMS_GEAR_UNLOCKED = 0x0002u,      /* unlockedPerks := unlocked (0..availablePerks after AVAILABLE); Level moves
                                                by the same number (it needs a Level) */
    KILN_ITEMS_GEAR_LEVEL = 0x0004u,         /* Level := level (the shown level, enhancements included); the base level
                                                (level - unlockedPerks) must be in the type's level range */
    KILN_ITEMS_GEAR_FORCE_SLOT = 0x0008u,    /* AVAILABLE beyond the type's last filled perk slot (an empty slot; the
                                                game skips it): without it such a request ends NO_SLOT */
    KILN_ITEMS_GEAR_DURABILITY_REFRESH = 0x0010u, /* clear DurabilityInitialized: the game recomputes max durability at
                                                     the new rarity and level in the next step, keeping the ratio */
    KILN_ITEMS_GEAR_DURABILITY_REPAIR = 0x0020u,  /* current := max, then as REFRESH: full at the new max */
    KILN_ITEMS_GEAR_DRY_RUN = 0x0040u,       /* say what would change (after = the plan), change nothing */
    KILN_ITEMS_GEAR_NOT_EQUIPPED = 0x0080u,  /* refuse (EQUIPPED) when the item is equipped; default: allowed */
    /* package P7 (per-instance values; the same v8 table, 0x0100 stays unused): */
    KILN_ITEMS_GEAR_ANY_LEVEL = 0x0200u,     /* LEVEL's base may lie outside the type's level range (1..255 still). A save
                                                load sets such a level back into the range (docs/re/items.md section 20):
                                                the caller keeps the level it wants and writes it again after a load */
    KILN_ITEMS_GEAR_DURABILITY_MAX = 0x0400u,/* Durability max := durability_max, current kept at the same share of it
                                                (REPAIR: current := the new max). Refused with REFRESH, whose recompute
                                                would replace it; REFUSED while the game has not computed the item's
                                                durability yet (DurabilityInitialized clear). The game computes max again
                                                after a save load (ItemState, where the bit lives, is not saved): the
                                                caller writes it again after a load */
};

#define KILN_ITEMS_GEAR_MAX_DURABILITY 1000000 /* the highest durability_max (package P7) */

/* KilnItemsGearResult.status */
enum {
    KILN_ITEMS_GEAR_DONE = 0,         /* written (or, DRY_RUN, would be); also when nothing needed a change */
    KILN_ITEMS_GEAR_STALE = 1,        /* the entity is not the stamped item entity any more (gone, or another item) */
    KILN_ITEMS_GEAR_NOT_GEAR = 2,     /* no PerkContainerNew (the type has no enhancement slots), or no Level for an
                                         UNLOCKED change or LEVEL, or no Durability for DURABILITY_MAX */
    KILN_ITEMS_GEAR_NO_SLOT = 3,      /* AVAILABLE beyond the type's last filled perk slot (without FORCE_SLOT) */
    KILN_ITEMS_GEAR_OUT_OF_RANGE = 4, /* unlocked above available, a level whose base is outside the type's range
                                         (without ANY_LEVEL) */
    KILN_ITEMS_GEAR_EQUIPPED = 5,     /* NOT_EQUIPPED and the item is equipped */
    KILN_ITEMS_GEAR_UNAVAILABLE = 6,  /* gear writes were switched off since the request was accepted */
    KILN_ITEMS_GEAR_DROPPED = 7,      /* not run: the server world went away or did not step for 10 s */
    KILN_ITEMS_GEAR_REFUSED = 8,      /* a component of the entity is not laid out as expected; nothing changed */
};

/* KilnItemsGearResult.changed / KilnItemsGearState.flags */
enum {
    KILN_ITEMS_GEAR_CHANGED_PERKS = 0x01u,      /* PerkContainerNew */
    KILN_ITEMS_GEAR_CHANGED_LEVEL = 0x02u,      /* Level (current or max) */
    KILN_ITEMS_GEAR_CHANGED_DURABILITY = 0x04u, /* Durability current (REPAIR) */
    KILN_ITEMS_GEAR_CHANGED_STATE = 0x08u,      /* ItemState: DurabilityInitialized cleared */
    KILN_ITEMS_GEAR_CHANGED_DURABILITY_MAX = 0x10u, /* Durability max (DURABILITY_MAX, package P7) */
};
enum {
    KILN_ITEMS_GEAR_HAS_PERKS = 0x01u,      /* the entity has PerkContainerNew */
    KILN_ITEMS_GEAR_HAS_LEVEL = 0x02u,      /* ... a Level with values */
    KILN_ITEMS_GEAR_HAS_DURABILITY = 0x04u, /* ... a Durability with values */
    KILN_ITEMS_GEAR_EQUIPPED_NOW = 0x08u,   /* ItemState IsEquipped */
    KILN_ITEMS_GEAR_DURABILITY_INIT = 0x10u,/* ItemState DurabilityInitialized */
};

typedef struct KilnItemsGearRequest {
    uint32_t size;       /* sizeof(KilnItemsGearRequest) */
    uint32_t flags;      /* KILN_ITEMS_GEAR_*: at least one of AVAILABLE, UNLOCKED, LEVEL, DURABILITY_REFRESH,
                            DURABILITY_REPAIR; other bits are refused */
    KilnEntity entity;   /* the item entity */
    uint32_t stamp;      /* its v4 stamp (required) */
    KilnItemId item;     /* the item it must hold (0 = whatever the stamp names) */
    uint8_t available;   /* AVAILABLE: 1..5 */
    uint8_t unlocked;    /* UNLOCKED: 0..5 */
    uint16_t reserved0;  /* 0 */
    uint32_t level;      /* LEVEL: 1..255 (the save record keeps the low byte) */
    int32_t durability_max; /* DURABILITY_MAX: 1..KILN_ITEMS_GEAR_MAX_DURABILITY; else 0 (package P7: was reserved) */
    uint32_t reserved[2];/* 0 */
} KilnItemsGearRequest;

/* One item entity's gear as the program (or gear_read) sees it. */
typedef struct KilnItemsGearState {
    uint32_t flags;           /* KILN_ITEMS_GEAR_HAS_* */
    KilnItemId item;
    uint8_t available;        /* HAS_PERKS: availablePerks */
    uint8_t unlocked;         /* HAS_PERKS: unlockedPerks */
    uint8_t rarity;           /* itemRarityOf: available - 1 when that is 0..4, else type_rarity */
    uint8_t type_rarity;      /* ItemInfo.rarity */
    uint8_t max_available;    /* the type's last filled perk slot + 1 (0: none filled): the highest AVAILABLE without
                                 FORCE_SLOT */
    uint8_t rarity_ui;        /* ItemState.itemRarityUi: the rarity the game derived (and players are sent); it follows a
                                 write in the next sim step */
    uint8_t level_ui;         /* ItemState.itemLevelUi, likewise */
    uint8_t reserved0;        /* 0 */
    uint32_t level;           /* HAS_LEVEL: Level current */
    uint32_t level_max;       /* HAS_LEVEL: Level max */
    uint32_t type_level_min;  /* ItemInfo.itemLevelRange */
    uint32_t type_level_max;
    int32_t durability;       /* HAS_DURABILITY: current */
    int32_t durability_max;   /* HAS_DURABILITY: max */
    uint16_t state;           /* ItemState.itemState bits */
    uint16_t reserved1;       /* 0 */
} KilnItemsGearState;

typedef struct KilnItemsGearResult {
    uint32_t size;            /* sizeof(KilnItemsGearResult) of kiln.items */
    int32_t status;           /* KILN_ITEMS_GEAR_DONE ... */
    uint64_t ticket;
    KilnEntity entity;
    uint32_t stamp;
    uint32_t flags;           /* the request's */
    uint32_t changed;         /* KILN_ITEMS_GEAR_CHANGED_* (DRY_RUN: would change) */
    uint32_t steps;           /* sim steps between the request and the write */
    uint32_t reserved;        /* 0 */
    uint64_t step;            /* the sim step it ran in (0: it never ran) */
    KilnItemsGearState before;
    KilnItemsGearState after; /* read back after the writes (DRY_RUN: the plan). rarity is the derived one: the game's
                                 itemRarityUi follows in the next step */
    char message[160];        /* "" when DONE; NUL-terminated */
} KilnItemsGearResult;

typedef void(KILN_CALL* KilnItemsGearDoneFn)(const KilnItemsGearResult* result, void* user);

typedef struct KilnItemsGearStatus {
    uint32_t size;            /* in: sizeof(KilnItemsGearStatus) */
    uint8_t available;        /* 1: gear_write can run (feature items.gear, kiln.world, the layouts, the program) */
    uint8_t program;          /* KILN_ITEMS_PROGRAM_* of kiln_items_gear */
    uint8_t replicated;       /* 1: the world replicates ItemState, where the game puts the rarity, level and damage it
                                 derives from the written state (checked when the program first runs) */
    uint8_t saved;            /* 1: the world saves PerkContainerNew and Level */
    uint32_t pending;         /* requests waiting for the program */
    uint64_t writes;          /* item entities changed since the start */
    uint64_t refused;         /* requests that ended with anything but DONE */
    char detail[160];         /* why it is off; NUL-terminated */
} KilnItemsGearStatus;

/* ---------------------------------------------------------------------------------------------- upgrades ---- */

/* v8, since 0.2.0-dev.29.19 (the equipment wave, package P3; feature items.gear and the inventory transaction; check
 * header.size).
 *
 * A gear write paid for: gear_upgrade takes the costs from the payers and makes the gear write of `gear` in one step
 * of kiln_items_gear, all or nothing. The program checks the item (the v4 stamp, as gear_write), plans the write
 * (gear_logic.h: a refusal such as NO_SLOT takes nothing), counts each cost item in the payers' inventories, takes
 * them on one inventory transaction of its own (removeItems per payer, in the payers' order: each pays what it has
 * until the cost is met), commits it, counts again to see the commit stood, and only then writes the gear components.
 * A commit that rolled back by itself ends REFUSED with nothing written. Without KILN_ITEMS_UPGRADE_WRITE it only
 * pays (a failed roll that keeps the item and spends the cost). The roll itself is the caller's: kiln.items never
 * rolls.
 *
 * Payers: the player, or initialized inventories that are no player's and no NPC's (a container the caller vouches
 * for). A cost item must be a plain stack (no item entity of its own: never gear, never a gem), so the item being
 * upgraded can never be spent. */

#define KILN_ITEMS_UPGRADE_MAX_COSTS 8u
#define KILN_ITEMS_UPGRADE_MAX_PAYERS 8u
#define KILN_ITEMS_UPGRADE_MAX_COUNT 100000u

/* KilnItemsUpgradeRequest.flags */
enum {
    KILN_ITEMS_UPGRADE_WRITE = 0x0001u,   /* make `gear`'s write (its flags as gear_write takes them, without DRY_RUN);
                                             without it `gear` only names the item (flags 0) and the costs are taken */
    KILN_ITEMS_UPGRADE_DRY_RUN = 0x0002u, /* check, count and plan; take and write nothing */
};

/* KilnItemsUpgradeResult.status: KILN_ITEMS_GEAR_DONE .. KILN_ITEMS_GEAR_REFUSED (the write's own outcome, nothing
 * taken unless DONE), and: */
enum {
    KILN_ITEMS_UPGRADE_CANT_PAY = 9,  /* the payers hold too little of a cost item (have[] says how much); nothing changed */
    KILN_ITEMS_UPGRADE_BAD_PAYER = 10,/* a payer has no initialized inventory, or is another player or an NPC */
    KILN_ITEMS_UPGRADE_CHANGED = 11,  /* expect_available: the item's enhancement slots are not that many now */
};

typedef struct KilnItemsUpgradeCost {
    KilnItemId item;  /* a plain stack's item (no item entity) */
    uint32_t count;   /* 1..KILN_ITEMS_UPGRADE_MAX_COUNT */
} KilnItemsUpgradeCost;

typedef struct KilnItemsUpgradeRequest {
    uint32_t size;               /* sizeof(KilnItemsUpgradeRequest) */
    uint32_t flags;              /* KILN_ITEMS_UPGRADE_* */
    KilnEntity player;           /* who upgrades (required); the only player that may pay */
    uint32_t cost_count;         /* 0..8 */
    uint32_t payer_count;        /* 0..8; at least 1 when cost_count is not 0 */
    uint32_t expect_available;   /* 0, or 1..5: the item must still have this many enhancement slots (the rarity the
                                    caller priced), else CHANGED and nothing happens (two upgrades of one item in a step) */
    KilnItemsGearRequest gear;   /* the item (entity, stamp, item) and, with WRITE, the write */
    KilnItemsUpgradeCost costs[KILN_ITEMS_UPGRADE_MAX_COSTS];  /* an item at most once; unused entries 0 */
    KilnEntity payers[KILN_ITEMS_UPGRADE_MAX_PAYERS];          /* in order; each at most once; unused entries 0 */
} KilnItemsUpgradeRequest;

typedef struct KilnItemsUpgradeResult {
    uint32_t size;               /* sizeof(KilnItemsUpgradeResult) of kiln.items */
    int32_t status;              /* KILN_ITEMS_GEAR_DONE ... KILN_ITEMS_UPGRADE_BAD_PAYER */
    uint64_t ticket;
    uint32_t flags;              /* the request's */
    uint32_t cost_count;         /* the request's */
    uint32_t paid_from_player;   /* items the player paid, all costs together (the rest came from other payers) */
    uint32_t reserved;           /* 0 */
    uint32_t paid[KILN_ITEMS_UPGRADE_MAX_COSTS]; /* per cost: items taken (DONE: the count; DRY_RUN: would take) */
    uint32_t have[KILN_ITEMS_UPGRADE_MAX_COSTS]; /* per cost: what the payers held before (capped at 2^32-1) */
    KilnItemsGearResult gear;    /* the write's result (before, after, changed, steps, message); without WRITE before ==
                                    after and changed 0. The upgrade's message is gear.message */
} KilnItemsUpgradeResult;

typedef void(KILN_CALL* KilnItemsUpgradeDoneFn)(const KilnItemsUpgradeResult* result, void* user);

/* ---------------------------------------------------------------------------------------------- rarity rolls -- */

/* v8, since 0.2.0-dev.29.19 (the equipment wave, package P5; feature items.gear.rolls; check header.size).
 *
 * A rarity rolled when the game creates an item, per item type. createPide (docs/re/items.md section 10) takes the
 * rarity its caller asked for, or, with none asked, the type's rule (generateRarity: the lowest allowed, else
 * ItemInfo.rarity); the item gets PerkContainerNew {rarity + 1, 0} when the type has perk slot [rarity], and its weapon
 * gem slot by the rarity's chance. kiln.items' mid hook where createPide reads the asked rarity (items/PideRarity)
 * replaces it, for item types a rule names, with a weighted roll among the rarities the type has a perk slot for, so the
 * game itself then makes the item at that rarity: enhancements, socket chance, durability. Which creations a rule takes
 * (KILN_ITEMS_ROLL_*):
 *  - CRAFT: the game creates the item and asked for no rarity: crafting at a station or the hand menu (the recipe
 *    function clears level, rarity and fuel), Items.GiveRecipeResult, and any other game creation without a rarity;
 *  - LOOT: the game creates it with a rarity it rolled or fixed: random loot (containers, world pickups, random
 *    drop-table stacks roll one from globalRarityDropRates); a loot entry that fixes its rarity (LootableItemInfo
 *    useUiRarity) is a LOOT creation too: leave LOOT out for such an item to keep it;
 *  - GIVE: a give of kiln.items for this item type without KILN_ITEMS_RARITY. A give with KILN_ITEMS_RARITY is never
 *    rolled.
 * Items no rule names are never touched. Server world (and a hosting client, which is the server of its world). */
enum {
    KILN_ITEMS_ROLL_CRAFT = 0x1u,
    KILN_ITEMS_ROLL_LOOT = 0x2u,
    KILN_ITEMS_ROLL_GIVE = 0x4u,
};

/* One item type's rule. */
typedef struct KilnItemsRarityRoll {
    KilnItemId item;     /* the item type (not 0; once per set) */
    uint32_t sources;    /* KILN_ITEMS_ROLL_* (at least one; other bits are refused) */
    float weights[5];    /* Common .. Legendary: 0 or more, finite, at least one above 0. A rarity whose perk slot the
                            type lacks never comes up (its weight is left out); with none left the game's rarity stays */
    uint32_t reserved;   /* 0 */
} KilnItemsRarityRoll;

typedef struct KilnItemsRollStatus {
    uint32_t size;        /* in: sizeof(KilnItemsRollStatus) */
    uint8_t available;    /* 1: rolls act (feature items.gear.rolls hooked, ItemInfo's perk slots found by reflection) */
    uint8_t reserved0[3];
    uint32_t items;       /* item types with a rule now (all owners merged) */
    uint32_t owners;      /* owners with rules */
    uint32_t conflicts;   /* item types more than one owner names (the last owner to set its rules wins) */
    uint32_t reserved1;
    uint64_t rolled[5];   /* creations given each rarity by a roll since the start */
    uint64_t kept;        /* creations of a named type left as the game made them (the rule does not take that source) */
    uint64_t no_slot;     /* rolls with no rarity the type has a perk slot for: the game's rarity stayed */
    uint64_t faults;      /* the hook faulted (the item was created as the game made it) */
    char detail[160];     /* why rolls are off; NUL-terminated */
} KilnItemsRollStatus;

/* ---------------------------------------------------------------------------- power and armour scales ---- */

/* v8, since 0.2.0-dev.29.19 (the equipment wave, package P7; features items.gear.power and items.gear.armor; check
 * header.size).
 *
 * A multiplier of one item entity's power (the damage it deals) and of its armour, kept by kiln.items for this server
 * world and applied where the game computes them, so nothing of the item's saved state changes:
 *  - power: the game computes a used item's power when an impact of it starts (a swing, a shot, a spell: impact_initialize,
 *    items/ItemPower: int(int(seed x creep x level + seed) x dmgMod x speedMod)) and multiplies the impact's damage
 *    distribution by the weapon's power plus its ammunition's (docs/re/abilities.md). kiln.items' detour multiplies the
 *    power of an item entity with a scale right after the game computed it, so the damage of that item's impacts
 *    follows (armour, block, crit and attributes still act after, as for the game's own numbers);
 *  - armour: apply_armor sums every worn piece's armour (its type's armorSetup at its base level plus each unlocked
 *    perk's) into the wearer's nine armour values, slot by slot. Two mid hooks around one piece's part
 *    (items/ArmorSlotBase, items/ArmorSlotEnd) multiply what that piece added when it has a scale.
 * Server world only: the hooks act on the server world's storage, never on a game client's own world. What players see
 * in the game's tooltip is the type's number (a game client computes it from its own data, docs/re/game-ui.md): Kiln
 * clients can be shown the scaled number through kiln.ui.values' tooltip_values. The table is forgotten when a server
 * world closes or starts (entity ids start again); a caller that keeps scales across a restart sets them again. The
 * hooks install at the first scale other than 1 (nothing is hooked on a server that never uses them). */

#define KILN_ITEMS_SCALE_CAPACITY 8192u /* items with a scale at once */
#define KILN_ITEMS_SCALE_MIN 0.01f
#define KILN_ITEMS_SCALE_MAX 100.0f

/* KilnItemsGearScale.flags */
enum {
    KILN_ITEMS_SCALE_POWER = 0x01u, /* power: the damage the item's impacts deal */
    KILN_ITEMS_SCALE_ARMOR = 0x02u, /* armor: the armour values the item adds to its wearer */
};

typedef struct KilnItemsGearScale {
    uint32_t size;        /* sizeof(KilnItemsGearScale) */
    uint32_t flags;       /* gear_scale: which scales to set (KILN_ITEMS_SCALE_*); read: which are not 1 */
    KilnEntity entity;    /* the item entity */
    uint32_t stamp;       /* its v4 stamp (gear_scale: required) */
    float power;          /* SCALE_POWER: KILN_ITEMS_SCALE_MIN..MAX; 1 = the item's own power */
    float armor;          /* SCALE_ARMOR: likewise */
    uint32_t reserved[2]; /* 0 */
} KilnItemsGearScale;

typedef struct KilnItemsScaleStatus {
    uint32_t size;        /* in: sizeof(KilnItemsScaleStatus) */
    uint8_t power;        /* 1: power scales act (feature items.gear.power, the detour installed or installable) */
    uint8_t armor;        /* 1: armour scales act (feature items.gear.armor) */
    uint8_t power_hooked; /* 1: the power detour is installed */
    uint8_t armor_hooked; /* 1: the armour mid hooks are installed */
    uint32_t entries;     /* items with a scale now */
    uint32_t capacity;    /* KILN_ITEMS_SCALE_CAPACITY */
    uint32_t reserved;    /* 0 */
    uint64_t powered;     /* item powers the detour scaled since the start */
    uint64_t armored;     /* armour pieces the mid hooks scaled */
    uint64_t faults;      /* faults inside a hook (each ended the hook's work for that call; the game's own value stood) */
    char detail[160];     /* why a scale is off; NUL-terminated */
} KilnItemsScaleStatus;

/* ---------------------------------------------------------------------------------------- renaming a container ---- */

/* v9, since 0.2.0-dev.30.3 (GitHub issue #112; feature items.rename; check header.size: a table without it ends at 368).
 *
 * What a player's Rename button does, run by Kiln on the server: the game's own rename (docs/re/items.md section 14)
 * stores the text in the world's string table (which queues it for every client) and puts its id into the object's name
 * component through the world's tracked write (replication and the save) or, for an object never named before, adds the
 * component. So the name shows for other players, is in the next save and is read back by container_name and by the
 * game's own Rename screen, like one a player typed.
 *
 * The text is checked like the game's Rename field: at most KILN_ITEMS_NAME_MAX_CHARS characters (code points) and
 * KILN_ITEMS_NAME_MAX_BYTES bytes of well-formed UTF-8, no control character other than TAB (the field drops them), no
 * trimming and no other filter (the game has no profanity filter on names; a client's display may filter them). An empty
 * text clears the name, as the server's rename does for one (the Rename field itself never sends it). Only a
 * container may be renamed: an entity with an initialized inventory that is neither a player's nor an NPC's (a chest, a
 * storage station). Only where the server world runs (a dedicated server, or the game client hosting the world); a game
 * client joined to another machine's world is refused (KILN_ITEMS_RENAME_NOT_SERVER). */

#define KILN_ITEMS_NAME_CAPACITY 256u /* bytes: the game's rename message and its string records (the request's buffer) */
#define KILN_ITEMS_NAME_MAX_BYTES 64u /* bytes of UTF-8 the game's Rename field holds */
#define KILN_ITEMS_NAME_MAX_CHARS 48u /* code points it takes (FbUiBundle.maxInputLengthForLabelledEntities in the game data) */

/* KilnItemsRenameResult.status */
enum {
    KILN_ITEMS_RENAME_DONE = 0,          /* the game took the name (changed = 0: the object already had it) */
    KILN_ITEMS_RENAME_GONE = 1,          /* no such entity (any more) */
    KILN_ITEMS_RENAME_NOT_CONTAINER = 2, /* the entity has no container inventory (a player, an NPC, a prop, an altar) */
    KILN_ITEMS_RENAME_TOO_LONG = 3,      /* more than KILN_ITEMS_NAME_MAX_CHARS characters or _MAX_BYTES bytes */
    KILN_ITEMS_RENAME_BAD_TEXT = 4,      /* not well-formed UTF-8, or a control character the Rename field drops */
    KILN_ITEMS_RENAME_NOT_SERVER = 5,    /* no server world in this process (a joined game client, the main menu) */
    KILN_ITEMS_RENAME_FULL = 6,          /* the game's string table holds its maximum of different names (game_error) */
    KILN_ITEMS_RENAME_UNAVAILABLE = 7,   /* feature items.rename off, or switched off after a fault; nothing changed */
    KILN_ITEMS_RENAME_DROPPED = 8,       /* not run: the server world went away or did not step for 10 s */
};

typedef struct KilnItemsRenameRequest {
    uint32_t size;                   /* sizeof(KilnItemsRenameRequest) */
    KilnEntity entity;               /* the container */
    uint32_t len;                    /* bytes of text; 0 clears the name */
    uint32_t reserved;               /* 0 */
    char text[KILN_ITEMS_NAME_CAPACITY]; /* UTF-8, not NUL-terminated (bytes past len are ignored) */
} KilnItemsRenameRequest;

typedef struct KilnItemsRenameResult {
    uint32_t size;        /* sizeof(KilnItemsRenameResult) */
    int32_t status;       /* KILN_ITEMS_RENAME_* */
    uint64_t ticket;      /* 0 for a refusal at the call */
    KilnEntity entity;
    uint32_t game_error;  /* FULL: the game's error code */
    uint32_t steps;       /* sim steps from the call to the run */
    uint8_t changed;      /* DONE: 1 the stored name changed, 0 it was already this name */
    uint8_t cleared;      /* DONE: 1 the object has no name now (an empty text) */
    uint16_t reserved;
    char message[160];    /* what happened, in words; NUL-terminated */
} KilnItemsRenameResult;

/* Tick thread, in Pre, once per accepted rename. */
typedef void(KILN_CALL* KilnItemsRenameDoneFn)(const KilnItemsRenameResult* result, void* user);

typedef struct KilnItemsRenameStatus {
    uint32_t size;        /* in: sizeof(KilnItemsRenameStatus) */
    uint8_t available;    /* 1: renames can be queued here now (feature on, a server world in this process) */
    uint8_t feature;      /* 1: feature items.rename resolved and kiln.items' checks passed */
    uint8_t server;       /* 1: a server world runs in this process (a dedicated server or a hosting client) */
    uint8_t reserved0;
    uint32_t pending;     /* renames waiting for the next Pre */
    uint32_t done;        /* renames the game took since the start */
    uint32_t refused;     /* renames refused at the tick (gone, not a container, full, unavailable, dropped) */
    uint32_t reserved1;
    char detail[160];     /* why renames are off; NUL-terminated */
} KilnItemsRenameStatus;

/* ---------------------------------------------------------------------------------------------- the table ---- */

typedef struct KilnItemsApi {
    KilnServiceHeader header; /* { sizeof(KilnItemsApi), KILN_ITEMS_VERSION } */

    /* Queues a give / take. *out_ticket (optional) = the request's ticket (never 0). Refused at once with
     * KILN_ERR_INVALID_ARGUMENT (entity 0, item 0, count out of range, bad flags / rarity / level / slot) or
     * KILN_ERR_UNAVAILABLE (give/take are off on this build, or 1024 requests are waiting). Otherwise `done` runs once
     * with the result (it may be NULL). Any thread. */
    KilnResult(KILN_CALL* give)(KilnOwner owner, const KilnItemsRequest* request, KilnItemsDoneFn done, void* user,
                                uint64_t* out_ticket);
    KilnResult(KILN_CALL* take)(KilnOwner owner, const KilnItemsRequest* request, KilnItemsDoneFn done, void* user,
                                uint64_t* out_ticket);
    /* Drops a request's callback (the transaction may still run if it already started). KILN_ERR_NOT_FOUND when its
     * callback already ran. Any thread. */
    KilnResult(KILN_CALL* cancel)(uint64_t ticket);
    /* Drops the callbacks of every request of `owner` and ends its fuel_keep entries and its transfer rules, handlers
     * and reports (call it when a plugin's owner goes away). Transfer callback draining follows transfer_end below.
     * Any thread. */
    void(KILN_CALL* cancel_owner)(KilnOwner owner);

    /* The inventory of `entity`: *info (optional) and up to `capacity` slots (every slot of every linked 8-slot
     * inventory, empty ones included, in link order). *out_count = slots written. KILN_ERR_NOT_FOUND: no such entity
     * or no InventorySetup; KILN_ERR_BUFFER_TOO_SMALL: capacity < info.slot_total (the first `capacity` are written);
     * KILN_ERR_UNAVAILABLE before the server world's first step. Tick thread (Pre / Post). */
    KilnResult(KILN_CALL* read)(KilnEntity entity, KilnItemsInventory* info, KilnItemsSlot* slots, uint32_t capacity,
                                uint32_t* out_count);
    /* Entities with an initialized InventorySetup matching `flags` (KILN_ITEMS_FIND_*; 0 = all), in no particular
     * order. *out_count = how many match; more than capacity: the first `capacity` are written and
     * KILN_ERR_BUFFER_TOO_SMALL is returned. One pass over the world's entities. Tick thread (Pre / Post). */
    KilnResult(KILN_CALL* find_inventories)(uint32_t flags, KilnEntity* out, uint32_t capacity, uint32_t* out_count);

    /* Catalog (the server world's item registry; empty until it has loaded, a few steps after the world starts).
     * Any thread. */
    uint32_t(KILN_CALL* item_count)(void);
    KilnResult(KILN_CALL* item_at)(uint32_t index, KilnItemInfo* out); /* index < item_count(), sorted by name */
    KilnResult(KILN_CALL* item_info)(KilnItemId id, KilnItemInfo* out);
    KilnItemId(KILN_CALL* find_item)(KilnStr name);                    /* debug name: exact, else case-insensitive */
    KilnItemId(KILN_CALL* item_id_of_guid)(const uint8_t guid[16]);    /* FNV-1a-32, as the game */

    KilnResult(KILN_CALL* status)(KilnItemsStatus* out);

    /* ---- v2: water (feature items.fuel; KILN_ERR_UNAVAILABLE without it) ---- */

    /* Queues a Fuel write; it runs in the next sim step inside the program, `done` (optional) in the Pre after.
     * KILN_ERR_INVALID_ARGUMENT for entity 0, bad flags or a non-zero `reserved`. Any thread. cancel() drops the
     * callback, cancel_owner() too. */
    KilnResult(KILN_CALL* fuel)(KilnOwner owner, const KilnItemsFuelRequest* request, KilnItemsFuelDoneFn done,
                                void* user, uint64_t* out_ticket);
    /* An entity's water: *current and *max in units (optional). KILN_ERR_NOT_FOUND: it holds none (no Fuel).
     * Tick thread (Pre / Post). */
    KilnResult(KILN_CALL* fuel_read)(KilnEntity entity, int32_t* current, int32_t* max);
    /* A refill policy: every sim step, inside the program after the game's fill and pour, the containers of `request`
     * (INVENTORIES and `item` as for fuel()) are refilled: FILL keeps them at their max (or `limit`), ADD adds `units`
     * per second of game time up to the same level. SET and DRY_RUN are refused. It runs until fuel_keep_end,
     * cancel_owner(owner) or the server world closing. At most 256 at once. `on_refill` (optional) reports what it
     * changed, once per Pre. *out_handle (required) identifies it. It does not stop by itself when the entity goes
     * away: a player's entity id is reused by the next player, so end it when the player leaves. Any thread. */
    KilnResult(KILN_CALL* fuel_keep)(KilnOwner owner, const KilnItemsFuelRequest* request, KilnItemsFuelKeepFn on_refill,
                                     void* user, uint64_t* out_handle);
    KilnResult(KILN_CALL* fuel_keep_end)(uint64_t handle); /* KILN_ERR_NOT_FOUND: no such keep */

    /* ---- v3: moves, names, transfer events (check header.size; KILN_ERR_UNAVAILABLE without the feature) ---- */

    /* Queues a move (see "moves"). *out_ticket (optional) = its ticket. Refused at once with KILN_ERR_INVALID_ARGUMENT
     * (an entity 0, from == to, a bad kind / flags / slot / count, a non-zero `reserved`) or KILN_ERR_UNAVAILABLE
     * (feature items.move off, 1024 requests waiting). Otherwise `done` runs once on the tick thread in Pre with the
     * result (it may be NULL). cancel(ticket) and cancel_owner(owner) drop the callback. Any thread. */
    KilnResult(KILN_CALL* move)(KilnOwner owner, const KilnItemsMoveRequest* request, KilnItemsMoveDoneFn done,
                                void* user, uint64_t* out_ticket);

    /* The custom name a player gave an object with the Rename button (a chest, a station...), as UTF-8, NUL-terminated,
     * at most capacity - 1 bytes of it; *out_len = its full length. KILN_ERR_NOT_FOUND: no name (the entity has no
     * name component, or it is empty, or the game's string table has no such string); KILN_ERR_BUFFER_TOO_SMALL: the
     * name was cut; KILN_ERR_UNAVAILABLE: feature items.names is off, or before the server world's first step. Reads
     * the game's string table: tick thread (Pre / Post). */
    KilnResult(KILN_CALL* container_name)(KilnEntity entity, char* buf, uint32_t capacity, uint32_t* out_len);

    /* Transfer rules, handlers and reports (see "transfer events and rules"). Each returns a handle for transfer_end.
     * KILN_ERR_INVALID_ARGUMENT for a bad rule or NULL callback; KILN_ERR_UNAVAILABLE when feature items.transfer is
     * off, the hooks cannot be installed (logged), or 256 of that kind are registered. The first registration installs
     * the hooks. Any thread. */
    KilnResult(KILN_CALL* transfer_rule)(KilnOwner owner, const KilnItemsTransferRule* rule, uint64_t* out_handle);
    KilnResult(KILN_CALL* transfer_listen)(KilnOwner owner, KilnItemsTransferFn fn, void* user, uint64_t* out_handle);
    KilnResult(KILN_CALL* transfer_report)(KilnOwner owner, KilnItemsTransferReportFn fn, void* user,
                                           uint64_t* out_handle);
    /* Ends a rule, handler or report listener; the hooks stay installed (they are never removed, so another loader's
     * hook on the same functions is never undone). KILN_ERR_NOT_FOUND: no such handle. Any thread. cancel_owner(owner)
     * ends all of an owner's. Invalidation prevents further callback starts, including in a captured registry snapshot.
     * Called outside a transfer handler/report callback, waits for callbacks already running before returning: the
     * caller may then release their code/userdata. Called reentrantly from a transfer callback, does not wait (avoids
     * self/cross-callback deadlocks); already-running callbacks may finish. Keep code/userdata alive until those
     * callbacks return, or drain again from outside any transfer callback before freeing/unloading. */
    KilnResult(KILN_CALL* transfer_end)(uint64_t handle);

    KilnResult(KILN_CALL* status2)(KilnItemsStatus2* out);

    /* Appended v3: copies a committed give/take result before its callback is delivered, or while its callback batch
     * is being delivered (including from another result's callback). No callback runs and nothing is consumed. After
     * the batch returns the result expires: KILN_ERR_NOT_FOUND also means the request hasn't completed yet. Any thread.
     * For save journals: the inventory commit can precede the next Pre callback. Check header.size before calling.
     * out->size must be at least sizeof(KilnItemsResult). */
    KilnResult(KILN_CALL* peek_result)(uint64_t ticket, KilnItemsResult* out);

    /* ---- v4: item instances (check header.size; KILN_ERR_UNAVAILABLE without feature items.instances) ---- */

    /* Reads `count` item entities: items[i].entity in, every other field out (zeroed first). `item_size` =
     * sizeof(KilnItemsInstance) the caller was built with (at least the v4 size; the array's stride). `flags`:
     * KILN_ITEMS_READ_* (0 = everything). An entity that is no item entity now, or one kiln.items saw destroyed, comes
     * back without FOUND. KILN_ERR_UNAVAILABLE: feature items.instances is off, or before the server world's first
     * step. Tick thread (Pre / Post). */
    KilnResult(KILN_CALL* read_instances)(KilnItemsInstance* items, uint32_t count, uint32_t item_size, uint32_t flags);

    /* Every item entity in `holder`'s inventories (Virtual links expanded), as read_instances reads it, in flat-slot
     * order (saved slots first). *out_count = how many there are (more than capacity: the first `capacity` are written
     * and KILN_ERR_BUFFER_TOO_SMALL is returned); *out_initialized (optional) = InventorySetup.isInitialized.
     * KILN_ERR_NOT_FOUND: no such entity or no InventorySetup. Tick thread (Pre / Post). */
    KilnResult(KILN_CALL* read_holder)(KilnEntity holder, KilnItemsInstance* out, uint32_t capacity, uint32_t item_size,
                                       uint32_t* out_count, uint8_t* out_initialized);

    /* The stamps' state. Any thread. */
    KilnResult(KILN_CALL* instances_status)(KilnItemsInstancesStatus* out);

    /* ---- v5, since 0.2.0-dev.27: equipment (check header.size; KILN_ERR_UNAVAILABLE without feature items.equip) ---- */

    /* Queues an equip or unequip (see "equipment"). *out_ticket (optional) = its ticket. Refused at once with
     * KILN_ERR_INVALID_ARGUMENT (player 0, a slot that is no equipment slot, ANY with UNEQUIP, an equip without `slot`, an
     * unequip with `slot` or `pide`, bad flags, non-zero reserved fields), KILN_ERR_UNAVAILABLE (feature items.equip off,
     * no kiln.world or kiln.players, the death check cannot be made, the program refused or broken, no live server world
     * in this process (a game client in its menu or joined to another machine's world, a world that stopped), or the
     * core's session query does not answer) or KILN_ERR_BUFFER_TOO_SMALL (1024 requests are waiting: a refusal for now).
     * Otherwise `done` runs once with the result (it may be NULL). cancel(ticket) and cancel_owner(owner) drop the
     * callback; owner 0 is Kiln's own (no release drops it). Any thread. A request for a player entity (1..1023) runs only
     * for the session it was made for, checked in the Pre of its step against the game's player record read then: the
     * next player in the slot gets the same id, so a request whose player left comes back NO_INVENTORY (REFUSED when the
     * records cannot be read). A dead player is refused (REFUSED) right before the transaction. */
    KilnResult(KILN_CALL* equip)(KilnOwner owner, const KilnItemsEquipRequest* request, KilnItemsEquipDoneFn done,
                                 void* user, uint64_t* out_ticket);
    KilnResult(KILN_CALL* equip_status)(KilnItemsEquipStatus* out); /* any thread */

    /* ---- v6, since 0.2.0-dev.29: equipment sets (check header.size; KILN_ERR_UNAVAILABLE without feature
     * items.equip_sets) ---- */

    /* Captures a snapshot of `player`'s inventory for `owner` (see "equipment sets"); out->size in. KILN_OK: out holds
     * the token, epoch, step, nonce and identity. KILN_ERR_INVALID_ARGUMENT: player 0, out NULL or too small.
     * KILN_ERR_UNAVAILABLE: sets are off (set_status says why). KILN_ERR_WRONG_THREAD: not the world thread in a
     * world-safe phase (kiln.window_clock: Pre or Post). KILN_ERR_NOT_FOUND: refused for this player (out->refusal and
     * out->reason when out->size covers them). KILN_ERR_BUFFER_TOO_SMALL: the snapshot budget is used up (refusal
     * BUDGET; nothing was evicted). */
    KilnResult(KILN_CALL* set_capture)(KilnOwner owner, uint32_t player, KilnSetSnapshotV1* out);
    /* Releases a snapshot `owner` captured. KILN_ERR_NOT_FOUND: unknown, used up, expired or another owner's. Any
     * thread. */
    KilnResult(KILN_CALL* set_release_snapshot)(KilnOwner owner, uint64_t token);
    /* The state of equipment sets. Any thread. */
    KilnResult(KILN_CALL* set_status)(KilnSetStatusV1* out);
    /* Plans `count` entries (KilnSetOpV1, kiln_decisions.h; omitted slots stay as they are) against the snapshot `token`
     * of `owner` (see "Planning"; the snapshot is not taken). KILN_OK: *out has the plan or the refusal.
     * KILN_ERR_INVALID_ARGUMENT: out NULL or too small, or malformed entries (more than 20, a slot that is no equipment
     * slot or named twice, an item selected twice, an item without its entity, id or stamp, a stack without its item,
     * slot or count, non-zero reserved fields). KILN_ERR_UNAVAILABLE: sets are off. Any thread. */
    KilnResult(KILN_CALL* set_plan)(KilnOwner owner, uint64_t token, const KilnSetOpV1* entries, uint32_t count, KilnSetPlanV1* out);
    /* Queues a set (see "Applying"). *ticket (optional) = its ticket, from the service's one ticket counter.
     * KILN_ERR_INVALID_ARGUMENT: request NULL or too small, player 0, bad flags, malformed entries (as set_plan), an
     * origin_owner the caller may not name, or a snapshot of another player. KILN_ERR_UNAVAILABLE: sets are off.
     * KILN_ERR_NOT_FOUND: stale: another process's nonce, or a snapshot that is unknown, used, expired, another owner's,
     * another world's or from before a stamp reset (no callback). KILN_ERR_BUFFER_TOO_SMALL: 256 sets are waiting (a
     * refusal for now; the snapshot is not used). KILN_OK: `done` (it may be NULL) runs once later. Any thread. */
    KilnResult(KILN_CALL* equip_set)(KilnOwner owner, const KilnSetRequestV1* request, KilnSetDoneV1 done, void* user, uint64_t* ticket);
    /* Ends a queued set (not started) Dropped at the next Pre; its callback is kept and runs once. KILN_ERR_NOT_FOUND:
     * no such set, or it already ran or is running. Never undoes a commit. Any thread. */
    KilnResult(KILN_CALL* set_cancel)(uint64_t ticket);

    /* ---- v7, since 0.2.0-dev.30: gem sockets (check header.size; KILN_ERR_UNAVAILABLE without feature
     * items.pide_options) ---- */

    /* Sets `owner`'s socket rolls (see "gem sockets"); NULL or every chance 0 ends them. KILN_ERR_UNAVAILABLE (checked
     * first, for any non-NULL roll, even an invalid one: a probe that changes nothing): feature gems.sockets is off.
     * KILN_ERR_INVALID_ARGUMENT: a chance outside 0..1 (or not a number), size too small, reserved not 0.
     * cancel_owner(owner) ends them too. Any thread; the next item the game creates uses them. */
    KilnResult(KILN_CALL* socket_roll)(KilnOwner owner, const KilnItemsSocketRoll* roll);

    /* ---- v8, since 0.2.0-dev.29.19: gear (check header.size; KILN_ERR_UNAVAILABLE without feature items.gear) ---- */

    /* Queues a gear write (see "gear"). *out_ticket (optional) = its ticket. Refused at once with
     * KILN_ERR_INVALID_ARGUMENT (no request or a short one, entity 0, stamp 0, no operation, bad flags, available
     * outside 1..5, unlocked above 5, level outside 1..255, non-zero reserved fields) or KILN_ERR_UNAVAILABLE (feature
     * items.gear off, no kiln.world, a layout check failed, the program refused or switched off, 1024 requests
     * waiting). Otherwise `done` runs once on the tick thread in Pre with the result (it may be NULL). cancel(ticket)
     * and cancel_owner(owner) drop the callback. Any thread. */
    KilnResult(KILN_CALL* gear_write)(KilnOwner owner, const KilnItemsGearRequest* request, KilnItemsGearDoneFn done,
                                      void* user, uint64_t* out_ticket);
    /* An item entity's gear now, through GetComponent (no write): *out zeroed, then filled. stamp 0 = any; otherwise
     * KILN_ERR_NOT_FOUND also when the entity is not that stamped item entity. KILN_ERR_NOT_FOUND: no item entity
     * (no ItemState). KILN_ERR_UNAVAILABLE: feature items.gear or items.instances off, or before the server world's
     * first step. Tick thread (Pre / Post). */
    KilnResult(KILN_CALL* gear_read)(KilnEntity entity, uint32_t stamp, KilnItemsGearState* out);
    KilnResult(KILN_CALL* gear_status)(KilnItemsGearStatus* out); /* any thread */

    /* ---- v8, since 0.2.0-dev.29.19 (package P5): rarity rolls at creation (check header.size; KILN_ERR_UNAVAILABLE
     * without feature items.gear.rolls) ---- */

    /* Replaces `owner`'s rules (see "rarity rolls"): `count` entries of `roll_size` bytes (sizeof(KilnItemsRarityRoll)
     * the caller was built with, at least the v8 size). rolls NULL or count 0 ends them (always KILN_OK; cancel_owner
     * does it too). KILN_ERR_UNAVAILABLE (checked first for a set): rolls are off (roll_status says why).
     * KILN_ERR_INVALID_ARGUMENT: roll_size too small, more than 65536 entries, an entry roll_status would not take (item
     * 0 or named twice, no source or an unknown bit, a weight below 0 or not a number, no weight above 0, reserved not
     * 0); nothing changes then. Any thread; the next item the game creates uses them. */
    KilnResult(KILN_CALL* rarity_rolls)(KilnOwner owner, const KilnItemsRarityRoll* rolls, uint32_t count, uint32_t roll_size);
    KilnResult(KILN_CALL* roll_status)(KilnItemsRollStatus* out); /* any thread */

    /* ---- v8, since 0.2.0-dev.29.19 (package P3): rarity upgrades, a gear write paid for (see "upgrades"; check
     * header.size; KILN_ERR_UNAVAILABLE without feature items.gear or the inventory transaction) ---- */

    /* Queues an upgrade. *out_ticket (optional) = its ticket. Refused at once with KILN_ERR_INVALID_ARGUMENT (no request
     * or a short one, player 0, entity or stamp 0, bad flags, a WRITE whose gear request gear_write would refuse or that
     * has DRY_RUN, gear fields set without WRITE, more than 8 costs or payers, costs without a payer, a cost item 0 or
     * named twice, a count outside 1..100000, a payer 0 or named twice, expect_available above 5, non-zero unused entries) or
     * KILN_ERR_UNAVAILABLE (as gear_write, or the inventory transaction is off). Otherwise `done` runs once on the tick
     * thread in Pre with the result (it may be NULL). cancel(ticket) and cancel_owner(owner) drop the callback. Any
     * thread. */
    KilnResult(KILN_CALL* gear_upgrade)(KilnOwner owner, const KilnItemsUpgradeRequest* request, KilnItemsUpgradeDoneFn done,
                                        void* user, uint64_t* out_ticket);

    /* ---- v8, since 0.2.0-dev.29.19 (package P7): per-instance power and armour (see "power and armour scales"; check
     * header.size: a table without them ends at 344) ---- */

    /* Sets an item entity's power and/or armour scale (s->flags names which; 1 = the item's own value, which removes
     * it). KILN_ERR_INVALID_ARGUMENT: no request or a short one, entity or stamp 0, no flag or an unknown one, a scale
     * outside KILN_ITEMS_SCALE_MIN..KILN_ITEMS_SCALE_MAX or not a number, reserved not 0. KILN_ERR_NOT_FOUND: the entity
     * is not that stamped item entity (gone, or never stamped). KILN_ERR_UNAVAILABLE: the scale's feature is off
     * (items.gear.power, items.gear.armor; scale_status says why), or its hook could not be installed (the first scale
     * other than 1 installs it). KILN_ERR_BUFFER_TOO_SMALL: KILN_ITEMS_SCALE_CAPACITY items have a scale. Tick thread
     * (Pre / Post); the next game computation uses it. */
    KilnResult(KILN_CALL* gear_scale)(const KilnItemsGearScale* scale);
    /* An item entity's scales: *out zeroed, then filled (size, entity, stamp, flags = the scales other than 1, power,
     * armor; 1 for a scale it has none of). stamp 0 = any. KILN_ERR_NOT_FOUND: not that stamped item entity. Any thread. */
    KilnResult(KILN_CALL* gear_scale_read)(KilnEntity entity, uint32_t stamp, KilnItemsGearScale* out);
    KilnResult(KILN_CALL* scale_status)(KilnItemsScaleStatus* out); /* any thread */

    /* ---- v9, since 0.2.0-dev.30.3: renaming a container (see "renaming a container"; check header.size: a table
     * without these ends at 368) ---- */

    /* Checks the request now and queues the rename for the next Pre, where it runs through the game's own rename.
     * Refused at once (nothing queued, *out_refusal filled when not NULL; no callback) with KILN_ERR_INVALID_ARGUMENT
     * (no request or a short one, reserved not 0: status BAD_TEXT; entity 0: GONE; a text longer than the Rename field
     * takes: TOO_LONG; one it would not take: BAD_TEXT), KILN_ERR_NOT_FOUND (called on the tick thread: no such
     * entity, GONE, or not a container, NOT_CONTAINER) or KILN_ERR_UNAVAILABLE (feature off: UNAVAILABLE; no server world
     * in this process: NOT_SERVER; 1024 renames waiting: UNAVAILABLE). Otherwise KILN_OK, *out_ticket (optional) = its
     * ticket, and `done` (it may be NULL) runs once on the tick thread in Pre with the result: the entity is checked again
     * there (GONE, NOT_CONTAINER), and renames of one entity run in the order they were made. cancel(ticket) and
     * cancel_owner(owner) drop the callback (the rename itself still runs). Any thread. */
    KilnResult(KILN_CALL* container_rename)(KilnOwner owner, const KilnItemsRenameRequest* request, KilnItemsRenameDoneFn done,
                                            void* user, uint64_t* out_ticket, KilnItemsRenameResult* out_refusal);
    KilnResult(KILN_CALL* rename_status)(KilnItemsRenameStatus* out); /* any thread */

    /* Since 0.2.0-dev.30.6; appended at 384, table end 392. Same queue, sessions, death check, cancellation and
     * callbacks as equip. Server-authoritative only. INVALID_ARGUMENT: malformed request, count outside 1..65535,
     * invalid physical equipment slot or flags/reserved. UNAVAILABLE: equip/transaction/move/commit guards missing,
     * wrong side or no server session. A NULL request is a side-effect-free availability probe (OK or UNAVAILABLE).
     * BUFFER_TOO_SMALL: 1024 equip requests waiting. Game outcomes in the callback. */
    KilnResult(KILN_CALL* give_equip)(KilnOwner owner, const KilnItemsGiveEquipRequest* request, KilnItemsEquipDoneFn done,
                                    void* user, uint64_t* out_ticket);
} KilnItemsApi;

#ifdef __cplusplus
}
#endif
#endif
