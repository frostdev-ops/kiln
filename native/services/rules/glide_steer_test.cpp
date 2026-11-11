// SPDX-License-Identifier: MIT
// Host test of the steered glider's math (glide_steer_math.h): native/services/rules/test-glide-math.sh builds and runs it.
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <limits>

#include "glide_steer_math.h"

using namespace kiln_glide;

namespace {

int g_fail = 0, g_pass = 0;

void check(bool ok, const char* what) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", what);
    (ok ? g_pass : g_fail)++;
}

constexpr float kD = 3.14159265358979f / 180.0f;
constexpr float kNaN = std::numeric_limits<float>::quiet_NaN();
constexpr float kInf = std::numeric_limits<float>::infinity();
bool near(float a, float b, float eps = 1e-5f) { return std::fabs(a - b) <= eps; }

// actor_rotation's glider pitch step as disassembled (server 0x8163b-0x818ed, docs/re/rules.md 7): the target by input
// mode, clamped to 5..80 degrees nose down (+ = nose down), then at most the max step toward it; a non-positive rate
// gives a step away from the target (or none), as the game computes it.
float game_step(float pitch, float z, uint8_t mode, float rate_deg, float dt, bool impulse_up = false) {
    const float max_step = rate_deg * kD * dt;
    float target = pitch;
    if (mode == 0) target = pitch + rate_deg * kD * z * dt;
    else if (mode == 1) target = z * 90.0f * kD;
    target = impulse_up ? 5.0f * kD : (target < 5.0f * kD ? 5.0f * kD : std::min(80.0f * kD, target));
    const float diff = target - pitch;
    if (!(std::fabs(diff) > max_step)) return diff;
    if (diff == 0.0f) return 0.0f;
    return diff < 0.0f ? -max_step : max_step;
}

// One step of the game with Kiln's rule at the hook (the game adds xmm6 to the pitch afterwards).
float fly(float pitch, float z, float rate, float dt, float limit = 85.0f, uint8_t mode = 0) {
    const float g = game_step(pitch, z, mode, rate, dt);
    return pitch + steer_step(g, pitch, rate * kD * dt, z, mode, limit);
}

Params creative() {
    Params p;
    p.thrust = 15, p.max_speed = 35, p.lift = 0, p.coast = 8, p.climb = 15, p.hold_altitude = true;
    return p;
}

// A step along a glider pitched `deg` up (negative: nose down), the game's velocity `game` along the nose, v0 `start`.
Step along(float deg, float start, float game, bool sprint, float dt = 1.0f / 60.0f) {
    Step s;
    const float c = std::cos(deg * kD), sn = std::sin(deg * kD);
    s.heading[0] = 0, s.heading[1] = sn, s.heading[2] = c;
    for (int i = 0; i < 3; ++i) s.v0[i] = s.heading[i] * start, s.v[i] = s.heading[i] * game;
    s.boost = sprint;
    s.dt = dt;
    return s;
}

float dot_f(const float* v, const Step& s) { return v[0] * s.heading[0] + v[1] * s.heading[1] + v[2] * s.heading[2]; }

}  // namespace

int main() {
    const float dt = 1.0f / 60.0f, rate = 85.0f, max = rate * kD * dt;  // T1's pitchAngleSpeed, 60 steps a second

    // ---- the step rule ----
    check(near(steer_step(game_step(5 * kD, -1, 0, rate, dt), 5 * kD, max, -1, 0, 85), -max),
          "S full: from the game's 5-degree floor the nose rises at the game's own max step");
    check(near(steer_step(game_step(5 * kD, -0.5f, 0, rate, dt), 5 * kD, max, -0.5f, 0, 85), -0.5f * max),
          "S half: an analog stick raises the nose at half the max step");
    check(near(steer_step(game_step(-30 * kD, -1, 0, rate, dt), -30 * kD, max, -1, 0, 85), -max),
          "S keeps raising a nose already above level (the game would level it out)");
    check(near(-84.5f * kD + steer_step(game_step(-84.5f * kD, -1, 0, rate, dt), -84.5f * kD, max, -1, 0, 85), -85 * kD),
          "the clamp: S stops exactly at the limit (85 degrees up)");
    check(near(steer_step(game_step(-85 * kD, -1, 0, rate, dt), -85 * kD, max, -1, 0, 85), 0),
          "at the limit S holds");
    check(near(-84.5f * kD + steer_step(game_step(-84.5f * kD, -1, 0, rate, dt), -84.5f * kD, max, -1, 0, 120), -85 * kD),
          "a limit over 85 degrees is held to 85: never across vertical");
    check(near(-40 * kD + steer_step(game_step(-40 * kD, -1, 0, rate, dt), -40 * kD, max, -1, 0, 30), -40 * kD + max),
          "a nose above a lowered limit comes back toward it at the max step");
    check(near(steer_step(game_step(-30 * kD, 0, 0, rate, dt), -30 * kD, max, 0, 0, 85), 0) &&
              game_step(-30 * kD, 0, 0, rate, dt) > 0,
          "centred above 5 degrees: the game's levelling step is dropped, the climb holds");
    check(near(steer_step(game_step(3 * kD, 0, 0, rate, dt), 3 * kD, max, 0, 0, 85), 0),
          "centred just above the game's floor: no levelling either");
    check(steer_step(game_step(30 * kD, 0, 0, rate, dt), 30 * kD, max, 0, 0, 85) == game_step(30 * kD, 0, 0, rate, dt),
          "centred below 5 degrees: the game's step stands");
    {
        const float g = game_step(30 * kD, 0, 0, rate, dt, true);  // the Impulse points up: the game targets 5 degrees
        check(g < 0 && steer_step(g, 30 * kD, max, 0, 0, 85) == g, "centred: the game's nose-up step (an impulse) stands");
    }
    check(steer_step(game_step(-30 * kD, 0.05f, 0, rate, dt), -30 * kD, max, 0.05f, 0, 85) == 0.0f,
          "a stick within 0.1 of centre is centred (analog drift does not level a climb)");
    check(steer_step(game_step(-30 * kD, 1, 0, rate, dt), -30 * kD, max, 1, 0, 85) == game_step(-30 * kD, 1, 0, rate, dt),
          "W: the game's step (back through level into the dive)");
    check(steer_step(game_step(40 * kD, 1, 0, rate, dt), 40 * kD, max, 1, 0, 85) == game_step(40 * kD, 1, 0, rate, dt),
          "W in a dive: the game's step");
    check(steer_step(game_step(20 * kD, -1, 1, rate, dt), 20 * kD, max, -1, 1, 85) == game_step(20 * kD, -1, 1, rate, dt),
          "input mode 1 (pitch to an angle): the game's step");
    check(steer_step(0.01f, 20 * kD, max, -1, 2, 85) == 0.01f, "an unknown input mode: the game's step");
    check(steer_step(0.0f, -30 * kD, 0.0f, -1, 0, 85) == 0.0f, "max step 0 (another mod holds the rate at 0): the game's step");
    check(steer_step(-0.02f, -30 * kD, -0.02f, -1, 0, 85) == -0.02f && steer_step(0.02f, -30 * kD, -0.02f, 1, 0, 85) == 0.02f,
          "a negative max step (SF Unlimited Flight owns the rate): the game's step");
    check(steer_step(0.01f, kNaN, max, -1, 0, 85) == 0.01f && steer_step(0.01f, 0, kNaN, -1, 0, 85) == 0.01f &&
              steer_step(0.01f, 0, max, kNaN, 0, 85) == 0.01f && steer_step(0.01f, 0, kInf, -1, 0, 85) == 0.01f &&
              steer_step(0.01f, 0, max, -1, 0, kNaN) == 0.01f && std::isnan(steer_step(kNaN, 0, max, -1, 0, 85)),
          "a non-finite value anywhere: the game's step untouched");
    check(near(0 + steer_step(game_step(0, -1, 0, rate, dt), 0, max, -1, 0, 0), 0) &&
              near(5 * kD + steer_step(game_step(5 * kD, -1, 0, rate, dt), 5 * kD, max, -1, 0, -10), 5 * kD - max),
          "limit 0: S rises to level and no further (a negative limit counts as 0)");

    // ---- runs at 30, 60 and 90 steps a second ----
    for (const float hz : {30.0f, 60.0f, 90.0f}) {
        const float h = 1.0f / hz;
        float p = 30 * kD, top = p;
        bool monotonic = true;
        int to_limit = -1;
        for (int i = 0; i < int(3 * hz); ++i) {  // 3 s of S from a 30-degree dive
            const float n = fly(p, -1, rate, h);
            monotonic = monotonic && n <= p + 1e-7f;
            p = n;
            top = std::min(top, p);
            if (to_limit < 0 && near(p, -85 * kD, 1e-4f)) to_limit = i + 1;
        }
        char what[160];
        std::snprintf(what, sizeof what, "%.0f Hz: S climbs steadily to the limit in %.2f s and never past it (top %.3f degrees)", hz,
                      to_limit / hz, top / kD);
        check(monotonic && top >= -85 * kD - 1e-5f && to_limit > 0 && std::fabs(to_limit / hz - 115.0f / rate) < 2.0f / hz, what);
        const float held = p;
        for (int i = 0; i < int(2 * hz); ++i) p = fly(p, 0, rate, h);
        std::snprintf(what, sizeof what, "%.0f Hz: centred for 2 s holds the climb (%.3f -> %.3f degrees)", hz, held / kD, p / kD);
        check(p == held, what);
        for (int i = 0; i < int(3 * hz); ++i) p = fly(p, 1, rate, h);
        std::snprintf(what, sizeof what, "%.0f Hz: W for 3 s goes back through level into the game's dive (%.3f degrees)", hz, p / kD);
        check(near(p, 80 * kD, 1e-4f), what);
        float q = 5 * kD;
        for (int i = 0; i < int(4 * hz); ++i) q = fly(q, -0.5f, rate, h);
        std::snprintf(what, sizeof what, "%.0f Hz: half S also stops at the limit (%.3f degrees)", hz, q / kD);
        check(q >= -85 * kD - 1e-5f && near(q, -85 * kD, 1e-4f), what);
    }

    // ---- GlideBoost while steering ----
    const Params p = creative();
    {
        Step s = along(20, 15, 14.9f, true);
        float out[3];
        const bool r = steer_boost(s, p, out);
        check(r && near(dot_f(out, s), 15 + 15 * dt, 1e-4f),
              "sprint: the speed along the nose rises by Thrust x dt from where the step started");
        const float side = out[0];
        check(side == 0 && near(out[1] / out[2], s.heading[1] / s.heading[2], 1e-5f), "the speed-up is along the glider's nose only");
    }
    {
        // the game's drag at speed: each step it takes 2% of the along-nose speed; sprint still reaches and holds the cap
        float v = 15;
        for (int i = 0; i < 240; ++i) {
            Step s = along(0, v, v * 0.98f, true);
            float out[3];
            steer_boost(s, p, out);
            v = dot_f(out, s);
        }
        check(near(v, 35, 1e-3f), "sprint sets the along-nose speed: it reaches and holds MaxSpeed against the game's quadratic drag");
    }
    {
        Step s = along(-40, 50, 48, true);
        float out[3];
        steer_boost(s, p, out);
        check(near(dot_f(out, s), 50 - 8 * dt, 1e-4f), "above MaxSpeed (a dive) sprint slows at most by Coast, never adds");
        s.boost = false;
        steer_boost(s, p, out);
        check(near(dot_f(out, s), 50 - 8 * dt, 1e-4f), "after letting go the glider slows at most by Coast");
        Params q = p;
        q.coast = 0;
        check(!steer_boost(s, q, out) && out[2] == s.v[2] && out[1] == s.v[1], "Coast 0: the game's drag stands");
    }
    {
        Step s = along(-60, 200, 199, false);
        float out[3];
        steer_boost(s, p, out);
        check(!steer_boost(s, p, out) && out[1] == s.v[1], "no speed is held above 2 x MaxSpeed (a long dive)");
        Step t = along(-60, 80, 60, false);
        steer_boost(t, p, out);
        check(near(dot_f(out, t), 70, 1e-4f), "a dive's coast is held at 2 x MaxSpeed at most");
    }
    {
        Step s = along(10, 20, 21, false);
        float out[3];
        check(!steer_boost(s, p, out) && out[2] == s.v[2], "the game faster than the start: the game's velocity stands");
    }
    {
        Step s = along(60, 17.5f, 17.2f, true);  // the game's own climb 14.9 m/s, sprint would make it 15.4
        float out[3];
        steer_boost(s, p, out);
        check(near(out[1], 15, 1e-4f), "sprint with the nose 60 degrees up: the climb rate is held to Climb");
        // a sustained sprint climb with the game's drag (2% a step) and gravity between steps settles at Climb
        float v0[3] = {0, 15 * 0.866f, 15 * 0.5f}, top = 0;
        for (int i = 0; i < 600; ++i) {
            Step c = along(60, 0, 0, true);
            for (int k = 0; k < 3; ++k) c.v0[k] = v0[k], c.v[k] = v0[k] * 0.98f;
            steer_boost(c, p, out);
            if (i > 300) top = std::max(top, out[1]);
            for (int k = 0; k < 3; ++k) v0[k] = out[k];
            v0[1] -= 9.81f * dt;
        }
        check(top <= 15.0f + 1e-4f && top > 14.0f, "a sustained sprint climb settles at Climb, not a runaway");
        Step t = along(60, 34, 33, true);
        t.v[1] = 20;  // the game's own climb is faster than Climb
        steer_boost(t, p, out);
        check(near(out[1], 20, 1e-4f), "Climb never cuts the game's own climb");
        Step u = along(60, 34, 33, false);
        steer_boost(u, p, out);
        check(out[1] > 15, "Climb applies while boosting only (a coasting zoom climb keeps its speed)");
    }
    {
        Step s = along(20, 15, 14.9f, true);
        Params q = p;
        q.lift = 50;
        float a[3], b[3];
        steer_boost(s, p, a);
        steer_boost(s, q, b);
        check(a[0] == b[0] && a[1] == b[1] && a[2] == b[2], "Lift is ignored while steering");
    }
    {
        Step s = along(20, 15, 14.9f, true);
        s.v0[1] = kNaN;
        float out[3];
        check(!steer_boost(s, p, out) && out[2] == s.v[2], "a non-finite start: the game's velocity");
        s = along(20, 15, 14.9f, true);
        s.heading[0] = kInf;
        check(!steer_boost(s, p, out), "a non-finite heading: the game's velocity");
        s = along(20, 15, 14.9f, true);
        s.dt = 0;
        check(!steer_boost(s, p, out), "no step time: the game's velocity");
        s = along(20, 15, 14.9f, false);
        Params none;
        check(!steer_boost(s, none, out), "no thrust and no coast: the game's velocity");
    }
    std::printf("glide steer math: %d PASS, %d FAIL\n", g_pass, g_fail);
    return g_fail ? EXIT_FAILURE : EXIT_SUCCESS;
}
