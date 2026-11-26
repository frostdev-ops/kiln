/* SPDX-License-Identifier: MIT
 * kiln.stations v1 (since 0.2.0-dev.29): the production stations of a base and the game's own station actions, for code.
 * Published by kiln-items.dll (native/services/items/stations.cpp) on the server world: a dedicated server, or a game client
 * that hosts. A joined client has no server world: status() says so and every call answers KILN_ERR_UNAVAILABLE.
 * Capabilities (sigdb features, server.d/stations.json): stations.read, stations.fill, stations.control, stations.events.
 * Evidence: docs/re/stations.md. Guide: docs/kiln/services/stations.md.
 *
 *  - A station is any entity with the game's FactoryStation component (kilns, smelters, looms, grinders, seed beds: 31
 *    templates). It runs the recipes of its Crafting.workshopId on its own, from one inventory that holds the inputs and
 *    the outputs. The selected recipe stays set and repeats; inputs leave when a cycle starts, outputs arrive when it ends.
 *  - A base is a record of the game's PlayerBasesSystemServer: the union of overlapping flame-altar zones (at most 16),
 *    with two lists the game rebuilds every server frame: its crafting-stock containers (the Magic storage chests and the
 *    player-planted refilling fruit trees: the template component InventoryCraftingStock) and its water sources (wells and
 *    barrels that hold water). Ordinary chests are never in it: the game's fill button and crafting from base storage
 *    never use them, and neither does this service. A base's id is the smallest BaseIdComponent.baseId of its altars (the
 *    16 bytes compared in memory order); it changes when an overlapping altar is added or removed.
 *  - Fill runs the game's own "fill from chests" (AutoTransferRecipe: every input topped up to "complete crafts loaded +
 *    n", taken from the base's stock containers in the game's list order, water from its water sources; one inventory
 *    transaction, all or nothing) with no player: nothing is taken from a player's bag unless the request names one.
 *    Kiln chooses n so the call does not fail for a short input (unless KILN_STATION_FILL_EXACT).
 *  - Stop, resume and select write the bytes the game's own buttons write (PlayerCraftingAction types 4 and 2) through the
 *    game's tracked write, and tell ServerFactoryInfo so open station screens refresh. Stopping aborts the running cycle
 *    and the game puts its inputs back into the station (progress is lost); KILN_STATION_AFTER_CRAFT waits for the cycle's
 *    end first. Select refuses a recipe no one on the server has unlocked (the world or a player in game, through
 *    kiln.knowledge) unless KILN_STATION_ALLOW_LOCKED.
 *  - Station outputs as crafting stock (since 0.2.0-dev.29.17, capability stations.output_sources): a station opted in
 *    with set_output_source() lends its finished items (its Outputs: the stacks of an output of its selected or running
 *    recipe that is not also an input of either, a category input's options all counting as inputs) to every FILL in its
 *    base, last: after the named player's bag, the base's Magic chests, and any ordinary chests the request names.
 *    Revalidated inside the transaction against the live inventory and the station's current recipes; a station's queued
 *    inputs, its leftovers and an output that is also an input are never taken; a station of another base is never used.
 *    Kiln asks the game's own AutoTransferRecipe for each later source's share with a source list of only those entities
 *    (the game's take is per item), then for the rest as usual, in one transaction: all or nothing. Nothing is opted in
 *    by default; the opt-ins are this session's (Kiln.Blocks keeps them with the save). docs/re/stations.md section 8.
 *  - Ordinary chests (since 0.2.0-dev.29.17, on the FILL request, default off): chest_count and chests name storage
 *    containers in the station's base that are not Magic chests and not refilling plants. Order: the named player's bag,
 *    the Magic chests (after the reserves), those chests in the order the request names them, then opted-in station
 *    outputs. Revalidated inside the transaction: same base, an initialized inventory that is neither a player's nor an
 *    NPC's (InventorySetup's own flags, as KILN_ITEMS_FIND_CONTAINERS), not a production station, not already in the
 *    base's crafting-stock list. A request of KILN_STATION_REQUEST_V1 bytes names none. The station screen's button does
 *    not name chests. max_chests() returns KILN_STATIONS_MAX_CHESTS; an older table has no such slot.
 *  - Collect (since 0.2.0-dev.29.17, collect()): a station's finished stacks into one inventory or the base's Magic
 *    chests, each stack through kiln.items' move (the game's slot transfer, the same one Items.Move uses). Its own rules,
 *    which are not Kiln.Blocks' Stations.Collect (that managed call keeps its own Items.Move loop): see KilnStationCollect.
 *  - Station outputs in a craft (since 0.2.0-dev.29.17, capabilities stations.craft_sources and stations.craft_ui): the
 *    same opt-in is the last source of a player's craft. kiln-items pays such a craft inside the game's own payment
 *    (through kiln.rules' hook on it), only when the base's opted-in outputs cover every line's shortfall; a Kiln client's
 *    crafting menu counts what the server sends it (set_craft_stock, craft_items). docs/re/stations.md section 10.
 *  - The station screen's fill buttons on a Kiln client (since 0.2.0-dev.29.19, capability stations.fill_ui): the game
 *    enables a production station's "+1" and "+10" fill only when its own dry run of the fill succeeds on the client's
 *    view (the bag and the Magic chests). While the server's craft-stock snapshot is on, kiln-items answers "can fill"
 *    for a fill the game refused for missing materials when the server said its own fill, with opted-in station outputs,
 *    covers that many crafts (set_fill_answers); the refused fills with no answer yet are what the client asks the
 *    server about (fill_asks). The press is the game's own; the server's Kiln.Blocks runs it as Kiln's fill. Not
 *    authority. docs/re/stations.md section 10.3.
 *  - A finished cycle is kiln.gameplay's KILN_GAMEPLAY_CRAFTED with actor == target == the station (once per cycle since
 *    0.2.0-dev.29). This table adds state, placed, removed and base-list events by polling (watch, events).
 *
 * Threads: status, submit, cancel, watch, last_seq, set_output_source, output_sources, max_chests, collect,
 * set_craft_stock, set_fill_answers, fill_asks: any thread. Every read (base_* including base_stock_with_stations, stations, station_read, recipe*,
 * category*, workshop_recipes, plan, events, craft_items): the tick thread, Pre or Post (KILN_ERR_WRONG_THREAD otherwise). Actions (submit, collect) run
 * inside kiln.items' program kiln_items_stations (registered through kiln.world, after the game's programs, so a player's
 * own action in the same step runs first); their callbacks come on the tick thread in Pre, one step after they ran.
 * The table only grows at the end. */
#ifndef KILN_STATIONS_H
#define KILN_STATIONS_H
#include "../kiln_api.h"
#include "kiln_items.h" /* KilnItemId, KilnSlotId, KilnItemsMoveChange */
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_STATIONS_SERVICE "kiln.stations"
#define KILN_STATIONS_VERSION 1u

#define KILN_STATIONS_MAX_BASES 16u   /* the game's own limit */
#define KILN_STATIONS_MAX_ALTARS 16u  /* altars listed per base (a base merges at most 16 zones) */
#define KILN_STATIONS_MAX_INPUTS 32u  /* recipe inputs (the game's data has at most 32) */
#define KILN_STATIONS_MAX_OUTPUTS 8u
#define KILN_STATIONS_MAX_CHANGES 32u /* ledger entries in a result */
#define KILN_STATIONS_MAX_KEEP 64u    /* reserves in a fill request */
#define KILN_STATIONS_MAX_CHESTS 64u  /* ordinary chests named on one fill request */
/* KilnStationRequest through reserved[4]. A caller built before ordinary chests passes this size and names none. */
#define KILN_STATION_REQUEST_V1 88u
#define KILN_STATIONS_MAX_CRAFT_STOCK 1024u /* rows in a Kiln client's crafting-stock snapshot (set_craft_stock) */
#define KILN_STATIONS_MAX_FILL_KEYS 16u /* fills a Kiln client asks about, and answers it keeps (since 0.2.0-dev.29.19) */

/* A base's id: the 16 bytes of the game's BaseId (a GUID, saved with the altar). All zero: no altar was found. */
typedef struct KilnBaseId {
    uint8_t guid[16];
} KilnBaseId;

/* One category choice of a recipe (the game's RecipeInputCategorySelection, 8 bytes): for the input "any item of
 * category X", the option at `index` of that category. */
typedef struct KilnStationChoice {
    uint32_t category;
    uint8_t index;
    uint8_t reserved[3]; /* 0 */
} KilnStationChoice;

typedef struct KilnStationStack {
    KilnItemId item;
    uint32_t count;
} KilnStationStack;

/* A fill the station screen asks the game about (since 0.2.0-dev.29.19): the station, the recipe and its category
 * choices as the screen passes them (a choice's reserved bytes are 0 here whatever the game held). 40 bytes. */
typedef struct KilnStationFillKey {
    KilnEntity station;
    uint32_t recipe;
    KilnStationChoice choices[4];
} KilnStationFillKey;

/* The server's answer for one key: how many more complete crafts its fill with opted-in station outputs covers now
 * (0: none). 48 bytes. */
typedef struct KilnStationFillAnswer {
    KilnStationFillKey key;
    uint32_t crafts;
    uint32_t reserved; /* 0 */
} KilnStationFillAnswer;

/* KilnStation.state: the game's FactoryStationState */
enum {
    KILN_STATION_STATE_NO_RECIPE = 0,
    KILN_STATION_STATE_MISSING_MATERIALS = 1,
    KILN_STATION_STATE_PAUSED = 2,   /* no water under it / not enough energy (the game pauses the cycle) */
    KILN_STATION_STATE_STOPPED = 3,  /* the stop button (isStoppedByPlayer), or Kiln's stop */
    KILN_STATION_STATE_RUNNING = 4,
    KILN_STATION_STATE_OUTPUT_FULL = 5, /* the cycle ended and its outputs do not fit: retried every step */
};

/* KilnStation.needs: the game's missingRequirements (bit = FactoryStationRequirement) */
enum {
    KILN_STATION_NEEDS_RECIPE = 0x01u,
    KILN_STATION_NEEDS_ENERGY_SOURCE = 0x02u,
    KILN_STATION_NEEDS_ENERGY = 0x04u,
    KILN_STATION_NEEDS_INPUTS = 0x08u,
    KILN_STATION_NEEDS_OUTPUT_SPACE = 0x10u,
    KILN_STATION_NEEDS_ABOVE_WATER = 0x20u,
};

typedef struct KilnStationBase {
    uint32_t size;          /* set to sizeof(KilnStationBase) before calling */
    uint32_t index;         /* the game's record index this session (0..15): changes when the bases are rebuilt */
    KilnBaseId id;          /* the smallest altar baseId of the record */
    uint32_t altar_count;   /* altars whose zones make this base (more than one when their zones overlap) */
    KilnEntity altars[KILN_STATIONS_MAX_ALTARS];   /* their entities (this session) */
    KilnBaseId altar_ids[KILN_STATIONS_MAX_ALTARS]; /* their own baseId, kept in the save: a stable key per altar */
    uint32_t storage_count; /* crafting-stock containers the game lists for the base now */
    uint32_t water_count;   /* water sources that hold water */
    uint32_t zone_count;    /* altar zone boxes merged into the base */
    uint32_t station_count; /* production stations whose position is in the base */
    uint32_t version;       /* Kiln's count of base rebuilds this session (it moves when the records change) */
    KilnVec3 min, max;      /* the record's box, metres */
    uint32_t reserved[4];
} KilnStationBase;

typedef struct KilnStation {
    uint32_t size;          /* set to sizeof(KilnStation) before calling */
    KilnEntity entity;
    uint32_t workshop;      /* Crafting.workshopId: which recipes it runs */
    int32_t base_index;     /* -1: in no base */
    KilnBaseId base;        /* zero: in no base */
    KilnVec3 position;
    uint8_t state;          /* KILN_STATION_STATE_* */
    uint8_t stopped_by_player; /* FactoryStation.isStoppedByPlayer */
    uint8_t use_all_options;   /* FactoryStation.useAllAvailableCategoryIngredients */
    uint8_t needs;          /* KILN_STATION_NEEDS_* */
    uint32_t running_recipe;  /* the cycle in progress (0: none) */
    uint32_t selected_recipe; /* the recipe the station repeats (FactoryStation.nextRecipe; 0: none) */
    KilnStationChoice running_choices[4], selected_choices[4];
    uint32_t crafts_loaded; /* complete crafts of the selected recipe its inputs hold now (the game's count) */
    int64_t recipe_start_ns; /* the running cycle's start, world program time */
    int64_t pause_ns;        /* FactoryStation.recipePauseDuration */
    int64_t now_ns;          /* the world's program time of the last step (0 before kiln_items_stations ran) */
    int64_t duration_ns;     /* the cycle in progress' duration (running_recipe != 0), else the selected recipe's; scaled by
                                factoryProductionSpeedFactor */
    float progress;          /* 0..1 of the cycle in progress (while paused: where it paused), 0 when none runs */
    int32_t water, water_max; /* the station's water (Fuel units, 64 per charge; 0, 0 for a station without water) */
    uint32_t flags;          /* KILN_STATION_FLAG_* */
    uint32_t reserved[4];
} KilnStation;

/* KilnStation.flags */
enum {
    KILN_STATION_FLAG_STOP_PENDING = 0x1u, /* a STOP with KILN_STATION_AFTER_CRAFT waits for the cycle's end (not saved) */
};

/* One recipe input as the data has it: an item (item != 0) or "any item of a category" (category != 0). */
typedef struct KilnStationInput {
    KilnItemId item;
    uint32_t count;
    uint32_t category;
    uint32_t category_count;
} KilnStationInput;

typedef struct KilnStationRecipe {
    uint32_t size;          /* set to sizeof(KilnStationRecipe) before calling */
    uint32_t id;
    uint32_t workshop;
    uint32_t input_count;   /* entries in inputs (at most 32) */
    uint32_t output_count;  /* entries in outputs (at most 8 copied) */
    uint32_t water_charges; /* RecipeInfo.waterCharges */
    int64_t duration_ns;    /* RecipeInfo.craftingDuration (not scaled) */
    uint32_t knowledge_id;  /* RecipeInfo.knowledgeRequirement (the unlock; id 0: always unlocked) */
    uint32_t knowledge_value;
    uint8_t knowledge_compare, knowledge_type, knowledge_player_only, reserved0;
    uint32_t reserved1;
    KilnStationInput inputs[KILN_STATIONS_MAX_INPUTS];
    KilnStationStack outputs[KILN_STATIONS_MAX_OUTPUTS];
} KilnStationRecipe;

/* KilnStationRequest.action */
enum {
    KILN_STATION_FILL = 1,   /* the game's fill from the base's stock containers */
    KILN_STATION_STOP = 2,   /* the stop button */
    KILN_STATION_RESUME = 3, /* clears the stop (the station starts a new cycle) */
    KILN_STATION_SELECT = 4, /* the recipe the station repeats, with its category choices */
    KILN_STATION_COLLECT = 5, /* finished stacks into a container or the base's Magic chests (collect()) */
};

/* KilnStationRequest.flags */
enum {
    KILN_STATION_FILL_ADD = 0x0001u,     /* FILL: crafts = this many more complete crafts than are loaded (the game's
                                            meaning); default: crafts = how many to have loaded in all */
    KILN_STATION_FILL_EXACT = 0x0002u,   /* FILL: all of it or nothing (no smaller amount) */
    KILN_STATION_DRY_RUN = 0x0004u,      /* FILL: run the game's function and roll back: says what would move */
    KILN_STATION_NO_WATER = 0x0008u,     /* FILL: pour no water (waterCharges 0) */
    KILN_STATION_AFTER_CRAFT = 0x0010u,  /* STOP: when the running cycle has ended (nothing is refunded or lost) */
    KILN_STATION_KEEP_STOPPED = 0x0020u, /* SELECT: change the recipe, leave a stopped station stopped */
    KILN_STATION_ALLOW_LOCKED = 0x0040u, /* SELECT: also a recipe no one on the server has unlocked */
    KILN_STATION_FILL_NO_OUTPUTS = 0x0080u, /* FILL: never take from the outputs of opted-in stations (the game's sources
                                               only; since 0.2.0-dev.29.17) */
    KILN_STATION_FILL_LINKED = 0x0100u,  /* FILL (since 0.2.0-dev.31.12, GitHub #132): also take from the named player's
                                            linked crafting sources registered with KILN_CRAFT_SOURCE_FILLS, in any base or
                                            none: after the bag, the Magic chests and the named chests; a link's order puts
                                            it before or after opted-in station outputs. Needs a player. Each link is
                                            revalidated in the transaction (the player's CanAccessInventories and login, an
                                            initialized container that is no person and no production station, its item
                                            filter); a container is taken from once per item */
    KILN_STATION_FILL_ITEM = 0x0200u,    /* FILL (since 0.2.0-dev.31.16, GitHub #132; only where fill_flags() names it): the
                                            station screen's per-ingredient "+1" / "+n", the game's AutoTransferItem
                                            (docs/re/stations.md section 10.9): exactly `crafts` (1..65535) of `item` into
                                            the station, all or nothing, in one transaction: the player's bag and the
                                            base's Magic chests first (the game's own call, the player's inventories first),
                                            then, with KILN_STATION_FILL_LINKED, the player's fill-opted links
                                            (BeforeStationOutputs links before AfterStationOutputs links) for what those
                                            cannot cover. recipe 0, no choices, no keep, no chests; no water and no station
                                            outputs. ADD, EXACT, NO_WATER and NO_OUTPUTS are implied (and accepted). The plan
                                            has one input {item, 1}; its crafts_* and the result's crafts_* count items */
};

typedef struct KilnStationRequest {
    uint32_t size;          /* sizeof(KilnStationRequest), or KILN_STATION_REQUEST_V1 (no chests). Since 0.2.0-dev.29.17
                               exactly one of those two: 29.16 also took any larger size and read the first 88 bytes */
    uint32_t action;        /* KILN_STATION_FILL .. SELECT (COLLECT is collect()'s, not submit's) */
    uint32_t flags;         /* KILN_STATION_* request flags */
    KilnEntity station;
    uint32_t crafts;        /* FILL: 1..1000 */
    uint32_t recipe;        /* SELECT: the recipe; FILL: 0 = the station's selected recipe (else that one) */
    KilnStationChoice choices[4]; /* SELECT, FILL with a recipe: the category choices; all zero = the station's own */
    KilnEntity player;      /* FILL: also take from this player's inventories first, as the game's button does (a player
                               entity, 1..1023); 0 = never */
    uint32_t keep_count;    /* FILL: entries in keep (0..64) */
    const KilnStationStack* keep; /* FILL: leave at least this many of each item in the base's stock (copied by submit) */
    KilnItemId item;        /* FILL with KILN_STATION_FILL_ITEM: the item to move (since 0.2.0-dev.31.16; reserved[0]
                               before, so 0 everywhere else) */
    uint32_t reserved[3];   /* 0 */
    /* since ordinary chests (a request of KILN_STATION_REQUEST_V1 bytes has none of these; submit copies the list): */
    uint32_t chest_count;   /* FILL: 0..KILN_STATIONS_MAX_CHESTS. 0: the game's sources and station outputs only */
    uint32_t chest_reserved; /* 0 */
    const KilnEntity* chests; /* the entities, copied by submit; NULL when chest_count is 0. Not a player (1..1023), not
                                 the station. Whether each is an ordinary chest of this base is checked at the fill. */
} KilnStationRequest;

/* KilnStationResult.status (also KilnStationPlan.status) */
enum {
    KILN_STATION_DONE = 0,          /* it happened */
    KILN_STATION_PARTIAL = 1,       /* FILL: fewer crafts than asked (the stock or the station's room) */
    KILN_STATION_NOT_ENOUGH = 2,    /* FILL: the base's stock (after the reserves) covers not even one craft */
    KILN_STATION_NO_ROOM = 3,       /* FILL: the station has no room for one more craft's inputs */
    KILN_STATION_NOT_A_STATION = 4, /* the entity has no FactoryStation (gone, or never one) */
    KILN_STATION_NO_BASE = 5,       /* FILL: the station stands in no base */
    KILN_STATION_NO_RECIPE = 6,     /* FILL: no recipe selected (or the recipe is unknown) */
    KILN_STATION_WRONG_WORKSHOP = 7,/* the recipe is not one of this station's workshop */
    KILN_STATION_UNCHANGED = 8,     /* nothing to do: already stopped / running / selected / loaded */
    KILN_STATION_LOCKED = 9,        /* SELECT: no one on the server has unlocked the recipe (message says how it was checked) */
    KILN_STATION_REFUSED = 10,      /* the game's function refused it (game_error) */
    KILN_STATION_UNAVAILABLE = 11,  /* the capability went off after the request was accepted */
    KILN_STATION_DROPPED = 12,      /* not run: the world closed, or kiln_items_stations did not run it in 10 s */
    KILN_STATION_TIMED_OUT = 13,    /* STOP after craft: the cycle did not end in time (about an hour of steps); nothing written */
    KILN_STATION_QUEUE_FULL = 14,   /* not taken: 1024 actions wait (submit's KILN_ERR_BUFFER_TOO_SMALL; a managed result) */
    KILN_STATION_CANCELED = 15,     /* STOP after craft: withdrawn before it was written, by a later RESUME, SELECT or STOP
                                       (not after craft) of kiln.stations' on the station that passed its checks (that
                                       action's message says it withdrew it), or because the station's selected recipe
                                       changed (a player's or a mod's select); nothing written. A stop meanwhile (a
                                       player's too) answers UNCHANGED instead */
};

/* KilnStationResult.limited_by / KilnStationPlan.limited_by: what kept a FILL below what was asked */
enum {
    KILN_STATION_LIMIT_NONE = 0,
    KILN_STATION_LIMIT_STOCK = 1,   /* the base's stock (short_item) */
    KILN_STATION_LIMIT_RESERVE = 2, /* the request's reserve of short_item */
    KILN_STATION_LIMIT_ROOM = 3,    /* the station's room */
    KILN_STATION_LIMIT_WATER = 4,   /* water: the base's water sources, or the station's water capacity */
};

typedef struct KilnStationResult {
    uint32_t size;          /* sizeof(KilnStationResult) of kiln.stations */
    int32_t status;         /* KILN_STATION_DONE .. CANCELED */
    uint32_t action;
    uint32_t flags;         /* the request's flags; COLLECT: the KilnStationCollect's flags (KILN_STATION_COLLECT_*, which
                               share no bit with the request flags: a collect never has KILN_STATION_DRY_RUN) */
    KilnEntity station;
    uint32_t recipe;        /* FILL / SELECT: the recipe; COLLECT: the station's selected recipe */
    uint32_t crafts_requested; /* FILL: what the request asked (after the UpTo arithmetic: crafts to add) */
    uint32_t crafts_before, crafts_after; /* FILL: complete crafts loaded before and after */
    uint32_t crafts_added;  /* FILL: the n the game's function ran with */
    int32_t game_error;     /* FILL: AutoTransferRecipe's code of the last attempt (1 no inputs or water, 2 no room in the
                               station, 4 missing materials, 5 no station inventory), 0 */
    uint32_t change_count;  /* entries in changes */
    uint8_t state_before, state_after, stopped_before, stopped_after;
    uint8_t limited_by;     /* FILL, not DONE: KILN_STATION_LIMIT_* */
    uint8_t reserved8[3];
    KilnItemId short_item;  /* FILL, limited by STOCK or RESERVE: the item; 0 otherwise */
    uint32_t from_outputs;  /* FILL: items taken from opted-in stations' outputs (since 0.2.0-dev.29.17; 0 before) */
    uint64_t ticket, step;
    KilnItemsMoveChange changes[KILN_STATIONS_MAX_CHANGES]; /* FILL: the transaction's ledger (a take: from_slot set, to_slot
                               0; a put into the station: to_slot set, from_slot 0) */
    char message[128];      /* why, in words, for anything but DONE; NUL-terminated */
    /* since ordinary chests: read these only when size >= offsetof(KilnStationResult, reserved_tail) + 4 (a 29.16
       kiln.stations passes a 1232-byte result, which ends at message): */
    uint32_t from_chests;   /* FILL: items taken from the named ordinary chests; 0 when they were not needed */
    uint32_t reserved_tail; /* 0 */
    /* since 0.2.0-dev.31.12: read only when size >= offsetof(KilnStationResult, reserved_linked) + 4 (an older
       kiln.stations passes a 1240-byte result) */
    uint32_t from_linked;     /* FILL with KILN_STATION_FILL_LINKED: items taken from the player's linked sources */
    uint32_t reserved_linked; /* 0 */
} KilnStationResult;

typedef void(KILN_CALL* KilnStationDoneFn)(const KilnStationResult* result, void* user);

/* What a fill would do now (plan(), tick thread; no game function runs). */
typedef struct KilnStationPlan {
    uint32_t size;          /* set to sizeof(KilnStationPlan) before calling */
    int32_t status;         /* KILN_STATION_DONE (crafts_planned > 0), UNCHANGED, NOT_ENOUGH, NO_BASE, NO_RECIPE, ... */
    KilnEntity station;
    uint32_t recipe;
    int32_t base_index;
    uint32_t crafts_loaded;     /* complete crafts the station holds now */
    uint32_t crafts_affordable; /* more crafts the base's stock covers (after the reserves) */
    uint32_t crafts_planned;    /* what a fill with this request would ask the game for */
    uint32_t input_count;       /* resolved inputs (the category choices applied) */
    KilnItemId short_item;      /* limited by STOCK or RESERVE: the item; 0 otherwise */
    uint8_t limited_by;         /* KILN_STATION_LIMIT_* when crafts_planned is below what was asked */
    uint8_t use_all_options;    /* the station has "use all ingredient options" on: it may consume other options */
    uint8_t reserved8[2];
    uint32_t water_per_craft;   /* Fuel units one craft needs (0: none, or NO_WATER) */
    uint32_t water_have;        /* in the station now */
    uint32_t water_stock;       /* in the base's water sources */
    KilnStationStack inputs[KILN_STATIONS_MAX_INPUTS]; /* per craft */
    uint32_t have[KILN_STATIONS_MAX_INPUTS];           /* in the station now */
    uint32_t stock[KILN_STATIONS_MAX_INPUTS];          /* in the base's stock containers, after the reserves (plus the
                                                          named player's inventories) */
    char message[128];
    /* since 0.2.0-dev.29.17 (a caller's smaller size, up to message, still works and gets no more): */
    uint32_t outputs[KILN_STATIONS_MAX_INPUTS];        /* what opted-in stations of the base lend of each input (their
                                                          Outputs); crafts_affordable counts it, stock does not */
    /* since ordinary chests (a caller's size up to outputs still works and gets no chests[]): */
    uint32_t chests[KILN_STATIONS_MAX_INPUTS];         /* what the named ordinary chests hold of each input; taken after
                                                          stock and before outputs. crafts_affordable counts it */
    /* since 0.2.0-dev.31.12 (a caller's size up to chests still works and gets no linked[]): */
    uint32_t linked[KILN_STATIONS_MAX_INPUTS];         /* KILN_STATION_FILL_LINKED: what the named player's fill-opted
                                                          linked sources hold of each input (after chests; before or
                                                          after outputs by each link's order). crafts_affordable counts it */
} KilnStationPlan;

/* KilnStationEvent.kind */
enum {
    KILN_STATION_EV_STATE = 1,   /* state, stop flag, selected or running recipe changed (seen in Pre) */
    KILN_STATION_EV_PLACED = 2,  /* a station appeared (placed, or loaded) */
    KILN_STATION_EV_REMOVED = 3, /* a station is gone */
    KILN_STATION_EV_BASES = 4,   /* the game rebuilt its bases (an altar placed, removed or upgraded) */
};

typedef struct KilnStationEvent {
    uint32_t size;
    uint32_t kind;          /* KILN_STATION_EV_* */
    uint64_t seq;           /* increasing; events(after = the last seq you saw) */
    uint64_t step;
    KilnEntity station;
    uint32_t workshop;
    uint8_t state_before, state_after, stopped_before, stopped_after;
    uint32_t selected_before, selected_after;
    uint32_t running_before, running_after;
    int32_t base_index;     /* this session's record index; -1: in no base */
    uint32_t reserved0;
    KilnOwner by;           /* STATE: the owner whose kiln.stations request made exactly this change (its stop flag or its
                               selection) in the step before; 0 = the game, a player, or a change the game made after it */
    KilnVec3 position;
    KilnBaseId base;        /* the base's id (zero: in no base) */
} KilnStationEvent;

typedef struct KilnStationsStatus {
    uint32_t size;          /* set to sizeof(KilnStationsStatus) before calling */
    uint8_t read;           /* 1: reads work here (stations.read and a server world) */
    uint8_t fill;           /* 1: FILL runs (stations.fill, the program verified) */
    uint8_t control;        /* 1: STOP / RESUME / SELECT run (stations.control, the program verified) */
    uint8_t events;         /* 1: watch() works */
    uint8_t program;        /* KILN_ITEMS_PROGRAM_* of kiln_items_stations */
    uint8_t watching;       /* 1: a watch is on */
    uint8_t knowledge;      /* 1: kiln.knowledge is here (SELECT's unlock check) */
    uint8_t scanned;        /* 1: the first scan of the world's stations is done (stations() is complete from then on) */
    uint32_t bases;         /* the game's bases now */
    uint32_t stations;      /* stations seen at the last scan */
    uint32_t pending;       /* requests waiting */
    uint32_t done;          /* requests answered so far */
    char detail[160];       /* why something is off, in words; "" when all works */
} KilnStationsStatus;

typedef struct KilnStationCollect KilnStationCollect;

/* Player-specific linked storage (since 0.2.0-dev.30.6). The mod chooses the containers; the server enforces the
 * player's own CanAccessInventories permission and a readable initialized container, not membership of a base.
 * order: 0 before station outputs, 1 after. filter: 0 deny set, 1 allow set. Empty deny set allows every item.
 * items is copied at registration; no callbacks run during native payment. All reserved fields must be zero. */
enum { KILN_CRAFT_SOURCE_STOCK_ALL = 1u }; /* source_stock: include overlaps with bag/base/station stock */
/* source_stock (since 0.2.0-dev.31.12, GitHub #147): only the registrations made with KILN_CRAFT_SOURCE_LOCKS, every
 * container once per item, the filter applied, no base and no bag; KILN_CRAFT_SOURCE_STOCK_ALL is implied. */
enum { KILN_CRAFT_SOURCE_STOCK_LOCKS = 2u };
/* KilnCraftSourceRequest.flags (since 0.2.0-dev.31.12, GitHub #147): the link also counts and pays the item an
 * InteractionLock requires (capability items.lock_sources). Unknown bits: KILN_ERR_INVALID_ARGUMENT. */
enum { KILN_CRAFT_SOURCE_LOCKS = 1u };
/* Since 0.2.0-dev.31.12 (GitHub #132): FILLS, production-station fills named with KILN_STATION_FILL_LINKED take from the
 * link (the station screen's "+1" / "+10" through Kiln.Blocks); BUILDING, the player's block placements count and pay
 * from it (capability crafting.build_sources). Both default off: a link serves crafting only. */
enum { KILN_CRAFT_SOURCE_FILLS = 2u, KILN_CRAFT_SOURCE_BUILDING = 4u };
/* source_stock: only the FILLS / BUILDING links, as KILN_CRAFT_SOURCE_STOCK_LOCKS does for locks (since 0.2.0-dev.31.12).
 * Both leave out the Magic chests of the base the player stands in (FILLS since 0.2.0-dev.31.16): the bag and those chests
 * pay first and are counted apart, so a link to one of them never counts twice. */
enum { KILN_CRAFT_SOURCE_STOCK_FILLS = 4u, KILN_CRAFT_SOURCE_STOCK_BUILDING = 8u };
/* Since 0.2.0-dev.31.18 (GitHub #150): RESPEC, the skill tree's paid reset and a node's unlearn count and pay their
 * respec item (the game's Runes) from the link (capability crafting.respec_sources). Default off: a link serves crafting
 * only. source_stock with KILN_CRAFT_SOURCE_STOCK_RESPEC: one row, the respec item and what the RESPEC links hold of it
 * (0 when none), without the Magic chests of the base the player stands in (the bag and those chests pay first and are
 * counted apart, as for BUILDING); KILN_ERR_UNAVAILABLE where crafting.respec_sources did not resolve. */
enum { KILN_CRAFT_SOURCE_RESPEC = 8u };
enum { KILN_CRAFT_SOURCE_STOCK_RESPEC = 16u };
/* The request's size before flags: an older caller's request (no flags, no lock use). */
#define KILN_CRAFT_SOURCE_REQUEST_V1 32u
typedef struct KilnCraftSourceRequest {
    uint32_t size;
    KilnEntity player;
    KilnEntity container;
    uint32_t order;
    uint32_t filter;
    uint32_t item_count;
    const KilnItemId* items;
    /* ---- since 0.2.0-dev.31.12: read only when size covers them ---- */
    uint32_t flags;    /* KILN_CRAFT_SOURCE_* */
    uint32_t reserved; /* 0 */
} KilnCraftSourceRequest;

/* A Kiln client's lock snapshot (GitHub #147): rows the server sends for the lock items this client asked about, at
 * most KILN_STATIONS_MAX_LOCK_STOCK; the items the client's lock checks met with no row, at most
 * KILN_STATIONS_MAX_LOCK_ASKS. */
#define KILN_STATIONS_MAX_LOCK_STOCK 256u
#define KILN_STATIONS_MAX_LOCK_ASKS 16u
/* A Kiln client's respec snapshot (GitHub #150): rows the server sends for its respec item, at most this many. */
#define KILN_STATIONS_MAX_RESPEC_STOCK 16u

typedef struct KilnStationsApi {
    KilnServiceHeader header; /* { sizeof(KilnStationsApi), KILN_STATIONS_VERSION } */

    KilnResult(KILN_CALL* status)(KilnStationsStatus* out);
    /* The game's bases. base_at: index 0..count-1. base_of: the base the entity stands in (KILN_ERR_NOT_FOUND: none). */
    KilnResult(KILN_CALL* base_count)(uint32_t* out);
    KilnResult(KILN_CALL* base_at)(uint32_t index, KilnStationBase* out);
    KilnResult(KILN_CALL* base_of)(KilnEntity entity, KilnStationBase* out);
    KilnResult(KILN_CALL* base_at_position)(const KilnVec3* position, KilnStationBase* out);
    /* A base's crafting-stock containers / water sources in the game's order; its stock summed per item (stock
     * containers only). KILN_ERR_BUFFER_TOO_SMALL: *out_count = what is needed. */
    KilnResult(KILN_CALL* base_storage)(uint32_t index, KilnEntity* out, uint32_t capacity, uint32_t* out_count);
    KilnResult(KILN_CALL* base_water)(uint32_t index, KilnEntity* out, uint32_t capacity, uint32_t* out_count);
    KilnResult(KILN_CALL* base_stock)(uint32_t index, KilnStationStack* out, uint32_t capacity, uint32_t* out_count);
    /* Production stations: base_index -1 = all, else those in that base. A scan of the world's entities at most every 32
     * steps (or on demand when older). */
    KilnResult(KILN_CALL* stations)(int32_t base_index, KilnEntity* out, uint32_t capacity, uint32_t* out_count);
    /* KILN_ERR_NOT_FOUND: the entity has no FactoryStation. */
    KilnResult(KILN_CALL* station_read)(KilnEntity station, KilnStation* out);
    /* Recipes: KILN_ERR_NOT_FOUND for an unknown id. recipe_inputs: the game's own resolution with choices (NULL or all
     * zero: index 0 of every category), counts scaled as the game scales them (ceil(count x option.scaling)). */
    KilnResult(KILN_CALL* recipe)(uint32_t id, KilnStationRecipe* out);
    KilnResult(KILN_CALL* recipe_inputs)(uint32_t recipe, const KilnStationChoice* choices4, KilnStationStack* out,
                                         uint32_t capacity, uint32_t* out_count);
    /* Ingredient categories (22 in the game's data): the options of one (item, scaling); category_at walks them. */
    KilnResult(KILN_CALL* category)(uint32_t id, KilnItemId* items, float* scaling, uint32_t capacity, uint32_t* out_count);
    KilnResult(KILN_CALL* category_at)(uint32_t index, uint32_t* out_id);
    /* The recipes of a workshop (a station's Crafting.workshopId). */
    KilnResult(KILN_CALL* workshop_recipes)(uint32_t workshop, uint32_t* out, uint32_t capacity, uint32_t* out_count);
    /* What a FILL request would do now (no game function runs). KILN_ERR_UNAVAILABLE for KILN_STATION_FILL_ITEM where
     * fill_flags() does not name it. */
    KilnResult(KILN_CALL* plan)(const KilnStationRequest* request, KilnStationPlan* out);
    /* Queues an action. KILN_ERR_INVALID_ARGUMENT: bad size, action, flags or reserved fields, station 0, crafts outside
     * 1..1000 (1..65535 with KILN_STATION_FILL_ITEM), keep_count over 64, an item on anything but an item fill. KILN_ERR_UNAVAILABLE: the capability is off or no server world here.
     * KILN_ERR_BUFFER_TOO_SMALL: 1024 actions wait. KILN_OK: `done` (may be NULL) runs once. */
    KilnResult(KILN_CALL* submit)(KilnOwner owner, const KilnStationRequest* request, KilnStationDoneFn done, void* user,
                                  uint64_t* out_ticket);
    /* Drops an action's callback (the action may still run). KILN_ERR_NOT_FOUND: no such ticket waiting. */
    KilnResult(KILN_CALL* cancel)(uint64_t ticket);
    /* Events are collected while any watch is on (each on() needs an off()); they cost a scan every 32 steps and a read
     * of every station each step. */
    KilnResult(KILN_CALL* watch)(uint32_t on);
    /* Events with seq > after, oldest first (the last 1024 are kept). KILN_ERR_BUFFER_TOO_SMALL: more wait. */
    KilnResult(KILN_CALL* events)(uint64_t after, KilnStationEvent* out, uint32_t capacity, uint32_t* out_count);
    uint64_t(KILN_CALL* last_seq)(void);
    /* ---- since 0.2.0-dev.29.17 (header.size tells whether these are here) ---- */
    /* Station outputs as crafting stock: on = 1 opts the station in (its Outputs become the last source of every FILL in
     * its base), 0 opts it out. Any thread. KILN_ERR_UNAVAILABLE: stations.output_sources is off; KILN_ERR_INVALID_ARGUMENT:
     * station 0 or on > 1; KILN_ERR_BUFFER_TOO_SMALL: 4096 stations are opted in already. The set is this session's and
     * is cleared when the server world closes. Whether the entity is a station is checked at each fill, not here. */
    KilnResult(KILN_CALL* set_output_source)(KilnEntity station, uint32_t on);
    /* The opted-in stations, in the order a fill uses them (entity order). KILN_ERR_BUFFER_TOO_SMALL: *out_count = needed. */
    KilnResult(KILN_CALL* output_sources)(KilnEntity* out, uint32_t capacity, uint32_t* out_count);
    /* ---- ordinary chests on a FILL (header.size tells whether this is here) ---- */
    /* KILN_STATIONS_MAX_CHESTS. A smaller table has no slot. Any thread. */
    uint32_t(KILN_CALL* max_chests)(void);
    /* ---- collect (header.size tells whether this is here) ---- */
    /* Moves a station's finished stacks, each through kiln.items' move (the game's slot transfer; KilnStationCollect has
     * the rules). Joins the same action queue as submit (1024 waiting; KILN_ERR_BUFFER_TOO_SMALL when it is full). Any
     * thread; `done` (may be NULL) runs once, on the tick thread. KILN_ERR_INVALID_ARGUMENT: a bad request.
     * KILN_ERR_UNAVAILABLE: no server world, stations.read is off, items.move is off, or a fault switched the service's
     * actions off. */
    KilnResult(KILN_CALL* collect)(KilnOwner owner, const KilnStationCollect* request, KilnStationDoneFn done, void* user,
                                   uint64_t* out_ticket);
    /* ---- stock plus finished station outputs (header.size tells whether this is here) ---- */
    /* base_stock plus the finished outputs still in this base's production stations (Bases.StockWithStations).
     * Tick thread. KILN_ERR_BUFFER_TOO_SMALL: *out_count = what is needed. */
    KilnResult(KILN_CALL* base_stock_with_stations)(uint32_t index, KilnStationStack* out, uint32_t capacity,
                                                    uint32_t* out_count);
    /* ---- the crafting menu's snapshot for Kiln clients, GitHub #68 step 2 (header.size tells whether these are here) ---- */
    /* A Kiln client's snapshot of the crafting stock its crafting menu may count: per item, the player's bag, the base's
     * Magic chests and the opted-in stations' Outputs, for the items craft_items names. count 0 clears it (the game's own
     * counts again). A row whose count is 0 names its item with none: the snapshot stays on and that item keeps the game's
     * counts (since 0.2.0-dev.30.8; before, such a row was dropped). Any thread. The server's payment never reads it.
     * KILN_ERR_INVALID_ARGUMENT: count over KILN_STATIONS_MAX_CRAFT_STOCK, or rows null with count set. */
    KilnResult(KILN_CALL* set_craft_stock)(const KilnStationStack* rows, uint32_t count);
    /* The items a Kiln client must have counted to raise a recipe's count with station outputs: every input (an item, or
     * every option of a category input) of every recipe that takes one of `items` (the lent outputs), and `items`
     * themselves; sorted. A recipe that takes none of them keeps the game's own count. Server world, tick thread.
     * KILN_ERR_BUFFER_TOO_SMALL: *out_count = needed. */
    KilnResult(KILN_CALL* craft_items)(const KilnItemId* items, uint32_t count, KilnItemId* out, uint32_t capacity,
                                       uint32_t* out_count);
    /* ---- the station screen's fill buttons for Kiln clients, GitHub #68 (since 0.2.0-dev.29.19; header.size tells) ---- */
    /* The server's answers for the fills this client asked about; count 0 clears them. They count only while the
     * craft-stock snapshot is on (set_craft_stock with rows; clearing it clears these and the asks too). Any thread.
     * KILN_ERR_INVALID_ARGUMENT: count over KILN_STATIONS_MAX_FILL_KEYS, rows null with count set, a reserved byte set. */
    KilnResult(KILN_CALL* set_fill_answers)(const KilnStationFillAnswer* rows, uint32_t count);
    /* The fills the station screen asked the game about while the snapshot was on, that the game refused for missing
     * materials and that no answer names: at most KILN_STATIONS_MAX_FILL_KEYS, oldest first. They stay until an answer
     * names them or the snapshot goes. Any thread. KILN_ERR_BUFFER_TOO_SMALL: *out_count = needed. */
    KilnResult(KILN_CALL* fill_asks)(KilnStationFillKey* out, uint32_t capacity, uint32_t* out_count);
    /* ---- player crafting sources, since 0.2.0-dev.30.6; capability crafting.sources ---- */
    /* Tick thread, server world. Rejects non-containers, players/NPCs, production stations (FactoryStation), and
     * inventory links without a container setup. Production stations lend their outputs through set_output_source.
     * Up to 4096 registrations and 65536 filter items per registration. Returns an owner-bound disposable handle.
     * Registrations are session-only: after restart the mod must register them again. */
    KilnResult(KILN_CALL* add_source)(KilnOwner owner, const KilnCraftSourceRequest* request, uint64_t* out_handle);
    /* Any thread. NOT_FOUND for a removed handle or another owner's handle. In-flight payments may finish. */
    KilnResult(KILN_CALL* remove_source)(KilnOwner owner, uint64_t handle);
    /* Any thread: remove this owner's registrations (also called by kiln.items.cancel_owner). */
    void(KILN_CALL* cancel_sources)(KilnOwner owner);
    /* Tick thread: the registered sources' filtered extra counts, beyond the bag, current base stock and opted-in
     * station outputs. No base required. Access is checked again, never inferred from a client's message.
     * flags 0: crafting extras; KILN_CRAFT_SOURCE_STOCK_ALL: all registered counts (e.g. for dyes); KILN_CRAFT_SOURCE_STOCK_LOCKS:
 * the lock-opted registrations' counts (since 0.2.0-dev.31.12). Unknown bits: INVALID_ARGUMENT.
     * BUFFER_TOO_SMALL reports the whole needed count: never send a truncated crafting snapshot. */
    KilnResult(KILN_CALL* source_stock)(KilnEntity player, uint32_t flags, KilnStationStack* out, uint32_t capacity, uint32_t* out_count);
    /* Since 0.2.0-dev.30.6: a Kiln server's supported dye-source announcement for the local player. Client only,
     * any thread; at most 256 distinct item/count rows, copied. player 0 or count 0 clears. The client shows these
     * only in its tinting screen, for at most 15 seconds without a refresh. Counts are not authority: the server's
     * Tint entry and payment re-resolve/recheck the shared per-player registrations, access and filters at use time.
     * UNAVAILABLE: wrong side, no client world, unqualified wire/palette hooks. INVALID_ARGUMENT: malformed rows.
     * This is Kiln's own transport bridge, not a way for a mod to grant access to a source. */
    KilnResult(KILN_CALL* set_dye_stock)(KilnEntity player, const KilnStationStack* rows, uint32_t count);
    /* ---- locks from linked sources, since 0.2.0-dev.31.12 (GitHub #147; capability items.lock_sources; header.size
     * tells whether these are here) ---- */
    /* A Kiln client: the server's lock snapshot for the local player. on 1: the server counts and pays lock items from
     * the player's opted-in links (KILN_CRAFT_SOURCE_LOCKS); rows are per item the count those links hold (the bag not
     * included; a row of 0 names an item the links do not hold). on 0 clears it (rows ignored): the game's own lock
     * checks again. While on, the client's lock checks count the bag plus the row of the lock's item. Not authority: the
     * server checks the interaction again and pays from the bag first. Any thread. KILN_ERR_INVALID_ARGUMENT: on over
     * 1, count over KILN_STATIONS_MAX_LOCK_STOCK, rows null with count set, an item 0. */
    KilnResult(KILN_CALL* set_lock_stock)(uint32_t on, const KilnStationStack* rows, uint32_t count);
    /* A Kiln client: the lock items its lock checks (the press and the locked prompt) met while the snapshot was on and
     * that no row names, oldest first, at most KILN_STATIONS_MAX_LOCK_ASKS. They stay until a row names them or the
     * snapshot goes. Any thread. KILN_ERR_BUFFER_TOO_SMALL: *out_count = needed. */
    KilnResult(KILN_CALL* lock_asks)(KilnItemId* out, uint32_t capacity, uint32_t* out_count);
    /* ---- block placements from linked sources, since 0.2.0-dev.31.12 (GitHub #132; capability crafting.build_sources;
     * header.size tells whether this is here) ---- */
    /* A Kiln client: the server's building snapshot for the local player. on 1: the server counts and pays block
     * placements from the player's links registered with KILN_CRAFT_SOURCE_BUILDING; rows are per item the count those
     * links hold (the bag and the base not included), at most KILN_STATIONS_MAX_CRAFT_STOCK. on 0 clears it (rows
     * ignored). While on, the client's build cursor counts the bag, the base's stock and the item's row when the first two
     * fall short of a blueprint's blocks. Not authority: the server checks the placement again and pays bag and base first,
     * all or nothing. Any thread. KILN_ERR_INVALID_ARGUMENT: on over 1, count over the limit, rows null with count set, an
     * item 0. */
    KilnResult(KILN_CALL* set_build_stock)(uint32_t on, const KilnStationStack* rows, uint32_t count);
    /* ---- since 0.2.0-dev.31.16 (GitHub #132; header.size tells whether this is here) ---- */
    /* The KILN_STATION_FILL_* request flags plan and submit take for a FILL on this build: KILN_STATION_FILL_ITEM only
     * where the game's item transfer resolved (feature stations.fill_items) and stations.fill is on. Any thread. */
    uint32_t(KILN_CALL* fill_flags)(void);
    /* ---- the skill tree's respec from linked sources, since 0.2.0-dev.31.18 (GitHub #150; capability
     * crafting.respec_sources; header.size tells whether this is here) ---- */
    /* A Kiln client: the server's respec snapshot for the local player. on 1: the server counts and pays the skill tree's
     * paid reset and a node's unlearn from the player's links registered with KILN_CRAFT_SOURCE_RESPEC; rows are per item
     * the count those links hold (the bag and the Magic chests of the player's base not included; the server sends its
     * respec item's row, 0 when the links hold none), at most KILN_STATIONS_MAX_RESPEC_STOCK. on 0 clears it (rows
     * ignored): the game's own counts again. While on, the respec skill tree's unlearn check, the Flame Altar's reskill
     * check and the respec tree's Rune row count the game's count (the bag and the base's Magic chests) plus the item's
     * row. Not authority: the server pays from the bag and the Magic chests first, then the links, all or nothing. Any
     * thread. KILN_ERR_INVALID_ARGUMENT: on over 1, count over the limit, rows null with count set, an item 0. */
    KilnResult(KILN_CALL* set_respec_stock)(uint32_t on, const KilnStationStack* rows, uint32_t count);
} KilnStationsApi;

/* KilnStationCollect.flags. Bits 16 and up, so a result's flags never read as a request flag (KILN_STATION_DRY_RUN). */
enum {
    KILN_STATION_COLLECT_OUTPUTS = 0x10000u,   /* stacks of an output that is not also an input */
    KILN_STATION_COLLECT_LEFTOVERS = 0x20000u, /* stacks that are neither an input nor an output (none without a recipe) */
    KILN_STATION_COLLECT_TO_BASE = 0x40000u,   /* into the base's Magic chests that take deposits; `to` is 0 */
};

/* A collect. size is sizeof(KilnStationCollect). The stacks are the station's slots whose item is an output of its
 * selected or running recipe and no input of either (OUTPUTS), or neither (LEFTOVERS), the same split as
 * StationInfo.Outputs / Leftovers; `item` narrows them to one item. Each stack is one kiln.items move (its own
 * transaction, as Items.Move; component watchers see kiln.items as the writer), in the station's slot order:
 *  - into `to`: any inventory holder but the station (a chest, a player's bag: a player's equipped and action-bar slots
 *    are never filled, as Items.Move);
 *  - TO_BASE: the base's Magic chests that take deposits (the DynamicInventoryCraftingStock override when set, else the
 *    template's canReceiveAreaDeposit), a chest that holds the item with room first, then one with a free slot, each
 *    group in the base's order. A stack that one chest takes only in part goes on into the next chest.
 * The result is a KilnStationResult with action COLLECT and flags = this request's flags: DONE every stack moved whole,
 * PARTIAL some items moved and a stack is not finished (or a move faulted after earlier stacks moved: the message says
 * so), NO_ROOM nothing moved (also TO_BASE when the station stands in no base), UNCHANGED nothing to collect,
 * NOT_A_STATION, UNAVAILABLE (items.move off, the deposit flag unreadable on this build, or a fault before anything
 * moved). A fault switches kiln.stations' actions off, as a faulted fill does. changes holds the first
 * KILN_STATIONS_MAX_CHANGES ledger rows of all the moves; change_count says how many it holds. */
typedef struct KilnStationCollect {
    uint32_t size;
    KilnEntity station;
    KilnEntity to;     /* the inventory holder (a chest, a player 1..1023); 0 with KILN_STATION_COLLECT_TO_BASE */
    uint32_t item;     /* 0: every matching stack; otherwise only this item */
    uint32_t flags;    /* KILN_STATION_COLLECT_* ; OUTPUTS or LEFTOVERS or both is required */
    uint32_t reserved; /* 0 */
} KilnStationCollect;

#ifdef __cplusplus
}
#endif
#endif
