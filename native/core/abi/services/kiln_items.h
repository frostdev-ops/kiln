/* SPDX-License-Identifier: MIT
 * kiln.items v2 (v1 + water in containers: fuel, fuel_read, fuel_keep, fuel_keep_end): put items into and take them out of inventories (players' backpacks, chests, villagers), gear with a
 * level and rarity included; read an entity's inventory; the game's item catalog.
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
#define KILN_ITEMS_VERSION 2u  /* v2 appends the fuel functions; check header.size before using them */

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
};

typedef struct KilnItemsRequest {
    uint32_t size;       /* sizeof(KilnItemsRequest) */
    uint32_t flags;      /* KILN_ITEMS_* above; other bits are refused */
    KilnEntity entity;   /* whose inventory: a player (1..1023), chest, villager: any entity with an InventorySetup */
    KilnItemId item;
    uint32_t count;      /* 1..100000 (give spreads over as many slots as needed) */
    uint32_t level;      /* give with KILN_ITEMS_LEVEL: 1..255 */
    uint8_t rarity;      /* give with KILN_ITEMS_RARITY: 0..4 */
    uint8_t reserved[3]; /* 0 */
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
    /* Drops the callbacks of every request of `owner` and ends its fuel_keep entries (call it when a plugin's owner
     * goes away). Any thread. */
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
} KilnItemsApi;

#ifdef __cplusplus
}
#endif
#endif
