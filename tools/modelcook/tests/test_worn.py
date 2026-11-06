# SPDX-License-Identifier: MIT
"""Worn models without game files: morph targets read and written as the vertex shader reads them, one-joint attachment, the steps a mod's blocks ask for.

    python3 -m unittest discover -s tools/modelcook/tests -p test_worn.py
"""
import base64
import json
import struct
import sys
import tempfile
import unittest
import uuid
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
sys.path[:0] = [str(HERE), str(HERE.parent), str(HERE.parent / "samples"), str(HERE.parent.parent / "kfc"), str(HERE.parent.parent / "animcook")]

import skindeform as D  # noqa: E402
import test_material_cook as MC  # noqa: E402
from cook import gltf, recipe, rendermodel as RM, skin as S, steps, worn as W  # noqa: E402
from cook.validate import validate  # noqa: E402
from kfc import mesh  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

TYPES = TypeDb.load(HERE / "fixtures" / "types-rendermodel.json")
MATERIAL = {"material": "71165a08-1a37-495c-9cb9-39cd1145ebf0", "renderEffectId": "Pbr", "materialFlags": []}
SKELETON = "aaaaaaaa-0000-4000-8000-000000000001"
BASE = {"debugName": "", "materials": [], "meshlets": [], "meshes": [], "lods": []}
QUAD = [(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (1.0, 1.0, 0.0), (0.0, 1.0, 0.0)]
TRIS = [0, 1, 2, 0, 2, 3]


def target_with(joints=3, at=(0.1, 1.5, -0.2)):
    bind = [[[1.0, 0.0, 0.0, 0.0], [0.0, 1.0, 0.0, 0.0], [0.0, 0.0, 1.0, 0.0], [0.0, 0.0, 0.0, 1.0]] for _ in range(joints)]
    bind[2] = [[1.0, 0.0, 0.0, at[0]], [0.0, 1.0, 0.0, at[1]], [0.0, 0.0, 1.0, at[2]], [0.0, 0.0, 0.0, 1.0]]
    return S.Target(SKELETON, [f"j{i}" for i in range(joints)], bind, "test")


def write_gltf(path, morphs, positions=QUAD, node=None, sparse=None, names_on="mesh", material="cloth"):
    """A glTF of one quad with morph targets, data in a data URI. morphs: {name: [delta per vertex]}."""
    blob = bytearray()
    views, accessors = [], []

    def add(data, comp, typ, count, **extra):
        views.append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data)})
        blob.extend(data)
        while len(blob) % 4:
            blob.append(0)
        accessors.append({"bufferView": len(views) - 1, "componentType": comp, "count": count, "type": typ, **extra})
        return len(accessors) - 1

    pos = add(b"".join(struct.pack("<3f", *p) for p in positions), 5126, "VEC3", len(positions), min=[0, 0, 0], max=[1, 1, 1])
    idx = add(struct.pack(f"<{len(TRIS)}H", *TRIS), 5123, "SCALAR", len(TRIS))
    targets = []
    for name, deltas in morphs.items():
        if sparse and name in sparse:  # only the listed vertices have data: Blender writes shape keys so
            used = sorted(sparse[name])
            iv = add(struct.pack(f"<{len(used)}H", *used), 5123, "SCALAR", len(used))
            vv = add(b"".join(struct.pack("<3f", *deltas[i]) for i in used), 5126, "VEC3", len(used))
            accessors.append({"componentType": 5126, "count": len(positions), "type": "VEC3",
                              "sparse": {"count": len(used), "indices": {"bufferView": accessors[iv]["bufferView"], "componentType": 5123}, "values": {"bufferView": accessors[vv]["bufferView"]}}})
            targets.append({"POSITION": len(accessors) - 1})
        else:
            targets.append({"POSITION": add(b"".join(struct.pack("<3f", *d) for d in deltas), 5126, "VEC3", len(positions))})
    prim = {"attributes": {"POSITION": pos}, "indices": idx, "material": 0}
    if targets:
        prim["targets"] = targets
    mesh_doc = {"name": "m", "primitives": [prim]}
    names = {"targetNames": list(morphs)}
    if names_on == "mesh":
        mesh_doc["extras"] = names
    elif names_on == "primitive":
        prim["extras"] = names
    doc = {"asset": {"version": "2.0"}, "scene": 0, "scenes": [{"nodes": [0]}], "nodes": [{"name": "piece", "mesh": 0, **(node or {})}], "meshes": [mesh_doc],
           "materials": [{"name": material}], "bufferViews": views, "accessors": accessors,
           "buffers": [{"byteLength": len(blob), "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(blob)).decode()}]}
    Path(path).write_text(json.dumps(doc))
    return path


def build(docs, target):
    """As steps.model does, with no game behind it. -> (desc, data, blob, warnings)."""
    lods = []
    for doc in docs:
        lods.append([dict(RM.prepare(p), material_slot=0) for p in doc["primitives"]])
    names, warnings = S.morph_names(lods)
    desc, blob = RM.build(lods, [MATERIAL], BASE, "piece", None, skin={"hierarchy": target.guid, "hierarchyJointCount": len(target), "globalScale": 1.0}, morphs=names)
    return desc, RM.encode(TYPES, desc), blob, warnings


class Reading(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.d = Path(self.tmp.name)

    def test_names_and_deltas(self):
        deltas = [(0.0, 0.1, 0.0)] * 4
        for where in ("mesh", "primitive"):
            d = gltf.read(write_gltf(self.d / f"{where}.gltf", {"Female": deltas}, names_on=where))
            self.assertAlmostEqual(d["primitives"][0]["morphs"]["Female"][2][1], 0.1, places=6)

    def test_no_names_are_numbered(self):
        d = gltf.read(write_gltf(self.d / "n.gltf", {"x": [(0.0, 0.0, 0.0)] * 4}, names_on=None))
        self.assertEqual(list(d["primitives"][0]["morphs"]), ["target0"])

    def test_sparse_target(self):
        deltas = [(0.0, 0.0, 0.0), (0.5, 0.0, 0.0), (0.0, 0.0, 0.0), (0.0, 0.25, 0.0)]
        d = gltf.read(write_gltf(self.d / "s.gltf", {"Female": deltas}, sparse={"Female": [1, 3]}))
        self.assertEqual(d["primitives"][0]["morphs"]["Female"], deltas)

    def test_node_transform_turns_a_rigid_mesh_targets(self):
        turn = {"scale": [2.0, 2.0, 2.0]}
        d = gltf.read(write_gltf(self.d / "t.gltf", {"Female": [(0.0, 0.1, 0.0)] * 4}, node=turn))
        self.assertAlmostEqual(d["primitives"][0]["morphs"]["Female"][0][1], 0.2, places=6)

    def test_reference_nodes_are_left_out(self):
        p = write_gltf(self.d / "r.gltf", {}, node={"name": "reference_body"})
        with self.assertRaises(gltf.GltfError):
            gltf.read(p)


class BlendData(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.d = Path(self.tmp.name)
        self.target = target_with()

    def cooked(self, morphs, **kw):
        doc = gltf.read(write_gltf(self.d / "a.gltf", morphs, **kw))
        S.attach(doc, self.target, 2)
        return (doc,) + build([doc], self.target)

    def test_layout_as_the_vertex_shader_reads_it(self):
        female = [(0.01 * i, 0.0, 0.0) for i in range(4)]
        hood = [(0.0, 0.0, 0.02 * i) for i in range(4)]
        doc, desc, data, blob, warnings = self.cooked({"Hood": hood, "Female": female})
        self.assertEqual(warnings, [])
        self.assertEqual([b["id"] for b in desc["blendShapes"]], [mesh.BLEND_SHAPES["Female"], mesh.BLEND_SHAPES["Hood"]], "in the flags' order")
        self.assertEqual(desc["blendShapeMask"], 3)
        st = desc["blendShapeData"]["start"]
        self.assertEqual(st, desc["skinningData"]["start"] + desc["skinningData"]["count"])
        self.assertEqual(desc["blendShapeData"]["count"], 4 * 6 * 2)
        self.assertEqual(desc["indexData"]["start"] % 4, 0)
        self.assertEqual(desc["indexData"]["start"], (st + 48 + 3) & ~3)
        for shape, want in enumerate((female, hood)):
            for v in range(4):
                # the shader: start + group offset + (shape index x vertices + vertex) x 6, three halves; glTF's x is mirrored
                got = struct.unpack_from("<3e", blob, st + (shape * 4 + v) * 6)
                self.assertAlmostEqual(got[0], -want[v][0], places=3)
                self.assertAlmostEqual(got[1], want[v][1], places=3)
                self.assertAlmostEqual(got[2], want[v][2], places=3)
        r = validate(None, TYPES, data, blob, gltf_docs=[doc], target=self.target)
        self.assertTrue(r["ok"], r["checks"])
        self.assertEqual(r["morphs"]["shapes"], ["Female", "Hood"])

    def test_decodes_back_with_tools_kfc(self):
        doc, desc, data, blob, _ = self.cooked({"Female": [(0.0, 0.03, 0.0)] * 4})
        d = Decoder(TYPES).decode("keen::RenderModel", data)
        m = mesh.RenderModel(guid="g", desc=d, data=blob)
        self.assertEqual(mesh.blend_groups(m), {0})
        self.assertAlmostEqual(mesh.blend_of(m, d["meshes"][0], 0)[3][1], 0.03, places=3)
        tri = mesh.to_rh(mesh.triangles(m, 0))
        self.assertAlmostEqual(tri.submeshes[0]["morphs"]["Female"][1][1], 0.03, places=3)

    def test_weight_moves_vertices_before_skinning(self):
        doc, desc, data, blob, _ = self.cooked({"Female": [(0.0, 0.25, 0.0)] * 4})
        d = Decoder(TYPES).decode("keen::RenderModel", data)
        hierarchy = {"skinToJointMatrices": [{"rot": {c: {a: float(a == c) for a in "xyz"} for c in "xyz"}, "pos": {"x": 0.0, "y": 0.0, "z": 0.0}}] * 3}
        world = [[[1.0, 0, 0, 0], [0, 1.0, 0, 0], [0, 0, 1.0, 0], [0, 0, 0, 1.0]] for _ in range(3)]
        base = D.cooked_posed(d, blob, hierarchy, world)
        moved = D.cooked_posed(d, blob, hierarchy, world, shapes={"Female": 1.0})
        half = D.cooked_posed(d, blob, hierarchy, world, shapes={"Female": 0.5})
        self.assertAlmostEqual(moved[0][0][1] - base[0][0][1], 0.25, places=3)
        self.assertAlmostEqual(half[0][0][1] - base[0][0][1], 0.125, places=3)

    def test_a_model_without_targets_is_unchanged(self):
        doc = gltf.read(write_gltf(self.d / "n.gltf", {}))
        S.attach(doc, self.target, 2)
        desc, data, blob, _ = build([doc], self.target)
        self.assertEqual(desc["blendShapes"], [])
        self.assertEqual(desc["blendShapeMask"], 0)
        self.assertEqual(desc["blendShapeData"], {"start": 0, "count": 0})
        self.assertEqual(desc["indexData"]["start"], desc["vertexData"]["count"] + desc["skinningData"]["count"])

    def test_case_is_forgiven_and_other_names_left_out(self):
        doc, desc, data, blob, warnings = self.cooked({"female": [(0.0, 0.1, 0.0)] * 4, "Smile": [(0.0, 0.2, 0.0)] * 4})
        self.assertEqual([mesh.BLEND_SHAPE_NAMES[b["id"]] for b in desc["blendShapes"]], ["Female"])
        self.assertEqual(desc["blendShapeData"]["count"], 24)
        self.assertTrue(any("Smile" in w for w in warnings), warnings)
        self.assertTrue(validate(None, TYPES, data, blob, gltf_docs=[doc], target=self.target)["ok"])

    def test_a_coarser_level_without_the_target(self):
        lod0 = gltf.read(write_gltf(self.d / "a.gltf", {"Female": [(0.0, 0.1, 0.0)] * 4}))
        lod1 = gltf.read(write_gltf(self.d / "b.gltf", {}))
        S.attach(lod0, self.target, 2), S.attach(lod1, self.target, 2)
        desc, data, blob, warnings = build([lod0, lod1], self.target)
        self.assertTrue(any("LOD 1" in w and "Female" in w for w in warnings), warnings)
        self.assertEqual(desc["blendShapeData"]["count"], 2 * 4 * 6, "every group has data, zeros where a level has no such target")
        self.assertEqual(desc["meshes"][1]["blendShapeByteOffset"], 24)
        self.assertEqual(blob[desc["blendShapeData"]["start"] + 24:desc["blendShapeData"]["start"] + 48], bytes(24))

    def test_more_shapes_than_apply_at_once(self):
        morphs = {n: [(0.0, 0.01, 0.0)] * 4 for n in ("Female", "Shield", "Tight", "ArmsTight01", "NoArmor")}
        _, desc, _, _, warnings = self.cooked(morphs)
        self.assertEqual(len(desc["blendShapes"]), 5)
        self.assertTrue(any("at most 4" in w for w in warnings), warnings)

    def test_a_delta_a_half_can_not_hold(self):
        with self.assertRaises(RM.CookError):
            self.cooked({"Female": [(0.0, 1e6, 0.0)] * 4})


class Attach(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.d = Path(self.tmp.name)

    def test_origin_on_the_joint_all_weight_on_it(self):
        target = target_with(at=(0.1, 1.5, -0.2))
        doc = gltf.read(write_gltf(self.d / "a.gltf", {}))
        S.attach(doc, target, 2)
        desc, data, blob, _ = build([doc], target)
        self.assertEqual(desc["skinningMap"], [2])
        m = mesh.RenderModel(guid="g", desc=desc, data=blob)
        got = mesh.decode_group(m, desc["meshes"][0])["positions"]
        # the file's (1, 1, 0) in game space is (-1, 1, 0); the joint is at (0.1, 1.5, -0.2) in game space
        self.assertAlmostEqual(got[2][0], -0.9, places=5)
        self.assertAlmostEqual(got[2][1], 2.5, places=5)
        self.assertAlmostEqual(got[2][2], -0.2, places=5)
        st = desc["skinningData"]["start"]
        self.assertEqual(blob[st:st + 8], bytes((0, 0, 0, 0, 255, 0, 0, 0)))
        self.assertTrue(validate(None, TYPES, RM.encode(TYPES, desc), blob, gltf_docs=[doc], target=target)["ok"])

    def test_bad_joint(self):
        with self.assertRaises(S.SkinError):
            S.attach(gltf.read(write_gltf(self.d / "a.gltf", {})), target_with(), 9)

    def test_joint_names_with_a_slot(self):
        target = target_with()
        doc = {"node_names": ["j2_Head", "j9_Head", "other"], "node_extras": [{}, {}, {}]}
        self.assertEqual(target.joint_of(doc, 0), 2)
        self.assertIsNone(target.joint_of(doc, 1), "past the end of the skeleton")
        self.assertIsNone(target.joint_of(doc, 2))


class FromBlocks(unittest.TestCase):
    def test_names(self):
        self.assertEqual(W.name_of("models/frost-hood.glb"), "models/frost-hood")
        self.assertEqual(W.stem_of("models/a/b.gltf"), "models_a_b")

    def test_steps_of_blocks(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            (root / "models").mkdir()
            for f in ("a.glb", "a1.glb", "f.glb", "s.gltf", "d.glb"):
                (root / "models" / f).write_bytes(b"")
            (root / "blocks").mkdir()
            (root / "blocks" / "a.json").write_text("""{ "format": 1, // comments are fine
              "wornLooks": { "l": { "model": "models/a.glb", "attach": "Head", "lods": ["models/a1.glb"], "pixelSizes": [0, 0.1], "female": "models/f.glb",
                                    "variants": { "shield": "models/s.gltf" }, "materials": { "*": "steel" } },
                             "game": { "like": "Vanity_X" }, "known": { "model": "item:Vanity_X" } },
              "cosmetics": { "c": { "name": "C", "slot": "head", "look": { "model": "models/a.glb" } },
                             "d": { "name": "D", "slot": "head", "look": { "model": "models/d.glb" } }, "by-name": { "name": "E", "slot": "head", "look": "l" } } }""")
            (root / "blocks" / "b.json").write_text('{ "format": 1, "wornLooks": { "gone": { "model": "models/gone.glb" }, "out": { "model": "../out.glb" } } }')
            (root.parent / "out.glb").write_bytes(b"")
            got = {st["model"]: st for st in W.steps_from_blocks(root)}
        self.assertEqual(sorted(got), ["models/a.glb", "models/d.glb", "models/f.glb", "models/s.gltf"], "a file not in the mod's folder is the block's mistake, not the cook's")
        self.assertEqual(got["models/a.glb"]["lods"], ["models/a1.glb"])
        self.assertNotIn("lods", got["models/f.glb"], "levels are the plain model's")
        self.assertEqual(got["models/f.glb"]["attach"], "Head")
        self.assertEqual(got["models/f.glb"]["materials"], {"*": "steel"})

    def test_a_guid_follows_from_the_path(self):
        self.assertEqual(str(uuid.uuid5(uuid.UUID("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77"), "cosmetic-sample:model:models/frost-hood")), "c965271f-57f6-5e8e-b8aa-cf274ec1426e")


class Recipe(MC.Base):
    """A whole recipe, on a game with one material and a stand-in for the player's skeleton."""

    def run_worn(self, name, steps_, looks=None, gltf_material="cloth"):
        root = Path(self.tmp.name) / name
        (root / "assets").mkdir(parents=True)
        (root / "models").mkdir()
        (root / "kiln.mod.json").write_text('{ "id": "worn-mod", "name": "x", "version": "1.0.0", "apiVersion": "0.2", "entryAssembly": "x.dll" }')
        write_gltf(root / "models" / "hat.gltf", {"Female": [(0.0, 0.02, 0.0)] * 4}, material=gltf_material)
        (root / "blocks").mkdir()
        (root / "blocks" / "m.json").write_text(json.dumps({"format": 1, "materials": {"steel": {"kind": "metal"}}, **(looks or {})}))
        p = root / "assets" / "cook.json"
        p.write_text(json.dumps({"format": 1, "tool": "modelcook", "steps": steps_}))
        with mock.patch.object(steps, "game_target", lambda k, t, g: target_with()), mock.patch.object(steps, "slot_joints", lambda k, t, g: {"Head": 2}), mock.patch.object(steps, "global_scale_of", lambda k, t, g: 1.0):
            r = recipe.run(p, self.kfc, TYPES, update_manifest=True)
        return p, r

    def test_a_step_cooks_the_same_as_a_look(self):
        by_step, r1 = self.run_worn("step", [{"op": "worn", "model": "models/hat.gltf", "attach": "Head", "materials": {"*": "steel"}}])
        by_look, r2 = self.run_worn("look", [], {"wornLooks": {"hat": {"model": "models/hat.gltf", "attach": "Head", "materials": {"*": "steel"}}}})
        self.assertTrue(r1["ok"], json.dumps(r1["steps"], indent=1)[:1500])
        self.assertTrue(r2["ok"], json.dumps(r2["steps"], indent=1)[:1500])
        self.assertEqual((by_step.parent / "cooked" / "models_hat.bin").read_bytes(), (by_look.parent / "cooked" / "models_hat.bin").read_bytes())
        self.assertEqual((by_step.parent / "cooked" / "models_hat.blob").read_bytes(), (by_look.parent / "cooked" / "models_hat.blob").read_bytes())
        side = json.loads((by_look.parent / "cooked" / "models_hat.model.json").read_text())
        self.assertEqual(side["guid"], str(uuid.uuid5(uuid.UUID("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77"), "worn-mod:model:models/hat")))
        self.assertEqual(side["attach"], "Head")
        self.assertEqual(side["morphs"], ["Female"])
        self.assertEqual(side["joints"], [2])
        man = json.loads((by_look.parent / "assets.json").read_text())
        self.assertEqual([e["guid"] for e in man["entries"] if e["kind"] == "resource"], [side["guid"]])

    def test_a_step_says_more_than_a_look(self):
        _, r = self.run_worn("both", [{"op": "worn", "model": "models/hat.gltf", "attach": "j1", "materials": {"*": "steel"}}],
                             {"wornLooks": {"hat": {"model": "models/hat.gltf", "attach": "Head"}}})
        self.assertTrue(r["ok"])
        self.assertEqual(len(r["steps"]), 1)
        self.assertEqual(r["steps"][0]["skin"]["joints"], 1)

    def test_own_materials_from_the_gltf(self):
        p, r = self.run_worn("own", [], {"wornLooks": {"hat": {"model": "models/hat.gltf", "attach": "Head"}}}, gltf_material="felt")
        self.assertTrue(r["ok"], json.dumps(r["steps"], indent=1)[:1500])
        self.assertTrue((p.parent.parent / "blocks" / "models_hat.materials.json").exists())

    def test_cook_again_when_the_look_changes(self):
        p, r = self.run_worn("again", [], {"wornLooks": {"hat": {"model": "models/hat.gltf", "attach": "Head", "materials": {"*": "steel"}}}})
        self.assertFalse(recipe.needs_cook(p))
        f = p.parent.parent / "blocks" / "m.json"
        f.write_text(f.read_text().replace('"Head"', '"j1"'))
        self.assertTrue(recipe.needs_cook(p))

    def test_a_bad_attach_says_what_there_is(self):
        with self.assertRaisesRegex(steps.StepError, "Head"):
            self.run_worn("bad", [{"op": "worn", "model": "models/hat.gltf", "attach": "Tail", "materials": {"*": "steel"}}])

    def test_looks_with_no_recipe(self):
        root = Path(self.tmp.name) / "norecipe"
        p, _ = self.run_worn("norecipe", [], {"wornLooks": {"hat": {"model": "models/hat.gltf", "attach": "Head", "materials": {"*": "steel"}}}})
        p.unlink()  # the mod has no recipe of its own: its looks make one
        path, doc = recipe.looks_recipe(root)
        self.assertEqual(path, p)
        self.assertTrue(recipe.needs_cook(path, doc), "never cooked this way")
        with mock.patch.object(steps, "game_target", lambda k, t, g: target_with()), mock.patch.object(steps, "slot_joints", lambda k, t, g: {"Head": 2}), mock.patch.object(steps, "global_scale_of", lambda k, t, g: 1.0):
            r = recipe.run(path, self.kfc, TYPES, update_manifest=True, given=doc)
        self.assertTrue(r["ok"])
        self.assertTrue((root / "assets" / "cooked" / recipe.LOOKS_REPORT).exists())
        self.assertFalse(recipe.needs_cook(path, doc))
        with_step, _ = self.run_worn("withstep", [{"op": "worn", "model": "models/hat.gltf", "attach": "Head", "materials": {"*": "steel"}}])
        self.assertEqual((root / "assets" / "cooked" / "models_hat.bin").read_bytes(), (with_step.parent / "cooked" / "models_hat.bin").read_bytes(), "the same model as a recipe's step makes")
        f = root / "blocks" / "m.json"
        f.write_text(f.read_text().replace('"Head"', '"j1"'))
        self.assertTrue(recipe.needs_cook(path, doc), "a look's change is a new cook")
        _, empty = recipe.looks_recipe(Path(self.tmp.name) / "nothing")
        self.assertEqual(recipe.own_steps(recipe.load(Path(self.tmp.name) / "nothing" / "assets" / "cook.json", empty)), [], "no blocks, no steps")

    def test_armor_looks_are_found(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            (root / "models").mkdir()
            for f in ("mail.glb", "helm.glb"):
                (root / "models" / f).write_bytes(b"")
            (root / "blocks").mkdir()
            (root / "blocks" / "a.json").write_text(json.dumps({"format": 1, "armor": {"mail": {"name": "M", "slot": "chest", "look": {"model": "models/mail.glb"}}},
                                                                "armorSets": {"s": {"head": {"model": "models/helm.glb", "attach": "Head"}, "chest": {"like": "Armor_X"}}}}))
            self.assertEqual(sorted(st["model"] for st in W.steps_from_blocks(root)), ["models/helm.glb", "models/mail.glb"])

    def test_worn_step_needs_a_file(self):
        with self.assertRaisesRegex(recipe.RecipeError, "model missing"):
            self.run_worn("nofile", [{"op": "worn"}])


if __name__ == "__main__":
    unittest.main()
