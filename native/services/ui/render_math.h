// SPDX-License-Identifier: MIT
// kiln.render's projection: the game's camera (position, rotation quaternion, vertical field of view) to pixels.
// Header only and free of Windows headers, so tests/render_math_test.cpp builds it on the host.
// Spaces (docs/re/models.md, docs/re/client-render.md "The camera"): world x east, y up, z north, left-handed; camera
// space x right, y up, z forward. The quaternion turns camera space into world space (rotate (0,0,1) = view direction).
#pragma once
#include <cmath>

namespace krender {

struct Camera {
    double pos[3];
    float rot[4];   // x y z w
    float fov_y;    // radians
    float near_plane;
};

// v' = q * v * q^-1 for a unit quaternion (x y z w)
inline void quat_rotate(const float q[4], const double v[3], double out[3]) {
    const double x = q[0], y = q[1], z = q[2], w = q[3];
    // t = 2 * cross(q.xyz, v); v' = v + w * t + cross(q.xyz, t)
    const double tx = 2.0 * (y * v[2] - z * v[1]);
    const double ty = 2.0 * (z * v[0] - x * v[2]);
    const double tz = 2.0 * (x * v[1] - y * v[0]);
    out[0] = v[0] + w * tx + (y * tz - z * ty);
    out[1] = v[1] + w * ty + (z * tx - x * tz);
    out[2] = v[2] + w * tz + (x * ty - y * tx);
}

// world -> camera space (the inverse rotation: the conjugate quaternion)
inline void to_camera(const Camera& c, const double world[3], double out[3]) {
    const double rel[3] = {world[0] - c.pos[0], world[1] - c.pos[1], world[2] - c.pos[2]};
    const float inv[4] = {-c.rot[0], -c.rot[1], -c.rot[2], c.rot[3]};
    quat_rotate(inv, rel, out);
}

// The game stores degrees in its camera (its default is 60); radians are taken as they are.
inline float fov_radians(float fov) {
    if (!(fov > 0.0f) || !std::isfinite(fov)) return 1.0471976f;  // 60 degrees
    return fov > 3.2f ? fov * 0.017453292f : fov;
}

// Camera space -> pixels on a width x height screen. out = {x, y, depth}. False when behind the near plane (out then
// holds the mirrored projection, clamped to finite values).
inline bool project_camera(const Camera& c, const double v[3], float width, float height, float out[3]) {
    const double t = std::tan(static_cast<double>(c.fov_y) * 0.5);
    const double aspect = height > 0 ? static_cast<double>(width) / height : 16.0 / 9.0;
    out[0] = out[1] = out[2] = 0;
    if (!std::isfinite(width) || !std::isfinite(height) || width <= 0 || height <= 0 ||
        !std::isfinite(v[0]) || !std::isfinite(v[1]) || !std::isfinite(v[2]) ||
        !std::isfinite(c.fov_y) || c.fov_y <= 0 || c.fov_y >= 3.14159265f ||
        !std::isfinite(c.near_plane) || c.near_plane <= 0) return false;
    double z = v[2];
    const bool front = z > c.near_plane;
    if (std::fabs(z) < 1e-6) z = z < 0 ? -1e-6 : 1e-6;
    const double nx = v[0] / (z * t * aspect);
    const double ny = v[1] / (z * t);
    double sx = (nx * 0.5 + 0.5) * width;
    double sy = (0.5 - ny * 0.5) * height;
    if (!std::isfinite(sx) || sx > 1e7 || sx < -1e7) sx = sx < 0 ? -1e7 : 1e7;
    if (!std::isfinite(sy) || sy > 1e7 || sy < -1e7) sy = sy < 0 ? -1e7 : 1e7;
    out[0] = static_cast<float>(sx);
    out[1] = static_cast<float>(sy);
    out[2] = static_cast<float>(v[2]);
    return front;
}

inline bool project(const Camera& c, const double world[3], float width, float height, float out[3]) {
    double v[3];
    to_camera(c, world, v);
    return project_camera(c, v, width, height, out);
}

}  // namespace krender
