// SPDX-License-Identifier: MIT
// kiln.combat's entity pre-test of held hits (since 0.2.0-dev.27, kiln_combat.h hold_filter): the listener's handlers
// watch some entities (managed event filters), so only a hit that names one of them waits a step for them. Header-only,
// so the host test (tests/hold_test.cpp) checks the same code.
#pragma once
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <vector>

namespace kc {

// The entity fields of a HitCandidate (combat_internal.h HitOffsets; combat.cpp checks they match): target, root target,
// source, root source.
constexpr int64_t kHoldOffsets[4] = {0x94, 0xA0, 0xA4, 0x90};

class HoldFilter {
public:
    static constexpr uint32_t kMax = 4096;

    // count 0: every hit is held (no filter)
    void set(const uint32_t* ids, uint32_t count) {
        ids_.assign(ids, ids + count);
        std::sort(ids_.begin(), ids_.end());
        ids_.erase(std::unique(ids_.begin(), ids_.end()), ids_.end());
        ids_.erase(std::remove(ids_.begin(), ids_.end(), 0u), ids_.end());
        all_ = count == 0;
    }

    bool all() const { return all_; }
    size_t size() const { return ids_.size(); }

    // A HitCandidate record (HitOffsets): held when the filter is off or names its target, root target, source or root source.
    bool wants(const uint8_t* rec) const {
        if (all_) return true;
        for (const int64_t off : kHoldOffsets) {
            uint32_t id = 0;
            std::memcpy(&id, rec + off, 4);
            if (id && std::binary_search(ids_.begin(), ids_.end(), id)) return true;
        }
        return false;
    }

private:
    std::vector<uint32_t> ids_;
    bool all_ = true;
};

}  // namespace kc
