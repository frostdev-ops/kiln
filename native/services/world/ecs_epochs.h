// SPDX-License-Identifier: MIT
// Each owner's current reload epoch for kiln.world's verified-copy reads (ecs_reads.h, Backend::open_epoch). An owner
// is at the newest epoch it has opened a ref with (0 before its first): a newer epoch in entity_open is its reload,
// and from then on its refs of every other epoch validate stale; an older one is a ref from before its reload and is
// refused. Query refs carry the owner's current epoch. Any thread; one short lock, no allocation beyond the map.
// Bounded: an owner that retires is dropped (kiln.hooks' owner-retiring observer); past kMaxOwners the oldest owner
// (lowest id: owners are never reused) is forgotten, which can only make its refs stale, never valid.
#pragma once
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <unordered_map>

#include "kiln_api.h"

namespace kw::ecs {

class OwnerEpochs {
public:
    static constexpr size_t kMaxOwners = 4096;

    uint64_t current(KilnOwner owner) const {
        std::lock_guard lock(mu_);
        const auto it = map_.find(owner);
        return it == map_.end() ? 0 : it->second;
    }
    // The owner opens a ref under `epoch`: recorded when newer than its current one. Returns the current epoch.
    uint64_t open(KilnOwner owner, uint64_t epoch) {
        std::lock_guard lock(mu_);
        const auto it = map_.find(owner);
        if (it != map_.end()) {
            if (epoch > it->second) it->second = epoch;
            return it->second;
        }
        if (epoch == 0 || owner == 0) return 0;  // 0 is every owner's epoch until it opens another
        if (map_.size() >= kMaxOwners) {
            auto oldest = map_.begin();
            for (auto i = map_.begin(); i != map_.end(); ++i)
                if (i->first < oldest->first) oldest = i;
            map_.erase(oldest);
        }
        map_.emplace(owner, epoch);
        return epoch;
    }
    void retire(KilnOwner owner) {
        std::lock_guard lock(mu_);
        map_.erase(owner);
    }
    size_t size() const {
        std::lock_guard lock(mu_);
        return map_.size();
    }

private:
    mutable std::mutex mu_;
    std::unordered_map<KilnOwner, uint64_t> map_;
};

} // namespace kw::ecs
