/* SPDX-License-Identifier: MIT
 * kiln.abilities v1: the game's Impact VM for mods. Observe impact programs (instances, native calls), tweak them per
 * cast (config values before an instance's first run, wrappers around natives), add Kiln natives (C++ built-ins and
 * managed ones) and register new Impact programs at run time. Evidence: docs/re/abilities.md; guide:
 * docs/kiln/services/abilities.md (also the program/native contract with tools/impact).
 *
 * Words:
 *  - program: a keen::ImpactProgram (bytecode + variables), id = FNV-1a-32 of its GUID bytes. 571 ship with the game.
 *  - instance: a running program bound to one entity (ImpactSystem slot): impact id, program id, self entity, data
 *    (the program's variables, config values copied in by the game when the instance is created).
 *  - native: a C++ function programs call by name hash (ECALL). The game registers 107; Kiln adds its own.
 *  - config id: the HashKey32 that binds a program variable to a value of a template, sequence event, item, buff or
 *    skill (Damage 0x72864524, NoiseRange 0xa19409b2, AttackMod 0x854ea8fa, ...).
 *
 * Threads: functions marked "tick" run on the server world's thread in Pre or Post (posted tasks count as Pre) and
 * return KILN_ERR_WRONG_THREAD elsewhere. The listener runs in Pre. Natives and their managed sync wrappers run on
 * the game's job threads inside the sim step.
 * Fail soft: a missing symbol turns the part that needs it off (status() says which); the service stays published.
 */
#ifndef KILN_ABILITIES_H
#define KILN_ABILITIES_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_ABILITIES_SERVICE "kiln.abilities"
#define KILN_ABILITIES_VERSION 1u

/* Engine value types of program variables (FNV-1a of the reflection "impact name"). Others exist (structs such as
 * DamageSetup 0x4af58ef7, entity ids, float3, templates): read them by size. */
#define KILN_IMPACT_TYPE_UINT32 0x32940becu
#define KILN_IMPACT_TYPE_SINT32 0x9859f31eu
#define KILN_IMPACT_TYPE_FLOAT32 0xe89f7410u
#define KILN_IMPACT_TYPE_BOOL 0xc894953du
#define KILN_IMPACT_TYPE_TIME 0xdfe4e404u /* int64 nanoseconds */

/* Well-known native hashes (FNV-1a-32 of the node name). */
#define KILN_NATIVE_APPLY_DAMAGE 0xb04d0e7au /* argc 25 */
#define KILN_NATIVE_APPLY_BUFF 0x91f30b26u   /* argc 5 */
#define KILN_NATIVE_SPAWN_ENTITY 0x61b11b03u /* argc 13 */

/* status() */
typedef struct KilnAbilitiesStatus {
    uint32_t size;         /* set by the caller */
    uint8_t natives;       /* the native registry was captured and Kiln's natives/wrappers are registered */
    uint8_t programs;      /* the ImpactRegistry is reachable (program lookup, registration) */
    uint8_t instances;     /* instance creation and first-run hooks are on (observe, overrides, hold) */
    uint8_t attributes;    /* attr_get / attr_add work */
    uint32_t vanilla_natives;   /* natives the game registered (107 on svn 1285601) */
    uint32_t native_capacity;   /* the registry's capacity (256) */
    uint32_t kiln_natives;      /* live Kiln natives (built-ins + managed) */
    uint32_t programs_total;    /* programs in the ImpactRegistry */
    uint32_t programs_kiln;     /* programs Kiln inserted */
    uint32_t held_now;          /* instances held for the listener right now */
    uint32_t interest;          /* the listener's current interest mask */
    uint64_t instances_created, instances_held, overrides_applied, overrides_mismatched, runs, native_calls,
        native_calls_observed, signals, managed_calls, managed_suspends, events_delivered, events_dropped,
        wrap_calls, programs_refused;
    uint64_t step;
} KilnAbilitiesStatus;

/* KilnAbilityEvent.kind; the listener's interest mask uses (1 << kind). */
enum {
    KILN_ABILITY_EV_IMPACT_STARTED = 1,  /* an instance was created (configs applied). With HOLD interest it is held:
                                            its first run waits until the listener returned (one step). Writable:
                                            `canceled` (destroy the entity; the program never runs). Read/write its
                                            variables with instance_* during the call. */
    KILN_ABILITY_EV_IMPACT_ENDED = 2,    /* an instance reported earlier no longer exists (checked every Pre) */
    KILN_ABILITY_EV_NATIVE = 3,          /* a wrapped vanilla native ran (observe; one step late): hash, args, result */
    KILN_ABILITY_EV_SIGNAL = 4,          /* kiln.Signal ran: tag + value */
    KILN_ABILITY_EV_MANAGED_CALL = 5,    /* a managed Kiln native was called: answer it with native_complete() during
                                            the listener call (else it completes with value 0). The ECALL resumes in
                                            the next step with that value. */
};
#define KILN_ABILITY_INTEREST_HOLD (1u << 31) /* hold new instances (see IMPACT_STARTED); hold_filter narrows it */

/* One event for the listener (640 bytes in v1). Payload fields depend on the kind (unused ones are 0). */
typedef struct KilnAbilityEvent {
    uint32_t size;       /* sizeof(KilnAbilityEvent) */
    uint32_t kind;       /* KILN_ABILITY_EV_* */
    uint64_t step;       /* the step it happened in (the listener runs in the next Pre) */
    uint32_t impact_id;  /* the instance */
    uint32_t program_id;
    KilnEntity self;     /* the instance's entity */
    KilnEntity root;     /* ImpactHierarchy root of self (the caster for player abilities), 0 if none or gone */
    uint32_t native_hash;/* NATIVE / MANAGED_CALL: the native */
    uint32_t site;       /* NATIVE / SIGNAL / MANAGED_CALL: the ECALL's call-site id */
    uint32_t argc;       /* NATIVE / MANAGED_CALL: args[] used */
    uint8_t result;      /* NATIVE: the value the native pushed */
    uint8_t error;       /* NATIVE: its error byte (0 ok, 1-3 suspended) */
    uint8_t held;        /* IMPACT_STARTED: the instance is held for this call */
    uint8_t canceled;    /* IMPACT_STARTED (held): set by the listener to destroy the entity before the first run */
    uint32_t args[31];   /* NATIVE / MANAGED_CALL: the raw argument values (handles 0xFFFF0000|index, 0, raw u32) */
    uint32_t tag;        /* SIGNAL: the tag */
    uint32_t arg_types[31]; /* NATIVE: the type hash of each handle's variable (0 = raw value or not connected) */
    uint32_t value_type; /* SIGNAL: the value's variable type (0 = raw u32 in value[0..3]) */
    uint32_t value_size; /* SIGNAL: bytes in value */
    uint32_t arg_values[31]; /* NATIVE: after the call, the first 4 bytes of each handle's variable (or the raw value) */
    uint8_t arg_offsets[31]; /* NATIVE: where each arg's variable bytes start in value[] (0xFF = not captured) */
    uint8_t arg_sizes[31];   /* NATIVE: how many bytes of it value[] holds */
    uint8_t reserved0[2];
    uint64_t call_id;    /* MANAGED_CALL: pass to native_complete */
    uint8_t value[136];  /* SIGNAL: the value bytes. NATIVE: the variable bytes of the arguments (arg_offsets) */
} KilnAbilityEvent;

/* Listener: called in Pre on the tick thread with the events of the previous step. Returns the interest mask for the
 * next step (bits 1 << KILN_ABILITY_EV_*, plus KILN_ABILITY_INTEREST_HOLD). One listener (the managed Kiln.Abilities). */
typedef uint32_t(KILN_CALL* KilnAbilityListenerFn)(KilnAbilityEvent* events, uint32_t count, void* user);

/* A program variable (instance_vars / program_vars). */
typedef struct KilnImpactVar { /* 88 bytes, fixed in v1 */
    uint32_t index;      /* the variable's index (handle 0xFFFF0000|index) */
    uint32_t name_hash;
    uint32_t config_id;  /* 0 = not bound to a config value */
    uint32_t type;       /* KILN_IMPACT_TYPE_* or another type hash */
    uint16_t value_size; /* bytes */
    uint16_t offset;     /* in the instance data */
    uint32_t reserved;
    char name[64];       /* debug name ("NoiseRange::value"), NUL-terminated, cut at 63 */
} KilnImpactVar;

/* A config override for entity_overrides(): applied to the entity's next instance right after the game copied its
 * configs, before its first run. */
typedef struct KilnConfigOverride {
    uint32_t match;      /* KILN_OVERRIDE_BY_CONFIG: key = config id (every variable bound to it);
                            KILN_OVERRIDE_BY_NAME: key = variable name hash; KILN_OVERRIDE_BY_INDEX: key = index */
    uint32_t key;
    uint32_t size;       /* bytes in data; must equal the variable's size (else it is skipped and counted) */
    uint32_t reserved;
    uint8_t data[64];
} KilnConfigOverride;
enum { KILN_OVERRIDE_BY_CONFIG = 0, KILN_OVERRIDE_BY_NAME = 1, KILN_OVERRIDE_BY_INDEX = 2 };

/* A native in the registry (native_at). */
typedef struct KilnNativeInfo {
    uint32_t size;      /* set by the caller */
    uint32_t hash;
    uint32_t argc;
    uint32_t kind;      /* KILN_NATIVE_KIND_* */
    uintptr_t fn;       /* the game's function (vanilla) or Kiln's thunk */
    char name[64];      /* display name */
} KilnNativeInfo;
enum {
    KILN_NATIVE_KIND_VANILLA = 0, /* registered by the game, untouched */
    KILN_NATIVE_KIND_WRAPPED = 1, /* a vanilla native Kiln wraps (observe / managed wrappers); the game's fn below */
    KILN_NATIVE_KIND_BUILTIN = 2, /* kiln.Log, kiln.Signal */
    KILN_NATIVE_KIND_MANAGED = 3, /* registered through native_register (answers in the next step) */
};

/* A program in the ImpactRegistry (program_info). */
typedef struct KilnProgramInfo {
    uint32_t size;        /* set by the caller */
    uint32_t id;
    uint8_t guid[16];     /* game byte order */
    uint32_t code_words, shutdown_words, variables, data_bytes;
    uint16_t stack_size;
    uint8_t streams;      /* usedStreams bits: OnCollision 1, OnHit 2, OnParry 4, EntityInRange 8 */
    uint8_t kiln;         /* 1 = inserted by Kiln */
} KilnProgramInfo;

/* A managed native call or a sync wrapper call (job thread): the native context, typed access through the
 * instance's variables. `out_error`/`out_value` are what the native returns to the interpreter. */
typedef struct KilnNativeCall {
    uint32_t size;
    uint32_t hash;
    uint32_t argc;
    uint32_t reserved0;
    const uint32_t* args;       /* args[0..argc): handles 0xFFFF0000|index, 0, or raw values */
    const void* layout;         /* the program's ImpactVariable array (24 B each: name, configId, type, u16 size,
                                   u16 offsetInBytes, BlobString dbgName) */
    uint64_t layout_count;
    uint8_t* data;              /* the instance's variables */
    uint64_t data_size;
    uint32_t impact_id, program_id, site;
    KilnEntity self;
    uint8_t out_error;          /* 0 ok, 1-3 suspend (the ECALL re-runs next step), >= 4 error */
    uint8_t out_value;          /* pushed when out_error == 0 */
    uint8_t original_called;    /* set by call_original */
    uint8_t reserved1;
    uint32_t reserved2;
    void* engine[3];            /* opaque: the engine's result, arg slice and context */
} KilnNativeCall;

/* Sync wrapper dispatcher (managed Kiln.Abilities): runs on the job thread that runs the native. It may change
 * variables, call call_original(call) (at most once), and sets out_error/out_value. Returns nothing. */
typedef void(KILN_CALL* KilnNativeWrapFn)(KilnNativeCall* call, void* user);

typedef struct KilnAbilitiesApi {
    KilnServiceHeader header; /* { sizeof(KilnAbilitiesApi), KILN_ABILITIES_VERSION } */

    KilnResult(KILN_CALL* status)(KilnAbilitiesStatus* out);                 /* any thread */
    KilnResult(KILN_CALL* set_listener)(KilnAbilityListenerFn fn, void* user); /* NULL removes it */
    /* Narrows HOLD to instances of these programs (count 0 = hold every new instance while HOLD interest is set). */
    KilnResult(KILN_CALL* hold_filter)(const uint32_t* program_ids, uint32_t count);

    /* ---- instances (tick) ---- */
    /* Variables of a live instance (count = all; writes up to cap). NOT_FOUND: no such instance. */
    KilnResult(KILN_CALL* instance_vars)(uint32_t impact_id, KilnImpactVar* out, uint32_t cap, uint32_t* count);
    KilnResult(KILN_CALL* instance_read)(uint32_t impact_id, uint32_t var_index, void* dst, uint32_t cap,
                                         uint32_t* len);
    KilnResult(KILN_CALL* instance_write)(uint32_t impact_id, uint32_t var_index, const void* src, uint32_t len);
    /* The instance's self entity, program and flags (Initialized 1, Executed 2, Halt 8, Error 0x10, Destroy 0x40). */
    KilnResult(KILN_CALL* instance_info)(uint32_t impact_id, KilnEntity* self, uint32_t* program_id, uint32_t* flags);
    /* Config overrides for the next instance created on `entity` (e.g. one Kiln just spawned). count 0 clears.
     * Replaces earlier overrides of that entity. Expire after `steps` steps (0 = 50). */
    KilnResult(KILN_CALL* entity_overrides)(KilnEntity entity, const KilnConfigOverride* list, uint32_t count,
                                            uint32_t steps);

    /* ---- programs ---- */
    uint32_t(KILN_CALL* program_count)(void); /* tick */
    KilnResult(KILN_CALL* program_info)(uint32_t program_id, KilnProgramInfo* out);              /* tick */
    KilnResult(KILN_CALL* program_vars)(uint32_t program_id, KilnImpactVar* out, uint32_t cap, uint32_t* count); /* tick */
    /* tick. Verifies and inserts a keen::ImpactProgram blob (copied). *out_id = its id. INVALID_ARGUMENT: the
     * verifier refused it (error says why, NUL-terminated) or the GUID is already in the registry; UNAVAILABLE: the
     * registry is not reachable yet (before the first step). */
    KilnResult(KILN_CALL* program_register)(KilnOwner owner, const void* blob, size_t len, uint32_t* out_id,
                                            char* error, size_t error_cap);
    /* tick. Re-lists keen::ImpactProgram resources of kiln.assets and registers the new ones. *added = count. */
    KilnResult(KILN_CALL* program_refresh)(uint32_t* added);

    /* ---- natives ---- */
    uint32_t(KILN_CALL* native_count)(void);                               /* any thread */
    KilnResult(KILN_CALL* native_at)(uint32_t index, KilnNativeInfo* out); /* any thread */
    KilnResult(KILN_CALL* native_find)(uint32_t hash, KilnNativeInfo* out);
    /* any thread. Declares a Kiln native (from a mod's impact/natives.json) so programs that call it pass the
     * verifier before it is registered (a program waiting for an undeclared native is retried on every
     * native_declare / native_register / program_refresh). */
    KilnResult(KILN_CALL* native_declare)(uint32_t hash, uint32_t argc);
    /* tick. A managed native `name` (full "<modid>.<Name>", hash = FNV-1a-32 of it) with argc 0..31. Answers come
     * through MANAGED_CALL events. Collisions with a vanilla or a live Kiln native: INVALID_ARGUMENT. */
    KilnResult(KILN_CALL* native_register)(KilnOwner owner, KilnStr name, uint32_t argc, uint32_t* out_hash);
    /* tick. Takes a managed native off (programs that call it error out from then on). */
    KilnResult(KILN_CALL* native_unregister)(uint32_t hash);
    /* tick (listener). Answers a MANAGED_CALL: error 0 + value, or an error >= 4. */
    KilnResult(KILN_CALL* native_complete)(uint64_t call_id, uint8_t error, uint8_t value);
    /* any thread. Routes calls of a vanilla native through the sync dispatcher (on = 1) or not. Observation
     * (NATIVE events) needs no wrap: interest bit KILN_ABILITY_EV_NATIVE covers damage, buff and spawn; native_observe
     * adds other hashes. */
    KilnResult(KILN_CALL* native_wrap)(uint32_t hash, uint32_t on);
    KilnResult(KILN_CALL* native_observe)(uint32_t hash, uint32_t on);
    KilnResult(KILN_CALL* set_wrap_dispatcher)(KilnNativeWrapFn fn, void* user);
    /* job thread, from the dispatcher only: runs the game's native with the current args; sets out_error/out_value. */
    void(KILN_CALL* call_original)(KilnNativeCall* call);
    /* tick. Drops every managed native of `owner`. */
    KilnResult(KILN_CALL* release_owner)(KilnOwner owner);

    /* ---- attributes (tick) ----
     * The current value of attribute `attr` (e.g. Mana 0x6aa2574e) in component `component` (FNV-1a of
     * "keen::ecs::Mana", 0x4d7229a8) of an entity, and adding to it through the game's attribute code (formulas
     * re-run). NOT_FOUND: no such component or attribute on the entity. */
    KilnResult(KILN_CALL* attr_get)(KilnEntity entity, uint32_t component, uint32_t attr, int32_t* value);
    KilnResult(KILN_CALL* attr_add)(KilnEntity entity, uint32_t component, uint32_t attr, int32_t delta);

    /* ---- sequences (tick) ----
     * The actor's current sequence (Actor.sequenceRuntimeId): sequence id (FNV-1a of the ActorSequenceResource GUID,
     * 0 = none), trigger time (ECS ns) and call index. NOT_FOUND: the entity has no Actor. */
    KilnResult(KILN_CALL* sequence_state)(KilnEntity entity, uint32_t* sequence_id, int64_t* trigger_time,
                                          uint32_t* call_index);
    /* tick. Starts an action sequence (animation, its timed impacts, costs, locks) on an actor the way the game's own
     * programs do (the helper death, fall damage and player actions call: ActorInput.triggerContext). sequence_id =
     * FNV-1a of an ActorSequenceResource GUID the game has; trigger = KILN_SEQUENCE_TRIGGER_*. The actor's sequence
     * system takes it in its next step. NOT_FOUND: no ActorInput on the entity, or the sequence is unknown.
     * trigger may carry KILN_SEQUENCE_FLAG_HOLD; older services answer INVALID_ARGUMENT. */
    KilnResult(KILN_CALL* sequence_play)(KilnEntity entity, uint32_t sequence_id, uint32_t trigger);
} KilnAbilitiesApi;

/* sequence_play() trigger (keen::actor::ActionSequenceTrigger) */
enum {
    KILN_SEQUENCE_TRIGGER_EXTERNAL = 22,       /* ExternalTrigger */
    KILN_SEQUENCE_TRIGGER_ABORT_OVERRIDE = 24, /* ExternalAbortAndOverride: aborts the current sequence (fall damage uses it) */
    KILN_SEQUENCE_FLAG_HOLD = 0x10000,         /* OR into trigger: hold the sequence's keep-alive states (held input) */
};

#ifdef __cplusplus
}
#endif
#endif
