// SPDX-License-Identifier: MIT
// Exercises the production rule publication, ABI input, Shroud floor and ray guard without a game or Windows.
#define KILN_RULES_HOST_TEST
#include "rules.cpp"

#include <cstdlib>
#include <limits>
#include <thread>

namespace {
int failures = 0;
void check(bool ok, const char* text) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", text);
    if (!ok) ++failures;
}
bool same(const KilnRulesPlayer& a, const KilnRulesPlayer& b) { return std::memcmp(&a, &b, sizeof a) == 0; }
}

int main() {
    KilnRulesPlayer input = kDefault, out;
    input.size = 24;
    input.glide_sink = 0;
    input.glide_climb = 99;
    check(read_in(&input, &out) && out.glide_sink == 1 && out.glide_climb == 0, "v1 ABI input keeps all new defaults");
    input.size = 48;
    check(read_in(&input, &out) && out.glide_sink == 0 && out.glide_climb == 0, "48-byte ABI input keeps climb default");
    input.size = 23;
    check(!read_in(&input, &out), "undersized ABI input refused");
    input = kDefault;
    input.flags = KILN_RULES_GLIDE_BOOST;
    input.glide_sink = NAN;
    input.glide_thrust = INFINITY;
    input.glide_max_speed = 10000;
    input.glide_lift = 10000;
    input.glide_climb = 10000;
    store(g_local, &input);
    load(g_local, &out);
    check(out.glide_sink == 1 && out.glide_thrust == 0 && out.glide_max_speed == 120 && out.glide_lift == 100 && out.glide_climb == 120,
          "native input rejects non-finite and bounds glide values");

    Slot slot;
    auto a = kDefault, b = kDefault;
    a.flags = KILN_RULES_GLIDE_BOOST;
    a.glide_sink = 0;
    a.glide_thrust = 15;
    a.glide_max_speed = 35;
    a.glide_climb = 15;
    b.flags = KILN_RULES_SHROUD_FREEZE;
    b.build_cost = 0;
    b.shroud_floor = 0.75f;
    std::atomic<bool> done{false}, mixed{false};
    std::thread writer([&] {
        for (int i = 0; i < 100000; ++i) store(slot, i & 1 ? &a : &b);
        done = true;
    });
    auto read = [&] {
        do {
            KilnRulesPlayer value;
            load(slot, &value);
            if (!same(value, a) && !same(value, b) && !same(value, kDefault)) mixed = true;
        } while (!done);
    };
    std::thread other(read);
    read();
    writer.join();
    other.join();
    check(!mixed, "concurrent readers always see a whole published rule set");
    {
        std::lock_guard lock(g_extra_mu);
        store(g_extra[0], &a);
        g_extra_ids[0] = 2000;
    }
    check(player_rules(2000, &out) && same(out, a), "extra entity rules read with their identity");
    {
        std::lock_guard lock(g_extra_mu);
        store(g_extra[0], &b);
        g_extra_ids[0] = 2001;
    }
    check(!player_rules(2000, &out) && same(out, kDefault), "reused extra slot never grants its new rules to the old entity");

    store(g_players[3], &b); // BuildCost 0
    check(storage_payment_free(100, 100, 3, 3), "storage payment: only the authorized actor's exact upgrade call is free");
    check(!storage_payment_free(101, 100, 3, 3), "storage payment: unrelated callers still pay");
    check(!storage_payment_free(100, 0, 3, 3), "storage payment: a missing callsite never waives payment");
    check(!storage_payment_free(100, 100, 4, 3) && !storage_payment_free(100, 100, 3, 4), "storage payment: another actor/owner cannot borrow free building");
    check(!storage_payment_free(100, 100, 0, 0), "storage payment: an absent operator cannot waive payment");
    b.build_cost = 0.25f;
    store(g_players[3], &b);
    check(!storage_payment_free(100, 100, 3, 3), "storage payment: a nonzero discounted cost still pays");
    store(g_players[3], nullptr);
    check(!storage_payment_free(100, 100, 3, 3), "storage payment: turning the rule off takes effect on the next payment");

    check(shroud_branch(3, 20, 100, 0.05f, false, 0.25) == 4, "deadly Shroud on a slow step holds before crossing the floor");
    check(shroud_branch(3, 80, 100, 0.05f, false, 0.25) == 3, "Shroud well above the floor keeps its own drain");
    check(shroud_branch(2, 20, 100, 0.05f, false, 30) == 4, "dangerous Shroud floor also handles huge step time");
    check(shroud_branch(3, 0, 100, 0, true, 0) == 1, "frozen zero timer takes recovery without a death check");
    check(shroud_branch(3, 50, 100, 0, true, 0) == 4, "frozen positive timer takes no-drain branch");
    check(shroud_branch(3, 20, 100, 0.05f, false, NAN) == 4, "unknown step time cannot drain through a floor");
    check(shroud_branch(3, INT32_MAX, INT32_MAX, 1, false, 0.25) == 4, "large Shroud attributes cannot overflow floor conversion");
    check(shroud_branch(1, 0, 100, 1, true, 1) == 1 && shroud_branch(4, 0, 100, 1, true, 1) == 4,
          "safe and unknown fog types are unchanged");

    const double player[3] = {3000, 800, 2000}, bad_player[3] = {NAN, INFINITY, NAN};
    float from[3] = {3000, 800, 2000}, to[3] = {3100, 810, 2100};
    float before[6];
    std::memcpy(before, from, sizeof from);
    std::memcpy(before + 3, to, sizeof to);
    check(!guard_ray(from, to, player) && std::memcmp(before, from, sizeof from) == 0 && std::memcmp(before + 3, to, sizeof to) == 0,
          "normal health-bar rays are bit-for-bit unchanged");
    to[0] = 4000; to[1] = 800; to[2] = 2000;
    check(!guard_ray(from, to, player), "a ray exactly 1 km long is unchanged");
    to[0] = 4001;
    check(guard_ray(from, to, player) && std::memcmp(from, to, sizeof from) == 0 && from[0] == 4001,
          "an overlong ray becomes zero-length at its valid end");
    from[0] = NAN; to[0] = INFINITY;
    check(guard_ray(from, to, player) && finite3(from) && std::memcmp(from, to, sizeof from) == 0 && from[0] == 3000,
          "two corrupt endpoints use the valid player position");
    from[0] = NAN; to[0] = INFINITY;
    check(guard_ray(from, to, bad_player) && finite3(from) && std::memcmp(from, to, sizeof from) == 0 && from[0] == 0,
          "corrupt endpoints and player still produce a finite zero-length ray");
    from[0] = std::numeric_limits<float>::max(); to[0] = -from[0];
    check(guard_ray(from, to, bad_player) && from[0] == 0 && finite3(from), "huge finite endpoints never become replacement positions");
    std::printf("rules guards: %d failure(s)\n", failures);
    return failures ? EXIT_FAILURE : EXIT_SUCCESS;
}
