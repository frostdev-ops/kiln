# SPDX-License-Identifier: MIT
"""A mod's own particle module (ShaderBlock), run on the CPU through spvsim with the protocol checks of harness.py.

    python3 native/services/vfx/shaders/test/test_mod_module.py -v

Runs mods/samples/ShaderSample/shaders/build/swirl.spv (Kiln's sprite with two hooks, built by kiln-vfxcook shader build) next to Kiln's
own sprite on the same block. Shows that a kit-built module keeps every list and counter invariant, that its hooks change what they
should, and that bad hook output can't escape the kit's guards. Not shown: how it looks, or that the game compiles it.
"""
import math
import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[4]
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(ROOT / "tools/kfc"))

from harness import P, Entry, World, emitter_block, sprite_block  # noqa: E402
from spvsim import Sim  # noqa: E402

BUILD = HERE.parent / "build"
EMITTER = Sim((BUILD / "kiln_emitter.spv").read_bytes())
SPRITE = Sim((BUILD / "kiln_sprite.spv").read_bytes())
SWIRL_FILE = ROOT / "mods/samples/ShaderSample/shaders/build/swirl.spv"
SWIRL = Sim(SWIRL_FILE.read_bytes())


def world(child, user, n=24, ticks=0):
    block = sprite_block(life=(5, 5), speed=(0.5, 0.5), spread=P["KS_SPREAD_SPHERE"], shape=P["KS_SHAPE_DISC"], shape_size=(1.5, 0, 0), user=user)
    w = World([Entry(EMITTER, depth=0, child=1, state_size=P["KE_STATE_SIZE"], params=emitter_block(duration=0.001, burst=n)),
               Entry(child, depth=1, parent=0, max_count=64, state_size=P["KS_STATE_SIZE"], params=block)], position=(0.0, 0.0, 0.0))
    for _ in range(ticks):
        w.tick()
        w.check()
    return w


class ModModule(unittest.TestCase):
    def test_stamped_for_this_abi(self):
        self.assertIn(b"kiln-vfx-abi-%d" % P["KILN_VFX_ABI"], SWIRL_FILE.read_bytes())

    def test_keeps_the_protocol(self):
        w = world(SWIRL, [6.0, 0.8, 0.4], ticks=40)
        self.assertEqual(len(w.draws()), 24)

    def test_hooks_change_motion_and_look(self):
        plain = world(SPRITE, [6.0, 0.8, 0.4], ticks=40).draws()
        swirl = world(SWIRL, [6.0, 0.8, 0.4], ticks=40).draws()
        # same seed and block: Kiln's sprite ignores the constants, the module's curl pushes particles off their straight paths
        moved = sum(math.dist(a["position"], b["position"]) for a, b in zip(sorted(plain, key=lambda d: d["position"]), sorted(swirl, key=lambda d: d["position"])))
        self.assertGreater(moved, 1.0)
        # the draw hook turns each quad by the distance travelled (kept in aux), and brightens fast ones
        self.assertTrue(any(abs(d["rotation"][2]) > 1e-3 for d in swirl))
        self.assertGreater(max(d["color"][0] for d in swirl), max(d["color"][0] for d in plain))

    def test_zero_constants_is_the_plain_motion_plus_lift(self):
        plain = world(SPRITE, [0, 0, 0], ticks=10).draws()
        swirl = world(SWIRL, [0, 1, 0], ticks=10).draws()
        for a, b in zip(sorted(plain, key=lambda d: d["position"]), sorted(swirl, key=lambda d: d["position"])):
            for x, y in zip(a["position"], b["position"]):
                self.assertAlmostEqual(x, y, 4)

    def test_wild_constants_stay_contained(self):
        # huge forces fling particles out of the world: they die, nothing non-finite is drawn, lists stay whole
        for user in ([1e30, 1.0, 0.0], [float("nan"), 1.0, 0.0], [0.0, 1.0, float("inf")]):
            w = world(SWIRL, user, ticks=5)
            for d in w.draws():
                self.assertTrue(all(math.isfinite(c) for c in d["position"] + d["color"] + d["scale"]))


if __name__ == "__main__":
    unittest.main()
