// SPDX-License-Identifier: MIT
// Host checks of character flight's decisions (flight_math.h).
#include "flight_math.h"

#include <cstdio>
#include <cstdlib>

namespace {
int failures = 0;
void check(bool ok, const char* message) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", message);
    failures += !ok;
}
}  // namespace

int main() {
    using namespace flight_math;
    Actor air;  // airborne, nothing else
    Actor ground;
    ground.grounded = true;

    check(next_state(kFalling, kFalling, true, true, false, air, 0) == kBuildingFlying, "armed and airborne: take off");
    check(next_state(kWalking, kWalking, true, true, false, ground, 0) == kWalking, "armed on the ground: walk (jump to take off)");
    check(next_state(kFalling, kFalling, true, false, false, air, 0) == kFalling, "not armed: the game's fall");
    Actor glide = air;
    glide.gliding = true;
    check(next_state(kFalling, 4, true, true, false, glide, 0) == 4, "the glider stays the game's");
    check(next_state(kBuildingFlying, kBuildingFlying, true, true, false, glide, 0) == kFalling, "opening the glider ends the flight");
    Actor dead = air;
    dead.dead = true;
    check(next_state(kBuildingFlying, kBuildingFlying, true, true, false, dead, 0) == kFalling, "death ends the flight");
    Actor wet = air;
    wet.water = true;
    check(next_state(kBuildingFlying, kBuildingFlying, true, true, false, wet, 0) == kFalling, "water ends the flight");

    check(next_state(kBuildingFlying, kBuildingFlying, true, true, false, air, 0) == kBuildingFlying, "flying stays flying");
    check(next_state(kBuildingFlying, kBuildingFlying, true, true, false, ground, 0) == kBuildingFlying,
          "grounded while not sinking: still flying (hovering low)");
    check(next_state(kBuildingFlying, kBuildingFlying, true, true, false, ground, -kClimb) == kWalking, "grounded while sinking: landed");
    check(next_state(kBuildingFlying, kBuildingFlying, true, false, false, air, 0) == kFalling, "the rule ends in the air: fall");
    check(next_state(kBuildingFlying, kBuildingFlying, false, false, false, air, 0) == kBuildingFlying,
          "a BuildingFlying entity Kiln did not fly (the building spark) is left alone");
    check(next_state(kBuildingFlying, kWalking, true, true, false, air, 0) == kWalking, "a build whose state is not sticky decides itself");

    Actor zone = air;
    zone.near_zone = true;
    check(next_state(kFalling, kFalling, true, true, true, zone, 0) == kFalling, "a forced actor zone bit cannot authorize a raw base-only request");
    check(next_state(kFalling, kFalling, true, true, true, air, 0) == kFalling, "building area only: outside, no take-off");
    check(next_state(kBuildingFlying, kBuildingFlying, true, true, true, air, 0) == kFalling, "building area only: leaving the zone ends it");
    check(!allowed(true, true, zone) && !allowed(true, true, air) && !allowed(false, false, air), "base-only gravity needs the server-authored snapshot too");

    // NoClip: the same flight, at once and everywhere, never landing
    check(next_state(kWalking, kWalking, true, false, false, ground, 0, true) == kBuildingFlying, "NoClip takes off from the ground at once");
    check(next_state(kFalling, kFalling, true, false, false, air, 0, true) == kBuildingFlying, "NoClip takes off in the air");
    check(next_state(kSliding, kSliding, true, false, false, ground, 0, true) == kBuildingFlying &&
          next_state(kSwimming, kSwimming, true, false, false, wet, 0, true) == kBuildingFlying &&
          next_state(kDiving, kDiving, true, false, false, wet, 0, true) == kBuildingFlying,
          "NoClip takes off from a slide and from water");
    check(next_state(4, 4, true, false, false, glide, 0, true) == 4 && next_state(7, 7, true, false, false, air, 0, true) == 7 &&
          next_state(8, 8, true, false, false, air, 0, true) == 8 && next_state(5, 5, true, false, false, air, 0, true) == 5 &&
          next_state(3, 3, true, false, false, air, 0, true) == 3,
          "NoClip leaves the glider, the hook, a wall, a ladder and the game's own flying alone");
    check(next_state(kBuildingFlying, kBuildingFlying, true, false, false, wet, 0, true) == kBuildingFlying, "water does not end NoClip's flight");
    check(next_state(kBuildingFlying, kBuildingFlying, true, false, false, ground, -kClimb, true) == kBuildingFlying,
          "NoClip never lands: grounded while sinking keeps flying");
    check(next_state(kBuildingFlying, kBuildingFlying, true, false, false, dead, 0, true) == kFalling &&
          next_state(kBuildingFlying, kBuildingFlying, true, false, false, glide, 0, true) == kFalling,
          "death and the glider end NoClip's flight");
    Actor climbing = air;
    climbing.climbing = true;
    check(next_state(kBuildingFlying, kBuildingFlying, true, false, false, climbing, 0, true) == kFalling &&
          next_state(kWalking, kWalking, true, false, false, climbing, 0, true) == kWalking,
          "a ladder or a wall ends NoClip's flight and stops its take-off");
    check(next_state(kBuildingFlying, kBuildingFlying, true, false, false, air, 0, false) == kFalling, "NoClip's rule ends in the air: fall");
    check(next_state(kBuildingFlying, kBuildingFlying, true, true, false, ground, -kClimb, false) == kWalking,
          "NoClip ended, character flight still on: it lands as character flight does");
    check(next_state(kBuildingFlying, kBuildingFlying, true, true, false, wet, 0, true) == kBuildingFlying,
          "with both rules, NoClip's wider flight wins in water");
    check(next_state(kWalking, kWalking, false, false, false, ground, 0, true) == kWalking, "an entity without rules never takes off, whatever the flag says");
    check(next_state(kBuildingFlying, kBuildingFlying, true, false, true, air, 0, true) == kBuildingFlying,
          "building area only does not narrow NoClip");
    check(allowed_noclip(true, wet) && !allowed_noclip(true, dead) && !allowed_noclip(false, air) && noclip_takeoff(kWalking) &&
          !noclip_takeoff(kBuildingFlying) && !noclip_takeoff(6), "NoClip's own conditions");

    check(climb(true, false, false) == kClimb && climb(false, true, false) == -kClimb && climb(true, true, false) == 0,
          "jump climbs, sneak sinks, both hold");
    check(climb(true, false, true) == kClimb * kFast && climb(false, false, true) == 0, "sprint is faster, alone it adds nothing");
    std::printf("flight math: %d failure(s)\n", failures);
    return failures ? EXIT_FAILURE : EXIT_SUCCESS;
}
