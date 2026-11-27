// SPDX-License-Identifier: MIT
// kiln.pets' native safety model: see pets_safety.h. The target rule (19d §4.4 matrix, with the policy a definition
// declared instead of a fixed one):
//   pet root -> player              deny, unless the pet's PvP is by mod rule and the mod declared the owner and that
//                                   player hostile; a heal of the pet's own owner passes
//   pet root -> pet of the owner    a heal passes, harm is denied
//   pet root -> another owner's pet deny, unless both pets are by mod rule and their owners are a declared pair
//   pet root -> creature            harm passes only for an unowned enemy-AI creature of a faction the pet declared
//                                   hostile; heals of creatures are denied; anything unread is denied (conservative)
//   pet root -> terrain, building   deny (also a retired root's explosion: the broker's root_policy denies it first)
//   player -> pet                   deny, unless the pair rule above holds; the owner's heal passes
//   creature -> pet                 harm passes only from an enemy-AI creature of a faction the pet declared hostile
//   unknown root (0) -> pet         deny
//   allowed lethal harm -> pet      latched DownPending (and quarantined) and denied before the original
//   staging, down-pending, protection-lost rows: every hit of or on them is denied
#include "pets_safety.h"

#include "services/kiln_decisions.h"

#include <algorithm>
#include <vector>

namespace kcc {

namespace {
constexpr uint32_t kBlocked = KILN_PET_SAFETY_STAGING | KILN_PET_SAFETY_DOWN_PENDING | KILN_PET_SAFETY_RETIRING |
                              KILN_PET_SAFETY_RETIRED | KILN_PET_SAFETY_PROTECTION_LOST;
constexpr uint32_t kPlayersSide = (1u << 7) | (1u << 8) | (1u << 11);  // Player, AutomatedPlayer, PlayerAlly
constexpr uint32_t kNeverFair = kPlayersSide | (1u << 12);              // ... and FarmAnimal

bool is_player(KilnEntity e, uint8_t cls) { return (e >= 1 && e <= 1023) || (cls & KILN_PET_CLASS_PLAYER); }
bool harmful(const KilnPetHitQuery& q) { return q.health_change < 0 || q.reaction != 0 || q.pushback != 0 || q.aggro > 0.0f; }
bool heal_only(const KilnPetHitQuery& q) { return q.health_change > 0 && !harmful(q); }
uint64_t pair_key(KilnEntity a, KilnEntity b) {
    if (a > b) std::swap(a, b);
    return (static_cast<uint64_t>(a) << 32) | b;
}
}  // namespace

int PetSafety::find(uint64_t epoch, KilnEntity e) const {
    if (!e || !epoch || count_.load(std::memory_order_acquire) == 0) return -1;
    const uint32_t n = high_.load(std::memory_order_acquire);
    for (uint32_t i = 0; i < n; ++i)
        if (rows_[i].entity.load(std::memory_order_acquire) == e && rows_[i].epoch.load(std::memory_order_acquire) == epoch)
            return static_cast<int>(i);
    return -1;
}

int PetSafety::find_key(const KilnControlBodyKey& key) const {
    const int i = find(key.world_epoch, key.entity);
    if (i < 0 || rows_[i].generation.load(std::memory_order_acquire) != key.body_generation) return -1;
    return i;
}

KilnResult PetSafety::bind(const KilnPetSafetyBinding& b) {
    if (b.size < sizeof(KilnPetSafetyBinding) || b.reserved || b.body.reserved || !b.body.entity || !b.body.body_generation ||
        !b.body.world_epoch || (b.pet.lo == 0 && b.pet.hi == 0) || (b.owner.character.lo == 0 && b.owner.character.hi == 0) ||
        (b.hostile_factions & kNeverFair) || b.pvp > KILN_PET_PVP_BY_MOD_RULE || (b.flags & ~KILN_PET_BIND_STAGING) ||
        b.body.entity < 1024 || b.body.entity > 0x7fffffffu)
        return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(tick_mu_);
    const uint64_t epoch = epoch_.load(std::memory_order_acquire);
    if (epoch && b.body.world_epoch != epoch) return KILN_ERR_INVALID_ARGUMENT;
    if (find_key(b.body) >= 0) return KILN_OK;
    if (find(b.body.world_epoch, b.body.entity) >= 0) return KILN_ERR_INVALID_ARGUMENT;  // another generation still guarded
    if (withdrawn_.load(std::memory_order_acquire) || !guard_.load(std::memory_order_acquire)) return KILN_ERR_UNAVAILABLE;
    uint32_t slot = KILN_PETS_MAX_ROWS;
    const uint32_t n = high_.load(std::memory_order_acquire);
    for (uint32_t i = 0; i < n; ++i)
        if (rows_[i].entity.load(std::memory_order_acquire) == 0) {
            slot = i;
            break;
        }
    if (slot == KILN_PETS_MAX_ROWS) {
        if (n >= KILN_PETS_MAX_ROWS) return KILN_ERR_BUFFER_TOO_SMALL;
        slot = n;
    }
    Row& r = rows_[slot];
    r.pet = b.pet;
    r.owner = b.owner;
    r.hostile = b.hostile_factions;
    r.pvp = b.pvp;
    r.owner_entity.store(b.owner_entity, std::memory_order_relaxed);
    r.flags.store(KILN_PET_SAFETY_GUARDED | ((b.flags & KILN_PET_BIND_STAGING) ? KILN_PET_SAFETY_STAGING : 0), std::memory_order_relaxed);
    r.dirty.store(0, std::memory_order_relaxed);
    r.step.store(0, std::memory_order_relaxed);
    r.cause.store(0, std::memory_order_relaxed);
    r.denied.store(0, std::memory_order_relaxed);
    r.generation.store(b.body.body_generation, std::memory_order_relaxed);
    r.epoch.store(b.body.world_epoch, std::memory_order_release);
    if (!epoch) epoch_.store(b.body.world_epoch, std::memory_order_release);
    r.entity.store(b.body.entity, std::memory_order_release);   // published last: a worker sees a whole row or none
    if (slot == n) high_.store(n + 1, std::memory_order_release);
    count_.fetch_add(1, std::memory_order_acq_rel);
    return KILN_OK;
}

KilnResult PetSafety::activate(const KilnControlBodyKey& key) {
    std::lock_guard lock(tick_mu_);
    const int i = find_key(key);
    if (i < 0) return KILN_ERR_NOT_FOUND;
    rows_[i].flags.fetch_and(~static_cast<uint32_t>(KILN_PET_SAFETY_STAGING), std::memory_order_acq_rel);
    return KILN_OK;
}

void PetSafety::fill(const Row& r, KilnPetSafetyState* out) const {
    out->size = sizeof(KilnPetSafetyState);
    out->flags = r.flags.load(std::memory_order_acquire);
    out->body = KilnControlBodyKey{r.epoch.load(), r.generation.load(), r.entity.load(), 0};
    out->pet = r.pet;
    out->dirty_sequence = r.dirty.load(std::memory_order_acquire);
    out->observed_step = r.step.load(std::memory_order_acquire);
    out->cause = r.cause.load(std::memory_order_acquire);
    out->reserved = 0;
    out->denied_hits = r.denied.load(std::memory_order_relaxed);
}

KilnResult PetSafety::inspect(const KilnControlBodyKey& key, KilnPetSafetyState* out) const {
    if (!out || out->size < sizeof(KilnPetSafetyState)) return KILN_ERR_INVALID_ARGUMENT;
    const int i = find_key(key);
    if (i < 0) return KILN_ERR_NOT_FOUND;
    fill(rows_[i], out);
    // the row may have been retired while it was copied: then it is no answer
    if (rows_[i].entity.load(std::memory_order_acquire) != key.entity || rows_[i].generation.load() != key.body_generation) return KILN_ERR_NOT_FOUND;
    return KILN_OK;
}

KilnResult PetSafety::collect(KilnPetSafetyState* out, uint32_t capacity, uint32_t* written, uint32_t* rescan) const {
    if (!written || !rescan || (capacity && !out)) return KILN_ERR_INVALID_ARGUMENT;
    std::vector<uint32_t> dirty;
    const uint32_t n = high_.load(std::memory_order_acquire);
    for (uint32_t i = 0; i < n; ++i)
        if (rows_[i].entity.load(std::memory_order_acquire) &&
            (rows_[i].flags.load(std::memory_order_acquire) & (KILN_PET_SAFETY_DOWN_PENDING | KILN_PET_SAFETY_PROTECTION_LOST)))
            dirty.push_back(i);
    std::sort(dirty.begin(), dirty.end(), [&](uint32_t a, uint32_t b) { return rows_[a].dirty.load() < rows_[b].dirty.load(); });
    const uint32_t m = std::min<uint32_t>(capacity, static_cast<uint32_t>(dirty.size()));
    for (uint32_t k = 0; k < m; ++k) fill(rows_[dirty[k]], &out[k]);
    *written = m;
    *rescan = dirty.size() > m ? 1u : 0u;
    return KILN_OK;
}

KilnResult PetSafety::retire(const KilnControlBodyKey& key, bool body_exists) {
    std::lock_guard lock(tick_mu_);
    const int i = find_key(key);
    if (i < 0) return KILN_ERR_NOT_FOUND;
    if (body_exists) return KILN_ERR_INVALID_ARGUMENT;  // a live body is never unguarded by word
    rows_[i].entity.store(0, std::memory_order_release);
    count_.fetch_sub(1, std::memory_order_acq_rel);
    return KILN_OK;
}

KilnResult PetSafety::set_owner_entity(const KilnControlBodyKey& key, KilnEntity owner_entity) {
    std::lock_guard lock(tick_mu_);
    const int i = find_key(key);
    if (i < 0) return KILN_ERR_NOT_FOUND;
    rows_[i].owner_entity.store(owner_entity, std::memory_order_release);
    return KILN_OK;
}

KilnResult PetSafety::set_hostile(KilnEntity a, KilnEntity b, bool on) {
    if (!a || !b || a == b) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(tick_mu_);
    const uint64_t k = pair_key(a, b);
    int free_slot = -1;
    for (uint32_t i = 0; i < KILN_PETS_MAX_HOSTILE_PAIRS; ++i) {
        const uint64_t cur = pairs_[i].load(std::memory_order_acquire);
        if (cur == k) {
            if (!on) pairs_[i].store(0, std::memory_order_release);
            return KILN_OK;
        }
        if (cur == 0 && free_slot < 0) free_slot = static_cast<int>(i);
    }
    if (!on) return KILN_OK;
    if (free_slot < 0) return KILN_ERR_BUFFER_TOO_SMALL;
    pairs_[free_slot].store(k, std::memory_order_release);
    return KILN_OK;
}

bool PetSafety::hostile_pair(KilnEntity a, KilnEntity b) const {
    if (!a || !b || a == b) return false;
    const uint64_t k = pair_key(a, b);
    for (uint32_t i = 0; i < KILN_PETS_MAX_HOSTILE_PAIRS; ++i)
        if (pairs_[i].load(std::memory_order_acquire) == k) return true;
    return false;
}

void PetSafety::withdraw() {
    std::lock_guard lock(tick_mu_);
    withdrawn_.store(true, std::memory_order_release);
    const uint32_t n = high_.load(std::memory_order_acquire);
    for (uint32_t i = 0; i < n; ++i) {
        Row& r = rows_[i];
        if (!r.entity.load(std::memory_order_acquire)) continue;
        if (!(r.flags.fetch_or(KILN_PET_SAFETY_PROTECTION_LOST, std::memory_order_acq_rel) & KILN_PET_SAFETY_PROTECTION_LOST)) {
            r.dirty.store(dirty_seq_.fetch_add(1) + 1, std::memory_order_release);
            r.step.store(step_.load(std::memory_order_relaxed), std::memory_order_release);
            if (!r.cause.load()) r.cause.store(KILN_CONTROL_CAUSE_PROTECTION_LOST, std::memory_order_release);
            // the broker revokes every producer at once (a voluntary cause would not latch a down)
            if (quarantine_) quarantine_(KilnControlBodyKey{r.epoch.load(), r.generation.load(), r.entity.load(), 0}, KILN_CONTROL_CAUSE_PROTECTION_LOST);
        }
    }
}

void PetSafety::set_epoch(uint64_t epoch) {
    std::lock_guard lock(tick_mu_);
    if (epoch_.load(std::memory_order_acquire) == epoch) return;
    const uint32_t n = high_.load(std::memory_order_acquire);
    for (uint32_t i = 0; i < n; ++i) rows_[i].entity.store(0, std::memory_order_release);
    for (auto& p : pairs_) p.store(0, std::memory_order_release);
    count_.store(0, std::memory_order_release);
    high_.store(0, std::memory_order_release);
    withdrawn_.store(false, std::memory_order_release);
    epoch_.store(epoch, std::memory_order_release);
}

void PetSafety::latch(Row& r, uint32_t cause) {
    const uint32_t before = r.flags.fetch_or(KILN_PET_SAFETY_DOWN_PENDING, std::memory_order_acq_rel);
    if (before & KILN_PET_SAFETY_DOWN_PENDING) return;
    // the broker first: every producer loses authority and the root is denied before the latch is visible
    if (quarantine_) quarantine_(KilnControlBodyKey{r.epoch.load(), r.generation.load(), r.entity.load(), 0}, cause);
    r.cause.store(cause, std::memory_order_release);
    r.step.store(step_.load(std::memory_order_relaxed), std::memory_order_release);
    r.dirty.store(dirty_seq_.fetch_add(1) + 1, std::memory_order_release);
    latched_.fetch_add(1, std::memory_order_relaxed);
}

uint32_t PetSafety::death_policy(uint64_t epoch, KilnEntity entity, int32_t health) {
    const int i = find(epoch, entity);
    if (i < 0) return KILN_DEATH_GUARD_PASS;
    Row& r = rows_[i];
    const uint32_t f = r.flags.load(std::memory_order_acquire);
    death_skips_.fetch_add(1, std::memory_order_relaxed);
    if (f & KILN_PET_SAFETY_STAGING) return KILN_DEATH_GUARD_SKIP;   // a preview never becomes a semantic down
    if (health <= 0) latch(r, KILN_CONTROL_CAUSE_NATIVE_LETHAL);
    return KILN_DEATH_GUARD_SKIP;  // guarded: no death mark, no death event, no loot or XP; retired at the next Pre
}

bool PetSafety::same_owner(const Row& a, const Row& b) const {
    return a.owner.account == b.owner.account && a.owner.character.lo == b.owner.character.lo && a.owner.character.hi == b.owner.character.hi;
}

uint32_t PetSafety::deny(Row* a, Row* b) {
    if (a) a->denied.fetch_add(1, std::memory_order_relaxed);
    if (b) b->denied.fetch_add(1, std::memory_order_relaxed);
    denied_.fetch_add(1, std::memory_order_relaxed);
    return KILN_PET_HIT_DENY;
}

uint32_t PetSafety::hit_policy(const KilnPetHitQuery& q) {
    if (q.size < sizeof(KilnPetHitQuery)) return KILN_PET_HIT_PASS;
    const int ri = find(q.world_epoch, q.root_source), ti = find(q.world_epoch, q.target);
    if (ri < 0 && ti < 0) return KILN_PET_HIT_PASS;
    Row* R = ri >= 0 ? &rows_[ri] : nullptr;
    Row* T = ti >= 0 ? &rows_[ti] : nullptr;
    const bool harm = harmful(q), heal = heal_only(q);
    if (R) {
        const uint32_t rf = R->flags.load(std::memory_order_acquire);
        if (rf & kBlocked) return deny(R, T);
        if (q.root_health == 0 || (q.root_health < 0 && q.root_health != -1)) {
            latch(*R, KILN_CONTROL_CAUSE_NATIVE_LETHAL);   // a guarded root with no health: no new adjudication
            return deny(R, T);
        }
        if (q.hit_kind != 0 || !q.target) return deny(R, T);
        const KilnEntity owner = R->owner_entity.load(std::memory_order_acquire);
        if (T) {
            if (T->flags.load(std::memory_order_acquire) & kBlocked) return deny(R, T);
            if (same_owner(*R, *T)) return heal ? (passed_.fetch_add(1), static_cast<uint32_t>(KILN_PET_HIT_PASS)) : deny(R, T);
            if (R->pvp == KILN_PET_PVP_BY_MOD_RULE && T->pvp == KILN_PET_PVP_BY_MOD_RULE && harm &&
                hostile_pair(owner, T->owner_entity.load(std::memory_order_acquire))) {
                // fall through to the target's lethal check below
            } else {
                return deny(R, T);
            }
        } else if (is_player(q.target, q.target_class)) {
            if (heal && q.target == owner) return passed_.fetch_add(1), KILN_PET_HIT_PASS;
            if (R->pvp == KILN_PET_PVP_BY_MOD_RULE && harm && hostile_pair(owner, q.target)) return passed_.fetch_add(1), KILN_PET_HIT_PASS;
            return deny(R, T);
        } else {
            if (!harm) return deny(R, T);   // heals of creatures, or nothing at all
            if (!(q.target_class & KILN_PET_CLASS_KNOWN) || q.target_faction == KILN_PET_FACTION_UNKNOWN || q.target_faction >= 32)
                return deny(R, T);
            if (!(q.target_class & KILN_PET_CLASS_ENEMY_AI) || (kNeverFair >> q.target_faction & 1u)) return deny(R, T);
            if (!(R->hostile >> q.target_faction & 1u)) return deny(R, T);
            return passed_.fetch_add(1), KILN_PET_HIT_PASS;
        }
    }
    // the target is a guarded pet (and the root, if a pet, was allowed above)
    const uint32_t tf = T->flags.load(std::memory_order_acquire);
    if (tf & kBlocked) return deny(R, T);
    if (!R) {
        if (heal) return q.root_source && q.root_source == T->owner_entity.load(std::memory_order_acquire) ? (passed_.fetch_add(1), static_cast<uint32_t>(KILN_PET_HIT_PASS))
                                                                                                             : deny(R, T);
        if (!q.root_source) return deny(R, T);
        if (is_player(q.root_source, q.root_class)) {
            if (!(T->pvp == KILN_PET_PVP_BY_MOD_RULE && hostile_pair(q.root_source, T->owner_entity.load(std::memory_order_acquire))))
                return deny(R, T);
        } else {
            if (!(q.root_class & KILN_PET_CLASS_KNOWN) || q.root_faction == KILN_PET_FACTION_UNKNOWN || q.root_faction >= 32) return deny(R, T);
            if (!(q.root_class & KILN_PET_CLASS_ENEMY_AI) || (kNeverFair >> q.root_faction & 1u) || !(T->hostile >> q.root_faction & 1u))
                return deny(R, T);
        }
    }
    if (q.health_change < 0) {
        if (q.target_health < 0) return deny(R, T);   // unknown health on a guarded body: never a guess
        if (-static_cast<int64_t>(q.health_change) >= q.target_health) {
            latch(*T, KILN_CONTROL_CAUSE_NATIVE_LETHAL);
            return deny(R, T);   // the lethal hit never runs: no death, no reaction, no aggro; the body is retired at Pre
        }
    }
    passed_.fetch_add(1, std::memory_order_relaxed);
    return KILN_PET_HIT_PASS;
}

}  // namespace kcc
