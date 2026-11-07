// SPDX-License-Identifier: MIT
// The particle kit for mods: what a hook sees, and pure helpers. No buffer, no binding: a hook module built from this file and
// kiln_hooks_main.hlsl can't read or write anything but the values Kiln hands it. Guide: docs/kiln/modding/shaders.md.
//
//   #include "kiln_hooks.hlsl"
//   void swirl(inout KilnSprite s, KilnContext c) { s.velocity += kiln_curl(s.position * 0.5f, s.age) * c.dt; }
//   #define KILN_UPDATE swirl
//   #include "kiln_hooks_main.hlsl"
//
// Hooks, all optional:
//   KILN_SPAWN(inout KilnSprite s, KilnContext c)                  once, when the particle is born, after Kiln chose its start
//   KILN_UPDATE(inout KilnSprite s, KilnContext c)                 every tick, after Kiln moved it. s.alive = false kills it
//   KILN_DRAW(inout KilnLook l, KilnSprite s, KilnContext c)       before it is drawn. l.visible = false skips this frame's draw
// Kiln's cook links them into its own sprite module: spawning, the particle lists, culling and drawing stay Kiln's code. Every loop gets
// an iteration cap, every array index is clamped, and what a hook returns is checked (non-finite values die or fall back) before Kiln
// writes anything.
#ifndef KILN_HOOKS_HLSL
#define KILN_HOOKS_HLSL
#include "kiln_vfx_params.h"   // KILN_HOOKS_ABI, the KS_* numbers and limits (plain #defines)
#include "kiln_math.hlsl"

// ---- what a hook sees -----------------------------------------------------------------------------------------------------------
// One particle. Kiln keeps position, velocity, age, life, roll, spin and aux from tick to tick; rng is the particle's random state.
struct KilnSprite
{
    float3 position;   // world, metres
    float3 velocity;   // world, m/s
    float  age;        // seconds since birth
    float  life;       // seconds it lives
    float  roll;       // radians, around the view axis for a camera-facing quad
    float  spin;       // radians per second
    float  aux;        // yours: kept from tick to tick, 0 at birth
    uint   rng;        // kiln_random(s) draws from it
    bool   alive;      // false: it dies at the end of this tick
};
// The tick and the effect.
struct KilnContext
{
    float  dt;         // seconds this tick
    float  t;          // age / life, 0..1
    uint   time;       // the game's VFX clock (VfxUpdateBuffer.time; its unit is unverified: use s.age for a clock you can trust)
    float3 origin;     // the effect's position, world
    float4 rotation;   // the effect's orientation, a quaternion
    float  scale;      // the effect's scale
    float3 wind;       // the game's wind direction
    float3 sun;        // towards the sun
    KilnPlay play;     // the per-play parameters (color, size, speed, brightness, count)
    float4 user0;      // the block's "shader".constants[0..3]
    float4 user1;      // [4..7]
    uint   params;     // the parameter block's byte offset: only Kiln's own code can read it (a hook module has no buffers)
};
// How it is drawn this frame. Kiln has filled it from the block's curves; change what you like.
struct KilnLook
{
    float3 scale;      // metres; a camera-facing quad uses x and y
    float3 color;      // linear, may exceed 1
    float  alpha;      // 0..1
    float  roll;       // radians
    bool   visible;
};

float  kiln_random(inout KilnSprite s) { return kRandom(s.rng); }
float  kiln_random_range(inout KilnSprite s, float a, float b) { return kRandomRange(s.rng, a, b); }
float3 kiln_random_direction(inout KilnSprite s)
{
    float z = kRandomRange(s.rng, -1.0f, 1.0f);
    float a = kRandomRange(s.rng, 0.0f, 6.28318548f);
    float r = sqrt(saturate(1.0f - z * z));
    return float3(r * cos(a), z, r * sin(a));
}
// The rotation of a vector by the effect's orientation (local to world).
float3 kiln_rotate(float4 q, float3 v) { return kQuatRotate(q, v); }

// Value noise in -1..1, smooth, pure arithmetic (no texture), period 289.
float  kiln_hash(float3 p)
{
    p = frac(p * 0.3183099f + 0.1f);
    p *= 17.0f;
    return frac(p.x * p.y * p.z * (p.x + p.y + p.z)) * 2.0f - 1.0f;
}
float  kiln_noise(float3 p)
{
    float3 i = floor(p);
    float3 f = p - i;
    float3 u = f * f * (3.0f - 2.0f * f);
    float n000 = kiln_hash(i), n100 = kiln_hash(i + float3(1, 0, 0)), n010 = kiln_hash(i + float3(0, 1, 0)), n110 = kiln_hash(i + float3(1, 1, 0));
    float n001 = kiln_hash(i + float3(0, 0, 1)), n101 = kiln_hash(i + float3(1, 0, 1)), n011 = kiln_hash(i + float3(0, 1, 1)), n111 = kiln_hash(i + float3(1, 1, 1));
    return lerp(lerp(lerp(n000, n100, u.x), lerp(n010, n110, u.x), u.y), lerp(lerp(n001, n101, u.x), lerp(n011, n111, u.x), u.y), u.z);
}
// A divergence-free swirl from three noise fields: particles following it curl around without bunching up.
float3 kiln_curl(float3 p, float time)
{
    const float e = 0.1f;
    float3 q = p + float3(0.0f, time * 0.3f, 0.0f);
    float3 a = float3(31.4f, 0.0f, 0.0f), b = float3(0.0f, 47.2f, 0.0f), c = float3(0.0f, 0.0f, 13.7f);
    float dzdy = kiln_noise(q + c + float3(0, e, 0)) - kiln_noise(q + c - float3(0, e, 0));
    float dydz = kiln_noise(q + b + float3(0, 0, e)) - kiln_noise(q + b - float3(0, 0, e));
    float dxdz = kiln_noise(q + a + float3(0, 0, e)) - kiln_noise(q + a - float3(0, 0, e));
    float dzdx = kiln_noise(q + c + float3(e, 0, 0)) - kiln_noise(q + c - float3(e, 0, 0));
    float dydx = kiln_noise(q + b + float3(e, 0, 0)) - kiln_noise(q + b - float3(e, 0, 0));
    float dxdy = kiln_noise(q + a + float3(0, e, 0)) - kiln_noise(q + a - float3(0, e, 0));
    return float3(dzdy - dydz, dxdz - dzdx, dydx - dxdy) / (2.0f * e);
}

#endif
