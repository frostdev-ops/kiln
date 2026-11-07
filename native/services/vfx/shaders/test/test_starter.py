# SPDX-License-Identifier: MIT
"""The starter particle modules, run on the CPU through spvsim (no GPU, no Vulkan, no game launch).

    python3 native/services/vfx/shaders/test/test_starter.py -v

GameTest needs the installed client's files (read only) and skips without them. It runs the game's own compact modules on
the same harness: if the protocol in harness.py were wrong they would break its invariants.

What this cannot show: GPU behaviour (test_gpu.py covers that), whether the game accepts the modules, or how the result looks.
"""
import math
import os
import struct
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[4]
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(ROOT / "tools/kfc"))

from harness import (COUNTERS, KU, LIGHTS, LIGHT_SORT, P, SORT, STATE, UPDATE, Entry, World, emitter_block, light_block,  # noqa: E402
                     sprite_block)
from spvsim import BufferFault, Sim, u2f  # noqa: E402
from kfc.spirv import Module  # noqa: E402

BUILD = HERE.parent / "build"
EMITTER = (BUILD / "kiln_emitter.spv").read_bytes()
SPRITE = (BUILD / "kiln_sprite.spv").read_bytes()
LIGHT = (BUILD / "kiln_light.spv").read_bytes()

SHARED = {}


def sim(code):
    return SHARED.setdefault(code, Sim(code))


def pair(emitter, sprite, max_count=128):
    return [Entry(sim(EMITTER), depth=0, child=1, state_size=P["KE_STATE_SIZE"], params=emitter),
            Entry(sim(SPRITE), depth=1, parent=0, max_count=max_count, state_size=P["KS_STATE_SIZE"], params=sprite)]


def light_only(block):
    return [Entry(sim(LIGHT), depth=0, state_size=P["KL_STATE_SIZE"], params=block)]


class Interface(unittest.TestCase):
    """What the game's pipeline creation expects of a module (docs/re/vfx.md §3.2, §6.4)."""

    def test_shape(self):
        for code in (EMITTER, SPRITE, LIGHT):
            m = Module(code)
            self.assertLessEqual(m.version, (1, 5))
            self.assertEqual(m.entry_points, [(5, "cs_main")])
            self.assertEqual(m.local_size, (32, 1, 1))
            self.assertTrue(m.capabilities <= {1, 61, 63, 4466}, m.capabilities)   # the game's four
            self.assertTrue(m.has_push_constants())

    def test_bindings_and_kinds(self):
        ubo = {(1, 0)}
        for code in (EMITTER, SPRITE, LIGHT):
            m = Module(code)
            for sb, var in m.bindings().items():
                self.assertIn(sb[0], (0, 1), sb)
                self.assertTrue(sb == (0, 2) or sb[1] <= 82, sb)
                sc = m.variables[var][1]
                self.assertEqual(sc, 2 if sb in ubo else 12, f"{sb}: storage class {sc}")

    def test_modules_stay_small(self):
        for code in (EMITTER, SPRITE, LIGHT):
            self.assertLess(len(code), 60_000)


class Burst(unittest.TestCase):
    def world(self, **kw):
        e = kw.pop("emitter", emitter_block(duration=0.001, burst=24))
        s = kw.pop("sprite", sprite_block(life=(0.5, 0.5)))
        m = kw.pop("max_count", 128)
        return World(pair(e, s, m), **kw)

    def test_burst_spawns_draws_dies(self):
        w = self.world()
        w.tick()
        w.check()
        child = w.chunks[1]
        self.assertEqual(w.counter(w.active_counter(child)), 24)
        draws = w.draws()
        self.assertEqual(len(draws), 24)
        for i, d in enumerate(draws):
            self.assertEqual(d["instanceCount"], 1)
            self.assertEqual(d["indexCount"], 6)
            self.assertEqual(d["instanceOffset"], i)
            self.assertEqual(d["meshIndex"], 1)
            self.assertEqual(d["image"], 7)
            self.assertEqual(d["imageSampler"], 2)
            self.assertAlmostEqual(d["scale"][0], 0.5, 5)
            self.assertAlmostEqual(d["color"][0], 1.0, 5)
        for _ in range(40):
            w.tick()
            w.check()
        self.assertEqual(w.counter(w.active_counter(child)), 0)
        self.assertEqual(w.counter(child.counter_free), 128)
        self.assertEqual(len(w.draws()), 0)

    def test_emitter_ends_alone_and_particles_finish(self):
        w = self.world()
        w.tick()
        w.tick()
        w.tick()
        self.assertEqual(w.counter(w.active_counter(w.chunks[0])), 0, "a one-shot's emitter is gone")
        self.assertEqual(w.counter(w.active_counter(w.chunks[1])), 24, "its particles live on")

    def test_large_burst_spreads_over_ticks(self):
        w = self.world(emitter=emitter_block(duration=1.0, burst=200), max_count=256, sprite=sprite_block(life=(10, 10)))
        seen = []
        for _ in range(5):
            w.tick()
            w.check()
            seen.append(w.counter(w.active_counter(w.chunks[1])))
        self.assertEqual(seen, [64, 128, 192, 200, 200])

    def test_overflow_is_refused_not_broken(self):
        w = self.world(emitter=emitter_block(duration=2.0, burst=1000), max_count=32, sprite=sprite_block(life=(10, 10)))
        for _ in range(4):
            w.tick()
            w.check()
            self.assertEqual(w.counter(w.active_counter(w.chunks[1])), 32)

    def test_draw_list_full(self):
        w = self.world(cap=10)
        w.tick()
        w.check()
        self.assertEqual(w.counter(w.draw_counters["count"]), 10)

    def test_zero_capacity_list(self):
        w = self.world(cap=0)
        w.tick()
        w.check()
        self.assertEqual(w.counter(w.draw_counters["count"]), 0)

    def test_every_invocation_order(self):
        for seed in range(5):
            w = self.world(seed=seed, emitter=emitter_block(rate=300.0, duration=0.2, burst=30),
                           sprite=sprite_block(life=(0.1, 0.3)), max_count=64)
            for _ in range(40):
                w.tick()
                w.check()

    def test_rate(self):
        w = self.world(emitter=emitter_block(rate=60.0), sprite=sprite_block(life=(100, 100)))
        for _ in range(30):
            w.tick()
            w.check()
        n = w.counter(w.active_counter(w.chunks[1]))
        self.assertIn(n, (29, 30, 31))

    def test_delay_and_loop(self):
        w = self.world(emitter=emitter_block(duration=0.1, delay=0.05, burst=5, loop=True), sprite=sprite_block(life=(100, 100)))
        counts = []
        for _ in range(30):
            w.tick()
            w.check()
            counts.append(w.counter(w.active_counter(w.chunks[1])))
        self.assertEqual(counts[0], 0, "held back by the delay")
        self.assertGreaterEqual(counts[-1], 15, "loops re-burst")


class Phases(unittest.TestCase):
    def test_update_only_draws_nothing(self):
        w = World(pair(emitter_block(duration=0.001, burst=8), sprite_block(life=(1, 1))))
        w.tick(phase=1)
        w.check()
        self.assertEqual(w.counter(w.active_counter(w.chunks[1])), 8)
        self.assertEqual(len(w.draws()), 0)

    def test_draw_only_changes_nothing(self):
        w = World(pair(emitter_block(duration=0.2, rate=120.0, burst=8), sprite_block(life=(5, 5))))
        w.tick()
        w.tick()
        before = bytes(w.buf[STATE].data)
        n = w.counter(w.active_counter(w.chunks[1]))
        w.tick(phase=2, shuffle=False)
        w.check()
        self.assertEqual(w.counter(w.active_counter(w.chunks[1])), n, "no spawn, no death")
        self.assertEqual(len(w.draws()), n, "it still draws")
        # States are untouched; only the active list was rewritten.
        for c, e in zip(w.chunks, w.entries):
            size = c.count * e.state_size
            self.assertEqual(bytes(w.buf[STATE].data[c.states:c.states + size]), before[c.states:c.states + size])

    def test_draw_only_keeps_what_would_die(self):
        # Life 0.03 s at 1/60 s a tick: alive after the first tick, past its life in the second, which is draw-only.
        w = World(pair(emitter_block(duration=0.001, burst=8), sprite_block(life=(0.03, 0.03))))
        w.tick(phase=1)
        w.tick(phase=2)
        w.check()
        self.assertEqual(w.counter(w.active_counter(w.chunks[1])), 8)


class Instance(unittest.TestCase):
    def test_hidden_draws_nothing(self):
        w = World(pair(emitter_block(duration=1.0, burst=8), sprite_block(life=(5, 5))))
        w.set_instance(flags=3)
        w.tick()
        w.check()
        self.assertEqual(w.counter(w.active_counter(w.chunks[1])), 8)
        self.assertEqual(len(w.draws()), 0)

    def test_stop_ends_the_emitter_and_particles_finish(self):
        w = World(pair(emitter_block(rate=120.0), sprite_block(life=(0.3, 0.3))))
        for _ in range(5):
            w.tick()
        n = w.counter(w.active_counter(w.chunks[1]))
        self.assertGreater(n, 0)
        w.set_instance(flags=0)
        w.tick()
        w.check()
        self.assertEqual(w.counter(w.active_counter(w.chunks[0])), 0)
        for _ in range(30):
            w.tick()
            w.check()
        self.assertEqual(w.counter(w.active_counter(w.chunks[1])), 0)

    def test_bad_emitter_index_is_harmless(self):
        w = World(pair(emitter_block(duration=1.0, burst=8), sprite_block()))
        w.buf[UPDATE].put_u32(KU["emitter_count"], 0)
        w.tick()
        w.check()
        self.assertEqual(w.counter(w.active_counter(w.chunks[1])), 0)


class Motion(unittest.TestCase):
    def test_position_and_camera_relative(self):
        w = World(pair(emitter_block(duration=0.001, burst=1), sprite_block(life=(5, 5), speed=(0, 0))), position=(100.0, 50.0, 25.0))
        cam = (90 * 131072, 50 * 131072, 20 * 131072)
        for i, v in enumerate(cam):
            w.buf[UPDATE].put_u32(KU["camera_world"] + 4 * i, v)
        w.tick()
        d = w.draws()[0]
        self.assertAlmostEqual(d["position"][0], 10.0, 3)
        self.assertAlmostEqual(d["position"][1], 0.0, 3)
        self.assertAlmostEqual(d["position"][2], 5.0, 3)

    def test_gravity_and_drag(self):
        w = World(pair(emitter_block(duration=0.001, burst=1), sprite_block(life=(50, 50), speed=(0, 0), accel=(0, -10, 0))), position=(0.0, 100.0, 0.0))
        for _ in range(60):
            w.tick()
        y = w.draws()[0]["position"][1]
        self.assertLess(y, 96.0)
        self.assertGreater(y, 94.0)     # 100 - 0.5 g t²

    def test_cone_stays_in_the_cone(self):
        angle = math.radians(20)
        w = World(pair(emitter_block(duration=0.001, burst=64), sprite_block(life=(5, 5), speed=(10, 10), spread=P["KS_SPREAD_CONE"], angle=angle)), position=(0.0, 0.0, 0.0))
        w.tick()
        for d in w.draws():
            x, y, z = d["position"]
            self.assertGreaterEqual(y / max(math.sqrt(x * x + y * y + z * z), 1e-6), math.cos(angle) - 1e-3)

    def test_sphere_spread_covers_both_sides(self):
        w = World(pair(emitter_block(duration=0.001, burst=64), sprite_block(life=(5, 5), speed=(10, 10), spread=P["KS_SPREAD_SPHERE"])), position=(0.0, 0.0, 0.0))
        w.tick()
        ys = [d["position"][1] for d in w.draws()]
        self.assertTrue(min(ys) < 0 < max(ys))

    def test_size_curve_and_flipbook(self):
        w = World(pair(emitter_block(duration=0.001, burst=1),
                       sprite_block(life=(1, 1), speed=(0, 0), size=[1.0, 0, 0, 0, 0, 0, 0, 0], flipbook=4 | 4 << 16)))
        w.tick()
        self.assertLess(w.draws()[0]["scale"][0], 1.0)
        self.assertEqual(w.draws()[0]["encodedFlipbookSize"], 4 | 4 << 16)
        self.assertGreater(w.draws()[0]["flipbookTime"], 0.0)
        self.assertLess(w.draws()[0]["flipbookTime"], 1.0)

    def test_instance_scale_scales_size(self):
        w = World(pair(emitter_block(duration=0.001, burst=1), sprite_block(life=(5, 5), speed=(0, 0))))
        w.set_instance(scale=(2.0, 2.0, 2.0))
        w.tick()
        self.assertAlmostEqual(w.draws()[0]["scale"][0], 1.0, 5)


class Shapes(unittest.TestCase):
    """ABI 2: where a particle starts, and which way it goes from there."""

    def start(self, shape, size, spread=0, n=64, speed=(0.0, 0.0), scale=None, orientation=(0.0, 0.0, 0.0, 1.0)):
        w = World(pair(emitter_block(duration=0.001, burst=n), sprite_block(life=(5, 5), speed=speed, spread=spread, shape=shape, shape_size=size)),
                  position=(0.0, 0.0, 0.0))
        w.set_instance(scale=scale, orientation=orientation)
        w.tick()
        return w, [d["position"] for d in w.draws()]

    def test_point_stays_at_the_instance(self):
        _, ps = self.start(P["KS_SHAPE_POINT"], (5.0, 0.0, 0.0))
        for p in ps:
            self.assertLess(max(abs(c) for c in p), 1e-3)

    def test_sphere_shell_and_inner_radius(self):
        _, ps = self.start(P["KS_SHAPE_SPHERE"], (2.0, 2.0, 0.0))
        for p in ps:
            self.assertAlmostEqual(math.dist(p, (0, 0, 0)), 2.0, 2)
        _, ps = self.start(P["KS_SHAPE_SPHERE"], (2.0, 1.0, 0.0))
        rs = [math.dist(p, (0, 0, 0)) for p in ps]
        self.assertTrue(all(1.0 - 1e-3 <= r <= 2.0 + 1e-3 for r in rs), rs)
        self.assertTrue(min(p[1] for p in ps) < 0 < max(p[1] for p in ps))

    def test_hemisphere_stays_up(self):
        _, ps = self.start(P["KS_SHAPE_HEMISPHERE"], (3.0, 0.0, 0.0))
        self.assertTrue(all(p[1] >= -1e-4 and math.dist(p, (0, 0, 0)) <= 3.0 + 1e-3 for p in ps))

    def test_tiny_shell_preserves_its_inner_radius(self):
        w, _ = self.start(P["KS_SHAPE_SPHERE"], (1e-8, 1e-8, 0), n=32)
        # Draw positions use fixed point; inspect the floating point state instead.
        for i in range(32):
            state = w.chunks[1].states + i * P["KS_STATE_SIZE"]
            radius = math.sqrt(sum(w.buf[STATE].f32(state + P["KS_S_POS"] + j * 4) ** 2 for j in range(3)))
            self.assertAlmostEqual(radius / 1e-8, 1, 5)

    def test_zero_radius_outward_uses_up(self):
        for shape in (P["KS_SHAPE_SPHERE"], P["KS_SHAPE_DISC"], P["KS_SHAPE_LINE"]):
            _, ps = self.start(shape, (0, 0, 0), spread=P["KS_SPREAD_OUTWARD"], speed=(6, 6), n=16)
            for x, y, z in ps:
                self.assertAlmostEqual(x, 0, 5)
                self.assertAlmostEqual(y, 0.1, 4)
                self.assertAlmostEqual(z, 0, 5)

    def test_box(self):
        _, ps = self.start(P["KS_SHAPE_BOX"], (1.0, 2.0, 3.0))
        for p in ps:
            self.assertTrue(abs(p[0]) <= 1.0 + 1e-3 and abs(p[1]) <= 2.0 + 1e-3 and abs(p[2]) <= 3.0 + 1e-3, p)
        self.assertGreater(max(abs(p[2]) for p in ps), 1.5)

    def test_disc_and_ring_are_flat(self):
        _, ps = self.start(P["KS_SHAPE_DISC"], (2.0, 0.0, 0.0))
        self.assertTrue(all(abs(p[1]) < 1e-3 and math.hypot(p[0], p[2]) <= 2.0 + 1e-3 for p in ps))
        _, ps = self.start(P["KS_SHAPE_DISC"], (2.0, 2.0, 0.0))
        for p in ps:
            self.assertAlmostEqual(math.hypot(p[0], p[2]), 2.0, 2)

    def test_line_along_x(self):
        _, ps = self.start(P["KS_SHAPE_LINE"], (4.0, 0.0, 0.0))
        self.assertTrue(all(abs(p[1]) < 1e-3 and abs(p[2]) < 1e-3 and abs(p[0]) <= 2.0 + 1e-3 for p in ps))
        self.assertGreater(max(p[0] for p in ps) - min(p[0] for p in ps), 2.0)

    def test_shape_follows_instance_scale_and_rotation(self):
        # 90 degrees about z: the line's x becomes y
        q = (0.0, 0.0, math.sin(math.pi / 4), math.cos(math.pi / 4))
        _, ps = self.start(P["KS_SHAPE_LINE"], (2.0, 0.0, 0.0), scale=(3.0, 3.0, 3.0), orientation=q)
        self.assertTrue(all(abs(p[0]) < 1e-3 for p in ps))
        self.assertGreater(max(abs(p[1]) for p in ps), 1.5)
        self.assertTrue(all(abs(p[1]) <= 3.0 + 1e-3 for p in ps))

    def test_outward_moves_away_from_the_centre(self):
        # 11 ticks at 5 m/s carry a particle 0.92 m further out than it started
        for shape, size, least in ((P["KS_SHAPE_SPHERE"], (1.0, 1.0, 0.0), 1.9), (P["KS_SHAPE_DISC"], (1.0, 1.0, 0.0), 1.9),
                                   (P["KS_SHAPE_BOX"], (0.05, 0.05, 0.05), 0.85)):
            w, before = self.start(shape, size, spread=P["KS_SPREAD_OUTWARD"], speed=(5.0, 5.0), n=32)
            for _ in range(10):
                w.tick()
            after = [d["position"] for d in w.draws()]
            self.assertGreater(min(math.dist(p, (0, 0, 0)) for p in after), least, shape)
            if shape == P["KS_SHAPE_DISC"]:
                self.assertTrue(all(abs(p[1]) < 1e-3 for p in after))

    def test_bad_shape_data_is_harmless(self):
        nan = float("nan")
        for shape, size in ((99, (1.0, 1.0, 1.0)), (P["KS_SHAPE_SPHERE"], (-2.0, 5.0, 0.0)), (P["KS_SHAPE_BOX"], (nan, 1.0, 1.0)),
                            (P["KS_SHAPE_DISC"], (1e30, 0.0, 0.0))):
            w = World(pair(emitter_block(duration=0.001, burst=16), sprite_block(life=(5, 5), shape=shape, shape_size=size)))
            for _ in range(3):
                w.tick()
                w.check()
            for d in w.draws():
                self.assertTrue(all(math.isfinite(c) for c in d["position"]))


class Stretch(unittest.TestCase):
    def test_aligns_with_velocity_and_stretches(self):
        flags = P["KS_FLAG_FACE_CAMERA"] | P["KS_FLAG_ALIGN_VELOCITY"]
        w = World(pair(emitter_block(duration=0.001, burst=1),
                       sprite_block(life=(5, 5), speed=(4.0, 4.0), spread=P["KS_SPREAD_UP"], flags=flags, stretch=0.25, size=[0.5] * 8)),
                  position=(0.0, 0.0, 0.0))
        w.tick()
        d = w.draws()[0]
        self.assertAlmostEqual(d["scale"][0], 0.5, 4)
        self.assertAlmostEqual(d["scale"][1], 0.5 + 0.25 * 4.0, 3)
        # The quad's +y, turned by its rotation, points along the motion (the camera is the identity in the harness).
        x, y, z, qw = d["rotation"]
        up = (2 * (x * y - qw * z), 1 - 2 * (x * x + z * z), 2 * (y * z + qw * x))
        self.assertGreater(up[1], 0.99)

    def test_instance_scale_does_not_multiply_world_speed_twice(self):
        flags = P["KS_FLAG_FACE_CAMERA"] | P["KS_FLAG_ALIGN_VELOCITY"]
        w = World(pair(emitter_block(duration=0.001, burst=1),
                       sprite_block(life=(5, 5), speed=(4, 4), flags=flags, stretch=0.25, size=[0.5] * 8)))
        w.set_instance(scale=(3, 3, 3))
        w.tick()
        d = w.draws()[0]
        self.assertAlmostEqual(d["scale"][0], 1.5, 4)
        self.assertAlmostEqual(d["scale"][1], 1.5 + 0.25 * 12, 4)

    def test_sideways_turns_the_quad(self):
        flags = P["KS_FLAG_FACE_CAMERA"] | P["KS_FLAG_ALIGN_VELOCITY"]
        w = World(pair(emitter_block(duration=0.001, burst=1),
                       sprite_block(life=(5, 5), speed=(3.0, 3.0), flags=flags, stretch=0.0)), position=(0.0, 0.0, 0.0))
        q = (0.0, 0.0, -math.sin(math.pi / 4), math.cos(math.pi / 4))  # up becomes +x
        w.set_instance(orientation=q)
        w.tick()
        x, y, z, qw = w.draws()[0]["rotation"]
        up = (2 * (x * y - qw * z), 1 - 2 * (x * x + z * z), 2 * (y * z + qw * x))
        self.assertGreater(up[0], 0.99)

    def test_off_without_the_flag(self):
        w = World(pair(emitter_block(duration=0.001, burst=1), sprite_block(life=(5, 5), speed=(4.0, 4.0), stretch=1.0)))
        w.tick()
        d = w.draws()[0]
        self.assertAlmostEqual(d["scale"][0], d["scale"][1], 5)


class PlayParameters(unittest.TestCase):
    """KX_*: what a play, a template, a sequence or a buff sets by name."""

    def one(self, **play):
        w = World(pair(emitter_block(duration=0.001, burst=4), sprite_block(life=(5, 5), speed=(2.0, 2.0), size=[0.5] * 8, color=[(1.0, 1.0, 1.0)] * 8)),
                  position=(0.0, 0.0, 0.0))
        if play:
            w.set_play(**play)
        w.tick()
        return w

    def test_no_block_reads_as_defaults(self):
        d = self.one().draws()[0]
        self.assertAlmostEqual(d["scale"][0], 0.5, 5)
        self.assertEqual(d["color"], (1.0, 1.0, 1.0))
        self.assertAlmostEqual(d["alpha"], 1.0, 5)
        # garbage without the marker is ignored too
        d = self.one(color=(0, 0, 0, 0), size=0.0, magic=False).draws()[0]
        self.assertAlmostEqual(d["scale"][0], 0.5, 5)

    def test_kiln_defaults_change_nothing(self):
        d = self.one(color=(255, 255, 255, 255)).draws()[0]
        self.assertAlmostEqual(d["scale"][0], 0.5, 5)
        for c in d["color"]:
            self.assertAlmostEqual(c, 1.0, 5)

    def test_colour_size_brightness_alpha(self):
        d = self.one(color=(255, 0, 128, 128), size=2.0, brightness=3.0).draws()[0]
        self.assertAlmostEqual(d["scale"][0], 1.0, 5)
        self.assertAlmostEqual(d["color"][0], 3.0, 4)
        self.assertAlmostEqual(d["color"][1], 0.0, 5)
        self.assertAlmostEqual(d["color"][2], 3.0 * ((128 / 255 + 0.055) / 1.055) ** 2.4, 3)
        self.assertAlmostEqual(d["alpha"], 128 / 255, 3)

    def test_count_scales_the_burst(self):
        w = self.one(count=2.5)
        self.assertEqual(len(w.draws()), 10)
        w = self.one(count=0.0)
        self.assertEqual(len(w.draws()), 0)

    def test_speed(self):
        w = self.one(speed=3.0)
        for _ in range(29):
            w.tick()
        y = max(d["position"][1] for d in w.draws())
        self.assertAlmostEqual(y, 6.0 * 30 / 60, 1)

    def test_wild_values_are_clamped(self):
        nan = float("nan")
        d = self.one(size=nan, brightness=float("inf"), speed=-5.0).draws()[0]
        self.assertAlmostEqual(d["scale"][0], 0.5, 5)
        self.assertTrue(all(math.isfinite(c) for c in d["color"]))
        d = self.one(size=1e9).draws()[0]
        self.assertLessEqual(d["scale"][0], 0.5 * P["KX_MAX_FACTOR"] + 1e-3)

    def test_light_brightness_is_finite_in_the_half_float_draw_record(self):
        w = World(light_only(light_block(brightness=1e6)), position=(1, 2, 3))
        w.set_play(brightness=16)
        w.tick()
        words = w.buf[LIGHTS]
        for half in (words.u32(16) >> 16, words.u32(16) & 0xffff, words.u32(20) >> 16):
            value = struct.unpack("<e", struct.pack("<H", half))[0]
            self.assertTrue(math.isfinite(value))
            self.assertEqual(value, 65504)

    def test_light_takes_colour_and_brightness(self):
        w = World(light_only(light_block(radius=6.0, brightness=2.0, color=(0.5, 0.5, 0.5))), position=(1.0, 2.0, 3.0))
        w.set_play(color=(255, 0, 0, 255), brightness=2.0)
        w.tick()
        rec = w.buf[LIGHTS]
        import struct as _s
        half = lambda h: _s.unpack("<e", _s.pack("<H", h))[0]
        rg = rec.u32(16)
        self.assertAlmostEqual(half(rg >> 16), 0.5 * 2.0 * 2.0, 2)
        self.assertAlmostEqual(half(rg & 0xffff), 0.0, 3)


class Lists(unittest.TestCase):
    def split(self, flags):
        return World(pair(emitter_block(duration=0.001, burst=4), sprite_block(draw_flags=flags)), split=True)

    def test_split_unlit(self):
        w = self.split(P["KS_DRAW_ADDITIVE"])
        w.tick()
        w.check()
        self.assertEqual(w.counter(w.draw_counters["count_unlit"]), 4)
        self.assertEqual(w.draws(1, "count_unlit")[0]["flags"], P["KS_DRAW_ADDITIVE"])
        self.assertEqual(w.counter(w.draw_counters["count"]), 0)

    def test_split_lit(self):
        w = self.split(P["KS_DRAW_LIT"])
        w.tick()
        self.assertEqual(w.counter(w.draw_counters["count_lit"]), 4)
        self.assertEqual(len(w.draws(2, "count_lit")), 4)

    def test_split_shaded(self):
        w = self.split(P["KS_DRAW_LIT"] | P["KS_DRAW_SHADED"])
        w.tick()
        self.assertEqual(w.counter(w.draw_counters["count_shaded"]), 4)
        self.assertEqual(len(w.draws(3, "count_shaded")), 4)

    def test_sort_data_only_when_not_split(self):
        w = World(pair(emitter_block(duration=0.001, burst=4), sprite_block()), split=False)
        w.tick()
        sort = w.buf[SORT]
        self.assertEqual([sort.u32(i * 8 + 4) for i in range(4)], [0, 1, 2, 3])
        self.assertTrue(all(sort.u32(i * 8) != 0 for i in range(4)))
        w = self.split(0)
        w.tick()
        self.assertTrue(all(b == 0 for b in w.buf[SORT].data))


class Bounded(unittest.TestCase):
    """Every loop in the modules has a fixed limit: an invocation's work must not grow with the data."""

    def test_work_per_invocation(self):
        huge = emitter_block(rate=1e30, burst=0xffffffff, duration=1.0)
        w = World(pair(huge, sprite_block(life=(10, 10)), 256))
        for e in w.entries:
            e.sim.steps = 0
        w.tick()
        root = w.entries[0].sim.steps / 32
        for e in w.entries:
            e.sim.steps = 0
        w.tick()
        child = w.entries[1].sim.steps / 256
        # 64 spawns x ~200 instructions; one particle with a draw: a couple of thousand.
        self.assertLess(root, 40_000)
        self.assertLess(child, 6_000)


class SortKey(unittest.TestCase):
    def test_ascending_key_is_far_first(self):
        # The renderer sorts ascending and draws in that order: the far particle must get the smaller key.
        w = World(pair(emitter_block(duration=0.001, burst=2), sprite_block(life=(100, 100), speed=(0, 0), radius=0.0)), position=(0.0, 0.0, 10.0))
        w.tick()
        near = w.buf[SORT].u32(0)
        w2 = World(pair(emitter_block(duration=0.001, burst=2), sprite_block(life=(100, 100), speed=(0, 0), radius=0.0)), position=(0.0, 0.0, 500.0))
        w2.tick()
        far = w2.buf[SORT].u32(0)
        self.assertLess(far >> 16, near >> 16)


class BadData(unittest.TestCase):
    """Whatever the numbers, a module must stay in its buffers, keep its lists straight and draw nothing absurd."""

    def stress(self, emitter, sprite, ticks=30, **kw):
        w = World(pair(emitter, sprite, kw.pop("max_count", 32)), **kw)
        for _ in range(ticks):
            w.tick()
            w.check()
            for d in w.draws():
                for v in d["position"] + d["scale"] + d["color"] + d["rotation"]:
                    self.assertTrue(math.isfinite(v), d)
                self.assertLessEqual(d["scale"][0], P["KILN_VFX_MAX_SCALE"])
        return w

    def test_nan_everywhere_in_motion(self):
        nan = float("nan")
        self.stress(emitter_block(duration=0.001, burst=8), sprite_block(speed=(nan, nan), accel=(nan, nan, nan), life=(5, 5)))

    def test_nan_size_and_color(self):
        nan = float("nan")
        self.stress(emitter_block(duration=0.001, burst=8),
                    sprite_block(size=[nan] * 8, color=[(nan, nan, nan)] * 8, alpha=[nan] * 8))

    def test_huge_values(self):
        big = 1e30
        self.stress(emitter_block(duration=0.001, burst=8), sprite_block(speed=(big, big), size=[big] * 8, life=(big, big)))

    def test_negative_and_zero_life(self):
        self.stress(emitter_block(duration=0.001, burst=8), sprite_block(life=(-5, 0)))

    def test_silly_rates(self):
        self.stress(emitter_block(rate=1e30, duration=0.0), sprite_block(life=(1, 1)))
        self.stress(emitter_block(rate=-1e30, duration=float("nan")), sprite_block(life=(1, 1)))
        self.stress(emitter_block(burst=0xffffffff, duration=1.0), sprite_block(life=(1, 1)))

    def test_mesh_zero_and_empty_mesh_draw_nothing(self):
        w = self.stress(emitter_block(duration=0.001, burst=8), sprite_block(mesh=0, life=(5, 5)), ticks=3)
        self.assertEqual(len(w.draws()), 0)
        w = self.stress(emitter_block(duration=0.001, burst=8), sprite_block(mesh=2, life=(5, 5)), ticks=3,
                        meshes=[(0, 0, 0, 0, 0.0), (0, 0, 6, 4, 0.7), (0, 0, 0, 0, 0.0)])
        self.assertEqual(len(w.draws()), 0)

    def test_child_state_size_is_bounded(self):
        # A bad block must not let the clearing loop run past one state. Reads the module's clamp in practice.
        e = emitter_block(duration=0.001, burst=2, child_state=0xffffffff)
        w = World(pair(e, sprite_block()))
        try:
            w.tick()
        except BufferFault as err:
            self.fail(f"out of bounds: {err}")

    def test_far_from_the_world_dies(self):
        w = World(pair(emitter_block(duration=0.001, burst=4), sprite_block(life=(100, 100), speed=(0, 0))), position=(20000.0, 0.0, 0.0))
        w.tick()
        w.check()
        self.assertEqual(w.counter(w.active_counter(w.chunks[1])), 0)
        self.assertEqual(len(w.draws()), 0)


class Light(unittest.TestCase):
    def light(self, w, i=0):
        b = w.buf[LIGHTS]
        base = 52 * i
        def half(x):
            return struct.unpack("<e", struct.pack("<H", x & 0xffff))[0]
        r2r = b.u32(base + 12)
        c0, c1 = b.u32(base + 16), b.u32(base + 20)
        return dict(pos=tuple(b.f32(base + 4 * k) for k in range(3)), r2=half(r2r), r=half(r2r >> 16),
                    color=(half(c0 >> 16), half(c0), half(c1 >> 16)), uid=b.u32(base + 28),
                    orient=(b.u32(base + 32), b.u32(base + 36)), flags=b.u32(base + 44))

    def test_record(self):
        w = World(light_only(light_block(radius=6.0, brightness=2.0, color=(0.5, 0.25, 1.0))), position=(1.0, 2.0, 3.0))
        w.tick()
        w.check()
        self.assertEqual(w.counter(w.draw_counters["light"]), 1)
        l = self.light(w)
        self.assertEqual(l["pos"], (1.0, 2.0, 3.0))
        self.assertAlmostEqual(l["r"], 6.0, 2)
        self.assertAlmostEqual(l["r2"], 36.0, 1)
        self.assertAlmostEqual(l["color"][0], 1.0, 2)
        self.assertAlmostEqual(l["color"][1], 0.5, 2)
        self.assertAlmostEqual(l["color"][2], 2.0, 2)
        self.assertEqual(l["orient"], (0, 0x3c00 << 16))
        sort = w.buf[LIGHT_SORT]
        self.assertEqual(sort.u32(4), 0)

    def test_lifetime_and_fade(self):
        w = World(light_only(light_block(lifetime=0.2, fade_in=0.05, fade_out=0.05, brightness=1.0, color=(1, 1, 1))))
        seen = []
        for _ in range(20):
            w.tick()
            w.check()
            n = w.counter(w.draw_counters["light"])
            seen.append(self.light(w)["color"][0] if n else None)
        self.assertLess(seen[0], seen[3], "fades in")
        self.assertTrue(seen[-1] is None, "gone after its lifetime")
        self.assertEqual(w.counter(w.active_counter(w.chunks[0])), 0)

    def test_stop_fades_out(self):
        w = World(light_only(light_block(fade_out=0.1, brightness=1.0, color=(1, 1, 1))))
        for _ in range(3):
            w.tick()
        w.set_instance(flags=0)
        vals = []
        for _ in range(12):
            w.tick()
            n = w.counter(w.draw_counters["light"])
            vals.append(self.light(w)["color"][0] if n else 0.0)
        self.assertEqual(vals[-1], 0.0)
        self.assertLess(vals[3], 1.0)

    def test_capacity(self):
        w = World(light_only(light_block()), light_cap=0)
        w.tick()
        w.check()
        self.assertEqual(w.counter(w.draw_counters["light"]), 0)

    def test_nan_and_zero(self):
        nan = float("nan")
        for b in (light_block(radius=nan), light_block(brightness=nan), light_block(radius=0.0), light_block(color=(0, 0, 0)),
                  light_block(radius=1e30)):
            w = World(light_only(b))
            w.tick()
            w.check()
        self.assertEqual(w.counter(w.draw_counters["light"]), 1, "1e30 is clamped, and still one light")


GAME = Path(os.environ.get("KILN_GAME", str(Path.home() / ".local/share/Steam/steamapps/common/Enshrouded")))


@unittest.skipUnless((GAME / "enshrouded.kfc").exists(), "needs the installed client's files")
class GameTest(unittest.TestCase):
    """Validates the harness on the game's own modules, and the header's numbers on the game's reflection."""

    @classmethod
    def setUpClass(cls):
        os.environ.setdefault("KILN_OUT", str(Path(tempfile.gettempdir()) / "kiln-vfx-test"))
        from kfc.archive import ContentHash, Kfc
        from kfc.blob import Decoder
        from kfc.refl import TypeDb
        cls.kfc = Kfc(GAME)
        cls.types = TypeDb.from_exe(GAME / "enshrouded.exe")
        cls.dec = Decoder(cls.types)
        cls.ContentHash = ContentHash

    def vfx(self, guid):
        th = self.types.get("keen::VfxResource")["hash"]
        k = [x for x in self.kfc.resources_of_type(th) if x.guid.startswith(guid)][0]
        return self.dec.decode(th, self.kfc.read(k))

    def code(self, ref):
        h = ref["hash"]
        return self.kfc.read_content(self.ContentHash(h["size"], h["hash0"], h["hash1"], h["hash2"]))

    def entries(self, v, picks):
        g = v["gpuResourceData"]
        raw = bytearray(bytes.fromhex(g["bytes"]) if isinstance(g, dict) else bytes(g))
        for x in v["meshes"]:
            struct.pack_into("<I", raw, x["patchPoint"]["resourceDataRange"]["start"], 1)
        for x in v["images"]:
            struct.pack_into("<II", raw, x["patchPoint"]["resourceDataRange"]["start"], 7, 2)
        out = []
        index = {old: new for new, old in enumerate(picks)}
        for i in picks:
            p = v["particles"][i]
            e = Entry(self.code(p["strippedCode"]), depth=p["depth"],
                      parent=index.get(p["parent"], 0xffffffff), child=index.get(p["child"], 0),
                      max_count=p["maxCount"], state_size=p["stateSizeInBytes"])
            e.res_start = p["evaluateParametersGpuResourceDataRange"]["start"]
            out.append(e)
        return out, bytes(raw)

    def test_vanilla_emitter_and_sprite_keep_the_invariants(self):
        v = self.vfx("54347d4a")
        es, raw = self.entries(v, [2, 3])
        w = World(es, resource=raw)
        peak = 0
        for _ in range(240):
            w.tick()
            w.check()
            peak = max(peak, w.counter(w.active_counter(w.chunks[1])))
        self.assertGreater(peak, 0, "the game's emitter spawned through this harness")
        # The game's particles draw through the same list code path.
        self.assertGreater(len(w.draws()), 0)
        for d in w.draws():
            self.assertEqual(d["instanceCount"], 1)
            self.assertEqual(d["meshIndex"], 1)
            self.assertEqual(d["indexCount"], 6)

    def test_vanilla_light_reads_as_the_record_kiln_writes(self):
        v = self.vfx("5bc940fc")
        es, raw = self.entries(v, [0])
        w = World(es, resource=raw, position=(1.0, 2.0, 3.0))
        w.tick()
        w.check()
        self.assertEqual(w.counter(w.draw_counters["light"]), 1)
        l = Light.light(None, w)
        self.assertEqual(l["pos"], (1.0, 2.0, 3.0))
        self.assertAlmostEqual(l["r"], 5.0, 2)              # its data: radius 5, intensity 20, white
        self.assertAlmostEqual(l["r2"], 25.0, 1)
        self.assertAlmostEqual(l["color"][0], 20.0, 0)
        self.assertEqual(l["orient"], (0, 0x3c00 << 16))

    def test_vanilla_prepare_matches_the_harness_formula(self):
        th = self.types.get("keen::GpcResource")["hash"]
        k = [x for x in self.kfc.resources_of_type(th) if x.guid.startswith("ad606c3f")][0]
        g = self.dec.decode(th, self.kfc.read(k))
        blob = self.code({"hash": g["strippedCode"]["hash"]})
        pipe = [p for p in g["computePipelines"] if p["name"] == "VfxPrepare"][0]
        r = pipe["computeShader"]["variants"][0]["strippedCode"]["range"]
        prepare = Sim(blob[r["start"]:r["start"] + r["count"]])
        v = self.vfx("54347d4a")
        es, raw = self.entries(v, [2, 3])
        a = World(es, resource=raw, seed=5)
        es2, raw2 = self.entries(v, [2, 3])
        b = World(es2, resource=raw2, seed=5, prepare=prepare)
        for _ in range(60):
            a.tick(shuffle=False)
            b.tick(shuffle=False)
            self.assertEqual(a.counter(a.active_counter(a.chunks[1])), b.counter(b.active_counter(b.chunks[1])))
            self.assertEqual(a.buf[(1, 13)].u32(12), b.buf[(1, 13)].u32(12))

    def test_kiln_modules_run_with_the_games_prepare(self):
        th = self.types.get("keen::GpcResource")["hash"]
        k = [x for x in self.kfc.resources_of_type(th) if x.guid.startswith("ad606c3f")][0]
        g = self.dec.decode(th, self.kfc.read(k))
        blob = self.code({"hash": g["strippedCode"]["hash"]})
        pipe = [p for p in g["computePipelines"] if p["name"] == "VfxPrepare"][0]
        r = pipe["computeShader"]["variants"][0]["strippedCode"]["range"]
        prepare = Sim(blob[r["start"]:r["start"] + r["count"]])
        w = World(pair(emitter_block(rate=120.0, duration=0.5, burst=8), sprite_block(life=(0.2, 0.4))), prepare=prepare)
        for _ in range(80):
            w.tick()
            w.check()
        self.assertEqual(w.counter(w.active_counter(w.chunks[1])), 0)

    def test_constants_match_reflection(self):
        fields = lambda name: {f["name"]: f["offset"] for f in self.types.all_fields(self.types.get(name))}
        src = (HERE.parent / "kiln_vfx.hlsl").read_text()
        import re
        d = {m.group(1): int(m.group(2), 0) for m in re.finditer(r"#define (K[UCMBI]_\w+)\s+(0x[0-9a-f]+|\d+)", src)}
        up = fields("keen::VfxUpdateBuffer")
        expect = dict(KU_TIME_STEP="timeStep", KU_SPLIT_DRAW_PIPELINES="splitTransparentDrawPipelines", KU_EMITTER_STATE_COUNT="emitterStateCount",
                      KU_TRANSPARENT_CAP="transparentInstanceBufferCapacity", KU_TRANSPARENT_UNLIT_CAP="transparentInstanceUnlitUnshadedBufferCapacity",
                      KU_TRANSPARENT_LIT_CAP="transparentInstanceLitUnshadedBufferCapacity", KU_TRANSPARENT_SHADED_CAP="transparentInstanceLitShadedBufferCapacity",
                      KU_TRANSPARENT_COUNT="transparentInstanceCounterByteOffset", KU_TRANSPARENT_UNLIT_COUNT="transparentInstanceUnlitUnshadedCounterByteOffset",
                      KU_TRANSPARENT_LIT_COUNT="transparentInstanceLitUnshadedCounterByteOffset", KU_TRANSPARENT_SHADED_COUNT="transparentInstanceLitShadedCounterByteOffset",
                      KU_POINT_LIGHT_CAP="pointLightBufferCapacity", KU_POINT_LIGHT_COUNT="pointLightCounterByteOffset",
                      KU_EXPOSURE_CORRECTION="exposureCorrection", KU_CAMERA_POSITION="cameraPosition", KU_CAMERA_WORLD_POSITION="cameraWorldPosition",
                      KU_CAMERA_ORIENTATION="cameraOrientation", KU_FRUSTUM_PLANES="frustumPlanes")
        for k, f in expect.items():
            self.assertEqual(d[k], up[f], k)
        ch = fields("keen::VfxChunkUpdateBuffer")
        for k, f in dict(KC_EVALUATE_PARAMETERS="evaluateParametersResourceDataOffset", KC_ACTIVE_COUNTER="activeParticleCounterByteOffset",
                         KC_NEXT_ACTIVE_COUNTER="nextActiveParticleCounterByteOffset", KC_FREE_COUNTER="freeParticleCounterByteOffset",
                         KC_PARTICLE_STATES="particleStatesOffset", KC_OLD_ACTIVE="oldActiveParticleIndicesOffset", KC_NEW_ACTIVE="newActiveParticleIndicesOffset",
                         KC_FREE_INDICES="freeParticleIndicesOffset", KC_CHILD_STATES="childParticleStatesOffset", KC_CHILD_FREE_LIST="childParticleFreeListOffset",
                         KC_CHILD_ACTIVE="childActiveParticleIndicesOffset").items():
            self.assertEqual(d[k], ch[f], k)
        em = fields("keen::VfxEmitterState")
        for k, f in dict(KM_PARTICLE_COUNTER="particleCounterByteOffset", KM_SPAWN_COUNTER="spawnCounterByteOffset", KM_MAX_SPAWN="maxSpawnCount",
                         KM_PARTICLE_CHUNK="particleChunkIndex", KM_INSTANCE="instanceIndex").items():
            self.assertEqual(d[k], em[f], k)
        base = fields("keen::VfxBaseParticleState")
        for k, f in dict(KB_EMITTER_INDEX="emitterIndex", KB_PARENT_EMITTER="myParentEmitterIndex", KB_INSTANCE="instanceIndex", KB_RNG="rngState", KB_TICK="tickCount").items():
            self.assertEqual(d[k], base[f], k)
        inst = fields("keen::VfxBaseEffectParameters")
        for k, f in dict(KI_POSITION="position", KI_FLAGS="flags", KI_ORIENTATION="orientation", KI_SCALE="scale").items():
            self.assertEqual(d[k], inst[f], k)
        self.assertEqual(self.types.get("keen::TransparentInstanceData")["size"], 176)
        self.assertEqual(self.types.get("keen::PointLightData")["size"], 52)
        self.assertEqual(self.types.get("keen::VfxChunkUpdateBuffer")["size"], d["KC_STRIDE"])
        self.assertEqual(self.types.get("keen::VfxEmitterState")["size"], d["KM_STRIDE"])
        self.assertEqual(self.types.get("keen::VfxCombinedEffectParameters")["size"], d["KI_STRIDE"])
        ti = fields("keen::TransparentInstanceData")
        self.assertEqual((ti["indexCount"], ti["flags"], ti["encodedFlipbookSize"]), (0, 39 * 4, 43 * 4), "the word indexes in kiln_sprite.hlsl")
        self.assertEqual((ti["rotation"], ti["scale"], ti["color"], ti["alpha"]), (15 * 4, 19 * 4, 22 * 4, 25 * 4))


if __name__ == "__main__":
    unittest.main()
