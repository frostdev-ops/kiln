// SPDX-License-Identifier: MIT
// kiln.tasks diagnostics store: see tasks_stats.h. All shared fields are std::atomic; compound
// updates hold only the one slot's/entry's writer flag and publish through its seqlock sequence, so
// a reader (the once-a-minute tick writer, a crash report) copies lock-free with a bounded retry
// and falls back instead of spinning. This file never calls into the pool, the model or the log.
#include "tasks_stats.h"

#include <algorithm>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <thread>

namespace kiln::tasks_stats {
namespace {

using Name = std::array<std::atomic<unsigned char>, kNameBytes + 1>;

void write_name(Name& dst, std::atomic<uint32_t>& len, std::string_view s) {
    const std::size_t n = std::min(s.size(), kNameBytes);
    for (std::size_t i = 0; i < n; ++i) dst[i].store(static_cast<unsigned char>(s[i]), std::memory_order_relaxed);
    dst[n].store(0, std::memory_order_relaxed);
    len.store(static_cast<uint32_t>(n), std::memory_order_relaxed);
}

void read_name(const Name& src, const std::atomic<uint32_t>& len, char* out) {
    const std::size_t n = std::min<std::size_t>(len.load(std::memory_order_relaxed), kNameBytes);
    for (std::size_t i = 0; i < n; ++i) out[i] = static_cast<char>(src[i].load(std::memory_order_relaxed));
    out[n] = '\0';
}

// Serialises the writers of one slot/entry. Held only for a few atomic updates; readers never take
// it, and a reader that finds the sequence odd retries a bounded number of times and gives up, so a
// writer wedged here (a crash mid-update) can stall neither tick-stats nor a crash report.
struct Writer {
    std::atomic_flag& flag;
    std::atomic<uint64_t>& seq;
    explicit Writer(std::atomic_flag& f, std::atomic<uint64_t>& s) : flag(f), seq(s) {
        while (flag.test_and_set(std::memory_order_acquire)) std::this_thread::yield();
        seq.fetch_add(1, std::memory_order_acq_rel);  // odd: an update is in flight
    }
    ~Writer() {
        seq.fetch_add(1, std::memory_order_release);  // even: stable
        flag.clear(std::memory_order_release);
    }
    Writer(const Writer&) = delete;
    Writer& operator=(const Writer&) = delete;
};

struct Claim {
    std::atomic_flag& flag;
    explicit Claim(std::atomic_flag& f) : flag(f) {
        while (flag.test_and_set(std::memory_order_acquire)) std::this_thread::yield();
    }
    ~Claim() { flag.clear(std::memory_order_release); }
    Claim(const Claim&) = delete;
    Claim& operator=(const Claim&) = delete;
};

}  // namespace

struct Store::Slot {
    std::atomic<uint64_t> seq{0};
    std::atomic_flag writer = ATOMIC_FLAG_INIT;
    std::atomic<bool> active{false}, closed{false};
    std::atomic<uint64_t> owner{0}, instance{0};
    Name owner_name{};
    std::atomic<uint32_t> owner_name_len{0};
    std::atomic<uint64_t> queued_cur{0}, queued_max_w{0}, running_cur{0}, running_max_w{0};
    std::atomic<uint64_t> completed_w{0}, faulted_w{0}, cancelled_w{0};
    std::atomic<uint64_t> cpu_ns_w{0}, wall_ns_w{0}, longest_ns_w{0};
    Name longest_name{};
    std::atomic<uint32_t> longest_name_len{0};
    std::atomic<uint64_t> queued_too_long_w{0}, running_too_long_w{0}, queue_full_w{0}, unload_blocked_w{0};
    std::atomic<uint64_t> completed_t{0}, faulted_t{0}, cancelled_t{0};
};

struct Store::Entry {
    std::atomic<uint64_t> seq{0};
    std::atomic_flag writer = ATOMIC_FLAG_INIT;
    std::atomic<bool> active{false};
    std::atomic<uint32_t> state{0};
    std::atomic<uint64_t> owner{0}, instance{0}, handle{0}, thread_id{0};
    std::atomic<uint64_t> task_started_ns{0}, segment_started_ns{0};
    std::atomic<int> owner_slot{-1};
    Name owner_name{};
    std::atomic<uint32_t> owner_name_len{0};
    Name task_name{};
    std::atomic<uint32_t> task_name_len{0};
    std::atomic<uint32_t> fault_code{0};
};

struct Store::FaultEntry {
    std::atomic<uint64_t> seq{0};
    std::atomic_flag writer = ATOMIC_FLAG_INIT;
    std::atomic<uint64_t> sequence{0}, owner{0}, handle{0}, at_ns{0};
    std::atomic<uint32_t> state{0}, code{0};
    Name owner_name{};
    std::atomic<uint32_t> owner_name_len{0};
    Name task_name{};
    std::atomic<uint32_t> task_name_len{0};
};

Store::Store() {
    for (auto*& s : slots_) s = new Slot;
    for (auto*& e : entries_) e = new Entry;
    for (auto*& f : faults_) f = new FaultEntry;
}

Store::~Store() {
    for (auto*& s : slots_) delete s;
    for (auto*& e : entries_) delete e;
    for (auto*& f : faults_) delete f;
}

void Store::begin(uint64_t now_ns) { last_collect_ns_.store(now_ns, std::memory_order_relaxed); }

void Store::set_pool_counts(uint32_t workers, uint32_t long_running) {
    pool_workers_.store(workers, std::memory_order_relaxed);
    pool_long_running_.store(long_running, std::memory_order_relaxed);
}

AdmitResult Store::admit(uint64_t owner, uint64_t instance, std::string_view owner_name, std::string_view task_name,
                         uint64_t handle, uint64_t now_ns) {
    AdmitResult r;
    {
        Claim claim(claim_);
        for (std::size_t i = 0; i < kMaxOwners; ++i) {
            Slot& s = slot_at(static_cast<int>(i));
            if (s.active.load(std::memory_order_relaxed) && s.owner.load(std::memory_order_relaxed) == owner &&
                s.instance.load(std::memory_order_relaxed) == instance) {
                r.owner_slot = static_cast<int>(i);
                break;
            }
        }
        if (r.owner_slot < 0) {
            for (std::size_t i = 0; i < kMaxOwners; ++i) {
                Slot& s = slot_at(static_cast<int>(i));
                if (s.active.load(std::memory_order_relaxed)) continue;
                Writer w(s.writer, s.seq);
                s.owner.store(owner, std::memory_order_relaxed);
                s.instance.store(instance, std::memory_order_relaxed);
                write_name(s.owner_name, s.owner_name_len, owner_name);
                s.closed.store(false, std::memory_order_relaxed);
                s.queued_cur.store(0, std::memory_order_relaxed);
                s.queued_max_w.store(0, std::memory_order_relaxed);
                s.running_cur.store(0, std::memory_order_relaxed);
                s.running_max_w.store(0, std::memory_order_relaxed);
                s.completed_w.store(0, std::memory_order_relaxed);
                s.faulted_w.store(0, std::memory_order_relaxed);
                s.cancelled_w.store(0, std::memory_order_relaxed);
                s.cpu_ns_w.store(0, std::memory_order_relaxed);
                s.wall_ns_w.store(0, std::memory_order_relaxed);
                s.longest_ns_w.store(0, std::memory_order_relaxed);
                s.longest_name_len.store(0, std::memory_order_relaxed);
                s.queued_too_long_w.store(0, std::memory_order_relaxed);
                s.running_too_long_w.store(0, std::memory_order_relaxed);
                s.queue_full_w.store(0, std::memory_order_relaxed);
                s.unload_blocked_w.store(0, std::memory_order_relaxed);
                s.completed_t.store(0, std::memory_order_relaxed);
                s.faulted_t.store(0, std::memory_order_relaxed);
                s.cancelled_t.store(0, std::memory_order_relaxed);
                s.active.store(true, std::memory_order_relaxed);
                r.owner_slot = static_cast<int>(i);
                break;
            }
            if (r.owner_slot < 0) truncated_instances_.fetch_add(1, std::memory_order_relaxed);
        }
        for (std::size_t i = 0; i < kMaxRunning; ++i) {
            Entry& e = entry_at(static_cast<int>(i));
            if (e.active.load(std::memory_order_relaxed)) continue;
            Writer w(e.writer, e.seq);
            e.owner.store(owner, std::memory_order_relaxed);
            e.instance.store(instance, std::memory_order_relaxed);
            e.handle.store(handle, std::memory_order_relaxed);
            e.thread_id.store(0, std::memory_order_relaxed);
            e.task_started_ns.store(now_ns, std::memory_order_relaxed);
            e.segment_started_ns.store(now_ns, std::memory_order_relaxed);
            e.owner_slot.store(r.owner_slot, std::memory_order_relaxed);
            e.state.store(static_cast<uint32_t>(EntryState::Queued), std::memory_order_relaxed);
            e.fault_code.store(0, std::memory_order_relaxed);
            write_name(e.owner_name, e.owner_name_len, owner_name);
            write_name(e.task_name, e.task_name_len, task_name);
            e.active.store(true, std::memory_order_relaxed);
            r.entry = static_cast<int>(i);
            break;
        }
    }
    if (r.owner_slot >= 0) {
        Slot& s = slot_at(r.owner_slot);
        Writer w(s.writer, s.seq);
        const uint64_t q = s.queued_cur.load(std::memory_order_relaxed) + 1;
        s.queued_cur.store(q, std::memory_order_relaxed);
        if (q > s.queued_max_w.load(std::memory_order_relaxed)) s.queued_max_w.store(q, std::memory_order_relaxed);
    }
    return r;
}

void Store::close_instance(int owner_slot) {
    if (owner_slot < 0) return;
    Slot& s = slot_at(owner_slot);
    Writer w(s.writer, s.seq);
    s.closed.store(true, std::memory_order_relaxed);
}

void Store::close_owner(uint64_t owner) {
    for (std::size_t i = 0; i < kMaxOwners; ++i) {
        Slot& s = slot_at(static_cast<int>(i));
        if (s.active.load(std::memory_order_relaxed) && s.owner.load(std::memory_order_relaxed) == owner)
            close_instance(static_cast<int>(i));
    }
}

void Store::segment_started(int owner_slot, int entry, uint64_t thread_id, uint64_t now_ns) {
    bool was_queued = entry < 0;  // no entry to ask (table was full): the admission counted it queued
    if (entry >= 0) {
        Entry& e = entry_at(entry);
        Writer w(e.writer, e.seq);
        was_queued = e.state.load(std::memory_order_relaxed) == static_cast<uint32_t>(EntryState::Queued);
        e.state.store(static_cast<uint32_t>(EntryState::Running), std::memory_order_relaxed);
        e.thread_id.store(thread_id, std::memory_order_relaxed);
        e.segment_started_ns.store(now_ns, std::memory_order_relaxed);
    }
    if (owner_slot < 0) return;
    Slot& s = slot_at(owner_slot);
    Writer w(s.writer, s.seq);
    if (was_queued) {
        const uint64_t q = s.queued_cur.load(std::memory_order_relaxed);
        if (q) s.queued_cur.store(q - 1, std::memory_order_relaxed);
    }
    const uint64_t run = s.running_cur.load(std::memory_order_relaxed) + 1;
    s.running_cur.store(run, std::memory_order_relaxed);
    if (run > s.running_max_w.load(std::memory_order_relaxed)) s.running_max_w.store(run, std::memory_order_relaxed);
}

void Store::continuation_queued(int owner_slot, int entry) {
    if (entry >= 0) {
        Entry& e = entry_at(entry);
        Writer w(e.writer, e.seq);
        if (!e.active.load(std::memory_order_relaxed) ||
            e.state.load(std::memory_order_relaxed) != static_cast<uint32_t>(EntryState::Suspended))
            return;
        e.state.store(static_cast<uint32_t>(EntryState::Queued), std::memory_order_relaxed);
    }
    if (owner_slot < 0) return;
    Slot& s = slot_at(owner_slot);
    Writer w(s.writer, s.seq);
    const uint64_t q = s.queued_cur.load(std::memory_order_relaxed) + 1;
    s.queued_cur.store(q, std::memory_order_relaxed);
    if (q > s.queued_max_w.load(std::memory_order_relaxed)) s.queued_max_w.store(q, std::memory_order_relaxed);
}

void Store::segment_ended(int owner_slot, int entry, uint64_t cpu_ns, uint64_t wall_ns, bool suspended) {
    if (entry >= 0) {
        Entry& e = entry_at(entry);
        Writer w(e.writer, e.seq);
        e.state.store(static_cast<uint32_t>(suspended ? EntryState::Suspended : EntryState::DonePending),
                      std::memory_order_relaxed);
        e.thread_id.store(0, std::memory_order_relaxed);
    }
    if (owner_slot < 0) return;
    Slot& s = slot_at(owner_slot);
    Writer w(s.writer, s.seq);
    const uint64_t run = s.running_cur.load(std::memory_order_relaxed);
    if (run) s.running_cur.store(run - 1, std::memory_order_relaxed);
    s.cpu_ns_w.fetch_add(cpu_ns, std::memory_order_relaxed);
    s.wall_ns_w.fetch_add(wall_ns, std::memory_order_relaxed);
    if (wall_ns > s.longest_ns_w.load(std::memory_order_relaxed)) {
        s.longest_ns_w.store(wall_ns, std::memory_order_relaxed);
        if (entry >= 0) {
            const Entry& e = entry_at(entry);
            // The task name is immutable since admission; copied byte for byte.
            const std::size_t n = std::min<std::size_t>(e.task_name_len.load(std::memory_order_relaxed), kNameBytes);
            for (std::size_t i = 0; i < n; ++i)
                s.longest_name[i].store(e.task_name[i].load(std::memory_order_relaxed), std::memory_order_relaxed);
            s.longest_name[n].store(0, std::memory_order_relaxed);
            s.longest_name_len.store(static_cast<uint32_t>(n), std::memory_order_relaxed);
        }
    }
}

void Store::set_on_tick(int entry, uint64_t thread_id, uint64_t now_ns) {
    if (entry < 0) return;
    Entry& e = entry_at(entry);
    Writer w(e.writer, e.seq);
    if (!e.active.load(std::memory_order_relaxed)) return;
    e.state.store(static_cast<uint32_t>(EntryState::OnTick), std::memory_order_relaxed);
    e.thread_id.store(thread_id, std::memory_order_relaxed);
    e.segment_started_ns.store(now_ns, std::memory_order_relaxed);
}

void Store::end_on_tick(int entry) {
    if (entry < 0) return;
    Entry& e = entry_at(entry);
    Writer w(e.writer, e.seq);
    if (!e.active.load(std::memory_order_relaxed)) return;
    e.state.store(static_cast<uint32_t>(EntryState::Suspended), std::memory_order_relaxed);
    e.thread_id.store(0, std::memory_order_relaxed);
}

int Store::entry_of_handle(uint64_t handle) const {
    for (std::size_t i = 0; i < kMaxRunning; ++i) {
        const Entry& e = entry_at(static_cast<int>(i));
        if (e.active.load(std::memory_order_acquire) && e.handle.load(std::memory_order_relaxed) == handle)
            return static_cast<int>(i);
    }
    return -1;
}

void Store::note_fault_code(int entry, uint32_t code) {
    if (entry < 0) return;
    entry_at(entry).fault_code.store(code, std::memory_order_relaxed);
}

void Store::record_fault(const Entry& e, uint32_t final_state, uint32_t code, uint64_t now_ns) {
    const uint64_t sequence = fault_sequence_.fetch_add(1, std::memory_order_relaxed) + 1;
    FaultEntry& f = *faults_[static_cast<std::size_t>((sequence - 1) % kMaxFaults)];
    Writer w(f.writer, f.seq);
    f.sequence.store(sequence, std::memory_order_relaxed);
    f.owner.store(e.owner.load(std::memory_order_relaxed), std::memory_order_relaxed);
    f.handle.store(e.handle.load(std::memory_order_relaxed), std::memory_order_relaxed);
    f.at_ns.store(now_ns, std::memory_order_relaxed);
    f.state.store(final_state, std::memory_order_relaxed);
    f.code.store(code ? code : e.fault_code.load(std::memory_order_relaxed), std::memory_order_relaxed);
    const std::size_t on = std::min<std::size_t>(e.owner_name_len.load(std::memory_order_relaxed), kNameBytes);
    for (std::size_t i = 0; i < on; ++i)
        f.owner_name[i].store(e.owner_name[i].load(std::memory_order_relaxed), std::memory_order_relaxed);
    f.owner_name[on].store(0, std::memory_order_relaxed);
    f.owner_name_len.store(static_cast<uint32_t>(on), std::memory_order_relaxed);
    const std::size_t tn = std::min<std::size_t>(e.task_name_len.load(std::memory_order_relaxed), kNameBytes);
    for (std::size_t i = 0; i < tn; ++i)
        f.task_name[i].store(e.task_name[i].load(std::memory_order_relaxed), std::memory_order_relaxed);
    f.task_name[tn].store(0, std::memory_order_relaxed);
    f.task_name_len.store(static_cast<uint32_t>(tn), std::memory_order_relaxed);
}

void Store::terminal(int owner_slot, int entry, uint32_t final_state, uint32_t code, uint64_t now_ns) {
    if (entry >= 0) {
        Entry& e = entry_at(entry);
        if (final_state == kStateFaulted) record_fault(e, final_state, code, now_ns);
        Writer w(e.writer, e.seq);
        // A task cancelled while queued still holds its queued count; a running one released it at
        // its segment's end (its state left Queued only then).
        const bool was_queued = e.state.load(std::memory_order_relaxed) == static_cast<uint32_t>(EntryState::Queued);
        if (owner_slot >= 0 && was_queued) {
            Slot& s = slot_at(owner_slot);
            Writer ws(s.writer, s.seq);
            const uint64_t q = s.queued_cur.load(std::memory_order_relaxed);
            if (q) s.queued_cur.store(q - 1, std::memory_order_relaxed);
        }
        e.active.store(false, std::memory_order_relaxed);
    } else if (owner_slot >= 0) {
        // No entry (the running table was full at admission): the queued count still comes back.
        Slot& s = slot_at(owner_slot);
        Writer ws(s.writer, s.seq);
        const uint64_t q = s.queued_cur.load(std::memory_order_relaxed);
        if (q) s.queued_cur.store(q - 1, std::memory_order_relaxed);
    }
    if (owner_slot < 0) return;
    Slot& s = slot_at(owner_slot);
    Writer w(s.writer, s.seq);
    if (final_state == kStateCompleted) {
        s.completed_w.fetch_add(1, std::memory_order_relaxed);
        s.completed_t.fetch_add(1, std::memory_order_relaxed);
    } else if (final_state == kStateFaulted) {
        s.faulted_w.fetch_add(1, std::memory_order_relaxed);
        s.faulted_t.fetch_add(1, std::memory_order_relaxed);
    } else if (final_state == kStateCancelled) {
        s.cancelled_w.fetch_add(1, std::memory_order_relaxed);
        s.cancelled_t.fetch_add(1, std::memory_order_relaxed);
    }
}

void Store::note_too_long(int owner_slot, bool running) {
    if (owner_slot < 0) return;
    Slot& s = slot_at(owner_slot);
    Writer w(s.writer, s.seq);
    (running ? s.running_too_long_w : s.queued_too_long_w).fetch_add(1, std::memory_order_relaxed);
}

void Store::note_queue_full(uint64_t owner) {
    int best = -1;
    uint64_t best_instance = 0;
    for (std::size_t i = 0; i < kMaxOwners; ++i) {
        const Slot& s = slot_at(static_cast<int>(i));
        if (!s.active.load(std::memory_order_relaxed) || s.owner.load(std::memory_order_relaxed) != owner) continue;
        const uint64_t inst = s.instance.load(std::memory_order_relaxed);
        if (best < 0 || inst >= best_instance) {
            best = static_cast<int>(i);
            best_instance = inst;
        }
    }
    if (best < 0) return;
    Slot& s = slot_at(best);
    Writer w(s.writer, s.seq);
    s.queue_full_w.fetch_add(1, std::memory_order_relaxed);
}

void Store::note_unload_blocked(uint64_t owner) {
    int best = -1;
    uint64_t best_instance = 0;
    for (std::size_t i = 0; i < kMaxOwners; ++i) {
        const Slot& s = slot_at(static_cast<int>(i));
        if (!s.active.load(std::memory_order_relaxed) || s.owner.load(std::memory_order_relaxed) != owner) continue;
        const uint64_t inst = s.instance.load(std::memory_order_relaxed);
        if (best < 0 || inst >= best_instance) {
            best = static_cast<int>(i);
            best_instance = inst;
        }
    }
    if (best < 0) return;
    Slot& s = slot_at(best);
    Writer w(s.writer, s.seq);
    s.unload_blocked_w.fetch_add(1, std::memory_order_relaxed);
}

void Store::note_queue_depth(uint64_t depth) {
    queue_current_.store(depth, std::memory_order_relaxed);
    uint64_t cur = queue_max_window_.load(std::memory_order_relaxed);
    while (depth > cur && !queue_max_window_.compare_exchange_weak(cur, depth, std::memory_order_relaxed)) {
    }
}

void Store::record_apply(uint64_t applied, uint64_t cancelled, uint64_t apply_ns, uint64_t owners_served,
                         bool owner_budget_stop, bool step_budget_stop, uint64_t deferred_now) {
    apply_applied_.fetch_add(applied, std::memory_order_relaxed);
    apply_cancelled_.fetch_add(cancelled, std::memory_order_relaxed);
    apply_ns_.fetch_add(apply_ns, std::memory_order_relaxed);
    apply_owners_served_.fetch_add(owners_served, std::memory_order_relaxed);
    if (owner_budget_stop) apply_owner_stops_.fetch_add(1, std::memory_order_relaxed);
    if (step_budget_stop) apply_step_stops_.fetch_add(1, std::memory_order_relaxed);
    apply_deferred_.fetch_add(deferred_now, std::memory_order_relaxed);
}

void Store::note_hop_refused() { hops_refused_.fetch_add(1, std::memory_order_relaxed); }

// ---- readers ----

bool Store::copy_slot(const Slot& s, OwnerSnapshot* out) const {
    for (int attempt = 0; attempt < kCopyRetries; ++attempt) {
        const uint64_t before = s.seq.load(std::memory_order_acquire);
        if (before & 1) continue;
        OwnerSnapshot o;
        o.present = s.active.load(std::memory_order_relaxed);
        if (!o.present) {
            *out = o;
            return true;
        }
        o.owner = s.owner.load(std::memory_order_relaxed);
        o.instance = s.instance.load(std::memory_order_relaxed);
        read_name(s.owner_name, s.owner_name_len, o.owner_name);
        o.queued_current = s.queued_cur.load(std::memory_order_relaxed);
        o.queued_max = s.queued_max_w.load(std::memory_order_relaxed);
        o.running_current = s.running_cur.load(std::memory_order_relaxed);
        o.running_max = s.running_max_w.load(std::memory_order_relaxed);
        o.completed = s.completed_w.load(std::memory_order_relaxed);
        o.faulted = s.faulted_w.load(std::memory_order_relaxed);
        o.cancelled = s.cancelled_w.load(std::memory_order_relaxed);
        o.cpu_ns = s.cpu_ns_w.load(std::memory_order_relaxed);
        o.wall_ns = s.wall_ns_w.load(std::memory_order_relaxed);
        o.longest_ns = s.longest_ns_w.load(std::memory_order_relaxed);
        read_name(s.longest_name, s.longest_name_len, o.longest_name);
        o.queued_too_long = s.queued_too_long_w.load(std::memory_order_relaxed);
        o.running_too_long = s.running_too_long_w.load(std::memory_order_relaxed);
        o.queue_full = s.queue_full_w.load(std::memory_order_relaxed);
        o.unload_blocked = s.unload_blocked_w.load(std::memory_order_relaxed);
        if (s.seq.load(std::memory_order_acquire) == before) {
            *out = o;
            return true;
        }
    }
    return false;
}

bool Store::reset_window(Slot& s) {
    // A slot whose copy failed is not reset: its next window keeps the numbers.
    Writer w(s.writer, s.seq);
    if (!s.active.load(std::memory_order_relaxed)) return true;
    s.queued_max_w.store(s.queued_cur.load(std::memory_order_relaxed), std::memory_order_relaxed);
    s.running_max_w.store(s.running_cur.load(std::memory_order_relaxed), std::memory_order_relaxed);
    s.completed_w.store(0, std::memory_order_relaxed);
    s.faulted_w.store(0, std::memory_order_relaxed);
    s.cancelled_w.store(0, std::memory_order_relaxed);
    s.cpu_ns_w.store(0, std::memory_order_relaxed);
    s.wall_ns_w.store(0, std::memory_order_relaxed);
    s.longest_ns_w.store(0, std::memory_order_relaxed);
    s.longest_name_len.store(0, std::memory_order_relaxed);
    s.longest_name[0].store(0, std::memory_order_relaxed);
    s.queued_too_long_w.store(0, std::memory_order_relaxed);
    s.running_too_long_w.store(0, std::memory_order_relaxed);
    s.queue_full_w.store(0, std::memory_order_relaxed);
    s.unload_blocked_w.store(0, std::memory_order_relaxed);
    if (s.closed.load(std::memory_order_relaxed) && !s.queued_cur.load(std::memory_order_relaxed) &&
        !s.running_cur.load(std::memory_order_relaxed))
        s.active.store(false, std::memory_order_relaxed);
    return true;
}

WindowSnapshot Store::collect_window(uint64_t now_ns, uint64_t backlog, uint64_t queue_current) {
    WindowSnapshot w;
    w.id = window_id_.fetch_add(1, std::memory_order_relaxed) + 1;
    const uint64_t last = last_collect_ns_.exchange(now_ns, std::memory_order_relaxed);
    w.span_ns = last && now_ns >= last ? now_ns - last : 0;
    w.age_ns = 0;  // the snapshot is taken at the window's end; its age when written is ~0
    w.pool.workers = pool_workers_.load(std::memory_order_relaxed);
    w.pool.long_running = pool_long_running_.load(std::memory_order_relaxed);
    w.pool.queue_current = queue_current;
    w.pool.queue_max = queue_max_window_.exchange(queue_current, std::memory_order_relaxed);
    if (w.pool.queue_max < queue_current) w.pool.queue_max = queue_current;
    w.owners_truncated = truncated_instances_.load(std::memory_order_relaxed) > 0;
    for (std::size_t i = 0; i < kMaxOwners; ++i) {
        Slot& s = slot_at(static_cast<int>(i));
        if (!s.active.load(std::memory_order_relaxed)) continue;
        OwnerSnapshot o;
        if (!copy_slot(s, &o)) {
            w.slot_unavailable = true;
            continue;
        }
        if (!o.present) continue;
        const bool activity = o.queued_current || o.running_current || o.completed || o.faulted || o.cancelled ||
                              o.cpu_ns || o.wall_ns || o.queued_too_long || o.running_too_long || o.queue_full ||
                              o.unload_blocked;
        if (activity && w.owner_count < kMaxOwners) w.owners[w.owner_count++] = o;
        reset_window(s);
    }
    w.apply.applied = apply_applied_.exchange(0, std::memory_order_relaxed);
    w.apply.deferred = apply_deferred_.exchange(0, std::memory_order_relaxed);
    w.apply.cancelled = apply_cancelled_.exchange(0, std::memory_order_relaxed);
    w.apply.apply_ns = apply_ns_.exchange(0, std::memory_order_relaxed);
    w.apply.owner_budget_stops = apply_owner_stops_.exchange(0, std::memory_order_relaxed);
    w.apply.step_budget_stops = apply_step_stops_.exchange(0, std::memory_order_relaxed);
    w.apply.owners_served = apply_owners_served_.exchange(0, std::memory_order_relaxed);
    w.apply.backlog = backlog;
    w.hops_refused = hops_refused_.exchange(0, std::memory_order_relaxed);
    return w;
}

CrashSnapshot Store::snapshot_crash(uint64_t now_ns) const {
    CrashSnapshot out;
    std::array<RunningSnapshot, kMaxRunning> all{};
    std::size_t n = 0;
    for (std::size_t i = 0; i < kMaxRunning; ++i) {
        const Entry& e = entry_at(static_cast<int>(i));
        if (!e.active.load(std::memory_order_acquire)) continue;
        bool ok = false;
        RunningSnapshot r;
        for (int attempt = 0; attempt < kCopyRetries && !ok; ++attempt) {
            const uint64_t before = e.seq.load(std::memory_order_acquire);
            if (before & 1) continue;
            r = RunningSnapshot{};
            r.present = e.active.load(std::memory_order_relaxed);
            r.owner = e.owner.load(std::memory_order_relaxed);
            r.instance = e.instance.load(std::memory_order_relaxed);
            r.handle = e.handle.load(std::memory_order_relaxed);
            r.thread_id = e.thread_id.load(std::memory_order_relaxed);
            r.started_ns = e.task_started_ns.load(std::memory_order_relaxed);
            const uint64_t segment = e.segment_started_ns.load(std::memory_order_relaxed);
            r.state = static_cast<EntryState>(e.state.load(std::memory_order_relaxed));
            read_name(e.owner_name, e.owner_name_len, r.owner_name);
            read_name(e.task_name, e.task_name_len, r.task_name);
            const uint64_t since =
                (r.state == EntryState::Running || r.state == EntryState::OnTick) ? segment : r.started_ns;
            r.elapsed_ns = now_ns >= since ? now_ns - since : 0;
            ok = e.seq.load(std::memory_order_acquire) == before;
        }
        if (!ok || !r.present) {
            out.unavailable = true;
            out.running_count = 0;
            return out;
        }
        if (r.state == EntryState::DonePending) continue;  // terminal, its done is next: not running
        all[n++] = r;
    }
    out.running_total = n;
    out.running_count = std::min<std::size_t>(n, kMaxCrashRunning);
    for (std::size_t i = 0; i < out.running_count; ++i) out.running[i] = all[i];
    std::array<FaultSnapshot, kMaxFaults> faults{};
    std::size_t fn = 0;
    for (std::size_t i = 0; i < kMaxFaults; ++i) {
        const FaultEntry& f = *faults_[i];
        FaultSnapshot fs;
        bool ok = false;
        for (int attempt = 0; attempt < kCopyRetries && !ok; ++attempt) {
            const uint64_t before = f.seq.load(std::memory_order_acquire);
            if (before & 1) continue;
            fs = FaultSnapshot{};
            fs.sequence = f.sequence.load(std::memory_order_relaxed);
            fs.present = fs.sequence != 0;
            fs.owner = f.owner.load(std::memory_order_relaxed);
            fs.handle = f.handle.load(std::memory_order_relaxed);
            fs.at_ns = f.at_ns.load(std::memory_order_relaxed);
            fs.state = f.state.load(std::memory_order_relaxed);
            fs.code = f.code.load(std::memory_order_relaxed);
            read_name(f.owner_name, f.owner_name_len, fs.owner_name);
            read_name(f.task_name, f.task_name_len, fs.task_name);
            ok = f.seq.load(std::memory_order_acquire) == before;
        }
        if (!ok) {
            out.unavailable = true;
            return out;
        }
        if (fs.present) faults[fn++] = fs;
    }
    std::sort(faults.begin(), faults.begin() + static_cast<std::ptrdiff_t>(fn),
              [](const FaultSnapshot& a, const FaultSnapshot& b) { return a.sequence < b.sequence; });
    out.fault_count = fn;
    for (std::size_t i = 0; i < fn; ++i) out.faults[i] = faults[i];
    return out;
}

Totals Store::totals(uint64_t) const {
    Totals t;
    t.workers = pool_workers_.load(std::memory_order_relaxed);
    t.long_running = pool_long_running_.load(std::memory_order_relaxed);
    for (std::size_t i = 0; i < kMaxOwners; ++i) {
        const Slot& s = slot_at(static_cast<int>(i));
        if (!s.active.load(std::memory_order_relaxed)) continue;
        bool ok = false;
        for (int attempt = 0; attempt < kCopyRetries && !ok; ++attempt) {
            const uint64_t before = s.seq.load(std::memory_order_acquire);
            if (before & 1) continue;
            ++t.owners;
            t.queued += s.queued_cur.load(std::memory_order_relaxed);
            t.running += s.running_cur.load(std::memory_order_relaxed);
            t.completed += s.completed_t.load(std::memory_order_relaxed);
            t.faulted += s.faulted_t.load(std::memory_order_relaxed);
            t.cancelled += s.cancelled_t.load(std::memory_order_relaxed);
            ok = s.seq.load(std::memory_order_acquire) == before;
        }
        if (!ok) {
            t.unavailable = true;
            return t;
        }
    }
    const CrashSnapshot c = snapshot_crash(0);
    if (c.unavailable) {
        t.unavailable = true;
        return t;
    }
    t.running_tasks = c.running_total;
    t.faults = fault_sequence_.load(std::memory_order_relaxed);
    return t;
}

uint32_t Store::owner_count() const {
    uint32_t n = 0;
    for (std::size_t i = 0; i < kMaxOwners; ++i) {
        OwnerSnapshot o;
        if (copy_slot(slot_at(static_cast<int>(i)), &o) && o.present) ++n;
    }
    return n;
}

bool Store::owner_row(uint32_t index, OwnerSnapshot* out) const {
    if (!out) return false;
    uint32_t seen = 0;
    for (std::size_t i = 0; i < kMaxOwners; ++i) {
        const Slot& s = slot_at(static_cast<int>(i));
        OwnerSnapshot o;
        if (!copy_slot(s, &o) || !o.present) continue;
        if (seen++ == index) {
            // The crash-facing row carries the instance's totals, not the tick window's counts
            // (copy_slot read the window's; the totals sit beside them in the same slot).
            o.completed = s.completed_t.load(std::memory_order_relaxed);
            o.faulted = s.faulted_t.load(std::memory_order_relaxed);
            o.cancelled = s.cancelled_t.load(std::memory_order_relaxed);
            *out = o;
            return true;
        }
    }
    return false;
}

bool Store::running_row(uint32_t index, uint64_t now_ns, RunningSnapshot* out) const {
    if (!out) return false;
    uint32_t seen = 0;
    for (std::size_t i = 0; i < kMaxRunning; ++i) {
        const Entry& e = entry_at(static_cast<int>(i));
        if (!e.active.load(std::memory_order_acquire)) continue;
        if (static_cast<EntryState>(e.state.load(std::memory_order_relaxed)) == EntryState::DonePending) continue;
        if (seen++ != index) continue;
        for (int attempt = 0; attempt < kCopyRetries; ++attempt) {
            const uint64_t before = e.seq.load(std::memory_order_acquire);
            if (before & 1) continue;
            RunningSnapshot r;
            r.present = e.active.load(std::memory_order_relaxed);
            r.owner = e.owner.load(std::memory_order_relaxed);
            r.instance = e.instance.load(std::memory_order_relaxed);
            r.handle = e.handle.load(std::memory_order_relaxed);
            r.thread_id = e.thread_id.load(std::memory_order_relaxed);
            r.started_ns = e.task_started_ns.load(std::memory_order_relaxed);
            const uint64_t segment = e.segment_started_ns.load(std::memory_order_relaxed);
            r.state = static_cast<EntryState>(e.state.load(std::memory_order_relaxed));
            read_name(e.owner_name, e.owner_name_len, r.owner_name);
            read_name(e.task_name, e.task_name_len, r.task_name);
            const uint64_t since = (r.state == EntryState::Running || r.state == EntryState::OnTick) ? segment : r.started_ns;
            r.elapsed_ns = now_ns >= since ? now_ns - since : 0;
            if (e.seq.load(std::memory_order_acquire) == before) {
                *out = r;
                return r.present;
            }
        }
        return false;
    }
    return false;
}

bool Store::fault_row(uint32_t index, FaultSnapshot* out) const {
    if (!out) return false;
    const CrashSnapshot c = snapshot_crash(0);
    if (c.unavailable || index >= c.fault_count) return false;
    *out = c.faults[index];
    return true;
}

void Store::force_seq_odd_for_test(int owner_slot) {
    if (owner_slot < 0) return;
    slot_at(owner_slot).seq.fetch_add(1, std::memory_order_acq_rel);
}

void Store::clear_seq_odd_for_test(int owner_slot) {
    if (owner_slot < 0) return;
    slot_at(owner_slot).seq.fetch_add(1, std::memory_order_acq_rel);
}

void Store::force_entry_seq_odd_for_test(int entry) {
    if (entry < 0) return;
    entry_at(entry).seq.fetch_add(1, std::memory_order_acq_rel);
}

void Store::clear_entry_seq_odd_for_test(int entry) {
    if (entry < 0) return;
    entry_at(entry).seq.fetch_add(1, std::memory_order_acq_rel);
}

// ---- formatting ----

namespace {

void appendf(std::string& out, const char* fmt, ...) {
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (static_cast<std::size_t>(n) < sizeof buf) {
        out.append(buf, static_cast<std::size_t>(n));
        return;
    }
    std::string big(static_cast<std::size_t>(n) + 1, '\0');
    va_start(ap, fmt);
    std::vsnprintf(big.data(), big.size(), fmt, ap);
    va_end(ap);
    big.resize(static_cast<std::size_t>(n));
    out += big;
}

double ms(uint64_t ns) { return static_cast<double>(ns) / 1e6; }

// JSON string escaping, the same rules as tick_stats.h's detail::escape (quotes and backslashes
// escaped, control characters as \uXXXX, ill-formed UTF-8 bytes as '?'). Task names are validated
// before admission, so this is defence in depth for owner names, which are registry text.
void escape(std::string& out, const char* s) {
    const auto* p = reinterpret_cast<const unsigned char*>(s);
    for (std::size_t i = 0; p[i];) {
        const unsigned char c = p[i];
        if (c == '"' || c == '\\') {
            out += '\\';
            out += static_cast<char>(c);
            ++i;
        } else if (c < 0x20) {
            appendf(out, "\\u%04x", static_cast<unsigned>(c));
            ++i;
        } else if (c < 0x80) {
            out += static_cast<char>(c);
            ++i;
        } else {
            const std::size_t len = c >= 0xF0 ? (c <= 0xF4 ? 4 : 0) : c >= 0xE0 ? 3 : c >= 0xC2 ? 2 : 0;
            bool ok = len != 0;
            for (std::size_t k = 1; ok && k < len; ++k) ok = p[i + k] && (p[i + k] & 0xC0) == 0x80;
            if (ok) {
                out.append(reinterpret_cast<const char*>(p + i), len);
                i += len;
            } else {
                out += '?';
                ++i;
            }
        }
    }
}

}  // namespace

std::string format_window_json(const WindowSnapshot& w) {
    std::string out;
    out.reserve(1024);
    appendf(out,
            "\"tasks\":{\"window\":{\"id\":%llu,\"span_ms\":%.3f,\"age_ms\":%.3f},"
            "\"pool\":{\"workers\":%u,\"long_running\":%u,\"queue_max\":%llu},\"owners\":[",
            static_cast<unsigned long long>(w.id), ms(w.span_ns), ms(w.age_ns), w.pool.workers, w.pool.long_running,
            static_cast<unsigned long long>(w.pool.queue_max));
    for (std::size_t i = 0; i < w.owner_count; ++i) {
        const OwnerSnapshot& o = w.owners[i];
        out += i ? ",{" : "{";
        out += "\"owner\":\"";
        escape(out, o.owner_name);
        appendf(out,
                "\",\"queued\":{\"current\":%llu,\"max\":%llu},\"running\":{\"current\":%llu,\"max\":%llu},"
                "\"completed\":%llu,\"faulted\":%llu,\"cancelled\":%llu,\"cpu_ms\":%.3f,\"wall_ms\":%.3f,"
                "\"longest_ms\":%.3f,\"longest_name\":\"",
                static_cast<unsigned long long>(o.queued_current), static_cast<unsigned long long>(o.queued_max),
                static_cast<unsigned long long>(o.running_current), static_cast<unsigned long long>(o.running_max),
                static_cast<unsigned long long>(o.completed), static_cast<unsigned long long>(o.faulted),
                static_cast<unsigned long long>(o.cancelled), ms(o.cpu_ns), ms(o.wall_ns), ms(o.longest_ns));
        escape(out, o.longest_name);
        appendf(out, "\",\"queued_too_long\":%llu,\"running_too_long\":%llu,\"queue_full\":%llu,\"unload_blocked\":%llu}",
                static_cast<unsigned long long>(o.queued_too_long), static_cast<unsigned long long>(o.running_too_long),
                static_cast<unsigned long long>(o.queue_full), static_cast<unsigned long long>(o.unload_blocked));
    }
    out += ']';
    if (w.owners_truncated) out += ",\"owners_truncated\":true";
    appendf(out,
            ",\"apply\":{\"applied\":%llu,\"deferred\":%llu,\"cancelled\":%llu,\"ms\":%.3f,"
            "\"owner_budget_stops\":%llu,\"step_budget_stops\":%llu,\"owners_served\":%llu,\"backlog\":%llu},"
            "\"hops\":{\"refused\":%llu}}",
            static_cast<unsigned long long>(w.apply.applied), static_cast<unsigned long long>(w.apply.deferred),
            static_cast<unsigned long long>(w.apply.cancelled), ms(w.apply.apply_ns),
            static_cast<unsigned long long>(w.apply.owner_budget_stops),
            static_cast<unsigned long long>(w.apply.step_budget_stops),
            static_cast<unsigned long long>(w.apply.owners_served), static_cast<unsigned long long>(w.apply.backlog),
            static_cast<unsigned long long>(w.hops_refused));
    return out;
}

namespace {

void append_crash(char*& p, char* end, const char* fmt, ...) {
    if (p >= end) return;
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(p, static_cast<std::size_t>(end - p), fmt, ap);
    va_end(ap);
    if (n < 0) return;
    p += std::min<std::size_t>(static_cast<std::size_t>(n), static_cast<std::size_t>(end - p));
}

// Crash text is not JSON: quotes and controls in a name are flattened so one task cannot forge a line.
void append_name(char*& p, char* end, const char* s) {
    for (const unsigned char* q = reinterpret_cast<const unsigned char*>(s); *q && p < end; ++q)
        *p++ = (*q < 0x20 || *q == '"' || *q == '\\') ? '?' : static_cast<char>(*q);
    if (p < end) *p = '\0';
}

const char* state_word(EntryState s) {
    switch (s) {
        case EntryState::Running: return "running";
        case EntryState::Suspended: return "suspended";
        case EntryState::OnTick: return "on-tick";
        case EntryState::Queued: return "queued";
        default: return "ending";
    }
}

const char* final_word(uint32_t s) {
    switch (s) {
        case kStateCompleted: return "completed";
        case kStateFaulted: return "faulted";
        case kStateCancelled: return "cancelled";
        default: return "ended";
    }
}

}  // namespace

std::size_t format_crash(const CrashSnapshot& s, char* buf, std::size_t cap) {
    if (!buf || !cap) return 0;
    buf[0] = '\0';
    char* p = buf;
    char* const end = buf + cap - 1;
    if (s.unavailable) {
        append_crash(p, end, "(task snapshot unavailable)\n");
        return static_cast<std::size_t>(p - buf);
    }
    if (!s.running_count && !s.fault_count) {
        append_crash(p, end, "   (no task running, no task fault recorded)\n");
        return static_cast<std::size_t>(p - buf);
    }
    for (std::size_t i = 0; i < s.running_count; ++i) {
        const RunningSnapshot& r = s.running[i];
        append_crash(p, end, "  running thread=%llu owner=\"",
                     static_cast<unsigned long long>(r.thread_id));
        append_name(p, end, r.owner_name);
        append_crash(p, end, "\" task=\"");
        append_name(p, end, r.task_name);
        append_crash(p, end, "\" handle=0x%llx started_ns=%llu elapsed_ms=%.3f state=%s\n",
                     static_cast<unsigned long long>(r.handle), static_cast<unsigned long long>(r.started_ns),
                     ms(r.elapsed_ns), state_word(r.state));
    }
    if (s.running_total > s.running_count)
        append_crash(p, end, "  ... and %llu more running (at most %zu shown)\n",
                     static_cast<unsigned long long>(s.running_total - s.running_count), kMaxCrashRunning);
    for (std::size_t i = 0; i < s.fault_count; ++i) {
        const FaultSnapshot& f = s.faults[i];
        append_crash(p, end, "  fault #%llu owner=\"", static_cast<unsigned long long>(f.sequence));
        append_name(p, end, f.owner_name);
        append_crash(p, end, "\" task=\"");
        append_name(p, end, f.task_name);
        append_crash(p, end, "\" handle=0x%llx state=%s code=0x%08x at_ns=%llu\n",
                     static_cast<unsigned long long>(f.handle), final_word(f.state), f.code,
                     static_cast<unsigned long long>(f.at_ns));
    }
    return static_cast<std::size_t>(p - buf);
}

}  // namespace kiln::tasks_stats
