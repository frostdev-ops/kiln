// SPDX-License-Identifier: MIT
// kiln.combat's entity pre-test of held hits (hold_filter.h, since 0.2.0-dev.27), on the host: which hit records a
// filter holds, the entities each shape of hit names (what its event reports: hit_entities), and that the table only
// grew at its end. The shapes are the ones of managed/tests/Kiln.Contract.Tests/CombatHitShapeTests.cs.
#include <cstddef>
#include <cstdio>
#include <cstring>

#include "../hold_filter.h"
#include "services/kiln_combat.h"

using namespace kc;

static int g_fail = 0;
#define CHECK(cond)                                                          \
    do {                                                                     \
        if (!(cond)) {                                                       \
            ++g_fail;                                                        \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);      \
        }                                                                    \
    } while (0)

struct Rec {
    uint8_t b[0x200] = {};
    Rec(uint32_t target, uint32_t root_target, uint32_t source, uint32_t root_source) {
        std::memcpy(b + 0x94, &target, 4);       // HitOffsets::target
        std::memcpy(b + 0xA0, &root_target, 4);  // root_target
        std::memcpy(b + 0xA4, &source, 4);       // source
        std::memcpy(b + 0x90, &root_source, 4);  // root_source
    }
};

// The entities of the owner's guided check (event-filter-test/events.filter-hits, 13 Nov 2026): the player, the watched
// Vukah, the other one; and what hits come from.
constexpr uint32_t kPlayer = 1, kDoll = 17887, kOther = 17888, kWeakSpot = 17889, kSwing = 18001, kClaw = 18002,
                   kOtherClaw = 18003, kArrow = 18004, kArea = 18005;

struct Shape {
    const char* name;
    Rec rec;
    HitEntities want;  // what the event reports: Target, Part, Attacker, Source
};

static const Shape kShapes[] = {
    {"the player's swing on the doll", Rec(kDoll, kDoll, kSwing, kPlayer), {kDoll, kDoll, kPlayer, kSwing}},
    {"the player's swing on the doll's weak spot", Rec(kWeakSpot, kDoll, kSwing, kPlayer), {kDoll, kWeakSpot, kPlayer, kSwing}},
    {"the doll's own attack on the player", Rec(kPlayer, kPlayer, kClaw, kDoll), {kPlayer, kPlayer, kDoll, kClaw}},
    {"the player's arrow on the doll", Rec(kDoll, kDoll, kArrow, kPlayer), {kDoll, kDoll, kPlayer, kArrow}},
    {"the player's spell area on the doll", Rec(kDoll, kDoll, kArea, kPlayer), {kDoll, kDoll, kPlayer, kArea}},
    {"a hit on the doll with no root target", Rec(kDoll, 0, kPlayer, kPlayer), {kDoll, kDoll, kPlayer, kPlayer}},
    {"Kiln's Combat.Damage on the doll (AsGameHit)", Rec(kDoll, kDoll, kPlayer, kPlayer), {kDoll, kDoll, kPlayer, kPlayer}},
    {"the player's swing on the other", Rec(kOther, kOther, kSwing, kPlayer), {kOther, kOther, kPlayer, kSwing}},
    {"the other's attack on the player", Rec(kPlayer, kPlayer, kOtherClaw, kOther), {kPlayer, kPlayer, kOther, kOtherClaw}},
};

static bool names(const HitEntities& e, uint32_t id) {
    return id && (e.target == id || e.part == id || e.attacker == id || e.source == id);
}

int main() {
    HoldFilter f;
    const Rec doll(100, 100, 7, 7), weakspot(101, 100, 7, 7), other(200, 200, 7, 7), byDoll(200, 200, 100, 100), nothing(0, 0, 0, 0);
    // off (the default, and count 0): every hit is held
    CHECK(f.all() && f.wants(doll.b) && f.wants(other.b) && f.wants(nothing.b));
    // one watched doll: its hits (also on a weak spot of it, and the hits it deals), nothing else
    const uint32_t one[] = {100};
    CHECK(f.set(one, 1));                 // another filter than before
    CHECK(!f.set(one, 1));                // the same again: nothing changed (it is pushed at every added subscription)
    CHECK(!f.all() && f.size() == 1);
    CHECK(f.wants(doll.b));
    CHECK(f.wants(weakspot.b));
    CHECK(f.wants(byDoll.b));
    CHECK(!f.wants(other.b));
    CHECK(!f.wants(nothing.b));
    // duplicates and 0 are dropped; several entities
    const uint32_t many[] = {300, 0, 200, 300};
    CHECK(f.set(many, 4));
    CHECK(f.size() == 2 && f.wants(other.b) && !f.wants(doll.b));
    const uint32_t same[] = {200, 300};
    CHECK(!f.set(same, 2));               // the same entities in another order, without the duplicates
    // back to every hit
    CHECK(f.set(nullptr, 0));
    CHECK(!f.set(nullptr, 0));
    CHECK(f.all() && f.wants(other.b));

    // Every shape of hit: the entities its event reports, and the hold agrees with them for every watched entity. A hit
    // is about an entity when the entity is its target, the part hit, its attacker or its source; the doll's own attack
    // on the player is about the doll (Target the player, Attacker the doll), which is what the owner's check got.
    const uint32_t watched[] = {kDoll, kPlayer, kOther, kWeakSpot, kSwing, kClaw, kArrow, kArea, 99999};
    for (const Shape& s : kShapes) {
        const HitEntities e = hit_entities(s.rec.b);
        const bool same = e.target == s.want.target && e.part == s.want.part && e.attacker == s.want.attacker &&
                          e.source == s.want.source;
        if (!same) {
            ++g_fail;
            std::printf("FAIL %s: entities %u/%u/%u/%u, want %u/%u/%u/%u\n", s.name, e.target, e.part, e.attacker,
                        e.source, s.want.target, s.want.part, s.want.attacker, s.want.source);
        }
        for (const uint32_t w : watched) {
            HoldFilter one_entity;
            one_entity.set(&w, 1);
            if (one_entity.wants(s.rec.b) != names(s.want, w)) {
                ++g_fail;
                std::printf("FAIL %s: watching %u the hold says %d, the event names it: %d\n", s.name, w,
                            one_entity.wants(s.rec.b), names(s.want, w));
            }
        }
    }
    // the owner's case spelled out: a filter on the doll holds its own attack on the player and both kinds of hit on it,
    // and nothing between the player and the other Vukah
    {
        HoldFilter d;
        d.set(&kDoll, 1);
        CHECK(d.wants(kShapes[0].rec.b) && d.wants(kShapes[1].rec.b) && d.wants(kShapes[2].rec.b));
        CHECK(hit_entities(kShapes[2].rec.b).target == kPlayer && hit_entities(kShapes[2].rec.b).attacker == kDoll);
        CHECK(!d.wants(kShapes[7].rec.b) && !d.wants(kShapes[8].rec.b));
    }

    // the table grew at its end only: v2's last function, then hold_filter
    CHECK(offsetof(KilnCombatApi, hold_filter) == offsetof(KilnCombatApi, attribute_add) + sizeof(void*));
    CHECK(sizeof(KilnCombatApi) == offsetof(KilnCombatApi, hold_filter) + sizeof(void*));
    std::printf("hold_test: %zu hit shapes, %s\n", sizeof kShapes / sizeof kShapes[0], g_fail ? "FAILED" : "ok");
    return g_fail ? 1 : 0;
}
