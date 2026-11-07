// SPDX-License-Identifier: MIT
// The boosted glider's step, "like a plane" (docs/kiln/services/rules.md, "Gliding"). Plain math: no Windows, no game
// memory, so glide_math_test.cpp runs it on the build machine (native/services/rules/test-glide-math.sh).
//
// After the game's own glide step (its thrust along the glider's heading and its drag), a player with GLIDE_BOOST flies
// along a path that keeps the glider's heading (the game turns it as it always does) and takes its pitch from the
// movement stick, like a plane's stick (the mouse stays free: the camera never pitches the path):
//  - holding back (S: the stick's forward axis below 0) pitches up toward kMaxClimbPitch, holding forward (W) pitches
//    down toward kMaxDivePitch, neither returns to level (the altitude holds); an analog stick pitches in proportion;
//  - the pitch moves toward that target at kPitchRate at most, from the pitch of the velocity the step starts with (the
//    replicated state the server and the player's prediction share, so no per-player ramp state exists);
//  - on a build without the stick (Step.has_stick false) the pitch comes from the camera as before (stick_pitch is not
//    used, path_pitch is);
//  - the speed along the path changes by gravity's share of it (-g x sin pitch): climbing costs speed, diving gives it
//    (an energy trade), plus the thrust while sprint is held (up to max_speed; a dive's speed stays), minus the coast
//    while faster than the path's own glide speed (the game's when level or climbing, more in a dive: up to
//    kDiveFactor x max_speed straight down);
//  - camera fallback only: a view within kLevelBand of level is level flight (the altitude holds);
//  - the climb rate never passes `climb` (0: the path never climbs);
//  - once the path is no faster than the game's own glide (letting go, or a climb that ran out of speed), the game's
//    glide stands again: releasing everything settles into a normal glide.
#pragma once

#include <algorithm>
#include <cmath>

namespace kiln_glide {

struct Params {
    float thrust = 0.0f;     // m/s^2 along the path while sprint is held
    float max_speed = 0.0f;  // m/s: the thrust's cap (a dive goes on to kDiveFactor x this)
    float lift = 0.0f;       // m/s^2 upward while sprint is held
    float coast = 0.0f;      // m/s^2: without sprint, how fast a faster-than-the-game glide slows (0 = the game's drag)
    float climb = 0.0f;      // m/s: the highest climb rate (0 = never climbs)
    bool hold_altitude = false;  // glide sink 0: the game's own glide does not sink while the glider is not pointing down
};

struct Step {
    float v0[3] = {};       // velocity before the game's glide step
    float v[3] = {};        // after it
    float heading[3] = {};  // the glider's forward (unit)
    float view[3] = {};     // the camera's forward, or all 0 when unknown (the heading's pitch then)
    float stick[2] = {};    // the movement stick (x right, y forward: W +1, S -1), when has_stick
    bool has_stick = false;  // the stick is known on this build: it sets the pitch (else the camera does)
    float dt = 0.0f;        // s
    bool boost = false;     // sprint held
};

enum class Result { Game, Plane };

constexpr float kGravity = 9.81f;         // m/s^2, for the energy trade
constexpr float kLevelBand = 0.08f;       // |sin pitch| of the view below this: level flight
constexpr float kMaxClimbPitch = 0.866f;  // the path climbs at 60 degrees at most
constexpr float kDiveFactor = 2.0f;       // a dive without the thrust gains up to this x max_speed
constexpr float kSpeedLimit = 120.0f;     // m/s, any path speed
constexpr float kMaxDivePitch = 0.866f;   // stick: the path dives at 60 degrees at most
constexpr float kPitchRate = 0.8f;        // stick: rad/s, how fast the path's pitch follows the stick (60 deg in 1.3 s)
constexpr float kStickDeadZone = 0.2f;    // stick: |forward axis| below this is centred (an analog stick's drift)

inline float len3(const float* a) { return std::sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]); }
inline bool finite3(const float* a) { return std::isfinite(a[0]) && std::isfinite(a[1]) && std::isfinite(a[2]); }

// The path's pitch (sine) from the view: level within the band, the rest stretched so it stays continuous; never
// steeper upward than kMaxClimbPitch.
inline float path_pitch(const Step& s) {
    const float vl = len3(s.view);
    float y = vl > 0.5f && vl < 2.0f ? s.view[1] / vl : s.heading[1];
    if (!std::isfinite(y)) return 0.0f;
    y = std::clamp(y, -1.0f, 1.0f);
    const float a = std::fabs(y);
    if (a <= kLevelBand) return 0.0f;
    y = std::copysign((a - kLevelBand) / (1.0f - kLevelBand), y);
    return std::min(y, kMaxClimbPitch);
}

// The path's pitch (sine) from the stick: the velocity's pitch at the start of the step moved toward the stick's target
// by kPitchRate x dt at most. `lift` is the boost's upward push the last step added on top of its path (lift x dt while
// boosting), taken off again so it does not count as pitch. Non-finite stick values count as centred.
inline float stick_pitch(const Step& s, float lift) {
    float y = s.stick[1];
    if (!std::isfinite(y) || !std::isfinite(s.stick[0])) y = 0.0f;
    y = std::clamp(y, -1.0f, 1.0f);
    const float a = std::fabs(y);
    const float k = a <= kStickDeadZone ? 0.0f : (a - kStickDeadZone) / (1.0f - kStickDeadZone);
    const float max_up = std::asin(kMaxClimbPitch), max_down = std::asin(kMaxDivePitch);
    const float target = y < 0.0f ? k * max_up : -k * max_down;  // back (S) climbs, forward (W) dives
    float cur = 0.0f;
    const float sp = len3(s.v0);
    if (sp > 1e-3f && std::isfinite(sp)) {
        float vy = s.v0[1];
        if (s.boost && lift > 0.0f && std::isfinite(lift)) vy -= lift * s.dt;
        cur = std::asin(std::clamp(vy / sp, -1.0f, 1.0f));
    }
    const float step = kPitchRate * std::min(s.dt, 1.0f);
    const float next = cur + std::clamp(target - cur, -step, step);
    if (!std::isfinite(next)) return 0.0f;
    return std::min(std::sin(next), kMaxClimbPitch);
}

// The game's glide, with the altitude hold when asked (as before the plane: unless the glider points down).
inline Result game_glide(const Step& s, const Params& p, float out[3]) {
    out[0] = s.v[0], out[1] = s.v[1], out[2] = s.v[2];
    if (p.hold_altitude && s.heading[1] > -0.2f && out[1] < 0.0f) out[1] = 0.0f;
    return Result::Game;
}

// out = the velocity to use. Non-finite input never builds a path (the game's velocity stands).
inline Result plane_step(const Step& s, const Params& p, float out[3]) {
    if (!finite3(s.v0) || !finite3(s.v) || !finite3(s.heading) || !(s.dt > 0.0f) || !std::isfinite(s.dt))
        return game_glide(s, p, out);
    if (!s.boost && !(p.coast > 0.0f)) return game_glide(s, p, out);  // coast 0: the game's drag after a boost
    // horizontal direction: the glider's heading (else the view's, else the motion's)
    float hx = s.heading[0], hz = s.heading[2];
    float hl = std::sqrt(hx * hx + hz * hz);
    if (hl < 1e-3f) hx = s.view[0], hz = s.view[2], hl = std::sqrt(hx * hx + hz * hz);
    if (hl < 1e-3f || !std::isfinite(hl)) hx = s.v0[0], hz = s.v0[2], hl = std::sqrt(hx * hx + hz * hz);
    if (hl < 1e-3f || !std::isfinite(hl)) hx = 0.0f, hz = 1.0f, hl = 1.0f;
    hx /= hl, hz /= hl;
    const float sp = s.has_stick ? stick_pitch(s, p.lift) : path_pitch(s);
    const float c = std::sqrt(std::max(0.0f, 1.0f - sp * sp));
    // the game's own glide speed: its horizontal speed after its step (its thrust and drag set that; the vertical part
    // is ours from the last step, or the game's sink)
    const float s0 = len3(s.v0), sg = std::sqrt(s.v[0] * s.v[0] + s.v[2] * s.v[2]);
    const float cap = std::max(0.0f, p.max_speed);
    const float dive_top = std::min(kSpeedLimit, std::max(cap, kDiveFactor * cap));
    // the coast slows only what is over this path's own glide speed: the game's when level or climbing, more the steeper
    // the dive (up to dive_top straight down), so a dive gains speed and settles at a speed for its angle
    const float settle = sp < 0.0f ? sg + (dive_top - sg) * -sp : sg;
    float a = -kGravity * sp;
    if (s.boost) a += std::max(0.0f, p.thrust);
    else if (s0 > settle) a -= p.coast;
    float speed = s0 + a * s.dt;
    if (s.boost) speed = std::min(speed, std::max(s0, cap));  // the thrust only up to its cap; a dive's speed stays
    speed = std::min(speed, dive_top);
    if (!(speed > sg)) return game_glide(s, p, out);  // no faster than the game's own glide: the game glides
    out[0] = hx * c * speed;
    out[1] = sp * speed;
    out[2] = hz * c * speed;
    if (s.boost && p.lift > 0.0f) out[1] += p.lift * s.dt;
    out[1] = std::min(out[1], std::max(0.0f, p.climb));
    if (!finite3(out)) return game_glide(s, p, out);
    // Lift changes the speed as well as the climb rate. Apply the speed budget to the final vector too, keeping
    // speed already earned in a dive but never letting the boost (including lift) add speed beyond its cap.
    const float limit = s.boost ? std::min(dive_top, std::max(s0, cap)) : dive_top;
    const float final_speed = len3(out);
    if (!std::isfinite(final_speed)) return game_glide(s, p, out);
    if (final_speed > limit) {
        const float scale = limit / final_speed;
        for (int i = 0; i < 3; ++i) out[i] *= scale;
    }
    return Result::Plane;
}

}  // namespace kiln_glide
