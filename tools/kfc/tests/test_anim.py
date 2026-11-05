# SPDX-License-Identifier: MIT
"""keen::JointAnimation codec and the skeleton / graph writers (docs/re/npc-internals.md).

    python3 -m unittest discover -s tools/kfc/tests -p test_anim.py

The synthetic half needs nothing; the other half reads the installed client and skips without it. Nothing is written
outside $KILN_OUT.
"""
import math
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kfc import anim, animgraph  # noqa: E402

GAME = Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"


def _q(deg):
    a = math.radians(deg) / 2
    return [0.0, 0.0, math.sin(a), math.cos(a)]


class Synthetic(unittest.TestCase):
    def setUp(self):
        n = 3
        self.dp = ([(0.0, 0.0, 0.0, 1.0)] * n, [(0.0, 0.0, 0.0), (0.0, 0.9, 0.0), (0.6, 0.0, 0.0)], [(1.0, 1.0, 1.0)] * n)

    def _frames(self, count):
        out = []
        for f in range(count + 1):
            u = f / count
            rot = [list(q) for q in self.dp[0]]
            pos = [list(p) for p in self.dp[1]]
            scl = [list(s) for s in self.dp[2]]
            rot[2] = _q(60 * math.sin(2 * math.pi * u))
            pos[1][1] = 0.9 + 0.1 * math.sin(4 * math.pi * u)
            out.append((rot, pos, scl))
        return out

    def test_round_trip_and_error(self):
        frames = self._frames(200)
        raw = anim.build(anim.encode(frames, self.dp, 0x1234, fps=60, loop=True))
        clip = anim.parse(raw)
        self.assertEqual(anim.build(clip), raw)
        self.assertEqual(clip.frame_count, 200)
        self.assertEqual(len(clip.segments), 4)  # 63-frame segments
        for s in clip.segments:
            self.assertEqual(s.end - s.start, 63)
            for ch in (s.orientation, s.position, s.scale):
                for t in ch.animated:
                    self.assertEqual(t.mask & 1, 1, "a segment's first frame needs a key")
                    self.assertEqual(len(t.keys), bin(t.mask).count("1"))
        for f in range(0, 200, 7):
            pose = anim.sample_frame(clip, float(f), self.dp)
            want = frames[f]
            self.assertLess(max(abs(a - b) for a, b in zip(pose[0][2], want[0][2])), 0.002)
            self.assertLess(abs(pose[1][1][1] - want[1][1][1]), 0.001)
        self.assertEqual(sorted(clip.segments[0].scale.default), [0, 1, 2], "constant at the default pose: default list")

    def test_clamp_flag_and_end_key(self):
        clip = anim.parse(anim.build(anim.encode(self._frames(100), self.dp, 1, fps=60, loop=False)))
        self.assertEqual(clip.flags, 0)
        # the last key is at frame_count, one past the last whole frame: the sampler interpolates into it
        last = clip.segments[-1]
        for t in last.orientation.animated:
            self.assertEqual(last.start + max(i for i in range(64) if (t.mask >> i) & 1), clip.frame_count)

    def test_builders(self):
        v, names = animgraph.skeleton_value("00000000-0000-0000-0000-00000000000a", ["a", "b"], [0xFFFF, 0], [(0, 0, 0), (0, 1, 0)])
        self.assertEqual(v["defaultPose"]["alignedJointCount"], 4)
        self.assertEqual(len(v["defaultPose"]["data"]), 12)  # 3 x aligned float4s
        with self.assertRaises(ValueError):
            animgraph.skeleton_value("00000000-0000-0000-0000-00000000000a", ["a", "b"], [1, 0xFFFF], [(0, 0, 0), (0, 1, 0)])
        b = animgraph.GraphBuilder("00000000-0000-0000-0000-00000000000a", {8: 1})
        b.state("Idle", animgraph.IDLE_STATE_GUID, "00000000-0000-0000-0000-00000000000b", 2.0)
        g = b.value()
        self.assertEqual(g["slotBoneIndexMapping"][8], 1)
        self.assertEqual(g["usedInputIds"]["parameterIds"][0]["value"], 0x1490BED7)
        pos = {n["value"]["id"]["value"]: i for i, n in enumerate(g["nodeDefinitions"])}
        for n in g["nodeDefinitions"]:
            if n["value"].get("type") == "PoseResult":
                self.assertLess(pos[n["value"]["input"]["value"]], pos[n["value"]["id"]["value"]], "inputs come first")


@unittest.skipUnless((GAME / "enshrouded.kfc").exists(), "no installed client")
class GameData(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        os.environ.setdefault("KILN_OUT", tempfile.mkdtemp())
        from kfc.archive import Kfc, ResourceKey
        from kfc.blob import Decoder
        from kfc.blobwrite import Encoder
        from kfc.refl import TypeDb
        cls.kfc = Kfc(GAME)
        cls.types = TypeDb.from_exe(GAME / "enshrouded.exe")
        cls.Key, cls.Decoder, cls.Encoder = ResourceKey, Decoder, Encoder

    def test_clips_byte_identical(self):
        th = self.types.get("keen::JointAnimation")["hash"]
        keys = self.kfc.resources_of_type(th)
        step = 1 if os.environ.get("KILN_ANIM_ALL") else 40
        for k in keys[::step]:
            raw = self.kfc.read(k)
            self.assertEqual(anim.build(anim.parse(raw)), raw, k.guid)

    def test_key_norms_and_hierarchy_hash(self):
        mh = self.types.get("keen::ModelHierarchyResource")
        hg = "aa1770ef-e285-453c-a5c5-2356d2c3b7be"  # the Vukah's skeleton
        h = self.Decoder(self.types, max_array=1 << 26).decode(mh, self.kfc.read(self.Key(hg, mh["hash"], 0)))
        raw = self.kfc.read(self.Key("d099cbe2-d3fc-4b3e-af5a-ebb1aa7e4324", self.types.get("keen::JointAnimation")["hash"], 0))
        clip = anim.parse(raw)
        self.assertEqual(anim.hierarchy_hash(h["jointNames"]), clip.hierarchy_hash)
        for s in clip.segments:
            for t in s.orientation.animated:
                for v, _ in t.keys:
                    self.assertAlmostEqual(math.sqrt(sum((x / 32767) ** 2 for x in v)), 1.0, places=3)

    def test_builders_have_the_games_fields(self):
        t = self.types.get("keen::anim_graph::runtime_graph::AnimationGraphResource2_0")
        game = self.Decoder(self.types, max_array=1 << 26).decode(t, self.kfc.read(self.Key("6d44ec65-6ae2-405f-9f62-12a10d721eb7", t["hash"], 0)))
        kinds = {n["$type"]: n["value"] for n in game["nodeDefinitions"]}
        b = animgraph.GraphBuilder("00000000-0000-0000-0000-00000000000a")
        b.state("Idle", animgraph.IDLE_STATE_GUID, "00000000-0000-0000-0000-00000000000b", 2.0)
        g = b.value()
        self.assertEqual(set(g), set(game))
        for n in g["nodeDefinitions"]:
            self.assertEqual(set(n["value"]), set(kinds[n["$type"]]), n["$type"])
        raw = self.Encoder(self.types).encode(t, g)
        again = self.Decoder(self.types, max_array=1 << 26).decode(t, raw)
        self.assertEqual(self.Encoder(self.types).encode(t, again), raw)


if __name__ == "__main__":
    unittest.main()
