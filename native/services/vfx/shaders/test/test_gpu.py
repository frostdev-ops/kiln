# SPDX-License-Identifier: MIT
"""test_starter.py's scenarios and checks on a real GPU, then stress, compile times and a render. Headless, no game launch.

    native/services/vfx/shaders/gpu/build.sh
    python3 native/services/vfx/shaders/test/test_gpu.py --device NVIDIA --stage suite|stress|compile|render|cooked

With the game installed (read only), layouts and the comparison shaders come from its files at run time, and its own modules
run through the same checks. Without it, everything that only needs Kiln's modules still runs.

A timeout or a lost device ends the process with exit code 3. Results go to $KILN_OUT/<device>/.
"""
import argparse
import json
import math
import struct
import sys
import time
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import gpu  # noqa: E402
from gpu import GpuWorld, OUT  # noqa: E402

ap = argparse.ArgumentParser()
ap.add_argument("--device", default="NVIDIA")
ap.add_argument("--stage", default="suite")
ap.add_argument("--cooked", help="for --stage cooked: a kiln-vfxcook --out folder")
ap.add_argument("--validate", nargs="?", const="sync", choices=["sync", "gpuav"],
                help="run under VK_LAYER_KHRONOS_validation: sync adds synchronization validation (default), gpuav adds GPU-assisted "
                     "checks with robust access off. Findings go to validation-<stage>.json. Stops with a message if the layer is missing")
args = ap.parse_args()
BUILD = HERE.parent / "build"
EMITTER = (BUILD / "kiln_emitter.spv").read_bytes()
SPRITE = (BUILD / "kiln_sprite.spv").read_bytes()
LIGHT = (BUILD / "kiln_light.spv").read_bytes()

VALIDATE = {None: 0, "sync": 1, "gpuav": 2}[args.validate]
DEV = gpu.start(args.device, [EMITTER, SPRITE, LIGHT], VALIDATE)
RES = OUT / "".join(c if c.isalnum() else "-" for c in args.device.lower())
if VALIDATE:
    RES = RES / f"validate-{args.validate}"
    missed = DEV.control(args.validate)
    if missed:
        print(f"the validation layer did not report: {', '.join(missed)}. A clean run would mean nothing, so stopping.", flush=True)
        sys.exit(5)
    print("validation control: the layer reports what it should", flush=True)
RES.mkdir(parents=True, exist_ok=True)

import test_starter as T  # noqa: E402
from harness import P, Entry, emitter_block, light_block, sprite_block  # noqa: E402

ALIVE = []


class World(GpuWorld):
    """Frees older worlds, keeping the last few (some tests compare two side by side)."""

    def __init__(self, *a, **kw):
        super().__init__(*a, **kw)
        ALIVE.append(self)
        while len(ALIVE) > 4:
            ALIVE.pop(0).close()


T.World = World


def suite():
    s = unittest.TestSuite()
    load = unittest.defaultTestLoader.loadTestsFromTestCase
    for c in (T.Interface, T.Burst, T.Phases, T.Instance, T.Motion, T.Lists, T.SortKey, T.BadData, T.Light, T.GameTest):
        s.addTests(load(c))
    return s


class TaggingResult(unittest.TextTestResult):
    def startTest(self, test):
        DEV.tag(test.id())
        super().startTest(test)


def run_suite():
    r = unittest.TextTestRunner(verbosity=1, stream=sys.stdout, resultclass=TaggingResult).run(suite())
    out = dict(device=DEV.info, run=r.testsRun, failures=[(str(t), m) for t, m in r.failures], errors=[(str(t), m) for t, m in r.errors],
               skipped=[(str(t), m) for t, m in r.skipped], time=time.ctime())
    (RES / "suite.json").write_text(json.dumps(out, indent=1))
    print(f"suite: {r.testsRun} run, {len(r.failures)} failures, {len(r.errors)} errors", flush=True)


def check_states(w, life, tag):
    """Every active child state as the GPU left it: no torn or stale record. parity has flipped: the 'old' list is the live one."""
    st = w.buf[(1, 42)]
    for c in w.chunks:
        e = w.entries[c.index]
        if e.depth != 1:
            continue
        n = w.counter(w.active_counter(c))
        parent = w.chunks[e.parent]
        for i in range(n):
            pid = st.u32(w.old_list(c) + 4 * i)
            s = c.states + P["KS_STATE_SIZE"] * pid
            assert st.u32(s + 4) == parent.emitter, f"{tag}: chunk {c.index} particle {pid}: parent emitter {st.u32(s + 4)}, want {parent.emitter}"
            assert st.u32(s + 8) == 0, f"{tag}: instance {st.u32(s + 8)}"
            assert st.u32(s + 16) >= 1, f"{tag}: alive with tick 0"
            lf = st.f32(s + P["KS_S_LIFE"])
            age = st.f32(s + P["KS_S_AGE"])
            assert life[0] - 1e-3 <= lf <= life[1] + 1e-3, f"{tag}: life {lf}"
            assert 0.0 <= age < lf + 1e-3, f"{tag}: age {age} life {lf}"
            for k in range(3):
                assert math.isfinite(st.f32(s + P["KS_S_POS"] + 4 * k)), f"{tag}: position"


def check_draws(w, tag, want_max=None):
    n = w.counter(w.draw_counters["count"])
    if want_max is not None:
        assert n <= want_max, f"{tag}: {n} draws over {want_max}"
    b = w.buf[(1, 3)]
    sort = w.buf[(1, 4)]
    for i in range(min(n, w.cap)):
        base = 176 * i
        assert b.u32(base + 4) == 1 and b.u32(base + 0) == 6, f"{tag}: draw {i}: head not filled"
        assert b.u32(base + 16) == i, f"{tag}: draw {i}: instanceOffset {b.u32(base + 16)}"
        assert b.u32(base + 20) == 1, f"{tag}: draw {i}: mesh"
        for k in (0x2c, 0x30, 0x34, 0x4c):
            assert math.isfinite(b.f32(base + k)), f"{tag}: draw {i}: non-finite at {k}"
        assert sort.u32(i * 8 + 4) == i, f"{tag}: sort {i}"
    return n


def pair(em, sp, max_count, base=0):
    return [Entry(EMITTER, depth=0, child=base + 1, state_size=P["KE_STATE_SIZE"], params=em),
            Entry(SPRITE, depth=1, parent=base, max_count=max_count, state_size=P["KS_STATE_SIZE"], params=sp)]


def stress():
    results = {}
    def run(tag, entries, ticks, life, cap, step=1, check_every=1, **kw):
        DEV.tag(tag)
        t0 = time.time()
        w = World(entries, cap=cap, **kw)
        peak = peak_draws = 0
        total_spawned = 0
        for t in range(ticks):
            w.tick()
            if t % check_every == 0:
                w.check()
                check_states(w, life, f"{tag} tick {t}")
                peak_draws = max(peak_draws, check_draws(w, f"{tag} tick {t}", cap))
            kids = sum(w.counter(w.active_counter(c)) for c, e in zip(w.chunks, w.entries) if e.depth == 1)
            peak = max(peak, kids)
        ms = w.gpu_ms
        r = dict(ticks=ticks, instances=len(entries) // 2, peak_particles=peak, peak_draws=peak_draws, cap=cap,
                 gpu_ms_avg=round(sum(ms) / len(ms), 4), gpu_ms_max=round(max(ms), 4), wall_s=round(time.time() - t0, 1))
        results[tag] = r
        print(tag, r, flush=True)
        return w

    # Smallest first.
    run("one pair, 64 particles", pair(emitter_block(rate=600.0, duration=0.0), sprite_block(life=(0.05, 0.2)), 64), 200, (0.05, 0.2), 256)
    run("one pair, 1024 particles", pair(emitter_block(rate=3000.0), sprite_block(life=(0.1, 0.4)), 1024), 200, (0.1, 0.4), 2048)
    run("one pair, 4096 particles, churn", pair(emitter_block(rate=4000.0), sprite_block(life=(0.3, 1.2)), 4096), 300, (0.3, 1.2), 8192)
    # Many instances run in the same depth with no barrier between them: all fight over one draw counter.
    entries = []
    for i in range(32):
        entries += pair(emitter_block(rate=1500.0), sprite_block(life=(0.2, 0.6)), 128, base=2 * i)
    run("32 pairs, 128 particles each", entries, 200, (0.2, 0.6), 8192)
    # List overflow under contention: far more draws than capacity, the counter must settle exactly at capacity.
    w = run("overflow, 2048 particles, capacity 64", pair(emitter_block(burst=2048, duration=5.0), sprite_block(life=(10, 10)), 2048), 60, (10, 10), 64)
    assert w.counter(w.draw_counters["count"]) == 64
    results["overflow settled at capacity"] = True
    # Spawn budget: a burst far over maxCount.
    w = run("spawn over budget", pair(emitter_block(burst=100000, duration=3.0), sprite_block(life=(100, 100)), 256), 60, (100, 100), 512)
    assert w.counter(w.active_counter(w.chunks[1])) == 256
    results["spawn stopped at maxCount"] = True
    # Whole life and death, then everything returned: many cycles.
    entries = []
    for i in range(8):
        entries += pair(emitter_block(burst=128, duration=0.05), sprite_block(life=(0.1, 0.3)), 256, base=2 * i)
    w = World(entries, cap=4096)
    for t in range(120):
        w.tick()
        w.check()
    for c, e in zip(w.chunks, w.entries):
        assert w.counter(w.active_counter(c)) == 0, "everything died"
        assert w.counter(c.counter_free) == c.count, "everything returned"
    results["8 bursts lived and died, all slots returned"] = True
    (RES / "stress.json").write_text(json.dumps(dict(device=DEV.info, results=results, time=time.ctime()), indent=1))
    print("stress ok", flush=True)


def compile_times():
    """vkCreateComputePipelines with no cache (driver disk caches are off in gpu.py)."""
    import ctypes
    lib = DEV.lib
    def once(code):
        ms = ctypes.c_double()
        DEV.tag(f"compiling a {len(code)} B module")
        p = lib.vg_compute_pipeline(gpu.VFX, bytes(code), len(code), b"cs_main", ctypes.byref(ms))
        DEV.ok(p, "compile")
        return ms.value
    out = {}
    for name, code in (("kiln_emitter", EMITTER), ("kiln_sprite", SPRITE), ("kiln_light", LIGHT)):
        out[f"{name} ({len(code)} B)"] = [round(once(code), 1) for _ in range(5)]
        print(name, out[f"{name} ({len(code)} B)"], flush=True)
    if gpu.GPC is None:
        (RES / "compile.json").write_text(json.dumps(dict(device=DEV.info, ms=out, time=time.ctime()), indent=1))
        return
    # The game's own modules, for scale. Read from the installed game, compiled, thrown away.
    gp = gpu.GPC
    tv = gp.types.get("keen::VfxResource")["hash"]
    sizes = {}
    for k in gp.kfc.resources_of_type(tv):
        v = gp.dec.decode(tv, gp.kfc.read(k))
        for p in v["particles"]:
            h = p["strippedCode"]["hash"]
            sizes.setdefault(h["size"], (h["size"], h["hash0"], h["hash1"], h["hash2"]))
    ordered = sorted(sizes)
    picks = [ordered[0], ordered[len(ordered) // 10], ordered[len(ordered) // 2], ordered[len(ordered) * 9 // 10]] + ordered[-4:]
    for s in picks:
        code = gp.kfc.read_content(gp.CH(*sizes[s]))
        t = time.time()
        ms = once(code)
        out[f"game module {s} B"] = [round(ms, 1)]
        print(f"game module {s} B: {ms:.1f} ms", flush=True)
    (RES / "compile.json").write_text(json.dumps(dict(device=DEV.info, ms=out, time=time.ctime()), indent=1))


if args.stage == "suite":
    run_suite()
elif args.stage == "stress":
    stress()
elif args.stage == "compile":
    compile_times()
elif args.stage == "render":
    import render
    render.run(DEV, World, RES)
elif args.stage == "cooked":
    import cooked
    cooked.run(DEV, World, RES, args.cooked)
gpu.GPU.lib.vg_shutdown()
if VALIDATE:
    findings = DEV.validation()
    (RES / f"validation-{args.stage}.json").write_text(json.dumps(findings, indent=1))
    errors = [f for f in findings if f["severity"] == "error"]
    print(f"validation ({args.validate}), {args.stage}: {len(errors)} kinds of error, {len(findings) - len(errors)} kinds of warning", flush=True)
    for f in findings[:40]:
        print(f"  {f['severity']} x{f['count']} {f['id']} [{f['first_during']}]", flush=True)
