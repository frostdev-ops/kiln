/* SPDX-License-Identifier: MIT
 * Kiln-internal (NOT core/abi, NOT kiln-abi, NOT the SDK): checked with the build stamp, never an ABI promise.
 *
 * kiln.internal.tasks.host v1: the managed host's lease on the ONE kiln.tasks pool. The managed host (Kiln.Host) builds
 * managed logical tasks (with awaits and tick hops) on it, so native and managed tasks share one pool, one set of
 * stats and one unload rule. It is not a hidden service for plugins:
 *  - resolved only through the KilnApi view api_for_managed_host() (a copy of the public KilnApi with a host-only
 *    resolver; clr.cpp passes it to the existing Entry.Init). The plugin, service and public views never resolve or
 *    publish a "kiln.internal.tasks.host" name, and the host cannot publish it either (the core's kiln.internal.
 *    name rule, api.cpp);
 *  - same build: the host calls build_stamp() FIRST and compares it with its own compiled stamp (the build identity
 *    of native/cmake/kiln_build_stamp.sh). On a mismatch, a version below 1, or a short table, the host reports
 *    tasks.pool unavailable and calls nothing else; no function table crosses builds;
 *  - not a sandbox: reflection into Kiln.Host or native code in the process can reach anything.
 *
 * Why a lease and not public submit: a managed callback is a JIT thunk outside any PE module, so the public module
 * reference cannot pin it. The host instead registers ONE stable native-callable dispatcher (an UnmanagedCallersOnly
 * method of Kiln.Host, which is not collectible) and passes an opaque GCHandle per task as `host_state`. The core runs
 * its OWN native trampoline on a pool worker, which calls the dispatcher. The managed delegate, its GCHandle and the
 * mod's collectible context stay pinned by the host until the lease is quiescent. The public plugin pin checks are
 * unchanged.
 *
 * Accounting (one source of truth with the public tasks): a lease is one logical task = one record against its
 * owner's capacity (1024) and quota, admitted like a public submit (root admission can be sealed). Its segments (the
 * first, and one continuation per await) are ordinary pool work that occupies a worker only while it runs; a
 * suspended lease occupies none. Continuations NEVER spend quota and are accepted after the owner is sealed, so a
 * cancelled task can always run its finalisation. A lease is quiescent only after finish() AND no segment, tick hop or
 * apply of it is in flight or pending; only then does seal/drain count it out.
 */
#pragma once
#include <cstddef>
#include <cstdint>

#include "kiln_api.h"
#include "services/kiln_tasks.h"

#define KILN_TASKS_HOST_SERVICE "kiln.internal.tasks.host"
#define KILN_TASKS_HOST_VERSION 1u

extern "C" {

typedef uint64_t KilnLease; /* a managed logical task; the same value as its public KilnHandle (status works on it) */

/* What the core's trampoline tells the host's dispatcher to run. */
enum {
    KILN_LEASE_SEGMENT = 1,  /* run the next segment (the first, or the continuation of the armed await) */
    KILN_LEASE_TICK_HOP = 2  /* run a bounded tick apply (OnTick/ToTick/PostToTick) on the tick thread */
};

/* The host's one dispatcher. `cancelled` is the lease's cancellation state at dispatch (the host also polls
 * is_cancelled). Runs on a pool worker (SEGMENT), on the server world's tick (TICK_HOP) and on the thread that
 * destroys the server world (the cancelled hops of its STOPPING), so it can run on several threads at once: it must
 * be reentrant and thread-safe. A managed exception is caught by the host before it returns here, so it never crosses
 * the ABI and never quarantines the owner; a structured fault is contained by the core (the task cancelled, its owner
 * quarantined, kiln.log). */
typedef void(KILN_CALL* KilnHostDispatchFn)(KilnLease lease, uint32_t what, uint32_t cancelled, void* host_state);

/* The host's end notification (bind_ended): the lease is quiescent. Called once for every accepted lease, from the
 * core's done trampoline on a pool worker, after its last segment returned and its last tick hop was dispatched; also
 * for a lease that was cancelled before its first segment (cancel, seal, revoke, its session ending), which never
 * reaches the dispatcher. `state` is the lease's final KILN_TASK_* state. After it returns nothing of the lease calls
 * the host again, so this is where the host drops its GCHandle. Like every done, it is not guaranteed once the
 * process exit began. */
typedef void(KILN_CALL* KilnHostEndedFn)(KilnLease lease, uint32_t state, void* host_state);

/* Bounded diagnostics snapshot row (tick-stats and crash reports read it). */
typedef struct KilnLeaseSnapshot {
    uint32_t size, state;      /* KILN_TASK_* */
    KilnOwner owner;
    KilnLease lease;
    uint64_t running_ns;       /* time in the current segment, 0 when suspended */
    char name[129];            /* copied task name, NUL-terminated */
    char reserved[7];
} KilnLeaseSnapshot;

/* Diagnostics rows (issue #49): fixed-size copies out of the core's lock-free tasks_stats store.
 * Names are the admission copies (at most 128 bytes, NUL-terminated). Times are nanoseconds on the
 * pool's monotonic clock; cpu_ns is the executing threads' CPU (on Windows, GetThreadTimes:
 * quantum-granular, see tasks_win.cpp). */
typedef struct KilnTaskStatsOwnerRow {
    uint32_t size, reserved;
    KilnOwner owner;
    uint64_t instance;
    uint64_t queued_current, queued_max, running_current, running_max;
    uint64_t completed, faulted, cancelled; /* the owner instance's totals */
    uint64_t cpu_ns, wall_ns, longest_ns;   /* the current tick-stats window */
    uint64_t queued_too_long, running_too_long, queue_full, unload_blocked;
    char owner_name[129];
    char longest_name[129];
    char reserved2[6];
} KilnTaskStatsOwnerRow;

typedef struct KilnTaskStatsRunningRow {
    uint32_t size, reserved;
    KilnOwner owner;
    KilnLease lease;
    uint64_t thread_id;   /* 0 while suspended */
    uint64_t started_ns;  /* the task's admission on the pool clock */
    uint64_t elapsed_ns;  /* the current segment's (or the task's, while suspended) age */
    uint32_t state;       /* 1 running, 2 suspended, 3 on-tick (tasks_stats EntryState) */
    uint32_t reserved3;
    char owner_name[129];
    char task_name[129];
    char reserved2[6];
} KilnTaskStatsRunningRow;

typedef struct KilnTaskStatsFaultRow {
    uint32_t size, reserved;
    uint64_t sequence, owner, handle, at_ns;
    uint32_t state, code;  /* KILN_TASK_* and the structured fault code (0 when none) */
    char owner_name[129];
    char task_name[129];
    char reserved2[6];
} KilnTaskStatsFaultRow;

typedef struct KilnTaskStatsTotals {
    uint32_t size, owners;
    uint32_t workers, long_running;
    uint64_t queued, running, completed, faulted, cancelled, running_tasks, faults;
} KilnTaskStatsTotals;

typedef struct KilnTasksHostApi {
    KilnServiceHeader header;  /* size, version (KILN_TASKS_HOST_VERSION) */
    /* The core's build identity ("<version><build metadata>", "" without metadata: then the host refuses tasks). */
    const char*(KILN_CALL* build_stamp)(void);
    /* Bind the host's one dispatcher (once per process; INVALID_ARGUMENT if already bound to another). */
    KilnResult(KILN_CALL* bind)(KilnHostDispatchFn dispatch);
    /* Admit a logical root for `owner` (same refusals and *refusal semantics as public submit, AFTER_STEP included);
     * its first segment is queued. *lease is also a public handle: status/release apply, except that releasing a lease
     * returns UNAVAILABLE while a tick hop of it is pending, even after the lease ended (its done is held back until
     * the hop ran): the host releases a lease only after its tick hops have been dispatched. */
    KilnResult(KILN_CALL* admit)(KilnOwner owner, const KilnTaskInfo* info, void* host_state, KilnLease* lease,
                                 uint32_t* refusal);
    /* From inside the lease's running segment: it awaits; exactly one resumption may follow. INTERNAL on misuse. */
    KilnResult(KILN_CALL* arm)(KilnLease lease, uint64_t* registration);
    /* Queue that one continuation segment (held until the arming segment returned: segments never overlap). Never
     * refused by the capacity or by a seal; INTERNAL for a duplicate or unknown registration. */
    KilnResult(KILN_CALL* resume)(KilnLease lease, uint64_t registration);
    /* Arm a Post-edge resumption: eligible at the next Post edge of the session live NOW; cancelled
     * (dispatched with cancelled=1) when that session ends; UNAVAILABLE + NO_SERVER_WORLD with no live server world. */
    KilnResult(KILN_CALL* resume_after_step)(KilnLease lease, uint64_t registration, uint32_t* refusal);
    /* Queue a bounded tick hop for the lease (dispatched as TICK_HOP on the tick before the owner's Pre; per owner at
     * most 64 hops and 2 ms per server step, and 4 ms per step across owners round-robin; excess deferred FIFO, never
     * dropped). Allowed after the owner is sealed; a hop of a retiring lease is dispatched with cancelled=1. */
    KilnResult(KILN_CALL* tick_hop)(KilnLease lease);
    /* The logical task ended, exactly once: outcome = Completed (0) / Faulted (1) / OwnTokenCancelled (2), the same
     * precedence as native (a normal finish wins over a late cancel; own-token cancellation is Cancelled only after a
     * request). INTERNAL on a second call. */
    KilnResult(KILN_CALL* finish)(KilnLease lease, uint32_t outcome);
    /* The lease's cancellation state (cancel/drain/revoke/STOPPING asked). Any thread; never blocks. */
    uint32_t(KILN_CALL* is_cancelled)(KilnLease lease);
    /* drain_owner(0) for the host: seal, cancel queued roots, signal running leases, *outstanding = not yet quiescent.
     * Never waits; the host's own off-tick join polls it. */
    KilnResult(KILN_CALL* seal_owner)(KilnOwner owner, uint32_t* outstanding);
    /* A bounded copy of the running/suspended leases (at most `capacity` rows); BUFFER_TOO_SMALL sets *count to the
     * number needed. Any thread; never blocks on a task. */
    KilnResult(KILN_CALL* snapshot)(KilnLeaseSnapshot* rows, uint32_t capacity, uint32_t* count);
    /* ---- appended (same version: the table is checked by size and build stamp) ---- */
    /* Bind the host's end notification (once per process; INVALID_ARGUMENT if already bound to another). With it
     * bound, the core releases a lease's record itself right after the notification returned: the host never calls
     * release, and ended leases do not count against their owner's capacity. Without it nothing changes: a lease's
     * record stays until release or the owner's drain. Bind it before the first admit. */
    KilnResult(KILN_CALL* bind_ended)(KilnHostEndedFn ended);
    /* resume_after_step for the session the host asked the window in: armed only while `session` is the live server
     * session (one snapshot for the compare and the arming), else UNAVAILABLE + NO_SERVER_WORLD with the registration
     * still armed. A window asked for in one world is never moved into the next. */
    KilnResult(KILN_CALL* resume_after_step_in)(KilnLease lease, uint64_t registration, uint32_t session,
                                                uint32_t* refusal);
    /* 1 on one of the pool's worker threads (inside a segment or an end notification), else 0. Never blocks. */
    uint32_t(KILN_CALL* on_pool_thread)(void);
    /* The live server world's session (the one tick hops and after-step resumptions use); 0 with none, from its
     * STOPPING on. Any thread; never blocks on a task. */
    uint32_t(KILN_CALL* server_session)(void);
    /* ---- appended (issue #49; same version, checked by size and build stamp): task diagnostics
     * for Kiln.Host's crash reports. All read the lock-free stats store: no pool lock, no allocation,
     * bounded retries; UNAVAILABLE when a snapshot could not be copied, NOT_FOUND past the last row. */
    /* The number of owner-instance rows (at most 64). */
    uint32_t(KILN_CALL* stats_owner_count)(void);
    KilnResult(KILN_CALL* stats_owner_row)(uint32_t index, KilnTaskStatsOwnerRow* row);
    KilnResult(KILN_CALL* stats_running_row)(uint32_t index, KilnTaskStatsRunningRow* row);
    KilnResult(KILN_CALL* stats_fault_row)(uint32_t index, KilnTaskStatsFaultRow* row);
    KilnResult(KILN_CALL* stats_totals)(KilnTaskStatsTotals* totals);
} KilnTasksHostApi;
}

static_assert(sizeof(KilnLeaseSnapshot) == 168, "KilnLeaseSnapshot v1");
static_assert(sizeof(KilnTaskStatsOwnerRow) == 400 && sizeof(KilnTaskStatsRunningRow) == 320 &&
                  sizeof(KilnTaskStatsFaultRow) == 312 && sizeof(KilnTaskStatsTotals) == 72,
              "kiln.tasks diagnostics rows (issue #49)");
static_assert(sizeof(KilnTasksHostApi) == 168 && offsetof(KilnTasksHostApi, build_stamp) == 8 &&
                  offsetof(KilnTasksHostApi, bind) == 16 && offsetof(KilnTasksHostApi, admit) == 24 &&
                  offsetof(KilnTasksHostApi, arm) == 32 && offsetof(KilnTasksHostApi, resume) == 40 &&
                  offsetof(KilnTasksHostApi, resume_after_step) == 48 && offsetof(KilnTasksHostApi, tick_hop) == 56 &&
                  offsetof(KilnTasksHostApi, finish) == 64 && offsetof(KilnTasksHostApi, is_cancelled) == 72 &&
                  offsetof(KilnTasksHostApi, seal_owner) == 80 && offsetof(KilnTasksHostApi, snapshot) == 88 &&
                  offsetof(KilnTasksHostApi, bind_ended) == 96 &&
                  offsetof(KilnTasksHostApi, resume_after_step_in) == 104 &&
                  offsetof(KilnTasksHostApi, on_pool_thread) == 112 &&
                  offsetof(KilnTasksHostApi, server_session) == 120 &&
                  offsetof(KilnTasksHostApi, stats_owner_count) == 128 &&
                  offsetof(KilnTasksHostApi, stats_owner_row) == 136 &&
                  offsetof(KilnTasksHostApi, stats_running_row) == 144 &&
                  offsetof(KilnTasksHostApi, stats_fault_row) == 152 &&
                  offsetof(KilnTasksHostApi, stats_totals) == 160,
              "kiln.internal.tasks.host v1 (Kiln-internal; checked with the build stamp, not an ABI promise)");

/* Not a member: the core-side bootstrap (api.cpp). Returns the managed host's KilnApi view (a copy of the public one
 * whose service resolver also resolves kiln.internal.tasks.host); clr.cpp hands it to Entry.Init. */
namespace kiln {
const KilnApi* api_for_managed_host();
}
