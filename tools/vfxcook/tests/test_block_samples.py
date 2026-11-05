# SPDX-License-Identifier: MIT
"""The sample's VFX blocks through the builder, as a client's boot takes them, and on a real GPU when asked.

    python3 -m unittest tools/vfxcook/tests/test_block_samples.py -v

    KILN_TEST_GPU=NVIDIA ...        also runs the cooked data in native/services/vfx/shaders/test/test_gpu.py --stage cooked
                                    (build the runner first: native/services/vfx/shaders/gpu/build.sh)
    KILN_TEST_DEFINITIONS=<dir> ... also builds what C# blocks serialise to: <dir> is blocks-test's data folder, vfx-definitions/ in it

No game files are needed. What this can't show: whether the game accepts the data, or how it looks.
"""
import json
import os
import subprocess
import sys
import tempfile
import unittest
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from test_vfxcook import Cooked, DEC, NS, REPO, SHADERS, checks, run  # noqa: E402

SAMPLE = REPO / "mods" / "samples" / "FrostboltBlock"
MOD = "frostbolt-block"
NAMES = ["frost_burst", "frost_charge", "frost_trail", "frost_aura"]


def strip_comments(text):
    return "\n".join(line for line in text.splitlines() if not line.lstrip().startswith("//"))


def sample_definitions():
    return json.loads(strip_comments((SAMPLE / "blocks" / "frostlance.json").read_text()))["vfx"]


def cook(root, mod_id, vfx, files=SAMPLE / "vfx"):
    """The given blocks in a mod folder of their own, so that a name means the same thing on both sides of a comparison."""
    folder = Path(root) / "mod"
    (folder / "blocks").mkdir(parents=True)
    (folder / "kiln.mod.json").write_text(json.dumps({"id": mod_id, "version": "1.0.0", "side": "both", "clients": "mod"}))
    (folder / "blocks" / "b.json").write_text(json.dumps({"format": 1, "vfx": vfx}))
    (folder / "vfx").symlink_to(files)
    out = Path(root) / "out"
    run("cook", "--mod", folder, "--out", out)
    return Cooked(out)


class Sample(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.out = Path(cls.tmp.name) / "out"
        # the mod's folder as it stands: spells and statuses beside the "vfx" section are not the builder's business
        cls.manifest = Path(cls.tmp.name) / "mod"
        (cls.manifest / "blocks").mkdir(parents=True)
        (cls.manifest / "kiln.mod.json").write_text(json.dumps({"id": MOD, "version": "0.1.0", "side": "both", "clients": "mod"}))
        (cls.manifest / "blocks" / "frostlance.json").write_text((SAMPLE / "blocks" / "frostlance.json").read_text())
        (cls.manifest / "vfx").symlink_to(SAMPLE / "vfx")
        run("cook", "--mod", cls.manifest, "--out", cls.out)
        cls.cooked = Cooked(cls.out)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_every_block_builds_under_the_rule_for_ids(self):
        blocks = self.cooked.blocks()
        self.assertEqual(self.cooked.report["errors"], [])
        for name in NAMES:
            b = blocks[f"{name}-json"]
            self.assertEqual(b["guid"], str(uuid.uuid5(NS, f"{MOD}:vfx:{name}-json")))

    def test_the_data_passes_the_structural_checks(self):
        for name in NAMES:
            guid = self.cooked.blocks()[f"{name}-json"]["guid"]
            v = self.cooked.decoded(guid, "keen::VfxResource")
            self.assertEqual(checks.check_vfx(v, strict=True), [], name)

    def test_what_kiln_blocks_takes_for_a_length_is_what_the_builder_says(self):
        blocks = self.cooked.blocks()
        # VfxTiming in Kiln.Blocks: delay + duration (0.001 for a burst alone) + ceil(burst / 64) / 60 + the longest life
        self.assertAlmostEqual(blocks["frost_burst-json"]["stopAfterSeconds"], 0.001 + 1 / 60 + 0.9, places=4)
        self.assertAlmostEqual(blocks["frost_charge-json"]["stopAfterSeconds"], 0.001 + 1 / 60 + 0.3, places=4)
        self.assertTrue(blocks["frost_trail-json"]["loops"])
        self.assertTrue(blocks["frost_aura-json"]["loops"])

    def test_a_block_with_a_light_and_a_block_without(self):
        def lights(name):
            v = self.cooked.decoded(self.cooked.blocks()[f"{name}-json"]["guid"], "keen::VfxResource")
            return len(v["particles"])
        self.assertEqual(lights("frost_burst"), 3)   # emitter, sprite, light
        self.assertEqual(lights("frost_charge"), 2)  # emitter, sprite
        self.assertEqual(lights("frost_trail"), 3)

    @unittest.skipUnless(os.environ.get("KILN_TEST_DEFINITIONS"), "needs a run of blocks-test: KILN_TEST_DEFINITIONS")
    def test_a_block_from_code_builds_byte_for_byte_as_its_data_file_does(self):
        folder = Path(os.environ["KILN_TEST_DEFINITIONS"])
        data = sample_definitions()
        for name in NAMES:
            with tempfile.TemporaryDirectory() as a, tempfile.TemporaryDirectory() as b:
                code = cook(a, "same-mod", {name: json.loads((folder / f"{name}.json").read_text())})
                file = cook(b, "same-mod", {name: data[f"{name}-json"]})
                guid = code.blocks()[name]["guid"]
                self.assertEqual(guid, file.blocks()[name]["guid"], name)
                self.assertEqual(code.resource(guid, "keen::VfxResource"), file.resource(guid, "keen::VfxResource"), name)

    @unittest.skipUnless(os.environ.get("KILN_TEST_GPU"), "needs a GPU: KILN_TEST_GPU=<device name>")
    def test_the_cooked_data_runs_on_a_real_gpu(self):
        device = os.environ["KILN_TEST_GPU"]
        out = Path(os.environ.get("KILN_OUT", self.tmp.name))
        env = dict(os.environ, KILN_OUT=str(out))
        p = subprocess.run([sys.executable, str(SHADERS / "test" / "test_gpu.py"), "--device", device, "--stage", "cooked", "--cooked", str(self.out)],
                           capture_output=True, text=True, env=env)
        self.assertEqual(p.returncode, 0, p.stdout[-2000:] + p.stderr[-2000:])
        results = json.loads((out / "".join(c if c.isalnum() else "-" for c in device.lower()) / "cooked.json").read_text())
        self.assertEqual(results["problems"], [])
        for name in NAMES:
            r = results["vfx"][f"{name}-json"]
            self.assertEqual(r["wiring_problems"], [], name)
            self.assertGreater(r["peak_particles"], 0, name)
            if name in ("frost_trail", "frost_aura"):
                self.assertTrue(r["finished_after_stop"], name)
            else:
                self.assertTrue(r["all_finished_at_end"], name)
        self.assertGreater(results["vfx"]["frost_burst-json"]["ticks_with_a_light"], 0)


if __name__ == "__main__":
    unittest.main()
