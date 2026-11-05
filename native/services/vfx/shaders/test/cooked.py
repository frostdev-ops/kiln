# SPDX-License-Identifier: MIT
"""Runs a cooked mod's VFX on the GPU: the bytes kiln.assets would serve, patched the way the game patches them on activation.

    python3 test_gpu.py --device NVIDIA --stage cooked --cooked <kiln-vfxcook --out folder>

It checks what a builder can get wrong: blob names against their real content hash, entry wiring, stop times, list
bookkeeping on real dispatches, and that the records draw with the cooked mesh and texture.
"""
import json
import struct
import sys
from pathlib import Path

import numpy as np

import gpu
import render as R
from harness import P, Entry

ROOT = Path(__file__).resolve().parents[5]
sys.path.insert(0, str(ROOT / "tools/vfxcook"))

BC7_SRGB = 146
BC7_UNORM = 145
MESH_INDEX = 1


def chain_bytes(w, h, levels):
    """BC7: 16 bytes per 4x4 block, mips largest first."""
    total = 0
    for _ in range(levels):
        total += ((w + 3) // 4) * ((h + 3) // 4) * 16
        w, h = max(w // 2, 1), max(h // 2, 1)
    return total


def blob_name(h):
    return f"{h['size']:x}-{h['hash0']:08x}-{h['hash1']:08x}-{h['hash2']:08x}"


class Cooked:
    def __init__(self, folder):
        from kfc.blob import Decoder
        from kfc.contenthash import content_hash
        from vfxcook.layouts import types
        self.folder = Path(folder)
        self.report = json.loads((self.folder / "report.json").read_text())
        self.dec = Decoder(types())
        self.types = types()
        self.blobs = {}
        self.problems = []
        for b in self.report["served"]["blobs"]:
            data = (self.folder / b["file"]).read_bytes()
            self.blobs[b["hash"]] = data
            want = tuple(int(x, 16) for x in b["hash"].split("-"))
            if content_hash(data) != want:
                self.problems.append(f"{b['file']}: name says {b['hash']}, content hash is {content_hash(data)}")
        self.res = {}
        for r in self.report["served"]["resources"]:
            raw = (self.folder / r["file"]).read_bytes()
            self.res[r["guid"]] = (r, raw, self.dec.decode(r["type"], raw))
        self.names = {b["guid"]: b for b in self.report["blocks"]}
        self.slots = {}   # texture guid -> bindless slot

    def by_type(self, t):
        return [v for v in self.res.values() if v[0]["type"] == t]

    def entries(self, v):
        """(entries, patched gpuResourceData) for a decoded VfxResource."""
        g = v["gpuResourceData"]
        raw = bytearray(bytes.fromhex(g["bytes"]) if isinstance(g, dict) else bytes(g))
        for x in v["meshes"]:
            struct.pack_into("<I", raw, x["patchPoint"]["resourceDataRange"]["start"], MESH_INDEX)
        for x in v["images"]:
            struct.pack_into("<II", raw, x["patchPoint"]["resourceDataRange"]["start"], self.slots.get(x["image"], 0), R.SAMPLER)
        out = []
        for p in v["particles"]:
            code = self.blobs[blob_name(p["strippedCode"]["hash"])]
            e = Entry(code, depth=p["depth"], parent=p["parent"], child=p["child"], max_count=p["maxCount"], state_size=p["stateSizeInBytes"])
            e.res_start = p["evaluateParametersGpuResourceDataRange"]["start"]
            out.append(e)
        return out, bytes(raw)

    def wiring(self, name, v):
        """What the game's chunk code relies on."""
        bad = []
        ps = v["particles"]
        for x in v["images"]:
            if x["image"] not in self.slots:
                bad.append(f"image {x['image']} isn't a texture this cook could upload")
        if v["emitterCount"] != sum(1 for p in ps if p["child"] != 0):
            bad.append("emitterCount != entries with a child")
        for i, p in enumerate(ps):
            ref = p["strippedCode"]
            if ref["category"]["value"] != 2603049049 or ref["bundleId"] != 1:
                bad.append(f"entry {i}: strippedCode category or bundle")
            if blob_name(ref["hash"]) not in self.blobs:
                bad.append(f"entry {i}: module blob missing")
            if p["depth"] == 0 and p["parent"] != 0xffffffff:
                bad.append(f"entry {i}: root with a parent")
            if p["child"]:
                c = ps[p["child"]]
                if c["parent"] != i or c["depth"] != 1:
                    bad.append(f"entry {i}: child doesn't point back")
            if p["depth"] and p["maxCount"] > P["KILN_VFX_MAX_PARTICLES"]:
                bad.append(f"entry {i}: maxCount")
            info = v["stateInfos"][i]
            if info["sizeInBytes"] != p["stateSizeInBytes"]:
                bad.append(f"entry {i}: stateInfos size")
            e = p["evaluateParametersGpuResourceDataRange"]
            g = v["gpuResourceData"]
            size = len(bytes.fromhex(g["bytes"])) if isinstance(g, dict) else len(g)
            if e["start"] % 4 or e["start"] + e["count"] > size:
                bad.append(f"entry {i}: parameter range")
        if (len(bytes.fromhex(v["gpuResourceData"]["bytes"])) if isinstance(v["gpuResourceData"], dict) else len(v["gpuResourceData"])) % 4:
            bad.append("gpuResourceData not a multiple of 4")
        return bad


def run(dev, WorldClass, res, folder):
    c = Cooked(folder)
    results = dict(device=dev.info, problems=list(c.problems), vfx={})
    # The cooked quad and texture.
    mesh_guid, (mesh_r, _, mesh) = [(k, v) for k, v in c.res.items() if v[0]["type"] == "keen::VfxMeshResource"][0]
    mesh_blob = c.blobs[blob_name(mesh["meshData"]["hash"])]
    results["mesh"] = dict(vertices=mesh["vertexCount"], indices=mesh["indexCount"], bytes=len(mesh_blob), multiple_of_28=len(mesh_blob) % 28 == 0,
                           same_as_harness_quad=mesh_blob == R.quad_mesh())
    mesh_row = struct.pack("<4I4f", 0, 28 * mesh["vertexCount"] // 2, mesh["indexCount"], mesh["vertexCount"], *[mesh["boundingSphere"][k] for k in "xyzw"])

    r = R.Renderer(dev, res)
    r.mesh_view = None
    # Rebuild the renderer's mesh from the cooked blob, not its own.
    r.mesh_id, view = dev.buffer(len(mesh_blob))
    view[:] = mesh_blob
    if r.real_shaders:
        dev.ok(dev.lib.vg_bind_buffer(r.layout, r.names["g_meshData"][0], r.names["g_meshData"][1], r.mesh_id), "mesh")
        # Lit sprites need some ambient to show.
        dev.ok(dev.lib.vg_bind_image(r.layout, r.names["g_fog3ViewVolumeAmbientAndPointLightRadiance"][0], r.names["g_fog3ViewVolumeAmbientAndPointLightRadiance"][1], 0, r.white3), "ambient")
    else:
        dev.ok(dev.lib.vg_bind_buffer(r.layout, 0, 1, r.mesh_id), "mesh")
    # Each texture gets its own bindless slot and each effect the one it names. The whole mip chain goes up. The ranges in
    # mipChainContentRanges are streaming tiers (the first spans the whole chain), not one per mip.
    results["textures"] = []
    for n, (meta, _, t) in enumerate(c.by_type("keen::RenderTextureResource")):
        data = c.blobs[blob_name(t["content"]["hash"])]
        results["textures"].append(dict(guid=meta["guid"], width=t["width"], height=t["height"], levels=t["levelCount"], format=t["format"],
                                        bindless=t["bindless"], bytes=len(data), ranges=t["mipChainContentRanges"]))
        fmt = {"BC7_srgb_block": BC7_SRGB, "BC7_unorm_block": BC7_UNORM}.get(t["format"])
        want = chain_bytes(t["width"], t["height"], t["levelCount"])
        if fmt is None:
            results["problems"].append(f"texture {meta['guid']}: format {t['format']} not handled here")
        elif len(data) != want:
            results["problems"].append(f"texture {meta['guid']}: {len(data)} bytes, its mip chain needs {want}")
        else:
            i = dev.ok(dev.lib.vg_image_bc7(t["width"], t["height"], fmt, t["levelCount"], data, len(data)), "texture")
            slot = R.IMAGE + n
            dev.ok(dev.lib.vg_bind_image(r.layout, r.bindless, 0, slot, i), "bindless")
            c.slots[meta["guid"]] = slot
    r.constants((0.0, 0.0, 0.0, 1.0))

    for r_, raw, v in c.by_type("keen::VfxResource"):
        guid = r_["guid"]
        info = c.names[guid]
        name = info["name"]
        out = dict(entries=len(v["particles"]), emitterCount=v["emitterCount"], killTime=v["killTimeInSeconds"], cull=(v["cullingDistanceMin"], v["cullingDistanceMax"]))
        out["wiring_problems"] = c.wiring(name, v)
        entries, data = c.entries(v)
        w = WorldClass(entries, resource=data, cap=1024, position=(0.0, -0.6, 3.0))
        w.buf[(1, 17)].data[32:64] = mesh_row
        R.set_camera(w, (0.0, 0.0, 0.0, 1.0))

        frames, counts, lights = [], [], []
        stop_after = info.get("stopAfterSeconds")
        peak = 0
        steps = 120
        for t in range(1, steps + 1):
            w.tick()
            w.check()
            n = w.counter(w.draw_counters["count"])
            nl = w.counter(w.draw_counters["light"])
            peak = max(peak, sum(w.counter(w.active_counter(ch)) for ch, e in zip(w.chunks, w.entries) if e.depth == 1))
            if t in (1, 10, 20, 40, 60, 90):
                counts.append(dict(tick=t, sprites=n, lights=nl))
                if n:
                    r.draw(w, 3, n, clear=(0.02, 0.02, 0.03, 1.0))
                    frames.append(r.image())
            if nl:
                lights.append(nl)
            if stop_after is not None and t == int(np.ceil(stop_after * 60)) + 1:
                alive = {i: w.counter(w.active_counter(ch)) for i, ch in enumerate(w.chunks) if w.counter(w.active_counter(ch))}
                out["alive_at_stop_time"] = alive
        out["counts"] = counts
        out["peak_particles"] = peak
        out["ticks_with_a_light"] = len(lights)
        out["all_finished_at_end"] = all(w.counter(w.active_counter(ch)) == 0 for ch in w.chunks)
        if frames:
            while len(frames) < 6:
                frames.append(frames[-1])
            r.save(np.concatenate([np.concatenate(frames[:3], axis=1), np.concatenate(frames[3:6], axis=1)], axis=0), f"cooked_{name}.png")
        # After stopping, an instance must end: a looping effect's emitter dies at once, its particles finish.
        if info.get("loops"):
            w.set_instance(flags=0)
            for _ in range(180):
                w.tick()
                w.check()
            out["finished_after_stop"] = all(w.counter(w.active_counter(ch)) == 0 for ch in w.chunks)
        results["vfx"][name] = out
    (res / "cooked.json").write_text(json.dumps(results, indent=1, default=str))
    print(json.dumps(results, indent=1, default=str), flush=True)
