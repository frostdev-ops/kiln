// SPDX-License-Identifier: MIT
// kiln.tasks' model: owners, tasks, admission, fair ready queues, cancellation, release and callback
// quiescence. No thread, no clock, no user callback of its own: the pool's workers (tasks.h) run
// the work dequeue() hands them and report back; the public kiln.tasks v1 table and the managed host's leases sit on
// top. One mutex guards all state, and nothing here calls user code, so no callback can ever run under it.
// Host-portable (only kiln_api.h).
//
// Life of a task (every accepted submission gets exactly one done delivery):
//   submit -> Queued (ready, or waiting for a Post edge when AFTER_STEP) -> dequeue -> Running (one segment at a
//   time; a managed lease may suspend between segments) -> terminal (Completed / Faulted / Cancelled, published once)
//   -> its done delivery is queued -> dequeue -> done runs -> done_returned (callback quiescence).
//   A record stays in its owner's table, counted against the owner's capacity, until release() (terminal only) or
//   the owner's seal/retire; release inside done is legal: the delivery keeps the record alive until done returns.
#pragma once
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

#include "kiln_api.h"

namespace kiln::tasks {

// Frozen tags (the same values are public in kiln_tasks.h).
enum class State : uint32_t { None = 0, Queued = 1, Running = 2, Completed = 3, Faulted = 4, Cancelled = 5 };
enum class Priority : uint32_t { Normal = 0, Low = 1, High = 2 };
enum class Refusal : uint32_t {
    None = 0,
    QueueFull = 1,
    OwnerSealed = 2,
    OwnerQuarantined = 3,
    Capability = 4,
    Argument = 5,
    Shutdown = 6,
    NoServerWorld = 7,
    NoWorld = 8,  // kiln_tasks.h submit_in: the world named has no live session, or not the one named
};
// The executor's classification of how a task's last segment ended (native bodies and managed leases).
enum class Outcome : uint32_t { Completed, Faulted, OwnTokenCancelled };
// Which worker kind runs a piece of work: the normal pool, or a dedicated LongRunning thread.
enum class Slot : uint32_t { Normal = 0, LongRunning = 1 };

inline bool terminal(State s) { return s == State::Completed || s == State::Faulted || s == State::Cancelled; }

// The worlds whose sessions and Post edges the model follows: KILN_WORLD_SERVER and KILN_WORLD_CLIENT. Each has its
// own session line (a new server session never ends a client one, and the reverse); the prediction world has none.
constexpr bool session_world(uint32_t world) { return world == KILN_WORLD_SERVER || world == KILN_WORLD_CLIENT; }
constexpr std::size_t world_line(uint32_t world) { return world == KILN_WORLD_CLIENT ? 1 : 0; }
constexpr std::size_t kWorldLines = 2;

constexpr std::size_t kNameMax = 128;  // UTF-8 bytes
// A task name: 1..128 bytes of valid UTF-8 without NUL or control characters (never truncated: it is identity).
bool valid_name(std::string_view name);
// An owner name for diagnostics: copied, cut at a character boundary to at most 128 bytes (it survives revocation).
std::string bounded_owner_name(std::string_view name);

struct Limits {
    uint32_t owner_capacity = 1024;  // records per owner, retained terminal records included
    uint32_t owner_running = 2;      // normal-slot work one owner may run at once
    uint32_t long_running_per_owner = 2;
    uint32_t long_running_global = 8;
    uint64_t max_sequence = (uint64_t{1} << 40) - 1;  // handles per owner instance; exhaustion refuses, never wraps
};

// What the executor needs to run the work; opaque to the model.
struct Payload {
    uintptr_t body = 0, done = 0, user = 0;
};

struct SubmitArgs {
    std::string_view name;
    Priority priority = Priority::Normal;
    bool long_running = false;
    bool lease = false;       // a managed logical task (kiln.internal.tasks.host): its outcome comes from finish()
    bool after_step = false;  // runnable only after a Post edge of `session` newer than `edge`
    uint32_t world = KILN_WORLD_SERVER;  // AFTER_STEP: the world of `session` (session_world; else ARGUMENT)
    uint64_t session = 0;     // AFTER_STEP: that world's live session at acceptance (never 0: ARGUMENT)
    uint64_t edge = 0;        // AFTER_STEP: that session's last Post edge the caller observed at acceptance
    Payload payload;
};

struct Record;
struct Owner;

// One unit of pool work: a task's body (its first segment), a continuation segment, or its done delivery.
struct Work {
    enum class Kind : uint32_t { None, Body, Continuation, Done };
    Kind kind = Kind::None;
    Slot slot = Slot::Normal;
    KilnOwner owner = 0;
    KilnHandle task = 0;
    State final_state = State::None;  // Done: the immutable terminal state the done callback receives
    Payload payload;
    std::shared_ptr<Record> record;  // keeps the record alive while the work runs, even if released meanwhile
    // The cancelled() poll for a running segment: lock-free, valid while this Work is.
    bool cancel_requested() const;
};

struct Counts {
    std::size_t owners = 0;       // owner instances known (tombstones of retired ones included)
    std::size_t records = 0;      // records in owner tables (counted against capacity)
    std::size_t ready = 0;        // work items ready to dequeue (bodies, continuations, done deliveries)
    std::size_t ready_max = 0;    // the most ever ready at once
    std::size_t waiting = 0;      // AFTER_STEP tasks waiting for an edge
    std::size_t outstanding = 0;  // accepted tasks whose done has not returned yet
    std::size_t running = 0;      // work items dequeued and not yet reported back
};

class Model {
public:
    explicit Model(Limits limits = {});
    ~Model();
    Model(const Model&) = delete;
    Model& operator=(const Model&) = delete;

    // ---- owners: one instance per loaded owner ----
    // A new instance for `owner` (a retired owner id may be opened again: a new instance, its old handles stay
    // foreign). INVALID_ARGUMENT: owner 0 or already open; UNAVAILABLE: instance numbers exhausted.
    KilnResult open_owner(KilnOwner owner, std::string_view name);
    // The binding's open: create the owner's instance only when it has none. OK: open (new or existing); REVOKED: its
    // instance was retired (a tombstone is never replaced: registry owner ids are never reused); UNAVAILABLE: the
    // owner has no instance and the process exit closed admission (seal_all), or instance numbers are exhausted. An
    // existing instance is found first, also after the exit (sealed by then), so drain_owner reports its real count.
    KilnResult ensure_owner(KilnOwner owner, std::string_view name);
    // drain_owner with no wait: seal forever, cancel queued roots (each still gets its done), signal running ones,
    // release terminal records; later terminal records release themselves. *outstanding = tasks whose done has not
    // returned (0 = body AND done quiescent).
    KilnResult seal(KilnOwner owner, uint32_t* outstanding);
    // The owner's first native fault: no new roots, queued roots cancelled; running ones and records stay.
    KilnResult quarantine(KilnOwner owner);
    // The owner was revoked: seal, signal running, release every terminal record; the instance stays a tombstone.
    KilnResult retire(KilnOwner owner, uint32_t* outstanding);
    KilnResult outstanding(KilnOwner owner, uint32_t* count) const;
    // Process exit: seal every open owner (queued roots cancelled, running ones signalled, terminal records dropped).
    void seal_all();

    // ---- the public operations (services/tasks.cpp binds them) ----
    // *task = 0 and *why set on every non-OK return; *why untouched on OK.
    KilnResult submit(KilnOwner owner, const SubmitArgs& args, KilnHandle* task, Refusal* why);
    KilnResult cancel(KilnOwner owner, KilnHandle task, State* state);
    KilnResult status(KilnOwner owner, KilnHandle task, State* state) const;
    // Terminal only: OK and the capacity slot returns; OK again for a handle this owner instance already released;
    // UNAVAILABLE for a queued or running task (nothing changes); NOT_FOUND for an unknown or foreign handle.
    KilnResult release(KilnOwner owner, KilnHandle task);

    // ---- managed leases: one segment at a time, continuations never spend admission quota ----
    // The running segment of `task` awaits: exactly one resumption may follow. INTERNAL: not a running segment, or
    // already armed (a bug in the caller, never silently merged).
    KilnResult arm(KilnHandle task, uint64_t* registration);
    // Queue that resumption (after the arming segment returned, so segments never overlap). Allowed after seal,
    // quarantine and retire (finalization). INTERNAL: unknown, not armed or wrong registration (a duplicate).
    KilnResult resume(KilnHandle task, uint64_t registration);
    // A resumption at a later Post edge of the live server `session` (captured `edge`), as AFTER_STEP: parked while
    // the arming segment still runs (segments never overlap); when that session ends (or the task is cancelled,
    // sealed or retired) it is queued at once with its cancellation set, never carried into another session.
    // INTERNAL: as resume.
    KilnResult resume_after(KilnHandle task, uint64_t registration, uint64_t session, uint64_t edge);
    // The same for a session of `world` (session_world; else INTERNAL). The server form above is world SERVER.
    KilnResult resume_after(KilnHandle task, uint64_t registration, uint32_t world, uint64_t session, uint64_t edge);
    // A lease's logical end, exactly once, from inside its running segment (not armed): the segment's return then
    // publishes this outcome with the usual precedence. INTERNAL: not a running lease segment, armed, or a second call.
    KilnResult finish(KilnHandle task, Outcome outcome);
    // A lease's cancellation state (cancel, drain, revoke, exit or its session's end asked); false for an unknown one.
    bool cancel_requested(KilnHandle task) const;
    // A tick hop of a lease is pending / ended: the lease is not quiescent (its done is held back) while one is.
    // INTERNAL: not an outstanding lease (hop_begin) or no hop pending (hop_end).
    // An outstanding lease's owner and payload user (the core's per-lease context); false otherwise.
    bool lease_of(KilnHandle task, KilnOwner* owner, uintptr_t* user) const;
    KilnResult hop_begin(KilnHandle task);
    KilnResult hop_end(KilnHandle task);

    // ---- Post edges and sessions ----
    // A Post edge of the live server `session`: AFTER_STEP tasks captured in THAT session before `edge` become ready;
    // an edge of any other session releases none of them. Sessions and, inside one session, edges only move forward:
    // an older or repeated one changes nothing. A newer session ends every older one (see end_session). A submission
    // that captured an edge older than its session's last one is ready at once (no lost wakeup).
    void edge(uint64_t session, uint64_t edge);
    // The server `session` ended (its KILN_WORLD_STOPPING), and with it every older one: each AFTER_STEP task still
    // waiting on one of them is CANCELLED, its done delivered as Cancelled; none ever wakes in a later session. A later
    // submission that captured an ended session is accepted and cancelled at once (never transferred).
    void end_session(uint64_t session);
    // The same per world: each world in session_world has its own line (sessions, edges, ends), and an edge or end of
    // one world never releases or cancels a task waiting on another. The forms above are world SERVER.
    void edge(uint32_t world, uint64_t session, uint64_t edge);
    void end_session(uint32_t world, uint64_t session);

    // ---- executor ----
    // The next work for a free worker of `slot`, round-robin across owners, under the caps; marks it running.
    bool dequeue(Slot slot, Work* out);
    // A body or continuation returned: suspended (it armed a resumption) or ended with `outcome`.
    // Precedence: Completed wins over a late cancel; OwnTokenCancelled is Cancelled only if cancel was requested,
    // else Faulted. Publishes the terminal state once and queues the done delivery. INTERNAL on a misuse.
    KilnResult segment_ended(const Work& work, bool suspended, Outcome outcome);
    // The done callback returned: the task is quiescent.
    KilnResult done_returned(const Work& work);

    Counts counts() const;
    // Diagnostics (the watchdog): a record's state by handle alone, also after its owner was retired, for as long as
    // the record is outstanding; None once released or unknown. Not an owner-scoped public lookup.
    State diag_state(KilnHandle task) const;
    std::string owner_name(KilnOwner owner) const;  // the bounded copy, also after retire ("" when unknown)

    // Test seam: called (outside the model mutex) at the points below; nullptr in production.
    enum class Point : uint32_t {
        SubmitBeforePublish,  // outside the model mutex
        SubmitPublishing,     // under it: the record is in its owner's table, not yet queued (a test may throw here)
    };
    void set_test_hook(std::function<void(Point)> hook);
    bool lock_held_by_anyone() const { return locked_.load(); }

private:
    class Lock;

    std::shared_ptr<Owner> find_open(KilnOwner owner) const;
    std::shared_ptr<Record> find_record(const Owner& o, KilnHandle task, bool* foreign, bool* released) const;
    void enqueue(Owner& o, const std::shared_ptr<Record>& r, Work::Kind kind);
    void cancel_queued(Owner& o, const std::shared_ptr<Record>& r);
    void publish_terminal(Owner& o, const std::shared_ptr<Record>& r, State state);
    void drop_from_table(Owner& o, Record& r);
    void cancel_all_queued(Owner& o);
    void signal_running(Owner& o);
    void drop_terminal(Owner& o);
    void leave_ring_if_done(Owner& o);
    // AFTER_STEP tasks (and released continuations) of `world` captured in a session older than `session`
    void cancel_waiting_before(uint32_t world, uint64_t session);
    void release_continuation(Owner& o, const std::shared_ptr<Record>& r);
    void place_after(Owner& o, const std::shared_ptr<Record>& r, uint32_t world, uint64_t session, uint64_t edge);
    std::shared_ptr<Record> find_lease(KilnHandle task) const;
    void note_ready(std::ptrdiff_t delta);
    void hook(Point p) const;

    Limits limits_;
    mutable std::mutex mutex_;
    mutable std::atomic<bool> locked_{false};
    std::unordered_map<KilnOwner, std::shared_ptr<Owner>> current_;   // the latest instance per owner id
    std::unordered_map<uint64_t, std::shared_ptr<Owner>> instances_;  // every instance (tombstones of retired ones)
    std::vector<std::shared_ptr<Owner>> ring_;                        // owners that may still have work, in order
    std::size_t cursor_[2] = {0, 0};  // the round-robin turn of Normal and of LongRunning dequeues, independent
    uint64_t next_instance_ = 1;
    // One session line per world (world_line): the newest session an edge or end named, its last Post edge, and the
    // session up to which every one has ended.
    struct Line {
        uint64_t session = 0;
        uint64_t last_edge = 0;
        uint64_t ended = 0;
    };
    Line lines_[kWorldLines];
    bool closing_ = false;    // the process exit closed root admission for good (seal_all)
    std::size_t ready_ = 0, ready_max_ = 0, running_ = 0;
    uint32_t long_running_ = 0;
    std::function<void(Point)> hook_;
};

}  // namespace kiln::tasks
