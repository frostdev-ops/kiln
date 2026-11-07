// SPDX-License-Identifier: MIT
// The particle kit, part 2: Kiln's sprite module, calling the hooks (kiln_sprite_hooked.hlsl: link points for a mod's hook module). The child entry of
// a ParticleBlock: simulates, then appends one TransparentInstanceData that the game's Transparent_Forward* shaders draw.
#include "kiln_particle.hlsl"

[numthreads(32, 1, 1)]
void cs_main(uint3 id : SV_DispatchThreadID)
{
    KilnParticle p;
    if (!kBegin(id.x, KS_STATE_SIZE, p))
        return;

    KilnSprite s;
    KilnContext c;
    c.dt       = kUpdateF(KU_TIME_STEP);
    c.time     = kUpdateU(KU_TIME);
    c.origin   = kInstF3(p.instance, KI_POSITION);
    c.rotation = kInstF4(p.instance, KI_ORIENTATION);
    c.scale    = kInstF(p.instance, KI_SCALE);
    c.wind     = kUpdateF3(KU_WIND_DIRECTION);
    c.sun      = kUpdateF3(KU_SUN_DIRECTION);
    c.play     = kPlay(p.instance);
    c.params   = p.params;
    c.user0    = float4(kParamF(p.params + KS_USER), kParamF(p.params + KS_USER + 4), kParamF(p.params + KS_USER + 8), kParamF(p.params + KS_USER + 12));
    c.user1    = float4(kParamF(p.params + KS_USER + 16), kParamF(p.params + KS_USER + 20), kParamF(p.params + KS_USER + 24), kParamF(p.params + KS_USER + 28));
    s.alive    = true;

    if (p.tick == 0) {
        s.life = max(kRandomRange(p.rng, kParamF(p.params + KS_LIFE_MIN), kParamF(p.params + KS_LIFE_MAX)), 0.001f);
        float speed = kRandomRange(p.rng, kParamF(p.params + KS_SPEED_MIN), kParamF(p.params + KS_SPEED_MAX)) * c.play.speed;
        // A start point on the spawn shape, and the way out of it.
        float3 outward;
        float3 offset = kSpawnOffset(p.params, p.rng, outward) * c.scale;
        // A direction: y up, then spread.
        uint  kind = kParamU(p.params + KS_SPREAD_KIND);
        float3 dir;
        if (kind == KS_SPREAD_OUTWARD) {
            dir = outward;
        } else {
            float cosMax = kind == KS_SPREAD_SPHERE ? -1.0f : (kind == KS_SPREAD_CONE ? cos(kParamF(p.params + KS_SPREAD_ANGLE)) : 1.0f);
            float cosT = kRandomRange(p.rng, cosMax, 1.0f);
            float sinT = sqrt(saturate(1.0f - cosT * cosT));
            float phi  = kRandomRange(p.rng, 0.0f, 6.28318548f);
            dir = float3(sinT * cos(phi), cosT, sinT * sin(phi));
        }
        s.position = c.origin + kQuatRotate(c.rotation, offset);
        s.velocity = kQuatRotate(c.rotation, dir) * speed * c.scale;
        s.roll = kRandomRange(p.rng, kParamF(p.params + KS_ROLL_MIN), kParamF(p.params + KS_ROLL_MAX));
        s.spin = kRandomRange(p.rng, kParamF(p.params + KS_SPIN_MIN), kParamF(p.params + KS_SPIN_MAX));
        s.age  = 0.0f;
        s.aux  = 0.0f;
#ifdef KILN_SPAWN
        s.rng = p.rng;
        c.t = 0.0f;
        KILN_SPAWN(s, c);
        kSanitizeSprite(s);
        p.rng = s.rng;
#endif
    } else {
        s.age      = kStateF(p.state + KS_S_AGE);
        s.life     = kStateF(p.state + KS_S_LIFE);
        s.position = kStateF3(p.state + KS_S_POS);
        s.velocity = kStateF3(p.state + KS_S_VEL);
        s.roll     = kStateF(p.state + KS_S_ROLL);
        s.spin     = kStateF(p.state + KS_S_SPIN);
        s.aux      = kStateF(p.state + KS_S_AUX);
    }

    s.age += c.dt;
    s.velocity += kParamF3(p.params + KS_ACCEL_X) * c.dt;
    s.velocity *= exp(-max(kParamF(p.params + KS_DRAG), 0.0f) * c.dt);
    s.position += s.velocity * c.dt;
    s.roll += s.spin * c.dt;
    s.life = max(s.life, 0.001f);
    c.t = saturate(s.age / s.life);
#ifdef KILN_UPDATE
    s.rng = p.rng;
    KILN_UPDATE(s, c);
    kSanitizeSprite(s);
    p.rng = s.rng;
    s.life = max(s.life, 0.001f);
    c.t = saturate(s.age / s.life);
#endif

    // Particles outlive a stopped instance, so an effect's end looks natural. The game frees all of them at the kill time.
    // Whatever a hook did, a particle that isn't finite or has left the world dies and never draws.
    bool alive = s.alive && s.age < s.life && kFinite(s.position) && kFinite(s.velocity) && all(abs(s.position) < (float)KILN_VFX_MAX_WORLD);

    uint instFlags = kInstU(p.instance, KI_FLAGS);
    if (alive && p.phase != KP_UPDATE_ONLY && (instFlags & KI_FLAG_HIDDEN) == 0) {
        uint mesh = kParamU(p.params + KS_MESH);
        float t = c.t;
        float size = kCurve(p.params + KS_SIZE_CURVE, t) * c.scale * c.play.size;
        KilnLook look;
        look.scale   = float3(size, size, size);
        look.color   = kCurve3(p.params + KS_COLOR_CURVE, t) * c.play.tint * c.play.brightness;
        look.alpha   = kCurve(p.params + KS_ALPHA_CURVE, t) * c.play.alpha;
        look.roll    = s.roll;
        look.visible = true;
        uint  spriteFlags = kParamU(p.params + KS_FLAGS);
        bool  faceCamera = (spriteFlags & KS_FLAG_FACE_CAMERA) != 0;
        if (faceCamera && (spriteFlags & KS_FLAG_ALIGN_VELOCITY) != 0) {
            // The velocity in the camera's own axes; the quad's +y turns onto its projection, and grows with the speed.
            float4 camera = kUpdateF4(KU_CAMERA_ORIENTATION);
            float3 v = kQuatRotate(float4(-camera.xyz, camera.w), s.velocity);
            if (v.x * v.x + v.y * v.y > 1.0e-8f)
                look.roll = atan2(-v.x, v.y);
            look.scale.y = size + max(kParamF(p.params + KS_STRETCH), 0.0f) * length(s.velocity);
        }
#ifdef KILN_DRAW
        KILN_DRAW(look, s, c);
        kSanitizeLook(look);
#endif
        float3 scale = clamp(look.scale, float3(0.0f, 0.0f, 0.0f), float3(KILN_VFX_MAX_SCALE, KILN_VFX_MAX_SCALE, KILN_VFX_MAX_SCALE));
        float alpha = saturate(look.alpha);
        float3 color = look.color;
        if (look.visible && mesh != 0 && max(scale.x, max(scale.y, scale.z)) > 0.0f && alpha > 0.0f && kFinite(color) && kFinite(scale) &&
            !isnan(look.roll) && !isinf(look.roll) && g_vfxMeshes.Load(mesh * 32 + 12) != 0) {
            // VfxMeshLayout.boundingSphere sizes the cull. Its centre counts as extra radius: conservative, never culls too much.
            float3 sphereCenter = float3(asfloat(g_vfxMeshes.Load(mesh * 32 + 16)), asfloat(g_vfxMeshes.Load(mesh * 32 + 20)), asfloat(g_vfxMeshes.Load(mesh * 32 + 24)));
            float cullRadius = (length(sphereCenter) + asfloat(g_vfxMeshes.Load(mesh * 32 + 28))) * max(scale.x, max(scale.y, scale.z));
            if (!kCulled(s.position, cullRadius)) {
                float4 base = faceCamera ? kUpdateF4(KU_CAMERA_ORIENTATION) : c.rotation;
                float4 rotation = kQuatMul(base, kQuatRollZ(look.roll));
                float emissive = saturate(kParamF(p.params + KS_EMISSIVE));
                color *= lerp(1.0f, kUpdateF(KU_EXPOSURE_CORRECTION), emissive);
                float fps = kParamF(p.params + KS_FLIPBOOK_FPS);
                uint flip = kParamU(p.params + KS_FLIPBOOK);
                float frames = max((float)((flip & 0xffffu) * (flip >> 16)), 1.0f);
                float flipTime = fps > 0.0f ? s.age * fps / frames : t * 0.999f;
                // Finite hook outputs can still overflow in these later calculations (notably age * fps). Check the final record.
                if (kFinite(rotation.xyz) && kFinite1(rotation.w) && kFinite(color) && kFinite1(flipTime))
                    kAppendDraw(p, kParamU(p.params + KS_DRAW_FLAGS), mesh, s.position, rotation, scale, color, alpha, flipTime);
            }
        }
    }

    if (p.phase != KP_DRAW_ONLY && alive) {
        kStorePutF(p.state + KS_S_AGE, s.age);
        kStorePutF(p.state + KS_S_LIFE, s.life);
        kStorePutF3(p.state + KS_S_POS, s.position);
        kStorePutF3(p.state + KS_S_VEL, s.velocity);
        kStorePutF(p.state + KS_S_ROLL, s.roll);
        kStorePutF(p.state + KS_S_SPIN, s.spin);
        kStorePutF(p.state + KS_S_AUX, s.aux);
    }
    kEnd(p, alive, false);
}
