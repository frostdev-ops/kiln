// SPDX-License-Identifier: MIT
// Pure helpers of the particle kit: arithmetic only, no buffer, no binding. Kiln's own modules (through kiln_vfx.hlsl) and a mod's
// hook module (through kiln_hooks.hlsl) both include this file, so a hook draws random numbers and turns vectors exactly as Kiln does.
#ifndef KILN_MATH_HLSL
#define KILN_MATH_HLSL

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

// Per-play parameters (KX_*), read from the instance's user block by kiln_vfx.hlsl's kPlay. They are factors on what the block says,
// all 1 (white) when the block isn't Kiln's.
struct KilnPlay
{
    float3 tint;       // linear
    float  alpha;
    float  size;
    float  speed;
    float  brightness;
    float  count;
};
float kSrgbToLinear(float c) { return c <= 0.04045f ? c / 12.92f : pow((c + 0.055f) / 1.055f, 2.4f); }

#endif
