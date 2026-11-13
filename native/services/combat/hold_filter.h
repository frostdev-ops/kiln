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

// The entity fields of a HitCandidate (combat_internal.h HitOffsets; combat.cpp checks they match).
constexpr int64_t kHitTarget = 0x94, kHitRootTarget = 0xA0, kHitSource = 0xA4, kHitRootSource = 0x90;

// The entities a hit names, as kiln.combat's events report them (fill_from_hit) and as the hold filter tests them: one
// definition for both, so a held hit is always one whose event a filtered handler gets (Kiln.Combat's
// PendingHitEvent.Involves tests these four). target: the entity whose health the hit changes (the root target when the
// record has one: a creature, not its weak spot); part: what the hit landed on (a weak spot or the creature itself);
// attacker: the root source (a player, a creature); source: the direct source (a swing's impact, a projectile, the
// attacker itself). A creature's own attack on a player names the creature as attacker: it is a hit about it too.
struct HitEntities {
    uint32_t target, part, attacker, source;
};

inline HitEntities hit_entities(const uint8_t* rec) {
    uint32_t t = 0, rt = 0, s = 0, rs = 0;
    std::memcpy(&t, rec + kHitTarget, 4);
    std::memcpy(&rt, rec + kHitRootTarget, 4);
    std::memcpy(&s, rec + kHitSource, 4);
    std::memcpy(&rs, rec + kHitRootSource, 4);
    return HitEntities{rt ? rt : t, t, rs, s};
}

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

    // Whether a watched entity is one of the hit's entities (hit_entities: what its event names).
    bool names(const HitEntities& e) const {
        for (const uint32_t id : {e.target, e.part, e.attacker, e.source})
            if (id && std::binary_search(ids_.begin(), ids_.end(), id)) return true;
        return false;
    }

    // A HitCandidate record (HitOffsets): held when the filter is off or names one of its entities.
    bool wants(const uint8_t* rec) const { return all_ || names(hit_entities(rec)); }

private:
    std::vector<uint32_t> ids_;
    bool all_ = true;
};

}  // namespace kc
