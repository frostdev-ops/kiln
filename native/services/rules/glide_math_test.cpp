// SPDX-License-Identifier: MIT
// Host test of the boosted glider's math (glide_math.h): native/services/rules/test-glide-math.sh builds and runs it.
#include <cstdio>
#include <cstdlib>

#include "glide_math.h"

using namespace kiln_glide;

namespace {

int g_fail = 0, g_pass = 0;

void check(bool ok, const char* what) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", what);
    (ok ? g_pass : g_fail)++;
}

// Creative's preset: thrust 15, max 35, lift 0, coast 8, climb 15, sink 0.
Params creative() {
    Params p;
    p.thrust = 15, p.max_speed = 35, p.lift = 0, p.coast = 8, p.climb = 15, p.hold_altitude = true;
    return p;
}

// A view pitched by `deg` (positive up) along +z.
void view_at(Step& s, float deg) {
    const float r = deg * 3.14159265f / 180.0f;
    s.view[0] = 0, s.view[1] = std::sin(r), s.view[2] = std::cos(r);
}

// The game's glide reduced to its essentials for the loop: whatever came in, it pulls the speed toward 11 m/s along
// the heading (its thrust and drag), and with sink 0 no gravity.
void game(Step& s, const float* v0) {
    for (int i = 0; i < 3; ++i) s.v0[i] = v0[i];
    const float sp = len3(v0);
    const float k = sp > 11.0f ? 0.5f : 1.0f;  // the game's drag cuts a fast glide hard
    const float target = sp > 11.0f ? sp - (sp - 11.0f) * k : 11.0f;
    s.v[0] = s.heading[0] * target, s.v[1] = v0[1] * 0.5f, s.v[2] = s.heading[2] * target;
}

// Runs `seconds` of 60 Hz steps from velocity v; returns the climbed height and leaves v as the end velocity.
float fly(float* v, float seconds, float view_deg, bool boost, const Params& p, float* max_climb = nullptr, int* plane = nullptr) {
    Step s;
    s.heading[0] = 0, s.heading[1] = 0, s.heading[2] = 1;
    s.dt = 1.0f / 60.0f;
    s.boost = boost;
    view_at(s, view_deg);
    float h = 0;
    for (int i = 0; i < static_cast<int>(seconds * 60); ++i) {
        game(s, v);
        float out[3];
        const Result r = plane_step(s, p, out);
        if (plane && r == Result::Plane) ++*plane;
        for (int k = 0; k < 3; ++k) v[k] = out[k];
        if (max_climb && v[1] > *max_climb) *max_climb = v[1];
        h += v[1] * s.dt;
    }
    return h;
}

}  // namespace

int main() {
    const Params p = creative();
    {
        // boost while looking 30 degrees up: climbs, no faster than `climb`, speed up to the cap
        float v[3] = {0, 0, 11};
        float top = 0;
        const float h = fly(v, 6, 30, true, p, &top);
        check(h > 30.0f, "boost + pitch up climbs (more than 30 m in 6 s)");
        check(top <= p.climb + 1e-3f, "the climb rate never passes `climb` (15 m/s)");
        check(len3(v) <= p.max_speed + 0.5f, "the boost stops at max_speed (35 m/s)");
    }
    {
        // boost looking level: altitude holds, speed to the cap
        float v[3] = {0, 0, 11};
        const float h = fly(v, 5, 2, true, p);
        check(std::fabs(h) < 0.01f && std::fabs(v[1]) < 1e-4f, "boost looking level (within the band) holds the altitude");
        check(std::fabs(len3(v) - p.max_speed) < 0.5f, "boost looking level reaches max_speed");
    }
    {
        // climb 0: boosting while looking up never climbs
        Params q = p;
        q.climb = 0;
        float v[3] = {0, 0, 11};
        float top = 0;
        fly(v, 4, 45, true, q, &top);
        check(top <= 0.0f, "climb 0: never climbs");
    }
    {
        // energy trade: from 35 m/s level, pitch up without boost: climbs while slowing, then the game's glide again
        float v[3] = {0, 0, 35};
        int plane = 0;
        const float h = fly(v, 8, 30, false, p, nullptr, &plane);
        check(h > 5.0f, "no boost, pitch up from 35 m/s: trades speed for height (climbs more than 5 m)");
        check(len3(v) <= 11.5f, "the climb runs out of speed and settles into the game's glide (about 11 m/s)");
        check(plane > 0 && plane < 8 * 60, "the plane path ends by itself (the game's glide stands again)");
    }
    {
        // energy trade: pitch down without boost gains speed and loses height
        float v[3] = {0, 0, 11};
        const float h = fly(v, 3, -30, false, p);
        check(h < -10.0f && len3(v) > 20.0f, "no boost, pitch down: trades height for speed");
        check(len3(v) <= kDiveFactor * p.max_speed + 0.5f, "a dive stops at 2 x max_speed");
    }
    {
        // a long shallow dive settles at a speed for its angle (the coast against gravity's share), not at the cap
        float v[3] = {0, 0, 11};
        fly(v, 29, -30, false, p);
        const float at29 = len3(v);
        fly(v, 1, -30, false, p);
        std::printf("     30-degree dive: %.1f m/s after 29 s, %.1f after 30 s\n", at29, len3(v));
        check(std::fabs(len3(v) - at29) < 0.5f && len3(v) > 20.0f && len3(v) < kDiveFactor * p.max_speed - 5.0f,
              "a 30-degree dive settles at a speed of its own (over 20 m/s, under the 70 m/s cap)");
    }
    {
        // releasing everything at speed, looking level: slows by the coast, holds altitude, then a normal glide
        float v[3] = {0, 0, 35};
        const float h = fly(v, 6, 0, false, p);
        check(std::fabs(h) < 0.01f, "letting go looking level holds the altitude");
        check(len3(v) <= 11.5f, "letting go settles into the game's glide");
    }
    {
        // a normal glide (no boost, game speed) looking up does not climb: no spare energy
        float v[3] = {0, 0, 11};
        float top = 0;
        fly(v, 3, 40, false, p, &top);
        check(top <= 1e-3f, "a plain glide looking up does not climb");
    }
    {
        // guards: non-finite input never builds a path; the speed never passes the limit
        Step s;
        s.heading[2] = 1, s.dt = 1.0f / 60.0f, s.boost = true;
        s.v0[2] = NAN, s.v[2] = 11;
        float out[3];
        check(plane_step(s, p, out) == Result::Game && out[2] == 11.0f, "a non-finite velocity: the game's stands");
        Params big = p;
        big.max_speed = 1e9f, big.thrust = 1e9f;
        s.v0[2] = 11;
        plane_step(s, big, out);
        check(finite3(out) && len3(out) <= kSpeedLimit + 1e-3f, "no path speed over 120 m/s, whatever the values");
        s.dt = NAN;
        check(plane_step(s, p, out) == Result::Game, "a non-finite step time: the game's stands");
        Step d;  // view unknown: the heading's pitch
        d.heading[2] = 1, d.dt = 1.0f / 60.0f, d.boost = true, d.v0[2] = 11, d.v[2] = 11;
        check(plane_step(d, p, out) == Result::Plane && std::fabs(out[1]) < 1e-6f, "no view: level along the heading");
    }
    {
        // the game's glide with sink 0 keeps the altitude unless the glider points down
        Step s;
        s.heading[2] = 1, s.dt = 1.0f / 60.0f, s.v[1] = -2, s.v[2] = 11, s.v0[2] = 11;
        Params q = p;
        q.coast = 0;
        float out[3];
        plane_step(s, q, out);
        check(out[1] == 0.0f, "the game's glide, sink 0: no sinking");
        s.heading[1] = -0.5f;
        plane_step(s, q, out);
        check(out[1] == -2.0f, "the game's glide pointing down still descends");
    }
    std::printf("glide math: %d PASS, %d FAIL\n", g_pass, g_fail);
    return g_fail ? EXIT_FAILURE : EXIT_SUCCESS;
}
