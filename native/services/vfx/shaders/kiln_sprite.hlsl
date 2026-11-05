// SPDX-License-Identifier: MIT
// Kiln's starter particle: the child entry of a ParticleBlock. Simulates, then appends one TransparentInstanceData.
// The game's Transparent_Forward* shaders draw it. Parameters: KS_*.
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

[numthreads(32, 1, 1)]
void cs_main(uint3 id : SV_DispatchThreadID)
{
    KilnParticle p;
    if (!kBegin(id.x, KS_STATE_SIZE, p))
        return;

    bool  first = p.tick == 0;
    float dt    = kUpdateF(KU_TIME_STEP);
    float age, life, roll, spin;
    float3 pos, vel;
    float4 instRotation = kInstF4(p.instance, KI_ORIENTATION);
    float  instScale    = kInstF(p.instance, KI_SCALE);
    if (first) {
        float3 origin = kInstF3(p.instance, KI_POSITION);
        life = max(kRandomRange(p.rng, kParamF(p.params + KS_LIFE_MIN), kParamF(p.params + KS_LIFE_MAX)), 0.001f);
        float speed = kRandomRange(p.rng, kParamF(p.params + KS_SPEED_MIN), kParamF(p.params + KS_SPEED_MAX));
        // A direction: y up, then spread.
        uint  kind = kParamU(p.params + KS_SPREAD_KIND);
        float cosMax = kind == KS_SPREAD_SPHERE ? -1.0f : (kind == KS_SPREAD_CONE ? cos(kParamF(p.params + KS_SPREAD_ANGLE)) : 1.0f);
        float cosT = kRandomRange(p.rng, cosMax, 1.0f);
        float sinT = sqrt(saturate(1.0f - cosT * cosT));
        float phi  = kRandomRange(p.rng, 0.0f, 6.28318548f);
        float3 dir = float3(sinT * cos(phi), cosT, sinT * sin(phi));
        // A start point inside the spawn sphere.
        float radius = kParamF(p.params + KS_SPAWN_RADIUS) * instScale;
        float3 offset = float3(0.0f, 0.0f, 0.0f);
        if (radius > 0.0f) {
            float z = kRandomRange(p.rng, -1.0f, 1.0f);
            float a = kRandomRange(p.rng, 0.0f, 6.28318548f);
            float r = radius * pow(kRandom(p.rng), 0.33333334f);
            float s = sqrt(saturate(1.0f - z * z));
            offset = float3(s * cos(a), z, s * sin(a)) * r;
        }
        pos  = origin + kQuatRotate(instRotation, offset);
        vel  = kQuatRotate(instRotation, dir) * speed * instScale;
        roll = kRandomRange(p.rng, kParamF(p.params + KS_ROLL_MIN), kParamF(p.params + KS_ROLL_MAX));
        spin = kRandomRange(p.rng, kParamF(p.params + KS_SPIN_MIN), kParamF(p.params + KS_SPIN_MAX));
        age  = 0.0f;
    } else {
        age  = kStateF(p.state + KS_S_AGE);
        life = kStateF(p.state + KS_S_LIFE);
        pos  = kStateF3(p.state + KS_S_POS);
        vel  = kStateF3(p.state + KS_S_VEL);
        roll = kStateF(p.state + KS_S_ROLL);
        spin = kStateF(p.state + KS_S_SPIN);
    }

    age += dt;
    vel += kParamF3(p.params + KS_ACCEL_X) * dt;
    vel *= exp(-max(kParamF(p.params + KS_DRAG), 0.0f) * dt);
    pos += vel * dt;
    roll += spin * dt;

    // Particles outlive a stopped instance, so an effect's end looks natural. The game frees all of them at the kill time.
    bool alive = age < life && kFinite(pos) && kFinite(vel) && all(abs(pos) < (float)KILN_VFX_MAX_WORLD);

    uint instFlags = kInstU(p.instance, KI_FLAGS);
    if (alive && p.phase != KP_UPDATE_ONLY && (instFlags & KI_FLAG_HIDDEN) == 0) {
        uint mesh = kParamU(p.params + KS_MESH);
        float t = age / life;
        float size = clamp(kCurve(p.params + KS_SIZE_CURVE, t) * instScale, 0.0f, (float)KILN_VFX_MAX_SCALE);
        float alpha = saturate(kCurve(p.params + KS_ALPHA_CURVE, t));
        float3 color = kCurve3(p.params + KS_COLOR_CURVE, t);
        if (mesh != 0 && size > 0.0f && alpha > 0.0f && kFinite(color) && g_vfxMeshes.Load(mesh * 32 + 12) != 0) {
            // VfxMeshLayout.boundingSphere sizes the cull. Its centre counts as extra radius: conservative, never culls too much.
            float3 sphereCenter = float3(asfloat(g_vfxMeshes.Load(mesh * 32 + 16)), asfloat(g_vfxMeshes.Load(mesh * 32 + 20)), asfloat(g_vfxMeshes.Load(mesh * 32 + 24)));
            float cullRadius = (length(sphereCenter) + asfloat(g_vfxMeshes.Load(mesh * 32 + 28))) * size;
            if (!kCulled(pos, cullRadius)) {
                float4 base = (kParamU(p.params + KS_FLAGS) & KS_FLAG_FACE_CAMERA) != 0 ? kUpdateF4(KU_CAMERA_ORIENTATION) : instRotation;
                float4 rotation = kQuatMul(base, kQuatRollZ(roll));
                float emissive = saturate(kParamF(p.params + KS_EMISSIVE));
                color *= lerp(1.0f, kUpdateF(KU_EXPOSURE_CORRECTION), emissive);
                float fps = kParamF(p.params + KS_FLIPBOOK_FPS);
                uint flip = kParamU(p.params + KS_FLIPBOOK);
                float frames = max((float)((flip & 0xffffu) * (flip >> 16)), 1.0f);
                float flipTime = fps > 0.0f ? age * fps / frames : t * 0.999f;
                kAppendDraw(p, kParamU(p.params + KS_DRAW_FLAGS), mesh, pos, rotation, float3(size, size, size), color, alpha, flipTime);
            }
        }
    }

    if (p.phase != KP_DRAW_ONLY && alive) {
        kStorePutF(p.state + KS_S_AGE, age);
        kStorePutF(p.state + KS_S_LIFE, life);
        kStorePutF3(p.state + KS_S_POS, pos);
        kStorePutF3(p.state + KS_S_VEL, vel);
        kStorePutF(p.state + KS_S_ROLL, roll);
        kStorePutF(p.state + KS_S_SPIN, spin);
    }
    kEnd(p, alive, false);
}
