# SPDX-License-Identifier: MIT
"""A mod's particle hooks (ShaderBlock), linked into Kiln's sprite and run on the CPU through spvsim with the protocol checks of harness.py.

    python3 native/services/vfx/shaders/test/test_mod_module.py -v

Links mods/samples/ShaderSample/shaders/build/swirl.spv (two hooks, built by kiln-vfxcook shader build) the way the game's cook does, and
runs the result next to Kiln's own sprite on the same block. Shows that the linked module keeps every list and counter invariant, that
the hooks change what they should, and that hostile hooks (endless loops, wild indices, NaN everywhere) stay contained: they end, they
never touch a buffer, and nothing non-finite is drawn. Not shown: how it looks, or that the game compiles it.
"""
import math
import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[4]
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(ROOT / "tools/kfc"))

import hooks  # noqa: E402
from harness import P, Entry, World, emitter_block, sprite_block  # noqa: E402
from spvsim import Sim  # noqa: E402

BUILD = HERE.parent / "build"
EMITTER = Sim((BUILD / "kiln_emitter.spv").read_bytes())
SPRITE = Sim((BUILD / "kiln_sprite.spv").read_bytes())
HOOKED = Sim((BUILD / "kiln_sprite_hooked.spv").read_bytes())
SWIRL_FILE = ROOT / "mods/samples/ShaderSample/shaders/build/swirl.spv"
SWIRL_CODE, SWIRL_REPORT = hooks.link(SWIRL_FILE)
SWIRL = Sim(SWIRL_CODE)


def world(child, user, n=24, ticks=0):
    block = sprite_block(life=(5, 5), speed=(0.5, 0.5), spread=P["KS_SPREAD_SPHERE"], shape=P["KS_SHAPE_DISC"], shape_size=(1.5, 0, 0), user=user)
    w = World([Entry(EMITTER, depth=0, child=1, state_size=P["KE_STATE_SIZE"], params=emitter_block(duration=0.001, burst=n)),
               Entry(child, depth=1, parent=0, max_count=64, state_size=P["KS_STATE_SIZE"], params=block)], position=(0.0, 0.0, 0.0))
    for _ in range(ticks):
        w.tick()
        w.check()
    return w


def by_position(draws):
    return sorted(draws, key=lambda d: d["position"])


class Linked(unittest.TestCase):
    def test_the_hook_module_is_stamped_and_holds_no_buffer(self):
        code = SWIRL_FILE.read_bytes()
        self.assertIn(b"kiln-vfx-hooks-%d" % P["KILN_HOOKS_ABI"], code)
        self.assertNotIn(b"g_chunkStateDataBuffer", code)
        self.assertEqual(SWIRL_REPORT["hooks"], ["update", "draw"])

    def test_the_linked_sprite_with_no_hooks_is_kilns_sprite(self):
        a = by_position(world(SPRITE, [6.0, 0.8, 0.4], ticks=20).draws())
        b = by_position(world(HOOKED, [6.0, 0.8, 0.4], ticks=20).draws())
        self.assertEqual(len(a), len(b))
        for x, y in zip(a, b):
            for k in ("position", "color", "scale"):
                for p, q in zip(x[k], y[k]):
                    self.assertAlmostEqual(p, q, 5)

    def test_keeps_the_protocol(self):
        w = world(SWIRL, [6.0, 0.8, 0.4], ticks=40)
        self.assertEqual(len(w.draws()), 24)

    def test_hooks_change_motion_and_look(self):
        plain = world(SPRITE, [6.0, 0.8, 0.4], ticks=40).draws()
        swirl = world(SWIRL, [6.0, 0.8, 0.4], ticks=40).draws()
        moved = sum(math.dist(a["position"], b["position"]) for a, b in zip(by_position(plain), by_position(swirl)))
        self.assertGreater(moved, 1.0)
        self.assertTrue(any(abs(d["rotation"][2]) > 1e-3 for d in swirl))
        self.assertGreater(max(d["color"][0] for d in swirl), max(d["color"][0] for d in plain))

    def test_zero_constants_is_the_plain_motion_plus_lift(self):
        plain = world(SPRITE, [0, 0, 0], ticks=10).draws()
        swirl = world(SWIRL, [0, 1, 0], ticks=10).draws()
        for a, b in zip(by_position(plain), by_position(swirl)):
            for x, y in zip(a["position"], b["position"]):
                self.assertAlmostEqual(x, y, 4)

    def test_wild_constants_stay_contained(self):
        for user in ([1e30, 1.0, 0.0], [float("nan"), 1.0, 0.0], [0.0, 1.0, float("inf")]):
            w = world(SWIRL, user, ticks=5)
            w.check()
            for d in w.draws():
                self.assertTrue(all(math.isfinite(c) for c in d["position"] + d["color"] + d["scale"]))


HEAD = '#include "kiln_hooks.hlsl"\n'
TAIL = '#include "kiln_hooks_main.hlsl"\n'


@unittest.skipUnless(hooks.HAVE_GLSLANG, "glslangValidator not installed")
class Hostile(unittest.TestCase):
    """Hooks written to hurt. Each is built, linked and run for real: the harness faults on any access outside a buffer."""

    def run_hook(self, body, draw=None, ticks=6, n=8):
        src = HEAD + f"void up(inout KilnSprite s, KilnContext c) {{ {body} }}\n#define KILN_UPDATE up\n"
        if draw:
            src += f"void dr(inout KilnLook l, KilnSprite s, KilnContext c) {{ {draw} }}\n#define KILN_DRAW dr\n"
        code, report = hooks.build_and_link(src + TAIL)
        sim = Sim(code)
        w = world(sim, [1.0, 2.0, 3.0], n=n)
        for _ in range(ticks):
            w.tick()
            w.check()
        return w, sim, report

    def test_an_endless_loop_ends(self):
        w, sim, report = self.run_hook("while (true) { s.aux += 1.0f; }")
        self.assertEqual(report["loops"], 1)
        self.assertGreater(report["loopCap"], 0)
        # every invocation ended; the loop ran at most the cap
        self.assertEqual(len(w.draws()), 8)

    def test_nested_endless_loops_end_within_the_budget(self):
        w, sim, report = self.run_hook("for (;;) { for (;;) { do { s.spin += 1.0f; } while (s.spin > -1.0f); } }")
        self.assertEqual(report["loops"], 3)
        per = sim.steps / (8 * 6)
        # the sprite's own work plus the capped hook: far below what an unbounded loop would reach
        self.assertLess(per, 20 * report["budget"])

    def test_loops_in_called_functions_share_the_cap(self):
        src = HEAD + ("float burn(float x) { while (x > -1.0f) { x += 1.0f; } return x; }\n"
                      "void up(inout KilnSprite s, KilnContext c) { for (int i = 0; i < 1000000; i++) { s.aux += burn(s.aux); } }\n"
                      "#define KILN_UPDATE up\n") + TAIL
        code, report = hooks.build_and_link(src)
        self.assertEqual(report["loops"], 2)
        w = world(Sim(code), [0, 0, 0], n=4)
        for _ in range(3):
            w.tick()
            w.check()

    def test_wild_indices_are_clamped(self):
        w, sim, report = self.run_hook(
            "float a[4] = {1.0f, 2.0f, 3.0f, 4.0f}; int k = (int)(s.position.x * 1e9f) - 2000000000;"
            " a[k] = 5.0f; s.velocity.x += a[k + 7] + c.user0[k] + s.position[k];")
        self.assertGreaterEqual(report["clampedIndices"], 3)
        self.assertEqual(len(w.draws()), 8)

    def test_nan_everywhere_kills_or_falls_back(self):
        nan = "asfloat(0x7fc00000u)"
        w, sim, report = self.run_hook(
            f"s.position = {nan}; s.velocity = 1.0f / 0.0f; s.age = {nan}; s.roll = {nan}; s.spin = {nan}; s.aux = {nan};",
            draw=f"l.color = {nan}; l.scale = {nan}; l.alpha = {nan}; l.roll = {nan};")
        self.assertEqual(w.draws(), [])
        for c, e in zip(w.chunks, w.entries):
            if e.depth == 1:
                self.assertEqual(w.counter(w.active_counter(c)), 0, "every particle died")

    def test_nan_look_only_is_not_drawn(self):
        w, _, _ = self.run_hook("", draw="l.color = asfloat(0x7f800000u); l.alpha = asfloat(0x7fc00000u);")
        self.assertEqual(w.draws(), [])
        for d in w.draws():
            self.assertTrue(all(math.isfinite(x) for x in d["color"]))


if __name__ == "__main__":
    unittest.main()
