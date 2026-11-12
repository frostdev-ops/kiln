# SPDX-License-Identifier: MIT
"""Clips reworked from other clips (`"from"`, anicook/clipops.py) without game files: the sample golem's clips and a small asymmetric rig built
here, each operation run through the encoder and compared with what it must give, in model space where it matters.
`python3 -m unittest discover -s tools/animcook/tests -v`. Nothing is written outside a temp folder."""
import json
import math
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path[:0] = [str(ROOT), str(ROOT / "samples"), str(ROOT.parent / "kfc"), str(ROOT.parent / "modelcook")]

import make_golem  # noqa: E402
from anicook import clipops as CO, clips as C, graphgen as G, recipe as R, retarget as RT, skeleton as K  # noqa: E402
from anicook.gltfdoc import Doc, qinv, qmul, qnorm, qrot  # noqa: E402
from kfc import anim  # noqa: E402
from kfc.archive import ResourceKey  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

TYPES = TypeDb.load(ROOT / "types" / "anim-types.json")
ROT_TOL = 0.3     # degrees: the codec's default tolerance, decoded twice, stays well inside this
POS_TOL = 0.001   # metres
SLOTS = [v["name"] for v in TYPES.get("keen::AttachmentSlot")["values"]]


def qangle(a, b):
    d = abs(sum(x * y for x, y in zip(qnorm(a), qnorm(b))))
    return math.degrees(2 * math.acos(min(1.0, d)))


def axis_q(axis, deg):
    h = math.radians(deg) / 2
    q = [0.0, 0.0, 0.0, math.cos(h)]
    q[axis] = math.sin(h)
    return tuple(q)


def mirror_v(v):
    return (-v[0], v[1], v[2])


def mirror_q(q):
    return (q[0], -q[1], -q[2], q[3])


def worst(sk, a, b):
    """Largest model-space rotation (degrees) and position (metres) difference between two lists of frames."""
    wq = wp = 0.0
    for fa, fb in zip(a, b):
        ma, mb = RT.model_space(sk, fa), RT.model_space(sk, fb)
        for j in range(len(sk)):
            wq = max(wq, qangle(ma[j][1], mb[j][1]))
            wp = max(wp, max(abs(x - y) for x, y in zip(ma[j][0], mb[j][0])))
    return wq, wp


def frames(src):
    return [src.pose(f) for f in range(src.n + 1)]


def cooked(sk, res, name="t", loop=False):
    """A Result through the encoder and back: what the game would play."""
    raw, info, _, _ = C.finish_clip(res.frames, res.n, sk, name, loop=loop)
    return CO.Source(name, anim.parse(raw), sk, rm=res.rm, sequences=res.sequences)


def source_of(sk, frames_, name, rm=None, sequences=()):
    raw, *_ = C.finish_clip(frames_, len(frames_) - 1, sk, name)
    return CO.Source(name, anim.parse(raw), sk, rm=rm, sequences=sequences)


def asym_rig():
    """A rig whose left and right are not mirror images in their rest frames: the right arm's frames are turned 90 degrees about the bone,
    the left hand has a finger the right has not, and the right hand a prop the left has not. Built in model space, game axes."""
    R_l = axis_q(2, 25.0)
    R_r = qnorm(qmul(mirror_q(R_l), axis_q(0, 90.0)))  # mirror image, then a quarter turn about the bone (local x)
    joints = [  # name, parent, model position, model rotation
        ("root", None, (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0)),
        ("spine", "root", (0.0, 1.0, 0.0), axis_q(0, 10.0)),
        ("arm_l", "spine", (-0.2, 1.4, 0.05), R_l),
        ("hand_l", "arm_l", (-0.7, 1.2, 0.05), R_l),
        ("finger_l", "hand_l", (-0.8, 1.15, 0.05), R_l),
        ("arm_r", "spine", (0.2, 1.4, 0.05), R_r),
        ("hand_r", "arm_r", (0.7, 1.2, 0.05), R_r),
        ("prop_r", "hand_r", (0.75, 1.1, 0.3), R_r),
        ("head", "spine", (0.0, 1.7, 0.0), axis_q(1, 30.0)),
    ]
    names = [j[0] for j in joints]
    parents, rest = [], []
    glob = {}
    for n, p, pos, q in joints:
        glob[n] = (pos, q)
        if p is None:
            parents.append(0xFFFF)
            rest.append((pos, q, (1.0, 1.0, 1.0)))
        else:
            pp, pq = glob[p]
            parents.append(names.index(p))
            rest.append((qrot(qinv(pq), tuple(a - b for a, b in zip(pos, pp))), qnorm(qmul(qinv(pq), q)), (1.0, 1.0, 1.0)))
    return K.Skeleton(names, parents, rest, source="test")


def asym_clip(sk, n=90):
    """The left arm swings, the spine turns a little, the root walks sideways: (frames, root motion path)."""
    out = []
    for f in range(n + 1):
        u = f / n
        rots = [list(r[1]) for r in sk.rest]
        poss = [list(r[0]) for r in sk.rest]
        scls = [list(r[2]) for r in sk.rest]
        i = sk.index["arm_l"]
        rots[i] = list(qnorm(qmul(tuple(rots[i]), axis_q(2, 50 * math.sin(2 * math.pi * u)))))
        i = sk.index["spine"]
        rots[i] = list(qnorm(qmul(tuple(rots[i]), axis_q(1, 15 * u))))
        i = sk.index["hand_l"]
        rots[i] = list(qnorm(qmul(tuple(rots[i]), axis_q(1, 30 * u))))
        poss[0] = [0.1 * u, 0.0, 0.0]
        out.append((rots, poss, scls))
    path = ([(0.3 * f / n, 0.0, 1.0 * f / n) for f in range(n + 1)], [axis_q(1, 20.0 * f / n) for f in range(n + 1)])
    return out, path


class Golem(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.glb = Path(cls.tmp.name) / "golem.glb"
        make_golem.write(cls.glb)
        cls.doc = Doc(cls.glb)
        cls.sk = K.from_gltf(cls.doc)
        cls.src = {}
        for nm in ("Idle", "WalkF", "Slam", "Step", "Run"):
            raw, info, rm, lv = C.cook_clip(cls.doc, nm, cls.sk, root_motion={} if nm == "Step" else None)
            cls.src[nm] = CO.Source(nm, anim.parse(raw), cls.sk, rm=CO.path_of(rm) if rm else None)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def rework(self, src, **spec):
        spec["from"] = src.label
        return CO.rework("t", spec, src, self.sk, lambda r: self.src[r], slots=SLOTS)

    def test_mirror_twice_is_the_source(self):
        for nm in ("WalkF", "Idle", "Slam"):
            src = self.src[nm]
            once = cooked(self.sk, self.rework(src, mirror=True))
            twice = cooked(self.sk, CO.rework("t", {"from": "x", "mirror": True}, once, self.sk, None))
            wq, wp = worst(self.sk, frames(src), frames(twice))
            self.assertLess(wq, ROT_TOL, nm)
            self.assertLess(wp, POS_TOL, nm)

    def test_mirror_moves_the_right_arm_to_the_left(self):
        src = self.src["Idle"]  # only arm_r moves
        out = cooked(self.sk, self.rework(src, mirror=True))
        a, b = frames(src), frames(out)
        rest = RT.model_space(self.sk, RT.rest_frame(self.sk))
        moved = 0.0
        for fa, fb in zip(a, b):
            ma, mb = RT.model_space(self.sk, fa), RT.model_space(self.sk, fb)
            for r, l in (("arm_r", "arm_l"), ("hand_r", "hand_l")):
                i, j = self.sk.index[r], self.sk.index[l]
                self.assertLess(max(abs(x - y) for x, y in zip(mb[j][0], mirror_v(ma[i][0]))), POS_TOL)
                self.assertLess(qangle(mb[j][1], mirror_q(ma[i][1])), ROT_TOL)
                self.assertLess(max(abs(x - y) for x, y in zip(mb[i][0], rest[i][0])), POS_TOL, "the right arm rests now")
            moved = max(moved, max(abs(x - y) for x, y in zip(mb[self.sk.index["hand_l"]][0], rest[self.sk.index["hand_l"]][0])))
        self.assertGreater(moved, 0.1, "the left hand swings")

    def test_mirror_pairs_on_the_sample(self):
        m = CO.Mirror(self.sk)
        rep = m.report()
        self.assertEqual(rep["pairs"], [["arm_r", "arm_l"], ["hand_r", "hand_l"]])
        self.assertEqual(rep["unplaced"], ["leg_r"])
        self.assertEqual(rep["centre"], 4)

    def test_reverse(self):
        src = self.src["WalkF"]
        once = cooked(self.sk, self.rework(src, reverse=True))
        self.assertEqual(once.n, src.n)
        a = frames(src)
        b = frames(once)
        wq, wp = worst(self.sk, b, list(reversed(a)))
        self.assertLess(wq, ROT_TOL)
        self.assertLess(wp, POS_TOL)
        twice = cooked(self.sk, CO.rework("t", {"from": "x", "reverse": True}, once, self.sk, None))
        wq, wp = worst(self.sk, a, frames(twice))
        self.assertLess(wq, ROT_TOL)
        self.assertGreater(worst(self.sk, a, b)[0], 10.0, "reversed once is not the source")

    def test_speed(self):
        src = self.src["Slam"]  # 1.5 s
        for s in (2.0, 0.5):
            out = cooked(self.sk, self.rework(src, speed=s))
            self.assertAlmostEqual(out.duration, src.duration / s, places=6)
            for f in range(0, out.n + 1, 5):
                t = f / 60
                wq, wp = worst(self.sk, [out.pose(f)], [src.pose(t * s * 60)])
                self.assertLess(wq, ROT_TOL, f"speed {s} at {t:.3f} s")

    def test_trim(self):
        src = self.src["Idle"]  # 2 s
        out = cooked(self.sk, self.rework(src, trim=[0.25, 1.5]))
        self.assertAlmostEqual(out.duration, 1.25, places=6)
        for f_out, t_src in ((0, 0.25), (out.n, 1.5), (30, 0.75)):
            wq, _ = worst(self.sk, [out.pose(f_out)], [src.pose(t_src * 60)])
            self.assertLess(wq, ROT_TOL, f"trim at {t_src} s")
        self.assertFalse(cooked(self.sk, self.rework(src, trim=[0.25, 1.5])).loop, "a trimmed loop plays once unless told")

    def test_blend_by_mask(self):
        walk, idle = self.src["WalkF"], self.src["Idle"]  # walk swings both arms over 1 s, idle the right arm over 2 s
        out = cooked(self.sk, self.rework(walk, blend={"with": "Idle", "bones": ["arm_r"]}))
        dp = self.sk.default_pose()
        r, l = self.sk.index["arm_r"], self.sk.index["arm_l"]
        for f in range(0, out.n + 1, 4):
            o = out.pose(f)
            self.assertLess(qangle(o[0][r], idle.pose(f / out.n * idle.n)[0][r]), ROT_TOL, "arm_r from the idle, stretched to the walk's length")
            self.assertLess(qangle(o[0][l], walk.pose(f)[0][l]), ROT_TOL, "arm_l from the walk")
        # exactly the named joints: hand_r keeps the walk's (its rest) unless children are asked for
        h = self.sk.index["hand_r"]
        self.assertEqual(CO.mask_weights(self.sk, {"bones": ["arm_r"]}, "x")[h], 0.0)
        self.assertEqual(CO.mask_weights(self.sk, {"bones": ["arm_r"], "children": True}, "x")[h], 1.0)
        self.assertEqual(CO.mask_weights(self.sk, {"bones": {"arm_r": 0.25}, "children": True}, "x")[h], 0.25)
        self.assertTrue(dp)

    def test_blend_by_weight(self):
        idle, run = self.src["Idle"], self.src["Run"]
        both = cooked(self.sk, self.rework(idle, blend={"with": "Run", "weight": 0.0}))
        self.assertLess(worst(self.sk, frames(idle), frames(both))[0], ROT_TOL)
        one = cooked(self.sk, self.rework(idle, blend={"with": "Run", "weight": 1.0}))
        other = [run.pose(f / idle.n * run.n) for f in range(idle.n + 1)]
        self.assertLess(worst(self.sk, other, frames(one))[0], ROT_TOL)
        half = cooked(self.sk, self.rework(idle, blend={"with": "Run", "weight": 0.5}))
        h = self.sk.index["hand_r"]
        far = 0.0
        for f in range(0, idle.n + 1, 3):
            ma = RT.model_space(self.sk, idle.pose(f))[h]
            mb = RT.model_space(self.sk, other[f])[h]
            mh = RT.model_space(self.sk, half.pose(f))[h]
            total = qangle(ma[1], mb[1])
            far = max(far, total)
            self.assertAlmostEqual(qangle(ma[1], mh[1]), total / 2, delta=ROT_TOL)
            self.assertAlmostEqual(qangle(mb[1], mh[1]), total / 2, delta=ROT_TOL)
        self.assertGreater(far, 20.0)
        quarter = cooked(self.sk, self.rework(idle, blend={"with": "Run", "weight": 0.25}))
        for f in range(0, idle.n + 1, 3):
            ma = RT.model_space(self.sk, idle.pose(f))[h]
            mb = RT.model_space(self.sk, other[f])[h]
            mq = RT.model_space(self.sk, quarter.pose(f))[h]
            self.assertAlmostEqual(qangle(ma[1], mq[1]), qangle(ma[1], mb[1]) / 4, delta=ROT_TOL, msg="a quarter of the way to the other clip")

    def test_offset(self):
        src = self.src["Idle"]
        out = cooked(self.sk, self.rework(src, offset={"head": {"rotate": [0, 0, 30]}, "hand_l": {"translate": [0.0, 0.1, 0.0]}}))
        hd, hl = self.sk.index["head"], self.sk.index["hand_l"]
        for f in range(0, src.n + 1, 10):
            a, b = src.pose(f), out.pose(f)
            self.assertAlmostEqual(qangle(a[0][hd], b[0][hd]), 30.0, delta=ROT_TOL)
            # about z in the parent's glTF axes: the game's space flips the sign of z turns
            turn = qnorm(qmul(tuple(b[0][hd]), qinv(tuple(a[0][hd]))))
            self.assertAlmostEqual(abs(turn[2]), math.sin(math.radians(15)), places=3)
            self.assertAlmostEqual(b[1][hl][1] - a[1][hl][1], 0.1, places=3)
            for j in range(len(self.sk)):
                if j not in (hd, hl):
                    self.assertLess(qangle(a[0][j], b[0][j]), ROT_TOL)
                    self.assertLess(max(abs(x - y) for x, y in zip(a[1][j], b[1][j])), POS_TOL)

    def test_operations_run_in_their_order(self):
        src = self.src["Slam"]
        a = self.rework(src, speed=2.0, trim=[0.0, 0.75])
        b = CO.rework("t", {"from": "x", "speed": 2.0}, cooked(self.sk, CO.rework("t", {"from": "x", "trim": [0.0, 0.75]}, src, self.sk, None)), self.sk, None)
        self.assertEqual(a.n, b.n, "trim first, then speed: 0.75 s at speed 2")
        self.assertEqual(a.n, round(0.75 * 60 / 2))
        self.assertEqual(a.report["ops"], ["trim", "speed"])


class Asymmetric(unittest.TestCase):
    def setUp(self):
        self.sk = asym_rig()
        fr, self.path = asym_clip(self.sk)
        self.src = source_of(self.sk, fr, "asym", rm=self.path)

    def test_pairs_by_position(self):
        rep = CO.Mirror(self.sk).report()
        self.assertEqual(rep["pairs"], [["arm_l", "arm_r"], ["hand_l", "hand_r"]])
        self.assertEqual(sorted(rep["unplaced"]), ["finger_l", "prop_r"])
        self.assertEqual(rep["centre"], 3)

    def test_mirror_once_in_model_space(self):
        out = cooked(self.sk, CO.rework("t", {"from": "asym", "mirror": True}, self.src, self.sk, None))
        rest = RT.model_space(self.sk, RT.rest_frame(self.sk))
        ix = self.sk.index
        swing = 0.0
        for f in range(0, self.src.n + 1, 3):
            a = RT.model_space(self.sk, self.src.pose(f))
            b = RT.model_space(self.sk, out.pose(f))
            for s, d in (("arm_l", "arm_r"), ("hand_l", "hand_r"), ("arm_r", "arm_l"), ("spine", "spine"), ("head", "head"), ("root", "root")):
                i, j = ix[s], ix[d]
                self.assertLess(max(abs(x - y) for x, y in zip(b[j][0], mirror_v(a[i][0]))), POS_TOL, f"{s} -> {d} at {f}")
                # the turn away from rest, reflected, on the other side's own rest frame
                ta = qmul(a[i][1], qinv(rest[i][1]))
                tb = qmul(b[j][1], qinv(rest[j][1]))
                self.assertLess(qangle(tb, mirror_q(ta)), ROT_TOL, f"{s} -> {d} at {f}")
            swing = max(swing, qangle(b[ix["arm_r"]][1], rest[ix["arm_r"]][1]))
        self.assertGreater(swing, 30.0, "the right arm swings now")

    def test_mirror_twice_and_rest(self):
        once = cooked(self.sk, CO.rework("t", {"from": "asym", "mirror": True}, self.src, self.sk, None))
        twice = cooked(self.sk, CO.rework("t", {"from": "x", "mirror": True}, once, self.sk, None))
        wq, wp = worst(self.sk, frames(self.src), frames(twice))
        self.assertLess(wq, ROT_TOL)
        self.assertLess(wp, POS_TOL)
        m = CO.Mirror(self.sk)
        back = m.frame(RT.rest_frame(self.sk))
        self.assertLess(worst(self.sk, [RT.rest_frame(self.sk)], [back])[0], 1e-6, "the rest pose mirrors onto itself")

    def test_root_motion_and_events_follow(self):
        evs = [{"$type": "keen::LeftFootPlantingEvent", "value": {"time": {"value": 250_000_000}, "duration": {"value": 0}}},
               {"$type": "keen::SfxNotifierEvent", "value": {"time": {"value": 1_000_000_000}, "duration": {"value": 200_000_000}, "slot": "HandL",
                                                             "notifierOffset": {"localOffset": {"x": 0.0, "y": 0.0, "z": 0.0},
                                                                                "worldOffset": {"x": 0.5, "y": 0.0, "z": 0.0},
                                                                                "orientationOffset": {"x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0}}}}]
        src = CO.Source("asym", self.src.clip, self.sk, rm=self.path, sequences=[{"events": evs, "length": {"value": 1_500_000_000}, "loop": False, "name": "x"}])
        D = src.duration  # 1.5 s, the path goes 0.3 m right, 1 m forward and turns 20 degrees

        def run(**spec):
            return CO.rework("t", dict(spec, **{"from": "asym"}), src, self.sk, lambda r: blendsrc, slots=SLOTS)

        def times(res):
            return [(e["$type"].split("::")[-1], e["value"]["time"]["value"] / 1e9, e["value"]["duration"]["value"] / 1e9) for e in res.sequences[0]["events"]]

        def end(res):
            return res.rm[0][-1]

        blendsrc = CO.Source("still", self.src.clip, self.sk)
        r = run(trim=[0.5, 1.5])
        self.assertAlmostEqual(r.n / 60, 1.0)
        self.assertEqual(r.rm[0][0], (0.0, 0.0, 0.0))
        # the second half of the path, in the frame of where it stood at 0.5 s
        p0, q0 = src.rm_at(30), src.rm_at(30)[1]
        want = qrot(qinv(q0), tuple(a - b for a, b in zip(self.path[0][-1], p0[0])))
        for x, y in zip(end(r), want):
            self.assertAlmostEqual(x, y, places=6)
        self.assertEqual(times(r), [("SfxNotifierEvent", 0.5, 0.2)], "the foot plant at 0.25 s is outside the span")
        self.assertTrue(any("trim drops 1 event" in w for w in r.report["warnings"]))

        r = run(reverse=True)
        self.assertAlmostEqual(qangle(r.rm[1][-1], qinv(self.path[1][-1])), 0.0, places=4)
        back = qrot(qinv(self.path[1][-1]), tuple(-c for c in self.path[0][-1]))
        for x, y in zip(end(r), back):
            self.assertAlmostEqual(x, y, places=6)
        self.assertEqual([(a, round(b, 6), round(c, 6)) for a, b, c in times(r)], [("SfxNotifierEvent", 0.3, 0.2), ("LeftFootPlantingEvent", 1.25, 0.0)])

        r = run(speed=2.0)
        self.assertAlmostEqual(r.n / 60, D / 2)
        for x, y in zip(end(r), self.path[0][-1]):
            self.assertAlmostEqual(x, y, places=6)
        self.assertEqual([(a, round(b, 6), round(c, 6)) for a, b, c in times(r)], [("LeftFootPlantingEvent", 0.125, 0.0), ("SfxNotifierEvent", 0.5, 0.1)])

        r = run(mirror=True)
        self.assertAlmostEqual(end(r)[0], -0.3, places=6)
        self.assertAlmostEqual(end(r)[2], 1.0, places=6)
        self.assertAlmostEqual(qangle(r.rm[1][-1], axis_q(1, -20.0)), 0.0, places=4)
        evs_m = r.sequences[0]["events"]
        self.assertEqual(evs_m[0]["$type"], "keen::RightFootPlantingEvent")
        self.assertEqual(evs_m[1]["value"]["slot"], "HandR")
        self.assertEqual(evs_m[1]["value"]["notifierOffset"]["worldOffset"]["x"], -0.5)

        r = run(offset={"head": {"rotate": [0, 10, 0]}})
        self.assertLess(max(abs(a - b) for p, q in zip(r.rm[0], self.path[0]) for a, b in zip(p, q)), 1e-9)
        self.assertEqual(times(r), [("LeftFootPlantingEvent", 0.25, 0.0), ("SfxNotifierEvent", 1.0, 0.2)])

        r = run(blend={"with": "still", "weight": 0.5})
        for x, y in zip(end(r), self.path[0][-1]):
            self.assertAlmostEqual(x, y / 2, places=6)
        self.assertIn("standing still", r.report["siblings"]["rootMotion"])
        r = run(blend={"with": "still", "bones": ["arm_l"]})
        self.assertLess(max(abs(a - b) for a, b in zip(end(r), self.path[0][-1])), 1e-9, "a mask without the root keeps the source's root motion")
        blendsrc = CO.Source("still", self.src.clip, self.sk, rm=self.path, sequences=src.sequences)
        r = run(blend={"with": "still", "weight": 0.3})
        self.assertTrue(any("events of still are dropped" in w for w in r.report["warnings"]))
        self.assertEqual(len(r.sequences[0]["events"]), 2)

    def test_root_motion_that_does_not_start_at_zero(self):
        # some of the game's paths start away from zero (z 1.0 -> 2.25, startToEnd -1.25): an operation keeps the start
        path = ([(0.0, 0.0, 1.0 + 1.25 * f / 90) for f in range(91)], [(0.0, 0.0, 0.0, -1.0)] * 91)
        src = CO.Source("asym", self.src.clip, self.sk, rm=path)
        for spec in ({"trim": [0.0, 1.5]}, {"reverse": True}, {"mirror": True}):
            once = CO.rework("t", dict(spec, **{"from": "asym"}), src, self.sk, None)
            back = CO.rework("t", dict(spec, **{"from": "asym"}), CO.Source("asym", self.src.clip, self.sk, rm=once.rm), self.sk, None)
            self.assertEqual(once.rm[0][0][2], 1.0, spec)
            self.assertLess(max(abs(a - b) for p, q in zip(back.rm[0], path[0]) for a, b in zip(p, q)), 1e-9, spec)
            self.assertLess(qangle(back.rm[1][0], path[1][0]), 1e-6)
        v = C.root_motion_value(*path)
        self.assertAlmostEqual(v["startToEnd"]["z"], -1.25)
        self.assertAlmostEqual(C.lve_value(*path)["startToEnd"]["z"], -1.25)

    def test_refusals(self):
        def bad(spec, word, src=None, resolve=None, sk=None):
            with self.assertRaises(CO.OpError) as e:
                CO.check("t", dict(spec, **{"from": "asym"}))
                CO.rework("t", dict(spec, **{"from": "asym"}), src or self.src, sk or self.sk, resolve or (lambda r: other), slots=SLOTS)
            self.assertIn(word, str(e.exception))

        golem_dir = tempfile.TemporaryDirectory()
        self.addCleanup(golem_dir.cleanup)
        glb = Path(golem_dir.name) / "g.glb"
        make_golem.write(glb)
        gsk = K.from_gltf(glb)
        raw, *_ = C.cook_clip(Doc(glb), "Idle", gsk)
        other = CO.Source("golem_idle", anim.parse(raw), gsk)
        bad({"trim": [0.5, 9.0]}, "outside")
        bad({"trim": [1.0, 0.5]}, "start < end")
        bad({"speed": 0}, "above 0")
        bad({"speed": -1}, "above 0")
        bad({"blend": {"with": "golem_idle", "weight": 0.5}}, "another skeleton")
        bad({"blend": {"with": "x", "weight": 0.5, "bones": ["arm_l"]}}, "one of the two")
        bad({"blend": {"with": "x", "bones": ["nope"]}}, "'nope' is not a joint", resolve=lambda r: self.src)
        bad({"offset": {"nope": {"rotate": [0, 0, 1]}}}, "'nope' is not a joint")
        bad({"offset": {"head": {"turn": [0, 0, 1]}}}, "offset.head")
        bad({"mirror": {"pairs": {"arm_l": "nope"}}}, "'nope' is not a joint")
        bad({"mirror": {"pairs": {"hand_l": "arm_r"}}}, "parents are not")
        bad({}, "another skeleton", src=other)
        with self.assertRaises(CO.OpError):
            CO.check("t", {"from": "a", "animation": "b"})
        with self.assertRaises(CO.OpError):
            CO.check("t", {"mirror": True})

    def test_ambiguous_mirror(self):
        q = (0.0, 0.0, 0.0, 1.0)
        names = ["root", "a_l", "b_l", "a_r", "b_r"]
        rest = [((0.0, 0.0, 0.0), q, (1.0,) * 3), ((-0.1, 1.0, 0.0), q, (1.0,) * 3), ((-0.1, 1.0, 0.0), q, (1.0,) * 3),
                ((0.1, 1.0, 0.0), q, (1.0,) * 3), ((0.1, 1.0, 0.0), q, (1.0,) * 3)]
        sk = K.Skeleton(names, [0xFFFF, 0, 0, 0, 0], rest, source="test")
        with self.assertRaises(CO.OpError) as e:
            CO.Mirror(sk)
        self.assertIn("fit equally well", str(e.exception))
        m = CO.Mirror(sk, {"pairs": {"a_l": "a_r", "b_l": "b_r"}})
        self.assertEqual(m.report()["pairs"], [["a_l", "a_r"], ["b_l", "b_r"]])
        # rest frames that differ tell coincident joints apart: b_r turned like b_l's mirror image
        rest2 = list(rest)
        rest2[2] = (rest[2][0], axis_q(2, 40.0), rest[2][2])
        rest2[4] = (rest[4][0], mirror_q(axis_q(2, 40.0)), rest[4][2])
        self.assertEqual(CO.Mirror(K.Skeleton(names, [0xFFFF, 0, 0, 0, 0], rest2, source="test")).report()["pairs"], [["a_l", "a_r"], ["b_l", "b_r"]])


class FakeGame:
    """Stands in for the installed game (tools/kfc's Kfc): resources by key."""

    def __init__(self):
        self.res = {}

    def put(self, guid, type_name, raw):
        self.res[ResourceKey(guid, TYPES.get(type_name)["hash"], 0)] = raw

    def has(self, key):
        return key in self.res

    def read(self, key):
        return self.res[key]


class RecipeRun(unittest.TestCase):
    GAME_CLIP = "11111111-2222-3333-4444-555555555555"

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)
        make_golem.write(self.dir / "golem.glb")
        self.r = self.dir / "cook.json"

    def tearDown(self):
        self.tmp.cleanup()

    def recipe(self, steps, inputs=None):
        doc = {"format": 1, "tool": "animcook", "mod": "m", "steps": [{"op": "skeleton", "name": "g", "gltf": "golem.glb"}] + steps}
        if inputs:
            doc["inputs"] = inputs
        self.r.write_text(json.dumps(doc))

    def game(self, with_siblings=True):
        """A 'game' whose clip is the golem's Step with root motion and a foot plant, as the game stores its clips."""
        doc = Doc(self.dir / "golem.glb")
        sk = K.from_gltf(doc)
        raw, info, rm, lv = C.cook_clip(doc, "Step", sk, root_motion={})
        g = FakeGame()
        g.put(self.GAME_CLIP, "keen::JointAnimation", raw)
        if with_siblings:
            enc = R.Encoder(TYPES)
            g.put(self.GAME_CLIP, "keen::RootMotionAnimation", enc.encode(TYPES.get("keen::RootMotionAnimation"), rm))
            g.put(self.GAME_CLIP, "keen::LveAnimation", enc.encode(TYPES.get("keen::LveAnimation"), lv))
            seq = C.event_sequence_value(TYPES, Decoder(TYPES), "x", [{"at": 0.5, "foot": "left"}, {"at": 1.5, "foot": "right"}], info["duration"])
            sg = "99999999-2222-3333-4444-555555555555"
            g.put(sg, "keen::SequenceResource", enc.encode(TYPES.get("keen::SequenceResource"), seq))
            g.put(self.GAME_CLIP, "keen::AnimationSequenceContainer", enc.encode(TYPES.get("keen::AnimationSequenceContainer"), {"sequences": [sg]}))
        return g

    def entries(self):
        return json.loads((self.dir / "cooked" / "cook-report.json").read_text())

    def test_from_clips_need_no_gltf(self):
        self.recipe([{"op": "clips", "skeleton": "g", "gltf": "golem.glb", "clips": {"idle": {"animation": "Idle", "loop": True}}},
                     {"op": "clips", "skeleton": "g", "clips": {"idle_m": {"from": "idle", "mirror": True}, "idle_mm": {"from": "idle_m", "mirror": True},
                                                                 "slow": {"from": "idle", "speed": 0.5}}},
                     {"op": "graph", "name": "g", "skeleton": "g", "states": {"idle": {"engine": "Idle", "clip": "idle_m"}}}])
        rep = R.Run(self.r, TYPES).run(update_manifest=True)
        self.assertTrue(rep["ok"], rep)
        step = rep["steps"][2]
        self.assertIsNone(step["gltf"])
        by = {c["name"]: c for c in step["clips"]}
        self.assertEqual(by["idle_m"]["guid"], G.derive("m", "clip", "idle_m"))
        self.assertTrue(by["idle_m"]["loop"], "a loop stays a loop")
        self.assertAlmostEqual(by["slow"]["duration"], 4.0)
        self.assertEqual(by["idle_m"]["mirror"]["unplaced"], ["leg_r"])
        a = anim.parse((self.dir / "cooked/clip_idle.bin").read_bytes())
        b = anim.parse((self.dir / "cooked/clip_idle_mm.bin").read_bytes())
        sk = K.from_gltf(self.dir / "golem.glb")
        sa, sb = CO.Source("a", a, sk), CO.Source("b", b, sk)
        self.assertLess(worst(sk, frames(sa), frames(sb))[0], ROT_TOL)

    def test_a_game_clip_and_its_siblings(self):
        self.recipe([{"op": "clips", "skeleton": "g", "clips": {
            "back": {"from": "@step", "reverse": True},
            "fast_m": {"from": "@step", "speed": 2, "mirror": True},
            "cut": {"from": "@step", "trim": [0.25, 1.0], "guid": "@step"},
            "own": {"from": "@step", "events": [{"at": 0.1, "noise": 3}]},
            "noroot": {"from": "@step", "rootMotion": False, "guid": "@step"}}}],
            inputs={"step": {"type": "keen::JointAnimation", "guid": self.GAME_CLIP}})
        rep = R.Run(self.r, TYPES, kfc=self.game()).run(update_manifest=True)
        self.assertTrue(rep["ok"], rep)
        by = {c["name"]: c for c in rep["steps"][1]["clips"]}
        self.assertAlmostEqual(by["back"]["rootMotionEnd"]["z"], -2.0, places=3)
        self.assertAlmostEqual(by["fast_m"]["rootMotionEnd"]["z"], 2.0, places=3)
        self.assertAlmostEqual(by["cut"]["rootMotionEnd"]["z"], 0.75, places=3)
        self.assertEqual(by["cut"]["guid"], self.GAME_CLIP)
        self.assertEqual(by["cut"]["mode"], "override")
        dec = Decoder(TYPES)
        seq = dec.decode(TYPES.get("keen::SequenceResource"), (self.dir / "cooked/clip_fast_m.events.bin").read_bytes())
        self.assertEqual([(e["$type"].split("::")[-1], e["value"]["time"]["value"]) for e in seq["events"]],
                         [("RightFootPlantingEvent", 250_000_000), ("LeftFootPlantingEvent", 750_000_000)])
        seq = dec.decode(TYPES.get("keen::SequenceResource"), (self.dir / "cooked/clip_cut.events.bin").read_bytes())
        self.assertEqual([e["value"]["time"]["value"] for e in seq["events"]], [250_000_000])
        self.assertEqual(seq["length"]["value"], 750_000_000)
        seq = dec.decode(TYPES.get("keen::SequenceResource"), (self.dir / "cooked/clip_own.events.bin").read_bytes())
        self.assertEqual([e["$type"].split("::")[-1] for e in seq["events"]], ["TriggerNoiseEvent"])
        self.assertTrue(any("replace the 2 event" in w for w in by["own"]["warnings"]))
        # rootMotion false on an override: the game's root motion under that GUID is replaced by a still one
        rm = dec.decode(TYPES.get("keen::RootMotionAnimation"), (self.dir / "cooked/clip_noroot.rootmotion.bin").read_bytes())
        self.assertEqual(rm["positions"][-1], {"x": 0.0, "y": 0.0, "z": 0.0})
        self.assertTrue(any("replaced by none" in w for w in by["noroot"]["warnings"]))
        self.assertEqual(by["noroot"]["siblings"]["events"]["kept"], 2)

    def test_refusals(self):
        cases = [
            ([{"op": "clips", "skeleton": "g", "clips": {"a": {"from": "nope"}}}], None, "no clip 'nope'"),
            ([{"op": "clips", "skeleton": "g", "clips": {"a": {"from": "@nope"}}}], None, "no such input"),
            ([{"op": "clips", "skeleton": "g", "clips": {"a": {"from": "@step", "rootMotion": True}}}], {"step": {"type": "keen::JointAnimation", "guid": self.GAME_CLIP}},
             "has root motion already"),
            ([{"op": "clips", "skeleton": "g", "clips": {"a": {"from": "@step", "trim": [0.0, 5.0]}}}], {"step": {"type": "keen::JointAnimation", "guid": self.GAME_CLIP}},
             "outside"),
        ]
        for steps, inputs, word in cases:
            with self.subTest(word=word):
                self.recipe(steps, inputs)
                with self.assertRaises(R.RecipeError) as e:
                    R.Run(self.r, TYPES, kfc=self.game()).run()
                self.assertIn(word, str(e.exception))
        self.recipe([{"op": "clips", "skeleton": "g", "clips": {"a": {"from": "@step"}}}], {"step": {"type": "keen::JointAnimation", "guid": self.GAME_CLIP}})
        with self.assertRaises(R.RecipeError) as e:
            R.Run(self.r, TYPES).run()
        self.assertIn("reads the installed game", str(e.exception))
        for clip, word in (({"from": "x", "animation": "Idle"}, "not both"), ({"from": "x", "speed": 0}, "above 0"), ({"mirror": True}, "has none"),
                           ({"from": "x", "blend": {"with": "y"}}, "weight or bones")):
            with self.subTest(word=word):
                self.recipe([{"op": "clips", "skeleton": "g", "clips": {"a": clip}}])
                with self.assertRaises(R.RecipeError) as e:
                    R.load(self.r)
                self.assertIn(word, str(e.exception))
        self.recipe([{"op": "clips", "skeleton": "g", "clips": {"a": {"animation": "Idle"}}}])
        with self.assertRaises(R.RecipeError) as e:
            R.load(self.r)
        self.assertIn("gltf missing", str(e.exception))


if __name__ == "__main__":
    unittest.main()
