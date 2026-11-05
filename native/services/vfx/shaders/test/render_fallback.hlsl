// SPDX-License-Identifier: MIT
// Draws TransparentInstanceData records when the game's own shaders aren't available to the test: same mesh decode, same
// instance fields, same rotation, a textured quad with flipbook and premultiplied output. No fog or lighting.
[[vk::binding(0, 0)]] ByteAddressBuffer g_instanceData;
[[vk::binding(1, 0)]] ByteAddressBuffer g_meshData;
[[vk::binding(2, 0)]] cbuffer Camera { float4 g_clip[4]; };
[[vk::binding(0, 1)]] Texture2D<float4> g_images[32768];
[[vk::binding(1, 1)]] SamplerState g_samplers[256];

struct VsOut {
    float4 position : SV_Position;
    [[vk::location(0)]] nointerpolation uint instance : INSTANCE;
    [[vk::location(1)]] float2 uv : UV;
};

float3 rotate(float4 q, float3 v) { return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v); }

VsOut vs_main(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    uint i = instance * 176, v = vertex * 28;
    float3 local = asfloat(g_meshData.Load3(v));
    uint packedUv = g_meshData.Load(v + 12);
    float4 q = asfloat(g_instanceData.Load4(i + 0x3c));
    float3 scale = asfloat(g_instanceData.Load3(i + 0x4c));
    float3 relative = asfloat(g_instanceData.Load3(i + 0x2c));
    float3 p = rotate(q, local * scale) + relative;
    float4 h = float4(p, 1.0);
    VsOut o;
    o.position = float4(dot(g_clip[0], h), -dot(g_clip[1], h), dot(g_clip[2], h), dot(g_clip[3], h));
    o.instance = instance;
    o.uv = float2(f16tof32(packedUv), f16tof32(packedUv >> 16));
    return o;
}

float4 fs_main(VsOut input) : SV_Target0
{
    uint i = input.instance * 176;
    int image = (int)g_instanceData.Load(i + 0x18), sampler_ = (int)g_instanceData.Load(i + 0x20);
    uint flags = g_instanceData.Load(i + 0x9c), flip = g_instanceData.Load(i + 0xac);
    float2 uv = input.uv;
    if (flip != 0) {
        uint cols = flip & 0xffff, rows = flip >> 16, total = cols * rows;
        uint frame = (uint)(frac(asfloat(g_instanceData.Load(i + 0xa8))) * total);
        uv = (float2(frame % cols, frame / cols) + uv) / float2(cols, rows);
    }
    float4 tex = float4(1, 1, 1, 1);
    if (image != 0)
        tex = g_images[image].Sample(g_samplers[sampler_], uv);
    float alpha = asfloat(g_instanceData.Load(i + 0x64)) * tex.a;
    float3 color = asfloat(g_instanceData.Load3(i + 0x58)) * tex.rgb * alpha;
    return float4(color, (flags & 1) != 0 ? 0.0 : alpha);
}
