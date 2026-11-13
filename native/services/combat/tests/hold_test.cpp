// SPDX-License-Identifier: MIT
// kiln.combat's entity pre-test of held hits (hold_filter.h, since 0.2.0-dev.27), on the host: which hit records a
// filter holds, and that the table only grew at its end.
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
    // the table grew at its end only: v2's last function, then hold_filter
    CHECK(offsetof(KilnCombatApi, hold_filter) == offsetof(KilnCombatApi, attribute_add) + sizeof(void*));
    CHECK(sizeof(KilnCombatApi) == offsetof(KilnCombatApi, hold_filter) + sizeof(void*));
    std::printf("hold_test: %s\n", g_fail ? "FAILED" : "ok");
    return g_fail ? 1 : 0;
}
