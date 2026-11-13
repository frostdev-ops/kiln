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
//
// NoClip (KILN_RULES_NOCLIP; noclip.cpp skips the collision step of a character in this flight) is the same flight with
// three differences: it takes off at once, from the ground and from water too (a character with no collision cannot
// walk: it would fall through the world); water does not end it; and it never lands, because without the collision
// step there is no ground to stand on. Death, the glider, a ladder or a wall end it, and the collision step comes back
// with the same decision.
#pragma once
#include <cstdint>

namespace flight_math {

constexpr uint8_t kWalking = 0, kSliding = 1, kFalling = 2, kSwimming = 9, kDiving = 10, kBuildingFlying = 11;
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

// NoClip allows its flight now: everywhere, water included.
inline bool allowed_noclip(bool noclip, const Actor& a) { return noclip && !a.dead && !a.gliding && !a.climbing; }

// The game states NoClip takes off from: every state in which the character's own body moves it. Not the glider, the
// hook and the climbing states (the game is moving the character along something), and not the game's own Flying.
inline bool noclip_takeoff(uint8_t game) {
    return game == kWalking || game == kSliding || game == kFalling || game == kSwimming || game == kDiving;
}

// The state after the game's decision `game` for an entity in state `cur`. `ours`: the entity has rules (a spark never
// has) or Kiln owns its flight (its rules just ended). `input_vy`: DynamicLocomotion.inputVelocity.y (the sink).
// `noclip`: the entity's rules carry NoClip (and Kiln's flight is not idle).
inline uint8_t next_state(uint8_t cur, uint8_t game, bool ours, bool armed, bool base_only, const Actor& a, float input_vy,
                          bool noclip = false) {
    const bool clip = allowed_noclip(noclip, a);
    const bool ok = clip || allowed(armed, base_only, a);
    if (cur == kBuildingFlying) {
        if (game != kBuildingFlying || !ours) return game;  // not sticky (another build) or the building spark
        if (!ok) return kFalling;
        if (!clip && a.grounded && input_vy < 0.0f) return kWalking;  // landed (NoClip has no ground)
        return kBuildingFlying;
    }
    if (ours && clip && noclip_takeoff(game)) return kBuildingFlying;
    if (ours && ok && game == kFalling && !a.grounded) return kBuildingFlying;
    return game;
}

// DynamicLocomotion.inputVelocity.y while flying.
inline float climb(bool jump, bool sneak, bool sprint) {
    const float v = (jump ? kClimb : 0.0f) - (sneak ? kClimb : 0.0f);
    return sprint ? v * kFast : v;
}

}  // namespace flight_math
