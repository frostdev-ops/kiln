# SPDX-License-Identifier: MIT
"""Retargeting without game files: the sample golem's animations played on rigs that differ from it in joint names, rest orientations, proportions and
joint count, checked against what the motion must be. `python3 -m unittest tools/animcook/tests/test_retarget.py -v`."""
import base64
import json
import math
import random
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path[:0] = [str(ROOT), str(ROOT / "samples"), str(ROOT.parent / "kfc"), str(ROOT.parent / "modelcook")]

import make_golem  # noqa: E402
from anicook import clips as C, recipe as R, retarget as RT, skeleton as K  # noqa: E402
from anicook.gltfdoc import Doc, qinv, qmul, qnorm, qrot  # noqa: E402
from kfc import anim  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

TYPES = TypeDb.load(ROOT / "types" / "anim-types.json")


def qangle(a, b):
    d = abs(sum(x * y for x, y in zip(qnorm(a), qnorm(b))))
    return math.degrees(2 * math.acos(min(1.0, d)))


def rand_quat(rng, max_deg):
    ax = [rng.uniform(-1, 1) for _ in range(3)]
    m = math.sqrt(sum(c * c for c in ax)) or 1.0
    h = math.radians(rng.uniform(-max_deg, max_deg)) / 2
    return tuple([c / m * math.sin(h) for c in ax] + [math.cos(h)])


def foreign(sk, prefix="T_", twist_deg=0.0, scale=1.0, seed=1, drop=(), extra=()):
    """Another rig in the same posture as `sk`: joints renamed, each joint's rest frame turned by a random twist (positions kept, so bones keep their
    places), the whole scaled, some joints left out (their children go to the nearest kept parent) and extra joints added. -> (Skeleton, twists)."""
    rng = random.Random(seed)
    ms = RT.model_space(sk, RT.rest_frame(sk))
    keep = [j for j in range(len(sk)) if sk.names[j] not in drop]
    new_index = {j: i for i, j in enumerate(keep)}
    names, parents, rest, twists = [], [], [], []
    glob = []
    for j in keep:
        p = sk.parents[j]
        while p != 0xFFFF and p not in new_index:
            p = sk.parents[p]
        tw = rand_quat(rng, twist_deg) if twist_deg else (0.0, 0.0, 0.0, 1.0)
        gq = qnorm(qmul(ms[j][1], tw))
        gp = tuple(c * scale for c in ms[j][0])
        if p == 0xFFFF:
            lt, lq, np_ = gp, gq, 0xFFFF
        else:
            pp, pq = glob[new_index[p]]
            lt = qrot(qinv(pq), tuple(gp[i] - pp[i] for i in range(3)))
            lq = qnorm(qmul(qinv(pq), gq))
            np_ = new_index[p]
        names.append(prefix + sk.names[j])
        parents.append(np_)
        rest.append((tuple(lt), tuple(lq), (1.0, 1.0, 1.0)))
        twists.append(tw)
        glob.append((gp, gq))
    for name, parent in extra:
        pi = names.index(prefix + parent)
        names.append(prefix + name)
        parents.append(pi)
        rest.append(((0.0, 0.1, 0.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0)))
        twists.append((0.0, 0.0, 0.0, 1.0))
    return K.Skeleton(names, parents, rest, source="test"), twists


class Base(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.dir = Path(cls.tmp.name)
        cls.glb = cls.dir / "golem.glb"
        make_golem.write(cls.glb)
        cls.doc = Doc(cls.glb)
        cls.src = K.from_gltf(cls.doc)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def name_map(self, dst, prefix="T_"):
        return {n: prefix + n for n in self.src.names if prefix + n in dst.index}


class Plan(Base):
    def test_twisted_rig_moves_its_joints_where_the_source_does(self):
        dst, tw = foreign(self.src, twist_deg=120.0)
        plan = RT.Plan(self.src, dst, {"map": self.name_map(dst)})
        self.assertEqual([dst.names[j] for j in plan.translate], ["T_root", "T_pelvis"])  # the root is on the ground: the hips too
        self.assertAlmostEqual(plan.scale, 1.0)
        for anim_name in ("Idle", "WalkF", "Slam", "Step"):
            frames, _ = C.sample_animation(self.doc, anim_name, self.src)
            worst_p = worst_q = 0.0
            for fr in frames:
                a = RT.model_space(self.src, fr)
                b = RT.model_space(dst, plan.frame(fr))
                for j in range(len(dst)):
                    worst_p = max(worst_p, max(abs(x - y) for x, y in zip(a[j][0], b[j][0])))
                    worst_q = max(worst_q, qangle(qmul(a[j][1], tw[j]), b[j][1]))
            self.assertLess(worst_p, 1e-5, anim_name)
            self.assertLess(worst_q, 1e-3, anim_name)

    def test_proportions_scale_the_hips_travel(self):
        dst, _ = foreign(self.src, scale=1.5, twist_deg=40.0, seed=3)
        plan = RT.Plan(self.src, dst, {"map": self.name_map(dst)})
        self.assertAlmostEqual(plan.scale, 1.5, places=5)
        frames, _ = C.sample_animation(self.doc, "Step", self.src)
        end_src = RT.model_space(self.src, frames[-1])[0][0]
        end_dst = RT.model_space(dst, plan.frame(frames[-1]))[0][0]
        self.assertAlmostEqual(end_src[2], 2.0, places=4)
        self.assertAlmostEqual(end_dst[2], 3.0, places=4)
        # joints keep the target's bone lengths: the hand is 1.5 x as far from the arm
        b = RT.model_space(dst, plan.frame(frames[10]))
        arm, hand = dst.index["T_arm_r"], dst.index["T_hand_r"]
        self.assertAlmostEqual(math.dist(b[arm][0], b[hand][0]), 0.9, places=5)

    def test_explicit_scale_and_translate(self):
        dst, _ = foreign(self.src)
        plan = RT.Plan(self.src, dst, {"map": self.name_map(dst), "scale": 0.5, "translate": ["root"]})
        frames, _ = C.sample_animation(self.doc, "Step", self.src)
        self.assertAlmostEqual(RT.model_space(dst, plan.frame(frames[-1]))[0][0][2], 1.0, places=4)
        self.assertEqual(len(RT.Plan(self.src, dst, {"map": self.name_map(dst), "translate": "all"}).translate), len(dst))
        plan = RT.Plan(self.src, dst, {"map": self.name_map(dst), "translate": []})
        self.assertAlmostEqual(RT.model_space(dst, plan.frame(frames[-1]))[0][0][2], 0.0, places=6)

    def test_unpaired_joints_hold_their_rest_on_the_parent(self):
        dst, _ = foreign(self.src, drop=("hand_l",), extra=(("tail", "pelvis"),))
        plan = RT.Plan(self.src, dst, {"map": self.name_map(dst)})
        rep = plan.report()
        self.assertEqual(rep["unpaired"], ["T_tail"])
        frames, _ = C.sample_animation(self.doc, "WalkF", self.src)
        tail = dst.index["T_tail"]
        for fr in frames[::7]:
            out = plan.frame(fr)
            self.assertLess(qangle(out[0][tail], dst.rest[tail][1]), 1e-6)
            self.assertEqual(tuple(out[1][tail]), dst.rest[tail][0])

    def test_same_names_pair_by_themselves(self):
        dst, _ = foreign(self.src, prefix="")
        plan = RT.Plan(self.src, dst, {})
        self.assertEqual(len(plan.pairs), len(dst))
        plan = RT.Plan(self.src, dst, {"byName": False, "map": {"arm_r": "arm_r"}})
        self.assertEqual(len(plan.pairs), 1)

    def test_reference_pose(self):
        """A reference pose that is the rest pose turned changes the result by exactly that turn."""
        dst, _ = foreign(self.src)
        frames, _ = C.sample_animation(self.doc, "Idle", self.src)
        ref = C.sample_animation(self.doc, "Ref", self.src)[0][0]
        a = RT.Plan(self.src, dst, {"map": self.name_map(dst)})
        b = RT.Plan(self.src, dst, {"map": self.name_map(dst), "referenceFrame": ref})
        arm = dst.index["T_arm_r"]
        self.assertLess(qangle(a.frame(frames[5])[0][arm], b.frame(frames[5])[0][arm]), 1e-3)

    def test_refusals(self):
        dst, _ = foreign(self.src)
        for cfg, msg in [({"map": {"nope": "T_root"}}, "not a joint of the clips' rig"),
                         ({"map": {"root": "nope"}}, "not a joint of the target"),
                         ({"map": {"root": "T_root", "pelvis": "T_root"}}, "paired twice"),
                         ({"map": "all"}, "retarget.map"),
                         ({"map": self.name_map(dst), "translate": ["nope"]}, "retarget.translate"),
                         ({"map": self.name_map(dst), "scale": -1}, "retarget.scale")]:
            with self.assertRaises(RT.RetargetError) as e:
                RT.Plan(self.src, dst, cfg)
            self.assertIn(msg, str(e.exception))
        with self.assertRaises(RT.RetargetError) as e:
            RT.Plan(self.src, dst, {"byName": False})
        self.assertIn("no joint", str(e.exception))


class Recipe(Base):
    """A rig of other names and 1.5 times the size, cooked onto the golem's own skeleton through a recipe."""

    def setUp(self):
        self.mod = Path(tempfile.mkdtemp(dir=self.dir))
        (self.mod / "golem.glb").write_bytes(self.glb.read_bytes())
        doc, blob = make_golem.build()
        for n in doc["nodes"]:
            if n["name"] in self.src.index:
                n["name"] = "mixamorig:" + n["name"]
                if "translation" in n:
                    n["translation"] = [c * 1.5 for c in n["translation"]]
        # the Step animation's root translation, 1.5 x as far
        acc = doc["accessors"]
        for a in doc["animations"]:
            if a["name"] != "Step":
                continue
            for ch in a["channels"]:
                if ch["target"]["path"] == "translation":
                    out = acc[a["samplers"][ch["sampler"]]["output"]]
                    view = doc["bufferViews"][out["bufferView"]]
                    import struct
                    vals = struct.unpack_from(f"<{out['count'] * 3}f", blob, view["byteOffset"])
                    blob = bytearray(blob)
                    struct.pack_into(f"<{len(vals)}f", blob, view["byteOffset"], *[v * 1.5 for v in vals])
                    blob = bytes(blob)
        doc["buffers"][0]["uri"] = "data:application/octet-stream;base64," + base64.b64encode(blob).decode()
        (self.mod / "mixamo.gltf").write_text(json.dumps(doc))
        self.r = self.mod / "cook.json"

    def recipe(self, retarget):
        return {"format": 1, "tool": "animcook", "mod": "m", "steps": [
            {"op": "skeleton", "name": "g", "gltf": "golem.glb"},
            {"op": "clips", "skeleton": "g", "gltf": "mixamo.gltf", "retarget": retarget,
             "clips": {"idle": {"animation": "Idle", "loop": True}, "step": {"animation": "Step"}}},
            {"op": "clips", "skeleton": "g", "gltf": "golem.glb", "clips": {"idle_direct": {"animation": "Idle", "loop": True}}}]}

    def test_cooks_onto_the_skeleton(self):
        self.r.write_text(json.dumps(self.recipe({"map": {"mixamorig:" + n: n for n in self.src.names}})))
        rep = R.Run(self.r, TYPES).run(update_manifest=True)
        self.assertTrue(rep["ok"], rep)
        st = rep["steps"][1]
        self.assertEqual(st["retarget"]["paired"], len(self.src))
        self.assertAlmostEqual(st["retarget"]["scale"], 1 / 1.5, places=5)
        dp = self.src.default_pose()
        a = anim.parse((self.mod / "cooked/clip_idle.bin").read_bytes())
        b = anim.parse((self.mod / "cooked/clip_idle_direct.bin").read_bytes())
        self.assertEqual(a.hierarchy_hash, self.src.hierarchy_hash)
        self.assertEqual(a.frame_count, b.frame_count)
        worst = 0.0
        for f in range(0, a.frame_count, 5):
            ra, _, _ = anim.sample_frame(a, f, dp)
            rb, _, _ = anim.sample_frame(b, f, dp)
            worst = max(worst, max(qangle(x, y) for x, y in zip(ra, rb)))
        self.assertLess(worst, 0.2)
        step = anim.parse((self.mod / "cooked/clip_step.bin").read_bytes())
        _, pos, _ = anim.sample_frame(step, step.frame_count - 1, dp)
        self.assertAlmostEqual(pos[0][2], 2.0, delta=0.02)  # the last stored frame, one 60th of a second before the end
        # the rig's own (bigger) bones are not taken: the arm's position is the golem's
        self.assertAlmostEqual(pos[self.src.index["arm_r"]][0], self.src.rest[self.src.index["arm_r"]][0][0], places=3)

    def test_without_a_map_nothing_pairs(self):
        self.r.write_text(json.dumps(self.recipe(True)))
        with self.assertRaises(R.RecipeError) as e:
            R.Run(self.r, TYPES).run()
        self.assertIn("no joint of the clips' rig pairs", str(e.exception))

    def test_without_retarget_the_rig_does_not_fit(self):
        rec = self.recipe({})
        del rec["steps"][1]["retarget"]
        self.r.write_text(json.dumps(rec))
        with self.assertRaises(R.RecipeError) as e:
            R.Run(self.r, TYPES).run()
        self.assertIn("does not fit", str(e.exception))


if __name__ == "__main__":
    unittest.main()
