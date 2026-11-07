// SPDX-License-Identifier: MIT
// Plain decisions of kiln.rules' character flight (flight.cpp), kept apart so the host tests check them without a game
// (docs/re/creative-mode-146.md Part 3 section 6, Part 4).
//
// The character flies in the game's own BuildingFlying locomotion state (11), the building spark's: the state program
// keeps 11 once it is set (LocoNextState returns 11 first of all), and the BuildingFlying mover steers Velocity toward
// DynamicLocomotion.inputVelocity in three dimensions. Kiln decides, after the game's own decision each step:
//  - take-off: armed (the rule, and the server confirmed a real altar zone for "building area only"), airborne (not Grounded), the
//    game says Falling, not dead, gliding, swimming or climbing: BuildingFlying;
//  - while flying: the same conditions keep it; when they fail (the rule ended, the zone left, death, the glider,
//    water) the character falls (Falling: the game decides from there);
//  - landing: grounded while sinking (the player holds the sneak key): Walking;
//  - a BuildingFlying entity Kiln did not put there (the building spark) is left alone.
// The vertical input: Jump climbs, Sneak sinks, Sprint makes both faster; the horizontal part stays the game's.
#pragma once
#include <cstdint>

namespace flight_math {

constexpr uint8_t kWalking = 0, kFalling = 2, kBuildingFlying = 11;
constexpr float kClimb = 8.0f;  // m/s up or down
constexpr float kFast = 2.5f;   // x with sprint held

// The actor state flags flight looks at (keen::actor::StateFlag).
struct Actor {
    bool grounded = false;   // Grounded 0
    bool dead = false;       // Dead 7
    bool gliding = false;    // HangGliding 37
    bool water = false;      // FloatingOnWater 43, UnderWater 44
    bool climbing = false;   // Climbing 34, Attached 36
    bool near_zone = false;  // IsNearBuildingZone 54
};

// The rule allows flight now.
inline bool allowed(bool armed, bool base_only, const Actor& a) {
    // The server resolves BuildingArea against actual altar bounds and sends an ordinary flight bit on both sides.
    // A raw base-only request cannot safely use IsNearBuildingZone (build-anywhere forces that actor flag).
    return armed && !base_only && !a.dead && !a.gliding && !a.water && !a.climbing;
}

// The state after the game's decision `game` for an entity in state `cur`. `ours`: the entity has rules (a spark never
// has) or Kiln owns its flight (its rules just ended). `input_vy`: DynamicLocomotion.inputVelocity.y (the sink).
inline uint8_t next_state(uint8_t cur, uint8_t game, bool ours, bool armed, bool base_only, const Actor& a, float input_vy) {
    const bool ok = allowed(armed, base_only, a);
    if (cur == kBuildingFlying) {
        if (game != kBuildingFlying || !ours) return game;  // not sticky (another build) or the building spark
        if (!ok) return kFalling;
        if (a.grounded && input_vy < 0.0f) return kWalking;  // landed
        return kBuildingFlying;
    }
    if (ours && ok && game == kFalling && !a.grounded) return kBuildingFlying;
    return game;
}

// DynamicLocomotion.inputVelocity.y while flying.
inline float climb(bool jump, bool sneak, bool sprint) {
    const float v = (jump ? kClimb : 0.0f) - (sneak ? kClimb : 0.0f);
    return sprint ? v * kFast : v;
}

}  // namespace flight_math
