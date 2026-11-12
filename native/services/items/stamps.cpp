// SPDX-License-Identifier: MIT
// kiln.items v4: the item entities' stamps (stamps.h).
#include "stamps.h"

namespace kitems {

uint32_t StampTable::next_locked() {
    if (exhausted_) return 0;
    const uint32_t s = next_;
    if (next_ == 0xFFFFFFFFu) exhausted_ = true;  // 2^32 - 1 item entities seen: no stamp is handed out twice
    else ++next_;
    ++issued_;
    return s;
}

uint32_t StampTable::stamp(uint32_t entity, uint32_t item, uint64_t step) {
    if (!stampable(entity)) return 0;
    std::lock_guard lock(mu_);
    auto it = map_.find(entity);
    if (it != map_.end()) {
        Entry& e = it->second;
        if (e.gone) return 0;
        if (item == 0 || e.item == 0 || e.item == item) {
            if (e.item == 0) e.item = item;
            return e.stamp;
        }
        // The id holds another item than the one it was stamped for: not the same item entity.
        ++mismatches_;
        const uint32_t s = next_locked();
        if (!s) return 0;
        e = Entry{s, item, step, false};
        return s;
    }
    const uint32_t s = next_locked();
    if (!s) return 0;
    map_.emplace(entity, Entry{s, item, step, false});
    return s;
}

uint32_t StampTable::peek(uint32_t entity) const {
    std::lock_guard lock(mu_);
    auto it = map_.find(entity);
    return it == map_.end() || it->second.gone ? 0 : it->second.stamp;
}

uint32_t StampTable::destroyed(uint32_t entity, uint32_t item, uint64_t step) {
    if (!stampable(entity)) return 0;
    std::lock_guard lock(mu_);
    auto it = map_.find(entity);
    if (it == map_.end()) {
        const uint32_t s = next_locked();
        if (!s) return 0;
        map_.emplace(entity, Entry{s, item, step, true});
        return s;
    }
    Entry& e = it->second;
    if (e.gone) return e.stamp;  // reported twice (a take that names it again): the same stamp
    if (item != 0 && e.item != 0 && e.item != item) {
        ++mismatches_;
        const uint32_t s = next_locked();
        e = Entry{s, item, step, true};
        return s;
    }
    e.gone = true;
    return e.stamp;
}

bool StampTable::gone(uint32_t entity) const {
    std::lock_guard lock(mu_);
    auto it = map_.find(entity);
    return it != map_.end() && it->second.gone;
}

void StampTable::reset() {
    std::lock_guard lock(mu_);
    map_.clear();
    counter_ = 0;  // the next world's counter starts low again: that is not a second reset
    ++resets_;
}

bool StampTable::observe_counter(uint32_t next_id) {
    std::lock_guard lock(mu_);
    const bool down = counter_ != 0 && next_id < counter_;
    counter_ = next_id;
    if (down) {
        map_.clear();
        ++resets_;
    }
    return down;
}

size_t StampTable::size() const {
    std::lock_guard lock(mu_);
    return map_.size();
}
uint64_t StampTable::issued() const {
    std::lock_guard lock(mu_);
    return issued_;
}
uint64_t StampTable::resets() const {
    std::lock_guard lock(mu_);
    return resets_;
}
uint64_t StampTable::mismatches() const {
    std::lock_guard lock(mu_);
    return mismatches_;
}
uint32_t StampTable::counter() const {
    std::lock_guard lock(mu_);
    return counter_;
}
bool StampTable::exhausted() const {
    std::lock_guard lock(mu_);
    return exhausted_;
}
void StampTable::set_next_for_tests(uint32_t next) {
    std::lock_guard lock(mu_);
    next_ = next;
    exhausted_ = false;
}

}  // namespace kitems
