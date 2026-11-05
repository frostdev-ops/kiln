# SPDX-License-Identifier: MIT
"""kiln-vfxcook tests: the builder's bytes, against tools/kfc and the starter modules' CPU harness.

    python3 -m unittest discover -s tools/vfxcook/tests -v

No game files are needed. With the installed client (read only; skipped when absent): the rules in kfc.vfx hold for every game VFX, mesh
and texture they claim to, the hand-written layouts match reflection, and the fingerprint and budget match this build.

What this can't show: anything about a real GPU, whether the game accepts the data, or how it looks.
"""
import json
import math
import os
import random
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
import uuid
import zlib
from pathlib import Path

HERE = Path(__file__).resolve().parent
TOOL = HERE.parent
REPO = TOOL.parents[1]
SAMPLE = TOOL / "samples" / "frost"
SHADERS = REPO / "native" / "services" / "vfx" / "shaders"
sys.path[:0] = [str(TOOL), str(REPO / "tools" / "kfc"), str(SHADERS / "test")]

from harness import LIGHTS, P, Entry, World  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.blobwrite import Encoder  # noqa: E402
from kfc.contenthash import content_hash, keen_hash128  # noqa: E402
from kfc import vfx as checks  # noqa: E402
from spvsim import Sim  # noqa: E402
from vfxcook.layouts import types  # noqa: E402

GAME = Path(os.environ.get("KILN_TEST_GAME", Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"))
HAS_GAME = (GAME / "enshrouded.kfc").exists()
T = types()
DEC = Decoder(T, max_array=1 << 26)
ENC = Encoder(T)
NS = uuid.UUID("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77")
TYPES = {"keen::VfxResource": 0xe64ee036, "keen::VfxMeshResource": 0x8e2c1e03, "keen::RenderTextureResource": 0x3002d39d}
SIMS = {}


def tool():
    cached = getattr(tool, "path", None)
    if cached:
        return cached
    env = dict(os.environ)
    out = subprocess.run([str(TOOL / "native" / "build.sh")], capture_output=True, text=True, env=env)
    if out.returncode:
        raise RuntimeError("kiln-vfxcook did not build:\n" + out.stderr[-4000:])
    tool.path = out.stdout.strip().splitlines()[-1]
    return tool.path


def run(*args, ok=(0,)):
    p = subprocess.run([tool(), *map(str, args)], capture_output=True, text=True)
    if p.returncode not in ok:
        raise AssertionError(f"kiln-vfxcook {args}: {p.returncode}\n{p.stdout}\n{p.stderr}")
    return p


def png(width, height, rgba=(255, 255, 255, 255), colortype=6):
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d))
    row = b"\0" + bytes(rgba) * width
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, colortype, 0, 0, 0)) + \
        chunk(b"IDAT", zlib.compress(row * height)) + chunk(b"IEND", b"")


class Mod:
    """A mod folder in a temp dir."""

    def __init__(self, root, mod_id, vfx, side="both", clients="mod", files=None, raw_doc=None):
        self.id = mod_id
        self.dir = Path(root) / mod_id
        (self.dir / "blocks").mkdir(parents=True)
        (self.dir / "kiln.mod.json").write_text(json.dumps({"id": mod_id, "version": "1.0.0", "side": side, "clients": clients}))
        (self.dir / "blocks" / "b.json").write_text(raw_doc if raw_doc is not None else json.dumps({"format": 1, "vfx": vfx}))
        for rel, data in (files or {}).items():
            (self.dir / rel).parent.mkdir(parents=True, exist_ok=True)
            (self.dir / rel).write_bytes(data)


class Cooked:
    def __init__(self, out):
        self.out = Path(out)
        self.report = json.loads((self.out / "report.json").read_text())
        self.manifest = json.loads((self.out / "assets.json").read_text())

    def blocks(self):
        return {b["name"]: b for b in self.report["blocks"]}

    def resource(self, guid, type_name):
        rel = next(r["file"] for r in self.report["served"]["resources"] if r["guid"] == guid and r["type"] == type_name)
        return (self.out / rel).read_bytes()

    def decoded(self, guid, type_name):
        return DEC.decode(type_name, self.resource(guid, type_name))

    def blob(self, ref_hash):
        text = "-".join(f"{x:x}" for x in (ref_hash["size"], ref_hash["hash0"], ref_hash["hash1"], ref_hash["hash2"]))
        return (self.out / "cooked" / f"blob-{text}.bin").read_bytes()

    def resources_of(self, type_name):
        return [r for r in self.report["served"]["resources"] if r["type"] == type_name]


def cook(*mods, extra=(), ok=(0,), tmp=None):
    out = Path(tmp) / "out"
    if out.exists():
        shutil.rmtree(out)
    args = ["cook", "--out", out]
    for m in mods:
        args += ["--mod", m.dir if isinstance(m, Mod) else m]
    p = run(*args, *extra, ok=ok)
    c = Cooked(out)
    c.stderr = p.stderr
    c.returncode = p.returncode
    return c


def sprite(**kw):
    d = {"burst": 8, "lifetime": 0.5}
    d.update(kw)
    return d


def patched(v, mesh_index=1, image_index=7, sampler_index=2):
    """gpuResourceData with the patch points applied, as the game does at activation (client 0x1afb50)."""
    data = bytearray(checks.resource_data(v))
    for pp in v["meshes"]:
        struct.pack_into("<I", data, pp["patchPoint"]["resourceDataRange"]["start"], mesh_index)
    for pp in v["images"]:
        struct.pack_into("<II", data, pp["patchPoint"]["resourceDataRange"]["start"], image_index, sampler_index)
    return bytes(data)


def entries_of(cooked, v, mesh_index=1):
    out = []
    for p in v["particles"]:
        code = cooked.blob(p["strippedCode"]["hash"])
        sim = SIMS.setdefault(code, Sim(code))
        e = Entry(sim, depth=p["depth"], parent=p["parent"], child=p["child"], max_count=p["maxCount"], state_size=p["stateSizeInBytes"])
        e.res_start = p["evaluateParametersGpuResourceDataRange"]["start"]
        out.append(e)
    return out


class Base(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()


class ContentHashAgrees(Base):
    """The C++ port and the reference, on shared vectors. A wrong hash on a shader blob is fatal in the game."""

    def _both(self, blobs):
        files = []
        for i, b in enumerate(blobs):
            f = self.tmp / f"{i}.bin"
            f.write_bytes(b)
            files.append(f)
        lines = run("hash", *files).stdout.splitlines()
        self.assertEqual(len(lines), len(blobs))
        for b, line in zip(blobs, lines):
            h128, text, _ = line.split(" ", 2)
            self.assertEqual(h128, keen_hash128(b).hex(), f"{len(b)} bytes")
            self.assertEqual(text, "-".join(f"{x:x}" for x in content_hash(b)), f"{len(b)} bytes")

    def test_every_length_around_the_block_sizes(self):
        data = bytes((i * 131 + 7) & 0xff for i in range(400))
        self._both([data[:n] for n in range(0, 200)] + [data])

    def test_random_blobs(self):
        r = random.Random(1234)
        self._both([r.randbytes(r.randrange(1, 70000)) for _ in range(30)])

    def test_the_known_vectors(self):
        vectors = {b"a": "6a2924cb720b42a7dfa8c9f7d2961b9e", bytes(range(16)): "525399fa7c32a7d3ce7ce572cf12109f",
                   bytes(range(64)): "65894db736bd54ce7e0ef8d8d5e4a08c", bytes(range(100)): "c0a73764cb9aa382a53bb0f732f86207",
                   bytes(range(256)) * 3: "fc6484a4598e2f95104fb940ebf4e865"}
        files = []
        for i, b in enumerate(vectors):
            (self.tmp / f"{i}").write_bytes(b)
            files.append(self.tmp / f"{i}")
        got = [x.split()[0] for x in run("hash", *files).stdout.splitlines()]
        self.assertEqual(got, list(vectors.values()))

    def test_the_empty_hash_is_the_games(self):
        f = self.tmp / "empty"
        f.write_bytes(b"")
        self.assertEqual(run("hash", f).stdout.split()[1], "0-e93d8778-6d71d111-fd535c96")

    def test_the_starter_modules(self):
        mods = json.loads(run("modules").stdout)
        self.assertEqual([m["name"] for m in mods], ["kiln_emitter", "kiln_sprite", "kiln_light"])
        for m in mods:
            code = (SHADERS / "build" / f"{m['name']}.spv").read_bytes()
            self.assertEqual(m["size"], len(code), "embedded as committed")
            self.assertEqual(m["hash"], "-".join(f"{x:x}" for x in content_hash(code)))
            self.assertEqual(m["check"], "ok")
            self.assertEqual(checks.check_module(code), [])


class Ids(Base):
    def test_a_block_id_is_uuid5_as_kiln_blocks_makes_it(self):
        for mod, kind, name in (("frost-fx", "vfx", "frost_burst"), ("a", "vfx", "b.c-d_e"), ("módulo", "vfx", "é")):
            self.assertEqual(run("id", mod, kind, name).stdout.strip(), str(uuid.uuid5(NS, f"{mod}:{kind}:{name}")))

    def test_was_keeps_the_id(self):
        a = cook(Mod(self.tmp / "a", "m", {"new_name": {"was": "old", "light": {}}}), tmp=self.tmp / "a")
        self.assertEqual(a.blocks()["new_name"]["guid"], str(uuid.uuid5(NS, "m:vfx:old")))


class SampleBytes(Base):
    """The sample mod through the same checks real game data passes."""

    def setUp(self):
        super().setUp()
        self.c = cook(SAMPLE, tmp=self.tmp)

    def test_round_trips_byte_identical(self):
        n = 0
        for r in self.c.report["served"]["resources"]:
            raw = (self.c.out / r["file"]).read_bytes()
            v = DEC.decode(r["type"], raw)
            self.assertEqual(ENC.encode(r["type"], v), raw, r["what"])
            n += 1
        self.assertEqual(n, 6)

    def test_every_vfx_passes_the_structural_checks(self):
        for r in self.c.resources_of("keen::VfxResource"):
            v = DEC.decode(r["type"], (self.c.out / r["file"]).read_bytes())
            self.assertEqual(checks.check_vfx(v, strict=True), [], r["what"])

    def test_every_mesh_and_texture_passes(self):
        for r in self.c.resources_of("keen::VfxMeshResource"):
            m = DEC.decode(r["type"], (self.c.out / r["file"]).read_bytes())
            self.assertEqual(checks.check_mesh(m, self.c.blob(m["meshData"]["hash"])), [])
        tex = self.c.resources_of("keen::RenderTextureResource")
        sizes = {}
        for r in tex:
            t = DEC.decode(r["type"], (self.c.out / r["file"]).read_bytes())
            self.assertEqual(checks.check_texture(t, len(self.c.blob(t["content"]["hash"]))), [])
            self.assertEqual(t["format"], "BC7_srgb_block")
            sizes[t["width"]] = (t["height"], t["levelCount"])
        self.assertEqual(sizes, {64: (64, 7), 128: (128, 8)})

    def test_blobs_carry_their_real_content_hash(self):
        for b in self.c.report["served"]["blobs"]:
            data = (self.c.out / b["file"]).read_bytes()
            self.assertEqual(b["hash"], "-".join(f"{x:x}" for x in content_hash(data)), b["what"])

    def test_strippedcode_names_the_right_module(self):
        names = {"kiln_emitter": P["KE_STATE_SIZE"], "kiln_sprite": P["KS_STATE_SIZE"], "kiln_light": P["KL_STATE_SIZE"]}
        for r in self.c.resources_of("keen::VfxResource"):
            v = DEC.decode(r["type"], (self.c.out / r["file"]).read_bytes())
            for p in v["particles"]:
                code = self.c.blob(p["strippedCode"]["hash"])
                self.assertEqual(checks.check_module(code), [])
                which = next(n for n in names if (SHADERS / "build" / f"{n}.spv").read_bytes() == code)
                self.assertEqual(p["stateSizeInBytes"], names[which])

    def test_every_guid_a_vfx_names_is_served(self):
        served = {r["guid"] for r in self.c.report["served"]["resources"]}
        blobs = {b["hash"] for b in self.c.report["served"]["blobs"]}
        for r in self.c.resources_of("keen::VfxResource"):
            v = DEC.decode(r["type"], (self.c.out / r["file"]).read_bytes())
            for pp in v["meshes"]:
                self.assertIn(pp["mesh"], served)
            for pp in v["images"]:
                self.assertIn(pp["image"], served)
            for p in v["particles"]:
                h = p["strippedCode"]["hash"]
                self.assertIn("-".join(f"{h[k]:x}" for k in ("size", "hash0", "hash1", "hash2")), blobs)

    def test_the_manifest_lists_it_all_for_clients_only(self):
        kinds = {}
        for e in self.c.manifest["entries"]:
            self.assertEqual(e["side"], "client")
            self.assertTrue((self.c.out / e["file"]).exists())
            kinds[e["kind"]] = kinds.get(e["kind"], 0) + 1
            if e["kind"] == "resource":
                self.assertEqual(e["mode"], "new")
                self.assertIn(e["type"], TYPES)
        self.assertEqual(kinds, {"blob": 6, "resource": 6})

    def test_same_input_same_bytes(self):
        again = cook(SAMPLE, tmp=self.tmp / "again")
        for r in self.c.report["served"]["resources"] + self.c.report["served"]["blobs"]:
            self.assertEqual((self.c.out / r["file"]).read_bytes(), (again.out / r["file"]).read_bytes(), r["what"])

    def test_the_quad_uses_the_documented_numbers(self):
        m = self.c.resources_of("keen::VfxMeshResource")[0]
        raw = (self.c.out / m["file"]).read_bytes()
        d = DEC.decode(m["type"], raw)
        self.assertEqual((d["vertexCount"], d["indexCount"]), (4, 6))
        blob = self.c.blob(d["meshData"]["hash"])
        self.assertEqual(len(blob), 140)
        self.assertEqual(struct.unpack_from("<6H", blob, 112), (0, 1, 2, 2, 3, 0))
        self.assertEqual(blob[124:], bytes(16))
        for i, (pos, uv) in enumerate((((-.5, .5, 0), (0, 0)), ((-.5, -.5, 0), (0, 1)), ((.5, -.5, 0), (1, 1)), ((.5, .5, 0), (1, 0)))):
            self.assertEqual(struct.unpack_from("<3f", blob, i * 28), pos)
            self.assertEqual(tuple(checks.half_to_float(x) for x in struct.unpack_from("<2H", blob, i * 28 + 12)), uv)
            self.assertEqual(struct.unpack_from("<3I", blob, i * 28 + 16), (0x00200400, 0x200803ff, 0xffffffff))
        self.assertAlmostEqual(d["boundingSphere"]["w"], 0.7071068, 6)


class FieldValues(Base):
    """What the definition becomes, field by field (the layout in kiln_vfx_params.h)."""

    def one(self, block, files=None):
        c = cook(Mod(self.tmp / "m", "m", {"x": block}, files=files), tmp=self.tmp / "m")
        guid = c.blocks()["x"]["guid"]
        return c, c.decoded(guid, "keen::VfxResource"), checks.resource_data(c.decoded(guid, "keen::VfxResource"))

    @staticmethod
    def f(data, start, off):
        return struct.unpack_from("<f", data, start + P[off])[0]

    @staticmethod
    def u(data, start, off):
        return struct.unpack_from("<I", data, start + P[off])[0]

    def test_an_entry_recipe(self):
        _, v, _ = self.one({"particles": [sprite(), sprite()], "light": {}})
        ps = v["particles"]
        self.assertEqual(len(ps), 5)
        self.assertEqual(v["emitterCount"], 2)
        self.assertEqual([p["depth"] for p in ps], [0, 1, 0, 1, 0])
        self.assertEqual([p["child"] for p in ps], [1, 0, 3, 0, 0])
        self.assertEqual([p["parent"] for p in ps], [0xffffffff, 0, 0xffffffff, 2, 0xffffffff])
        self.assertEqual([p["stateSizeInBytes"] for p in ps], [P["KE_STATE_SIZE"], P["KS_STATE_SIZE"], P["KE_STATE_SIZE"], P["KS_STATE_SIZE"], P["KL_STATE_SIZE"]])
        self.assertEqual([p["strippedCode"]["sortKey"] for p in ps], list(range(5)))
        self.assertTrue(all(p["strippedCode"]["category"]["value"] == 2603049049 and p["strippedCode"]["bundleId"] == 1 for p in ps))
        self.assertTrue(all(p["code"]["hash"]["size"] == 0 for p in ps))
        self.assertEqual(ps[0]["maxCount"], 1)
        self.assertEqual((v["cullingDistanceMin"], v["cullingDistanceMax"]), (50.0, 75.0))
        self.assertEqual(v["killTimeInSeconds"], 10)
        self.assertEqual([x["sizeInBytes"] for x in v["stateInfos"]], [p["stateSizeInBytes"] for p in ps])
        self.assertEqual([m["offsetInBytes"] for m in v["stateInfos"][1]["members"]],
                         [P["KS_S_AGE"], P["KS_S_LIFE"], P["KS_S_POS"], P["KS_S_VEL"], P["KS_S_ROLL"], P["KS_S_SPIN"]])

    def test_a_burst_alone_gets_a_small_duration(self):
        _, v, d = self.one({"particles": [{"burst": 24}]})
        self.assertGreater(self.f(d, 0, "KE_DURATION"), 0.0)
        self.assertLess(self.f(d, 0, "KE_DURATION"), 0.01)
        self.assertEqual(self.u(d, 0, "KE_BURST"), 24)
        self.assertEqual(self.u(d, 0, "KE_VERSION"), P["KILN_VFX_ABI"])
        self.assertEqual(self.u(d, 0, "KE_CHILD_STATE"), P["KS_STATE_SIZE"])

    def test_a_rate_runs_until_stopped_unless_it_has_a_duration(self):
        c, _, d = self.one({"particles": [{"rate": 10}]})
        self.assertEqual(self.f(d, 0, "KE_DURATION"), 0.0)
        self.assertTrue(c.blocks()["x"]["loops"])
        self.assertNotIn("stopAfterSeconds", c.blocks()["x"])

    def test_a_finite_block_says_when_to_stop(self):
        c = cook(Mod(self.tmp / "m", "m", {"x": {"particles": [{"rate": 10, "duration": 2, "delay": 0.5, "lifetime": [0.5, 1]}]}}), tmp=self.tmp / "m")
        b = c.blocks()["x"]
        self.assertFalse(b["loops"])
        self.assertAlmostEqual(b["stopAfterSeconds"], 3.5, 3)

    def test_max_count_follows_what_the_block_can_keep_alive(self):
        _, v, _ = self.one({"particles": [{"burst": 24, "lifetime": 1}]})
        self.assertEqual(v["particles"][1]["maxCount"], 24)

    def test_max_count_derived_with_a_rate(self):
        c = cook(Mod(self.tmp / "r", "r", {"x": {"particles": [{"rate": 100, "lifetime": 2}]}}), tmp=self.tmp / "r")
        v = c.decoded(c.blocks()["x"]["guid"], "keen::VfxResource")
        self.assertEqual(v["particles"][1]["maxCount"], 201)

    def test_loops_and_bursts_overlap(self):
        c = cook(Mod(self.tmp / "r", "r", {"x": {"particles": [{"burst": 10, "loop": True, "duration": 0.5, "lifetime": 1}]}}), tmp=self.tmp / "r")
        v = c.decoded(c.blocks()["x"]["guid"], "keen::VfxResource")
        self.assertEqual(v["particles"][1]["maxCount"], 30)

    def test_a_derived_count_holds_at_the_limit(self):
        c = cook(Mod(self.tmp / "r", "r", {"x": {"particles": [{"rate": 10000, "lifetime": 10}]}}), tmp=self.tmp / "r")
        v = c.decoded(c.blocks()["x"]["guid"], "keen::VfxResource")
        self.assertEqual(v["particles"][1]["maxCount"], 4096)
        self.assertIn("held to 4096", c.stderr)

    def test_sprite_fields(self):
        files = {"vfx/a.png": png(8, 8)}
        _, v, d = self.one({"particles": [{
            "burst": 4, "lifetime": [0.4, 0.9], "speed": [2, 5], "spread": {"cone": 90}, "gravity": -4, "acceleration": [1, 2, 3], "drag": 0.5,
            "spawnRadius": 1.5, "spin": [0, 180], "roll": 90, "depthFade": 0.25, "emissive": 0.5, "blend": "additive", "lit": True,
            "flipbook": {"columns": 4, "rows": 2, "fps": 12}, "texture": "vfx/a.png", "sampler": "clampToZero",
        }]}, files=files)
        s = P["KE_BLOCK_SIZE"]
        close = self.assertAlmostEqual
        close(self.f(d, s, "KS_LIFE_MIN"), 0.4, 5)
        close(self.f(d, s, "KS_LIFE_MAX"), 0.9, 5)
        self.assertEqual((self.f(d, s, "KS_SPEED_MIN"), self.f(d, s, "KS_SPEED_MAX")), (2.0, 5.0))
        self.assertEqual(self.u(d, s, "KS_SPREAD_KIND"), P["KS_SPREAD_CONE"])
        close(self.f(d, s, "KS_SPREAD_ANGLE"), math.pi / 2, 5)
        self.assertEqual([self.f(d, s, k) for k in ("KS_ACCEL_X", "KS_ACCEL_Y", "KS_ACCEL_Z")], [1.0, -2.0, 3.0])
        self.assertEqual(self.f(d, s, "KS_DRAG"), 0.5)
        self.assertEqual(self.f(d, s, "KS_SPAWN_RADIUS"), 1.5)
        close(self.f(d, s, "KS_SPIN_MAX"), math.pi, 5)
        close(self.f(d, s, "KS_ROLL_MIN"), math.pi / 2, 5)
        self.assertEqual(self.f(d, s, "KS_DEPTH_FADE"), 0.25)
        self.assertEqual(self.f(d, s, "KS_EMISSIVE"), 0.5)
        self.assertEqual(self.u(d, s, "KS_DRAW_FLAGS"), P["KS_DRAW_ADDITIVE"] | P["KS_DRAW_LIT"])
        self.assertEqual(self.u(d, s, "KS_FLAGS"), P["KS_FLAG_FACE_CAMERA"])
        self.assertEqual(self.u(d, s, "KS_FLIPBOOK"), 4 | 2 << 16)
        self.assertEqual(self.f(d, s, "KS_FLIPBOOK_FPS"), 12.0)
        self.assertEqual((self.u(d, s, "KS_IMAGE"), self.u(d, s, "KS_SAMPLER"), self.u(d, s, "KS_MESH")), (0, 0, 0))
        img = v["images"][0]
        self.assertEqual(img["patchPoint"]["resourceDataRange"], {"start": s + P["KS_IMAGE"], "count": 8})
        self.assertEqual(img["sampler"], {"addressU": "ClampToZero", "addressV": "ClampToZero", "addressW": "Wrap"})
        self.assertEqual(v["meshes"][0]["patchPoint"]["resourceDataRange"], {"start": s + P["KS_MESH"], "count": 4})

    def test_no_texture_means_no_patch_point(self):
        _, v, _ = self.one({"particles": [{"burst": 1}]})
        self.assertEqual(v["images"], [])
        self.assertEqual(len(v["meshes"]), 1)

    def test_curves_sample_to_eight(self):
        _, _, d = self.one({"particles": [{"burst": 1, "size": {"linear": [0.15, 0]}, "alpha": [1, 0], "color": "#ffffff",
                                            }]})
        s = P["KE_BLOCK_SIZE"]
        size = struct.unpack_from("<8f", d, s + P["KS_SIZE_CURVE"])
        alpha = struct.unpack_from("<8f", d, s + P["KS_ALPHA_CURVE"])
        for i in range(8):
            self.assertAlmostEqual(size[i], 0.15 * (1 - i / 7), 5)
            self.assertAlmostEqual(alpha[i], 1 - i / 7, 5)

    def test_keys_are_piecewise_linear(self):
        _, _, d = self.one({"particles": [{"burst": 1, "size": {"keys": [[0, 0], [0.5, 1], [1, 0]]}}]})
        size = struct.unpack_from("<8f", d, P["KE_BLOCK_SIZE"] + P["KS_SIZE_CURVE"])
        for i in range(8):
            t = i / 7
            self.assertAlmostEqual(size[i], 1 - abs(2 * t - 1), 5)

    def test_colours_become_linear(self):
        _, _, d = self.one({"particles": [{"burst": 1, "color": ["#ffffff", "#000000"], "intensity": 2}]})
        s = P["KE_BLOCK_SIZE"] + P["KS_COLOR_CURVE"]
        first = struct.unpack_from("<3f", d, s)
        last = struct.unpack_from("<3f", d, s + 7 * 12)
        self.assertEqual(first, (2.0, 2.0, 2.0))
        self.assertEqual(last, (0.0, 0.0, 0.0))

    def test_srgb_conversion_values(self):
        c = cook(Mod(self.tmp / "c", "c", {"x": {"particles": [{"burst": 1, "color": "#7fd4ff"}]}}), tmp=self.tmp / "c")
        d = checks.resource_data(c.decoded(c.blocks()["x"]["guid"], "keen::VfxResource"))
        got = struct.unpack_from("<3f", d, P["KE_BLOCK_SIZE"] + P["KS_COLOR_CURVE"])
        def lin(b):
            x = b / 255
            return x / 12.92 if x <= 0.04045 else ((x + 0.055) / 1.055) ** 2.4
        for g, want in zip(got, (lin(0x7f), lin(0xd4), lin(0xff))):
            self.assertAlmostEqual(g, want, 5)

    def test_light_fields(self):
        _, v, d = self.one({"light": {"color": "#ffffff", "radius": 6, "brightness": 4, "lifetime": 2, "fadeIn": 0.25, "fadeOut": 0.5, "flicker": 0.5, "offset": [1, 2, 3]}})
        self.assertEqual(len(v["particles"]), 1)
        self.assertEqual(v["emitterCount"], 0)
        p = v["particles"][0]
        self.assertEqual((p["depth"], p["child"], p["parent"], p["maxCount"]), (0, 0, 0xffffffff, 1))
        self.assertEqual(p["evaluateParametersGpuResourceDataRange"], {"start": 0, "count": P["KL_BLOCK_SIZE"]})
        self.assertEqual(self.f(d, 0, "KL_RADIUS"), 6.0)
        self.assertEqual(self.f(d, 0, "KL_BRIGHTNESS"), 4.0)
        self.assertEqual(self.f(d, 0, "KL_LIFETIME"), 2.0)
        self.assertEqual(self.f(d, 0, "KL_FADE_IN"), 0.25)
        self.assertEqual(self.f(d, 0, "KL_FADE_OUT"), 0.5)
        self.assertEqual(self.f(d, 0, "KL_FLICKER"), 0.5)
        self.assertEqual(struct.unpack_from("<3f", d, P["KL_OFFSET"]), (1.0, 2.0, 3.0))
        self.assertEqual(struct.unpack_from("<3f", d, P["KL_COLOR"]), (1.0, 1.0, 1.0))

    def test_kill_time_covers_the_longest_life(self):
        _, v, _ = self.one({"particles": [{"burst": 1, "lifetime": 25.5}], "light": {"fadeOut": 2}})
        self.assertEqual(v["killTimeInSeconds"], 28)

    def test_cull_distance(self):
        _, v, _ = self.one({"light": {}, "cullDistance": [20, 30]})
        self.assertEqual((v["cullingDistanceMin"], v["cullingDistanceMax"]), (20.0, 30.0))

    def test_the_mesh_choice(self):
        c = cook(Mod(self.tmp / "q", "q", {"x": {"particles": [{"burst": 1, "mesh": "quad-xz"}, {"burst": 1}]}}), tmp=self.tmp / "q")
        v = c.decoded(c.blocks()["x"]["guid"], "keen::VfxResource")
        self.assertEqual(len({m["mesh"] for m in v["meshes"]}), 2)
        d = checks.resource_data(v)
        self.assertEqual(self.u(d, P["KE_BLOCK_SIZE"], "KS_FLAGS"), 0, "a flat quad keeps the instance's orientation")
        for r in c.resources_of("keen::VfxMeshResource"):
            m = DEC.decode(r["type"], (c.out / r["file"]).read_bytes())
            self.assertEqual(checks.check_mesh(m, c.blob(m["meshData"]["hash"])), [])


class OnTheModules(Base):
    """The built data, run on the starter modules through the CPU harness (spvsim) with the game's protocol."""

    def world(self, name, **kw):
        c = cook(SAMPLE, tmp=self.tmp)
        v = c.decoded(c.blocks()[name]["guid"], "keen::VfxResource")
        return c, v, World(entries_of(c, v), resource=patched(v), **kw)

    def test_frost_burst(self):
        c, v, w = self.world("frost_burst")
        first = []
        for i in range(90):
            w.tick()
            w.check()
            if i == 0:
                first = w.draws()
        self.assertEqual(len(first), 24, "24 sprites, drawn on the first tick")
        for d in first:
            self.assertEqual(d["meshIndex"], 1)
            self.assertEqual(d["image"], 7)
            self.assertEqual(d["imageSampler"], 2)
            self.assertEqual(d["flags"], P["KS_DRAW_ADDITIVE"])
            self.assertEqual(d["indexCount"], 6)
            self.assertLess(d["scale"][0], 0.16)
            self.assertGreater(d["alpha"], 0.9)
            self.assertAlmostEqual(d["color"][0], 1.0, delta=0.1)
        # The light comes on and goes with its lifetime.
        self.assertEqual(w.counter(w.active_counter(w.chunks[2])), 0)
        self.assertEqual(w.draws(), [], "1.5 s later nothing is left: lifetimes run out")
        self.assertEqual(w.counter(w.active_counter(w.chunks[1])), 0, "every particle went back to the free list")

    def test_the_emitter_finishes_after_a_burst(self):
        c, v, w = self.world("frost_burst")
        for _ in range(3):
            w.tick()
            w.check()
        self.assertEqual(w.counter(w.active_counter(w.chunks[0])), 0, "a burst-only emitter does not wait for a stop")

    def test_the_light_lights(self):
        c, v, w = self.world("frost_burst", position=(5.0, 6.0, 7.0))
        w.tick()
        w.check()
        self.assertEqual(w.counter(w.draw_counters["light"]), 1)
        self.assertEqual(struct.unpack_from("<3f", w.buf[LIGHTS].data, 0), (5.0, 6.0, 7.0))

    def test_frost_trail_keeps_going_until_it_stops(self):
        c, v, w = self.world("frost_trail")
        peak = 0
        for _ in range(180):
            w.tick()
            w.check()
            peak = max(peak, w.counter(w.active_counter(w.chunks[1])))
        self.assertLessEqual(peak, v["particles"][1]["maxCount"])
        self.assertGreater(peak, 40, "1.2 s of life at 40 per second")
        draws = w.draws()
        self.assertTrue(draws)
        self.assertEqual(draws[0]["flags"], P["KS_DRAW_LIT"])
        self.assertEqual(draws[0]["encodedFlipbookSize"], 2 | 2 << 16)
        # stop: the instance goes inactive, the emitter ends, particles run out
        w.set_instance(flags=0)
        for _ in range(120):
            w.tick()
            w.check()
        self.assertEqual(w.counter(w.active_counter(w.chunks[1])), 0)
        self.assertEqual(w.counter(w.active_counter(w.chunks[0])), 0)

    def test_cold_glow_is_one_light(self):
        c, v, w = self.world("cold_glow")
        for _ in range(30):
            w.tick()
            w.check()
        self.assertEqual(w.counter(w.draw_counters["light"]), 1)
        w.set_instance(flags=0)
        for _ in range(60):
            w.tick()
            w.check()
        self.assertEqual(w.counter(w.active_counter(w.chunks[0])), 0, "it fades out after the stop and ends")

    def test_all_phases_and_split_lists_keep_the_invariants(self):
        c = cook(SAMPLE, tmp=self.tmp)
        for name in c.blocks():
            v = c.decoded(c.blocks()[name]["guid"], "keen::VfxResource")
            for split in (False, True):
                w = World(entries_of(c, v), resource=patched(v), split=split)
                for i in range(60):
                    w.tick(phase=(i % 3))
                    w.check()

    def test_a_big_burst_arrives_over_several_ticks(self):
        c = cook(Mod(self.tmp / "m", "m", {"x": {"particles": [{"burst": 200, "lifetime": 5}]}}), tmp=self.tmp / "m")
        v = c.decoded(c.blocks()["x"]["guid"], "keen::VfxResource")
        w = World(entries_of(c, v), resource=patched(v), cap=4096)
        counts = []
        for _ in range(6):
            w.tick()
            w.check()
            counts.append(w.counter(w.active_counter(w.chunks[1])))
        self.assertEqual(counts[:4], [64, 128, 192, 200])


class Mistakes(Base):
    """Named errors, not bad bytes. A wrong block registers nothing; the rest of the file loads."""

    def expect(self, block, *needles):
        c = cook(Mod(self.tmp / "m", "m", {"bad": block, "fine": {"light": {}}}), tmp=self.tmp / "m", ok=(1,))
        text = "\n".join(c.report["errors"])
        for n in needles:
            self.assertIn(n, text)
        self.assertIn("vfx.bad", text)
        self.assertIn("fine", c.blocks())
        self.assertNotIn("bad", c.blocks())

    def test_max_count_over_the_limit(self):
        self.expect({"particles": [{"burst": 1, "maxCount": 4097}]}, "particles[0].maxCount", "4097", "4096")

    def test_max_count_at_the_limit_is_fine(self):
        c = cook(Mod(self.tmp / "ok", "ok", {"x": {"particles": [{"burst": 1, "maxCount": 4096}]}}), tmp=self.tmp / "ok")
        self.assertEqual(c.decoded(c.blocks()["x"]["guid"], "keen::VfxResource")["particles"][1]["maxCount"], 4096)

    def test_named_fields(self):
        cases = [
            ({"particles": [{"burst": -1}]}, "particles[0].burst"),
            ({"particles": [{"burst": 1, "lifetime": -1}]}, "particles[0].lifetime"),
            ({"particles": [{"burst": 1, "lifetime": 0}]}, "particles[0].lifetime"),
            ({"particles": [{"burst": 1, "lifetime": [2, 1]}]}, "min is above max"),
            ({"particles": [{"burst": 1, "typo": 1}]}, "particles[0].typo"),
            ({"typo": 1, "light": {}}, "vfx.bad.typo"),
            ({"particles": [{}]}, "spawns nothing"),
            ({"particles": [{"rate": 1, "loop": True}]}, "a loop needs a duration"),
            ({"particles": [{"burst": 1, "color": "red"}]}, "particles[0].color"),
            ({"particles": [{"burst": 1, "color": ["#fff", 2]}]}, "particles[0].color[1]"),
            ({"particles": [{"burst": 1, "alpha": 2}]}, "particles[0].alpha"),
            ({"particles": [{"burst": 1, "size": {"keys": [[0, 1], [0, 2]]}}]}, "t must grow"),
            ({"particles": [{"burst": 1, "spread": {"cone": 200}}]}, "spread.cone"),
            ({"particles": [{"burst": 1, "spread": "sideways"}]}, "particles[0].spread"),
            ({"particles": [{"burst": 1, "blend": "multiply"}]}, "particles[0].blend"),
            ({"particles": [{"burst": 1, "mesh": "teapot"}]}, "particles[0].mesh"),
            ({"particles": [{"burst": 1, "sampler": "repeat"}]}, "particles[0].sampler"),
            ({"particles": [{"burst": 1, "lifetime": 1e39}]}, "particles[0].lifetime"),
            ({"light": {"radius": 0}}, "light.radius"),
            ({"light": {"flicker": 2}}, "light.flicker"),
            ({"light": {"offset": [1, 2]}}, "light.offset"),
            ({}, "nothing to draw"),
            ({"particles": [{"burst": 1}] * 28}, "too many"),
            ("text", "expected an object"),
            ({"light": {}, "cullDistance": [9, 3]}, "cullDistance"),
        ]
        for i, (block, needle) in enumerate(cases):
            with self.subTest(needle):
                sub = self.tmp / f"case{i}"
                c = cook(Mod(sub, "m", {"bad": block, "fine": {"light": {}}}), tmp=sub, ok=(1,))
                self.assertIn(needle, "\n".join(c.report["errors"]))
                self.assertEqual(list(c.blocks()), ["fine"])

    def test_bad_names(self):
        c = cook(Mod(self.tmp / "m", "m", {"bad name": {"light": {}}}), tmp=self.tmp / "m", ok=(1,))
        self.assertIn("the name may hold", "\n".join(c.report["errors"]))

    def test_textures(self):
        cases = {
            "missing.png": ("can't read it", {}),
            "notpng.png": ("not a PNG", {"notpng.png": b"hello world"}),
            "odd.png": ("not a power of two", {"odd.png": png(6, 8)}),
            "tiny.png": ("outside", {"tiny.png": png(2, 2)}),
            "big.png": ("outside", {"big.png": png(4096, 4)}),
            "../up.png": ("inside the mod's folder", {}),
            "/etc/passwd": ("inside the mod's folder", {}),
            "C:/x.png": ("inside the mod's folder", {}),
        }
        for i, (rel, (needle, files)) in enumerate(cases.items()):
            with self.subTest(rel):
                sub = self.tmp / f"t{i}"
                c = cook(Mod(sub, "m", {"bad": {"particles": [{"burst": 1, "texture": rel}]}, "fine": {"light": {}}}, files=files), tmp=sub, ok=(1,))
                text = "\n".join(c.report["errors"])
                self.assertIn(needle, text)
                self.assertIn("particles[0].texture", text)
                self.assertEqual(list(c.blocks()), ["fine"])

    def test_a_broken_file_only_loses_itself(self):
        m = Mod(self.tmp / "m", "m", {"fine": {"light": {}}})
        (m.dir / "blocks" / "a.json").write_text("{ not json")
        c = cook(m, tmp=self.tmp, ok=(1,))
        self.assertIn("a.json: not valid JSON", "\n".join(c.report["errors"]))
        self.assertIn("fine", c.blocks())

    def test_the_format_is_checked(self):
        c = cook(Mod(self.tmp / "m", "m", None, raw_doc=json.dumps({"format": 2, "vfx": {"x": {"light": {}}}})), tmp=self.tmp, ok=(1,))
        self.assertIn("format 2", "\n".join(c.report["errors"]))

    def test_other_block_kinds_are_not_touched(self):
        c = cook(Mod(self.tmp / "m", "m", None, raw_doc=json.dumps({"format": 1, "spells": {"x": {}}})), tmp=self.tmp)
        self.assertEqual(c.report["blocks"], [])
        self.assertEqual(c.report["served"]["blobs"], [], "no VFX: nothing to serve, not even the modules")

    def test_comments_and_trailing_commas(self):
        doc = '''{ // a comment with a , before } in it
          "format": 1, /* and , ] here */
          "vfx": { "x": { "light": { "color": "#fff", "radius": 2, }, "comment": "a string with ,} in it", }, },
        }'''
        c = cook(Mod(self.tmp / "m", "m", None, raw_doc=doc), tmp=self.tmp)
        self.assertEqual(list(c.blocks()), ["x"])

    def test_two_blocks_with_one_id(self):
        c = cook(Mod(self.tmp / "m", "m", {"a": {"light": {}}, "b": {"was": "a", "light": {}}}), tmp=self.tmp, ok=(1,))
        self.assertIn("names the id of another block", "\n".join(c.report["errors"]))
        self.assertEqual(list(c.blocks()), ["a"])

    def test_a_sound_is_counted_but_says_it_is_not_played(self):
        c = cook(Mod(self.tmp / "m", "m", {"a": {"light": {}, "sound": "boom"}}), tmp=self.tmp)
        self.assertIn("not played yet", "\n".join(c.report["warnings"]))
        self.assertEqual(c.report["budget"]["sounds"]["mods"], 1)
        v = c.decoded(c.blocks()["a"]["guid"], "keen::VfxResource")
        self.assertEqual(v["sounds"], [])


class Manifest(Base):
    def test_clients_must_be_mod_for_a_mod_on_both_sides(self):
        for clients in ("kiln", "none"):
            sub = self.tmp / clients
            c = cook(Mod(sub, "m", {"a": {"light": {}}}, clients=clients), tmp=sub, ok=(1,))
            self.assertIn('needs "clients": "mod"', "\n".join(c.report["refusedMods"]))
            self.assertEqual(c.report["served"]["resources"], [])

    def test_a_client_only_cosmetic_may_say_none(self):
        c = cook(Mod(self.tmp / "m", "m", {"a": {"light": {}}}, side="client", clients="none"), tmp=self.tmp)
        self.assertEqual(list(c.blocks()), ["a"])

    def test_a_server_mod_is_skipped(self):
        c = cook(Mod(self.tmp / "m", "m", {"a": {"light": {}}}, side="server", clients="none"), tmp=self.tmp)
        self.assertEqual(list(c.blocks()), [])
        self.assertIn("clients never load this mod", "\n".join(c.report["warnings"]))


class BudgetLimits(Base):
    """Every limit that is fatal in the game: refused here, whole mod, with a line that says which."""

    def many(self, mod_id, n, sub="x", **each):
        return Mod(self.tmp / f"{sub}{mod_id}", mod_id, {f"v{i}": {"light": {}, **each} for i in range(n)})

    def test_room_is_what_the_game_leaves(self):
        b = json.loads(run("budget").stdout)
        self.assertEqual((b["vfx"]["room"], b["meshes"]["room"], b["sounds"]["room"], b["curves"]["room"]), (473, 386, 10, 8))
        self.assertEqual(b["meshBytes"]["capacity"], 0x1100000)
        self.assertEqual(b["meshBytes"]["room"], 0x1100000 - 17567564 - 16384)

    def test_473_new_vfx_fit_and_474_do_not(self):
        c = cook(self.many("full", 473), tmp=self.tmp / "a")
        self.assertEqual(len(c.report["blocks"]), 473)
        c = cook(self.many("over", 474), tmp=self.tmp / "b", ok=(1,))
        self.assertEqual(c.report["blocks"], [])
        self.assertEqual(c.report["served"]["resources"], [], "nothing of a refused mod is served")
        self.assertEqual(c.report["served"]["blobs"], [])
        self.assertIn("VFX: needs 474, 473 left", c.report["refusedMods"][0])
        self.assertIn("over", c.report["refusedMods"][0])

    def test_the_budget_is_shared_by_all_mods_in_load_order(self):
        a, b, d = self.many("a", 400), self.many("b", 100), self.many("d", 73)
        c = cook(a, b, d, tmp=self.tmp / "o", ok=(1,))
        by_mod = {}
        for x in c.report["blocks"]:
            by_mod[x["mod"]] = by_mod.get(x["mod"], 0) + 1
        self.assertEqual(by_mod, {"a": 400, "d": 73})
        self.assertEqual(len(c.report["refusedMods"]), 1)
        self.assertIn("b: over the game's limits", c.report["refusedMods"][0])

    def test_sounds(self):
        ok = Mod(self.tmp / "s1", "m", {f"v{i}": {"light": {}, "sound": f"s{i}"} for i in range(10)})
        self.assertEqual(len(cook(ok, tmp=self.tmp / "s1").report["blocks"]), 10)
        over = Mod(self.tmp / "s2", "m", {f"v{i}": {"light": {}, "sound": f"s{i}"} for i in range(11)})
        c = cook(over, tmp=self.tmp / "s2", ok=(1,))
        self.assertIn("sounds in VFX: needs 11, 10 left", c.report["refusedMods"][0])

    def test_a_shared_sound_counts_once(self):
        m = Mod(self.tmp / "s3", "m", {f"v{i}": {"light": {}, "sound": "same"} for i in range(20)})
        self.assertEqual(cook(m, tmp=self.tmp / "s3").report["budget"]["sounds"]["mods"], 1)

    def test_meshes_and_their_memory(self):
        m = Mod(self.tmp / "q", "m", {"a": {"particles": [{"burst": 1}]}})
        c = cook(m, extra=["--counts", "meshes=1024"], tmp=self.tmp / "q1", ok=(1,))
        self.assertIn("meshes: needs 1, 0 left", c.report["refusedMods"][0])
        c = cook(m, extra=["--counts", "meshBytes=17809400"], tmp=self.tmp / "q2", ok=(1,))
        self.assertIn("mesh memory (bytes): needs 140", c.report["refusedMods"][0])
        c = cook(m, extra=["--counts", "meshes=1023"], tmp=self.tmp / "q3")
        self.assertEqual(c.report["budget"]["meshes"]["mods"], 1)

    def test_kilns_quad_counts_once(self):
        a = Mod(self.tmp / "qa", "a", {"x": {"particles": [{"burst": 1}]}})
        b = Mod(self.tmp / "qb", "b", {"y": {"particles": [{"burst": 1}]}})
        c = cook(a, b, tmp=self.tmp / "q")
        self.assertEqual(c.report["budget"]["meshes"]["mods"], 1)
        self.assertEqual(c.report["budget"]["meshBytes"]["mods"], 140)
        self.assertEqual(len(c.resources_of("keen::VfxMeshResource")), 1)

    def test_a_game_update_moves_the_room(self):
        c = cook(self.many("u", 3), extra=["--counts", "vfx=2046"], tmp=self.tmp / "u", ok=(1,))
        self.assertIn("VFX: needs 3, 2 left", c.report["refusedMods"][0])

    def test_blocks_use_no_curve_slots(self):
        c = cook(SAMPLE, tmp=self.tmp)
        self.assertEqual(c.report["budget"]["curves"]["mods"], 0)


class Textures(Base):
    def one(self, file, block_texture=None, files=None):
        c = cook(Mod(self.tmp / "m", "m", {"x": {"particles": [{"burst": 1, "texture": block_texture or "t.png"}]}}, files=files or {"t.png": file}), tmp=self.tmp / "m")
        r = c.resources_of("keen::RenderTextureResource")[0]
        return c, DEC.decode(r["type"], (c.out / r["file"]).read_bytes())

    def test_a_chain_down_to_one_pixel(self):
        c, t = self.one(png(256, 64, (200, 100, 50, 128)))
        self.assertEqual((t["width"], t["height"], t["levelCount"]), (256, 64, 9))
        self.assertEqual(checks.check_texture(t, len(c.blob(t["content"]["hash"]))), [])
        self.assertTrue(t["mipChainContentRanges"])
        self.assertTrue(t["debugName"].startswith("kiln_m_t"))

    def test_small_textures_have_no_ranges(self):
        c, t = self.one(png(16, 16))
        self.assertEqual(t["mipChainContentRanges"], [])
        self.assertEqual(checks.check_texture(t, len(c.blob(t["content"]["hash"]))), [])

    def test_linear_textures(self):
        c, t = self.one(None, {"file": "t.png", "linear": True}, {"t.png": png(8, 8)})
        self.assertEqual(t["format"], "BC7_unorm_block")

    def test_a_texture_shared_by_two_blocks_is_one_resource(self):
        c = cook(Mod(self.tmp / "s", "s", {"a": {"particles": [{"burst": 1, "texture": "t.png"}]}, "b": {"particles": [{"burst": 1, "texture": "t.png"}]}},
                     files={"t.png": png(8, 8)}), tmp=self.tmp / "s")
        self.assertEqual(len(c.resources_of("keen::RenderTextureResource")), 1)

    def test_a_flipbook_that_cannot_divide_says_so(self):
        c = cook(Mod(self.tmp / "f", "f", {"a": {"particles": [{"burst": 1, "texture": "t.png", "flipbook": {"columns": 3, "rows": 2}}]},
                                           "b": {"particles": [{"burst": 1, "texture": "t.png", "flipbook": {"columns": 2, "rows": 2}}]}},
                     files={"t.png": png(8, 8)}), tmp=self.tmp / "f")
        warnings = "\n".join(c.report["warnings"])
        self.assertIn("vfx.a.particles[0].flipbook: 8x8 doesn't divide into 3 columns", warnings)
        self.assertNotIn("vfx.b", warnings)

    def test_the_cache_is_used(self):
        cache = self.tmp / "cache"
        m = Mod(self.tmp / "c", "c", {"a": {"particles": [{"burst": 1, "texture": "t.png"}]}}, files={"t.png": png(8, 8, (10, 20, 30, 40))})
        first = cook(m, extra=["--cache", cache], tmp=self.tmp / "c1")
        files = list(cache.glob("*.tex"))
        self.assertEqual(len(files), 1)
        before = files[0].read_bytes()
        again = cook(m, extra=["--cache", cache], tmp=self.tmp / "c2")
        r = first.resources_of("keen::RenderTextureResource")[0]
        self.assertEqual((first.out / r["file"]).read_bytes(), (again.out / r["file"]).read_bytes())
        self.assertEqual(files[0].read_bytes(), before)
        # A truncated cache entry is not trusted.
        files[0].write_bytes(before[:10])
        third = cook(m, extra=["--cache", cache], tmp=self.tmp / "c3")
        self.assertEqual((first.out / r["file"]).read_bytes(), (third.out / r["file"]).read_bytes())

    def test_the_pixels_decode_back(self):
        # A flat colour survives BC7 within a small error at every level.
        c, t = self.one(png(32, 32, (40, 160, 220, 255)))
        blob = c.blob(t["content"]["hash"])
        self.assertEqual(len(blob), sum(checks.mip_sizes(32, 32, t["levelCount"])))
        rgba = bc7_mode_decode_flat(blob[:16])
        if rgba:
            for got, want in zip(rgba, (40, 160, 220, 255)):
                self.assertLess(abs(got - want), 6)


def bc7_mode_decode_flat(block):
    """Colour of a BC7 block whose endpoints agree, for modes 5 and 6 (what a flat colour encodes to). None for any other mode."""
    bits = int.from_bytes(block, "little")
    if bits & 0x7f == 0x40:   # mode 6: 7+1 bit RGBA endpoints
        at = 7
        ch = []
        for _ in range(8):
            ch.append((bits >> at) & 0x7f)
            at += 7
        p0, p1 = (bits >> at) & 1, (bits >> (at + 1)) & 1
        return tuple(((ch[i * 2] << 1) | p0) for i in range(4))
    return None


class GameSpriteModule(Base):
    """The diagnostic: a block on the game's own sprite module. A pipeline whose blob the game lacks is fatal, so it is refused unless the game has it."""

    BLOCK = {"gameSpriteModule": True, "particles": [{"burst": 4, "size": 0.5, "color": "#302010", "texture": "t.png"}]}

    def fake_kfc(self, listed):
        b = bytearray(0x90)
        b[:4] = b"KFC4"
        keys = len(b)
        for h in listed:
            b += struct.pack("<4I", *h)
        struct.pack_into("<II", b, 0x48, keys - 0x48, len(listed))
        f = self.tmp / "fake.kfc"
        f.write_bytes(b)
        return f

    def test_refused_without_a_game_to_ask(self):
        c = cook(Mod(self.tmp / "m", "m", {"x": self.BLOCK, "fine": {"light": {}}}, files={"t.png": png(8, 8)}), tmp=self.tmp / "o", ok=(1,))
        self.assertIn("vfx.x.gameSpriteModule", "\n".join(c.report["errors"]))
        self.assertEqual(list(c.blocks()), ["fine"])

    def test_refused_when_this_game_lacks_it(self):
        kfc = self.fake_kfc([(1, 2, 3, 4)])
        c = cook(Mod(self.tmp / "m", "m", {"x": self.BLOCK}, files={"t.png": png(8, 8)}), extra=["--kfc", kfc], tmp=self.tmp / "o", ok=(1,))
        self.assertIn("is not in this game", "\n".join(c.report["errors"]))
        self.assertEqual(c.report["served"]["resources"], [])

    def test_built_when_the_game_has_it(self):
        h = (25472, 2458470088, 2128813632, 3226514388)
        kfc = self.fake_kfc([(1, 2, 3, 4), h])
        c = cook(Mod(self.tmp / "m", "m", {"x": self.BLOCK}, files={"t.png": png(8, 8)}), extra=["--kfc", kfc], tmp=self.tmp / "o")
        v = c.decoded(c.blocks()["x"]["guid"], "keen::VfxResource")
        self.assertEqual(checks.check_vfx(v, strict=True), [])
        kid = v["particles"][1]
        self.assertEqual(tuple(kid["strippedCode"]["hash"][k] for k in ("size", "hash0", "hash1", "hash2")), h)
        self.assertEqual(kid["stateSizeInBytes"], 64)
        self.assertEqual(kid["evaluateParametersGpuResourceDataRange"]["count"], 48)
        self.assertEqual(v["images"][0]["patchPoint"]["resourceDataRange"], {"start": P["KE_BLOCK_SIZE"] + 16, "count": 8})
        self.assertEqual(v["meshes"][0]["patchPoint"]["resourceDataRange"], {"start": P["KE_BLOCK_SIZE"] + 28, "count": 4})
        d = checks.resource_data(v)
        self.assertEqual(struct.unpack_from("<I", d, P["KE_BLOCK_SIZE"] + 36)[0], 0xff102030)
        self.assertEqual(struct.unpack_from("<f", d, P["KE_BLOCK_SIZE"] + 44)[0], 0.5)
        self.assertNotIn("6380-" + "9281d4c8", "".join(b["hash"] for b in c.report["served"]["blobs"]), "the game's blob is not served by Kiln")
        self.assertEqual(len(c.report["served"]["blobs"]), 5, "3 starter modules, the quad, the texture")
        self.assertIn("a diagnostic", "\n".join(c.report["warnings"]))


class Pending(Base):
    def test_definitions_from_code_load_beside_the_mods_own(self):
        m = Mod(self.tmp / "m", "m", {"a": {"light": {}}})
        pending = self.tmp / "pending" / "m"
        pending.mkdir(parents=True)
        (pending / "b.json").write_text(json.dumps({"format": 1, "vfx": {"from_code": {"light": {"radius": 2}}}}))
        c = cook(m, extra=["--pending", self.tmp / "pending"], tmp=self.tmp)
        self.assertEqual(set(c.blocks()), {"a", "from_code"})

    def test_the_mods_own_block_wins(self):
        m = Mod(self.tmp / "m", "m", {"a": {"light": {"radius": 1}}})
        pending = self.tmp / "pending" / "m"
        pending.mkdir(parents=True)
        (pending / "b.json").write_text(json.dumps({"format": 1, "vfx": {"a": {"light": {"radius": 2}}}}))
        c = cook(m, extra=["--pending", self.tmp / "pending"], tmp=self.tmp)
        d = checks.resource_data(c.decoded(c.blocks()["a"]["guid"], "keen::VfxResource"))
        self.assertEqual(struct.unpack_from("<f", d, P["KL_RADIUS"])[0], 1.0)


class Fingerprint(Base):
    """On a synthetic PE: the reader, the compare, a changed layout."""

    def make_pe(self, layouts):
        # headers, one .rdata section with names then records
        names = bytearray()
        recs = bytearray()
        base = 0x140000000
        va = 0x1000
        for name, layout in layouts:
            names += name.encode() + b"\0"
        names_size = (len(names) + 15) & ~15
        cur = 0
        for name, layout in layouts:
            rec = bytearray(0x70)
            struct.pack_into("<QQ", rec, 0x20, base + va + cur, len(name))
            from kfc.util import fnv1a32
            struct.pack_into("<II", rec, 0x50, fnv1a32(name), layout)
            recs += rec
            cur += len(name) + 1
        section = bytes(names).ljust(names_size, b"\0") + bytes(recs)
        pe = 0x80
        head = bytearray(0x400)
        head[0:2] = b"MZ"
        struct.pack_into("<I", head, 0x3c, pe)
        head[pe:pe + 4] = b"PE\0\0"
        struct.pack_into("<HH", head, pe + 4, 0x8664, 1)
        struct.pack_into("<H", head, pe + 0x14, 0xf0)
        struct.pack_into("<Q", head, pe + 0x18 + 0x18, base)
        struct.pack_into("<I", head, pe + 0x18 + 0x38, 0x1000 + len(section) + 0x1000)
        struct.pack_into("<I", head, pe + 0x18 + 0x3c, 0x400)
        s = pe + 0x18 + 0xf0
        head[s:s + 7] = b".rdata\0"
        struct.pack_into("<II", head, s + 8, len(section), va)
        struct.pack_into("<II", head, s + 16, len(section), 0x400)
        return bytes(head) + section

    def _pe(self, layouts):
        p = self.tmp / "pe.exe"
        p.write_bytes(self.make_pe(list(layouts)))
        return p

    def names(self):
        r = json.loads(run("fingerprint", "--exe", self._pe([("keen::Nothing", 1)]), ok=(1,)).stdout)
        return [(x["name"], int(x["expected"], 16)) for x in r["layouts"] + r["materialLayouts"]]

    def test_all_found_and_matching(self):
        names = self.names()
        r = json.loads(run("fingerprint", "--exe", self._pe(names)).stdout)
        self.assertTrue(r["layoutsOk"])

    def test_a_changed_layout_says_which(self):
        names = self.names()
        changed = [(n, (l ^ 1) if n == "keen::VfxUpdateBuffer" else l) for n, l in names]
        p = run("fingerprint", "--exe", self._pe(changed), ok=(1,))
        r = json.loads(p.stdout)
        self.assertFalse(r["layoutsOk"])
        self.assertIn("keen::VfxUpdateBuffer", r["difference"])
        self.assertNotIn("VfxResource", r["difference"])

    def test_a_missing_record_says_which(self):
        names = [x for x in self.names() if x[0] != "keen::PointLightData"]
        r = json.loads(run("fingerprint", "--exe", self._pe(names), ok=(1,)).stdout)
        self.assertIn("keen::PointLightData not found", r["difference"])

    def test_the_binding_hash_reads_a_gpc(self):
        # A GpcResource: descriptorSetLayouts at +0x28 -> one layout with its name and two bindings.
        b = bytearray(136)
        layouts = len(b)
        b += bytes(28)
        struct.pack_into("<iI", b, 0x28, layouts - 0x28, 1)
        name = b"VfxUpdateDescriptorSetLayout"
        struct.pack_into("<iI", b, layouts, len(b) - layouts, len(name))
        b += name
        at = (len(b) + 3) & ~3
        b += bytes(at - len(b))
        recs = len(b)
        b += bytes(48)
        struct.pack_into("<iI", b, layouts + 0x0c, recs - (layouts + 0x0c), 2)
        for i, (n, ty, st) in enumerate(((b"g_update", 7, 1392), (b"g_list", 11, 176))):
            r = recs + i * 24
            struct.pack_into("<iI", b, r, len(b) - r, len(n))
            b += n
            b[r + 8] = ty
            struct.pack_into("<I", b, r + 0x10, st)
        f = self.tmp / "g.bin"
        f.write_bytes(b)
        out = json.loads(run("fingerprint", "--gpc", f, ok=(1,)).stdout)["bindings"]
        self.assertTrue(out["found"])
        self.assertEqual(out["count"], 2)
        h = 0x811c9dc5
        for chunk in (b"g_update\0", bytes([7]), struct.pack("<I", 1392), b"g_list\0", bytes([11]), struct.pack("<I", 176)):
            for c in chunk:
                h = ((h ^ c) * 0x01000193) & 0xffffffff
        self.assertEqual(out["hash"], f"{h:08x}")
        self.assertFalse(out["ok"], "not the game's list")
        b2 = bytes(b[:0x28]) + bytes(8) + bytes(b[0x30:])
        f.write_bytes(b2)
        self.assertFalse(json.loads(run("fingerprint", "--gpc", f, ok=(1,)).stdout)["bindings"]["found"])

    def test_a_cut_gpc_is_refused_not_read_past_its_end(self):
        f = self.tmp / "cut.bin"
        b = bytearray(0x30)
        struct.pack_into("<iI", b, 0x28, 0x7fffff00, 1000)
        f.write_bytes(b)
        self.assertFalse(json.loads(run("fingerprint", "--gpc", f, ok=(1,)).stdout)["bindings"]["found"])

    def test_a_bad_exe_is_refused(self):
        f = self.tmp / "no.exe"
        f.write_bytes(b"not a pe")
        self.assertEqual(run("fingerprint", "--exe", f, ok=(2,)).returncode, 2)


def validate(node, schema, root, path="$"):
    """The part of JSON Schema schema/kiln-vfx.schema.json uses. Returns problems."""
    bad = []
    if "$ref" in schema:
        r = schema["$ref"]
        target = root
        for part in r.lstrip("#/").split("/"):
            target = target[part]
        bad += validate(node, target, root, path)
    t = schema.get("type")
    if t:
        ok = {"object": isinstance(node, dict), "array": isinstance(node, list), "string": isinstance(node, str), "boolean": isinstance(node, bool),
              "integer": isinstance(node, int) and not isinstance(node, bool), "number": isinstance(node, (int, float)) and not isinstance(node, bool)}[t]
        if not ok:
            return bad + [f"{path}: not {t}"]
    if "enum" in schema and node not in schema["enum"]:
        bad.append(f"{path}: {node!r} not in {schema['enum']}")
    if isinstance(node, (int, float)) and not isinstance(node, bool):
        if "minimum" in schema and node < schema["minimum"]:
            bad.append(f"{path}: below {schema['minimum']}")
        if "maximum" in schema and node > schema["maximum"]:
            bad.append(f"{path}: above {schema['maximum']}")
        if "exclusiveMinimum" in schema and node <= schema["exclusiveMinimum"]:
            bad.append(f"{path}: not above {schema['exclusiveMinimum']}")
    if isinstance(node, str):
        import re
        if "pattern" in schema and not re.search(schema["pattern"], node):
            bad.append(f"{path}: {node!r} doesn't match")
        if "minLength" in schema and len(node) < schema["minLength"]:
            bad.append(f"{path}: too short")
    if isinstance(node, list):
        if "minItems" in schema and len(node) < schema["minItems"]:
            bad.append(f"{path}: too few")
        if "maxItems" in schema and len(node) > schema["maxItems"]:
            bad.append(f"{path}: too many")
        if "items" in schema:
            for i, x in enumerate(node):
                bad += validate(x, schema["items"], root, f"{path}[{i}]")
    if isinstance(node, dict):
        props = schema.get("properties", {})
        for k in schema.get("required", []):
            if k not in node:
                bad.append(f"{path}: needs {k}")
        for k, v in node.items():
            if "propertyNames" in schema:
                bad += validate(k, schema["propertyNames"], root, f"{path}.{k}(name)")
            if k in props:
                bad += validate(v, props[k], root, f"{path}.{k}")
            elif "additionalProperties" in schema:
                if schema["additionalProperties"] is False:
                    bad.append(f"{path}.{k}: unknown")
                else:
                    bad += validate(v, schema["additionalProperties"], root, f"{path}.{k}")
    for key, need in (("oneOf", 1), ("anyOf", None)):
        if key in schema:
            hits = sum(1 for s in schema[key] if not validate(node, s, root, path))
            if (need is not None and hits != need) or (need is None and hits < 1):
                bad.append(f"{path}: fits {hits} of {key}")
    return bad


class Schema(Base):
    """schema/kiln-vfx.schema.json and the builder have to agree: same fields, same limits."""

    @classmethod
    def setUpClass(cls):
        cls.schema = json.loads((REPO / "schema" / "kiln-vfx.schema.json").read_text())

    def check(self, section):
        return validate(section, self.schema, self.schema)

    def test_the_sample_fits(self):
        text = (SAMPLE / "blocks" / "frost.json").read_text()
        doc = json.loads(run_relaxed(text))
        self.assertEqual(self.check(doc["vfx"]), [])

    def test_the_same_fields_as_the_builder(self):
        def known(path_block, field):
            c = cook(Mod(self.tmp / field, "m", {"bad": path_block}), tmp=self.tmp / field, ok=(1,))
            line = next(e for e in c.report["errors"] if "unknown field; there are " in e)
            return set(line.split("unknown field; there are ")[1].split(", "))
        d = self.schema["$defs"]
        self.assertEqual(known({"particles": [{"burst": 1, "zzz": 1}]}, "p") | {"comment"}, set(d["particle"]["properties"]))
        self.assertEqual(known({"light": {"zzz": 1}}, "l") | {"comment"}, set(d["light"]["properties"]))
        self.assertEqual(known({"zzz": 1, "light": {}}, "b") | {"comment"}, set(d["block"]["properties"]))

    def test_max_count_stops_at_the_limit_in_both(self):
        self.assertEqual(self.schema["$defs"]["particle"]["properties"]["maxCount"]["maximum"], P["KILN_VFX_MAX_PARTICLES"])
        self.assertEqual(self.check({"x": {"particles": [{"burst": 1, "maxCount": 4096}]}}), [])
        self.assertTrue(self.check({"x": {"particles": [{"burst": 1, "maxCount": 4097}]}}))

    def test_mistakes_the_schema_catches_too(self):
        for block in ({"particles": [{"burst": 1, "typo": 1}]}, {"typo": 1, "light": {}}, {}, {"particles": [{}]}, {"particles": [{"burst": 1, "lifetime": 0}]},
                      {"particles": [{"burst": 1, "spread": "sideways"}]}, {"particles": [{"burst": 1, "color": "red"}]}, {"particles": [{"burst": 1, "alpha": 2}]},
                      {"particles": [{"burst": 1, "texture": "a.tga"}]}, {"particles": [{"burst": 1, "blend": "multiply"}]}, {"particles": [{"burst": 1, "mesh": "teapot"}]},
                      {"light": {"radius": 0}}, {"light": {"flicker": 2}}, {"light": {"offset": [1, 2]}}, {"particles": [{"burst": 1}] * 28}):
            with self.subTest(block):
                self.assertTrue(self.check({"x": block}))
        self.assertTrue(self.check({"bad name": {"light": {}}}))

    def test_what_the_schema_takes_the_builder_takes(self):
        block = {"particles": [{"burst": 1, "rate": 2, "duration": 1, "delay": 0.5, "loop": True, "maxCount": 9, "lifetime": [0.5, 1], "speed": -1, "spread": {"cone": 10},
                                "spawnRadius": 1, "gravity": -9, "acceleration": [1, 2, 3], "drag": 1, "spin": [-1, 1], "roll": 2, "size": [1, 2, 3], "alpha": {"linear": [1, 0]},
                                "color": {"keys": [[0, "#fff"], [1, "000000"]]}, "intensity": 2, "texture": {"file": "t.png", "linear": True}, "sampler": "mirror",
                                "flipbook": {"columns": 2, "rows": 2, "fps": 1}, "blend": "alpha", "lit": False, "mesh": "quad-xz", "faceCamera": True, "depthFade": 1, "emissive": 1,
                                "comment": "x"}],
                 "light": {"color": "#fff", "radius": 1, "brightness": 1, "lifetime": 1, "fadeIn": 1, "fadeOut": 1, "flicker": 1, "offset": [0, 0, 0], "comment": 1},
                 "sound": "s", "cullDistance": 9, "was": "old", "comment": "y"}
        self.assertEqual(self.check({"x": block}), [])
        c = cook(Mod(self.tmp / "m", "m", {"x": block}, files={"t.png": png(8, 8)}), tmp=self.tmp / "m")
        self.assertEqual(list(c.blocks()), ["x"])


def run_relaxed(text):
    """Comments and trailing commas out, for Python's json, on the sample only."""
    import re
    text = re.sub(r"//[^\n]*", "", text)
    return re.sub(r",(\s*[}\]])", r"\1", text)


class Layouts(unittest.TestCase):
    def test_the_hashes_are_the_names(self):
        from kfc.util import fnv1a32
        for name, h in TYPES.items():
            self.assertEqual(fnv1a32(name), h)
            self.assertEqual(T.get(name)["hash"], h)


@unittest.skipUnless(HAS_GAME, "the installed client is not here")
class WithTheGame(Base):
    """Read-only against the installed client. Nothing from it goes into the repo."""

    @classmethod
    def setUpClass(cls):
        os.environ.setdefault("KILN_OUT", str(Path(tempfile.gettempdir()) / "kiln-vfxcook-test"))
        from kfc.archive import ContentHash, Kfc, ResourceKey
        from kfc.refl import TypeDb
        cls.ContentHash, cls.ResourceKey = ContentHash, ResourceKey
        cls.kfc = Kfc(GAME)
        cls.types = TypeDb.from_exe(GAME / "enshrouded.exe")
        cls.dec = Decoder(cls.types, max_array=1 << 26)
        cls.enc = Encoder(cls.types)
        cls.th = {n: cls.types.get(n)["hash"] for n in TYPES}

    def blob(self, h):
        raw = struct.pack("<4I", h["size"], h["hash0"], h["hash1"], h["hash2"]).hex()
        return self.kfc.read_content(self.ContentHash(raw))

    def test_the_rules_hold_for_every_game_vfx(self):
        th = self.th["keen::VfxResource"]
        keys = self.kfc.resources_of_type(th)
        self.assertEqual(len(keys), 1575)
        for k in keys:
            v = self.dec.decode(th, self.kfc.read(k))
            self.assertEqual(checks.check_vfx(v), [], k.guid)

    def test_the_rules_hold_for_every_game_mesh(self):
        th = self.th["keen::VfxMeshResource"]
        keys = self.kfc.resources_of_type(th)
        self.assertEqual(len(keys), 638)
        total = 0
        for k in keys:
            m = self.dec.decode(th, self.kfc.read(k))
            blob = self.blob(m["meshData"]["hash"])
            total += len(blob)
            self.assertEqual(checks.check_mesh(m, blob), [], k.guid)
        self.assertEqual(json.loads(run("budget").stdout)["meshBytes"]["game"], total, "the mesh memory the budget assumes")

    def test_the_rules_hold_for_every_texture_a_game_vfx_names(self):
        th, tt = self.th["keen::VfxResource"], self.th["keen::RenderTextureResource"]
        images = set()
        sounds = set()
        for k in self.kfc.resources_of_type(th):
            v = self.dec.decode(th, self.kfc.read(k))
            images.update(p["image"] for p in v["images"])
            sounds.update(p["soundContainer"] for p in v["sounds"])
        self.assertEqual(len(images), 145)
        for g in images:
            r = self.dec.decode(tt, self.kfc.read(self.ResourceKey(g, tt, 0)))
            self.assertEqual(checks.check_texture(r), [], g)
        self.assertEqual(json.loads(run("budget").stdout)["sounds"]["game"], len(sounds), "the sound slots the budget assumes")

    def test_the_rules_hold_for_a_sample_of_game_modules(self):
        th = self.th["keen::VfxResource"]
        seen = {}
        for k in self.kfc.resources_of_type(th)[:80]:
            v = self.dec.decode(th, self.kfc.read(k))
            for p in v["particles"]:
                h = p["strippedCode"]["hash"]
                seen.setdefault(h["size"], h)
        for h in list(seen.values())[:60]:
            code = self.blob(h)
            self.assertEqual(checks.check_module(code), [], h)
            self.assertEqual(content_hash(code), (h["size"], h["hash0"], h["hash1"], h["hash2"]))

    def test_built_output_decodes_with_the_games_reflection(self):
        c = cook(SAMPLE, tmp=self.tmp)
        for r in c.report["served"]["resources"]:
            raw = (c.out / r["file"]).read_bytes()
            v = self.dec.decode(r["type"], raw)
            self.assertEqual(self.enc.encode(r["type"], v), raw, r["what"])
            if r["type"] == "keen::VfxResource":
                self.assertEqual(checks.check_vfx(v, strict=True), [], r["what"])

    def test_the_game_sprite_diagnostic_runs_on_the_games_module(self):
        c = cook(Mod(self.tmp / "m", "m", {"x": {"gameSpriteModule": True, "particles": [{"burst": 4, "size": 0.5, "color": "#302010", "texture": "t.png", "blend": "additive"}]}},
                     files={"t.png": png(8, 8)}), extra=["--kfc", GAME / "enshrouded.kfc"], tmp=self.tmp / "o")
        v = c.decoded(c.blocks()["x"]["guid"], "keen::VfxResource")
        entries = []
        for p in v["particles"]:
            h = p["strippedCode"]["hash"]
            try:
                code = c.blob(h)
            except FileNotFoundError:
                code = self.blob(h)
            self.assertEqual(checks.check_module(code), [])
            e = Entry(SIMS.setdefault(code, Sim(code)), depth=p["depth"], parent=p["parent"], child=p["child"], max_count=p["maxCount"], state_size=p["stateSizeInBytes"])
            e.res_start = p["evaluateParametersGpuResourceDataRange"]["start"]
            entries.append(e)
        w = World(entries, resource=patched(v))
        for _ in range(5):
            w.tick()
            w.check()
        draws = w.draws()
        self.assertEqual(len(draws), 4, "Kiln's emitter spawns into the game's sprite")
        self.assertEqual((draws[0]["image"], draws[0]["imageSampler"], draws[0]["meshIndex"]), (7, 2, 1))

    def test_the_quad_is_the_games_own_numbers(self):
        c = cook(SAMPLE, tmp=self.tmp)
        ours = c.decoded(c.resources_of("keen::VfxMeshResource")[0]["guid"], "keen::VfxMeshResource")
        th = self.th["keen::VfxMeshResource"]
        theirs = self.dec.decode(th, self.kfc.read(self.ResourceKey("e55ea493-8110-4c44-897b-343eb1456076", th, 0)))
        self.assertEqual(ours["meshData"], theirs["meshData"], "same bytes, so the same content hash")
        self.assertEqual(ours["boundingSphere"], theirs["boundingSphere"])
        self.assertEqual((ours["vertexCount"], ours["indexCount"]), (theirs["vertexCount"], theirs["indexCount"]))

    def test_the_handwritten_layouts_match_reflection(self):
        real = self.types
        for t in T.dump["types"]:
            r = real.get(t["name"])
            if r is None:
                continue
            r = real.resolve(r)
            self.assertEqual(t["size"], r["size"], t["name"])
            if t["kind"] == 18 and t.get("fields"):   # a struct: its alignment, and every field at its offset with its type
                self.assertEqual(t["align"], r["align"], t["name"])
                want = {f["name"]: (f["offset"], f["type"]) for f in real.all_fields(r)}
                self.assertEqual({f["name"]: (f["offset"], f["type"]) for f in t["fields"]}, want, t["name"])

    def test_the_fingerprint_matches_this_game(self):
        th = self.types.get("keen::GpcResource")["hash"]
        k = self.ResourceKey("ad606c3f-f737-51b2-9617-e0c5aba82b11", th, 0)
        gpc = self.tmp / "vfx.gpc"
        gpc.write_bytes(self.kfc.read(k))
        r = json.loads(run("fingerprint", "--exe", GAME / "enshrouded.exe", "--gpc", gpc, "--kfc", GAME / "enshrouded.kfc").stdout)
        self.assertTrue(r["layoutsOk"])
        self.assertTrue(r["bindings"]["ok"])
        self.assertEqual(r["bindings"]["count"], 83)
        self.assertEqual(r["kfc"], {"vfx": 1575, "meshes": 638, "curves": 8})
        for x in r["layouts"]:
            self.assertEqual(int(x["expected"], 16), self.types.get(x["name"])["layout"], x["name"])

    def test_a_vanilla_vfx_rebuilt_from_its_decode_is_identical(self):
        th = self.th["keen::VfxResource"]
        for k in self.kfc.resources_of_type(th)[:50]:
            raw = self.kfc.read(k)
            self.assertEqual(self.enc.encode(th, self.dec.decode(th, raw)), raw, k.guid)


if __name__ == "__main__":
    unittest.main()
