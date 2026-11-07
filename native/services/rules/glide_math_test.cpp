// SPDX-License-Identifier: MIT
// Host test of the boosted glider's math (glide_math.h): native/services/rules/test-glide-math.sh builds and runs it.
#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <limits>

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

// The stick for a flight: none (the camera fallback: Step.has_stick false) or its forward axis (S -1, W +1).
struct Stick {
    bool known = false;
    float y = 0.0f;
};
constexpr Stick kCamera{};
constexpr Stick kCentred{true, 0.0f}, kBack{true, -1.0f}, kForward{true, 1.0f};

// The path's pitch angle (rad) of a velocity.
float pitch_of(const float* v) {
    const float n = len3(v);
    return n > 0 ? std::asin(std::clamp(v[1] / n, -1.0f, 1.0f)) : 0.0f;
}

struct Track {
    float max_climb = -1e9f, max_pitch = -10, min_pitch = 10, max_turn = 0;  // turn: largest pitch change per step
    int plane = 0;
};

// Runs `seconds` of 60 Hz steps from velocity v; returns the climbed height and leaves v as the end velocity.
float fly(float* v, float seconds, float view_deg, bool boost, const Params& p, float* max_climb = nullptr, int* plane = nullptr,
          Stick stick = kCamera, Track* t = nullptr) {
    Step s;
    s.heading[0] = 0, s.heading[1] = 0, s.heading[2] = 1;
    s.dt = 1.0f / 60.0f;
    s.boost = boost;
    s.has_stick = stick.known;
    s.stick[1] = stick.y;
    view_at(s, view_deg);
    float h = 0;
    for (int i = 0; i < static_cast<int>(seconds * 60); ++i) {
        game(s, v);
        float out[3];
        const Result r = plane_step(s, p, out);
        if (plane && r == Result::Plane) ++*plane;
        if (t) {
            if (r == Result::Plane) ++t->plane;
            const float a = pitch_of(out), b = pitch_of(v);
            if (r == Result::Plane && len3(v) > 1e-3f) t->max_turn = std::max(t->max_turn, std::fabs(a - b));
            t->max_climb = std::max(t->max_climb, out[1]);
            if (r == Result::Plane) t->max_pitch = std::max(t->max_pitch, a), t->min_pitch = std::min(t->min_pitch, a);
        }
        for (int k = 0; k < 3; ++k) v[k] = out[k];
        if (max_climb && v[1] > *max_climb) *max_climb = v[1];
        h += v[1] * s.dt;
    }
    return h;
}

}  // namespace

int main() {
    const Params p = creative();
    const float dt = 1.0f / 60.0f;
    const float rad60 = std::asin(kMaxClimbPitch);
    // ---------------------------------------------------------------- the stick sets the pitch (S up, W down)
    {
        // boost + hold S: climbs, no faster than `climb`, never steeper than 60 degrees
        float v[3] = {0, 0, 11};
        Track t;
        const float h = fly(v, 6, 0, true, p, nullptr, nullptr, kBack, &t);
        check(h > 30.0f, "stick: boost + hold S climbs (more than 30 m in 6 s)");
        check(t.max_climb <= p.climb + 1e-3f, "stick: the climb rate never passes `climb` (15 m/s)");
        check(t.max_pitch <= rad60 + 1e-3f, "stick: holding S never climbs steeper than 60 degrees");
        // release: back to level within a second or two, then the altitude holds
        fly(v, 2, 0, true, p, nullptr, nullptr, kCentred);
        check(std::fabs(v[1]) < 1e-4f, "stick: releasing S returns to level flight");
        const float hold = fly(v, 3, 0, true, p, nullptr, nullptr, kCentred);
        check(std::fabs(hold) < 0.01f, "stick: level after the release holds the altitude");
    }
    {
        // with a climb cap out of the way the path reaches the 60-degree cap, at kPitchRate per step at most, and the
        // release ramps back down at the same rate (a plane's stick, not a snap)
        Params q = p;
        q.climb = 120, q.max_speed = 60;
        float v[3] = {0, 0, 40};
        Track up;
        fly(v, 0.5f, 0, true, q, nullptr, nullptr, kBack, &up);
        check(up.max_pitch > 0.3f && up.max_pitch < rad60 - 0.2f, "stick: half a second of S is part of the way up (a ramp, not a snap)");
        fly(v, 1.5f, 0, true, q, nullptr, nullptr, kBack, &up);
        check(std::fabs(pitch_of(v) - rad60) < 1e-3f, "stick: holding S reaches the 60-degree cap and stays there");
        check(up.max_turn <= kPitchRate * dt + 1e-4f, "stick: the pitch never moves faster than kPitchRate (climbing)");
        Track down;
        fly(v, 0.5f, 0, true, q, nullptr, nullptr, kCentred, &down);
        check(pitch_of(v) > 0.3f, "stick: half a second after the release the path is still coming down (a ramp)");
        fly(v, 1.5f, 0, true, q, nullptr, nullptr, kCentred, &down);
        check(std::fabs(v[1]) < 1e-4f && down.max_turn <= kPitchRate * dt + 1e-4f,
              "stick: the release levels out at kPitchRate at most, then flies level");
    }
    {
        // W dives (no boost): height into speed, at 60 degrees at most
        float v[3] = {0, 0, 11};
        Track t;
        const float h = fly(v, 3, 0, false, p, nullptr, nullptr, kForward, &t);
        check(h < -10.0f && len3(v) > 20.0f, "stick: hold W dives and trades height for speed");
        check(t.min_pitch >= -std::asin(kMaxDivePitch) - 1e-3f, "stick: a dive is never steeper than 60 degrees");
        check(t.max_turn <= kPitchRate * dt + 1e-4f, "stick: the pitch never moves faster than kPitchRate (diving)");
        check(len3(v) <= kDiveFactor * p.max_speed + 0.5f, "stick: a dive stops at 2 x max_speed");
    }
    {
        // the camera alone changes nothing: looking up, down or level with the stick centred flies the same path
        float a[3] = {0, 0, 11}, b[3] = {0, 0, 11}, c[3] = {0, 0, 11};
        const float ha = fly(a, 4, 45, true, p, nullptr, nullptr, kCentred);
        const float hb = fly(b, 4, -45, true, p, nullptr, nullptr, kCentred);
        const float hc = fly(c, 4, 0, true, p, nullptr, nullptr, kCentred);
        check(ha == hb && hb == hc && a[1] == b[1] && a[2] == c[2], "stick: the camera's pitch changes nothing");
        check(std::fabs(ha) < 0.01f && std::fabs(len3(a) - p.max_speed) < 0.5f,
              "stick: boost with the stick centred holds the altitude and reaches max_speed");
        float d[3] = {0, 0, 35}, e[3] = {0, 0, 35};
        fly(d, 3, 80, false, p, nullptr, nullptr, kBack);
        fly(e, 3, -80, false, p, nullptr, nullptr, kBack);
        check(d[0] == e[0] && d[1] == e[1] && d[2] == e[2], "stick: holding S climbs the same wherever the camera looks");
    }
    {
        // an analog stick's drift (inside the dead zone) is level; non-finite stick values count as centred
        float a[3] = {0, 0, 11}, b[3] = {0, 0, 11};
        const float ha = fly(a, 3, 0, true, p, nullptr, nullptr, Stick{true, -0.15f});
        check(std::fabs(ha) < 0.01f, "stick: inside the dead zone the path stays level");
        const float hb = fly(b, 3, 0, true, p, nullptr, nullptr, Stick{true, NAN});
        check(std::fabs(hb) < 0.01f && finite3(b) && b[1] == a[1] && b[2] == a[2], "stick: a NaN stick counts as centred");
        float c[3] = {0, 0, 11};
        const float hc = fly(c, 3, 0, true, p, nullptr, nullptr, Stick{true, -INFINITY});
        check(std::isfinite(hc) && finite3(c) && std::fabs(hc) < 0.01f, "stick: an infinite stick counts as centred");
        Step s;
        s.heading[2] = 1, s.dt = dt, s.boost = true, s.has_stick = true, s.v0[2] = 11, s.v[2] = 11;
        s.stick[0] = NAN, s.stick[1] = -1;
        float out[3];
        check(plane_step(s, p, out) == Result::Plane && out[1] == 0.0f, "stick: a NaN sideways axis counts as centred too");
        s.stick[0] = 0, s.v0[1] = NAN;
        check(plane_step(s, p, out) == Result::Game, "stick: a non-finite velocity still leaves the game's glide");
    }
    {
        // the energy trade still holds: from 35 m/s without boost, S climbs while slowing, then the game's glide again
        float v[3] = {0, 0, 35};
        Track t;
        const float h = fly(v, 8, 0, false, p, nullptr, nullptr, kBack, &t);
        check(h > 5.0f, "stick: no boost, S from 35 m/s trades speed for height (more than 5 m)");
        check(len3(v) <= 11.5f && t.plane > 0 && t.plane < 8 * 60, "stick: the climb runs out of speed and the game glides again");
        // a plain glide holding S has no spare energy: no climb
        float w[3] = {0, 0, 11};
        Track g;
        fly(w, 3, 0, false, p, nullptr, nullptr, kBack, &g);
        check(g.max_climb <= 1e-3f, "stick: a plain glide holding S does not climb");
        // letting go at speed: the coast slows it, level, then a normal glide
        float c[3] = {0, 0, 35};
        const float hc = fly(c, 6, 0, false, p, nullptr, nullptr, kCentred);
        check(std::fabs(hc) < 0.01f && len3(c) <= 11.5f, "stick: letting go holds the altitude and settles into the game's glide");
        // a half-forward analog stick dives at about 30 degrees and settles at a speed of its own
        float d[3] = {0, 0, 11};
        fly(d, 29, 0, false, p, nullptr, nullptr, Stick{true, 0.6f});
        const float at29 = len3(d);
        fly(d, 1, 0, false, p, nullptr, nullptr, Stick{true, 0.6f});
        check(std::fabs(pitch_of(d) + rad60 * 0.5f) < 0.01f && std::fabs(len3(d) - at29) < 0.5f && len3(d) > 20.0f &&
                  len3(d) < kDiveFactor * p.max_speed - 5.0f,
              "stick: half forward dives at 30 degrees and settles at a speed of its own");
        // climb 0: holding S never climbs
        Params q = p;
        q.climb = 0;
        float z[3] = {0, 0, 11};
        Track zt;
        fly(z, 4, 0, true, q, nullptr, nullptr, kBack, &zt);
        check(zt.max_climb <= 0.0f, "stick: climb 0 never climbs");
    }
    {
        // the boost's lift does not count as pitch: level with lift climbs gently (lift x dt), it does not build up
        Params q = p;
        q.lift = 10, q.climb = 120;
        float v[3] = {0, 0, 11};
        Track t;
        fly(v, 5, 0, true, q, nullptr, nullptr, kCentred, &t);
        check(t.max_climb <= q.lift * dt + 1e-3f, "stick: the boost's lift does not build up into a climb");
    }
    {
        // hard bounds with the stick: zero/huge speeds and dt stay finite and capped
        bool bounded = true;
        for (float initial : {0.0f, 1e-10f, 11.0f, 120.0f})
            for (float y : {-1.0f, 0.0f, 1.0f})
                for (float t : {dt, 0.25f, 1e6f})
                    for (float vy : {-120.0f, 0.0f, 120.0f}) {
                        Step s;
                        s.heading[2] = 1, s.has_stick = true, s.stick[1] = y, s.boost = true, s.dt = t;
                        s.v0[1] = vy, s.v0[2] = initial, s.v[2] = initial * 0.5f;
                        float out[3];
                        plane_step(s, p, out);
                        bounded = bounded && finite3(out) && len3(out) <= kSpeedLimit + 1e-3f && out[1] <= p.climb + 1e-3f;
                    }
        check(bounded, "stick: any speed, pitch and step time keep finite, bounded velocities");
    }
    {
        // Lift cannot be recovered from velocity using the current sprint bit, dt or climb cap: the last step may
        // have had different values, and its final vector may have been clamped. Use the actual flight angle.
        Step s;
        s.heading[2] = 1, s.has_stick = true, s.boost = true, s.dt = 0.25f;
        s.v0[2] = 35, s.v[2] = 20;
        Params q = p;
        q.lift = 100, q.climb = 0;
        float out[3];
        plane_step(s, q, out);
        check(out[1] == 0.0f, "stick: engaging lift with climb 0 cannot invent a dive from level flight");
        q.climb = 120;
        plane_step(s, q, out);
        check(out[1] > 0 && pitch_of(out) <= kPitchRate * s.dt + 1e-5f,
              "stick: lift starts upward from actual velocity within the pitch rate");

        bool settling = true;
        for (bool boost : {false, true}) {
            float v[3] = {0, 2.5f, 35};  // a previous 0.25 s step's lift, or an updraft: no hidden history
            for (int i = 0; i < 240; ++i) {
                s.dt = i % 2 ? dt : 1.0f / 240.0f;
                s.boost = boost;
                game(s, v);
                q.lift = 10;
                plane_step(s, q, out);
                const float before = pitch_of(v), after = pitch_of(out);
                settling = settling && after >= 0 && std::fabs(after - before) <= kPitchRate * s.dt + 1e-5f;
                std::copy(out, out + 3, v);
            }
            settling = settling && v[1] <= (boost ? q.lift * dt + 1e-3f : 1e-5f);
        }
        check(settling, "stick: variable dt and sprint release settle lift without crossing level or accumulating");

        // At the speed budget, normalization used to destroy the assumed lift offset too.
        s.dt = dt, s.boost = true, s.v0[1] = 0, s.v0[2] = 35, s.v[1] = 0, s.v[2] = 20;
        q.lift = 100;
        bool bounded_lift = true;
        for (int i = 0; i < 300; ++i) {
            game(s, s.v0);
            plane_step(s, q, out);
            bounded_lift = bounded_lift && out[1] >= 0 && out[1] <= q.lift * dt + 1e-3f &&
                           len3(out) <= q.max_speed + 1e-3f;
            std::copy(out, out + 3, s.v0);
        }
        check(bounded_lift, "stick: lift at the speed budget stays an offset instead of feeding back into pitch");
    }
    {
        // A vertical arrival is possible after the game's other programs, independently of the stick target.
        bool capped = true;
        for (float vy : {-40.0f, 40.0f}) {
            Step s;
            s.heading[2] = 1, s.has_stick = true, s.boost = true, s.dt = dt;
            s.v0[1] = vy, s.v[2] = 20;
            Params q = p;
            q.climb = 120, q.max_speed = 60;
            float out[3];
            plane_step(s, q, out);
            const float angle = pitch_of(out);
            capped = capped && finite3(out) && angle >= -std::asin(kMaxDivePitch) - 1e-5f && angle <= rad60 + 1e-5f;
        }
        check(capped, "stick: vertical incoming velocity respects both 60-degree caps");
        Step s;
        s.heading[2] = 1, s.has_stick = true, s.boost = true, s.dt = 0.25f;
        s.v0[2] = 11, s.v[2] = 5;
        Params q = p;
        q.lift = 100, q.climb = 120;
        float out[3];
        plane_step(s, q, out);
        check(pitch_of(out) <= kPitchRate * s.dt + 1e-5f && pitch_of(out) <= rad60 + 1e-5f,
              "stick: a large lift cannot push the final velocity past the pitch rate or climb angle cap");
    }
    {
        // Releasing a tiny climb or sink must reach exactly level without crossing or oscillating around it.
        bool level = true;
        for (float vy : {-0.001f, 0.001f}) {
            Step s;
            s.heading[2] = 1, s.has_stick = true, s.boost = true, s.dt = dt;
            s.v0[1] = vy, s.v0[2] = 35, s.v[2] = 20;
            float out[3];
            plane_step(s, p, out);
            level = level && out[1] == 0;
        }
        check(level, "stick: a slight climb or sink releases to exactly level without overshoot");

        // The glide step can change the vertical velocity; the ramp starts from v0, not the game's changed value.
        Step a;
        a.heading[2] = 1, a.has_stick = true, a.boost = true, a.dt = dt, a.v0[2] = 35, a.v[2] = 20;
        Step b = a;
        a.v[1] = 25, b.v[1] = -25;
        float va[3], vb[3];
        plane_step(a, p, va);
        plane_step(b, p, vb);
        check(va[1] == 0 && std::equal(va, va + 3, vb), "stick: the game's vertical drag does not become ramp state");

        // An updraft pulse becomes actual pitch, then unwinds; it does not persist as extra ramp state.
        Params q = p;
        q.climb = 120, q.max_speed = 60;
        float v[3] = {0, 45, 35};
        Track t;
        fly(v, 3, 0, true, q, nullptr, nullptr, kCentred, &t);
        check(v[1] == 0 && t.max_pitch <= rad60 + 1e-5f, "stick: an updraft pulse is bounded and settles back to level");

        // The climb cap and normal-glide handoff are deliberate constraints on the ramp, not hidden history.
        q.climb = 0;
        a.stick[1] = -1;
        plane_step(a, q, va);
        std::copy(va, va + 3, a.v0);
        a.stick[1] = 0;
        plane_step(a, q, vb);
        check(va[1] == 0 && vb[1] == 0, "stick: releasing after a climb cap uses the actual capped velocity");
        a.boost = false, a.v0[1] = 2, a.v0[2] = 10, a.v[1] = -2, a.v[2] = 11;
        check(plane_step(a, p, va) == Result::Game && va[1] == 0 && va[2] == 11,
              "stick: the coast hands back to the game's glide with altitude hold");
    }
    {
        // Malicious finite values saturate at the same target; the dead zone includes both endpoints.
        bool saturated = true, centred = true, proportional = true, tiny = true;
        for (float sign : {-1.0f, 1.0f}) {
            Step s;
            s.heading[2] = 1, s.has_stick = true, s.boost = true, s.dt = 0.25f;
            s.v0[2] = 40, s.v[2] = 20;
            s.v0[1] = sign * 30;
            Params q = p;
            q.climb = 120, q.max_speed = 60;
            float a[3], b[3];
            s.stick[1] = sign;
            plane_step(s, q, a);
            s.stick[0] = -std::numeric_limits<float>::max(), s.stick[1] = sign * std::numeric_limits<float>::max();
            plane_step(s, q, b);
            saturated = saturated && std::equal(a, a + 3, b);
            s.v0[1] = 0, s.stick[1] = sign * kStickDeadZone;
            plane_step(s, q, a);
            centred = centred && a[1] == 0;
            float v[3] = {0, 0, 40};
            fly(v, 3, 0, true, q, nullptr, nullptr, Stick{true, sign * 0.6f});
            proportional = proportional && std::fabs(pitch_of(v) + sign * rad60 * 0.5f) < 1e-4f;
            s.stick[1] = sign, s.v0[2] = 1e-10f, s.v[2] = 0;
            plane_step(s, q, a);
            tiny = tiny && finite3(a) && std::fabs(pitch_of(a)) <= kPitchRate * s.dt + 1e-5f;
        }
        check(saturated, "stick: out-of-range finite axes saturate before use");
        check(centred, "stick: both dead-zone boundaries are centred");
        check(proportional, "stick: half-range analog pitch is proportional in both directions");
        check(tiny, "stick: near-zero velocity starts the ramp from level");

        Step s;
        s.heading[2] = 1, s.has_stick = true, s.boost = true, s.stick[1] = -1;
        s.v0[2] = 35, s.v[2] = 20, s.dt = 0.25f;
        float a[3], b[3];
        plane_step(s, p, a);
        s.dt = 10;
        plane_step(s, p, b);
        check(std::equal(a, a + 3, b), "stick: all math shares the runtime's 0.25 s dt clamp");
    }
    // ---------------------------------------------------------------- camera fallback (a build without the stick)
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
        // Lift is another boost acceleration: it must respect the speed cap after it changes the vector.
        Step s;
        s.heading[2] = 1, s.dt = 0.25f, s.boost = true, s.v0[2] = 120, s.v[2] = 60;
        view_at(s, 90);
        Params q = p;
        q.max_speed = 120, q.thrust = 100, q.lift = 100, q.climb = 120;
        float out[3];
        check(plane_step(s, q, out) == Result::Plane && finite3(out) && len3(out) <= kSpeedLimit + 1e-3f,
              "lift at the hard speed limit cannot accelerate beyond 120 m/s");
        s.v0[2] = 35, s.v[2] = 20;
        view_at(s, 0);
        q.max_speed = 35;
        plane_step(s, q, out);
        check(finite3(out) && len3(out) <= q.max_speed + 1e-3f,
              "lift at the boost cap cannot accelerate beyond max_speed");
        // Speed already earned in a dive is kept, even when the boost cap is lower.
        s.v0[2] = 50;
        plane_step(s, q, out);
        check(std::fabs(len3(out) - 50.0f) < 1e-3f, "lift respects a dive's existing speed above the boost cap");
    }
    {
        // No division by the glide's speed, even at rest or while pointing straight up/down.
        bool bounded = true, pitch_bounded = true;
        for (float initial : {0.0f, 1e-10f, 1e-4f, 11.0f, 35.0f, 120.0f}) {
            for (float pitch : {-90.0f, -89.999f, -60.0f, 0.0f, 60.0f, 90.0f}) {
                for (float dt : {1.0f / 60.0f, 0.25f, 1e6f, std::numeric_limits<float>::max()}) {
                    for (bool boost : {false, true}) {
                        Step s;
                        view_at(s, pitch);
                        for (int i = 0; i < 3; ++i) s.heading[i] = s.view[i];
                        s.v0[2] = initial, s.v[2] = initial * 0.5f, s.dt = dt, s.boost = boost;
                        float out[3];
                        const auto r = plane_step(s, p, out);
                        bounded = bounded && finite3(out) && len3(out) <= kSpeedLimit + 1e-3f && out[1] <= p.climb + 1e-3f;
                        if (r == Result::Plane && len3(out) > 0)
                            pitch_bounded = pitch_bounded && out[1] / len3(out) <= kMaxClimbPitch + 1e-3f;
                    }
                }
            }
        }
        check(bounded, "zero/near-zero speed, vertical headings and huge dt keep finite, bounded velocities");
        check(pitch_bounded, "the plane path never climbs steeper than 60 degrees (without lift)");
        Step s;
        s.heading[2] = 1, s.v0[2] = 35, s.v[1] = -2, s.v[2] = 20, s.boost = true;
        Params q = p;
        q.hold_altitude = false;
        bool unchanged = true;
        for (float dt : {0.0f, -1.0f, NAN, INFINITY}) {
            s.dt = dt;
            float out[3];
            unchanged = unchanged && plane_step(s, q, out) == Result::Game && out[0] == s.v[0] &&
                        out[1] == s.v[1] && out[2] == s.v[2];
        }
        check(unchanged, "zero, negative and non-finite dt use the game's glide");
        s.dt = 1.0f / 60.0f, s.view[0] = NAN;
        float out[3];
        check(plane_step(s, p, out) == Result::Plane && finite3(out), "a NaN view falls back to the finite heading");
        s.heading[1] = NAN;
        check(plane_step(s, q, out) == Result::Game && out[1] == s.v[1] && out[2] == s.v[2],
              "a NaN heading uses the game's glide");
        s.heading[1] = 0;
        view_at(s, 90);
        check(std::fabs(path_pitch(s) - kMaxClimbPitch) < 1e-6f, "a vertical upward view uses the 60-degree pitch limit");
        view_at(s, -90);
        check(std::fabs(path_pitch(s) + 1.0f) < 1e-6f, "a vertical downward view may dive straight down");
        s.view[1] = kLevelBand + 1e-5f, s.view[2] = std::sqrt(1.0f - s.view[1] * s.view[1]);
        check(path_pitch(s) > 0 && path_pitch(s) < 1e-3f, "the level-flight band has a continuous pitch transition");
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
