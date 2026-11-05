# SPDX-License-Identifier: MIT
"""Materials in model steps: a MaterialBlock by name, and a glTF's own materials made into blocks. Against a synthetic game (tools/kfc's test builder), with nothing from the real one.

    python3 -m unittest discover -s tools/modelcook/tests -p 'test_material*' -v

With kiln-vfxcook built ($KILN_OUT/vfxcook/bin) the blocks made here also go through the builder, and the flags a model is cooked with are held to the flags the builder writes.
"""
import base64
import json
import os
import struct
import subprocess
import sys
import tempfile
import unittest
import uuid
import zlib
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
sys.path[:0] = [str(HERE.parent), str(HERE.parent / "samples"), str(REPO / "tools" / "kfc"), str(REPO / "tools" / "kfc" / "tests")]

import material_sword  # noqa: E402
import rigged_figure  # noqa: E402
from cook import gltf, material as MB, recipe, rendermodel as RM, skin as SK, steps  # noqa: E402
from kfc.archive import Kfc  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.blobwrite import Encoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402
from test_kfc import build_kfc  # noqa: E402

TYPES = TypeDb.load(HERE / "fixtures" / "types-rendermodel.json")
GAME_MATERIAL = "11111111-0000-4000-8000-000000000001"
NS = uuid.UUID("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77")
MANIFEST = json.dumps({"id": "mat-mod", "version": "1.0.0", "side": "both", "clients": "mod"})


def png_bytes(w=8, h=8):
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d))
    rows = (b"\0" + b"\xff\x80\x40\xff" * w) * h
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")


def block_guid(name, mod="mat-mod"):
    return str(uuid.uuid5(NS, f"{mod}:material:{name}"))


class Base(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        root = Path(cls.tmp.name)
        game = root / "game"
        game.mkdir()
        material = Encoder(TYPES).encode("keen::RenderMaterialResource", {"effectId": "Pbr", "flags": [], "debugName": "m"})
        th = TYPES.get("keen::RenderMaterialResource")["hash"]
        build_kfc(game, [(GAME_MATERIAL, th, 0, material)], [b"unused"], chunk_size=4096)
        k = Kfc(game)
        kb = bytearray((game / "test.kfc").read_bytes())
        vo = struct.unpack_from("<I", kb, 0x80)[0] + 0x80
        for i, (h, _) in enumerate(sorted(k.bundles.items())):
            struct.pack_into("<I", kb, vo + 12 * i, TYPES.get(h)["layout"])
        (game / "test.kfc").write_bytes(bytes(kb))
        cls.kfc = Kfc(game)

    @classmethod
    def tearDownClass(cls):
        cls.kfc.close()
        cls.tmp.cleanup()

    def mod(self, name, steps_, blocks=None, block_text=None):
        root = Path(self.tmp.name) / name
        (root / "assets" / "src").mkdir(parents=True)
        (root / "kiln.mod.json").write_text(MANIFEST)
        material_sword.write(root)
        if blocks is not None or block_text is not None:
            (root / "blocks").mkdir()
            (root / "blocks" / "m.json").write_text(block_text if block_text is not None else json.dumps({"format": 1, "materials": blocks}))
        p = root / "assets" / "cook.json"
        p.write_text(json.dumps({"format": 1, "tool": "modelcook", "out": "cooked", "inputs": {"mat": {"guid": GAME_MATERIAL, "type": "keen::RenderMaterialResource"}}, "steps": steps_}))
        return p

    def model_of(self, p, stem):
        return Decoder(TYPES).decode("keen::RenderModel", (p.parent / "cooked" / f"{stem}.bin").read_bytes())

    def run_recipe(self, p):
        r = recipe.run(p, self.kfc, TYPES, update_manifest=True)
        self.assertTrue(r["ok"], json.dumps(r["steps"], indent=1)[:2000])
        return r


class ByName(Base):
    STEP = {"op": "model", "gltf": "src/frost_sword.gltf", "guid": "44444444-0000-4000-8000-000000000004", "mode": "new", "stem": "s"}

    def test_a_block_by_name_gives_the_model_its_guid_and_flags(self):
        p = self.mod("a", [dict(self.STEP, materialMap={"blade": "steel", "grip": "@mat"})], {"steel": {"kind": "foliage", "shadows": False}})
        self.run_recipe(p)
        d = self.model_of(p, "s")
        got = {m["material"]: m for m in d["materials"]}
        self.assertEqual(set(got), {block_guid("steel"), GAME_MATERIAL})
        self.assertEqual(got[block_guid("steel")]["renderEffectId"], "Pbr")
        self.assertEqual(set(got[block_guid("steel")]["materialFlags"]), {"AlphaTested", "DoubleSided", "DisableShadows", "Translucent", "Foliage"})

    def test_one_material_for_every_primitive(self):
        p = self.mod("b", [dict(self.STEP, material="steel")], {"steel": {}})
        self.run_recipe(p)
        self.assertEqual({m["material"] for m in self.model_of(p, "s")["materials"]}, {block_guid("steel")})

    def test_was_keeps_the_guid(self):
        p = self.mod("c", [dict(self.STEP, material="new_name")], {"new_name": {"was": "old_name"}})
        self.run_recipe(p)
        self.assertEqual(self.model_of(p, "s")["materials"][0]["material"], block_guid("old_name"))

    def test_comments_and_trailing_commas_in_blocks(self):
        p = self.mod("d", [dict(self.STEP, material="steel")], block_text='{ "format": 1, // a block\n "materials": { "steel": { "kind": "cloth", }, }, /* done */ }')
        self.run_recipe(p)
        self.assertIn("DoubleSided", self.model_of(p, "s")["materials"][0]["materialFlags"])

    def test_a_name_that_is_nothing(self):
        p = self.mod("e", [dict(self.STEP, material="nope")], {"steel": {}})
        with self.assertRaisesRegex(steps.StepError, "'nope' is neither a GUID nor a block"):
            recipe.run(p, self.kfc, TYPES)

    def test_a_change_in_the_blocks_means_a_new_cook(self):
        p = self.mod("f", [dict(self.STEP, material="steel")], {"steel": {}})
        self.assertTrue(recipe.needs_cook(p))
        self.run_recipe(p)
        self.assertFalse(recipe.needs_cook(p))
        (p.parent.parent / "blocks" / "m.json").write_text(json.dumps({"format": 1, "materials": {"steel": {"kind": "cutout"}}}))
        self.assertTrue(recipe.needs_cook(p))

    def test_a_game_material_still_works_and_reads_no_blocks(self):
        p = self.mod("g", [dict(self.STEP, material="@mat")])
        self.run_recipe(p)
        self.assertEqual(self.model_of(p, "s")["materials"][0]["material"], GAME_MATERIAL)


class Skinned(Base):
    def test_a_body_draws_with_a_block(self):
        p = self.mod("a", [{"tool": "animcook", "op": "skeleton", "name": "figure", "gltf": "src/figure.glb"},
                           {"op": "model", "gltf": "src/figure.glb", "skeleton": "figure", "material": "skin", "name": "figure", "stem": "figure"}],
                     {"skin": {"kind": "cloth", "tint": "#c08060"}})
        rigged_figure.write(p.parent / "src" / "figure.glb")
        self.run_recipe(p)
        d = self.model_of(p, "figure")
        self.assertTrue(d["materials"])
        self.assertEqual({m["material"] for m in d["materials"]}, {block_guid("skin")})
        self.assertIn("DoubleSided", d["materials"][0]["materialFlags"])
        self.assertTrue(d["skinningData"]["count"])

    def test_a_body_can_use_its_gltfs_materials(self):
        p = self.mod("b", [{"tool": "animcook", "op": "skeleton", "name": "figure", "gltf": "src/figure.glb"},
                           {"op": "model", "gltf": "src/figure.glb", "skeleton": "figure", "materials": "gltf", "materialMap": {"club": "@mat"}, "name": "figure", "stem": "figure"}])
        rigged_figure.write(p.parent / "src" / "figure.glb")
        self.run_recipe(p)
        self.assertEqual({m["material"] for m in self.model_of(p, "figure")["materials"]}, {block_guid("figure.body"), GAME_MATERIAL})
        self.assertTrue((p.parent.parent / "blocks" / "figure.materials.json").exists())


class FromGltf(Base):
    STEP = {"op": "model", "gltf": "src/ember_sword.gltf", "guid": "44444444-0000-4000-8000-000000000005", "mode": "new", "stem": "ember", "materials": "gltf"}

    def test_it_writes_blocks_and_pngs_and_the_model_names_them(self):
        p = self.mod("a", [self.STEP])
        self.run_recipe(p)
        root = p.parent.parent
        doc = json.loads((root / "blocks" / "ember.materials.json").read_text())
        self.assertEqual(doc["format"], 1)
        self.assertEqual(set(doc["materials"]), {"ember.blade", "ember.grip"})
        blade, grip = doc["materials"]["ember.blade"], doc["materials"]["ember.grip"]
        self.assertEqual(blade["kind"], "emissive")
        self.assertTrue((root / blade["albedo"]).read_bytes().startswith(b"\x89PNG"))
        self.assertEqual(blade["metallic"], 0.9)
        self.assertEqual(blade["roughness"], 0.55)
        self.assertEqual(blade["emissive"]["strength"], 6)
        self.assertTrue((root / blade["emissive"]["map"]).exists())
        self.assertEqual(grip["metallic"], 0)
        self.assertEqual(grip["tint"], MB._hex([0.12, 0.06, 0.04]))
        d = self.model_of(p, "ember")
        self.assertEqual([m["material"] for m in d["materials"]], [block_guid("ember.blade"), block_guid("ember.grip")])

    def test_a_map_entry_wins_over_the_gltf(self):
        p = self.mod("b", [dict(self.STEP, materialMap={"grip": "@mat"})])
        self.run_recipe(p)
        self.assertEqual([m["material"] for m in self.model_of(p, "ember")["materials"]], [block_guid("ember.blade"), GAME_MATERIAL])

    def test_a_cook_twice_is_the_same_and_not_its_own_source(self):
        p = self.mod("c", [self.STEP])
        self.run_recipe(p)
        first = (p.parent.parent / "blocks" / "ember.materials.json").read_bytes()
        r = self.run_recipe(p)
        self.assertEqual((p.parent.parent / "blocks" / "ember.materials.json").read_bytes(), first)
        self.assertFalse(recipe.needs_cook(p))
        self.assertEqual(set(r["sources"]), {"src/ember_sword.gltf"})

    def test_a_recipe_without_a_material_source_says_so(self):
        p = self.mod("d", [{"op": "model", "gltf": "src/frost_sword.gltf", "stem": "x"}])
        with self.assertRaisesRegex(recipe.RecipeError, "nothing says what to draw"):
            recipe.load(p)

    def test_only_gltf_is_a_value(self):
        p = self.mod("e", [dict(self.STEP, materials="auto")])
        with self.assertRaisesRegex(recipe.RecipeError, "the only value is"):
            recipe.load(p)


def doc_for(material, images=None, samplers=None, extra=None):
    """A minimal one-triangle glTF document with one material, in a temp file."""
    buf = struct.pack("<9f", 0, 0, 0, 1, 0, 0, 0, 1, 0)
    d = {"asset": {"version": "2.0"}, "scene": 0, "scenes": [{"nodes": [0]}], "nodes": [{"mesh": 0}], "materials": [material],
         "meshes": [{"primitives": [{"attributes": {"POSITION": 0}, "material": 0}]}], "accessors": [{"bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3"}],
         "bufferViews": [{"buffer": 0, "byteLength": len(buf)}], "buffers": [{"byteLength": len(buf), "uri": "data:;base64," + base64.b64encode(buf).decode()}]}
    if images:
        d["images"] = images
        d["textures"] = [{"source": i, **({"sampler": 0} if samplers else {})} for i in range(len(images))]
    if samplers:
        d["samplers"] = samplers
    if extra:
        d.update(extra)
    return d


class Mapping(unittest.TestCase):
    """A glTF material, field by field."""

    def block(self, material, images=None, samplers=None):
        with tempfile.TemporaryDirectory() as t:
            root = Path(t)
            (root / "assets" / "src").mkdir(parents=True)
            p = root / "assets" / "src" / "m.gltf"
            p.write_text(json.dumps(doc_for(material, images, samplers)))
            names, blocks, files, warnings = MB.from_gltf(p, root, "m", "assets/cooked/materials")
            self.files = files
            return blocks[names[material.get("name", "material0")]], warnings

    def data_image(self, data=None):
        return {"uri": "data:image/png;base64," + base64.b64encode(data or png_bytes()).decode(), "name": "i"}

    def test_defaults_follow_the_gltf_spec(self):
        b, w = self.block({"name": "x"})
        self.assertEqual((b["metallic"], b["roughness"], b["tint"], b["kind"]), (1, 1, "#ffffff", "metal"))
        self.assertEqual(w, [])

    def test_factors_become_srgb(self):
        b, _ = self.block({"name": "x", "pbrMetallicRoughness": {"baseColorFactor": [0.5, 0.0, 1.0, 1], "metallicFactor": 0, "roughnessFactor": 0.25}})
        self.assertEqual(b["tint"], "#bc00ff")
        self.assertEqual((b["metallic"], b["roughness"]), (0, 0.25))
        self.assertNotIn("kind", b)

    def test_maps(self):
        images = [self.data_image()]
        tex = {"index": 0}
        b, _ = self.block({"name": "x", "pbrMetallicRoughness": {"baseColorTexture": tex, "metallicRoughnessTexture": tex, "roughnessFactor": 0.5}, "normalTexture": {"index": 0, "scale": 2},
                           "occlusionTexture": tex, "emissiveTexture": tex, "emissiveFactor": [1, 0, 0]}, images)
        rel = "assets/cooked/materials/m/i.png"
        self.assertEqual((b["albedo"], b["metallicRoughness"], b["occlusion"]), (rel, rel, rel))
        self.assertEqual(b["roughness"], 0.5)
        self.assertNotIn("metallic", b)
        self.assertEqual(b["normal"], {"map": rel, "strength": 2})
        self.assertEqual(b["emissive"], {"color": "#ff0000", "strength": 1, "map": rel})
        self.assertEqual(self.files, {rel: png_bytes()})

    def test_alpha_modes(self):
        b, _ = self.block({"name": "x", "alphaMode": "MASK", "alphaCutoff": 0.3})
        self.assertEqual((b["kind"], b["alphaTest"]), ("cutout", 0.3))
        b, w = self.block({"name": "x", "alphaMode": "BLEND"})
        self.assertEqual(b["kind"], "dithered")
        self.assertIn("BLEND", w[0])

    def test_double_sided_and_uv_scale(self):
        images = [self.data_image()]
        b, _ = self.block({"name": "x", "doubleSided": True, "pbrMetallicRoughness": {"baseColorTexture": {"index": 0, "extensions": {"KHR_texture_transform": {"scale": [2, 3]}}}}}, images)
        self.assertEqual(b["doubleSided"], True)
        self.assertEqual(b["uvScale"], [2, 3])

    def test_what_the_game_cant_do_warns_by_name(self):
        images = [self.data_image()]
        _, w = self.block({"name": "x", "pbrMetallicRoughness": {"baseColorTexture": {"index": 0}}, "extensions": {"KHR_materials_unlit": {}}}, images, samplers=[{"wrapS": 33071}])
        text = " ".join(w)
        self.assertIn("doesn't repeat", text)
        self.assertIn("KHR_materials_unlit", text)

    def test_what_the_game_cant_take_is_refused_by_name(self):
        tex = {"pbrMetallicRoughness": {"baseColorTexture": {"index": 0}}}
        with self.assertRaisesRegex(MB.MaterialError, "not a PNG"):
            self.block({"name": "x", **tex}, [self.data_image(b"\xff\xd8\xffJPEG" + b"0" * 32)])
        with self.assertRaisesRegex(MB.MaterialError, "6x8"):
            self.block({"name": "x", **tex}, [self.data_image(png_bytes(6, 8))])
        with self.assertRaisesRegex(MB.MaterialError, "UV set 1"):
            self.block({"name": "x", "pbrMetallicRoughness": {"baseColorTexture": {"index": 0, "texCoord": 1}}}, [self.data_image()])

    def test_an_external_png_stays_where_it_is(self):
        with tempfile.TemporaryDirectory() as t:
            root = Path(t)
            (root / "src").mkdir()
            (root / "src" / "a.png").write_bytes(png_bytes())
            p = root / "src" / "m.gltf"
            p.write_text(json.dumps(doc_for({"name": "x", "pbrMetallicRoughness": {"baseColorTexture": {"index": 0}}}, [{"uri": "a.png"}])))
            names, blocks, files, _ = MB.from_gltf(p, root, "m", "out")
            self.assertEqual(blocks["m.x"]["albedo"], "src/a.png")
            self.assertEqual(files, {})

    def test_an_image_outside_the_mod_is_refused(self):
        with tempfile.TemporaryDirectory() as t:
            root = Path(t)
            (root / "mod").mkdir()
            (root / "a.png").write_bytes(png_bytes())
            p = root / "m.gltf"
            p.write_text(json.dumps(doc_for({"name": "x", "pbrMetallicRoughness": {"baseColorTexture": {"index": 0}}}, [{"uri": "a.png"}])))
            with self.assertRaisesRegex(MB.MaterialError, "outside the mod's folder"):
                MB.from_gltf(p, root / "mod", "m", "out")

    def test_vertex_colours_turn_on_only_when_they_are_colours(self):
        def doc(colour):
            buf = struct.pack("<9f", 0, 0, 0, 1, 0, 0, 0, 1, 0) + struct.pack("<12f", *(colour * 3))
            d = doc_for({"name": "x"})
            d["meshes"][0]["primitives"][0]["attributes"]["COLOR_0"] = 1
            d["accessors"].append({"bufferView": 1, "componentType": 5126, "count": 3, "type": "VEC4"})
            d["bufferViews"] = [{"buffer": 0, "byteLength": 36}, {"buffer": 0, "byteOffset": 36, "byteLength": 48}]
            d["buffers"] = [{"byteLength": len(buf), "uri": "data:;base64," + base64.b64encode(buf).decode()}]
            return d
        with tempfile.TemporaryDirectory() as t:
            p = Path(t) / "m.gltf"
            p.write_text(json.dumps(doc((1.0, 1.0, 1.0, 1.0))))
            self.assertNotIn("vertexColor", MB.from_gltf(p, t, "m", "o")[1]["m.x"])
            p.write_text(json.dumps(doc((1.0, 0.0, 0.0, 1.0))))
            self.assertTrue(MB.from_gltf(p, t, "m", "o")[1]["m.x"]["vertexColor"])


def vfxcook():
    out = os.environ.get("KILN_OUT")
    tool = Path(out) / "vfxcook" / "bin" / "kiln-vfxcook" if out else None
    return tool if tool and tool.exists() else None


FLAGS = ["Tessellated", "AlphaTested", "DoubleSided", "DisableShadows", "CanDissolve", "Translucent", "Foliage", "HashedAlpha"]


@unittest.skipUnless(vfxcook(), "kiln-vfxcook isn't built")
class AgainstTheBuilder(Base):
    def cook(self, mod_dir):
        out = Path(self.tmp.name) / ("out-" + Path(mod_dir).name)
        p = subprocess.run([str(vfxcook()), "cook", "--mod", str(mod_dir), "--out", str(out)], capture_output=True, text=True)
        self.assertEqual(p.returncode, 0, p.stderr)
        return json.loads((out / "report.json").read_text())

    def test_the_flags_a_model_is_cooked_with_are_the_ones_the_builder_writes(self):
        blocks = {k: {"kind": k} for k in MB.KINDS}
        blocks.update({"plain": {}, "two": {"doubleSided": True}, "noshadow": {"shadows": False}, "cut": {"alphaTest": 0.25}, "uncut": {"kind": "cutout", "alphaTest": False},
                       "melt": {"dissolve": True}, "through": {"translucent": 0.5}, "solid": {"kind": "foliage", "translucent": 0, "doubleSided": False}})
        p = self.mod("flags", [dict(ByName.STEP, material="plain")], blocks)
        report = self.cook(p.parent.parent)
        built = {m["name"]: m for m in report["materials"]}
        for name, block in blocks.items():
            want = {FLAGS[i] for i in range(8) if built[name]["renderFlags"] >> i & 1}
            self.assertEqual(set(MB.flags_of(block)), want, name)
            self.assertEqual(built[name]["guid"], block_guid(name), name)

    def test_blocks_made_from_a_gltf_build(self):
        p = self.mod("made", [FromGltf.STEP])
        self.run_recipe(p)
        report = self.cook(p.parent.parent)
        self.assertEqual({m["name"] for m in report["materials"]}, {"ember.blade", "ember.grip"})
        self.assertEqual(report["errors"], [])


if __name__ == "__main__":
    unittest.main()
