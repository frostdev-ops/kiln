#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""MaterialBlock through the game's own shaders on a real GPU: headless, no game running, nothing but this process on the device.

    python3 tools/vfxcook/tests/material_gpu.py [--device NAME] [--out DIR]

Needs the game installed (read only), glslc and a Vulkan driver. It builds materials with kiln-vfxcook, patches them into a g_rfxMaterials buffer the way the game's loader does (a
handle at each image's dataOffset), and draws a full-screen triangle with the fragment shaders of Model_GBuffer_GpuCulling2_Instanced and
Model_Visibility_Discard_GpuCulling2_Instanced behind a small vertex shader of its own. Then it reads the G-buffer back: so the numbers below are what the game's shader made of Kiln's block
and Kiln's BC7, BC5 and BC1 textures.

What this can't show: the game's loader accepting them, the game's vertex shaders and culling, its lighting, or how it looks in the world. Exit code 0 means every check held.
"""
import argparse
import ctypes
import json
import math
import os
import struct
import subprocess
import sys
import tempfile
import zlib
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
ROOT = HERE.parents[2]
SHADERS = ROOT / "native" / "services" / "vfx" / "shaders"
sys.path.insert(0, str(SHADERS / "test"))

ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
ap.add_argument("--device", default="NVIDIA")
ap.add_argument("--out", help="where PNGs of the G-buffer go; default $KILN_OUT/material-gpu")
args = ap.parse_args()

KILN_OUT = Path(os.environ.get("KILN_OUT", Path(tempfile.gettempdir()) / "kiln-vfx-gpu"))
subprocess.run([str(SHADERS / "gpu" / "build.sh")], check=True, capture_output=True, env={**os.environ, "KILN_OUT": str(KILN_OUT)})

import gpu  # noqa: E402
from gpu import FMT  # noqa: E402
from test_vfxcook import DEC, Mod, cook, run  # noqa: E402
from kfc import material as checks  # noqa: E402

MATERIAL = "keen::RenderMaterialResource"
SIZE = 256
FORMAT_NUMBERS = {"BC1_RGB_unorm_block": (131, 8), "BC5_unorm_block": (141, 16), "BC7_unorm_block": (145, 16), "BC7_srgb_block": (146, 16)}
VK = {"R8G8B8A8_srgb": 43, "A2B10G10R10_unorm_pack32": 64, "R8G8B8A8_uint": 41, "R32_uint": 98, "R16G16_snorm": 78, "R8_unorm": 9, "D32_sfloat": 126}
GBUFFER = [("R8G8B8A8_srgb", 4), ("A2B10G10R10_unorm_pack32", 4), ("R8G8B8A8_uint", 4), ("R32_uint", 4), ("R16G16_snorm", 4), ("R8_unorm", 1)]
VS_GBUFFER = """#version 450
layout(location = 0) flat out uint o0;
layout(location = 1) out vec3 o1;
layout(location = 2) out vec3 o2;
layout(location = 3) out vec4 o3;
layout(location = 4) out vec4 o4;
layout(location = 5) out vec2 o5;
layout(location = 6) out vec4 o6;
layout(location = 7) out vec2 o7;
layout(location = 8) out vec3 o8;
layout(location = 9) out float o9;
void main() {
    vec2 uv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4(uv * 2.0 - 1.0, 0.5, 1.0);
    o0 = 0u;
    o1 = vec3(0.0);
    o2 = vec3(0.0, 0.0, 1.0);          // normal
    o3 = vec4(1.0, 0.0, 0.0, 1.0);     // tangent, handedness
    o4 = vec4(1.0);                    // vertex colour
    o5 = uv;
    o6 = vec4(0.0);
    o7 = vec2(0.0);
    o8 = vec3(0.0);
    o9 = 0.0;
}
"""
VS_VISIBILITY = """#version 450
layout(location = 0) flat out uint o0;
layout(location = 1) out vec4 o1;
layout(location = 2) out vec2 o2;
layout(location = 3) out vec3 o3;
void main() {
    vec2 uv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4(uv * 2.0 - 1.0, 0.5, 1.0);
    o0 = 0u;
    o1 = vec4(1.0);
    o2 = uv;
    o3 = vec3(0.0);
}
"""


def glsl(source, name):
    src, out = KILN_OUT / f"{name}.vert", KILN_OUT / f"{name}.spv"
    src.write_text(source)
    subprocess.run(["glslc", "--target-env=vulkan1.2", "-fshader-stage=vertex", str(src), "-o", str(out)], check=True)
    return out.read_bytes()


def png(w, h, fn):
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d))
    rows = bytearray()
    for y in range(h):
        rows.append(0)
        for x in range(w):
            rows += bytes(fn(x, y))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(bytes(rows))) + chunk(b"IEND", b"")


def to_linear(v):
    return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4


LEFT_ALBEDO, RIGHT_ALBEDO = (200, 50, 50), (50, 200, 90)
FILES = {
    "albedo.png": png(SIZE, SIZE, lambda x, y: LEFT_ALBEDO + (255,) if x < SIZE // 2 else RIGHT_ALBEDO + (64,)),
    "mr.png": png(SIZE, SIZE, lambda x, y: (0, 64, 255, 255) if x < SIZE // 2 else (0, 200, 0, 255)),
    "ao.png": png(SIZE, SIZE, lambda x, y: (128, 0, 0, 255)),
    "normal.png": png(SIZE, SIZE, lambda x, y: (128, 128, 0, 255) if x < SIZE // 2 else (200, 128, 0, 255)),
    "emissive.png": png(SIZE, SIZE, lambda x, y: (0, 0, 0, 255) if x < SIZE // 2 else (255, 255, 255, 255)),
}
MATERIALS = {
    "solid": {"tint": "#336699", "roughness": 0.25, "metallic": 0.75, "reflectance": 0.4},
    "metal": {"kind": "metal", "tint": "#c0c0c0"},
    "maps": {"albedo": "albedo.png", "metallicRoughness": "mr.png", "occlusion": "ao.png", "normal": "normal.png",
             "emissive": {"map": "emissive.png", "color": "#7fd4ff", "strength": 8, "exposure": 0.5}},
    "flip": {"tint": "#ffffff", "doubleSided": True},
    "keep": {"kind": "cutout", "albedo": "albedo.png", "alphaTest": 0.2},
    "cut": {"kind": "cutout", "albedo": "albedo.png"},
}

failures = 0


def check(what, got, want, tol=0.0):
    global failures
    ok = all(abs(g - w) <= tol for g, w in zip(got, want)) if isinstance(want, (list, tuple)) else abs(got - want) <= tol
    print(f"{'PASS' if ok else 'FAIL'} {what}: {got} (want {want}, within {tol})", flush=True)
    if not ok:
        failures += 1


tmp = tempfile.TemporaryDirectory()
c = cook(Mod(tmp.name, "gpu-test", None, raw_doc=json.dumps({"format": 1, "materials": MATERIALS}), files=FILES), tmp=tmp.name)
assert not c.report["errors"], c.report["errors"]

dev = gpu.Gpu(args.device)
lib = dev.lib
lib.vg_image_bc.argtypes = [ctypes.c_uint32, ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_char_p, ctypes.c_uint64]
lib.vg_graphics_pipeline.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_size_t, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_size_t, ctypes.c_char_p,
                                     ctypes.c_int, ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_int), ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                     ctypes.c_int, ctypes.c_int, ctypes.POINTER(ctypes.c_double)]
lib.vg_image.argtypes = [ctypes.c_uint32, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32, ctypes.c_int, ctypes.c_char_p]
lib.vg_cmd_render.argtypes = [ctypes.c_int, ctypes.POINTER(ctypes.c_int), ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_uint32, ctypes.c_uint32,
                              ctypes.POINTER(ctypes.c_float)]
gpc = gpu.GpcLayouts()
renderer = "renderer/renderer.gpc"
pipelines = {p["name"]: p for p in gpc.gpc[renderer]["rasterPipelines"]}


def world(layout, names):
    """The game's buffers for one material draw: everything else stays a null descriptor."""
    buffers = {}
    for name, size in (("g_textureFeedbackConstants", 256), ("g_textureFeedbackBuffer", 1 << 16), ("g_rfxMaterials", 244 * len(MATERIALS)), ("g_meshDrawData", 80 * len(MATERIALS)),
                       ("g_instanceData", 128), ("g_culledInstanceData", 8), ("g_constants", 2048)):
        if name not in names:
            continue
        i, view = dev.buffer(size)
        buffers[name] = view
        dev.ok(lib.vg_bind_buffer(layout, names[name][0], names[name][1], i), name)
    if "g_instanceData" in buffers:
        struct.pack_into("<I", buffers["g_instanceData"], 0x20, 0xffffffff)            # tintColor: none
        struct.pack_into("<I", buffers["g_instanceData"], 0x2c, 0xffffff00)            # the emissive factor: white
        struct.pack_into("<I", buffers["g_instanceData"], 0x44, 0xff)                  # not dissolving
    if "g_constants" in buffers:
        struct.pack_into("<f", buffers["g_constants"], 0x240 + 20, 1.0)                # lightFactors: the exposure the emissive follows
    return buffers


class Bound:
    """Textures of every material in the bindless set, and each block with its handles written in, as the loader does."""

    def __init__(self, layout, bindless_set, buffers):
        self.layout, self.buffers = layout, buffers
        self.next = 1
        self.index = {}
        for i, name in enumerate(MATERIALS):
            v = c.decoded(c.report["materials"][i]["guid"], MATERIAL)
            block = bytearray(checks.block_bytes(v))
            for image in v["images"]:
                t = image["texture"]
                data = c.blob(t["content"]["hash"])
                fmt, block_bytes = FORMAT_NUMBERS[t["format"]]
                tex = dev.ok(lib.vg_image_bc(t["width"], t["height"], fmt, t["levelCount"], block_bytes, data, len(data)), "texture")
                dev.ok(lib.vg_bind_image(layout, bindless_set, 0, self.next, tex), "bindless")
                struct.pack_into("<I", block, image["dataOffset"], self.next)
                self.next += 1
            buffers["g_rfxMaterials"][244 * i:244 * (i + 1)] = bytes(block)
            struct.pack_into("<I", buffers["g_meshDrawData"], 80 * i + 0xc, i)         # materialDataIndex
            self.index[name] = i

    def select(self, name):
        struct.pack_into("<II", self.buffers["g_culledInstanceData"], 0, self.index[name], 0)


def pipeline(layout, vs, fs, targets, front=0, cull=0):
    fm = (ctypes.c_int * len(targets))(*[VK[t[0]] for t in targets])
    blend = (ctypes.c_int * (5 * len(targets)))(*([0, 0, 0, 0, 0] * len(targets)))
    ms = ctypes.c_double()
    p = lib.vg_graphics_pipeline(layout, bytes(vs), len(vs), b"main", bytes(fs), len(fs), b"fs_main", len(targets), fm, blend, VK["D32_sfloat"], 7, 0, cull, front, ctypes.byref(ms))
    return dev.ok(p, "pipeline")


index_id, index = dev.buffer(16)
index[:6] = struct.pack("<3H", 0, 1, 2)
indirect_id, indirect = dev.buffer(20)
indirect[:20] = struct.pack("<5I", 3, 1, 0, 0, 0)


def targets_of(formats):
    return [dev.ok(lib.vg_image(SIZE, SIZE, 1, VK[f], b, 1, None), "target") for f, b in formats]


def draw(pipe, layout, colors, depth):
    cs = (ctypes.c_int * len(colors))(*colors)
    clear = (ctypes.c_float * 4)(0, 0, 0, 0)
    dev.begin()
    dev.ok(lib.vg_cmd_render(len(colors), cs, depth, pipe, layout, index_id, indirect_id, 1, 20, clear), "render")
    return dev.submit()


def read(image, bytes_per_pixel):
    out = (ctypes.c_uint8 * (SIZE * SIZE * bytes_per_pixel))()
    dev.ok(lib.vg_read_image(image, out), "read")
    return bytes(out)


def pixel(raw, x, y, stride=4):
    at = (y * SIZE + x) * stride
    return raw[at:at + stride]


def rgb9e5(v):
    e = (v >> 27) - 15 - 9
    return tuple((v >> s & 511) * 2.0 ** e for s in (0, 9, 18))


def linear_of(hex_colour):
    return tuple(to_linear(int(hex_colour[i:i + 2], 16) / 255) for i in (1, 3, 5))


def run_gbuffer():
    p = pipelines["Model_GBuffer_GpuCulling2_Instanced"]
    layout, names, _, _ = gpc.build(dev, renderer, "PbrModelRfxGBufferPipelineLayout", 0)
    fs = gpc.module(renderer, p["fsShaderCode"])
    buffers = world(layout, names)
    bound = Bound(layout, 4, buffers)
    vs = glsl(VS_GBUFFER, "material_gbuffer")
    pipe = pipeline(layout, vs, fs, GBUFFER)
    colors = targets_of(GBUFFER)
    depth = dev.ok(lib.vg_image(SIZE, SIZE, 1, VK["D32_sfloat"], 4, 2, None), "depth")

    def gbuffer(name, pipeline_id=pipe):
        bound.select(name)
        draw(pipeline_id, layout, colors, depth)
        return [read(t, b) for t, (_, b) in zip(colors, GBUFFER)]

    def values(g, x, y):
        a = pixel(g[0], x, y)
        n = struct.unpack("<I", pixel(g[1], x, y))[0]
        m = pixel(g[2], x, y)
        e = struct.unpack("<I", pixel(g[3], x, y))[0]
        return {"albedo": tuple(a[:3]), "ao": a[3], "nx": (n & 1023) / 1023, "ny": (n >> 10 & 1023) / 1023, "roughness": (n >> 20 & 1023) / 1023, "metallic": m[0], "reflectance": m[1],
                "translucency": m[2], "flags": m[3], "emissive": e}

    g = gbuffer("solid")
    v = values(g, 40, 40)
    check("solid: albedo, sRGB", v["albedo"], (0x33, 0x66, 0x99), 2)
    check("solid: roughness", v["roughness"], 0.25, 0.02)
    check("solid: metallic", v["metallic"], 0.75 * 255, 2)
    check("solid: reflectance", v["reflectance"], 0.4 * 255, 1)
    check("solid: no occlusion", v["ao"], 255, 0)
    check("solid: a flat normal stays the vertex's: octahedral x", v["nx"], 0.5, 0.01)
    check("solid: a flat normal stays the vertex's: octahedral y", v["ny"], 0.5, 0.01)
    check("solid: no emissive", v["emissive"], 0, 0)

    v = values(gbuffer("metal"), 40, 40)
    check("kind metal: metallic", v["metallic"], 255, 1)
    check("kind metal: roughness", v["roughness"], 0.35, 0.02)

    g = gbuffer("maps")
    left, right = values(g, 64, 128), values(g, 192, 128)
    check("maps: albedo left", left["albedo"], LEFT_ALBEDO, 3)
    check("maps: albedo right", right["albedo"], RIGHT_ALBEDO, 3)
    check("maps: metallicRoughness G to roughness", left["roughness"], 64 / 255, 0.02)
    check("maps: metallicRoughness G to roughness, right", right["roughness"], 200 / 255, 0.02)
    check("maps: metallicRoughness B to metallic", left["metallic"], 255, 2)
    check("maps: metallicRoughness B to metallic, right", right["metallic"], 0, 2)
    check("maps: occlusion R to the G-buffer's AO", left["ao"], 128, 3)
    check("maps: reflectance, the default", left["reflectance"], 0.5029 * 255, 1)
    # normal map right half: x = 200/255*2-1 along the tangent, so the octahedral x is 0.5 + x'/2 with x' over the L1 norm
    x = 200 / 255 * 2 - 1
    z = math.sqrt(1 - x * x)
    check("maps: normal map along the tangent", right["nx"], 0.5 + 0.5 * x / (x + z), 0.015)
    check("maps: flat half of the normal map", left["nx"], 0.5, 0.015)
    check("maps: emissive off where the map is black", left["emissive"], 0, 0)
    want = tuple(ch * 8 * (0.5 * 1.0 + 0.5 * 1.0) for ch in linear_of("#7fd4ff"))
    check("maps: emissive = map x colour x strength", rgb9e5(right["emissive"]), want, 0.08)

    # The triangle's area is negative in framebuffer coordinates: with front face counter-clockwise, this pipeline's, it shows its back.
    def z_bit(name, pipeline_id=pipe):
        return struct.unpack("<I", pixel(gbuffer(name, pipeline_id)[1], 40, 40))[0] >> 30
    front = pipeline(layout, vs, fs, GBUFFER, front=1)
    check("doubleSided, the back face: the normal turns away, its z bit goes", z_bit("flip"), 0, 0)
    check("doubleSided, the front face: its z bit stays", z_bit("flip", front), 2, 0)
    check("one sided, the back face: the shader leaves the normal", z_bit("solid"), 2, 0)

    # A picture, for looking at: albedo of the map material.
    res = Path(args.out) if args.out else KILN_OUT / "material-gpu"
    res.mkdir(parents=True, exist_ok=True)
    try:
        from PIL import Image
        g = gbuffer("maps")
        Image.frombytes("RGBA", (SIZE, SIZE), g[0]).convert("RGB").save(res / f"maps_albedo_{args.device}.png")
        print("wrote", res / f"maps_albedo_{args.device}.png")
    except ImportError:
        pass


def run_visibility():
    p = pipelines["Model_Visibility_Discard_GpuCulling2_Instanced"]
    layout, names, _, _ = gpc.build(dev, renderer, "PbrModelRfxDepthPrepassPipelineLayout", 0)
    fs = gpc.module(renderer, p["fsShaderCode"])
    buffers = world(layout, names)
    bound = Bound(layout, 4, buffers)
    vs = glsl(VS_VISIBILITY, "material_visibility")
    formats = [("R32_uint", 4)]
    pipe = pipeline(layout, vs, fs, formats)
    colors = targets_of(formats)
    depth = dev.ok(lib.vg_image(SIZE, SIZE, 1, VK["D32_sfloat"], 4, 2, None), "depth")

    def survives(name, x):
        bound.select(name)
        draw(pipe, layout, colors, depth)
        return struct.unpack("<I", pixel(read(colors[0], 4), x, 128))[0] != 0

    check("cutout at 0.5: alpha 1 stays", survives("cut", 64), 1, 0)
    check("cutout at 0.5: alpha 0.25 is cut", survives("cut", 192), 0, 0)
    check("cutout at 0.2: alpha 1 stays", survives("keep", 64), 1, 0)
    check("cutout at 0.2: alpha 0.25 stays: the block's cutoff, in the game's fixed 0.5", survives("keep", 192), 1, 0)


run_gbuffer()
run_visibility()
print(f"{'all checks held' if failures == 0 else str(failures) + ' checks failed'} on {dev.info}", flush=True)
lib.vg_shutdown()
sys.exit(1 if failures else 0)
