/* SPDX-License-Identifier: MIT
 * kiln.pets v1: the native safety module of Kiln.Pets (since 0.2.0-dev.32; dossier 19d §4.4-4.6, §5.3). A new table in
 * the same pinned module as kiln.creature_control (kiln-creature-control.dll): it shares the broker's quarantine and
 * retired-root lifetime and adds what only native code can do at the engine's worker-thread seams:
 *   - a cached guard row per protected pet body (at most 256), keyed by the broker's body key;
 *   - the death policy (kiln.decisions' native death guard): a guarded pet whose health reaches 0 is never marked dead;
 *     its row latches DownPending (and the broker's quarantine) and Kiln.Pets retires the body at the next Pre;
 *   - the hit policy (asked by kiln.combat before ApplyHit, for every hit a pet row is part of): one rule for damage,
 *     knockback, reaction and aggro, the pet's own declared target rule (PetTargetPolicy): its hostile creature factions
 *     and its PvP mode (off: every player and every other character's pet protected; by mod rule: protected unless the
 *     mod declared the two players hostile, set_hostile). A lethal allowed hit on a guarded pet latches DownPending and is
 *     denied before the original runs (no reaction, no aggro, no death);
 *   - two-phase protection loss (withdraw): every row is marked ProtectionLost at once and stays guarded until Kiln.Pets
 *     retired its body.
 * The managed runtime (Kiln.Pets) owns records and definitions; this table never calls managed code, keeps no game
 * pointer and calls no tick-only service from a worker.
 *
 * Threads: bind, activate, collect_dirty, retire_binding, set_owner_entity, set_hostile and withdraw run on the server
 * tick thread (KILN_ERR_WRONG_THREAD otherwise). inspect_body, death_policy, hit_policy and involved read atomics and may
 * be called from any thread, including a game worker inside the simulation.
 * Records are POD, copied on every call (Windows x64, pack 8): binding 96 bytes, state 80, hit query 56. Every input
 * `size` must be set; reserved fields must be 0. The table only grows at the end; check header.size.
 * Contract: docs/kiln/services/pets.md. */
#ifndef KILN_PETS_H
#define KILN_PETS_H
#include "../kiln_api.h"
#include "kiln_creature_control.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_PETS_SERVICE "kiln.pets"
#define KILN_PETS_VERSION 1u

#define KILN_PETS_MAX_ROWS 256u
#define KILN_PETS_MAX_HOSTILE_PAIRS 256u

/* KilnPetSafetyState.flags */
enum {
    KILN_PET_SAFETY_GUARDED = 1u << 0,         /* the row protects its body (always set on a bound row) */
    KILN_PET_SAFETY_DOWN_PENDING = 1u << 1,    /* a lethal health or hit was caught: the body must be retired */
    KILN_PET_SAFETY_RETIRING = 1u << 2,        /* reserved for the managed side's retirement */
    KILN_PET_SAFETY_RETIRED = 1u << 3,         /* reserved */
    KILN_PET_SAFETY_PROTECTION_LOST = 1u << 4, /* withdraw: Kiln can no longer guarantee its protection */
    KILN_PET_SAFETY_STAGING = 1u << 5,         /* a body being prepared: invulnerable, harmless, never a semantic down */
};

/* KilnPetSafetyBinding.flags */
enum { KILN_PET_BIND_STAGING = 1u << 0 };

/* KilnPetSafetyBinding.pvp: the pet's declared PvP mode (PetTargetPolicy.Pvp) */
enum { KILN_PET_PVP_OFF = 0, KILN_PET_PVP_BY_MOD_RULE = 1 };

/* KilnPetHitQuery.root_class / target_class: what kiln.combat read of the entity at the hit seam */
enum {
    KILN_PET_CLASS_PLAYER = 1u << 0,   /* it has the game's PlayerInput */
    KILN_PET_CLASS_ENEMY_AI = 1u << 1, /* it has enemy AI (DynamicEnemy01BehaviorState) */
    KILN_PET_CLASS_KNOWN = 1u << 7,    /* the components were read (else nothing is known: conservative denial) */
};
#define KILN_PET_FACTION_UNKNOWN 0xFFu

/* hit_policy's answer */
enum { KILN_PET_HIT_PASS = 0, KILN_PET_HIT_DENY = 1 };

typedef struct KilnPetId128 {
    uint64_t lo, hi;
} KilnPetId128;

typedef struct KilnPetCharacterKey {
    uint64_t account;
    KilnPetId128 character;
} KilnPetCharacterKey;

typedef struct KilnPetSafetyBinding {
    uint32_t size;                 /* sizeof(KilnPetSafetyBinding) */
    uint32_t flags;                /* KILN_PET_BIND_STAGING for a body being prepared */
    KilnControlBodyKey body;       /* from kiln.creature_control's bind_body (the body is bound there first) */
    KilnPetId128 pet;              /* nonzero */
    KilnPetCharacterKey owner;     /* the owner character (a real character id) */
    uint64_t character_session;    /* the owner's session (diagnostics) */
    KilnEntity owner_entity;       /* the owner's player entity now; 0 while offline */
    uint32_t hostile_factions;     /* bit n: unowned creatures of faction n are fair targets (never 7, 8, 11, 12) */
    uint32_t pvp;                  /* KILN_PET_PVP_* */
    uint32_t reserved;             /* 0 */
} KilnPetSafetyBinding;

typedef struct KilnPetSafetyState {
    uint32_t size;                 /* in: sizeof(KilnPetSafetyState) */
    uint32_t flags;                /* KILN_PET_SAFETY_* */
    KilnControlBodyKey body;
    KilnPetId128 pet;
    uint64_t dirty_sequence;       /* raised at each latch; 0 while nothing was latched */
    uint64_t observed_step;        /* the server step of the latch (captured at Pre) */
    uint32_t cause;                /* KILN_CONTROL_CAUSE_* of the latch; 0 while none */
    uint32_t reserved;
    uint64_t denied_hits;          /* hits of or on this body the rule denied */
} KilnPetSafetyState;

typedef struct KilnPetHitQuery {
    uint32_t size;                 /* sizeof(KilnPetHitQuery) */
    uint32_t hit_flags;            /* the game's hit flags */
    uint64_t world_epoch;          /* the server world's session number */
    KilnEntity root_source, source, target;
    int32_t health_change;         /* after the handlers: negative damage, positive healing */
    int32_t root_health, target_health; /* -1 unknown */
    uint16_t reaction;
    uint8_t pushback, hit_kind;    /* hit_kind 0 entity, 1 terrain, 2 building */
    float aggro;
    uint8_t root_faction, target_faction; /* keen::FactionType, KILN_PET_FACTION_UNKNOWN when unknown */
    uint8_t root_class, target_class;     /* KILN_PET_CLASS_* */
    uint32_t reserved;             /* 0 */
} KilnPetHitQuery;

typedef struct KilnPetsApi {
    KilnServiceHeader header; /* { sizeof(KilnPetsApi), KILN_PETS_VERSION } */

    /* A guard row for a body the owner bound in kiln.creature_control. KILN_ERR_INVALID_ARGUMENT: bad sizes or fields
     * (a zero pet, a key of another world, a hostile faction of the players' side). KILN_ERR_BUFFER_TOO_SMALL: 256 rows.
     * KILN_ERR_UNAVAILABLE: the native death guard is not installed (kiln.decisions' native_guards is off) or protection
     * was withdrawn: no pet body is protected here, so none is admitted. An existing row of the same key: KILN_OK
     * (idempotent). Tick thread. */
    KilnResult(KILN_CALL* bind)(KilnOwner owner, const KilnPetSafetyBinding* binding);
    /* A staging row becomes a pet's guarded body (its final lease is Active). KILN_ERR_NOT_FOUND: no such row. Tick. */
    KilnResult(KILN_CALL* activate)(KilnOwner owner, KilnControlBodyKey body);
    /* The row now. KILN_ERR_NOT_FOUND: no row. Any thread. */
    KilnResult(KILN_CALL* inspect_body)(KilnControlBodyKey body, KilnPetSafetyState* out);
    /* Up to `capacity` rows with DownPending or ProtectionLost, oldest latch first; *needs_rescan = 1 when more were
     * there. A latch is never cleared by being collected: the row keeps it until it is retired. Tick thread. */
    KilnResult(KILN_CALL* collect_dirty)(KilnOwner owner, KilnPetSafetyState* out, uint32_t capacity, uint32_t* written,
                                         uint32_t* needs_rescan);
    /* The owner destroyed the body: its row goes (the broker keeps its root denied for the world epoch).
     * KILN_ERR_INVALID_ARGUMENT while the body still exists. Tick thread. */
    KilnResult(KILN_CALL* retire_binding)(KilnOwner owner, KilnControlBodyKey body);
    /* kiln.decisions' native death guard: KILN_DEATH_GUARD_SKIP (1) for a guarded body (latching DownPending when its
     * health is 0 or less), KILN_DEATH_GUARD_PASS (0) otherwise. Native any thread; POD only. */
    uint32_t(KILN_CALL* death_policy)(uint64_t world_epoch, KilnEntity entity, int32_t health);
    /* The pet target rule for one hit: KILN_PET_HIT_DENY (1) denies the whole hit before ApplyHit (no health change,
     * reaction, aggro or HitEvent), KILN_PET_HIT_PASS (0) lets it run. Native any thread; POD only. */
    uint32_t(KILN_CALL* hit_policy)(const KilnPetHitQuery* hit);
    /* 1 when the entity is a guarded pet body of this world epoch (kiln.combat asks before it reads anything). Any thread. */
    uint32_t(KILN_CALL* involved)(uint64_t world_epoch, KilnEntity entity);
    /* The owner's player entity changed (a login): owner heals and hostile pairs follow it. Tick thread. */
    KilnResult(KILN_CALL* set_owner_entity)(KilnOwner owner, KilnControlBodyKey body, KilnEntity owner_entity);
    /* Declares (on 1) or ends (on 0) a consensual hostility between two player entities, for pets of the by-mod-rule PvP
     * mode: the mod's rule decides, Kiln enforces it. KILN_ERR_BUFFER_TOO_SMALL: 256 pairs. Tick thread. */
    KilnResult(KILN_CALL* set_hostile)(KilnOwner owner, KilnEntity a, KilnEntity b, uint32_t on);
    /* Two-phase protection loss, phase 1: every row is marked ProtectionLost at once (and stays guarded); new binds are
     * refused. Kiln.Pets retires each body at the next Pre; only then may rows go. Tick thread. */
    KilnResult(KILN_CALL* withdraw)(KilnOwner owner);
} KilnPetsApi;

#ifdef __cplusplus
}
static_assert(sizeof(KilnPetSafetyBinding) == 96, "KilnPetSafetyBinding is 96 bytes");
static_assert(offsetof(KilnPetSafetyBinding, body) == 8 && offsetof(KilnPetSafetyBinding, pet) == 32 &&
                  offsetof(KilnPetSafetyBinding, owner) == 48 && offsetof(KilnPetSafetyBinding, character_session) == 72 &&
                  offsetof(KilnPetSafetyBinding, owner_entity) == 80 && offsetof(KilnPetSafetyBinding, hostile_factions) == 84 &&
                  offsetof(KilnPetSafetyBinding, pvp) == 88,
              "KilnPetSafetyBinding offsets");
static_assert(sizeof(KilnPetSafetyState) == 80, "KilnPetSafetyState is 80 bytes");
static_assert(offsetof(KilnPetSafetyState, body) == 8 && offsetof(KilnPetSafetyState, pet) == 32 &&
                  offsetof(KilnPetSafetyState, dirty_sequence) == 48 && offsetof(KilnPetSafetyState, observed_step) == 56 &&
                  offsetof(KilnPetSafetyState, cause) == 64 && offsetof(KilnPetSafetyState, denied_hits) == 72,
              "KilnPetSafetyState offsets");
static_assert(sizeof(KilnPetHitQuery) == 56, "KilnPetHitQuery is 56 bytes");
static_assert(offsetof(KilnPetHitQuery, world_epoch) == 8 && offsetof(KilnPetHitQuery, root_source) == 16 &&
                  offsetof(KilnPetHitQuery, health_change) == 28 && offsetof(KilnPetHitQuery, reaction) == 40 &&
                  offsetof(KilnPetHitQuery, aggro) == 44 && offsetof(KilnPetHitQuery, root_faction) == 48,
              "KilnPetHitQuery offsets");
static_assert(sizeof(KilnPetsApi) == 8 + 11 * sizeof(void*), "KilnPetsApi is 96 bytes in v1");
#endif
#endif
