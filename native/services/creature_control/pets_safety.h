// SPDX-License-Identifier: MIT
// kiln.pets' native safety model (19d §4.4-4.6, §5.3): the guard rows, the death and hit policies, hostile pairs and
// two-phase withdrawal. No engine access: what it needs of the game comes in the queries (kiln.combat reads the hit's
// health, factions and classes; kiln.decisions gives the death visit's health), and the one call out is the broker's
// quarantine, a function pointer that touches atomics only. Workers read rows through atomics; the tick thread writes.
#pragma once

#include <atomic>
#include <cstdint>
#include <mutex>

#include "services/kiln_pets.h"

namespace kcc {

class PetSafety {
public:
    using QuarantineFn = KilnResult (*)(const KilnControlBodyKey& key, uint32_t cause);

    explicit PetSafety(QuarantineFn quarantine) : quarantine_(quarantine) {}

    // tick thread
    KilnResult bind(const KilnPetSafetyBinding& b);
    KilnResult activate(const KilnControlBodyKey& key);
    KilnResult collect(KilnPetSafetyState* out, uint32_t capacity, uint32_t* written, uint32_t* rescan) const;
    KilnResult retire(const KilnControlBodyKey& key, bool body_exists);
    KilnResult set_owner_entity(const KilnControlBodyKey& key, KilnEntity owner_entity);
    KilnResult set_hostile(KilnEntity a, KilnEntity b, bool on);
    void withdraw();
    void set_guard_installed(bool on) { guard_.store(on, std::memory_order_release); }
    bool guard_installed() const { return guard_.load(std::memory_order_acquire); }
    void set_step(uint64_t step) { step_.store(step, std::memory_order_relaxed); }
    // a server world ended: rows of the old epoch go (their bodies went with it), withdrawal ends
    void set_epoch(uint64_t epoch);
    uint32_t rows() const { return count_.load(std::memory_order_acquire); }

    // any thread
    KilnResult inspect(const KilnControlBodyKey& key, KilnPetSafetyState* out) const;
    uint32_t death_policy(uint64_t epoch, KilnEntity entity, int32_t health);
    uint32_t hit_policy(const KilnPetHitQuery& q);
    uint32_t involved(uint64_t epoch, KilnEntity entity) const { return find(epoch, entity) >= 0 ? 1u : 0u; }

    struct Counters {
        uint64_t latched, denied, passed, death_skips;
    };
    Counters counters() const {
        return {latched_.load(), denied_.load(), passed_.load(), death_skips_.load()};
    }

private:
    struct Row {
        std::atomic<uint32_t> entity{0};   // published last on bind, cleared first on retire
        std::atomic<uint64_t> epoch{0};
        std::atomic<uint64_t> generation{0};
        std::atomic<uint32_t> flags{0};
        std::atomic<uint64_t> dirty{0};
        std::atomic<uint64_t> step{0};
        std::atomic<uint32_t> cause{0};
        std::atomic<uint32_t> owner_entity{0};
        std::atomic<uint64_t> denied{0};
        // immutable while the row is published
        KilnPetId128 pet{};
        KilnPetCharacterKey owner{};
        uint32_t hostile = 0;
        uint32_t pvp = 0;
    };

    int find(uint64_t epoch, KilnEntity e) const;
    int find_key(const KilnControlBodyKey& key) const;
    void latch(Row& r, uint32_t cause);
    bool same_owner(const Row& a, const Row& b) const;
    bool hostile_pair(KilnEntity a, KilnEntity b) const;
    uint32_t deny(Row* a, Row* b);
    void fill(const Row& r, KilnPetSafetyState* out) const;

    QuarantineFn quarantine_;
    Row rows_[KILN_PETS_MAX_ROWS];
    std::atomic<uint32_t> high_{0};    // rows_[0..high_) were ever used
    std::atomic<uint32_t> count_{0};
    std::atomic<uint64_t> pairs_[KILN_PETS_MAX_HOSTILE_PAIRS]{};
    std::atomic<uint64_t> epoch_{0};
    std::atomic<uint64_t> dirty_seq_{0};
    std::atomic<uint64_t> step_{0};
    std::atomic<bool> withdrawn_{false};
    std::atomic<bool> guard_{false};
    std::atomic<uint64_t> latched_{0}, denied_{0}, passed_{0}, death_skips_{0};
    mutable std::mutex tick_mu_;  // tick-side writers only; readers never lock
};

}  // namespace kcc
