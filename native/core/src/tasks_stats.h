// SPDX-License-Identifier: MIT
// kiln.tasks diagnostics (issue #49): an immortal-shape, fixed-size, lock-free store that tick-stats
// (once a minute) and crash reports (at any moment, from any thread) read without ever taking the
// pool or model lock, and without allocating on a read path.
//
// Shape: 64 owner slots (one per owner *instance*, so a reload never mixes with the previous load),
// 64 running-task entries and a 16-entry fault ring. Every field is a std::atomic (the store is clean
// under ThreadSanitizer). Compound updates of one slot/entry are serialised by a per-slot writer flag
// and published with a seqlock sequence: writers bump it odd, write, bump it even. Readers copy
// between two sequence reads and retry at most kCopyRetries times; past that they report the snapshot
// unavailable instead of spinning on a writer stuck mid-update (a crash inside an update, a wedged
// thread). Names are copied at admission (at most 128 bytes, the task-name contract's cap); task names
// reach this store only after valid_name() accepted them, and JSON rendering escapes what it emits.
//
// Cost on the hot path: an admission claims a slot (brief claim flag), a segment start/end takes the
// owner's writer flag for a few relaxed atomic updates plus the caller's two clock reads (wall and
// thread CPU, in tasks.cpp). Nothing here locks the pool, allocates, or waits on a reader.
#pragma once
#include <array>
#include <atomic>
#include <cstdint>
#include <string>
#include <string_view>

namespace kiln::tasks_stats {

inline constexpr std::size_t kMaxOwners = 64;    // tick-stats rows; beyond this owners_truncated
inline constexpr std::size_t kMaxRunning = 64;   // running-task entries (crash reports show at most 16)
inline constexpr std::size_t kMaxCrashRunning = 16;
inline constexpr std::size_t kMaxFaults = 16;
inline constexpr std::size_t kNameBytes = 128;
inline constexpr int kCopyRetries = 8;

enum class EntryState : uint32_t { Queued = 0, Running = 1, Suspended = 2, OnTick = 3, DonePending = 4 };

// Final states, matching kiln.tasks (KILN_TASK_*).
inline constexpr uint32_t kStateCompleted = 3, kStateFaulted = 4, kStateCancelled = 5;

struct AdmitResult {
    int owner_slot = -1;  // -1: the owner table is full (the task still runs; it is not a row)
    int entry = -1;       // -1: the running table is full (likewise)
};

struct OwnerSnapshot {
    bool present = false;
    uint64_t owner = 0, instance = 0;
    char owner_name[kNameBytes + 1] = {};
    uint64_t queued_current = 0, queued_max = 0, running_current = 0, running_max = 0;
    uint64_t completed = 0, faulted = 0, cancelled = 0;
    uint64_t cpu_ns = 0, wall_ns = 0, longest_ns = 0;
    char longest_name[kNameBytes + 1] = {};
    uint64_t queued_too_long = 0, running_too_long = 0, queue_full = 0, unload_blocked = 0;
};

struct PoolSnapshot {
    uint32_t workers = 0, long_running = 0;
    uint64_t queue_current = 0, queue_max = 0;
};

struct ApplySnapshot {
    uint64_t applied = 0, deferred = 0, cancelled = 0, apply_ns = 0;
    uint64_t owner_budget_stops = 0, step_budget_stops = 0, owners_served = 0, backlog = 0;
};

struct WindowSnapshot {
    uint64_t id = 0, span_ns = 0, age_ns = 0;
    PoolSnapshot pool;
    std::array<OwnerSnapshot, kMaxOwners> owners{};
    std::size_t owner_count = 0;
    bool owners_truncated = false;
    bool slot_unavailable = false;  // a slot could not be copied (stuck writer): its row is missing
    ApplySnapshot apply;
    uint64_t hops_refused = 0;
};

struct RunningSnapshot {
    bool present = false;
    uint64_t owner = 0, instance = 0, handle = 0, thread_id = 0;
    uint64_t started_ns = 0, elapsed_ns = 0;
    EntryState state = EntryState::Queued;
    char owner_name[kNameBytes + 1] = {};
    char task_name[kNameBytes + 1] = {};
};

struct FaultSnapshot {
    bool present = false;
    uint64_t sequence = 0, owner = 0, handle = 0, at_ns = 0;
    uint32_t state = 0, code = 0;
    char owner_name[kNameBytes + 1] = {};
    char task_name[kNameBytes + 1] = {};
};

struct CrashSnapshot {
    bool unavailable = false;  // a copy kept changing (or a sequence was odd) past the retry bound
    std::array<RunningSnapshot, kMaxRunning> running{};
    std::size_t running_count = 0;  // at most kMaxCrashRunning rows are filled for display
    uint64_t running_total = 0;
    std::array<FaultSnapshot, kMaxFaults> faults{};
    std::size_t fault_count = 0;
};

struct Totals {
    bool unavailable = false;
    uint32_t owners = 0, workers = 0, long_running = 0;
    uint64_t queued = 0, running = 0, completed = 0, faulted = 0, cancelled = 0;
    uint64_t running_tasks = 0, faults = 0;
};

class Store {
public:
    Store();
    Store(const Store&) = delete;
    Store& operator=(const Store&) = delete;

    // The wall clock baseline for the first window's span (the pool's creation time).
    void begin(uint64_t now_ns);
    void set_pool_counts(uint32_t workers, uint32_t long_running);

    // ---- writers (pool workers / submit / tick; brief, lock-free towards readers) ----
    // A task was accepted: claims (or finds) its owner-instance slot and a running entry, copies the
    // names, queued++. The owner name is the bounded registry copy.
    AdmitResult admit(uint64_t owner, uint64_t instance, std::string_view owner_name, std::string_view task_name,
                      uint64_t handle, uint64_t now_ns);
    // The owner instance is being retired/sealed for good: after its last window row is collected and
    // nothing is queued or running, the slot returns to the free pool.
    void close_instance(int owner_slot);
    void close_owner(uint64_t owner);  // every active slot of the owner (retire/drain)
    void segment_started(int owner_slot, int entry, uint64_t thread_id, uint64_t now_ns);
    // A suspended lease's continuation was queued (resume / resume_after): queued counts it again
    // until its segment starts. No entry, or not suspended: nothing changes.
    void continuation_queued(int owner_slot, int entry);
    // A segment returned: running--, CPU/wall in, longest updated; suspended: the entry stays as a
    // suspended lease, else it waits for terminal() at its done.
    void segment_ended(int owner_slot, int entry, uint64_t cpu_ns, uint64_t wall_ns, bool suspended);
    void set_on_tick(int entry, uint64_t thread_id, uint64_t now_ns);
    void end_on_tick(int entry);
    int entry_of_handle(uint64_t handle) const;
    void note_fault_code(int entry, uint32_t code);
    // The task's done is about to run: terminal counter in (exactly once per accepted task), the
    // running entry is released. A faulted task also lands in the fault ring.
    void terminal(int owner_slot, int entry, uint32_t final_state, uint32_t code, uint64_t now_ns);
    void note_too_long(int owner_slot, bool running);
    void note_queue_full(uint64_t owner);
    void note_unload_blocked(uint64_t owner);
    void note_queue_depth(uint64_t depth);
    // Tick hops (the apply path), accumulated over the window and exchanged out by collect_window.
    void record_apply(uint64_t applied, uint64_t cancelled, uint64_t apply_ns, uint64_t owners_served,
                      bool owner_budget_stop, bool step_budget_stop, uint64_t deferred_now);
    void note_hop_refused();

    // ---- readers (no pool lock, no allocation, bounded retries) ----
    // Once a minute: copies at most 64 slots, then resets the window fields of the slots it copied
    // (maxima restart from the current values). `backlog`: tick hops pending at collection.
    WindowSnapshot collect_window(uint64_t now_ns, uint64_t backlog, uint64_t queue_current);
    CrashSnapshot snapshot_crash(uint64_t now_ns) const;
    Totals totals(uint64_t now_ns) const;
    // One owner row / running row / fault row by index, for the managed host's crash supplier
    // (KilnTasksHostApi's appended diagnostics members). False: no such row (or copy unavailable).
    bool owner_row(uint32_t index, OwnerSnapshot* out) const;
    uint32_t owner_count() const;
    bool running_row(uint32_t index, uint64_t now_ns, RunningSnapshot* out) const;
    bool fault_row(uint32_t index, FaultSnapshot* out) const;

    // Test seams: a writer stuck mid-update (an odd sequence that never closes).
    void force_seq_odd_for_test(int owner_slot);
    void clear_seq_odd_for_test(int owner_slot);
    void force_entry_seq_odd_for_test(int entry);
    void clear_entry_seq_odd_for_test(int entry);
    ~Store();

private:
    struct Slot;
    struct Entry;
    struct FaultEntry;
    Slot& slot_at(int i) { return *slots_[static_cast<std::size_t>(i)]; }
    const Slot& slot_at(int i) const { return *slots_[static_cast<std::size_t>(i)]; }
    Entry& entry_at(int i) { return *entries_[static_cast<std::size_t>(i)]; }
    const Entry& entry_at(int i) const { return *entries_[static_cast<std::size_t>(i)]; }
    bool copy_slot(const Slot& s, OwnerSnapshot* out) const;
    bool reset_window(Slot& s);
    void record_fault(const Entry& e, uint32_t final_state, uint32_t code, uint64_t now_ns);

    // Fixed storage, allocated once at construction and never again (the production store lives in
    // the pool's Core, which is itself immortal in the core).
    std::array<Slot*, kMaxOwners> slots_{};
    std::array<Entry*, kMaxRunning> entries_{};
    std::array<FaultEntry*, kMaxFaults> faults_{};
    mutable std::atomic_flag claim_ = ATOMIC_FLAG_INIT;  // admission claims only; readers never take it
    std::atomic<uint64_t> truncated_instances_{0};
    std::atomic<uint32_t> pool_workers_{0}, pool_long_running_{0};
    std::atomic<uint64_t> queue_current_{0}, queue_max_window_{0};
    std::atomic<uint64_t> window_id_{0}, last_collect_ns_{0};
    std::atomic<uint64_t> apply_applied_{0}, apply_deferred_{0}, apply_cancelled_{0}, apply_ns_{0};
    std::atomic<uint64_t> apply_owner_stops_{0}, apply_step_stops_{0}, apply_owners_served_{0};
    std::atomic<uint64_t> hops_refused_{0};
    std::atomic<uint64_t> fault_sequence_{0};
};

// The `"tasks":{...}` JSON object for a collected window (no leading comma), owner and task names
// escaped as tick_stats.h escapes its strings. Empty owners still render.
std::string format_window_json(const WindowSnapshot& w);

// The crash-report section body (without the "-- Tasks --" header) into a fixed buffer: running
// tasks first, then the last faults; "(task snapshot unavailable)" when a copy could not be made.
// No allocation, no lock; returns the number of bytes written (excluding the NUL).
std::size_t format_crash(const CrashSnapshot& s, char* buf, std::size_t cap);

}  // namespace kiln::tasks_stats
