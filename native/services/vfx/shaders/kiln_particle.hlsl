// SPDX-License-Identifier: MIT
// The particle kit, part 1 (Kiln's side): what Kiln's sprite module needs beyond kiln_hooks.hlsl: curves, the draw list append and the
// spawn shapes. Kiln's kiln_sprite.hlsl is kiln_particle_main.hlsl with no hooks; kiln_sprite_hooked.hlsl is the same module with empty,
// separate hook functions, which the cook links a mod's hook module into (cook/spvlink.cpp, docs/kiln/modding/shaders.md).
// A mod never includes this file: its hook module is built from kiln_hooks.hlsl and kiln_hooks_main.hlsl only.
#ifndef KILN_PARTICLE_HLSL
#define KILN_PARTICLE_HLSL
#include "kiln_vfx.hlsl"
#include "kiln_hooks.hlsl"

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
        float inner = a > 0.0f ? min(b / a, 1.0f) : 0.0f;
        float r = a * pow(lerp(inner * inner * inner, 1.0f, kRandom(rng)), 0.33333334f);
        o = n * r;
        outward = r > 0.0f ? n : float3(0.0f, 1.0f, 0.0f);
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
        outward = r > 0.0f ? n : float3(0.0f, 1.0f, 0.0f);
    } else if (shape == KS_SHAPE_LINE) {
        o = float3(kRandomRange(rng, -0.5f, 0.5f) * a, 0.0f, 0.0f);
        float t = kRandomRange(rng, 0.0f, 6.28318548f);
        outward = o.x != 0.0f ? float3(0.0f, cos(t), sin(t)) : float3(0.0f, 1.0f, 0.0f);
    }
    return o;
}

// A float that is neither NaN nor infinite. A comparison, not isnan/isinf: a driver's fast-math can't fold it away.
bool kFinite1(float x) { return abs(x) <= 3.4028234e38f; }
// What a hook handed back, before Kiln's code uses it: a particle whose clock or place is not finite dies, other fields fall back to
// harmless values. Kiln's own sprite runs it too (it is a no-op on what Kiln computes).
void kSanitizeSprite(inout KilnSprite s)
{
    bool ok = kFinite(s.position) && kFinite(s.velocity) && kFinite1(s.age) && kFinite1(s.life);
    s.position = ok ? s.position : float3(0.0f, 0.0f, 0.0f);
    s.velocity = ok ? clamp(s.velocity, -1.0e6f, 1.0e6f) : float3(0.0f, 0.0f, 0.0f);
    s.age = ok ? s.age : 0.0f;
    s.life = ok ? s.life : 0.001f;
    s.roll = kFinite1(s.roll) ? s.roll : 0.0f;
    s.spin = kFinite1(s.spin) ? clamp(s.spin, -1.0e6f, 1.0e6f) : 0.0f;
    s.aux = kFinite1(s.aux) ? s.aux : 0.0f;
    s.alive = s.alive && ok;
}
void kSanitizeLook(inout KilnLook l)
{
    bool ok = kFinite(l.scale) && kFinite(l.color) && kFinite1(l.alpha) && kFinite1(l.roll);
    l.scale = ok ? l.scale : float3(0.0f, 0.0f, 0.0f);
    l.color = ok ? clamp(l.color, float3(0.0f, 0.0f, 0.0f), float3(65504.0f, 65504.0f, 65504.0f)) : float3(0.0f, 0.0f, 0.0f);
    l.alpha = ok ? l.alpha : 0.0f;
    l.roll = ok ? l.roll : 0.0f;
    l.visible = l.visible && ok;
}

// For Kiln's own code only: a hook module has no buffers to read a curve from.
float  kiln_curve(uint base, float t) { return kCurve(base, t); }

#endif
