// SPDX-License-Identifier: MIT
// The steered glider (GLIDE_STEER; docs/kiln/services/rules.md, "Gliding"; docs/re/rules.md 7). Plain math: no Windows,
// no game memory, so glide_steer_test.cpp runs it on the build machine (native/services/rules/test-glide-math.sh).
//
// The game turns the glider in actor_rotation: each step it measures the pitch from the transform (+ = nose down,
// -90..90 degrees), builds a target (input mode 0: pitch + pitchAngleSpeed x z x dt, z the stick's forward axis: W +1,
// S -1), clamps it to 5..80 degrees nose down and moves at most the max step (pitchAngleSpeed x dt) toward it. Kiln's
// mid hook replaces that step for a player with GLIDE_STEER (steer_step):
//  - S raises the nose at up to the game's own max step, scaled by the stick, and never past the limit (degrees above
//    level, at most kMaxSteerLimit: never across vertical, where the game's pitch measure folds);
//  - a centred stick (|z| <= kSteerCentre) holds a climb: the game's levelling step toward 5 degrees is dropped while
//    the nose is above it; the rest of the game's step stands;
//  - W keeps the game's step (back through level into its dive), from a climb at most the stick's share of the max step
//    (the game would take all of it for any forward stick there);
//  - a nose above the limit (the limit was lowered) comes back toward it at the max step;
//  - input mode other than 0, any non-finite value, or a max step of 0 or less (another mod owns the rate: SF
//    Unlimited Flight writes a negative or zero pitchAngleSpeed) keeps the game's step untouched.
// The rule has no state: the same pitch, input and rules give the same step in the server world and the player's
// prediction.
//
// GlideBoost while steering (steer_boost): the game's own thrust along the nose and its drag already follow the real
// pitch, so there is no plane path and no energy trade. Kiln only sets the speed along the glider's forward F after the
// game's step (an added acceleration would be eaten by the game's quadratic drag):
//  - sprint held: the along-nose speed rises by Thrust x dt from the larger of the step's start and the game's, up to
//    MaxSpeed;
//  - an along-nose speed above the game's (after a sprint, or a dive's) falls by at most Coast x dt, and is held at
//    most at kDiveFactor x MaxSpeed (Coast 0: the game's drag);
//  - while sprint is held the climb rate stays at most at max(Climb, the game's own climb rate);
//  - Lift is not used; Kiln never makes the glider slower than the game's step does.
#pragma once

#include <cstdint>

#include "glide_math.h"

namespace kiln_glide {

constexpr float kRad = 3.14159265358979f / 180.0f;
constexpr float kGameTopPitch = 5.0f * kRad;  // the game's pitch target never goes above this (nose 5 degrees down)
constexpr float kMaxSteerLimit = 85.0f;       // degrees above level: the highest limit any rule may set
constexpr float kSteerCentre = 0.1f;          // |stick forward axis| up to this is a centred stick

// The input mode steer_step steers (PlayerInput.fromClient.movementStickInputMode 0, YawSpeedPitchSpeed). The glide
// wrap's steering form (rules.cpp glide_pre, steer.cpp steers) asks the same, of the same replicated byte: in any other
// mode the pitch stays the game's, so the glide must too (GlideBoost's plane path, sink 0's altitude hold), on the
// server and in the player's prediction alike.
constexpr bool steers_mode(uint8_t mode) { return mode == 0; }

// The pitch step to use (rad, + = nose down) for actor_rotation's step `game_step`. pitch: the pitch measured this step;
// max_step: the game's bound (pitchAngleSpeed x deg2rad x dt); z: ActorInput.desiredLocalMoveInput.z; mode:
// PlayerInput.fromClient.movementStickInputMode; limit_deg: how far above level the nose may rise.
inline float steer_step(float game_step, float pitch, float max_step, float z, uint8_t mode, float limit_deg) {
    if (!steers_mode(mode) || !std::isfinite(game_step) || !std::isfinite(pitch) || !std::isfinite(max_step) || !std::isfinite(z) ||
        !std::isfinite(limit_deg) || !(max_step > 0.0f))
        return game_step;
    const float top = -std::clamp(limit_deg, 0.0f, kMaxSteerLimit) * kRad;  // the highest nose allowed (pitch)
    float step = game_step;
    if (z < -kSteerCentre) {
        // S: up at the stick's share of the max step, to the limit at most
        step = -std::min(max_step * std::min(-z, 1.0f), std::max(0.0f, pitch - top));
    } else if (z <= kSteerCentre) {
        // centred: no levelling while the nose is above the game's top
        if (pitch < kGameTopPitch && game_step > 0.0f) step = 0.0f;
    } else if (pitch < kGameTopPitch) {
        // W from a climb: the game aims at its top, far below, and would take its full step for any stick; at most the
        // stick's share, as S (a full stick: the game's step exactly)
        step = std::min(game_step, max_step * std::min(z, 1.0f));
    }
    // never above the limit: back toward it (a lowered limit), at the max step
    if (pitch + step < top) step = std::min(max_step, top - pitch);
    return std::isfinite(step) ? step : game_step;
}

// out = the velocity to use; true when it is Kiln's (false: the game's velocity, copied to out).
inline bool steer_boost(const Step& s, const Params& p, float out[3]) {
    out[0] = s.v[0], out[1] = s.v[1], out[2] = s.v[2];
    if (!finite3(s.v0) || !finite3(s.v) || !finite3(s.heading) || !(s.dt > 0.0f) || !std::isfinite(s.dt)) return false;
    const float hl = len3(s.heading);
    if (!(hl > 0.5f && hl < 2.0f)) return false;
    const float F[3] = {s.heading[0] / hl, s.heading[1] / hl, s.heading[2] / hl};
    const float dt = std::min(s.dt, kMaxStep);
    const float a0 = s.v0[0] * F[0] + s.v0[1] * F[1] + s.v0[2] * F[2];  // along the nose at the step's start
    const float ag = s.v[0] * F[0] + s.v[1] * F[1] + s.v[2] * F[2];     // and after the game's step
    const float cap = std::isfinite(p.max_speed) ? std::clamp(p.max_speed, 0.0f, kSpeedLimit) : 0.0f;
    const float top = std::min(kSpeedLimit, std::max(cap, kDiveFactor * cap));
    float want = ag;
    if (p.coast > 0.0f && std::isfinite(p.coast) && a0 > ag) want = std::max(want, std::min(a0 - p.coast * dt, top));
    if (s.boost && p.thrust > 0.0f && std::isfinite(p.thrust) && cap > 0.0f)
        want = std::max(want, std::min(std::max(a0, ag) + p.thrust * dt, cap));
    if (!(want > ag)) return false;
    const float add = want - ag;
    float v[3] = {s.v[0] + add * F[0], s.v[1] + add * F[1], s.v[2] + add * F[2]};
    if (s.boost) v[1] = std::min(v[1], std::max(std::isfinite(p.climb) ? std::max(p.climb, 0.0f) : 0.0f, s.v[1]));
    if (!finite3(v)) return false;
    out[0] = v[0], out[1] = v[1], out[2] = v[2];
    return true;
}

}  // namespace kiln_glide
