# SPDX-License-Identifier: MIT
"""Draws the particles a compute tick produced, offscreen, from the draw records as real indirect arguments.

With the game installed, its own Transparent_Forward vertex and fragment shaders draw them, read from its files at run time.
Without it, render_fallback.hlsl does (it decodes the same records, with no fog or lighting). PNGs go to the results folder.
"""
import ctypes
import json
import math
import struct
import subprocess
import time
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

import gpu
from gpu import FMT, BLEND, CMP, CULL
from harness import KU, MESHES, P, Entry, emitter_block, sprite_block

HERE = Path(__file__).resolve().parent
BUILD = HERE.parent / "build"
SIZE = 512
FOV = math.radians(60)
NEAR = 0.1
IMAGE, SAMPLER = 7, 2


def quad_mesh():
    """The XY quad, normal -Z, as VfxMeshLayout and g_meshData expect (28 B vertices, then u16 indices)."""
    n, t, c = 0x00200400, 0x200803ff, 0xffffffff
    verts = [(-0.5, 0.5, 0.0, 0.0), (-0.5, -0.5, 0.0, 1.0), (0.5, -0.5, 1.0, 1.0), (0.5, 0.5, 1.0, 0.0)]
    b = bytearray()
    for x, y, u, v in verts:
        b += struct.pack("<3f2e3I", x, y, 0.0, u, v, n, t, c)
    b += struct.pack("<6H", 0, 1, 2, 2, 3, 0)
    b += bytes(16)
    return bytes(b)


def flipbook_atlas(cell=128, n=4):
    """16 numbered cells, each with a white block in its top-left corner: a mirrored or flipped sprite shows at once."""
    im = Image.new("RGBA", (cell * n, cell * n), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    for i in range(n * n):
        x, y = (i % n) * cell, (i // n) * cell
        hue = i / (n * n)
        col = tuple(int(255 * (0.5 + 0.5 * math.cos(6.283 * (hue + k / 3)))) for k in range(3)) + (255,)
        d.rectangle([x + 4, y + 4, x + cell - 5, y + cell - 5], fill=col)
        d.rectangle([x + 8, y + 8, x + 36, y + 36], fill=(255, 255, 255, 255))
        d.text((x + cell // 2 - 18, y + cell // 2 - 22), f"{i}", fill=(0, 0, 0, 255), font_size=44)
    return np.asarray(im, dtype=np.uint8).tobytes(), cell * n


def soft_dot(size=64):
    y, x = np.mgrid[0:size, 0:size]
    r = np.hypot(x - size / 2 + 0.5, y - size / 2 + 0.5) / (size / 2)
    a = np.clip(1.0 - r, 0.0, 1.0) ** 1.5
    px = np.zeros((size, size, 4), dtype=np.uint8)
    px[..., :3] = 255
    px[..., 3] = (a * 255).astype(np.uint8)
    return px.tobytes(), size


def view_matrix(orientation):
    """Row-major rotation that takes world directions to camera axes: the inverse of the camera's quaternion."""
    x, y, z, w = orientation
    # conjugate rotation matrix (rows are the camera's axes)
    return [[1 - 2 * (y * y + z * z), 2 * (x * y + w * z), 2 * (x * z - w * y)],
            [2 * (x * y - w * z), 1 - 2 * (x * x + z * z), 2 * (y * z + w * x)],
            [2 * (x * z + w * y), 2 * (y * z - w * x), 1 - 2 * (x * x + y * y)]]


def clip_matrix(orientation):
    """cameraCenteredToClipSpace: left-handed, x right, y up, z forward, reverse-Z. The vertex shader flips y for Vulkan."""
    f = 1.0 / math.tan(FOV / 2)
    v = view_matrix(orientation)
    r0 = [f * a for a in v[0]] + [0.0]
    r1 = [f * a for a in v[1]] + [0.0]
    r2 = [0.0, 0.0, 0.0, NEAR]
    r3 = list(v[2]) + [0.0]
    return r0, r1, r2, r3


class Renderer:
    def __init__(self, dev, res):
        self.dev = dev
        self.res = res
        self.compile_ms = {}
        lib = dev.lib
        lib.vg_graphics_pipeline.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_size_t, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_size_t, ctypes.c_char_p,
                                             ctypes.c_int, ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_int), ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                             ctypes.c_int, ctypes.c_int, ctypes.POINTER(ctypes.c_double)]
        lib.vg_image.argtypes = [ctypes.c_uint32, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32, ctypes.c_int, ctypes.c_char_p]
        lib.vg_cmd_render.argtypes = [ctypes.c_int, ctypes.POINTER(ctypes.c_int), ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                      ctypes.c_uint32, ctypes.c_uint32, ctypes.POINTER(ctypes.c_float)]
        self.mesh_id, view = dev.buffer(len(quad_mesh()))
        view[:] = quad_mesh()
        self.ubo_id, self.ubo = dev.buffer(2320)
        self.ubo2_id, self.ubo2 = dev.buffer(96)
        self.colors = []
        self.real_shaders = gpu.GPC is not None
        if self.real_shaders:
            self._game_pipeline()
        else:
            self._fallback_pipeline()
        self.depth = dev.ok(lib.vg_image(SIZE, SIZE, 1, FMT["D32_sfloat"], 4, 2, None), "depth")
        self.colors = [dev.ok(lib.vg_image(SIZE, SIZE, 1, f, b, 1, None), "target") for f, b in self.targets]
        self.textures = {}

    def _targets(self, p):
        self.targets = [(FMT[c["format"]], 8 if "R16G16B16A16" in c["format"] else 1) for c in p["colorTargets"]]
        blend = []
        for c in p["colorTargets"]:
            blend += [1, BLEND[c["blendSourceFactor"]], BLEND[c["blendDestFactor"]], BLEND[c["alphaBlendSourceFactor"]], BLEND[c["alphaBlendDestFactor"]]]
        return blend

    def _game_pipeline(self):
        dev, gp, lib = self.dev, gpu.GPC, self.dev.lib
        g = gp.gpc["renderer/renderer.gpc"]
        self.p = [x for x in g["rasterPipelines"] if x["name"] == "Transparent_Forward"][0]
        layout_name = g["pipelineLayouts"][self.p["pipelineLayoutIndex"]]["debugName"]
        self.layout, self.names, _, _ = gp.build(dev, "renderer/renderer.gpc", layout_name, 0)
        vs = gp.module("renderer/renderer.gpc", self.p["vsShaderCode"])
        fs = gp.module("renderer/renderer.gpc", self.p["fsShaderCode"])
        blend = self._targets(self.p)
        self._build(vs, b"vs_main", fs, b"fs_main", blend, CMP[self.p["depthComparisonFunction"]], CULL[self.p["cullMode"]], 0 if self.p["windingOrder"] == "Ccw" else 1, "game Transparent_Forward")
        bind = lambda name, i: dev.ok(lib.vg_bind_buffer(self.layout, self.names[name][0], self.names[name][1], i), name)
        bind("g_constants", self.ubo_id)
        bind("g_transparentConstants", self.ubo2_id)
        bind("g_meshData", self.mesh_id)
        # Fog terms the fragment shader always reads: no scatter, full transmittance.
        white = dev.ok(lib.vg_image(1, 1, 1, FMT["R8G8B8A8_unorm"], 4, 0, bytes([255] * 4)), "white")
        black = dev.ok(lib.vg_image(1, 1, 1, FMT["R8G8B8A8_unorm"], 4, 0, bytes([0, 0, 0, 255])), "black")
        white3 = dev.ok(lib.vg_image(2, 2, 2, FMT["R8G8B8A8_unorm"], 4, 0, bytes([255] * 32)), "white3")
        self.white3 = white3
        black3 = dev.ok(lib.vg_image(2, 2, 2, FMT["R8G8B8A8_unorm"], 4, 0, bytes([0, 0, 0, 255] * 8)), "black3")
        img = lambda name, i: dev.ok(lib.vg_bind_image(self.layout, self.names[name][0], self.names[name][1], 0, i), name)
        img("g_fog3Transmittance", white)
        img("g_fog3Luminance", black)
        img("g_fog3DepthMoments", black)
        img("g_fog3ViewVolumeTransmittance", white3)
        img("g_fog3ViewVolumeScatter", black3)
        img("g_fog3ViewVolumeAmbientAndPointLightRadiance", black3)
        img("g_fog3ViewVolumeDirLightVisibility", black3)
        self.bindless = 4
        self.instance_binding = self.names["g_instanceData"][:2]
        for name in self.names:
            pass
        lib.vg_bind_sampler(self.layout, 4, 1, SAMPLER)

    def _fallback_pipeline(self):
        dev, lib = self.dev, self.dev.lib
        src = HERE / "render_fallback.hlsl"
        out = {}
        for stage, entry in (("vertex", "vs_main"), ("fragment", "fs_main")):
            f = Path(gpu.OUT) / f"fallback_{entry}.spv"
            subprocess.run(["glslc", "--target-env=vulkan1.2", "-x", "hlsl", f"-fshader-stage={stage}", f"-fentry-point={entry}", str(src), "-o", str(f)], check=True)
            out[entry] = f.read_bytes()
        # set 0: instance records, mesh data, constants; set 1: texture, sampler
        self.layout = dev.layout([[(7, 1, 1), (7, 1, 1), (6, 1, 1)], [(2, 32768, 1), (0, 256, 1)]], 0)
        self.targets = [(FMT["R16G16B16A16_sfloat"], 8)]
        blend = [1, BLEND["One"], BLEND["InvSrcAlpha"], BLEND["One"], BLEND["InvSrcAlpha"]]
        self._build(out["vs_main"], b"vs_main", out["fs_main"], b"fs_main", blend, CMP["Greater"], CULL["Back"], 0, "fallback")
        dev.ok(lib.vg_bind_buffer(self.layout, 0, 1, self.mesh_id), "mesh")
        dev.ok(lib.vg_bind_buffer(self.layout, 0, 2, self.ubo_id), "constants")
        self.bindless = 1
        self.instance_binding = (0, 0)
        lib.vg_bind_sampler(self.layout, 1, 1, SAMPLER)

    def _build(self, vs, vse, fs, fse, blend, cmp_, cull, front, label):
        n = len(self.targets)
        fm = (ctypes.c_int * n)(*[t[0] for t in self.targets])
        bl = (ctypes.c_int * len(blend))(*blend)
        ms = ctypes.c_double()
        self.pipeline = self.dev.lib.vg_graphics_pipeline(self.layout, bytes(vs), len(vs), vse, bytes(fs), len(fs), fse, n, fm, bl, FMT["D32_sfloat"], cmp_, 0, cull, front, ctypes.byref(ms))
        self.dev.ok(self.pipeline, label)
        self.compile_ms[label] = ms.value
        print(f"graphics pipeline {label}: {ms.value:.1f} ms", flush=True)

    def texture(self, pixels, size):
        i = self.dev.ok(self.dev.lib.vg_image(size, size, 1, FMT["R8G8B8A8_srgb"], 4, 0, pixels), "texture")
        self.dev.ok(self.dev.lib.vg_bind_image(self.layout, self.bindless, 0, IMAGE, i), "bindless")
        return i

    def constants(self, orientation):
        rows = clip_matrix(orientation)
        for i, r in enumerate(rows):
            struct.pack_into("<4f", self.ubo, 2160 + i * 16 if self.real_shaders else i * 16, *r)
        if self.real_shaders:
            struct.pack_into("<f", self.ubo, 1952 + 16, 1.0)     # the exposure the fragment shader multiplies by
            struct.pack_into("<3f", self.ubo, 1824, 1.0, 1.0, 1.0)
            struct.pack_into("<2f", self.ubo2, 64, NEAR, 1.0)
            struct.pack_into("<2f", self.ubo2, 72, 1.0 / SIZE, 1.0 / SIZE)

    def draw(self, world, list_binding, count, clear=(0.03, 0.04, 0.06, 1.0)):
        """Draws `count` records of a world's list."""
        dev, lib = self.dev, self.dev.lib
        ids = world.gb[(1, list_binding)]
        s, b = self.instance_binding
        dev.ok(lib.vg_bind_buffer(self.layout, s, b, ids), "instances")
        dev.begin()
        cs = (ctypes.c_int * len(self.colors))(*self.colors)
        cl = (ctypes.c_float * 4)(*clear)
        dev.ok(lib.vg_cmd_render(len(self.colors), cs, self.depth, self.pipeline, self.layout, self.mesh_id, ids, count, 176, cl), "render")
        return dev.submit()

    def image(self):
        out = (ctypes.c_uint8 * (SIZE * SIZE * 8))()
        self.dev.ok(self.dev.lib.vg_read_image(self.colors[0], out), "read")
        return np.frombuffer(out, dtype=np.float16).reshape(SIZE, SIZE, 4).astype(np.float32)

    def save(self, linear, name):
        rgb = np.clip(np.nan_to_num(linear[..., :3]), 0.0, 1.0)
        srgb = np.where(rgb <= 0.0031308, rgb * 12.92, 1.055 * rgb ** (1 / 2.4) - 0.055)
        path = self.res / name
        Image.fromarray((srgb * 255 + 0.5).astype(np.uint8), "RGB").save(path)
        print("wrote", path, flush=True)
        return path


def tile_world(WorldClass, n, sprite_for, em, **kw):
    """n instances, each its own pair (root, child), each root on its own instance slot."""
    emitter, sprite = (BUILD / "kiln_emitter.spv").read_bytes(), (BUILD / "kiln_sprite.spv").read_bytes()
    entries = []
    for i in range(n):
        entries += [Entry(emitter, depth=0, child=2 * i + 1, state_size=P["KE_STATE_SIZE"], params=em(i)),
                    Entry(sprite, depth=1, parent=2 * i, max_count=kw.get("max_count", 8), state_size=P["KS_STATE_SIZE"], params=sprite_for(i))]
    w = WorldClass(entries, n_instances=n, **{k: v for k, v in kw.items() if k != "max_count"})
    for i in range(n):
        w.assign_instance(2 * i, i)
        w.buf[(1, 17)].data[32:64] = struct.pack("<4I4f", 0, 112 // 2, 6, 4, 0.0, 0.0, 0.0, 0.7071068)
    return w


def set_camera(world, orientation):
    u = world.buf[(1, 0)]
    for i, v in enumerate(orientation):
        u.put_f32(KU["camera_orientation"] + i * 4, v)


def quat(axis, degrees):
    a = math.radians(degrees) / 2
    return (axis[0] * math.sin(a), axis[1] * math.sin(a), axis[2] * math.sin(a), math.cos(a))


def qmul(a, b):
    return (a[3] * b[0] + b[3] * a[0] + a[1] * b[2] - a[2] * b[1],
            a[3] * b[1] + b[3] * a[1] + a[2] * b[0] - a[0] * b[2],
            a[3] * b[2] + b[3] * a[2] + a[0] * b[1] - a[1] * b[0],
            a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2])


def rotate(q, v):
    t = (2 * (q[1] * v[2] - q[2] * v[1]), 2 * (q[2] * v[0] - q[0] * v[2]), 2 * (q[0] * v[1] - q[1] * v[0]))
    return (v[0] + q[3] * t[0] + q[1] * t[2] - q[2] * t[1], v[1] + q[3] * t[1] + q[2] * t[0] - q[0] * t[2], v[2] + q[3] * t[2] + q[0] * t[1] - q[1] * t[0])


def run(dev, WorldClass, res):
    r = Renderer(dev, res)
    results = dict(device=dev.info, shaders="game Transparent_Forward" if r.real_shaders else "fallback", compile_ms=r.compile_ms, checks={}, time=time.ctime())
    check = results["checks"]

    # ---- 1. Upright flipbook grid: the white block must land top-left of every sprite, digit readable, in frame order.
    atlas, size = flipbook_atlas()
    r.texture(atlas, size)
    cols, rows = 4, 4

    def grid(orientation, name):
        r.constants(orientation)
        n = cols * rows

        def sprite(i):
            # After 1 s, fps = i + 0.5 frames per second puts this sprite on frame i.
            return sprite_block(life=(100, 100), speed=(0, 0), size=[1.1] * 8, color=[(1, 1, 1)] * 8, flipbook=4 | 4 << 16, fps=i + 0.5, image=IMAGE, sampler=SAMPLER)
        w = tile_world(WorldClass, n, sprite, lambda i: emitter_block(duration=0.01, burst=1), max_count=1, cap=256)
        set_camera(w, orientation)
        for i in range(n):
            local = (1.2 * (i % cols - 1.5), 1.2 * (1.5 - i // cols), 4.5)
            w.set_instance(position=rotate(orientation, local), slot=i, flags=1)
        for _ in range(60):
            w.tick()
            w.check()
        count = w.counter(w.draw_counters["count"])
        assert count == n, f"{count} draws"
        d = w.draws()[0]
        print("first record", {k: d[k] for k in ("indexCount", "instanceCount", "indexOffset", "vertexOffset", "instanceOffset", "meshIndex", "image", "imageSampler", "flags", "flipbookTime", "encodedFlipbookSize")}, flush=True)
        gpu_ms = r.draw(w, 3, n)
        im = r.image()
        r.save(im, name)
        return im, w, gpu_ms

    im, w, ms = grid((0.0, 0.0, 0.0, 1.0), "billboards_identity.png")
    # Each sprite covers a known square. Find its centre and the corner marker.
    f = 1.0 / math.tan(FOV / 2)
    ok_blocks = ok_cover = 0
    for i in range(16):
        x, y, z = 1.2 * (i % 4 - 1.5), 1.2 * (1.5 - i // 4), 4.5
        sx, sy = (x * f / z * 0.5 + 0.5) * SIZE, (0.5 - y * f / z * 0.5) * SIZE
        half = 0.55 * f / z * 0.5 * SIZE
        # the block sits at 8..36 of 128 in the top-left of the cell
        bx, by = int(sx - half + half * 2 * 22 / 128), int(sy - half + half * 2 * 22 / 128)
        tl = im[by, bx, :3]
        # the mirrored positions must not be white
        tr = im[by, int(sx + half - half * 2 * 22 / 128), :3]
        bl = im[int(sy + half - half * 2 * 22 / 128), bx, :3]
        if tl.min() > 0.7 and tr.min() < 0.7 and bl.min() < 0.7:
            ok_blocks += 1
        if im[int(sy), int(sx - half * 0.7), :3].max() > 0.05:
            ok_cover += 1
    check["billboards face the camera, upright, not mirrored (identity)"] = f"{ok_blocks}/16 corner markers top-left, {ok_cover}/16 sprites drawn"
    results["draw_gpu_ms"] = ms

    q = qmul(qmul(quat((0, 1, 0), 40), quat((1, 0, 0), -25)), quat((0, 0, 1), 20))
    grid(q, "billboards_rotated_camera.png")

    # ---- 2. Blending: additive adds; alpha covers.
    dot, dsize = soft_dot()
    r.texture(dot, dsize)
    r.constants((0.0, 0.0, 0.0, 1.0))
    solid = bytes([255, 255, 255, 255] * 16)
    r.texture(solid, 4)

    def blend(draw_flags, name):
        cs = [(1.0, 0.1, 0.1), (0.1, 1.0, 0.1), (0.1, 0.1, 1.0)]
        pos = [(-0.5, 0.25, 4.0), (0.5, 0.25, 4.0), (0.0, -0.45, 4.0)]
        w = tile_world(WorldClass, 3, lambda i: sprite_block(life=(100, 100), speed=(0, 0), size=[2.0] * 8, color=[cs[i]] * 8, alpha=[0.75] * 8, draw_flags=draw_flags, image=IMAGE, sampler=SAMPLER),
                       lambda i: emitter_block(duration=0.01, burst=1), max_count=1, cap=16)
        set_camera(w, (0.0, 0.0, 0.0, 1.0))
        for i in range(3):
            w.set_instance(position=pos[i], slot=i, flags=1)
        for _ in range(2):
            w.tick()
        r.draw(w, 3, 3)
        im = r.image()
        r.save(im, name)
        return im[SIZE // 2 + 10, SIZE // 2].copy()

    add = blend(P["KS_DRAW_ADDITIVE"], "blend_additive.png")
    over = blend(0, "blend_alpha.png")
    check["additive centre pixel (all three overlap)"] = [round(float(x), 3) for x in add[:3]]
    check["alpha centre pixel"] = [round(float(x), 3) for x in over[:3]]
    check["additive adds, alpha covers"] = bool(add[:3].min() > over[:3].min())

    # ---- 3. Depth order: the sort keys through the game's own writeback shader (the sort itself is done here, ascending).
    if r.real_shaders:
        wb = gpu.GPC.gpc["vfx/vfx.gpc"]
        layout, names, _, _ = gpu.GPC.build(dev, "vfx/vfx.gpc", "VfxTransparentDrawWritebackLayout", 0)
        pipe = dev.compute(layout, gpu.GPC.module("vfx/vfx.gpc", [x for x in wb["computePipelines"] if x["name"] == "VfxTransparentDrawWriteback"][0]["computeShader"]), label="game VfxTransparentDrawWriteback")
        cs = [(1.0, 0.1, 0.1), (0.1, 1.0, 0.1), (0.1, 0.1, 1.0)]
        depths = [3.0, 6.0, 9.0]   # red nearest
        w = tile_world(WorldClass, 3, lambda i: sprite_block(life=(100, 100), speed=(0, 0), size=[1.0 * depths[i]] * 8, color=[cs[i]] * 8, alpha=[1.0] * 8, image=IMAGE, sampler=SAMPLER),
                       lambda i: emitter_block(duration=0.01, burst=1), max_count=1, cap=16)
        set_camera(w, (0.0, 0.0, 0.0, 1.0))
        for i in range(3):
            w.set_instance(position=(0.0, 0.0, depths[i]), slot=i, flags=1)
        for _ in range(2):
            w.tick()
        sort = w.buf[(1, 4)]
        pairs = sorted((sort.u32(k * 8), sort.u32(k * 8 + 4)) for k in range(3))
        sorted_id, sorted_view = dev.buffer(8 * 3)
        for k, (key, index) in enumerate(pairs):
            struct.pack_into("<II", sorted_view, k * 8, key, index)
        dest_id, dest_view = dev.buffer(176 * 16)
        shader_id, shader_view = dev.buffer(4)
        struct.pack_into("<I", shader_view, 0, w.draw_counters["count"])
        for name, i in (("g_shaderData", shader_id), ("g_sortData", sorted_id), ("g_sourceData", w.gb[(1, 3)]), ("g_destinationData", dest_id), ("g_counterBuffer", w.gb[(0, 2)])):
            dev.ok(dev.lib.vg_bind_buffer(layout, names[name][0], names[name][1], i), name)
        dev.begin()
        dev.lib.vg_cmd_bind_compute(pipe, layout)
        dev.lib.vg_cmd_dispatch(1, 1, 1)
        dev.lib.vg_cmd_barrier()
        dev.submit()
        assert [struct.unpack_from("<I", dest_view, 176 * k + 16)[0] for k in range(3)] == [0, 1, 2], "writeback rewrites instanceOffset"
        s_, b_ = r.instance_binding
        dev.ok(dev.lib.vg_bind_buffer(r.layout, s_, b_, dest_id), "instances")
        dev.begin()
        cl = (ctypes.c_float * 4)(0.03, 0.04, 0.06, 1.0)
        col = (ctypes.c_int * len(r.colors))(*r.colors)
        dev.ok(dev.lib.vg_cmd_render(len(r.colors), col, r.depth, r.pipeline, r.layout, r.mesh_id, dest_id, 3, 176, cl), "render")
        dev.submit()
        im = r.image()
        r.save(im, "sorted_alpha.png")
        c = im[SIZE // 2, SIZE // 2, :3]
        check["sorted through the game's writeback: nearest on top"] = dict(order_far_to_near=[p[1] for p in pairs], centre=[round(float(x), 3) for x in c], red_on_top=bool(c[0] > c[1] and c[0] > c[2]))

    # ---- 4. A real burst over time: emitter, spread, gravity, curves.
    r.texture(dot, dsize)
    n_curve = P["KS_CURVE_SAMPLES"]
    sprite = sprite_block(life=(0.8, 1.4), speed=(2.5, 5.0), spread=P["KS_SPREAD_CONE"], angle=math.radians(35), accel=(0, -4.0, 0), drag=0.2,
                          size=[0.28 - 0.26 * i / (n_curve - 1) for i in range(n_curve)], alpha=[1.0 - i / (n_curve - 1) for i in range(n_curve)],
                          color=[(1.0 * 3, (0.8 - 0.6 * i / (n_curve - 1)) * 3, (0.2 - 0.2 * i / (n_curve - 1)) * 3) for i in range(n_curve)],
                          draw_flags=P["KS_DRAW_ADDITIVE"], image=IMAGE, sampler=SAMPLER)
    emitter, spr = (BUILD / "kiln_emitter.spv").read_bytes(), (BUILD / "kiln_sprite.spv").read_bytes()
    w = WorldClass([Entry(emitter, depth=0, child=1, state_size=P["KE_STATE_SIZE"], params=emitter_block(duration=0.6, rate=200.0, burst=64)),
                    Entry(spr, depth=1, parent=0, max_count=512, state_size=P["KS_STATE_SIZE"], params=sprite)], cap=1024, position=(0.0, -2.0, 7.0))
    w.buf[(1, 17)].data[32:64] = struct.pack("<4I4f", 0, 112 // 2, 6, 4, 0.0, 0.0, 0.0, 0.7071068)
    set_camera(w, (0.0, 0.0, 0.0, 1.0))
    frames = []
    counts = []
    for t in range(1, 91):
        w.tick()
        w.check()
        if t in (1, 15, 30, 45, 60, 90):
            n = w.counter(w.draw_counters["count"])
            counts.append((t, n))
            r.draw(w, 3, n, clear=(0.0, 0.0, 0.0, 1.0))
            frames.append(r.image())
    montage = np.concatenate([np.concatenate(frames[:3], axis=1), np.concatenate(frames[3:], axis=1)], axis=0)
    r.save(montage, "burst_over_time.png")
    results["burst"] = dict(counts=counts)
    check["burst draws through ticks"] = counts

    # ---- 5. The same path with the game's own emitter and particle: if this draws, the path treats records as the game does.
    if r.real_shaders:
        gp = gpu.GPC
        tv = gp.types.get("keen::VfxResource")["hash"]
        k = [x for x in gp.kfc.resources_of_type(tv) if x.guid.startswith("54347d4a")]
        if k:
            v = gp.dec.decode(tv, gp.kfc.read(k[0]))
            g = v["gpuResourceData"]
            raw = bytearray(bytes.fromhex(g["bytes"]) if isinstance(g, dict) else bytes(g))
            for x in v["meshes"]:
                struct.pack_into("<I", raw, x["patchPoint"]["resourceDataRange"]["start"], 1)
            for x in v["images"]:
                struct.pack_into("<II", raw, x["patchPoint"]["resourceDataRange"]["start"], IMAGE, SAMPLER)
            ents = []
            for i, picks in ((2, 0), (3, 1)):
                p = v["particles"][i]
                h = p["strippedCode"]["hash"]
                e = Entry(gp.kfc.read_content(gp.CH(h["size"], h["hash0"], h["hash1"], h["hash2"])), depth=p["depth"], parent=0 if p["depth"] else 0xffffffff, child=1 if p["depth"] == 0 else 0,
                          max_count=p["maxCount"], state_size=p["stateSizeInBytes"])
                e.res_start = p["evaluateParametersGpuResourceDataRange"]["start"]
                ents.append(e)
            r.texture(dot, dsize)
            w = WorldClass(ents, resource=bytes(raw), cap=512, position=(0.0, -1.0, 4.0))
            w.buf[(1, 17)].data[32:64] = struct.pack("<4I4f", 0, 112 // 2, 6, 4, 0.0, 0.0, 0.0, 0.7071068)
            set_camera(w, (0.0, 0.0, 0.0, 1.0))
            for t in range(90):
                w.tick()
                w.check()
            n = w.counter(w.draw_counters["count"])
            r.draw(w, 3, n)
            r.save(r.image(), "game_module.png")
            check["the game's own emitter and sprite, same path"] = f"{n} draws"

    results["compile_ms"] = r.compile_ms
    (res / "render.json").write_text(json.dumps(results, indent=1))
    print(json.dumps(results["checks"], indent=1), flush=True)
