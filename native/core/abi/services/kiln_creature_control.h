/* SPDX-License-Identifier: MIT
 * kiln.creature_control v1: the one broker that owns a controlled creature body's movement (since 0.2.0-dev.28).
 * A controlled body is a game creature driven by Kiln instead of its own AI: a pet that follows, a mount, a vehicle, a
 * Kiln.Blocks NPC brain. The game's steering program overwrites a creature's movement input every step while the body
 * has a SteeringAgent, and its AI keeps deciding while it is on; so a feed that writes ActorInput needs both gone first.
 * The broker is the only code that does that for a body: it holds its own AI-off layer (kiln.creatures ai_switch),
 * snapshots the body's SteeringAgent (80 bytes) at a world-job barrier, removes it through its own serial world program,
 * verifies the removal, and only then grants the lease. When the last lease ends it zeroes the input for 3 steps, puts
 * the saved SteeringAgent back with queryId and both velocity vectors zeroed, and releases its AI layer.
 * Clients (pets, mounts, vehicles, Kiln.Blocks brains; managed code through Kiln.CreatureControl) ask for a lease, wait
 * for Active, write input only while can_drive says so, and release.
 *
 * Lease life: acquire_control is admission only (Pending). After the component transition is verified the lease is
 * Active and has a new transition_sequence; can_drive(lease, key, transition_sequence) is the only authority to write,
 * checked right before every write (a cached answer grants nothing). Release revokes at once (can_drive false), then
 * restores, then reports Released. Terminal leases stay queryable for 10 minutes of simulation time.
 *
 * Body key: (world_epoch, body_generation, entity). world_epoch is the core's server-world session number
 * (KilnSessionInfo.sessions[KILN_WORLD_SERVER], zero-extended): a hosting client's own worlds never change it.
 * body_generation is issued by the first bind_body of a body, never 0, never reused. Bodies are non-player entities in
 * the engine's counter range [1024, 0x7fffffff] of the running server world.
 *
 * Threads: bind_body, acquire_control, release_control, notices, body_retired, next_request and release_body run on
 * the server tick thread (Pre or Post; KILN_ERR_WRONG_THREAD otherwise). query_control and can_drive read published
 * snapshots and may be called from any thread, including a world program on a game worker. quarantine and root_policy
 * are native any-thread calls that never touch the game. Nothing runs on a joined client (no server world there).
 *
 * Records are POD, copied on every call (Windows x64, pack 8): body key 24 bytes, request 48, state 248. Every input
 * `size` must be set; reserved fields must be 0. No engine pointer or caller buffer is kept.
 * Contract and evidence: docs/kiln/services/creature-control.md. The table only grows at the end; check header.size. */
#ifndef KILN_CREATURE_CONTROL_H
#define KILN_CREATURE_CONTROL_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_CREATURE_CONTROL_SERVICE "kiln.creature_control"
#define KILN_CREATURE_CONTROL_VERSION 1u

/* KilnControlRequest.role, KilnControlState.requested_role / effective_role. Priority:
 * Quarantine > Ridden > Vehicle > PetCombat > PetFollow = PetStay = EngineGuard (the "base" group: one base lease per
 * body). Input roles (PetFollow, PetStay, Ridden, Vehicle) remove SteeringAgent and keep the broker's AI-off layer;
 * AI-on roles (PetCombat, EngineGuard) keep or restore SteeringAgent and release the broker's layer. Quarantine is the
 * broker's own internal policy and is never requested. */
enum {
    KILN_CONTROL_ROLE_NONE = 0,
    KILN_CONTROL_ROLE_PET_FOLLOW = 1,
    KILN_CONTROL_ROLE_PET_COMBAT = 2,
    KILN_CONTROL_ROLE_RIDDEN = 3,
    KILN_CONTROL_ROLE_QUARANTINE = 4,
    KILN_CONTROL_ROLE_VEHICLE = 5,
    KILN_CONTROL_ROLE_PET_STAY = 6,
    KILN_CONTROL_ROLE_ENGINE_GUARD = 7,
};

/* KilnControlState.lease_state. Pending and Preempted never grant authority; Released and Failed are terminal. */
enum {
    KILN_CONTROL_PENDING = 0,
    KILN_CONTROL_ACTIVE = 1,
    KILN_CONTROL_PREEMPTED = 2,
    KILN_CONTROL_RELEASED = 3,
    KILN_CONTROL_FAILED = 4,
};

/* KilnControlState.result: why the lease is in its state. The reason text says more; it starts with a stable word
 * ("Capacity", "ExternalController", "RequestConflict", "Quiescence", "BodyNotReady", "Replaced", ...). */
enum {
    KILN_CONTROL_RESULT_NONE = 0,
    KILN_CONTROL_RESULT_GRANTED = 1,
    KILN_CONTROL_RESULT_RELEASED = 2,
    KILN_CONTROL_RESULT_PREEMPTED = 3,
    KILN_CONTROL_RESULT_CANCELED = 4,
    KILN_CONTROL_RESULT_BUSY = 5,
    KILN_CONTROL_RESULT_TIMED_OUT = 6,
    KILN_CONTROL_RESULT_BODY_GONE = 7,
    KILN_CONTROL_RESULT_WORLD_ENDED = 8,
    KILN_CONTROL_RESULT_CAPABILITY_OFF = 9,
    KILN_CONTROL_RESULT_OWNER_UNLOADED = 10,
    KILN_CONTROL_RESULT_RESTORE_FAILED = 11,
    KILN_CONTROL_RESULT_RETIRED = 12,
    KILN_CONTROL_RESULT_EXPIRED_HANDLE = 13,
    KILN_CONTROL_RESULT_AI_INHIBITED = 14,
};

/* KilnControlState.changed_mask: what the broker has changed on the body now. */
enum {
    KILN_CONTROL_CHANGED_STEERING = 1u << 0,         /* SteeringAgent removed by the broker */
    KILN_CONTROL_CHANGED_AI_LAYER = 1u << 1,         /* the broker's AI-off layer is held */
    KILN_CONTROL_CHANGED_VEHICLE_BEHAVIOR = 1u << 2, /* DynamicEnemy01BehaviorState removed for a Vehicle (never restored) */
};

/* bind_body baseline_kind */
enum {
    KILN_CONTROL_BASELINE_ENEMY = 0,          /* a game creature with its AI and SteeringAgent (refused without one) */
    KILN_CONTROL_BASELINE_EXPLICIT_NO_AI = 1, /* a body defined without AI (vehicles): an absent SteeringAgent is its baseline */
};

/* quarantine cause. 1-4 latch a semantic down for a guarded pet; 5-9 only revoke and deny for teardown. */
enum {
    KILN_CONTROL_CAUSE_NATIVE_LETHAL = 1,
    KILN_CONTROL_CAUSE_PROTECTION_LOST = 2,
    KILN_CONTROL_CAUSE_CONTROL_FAILURE = 3,
    KILN_CONTROL_CAUSE_BODY_LOST = 4,
    KILN_CONTROL_CAUSE_DISMISS = 5,
    KILN_CONTROL_CAUSE_RELEASE = 6,
    KILN_CONTROL_CAUSE_OWNER_LEFT = 7,
    KILN_CONTROL_CAUSE_PREVIEW_ABORT = 8,
    KILN_CONTROL_CAUSE_WORLD_END = 9,
};

/* Ceilings of v1 (refused before allocation when full). */
#define KILN_CONTROL_MAX_BODIES 256u
#define KILN_CONTROL_MAX_LEASES 1024u      /* nonterminal leases */
#define KILN_CONTROL_MAX_TOMBSTONES 65536u /* retired-root deny entries per world epoch */
#define KILN_CONTROL_REQUEST_QUEUE 1024u
#define KILN_CONTROL_NOTICE_QUEUE 1024u    /* per owner */

typedef struct KilnControlBodyKey {
    uint64_t world_epoch;     /* the server world's session number; 0 = none */
    uint64_t body_generation; /* issued by bind_body; 0 = none */
    KilnEntity entity;
    uint32_t reserved;        /* 0 */
} KilnControlBodyKey;

typedef struct KilnControlRequest {
    uint32_t size;            /* sizeof(KilnControlRequest) */
    uint32_t role;            /* KILN_CONTROL_ROLE_*, not None or Quarantine */
    KilnControlBodyKey body;  /* from bind_body */
    uint64_t request_id;      /* nonzero, from next_request; monotonic per owner. The same id with the same payload
                                 returns the same lease (also a terminal one); with another payload: RequestConflict */
    uint32_t flags;           /* 0 in v1 */
    uint32_t reserved;        /* 0 */
} KilnControlRequest;

typedef struct KilnControlState {
    uint32_t size;            /* in: sizeof(KilnControlState); out: as filled */
    uint32_t lease_state;     /* KILN_CONTROL_PENDING .. _FAILED */
    uint32_t result;          /* KILN_CONTROL_RESULT_* */
    uint32_t requested_role;  /* the role this lease asked for */
    uint32_t effective_role;  /* the role that wins the body now; 0 while a transition is not verified */
    uint32_t changed_mask;    /* KILN_CONTROL_CHANGED_* */
    KilnControlBodyKey body;
    KilnHandle lease;         /* 0 for a refused admission */
    KilnHandle winning_lease; /* the body's Active winner; 0 while none */
    uint64_t request_id;
    uint64_t transition_sequence; /* the body's sequence when this lease last became Active (0 before) */
    uint64_t notice_sequence;     /* the owner's notice counter when this state was published */
    char reason[160];         /* UTF-8, NUL-terminated */
} KilnControlState;

typedef struct KilnCreatureControlApi {
    KilnServiceHeader header; /* { sizeof(KilnCreatureControlApi), KILN_CREATURE_CONTROL_VERSION } */

    /* One binding reference of `owner` to the body (idempotent per owner; other owners may share the key). The first
     * reference of any owner issues the key; while the body stays bound every bind returns the same key and never takes
     * a new snapshot. baseline_kind: KILN_CONTROL_BASELINE_*; retire_on_fatal must be 1 (the caller consents that an
     * irrecoverable restore failure retires the body). Reserves the body's retired-root deny entry.
     * KILN_ERR_INVALID_ARGUMENT: owner 0, out NULL, a bad kind, retire_on_fatal != 1, entity 0, a player or an id outside
     * the counter range. KILN_ERR_NOT_FOUND: no such entity in the server world. KILN_ERR_UNAVAILABLE: creature_control
     * is off, no server world runs here, or the body cannot be controlled (no Actor/ActorInput, or an Enemy body whose
     * SteeringAgent is already gone: ExternalController). KILN_ERR_BUFFER_TOO_SMALL: a ceiling (256 bodies, the
     * tombstone table). Tick thread. */
    KilnResult(KILN_CALL* bind_body)(KilnOwner owner, KilnEntity entity, uint32_t baseline_kind, uint32_t retire_on_fatal,
                                     KilnControlBodyKey* out);
    /* Admission. KILN_OK when a well-formed request was processed: read `out` (size set by the caller). A refusal is
     * lease_state Failed with lease 0 and a reason; an admission is a nonzero lease, Pending (or Preempted under a
     * higher lease). Neither grants authority. KILN_ERR_INVALID_ARGUMENT: bad sizes/fields, role None or Quarantine,
     * request_id 0, or RequestConflict (out says so). Tick thread. */
    KilnResult(KILN_CALL* acquire_control)(KilnOwner owner, const KilnControlRequest* request, KilnControlState* out);
    /* The complete state of a lease (any owner may read). A retained terminal lease returns its terminal state; an
     * unknown or expired nonzero handle returns Failed/ExpiredHandle. KILN_ERR_INVALID_ARGUMENT: lease 0, out NULL or
     * out->size too small. Any thread. */
    KilnResult(KILN_CALL* query_control)(KilnOwner owner, KilnHandle lease, KilnControlState* out);
    /* *out = 1 only while `lease` is the body's Active winner, belongs to `owner`, and key and transition_sequence
     * match; 0 otherwise (also for an unknown handle). Any thread; check it right before every write. */
    KilnResult(KILN_CALL* can_drive)(KilnOwner owner, KilnHandle lease, KilnControlBodyKey body, uint64_t transition_sequence,
                                     uint32_t* out);
    /* Releases the owner's lease: drive permission ends at once, then the body is restored when no lease is left, then
     * the lease is Released (Canceled when it was not Active). A retained terminal lease: KILN_OK, nothing changes.
     * KILN_ERR_NOT_FOUND: unknown/expired handle or another owner's lease. Tick thread. */
    KilnResult(KILN_CALL* release_control)(KilnOwner owner, KilnHandle lease);
    /* Takes up to `capacity` notices of the owner's leases, oldest first (each exactly a KilnControlState; out[i].size
     * is set by the broker). *needs_resync = 1 when notices were dropped since the last call (a gap in
     * notice_sequence): query every lease you hold. Notices are history: query before you act. Tick thread. */
    KilnResult(KILN_CALL* notices)(KilnOwner owner, KilnControlState* out, uint32_t capacity, uint32_t* written,
                                   uint32_t* needs_resync);
    /* Latches the body's quarantine (cause KILN_CONTROL_CAUSE_*): every lease loses authority at once and its root is
     * denied (root_policy) from now on. No game call, no callback. Any thread. */
    KilnResult(KILN_CALL* quarantine)(KilnControlBodyKey body, uint32_t cause);
    /* The owner destroyed the body: the broker checks it is gone at a job barrier, keeps its root denied for the world
     * epoch and drops its rows. KILN_ERR_INVALID_ARGUMENT while it is alive. Tick thread. */
    KilnResult(KILN_CALL* body_retired)(KilnOwner owner, KilnControlBodyKey body);
    /* 1: deny every outgoing effect of hits whose root is this entity (a quarantined or retired body of this epoch);
     * 0: no broker suppression. Native any thread; POD only. */
    uint32_t(KILN_CALL* root_policy)(uint64_t world_epoch, KilnEntity root_source);
    /* A new request id for the owner (unique, increasing; allocate just before acquire_control). Tick thread. */
    KilnResult(KILN_CALL* next_request)(KilnOwner owner, uint64_t* out);
    /* Drops the owner's binding reference. KILN_ERR_INVALID_ARGUMENT while the owner still holds a nonterminal lease
     * on the body; KILN_ERR_NOT_FOUND when the owner holds no reference. The binding goes when no reference, lease or
     * pin is left and the baseline is restored. Tick thread. */
    KilnResult(KILN_CALL* release_body)(KilnOwner owner, KilnControlBodyKey body);
} KilnCreatureControlApi;

#ifdef __cplusplus
}
static_assert(sizeof(KilnControlBodyKey) == 24, "KilnControlBodyKey is 24 bytes");
static_assert(sizeof(KilnControlRequest) == 48, "KilnControlRequest is 48 bytes");
static_assert(sizeof(KilnControlState) == 248, "KilnControlState is 248 bytes");
static_assert(offsetof(KilnControlState, body) == 24 && offsetof(KilnControlState, lease) == 48 &&
                  offsetof(KilnControlState, request_id) == 64 && offsetof(KilnControlState, transition_sequence) == 72 &&
                  offsetof(KilnControlState, notice_sequence) == 80 && offsetof(KilnControlState, reason) == 88,
              "KilnControlState offsets");
static_assert(sizeof(KilnCreatureControlApi) == 96, "KilnCreatureControlApi is 96 bytes in v1");
#endif
#endif
