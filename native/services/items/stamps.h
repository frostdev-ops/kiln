// SPDX-License-Identifier: MIT
// kiln.items v4: the stamp of every item entity (docs/kiln/services/items.md "Item instances"). A stamp is a 32-bit
// sequence number, never 0, new for every item entity this process sees; (entity id, stamp) names one item entity,
// the id alone does not.
//
// Why a table keyed by the id is enough (the evidence is in docs/kiln/services/items.md "Item instances"): in one
// server world the engine hands out every non-player entity id from one counter that only grows (createEntity with
// id 0, and the job command queues' queued creates, which createPide uses, both `lock xadd` the same storage counter),
// and an item entity
// is never made any other way. So within one world an id never names a second entity, and its entry can only ever
// describe the item entity it was made for. The ids start again when a world is set up (the counter is reset to 1023):
// the table is forgotten whenever a server world closes or starts, and when the counter is seen to go down
// (observe_counter, every Pre). Ids outside the counter's range (players 1..1023, and the ids with bit 31 set that the
// engine composes for its own sub-ranges) are never stamped: their entities get stamp 0 and no ref.
//
// Plain data and one mutex: called from the tick thread (reads) and from the worker thread inside the kiln_items
// program (the commits of give, take and move). Nothing here touches game memory.
#pragma once
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <unordered_map>

namespace kitems {

class StampTable {
public:
    // The first and last ids the engine's counter hands out (players live below it; bit 31 is another id space).
    static constexpr uint32_t kFirstId = 1024;
    static constexpr uint32_t kLastId = 0x7FFFFFFFu;
    static bool stampable(uint32_t entity) { return entity >= kFirstId && entity <= kLastId; }

    // The stamp of `entity`, which holds `item` now: its entry's, or a new one for an id without an entry. 0 for an id
    // outside the counter's range, for a destroyed entity (gone), and once the sequence is used up. An entry whose
    // item differs from `item` (item != 0) is not this item entity: it gets a new stamp (counted as a mismatch). `step`
    // is when it was seen first (pruning keeps young entries).
    uint32_t stamp(uint32_t entity, uint32_t item, uint64_t step);

    // The entry's stamp without making one; 0 = none (or gone).
    uint32_t peek(uint32_t entity) const;

    // The game destroys `entity` (a take's commit): its stamp (made now if it had none) is returned for the result
    // that reports it, and the entry stays as a tombstone: the id is never stamped again in this world.
    uint32_t destroyed(uint32_t entity, uint32_t item, uint64_t step);

    bool gone(uint32_t entity) const;

    // Forgets every entry (a world closed or started, or the counter went down). Stamps already handed out stay
    // unique: the sequence goes on.
    void reset();

    // The engine's entity id counter as read in Pre. True when it went down since the last call: the table was reset.
    bool observe_counter(uint32_t next_id);

    // Drops entries of entities that no longer exist and were seen first at least `min_age` steps before `now`
    // (`exists` is asked once per such entry). Returns how many went.
    template <typename Exists>
    size_t prune(uint64_t now, uint64_t min_age, Exists&& exists);

    size_t size() const;
    uint64_t issued() const;
    uint64_t resets() const;
    uint64_t mismatches() const;
    uint32_t counter() const;
    bool exhausted() const;

    // Tests: the next sequence number to hand out.
    void set_next_for_tests(uint32_t next);

private:
    struct Entry {
        uint32_t stamp;
        uint32_t item;
        uint64_t first_step;
        bool gone;
    };
    uint32_t next_locked();

    mutable std::mutex mu_;
    std::unordered_map<uint32_t, Entry> map_;
    uint32_t next_ = 1;
    bool exhausted_ = false;
    uint64_t issued_ = 0, resets_ = 0, mismatches_ = 0;
    uint32_t counter_ = 0;
};

template <typename Exists>
size_t StampTable::prune(uint64_t now, uint64_t min_age, Exists&& exists) {
    std::unordered_map<uint32_t, Entry> keep;
    {
        std::lock_guard lock(mu_);
        keep = map_;
    }
    size_t dropped = 0;
    for (auto it = keep.begin(); it != keep.end();) {
        const bool young = now < it->second.first_step + min_age;
        if (!young && !exists(it->first)) {
            ++dropped;
            it = keep.erase(it);
        } else {
            ++it;
        }
    }
    if (!dropped) return 0;
    std::lock_guard lock(mu_);
    // Entries made or changed while `exists` ran are kept as they are now; only the dead ones go.
    size_t n = 0;
    for (auto it = map_.begin(); it != map_.end();) {
        if (!keep.count(it->first) && now >= it->second.first_step + min_age) {
            it = map_.erase(it);
            ++n;
        } else {
            ++it;
        }
    }
    return n;
}

}  // namespace kitems
