# SPDX-License-Identifier: MIT
"""Runs the harness protocol on a real GPU through gpu/vfxgpu.cpp: same World, same checks, real dispatches. Headless.

Any timeout or lost device ends the process at once with exit code 3. Nothing retries.
"""
import ctypes
import os
import struct
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[4]
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(ROOT / "tools/kfc"))
OUT = Path(os.environ.get("KILN_OUT", Path(tempfile.gettempdir()) / "kiln-vfx-gpu"))
OUT.mkdir(parents=True, exist_ok=True)
os.environ["KILN_OUT"] = str(OUT)

# Real compile times, and nothing left in the user's driver caches.
os.environ["__GL_SHADER_DISK_CACHE"] = "0"
os.environ["MESA_SHADER_CACHE_DISABLE"] = "true"

from harness import CHUNKS, COUNTERS, EMITTERS, World  # noqa: E402
from kfc.spirv import Module  # noqa: E402

DT = {"Sampler": 0, "SamplerArray": 0, "SampledImage": 2, "SampledImageArray": 2, "StorageImage": 3, "StorageImageArray": 3,
      "UniformBuffer": 6, "ByteAddressBuffer": 7, "StructuredBuffer": 7, "RWByteAddressBuffer": 7, "RWStructuredBuffer": 7}
FMT = {"R16G16B16A16_sfloat": 97, "R8_unorm": 9, "R16_unorm": 70, "D32_sfloat": 126, "R8G8B8A8_unorm": 37, "R8G8B8A8_srgb": 43, "R32_uint": 98}
BLEND = {"Zero": 0, "One": 1, "SrcColor": 2, "InvSrcColor": 3, "SrcAlpha": 6, "InvSrcAlpha": 7, "DestAlpha": 8, "InvDestAlpha": 9, "DestColor": 4, "InvDestColor": 5}
CMP = {"Never": 0, "Less": 1, "Equal": 2, "LessEqual": 3, "Greater": 4, "NotEqual": 5, "GreaterEqual": 6, "Always": 7}
CULL = {"None": 0, "Front": 1, "Back": 2}
TIMEOUT_MS = 2000


class Gpu:
    def __init__(self, name, validate=0):
        lib = ctypes.CDLL(str(OUT / "libvfxgpu.so"))
        lib.vg_validation_report.argtypes = [ctypes.c_char_p, ctypes.c_int]
        lib.vg_tag.argtypes = [ctypes.c_char_p]
        lib.vg_image_bc7.argtypes = [ctypes.c_uint32, ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32, ctypes.c_char_p, ctypes.c_uint64]
        lib.vg_buffer_map.restype = ctypes.c_void_p
        lib.vg_buffer.argtypes = [ctypes.c_uint64]
        lib.vg_compute_pipeline.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_size_t, ctypes.c_char_p, ctypes.POINTER(ctypes.c_double)]
        self.lib = lib
        self.compile_ms = {}
        self.pipelines = {}
        self.log = []
        info = ctypes.create_string_buffer(512)
        r = lib.vg_init(name.encode(), validate, info, 512)
        if r == -10:
            print("--validate needs VK_LAYER_KHRONOS_validation, which isn't installed (vulkan-validation-layers, or the Vulkan SDK). "
                  "Install it, or run without --validate.", flush=True)
            sys.exit(4)
        if r:
            self.fatal(f"vg_init {r}")
        self.info = info.value.decode()
        print("device:", self.info, flush=True)

    def tag(self, text):
        self.lib.vg_tag(text.encode())

    def validation(self):
        """Findings so far: [{severity, count, id, first_during, message}]."""
        n = self.lib.vg_validation_report(None, 0)
        buf = ctypes.create_string_buffer(n + 1)
        self.lib.vg_validation_report(buf, n + 1)
        out = []
        for line in buf.value.decode(errors="replace").splitlines():
            sev, count, id_, tag, text = line.split("\t", 4)
            out.append(dict(severity=sev, count=int(count), id=id_, first_during=tag, message=text))
        return out

    def control(self, mode):
        """Provokes what the layer should catch, because a clean run means nothing if it can't report. Returns what it missed."""
        seen = self.ok(self.lib.vg_validation_control(), "validation control")
        missed = [] if seen & 1 else ["core rule"]
        if mode == "sync" and not seen & 2:
            missed.append("synchronization hazard")
        if mode == "gpuav" and not self._gpuav_control():
            missed.append("out-of-bounds read in a shader")
        return missed

    def _gpuav_control(self):
        # Reads 64 bytes into a 16-byte buffer. That's still inside the buffer's own memory page, so nothing can fault.
        src = OUT / "gpuav_control.comp"
        spv = OUT / "gpuav_control.spv"
        src.write_text("#version 450\nlayout(local_size_x = 1) in;\nlayout(set = 0, binding = 0) buffer B { uint v[]; } b;\n"
                       "void main() { b.v[0] = b.v[16]; }\n")
        subprocess.run(["glslc", "--target-env=vulkan1.2", "-O0", str(src), "-o", str(spv)], check=True)
        code = spv.read_bytes()
        self.lib.vg_validation_hold(1)
        layout = self.layout([[(7, 1, 0)]], 0)
        buf = self.ok(self.lib.vg_buffer(16), "buffer")
        self.ok(self.lib.vg_bind_buffer(layout, 0, 0, buf), "bind")
        ms = ctypes.c_double()
        pipe = self.ok(self.lib.vg_compute_pipeline(layout, code, len(code), b"main", ctypes.byref(ms)), "control pipeline")
        self.begin()
        self.lib.vg_cmd_bind_compute(pipe, layout)
        self.lib.vg_cmd_dispatch(1, 1, 1)
        self.submit()
        seen = any(f["severity"] == "error" for f in self.validation())
        self.lib.vg_validation_hold(0)
        self.lib.vg_buffer_free(buf)
        return seen

    def fatal(self, what):
        # Stop at once, no retry.
        print(f"GPU FATAL: {what}: lost={self.lib.vg_lost()}. Stopping.", flush=True)
        try:
            found = "".join(f"{f['severity']} x{f['count']} {f['id']} [{f['first_during']}] {f['message']}\n" for f in self.validation())
            (OUT / "FATAL.txt").write_text(f"{time.ctime()} {what} lost={self.lib.vg_lost()}\n{found}")
        finally:
            os._exit(3)

    def ok(self, r, what):
        if r < 0 or self.lib.vg_lost():
            self.fatal(f"{what}: {r}")
        return r

    def layout(self, sets, push):
        """sets: list of lists of (type, count, flags)."""
        counts = [len(s) for s in sets]
        flat = [x for s in sets for x in s]
        arr = lambda v: (ctypes.c_int * max(len(v), 1))(*v)
        return self.ok(self.lib.vg_layout(len(sets), arr(counts), arr([x[0] for x in flat]), arr([x[1] for x in flat]), arr([x[2] for x in flat]), push), "layout")

    def compute(self, layout, code, entry=b"cs_main", label=None):
        key = (layout, bytes(code))
        if key in self.pipelines:
            return self.pipelines[key]
        ms = ctypes.c_double()
        self.tag(f"creating {label or len(code)}")
        p = self.lib.vg_compute_pipeline(layout, bytes(code), len(code), entry, ctypes.byref(ms))
        self.ok(p, f"compute pipeline {label}")
        self.pipelines[key] = p
        self.compile_ms[label or f"{len(code)} B"] = ms.value
        return p

    def buffer(self, size):
        i = self.ok(self.lib.vg_buffer(size), "buffer")
        addr = self.lib.vg_buffer_map(i)
        return i, memoryview((ctypes.c_char * max(size, 4)).from_address(addr)).cast("B")[:size]

    def begin(self):
        self.ok(self.lib.vg_begin(), "begin")

    def submit(self, timeout_ms=TIMEOUT_MS):
        ms = ctypes.c_double()
        self.ok(self.lib.vg_submit_wait(timeout_ms, ctypes.byref(ms)), "submit")
        return ms.value


GPU = None
VFX = None      # the game's VfxUpdatePipelineLayout, recreated, or one derived from the modules
PREPARE = None
DERIVED = set()


GAME = Path(os.environ.get("KILN_GAME", str(Path.home() / ".local/share/Steam/steamapps/common/Enshrouded")))


def have_game():
    return (GAME / "enshrouded.kfc").exists()


def game():
    from kfc.archive import ContentHash, Kfc
    from kfc.blob import Decoder
    from kfc.refl import TypeDb
    kfc = Kfc(GAME)
    types = TypeDb.from_exe(GAME / "enshrouded.exe")
    return kfc, types, Decoder(types), ContentHash


def layout_from_modules(gpu, codes, push):
    """Without the game's files: a pipeline layout holding just what these modules declare, read from their SPIR-V."""
    used = {}
    for code in codes:
        m = Module(code)
        for sb, var in m.bindings().items():
            used[sb] = 6 if m.variables[var][1] == 2 else 7   # Uniform: uniform buffer; StorageBuffer: storage buffer
    sets = []
    for s in range(max(k[0] for k in used) + 1):
        top = max((k[1] for k in used if k[0] == s), default=-1)
        # Holes are storage buffers nothing reads, kept so binding numbers stay the module's.
        sets.append([(used.get((s, b), 7), 1, 1) for b in range(top + 1)])
    lid = gpu.layout(sets, push)
    for s, row in enumerate(sets):
        for b in range(len(row)):
            gpu.ok(gpu.lib.vg_bind_buffer(lid, s, b, -1), "null buffer")
            DERIVED.add((s, b))
    return lid


class GpcLayouts:
    """Pipeline layouts built from the game's own GpcResource, read at run time from the installed game."""

    def __init__(self):
        self.kfc, self.types, self.dec, self.CH = game()
        th = self.types.get("keen::GpcResource")["hash"]
        self.gpc = {}
        for k in self.kfc.resources_of_type(th):
            v = self.dec.decode(th, self.kfc.read(k))
            self.gpc[v["debugName"]] = v

    def code_blob(self, name):
        h = self.gpc[name]["strippedCode"]["hash"]
        return self.kfc.read_content(self.CH(h["size"], h["hash0"], h["hash1"], h["hash2"]))

    def module(self, name, shader):
        r = shader["variants"][0]["strippedCode"]["range"]
        return self.code_blob(name)[r["start"]:r["start"] + r["count"]]

    def build(self, gpu, gpc, layout_name, push):
        g = self.gpc[gpc]
        L = [x for x in g["pipelineLayouts"] if x["debugName"] == layout_name][0]
        sets, names = [], {}
        for si, di in enumerate(L["descriptorSetLayoutIndices"]):
            d = g["descriptorSetLayouts"][di]
            row = []
            for j, b in enumerate(d["bindings"]):
                t = DT[b["type"]]
                is_array = b["type"].endswith("Array")
                count = b["arraySizeOrBufferStride"] if is_array else 1
                row.append((t, max(count, 1), 1))
                names[b["debugName"]] = (si, j, b["type"], count, b["dataType"])
            for _ in d["staticSamplers"]:
                row.append((0, 1, 2))   # immutable
            sets.append(row)
        if L["useBindlessDescriptors"]:
            while len(sets) < 4:
                sets.append([])
            sets.append([(2, 32768, 1), (0, 256, 1)])
        lid = gpu.layout(sets, push)
        # Null descriptors for everything that isn't bound later.
        for name, (si, j, ty, count, _) in names.items():
            if ty in ("UniformBuffer", "ByteAddressBuffer", "StructuredBuffer", "RWByteAddressBuffer", "RWStructuredBuffer"):
                gpu.ok(gpu.lib.vg_bind_buffer(lid, si, j, -1), "null buffer")
            elif ty in ("Sampler", "SamplerArray"):
                for e in range(max(count, 1)):
                    gpu.ok(gpu.lib.vg_bind_sampler(lid, si, j, e), "sampler")
            elif ty == "SampledImage" or ty == "StorageImage":
                gpu.ok(gpu.lib.vg_bind_image(lid, si, j, 0, -1), "null image")
            elif ty in ("SampledImageArray", "StorageImageArray"):
                for e in range(max(count, 1)):
                    gpu.ok(gpu.lib.vg_bind_image(lid, si, j, e, -1), "null image")
        return lid, names, L, g


GPC = None


def start(device, kiln_modules, validate=0):
    """The game's real layouts when its files are installed, else one derived from Kiln's modules."""
    global GPU, VFX, PREPARE, GPC
    GPU = Gpu(device, validate)
    if have_game():
        GPC = GpcLayouts()
        VFX = GPC.build(GPU, "vfx/vfx.gpc", "VfxUpdatePipelineLayout", 8)[0]
        PREPARE = GPC.build(GPU, "vfx/vfx.gpc", "VfxPrepareLayout", 0)[0]
    else:
        print("no game files: layout derived from the modules; the game's modules and shaders are skipped", flush=True)
        VFX = layout_from_modules(GPU, kiln_modules, 8)
    return GPU


class GpuWorld(World):
    """World whose tick runs on the GPU, in the game's order: prepare, a barrier, each depth's dispatches, a barrier."""

    def __init__(self, entries, **kw):
        super().__init__(entries, **kw)
        self.gb = {}
        self.owned = []
        for key, b in list(self.buf.items()):
            if b is self.chunks_tab[0]:
                continue
            self._adopt(key, b)
        self.tab_ids = []
        for b in self.chunks_tab:
            self.tab_ids.append(self._adopt(None, b))
        self.gb[CHUNKS] = self.tab_ids[0]
        self.gpu_ms = []
        # The prepare inputs
        self.prep_ubo = None

    def _adopt(self, key, b):
        i, view = GPU.buffer(len(b.data))
        view[:len(b.data)] = bytes(b.data)
        b.data = view
        self.owned.append((i, view))
        if key is not None:
            self.gb[key] = i
        return i

    def close(self):
        for i, view in self.owned:
            view.release()
            GPU.lib.vg_buffer_free(i)
        self.owned = []

    def _bind(self):
        for (s, b), i in self.gb.items():
            if GPC is None and not self._in_derived_layout(s, b):
                continue
            GPU.ok(GPU.lib.vg_bind_buffer(VFX, s, b, i), "bind")

    @staticmethod
    def _in_derived_layout(s, b):
        return (s, b) in DERIVED

    def tick(self, phase=0, shuffle=True, reset_draws=True):
        cnt = self.buf[COUNTERS]
        self.gb[CHUNKS] = self.tab_ids[self.parity]
        self.buf[CHUNKS] = self.chunks_tab[self.parity]
        self._bind()
        if reset_draws:
            for off in self.draw_counters.values():
                cnt.put_u32(off, 0)
        emitters = [c for c in self.chunks if c.has_emitter]
        GPU.begin()
        if self.prepare is not None and emitters:
            self._prepare_on_gpu(emitters)
        else:
            em = self.buf[EMITTERS]
            for c in emitters:
                base = c.emitter * 32
                child = self.chunks[self.entries[c.index].child]
                live = cnt.u32(c.particle_counter)
                free = cnt.u32(child.counter_free)
                em.put_u32(base + 12, min(free, (em.u32(base + 8) - live) & 0xffffffff))
                cnt.put_u32(c.spawn_counter, 0)
        for c in self.chunks:
            cnt.put_u32(self.next_counter(c), 0)
        for depth in sorted({e.depth for e in self.entries}):
            for c in self.chunks:
                e = self.entries[c.index]
                if e.depth != depth:
                    continue
                p = GPU.compute(VFX, e.sim.raw, label=f"module {len(e.sim.raw)} B")
                GPU.lib.vg_cmd_bind_compute(p, VFX)
                pc = struct.pack("<II", c.index * 4 | phase, 0)
                GPU.lib.vg_cmd_push(VFX, pc, 8)
                GPU.lib.vg_cmd_dispatch((c.count + 31) // 32, 1, 1)
            GPU.lib.vg_cmd_barrier()
        self.gpu_ms.append(GPU.submit())
        self.parity ^= 1
        self.ticks += 1

    def _prepare_on_gpu(self, emitters):
        if self.prep_ubo is None:
            self.prep_ubo = GPU.buffer(16)
            self.prep_list = GPU.buffer(4 * len(emitters))
            self.owned += [self.prep_ubo, self.prep_list]
        struct.pack_into("<I", self.prep_ubo[1], 0, len(emitters))
        for k, c in enumerate(emitters):
            struct.pack_into("<I", self.prep_list[1], 4 * k, c.emitter)
        for b, i in ((0, self.prep_ubo[0]), (1, self.prep_list[0]), (2, self.gb[COUNTERS]), (3, self.gb[EMITTERS]), (4, self.gb[CHUNKS])):
            GPU.ok(GPU.lib.vg_bind_buffer(PREPARE, 0, b, i), "bind prepare")
        p = GPU.compute(PREPARE, self.prepare.raw, label="game VfxPrepare")
        GPU.lib.vg_cmd_bind_compute(p, PREPARE)
        GPU.lib.vg_cmd_dispatch((len(emitters) + 63) // 64, 1, 1)
        GPU.lib.vg_cmd_barrier()
