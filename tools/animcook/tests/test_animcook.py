# SPDX-License-Identifier: MIT
"""animcook without game files: the sample glTF cooked with the bundled reflection, parsed back by tools/kfc, compared with what it was authored
to do. `python3 -m unittest discover -s tools/animcook/tests -v`. Nothing is written outside a temp folder."""
import json
import math
import struct
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path[:0] = [str(ROOT), str(ROOT / "samples"), str(ROOT.parent / "kfc"), str(ROOT.parent / "modelcook")]

import make_golem  # noqa: E402
from anicook import clips as C, graphgen as G, recipe as R, skeleton as K  # noqa: E402
from anicook.gltfdoc import Channel, Doc, matrix_to_trs, qmul, qnorm  # noqa: E402
from kfc import anim, animgraph  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

TYPES = TypeDb.load(ROOT / "types" / "anim-types.json")
GRAPH = "keen::anim_graph::runtime_graph::AnimationGraphResource2_0"


def gltf_angle_deg(q, rest_q):
    """The extra rotation about local z from a rest quaternion, in glTF's sense, for a game-space q (the mirror flips the sign)."""
    rel = qmul((-rest_q[0], -rest_q[1], -rest_q[2], rest_q[3]), tuple(q))
    return -2 * math.degrees(math.atan2(rel[2], rel[3]))


class Base(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.dir = Path(cls.tmp.name)
        cls.glb = cls.dir / "golem.glb"
        make_golem.write(cls.glb)
        cls.gltf = cls.dir / "golem.gltf"
        make_golem.write(cls.gltf, as_gltf=True)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()


class Skeleton(Base):
    def test_glb_and_gltf_give_one_skeleton(self):
        a, b = K.from_gltf(self.glb), K.from_gltf(self.gltf)
        self.assertEqual(a.names, ["root", "pelvis", "spine", "head", "arm_r", "hand_r", "arm_l", "hand_l", "leg_r"])
        self.assertEqual(a.parents[0], 0xFFFF)
        self.assertEqual(a.compare(b), [])
        self.assertEqual(a.hierarchy_hash, b.hierarchy_hash)

    def test_game_space_is_mirrored_x(self):
        sk = K.from_gltf(self.glb)
        t, q, _ = sk.rest[sk.index["arm_r"]]
        self.assertAlmostEqual(t[0], -0.25)
        self.assertAlmostEqual(t[1], 0.35)
        self.assertAlmostEqual(q[2], -math.sin(math.radians(10)), places=5)  # +20 deg about z in glTF, -20 in the game

    def test_value_and_inverse_binds(self):
        sk = K.from_gltf(self.glb)
        g = "00000000-0000-0000-0000-00000000000a"
        v = sk.value(g)
        self.assertEqual(v["jointCount"], 9)
        self.assertEqual(v["defaultPose"]["alignedJointCount"], 12)
        self.assertEqual(len(v["defaultPose"]["data"]), 36)
        # each skinToJoint is the inverse of the chained rest pose: joint origin maps to zero
        glob = {}
        for i, p in enumerate(sk.parents):
            t, q, s = sk.rest[i]
            loc = animgraph._affine(t, q, s)
            glob[i] = loc if p == 0xFFFF else animgraph._mul_affine(glob[p], loc)
            m = v["skinToJointMatrices"][i]
            r = m["rot"]
            o = [glob[i][k][3] for k in range(3)]
            for k, axis in enumerate("xyz"):
                val = r["x"][axis] * o[0] + r["y"][axis] * o[1] + r["z"][axis] * o[2] + m["pos"][axis]
                self.assertAlmostEqual(val, 0.0, places=5)
        raw = R.Encoder(TYPES).encode(TYPES.get(R.HIER), v)
        dp = anim.parse_default_pose(raw)
        self.assertAlmostEqual(dp[1][4][0], -0.25, places=6)
        self.assertAlmostEqual(dp[0][4][3], sk.rest[4][1][3], places=6)

    def test_armature_node_is_baked(self):
        doc, blob = make_golem.build()
        doc["nodes"][1]["translation"] = [1.0, 2.0, 3.0]
        p = self.dir / "moved.glb"
        js = json.dumps(doc).encode()
        js += b" " * (-len(js) % 4)
        blob += b"\0" * (-len(blob) % 4)
        p.write_bytes(struct.pack("<III", 0x46546C67, 2, 28 + len(js) + len(blob)) + struct.pack("<II", len(js), 0x4E4F534A) + js + struct.pack("<II", len(blob), 0x004E4942) + blob)
        sk = K.from_gltf(p)
        self.assertEqual(sk.rest[0][0], (-1.0, 2.0, 3.0))

    def test_joint_of_node_maps_skin_joints(self):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "golem.glb"
            make_golem.write(path)
            sk = K.from_gltf(path)
            gltf = Doc(path)
            for i, node in enumerate(sk.nodes):
                self.assertEqual(sk.joint_of_node(node), i)
                self.assertEqual(gltf.names[node], sk.names[i])
            self.assertIsNone(sk.joint_of_node(10_000))

    def test_sidecar_round_trip(self):
        sk = K.from_gltf(self.glb)
        back = K.Skeleton.from_json(json.loads(json.dumps(sk.to_json("00000000-0000-0000-0000-00000000000a"))))
        self.assertEqual(back.compare(sk), [])
        self.assertEqual(back.hierarchy_hash, sk.hierarchy_hash)

    def test_refusals(self):
        with self.assertRaises(ValueError):
            animgraph.skeleton_value("00000000-0000-0000-0000-00000000000a", ["a"] * 2, [0xFFFF, 0], [(0, 0, 0)] * 2)
        with self.assertRaises(ValueError):
            animgraph.skeleton_value("00000000-0000-0000-0000-00000000000a", [str(i) for i in range(256)], [0xFFFF] * 256, [(0, 0, 0)] * 256)
        a = K.from_gltf(self.glb)
        b = K.Skeleton(a.names[:-1] + ["other"], a.parents, a.rest)
        self.assertTrue(a.compare(b))
        c = K.Skeleton(a.names, a.parents, [((9.0, 9.0, 9.0), r[1], r[2]) for r in a.rest])
        self.assertTrue(any("rest position" in x for x in a.compare(c)))

    def test_matrix_decomposition(self):
        q = make_golem.euler(0, 0, 20)
        t, r, s = matrix_to_trs(make_golem._trs_matrix((1, 2, 3), q))
        self.assertEqual(t, (1.0, 2.0, 3.0))
        for a, b in zip(r, q):
            self.assertAlmostEqual(a, b, places=6)
        self.assertAlmostEqual(s[0], 1.0)


class Channels(unittest.TestCase):
    def test_interpolations(self):
        lin = Channel("translation", [0.0, 1.0], [(0.0, 0.0, 0.0), (2.0, 0.0, 0.0)], "LINEAR")
        self.assertAlmostEqual(lin.eval(0.25)[0], 0.5)
        step = Channel("translation", [0.0, 1.0], [(0.0, 0.0, 0.0), (2.0, 0.0, 0.0)], "STEP")
        self.assertEqual(step.eval(0.99)[0], 0.0)
        self.assertEqual(step.eval(1.0)[0], 2.0)
        cub = Channel("translation", [0.0, 2.0], [(0.0,) * 3, (0.0,) * 3, (1.0, 0.0, 0.0), (0.0,) * 3, (4.0, 0.0, 0.0), (0.0,) * 3], "CUBICSPLINE")
        self.assertAlmostEqual(cub.eval(1.0)[0], 2.0 + 1.0 * 2 * 0.125, places=6)  # hermite midpoint: p0/2 + p1/2 + d(m0-m1)/8
        q0, q1 = make_golem.euler(0, 0, 0), make_golem.euler(0, 0, 90)
        rot = Channel("rotation", [0.0, 1.0], [tuple(q0), tuple(q1)], "LINEAR")
        q = rot.eval(0.5)
        self.assertAlmostEqual(2 * math.degrees(math.atan2(q[2], q[3])), 45.0, places=4)


class Clips(Base):
    def cook(self, name, **kw):
        doc = Doc(self.glb)
        sk = K.from_gltf(doc)
        raw, info, rm, lv = C.cook_clip(doc, name, sk, **kw)
        return sk, anim.parse(raw), raw, info, rm, lv

    def arm(self, sk, clip, f, joint="arm_r"):
        dp = sk.default_pose()
        pose = anim.sample_frame(clip, float(f), dp)
        return gltf_angle_deg(pose[0][sk.index[joint]], sk.rest[sk.index[joint]][1])

    def test_idle_follows_the_authored_sine(self):
        sk, clip, raw, info, _, _ = self.cook("Idle", loop=True)
        self.assertEqual(anim.build(clip), raw)
        self.assertEqual((clip.frame_count, clip.flags, clip.fps), (120, 1, 60))
        self.assertEqual(info["duration"], 2.0)
        self.assertEqual(len(clip.segments), 2)
        for f in range(0, 120, 7):
            self.assertAlmostEqual(self.arm(sk, clip, f), 30 * math.sin(2 * math.pi * f / 120), delta=0.2, msg=f"frame {f}")
        self.assertLess(info["errors"]["rotationDeg"], 0.2)
        self.assertEqual(clip.hierarchy_hash, sk.hierarchy_hash)

    def test_format_rules(self):
        sk, clip, *_ = self.cook("WalkF", loop=True)
        for si, s in enumerate(clip.segments):
            self.assertEqual(s.start, si * 63)
            self.assertEqual(s.end, s.start + 63)
            for ch in (s.orientation, s.position, s.scale):
                joints = [t.joint for t in ch.animated] + [j for j, _ in ch.constant] + list(ch.default)
                self.assertEqual(sorted(joints), list(range(9)), "each joint in exactly one list")
                for t in ch.animated:
                    self.assertTrue(t.mask & 1)
                    if si < len(clip.segments) - 1:
                        self.assertTrue(t.mask >> 63 & 1)
                    else:
                        self.assertEqual(s.start + max(i for i in range(64) if t.mask >> i & 1), clip.frame_count)

    def test_loop_seam_warns(self):
        *_, info, _, _ = self.cook("Slam", loop=True)
        self.assertEqual(info["warnings"], [])
        *_, info, _, _ = self.cook("Fall", loop=True)
        self.assertTrue(info["warnings"], "the Fall animation does not end where it starts")

    def test_step_and_cubic(self):
        sk, clip, *_ = self.cook("Fall")
        self.assertAlmostEqual(self.arm(sk, clip, 15), 0.0, delta=0.2)
        self.assertAlmostEqual(self.arm(sk, clip, 40), 45.0, delta=0.2)
        self.assertAlmostEqual(self.arm(sk, clip, 58), 45.0, delta=0.5)
        sk, clip, *_ = self.cook("Slam")
        self.assertAlmostEqual(self.arm(sk, clip, 45), -90.0, delta=0.2)
        # halfway along the first hermite segment with zero tangents is the midpoint, in quaternion space
        self.assertAlmostEqual(self.arm(sk, clip, 22.5), -45.0, delta=1.0)

    def test_root_motion(self):
        sk, clip, raw, info, rm, lv = self.cook("Step", root_motion={})
        self.assertEqual(rm["frameCount"], 120)
        self.assertEqual(len(rm["positions"]), 121)
        self.assertAlmostEqual(rm["positions"][-1]["z"] if isinstance(rm["positions"][-1], dict) else 0, 2.0, places=3)
        self.assertAlmostEqual(rm["startToEnd"]["z"], -2.0, places=3)
        pose = anim.sample_frame(clip, 100.0, sk.default_pose())
        self.assertLess(abs(pose[1][0][2]), 0.002, "the root stays: the motion moved to the entity")
        self.assertEqual(lv["frameCount"], 120)
        self.assertEqual(lv["trackName"], 0xFA31A0F1)
        self.assertEqual(len(lv["positionKeys"]), 121)
        for tn, v in (("keen::RootMotionAnimation", rm), ("keen::LveAnimation", lv)):
            t = TYPES.get(tn)
            b = R.Encoder(TYPES).encode(t, v)
            self.assertEqual(R.Encoder(TYPES).encode(t, Decoder(TYPES).decode(t, b)), b)

    def test_bad_input(self):
        with self.assertRaises(C.ClipError):
            self.cook("NoSuch")
        doc = Doc(self.glb)
        sk = K.from_gltf(doc)
        small = K.Skeleton([n for n in sk.names if n != "arm_r" and n != "hand_r"], [0xFFFF, 0, 1, 2, 2, 5, 1], [sk.rest[i] for i, n in enumerate(sk.names) if n not in ("arm_r", "hand_r")])
        with self.assertRaises(C.ClipError):
            C.cook_clip(doc, "Idle", small)

    def game_shaped(self):
        """The golem as a game skeleton plays it: joints j<index> with no glTF node, found in the file by the `keen_joint` extra (what
        `kiln skeleton player` and make_clips.py write), the node names left as they are so only the extra can pair them."""
        own = K.from_gltf(Doc(self.glb))
        d = json.loads(self.gltf.read_text())
        for j, node in enumerate(own.nodes):
            d["nodes"][node].setdefault("extras", {})["keen_joint"] = j
        path = self.dir / "golem_game.gltf"
        path.write_text(json.dumps(d))
        game = K.Skeleton([f"j{i}" for i in range(len(own))], own.parents, own.rest, source="game")
        return own, game, Doc(path)

    def test_game_skeleton_pose_matches_own_skeleton(self):
        """A clip cooked onto a game skeleton is the same clip as on the file's own skeleton: every joint's local transform, every frame. The
        animations leave some joints alone (Idle turns arm_r only): those stay at their rest pose, they are not pulled up to the model-space
        transform of the joints above them (the body torn into spikes in the client, dev.26)."""
        own, game, doc = self.game_shaped()
        for name in ("Idle", "WalkF", "Slam"):
            a = anim.parse(C.cook_clip(Doc(self.glb), name, own)[0])
            b = anim.parse(C.cook_clip(doc, name, game)[0])
            moved = {own.joint_of_node(n) for n in doc.animations()[name]}
            for f in range(0, a.frame_count, 9):
                pa, pb = anim.sample_frame(a, float(f), own.default_pose()), anim.sample_frame(b, float(f), game.default_pose())
                for j in range(len(own)):
                    where = f"{name} frame {f} joint {own.names[j]}"
                    self.assertLess(max(abs(x - y) for x, y in zip(pa[1][j], pb[1][j])), 0.001, where)
                    self.assertLess(C.qdeg(pa[0][j], pb[0][j]), 0.1, where)
                    if j not in moved:
                        self.assertLess(max(abs(x - y) for x, y in zip(pb[1][j], game.rest[j][0])), 0.001, f"{where}: not animated, off its rest")
                        self.assertLess(C.qdeg(pb[0][j], game.rest[j][1]), 0.1, f"{where}: not animated, off its rest")

    def test_game_skeleton_file_must_have_its_hierarchy(self):
        """A node is a joint's local transform only under its parent joint's node: a file that moved a joint elsewhere is refused."""
        own, game, doc = self.game_shaped()
        d = json.loads(doc.path.read_text())
        spine, hand, arm = own.nodes[own.index["spine"]], own.nodes[own.index["hand_r"]], own.nodes[own.index["arm_r"]]
        d["nodes"][arm]["children"].remove(hand)
        d["nodes"][spine]["children"].append(hand)
        doc.path.write_text(json.dumps(d))
        with self.assertRaisesRegex(C.ClipError, "the skeleton has it under j"):
            C.cook_clip(Doc(doc.path), "Idle", game)

    def test_events(self):
        dec = Decoder(TYPES)
        v = C.event_sequence_value(TYPES, dec, "anim_x", [{"at": 0.5, "noise": 8}, {"at": 0.1, "foot": "left"}, {"at": 0.2, "sound": "00000000-0000-0000-0000-000000000001", "slot": "Head"},
                                                          {"at": 0.3, "vfx": "00000000-0000-0000-0000-000000000002", "offset": [0, 1, 0]}], 1.0)
        t = TYPES.get("keen::SequenceResource")
        b = R.Encoder(TYPES).encode(t, v)
        back = dec.decode(t, b)
        self.assertEqual([e["$type"].split("::")[-1] for e in back["events"]], ["LeftFootPlantingEvent", "SfxNotifierEvent", "VfxNotifierEvent", "TriggerNoiseEvent"])
        self.assertEqual(back["events"][0]["value"]["time"]["value"], 100000000)
        self.assertEqual(back["events"][2]["value"]["notifierOffset"]["orientationOffset"]["w"], 1.0)
        self.assertEqual(back["length"]["value"], 1000000000)


class Graphs(Base):
    def clips(self, names, dur=1.0, **kw):
        return {n: dict({"guid": G.derive("m", "clip", n), "duration": dur, "loop": True, "rootMotion": False, "events": False}, **kw) for n in names}

    def compile(self, states, extra=None, clips=None):
        sk = K.from_gltf(self.glb)
        sk.guid = "00000000-0000-0000-0000-00000000000a"
        spec = dict({"states": states}, **(extra or {}))
        c = G.Compiler("m", "g", spec, sk, clips if clips is not None else self.clips(["a", "b", "c", "d", "e"]))
        v, info = c.compile()
        G.check_fields(TYPES, v)
        t = TYPES.get(GRAPH)
        raw = R.Encoder(TYPES).encode(t, v)
        self.assertEqual(R.Encoder(TYPES).encode(t, Decoder(TYPES, max_array=1 << 20).decode(t, raw)), raw)
        return v, info, c

    def node(self, v, kind):
        return [n["value"] for n in v["nodeDefinitions"] if n["value"]["type"] == kind]

    def test_flat_states(self):
        v, info, _ = self.compile({"idle": {"engine": "Idle", "clip": "a"}, "slam": {"clip": "b", "blendIn": 0.1}}, {"slots": {"HandR": "hand_r"}})
        self.assertEqual(info["states"]["idle"]["id"], "0x1490bed7")
        self.assertEqual(info["states"]["idle"]["guid"], "2e701760-3fb1-4e08-b14a-4e012cac15d0")
        g = info["states"]["slam"]["guid"]
        self.assertEqual(g, G.derive("m", "state", "slam"))
        self.assertEqual(int(info["states"]["slam"]["id"], 16), animgraph.fnv_guid(g))
        self.assertEqual(v["slotBoneIndexMapping"][8], 5)
        sm = self.node(v, "StateMachine")
        self.assertEqual(len(sm), 1)
        self.assertEqual(sm[0]["numGlobalTransitions"], 2)
        self.assertEqual(len(sm[0]["globalTransitions"]), 34)
        pos = {n["value"]["id"]["value"]: i for i, n in enumerate(v["nodeDefinitions"])}
        for n in self.node(v, "PoseResult"):
            self.assertLess(pos[n["input"]["value"]], pos[n["id"]["value"]])
        self.assertEqual(v["nodeDefinitions"][-1]["value"]["id"]["value"], v["rootNode"]["value"])

    def test_derive_matches_blockids(self):
        # the C# rule (Kiln.Blocks BlockIds.Derive): UUIDv5 of "<mod>:<kind>:<name>" under this namespace
        import uuid
        self.assertEqual(G.derive("m", "state", "slam"), str(uuid.uuid5(uuid.UUID("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77"), "m:state:slam")))

    def test_more_than_34_states_nest(self):
        states = {f"s{i}": {"clip": "a"} for i in range(40)}
        states["idle"] = {"engine": "Idle", "clip": "b"}
        v, info, _ = self.compile(states, {"entry": "idle"})
        self.assertTrue(info["nested"])
        sm = self.node(v, "StateMachine")
        self.assertEqual(len(sm), 3)
        for m in sm:
            self.assertLessEqual(m["numGlobalTransitions"], 34)
        outer = sm[-1]
        self.assertEqual(outer["debugName"], "g")
        lists = self.node(v, "IdParameterEqualsConstantList")
        self.assertEqual(len(lists), 2)
        all_ids = sorted(x["value"] for l in lists for x in l["values"])
        self.assertEqual(all_ids, sorted(int(s["id"], 16) for s in info["states"].values()))
        ids = {n["value"]["id"]["value"]: n["value"] for n in v["nodeDefinitions"]}
        self.assertTrue(any(ids[outer["entryState"]["value"]]["poseResult"] for _ in [0]))

    def test_chains_are_their_own_machine(self):
        v, info, _ = self.compile({"a": {"clip": "a", "loop": False, "then": "b"}, "b": {"clip": "b", "loop": False, "then": "c"}, "c": {"clip": "c"}, "d": {"clip": "d"}})
        sm = self.node(v, "StateMachine")
        self.assertEqual([m["debugName"] for m in sm], ["a", "g"])
        inner, outer = sm
        self.assertEqual(outer["numGlobalTransitions"], 2)
        self.assertEqual(inner["numGlobalTransitions"], 2, "the first state of a chain has no global transition: the input stays on its id")
        trs = self.node(v, "StateMachineTransition")
        self.assertEqual(len(trs), 2)
        self.assertTrue(all(t["autoTransition"] for t in trs))
        lst = self.node(v, "IdParameterEqualsConstantList")[0]
        self.assertEqual(len(lst["values"]), 3, "any of the chain's ids enters it")
        self.assertEqual(sorted(x["value"] for x in lst["values"]), sorted(int(info["states"][k]["id"], 16) for k in "abc"))

    def test_chains_count_once_when_nesting(self):
        states = {f"s{i}": {"clip": "a"} for i in range(33)}
        states["c1"] = {"clip": "a", "loop": False, "then": "c2"}
        states["c2"] = {"clip": "b"}
        v, info, _ = self.compile(states)
        self.assertFalse(info["nested"], "33 states + one chain are 34 items")
        states["one_more"] = {"clip": "a"}
        v, info, _ = self.compile(states)
        self.assertTrue(info["nested"])

    def test_two_ways_into_a_state_refused(self):
        with self.assertRaises(G.GraphError):
            self.compile({"a": {"clip": "a", "then": "c"}, "b": {"clip": "a", "then": "c"}, "c": {"clip": "b"}})

    def test_blend(self):
        v, info, _ = self.compile({"walk": {"engine": "Walk", "blend": {"input": "moveDirection", "clips": {"90": "a", "-90": "b", "0": "c"}}}})
        b = self.node(v, "BlendSpace_1D")[0]
        self.assertEqual([i["parameterValue"] for i in b["items"]], [-90.0, 0.0, 90.0])
        self.assertEqual(b["parameterHash"]["value"], 0x28BB2FF6)
        self.assertEqual(info["floatInputs"], ["0x28bb2ff6"])
        v, info, _ = self.compile({"s": {"blend": {"input": "mine", "clips": {"0": "a", "1": "b"}}}})
        self.assertEqual(int(info["floatInputs"][0], 16), animgraph.fnv_guid(G.derive("m", "input", "mine")))

    def test_layer_with_overlay_and_mask(self):
        v, info, c = self.compile({"i": {"clip": "a"}}, {"layers": [{"name": "f", "overlay": "hit", "clip": "b", "reference": "c", "weight": 0.5, "bones": ["arm_r"]}]})
        lb = self.node(v, "LayerBlend")[0]
        self.assertEqual(lb["blendOptions"]["blendMode"], "Additive")
        self.assertEqual(lb["blendOptions"]["layerWeight"], 0.5)
        self.assertEqual(lb["blendOptions"]["boneMask"], c.masks[0][1])
        self.assertEqual(c.masks[0][2]["entries"], [{"boneIndex": 4, "weight": 1.0}])
        self.assertEqual(self.node(v, "IsOverlayActive")[0]["overlay"]["value"], 0xC6B6F9D4)
        self.assertEqual(self.node(v, "SampleAnimationClip")[1]["additiveType"], "Additive_LocalSpace")
        self.assertEqual(self.node(v, "SampleAnimationClip")[1]["additiveReferenceAnimation"], G.derive("m", "clip", "c"))
        self.assertEqual(len(self.node(v, "StateMachineTransition")), 5)

    def test_override_layer(self):
        v, *_ = self.compile({"i": {"clip": "a"}}, {"layers": [{"name": "up", "mode": "override", "clip": "b", "bones": {"spine": 0.5}}]})
        self.assertEqual(self.node(v, "LayerBlend")[0]["blendOptions"]["blendMode"], "Override")

    def test_refusals(self):
        for states, extra, word in [({}, None, "no states"), ({"i": {"clip": "zz"}}, None, "no clip"), ({"i": {}}, None, "needs clip or blend"),
                                    ({"i": {"clip": "a", "blend": {}}}, None, "not both"), ({"i": {"engine": "Nope", "clip": "a"}}, None, "engine state"),
                                    ({"a": {"engine": "Idle", "clip": "a"}, "b": {"id": "0x1490bed7", "clip": "a"}}, None, "same state id"),
                                    ({"i": {"clip": "a"}}, {"entry": "x"}, "entry"), ({"i": {"clip": "a", "then": "x"}}, None, "then"),
                                    ({"i": {"clip": "a"}}, {"slots": {"Nope": "root"}}, "slot"), ({"i": {"clip": "a"}}, {"slots": {"Head": "nope"}}, "joint"),
                                    ({"i": {"clip": "a"}}, {"layers": [{"name": "x", "clip": "b"}]}, "reference"), ({"i": {"clip": "a"}}, {"layers": [{"name": "x", "mode": "override", "clip": "b", "bones": ["no"]}]}, "joint")]:
            with self.subTest(word=word):
                with self.assertRaises(G.GraphError) as e:
                    self.compile(states, extra)
                self.assertIn(word, str(e.exception))
        many = {f"s{i}": {"blend": {"input": f"in{i}", "clips": {"0": "a", "1": "b"}}} for i in range(5)}
        with self.assertRaises(G.GraphError) as e:
            self.compile(many)
        self.assertIn("4 are left", str(e.exception))
        self.compile({f"s{i}": {"blend": {"input": f"in{i}", "clips": {"0": "a", "1": "b"}}} for i in range(4)})
        self.compile({"s": {"blend": {"input": "moveDirection", "clips": {"0": "a", "1": "b"}}}})


class RecipeRun(Base):
    RECIPE = {"format": 1, "tool": "animcook", "mod": "m", "steps": [
        {"op": "skeleton", "name": "g", "gltf": "golem.glb"},
        {"op": "clips", "skeleton": "g", "gltf": "golem.glb", "clips": {"idle": {"animation": "Idle", "loop": True}, "slam": {"animation": "Slam", "events": [{"at": 0.2, "noise": 5}]},
                                                                       "step": {"animation": "Step", "rootMotion": True}}},
        {"op": "graph", "name": "g", "skeleton": "g", "slots": {"HandR": "hand_r"},
         "states": {"idle": {"engine": "Idle", "clip": "idle"}, "slam": {"clip": "slam"}, "step": {"clip": "step"}}}]}

    def setUp(self):
        self.mod = Path(tempfile.mkdtemp(dir=self.dir)) / "assets"  # a recipe is in its mod's assets folder: the cook writes to assets/cooked only
        self.mod.mkdir()
        (self.mod / "golem.glb").write_bytes(self.glb.read_bytes())
        self.r = self.mod / "cook.json"
        self.r.write_text(json.dumps(self.RECIPE))

    def test_out_is_the_mods_cooked_folder(self):
        """A cook writes into the mod's assets/cooked folder only: another "out" is refused with what to change, and nothing is written."""
        for out in ("built", "../cooked", ".", "cooked/../src"):
            self.r.write_text(json.dumps(dict(self.RECIPE, out=out)))
            before = sorted(str(f) for f in self.mod.parent.rglob("*"))
            R.load(self.r)  # the reader takes the recipe's shape; the cook refuses where it would write
            with self.assertRaises(R.RecipeError, msg=out) as e:
                R.Run(self.r, TYPES)
            self.assertIn("outside the mod's assets/cooked folder", str(e.exception))
            self.assertIn('"out": "cooked"', str(e.exception))
            with self.assertRaises(R.RecipeError):
                R.needs_cook(self.r)
            self.assertEqual(sorted(str(f) for f in self.mod.parent.rglob("*")), before)
        # a step's name that would lead out of the folder: refused before anything is written
        rec = json.loads(json.dumps(self.RECIPE))
        rec["steps"][0]["name"] = "../../escaped"
        rec["steps"][1]["skeleton"] = rec["steps"][2]["skeleton"] = "../../escaped"
        self.r.write_text(json.dumps(rec))
        before = sorted(str(f) for f in self.mod.parent.rglob("*"))
        with self.assertRaises(R.RecipeError) as e:
            R.Run(self.r, TYPES).run(update_manifest=True)
        self.assertIn("outside the mod's assets/cooked folder", str(e.exception))
        self.assertEqual(sorted(str(f) for f in self.mod.parent.rglob("*")), before)
        self.r.write_text(json.dumps(dict(self.RECIPE, out="cooked/anim")))
        self.assertTrue(R.Run(self.r, TYPES).run(update_manifest=True)["ok"])
        self.assertTrue((self.mod / "cooked/anim/cook-report.json").exists())
        self.assertEqual(sorted(f.name for f in self.mod.iterdir()), ["assets.json", "cook.json", "cooked", "golem.glb"])
        # the report is a file of the cook like its outputs: a link in its place is refused before anything is written, and the file it
        # points to is left alone
        victim = self.mod.parent / "victim.json"
        victim.write_text("theirs")
        report = self.mod / "cooked/anim/cook-report.json"
        report.unlink()
        report.symlink_to(victim)
        before = sorted(str(f) for f in self.mod.parent.rglob("*"))
        with self.assertRaises(R.RecipeError) as e:
            R.Run(self.r, TYPES).run(update_manifest=True)
        self.assertIn("is a link", str(e.exception))
        self.assertEqual((victim.read_text(), sorted(str(f) for f in self.mod.parent.rglob("*"))), ("theirs", before))

    def test_run_and_check(self):
        self.assertTrue(R.needs_cook(self.r))
        rep = R.Run(self.r, TYPES).run(update_manifest=True)
        self.assertTrue(rep["ok"], rep)
        self.assertFalse(R.needs_cook(self.r))
        man = json.loads((self.mod / "assets.json").read_text())
        kinds = sorted(e["type"].split("::")[-1] for e in man["entries"])
        self.assertEqual(kinds, sorted(["ModelHierarchyResource"] + ["JointAnimation"] * 3 + ["RootMotionAnimation", "LveAnimation", "SequenceResource", "AnimationSequenceContainer", "AnimationGraphResource2_0"]))
        for e in man["entries"]:
            self.assertTrue((self.mod / e["file"]).exists())
        # sibling resources sit under the clip's own GUID
        clip_guid = next(e["guid"] for e in man["entries"] if e["file"] == "cooked/clip_step.bin")
        self.assertEqual(sum(1 for e in man["entries"] if e["guid"] == clip_guid), 3)
        # the sidecars
        sk = K.load_sidecar(self.mod / "cooked/g.skeleton.json")
        self.assertEqual(sk.guid, G.derive("m", "skeleton", "g"))
        side = json.loads((self.mod / "cooked/g.graph.json").read_text())
        self.assertEqual(side["states"]["slam"]["guid"], G.derive("m", "state", "slam"))
        clips = json.loads((self.mod / "cooked/clips.json").read_text())
        self.assertEqual(clips["clips"]["slam"]["guid"], G.derive("m", "clip", "slam"))
        # a changed source means a cook
        with open(self.mod / "golem.glb", "ab") as f:
            f.write(b"\0" * 4)
        self.assertTrue(R.needs_cook(self.r))

    def test_clips_must_fit_the_skeleton(self):
        doc, blob = make_golem.build()
        doc["nodes"][2 + 4]["translation"] = [5.0, 0.0, 0.0]  # arm_r moved
        p = self.mod / "other.gltf"
        import base64
        doc["buffers"][0]["uri"] = "data:application/octet-stream;base64," + base64.b64encode(blob).decode()
        p.write_text(json.dumps(doc))
        rec = json.loads(json.dumps(self.RECIPE))
        rec["steps"][1]["gltf"] = "other.gltf"
        self.r.write_text(json.dumps(rec))
        with self.assertRaises(R.RecipeError) as e:
            R.Run(self.r, TYPES).run()
        self.assertIn("does not fit", str(e.exception))

    def test_loader_refuses(self):
        bad = [dict(self.RECIPE, format=2), {"format": 1, "tool": "animcook", "steps": [{"op": "nope"}]},
               {"format": 1, "tool": "animcook", "steps": [{"op": "skeleton", "name": "g"}]},
               {"format": 1, "tool": "animcook", "steps": [{"op": "skeleton", "name": "g", "gltf": "x", "typo": 1}]},
               {"format": 1, "tool": "animcook", "steps": [{"op": "clips", "skeleton": "g", "gltf": "x", "clips": {"a": {"typo": 1}}}]}]
        for doc in bad:
            self.r.write_text(json.dumps(doc))
            with self.assertRaises(R.RecipeError):
                R.load(self.r)

    def test_cli(self):
        cli = [sys.executable, str(ROOT / "animcook.py"), "--no-game"]
        out = subprocess.run(cli + ["recipe", str(self.r), "--check"], capture_output=True, text=True)
        self.assertEqual(out.returncode, 1)
        out = subprocess.run(cli + ["recipe", str(self.r), "--update-manifest"], capture_output=True, text=True)
        self.assertEqual(out.returncode, 0, out.stderr + out.stdout)
        self.assertIn("graph g", out.stdout)
        out = subprocess.run(cli + ["recipe", str(self.r), "--check"], capture_output=True, text=True)
        self.assertEqual(out.returncode, 0)
        out = subprocess.run(cli + ["clip", str(self.mod / "cooked/clip_idle.bin")], capture_output=True, text=True)
        self.assertIn("rebuilds byte-identical: True", out.stdout)
        out = subprocess.run(cli + ["info", str(self.mod / "golem.glb")], capture_output=True, text=True)
        self.assertIn("9 joints", out.stdout)


if __name__ == "__main__":
    unittest.main()
