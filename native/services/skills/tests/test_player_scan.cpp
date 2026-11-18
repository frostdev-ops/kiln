// SPDX-License-Identifier: MIT
// player_scan.h against a fake server world: the entities kiln.skills' shadow table maps every step are the ones a walk
// of the whole entity map every step would give (the code before 0.2.0-dev.29.16), except a PlayerState entity with no
// player record for a few steps after it appears; and the walks it takes. tests/run.sh.
#include <cstdio>
#include <cstdlib>
#include <map>
#include <random>
#include <set>
#include <vector>

#include "player_scan.h"

using kiln::skills::shadow::PlayerScan;

namespace {
int g_fail = 0;
void check(bool ok, const char* what) {
    if (!ok) {
        std::printf("FAIL %s\n", what);
        ++g_fail;
    }
}

struct FakeWorld {
    std::set<KilnEntity> entities;              // every entity (the count)
    std::set<KilnEntity> with_state;            // the ones with PlayerState
    std::map<uint32_t, KilnEntity> slots;       // slot -> the record's entity (0: none)
    bool records_ok = true;
    KilnResult find_rc = KILN_OK;               // a forced answer (KILN_OK: the real one)
    uint64_t finds = 0;
    KilnEntity next = 100;

    // the world
    KilnResult find(KilnEntity* out, uint32_t cap, uint32_t* n) {
        ++finds;
        if (find_rc != KILN_OK) return find_rc;
        uint32_t i = 0;
        for (KilnEntity e : with_state) {
            if (i < cap) out[i] = e;
            ++i;
        }
        *n = i;
        return i > cap ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
    }
    uint32_t entity_count() { return static_cast<uint32_t>(entities.size()); }
    bool records(std::vector<KilnEntity>* out) {
        if (!records_ok) return false;
        for (auto& [slot, e] : slots)
            if (e) out->push_back(e);
        return true;
    }
    bool has_player_state(KilnEntity e) { return with_state.count(e) != 0; }

    // what happens in it
    KilnEntity spawn(bool state) {
        const KilnEntity e = next++;
        entities.insert(e);
        if (state) with_state.insert(e);
        return e;
    }
    KilnEntity join(uint32_t slot) {
        const KilnEntity e = spawn(true);
        slots[slot] = e;
        return e;
    }
    void destroy(KilnEntity e) {
        entities.erase(e);
        with_state.erase(e);  // a record keeps its stale id until the slot is reused
    }

    // the walk every step: what the code before did
    std::vector<KilnEntity> reference() const {
        std::vector<KilnEntity> v(with_state.begin(), with_state.end());
        if (v.size() > PlayerScan::kMax) v.resize(PlayerScan::kMax);
        return v;
    }
};

bool subset(const std::vector<KilnEntity>& a, const std::vector<KilnEntity>& b) {
    for (KilnEntity e : a)
        if (std::find(b.begin(), b.end(), e) == b.end()) return false;
    return true;
}

void joined_players_are_mapped_at_once_without_a_walk() {
    FakeWorld w;
    PlayerScan s;
    const KilnEntity a = w.join(0);
    for (int i = 0; i < 3; ++i) w.spawn(false);
    check(s.step(w) == w.reference(), "the first step walks and finds the player");
    check(w.finds == 1, "one walk");
    const KilnEntity b = w.join(1);  // the entity count moved, but the last walk was one step ago
    check(s.step(w) == w.reference(), "a player who joins is mapped the next step, as a walk would");
    check(w.finds == 1, "from the records: no walk");
    w.destroy(a);
    w.slots[0] = 0;
    check(s.step(w) == std::vector<KilnEntity>{b}, "a player who left drops out the next step");
    w.destroy(b);  // the record still names the destroyed entity
    check(s.step(w).empty(), "a stale record id without PlayerState is not mapped");
    check(w.finds == 1, "still one walk");
    std::printf("PASS a joined player is mapped from kiln.players' records at the step a walk would map them\n");
}

void a_template_entity_waits_for_the_next_walk() {
    FakeWorld w;
    PlayerScan s;
    w.join(0);
    s.step(w);
    const KilnEntity t = w.spawn(true);  // a 1_Player template: PlayerState, no record
    uint32_t seen_at = 0;
    for (uint32_t i = 1; i <= PlayerScan::kPeriod && !seen_at; ++i) {
        const auto& got = s.step(w);
        check(subset(got, w.reference()), "never more than a walk gives");
        if (std::find(got.begin(), got.end(), t) != got.end()) seen_at = i;
    }
    check(seen_at == PlayerScan::kMinGap, "mapped by the walk kMinGap steps after the last (the entity count moved)");
    check(w.finds == 2, "two walks");
    w.with_state.erase(t);  // PlayerState taken away: dropped at once, no walk
    check(s.step(w) == w.reference(), "a cached entity that lost PlayerState drops out at once");
    std::printf("PASS a PlayerState entity with no record is mapped %u steps after the last walk (the entity count moved)\n",
                seen_at);
}

void a_late_component_waits_for_the_period() {
    FakeWorld w;
    PlayerScan s;
    w.join(0);
    const KilnEntity e = w.spawn(false);
    s.step(w);
    w.with_state.insert(e);  // PlayerState added to an existing entity: the count does not move
    uint32_t seen_at = 0;
    for (uint32_t i = 1; i <= 2 * PlayerScan::kPeriod && !seen_at; ++i) {
        const auto& got = s.step(w);
        if (std::find(got.begin(), got.end(), e) != got.end()) seen_at = i;
    }
    check(seen_at == PlayerScan::kPeriod, "a walk every kPeriod steps catches what the count does not show");
    std::printf("PASS PlayerState added to an existing entity is mapped by the periodic walk (%u steps)\n", seen_at);
}

void without_records_every_step_walks() {
    FakeWorld w;
    PlayerScan s;
    w.records_ok = false;  // kiln.players missing, or reading the game log only
    w.join(0);
    for (int i = 0; i < 20; ++i) {
        if (i == 5) w.spawn(true);
        if (i == 9) w.join(1);
        check(s.step(w) == w.reference(), "the same as a walk every step");
    }
    check(w.finds == 20, "a walk every step, as before");
    check(s.record_steps() == 0, "no record steps");
    std::printf("PASS without the players' records every step walks, as before\n");
}

void a_walk_that_could_not_run_is_tried_next_step() {
    FakeWorld w;
    PlayerScan s;
    w.join(0);
    w.find_rc = KILN_ERR_UNAVAILABLE;
    check(s.step(w) == w.reference(), "the records still map the player");
    w.find_rc = KILN_OK;
    const KilnEntity t = w.spawn(true);
    const auto& got = s.step(w);
    check(std::find(got.begin(), got.end(), t) != got.end(), "the next step walks again");
    check(w.finds == 2, "two walks");
    std::printf("PASS a walk kiln.world could not run is tried again the next step\n");
}

void at_most_64_and_a_reset() {
    FakeWorld w;
    PlayerScan s;
    for (int i = 0; i < 70; ++i) w.spawn(true);
    w.join(0);
    check(s.step(w).size() == PlayerScan::kMax, "at most 64 entities, as find's buffer");
    s.reset();
    check(s.walks() == 0 && s.steps() == 0, "a reset starts the counts again");
    s.step(w);
    check(s.walks() == 1, "the first step after a reset walks");
    std::printf("PASS at most 64 entities a step; after a world stops the next step walks\n");
}

// A minute of server steps (60 Hz): how many walks, against the 3600 of a walk every step.
void walks_in_a_minute() {
    for (int churn : {0, 1}) {
        FakeWorld w;
        PlayerScan s;
        for (uint32_t i = 0; i < 4; ++i) w.join(i);
        for (int i = 0; i < 2000; ++i) w.spawn(false);
        for (int i = 0; i < 3600; ++i) {
            if (churn) w.destroy(*w.entities.begin()), w.spawn(false), w.spawn(false);  // the count moves every step
            check(s.step(w) == w.reference(), "players with records: the same as a walk every step");
        }
        const uint64_t bound = churn ? 3600 / PlayerScan::kMinGap + 1 : 3600 / PlayerScan::kPeriod + 1;
        check(s.walks() <= bound, "walks bounded");
        std::printf("PASS %s: %llu walk(s) of the entity map in 3600 steps (a walk every step before: 3600)\n",
                    churn ? "entities created or destroyed every step" : "no entity created or destroyed",
                    static_cast<unsigned long long>(s.walks()));
    }
}

// Random worlds: never more than a walk gives; a player with a record is never missing; what is missing is a recent
// PlayerState entity with no record.
void random_worlds() {
    std::mt19937 rng(1234);
    for (int run = 0; run < 200; ++run) {
        FakeWorld w;
        PlayerScan s;
        std::map<KilnEntity, uint32_t> appeared;  // entity -> step it got PlayerState
        uint32_t step = 0;
        for (; step < 600; ++step) {
            switch (rng() % 10) {
                case 0: {
                    const uint32_t slot = rng() % 16;
                    if (!w.slots.count(slot) || !w.slots[slot] || !w.with_state.count(w.slots[slot])) appeared[w.join(slot)] = step;
                    break;
                }
                case 1:
                    if (!w.with_state.empty()) w.destroy(*std::next(w.with_state.begin(), rng() % w.with_state.size()));
                    break;
                case 2: appeared[w.spawn(true)] = step; break;
                case 3: w.spawn(false); break;
                case 4:
                    if (rng() % 8 == 0) {
                        const KilnEntity e = w.spawn(false);
                        w.with_state.insert(e);
                        appeared[e] = step;
                    }
                    break;
                default: break;
            }
            w.records_ok = rng() % 50 != 0;
            const auto got = s.step(w);
            const auto ref = w.reference();
            check(subset(got, ref), "random: never more than a walk gives");
            std::set<KilnEntity> recorded;
            if (w.records_ok)
                for (auto& [slot, e] : w.slots) recorded.insert(e);
            for (KilnEntity e : ref) {
                if (std::find(got.begin(), got.end(), e) != got.end()) continue;
                check(!recorded.count(e), "random: a player with a record is never missing");
                check(step - appeared[e] <= PlayerScan::kPeriod, "random: missing at most kPeriod steps");
            }
        }
    }
    std::printf("PASS 200 random worlds of 600 steps: never more than a walk, never without a recorded player\n");
}
}  // namespace

int main() {
    joined_players_are_mapped_at_once_without_a_walk();
    a_template_entity_waits_for_the_next_walk();
    a_late_component_waits_for_the_period();
    without_records_every_step_walks();
    a_walk_that_could_not_run_is_tried_next_step();
    at_most_64_and_a_reset();
    walks_in_a_minute();
    random_worlds();
    if (g_fail) {
        std::printf("%d check(s) failed\n", g_fail);
        return 1;
    }
    std::printf("PASS kiln.skills player scan\n");
    return 0;
}
