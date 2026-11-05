// SPDX-License-Identifier: MIT
// The particle ABI of keen::VfxUpdatePipelineLayout, client 26430625 / svn 1287369. Evidence: docs/re/vfx.md §3.
//
// The protocol is read from the game's own modules and code. test/test_starter.py runs the compiled modules in a SPIR-V
// interpreter and test/test_gpu.py on real GPUs, with the game's modules through the same checks. Neither runs inside the game.
//
// Every buffer is declared as raw words and read with explicit offsets. Struct packing rules then can't differ between
// front ends: HLSL layout through glslang is not what DXC gave the game. Only the bindings a module touches end up in its
// SPIR-V, so a module's interface stays small.
#ifndef KILN_VFX_HLSL
#define KILN_VFX_HLSL

#include "kiln_vfx_params.h"

[[vk::binding(2, 0)]]  RWByteAddressBuffer g_counterBuffer;
[[vk::binding(0, 1)]]  cbuffer KilnUpdateBuffer { uint4 g_updateWords[87]; };  // VfxUpdateBuffer, 1392 B
[[vk::binding(1, 1)]]  RWByteAddressBuffer g_transparentUnlitUnshaded;         // TransparentInstanceData, stride 176
[[vk::binding(2, 1)]]  RWByteAddressBuffer g_transparentLitUnshaded;
[[vk::binding(3, 1)]]  RWByteAddressBuffer g_transparentLitShaded;             // also the only list when not split
[[vk::binding(4, 1)]]  RWByteAddressBuffer g_transparentSortData;              // {u32 key, u32 index}
[[vk::binding(9, 1)]]  RWByteAddressBuffer g_pointLightList;                   // PointLightData, stride 52
[[vk::binding(10, 1)]] RWByteAddressBuffer g_pointLightSortData;               // {f32 distance, u32 index}
[[vk::binding(13, 1)]] RWByteAddressBuffer g_emitterStates;                    // VfxEmitterState, stride 32
[[vk::binding(17, 1)]] ByteAddressBuffer   g_vfxMeshes;                        // VfxMeshLayout, stride 32
[[vk::binding(39, 1)]] ByteAddressBuffer   g_chunkUpdate;                      // VfxChunkUpdateBuffer, stride 68
[[vk::binding(40, 1)]] ByteAddressBuffer   g_instanceParameters;               // VfxCombinedEffectParameters, stride 128
[[vk::binding(41, 1)]] ByteAddressBuffer   g_resourceDataBuffer;
[[vk::binding(42, 1)]] RWByteAddressBuffer g_chunkStateDataBuffer;

// VfxPushConstants. A plain global here would land in a $Global uniform block: glslang wants a constant buffer.
struct KilnPush { uint data; uint breadcrumbMarkerOffset; };
[[vk::push_constant]] ConstantBuffer<KilnPush> g_push;

// VfxUpdateBuffer, byte offsets (layout hash 0x3609c027)
#define KU_TIME_STEP              0x004
#define KU_SPLIT_DRAW_PIPELINES   0x008
#define KU_EMITTER_STATE_COUNT    0x220
#define KU_TRANSPARENT_CAP        0x224
#define KU_TRANSPARENT_UNLIT_CAP  0x228
#define KU_TRANSPARENT_LIT_CAP    0x22c
#define KU_TRANSPARENT_SHADED_CAP 0x230
#define KU_TRANSPARENT_COUNT      0x234
#define KU_TRANSPARENT_UNLIT_COUNT 0x238
#define KU_TRANSPARENT_LIT_COUNT  0x23c
#define KU_TRANSPARENT_SHADED_COUNT 0x240
#define KU_POINT_LIGHT_CAP        0x25c
#define KU_POINT_LIGHT_COUNT      0x260
#define KU_EXPOSURE_CORRECTION    0x288
#define KU_CAMERA_POSITION        0x294
#define KU_CAMERA_WORLD_POSITION  0x2a4
#define KU_CAMERA_ORIENTATION     0x2d0
#define KU_FRUSTUM_PLANES         0x2e0

// VfxChunkUpdateBuffer, byte offsets (layout hash 0xb7750d24)
#define KC_STRIDE                 68
#define KC_EVALUATE_PARAMETERS     4
#define KC_ACTIVE_COUNTER         12
#define KC_NEXT_ACTIVE_COUNTER    16
#define KC_FREE_COUNTER           20
#define KC_PARTICLE_STATES        24
#define KC_OLD_ACTIVE             28
#define KC_NEW_ACTIVE             32
#define KC_FREE_INDICES           36
#define KC_CHILD_STATES           52
#define KC_CHILD_FREE_LIST        56
#define KC_CHILD_ACTIVE           60

// VfxEmitterState, byte offsets (layout hash 0x5685ca19)
#define KM_STRIDE                 32
#define KM_PARTICLE_COUNTER        0
#define KM_SPAWN_COUNTER           4
#define KM_MAX_SPAWN              12
#define KM_PARTICLE_CHUNK         16
#define KM_INSTANCE               20

// VfxBaseParticleState: the first 20 bytes of every state
#define KB_EMITTER_INDEX           0
#define KB_PARENT_EMITTER          4
#define KB_INSTANCE                8
#define KB_RNG                    12
#define KB_TICK                   16

// VfxCombinedEffectParameters, byte offsets (layout hash 0x12e19673)
#define KI_STRIDE                128
#define KI_POSITION                0
#define KI_FLAGS                  12
#define KI_ORIENTATION            16
#define KI_SCALE                  32
#define KI_FLAG_ACTIVE             1
#define KI_FLAG_HIDDEN             2

// Push constant low bits: what a dispatch may do (docs/re/vfx.md §3.1)
#define KP_UPDATE_AND_DRAW 0
#define KP_UPDATE_ONLY     1
#define KP_DRAW_ONLY       2

uint   kUpdateU(uint off)  { return g_updateWords[off >> 4][(off >> 2) & 3]; }
float  kUpdateF(uint off)  { return asfloat(kUpdateU(off)); }
float3 kUpdateF3(uint off) { return float3(kUpdateF(off), kUpdateF(off + 4), kUpdateF(off + 8)); }
float4 kUpdateF4(uint off) { return float4(kUpdateF3(off), kUpdateF(off + 12)); }

uint   kCounter(uint byteOffset)          { return g_counterBuffer.Load(byteOffset); }
uint   kCounterAdd(uint byteOffset, uint v) { uint old; g_counterBuffer.InterlockedAdd(byteOffset, v, old); return old; }
#define KILN_MINUS_ONE 0xffffffffu

uint   kStateU(uint off)           { return g_chunkStateDataBuffer.Load(off); }
float  kStateF(uint off)           { return asfloat(g_chunkStateDataBuffer.Load(off)); }
float3 kStateF3(uint off)          { return float3(kStateF(off), kStateF(off + 4), kStateF(off + 8)); }
void   kStorePutU(uint off, uint v)  { g_chunkStateDataBuffer.Store(off, v); }
void   kStorePutF(uint off, float v) { g_chunkStateDataBuffer.Store(off, asuint(v)); }
void   kStorePutF3(uint off, float3 v) { kStorePutF(off, v.x); kStorePutF(off + 4, v.y); kStorePutF(off + 8, v.z); }

uint   kParamU(uint off)  { return g_resourceDataBuffer.Load(off); }
float  kParamF(uint off)  { return asfloat(g_resourceDataBuffer.Load(off)); }
float3 kParamF3(uint off) { return float3(kParamF(off), kParamF(off + 4), kParamF(off + 8)); }

uint   kInstU(uint inst, uint off)  { return g_instanceParameters.Load(inst * KI_STRIDE + off); }
float  kInstF(uint inst, uint off)  { return asfloat(kInstU(inst, off)); }
float3 kInstF3(uint inst, uint off) { return float3(kInstF(inst, off), kInstF(inst, off + 4), kInstF(inst, off + 8)); }
float4 kInstF4(uint inst, uint off) { return float4(kInstF3(inst, off), kInstF(inst, off + 12)); }

// The game's PCG-style generator, as its modules have it.
uint  kRngAdvance(uint s) { return s * 0x2c9277b5u + 0xac564b05u; }
uint  kRngOutput(uint s)  { uint x = ((s >> (((s >> 28) + 4) & 31)) ^ s) * 0x108ef2d9u; return (x >> 22) ^ x; }
float kRandom(inout uint s) { uint out_ = kRngOutput(s); s = kRngAdvance(s); return float(out_) * 2.32830644e-10f; }
float kRandomRange(inout uint s, float a, float b) { return a + kRandom(s) * (b - a); }

float4 kQuatMul(float4 a, float4 b)
{
    return float4(a.w * b.xyz + b.w * a.xyz + cross(a.xyz, b.xyz), a.w * b.w - dot(a.xyz, b.xyz));
}
float3 kQuatRotate(float4 q, float3 v)
{
    float3 t = cross(q.xyz, v);
    return v + 2.0f * cross(q.xyz, t + q.w * v);
}
float4 kQuatRollZ(float angle) { return float4(0.0f, 0.0f, sin(angle * 0.5f), cos(angle * 0.5f)); }

bool kFinite(float3 v) { return !any(isnan(v)) && !any(isinf(v)); }

// True when a sphere is outside the view (the game's test, in the same order).
bool kCulled(float3 center, float radius)
{
    for (uint i = 0; i < 5; i++) {
        float4 plane = kUpdateF4(KU_FRUSTUM_PLANES + i * 16);
        if (dot(center, plane.xyz) + plane.w > radius)
            return true;
    }
    return false;
}

// 1/131072 m fixed point, as the game converts world positions.
float3 kCameraRelative(float3 world)
{
    int3 fixedWorld = int3(round(world * 131072.0f));
    int3 camera = int3(kUpdateU(KU_CAMERA_WORLD_POSITION), kUpdateU(KU_CAMERA_WORLD_POSITION + 4), kUpdateU(KU_CAMERA_WORLD_POSITION + 8));
    return float3(fixedWorld - camera) * 7.62939453e-06f;
}

// One 32-bit half float from f.
uint kHalf(float f) { return f32tof16(f) & 0xffffu; }

// The working set of one particle: what every entry reads first.
struct KilnParticle
{
    uint chunk;       // byte offset of this entry's VfxChunkUpdateBuffer
    uint phase;       // KP_*
    uint id;          // index in this entry's state array
    uint state;       // byte offset of its state in g_chunkStateDataBuffer
    uint emitterIndex;
    uint parentEmitter;
    uint instance;
    uint rng;
    uint tick;
    uint params;      // byte offset of the parameter block in g_resourceDataBuffer
};

// Returns false when this invocation has no particle.
bool kBegin(uint invocation, uint stateSize, out KilnParticle p)
{
    p = (KilnParticle)0;
    p.chunk = (g_push.data >> 2) * KC_STRIDE;
    p.phase = g_push.data & 3u;
    if (invocation >= kCounter(g_chunkUpdate.Load(p.chunk + KC_ACTIVE_COUNTER)))
        return false;
    p.id = kStateU(g_chunkUpdate.Load(p.chunk + KC_OLD_ACTIVE) + 4 * invocation);
    p.state = g_chunkUpdate.Load(p.chunk + KC_PARTICLE_STATES) + stateSize * p.id;
    p.emitterIndex = kStateU(p.state + KB_EMITTER_INDEX);
    p.parentEmitter = kStateU(p.state + KB_PARENT_EMITTER);
    p.instance = kStateU(p.state + KB_INSTANCE);
    p.rng = kStateU(p.state + KB_RNG) + invocation;
    p.tick = kStateU(p.state + KB_TICK);
    p.params = g_chunkUpdate.Load(p.chunk + KC_EVALUATE_PARAMETERS);
    return true;
}

// Ends the tick for a particle: state header, then stays active or goes back to the free list. A draw-only dispatch keeps
// every particle and writes nothing, as the game's modules do. Roots have no parent counter to give back.
void kEnd(KilnParticle p, bool alive, bool isRoot)
{
    if (alive || p.phase == KP_DRAW_ONLY) {
        if (p.phase != KP_DRAW_ONLY) {
            kStorePutU(p.state + KB_RNG, p.rng);
            kStorePutU(p.state + KB_TICK, p.tick + 1);
        }
        uint n = kCounterAdd(g_chunkUpdate.Load(p.chunk + KC_NEXT_ACTIVE_COUNTER), 1u);
        kStorePutU(g_chunkUpdate.Load(p.chunk + KC_NEW_ACTIVE) + 4 * n, p.id);
    } else {
        uint n = kCounterAdd(g_chunkUpdate.Load(p.chunk + KC_FREE_COUNTER), 1u);
        kStorePutU(g_chunkUpdate.Load(p.chunk + KC_FREE_INDICES) + 4 * n, p.id);
        if (!isRoot)
            kCounterAdd(g_emitterStates.Load(p.parentEmitter * KM_STRIDE + KM_PARTICLE_COUNTER), KILN_MINUS_ONE);
    }
}

#endif
