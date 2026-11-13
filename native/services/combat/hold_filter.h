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

    // count 0: every hit is held (no filter). True when the filter is another one than before.
    bool set(const uint32_t* ids, uint32_t count) {
        std::vector<uint32_t> next(ids, ids + count);
        std::sort(next.begin(), next.end());
        next.erase(std::unique(next.begin(), next.end()), next.end());
        next.erase(std::remove(next.begin(), next.end(), 0u), next.end());
        const bool all = count == 0;
        if (all == all_ && next == ids_) return false;
        ids_ = std::move(next);
        all_ = all;
        return true;
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
