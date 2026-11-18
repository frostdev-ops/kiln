// SPDX-License-Identifier: MIT
// kiln.skills: which server-world entities the shadow table maps every step (shadow::pre_server). The set it wants is
// every entity with PlayerState: the joined players, and also a player-template entity (`1_Player`, a lab's or a mod's)
// that has no player record. kiln.world's find gets that set by walking the whole entity map; on a player's world
// kiln-skills' Pre took about 0.9 ms of every server step (3.1 s a minute), most of it that walk (perf capture of
// 2026-11-17). So the walk is kept, but rare:
//   - the players' records (kiln.players raw_slot, read now on the tick thread) are added every step, so a player who
//     joins is mapped at the same step as before;
//   - the last walk's entities are kept, and each is checked again every step (a destroyed one drops out);
//   - the walk runs again when the world's entity count moved, at most every kMinGap steps, and every kPeriod steps
//     whatever happens (an entity created and another destroyed in one step, a PlayerState added later).
// Every entity used is checked to have PlayerState now, so the set is never more than the walk would give. What it can
// lack, for at most kMinGap steps after the entity count moved (kPeriod at worst), is a PlayerState entity with no
// player record created since the last walk. Without the records (kiln.players missing, or reading the game log only)
// the walk runs every step, as before. Pure: host-tested by tests/test_player_scan.cpp (tests/run.sh).
#pragma once
#include <algorithm>
#include <cstdint>
#include <vector>

#include "kiln_api.h"

namespace kiln::skills::shadow {

class PlayerScan {
public:
    static constexpr uint32_t kMax = 64;       // entities a step maps (find's buffer, as before)
    static constexpr uint32_t kMinGap = 8;     // steps between two walks while the entity count keeps moving
    static constexpr uint32_t kPeriod = 128;   // steps after which a walk runs anyway

    // Ops (the world, or a test's fake):
    //   KilnResult find(KilnEntity* out, uint32_t cap, uint32_t* n): kiln.world find(PlayerState)
    //   uint32_t entity_count(): the server world's entity count (0 when unknown)
    //   bool records(std::vector<KilnEntity>* out): the player records' entities now (0s skipped by the caller);
    //       false when the records cannot be read (then every step walks)
    //   bool has_player_state(KilnEntity e)
    // Returns the entities to map this step: sorted, unique, at most kMax.
    template <class Ops>
    const std::vector<KilnEntity>& step(Ops& ops) {
        rec_.clear();
        const bool records = ops.records(&rec_);
        const uint32_t count = ops.entity_count();
        if (since_ < kPeriod) ++since_;
        const bool walk = !records || !walked_ || since_ >= kPeriod || (count != count_at_walk_ && since_ >= kMinGap);
        out_.clear();
        if (walk) {
            KilnEntity found[kMax];
            uint32_t n = 0;
            const KilnResult rc = ops.find(found, kMax, &n);
            if (rc != KILN_OK) n = std::min<uint32_t>(n, kMax);
            cache_.assign(found, found + std::min<uint32_t>(n, kMax));
            count_at_walk_ = count;
            since_ = 0;
            walked_ = rc == KILN_OK || rc == KILN_ERR_BUFFER_TOO_SMALL;  // a walk that could not run is tried next step
            ++walks_;
            out_ = cache_;  // found with PlayerState just now
        } else {
            for (KilnEntity e : cache_)
                if (ops.has_player_state(e)) out_.push_back(e);
        }
        if (records)
            for (KilnEntity e : rec_)
                if (e && std::find(out_.begin(), out_.end(), e) == out_.end() && ops.has_player_state(e)) out_.push_back(e);
        std::sort(out_.begin(), out_.end());
        out_.erase(std::unique(out_.begin(), out_.end()), out_.end());
        if (out_.size() > kMax) out_.resize(kMax);
        ++steps_;
        record_steps_ += records;
        return out_;
    }

    // A world stopped: the next step walks, and the counts start again.
    void reset() {
        cache_.clear();
        walked_ = false;
        since_ = 0;
        walks_ = steps_ = record_steps_ = 0;
    }

    uint64_t walks() const { return walks_; }
    uint64_t steps() const { return steps_; }
    uint64_t record_steps() const { return record_steps_; }  // steps that had the players' records

private:
    std::vector<KilnEntity> cache_, rec_, out_;
    uint32_t count_at_walk_ = 0, since_ = 0;
    bool walked_ = false;
    uint64_t walks_ = 0, steps_ = 0, record_steps_ = 0;
};

}  // namespace kiln::skills::shadow
