// SPDX-License-Identifier: MIT
// Kiln's starter emitter: the root entry of a ParticleBlock. One state per instance. It spawns into its child entry.
// Parameters: kiln_vfx_params.h, KE_*.
#include "kiln_vfx.hlsl"

// The game's spawn sequence (module 15,544 B): take a spawn slot, take a free particle, publish it, write its state.
// False means no room this tick, and nothing was taken.
bool kSpawn(KilnParticle p, uint childStateSize, inout uint rng)
{
    if (p.emitterIndex >= kUpdateU(KU_EMITTER_STATE_COUNT))
        return false;
    uint emitter = p.emitterIndex * KM_STRIDE;
    uint spawnCounter = g_emitterStates.Load(emitter + KM_SPAWN_COUNTER);
    if (kCounterAdd(spawnCounter, 1u) >= g_emitterStates.Load(emitter + KM_MAX_SPAWN)) {
        kCounterAdd(spawnCounter, KILN_MINUS_ONE);
        return false;
    }
    uint child = g_emitterStates.Load(emitter + KM_PARTICLE_CHUNK) * KC_STRIDE;
    uint freeNow = kCounterAdd(g_chunkUpdate.Load(child + KC_FREE_COUNTER), KILN_MINUS_ONE);
    uint newId = kStateU(g_chunkUpdate.Load(p.chunk + KC_CHILD_FREE_LIST) + 4 * (freeNow - 1));
    uint slot = kCounterAdd(g_chunkUpdate.Load(child + KC_ACTIVE_COUNTER), 1u);
    kStorePutU(g_chunkUpdate.Load(p.chunk + KC_CHILD_ACTIVE) + 4 * slot, newId);
    kCounterAdd(g_emitterStates.Load(emitter + KM_PARTICLE_COUNTER), 1u);

    uint s = g_chunkUpdate.Load(p.chunk + KC_CHILD_STATES) + childStateSize * newId;
    rng = kRngAdvance(rng);
    kStorePutU(s + KB_EMITTER_INDEX, 0u);
    kStorePutU(s + KB_PARENT_EMITTER, p.emitterIndex);
    kStorePutU(s + KB_INSTANCE, g_emitterStates.Load(emitter + KM_INSTANCE));
    kStorePutU(s + KB_RNG, kRngOutput(rng));
    kStorePutU(s + KB_TICK, 0u);
    for (uint off = 20; off < childStateSize; off += 4)
        kStorePutU(s + off, 0u);
    return true;
}

[numthreads(32, 1, 1)]
void cs_main(uint3 id : SV_DispatchThreadID)
{
    KilnParticle p;
    if (!kBegin(id.x, KE_STATE_SIZE, p))
        return;

    uint  flags    = kParamU(p.params + KE_FLAGS);
    float duration = kParamF(p.params + KE_DURATION);
    float delay    = kParamF(p.params + KE_DELAY);
    // The per-play "count" scales burst and rate (KX_COUNT, at most KX_MAX_FACTOR times).
    float count    = kPlay(p.instance).count;
    float rate     = kParamF(p.params + KE_RATE) * count;
    uint  burst    = (uint)min((float)kParamU(p.params + KE_BURST) * count + 0.5f, 1048576.0f);
    uint  childSize = min(kParamU(p.params + KE_CHILD_STATE) & ~3u, (uint)KILN_VFX_MAX_CHILD_STATE);

    bool  first   = p.tick == 0;
    float age     = first ? 0.0f : kStateF(p.state + KE_S_AGE);
    float accum   = first ? 0.0f : kStateF(p.state + KE_S_ACCUM);
    uint  pending = first ? burst : kStateU(p.state + KE_S_PENDING);
    float dt      = kUpdateF(KU_TIME_STEP);

    bool active = (kInstU(p.instance, KI_FLAGS) & KI_FLAG_ACTIVE) != 0;
    bool loops  = (flags & KE_FLAG_LOOP) != 0 && duration > 0.0f;
    bool alive  = active;
    if (active) {
        age += dt;
        float running = age - delay;
        if (running >= 0.0f) {
            bool emitting = duration <= 0.0f || running < duration;
            if (!emitting && loops) {
                age -= duration;
                pending = min(pending + burst, (uint)KILN_VFX_MAX_ACCUM);
                emitting = true;
            }
            if (emitting)
                accum = min(accum + max(rate, 0.0f) * dt, (float)KILN_VFX_MAX_ACCUM);
            else if (pending == 0)
                alive = false;   // one-shot finished: the emitter goes, its particles live on
        }
        if (p.phase != KP_DRAW_ONLY && running >= 0.0f) {
            uint owed = pending + (uint)accum;
            uint spawns = min(owed, (uint)KILN_VFX_MAX_SPAWNS_PER_TICK);
            uint done = 0;
            for (; done < spawns; done++) {
                if (!kSpawn(p, childSize, p.rng))
                    break;
            }
            uint fromPending = min(done, pending);
            pending -= fromPending;
            accum -= (float)(done - fromPending);
        }
    }

    if (p.phase != KP_DRAW_ONLY) {
        kStorePutF(p.state + KE_S_AGE, age);
        kStorePutF(p.state + KE_S_ACCUM, accum);
        kStorePutU(p.state + KE_S_PENDING, pending);
    }
    kEnd(p, alive, true);
}
