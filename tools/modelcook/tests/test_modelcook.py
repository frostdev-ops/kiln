# SPDX-License-Identifier: MIT
"""modelcook tests without game files: glTF reading (gltf + glb, node transforms), cooking the procedural sample sword
against the reflected RenderModel layout in tests/fixtures (a closure of the game's reflection: layouts only, no game
data), decode with tools/kfc and compare.

    python3 -m unittest discover -s tools/modelcook/tests -v
"""
import json
import math
import struct
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE.parent / "samples"))
sys.path.insert(0, str(HERE.parent.parent / "kfc"))

import cube_sword  # noqa: E402
from cook import geometry as G, gltf, rendermodel as RM  # noqa: E402
from cook.validate import validate  # noqa: E402
from kfc import mesh  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

TYPES = TypeDb.load(HERE / "fixtures" / "types-rendermodel.json")
MATERIAL = {"material": "71165a08-1a37-495c-9cb9-39cd1145ebf0", "renderEffectId": "Pbr", "materialFlags": []}


def base_desc():
    """A minimal template: every RenderModel field present (as a decoded vanilla model would have them)."""
    return {"debugName": "", "materials": [], "meshlets": [], "meshes": [], "lods": []}


def cook(path, variant="cubes"):
    doc = gltf.read(path)
    lod = []
    for prim in doc["primitives"]:
        p = RM.prepare(prim)
        p["material_slot"] = 0
        lod.append(p)
    desc, blob = RM.build([lod], [MATERIAL], base_desc(), "test_" + variant)
    return doc, desc, RM.encode(TYPES, desc), blob


class GltfTests(unittest.TestCase):
    def test_sample_reads(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "s.gltf"
            nv, nt = cube_sword.write(p)
            doc = gltf.read(p)
            pr = doc["primitives"][0]
            self.assertEqual(len(pr["positions"]), nv)
            self.assertEqual(len(pr["indices"]), 3 * nt)
            self.assertIsNotNone(pr["normals"])
            self.assertEqual(doc["materials"], ["sword"])

    def test_glb_with_node_transform(self):
        # one triangle, node translated by +1 y and mirrored in x (scale -1): winding must be flipped back
        pos = struct.pack("<9f", 0, 0, 0, 1, 0, 0, 0, 1, 0)
        idx = struct.pack("<3H", 0, 1, 2) + b"\0\0"
        bin_ = pos + idx
        doc = {"asset": {"version": "2.0"}, "scene": 0, "scenes": [{"nodes": [0]}],
               "nodes": [{"mesh": 0, "translation": [0, 1, 0], "scale": [-1, 1, 1]}],
               "meshes": [{"primitives": [{"attributes": {"POSITION": 0}, "indices": 1}]}],
               "buffers": [{"byteLength": len(bin_)}],
               "bufferViews": [{"buffer": 0, "byteOffset": 0, "byteLength": 36},
                               {"buffer": 0, "byteOffset": 36, "byteLength": 6}],
               "accessors": [{"bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3"},
                             {"bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR"}]}
        js = json.dumps(doc).encode()
        js += b" " * ((4 - len(js) % 4) % 4)
        glb = struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(bin_))
        glb += struct.pack("<II", len(js), 0x4E4F534A) + js + struct.pack("<II", len(bin_), 0x004E4942) + bin_
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "t.glb"
            p.write_bytes(glb)
            pr = gltf.read(p)["primitives"][0]
        self.assertEqual(pr["positions"][1], (-1.0, 1.0, 0.0))
        self.assertEqual(pr["indices"], [0, 2, 1])
        n = G.compute_normals(pr["positions"], pr["indices"])
        self.assertAlmostEqual(n[0][2], 1.0)  # still facing +z after the mirror


class CookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.path = Path(cls.tmp.name) / "sword.gltf"
        cube_sword.write(cls.path)
        cls.doc, cls.desc, cls.data, cls.blob = cook(cls.path)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_validates_offline(self):
        r = validate(None, TYPES, self.data, self.blob, gltf_docs=[self.doc])
        self.assertTrue(r["ok"], r)
        self.assertLess(r["roundtrip"]["max_position_error_m"], 1e-6)

    def test_layout(self):
        d = Decoder(TYPES).decode("keen::RenderModel", self.data)
        self.assertEqual(d["vertexData"], {"start": 0, "count": 24 * d["meshes"][0]["vertexCount"]})
        self.assertEqual(d["indexData"]["start"], d["vertexData"]["count"])
        self.assertEqual(d["indexData"]["count"], 2 * d["meshes"][0]["indexCount"])
        self.assertEqual(len(self.blob), d["indexData"]["start"] + d["indexData"]["count"])
        self.assertEqual(d["meshes"][0]["vertexFormat"], "PNTCT")
        self.assertEqual(d["lods"], [{"meshOffset": 0, "meshCount": 1, "pixelSize": 0.0}])
        self.assertEqual(d["renderData"]["category"]["value"], RM.RENDER_DATA_CATEGORY)
        # the content hash kiln.assets derives (SHA-256) sits at the patch offset
        h = struct.unpack_from("<4I", self.data, RM.RENDER_DATA_OFFSET)
        self.assertEqual(h, RM.content_hash(self.blob))

    def test_quantization_box(self):
        d = Decoder(TYPES).decode("keen::RenderModel", self.data)
        bb, of, sc = d["boundingBox"], d["positionOffset"], d["positionScale"]
        half = max((bb["max"][a] - bb["min"][a]) / 2 for a in "xyz")
        for a in "xyz":
            self.assertAlmostEqual(of[a], bb["min"][a] - 0.2 * half, places=5)
            self.assertAlmostEqual(of[a] + sc[a], bb["max"][a] + 0.2 * half, places=5)

    def test_handedness(self):
        """Game space is left-handed: positions are x-mirrored and, like every vanilla model, no triangle is
        counter-clockwise around its normals with the component cross product."""
        d = Decoder(TYPES).decode("keen::RenderModel", self.data)
        m = mesh.RenderModel("t", d, self.blob)
        tri = mesh.triangles(m, 0)
        self.assertEqual(mesh.winding_check(tri), 0.0)
        src = self.doc["primitives"][0]["positions"]
        got = tri.submeshes[0]["positions"]
        i = max(range(len(src)), key=lambda k: abs(src[k][0]))
        self.assertAlmostEqual(got[i][0], -src[i][0], places=5)
        self.assertGreater(mesh.winding_check(mesh.to_rh(tri)), 0.99)

    def test_tangent_handedness_roundtrip(self):
        d = Decoder(TYPES).decode("keen::RenderModel", self.data)
        rh = mesh.to_rh(mesh.triangles(mesh.RenderModel("t", d, self.blob), 0)).submeshes[0]
        src = RM.prepare(self.doc["primitives"][0])  # game space
        for t_src, t_rh in zip(src["tangents"], rh["tangents"]):
            self.assertEqual(-t_src[3], t_rh[3])

    def test_too_many_vertices_refused(self):
        prim = {"positions": [(0, 0, 0)] * 70000, "indices": [0, 1, 2], "normals": None, "tangents": None,
                "uvs": None, "colors": None, "material": None, "material_name": None}
        with self.assertRaises(RM.CookError):
            RM.prepare(prim)

    def test_two_lods_two_groups(self):
        doc = gltf.read(self.path)
        lods = []
        for _ in range(2):
            p = RM.prepare(doc["primitives"][0])
            p["material_slot"] = 0
            lods.append([p])
        desc, blob = RM.build(lods, [MATERIAL], base_desc(), "two", pixel_sizes=[0.0, 0.05])
        data = RM.encode(TYPES, desc)
        d = Decoder(TYPES).decode("keen::RenderModel", data)
        self.assertEqual([x["meshOffset"] for x in d["lods"]], [0, 1])
        self.assertEqual(d["meshes"][1]["vertexDataByteOffset"], 24 * d["meshes"][0]["vertexCount"])
        self.assertEqual(d["meshes"][1]["indexOffset"], d["meshes"][0]["indexCount"])
        self.assertEqual(d["meshes"][1]["skinVertexOffset"], d["meshes"][0]["vertexCount"])
        r = validate(None, TYPES, data, blob)
        self.assertTrue(r["ok"], r)


class GeometryTests(unittest.TestCase):
    def test_tangents_follow_u(self):
        P = [(0, 0, 0), (1, 0, 0), (0, 1, 0)]
        UV = [(0, 0), (1, 0), (0, 1)]
        N = G.compute_normals(P, [0, 1, 2])
        T = G.compute_tangents(P, N, UV, [0, 1, 2])
        self.assertAlmostEqual(T[0][0], 1.0)
        self.assertEqual(T[0][3], 1.0)
        self.assertTrue(all(math.isclose(n[2], 1.0) for n in N))


if __name__ == "__main__":
    unittest.main()


class RecipeTests(unittest.TestCase):
    """cook.json against a synthetic game: a made-up base RenderModel, material and visual template packed into a
    synthetic KFC4 (tools/kfc's test builder); nothing from the real game."""

    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(HERE.parent.parent / "kfc" / "tests"))
        from test_kfc import build_kfc
        from kfc.archive import Kfc
        from kfc.blobwrite import Encoder
        from cook import recipe
        cls.recipe = recipe
        cls.tmp = tempfile.TemporaryDirectory()
        root = Path(cls.tmp.name)
        game = root / "game"
        game.mkdir()
        enc = Encoder(TYPES)
        mat_guid, base_model, base_visual = ("11111111-0000-4000-8000-000000000001", "22222222-0000-4000-8000-000000000002",
                                             "33333333-0000-4000-8000-000000000003")
        material = enc.encode("keen::RenderMaterialResource", {"effectId": "Pbr", "flags": [], "debugName": "m"})
        doc = gltf.read(cls._sword(root))
        p = RM.prepare(doc["primitives"][0])
        p["material_slot"] = 0
        desc, base_blob = RM.build([[p]], [{"material": mat_guid, "renderEffectId": "Pbr", "materialFlags": []}], base_desc(), "base")
        model = RM.encode(TYPES, desc)
        visual = enc.encode("keen::ecs::TemplateResource", {"name": "Visuals_Base", "components": [
            {"$type": "keen::ecs::ModelResource", "value": {"model": base_model}},
            {"$type": "keen::ecs::SlotAttachment", "value": {"attachmentSlot": "HandR"}}]})
        th = {n: TYPES.get(n)["hash"] for n in ("keen::RenderMaterialResource", "keen::RenderModel", "keen::ecs::TemplateResource")}
        build_kfc(game, [(mat_guid, th["keen::RenderMaterialResource"], 0, material),
                         (base_model, th["keen::RenderModel"], 0, model),
                         (base_visual, th["keen::ecs::TemplateResource"], 0, visual)],
                  [b"unused", (RM.content_hash(base_blob), base_blob)], chunk_size=4096)
        # bundles carry the real layout hashes (the builder writes placeholders): patch them in
        k = Kfc(game)
        kb = bytearray((game / "test.kfc").read_bytes())
        vo = struct.unpack_from("<I", kb, 0x80)[0] + 0x80
        for i, (h, bd) in enumerate(sorted(k.bundles.items())):
            struct.pack_into("<I", kb, vo + 12 * i, TYPES.get(h)["layout"])
        (game / "test.kfc").write_bytes(bytes(kb))
        cls.kfc = Kfc(game)
        mod = root / "mod" / "assets"
        (mod / "src").mkdir(parents=True)
        cube_sword.write(mod / "src" / "sword.gltf", "tower")
        cls.recipe_path = mod / "cook.json"
        cls.recipe_path.write_text(json.dumps({
            "format": 1, "tool": "modelcook", "out": "cooked",
            "inputs": {"model": {"guid": base_model, "type": "keen::RenderModel"},
                       "visual": {"guid": base_visual, "type": "keen::ecs::TemplateResource"}},
            "steps": [
                {"op": "model", "gltf": "src/sword.gltf", "base": "model", "guid": "@model", "mode": "override", "stem": "a"},
                {"op": "model", "gltf": "src/sword.gltf", "base": "model", "guid": "44444444-0000-4000-8000-000000000004",
                 "stem": "b"},
                {"op": "visual", "base": "visual", "model": "44444444-0000-4000-8000-000000000004",
                 "guid": "55555555-0000-4000-8000-000000000005", "stem": "v"}]}))
        cls.mod = mod

    @staticmethod
    def _sword(root):
        p = root / "base.gltf"
        cube_sword.write(p)
        return p

    @classmethod
    def tearDownClass(cls):
        cls.kfc.close()
        cls.tmp.cleanup()

    def test_run_writes_outputs_manifest_and_report(self):
        self.assertTrue(self.recipe.needs_cook(self.recipe_path))
        r = self.recipe.run(self.recipe_path, self.kfc, TYPES, update_manifest=True)
        self.assertTrue(r["ok"], json.dumps(r["steps"], indent=1)[:2000])
        man = json.loads((self.mod / "assets.json").read_text())
        files = {e["file"] for e in man["entries"]}
        self.assertEqual(files, {"cooked/a.bin", "cooked/a.blob", "cooked/b.bin", "cooked/b.blob", "cooked/v.bin"})
        modes = {e["guid"]: e["mode"] for e in man["entries"] if e["kind"] == "resource"}
        self.assertEqual(modes["22222222-0000-4000-8000-000000000002"], "override")
        self.assertEqual(modes["44444444-0000-4000-8000-000000000004"], "new")
        v = Decoder(TYPES).decode("keen::ecs::TemplateResource", (self.mod / "cooked" / "v.bin").read_bytes())
        self.assertEqual(v["components"][0]["value"]["model"], "44444444-0000-4000-8000-000000000004")
        self.assertEqual(v["components"][1]["value"]["attachmentSlot"], "HandR")
        self.assertFalse(self.recipe.needs_cook(self.recipe_path))
        self.assertEqual(set(r["inputs"]), {"model", "visual"})
        (self.mod / "cooked" / "b.blob").write_bytes(b"tampered")
        self.assertTrue(self.recipe.needs_cook(self.recipe_path))

    def test_foreign_steps_are_left_to_their_tool(self):
        """A step with "tool": "soundcook" (and an input only it uses) is skipped; model outputs stay identical."""
        import shutil
        mod = Path(self.tmp.name) / "mixed" / "assets"
        shutil.copytree(self.mod / "src", mod / "src")
        plain = json.loads(self.recipe_path.read_text())
        mixed = json.loads(self.recipe_path.read_text())
        mixed["inputs"]["vanilla_cue"] = {"guid": "66666666-0000-4000-8000-000000000006", "type": "keen::SoundContainerResource"}
        mixed["steps"].insert(1, {"tool": "soundcook", "op": "cue", "name": "c", "base": "vanilla_cue", "sounds": ["x"]})
        (mod / "cook.json").write_text(json.dumps(mixed))
        doc = self.recipe.load(mod / "cook.json")
        self.assertEqual([i for i, _ in self.recipe.own_steps(doc)], [0, 2, 3])
        self.assertEqual(self.recipe.other_tools(doc), ["soundcook"])
        self.assertEqual(self.recipe.report_name(doc), "cook-report.json")
        r = self.recipe.run(mod / "cook.json", self.kfc, TYPES, update_manifest=True)
        self.assertTrue(r["ok"])
        self.assertEqual([s["step"] for s in r["steps"]], [0, 2, 3])
        self.assertEqual(set(r["inputs"]), {"model", "visual"})          # the cue input is soundcook's
        mine = Path(self.tmp.name) / "plain" / "assets"
        shutil.copytree(self.mod / "src", mine / "src")
        (mine / "cook.json").write_text(json.dumps(plain))
        r2 = self.recipe.run(mine / "cook.json", self.kfc, TYPES, update_manifest=True)
        self.assertEqual({o["file"]: o["sha256"] for o in r["outputs"]}, {o["file"]: o["sha256"] for o in r2["outputs"]})
        self.assertEqual((mod / "assets.json").read_text(), (mine / "assets.json").read_text())
        # a sound recipe with model steps: modelcook's report gets its own name
        mixed["tool"] = "soundcook"
        for st in mixed["steps"]:
            st.setdefault("tool", "modelcook")
        mixed["steps"][1]["tool"] = "soundcook"
        (mod / "cook.json").write_text(json.dumps(mixed))
        self.assertEqual(self.recipe.report_name(self.recipe.load(mod / "cook.json")), "cook-report.modelcook.json")
        bad = dict(mixed, steps=[{"tool": "texcook", "op": "x"}])
        (mod / "bad.json").write_text(json.dumps(bad))
        with self.assertRaises(self.recipe.RecipeError):
            self.recipe.load(mod / "bad.json")

    def test_bad_recipe(self):
        bad = self.mod / "bad.json"
        bad.write_text(json.dumps({"format": 1, "steps": [{"op": "explode"}]}))
        with self.assertRaises(self.recipe.RecipeError):
            self.recipe.load(bad)
