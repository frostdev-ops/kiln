// SPDX-License-Identifier: MIT
// kiln.screen's effect list on its own: ranges, the envelope, order, the limit, and threads against the render thread. tests/run.sh builds it with
// sanitizers. The maths on a GPU: test_gpu.py.
#include <atomic>
#include <cmath>
#include <cstdio>
#include <thread>
#include <vector>

#include "../core.h"

using namespace kscreen;

static int g_fails = 0;
static void check(bool ok, const char* what) {
    printf("%s %s\n", ok ? "PASS" : "FAIL", what);
    if (!ok) ++g_fails;
}
static bool near(float a, float b, float eps = 1e-4f) { return std::fabs(a - b) <= eps; }

static float weight_at(Mixer& m, double t) {
    GpuFrame f{};
    return m.compose(t, f) ? f.effects[0].shape[3] : 0.0f;
}

int main() {
    {
        KilnScreenEffect e = kiln_screen_effect_defaults();
        e.exposure = 100;
        e.contrast = NAN;
        e.saturation = -1;
        e.gamma = 0;
        e.temperature = INFINITY;
        e.strength = 2;
        e.tint[3] = 7;
        e.seconds = -5;
        e.fade_in = -1;
        e.vignette_softness = 0;
        const KilnScreenEffect s = sanitize(e);
        check(s.exposure == 8 && s.contrast == 1 && s.saturation == 0 && near(s.gamma, 0.1f), "values are clamped, and a NaN reads as no change");
        check(s.temperature == 0 && s.strength == 1 && s.tint[3] == 1 && s.seconds == -1 && s.fade_in == 0 && s.vignette_softness > 0, "the other ranges too");
    }
    {
        Mixer m;
        KilnScreenEffect e = kiln_screen_effect_defaults();
        e.fade_in = 1;
        e.seconds = 1;
        e.fade_out = 1;
        const uint32_t h = m.play(1, e);
        check(h != 0 && m.playing(h), "a play gives a handle");
        check(near(weight_at(m, 10.0), 0), "its clock starts at the first compose that sees it");
        check(near(weight_at(m, 10.5), 0.5f), "half way up the fade in");
        check(near(weight_at(m, 11.5), 1), "full");
        check(near(weight_at(m, 12.5), 0.5f), "half way down the fade out");
        check(m.playing(h), "it plays until it has faded");
        check(near(weight_at(m, 13.0), 0) && !m.playing(h) && m.count() == 0, "then it is gone");
        check(!m.stop(h, false), "stopping what ended says so");
    }
    {
        Mixer m;
        KilnScreenEffect e = kiln_screen_effect_defaults();
        e.fade_in = 1;
        e.fade_out = 2;
        const uint32_t h = m.play(1, e);
        weight_at(m, 0);
        check(near(weight_at(m, 0.5), 0.5f), "half way up");
        check(m.stop(h, false), "a stop in the fade in");
        check(near(weight_at(m, 0.5), 0.5f), "fades from where it was");
        check(near(weight_at(m, 1.5), 0.25f), "over its fade out");
        check(near(weight_at(m, 2.5), 0) && m.count() == 0, "and ends");
    }
    {
        Mixer m;
        KilnScreenEffect e = kiln_screen_effect_defaults();
        e.seconds = -1;
        const uint32_t h = m.play(1, e);
        check(near(weight_at(m, 0), 1), "no fade in: full at once");
        check(near(weight_at(m, 1000), 1), "until stopped means until stopped");
        check(m.set_strength(h, 0.25f) && near(weight_at(m, 1001), 0.25f), "strength changes while it plays");
        check(m.stop(h, false) && near(weight_at(m, 1001), 0) && m.count() == 0, "no fade out: gone at the stop");
    }
    {
        Mixer m;
        KilnScreenEffect e = kiln_screen_effect_defaults();
        e.fade_out = 1;
        const uint32_t h = m.play(1, e);
        check(m.stop(h, false) && m.count() == 0, "stopped before it was ever seen: never shows");
        const uint32_t h2 = m.play(1, e);
        weight_at(m, 0);
        check(m.stop(h2, true) && m.count() == 0, "immediate: gone");
    }
    {
        Mixer m;
        KilnScreenEffect e = kiln_screen_effect_defaults();
        e.order = 5;
        e.exposure = 1;
        m.play(1, e);
        e.order = -5;
        e.exposure = 2;
        m.play(1, e);
        e.order = 5;
        e.exposure = 3;
        m.play(1, e);
        GpuFrame f{};
        check(m.compose(0, f) == 3, "three play");
        check(near(f.effects[0].grade[0], 4) && near(f.effects[1].grade[0], 2) && near(f.effects[2].grade[0], 8), "lower order first, among equals the older first");
    }
    {
        Mixer m;
        KilnScreenEffect e = kiln_screen_effect_defaults();
        for (uint32_t i = 0; i < KILN_SCREEN_MAX_EFFECTS; ++i) m.play(i == 0 ? 2 : 1, e);
        check(m.play(1, e) == 0, "one beyond the limit is refused");
        check(m.stop_owner(1, true) == KILN_SCREEN_MAX_EFFECTS - 1 && m.count() == 1, "an owner's effects stop together, and no one else's");
    }
    {
        // grey stays as bright at any temperature, and the colours convert as sRGB says
        KilnScreenEffect e = kiln_screen_effect_defaults();
        e.temperature = 1;
        const GpuEffect g = to_gpu(sanitize(e), 1);
        check(near(0.2126f * g.balance[0] + 0.7152f * g.balance[1] + 0.0722f * g.balance[2], 1) && g.balance[0] > g.balance[2], "warm: more red, same brightness");
        check(near(srgb_to_linear(1), 1) && near(srgb_to_linear(0), 0) && near(srgb_to_linear(0.5f), 0.2140f, 0.001f), "sRGB to linear");
    }
    {
        // plays and stops from other threads against a render thread
        Mixer m;
        std::atomic<bool> stop{false};
        std::thread render([&] {
            GpuFrame f{};
            double t = 0;
            while (!stop) {
                m.compose(t, f);
                t += 0.001;
            }
        });
        std::vector<std::thread> workers;
        std::atomic<int> played{0};
        for (int w = 0; w < 4; ++w)
            workers.emplace_back([&, w] {
                KilnScreenEffect e = kiln_screen_effect_defaults();
                e.fade_out = 0.01f;
                for (int i = 0; i < 20000; ++i) {
                    const uint32_t h = m.play(static_cast<KilnOwner>(w + 1), e);
                    if (h) ++played;
                    m.set_strength(h, 0.5f);
                    m.playing(h);
                    m.stop(h, (i & 1) != 0);
                }
            });
        for (auto& t : workers) t.join();
        stop = true;
        render.join();
        m.clear();
        check(played > 0 && m.count() == 0, "threads against the render thread");
    }
    printf("%d failed\n", g_fails);
    return g_fails ? 1 : 0;
}
