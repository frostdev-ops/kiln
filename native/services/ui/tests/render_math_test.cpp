// SPDX-License-Identifier: MIT
// Host test of kiln.render's projection (render_math.h): no game, no GPU, no Wine. Run: run.sh
#include <cmath>
#include <cstdio>

#include "../render_math.h"

static int g_fail = 0;
#define CHECK(cond)                                                          \
    do {                                                                     \
        if (!(cond)) {                                                       \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);      \
            ++g_fail;                                                        \
        }                                                                    \
    } while (0)
static bool near(double a, double b, double eps = 0.01) { return std::fabs(a - b) <= eps; }

int main() {
    using namespace krender;
    const float W = 1920, H = 1080;
    Camera c{{0, 0, 0}, {0, 0, 0, 1}, fov_radians(60.0f), 0.1f};
    float o[3];

    // straight ahead: the centre, depth = distance
    { const double p[3] = {0, 0, 10}; CHECK(project(c, p, W, H, o)); CHECK(near(o[0], W / 2) && near(o[1], H / 2) && near(o[2], 10)); }
    // +x is right, +y is up (left-handed, y down in pixels)
    { const double p[3] = {1, 0, 10}; CHECK(project(c, p, W, H, o)); CHECK(o[0] > W / 2 && near(o[1], H / 2)); }
    { const double p[3] = {0, 1, 10}; CHECK(project(c, p, W, H, o)); CHECK(o[1] < H / 2 && near(o[0], W / 2)); }
    // behind the camera and on the near plane: not drawable
    { const double p[3] = {0, 0, -10}; CHECK(!project(c, p, W, H, o)); }
    { const double p[3] = {0, 0, 0.05}; CHECK(!project(c, p, W, H, o)); }
    // the vertical field of view: tan(30 deg) * z up is the top edge, and the horizontal edge follows the aspect
    { const double p[3] = {0, std::tan(30.0 * M_PI / 180.0) * 10, 10}; CHECK(project(c, p, W, H, o)); CHECK(near(o[1], 0, 0.05)); }
    { const double p[3] = {std::tan(30.0 * M_PI / 180.0) * 10 * W / H, 0, 10}; CHECK(project(c, p, W, H, o)); CHECK(near(o[0], W, 0.05)); }
    // degrees vs radians, bad values
    CHECK(near(fov_radians(60.0f), M_PI / 3, 1e-5));
    CHECK(near(fov_radians(1.2f), 1.2, 1e-6));
    CHECK(near(fov_radians(NAN), M_PI / 3, 1e-5));
    CHECK(near(fov_radians(-5), M_PI / 3, 1e-5));
    // the quaternion turns camera space into world space: 90 degrees about y looks east (+x)
    {
        const float s = std::sqrt(0.5f);
        Camera e{{0, 0, 0}, {0, s, 0, s}, fov_radians(60.0f), 0.1f};
        const double fwd[3] = {0, 0, 1};
        double w[3];
        quat_rotate(e.rot, fwd, w);
        CHECK(near(w[0], 1, 1e-6) && near(w[1], 0, 1e-6) && near(w[2], 0, 1e-6));
        const double east[3] = {10, 0, 0};
        CHECK(project(e, east, W, H, o));
        CHECK(near(o[0], W / 2) && near(o[1], H / 2));
        const double north[3] = {0, 0, 10};  // now to the camera's left
        CHECK(!project(e, north, W, H, o) || o[0] < 0);
        const double north_east[3] = {10, 0, 2};  // a little left of the centre
        CHECK(project(e, north_east, W, H, o) && o[0] < W / 2);
    }
    // pitch down 45 degrees (about x): a point below ahead is in the centre
    {
        const float s = static_cast<float>(std::sin(M_PI / 8)), co = static_cast<float>(std::cos(M_PI / 8));
        Camera d{{0, 0, 0}, {s, 0, 0, co}, fov_radians(60.0f), 0.1f};
        const double below[3] = {0, -10, 10};
        CHECK(project(d, below, W, H, o));
        CHECK(near(o[0], W / 2) && near(o[1], H / 2));
    }
    // far from the origin (world coordinates of the real map, ~10 km): no precision loss at a 2 m offset
    {
        Camera f{{8123.25, 850.5, 6021.75}, {0, 0, 0, 1}, fov_radians(60.0f), 0.1f};
        const double p[3] = {8123.25 + 0.5, 850.5, 6021.75 + 2};
        CHECK(project(f, p, W, H, o));
        const double ex = (0.5 / (2 * std::tan(M_PI / 6) * W / H) * 0.5 + 0.5) * W;
        CHECK(near(o[0], ex, 0.01));
    }
    // a point exactly at the camera does not produce NaN
    { const double p[3] = {0, 0, 0}; CHECK(!project(c, p, W, H, o)); CHECK(std::isfinite(o[0]) && std::isfinite(o[1])); }
    // Non-finite positions and invalid viewports must never be reported drawable.
    { const double p[3] = {NAN, 0, 10}; CHECK(!project(c, p, W, H, o)); }
    { const double p[3] = {0, 0, INFINITY}; CHECK(!project(c, p, W, H, o)); }
    { const double p[3] = {0, 0, 10}; CHECK(!project(c, p, 0, H, o)); CHECK(!project(c, p, W, NAN, o)); }
    if (g_fail == 0) std::printf("render_math: all checks passed\n");
    return g_fail == 0 ? 0 : 1;
}
