/* SPDX-License-Identifier: MIT */
/*
 * kiln.item_costs v1 (since 0.2.0-dev.32): exact costs from a selected inventory slot, with a receipt. Published by
 * kiln-items.dll. docs/kiln/services/item-costs.md; design: dossier chapter 19e section 5.2.
 *
 * A cost names ONE slot of one holder (a player's backpack slot, a chest's slot) and what that slot must hold right now:
 *   - a stack take (KILN_COST_STACK_TAKE): `amount` of the plain stack in that slot, and only from it, all or nothing;
 *   - a gear wear (KILN_COST_GEAR_WEAR): `amount` durability units off the item entity in that slot (its stamp checked),
 *     through the game's own WearDelta (other loaders' hooks on it stay in the chain), never by destroying and giving
 *     the tool again. allow_break 0 refuses a wear that would leave less than 1;
 *   - their refunds (KILN_COST_STACK_REFUND: `amount` of the item given back to the holder, the slot preferred;
 *     KILN_COST_GEAR_RESTORE: up to `amount` units given back to the same stamped item entity, never above its max).
 * There is no search: a slot that no longer holds exactly what the cost was quoted for (item, count, item entity and
 * stamp) refuses with KILN_COST_STALE_SELECTION and nothing changes. Stacks are fungible values: the same item and count
 * removed and put back is the same selection.
 *
 * How it runs: submit (tick thread) copies the cost; it runs in the next sim step inside kiln.items' serial program
 * kiln_items_gear (the inventory transaction of the game and the tracked Durability write); peek then reports its
 * receipt: QUEUED until it ran, then COMMITTED (it happened: what moved is in the receipt), REFUSED (nothing changed) or
 * UNCERTAIN (a fault inside the game's commit: the receipt says what was read afterwards, and the cost must never be
 * submitted again as if nothing happened). A receipt is kept until its owner acknowledges it (or 10 minutes of process
 * time after it settled, for an owner that never does); its request id is remembered after that, so the same request id
 * can never debit twice in this process: a submit of a request id still held returns the same ticket when the cost is
 * the same and KILN_ITEM_COSTS_REQUEST_CONFLICT when it is not; one acknowledged or expired returns
 * KILN_ITEM_COSTS_REQUEST_CLOSED.
 *
 * The world: world_epoch is the server world's session number (KilnSessionInfo.sessions[KILN_WORLD_SERVER]); a cost of
 * another world refuses at submit, and a world that ends refuses what has not run (KILN_COST_WORLD_ENDED). A player
 * holder's character_session is ((uint64_t)KilnPlayer.handle << 32) ^ KilnPlayer.joined_step of the player in game
 * (kiln.players); a player who left, or a new player on the same entity, refuses (KILN_COST_STALE_SESSION). A holder that
 * is no player (a chest) passes character_session 0.
 *
 * Not promised: atomicity with anything else (a pet record, a save of another store). A committed receipt says the
 * inventory changed in the running world; whether a save kept it is the save's business.
 *
 * Threads: submit, peek, acknowledge on the tick thread (KILN_ERR_WRONG_THREAD otherwise); status on any thread.
 * Capabilities: items.costs.exact (stack takes and refunds), items.costs.wear (gear wear and restore; needs the
 * durability debit's facts of items.tool_wear_debit on an approved build).
 */
#ifndef KILN_ITEM_COSTS_H
#define KILN_ITEM_COSTS_H

#include "../kiln_api.h"

#ifdef __cplusplus
extern "C" {
#endif

#define KILN_ITEM_COSTS_SERVICE "kiln.item_costs"
#define KILN_ITEM_COSTS_VERSION 1

/* submit/peek/acknowledge results besides KILN_OK and the common codes (KILN_ERR_INVALID_ARGUMENT: a bad size, kind,
 * slot, amount, request id 0; KILN_ERR_NOT_FOUND: no such ticket of this owner; KILN_ERR_UNAVAILABLE: the kind's
 * capability is off now (status says why); KILN_ERR_WRONG_THREAD). */
#define KILN_ITEM_COSTS_REQUEST_CONFLICT 100 /* the request id is held by another cost (of this owner) */
#define KILN_ITEM_COSTS_REQUEST_CLOSED 101   /* the request id's receipt was acknowledged or expired: never again */
#define KILN_ITEM_COSTS_BUSY 102             /* 1024 costs are queued or unacknowledged: a refusal for now */
#define KILN_ITEM_COSTS_WRONG_WORLD 103      /* world_epoch is not the running server world's (or none runs) */
#define KILN_ITEM_COSTS_STALE_SESSION 104    /* the holder is a player and character_session is not theirs now */

/* KilnSelectedCost.kind */
enum {
    KILN_COST_NONE = 0,         /* nothing to pay: refused (a no-cost step needs no receipt from here) */
    KILN_COST_STACK_TAKE = 1,
    KILN_COST_GEAR_WEAR = 2,
    KILN_COST_STACK_REFUND = 3, /* item + amount given back; slot preferred (0: the game's choice); item_entity 0 */
    KILN_COST_GEAR_RESTORE = 4, /* item_entity + item_stamp of the worn item; amount: units to give back */
};

/* KilnCostResult.state */
enum {
    KILN_COST_QUEUED = 0,
    KILN_COST_COMMITTED = 1,
    KILN_COST_REFUSED = 2,
    KILN_COST_UNCERTAIN = 3,
};

/* KilnCostResult.disposition (COMMITTED and UNCERTAIN) */
enum {
    KILN_COST_EXACT = 0,               /* exactly what was asked moved */
    KILN_COST_WEAR_WAIVED_BY_WORLD = 1,/* gear wear in a world with enableDurability off: 0 applied, as the game's own debits */
    KILN_COST_MODIFIED_BY_OTHER = 2,   /* gear: wear_applied (after - before) differs from what was asked: another hook on
                                          WearDelta changed it (negative for a repair); the use happened once */
    KILN_COST_PARTIAL = 3,             /* a stack take moved less than asked (the game took part), or a refund fit only
                                          partly (quantity_taken says how much came back); never retried by Kiln */
};

/* KilnCostResult.refusal (REFUSED) */
enum {
    KILN_COST_REFUSAL_NONE = 0,
    KILN_COST_STALE_SELECTION = 1, /* the slot is not the holder's, or does not hold item/count/item entity/stamp now */
    KILN_COST_NOT_A_STACK = 2,     /* a stack take or refund of an item with an item entity of its own (gear, gems) */
    KILN_COST_NOT_GEAR = 3,        /* a gear cost of an item without durability, or no item entity */
    KILN_COST_WOULD_BREAK = 4,     /* allow_break 0 and the wear would leave less than 1 */
    KILN_COST_BROKEN = 5,          /* the item's durability is 0 already (a broken tool is not usable) */
    KILN_COST_GAME_REFUSED = 6,    /* the game's transaction refused it (game_error) */
    KILN_COST_NO_ROOM = 7,         /* a stack refund: nothing fit */
    KILN_COST_WORLD_ENDED = 8,     /* the server world closed before it ran */
    KILN_COST_NOT_RUN = 9,         /* the program did not run for 10 s, or the service was switched off */
    KILN_COST_FULL = 10,           /* a gear restore of an item at its max: nothing to give back */
};

/* What one cost asks for. Copied at submit. */
typedef struct KilnSelectedCost {
    uint32_t size;               /* sizeof(KilnSelectedCost) */
    uint32_t kind;               /* KILN_COST_* */
    uint64_t world_epoch;        /* KilnSessionInfo.sessions[KILN_WORLD_SERVER] */
    uint64_t character_session;  /* the holder player's; 0 for a holder that is no player */
    KilnEntity holder;           /* whose inventory tree the slot belongs to */
    uint32_t reserved;           /* 0 */
    uint64_t slot;               /* KilnSlotId: inventory entity in bits 0-31, index 0-7 in bits 32-39, the rest 0 */
    uint32_t item;               /* KilnItemId the slot holds (the refunded item for STACK_REFUND) */
    uint32_t expected_count;     /* STACK_TAKE: the slot's count now (exact); GEAR_WEAR: 1; refunds: 0 */
    uint32_t item_entity;        /* GEAR_*: the item entity (pide); stacks: 0 */
    uint32_t item_stamp;         /* GEAR_*: kiln.items' stamp of that entity (KilnItemsSlot.reserved); stacks: 0 */
    uint32_t amount;             /* STACK_*: 1..100000 items (a take at most expected_count); GEAR_*: 0..32767 units */
    uint32_t allow_break;        /* GEAR_WEAR: 1 lets the wear reach 0 (the tool breaks); else 0 */
    uint64_t request_lo, request_hi; /* the caller's request id (128 bits, not both 0): one debit per id, ever */
} KilnSelectedCost;

/* A cost's receipt. */
typedef struct KilnCostResult {
    uint32_t size;          /* in: at least sizeof(KilnCostResult); out: the service's size */
    uint32_t state;         /* KILN_COST_QUEUED .. UNCERTAIN */
    uint32_t game_error;    /* the game's transaction error (0 none) */
    uint32_t disposition;   /* KILN_COST_EXACT .. (COMMITTED, UNCERTAIN) */
    uint64_t ticket;
    uint32_t quantity_taken;/* stack take: items taken; stack refund: items given back */
    uint32_t wear_requested;/* gear: the units asked for */
    int32_t wear_applied;   /* gear: durability_before - durability_after for a wear, after - before for a restore */
    uint32_t refusal;       /* KILN_COST_REFUSAL_* (REFUSED) */
    int32_t durability_before, durability_after; /* gear: as read in the program around the change; -1 unknown */
    uint32_t kind;          /* the cost's kind */
    uint32_t item;          /* the cost's item */
    uint64_t slot;          /* the cost's slot (a refund: where the most went; 0 unknown) */
    uint32_t item_entity, item_stamp; /* the cost's item entity and stamp (gear) */
    uint64_t world_epoch;   /* the cost's world */
    uint64_t request_lo, request_hi;
    uint64_t step;          /* the sim step it ran in (0: it did not run) */
    char reason[160];       /* why it was refused or is uncertain, in words; "" otherwise. NUL-terminated */
} KilnCostResult;

typedef struct KilnItemCostsStatus {
    uint32_t size;          /* in: sizeof(KilnItemCostsStatus) */
    uint32_t stacks;        /* 1: stack takes and refunds can be submitted now */
    uint32_t wear;          /* 1: gear wear and restore can be submitted now */
    uint32_t held;          /* receipts queued or not acknowledged */
    uint64_t committed, refused, uncertain; /* since the process started */
    uint64_t world_epoch;   /* the running server world's session number, what a cost's world_epoch must be (0: none) */
    char stacks_reason[160];/* why stacks is 0; "" otherwise */
    char wear_reason[160];  /* why wear is 0; "" otherwise */
} KilnItemCostsStatus;

typedef struct KilnItemCostsApi {
    KilnServiceHeader header;
    /* Queues one cost; *ticket = its ticket (never 0). A request id this owner holds: the same ticket when the cost is
     * byte-for-byte the same (nothing is queued again), KILN_ITEM_COSTS_REQUEST_CONFLICT otherwise. Tick thread. */
    KilnResult(KILN_CALL* submit)(KilnOwner owner, const KilnSelectedCost* cost, uint64_t* ticket);
    /* The receipt now (QUEUED until it ran). Nothing is taken away. Tick thread. */
    KilnResult(KILN_CALL* peek)(KilnOwner owner, uint64_t ticket, KilnCostResult* out);
    /* The owner has the receipt: it is let go (its request id stays closed). A QUEUED ticket refuses
     * (KILN_ERR_INVALID_ARGUMENT): a cost is never forgotten before it settled. Tick thread. */
    KilnResult(KILN_CALL* acknowledge)(KilnOwner owner, uint64_t ticket);
    /* What can be submitted now, and the counters. Any thread. */
    KilnResult(KILN_CALL* status)(KilnItemCostsStatus* out);
} KilnItemCostsApi;

#ifdef __cplusplus
}
#endif

#ifdef __cplusplus
static_assert(sizeof(KilnSelectedCost) == 80, "KilnSelectedCost is 80 bytes");
static_assert(offsetof(KilnSelectedCost, slot) == 32 && offsetof(KilnSelectedCost, amount) == 56 &&
                  offsetof(KilnSelectedCost, request_lo) == 64,
              "KilnSelectedCost's fields");
static_assert(sizeof(KilnCostResult) == 264, "KilnCostResult is 264 bytes");
static_assert(offsetof(KilnCostResult, ticket) == 16 && offsetof(KilnCostResult, durability_before) == 40 &&
                  offsetof(KilnCostResult, slot) == 56 && offsetof(KilnCostResult, world_epoch) == 72 &&
                  offsetof(KilnCostResult, step) == 96 && offsetof(KilnCostResult, reason) == 104,
              "KilnCostResult's fields");
static_assert(sizeof(KilnItemCostsStatus) == 368 && offsetof(KilnItemCostsStatus, stacks_reason) == 48, "KilnItemCostsStatus is 368 bytes");
static_assert(sizeof(KilnItemCostsApi) == sizeof(KilnServiceHeader) + 4 * sizeof(void*), "kiln.item_costs v1 has four functions");
#endif

#endif /* KILN_ITEM_COSTS_H */
