# SPDX-License-Identifier: MIT
"""Particle ABI 2 in the builder: spawn shapes, velocity stretch, per-play parameters, and a mod's own particle module (ShaderBlock).

    python3 -m unittest discover -s tools/vfxcook/tests -p 'test_vfx_abi2*'

Bytes are read back with the hand-written layouts and the parameter layout of kiln_vfx_params.h. What these do on a GPU is in
native/services/vfx/shaders/test (test_starter.py Shapes/Stretch/PlayParameters, test_mod_module.py).
"""
import json
import shutil
import struct
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from test_vfxcook import DEC, P, Mod, checks, cook, png, run, sprite  # noqa: E402

REPO = HERE.parents[2]
SAMPLE = REPO / "mods/samples/ShaderSample"


def block_words(c, name):
    v = c.decoded(c.blocks()[name]["guid"], "keen::VfxResource")
    data = checks.resource_data(v)
    child = v["particles"][1]
    start = child["evaluateParametersGpuResourceDataRange"]["start"]
    return v, data[start:start + child["evaluateParametersGpuResourceDataRange"]["count"]]


def u32(b, k):
    return struct.unpack_from("<I", b, P[k])[0]


def f32(b, k):
    return struct.unpack_from("<f", b, P[k])[0]


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="vfx-abi2-")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)


class Shapes(Base):
    def test_each_shape_writes_its_words(self):
        cases = {
            "sphere": ({"sphere": 2, "inner": 1}, P["KS_SHAPE_SPHERE"], (2, 1, 0)),
            "shell": ({"shell": 3}, P["KS_SHAPE_SPHERE"], (3, 3, 0)),
            "hemi": ({"hemisphere": 2}, P["KS_SHAPE_HEMISPHERE"], (2, 0, 0)),
            "box": ({"box": [2, 4, 6]}, P["KS_SHAPE_BOX"], (1, 2, 3)),
            "disc": ({"disc": 2}, P["KS_SHAPE_DISC"], (2, 0, 0)),
            "ring": ({"ring": 2}, P["KS_SHAPE_DISC"], (2, 2, 0)),
            "line": ({"line": 5}, P["KS_SHAPE_LINE"], (5, 0, 0)),
            "point": ("point", P["KS_SHAPE_POINT"], (0, 0, 0)),
        }
        m = Mod(self.tmp, "shapes", {k: {"particles": [sprite(shape=s, spread="outward")]} for k, (s, _, _) in cases.items()})
        c = cook(m, tmp=self.tmp)
        for name, (_, kind, size) in cases.items():
            _, b = block_words(c, name)
            self.assertEqual(u32(b, "KS_SHAPE"), kind, name)
            self.assertEqual((f32(b, "KS_SHAPE_A"), f32(b, "KS_SHAPE_B"), f32(b, "KS_SHAPE_C")), size, name)
            self.assertEqual(u32(b, "KS_SPREAD_KIND"), P["KS_SPREAD_OUTWARD"], name)
            self.assertEqual(u32(b, "KS_VERSION"), P["KILN_VFX_ABI"])

    def test_spawn_radius_is_a_sphere(self):
        c = cook(Mod(self.tmp, "r", {"a": {"particles": [sprite(spawnRadius=1.5)]}}), tmp=self.tmp)
        _, b = block_words(c, "a")
        self.assertEqual((u32(b, "KS_SHAPE"), f32(b, "KS_SHAPE_A"), f32(b, "KS_SPAWN_RADIUS")), (P["KS_SHAPE_SPHERE"], 1.5, 1.5))

    def test_mistakes_by_field(self):
        bad = {
            "both": sprite(spawnRadius=1, shape={"sphere": 1}),
            "two": sprite(shape={"sphere": 1, "box": [1, 1, 1]}),
            "inner": sprite(shape={"disc": 1, "inner": 2}),
            "unknown": sprite(shape={"cube": 1}),
            "stretch": sprite(stretch=1),
            "flat": sprite(align="velocity", mesh="quad-xz"),
        }
        c = cook(Mod(self.tmp, "bad", {k: {"particles": [v]} for k, v in bad.items()}), tmp=self.tmp, ok=(1,))
        text = "\n".join(c.report["errors"])
        for name, field in (("both", "shape"), ("two", "shape"), ("inner", "shape.inner"), ("unknown", "shape"), ("stretch", "stretch"), ("flat", "align")):
            self.assertIn(f"vfx.{name}.particles[0].{field}", text)
        self.assertEqual(c.blocks(), {})


class Stretch(Base):
    def test_flag_and_length(self):
        c = cook(Mod(self.tmp, "s", {"a": {"particles": [sprite(align="velocity", stretch=0.2)]}}), tmp=self.tmp)
        _, b = block_words(c, "a")
        self.assertEqual(u32(b, "KS_FLAGS"), P["KS_FLAG_FACE_CAMERA"] | P["KS_FLAG_ALIGN_VELOCITY"])
        self.assertAlmostEqual(f32(b, "KS_STRETCH"), 0.2, 6)


class Parameters(Base):
    def test_every_vfx_describes_the_same_parameters(self):
        c = cook(Mod(self.tmp, "p", {"a": {"particles": [sprite()]}, "b": {"light": {"color": "#ff0000"}}}), tmp=self.tmp)
        for name in ("a", "b"):
            v = c.decoded(c.blocks()[name]["guid"], "keen::VfxResource")
            self.assertEqual(checks._check_kiln(v), [])
            names = [p["dataRange"]["start"] for p in v["parameters"]]
            self.assertEqual(names, [P["KX_COLOR"], P["KX_SIZE"], P["KX_SPEED"], P["KX_BRIGHTNESS"], P["KX_COUNT"]])
            self.assertEqual(v["defaultParameterData"]["data"][15], 0x4E4C494B)


class ModModule(Base):
    def module(self):
        return (SAMPLE / "shaders/build/swirl.spv").read_bytes()

    def mod(self, advanced=True, shader="shaders/build/swirl.spv", files=None, name="m"):
        m = Mod(self.tmp, name, {"a": {"particles": [sprite(shader={"file": shader, "constants": [1, 2, 3]})]}, "plain": {"particles": [sprite()]}},
                files=files if files is not None else {"shaders/build/swirl.spv": self.module()})
        j = json.loads((m.dir / "kiln.mod.json").read_text())
        j["advanced"] = advanced
        (m.dir / "kiln.mod.json").write_text(json.dumps(j))
        return m

    def entry_code(self, c, name):
        v = c.decoded(c.blocks()[name]["guid"], "keen::VfxResource")
        h = v["particles"][1]["strippedCode"]["hash"]
        return (h["size"], h["hash0"], h["hash1"], h["hash2"])

    def test_served_and_named_by_its_entry(self):
        c = cook(self.mod(), tmp=self.tmp)
        self.assertEqual(c.report["modModules"], 1)
        code = self.entry_code(c, "a")
        self.assertNotEqual(code, self.entry_code(c, "plain"))
        self.assertEqual(code[0], len(self.module()))
        self.assertEqual(c.blob(dict(zip(("size", "hash0", "hash1", "hash2"), code))), self.module())
        _, b = block_words(c, "a")
        self.assertEqual([struct.unpack_from("<f", b, P["KS_USER"] + 4 * i)[0] for i in range(4)], [1, 2, 3, 0])

    def falls_back(self, c, why):
        self.assertEqual(c.report["modModules"], 0)
        self.assertEqual(self.entry_code(c, "a"), self.entry_code(c, "plain"))
        self.assertTrue(any(why in w and "Kiln's own sprite draws this particle instead" in w for w in c.report["warnings"]), c.report["warnings"])

    def test_not_advanced_falls_back(self):
        self.falls_back(cook(self.mod(advanced=False), tmp=self.tmp), "\"advanced\": true")

    def test_missing_falls_back(self):
        self.falls_back(cook(self.mod(files={}), tmp=self.tmp), "can't read it")

    def test_unstamped_falls_back(self):
        self.falls_back(cook(self.mod(files={"shaders/build/swirl.spv": (SAMPLE / "shaders/build/broken.spv").read_bytes()}), tmp=self.tmp), "no Kiln ABI stamp")

    def test_other_abi_falls_back(self):
        code = self.module().replace(b"kiln-vfx-abi-%d" % P["KILN_VFX_ABI"], b"kiln-vfx-abi-9")
        self.falls_back(cook(self.mod(files={"shaders/build/swirl.spv": code}), tmp=self.tmp), "built for particle ABI 9")

    def test_garbage_falls_back(self):
        self.falls_back(cook(self.mod(files={"shaders/build/swirl.spv": b"\x03\x02\x23\x07" + b"\0" * 64}), tmp=self.tmp), "Kiln's own sprite")

    def test_outside_the_folder_falls_back(self):
        self.falls_back(cook(self.mod(shader="../x/swirl.spv"), tmp=self.tmp), "inside the mod's folder")

    def test_check_command(self):
        p = run("shader", "check", SAMPLE / "shaders/build/swirl.spv", SAMPLE / "shaders/build/broken.spv", ok=(1,))
        r = json.loads(p.stdout)
        self.assertEqual([x["ok"] for x in r], [True, False])
        self.assertEqual(r[0]["abi"], str(P["KILN_VFX_ABI"]))
        allowed = {(0, 2), (1, 0), (1, 1), (1, 2), (1, 3), (1, 4), (1, 9), (1, 10), (1, 13), (1, 17), (1, 39), (1, 40), (1, 41), (1, 42)}
        self.assertTrue({tuple(b) for b in r[0]["bindings"]} <= allowed)

    @unittest.skipUnless(shutil.which("glslc"), "glslc not installed")
    def test_build_command_stamps_and_refuses_an_extra_binding(self):
        mod = Path(self.tmp) / "src"
        (mod / "shaders").mkdir(parents=True)
        (mod / "shaders" / "good.hlsl").write_text('#include "kiln_particle.hlsl"\n'
                                                  'void up(inout KilnSprite s, KilnContext c) { s.velocity.y += c.user0.x * c.dt; }\n'
                                                  '#define KILN_UPDATE up\n#include "kiln_particle_main.hlsl"\n')
        (mod / "shaders" / "reach.hlsl").write_text('#include "kiln_particle.hlsl"\n'
                                                   '[[vk::binding(22, 1)]] Texture3D<float> g_noise;\n'
                                                   'void up(inout KilnSprite s, KilnContext c) { s.aux = g_noise.Load(int4(0, 0, 0, 0)); }\n'
                                                   '#define KILN_UPDATE up\n#include "kiln_particle_main.hlsl"\n')
        p = run("shader", "build", "--mod", mod, ok=(1,))
        r = {Path(x["source"]).name: x for x in json.loads(p.stdout)}
        self.assertTrue(r["good.hlsl"]["ok"], r)
        self.assertFalse(r["reach.hlsl"]["ok"])
        self.assertIn("set 1 binding 22", r["reach.hlsl"]["problem"])
        self.assertIn(b"kiln-vfx-abi-", (mod / "shaders/build/good.spv").read_bytes())

    def test_sample_cooks(self):
        c = cook(SAMPLE, tmp=self.tmp)
        self.assertEqual(c.report["modModules"], 1)
        self.assertEqual(set(c.blocks()), {"swirl", "swirl_plain", "swirl_broken"})
        self.assertTrue(any("swirl_broken" in w and "no Kiln ABI stamp" in w for w in c.report["warnings"]))


if __name__ == "__main__":
    unittest.main()


class ReplacingMaterial(Base):
    GAME = "39745562-5d58-4749-bc64-582e5630324a"   # npc_props_workbench00

    def mod(self, materials, name="rep"):
        m = Mod(self.tmp, name, {}, files={"t.png": png(16, 16)})
        (m.dir / "blocks" / "b.json").write_text(json.dumps({"format": 1, "materials": materials}))
        return m

    def test_built_under_the_game_guid_and_served_as_an_override(self):
        c = cook(self.mod({"bench": {"replaces": self.GAME, "albedo": "t.png"}, "own": {"albedo": "t.png"}}), tmp=self.tmp)
        mats = {m["name"]: m for m in c.report["materials"]}
        self.assertEqual(mats["bench"]["guid"], self.GAME)
        self.assertTrue(mats["bench"]["replaces"])
        self.assertNotIn("replaces", mats["own"])
        modes = {e["guid"]: e["mode"] for e in c.manifest["entries"] if e["kind"] == "resource"}
        self.assertEqual(modes[self.GAME], "override")
        self.assertEqual(modes[mats["own"]["guid"]], "new")
        # a replacement takes the game material's own slot
        self.assertEqual(c.report["budget"]["materials"]["mods"], 1)
        v = c.decoded(self.GAME, "keen::RenderMaterialResource")
        self.assertEqual([i["texture"]["content"]["bundleId"] for i in v["images"]][0], 0x39745562)

    def test_mistakes(self):
        c = cook(self.mod({"a": {"replaces": "workbench"}, "b": {"replaces": self.GAME, "was": "x"},
                           "c": {"replaces": self.GAME}, "d": {"replaces": self.GAME}}), tmp=self.tmp, ok=(1,))
        text = "\n".join(c.report["errors"])
        self.assertIn("materials.a.replaces", text)
        self.assertIn("materials.b.replaces", text)
        self.assertIn("another block of this mod replaces the same game material", text)
