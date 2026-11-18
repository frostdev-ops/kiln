/* SPDX-License-Identifier: MIT
 * kiln.tasks v1 (since 0.2.0-dev.30): Kiln-owned worker threads for mods' off-tick work (docs/kiln/services/tasks.md).
 * A task runs its body on a pool worker, never on the tick, a world thread, a window/render thread or one of the
 * game's job workers, then its done callback, exactly once, also on a pool worker. A body touches game state only
 * through members documented as callable from any thread; anything phase-bound is posted back with
 * KilnApi.post/post_world.
 *
 * Capability row: tasks.pool (AVAILABLE on a booted runtime on both sides; UNAVAILABLE in safe idle or when the pool
 * could not start any worker, with the reason). Independent of the world-read capabilities: tasks run without them.
 *
 * Threads and blocking, per member (D = data/state only, never waits for a task):
 *   submit   any thread, the tick and world threads included; never blocks (a short internal lock only).
 *   cancel   any thread; never blocks; cooperative: a running body sees it through run->cancelled().
 *   status   any thread; never blocks.
 *   release  any thread, also inside this task's own done; never blocks.
 *   drain_owner
 *            timeout_ms == 0: seal + cancel queued + poll; any thread EXCEPT a pool worker (no self-drain).
 *            timeout_ms  > 0: a bounded wait; ONLY off-tick and off-pool: KILN_ERR_WRONG_THREAD on the tick, on any
 *                         world thread (KilnApi.on_world_thread for any kind) and on a pool worker.
 *   body     runs on a pool worker; may call every member above except a timed drain_owner (WRONG_THREAD).
 *   done     runs on a pool worker after the body (or after a queued cancellation); same rules as body; release of
 *            its own handle inside done is legal.
 *   cancelled() (KilnTaskRun)  only during that body's run, from the body's own thread; never blocks.
 * Side: both (dedicated server and client). Owner: every member is scoped to the calling owner's loaded instance;
 * another owner's handle, or one from an earlier instance of the same owner id, is NOT_FOUND. Owner 0 (the core) is
 * refused on every member with KILN_ERR_INVALID_ARGUMENT (submit: + ARGUMENT). `owner` must be the caller's own: Kiln
 * trusts native plugins as it does everywhere else, and nothing beyond the instance check stops a plugin naming
 * another owner.
 *
 * Lifetime: `user` (and the module code of body/done) must stay valid until done has RETURNED, or until drain_owner
 * reported 0 for the owner. Kiln holds a module reference (GetModuleHandleExW FROM_ADDRESS, not a pin) on body's and
 * done's modules from before acceptance until done returns; a refused submit transfers nothing. Revoking an owner
 * starts its retirement; it is not callback quiescence: drain_owner(0) == 0 is.
 *
 * Exit: once the process's exit begins (SHUTDOWN; kiln_api.h, KilnPluginInfo.on_unload) queued tasks may never start
 * and a done is NOT guaranteed: only the exit cleanup budget applies, nothing waits for tasks beyond it. A mod that
 * frees resources in done must not rely on done at exit (free them in on_unload, or leave them to the process exit).
 *
 * Record sizes and later versions: KilnTaskInfo.size is the caller's sizeof. A size below v1 (32) is refused
 * (ARGUMENT); a larger one is accepted and the bytes past v1 are ignored, so a newer caller on an older Kiln gets v1
 * behaviour (check the service version before relying on a later field). KilnTaskRun.size is set by Kiln to what it
 * filled: a body checks it before reading any field added after v1; a v1 body never reads past 40 bytes.
 *
 * Done deliveries and the running cap: an owner runs at most 2 normal-slot items at once, and its done deliveries count
 * toward that cap like its bodies. An owner whose two normal bodies never return therefore also holds back its queued
 * cancellations' dones, and its drain_owner reports outstanding > 0 until its timeout. That is bounded and reported
 * (the watchdog names the long-running bodies), not lost work.
 *
 * Appended members: the table grows at its end and keeps version 1. Check header.size before calling a member past
 * the first five (submit_in: header.size >= 56); an older Kiln's table is 48 bytes.
 */
#ifndef KILN_TASKS_H
#define KILN_TASKS_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_TASKS_SERVICE "kiln.tasks"
#define KILN_TASKS_VERSION 1u

/* Task state (status/cancel out, done's `state`). Permanent numeric tags. */
enum {
    KILN_TASK_NONE = 0,       /* unknown, released or foreign handle */
    KILN_TASK_QUEUED = 1,     /* accepted, not started (also: AFTER_STEP waiting for its Post edge) */
    KILN_TASK_RUNNING = 2,    /* its body runs (or, for a managed task, it is suspended between segments) */
    KILN_TASK_COMPLETED = 3,  /* the body returned normally: for a NATIVE body always, also after a cancel request
                                 (it wins over a cancel requested too late) */
    KILN_TASK_FAULTED = 4,    /* the body faulted; a native structured fault also quarantined the owner */
    KILN_TASK_CANCELLED = 5   /* cancelled before it started; for a MANAGED task also: its own token's cancellation
                                 ended it. A native body that returns is never CANCELLED (it cannot say why it
                                 returned), so a body that stops early on cancelled() records that in its own state */
};

/* KilnTaskInfo.priority: orders work inside one owner only; never raises an owner's share or an OS priority. A
 * zero-initialised request selects Normal. */
enum { KILN_TASK_PRIORITY_NORMAL = 0, KILN_TASK_PRIORITY_LOW = 1, KILN_TASK_PRIORITY_HIGH = 2 };

/* KilnTaskInfo.flags. Unknown bits are refused (ARGUMENT). */
enum {
    KILN_TASK_LONG_RUNNING = 1u << 0, /* a dedicated below-normal thread: at most 2 per owner and 8 in all, else queued */
    KILN_TASK_AFTER_STEP = 1u << 1    /* runnable only after the live server world's next Post edge (see below) */
};

/* submit's `refusal` out, written on EVERY non-OK return (when non-NULL), untouched on OK. New reasons append. */
enum {
    KILN_TASK_REFUSAL_NONE = 0,
    KILN_TASK_REFUSAL_QUEUE_FULL = 1,        /* KILN_ERR_UNAVAILABLE: see "Queue full" below */
    KILN_TASK_REFUSAL_OWNER_SEALED = 2,      /* UNAVAILABLE: drained; REVOKED: the owner is dead/retired */
    KILN_TASK_REFUSAL_OWNER_QUARANTINED = 3, /* UNAVAILABLE: a native fault in one of this owner's callbacks */
    KILN_TASK_REFUSAL_CAPABILITY = 4,        /* UNAVAILABLE: tasks.pool is unavailable (safe idle, no worker) */
    KILN_TASK_REFUSAL_ARGUMENT = 5,          /* INVALID_ARGUMENT: a malformed request (nothing changed) */
    KILN_TASK_REFUSAL_SHUTDOWN = 6,          /* UNAVAILABLE: the process is exiting / the pool is closing */
    KILN_TASK_REFUSAL_NO_SERVER_WORLD = 7,   /* UNAVAILABLE: AFTER_STEP with no live authoritative server world */
    KILN_TASK_REFUSAL_NO_WORLD = 8           /* UNAVAILABLE: submit_in: no live session of that world, or not the one
                                                named (appended) */
};

/* Queue full: each owner holds at most 1024 task records, and a record counts until release() (terminal tasks
 * included: they are never silently evicted) or the owner's drain/revoke. At the cap submit returns
 * KILN_ERR_UNAVAILABLE + QUEUE_FULL with *task = 0 and changes nothing; release() of any terminal task restores one
 * slot. The same refusal reports per-owner handle exhaustion (2^40 handles per owner instance; handles never wrap).
 * Never KILN_ERR_BUFFER_TOO_SMALL. Kiln's own continuations of already-admitted managed tasks are never refused by the
 * cap (they are not submissions).
 *
 * AFTER_STEP and sessions: at acceptance submit captures the live server world's session (KilnWorldEvent.session /
 * KilnSessionInfo.sessions[KILN_WORLD_SERVER]) and the last Post edge of that session. The task becomes runnable at
 * the first later Post edge OF THE SAME SESSION (an edge seen between acceptance and queueing is not lost). A wake
 * means "Kiln's Post ran" (a scheduling hint), not that the body may touch game state. When that session ends
 * (its KILN_WORLD_STOPPING), every task still waiting on it is cancelled: KILN_TASK_CANCELLED, its done delivered as
 * Cancelled; it never wakes in the next world. With no live authoritative server world (main menu, a client joined to
 * a remote server) submit refuses UNAVAILABLE + NO_SERVER_WORLD.
 *
 * World-bound submission (submit_in, appended): the task is for the session `session` of the world `world`
 * (KILN_WORLD_SERVER or KILN_WORLD_CLIENT; 0 = the one live now; KilnWorldEvent.session / KilnSessionInfo.sessions[]).
 * Refused UNAVAILABLE + NO_WORLD when that world has no live session or it is not `session`. With AFTER_STEP the task
 * waits for that world's next Post edge (the client world: the end of its step on the game's main thread, about 60 a
 * second while in game) and is CANCELLED when that session ends, exactly as AFTER_STEP above for the server world; an
 * edge or a STOPPING of the other world changes nothing for it. Without AFTER_STEP the session is checked at
 * acceptance only and the body runs as after submit. The body always runs on a pool worker, never on the world's
 * thread: hand phase-bound work to it with KilnApi.post_world.
 */

typedef struct KilnTaskInfo {
    uint32_t size;     /* sizeof(KilnTaskInfo) of the caller (32); shorter is refused, longer accepted (see above) */
    uint32_t flags;    /* KILN_TASK_LONG_RUNNING | KILN_TASK_AFTER_STEP */
    KilnStr name;      /* 1..128 bytes of UTF-8, no NUL or control characters; copied at submit, never truncated */
    uint32_t priority; /* KILN_TASK_PRIORITY_* */
    uint32_t reserved; /* 0 */
} KilnTaskInfo;

/* What a body receives: valid only while that body runs (never keep the pointer after it returns). */
typedef struct KilnTaskRun {
    uint32_t size, reserved; /* what Kiln filled (v1: 40; check it before reading a later field), 0 */
    KilnHandle task;
    KilnOwner owner;
    uint32_t(KILN_CALL* cancelled)(const struct KilnTaskRun* run); /* 1 once cancel/drain/revoke/stop asked; poll */
    void* internal;                                                /* Kiln's; opaque, never a game pointer */
} KilnTaskRun;

typedef void(KILN_CALL* KilnTaskBodyFn)(const KilnTaskRun* run, void* user);
/* After the body (or a queued cancellation): `state` is the immutable terminal state (COMPLETED/FAULTED/CANCELLED). */
typedef void(KILN_CALL* KilnTaskDoneFn)(KilnHandle task, uint32_t state, void* user);

typedef struct KilnTasksApi {
    KilnServiceHeader header;
    /* Accept a task for `owner`: body and done both required. KILN_OK with *task set. On any other result *task = 0
     * (task non-NULL) and *refusal set (refusal non-NULL): INVALID_ARGUMENT + ARGUMENT for owner 0, a NULL
     * info/body/done/task, a short info, unknown flags, a bad name, priority > 2 or reserved != 0; REVOKED + OWNER_SEALED for a dead owner;
     * UNAVAILABLE + OWNER_SEALED / OWNER_QUARANTINED / QUEUE_FULL / CAPABILITY / SHUTDOWN / NO_SERVER_WORLD. Required
     * pointers are checked before any side effect. */
    KilnResult(KILN_CALL* submit)(KilnOwner owner, const KilnTaskInfo* info, KilnTaskBodyFn body, KilnTaskDoneFn done,
                                  void* user, KilnHandle* task, uint32_t* refusal);
    /* Queued (or AFTER_STEP waiting): CANCELLED now, its done delivered on a worker (never inline on this thread).
     * Running: requested (cancelled() returns 1); the state stays RUNNING. Terminal: no change. *state = the state
     * after the call. NOT_FOUND: unknown/foreign/released handle; REVOKED: dead owner. */
    KilnResult(KILN_CALL* cancel)(KilnOwner owner, KilnHandle task, uint32_t* state);
    /* *state = KILN_TASK_*; NOT_FOUND (state NONE) for an unknown, foreign or released handle. */
    KilnResult(KILN_CALL* status)(KilnOwner owner, KilnHandle task, uint32_t* state);
    /* Terminal only: KILN_OK and the record (and its capacity slot) is gone; KILN_OK again for a handle this owner
     * instance already released (idempotent); UNAVAILABLE for a queued or running task (nothing changes, no cancel);
     * NOT_FOUND for an unknown or foreign handle. Legal inside the task's own done. */
    KilnResult(KILN_CALL* release)(KilnOwner owner, KilnHandle task);
    /* Seal the owner FOREVER (no new submissions), cancel its queued tasks (each still gets its done), signal its
     * running ones, release its terminal records, then wait up to timeout_ms for quiescence. *running = tasks whose done
     * has not returned yet (0 = body AND done quiescent). A timeout returns KILN_OK with *running > 0: nothing is freed
     * or forced, code and `user` stay referenced. Thread rules above (WRONG_THREAD). */
    KilnResult(KILN_CALL* drain_owner)(KilnOwner owner, uint32_t timeout_ms, uint32_t* running);
    /* ---- appended (check header.size >= 56) ---- */
    /* submit, bound to `world`'s `session` (see "World-bound submission" above). Every refusal of submit, plus
     * INVALID_ARGUMENT + ARGUMENT for another world kind and UNAVAILABLE + NO_WORLD as above. */
    KilnResult(KILN_CALL* submit_in)(KilnOwner owner, const KilnTaskInfo* info, uint32_t world, uint32_t session,
                                     KilnTaskBodyFn body, KilnTaskDoneFn done, void* user, KilnHandle* task,
                                     uint32_t* refusal);
} KilnTasksApi;

#ifdef __cplusplus
}
#endif

#ifdef __cplusplus
#define KILN_TASKS_ASSERT static_assert
#else
#define KILN_TASKS_ASSERT _Static_assert
#endif
KILN_TASKS_ASSERT(sizeof(KilnTaskInfo) == 32 && offsetof(KilnTaskInfo, flags) == 4 && offsetof(KilnTaskInfo, name) == 8 &&
                      offsetof(KilnTaskInfo, priority) == 24 && offsetof(KilnTaskInfo, reserved) == 28,
                  "KilnTaskInfo v1 layout");
KILN_TASKS_ASSERT(sizeof(KilnTaskRun) == 40 && offsetof(KilnTaskRun, task) == 8 && offsetof(KilnTaskRun, owner) == 16 &&
                      offsetof(KilnTaskRun, cancelled) == 24 && offsetof(KilnTaskRun, internal) == 32,
                  "KilnTaskRun v1 layout");
KILN_TASKS_ASSERT(sizeof(KilnTasksApi) == 56 && offsetof(KilnTasksApi, submit) == 8 && offsetof(KilnTasksApi, cancel) == 16 &&
                      offsetof(KilnTasksApi, status) == 24 && offsetof(KilnTasksApi, release) == 32 &&
                      offsetof(KilnTasksApi, drain_owner) == 40 && offsetof(KilnTasksApi, submit_in) == 48,
                  "KilnTasksApi v1 layout (48 bytes, x64; 56 with submit_in appended)");
#undef KILN_TASKS_ASSERT

#endif /* KILN_TASKS_H */
