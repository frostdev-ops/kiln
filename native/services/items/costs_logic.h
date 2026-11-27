// SPDX-License-Identifier: MIT
// kiln.item_costs (kiln_item_costs.h) without game memory: the request checks, the wear arithmetic and the receipt table
// (tickets, request ids, settlement, acknowledgement, expiry, closed request ids). costs.cpp wraps the table in a mutex
// and publishes the service; gear.cpp runs the queued costs inside kiln_items_gear. Host test: tests/costs_test.cpp.
#pragma once
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <deque>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "services/kiln_item_costs.h"

namespace kitems::costs {

constexpr uint32_t kMaxStack = 100000;      // as kiln.items' give/take
constexpr uint32_t kMaxWear = 32767;        // as kiln.toolwear's cost
constexpr uint32_t kMaxHeld = 1024;         // queued + unacknowledged receipts, process-wide
constexpr size_t kMaxClosed = 65536;        // closed request ids remembered (oldest forgotten first)
constexpr uint64_t kExpireMs = 10 * 60 * 1000;  // a settled receipt nobody acknowledges is let go after this
constexpr uint64_t kNotRunSteps = 250;      // ~10 s at 24.5 Hz without a program update: NOT_RUN

inline bool is_stack(uint32_t kind) { return kind == KILN_COST_STACK_TAKE || kind == KILN_COST_STACK_REFUND; }
inline bool is_gear(uint32_t kind) { return kind == KILN_COST_GEAR_WEAR || kind == KILN_COST_GEAR_RESTORE; }

// A slot id kiln.items accepts: an inventory entity, index 0..7, nothing above bit 39.
inline bool slot_ok(uint64_t slot) {
    return static_cast<uint32_t>(slot) != 0 && ((slot >> 32) & 0xFFu) <= 7 && (slot >> 40) == 0;
}

// The shape of a cost (no world read). nullptr: fine; else why not.
inline const char* check_shape(const KilnSelectedCost& c) {
    if (c.size < sizeof(KilnSelectedCost)) return "the cost's size is smaller than KilnSelectedCost";
    if (c.kind < KILN_COST_STACK_TAKE || c.kind > KILN_COST_GEAR_RESTORE) return "no such cost kind";
    if (c.reserved != 0) return "reserved is not 0";
    if (c.holder == 0) return "no holder";
    if (c.item == 0) return "no item";
    if (c.world_epoch == 0) return "no world (world_epoch 0)";
    if (c.request_lo == 0 && c.request_hi == 0) return "no request id";
    const bool needs_slot = c.kind == KILN_COST_STACK_TAKE || c.kind == KILN_COST_GEAR_WEAR;
    if ((needs_slot || c.slot != 0) && !slot_ok(c.slot)) return "the slot is not an inventory slot id (index 0-7, nothing above bit 39)";
    switch (c.kind) {
        case KILN_COST_STACK_TAKE:
            if (c.amount < 1 || c.amount > kMaxStack) return "a stack take is 1 to 100000 items";
            if (c.expected_count < c.amount) return "the slot's expected count is smaller than the amount";
            if (c.item_entity || c.item_stamp) return "a stack has no item entity or stamp";
            if (c.allow_break) return "allow_break is for gear wear";
            return nullptr;
        case KILN_COST_GEAR_WEAR:
            if (c.amount > kMaxWear) return "a gear wear is 0 to 32767 units";
            if (c.expected_count != 1) return "a gear wear's expected count is 1";
            if (!c.item_entity || !c.item_stamp) return "a gear wear names the item entity and its stamp";
            if (c.allow_break > 1) return "allow_break is 0 or 1";
            return nullptr;
        case KILN_COST_STACK_REFUND:
            if (c.amount < 1 || c.amount > kMaxStack) return "a stack refund is 1 to 100000 items";
            if (c.expected_count || c.item_entity || c.item_stamp || c.allow_break) return "a stack refund has no expected count, item entity, stamp or allow_break";
            return nullptr;
        default:  // KILN_COST_GEAR_RESTORE
            if (c.amount < 1 || c.amount > kMaxWear) return "a gear restore is 1 to 32767 units";
            if (!c.item_entity || !c.item_stamp) return "a gear restore names the item entity and its stamp";
            if (c.expected_count || c.allow_break) return "a gear restore has no expected count or allow_break";
            return nullptr;
    }
}

// The session a player holder's cost carries: the player's connection handle and the step Kiln saw the join, as
// Kiln.Pets' owner sessions (a new connection or a new join is a new session).
inline uint64_t character_session(uint32_t handle, uint64_t joined_step) { return (static_cast<uint64_t>(handle) << 32) ^ joined_step; }

// ---- the wear arithmetic -------------------------------------------------------------------------------------------
struct WearPlan {
    uint32_t refusal = KILN_COST_REFUSAL_NONE;  // not NONE: refuse, change nothing
    bool waived = false;                        // the world has durability off: commit with 0 applied
    int32_t delta = 0;                          // what to hand WearDelta (negative: a wear; positive: a restore)
    int32_t expect = 0;                         // the change the game should make (wear: units off; restore: units back)
    bool crosses_zero = false;                  // a wear that reaches 0 (allow_break): the tool breaks
    const char* why = "";
};

// A wear of `amount` from `before` (of `max`) in a world whose enableDurability is `enabled`.
inline WearPlan plan_wear(int32_t before, int32_t max, uint32_t amount, bool allow_break, bool enabled) {
    WearPlan p;
    (void)max;
    if (!enabled) {
        p.waived = true;
        return p;
    }
    if (before <= 0) {
        p.refusal = KILN_COST_BROKEN;
        p.why = "the item is broken (durability 0)";
        return p;
    }
    if (amount == 0) return p;  // a use that costs nothing: committed, 0 applied
    const int64_t after = static_cast<int64_t>(before) - amount;
    if (after < 1 && !allow_break) {
        p.refusal = KILN_COST_WOULD_BREAK;
        p.why = "the wear would leave the item below 1 (allow_break is off)";
        return p;
    }
    p.delta = -static_cast<int32_t>(amount);
    p.crosses_zero = after <= 0;
    p.expect = after <= 0 ? before : static_cast<int32_t>(amount);  // the game clamps at 0
    return p;
}

// A restore of up to `amount` to `before` (of `max`).
inline WearPlan plan_restore(int32_t before, int32_t max, uint32_t amount) {
    WearPlan p;
    if (max <= 0) {
        p.refusal = KILN_COST_NOT_GEAR;
        p.why = "the item has no maximum durability";
        return p;
    }
    if (before >= max) {
        p.refusal = KILN_COST_FULL;
        p.why = "the item is at its maximum durability";
        return p;
    }
    const int64_t room = static_cast<int64_t>(max) - (before < 0 ? 0 : before);
    p.delta = static_cast<int32_t>(room < amount ? room : amount);
    p.expect = p.delta;
    return p;
}

// The disposition of a gear change the program read back: exact when the game did what the plan expected.
inline uint32_t gear_disposition(const WearPlan& p, int32_t applied) {
    if (p.waived) return KILN_COST_WEAR_WAIVED_BY_WORLD;
    return applied == p.expect ? KILN_COST_EXACT : KILN_COST_MODIFIED_BY_OTHER;
}

// ---- the receipt table ----------------------------------------------------------------------------------------------
struct RequestKey {
    uint64_t owner, lo, hi;
    bool operator==(const RequestKey& o) const { return owner == o.owner && lo == o.lo && hi == o.hi; }
};
struct RequestKeyHash {
    size_t operator()(const RequestKey& k) const {
        uint64_t h = k.owner * 0x9E3779B97F4A7C15ull ^ k.lo;
        h = (h ^ (h >> 29)) * 0xBF58476D1CE4E5B9ull ^ k.hi;
        return static_cast<size_t>(h ^ (h >> 32));
    }
};

struct Job {
    uint64_t ticket = 0;
    KilnOwner owner = 0;
    KilnSelectedCost cost{};
    uint64_t submitted_step = 0;
};

inline void set_reason(KilnCostResult* r, const char* why) { std::snprintf(r->reason, sizeof r->reason, "%s", why ? why : ""); }

// A receipt with the cost's own fields filled and nothing settled.
inline KilnCostResult blank_result(uint64_t ticket, const KilnSelectedCost& c) {
    KilnCostResult r{};
    r.size = sizeof r;
    r.state = KILN_COST_QUEUED;
    r.ticket = ticket;
    r.kind = c.kind;
    r.item = c.item;
    r.slot = c.slot;
    r.item_entity = c.item_entity;
    r.item_stamp = c.item_stamp;
    r.world_epoch = c.world_epoch;
    r.request_lo = c.request_lo;
    r.request_hi = c.request_hi;
    r.wear_requested = is_gear(c.kind) ? c.amount : 0;
    r.durability_before = r.durability_after = -1;
    return r;
}

inline KilnCostResult refused(const Job& j, uint32_t refusal, const char* why, uint64_t step = 0) {
    KilnCostResult r = blank_result(j.ticket, j.cost);
    r.state = KILN_COST_REFUSED;
    r.refusal = refusal;
    r.step = step;
    set_reason(&r, why);
    return r;
}

// Not thread-safe: costs.cpp holds its mutex around every call.
class Table {
public:
    // KILN_OK and *ticket, or a KilnResult / KILN_ITEM_COSTS_* code. `step`: the server step now.
    KilnResult submit(KilnOwner owner, const KilnSelectedCost& c, uint64_t step, uint64_t* ticket) {
        const RequestKey key{owner, c.request_lo, c.request_hi};
        if (auto it = by_request_.find(key); it != by_request_.end()) {
            const Entry& e = entries_.at(it->second);
            if (std::memcmp(&e.job.cost, &c, sizeof c) != 0) return KILN_ITEM_COSTS_REQUEST_CONFLICT;
            *ticket = e.job.ticket;
            return KILN_OK;
        }
        if (closed_.count(key)) return KILN_ITEM_COSTS_REQUEST_CLOSED;
        if (entries_.size() >= kMaxHeld) return KILN_ITEM_COSTS_BUSY;
        Entry e;
        e.job.ticket = next_ticket_++;
        e.job.owner = owner;
        e.job.cost = c;
        e.job.cost.size = sizeof(KilnSelectedCost);
        e.job.submitted_step = step;
        e.result = blank_result(e.job.ticket, e.job.cost);
        by_request_[key] = e.job.ticket;
        queue_.push_back(e.job.ticket);
        *ticket = e.job.ticket;
        entries_.emplace(e.job.ticket, e);
        return KILN_OK;
    }

    // The program takes up to `max` queued costs (oldest first); they are running until settled.
    std::vector<Job> take(size_t max) {
        std::vector<Job> out;
        while (out.size() < max && !queue_.empty()) {
            const uint64_t t = queue_.front();
            queue_.pop_front();
            auto it = entries_.find(t);
            if (it == entries_.end() || it->second.result.state != KILN_COST_QUEUED) continue;
            it->second.running = true;
            out.push_back(it->second.job);
        }
        return out;
    }
    bool queued() const { return !queue_.empty(); }

    // The program's receipt for a running cost. `now_ms`: process time.
    void settle(const KilnCostResult& r, uint64_t now_ms) {
        auto it = entries_.find(r.ticket);
        if (it == entries_.end() || it->second.result.state != KILN_COST_QUEUED) return;
        Entry& e = it->second;
        e.result = r;
        e.result.size = sizeof(KilnCostResult);
        if (e.result.state == KILN_COST_QUEUED) e.result.state = KILN_COST_UNCERTAIN;  // a program never leaves one queued
        e.running = false;
        e.settled_ms = now_ms;
        count(e.result.state);
    }

    KilnResult peek(KilnOwner owner, uint64_t ticket, KilnCostResult* out) const {
        auto it = entries_.find(ticket);
        if (it == entries_.end() || it->second.job.owner != owner) return KILN_ERR_NOT_FOUND;
        *out = it->second.result;
        return KILN_OK;
    }

    KilnResult acknowledge(KilnOwner owner, uint64_t ticket) {
        auto it = entries_.find(ticket);
        if (it == entries_.end() || it->second.job.owner != owner) return KILN_ERR_NOT_FOUND;
        if (it->second.result.state == KILN_COST_QUEUED) return KILN_ERR_INVALID_ARGUMENT;
        close(it);
        return KILN_OK;
    }

    // Tick thread: what did not run ends here. A world that closed (`world_closed`): every queued cost of an older
    // world, WORLD_ENDED. A program that did not run for kNotRunSteps since a cost was submitted (`last_update` is the
    // step of its last update): NOT_RUN. Switched off (`off`): every queued cost, NOT_RUN with `why`.
    void end_queued(uint64_t step, uint64_t last_update, uint64_t world_epoch_now, bool off, const char* off_why, uint64_t now_ms) {
        for (auto& [t, e] : entries_) {
            if (e.result.state != KILN_COST_QUEUED || e.running) continue;
            if (e.job.cost.world_epoch != world_epoch_now) {
                settle_as(e, refused(e.job, KILN_COST_WORLD_ENDED, "the server world closed before the cost ran"), now_ms);
            } else if (off) {
                settle_as(e, refused(e.job, KILN_COST_NOT_RUN, off_why), now_ms);
            } else if (step > e.job.submitted_step + kNotRunSteps && last_update < e.job.submitted_step + kNotRunSteps) {
                settle_as(e, refused(e.job, KILN_COST_NOT_RUN, "the server world did not run kiln_items_gear for 10 s"), now_ms);
            }
        }
    }

    // Settled receipts nobody acknowledged for kExpireMs are let go (their request ids stay closed).
    void expire(uint64_t now_ms) {
        for (auto it = entries_.begin(); it != entries_.end();) {
            if (it->second.result.state != KILN_COST_QUEUED && now_ms >= it->second.settled_ms + kExpireMs) it = close(it);
            else ++it;
        }
    }

    // An owner went away: its settled receipts are let go now; its queued ones run and settle first (never forgotten
    // before they settled), then go at the next expiry.
    void owner_gone(KilnOwner owner) {
        for (auto it = entries_.begin(); it != entries_.end();) {
            if (it->second.job.owner == owner && it->second.result.state != KILN_COST_QUEUED) it = close(it);
            else ++it;
        }
    }

    uint32_t held() const { return static_cast<uint32_t>(entries_.size()); }
    uint64_t committed() const { return committed_; }
    uint64_t refused_count() const { return refused_; }
    uint64_t uncertain() const { return uncertain_; }
    size_t closed_count() const { return closed_.size(); }

private:
    struct Entry {
        Job job;
        KilnCostResult result{};
        bool running = false;
        uint64_t settled_ms = 0;
    };
    using Map = std::unordered_map<uint64_t, Entry>;

    void count(uint32_t state) {
        if (state == KILN_COST_COMMITTED) ++committed_;
        else if (state == KILN_COST_REFUSED) ++refused_;
        else ++uncertain_;
    }
    void settle_as(Entry& e, const KilnCostResult& r, uint64_t now_ms) {
        e.result = r;
        e.settled_ms = now_ms;
        count(r.state);
    }
    Map::iterator close(Map::iterator it) {
        const RequestKey key{it->second.job.owner, it->second.job.cost.request_lo, it->second.job.cost.request_hi};
        by_request_.erase(key);
        if (closed_.insert(key).second) {
            closed_order_.push_back(key);
            while (closed_order_.size() > kMaxClosed) {
                closed_.erase(closed_order_.front());
                closed_order_.pop_front();
            }
        }
        return entries_.erase(it);
    }

    Map entries_;
    std::unordered_map<RequestKey, uint64_t, RequestKeyHash> by_request_;
    std::unordered_set<RequestKey, RequestKeyHash> closed_;
    std::deque<RequestKey> closed_order_;
    std::deque<uint64_t> queue_;
    uint64_t next_ticket_ = 1;
    uint64_t committed_ = 0, refused_ = 0, uncertain_ = 0;
};

}  // namespace kitems::costs
