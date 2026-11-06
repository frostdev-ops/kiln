#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""kiln.screen's pass on a real GPU, headless: no window, no game. The same pass.cpp and core.cpp as kiln-screen.dll.

    native/services/screen/tests/build.sh
    python3 native/services/screen/tests/test_gpu.py --device NVIDIA --validate
    python3 native/services/screen/tests/test_gpu.py --device RADV --validate

Every image that comes out is compared with a reference written here in Python, not with what the shader computes. Failures are real calls that
return errors, made by a wrapped vkGetDeviceProcAddr. Every wait has a timeout: a timeout or a lost device outside a requested failure ends the
process with exit code 3. Results go to $KILN_OUT/screen-gpu/<device>/result.json.
"""
import argparse
import ctypes
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import time
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
SHADERS = HERE.parent / "shaders"
OUT = Path(os.environ.get("KILN_OUT", Path(tempfile.gettempdir()) / "kiln-screen-gpu"))

UNORM_BGRA, UNORM_RGBA, SRGB_BGRA, SRGB_RGBA, A2B10, FLOAT16 = 44, 37, 50, 43, 64, 97
CS_SRGB, CS_HDR10, CS_SCRGB = 0, 1000104008, 1000104002
TRANSFER_SRC, COLOR_ATTACHMENT = 0x1, 0x10
OOM, DEVICE_LOST, TIMEOUT = -2, -4, 2

ap = argparse.ArgumentParser()
ap.add_argument("--device", default="NVIDIA")
ap.add_argument("--validate", action="store_true", help="under VK_LAYER_KHRONOS_validation with synchronization validation. Stops if the layer is missing")
args = ap.parse_args()

fails = 0
results = {"checks": [], "cost": {}}


def check(ok, what):
    global fails
    print(("PASS " if ok else "FAIL ") + what, flush=True)
    results["checks"].append({"ok": bool(ok), "what": what})
    if not ok:
        fails += 1


class Effect(ctypes.Structure):
    _fields_ = [("size", ctypes.c_uint32), ("order", ctypes.c_int32), ("strength", ctypes.c_float), ("fade_in", ctypes.c_float), ("seconds", ctypes.c_float),
                ("fade_out", ctypes.c_float), ("exposure", ctypes.c_float), ("contrast", ctypes.c_float), ("saturation", ctypes.c_float), ("gamma", ctypes.c_float),
                ("temperature", ctypes.c_float), ("tint", ctypes.c_float * 4), ("vignette", ctypes.c_float * 4), ("vignette_radius", ctypes.c_float),
                ("vignette_softness", ctypes.c_float), ("fill", ctypes.c_float * 4)]


def effect(**kw):
    """kiln_screen_effect_defaults, then the fields asked for. A colour is (r, g, b, amount)."""
    e = Effect(size=ctypes.sizeof(Effect), strength=1, seconds=-1, contrast=1, saturation=1, gamma=1, vignette_radius=0.5, vignette_softness=0.5)
    e.tint[:] = [1, 1, 1, 0]
    for k, v in kw.items():
        if k in ("tint", "vignette", "fill"):
            getattr(e, k)[:] = v
        else:
            setattr(e, k, v)
    return e


# ---- the reference: the block's meaning, on numpy arrays of linear Rec.709 ------------------------------------------------------------------------
def srgb_decode(v):
    return np.where(v <= 0.04045, v / 12.92, ((v + 0.055) / 1.055) ** 2.4)


def srgb_encode(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055)


M1, M2, C1, C2, C3 = 0.1593017578125, 78.84375, 0.8359375, 18.8515625, 18.6875
TO_2020 = np.array([[0.6274040, 0.3292820, 0.0433136], [0.0690970, 0.9195400, 0.0113612], [0.0163916, 0.0880132, 0.8955950]])
FROM_2020 = np.array([[1.6604910, -0.5876411, -0.0728499], [-0.1245505, 1.1328999, -0.0083494], [-0.0181508, -0.1005789, 1.1187297]])
PQ_WHITE = 10000 / 203


def pq_decode(v):
    p = np.clip(v, 0, 1) ** (1 / M2)
    return (np.maximum(p - C1, 0) / (C2 - C3 * p)) ** (1 / M1)


def pq_encode(l):
    y = np.clip(l, 0, 1) ** M1
    return ((C1 + C2 * y) / (1 + C3 * y)) ** M2


def lin(v):
    return float(srgb_decode(np.array(v, dtype=np.float64)))


def signed_pow(v, k):
    return np.sign(v) * np.abs(v) ** k


def apply_effect(e, c, edge, weight=None):
    """One effect over `c`. Written from what the header promises for each field, apart from the shader."""
    weight = e.strength if weight is None else weight
    red, blue = 1 + 0.25 * e.temperature, 1 - 0.25 * e.temperature
    luma = 0.2126 * red + 0.7152 + 0.0722 * blue
    out = c * 2.0 ** e.exposure * np.array([red, 1, blue]) / luma
    out = 0.18 * signed_pow(out / 0.18, e.contrast)
    grey = (out @ np.array([0.2126, 0.7152, 0.0722]))[..., None]
    out = grey + (out - grey) * e.saturation
    out = signed_pow(out, 1 / e.gamma)
    tint = np.array([lin(e.tint[i]) for i in range(3)])
    out = out + (out * tint - out) * e.tint[3]
    r0, r1 = e.vignette_radius, e.vignette_radius + e.vignette_softness
    x = np.clip((edge - r0) / (r1 - r0), 0, 1)
    v = (x * x * (3 - 2 * x))[..., None] * e.vignette[3]
    out = out + (np.array([lin(e.vignette[i]) for i in range(3)]) - out) * v
    out = out + (np.array([lin(e.fill[i]) for i in range(3)]) - out) * e.fill[3]
    return c + (out - c) * weight


FORMATS = {
    "B8G8R8A8_UNORM": (UNORM_BGRA, CS_SRGB),
    "R8G8B8A8_UNORM": (UNORM_RGBA, CS_SRGB),
    "B8G8R8A8_SRGB": (SRGB_BGRA, CS_SRGB),
    "A2B10G10R10_UNORM HDR10": (A2B10, CS_HDR10),
    "R16G16B16A16_SFLOAT scRGB": (FLOAT16, CS_SCRGB),
}


def packer(fmt):
    """(bytes per pixel, encoded values -> bytes, bytes -> encoded values, one step of the format, the transfer's decode and encode)."""
    if fmt in (UNORM_BGRA, SRGB_BGRA, UNORM_RGBA, SRGB_RGBA):
        order = [2, 1, 0, 3] if fmt in (UNORM_BGRA, SRGB_BGRA) else [0, 1, 2, 3]

        def pack(v):
            b = np.rint(np.clip(v, 0, 1) * 255).astype(np.uint8)
            a = np.full(b.shape[:2] + (1,), 255, np.uint8)
            return np.concatenate([b, a], axis=2)[..., order].tobytes()

        def unpack(raw, w, h):
            b = np.frombuffer(raw, np.uint8).reshape(h, w, 4)[..., order]
            return b[..., :3] / 255.0, b[..., 3] / 255.0
        return 4, pack, unpack, 1 / 255, srgb_decode, srgb_encode
    if fmt == A2B10:
        def pack(v):
            b = np.rint(np.clip(v, 0, 1) * 1023).astype(np.uint32)
            return ((3 << 30) | (b[..., 2] << 20) | (b[..., 1] << 10) | b[..., 0]).astype(np.uint32).tobytes()

        def unpack(raw, w, h):
            p = np.frombuffer(raw, np.uint32).reshape(h, w)
            return np.stack([p & 1023, (p >> 10) & 1023, (p >> 20) & 1023], axis=2) / 1023.0, (p >> 30) / 3.0
        return 4, pack, unpack, 1 / 1023, (lambda v: pq_decode(v) @ FROM_2020.T * PQ_WHITE), (lambda c: pq_encode(np.maximum(c @ TO_2020.T, 0) / PQ_WHITE))
    if fmt == FLOAT16:
        def pack(v):
            a = np.ones(v.shape[:2] + (1,))
            return np.concatenate([v, a], axis=2).astype(np.float16).tobytes()

        def unpack(raw, w, h):
            b = np.frombuffer(raw, np.float16).reshape(h, w, 4).astype(np.float64)
            return b[..., :3], b[..., 3]
        return 8, pack, unpack, 1 / 512, (lambda v: v), (lambda c: c)
    raise ValueError(fmt)


def edge_of(w, h):
    x, y = np.meshgrid((np.arange(w) + 0.5) / w - 0.5, (np.arange(h) + 0.5) / h - 0.5)
    aspect = w / h
    return np.sqrt((x * aspect) ** 2 + y ** 2) / (0.5 * np.sqrt(aspect * aspect + 1))


def test_image(w, h, seed=7, hdr=False):
    """Encoded values: every level of a channel in the top rows, then noise."""
    rng = np.random.default_rng(seed)
    v = rng.random((h, w, 3))
    ramp = np.linspace(0, 1, w)
    v[0] = np.stack([ramp, ramp, ramp], axis=1)
    v[1] = np.stack([ramp, ramp[::-1], np.full(w, 0.5)], axis=1)
    return v * (1.5 if hdr else 1)


# ---- the runner ----------------------------------------------------------------------------------------------------------------------------------
lib = ctypes.CDLL(str(OUT / "libscreengpu.so"))
lib.sg_inject.argtypes = [ctypes.c_char_p, ctypes.c_int, ctypes.c_int]
lib.sg_tag.argtypes = [ctypes.c_char_p]
lib.sg_validation_report.argtypes = [ctypes.c_char_p, ctypes.c_int]
lib.sg_frame.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_int, ctypes.c_double, ctypes.POINTER(ctypes.c_int)]
lib.sg_cpu_us.restype = ctypes.c_double
lib.sg_gpu_us.restype = ctypes.c_double
lib.sg_set_strength.argtypes = [ctypes.c_uint32, ctypes.c_float]
lib.sg_play.argtypes = [ctypes.POINTER(Effect)]
lib.sg_play.restype = ctypes.c_uint32
lib.sg_stop.argtypes = [ctypes.c_uint32, ctypes.c_int]
lib.sg_playing.argtypes = [ctypes.c_uint32]
lib.sg_target.argtypes = [ctypes.c_int] * 6 + [ctypes.c_uint32]
lib.sg_log.argtypes = [ctypes.c_char_p, ctypes.c_int]
lib.sg_surface_formats.argtypes = [ctypes.c_char_p, ctypes.c_int]
lib.sg_status.argtypes = [ctypes.POINTER(ctypes.c_int)] * 3 + [ctypes.POINTER(ctypes.c_uint64)] * 2 + [ctypes.POINTER(ctypes.c_double), ctypes.c_char_p, ctypes.c_int]


def fatal(what):
    print(f"stopping: {what}", flush=True)
    sys.exit(3)


def ok(r, what):
    if lib.sg_lost():
        fatal(f"{what}: lost device or timeout ({lib.sg_lost()})")
    if r < 0:
        fatal(f"{what}: {r}")
    return r


def findings():
    n = lib.sg_validation_report(None, 0)
    buf = ctypes.create_string_buffer(n + 1)
    lib.sg_validation_report(buf, n + 1)
    out = []
    for line in buf.value.decode(errors="replace").splitlines():
        sev, count, id_, tag, text = line.split("\t", 4)
        out.append(dict(severity=sev, count=int(count), id=id_, first_during=tag, message=text))
    return out


def tag(text):
    lib.sg_tag(text.encode())


def status():
    off, ready, transfer = ctypes.c_int(), ctypes.c_int(), ctypes.c_int()
    frames, skipped = ctypes.c_uint64(), ctypes.c_uint64()
    ms = ctypes.c_double()
    reason = ctypes.create_string_buffer(256)
    lib.sg_status(off, ready, transfer, frames, skipped, ms, reason, 256)
    return dict(off=bool(off.value), ready=bool(ready.value), transfer=transfer.value, frames=frames.value, skipped=skipped.value, pipeline_ms=ms.value, reason=reason.value.decode())


def log_text():
    n = lib.sg_log(None, 0)
    buf = ctypes.create_string_buffer(n + 1)
    lib.sg_log(buf, n + 1)
    return buf.value.decode()


class Screen:
    """A target and its formats' packing. frame() is one present."""

    def __init__(self, name_or_fmt, w, h, kind=0, count=3, usage=0, cs=None):
        fmt, space = FORMATS[name_or_fmt] if isinstance(name_or_fmt, str) else (name_or_fmt, cs if cs is not None else CS_SRGB)
        self.w, self.h, self.fmt, self.cs = w, h, fmt, space
        self.bpp, self.pack, self.unpack, self.step, self.decode, self.encode = packer(fmt)
        self.images = ok(lib.sg_target(fmt, space, w, h, kind, count, usage), f"target {fmt}/{space}")
        self.now = 1000.0

    def raw(self, encoded, now=None):
        data = self.pack(encoded)
        out = ctypes.create_string_buffer(len(data))
        drew = ctypes.c_int()
        ok(lib.sg_frame(data, out, self.bpp, self.now if now is None else now, drew), "frame")
        return out.raw, bool(drew.value)

    def frame(self, encoded, now=None):
        raw, drew = self.raw(encoded, now)
        rgb, _ = self.unpack(raw, self.w, self.h)
        return rgb, drew

    def until_drawn(self, encoded, now=None, seconds=20):
        """Frames go on, showing the game's picture, until the pass has built on its own thread and drawn."""
        t0 = time.monotonic()
        while time.monotonic() - t0 < seconds:
            got, drew = self.frame(encoded, now)
            if drew:
                return got
            if status()["off"]:
                return None
            time.sleep(0.001)
        fatal("the pass did not draw within its time")


def compare(screen, got, expected, what):
    """`expected` in linear, as the shader works. A value passes within one step of the format. Where it isn't, it has to be within 0.5% of its
    pixel's brightest channel in light (1.5% on a format with sRGB in hardware, below): a channel near zero next to much brighter ones comes
    from a difference of large numbers (saturation, the Rec.2020 matrices). Those are many steps in code values and little in light."""
    want = screen.encode(expected)
    if screen.fmt == FLOAT16:
        # half floats: a step is relative to the value
        worst = float(np.max(np.abs(got - want) / np.maximum(np.abs(want), 0.01)))
        check(worst < 0.002, f"{what}: within 0.2% of the reference (worst {worst * 100:.3f}%)")
        return
    steps = np.abs(got - want) / screen.step
    light = pq_decode if screen.fmt == A2B10 else srgb_decode
    lg, lw = light(got), light(want)
    peak = np.maximum(np.max(np.maximum(lg, lw), axis=2, keepdims=True), 1e-3)
    rel = np.abs(lg - lw) / peak
    off = steps > 1.0
    # With sRGB in hardware the GPU decodes the frame, and both GPUs here differ from the formula by a fraction of a step (scaling alone stays
    # within 0.8 of a step). A saturation of 1.8 multiplies that difference in the channels it pulls apart: measured up to 1.05% of a pixel's peak.
    bound = 0.015 if screen.fmt in (SRGB_BGRA, SRGB_RGBA) else 0.005
    bad = off & (rel > bound)
    note = f"; {int(off.sum())} values more than a step off, worst {float(rel[off].max()) * 100:.3f}% in light" if off.any() else ""
    check(not bad.any(), f"{what}: within a step of the reference (worst {float(steps.max()):.2f}){note}")


info = ctypes.create_string_buffer(512)
r = lib.sg_init(args.device.encode(), 1 if args.validate else 0, info, 512)
if r == -10:
    print("--validate needs VK_LAYER_KHRONOS_validation, which isn't installed.", flush=True)
    sys.exit(4)
if r == -2:
    print(f"no Vulkan device has {args.device!r} in its name.", flush=True)
    sys.exit(4)
ok(r, "init")
results["device"] = info.value.decode()
print("device:", results["device"], flush=True)
RES = OUT / "screen-gpu" / "".join(c if c.isalnum() else "-" for c in args.device.lower())
RES.mkdir(parents=True, exist_ok=True)

if args.validate:
    seen = lib.sg_validation_control()
    if seen != 3:
        print(f"the validation layer did not report what it should (saw {seen} of the control's 3). A clean run would mean nothing, so stopping.", flush=True)
        sys.exit(5)
    check(True, "validation control: the layer reports a core rule and a synchronization hazard")

# ---- the committed SPIR-V ---------------------------------------------------------------------------------------------------------------------------
tag("spirv")
check(lib.sg_effect_size() == ctypes.sizeof(Effect), "KilnScreenEffect's size is the mirror's here")
check(lib.sg_frame_size() == 16 + 96 * 8, "GpuFrame is 784 bytes")
if shutil.which("spirv-val") and shutil.which("spirv-dis"):
    for name in ("screen_vs", "screen_fs"):
        path = SHADERS / "build" / f"{name}.spv"
        check(subprocess.run(["spirv-val", "--target-env", "vulkan1.0", str(path)]).returncode == 0, f"{name}.spv passes spirv-val for Vulkan 1.0")
    dis = subprocess.run(["spirv-dis", str(SHADERS / "build" / "screen_fs.spv")], capture_output=True, text=True).stdout
    offsets = [int(x) for x in re.findall(r"OpMemberDecorate %\S+ \d+ Offset (\d+)", dis)]
    check(offsets == [0, 16, 32, 48, 64, 80, 0, 8, 12, 16], f"the constant buffer's offsets are GpuFrame's ({offsets})")
    check("ArrayStride 96" in dis, "effects are 96 bytes apart")
    check(sorted(re.findall(r"OpDecorate %\S+ Binding (\d+)", dis)) == ["0", "1"] and set(re.findall(r"OpDecorate %\S+ DescriptorSet (\d+)", dis)) == {"0"}, "set 0, bindings 0 and 1")
if shutil.which("glslc"):
    with tempfile.TemporaryDirectory() as tmp:
        same = True
        for stage, entry, name in (("vertex", "vs_main", "screen_vs"), ("fragment", "ps_main", "screen_fs")):
            out = Path(tmp) / f"{name}.spv"
            subprocess.run(["glslc", "--target-env=vulkan1.0", "--target-spv=spv1.0", "-O", "-x", "hlsl", f"-fshader-stage={stage}", f"-fentry-point={entry}", str(SHADERS / "screen.hlsl"), "-o", str(out)], check=True)
            same = same and out.read_bytes() == (SHADERS / "build" / f"{name}.spv").read_bytes()
        check(same, "the committed .spv is what screen.hlsl compiles to")

# ---- the maths, on every kind of swapchain ---------------------------------------------------------------------------------------------------------------
W, H = 64, 48
EDGE = edge_of(W, H)
CASES = {
    "exposure": [effect(exposure=1.0)],
    "exposure down": [effect(exposure=-2.0)],
    "contrast": [effect(contrast=1.4)],
    "contrast flat": [effect(contrast=0.5)],
    "saturation grey": [effect(saturation=0.0)],
    "saturation up": [effect(saturation=1.8)],
    "gamma": [effect(gamma=1.6)],
    "temperature warm": [effect(temperature=0.8)],
    "temperature cold": [effect(temperature=-1.0)],
    "tint": [effect(tint=(0.5, 0.8, 1.0, 1.0))],
    "tint half": [effect(tint=(0.5, 0.8, 1.0, 0.5))],
    "vignette": [effect(vignette=(0.0, 0.0, 0.0, 1.0), vignette_radius=0.3, vignette_softness=0.6)],
    "vignette coloured": [effect(vignette=(0.1, 0.3, 0.6, 0.7), vignette_radius=0.0, vignette_softness=1.0)],
    "fill black": [effect(fill=(0.0, 0.0, 0.0, 1.0))],
    "fill flash": [effect(fill=(1.0, 1.0, 1.0, 0.6))],
    "half strength": [effect(strength=0.5, exposure=1.0, tint=(0.2, 0.9, 0.4, 1.0), saturation=0.3)],
    "everything": [effect(exposure=0.5, contrast=1.2, saturation=0.7, gamma=1.1, temperature=-0.4, tint=(0.8, 0.9, 1.0, 0.5), vignette=(0.02, 0.05, 0.1, 0.8), vignette_radius=0.4, vignette_softness=0.5, fill=(1.0, 0.2, 0.1, 0.1))],
    "two in order": [effect(order=1, fill=(1.0, 1.0, 1.0, 0.3)), effect(order=2, tint=(1.0, 0.2, 0.2, 1.0))],
    "two the other way": [effect(order=2, fill=(1.0, 1.0, 1.0, 0.3)), effect(order=1, tint=(1.0, 0.2, 0.2, 1.0))],
    "eight at once": [effect(order=i, exposure=0.05 * i, tint=(1.0 - 0.05 * i, 1.0, 1.0, 0.5)) for i in range(8)],
}

for name in FORMATS:
    tag(f"maths {name}")
    s = Screen(name, W, H)
    image = test_image(W, H, hdr=s.fmt == FLOAT16)
    before = s.pack(image)
    # the game's picture, as the format holds it
    held, _ = s.unpack(before, W, H)
    linear = s.decode(held)

    h = lib.sg_play(effect())
    got = s.until_drawn(image)
    check(got is not None, f"{name}: the pass builds and draws, the transfer read as {status()['transfer']}")
    if s.fmt in (UNORM_BGRA, UNORM_RGBA, SRGB_BGRA, SRGB_RGBA):
        check(float(np.max(np.abs(got - held))) == 0, f"{name}: an effect that changes nothing leaves the picture exactly as it was")
    else:
        compare(s, got, linear, f"{name}: an effect that changes nothing")
    lib.sg_stop(h, 1)
    raw, drew = s.raw(image)
    check(not drew and raw == before, f"{name}: stopped, the picture is the game's, byte for byte")

    for case, effects in CASES.items():
        handles = [lib.sg_play(e) for e in effects]
        got, drew = s.frame(image)
        ordered = sorted(enumerate(effects), key=lambda x: (x[1].order, x[0]))
        want = linear
        for _, e in ordered:
            want = apply_effect(e, want, EDGE)
        check(drew, f"{name}: {case}: drew")
        compare(s, got, want, f"{name}: {case}")
        for x in handles:
            lib.sg_stop(x, 1)

    a = lib.sg_play(effect(tint=(0.2, 0.4, 0.8, 1.0)))
    got, _ = s.frame(image)
    alpha = s.unpack(s.raw(image)[0], W, H)[1]
    check(float(np.min(alpha)) == 1.0, f"{name}: alpha is kept")
    lib.sg_stop(a, 1)

# ---- the envelope, reaching the pixels ------------------------------------------------------------------------------------------------------------------------
tag("envelope")
s = Screen("B8G8R8A8_UNORM", 16, 16)
grey = np.full((16, 16, 3), 0.5)
held, _ = s.unpack(s.pack(grey), 16, 16)
base = s.decode(held)
black = effect(fill=(0, 0, 0, 1), fade_in=1, seconds=1, fade_out=1)
# the pipeline first: an effect's clock starts at the first frame the pass can draw
h = lib.sg_play(effect(exposure=0.5))
s.until_drawn(grey, 90.0)
lib.sg_stop(h, 1)


def weight(got):
    return 1 - float(np.mean(s.decode(got)) / np.mean(base))


h = lib.sg_play(black)
_, drew = s.frame(grey, 100.0)
check(not drew, "at its first frame a fade in shows nothing yet, and the pass isn't asked to draw")
got, _ = s.frame(grey, 100.5)
check(abs(weight(got) - 0.5) < 0.01, f"half way up the fade in: half black ({weight(got):.3f})")
got, _ = s.frame(grey, 101.5)
check(abs(weight(got) - 1) < 0.01, f"at full: all black ({weight(got):.3f})")
got, _ = s.frame(grey, 102.5)
check(abs(weight(got) - 0.5) < 0.01, f"half way down the fade out: half black ({weight(got):.3f})")
got, drew = s.frame(grey, 103.1)
check(not drew and not lib.sg_playing(h), "ended: the pass is not asked to draw")
h = lib.sg_play(black)
s.frame(grey, 200.0)
got, _ = s.frame(grey, 200.5)
lib.sg_stop(h, 0)
got, _ = s.frame(grey, 200.5)
check(abs(weight(got) - 0.5) < 0.01, f"a stop half way up starts down from there ({weight(got):.3f})")
got, _ = s.frame(grey, 201.0)
check(abs(weight(got) - 0.25) < 0.01, f"half way through its fade out, a quarter ({weight(got):.3f})")
lib.sg_stop(h, 1)
h = lib.sg_play(effect(fill=(0, 0, 0, 1)))
got, _ = s.frame(grey, 300.0)
check(abs(weight(got) - 1) < 0.01, "strength 1")
lib.sg_set_strength(h, 0.25)
got, _ = s.frame(grey, 300.1)
check(abs(weight(got) - 0.25) < 0.01, f"set_strength reaches the pixels ({weight(got):.3f})")
lib.sg_stop(h, 1)

# ---- a real swapchain, on a headless surface: the usage flag, images from the driver, the present ---------------------------------------------------------------
tag("swapchain")
text = ctypes.create_string_buffer(4096)
usage = lib.sg_surface_formats(text, 4096)
if usage < 0:
    # NVIDIA's driver lists the extension and doesn't present to it. The plain image list above stands in there.
    results["swapchain"] = f"this device can't present to a headless surface ({usage})"
    print(f"SKIP a real swapchain: {results['swapchain']}", flush=True)
else:
    pairs = [tuple(int(x) for x in line.split()) for line in text.value.decode().splitlines()]
    results["swapchain_formats"] = pairs
    results["swapchain_usage"] = usage
    check(usage & TRANSFER_SRC != 0, f"the surface allows TRANSFER_SRC (usage 0x{usage:x}), what the pass needs kiln.ui to add")
    fmts = [p for p in pairs if p[1] == CS_SRGB and p[0] in (UNORM_BGRA, UNORM_RGBA, SRGB_BGRA, SRGB_RGBA)]
    check(bool(fmts), f"the surface offers a format the pass reads ({pairs})")
    f = fmts[0][0]
    s = Screen(f, 128, 96, kind=1, count=3)
    print(f"swapchain: format {f}, {s.images} images", flush=True)
    image = test_image(128, 96)
    held, _ = s.unpack(s.pack(image), 128, 96)
    linear = s.decode(held)
    e = effect(tint=(0.6, 0.8, 1.0, 1.0), saturation=0.5, vignette=(0, 0, 0, 0.5))
    want = apply_effect(e, linear, edge_of(128, 96))
    h = lib.sg_play(e)
    got = s.until_drawn(image, 1.0)
    compare(s, got, want, "swapchain: the effect, read back from a driver's swapchain image")
    frames_before = status()["frames"]
    for i in range(60):
        got, drew = s.frame(image, 1.0 + i / 60)
        if not drew:
            check(False, f"swapchain: frame {i} did not draw")
    check(status()["frames"] - frames_before == 60, f"swapchain: 60 presents in a row through {s.images} images")
    compare(s, got, want, "swapchain: and it is still right")
    # the game resizes: a new swapchain, the old one passed on and destroyed
    s2 = Screen(f, 200, 120, kind=1, count=3)
    image2 = test_image(200, 120, seed=11)
    held2, _ = s2.unpack(s2.pack(image2), 200, 120)
    got2 = s2.until_drawn(image2, 3.0)
    compare(s2, got2, apply_effect(e, s2.decode(held2), edge_of(200, 120)), "swapchain: after a resize the same effect plays on, at the new size")
    check(lib.sg_playing(h) == 1 and "the swapchain changed" in log_text(), "swapchain: the change was logged, and the effect kept playing")
    calls = lib.sg_pipeline_calls()
    s3 = Screen(f, 128, 96, kind=1, count=3)
    s3.until_drawn(image, 3.5)
    check(lib.sg_pipeline_calls() == calls, "swapchain: the same format needs no new pipeline")
    other = [p for p in pairs if p[1] == CS_SRGB and p[0] in (UNORM_BGRA, UNORM_RGBA, SRGB_BGRA, SRGB_RGBA) and p[0] != f]
    if other:
        s4 = Screen(other[0][0], 128, 96, kind=1, count=3)
        got = s4.until_drawn(image, 4.0)
        check(got is not None and lib.sg_pipeline_calls() == calls + 1, f"swapchain: another format ({other[0][0]}) gets its own pipeline")
    lib.sg_stop(h, 1)

# ---- nothing playing -----------------------------------------------------------------------------------------------------------------------------------------------
tag("idle")
s = Screen("B8G8R8A8_UNORM", 32, 32)
image = test_image(32, 32)
before = s.pack(image)
h = lib.sg_play(effect(exposure=1))
s.until_drawn(image, 1.0)
lib.sg_stop(h, 1)
raw, drew = s.raw(image, 1.1)
check(not drew and raw == before, "after the last effect, the frame is the game's")
released = False
for _ in range(1000):
    if lib.sg_release():
        released = True
        break
    s.raw(image, 1.2)
check(released, "the frame's copy is freed once nothing is in flight, with no wait")
calls = lib.sg_pipeline_calls()
h = lib.sg_play(effect(exposure=1))
got = s.until_drawn(image, 2.0)
held, _ = s.unpack(before, 32, 32)
compare(s, got, apply_effect(effect(exposure=1), s.decode(held), edge_of(32, 32)), "a play after that builds again and draws")
check(lib.sg_pipeline_calls() == calls, "and the pipeline was kept")
lib.sg_stop(h, 1)

# ---- failures: the game's frame goes on every time ------------------------------------------------------------------------------------------------------------------


def failing(fn, result, what, after=0, needs_frames=0):
    tag(f"failure {what}")
    lib.sg_new_pass()
    s = Screen("B8G8R8A8_UNORM", 32, 32)
    image = test_image(32, 32)
    before = s.pack(image)
    h = lib.sg_play(effect(exposure=1))
    if needs_frames:
        s.until_drawn(image, 1.0)
        for i in range(needs_frames):
            s.raw(image, 1.0 + i / 60)
    lib.sg_inject(fn.encode(), result, after)
    t0 = time.monotonic()
    while not status()["off"] and time.monotonic() - t0 < 20:
        s.raw(image, 2.0)
    lib.sg_inject(fn.encode(), 0, 0)
    st = status()
    check(st["off"], f"{what}: the pass turns off: {st['reason']}")
    check(f"screen: off, the game carries on: {st['reason']}" in log_text(), f"{what}: with a plain log line")
    same = True
    for i in range(s.images * 2 + 1):
        raw, drew = s.raw(image, 3.0)
        same = same and raw == before and not drew
    check(same, f"{what}: every frame after it is the game's, byte for byte, and its semaphores still work")
    lib.sg_clear()


failing("vkCreateGraphicsPipelines", OOM, "a failed pipeline")
failing("vkCreateShaderModule", OOM, "a failed shader module")
failing("vkCreateImage", OOM, "no memory for the frame's copy")
failing("vkAllocateMemory", OOM, "no memory at the second allocation", after=1)
failing("vkQueueSubmit", DEVICE_LOST, "a lost device at submit", needs_frames=4)
failing("vkWaitForFences", DEVICE_LOST, "a lost device at a fence", needs_frames=4)

tag("failure stuck fence")
lib.sg_new_pass()
s = Screen("B8G8R8A8_UNORM", 32, 32)
image = test_image(32, 32)
before = s.pack(image)
held, _ = s.unpack(before, 32, 32)
h = lib.sg_play(effect(exposure=1))
s.until_drawn(image, 1.0)
for i in range(4):
    s.raw(image, 1.1)
lib.sg_inject(b"vkWaitForFences", TIMEOUT, 0)
worst = 0.0
game_frames = 0
n = 0
while not status()["off"] and n < 700:
    raw, drew = s.raw(image, 2.0)
    worst = max(worst, lib.sg_cpu_us())
    game_frames += 1 if (not drew and raw == before) else 0
    n += 1
lib.sg_inject(b"vkWaitForFences", 0, 0)
st = status()
check(game_frames == n, f"a frame whose image the GPU isn't done with is left alone: {n} frames showed the game's picture")
check(st["skipped"] > 0, f"and counted ({st['skipped']})")
check(st["off"] and n <= 601, f"after 600 of them it turns off, with the reason: {st['reason']}")
lib.sg_clear()

tag("failure unsupported")
lib.sg_new_pass()
s = Screen(FLOAT16, 16, 16, cs=CS_SRGB)
image = test_image(16, 16)
lib.sg_play(effect(exposure=1))
_, drew = s.raw(image, 1.0)
st = status()
check(st["off"] and not drew and "no reading" in st["reason"], f"a swapchain it can't interpret: {st['reason']}")
lib.sg_clear()
lib.sg_new_pass()
s = Screen("B8G8R8A8_UNORM", 16, 16, usage=COLOR_ATTACHMENT)
lib.sg_play(effect(exposure=1))
_, drew = s.raw(test_image(16, 16), 1.0)
st = status()
check(st["off"] and not drew and "can't be read" in st["reason"], f"a swapchain without TRANSFER_SRC, as when a surface refuses it: {st['reason']}")
lib.sg_clear()

# ---- what it costs --------------------------------------------------------------------------------------------------------------------------------------------------------
tag("cost")
lib.sg_new_pass()
for w, h_ in ((1920, 1080), (2560, 1440), (3840, 2160)):
    s = Screen("B8G8R8A8_UNORM", w, h_)
    image = np.zeros((h_, w, 3)) + 0.4
    idle = []
    for _ in range(20):
        s.raw(image, 1.0)
        idle.append(lib.sg_cpu_us())
    e = effect(exposure=0.2, contrast=1.1, saturation=0.8, tint=(0.8, 0.9, 1.0, 0.5), vignette=(0, 0, 0, 0.5))
    x = lib.sg_play(e)
    s.until_drawn(image, 1.0)
    cpu, gpu = [], []
    for i in range(40):
        _, drew = s.raw(image, 1.0 + i / 60)
        if drew:
            cpu.append(lib.sg_cpu_us())
            gpu.append(lib.sg_gpu_us())
    lib.sg_stop(x, 1)
    results["cost"][f"{w}x{h_}"] = dict(cpu_us_idle=float(np.median(idle)), cpu_us=float(np.median(cpu)), gpu_us=float(np.median(gpu)))
    print(f"cost at {w}x{h_}: nothing playing {np.median(idle):.1f} us of CPU in the runner; playing {np.median(cpu):.0f} us of CPU, {np.median(gpu):.0f} us of GPU", flush=True)
results["pipeline_ms"] = status()["pipeline_ms"]
print(f"pipeline: {results['pipeline_ms']:.1f} ms, on the pass's own thread", flush=True)

results["log"] = log_text().splitlines()
lib.sg_shutdown()
results["validation"] = findings()
errors = [f for f in results["validation"] if f["severity"] == "error"]
warnings = [f for f in results["validation"] if f["severity"] == "warning"]
if args.validate:
    check(not errors, f"the validation layer found no errors, synchronization included ({len(errors)} kinds)")
    for f in errors + warnings:
        print(f"  {f['severity']} x{f['count']} {f['id']} during {f['first_during']}: {f['message'][:400]}", flush=True)
(RES / "result.json").write_text(json.dumps(results, indent=1))
print(f"{len(results['checks']) - fails} passed, {fails} failed; {RES / 'result.json'}", flush=True)
sys.exit(1 if fails else 0)
