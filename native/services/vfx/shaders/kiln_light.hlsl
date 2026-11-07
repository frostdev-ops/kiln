// SPDX-License-Identifier: MIT
// Kiln's starter light: a root entry with no child. One point light while it lives.
// Parameters: kiln_vfx_params.h, KL_*.
#include "kiln_vfx.hlsl"

[numthreads(32, 1, 1)]
void cs_main(uint3 id : SV_DispatchThreadID)
{
    KilnParticle p;
    if (!kBegin(id.x, KL_STATE_SIZE, p))
        return;

    bool  first = p.tick == 0;
    float dt    = kUpdateF(KU_TIME_STEP);
    float age   = first ? 0.0f : kStateF(p.state + KL_S_AGE);
    float stopAge = first ? -1.0f : kStateF(p.state + KL_S_STOP_AGE);
    age += dt;

    uint  instFlags = kInstU(p.instance, KI_FLAGS);
    float lifetime = kParamF(p.params + KL_LIFETIME);
    float fadeIn   = kParamF(p.params + KL_FADE_IN);
    float fadeOut  = kParamF(p.params + KL_FADE_OUT);
    if ((instFlags & KI_FLAG_ACTIVE) == 0 && stopAge < 0.0f)
        stopAge = age;

    float fade = fadeIn > 0.0f ? saturate(age / fadeIn) : 1.0f;
    bool alive = true;
    if (lifetime > 0.0f) {
        if (fadeOut > 0.0f)
            fade *= saturate((lifetime - age) / fadeOut);
        alive = age < lifetime;
    }
    if (stopAge >= 0.0f) {
        if (fadeOut > 0.0f)
            fade *= saturate(1.0f - (age - stopAge) / fadeOut);
        alive = alive && fadeOut > 0.0f && age - stopAge < fadeOut;
    }

    if (alive && p.phase != KP_UPDATE_ONLY && (instFlags & KI_FLAG_HIDDEN) == 0) {
        float instScale = kInstF(p.instance, KI_SCALE);
        float4 instRotation = kInstF4(p.instance, KI_ORIENTATION);
        float3 world = kInstF3(p.instance, KI_POSITION) + kQuatRotate(instRotation, kParamF3(p.params + KL_OFFSET) * instScale);
        float radius = min(kParamF(p.params + KL_RADIUS) * instScale, (float)KILN_VFX_MAX_SCALE);
        // The game's flicker (module 16,836 B).
        float wobble = sin(age * 10.0f + cos(age * 13.0f + sin(age * 17.0f)));
        float brightness = kParamF(p.params + KL_BRIGHTNESS) * fade * (1.0f + kParamF(p.params + KL_FLICKER) * wobble * 0.05f);
        KilnPlay play = kPlay(p.instance);
        float3 color = max(kParamF3(p.params + KL_COLOR) * play.tint * (brightness * play.brightness), float3(0.0f, 0.0f, 0.0f));
        if (radius > 0.0f && any(color > 0.0f) && kFinite(world) && kFinite(color) && !kCulled(world, radius)) {
            uint counterOff = kUpdateU(KU_POINT_LIGHT_COUNT);
            uint n = kCounterAdd(counterOff, 1u);
            if (n >= kUpdateU(KU_POINT_LIGHT_CAP)) {
                kCounterAdd(counterOff, KILN_MINUS_ONE);
            } else {
                uint base = n * 52;
                g_pointLightList.Store(base + 0, asuint(world.x));
                g_pointLightList.Store(base + 4, asuint(world.y));
                g_pointLightList.Store(base + 8, asuint(world.z));
                g_pointLightList.Store(base + 12, kHalf(radius * radius) | kHalf(radius) << 16);
                g_pointLightList.Store(base + 16, kHalf(color.x) << 16 | kHalf(color.y));
                g_pointLightList.Store(base + 20, kHalf(color.z) << 16);          // shadow position offset x = 0
                g_pointLightList.Store(base + 24, 0u);                            // shadow position offset y, z = 0
                g_pointLightList.Store(base + 28, p.instance << 16 | p.id);       // uniqueId
                g_pointLightList.Store(base + 32, 0u);                            // orientation (0, 0, 0, 1) as half floats
                g_pointLightList.Store(base + 36, kHalf(1.0f) << 16);
                g_pointLightList.Store(base + 40, 0u);                            // spotScaleAndOffset: not a spot
                g_pointLightList.Store(base + 44, kParamU(p.params + KL_FLAGS));
                g_pointLightList.Store(base + 48, 0u);                            // filterTexture
                float3 camera = kUpdateF3(KU_CAMERA_POSITION);
                g_pointLightSortData.Store(n * 8, asuint(length(world - camera)));
                g_pointLightSortData.Store(n * 8 + 4, n);
            }
        }
    }

    if (p.phase != KP_DRAW_ONLY && alive) {
        kStorePutF(p.state + KL_S_AGE, age);
        kStorePutF(p.state + KL_S_STOP_AGE, stopAge);
    }
    kEnd(p, alive, true);
}
