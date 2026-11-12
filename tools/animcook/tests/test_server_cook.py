# SPDX-License-Identifier: MIT
"""animcook.py recipe against a dedicated server's files (a made-up one: tools/modelcook/tests/fakegame.py): its own steps cook as on a
client, and the model steps it hands to modelcook are skipped there with a line each, so the cook succeeds.

    python3 -m unittest discover -s tools/animcook/tests -p test_server_cook.py -v
"""
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(ROOT.parent / "modelcook" / "tests"))

import fakegame as F  # noqa: E402

SKIPPED = "skipped on a dedicated server: only a game client draws it"


class AnimcookOnAServer(unittest.TestCase):
    def _cook(self, side):
        d = tempfile.TemporaryDirectory()
        self.addCleanup(d.cleanup)
        root = Path(d.name)
        F.build(root / side, side, kiln_out=root / "out").close()
        rec = json.loads(json.dumps(F.RECIPE))
        rec["tool"] = "animcook"  # the recipe is animcook's; its model steps are modelcook's
        for st in rec["steps"]:
            st.setdefault("tool", "modelcook")
        r = F.write_mod(root / "mod", rec)
        env = dict(os.environ, KILN_OUT=str(root / "out"), TMPDIR=d.name)
        p = subprocess.run([sys.executable, str(ROOT / "animcook.py"), "--game", str(root / side), "recipe", str(r), "--update-manifest"],
                           capture_output=True, text=True, env=env, timeout=300)
        return p, root / "mod" / "assets"

    def test_server(self):
        p, assets = self._cook("server")
        self.assertEqual(p.returncode, 0, p.stdout + p.stderr)
        self.assertIn(f"step 0 model a: {SKIPPED}", p.stdout)
        self.assertIn(f"step 1 model b: {SKIPPED}", p.stdout)
        self.assertIn("clip idle:", p.stdout)
        for f in ("cooked/clip_idle.bin", "cooked/golem.skeleton.bin", "cooked/v.bin"):
            self.assertTrue((assets / f).exists(), f)
        self.assertFalse((assets / "cooked/a.bin").exists())
        man = json.loads((assets / "assets.json").read_text())
        self.assertIn("cooked/clip_idle.bin", {e["file"] for e in man["entries"]})
        self.assertFalse(any(e.get("side") for e in man["entries"]))  # the clip and skeleton: both sides, unmarked

    def test_client_unchanged(self):
        p, assets = self._cook("client")
        self.assertEqual(p.returncode, 0, p.stdout + p.stderr)
        self.assertNotIn("skipped", p.stdout)
        for f in ("cooked/a.bin", "cooked/b.blob", "cooked/clip_idle.bin", "cooked/v.bin"):
            self.assertTrue((assets / f).exists(), f)


if __name__ == "__main__":
    unittest.main()
