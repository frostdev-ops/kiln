// SPDX-License-Identifier: MIT
// kiln.pets' native safety model on the host (dev.32 package 5; 19d §4.4-4.6, gates 3-5): rows and their limits, the
// death policy (a guarded pet is never marked dead; a staging body never latches), the whole target rule for hits
// (player, owner, same-owner pet, another owner's pet, declared pairs, hostile and friendly creatures, unknown sources,
// terrain, heals), lethal allowed hits latching and denied before the original, quarantine called before the latch is
// visible, withdrawal, collection without clearing, retirement refused while the body lives, a world's end, and the
// worker-thread reads racing tick-side binds.
//   native/services/creature_control/tests/run.sh
#include <atomic>
#include <cstdio>
#include <cstring>
#include <thread>
#include <vector>

#include "../pets_safety.h"
#include "services/kiln_decisions.h"

using namespace kcc;

namespace {

int g_fail = 0, g_pass = 0;
#define CHECK(cond)                                                              \
    do {                                                                         \
        if (cond) ++g_pass;                                                      \
        else {                                                                   \
            ++g_fail;                                                            \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);          \
        }                                                                        \
    } while (0)

std::vector<std::pair<KilnControlBodyKey, uint32_t>> g_quarantined;
std::atomic<int> g_quarantines{0};
KilnResult fake_quarantine(const KilnControlBodyKey& key, uint32_t cause) {
    g_quarantines.fetch_add(1);
    if (g_quarantined.size() < 4096) g_quarantined.push_back({key, cause});
    return KILN_OK;
}

constexpr uint64_t E = 7;   // the world epoch
constexpr uint32_t Vukah = 3, WildBeast = 6, Wildlife = 5, PlayerAlly = 11;
constexpr uint32_t Hostile = (1u << 2) | (1u << Vukah) | (1u << 4) | (1u << WildBeast) | (1u << 10) | (1u << 13) | (1u << 14) | (1u << 15);

KilnControlBodyKey key(KilnEntity e, uint64_t gen = 1) { return KilnControlBodyKey{E, gen, e, 0}; }

KilnPetSafetyBinding binding(KilnEntity body, uint64_t owner_char, KilnEntity owner_entity, uint32_t pvp = KILN_PET_PVP_OFF,
                             bool staging = false, uint64_t gen = 1) {
    KilnPetSafetyBinding b{};
    b.size = sizeof b;
    b.flags = staging ? KILN_PET_BIND_STAGING : 0;
    b.body = key(body, gen);
    b.pet = KilnPetId128{body, 0xbeef};
    b.owner = KilnPetCharacterKey{0xA, KilnPetId128{owner_char, 1}};
    b.character_session = 99;
    b.owner_entity = owner_entity;
    b.hostile_factions = Hostile;
    b.pvp = pvp;
    return b;
}

KilnPetHitQuery hit(KilnEntity root, KilnEntity target, int32_t change = -10, int32_t target_hp = 100) {
    KilnPetHitQuery q{};
    q.size = sizeof q;
    q.world_epoch = E;
    q.root_source = q.source = root;
    q.target = target;
    q.health_change = change;
    q.root_health = 100;
    q.target_health = target_hp;
    q.root_faction = q.target_faction = KILN_PET_FACTION_UNKNOWN;
    return q;
}

KilnPetHitQuery creature_hit(KilnEntity root, KilnEntity target, uint8_t faction, bool enemy, bool root_is_creature, int32_t change = -10) {
    KilnPetHitQuery q = hit(root, target, change);
    const uint8_t cls = static_cast<uint8_t>(KILN_PET_CLASS_KNOWN | (enemy ? KILN_PET_CLASS_ENEMY_AI : 0));
    if (root_is_creature) {
        q.root_faction = faction;
        q.root_class = cls;
    } else {
        q.target_faction = faction;
        q.target_class = cls;
    }
    return q;
}

// A: owner character 1 (player entity 1) with pets 2001 and 2002; B: character 2 (player entity 2) with pet 2003;
// creatures: 3000 (a Vukah enemy), 3001 (a PlayerAlly companion), 3002 (wildlife).
PetSafety* make() {
    auto* s = new PetSafety(&fake_quarantine);
    s->set_guard_installed(true);
    s->set_epoch(E);
    CHECK(s->bind(binding(2001, 1, 1)) == KILN_OK);
    CHECK(s->bind(binding(2002, 1, 1)) == KILN_OK);
    CHECK(s->bind(binding(2003, 2, 2)) == KILN_OK);
    return s;
}

void test_rows() {
    PetSafety s(&fake_quarantine);
    s.set_epoch(E);
    CHECK(s.bind(binding(2001, 1, 1)) == KILN_ERR_UNAVAILABLE);   // no native death guard installed: no pet is admitted
    s.set_guard_installed(true);
    CHECK(s.bind(binding(2001, 1, 1)) == KILN_OK);
    CHECK(s.bind(binding(2001, 1, 1)) == KILN_OK);                  // idempotent
    CHECK(s.rows() == 1);
    CHECK(s.bind(binding(2001, 1, 1, KILN_PET_PVP_OFF, false, 2)) == KILN_ERR_INVALID_ARGUMENT);   // another generation, same entity
    auto bad = binding(2005, 1, 1);
    bad.hostile_factions |= 1u << PlayerAlly;
    CHECK(s.bind(bad) == KILN_ERR_INVALID_ARGUMENT);               // the players' side is never a fair target
    bad = binding(500, 1, 1);
    CHECK(s.bind(bad) == KILN_ERR_INVALID_ARGUMENT);               // a player or reserved id
    bad = binding(2006, 1, 1);
    bad.pet = KilnPetId128{0, 0};
    CHECK(s.bind(bad) == KILN_ERR_INVALID_ARGUMENT);
    bad = binding(2007, 1, 1);
    bad.body.world_epoch = E + 1;
    CHECK(s.bind(bad) == KILN_ERR_INVALID_ARGUMENT);               // a key of another world
    KilnPetSafetyState st{};
    st.size = sizeof st;
    CHECK(s.inspect(key(2001), &st) == KILN_OK);
    CHECK(st.flags == KILN_PET_SAFETY_GUARDED);
    CHECK(st.pet.lo == 2001);
    CHECK(s.inspect(key(2001, 2), &st) == KILN_ERR_NOT_FOUND);
    CHECK(s.involved(E, 2001) == 1 && s.involved(E, 2002) == 0 && s.involved(E + 1, 2001) == 0);
    // the ceiling: 256 rows, refused before anything is written
    for (KilnEntity e = 4000; e < 4000 + KILN_PETS_MAX_ROWS - 1; ++e) CHECK(s.bind(binding(e, 9, 9)) == KILN_OK);
    CHECK(s.bind(binding(9000, 9, 9)) == KILN_ERR_BUFFER_TOO_SMALL);
    CHECK(s.rows() == KILN_PETS_MAX_ROWS);
    // retirement: refused while the body lives; the slot is reused after
    CHECK(s.retire(key(4000), true) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(s.retire(key(4000), false) == KILN_OK);
    CHECK(s.involved(E, 4000) == 0);
    CHECK(s.bind(binding(9000, 9, 9)) == KILN_OK);
    // a world's end drops every row of the old epoch
    s.set_epoch(E + 1);
    CHECK(s.rows() == 0 && s.involved(E, 2001) == 0);
}

void test_death_policy() {
    g_quarantined.clear();
    PetSafety* s = make();
    CHECK(s->death_policy(E, 3000, 0) == KILN_DEATH_GUARD_PASS);     // not a pet: the death goes on
    CHECK(s->death_policy(E, 2001, 0) == KILN_DEATH_GUARD_SKIP);     // a guarded pet is never marked dead
    KilnPetSafetyState st{};
    st.size = sizeof st;
    s->inspect(key(2001), &st);
    CHECK(st.flags & KILN_PET_SAFETY_DOWN_PENDING);
    CHECK(st.cause == KILN_CONTROL_CAUSE_NATIVE_LETHAL);
    CHECK(st.dirty_sequence == 1);
    CHECK(g_quarantined.size() == 1 && g_quarantined[0].first.entity == 2001 && g_quarantined[0].second == KILN_CONTROL_CAUSE_NATIVE_LETHAL);
    CHECK(s->death_policy(E, 2001, 0) == KILN_DEATH_GUARD_SKIP);     // keeps skipping while it waits for retirement
    CHECK(s->death_policy(E, 2001, 5) == KILN_DEATH_GUARD_SKIP);     // healed after the latch: still skipped, never resumed
    CHECK(g_quarantined.size() == 1);                                // one logical down
    s->inspect(key(2001), &st);
    CHECK(st.dirty_sequence == 1);
    // a staging body skips the death visit without a semantic down
    CHECK(s->bind(binding(2010, 1, 1, KILN_PET_PVP_OFF, true)) == KILN_OK);
    CHECK(s->death_policy(E, 2010, -3) == KILN_DEATH_GUARD_SKIP);
    s->inspect(key(2010), &st);
    CHECK(!(st.flags & KILN_PET_SAFETY_DOWN_PENDING));
    CHECK(s->activate(key(2010)) == KILN_OK);
    s->inspect(key(2010), &st);
    CHECK(st.flags == KILN_PET_SAFETY_GUARDED);
    delete s;
}

void test_hit_matrix() {
    PetSafety* s = make();
    // pet -> players: the owner, an unrelated player, an explicit attack, retaliation: all denied
    CHECK(s->hit_policy(hit(2001, 1)) == KILN_PET_HIT_DENY);
    CHECK(s->hit_policy(hit(2001, 2)) == KILN_PET_HIT_DENY);
    // players -> pet: denied
    CHECK(s->hit_policy(hit(1, 2001)) == KILN_PET_HIT_DENY);
    CHECK(s->hit_policy(hit(2, 2001)) == KILN_PET_HIT_DENY);
    // pet -> pet: the same owner's and another owner's
    CHECK(s->hit_policy(hit(2001, 2002)) == KILN_PET_HIT_DENY);
    CHECK(s->hit_policy(hit(2001, 2003)) == KILN_PET_HIT_DENY);
    // pet -> creatures: a hostile unowned enemy passes; a PlayerAlly, wildlife, an unread creature, a non-enemy do not
    CHECK(s->hit_policy(creature_hit(2001, 3000, Vukah, true, false)) == KILN_PET_HIT_PASS);
    CHECK(s->hit_policy(creature_hit(2001, 3001, PlayerAlly, true, false)) == KILN_PET_HIT_DENY);
    CHECK(s->hit_policy(creature_hit(2001, 3002, Wildlife, false, false)) == KILN_PET_HIT_DENY);
    CHECK(s->hit_policy(creature_hit(2001, 3002, Wildlife, true, false)) == KILN_PET_HIT_DENY);   // not in the declared list
    CHECK(s->hit_policy(hit(2001, 3000)) == KILN_PET_HIT_DENY);                                   // nothing read: conservative
    CHECK(s->hit_policy(creature_hit(2001, 3000, Vukah, false, false)) == KILN_PET_HIT_DENY);    // no enemy AI (a villager)
    // a player-like component always overrides the faction
    auto q = creature_hit(2001, 3000, Vukah, true, false);
    q.target_class |= KILN_PET_CLASS_PLAYER;
    CHECK(s->hit_policy(q) == KILN_PET_HIT_DENY);
    // terrain and buildings: denied for a pet root
    q = creature_hit(2001, 3000, Vukah, true, false);
    q.hit_kind = 1;
    CHECK(s->hit_policy(q) == KILN_PET_HIT_DENY);
    // creatures -> pet: a hostile enemy passes, a friendly one or an unknown source is denied
    CHECK(s->hit_policy(creature_hit(3000, 2001, Vukah, true, true)) == KILN_PET_HIT_PASS);
    CHECK(s->hit_policy(creature_hit(3001, 2001, PlayerAlly, true, true)) == KILN_PET_HIT_DENY);
    CHECK(s->hit_policy(hit(0, 2001)) == KILN_PET_HIT_DENY);
    CHECK(s->hit_policy(hit(3000, 2001)) == KILN_PET_HIT_DENY);   // unread root
    // heals: of the owner and of the same owner's pets pass; of anyone else they are denied
    CHECK(s->hit_policy(hit(2001, 1, +10)) == KILN_PET_HIT_PASS);
    CHECK(s->hit_policy(hit(2001, 2002, +10)) == KILN_PET_HIT_PASS);
    CHECK(s->hit_policy(hit(2001, 2, +10)) == KILN_PET_HIT_DENY);
    CHECK(s->hit_policy(hit(2001, 2003, +10)) == KILN_PET_HIT_DENY);
    CHECK(s->hit_policy(creature_hit(2001, 3000, Vukah, true, false, +10)) == KILN_PET_HIT_DENY);
    CHECK(s->hit_policy(hit(1, 2001, +10)) == KILN_PET_HIT_PASS);   // the owner heals the pet
    CHECK(s->hit_policy(hit(2, 2001, +10)) == KILN_PET_HIT_DENY);
    // a heal with a harmful reaction is harm
    q = hit(2001, 2002, +10);
    q.reaction = 1;
    CHECK(s->hit_policy(q) == KILN_PET_HIT_DENY);
    // knockback or aggro alone is harm: denied on a protected target
    q = hit(2001, 2, 0);
    q.pushback = 1;
    CHECK(s->hit_policy(q) == KILN_PET_HIT_DENY);
    q = hit(2001, 2, 0);
    q.aggro = 5;
    CHECK(s->hit_policy(q) == KILN_PET_HIT_DENY);
    // not a pet at all: never asked about, never in the way
    CHECK(s->hit_policy(creature_hit(3000, 1, Vukah, true, true)) == KILN_PET_HIT_PASS);
    delete s;
}

void test_pairs() {
    PetSafety* s = new PetSafety(&fake_quarantine);
    s->set_guard_installed(true);
    s->set_epoch(E);
    CHECK(s->bind(binding(2001, 1, 1, KILN_PET_PVP_BY_MOD_RULE)) == KILN_OK);
    CHECK(s->bind(binding(2003, 2, 2, KILN_PET_PVP_BY_MOD_RULE)) == KILN_OK);
    CHECK(s->bind(binding(2004, 3, 3, KILN_PET_PVP_OFF)) == KILN_OK);
    // by mod rule, but no pair declared: protected like off
    CHECK(s->hit_policy(hit(2001, 2)) == KILN_PET_HIT_DENY);
    CHECK(s->hit_policy(hit(2001, 2003)) == KILN_PET_HIT_DENY);
    CHECK(s->set_hostile(1, 2, true) == KILN_OK);
    CHECK(s->hit_policy(hit(2001, 2)) == KILN_PET_HIT_PASS);        // the mod's duel: pet -> the other player
    CHECK(s->hit_policy(hit(2, 2001)) == KILN_PET_HIT_PASS);        // the other player -> the pet
    CHECK(s->hit_policy(hit(2001, 2003)) == KILN_PET_HIT_PASS);     // both pets by mod rule, owners paired
    CHECK(s->hit_policy(hit(2001, 3)) == KILN_PET_HIT_DENY);        // a player outside the pair
    CHECK(s->set_hostile(1, 3, true) == KILN_OK);
    CHECK(s->hit_policy(hit(3, 2004)) == KILN_PET_HIT_DENY);        // the pet of PvP off stays protected whatever the pairs
    CHECK(s->hit_policy(hit(2001, 2004)) == KILN_PET_HIT_DENY);
    CHECK(s->set_hostile(2, 1, false) == KILN_OK);                  // either order ends it
    CHECK(s->hit_policy(hit(2001, 2)) == KILN_PET_HIT_DENY);
    CHECK(s->set_hostile(1, 1, true) == KILN_ERR_INVALID_ARGUMENT);
    for (KilnEntity a = 10; a < 10 + KILN_PETS_MAX_HOSTILE_PAIRS - 1; ++a) CHECK(s->set_hostile(a, 900, true) == KILN_OK);
    CHECK(s->set_hostile(5, 6, true) == KILN_ERR_BUFFER_TOO_SMALL);
    delete s;
}

void test_lethal() {
    g_quarantined.clear();
    PetSafety* s = make();
    // an allowed hit that would kill: latched, quarantined first, denied before the original
    auto q = creature_hit(3000, 2001, Vukah, true, true, -150);
    q.target_health = 100;
    CHECK(s->hit_policy(q) == KILN_PET_HIT_DENY);
    KilnPetSafetyState st{};
    st.size = sizeof st;
    s->inspect(key(2001), &st);
    CHECK(st.flags & KILN_PET_SAFETY_DOWN_PENDING);
    CHECK(g_quarantined.size() == 1);
    // from now on nothing of it or on it is adjudicated: the hit it was swinging is denied, no further hit lands
    CHECK(s->hit_policy(creature_hit(2001, 3000, Vukah, true, false)) == KILN_PET_HIT_DENY);
    CHECK(s->hit_policy(creature_hit(3000, 2001, Vukah, true, true, -1)) == KILN_PET_HIT_DENY);
    // a hit that leaves health passes
    q = creature_hit(3000, 2002, Vukah, true, true, -40);
    q.target_health = 100;
    CHECK(s->hit_policy(q) == KILN_PET_HIT_PASS);
    // unknown health on a guarded body is never guessed
    q.target_health = -1;
    CHECK(s->hit_policy(q) == KILN_PET_HIT_DENY);
    // a guarded root with no health latches and is denied before the next death visit
    q = creature_hit(2002, 3000, Vukah, true, false);
    q.root_health = 0;
    CHECK(s->hit_policy(q) == KILN_PET_HIT_DENY);
    s->inspect(key(2002), &st);
    CHECK(st.flags & KILN_PET_SAFETY_DOWN_PENDING);
    // staging rows: invulnerable and harmless
    CHECK(s->bind(binding(2011, 1, 1, KILN_PET_PVP_OFF, true)) == KILN_OK);
    CHECK(s->hit_policy(creature_hit(3000, 2011, Vukah, true, true, -999)) == KILN_PET_HIT_DENY);
    CHECK(s->hit_policy(creature_hit(2011, 3000, Vukah, true, false)) == KILN_PET_HIT_DENY);
    s->inspect(key(2011), &st);
    CHECK(!(st.flags & KILN_PET_SAFETY_DOWN_PENDING));
    delete s;
}

void test_collect_and_withdraw() {
    g_quarantined.clear();
    PetSafety* s = make();
    KilnPetSafetyState out[4]{};
    uint32_t written = 9, rescan = 9;
    CHECK(s->collect(out, 4, &written, &rescan) == KILN_OK && written == 0 && rescan == 0);
    s->death_policy(E, 2002, 0);
    s->death_policy(E, 2001, 0);
    CHECK(s->collect(out, 4, &written, &rescan) == KILN_OK && written == 2 && rescan == 0);
    CHECK(out[0].body.entity == 2002 && out[1].body.entity == 2001);   // oldest latch first
    CHECK(s->collect(out, 1, &written, &rescan) == KILN_OK && written == 1 && rescan == 1);   // a full buffer asks for a rescan
    CHECK(s->collect(out, 4, &written, &rescan) == KILN_OK && written == 2);                  // collecting never clears a latch
    // withdrawal: every row protection-lost at once, still guarded, new binds refused
    s->withdraw();
    CHECK(s->collect(out, 4, &written, &rescan) == KILN_OK && written == 3);
    KilnPetSafetyState st{};
    st.size = sizeof st;
    s->inspect(key(2003), &st);
    CHECK((st.flags & KILN_PET_SAFETY_PROTECTION_LOST) && (st.flags & KILN_PET_SAFETY_GUARDED));
    CHECK(st.cause == KILN_CONTROL_CAUSE_PROTECTION_LOST);
    CHECK(s->death_policy(E, 2003, 0) == KILN_DEATH_GUARD_SKIP);   // still guarded until retired: never fail open
    CHECK(s->hit_policy(creature_hit(3000, 2003, Vukah, true, true)) == KILN_PET_HIT_DENY);
    CHECK(s->bind(binding(2020, 1, 1)) == KILN_ERR_UNAVAILABLE);
    // rows go only once their bodies are gone
    CHECK(s->retire(key(2003), true) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(s->retire(key(2003), false) == KILN_OK);
    delete s;
}

void test_threads() {
    // workers read (death and hit policies) while the tick binds and retires: no torn row, no crash under the sanitizers
    PetSafety* s = new PetSafety(&fake_quarantine);
    s->set_guard_installed(true);
    s->set_epoch(E);
    std::atomic<bool> stop{false};
    std::atomic<uint64_t> reads{0};
    std::vector<std::thread> workers;
    for (int w = 0; w < 4; ++w)
        workers.emplace_back([&] {
            while (!stop.load()) {
                for (KilnEntity e = 5000; e < 5032; ++e) {
                    s->hit_policy(creature_hit(3000, e, Vukah, true, true, -1));
                    s->involved(E, e);
                    reads.fetch_add(1);
                }
            }
        });
    for (int round = 0; round < 200; ++round) {
        for (KilnEntity e = 5000; e < 5032; ++e) s->bind(binding(e, 1, 1));
        for (KilnEntity e = 5000; e < 5032; ++e) s->retire(key(e), false);
    }
    stop = true;
    for (auto& t : workers) t.join();
    CHECK(reads.load() > 0);
    CHECK(s->rows() == 0);
    delete s;
}

}  // namespace

int main() {
    test_rows();
    test_death_policy();
    test_hit_matrix();
    test_pairs();
    test_lethal();
    test_collect_and_withdraw();
    test_threads();
    std::printf("pets_test: %d passed, %d failed\n", g_pass, g_fail);
    return g_fail == 0 ? 0 : 1;
}
