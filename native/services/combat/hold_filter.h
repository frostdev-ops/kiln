// SPDX-License-Identifier: MIT
// kiln.combat's entity pre-test of held hits (since 0.2.0-dev.27, kiln_combat.h hold_filter): the listener's handlers
// watch some entities (managed event filters), so only a hit that names one of them waits a step for them. Header-only,
// so the host test (tests/hold_test.cpp) checks the same code.
#pragma once
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <unordered_set>
#include <vector>

namespace kc {

// The entity fields of a HitCandidate (combat_internal.h HitOffsets; combat.cpp checks they match).
constexpr int64_t kHitTarget = 0x94, kHitRootTarget = 0xA0, kHitSource = 0xA4, kHitRootSource = 0x90, kHitImpact = 0xD4;

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

// The conclusion's merge group of a hit record: its root target (else its target) and its impact. The conclusion applies
// one hit per group, the one with the largest abs(healthChange) (docs/re/combat.md), and Kiln reports one event per group.
inline uint64_t hit_group(const uint8_t* rec) {
    uint32_t t = 0, rt = 0, imp = 0;
    std::memcpy(&t, rec + kHitTarget, 4);
    std::memcpy(&rt, rec + kHitRootTarget, 4);
    std::memcpy(&imp, rec + kHitImpact, 4);
    return (static_cast<uint64_t>(rt ? rt : t) << 32) | imp;
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

    // Which of one step's entity-hit records (the pending list's kind-0 records, in order) to hold: every record of a
    // merge group (hit_group) the filter names in any of its records. Held record by record, a swing on a watched weak
    // spot would be split: its body record (same impact, same root target) applies at once, and the held weak-spot
    // record is then a repeat of an impact that already hit the creature (the conclusion skips it, and so does Kiln's
    // report), so the handler hears nothing and the damage is the body's, not the group's strongest.
    std::vector<bool> hold(const std::vector<const uint8_t*>& recs) const {
        std::vector<bool> out(recs.size(), all_);
        if (all_) return out;
        std::unordered_set<uint64_t> named;
        for (const uint8_t* r : recs)
            if (names(hit_entities(r))) named.insert(hit_group(r));
        for (size_t i = 0; i < recs.size(); ++i) out[i] = named.count(hit_group(recs[i])) != 0;
        return out;
    }

private:
    std::vector<uint32_t> ids_;
    bool all_ = true;
};

}  // namespace kc
