# SPDX-License-Identifier: MIT
"""Skinned models without game files: the procedural figure (samples/rigged_figure.py) is read, bound, cooked and decoded again with tools/kfc, then posed by its own cooked clips and
compared with the same pose evaluated from the glTF per its specification. The limits are checked with small broken variants of the same file.

    python3 -m unittest discover -s tools/modelcook/tests -p test_skin.py
"""
import json
import struct
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path[:0] = [str(HERE), str(HERE.parent), str(HERE.parent / "samples"), str(HERE.parent.parent / "kfc"), str(HERE.parent.parent / "animcook")]

import rigged_figure  # noqa: E402
import skindeform as D  # noqa: E402
from anicook import clips as C, skeleton as K  # noqa: E402
from anicook.gltfdoc import Doc  # noqa: E402
from cook import gltf, recipe, rendermodel as RM, skin as S, steps  # noqa: E402
from cook.validate import validate  # noqa: E402
from kfc import anim, mesh  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.blobwrite import Encoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

TYPES = TypeDb.load(HERE / "fixtures" / "types-rendermodel.json")
ANIM_TYPES = TypeDb.load(HERE.parent.parent / "animcook" / "types" / "anim-types.json")
MATERIAL = {"material": "71165a08-1a37-495c-9cb9-39cd1145ebf0", "renderEffectId": "Pbr", "materialFlags": []}
SKELETON = "aaaaaaaa-0000-4000-8000-000000000001"
BASE = {"debugName": "", "materials": [], "meshlets": [], "meshes": [], "lods": []}


def glb_parts(path):
    raw = Path(path).read_bytes()
    jl = struct.unpack_from("<I", raw, 12)[0]
    return json.loads(raw[20:20 + jl]), bytearray(raw[28 + jl:])


def write_glb(path, doc, blob):
    js = json.dumps(doc).encode()
    js += b" " * ((4 - len(js) % 4) % 4)
    blob = bytes(blob) + b"\0" * ((4 - len(blob) % 4) % 4)
    Path(path).write_bytes(struct.pack("<III", 0x46546C67, 2, 28 + len(js) + len(blob)) + struct.pack("<II", len(js), 0x4E4F534A) + js
                           + struct.pack("<II", len(blob), 0x004E4942) + blob)


def cook(paths, target=None, pixel_sizes=None, name="figure"):
    target = target or S.Target.from_skeleton(K.from_gltf(Doc(paths[0])), SKELETON)
    docs = [gltf.read(p) for p in paths]
    warnings = []
    for d in docs:
        warnings += S.bind(d, target)
    lods = []
    for d in docs:
        lod = []
        for prim in d["primitives"]:
            p = RM.prepare(prim)
            p["material_slot"] = 0
            lod.append(p)
        lods.append(lod)
    desc, blob = RM.build(lods, [MATERIAL], BASE, name, pixel_sizes, skin={"hierarchy": target.guid, "hierarchyJointCount": len(target), "globalScale": 1.0})
    return docs, desc, RM.encode(TYPES, desc), blob, target, warnings


class FigureBase(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.dir = Path(cls.tmp.name)
        cls.path = cls.dir / "figure.glb"
        cls.lod1 = cls.dir / "figure_lod1.glb"
        rigged_figure.write(cls.path)
        rigged_figure.write(cls.lod1, lod1=True)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def variant(self, edit, name="v.glb", source=None):
        doc, blob = glb_parts(source or self.path)
        edit(doc, blob)
        p = self.dir / name
        write_glb(p, doc, blob)
        return p


class WeightTests(unittest.TestCase):
    def test_quantize_sums_to_255_heaviest_first(self):
        for w in ([1.0], [0.5, 0.5], [1 / 3] * 3, [0.4, 0.3, 0.2, 0.1], [0.997, 0.001, 0.001, 0.001]):
            out = S.quantize(list(enumerate(w)))
            self.assertEqual(sum(x for _, x in out), 255)
            self.assertEqual([x for _, x in out], sorted((x for _, x in out), reverse=True))
            self.assertTrue(all(x > 0 for _, x in out))

    def test_stored_weights_come_back_exactly(self):
        for b in ([255], [128, 127], [85, 85, 85], [100, 90, 40, 25], [252, 1, 1, 1]):
            self.assertEqual([x for _, x in S.quantize([(i, v / 255) for i, v in enumerate(b)])], b)

    def test_reduce_keeps_four_and_says_what_it_lost(self):
        pairs, lost = S.reduce_influences([(1, 0.1), (2, 0.4), (3, 0.3), (4, 0.15), (5, 0.05)])
        self.assertEqual([j for j, _ in pairs], [2, 3, 4, 1])
        self.assertAlmostEqual(lost, 0.05)
        self.assertAlmostEqual(sum(w for _, w in pairs), 1.0)

    def test_reduce_merges_a_repeated_joint(self):
        pairs, lost = S.reduce_influences([(8, 0.3), (8, 0.3), (2, 0.4)])
        self.assertEqual(sorted(j for j, _ in pairs), [2, 8])
        self.assertEqual(lost, 0)

    def test_reduce_nothing(self):
        self.assertIsNone(S.reduce_influences([])[0])

    def test_stream_round_trip(self):
        data = S.encode_stream([[(10, 200), (20, 55)], [(20, 255)]], {10: 0, 20: 1})
        self.assertEqual(data, bytes([0, 1, 0, 0, 200, 55, 0, 0, 1, 0, 0, 0, 255, 0, 0, 0]))
        self.assertEqual(S.decode_stream(data), [[(0, 200), (1, 55)], [(1, 255)]])


class ReadTests(FigureBase):
    def test_skin_is_read(self):
        d = gltf.read(self.path)
        body, club = d["primitives"]
        self.assertEqual(body["skin"], 0)
        self.assertEqual(len(body["influences"]), len(body["positions"]))
        self.assertNotIn("skin", club)
        self.assertIn(d["node_names"].index("hand_r"), club["ancestors"])
        self.assertEqual(d["skins"][0]["joints"][0], d["node_names"].index("root"))

    def test_skinned_node_transform_is_ignored(self):
        """The file puts the body's node at x = 5, scaled 2: glTF ignores that for a skinned mesh."""
        d = gltf.read(self.path)
        xs = [p[0] for p in d["primitives"][0]["positions"]]
        self.assertLess(max(abs(x) for x in xs), 0.5)

    def test_two_weight_sets_are_merged(self):
        def edit(doc, blob):
            at = doc["meshes"][0]["primitives"][0]["attributes"]
            at["JOINTS_1"], at["WEIGHTS_1"] = at["JOINTS_0"], at["WEIGHTS_0"]
        d = gltf.read(self.variant(edit))
        self.assertEqual(max(len(i) for i in d["primitives"][0]["influences"]), 6)

    def test_rigid_file_is_unchanged(self):
        sys.path.insert(0, str(HERE.parent / "samples"))
        import cube_sword
        p = self.dir / "s.gltf"
        cube_sword.write(p)
        d = gltf.read(p)
        self.assertNotIn("skin", d["primitives"][0])
        self.assertEqual(d["skins"], [])


class CookTests(FigureBase):
    @classmethod
    def setUpClass(cls):
        super().setUpClass()
        cls.docs, cls.desc, cls.data, cls.blob, cls.target, cls.warnings = cook([cls.path, cls.lod1], pixel_sizes=[0.0, 0.05])
        cls.d = Decoder(TYPES).decode("keen::RenderModel", cls.data)

    def test_validates(self):
        r = validate(None, TYPES, self.data, self.blob, gltf_docs=self.docs, target=self.target)
        self.assertTrue(r["ok"], json.dumps(r, indent=1)[:1500])
        self.assertEqual(self.warnings, [])

    def test_fields(self):
        d = self.d
        self.assertEqual(d["hierarchy"], SKELETON)
        self.assertEqual(d["hierarchyJointCount"], 15)
        self.assertEqual(d["jointCount"], 14)  # the root carries no weight, so it needs no entry
        self.assertEqual(d["skinningMap"], list(range(1, 15)))
        self.assertEqual(d["globalScale"], 1.0)
        self.assertIsNone(d["cloth"])
        self.assertEqual(d["blendShapes"], [])

    def test_blob_layout_is_the_games(self):
        d = self.d
        v, sk, ix = d["vertexData"], d["skinningData"], d["indexData"]
        self.assertEqual(v["start"], 0)
        self.assertEqual(sk["start"], v["count"])
        self.assertEqual(ix["start"], sk["start"] + sk["count"])
        self.assertEqual(sk["count"] * 3, v["count"])  # 8 B per vertex against 24
        self.assertEqual(d["blendShapeData"], {"start": 0, "count": 0})
        self.assertEqual(d["meshletData"]["start"] % 4, 0)
        self.assertEqual(len(self.blob), ix["start"] + ix["count"])

    def test_skin_offsets_follow_the_groups(self):
        ms = self.d["meshes"]
        self.assertEqual([m["skinVertexOffset"] * 24 for m in ms], [m["vertexDataByteOffset"] for m in ms])

    def test_lods_share_identical_groups(self):
        ms = self.d["meshes"]
        self.assertEqual(len(ms), 4)
        self.assertEqual(ms[1]["vertexDataByteOffset"], ms[3]["vertexDataByteOffset"])  # the club is the same in both LODs
        self.assertNotEqual(ms[0]["vertexDataByteOffset"], ms[2]["vertexDataByteOffset"])

    def test_kfc_reads_it_back(self):
        m = mesh.RenderModel(guid="c", desc=self.d, data=self.blob)
        tri = mesh.triangles(m, 0)
        body, club = tri.submeshes
        hand = rigged_figure.NAMES.index("hand_r")
        self.assertEqual({j[0] for j in club["joints"]}, {hand})
        self.assertTrue(all(abs(sum(w) - 1) < 1e-6 for w in body["weights"]))
        self.assertEqual(len(body["joints"]), len(body["positions"]))
        self.assertLess(mesh.winding_check(tri), 0.05)


class DeformTests(FigureBase):
    """The cooked data, posed as the client poses it, against the glTF evaluated per its specification."""

    @classmethod
    def setUpClass(cls):
        super().setUpClass()
        cls.docs, cls.desc, cls.data, cls.blob, cls.target, _ = cook([cls.path])
        cls.d = Decoder(TYPES).decode("keen::RenderModel", cls.data)
        cls.sk = K.from_gltf(Doc(cls.path))
        value = cls.sk.value(SKELETON)
        cls.raw_h = Encoder(ANIM_TYPES).encode("keen::ModelHierarchyResource", value)
        cls.hier = Decoder(ANIM_TYPES).decode("keen::ModelHierarchyResource", cls.raw_h)

    def reference(self, animation=None, t=0.0):
        return D.mirrored(D.gltf_posed(self.path, animation, t))

    def test_bind_pose_within_a_quantum(self):
        got = D.cooked_posed(self.d, self.blob, self.hier, D.rest_pose(self.hier))
        sc = self.d["positionScale"]
        step = max(sc["x"], sc["y"], sc["z"]) / ((1 << 21) - 1)
        self.assertLess(D.max_distance(got, self.reference()), 1.5 * step)

    def test_a_pose_deforms_as_authored(self):
        doc = Doc(self.path)
        for name, cap in (("Bend", 0.002), ("Attack", 0.002), ("Walk", 0.002), ("Run", 0.004)):
            raw, info, _, _ = C.cook_clip(doc, name, self.sk)
            clip = anim.parse(raw)
            dp = anim.parse_default_pose(self.raw_h)
            length = rigged_figure.ANIMATIONS[name][1]
            for u in (0.25, 0.5, 1.0):
                f = u * clip.frame_count
                world = D.pose_of_clip(self.hier, anim.sample_frame(clip, min(f, clip.frame_count - 1 + 0.999), dp))
                got = D.cooked_posed(self.d, self.blob, self.hier, world)
                self.assertLess(D.max_distance(got, self.reference(name, u * length)), cap, f"{name} at {u}")

    def test_a_pose_really_moves_it(self):
        world = D.chain(self.hier["jointParentIndices"], [D.trs(p, q, s) for (p, q, s) in self._bend()])
        got = D.cooked_posed(self.d, self.blob, self.hier, world)
        rest = D.cooked_posed(self.d, self.blob, self.hier, D.rest_pose(self.hier))
        self.assertGreater(D.max_distance(got, rest), 0.5)

    def _bend(self):
        pose = rigged_figure._pose_bend(1.0)
        return [(self.sk.rest[i][0], (lambda q: (q[0], -q[1], -q[2], q[3]))(rigged_figure.pose_rotation(n, pose)), self.sk.rest[i][2]) for i, n in enumerate(self.sk.names)]

    def test_game_side_right_hand_is_on_plus_x(self):
        """The file's -X is the character's right, the game's +X: the mirror happened."""
        m = mesh.RenderModel(guid="c", desc=self.d, data=self.blob)
        club = mesh.triangles(m, 0).submeshes[1]["positions"]
        self.assertGreater(min(p[0] for p in club), 0.2)


class GameSkeletonTests(FigureBase):
    def game_value(self):
        sk = K.from_gltf(Doc(self.path))
        names = [f"j{i}" for i in range(len(sk))]
        return K.Skeleton(names, sk.parents, sk.rest).value(SKELETON)

    def renamed(self, source=None):
        """The figure's joints named j<index>, the way tools/kfc exports a game skeleton."""
        def edit(doc, blob):
            for i, n in enumerate(doc["nodes"]):
                if n["name"] in rigged_figure.NAMES:
                    n["name"] = "j" + str(rigged_figure.NAMES.index(n["name"]))
        return self.variant(edit, "renamed.glb", source)

    def target(self):
        v = Decoder(ANIM_TYPES).decode("keen::ModelHierarchyResource", Encoder(ANIM_TYPES).encode("keen::ModelHierarchyResource", self.game_value()))
        return S.Target.from_game(v, SKELETON)

    def test_inverse_of_its_inverse_binds(self):
        t = self.target()
        own = S.Target.from_skeleton(K.from_gltf(Doc(self.path)), SKELETON)
        for a, b in zip(t.bind, own.bind):
            self.assertLess(S._diff(a, b), 1e-5)

    def test_binds_by_index(self):
        docs, desc, data, blob, target, _ = cook([self.renamed()], target=self.target())
        self.assertEqual(Decoder(TYPES).decode("keen::RenderModel", data)["skinningMap"], list(range(1, 15)))
        self.assertTrue(validate(None, TYPES, data, blob, gltf_docs=docs, target=target)["ok"])

    def test_by_the_extras_of_a_kfc_export(self):
        def edit(doc, blob):
            for n in doc["nodes"]:
                if n["name"] in rigged_figure.NAMES:
                    n["extras"] = {"keen_joint": rigged_figure.NAMES.index(n["name"])}
                    n["name"] = "renamed_" + n["name"]
        docs, desc, data, blob, target, _ = cook([self.variant(edit, "extras.glb")], target=self.target())
        self.assertEqual(Decoder(TYPES).decode("keen::RenderModel", data)["jointCount"], 14)


    def test_a_skeleton_whose_default_pose_is_not_its_bind(self):
        """Some of the game's skeletons turn joints away in their default pose; what the mesh is bound to is skinToJointMatrices, and an export of them has to say so."""
        v = Decoder(ANIM_TYPES).decode("keen::ModelHierarchyResource", Encoder(ANIM_TYPES).encode("keen::ModelHierarchyResource", self.game_value()))
        hand, forearm = rigged_figure.NAMES.index("hand_r"), rigged_figure.NAMES.index("forearm_r")
        v["defaultPoseJoints"][forearm]["orientation"] = {"x": 1.0, "y": 0.0, "z": 0.0, "w": 0.0}
        sk = mesh.skeleton_rh(v)
        self.assertIn("matrix", sk[forearm])
        d = gltf.read(self.path)
        S.bind(d, S.Target.from_skeleton(K.from_gltf(Doc(self.path)), SKELETON))
        club = d["primitives"][1]
        tri = mesh.Tris()
        n = len(club["positions"])
        tri.submeshes.append({"material": "m", "materialIndex": 0, "positions": club["positions"], "indices": club["indices"], "joints": [(hand, 0, 0, 0)] * n, "weights": [(1.0, 0.0, 0.0, 0.0)] * n})
        p = self.dir / "turned.gltf"
        mesh.write_gltf(tri, p, skeleton=sk)
        back = gltf.read(p)
        S.bind(back, S.Target.from_game(v, SKELETON))  # raises if the export's rest pose isn't the bind pose
        self.assertEqual(back["primitives"][0]["influences"][0], [(hand, 255)])
        self.assertLess(D.max_distance([back["primitives"][0]["positions"]], [club["positions"]]), 1e-5)


class LimitTests(FigureBase):
    def target(self):
        return S.Target.from_skeleton(K.from_gltf(Doc(self.path)), SKELETON)

    def bind(self, path):
        return S.bind(gltf.read(path), self.target())

    def weights_view(self, doc, blob):
        a = doc["accessors"][doc["meshes"][0]["primitives"][0]["attributes"]["WEIGHTS_0"]]
        return doc["bufferViews"][a["bufferView"]]["byteOffset"], a["count"]

    def joints_view(self, doc):
        a = doc["accessors"][doc["meshes"][0]["primitives"][0]["attributes"]["JOINTS_0"]]
        return doc["bufferViews"][a["bufferView"]]["byteOffset"], a["count"]

    def test_unweighted_vertex(self):
        def edit(doc, blob):
            o, _ = self.weights_view(doc, blob)
            struct.pack_into("<4f", blob, o + 16 * 3, 0, 0, 0, 0)
        with self.assertRaisesRegex(S.SkinError, "vertex 3 has no weight"):
            self.bind(self.variant(edit))

    def test_joint_outside_the_skeleton(self):
        def edit(doc, blob):
            for n in doc["nodes"]:
                if n["name"] == "shin_l":
                    n["name"] = "shin_left"
        target = S.Target.from_skeleton(K.from_gltf(Doc(self.path)), SKELETON)
        with self.assertRaisesRegex(S.SkinError, "shin_left.*not in the skeleton"):
            S.bind(gltf.read(self.variant(edit)), target)

    def test_skin_index_outside_the_skin(self):
        def edit(doc, blob):
            o, _ = self.joints_view(doc)
            blob[o] = 200
        with self.assertRaisesRegex(S.SkinError, "joint 200, the skin has 15"):
            self.bind(self.variant(edit))

    def with_second_set(self, name, first, extra):
        """The first vertex with 4 influences in JOINTS_0 and more in a new JOINTS_1."""
        doc, blob = glb_parts(self.path)
        at = doc["meshes"][0]["primitives"][0]["attributes"]
        n = doc["accessors"][at["JOINTS_0"]]["count"]
        more = bytearray(4 * n) + bytearray(16 * n)
        struct.pack_into("<4B", more, 0, *[j for j, _ in extra])
        struct.pack_into("<4f", more, 4 * n, *[w for _, w in extra])
        struct.pack_into("<4f", blob, self.weights_view(doc, blob)[0], *[w for _, w in first])
        struct.pack_into("<4B", blob, self.joints_view(doc)[0], *[j for j, _ in first])
        base = len(blob)
        blob += more
        doc["bufferViews"] += [{"buffer": 0, "byteOffset": base, "byteLength": 4 * n}, {"buffer": 0, "byteOffset": base + 4 * n, "byteLength": 16 * n}]
        doc["accessors"] += [{"bufferView": len(doc["bufferViews"]) - 2, "componentType": 5121, "count": n, "type": "VEC4"},
                             {"bufferView": len(doc["bufferViews"]) - 1, "componentType": 5126, "count": n, "type": "VEC4"}]
        at["JOINTS_1"], at["WEIGHTS_1"] = len(doc["accessors"]) - 2, len(doc["accessors"]) - 1
        doc["buffers"][0]["byteLength"] = len(blob)
        p = self.dir / name
        write_glb(p, doc, blob)
        return p

    def test_too_many_influences_changes_the_shape(self):
        p = self.with_second_set("eight.glb", [(j, 0.125) for j in (1, 2, 3, 4)], [(j, 0.125) for j in (9, 10, 11, 12)])
        with self.assertRaisesRegex(S.SkinError, "more than 4 influences.*Limit Total"):
            self.bind(p)

    def test_a_small_loss_only_warns(self):
        p = self.with_second_set("five.glb", [(1, 0.45), (2, 0.3), (3, 0.1), (4, 0.1)], [(9, 0.05), (0, 0.0), (0, 0.0), (0, 0.0)])
        w = self.bind(p)
        self.assertEqual(len(w), 1)
        self.assertIn("more than 4 influences", w[0])

    def test_posed_armature(self):
        def edit(doc, blob):
            for n in doc["nodes"]:
                if n["name"] == "forearm_r":
                    n["rotation"] = rigged_figure.axis_quat("x", 40)
        with self.assertRaisesRegex(S.SkinError, "don't agree on the bind pose.*Apply the pose as rest"):
            self.bind(self.variant(edit))

    def test_rest_pose_must_match_the_skeleton(self):
        """The mesh is fine by itself, its armature isn't the skeleton's: the leg is 5 cm off."""
        target = self.target()
        def edit(doc, blob):
            for n in doc["nodes"]:
                if n["name"] == "shin_r":
                    n["translation"][1] -= 0.05
            a = doc["skins"][0]["inverseBindMatrices"]
            o = doc["bufferViews"][doc["accessors"][a]["bufferView"]]["byteOffset"]
            i = rigged_figure.NAMES.index("shin_r")
            v = list(struct.unpack_from("<16f", blob, o + 64 * i))
            v[13] += 0.05
            struct.pack_into("<16f", blob, o + 64 * i, *v)
        with self.assertRaisesRegex(S.SkinError, "1 joints are not where skeleton.*shin_r"):
            S.bind(gltf.read(self.variant(edit)), target)

    def test_rigid_mesh_with_no_joint_above(self):
        def edit(doc, blob):
            for n in doc["nodes"]:
                if n["name"] == "hand_r":
                    n["children"] = [c for c in n["children"] if doc["nodes"][c]["name"] != "Club"]
            doc["scenes"][0]["nodes"].append(next(i for i, n in enumerate(doc["nodes"]) if n["name"] == "Club"))
        with self.assertRaisesRegex(S.SkinError, "no skin and no joint above it"):
            self.bind(self.variant(edit))

    def test_lod_rules(self):
        one = [[{}]]
        with self.assertRaisesRegex(S.SkinError, "no game model has more than 8"):
            S.check_limits(one * 9, None, [MATERIAL])
        with self.assertRaisesRegex(S.SkinError, "give one per LOD"):
            S.check_limits(one * 2, [0.0], [MATERIAL])
        with self.assertRaisesRegex(S.SkinError, "LOD 0's pixel size"):
            S.check_limits(one, [0.1], [MATERIAL])
        with self.assertRaisesRegex(S.SkinError, "larger than the one before"):
            S.check_limits(one * 3, [0.0, 0.2, 0.1], [MATERIAL])
        S.check_limits(one * 8, None, [MATERIAL])

    def test_material_rules(self):
        with self.assertRaisesRegex(S.SkinError, "no material"):
            S.check_limits([[{}]], None, [])
        with self.assertRaisesRegex(S.SkinError, "13 materials"):
            S.check_limits([[{}]], None, [MATERIAL] * 13)

    def test_more_joints_than_the_stream_addresses(self):
        p = RM.prepare({"positions": [(0.0, 0.0, 0.0)] * 300, "indices": [0, 1, 2], "normals": None, "tangents": None, "uvs": None, "colors": None,
                        "influences": [[(j, 255)] for j in range(300)]})
        p["material_slot"] = 0
        with self.assertRaisesRegex(RM.CookError, "300 joints carry weight"):
            RM.build([[p]], [MATERIAL], BASE, "x", skin={"hierarchy": SKELETON, "hierarchyJointCount": 300})

    def test_skinned_file_without_a_skeleton(self):
        k = _NoGame()
        with self.assertRaisesRegex(steps.StepError, "name its skeleton"):
            steps.model(k, TYPES, [str(self.path)], None, guid="bbbbbbbb-0000-4000-8000-000000000001")


class _NoGame:
    path = "none"

    def has(self, key):
        return False

    def by_guid(self, guid):
        return []


class RecipeTests(unittest.TestCase):
    """Skinned steps through cook.json against a synthetic game: a made-up material in a synthetic KFC4, the figure from the sample."""

    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(HERE.parent.parent / "kfc" / "tests"))
        from test_kfc import build_kfc
        from kfc.archive import Kfc
        cls.tmp = tempfile.TemporaryDirectory()
        root = Path(cls.tmp.name)
        game = root / "game"
        game.mkdir()
        cls.material = "11111111-0000-4000-8000-000000000001"
        raw = Encoder(TYPES).encode("keen::RenderMaterialResource", {"effectId": "Pbr", "flags": [], "debugName": "m"})
        build_kfc(game, [(cls.material, TYPES.get("keen::RenderMaterialResource")["hash"], 0, raw)], [b"unused"], chunk_size=4096)
        k = Kfc(game)
        kb = bytearray((game / "test.kfc").read_bytes())
        vo = struct.unpack_from("<I", kb, 0x80)[0] + 0x80
        for i, (h, _) in enumerate(sorted(k.bundles.items())):
            struct.pack_into("<I", kb, vo + 12 * i, TYPES.get(h)["layout"])
        (game / "test.kfc").write_bytes(bytes(kb))
        k.close()
        cls.kfc = Kfc(game)
        cls.game = game

    @classmethod
    def tearDownClass(cls):
        cls.kfc.close()
        cls.tmp.cleanup()

    def mod(self, name, steps_, tool="modelcook"):
        mod = Path(self.tmp.name) / name
        a = mod / "assets"
        (a / "src").mkdir(parents=True)
        (mod / "kiln.mod.json").write_text(json.dumps({"id": "fig-mod"}))
        rigged_figure.write(a / "src" / "figure.glb")
        rigged_figure.write(a / "src" / "lod1.glb", lod1=True)
        p = a / "cook.json"
        p.write_text(json.dumps({"format": 1, "tool": tool, "inputs": {"mat": {"guid": self.material, "type": "keen::RenderMaterialResource"}}, "steps": steps_}))
        return p

    SKEL = {"tool": "animcook", "op": "skeleton", "name": "figure", "gltf": "src/figure.glb"}

    def test_model_on_a_skeleton_step(self):
        p = self.mod("a", [self.SKEL, {"op": "model", "gltf": "src/figure.glb", "lods": ["src/lod1.glb"], "pixelSizes": [0, 0.05], "skeleton": "figure", "material": "@mat", "name": "figure", "stem": "figure"}])
        r = recipe.run(p, self.kfc, TYPES, update_manifest=True)
        self.assertTrue(r["ok"], json.dumps(r["steps"], indent=1)[:1500])
        d = Decoder(TYPES).decode("keen::RenderModel", (p.parent / "cooked/figure.bin").read_bytes())
        side = json.loads((p.parent / "cooked/figure.model.json").read_text())
        self.assertEqual(side["guid"], r["steps"][0]["guid"])
        self.assertEqual(d["hierarchy"], side["skeleton"])
        self.assertEqual(side["joints"], d["skinningMap"])
        man = json.loads((p.parent / "assets.json").read_text())
        self.assertNotIn("cooked/figure.model.json", {e["file"] for e in man["entries"]})
        self.assertEqual(set(r["sources"]), {"src/figure.glb", "src/lod1.glb"})

    def test_it_does_not_matter_which_tool_runs_first(self):
        """modelcook runs its own steps before the animation steps: the model has to name the skeleton they will write."""
        p = self.mod("b", [self.SKEL, {"op": "model", "gltf": "src/figure.glb", "skeleton": "figure", "material": "@mat", "name": "figure", "stem": "figure"}])
        recipe.run(p, self.kfc, TYPES, update_manifest=True)
        self.assertFalse((p.parent / "cooked/figure.skeleton.json").exists())
        d = Decoder(TYPES).decode("keen::RenderModel", (p.parent / "cooked/figure.bin").read_bytes())
        from anicook import recipe as anim_recipe
        anim_recipe.Run(p, ANIM_TYPES).run(update_manifest=True)
        sidecar = K.load_sidecar(p.parent / "cooked/figure.skeleton.json")
        self.assertEqual(d["hierarchy"], sidecar.guid)
        self.assertEqual(d["hierarchyJointCount"], len(sidecar))
        self.assertEqual(sidecar.names[d["skinningMap"][0]], "pelvis")

    def test_new_sources_mean_a_new_cook(self):
        p = self.mod("c", [self.SKEL, {"op": "model", "gltf": "src/figure.glb", "skeleton": "figure", "material": "@mat", "stem": "f"}])
        self.assertTrue(recipe.needs_cook(p))
        recipe.run(p, self.kfc, TYPES, update_manifest=True)
        self.assertFalse(recipe.needs_cook(p))
        rigged_figure.write(p.parent / "src" / "figure.glb", lod1=True)
        self.assertTrue(recipe.needs_cook(p))

    def test_guid_follows_the_names(self):
        p = self.mod("d", [self.SKEL, {"op": "model", "gltf": "src/figure.glb", "skeleton": "figure", "material": "@mat", "name": "body", "stem": "f"}])
        r = recipe.run(p, self.kfc, TYPES, update_manifest=True)
        from anicook.graphgen import derive
        self.assertEqual(r["steps"][0]["guid"], derive("fig-mod", "model", "body"))
        self.assertEqual(r["steps"][0]["mode"], "new")

    def test_bad_skeleton_name(self):
        p = self.mod("e", [{"op": "model", "gltf": "src/figure.glb", "skeleton": "nope", "material": "@mat", "stem": "f"}])
        with self.assertRaisesRegex(recipe.RecipeError, "no skeleton step of that name"):
            recipe.run(p, self.kfc, TYPES)

    def test_needs_a_material_source(self):
        p = self.mod("f", [{"op": "model", "gltf": "src/figure.glb", "skeleton": "@s"}])
        with self.assertRaisesRegex(recipe.RecipeError, "base, material or materialMap"):
            recipe.load(p)


if __name__ == "__main__":
    unittest.main()
