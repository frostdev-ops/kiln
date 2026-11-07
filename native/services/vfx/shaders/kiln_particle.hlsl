// SPDX-License-Identifier: MIT
// The particle kit, part 1: types and helpers of Kiln's sprite module. A mod's own particle module (ShaderBlock) includes this file,
// defines any of the hooks below, then includes kiln_particle_main.hlsl, which is Kiln's sprite module calling them. Kiln's own
// kiln_sprite.hlsl is exactly that with no hooks. Guide: docs/kiln/modding/shaders.md. Parameters: KS_* in kiln_vfx_params.h.
//
//   #include "kiln_particle.hlsl"
//   void swirl(inout KilnSprite s, KilnContext c) { s.velocity += kiln_curl(s.position * 0.5f, s.age) * c.dt; }
//   #define KILN_UPDATE swirl
//   #include "kiln_particle_main.hlsl"
//
// Hooks, all optional:
//   KILN_SPAWN(inout KilnSprite s, KilnContext c)                  once, when the particle is born, after Kiln chose its start
//   KILN_UPDATE(inout KilnSprite s, KilnContext c)                 every tick, after Kiln moved it. s.alive = false kills it
//   KILN_DRAW(inout KilnLook l, KilnSprite s, KilnContext c)       before it is drawn. l.visible = false skips this frame's draw
// Kiln does the rest: the particle protocol, spawning, list appends, culling, the sort key. A hook can't break those.
#ifndef KILN_PARTICLE_HLSL
#define KILN_PARTICLE_HLSL
#include "kiln_vfx.hlsl"

float kCurve(uint base, float t)
{
    float x = saturate(t) * (KS_CURVE_SAMPLES - 1);
    uint  i = min((uint)x, (uint)(KS_CURVE_SAMPLES - 2));
    return lerp(kParamF(base + i * 4), kParamF(base + (i + 1) * 4), x - (float)i);
}
float3 kCurve3(uint base, float t)
{
    float x = saturate(t) * (KS_CURVE_SAMPLES - 1);
    uint  i = min((uint)x, (uint)(KS_CURVE_SAMPLES - 2));
    return lerp(kParamF3(base + i * 12), kParamF3(base + (i + 1) * 12), x - (float)i);
}

// The list and counter for this draw: one sorted list unless the renderer splits by lighting.
void kAppendDraw(KilnParticle p, uint drawFlags, uint mesh, float3 world, float4 rotation, float3 scale, float3 color, float alpha, float flipTime)
{
    bool split = kUpdateU(KU_SPLIT_DRAW_PIPELINES) != 0;
    uint list = 3;   // binding of g_transparentLitShaded
    uint counterOff, capacity;
    if (!split) {
        counterOff = kUpdateU(KU_TRANSPARENT_COUNT);
        capacity = kUpdateU(KU_TRANSPARENT_CAP);
    } else if ((drawFlags & KS_DRAW_SHADED) != 0) {
        counterOff = kUpdateU(KU_TRANSPARENT_SHADED_COUNT);
        capacity = kUpdateU(KU_TRANSPARENT_SHADED_CAP);
    } else if ((drawFlags & KS_DRAW_LIT) != 0) {
        counterOff = kUpdateU(KU_TRANSPARENT_LIT_COUNT);
        capacity = kUpdateU(KU_TRANSPARENT_LIT_CAP);
        list = 2;
    } else {
        counterOff = kUpdateU(KU_TRANSPARENT_UNLIT_COUNT);
        capacity = kUpdateU(KU_TRANSPARENT_UNLIT_CAP);
        list = 1;
    }
    uint n = kCounterAdd(counterOff, 1u);
    if (n >= capacity) {
        kCounterAdd(counterOff, KILN_MINUS_ONE);
        return;
    }

    uint words[44];
    uint meshOff = mesh * 32;
    words[0]  = g_vfxMeshes.Load(meshOff + 8);    // indexCount
    words[1]  = 1;                                 // instanceCount
    words[2]  = g_vfxMeshes.Load(meshOff + 4);    // indexOffset
    words[3]  = g_vfxMeshes.Load(meshOff + 0);    // vertexOffset
    words[4]  = n;                                 // instanceOffset
    words[5]  = mesh;
    words[6]  = kParamU(p.params + KS_IMAGE);
    words[7]  = 0;                                 // normals
    words[8]  = kParamU(p.params + KS_SAMPLER);
    words[9]  = 0;                                 // uvDistortionMapSampler
    words[10] = asuint(0.0f);                      // edgeFadeOutFactor: off
    float3 rel = kCameraRelative(world);
    words[11] = asuint(rel.x); words[12] = asuint(rel.y); words[13] = asuint(rel.z);
    words[14] = kParamU(p.params + KS_DEPTH_FADE);
    words[15] = asuint(rotation.x); words[16] = asuint(rotation.y); words[17] = asuint(rotation.z); words[18] = asuint(rotation.w);
    words[19] = asuint(scale.x); words[20] = asuint(scale.y); words[21] = asuint(scale.z);
    words[22] = asuint(color.x); words[23] = asuint(color.y); words[24] = asuint(color.z);
    words[25] = asuint(alpha);
    words[26] = kParamU(p.params + KS_ROUGHNESS);
    words[27] = asuint(0.0f); words[28] = asuint(0.0f);   // uvOffset
    words[29] = asuint(1.0f); words[30] = asuint(1.0f);   // uvScale
    words[31] = asuint(0.0f); words[32] = asuint(0.0f);   // uvDistortionOffset
    words[33] = asuint(1.0f); words[34] = asuint(1.0f);   // uvDistortionScale
    words[35] = asuint(0.0f);                      // uvDistortionStrength
    words[36] = 0;                                 // uvDistortionMap
    words[37] = asuint(0.0f);                      // alphaClipValue
    words[38] = asuint(0.1f);                      // alphaClipHalfRange
    words[39] = drawFlags;
    words[40] = 0;                                 // motionVectorImage
    words[41] = asuint(1.0f);                      // motionVectorScale
    words[42] = asuint(flipTime);
    words[43] = kParamU(p.params + KS_FLIPBOOK);

    uint base = n * 176;
    for (uint i = 0; i < 44; i++) {
        if (list == 1)
            g_transparentUnlitUnshaded.Store(base + i * 4, words[i]);
        else if (list == 2)
            g_transparentLitUnshaded.Store(base + i * 4, words[i]);
        else
            g_transparentLitShaded.Store(base + i * 4, words[i]);
    }
    if (!split) {
        // The game's sort key: the far side first, particle id in the low bits.
        float3 camera = kUpdateF3(KU_CAMERA_POSITION);
        uint h = kHalf(-length(world - camera));
        uint key = ((h ^ (0u - (h >> 15) | 0x8000u)) & 0xffffu) << 16 | (p.id & 0xffffu);
        g_transparentSortData.Store(n * 8, key);
        g_transparentSortData.Store(n * 8 + 4, n);
    }
}

// A start point on the block's spawn shape, in the instance's space before its scale, and the direction out of the shape there.
// Bad sizes (negative, an inner radius above the outer) fold into something harmless.
float3 kSpawnOffset(uint params, inout uint rng, out float3 outward)
{
    uint  shape = kParamU(params + KS_SHAPE);
    float a = abs(kParamF(params + KS_SHAPE_A));
    float b = abs(kParamF(params + KS_SHAPE_B));
    float c = abs(kParamF(params + KS_SHAPE_C));
    float3 o = float3(0.0f, 0.0f, 0.0f);
    outward = float3(0.0f, 1.0f, 0.0f);
    if (shape == KS_SHAPE_SPHERE || shape == KS_SHAPE_HEMISPHERE) {
        float z = kRandomRange(rng, shape == KS_SHAPE_HEMISPHERE ? 0.0f : -1.0f, 1.0f);
        float t = kRandomRange(rng, 0.0f, 6.28318548f);
        float s = sqrt(saturate(1.0f - z * z));
        float3 n = float3(s * cos(t), z, s * sin(t));   // z is the height: a hemisphere stays above the instance
        float inner = min(b, a) / max(a, 1.0e-6f);
        float r = a * pow(lerp(inner * inner * inner, 1.0f, kRandom(rng)), 0.33333334f);
        o = n * r;
        outward = n;
    } else if (shape == KS_SHAPE_BOX) {
        o = float3(kRandomRange(rng, -a, a), kRandomRange(rng, -b, b), kRandomRange(rng, -c, c));
        if (dot(o, o) > 1.0e-12f)
            outward = normalize(o);
    } else if (shape == KS_SHAPE_DISC) {
        float t = kRandomRange(rng, 0.0f, 6.28318548f);
        float inner = min(b, a);
        float r = sqrt(lerp(inner * inner, a * a, kRandom(rng)));
        float3 n = float3(cos(t), 0.0f, sin(t));
        o = n * r;
        outward = n;
    } else if (shape == KS_SHAPE_LINE) {
        o = float3(kRandomRange(rng, -0.5f, 0.5f) * a, 0.0f, 0.0f);
        float t = kRandomRange(rng, 0.0f, 6.28318548f);
        outward = float3(0.0f, cos(t), sin(t));
    }
    return o;
}

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
    uint   params;     // the parameter block, for kParamF(c.params + KS_...)
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
float  kiln_curve(uint base, float t) { return kCurve(base, t); }
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
