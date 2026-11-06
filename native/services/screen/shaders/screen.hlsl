// SPDX-License-Identifier: MIT
// kiln.screen's one pass: a full-screen triangle over a copy of the frame the game is about to present. Every effect playing goes through
// the same loop, in order, so any number of them costs one copy and one draw. The fields are filled by core.cpp; the maths is checked
// against a reference in tests/test_gpu.py, on real GPUs.
//
// Colours work in linear light with Rec.709 primaries, whatever the swapchain holds: decode, grade, encode.

#define KILN_SCREEN_MAX_EFFECTS 8

struct Effect
{
    float4 grade;     // x exposure as a factor, y contrast, z saturation, w 1/gamma
    float4 balance;   // rgb white balance, as factors. w: unused
    float4 tint;      // rgb multiplies, a how much
    float4 vignette;  // rgb colour, a how much at the edge
    float4 shape;     // x where the vignette starts (0 centre, 1 corner), y how far it takes to reach full, w how much of this effect shows
    float4 fill;      // rgb colour, a how much
};

[[vk::binding(0, 0)]] cbuffer Frame
{
    float2 resolution;
    uint effectCount;
    uint transfer;    // 0 the view already gives linear light, 1 sRGB, 2 PQ with Rec.2020 primaries
    Effect effects[KILN_SCREEN_MAX_EFFECTS];
};

[[vk::binding(1, 0)]] Texture2D<float4> scene;

static const float3 Luma = float3(0.2126, 0.7152, 0.0722);
static const float MidGrey = 0.18;
static const float PqWhite = 10000.0 / 203.0;  // SDR white, 203 cd/m2 (ITU-R BT.2408), is 1.0 here

// The standard's matrices, row by row: Rec.709 linear to Rec.2020 linear, and back.
static const float3x3 ToRec2020 = float3x3(
    0.6274040, 0.3292820, 0.0433136,
    0.0690970, 0.9195400, 0.0113612,
    0.0163916, 0.0880132, 0.8955950);
static const float3x3 FromRec2020 = float3x3(
    1.6604910, -0.5876411, -0.0728499,
    -0.1245505, 1.1328999, -0.0083494,
    -0.0181508, -0.1005789, 1.1187297);

float3 srgb_decode(float3 v)
{
    return lerp(v / 12.92, pow((v + 0.055) / 1.055, 2.4), step(0.04045, v));
}

float3 srgb_encode(float3 c)
{
    c = saturate(c);
    return lerp(c * 12.92, 1.055 * pow(c, 1.0 / 2.4) - 0.055, step(0.0031308, c));
}

float3 pq_decode(float3 v)
{
    const float m1 = 0.1593017578125, m2 = 78.84375, c1 = 0.8359375, c2 = 18.8515625, c3 = 18.6875;
    float3 p = pow(saturate(v), 1.0 / m2);
    return pow(max(p - c1, 0.0) / (c2 - c3 * p), 1.0 / m1);
}

float3 pq_encode(float3 l)
{
    const float m1 = 0.1593017578125, m2 = 78.84375, c1 = 0.8359375, c2 = 18.8515625, c3 = 18.6875;
    float3 y = pow(saturate(l), m1);
    return pow((c1 + c2 * y) / (1.0 + c3 * y), m2);
}

float3 decode(float3 v)
{
    if (transfer == 1) return srgb_decode(v);
    if (transfer == 2) return mul(FromRec2020, pq_decode(v) * PqWhite);
    return v;
}

float3 encode(float3 c)
{
    if (transfer == 1) return srgb_encode(c);
    if (transfer == 2) return pq_encode(max(mul(ToRec2020, c), 0.0) / PqWhite);
    return c;
}

// HDR holds colours outside Rec.709, which are negative here. They keep their sign, so that a grade that changes nothing changes nothing.
float3 signed_pow(float3 v, float k)
{
    return sign(v) * pow(abs(v), k);
}

float3 apply(Effect e, float3 input, float edge)
{
    float3 c = input * e.grade.x * e.balance.rgb;
    c = MidGrey * signed_pow(c / MidGrey, e.grade.y);
    c = dot(c, Luma) + (c - dot(c, Luma)) * e.grade.z;
    c = signed_pow(c, e.grade.w);
    c = lerp(c, c * e.tint.rgb, e.tint.a);
    c = lerp(c, e.vignette.rgb, smoothstep(e.shape.x, e.shape.x + e.shape.y, edge) * e.vignette.a);
    c = lerp(c, e.fill.rgb, e.fill.a);
    return lerp(input, c, e.shape.w);
}

float4 vs_main(uint id : SV_VertexID) : SV_Position
{
    return float4(float(id & 1) * 4.0 - 1.0, float(id >> 1) * 4.0 - 1.0, 0.0, 1.0);
}

float4 ps_main(float4 position : SV_Position) : SV_Target0
{
    float4 source = scene.Load(int3(int2(position.xy), 0));
    float aspect = resolution.x / resolution.y;
    float2 away = (position.xy / resolution - 0.5) * float2(aspect, 1.0);
    float edge = length(away) / (0.5 * sqrt(aspect * aspect + 1.0));  // 0 at the centre, 1 at a corner
    float3 c = decode(source.rgb);
    for (uint i = 0; i < effectCount; ++i)
        c = apply(effects[i], c, edge);
    return float4(encode(c), source.a);
}
