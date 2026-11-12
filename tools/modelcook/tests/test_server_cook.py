# SPDX-License-Identifier: MIT
"""cook.json against a dedicated server's files (enshrouded_server.kfc, which holds no RenderModel and no material): the model steps
are skipped with a line each, the rest is cooked as on a client, and the manifest stays the clients' with the skipped steps' entries
marked "side": "client". Made-up games (tests/fakegame.py), nothing from the real one.

    python3 -m unittest discover -s tools/modelcook/tests -p test_server_cook.py -v
"""
import json
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent))

import fakegame as F  # noqa: E402
from cook import recipe as R  # noqa: E402

SKIPPED_FILES = {"cooked/a.bin", "cooked/a.blob", "cooked/b.bin", "cooked/b.blob", "cooked/c.bin", "cooked/c.blob"}


class ServerCook(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.root = Path(cls.tmp.name)
        cls.t = F.types()
        cls.client = F.build(cls.root / "client", "client", cls.t)
        cls.server = F.build(cls.root / "server", "server", cls.t)
        cls.recipe = json.loads(json.dumps(F.RECIPE))
        # a model whose glTF materials become blocks: blocks every machine reads, so the server writes them too
        cls.recipe["steps"].append({"op": "model", "gltf": "src/sword.gltf", "materials": "gltf", "name": "c"})
        cls.mod_client = cls.root / "mod-client"
        r = F.write_mod(cls.mod_client, cls.recipe)
        cls.client_report = R.run(r, cls.client, cls.t, update_manifest=True)

    @classmethod
    def tearDownClass(cls):
        cls.client.close()
        cls.server.close()
        cls.tmp.cleanup()

    def _server_mod(self, name, with_client_manifest=True):
        """A copy of the mod as a server operator has it: the sources, and the clients' assets.json (the mod ships it)."""
        mod = self.root / name
        r = F.write_mod(mod, self.recipe)
        if with_client_manifest:
            shutil.copy(self.mod_client / "assets" / "assets.json", mod / "assets" / "assets.json")
        return r

    def test_client_cooks_every_step(self):
        self.assertTrue(self.client_report["ok"])
        self.assertFalse(any(s.get("skipped") for s in self.client_report["steps"]))
        self.assertNotIn("side", self.client_report["game"])

    def test_server_skips_models_and_cooks_the_rest(self):
        r = self._server_mod("mod-server")
        mod = r.parent.parent
        before = json.loads((mod / "assets" / "assets.json").read_text())
        rep = R.run(r, self.server, self.t, update_manifest=True)
        self.assertTrue(rep["ok"], json.dumps(rep["steps"], indent=1)[:2000])
        self.assertEqual(rep["game"]["side"], "server")
        by_step = {s["step"]: s for s in rep["steps"]}
        for i, name in ((0, "a"), (1, "b"), (5, "c")):
            self.assertEqual(by_step[i]["skipped"], R.SKIPPED)
            self.assertEqual(by_step[i]["name"], name)
        self.assertEqual(by_step[0]["guid"], F.BASE_MODEL)
        self.assertEqual(by_step[1]["guid"], F.NEW_MODEL)
        # the visual template is served on a server too: cooked, byte for byte the client's
        self.assertNotIn("skipped", by_step[2])
        self.assertEqual([o["file"] for o in rep["outputs"]], ["cooked/v.bin"])
        self.assertEqual((mod / "assets/cooked/v.bin").read_bytes(), (self.mod_client / "assets/cooked/v.bin").read_bytes())
        for f in SKIPPED_FILES:
            self.assertFalse((mod / "assets" / f).exists(), f)
        # the material blocks from the glTF: the same file on both sides
        self.assertEqual((mod / "blocks/c.materials.json").read_bytes(), (self.mod_client / "blocks/c.materials.json").read_bytes())
        # the model input was not read: a server has none
        self.assertFalse(rep["inputs"]["model"]["available"])
        self.assertTrue(rep["inputs"]["visual"]["sha256"])
        # the manifest: the clients' entries, in their order; the skipped steps' marked for kiln.assets to skip on a server
        after = json.loads((mod / "assets" / "assets.json").read_text())
        self.assertEqual([(e["kind"], e["file"]) for e in after["entries"]], [(e["kind"], e["file"]) for e in before["entries"]])
        for b, a in zip(before["entries"], after["entries"]):
            if a["file"] in SKIPPED_FILES:
                self.assertEqual(a, dict(b, side="client"))
            else:
                self.assertEqual(a, b)
        # cooked again: nothing to do, nothing changes
        self.assertFalse(R.needs_cook(r))
        R.run(r, self.server, self.t, update_manifest=True)
        self.assertEqual(json.loads((mod / "assets" / "assets.json").read_text()), after)

    def test_server_without_update_manifest_says_what_kiln_assets_will_warn_about(self):
        r = self._server_mod("mod-server-plain")
        mp = r.parent / "assets.json"
        before = mp.read_text()
        rep = R.run(r, self.server, self.t)
        self.assertTrue(rep["ok"])
        self.assertEqual(sorted(rep["manifest"]["clientOnlyUnmarked"]), sorted(SKIPPED_FILES))
        self.assertEqual(mp.read_text(), before)

    def test_server_without_a_manifest(self):
        r = self._server_mod("mod-server-new", with_client_manifest=False)
        rep = R.run(r, self.server, self.t, update_manifest=True)
        self.assertTrue(rep["ok"])
        man = json.loads((r.parent / "assets.json").read_text())
        self.assertEqual([e["file"] for e in man["entries"]], ["cooked/v.bin"])

    def test_missing_on_a_server_what_a_server_has_still_fails(self):
        """A template the server's files lack is a game update (or a wrong GUID), not a side: today's message."""
        rec = json.loads(json.dumps(self.recipe))
        rec["inputs"]["visual"]["guid"] = "77777777-0000-4000-8000-000000000007"
        r = F.write_mod(self.root / "mod-server-update", rec)
        with self.assertRaises(R.RecipeError) as e:
            R.run(r, self.server, self.t, update_manifest=True)
        self.assertIn("input visual:", str(e.exception))
        self.assertIn("(game update or wrong side?)", str(e.exception))

    def test_missing_on_a_client_a_model_still_fails(self):
        rec = json.loads(json.dumps(self.recipe))
        rec["inputs"]["model"]["guid"] = "88888888-0000-4000-8000-000000000008"
        r = F.write_mod(self.root / "mod-client-update", rec)
        with self.assertRaises(R.RecipeError) as e:
            R.run(r, self.client, self.t, update_manifest=True)
        self.assertIn("input model:", str(e.exception))
        self.assertIn("(game update or wrong side?)", str(e.exception))

    def test_side_of(self):
        self.assertEqual(R.side_of(self.server), "server")
        self.assertEqual(R.side_of(self.client), "client")


class ServerCookCommand(unittest.TestCase):
    """modelcook.py recipe against a made-up server folder, as `kiln cook --game <server folder>` runs it (with the animcook steps)."""

    def test_recipe_command(self):
        import os
        import subprocess
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            F.build(root / "server", "server", kiln_out=root / "out").close()
            r = F.write_mod(root / "mod")
            env = dict(os.environ, KILN_OUT=str(root / "out"), TMPDIR=d)
            p = subprocess.run([sys.executable, str(HERE.parent / "modelcook.py"), "--game", str(root / "server"), "recipe", str(r), "--update-manifest"],
                               capture_output=True, text=True, env=env, timeout=300)
            self.assertEqual(p.returncode, 0, p.stdout + p.stderr)
            self.assertIn(f"step 0 model a: {R.SKIPPED}", p.stdout)
            self.assertIn(f"step 1 model b: {R.SKIPPED}", p.stdout)
            self.assertIn("clip idle:", p.stdout)
            self.assertTrue((root / "mod/assets/cooked/clip_idle.bin").exists())
            self.assertTrue((root / "mod/assets/cooked/v.bin").exists())


if __name__ == "__main__":
    unittest.main()
