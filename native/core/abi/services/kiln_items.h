/* SPDX-License-Identifier: MIT
 * kiln.items v3 (v1 + water in containers: fuel, fuel_read, fuel_keep, fuel_keep_end; v3 + item moves that keep item state,
 * container names, inventory-transfer events and rules): put items into and take them out of inventories (players'
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
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_ITEMS_SERVICE "kiln.items"
#define KILN_ITEMS_VERSION 3u  /* v2 appends the fuel functions, v3 the move, name and transfer functions; check header.size */

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
    uint32_t reserved;
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
    uint32_t reserved;
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
    KILN_ITEMS_MOVE_DRY_RUN = 0x01u,   /* run and roll back: the result says what would move; no transfer report */
    KILN_ITEMS_MOVE_EQUIPPED = 0x02u,  /* STACK: a slot a player has equipped or holds may move (default: refused) */
    KILN_ITEMS_MOVE_ACTIONBAR = 0x04u, /* STACK: may take from and fill a player's action-bar pages (default: refused
                                          as a source, skipped as a destination); MOVE_ALL: takes the action bar along
                                          (default: it stays) */
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
    KilnSlotId to_slot;  /* STACK: a slot of `to` to fill first (0 = the game's choice: partial stacks of the item,
                            then the first empty slot) */
} KilnItemsMoveRequest;

/* One piece of the ledger: `amount` of `item` left `from_slot` and arrived in `to_slot`. Gear (a stack with an item
 * entity) moves whole: `pide` is the item entity that moved with it (the same entity before and after). */
typedef struct KilnItemsMoveChange {
    KilnSlotId from_slot;
    KilnSlotId to_slot;
    KilnItemId item;
    uint32_t amount;
    KilnEntity pide;
    uint32_t reserved;
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
};

typedef struct KilnItemsTransferCtx KilnItemsTransferCtx; /* opaque; valid only during the handler call */

typedef struct KilnItemsTransferEvent {
    uint32_t size;
    uint32_t kind;         /* KILN_TRANSFER_QUICK_STACK or MOVE_ALL (one bit) */
    KilnEntity from;       /* the inventory the items leave */
    KilnEntity to;         /* the inventory they enter */
    KilnEntity player;     /* whichever of from / to is a player's inventory, else 0 */
    uint32_t flags;        /* KILN_TRANSFER_BY_MOD */
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
} KilnItemsApi;

#ifdef __cplusplus
}
#endif
#endif
